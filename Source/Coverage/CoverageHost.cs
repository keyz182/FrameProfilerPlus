using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameProfilerPlus.Core;
using Verse;

namespace FrameProfilerPlus.Coverage
{
    // Owns the one CoverageService of a running game. Null unless "Extended method coverage" was on at startup.
    public static class CoverageHost
    {
        public static CoverageService Service { get; private set; }

        public static void Initialise()
        {
            var instrumenter = new CoverageInstrumenter(ScopeRuntime.Registry, ScopeRuntime.Emitter, ModResolver.ModName, GameCoverage.Epoch,
                FrameProfilerPlusMod.Warn);
            var recompiler = new CallerRecompiler(() => AppDomain.CurrentDomain.GetAssemblies(), _ => false, FrameProfilerPlusMod.Warn);
            Service = new CoverageService(instrumenter, recompiler, () => AppDomain.CurrentDomain.GetAssemblies(),
                GameCoverage.VanillaTimed, CoverageService.HasForeignPatch);
        }

        // Bumped when target lines change outside the Coverage tab's editor, so the editor reloads them.
        public static int TargetsVersion { get; private set; }

        // The target line a method row expands to: its assembly, type and source name (lambdas map to their owner).
        public static string LineFor(MethodBase method) =>
            $"{method.DeclaringType?.Assembly.GetName().Name}!{GeneratedNames.SourceTypeName(method)}::{GeneratedNames.SourceMethodName(method)}";

        // Appends lines that are not already saved; returns how many were new.
        public static int AddTargets(IEnumerable<string> lines)
        {
            var settings = FrameProfilerPlusMod.Settings;
            var added = lines.Where(l => !settings.coverageTargets.Contains(l)).Distinct().ToList();
            if (added.Count == 0)
                return 0;
            settings.coverageTargets.AddRange(added);
            settings.Write();
            TargetsVersion++;
            return added.Count;
        }

        public static void ApplySaved()
        {
            var settings = FrameProfilerPlusMod.Settings;
            var report = Service.Apply(settings.coverageTargets, settings.maxCoverageMethods);
            Log.Message($"[FrameProfilerPlus] Coverage at startup: {Describe(report)}");
        }

        public static string Describe(CoverageService.Report report)
        {
            if (report.ParseErrors.Count > 0)
                return string.Join("; ", report.ParseErrors);
            if (report.Refused)
                return $"{report.Selection.Matched} methods matched, over the limit; nothing changed";
            var skipped = string.Join(", ", report.Selection.Skipped.Select(s => $"{s.Value} {s.Key}"));
            return $"{report.Result?.Added ?? 0} added, {report.Result?.Removed ?? 0} removed, {report.Result?.Failed.Count ?? 0} failed, " +
                   $"{report.RecompiledCallers} callers recompiled" + (skipped.Length > 0 ? $"; skipped: {skipped}" : "");
        }
    }

    // After every mod has loaded, so assemblies and Harmony patches are final.
    [StaticConstructorOnStartup]
    public static class CoverageStartup
    {
        static CoverageStartup()
        {
            var settings = FrameProfilerPlusMod.Settings;
            if (CoverageHost.Service != null && settings.applyCoverageAtStartup && settings.coverageTargets.Count > 0)
                CoverageHost.ApplySaved();
        }
    }
}
