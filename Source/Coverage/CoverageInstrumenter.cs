using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FrameProfilerPlus.Core;
using HarmonyLib;

namespace FrameProfilerPlus.Coverage
{
    // Times a whole method body as one "X:" scope, including methods the vanilla instrumenter skips
    // (constructors, closures, iterator MoveNext, small methods). A Harmony transpiler, so it also survives
    // other patches. Apply() makes the covered set equal to the one asked for, so a changed target list costs
    // only the difference. One bad method is recorded as failed and never stops the batch.
    //
    // Harmony 3.0.0-rc.3 cannot restore an original body: Unpatch rebuilds a replacement and detours to it.
    // So a method stays "detoured until restart" after it was covered once, and vanilla timing on it records
    // nothing from then on. Callers compiled before a patch also keep inlined copies; see CallerRecompiler.
    public sealed class CoverageInstrumenter
    {
        public const string HarmonyId = FrameProfilerPlusMod.HarmonyId + ".coverage";

        private sealed class Plan
        {
            public ScopeEmitter Emitter;
            public int Slot;
        }

        // Harmony patch methods are static, so the plan per method lives in a static table.
        private static readonly Dictionary<MethodBase, Plan> Plans = new Dictionary<MethodBase, Plan>();
        private static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(CoverageInstrumenter), nameof(Transpiler));

        private readonly LabelRegistry registry;
        private readonly ScopeEmitter emitter;
        private readonly Func<Assembly, string> ownerOf;
        private readonly Func<long> epoch;
        private readonly Action<string> warn;
        private readonly Harmony harmony = new Harmony(HarmonyId);

        private readonly Dictionary<MethodBase, (int slot, long epoch)> covered = new Dictionary<MethodBase, (int, long)>();
        private readonly HashSet<MethodBase> detoured = new HashSet<MethodBase>();
        private readonly Dictionary<MethodBase, string> failed = new Dictionary<MethodBase, string>();

        public CoverageInstrumenter(LabelRegistry registry, ScopeEmitter emitter, Func<Assembly, string> ownerOf, Func<long> epoch, Action<string> warn)
        {
            this.registry = registry;
            this.emitter = emitter;
            this.ownerOf = ownerOf;
            this.epoch = epoch;
            this.warn = warn;
        }

        public sealed class ApplyResult
        {
            public int Added { get; internal set; }
            public int Removed { get; internal set; }
            public List<(MethodBase method, string error)> Failed { get; } = new List<(MethodBase, string)>();
        }

        public IReadOnlyCollection<MethodBase> Covered => covered.Keys.ToList();
        public IReadOnlyCollection<MethodBase> DetouredUntilRestart => detoured.ToList();
        public IReadOnlyDictionary<MethodBase, string> Failed => failed;

        public bool IsCovered(MethodBase method) => covered.ContainsKey(method);

        // Registry slot and the profiler frame count at apply time, for the "never seen" check.
        public bool TryGetSlot(MethodBase method, out int slot, out long appliedAt)
        {
            var found = covered.TryGetValue(method, out var entry);
            slot = found ? entry.slot : LabelRegistry.Unassigned;
            appliedAt = found ? entry.epoch : 0;
            return found;
        }

        public ApplyResult Apply(IReadOnlyCollection<MethodBase> desired)
        {
            var result = new ApplyResult();
            var wanted = new HashSet<MethodBase>(desired);
            foreach (var method in covered.Keys.Where(m => !wanted.Contains(m)).ToList())
            {
                Remove(method);
                result.Removed++;
            }
            failed.Clear();
            foreach (var method in wanted.Where(m => !covered.ContainsKey(m)))
            {
                try
                {
                    Add(method);
                    result.Added++;
                }
                catch (Exception e)
                {
                    failed[method] = e.Message;
                    result.Failed.Add((method, e.Message));
                    warn?.Invoke($"Could not time {method.DeclaringType?.FullName}.{method.Name}: {e.Message}");
                }
            }
            return result;
        }

        public void Clear() => Apply(Array.Empty<MethodBase>());

        private void Add(MethodBase method)
        {
            var slot = registry.Register(ScopeKind.Coverage, CoverageLabels.Key(method), CoverageLabels.Text(method),
                ownerOf(method.DeclaringType?.Assembly), null, method);
            lock (Plans)
                Plans[method] = new Plan { Emitter = emitter, Slot = slot };
            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(TranspilerMethod));
            }
            catch
            {
                lock (Plans)
                    Plans.Remove(method);
                throw;
            }
            covered[method] = (slot, epoch());
            detoured.Add(method);
        }

        private void Remove(MethodBase method)
        {
            harmony.Unpatch(method, TranspilerMethod);
            lock (Plans)
                Plans.Remove(method);
            covered.Remove(method);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            Plan plan;
            lock (Plans)
                Plans.TryGetValue(original, out plan);
            if (plan == null)
                return instructions;
            var returnType = (original as MethodInfo)?.ReturnType ?? typeof(void);
            return plan.Emitter.WrapBody(instructions, returnType, plan.Slot, repair: false, generator);
        }
    }
}
