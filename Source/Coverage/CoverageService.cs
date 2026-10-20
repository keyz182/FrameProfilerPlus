using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FrameProfilerPlus.Coverage
{
    // Ties target lines to selection, the instrumenter and the caller recompiler. The one place that decides
    // what is timed. Pure over its injected functions, so tests need no game.
    public sealed class CoverageService
    {
        public sealed class Report
        {
            public Selection Selection { get; internal set; }
            public CoverageInstrumenter.ApplyResult Result { get; internal set; }
            public int RecompiledCallers { get; internal set; }
            public List<string> ParseErrors { get; } = new List<string>();
            // Nothing was changed because the selection was over the cap.
            public bool Refused => Selection.OverCap;
        }

        private readonly CoverageInstrumenter instrumenter;
        private readonly CallerRecompiler recompiler;
        private readonly Func<IEnumerable<Assembly>> assemblies;
        private readonly Func<IReadOnlyCollection<MethodBase>> vanillaTimed;
        private readonly Func<MethodBase, bool> hasForeignPatch;

        public CoverageService(CoverageInstrumenter instrumenter, CallerRecompiler recompiler, Func<IEnumerable<Assembly>> assemblies,
            Func<IReadOnlyCollection<MethodBase>> vanillaTimed, Func<MethodBase, bool> hasForeignPatch)
        {
            this.instrumenter = instrumenter;
            this.recompiler = recompiler;
            this.assemblies = assemblies;
            this.vanillaTimed = vanillaTimed;
            this.hasForeignPatch = hasForeignPatch;
        }

        public CoverageInstrumenter Instrumenter => instrumenter;

        public CallerRecompiler Recompiler => recompiler;

        public Report Last { get; private set; }

        // True when another mod's Harmony patch (not ours) is on the method.
        public static bool HasForeignPatch(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null)
                return false;
            return info.Owners.Any(o => !o.StartsWith(FrameProfilerPlusMod.HarmonyId, StringComparison.Ordinal));
        }

        // What the lines would select, nothing patched. Methods already covered stay in the selection, so
        // applying the same lines again changes nothing.
        public Report Preview(IEnumerable<string> lines, int maxMethods)
        {
            var report = new Report();
            var targets = CoverageTarget.ParseAll(lines, report.ParseErrors);
            var timed = new HashSet<MethodBase>(vanillaTimed());
            var policy = new SkipPolicy
            {
                MaxMethods = maxMethods,
                TimedByVanilla = m => timed.Contains(m) && !instrumenter.IsCovered(m),
                HasForeignPatch = m => hasForeignPatch(m) && !instrumenter.IsCovered(m),
            };
            report.Selection = MethodSelector.Select(assemblies(), targets, policy);
            return report;
        }

        public Report Apply(IEnumerable<string> lines, int maxMethods)
        {
            var report = Preview(lines, maxMethods);
            Last = report;
            if (report.Refused)
                return report;

            var before = new HashSet<MethodBase>(instrumenter.Covered);
            report.Result = instrumenter.Apply(report.Selection.Methods);
            var after = new HashSet<MethodBase>(instrumenter.Covered);

            foreach (var removed in before.Where(m => !after.Contains(m)))
                recompiler.Release(removed);
            var timed = new HashSet<MethodBase>(vanillaTimed());
            recompiler.LeaveAlone = m => hasForeignPatch(m) || timed.Contains(m);
            foreach (var added in after.Where(m => !before.Contains(m)))
                report.RecompiledCallers += recompiler.Recompile(added);
            return report;
        }

        // Stops timing. Methods stay detoured until restart (see CoverageInstrumenter).
        public void Clear()
        {
            instrumenter.Clear();
            recompiler.ReleaseAll();
            Last = null;
        }

        // Manual action for a method that was patched but never seen: re-patch its callers again.
        public int RecompileCallers(MethodBase target)
        {
            var timed = new HashSet<MethodBase>(vanillaTimed());
            recompiler.LeaveAlone = m => hasForeignPatch(m) || timed.Contains(m);
            recompiler.Release(target);
            return recompiler.Recompile(target);
        }

        // Detoured by us and also in vanilla's instrumented set: vanilla's row for it now reads zero.
        public List<MethodBase> DetouredAndVanillaTimed()
        {
            var timed = new HashSet<MethodBase>(vanillaTimed());
            return instrumenter.DetouredUntilRestart.Where(timed.Contains).ToList();
        }
    }
}
