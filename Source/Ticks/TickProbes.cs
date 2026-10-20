using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Verse;
using Waylith.Profiling;

namespace FrameProfilerPlus.Ticks
{
    // Installs the tick probes and owns the shared aggregator. A probe times a call only while the profiler is
    // capturing and only on the main thread. Begin returns 0 when it did not enter, and End ignores 0, so a
    // capture change between the two cannot unbalance the nesting stack.
    public static class TickProbes
    {
        // About 20 s of frames at 60 fps; each frame holds only the groups it touched.
        public const int HistoryFrames = 1200;

        private static readonly Dictionary<Type, int>[] TypeKeys = CreateTypeKeys();
        private static readonly Dictionary<string, Type> TypesByName = new Dictionary<string, Type>();
        private static readonly List<TickProbe> EnabledProbes = new List<TickProbe>();

        public static TickAggregator Aggregator { get; private set; }

        public static IReadOnlyList<TickProbe> Enabled => EnabledProbes;

        public static void Install(Harmony harmony, bool drivers, bool comps)
        {
            if (Aggregator != null)
                return;
            Aggregator = new TickAggregator(HistoryFrames);
            EnabledProbes.Add(TickProbe.Thing);
            // A finalizer, not a postfix: TickList catches per-thing exceptions, and a skipped postfix would leave the stack open.
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)),
                prefix: new HarmonyMethod(typeof(ThingProbe), nameof(ThingProbe.Prefix)),
                finalizer: new HarmonyMethod(typeof(ThingProbe), nameof(ThingProbe.Finalizer)));
            if (drivers)
            {
                DriverProbe.Install(harmony);
                EnabledProbes.Add(TickProbe.Driver);
            }
            if (comps && ComponentProbe.Install(harmony))
                EnabledProbes.Add(TickProbe.Comp);
        }

        public static long Begin(TickProbe probe)
        {
            if (!FrameProfiler.IsCapturing || !UnityData.IsInMainThread)
                return 0;
            return Aggregator.Enter(probe, Waylith.Time.FrameCount) ? Stopwatch.GetTimestamp() : 0;
        }

        public static void End(TickProbe probe, int key, long state, long individualId) =>
            Aggregator.Exit(probe, key, state, Stopwatch.GetTimestamp(), individualId);

        // Groups of the driver and comp probes are named by type; the type is kept to find its mod later.
        public static int KeyOf(TickProbe probe, Type type)
        {
            var keys = TypeKeys[(int)probe];
            if (!keys.TryGetValue(type, out var key))
            {
                var name = type.FullName ?? type.Name;
                keys[type] = key = Aggregator.Intern(probe, name);
                TypesByName[name] = type;
            }
            return key;
        }

        public static Type TypeNamed(string name) => TypesByName.TryGetValue(name, out var type) ? type : null;

        private static Dictionary<Type, int>[] CreateTypeKeys()
        {
            var keys = new Dictionary<Type, int>[Enum.GetValues(typeof(TickProbe)).Length];
            for (var i = 0; i < keys.Length; i++)
                keys[i] = new Dictionary<Type, int>();
            return keys;
        }
    }

    // Group = the thing's ThingDef; the individual kept for the slowest calls is its thingIDNumber.
    public static class ThingProbe
    {
        private static readonly Dictionary<ThingDef, int> Keys = new Dictionary<ThingDef, int>();

        public static void Prefix(out long __state) => __state = TickProbes.Begin(TickProbe.Thing);

        public static void Finalizer(long __state, Thing __instance)
        {
            if (__state != 0)
                TickProbes.End(TickProbe.Thing, KeyOf(__instance.def), __state, __instance.thingIDNumber);
        }

        private static int KeyOf(ThingDef def)
        {
            if (!Keys.TryGetValue(def, out var key))
                Keys[def] = key = TickProbes.Aggregator.Intern(TickProbe.Thing, def.defName);
            return key;
        }
    }
}
