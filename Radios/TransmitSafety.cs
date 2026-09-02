using System;

namespace Radios
{
    /// <summary>
    /// Decisions about whether a transmission in progress is safe, kept as pure
    /// functions so they can be tested without a radio, a window or a thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not simply a private method on the PTT controller.</b> It
    /// was, briefly, and that is the shape this project keeps getting caught by:
    /// a warning whose decision lives inside a WPF class, reachable only by
    /// keying a real transmitter into a real fault. Such a warning compiles,
    /// reviews clean, and is indistinguishable from a working one right up until
    /// the day somebody needs it and hears nothing. The Alt+L binding that
    /// shipped completely dead on 2026-08-13 was the same shape.
    /// </para>
    /// <para>
    /// So the judgement lives here, where a test can put numbers in and read a
    /// verdict out, and the controller keeps only the parts that genuinely need
    /// a radio: reading the meters, playing the earcon, speaking the sentence.
    /// </para>
    /// </remarks>
    public static class TransmitSafety
    {
        /// <summary>
        /// THE reflected-power threshold, as a percentage of forward power.
        /// This is the one home; everything that judges reflected share names
        /// this constant or is tested against it.
        /// </summary>
        /// <remarks>
        /// MEASURED on 2026-08-22, not guessed. The bench 8600 transmitting into
        /// an EMPTY antenna connector — the dummy load was on the other port —
        /// sent 76 percent of its power straight back. Minutes later, into the
        /// load, 0.05 percent. Three orders of magnitude apart, so 40 percent
        /// sits in an enormous empty gap rather than on a judgement call. For
        /// scale it is a standing wave ratio near 5 to 1, past anything a
        /// working antenna presents.
        /// <para><b>Three consumers, kept in step by a test, not by this
        /// comment.</b> (1) The live PTT warning, through
        /// <see cref="ReflectedWarnFraction"/>. (2) The power-coming-back rule
        /// in tx-chain-rules.txt — a data file that cannot reference this
        /// constant, so ReflectedThresholdAgreementTests parses the shipped
        /// file and fails if the two drift. (3) The transmit-check tune probe's
        /// fallback, <c>TxTuneProbe.ReflectedSuspectPercent</c>, which is
        /// DELIBERATELY STRICTER and derives from this constant so the
        /// relationship is visible — see its own remarks. An operator who hears
        /// the live warning and then runs a check must not be given two
        /// different answers about the same station.</para>
        /// <para>History: this invariant was documented for the first two
        /// consumers, honoured for months, and then quietly broken when the
        /// probe's fallback was written at 20 without reading the note (#237).
        /// Hence the test — a comment asked future editors to keep the figures
        /// in step, and a future editor did not.</para>
        /// </remarks>
        public const double ReflectedWarnPercent = 40.0;

        /// <summary>
        /// <see cref="ReflectedWarnPercent"/> as a fraction between 0 and 1,
        /// for the live warning path which works in fractions.
        /// </summary>
        public const float ReflectedWarnFraction = (float)(ReflectedWarnPercent / 100.0);

        /// <summary>
        /// Seconds of transmit before the reflected-power warning may speak.
        /// <para>Two, where the audio-quality warnings wait five, and the
        /// difference is the point: a hot microphone is an embarrassment the
        /// operator can fix next over, while power coming back is arriving at
        /// the finals right now. One tick of settling is enough to reject a
        /// meter that has not caught up with key-down.</para>
        /// </summary>
        public const int ReflectedWarnSeconds = 2;

        /// <summary>
        /// The ABSOLUTE floor below which a reflected fraction means nothing,
        /// because a meter wandering around zero can produce any ratio at all.
        /// </summary>
        /// <remarks>
        /// <b>This is the floor of a floor, not the floor (#453.)</b> It was
        /// measured against a DEAD KEY — 0.22 W into an open port on
        /// 2026-08-22 — which is a steady-state number that says nothing about
        /// speech. On a hundred-watt voice envelope one watt excludes almost
        /// nothing: the envelope crosses it constantly on its way down between
        /// syllables, which is exactly where a mismatched pair of readings
        /// produces a spike. The floor that actually acts is
        /// <see cref="ReflectedWarnFloorWatts"/>, which scales with the
        /// transmission; this remains as its lower bound, for the QRP and
        /// transverter-drive case where a share of the peak would be a
        /// fraction of a watt and the ratio really is noise.
        /// </remarks>
        public const float ReflectedWarnMinWatts = 1f;

