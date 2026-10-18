using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using FrameProfilerPlus.Core;
using HarmonyLib;

namespace FrameProfilerPlus.Coverage
{
    // Times a whole method body as one "X:" scope, including methods the vanilla instrumenter skips
    // (constructors, closures, iterator MoveNext). A Harmony transpiler, so it also survives other patches.
    // Callers compiled before the patch keep an inlined copy and stay untimed until they are recompiled (P2).
    public static class CoverageInstrumenter
    {
        public const string HarmonyId = FrameProfilerPlusMod.HarmonyId + ".coverage";

        private static readonly Dictionary<MethodBase, int> SlotByMethod = new Dictionary<MethodBase, int>();

        public static void Instrument(MethodBase method)
        {
            lock (SlotByMethod)
            {
                if (SlotByMethod.ContainsKey(method))
                    return;
                var type = method.DeclaringType;
                SlotByMethod[method] = ScopeRuntime.Registry.Register(ScopeKind.Coverage, $"X|{type?.FullName}::{method}",
                    $"X:{type?.FullName}::{method.Name}", ModResolver.ModName(type?.Assembly), null, method);
            }
            new Harmony(HarmonyId).Patch(method, transpiler: new HarmonyMethod(typeof(CoverageInstrumenter), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            int slot;
            lock (SlotByMethod)
                slot = SlotByMethod[original];
            var returnType = (original as MethodInfo)?.ReturnType ?? typeof(void);
            return ScopeRuntime.Emitter.WrapBody(instructions, returnType, slot, repair: false, generator);
        }
    }
}
