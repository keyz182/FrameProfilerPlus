using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Verse.ProfilerUI;
using Waylith.Profiling;

namespace FrameProfilerPlus.UI
{
    // Adds a tab after the vanilla ones in the Frame Profiler window. The tab strip's labels are a static
    // readonly array, so the transpiler swaps the array load, maps the active index to our tab while our
    // section is active, and handles a click on our index. The vanilla tab count differs between builds
    // (4976 has three, 4981 has four), so the index comes from the label array, never a constant.
    [HarmonyPatch(typeof(Window_FrameProfiler), "DrawTabStrip")]
    public static class PlusTab
    {
        private static readonly FieldInfo TabLabelsField = AccessTools.Field(typeof(Window_FrameProfiler), "TabLabels");
        private static readonly MethodInfo SegmentsMethod = AccessTools.Method(typeof(ProfilerGui), nameof(ProfilerGui.Segments));
        private static readonly AccessTools.FieldRef<Window_FrameProfiler, ProfilerSession> SessionRef =
            AccessTools.FieldRefAccess<Window_FrameProfiler, ProfilerSession>("session");
        private static readonly MethodInfo SwitchSection = AccessTools.Method(typeof(Window_FrameProfiler), "SwitchSection");
        private static readonly ConditionalWeakTable<Window_FrameProfiler, PlusSection> Sections = new ConditionalWeakTable<Window_FrameProfiler, PlusSection>();

        private static readonly AccessTools.FieldRef<Window_FrameProfiler, ProfilerRowSection> ActiveSectionRef =
            AccessTools.FieldRefAccess<Window_FrameProfiler, ProfilerRowSection>("activeSection");

        private static string[] labels;

        public static int TabIndex => ((string[])TabLabelsField.GetValue(null)).Length;

        public static string[] Labels() => labels ??= ((string[])TabLabelsField.GetValue(null)).Append("Plus").ToArray();

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var labelsAt = codes.FindIndex(c => c.LoadsField(TabLabelsField));
            var segmentsAt = codes.FindIndex(c => c.Calls(SegmentsMethod));
            // Segments(ref x, y, labels, activeIndex, Color? activeFill, redIndex): the default Color? is built
            // with `ldloca; initobj`, and the active-index branches all join at that ldloca.
            var initobjAt = codes.FindIndex(labelsAt + 1, c => c.opcode == OpCodes.Initobj);
            if (labelsAt < 0 || segmentsAt < 0 || initobjAt < 0 || initobjAt > segmentsAt || !codes[segmentsAt + 1].IsStloc())
            {
                Log.Warning("[FrameProfilerPlus] Profiler tab strip not recognised; the Plus tab is unavailable.");
                return codes;
            }

            codes[labelsAt] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlusTab), nameof(Labels))).MoveLabelsFrom(codes[labelsAt]);
            // After `stloc clicked`: clicked = OnClicked(this, clicked).
            var store = codes[segmentsAt + 1];
            var load = LoadFor(store);
            codes.InsertRange(segmentsAt + 2, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                load,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlusTab), nameof(OnClicked))),
                new CodeInstruction(store.opcode, store.operand),
            });
            // Before the Color? argument: activeIndex = ActiveIndex(activeIndex, this).
            var join = codes[initobjAt - 1];
            codes.InsertRange(initobjAt - 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(join),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlusTab), nameof(ActiveIndex))),
            });
            return codes;
        }

        public static int ActiveIndex(int vanillaIndex, Window_FrameProfiler window) =>
            ActiveSectionRef(window) is PlusSection ? TabIndex : vanillaIndex;

        private static CodeInstruction LoadFor(CodeInstruction store)
        {
            if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            return new CodeInstruction(store.opcode == OpCodes.Stloc_S ? OpCodes.Ldloc_S : OpCodes.Ldloc, store.operand);
        }

        // Returns the index vanilla should act on: -1 when the click was ours.
        public static int OnClicked(Window_FrameProfiler window, int clicked)
        {
            if (clicked != TabIndex)
                return clicked;
            var section = Sections.GetValue(window, w => new PlusSection(SessionRef(w)));
            SwitchSection.Invoke(window, new object[] { section });
            return -1;
        }
    }

    // Spike section: Harmony patch (H:) and coverage (X:) scopes in the selected frames, averaged per frame.
    public sealed class PlusSection : ProfilerRowSection
    {
        private readonly List<FrameProfiler.FrameSnapshot> frames = new List<FrameProfiler.FrameSnapshot>();
        private int builtForRefresh = -1;

        public PlusSection(ProfilerSession session) : base(session)
        {
            View.Columns = new[]
            {
                new ProfilerColumn(ProfilerColumnKind.Total, 90f, () => "total"),
                new ProfilerColumn(ProfilerColumnKind.Calls, 52f, () => "calls"),
            };
            View.LabelHeader = () => "Harmony patches and extra coverage";
        }

        protected override string CopyHeader => "scope\ttotal\tcalls";

        protected override bool SupportsSubtreeCopy => false;

        public override void Refresh()
        {
            if (builtForRefresh == session.RefreshTick)
                return;
            builtForRefresh = session.RefreshTick;

            session.CollectFrames(Math.Max(1, session.Span), frames);
            var totals = new Dictionary<string, (double us, long calls)>();
            foreach (var frame in frames)
            {
                for (var i = 0; i < frame.pathEntryCount; i++)
                {
                    var entry = frame.pathEntries[i];
                    var label = FrameProfiler.GetLabel(FrameProfiler.PathLabel(entry.pathId));
                    if (!label.StartsWith("H:") && !label.StartsWith("X:"))
                        continue;
                    totals.TryGetValue(label, out var sum);
                    totals[label] = (sum.us + entry.totalMicroseconds, sum.calls + entry.calls);
                }
            }

            var count = Math.Max(1, frames.Count);
            var ordered = totals.OrderByDescending(t => t.Value.us).ToList();
            View.Resize(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var data = View.Rows[i];
                data.Reset();
                data.Label = ordered[i].Key;
                data.TotalMicroseconds = ordered[i].Value.us / count;
                data.Calls = ordered[i].Value.calls / count;
            }
            View.Notify_RowsChanged();
        }

        public override void Invalidate() => builtForRefresh = -1;

        public override void Notify_SearchChanged() => Invalidate();

        public override void ExpandAll()
        {
        }

        public override void CollapseAll()
        {
        }

        protected override void ToggleExpansionAt(int index)
        {
        }

        protected override bool BuildFullSubtree(int index, List<ProfilerRowData> into) => false;
    }
}
