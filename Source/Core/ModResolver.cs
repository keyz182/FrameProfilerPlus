using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace FrameProfilerPlus.Core
{
    // Names the mod that loaded an assembly. Game assemblies resolve to "Core".
    public static class ModResolver
    {
        private static readonly Dictionary<Assembly, string> Cache = new Dictionary<Assembly, string>();
        private static readonly Assembly GameAssembly = typeof(Pawn).Assembly;

        public static string ModName(Assembly assembly)
        {
            if (assembly == null)
                return "?";
            lock (Cache)
            {
                if (Cache.TryGetValue(assembly, out var name))
                    return name;
                name = Resolve(assembly);
                // Mods load their assemblies before patching, so a miss is only cached once mods are listed.
                if (LoadedModManager.RunningModsListForReading.Count > 0)
                    Cache[assembly] = name;
                return name;
            }
        }

        private static string Resolve(Assembly assembly)
        {
            if (assembly == GameAssembly)
                return "Core";
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies?.loadedAssemblies.Contains(assembly) == true)
                    return mod.Name;
            }
            return assembly.GetName().Name;
        }
    }
}
