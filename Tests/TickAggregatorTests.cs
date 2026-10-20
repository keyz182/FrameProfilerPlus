using System.Collections.Generic;
using System.Linq;
using FrameProfilerPlus.Ticks;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    // One timestamp tick is one millisecond here (ticksPerSecond = 1000), so tick counts read as ms.
    public class TickAggregatorTests
    {
        private const double TicksPerSecond = 1000;

        private static List<FrameTicks> Frames(params (int frame, int ticks)[] frames) =>
            frames.Select(f => new FrameTicks(f.frame, f.ticks)).ToList();

        private static void Call(TickAggregator a, TickProbe probe, int frame, int key, long start, long end, long id = 0)
        {
            Assert.True(a.Enter(probe, frame));
            a.Exit(probe, key, start, end, id);
        }

        [Fact]
        public void SelfTime_ExcludesNestedCalls()
        {
            var a = new TickAggregator(8);
            var outer = a.Intern(TickProbe.Thing, "Outer");
            var inner = a.Intern(TickProbe.Thing, "Inner");

            a.Enter(TickProbe.Thing, 1);
            a.Enter(TickProbe.Thing, 1);
            a.Exit(TickProbe.Thing, inner, 10, 30, 0);
            a.Exit(TickProbe.Thing, outer, 0, 50, 0);

            var stats = a.Summarise(TickProbe.Thing, Frames((1, 1)), TicksPerSecond).ToDictionary(s => s.Name);
            Assert.Equal(30, stats["Outer"].MeanMsPerTick);
            Assert.Equal(20, stats["Inner"].MeanMsPerTick);
        }

        [Fact]
        public void SelfTime_OfRecursiveKey_SumsToTheOuterTime()
        {
            var a = new TickAggregator(8);
            var key = a.Intern(TickProbe.Stat, "Recursive");

            a.Enter(TickProbe.Stat, 1);
            a.Enter(TickProbe.Stat, 1);
            a.Exit(TickProbe.Stat, key, 20, 40, 0);
            a.Exit(TickProbe.Stat, key, 0, 60, 0);

            var stat = Assert.Single(a.Summarise(TickProbe.Stat, Frames((1, 1)), TicksPerSecond));
            Assert.Equal(60, stat.MeanMsPerTick);
            Assert.Equal(2, stat.CallsPerTick);
        }

        [Fact]
        public void Probes_KeepSeparateStacks_SoNestingAcrossProbesSubtractsNothing()
        {
            var a = new TickAggregator(8);
            var thing = a.Intern(TickProbe.Thing, "Pawn");
            var driver = a.Intern(TickProbe.Driver, "JobDriver_Mine");

            a.Enter(TickProbe.Thing, 1);
            a.Enter(TickProbe.Driver, 1);
            a.Exit(TickProbe.Driver, driver, 10, 30, 0);
            a.Exit(TickProbe.Thing, thing, 0, 50, 0);

            Assert.Equal(50, a.Summarise(TickProbe.Thing, Frames((1, 1)), TicksPerSecond).Single().MeanMsPerTick);
            Assert.Equal(20, a.Summarise(TickProbe.Driver, Frames((1, 1)), TicksPerSecond).Single().MeanMsPerTick);
        }

        [Fact]
        public void Exit_WithNothingEntered_IsCountedAndRecordsNothing()
        {
            var a = new TickAggregator(8);
            var key = a.Intern(TickProbe.Thing, "X");
            a.Enter(TickProbe.Thing, 1);
            a.Exit(TickProbe.Thing, key, 0, 5, 0);

            a.Exit(TickProbe.Thing, key, 0, 5, 0);

            Assert.Equal(1, a.Unbalanced);
            Assert.Equal(1, a.Summarise(TickProbe.Thing, Frames((1, 1)), TicksPerSecond).Single().CallsPerTick);
        }

        [Fact]
        public void NewFrame_DropsEntriesLeftOpenAndCountsThem()
        {
            var a = new TickAggregator(8);
            var key = a.Intern(TickProbe.Thing, "X");
            a.Enter(TickProbe.Thing, 1);

            Call(a, TickProbe.Thing, 2, key, 0, 7);

            Assert.Equal(1, a.Unbalanced);
            Assert.Equal(7, a.Summarise(TickProbe.Thing, Frames((2, 1)), TicksPerSecond).Single().MeanMsPerTick);
        }

        [Fact]
        public void Enter_PastTheDepthLimit_RefusesAndCounts()
        {
            var a = new TickAggregator(8);
            for (var i = 0; i < TickAggregator.MaxDepth; i++)
                Assert.True(a.Enter(TickProbe.Thing, 1));

            Assert.False(a.Enter(TickProbe.Thing, 1));
            Assert.Equal(1, a.DepthOverflows);
        }

        [Fact]
        public void Intern_ReturnsTheSameKeyForTheSameName_AndFoldsOverflowIntoOther()
        {
            var a = new TickAggregator(8, maxKeys: 2);
            var first = a.Intern(TickProbe.Comp, "A");
            var second = a.Intern(TickProbe.Comp, "B");

            var other = a.Intern(TickProbe.Comp, "C");

            Assert.Equal(first, a.Intern(TickProbe.Comp, "A"));
            Assert.NotEqual(first, second);
            Assert.Equal("<other>", a.NameOf(other));
            Assert.Equal(other, a.Intern(TickProbe.Comp, "D"));
            Assert.NotEqual(other, a.Intern(TickProbe.Thing, "C"));
        }

        [Fact]
        public void TopIndividuals_KeepsTheWorstCallsOfTheFrame_InDescendingOrder()
        {
            var a = new TickAggregator(8, topK: 2);
            var key = a.Intern(TickProbe.Thing, "Pawn");
            Call(a, TickProbe.Thing, 1, key, 0, 5, id: 101);
            Call(a, TickProbe.Thing, 1, key, 0, 9, id: 102);
            Call(a, TickProbe.Thing, 1, key, 0, 7, id: 103);
            Call(a, TickProbe.Thing, 1, key, 0, 3, id: 104);

            var top = a.TopIndividuals(1, TickProbe.Thing);

            Assert.Equal(new long[] { 102, 103 }, top.Select(t => t.Id));
            Assert.Equal(new long[] { 9, 7 }, top.Select(t => t.SelfTicks));
            Assert.Empty(a.TopIndividuals(1, TickProbe.Driver));
        }

        [Fact]
        public void Ring_ForgetsTheOldestFrameWhenFull()
        {
            var a = new TickAggregator(3);
            var key = a.Intern(TickProbe.Thing, "X");
            for (var frame = 1; frame <= 5; frame++)
                Call(a, TickProbe.Thing, frame, key, 0, frame);

            Assert.False(a.HasFrame(1));
            Assert.False(a.HasFrame(2));
            Assert.True(a.HasFrame(3));
            Assert.True(a.HasFrame(5));
            Assert.Empty(a.Summarise(TickProbe.Thing, Frames((1, 1)), TicksPerSecond));
        }

        [Fact]
        public void Summarise_WeightsByTicks_ReportsWorstFrame_AndZeroFillsFramesWithoutTheKey()
        {
            var a = new TickAggregator(8);
            var x = a.Intern(TickProbe.Thing, "X");
            var y = a.Intern(TickProbe.Thing, "Y");
            Call(a, TickProbe.Thing, 1, x, 0, 10);
            Call(a, TickProbe.Thing, 2, x, 0, 60);
            Call(a, TickProbe.Thing, 2, x, 0, 60);
            Call(a, TickProbe.Thing, 3, y, 0, 4);

            var stats = a.Summarise(TickProbe.Thing, Frames((1, 1), (2, 3), (3, 1)), TicksPerSecond).ToDictionary(s => s.Name);

            var sx = stats["X"];
            Assert.Equal(130d / 5, sx.MeanMsPerTick);
            Assert.Equal(40, sx.WorstMsPerTick);
            Assert.Equal(2, sx.WorstFrame);
            Assert.Equal(40, sx.P95MsPerTick);
            Assert.Equal(3d / 5, sx.CallsPerTick);
            Assert.Equal(3, sx.Frames);
            Assert.Equal(4, stats["Y"].WorstMsPerTick);
            Assert.Equal(3, stats["Y"].WorstFrame);
        }

        [Fact]
        public void Summarise_SkipsFramesWithoutTicksOrData_AndSortsByMean()
        {
            var a = new TickAggregator(8);
            var small = a.Intern(TickProbe.Thing, "Small");
            var big = a.Intern(TickProbe.Thing, "Big");
            Call(a, TickProbe.Thing, 1, small, 0, 2);
            Call(a, TickProbe.Thing, 1, big, 0, 8);
            Call(a, TickProbe.Thing, 2, big, 0, 1000);

            var stats = a.Summarise(TickProbe.Thing, Frames((1, 1), (2, 0), (9, 1)), TicksPerSecond);

            Assert.Equal(new[] { "Big", "Small" }, stats.Select(s => s.Name));
            Assert.Equal(8, stats[0].MeanMsPerTick);
            Assert.Equal(1, stats[0].Frames);
        }

        [Theory]
        [InlineData(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.95, 10)]
        [InlineData(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.5, 5)]
        [InlineData(new double[] { 7 }, 0.95, 7)]
        [InlineData(new double[] { 0, 0, 0, 9 }, 0.95, 9)]
        public void Percentile_UsesNearestRank(double[] sorted, double p, double expected) =>
            Assert.Equal(expected, TickAggregator.Percentile(sorted, p));
    }
}
