using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Reflection;

namespace FrameProfilerPlus.Coverage
{
    // One target line: [-][Assembly!][Type][::Method], same syntax as the vanilla profiler's targets.
    // A missing part means "*"; "*" and "?" are the only wildcards, matched ignoring case. A leading "-"
    // excludes. Extension: ".ctor" selects instance constructors, which "*" never does.
    // Method and type are matched against the SOURCE names, so a match also covers the compiler-generated
    // lambdas, local functions and iterator/async state machines that belong to the method.
    public sealed class CoverageTarget
    {
        private const string Any = "*";

        public string Text { get; private set; }
        public bool Exclude { get; private set; }
        public string Assembly { get; private set; }
        public string Type { get; private set; }
        public string Method { get; private set; }

        public static bool TryParse(string line, out CoverageTarget target, out string error)
        {
            target = null;
            error = null;
            var text = (line ?? "").Trim();
            if (text.Length == 0 || text[0] == '#')
                return false;

            var exclude = text[0] == '-';
            var body = exclude ? text.Substring(1) : text;
            var assembly = Any;
            var bang = body.IndexOf('!');
            if (bang >= 0)
            {
                assembly = Part(body.Substring(0, bang));
                body = body.Substring(bang + 1);
            }
            var method = Any;
            var colons = body.IndexOf("::", StringComparison.Ordinal);
            if (colons >= 0)
            {
                method = Part(body.Substring(colons + 2));
                body = body.Substring(0, colons);
            }
            if (method == ".cctor")
            {
                error = $"'{text}': static constructors run once and cannot be targeted";
                return false;
            }
            target = new CoverageTarget { Text = text, Exclude = exclude, Assembly = assembly, Type = Part(body), Method = method };
            return true;
        }

        // Parses every line; lines that fail are reported, blank lines and "#" comments are skipped.
        public static List<CoverageTarget> ParseAll(IEnumerable<string> lines, List<string> errors)
        {
            var result = new List<CoverageTarget>();
            foreach (var line in lines)
            {
                if (TryParse(line, out var target, out var error))
                    result.Add(target);
                else if (error != null)
                    errors?.Add(error);
            }
            return result;
        }

        public bool MatchesAssembly(string assemblyName) => Matches(Assembly, assemblyName);

        public bool Matches(MethodBase method)
        {
            if (!GeneratedNames.IsUserBody(method))
                return false;
            var sourceMethod = GeneratedNames.SourceMethodName(method);
            if (Method == Any && sourceMethod.StartsWith(".", StringComparison.Ordinal))
                return false;
            return Matches(Type, GeneratedNames.SourceTypeName(method)) && Matches(Method, sourceMethod);
        }

        private static string Part(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length == 0 ? Any : trimmed;
        }

        private static bool Matches(string pattern, string value) =>
            pattern == Any || (value != null && FileSystemName.MatchesSimpleExpression(pattern, value));

        public override string ToString() => Text;
    }
}
