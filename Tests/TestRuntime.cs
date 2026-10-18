using System;
using System.Collections.Generic;
using System.Reflection;
using FrameProfilerPlus.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FrameProfilerPlus.Tests
{
    // Counting scope runtime with the ScopeEmitter contract. Depth is the number of open zones: every test
    // asserts it returns to zero, which is what keeps the real profiler's tree correctly nested.
    public static class TestRuntime
    {
        [ThreadStatic] private static int tracked;

        public static int Depth;
        public static readonly List<int> EnteredSlots = new List<int>();

        public static bool Enter(int slot)
        {
            Depth++;
            EnteredSlots.Add(slot);
            return true;
        }

        public static void Leave(bool entered)
        {
            if (entered)
                Depth--;
        }

        public static bool EnterTracked(int slot)
        {
            var entered = Enter(slot);
            tracked++;
            return entered;
        }

        public static void LeaveTracked(bool entered)
        {
            if (!entered)
                return;
            Depth--;
            tracked--;
        }

        public static int Mark() => tracked;

        public static void RepairTo(int mark)
        {
            while (tracked > mark)
            {
                Depth--;
                tracked--;
            }
        }

        public static void Reset()
        {
            Depth = 0;
            tracked = 0;
            EnteredSlots.Clear();
        }

        public static LabelRegistry NewRegistry()
        {
            var next = 0;
            return new LabelRegistry(_ => next++, () => next, () => int.MaxValue, 65535);
        }

        public static IEnumerable<string> EnteredLabels(LabelRegistry registry)
        {
            foreach (var slot in EnteredSlots)
                yield return registry.Get(slot).LabelText;
        }

        public static MethodInfo Method(Type type, string name) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }
}
