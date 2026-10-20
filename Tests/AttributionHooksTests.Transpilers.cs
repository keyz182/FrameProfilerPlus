using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Attribution;
using FrameProfilerPlus.Core;
using HarmonyLib;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    // Calls that foreign transpilers add, retarget or clone, run through the real hooks.
    public partial class AttributionHooksTests
    {
        public static class TCalls
        {
            public static int Count;

            [MethodImpl(MethodImplOptions.NoInlining)] public static void Slow() => Count++;
            [MethodImpl(MethodImplOptions.NoInlining)] public static void Original() => Count++;
            [MethodImpl(MethodImplOptions.NoInlining)] public static void Replacement() => Count += 10;
            [MethodImpl(MethodImplOptions.NoInlining)] public static void Other() => Count += 100;
            [MethodImpl(MethodImplOptions.NoInlining)] public static void Throws() => throw new InvalidOperationException("added call");
        }

        public static class TTargets
        {
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Added(int x) => x + 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Retargeted(int x)
            {
                TCalls.Original();
                return x + 1;
            }
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Cloned(int x)
            {
                TCalls.Original();
                return x + 1;
            }
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Two(int x) => x + 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Seen(int x) => x + 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Removed(int x) => x + 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Thrower(int x) => x + 1;
        }

        public static class TTranspilers
        {
            public static List<CodeInstruction> SeenByLater = new List<CodeInstruction>();

            private static IEnumerable<CodeInstruction> Prepend(IEnumerable<CodeInstruction> codes, string callee)
            {
                yield return new CodeInstruction(OpCodes.Call, typeof(TCalls).GetMethod(callee));
                foreach (var code in codes)
                    yield return code;
            }

            public static IEnumerable<CodeInstruction> AddsSlow(IEnumerable<CodeInstruction> codes) => Prepend(codes, nameof(TCalls.Slow));

            public static IEnumerable<CodeInstruction> AddsOther(IEnumerable<CodeInstruction> codes) => Prepend(codes, nameof(TCalls.Other));

            public static IEnumerable<CodeInstruction> AddsThrows(IEnumerable<CodeInstruction> codes) => Prepend(codes, nameof(TCalls.Throws));

            public static IEnumerable<CodeInstruction> RetargetsOriginal(IEnumerable<CodeInstruction> codes)
            {
                foreach (var code in codes)
                {
                    if (code.Calls(typeof(TCalls).GetMethod(nameof(TCalls.Original))))
                        code.operand = typeof(TCalls).GetMethod(nameof(TCalls.Replacement));
                    yield return code;
                }
            }

            public static IEnumerable<CodeInstruction> Clones(IEnumerable<CodeInstruction> codes) => codes.Select(c => new CodeInstruction(c)).ToList();

            public static IEnumerable<CodeInstruction> RemovesOther(IEnumerable<CodeInstruction> codes) =>
                codes.Where(c => !c.Calls(typeof(TCalls).GetMethod(nameof(TCalls.Other)))).ToList();

            public static IEnumerable<CodeInstruction> Records(IEnumerable<CodeInstruction> codes)
            {
                var list = codes.ToList();
                SeenByLater = list.Select(c => new CodeInstruction(c)).ToList();
                return list;
            }
        }

        private static HarmonyMethod T(string name, int priority = -1)
        {
            var method = new HarmonyMethod(TestRuntime.Method(typeof(TTranspilers), name));
            if (priority >= 0)
                method.priority = priority;
            return method;
        }

        private void PatchT(string target, params HarmonyMethod[] transpilers)
        {
            var method = TestRuntime.Method(typeof(TTargets), target);
            foreach (var transpiler in transpilers)
                harmony.Patch(method, transpiler: transpiler);
        }

        private const string Own = "T:FrameProfilerPlus.Tests TTranspilers.";

        [Fact]
        public void TranspilerAddedCall_IsTimedUnderThePatchedMethodScope()
        {
            var before = stats.TranspilerCallsWrapped;
            PatchT(nameof(TTargets.Added), T(nameof(TTranspilers.AddsSlow)));
            TCalls.Count = 0;

            Assert.Equal(2, TTargets.Added(1));

            Assert.Equal(1, TCalls.Count);
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(new[] { "P:TTargets.Added", Own + "AddsSlow: TCalls.Slow" }, Labels());
            Assert.Equal(before + 1, stats.TranspilerCallsWrapped);
        }

        [Fact]
        public void TranspilerRetargetedCall_IsTimed()
        {
            PatchT(nameof(TTargets.Retargeted), T(nameof(TTranspilers.RetargetsOriginal)));
            TCalls.Count = 0;

            Assert.Equal(2, TTargets.Retargeted(1));

            Assert.Equal(10, TCalls.Count);
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(new[] { "P:TTargets.Retargeted", Own + "RetargetsOriginal: TCalls.Replacement" }, Labels());
        }

        [Fact]
        public void TranspilerThatOnlyClones_IsNotBlamedForTheOriginalCalls()
        {
            PatchT(nameof(TTargets.Cloned), T(nameof(TTranspilers.Clones)));

            Assert.Equal(2, TTargets.Cloned(1));

            Assert.Equal(new[] { "P:TTargets.Cloned" }, Labels());
        }

        [Fact]
        public void TwoTranspilers_AreEachCreditedTheirOwnCall()
        {
            PatchT(nameof(TTargets.Two), T(nameof(TTranspilers.AddsSlow), 600), T(nameof(TTranspilers.AddsOther), 100));
            TCalls.Count = 0;

            Assert.Equal(2, TTargets.Two(1));

            Assert.Equal(101, TCalls.Count);
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(new[] { Own + "AddsOther: TCalls.Other", Own + "AddsSlow: TCalls.Slow", "P:TTargets.Two" }.OrderBy(x => x), Labels().OrderBy(x => x));
        }

        [Fact]
        public void LaterTranspiler_SeesNoScopeCodeFromUs()
        {
            PatchT(nameof(TTargets.Seen), T(nameof(TTranspilers.AddsSlow), 600), T(nameof(TTranspilers.Records), 100));

            Assert.Equal(2, TTargets.Seen(1));

            Assert.Contains(TTranspilers.SeenByLater, c => c.Calls(typeof(TCalls).GetMethod(nameof(TCalls.Slow))));
            Assert.DoesNotContain(TTranspilers.SeenByLater, c => c.operand is MethodInfo m && m.DeclaringType == typeof(TestRuntime));
        }

        [Fact]
        public void CallRemovedByALaterTranspiler_IsNotWrapped()
        {
            PatchT(nameof(TTargets.Removed), T(nameof(TTranspilers.AddsOther), 600), T(nameof(TTranspilers.RemovesOther), 100));
            TCalls.Count = 0;

            Assert.Equal(2, TTargets.Removed(1));

            Assert.Equal(0, TCalls.Count);
            Assert.Equal(new[] { "P:TTargets.Removed" }, Labels());
        }

        [Fact]
        public void AddedCallThatThrows_LeavesNoZoneOpen()
        {
            PatchT(nameof(TTargets.Thrower), T(nameof(TTranspilers.AddsThrows)));

            Assert.Throws<InvalidOperationException>(() => TTargets.Thrower(1));

            Assert.Equal(0, TestRuntime.Depth);
        }

        [Fact]
        public void Insert_KeepsAPrefixWithItsCall_AndSkipsTailCalls()
        {
            var emitter = new ScopeEmitter(typeof(TestRuntime));
            var il = new DynamicMethod("insert", typeof(void), Type.EmptyTypes).GetILGenerator();
            var callee = typeof(TCalls).GetMethod(nameof(TCalls.Slow));
            var prefixed = new CodeInstruction(OpCodes.Call, callee);
            var tail = new CodeInstruction(OpCodes.Call, callee);
            var codes = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Volatile),
                prefixed,
                new CodeInstruction(OpCodes.Tailcall),
                tail,
                new CodeInstruction(OpCodes.Ret),
            };
            var recorded = new[]
            {
                new RecordedCall { Instruction = prefixed, Callee = callee, Transpiler = callee },
                new RecordedCall { Instruction = tail, Callee = callee, Transpiler = callee },
            };

            var result = TranspilerAttribution.Insert(codes, recorded, il, emitter, _ => 7, out var wrapped);

            Assert.Equal(1, wrapped);
            var opcodes = result.Select(c => c.opcode).ToList();
            var volatileAt = opcodes.IndexOf(OpCodes.Volatile);
            Assert.Equal(OpCodes.Stloc, opcodes[volatileAt - 1]);
            Assert.Same(prefixed, result[volatileAt + 1]);
            Assert.Equal(OpCodes.Ldloc, opcodes[volatileAt + 2]);
            Assert.Equal(OpCodes.Tailcall, opcodes[opcodes.Count - 3]);
            Assert.Same(tail, result[opcodes.Count - 2]);
        }

        [Fact]
        public void Insert_DoesNotWrapACallWhoseCalleeChangedAfterRecording()
        {
            var emitter = new ScopeEmitter(typeof(TestRuntime));
            var il = new DynamicMethod("insert2", typeof(void), Type.EmptyTypes).GetILGenerator();
            var recordedCallee = typeof(TCalls).GetMethod(nameof(TCalls.Slow));
            var call = new CodeInstruction(OpCodes.Call, typeof(TCalls).GetMethod(nameof(TCalls.Other)));
            var recorded = new[] { new RecordedCall { Instruction = call, Callee = recordedCallee, Transpiler = recordedCallee } };

            var result = TranspilerAttribution.Insert(new List<CodeInstruction> { call, new CodeInstruction(OpCodes.Ret) }, recorded, il, emitter, _ => 7, out var wrapped);

            Assert.Equal(0, wrapped);
            Assert.Equal(2, result.Count);
        }
    }
}
