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

        // ==================================================================
        // The forward-power floor: ONE floor, two gates (#571, #453, #238)
        // ==================================================================
        //
        // Below some forward power a forward/reflected pair means nothing —
        // the ratio of two numbers near the coupler's resolution is noise, and
        // on 2026-09-07 that noise read 2.96 on a dummy load the radio itself
        // measured at 1.01. Until Sprint 47 there were THREE answers to where
        // that floor sits, in three files, two of them arguing against each
        // other in their own remarks:
        //
        //   * the alarm floored at a tenth of the transmission's measured
        //     PEAK, and argued that a floor built from the power SETTING
        //     would sit above everything a badly folded-back station can
        //     make (101.2 W into a load, 17.5 W into an open port, same
        //     setting, 2026-08-22);
        //   * the operator-facing SWR floored at a twentieth of COMMANDED
        //     power, and argued that the setting does not chase the voice the
        //     way the peak-relative floor's INPUT does;
        //   * and underneath both, the share arithmetic refused to divide
        //     below 0.05 W, a number measured against a dead key.
        //
        // Both arguments are right about different failure modes, and the
        // 194-sample run of 2026-09-07 cannot referee: both remove all seven
        // false highs on it. The resolution is EBU R128's, which faces the
        // identical problem — a signal whose instantaneous value swings and
        // whose quiet parts are not information — and solves it with two
        // gates rather than one:
        //
        //   ABSOLUTE gate:  the instrument's own resolution floor. Ours is
        //                   ForwardFloorWatts, and it is NOT yet measured —
        //                   see its remarks.
        //   RELATIVE gate:  signal-relative. Ours is the SMALLER of a
        //                   twentieth of commanded and a tenth of peak:
        //                   commanded caps it so a full-power voice envelope
        //                   is judged from five watts up rather than ten,
        //                   and peak lets it fall when the radio folds back
        //                   so the alarm never goes quiet in the case it
        //                   exists for.
        //
        // The floor is the larger of the two gates. Checked by arithmetic
        // against all three measured cases — the numbers are in
        // BelievableForwardFloorWatts's remarks and pinned in
        // TransmitSafetyTests — and against both recorded faults, on which it
        // lands on exactly the floor the alarm used before.

        /// <summary>
        /// THE absolute forward-power floor, in watts: below it a
        /// forward/reflected pair is not believed by anything, whatever was
        /// commanded and whatever the peak was. R128's absolute gate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One number, on purpose, and its value is a placeholder for a
        /// measurement #238 still owes.</b> Three absolute floors answered
        /// this one physical question until Sprint 47: 1 W here (the alarm's
        /// lower bound, measured against a 0.22 W dead key), 0.25 W in
        /// <c>FlexBase</c> (chosen so a one-watt tune carrier would still be
        /// judged, not from any coupler data), and 0.05 W in the share
        /// arithmetic (the dead key again). None of the three was a
        /// measurement of the thing it floors. What IS measured: at 100 W
        /// commanded, every sample at or below 1.40 W forward was noise and
        /// every sample from 8.83 W up was good (2026-09-07); at 5 W
        /// commanded a 4.1 W carrier resolved a 76-percent mismatch cleanly
        /// (2026-09-01). Between 1.4 and 4 W there is no data at all, and
        /// whether the coupler's floor is absolute or moves with the power
        /// setting is exactly the question #238 asks.
        /// </para>
        /// <para>
        /// <b>Why the three collapsed to the LARGEST, not the smallest.</b>
        /// Lowering the alarm's absolute floor would let the alarm judge
        /// samples between a quarter-watt and a watt on QRP and transverter
        /// drive, where a share of the peak is a fraction of a watt and the
        /// ratio genuinely is noise — a protective guard quietly retuned by a
        /// refactor, which is the worst outcome available here. Raising the
        /// other two moves nothing protective: the operator-facing SWR now
        /// says "not measured" for a carrier under one watt where it used to
        /// show a number of unknown worth, and the transmit-check probe says
        /// in words that a sub-watt carrier is too little to judge the load
        /// by (<c>TxTuneProbe.Verdict.MakesPowerLoadNotJudged</c>) instead of
        /// judging it anyway.
        /// </para>
        /// <para>
        /// <b>The discovery test is worth more than this constant.</b>
        /// <c>IntegrationPassRuleTests.Every_forward_power_floor_is_the_same_number</c>
        /// finds every <c>*Watts</c> constant in this assembly and requires
        /// anything named as a floor to equal this; its sibling refuses a
        /// forward-power floor written as a bare literal, which is how the
        /// 0.05 hid for a month behind a comment claiming it was measured.
        /// When #238 lands a number, change it HERE and nowhere else, and the
        /// test says whether anywhere else still needs changing.
        /// </para>
        /// </remarks>
        public const float ForwardFloorWatts = 1f;

        /// <summary>
        /// The relative gate's first term: the share of COMMANDED power below
        /// which a forward reading is not believed. A twentieth.
        /// </summary>
        /// <remarks>
        /// Measured 2026-09-07 across 194 samples, 100 W commanded into the
        /// 400 W dummy load, speech processor and compander ON: every false
        /// high came from a sample at or below 1.40 W forward, and the lowest
        /// good sample was 8.83 W. Five percent lands on 5 W, near the middle
        /// on a log scale, which is the honest place to sit when the true
        /// boundary is unknown. The processor being on makes the case
        /// stronger, not weaker: it REDUCES dynamic range, so those troughs
        /// were genuine silences between words and forward goes lower still
        /// without it.
        /// <para>Commanded power is a SETTING, so unlike forward power it does
        /// not chase the operator's voice; that is the property relied on.
        /// One radio at one power on one day — #238 owns the curve.</para>
        /// <para>Not named <c>...Fraction</c> or <c>...Percent</c>: those
        /// suffixes on a <c>Reflected*</c> constant mean a share coming BACK,
        /// and the reflected-threshold discovery test rightly judges them
        /// against <see cref="ReflectedWarnFraction"/>. This is a share of
        /// what was ASKED FOR.</para>
        /// </remarks>
        public const float ForwardFloorShareOfCommanded = 0.05f;

        /// <summary>
        /// The relative gate's second term: the share of a transmission's own
        /// measured forward-power PEAK below which a reading is not believed.
        /// A tenth.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the term that keeps the floor honest under FOLDBACK. A Flex
        /// reduces its own power into a bad match: on 2026-08-22 the bench
        /// 8600 made 101.2 W into a properly connected dummy load and 17.5 W
        /// into an empty antenna port minutes earlier at the same setting. A
        /// floor from the setting alone sits at 5 W there and still judges
        /// 17.5 W — but a station folded back to 4 W on a hundred-watt setting
        /// is a worse match than the one measured and entirely possible, and a
        /// setting-only floor would go quiet on it. A tenth of a 4 W peak is
        /// 0.4 W, so the floor falls to the absolute gate and the alarm keeps
        /// judging. Taking the SMALLER of the two relative terms is what makes
        /// that work: the peak term can only ever lower the floor.
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
        public const float ForwardFloorShareOfPeak = 0.10f;

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
        /// THE forward-power floor: the lowest forward power at which a
        /// forward/reflected pair means anything, given what the operator
        /// asked for and what the transmission has actually made so far.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The larger of the absolute gate (<see cref="ForwardFloorWatts"/>)
        /// and the relative gate, where the relative gate is the SMALLER of
        /// <paramref name="commandedWatts"/> times
        /// <see cref="ForwardFloorShareOfCommanded"/> and
        /// <paramref name="forwardPeakWatts"/> times
        /// <see cref="ForwardFloorShareOfPeak"/>. A term that is not known
        /// simply drops out: a caller with no peak gets the commanded term
        /// alone, a caller with no commanded power gets the peak term alone,
        /// and a caller with neither gets the absolute gate.
        /// </para>
        /// <para>
        /// <b>Checked against every measured case, by arithmetic, before it
        /// was adopted (#571):</b>
        /// </para>
        /// <list type="bullet">
        /// <item>2026-08-22, open port, 100 W commanded, 17.5 W peak: the
        /// relative gate is the smaller of 5 W and 1.75 W, so the floor is
        /// 1.75 W and the 17.5 W fault is judged. That is the floor the alarm
        /// used before this function existed, to the watt.</item>
        /// <item>2026-09-07, dummy load, 100 W commanded, 107.27 W peak: the
        /// smaller of 5 W and 10.73 W, so the floor is 5 W. All seven false
        /// highs sat at or below 1.40 W and are rejected; the lowest good
        /// sample, 8.83 W, is admitted. The old peak-only floor of 10.73 W
        /// would have rejected that good sample too.</item>
        /// <item>2026-09-01, open port, 5 W commanded, 4.1 W peak: the
        /// smaller of 0.25 W and 0.41 W is 0.25 W, under the absolute gate,
        /// so the floor is 1 W and the 4.1 W fault is judged — again the
        /// alarm's floor from before, exactly.</item>
        /// </list>
        /// <para>
        /// <b>Why the operator-facing SWR passes no peak.</b> The peak term is
        /// weakest at the START of a transmission, when the peak is still
        /// climbing and a tenth of it is under the absolute gate; the alarm
        /// tolerates that because two seconds of settling and three judged
        /// samples stand behind it, and the display has nothing of the kind.
        /// On the 2026-09-07 run a commanded-only floor rejects every false
        /// high and a peak-only one admits the 1.40 W offender for as long as
        /// the peak stays under 14 W. So <c>FlexBase.ComputedSWR</c> asks for
        /// the commanded term alone, and the gated integrator that #571
        /// describes — which will hold a window and a peak — is where the
        /// display gets its second gate.
        /// </para>
        /// </remarks>
        /// <param name="commandedWatts">
        /// What the operator asked for at the moment of the reading — tune
        /// power during a tune carrier, RF power otherwise; normally
        /// <see cref="TransmitPowerReading.CommandedWatts"/>. Zero or less
        /// means unknown.
        /// </param>
        /// <param name="forwardPeakWatts">
        /// The highest forward power seen this transmission — normally
        /// <see cref="ReflectedPowerRun.ForwardPeakWatts"/>. NaN, zero or
        /// less means unknown.
        /// </param>
        public static float BelievableForwardFloorWatts(int commandedWatts, float forwardPeakWatts)
        {
            float relative = float.NaN;
            if (commandedWatts > 0)
                relative = commandedWatts * ForwardFloorShareOfCommanded;
            if (!float.IsNaN(forwardPeakWatts) && forwardPeakWatts > 0f)
            {
                float fromPeak = forwardPeakWatts * ForwardFloorShareOfPeak;
                relative = float.IsNaN(relative) ? fromPeak : Math.Min(relative, fromPeak);
            }
            return float.IsNaN(relative) ? ForwardFloorWatts : Math.Max(ForwardFloorWatts, relative);
        }

        /// <summary>
        /// How much of the forward power is coming back, from 0 to 1, or NaN
        /// when the question cannot be answered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// NaN rather than 0 when there is too little power to judge. Returning
        /// a comfortable number for "no idea" is the exact defect this whole
        /// area exists to fix — the radio's own SWR meter answers 1.008 when it
        /// has nothing useful to say, and two bench sessions were measured
        /// through that reassurance.
        /// </para>
        /// <para>
        /// The guard is the absolute gate, <see cref="ForwardFloorWatts"/>,
        /// and it is defence in depth: every live judgement stands behind
        /// <see cref="BelievableForwardFloorWatts"/> first. It floored at
        /// 0.05 W until Sprint 47 — a dead-key number, thirty times below
        /// where the bench found noise — which was the third of three
        /// absolute floors for one question.
        /// </para>
        /// </remarks>
        public static float ReflectedFractionOf(float forwardWatts, float reflectedWatts)
        {
            if (float.IsNaN(forwardWatts) || float.IsNaN(reflectedWatts)) return float.NaN;
            if (forwardWatts < ForwardFloorWatts) return float.NaN;
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
        /// The SHARE rung of the cut (#224): whether the transmission should be
        /// CUT, not merely warned about, because the warning has fired and a
        /// further sample at real power still shows most of the power coming
        /// back. Only ever true when the operator turned the setting on: an
        /// app that unilaterally unkeys a transmitter has taken the station
        /// away mid-transmission, and some operators — a reactive load, a
        /// tuner mid-cycle, an experimental antenna — would find that
        /// intolerable. The WATTS rung is <see cref="ShouldCutReflectedWatts"/>;
        /// live callers ask <see cref="JudgeReflectedCut"/>, which asks both.
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
        /// What is said when the SHARE rung cuts. It must say what happened,
        /// why, and above all that the operator is NO LONGER TRANSMITTING —
        /// they have no visual cue that it happened and will keep talking.
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
        // Tier 1 (#571): the PROTECTIVE rung, in reflected WATTS
        // ==================================================================
        //
        // #237 ruled two ladders with two jobs and two units: protective
        // watts, diagnostic ratio, and neither may ever be stated in the
        // other's unit. #224 described the cut as firing "above 10 watts" and
        // #237 as "above 10 W reflected" — and the code did NEITHER. What
        // shipped, ShouldCutReflected above, is a RATIO test with a forward
        // floor: forward over ten watts AND forty percent or more coming
        // back. So it inherited exactly the defect tier 1 was meant to be
        // immune to. The 2026-09-07 run proved the point: reflected never
        // exceeded 0.105 W across 194 samples while the display read 2.96.
        //
        // Reflected watts is the quantity that heats the finals. Ten watts
        // back is ten watts of heat whatever forward is doing, and a voice
        // trough cannot fake it — a trough makes forward SMALL, and a small
        // forward cannot have ten watts of itself coming back. That is why it
        // is the honest protective measure, and why it is an ADDITION beside
        // the share rung rather than a replacement: two independent rungs
        // that can disagree informatively (see #237's two cases) is the safer
        // shape, and the share rung is the one that continues the story of
        // the warning the operator just heard.

        /// <summary>
        /// Reflected power, in WATTS, at or above which the cut acts whatever
        /// share of forward it is. Tier 1 of #571; the number is #237's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ten watts is a first number chosen from a register entry, not
        /// from a measurement.</b> #237 ruled the protective ladder "above
        /// 10 W reflected" on 2026-08-26, reasoning from what lands in the PA
        /// as heat; nobody has yet watched this rung fire. The empty-port
        /// bench test #571 still owes — key at real power into an EMPTY
        /// antenna port, never a dummy load, which has nothing to reflect —
        /// is what confirms or moves it. On the one recorded fault at real
        /// power, 2026-08-22, the radio folded itself back to 17.5 W and
        /// 13.4 W of that came back: over this rung. On 2026-09-01 at five
        /// watts, 3.10 W came back: under it, correctly, because 3 W is not
        /// hurting anything and the share warning already covers it.
        /// </para>
        /// <para>
        /// <b>Watts, and never spoken as a ratio.</b> The sentence for this
        /// rung (<see cref="ReflectedCutWattsText"/>) says watts; the share
        /// rung's sentence says percent; neither borrows the other's unit,
        /// and a test holds both to that. Ends in <c>Watts</c> so the
        /// forward-floor discovery test lists it as a rung.
        /// </para>
        /// <para>
        /// <b>Equal to <see cref="ReflectedCutMinForwardWatts"/> by
        /// coincidence, not by design.</b> That one is the share rung's
        /// FORWARD floor — the boundary between worth telling and worth
        /// stopping for, #224. This is REFLECTED heat, #237. Two rulings,
        /// two quantities; do not tie them.
        /// </para>
        /// </remarks>
        public const float ReflectedCutWatts = 10f;

        /// <summary>
        /// Coherent samples in a row at or above <see cref="ReflectedCutWatts"/>
        /// before the watts rung may cut. Two: #224's two-distinct-samples
        /// rule, which applies to both rungs.
        /// </summary>
        /// <remarks>
        /// The share rung gets its two samples by reuse — the warning latched
        /// on an earlier tick, the cut reads this one. The watts rung cannot
        /// borrow that, because it must fire when the share warning never
        /// does: 100 W at a 2.5-to-1 match sends 18 W back at 18 percent,
        /// which is under the 40-percent warning and over this rung, and
        /// #237 says that cut is correct. So <see cref="ReflectedPowerRun"/>
        /// counts the rung's own streak. Persistence, not smoothing: a single
        /// key-down transient cannot cut, and a second hot sample a quarter
        /// of a second later can. Tier 1 judges the momentary value and the
        /// settling rule does not apply to it — latency is a feature of the
        /// diagnostic tier only (#571).
        /// </remarks>
        public const int ReflectedCutSustainedSamples = 2;

        /// <summary>
        /// Whether the transmission should be cut on reflected WATTS alone:
        /// the setting is on, no tune cycle is running, this reading is one
        /// sample and shows at least <see cref="ReflectedCutWatts"/> coming
        /// back, and so did the coherent sample before it.
        /// </summary>
        /// <param name="settingEnabled">The operator's own choice. Never
        /// defaulted to true by a caller.</param>
        /// <param name="reading">Forward and reflected as ONE reading. An
        /// incoherent pair never cuts.</param>
        /// <param name="run">This transmission's accumulated state;
        /// <see cref="ReflectedPowerRun.Observe"/> must already have been
        /// given this reading, so <see cref="ReflectedPowerRun.HotSamples"/>
        /// counts it.</param>
        /// <param name="tuning">True while the antenna tuner runs a cycle.</param>
        /// <remarks>
        /// <b>No forward floor, no share, no nonphysical check — on
        /// purpose.</b> Ten watts back implies more than ten watts forward,
        /// so the share rung's forward floor is satisfied by physics. And a
        /// coherent pair reporting more back than forward is a meter artefact
        /// in the direction of MORE heat; <see cref="ReflectedFractionOf"/>
        /// already treats that as everything coming back rather than as
        /// nothing, and a protective rung errs the same way.
        /// </remarks>
        public static bool ShouldCutReflectedWatts(bool settingEnabled,
                                                   in TransmitPowerReading reading,
                                                   ReflectedPowerRun run, bool tuning)
        {
            if (!settingEnabled || tuning || run == null) return false;
            if (!reading.IsCoherent) return false;
            if (float.IsNaN(reading.ReflectedWatts)
                || reading.ReflectedWatts < ReflectedCutWatts) return false;
            return run.HotSamples >= ReflectedCutSustainedSamples;
        }

        /// <summary>Which rung, if any, ended the transmission.</summary>
        public enum ReflectedCut
        {
            /// <summary>Keep transmitting.</summary>
            None,

            /// <summary>The share rung: the warning had fired and a further
            /// coherent sample above ten watts forward still had forty
            /// percent or more coming back. Spoken in percent.</summary>
            Share,

            /// <summary>The watts rung: ten watts or more coming back, on two
            /// coherent samples in a row, whatever the share. Spoken in
            /// watts.</summary>
            Watts,
        }

        /// <summary>
        /// Both rungs of the cut, in one decision, so that a live caller
        /// cannot consult one and forget the other.
        /// </summary>
        /// <remarks>
        /// The share rung is asked first. When both would fire, the operator
        /// has just heard "76 percent coming back" and the cut sentence
        /// should continue that story in the same unit; the watts rung's
        /// sentence is for the case the share warning never covered.
        /// </remarks>
        public static ReflectedCut JudgeReflectedCut(bool settingEnabled, bool alreadyWarned,
                                                     in TransmitPowerReading reading,
                                                     ReflectedPowerRun run, bool tuning)
        {
            if (ShouldCutReflected(settingEnabled, alreadyWarned, reading, tuning))
                return ReflectedCut.Share;
            if (ShouldCutReflectedWatts(settingEnabled, reading, run, tuning))
                return ReflectedCut.Watts;
            return ReflectedCut.None;
        }

        /// <summary>
        /// What is said when the WATTS rung cuts. Watts, never a percentage
        /// (#237), and it must say the operator is no longer on the air.
        /// </summary>
        public static string ReflectedCutWattsText(float reflectedWatts, string antennaName)
        {
            int watts = (int)Math.Round(reflectedWatts);
            bool named = !string.IsNullOrWhiteSpace(antennaName);
            string key = named ? "audio.ptt.reflected_cut_watts_on" : "audio.ptt.reflected_cut_watts";
            return named
                ? Lexicon.Get(key, ("watts", watts), ("antenna", antennaName))
                : Lexicon.Get(key, ("watts", watts));
        }

        /// <summary>
        /// The cut sentence for whichever rung fired, in that rung's own unit.
        /// </summary>
        public static string ReflectedCutTextFor(ReflectedCut rung, in TransmitPowerReading reading,
                                                 string antennaName)
        {
            switch (rung)
            {
                case ReflectedCut.Share: return ReflectedCutText(reading.ReflectedShare, antennaName);
                case ReflectedCut.Watts: return ReflectedCutWattsText(reading.ReflectedWatts, antennaName);
                default: return "";
            }
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
