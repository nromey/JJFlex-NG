using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using JJTrace;
using Radios.Fixer;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The transmit-audio boundary: supplies the Fixer Tool's last two host
    /// measurements — <c>TransmitStageSet.Hosts.RunInjectedTransmit</c>
    /// (stage 3: tones and a reference voice with the microphone bypassed) and
    /// <c>RunSpokenTransmit</c> (stage 4: the operator's own voice) — behind
    /// <see cref="FixerTransmitGate"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shaped on <see cref="FixerTransmitBoundary"/>, and in the same
    /// namespace for the same reason: it must name <c>FlexBase</c> to do its
    /// job, and Radios.Fixer is structurally forbidden from that by a
    /// reflection test. <b>The host measures, the engine interprets</b> —
    /// every word the operator reads about these results belongs to
    /// <c>TransmitStages.Injected</c> and <c>TransmitStages.Spoken</c>; this
    /// file owns only whether the measurement may be taken and what the meters
    /// actually did.
    /// </para>
    /// <para>
    /// <b>An instance, where the transmitter boundary is static, because the
    /// two stages share evidence.</b> Stage 4's whole reason to exist is the
    /// comparison against stage 3 — the two runs differ in exactly one thing —
    /// so the injected run's meter capture is held here and read into the
    /// spoken run's detail through <see cref="TxDifferential"/>. One instance
    /// per dialog, exactly as the gate is.
    /// </para>
    /// <para>
    /// <b>Each stage keys the radio exactly once, and only through a granted
    /// <see cref="FixerTransmitGate.Decision"/>.</b> <c>radioReachable</c> and
    /// <c>rigIsKeyed</c> are read from the radio here, never taken from a
    /// caller. Key-down time is charged when the RADIO confirms transmitting,
    /// and the unkey runs in a finally, unconditionally, and is confirmed —
    /// with the injection sources disarmed in the same finally so nothing
    /// armed here can ride the operator's own next key-down.
    /// </para>
    /// <para>
    /// <b>The microphone is never on the air during stage 3.</b> The tone is
    /// armed BEFORE the key goes down, so the self-clocked injection source
    /// owns the transmit stream from the first frame; the voice recording is
    /// engaged before the tone is released, so the slot passes source to
    /// source with the microphone never taking it back mid-stage.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> This runs when something is already broken. A
    /// refusal or a failure comes back as facts whose fields honestly say what
    /// was not measured — never as an exception, and never as a plausible
    /// number.
    /// </para>
    /// </remarks>
    public sealed class FixerTransmitAudioBoundary
    {
        /// <summary>
        /// Make the reference voice ready to transmit on this rig — typically
        /// by loading the shipped reference recording into
        /// <c>FlexBase.TxFilePlayer</c>. Returns empty when a recording is
        /// loaded and ready, otherwise why not, written to be read as it
        /// stands. Loading is the host's job because decoding audio files
        /// lives in the UI assembly, not here.
        /// </summary>
        public delegate string VoicePreparer(FlexBase rig);

        /// <summary>
        /// The PC-side microphone the transmit stream captures from, for the
        /// spoken stage's evidence — the same resolver stage 1 used, so the
        /// two stages name the same device the same way. Empty strings when
        /// nothing resolves.
        /// </summary>
        public delegate (string device, string hostApi) MicPathInfo();

        /// <summary>
        /// This computer's half of the transmit chain facts — the microphone
        /// Windows offers, its mute, its level, the open stream. Supplied by the
        /// host because the radio layer cannot see any of it, and because
        /// inventing a value for it would be worse than reporting it unchecked.
        /// Null leaves the walk's computer-side stages honestly unmeasured.
        /// </summary>
        public delegate IReadOnlyList<DiagnosticFact> PcChainFacts();

        /// <summary>
        /// The fallback key-up moment, in milliseconds from the start of the
        /// countdown, for a host that publishes no pacing of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The count runs UNKEYED, and those milliseconds are the operator's
        /// abort window (#236, #261).</b> A countdown after MOX would burn keyed
        /// dead air against the gate's budget, and one that promised a transmit
        /// before the radio confirmed would have the operator talking into a
        /// transmitter that may never key. So: count unkeyed, issue the key-up
        /// near the end of the count, and speak "go" only on MOX confirmation.
        /// The gap between the two is honest MOX latency.
        /// </para>
        /// <para>
        /// <b>THIS WAS A HAND-COPIED CONSTANT AND IT DRIFTED.</b> It read 300,
        /// with a remark saying "150 ms count steps, so the third tone starts at
        /// 300 ms" — and <c>EarconPlayer.CountdownStepMs</c> had been 300 ms for
        /// months, putting the third tone at 600 ms. So every keying stage
        /// raised RF during the SECOND dit and then played the rest of the
        /// warning at an operator who was already transmitting. A field trace of
        /// 2026-08-29 caught it: countdown at 152330 ms, carrier at 152645, with
        /// "Transmitting a tune carrier" spoken at 152671 on top of the count.
        /// Nothing failed, nothing conflicted, and no test could see it, because
        /// the two numbers live in assemblies that cannot see each other.
        /// </para>
        /// <para>
        /// <b>So the number is no longer stated here.</b> The host passes it in
        /// beside the countdown action, derived from the sound it is actually
        /// going to play — see <see cref="Create"/>. This constant is only what
        /// a host that supplies nothing gets, and it is deliberately LATE rather
        /// than early: waiting too long costs a moment of silence, and keying
        /// too early costs the operator the window in which they could have
        /// stopped it. Never shorten it without treating that as a safety
        /// change.
        /// </para>
        /// </remarks>
        public const int DefaultCountdownKeyUpMs = 2000;

        /// <summary>
        /// The beat of silence between the countdown's last sound and the
        /// moment the operator is asked to speak. Noel, 2026-08-30: <i>"if
        /// there's 300 ms of silence to give it time, that's ok too."</i>
        /// </summary>
        public const int DefaultCountdownSettleMs = 300;

        /// <summary>
        /// How long the countdown SOUND lasts, when the host does not say.
        /// Deliberately the full transmit figure at the shipping timings, so a
        /// host that reports nothing still waits the sound out rather than
        /// talking over it.
        /// </summary>
        public const int DefaultCountdownDurationMs = 3840;

        private readonly FixerTransmitGate _gate;
        private readonly FixerTransmitBoundary.RadioSource _radio;
        private readonly VoicePreparer _prepareVoice;
        private readonly MicPathInfo _pcMicrophone;
        private readonly PcChainFacts _pcChainFacts;
        private readonly Func<bool> _stopRequested;
        private readonly Action _speakNow;
        private readonly Action _speakDone;
        private readonly Action _countdown;

        /// <summary>
        /// When to issue the key-up, in milliseconds from the start of the
        /// countdown. Published by the host beside the sound it plays, never
        /// copied from it — see <see cref="DefaultCountdownKeyUpMs"/>.
        /// </summary>
        private readonly int _countdownKeyUpAtMs;

        /// <summary>
        /// How long the countdown SOUND lasts, in milliseconds from its start.
        /// Published by the host beside the sound it plays, never copied.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The key-up is not the end of the countdown, and conflating them
        /// is what this exists to stop.</b> The count starts, the key-up goes
        /// out on the last dit so the radio is transmitting by the landing —
        /// and the LANDING IS STILL PLAYING for another second and a half. A
        /// stage that cues the operator the moment MOX confirms therefore says
        /// "speak now" over its own countdown, and then measures the landing as
        /// if it were the operator's voice.
        /// </para>
        /// <para>
        /// Found by the Sprint 42 integration pass. The record stage already
        /// waits the sound out — <c>MicCountdownSoundMs</c>, fixed when it was
        /// discovered to be 600 ms short and leaving the landing ringing inside
        /// the capture, which makes a quiet shack read as noisy. The transmit
        /// stages never had the equivalent wait at all.
        /// </para>
        /// </remarks>
        private readonly int _countdownDurationMs;

        /// <summary>
        /// Running since the countdown began, so the wait above is measured
        /// from the sound's own start rather than from whenever MOX happened to
        /// confirm. Null until a countdown has actually been started.
        /// </summary>
        private Stopwatch _sinceCountdown;

        /// <summary>
        /// Stage 3's meter capture, kept for stage 4's comparison. Replaced on
        /// an injected re-run; null until the injected stage has really keyed.
        /// </summary>
        private TxDifferential.TxRunSample _injectedSample;

        private FixerTransmitAudioBoundary(FixerTransmitGate gate,
                                           FixerTransmitBoundary.RadioSource radio,
                                           VoicePreparer prepareVoice,
                                           MicPathInfo pcMicrophone,
                                           PcChainFacts pcChainFacts,
                                           Func<bool> stopRequested,
                                           Action speakNow,
                                           Action speakDone,
                                           Action countdown,
                                           int countdownKeyUpAtMs,
                                           int countdownDurationMs)
        {
            _gate = gate;
            _radio = radio;
            _prepareVoice = prepareVoice;
            _pcMicrophone = pcMicrophone;
            _pcChainFacts = pcChainFacts;
            _stopRequested = stopRequested;
            _speakNow = speakNow;
            _speakDone = speakDone;
            _countdown = countdown;
            // Never earlier than the fallback would have been on its own: a
            // host that reports nonsense must not be able to bring RF forward.
            _countdownKeyUpAtMs = countdownKeyUpAtMs > 0
                ? countdownKeyUpAtMs : DefaultCountdownKeyUpMs;

            // Same shape, same reason: a host that reports nothing or nonsense
            // waits the full figure out rather than cutting it short. Erring
            // long costs a moment of silence on a keyed but silent SSB
            // transmitter, which radiates nothing; erring short puts our own
            // sound inside the operator's measurement.
            _countdownDurationMs = countdownDurationMs > 0
                ? countdownDurationMs : DefaultCountdownDurationMs;
        }

        /// <summary>
        /// Build the boundary. Null when the gate or the radio source is
        /// missing — null is the engine's "the host wired nothing" signal, and
        /// for transmitting stages it is what stands between a half-wired host
        /// and a keyed radio. The other hooks are conveniences and may be
        /// null: a missing voice preparer leaves the voice probe honestly
        /// unavailable, missing announcements announce nothing, and a missing
        /// stop hook means the stages simply run to their own bounded ends.
        /// </summary>
        /// <param name="gate">Holds every fact that decides whether RF may go out.</param>
        /// <param name="radio">Where the live radio comes from.</param>
        /// <param name="prepareVoice">Makes the reference recording ready, or says why not.</param>
        /// <param name="pcMicrophone">Names the PC microphone for the spoken evidence.</param>
        /// <param name="stopRequested">
        /// Polled between measurements. True ends the stage early — the
        /// dialog's stage timeout reaches the boundary through this, since the
        /// host delegate signatures carry no cancellation token.
        /// </param>
        /// <param name="speakNow">
        /// Told when the radio has confirmed keying for the spoken stage and
        /// listening has begun. The host owns the words; a blind operator has
        /// no other way to know the moment to speak has arrived.
        /// </param>
        /// <param name="speakDone">
        /// Told after the spoken stage's carrier is confirmed down — only if
        /// <paramref name="speakNow"/> was told, so nobody is told to stop
        /// speaking who was never asked to start.
        /// </param>
        /// <param name="countdown">
        /// Starts the transmit countdown tones (#261) — fire-and-forget, and
        /// the count runs UNKEYED with the key-up issued at
        /// the host's published key-up moment. Both keying stages use it: on
        /// the spoken stage it counts the operator in, and on the injected
        /// stage — where the operator does nothing — it is the warning that
        /// RF is imminent, which this tool otherwise does not give (Noel,
        /// 2026-08-26).
        /// </param>
        /// <param name="pcChainFacts">
        /// This computer's half of the transmit chain facts, for the walk taken
        /// while keyed (#400). Null leaves the walk's computer-side stages
        /// honestly unmeasured rather than healthy — the walk still runs, and
        /// its census says how much of the chain was actually seen.
        /// </param>
        public static FixerTransmitAudioBoundary Create(FixerTransmitGate gate,
                                                        FixerTransmitBoundary.RadioSource radio,
                                                        VoicePreparer prepareVoice = null,
                                                        MicPathInfo pcMicrophone = null,
                                                        PcChainFacts pcChainFacts = null,
                                                        Func<bool> stopRequested = null,
                                                        Action speakNow = null,
                                                        Action speakDone = null,
                                                        Action countdown = null,
                                                        int countdownKeyUpAtMs =
                                                            DefaultCountdownKeyUpMs,
                                                        int countdownDurationMs =
                                                            DefaultCountdownDurationMs)
        {
            if (gate == null || radio == null) return null;
            return new FixerTransmitAudioBoundary(gate, radio, prepareVoice, pcMicrophone,
                                                  pcChainFacts, stopRequested, speakNow,
                                                  speakDone, countdown, countdownKeyUpAtMs,
                                                  countdownDurationMs);
        }

        // ================================================================
        // The transmit chain walk, taken WHILE KEYED
        // ================================================================

        /// <summary>
        /// Walk the thirteen-stage transmit chain right now, at this instant,
        /// with the radio keyed and audio in the path (#400).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The moment is the whole point.</b> Three stages of the walk can
        /// only be measured during a transmission — stage 2, the microphone
        /// actually capturing; stage 11, what the radio says it hears; stage 12,
        /// radio frequency out of the radio — and the Audio Workshop, which is
        /// the only place that ever ran the walk, cannot key a radio. Its report
        /// says <i>"transmit and run the test again"</i> three times, on every
        /// run, for ever. This is the transmit.
        /// </para>
        /// <para>
        /// <b>It is called from inside the keyed block on purpose, and it must
        /// stay there.</b> Moved a few lines later, past the unkey, every one of
        /// those three stages would come back "nothing to check" and the walk
        /// would be worth precisely what the Workshop's already is. It reads
        /// properties and meters and sleeps for nothing, so it costs the
        /// transmission no measurable time.
        /// </para>
        /// <para>
        /// Never throws, and never returns a walk it did not take: a failure
        /// leaves the stage reporting what its own probes found and inventing no
        /// chain answer, which is the same honesty every unwired delegate gets.
        /// </para>
        /// </remarks>
        private TransmitCheckResult WalkWhileKeyed(FlexBase rig)
        {
            try
            {
                IReadOnlyList<DiagnosticFact> pc = null;
                try { pc = _pcChainFacts?.Invoke(); }
                catch (Exception ex)
                {
                    // The computer's half failing must not cost the radio's
                    // half. The walk's census then says how much was seen.
                    Tracing.TraceLine("FixerTransmitAudioBoundary: the computer's chain facts "
                                      + "failed — " + ex.Message, TraceLevel.Warning);
                }

                TransmitCheckResult walk = TransmitChainCheck.Run(rig, pc);
                Tracing.TraceLine("FixerTransmitAudioBoundary: transmit chain walked while keyed — "
                                  + walk.Verdict, TraceLevel.Info);
                return walk;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerTransmitAudioBoundary: the transmit chain walk failed — "
                                  + ex.Message, TraceLevel.Error);
                return null;
            }
        }

        // ================================================================
        // Stage 3: injected transmit
        // ================================================================

        /// <summary>
        /// Build the host's injected-transmit measurement. Null when the stage
        /// id is missing, so the gate's once-per-stage rule always has
        /// something real to charge.
        /// </summary>
        public Func<InjectedTransmitFacts> InjectedTransmit(string stageId)
        {
            if (string.IsNullOrWhiteSpace(stageId)) return null;

            return () =>
            {
                try
                {
                    return RunInjected(stageId);
                }
                catch (Exception ex)
                {
                    // The finally inside RunInjected has already unkeyed on any
                    // path that keyed. This catch only keeps a fault in the
                    // reporting from replacing the diagnosis with a crash.
                    Tracing.TraceLine("FixerTransmitAudioBoundary: injected stage failed — "
                                      + ex.Message, TraceLevel.Error);
                    return new InjectedTransmitFacts
                    {
                        Detail = "The injected check failed unexpectedly: " + ex.Message,
                    };
                }
            };
        }

        private InjectedTransmitFacts RunInjected(string stageId)
        {
            var facts = new InjectedTransmitFacts();

            FlexBase rig = FixerTransmitBoundary.Safely(_radio);

            // Conditioning is a fact about the software chain, not the radio's
            // transmitter — readable, and worth reading, whether or not
            // anything transmits. The explanation must not name a setting
            // without consulting it.
            facts.ConditioningActive = TxDifferentialCapture.ConditioningActive(rig);

            // Facts the gate is not allowed to take on trust, read from the
            // radio itself. This stage keys MOX, so the transmit power — not
            // tune power — is what the low-power ceiling judges (#180).
            FixerTransmitGate.Decision d = _gate.Request(
                _gate.RunId, stageId, stageTransmits: true,
                radioReachable: rig != null,
                rigIsKeyed: FixerTransmitBoundary.ReadKeyed(rig),
                transmitPowerWatts: FixerTransmitBoundary.ReadTransmitPowerWatts(
                    rig, tuneCarrier: false));

            if (!d.Allowed)
            {
                Tracing.TraceLine("FixerTransmitAudioBoundary: injected transmit refused ("
                                  + d.Why + ") — " + d.Explanation, TraceLevel.Warning);
                // The gate's own words, verbatim — every refusal it writes
                // already says that nothing was transmitted, and wrapping it
                // would say it twice.
                facts.Detail = d.Explanation;
                return facts;
            }

            // The injected probes ride the PC-audio transmit path; the rig
            // already knows, in reviewed words, every way that path can be
            // shut. Keying with the path shut would put dead air on the air
            // and then truthfully report that nothing arrived — a measurement
            // of the wrong thing.
            string pathTrouble = SafeTonePathTrouble(rig);
            if (pathTrouble.Length > 0)
            {
                Tracing.TraceLine("FixerTransmitAudioBoundary: injected transmit not run — "
                                  + pathTrouble, TraceLevel.Warning);
                facts.Detail = "Nothing was transmitted — the injection path is not "
                             + "available. " + pathTrouble;
                return facts;
            }

            var results = new List<TxProbeSet.ProbeResult>(3);
            var detail = new StringBuilder();
            detail.Append("Probes injected at ")
                  .Append(TxAudioProbe.InjectLevelDb.ToString("0.#", CultureInfo.InvariantCulture))
                  .AppendLine(" dBFS, with the microphone replaced at the injection point "
                              + "for the whole transmission.");

            bool everKeyed = false;
            bool stopped = false;

            // The scope decides what to do about the mode, switches if needed,
            // and reads the transmit filter AFTER any switch — the order that
            // keeps the ladder honest. Its Dispose puts the mode back, and
            // runs after the finally below has unkeyed.
            using (TxToneLadderScope scope = TxToneLadderScope.Enter(rig))
            // Armed BEFORE the countdown, not at key-down and certainly not at
            // the gate's NoteKeyed — which fires only once the RADIO confirms,
            // up to a second and a half later, and may never fire at all. A
            // radio transmitting while reporting that it is not is exactly the
            // transmit that most needs a way out. Arming here also means Escape
            // during the count stops the check before any RF (#236).
            using (TransmitKillSwitch.Arm(rig, "the injected transmit check"))
            {
                try
                {
                    // Arm the tone BEFORE keying, so the injection source owns
                    // the transmit stream from the first frame and the
                    // operator's microphone is never on the air in this stage.
                    SafeDo(() =>
                    {
                        rig.TxToneLevelDb = TxAudioProbe.InjectLevelDb;
                        rig.TxToneFrequency = TxAudioProbe.SingleToneHz;
                        rig.TxToneStart();
                    }, "arm tone");

                    // The countdown, UNKEYED (#261). The operator does nothing
                    // in this stage — the count is the warning that RF is
                    // imminent, ruled in by Noel 2026-08-26, and currently the
                    // only cue standing between an idle stage and a live
                    // transmitter. Key-up is issued on the third tone.
                    if (!CountdownThenReadyToKey())
                    {
                        facts.Detail = StoppedDuringCountdownText;
                        return facts;
                    }

                    Tracing.TraceLine("FixerTransmitAudioBoundary: keying for the injected "
                                      + "probes", TraceLevel.Info);
                    if (!TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox))
                    {
                        facts.Detail = "Nothing was transmitted, because no way to stop the "
                                     + "transmission was in place.";
                        return facts;
                    }
                    everKeyed = WaitForMox(rig, wantKeyed: true);

                    if (!everKeyed)
                    {
                        Tracing.TraceLine("FixerTransmitAudioBoundary: the radio never "
                                          + "reported transmitting", TraceLevel.Warning);
                        facts.Detail = "The radio was asked to transmit and never reported "
                                     + "doing so, so nothing was measured.";
                        return facts;
                    }

                    // Charged when the RADIO confirms, not when the setter
                    // returned — the same rule the transmitter probe follows.
                    _gate.NoteKeyed(stageId);

                    // ---- probe: the single tone ----
                    (double refDb, bool refRead) = MeasureScMic(
                        rig, TxAudioProbe.RungSettleMs, TxAudioProbe.RungWindowMs);
                    results.Add(TxAudioProbe.Judge(TxProbeSet.Probe.SingleTone, refRead, refDb,
                        "a steady " + TxAudioProbe.SingleToneHz + " hertz tone"));

                    // ---- probe: the ladder ----
                    if (!scope.CanRun)
                    {
                        results.Add(new TxProbeSet.ProbeResult(TxProbeSet.Probe.ToneLadder,
                            TxProbeSet.Outcome.Unavailable, scope.BlockedReason));
                    }
                    else
                    {
                        var readings = new List<TxToneLadder.RungReading>(scope.Rungs.Length);
                        foreach (TxToneLadder.Rung rung in scope.Rungs)
                        {
                            if (StopRequested()) { stopped = true; break; }
                            SafeDo(() => rig.TxToneFrequency = rung.Hz, "rung frequency");
                            (double db, bool read) = MeasureScMic(
                                rig, TxAudioProbe.RungSettleMs, TxAudioProbe.RungWindowMs);
                            readings.Add(new TxToneLadder.RungReading(rung, db, read));
                        }
                        results.Add(TxAudioProbe.LadderProbe(refRead, refDb, readings,
                                                             scope.Passband));

                        // Back to the reference so the capture below reads a
                        // known tone, not whichever rung happened to be last.
                        SafeDo(() => rig.TxToneFrequency = TxAudioProbe.SingleToneHz,
                               "reference frequency");
                    }

                    // A capture under the tone — replaced by a voice-time one
                    // below when the voice runs, because a capture taken while
                    // a voice-shaped signal travels the conditioning chain is
                    // the fairest partner for the spoken run.
                    if (!StopRequested())
                    {
                        SleepUnlessStopped(TxAudioProbe.RungSettleMs);
                        _injectedSample = TxDifferentialCapture.Capture(
                            rig, TxDifferential.RunKind.Injected);
                    }

                    // ---- probe: the reference voice ----
                    if (StopRequested())
                    {
                        stopped = true;
                        results.Add(new TxProbeSet.ProbeResult(TxProbeSet.Probe.Voice,
                            TxProbeSet.Outcome.NotAttempted,
                            "the test was stopped before the voice played"));
                    }
                    else
                    {
                        string voiceTrouble = PrepareVoice(rig);
                        if (voiceTrouble.Length > 0)
                        {
                            results.Add(new TxProbeSet.ProbeResult(TxProbeSet.Probe.Voice,
                                TxProbeSet.Outcome.Unavailable, voiceTrouble));
                        }
                        else
                        {
                            // Engage the voice BEFORE releasing the tone: the
                            // injection mux hands the slot source to source,
                            // and the microphone never takes it back
                            // mid-stage.
                            string voiceName = SafeContentName(rig);
                            SafeDo(() => rig.TxFileStart(), "start voice");
                            SafeDo(() => rig.TxToneStop(), "release tone");

                            (double vDb, bool vRead) = MeasureVoice(rig);

                            // The voice-time capture supersedes the tone-time
                            // one for the stage 4 comparison — see above.
                            _injectedSample = TxDifferentialCapture.Capture(
                                rig, TxDifferential.RunKind.Injected);

                            results.Add(TxAudioProbe.Judge(TxProbeSet.Probe.Voice, vRead, vDb,
                                voiceName.Length > 0
                                    ? "the reference recording \"" + voiceName + "\""
                                    : "the reference recording"));
                        }
                    }

                    // THE WALK, WHILE STILL KEYED (#400). Last inside the try,
                    // so every probe above has already exercised the path and
                    // the meters it reads are the meters of a transmission in
                    // progress. This is the moment Noel described: keyed, a
                    // known tone at a known level, the microphone replaced at
                    // the injection point, and nothing depending on anyone
                    // speaking.
                    facts.ChainWalk = WalkWhileKeyed(rig);

                    // And where it happened (#399) — read here rather than
                    // afterwards, because the operator may retune between
                    // stages and a run measured at two frequencies has to say
                    // which stage ran where.
                    facts.Conditions = StationConditions.Line(rig);
                }
                finally
                {
                    // READ THE STOP HERE, while the kill is still armed. The
                    // kill flag is cleared on disarm — which is correct, the
                    // next stage must not start pre-stopped — so asking after
                    // the using block would always answer no, and a run cut
                    // short during its FIRST measurement would set none of the
                    // flags below and read as a complete one. Nothing could end
                    // this stage early before the operator had a real abort
                    // (#236); now something can.
                    if (StopRequested()) stopped = true;

                    // Every path out lands here. Unkey FIRST and confirm it
                    // took; then disarm both sources, unconditionally, so
                    // nothing armed by this stage rides the operator's own
                    // next key-down; then tell the gate. The scope's mode
                    // restore runs after this block, on a radio that is no
                    // longer transmitting.
                    UnkeyMox(rig);
                    SafeDo(() => rig.TxToneStop(), "disarm tone");
                    SafeDo(() => rig.TxFileStop(), "disarm voice");
                    _gate.NoteUnkeyed();
                }

                // Conditions travel with the measurement.
                if (scope.Plan.Action == TxToneLadder.ModeAction.SwitchAndRestore)
                    detail.AppendLine("The radio was in " + scope.Plan.CurrentMode
                        + " and was switched to " + scope.Plan.SwitchTo
                        + " for the test, then put back.");
                if (scope.Passband.Known)
                    detail.AppendLine("Transmit filter during the test: " + scope.Passband + ".");
            }

            if (stopped)
                detail.AppendLine("The test was stopped before every probe had run; the "
                                + "results above are the ones that finished.");
            detail.Append(TxAudioProbe.DescribeSample(_injectedSample));

            facts.Probes = results;
            facts.Detail = detail.ToString().TrimEnd();
            return facts;
        }

        // ================================================================
        // Stage 4: spoken transmit
        // ================================================================

        /// <summary>
        /// Build the host's spoken-transmit measurement. Null when the stage
        /// id is missing.
        /// </summary>
        public Func<SpokenTransmitFacts> SpokenTransmit(string stageId)
        {
            if (string.IsNullOrWhiteSpace(stageId)) return null;

            return () =>
            {
                try
                {
                    return RunSpoken(stageId);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("FixerTransmitAudioBoundary: spoken stage failed — "
                                      + ex.Message, TraceLevel.Error);
                    return new SpokenTransmitFacts
                    {
                        Detail = "The spoken check failed unexpectedly: " + ex.Message,
                    };
                }
            };
        }

        private SpokenTransmitFacts RunSpoken(string stageId)
        {
            var facts = new SpokenTransmitFacts();

            FlexBase rig = FixerTransmitBoundary.Safely(_radio);

            FixerTransmitGate.Decision d = _gate.Request(
                _gate.RunId, stageId, stageTransmits: true,
                radioReachable: rig != null,
                rigIsKeyed: FixerTransmitBoundary.ReadKeyed(rig),
                transmitPowerWatts: FixerTransmitBoundary.ReadTransmitPowerWatts(
                    rig, tuneCarrier: false));

            if (!d.Allowed)
            {
                Tracing.TraceLine("FixerTransmitAudioBoundary: spoken transmit refused ("
                                  + d.Why + ") — " + d.Explanation, TraceLevel.Warning);
                // The gate's own words, verbatim — see the injected stage.
                facts.Detail = d.Explanation;
                return facts;
            }

            string micSource = SafeMicSource(rig);
            string pathTrouble = TxAudioProbe.SpokenPathTrouble(
                SafeMode(rig), micSource, SafePcAudio(rig));
            if (pathTrouble.Length > 0)
            {
                Tracing.TraceLine("FixerTransmitAudioBoundary: spoken transmit not run — "
                                  + pathTrouble, TraceLevel.Warning);
                facts.Detail = "Nothing was transmitted. " + pathTrouble;
                return facts;
            }

            // Which microphone is in the path — the whole difference between
            // this stage and the injected one, so the evidence must name it.
            if (string.Equals(micSource, "PC", StringComparison.OrdinalIgnoreCase))
            {
                (string device, string hostApi) = DescribePcMicrophone();
                facts.Device = device;
                facts.HostApi = hostApi;
            }
            else if (micSource.Length > 0)
            {
                // Not a computer path at all: the operator speaks into the
                // radio's own jack, and no host API is involved.
                facts.Device = "the radio's " + micSource + " input";
            }

            bool everKeyed = false;
            bool cuedToSpeak = false;
            double peakDb = double.NaN;
            bool meterRead = false;
            bool stoppedShort = false;
            TxDifferential.TxRunSample spokenSample = null;

            // Armed before the count-in, for the same reasons as the injected
            // stage: the operator's Escape has to reach the transmitter while
            // this stage blocks the UI thread for its whole eight-second listen.
            using (TransmitKillSwitch.Arm(rig, "the spoken transmit check"))
            try
            {
                // Count the operator in, UNKEYED (#261): three tones, key-up
                // issued on the third, and the spoken "go" (speakNow, below)
                // only on MOX confirmation — so a radio that never keys never
                // gets a "go", no keyed dead air is burned against the gate's
                // budget, and the gap between the third tone and "go" is
                // honest MOX latency.
                if (!CountdownThenReadyToKey())
                {
                    facts.Detail = StoppedDuringCountdownText;
                    return facts;
                }

                Tracing.TraceLine("FixerTransmitAudioBoundary: keying for the spoken check",
                                  TraceLevel.Info);
                if (!TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox))
                {
                    facts.Detail = "Nothing was transmitted, because no way to stop the "
                                 + "transmission was in place.";
                    return facts;
                }
                everKeyed = WaitForMox(rig, wantKeyed: true);

                if (!everKeyed)
                {
                    Tracing.TraceLine("FixerTransmitAudioBoundary: the radio never reported "
                                      + "transmitting", TraceLevel.Warning);
                    facts.Detail = "The radio was asked to transmit and never reported "
                                 + "doing so, so nothing was measured.";
                    return facts;
                }

                _gate.NoteKeyed(stageId);

                // AND ONLY NOW IS THE COUNTDOWN ACTUALLY OVER. The key-up goes
                // out on the last dit so the radio is up by the landing; the
                // landing itself is still sounding for another second and a
                // half after that. Cueing at MOX confirmation said "speak now"
                // over our own counting and then measured the landing as if it
                // were the operator's voice, through a live microphone in the
                // same room as the speaker playing it.
                if (!WaitOutTheCountdown())
                {
                    Tracing.TraceLine("FixerTransmitAudioBoundary: stopped while the countdown "
                                      + "finished sounding; nothing was asked of the operator",
                                      TraceLevel.Info);
                    facts.Detail = "The check was stopped before you were asked to speak.";
                    return facts;
                }

                // Only now — after the radio has confirmed AND the countdown
                // has finished — is the operator told to speak. A cue before
                // confirmation would have them talking into a transmitter that
                // may never key.
                cuedToSpeak = true;
                Witness(_speakNow, "speakNow");

                (peakDb, meterRead) = MeasureScMic(rig, settleMs: 0,
                                                   windowMs: TxAudioProbe.SpokenListenMs);

                // Captured at the end of the listen, while still keyed — the
                // meters read the transmission, not the moment after it.
                spokenSample = TxDifferentialCapture.Capture(
                    rig, TxDifferential.RunKind.Spoken);

                // THE WALK, WHILE STILL KEYED (#400), with the operator's own
                // microphone in the path. Stage 3's walk and this one differ in
                // exactly the one thing the two stages differ in, so a chain
                // stage that dies here and lives there names the microphone.
                facts.ChainWalk = WalkWhileKeyed(rig);

                // And where it happened (#399), read while keyed.
                facts.Conditions = StationConditions.Line(rig);
            }
            finally
            {
                // Read while the kill is still armed — see the injected stage.
                stoppedShort = StopRequested();

                UnkeyMox(rig);
                _gate.NoteUnkeyed();

                // Told after the carrier is confirmed down, and only if the
                // start cue went out — its whole purpose is "you can stop
                // talking, and you are no longer on the air".
                if (cuedToSpeak) Witness(_speakDone, "speakDone");
            }

            facts.Attempted = meterRead;
            facts.ReachedRadio = meterRead && TxAudioProbe.Reached(peakDb);

            var detail = new StringBuilder();

            // The listen is a CEILING, not a duration — the sampling loop
            // breaks the moment a stop arrives, so a stopped run measured for
            // less than this and must not report the full window. It could not
            // happen before the operator had a real abort (#236); now it can,
            // and a peak read over two seconds described as an eight-second
            // listen is a measurement of the wrong thing wearing the right
            // number.
            if (stoppedShort)
                detail.AppendLine("The check was stopped before the listen finished, so "
                                + "anything below was measured over less than the full "
                                + "window.");
            else
                detail.Append("Listened for ")
                      .Append((TxAudioProbe.SpokenListenMs / 1000.0)
                              .ToString("0.#", CultureInfo.InvariantCulture))
                      .AppendLine(" seconds while keyed.");
            detail.AppendLine(meterRead
                ? "SC_MIC peaked at " + peakDb.ToString("0.#", CultureInfo.InvariantCulture)
                  + " dBFS over the listen."
                : "The radio's transmit audio meter (SC_MIC) never updated during the "
                  + "listen, so nothing was measured.");
            detail.AppendLine(TxAudioProbe.DescribeSample(spokenSample));
            detail.Append(TxAudioProbe.SpokenComparison(_injectedSample, spokenSample));

            facts.Detail = detail.ToString().TrimEnd();
            return facts;
        }

        // ================================================================
        // Keying plumbing
        // ================================================================

        /// <summary>
        /// Start the countdown tones and wait, unkeyed, until the moment the
        /// key-up should be issued. False
        /// when a stop arrived during the count — the caller must then not
        /// key. A missing hook counts silently and still paces the key-up,
        /// so the timing an operator learns does not change with the sound.
        /// </summary>
        private bool CountdownThenReadyToKey()
        {
            // Started here rather than inside the helper, because the helper is
            // also called directly by tests and by the tune probe. A stopwatch
            // from the sound's own start is what lets a later stage know how
            // much of the countdown is still playing.
            _sinceCountdown = Stopwatch.StartNew();
            return CountUnkeyedThenReadyToKey(_countdown, _stopRequested, _countdownKeyUpAtMs);
        }

        /// <summary>
        /// Wait, keyed, until the countdown has finished sounding and a beat of
        /// silence has passed. Returns false if a stop arrived while waiting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Called only by the spoken stage, deliberately.</b> The injected
        /// stage replaces the microphone at the injection point, so nothing of
        /// ours can reach its measurement acoustically and the wait would buy
        /// it only a longer carrier. The spoken stage has the operator's real
        /// microphone live in the room, which is exactly where the landing can
        /// be heard.
        /// </para>
        /// <para>
        /// <b>It costs a keyed but silent transmitter, and that is cheap.</b>
        /// Noel, 2026-08-30: <i>"if the mic isn't actually activated, if the
        /// radio's transmitting, we're not producing RF until the exciter
        /// receives audio."</i> On SSB the silence radiates nothing, so erring
        /// long here is close to free — which is why the fallbacks err long.
        /// </para>
        /// </remarks>
        private bool WaitOutTheCountdown()
        {
            if (_sinceCountdown == null) return !StopAsked(_stopRequested);

            int until = _countdownDurationMs + DefaultCountdownSettleMs;
            while (_sinceCountdown.ElapsedMilliseconds < until)
            {
                if (StopAsked(_stopRequested)) return false;
                Thread.Sleep(25);
            }
            return !StopAsked(_stopRequested);
        }

        /// <summary>
        /// Wait for the radio to confirm the transmit state. Mox is queued
        /// like every other write — the setter enqueues a command — so "I set
        /// it" is not "it happened", the same trap the transmitter probe
        /// documents.
        /// </summary>
        private bool WaitForMox(FlexBase rig, bool wantKeyed)
        {
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < TxTuneProbeRunner.KeyUpTimeoutMs)
            {
                try { if (rig.Transmit == wantKeyed) return true; }
                catch { return false; }
                if (wantKeyed && StopRequested()) return false;
                Thread.Sleep(25);
            }
            return false;
        }

        /// <summary>
        /// Drop the carrier and confirm. Never throws — this runs in a
        /// finally, and an exception escaping here would replace whatever
        /// actually went wrong with a failure to tidy up.
        /// </summary>
        private void UnkeyMox(FlexBase rig)
        {
            if (rig == null) return;

            // Through the switch, which never refuses and never throws — the
            // same drop the kill uses, so there is one unkey and not two.
            TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Mox);

            if (!WaitForMox(rig, wantKeyed: false))
            {
                Tracing.TraceLine("FixerTransmitAudioBoundary: RADIO STILL REPORTS "
                                  + "TRANSMITTING after " + TxTuneProbeRunner.KeyUpTimeoutMs
                                  + " ms — unkey may not have taken", TraceLevel.Error);
            }
        }

        // ================================================================
        // Measuring plumbing
        // ================================================================

        /// <summary>
        /// Peak SC_MIC over a bounded window, and whether the meter actually
        /// updated during it.
        /// </summary>
        /// <remarks>
        /// Read through the meter inventory rather than the rig's cached
        /// convenience fields, because the inventory records HOW MANY readings
        /// a meter has produced — and the update count across this window is
        /// the only honest answer to "did the meter measure THIS signal, or is
        /// that number left over from an earlier one". The meter itself may
        /// register mid-window (the transmit meters appear with the transmit
        /// chain), so the lookup is retried while sampling.
        /// </remarks>
        private (double db, bool updated) MeasureScMic(FlexBase rig, int settleMs, int windowMs)
        {
            MeterReading sc = SafeScMic(rig);
            long before = SafeUpdateCount(sc);

            SleepUnlessStopped(settleMs);

            double max = double.NaN;
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < windowMs)
            {
                if (StopRequested()) break;
                sc ??= SafeScMic(rig);
                if (sc != null && SafeHasReading(sc))
                {
                    double v = SafeValue(sc);
                    if (double.IsNaN(max) || v > max) max = v;
                }
                Thread.Sleep(50);
            }

            long after = SafeUpdateCount(sc);
            bool updated = sc != null && after > before && !double.IsNaN(max);
            return (max, updated);
        }

        /// <summary>
        /// Peak SC_MIC while the reference voice plays: to the end of the
        /// recording or to <see cref="TxAudioProbe.VoiceCapMs"/>, whichever
        /// comes first.
        /// </summary>
        private (double db, bool updated) MeasureVoice(FlexBase rig)
        {
            int capMs = TxAudioProbe.VoiceCapMs;
            double contentSeconds = SafeContentSeconds(rig);
            if (contentSeconds > 0 && contentSeconds * 1000 < capMs)
                capMs = (int)(contentSeconds * 1000);

            MeterReading sc = SafeScMic(rig);
            long before = SafeUpdateCount(sc);

            double max = double.NaN;
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < capMs)
            {
                if (StopRequested()) break;
                if (SafeReachedEnd(rig)) break;
                sc ??= SafeScMic(rig);
                if (sc != null && SafeHasReading(sc))
                {
                    double v = SafeValue(sc);
                    if (double.IsNaN(max) || v > max) max = v;
                }
                Thread.Sleep(50);
            }

            long after = SafeUpdateCount(sc);
            bool updated = sc != null && after > before && !double.IsNaN(max);
            return (max, updated);
        }

        private void SleepUnlessStopped(int ms)
        {
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (StopRequested()) return;
                Thread.Sleep(25);
            }
        }

        /// <summary>Should this stage stop now?</summary>
        /// <remarks>
        /// The kill-switch half of this question lives in <see cref="StopAsked"/>
        /// so that EVERY stop check gets it, not just this one — see the note
        /// there.
        /// </remarks>
        private bool StopRequested() => StopAsked(_stopRequested);

        /// <summary>
        /// Tell a witness something happened, and never let it break the run —
        /// one of these fires next to the unkey path, where an escaping
        /// exception would replace what actually went wrong with a failure to
        /// keep a note.
        /// </summary>
        /// <remarks>
        /// Internal since Sprint 37: the tune-probe boundary
        /// (<see cref="FixerTransmitBoundary"/>) carries the same cues for
        /// stage 2 (#255), and two implementations of "a cue must never take
        /// the run down" is one more than is safe — the same reasoning that
        /// made <c>Safely</c> and <c>ReadKeyed</c> internal.
        /// </remarks>
        internal static void Witness(Action a, string which)
        {
            if (a == null) return;
            try { a(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerTransmitBoundaries: " + which
                                  + " threw and was ignored — " + ex.Message,
                                  TraceLevel.Warning);
            }
        }

        /// <summary>
        /// The one sentence for a stage stopped during its countdown, before
        /// anything keyed. One home: it is said by both audio stages and by
        /// the tune probe, and a reader comparing reports must be able to see
        /// they describe the same event.
        /// </summary>
        internal const string StoppedDuringCountdownText =
            "The check was stopped during the countdown, before the radio was "
            + "keyed. Nothing was transmitted.";

        /// <summary>
        /// The countdown-then-key pacing, shared by every keying stage: start
        /// the tones, wait unkeyed for <paramref name="keyUpAtMs"/>, and
        /// answer whether the caller may key. False means a stop arrived —
        /// the caller must then not key. A missing hook counts silently and
        /// still paces the key-up, so the timing an operator learns does not
        /// change with the sound.
        /// </summary>
        internal static bool CountUnkeyedThenReadyToKey(Action countdown,
                                                        Func<bool> stopRequested,
                                                        int keyUpAtMs = DefaultCountdownKeyUpMs)
        {
            // A caller that names nothing, or names nonsense, waits the
            // fallback. Never zero: the one direction this must not fail in is
            // early.
            if (keyUpAtMs <= 0) keyUpAtMs = DefaultCountdownKeyUpMs;

            Witness(countdown, "countdown");
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < keyUpAtMs)
            {
                if (StopAsked(stopRequested)) return false;
                Thread.Sleep(25);
            }
            return !StopAsked(stopRequested);
        }

        /// <summary>A stop hook read defensively: a hook that throws reads as
        /// "stop", because carrying on past a broken stop path is the one
        /// direction this must not fail in.</summary>
        /// <remarks>
        /// TWO SOURCES, and only one of them is an operator. The hook is the
        /// dialog's stage TIMEOUT — a token cancelled by a timer, which is why
        /// it can fire at all while these stages block the UI thread. The kill
        /// switch is the operator's own stop, raised on a thread that does not
        /// need the dispatcher (#236). Before that existed, every sampling loop
        /// in this file could only be ended by a clock.
        /// <para><b>The kill check is HERE rather than at one caller, and that
        /// placement is the whole point.</b> Sprint 37 merged the kill switch
        /// (Track Q) with the extraction of this helper (Track N); Track Q had
        /// guarded only <c>StopRequested</c>, while this helper is also what
        /// <see cref="CountUnkeyedThenReadyToKey"/> asks, twice, during the
        /// count-in. Leaving it at the one site would have meant the countdown
        /// listened ONLY to the dialog's hook — the dispatcher-queued path that
        /// cannot run while a stage holds the UI thread — so Escape during the
        /// count-in would have done nothing, silently, which is the exact defect
        /// #236 was raised about. One question, one place to ask it.</para>
        /// </remarks>
        internal static bool StopAsked(Func<bool> stopRequested)
        {
            if (TransmitKillSwitch.KillRequested) return true;
            try { return stopRequested != null && stopRequested(); }
            catch { return true; }
        }

        // ================================================================
        // Defensive accessors — a fact that cannot be read says so and never
        // takes the run down with it.
        // ================================================================

        private string PrepareVoice(FlexBase rig)
        {
            if (_prepareVoice == null)
                return "no reference recording is available to this host";
            try
            {
                string trouble = _prepareVoice(rig);
                return string.IsNullOrWhiteSpace(trouble) ? "" : trouble.Trim();
            }
            catch (Exception ex)
            {
                return "the reference recording could not be prepared: " + ex.Message;
            }
        }

        private (string device, string hostApi) DescribePcMicrophone()
        {
            if (_pcMicrophone == null) return ("", "");
            try
            {
                (string device, string hostApi) = _pcMicrophone();
                return (device ?? "", hostApi ?? "");
            }
            catch { return ("", ""); }
        }

        private static void SafeDo(Action a, string what)
        {
            try { a(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerTransmitAudioBoundary: " + what + " failed — "
                                  + ex.Message, TraceLevel.Warning);
            }
        }

        private static string SafeTonePathTrouble(FlexBase rig)
        {
            try { return rig?.TxTonePathTrouble ?? ""; }
            catch { return "the radio could not be asked whether the injection path is open"; }
        }

        private static MeterReading SafeScMic(FlexBase rig)
        {
            try { return rig?.MeterInventory?.Find("SC_MIC"); } catch { return null; }
        }

        private static long SafeUpdateCount(MeterReading r)
        {
            try { return r?.UpdateCount ?? 0L; } catch { return 0L; }
        }

        private static bool SafeHasReading(MeterReading r)
        {
            try { return r != null && r.HasReading; } catch { return false; }
        }

        private static double SafeValue(MeterReading r)
        {
            try { return r == null ? double.NaN : (double)r.Value; }
            catch { return double.NaN; }
        }

        private static string SafeMode(FlexBase rig)
        {
            try { return rig?.Mode ?? ""; } catch { return ""; }
        }

        private static string SafeMicSource(FlexBase rig)
        {
            try { return rig?.MicSource ?? ""; } catch { return ""; }
        }

        private static bool SafePcAudio(FlexBase rig)
        {
            try { return rig?.PCAudio ?? false; } catch { return false; }
        }

        private static string SafeContentName(FlexBase rig)
        {
            try { return rig?.TxFilePlayer?.ContentName ?? ""; } catch { return ""; }
        }

        private static double SafeContentSeconds(FlexBase rig)
        {
            try { return rig?.TxFilePlayer?.ContentSeconds ?? 0; } catch { return 0; }
        }

        private static bool SafeReachedEnd(FlexBase rig)
        {
            try { return rig?.TxFilePlayer?.ReachedEnd ?? true; } catch { return true; }
        }
    }
}
