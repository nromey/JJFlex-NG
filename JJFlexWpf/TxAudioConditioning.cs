using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;

namespace JJFlexWpf
{
    /// <summary>How applying a named transmit noise setup ended.</summary>
    public enum TxSetupApplyOutcome
    {
        /// <summary>The setup was found and put in place (its slot changes wait for the unkey when keyed).</summary>
        Applied,
        /// <summary>
        /// This radio has no setup of that name: nothing was applied and the
        /// chain kept its last values (ruled 2026-11-07 07:52). Said once,
        /// when <see cref="TxSetupApplyResult.FirstTime"/>.
        /// </summary>
        Missing,
        /// <summary>No name was given, or no settings root; nothing happened.</summary>
        NoName,
    }

    /// <summary>The outcome of <see cref="TxAudioConditioning.ApplyNamedSetup"/>.</summary>
    public sealed record TxSetupApplyResult(TxSetupApplyOutcome Outcome, bool FirstTime = false, TxChangeTiming Timing = TxChangeTiming.Now);

    /// <summary>
    /// App-side coordinator for the transmit conditioning chain (Track I;
    /// Sprint 49 Track H). FlexBase owns the chain itself (TxAudioConditioner:
    /// noise reduction seam, gate and monitor tap, running in the PortAudio
    /// input callback); this class owns everything the chain must not know
    /// about:
    ///
    ///  * the transmit noise reduction: <see cref="TxNoiseChain"/>, two
    ///    transmit slots (spectral, neural) from the same slot class and
    ///    catalog receive uses (Sprint 49 Track H, item A),
    ///  * the residual monitor's local playback (TxMonitorPlayer),
    ///  * the threshold policy: the gate threshold is DERIVED from the
    ///    measured noise floor (LufsMeter.Profile.NoiseFloorLufs, the same
    ///    figure the Microphone Check reports), floor + 6..10 dB, refreshed
    ///    a couple of times a second while the floor estimate improves. A
    ///    fixed-dB gate is what a podcast plugin ships because it cannot
    ///    know your room; this app knows the room.
    ///  * mode policy: the whole chain steps aside in CW/digital modes
    ///    (there is no microphone audio to clean; a gate has no business
    ///    shaping data).
    ///  * the key state the chain reads: our Mox OR the radio's interlock.
    ///
    /// <para><b>The operator's choices outlive the connection</b> (#1077,
    /// settings are intents). The noise chain and the gate's settings are held
    /// here for the session whether or not a radio is connected; a toggle made
    /// with no radio is kept and applied when one connects, never refused.</para>
    ///
    /// <para><b>Where the choices are stored</b> (item F): a named transmit
    /// noise setup per radio (<see cref="TxNoiseSetupStore"/>), pointed at by a
    /// microphone setup. <see cref="CaptureSetup"/> and <see cref="ApplySetup"/>
    /// move between this live state and a setup.</para>
    /// </summary>
    /// <summary>
    /// The compressor, the limiter and JJ Clarity as ONE thing a change edits
    /// (Sprint 49 Track J repair 1, B1): the three transmit shaping settings
    /// whose switches wait for the unkey. A change through the chain says
    /// what it does to these as an edit; <see cref="TxAudioConditioning.AskedShaping"/>
    /// runs the waiting edits on a copy, so a control can step from the
    /// newest request and not from the value before it.
    /// </summary>
    internal sealed class TxShaping
    {
        public TxCompressorSetup Compressor = new();
        public TxLimiterSetup Limiter = new();
        public TxClaritySetup Clarity = new();
    }

    public static class TxAudioConditioning
    {
        private static readonly object _gate = new();
        private static FlexBase _rig;
        private static TxNoiseChain _chain;
        private static int _nrAppliedLatency;
        private static TxMonitorPlayer _player;
        private static System.Timers.Timer _policyTimer;

        private static volatile float _thresholdMarginDb = 8f;
        private static volatile bool _autoThreshold = true;

        // The gate's settings as an intent (#1077): held with or without a
        // radio, copied onto the rig's gate whenever one is attached.
        private static TxNoiseGateSetup _gateIntent = new();

        // The stages after the expander, and the stage order, as intents too
        // (Sprint 49 Track I). Guarded by _gate with the gate's intent, and put
        // on the conditioner as ONE staged record with it.
        private static ParametricEqSetup _eqIntent = new();
        private static TxDeEsserSetup _deEsserIntent = new();
        private static string[] _orderIntent = JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder;
        private static TxWdspExpander _wdsp;

        /// <summary>
        /// WDSP's expander, the expander's other mode (Sprint 49 Track I phase
        /// 2): one for the session, made on first use and kept, like the chain.
        /// </summary>
        internal static TxWdspExpander Wdsp
        {
            get
            {
                lock (_gate)
                {
                    if (_wdsp != null) return _wdsp;
                    var made = new TxWdspExpander(TxWdspExpander.LiveEngine);
                    made.FellBack += TellExpanderFellBack;
                    return _wdsp = made;
                }
            }
        }

        /// <summary>Tests only: put a WDSP expander in place (null to drop it).</summary>
        internal static void UseWdspForTest(TxWdspExpander wdsp) { lock (_gate) _wdsp = wdsp; }

        // The compressor stage and the limiter, as intents too (Sprint 49 Track
        // CL, #1278). Guarded by _gate with the rest, and put on the
        // conditioner in the SAME one staged record. A session that has loaded
        // nothing starts as a NEW setup does: the compressor off, the limiter
        // on at full scale (TxLimiterSetup.ForNewSetup). A saved setup then
        // replaces both, and one saved before they existed has both off.
        private static TxCompressorSetup _compIntent = new();
        private static TxLimiterSetup _limiterIntent = TxLimiterSetup.ForNewSetup();

        // JJ Clarity, as an intent too (Sprint 49 Track J, #736): off until the
        // operator turns it on, held with or without a radio, guarded by _gate
        // with the rest, and put on the conditioner in the SAME one staged
        // record. Its design is the conditioner's to build (it owns the
        // passband edges): nothing here ever builds one or sets an edge on the
        // stage itself.
        private static TxClaritySetup _clarityIntent = new();

        // ------------------------------------------------------------------
        // The newest request (Sprint 49 Track J repair 1, B1). A switch asked
        // for while keyed is HELD by the chain, and its intent above changes
        // only when it runs, at the unkey. A control that reads the intent to
        // decide what to do next (a toggle, a "next" row, "is this already
        // so?") therefore read the value from BEFORE the waiting request: two
        // toggles while keyed both asked for the same thing, and an explicit
        // off after a waiting on was dropped as "already off". So every road
        // that changes the compressor, the limiter or JJ Clarity through the
        // chain's ordering also says what it will do to the three, as an
        // EDIT, kept here while the change waits. AskedShaping() is the three
        // with every waiting edit run on them, in the chain's order: what
        // they will read once everything now waiting has run.
        //
        // An edit stops waiting when its change RUNS, or when its owner
        // WITHDRAWS it (the editor's Cancel, the noise layer's Escape): a
        // record is queued right behind the change, tagged with the same
        // owner, and the chain runs an owner's records when it withdraws that
        // owner's changes. So a withdrawn request is never counted, and
        // nothing here needs to know who owns what.
        // ------------------------------------------------------------------

        private sealed class WaitingEdit
        {
            public readonly TxNoiseChain Chain;
            public readonly Action<TxShaping> Edit;
            public WaitingEdit(TxNoiseChain chain, Action<TxShaping> edit) { Chain = chain; Edit = edit; }
        }

        /// <summary>The edits of changes now held by a chain, in the order they were asked for. Guarded by _gate.</summary>
        private static readonly List<WaitingEdit> _waitingEdits = new();

        /// <summary>
        /// The compressor, the limiter and JJ Clarity as LAST ASKED FOR: the
        /// intents with every change still waiting for the unkey run on them,
        /// in the chain's order. With nothing waiting, the intents. Copies;
        /// reading changes nothing. For a control that steps from where things
        /// are going (a toggle, a "next" row, one step of a lever), and for
        /// "is this already what was asked?".
        /// </summary>
        internal static TxShaping AskedShaping()
        {
            var chain = _chain;
            TxShaping asked;
            WaitingEdit[] waiting;
            lock (_gate)
            {
                asked = new TxShaping { Compressor = _compIntent.Clone(), Limiter = _limiterIntent.Clone(), Clarity = _clarityIntent.Clone() };
                // Only what THIS session's chain holds: a change held by any
                // other chain says nothing about these intents.
                waiting = _waitingEdits.Where(w => ReferenceEquals(w.Chain, chain)).ToArray();
            }
            // Outside the lock: an edit may read the controller's own notes.
            foreach (var w in waiting)
            {
                try { w.Edit(asked); }
                catch (Exception ex) { Trace.WriteLine("TxAudioConditioning: a waiting change could not be read ahead: " + ex.GetType().Name + ": " + ex.Message); }
            }
            return asked;
        }

        /// <summary>How many changes this session's chain holds whose edits <see cref="AskedShaping"/> is counting. For tests and traces.</summary>
        internal static int WaitingShapingEdits
        {
            get { var chain = _chain; lock (_gate) return _waitingEdits.Count(w => ReferenceEquals(w.Chain, chain)); }
        }

        /// <summary>
        /// ONE change through the chain's ordering, with what it will do to
        /// the compressor, the limiter and JJ Clarity (null: nothing). A
        /// switch is held while keyed (<see cref="TxNoiseChain.RunOrDefer"/>);
        /// a lever is live unless something is held ahead of it
        /// (<see cref="TxNoiseChain.Continuous"/>). While the change waits,
        /// <see cref="AskedShaping"/> counts its edit. The edit must give the
        /// same answer however often it is asked.
        /// </summary>
        internal static TxChangeResult Ask(TxNoiseChain chain, bool lever, Action change, Action<TxShaping>? edit)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (edit == null) return lever ? chain.Continuous(change) : chain.RunOrDefer(change);
            var result = lever ? chain.Continuous(change) : chain.RunOrDefer(change);
            if (result.Timing != TxChangeTiming.AtUnkey) return result;
            // Held: its edit waits with it. The record runs straight after the
            // change when it runs, and at the withdrawal when its owner
            // withdraws it (this thread, so the same owner's tag as the change
            // just held). Had the change already run by now (an unkey between
            // the two lines), the record runs at once.
            var waiting = new WaitingEdit(chain, edit);
            lock (_gate) _waitingEdits.Add(waiting);
            chain.AfterHeld(() => { lock (_gate) _waitingEdits.Remove(waiting); });
            return result;
        }

        /// <summary>Make an edit to the three intents; a copy of the three as they then read.</summary>
        private static TxShaping CommitShaping(Action<TxShaping> edit)
        {
            lock (_gate)
            {
                var now = new TxShaping { Compressor = _compIntent, Limiter = _limiterIntent, Clarity = _clarityIntent };
                edit(now);
                _compIntent = now.Compressor ?? new TxCompressorSetup();
                _limiterIntent = now.Limiter ?? new TxLimiterSetup();
                _clarityIntent = now.Clarity ?? new TxClaritySetup();
                return new TxShaping { Compressor = _compIntent.Clone(), Limiter = _limiterIntent.Clone(), Clarity = _clarityIntent.Clone() };
            }
        }

        /// <summary>A switch of the three (S2): the edit is made when the change runs, then <paramref name="push"/> puts it on the conditioner; the same edit is what waits.</summary>
        private static TxChangeResult ShapingSwitch(Action<TxShaping> edit, Action<TxShaping> push) =>
            Ask(Chain, lever: false, () => push(CommitShaping(edit)), edit);

        /// <summary>A continuous lever of the three (S15): live unless something is held ahead of it, when it waits behind it and is counted like a switch.</summary>
        private static TxChangeResult ShapingLever(Action<TxShaping> edit, Action<TxShaping> push) =>
            Ask(Chain, lever: true, () => push(CommitShaping(edit)), edit);

        /// <summary>What a whole setup's compressor and limiter do to the three: replace both. Null is off.</summary>
        internal static Action<TxShaping> DynamicsEdit(TxCompressorSetup? compressor, TxLimiterSetup? limiter)
        {
            var c = (compressor ?? new TxCompressorSetup()).Clone();
            var l = (limiter ?? new TxLimiterSetup()).Clone();
            return s => { s.Compressor = c.Clone(); s.Limiter = l.Clone(); };
        }

        /// <summary>What a whole JJ Clarity setting does to the three: replace it. Null is off.</summary>
        internal static Action<TxShaping> ClarityEdit(TxClaritySetup? clarity)
        {
            var c = (clarity ?? new TxClaritySetup()).Clone();
            return s => s.Clarity = c.Clone();
        }

        /// <summary>What a whole setup does to the three: its compressor, limiter and JJ Clarity replace them (a setup saved before one of them existed puts it off).</summary>
        internal static Action<TxShaping> WholeSetupEdit(TxNoiseSetup setup)
        {
            var dynamics = DynamicsEdit(setup?.Compressor, setup?.Limiter);
            var clarity = ClarityEdit(setup?.Clarity);
            return s => { dynamics(s); clarity(s); };
        }

        /// <summary>The chain on the current rig, or null when detached.</summary>
        public static JJPortaudio.TxAudioConditioner Conditioner => _rig?.TxConditioner;

        /// <summary>
        /// The transmit noise reduction for this session (item B), made on first
        /// use and kept across connections, so a choice made with no radio is
        /// held (#1077). Its slots build their engines on worker threads.
        /// </summary>
        public static TxNoiseChain Chain
        {
            get
            {
                var chain = _chain;
                if (chain != null) return chain;
                lock (_gate)
                {
                    if (_chain != null) return _chain;
                    var made = CreateChain();
                    HookNotices(made);
                    return _chain = made;
                }
            }
        }

        /// <summary>How the mid-over fallback notice is given (repair round 1, R1).</summary>
        public enum TxNoticeRoute { ToneAndWords, WordsOnlyNoAlertChannel, WordsOnlyCaptureHearsOutput }

        /// <summary>
        /// The tone only on the alert channel, and only when the transmit
        /// microphone is not a wire that could carry it back (a loopback or a
        /// virtual cable, or a Windows default that is one). Words otherwise.
        /// This closes the routes the app can see. A loudspeaker the microphone
        /// hears in the room is physics, and screen reader speech has the same
        /// exposure; nothing here claims to solve it.
        /// </summary>
        internal static TxNoticeRoute ChooseNoticeRoute(bool alertChannelLive, string captureDevice, string windowsDefaultInput)
        {
            if (!alertChannelLive) return TxNoticeRoute.WordsOnlyNoAlertChannel;
            if (JJPortaudio.Devices.CapturesComputerOutput(captureDevice ?? "", windowsDefaultInput)) return TxNoticeRoute.WordsOnlyCaptureHearsOutput;
            return TxNoticeRoute.ToneAndWords;
        }

        /// <summary>
        /// Wire a chain's two reports to the operator and the trace: a slot that
        /// fell back mid-over (item C) and each over's frame counts (item D).
        /// </summary>
        internal static void HookNotices(TxNoiseChain chain)
        {
            // One notice for one failure (Sprint 49 Track H2 bench round, item 6).
            chain.FellBackTogether += engines => TellFellBack(chain, engines);
            chain.OverReport += line => Trace.WriteLine(line);
            // S17: the live choices persist across a restart; the app saves on this.
            chain.IntentChanged += () => IntentChanged?.Invoke();
        }

        /// <summary>
        /// Raised after any transmit noise choice has changed: either slot, an
        /// engine or its settings, or the gate (S17, ruled 2026-11-18 21:35: they
        /// persist across a restart the way receive's do). On the thread that
        /// made the change, or a pool thread for one held to the unkey.
        /// </summary>
        public static event Action? IntentChanged;

