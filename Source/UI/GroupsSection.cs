using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using FrameProfilerPlus.Core;
using FrameProfilerPlus.Ticks;
using RimWorld;
using Verse;
using Verse.ProfilerUI;
using Waylith.Profiling;

namespace FrameProfilerPlus.UI
{
    // Tick time grouped by game object, one root per probe, over the selected frames. Per-tick figures divide a
    // group's time by the game ticks of the selection; p95 and worst compare frames. Self time only, and the
    // time is simulation code. Probes overlap (a pawn's tick contains its job driver and comps), so roots are
    // never summed together; a note row under each root says what it overlaps with.
    public sealed class GroupsSection : ProfilerRowSection
    {
        private const int MaxGroupRows = 500;

        private sealed class RowInfo
        {
            public TickProbe Probe;
            public GroupStat Stat;
        }

        private readonly List<FrameProfiler.FrameSnapshot> frames = new List<FrameProfiler.FrameSnapshot>();
        private readonly List<FrameTicks> selection = new List<FrameTicks>();
        private readonly List<RowInfo> rowInfo = new List<RowInfo>();
        private readonly Dictionary<(TickProbe, string), string> owners = new Dictionary<(TickProbe, string), string>();
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

            var rows = new List<(string label, int depth, double us, long calls, string detail, bool dim, RowInfo info)>();
            foreach (var probe in TickProbes.Enabled)
            {
                var stats = aggregator.Summarise(probe, selection, Stopwatch.Frequency).Take(MaxGroupRows).ToList();
                rows.Add((stats.Count == 0 ? $"{Title(probe)}: no tick data in the selected frames" : $"{Title(probe)} ({stats.Count})", 0,
                    stats.Sum(s => s.MeanMsPerTick) * 1000, 0, stats.Count == 0 ? "capture frames while the game runs unpaused" : "", false, null));
                rows.Add((Note(probe), 1, 0, 0, "", true, null));
                foreach (var s in stats)
                {
                    rows.Add((ShortName(probe, s.Name), 1, s.MeanMsPerTick * 1000, (long)Math.Round(s.CallsPerTick),
                        $"{Owner(probe, s.Name)} · p95 {s.P95MsPerTick:F3} ms · worst {s.WorstMsPerTick:F3} ms (frame {s.WorstFrame}) · {s.CallsPerTick:F2} calls/tick",
                        false, new RowInfo { Probe = probe, Stat = s }));
                }
            }

            View.Resize(rows.Count);
            rowInfo.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                SetRow(View.Rows[i], row.label, row.depth, row.us, row.calls, row.detail, row.dim);
                rowInfo.Add(row.info);
            }
            View.Notify_RowsChanged();
        }

        private static string Title(TickProbe probe) => probe switch
        {
            TickProbe.Thing => "Things",
            TickProbe.Driver => "Job drivers",
            TickProbe.Comp => "Comps",
            _ => probe.ToString(),
        };

        // What each root overlaps with, so nobody adds the roots together.
        private static string Note(TickProbe probe) => probe switch
        {
            TickProbe.Thing => "Includes the time of these things' job drivers and comps. Do not add the roots together.",
            TickProbe.Driver => "Also counted inside the Pawn rows under Things.",
            TickProbe.Comp => "Also counted inside the parent's row under Things.",
            _ => "Overlaps the other roots.",
        };

        private static string ShortName(TickProbe probe, string name)
        {
            if (probe == TickProbe.Thing)
                return name;
            var dot = name.LastIndexOf('.');
            return dot < 0 ? name : name.Substring(dot + 1);
        }

        private string Owner(TickProbe probe, string name)
        {
            if (owners.TryGetValue((probe, name), out var owner))
                return owner;
            if (probe == TickProbe.Thing)
                owner = DefDatabase<ThingDef>.GetNamedSilentFail(name)?.modContentPack?.Name;
            else
                owner = ModResolver.ModName(TickProbes.TypeNamed(name)?.Assembly);
            return owners[(probe, name)] = owner ?? "?";
        }

        private static void SetRow(ProfilerRowData data, string label, int depth, double us, long calls, string detail, bool dim)
        {
            data.Reset();
            data.Label = label;
            data.Depth = depth;
            data.TotalMicroseconds = us;
            data.Calls = calls;
            data.Detail = detail;
            data.Dimmed = dim;
        }

        public List<FloatMenuOption> MenuOptions(int row)
        {
            var options = new List<FloatMenuOption>();
            var info = row >= 0 && row < rowInfo.Count ? rowInfo[row] : null;
            if (info == null)
                return options;
            var stat = info.Stat;
            options.Add(new FloatMenuOption($"Jump to worst frame (frame {stat.WorstFrame}, {stat.WorstMsPerTick:F3} ms/tick)", () => JumpTo(stat.WorstFrame)));
            options.Add(new FloatMenuOption("Show the slowest calls in that frame", () => ShowSlowest(info.Probe, stat)));
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

        // Every probe records a thing's ID as the individual (the thing, its pawn, or its comp's parent).
        private static void ShowSlowest(TickProbe probe, GroupStat stat)
        {
            var calls = TickProbes.Aggregator.TopIndividuals(stat.WorstFrame, probe).Where(i => i.Key == stat.Key).ToList();
            var text = new StringBuilder($"{stat.Name}, frame {stat.WorstFrame}: the group's calls among the frame's 16 slowest of this kind.\n\n");
            if (calls.Count == 0)
                text.AppendLine("None of this group's calls were among them.");
            var live = new Dictionary<long, Thing>();
            foreach (var thing in Find.Maps.SelectMany(m => m.listerThings.AllThings).Where(t => calls.Any(c => c.Id == t.thingIDNumber)))
                live[thing.thingIDNumber] = thing;
            var ticksPerMs = Stopwatch.Frequency / 1000d;
            foreach (var call in calls)
            {
                var who = live.TryGetValue(call.Id, out var found) ? found.LabelShort : "gone or off the map";
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
