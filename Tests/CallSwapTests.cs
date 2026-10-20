using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameProfilerPlus.Ticks;
using HarmonyLib;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class CallSwapTests
    {
        public class Worker
        {
            public virtual void Work(int amount) => Log.Add("base" + amount);
            public virtual void Idle() => Log.Add("idle");
        }

        public class SpecialWorker : Worker
        {
            public override void Work(int amount) => Log.Add("special" + amount);
        }

        public static class Log
        {
            public static readonly List<string> Entries = new List<string>();
            public static void Add(string entry) => Entries.Add(entry);
        }

        public static class Probe
        {
            public static void Work(Worker worker, int amount)
            {
                Log.Add("before");
                try
                {
                    worker.Work(amount);
                }
                finally
                {
                    Log.Add("after");
                }
            }
        }

        public static class Targets
        {
            public static void RunTwice(Worker worker)
            {
                worker.Work(1);
                worker.Idle();
                worker.Work(2);
            }
        }

        public static int Replaced;

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> codes)
        {
            var swap = new CallSwap(new Dictionary<MethodInfo, MethodInfo> { [typeof(Worker).GetMethod("Work")] = typeof(Probe).GetMethod("Work") });
            var result = swap.Apply(codes).ToList();
            Replaced = swap.Count;
            return result;
        }

        [Fact]
        public void Apply_RoutesTheChosenCallsThroughTheReplacement_AndLeavesOthersAlone()
        {
            new Harmony("keyz.tests.callswap").Patch(typeof(Targets).GetMethod("RunTwice"),
                transpiler: new HarmonyMethod(typeof(CallSwapTests), nameof(Transpile)));
            Log.Entries.Clear();

            Targets.RunTwice(new SpecialWorker());

            Assert.Equal(2, Replaced);
            Assert.Equal(new[] { "before", "special1", "after", "idle", "before", "special2", "after" }, Log.Entries);
        }
    }
}
