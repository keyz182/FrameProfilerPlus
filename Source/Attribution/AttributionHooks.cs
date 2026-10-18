using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FrameProfilerPlus.Core;
using HarmonyLib;

namespace FrameProfilerPlus.Attribution
{
    public sealed class AttributionStats
    {
        public bool PatchCallsActive;
        public bool MethodWrapActive;
        public int Generations;
        public int PatchCallsWrapped;
        public int MethodsWrapped;
        public int MissedMethodWraps;
        public int Retries;

        public override string ToString() =>
            $"{Generations} replacements built, {MethodsWrapped} patched methods timed (P:), {PatchCallsWrapped} patch calls timed (H:), " +
            $"{Retries} rebuilt without timing, {MissedMethodWraps} missed";
    }

    // Times Harmony patches from inside Harmony's replacement builder (0Harmony 3.0.0-rc.3 internals):
    // - EmitPatchCall builds every prefix, postfix and finalizer call: each becomes an H: scope.
    // - CreateReplacement hands the finished replacement to EmitCodes: it becomes a P: scope, which restores the
    //   timing that vanilla instrumentation loses on a patched method and repairs zones a throwing patch left open.
    // - UpdateWrapper builds and detours one replacement: it carries the per-generation state and, if our IL
    //   made the build fail, builds it once more without timing.
    // Installed explicitly, with the scope runtime injected, so tests can run the real hooks.
    public static class AttributionHooks
    {
        internal enum Site
        {
            None,
            Prefix,
            Postfix,
            FinalizerNormal,
            FinalizerCatch,
        }

        internal sealed class Generation
        {
            public PatchInfo patchInfo;
            public bool touched;
            public bool wrapExpected;
            public bool wrapCalled;
        }

        [ThreadStatic] private static Site site;
        [ThreadStatic] private static Generation current;
        [ThreadStatic] private static bool suppressed;

        private static readonly Assembly Own = typeof(AttributionHooks).Assembly;
        private static readonly MethodInfo UpdateWrapper = AccessTools.Method("HarmonyLib.PatchFunctions:UpdateWrapper");
        private static readonly MethodInfo EmitPatchCall = AccessTools.Method("HarmonyLib.MethodCreatorTools:EmitPatchCall");
        private static readonly MethodInfo EmitCodes = AccessTools.Method("HarmonyLib.MethodCreatorTools:EmitCodes");
        private static readonly MethodInfo CreateReplacement = AccessTools.Method("HarmonyLib.MethodCreator:CreateReplacement");
        private static readonly FieldInfo[] PatchArrays = typeof(PatchInfo).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(Patch[])).ToArray();

        private static ScopeEmitter emitter;
        private static LabelRegistry registry;
        private static Func<Assembly, string> ownerOf;
        private static Action<string> warn;
        private static bool failureWarned;

        public static AttributionStats Stats { get; } = new AttributionStats();

        // Tests only: makes the P: wrap emit invalid IL so the rebuild path can be exercised.
        internal static bool ForceInvalidMethodWrap { get; set; }

