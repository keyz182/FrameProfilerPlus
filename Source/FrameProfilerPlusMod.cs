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

            var harmony = new Harmony(HarmonyId);
            // Parameterless PatchAll() finds no calling assembly on the 1.7 runtime and patches nothing.
            harmony.PatchAll(typeof(FrameProfilerPlusMod).Assembly);

            Log.Message($"[FrameProfilerPlus] Loaded. Harmony attribution: {Settings.harmonyAttribution}, " +
                        $"coverage: {Settings.extendedCoverage}, tick grouping: {Settings.tickGrouping}");
        }

        public override string SettingsCategory() => "Frame Profiler Plus";

        public override void DoSettingsWindowContents(Rect inRect) => Settings.Draw(inRect);
    }
}
