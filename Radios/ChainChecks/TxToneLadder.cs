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
        /// The ladder. Six rungs, low to high.
        /// </summary>
        /// <remarks>
        /// Chosen for what each one can reveal, not for even spacing:
        /// 200 sits below any sane SSB filter; 300 is the classic low corner;
        /// 700 and 1500 bracket where speech intelligibility actually lives;
        /// 2400 sits just inside a 2.7 kHz filter; 3200 is above it. The pair
        /// at the ends are the controls, the four in the middle are the
        /// measurement.
        /// <para>
        /// 1000 Hz is deliberately NOT here as a rung of its own — it is the
        /// reference the rest are compared against, and it is what the single
        /// tone has always used, so it stays the level-setting tone rather than
        /// becoming one data point among six.
        /// </para>
        /// </remarks>
        public static readonly Rung[] Rungs =
        {
            new Rung(200,  Placement.BelowPassband,
                     "below any normal transmit filter — should come back quieter"),
            new Rung(300,  Placement.InPassband,
                     "the low corner most SSB filters are built around"),
            new Rung(700,  Placement.InPassband,
                     "low speech, where a voice gets its weight"),
            new Rung(1500, Placement.InPassband,
                     "where most of the intelligibility lives"),
            new Rung(2400, Placement.InPassband,
                     "just inside the high corner of a 2.7 kilohertz filter"),
            new Rung(3200, Placement.AbovePassband,
                     "above a normal transmit filter — should come back quieter"),
        };

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

        /// <summary>Total airtime for a full ladder including the reference.</summary>
        public static int TotalMs => (Rungs.Length + 1) * RungMs;

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
