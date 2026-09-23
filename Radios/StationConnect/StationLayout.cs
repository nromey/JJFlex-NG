using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.StationConnect
{
    /// <summary>One slice's frequency and mode, in the operator's own words.</summary>
    public sealed class SliceLayoutEntry
    {
        public SliceLayoutEntry() { }
        public SliceLayoutEntry(long freqHz, string mode) { FreqHz = freqHz; Mode = mode ?? ""; }
        public long FreqHz { get; set; }
        public string Mode { get; set; } = "";
        public override string ToString() => FreqHz + " Hz " + Mode;
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
