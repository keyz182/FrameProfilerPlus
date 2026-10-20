using System.Collections.Generic;
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
        // Probes under tickGrouping; the thing probe is always on with it.
        public bool tickDrivers = true;
        public bool tickComps = true;
        public int labelCeiling = DefaultLabelCeiling;
        // One target per line, vanilla syntax [-][Asm!][Type][::Method]. Applied from the Coverage tab.
        public List<string> coverageTargets = new List<string>();
        // Each extra scope costs profiler buffer; vanilla overflows past a few thousand live scopes.
        public int maxCoverageMethods = 2000;
        public bool applyCoverageAtStartup;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref harmonyAttribution, "harmonyAttribution", false);
            Scribe_Values.Look(ref extendedCoverage, "extendedCoverage", false);
            Scribe_Values.Look(ref tickGrouping, "tickGrouping", false);
            Scribe_Values.Look(ref tickDrivers, "tickDrivers", true);
            Scribe_Values.Look(ref tickComps, "tickComps", true);
            Scribe_Values.Look(ref labelCeiling, "labelCeiling", DefaultLabelCeiling);
            Scribe_Collections.Look(ref coverageTargets, "coverageTargets", LookMode.Value);
            coverageTargets ??= new List<string>();
            Scribe_Values.Look(ref maxCoverageMethods, "maxCoverageMethods", 2000);
            Scribe_Values.Look(ref applyCoverageAtStartup, "applyCoverageAtStartup", false);
        }

        public void Draw(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Harmony attribution (restart required)", ref harmonyAttribution,
                "Time each Harmony prefix, postfix and finalizer as its own scope, labelled with its owning mod.");
            listing.CheckboxLabeled("Extended method coverage (restart required)", ref extendedCoverage,
                "Allow constructors, lambdas, iterators and small methods as profiling targets.");
            if (extendedCoverage)
            {
                listing.CheckboxLabeled("Apply coverage targets at startup", ref applyCoverageAtStartup,
                    "Time the saved targets as soon as the game has loaded. Costs about 0.7 ms per method at boot.");
                listing.Label($"Most methods timed at once: {maxCoverageMethods}");
                maxCoverageMethods = (int)listing.Slider(maxCoverageMethods, 100, 5000);
            }
            listing.CheckboxLabeled("Per-tick grouping (restart required)", ref tickGrouping,
                "Time each thing's tick while the profiler captures, grouped by ThingDef, for the Groups tab. Costs about 0.1 ms per tick at 1,500 things.");
            if (tickGrouping)
            {
                listing.CheckboxLabeled("  Time job drivers (restart required)", ref tickDrivers, "Group time in JobDriver ticks by driver type.");
                listing.CheckboxLabeled("  Time comps (restart required)", ref tickComps, "Group time in ThingComp ticks by comp type.");
            }
            listing.Label($"Label ceiling: {labelCeiling}");
            labelCeiling = (int)listing.Slider(labelCeiling, 1000, DefaultLabelCeiling);
            listing.End();
        }
    }
}
