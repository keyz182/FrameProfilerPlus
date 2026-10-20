using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace FrameProfilerPlus.TestMod
{
    // Known patches and workloads for in-game checks of Frame Profiler Plus. Development only.
    public class TestModMain : Mod
    {
        public TestModMain(ModContentPack content) : base(content)
        {
            new Harmony("keyz.frameprofilerplus.testmod").PatchAll(typeof(TestModMain).Assembly);
            Log.Message("[FrameProfilerPlus.TestMod] Loaded.");
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class DoSingleTickPatches
    {
        public static bool Prefix()
        {
            Workload.Run();
            return true;
        }

        public static void Postfix() => Workload.Spin(2000);
    }

    // Passthrough postfix: the original's result is on the stack around the patch call.
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickRateMultiplier), MethodType.Getter)]
    public static class TickRatePassthrough
    {
        public static float Postfix(float result)
        {
            Workload.Spin(500);
            return result;
        }
    }

    // Set ThrowOnce from the REPL to check that an exception in a patch does not leave a scope open.
    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    public static class MapUpdateThrow
    {
        public static bool ThrowOnce;

        public static void Prefix()
        {
            if (!ThrowOnce)
                return;
            ThrowOnce = false;
            throw new InvalidOperationException("[FrameProfilerPlus.TestMod] deliberate prefix exception");
        }
    }

    // Transpiler attribution check: adds a deliberately slow call at the start of Map.MapUpdate.
    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    public static class MapUpdateTranspiler
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MapUpdateTranspiler), nameof(SlowCall)));
            foreach (var instruction in instructions)
                yield return instruction;
        }

        public static void SlowCall() => Workload.Spin(60000);
    }

    // Spike S5: small methods with no inlining attributes, driven every frame so their callers are
    // compiled (and inline them) before any coverage patch exists.
    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    public static class InlineSpikeDriver
    {
        public static void Postfix() => InlineSpike.Drive();
    }

    // Identity transpiler: re-patching a caller makes Harmony rebuild it, dropping inlined copies of its callees.
    public static class Recompile
    {
        public static IEnumerable<HarmonyLib.CodeInstruction> Identity(IEnumerable<HarmonyLib.CodeInstruction> instructions) => instructions;
    }

    public static class InlineSpike
    {
        public static int Sink;
        public static bool Enabled = true;

        public static int Tiny(int x) => Sink += x;

        public static int Mid(int x)
        {
            var a = x * 3 + Sink;
            if (a > 1000000)
                a -= 999983;
            for (var i = 0; i < 4; i++)
                a ^= (a << 3) + i;
            Sink += a & 7;
            return a;
        }

        public static int Middle(int x) => Tiny(x + 1);

        public static int Outer(int x) => Middle(x) + 1;

        public static void Direct()
        {
            for (var i = 0; i < 20; i++)
                Tiny(i);
            for (var i = 0; i < 20; i++)
                Mid(i);
        }

        public static void Chain()
        {
            for (var i = 0; i < 20; i++)
                Outer(i);
        }

        public static void Drive()
        {
            if (!Enabled)
                return;
            Direct();
            Chain();
        }
    }

    // Coverage targets the vanilla instrumenter skips: a constructor, a closure and an iterator.
    public static class Workload
    {
        public static int Sink;
        private static readonly List<int> Items = Enumerable.Range(0, 256).ToList();

        // Plain unchecked loops: Enumerable.Sum is checked and Sink wraps on purpose.
        public static void Run()
        {
            var total = new HeavyCtor(4000).Value;
            foreach (var n in Numbers(256))
                total += n;
            var factor = (Sink & 3) + 1;
            foreach (var x in Items.Select(x => x * factor))
                total += x;
            Sink = total;
        }

        public static IEnumerable<int> Numbers(int count)
        {
            for (var i = 0; i < count; i++)
                yield return i ^ Sink;
        }

        public static void Spin(int iterations)
        {
            for (var i = 0; i < iterations; i++)
                Sink = Sink * 31 + i;
        }
    }

    public sealed class HeavyCtor
    {
        public readonly int Value;

        public HeavyCtor(int iterations)
        {
            for (var i = 0; i < iterations; i++)
                Value += i * i;
        }
    }
}
