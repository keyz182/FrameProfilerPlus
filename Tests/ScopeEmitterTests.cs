using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Core;
using HarmonyLib;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    // WrapBody through a real Harmony transpiler: the result must be valid IL that runs, returns the same
    // values and closes its zone on every exit, including exceptions.
    public class ScopeEmitterTests
    {
        private static readonly ScopeEmitter Emitter = new ScopeEmitter(typeof(TestRuntime));
        private static readonly Harmony Harmony = new Harmony("test.scopeemitter");

        public class Base
        {
            public int baseValue;

            public Base(int value) => baseValue = value;
        }

        public class Derived : Base
        {
            public int field = 7;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public Derived(int value) : base(value * 2)
            {
                field += value;
            }
        }

        public static class Targets
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int Branches(int x)
            {
                if (x < 0)
                    return -1;
                if (x == 0)
                    return 0;
                return x * 10;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Throws(bool shouldThrow)
            {
                if (shouldThrow)
                    throw new InvalidOperationException("boom");
            }

            public static IEnumerable<int> Numbers(int count)
            {
                try
                {
                    for (var i = 0; i < count; i++)
                        yield return i;
                }
                finally
                {
                    TestRuntime.EnteredSlots.Add(-1);
                }
            }
        }

        private static IEnumerable<CodeInstruction> WrapTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            var returnType = (original as MethodInfo)?.ReturnType ?? typeof(void);
            return Emitter.WrapBody(instructions, returnType, 0, repair: true, generator);
        }

        private static void Wrap(MethodBase method) =>
            Harmony.Patch(method, transpiler: new HarmonyMethod(TestRuntime.Method(typeof(ScopeEmitterTests), nameof(WrapTranspiler))));

        public ScopeEmitterTests() => TestRuntime.Reset();

        [Fact]
        public void WrapBody_MultipleReturns_KeepsResultsAndCloses()
        {
            Wrap(TestRuntime.Method(typeof(Targets), nameof(Targets.Branches)));

            Assert.Equal(-1, Targets.Branches(-5));
            Assert.Equal(0, Targets.Branches(0));
            Assert.Equal(30, Targets.Branches(3));
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(3, TestRuntime.EnteredSlots.Count);
        }

        [Fact]
        public void WrapBody_Throwing_ClosesZoneAndRethrows()
        {
            Wrap(TestRuntime.Method(typeof(Targets), nameof(Targets.Throws)));

            Assert.Throws<InvalidOperationException>(() => Targets.Throws(true));
            Assert.Equal(0, TestRuntime.Depth);
            Targets.Throws(false);
            Assert.Equal(0, TestRuntime.Depth);
        }

        [Fact]
        public void WrapBody_ConstructorWithBaseCall_RunsFieldInitialisersAndBase()
        {
            Wrap(typeof(Derived).GetConstructor(new[] { typeof(int) }));

            var instance = new Derived(3);

            Assert.Equal(6, instance.baseValue);
            Assert.Equal(10, instance.field);
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Single(TestRuntime.EnteredSlots);
        }

        [Fact]
        public void WrapBody_IteratorMoveNext_KeepsIterationAndInnerFinally()
        {
            var iterator = typeof(Targets).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<Numbers>"));
            Wrap(iterator.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));

            var values = Targets.Numbers(3).ToList();

            Assert.Equal(new[] { 0, 1, 2 }, values);
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(4, TestRuntime.EnteredSlots.Count(s => s == 0));
            Assert.Contains(-1, TestRuntime.EnteredSlots);
        }
    }
}
