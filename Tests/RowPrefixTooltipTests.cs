using FrameProfilerPlus.UI;
using Xunit;

namespace FrameProfilerPlus.Tests
{
    public class RowPrefixTooltipTests
    {
        [Theory]
        [InlineData("Auto:Pawn_NeedsTracker.NeedsTrackerTick", "Auto:")]
        [InlineData("P:Pawn.Tick", "P:")]
        [InlineData("H:SomeMod Patch_Pawn.Postfix [postfix]", "H:")]
        [InlineData("T:SomeMod Patch.Transpiler: Foo.Bar", "T:")]
        [InlineData("X:Thing.DoTick", "X:")]
        public void KnownPrefixesAreExplained(string label, string prefix)
        {
            var text = RowPrefixTooltip.Describe(label);
            Assert.NotNull(text);
            Assert.StartsWith(prefix, text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Frame")]
        [InlineData("MapUpdate")]
        [InlineData("p:lowercase")]
        public void OtherLabelsGetNoTooltip(string label)
        {
            Assert.Null(RowPrefixTooltip.Describe(label));
        }
    }
}