        /// <summary>
        /// The share of a transmission's own forward-power PEAK below which a
        /// reflected reading is not judged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A tenth — ten times the old absolute watt, and on a hundred-watt
        /// transmission it lands on ten watts, the figure Noel independently
        /// ruled as the boundary worth stopping for. It discards the deep
        /// troughs between syllables, which is where the mismatched-pair
        /// artefact lived, without discarding most of the transmission.
        /// </para>
        /// <para>
        /// <b>Deliberately not a quarter, and the reasoning is the same
        /// reasoning that rules out smoothing.</b> The register's objection to
        /// smoothing is that it lowers a false spike AND delays a real alarm,
        /// which is the wrong trade on a protective feature. A floor and a
        /// persistence rule stack the same way: the floor decides how often a
        /// sample is judgeable at all, and the persistence rule then waits for
        /// several of them, so raising the floor multiplies the delay before a
        /// GENUINE fault is announced. Speech has roughly ten decibels of
        /// peak-to-average, so a quarter-of-peak floor would leave only a small
        /// minority of one-a-second samples judgeable and push the warning out
        /// by tens of seconds. The pairing rule is what removes the defect;
        /// this is defence in depth and must not be paid for in alarm latency.
        /// </para>
        /// <para>
        /// <b>Still to be measured on the bench:</b> how often a sample is
        /// judgeable on real speech, and therefore how long the warning
        /// actually takes on a genuinely bad match.
        /// <see cref="ReflectedPowerRun.JudgedSamples"/> is traced with the
        /// warning precisely so a sitting can answer that rather than an
        /// estimate standing in for it.
        /// </para>
        /// <para>
        /// <b>A share of the PEAK, not of the operator's power setting</b>, and
        /// the difference is not cosmetic — see
        /// <see cref="ReflectedPowerRun"/> for the foldback measurement that
        /// decides it.
        /// </para>
        /// <para>
        /// <b>Not named <c>...Fraction</c> or <c>...Percent</c> on purpose.</b>
        /// In this assembly those suffixes on a <c>Reflected*</c> constant mean
        /// a share of forward power that is coming BACK, and
        /// <c>IntegrationPassRuleTests.Every_reflected_power_threshold_is_the_same_number</c>
        /// discovers them by that convention and requires them all to agree
        /// with <see cref="ReflectedWarnFraction"/>. This is a share of FORWARD
        /// power — a different quantity that would have been judged against the
        /// wrong ruler, and rightly so, had it kept the wrong suffix.
        /// </para>
        /// </remarks>
        public const float ReflectedWarnFloorShareOfPeak = 0.10f;

        /// <summary>
        /// Judgeable samples in a row that must be bad before the warning
        /// speaks.
        /// </summary>
        /// <remarks>
        /// Three. The pre-existing persistence was "the warning fired on an
        /// earlier tick and the cut reads this one", which is sound against a
        /// key-down transient and no defence at all against a voice envelope —
        /// troughs recur many times a second and supply a second bad sample for
        /// free. Counting JUDGEABLE samples rather than ticks is what makes
        /// three achievable; see <see cref="ReflectedPowerRun.Observe"/>.
        /// </remarks>
        public const int ReflectedWarnSustainedSamples = 3;

        // ==================================================================
        // The settling rule (#453): judge the SHAPE, not only the level
        // ==================================================================
        //
        // A tester's 6300 has no internal tuner. He drives a remote tuner by
        // transmitting into it, so ATUTuneInProgress — the flag the alarm
        // stands down on — is never set on his station, and the alarm cut him
        // off while his tuner was still hunting, a second before it settled to
        // 1.7. He saw 1.7 and reasonably concluded the alarm was wrong. It was
        // not wrong; it was unsuppressed. Noel's discriminator, which needs no
        // declaration, no timer and no visibility into a tuner we do not own:
        //
        //   A bad antenna's reflected power is STABLE. A tuner searching
        //   produces reflected power that CHANGES and trends down.
        //
        // So: falling and settling defers; high and stable alarms; and a
        // deferral is not a cancellation — if the share is still high when it
        // stops moving, or the outer bound passes, the alarm fires. A tuner
        // that never finds a match is precisely the case the operator most
        // needs telling about.

