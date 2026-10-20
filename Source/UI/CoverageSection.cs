using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameProfilerPlus.Core;
using FrameProfilerPlus.Coverage;
using UnityEngine;
using Verse;
using Verse.ProfilerUI;
using Waylith.Profiling;

namespace FrameProfilerPlus.UI
{
    // Extended coverage: the target lines, what they selected, and what became of each method. Rows list what
    // is timed (with its time over the selected frames and whether the scope was ever seen), what failed, what
    // stays detoured after being untimed, and what was skipped. The footer holds the editor and the buttons.
    public sealed class CoverageSection : ProfilerRowSection
    {
        private const float EditorHeight = 112f;

        private readonly List<FrameProfiler.FrameSnapshot> frames = new List<FrameProfiler.FrameSnapshot>();
        private readonly List<MethodBase> rowMethods = new List<MethodBase>();
        private Dictionary<MethodBase, SeenState> seen = new Dictionary<MethodBase, SeenState>();
        private int builtForRefresh = -1;
        private int loadedVersion = CoverageHost.TargetsVersion;
        private string text = string.Join("\n", FrameProfilerPlusMod.Settings.coverageTargets);
        private string status = "Edit the targets, then Apply. One per line: [-][Assembly!][Type][::Method]; * and ? match, - excludes.";
        private static CoverageService Service => CoverageHost.Service;

        public CoverageSection(ProfilerSession session) : base(session)
        {
            View.Columns = new[]
            {
                new ProfilerColumn(ProfilerColumnKind.Total, 90f, () => "total"),
                new ProfilerColumn(ProfilerColumnKind.Calls, 52f, () => "calls"),
            };
            View.LabelHeader = () => "method / state";
            View.RootCrumb = () => "Coverage";
            View.DetailHeader = "state";
        }

        protected override string CopyHeader => "method\ttotal\tcalls";

        protected override bool SupportsSubtreeCopy => false;

        protected override float FooterHeight => EditorHeight + 26f;

        public MethodBase MethodAt(int row) => row >= 0 && row < rowMethods.Count ? rowMethods[row] : null;

        public SeenState StateOf(MethodBase method) => seen.TryGetValue(method, out var state) ? state : SeenState.NoFramesYet;

        public override void Refresh()
        {
            if (builtForRefresh == session.RefreshTick || Service == null)
                return;
            builtForRefresh = session.RefreshTick;
            session.CollectFrames(Math.Max(1, session.Span), frames);
            var rows = new List<(string label, int depth, double us, long calls, string detail, MethodBase method)>();
            AddTimed(rows);
            AddFailed(rows);
            AddDetoured(rows);
            AddSkipped(rows);

            View.Resize(rows.Count);
            rowMethods.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                SetRow(View.Rows[i], row.label, row.depth, row.us, row.calls, row.detail);
                rowMethods.Add(row.method);
            }
            View.Notify_RowsChanged();
        }

        private void AddTimed(List<(string, int, double, long, string, MethodBase)> rows)
        {
            var covered = Service.Instrumenter.Covered.ToList();
            var perLabel = new Dictionary<int, (double us, long calls)>();
            foreach (var frame in frames)
            {
                for (var i = 0; i < frame.pathEntryCount; i++)
                {
                    var entry = frame.pathEntries[i];
                    var id = FrameProfiler.PathLabel(entry.pathId).Value;
                    perLabel.TryGetValue(id, out var sum);
                    perLabel[id] = (sum.us + entry.totalMicroseconds, sum.calls + entry.calls);
                }
            }
            var perFrame = Math.Max(1, frames.Count);
            var timed = new List<(string label, double us, long calls, string detail, MethodBase method)>();
            foreach (var method in covered)
            {
                Service.Instrumenter.TryGetSlot(method, out var slot, out _);
                var entry = ScopeRuntime.Registry.Get(slot);
                perLabel.TryGetValue(entry.LabelId, out var sum);
                timed.Add((entry.LabelText, sum.us / perFrame, sum.calls / perFrame, seen.Count == 0 ? "not checked" : Describe(StateOf(method)), method));
            }
            rows.Add(($"Timed ({timed.Count})", 0, timed.Sum(t => t.us), timed.Sum(t => t.calls), "", null));
            foreach (var t in timed.OrderByDescending(t => t.us).ThenBy(t => t.label))
                rows.Add((t.label, 1, t.us, t.calls, t.detail, t.method));
        }

        private void AddFailed(List<(string, int, double, long, string, MethodBase)> rows)
        {
            var failed = Service.Instrumenter.Failed;
            if (failed.Count == 0)
                return;
            rows.Add(($"Failed ({failed.Count})", 0, 0, 0, "Harmony could not patch these", null));
            foreach (var pair in failed)
                rows.Add((Name(pair.Key), 1, 0, 0, pair.Value, pair.Key));
        }

        private void AddDetoured(List<(string, int, double, long, string, MethodBase)> rows)
        {
            var idle = Service.Instrumenter.DetouredUntilRestart.Where(m => !Service.Instrumenter.IsCovered(m)).ToList();
            if (idle.Count > 0)
            {
                rows.Add(($"Untimed but detoured until restart ({idle.Count})", 0, 0, 0, "vanilla timing on these records nothing", null));
                rows.AddRange(idle.Select(m => (Name(m), 1, 0d, 0L, "", m)));
            }
            var zero = Service.DetouredAndVanillaTimed();
            if (zero.Count > 0)
            {
                rows.Add(($"Vanilla rows that now read zero ({zero.Count})", 0, 0, 0, "also in vanilla's instrumented set", null));
                rows.AddRange(zero.Select(m => (Name(m), 1, 0d, 0L, "", m)));
            }
        }

