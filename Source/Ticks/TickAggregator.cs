using System;
using System.Collections.Generic;
using System.Linq;

namespace FrameProfilerPlus.Ticks
{
    // Per-frame tick time by group, for the Groups tab. Main thread only; callers gate on capture and keep the
    // start timestamp themselves. Memory is bounded: a ring of frames, each holding only the groups it touched,
    // plus a global key table capped at maxKeys (groups past it fold into one "<other>" key per probe).
    public sealed partial class TickAggregator
    {
        public const int MaxDepth = 64;
        public const string OtherName = "<other>";

        private static readonly int ProbeCount = Enum.GetValues(typeof(TickProbe)).Length;

        private sealed class Slot
        {
            public int Frame = -1;
            public int Count;
            public int[] Keys = new int[64];
            public long[] Self = new long[64];
            public int[] Calls = new int[64];
            public readonly Individual[] Top;
            public readonly int[] TopCount = new int[ProbeCount];
            public readonly long[] TopMin = new long[ProbeCount];

            public Slot(int topK) => Top = new Individual[topK * ProbeCount];

            public void Reset(int frame)
            {
                Frame = frame;
                Count = 0;
                Array.Clear(TopCount, 0, TopCount.Length);
                Array.Fill(TopMin, -1L);
            }
        }

        private readonly int maxKeys;
        private readonly int topK;
        private readonly List<string> names = new List<string>();
        private readonly List<TickProbe> keyProbes = new List<TickProbe>();
        private readonly Dictionary<(TickProbe, string), int> interned = new Dictionary<(TickProbe, string), int>();
        private readonly Slot[] ring;
        private readonly Dictionary<int, int> slotByFrame = new Dictionary<int, int>();
        private readonly int[] position;
        private readonly long[][] stacks = new long[ProbeCount][];
        private readonly int[] depth = new int[ProbeCount];
        private int nextSlot;
        private int currentFrame = int.MinValue;
        private Slot current;

        // Exits with nothing entered, entries dropped at a frame change, and entries refused past MaxDepth.
        public long Unbalanced { get; private set; }

        public long DepthOverflows { get; private set; }

        public TickAggregator(int historyFrames, int maxKeys = 4096, int topK = 16)
        {
            if (historyFrames < 1 || maxKeys < 1 || topK < 1)
                throw new ArgumentOutOfRangeException(nameof(historyFrames), "History, key and top counts must be positive.");
            this.maxKeys = maxKeys;
            this.topK = topK;
            ring = new Slot[historyFrames];
            for (var i = 0; i < ring.Length; i++)
                ring[i] = new Slot(topK);
            for (var p = 0; p < ProbeCount; p++)
            {
                names.Add(OtherName);
                keyProbes.Add((TickProbe)p);
                stacks[p] = new long[MaxDepth];
            }
            position = new int[maxKeys + ProbeCount];
            Array.Fill(position, -1);
        }

        public string NameOf(int key) => names[key];

        public int Intern(TickProbe probe, string name)
        {
            if (interned.TryGetValue((probe, name), out var key))
                return key;
            if (names.Count - ProbeCount >= maxKeys)
                return (int)probe;
            key = names.Count;
            names.Add(name);
            keyProbes.Add(probe);
            interned[(probe, name)] = key;
            return key;
        }

        public bool HasFrame(int frame) => slotByFrame.ContainsKey(frame);

        // Starts timing one call. False means the nesting limit was hit: do not call Exit for it.
        public bool Enter(TickProbe probe, int frame)
        {
            if (frame != currentFrame)
                StartFrame(frame);
            var p = (int)probe;
            var d = depth[p];
            if (d >= MaxDepth)
            {
                DepthOverflows++;
                return false;
            }
            stacks[p][d] = 0;
            depth[p] = d + 1;
            return true;
        }

        // Ends the innermost call of the probe: self time is its elapsed time minus its nested calls.
        public void Exit(TickProbe probe, int key, long startTicks, long nowTicks, long individualId)
        {
            var p = (int)probe;
            var d = depth[p];
            if (d == 0)
            {
                Unbalanced++;
                return;
            }
            var elapsed = nowTicks - startTicks;
            var child = stacks[p][--d];
            depth[p] = d;
            if (d > 0)
                stacks[p][d - 1] += elapsed;
            Record(p, key, Math.Max(0, elapsed - child), individualId);
        }

        public List<Individual> TopIndividuals(int frame, TickProbe probe)
        {
            if (!slotByFrame.TryGetValue(frame, out var index))
                return new List<Individual>();
            var slot = ring[index];
            var p = (int)probe;
            return slot.Top.Skip(p * topK).Take(slot.TopCount[p]).OrderByDescending(i => i.SelfTicks).ToList();
        }

        private void StartFrame(int frame)
        {
            if (current != null)
            {
                for (var i = 0; i < current.Count; i++)
                    position[current.Keys[i]] = -1;
            }
            for (var p = 0; p < ProbeCount; p++)
            {
                Unbalanced += depth[p];
                depth[p] = 0;
            }
            var index = nextSlot;
            nextSlot = (nextSlot + 1) % ring.Length;
            var slot = ring[index];
            if (slot.Frame >= 0 && slotByFrame.TryGetValue(slot.Frame, out var held) && held == index)
                slotByFrame.Remove(slot.Frame);
            slot.Reset(frame);
            slotByFrame[frame] = index;
            current = slot;
            currentFrame = frame;
        }

        private void Record(int p, int key, long self, long id)
        {
            var slot = current;
            var at = position[key];
            if (at < 0)
            {
                if (slot.Count == slot.Keys.Length)
                    Grow(slot);
                at = slot.Count++;
                slot.Keys[at] = key;
                slot.Self[at] = 0;
                slot.Calls[at] = 0;
                position[key] = at;
            }
            slot.Self[at] += self;
            slot.Calls[at]++;
            RecordIndividual(slot, p, new Individual(key, id, self));
        }

        private void RecordIndividual(Slot slot, int p, Individual call)
        {
            if (call.SelfTicks <= slot.TopMin[p])
                return;
            var segment = p * topK;
            if (slot.TopCount[p] < topK)
                slot.Top[segment + slot.TopCount[p]++] = call;
            else
                slot.Top[segment + IndexOfSmallest(slot, segment)] = call;
            slot.TopMin[p] = slot.TopCount[p] < topK ? -1L : slot.Top[segment + IndexOfSmallest(slot, segment)].SelfTicks;
        }

        private int IndexOfSmallest(Slot slot, int segment)
        {
            var smallest = 0;
            for (var i = 1; i < topK; i++)
            {
                if (slot.Top[segment + i].SelfTicks < slot.Top[segment + smallest].SelfTicks)
                    smallest = i;
            }
            return smallest;
        }

        private static void Grow(Slot slot)
        {
            var size = slot.Keys.Length * 2;
            Array.Resize(ref slot.Keys, size);
            Array.Resize(ref slot.Self, size);
            Array.Resize(ref slot.Calls, size);
        }
    }
}
