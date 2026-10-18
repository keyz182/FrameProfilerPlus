using System.Collections.Generic;
using System.Threading;
using Waylith.Profiling;

namespace FrameProfilerPlus.Attribution
{
    // Runtime side of the patch-call hook. Generated IL calls Enter(slot) before a Harmony patch
    // method and LeaveScope after it when Enter returned true.
    public static class PatchScopes
    {
        private sealed class Slot
        {
            public readonly string label;
            public int labelId = -1;

            public Slot(string label) => this.label = label;
        }

        private static readonly object Gate = new object();
        private static readonly List<Slot> Slots = new List<Slot>();
        private static Slot[] snapshot = new Slot[0];
        private static int otherLabelId = -1;
        private static int highestLabelId;

        public static int SlotCount => Volatile.Read(ref snapshot).Length;

        // Called at patch time. Costs no profiler label until the scope first runs while capturing.
        public static int Register(string label)
        {
            lock (Gate)
            {
                Slots.Add(new Slot(label));
                Volatile.Write(ref snapshot, Slots.ToArray());
                return Slots.Count - 1;
            }
        }

        public static bool Enter(int slot)
        {
            if (!FrameProfiler.IsCapturing)
                return false;
            return FrameProfiler.TryEnterScope(new ProfilerLabelId((ushort)LabelIdFor(slot)));
        }

        private static int LabelIdFor(int slot)
        {
            var entry = Volatile.Read(ref snapshot)[slot];
            var id = Volatile.Read(ref entry.labelId);
            if (id >= 0)
                return id;

            // Labels are capped per process and shared with vanilla; past the ceiling, share one label.
            if (Volatile.Read(ref highestLabelId) >= FrameProfilerPlusMod.Settings.labelCeiling)
                id = OtherLabelId();
            else
                id = FrameProfiler.GetOrCreateLabelId(entry.label).Value;

            InterlockedMax(ref highestLabelId, id);
            Volatile.Write(ref entry.labelId, id);
            return id;
        }

        private static int OtherLabelId()
        {
            var id = Volatile.Read(ref otherLabelId);
            if (id < 0)
            {
                id = FrameProfiler.GetOrCreateLabelId("H:<other>").Value;
                Volatile.Write(ref otherLabelId, id);
            }
            return id;
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value)
            {
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                    return;
            }
        }
    }
}
