using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameProfilerPlus.Core;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class LabelRegistryTests
    {
        private const int Overflow = 65535;

        private readonly List<string> created = new List<string>();
        private readonly List<ScopeKind> createdKinds = new List<ScopeKind>();
        private int ceiling = 100;
        private int baseCount;

        private LabelRegistry NewRegistry() => new LabelRegistry(
            (text, kind) =>
            {
                createdKinds.Add(kind);
                if (baseCount + created.Count >= 65535)
                    return Overflow;
                created.Add(text);
                return baseCount + created.Count - 1;
            },
            () => baseCount + created.Count,
            () => ceiling,
            Overflow);

        private static MethodBase M(string name) => typeof(LabelRegistryTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

        private static void OriginalA() { }

        private static void OriginalB() { }

        [Fact]
        public void Register_SameKey_ReusesSlotAndCollectsOriginals()
        {
            var registry = NewRegistry();

            var first = registry.Register(ScopeKind.HarmonyPatch, "k", "H:a", "ModA", null, M(nameof(OriginalA)));
            var second = registry.Register(ScopeKind.HarmonyPatch, "k", "H:a", "ModA", null, M(nameof(OriginalB)));

            Assert.Equal(first, second);
            Assert.Equal(1, registry.SlotCount);
            Assert.Equal(2, registry.Get(first).Originals.Count);
        }

        [Fact]
        public void Register_CreatesNoLabel_UntilFirstUse()
        {
            var registry = NewRegistry();

            var slot = registry.Register(ScopeKind.PatchedMethod, "k", "P:a", "Core", null, null);

            Assert.Empty(created);
            Assert.Equal(LabelRegistry.Unassigned, registry.Get(slot).LabelId);
            var id = registry.LabelIdFor(slot);
            Assert.Equal(new[] { "P:a" }, created);
            Assert.Equal(id, registry.LabelIdFor(slot));
            Assert.Single(created);
        }

        [Fact]
        public void LabelIdFor_AtCeiling_UsesSharedOtherLabelPerKind()
        {
            baseCount = 98;
            var registry = NewRegistry();
            var a = registry.Register(ScopeKind.HarmonyPatch, "a", "H:a", "M", null, null);
            var b = registry.Register(ScopeKind.HarmonyPatch, "b", "H:b", "M", null, null);
            var c = registry.Register(ScopeKind.HarmonyPatch, "c", "H:c", "M", null, null);
            var p = registry.Register(ScopeKind.PatchedMethod, "p", "P:p", "M", null, null);

            registry.LabelIdFor(a);
            registry.LabelIdFor(b);
            var idC = registry.LabelIdFor(c);
            var idP = registry.LabelIdFor(p);

            Assert.Equal(new[] { "H:a", "H:b", "H:<other>", "P:<other>" }, created);
            Assert.True(registry.CeilingReached);
            Assert.Equal("H:<other>", created[idC - baseCount]);
            Assert.Equal("P:<other>", created[idP - baseCount]);
        }

        [Fact]
        public void LabelIdFor_FactoryReturnsOverflow_MarksCeilingReached()
        {
            baseCount = 65535;
            ceiling = int.MaxValue;
            var registry = NewRegistry();
            var slot = registry.Register(ScopeKind.Coverage, "x", "X:x", "M", null, null);

            Assert.Equal(Overflow, registry.LabelIdFor(slot));
            Assert.True(registry.CeilingReached);
        }

        [Fact]
        public void SlotsForLabel_ReturnsEverySlotSharingTheLabel()
        {
            baseCount = 100;
            var registry = NewRegistry();
            var a = registry.Register(ScopeKind.HarmonyPatch, "a", "H:a", "ModA", null, null);
            var b = registry.Register(ScopeKind.HarmonyPatch, "b", "H:b", "ModB", null, null);

            var id = registry.LabelIdFor(a);
            Assert.Equal(id, registry.LabelIdFor(b));

            Assert.Equal(new[] { "ModA", "ModB" }, registry.SlotsForLabel(id).Select(s => s.Owner));
        }

        [Fact]
        public void LabelFactory_ReceivesTheKindOfTheSlot()
        {
            baseCount = 100;
            var registry = NewRegistry();
            registry.LabelIdFor(registry.Register(ScopeKind.Coverage, "x", "X:x", "M", null, null));
            registry.LabelIdFor(registry.Register(ScopeKind.HarmonyPatch, "h", "H:h", "M", null, null));

            Assert.Equal(new[] { ScopeKind.Coverage, ScopeKind.HarmonyPatch }, createdKinds);
        }

        [Fact]
        public void LabelIdFor_CalledFromLabelCreation_ReturnsUnassignedInsteadOfRecursing()
        {
            LabelRegistry registry = null;
            var inner = LabelRegistry.Unassigned;
            var innerSlot = 0;
            var calls = 0;
            registry = new LabelRegistry(
                (text, kind) =>
                {
                    calls++;
                    inner = registry.LabelIdFor(innerSlot);
                    return 7;
                },
                () => 0, () => int.MaxValue, Overflow);
            innerSlot = registry.Register(ScopeKind.Coverage, "inner", "X:inner", "M", null, null);
            var outer = registry.Register(ScopeKind.Coverage, "outer", "X:outer", "M", null, null);

            Assert.Equal(7, registry.LabelIdFor(outer));

            Assert.Equal(LabelRegistry.Unassigned, inner);
            Assert.Equal(1, calls);
            Assert.Equal(7, registry.LabelIdFor(innerSlot));
        }
    }
}
