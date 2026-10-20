using System;
using System.Collections.Generic;
using System.Linq;
using FrameProfilerPlus.Core;
using Verse.ProfilerUI;
using Waylith.Profiling;

namespace FrameProfilerPlus.UI
{
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
            View.RootCrumb = () => "Patches";
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
