using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FrameProfilerPlus.Core;
using FrameProfilerPlus.Ticks;
using HarmonyLib;

namespace FrameProfilerPlus.Attribution
{
    // A call a foreign transpiler added to a method, seen while Harmony ran that transpiler.
    internal sealed class RecordedCall
    {
        public CodeInstruction Instruction;
        public MethodBase Callee;
        public MethodBase Transpiler;
    }

    // Times the calls foreign transpilers add. Harmony runs a method's transpilers one after another in a
    // closure that calls each through MethodBase.Invoke. That call is redirected to RunStep, which runs the
    // transpiler and only RECORDS what it added: later transpilers must see exactly the IL they would see
    // without us. After the last transpiler, AttributionHooks hands the finished list to Insert, which wraps
    // the recorded calls that survived, once.
    internal static class TranspilerAttribution
    {
        private static readonly MethodInfo InvokeMethod = AccessTools.Method(typeof(MethodBase), nameof(MethodBase.Invoke), new[] { typeof(object), typeof(object[]) });
        private static Action<string> warn = _ => { };
        private static bool failureWarned;
        private static int stepSites;

        public static int StepsSkipped { get; private set; }

        public static int Recorded { get; private set; }

        // False when Harmony's per-transpiler closure was not found; the feature then stays off.
        public static bool Install(Harmony harmony, Action<string> warning)
        {
            warn = warning;
            var step = FindStepMethod();
            if (step == null)
            {
                warning("Transpiler attribution is off: Harmony's per-transpiler step was not found.");
                return false;
            }
            harmony.Patch(step, transpiler: new HarmonyMethod(typeof(TranspilerAttribution), nameof(StepTranspiler)));
            if (stepSites > 0)
                return true;
            warning("Transpiler attribution is off: Harmony's transpiler call was not found in its step.");
            return false;
        }

        // The closure inside CodeTranspiler.GetResult that invokes one transpiler.
        private static MethodInfo FindStepMethod()
        {
            var owner = AccessTools.TypeByName("HarmonyLib.CodeTranspiler");
            if (owner == null || InvokeMethod == null)
                return null;
            var candidates = owner.GetNestedTypes(AccessTools.all)
                .SelectMany(t => t.GetMethods(AccessTools.allDeclared))
                .Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(MethodInfo) && CallsInvoke(m))
                .ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static bool CallsInvoke(MethodBase method)
        {
            try
            {
                return PatchProcessor.GetOriginalInstructions(method).Any(c => c.Calls(InvokeMethod));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> StepTranspiler(IEnumerable<CodeInstruction> codes)
        {
            var swap = new CallSwap(new Dictionary<MethodInfo, MethodInfo> { [InvokeMethod] = AccessTools.Method(typeof(TranspilerAttribution), nameof(RunStep)) });
            var result = swap.Apply(codes).ToList();
            stepSites += swap.Count;
            return result;
        }

        public static object RunStep(MethodBase transpiler, object target, object[] args)
        {
            var generation = AttributionHooks.CurrentGeneration;
            if (generation == null || AttributionHooks.IsSuppressed || transpiler.DeclaringType?.Assembly == AttributionHooks.OwnAssembly)
                return transpiler.Invoke(target, args);
            // Harmony hands the standard parameter type a List; transpilers of another CodeInstruction type go through its
            // conversion path and are not attributed.
            var input = args.OfType<List<CodeInstruction>>().FirstOrDefault();
            if (input == null)
            {
                StepsSkipped++;
                return transpiler.Invoke(target, args);
            }
            var snapshot = TranspilerDiff.Snapshot(input);
            var result = transpiler.Invoke(target, args);
            if (!(result is IEnumerable<CodeInstruction> output))
                return result;
            try
            {
                var list = output as List<CodeInstruction> ?? output.ToList();
                foreach (var added in TranspilerDiff.Added(snapshot, list))
                {
                    generation.recorded.Add(new RecordedCall { Instruction = added, Callee = TranspilerDiff.CalleeOf(added), Transpiler = transpiler });
                    Recorded++;
                }
                return list;
            }
            catch (Exception e)
            {
                WarnOnce($"Could not record what a transpiler added; its calls are not timed. {e.Message}");
                return result;
            }
        }

        // Wraps each recorded call that is still in the final list, by reference and with the same callee. A
        // prefix opcode (constrained., volatile., ...) belongs to its call, so the enter code goes before it;
        // a tail. call is left alone.
        internal static List<CodeInstruction> Insert(List<CodeInstruction> codes, IReadOnlyList<RecordedCall> recorded, ILGenerator il,
            ScopeEmitter emitter, Func<RecordedCall, int> slotFor, out int wrapped)
        {
            wrapped = 0;
            var byInstruction = new Dictionary<CodeInstruction, RecordedCall>(ReferenceEqualityComparer.Instance);
            foreach (var call in recorded)
                byInstruction[call.Instruction] = call;

            var output = new List<CodeInstruction>(codes.Count + recorded.Count * 4);
            var pending = new List<CodeInstruction>();
            foreach (var code in codes)
            {
                if (code.opcode.OpCodeType == OpCodeType.Prefix)
                {
                    pending.Add(code);
                    continue;
                }
                if (byInstruction.TryGetValue(code, out var call) && TranspilerDiff.CalleeOf(code) == call.Callee && !pending.Any(p => p.opcode == OpCodes.Tailcall))
                {
                    var segment = new List<CodeInstruction>(pending) { code };
                    var scoped = emitter.WrapTracked(segment, slotFor(call), il);
                    scoped[0].blocks.AddRange(segment[0].blocks);
                    segment[0].blocks.Clear();
                    output.AddRange(scoped);
                    wrapped++;
                }
                else
                {
                    output.AddRange(pending);
                    output.Add(code);
                }
                pending.Clear();
            }
            output.AddRange(pending);
            return output;
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