        /// <summary>
        /// The mid-over failure notice (item C; Noel 2026-11-18 08:18 and 08:21:
        /// "Wouldn't transmit a beep on air", then "the one line or few words
        /// with a tone (short beep)"). A short earcon on the operator's ALERT
        /// channel, the existing calm "something failed" tone, and a few words.
        /// What this guarantees, and no more (repair round 1, R1): the tone is
        /// never written into the transmit buffers (the earcon player owns its
        /// own mixer and device; transmit frames are built only by
        /// <c>TxFramePipeline</c>); it plays ONLY on the alert channel, never the
        /// system-beep fallback, so with no alert output it is words only; and
        /// it is not played when the transmit microphone is a loopback or a
        /// virtual cable that could carry it back by wire
        /// (<see cref="ChooseNoticeRoute"/>). A loudspeaker the microphone hears
        /// across the room is acoustic, as screen reader speech is, and is not
        /// solved here. The route taken is traced every time.
        /// </summary>
        private static void TellFellBack(TxNoiseChain chain, IReadOnlyList<(DenoiseFamily Family, string EngineId)> engines)
        {
            string Name(string id) => chain.Catalog.Find(id) is { } row ? Lexicon.Get(row.LabelKey) : id;
            GiveFallbackNotice(() => engines.Count >= 2
                ? Lexicon.Get("audio.denoise.tx.fell_back_both", ("first", Name(engines[0].EngineId)), ("second", Name(engines[1].EngineId)))
                : Lexicon.Get("audio.denoise.tx.fell_back", ("engine", Name(engines[0].EngineId))));
        }

        /// <summary>
        /// WDSP's expander missed a frame mid-over (phase 2): the same notice,
        /// tone and words by the same route, as a noise slot's (#737's ruled
        /// fallback: the unprocessed microphone at the same delay until the unkey).
        /// </summary>
        private static void TellExpanderFellBack()
        {
            Trace.WriteLine("TxAudioConditioning: WDSP's expander missed a frame mid-over; latched to the unprocessed microphone at the same delay until the unkey; telling the operator.");
            GiveFallbackNotice(() => Lexicon.Get(TxWdspExpander.FellBackKey));
        }

        /// <summary>The fallback notice's one body: the route chosen and traced, the tone where it is safe, the words.</summary>
        private static void GiveFallbackNotice(Func<string> words)
        {
            try
            {
                var route = ChooseNoticeRoute(EarconPlayer.AlertChannelLive, _rig?.TransmitCaptureDeviceName ?? "", null);
                bool toned = route == TxNoticeRoute.ToneAndWords && EarconPlayer.ProblemRecordedToneOnAlertChannelOnly();
                Trace.WriteLine("TxAudioConditioning: transmit fallback notice: "
                    + (toned ? "tone on the alert channel and words"
                       : route == TxNoticeRoute.WordsOnlyNoAlertChannel ? "words only: the alert channel has no output, and the system beep is never used for this"
                       : route == TxNoticeRoute.WordsOnlyCaptureHearsOutput ? "words only: the transmit microphone is a loopback or virtual cable that could carry the tone"
                       : "words only: the tone did not play (warning sounds off, or the alert channel went away)"));
                ScreenReaderOutput.Speak(words(),
                    Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                    subject: Radios.Speech.SpeechSubject.TransmitNoiseFallback);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: the fallback notice could not be given: " + ex.Message);
            }
        }

        /// <summary>A new transmit chain on the app's catalog, its neural slot on RNNoise at the transmit default floor (S8), both slots off.</summary>
        internal static TxNoiseChain CreateChain(DenoiseEngineCatalog catalog = null) => new TxNoiseChain(catalog);

        /// <summary>Tests only: put a chain in place (null to drop it) without attaching a rig.</summary>
        internal static void UseChainForTest(TxNoiseChain chain) { lock (_gate) { _chain = chain; _waitingEdits.Clear(); } }

        /// <summary>
        /// Wire the conditioning chain of the given rig: plug the noise
        /// reduction, the key state and the monitor playback into it, put the
        /// held gate settings on its gate, and start the policy tick. Safe to
        /// call again on reconnect.
        /// </summary>
        public static void Attach(FlexBase rig)
        {
            Detach();
            if (rig == null) return;
            _rig = rig;

            var chain = Chain;
            _player = new TxMonitorPlayer();

            var cond = rig.TxConditioner;
            // Keyed means our Mox OR the radio's interlock (item B): VOX keys
            // only the interlock, so Mox alone would apply changes mid-over
            // for a VOX operator. FlexBase hands the same question to the
            // conditioner itself when the transmit stream opens; the chain
            // asks it on the control side to say whether a change waits.
            chain.KeySource = rig.TransmitKeySource;
            PlugNoiseStage(cond, rig.TransmitKeySource, NrStage, AppliedNrLatency, EngagedOf(chain));
            PlugExpanderMode(cond, Wdsp);
            cond.MonitorSink = MonitorStage;
            PushGate(cond);
            SyncWdsp();
            // The take header's "pc-chain" line asks here what the computer's chain was doing (Track CL, CL8).
            PcTransmitChain.Describe = DescribePcChain;
            // The handoff with the radio's speech processor (#1278): its notices are said here, never silent.
            rig.ProcessorHandoffNoticed += OnProcessorHandoffNotice;

            // The TRANSMIT slice's mode (Sprint 49 Track H2, slice-mode round):
            // ModeChanged follows only the active slice, so a second slice in CW
            // bypassed cleanup on a USB transmit slice, and a CW transmit slice
            // with a USB active slice ran it.
            rig.ModeChanged += OnModeChanged;
            rig.TransmitModeMayHaveChanged += OnTransmitModeMayHaveChanged;
            ApplyModePolicy(TransmitModeOf(rig));

            // Twice a second: refresh the derived threshold from the live
            // noise-floor estimate. Cheap — the profile is cached against
            // the meter's block count and only recomputes while transmitting,
            // which is exactly when the floor estimate is improving.
            _policyTimer = new System.Timers.Timer(500) { AutoReset = true };
            _policyTimer.Elapsed += (s, e) =>
            {
                RefreshDerivedThreshold();
                // Sprint 49 Track I: the de-esser's band follows the transmit
                // filter; a finished "Learn the room" is acted on and said; and
                // the stages' per-over counts are written at the unkey, here,
                // never on the audio thread.
                PolicyTickStages();
                // Sprint 49 Track CL: the handoff between our compressor and the
                // radio's speech processor is driven from here, twice a second.
                ProcessorHandoffTick();
                // An over the microphone capture never saw end (a file over:
                // the capture is stopped while a recording sends) ends by the key
                // state (Sprint 49 Track H2 bench round, item 5).
                _chain?.EndOverByKeyState();
                // R3's backstop: held changes run once unkeyed, even through an
                // over in CW, where the noise chain is stood aside and never
                // sees the unkey.
                _chain?.ApplyDeferred();
            };
            _policyTimer.Start();

            Trace.WriteLine("TxAudioConditioning: attached");
        }

        /// <summary>Unhook from the rig and release the monitor device. The noise chain and the gate settings stay, as the operator's intent.</summary>
        public static void Detach()
        {
            var rig = _rig;
            _rig = null;
            if (rig != null)
            {
                // The leave before a teardown is NOT here (repair 1, B2): an
                // ordinary Disconnect or Exit never detaches this chain while
                // the link is up, and Attach calls this for a rig that may
                // still be live. See LeaveRadioBeforeTeardown.
                rig.ProcessorHandoffNoticed -= OnProcessorHandoffNotice;
                rig.ModeChanged -= OnModeChanged;
                rig.TransmitModeMayHaveChanged -= OnTransmitModeMayHaveChanged;
                var cond = rig.TxConditioner;
                cond.NoiseReducer = null;
                cond.NoiseReducerAppliedLatencySamples = null;
                cond.NoiseReducerEngaged = null;
                cond.ExpanderReplacement = null;
                cond.ExpanderReplacementAppliedLatencySamples = null;
                cond.MonitorSink = null;
                cond.MonitorMode = JJPortaudio.TxAudioConditioner.MonitorModes.Off;
            }
            var chain = _chain;
            if (chain != null) chain.KeySource = null;
            _policyTimer?.Stop();
            _policyTimer?.Dispose();
            _policyTimer = null;
            _player?.Dispose();
            _player = null;
        }

        // ------------------------------------------------------------------
        // Stages (audio callback thread)
        // ------------------------------------------------------------------

        private static void NrStage(float[] buffer, int count, uint sampleRate)
        {
            _nrAppliedLatency = 0;
            _nrAppliedLatency = NoiseStage(_chain, _rig?.TxConditioner.Keyed ?? false, buffer, count, sampleRate);
        }

        /// <summary>
        /// The noise stage itself: the chain cleans the buffer in place under this
        /// buffer's key state, and the delay it went out with is returned. ONE body
        /// for the live wiring (<see cref="NrStage"/>) and the audition's positive
        /// control (Sprint 49 Track Q phase B repair 1, Codex's drift caveat), so a
        /// change here reaches both. Every engine is 48 kHz-native (480-sample
        /// frames are 10 ms only there); the chain passes any other rate through
        /// honestly, and honestly means the residual goes silent, which is the
        /// designed tell that the stage is not live.
        /// </summary>
        internal static int NoiseStage(TxNoiseChain chain, bool keyed, float[] buffer, int count, uint sampleRate)
        {
            if (chain == null) return 0;
            chain.Process(buffer, count, sampleRate, keyed);
            return chain.AppliedLatencySamples;
        }

        /// <summary>
        /// Plug a noise stage into a conditioner: the key source it reads each
        /// buffer, the stage it runs before the gate, and where it reads that
        /// stage's delay. ONE wiring for <see cref="Attach"/> and the audition's
        /// positive control (repair 1), so a change to how Attach plugs the stage
        /// reaches the control too.
        /// </summary>
        internal static void PlugNoiseStage(JJPortaudio.TxAudioConditioner cond, Func<bool> keySource,
            Action<float[], int, uint> stage, Func<int> appliedLatency, Func<bool>? engaged = null)
        {
            cond.KeySource = keySource;
            cond.NoiseReducer = stage;
            cond.NoiseReducerAppliedLatencySamples = appliedLatency;
            cond.NoiseReducerEngaged = engaged;
        }

        /// <summary>Whether a chain's noise reduction is switched on in either slot (repair 1, N3): what the conditioner asks to say whether its noise stage does anything.</summary>
        internal static Func<bool> EngagedOf(TxNoiseChain chain) =>
            () => chain.WantsEnabled(DenoiseFamily.Spectral) || chain.WantsEnabled(DenoiseFamily.Neural);

        private static int AppliedNrLatency() => _nrAppliedLatency;

        /// <summary>
        /// Plug WDSP's expander into a conditioner as the expander's other mode
        /// (phase 2). ONE wiring for <see cref="Attach"/> and the tests, as
        /// <see cref="PlugNoiseStage"/> is for the noise stage.
        /// </summary>
        internal static void PlugExpanderMode(JJPortaudio.TxAudioConditioner cond, TxWdspExpander wdsp)
        {
            cond.ExpanderReplacement = wdsp.Process;
            cond.ExpanderReplacementAppliedLatencySamples = wdsp.AppliedLatencySamples;
        }

        private static void MonitorStage(float[] buffer, int count, uint sampleRate)
        {
            _player?.Push(buffer, count, (int)sampleRate);
        }

        // ------------------------------------------------------------------
        // Policy
        // ------------------------------------------------------------------

        /// <summary>
        /// Derive the gate threshold from the measured noise floor:
        /// floor + margin, from the same LufsMeter profile the Microphone
        /// Check reports. No-op until the meter has enough transmitted audio
        /// (~3 seconds of speech) to know the floor; before that the gate
        /// keeps its deliberately inert default.
        /// </summary>
        private static void RefreshDerivedThreshold()
        {
            var rig = _rig;
            // The policy IN FORCE on the chain, not the intent: a whole setup
            // that is still waiting for the unkey has not changed it (repair
            // round 3).
            var cond = rig?.TxConditioner;
            if (cond == null) return;
            try
            {
                // S15 detail, ruled 2026-11-18 21:27: the derived threshold holds
                // still while keyed (our Mox or the radio's interlock), because
                // re-measuring during speech can count the voice as noise. It
                // resumes on the first unkeyed tick. The continuous levers stay
                // live while keyed (S15 itself; ContinuousLeversLiveWhileKeyed).
                //
                // Sprint 49 Track I (#1252, ruled S3 2026-11-23): the floor is the
                // conditioner's own room meter at the expander's INPUT, after
                // noise reduction, learned while unkeyed; never the meter after
                // the chain (rig.TxLoudnessProfile), which measured the gate's own
                // effect and flipped the threshold from over to over. The rule is
                // radio-neutral and lives beside the conditioner.
                bool keyed = rig.TransmitKeyedOrInterlock;
                cond.RefreshDerivedThreshold(keyed);
                TraceThresholdHold(cond.GateInForce.Enabled && cond.AutoThresholdInForce && keyed,
                    cond.Gate.ThresholdDb, cond.ThresholdMarginInForceDb);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: threshold refresh failed: " + ex.Message);
            }
        }

        /// <summary>1 while the automatic gate threshold is held for an over, else 0. The policy tick's alone; see <see cref="TraceThresholdHold"/>.</summary>
        private static int _thresholdHeld;

        /// <summary>The threshold in force when the current hold started, dB.</summary>
        private static float _heldThresholdDb;

        /// <summary>
        /// S15, made observable (Sprint 49 Track L2; the integration-day
        /// bench's finding 4, "no trace or readout of the derived gate threshold
        /// while automatic"). One trace line when the automatic threshold starts
        /// holding still for an over (the gate on, its threshold automatic, the
        /// radio keyed), and one when it lets go, each with the threshold in
        /// force, so a bench can check from the trace that it did not move while
        /// keyed and resumed after. Trace only: nothing is said or shown. An
        /// over with the gate off, or with a threshold set by hand, writes
        /// nothing, because nothing is held.
        /// </summary>
        /// <param name="heldNow">Whether the hold is in force on this tick.</param>
        /// <param name="thresholdDb">The gate's threshold in force after this tick's refresh.</param>
        /// <param name="marginDb">The margin over the measured floor in force.</param>
        private static void TraceThresholdHold(bool heldNow, float thresholdDb, float marginDb)
        {
            int now = heldNow ? 1 : 0;
            if (System.Threading.Interlocked.Exchange(ref _thresholdHeld, now) == now) return;
            if (heldNow)
            {
                _heldThresholdDb = thresholdDb;
                Trace.WriteLine(FormattableString.Invariant(
                    $"TxAudioConditioning: automatic gate threshold held for the over at {thresholdDb:F1} dB (margin {marginDb:F1} dB over the measured floor; S15)"));
            }
            else
            {
                Trace.WriteLine(FormattableString.Invariant(
                    $"TxAudioConditioning: automatic gate threshold released (unkeyed, or the gate's threshold policy changed): held at {_heldThresholdDb:F1} dB, now {thresholdDb:F1} dB (S15)"));
            }
        }

        /// <summary>
        /// The derived threshold the policy tick would set, or null when it must
        /// leave the gate alone: the threshold is by hand, the radio is keyed
        /// (S15 detail: it holds still while keyed), or the meter cannot yet say
        /// where the floor is.
        /// </summary>
        internal static float? DerivedThreshold(bool autoInForce, bool keyed, bool profileValid, float noiseFloorLufs, float marginDb)
        {
            // THE rule now lives beside the conditioner (Sprint 49 Track I,
            // #1252), radio-neutral; this keeps its old guard for a floor the
            // loudness meter reports as "nothing".
            if (noiseFloorLufs <= JJPortaudio.LufsMeter.Floor) return null;
            return JJPortaudio.TxRoomFloor.ThresholdRule(autoInForce, keyed, profileValid, noiseFloorLufs, marginDb);
        }

        // The active slice's mode changing is one of the ways the transmit
        // slice's can (they are often the same slice): the policy reads the
        // transmit slice either way, never the mode this event carries.
        private static void OnModeChanged(string newMode) => OnTransmitModeMayHaveChanged();

        private static void OnTransmitModeMayHaveChanged()
        {
            if (_rig is { } rig) ApplyModePolicy(TransmitModeOf(rig));
        }

        /// <summary>
        /// The transmit slice's mode: the slice the radio has DESIGNATED, read
        /// live; when none of ours is designated, the active slice's receive mode,
        /// read live (slice-mode round 2, Codex: the positional read kept a
        /// designation's old CW after the radio removed it, and the fallback read
        /// the transmit mode while transmitting).
        /// </summary>
        internal static string TransmitModeOf(FlexBase rig)
        {
            string designated = rig.DesignatedTransmitMode;
            return designated.Length > 0 ? designated : rig.ActiveSliceModeAsReported;
        }

        private static void ApplyModePolicy(string mode)
        {
            var rig = _rig;
            if (rig == null) return;
            rig.TxConditioner.BypassAll = BypassedIn(mode);
        }

