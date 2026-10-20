using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;
using System.Reflection;

namespace FrameProfilerPlus.Coverage
{
    public enum SkipReason
    {
        NoBody,
        OpenGeneric,
        AlreadyCovered,
        TimedByVanilla,
        ForeignPatch,
    }

    // What the caller knows about methods that selection cannot see from reflection alone.
    public sealed class SkipPolicy
    {
        public Func<MethodBase, bool> AlreadyCovered { get; set; } = _ => false;
        public Func<MethodBase, bool> TimedByVanilla { get; set; } = _ => false;
        // A method with another mod's Harmony patch already has a whole-method P: scope.
        public Func<MethodBase, bool> HasForeignPatch { get; set; } = _ => false;
        public int MaxMethods { get; set; } = 2000;
    }

    public sealed class Selection
    {
        public List<MethodBase> Methods { get; } = new List<MethodBase>();
        public Dictionary<SkipReason, int> Skipped { get; } = new Dictionary<SkipReason, int>();
        // Assemblies a target named but that must never be timed (the scope runtime depends on them).
        public List<string> RefusedAssemblies { get; } = new List<string>();
        public int Matched { get; internal set; }
        public bool OverCap { get; internal set; }

        internal void Skip(SkipReason reason) => Skipped[reason] = (Skipped.TryGetValue(reason, out var n) ? n : 0) + 1;
    }

    // Chooses the methods that a set of target lines selects. Pure reflection; no game types.
    public static class MethodSelector
    {
        private static readonly string[] RefusedAssemblyPatterns =
        {
            "System", "System.*", "mscorlib", "netstandard", "Microsoft.*", "Waylith*", "0Harmony", "MonoMod.*", "Mono.Cecil*", "FrameProfilerPlus",
        };

        // The scope runtime takes locks and creates labels through these; timing them would recurse into itself.
        public static bool IsRefusedAssembly(string assemblyName) =>
            RefusedAssemblyPatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, assemblyName));

        public static Selection Select(IEnumerable<Assembly> assemblies, IReadOnlyList<CoverageTarget> targets, SkipPolicy policy)
        {
            var selection = new Selection();
            var includes = targets.Where(t => !t.Exclude).ToList();
            var excludes = targets.Where(t => t.Exclude).ToList();
            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic || assembly.IsCollectible)
                    continue;
                var name = assembly.GetName().Name;
                var forAssembly = includes.Where(t => t.MatchesAssembly(name)).ToList();
                if (forAssembly.Count == 0)
                    continue;
                if (IsRefusedAssembly(name))
                {
                    selection.RefusedAssemblies.Add(name);
                    continue;
                }
                var excludedHere = excludes.Where(t => t.MatchesAssembly(name)).ToList();
                SelectFrom(assembly, forAssembly, excludedHere, policy, selection);
            }
            if (selection.Methods.Count > policy.MaxMethods)
            {
                selection.OverCap = true;
                selection.Methods.Clear();
            }
            return selection;
        }

        private static void SelectFrom(Assembly assembly, List<CoverageTarget> includes, List<CoverageTarget> excludes, SkipPolicy policy, Selection selection)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                foreach (var method in ILScan.AllBodies(type))
                {
                    if (method.IsConstructor && method.IsStatic)
                        continue;
                    if (!includes.Any(t => t.Matches(method)) || excludes.Any(t => t.Matches(method)))
                        continue;
                    selection.Matched++;
                    var reason = SkipFor(method, policy);
                    if (reason == null)
                        selection.Methods.Add(method);
                    else
                        selection.Skip(reason.Value);
                }
            }
        }

        private static SkipReason? SkipFor(MethodBase method, SkipPolicy policy)
        {
            if (method.ContainsGenericParameters || (method.DeclaringType?.ContainsGenericParameters ?? false))
                return SkipReason.OpenGeneric;
            if (method.IsAbstract || (method.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0
                || (method.Attributes & MethodAttributes.PinvokeImpl) != 0 || ILScan.BodySize(method) == 0)
                return SkipReason.NoBody;
            if (policy.AlreadyCovered(method))
                return SkipReason.AlreadyCovered;
            if (policy.TimedByVanilla(method))
                return SkipReason.TimedByVanilla;
            if (policy.HasForeignPatch(method))
                return SkipReason.ForeignPatch;
            return null;
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null).ToList();
            }
        }
    }
}
