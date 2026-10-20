using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace FrameProfilerPlus.Core
{
    public enum ScopeKind
    {
        HarmonyPatch,
        PatchedMethod,
        Coverage,
        Transpiler,
    }

    // Maps scope sites to profiler labels. Pure logic: the label factory and the label count are injected.
    // - One slot per unique key, so Harmony regenerating a replacement reuses the slot.
    // - Labels are created lazily, the first time a scope runs while capturing; patches that never run cost none.
    // - Profiler labels are capped per process, never freed and shared with vanilla, so past the ceiling
    //   new sites share one "<other>" label per kind.
    public sealed class LabelRegistry
    {
        public sealed class Slot
        {
            private readonly HashSet<MethodBase> originals = new HashSet<MethodBase>();

            public ScopeKind Kind { get; }
            public string Key { get; }
            public string LabelText { get; }
            public string Owner { get; }
            public MethodBase Patch { get; }

            internal int labelId = Unassigned;

            internal Slot(ScopeKind kind, string key, string labelText, string owner, MethodBase patch)
            {
                Kind = kind;
                Key = key;
                LabelText = labelText;
                Owner = owner;
                Patch = patch;
            }

            public int LabelId => Volatile.Read(ref labelId);

            public IReadOnlyCollection<MethodBase> Originals
            {
                get
                {
                    lock (originals)
                        return new List<MethodBase>(originals);
                }
            }

            internal void AddOriginal(MethodBase original)
            {
                if (original == null)
                    return;
                lock (originals)
                    originals.Add(original);
            }
        }

        public const int Unassigned = -1;

        private readonly Func<string, ScopeKind, int> createLabel;

        // Set while this thread is inside label creation; a scope entered meanwhile is not timed.
        [ThreadStatic] private static bool assigning;
        private readonly Func<int> labelCount;
        private readonly Func<int> ceiling;
        private readonly int overflowLabelId;

        private readonly object gate = new object();
        private readonly Dictionary<string, int> slotByKey = new Dictionary<string, int>();
        private readonly List<Slot> slots = new List<Slot>();
        private readonly Dictionary<int, List<Slot>> slotsByLabel = new Dictionary<int, List<Slot>>();
        private readonly Dictionary<ScopeKind, int> otherLabels = new Dictionary<ScopeKind, int>();
        private Slot[] snapshot = Array.Empty<Slot>();

        public LabelRegistry(Func<string, ScopeKind, int> createLabel, Func<int> labelCount, Func<int> ceiling, int overflowLabelId)
        {
            this.createLabel = createLabel;
            this.labelCount = labelCount;
            this.ceiling = ceiling;
            this.overflowLabelId = overflowLabelId;
        }

        public int SlotCount => Volatile.Read(ref snapshot).Length;

        public bool CeilingReached { get; private set; }

        public int Register(ScopeKind kind, string key, string labelText, string owner, MethodBase patch, MethodBase original)
        {
            lock (gate)
            {
                if (!slotByKey.TryGetValue(key, out var index))
                {
                    index = slots.Count;
                    slots.Add(new Slot(kind, key, labelText, owner, patch));
                    slotByKey[key] = index;
                    Volatile.Write(ref snapshot, slots.ToArray());
                }
                slots[index].AddOriginal(original);
                return index;
            }
        }

        public Slot Get(int slot) => Volatile.Read(ref snapshot)[slot];

        // Hot path: one array read and one volatile read once the label exists. Returns Unassigned when called
        // re-entrantly from label creation (something the profiler calls to make a label was itself timed).
        public int LabelIdFor(int slot)
        {
            var entry = Volatile.Read(ref snapshot)[slot];
            var id = Volatile.Read(ref entry.labelId);
            if (id != Unassigned)
                return id;
            if (assigning)
                return Unassigned;
            assigning = true;
            try
            {
                return AssignLabel(entry);
            }
            finally
            {
                assigning = false;
            }
        }

        public IReadOnlyList<Slot> SlotsForLabel(int labelId)
        {
            lock (gate)
                return slotsByLabel.TryGetValue(labelId, out var list) ? list.ToArray() : Array.Empty<Slot>();
        }

        private int AssignLabel(Slot entry)
        {
            lock (gate)
            {
                if (entry.labelId != Unassigned)
                    return entry.labelId;

                var id = CeilingReached || labelCount() >= ceiling() ? OtherLabel(entry.Kind) : createLabel(entry.LabelText, entry.Kind);
                if (id == overflowLabelId)
                    CeilingReached = true;

                if (!slotsByLabel.TryGetValue(id, out var list))
                    slotsByLabel[id] = list = new List<Slot>();
                list.Add(entry);
                Volatile.Write(ref entry.labelId, id);
                return id;
            }
        }

        private int OtherLabel(ScopeKind kind)
        {
            CeilingReached = true;
            if (!otherLabels.TryGetValue(kind, out var id))
                otherLabels[kind] = id = createLabel(Prefix(kind) + "<other>", kind);
            return id;
        }

        public static string Prefix(ScopeKind kind) => kind switch
        {
            ScopeKind.HarmonyPatch => "H:",
            ScopeKind.PatchedMethod => "P:",
            ScopeKind.Transpiler => "T:",
            _ => "X:",
        };
    }
}