        /// <summary>
        /// How far apart the reflected shares in the settle window may be —
        /// highest minus lowest — and still count as holding still.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ten points of share. The two measured stable faults — 17.5 W with
        /// 13.4 W back on 2026-08-22, 4.1 W with 3.10 W back on 2026-09-01 —
        /// both read 76 percent, sample after sample, weeks apart at different
        /// powers; a stable mismatch does not wander by ten points. A tuner
        /// hunting through relay combinations above the threshold moves the
        /// share by tens of points per step, because the combinations are
        /// coarse where the match is bad. The band between is wide, which is
        /// what makes this a boundary rather than a guess — but it has been
        /// measured only on stable loads so far. <b>Still to be measured on the
        /// bench:</b> the spread a hunting tuner actually produces at one
        /// sample a second; the trace carries the recent shares for exactly
        /// that.
        /// </para>
        /// <para>
        /// <b>Not named <c>...Fraction</c> on purpose.</b> This is a DIFFERENCE
        /// between two shares, not a share of forward power, and
        /// <c>IntegrationPassRuleTests.Every_reflected_power_threshold_is_the_same_number</c>
        /// rightly requires anything with that suffix to equal
        /// <see cref="ReflectedWarnFraction"/>.
        /// </para>
        /// </remarks>
        public const float ReflectedSettleSpan = 0.10f;

        /// <summary>
        /// How much of the current bad streak the shape is judged over: the
        /// last two seconds of it, and never fewer than
        /// <see cref="ReflectedWarnSustainedSamples"/> samples.
        /// </summary>
        /// <remarks>
        /// "The last second or two" is the whole of the specification, and
        /// two rather than one because the kill switch samples four times a
        /// second: three samples there cover under a second, and a tuner
        /// stepping once a second would look settled between steps. The
        /// sample minimum is for speech at one a second, where most ticks are
        /// not judgeable and the last three judged samples may be six seconds
        /// apart. See <see cref="ReflectedPowerRun.RecentShares"/>.
        /// </remarks>
        public const double ReflectedSettleWindowSeconds = 2.0;

        /// <summary>
        /// How long a changing reflected share may hold the alarm off, counted
        /// from the first bad sample of the streak. Past it, the level alone
        /// decides.
        /// </summary>
        /// <remarks>
        /// Twenty seconds. The tester's own figure for his tuner is ten; a
        /// minute is not a tune, it is a fault that happens to be moving. The
        /// published worst cases for the common outboard tuners sit at or
        /// under fifteen, so twenty covers a hard match with margin and still
        /// ends, for a tuner that never finds one, well inside the time an
        /// operator would wonder why nothing had been said. Counted from the
        /// streak's start rather than key-down so that a re-hunt three minutes
        /// into a transmission gets the same patience as one at key-down —
        /// see <see cref="ReflectedPowerRun.BadStreakStartSeconds"/>.
        /// </remarks>
        public const double ReflectedSettleBoundSeconds = 20.0;

        /// <summary>
        /// The forward power below which a reflected share is not judged, given
        /// how much power this transmission has actually managed to make.
        /// </summary>
        /// <param name="forwardPeakWatts">
        /// The highest forward power seen this transmission — normally
        /// <see cref="ReflectedPowerRun.ForwardPeakWatts"/>.
        /// </param>
        public static float ReflectedWarnFloorWatts(float forwardPeakWatts)
        {
            if (float.IsNaN(forwardPeakWatts) || forwardPeakWatts <= 0f)
                return ReflectedWarnMinWatts;
            return Math.Max(ReflectedWarnMinWatts,
                            forwardPeakWatts * ReflectedWarnFloorShareOfPeak);
        }

        /// <summary>
        /// How much of the forward power is coming back, from 0 to 1, or NaN
        /// when the question cannot be answered.
        /// </summary>
        /// <remarks>
        /// NaN rather than 0 when there is too little power to judge. Returning
        /// a comfortable number for "no idea" is the exact defect this whole
        /// area exists to fix — the radio's own SWR meter answers 1.008 when it
        /// has nothing useful to say, and two bench sessions were measured
        /// through that reassurance.
        /// </remarks>
        public static float ReflectedFractionOf(float forwardWatts, float reflectedWatts)
        {
            if (float.IsNaN(forwardWatts) || float.IsNaN(reflectedWatts)) return float.NaN;
            if (forwardWatts < 0.05f) return float.NaN;
            if (reflectedWatts < 0f) return 0f;
            return Math.Min(reflectedWatts / forwardWatts, 1f);
        }