        public static AttributionStats Install(Harmony harmony, Type runtime, LabelRegistry labels, Func<Assembly, string> owner, Action<string> warning)
        {
            emitter = new ScopeEmitter(runtime);
            registry = labels;
            ownerOf = owner;
            warn = warning;

            var trackers = new[] { "EmitPrefixes", "EmitPostfixes", "EmitFinalizers" }
                .Select(n => AccessTools.Method("HarmonyLib.MethodCreator:" + n)).ToArray();
            if (UpdateWrapper == null || EmitPatchCall == null || trackers.Any(t => t == null))
            {
                warn($"Harmony attribution is off: Harmony {typeof(Harmony).Assembly.GetName().Version} does not have the expected internals.");
                return Stats;
            }

            harmony.Patch(UpdateWrapper, prefix: Hook(nameof(UpdateWrapperPrefix)), finalizer: Hook(nameof(UpdateWrapperFinalizer)));
            foreach (var tracker in trackers)
                harmony.Patch(tracker, prefix: Hook(nameof(SitePrefix)), finalizer: Hook(nameof(SiteFinalizer)));
            harmony.Patch(EmitPatchCall, postfix: Hook(nameof(EmitPatchCallPostfix)));
            Stats.PatchCallsActive = true;

            if (CreateReplacement != null && EmitCodes != null)
                harmony.Patch(CreateReplacement, transpiler: Hook(nameof(CreateReplacementTranspiler)));
            if (!Stats.MethodWrapActive)
                warn("Patched methods are not timed as a whole (P: scopes): Harmony's replacement builder did not match.");
            return Stats;
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(AttributionHooks), name));

        // ---- per-generation state -------------------------------------------------------------------------

        private static void UpdateWrapperPrefix(PatchInfo patchInfo, out Generation __state)
        {
            __state = current;
            // Decided up front: the build that installs the P: hook itself runs the unhooked builder.
            current = new Generation { patchInfo = patchInfo, wrapExpected = Stats.MethodWrapActive };
        }

        private static Exception UpdateWrapperFinalizer(Exception __exception, MethodBase original, PatchInfo patchInfo, ref MethodInfo __result, Generation __state)
        {
            var generation = current;
            current = __state;
            if (generation == null || suppressed)
                return __exception;

            Stats.Generations++;
            if (__exception == null)
            {
                if (generation.wrapExpected && !generation.wrapCalled)
                    Stats.MissedMethodWraps++;
                return null;
            }
            if (!generation.touched || !CausedByUs(__exception))
                return __exception;

            suppressed = true;
            try
            {
                __result = (MethodInfo)UpdateWrapper.Invoke(null, new object[] { original, patchInfo });
                Stats.Retries++;
                warn($"{original.FullDescription()} is patched but not timed: timing it made Harmony fail ({__exception.GetType().Name}: {__exception.Message}).");
                return null;
            }
            catch (TargetInvocationException)
            {
                return __exception;
            }
            finally
            {
                suppressed = false;
            }
        }

        // Retry only for failures our IL can cause: invalid IL found at detour time, an IL emitter error,
        // or an exception from our own code. A foreign transpiler's or binding error has none of these.
        private static bool CausedByUs(Exception exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is InvalidProgramException)
                    return true;
                foreach (var frame in new StackTrace(e, false).GetFrames())
                {
                    var type = frame.GetMethod()?.DeclaringType;
                    if (type?.Assembly == Own || type?.Namespace == "System.Reflection.Emit")
                        return true;
                }
            }
            return false;
        }

        private static bool HasForeignPatch(PatchInfo info)
        {
            foreach (var field in PatchArrays)
            {
                if (field.GetValue(info) is Patch[] patches && patches.Any(p => p.PatchMethod?.DeclaringType?.Assembly != Own))
                    return true;
            }
            return false;
        }

        // ---- H: one scope per patch call ---------------------------------------------------------------

        private static void SitePrefix(MethodBase __originalMethod, object[] __args, out Site __state)
        {
            __state = site;
            site = __originalMethod.Name switch
            {
                "EmitPrefixes" => Site.Prefix,
                "EmitPostfixes" => Site.Postfix,
                // EmitFinalizers(finalizers, context, catchExceptions, outerContext)
                _ => __args.Length > 2 && __args[2] is true ? Site.FinalizerCatch : Site.FinalizerNormal,
            };
        }

        private static void SiteFinalizer(Site __state) => site = __state;

        private static void EmitPatchCallPostfix(object creator, MethodInfo patch, object outerContext, ref List<CodeInstruction> __result)
        {
            var generation = current;
            // Infixes may be inlined into the replacement, so there is no single call to time.
            if (outerContext != null || suppressed || generation == null || __result.Count == 0 || patch.DeclaringType?.Assembly == Own)
                return;
            try
            {
                var config = Config(creator);
                var wrapped = WrapPatchCall(__result, patch, (ILGenerator)ConfigField(config, "il"), (MethodBase)ConfigField(config, "original"));
                if (wrapped == null)
                    return;
                __result = wrapped;
                generation.touched = true;
                Stats.PatchCallsWrapped++;
            }
            catch (Exception e)
            {
                WarnOnce($"Could not time Harmony patch {patch.FullDescription()}; it runs untimed. {e}");
            }
        }

        private static List<CodeInstruction> WrapPatchCall(List<CodeInstruction> codes, MethodInfo patch, ILGenerator il, MethodBase original)
        {
            switch (site)
            {
                case Site.Prefix:
                    return emitter.WrapCall(codes, patch.ReturnType, PatchSlot(patch, "prefix", original), il);
                case Site.Postfix when patch.ReturnType == typeof(void):
                    return emitter.WrapCall(codes, typeof(void), PatchSlot(patch, "postfix", original), il);
                case Site.Postfix:
                    // Passthrough postfix: the previous result is on the stack and is its first argument.
                    return emitter.WrapCallSpilled(codes, patch.GetParameters()[0].ParameterType, patch.ReturnType, PatchSlot(patch, "postfix", original), il);
                case Site.FinalizerCatch:
                    return emitter.WrapCall(codes, patch.ReturnType, PatchSlot(patch, "finalizer", original), il);
                case Site.FinalizerNormal when Stats.MethodWrapActive:
                    // The stack shape is not fixed here; the enclosing P: scope repairs a zone left open by a throw.
                    return emitter.WrapTracked(codes, PatchSlot(patch, "finalizer", original), il);
                default:
                    return null;
            }
        }

        private static int PatchSlot(MethodInfo patch, string kind, MethodBase original)
        {
            var type = patch.DeclaringType;
            var owner = ownerOf(type?.Assembly);
            return registry.Register(ScopeKind.HarmonyPatch, $"H|{type?.FullName}.{patch.Name}|{kind}",
                $"H:{owner} {type?.Name}.{patch.Name} [{kind}]", owner, patch, original);
        }

        // ---- P: one scope per patched method ------------------------------------------------------------

        private static IEnumerable<CodeInstruction> CreateReplacementTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var calls = codes.Select((c, i) => (c, i)).Where(x => x.c.Calls(EmitCodes)).Select(x => x.i).ToList();
            if (calls.Count != 1)
                return codes;
            // Stack before the call: creator, emitter, instructions. Replace the instructions with Wrap(instructions, creator).
            codes.InsertRange(calls[0], new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AttributionHooks), nameof(WrapReplacement))),
            });
            Stats.MethodWrapActive = true;
            return codes;
        }

        public static List<CodeInstruction> WrapReplacement(List<CodeInstruction> codes, object creator)
        {
            var generation = current;
            // Reverse patches and our own rebuild without timing do not get a P: scope.
            if (generation == null || suppressed)
                return codes;
            generation.wrapCalled = true;
            if (!HasForeignPatch(generation.patchInfo))
                return codes;
            try
            {
                var config = Config(creator);
                var original = (MethodBase)ConfigField(config, "original");
                var slot = MethodSlot(original);
                var wrapped = emitter.WrapBody(codes, (Type)ConfigField(config, "returnType"), slot, repair: true, (ILGenerator)ConfigField(config, "il"));
                if (ForceInvalidMethodWrap)
                    wrapped.Insert(0, new CodeInstruction(OpCodes.Pop));
                generation.touched = true;
                Stats.MethodsWrapped++;
                return wrapped;
            }
            catch (Exception e)
            {
                WarnOnce($"Could not time patched method; it runs untimed. {e}");
                return codes;
            }
        }

        private static int MethodSlot(MethodBase original)
        {
            var type = original.DeclaringType;
            var signature = string.Join(",", original.GetParameters().Select(p => p.ParameterType.FullName));
            var overloaded = type != null && type.GetMember(original.Name, AccessTools.all).Length > 1;
            var label = $"P:{type?.Name}.{original.Name}" + (overloaded ? $"({string.Join(", ", original.GetParameters().Select(p => p.ParameterType.Name))})" : "");
            return registry.Register(ScopeKind.PatchedMethod, $"P|{type?.FullName}.{original.Name}({signature})", label, ownerOf(type?.Assembly), null, original);
        }

        // ---- helpers ------------------------------------------------------------------------------------

        private static FieldInfo creatorConfig;
        private static readonly Dictionary<string, FieldInfo> ConfigFields = new Dictionary<string, FieldInfo>();

        private static object Config(object creator) => (creatorConfig ??= AccessTools.Field(creator.GetType(), "config")).GetValue(creator);

        private static object ConfigField(object config, string name)
        {
            FieldInfo field;
            lock (ConfigFields)
            {
                if (!ConfigFields.TryGetValue(name, out field))
                    ConfigFields[name] = field = AccessTools.Field(config.GetType(), name);
            }
            return field.GetValue(config);
        }

        private static void WarnOnce(string message)
        {
            if (failureWarned)
                return;
            failureWarned = true;
            warn(message);
        }
    }
}
