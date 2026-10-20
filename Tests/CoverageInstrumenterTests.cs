using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Core;
using FrameProfilerPlus.Coverage;
using HarmonyLib;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class CoverageInstrumenterTests
    {
        public static class Targets
        {
            [MethodImpl(MethodImplOptions.NoInlining)] public static int A(int x) => x + 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int B(int x) => x * 2;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int C(int x) => x - 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static void Throws() => throw new InvalidOperationException("boom");
        }

        private static readonly LabelRegistry Registry = TestRuntime.NewRegistry();
        private long frames = 42;
        private readonly CoverageInstrumenter instrumenter;

        public CoverageInstrumenterTests()
        {
            TestRuntime.Reset();
            instrumenter = new CoverageInstrumenter(Registry, new ScopeEmitter(typeof(TestRuntime)), a => "Tests", () => frames, null);
        }

        private static MethodBase M(string name) => typeof(Targets).GetMethod(name);

        private static string[] Entered() => TestRuntime.EnteredLabels(Registry).ToArray();

        [Fact]
        public void Apply_TimesCoveredMethods_AndKeepsResultsAndBalance()
        {
            var result = instrumenter.Apply(new[] { M("A"), M("B") });

            Assert.Equal(2, result.Added);
            Assert.Equal(2, Targets.A(1));
            Assert.Equal(6, Targets.B(3));
            Assert.Equal(0, TestRuntime.Depth);
            Assert.Equal(new[] { "X:FrameProfilerPlus.Tests.CoverageInstrumenterTests+Targets.A", "X:FrameProfilerPlus.Tests.CoverageInstrumenterTests+Targets.B" }, Entered());
        }

        [Fact]
        public void Apply_ASecondTime_OnlyAppliesTheDifference()
        {
            instrumenter.Apply(new[] { M("A") });
            var result = instrumenter.Apply(new[] { M("A"), M("C") });

            Assert.Equal(1, result.Added);
            Assert.Equal(0, result.Removed);
            Assert.Equal(2, instrumenter.Covered.Count);
        }

        [Fact]
        public void Apply_RemovedMethod_StopsBeingTimed_ButStaysDetouredUntilRestart()
        {
            instrumenter.Apply(new[] { M("A"), M("B") });
            var result = instrumenter.Apply(new[] { M("B") });
            TestRuntime.Reset();

            Assert.Equal(1, result.Removed);
            Assert.Equal(2, Targets.A(1));
            Assert.Empty(Entered());
            Assert.Contains(M("A"), instrumenter.DetouredUntilRestart);
            Assert.Contains(M("A"), Harmony.GetAllPatchedMethods());
        }

        [Fact]
        public void Clear_RemovesEveryTimingScope_WithoutChangingBehaviour()
        {
            instrumenter.Apply(new[] { M("A"), M("B"), M("C") });
            instrumenter.Clear();
            TestRuntime.Reset();

            Assert.Equal((2, 6, 2), (Targets.A(1), Targets.B(3), Targets.C(3)));
            Assert.Empty(Entered());
            Assert.Empty(instrumenter.Covered);
            Assert.Equal(3, instrumenter.DetouredUntilRestart.Count);
            var info = Harmony.GetPatchInfo(M("A"));
            Assert.True(info == null || info.Transpilers.All(t => t.owner != CoverageInstrumenter.HarmonyId));
        }

        [Fact]
        public void Apply_BadMethod_IsRecordedAndDoesNotStopTheBatch()
        {
            var unpatchable = typeof(IDisposable).GetMethod("Dispose");

            var result = instrumenter.Apply(new[] { unpatchable, M("A") });

            Assert.Single(result.Failed);
            Assert.Equal(unpatchable, result.Failed[0].method);
            Assert.Contains(unpatchable, instrumenter.Failed.Keys);
            Assert.Equal(1, result.Added);
            Assert.True(instrumenter.IsCovered(M("A")));
        }

        [Fact]
        public void TryGetSlot_ReportsSlotAndApplyEpoch()
        {
            instrumenter.Apply(new[] { M("A") });
            frames = 99;

            Assert.True(instrumenter.TryGetSlot(M("A"), out var slot, out var appliedAt));
            Assert.Equal(42, appliedAt);
            Assert.Equal("X:FrameProfilerPlus.Tests.CoverageInstrumenterTests+Targets.A", Registry.Get(slot).LabelText);
            Assert.False(instrumenter.TryGetSlot(M("B"), out _, out _));
        }

        [Fact]
        public void ThrowingCoveredMethod_StillClosesItsZone()
        {
            instrumenter.Apply(new[] { M("Throws") });

            Assert.Throws<InvalidOperationException>(() => Targets.Throws());
            Assert.Equal(0, TestRuntime.Depth);
        }
    }
}
