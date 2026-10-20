using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Core;
using FrameProfilerPlus.Coverage;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class CoverageServiceTests
    {
        // One method per test: Harmony patches last for the whole process.
        public static class Svc
        {
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Apply1(int x) => x + 1;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Apply2(int x) => x + 2;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Diff1(int x) => x + 3;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Diff2(int x) => x + 4;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Vanilla1(int x) => x + 5;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Foreign1(int x) => x + 6;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Clear1(int x) => x + 7;
            [MethodImpl(MethodImplOptions.NoInlining)] public static int Called1(int x) => x + 8;
        }

        public static class Callers
        {
            [MethodImpl(MethodImplOptions.NoInlining)] public static int CallsCalled1(int x) => Svc.Called1(x) * 2;
        }

        private const string Assembly = "FrameProfilerPlus.Tests!";
        private const string SvcType = "FrameProfilerPlus.Tests.CoverageServiceTests+Svc";

        private static readonly LabelRegistry Registry = TestRuntime.NewRegistry();

        private IReadOnlyCollection<MethodBase> vanilla = new List<MethodBase>();
        private HashSet<MethodBase> foreign = new HashSet<MethodBase>();

        private CoverageService NewService()
        {
            TestRuntime.Reset();
            var assemblies = new[] { typeof(Svc).Assembly };
            var instrumenter = new CoverageInstrumenter(Registry, new ScopeEmitter(typeof(TestRuntime)), a => "Tests", () => 1, null);
            var recompiler = new CallerRecompiler(() => assemblies, _ => false, null);
            return new CoverageService(instrumenter, recompiler, () => assemblies, () => vanilla, foreign.Contains);
        }

        private static MethodBase M(string name) => typeof(Svc).GetMethod(name);

        private static string Line(string method) => $"{Assembly}{SvcType}::{method}";

        [Fact]
        public void Apply_TimesTheMatchedMethods_AndASecondApplyChangesNothing()
        {
            var service = NewService();
            var lines = new[] { Line("Apply1"), Line("Apply2") };

            var first = service.Apply(lines, 100);
            var second = service.Apply(lines, 100);

            Assert.Equal(2, first.Result.Added);
            Assert.Equal((0, 0), (second.Result.Added, second.Result.Removed));
            Assert.Equal(2, service.Instrumenter.Covered.Count);
            Assert.Equal(2, Svc.Apply1(1));
        }

        [Fact]
        public void Apply_OverTheCap_ChangesNothing()
        {
            var service = NewService();

            var report = service.Apply(new[] { Line("Diff1"), Line("Diff2") }, 1);

            Assert.True(report.Refused);
            Assert.Empty(service.Instrumenter.Covered);
        }

        [Fact]
        public void Apply_WithFewerLines_RemovesTheDifference()
        {
            var service = NewService();
            service.Apply(new[] { Line("Diff1"), Line("Diff2") }, 100);

            var report = service.Apply(new[] { Line("Diff1") }, 100);

            Assert.Equal(1, report.Result.Removed);
            Assert.True(service.Instrumenter.IsCovered(M("Diff1")));
            Assert.False(service.Instrumenter.IsCovered(M("Diff2")));
        }

        [Fact]
        public void Preview_SkipsMethodsVanillaTimesAndMethodsOtherModsPatch_ButKeepsCoveredOnes()
        {
            var service = NewService();
            vanilla = new[] { M("Vanilla1") };
            foreign.Add(M("Foreign1"));

            var report = service.Preview(new[] { Line("Vanilla1"), Line("Foreign1") }, 100);

            Assert.Empty(report.Selection.Methods);
            Assert.Equal(1, report.Selection.Skipped[SkipReason.TimedByVanilla]);
            Assert.Equal(1, report.Selection.Skipped[SkipReason.ForeignPatch]);
            Assert.Empty(service.Instrumenter.Covered);
        }

        [Fact]
        public void Clear_StopsTimingAndReleasesRecompiledCallers()
        {
            var service = NewService();
            service.Apply(new[] { Line("Clear1") }, 100);

            service.Clear();

            Assert.Empty(service.Instrumenter.Covered);
            Assert.Equal(0, service.Recompiler.PatchedCallerCount);
            TestRuntime.Reset();
            Assert.Equal(8, Svc.Clear1(1));
            Assert.Empty(TestRuntime.EnteredSlots);
        }

        [Fact]
        public void Apply_RecompilesTheCallersOfEachAddedMethod()
        {
            var service = NewService();

            var report = service.Apply(new[] { Line("Called1") }, 100);

            Assert.True(report.RecompiledCallers >= 1);
            Assert.Contains(typeof(Callers).GetMethod("CallsCalled1"), service.Recompiler.CallersRecompiledFor(M("Called1")));
            Assert.Equal(18, Callers.CallsCalled1(1));
        }

        [Fact]
        public void ParseErrors_AreReportedAndNothingIsSelectedFromTheBadLine()
        {
            var service = NewService();

            var report = service.Preview(new[] { $"{Assembly}{SvcType}::.cctor" }, 100);

            Assert.Single(report.ParseErrors);
            Assert.Empty(report.Selection.Methods);
        }
    }
}
