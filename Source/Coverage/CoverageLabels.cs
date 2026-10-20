using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FrameProfilerPlus.Coverage
{
    // Names for X: scopes. Overloads and constructors carry their parameter types so two rows never read the
    // same; generated bodies read as a part of their owner: "X:Type.Run λ2", "X:Type.Run local Twice",
    // "X:Type.Run iterator".
    public static class CoverageLabels
    {
        private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Unique per method: the full signature, so overloads and generic instantiations never share a slot.
        public static string Key(MethodBase method) => $"X|{method.DeclaringType?.FullName}::{method}";

        public static string Text(MethodBase method)
        {
            var type = GeneratedNames.SourceTypeName(method);
            var owner = GeneratedNames.SourceMethodName(method);
            var text = new System.Text.StringBuilder("X:").Append(type).Append('.').Append(owner);
            if (GeneratedNames.IsGenerated(method))
            {
                var ownerOverloaded = OwnerIsOverloaded(method, owner);
                if (ownerOverloaded)
                    text.Append(Parameters(OwnerParameters(method, owner)));
                text.Append(' ').Append(Shape(method));
                return text.ToString();
            }
            if (method.IsConstructor || IsOverloaded(method))
                text.Append(Parameters(method.GetParameters()));
            return text.ToString();
        }

        private static string Shape(MethodBase method)
        {
            if (method.Name == "MoveNext")
                return typeof(IAsyncStateMachine).IsAssignableFrom(method.DeclaringType) ? "async" : "iterator";
            var name = method.Name;
            var local = name.IndexOf(">g__", StringComparison.Ordinal);
            if (local >= 0)
            {
                var start = local + 4;
                var end = name.IndexOf('|', start);
                return "local " + (end > start ? name.Substring(start, end - start) : name.Substring(start));
            }
            var marker = name.LastIndexOf(">b__", StringComparison.Ordinal);
            if (marker >= 0)
            {
                var tail = name.Substring(marker + 4);
                var underscore = tail.LastIndexOf('_');
                return "λ" + (underscore >= 0 ? tail.Substring(underscore + 1) : tail);
            }
            return "generated";
        }

        private static bool IsOverloaded(MethodBase method) =>
            method.DeclaringType != null && method.DeclaringType.GetMethods(Declared).Count(m => m.Name == method.Name) > 1;

        // Roslyn numbers a type's methods in declaration order and puts that number in the generated names,
        // so the ordinal picks out which overload owns a lambda. Without a match, no parameters are shown.
        private static ParameterInfo[] OwnerParameters(MethodBase method, string owner)
        {
            var type = SourceType(method);
            var ordinal = GeneratedNames.OwnerOrdinal(method);
            var ordered = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).OrderBy(m => m.MetadataToken).ToList();
            if (ordinal >= 0 && ordinal < ordered.Count && GeneratedNames.SourceMethodName(ordered[ordinal]) == owner)
                return ordered[ordinal].GetParameters();
            return Array.Empty<ParameterInfo>();
        }

        private static bool OwnerIsOverloaded(MethodBase method, string owner)
        {
            var type = SourceType(method);
            return type != null && Candidates(type, owner).Count() > 1;
        }

        private static System.Collections.Generic.IEnumerable<MethodBase> Candidates(Type type, string owner) =>
            owner == ".ctor" ? type.GetConstructors(Declared) : type.GetMethods(Declared).Where(m => m.Name == owner);

        private static Type SourceType(MethodBase method)
        {
            var type = method.DeclaringType;
            while (type != null && type.Name.StartsWith("<", StringComparison.Ordinal))
                type = type.DeclaringType;
            return type;
        }

        private static string Parameters(ParameterInfo[] parameters) =>
            "(" + string.Join(", ", parameters.Select(p => p.ParameterType.Name)) + ")";
    }
}
