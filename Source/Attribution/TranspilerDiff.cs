using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FrameProfilerPlus.Attribution
{
    // An instruction as it was before a transpiler ran. Transpilers edit the shared CodeInstruction objects
    // in place, so the state has to be copied out before the call.
    public readonly struct InstructionSnapshot
    {
        public readonly CodeInstruction Instruction;
        public readonly OpCode Opcode;
        public readonly object Operand;

        public InstructionSnapshot(CodeInstruction instruction)
        {
            Instruction = instruction;
            Opcode = instruction.opcode;
            Operand = instruction.operand;
        }
    }

    // Finds the calls one transpiler introduced: new instructions that call something, and existing
    // instructions it retargeted or turned into a call. A callee only counts while its number of call sites
    // went up, so a transpiler that merely clones instructions is not blamed for the originals.
    public static class TranspilerDiff
    {
        public static List<InstructionSnapshot> Snapshot(IEnumerable<CodeInstruction> input) =>
            input.Select(code => new InstructionSnapshot(code)).ToList();

        public static List<CodeInstruction> Added(IReadOnlyList<InstructionSnapshot> before, IEnumerable<CodeInstruction> output)
        {
            var outputList = output.ToList();
            var known = new Dictionary<CodeInstruction, InstructionSnapshot>(ReferenceEqualityComparer.Instance);
            foreach (var snapshot in before)
                known[snapshot.Instruction] = snapshot;
            var allowed = AllowedPerCallee(before.Select(s => CalleeOf(s.Opcode, s.Operand)), outputList.Select(c => CalleeOf(c.opcode, c.operand)));

            var result = new List<CodeInstruction>();
            foreach (var code in outputList)
            {
                var callee = CalleeOf(code.opcode, code.operand);
                if (callee == null || !allowed.TryGetValue(callee, out var left) || left <= 0)
                    continue;
                if (known.TryGetValue(code, out var was) && CalleeOf(was.Opcode, was.Operand) == callee)
                    continue;
                allowed[callee] = left - 1;
                result.Add(code);
            }
            return result;
        }

        private static Dictionary<MethodBase, int> AllowedPerCallee(IEnumerable<MethodBase> before, IEnumerable<MethodBase> after)
        {
            var allowed = new Dictionary<MethodBase, int>();
            foreach (var callee in after.Where(c => c != null))
                allowed[callee] = allowed.GetValueOrDefault(callee) + 1;
            foreach (var callee in before.Where(c => c != null))
            {
                if (allowed.ContainsKey(callee))
                    allowed[callee]--;
            }
            return allowed;
        }

        // The method a call, callvirt or newobj instruction invokes, or null for any other instruction.
        public static MethodBase CalleeOf(CodeInstruction code) => CalleeOf(code.opcode, code.operand);

        private static MethodBase CalleeOf(OpCode opcode, object operand) =>
            (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj) ? operand as MethodBase : null;
    }
}
