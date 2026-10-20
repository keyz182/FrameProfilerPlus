using System;
using System.Linq;
using System.Reflection;
using FrameProfilerPlus.Coverage;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class ILScanTests
    {
        public class Thing
        {
            public Thing(int x) { }
        }

        public static class Fixture
        {
            public static int Leaf(int x) => x;
            public static int Other(int x) => x + 1;

            public static int Callsite(int x)
            {
                var a = Leaf(x) + Leaf(x);
                new Thing(a);
                Func<int, int> f = Other;
                return f(a) + string.Empty.Length + typeof(Fixture).Name.Length;
            }

            public static int Indirect(int x) => Callsite(x);
        }

        private static MethodInfo M(string name) => typeof(Fixture).GetMethod(name);

        [Fact]
        public void Callees_ListsCallsAndNewobjInOrder_KeepingDuplicates()
        {
            var callees = ILScan.Callees(M("Callsite"), out var unresolved);

            Assert.Equal(0, unresolved);
            Assert.Equal(2, callees.Count(c => c == M("Leaf")));
            Assert.Contains(callees, c => c is ConstructorInfo && c.DeclaringType == typeof(Thing));
        }

        [Fact]
        public void Callees_IgnoresLdftn()
        {
            var callees = ILScan.Callees(M("Callsite"), out _);

            Assert.DoesNotContain(M("Other"), callees);
        }

        [Fact]
        public void Callees_OfBodylessMethod_IsEmpty()
        {
            var abstractMethod = typeof(Type).GetMethod("get_Namespace");
            Assert.Empty(ILScan.Callees(typeof(IDisposable).GetMethod("Dispose"), out var unresolved));
            Assert.Equal(0, unresolved);
            Assert.NotNull(abstractMethod);
        }

        [Fact]
        public void CallerIndex_FindsDirectCallersWithinTheAssembly()
        {
            var index = CallerIndex.Build(typeof(ILScanTests).Assembly);

            Assert.Contains(M("Callsite"), index.CallersOf(M("Leaf")));
            Assert.Contains(M("Indirect"), index.CallersOf(M("Callsite")));
            Assert.Empty(index.CallersOf(M("Indirect")));
            Assert.True(index.MethodsScanned > 10);
        }

        [Fact]
        public void BodySize_IsZeroWithoutBody_AndPositiveWithOne()
        {
            Assert.True(ILScan.BodySize(M("Leaf")) > 0);
            Assert.Equal(0, ILScan.BodySize(typeof(IDisposable).GetMethod("Dispose")));
        }
    }
}
