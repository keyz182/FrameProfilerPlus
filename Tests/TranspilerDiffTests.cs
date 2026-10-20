using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FrameProfilerPlus.Attribution;
using HarmonyLib;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class TranspilerDiffTests
    {
        public class Subject
        {
            public Subject()
            {
            }

            public static void Existing()
            {
            }

            public static void Added()
            {
            }

            public static void Other()
            {
            }
        }

        private static readonly MethodInfo Existing = typeof(Subject).GetMethod("Existing");
        private static readonly MethodInfo AddedCall = typeof(Subject).GetMethod("Added");
        private static readonly MethodInfo Other = typeof(Subject).GetMethod("Other");

        private static CodeInstruction Call(MethodInfo method) => new CodeInstruction(OpCodes.Call, method);

        private static List<CodeInstruction> Added(List<CodeInstruction> input, System.Action<List<CodeInstruction>> edit)
        {
            var snapshot = TranspilerDiff.Snapshot(input);
            var output = new List<CodeInstruction>(input);
            edit(output);
            return TranspilerDiff.Added(snapshot, output);
        }

        [Fact]
        public void NewCallInstruction_IsAdded()
        {
            var added = Call(AddedCall);
            var input = new List<CodeInstruction> { Call(Existing), new CodeInstruction(OpCodes.Ret) };

            var result = Added(input, output => output.Insert(1, added));

            Assert.Same(added, Assert.Single(result));
        }

        [Fact]
        public void NewConstructorCall_IsAdded()
        {
            var made = new CodeInstruction(OpCodes.Newobj, typeof(Subject).GetConstructor(System.Type.EmptyTypes));
            var input = new List<CodeInstruction> { new CodeInstruction(OpCodes.Ret) };

            var result = Added(input, output => output.Insert(0, made));

            Assert.Same(made, Assert.Single(result));
        }

        [Fact]
        public void ClonedInstructions_AreNotAttributed()
        {
            var input = new List<CodeInstruction> { Call(Existing), Call(Existing), new CodeInstruction(OpCodes.Ret) };

            var result = Added(input, output =>
            {
                for (var i = 0; i < output.Count; i++)
                    output[i] = new CodeInstruction(output[i]);
            });

            Assert.Empty(result);
        }

        [Fact]
        public void InPlaceRetarget_IsAdded()
        {
            var retargeted = Call(Existing);
            var input = new List<CodeInstruction> { retargeted, new CodeInstruction(OpCodes.Ret) };

            var result = Added(input, output => output[0].operand = Other);

            Assert.Same(retargeted, Assert.Single(result));
        }

        [Fact]
        public void InPlaceChangeFromANonCall_IsAdded()
        {
            var nop = new CodeInstruction(OpCodes.Nop);
            var input = new List<CodeInstruction> { nop, new CodeInstruction(OpCodes.Ret) };

            var result = Added(input, output =>
            {
                output[0].opcode = OpCodes.Call;
                output[0].operand = AddedCall;
            });

            Assert.Same(nop, Assert.Single(result));
        }

        [Fact]
        public void UnchangedCalls_ReorderedOrRemoved_AreNotAttributed()
        {
            var input = new List<CodeInstruction> { Call(Existing), Call(Other), new CodeInstruction(OpCodes.Ret) };

            Assert.Empty(Added(input, output => output.Reverse()));
            Assert.Empty(Added(input, output => output.RemoveAt(0)));
            Assert.Empty(Added(input, output =>
            {
                var first = output[0];
                output[0] = output[1];
                output[1] = first;
            }));
        }

        [Fact]
        public void ClonesAfterARemoval_AreNotAttributed()
        {
            var input = new List<CodeInstruction> { Call(Existing), Call(Existing), new CodeInstruction(OpCodes.Ret) };

            var result = Added(input, output =>
            {
                output.RemoveAt(0);
                output[0] = new CodeInstruction(output[0]);
            });

            Assert.Empty(result);
        }

        [Fact]
        public void AddedNonCallInstructions_AreIgnored()
        {
            var input = new List<CodeInstruction> { Call(Existing) };

            var result = Added(input, output => output.Insert(0, new CodeInstruction(OpCodes.Ldc_I4_1)));

            Assert.Empty(result);
        }

        [Fact]
        public void ClonesPlusOneExtraCallToTheSameMethod_AttributesExactlyOne()
        {
            var input = new List<CodeInstruction> { Call(Existing), new CodeInstruction(OpCodes.Ret) };

            var result = Added(input, output =>
            {
                output[0] = new CodeInstruction(output[0]);
                output.Insert(1, Call(Existing));
            });

            Assert.Single(result);
        }

        [Fact]
        public void Snapshot_IsolatesTheInputFromLaterMutation()
        {
            var call = Call(Existing);
            var snapshot = TranspilerDiff.Snapshot(new[] { call });
            call.operand = Other;

            Assert.Same(Existing, snapshot.Single().Operand);
            Assert.Same(call, snapshot.Single().Instruction);
        }

        [Fact]
        public void TwoStepsInARow_EachSeeOnlyItsOwnAdditions()
        {
            var first = Call(AddedCall);
            var second = Call(Other);
            var input = new List<CodeInstruction> { Call(Existing), new CodeInstruction(OpCodes.Ret) };
            var step1Snapshot = TranspilerDiff.Snapshot(input);
            var afterStep1 = new List<CodeInstruction>(input);
            afterStep1.Insert(1, first);
            var step2Snapshot = TranspilerDiff.Snapshot(afterStep1);
            var afterStep2 = new List<CodeInstruction>(afterStep1);
            afterStep2.Insert(2, second);

            Assert.Same(first, Assert.Single(TranspilerDiff.Added(step1Snapshot, afterStep1)));
            Assert.Same(second, Assert.Single(TranspilerDiff.Added(step2Snapshot, afterStep2)));
        }
    }
}
