using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Keys the transmitter with TUNE and asks one question: does this radio
    /// transmit at all, with the audio chain entirely out of the picture?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the positive control for every transmit measurement we take.</b>
    /// Everything else in ChainChecks measures the audio chain and the
    /// transmitter together and then attributes a failure to the audio chain by
    /// assumption. TUNE puts a carrier out with no microphone, no PortAudio
    /// device, no Opus encode, no VITA stream, no mic profile and no
    /// conditioning — so it separates the two, and the logic runs both ways:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>TUNE makes power, voice does not — the fault is in
    ///   the audio chain, and the differential's stage-by-stage reading is
    ///   worth reading.</description></item>
    ///   <item><description>TUNE makes no power — this was never an audio
    ///   problem. Stop testing microphones. Look at the antenna, the PA, an
    ///   interlock, the band, feature gating, TX inhibit, or a slice that is not
    ///   TX-enabled.</description></item>
    /// </list>
    /// <para>
    /// The second branch is the one that earns this its place. Without it, an
    /// operator whose transmitter is broken or misconfigured gets walked
    /// through the entire microphone diagnostic and told, confidently and
    /// wrongly, that their audio is at fault.
    /// </para>
    /// <para>
    /// It also works in CW, where the tone ladder cannot run at all — CW has no
    /// transmit audio path to measure, so TUNE is the only honest probe there.
    /// </para>
    /// <para>
    /// <b>Pure decision logic, no FlexLib.</b> Same split as
    /// <see cref="TxDifferential"/>: this half is exhaustively testable without
    /// a radio in the room, and the keying half is not. Mixing them would make
    /// the part that decides what an operator is told the part nobody can test.
    /// </para>
    /// </remarks>
    public static class TxTuneProbe
    {
        /// <summary>
        /// Meters read during the tune, always the same set.
        /// </summary>
        /// <remarks>
        /// <b>REFPWR, not REVPWR.</b> The radio publishes reflected power under
        /// REFPWR; REVPWR does not exist and a lookup for it silently finds
        /// nothing, which reads as "this radio has no reflected power meter"
        /// rather than as a typo. That exact mistake is documented in
        /// TxChainFacts around the meter-revpwr inventory entry.
        /// <para>
        /// A fixed set, reported present-or-absent rather than omitted, so a
        /// reading that did not happen stays distinguishable from a reading of
        /// zero. Same rule as <see cref="TxDifferential.Watched"/>.
        /// </para>
        /// </remarks>
        public static readonly string[] Watched =
        {
            "FWDPWR", "REFPWR", "SWR", "ALC",
        };

        /// <summary>How long to hold the carrier. Long enough for the transmit
        /// meters to appear and update — they arrive with the transmit chain and
        /// refresh a few times a second — and short enough to be thermally
        /// uninteresting.</summary>
        public const int TuneMs = 2000;

        /// <summary>
        /// Forward power at or below this is "no power at all" rather than "a
        /// little power". Deliberately generous: the question here is binary —
        /// did the transmitter do ANYTHING — not whether it hit its target.
        /// </summary>
        public const double NoPowerWatts = 0.5;

        /// <summary>
        /// Computed SWR at or above this says the load is suspect. Judged on
        /// SWR because that is the number an operator and a FlexRadio engineer
        /// both already think in — but on SWR WORKED OUT from forward and
        /// reflected power, never on the radio's own SWR meter. See
        /// <see cref="Assess"/>.
        /// </summary>
        public const double SwrSuspect = 3.0;

        /// <summary>
        /// Fallback threshold, used only when SWR cannot be derived. Reflected
        /// power as a share of forward: a fraction cannot blow up where SWR
        /// runs to infinity near the end of its range. Into the dummy load this
        /// measured 0.05 percent; into an empty connector on the same radio, 76.
        /// </summary>
        /// <remarks>
        /// <b>DELIBERATELY STRICTER than the live warning</b> — half of
        /// <see cref="TransmitSafety.ReflectedWarnPercent"/>, and derived from
        /// it so the two can only move together. The live warning fires while a
        /// human is holding the key with intent; this fires during a bounded
        /// UNATTENDED probe into a load the operator has only declared, and a
        /// probe that is more cautious than a person is the right way round.
        /// <para>The RATIO is a pin, not a ruling: whether the probe should sit
        /// at half, at parity, or somewhere else is an open question for Noel
        /// (#237). Until it is ruled, the derivation keeps the difference
        /// visible as a decision rather than leaving it to read as drift —
        /// which is exactly how this constant came to disagree with the other
        /// two in the first place.</para>
        /// </remarks>
        public const double ReflectedSuspectPercent = TransmitSafety.ReflectedWarnPercent / 2.0;

        /// <summary>Why a tune did not happen. Kept distinct because the
        /// remedies are completely different and an operator told the wrong one
        /// wastes the session.</summary>
        public enum SkipReason
        {
            None = 0,

            /// <summary>No radio, or it stopped answering.</summary>
            RadioNotReachable,

            /// <summary>The operator has not declared what is connected to the
            /// antenna port. Transmitting without knowing is the one thing this
            /// probe must never do on its own — see task #180.</summary>
            LoadNotDeclared,

            /// <summary>Something is already transmitting. Keying on top of it
            /// would measure the other thing, not this one.</summary>
            AlreadyTransmitting,

            /// <summary>The operator declined, or stopped it part-way.</summary>
            Cancelled,

            /// <summary>
            /// The host would not allow the measurement. Distinct from the
            /// reasons above, which are all facts about the STATION: this one
            /// is a fact about the software, and collapsing it into
            /// <see cref="Cancelled"/> would tell an operator they stopped
            /// something they never started.
            /// </summary>
            RefusedByHost,
        }

        /// <summary>What the tune established.</summary>
        public enum Verdict
        {
            /// <summary>Nothing was measured. Look at <see cref="Result.Skipped"/>.</summary>
            NotRun = 0,

            /// <summary>Power appeared. The transmitter works; anything wrong is
            /// downstream of here or in the audio chain.</summary>
            MakesPower,

            /// <summary>Power appeared, but a large share came back. The
            /// transmitter is working into something it does not like.</summary>
            MakesPowerLoadSuspect,

            /// <summary>The transmitter was keyed and no power appeared. This is
            /// NOT an audio fault and must not be reported as one.</summary>
            NoPower,

            /// <summary>Keyed, but the radio never reported forward power at
            /// all — so we cannot say whether power appeared. Different from
            /// <see cref="NoPower"/>, and the difference matters: absence of a
            /// meter is not a measurement of zero.</summary>
            NoForwardPowerMeter,
        }

        /// <summary>One meter reading, or a record that it was not reported.</summary>
        public readonly struct Reading
        {
            public string Name { get; }
            public bool Reported { get; }
            public double Value { get; }
            public string Units { get; }

            private Reading(string name, bool reported, double value, string units)
            {
                Name = name; Reported = reported; Value = value; Units = units ?? "";
            }

            public static Reading Got(string name, double value, string units)
                => new Reading(name, true, value, units);

            public static Reading Missing(string name)
                => new Reading(name, false, 0.0, "");

            public override string ToString()
                => Reported
                    ? Name + " " + Value.ToString("0.##", CultureInfo.InvariantCulture) +
                      (Units.Length > 0 ? " " + Units : "")
                    : Name + " not reported";
        }

        /// <summary>The outcome of one tune.</summary>
        public readonly struct Result
        {
            public Verdict Verdict { get; }
            public SkipReason Skipped { get; }
            public DateTime AtUtc { get; }
            public IReadOnlyList<Reading> Meters { get; }

            /// <summary>Tune power the radio was set to, as read — not as
            /// requested. Recorded so a later reader knows what "no power"
            /// was measured against.</summary>
            public int TunePowerSetting { get; }

            /// <summary>
            /// SWR worked out from the forward and reflected power readings,
            /// or NaN when it could not be derived. Supplied by the capture
            /// half from <c>FlexBase.ComputedSWR</c> so the arithmetic lives in
            /// exactly one place — this half stays free of FlexLib and does not
            /// re-implement the formula.
            /// </summary>
            public double ComputedSwr { get; }

            /// <summary>
            /// True when the carrier was dropped before <see cref="TuneMs"/>
            /// because the load reading was bad. Recorded rather than hidden:
            /// "we stopped early" is a finding about the antenna port, and a
            /// reader who sees a short tune with no explanation will wonder
            /// whether the test simply failed.
            /// </summary>
            public bool StoppedEarly { get; }

            public string Frequency { get; }
            public string Mode { get; }
            public string Antenna { get; }

            /// <summary>
            /// The refusing layer's own words, when it had better ones than
            /// <see cref="ExplainSkip"/> can produce from a reason alone.
            /// Empty otherwise.
            /// </summary>
            /// <remarks>
            /// This exists so a host guard can refuse in specific, speakable
            /// terms WITHOUT growing a second vocabulary for the same event.
            /// Two descriptions of one refusal drift apart, and the operator
            /// ends up hearing one thing and mailing FlexRadio another.
            /// </remarks>
            public string SkipDetail { get; }

            private Result(Verdict verdict, SkipReason skipped, DateTime atUtc,
                           IReadOnlyList<Reading> meters, int tunePower, double computedSwr,
                           bool stoppedEarly,
                           string frequency, string mode, string antenna,
                           string skipDetail = null)
            {
                SkipDetail = skipDetail ?? "";
                Verdict = verdict; Skipped = skipped; AtUtc = atUtc;
                Meters = meters ?? Array.Empty<Reading>();
                TunePowerSetting = tunePower; ComputedSwr = computedSwr;
                StoppedEarly = stoppedEarly;
                Frequency = frequency ?? ""; Mode = mode ?? ""; Antenna = antenna ?? "";
            }

            /// <summary>
            /// Nothing was measured, and why. <paramref name="detail"/> lets a
            /// refusing layer supply its own words rather than forcing its
            /// reason through <see cref="ExplainSkip"/>, which only knows the
            /// enum.
            /// </summary>
            public static Result NotRun(SkipReason why, string detail = null)
                => new Result(Verdict.NotRun, why, DateTime.UtcNow,
                              Array.Empty<Reading>(), 0, double.NaN, false, "", "", "",
                              detail);

            public static Result Ran(Verdict verdict, DateTime atUtc,
                                     IReadOnlyList<Reading> meters, int tunePower,
                                     double computedSwr, bool stoppedEarly,
                                     string frequency, string mode, string antenna)
                => new Result(verdict, SkipReason.None, atUtc, meters, tunePower,
                              computedSwr, stoppedEarly, frequency, mode, antenna);

            /// <summary>
            /// True when the audio-chain tests have standing to run and to be
            /// believed. False means either we could not establish that the
            /// transmitter works, or we established that it does not — and in
            /// both cases an audio verdict downstream would be unfounded.
            /// </summary>
            public bool AudioTestingHasStanding
                => Verdict == Verdict.MakesPower || Verdict == Verdict.MakesPowerLoadSuspect;
        }

        /// <summary>
        /// May the probe key the transmitter? Pure, so every combination is
        /// testable without a radio.
        /// </summary>
        /// <returns>
        /// <see cref="SkipReason.None"/> when it may proceed, otherwise the
        /// reason it may not.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Split out from the runner because the runner could only ever be
        /// tested down its first branch — with no radio you can never reach the
        /// load-declaration gate, so the gate that MATTERS MOST was the one
        /// nothing could exercise. Policy and plumbing are different jobs and
        /// only one of them needs a radio in the room.
        /// </para>
        /// <para>
        /// Order is deliberate. Without a radio nothing else is even knowable —
        /// we cannot ask whether it is transmitting. The load gate comes next
        /// because it is a POLICY refusal (#180) rather than a capability one:
        /// it must not be reachable only by accident of what else happens to be
        /// wrong. Already-transmitting is last because it is a fact about the
        /// world rather than about us.
        /// </para>
        /// </remarks>
        public static SkipReason CheckPreconditions(bool haveRadio,
                                                    bool loadDeclared,
                                                    bool alreadyTransmitting,
                                                    bool cancelled)
        {
            if (cancelled) return SkipReason.Cancelled;
            if (!haveRadio) return SkipReason.RadioNotReachable;
            if (!loadDeclared) return SkipReason.LoadNotDeclared;
            if (alreadyTransmitting) return SkipReason.AlreadyTransmitting;
            return SkipReason.None;
        }

        /// <summary>
        /// Abort above this computed SWR. Higher than <see cref="SwrSuspect"/>
        /// on purpose: "suspect" is something to report, this is something to
        /// act on.
        /// </summary>
        public const double SwrAbort = 5.0;

        /// <summary>Fallback abort bar when SWR cannot be derived: half the
        /// power coming straight back. The empty-connector case measured 76.</summary>
        public const double ReflectedAbortPercent = 50.0;

        /// <summary>
        /// How many consecutive bad samples before dropping the carrier. Two,
        /// not one: the PA ramps, and the first sample after key-down can read
        /// badly on a perfectly good load. One sample is a transient, two in a
        /// row is a load.
        /// </summary>
        public const int BadSamplesBeforeAbort = 2;

        /// <summary>
        /// Should the carrier be dropped now? Called on each sample during the
        /// tune. Pure, so the abort rule is tested without keying anything.
        /// </summary>
        /// <param name="computedSwr">SWR worked out from power, NaN if not derivable.</param>
        /// <param name="reflectedPercent">Reflected as a share of forward, NaN if unknown.</param>
        /// <param name="consecutiveBad">How many samples in a row have already looked bad,
        /// not counting this one.</param>
        /// <remarks>
        /// The measurement is already made by the time this is true — holding
        /// the carrier longer adds nothing and puts power into something that is
        /// sending it back. Stop, and report why.
        /// </remarks>
        public static bool ShouldStopEarly(double computedSwr, double reflectedPercent,
                                           int consecutiveBad)
            => LooksBad(computedSwr, reflectedPercent)
            && (consecutiveBad + 1) >= BadSamplesBeforeAbort;

        /// <summary>
        /// Does THIS one sample look bad? The single definition of the abort
        /// threshold, used both by <see cref="ShouldStopEarly"/> and by the
        /// runner counting how many bad samples have gone by.
        /// </summary>
        /// <remarks>
        /// The runner carried a byte-identical private copy of this expression
        /// until the Sprint 35 merge. Two copies of one rule is not a
        /// cross-check — they can only ever agree, and when one is edited the
        /// disagreement is silent. A real cross-check needs INDEPENDENT
        /// sources, which here means forward and reflected watts, not two
        /// spellings of the same comparison.
        /// </remarks>
        public static bool LooksBad(double computedSwr, double reflectedPercent)
            => (!double.IsNaN(computedSwr) && computedSwr >= SwrAbort)
            || (double.IsNaN(computedSwr) && !double.IsNaN(reflectedPercent)
                && reflectedPercent >= ReflectedAbortPercent);

        /// <summary>
        /// Decide what a set of readings means. Pure; call it from tests with
        /// whatever you like.
        /// </summary>
        /// <remarks>
        /// <b>Forward power is the primary signal, and SWR is deliberately not.</b>
        /// The SWR meter reads 1.008 into a completely open antenna port — it is
        /// right when things are fine and wrong exactly when they are not, which
        /// makes it useless as a fault detector and actively misleading as a
        /// reassurance (task #189, measured on the bench 8600 2026-08-22). So
        /// the load judgement here is reflected power as a SHARE of forward
        /// power: a ratio that cannot blow up, stays a plain percentage from
        /// zero to a hundred, and means nothing at all when there is no forward
        /// power to be a share of.
        /// <para>
        /// SWR is still captured and still reported, because a reader at
        /// FlexRadio will expect to see it and its absence would look like
        /// concealment. It is simply not what the verdict turns on.
        /// </para>
        /// </remarks>
        public static Verdict Assess(IReadOnlyList<Reading> meters, double computedSwr)
        {
            if (meters == null || meters.Count == 0) return Verdict.NoForwardPowerMeter;

            Reading fwd = Find(meters, "FWDPWR");
            if (!fwd.Reported) return Verdict.NoForwardPowerMeter;

            // Did the transmitter do anything at all? Everything else is a
            // question about the load, and a question about the load is
            // meaningless if nothing was transmitted into it.
            if (fwd.Value <= NoPowerWatts) return Verdict.NoPower;

            // Load judgement. Computed SWR first: it is the number an operator
            // and a FlexRadio engineer both already think in, and the caller
            // derived it from forward and reflected power rather than reading
            // the radio's SWR meter.
            if (!double.IsNaN(computedSwr) && computedSwr >= SwrSuspect)
                return Verdict.MakesPowerLoadSuspect;

            // Fallback only when SWR could not be derived — reflected above
            // forward, or too little forward power to divide by. The share
            // still works where the ratio does not.
            //
            // The share is computed by TransmitSafety.ReflectedFractionOf, the
            // SAME arithmetic the live warning uses, low-forward guard and all
            // (#237 — it was derived independently here, and two computations
            // of one ratio is how thresholds stop being comparable). Its guard
            // cannot fire on this path — fwd.Value already cleared NoPowerWatts
            // above — but one home for the rule beats a private copy that
            // happens to agree today.
            //
            // ORDERING, decided rather than discovered: the chain rules report
            // power-coming-back BEFORE high-swr, naming a cause the operator
            // can act on. This verdict leads with SWR because it feeds
            // Explain's prose, where the ratio is the number an operator and a
            // FlexRadio engineer both already think in, and the reflected
            // share only speaks when SWR could not be derived at all. Two
            // surfaces, two readers, same thresholds.
            if (double.IsNaN(computedSwr))
            {
                Reading rev = Find(meters, "REFPWR");
                if (rev.Reported)
                {
                    double sharePercent = 100.0 * TransmitSafety.ReflectedFractionOf(
                        (float)fwd.Value, (float)rev.Value);
                    if (!double.IsNaN(sharePercent) && sharePercent >= ReflectedSuspectPercent)
                        return Verdict.MakesPowerLoadSuspect;
                }
            }

            return Verdict.MakesPower;
        }

        private static Reading Find(IReadOnlyList<Reading> meters, string name)
        {
            for (int i = 0; i < meters.Count; i++)
                if (string.Equals(meters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return meters[i];
            return Reading.Missing(name);
        }

        /// <summary>
        /// The operator-facing sentence. Says what happened and what it rules
        /// in or out, without naming a cause we have not established.
        /// </summary>
        public static string Explain(Result r)
        {
            switch (r.Verdict)
            {
                case Verdict.MakesPower:
                    return "The radio keyed a tune carrier and produced RF. Your microphone, " +
                           "your computer's audio and the mic profile were all out of the " +
                           "path, so the transmitter itself is working and anything wrong " +
                           "with your transmitted audio lies in the audio path rather than " +
                           "in the radio's ability to transmit.";

                case Verdict.MakesPowerLoadSuspect:
                    return "The radio keyed a tune carrier and produced RF, so the " +
                           "transmitter is working. " +
                           "A large share " +
                           "of that power came back rather than going out, though, so check " +
                           "what is connected to the antenna port before reading anything " +
                           "into the audio measurements.";

                case Verdict.NoPower:
                    // WHAT HAPPENED, and nothing else. The diagnosis and the
                    // remedy are the finding's job, and this said both until
                    // 2026-08-25 — so an operator read the same paragraph
                    // twice in two voices, once here and once immediately
                    // below. It also SHOUTED, which some voices spell out
                    // letter by letter.
                    return "The radio keyed a tune carrier and produced no RF at all. Your " +
                           "microphone, your computer's audio and the mic profile were all " +
                           "out of the path.";

                case Verdict.NoForwardPowerMeter:
                    return "The transmitter was keyed, but this radio did not report a forward " +
                           "power meter, so whether power appeared cannot be said either way. " +
                           "That is a gap in the measurement, not a finding about the radio.";

                case Verdict.NotRun:
                default:
                    // The refusing layer's own words win when it had them. It
                    // knew more about why than the enum can carry, and this is
                    // spoken to somebody who has to act on it.
                    return r.SkipDetail.Length > 0 ? r.SkipDetail : ExplainSkip(r.Skipped);
            }
        }

        /// <summary>Why nothing was measured, and what to do about it.</summary>
        public static string ExplainSkip(SkipReason why)
        {
            switch (why)
            {
                case SkipReason.RadioNotReachable:
                    return "The radio was not reachable, so nothing was keyed and nothing " +
                           "was measured.";

                case SkipReason.LoadNotDeclared:
                    return "Nothing was transmitted, because it is not known what is connected " +
                           "to the antenna port. Declare a dummy load or an antenna first — " +
                           "transmitting into an unknown load is not something this will do on " +
                           "its own.";

                case SkipReason.AlreadyTransmitting:
                    return "Something was already transmitting, so this was not run. Anything " +
                           "measured would have been that transmission rather than this test.";

                case SkipReason.Cancelled:
                    return "The test was stopped before it finished, so there is no result.";

                case SkipReason.RefusedByHost:
                    // Only reached when the refusing layer supplied no words of
                    // its own, which it normally does. Deliberately does not
                    // guess at a cause.
                    return "This step was not run, and nothing was transmitted.";

                case SkipReason.None:
                default:
                    return "No result.";
            }
        }

        /// <summary>
        /// The evidence-text section. Reports readings, then what we make of
        /// them, clearly separated — same contract as the rest of the evidence
        /// block (#217): a reader who distrusts our software entirely must still
        /// be able to use the numbers.
        /// </summary>
        public static string EvidenceSection(Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Transmitter, audio chain not involved");
            sb.AppendLine("-------------------------------------");
            sb.AppendLine("The radio was keyed with its own tune carrier. No microphone, no");
            sb.AppendLine("computer audio device, no encoding and no audio streaming take part");
            sb.AppendLine("in this measurement.");
            sb.AppendLine();

            if (r.Verdict == Verdict.NotRun)
            {
                sb.AppendLine("Not run: " + ExplainSkip(r.Skipped));
                return sb.ToString();
            }

            sb.AppendLine("Taken at " + r.AtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'",
                                                         CultureInfo.InvariantCulture));
            if (r.Frequency.Length > 0) sb.AppendLine("Frequency: " + r.Frequency);
            if (r.Mode.Length > 0) sb.AppendLine("Mode: " + r.Mode);
            if (r.Antenna.Length > 0) sb.AppendLine("Antenna port: " + r.Antenna);
            sb.AppendLine("Tune power setting: " + r.TunePowerSetting.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();

            sb.AppendLine("Readings:");
            foreach (Reading m in r.Meters) sb.AppendLine("  " + m);
            sb.AppendLine();

            sb.AppendLine("What JJ Flexible made of the above (our interpretation, not a measurement):");
            sb.AppendLine("  " + Explain(r));
            return sb.ToString();
        }
    }
}
