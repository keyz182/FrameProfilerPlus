using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using FrameProfilerPlus.Attribution;
using HarmonyLib;
using Waylith.Profiling;

namespace FrameProfilerPlus.Coverage
{
    // Times a whole method body as one "X:" scope, including methods the vanilla instrumenter skips
    // (constructors, closures, iterator MoveNext). The body becomes:
    //   ldc.i4 slot; call PatchScopes.Enter; stloc entered
    //   .try { <body, each ret -> [stloc result;] leave end> }
    //   finally { ldloc entered; brfalse skip; call LeaveScope; skip: nop; endfinally }
    //   end: [ldloc result;] ret
    public static class CoverageInstrumenter
    {
        public const string HarmonyId = FrameProfilerPlusMod.HarmonyId + ".coverage";

        private static readonly MethodInfo EnterMethod = AccessTools.Method(typeof(PatchScopes), nameof(PatchScopes.Enter));
        private static readonly MethodInfo LeaveMethod = AccessTools.Method(typeof(FrameProfiler), nameof(FrameProfiler.LeaveScope));
        private static readonly Dictionary<MethodBase, int> SlotByMethod = new Dictionary<MethodBase, int>();

        public static void Instrument(MethodBase method)
        {
            lock (SlotByMethod)
            {
                if (SlotByMethod.ContainsKey(method))
                    return;
                SlotByMethod[method] = PatchScopes.Register($"X:{method.DeclaringType?.FullName}::{method.Name}");
            }
            new Harmony(HarmonyId).Patch(method, transpiler: new HarmonyMethod(typeof(CoverageInstrumenter), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            int slot;
            lock (SlotByMethod)
                slot = SlotByMethod[original];

            var returnType = (original as MethodInfo)?.ReturnType ?? typeof(void);
            var hasResult = returnType != typeof(void);
            var entered = generator.DeclareLocal(typeof(bool));
            var result = hasResult ? generator.DeclareLocal(returnType) : null;
            var end = generator.DefineLabel();
            var skip = generator.DefineLabel();

            var codes = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldc_I4, slot),
                new CodeInstruction(OpCodes.Call, EnterMethod),
                new CodeInstruction(OpCodes.Stloc, entered),
            };

            var bodyStart = codes.Count;
            foreach (var code in instructions)
            {
                if (code.opcode != OpCodes.Ret)
                {
                    codes.Add(code);
                    continue;
                }
                // Keep the ret's labels and blocks on the first replacement instruction.
                var replacement = hasResult ? new CodeInstruction(OpCodes.Stloc, result) : new CodeInstruction(OpCodes.Leave, end);
                replacement.labels.AddRange(code.labels);
                replacement.blocks.AddRange(code.blocks);
                codes.Add(replacement);
                if (hasResult)
                    codes.Add(new CodeInstruction(OpCodes.Leave, end));
            }

            // The outer try must open before any try block the body already starts on its first instruction.
            codes[bodyStart].blocks.Insert(0, new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));

            codes.Add(new CodeInstruction(OpCodes.Ldloc, entered).WithBlocks(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock)));
            codes.Add(new CodeInstruction(OpCodes.Brfalse, skip));
            codes.Add(new CodeInstruction(OpCodes.Call, LeaveMethod));
            codes.Add(new CodeInstruction(OpCodes.Nop).WithLabels(skip));
            codes.Add(new CodeInstruction(OpCodes.Endfinally).WithBlocks(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock)));

            var tail = hasResult ? new CodeInstruction(OpCodes.Ldloc, result) : new CodeInstruction(OpCodes.Ret);
            tail.labels.Add(end);
            codes.Add(tail);
            if (hasResult)
                codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }
}
