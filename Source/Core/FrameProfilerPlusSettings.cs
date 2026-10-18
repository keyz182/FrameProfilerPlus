using UnityEngine;
using Verse;

namespace FrameProfilerPlus
{
    public class FrameProfilerPlusSettings : ModSettings
    {
        // The label cap (65,535) is shared with vanilla and never freed; stay well below it.
        public const int DefaultLabelCeiling = 60000;

        // Off by default: the hook touches every Harmony replacement. Read once at startup.
        public bool harmonyAttribution;
        public bool extendedCoverage;
        public bool tickGrouping;
        public int labelCeiling = DefaultLabelCeiling;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref harmonyAttribution, "harmonyAttribution", false);
            Scribe_Values.Look(ref extendedCoverage, "extendedCoverage", false);
            Scribe_Values.Look(ref tickGrouping, "tickGrouping", false);
            Scribe_Values.Look(ref labelCeiling, "labelCeiling", DefaultLabelCeiling);
        }

        public void Draw(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Harmony attribution (restart required)", ref harmonyAttribution,
                "Time each Harmony prefix, postfix and finalizer as its own scope, labelled with its owning mod.");
            listing.CheckboxLabeled("Extended method coverage", ref extendedCoverage,
                "Allow constructors, lambdas, iterators and small methods as profiling targets.");
            listing.CheckboxLabeled("Per-tick grouping", ref tickGrouping,
                "Record per-tick time grouped by Def, Pawn and Thing for the Ticks tab.");
            listing.Label($"Label ceiling: {labelCeiling}");
            labelCeiling = (int)listing.Slider(labelCeiling, 1000, DefaultLabelCeiling);
            listing.End();
        }
    }
}
