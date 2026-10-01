#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Flex.Smoothlake.FlexLib;

namespace Radios.Alarms
{
    /// <summary>
    /// One meter as the radio described it on THIS connection: the radio's
    /// index, its name, source, source index, units and advertised range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain value copied out of a <see cref="MeterReading"/> (or a FlexLib
    /// <c>Meter</c>) so the alarm engine can be tested without constructing
    /// either. The numeric <see cref="Index"/> belongs to the current
    /// connection only and is never used as identity across connections —
    /// see <see cref="MeterSelector"/> for the identity that is persisted.
    /// </para>
    /// <para>
    /// <see cref="Low"/> and <see cref="High"/> describe the radio's DISPLAY
    /// range, not a guaranteed safe range: a finite reading outside them is
    /// still evidence (design section 2), and the observation flags it.
    /// </para>
    /// </remarks>
    public sealed record MeterDescriptor(
        int Index,
        string Name,
        string Description,
        string Source,
        int SourceIndex,
        MeterUnits Units,
        double Low,
        double High)
    {
        /// <summary>The radio's descriptor for this reading, as the inventory holds it.</summary>
        public static MeterDescriptor Of(MeterReading r) => new MeterDescriptor(
            r.Index, r.Name ?? "", r.Description ?? "", (r.Source ?? "").ToUpperInvariant(),
            r.SourceIndex, r.Units, r.Low, r.High);

        /// <summary>The radio's descriptor for a FlexLib meter, as delivered with a reading.</summary>
        public static MeterDescriptor Of(Meter m) => new MeterDescriptor(
            m.Index, m.Name ?? "", m.Description ?? "", (m.Source ?? "").ToUpperInvariant(),
            m.SourceIndex, m.Units, m.Low, m.High);

        /// <summary>Source and source index the way the trace prints them, e.g. "TX-:4".</summary>
        public string SourceLabel =>
            Source + ":" + SourceIndex.ToString(CultureInfo.InvariantCulture);

        /// <summary>Name with the radio's description beside it when there is one: "PATEMP (PA Temperature)".</summary>
        public string Label =>
            Description.Length == 0 ? Name : Name + " (" + Description + ")";

        /// <summary>Units in words, or empty for a unitless meter — the inventory's vocabulary, not a second one.</summary>
        public string UnitsText => MeterReading.UnitsText(Units);

        /// <summary>True when a finite value lies outside the advertised range.</summary>
        public bool IsOutOfRange(float value) =>
            float.IsFinite(value) && (value < Low || value > High);
    }

    /// <summary>
    /// Where a selector landed against the current inventory.
    /// </summary>
    public enum MeterSelectorStatus
    {
        /// <summary>Exactly one meter matched name, source, source index and units.</summary>
        Resolved,

        /// <summary>No meter of that name is published on this connection.</summary>
        Missing,

        /// <summary>More than one meter matched the full descriptor — a real case,
        /// see the remarks on <see cref="MeterInventory.Find"/>. Nothing is chosen.</summary>
        Ambiguous,

        /// <summary>The name is published, but not from the saved source or source
        /// index — firmware or a slice layout moved it. A re-selection is required.</summary>
        SourceChanged,

        /// <summary>The meter is there, but its units are not the units the alarm
        /// was defined in. Its threshold means nothing until re-selected.</summary>
        UnitChanged,
    }

    /// <summary>What resolving a selector against an inventory found.</summary>
    public sealed record MeterSelectorResolution(
        MeterSelectorStatus Status,
        MeterDescriptor? Match,
        IReadOnlyList<MeterDescriptor> Candidates)
    {
        public bool IsResolved => Status == MeterSelectorStatus.Resolved && Match != null;
    }

    /// <summary>
    /// The persisted identity of a meter: name, source, source index and unit,
    /// with the last description kept as context. NEVER the radio's numeric
    /// index — the 6300 of the 2026-09-06 trace publishes PATEMP at index 9 and the bench 8600 at 11,
    /// and either can move with firmware.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution uses every copy of the name (<see cref="MeterInventory.FindAll"/>'s
    /// semantics), never the first match: an 8600 with four slices publishes
    /// four byte-identical SC_MIC descriptors. Where more than one meter
    /// matches the whole descriptor the selector is <see cref="MeterSelectorStatus.Ambiguous"/>
    /// and nothing is chosen — no averaging, no highest-reading, no silent
    /// binding to another slice (design section 2).
    /// </para>
    /// </remarks>
    public sealed record MeterSelector(
        string Name,
        string Source,
        int SourceIndex,
        MeterUnits Units,
        string Description)
    {
        public static MeterSelector From(MeterDescriptor d) =>
            new MeterSelector(d.Name, d.Source, d.SourceIndex, d.Units, d.Description);

        /// <summary>Same name, source and source index; units are checked separately so a unit change can be named.</summary>
        public bool MatchesIdentity(MeterDescriptor d) =>
            string.Equals(Name, d.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Source, d.Source, StringComparison.OrdinalIgnoreCase)
            && SourceIndex == d.SourceIndex;

        /// <summary>Identity and units both match: this is the meter the alarm was defined on.</summary>
        public bool Matches(MeterDescriptor d) => MatchesIdentity(d) && Units == d.Units;

        /// <summary>
        /// Find this selector's meter among what the radio currently publishes.
        /// Pure: pass whatever descriptor list the caller holds.
        /// </summary>
        public MeterSelectorResolution Resolve(IEnumerable<MeterDescriptor> inventory)
        {
            var sameName = new List<MeterDescriptor>();
            var sameIdentity = new List<MeterDescriptor>();
            foreach (MeterDescriptor d in inventory)
            {
                if (!string.Equals(Name, d.Name, StringComparison.OrdinalIgnoreCase)) continue;
                sameName.Add(d);
                if (MatchesIdentity(d)) sameIdentity.Add(d);
            }

            if (sameIdentity.Count == 1)
            {
                MeterDescriptor only = sameIdentity[0];
                return only.Units == Units
                    ? new MeterSelectorResolution(MeterSelectorStatus.Resolved, only, sameIdentity)
                    : new MeterSelectorResolution(MeterSelectorStatus.UnitChanged, null, sameIdentity);
            }
            if (sameIdentity.Count > 1)
                return new MeterSelectorResolution(MeterSelectorStatus.Ambiguous, null, sameIdentity);
            if (sameName.Count > 0)
                return new MeterSelectorResolution(MeterSelectorStatus.SourceChanged, null, sameName);
            return new MeterSelectorResolution(MeterSelectorStatus.Missing, null, Array.Empty<MeterDescriptor>());
        }

        /// <summary>Name and source for a list row or a trace: "PATEMP, TX-:4".</summary>
        public string Label =>
            Name + ", " + Source + ":" + SourceIndex.ToString(CultureInfo.InvariantCulture);

        /// <summary>Units in words, or empty.</summary>
        public string UnitsText => MeterReading.UnitsText(Units);
    }
}