        /// <summary>
        /// What the reflected-power rule concluded about one tick.
        /// </summary>
        public enum ReflectedVerdict
        {
            /// <summary>Nothing to say: already warned, meters still settling,
            /// a tune cycle running, the sample unjudgeable or good, or the
            /// run not yet sustained.</summary>
            Quiet,

            /// <summary>The share is high and has been for long enough to
            /// believe — but it is still CHANGING, and the outer bound has not
            /// passed. Something is moving the match; wait for it to stop.
            /// Callers should record this with
            /// <see cref="ReflectedPowerRun.NoteDeferred"/> and trace the
            /// first one.</summary>
            Deferred,

            /// <summary>Tell the operator now.</summary>
            Warn,
        }

        /// <summary>
        /// Whether the operator should be told, right now, that their power is
        /// coming back instead of leaving — and if not, whether that is because
        /// the alarm is being HELD OFF while the match moves.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It takes a paired reading and a run, not two loose numbers, and
        /// that is the first half of #453.</b> The signature that used to be
        /// here accepted a forward float and a reflected float, and both live
        /// callers filled them with two independent property gets of two
        /// independently-updated fields. Every judgement was therefore made on
        /// a pair that might have been sampled at different instants, and on
        /// speech that is not a rare edge — it is most of them. There is no
        /// overload taking loose watts on purpose: leaving one would leave the
        /// defect available to the next caller.
        /// </para>
        /// <para>
        /// <b>The settling rule is the second half, and it is the layer ABOVE
        /// the pairing and the floor, which stand.</b> Everything up to
        /// <see cref="ReflectedPowerRun.Sustained"/> is the rule that was
        /// validated on real measurements on 2026-09-01 — 4.1 W into a genuine
        /// open port, 76 percent back, three judged samples, alarm as
        /// designed. What is added after it: a sustained bad run whose recent
        /// shares are still CHANGING is deferred rather than announced, for up
        /// to <see cref="ReflectedSettleBoundSeconds"/> from the streak's first
        /// bad sample. A high share that holds still warns at once — the three
        /// identical samples of 2026-09-01 are exactly that, and this function
        /// warns on them at the same tick it did before. A share that settles
        /// high warns when it settles. A share still moving at the bound warns
        /// at the bound. Nothing here can turn a warning into silence; it can
        /// only move it later, and never past the bound.
        /// </para>
        /// <para>
        /// <b>The deferral does not know whether a tuner exists, on purpose.</b>
        /// An operator who never told us about their tuner gets the same rule,
        /// because the shape of the last two seconds is evidence and a
        /// declaration is not. Equally, nothing here remembers that the match
        /// has always been fine: an antenna that has always been fine is
        /// precisely the one to be told about the day it stops.
        /// </para>
        /// <para>
        /// <b><paramref name="tuning"/> is read fresh every tick and remembered
        /// by nobody.</b> The manual-tune half of #453 wires it from the
        /// radio's own live tune-carrier state (<c>FlexBase.TxTune</c>) rather
        /// than from the <c>FlexAntTunerStartStop</c> event, because that event
        /// carries a start for the operator's tune carrier and no stop — the
        /// stop is raised only inside <c>FlexTunerOn</c>, which the carrier
        /// toggle does not go through. A flag latched from it would disable
        /// this alarm permanently the first time a carrier was dropped by any
        /// other route. The radio's state cannot latch: it is cleared by
        /// whatever drops the carrier, including the radio itself.
        /// </para>
        /// </remarks>
        /// <param name="reading">Forward and reflected as ONE reading.</param>
        /// <param name="run">
        /// This transmission's accumulated state — the forward peak that sets
        /// the floor, the run of bad judgeable samples, and their shape.
        /// <see cref="ReflectedPowerRun.Observe"/> must already have been given
        /// this reading, with this same clock.
        /// </param>
        /// <param name="txSeconds">Seconds transmitting, in any keying state —
        /// the same clock the run was observed with.</param>
        /// <param name="tuning">True while the antenna tuner is running a cycle,
        /// or the operator's own tune carrier is up.</param>
        /// <param name="alreadyWarned">True once this transmission has spoken.</param>
        public static ReflectedVerdict JudgeReflected(
            in TransmitPowerReading reading, ReflectedPowerRun run,
            double txSeconds, bool tuning, bool alreadyWarned)
        {
            // Once per transmission. A warning that repeats every second while
            // the operator is trying to act on it is noise, and noise is how a
            // warning gets switched off.
            if (alreadyWarned) return ReflectedVerdict.Quiet;

            if (txSeconds < ReflectedWarnSeconds) return ReflectedVerdict.Quiet;

            // A tune cycle transmits into a deliberately bad match and walks
            // toward a good one, so high reflected power during one is the tuner
            // doing its job. Without this, every routine tune-up would announce
            // a disconnected antenna — and an operator who has learned to ignore
            // a warning is worse off than one who never had it.
            if (tuning) return ReflectedVerdict.Quiet;

            if (run == null) return ReflectedVerdict.Quiet;

            // The current sample must itself be judgeable and bad. The run
            // carries the corroboration; it must not carry the verdict on its
            // own, or a warning could fire off three old samples after the
            // meters had already recovered.
            if (!reading.IsCoherent) return ReflectedVerdict.Quiet;
            if (float.IsNaN(reading.ForwardWatts)
                || reading.ForwardWatts < run.FloorWatts) return ReflectedVerdict.Quiet;

            float back = reading.ReflectedShare;
            if (float.IsNaN(back)) return ReflectedVerdict.Quiet;
            if (back <= ReflectedWarnFraction) return ReflectedVerdict.Quiet;

            if (!run.Sustained) return ReflectedVerdict.Quiet;

            // The settling rule. Only Changing defers: TooFew cannot coincide
            // with Sustained today, and if a future edit made it possible the
            // safe reading of "cannot tell the shape" is to judge on level,
            // not to wait.
            if (run.Shape == ReflectedShape.Changing
                && run.BadStreakSeconds(txSeconds) < ReflectedSettleBoundSeconds)
                return ReflectedVerdict.Deferred;

            return ReflectedVerdict.Warn;
        }

