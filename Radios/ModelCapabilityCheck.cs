#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// One trace line per connect when what FlexLib holds about the radio
    /// disagrees with FlexLib's own per-model table, <see cref="ModelInfo"/>
    /// (#429).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A cross-check, NOT a switch to the table.</b> The radio's own answer
    /// still decides everything, because it accounts for licences and firmware
    /// and a static table cannot. This only writes down a disagreement, which
    /// is exactly what would explain a support case. Trace only: nothing is
    /// spoken and nothing changes.
    /// </para>
    /// <para>
    /// <b>What is compared.</b> The slice count (<c>Radio.MaxSlices</c>
    /// against <c>ModelInfo.SliceList.Count</c>; the table carries no slice
    /// count of its own) and diversity (<c>Radio.DiversityIsAllowed</c> against
    /// <c>ModelInfo.IsDiversityAllowed</c>). DAX IQ is not compared: the radio
    /// gives FlexLib no channel count or rate list to compare against.
    /// </para>
    /// <para>
    /// <b>Zero slices is "not reported", never a disagreement.</b> FlexLib sets
    /// <c>MaxSlices</c> only from LOCAL discovery, so every radio reached over
    /// SmartLink reads 0 (read from FlexLib's source, and the remote 6300's
    /// MaxSlices=0 recorded in CLAUDE.md fits it).
    /// </para>
    /// <para>
    /// <b>Diversity may be FlexLib's own guess.</b> When the radio's info reply
    /// carries no <c>diversity_allowed</c>, FlexLib answers from a model switch
    /// of its own, so a diversity disagreement can be between FlexLib's two
    /// tables rather than between the radio and the table. The line says which
    /// value is which; it cannot say where FlexLib's came from.
    /// </para>
    /// <para>
    /// <b>An unknown model compares nothing.</b> <c>GetModelInfoForModel</c>
    /// quietly returns the DEFAULT row (two slices, no diversity) for any name
    /// it does not carry, which would read every such radio as a disagreement.
    /// That row is detected and the check stands down. The seven names the
    /// table carries that our rig table does not (ML-9600 and its relatives)
    /// are compared like any other: the row is FlexLib's, and so is the claim.
    /// </para>
    /// </remarks>
    internal static class ModelCapabilityCheck
    {
        /// <summary>
        /// The disagreements, in words, or none. <paramref name="standDown"/>
        /// says why nothing was compared, when nothing was.
        /// </summary>
        internal static IReadOnlyList<string> Disagreements(string? model, int reportedMaxSlices,
            bool diversityAllowed, out string? standDown)
        {
            var found = new List<string>();
            standDown = null;
            if (string.IsNullOrWhiteSpace(model))
            {
                standDown = "the radio has not reported its model";
                return found;
            }
            ModelInfo row = ModelInfo.GetModelInfoForModel(model);
            if (ReferenceEquals(row, ModelInfo.GetModelInfoForModel("DEFAULT")))
            {
                standDown = "FlexLib's model table has no row for " + model + " (it would answer with its DEFAULT row)";
                return found;
            }

            int tableSlices = row.SliceList?.Count ?? 0;
            if (reportedMaxSlices > 0 && tableSlices > 0 && reportedMaxSlices != tableSlices)
                found.Add("slices: FlexLib holds " + reportedMaxSlices + " for this radio, the model table lists " + tableSlices);

            if (diversityAllowed != row.IsDiversityAllowed)
                found.Add("diversity: FlexLib says " + (diversityAllowed ? "allowed" : "not allowed")
                    + " for this radio, the model table says " + (row.IsDiversityAllowed ? "allowed" : "not allowed"));
            return found;
        }

        /// <summary>The line written to the trace, always one.</summary>
        internal static string TraceLine(string? model, int reportedMaxSlices, bool diversityAllowed)
        {
            var found = Disagreements(model, reportedMaxSlices, diversityAllowed, out string? standDown);
            if (standDown != null) return "ModelCapabilityCheck: nothing compared, " + standDown;
            string slices = reportedMaxSlices > 0
                ? ""
                : "; FlexLib carries no slice count for this radio (0 means not reported, as on every SmartLink connect), so slices were not compared";
            return found.Count == 0
                ? "ModelCapabilityCheck: " + model + " agrees with FlexLib's model table" + slices
                : "ModelCapabilityCheck: " + model + " DISAGREES with FlexLib's model table: "
                  + string.Join("; ", found) + slices + ". The radio's own answer is the one in use.";
        }

        /// <summary>
        /// Write the line for this connect. Called once, from the end of the
        /// connect's station establishment, when the radio has answered.
        /// </summary>
        internal static void TraceForConnect(Radio? radio)
        {
            if (radio == null) return;
            try
            {
                string line = TraceLine(radio.Model, radio.MaxSlices, radio.DiversityIsAllowed);
                Tracing.TraceLine(line, line.Contains("DISAGREES") ? TraceLevel.Warning : TraceLevel.Info);
            }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("ModelCapabilityCheck: could not compare, " + ex.Message, TraceLevel.Info);
            }
        }
    }
}
