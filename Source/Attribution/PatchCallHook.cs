using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Waylith.Profiling;

namespace FrameProfilerPlus.Attribution
{
    // Harmony emits every prefix, postfix and finalizer call through MethodCreatorTools.EmitPatchCall.
    // Wrapping its output times each patch method as its own profiler scope:
    //   ldc.i4 slot; call PatchScopes.Enter; stloc entered
    //   <argument loads; call patch>
    //   ldloc entered; brfalse skip; call FrameProfiler.LeaveScope; skip:
    // Only void calls and a local are added, so whatever the stack holds around the patch call
    // (for example a passthrough postfix's result) is left alone.
    [HarmonyPatch]
    public static class PatchCallHook
    {
        private const string TargetName = "HarmonyLib.MethodCreatorTools:EmitPatchCall";

        private static readonly MethodInfo EnterMethod = AccessTools.Method(typeof(PatchScopes), nameof(PatchScopes.Enter));
        private static readonly MethodInfo LeaveMethod = AccessTools.Method(typeof(FrameProfiler), nameof(FrameProfiler.LeaveScope));
        private static readonly Assembly OwnAssembly = typeof(PatchCallHook).Assembly;

        private static FieldInfo configField;
        private static MethodInfo declareLocal;
        private static MethodInfo defineLabel;
        private static bool failureLogged;

        public static int WrappedCount { get; private set; }

        public static bool Prepare()
        {
            if (!FrameProfilerPlusMod.Settings.harmonyAttribution)
                return false;
            if (AccessTools.Method(TargetName) != null)
                return true;
            Log.Warning($"[FrameProfilerPlus] Harmony attribution disabled: {TargetName} not found in Harmony {typeof(Harmony).Assembly.GetName().Version}.");
            return false;
        }

        public static MethodBase TargetMethod() => AccessTools.Method(TargetName);

        public static void Postfix(object creator, MethodInfo patch, object outerContext, List<CodeInstruction> __result)
        {
            // Infixes may be inlined into the replacement, so there is no single call to wrap.
            if (outerContext != null || patch.DeclaringType?.Assembly == OwnAssembly)
                return;
            try
            {
                Wrap(creator, patch, __result);
                WrappedCount++;
            }
            catch (Exception e)
            {
                // Leave the patch call unwrapped rather than break another mod's patch.
                if (!failureLogged)
                {
                    failureLogged = true;
                    Log.Warning($"[FrameProfilerPlus] Could not wrap Harmony patch {patch.FullDescription()}; it will not be timed. {e}");
                }
            }
        }

        private static void Wrap(object creator, MethodInfo patch, List<CodeInstruction> codes)
        {
            var config = (configField ??= AccessTools.Field(creator.GetType(), "config")).GetValue(creator);
            declareLocal ??= AccessTools.Method(config.GetType(), "DeclareLocal");
            defineLabel ??= AccessTools.Method(config.GetType(), "DefineLabel");

            var entered = (LocalBuilder)declareLocal.Invoke(config, new object[] { typeof(bool), false });
            var skip = (Label)defineLabel.Invoke(config, null);
            var slot = PatchScopes.Register(LabelFor(patch));

            codes.InsertRange(0, new[]
            {
                new CodeInstruction(OpCodes.Ldc_I4, slot),
                new CodeInstruction(OpCodes.Call, EnterMethod),
                new CodeInstruction(OpCodes.Stloc, entered),
            });
            codes.AddRange(new[]
            {
                new CodeInstruction(OpCodes.Ldloc, entered),
                new CodeInstruction(OpCodes.Brfalse, skip),
                new CodeInstruction(OpCodes.Call, LeaveMethod),
                new CodeInstruction(OpCodes.Nop).WithLabels(skip),
            });
        }

        private static string LabelFor(MethodInfo patch)
        {
            var kind = PatchKindTracker.Current ?? "patch";
            return $"H:{patch.DeclaringType?.Assembly.GetName().Name} {patch.DeclaringType?.Name}.{patch.Name} [{kind}]";
        }
    }

    // Records which kind of patch Harmony is emitting, for the scope label.
    [HarmonyPatch]
    public static class PatchKindTracker
    {
        [ThreadStatic] public static string Current;

        public static bool Prepare() => FrameProfilerPlusMod.Settings.harmonyAttribution;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "EmitPrefixes", "EmitPostfixes", "EmitFinalizers" })
            {
                var method = AccessTools.Method("HarmonyLib.MethodCreator:" + name);
                if (method != null)
                    yield return method;
            }
        }

        public static void Prefix(MethodBase __originalMethod) =>
            Current = __originalMethod.Name switch
            {
                "EmitPrefixes" => "prefix",
                "EmitPostfixes" => "postfix",
                _ => "finalizer",
            };

        public static void Finalizer() => Current = null;
    }
}
