using FrameProfilerPlus.Attribution;
using FrameProfilerPlus.Core;
using FrameProfilerPlus.Coverage;
using FrameProfilerPlus.Ticks;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FrameProfilerPlus
{
    public class FrameProfilerPlusMod : Mod
    {
        public const string HarmonyId = "keyz.frameprofilerplus";

        public static FrameProfilerPlusSettings Settings { get; private set; }

        public FrameProfilerPlusMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FrameProfilerPlusSettings>();
            ScopeRuntime.Initialise(() => Settings.labelCeiling);
            if (Settings.extendedCoverage)
                CoverageHost.Initialise();

            var harmony = new Harmony(HarmonyId);
            // Install before any other mod patches (About.xml loads us right after Harmony): only patches
            // built after this point are timed. Read once; a change takes effect at the next start.
            if (Settings.harmonyAttribution)
                AttributionHooks.Install(harmony, typeof(ScopeRuntime), ScopeRuntime.Registry, ModResolver.ModName, Warn, Settings.transpilerAttribution);
            if (Settings.tickGrouping)
                TickProbes.Install(harmony, Settings.tickDrivers, Settings.tickComps);
            // Parameterless PatchAll() finds no calling assembly on the 1.7 runtime and patches nothing.
            harmony.PatchAll(typeof(FrameProfilerPlusMod).Assembly);

            Log.Message($"[FrameProfilerPlus] Loaded. Harmony attribution: {Settings.harmonyAttribution}, " +
                        $"coverage: {Settings.extendedCoverage}, tick grouping: {Settings.tickGrouping}");
        }

        public static void Warn(string message) => Log.Warning("[FrameProfilerPlus] " + message);

        public override string SettingsCategory() => "Frame Profiler Plus";

        public override void DoSettingsWindowContents(Rect inRect) => Settings.Draw(inRect);
    }

    // Runs after every mod has patched: reports what attribution covered, and flags a hook that was bypassed.
    [StaticConstructorOnStartup]
    public static class AttributionSummary
    {
        static AttributionSummary()
        {
            if (!FrameProfilerPlusMod.Settings.harmonyAttribution)
                return;
            var stats = AttributionHooks.Stats;
            Log.Message($"[FrameProfilerPlus] Harmony attribution: {stats}. Label count known: {ScopeRuntime.LabelCountKnown}.");
            if (stats.MissedMethodWraps > 0)
                FrameProfilerPlusMod.Warn($"{stats.MissedMethodWraps} patched methods were built without passing through the P: hook; their whole-method time is missing.");
        }
    }
}