        /// <summary>
        /// <see cref="JudgeReflected"/> as a plain yes or no. The live paths
        /// use the verdict so they can trace a deferral; this remains for
        /// callers and tests that only need the answer.
        /// </summary>
        public static bool ShouldWarnReflected(
            in TransmitPowerReading reading, ReflectedPowerRun run,
            int txSeconds, bool tuning, bool alreadyWarned)
        {
            return JudgeReflected(reading, run, txSeconds, tuning, alreadyWarned)
                   == ReflectedVerdict.Warn;
        }

        /// <summary>
        /// The sentence to speak, naming the transmit antenna when one is known
        /// and sharpening the wording when the operator has declared a dummy
        /// load.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Naming the port is the whole point rather than a nicety. "Check the
        /// antenna" is advice; "check ANT1" is an instruction — and the operator
        /// this is built for is blind and cannot read the labels moulded into
        /// the back panel.
        /// </para>
        /// <para>
        /// The declared-load variant exists because that combination is not a
        /// weaker signal, it is a far stronger one. An operator who has said "I
        /// am into a dummy load" has told us to expect almost nothing back. Most
        /// of it coming back means the load is not in the path they think it is
        /// — which is EXACTLY the fault of 2026-08-22, where the load sat on
        /// ANT2 while ANT1 was selected and two sessions of measurements were
        /// taken before anyone noticed it never got warm. That sentence is worth
        /// saying out loud rather than folding into the generic one.
        /// </para>
        /// <para>
        /// <paramref name="cutDisarmed"/> exists because the cut became
        /// defeatable (#224, ruled by Noel 2026-08-30), and a defeatable
        /// safety that is off and still trusted is worse than no safety at
        /// all — it is trusted. So the one moment the operator must not be
        /// able to forget they turned it off is the moment it would have
        /// acted: this alarm. When the setting is off, the warning says so
        /// out loud. Pass the INVERSE of the operator's setting. It defaults
        /// to false only so tests of the sentence itself can ignore it; the
        /// live alarm paths must pass it explicitly, and a source-read test
        /// (ReflectedWarningWiringTests) holds both of them to that.
        /// </para>
        /// </remarks>
        public static string ReflectedWarningText(
            float fraction, string antennaName, bool dummyLoadDeclared = false,
            bool cutDisarmed = false)
        {
            int percent = (int)Math.Round(fraction * 100f);
            bool named = !string.IsNullOrWhiteSpace(antennaName);

            string key = dummyLoadDeclared
                ? (named ? "audio.ptt.power_coming_back_on_dummy_load"
                         : "audio.ptt.power_coming_back_dummy_load")
                : (named ? "audio.ptt.power_coming_back_on"
                         : "audio.ptt.power_coming_back");

            string text = named
                ? Lexicon.Get(key, ("percent", percent), ("antenna", antennaName))
                : Lexicon.Get(key, ("percent", percent));

            if (cutDisarmed)
                text += " " + Lexicon.Get("audio.ptt.reflected_cutoff_is_off");
            return text;
        }

