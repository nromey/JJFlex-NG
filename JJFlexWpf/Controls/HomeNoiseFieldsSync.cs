using System.Collections.Generic;
using System.Linq;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// What Home's receive noise fields must do at a poll so they show what is
/// true, whatever road changed it (#1148): the menu, the noise layer, a
/// preset, a mode group or a setup. Until then the receive poll refreshed the
/// VALUES of the fields it showed and never asked WHICH engine they showed, so
/// Home could show an old engine as off while another was on, until Side or
/// another rebuilding action was used. The transmit twin was fixed in Track H2
/// (repair round 1, <c>PollTransmitNoise</c>); this is the same rule for
/// receive, kept free of WPF so it can be tested without a window.
/// </summary>
internal static class HomeNoiseFieldsSync
{
    internal enum Action
    {
        /// <summary>Only the values: the fields already show the right engine and levers.</summary>
        RefreshValues,
        /// <summary>Show another engine on the same side: the slot's engine changed elsewhere.</summary>
        ShowSlotEngine,
        /// <summary>Rebuild the shown engine's own fields: its lever set changed (NR2's trained values come and go, Advanced was opened elsewhere).</summary>
        RebuildEngine,
    }

    /// <summary>
    /// Decide. A computer side shows the engine its slot holds (a choice still
    /// settling is the slot's to the fields, <see cref="NoiseController.SlotEngine"/>,
    /// so an arrow on the Engine field is never undone). The radio side lists
    /// switches, each with its own On, so there is no "which engine" to follow.
    /// A rebuild that would take away the lever the operator is standing on
    /// waits until they leave the levers; their values still refresh.
    /// </summary>
    /// <param name="side">The family the Side field shows; null for the radio.</param>
    /// <param name="shownEngine">The engine the fields were built for.</param>
    /// <param name="slotEngine">The engine the slot holds now (for a computer side), or "".</param>
    /// <param name="listedEngines">The engines the Engine field lists for this side. A slot engine it does not list (one this build cannot show there) is not followed, or every poll would rebuild.</param>
    /// <param name="shownLevers">The lever keys the fields hold, in build order.</param>
    /// <param name="wantedLevers">The lever keys the shown engine has now, in the same order.</param>
    /// <param name="standingInLevers">Whether keyboard focus is inside the levers.</param>
    internal static Action Decide(DenoiseFamily? side, string shownEngine, string slotEngine, IEnumerable<string> listedEngines,
        IEnumerable<string> shownLevers, IEnumerable<string> wantedLevers, bool standingInLevers)
    {
        if (standingInLevers) return Action.RefreshValues;
        if (side != null && slotEngine.Length > 0 && slotEngine != shownEngine && listedEngines.Contains(slotEngine)) return Action.ShowSlotEngine;
        return shownLevers.SequenceEqual(wantedLevers) ? Action.RefreshValues : Action.RebuildEngine;
    }

    /// <summary>
    /// The lever keys Home builds for an engine, in its build order: the
    /// everyday levers, then the Advanced ones when Advanced is open there.
    /// Listen to what was removed is a checkbox, not a lever field, and is
    /// left out on both sides of the comparison.
    /// </summary>
    internal static IEnumerable<string> WantedLevers(NoiseController noise, string id)
    {
        var levers = noise.Levers(id, includeAdvanced: false).AsEnumerable();
        if (noise.Find(id) is { Side: DenoiseSide.Pc } && noise.IsAdvanced(id))
            levers = levers.Concat(noise.Levers(id, includeAdvanced: true).Where(l => l.Advanced));
        return levers.Where(l => l.Kind != NoiseLeverKind.ListenRemoved).Select(l => l.Key).ToList();
    }
}
