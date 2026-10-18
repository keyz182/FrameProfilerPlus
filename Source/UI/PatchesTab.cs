using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Core;
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
    public static class PatchesTab
    {
        private static readonly FieldInfo TabLabelsField = AccessTools.Field(typeof(Window_FrameProfiler), "TabLabels");
        private static readonly MethodInfo SegmentsMethod = AccessTools.Method(typeof(ProfilerGui), nameof(ProfilerGui.Segments));
        private static readonly AccessTools.FieldRef<Window_FrameProfiler, ProfilerSession> SessionRef =
            AccessTools.FieldRefAccess<Window_FrameProfiler, ProfilerSession>("session");
        private static readonly MethodInfo SwitchSection = AccessTools.Method(typeof(Window_FrameProfiler), "SwitchSection");
        private static readonly ConditionalWeakTable<Window_FrameProfiler, PatchesSection> Sections = new ConditionalWeakTable<Window_FrameProfiler, PatchesSection>();

        private static readonly AccessTools.FieldRef<Window_FrameProfiler, ProfilerRowSection> ActiveSectionRef =
            AccessTools.FieldRefAccess<Window_FrameProfiler, ProfilerRowSection>("activeSection");

        private static string[] labels;

        public static int TabIndex => ((string[])TabLabelsField.GetValue(null)).Length;

        public static string[] Labels() => labels ??= ((string[])TabLabelsField.GetValue(null)).Append("Patches").ToArray();

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

            codes[labelsAt] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PatchesTab), nameof(Labels))).MoveLabelsFrom(codes[labelsAt]);
            // After `stloc clicked`: clicked = OnClicked(this, clicked).
            var store = codes[segmentsAt + 1];
            var load = LoadFor(store);
            codes.InsertRange(segmentsAt + 2, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                load,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PatchesTab), nameof(OnClicked))),
                new CodeInstruction(store.opcode, store.operand),
            });
            // Before the Color? argument: activeIndex = ActiveIndex(activeIndex, this).
            var join = codes[initobjAt - 1];
            codes.InsertRange(initobjAt - 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(join),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PatchesTab), nameof(ActiveIndex))),
            });
            return codes;
        }

        public static int ActiveIndex(int vanillaIndex, Window_FrameProfiler window) =>
            ActiveSectionRef(window) is PatchesSection ? TabIndex : vanillaIndex;

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
            var section = Sections.GetValue(window, w => new PatchesSection(SessionRef(w)));
            SwitchSection.Invoke(window, new object[] { section });
            return -1;
        }
    }

    // Harmony patch time by mod: each mod's H: scopes in the selected frames, averaged per frame, with its
    // patches below it. Totals include anything a patch calls. Labels vanilla has auto-muted (cheap, hot
    // patches) record nothing, so they show as "muted" and their mod's total is too low.
    public sealed class PatchesSection : ProfilerRowSection
    {
        private sealed class Sum
        {
            public double us;
            public long calls;
            public bool muted;
        }

        private readonly List<FrameProfiler.FrameSnapshot> frames = new List<FrameProfiler.FrameSnapshot>();
        private int builtForRefresh = -1;

        public PatchesSection(ProfilerSession session) : base(session)
        {
            View.Columns = new[]
            {
                new ProfilerColumn(ProfilerColumnKind.Total, 90f, () => "total"),
                new ProfilerColumn(ProfilerColumnKind.Calls, 52f, () => "calls"),
            };
            View.LabelHeader = () => "mod / Harmony patch";
            View.DetailHeader = "muted patches under-count";
        }

        protected override string CopyHeader => "patch\ttotal\tcalls";

        protected override bool SupportsSubtreeCopy => false;

        public override void Refresh()
        {
            if (builtForRefresh == session.RefreshTick || ScopeRuntime.Registry == null)
                return;
            builtForRefresh = session.RefreshTick;

            var byMod = new Dictionary<string, Dictionary<string, Sum>>();
            session.CollectFrames(Math.Max(1, session.Span), frames);
            foreach (var frame in frames)
            {
                for (var i = 0; i < frame.pathEntryCount; i++)
                    Add(byMod, frame.pathEntries[i]);
            }
            AddMuted(byMod);
            Fill(byMod, Math.Max(1, frames.Count));
        }

        private static void Add(Dictionary<string, Dictionary<string, Sum>> byMod, FrameProfiler.PathEntry entry)
        {
            var labelId = FrameProfiler.PathLabel(entry.pathId);
            var label = FrameProfiler.GetLabel(labelId);
            if (!label.StartsWith("H:"))
                return;
            var sum = SumFor(byMod, ModOf(labelId.Value), label);
            sum.us += entry.totalMicroseconds;
            sum.calls += entry.calls;
        }

        // Muted labels produce no path entries; list them so the under-count is visible.
        private static void AddMuted(Dictionary<string, Dictionary<string, Sum>> byMod)
        {
            for (var slot = 0; slot < ScopeRuntime.Registry.SlotCount; slot++)
            {
                var entry = ScopeRuntime.Registry.Get(slot);
                if (entry.Kind != ScopeKind.HarmonyPatch || entry.LabelId == LabelRegistry.Unassigned)
                    continue;
                var id = new ProfilerLabelId((ushort)entry.LabelId);
                if (FrameProfiler.IsLabelMuted(id))
                    SumFor(byMod, ModOf(entry.LabelId), FrameProfiler.GetLabel(id)).muted = true;
            }
        }

        private static string ModOf(int labelId)
        {
            var owners = ScopeRuntime.Registry.SlotsForLabel(labelId).Select(s => s.Owner).Distinct().ToList();
            return owners.Count == 1 ? owners[0] : "<other>";
        }

        private static Sum SumFor(Dictionary<string, Dictionary<string, Sum>> byMod, string mod, string label)
        {
            if (!byMod.TryGetValue(mod, out var patches))
                byMod[mod] = patches = new Dictionary<string, Sum>();
            if (!patches.TryGetValue(label, out var sum))
                patches[label] = sum = new Sum();
            return sum;
        }

        private void Fill(Dictionary<string, Dictionary<string, Sum>> byMod, int frameCount)
        {
            var mods = byMod.Select(m => (mod: m.Key, patches: m.Value, us: m.Value.Values.Sum(s => s.us), calls: m.Value.Values.Sum(s => s.calls)))
                .OrderByDescending(m => m.us).ToList();
            View.Resize(mods.Sum(m => m.patches.Count + 1));
            var row = 0;
            foreach (var mod in mods)
            {
                SetRow(View.Rows[row++], mod.mod, 0, mod.us / frameCount, mod.calls / frameCount, mod.patches.Values.Any(s => s.muted) ? "has muted" : "");
                foreach (var patch in mod.patches.OrderByDescending(p => p.Value.us))
                    SetRow(View.Rows[row++], patch.Key, 1, patch.Value.us / frameCount, patch.Value.calls / frameCount, patch.Value.muted ? "muted" : "");
            }
            View.Notify_RowsChanged();
        }

        private static void SetRow(ProfilerRowData data, string label, int depth, double us, long calls, string detail)
        {
            data.Reset();
            data.Label = label;
            data.Depth = depth;
            data.TotalMicroseconds = us;
            data.Calls = calls;
            data.Detail = detail;
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
