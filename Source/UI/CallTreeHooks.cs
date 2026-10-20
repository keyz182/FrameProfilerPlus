using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using FrameProfilerPlus.Core;
using FrameProfilerPlus.Coverage;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.ProfilerUI;
using Waylith.Profiling;

namespace FrameProfilerPlus.UI
{
    // Fills the call tree's Detail column: the owning mod of our scopes, then time and calls per tick over the
    // selected frames. Row totals are averages per frame, so dividing by the average ticks per frame gives
    // per-tick figures. Only meaningful for simulation code; frame-rate code runs regardless of ticks.
    [HarmonyPatch(typeof(ProfilerCallTreeTable), MethodType.Constructor, typeof(ProfilerSession))]
    public static class CallTreeDetailHeader
    {
        public static void Postfix(ProfilerCallTreeTable __instance) => __instance.View.DetailHeader = "mod · per tick (sim only)";
    }

    [HarmonyPatch(typeof(ProfilerCallTreeTable), nameof(ProfilerCallTreeTable.Refresh))]
    public static class CallTreeTicksPerFrame
    {
        private static readonly AccessTools.FieldRef<ProfilerRowSection, ProfilerSession> SessionRef =
            AccessTools.FieldRefAccess<ProfilerRowSection, ProfilerSession>("session");

        public static float TicksPerFrame;

        public static void Prefix(ProfilerCallTreeTable __instance)
        {
            var session = SessionRef(__instance);
            if (!session.HasData)
                TicksPerFrame = 0f;
            else if (session.Span > 1)
                TicksPerFrame = session.SummarizeSelection().averageTickerDelta;
            else
                TicksPerFrame = session.Snapshot.tickDelta;
        }
    }

    [HarmonyPatch(typeof(ProfilerCallTreeTable), "Shape")]
    public static class CallTreeDetailShape
    {
        public static void Postfix(ProfilerTreeModel.Row row, ProfilerRowData data)
        {
            var owner = row.labelId.IsValid ? Owners(row.labelId.Value) : null;
            var ticks = CallTreeTicksPerFrame.TicksPerFrame;
            var perTick = ticks > 0f ? $"{row.totalMicroseconds / ticks / 1000f:F3} ms · {row.calls / ticks:F1} calls" : "";
            data.Detail = owner == null ? perTick : $"{owner} · {perTick}";
        }

        internal static string Owners(int labelId)
        {
            var slots = ScopeRuntime.Registry?.SlotsForLabel(labelId);
            if (slots == null || slots.Count == 0)
                return null;
            var owners = slots.Select(s => s.Owner).Distinct().ToList();
            return owners.Count == 1 ? owners[0] : $"{owners.Count} mods";
        }
    }

