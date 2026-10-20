using System;
using Waylith.Profiling;

namespace FrameProfilerPlus.Core
{
    // The scope runtime that generated IL calls in game (see ScopeEmitter for the contract).
    // When the profiler is not capturing, Enter costs one flag check.
    public static class ScopeRuntime
    {
        private static readonly Func<int> LabelCount = LabelCountReader();

        // Zones opened by EnterTracked and not yet closed, on this thread.
        [ThreadStatic] private static int openTracked;

        public static LabelRegistry Registry { get; private set; }

        public static ScopeEmitter Emitter { get; private set; }

        public static void Initialise(Func<int> ceiling)
        {
            Registry = new LabelRegistry(CreateLabel, LabelCount, ceiling, FrameProfiler.OverflowLabelId.Value);
            Emitter = new ScopeEmitter(typeof(ScopeRuntime));
        }

        public static bool Enter(int slot)
        {
            if (!FrameProfiler.IsCapturing)
                return false;
            var id = Registry.LabelIdFor(slot);
            return id != LabelRegistry.Unassigned && FrameProfiler.TryEnterScope(new ProfilerLabelId((ushort)id));
        }

        public static void Leave(bool entered)
        {
            if (entered)
                FrameProfiler.LeaveScope();
        }

        public static bool EnterTracked(int slot)
        {
            var entered = Enter(slot);
            if (entered)
                openTracked++;
            return entered;
        }

        public static void LeaveTracked(bool entered)
        {
            if (!entered)
                return;
            FrameProfiler.LeaveScope();
            openTracked--;
        }

        public static int Mark() => openTracked;

        // LeaveScope closes the innermost open zone. Our tracked zones are the only ones that can be left
        // open inside a wrapped body (vanilla scopes are exception-safe), so closing down to the mark is safe.
        public static void RepairTo(int mark)
        {
            while (openTracked > mark)
            {
                FrameProfiler.LeaveScope();
                openTracked--;
            }
        }

        // Vanilla mutes a discardable label once most of its zones are tiny leaf zones, which keeps cheap,
        // hot patches from costing more to time than to run. Coverage labels are never discardable: the tiny
        // methods the user asked to time would otherwise vanish while the Coverage tab says "patched".
        private static int CreateLabel(string text, ScopeKind kind)
        {
            var id = FrameProfiler.GetOrCreateLabelId(text);
            if (kind != ScopeKind.Coverage)
                FrameProfiler.MarkLabelDiscardable(id);
            return id.Value;
        }

        public static bool LabelCountKnown { get; private set; }

        // The real label count is private. If a future build renames it, report 0: the ceiling then only
        // triggers when the profiler returns its overflow label, and the summary line says so.
        // Read only when a new label is about to be created, so reflection cost does not matter.
        private static Func<int> LabelCountReader()
        {
            var field = HarmonyLib.AccessTools.Field(typeof(FrameProfiler), "labelCount");
            if (field == null || !field.IsStatic || field.FieldType != typeof(int))
                return () => 0;
            LabelCountKnown = true;
            return () => (int)field.GetValue(null);
        }
    }
}
