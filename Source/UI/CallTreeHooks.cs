using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.ProfilerUI;

namespace FrameProfilerPlus.UI
{
    // Fills the call tree's Detail column with time and calls per tick over the selected frames.
    // Row totals are averages per frame, so dividing by the average ticks per frame gives per-tick figures.
    // Only meaningful for simulation code; frame-rate code runs regardless of ticks.
    [HarmonyPatch(typeof(ProfilerCallTreeTable), MethodType.Constructor, typeof(ProfilerSession))]
    public static class CallTreeDetailHeader
    {
        public static void Postfix(ProfilerCallTreeTable __instance) => __instance.View.DetailHeader = "per tick (sim only)";
    }

    [HarmonyPatch(typeof(ProfilerCallTreeTable), nameof(ProfilerCallTreeTable.Refresh))]
    public static class CallTreeTicksPerFrame
    {
        private static readonly AccessTools.FieldRef<ProfilerRowSection, ProfilerSession> SessionRef =
            AccessTools.FieldRefAccess<ProfilerRowSection, ProfilerSession>("session");

        public static float TicksPerFrame;

        public static void Prefix(ProfilerCallTreeTable __instance)
        {
            var session = SessionRef(__instance);
            if (!session.HasData)
                TicksPerFrame = 0f;
            else if (session.Span > 1)
                TicksPerFrame = session.SummarizeSelection().averageTickerDelta;
            else
                TicksPerFrame = session.Snapshot.tickDelta;
        }
    }

    [HarmonyPatch(typeof(ProfilerCallTreeTable), "Shape")]
    public static class CallTreeDetailShape
    {
        public static void Postfix(ProfilerTreeModel.Row row, ProfilerRowData data)
        {
            var ticks = CallTreeTicksPerFrame.TicksPerFrame;
            data.Detail = ticks > 0f
                ? $"{row.totalMicroseconds / ticks / 1000f:F3} ms · {row.calls / ticks:F1} calls"
                : "";
        }
    }

    // Adds items to the row context menu without replacing the vanilla ones:
    // inserts `ldarg.0; call Extend` just before `newobj FloatMenu(List<FloatMenuOption>)`.
    [HarmonyPatch(typeof(ProfilerRowSection), "OpenContextMenu")]
    public static class RowContextMenu
    {
        private static readonly ConstructorInfo FloatMenuCtor = AccessTools.Constructor(typeof(FloatMenu), new[] { typeof(List<FloatMenuOption>) });

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var found = false;
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Newobj && code.operand as ConstructorInfo == FloatMenuCtor)
                {
                    found = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RowContextMenu), nameof(Extend)));
                }
                yield return code;
            }
            if (!found)
                Log.Warning("[FrameProfilerPlus] Profiler context menu not found; row actions are unavailable.");
        }

        public static List<FloatMenuOption> Extend(List<FloatMenuOption> options, ProfilerRowSection section)
        {
            var index = section.View.SelectedIndex;
            if (index < 0 || index >= section.View.Rows.Count)
                return options;
            var label = section.View.Rows[index].Label;
            options.Add(new FloatMenuOption("Frame Profiler Plus: log label", () => Log.Message($"[FrameProfilerPlus] Selected row: {label}")));
            return options;
        }
    }
}
