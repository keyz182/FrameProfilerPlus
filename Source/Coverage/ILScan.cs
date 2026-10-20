using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace FrameProfilerPlus.Coverage
{
    // Reads method bodies as raw IL to find direct calls. Pure reflection, no game types.
    // Only call, callvirt and newobj count: those are the sites where the JIT can inline a callee.
    // (ldftn creates a delegate; it never inlines.)
    public static class ILScan
    {
        private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<short, OpCode> OpCodeTable = BuildTable();

        public static IEnumerable<MethodBase> AllBodies(Type type)
        {
            var methods = new List<MethodBase>();
            try
            {
                methods.AddRange(type.GetMethods(Declared));
                methods.AddRange(type.GetConstructors(Declared));
            }
            catch (Exception e) when (e is TypeLoadException || e is FileNotFoundException || e is NotSupportedException)
            {
                // A type whose signatures reference missing assemblies cannot be listed; skip it.
            }
            return methods;
        }

        public static int BodySize(MethodBase method)
        {
            try
            {
                return method.GetMethodBody()?.GetILAsByteArray().Length ?? 0;
            }
            catch (Exception e) when (e is NotSupportedException || e is InvalidOperationException || e is BadImageFormatException)
            {
                return 0;
            }
        }

        // Callees of one method in call order, duplicates kept. Unresolvable tokens are counted, not thrown.
        public static List<MethodBase> Callees(MethodBase method, out int unresolved)
        {
            var callees = new List<MethodBase>();
            unresolved = 0;
            byte[] il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception e) when (e is NotSupportedException || e is InvalidOperationException || e is BadImageFormatException)
            {
                return callees;
            }
            if (il == null)
                return callees;

            var typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericTypeDefinition ? method.DeclaringType.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;
            foreach (var (token, _) in CallTokens(il))
            {
                try
                {
                    callees.Add(method.Module.ResolveMethod(token, typeArgs, methodArgs));
                }
                catch (Exception e) when (e is ArgumentException || e is BadImageFormatException || e is TypeLoadException || e is FileNotFoundException)
                {
                    unresolved++;
                }
            }
            return callees;
        }

        internal static IEnumerable<(int token, OpCode opcode)> CallTokens(byte[] il)
        {
            var i = 0;
            while (i < il.Length)
            {
                short value = il[i++];
                if (value == 0xFE)
                    value = (short)(0xFE00 | il[i++]);
                var op = OpCodeTable[value];
                var size = OperandSize(op.OperandType, il, i);
                if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
                    yield return (BitConverter.ToInt32(il, i), op);
                i += size;
            }
        }

        private static int OperandSize(OperandType type, byte[] il, int at)
        {
            switch (type)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default:
                    return 4;
            }
        }

        private static Dictionary<short, OpCode> BuildTable()
        {
            var table = new Dictionary<short, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(OpCode))
                {
                    var op = (OpCode)field.GetValue(null);
                    table[op.Value] = op;
                }
            }
            return table;
        }
    }

    // Who calls whom inside one assembly, built once (about half a second for all of Assembly-CSharp).
    public sealed class CallerIndex
    {
        private readonly Dictionary<MethodBase, List<MethodBase>> callers = new Dictionary<MethodBase, List<MethodBase>>();

        public int MethodsScanned { get; private set; }
        public int Unresolved { get; private set; }

        public static CallerIndex Build(Assembly assembly)
        {
            var index = new CallerIndex();
            foreach (var type in LoadableTypes(assembly))
            {
                foreach (var method in ILScan.AllBodies(type))
                    index.Add(method);
            }
            return index;
        }

        public IReadOnlyList<MethodBase> CallersOf(MethodBase callee) =>
            callers.TryGetValue(callee, out var list) ? list : (IReadOnlyList<MethodBase>)Array.Empty<MethodBase>();

        private void Add(MethodBase method)
        {
            MethodsScanned++;
            var callees = ILScan.Callees(method, out var unresolved);
            foreach (var callee in callees)
            {
                if (!callers.TryGetValue(callee, out var list))
                    callers[callee] = list = new List<MethodBase>();
                if (list.Count == 0 || list[list.Count - 1] != method)
                    list.Add(method);
            }
            Unresolved += unresolved;
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                var types = new List<Type>();
                foreach (var type in e.Types)
                {
                    if (type != null)
                        types.Add(type);
                }
                return types;
            }
        }
    }
}
