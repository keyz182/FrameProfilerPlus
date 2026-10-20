using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FrameProfilerPlus.Coverage;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class MethodSelectorTests
    {
        public abstract class Shape
        {
            public abstract int Area();
            public int Describe() => Area();
            public T Generic<T>() => default(T);
            [DllImport("libc", EntryPoint = "getpid")] public static extern int GetPid();
        }

        public class Holder<T>
        {
            public int Count() => 1;
        }

        public class Plain
        {
            public int Alpha() => 1;
            public int Beta() => 2;
        }

        private static Selection Select(string line, SkipPolicy policy = null)
        {
            var targets = CoverageTarget.ParseAll(line.Split('\n'), null);
            return MethodSelector.Select(new[] { typeof(MethodSelectorTests).Assembly }, targets, policy ?? new SkipPolicy());
        }

        private static string[] Names(Selection s) => s.Methods.Select(m => m.Name).OrderBy(n => n).ToArray();

        [Fact]
        public void Select_PicksMatchingMethodsOnly()
        {
            var selection = Select("FrameProfilerPlus.Tests!FrameProfilerPlus.Tests.MethodSelectorTests+Plain");

            Assert.Equal(new[] { "Alpha", "Beta" }, Names(selection));
        }

        [Fact]
        public void Select_ExclusionRemovesAMethod()
        {
            var selection = Select("FrameProfilerPlus.Tests!FrameProfilerPlus.Tests.MethodSelectorTests+Plain\n-FrameProfilerPlus.Tests!*Plain::Beta");

            Assert.Equal(new[] { "Alpha" }, Names(selection));
        }

        [Fact]
        public void Select_SkipsAbstractExternAndOpenGenerics_WithReasons()
        {
            var selection = Select("FrameProfilerPlus.Tests!*MethodSelectorTests+Shape\nFrameProfilerPlus.Tests!*MethodSelectorTests+Holder*");

            Assert.Equal(new[] { "Describe" }, Names(selection));
            Assert.Equal(2, selection.Skipped[SkipReason.NoBody]);
            Assert.Equal(2, selection.Skipped[SkipReason.OpenGeneric]);
        }

        [Fact]
        public void Select_AppliesPolicyReasonsInOrder()
        {
            var policy = new SkipPolicy
            {
                AlreadyCovered = m => m.Name == "Alpha",
                TimedByVanilla = m => m.Name == "Beta",
            };

            var selection = Select("FrameProfilerPlus.Tests!*MethodSelectorTests+Plain", policy);

            Assert.Empty(selection.Methods);
            Assert.Equal(1, selection.Skipped[SkipReason.AlreadyCovered]);
            Assert.Equal(1, selection.Skipped[SkipReason.TimedByVanilla]);
        }

        [Fact]
        public void Select_OverCap_SelectsNothingAndSaysSo()
        {
            var selection = Select("FrameProfilerPlus.Tests!*MethodSelectorTests+Plain", new SkipPolicy { MaxMethods = 1 });

            Assert.True(selection.OverCap);
            Assert.Empty(selection.Methods);
            Assert.Equal(2, selection.Matched);
        }

        [Theory]
        [InlineData("System.Private.CoreLib", true)]
        [InlineData("System.Linq", true)]
        [InlineData("Waylith", true)]
        [InlineData("0Harmony", true)]
        [InlineData("MonoMod.Utils", true)]
        [InlineData("FrameProfilerPlus", true)]
        [InlineData("Assembly-CSharp", false)]
        [InlineData("MyMod", false)]
        public void IsRefusedAssembly_ProtectsTheScopeRuntime(string name, bool refused) =>
            Assert.Equal(refused, MethodSelector.IsRefusedAssembly(name));

        [Fact]
        public void Select_RefusesRuntimeAssemblies_EvenWhenNamedExactly()
        {
            var targets = CoverageTarget.ParseAll(new[] { "System.Private.CoreLib!System.Threading.Monitor" }, null);

            var selection = MethodSelector.Select(new[] { typeof(object).Assembly }, targets, new SkipPolicy());

            Assert.Empty(selection.Methods);
            Assert.Equal(new[] { "System.Private.CoreLib" }, selection.RefusedAssemblies);
        }
    }
}
