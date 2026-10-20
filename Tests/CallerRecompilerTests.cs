using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Coverage;
using HarmonyLib;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class CallerRecompilerTests
    {
        public static class Chain
        {
            public static int Sink;

            public static int Leaf(int x) => Sink += x;
            public static int LeafB(int x) => Sink -= x;

            // Small enough to inline, so the walk continues past it.
            public static int Mid(int x) => Leaf(x) + 1;

            // Big (well over 100 bytes of IL): recompiled, but not expanded.
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static int Top(int x)
            {
                var a = Mid(x);
                a += x * 3; a ^= a << 1; a += x * 5; a ^= a >> 2; a += x * 7; a ^= a << 3; a += x * 11; a ^= a >> 1;
                a += x * 13; a ^= a << 2; a += x * 17; a ^= a >> 3; a += x * 19; a ^= a << 1; a += x * 23; a ^= a >> 2;
                a += x * 29; a ^= a << 3; a += x * 31; a ^= a >> 1; a += x * 37; a ^= a << 2; a += x * 41; a ^= a >> 3;
                return a + LeafB(x);
            }

            [MethodImpl(MethodImplOptions.NoInlining)] public static int Entry(int x) => Top(x);
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Sibling(int x) => Mid(x);
        }

        private static MethodBase M(string name) => typeof(Chain).GetMethod(name);

        private static readonly Assembly[] Here = { typeof(CallerRecompilerTests).Assembly };

        private static CallerRecompiler New(Func<MethodBase, bool> leaveAlone = null) =>
            new CallerRecompiler(() => Here, leaveAlone ?? (_ => false), null);

        private static int OurTranspilers(MethodBase m) =>
            Harmony.GetPatchInfo(m)?.Transpilers.Count(t => t.owner == CallerRecompiler.HarmonyId) ?? 0;

        [Fact]
        public void BodySizes_MatchTheFixtureAssumptions()
        {
            Assert.True(ILScan.BodySize(M("Mid")) <= CallerRecompiler.InlineCandidateBytes);
            Assert.True(ILScan.BodySize(M("Top")) > CallerRecompiler.InlineCandidateBytes);
        }

        [Fact]
        public void Plan_WalksUpThroughSmallCallers_AndStopsAfterALargeOne()
        {
            var plan = New().Plan(M("Leaf"));

            Assert.Contains(M("Mid"), plan);
            Assert.Contains(M("Top"), plan);
            Assert.Contains(M("Sibling"), plan);
            Assert.DoesNotContain(M("Entry"), plan);
            Assert.DoesNotContain(M("Leaf"), plan);
        }

        [Fact]
        public void Plan_RespectsTheCap()
        {
            var recompiler = New();
            recompiler.MaxCallers = 1;

            Assert.Single(recompiler.Plan(M("Leaf")));
        }

        [Fact]
        public void Plan_RespectsTheDepthLimit()
        {
            var recompiler = New();
            recompiler.MaxDepth = 1;

            Assert.Equal(new[] { M("Mid") }, recompiler.Plan(M("Leaf")));
        }

        [Fact]
        public void Plan_LeavesAloneWhatThePolicySays()
        {
            var plan = New(m => m.Name == "Top").Plan(M("Leaf"));

            Assert.DoesNotContain(M("Top"), plan);
            Assert.Contains(M("Mid"), plan);
        }

        [Fact]
        public void Recompile_PatchesCallers_KeepsBehaviour_AndReleaseRemovesOurTranspiler()
        {
            var recompiler = New();
            Chain.Sink = 0;
            var before = Chain.Entry(2);
            Chain.Sink = 0;

            var count = recompiler.Recompile(M("LeafB"));

            Assert.Equal(1, count);
            Assert.Equal(1, OurTranspilers(M("Top")));
            Assert.Equal(before, Chain.Entry(2));

            recompiler.Release(M("LeafB"));
            Assert.Equal(0, OurTranspilers(M("Top")));
            Assert.Equal(0, recompiler.PatchedCallerCount);
        }

        [Fact]
        public void SharedCaller_IsKeptUntilEveryTargetIsReleased()
        {
            var recompiler = New();
            recompiler.Recompile(M("Leaf"));
            recompiler.Recompile(M("LeafB"));

            Assert.Equal(1, OurTranspilers(M("Top")));

            recompiler.Release(M("Leaf"));
            Assert.Equal(1, OurTranspilers(M("Top")));

            recompiler.Release(M("LeafB"));
            Assert.Equal(0, OurTranspilers(M("Top")));
            recompiler.ReleaseAll();
        }

        [Fact]
        public void Recompile_TwiceForTheSameTarget_DoesNotDoubleCount()
        {
            var recompiler = New();
            var first = recompiler.Recompile(M("LeafB"));

            Assert.Equal(first, recompiler.Recompile(M("LeafB")));
            recompiler.Release(M("LeafB"));
            Assert.Equal(0, recompiler.PatchedCallerCount);
        }
    }
}
