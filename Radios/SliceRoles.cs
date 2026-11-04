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
                          string transmitLetter, ulong transmitHz, bool same,
                          bool selectedIsDiagnostic = false)
        {
            _selectedLetter = selectedLetter ?? "";
            SelectedHz = selectedHz;
            HasTransmit = hasTransmit;
            _transmitLetter = transmitLetter ?? "";
            TransmitHz = transmitHz;
            Same = same;
            SelectedIsDiagnostic = selectedIsDiagnostic;
        }

        /// <summary>
        /// The selected slice is one a check made for itself (Loopback's or
        /// the off-air take's listening slice), not one the operator chose.
        /// </summary>
        public bool SelectedIsDiagnostic { get; }

        private readonly string _selectedLetter;
        private readonly string _transmitLetter;

        /// <summary>The selected slice's letter, or empty (never null, even
        /// for a default value).</summary>
        public string SelectedLetter => _selectedLetter ?? "";

        /// <summary>The selected slice's own frequency, in hertz.</summary>
        public ulong SelectedHz { get; }

        /// <summary>True when a selected slice was read; false with none, when
        /// <see cref="SelectedLetter"/> is empty and <see cref="SelectedHz"/>
        /// is not a frequency.</summary>
        public bool HasSelected => SelectedLetter.Length > 0;

        /// <summary>False when the radio has designated no transmit slice.</summary>
        public bool HasTransmit { get; }

        /// <summary>The transmit slice's letter, or empty.</summary>
        public string TransmitLetter => _transmitLetter ?? "";

        /// <summary>The transmit slice's frequency, in hertz, or 0.</summary>
        public ulong TransmitHz { get; }

        /// <summary>True when the selected slice IS the transmit slice.</summary>
        public bool Same { get; }

        /// <summary>
        /// True when a transmission would go out on a slice other than the
        /// selected one: the case every "which slice" sentence exists for.
        /// </summary>
        public bool TransmitsElsewhere => HasTransmit && !Same;

        /// <summary>
        /// Read both roles off the rig as one coherent snapshot of slice
        /// objects (<see cref="FlexBase.ReadSliceRoles"/>): the transmit role
        /// is the radio's designation, never a list position, and each letter
        /// and frequency comes from the same slice object. With no designated
        /// transmit slice the snapshot says so and names none.
        /// </summary>
        public static SliceRoles Of(FlexBase rig) => rig == null ? default : rig.ReadSliceRoles();

        /// <summary>
        /// True when this reading describes the same split as
        /// <paramref name="other"/>: the same transmit slice, the same selected
        /// slice, and the transmit slice on the same frequency. What
        /// <see cref="TransmitSliceNotice"/> compares to decide whether a door
        /// is still keying the place it announced.
        /// </summary>
        public bool SamePlaceAs(SliceRoles other) =>
            HasTransmit == other.HasTransmit
            && string.Equals(TransmitLetter, other.TransmitLetter, StringComparison.Ordinal)
            && TransmitHz == other.TransmitHz
            && string.Equals(SelectedLetter, other.SelectedLetter, StringComparison.Ordinal)
            && Same == other.Same;
    }

    /// <summary>
    /// The push-to-talk announcements, with or without their slice (#496).
    /// Pure, so the choice is tested without a radio; the controller speaks
    /// what this returns.
    /// </summary>
    /// <remarks>
    /// Ruled 2026-09-01 (#496, recorded in the archived #497): extend the
    /// EXISTING announcements, all three, "transmitting on slice a",
    /// "receiving on slice b". Transmitting and locked name the TRANSMIT
    /// slice, which is where the RF goes; receiving names the SELECTED slice,
    /// which is where the operator is. With the switch off, or with no slice
    /// to name, the three sentences are exactly the ones that shipped.
    /// DRAFT PROSE (#496), awaiting Noel.
    /// </remarks>
    public static class PttSliceWords
    {
        public static string Transmitting(SliceRoles roles, bool nameSlice) =>
            nameSlice && roles.HasTransmit && roles.TransmitLetter.Length > 0
                ? Lexicon.Get("audio.ptt.announce_transmitting_on", ("slice", roles.TransmitLetter))
                : Lexicon.Get("audio.ptt.announce_transmitting");

        public static string Locked(SliceRoles roles, bool nameSlice) =>
            nameSlice && roles.HasTransmit && roles.TransmitLetter.Length > 0
                ? Lexicon.Get("audio.ptt.announce_locked_on", ("slice", roles.TransmitLetter))
                : Lexicon.Get("audio.ptt.announce_locked");

        public static string Receiving(SliceRoles roles, bool nameSlice) =>
            nameSlice && roles.SelectedLetter.Length > 0
                ? Lexicon.Get("audio.ptt.announce_receiving_on", ("slice", roles.SelectedLetter))
                : Lexicon.Get("audio.ptt.announce_receiving");
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
            // No slice read at all, or only a transmit slice: a missing role
            // is unavailable, never a frequency under an empty letter.
            if (!roles.HasSelected && !roles.HasTransmit)
                return Lexicon.Get("connect.home.frequency_unavailable");
            if (!roles.HasSelected)
                return Lexicon.Get("connect.home.frequency_transmit",
                    ("freqText", format(roles.TransmitHz)), ("slice", roles.TransmitLetter));
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
