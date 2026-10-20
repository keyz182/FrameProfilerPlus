using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameProfilerPlus.Core;
using HarmonyLib;
using Waylith.Profiling;
using Waylith.Profiling.Instrumentation;

namespace FrameProfilerPlus.Coverage
{
    public enum SeenState
    {
        // No frame has been captured since the method was patched.
        NoFramesYet,
        Seen,
        // Vanilla auto-muted the label; it records nothing, so it says nothing about whether the method ran.
        Muted,
        // Frames were captured after the patch and none shows the scope.
        NeverSeen,
    }

    // The parts of coverage that touch game types: vanilla's instrumented set and the profiler history.
    public static class GameCoverage
    {
        private static readonly FieldInfo ReplacedSet = AccessTools.Field(typeof(ProfilerInstrumentation), "replacedMethodSet");
        private static bool warned;

        // Methods vanilla times with Auto: scopes. A renamed field is treated as "none", with one warning.
        public static IReadOnlyCollection<MethodBase> VanillaTimed()
        {
            if (ReplacedSet?.GetValue(null) is HashSet<MethodBase> set)
            {
                try
                {
                    return set.ToList();
                }
                catch (InvalidOperationException)
                {
                    return Array.Empty<MethodBase>();
                }
            }
            if (!warned)
            {
                warned = true;
                FrameProfilerPlusMod.Warn("Vanilla's instrumented-method set was not found; coverage cannot skip methods vanilla already times.");
            }
            return Array.Empty<MethodBase>();
        }

        private static readonly FieldInfo ReplacedList = AccessTools.Field(typeof(ProfilerInstrumentation), "replacedMethods");

        // The method behind a vanilla Auto: row, or null.
        public static MethodBase VanillaMethodForLabel(int labelId)
        {
            if (!(ReplacedList?.GetValue(null) is IEnumerable<(MethodBase Method, ProfilerLabelId Label)> list))
                return null;
            try
            {
                return list.Where(e => e.Label.Value == labelId).Select(e => e.Method).FirstOrDefault();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        // On demand: reads the history under the profiler's read lock. A frame counts when it was captured
        // after the slot's apply epoch.
        public static Dictionary<MethodBase, SeenState> Scan(CoverageInstrumenter instrumenter)
        {
            var result = new Dictionary<MethodBase, SeenState>();
            var watched = new Dictionary<MethodBase, int>();
            foreach (var method in instrumenter.Covered)
            {
                if (instrumenter.TryGetSlot(method, out var slot, out _))
                    watched[method] = slot;
            }
            var seen = new HashSet<(int label, long after)>();
            var newestOrdinal = 0;
            using (FrameProfiler.BeginRead())
            {
                for (var offset = 0; FrameProfiler.TryGetHistorySnapshot(offset, out var frame); offset++)
                {
                    newestOrdinal = Math.Max(newestOrdinal, frame.captureOrdinal);
                    CollectLabels(frame, watched, instrumenter, seen);
                }
            }
            foreach (var pair in watched)
                result[pair.Key] = StateOf(instrumenter, pair.Key, pair.Value, newestOrdinal, seen);
            return result;
        }

        private static void CollectLabels(FrameProfiler.FrameSnapshot frame, Dictionary<MethodBase, int> watched, CoverageInstrumenter instrumenter,
            HashSet<(int label, long after)> seen)
        {
            var labels = new HashSet<int>();
            for (var i = 0; i < frame.pathEntryCount; i++)
                labels.Add(FrameProfiler.PathLabel(frame.pathEntries[i].pathId).Value);
            foreach (var pair in watched)
            {
                var label = ScopeRuntime.Registry.Get(pair.Value).LabelId;
                if (label == LabelRegistry.Unassigned || !labels.Contains(label))
                    continue;
                instrumenter.TryGetSlot(pair.Key, out _, out var appliedAt);
                if (frame.captureOrdinal > appliedAt)
                    seen.Add((label, appliedAt));
            }
        }

        private static SeenState StateOf(CoverageInstrumenter instrumenter, MethodBase method, int slot, int newestOrdinal,
            HashSet<(int label, long after)> seen)
        {
            instrumenter.TryGetSlot(method, out _, out var appliedAt);
            var label = ScopeRuntime.Registry.Get(slot).LabelId;
            if (label != LabelRegistry.Unassigned && seen.Contains((label, appliedAt)))
                return SeenState.Seen;
            if (label != LabelRegistry.Unassigned && FrameProfiler.IsLabelMuted(new ProfilerLabelId((ushort)label)))
                return SeenState.Muted;
            return newestOrdinal > appliedAt ? SeenState.NeverSeen : SeenState.NoFramesYet;
        }

        public static long Epoch() => FrameProfiler.CompletedFrameCount;
    }
}
