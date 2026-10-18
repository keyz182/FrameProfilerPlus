using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FrameProfilerPlus.Core
{
    // Builds the IL that wraps code in a profiler scope. Pure: the scope runtime is a type with these
    // static methods, so tests can count calls without the game:
    //   bool Enter(int slot)            void Leave(bool entered)
    //   bool EnterTracked(int slot)     void LeaveTracked(bool entered)
    //   int Mark()                      void RepairTo(int mark)
    // Leave(entered) closes the zone only when Enter returned true, so a finally block needs no branch.
    public sealed class ScopeEmitter
    {
        private readonly MethodInfo enter;
        private readonly MethodInfo leave;
        private readonly MethodInfo enterTracked;
        private readonly MethodInfo leaveTracked;
        private readonly MethodInfo mark;
        private readonly MethodInfo repairTo;

        public ScopeEmitter(Type runtime)
        {
            enter = Require(runtime, "Enter", typeof(bool), typeof(int));
            leave = Require(runtime, "Leave", typeof(void), typeof(bool));
            enterTracked = Require(runtime, "EnterTracked", typeof(bool), typeof(int));
            leaveTracked = Require(runtime, "LeaveTracked", typeof(void), typeof(bool));
            mark = Require(runtime, "Mark", typeof(int));
            repairTo = Require(runtime, "RepairTo", typeof(void), typeof(int));
        }

        // Code that starts and ends with an empty stack and may leave one value of resultType.
        public List<CodeInstruction> WrapCall(List<CodeInstruction> codes, Type resultType, int slot, ILGenerator il)
        {
            var hasResult = resultType != typeof(void);
            var result = hasResult ? il.DeclareLocal(resultType) : null;
            var entered = il.DeclareLocal(typeof(bool));
            var end = il.DefineLabel();

            var output = EnterCode(slot, entered, codes);
            output.Add(BeginTry());
            output.AddRange(codes);
            if (hasResult)
                output.Add(new CodeInstruction(OpCodes.Stloc, result));
            output.Add(new CodeInstruction(OpCodes.Leave, end));
            output.AddRange(FinallyCode(entered, null));
            output.Add(new CodeInstruction(OpCodes.Nop).WithLabels(end));
            if (hasResult)
                output.Add(new CodeInstruction(OpCodes.Ldloc, result));
            return output;
        }

        // Code that consumes one value of inType from the stack and leaves one value of outType
        // (a passthrough postfix). The input is spilled so the try starts with an empty stack.
        public List<CodeInstruction> WrapCallSpilled(List<CodeInstruction> codes, Type inType, Type outType, int slot, ILGenerator il)
        {
            var input = il.DeclareLocal(inType);
            var reload = new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldloc, input) };
            reload.AddRange(codes);
            var output = new List<CodeInstruction> { new CodeInstruction(OpCodes.Stloc, input).MoveLabelsFrom(codes[0]) };
            output.AddRange(WrapCall(reload, outType, slot, il));
            return output;
        }

        // Any stack shape, no exception safety: the enclosing wrapped body repairs a zone left open by a throw.
        public List<CodeInstruction> WrapTracked(List<CodeInstruction> codes, int slot, ILGenerator il)
        {
            var entered = il.DeclareLocal(typeof(bool));
            var output = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldc_I4, slot).MoveLabelsFrom(codes[0]),
                new CodeInstruction(OpCodes.Call, enterTracked),
                new CodeInstruction(OpCodes.Stloc, entered),
            };
            output.AddRange(codes);
            output.Add(new CodeInstruction(OpCodes.Ldloc, entered));
            output.Add(new CodeInstruction(OpCodes.Call, leaveTracked));
            return output;
        }

        // A whole method body. Every ret becomes [stloc result;] leave end. With repair, the finally first
        // closes zones that tracked sites inside the body left open, whether or not this scope was entered.
        public List<CodeInstruction> WrapBody(IEnumerable<CodeInstruction> codes, Type returnType, int slot, bool repair, ILGenerator il)
        {
            var hasResult = returnType != typeof(void);
            var result = hasResult ? il.DeclareLocal(returnType) : null;
            var entered = il.DeclareLocal(typeof(bool));
            var markLocal = repair ? il.DeclareLocal(typeof(int)) : null;
            var end = il.DefineLabel();

            var body = new List<CodeInstruction>();
            foreach (var code in codes)
            {
                if (code.opcode != OpCodes.Ret)
                {
                    body.Add(code);
                    continue;
                }
                var first = hasResult ? new CodeInstruction(OpCodes.Stloc, result) : new CodeInstruction(OpCodes.Leave, end);
                first.labels.AddRange(code.labels);
                first.blocks.AddRange(code.blocks);
                body.Add(first);
                if (hasResult)
                    body.Add(new CodeInstruction(OpCodes.Leave, end));
            }

            var output = EnterCode(slot, entered, body);
            if (repair)
            {
                output.Add(new CodeInstruction(OpCodes.Call, mark));
                output.Add(new CodeInstruction(OpCodes.Stloc, markLocal));
            }
            output.Add(BeginTry());
            output.AddRange(body);
            // Falling off the end of the body is impossible in valid IL, but keep the try closed explicitly.
            output.Add(new CodeInstruction(OpCodes.Leave, end));
            output.AddRange(FinallyCode(entered, markLocal));
            output.Add(new CodeInstruction(OpCodes.Nop).WithLabels(end));
            if (hasResult)
                output.Add(new CodeInstruction(OpCodes.Ldloc, result));
            output.Add(new CodeInstruction(OpCodes.Ret));
            return output;
        }

        private List<CodeInstruction> EnterCode(int slot, LocalBuilder entered, List<CodeInstruction> wrapped)
        {
            var first = new CodeInstruction(OpCodes.Ldc_I4, slot);
            if (wrapped.Count > 0)
                first.MoveLabelsFrom(wrapped[0]);
            return new List<CodeInstruction>
            {
                first,
                new CodeInstruction(OpCodes.Call, enter),
                new CodeInstruction(OpCodes.Stloc, entered),
            };
        }

        private static CodeInstruction BeginTry() =>
            new CodeInstruction(OpCodes.Nop).WithBlocks(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));

        private IEnumerable<CodeInstruction> FinallyCode(LocalBuilder entered, LocalBuilder markLocal)
        {
            var begin = new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock);
            if (markLocal != null)
            {
                yield return new CodeInstruction(OpCodes.Ldloc, markLocal).WithBlocks(begin);
                yield return new CodeInstruction(OpCodes.Call, repairTo);
                yield return new CodeInstruction(OpCodes.Ldloc, entered);
            }
            else
            {
                yield return new CodeInstruction(OpCodes.Ldloc, entered).WithBlocks(begin);
            }
            yield return new CodeInstruction(OpCodes.Call, leave);
            yield return new CodeInstruction(OpCodes.Endfinally).WithBlocks(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
        }

        private static MethodInfo Require(Type runtime, string name, Type returnType, params Type[] parameters)
        {
            var method = runtime.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            if (method == null || method.ReturnType != returnType)
                throw new ArgumentException($"{runtime.FullName} needs public static {returnType.Name} {name}({string.Join(", ", Array.ConvertAll(parameters, p => p.Name))}).");
            return method;
        }
    }
}
