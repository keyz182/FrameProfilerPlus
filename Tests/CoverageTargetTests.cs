using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FrameProfilerPlus.Coverage;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class CoverageTargetTests
    {
        // Every compiler-generated shape, compiled for real so the names are the ones Roslyn emits.
        public class Fixture
        {
            public int Field = Apply(x => x + 1);

            private static int Apply(Func<int, int> f) => f(1);

            public Fixture() { }

            public int Closure(int offset)
            {
                Func<int, int> add = x => x + offset;
                return add(1);
            }

            public int NoCapture() => Apply(x => x * 2);

            public int Local(int a)
            {
                int Twice(int v) => v * 2;
                return Twice(a);
            }

            public IEnumerable<int> Numbers()
            {
                yield return 1;
            }

            public async Task<int> Waits()
            {
                await Task.Yield();
                return 1;
            }
        }

        private static CoverageTarget Parse(string line)
        {
            Assert.True(CoverageTarget.TryParse(line, out var target, out var error), error);
            return target;
        }

        private static List<MethodBase> AllBodies() =>
            new[] { typeof(Fixture) }.Concat(typeof(Fixture).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                .SelectMany(t => t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>()
                    .Concat(t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)))
                .ToList();

        private static string[] Matching(string line) =>
            AllBodies().Where(Parse(line).Matches).Select(m => m.DeclaringType.Name + "." + m.Name).OrderBy(n => n).ToArray();

        [Fact]
        public void Parse_DefaultsMissingPartsToWildcard()
        {
            var target = Parse("MyMod!");
            Assert.Equal(("MyMod", "*", "*"), (target.Assembly, target.Type, target.Method));

            target = Parse("Verse.Pawn::Tick");
            Assert.Equal(("*", "Verse.Pawn", "Tick"), (target.Assembly, target.Type, target.Method));

            target = Parse("Assembly-CSharp!Verse.*::*");
            Assert.Equal(("Assembly-CSharp", "Verse.*", "*"), (target.Assembly, target.Type, target.Method));
        }

        [Fact]
        public void Parse_TrimsAndRecognisesExclusion()
        {
            var target = Parse("  -MyMod!MyMod.Noisy  ");
            Assert.True(target.Exclude);
            Assert.Equal(("MyMod", "MyMod.Noisy", "*"), (target.Assembly, target.Type, target.Method));
            Assert.False(Parse("MyMod!*").Exclude);
        }

        [Fact]
        public void Parse_SkipsBlankAndCommentLinesWithoutError()
        {
            var errors = new List<string>();
            var all = CoverageTarget.ParseAll(new[] { "", "   ", "# note", "A!B::C" }, errors);
            Assert.Single(all);
            Assert.Empty(errors);
        }

        [Fact]
        public void Parse_RejectsStaticConstructors()
        {
            var errors = new List<string>();
            var all = CoverageTarget.ParseAll(new[] { "MyMod!Foo::.cctor" }, errors);
            Assert.Empty(all);
            Assert.Contains("static constructors", Assert.Single(errors));
        }

        [Fact]
        public void MatchesAssembly_UsesWildcardsIgnoringCase()
        {
            Assert.True(Parse("my*!").MatchesAssembly("MyMod"));
            Assert.False(Parse("other!").MatchesAssembly("MyMod"));
            Assert.True(Parse("Foo::Bar").MatchesAssembly("Anything"));
        }

        [Fact]
        public void OwnerOf_HandlesEveryGeneratedShape()
        {
            Assert.Equal("Run", GeneratedNames.OwnerOf("<Run>b__0_0"));
            Assert.Equal("Run", GeneratedNames.OwnerOf("<Run>g__Inner|0_0"));
            Assert.Equal("Run", GeneratedNames.OwnerOf("<<Run>g__Inner|0_0>b__1"));
            Assert.Equal(".ctor", GeneratedNames.OwnerOf("<.ctor>b__0_0"));
            Assert.Equal("Plain", GeneratedNames.OwnerOf("Plain"));
        }

        [Fact]
        public void StarMatchesMethodsAndTheirGeneratedBodies_ButNotConstructors()
        {
            var names = Matching("FrameProfilerPlus.Tests.CoverageTargetTests+Fixture");
            Assert.Contains(names, n => n.StartsWith("<>c.") || n.Contains("DisplayClass"));
            Assert.Contains("Fixture.Closure", names);
            Assert.DoesNotContain("Fixture..ctor", names);
            Assert.DoesNotContain(names, n => n.Contains("<.ctor>"));
        }

        [Fact]
        public void MethodName_CoversLambdaClosureLocalFunctionIteratorAndAsync()
        {
            foreach (var name in new[] { "Closure", "NoCapture", "Local", "Numbers", "Waits" })
            {
                var hits = AllBodies().Where(Parse($"::{name}").Matches).ToList();
                Assert.NotEmpty(hits);
                Assert.All(hits, m => Assert.Equal("FrameProfilerPlus.Tests.CoverageTargetTests+Fixture", GeneratedNames.SourceTypeName(m)));
            }
            Assert.Contains(AllBodies().Where(Parse("::Closure").Matches), m => m.DeclaringType.Name.Contains("DisplayClass"));
            Assert.Contains(AllBodies().Where(Parse("::Local").Matches), m => m.Name.Contains("g__Twice"));
            Assert.Contains(AllBodies().Where(Parse("::Numbers").Matches), m => m.Name == "MoveNext");
            Assert.Contains(AllBodies().Where(Parse("::Waits").Matches), m => m.Name == "MoveNext");
        }

        [Fact]
        public void Ctor_SelectsConstructorsAndTheirFieldInitialiserLambdas()
        {
            var hits = AllBodies().Where(Parse("::.ctor").Matches).ToList();
            Assert.Contains(hits, m => m is ConstructorInfo && m.DeclaringType == typeof(Fixture));
            Assert.Contains(hits, m => m.Name.StartsWith("<.ctor>"));
            Assert.DoesNotContain(hits, m => m.Name == "Closure");
        }
    }
}
