using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The decisions behind the Fixer Tool's two transmit-audio stages — the
    /// injected probes (stage 3) and the spoken check (stage 4) — kept pure so
    /// every branch an operator can be told about is testable without a radio,
    /// a microphone or a transmitter in the room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split from <see cref="FixerTransmitAudioBoundary"/> for the same reason
    /// <see cref="TxTuneProbe"/> is split from <see cref="TxTuneProbeRunner"/>:
    /// the part that decides what a reading MEANS must be testable without a
    /// radio, and the part that keys one cannot be. Nothing here touches
    /// FlexBase, keys anything, or sleeps.
    /// </para>
    /// <para>
    /// The verdict vocabulary is not invented here either.
    /// <see cref="TxProbeSet"/> owns what a set of probes supports,
    /// <see cref="TxToneLadder"/> owns what a ladder means, and
    /// <see cref="TxDifferential"/> owns the injected-versus-spoken
    /// comparison. This file only maps raw readings into their words.
    /// </para>
    /// </remarks>
    public static class TxAudioProbe
    {
        /// <summary>
        /// The single tone's frequency — the same 1 kHz everything else in the
        /// chain checks measures against, so one number means one thing.
        /// </summary>
        public const int SingleToneHz = TxToneLadder.ReferenceHz;

        /// <summary>
        /// The level every injected probe tone plays at, dBFS. The tone
        /// generator's own default, restated here so the evidence can name it
        /// without reading it back from a generator that might have been left
        /// at another level by the Audio Workshop.
        /// </summary>
        public const float InjectLevelDb = -10f;

        /// <summary>
        /// How long to let a tone sound before believing the meter, per rung.
        /// The transmit meters update a few times a second (measured on the
        /// bench 8600, 2026-08-20), so most of a rung's airtime is spent
        /// letting the reading become the tone's rather than the previous
        /// moment's.
        /// </summary>
        public const int RungSettleMs = 900;

        /// <summary>
        /// The window the reading is actually taken over, after the settle.
        /// Settle plus window equals <see cref="TxToneLadder.RungMs"/> so the
        /// airtime an operator is promised stays the airtime they get.
        /// </summary>
        public const int RungWindowMs = TxToneLadder.RungMs - RungSettleMs;

        /// <summary>
        /// The longest the voice probe plays, however long the recording is.
        /// The question is "does a voice-shaped signal arrive", and twelve
        /// seconds of a known recording answers it; a reference take can
        /// legitimately run minutes, and transmitting all of it would spend
        /// the run's key-down budget answering nothing extra.
        /// </summary>
        public const int VoiceCapMs = 12000;

        /// <summary>
        /// How long the spoken check listens while the operator talks. Long
        /// enough for a sentence at a comfortable pace after the "speak now"
        /// cue has been heard; short enough that holding a transmitter keyed
        /// through it is not a burden.
        /// </summary>
        public const int SpokenListenMs = 8000;

        /// <summary>Did this SC_MIC reading arrive at the radio?</summary>
        /// <remarks>
        /// One threshold for every stage, deliberately —
        /// <see cref="TxDifferential.ReachedRadioDbfs"/>, the same line the
        /// injected-versus-spoken comparison draws. Two thresholds would let
        /// the same reading pass one stage and fail another.
        /// </remarks>
        public static bool Reached(double scMicDb)
            => scMicDb > TxDifferential.ReachedRadioDbfs;

        /// <summary>
        /// Judge one injected probe from what the SC_MIC meter did while it
        /// played.
        /// </summary>
        /// <param name="probe">Which probe this reading belongs to.</param>
        /// <param name="meterUpdated">
        /// Did the meter actually produce readings DURING this probe? A stale
        /// number from an earlier moment is not a measurement of this signal,
        /// and reading it as one would let a dead meter pass a dead chain.
        /// </param>
        /// <param name="scMicDb">The peak SC_MIC reading over the probe's window.</param>
        /// <param name="context">What was playing, for the evidence line.</param>
        /// <remarks>
        /// A probe whose meter never updated comes back
        /// <see cref="TxProbeSet.Outcome.NotAttempted"/> — not
        /// <c>DidNotReach</c> — because <see cref="TxProbeSet.Judge"/> counts
        /// only Reached and DidNotReach as votes, and a probe whose instrument
        /// was silent must not vote. The detail says plainly that RF went out
        /// and nothing was measured, so the report cannot read "not attempted"
        /// as "nothing happened".
        /// </remarks>
        public static TxProbeSet.ProbeResult Judge(TxProbeSet.Probe probe, bool meterUpdated,
                                                   double scMicDb, string context)
        {
            string what = string.IsNullOrWhiteSpace(context) ? "" : " (" + context.Trim() + ")";

            if (!meterUpdated)
                return new TxProbeSet.ProbeResult(probe, TxProbeSet.Outcome.NotAttempted,
                    "it was transmitted" + what + ", but the radio's transmit audio meter "
                    + "(SC_MIC) never updated while it played, so nothing was measured and "
                    + "this probe casts no vote");

            string reading = "SC_MIC peaked at " + Db(scMicDb) + what;
            return Reached(scMicDb)
                ? new TxProbeSet.ProbeResult(probe, TxProbeSet.Outcome.ReachedRadio, reading)
                : new TxProbeSet.ProbeResult(probe, TxProbeSet.Outcome.DidNotReach,
                    reading + ", below the " + Db(TxDifferential.ReachedRadioDbfs)
                    + " line that counts as arriving");
        }

        /// <summary>
        /// Judge the ladder as one probe in the set, carrying its own
        /// rung-by-rung story as the detail.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The set's question is binary — did audio arrive — so the ladder's
        /// vote comes from its best IN-BAND rung: tones placed inside the
        /// operator's own transmit filter are the ones that should arrive, and
        /// the deliberate out-of-band controls must not be allowed to fail the
        /// probe for doing exactly what they were placed to do.
        /// </para>
        /// <para>
        /// The shape of the ladder — filter seen, no filter seen, something
        /// shaping the audio — is <see cref="TxToneLadder"/>'s judgement and
        /// travels verbatim in the detail rather than being re-decided here.
        /// </para>
        /// </remarks>
        public static TxProbeSet.ProbeResult LadderProbe(bool referenceReported,
                                                         double referenceDb,
                                                         IReadOnlyList<TxToneLadder.RungReading> readings,
                                                         TxToneLadder.Passband band)
        {
            var got = (readings ?? Array.Empty<TxToneLadder.RungReading>()).ToList();
            var inBand = got.Where(r => r.Reported
                                     && r.Rung.Placement == TxToneLadder.Placement.InPassband)
                            .ToList();

            if (!referenceReported)
                return new TxProbeSet.ProbeResult(TxProbeSet.Probe.ToneLadder,
                    TxProbeSet.Outcome.NotAttempted,
                    // "measuring tone", not "reference" (#443) — see
                    // TxToneLadder.Describe for why the word moved.
                    "the " + SingleToneHz + " hertz measuring tone never read on the SC_MIC meter, "
                    + "so the ladder had nothing to measure its rungs against");

            if (inBand.Count == 0)
                return new TxProbeSet.ProbeResult(TxProbeSet.Probe.ToneLadder,
                    TxProbeSet.Outcome.NotAttempted,
                    "no rung inside the transmit filter (" + band + ") produced a reading, "
                    + "so the ladder measured nothing and casts no vote");

            double best = inBand.Max(r => r.Db);
            string story = "transmit filter " + band + ". "
                         + TxToneLadder.Describe(referenceDb, got);

            return Reached(best)
                ? new TxProbeSet.ProbeResult(TxProbeSet.Probe.ToneLadder,
                    TxProbeSet.Outcome.ReachedRadio,
                    "the strongest in-band rung peaked at " + Db(best) + ". " + story)
                : new TxProbeSet.ProbeResult(TxProbeSet.Probe.ToneLadder,
                    TxProbeSet.Outcome.DidNotReach,
                    "no in-band rung rose above " + Db(TxDifferential.ReachedRadioDbfs)
                    + " — the strongest read " + Db(best) + ". " + story);
        }

        /// <summary>
        /// Why the spoken check cannot run right now, or empty when it can.
        /// </summary>
        /// <remarks>
        /// Deliberately NARROWER than <c>FlexBase.TxTonePathTrouble</c>, which
        /// gates the injected probes. The injected probes ride the PC audio
        /// path and need all of it; a spoken voice reaches the radio EITHER
        /// through that path (transmit audio from PC) or through the radio's
        /// own microphone jack, and SC_MIC sits downstream of that selection,
        /// so it measures both honestly. Only two states leave a voice no path
        /// at all: CW, which has no transmit audio path in any direction, and
        /// a PC mic selection with PC audio off.
        /// </remarks>
        public static string SpokenPathTrouble(string mode, string micSource, bool pcAudioOn)
        {
            string m = (mode ?? "").Trim();
            if (m.StartsWith("CW", StringComparison.OrdinalIgnoreCase))
                return "The radio is in CW mode, where there is no transmit audio path — a "
                     + "voice cannot reach the radio in CW whatever the microphone does. "
                     + "Switch to a voice mode and run this step again.";

            if (string.Equals((micSource ?? "").Trim(), "PC", StringComparison.OrdinalIgnoreCase)
                && !pcAudioOn)
                return "Transmit audio is set to come from this computer, but PC audio is "
                     + "off, so your voice has no path to the radio. Turn PC audio on and "
                     + "run this step again.";

            return "";
        }

        /// <summary>
        /// One run's meter capture as evidence lines, conditions first — a
        /// reading with no recorded conditions cannot be reproduced by anyone
        /// (#217, #188).
        /// </summary>
        public static string DescribeSample(TxDifferential.TxRunSample s)
        {
            if (s == null || !s.Ran) return "No meter capture was taken.";

            var sb = new StringBuilder();
            sb.Append("Meters while keyed, at ").Append(s.Frequency)
              .Append(", ").Append(s.Mode)
              .Append(", antenna ").Append(s.Antenna).Append(": ");
            sb.Append(string.Join(", ", s.Meters.Select(m => m.Describe())));
            sb.Append('.');
            return sb.ToString();
        }

        /// <summary>
        /// The spoken run read against the injected one — the comparison the
        /// two stages exist to feed. <see cref="TxDifferential"/> owns every
        /// verdict in it; this only lays the lines out.
        /// </summary>
        public static string SpokenComparison(TxDifferential.TxRunSample injected,
                                              TxDifferential.TxRunSample spoken)
        {
            if (injected == null || !injected.Ran)
                return "The injected check has not run in this test, so there is no "
                     + "microphone-bypassed run to hold this against. Running the injected "
                     + "check closes that comparison.";

            var sb = new StringBuilder();
            sb.AppendLine("Against the injected run, meter by meter:");
            foreach (TxDifferential.MeterComparison c in TxDifferential.Compare(injected, spoken))
                sb.AppendLine("  " + c.Line());
            sb.Append(TxDifferential.OperatorSummary(injected, spoken));
            return sb.ToString();
        }

        private static string Db(double v)
            => double.IsNaN(v) ? "not measured"
                               : v.ToString("0.#", CultureInfo.InvariantCulture) + " dBFS";
    }
}