        private void AddSkipped(List<(string, int, double, long, string, MethodBase)> rows)
        {
            var selection = Service.Last?.Selection;
            if (selection == null || selection.Skipped.Count == 0)
                return;
            rows.Add(($"Skipped ({selection.Skipped.Values.Sum()})", 0, 0, 0, "from the last Apply", null));
            foreach (var pair in selection.Skipped)
                rows.Add((Why(pair.Key), 1, 0, pair.Value, "", null));
        }

        private static string Why(SkipReason reason) => reason switch
        {
            SkipReason.NoBody => "no body (abstract or native)",
            SkipReason.OpenGeneric => "open generic",
            SkipReason.AlreadyCovered => "already timed",
            SkipReason.TimedByVanilla => "vanilla already times it",
            SkipReason.ForeignPatch => "another mod patches it (has a P: scope)",
            _ => reason.ToString(),
        };

        private static string Name(MethodBase m) => $"{m.DeclaringType?.FullName}.{m.Name}";

        private static string Describe(SeenState state) => state switch
        {
            SeenState.Seen => "seen",
            SeenState.Muted => "muted by vanilla",
            SeenState.NeverSeen => "never seen: inlined, or not called",
            _ => "no frames yet",
        };

        private static void SetRow(ProfilerRowData data, string label, int depth, double us, long calls, string detail)
        {
            data.Reset();
            data.Label = label;
            data.Depth = depth;
            data.TotalMicroseconds = us;
            data.Calls = calls;
            data.Detail = detail;
        }

        protected override void DrawFooter(Rect rect)
        {
            if (loadedVersion != CoverageHost.TargetsVersion)
            {
                loadedVersion = CoverageHost.TargetsVersion;
                text = string.Join("\n", FrameProfilerPlusMod.Settings.coverageTargets);
            }
            var snapshot = session.HasData ? session.Snapshot : null;
            if (snapshot != null && (snapshot.overflowedScopes > 0 || snapshot.droppedEvents > 0))
            {
                GUI.color = new Color(1f, 0.6f, 0.4f);
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 22f), "Profiler buffer overflowed: coverage is too wide. Narrow the targets.");
                GUI.color = Color.white;
            }
            var editor = new Rect(rect.x, rect.y + 24f, rect.width, EditorHeight - 24f);
            text = Widgets.TextArea(editor, text);

            var y = rect.y + EditorHeight;
            var x = rect.x;
            if (Button(ref x, y, "Preview"))
                Run(() => Describe(Service.Preview(Lines(), FrameProfilerPlusMod.Settings.maxCoverageMethods)));
            if (Button(ref x, y, "Apply"))
                Run(ApplyTargets);
            if (Button(ref x, y, "Clear"))
                Run(() =>
                {
                    Service.Clear();
                    return "Stopped timing. Those methods stay detoured until restart.";
                });
            if (Button(ref x, y, "Check seen"))
                Run(() =>
                {
                    seen = GameCoverage.Scan(Service.Instrumenter);
                    return $"Checked {seen.Count} methods against the captured frames.";
                });
            Widgets.Label(new Rect(x + 6f, y, rect.xMax - x - 6f, 24f), status);
        }

        private bool Button(ref float x, float y, string label)
        {
            var clicked = Widgets.ButtonText(new Rect(x, y, 90f, 24f), label);
            x += 94f;
            return clicked;
        }

        private IEnumerable<string> Lines() => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

        private string ApplyTargets()
        {
            var settings = FrameProfilerPlusMod.Settings;
            settings.coverageTargets = Lines().ToList();
            settings.Write();
            var report = Service.Apply(settings.coverageTargets, settings.maxCoverageMethods);
            builtForRefresh = -1;
            return CoverageHost.Describe(report);
        }

        private string Describe(CoverageService.Report report) =>
            report.ParseErrors.Count > 0 ? string.Join("; ", report.ParseErrors)
            : report.Refused ? $"{report.Selection.Matched} methods match, over the limit of {FrameProfilerPlusMod.Settings.maxCoverageMethods}"
            : $"{report.Selection.Methods.Count} methods would be timed ({report.Selection.Matched} matched)";

        // Runs a button action; a failure shows in the status line instead of the log only.
        private void Run(Func<string> action)
        {
            try
            {
                status = action();
            }
            catch (Exception e)
            {
                status = "Failed: " + e.Message;
                FrameProfilerPlusMod.Warn(e.ToString());
            }
        }

        public List<FloatMenuOption> MenuOptions(int row)
        {
            var options = new List<FloatMenuOption>();
            var method = MethodAt(row);
            if (method != null && Service.Instrumenter.IsCovered(method))
            {
                options.Add(new FloatMenuOption("Recompile callers (re-patch the methods that call this one)", () =>
                    Run(() => $"Re-patched {Service.RecompileCallers(method)} callers of {Name(method)}.")));
            }
            return options;
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
