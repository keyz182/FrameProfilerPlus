using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.ProfilerUI;

namespace FrameProfilerPlus.UI
{
    // Explains a row's label prefix in a tooltip over the hovered row, in every profiler tab.
    [HarmonyPatch(typeof(ProfilerRowListView), "DrawRow")]
    public static class RowPrefixTooltip
    {
        private static readonly (string Prefix, string Text)[] Prefixes =
        {
            ("Auto:", "Auto: a method that the vanilla profiler times."),
            ("P:", "P: the whole method, with all of its Harmony patches. Added by Frame Profiler Plus."),
            ("H:", "H: one Harmony prefix, postfix or finalizer. The label names the mod that owns it. Added by Frame Profiler Plus."),
            ("T:", "T: a call that a transpiler added or changed. Added by Frame Profiler Plus."),
            ("X:", "X: a method timed by Extended coverage (Coverage tab). Added by Frame Profiler Plus."),
        };

        // The explanation for a row label, or null when the label has no known prefix.
        public static string Describe(string label)
        {
            if (string.IsNullOrEmpty(label))
                return null;
            foreach (var (prefix, text) in Prefixes)
            {
                if (label.StartsWith(prefix, System.StringComparison.Ordinal))
                    return text;
            }
            return null;
        }

        public static void Postfix(ProfilerRowListView __instance, int index, Rect rect, bool hovered)
        {
            if (!hovered || index < 0 || index >= __instance.Rows.Count)
                return;
            var text = Describe(__instance.Rows[index].Label);
            if (text != null)
                TooltipHandler.TipRegion(rect, text);
        }
    }
}
