using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FrameProfilerPlus.Coverage;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class CoverageLabelsTests
    {
        public class Subject
        {
            public Subject() { }
            public Subject(int a) { }

            public int Plain(int x) => x;
            public int Over(int x) { Func<int> f = () => x; return f(); }
            public int Over(string s) { Func<int> f = () => s.Length; return f(); }
            public int Unique(int x) { int Twice(int v) => v * 2; return Twice(x); }
            public IEnumerable<int> Numbers() { yield return 1; }
            public async Task<int> Waits() { await Task.Yield(); return 1; }
        }

        private static IEnumerable<MethodBase> Bodies() =>
            new[] { typeof(Subject) }.Concat(typeof(Subject).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                .SelectMany(ILScan.AllBodies).Where(GeneratedNames.IsUserBody);

        private static string[] Texts() => Bodies().Select(CoverageLabels.Text).ToArray();

        private const string T = "FrameProfilerPlus.Tests.CoverageLabelsTests+Subject";

        [Fact]
        public void Plain_MethodHasNoParameters() => Assert.Contains($"X:{T}.Plain", Texts());

        [Fact]
        public void Overloads_CarryParameterTypes()
        {
            Assert.Contains($"X:{T}.Over(Int32)", Texts());
            Assert.Contains($"X:{T}.Over(String)", Texts());
        }

        [Fact]
        public void Constructors_CarryParameterTypes()
        {
            Assert.Contains($"X:{T}..ctor()", Texts());
            Assert.Contains($"X:{T}..ctor(Int32)", Texts());
        }

        [Fact]
        public void GeneratedBodies_ReadAsPartOfTheirOwner()
        {
            var texts = Texts();
            Assert.Contains(texts, t => t.StartsWith($"X:{T}.Unique local Twice"));
            Assert.Contains($"X:{T}.Numbers iterator", texts);
            Assert.Contains($"X:{T}.Waits async", texts);
        }

        [Fact]
        public void GeneratedBodies_OfAnOverloadedOwner_NameTheOwnerSignature()
        {
            var lambdas = Texts().Where(t => t.Contains(".Over(") && t.Contains("λ")).ToArray();

            Assert.True(lambdas.Length == 2 && lambdas.Distinct().Count() == 2, string.Join(" | ", Texts()));
        }

        [Fact]
        public void Texts_AreUniquePerMethod() => Assert.Equal(Bodies().Count(), Texts().Distinct().Count());

        [Fact]
        public void Keys_AreUniquePerMethod() => Assert.Equal(Bodies().Count(), Bodies().Select(CoverageLabels.Key).Distinct().Count());
    }
}