        /// <summary>
        /// Forward watts above which the reflected-power CUT may act (#224).
        /// Below it there is little to protect and cutting costs the operator
        /// a contact for nothing — the bench dead key measured 0.22 W into an
        /// open port, harmless. Above it the radio is folding back to survive
        /// something: the case that started this had 13.4 of 17.5 W coming
        /// straight back. The boundary between "worth telling you" and
        /// "worth stopping for", ruled at ten by Noel 2026-08-25.
        /// </summary>
        public const float ReflectedCutMinForwardWatts = 10f;

        /// <summary>
        /// Whether the transmission should be CUT, not merely warned about
        /// (#224). Only ever true when the operator turned the setting on: an
        /// app that unilaterally unkeys a transmitter has taken the station
        /// away mid-transmission, and some operators — a reactive load, a
        /// tuner mid-cycle, an experimental antenna — would find that
        /// intolerable.
        /// </summary>
        /// <param name="settingEnabled">The operator's own choice. Never
        /// defaulted to true by a caller.</param>
        /// <param name="alreadyWarned">
        /// True once <see cref="ShouldWarnReflected"/> has fired this
        /// transmission. The cut requires it, which is the two-samples rule
        /// arriving by reuse rather than by a second counter: the warning
        /// fired on an EARLIER sample, this decision reads the current one,
        /// so a single transient at key-down can never cut — the same
        /// reasoning as the antenna checker's early stop.
        /// </param>
        /// <param name="reading">
        /// Forward and reflected as ONE reading (#453). An incoherent pair
        /// never cuts — ending an operator's transmission on two readings that
        /// were not taken together is the worst version of this defect, because
        /// the cost is a contact rather than a sentence.
        /// </param>
        /// <param name="tuning">True while the antenna tuner runs a cycle —
        /// high reflected power during one is the tuner working, and a cut
        /// here would kill every tune-up the operator starts.</param>
        /// <remarks>
        /// <b>The ten-watt floor is deliberately NOT replaced by the run's
        /// scaled floor.</b> Ten watts was ruled by Noel on 2026-08-25 as the
        /// boundary between "worth telling you" and "worth stopping for", and a
        /// share of the peak would sit above it on any full-power
        /// transmission — quietly raising a number a human set. The pairing
        /// requirement plus <paramref name="alreadyWarned"/> (which now needs a
        /// sustained run behind it) is what keeps a voice trough out of here.
        /// </remarks>
        public static bool ShouldCutReflected(bool settingEnabled, bool alreadyWarned,
                                              in TransmitPowerReading reading,
                                              bool tuning)
        {
            if (!settingEnabled || !alreadyWarned || tuning) return false;
            if (!reading.IsCoherent) return false;
            if (float.IsNaN(reading.ForwardWatts)
                || reading.ForwardWatts <= ReflectedCutMinForwardWatts)
                return false;

            float back = reading.ReflectedShare;
            return !float.IsNaN(back) && back >= ReflectedWarnFraction;
        }

        /// <summary>
        /// What is said when the cut fires. It must say what happened, why,
        /// and above all that the operator is NO LONGER TRANSMITTING — they
        /// have no visual cue that it happened and will keep talking.
        /// </summary>
        public static string ReflectedCutText(float fraction, string antennaName)
        {
            int percent = (int)Math.Round(fraction * 100f);
            bool named = !string.IsNullOrWhiteSpace(antennaName);
            string key = named ? "audio.ptt.reflected_cut_on" : "audio.ptt.reflected_cut";
            return named
                ? Lexicon.Get(key, ("percent", percent), ("antenna", antennaName))
                : Lexicon.Get(key, ("percent", percent));
        }

