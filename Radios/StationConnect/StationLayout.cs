using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.StationConnect
{
    /// <summary>
    /// Whether a recorded slice was muted. <b>Three states, and the third is
    /// the point</b> (#762).
    /// </summary>
    /// <remarks>
    /// <para><c>NotRecorded</c> is zero so that a layout written before mute
    /// was captured deserialises to "we never looked" rather than to
    /// "unmuted". A plain <c>bool</c> would have every historic record assert
    /// that every slice was audible, which is a claim nobody made, and the
    /// put-back in #499 would act on it.</para>
    /// <para>Same reasoning as the licence three-state settled on 2026-10-18:
    /// silence is not a no. The difference between "no" and "nothing was
    /// reported" has to survive into storage, or it is lost at the first
    /// restart.</para>
    /// </remarks>
    public enum SliceMuteState
    {
        NotRecorded = 0,
        Unmuted,
        Muted,
    }

    /// <summary>One slice's letter, frequency, mode and mute, in the
    /// operator's own words.</summary>
    public sealed class SliceLayoutEntry
    {
        public SliceLayoutEntry() { }

        public SliceLayoutEntry(long freqHz, string mode)
            : this(freqHz, mode, "", SliceMuteState.NotRecorded) { }

        public SliceLayoutEntry(long freqHz, string mode, string letter, SliceMuteState mute)
        {
            FreqHz = freqHz;
            Mode = mode ?? "";
            Letter = letter ?? "";
            Mute = mute;
        }

        /// <summary>
        /// Copy every field. <b>Use this to copy an entry; never re-list the
        /// fields by hand.</b>
        /// </summary>
        /// <remarks>
        /// Added 2026-10-18 (#762) because copying was spelled
        /// <c>new SliceLayoutEntry(s.FreqHz, s.Mode)</c> in THREE places — the
        /// write in <c>RecordStationLayout</c>, its skip-the-write comparison,
        /// and the read in <c>StationLayoutOf</c> — and adding a field left all
        /// three silently discarding it. Nothing failed: the build passed, the
        /// trace line printed the new field correctly, and only the stored copy
        /// was wrong. Two of the three were found by reading and the third by a
        /// test, which is the honest measure of how visible the shape is.
        /// <para>A field added to this type is now correct at every copy site
        /// for free, rather than correct wherever somebody remembered.</para>
        /// </remarks>
        public SliceLayoutEntry(SliceLayoutEntry other)
        {
            if (other == null) return;
            FreqHz = other.FreqHz;
            Mode = other.Mode ?? "";
            Letter = other.Letter ?? "";
            // The saved text, so a mute state a newer build wrote travels with
            // the copy (#847).
            MuteXml = other.MuteXml;
        }

        public long FreqHz { get; set; }
        public string Mode { get; set; } = "";

        /// <summary>The slice's own letter, so a recorded line can be read
        /// against an operator saying "A and C are muted". The list was
        /// positional until 2026-10-18 and a three-slice line could not be
        /// matched to a report (#762).</summary>
        public string Letter { get; set; } = "";

        [System.Xml.Serialization.XmlIgnore]
        public SliceMuteState Mute { get => _mute.Value; set => _mute.Value = value; }

        /// <summary>The saved text of <see cref="Mute"/>, read tolerantly (#847).</summary>
        [System.Xml.Serialization.XmlElement("Mute")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public string MuteXml { get => _mute.Text; set => _mute.Text = value; }
        private readonly TolerantXmlEnum<SliceMuteState> _mute = new(nameof(Mute), SliceMuteState.NotRecorded);

        /// <summary>
        /// Reads as the operator would say it. <b>Unmuted is stated rather
        /// than implied by silence</b>, so a reader can tell a slice we
        /// measured as audible from one whose mute we never captured.
        /// </summary>
        public override string ToString()
        {
            string head = string.IsNullOrEmpty(Letter) ? "" : Letter + " ";
            string tail =
                Mute == SliceMuteState.Muted ? " muted"
                : Mute == SliceMuteState.Unmuted ? " unmuted"
                : " (mute not recorded)";
            return head + FreqHz + " Hz " + Mode + tail;
        }
    }

    /// <summary>
    /// The owner's station as THEIR OWN CLIENT last saw it: the frequencies
    /// and modes of their slices, in slice order. Recorded on the owner's
    /// machine, because nothing on the radio will tell us: FlexLib's profile
    /// parser exposes a profile's NAME (list and current) and nothing of its
    /// contents, and the radio's only export is an opaque LAN-only file
    /// (Track G2, part two — the read the ruling asked for does not exist in
    /// the API, and this is the honest substitute: the owner's frequencies
    /// from the owner's last session, not the saved profile's).
    /// </summary>
    public sealed class StationLayout
    {
        /// <summary>
        /// How many slices a connect asks for when this computer holds no
        /// remembered layout for the radio. RULED by Noel 2026-09-22 21:26,
        /// in his words: <i>"The radio by default for some reason gives you
        /// 14.100 default ... I suppose you could tell the ham and then just
        /// give 'em 14.100, better to give them something rather than
        /// nothing."</i> So two slices, clipped to free capacity, left on
        /// whatever frequency the RADIO chooses — this app never names one.
        /// The same allocation a first-time guest gets.
        /// </summary>
        public const int SlicesWithNoRememberedLayout = 2;

        /// <summary>
        /// The most slices a connect asks for on a radio the operator has NOT
        /// declared theirs — every time, whatever this computer remembers for
        /// it. RULED by Noel 2026-09-23 04:44, in his words: <i>"if I'm coming
        /// into a radio that's not mine, the max number of slices it grabs on
        /// startup should be two."</i> A guest taking four of someone's slices
        /// can starve the radio's owner, and two is enough to work with, so
        /// the first-time-guest default becomes the rule. It is a ceiling
        /// clipped to free capacity, never a quota: nothing is padded up to it
        /// (#587). An OWNER with company keeps their remembered layout instead
        /// — their radio, their saved station.
        /// </summary>
        public const int SlicesForAGuest = 2;

        public List<SliceLayoutEntry> Slices { get; set; } = new List<SliceLayoutEntry>();
        public DateTime RecordedUtc { get; set; }
        public string ProfileName { get; set; } = "";

        public bool IsEmpty => Slices == null || Slices.Count == 0;

        public override string ToString() =>
            (IsEmpty ? "no slices" : string.Join(", ", Slices.Select(s => s.ToString())))
            + (string.IsNullOrEmpty(ProfileName) ? "" : " (profile '" + ProfileName + "')");
    }

    public enum PlacementStop
    {
        NotAttempted,
        /// <summary>No layout is known for this radio on this machine, so
        /// nothing was tuned. It is a reason, not a refusal to allocate: the
        /// slices are still asked for, at the radio's own defaults (ruled
        /// 2026-09-22 21:26; see <see cref="StationLayout.SlicesWithNoRememberedLayout"/>).</summary>
        NoLayoutKnown,
        /// <summary>No free slice was obtained to place anything on.</summary>
        NoSlices,
        /// <summary>Every obtained slice received a frequency and reported it.</summary>
        Completed,
        /// <summary>A placement was sent and the radio did not acknowledge it
        /// within the bound; nothing after it was placed.</summary>
        Unconfirmed,
        /// <summary>The radio answered the tune with an error; nothing after
        /// it was placed. The note carries the radio's text.</summary>
        Rejected,
        Refused,
        Cancelled,
    }

    /// <summary>What the per-client frequency placement did.</summary>
    public sealed class PlacementResult
    {
        public int Wanted;
        public int Placed;
        public int Sent;
        public PlacementStop Stop = PlacementStop.NotAttempted;
        public string Note = "";
        public override string ToString() =>
            Stop + " wanted=" + Wanted + " sent=" + Sent + " placed=" + Placed + (string.IsNullOrEmpty(Note) ? "" : " (" + Note + ")");
    }
}
