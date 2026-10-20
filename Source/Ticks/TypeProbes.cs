using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace FrameProfilerPlus.Ticks
{
    // Group = the job driver's type; the individual is its pawn's thingIDNumber.
    public static class DriverProbe
    {
        public static void Install(Harmony harmony)
        {
            foreach (var name in new[] { nameof(JobDriver.DriverTick), nameof(JobDriver.DriverTickInterval) })
            {
                harmony.Patch(AccessTools.Method(typeof(JobDriver), name),
                    prefix: new HarmonyMethod(typeof(DriverProbe), nameof(Prefix)),
                    finalizer: new HarmonyMethod(typeof(DriverProbe), nameof(Finalizer)));
            }
        }

        public static void Prefix(out long __state) => __state = TickProbes.Begin(TickProbe.Driver);

        public static void Finalizer(long __state, JobDriver __instance)
        {
            if (__state != 0)
                TickProbes.End(TickProbe.Driver, TickProbes.KeyOf(TickProbe.Driver, __instance.GetType()), __state, __instance.pawn?.thingIDNumber ?? 0);
        }
    }

    // Group = the comp's type; the individual is the thing that owns it. ThingWithComps calls each comp's tick
    // method at four sites; the transpiler sends those calls through the timing wrappers below.
    public static class ComponentProbe
    {
        private const int ExpectedSites = 4;
        private static readonly string[] TickNames = { "CompTick", "CompTickInterval", "CompTickRare", "CompTickLong" };

        private static int replaced;

        // False when the call sites were not found, so the tab never shows a probe that records nothing.
        public static bool Install(Harmony harmony)
        {
            foreach (var name in new[] { "Tick", "TickInterval", "TickRare", "TickLong" })
                harmony.Patch(AccessTools.Method(typeof(ThingWithComps), name), transpiler: new HarmonyMethod(typeof(ComponentProbe), nameof(Transpiler)));
            if (replaced == ExpectedSites)
                return true;
            FrameProfilerPlusMod.Warn($"Comp tick call sites: found {replaced} of {ExpectedSites} (another mod may have changed them); comp timing is incomplete.");
            return replaced > 0;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
        {
            var map = new Dictionary<MethodInfo, MethodInfo>();
            foreach (var name in TickNames)
                map[AccessTools.Method(typeof(ThingComp), name)] = AccessTools.Method(typeof(ComponentProbe), name);
            var swap = new CallSwap(map);
            var result = new List<CodeInstruction>(swap.Apply(codes));
            replaced += swap.Count;
            return result;
        }

        public static void CompTick(ThingComp comp)
        {
            var state = TickProbes.Begin(TickProbe.Comp);
            try
            {
                comp.CompTick();
            }
            finally
            {
                End(state, comp);
            }
        }

        public static void CompTickInterval(ThingComp comp, int delta)
        {
            var state = TickProbes.Begin(TickProbe.Comp);
            try
            {
                comp.CompTickInterval(delta);
            }
            finally
            {
                End(state, comp);
            }
        }

        public static void CompTickRare(ThingComp comp)
        {
            var state = TickProbes.Begin(TickProbe.Comp);
            try
            {
                comp.CompTickRare();
            }
            finally
            {
                End(state, comp);
            }
        }

        public static void CompTickLong(ThingComp comp)
        {
            var state = TickProbes.Begin(TickProbe.Comp);
            try
            {
                comp.CompTickLong();
            }
            finally
            {
                End(state, comp);
            }
        }

        private static void End(long state, ThingComp comp)
        {
            if (state != 0)
                TickProbes.End(TickProbe.Comp, TickProbes.KeyOf(TickProbe.Comp, comp.GetType()), state, comp.parent?.thingIDNumber ?? 0);
        }
    }
}