        // ==================================================================
        // Transmit audio: is anything arriving at all? (#459)
        // ==================================================================

        /// <summary>
        /// The SC_MIC peak-hold's idle value — what the field reads when no
        /// meter sample has arrived. Anything the meter actually reports is
        /// above it.
        /// </summary>
        /// <remarks>
        /// <b>This is the honest test for "nothing arrived", and the reason the
        /// old one cried wolf (#459).</b> The warning used to ask whether the
        /// peak had risen above <c>-45 dBFS</c> — a LEVEL judgement standing in
        /// for a PRESENCE one. An operator measured at <b>-92.59 dBFS</b> while
        /// audible on the air and making contacts was therefore told his
        /// microphone was dead on every transmission, 47 dB below a threshold
        /// that was never about him. A path that delivered nothing at all reads
        /// this floor; -92.59 is emphatically not this floor. Presence and
        /// level are two different faults with two different urgencies, and one
        /// threshold cannot do both jobs.
        /// </remarks>
        public const float MicNothingArrivedDbfs = -150f;

        /// <summary>
        /// How long a transmission may run with NOTHING arriving before the
        /// operator is told.
        /// </summary>
        /// <remarks>
        /// Ten seconds, up from five. Five is a normal amount of time to key up
        /// and gather your thoughts — one tester keys, thinks for about five
        /// seconds, talks, pauses again, all while keyed, and that is not
        /// unusual operating. A dead microphone still wants finding early
        /// rather than at unkey, so this waits rather than deferring.
        /// </remarks>
        public const double MicVerifyWindowSeconds = 10.0;

        /// <summary>
        /// How long a proven-good transmit audio path stays proven, across
        /// transmissions.
        /// </summary>
        /// <remarks>
        /// Ten minutes. A working microphone does not die mid-sentence, and
        /// re-running the check on every over is how a warning becomes noise.
        /// Time is only the backstop: <see cref="MicPathVerification"/> also
        /// drops the proof the moment anything that could change the audio path
        /// changes.
        /// </remarks>
        public const double MicVerifiedForSeconds = 600.0;

        /// <summary>What the transmit-audio watch has concluded so far.</summary>
        public enum MicPathVerdict
        {
            /// <summary>Nothing has arrived yet, but the window has not run
            /// out. Say nothing.</summary>
            KeepWatching,

            /// <summary>Audio arrived. The path is proven and nothing is said
            /// — the point of the whole rule is that success is what
            /// latches.</summary>
            Verified,

            /// <summary>The window elapsed with nothing at all. Wrong device,
            /// wrong profile, unplugged microphone: act now.</summary>
            NothingArrived,

            /// <summary>The window elapsed and the meter never delivered a
            /// sample, so there is no reading to judge. Say nothing to the
            /// operator; trace it for the person who reads traces (#502).</summary>
            NoTelemetry
        }

        /// <summary>
        /// Judge the transmit audio path from the peak-hold so far.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Latch the SUCCESS, not the failure.</b> The defect this replaces
        /// formed its verdict on the first tick at five seconds and latched
        /// "silent" forever. Because the peak-hold only ever grows, a verdict
        /// of silent at five seconds could be false by six — the warning could
        /// be contradicted by the meter before the sentence finished being
        /// spoken, and an operator who gathered his thoughts and then talked
        /// for four minutes was told his microphone was dead.
        /// </para>
        /// <para>
        /// Inverting it removes that whole class: once audio has arrived the
        /// answer can never become wrong, so it is the answer worth keeping.
        /// </para>
        /// <para>
        /// <b>A floor is not a silence (#502).</b> The peak-hold reads its
        /// -150 floor both when the meter reported silence and when the meter
        /// never reported at all — and on a radio that publishes several
        /// copies of SC_MIC the app was bound to a copy that never reports, so
        /// this fired on a working station whose transmit monitor was playing
        /// the operator's own voice. <paramref name="meterReported"/> is the
        /// distinction: without a sample since key-down there is no reading,
        /// and the only honest verdict is that nothing can be judged.
        /// </para>
        /// </remarks>
        /// <param name="micPeakDbfs">
        /// The SC_MIC peak-hold since key-down (<c>FlexBase.ScMicMaxDb</c>),
        /// which only ever grows, so a pause between words cannot lower it.
        /// </param>
        /// <param name="txSeconds">Seconds since key-down.</param>
        /// <param name="meterReported">
        /// Whether the meter behind <paramref name="micPeakDbfs"/> has delivered
        /// at least one sample since key-down (<c>FlexBase.ScMicReportedSinceReset</c>).
        /// A peak is a claim about samples; without one it is not evidence of
        /// anything, in either direction.
        /// </param>
        public static MicPathVerdict JudgeMicPath(float micPeakDbfs, double txSeconds, bool meterReported)
        {
            if (!meterReported)
                return txSeconds >= MicVerifyWindowSeconds
                    ? MicPathVerdict.NoTelemetry
                    : MicPathVerdict.KeepWatching;

            if (!float.IsNaN(micPeakDbfs) && micPeakDbfs > MicNothingArrivedDbfs)
                return MicPathVerdict.Verified;

            return txSeconds >= MicVerifyWindowSeconds
                ? MicPathVerdict.NothingArrived
                : MicPathVerdict.KeepWatching;
        }

