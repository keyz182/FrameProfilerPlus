using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FrameProfilerPlus.Ticks
{
    // Transpiler body that redirects calls to chosen methods onto static replacements taking the same
    // arguments (receiver first). Labels and exception blocks stay on the instruction, which is edited in place.
    public sealed class CallSwap
    {
        private readonly IReadOnlyDictionary<MethodInfo, MethodInfo> map;

        public int Count { get; private set; }

        public CallSwap(IReadOnlyDictionary<MethodInfo, MethodInfo> map) => this.map = map;

        public IEnumerable<CodeInstruction> Apply(IEnumerable<CodeInstruction> codes)
        {
            foreach (var code in codes)
            {
                if ((code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) && code.operand is MethodInfo called && map.TryGetValue(called, out var replacement))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = replacement;
                    Count++;
                }
                yield return code;
            }
        }
    }
}
