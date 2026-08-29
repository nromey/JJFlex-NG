using System;
using System.Collections.Generic;
using System.Globalization;

namespace Radios.ChainChecks
{
    /// <summary>
    /// What we actually saw arriving from the radio, summarised over a short
    /// run of readings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot, taken and finished. Nothing here updates itself, for the same
    /// reason <see cref="DiagnosticFact"/> does not: a report whose numbers
    /// changed while it was being read aloud would be worse than one that is a
    /// few seconds old.
    /// </para>
    /// <para>
    /// <b>Every field is a count of readings, never a claim about seconds.</b>
    /// The underlying figures are published by FlexLib on its own one-second
    /// timer and we sample them on ours; the two are not phase-locked, so the
    /// same published second can be read twice and a second can be missed. "Ten
    /// readings about a second apart" is true whatever the phase. "Ten seconds
    /// of audio" would not be, and it is the kind of small overstatement that a
    /// support engineer is entitled to catch us in.
    /// </para>
    /// </remarks>
    public sealed class RxTrafficReading
    {
        internal RxTrafficReading() { }

        /// <summary>How many readings this summarises. Zero means we have not
        /// looked yet, which is not the same answer as zero traffic.</summary>
        public int SampleCount { get; internal set; }

        /// <summary>When the oldest reading in the window was taken, UTC.
        /// Null when there are none.</summary>
        public DateTime? OldestAtUtc { get; internal set; }

        /// <summary>When the newest reading was taken, UTC. Null when there are
        /// none.</summary>
        public DateTime? NewestAtUtc { get; internal set; }

        /// <summary>Highest Opus receive rate seen in the window, kbps.</summary>
        public int AudioPeakKbps { get; internal set; }

        /// <summary>The most recent Opus receive rate, kbps.</summary>
        public int AudioLatestKbps { get; internal set; }

        /// <summary>How many of the readings carried any receive audio at all.
        /// This is the number that answers "are audio packets coming in".</summary>
        public int AudioReadingsWithTraffic { get; internal set; }

        /// <summary>
        /// Readings at the FRONT of the window from before the first one that
        /// carried audio — the warm-up. The sampler starts on connect and audio
        /// takes a few seconds to begin streaming, so a first run after any
        /// connect holds a run of zeros at the front that mean nothing (#368).
        /// Zero when the first reading already carried audio, and zero when NO
        /// reading carried audio — with no audio ever seen there is no "before
        /// it began" to count, and claiming one would be a guess.
        /// </summary>
        public int LeadingZeroReadings { get; internal set; }

        /// <summary>
        /// Readings with no audio AFTER audio had begun — the holes. This is
        /// the number that tells a warm-up from a dropout: audio missing from
        /// readings scattered through a run means the sound was cutting out,
        /// which on a remote connection is what a weak or congested network
        /// looks like. Zero when no reading carried audio, for the same reason
        /// as <see cref="LeadingZeroReadings"/>.
        /// </summary>
        public int AudioGapReadings { get; internal set; }

        /// <summary>
        /// How many readings there have been from the first one that carried
        /// audio to the newest — the honest denominator for the consistency
        /// count, with the warm-up left out. Zero when no reading carried
        /// audio; use <see cref="SampleCount"/> then, because with no audio the
        /// whole window is the story.
        /// </summary>
        public int ReadingsSinceAudioBegan =>
            AudioReadingsWithTraffic > 0 ? SampleCount - LeadingZeroReadings : 0;

        /// <summary>Highest total receive rate seen in the window, kbps — every
        /// stream the radio sends us, audio included.</summary>
        public int TotalPeakKbps { get; internal set; }

        /// <summary>The most recent total receive rate, kbps.</summary>
        public int TotalLatestKbps { get; internal set; }

        /// <summary>Highest meter-traffic rate seen in the window, kbps.</summary>
        public int MeterPeakKbps { get; internal set; }

        /// <summary>The most recent meter-traffic rate, kbps.</summary>
        public int MeterLatestKbps { get; internal set; }

        /// <summary>True when there is anything to report at all.</summary>
        public bool HasSamples => SampleCount > 0;

        /// <summary>
        /// The window in words, for the evidence block: how many readings, how
        /// far apart, and when they finished. Local times, because the rest of
        /// the report is in local time and a mixed report is a trap.
        /// </summary>
        public string DescribeWindow()
        {
            if (!HasSamples) return "no readings have been taken yet";

            string when = NewestAtUtc.HasValue
                ? NewestAtUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : "an unknown time";

            if (SampleCount == 1)
                return "one reading, taken at " + when;

            string from = OldestAtUtc.HasValue
                ? OldestAtUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : "an unknown time";

            return SampleCount + " readings about a second apart, from " + from + " to " + when;
        }
    }

