using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.ChainChecks
{
    /// <summary>
    /// A short ladder of tones across the speech band, instead of one tone in
    /// the middle of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noel, 2026-08-25:</b> "You could do a few tones over the range of
    /// human voice / radio filtering, that'd also be a good tone."
    /// </para>
    /// <para>
    /// A single 1 kHz tone answers one question — did audio arrive — and sails
    /// straight through the middle of every SSB filter ever made while doing
    /// it. A ladder answers a different and more useful question: what does
    /// this chain do to a signal ACROSS the band the operator's voice actually
    /// occupies. A microphone rolling off, an EQ doing something nobody asked
    /// for, a filter that is not where the radio says it is — none of those are
    /// visible at 1 kHz and all of them are visible here.
    /// </para>
    /// <para>
    /// <b>Two of the rungs are outside the passband on purpose, and they are
    /// the most valuable two.</b> A standard SSB transmit filter runs roughly
    /// 300 Hz to 2.7 kHz. A tone below the bottom edge and a tone above the top
    /// edge SHOULD come back attenuated — so if they do not, the filter is not
    /// where it is supposed to be, or the measurement is being taken somewhere
    /// ahead of it. That is a positive control on the instrument itself: a
    /// ladder where every rung reads the same is not a flat chain, it is a
    /// suspicious result. Same discipline as everything else in this project —
    /// a measurement that can only produce one answer is not a measurement.
    /// </para>
    /// <para>
    /// <b>It also needs no text-to-speech.</b> Every rung is arithmetic, so this
    /// is a complete, honest reference on a machine with no usable voice — which
    /// is the fallback that lets us stop shipping a rendered WAV (#219).
    /// </para>
    /// <para>
    /// <b>One level throughout.</b> Every rung is generated at the same
    /// amplitude, so a difference between rungs is the CHAIN's, never the
    /// generator's. The tone source is phase-continuous across frequency
    /// changes, so the ladder glides rather than clicking between steps.
    /// </para>
    /// </remarks>
    public static class TxToneLadder
    {
        /// <summary>Where a rung sits relative to a normal SSB transmit filter.</summary>
        public enum Placement
        {
            /// <summary>Below the filter's low edge. Expected to be attenuated.</summary>
            BelowPassband,
            /// <summary>Inside the filter. Expected to pass at full level.</summary>
            InPassband,
            /// <summary>Above the filter's high edge. Expected to be attenuated.</summary>
            AbovePassband,
        }

        /// <summary>One tone in the ladder, and why it is there.</summary>
        public readonly struct Rung
        {
            public readonly int Hz;
            public readonly Placement Placement;
            /// <summary>What this rung is for, in words an operator can read.</summary>
            public readonly string Purpose;

            public Rung(int hz, Placement placement, string purpose)
            {
                Hz = hz;
                Placement = placement;
                Purpose = purpose ?? "";
            }
        }

        /// <summary>
        /// The passband the ladder was derived against, or a record that it
        /// could not be read.
        /// </summary>
        /// <remarks>
        /// A ladder result without the passband it was measured against cannot
        /// be re-read later and cannot be compared between two operators. The
        /// cuts travel with the measurement for the same reason the antenna
        /// port does (#188).
        /// </remarks>
        public readonly struct Passband
        {
            public readonly bool Known;
            public readonly int LowHz;
            public readonly int HighHz;

            private Passband(bool known, int low, int high)
            { Known = known; LowHz = low; HighHz = high; }

            public static Passband Read(int lowHz, int highHz)
                => new Passband(true, lowHz, highHz);

            public static readonly Passband Unknown = new Passband(false, 0, 0);

            public int WidthHz => Known ? HighHz - LowHz : 0;

            public override string ToString()
                => Known
                    ? LowHz.ToString(CultureInfo.InvariantCulture) + " to "
                      + HighHz.ToString(CultureInfo.InvariantCulture) + " Hz"
                    : "not read";
        }

        /// <summary>
        /// How far outside the measured passband the control rungs sit.
        /// </summary>
        /// <remarks>
        /// Far enough that a filter with any real skirt has clearly rolled off,
        /// and not so far that the tone leaves the range the transmit chain can
        /// carry at all. A control that is merely at the edge proves nothing:
        /// filters do not stop dead at their stated corner.
        /// </remarks>
        public const int ControlMarginHz = 250;

        /// <summary>Lowest tone worth asking for. Below this the chain, the
        /// meters and most loudspeakers stop being informative together.</summary>
        public const int MinToneHz = 50;

        /// <summary>Highest tone worth asking for.</summary>
        public const int MaxToneHz = 6000;

        /// <summary>How many rungs sit inside the passband.</summary>
        public const int InPassbandRungs = 4;

        /// <summary>
        /// Build the ladder for a MEASURED passband. Pure; the caller reads
        /// TXFilterLow and TXFilterHigh from the radio and passes them in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This replaced a hardcoded six-rung ladder that assumed 300 Hz to
        /// 2.7 kHz.</b> That assumption was written in the code's own comments
        /// as though it were a fact; it is a SETTING, and the radio reports it.
        /// On a wide filter both controls fell INSIDE the passband, came back
        /// unattenuated, and the ladder reported a broken filter on a radio
        /// whose filter was fine. On a narrow one, genuine in-band rungs read
        /// as attenuated. Either way a confident wrong answer, which is the one
        /// outcome worse than no test (#221).
        /// </para>
        /// <para>
        /// The controls are placed relative to the MEASURED edges, so they are
        /// outside the passband by construction rather than by luck. The
        /// in-band rungs are spread across the real width instead of sitting at
        /// remembered frequencies that may or may not be inside it.
        /// </para>
        /// </remarks>
        public static Rung[] DeriveRungs(Passband band)
        {
            if (!band.Known || band.WidthHz <= 0) return Array.Empty<Rung>();

            var rungs = new List<Rung>(InPassbandRungs + 2);

            int below = Clamp(band.LowHz - ControlMarginHz);
            if (below < band.LowHz)
                rungs.Add(new Rung(below, Placement.BelowPassband,
                    "below your transmit filter's low edge of "
                    + band.LowHz.ToString(CultureInfo.InvariantCulture)
                    + " hertz — should come back quieter"));

            // Spread inside the passband, avoiding both edges: a rung sitting
            // exactly on a corner is neither in nor out, and reads as whichever
            // the filter's skirt happens to make it.
            for (int i = 1; i <= InPassbandRungs; i++)
            {
                int hz = band.LowHz + (int)Math.Round(band.WidthHz * (i / (double)(InPassbandRungs + 1)));
                hz = Clamp(hz);
                rungs.Add(new Rung(hz, Placement.InPassband,
                    "inside your transmit filter — should come back at full level"));
            }

            int above = Clamp(band.HighHz + ControlMarginHz);
            if (above > band.HighHz)
                rungs.Add(new Rung(above, Placement.AbovePassband,
                    "above your transmit filter's high edge of "
                    + band.HighHz.ToString(CultureInfo.InvariantCulture)
                    + " hertz — should come back quieter"));

            return rungs.ToArray();
        }

        private static int Clamp(int hz)
            => hz < MinToneHz ? MinToneHz : (hz > MaxToneHz ? MaxToneHz : hz);

        /// <summary>The frequency every rung is measured against.</summary>
        public const int ReferenceHz = 1000;

        /// <summary>How long each rung is held, in milliseconds.</summary>
        /// <remarks>
        /// Long enough for the radio's meters to settle and report — they update
        /// a few times a second — and short enough that six rungs plus the
        /// reference is a transmission an operator can hold a key through
        /// without their arm aching or a timeout ladder waking up.
        /// </remarks>
        public const int RungMs = 1500;

        /// <summary>
        /// Total airtime for a ladder, including the reference tone.
        /// </summary>
        /// <remarks>
        /// Takes the rungs rather than reading a fixed list, because the ladder
        /// is now derived from the operator's actual passband and a wide filter
        /// does not produce the same airtime as a narrow one. An operator is
        /// told how long their radio will be transmitting before it starts, so
        /// this has to be the real figure and not a remembered one.
        /// </remarks>
        public static int TotalMsFor(IReadOnlyList<Rung> rungs)
            => ((rungs?.Count ?? 0) + 1) * RungMs;

        /// <summary>
        /// How far down a rung must read before it counts as attenuated.
        /// </summary>
        /// <remarks>
        /// Six decibels: unmistakably down rather than meter jitter, and well
        /// short of what a real filter skirt does at these offsets, so an
        /// out-of-band rung that fails to clear it is genuinely surprising
        /// rather than marginal.
        /// </remarks>
        public const double AttenuatedByDb = 6.0;

        /// <summary>One rung as it came back.</summary>
        public readonly struct RungReading
        {
            public readonly Rung Rung;
            public readonly double Db;
            public readonly bool Reported;

            public RungReading(Rung rung, double db, bool reported)
            {
                Rung = rung;
                Db = db;
                Reported = reported;
            }
        }

        /// <summary>What the ladder as a whole supports.</summary>
        public enum LadderVerdict
        {
            /// <summary>Not enough rungs reported to say anything.</summary>
            Incomplete,
            /// <summary>In-band rungs passed, out-of-band rungs attenuated. What a filter looks like.</summary>
            LooksLikeAFilter,
            /// <summary>Every rung read alike, including the ones outside the passband.</summary>
            NoFilterSeen,
            /// <summary>In-band rungs are not uniform — something is shaping the audio.</summary>
            ShapedInBand,
        }

        /// <summary>
        /// Read the ladder.
        /// </summary>
        /// <param name="reference">The 1 kHz reading everything is measured against.</param>
        /// <param name="readings">One entry per rung, in any order.</param>
        /// <remarks>
        /// <para>
        /// The out-of-band pair is checked FIRST and deliberately, because it
        /// is the control. If neither end attenuates, the in-band numbers may
        /// be perfectly consistent and still be measuring something other than
        /// the transmitted signal — and reporting a tidy in-band result in that
        /// situation would be exactly the kind of confident wrong answer this
        /// project keeps finding.
        /// </para>
        /// </remarks>
        public static LadderVerdict Read(double reference, IReadOnlyList<RungReading> readings)
        {
            if (readings == null) return LadderVerdict.Incomplete;

            var got = readings.Where(r => r.Reported).ToList();
            var inBand = got.Where(r => r.Rung.Placement == Placement.InPassband).ToList();
            var outOfBand = got.Where(r => r.Rung.Placement != Placement.InPassband).ToList();

            // Need both controls and most of the measurement to say anything.
            if (outOfBand.Count < 2 || inBand.Count < 3) return LadderVerdict.Incomplete;

            bool endsAttenuate = outOfBand.All(r => reference - r.Db >= AttenuatedByDb);
            if (!endsAttenuate) return LadderVerdict.NoFilterSeen;

            double lowest = inBand.Min(r => r.Db);
            double highest = inBand.Max(r => r.Db);
            if (highest - lowest >= AttenuatedByDb) return LadderVerdict.ShapedInBand;

            return LadderVerdict.LooksLikeAFilter;
        }

        /// <summary>
        /// The ladder in words, for the operator. Observations first; the
        /// interpretation is named as one.
        /// </summary>
        public static string Describe(double reference, IReadOnlyList<RungReading> readings)
        {
            var got = (readings ?? Array.Empty<RungReading>()).ToList();
            var lines = new List<string>
            {
                "Reference tone at " + ReferenceHz + " hertz read "
                + reference.ToString("0.#", CultureInfo.CurrentCulture) + ".",
            };

            foreach (RungReading r in got.OrderBy(r => r.Rung.Hz))
            {
                if (!r.Reported)
                {
                    lines.Add(r.Rung.Hz + " hertz: no reading.");
                    continue;
                }
                double rel = r.Db - reference;
                lines.Add(r.Rung.Hz + " hertz: " + r.Db.ToString("0.#", CultureInfo.CurrentCulture)
                        + " (" + (rel >= 0 ? "+" : "") + rel.ToString("0.#", CultureInfo.CurrentCulture)
                        + " against the reference) — " + r.Rung.Purpose + ".");
            }

            lines.Add("");
            lines.Add(Read(reference, got) switch
            {
                LadderVerdict.LooksLikeAFilter =>
                    "The tones inside the speech band came back at much the same level, and the two "
                    + "outside it came back quieter. That is the shape a working transmit filter makes.",
                LadderVerdict.ShapedInBand =>
                    "The tones inside the speech band did NOT come back at the same level, so "
                    + "something is shaping your audio across the band — an equaliser, a processor, "
                    + "or a microphone with a strong response of its own.",
                LadderVerdict.NoFilterSeen =>
                    "The tones outside the speech band came back at the same level as the ones "
                    + "inside it. A transmit filter should have made them quieter, so either the "
                    + "filter is not doing what it is set to, or this reading is being taken before "
                    + "the filter rather than after it. Treat the rest of this ladder with caution.",
                _ =>
                    "Not enough of the ladder came back to read it. Run it again with the radio "
                    + "fully up, so its meters are reporting.",
            });

            return string.Join(Environment.NewLine, lines);
        }
    }
}
