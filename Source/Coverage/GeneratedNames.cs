using System;
using System.Reflection;

namespace FrameProfilerPlus.Coverage
{
    // Maps compiler-generated bodies back to the source method they belong to:
    //   lambda / closure   M's body in <>c or <>c__DisplayClassN_K, method <M>b__N_K
    //   local function     <M>g__Name|N_K
    //   iterator / async   MoveNext in the nested state machine type <M>d__N
    public static class GeneratedNames
    {
        public static bool IsGenerated(MethodBase method) =>
            method.Name.StartsWith("<", StringComparison.Ordinal) || IsStateMachineStep(method) || IsInGeneratedType(method.DeclaringType);

        // The name the user wrote: "Run" for <Run>b__0_0, "Run" for <Run>d__3.MoveNext, ".ctor" for <.ctor>b__0.
        public static string SourceMethodName(MethodBase method)
        {
            if (method.Name.StartsWith("<", StringComparison.Ordinal))
                return OwnerOf(method.Name);
            if (IsStateMachineStep(method))
                return OwnerOf(method.DeclaringType.Name);
            return method.Name;
        }

        // Full name of the nearest enclosing type that is not compiler-generated.
        public static string SourceTypeName(MethodBase method)
        {
            var type = method.DeclaringType;
            while (type != null && IsInGeneratedType(type))
                type = type.DeclaringType;
            return type?.FullName;
        }

        // "<Run>b__0_0" -> "Run"; "<<Run>g__Inner|0_0>b__1" -> "Run".
        public static string OwnerOf(string generatedName)
        {
            if (generatedName.Length == 0 || generatedName[0] != '<')
                return generatedName;
            var depth = 0;
            for (var i = 0; i < generatedName.Length; i++)
            {
                if (generatedName[i] == '<')
                    depth++;
                else if (generatedName[i] == '>' && --depth == 0)
                    return OwnerOf(generatedName.Substring(1, i - 1));
            }
            return generatedName;
        }

        private static bool IsStateMachineStep(MethodBase method) =>
            method.Name == "MoveNext" && method.DeclaringType != null && IsInGeneratedType(method.DeclaringType);

        private static bool IsInGeneratedType(Type type) =>
            type != null && type.Name.StartsWith("<", StringComparison.Ordinal);
    }
}
