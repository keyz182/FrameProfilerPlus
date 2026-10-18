using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Attribution;
using FrameProfilerPlus.Core;
using HarmonyLib;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    // The real attribution hooks, installed into this process's Harmony with the counting runtime, driven by
    // every patch shape Harmony emits. Each test asserts the result is unchanged and no zone is left open.
    public class AttributionHooksTests
    {
        private static readonly object InstallGate = new object();
        private static readonly List<string> Warnings = new List<string>();
        private static LabelRegistry registry;
        private static AttributionStats stats;

        private readonly Harmony harmony = new Harmony("test.patches");

        public AttributionHooksTests()
        {
            lock (InstallGate)
            {
                if (registry == null)
                {
                    registry = TestRuntime.NewRegistry();
                    stats = AttributionHooks.Install(new Harmony("test.hooks"), typeof(TestRuntime), registry, a => a.GetName().Name, Warnings.Add);
                }
            }
            TestRuntime.Reset();
        }

        public static class Targets
        {
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int a, int b) => a + b;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Skippable() => 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Doubled(int x) => x;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Chained(int x) => x;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Finalized(int x) => x;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Swallowed() => throw new InvalidOperationException("original");
            [MethodImpl(MethodImplOptions.NoInlining)] public static int PrefixThrows() => 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int PostfixThrows(int x) => x;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int FinalizerThrows() => 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Rebuilt(int x) => x + 1;
        }

        public static class Patches
        {
            public static int Seen;

            public static void VoidPrefix() => Seen++;
            public static bool SkipPrefix(ref int __result)
            {
                __result = 5;
                return false;
            }
            public static void RefPostfix(ref int __result) => __result += 100;
            public static int Passthrough(int result) => result * 2;
            public static int PassthroughPlusOne(int result) => result + 1;
            public static void QuietFinalizer() => Seen++;
            public static Exception SwallowFinalizer(Exception __exception) => null;
            public static void ThrowingPrefix() => throw new ApplicationException("prefix");
            public static int ThrowingPassthrough(int result) => throw new ApplicationException("postfix");
            public static void ThrowingFinalizer() => throw new ApplicationException("finalizer");
        }

        private static HarmonyMethod P(string name) => new HarmonyMethod(TestRuntime.Method(typeof(Patches), name));

        private void Patch(string target, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null) =>
            harmony.Patch(TestRuntime.Method(typeof(Targets), target), prefix, postfix, null, finalizer);

        private static string[] Labels() => TestRuntime.EnteredLabels(registry).ToArray();

        [Fact]
        public void Install_ActivatesBothHooks()
        {
            Assert.True(stats.PatchCallsActive);
            Assert.True(stats.MethodWrapActive);
        }

        [Fact]
        public void VoidPrefixAndRefPostfix_TimedInsidePatchedMethodScope()
        {
            Patch(nameof(Targets.Add), prefix: P(nameof(Patches.VoidPrefix)), postfix: P(nameof(Patches.RefPostfix)));

            Assert.Equal(103, Targets.Add(1, 2));

            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(new[]
            {
                "P:Targets.Add",
                "H:FrameProfilerPlus.Tests Patches.VoidPrefix [prefix]",
                "H:FrameProfilerPlus.Tests Patches.RefPostfix [postfix]",
            }, Labels());
            Assert.Equal(0, stats.MissedMethodWraps);
        }

        [Fact]
        public void SkippingPrefix_KeepsItsResult()
        {
            Patch(nameof(Targets.Skippable), prefix: P(nameof(Patches.SkipPrefix)));

            Assert.Equal(5, Targets.Skippable());
            Assert.Equal(0, TestRuntime.Depth);
        }

        [Fact]
        public void PassthroughPostfixes_ChainThroughSpilledValues()
        {
            Patch(nameof(Targets.Chained), postfix: P(nameof(Patches.Passthrough)));
            Patch(nameof(Targets.Chained), postfix: P(nameof(Patches.PassthroughPlusOne)));

            Assert.Equal(7, Targets.Chained(3));
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(3, TestRuntime.EnteredSlots.Count);
        }

        [Fact]
        public void PassthroughPostfixWithFinalizer_KeepsResult()
        {
            Patch(nameof(Targets.Finalized), postfix: P(nameof(Patches.Passthrough)), finalizer: P(nameof(Patches.QuietFinalizer)));

            Assert.Equal(8, Targets.Finalized(4));
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Contains("H:FrameProfilerPlus.Tests Patches.QuietFinalizer [finalizer]", Labels());
        }

        [Fact]
        public void CatchPathFinalizer_SwallowsAndCloses()
        {
            Patch(nameof(Targets.Swallowed), finalizer: P(nameof(Patches.SwallowFinalizer)));

            Assert.Equal(0, Targets.Swallowed());
            Assert.Equal(0, TestRuntime.Depth);
        }

        [Fact]
        public void ThrowingPrefix_ClosesEveryZone()
        {
            Patch(nameof(Targets.PrefixThrows), prefix: P(nameof(Patches.ThrowingPrefix)));

            Assert.Throws<ApplicationException>(() => Targets.PrefixThrows());
            Assert.Equal(0, TestRuntime.Depth);
        }

        [Fact]
        public void ThrowingPassthroughPostfix_ClosesEveryZone()
        {
            Patch(nameof(Targets.PostfixThrows), postfix: P(nameof(Patches.ThrowingPassthrough)));

            Assert.Throws<ApplicationException>(() => Targets.PostfixThrows(1));
            Assert.Equal(0, TestRuntime.Depth);
        }

        [Fact]
        public void ThrowingNormalPathFinalizer_RepairedByPatchedMethodScope()
        {
            Patch(nameof(Targets.FinalizerThrows), finalizer: P(nameof(Patches.ThrowingFinalizer)));

            Assert.Throws<ApplicationException>(() => Targets.FinalizerThrows());
            Assert.Equal(0, TestRuntime.Depth);
        }

        [Fact]
        public void InvalidTimingIL_RebuildsWithoutTimingAndWarns()
        {
            var retries = stats.Retries;
            AttributionHooks.ForceInvalidMethodWrap = true;
            try
            {
                Patch(nameof(Targets.Rebuilt), prefix: P(nameof(Patches.VoidPrefix)));
            }
            finally
            {
                AttributionHooks.ForceInvalidMethodWrap = false;
            }

            Assert.Equal(3, Targets.Rebuilt(2));
            Assert.Equal(retries + 1, stats.Retries);
            Assert.Contains(Warnings, w => w.Contains("Rebuilt") && w.Contains("not timed"));
            Assert.Empty(TestRuntime.EnteredSlots);
        }
    }
}
