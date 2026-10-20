using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Verse;
using Waylith.Profiling;

namespace FrameProfilerPlus.Ticks
{
    // Installs the tick probes and owns the shared aggregator. A probe times a call only while the profiler is
    // capturing and only on the main thread. The prefix tells the finalizer through its state whether it
    // entered, so a capture change between the two cannot unbalance the nesting stack.
    public static class TickProbes
    {
        // About 20 s of frames at 60 fps; each frame holds only the groups it touched.
        public const int HistoryFrames = 1200;

        public static TickAggregator Aggregator { get; private set; }

        public static void Install(Harmony harmony)
        {
            if (Aggregator != null)
                return;
            Aggregator = new TickAggregator(HistoryFrames);
            // A finalizer, not a postfix: TickList catches per-thing exceptions, and a skipped postfix would leave the stack open.
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)),
                prefix: new HarmonyMethod(typeof(ThingProbe), nameof(ThingProbe.Prefix)),
                finalizer: new HarmonyMethod(typeof(ThingProbe), nameof(ThingProbe.Finalizer)));
        }
    }

    // Group = the thing's ThingDef; the individual kept for the slowest calls is its thingIDNumber.
    public static class ThingProbe
    {
        private static readonly Dictionary<ThingDef, int> Keys = new Dictionary<ThingDef, int>();

        public static void Prefix(out long __state)
        {
            __state = 0;
            if (!FrameProfiler.IsCapturing || !UnityData.IsInMainThread)
                return;
            if (TickProbes.Aggregator.Enter(TickProbe.Thing, Waylith.Time.FrameCount))
                __state = Stopwatch.GetTimestamp();
        }

        public static void Finalizer(long __state, Thing __instance)
        {
            if (__state == 0)
                return;
            var now = Stopwatch.GetTimestamp();
            TickProbes.Aggregator.Exit(TickProbe.Thing, KeyOf(__instance.def), __state, now, __instance.thingIDNumber);
        }

        private static int KeyOf(ThingDef def)
        {
            if (!Keys.TryGetValue(def, out var key))
                Keys[def] = key = TickProbes.Aggregator.Intern(TickProbe.Thing, def.defName);
            return key;
        }
    }
}
