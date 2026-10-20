using System;
using System.Collections.Generic;
using System.Linq;

namespace FrameProfilerPlus.Ticks
{
    public sealed partial class TickAggregator
    {
        // Nearest-rank percentile of an ascending array; empty gives 0.
        public static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0)
                return 0;
            var rank = Math.Min(sorted.Length, Math.Max(1, (int)Math.Ceiling(p * sorted.Length)));
            return sorted[rank - 1];
        }

        // Figures per group of one probe over the frames that have data and at least one tick, slowest mean first.
        public List<GroupStat> Summarise(TickProbe probe, IReadOnlyList<FrameTicks> frames, double ticksPerSecond)
        {
            var used = frames.Where(f => f.Ticks > 0 && slotByFrame.ContainsKey(f.Frame)).ToList();
            var perFrame = new Dictionary<int, double[]>();
            var selfTotal = new Dictionary<int, long>();
            var callTotal = new Dictionary<int, long>();
            for (var u = 0; u < used.Count; u++)
            {
                var slot = ring[slotByFrame[used[u].Frame]];
                for (var i = 0; i < slot.Count; i++)
                {
                    var key = slot.Keys[i];
                    if (keyProbes[key] != probe)
                        continue;
                    if (!perFrame.TryGetValue(key, out var values))
                        perFrame[key] = values = new double[used.Count];
                    values[u] = slot.Self[i] / (double)used[u].Ticks;
                    selfTotal[key] = selfTotal.GetValueOrDefault(key) + slot.Self[i];
                    callTotal[key] = callTotal.GetValueOrDefault(key) + slot.Calls[i];
                }
            }
            var totalTicks = used.Sum(f => (long)f.Ticks);
            var toMs = 1000d / ticksPerSecond;
            return perFrame
                .Select(pair => Describe(pair.Key, pair.Value, used, selfTotal[pair.Key], callTotal[pair.Key], totalTicks, toMs))
                .OrderByDescending(s => s.MeanMsPerTick).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        }

        private GroupStat Describe(int key, double[] values, List<FrameTicks> used, long self, long calls, long totalTicks, double toMs)
        {
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            var worst = Array.IndexOf(values, sorted[sorted.Length - 1]);
            return new GroupStat
            {
                Key = key,
                Name = names[key],
                MeanMsPerTick = self * toMs / totalTicks,
                P95MsPerTick = Percentile(sorted, 0.95) * toMs,
                WorstMsPerTick = sorted[sorted.Length - 1] * toMs,
                WorstFrame = used[worst].Frame,
                CallsPerTick = calls / (double)totalTicks,
                Frames = used.Count,
            };
        }
    }
}