        /// <summary>
        /// Whether an earlier verification still describes the path in front of
        /// us.
        /// </summary>
        /// <remarks>
        /// <b>Both halves matter, and the signature is the important one.</b>
        /// A clock alone would suppress the warning for up to ten minutes after
        /// a microphone was unplugged or a profile switched — a new defect of
        /// exactly the shape this one is. So the proof is dropped the moment
        /// anything that defines the audio path differs from what it was when
        /// the proof was taken.
        /// </remarks>
        public static bool MicVerificationStillHolds(
            bool haveVerification, double secondsSinceVerified,
            string signatureWhenVerified, string signatureNow)
        {
            if (!haveVerification) return false;
            if (secondsSinceVerified < 0 || secondsSinceVerified > MicVerifiedForSeconds)
                return false;
            return string.Equals(signatureWhenVerified ?? "", signatureNow ?? "",
                                 StringComparison.Ordinal);
        }

        /// <summary>
        /// Everything that decides which audio path a transmission uses, as one
        /// comparable string.
        /// </summary>
        /// <remarks>
        /// A pulled fingerprint rather than a set of subscribed events, because
        /// an event set is only as complete as the last person to remember it.
        /// The radio's serial covers a radio change and a disconnect (it empties
        /// when nothing is connected, so a reconnect re-proves); the mic source
        /// and the PC-audio flag cover the two ways the transmit chain is
        /// re-pointed on the radio; <paramref name="audioDeviceId"/> carries
        /// whatever the caller can see of the Windows capture device.
        /// </remarks>
        public static string MicPathSignature(
            string radioSerial, string micSource, bool pcAudio, string audioDeviceId)
        {
            return (radioSerial ?? "") + "|" + (micSource ?? "") + "|"
                   + (pcAudio ? "pc" : "radio") + "|" + (audioDeviceId ?? "");
        }

        /// <summary>
        /// Whether transmit audio arrived but never got anywhere near a usable
        /// level — gain staging, which is advice rather than an alarm.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Separate from <see cref="JudgeMicPath"/> on purpose (#459). The
        /// meter distinguishes two faults the old code collapsed into one:
        /// nothing arriving is urgent and means the device or profile is wrong,
        /// while a present-but-low reading is a level to adjust and can wait
        /// for the end of the over. They want different sentences and different
        /// urgency.
        /// </para>
        /// <para>
        /// The threshold is passed in rather than declared here because it
        /// still lives with the PTT controller, and because it has NOT been set
        /// from measurement yet: the single spoken reading we hold came from a
        /// window that may have had very little talking in it. Ruled by Noel on
        /// 2026-09-01 — fix the shape now, set the number when the operator's
        /// QSO capture lands. Guessing a second number is how the first one got
        /// here.
        /// </para>
        /// </remarks>
        public static bool ShouldAdviseMicLevel(float micPeakDbfs, float adviceFloorDbfs)
        {
            if (float.IsNaN(micPeakDbfs)) return false;
            if (micPeakDbfs <= MicNothingArrivedDbfs) return false;  // that is the other fault
            return micPeakDbfs < adviceFloorDbfs;
        }
    }
}
