using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using FrameProfilerPlus.Ticks;
using RimWorld;
using Verse;
using Verse.ProfilerUI;
using Waylith.Profiling;

namespace FrameProfilerPlus.UI
{
    // Tick time grouped by game object (ThingDef for now), over the selected frames. Per-tick figures divide a
    // group's time by the game ticks of the selection; p95 and worst compare frames. Self time only, and the
    // time is simulation code. Groups of different probes overlap, so they are never summed together.
    public sealed class GroupsSection : ProfilerRowSection
    {
        private const int MaxGroupRows = 500;

        private readonly List<FrameProfiler.FrameSnapshot> frames = new List<FrameProfiler.FrameSnapshot>();
        private readonly List<FrameTicks> selection = new List<FrameTicks>();
        private readonly List<GroupStat> rowStats = new List<GroupStat>();
        private readonly Dictionary<string, string> owners = new Dictionary<string, string>();
        private int builtForRefresh = -1;

        public GroupsSection(ProfilerSession session) : base(session)
        {
            View.Columns = new[]
            {
                new ProfilerColumn(ProfilerColumnKind.Total, 90f, () => "µs/tick"),
                new ProfilerColumn(ProfilerColumnKind.Calls, 52f, () => "calls/tick"),
            };
            View.LabelHeader = () => "group (self time)";
            View.RootCrumb = () => "Groups";
            View.DetailHeader = "owner · p95 · worst frame";
        }

        protected override string CopyHeader => "group\tus/tick\tcalls/tick";

        protected override bool SupportsSubtreeCopy => false;

        public override void Refresh()
        {
            var aggregator = TickProbes.Aggregator;
            if (builtForRefresh == session.RefreshTick || aggregator == null)
                return;
            builtForRefresh = session.RefreshTick;
            session.CollectFrames(Math.Max(1, session.Span), frames);
            selection.Clear();
            selection.AddRange(frames.Select(f => new FrameTicks(f.frameIndex, f.tickDelta)));
            var stats = aggregator.Summarise(TickProbe.Thing, selection, Stopwatch.Frequency).Take(MaxGroupRows).ToList();

            rowStats.Clear();
            View.Resize(stats.Count + 1);
            SetRow(View.Rows[0], stats.Count == 0 ? "Things: no tick data in the selected frames" : $"Things ({stats.Count})", 0,
                stats.Sum(s => s.MeanMsPerTick) * 1000, 0, stats.Count == 0 ? "capture frames while the game runs unpaused" : "");
            rowStats.Add(null);
            for (var i = 0; i < stats.Count; i++)
            {
                var s = stats[i];
                SetRow(View.Rows[i + 1], s.Name, 1, s.MeanMsPerTick * 1000, (long)Math.Round(s.CallsPerTick),
                    $"{Owner(s.Name)} · p95 {s.P95MsPerTick:F3} ms · worst {s.WorstMsPerTick:F3} ms (frame {s.WorstFrame}) · {s.CallsPerTick:F2} calls/tick");
                rowStats.Add(s);
            }
            View.Notify_RowsChanged();
        }

        private string Owner(string defName)
        {
            if (!owners.TryGetValue(defName, out var owner))
                owners[defName] = owner = DefDatabase<ThingDef>.GetNamedSilentFail(defName)?.modContentPack?.Name ?? "?";
            return owner;
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

        public List<FloatMenuOption> MenuOptions(int row)
        {
            var options = new List<FloatMenuOption>();
            var stat = row >= 0 && row < rowStats.Count ? rowStats[row] : null;
            if (stat == null)
                return options;
            options.Add(new FloatMenuOption($"Jump to worst frame (frame {stat.WorstFrame}, {stat.WorstMsPerTick:F3} ms/tick)", () => JumpTo(stat.WorstFrame)));
            options.Add(new FloatMenuOption("Show the slowest calls in that frame", () => ShowSlowest(stat)));
            return options;
        }

        private void JumpTo(int frameIndex)
        {
            if (!TryOrdinalOf(frameIndex, out var ordinal) || !session.PinOrdinal(ordinal, false, out _))
                Messages.Message("That frame has left the profiler history.", MessageTypeDefOf.RejectInput, false);
        }

        // History frames carry a capture ordinal (what PinOrdinal takes) and the frame number the probes tag by.
        private static bool TryOrdinalOf(int frameIndex, out int ordinal)
        {
            using (FrameProfiler.BeginRead())
            {
                for (var offset = 0; FrameProfiler.TryGetHistorySnapshot(offset, out var snapshot); offset++)
                {
                    if (snapshot.frameIndex == frameIndex)
                    {
                        ordinal = snapshot.captureOrdinal;
                        return true;
                    }
                }
            }
            ordinal = 0;
            return false;
        }

        private static void ShowSlowest(GroupStat stat)
        {
            var aggregator = TickProbes.Aggregator;
            var calls = aggregator.TopIndividuals(stat.WorstFrame, TickProbe.Thing).Where(i => i.Key == stat.Key).ToList();
            var text = new StringBuilder($"{stat.Name}, frame {stat.WorstFrame}: the slowest calls of the frame's 16 slowest overall.\n\n");
            if (calls.Count == 0)
                text.AppendLine("None of this group's calls were among them.");
            var live = Find.Maps.SelectMany(m => m.listerThings.AllThings).Where(t => calls.Any(c => c.Id == t.thingIDNumber)).ToDictionary(t => (long)t.thingIDNumber);
            var ticksPerMs = Stopwatch.Frequency / 1000d;
            foreach (var call in calls)
            {
                var who = live.TryGetValue(call.Id, out var thing) ? thing.LabelShort : "gone or off the map";
                text.AppendLine($"{who} (ID {call.Id}): {call.SelfTicks / ticksPerMs:F3} ms");
            }
            Find.WindowStack.Add(new Dialog_MessageBox(text.ToString()));
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
