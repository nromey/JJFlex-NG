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

        /// <summary>What to do about the mode the radio is in.</summary>
        public enum ModeAction
        {
            /// <summary>The mode is already one the ladder can measure.</summary>
            RunAsIs,
            /// <summary>Switch to <see cref="ModePlan.SwitchTo"/>, then restore.</summary>
            SwitchAndRestore,
            /// <summary>Do not run. <see cref="ModePlan.Reason"/> says why.</summary>
            Refuse,
        }

        /// <summary>The decision, and enough to act on and to report.</summary>
        public readonly struct ModePlan
        {
            public readonly ModeAction Action;
            public readonly string CurrentMode;
            public readonly string SwitchTo;
            public readonly string Reason;

            private ModePlan(ModeAction a, string current, string to, string why)
            { Action = a; CurrentMode = current ?? ""; SwitchTo = to ?? ""; Reason = why ?? ""; }

            public static ModePlan AsIs(string current)
                => new ModePlan(ModeAction.RunAsIs, current, current, "");
            public static ModePlan Switch(string current, string to, string why)
                => new ModePlan(ModeAction.SwitchAndRestore, current, to, why);
            public static ModePlan No(string current, string why)
                => new ModePlan(ModeAction.Refuse, current, "", why);
        }

        /// <summary>
        /// Below this, the band convention is lower sideband; at or above it,
        /// upper. Only matters when transmitting into a real antenna, but it
        /// costs nothing to be right.
        /// </summary>
        public const ulong SidebandCrossoverHz = 10_000_000UL;

        /// <summary>
        /// Decide what to do about the current mode. Pure.
        /// </summary>
        /// <remarks>
        /// <para>
        /// RULED BY NOEL 2026-08-25: "switch to an appropriate mode if the rig
        /// is in FM or something weird, then switch back after test." So the
        /// ladder drives the radio into a mode it can measure rather than
        /// declining — with one exception.
        /// </para>
        /// <para>
        /// <b>CW is refused rather than switched.</b> Not because switching is
        /// hard, but because CW has NO TRANSMIT AUDIO PATH at all: switching
        /// out of it to run a tone ladder would measure a path that operator
        /// never uses, and report it as though it were theirs. The TUNE probe
        /// (#222) works in CW natively and is the honest answer there.
        /// </para>
        /// <para>
        /// <b>The caller must read the filter cuts AFTER acting on this, never
        /// before.</b> TX filter cuts are per-mode, so cuts read before a
        /// switch describe a passband the test is not going to run in — which
        /// reintroduces the very bug DeriveRungs exists to fix, one layer
        /// deeper and harder to see, because the code would then contain a
        /// truthful-looking "we read the real value" that is still wrong.
        /// </para>
        /// </remarks>
        public static ModePlan PlanForMode(string currentMode, ulong txFrequencyHz)
        {
            string mode = (currentMode ?? "").Trim().ToUpperInvariant();

            if (mode.Length == 0)
                return ModePlan.No(currentMode,
                    "the radio did not report which mode it is in, so it is not known "
                    + "whether a tone test would measure anything meaningful");

            if (mode == "CW")
                return ModePlan.No(currentMode,
                    "in CW there is no transmit audio path to measure. Switching out of CW "
                    + "would test a path you do not use. Use the transmitter check instead, "
                    + "which works in CW.");

            if (mode == "USB" || mode == "LSB")
                return ModePlan.AsIs(currentMode);

            string want = ConventionalSideband(txFrequencyHz);
            return ModePlan.Switch(currentMode, want,
                "a tone ladder measures a voice transmit filter, and " + currentMode
                + " does not have one in the same sense. Switching to " + want
                + " for the test and putting " + currentMode + " back afterwards.");
        }

        /// <summary>The sideband convention for a frequency.</summary>
        public static string ConventionalSideband(ulong hz)
            => hz >= SidebandCrossoverHz ? "USB" : "LSB";

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
                // "MEASURING TONE", NOT "REFERENCE TONE" (#443). The word
                // "reference" was doing two jobs a few lines apart in one
                // report: this 1000 hertz yardstick, and the shipped reference
                // recording the same stage plays through the transmitter. Heard
                // aloud, one word meant two things. The recording keeps the name
                // — it is a shipped file, a picker label and a spoken script —
                // and the tone, which appears in three sentences, gives it up.
                "The " + ReferenceHz + " hertz measuring tone read "
                + reference.ToString("0.#", CultureInfo.CurrentCulture) + ".",
            };

            // NOTHING ARRIVED: SAY SO ONCE, DO NOT READ OUT THE FLOOR (#443).
            //
            // Noel, running this on Don's radio: "the reference voice says that
            // it's a reference and didn't transmit and starts reading hertz
            // which is weird." The report announced that no rung rose above the
            // arrival line, then read five rungs that were every one of them at
            // the meter floor and identical to one another, then said it could
            // not be read. Three statements of one fact, two of them made of
            // numbers that carry none.
            //
            // The trailing verdict is dropped here too, and that is the more
            // important half. Its wording — "run it again with the radio fully
            // up, so its meters are reporting" — blames the meters, and the
            // meters WERE reporting: −150 is a reading. Sending an operator to
            // re-key a transmitter over a false diagnosis costs them RF.
            bool anythingArrived = got.Any(r => r.Reported && r.Db > TxDifferential.ReachedRadioDbfs);
            if (got.Count > 0 && !anythingArrived)
            {
                lines.Add("Nothing came back from any rung of the ladder: all "
                    + got.Count.ToString(CultureInfo.CurrentCulture)
                    + " read at or below "
                    + TxDifferential.ReachedRadioDbfs.ToString("0.#", CultureInfo.CurrentCulture)
                    + " dBFS, which is the level this check treats as nothing arriving. They are "
                    + "not listed one by one, because at the floor every rung says the same thing "
                    + "and none of them says anything about your transmit filter.");
                return string.Join(Environment.NewLine, lines);
            }

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
                        + " against the measuring tone) — " + r.Rung.Purpose + ".");
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
