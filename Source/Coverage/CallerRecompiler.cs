using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace FrameProfilerPlus.Coverage
{
    // The JIT inlines small methods into their callers, and into the callers' callers, so a patch on the
    // inlined method is never reached from code compiled earlier (spike S5: Gen.IsHashIntervalTick stayed
    // unseen until 64 transitive callers were re-patched). Re-patching a caller with an identity transpiler
    // makes Harmony rebuild it, and the rebuilt body calls the patched method.
    //
    // For a target, this walks its callers upward. A caller small enough to be inlined itself is expanded in
    // turn; a larger one is recompiled but not expanded. Callers with other mods' Harmony patches or vanilla
    // timing are left alone: re-running foreign transpilers is risky, and a patch drops vanilla timing.
    // Patches are reference-counted, so two targets sharing a caller never undo each other.
    public sealed class CallerRecompiler
    {
        public const string HarmonyId = FrameProfilerPlusMod.HarmonyId + ".recompile";
        public const int DefaultMaxCallers = 64;
        public const int DefaultMaxDepth = 3;
        // Callers up to this many IL bytes may be inlined into their own callers, so the walk continues past them.
        public const int InlineCandidateBytes = 100;

        private static readonly MethodInfo IdentityMethod = AccessTools.Method(typeof(CallerRecompiler), nameof(Identity));

        private readonly Func<IEnumerable<Assembly>> assemblies;
        private readonly Action<string> warn;
        private readonly Harmony harmony = new Harmony(HarmonyId);

        private readonly Dictionary<Assembly, CallerIndex> indexes = new Dictionary<Assembly, CallerIndex>();
        private readonly Dictionary<MethodBase, int> references = new Dictionary<MethodBase, int>();
        private readonly Dictionary<MethodBase, List<MethodBase>> byTarget = new Dictionary<MethodBase, List<MethodBase>>();

        public CallerRecompiler(Func<IEnumerable<Assembly>> assemblies, Func<MethodBase, bool> leaveAlone, Action<string> warn)
        {
            this.assemblies = assemblies;
            LeaveAlone = leaveAlone;
            this.warn = warn;
        }

        // Callers this returns true for are never re-patched. Replaceable, since the answer changes as mods patch.
        public Func<MethodBase, bool> LeaveAlone { get; set; }

        public int MaxCallers { get; set; } = DefaultMaxCallers;
        public int MaxDepth { get; set; } = DefaultMaxDepth;

        public int PatchedCallerCount => references.Count;

        public static IEnumerable<CodeInstruction> Identity(IEnumerable<CodeInstruction> instructions) => instructions;

        public IReadOnlyList<MethodBase> CallersRecompiledFor(MethodBase target) =>
            byTarget.TryGetValue(target, out var list) ? list : (IReadOnlyList<MethodBase>)Array.Empty<MethodBase>();

        // Callers that would be re-patched for the target; touches nothing.
        public List<MethodBase> Plan(MethodBase target)
        {
            var chosen = new List<MethodBase>();
            var seen = new HashSet<MethodBase> { target };
            var frontier = new List<MethodBase> { target };
            for (var depth = 0; depth < MaxDepth && frontier.Count > 0 && chosen.Count < MaxCallers; depth++)
            {
                var next = new List<MethodBase>();
                foreach (var method in frontier)
                {
                    foreach (var caller in CallersOf(method))
                    {
                        if (chosen.Count >= MaxCallers)
                            break;
                        if (!seen.Add(caller) || !CanRecompile(caller))
                            continue;
                        chosen.Add(caller);
                        if (ILScan.BodySize(caller) <= InlineCandidateBytes)
                            next.Add(caller);
                    }
                }
                frontier = next;
            }
            return chosen;
        }

        // Re-patches the planned callers and returns how many it holds for the target.
        public int Recompile(MethodBase target)
        {
            if (byTarget.TryGetValue(target, out var existing))
                return existing.Count;
            var done = new List<MethodBase>();
            foreach (var caller in Plan(target))
            {
                if (!AddReference(caller))
                    continue;
                done.Add(caller);
            }
            byTarget[target] = done;
            return done.Count;
        }

        public void Release(MethodBase target)
        {
            if (!byTarget.TryGetValue(target, out var callers))
                return;
            byTarget.Remove(target);
            foreach (var caller in callers)
            {
                if (--references[caller] > 0)
                    continue;
                references.Remove(caller);
                harmony.Unpatch(caller, IdentityMethod);
            }
        }

        public void ReleaseAll()
        {
            foreach (var target in byTarget.Keys.ToList())
                Release(target);
        }

        private bool AddReference(MethodBase caller)
        {
            if (references.TryGetValue(caller, out var count))
            {
                references[caller] = count + 1;
                return true;
            }
            try
            {
                harmony.Patch(caller, transpiler: new HarmonyMethod(IdentityMethod));
            }
            catch (Exception e)
            {
                warn?.Invoke($"Could not recompile {caller.DeclaringType?.FullName}.{caller.Name}: {e.Message}");
                return false;
            }
            references[caller] = 1;
            return true;
        }

        private bool CanRecompile(MethodBase method) =>
            !method.ContainsGenericParameters && !(method.DeclaringType?.ContainsGenericParameters ?? false)
            && !method.IsAbstract && ILScan.BodySize(method) > 0
            && !MethodSelector.IsRefusedAssembly(method.DeclaringType?.Assembly.GetName().Name ?? "")
            && !LeaveAlone(method);

        // Callers can live in any assembly that references the callee's (or in that assembly itself).
        private IEnumerable<MethodBase> CallersOf(MethodBase callee)
        {
            var home = callee.DeclaringType?.Assembly;
            if (home == null)
                yield break;
            var homeName = home.GetName().Name;
            foreach (var assembly in assemblies())
            {
                if (assembly != home && !assembly.GetReferencedAssemblies().Any(r => r.Name == homeName))
                    continue;
                if (!indexes.TryGetValue(assembly, out var index))
                    indexes[assembly] = index = CallerIndex.Build(assembly);
                foreach (var caller in index.CallersOf(callee))
                    yield return caller;
            }
        }
    }
}