    // Adds items to the row context menu without replacing the vanilla ones:
    // inserts `ldarg.0; call Extend` just before `newobj FloatMenu(List<FloatMenuOption>)`.
    [HarmonyPatch(typeof(ProfilerRowSection), "OpenContextMenu")]
    public static class RowContextMenu
    {
        private static readonly ConstructorInfo FloatMenuCtor = AccessTools.Constructor(typeof(FloatMenu), new[] { typeof(List<FloatMenuOption>) });

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var found = false;
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Newobj && code.operand as ConstructorInfo == FloatMenuCtor)
                {
                    found = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RowContextMenu), nameof(Extend)));
                }
                yield return code;
            }
            if (!found)
                FrameProfilerPlusMod.Warn("Profiler context menu not found; row actions are unavailable.");
        }

        public static List<FloatMenuOption> Extend(List<FloatMenuOption> options, ProfilerRowSection section)
        {
            var originals = OriginalsFor(section.View.Rows, section.View.SelectedIndex);
            if (originals.Count > 0)
                options.Add(new FloatMenuOption("Show Harmony patches", () => Find.WindowStack.Add(new Dialog_MessageBox(Describe(originals)))));
            if (section is CoverageSection coverage)
                options.AddRange(coverage.MenuOptions(section.View.SelectedIndex));
            if (CoverageHost.Service != null)
                options.AddRange(Expanders(MethodFor(section, section.View.SelectedIndex)));
            return options;
        }

        // The method behind a row: X: and P: rows through their slot, Auto: rows through vanilla's list.
        private static MethodBase MethodFor(ProfilerRowSection section, int index)
        {
            var rows = section.View.Rows;
            if (index < 0 || index >= rows.Count)
                return null;
            if (section is CoverageSection coverage)
                return coverage.MethodAt(index);
            var label = rows[index].Label;
            if (label.StartsWith("Auto:"))
                return GameCoverage.VanillaMethodForLabel(FrameProfiler.GetOrCreateLabelId(label).Value);
            if (ScopeRuntime.Registry == null || !(label.StartsWith("X:") || label.StartsWith("P:")))
                return null;
            var originals = SlotOriginals(label);
            return originals.Count == 1 ? originals[0] : null;
        }

        private const int MaxCalleeTargets = 32;

        // Each item appends target lines, so they persist and can be edited or removed in the Coverage tab.
        private static IEnumerable<FloatMenuOption> Expanders(MethodBase method)
        {
            if (method == null)
                yield break;
            yield return new FloatMenuOption("Time this method, its lambdas and iterators (add coverage target)",
                () => Add(new[] { CoverageHost.LineFor(method) }));

            var callees = ILScan.Callees(method, out _)
                .Where(c => c?.DeclaringType != null && !c.ContainsGenericParameters && !MethodSelector.IsRefusedAssembly(c.DeclaringType.Assembly.GetName().Name))
                .Select(CoverageHost.LineFor).Distinct().Take(MaxCalleeTargets).ToList();
            if (callees.Count > 0)
                yield return new FloatMenuOption($"Time the {callees.Count} methods this calls (add coverage targets)", () => Add(callees));
        }

        private static void Add(IEnumerable<string> lines)
        {
            var added = CoverageHost.AddTargets(lines);
            Messages.Message($"Added {added} coverage target(s). Press Apply in the Coverage tab.", MessageTypeDefOf.SilentInput, false);
        }

        // P: and X: rows name their method. An H: patch can serve several methods, so use its parent P: row.
        private static List<MethodBase> OriginalsFor(List<ProfilerRowData> rows, int index)
        {
            if (index < 0 || index >= rows.Count || ScopeRuntime.Registry == null)
                return new List<MethodBase>();
            var row = rows[index];
            if (row.Label.StartsWith("H:"))
            {
                for (var i = index - 1; i >= 0; i--)
                {
                    if (rows[i].Depth < row.Depth)
                        return rows[i].Label.StartsWith("P:") ? OriginalsFor(rows, i) : SlotOriginals(row.Label);
                }
            }
            return row.Label.StartsWith("P:") || row.Label.StartsWith("X:") || row.Label.StartsWith("H:")
                ? SlotOriginals(row.Label)
                : new List<MethodBase>();
        }

        private static List<MethodBase> SlotOriginals(string label) =>
            ScopeRuntime.Registry.SlotsForLabel(FrameProfiler.GetOrCreateLabelId(label).Value)
                .SelectMany(s => s.Originals).Distinct().ToList();

        private static string Describe(List<MethodBase> originals)
        {
            var text = new StringBuilder();
            foreach (var original in originals.Take(5))
            {
                text.AppendLine(original.FullDescription());
                var info = Harmony.GetPatchInfo(original);
                if (info == null)
                {
                    text.AppendLine("  not patched");
                    continue;
                }
                Append(text, "prefix", info.Prefixes);
                Append(text, "postfix", info.Postfixes);
                Append(text, "finalizer", info.Finalizers);
                Append(text, "transpiler", info.Transpilers);
                text.AppendLine();
            }
            if (originals.Count > 5)
                text.AppendLine($"... and {originals.Count - 5} more methods.");
            return text.ToString();
        }

        private static void Append(StringBuilder text, string kind, IEnumerable<Patch> patches)
        {
            foreach (var patch in patches.OrderByDescending(p => p.priority))
            {
                var method = patch.PatchMethod;
                text.AppendLine($"  {kind}: {ModResolver.ModName(method.DeclaringType?.Assembly)} ({patch.owner}) {method.DeclaringType?.Name}.{method.Name}, priority {patch.priority}");
            }
        }
    }
}