    /// <summary>
    /// A short rolling record of how much traffic the radio has been sending,
    /// so the receive check can report a measurement rather than a setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The receive report used to be nine facts and every
    /// one of them was something WE had set — mutes, levels, a routing switch. It
    /// could be entirely correct while no audio had ever reached the computer,
    /// which is the first thing a radio manufacturer's support desk would ask
    /// about. FlexLib has counted the arriving bytes all along and published a
    /// rate once a second; nothing in this application had ever read it.
    /// </para>
    /// <para>
    /// <b>Why a rolling window rather than one read.</b> The published figure is
    /// reset every second, so reading it once gives a one-second sample and an
    /// unlucky moment reads as an absence of audio. A run of readings makes the
    /// difference between "nothing arrived in the instant we looked" and "nothing
    /// has arrived at all", and only the second is worth telling anybody about.
    /// </para>
    /// <para>
    /// <b>Why it is polled rather than subscribed.</b> FlexLib raises
    /// PropertyChanged only when the value CHANGES. A steady stream publishes
    /// once and then goes quiet, and a rate that has been zero since connect
    /// never raises anything at all — so a subscription would be silent in
    /// exactly the two cases the report needs to tell apart. Polling is the only
    /// honest reader of that property.
    /// </para>
    /// <para>
    /// Pure, and holds no reference to a radio, so every path through it is
    /// reachable from a unit test with no hardware and no threads.
    /// </para>
    /// </remarks>
    public sealed class RxTrafficWindow
    {
        /// <summary>How many readings are kept. Half a minute at roughly one a
        /// second — long enough that a quiet instant cannot masquerade as an
        /// absence, short enough that the answer still describes now.</summary>
        public const int DefaultCapacity = 30;

        private struct Sample
        {
            public int Audio;
            public int Total;
            public int Meter;
            public DateTime AtUtc;
        }

        private readonly object _lock = new object();
        private readonly Queue<Sample> _samples = new Queue<Sample>();
        private readonly int _capacity;

        public RxTrafficWindow() : this(DefaultCapacity) { }

        public RxTrafficWindow(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
        }

        /// <summary>How many readings are kept before the oldest is dropped.</summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Record one reading. Called from a timer thread; read from whichever
        /// thread runs the check, hence the lock.
        /// </summary>
        public void Add(int audioKbps, int totalKbps, int meterKbps, DateTime atUtc)
        {
            var s = new Sample
            {
                Audio = audioKbps,
                Total = totalKbps,
                Meter = meterKbps,
                AtUtc = atUtc,
            };

            lock (_lock)
            {
                _samples.Enqueue(s);
                while (_samples.Count > _capacity) _samples.Dequeue();
            }
        }

        /// <summary>
        /// Forget everything. Called when a radio goes away, so a reconnect can
        /// never be described with the previous radio's numbers.
        /// </summary>
        public void Clear()
        {
            lock (_lock) _samples.Clear();
        }

        /// <summary>
        /// Summarise what is in the window. Never null: with nothing recorded it
        /// returns a reading that says so, and the fact source turns that into
        /// "could not be read" rather than into a zero.
        /// </summary>
        public RxTrafficReading Snapshot()
        {
            var r = new RxTrafficReading();

            lock (_lock)
            {
                if (_samples.Count == 0) return r;

                bool first = true;
                bool audioSeen = false;
                foreach (Sample s in _samples)
                {
                    r.SampleCount++;

                    if (first) { r.OldestAtUtc = s.AtUtc; first = false; }
                    r.NewestAtUtc = s.AtUtc;

                    if (s.Audio > r.AudioPeakKbps) r.AudioPeakKbps = s.Audio;
                    if (s.Total > r.TotalPeakKbps) r.TotalPeakKbps = s.Total;
                    if (s.Meter > r.MeterPeakKbps) r.MeterPeakKbps = s.Meter;

                    // "Any at all", not "at least one kilobit". The published
                    // rate is an integer cast of bytes times 0.008, so anything
                    // under 125 bytes in a second reads as zero — which is why
                    // this counts what the reading SAID rather than pretending
                    // to count packets we never saw.
                    //
                    // A zero BEFORE the first audio is warm-up — the sampler
                    // starts on connect, the stream a few seconds later — and a
                    // zero AFTER it is a hole in the stream. Two different
                    // reports (#368): the first misled the measurement's very
                    // first reader on a radio that was working perfectly, and
                    // the second is the single most useful thing this window
                    // can say, because holes are what a weak network looks like.
                    if (s.Audio > 0)
                    {
                        r.AudioReadingsWithTraffic++;
                        audioSeen = true;
                    }
                    else if (audioSeen)
                    {
                        r.AudioGapReadings++;
                    }
                    else
                    {
                        r.LeadingZeroReadings++;
                    }

                    r.AudioLatestKbps = s.Audio;
                    r.TotalLatestKbps = s.Total;
                    r.MeterLatestKbps = s.Meter;
                }

                // With no audio ever seen, "before it began" is a claim about
                // an event that never happened. Report the plain count and let
                // the reader of SampleCount have the whole window.
                if (!audioSeen) r.LeadingZeroReadings = 0;
            }

            return r;
        }
    }
}
