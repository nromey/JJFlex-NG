using System;

namespace Radios
{
    /// <summary>
    /// Which slice the frequency key reads (#642). Stored as an int in the
    /// operator's audio settings; Both is 0 so a missing value is the default.
    /// </summary>
    public enum FrequencyKeySlices
    {
        /// <summary>The transmit slice first, then the selected slice; one
        /// sentence when they are the same slice. The default.</summary>
        Both = 0,

        /// <summary>The transmit slice only.</summary>
        Transmit = 1,

        /// <summary>The selected (active) slice only, which is the slice the
        /// Frequency field and the Slice field show.</summary>
        Selected = 2,
    }

    /// <summary>
    /// The two slices an operator can mean when they ask "where am I": the
    /// SELECTED slice (the radio's active slice, the one Home's fields show)
    /// and the TRANSMIT slice. Read once, so every sentence that says which
    /// slice transmits uses one reading and one vocabulary (#642, #496, #863).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The words are the slice status's words.</b> The full slice status
    /// (<see cref="RadioStatusBuilder.BuildFullSliceStatus"/>) has said
    /// "Slice A, selected, transmit" since the slice list was built, and the
    /// letters come from <see cref="FlexBase.VFOToLetter"/>. These sentences
    /// reuse both rather than invent a third phrasing.
    /// </para>
    /// <para>
    /// <b>There is no "listening slice", by ruling.</b> Noel, 2026-10-18: "you
    /// could be listening to multiple slices ... that's a concept that's not
    /// really possible in current JJ Flexible." Selected and transmit are the
    /// only two roles a slice can be named by.
    /// </para>
    /// </remarks>
    public readonly struct SliceRoles
    {
        public SliceRoles(string selectedLetter, ulong selectedHz, bool hasTransmit,
                          string transmitLetter, ulong transmitHz, bool same)
        {
            SelectedLetter = selectedLetter ?? "";
            SelectedHz = selectedHz;
            HasTransmit = hasTransmit;
            TransmitLetter = transmitLetter ?? "";
            TransmitHz = transmitHz;
            Same = same;
        }

        /// <summary>The selected slice's letter.</summary>
        public string SelectedLetter { get; }

        /// <summary>The selected slice's own frequency, in hertz.</summary>
        public ulong SelectedHz { get; }

        /// <summary>False when the radio has designated no transmit slice.</summary>
        public bool HasTransmit { get; }

        /// <summary>The transmit slice's letter, or empty.</summary>
        public string TransmitLetter { get; }

        /// <summary>The transmit slice's frequency, in hertz, or 0.</summary>
        public ulong TransmitHz { get; }

        /// <summary>True when the selected slice IS the transmit slice.</summary>
        public bool Same { get; }

        /// <summary>
        /// True when a transmission would go out on a slice other than the
        /// selected one: the case every "which slice" sentence exists for.
        /// </summary>
        public bool TransmitsElsewhere => HasTransmit && !Same;

        /// <summary>Read both roles off the rig, once.</summary>
        public static SliceRoles Of(FlexBase rig)
        {
            if (rig == null) return default;
            int rx = rig.RXVFO;
            int tx = rig.TXVFO;
            bool hasTx = rig.ValidVFO(tx);
            return new SliceRoles(
                rig.VFOToLetter(rx), rig.RXFrequency,
                hasTx, hasTx ? rig.VFOToLetter(tx) : "", hasTx ? rig.TXFrequency : 0UL,
                hasTx && tx == rx);
        }
    }

    /// <summary>
    /// What the frequency key says (#642). Ruled by Noel 2026-10-18: a setting
    /// with three values, "Active, transmit, or both", Both speaking the
    /// transmit slice first, one sentence when the two are the same slice, and
    /// never shortened by verbosity: verbosity shapes words, it does not
    /// decide which facts survive.
    /// </summary>
    public static class FrequencyKeyReadout
    {
        /// <summary>The operator's choice, pushed here from the audio
        /// settings when they load or are saved.</summary>
        public static FrequencyKeySlices Setting { get; set; } = FrequencyKeySlices.Both;

        /// <summary>
        /// The sentence, pure: the setting, the two roles, and the caller's
        /// own frequency formatting (the same one the Frequency field uses).
        /// </summary>
        /// <remarks>
        /// When the slices are the same, or no transmit slice is designated,
        /// every setting collapses to the one sentence F has always said,
        /// "Frequency 14.250, slice A": the common case costs nothing. Only
        /// when the transmit slice is elsewhere does a sentence name a role,
        /// and then it always does, so a number is never left to mean one of
        /// two slices. DRAFT PROSE (#642), awaiting Noel.
        /// </remarks>
        public static string Sentence(FrequencyKeySlices setting, SliceRoles roles, Func<ulong, string> format)
        {
            if (format == null) throw new ArgumentNullException(nameof(format));
            if (!roles.TransmitsElsewhere)
                return Lexicon.Get("connect.home.frequency",
                    ("freqText", format(roles.SelectedHz)), ("slice", roles.SelectedLetter));

            switch (setting)
            {
                case FrequencyKeySlices.Transmit:
                    return Lexicon.Get("connect.home.frequency_transmit",
                        ("freqText", format(roles.TransmitHz)), ("slice", roles.TransmitLetter));
                case FrequencyKeySlices.Selected:
                    return Lexicon.Get("connect.home.frequency_selected",
                        ("freqText", format(roles.SelectedHz)), ("slice", roles.SelectedLetter));
                default:
                    return Lexicon.Get("connect.home.frequency_both",
                        ("transmitFreq", format(roles.TransmitHz)), ("transmitSlice", roles.TransmitLetter),
                        ("selectedFreq", format(roles.SelectedHz)), ("selectedSlice", roles.SelectedLetter));
            }
        }
    }
}
