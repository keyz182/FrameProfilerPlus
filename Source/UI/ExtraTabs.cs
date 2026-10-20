using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FrameProfilerPlus.Coverage;
using FrameProfilerPlus.Ticks;
using HarmonyLib;
using Verse;
using Verse.ProfilerUI;
using Waylith.Profiling;

namespace FrameProfilerPlus.UI
{
    // Adds tabs after the vanilla ones in the Frame Profiler window. The tab strip's labels are a static
    // readonly array, so the transpiler swaps the array load, maps the active index to our tab while our
    // section is active, and handles a click on our indexes. The vanilla tab count differs between builds
    // (4976 has three, 4981 has four), so indexes come from the label array, never a constant.
    [HarmonyPatch(typeof(Window_FrameProfiler), "DrawTabStrip")]
    public static class ExtraTabs
    {
        private sealed class Tab
        {
            public string Label;
            public Type SectionType;
            public Func<ProfilerSession, ProfilerRowSection> Create;
        }

        private static readonly FieldInfo TabLabelsField = AccessTools.Field(typeof(Window_FrameProfiler), "TabLabels");
        private static readonly MethodInfo SegmentsMethod = AccessTools.Method(typeof(ProfilerGui), nameof(ProfilerGui.Segments));
        private static readonly AccessTools.FieldRef<Window_FrameProfiler, ProfilerSession> SessionRef =
            AccessTools.FieldRefAccess<Window_FrameProfiler, ProfilerSession>("session");
        private static readonly MethodInfo SwitchSection = AccessTools.Method(typeof(Window_FrameProfiler), "SwitchSection");
        private static readonly ConditionalWeakTable<Window_FrameProfiler, ProfilerRowSection[]> Sections =
            new ConditionalWeakTable<Window_FrameProfiler, ProfilerRowSection[]>();
        private static readonly AccessTools.FieldRef<Window_FrameProfiler, ProfilerRowSection> ActiveSectionRef =
            AccessTools.FieldRefAccess<Window_FrameProfiler, ProfilerRowSection>("activeSection");

        private static List<Tab> tabs;
        private static string[] labels;

        // Built on first draw: settings are loaded and the coverage service exists by then.
        private static List<Tab> Tabs => tabs ??= BuildTabs();

        private static int VanillaCount => ((string[])TabLabelsField.GetValue(null)).Length;

        private static List<Tab> BuildTabs()
        {
            var list = new List<Tab> { new Tab { Label = "Patches", SectionType = typeof(PatchesSection), Create = s => new PatchesSection(s) } };
            if (CoverageHost.Service != null)
                list.Add(new Tab { Label = "Coverage", SectionType = typeof(CoverageSection), Create = s => new CoverageSection(s) });
            if (FrameProfilerPlusMod.Settings.tickGrouping && TickProbes.Aggregator != null)
                list.Add(new Tab { Label = "Groups", SectionType = typeof(GroupsSection), Create = s => new GroupsSection(s) });
            return list;
        }

        public static string[] Labels() => labels ??= ((string[])TabLabelsField.GetValue(null)).Concat(Tabs.Select(t => t.Label)).ToArray();

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var labelsAt = codes.FindIndex(c => c.LoadsField(TabLabelsField));
            var segmentsAt = codes.FindIndex(c => c.Calls(SegmentsMethod));
            // Segments(ref x, y, labels, activeIndex, Color? activeFill, redIndex): the default Color? is built
            // with `ldloca; initobj`, and the active-index branches all join at that ldloca.
            var initobjAt = codes.FindIndex(labelsAt + 1, c => c.opcode == OpCodes.Initobj);
            if (labelsAt < 0 || segmentsAt < 0 || initobjAt < 0 || initobjAt > segmentsAt || !codes[segmentsAt + 1].IsStloc())
            {
                Log.Warning("[FrameProfilerPlus] Profiler tab strip not recognised; the extra tabs are unavailable.");
                return codes;
            }

            codes[labelsAt] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ExtraTabs), nameof(Labels))).MoveLabelsFrom(codes[labelsAt]);
            // After `stloc clicked`: clicked = OnClicked(this, clicked).
            var store = codes[segmentsAt + 1];
            var load = LoadFor(store);
            codes.InsertRange(segmentsAt + 2, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                load,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ExtraTabs), nameof(OnClicked))),
                new CodeInstruction(store.opcode, store.operand),
            });
            // Before the Color? argument: activeIndex = ActiveIndex(activeIndex, this).
            var join = codes[initobjAt - 1];
            codes.InsertRange(initobjAt - 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(join),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ExtraTabs), nameof(ActiveIndex))),
            });
            return codes;
        }

        public static int ActiveIndex(int vanillaIndex, Window_FrameProfiler window)
        {
            var active = ActiveSectionRef(window);
            var at = active == null ? -1 : Tabs.FindIndex(t => t.SectionType == active.GetType());
            return at < 0 ? vanillaIndex : VanillaCount + at;
        }

        private static CodeInstruction LoadFor(CodeInstruction store)
        {
            if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            return new CodeInstruction(store.opcode == OpCodes.Stloc_S ? OpCodes.Ldloc_S : OpCodes.Ldloc, store.operand);
        }

        // Returns the index vanilla should act on: -1 when the click was ours.
        public static int OnClicked(Window_FrameProfiler window, int clicked)
        {
            var index = clicked - VanillaCount;
            if (clicked < 0 || index < 0 || index >= Tabs.Count)
                return clicked;
            var sections = Sections.GetValue(window, _ => new ProfilerRowSection[Tabs.Count]);
            sections[index] ??= Tabs[index].Create(SessionRef(window));
            SwitchSection.Invoke(window, new object[] { sections[index] });
            return -1;
        }
    }
}