        /// <summary>Whether the transmit chain stands aside in a transmit mode: CW and the digital modes (the one rule the summary reads too).</summary>
        public static bool BypassedIn(string mode) => IsNonVoiceMode(mode);

        /// <summary>
        /// The transmit chain's mode policy applies before its noise reducer.
        /// </summary>
        private static bool IsNonVoiceMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return false;
            // DFM and FDV are the radio's own digital modes, as FlexLib's Slice
            // filter branches spell them (slice-mode round 2); FM and NFM are voice.
            return mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("DIG", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("FDM", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("FDV", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("DFM", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("RTTY", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Operator-facing surface (UI binds to these)
        // ------------------------------------------------------------------

        /// <summary>
        /// The neural slot's on or off, the switch the Audio Workshop has always
        /// called transmit noise reduction. The operator's intent: held with no
        /// radio (#1077), adopted at the next unkey when keyed (item B). Use
        /// <see cref="TxNoiseChain.SetEnabled"/> to learn when it takes effect.
        /// </summary>
        public static bool NrEnabled
        {
            get { return Chain.WantsEnabled(DenoiseFamily.Neural); }
            set { Chain.SetEnabled(DenoiseFamily.Neural, value); }
        }

        /// <summary>
        /// The neural slot's chosen engine's strength, 0..1 wet/dry, stored as
        /// it always was; the operator meets it as a noise floor (#1019,
        /// <see cref="NoiseFloor"/>). LIVE while keyed and while monitoring —
        /// hear your own voice in the residual, raise the floor, hear it gone.
        /// That loop is the whole point of the residual monitor.
        /// </summary>
        public static float NrStrength
        {
            get { var c = Chain; return c.GetStrength(c.ChosenEngineId(DenoiseFamily.Neural)); }
            set { var c = Chain; c.SetStrength(c.ChosenEngineId(DenoiseFamily.Neural), Math.Clamp(value, 0f, 1f)); }
        }

        /// <summary>Transmit RNNoise's stored default (floor 20, ruled S3), from the one source.</summary>
        internal static float DefaultNrStrength => NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Transmit);

        /// <summary>Transmit noise gate on/off. Held with no radio (#1077).</summary>
        public static bool GateEnabled
        {
            get { lock (_gate) return _gateIntent.Enabled; }
            set { SetGateEnabled(value); }
        }

        /// <summary>
        /// Turn the gate on or off (repair round 1, R3): like an engine change,
        /// it waits for the unkey when keyed, and the returned timing says so;
        /// held with no radio (#1077). Unkeyed, the conditioner still adopts it
        /// only on an unkeyed buffer.
        /// </summary>
        public static TxChangeResult SetGateEnabled(bool on) => Chain.RunOrDefer(() => SetGateEnabledNow(on));

        private static void SetGateEnabledNow(bool on)
        {
            lock (_gate) _gateIntent.Enabled = on;
            Conditioner?.RequestGateEnabled(on);
        }

        /// <summary>A continuous gate lever: live or held for the unkey by the one policy, <see cref="TxNoiseChain.ContinuousLeversLiveWhileKeyed"/>, with its timing.</summary>
        private static TxChangeResult GateLever(Action<TxNoiseGateSetup> intent, Action<JJPortaudio.TxNoiseGate> live,
            Action<JJPortaudio.TxAudioConditioner.StagedGate> staged, bool sharedWithWdsp = true) =>
            WdspHolds(sharedWithWdsp, Chain.Continuous(() =>
            {
                lock (_gate) intent(_gateIntent);
                var c = Conditioner;
                if (c == null) return;
                // The one owner first (repair round 4): in force and in anything
                // waiting, so the later lone change wins even if the audio side
                // is adopting a whole setup at this moment; then on the gate at
                // once, which the audio side's next copy agrees with.
                c.ChangeGate(staged);
                live(c.Gate);
            }));

        /// <summary>
        /// Phase 2 repair 1 (B3): the timing of a lever WDSP's expander shares
        /// (the threshold and how it is worked out, attack, hold, release, the
        /// side-chain corners). The JJ expander takes them live while keyed (S15),
        /// but WDSP's takes its settings only at the unkey, because every one of
        /// its setters restarts its detector and would duck a word; so while
        /// WDSP's mode is chosen or still in force and the key is down, a change
        /// that would have been "now" is truthfully "at the unkey". Nothing is
        /// sent to the helper early to make "now" true.
        /// </summary>
        internal static TxChangeResult WdspHolds(bool shared, TxChangeResult result)
        {
            if (!shared || result.Timing != TxChangeTiming.Now) return result;
            bool wdsp;
            lock (_gate) wdsp = ExpanderModes.IsWdsp(_gateIntent.ExpanderMode);
            wdsp |= _wdsp?.InForce == true;
            return wdsp && Chain.IsKeyed ? TxChangeResult.AtUnkey : result;
        }

        /// <summary>
        /// Margin above the measured noise floor for the derived threshold,
        /// dB (6..10 recommended; clamped 3..15).
        /// </summary>
        public static float ThresholdMarginDb
        {
            get { return _thresholdMarginDb; }
            set { SetThresholdMargin(value); }
        }

        /// <summary>The margin, with its timing. The same shape as Track H2's (5bf60c11), on the one gate owner (repair round 4).</summary>
        public static TxChangeResult SetThresholdMargin(float db)
        {
            float v = Math.Clamp(db, 3f, 15f);
            return WdspHolds(true, Chain.Continuous(() =>
            {
                _thresholdMarginDb = v;
                Conditioner?.ChangeGate(g => g.ThresholdMarginDb = v);
            }));
        }

        /// <summary>Derive the threshold from the measured floor (default) or
        /// leave it wherever the operator set it by hand.</summary>
        public static bool AutoThreshold
        {
            get { return _autoThreshold; }
            set { SetAutoThreshold(value); }
        }

        /// <summary>
        /// The automatic threshold on or off, IN ORDER behind a held setup (Track
        /// H2's change in 5bf60c11, with the orchestrator's authority, taken here
        /// so the two branches agree): through <see cref="TxNoiseChain.Continuous"/>
        /// like every other gate lever. Whether it is live while keyed is still
        /// S15's question; with nothing held it is live, as before. On the one gate
        /// owner (repair round 4).
        /// </summary>
        public static TxChangeResult SetAutoThreshold(bool on) => WdspHolds(true, Chain.Continuous(() =>
        {
            _autoThreshold = on;
            Conditioner?.ChangeGate(g => { g.AutoThreshold = on; g.HasThreshold = !on; });
        }));

        /// <summary>One gate level by key (Track H2's controller levers), with its timing: threshold, attack, hold, release, depth or margin.</summary>
        public static TxChangeResult SetGateLevel(string key, float value) => key switch
        {
            "threshold" => GateLever(g => g.ThresholdDb = value, g => g.ThresholdDb = value, g => g.ThresholdDb = value),
            "attack" => GateLever(g => g.AttackMs = value, g => g.AttackMs = value, g => g.AttackMs = value),
            "hold" => GateLever(g => g.HoldMs = value, g => g.HoldMs = value, g => g.HoldMs = value),
            "release" => GateLever(g => g.ReleaseMs = value, g => g.ReleaseMs = value, g => g.ReleaseMs = value),
            // The JJ gate setting's depth: WDSP's expander does not use it.
            "depth" => GateLever(g => g.RangeDb = value, g => g.RangeDb = value, g => g.RangeDb = value, sharedWithWdsp: false),
            // Sprint 49 Track I: the side-chain's corners are continuous (live);
            // the look-ahead's length is a switch (it moves the stream in time).
            "sidechain_low" => GateLever(g => g.SideChainLowCutHz = value, g => g.SideChainLowHz = value, g => g.SideChainLowHz = value),
            "sidechain_high" => GateLever(g => g.SideChainHighCutHz = value, g => g.SideChainHighHz = value, g => g.SideChainHighHz = value),
            "lookahead" => SetLookAhead(value),
            "margin" => SetThresholdMargin(value),
            _ => new TxChangeResult(TxChangeTiming.Refused, "unknown-gate-lever"),
        };

        /// <summary>The gate's threshold by hand, dB (used when <see cref="AutoThreshold"/> is off). Held with no radio.</summary>
        public static float GateThresholdDb
        {
            get { var c = Conditioner; if (c != null) return c.Gate.ThresholdDb; lock (_gate) return _gateIntent.ThresholdDb; }
            set { GateLever(g => g.ThresholdDb = value, g => g.ThresholdDb = value, g => g.ThresholdDb = value); }
        }

        /// <summary>Gate attack, ms. Held with no radio.</summary>
        public static float GateAttackMs
        {
            get { lock (_gate) return _gateIntent.AttackMs; }
            set { GateLever(g => g.AttackMs = value, g => g.AttackMs = value, g => g.AttackMs = value); }
        }

        /// <summary>Gate hold, ms. Held with no radio.</summary>
        public static float GateHoldMs
        {
            get { lock (_gate) return _gateIntent.HoldMs; }
            set { GateLever(g => g.HoldMs = value, g => g.HoldMs = value, g => g.HoldMs = value); }
        }

        /// <summary>Gate release, ms. Held with no radio.</summary>
        public static float GateReleaseMs
        {
            get { lock (_gate) return _gateIntent.ReleaseMs; }
            set { GateLever(g => g.ReleaseMs = value, g => g.ReleaseMs = value, g => g.ReleaseMs = value); }
        }

        /// <summary>Gate depth, dB. Held with no radio.</summary>
        public static float GateRangeDb
        {
            get { lock (_gate) return _gateIntent.RangeDb; }
            set { GateLever(g => g.RangeDb = value, g => g.RangeDb = value, g => g.RangeDb = value, sharedWithWdsp: false); }
        }

        /// <summary>Copy the held gate settings onto a rig's gate (attach, and after applying a setup).</summary>
        /// <summary>Put the held gate settings in force on a rig's conditioner (attach: there is no over yet to protect).</summary>
        private static void PushGate(JJPortaudio.TxAudioConditioner cond)
        {
            TxNoiseGateSetup g; ParametricEqSetup eq; TxDeEsserSetup de; TxCompressorSetup comp; TxLimiterSetup lim; TxClaritySetup clarity;
            lock (_gate) { g = _gateIntent.Clone(); eq = _eqIntent.Clone(); de = _deEsserIntent.Clone(); comp = _compIntent.Clone(); lim = _limiterIntent.Clone(); clarity = _clarityIntent.Clone(); }
            cond.SetGateInForce(ToConditioner(g, eq, de, _autoThreshold, _thresholdMarginDb, comp, lim, clarity));
        }

        /// <summary>A setup's gate as the conditioner's one record, with the equaliser and the de-esser off.</summary>
        internal static JJPortaudio.TxAudioConditioner.StagedGate ToConditioner(TxNoiseGateSetup g, bool auto, float margin) =>
            ToConditioner(g, null, null, auto, margin);

        /// <summary>A whole setup's in-process stages as the conditioner's one record (Sprint 49 Track I): the expander, the equaliser and the de-esser. ONE function for the live wiring and the audition.</summary>
        internal static JJPortaudio.TxAudioConditioner.StagedGate ToConditioner(TxNoiseSetup s) =>
            ToConditioner(s.Gate ?? new TxNoiseGateSetup(), s.Eq, s.DeEsser, (s.Gate ?? new TxNoiseGateSetup()).AutoThreshold,
                (s.Gate ?? new TxNoiseGateSetup()).ThresholdMarginDb, s.Compressor, s.Limiter, s.Clarity);

        /// <summary>The expander, the equaliser and the de-esser as the conditioner's one record. A null equaliser or de-esser is off.</summary>
        internal static JJPortaudio.TxAudioConditioner.StagedGate ToConditioner(TxNoiseGateSetup g, ParametricEqSetup? eq, TxDeEsserSetup? de, bool auto, float margin,
            TxCompressorSetup? compressor = null, TxLimiterSetup? limiter = null, TxClaritySetup? clarity = null) => new()
        {
            // Sprint 49 Track CL: the compressor stage and the limiter ride in
            // the same record. Null is off, as for the equaliser and the de-esser.
            Comp = compressor?.ToSettings() ?? JJPortaudio.TxCompressorSettings.Default,
            Limiter = limiter?.ToSettings() ?? JJPortaudio.TxLimiterSettings.Default,
            // Sprint 49 Track J: JJ Clarity rides in the same record, as its
            // switch and its settings. Null is off. The DESIGN is not made
            // here: the conditioner builds it when the record is published,
            // with the passband edges it owns.
            ClarityEnabled = clarity?.Enabled ?? false,
            Clarity = clarity?.ToSettings() ?? JJPortaudio.ClaritySettings.Default,
            Enabled = g.Enabled,
            HasThreshold = !auto,
            ThresholdDb = g.ThresholdDb,
            AttackMs = g.AttackMs,
            HoldMs = g.HoldMs,
            ReleaseMs = g.ReleaseMs,
            RangeDb = g.RangeDb,
            AutoThreshold = auto,
            ThresholdMarginDb = Math.Clamp(margin, 3f, 15f),
            RatioToOne = g.RatioToOne,
            GateSetting = g.GateSetting,
            SideChainEnabled = g.SideChainFilter,
            SideChainLowHz = g.SideChainLowCutHz,
            SideChainHighHz = g.SideChainHighCutHz,
            LookAheadMs = g.LookAheadMs,
            Eq = eq?.ToDesign(),
            DeEsserEnabled = de?.Enabled ?? false,
            DeEsserFrequencyHz = de?.FrequencyHz ?? JJPortaudio.DeEsser.DefaultFrequencyHz,
            DeEsserAmountDb = de?.AmountDb ?? JJPortaudio.DeEsser.DefaultAmountDb,
            DeEsserThresholdDb = de?.SensitivityDb ?? JJPortaudio.DeEsser.DefaultThresholdDb,
        };

        /// <summary>What the monitor plays: Off, Output, Residual, or Split
        /// (output left, residual right). Live-switchable while transmitting.</summary>
        public static JJPortaudio.TxAudioConditioner.MonitorModes MonitorMode
        {
            get
            {
                return Conditioner?.MonitorMode
                    ?? JJPortaudio.TxAudioConditioner.MonitorModes.Off;
            }
            set
            {
                var c = Conditioner;
                if (c == null) return;
                c.MonitorMode = value;
                if (value == JJPortaudio.TxAudioConditioner.MonitorModes.Off)
                    _player?.Stop();
                else
                    // Open the output device now, on this thread, so the
                    // first monitored buffer never pays the device-open cost
                    // inside the audio callback.
                    _player?.Prewarm();
            }
        }

        /// <summary>Monitor playback volume, 0..1.</summary>
        public static float MonitorVolume
        {
            get { return _player?.Volume ?? 1f; }
            set { var p = _player; if (p != null) p.Volume = value; }
        }

        // ------------------------------------------------------------------
        // The expander's new levers, the equaliser and the de-esser
        // (Sprint 49 Track I). Switches go through RunOrDefer and land at an
        // unkeyed buffer (S2); continuous levers go through Continuous and are
        // live (S15). Every one is an intent held with no radio (#1077).
        // ------------------------------------------------------------------

        /// <summary>The expander's ratio, n:1, as intended (used when <see cref="ExpanderIsGate"/> is false).</summary>
        public static float ExpanderRatio { get { lock (_gate) return _gateIntent.RatioToOne; } }

        /// <summary>Whether the expander is at its top setting, the gate.</summary>
        public static bool ExpanderIsGate { get { lock (_gate) return _gateIntent.GateSetting; } }

        /// <summary>
        /// The expander's strength: a ratio, or the gate (its top stop). A
        /// continuous lever, live while keyed (S15): it changes how hard the
        /// stage works, not whether it is there. The ratio is kept when the
        /// gate is chosen, so stepping back down returns to it.
        /// </summary>
        public static TxChangeResult SetExpanderStrength(float ratio, bool gate)
        {
            float r = Math.Clamp(float.IsFinite(ratio) ? ratio : JJPortaudio.TxNoiseGate.DefaultRatioToOne,
                JJPortaudio.TxNoiseGate.MinRatioToOne, JJPortaudio.TxNoiseGate.MaxRatioToOne);
            return GateLever(g => { g.RatioToOne = r; g.GateSetting = gate; },
                g => { g.RatioToOne = r; g.GateSetting = gate; },
                g => { g.RatioToOne = r; g.GateSetting = gate; }, sharedWithWdsp: false); // the JJ mode's own strength
        }

        // ── The expander's mode (Sprint 49 Track I phase 2, ruled S6) ──

        /// <summary>The expander's mode as intended: <see cref="ExpanderModes.Jj"/> or <see cref="ExpanderModes.Wdsp"/>.</summary>
        public static string ExpanderMode { get { lock (_gate) return _gateIntent.ExpanderMode; } }

        /// <summary>
        /// The expander's mode, JJ Flexible's or WDSP's: a switch, so it waits
        /// for the unkey (S2), and WDSP's comes into force once its helper
        /// engine is built. Kept as the intent while the helper is missing.
        /// </summary>
        public static TxChangeResult SetExpanderMode(string mode)
        {
            string m = ExpanderModes.IsWdsp(mode) ? ExpanderModes.Wdsp : ExpanderModes.Jj;
            return Chain.RunOrDefer(() =>
            {
                lock (_gate) _gateIntent.ExpanderMode = m;
                SyncWdsp();
            });
        }

        /// <summary>The WDSP mode's depth, dB, as intended (its strength: DEXP has no ratio).</summary>
        public static float WdspDepthDb { get { lock (_gate) return _gateIntent.WdspDepthDb; } }

        /// <summary>
        /// The WDSP mode's depth. It reaches the helper at the unkey (every WDSP
        /// setter restarts the stage's detector), so the timing says so.
        /// </summary>
        public static TxChangeResult SetWdspDepth(float db)
        {
            float v = Math.Clamp(float.IsFinite(db) ? db : ExpanderModes.DefaultWdspDepthDb, ExpanderModes.MinWdspDepthDb, ExpanderModes.MaxWdspDepthDb);
            return Chain.RunOrDefer(() =>
            {
                lock (_gate) _gateIntent.WdspDepthDb = v;
                SyncWdsp();
            });
        }

        /// <summary>Whether WDSP's expander can run here, and if not why (the catalog's probe: never starts the helper).</summary>
        public static Unavailability WdspExpanderAvailability => WdspHelperEngine.Probe(TxWdspExpander.EngineId);

        /// <summary>WDSP's expander told the intent and the threshold in force: the mode, and its settings at the unkey.</summary>
        private static void SyncWdsp() => SyncWdsp(Conditioner, _rig?.TransmitKeyedOrInterlock ?? false);

        private static void SyncWdsp(JJPortaudio.TxAudioConditioner cond, bool keyed)
        {
            try
            {
                TxNoiseGateSetup g;
                lock (_gate) g = _gateIntent.Clone();
                var wdsp = Wdsp;
                wdsp.WantWdsp(ExpanderModes.IsWdsp(g.ExpanderMode));
                // #1349: an automatic threshold reaches WDSP's expander as the
                // margin over the room's AVERAGE, which is what its averaging
                // detector reads, never over our detector's higher reading.
                wdsp.Update(g, cond?.AveragingDetectorThreshold() ?? cond?.Gate.ThresholdDb ?? g.ThresholdDb, keyed);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: WDSP's expander could not be updated: " + ex.Message);
            }
        }

        /// <summary>Whether the expander's detector hears the voice band only, as intended.</summary>
        public static bool SideChainFilter { get { lock (_gate) return _gateIntent.SideChainFilter; } }

        /// <summary>The side-chain filter on or off: a switch, so it waits for the unkey (S2).</summary>
        public static TxChangeResult SetSideChainFilter(bool on) => Chain.RunOrDefer(() =>
        {
            lock (_gate) _gateIntent.SideChainFilter = on;
            Conditioner?.RequestSwitch(g => g.SideChainEnabled = on);
        });

        /// <summary>The side-chain filter's low and high corners, Hz, as intended.</summary>
        public static (float Low, float High) SideChainCorners { get { lock (_gate) return (_gateIntent.SideChainLowCutHz, _gateIntent.SideChainHighCutHz); } }

        /// <summary>The look-ahead, ms, as intended.</summary>
        public static float LookAheadMs { get { lock (_gate) return _gateIntent.LookAheadMs; } }

        /// <summary>
        /// The look-ahead's length: a switch, because it moves the transmitted
        /// stream in time, so it waits for the unkey like an engine change
        /// (Track I, item 1b). 0 to 10 ms.
        /// </summary>
        public static TxChangeResult SetLookAhead(float ms)
        {
            float v = Math.Clamp(float.IsFinite(ms) ? ms : 0f, 0f, JJPortaudio.TxNoiseGate.MaxLookAheadMs);
            return Chain.RunOrDefer(() =>
            {
                lock (_gate) _gateIntent.LookAheadMs = v;
                Conditioner?.RequestSwitch(g => g.LookAheadMs = v);
            });
        }

        /// <summary>The equaliser as intended: a copy.</summary>
        public static ParametricEqSetup EqSetup { get { lock (_gate) return _eqIntent.Clone(); } }

        /// <summary>The equaliser on or off: a switch (S2).</summary>
        public static TxChangeResult SetEqEnabled(bool on) => Chain.RunOrDefer(() =>
        {
            ParametricEqSetup eq;
            lock (_gate) { _eqIntent.Enabled = on; eq = _eqIntent.Clone(); }
            var design = eq.ToDesign();
            Conditioner?.RequestSwitch(g => g.Eq = design);
        });

        /// <summary>
        /// Load a preset's bands into the equaliser: a switch (S2), because it
        /// replaces the whole shape at once. The preset's bands become the
        /// operator's to change; its on or off is left as it was.
        /// </summary>
        public static TxChangeResult LoadEqPreset(string presetId)
        {
            var preset = ParametricEqPresets.Find(presetId);
            if (preset == null) return new TxChangeResult(TxChangeTiming.Refused, "unknown-eq-preset");
            return Chain.RunOrDefer(() =>
            {
                ParametricEqSetup eq;
                lock (_gate)
                {
                    _eqIntent.Bands = preset.Bands.Select(b => b.Clone()).ToList();
                    _eqIntent.OutputGainDb = preset.OutputGainDb;
                    _eqIntent.PresetId = preset.Id;
                    eq = _eqIntent.Clone();
                }
                var design = eq.ToDesign();
                Conditioner?.RequestSwitch(g => g.Eq = design);
            });
        }

        /// <summary>
        /// The equaliser's bands and output gain, edited: a continuous lever,
        /// live while keyed (S15). Adding, removing or changing a band fades
        /// to the new shape without a click (<see cref="JJPortaudio.ParametricEq"/>).
        /// Its on or off is not changed here.
        /// </summary>
        public static TxChangeResult SetEqShape(IEnumerable<ParametricEqBandSetup> bands, float outputGainDb, string presetId = "")
        {
            var list = (bands ?? Array.Empty<ParametricEqBandSetup>()).Where(b => b != null).Select(b => b.Clone()).Take(JJPortaudio.ParametricEq.MaxBands).ToList();
            return Chain.Continuous(() =>
            {
                ParametricEqSetup eq;
                lock (_gate)
                {
                    _eqIntent.Bands = list.Select(b => b.Clone()).ToList();
                    _eqIntent.OutputGainDb = Math.Clamp(float.IsFinite(outputGainDb) ? outputGainDb : 0f, -24f, 24f);
                    _eqIntent.PresetId = presetId ?? "";
                    eq = _eqIntent.Clone();
                }
                var design = eq.ToDesign();
                Conditioner?.ChangeGate(g => g.Eq = design);
            });
        }

        /// <summary>
        /// A whole equaliser setting, on or off included, as ONE switch (repair 1,
        /// B2): held for the unkey while keyed, in order behind anything already
        /// held, so it is what stands once those land.
        /// </summary>
        public static TxChangeResult SetEqWhole(ParametricEqSetup setup)
        {
            var copy = (setup ?? new ParametricEqSetup()).Clone();
            return Chain.RunOrDefer(() =>
            {
                lock (_gate) _eqIntent = copy.Clone();
                var design = copy.ToDesign();
                Conditioner?.RequestSwitch(g => g.Eq = design);
            });
        }

        /// <summary>The de-esser as intended: a copy.</summary>
        public static TxDeEsserSetup DeEsserSetup { get { lock (_gate) return _deEsserIntent.Clone(); } }

        /// <summary>The de-esser on or off: a switch (S2).</summary>
        public static TxChangeResult SetDeEsserEnabled(bool on) => Chain.RunOrDefer(() =>
        {
            lock (_gate) _deEsserIntent.Enabled = on;
            Conditioner?.RequestSwitch(g => g.DeEsserEnabled = on);
        });

        /// <summary>One de-esser level by key, live (S15): "frequency" (Hz), "amount" (dB) or "sensitivity" (dB).</summary>
        public static TxChangeResult SetDeEsserLevel(string key, float value)
        {
            if (!float.IsFinite(value)) return new TxChangeResult(TxChangeTiming.Refused, "not-a-number");
            Action<TxDeEsserSetup>? intent = key switch
            {
                "frequency" => d => d.FrequencyHz = Math.Clamp(value, 1000f, 12000f),
                "amount" => d => d.AmountDb = Math.Clamp(value, 1f, 24f),
                "sensitivity" => d => d.SensitivityDb = Math.Clamp(value, -60f, -10f),
                _ => null,
            };
            if (intent == null) return new TxChangeResult(TxChangeTiming.Refused, "unknown-deesser-lever");
            return Chain.Continuous(() =>
            {
                TxDeEsserSetup d;
                lock (_gate) { intent(_deEsserIntent); d = _deEsserIntent.Clone(); }
                Conditioner?.ChangeGate(g => { g.DeEsserFrequencyHz = d.FrequencyHz; g.DeEsserAmountDb = d.AmountDb; g.DeEsserThresholdDb = d.SensitivityDb; });
            });
        }

        /// <summary>The de-esser's band as it is applied now, Hz, and whether it is clamped below the transmit filter's edge.</summary>
        public static (float AppliedHz, bool Clamped) DeEsserBand
        {
            get
            {
                var c = Conditioner;
                if (c == null) { var d = DeEsserSetup; return (d.FrequencyHz, false); }
                return (c.DeEsser.AppliedFrequencyHz, c.DeEsser.Clamped);
            }
        }

        /// <summary>The chain's added delay on the last buffer, frames: noise reduction's plus the look-ahead (item 9). Zero with no radio.</summary>
        public static int ChainDelayFrames => Conditioner?.AppliedDelayFrames ?? 0;

        // ── Learn the room (#1252; Noel's ruling 2026-11-23 11:12) ──

        /// <summary>How a "Learn the room" request was taken.</summary>
        public enum LearnRoomOutcome
        {
            /// <summary>Listening now, unkeyed.</summary>
            Started,
            /// <summary>Asked while keyed: it listens from the unkey (keyed audio is speech, never the room).</summary>
            WaitsForUnkey,
            /// <summary>CW or digital: the chain stands aside, so nothing reaches the meter.</summary>
            NotInThisMode,
            /// <summary>No computer audio is reaching the chain (the radio's own microphone, or none).</summary>
            NoComputerAudio,
            /// <summary>No radio is connected.</summary>
            NoRadio,
        }

        /// <summary>What a finished learn found and did.</summary>
        public sealed record RoomLearnedResult(float FloorDb, float ThresholdDb, bool AutomaticThreshold);

        /// <summary>Raised on the policy tick when a learn finishes, after it has acted (the hand-set threshold written when the automatic one is off).</summary>
        public static event Action<RoomLearnedResult>? RoomLearned;

        private static long _learnBaseline = -1;

        /// <summary>
        /// "Learn the room": the meter at the expander's input takes the floor
        /// afresh from the next three seconds of unkeyed audio. With the
        /// automatic threshold on, the slow tracking carries on from there; with
        /// it off, the hand-set threshold is WRITTEN from the fresh floor by the
        /// same rule, and holds until the next learn or a change by hand (what
        /// Noel was told on 2026-11-23, register #1252 11:12). One command is
        /// also "remeasure". Asked while keyed it waits for the unkey rather
        /// than refusing, because keyed audio never reaches the meter and the
        /// operator's intent is plain.
        /// </summary>
        public static LearnRoomOutcome LearnRoom()
        {
            var rig = _rig;
            var cond = rig?.TxConditioner;
            if (cond == null) return LearnRoomOutcome.NoRadio;
            if (cond.BypassAll) return LearnRoomOutcome.NotInThisMode;
            if (!cond.HasRecentAudio) return LearnRoomOutcome.NoComputerAudio;
            System.Threading.Interlocked.Exchange(ref _learnBaseline, cond.RoomFloor.LearnsFinished);
            cond.RoomFloor.Learn();
            Trace.WriteLine("TxAudioConditioning: learning the room at the expander's input");
            return rig!.TransmitKeyedOrInterlock ? LearnRoomOutcome.WaitsForUnkey : LearnRoomOutcome.Started;
        }

        /// <summary>Whether a learn is under way.</summary>
        public static bool LearningRoom => Conditioner?.RoomFloor.Learning ?? false;

        /// <summary>The room's floor as the meter at the expander's input has it now, dB, and whether it has one.</summary>
        public static (bool HasFloor, float FloorDb) RoomFloor
        {
            get { var c = Conditioner; return c == null ? (false, float.NaN) : (c.RoomFloor.HasFloor, c.RoomFloor.FloorDb); }
        }

        private static int _wasKeyedForStats;

        /// <summary>The policy tick's Track I half; see <see cref="Attach"/>.</summary>
        private static void PolicyTickStages()
        {
            var rig = _rig;
            var cond = rig?.TxConditioner;
            if (cond == null) return;
            try
            {
                // The de-esser's band follows the transmit filter (radio-neutral:
                // the conditioner only ever hears a number).
                cond.DeEsser.PassbandHighEdgeHz = rig!.TXFilterHigh;
                // JJ Clarity keeps its work inside the transmit filter (Sprint 49
                // Track J): both edges, through the conditioner's one owner,
                // which publishes only when an edge has changed and rebuilds the
                // design in force and the one waiting together. Never through
                // the stage's own edge setters.
                cond.SetClarityPassband(rig.TXFilterLow, rig.TXFilterHigh);
                bool keyed = rig.TransmitKeyedOrInterlock;
                FinishLearnIfDone(cond, keyed);
                // WDSP's expander follows the gate's settings and the threshold in
                // force; it takes them only while unkeyed (TxWdspExpander, remarks).
                SyncWdsp(cond, keyed);
                int now = keyed ? 1 : 0;
                if (System.Threading.Interlocked.Exchange(ref _wasKeyedForStats, now) == 1 && now == 0)
                    TraceOverStats(cond);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: the stages' tick failed: " + ex.Message);
            }
        }

        /// <summary>A finished learn: write the hand-set threshold when the automatic one is off, then tell whoever listens. Policy tick only.</summary>
        internal static void FinishLearnIfDone(JJPortaudio.TxAudioConditioner cond, bool keyed)
        {
            // Repair 1, N6: a learn that finished just before a key-down is acted
            // on at the next UNKEYED tick, like every threshold change (S15), so
            // it never moves the threshold during an over.
            if (keyed) return;
            long baseline = System.Threading.Interlocked.Read(ref _learnBaseline);
            if (baseline < 0 || cond.RoomFloor.LearnsFinished <= baseline) return;
            System.Threading.Interlocked.Exchange(ref _learnBaseline, -1);
            // #1349: the floor JJ Flexible's expander sets its threshold from is
            // its own detector's reading of the room; WDSP's expander averages,
            // so its threshold is set over the room's average. A threshold set
            // by hand from a learn is set for the mode in force.
            bool wdsp;
            lock (_gate) wdsp = ExpanderModes.IsWdsp(_gateIntent.ExpanderMode);
            float floor = wdsp ? cond.RoomFloor.LearnedMeanDb : cond.RoomFloor.LearnedDb;
            bool auto = cond.AutoThresholdInForce;
            float margin = cond.ThresholdMarginInForceDb;
            float threshold = JJPortaudio.TxRoomFloor.ThresholdRule(true, false, true, floor, margin) ?? JJPortaudio.TxNoiseGate.DefaultThresholdDb;
            if (auto) cond.RefreshDerivedThreshold(false);
            else GateThresholdDb = threshold;
            threshold = auto && wdsp ? cond.AveragingDetectorThreshold() ?? cond.Gate.ThresholdDb : cond.Gate.ThresholdDb;
            Trace.WriteLine(FormattableString.Invariant(
                $"TxAudioConditioning: the room was learned: floor {floor:F1} dB at the expander's input; threshold {threshold:F1} dB ({(auto ? "automatic, tracking carries on from here" : "set by hand from the learn, holds until the next learn or a change by hand")})"));
            RoomLearned?.Invoke(new RoomLearnedResult(floor, threshold, auto));
        }

        /// <summary>One trace line per over for the stages after the expander: samples past full scale after the equaliser (it never clips silently), and the de-esser's work. Policy tick only, never the audio thread.</summary>
        private static void TraceOverStats(JJPortaudio.TxAudioConditioner cond)
        {
            TraceDynamicsOverStats(cond);
            TraceClarityOverStats(cond);
            var (clips, reduced, deepest) = cond.TakeOverStats();
            var (hasFloor, floor) = (cond.RoomFloor.HasFloor, cond.RoomFloor.FloorDb);
            Trace.WriteLine(FormattableString.Invariant(
                $"TxAudioConditioning: over ended: equaliser {(cond.Eq.IsActive ? "on" : "off")}, {clips} samples past full scale; de-esser {(cond.DeEsser.Enabled ? "on" : "off")} at {cond.DeEsser.AppliedFrequencyHz:F0} Hz{(cond.DeEsser.Clamped ? " (clamped to the transmit filter)" : "")}, {reduced} frames reduced, deepest {deepest:F1} dB; chain delay {cond.AppliedDelayFrames} frames; room floor {(hasFloor ? floor.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " dB" : "not yet measured")}"));
        }

        // ------------------------------------------------------------------
        // The compressor stage and the limiter (Sprint 49 Track CL, #1278).
        // Switches (on or off, a preset, the mode, the rotator's stage count,
        // the limiter's look-ahead) go through RunOrDefer and land at an
        // unkeyed buffer (S2); every level is a continuous lever through
        // Continuous and is live (S15). Every one is an intent held with no
        // radio (#1077).
        // ------------------------------------------------------------------

        /// <summary>The compressor stage as intended: a copy.</summary>
        public static TxCompressorSetup CompressorSetup { get { lock (_gate) return _compIntent.Clone(); } }

        /// <summary>The limiter as intended: a copy.</summary>
        public static TxLimiterSetup LimiterSetup { get { lock (_gate) return _limiterIntent.Clone(); } }

        /// <summary>The compressor on or off: a switch (S2).</summary>
        public static TxChangeResult SetCompressorEnabled(bool on) => ShapingSwitch(s => s.Compressor.Enabled = on,
            _ => Conditioner?.RequestSwitch(g => g.Comp = g.Comp with { Enabled = on }));

        /// <summary>
        /// Load a preset into the compressor: a switch (S2), because it
        /// replaces the whole shape at once. Every field becomes the
        /// operator's to change; its on or off is left as it was.
        /// </summary>
        public static TxChangeResult LoadCompressorPreset(string presetId)
        {
            var preset = TxCompressorPresets.Find(presetId);
            if (preset == null) return new TxChangeResult(TxChangeTiming.Refused, "unknown-compressor-preset");
            return ShapingSwitch(s => s.Compressor = TxCompressorPresets.Load(preset, s.Compressor.Enabled), now =>
            {
                var loaded = now.Compressor.ToSettings();
                Conditioner?.RequestSwitch(g => g.Comp = loaded with { Enabled = g.Comp.Enabled });
            });
        }

        /// <summary>The compressor's mode, one band or split band: a switch (S2), because the split restarts the stage.</summary>
        public static TxChangeResult SetCompressorSplitBand(bool split) => ShapingSwitch(
            s => s.Compressor.CompressorMode = split ? TxCompressorModes.Split : TxCompressorModes.Single,
            _ => Conditioner?.RequestSwitch(g => g.Comp = g.Comp with { SplitBand = split }));

        /// <summary>The phase rotator's sections, 0 (off) to 6: a switch (S2), because a change of count restarts the rotator.</summary>
        public static TxChangeResult SetCompressorRotator(int stages)
        {
            int n = Math.Clamp(stages, 0, JJPortaudio.PhaseRotator.MaxStages);
            return ShapingSwitch(s => s.Compressor.RotatorStages = n,
                _ => Conditioner?.RequestSwitch(g => g.Comp = g.Comp with { RotatorStages = n }));
        }

        /// <summary>A whole compressor setting, on or off included, as ONE switch (the editor's Cancel): held for the unkey while keyed, in order behind anything already held.</summary>
        public static TxChangeResult SetCompressorWhole(TxCompressorSetup setup)
        {
            var copy = (setup ?? new TxCompressorSetup()).Clone();
            return Ask(Chain, lever: false, () => SetCompressorWholeNow(copy), s => s.Compressor = copy.Clone());
        }

        /// <summary>The same, now, for a caller that is already ONE held change of the chain's (the editor's put-back): it must not be held a second time inside it, or put behind what was asked after it.</summary>
        internal static void SetCompressorWholeNow(TxCompressorSetup setup)
        {
            var copy = (setup ?? new TxCompressorSetup()).Clone();
            lock (_gate) _compIntent = copy.Clone();
            var whole = copy.ToSettings();
            Conditioner?.RequestSwitch(g => g.Comp = whole);
        }

        private static float Within(float value, float low, float high) => Math.Clamp(value, low, high);

        /// <summary>One continuous compressor lever: how it changes the intent, and how it changes the record in force.</summary>
        private static (Action<TxCompressorSetup> Intent, Func<JJPortaudio.TxCompressorSettings, JJPortaudio.TxCompressorSettings> Live)? CompressorLever(string key, float value)
        {
            float v;
            switch (key)
            {
                case "threshold":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinThresholdDb, JJPortaudio.TxCompressorSettings.MaxThresholdDb);
                    return (c => c.ThresholdDb = v, s => s with { ThresholdDb = v });
                case "ratio":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinRatio, JJPortaudio.TxCompressorSettings.MaxRatio);
                    return (c => c.RatioToOne = v, s => s with { RatioToOne = v });
                case "attack":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinAttackMs, JJPortaudio.TxCompressorSettings.MaxAttackMs);
                    return (c => c.AttackMs = v, s => s with { AttackMs = v });
                case "release":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinReleaseMs, JJPortaudio.TxCompressorSettings.MaxReleaseMs);
                    return (c => c.ReleaseMs = v, s => s with { ReleaseMs = v });
                case "knee":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinKneeDb, JJPortaudio.TxCompressorSettings.MaxKneeDb);
                    return (c => c.KneeDb = v, s => s with { KneeDb = v });
                case "makeup":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinMakeupDb, JJPortaudio.TxCompressorSettings.MaxMakeupDb);
                    return (c => c.MakeupDb = v, s => s with { MakeupDb = v });
                case "drive":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinDriveDb, JJPortaudio.TxCompressorSettings.MaxDriveDb);
                    return (c => c.DriveDb = v, s => s with { DriveDb = v });
                case "crossover_low":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinCrossoverLowHz, JJPortaudio.TxCompressorSettings.MaxCrossoverLowHz);
                    return (c => c.CrossoverLowHz = v, s => s with { CrossoverLowHz = v });
                case "crossover_high":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinCrossoverHighHz, JJPortaudio.TxCompressorSettings.MaxCrossoverHighHz);
                    return (c => c.CrossoverHighHz = v, s => s with { CrossoverHighHz = v });
            }
            // A split band's lever: "low.threshold", "mid.ratio", "high.makeup" and so on.
            int dot = key.IndexOf('.');
            if (dot <= 0) return null;
            int band = key[..dot] switch { "low" => 0, "mid" => 1, "high" => 2, _ => -1 };
            if (band < 0) return null;
            static JJPortaudio.TxCompressorSettings WithBand(JJPortaudio.TxCompressorSettings s, int b, Func<JJPortaudio.TxCompressorBand, JJPortaudio.TxCompressorBand> change) =>
                b == 0 ? s with { Low = change(s.Low) } : b == 1 ? s with { Mid = change(s.Mid) } : s with { High = change(s.High) };
            switch (key[(dot + 1)..])
            {
                case "threshold":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinThresholdDb, JJPortaudio.TxCompressorSettings.MaxThresholdDb);
                    return (c => c.Band(band).ThresholdDb = v, s => WithBand(s, band, x => x with { ThresholdDb = v }));
                case "ratio":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinRatio, JJPortaudio.TxCompressorSettings.MaxRatio);
                    return (c => c.Band(band).RatioToOne = v, s => WithBand(s, band, x => x with { RatioToOne = v }));
                case "attack":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinAttackMs, JJPortaudio.TxCompressorSettings.MaxAttackMs);
                    return (c => c.Band(band).AttackMs = v, s => WithBand(s, band, x => x with { AttackMs = v }));
                case "release":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinReleaseMs, JJPortaudio.TxCompressorSettings.MaxReleaseMs);
                    return (c => c.Band(band).ReleaseMs = v, s => WithBand(s, band, x => x with { ReleaseMs = v }));
                case "makeup":
                    v = Within(value, JJPortaudio.TxCompressorSettings.MinMakeupDb, JJPortaudio.TxCompressorSettings.MaxMakeupDb);
                    return (c => c.Band(band).MakeupDb = v, s => WithBand(s, band, x => x with { MakeupDb = v }));
            }
            return null;
        }

        /// <summary>
        /// One compressor level by key, live (S15): "threshold", "ratio",
        /// "attack", "release", "knee", "makeup", "drive", "crossover_low",
        /// "crossover_high", and a split band's "low.threshold", "mid.ratio",
        /// "high.makeup" and so on. "rotator" is the stage count, a switch.
        /// </summary>
        public static TxChangeResult SetCompressorLevel(string key, float value)
        {
            if (!float.IsFinite(value)) return new TxChangeResult(TxChangeTiming.Refused, "not-a-number");
            if (key == "rotator") return SetCompressorRotator((int)Math.Round(value));
            var lever = CompressorLever(key ?? "", value);
            if (lever == null) return new TxChangeResult(TxChangeTiming.Refused, "unknown-compressor-lever");
            var (intent, live) = lever.Value;
            return ShapingLever(s => { intent(s.Compressor); s.Compressor.PresetId = ""; },
                _ => Conditioner?.ChangeGate(g => g.Comp = live(g.Comp)));
        }

        /// <summary>The limiter on or off: a switch (S2).</summary>
        public static TxChangeResult SetLimiterEnabled(bool on) => ShapingSwitch(s => s.Limiter.Enabled = on,
            _ => Conditioner?.RequestSwitch(g => g.Limiter = g.Limiter with { Enabled = on }));

        /// <summary>One limiter level by key: "ceiling" (dBFS) and "release" (ms) are live (S15); "lookahead" (ms) is a switch, because its length moves the stream in time.</summary>
        public static TxChangeResult SetLimiterLevel(string key, float value)
        {
            if (!float.IsFinite(value)) return new TxChangeResult(TxChangeTiming.Refused, "not-a-number");
            float v;
            switch (key)
            {
                case "ceiling":
                    v = Within(value, JJPortaudio.TxLimiterSettings.MinCeilingDb, JJPortaudio.TxLimiterSettings.MaxCeilingDb);
                    return ShapingLever(s => s.Limiter.CeilingDb = v,
                        _ => Conditioner?.ChangeGate(g => g.Limiter = g.Limiter with { CeilingDb = v }));
                case "release":
                    v = Within(value, JJPortaudio.TxLimiterSettings.MinReleaseMs, JJPortaudio.TxLimiterSettings.MaxReleaseMs);
                    return ShapingLever(s => s.Limiter.ReleaseMs = v,
                        _ => Conditioner?.ChangeGate(g => g.Limiter = g.Limiter with { ReleaseMs = v }));
                case "lookahead":
                    v = Within(value, JJPortaudio.TxLimiterSettings.MinLookAheadMs, JJPortaudio.TxLimiterSettings.MaxLookAheadMs);
                    return ShapingSwitch(s => s.Limiter.LookAheadMs = v,
                        _ => Conditioner?.RequestSwitch(g => g.Limiter = g.Limiter with { LookAheadMs = v }));
            }
            return new TxChangeResult(TxChangeTiming.Refused, "unknown-limiter-lever");
        }

        /// <summary>A whole limiter setting as ONE switch (the editor's Cancel).</summary>
        public static TxChangeResult SetLimiterWhole(TxLimiterSetup setup)
        {
            var copy = (setup ?? new TxLimiterSetup()).Clone();
            return Ask(Chain, lever: false, () => SetLimiterWholeNow(copy), s => s.Limiter = copy.Clone());
        }

        /// <summary>The same, now (see <see cref="SetCompressorWholeNow"/>).</summary>
        internal static void SetLimiterWholeNow(TxLimiterSetup setup)
        {
            var copy = (setup ?? new TxLimiterSetup()).Clone();
            lock (_gate) _limiterIntent = copy.Clone();
            var whole = copy.ToSettings();
            Conditioner?.RequestSwitch(g => g.Limiter = whole);
        }

        // ── readings: on demand, never per frame ──

        /// <summary>What the compressor and the limiter are doing now. Running is false with no radio, when the stage is off in force, and when no buffer has reached the chain in the last half second (Track J repair 1, B3: the stages keep the last buffer's figure, and that is not "now" once the audio has stopped).</summary>
        public sealed record DynamicsNow(bool CompressorRunning, float CompressorReductionDb, bool SplitBand,
            float LowReductionDb, float MidReductionDb, float HighReductionDb, bool LimiterRunning, float LimiterReductionDb);

        /// <summary>The gain reduction being applied now, read from the stages (the end of the last buffer).</summary>
        public static DynamicsNow ReadDynamicsNow()
        {
            var c = Conditioner;
            if (c == null) return new DynamicsNow(false, 0, false, 0, 0, 0, false, 0);
            var comp = c.Compressor;
            var s = comp.Settings;
            bool arriving = c.HasRecentAudio;
            bool running = s.Enabled && !c.BypassAll && arriving;
            bool limiting = c.Limiter.Settings.Enabled && !c.BypassAll && arriving;
            return new DynamicsNow(running, running ? comp.ReductionDb : 0f, s.SplitBand,
                running ? comp.BandReductionDb(0) : 0f, running ? comp.BandReductionDb(1) : 0f, running ? comp.BandReductionDb(2) : 0f,
                limiting, limiting ? c.Limiter.ReductionDb : 0f);
        }

        /// <summary>One over's readings from the compressor and the limiter.</summary>
        public sealed record DynamicsOver(float CompressorMaxDb, float CompressorAverageDb, long CompressorFrames,
            long LimiterCaught, long OutputOverFullScale, long LimitedFrames, float LimiterMaxDb);

        /// <summary>The last finished over's readings, or null before any. Written by the policy tick at the unkey.</summary>
        public static DynamicsOver? LastDynamicsOver { get; private set; }

        /// <summary>One trace line per over for the compressor and the limiter, and the readings kept for the "now" sentence. Policy tick only, never the audio thread.</summary>
        private static void TraceDynamicsOverStats(JJPortaudio.TxAudioConditioner cond)
        {
            var (comp, lim) = cond.TakeDynamicsOverStats();
            var over = new DynamicsOver(comp.MaxReductionDb, comp.AverageReductionDb, comp.Frames,
                lim.Caught, lim.OutputOverFullScale, lim.LimitedFrames, lim.MaxReductionDb);
            LastDynamicsOver = over;
            var cs = cond.Compressor.Settings;
            var ls = cond.Limiter.Settings;
            Trace.WriteLine(FormattableString.Invariant(
                $"TxAudioConditioning: over ended: compressor {(cs.Enabled ? (cs.SplitBand ? "on (split band)" : "on") : "off")}, deepest reduction {over.CompressorMaxDb:F1} dB, average {over.CompressorAverageDb:F1} dB over {over.CompressorFrames} frames, drive {cs.DriveDb:F0} dB, rotator {cs.RotatorStages}; limiter {(ls.Enabled ? "on" : "off")} at {ls.CeilingDb:F1} dBFS, look-ahead {cond.Limiter.AppliedLookAheadFrames} frames, caught {over.LimiterCaught} samples over the ceiling on {over.LimitedFrames} frames, deepest {over.LimiterMaxDb:F1} dB, {over.OutputOverFullScale} samples left over full scale"));
        }

        // ------------------------------------------------------------------
        // JJ Clarity (Sprint 49 Track J, #736): an exciter, after the de-esser
        // and before the compressor. Switches (the whole stage, a band's own
        // on or off, a band's harmonics, a preset) go through RunOrDefer and
        // land at an unkeyed buffer (S2); tune, drive and mix are continuous
        // levers through Continuous and are live (S15). Every one is an
        // intent held with no radio (#1077). It changes nothing on the radio.
        // ------------------------------------------------------------------

        /// <summary>JJ Clarity as intended: a copy.</summary>
        public static TxClaritySetup ClaritySetup { get { lock (_gate) return _clarityIntent.Clone(); } }

        /// <summary>JJ Clarity as a whole, on or off: a switch (S2).</summary>
        public static TxChangeResult SetClarityEnabled(bool on) => ShapingSwitch(s => s.Clarity.Enabled = on,
            _ => Conditioner?.RequestSwitch(g => g.ClarityEnabled = on));

        /// <summary>Put JJ Clarity's bands as they now read into whatever record is waiting, as a switch.</summary>
        private static void RequestClarityBands(TxShaping now)
        {
            var settings = now.Clarity.ToSettings();
            Conditioner?.RequestSwitch(g => g.Clarity = settings);
        }

        /// <summary>One band of JJ Clarity on or off (the high band adds brightness, the low band fullness): a switch (S2).</summary>
        public static TxChangeResult SetClarityBandEnabled(bool high, bool on) => ShapingSwitch(
            s => { s.Clarity.Band(high).Enabled = on; s.Clarity.PresetId = ""; }, RequestClarityBands);

        /// <summary>The harmonics one band makes (a word of <see cref="TxClarityHarmonics"/>): a switch (S2), because it changes the sound's character and the band the shaper is fed.</summary>
        public static TxChangeResult SetClarityHarmonics(bool high, string harmonics)
        {
            string word = TxClarityHarmonics.ToWord(TxClarityHarmonics.ToChoice(harmonics));
            return ShapingSwitch(s => { s.Clarity.Band(high).Harmonics = word; s.Clarity.PresetId = ""; }, RequestClarityBands);
        }

        /// <summary>
        /// Load a preset into JJ Clarity: a switch (S2), because it replaces
        /// both bands at once. Every field becomes the operator's to change;
        /// the whole stage's on or off is left as it was.
        /// </summary>
        public static TxChangeResult LoadClarityPreset(string presetId)
        {
            var preset = JJPortaudio.ClarityPresets.Find((presetId ?? "").Trim());
            if (preset == null) return new TxChangeResult(TxChangeTiming.Refused, "unknown-clarity-preset");
            return ShapingSwitch(s => s.Clarity = TxClaritySetup.Load(preset, s.Clarity.Enabled), RequestClarityBands);
        }

        /// <summary>
        /// One JJ Clarity level by key, live (S15): "high.tune", "high.drive",
        /// "high.mix", "low.tune", "low.drive", "low.mix". The value is
        /// clamped to the lever's range; a tune stays the operator's choice
        /// and is applied clamped inside the transmit filter.
        /// </summary>
        public static TxChangeResult SetClarityLevel(string key, float value)
        {
            if (!float.IsFinite(value)) return new TxChangeResult(TxChangeTiming.Refused, "not-a-number");
            int dot = (key ?? "").IndexOf('.');
            string bandWord = dot > 0 ? key![..dot] : "";
            if (bandWord != "high" && bandWord != "low") return new TxChangeResult(TxChangeTiming.Refused, "unknown-clarity-lever");
            bool high = bandWord == "high";
            Action<TxClarityBandSetup>? intent = key![(dot + 1)..] switch
            {
                "tune" => b => b.TuneHz = high
                    ? Within(value, JJPortaudio.ClaritySettings.HighTuneMinHz, JJPortaudio.ClaritySettings.HighTuneMaxHz)
                    : Within(value, JJPortaudio.ClaritySettings.LowTuneMinHz, JJPortaudio.ClaritySettings.LowTuneMaxHz),
                "drive" => b => b.DriveDb = Within(value, JJPortaudio.ClaritySettings.DriveMinDb, JJPortaudio.ClaritySettings.DriveMaxDb),
                "mix" => b => b.MixPercent = Within(value, JJPortaudio.ClaritySettings.MixMinPercent, JJPortaudio.ClaritySettings.MixMaxPercent),
                _ => null,
            };
            if (intent == null) return new TxChangeResult(TxChangeTiming.Refused, "unknown-clarity-lever");
            return ShapingLever(s => { intent(s.Clarity.Band(high)); s.Clarity.PresetId = ""; }, now =>
            {
                var settings = now.Clarity.ToSettings();
                // The one owner: in force and in anything waiting, each keeping
                // its own switch. The conditioner builds the design.
                Conditioner?.ChangeGate(g => g.Clarity = Levers(g.Clarity, settings));
            });
        }

        /// <summary>
        /// A record's Clarity settings with the LEVERS of <paramref name="want"/>
        /// (each band's tune, drive and mix) and its own switches (each band's
        /// on or off and harmonics) kept, so a lever turned while a switch is
        /// still waiting for the unkey never carries that switch into force.
        /// </summary>
        private static JJPortaudio.ClaritySettings Levers(JJPortaudio.ClaritySettings record, JJPortaudio.ClaritySettings want)
        {
            record ??= JJPortaudio.ClaritySettings.Default;
            static JJPortaudio.ClarityBand Band(JJPortaudio.ClarityBand keep, JJPortaudio.ClarityBand lever) =>
                keep.WithTune(lever.TuneHz).WithDrive(lever.DriveDb).WithMix(lever.MixPercent);
            return new JJPortaudio.ClaritySettings(Band(record.High, want.High), Band(record.Low, want.Low));
        }

        /// <summary>A whole JJ Clarity setting, on or off included, as ONE switch (the suggest pass's Keep, the editor's put-back): held for the unkey while keyed, in order behind anything already held.</summary>
        public static TxChangeResult SetClarityWhole(TxClaritySetup setup)
        {
            var copy = (setup ?? new TxClaritySetup()).Clone();
            return Ask(Chain, lever: false, () => SetClarityWholeNow(copy), ClarityEdit(copy));
        }

        /// <summary>The same, now, for a caller that is already ONE held change of the chain's (see <see cref="SetCompressorWholeNow"/>).</summary>
        internal static void SetClarityWholeNow(TxClaritySetup setup)
        {
            var copy = (setup ?? new TxClaritySetup()).Clone();
            lock (_gate) _clarityIntent = copy.Clone();
            var settings = copy.ToSettings();
            bool on = copy.Enabled;
            Conditioner?.RequestSwitch(g => { g.ClarityEnabled = on; g.Clarity = settings; });
        }

        /// <summary>
        /// A whole setup's JJ Clarity as an INTENT, for a caller that is about
        /// to stage the rest of the setup (as <see cref="StageDynamics"/> is
        /// for the compressor and the limiter): the one staged record then
        /// carries it. Null is off. Nothing reaches the conditioner from here.
        /// </summary>
        internal static void StageClarity(TxClaritySetup? clarity)
        {
            CommitShaping(ClarityEdit(clarity));
        }

        /// <summary>JJ Clarity as intended: a copy (the whole-setup roads' name for <see cref="ClaritySetup"/>).</summary>
        public static TxClaritySetup CaptureClarity() => ClaritySetup;

        // ── readings: on demand, never per frame ──

        /// <summary>
        /// What JJ Clarity is doing now. Running is false with no radio, in CW
        /// or digital, when the stage is off or adds nothing in force, and
        /// when no buffer has reached the chain in the last half second (the
        /// computer's audio was switched off or its stream was lost, repair 1,
        /// B3): the stage keeps the last buffer's figure for ever, and a
        /// figure from a buffer that is no longer arriving is not "now".
        /// AddingDbfs is the RMS of what it added on the last buffer (-200:
        /// nothing). The applied tunes are the chosen ones clamped inside the
        /// transmit filter at the rate in force.
        /// </summary>
        public sealed record ClarityNow(bool Running, float AddingDbfs, float AppliedHighTuneHz, bool HighTuneClamped,
            float AppliedLowTuneHz, bool LowTuneClamped, float PassbandLowHz, float PassbandHighHz);

        /// <summary>The reading now, from the stage (the end of the last buffer).</summary>
        public static ClarityNow ReadClarityNow()
        {
            var c = Conditioner;
            if (c == null)
            {
                var s = ClaritySetup;
                return new ClarityNow(false, -200f, s.Band(true).TuneHz, false, s.Band(false).TuneHz, false, 0f, 0f);
            }
            var stage = c.Clarity;
            bool running = stage.IsActive && !c.BypassAll && c.HasRecentAudio;
            return new ClarityNow(running, running ? stage.AddingNowDbfs : -200f, stage.AppliedHighTuneHz, stage.HighTuneClamped,
                stage.AppliedLowTuneHz, stage.LowTuneClamped, c.ClarityPassbandLowEdgeHz, c.ClarityPassbandHighEdgeHz);
        }

        /// <summary>One over's readings from JJ Clarity: what it added, and the samples past full scale at its output.</summary>
        public sealed record ClarityOver(long Samples, double AddedRmsDbfs, double AddedPeakDbfs, long OverFullScale);

        /// <summary>The last finished over's readings, or null before any. Written by the policy tick at the unkey.</summary>
        public static ClarityOver? LastClarityOver { get; private set; }

        /// <summary>One trace line per over for JJ Clarity, and the readings kept for the "now" sentence. Policy tick only, never the audio thread.</summary>
        private static void TraceClarityOverStats(JJPortaudio.TxAudioConditioner cond)
        {
            var stats = cond.TakeClarityOverStats();
            var over = new ClarityOver(stats.Samples, stats.AddedRmsDbfs, stats.AddedPeakDbfs, stats.OverFullScale);
            LastClarityOver = over;
            var inForce = cond.GateInForce;
            var s = inForce.Clarity ?? JJPortaudio.ClaritySettings.Default;
            var stage = cond.Clarity;
            Trace.WriteLine(FormattableString.Invariant(
                $"TxAudioConditioning: over ended: JJ Clarity {(inForce.ClarityEnabled ? "on" : "off")}; high band {s.High} applied at {stage.AppliedHighTuneHz:F0} Hz{(stage.HighTuneClamped ? " (clamped to the transmit filter)" : "")}; low band {s.Low} applied at {stage.AppliedLowTuneHz:F0} Hz{(stage.LowTuneClamped ? " (clamped)" : "")}; passband {cond.ClarityPassbandLowEdgeHz:F0} to {cond.ClarityPassbandHighEdgeHz:F0} Hz; added {over.AddedRmsDbfs:F1} dBFS RMS, peak {over.AddedPeakDbfs:F1} dBFS, over {over.Samples} samples; {over.OverFullScale} samples past full scale at its output"));
        }

        // ── the handoff with the radio's speech processor (#1278) ──

        /// <summary>One handoff tick: what is true on the computer's side now, handed to the radio-neutral policy. Policy tick only.</summary>
        private static void ProcessorHandoffTick()
        {
            var rig = _rig;
            var cond = rig?.TxConditioner;
            if (cond == null) return;
            try
            {
                rig!.ProcessorHandoff.Tick(new ProcessorHandoffInputs(
                    SettingOn: AudioOutputConfig.RadioProcessorOffWithCompressorInForce,
                    // In force on the chain: a switch still waiting for the unkey has not come on yet.
                    CompressorOn: cond.GateInForce.Comp.Enabled,
                    PcMicrophone: rig.PcMicrophoneIsTransmitSource,
                    VoiceMode: !cond.BypassAll,
                    ChainFaults: rig.TransmitChainFaults));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: the processor handoff's tick failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The live link to this radio is about to be torn down (Disconnect,
        /// Exit, a retry's unwire): put the radio's speech processor back if
        /// our compressor turned it off, NOW, while the rig and its transport
        /// are still up (#1278; the brief's question 8; repair 1, B2). Called
        /// from <c>MainWindow.UnwireRadioEvents</c>, which
        /// <c>CloseTheRadio</c> calls before it disposes the rig: the one road
        /// an ordinary Disconnect and Exit both take. The write is made at
        /// once on this thread, never queued, because the command queue is
        /// about to stop. If the radio is transmitting or already gone,
        /// nothing is written and the record waits for the next connect.
        /// </summary>
        public static void LeaveRadioBeforeTeardown(FlexBase? rig)
        {
            if (rig == null) return;
            try { rig.ProcessorHandoff.Leaving(); }
            catch (Exception ex) { Trace.WriteLine("TxAudioConditioning: the processor handoff's leave failed: " + ex.Message); }
        }

        /// <summary>The words (a DRAFT PROSE key) for each thing the handoff did.</summary>
        internal static string ProcessorHandoffKey(ProcessorHandoffNotice notice) => notice switch
        {
            ProcessorHandoffNotice.TurnedOff => "audio.processor_handoff.turned_off",
            ProcessorHandoffNotice.TurnedOffShared => "audio.processor_handoff.turned_off_shared",
            ProcessorHandoffNotice.PutBack => "audio.processor_handoff.put_back",
            ProcessorHandoffNotice.PutBackShared => "audio.processor_handoff.put_back_shared",
            ProcessorHandoffNotice.PutBackAtConnect => "audio.processor_handoff.put_back_at_connect",
            ProcessorHandoffNotice.StillOffAtConnect => "audio.processor_handoff.still_off_at_connect",
            ProcessorHandoffNotice.PutBackAfterFault => "audio.processor_handoff.put_back_after_fault",
            _ => "audio.processor_handoff.left_alone_not_yours",
        };

        /// <summary>Tests only: what would have been said, in place of speech.</summary>
        internal static Action<string, string>? ProcessorHandoffSpeechForTest;

        /// <summary>The handoff did something: say it, once, on its own subject. Never silent (#1278).</summary>
        private static void OnProcessorHandoffNotice(ProcessorHandoffNotice notice)
        {
            try
            {
                string key = ProcessorHandoffKey(notice);
                Trace.WriteLine("TxAudioConditioning: processor handoff notice: " + notice);
                if (ProcessorHandoffSpeechForTest is { } test) { test(key, Radios.Speech.SpeechSubject.ProcessorHandoff); return; }
                ScreenReaderOutput.Speak(Lexicon.Get(key),
                    Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                    subject: Radios.Speech.SpeechSubject.ProcessorHandoff);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: the processor handoff's notice could not be given: " + ex.Message);
            }
        }

        // ── the take header's line about the computer's chain (CL8) ──

        private static string _namedSetup = "";
        private static TxNoiseSetup? _namedSetupAsApplied;

        private static void NoteNamedSetupApplied(TxNoiseSetup setup)
        {
            lock (_gate) { _namedSetup = setup.Name ?? ""; _namedSetupAsApplied = setup.Clone(); }
        }

        /// <summary>
        /// One line for a listen-back take's header: the named transmit setup
        /// last applied (and whether the chain still is that setup), the
        /// stages that are on in order, and the compressor's and the limiter's
        /// settings. A comparison bench reads it; nothing is said from it.
        /// </summary>
        internal static string DescribePcChain()
        {
            var s = CaptureSetup("");
            string name; TxNoiseSetup? asApplied;
            lock (_gate) { name = _namedSetup; asApplied = _namedSetupAsApplied?.Clone(); }
            string applied = "no-named-setup";
            if (asApplied != null)
            {
                // The automatic threshold moves by itself; it is not a change to the setup.
                if (s.Gate.AutoThreshold && asApplied.Gate.AutoThreshold) asApplied.Gate.ThresholdDb = s.Gate.ThresholdDb;
                applied = s.SameContentAs(asApplied) ? "as-applied" : "changed-since-applied";
            }
            var stages = new List<string>(6);
            if (s.Spectral.Enabled || s.Neural.Enabled) stages.Add(JJPortaudio.TxAudioConditioner.StageIds.NoiseReduction);
            if (s.Gate.Enabled) stages.Add(JJPortaudio.TxAudioConditioner.StageIds.Expander);
            if (s.Eq.Enabled) stages.Add(JJPortaudio.TxAudioConditioner.StageIds.Equaliser);
            if (s.DeEsser.Enabled) stages.Add(JJPortaudio.TxAudioConditioner.StageIds.DeEsser);
            // JJ Clarity is named when it is on (Sprint 49 Track J, J9): the
            // same key Track CL added, one more stage and its settings.
            if (s.Clarity.Enabled) stages.Add(JJPortaudio.TxAudioConditioner.StageIds.Clarity);
            if (s.Compressor.Enabled) stages.Add(JJPortaudio.TxAudioConditioner.StageIds.Compressor);
            if (s.Limiter.Enabled) stages.Add(JJPortaudio.TxAudioConditioner.StageIds.Limiter);
            var c = s.Compressor.ToSettings();
            var l = s.Limiter.ToSettings();
            bool bypassed = Conditioner?.BypassAll == true;
            var cl = s.Clarity.ToSettings();
            static string ClarityBandText(JJPortaudio.ClarityBand b) => b.Enabled
                ? FormattableString.Invariant($"tune {b.TuneHz:0} Hz, drive {b.DriveDb:0.#} dB, mix {b.MixPercent:0.#} percent, {TxClarityHarmonics.ToWord(b.Harmonics)} harmonics")
                : "off";
            string clarity = s.Clarity.Enabled
                ? "high band " + ClarityBandText(cl.High) + "; low band " + ClarityBandText(cl.Low) + (s.Clarity.PresetId.Length > 0 ? "; preset " + s.Clarity.PresetId : "")
                : "off";
            return FormattableString.Invariant(
                $"setup={(name.Length > 0 ? name : "none")} ({applied}); stages={(stages.Count > 0 ? string.Join(",", stages) : "none")}{(bypassed ? " (standing aside: CW or digital)" : "")}; clarity=[{clarity}]; compressor={(c.Enabled ? (c.SplitBand ? "split band, crossovers " + c.CrossoverLowHz.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " and " + c.CrossoverHighHz.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " Hz, " : "") + FormattableString.Invariant($"threshold {c.ThresholdDb:0.#} dBFS peak, ratio {c.RatioToOne:0.##}, attack {c.AttackMs:0.#} ms, release {c.ReleaseMs:0} ms, knee {c.KneeDb:0.#} dB, makeup {c.MakeupDb:0.#} dB, drive {c.DriveDb:0.#} dB, rotator {c.RotatorStages}") + (s.Compressor.PresetId.Length > 0 ? ", preset " + s.Compressor.PresetId : "") : "off")}; limiter={(l.Enabled ? FormattableString.Invariant($"ceiling {l.CeilingDb:0.#} dBFS, release {l.ReleaseMs:0} ms, look-ahead {l.LookAheadMs:0.#} ms") : "off")}");
        }

        // ------------------------------------------------------------------
        // Setups (item F)
        // ------------------------------------------------------------------

        /// <summary>The live state as a setup: both slots as the operator chose them, and the gate.</summary>
        public static TxNoiseSetup CaptureSetup(string name)
        {
            var (spectral, neural) = Chain.CaptureSlots();
            TxNoiseGateSetup gate;
            lock (_gate) gate = _gateIntent.Clone();
            gate.AutoThreshold = _autoThreshold;
            gate.ThresholdMarginDb = _thresholdMarginDb;
            // The live gate's figure is the truth for a DERIVED threshold (only
            // the gate knows what the rule last gave). A threshold set by hand is
            // the intent's: the live gate takes a staged record only on its next
            // unkeyed buffer, so reading it here straight after a setup was
            // applied gave the threshold from before the setup, and the layer's
            // Escape then took the gate for somebody else's and left it on
            // (Track CL repair 1, found by B4's test with a gate in the setup).
            var live = Conditioner?.Gate;
            if (live != null && gate.AutoThreshold) gate.ThresholdDb = live.ThresholdDb;
            ParametricEqSetup eq; TxDeEsserSetup de; string[] order; TxCompressorSetup comp; TxLimiterSetup lim; TxClaritySetup clarity;
            lock (_gate)
            {
                eq = _eqIntent.Clone(); de = _deEsserIntent.Clone(); order = (string[])_orderIntent.Clone();
                comp = _compIntent.Clone(); lim = _limiterIntent.Clone(); clarity = _clarityIntent.Clone();
            }
            return new TxNoiseSetup
            {
                Name = name ?? "", Spectral = spectral, Neural = neural, Gate = gate, Eq = eq, DeEsser = de, StageOrder = order,
                Compressor = comp, Limiter = lim, Clarity = clarity,
            };
        }

        /// <summary>
        /// Put a setup in place: both slots (their engine and on-or-off changes
        /// wait for the unkey when keyed, item B; the returned timing says
        /// which) and the gate (live).
        /// </summary>
        public static TxChangeResult ApplySetup(TxNoiseSetup setup)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            // A copy now: the caller's object may change before the unkey.
            var copy = setup.Clone();
            return Ask(Chain, lever: false, () => ApplySetupNow(copy), WholeSetupEdit(copy));
        }

        /// <summary>
        /// A whole setup, now, as ONE change (repair round 1, R3: a setup chosen
        /// or a microphone switched while keyed waits for the unkey entirely,
        /// gate included). The slots are replaced, not overlaid (R2).
        /// </summary>
        private static void ApplySetupNow(TxNoiseSetup setup)
        {
            Chain.ApplySlotsNow(setup.Spectral, setup.Neural);
            ApplyStagesNow(setup);
        }

        /// <summary>
        /// The in-process stages of a whole setup, now, as ONE staged record
        /// (Sprint 49 Track I): the expander, the equaliser, the de-esser and
        /// the stage order. For a caller that holds the whole change through
        /// <see cref="TxNoiseChain.RunOrDefer"/> already.
        /// </summary>
        internal static void ApplyStagesNow(TxNoiseSetup setup)
        {
            // The compressor and the limiter first, as intents only: the record
            // the next line stages carries them, so the whole setup still lands
            // on ONE unkeyed buffer (Sprint 49 Track CL).
            StageDynamics(setup.Compressor, setup.Limiter);
            // And JJ Clarity, the same way (Sprint 49 Track J): a setup saved
            // before it existed carries it off, so applying one switches it off.
            StageClarity(setup.Clarity);
            ApplyStagesNow(setup.Eq, setup.DeEsser, setup.StageOrder, setup.Gate ?? new TxNoiseGateSetup());
        }

        /// <summary>
        /// A whole setup's compressor and limiter as INTENTS, for a caller
        /// that is about to stage the rest of the setup (<see cref="ApplyStagesNow(ParametricEqSetup?, TxDeEsserSetup?, string[]?, TxNoiseGateSetup?)"/>
        /// or <see cref="ApplyGateNow"/>), whose one staged record then carries
        /// them. Null is off. Nothing reaches the conditioner from here.
        /// </summary>
        internal static void StageDynamics(TxCompressorSetup? compressor, TxLimiterSetup? limiter)
        {
            CommitShaping(DynamicsEdit(compressor, limiter));
        }

        /// <summary>The compressor and the limiter as intended: copies.</summary>
        public static (TxCompressorSetup Compressor, TxLimiterSetup Limiter) CaptureDynamics()
        {
            lock (_gate) return (_compIntent.Clone(), _limiterIntent.Clone());
        }

        /// <summary>
        /// The equaliser, the de-esser and the stage order, now, with the gate
        /// (or the gate as intended, when null) as ONE staged record. The noise
        /// controller's whole-setup road (presets, a radio's setups) comes here.
        /// </summary>
        internal static void ApplyStagesNow(ParametricEqSetup? eq, TxDeEsserSetup? deEsser, string[]? order, TxNoiseGateSetup? gate)
        {
            var o = (string[])(order ?? JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder).Clone();
            lock (_gate)
            {
                _eqIntent = (eq ?? new ParametricEqSetup()).Clone();
                _deEsserIntent = (deEsser ?? new TxDeEsserSetup()).Clone();
                _orderIntent = o;
                gate ??= _gateIntent.Clone();
            }
            // An older build's default (this build's with the stages added since
            // missing) runs as the default and is not worth a line (Track CL).
            if (!JJPortaudio.TxAudioConditioner.StageIds.RunsAsDefault(o))
                Trace.WriteLine("TxAudioConditioning: the setup names the stage order " + string.Join(",", o)
                    + "; this build runs only " + string.Join(",", JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder) + ", and keeps the saved order as it is.");
            ApplyGateNow(gate);
        }

        /// <summary>
        /// The gate half of a setup, now, on the control side (Sprint 49 Track
        /// H2): for a caller that holds a whole setup or preset as ONE change
        /// through <see cref="TxNoiseChain.RunOrDefer"/> and must not let the
        /// gate's own on and off be deferred a second time inside it, or put
        /// behind changes asked for after it.
        /// </summary>
        internal static void ApplyGateNow(TxNoiseGateSetup gate)
        {
            var g = (gate ?? new TxNoiseGateSetup()).Clone();
            _autoThreshold = g.AutoThreshold;
            _thresholdMarginDb = Math.Clamp(g.ThresholdMarginDb, 3f, 15f);
            ParametricEqSetup eq; TxDeEsserSetup de; TxCompressorSetup comp; TxLimiterSetup lim; TxClaritySetup clarity;
            lock (_gate) { _gateIntent = g; eq = _eqIntent.Clone(); de = _deEsserIntent.Clone(); comp = _compIntent.Clone(); lim = _limiterIntent.Clone(); clarity = _clarityIntent.Clone(); }
            // The gate as a whole, on or off, levels and threshold policy, staged
            // for the next unkeyed buffer (repair round 3): never partway through
            // an over, even if this call is running when a key-down arrives. The
            // equaliser and the de-esser in force ride in the same record (Track I).
            Conditioner?.StageGate(ToConditioner(g, eq, de, g.AutoThreshold, g.ThresholdMarginDb, comp, lim, clarity));
            SyncWdsp();
        }

        private static readonly HashSet<string> _missingSaid = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Apply the setup a microphone names on a radio. A name this radio does
        /// not hold applies NOTHING and keeps the chain as it was (ruled
        /// 2026-11-07 07:52); <see cref="TxSetupApplyResult.FirstTime"/> is true
        /// the first time a given radio and name are found missing this session,
        /// so the caller says it once. A name found again after being missing
        /// counts afresh.
        /// </summary>
        public static TxSetupApplyResult ApplyNamedSetup(string radioId, string name, TxNoiseSetupStore store = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return new(TxSetupApplyOutcome.NoName);
            store ??= TxNoiseSetupStore.Default();
            if (store == null) return new(TxSetupApplyOutcome.NoName);
            string key = (radioId ?? "") + "|" + name.Trim();
            var setup = store.Find(radioId, name);
            if (setup == null)
            {
                bool first;
                lock (_missingSaid) first = _missingSaid.Add(key);
                Trace.WriteLine($"TxAudioConditioning: radio {radioId} has no transmit noise setup called '{name}'; nothing applied, the last values stay.");
                return new(TxSetupApplyOutcome.Missing, first);
            }
            lock (_missingSaid) _missingSaid.Remove(key);
            var timing = ApplySetup(setup).Timing;
            NoteNamedSetupApplied(setup);
            return new(TxSetupApplyOutcome.Applied, false, timing);
        }

        /// <summary>The old settings shape, for code that still reads it: the neural slot and the gate.</summary>
        public static TxConditioningSettings CaptureSettings() => TxNoiseSetupMigration.ToConditioning(CaptureSetup(""));

        /// <summary>Apply the old settings shape: transmit RNNoise on the neural slot, and the gate.</summary>
        public static void ApplySettings(TxConditioningSettings s)
        {
            if (s == null) return;
            ApplySetup(TxNoiseSetupMigration.FromConditioning("", s));
        }

        /// <summary>Back to the recommended state: RNNoise at its default floor, off; spectral off; the gate as shipped, its threshold derived from the floor again.</summary>
        public static TxChangeResult ResetToRecommended()
        {
            var recommended = RecommendedSetup();
            return Ask(Chain, lever: false, () =>
            {
                ApplySetupNow(recommended);
                var gate = Conditioner?.Gate;
                if (gate != null) gate.ThresholdDb = JJPortaudio.TxNoiseGate.DefaultThresholdDb;
                lock (_gate) _gateIntent.ThresholdDb = JJPortaudio.TxNoiseGate.DefaultThresholdDb;
                RefreshDerivedThreshold();
            }, WholeSetupEdit(recommended));
        }

        /// <summary>
        /// The recommended setup: it replaces both slots (spectral off, RNNoise
        /// at its default floor and off) and the gate, as one change; the
        /// compressor off at its defaults, and the limiter as a new setup has
        /// it (Sprint 49 Track CL); JJ Clarity off.
        /// </summary>
        private static TxNoiseSetup RecommendedSetup()
        {
            var recommended = TxNoiseSetupMigration.FromConditioning("", new TxConditioningSettings());
            recommended.Limiter = TxLimiterSetup.ForNewSetup();
            return recommended;
        }
    }


    /// <summary>
    /// WDSP's downward expander (DEXP, in the helper) as the transmit
    /// expander's other mode (Sprint 49 Track I phase 2, ruled S6). Plugged
    /// into the conditioner as its <see cref="JJPortaudio.TxAudioConditioner.ExpanderReplacement"/>:
    /// while the expander is on and this mode is in force, it runs in the
    /// expander's place on the microphone, mono, through the same holder every
    /// transmit helper engine uses (<see cref="PerChannelDenoiserBank"/> around
    /// a <see cref="WdspHelperEngine"/>), on the same host.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the bank and not a transmit slot.</b> <see cref="PcDenoiseSlot"/>
    /// is a noise reducer's holder: strength blends, the shared delay plan,
    /// seams, profiles and its menus. None of that is an expander's. The bank is
    /// the part that is generic, and it already carries the ruled failure
    /// behaviour (#737): keyed, the first late or failed frame LATCHES it to
    /// the unprocessed microphone at the same delay until the unkey. So nothing
    /// here is a second host or client: one host, one client type, one bank.</para>
    /// <para><b>Switches land at an unkeyed buffer</b> (S2): the mode, and a new
    /// bank (the first build, or a rebuild for a setting that changes the
    /// delay). The mode comes into force only once its bank is built, so the
    /// expander never goes missing while the helper starts.</para>
    /// <para><b>Its levers reach it at the unkey.</b> Every one of WDSP's
    /// setters for this stage rebuilds its curves and restarts its detector,
    /// which closes it until it re-attacks: a change mid-word would duck the
    /// word. So settings are pushed by the policy tick while unkeyed only.</para>
    /// <para><b>Unavailable is not hidden.</b> When the helper cannot run it,
    /// its bank passes the microphone through unprocessed at the same delay,
    /// and the choice is kept; the operator is told once per failure.</para>
    /// </remarks>
    internal sealed class TxWdspExpander : IDisposable
    {
        /// <summary>The helper's engine id.</summary>
        internal const string EngineId = "dexp";

        // DRAFT PROSE keys (phase 2), for Codex.
        /// <summary>Said once when WDSP's expander misses a frame mid-over and the microphone goes out without it.</summary>
        internal const string FellBackKey = "audio.expander.tx.wdsp_fell_back";

        /// <summary>The mono scratch, frames: preallocated, so the audio thread never allocates (a longer buffer is taken in pieces).</summary>
        internal const int ScratchFrames = 8192;

        /// <summary>
        /// The detector's time constant sent to DEXP, ms: WDSP's own suggested
        /// starting point. Ours is a power envelope with separate rise and fall
        /// (0.5 and 50 ms); DEXP has one averaging constant, so it is not mapped.
        /// </summary>
        internal const double DetectorMs = 10.0;

        /// <summary>
        /// How far DEXP's detector reads below ours on the same signal with its
        /// side-channel filter OFF, dB: 20 log10 (2 / pi), about -3.92. Ours sums
        /// the POWER of the duplicated stereo pair, so a sine of peak A reads
        /// 20 log10 A (the BS.1770 accounting, see <see cref="JJPortaudio.TxNoiseGate"/>);
        /// DEXP averages the MAGNITUDE of one mono channel, and a sine's average
        /// magnitude is 2A / pi. With the filter ON there is no offset: DEXP's
        /// band-pass is complex (it keeps the positive frequencies, doubled), so
        /// its detector sees the analytic signal's magnitude, A itself. Both
        /// measured in the helper's own tests (dexp_detector_scale: -23.89 and
        /// -20.02 dB for a tone of peak 0.1), and calibrated here by a test that
        /// puts one tone at the same threshold in both modes, through the real helper.
        /// </summary>
        internal static readonly double DetectorOffsetDb = 20.0 * Math.Log10(2.0 / Math.PI);

        /// <summary>
        /// How far above a steady tone's peak level our detector reads, dB,
        /// MEASURED: 2.8 (a -40 dB threshold opens for a tone of peak -42.8 dB,
        /// from 300 Hz to 2 kHz, side-chain filter on or off; pinned in
        /// <c>TxWdspExpanderTwinTests</c>). Its envelope rises in 0.5 ms and
        /// falls in 50 ms, so on a sine it rides the power's peaks (2A squared,
        /// +3 dB) rather than their mean; DEXP's ten-millisecond average does
        /// not. So the mapping moves DEXP's threshold down by this as well.
        /// </summary>
        internal const double OurToneBiasDb = 2.8;

        /// <summary>
        /// Our threshold, on our detector's scale, as DEXP's threshold setting,
        /// with its side-channel filter on or off: so one steady tone opens both
        /// at the same level. Speech is not a steady tone, and the two detectors'
        /// dynamics differ (ours rides peaks, DEXP's averages), so on a voice the
        /// two modes open within a decibel or two of each other, not exactly.
        /// </summary>
        internal static double MapThresholdDb(float ourThresholdDb, bool sideFilter) =>
            ourThresholdDb - OurToneBiasDb + (sideFilter ? 0.0 : DetectorOffsetDb);

        // The settings that move the audio in time: a change of one needs a new channel.
        private static readonly string[] LatencySettings = { "side_filter", "audio_delay" };

        /// <summary>
        /// The helper's settings for a gate setup and the threshold in force (on
        /// our scale). The JJ mode's attack, hold and release, side-chain filter
        /// and corners carry across; the depth is the WDSP mode's own; the
        /// hysteresis is ours (3 dB); WDSP's audio delay stays off, because it
        /// acts after the gain is decided and so is not look-ahead.
        /// </summary>
        internal static Dictionary<string, EngineSettingValue> ValuesFor(TxNoiseGateSetup g, float thresholdDb) => new(StringComparer.Ordinal)
        {
            ["threshold_db"] = new(Math.Round(Math.Clamp(MapThresholdDb(thresholdDb, g.SideChainFilter), -120.0, 0.0), 2)),
            ["depth_db"] = new(Math.Round(Math.Clamp(g.WdspDepthDb, ExpanderModes.MinWdspDepthDb, ExpanderModes.MaxWdspDepthDb), 1)),
            ["hysteresis_db"] = new(JJPortaudio.TxNoiseGate.CloseBelowThresholdDb),
            ["detector_ms"] = new(DetectorMs),
            ["attack_ms"] = new(Math.Round(Math.Clamp(g.AttackMs, 1f, 100f), 1)),
            ["hold_ms"] = new(Math.Round(Math.Clamp(g.HoldMs, 0f, 2000f))),
            ["decay_ms"] = new(Math.Round(Math.Clamp(g.ReleaseMs, 1f, 2000f))),
            ["side_filter"] = new(0, g.SideChainFilter ? "on" : "off"),
            ["low_cut_hz"] = new(Math.Round(Math.Clamp(g.SideChainLowCutHz, 20f, 1000f))),
            ["high_cut_hz"] = new(Math.Round(Math.Clamp(g.SideChainHighCutHz, 1500f, 12000f))),
            ["audio_delay"] = new(0, "off"),
        };

        private static string LatencyKey(IReadOnlyDictionary<string, EngineSettingValue> v) =>
            string.Join("|", LatencySettings.Select(n => v.TryGetValue(n, out var x) ? x.Choice ?? x.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));

        /// <summary>The live factory: an engine on the app's one helper host, for transmit.</summary>
        internal static IDenoiseEngine LiveEngine(IReadOnlyDictionary<string, EngineSettingValue> values) =>
            new WdspHelperEngine(WdspHost.Default, EngineId, DenoiseDirection.Transmit, values);

        private readonly Func<IReadOnlyDictionary<string, EngineSettingValue>, IDenoiseEngine> _factory;
        private readonly object _lock = new();

        // Control side, under _lock.
        private bool _wantWdsp;
        private PerChannelDenoiserBank? _newest;                 // the newest bank built (adopted or waiting)
        private string _newestKey = "";
        private Dictionary<string, EngineSettingValue> _pushed = new(StringComparer.Ordinal);
        private bool _building;
        private Dictionary<string, EngineSettingValue>? _buildAgain;
        private long _builtAtMs;

        /// <summary>How long an unavailable bank is kept before a rebuild is tried (a helper installed or repaired meanwhile).</summary>
        internal const int RetryUnavailableMs = 30000;

        // Handed from the control side to the audio side.
        private PerChannelDenoiserBank? _ready;
        private int _modeWanted;

        // Audio side.
        private PerChannelDenoiserBank? _bank;
        private bool _inForce;
        private bool _latchSeen;
        private long _lastCallMs;

        /// <summary>
        /// Whether a pause in the calls (over 100 ms) clears the bank's carry
        /// (#1169). The live stream's buffers come every 10 ms; an offline
        /// render's may not, and must never be cleared mid-take, so the twin
        /// turns this off.
        /// </summary>
        internal bool ClearOnGap { get; init; } = true;
        private readonly float[] _mono = new float[ScratchFrames];
        private int _appliedLatency;
        private int _told;

        // ── Off the audio thread (phase 2 repair 1, N1) ──
        //
        // The callback hands two things on, and allocates for neither: a bank it
        // no longer runs (an intrusive stack through the bank's own RetiredNext,
        // the slots' pattern), and a notice owed (one flag). Both are the
        // operator's events, not trace lines: the notice is spoken, and a
        // retired bank holds a helper channel until it is disposed. So they go
        // to Track RT's normal-priority events writer (OffAudioDrainer.Events),
        // which disposes the one and raises the other directly, never through a
        // pool hop, and never waits behind the below-normal diagnostics writer
        // (Codex's RT re-check 2 reproduced that one starving for about 2.6 s
        // under load). The holder writes no trace line of its own from the audio
        // side; its bank's lines stay on that bank's (diagnostics) writer.
        private PerChannelDenoiserBank? _retiredHead;
        private int _tellOwed;
        private readonly HolderWork _work;
        private readonly JJTrace.IOffAudioScheduler _scheduler;

        private sealed class HolderWork : JJTrace.OffAudioWork
        {
            private readonly TxWdspExpander _owner;
            public HolderWork(TxWdspExpander owner) => _owner = owner;
            public override bool Pending => base.Pending || Volatile.Read(ref _owner._retiredHead) != null || Volatile.Read(ref _owner._tellOwed) != 0;
            protected override void DrainCore() => _owner.DrainOffAudio();
        }

        private void DrainOffAudio()
        {
            var list = Interlocked.Exchange(ref _retiredHead, null);
            while (list != null)
            {
                var next = list.RetiredNext;
                list.RetiredNext = null;
                try { list.Dispose(); }
                catch (Exception ex) { Trace.WriteLine("TxWdspExpander: a retired bank did not close: " + ex.GetType().Name); }
                list = next;
            }
            if (Interlocked.Exchange(ref _tellOwed, 0) != 0)
            {
                try { FellBack?.Invoke(); }
                catch (Exception ex) { Trace.WriteLine("TxWdspExpander: the fallback notice failed: " + ex.GetType().Name); }
            }
        }

        /// <summary>The audio side: hand a bank to the off-audio writer to dispose. No allocation, no lock.</summary>
        private void Retire(PerChannelDenoiserBank bank)
        {
            if (!bank.ClaimRetirement()) return;
            PerChannelDenoiserBank? head;
            do { head = Volatile.Read(ref _retiredHead); bank.RetiredNext = head; }
            while (!ReferenceEquals(Interlocked.CompareExchange(ref _retiredHead, bank, head), head));
            _work.Signal();
        }

        /// <param name="factory">Makes the engine for a bank, from the helper settings; called off the audio thread.</param>
        /// <param name="scheduler">Where its off-audio work runs: the normal-priority events writer when null (it is all the operator's events); a test passes one scheduler for everything, as Track RT's do.</param>
        internal TxWdspExpander(Func<IReadOnlyDictionary<string, EngineSettingValue>, IDenoiseEngine> factory,
            JJTrace.IOffAudioScheduler? scheduler = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _work = new HolderWork(this);
            _scheduler = scheduler ?? JJTrace.OffAudioDrainer.Events;
            _scheduler.Register(_work);
        }

        /// <summary>Tests only: its off-audio work (the retirements and the notice: events).</summary>
        internal JJTrace.OffAudioWork EventWorkForTest => _work;

        /// <summary>Stop its off-audio work, after one last drain. The banks it built are their owners' to dispose.</summary>
        public void Dispose()
        {
            _work.Drain();
            _scheduler.Unregister(_work);
        }

        /// <summary>
        /// Raised once for a failure (the first latch of an over, not again until
        /// the engine has really processed again, repair 1, N2), on the
        /// normal-priority events writer's thread: the operator
        /// is told the microphone is going out without WDSP's expander.
        /// </summary>
        internal event Action? FellBack;

        /// <summary>Whether WDSP's mode is in force on the audio side now (tests and the trace).</summary>
        internal bool InForce => Volatile.Read(ref _inForce);

        /// <summary>The bank the audio side runs, or null (tests and the twin's completeness).</summary>
        internal PerChannelDenoiserBank? RunningBank => Volatile.Read(ref _bank);

        /// <summary>Whether a bank has been built for the newest settings (waiting or running).</summary>
        internal bool BankBuilt { get { lock (_lock) return _newest != null && !_building; } }

        /// <summary>This buffer's delay, samples per channel: the bank's, when it ran.</summary>
        internal int AppliedLatencySamples() => _appliedLatency;

        /// <summary>
        /// The mode as intended: WDSP's or not. Adopted at the next unkeyed
        /// buffer once its bank is built; a first choice starts the build.
        /// </summary>
        internal void WantWdsp(bool wdsp)
        {
            lock (_lock) _wantWdsp = wdsp;
            Volatile.Write(ref _modeWanted, wdsp ? 1 : 0);
        }

        /// <summary>
        /// The control side's tick: the settings a gate setup and the threshold
        /// in force (our scale) call for. A setting that moves the audio in time,
        /// or no bank yet, builds a new bank (adopted at an unkeyed buffer); the
        /// rest are sent to the running engine, unkeyed only (see remarks). Does
        /// nothing while the JJ mode is wanted.
        /// </summary>
        internal void Update(TxNoiseGateSetup gate, float thresholdDb, bool keyed)
        {
            var values = ValuesFor(gate, thresholdDb);
            lock (_lock)
            {
                if (!_wantWdsp) return;
                string key = LatencyKey(values);
                // An engine that never opened stays unavailable for good; try a new
                // one now and then, unkeyed, in case the helper has come back.
                bool retry = _newest != null && !keyed && !_newest.AnyAvailable
                    && Environment.TickCount64 - _builtAtMs > RetryUnavailableMs;
                if (_newest == null || key != _newestKey || retry)
                {
                    if (_building) { _buildAgain = values; return; }
                    _building = true;
                    var v = values;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => Build(v));
                    return;
                }
                if (_building || keyed) return;
                var engine = _newest.EngineFor(0);
                foreach (var (name, value) in values)
                {
                    if (Array.IndexOf(LatencySettings, name) >= 0) continue;
                    if (_pushed.TryGetValue(name, out var was) && was == value) continue;
                    if (engine.TrySetSetting(name, value)) _pushed[name] = value;
                }
            }
        }

        /// <summary>Build a bank now, on this thread, for these settings, and hand it to the audio side. The twin calls this; the live side through <see cref="Update"/>.</summary>
        internal PerChannelDenoiserBank? BuildNow(IReadOnlyDictionary<string, EngineSettingValue> values)
        {
            lock (_lock) _building = true;
            Build(values);
            lock (_lock) return _newest;
        }

        private void Build(IReadOnlyDictionary<string, EngineSettingValue> values)
        {
            PerChannelDenoiserBank? bank = null;
            try
            {
                var v = new Dictionary<string, EngineSettingValue>(values, StringComparer.Ordinal);
                bank = new PerChannelDenoiserBank(() => _factory(v), 1);
                Trace.WriteLine("TxWdspExpander: WDSP's expander built" + (bank.IsAvailable ? "" : ", unavailable ("
                    + bank.EngineFor(0).Unavailability + ": " + bank.EngineFor(0).UnavailabilityDetail + "); the microphone passes through it unprocessed")
                    + "; delay " + bank.LatencySamples + " samples.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxWdspExpander: WDSP's expander could not be built: " + ex.GetType().Name + ": " + ex.Message);
            }
            Dictionary<string, EngineSettingValue>? again;
            lock (_lock)
            {
                if (bank != null)
                {
                    _newest = bank;
                    _builtAtMs = Environment.TickCount64;
                    _newestKey = LatencyKey(values);
                    _pushed = new Dictionary<string, EngineSettingValue>(values, StringComparer.Ordinal);
                }
                _building = false;
                again = _buildAgain;
                _buildAgain = null;
            }
            if (bank != null)
            {
                // A bank built but never adopted is retired by the next one.
                var unadopted = Interlocked.Exchange(ref _ready, bank);
                unadopted?.Dispose();
            }
            if (again != null) lock (_lock) { if (_wantWdsp && !_building) { _building = true; System.Threading.ThreadPool.QueueUserWorkItem(_ => Build(again)); } }
        }

        /// <summary>
        /// The conditioner's <see cref="JJPortaudio.TxAudioConditioner.ExpanderModeStage"/>,
        /// on the audio thread. True when WDSP's mode ran in the expander's place.
        /// </summary>
        internal bool Process(float[] buffer, int count, uint sampleRate, bool keyed)
        {
            long now = Environment.TickCount64;
            bool gap = _lastCallMs == 0 || now - _lastCallMs > 100;
            _lastCallMs = now;
            if (!keyed)
            {
                var ready = Interlocked.Exchange(ref _ready, null);
                if (ready != null)
                {
                    var old = _bank;
                    _bank = ready;
                    if (old != null) Retire(old);
                }
                bool was = _inForce;
                Volatile.Write(ref _inForce, Volatile.Read(ref _modeWanted) != 0 && _bank != null);
                if (_bank != null)
                {
                    if (_bank.Latched) _bank.ReleaseLatch();
                    // Audio queued before a pause or a mode switch is not played after it (#1169).
                    if (_inForce && (!was || (gap && ClearOnGap))) _bank.ClearCarry();
                }
                _latchSeen = false;
            }
            var bank = _bank;
            if (!_inForce || bank == null) { _appliedLatency = 0; return false; }
            if (sampleRate != WdspProtocol.SampleRate || count < 2)
            {
                // Every engine is 48 kHz-native; at another rate the stage stands aside, honestly unprocessed.
                _appliedLatency = 0;
                return true;
            }
            bank.LatchFallback = keyed;
            long took = bank.EngineFrames, late = bank.LateFrames, failed = bank.FallbackFrames, latched = bank.LatchedFrames;
            int frames = count / 2;
            for (int at = 0; at < frames; at += _mono.Length)
            {
                int n = Math.Min(_mono.Length, frames - at);
                for (int i = 0, j = 2 * at; i < n; i++, j += 2) _mono[i] = (buffer[j] + buffer[j + 1]) * 0.5f;
                bank.Process(_mono.AsSpan(0, n), 1f);
                for (int i = 0, j = 2 * at; i < n; i++, j += 2) { buffer[j] = _mono[i]; buffer[j + 1] = _mono[i]; }
            }
            _appliedLatency = bank.LatencySamples;
            // N2: the notice is owed again only after the engine has really
            // worked (a buffer it took with nothing late, failed or latched);
            // being "available" is not enough, since a connected helper can stay late.
            if (bank.EngineFrames != took && bank.LateFrames == late && bank.FallbackFrames == failed
                && bank.LatchedFrames == latched && !bank.Latched)
                Volatile.Write(ref _told, 0);
            if (keyed && bank.Latched && !_latchSeen)
            {
                _latchSeen = true;
                if (Interlocked.Exchange(ref _told, 1) == 0) { Volatile.Write(ref _tellOwed, 1); _work.Signal(); }
            }
            return true;
        }
    }

    // TxConditioningSettings moved to Radios\MicrophoneProfile.cs
    // (Track B, 2026-08-18, #44): it is the microphone profile's payload
    // now, exactly as the note it carried here always said it should be.
}
