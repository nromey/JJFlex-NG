using System;
using System.Diagnostics;
using System.Windows.Threading;
using JJTrace;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// PTT safety state machine — manages TX hold, lock, timeout warnings, and hard kill.
    /// All state transitions require _radioPowerOn and a valid RigControl.
    /// Uses DispatcherTimer for UI-thread warning escalation.
    /// </summary>
    public class PttSafetyController
    {
        public enum PttState
        {
            Idle,
            PttHold,    // Ctrl+Space held down — TX while key is down
            Locked,     // Shift+Space — TX stays on until unlocked
            Warning1,   // 10-second beeps (30s before timeout by default)
            Warning2,   // 5-second beeps (15s before timeout by default)
            OhCrap,     // 1-second beeps (5s before timeout by default)
            HardKill    // Absolute 15-min kill, non-configurable
        }

        public PttState State { get; private set; } = PttState.Idle;

        /// <summary>
        /// This controller's transmissions, counted by RF: the owner-side
        /// identity a safety sentence about a transmission is valid under.
        /// "You are no longer on the air" is true until the operator keys
        /// again; "power is coming back" is true while THIS transmission is
        /// still on the air; an unheard copy of either must not be replayed
        /// into a state where it is false (Astra's Track IJK review, blocker 6;
        /// #617).
        ///
        /// <para><b>Fed from <see cref="SetTx"/> and the external watch, and
        /// from nowhere else</b> — not from the state transitions. The first
        /// build advanced a counter at both <c>SetTx(true)</c> sites, and one
        /// of them is a held PTT becoming a lock, where RF never stops: a
        /// reflected-power warning cut short during the hold, and still true
        /// during the lock, was withdrawn as history (Astra's Track IJK2
        /// review, blocker 1). The rule — advance on the OFF-to-ON edge only
        /// — lives in <see cref="Radios.Speech.TransmitEpisode"/>, where a test can drive
        /// it; this class only reports what it has commanded.</para>
        /// </summary>
        private readonly Radios.Speech.TransmitEpisode _transmitEpisode = new Radios.Speech.TransmitEpisode();

        /// <summary>What this controller last commanded the rig to do with RF.</summary>
        private bool _rfCommandedOn;

        private readonly Func<FlexBase?> _getRigControl;
        private readonly Func<bool> _getRadioPowerOn;
        private readonly Action<string>? _updateStatusDisplay;
        private PttConfig _config;

        /// <summary>
        /// License-aware TX lockout check. When set and returns false,
        /// PTT is blocked with a spoken warning. Set by MainWindow when
        /// FreqOutHandlers are initialized.
        /// </summary>
        public Func<bool>? CanTransmitHereCheck { get; set; }

        /// <summary>
        /// Extra line spoken at every key-down (Audio Track C). Static so it
        /// survives controller recreation on operator switch. Set by the Audio
        /// Workshop while the TX test tone is armed and cleared on disarm —
        /// an operator who keys up with the tone still replacing their
        /// microphone must hear that on EVERY transmit path, not just the
        /// workshop's own check. Spoken at Critical regardless of the
        /// operator's PTT speech setting: it is trap-warning, not chrome.
        /// Returns null/empty when there is nothing to say.
        /// </summary>
        public static Func<string?>? KeyDownAnnouncementExtra { get; set; }
        private const string KeyDownExtraSubject = "ptt-key-down-extra";
        /// <summary>The Audio Check supplies its own short keying narration.</summary>
        public bool AudioCheckNarration { get; set; }

        /// <summary>
        /// The first thing a key-down that starts a transmission says. When the
        /// press took a box send over (#835; Noel, 2026-10-28 16:55), that news
        /// leads the line, "cq stopped. Transmitting.", as ONE utterance: it is
        /// the only one spoken before a held key's auto-repeat starts cutting
        /// speech, and a second sentence was heard only after the operator
        /// let go. The takeover part is spoken even with push to talk's own
        /// speech off, because it is about the send, not about keying.
        /// </summary>
        private void SpeakKeyDownFirstLine(string keyingLine)
        {
            string? takeover = null;
            try { takeover = VoiceMessageSender.TakeOverAtKeyDown(); }
            catch (Exception ex)
            {
                // Never let a send's bookkeeping break keying; the send's own
                // tick still takes it over a moment later.
                Tracing.TraceLine("PTT: send takeover at key-down failed: " + ex.Message, TraceLevel.Warning);
            }
            bool speakKeying = _config.SpeechEnabled && !AudioCheckNarration;
            if (string.IsNullOrEmpty(takeover))
            {
                if (speakKeying)
                    ScreenReaderOutput.Speak(keyingLine, VerbosityLevel.Critical, interrupt: true);
                return;
            }
            ScreenReaderOutput.Speak(speakKeying ? takeover + " " + keyingLine + "." : takeover,
                Radios.Speech.SpeechIntent.Interrupt, VerbosityLevel.Critical,
                subject: Radios.Speech.SpeechSubject.MessageKey);
        }

        private static void SpeakKeyDownExtra()
        {
            string? extra = null;
            try { extra = KeyDownAnnouncementExtra?.Invoke(); }
            catch { /* never let an announcement hook break keying */ }
            if (!string.IsNullOrEmpty(extra))
                ScreenReaderOutput.Speak(extra, Radios.Speech.SpeechIntent.Queue,
                    VerbosityLevel.Critical, subject: KeyDownExtraSubject);
        }

        /// <summary>
        /// Legacy session-scoped soft-timeout override. Audio Check now owns
        /// its recorder limit and suppresses this warning ladder during checks.
        /// Ordinary transmissions retain the configured warning and hard kill.
        /// </summary>
        public int? SessionTimeoutOverrideSeconds { get; set; }

        /// <summary>
        /// A planned stop that owns this transmission's cut (the Record and
        /// play back check's recorder limit). The warning ladder stands down
        /// ONLY while the planned stop comes before the operator's own
        /// timeout; when the operator's timeout is the shorter leash, it runs
        /// exactly as for any other transmission. Null for every other
        /// transmission, Monitor and off-air checks included.
        /// </summary>
        public int? PlannedStopSeconds { get; set; }

        private double EffectiveTimeoutSeconds =>
            SessionTimeoutOverrideSeconds is int o
                ? Math.Min(o, _config.TimeoutSeconds)
                : _config.TimeoutSeconds;

        // Timers
        private DispatcherTimer? _warningTimer;
        private DispatcherTimer? _beepTimer;
        private DispatcherTimer? _hardKillTimer;
        private DispatcherTimer? _alcTimer;

        // The lock's timeout clock: time held by hand, which stands still while
        // a recording's send rides the lock (#835; Noel, 2026-10-30 00:00).
        private readonly Radios.LockTimeoutClock _lockClock = new();
        private string _rideWhat = "";
        private Func<bool>? _rideStillGoing;

        // ALC zero-signal tracking
        private int _alcZeroConsecutiveSeconds;

        // TX health monitor — warn once per TX session.
        // 2026-08-11 rewrite: judge transmit audio by SC_MIC (universal across
        // the analog mic AND PC audio — see FlexBase) using a peak-hold over the
        // window so speech pauses don't read as silence, and by SW ALC for drive
        // (not HWALC, the external-amp jack the old code used). Thresholds in
        // dBFS; tunable. Silent: even the loudest moment in the window never rose
        // above the floor. Hot: SW ALC pegging near 0 dBFS.
        private const float SilentMicDbfs = -45f;
        private const float AlcHotDbfs = -0.3f;

        // "No transmit signal this second" for the dead-carrier auto-release —
        // SW ALC below this means essentially no modulation. (Old code read
        // HWALC, always ~0, so it treated EVERY transmission as dead and would
        // auto-unkey any lock held past AlcAutoReleaseSeconds.)
        private const float NoSignalDbfs = -50f;
        private bool _healthSilentMicWarned;
        private bool _healthAlcHighWarned;
        private bool _healthReflectedWarned;

        /// <summary>
        /// This transmission's reflected-power state (#453): the forward peak
        /// that sets the floor below which a reflected share means nothing, and
        /// the run of bad judgeable samples the warning needs behind it. Reset
        /// alongside <see cref="_healthReflectedWarned"/>.
        /// </summary>
        private readonly ReflectedPowerRun _reflectedRun = new ReflectedPowerRun();

        /// <summary>
        /// True once THIS transmission has been shown to be carrying audio
        /// (#459) — the peak-hold rose above the floor sentinel, so something
        /// arrived. Latching the SUCCESS is the whole shape of the fix: the old
        /// code latched the FAILURE at five seconds, and because the peak-hold
        /// only ever grows, a verdict of "silent" could be contradicted by the
        /// meter before the sentence finished being spoken.
        /// </summary>
        private bool _healthMicVerified;

        /// <summary>
        /// True once the level advice has been given, so it is said at most
        /// once per transmission.
        /// </summary>
        private bool _healthMicLevelAdvised;

        /// <summary>
        /// True once this transmission has been traced as unjudgeable — the
        /// SC_MIC meter delivered no sample at all — so the trace line is
        /// written once, not every second (#502).
        /// </summary>
        private bool _healthMicTelemetryMissingTraced;

        /// <summary>
        /// Seconds transmitting in ANY state, unlike <c>_healthLockSeconds</c>
        /// which counts only a locked transmission. The reflected-power warning
        /// runs on this one because a held PTT into a dead antenna port is
        /// exactly as bad for the finals as a locked one.
        /// </summary>
        private int _healthTxSeconds;
        private int _healthLockSeconds;

        /// <summary>
        /// The dispatcher every timer above belongs to, captured where they are
        /// created. <see cref="KillTransmitNow"/> may be called from a thread
        /// that is not this one, and <c>DispatcherTimer.Stop</c> is thread-
        /// affine — but the RF has to come off before anything is marshalled
        /// anywhere, so the two halves are deliberately separated.
        /// </summary>
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        public PttSafetyController(
            Func<FlexBase?> getRigControl,
            Func<bool> getRadioPowerOn,
            PttConfig config,
            Action<string>? updateStatusDisplay = null)
        {
            _getRigControl = getRigControl;
            _getRadioPowerOn = getRadioPowerOn;
            _config = config;
            _updateStatusDisplay = updateStatusDisplay;
            EnsureKillWiring();
            PublishCutSetting();
            PublishHoldGate();
        }

        /// <summary>
        /// Update config (e.g. after operator switch).
        /// </summary>
        public void UpdateConfig(PttConfig config)
        {
            _config = config;
            PublishCutSetting();
        }

        /// <summary>
        /// Give the kill switch the host-side things it cannot reach from
        /// Radios: the alarm earcon. Idempotent, and called from anywhere that
        /// is about to key outside this controller — the transmit checks open
        /// their dialog whether or not a controller was ever built, and a kill
        /// that cannot make a sound is half a kill.
        /// </summary>
        public static void EnsureKillWiring()
        {
            TransmitKillSwitch.Alarm = EarconPlayer.HardKillTone;
        }

        /// <summary>
        /// The operator's reflected-power cutoff, published to the kill switch
        /// so the checks' live watch reads the SAME setting this controller
        /// does. Never defaulted true by anyone: an app that unilaterally
        /// unkeys a transmitter has taken the station away mid-transmission.
        /// </summary>
        private void PublishCutSetting()
        {
            TransmitKillSwitch.CutOnReflectedAlarm = () => _config.CutTransmitOnReflectedAlarm;
        }

        /// <summary>
        /// Whether the controller is in any transmitting state.
        /// </summary>
        public bool IsTransmitting => State != PttState.Idle;

        // -------------------------------------------------------------------
        // The hold after a reflected-power cut (#571)
        // -------------------------------------------------------------------
        //
        // For ReflectedCutHold.HoldSeconds after the cut, a press that would
        // key the transmitter does not key it, and instead repeats why the
        // transmission was stopped; a change to the transmit setup lifts the
        // hold at once. The rulings, with Noel's words, are on the class.
        //
        // The hold lives in Radios so it can be driven from a test with no
        // dispatcher; this controller owns it, reads the rig for it, and is the
        // one place its refusal is consulted from inside the controller. The
        // doors OUTSIDE the controller — a voice message, a transmit check —
        // consult it through RefuseIfHeldAfterCut and the kill switch's hook.

        private readonly ReflectedCutHold _cutHold = new ReflectedCutHold(() => Environment.TickCount64);
        private readonly object _cutHoldWiring = new object();
        private DispatcherTimer? _cutHoldTimer;
        private FlexBase? _cutHoldRig;
        private int _cutHoldWiredGeneration;

        /// <summary>The hold after a reflected-power cut, for the doors outside this controller and for tests.</summary>
        public ReflectedCutHold CutHold => _cutHold;

        /// <summary>
        /// A press that would key the transmitter has arrived at
        /// <paramref name="door"/>. When the hold after a reflected-power cut
        /// is in force and the transmit setup is still as it was at the cut,
        /// speaks the refusal (the cut's own delivery: alarm tone, Urgent,
        /// Critical, so it is heard at Off) and returns true; the caller must
        /// not key. The setup is re-read first, so a change the operator made a
        /// moment ago lifts the hold and the press keys as normal. Safe from
        /// any thread: the Fixer's and the command loop's doors call it.
        /// </summary>
        public bool RefuseIfHeldAfterCut(string door)
        {
            if (!_cutHold.Holding) return false;
            long now = Environment.TickCount64;
            FlexBase? rig = null;
            try { rig = _getRigControl(); } catch { }
            if (rig != null && _cutHold.Tick(ReflectedCutHold.Snapshot(rig), now)) return false;
            return _cutHold.RefusePress(door, now, _transmitEpisode.NoTransmissionSince());
        }

        /// <summary>
        /// Publish the hold to the doors outside this class, the way
        /// <see cref="PublishCutSetting"/> publishes the cut setting: the kill
        /// switch's carrier gate and its reflected-power cut (a check's cut
        /// starts the same hold, Astra B1), and the rig's CW keying (B5). The
        /// newest controller wins, as there.
        /// </summary>
        private void PublishHoldGate()
        {
            TransmitKillSwitch.TransmitHeld = door => RefuseIfHeldAfterCut(door) ? _cutHold.LastRefusalSentence : null;
            TransmitKillSwitch.ReflectedCutHappened = BeginCutHoldFromKillSwitch;
            // The kill switch speaks its cut sentence AFTER the hold begins, so
            // the hold's VOX note waits for this rather than being flushed by
            // that Urgent sentence (Opus N4).
            TransmitKillSwitch.ReflectedCutSpoken = _cutHold.SpeakPendingVoxNote;
            FlexBase.RefuseIfTransmitHeld = RefuseIfHeldAfterCut;
            FlexBase.IsTransmitHeld = () => _cutHold.Holding;
        }

        /// <summary>
        /// The kill switch's reflected-power cut ended a transmit check (B1):
        /// the same hold, from what the watcher knew. Arrives on the watcher's
        /// or the Fixer's thread; the hold is in force before this returns,
        /// and only the timer is posted to the dispatcher.
        /// </summary>
        private void BeginCutHoldFromKillSwitch(ReflectedCutNotice notice)
        {
            FlexBase? rig = null;
            try { rig = _getRigControl(); } catch { }
            if (rig == null)
            {
                Tracing.TraceLine("PTT: a transmit check's reflected-power cut arrived with no rig to hold (" + notice + ")", TraceLevel.Warning);
                return;
            }
            BeginCutHold(rig, notice, voxNoteNow: false);   // the cut sentence is still to come (N4)
        }

        /// <summary>
        /// The controller's own cut has just ended the transmission (GoIdle has
        /// spoken): start the hold. Internal so
        /// <c>JJFlexWpf.Tests.LockSafetyHoldTests</c> can start a hold on the
        /// real controller without a meter stream; the one production caller
        /// is the cut in <see cref="CheckReflectedPower"/>, which a source pin
        /// holds to.
        /// </summary>
        internal void BeginCutHold(FlexBase rig, TransmitSafety.ReflectedCut cut, in TransmitPowerReading reading, string antenna)
            => BeginCutHold(rig, new ReflectedCutNotice(cut, reading, antenna));

        /// <summary>
        /// Start the hold, pause VOX if it was on, and watch the rig for a
        /// change. Callable from any thread: the hold itself is locked and
        /// generation-stamped, this class's wiring is under its own lock, and
        /// the one dispatcher-bound piece (the poll timer) is posted.
        /// </summary>
        internal void BeginCutHold(FlexBase rig, in ReflectedCutNotice notice, bool voxNoteNow = true)
        {
            long now = Environment.TickCount64;
            lock (_cutHoldWiring)
            {
                // A second cut while a hold is in force: end the first hold
                // HERE, before the new wiring goes on. Its Ended event carries
                // its own generation, so the handler below tears down only what
                // belongs to it.
                _cutHold.Abandon("the controller is starting the next cut's hold", now);
                EndCutHoldWiringLocked();

                _cutHoldRig = rig;
                _cutHold.Ended += OnCutHoldEnded;
                rig.OperatorChangedRadioSetting += OnOperatorChangedDuringCutHold;
                rig.AppInitiatedRadioSettingWritten += OnAppWroteDuringCutHold;
                rig.ModeChanged += OnModeChangedDuringCutHold;

                // The levers write the radio's own setting as an APP-INITIATED
                // change, so NoteOperatorChangedRadioSetting does not fire (the
                // hold would lift itself) and the disconnect save offer is not
                // armed. Which setting was switched off is the hold's to
                // remember (B5).
                _cutHoldWiredGeneration = _cutHold.Begin(notice, ReflectedCutHold.Snapshot(rig), now,
                    ReflectedCutHold.LeversFor(rig, () => EarconPlayer.ToggleTone(false), EarconPlayer.CutHoldEndedTone),
                    refusalTone: EarconPlayer.WarningAlarmTone,
                    voxNoteNow: voxNoteNow);

                if (!_cutHold.Holding) { EndCutHoldWiringLocked(); return; }   // ended inside Begin; nothing to watch
            }

            if (_dispatcher.CheckAccess()) StartCutHoldTimer();
            else _dispatcher.BeginInvoke(new Action(StartCutHoldTimer));
        }

        /// <summary>The poll timer, created on the dispatcher it belongs to.</summary>
        private void StartCutHoldTimer()
        {
            lock (_cutHoldWiring)
            {
                if (!_cutHold.Holding || _cutHoldRig == null) return;
                StopCutHoldTimerLocked();
                _cutHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReflectedCutHold.SignalPollMs) };
                _cutHoldTimer.Tick += CutHoldTimerTick;
                _cutHoldTimer.Start();
            }
        }

        private void CutHoldTimerTick(object? sender, EventArgs e)
        {
            FlexBase? rig = _cutHoldRig;
            if (rig == null || !_cutHold.Holding) { lock (_cutHoldWiring) EndCutHoldWiringLocked(); return; }
            _cutHold.Tick(ReflectedCutHold.Snapshot(rig), Environment.TickCount64);
        }

        // The hold is thread-safe, so the rig's events go straight to it on
        // whatever thread raised them; nothing here needs the dispatcher.
        private void OnOperatorChangedDuringCutHold(string what) => _cutHold.OperatorChanged(what, Environment.TickCount64);
        private void OnAppWroteDuringCutHold(string what, string value) => _cutHold.AppWrote(what, value);

        /// <summary>
        /// The rig's mode event fires for the ACTIVE slice, which is usually
        /// but not always the TX slice, so the TX slice's mode is re-read
        /// rather than taken from the event (Opus N6). The 250 ms poll remains
        /// the backstop for a TX slice that is not the active one.
        /// </summary>
        private void OnModeChangedDuringCutHold(string mode)
        {
            FlexBase? rig = _cutHoldRig;
            if (rig == null) return;
            string txMode;
            try { txMode = rig.TXModeAsReported ?? ""; } catch { return; }
            _cutHold.NoteMode(txMode, Environment.TickCount64);
        }

        /// <summary>
        /// A hold ended. Only the hold this class is wired to may tear the
        /// wiring down: an older hold's end arriving late, on any thread,
        /// leaves a newer hold's timer and subscriptions alone.
        /// </summary>
        private void OnCutHoldEnded(int generation, ReflectedCutHold.EndReason reason, string detail)
        {
            lock (_cutHoldWiring)
            {
                if (generation != _cutHoldWiredGeneration) return;
                EndCutHoldWiringLocked();
            }
        }

        /// <summary>
        /// The rig is about to be torn down on a path the app controls: the
        /// normal disconnect (<c>CloseTheRadio</c> through
        /// <c>MainWindow.UnwireRadioEvents</c>), the application exit, a
        /// power-off. Ends the hold and puts VOX back NOW, with an immediate
        /// write rather than a queued one, because the command loop is about
        /// to stop and a queued write might never run (Astra B3). Idempotent.
        /// </summary>
        public void ReleaseRigForTeardown(string why)
        {
            _cutHold.Abandon(why, Environment.TickCount64, immediate: true);
            lock (_cutHoldWiring) EndCutHoldWiringLocked();
        }

        /// <summary>Stop watching the rig for the hold. Idempotent; the hold's own state is the hold's.</summary>
        private void EndCutHoldWiringLocked()
        {
            StopCutHoldTimerLocked();
            _cutHold.Ended -= OnCutHoldEnded;
            FlexBase? rig = _cutHoldRig;
            _cutHoldRig = null;
            if (rig != null)
            {
                rig.OperatorChangedRadioSetting -= OnOperatorChangedDuringCutHold;
                rig.AppInitiatedRadioSettingWritten -= OnAppWroteDuringCutHold;
                rig.ModeChanged -= OnModeChangedDuringCutHold;
            }
        }

        /// <summary>DispatcherTimer.Stop is thread-affine; stop it where it lives.</summary>
        private void StopCutHoldTimerLocked()
        {
            DispatcherTimer? t = _cutHoldTimer;
            _cutHoldTimer = null;
            if (t == null) return;
            if (_dispatcher.CheckAccess()) t.Stop();
            else _dispatcher.BeginInvoke(new Action(t.Stop));
        }

        // -------------------------------------------------------------------
        // Every allowed RF source tells the episode (Astra B6)
        // -------------------------------------------------------------------
        //
        // The off-air sentence's validity answer ("no transmission since") is
        // only as good as the episode's knowledge of RF. SetTx and the external
        // watch were its only inputs; the tune carrier, the ATU cycle and a
        // transmission the radio keyed on its own (VOX, a hand mic) were not,
        // so an unheard cut sentence stayed replayable through a legitimate
        // tune. These three say what the radio and the tune controls are doing;
        // NoteRf still decides the edge. The tune carrier stays unheld and keys
        // exactly as before: this is plumbing only.

        private bool _radioReportsTransmit;
        private bool _tuneCarrierOn;
        private bool _atuTuning;

        /// <summary>
        /// The radio's own transmit status changed (MainWindow's
        /// TransmitChangeHandler). "Off" is the radio's word that RF has
        /// stopped, so it also clears the tune flags below: a carrier the
        /// radio's own timeout or another client dropped must not leave a flag
        /// standing that would hide the next off-to-on edge.
        /// </summary>
        public void NoteRadioTransmit(bool on)
        {
            _radioReportsTransmit = on;
            // The radio says it keyed: a VOX send's rising tri-tone, owed to
            // this edge, plays now (#863, ruled 2026-11-04). Its "off" is NOT
            // paid here: this notification follows FlexLib's Mox, which falls
            // the moment our own software key is withdrawn while a hand mic or
            // foot switch still holds the carrier (Codex's second re-check,
            // B1). The falling tone waits for real receive instead.
            if (on) RadioKeyedForOwedStartTone();
            if (!on) { _tuneCarrierOn = false; _atuTuning = false; }
            NoteRf();
        }

        /// <summary>The operator's tune carrier (Ctrl+Shift+T, the Transmit menu, the Tune button) went up or down.</summary>
        public void NoteTuneCarrier(bool on) { _tuneCarrierOn = on; NoteRf(); }

        /// <summary>The radio's ATU cycle started or ended, from the ATU status.</summary>
        public void NoteAtuTuning(bool on) { _atuTuning = on; NoteRf(); }

        // -------------------------------------------------------------------
        // External transmit watch (#236)
        // -------------------------------------------------------------------
        //
        // The transmit checks key the radio through their own gate, outside
        // this controller — which meant NO live reflected-power monitoring
        // while their carrier was up: the health tick is started only by this
        // controller's own key-down paths and stops itself at Idle. Verified
        // by reading before acting, as the audit asked. This is the middle
        // option from that audit: the checks keep their own gate for STARTING
        // a transmit, and additionally arm this controller's health
        // monitoring for the duration.
        //
        // ── WHAT THIS ACTUALLY DELIVERS, MEASURED BY READING (Track Q, #236) ──
        //
        // Less than it reads as, and the reason is the thread. StartAlcTimer
        // creates a DispatcherTimer, which ticks only while the WPF dispatcher
        // is pumping. The three transmit-check stages that key run
        // SYNCHRONOUSLY ON THE UI THREAD by design (FixerDialog.RunStage), for
        // between two and roughly twenty-five seconds each. For that entire
        // window the dispatcher is blocked, so this timer's first tick is
        // queued until after the carrier is already down — and the live
        // reflected-power watch it was wired for cannot fire during the one
        // transmission it was wired for.
        //
        // The watch is KEPT rather than removed: it is correct for any external
        // transmit that does not block the dispatcher, it costs nothing, and
        // removing a guard on the strength of today's call graph is how the
        // next one gets missed. What actually watches a check's carrier is
        // Radios.TransmitKillSwitch, which runs the SAME TransmitSafety rules
        // on a thread of its own — one home for the threshold, two schedulers,
        // and only one of them can run when it matters.

        private int _externalWatchers;

        /// <summary>True while an external transmit (a transmit-check probe)
        /// has asked to be watched.</summary>
        public bool ExternalTransmitWatch => _externalWatchers > 0;

        /// <summary>
        /// An external transmit has keyed: run the live reflected-power check
        /// over it. Counted, so overlapping callers cannot strip each other's
        /// watch; safe to call from any state.
        /// </summary>
        public void BeginExternalTransmitWatch()
        {
            _externalWatchers++;
            NoteRf();   // an external transmit is a transmission: its warnings are about it
            if (State == PttState.Idle)
            {
                _healthReflectedWarned = false;
                _reflectedRun.Reset();
                _healthTxSeconds = 0;
                StartAlcTimer();
            }
            Tracing.TraceLine("PTT: external transmit watch on (" + _externalWatchers + ")",
                              TraceLevel.Info);
        }

        /// <summary>
        /// The external transmit is down. Safe to call unmatched — an unkey
        /// notice is the one thing that must never be conditional.
        /// </summary>
        public void EndExternalTransmitWatch()
        {
            if (_externalWatchers > 0) _externalWatchers--;
            NoteRf();
            Tracing.TraceLine("PTT: external transmit watch off (" + _externalWatchers + ")",
                              TraceLevel.Info);
            // The tick stops itself on its next pass once Idle and unwatched.
        }

        // -------------------------------------------------------------------
        // The hard kill, from anywhere (#236)
        // -------------------------------------------------------------------

        /// <summary>
        /// Stop transmitting NOW, whatever is transmitting and whichever thread
        /// is asking.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Everything else in this class is dispatcher-bound, and that is
        /// the gap this closes.</b> The warning ladder, the fifteen-minute hard
        /// kill, the ALC auto-release and the reflected-power cut are all
        /// <c>DispatcherTimer</c> ticks, so none of them can fire while
        /// something is blocking the UI thread — which is exactly what the
        /// transmit checks do for the whole time their carrier is up. So does
        /// every route an operator has: the Fixer page's Stop button and its
        /// Escape arrive as WebView2 messages, and <c>OnPreviewKeyDown</c> is a
        /// WPF event. All three queue behind the blocked stage.
        /// </para>
        /// <para>
        /// So the order here is not stylistic. The carrier comes off on the
        /// CALLING thread first, both carriers, each attempted on its own.
        /// Only the bookkeeping — state, timers, the status line — is marshalled
        /// back, and only when this controller actually owned the transmission.
        /// </para>
        /// <para>
        /// An armed transmit check has its own confirmed announcement, so this
        /// hands the kill to <see cref="TransmitKillSwitch"/> rather than
        /// speaking over it. One kill, one voice.
        /// </para>
        /// </remarks>
        /// <param name="source">Who asked, for the trace.</param>
        public void KillTransmitNow(string source) => KillTransmitNow(source, null, null);

        /// <summary>
        /// The same kill, with the sentence the operator hears chosen by the
        /// caller. The default sentence says a CHECK was ended, which is the
        /// truth for the transmit checks and a lie for the system-wide push
        /// to talk's watchdog (#307), whose operator pressed nothing of the
        /// kind — so that caller names its own outcome and its own
        /// <c>SpeechSubject</c>, and the default stays what it was for
        /// everybody else.
        /// </summary>
        /// <param name="source">Who asked, for the trace.</param>
        /// <param name="spokenOutcome">What to say once the carrier is off, or null for the default.</param>
        /// <param name="subject">The sentence's <c>SpeechSubject</c>, or null to declare none.</param>
        public void KillTransmitNow(string source, string? spokenOutcome, string? subject)
        {
            _pendingKillOutcome = spokenOutcome;
            _pendingKillSubject = subject;
            bool owned = State != PttState.Idle;
            FlexBase? rig = null;
            try { rig = _getRigControl(); } catch { }

            Tracing.TraceLine("PTT: HARD KILL requested by " + source
                              + " (controller state " + State + ")", TraceLevel.Warning);

            // 1. RF off, here, now. Independently, so a throw on one carrier
            //    cannot skip the other.
            TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Mox);
            TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Tune);

            // 1b. Nothing left armed to key it again (#699). An engaged voice
            //     file or test tone replaces the microphone, and a deliberate
            //     VOX send reaches the radio while receiving — so a kill that
            //     dropped the carrier and left the source engaged either
            //     parked the operator's microphone or handed VOX the thing it
            //     keys on. Same independence as the two drops above.
            TransmitKillSwitch.DisarmSources(rig);

            // 2. If a transmit check is armed, it owns the confirmation and the
            //    words — it is the thing that knows whether a carrier was ever
            //    raised. A no-op when nothing is armed.
            TransmitKillSwitch.Request(TransmitKillSwitch.Source.HostRequest);

            if (!owned) return;

            // 3. Our own bookkeeping, on the thread that owns the timers.
            if (_dispatcher.CheckAccess()) FinishOwnedKill();
            else _dispatcher.BeginInvoke(new Action(FinishOwnedKill));
        }

        /// <summary>
        /// The first step of <see cref="KillTransmitNow"/> and nothing else:
        /// both carriers off, each independently, with no confirmation, no
        /// speech and no bookkeeping. For a caller that must not wait — the
        /// keyboard hook thread, where every keystroke on the machine waits
        /// behind whatever it does (#402) — and for process exit, where a
        /// posted release is lost in a dispatcher that has shut down (#698).
        /// </summary>
        /// <remarks>
        /// Non-blocking by construction: <see cref="FlexBase.Transmit"/> and
        /// <see cref="FlexBase.TxTune"/> enqueue to the radio's command queue.
        /// <see cref="KillTransmitNow"/> is NOT safe from such a caller even
        /// though its remarks say it may run on another thread — that is
        /// thread affinity, not non-blocking: through
        /// <see cref="TransmitKillSwitch.Request"/> it can spend
        /// <see cref="TransmitKillSwitch.ConfirmDownMs"/> in a sleep loop
        /// when a transmit check is armed. A caller that can wait should call
        /// the full kill; this is the one that cannot.
        /// </remarks>
        /// <param name="source">Who asked, for the trace.</param>
        public void DropCarrierNow(string source)
        {
            FlexBase? rig = null;
            try { rig = _getRigControl(); } catch { }
            Tracing.TraceLine("PTT: carrier dropped without waiting, by " + source
                              + " (controller state " + State + ")", TraceLevel.Warning);
            TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Mox);
            TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Tune);
        }

        /// <summary>
        /// The sentence and subject the next <see cref="FinishOwnedKill"/>
        /// speaks, set by the three-argument kill and consumed once. Written
        /// on the killing thread and read on the dispatcher after a post;
        /// a second kill before the first finishes simply wins, which is the
        /// right answer for two sentences about one carrier.
        /// </summary>
        private string? _pendingKillOutcome;
        private string? _pendingKillSubject;

        private void FinishOwnedKill()
        {
            if (State == PttState.Idle) return;
            EarconPlayer.HardKillTone();
            string outcome = _pendingKillOutcome ?? Lexicon.Get("audio.ptt.kill_stopped");
            string? subject = _pendingKillSubject;
            _pendingKillOutcome = null;
            _pendingKillSubject = null;
            GoIdle(outcome, forceSpeech: true, subject: subject);
        }

        /// <summary>
        /// Returns a spoken PTT status string for the Speak Status hotkey.
        /// Includes mode (hold/locked) and time remaining when locked.
        /// Returns null when idle (caller uses radio-level TX status instead).
        /// </summary>
        public string? GetSpokenStatus()
        {
            if (State == PttState.Idle)
                return null;

            if (State == PttState.PttHold)
                return Lexicon.Get("audio.ptt.status_hold");

            // Locked or warning states — calculate time remaining
            var elapsed = _lockClock.ElapsedSeconds(Environment.TickCount64);
            var remaining = Math.Max(0, EffectiveTimeoutSeconds - elapsed);

            string timeLeft;
            if (remaining >= 120)
            {
                int minutes = (int)(remaining / 60);
                int seconds = (int)(remaining % 60);
                timeLeft = seconds > 0
                    ? Lexicon.Get("audio.ptt.remaining_minutes_seconds", ("minutes", minutes), ("seconds", seconds))
                    : Lexicon.Get("audio.ptt.remaining_minutes", ("minutes", minutes));
            }
            else if (remaining >= 60)
            {
                int seconds = (int)(remaining % 60);
                timeLeft = seconds > 0
                    ? Lexicon.Get("audio.ptt.remaining_one_minute_seconds", ("seconds", seconds))
                    : Lexicon.Get("audio.ptt.remaining_one_minute");
            }
            else
            {
                timeLeft = Lexicon.Get("audio.ptt.remaining_seconds", ("seconds", (int)remaining));
            }

            return Lexicon.Get("audio.ptt.status_locked", ("timeLeft", timeLeft));
        }

        private bool CanTransmit()
        {
            return _getRadioPowerOn() && _getRigControl() != null;
        }

        /// <summary>
        /// The radio is being torn down and is putting an Audio Check's changes
        /// back (#835, fix round 3): nothing new keys until that has finished.
        /// Only ever refuses a press that would START a transmission; an unkey
        /// is never refused. Said, not silent.
        /// </summary>
        private bool RefusedForTeardown()
        {
            if (_getRigControl()?.TeardownInProgress != true) return false;
            // DRAFT PROSE (#835): awaiting Noel.
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.teardown_refuses"), VerbosityLevel.Critical, interrupt: true);
            return true;
        }

        /// <summary>Admission for a recording joining an existing transmission; never requests keying.</summary>
        public bool AllowsRecorderPlayback(FlexBase rig) => ReferenceEquals(_getRigControl(), rig) &&
            _getRigControl()?.TeardownInProgress != true &&
            CanTransmit() && !RefuseIfHeldAfterCut("slice playback") &&
            (CanTransmitHereCheck?.Invoke() ?? true);

        // ── Detecting a screen reader that does not deliver held keys (#216) ──
        //
        // JAWS synthesises key-down/key-UP pairs rather than passing a held key
        // through. Measured on Noel's machine 2026-08-24 with Freight Fate's
        // key probe: the pairs arrive roughly 250 ms apart, and the up follows
        // its down almost immediately. NVDA passes an unscripted key straight
        // through, so a hold really is a hold there.
        //
        // If Ctrl+Space is treated that way, a held PTT keys and unkeys about
        // four times a second and the operator — who is holding the key and
        // talking — has no way to know. That is the shape of fault this whole
        // arc has been about: a plausible-looking state rather than an error.
        //
        // DIVISION OF LABOUR, since Sprint 35 Track E: Radios.PttHoldFilter
        // now sits UPSTREAM of this controller (MainWindow's key handlers) and
        // absorbs synthetic pairs before PttUp is ever called — evidence-gated,
        // so a reader that delivers real holds sees no change at all. That
        // filter catches the implausible releases itself, which means THIS
        // detector should now stay at zero forever: it has become the
        // backstop. If it ever fires, a synthetic release got PAST the filter,
        // and that is worth knowing loudly — so it stays, unchanged, as a
        // positive control on the absorber. (Whether JAWS synthesises for the
        // Ctrl+Space chord at all is still unverified; Noel runs JAWS himself
        // when a bug is JAWS-specific, and the filter arms only on evidence,
        // so an unverified machine keeps today's behaviour exactly.)
        private long _pttDownTicks;
        private int _implausibleReleases;

        /// <summary>
        /// Shorter than any human release. A deliberate tap of Ctrl+Space runs
        /// 80 ms or more; a synthetic pair is near zero. Nothing in between is
        /// ambiguous enough to matter.
        /// </summary>
        private const int ImplausibleReleaseMs = 50;

        /// <summary>
        /// How many key-ups arrived too fast to be a human letting go. Zero on
        /// NVDA. Non-zero means the screen reader is synthesising, and every
        /// hold-shaped binding in the app is suspect — not just this one.
        /// </summary>
        public int ImplausiblePttReleases => _implausibleReleases;

        private void NotePttRelease()
        {
            if (_pttDownTicks == 0) return;
            long ms = (Stopwatch.GetTimestamp() - _pttDownTicks) * 1000 / Stopwatch.Frequency;
            _pttDownTicks = 0;
            if (ms >= ImplausibleReleaseMs) return;

            _implausibleReleases++;
            // First occurrence only. If this is real it happens four times a
            // second, and a line per occurrence is the trace flood that has
            // already cost this project two debugging sessions.
            if (_implausibleReleases == 1)
            {
                Tracing.TraceLine("PTT: key-up arrived " + ms + " ms after key-down — too fast to be"
                    + " a human letting go. The screen reader is almost certainly synthesising"
                    + " key-down/key-up pairs rather than passing a held key through (JAWS does"
                    + " this; NVDA does not). Transmit is keying and unkeying while the operator"
                    + " holds the key. Further occurrences counted, not logged.",
                    TraceLevel.Error);
            }
        }

        /// <summary>
        /// The ONE place this controller commands RF. The transmit episode is
        /// told here and only here (plus the external watch), so a transition
        /// that keeps RF on — a held PTT becoming a lock — is the same
        /// transmission by construction, and a future state that calls this
        /// cannot start a new episode by accident.
        /// </summary>
        private void SetTx(bool on)
        {
            _rfCommandedOn = on;
            NoteRf();
            var rig = _getRigControl();
            if (rig == null) return;
            // A release at teardown or at a dropped connection unkeys only
            // this client's own carrier, checked as the write goes out (#596
            // N1, #861 B2; Codex's re-check). Every other unkey stays the
            // unconditional one.
            if (!on && _unkeyOwnCarrierOnly) rig.UnkeyOwnCarrier("push to talk released at " + _releaseWhy);
            else rig.Transmit = on;
        }

        /// <summary>While set, <see cref="SetTx"/>'s unkey is <see cref="FlexBase.UnkeyOwnCarrier"/>.</summary>
        private bool _unkeyOwnCarrierOnly;
        private string _releaseWhy = "";

        /// <summary>
        /// The application is tearing the radio down (a disconnect, a radio
        /// switch, exit, power-off): to Idle from any state, which drops this
        /// controller's command to transmit and stops its timers, with an
        /// unkey that touches only this client's own carrier (#596 N1, #861
        /// B2). Escape's ordinary unlock is unchanged.
        /// </summary>
        public void ReleaseForTeardown()
        {
            ForgetEdgeTones();
            if (State == PttState.Idle) return;
            _unkeyOwnCarrierOnly = true;
            _releaseWhy = "teardown";
            try { GoIdle(ReceivingWords()); }
            finally { _unkeyOwnCarrierOnly = false; }
        }

        /// <summary>
        /// RF is on when this controller has keyed, an external transmit it
        /// watches is up, the radio reports transmitting, the tune carrier is
        /// up or the ATU is tuning (Astra B6: every allowed RF source). The
        /// episode advances only on the OFF-to-ON edge of that, inside
        /// <see cref="Radios.Speech.TransmitEpisode"/>.
        /// </summary>
        private void NoteRf() => _transmitEpisode.RfIs(_rfCommandedOn || _externalWatchers > 0 || _radioReportsTransmit || _tuneCarrierOn || _atuTuning);

        // -------------------------------------------------------------------
        // Public actions (called from key handlers)
        // -------------------------------------------------------------------

        /// <summary>
        /// Ctrl+Space KeyDown — begin PTT hold (TX on while key held).
        /// </summary>
        public void PttDown()
        {
            if (!CanTransmit()) return;
            if (State == PttState.Idle && RefusedForTeardown()) return;

            // The hold after a reflected-power cut (#571): a press that would
            // start a transmission does not, and says why. Only from Idle —
            // a down while already transmitting keys nothing anyway.
            if (State == PttState.Idle && RefuseIfHeldAfterCut("push to talk")) return;

            // A rig reading 0 watts (#859). Not the hold: its own rule and state.
            if (State == PttState.Idle && ZeroPowerDoors.RefuseTransmit(_getRigControl(), "push to talk")) return;

            // License-aware TX lockout check
            if (CanTransmitHereCheck != null && !CanTransmitHereCheck())
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.blocked_by_license"), VerbosityLevel.Critical, interrupt: true);
                EarconPlayer.Warning2Beep();
                return;
            }

            if (State == PttState.Idle)
            {
                _pttDownTicks = Stopwatch.GetTimestamp();   // #216, see NotePttRelease
                State = PttState.PttHold;
                // Every owed falling tone is void BEFORE the key is asked for
                // (repair round 5, Codex's #863 N2): a payment already under
                // way finishes first, and none starts after.
                ForgetEdgeTones();
                SetTx(true);   // RF off to on: a new transmission, and every sentence about the last one's end is now history
                StartFreshAudioSample();
                KeyDownTone();
                _updateStatusDisplay?.Invoke(Lexicon.Get("audio.ptt.display_transmitting"));
                SpeakKeyDownFirstLine(TransmittingWords());
                if (!AudioCheckNarration) SpeakKeyDownExtra();
                _healthReflectedWarned = false;
                _reflectedRun.Reset();
                _healthTxSeconds = 0;
                // A held PTT used to get NO transmit monitoring at all — the
                // tick returned early and stopped its own timer. That was fine
                // while every check was about an unattended lock, and stops
                // being fine the moment one of them is about the hardware.
                StartAlcTimer();
                Tracing.TraceLine("PTT: Hold started", TraceLevel.Info);
            }
            // If already locked/warning, ignore key-down (don't double-TX)
        }

        /// <summary>
        /// Ctrl+Space KeyUp — end PTT hold (return to RX).
        /// </summary>
        public void PttUp()
        {
            if (State == PttState.PttHold)
            {
                // Record BEFORE going idle: a release too fast to be human
                // means the screen reader is synthesising pairs (#216). The
                // release still happens — detection only, no behaviour change.
                NotePttRelease();
                GoIdle(ReceivingWords());
            }
            // If locked/warning, key-up does nothing (still locked)
        }

        /// <summary>
        /// Shift+Space — toggle TX lock.
        /// If idle, lock TX on. If locked/warning, unlock.
        /// </summary>
        public void ToggleLock()
        {
            if (!CanTransmit() && State == PttState.Idle) return;
            if (State == PttState.Idle && RefusedForTeardown()) return;

            // The hold after a reflected-power cut (#571): from Idle this press
            // would key, so it is refused and repeats why the transmission was
            // stopped. The 2026-10-21 case exactly: the cut had unlocked, and a
            // Shift+Space 149 ms later read as "lock" and keyed 25 W back into
            // the empty port. An unlock (any transmitting state) is never
            // refused — the hold only ever stops RF, never keeps it on.
            if (State == PttState.Idle && RefuseIfHeldAfterCut("the lock key")) return;

            // A rig reading 0 watts (#859). An operator's press only: a check
            // keys through KeyForCheck, and Test your microphone is at zero by
            // ruling. An unlock is never refused.
            if (State == PttState.Idle && !_keyingForCheck
                && ZeroPowerDoors.RefuseTransmit(_getRigControl(), "the lock key")) return;

            if (State == PttState.Idle || State == PttState.PttHold)
            {
                EnterLocked();
            }
            else
            {
                // Any TX state — unlock
                GoIdle(ReceivingWords());
            }
        }

        /// <summary>
        /// A check keys: everything the lock key does, except that it is not an
        /// operator's press, so a rig at 0 watts is not a reason to refuse it. A
        /// check decides its own power first, and only Test your microphone
        /// stays at zero, by ruling (<see cref="RadioPowerFloor.IsZeroWattException"/>).
        /// The teardown and the hold after a cut still answer it.
        /// </summary>
        public void KeyForCheck()
        {
            _keyingForCheck = true;
            try { ToggleLock(); }
            finally { _keyingForCheck = false; }
        }

        private bool _keyingForCheck;

        /// <summary>
        /// The radio's connection fell (#596; Noel, 2026-10-18: "if the radio
        /// drops, so should the key"). Whatever this controller was doing,
        /// held, locked or warning, it goes to Idle, and the operator is told
        /// why with the warning tone first, because they are talking into
        /// dead air and the key dropping is the only other cue they get. On a
        /// station with transmit sounds off, that cue is silent and the
        /// sentence carries it all.
        /// </summary>
        /// <param name="sentence">What happened, already worded. The caller
        /// owns it, because the same sentence is said when the radio was keyed
        /// by something other than this controller.</param>
        /// <returns>True when the controller was transmitting and has been
        /// released, which means the sentence has been said.</returns>
        /// <remarks>
        /// <see cref="GoIdle"/>'s unkey write goes to a connection that is
        /// gone, which is harmless: it is an unkey, and the queue that carries
        /// it traps a dead radio's exception.
        /// </remarks>
        public bool DropWithConnection(string sentence)
        {
            ForgetEdgeTones();
            if (State == PttState.Idle) return false;
            Tracing.TraceLine($"PTT: the connection fell while {State}; the key drops with it", TraceLevel.Warning);
            EarconPlayer.WarningAlarmTone();
            // Its unkey is this client's own carrier only (#596 N4): on a
            // radio object reused by a reconnect, a queued xmit 0 must not
            // reach whoever transmits on the new connection.
            _unkeyOwnCarrierOnly = true;
            _releaseWhy = "a dropped connection";
            try
            {
                GoIdle(sentence, forceSpeech: true,
                       subject: Radios.Speech.SpeechSubject.TransmitDroppedWithConnection);
            }
            finally { _unkeyOwnCarrierOnly = false; }
            return true;
        }

        /// <summary>
        /// The key-down tone (#863, ruled by Noel 2026-11-03): the three-note
        /// tone when this transmission goes out on a slice other than the
        /// selected one and the operator has not turned that notice off, the
        /// ordinary rising pair otherwise (under the chirp switch, as before).
        /// The choice is remembered, so the key-up answers in the same family.
        /// </summary>
        private void KeyDownTone()
        {
            _keyedElsewhere = Radios.TransmitSliceNotice.PlaysTones(_getRigControl(), _config.ChirpEnabled);
            // This key owns its own key-up tone now, whatever a taken-over
            // send or a VOX send owed (#863 R3).
            ForgetEdgeTones();
            // A key on the selected slice ends a remembered split, whichever
            // door it came through (#863, Codex's re-check F1).
            try { Radios.TransmitSliceNotice.KeyedWithoutNotice(_getRigControl()); } catch { }
            if (_keyedElsewhere) EarconPlayer.TxElsewhereStartTone();
            else if (_config.ChirpEnabled) EarconPlayer.TxStartTone();
        }

        /// <summary>
        /// The key-up in the family the key-down chose, OWED to the radio's own
        /// report that it is back in receive (ruled by Noel 2026-11-04 22:24 and
        /// 22:26, before repair round 4): every falling transmit cue, the
        /// three notes and the ordinary chirp alike, means "really off the
        /// air". Our release only asks the radio to unkey; the tone waits for
        /// its answer, with no delay of our own on top.
        /// </summary>
        private void KeyUpTone()
        {
            bool elsewhere = _keyedElsewhere;
            _keyedElsewhere = false;
            if (elsewhere) OweStopToneAtRealReceive(FallingTone.Elsewhere);
            else if (_config.ChirpEnabled) OweStopToneAtRealReceive(FallingTone.Chirp);
        }

        /// <summary>Which falling cue a debt plays: the three notes or the ordinary two.</summary>
        private enum FallingTone { Elsewhere, Chirp }

        /// <summary>This transmission's key-down played the three-note tone.</summary>
        private bool _keyedElsewhere;

        // ── The tri-tones on the radio's own edges (#863) ──────────────────
        //
        // ONE owner for a falling tri-tone this controller did not key for
        // itself, and for a rising one owed to a radio edge. Two routes reach
        // it:
        //
        // - A send on another slice taken over by a hand mic or foot switch
        //   (repair round 2, R3). Its carrier stays up under the operator, so
        //   the falling tone is owed to the REAL unkey. Repair round 3 (Codex's
        //   second re-check, B1): round 2 paid it on NoteRadioTransmit(false),
        //   which follows FlexLib's Mox, and Mox falls the moment our own
        //   software key is withdrawn while the hand mic still holds the
        //   carrier. "You stopped transmitting" was then false. It is now paid
        //   only when the radio really receives: our cached Transmit false AND
        //   the interlock out of transmit, on the same connection
        //   (FlexBase.WhenReallyReceiving, the wait the monitor and VOX
        //   releases already use).
        // - A box-off voice message released to VOX (ruled by Noel 2026-11-04
        //   before repair round 3): the rising tone when the radio reports it
        //   keyed, the falling one when it really receives. Nothing waits on
        //   these tones, so VOX is never delayed.
        //
        // A generation voids every older debt: a key this controller makes
        // (KeyDownTone), a drop and a teardown take the tones over or end them.

        // Repair round 4 (ruled by Noel 2026-11-04 22:24 and 22:26): this owner
        // now holds EVERY falling transmit cue, both families, including this
        // controller's own key-up and a send's own end (VoiceMessageSender
        // hands its debt here). One owner per debt: a debt carries its family
        // and its generation, and a newer one voids it.
        //
        // NO REPORT, NO TONE (the design for Noel's review). A falling cue is
        // played only on the radio's own receive report on the connection it
        // was owed on: FlexBase.WhenReallyReceiving, which needs our cached
        // Transmit false AND a recognised receive report on that still-live
        // connection. If the report never comes, nothing plays: the connection
        // ending or being replaced ends the debt silently (traced), a key of
        // this controller's voids it, and a carrier that never falls keeps it
        // waiting, silent. Never a timed tone: a tone on a timer would be a
        // claim the radio has not made.

        private readonly object _edgeToneLock = new();
        private long _edgeToneGeneration;
        private bool _edgeStopOwed;
        private FallingTone _edgeStopFamily;
        private bool _edgeStartOwed;
        private int _edgeStopTonesPlayed;
        private int _chirpStopTonesPlayed;
        private int _edgeStartTonesPlayed;

        /// <summary>For tests: a falling tri-tone is still owed to the radio's real receive.</summary>
        internal bool ElsewhereStopOwed { get { lock (_edgeToneLock) return _edgeStopOwed; } }

        /// <summary>For tests: a rising tri-tone is owed to the radio's report that it keyed.</summary>
        internal bool ElsewhereStartOwed { get { lock (_edgeToneLock) return _edgeStartOwed; } }

        /// <summary>For tests: how many owed falling tri-tones this controller has played.</summary>
        internal int ElsewhereStopTonesPlayed { get { lock (_edgeToneLock) return _edgeStopTonesPlayed; } }

        /// <summary>For tests: how many owed ordinary falling chirps this controller has played.</summary>
        internal int ChirpStopTonesPlayed { get { lock (_edgeToneLock) return _chirpStopTonesPlayed; } }

        /// <summary>For tests: an ordinary falling chirp is owed to the radio's real receive.</summary>
        internal bool ChirpStopOwed { get { lock (_edgeToneLock) return _edgeStopOwed && _edgeStopFamily == FallingTone.Chirp; } }

        /// <summary>
        /// For tests: when set, every falling tone actually handed to the
        /// earcon player is added here by its earcon's name, in order, at the
        /// call itself, so a test sees the submission and not only the
        /// accounting. Null in production, so nothing grows over a session.
        /// </summary>
        internal System.Collections.Generic.List<string>? FallingTonesSubmitted { get; set; }

        /// <summary>For tests: how many owed rising tri-tones this controller has played.</summary>
        internal int ElsewhereStartTonesPlayed { get { lock (_edgeToneLock) return _edgeStartTonesPlayed; } }

        /// <summary>Every owed edge tone is void: a key of ours, a drop or a teardown has them now.</summary>
        private void ForgetEdgeTones()
        {
            bool unpaid;
            FallingTone family;
            lock (_edgeToneLock)
            {
                unpaid = _edgeStopOwed;
                family = _edgeStopFamily;
                _edgeToneGeneration++;
                _edgeStopOwed = false;
                _edgeStartOwed = false;
            }
            // No report, no tone, and a line saying so (the design for Noel's
            // review): the radio never reported receive for that transmission
            // before a newer key, a drop or a teardown took the tones over.
            if (unpaid)
                Tracing.TraceLine("PTT: the falling " + (family == FallingTone.Elsewhere ? "three-note tone" : "chirp")
                    + " was NOT played: the radio had not reported receive for that transmission before a newer key,"
                    + " a drop or a teardown", TraceLevel.Info);
        }

        /// <summary>
        /// Owe a falling tone to the radio's real receive. When the radio
        /// already really receives, it plays now, so a hand-off after the
        /// carrier fell waits for no further notification.
        /// </summary>
        private void OweStopToneAtRealReceive(FallingTone family, long? generation = null)
        {
            long gen;
            lock (_edgeToneLock)
            {
                gen = generation ?? ++_edgeToneGeneration;
                if (gen != _edgeToneGeneration) return;
                _edgeStopOwed = true;
                _edgeStopFamily = family;
            }
            FlexBase? rig = null;
            try { rig = _getRigControl(); } catch { }
            if (rig == null) { PayStopTone(gen, sameConnection: false, submitIfStillReceiving: null); return; }
            // Given up quietly once a newer debt replaces this one, so a carrier
            // held for a long time does not collect waits behind it.
            rig.WhenReallyReceiving((same, submitIfStillReceiving) => PayStopTone(gen, same, submitIfStillReceiving), () => StillOwed(gen));
        }

        private bool StillOwed(long generation)
        {
            lock (_edgeToneLock) return _edgeStopOwed && generation == _edgeToneGeneration;
        }

        /// <summary>
        /// The radio reported receive on the connection the tone was owed on
        /// (or that connection went): the owed falling tone plays once, in its
        /// family, unless something newer took it over. A connection that went
        /// first ends it silently, with a trace. Any thread.
        /// </summary>
        /// <param name="submitIfStillReceiving">The wait's final step (repair
        /// rounds 5 and 6): it runs the hand-over only if the radio is still
        /// receiving for this transmission, as one step against every new key
        /// this process asks for and every report the transport publishes.
        /// Null when there is no rig.</param>
        private void PayStopTone(long generation, bool sameConnection, Func<Action, bool>? submitIfStillReceiving)
        {
            if (!sameConnection || submitIfStillReceiving == null)
            {
                NotPaid(generation, "the connection it was owed on ended before the radio reported receive");
                return;
            }
            // The last check and the hand-over are ONE step (repair round 6,
            // Codex's #863 S1): under the transport's dispatch gate (the radio's
            // reports, our record of them, the connection's changes), the
            // falling cue's gate (every key this process asks for: push to
            // talk, the lock, sends, checks, tune), and this controller's own
            // tone lock (a key-down's void, repair round 5's N2). A new
            // transmission, visible to this process, therefore either fails
            // that check or comes after the tone was handed to the player.
            bool stood;
            try { stood = submitIfStillReceiving(() => HandOverUnderLock(generation)); }
            catch { stood = false; }
            if (!stood)
                NotPaid(generation, "between the radio's receive report and the tone, a new transmission was asked for,"
                    + " the radio reported a carrier again, or its connection went");
        }

        /// <summary>Hand the owed tone to the player, if it is still this debt's, under the tone lock.</summary>
        private void HandOverUnderLock(long generation)
        {
            lock (_edgeToneLock)
            {
                if (!_edgeStopOwed || generation != _edgeToneGeneration) return;
                var family = _edgeStopFamily;
                _edgeStopOwed = false;
                if (family == FallingTone.Elsewhere) _edgeStopTonesPlayed++;
                else _chirpStopTonesPlayed++;
                var submitted = FallingTonesSubmitted;
                if (submitted != null)
                    lock (submitted)
                        submitted.Add(family == FallingTone.Elsewhere ? nameof(EarconPlayer.TxElsewhereStopTone) : nameof(EarconPlayer.TxStopTone));
                // Handing a tone to the player only queues it to the mixer;
                // nothing it calls comes back to this controller or the radio.
                if (family == FallingTone.Elsewhere) EarconPlayer.TxElsewhereStopTone();
                else EarconPlayer.TxStopTone();
            }
        }

        /// <summary>The owed tone, if it is still this debt's, ends unplayed, with a trace saying why.</summary>
        private void NotPaid(long generation, string why)
        {
            FallingTone family;
            lock (_edgeToneLock)
            {
                if (!_edgeStopOwed || generation != _edgeToneGeneration) return;
                family = _edgeStopFamily;
                _edgeStopOwed = false;
            }
            Tracing.TraceLine("PTT: the falling " + (family == FallingTone.Elsewhere ? "three-note tone" : "chirp")
                + " was NOT played: " + why, TraceLevel.Info);
        }

        /// <summary>
        /// The connection is dropping or being torn down (repair round 4):
        /// every falling tone still owed ends here, unplayed, whether or not
        /// this controller was transmitting. A debt must not outlive its
        /// connection, and the wait pinned to it would end it anyway.
        /// </summary>
        public void EndEdgeTonesWithConnection() => ForgetEdgeTones();

        /// <summary>The radio reported it keyed: a rising tri-tone owed to that edge plays, and its falling one is owed.</summary>
        private void RadioKeyedForOwedStartTone()
        {
            long gen;
            lock (_edgeToneLock)
            {
                if (!_edgeStartOwed) return;
                _edgeStartOwed = false;
                gen = _edgeToneGeneration;
                _edgeStartTonesPlayed++;
            }
            EarconPlayer.TxElsewhereStartTone();
            OweStopToneAtRealReceive(FallingTone.Elsewhere, gen);
        }

        /// <summary>
        /// A box-off voice message on another slice has been released to VOX
        /// (#863, ruled by Noel 2026-11-04): its rising tri-tone plays when the
        /// radio reports it keyed, and its falling one when the radio really
        /// receives. The caller has already applied the notice setting and the
        /// chirp switch. Nothing here delays VOX.
        /// </summary>
        public void TonesOnVoxEdges()
        {
            lock (_edgeToneLock)
            {
                _edgeToneGeneration++;
                _edgeStopOwed = false;
                _edgeStartOwed = true;
            }
            bool keyedAlready = _radioReportsTransmit;
            if (!keyedAlready)
            {
                try { keyedAlready = _getRigControl()?.RadioInterlockReportsTransmit == true; } catch { }
            }
            if (keyedAlready) RadioKeyedForOwedStartTone();
        }

        /// <summary>
        /// The VOX send has ended: a rising tone VOX never keyed for is not
        /// owed any more. A falling tone already owed stays owed to the real
        /// receive.
        /// </summary>
        public void EndVoxEdgeTones()
        {
            lock (_edgeToneLock) _edgeStartOwed = false;
        }

        /// <summary>For tests: this transmission's key-up will play the three-note tone.</summary>
        internal bool KeyedElsewhere => _keyedElsewhere;

        /// <summary>
        /// A box send that keyed on a slice other than the selected one has
        /// been taken over and its carrier stays up under the operator (#863,
        /// Codex's re-check R3). The falling tone belongs to the real unkey:
        /// this controller's own, when it is the one now keying (a push to
        /// talk or lock takeover), otherwise the radio's report of receive.
        /// </summary>
        public void TakeOverElsewhereStopTone()
        {
            if (State != PttState.Idle) { _keyedElsewhere = true; return; }
            OweStopToneAtRealReceive(FallingTone.Elsewhere);
        }

        /// <summary>
        /// A send on another slice ended its OWN carrier (repair round 4, ruled
        /// by Noel 2026-11-04 22:24): its falling tri-tone is owed to the
        /// radio's report that it really receives, through this one owner, not
        /// played at the file's end. A key of this controller's takes it over.
        /// </summary>
        public void OweElsewhereStopTone() => OweStopToneAtRealReceive(FallingTone.Elsewhere);

        /// <summary>
        /// "Play chirp tones on transmit and receive": the one switch both the
        /// two-tone pair and the three-note tones obey (ruled 2026-11-04 06:51),
        /// for a send that keys the radio itself.
        /// </summary>
        public bool ChirpEnabled => _config.ChirpEnabled;

        // The three announcements, with their slice when the operator has
        // asked for it (#496). One reading of the slices per sentence.
        private Radios.SliceRoles CurrentSliceRoles()
        {
            try { return Radios.SliceRoles.Of(_getRigControl()); }
            catch { return default; }
        }

        private string TransmittingWords() =>
            Radios.PttSliceWords.Transmitting(CurrentSliceRoles(), _config.AnnounceTransmitSlice);

        private string ReceivingWords() =>
            Radios.PttSliceWords.Receiving(CurrentSliceRoles(), _config.AnnounceTransmitSlice);

        private string LockedWords() =>
            Radios.PttSliceWords.Locked(CurrentSliceRoles(), _config.AnnounceTransmitSlice);

        /// <summary>
        /// Escape — unlock TX from any state.
        /// </summary>
        public void EscapeUnlock()
        {
            if (State != PttState.Idle)
            {
                GoIdle(ReceivingWords());
            }
        }

        /// <summary>
        /// A recording's send began going out on the operator's transmission
        /// (#835; Noel, 2026-10-30 00:00: <i>"we have a ten minute for longer
        /// recordings, I'm thinking amateur radio news line or another
        /// bulletin"</i>). Under the lock, the timeout's warning ladder stands
        /// down and its clock stands still until <see cref="EndRecordingSendRide"/>;
        /// a warning already sounding stops. The send's own ten-minute limit and
        /// its pause govern it instead, and the 15-minute hard kill is untouched.
        /// Harmless when not locked: a lock begun during the send rides from then.
        /// <paramref name="stillGoing"/> is asked at every tick of the ladder: a
        /// ride whose send has ended without saying so is ended there, so the
        /// lock's timeout can never be left standing down by a lost end.
        /// </summary>
        public void BeginRecordingSendRide(string what, Func<bool>? stillGoing = null)
        {
            if (!_lockClock.BeginSendRide(Environment.TickCount64)) return;
            _rideWhat = what;
            _rideStillGoing = stillGoing;
            bool locked = State is PttState.Locked or PttState.Warning1 or PttState.Warning2 or PttState.OhCrap;
            Tracing.TraceLine("PTT: " + what + " rides the transmission"
                + (locked ? "; the lock's timeout stands still while it goes out (its own ten-minute limit governs it)" : ""),
                TraceLevel.Info);
            if (State is PttState.Warning1 or PttState.Warning2 or PttState.OhCrap)
            {
                State = PttState.Locked;
                _beepTimer?.Stop();
                Tracing.TraceLine("PTT: the timeout warnings stand down for the recording", TraceLevel.Info);
            }
        }

        /// <summary>
        /// The recording stopped riding the transmission (sent, stopped, paused,
        /// taken over). The lock's clock carries on from where it stood, so the
        /// operator's own time by hand is counted exactly as before.
        /// </summary>
        public void EndRecordingSendRide(string what)
        {
            long now = Environment.TickCount64;
            if (!_lockClock.EndSendRide(now)) return;
            _rideStillGoing = null;
            if (State == PttState.Idle)
            {
                Tracing.TraceLine("PTT: " + what + " no longer rides the transmission", TraceLevel.Info);
                return;
            }
            double left = Math.Max(0, EffectiveTimeoutSeconds - _lockClock.ElapsedSeconds(now));
            Tracing.TraceLine("PTT: " + what + " no longer rides the transmission; the lock's timeout carries on with "
                + left.ToString("F0") + " s left", TraceLevel.Info);
        }

        /// <summary>The planned recorder stop: one cue and one outcome, without the timeout ladder.</summary>
        public void RecorderLimitUnlock()
        {
            if (State == PttState.Idle) return;
            if (!_config.ChirpEnabled) EarconPlayer.ConfirmTone();
            GoIdle("Check stopped at the recorder's two-minute limit.", forceSpeech: true);
        }

        // -------------------------------------------------------------------
        // State transitions
        // -------------------------------------------------------------------

        /// <summary>
        /// Every transmit-audio figure the operator can ask for describes "this
        /// transmit", so every key-down has to start them together. The SC_MIC
        /// peak-hold was already reset here for the locked path; the LUFS
        /// sample now joins it, and both now happen on the hold path too.
        ///
        /// Push-to-talk hold reset neither before, so an operator holding
        /// Ctrl+Space was told about a locked transmit from some minutes ago.
        /// Two figures measured over two different windows is exactly the
        /// disagreement the reading is supposed to resolve.
        ///
        /// A key-down that does not come through here (a foot switch, the
        /// radio's own PTT, CW, another client) is restarted by FlexBase at
        /// the radio's reported rising edge instead, and that edge leaves this
        /// reset alone when it is only the echo of our own key (#651).
        /// </summary>
        private void StartFreshAudioSample()
        {
            var rig = _getRigControl();
            if (rig == null) return;
            rig.ResetScMicMax();
            rig.ResetTxLufsIntegrated();
        }

        private void EnterLocked()
        {
            // License-aware TX lockout check
            if (CanTransmitHereCheck != null && !CanTransmitHereCheck())
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.blocked_by_license"), VerbosityLevel.Critical, interrupt: true);
                EarconPlayer.Warning2Beep();
                return;
            }

            bool fromIdle = State == PttState.Idle;
            State = PttState.Locked;
            // As at PttDown: owed falling tones are void before the key (#863 N2).
            if (fromIdle) ForgetEdgeTones();
            // From Idle this starts a transmission; from a held PTT it keeps
            // one on, and the episode must NOT change — a reflected-power
            // warning spoken during the hold is still about this RF. SetTx
            // tells the episode what it commanded and the episode decides
            // which of the two this was (Astra's Track IJK2 review, blocker 1).
            SetTx(true);
            if (fromIdle) KeyDownTone();
            else if (_keyedElsewhere) EarconPlayer.TxElsewhereStartTone();
            else if (_config.ChirpEnabled) EarconPlayer.TxStartTone();
            _lockClock.Start(Environment.TickCount64);
            _alcZeroConsecutiveSeconds = 0;
            _healthSilentMicWarned = false;
            _healthAlcHighWarned = false;
            _healthReflectedWarned = false;
            _healthMicVerified = false;
            _healthMicLevelAdvised = false;
            _healthMicTelemetryMissingTraced = false;
            _reflectedRun.Reset();
            _healthTxSeconds = 0;
            _healthLockSeconds = 0;
            StartFreshAudioSample(); // SC_MIC peak-hold and LUFS sample both start here

            _updateStatusDisplay?.Invoke(Lexicon.Get("audio.ptt.display_locked"));
            if (fromIdle) SpeakKeyDownFirstLine(LockedWords());
            else if (_config.SpeechEnabled && !AudioCheckNarration)
                ScreenReaderOutput.Speak(LockedWords(), VerbosityLevel.Critical, interrupt: true);
            if (!AudioCheckNarration) SpeakKeyDownExtra();
            Tracing.TraceLine("PTT: Locked", TraceLevel.Info);

            StartWarningTimer();
            StartHardKillTimer();
            StartAlcTimer();
        }

        /// <param name="subject">
        /// What the unkey sentence is ABOUT, for the speech arbiter's
        /// supersession — a <c>SpeechSubject</c> constant, or null for the
        /// ordinary unkey, which declares nothing and keeps the word-count
        /// bound. The reflected-power cut passes its own, so an unheard cut
        /// sentence is never binned by its length while the operator is still
        /// wondering why the transmission ended (#503, #571).
        /// </param>
        private void GoIdle(string speechMessage, bool forceSpeech = false, string subject = null)
        {
            var wasState = State;
            State = PttState.Idle;
            SetTx(false);
            // Our transmission is over: a restart of the audio sample it made
            // and no rising edge claimed is retired (#651 follow-up).
            try { _getRigControl()?.RetireOwnAudioSampleClaim(); } catch { }
            ScreenReaderOutput.Supersede(KeyDownExtraSubject, "transmission ended");
            // A where-it-transmits notice still unsaid is about a transmission
            // that has ended (#863; Codex's check of #496 noted push to talk's
            // queued notice outliving a short hold).
            ScreenReaderOutput.Supersede(Radios.Speech.SpeechSubject.TransmitSliceNotice, "transmission ended");
            KeyUpTone();

            // Read the transmission's audio verdict BEFORE the counters below
            // are cleared. Only on an ordinary unkey: forceSpeech marks the
            // three paths that are a safety outcome — timeout, hard kill, ALC
            // release — and an operator who has just been unkeyed by the
            // software does not also need to hear about their microphone gain.
            string levelAdvice = forceSpeech || AudioCheckNarration ? "" : MicLevelAdviceOrEmpty(_getRigControl(), wasState);

            StopAllTimers();
            _lockClock.Reset();
            _alcZeroConsecutiveSeconds = 0;
            _healthSilentMicWarned = false;
            _healthAlcHighWarned = false;
            _healthReflectedWarned = false;
            _healthMicVerified = false;
            _healthMicLevelAdvised = false;
            _healthMicTelemetryMissingTraced = false;
            _reflectedRun.Reset();
            _healthTxSeconds = 0;
            _healthLockSeconds = 0;

            _updateStatusDisplay?.Invoke("");
            // forceSpeech marks the three paths that are NOT a normal unkey:
            // transmit timeout, hard kill, and ALC release. Those are safety
            // outcomes and get the flushing tier; an ordinary release does not
            // need to tear down the queue.
            string unkeyMessage = string.IsNullOrEmpty(levelAdvice)
                ? speechMessage
                : (string.IsNullOrEmpty(speechMessage)
                    ? levelAdvice
                    : speechMessage + ". " + levelAdvice);

            // The safety outcome says the transmission has ENDED and the
            // operator is no longer on the air. That is true until they key
            // again: an unheard copy retained by the speech layer must not be
            // rescued into the next transmission, saying they are off the air
            // while they are on it (Astra's Track IJK review, blocker 6). The
            // episode this sentence is about is captured here, and the speech
            // layer asks before every replay whether it is still the current
            // one. Nothing here guesses what the speech layer will do with the
            // answer. Captured AFTER SetTx(false) above, which is the ended
            // transmission's episode: the answer stays yes until RF next goes
            // on.
            if ((forceSpeech || (_config.SpeechEnabled && !AudioCheckNarration)) && !string.IsNullOrEmpty(unkeyMessage))
                ScreenReaderOutput.Speak(
                    unkeyMessage,
                    forceSpeech
                        ? Radios.Speech.SpeechIntent.Urgent
                        : Radios.Speech.SpeechIntent.Interrupt,
                    VerbosityLevel.Critical,
                    subject: subject,
                    stillValid: forceSpeech ? _transmitEpisode.NoTransmissionSince() : null);

            Tracing.TraceLine($"PTT: Idle (was {wasState})", TraceLevel.Info);
        }

        // -------------------------------------------------------------------
        // Warning escalation timer
        // -------------------------------------------------------------------

        private void StartWarningTimer()
        {
            _warningTimer?.Stop();
            _warningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _warningTimer.Tick += WarningTimerTick;
            _warningTimer.Start();

            // Also start beep timer (initially stopped — starts when entering Warning1)
            _beepTimer?.Stop();
            _beepTimer = new DispatcherTimer();
            _beepTimer.Tick += BeepTimerTick;
        }

        private void WarningTimerTick(object? sender, EventArgs e)
        {
            if (State == PttState.Idle) { StopAllTimers(); return; }
            // A planned stop that comes first owns the cut, so the ladder for
            // a later timeout would only be noise. A shorter operator timeout
            // still wins, and the 15-minute hard kill runs regardless.
            if (PlannedStopSeconds is int planned && EffectiveTimeoutSeconds > planned) return;
            // A recording riding the lock is governed by its own ten-minute
            // limit, not the lock's timeout (#835; Noel, 2026-10-30 00:00), and
            // the clock stands still under it. The 15-minute hard kill is its
            // own timer and runs regardless.
            if (_lockClock.SendRiding)
            {
                bool going = true;
                try { going = _rideStillGoing?.Invoke() ?? true; }
                catch { going = false; }
                if (going) return;
                Tracing.TraceLine("PTT: the riding recording ended without saying so; the lock's timeout carries on",
                    TraceLevel.Warning);
                EndRecordingSendRide(_rideWhat);
            }

            var elapsed = _lockClock.ElapsedSeconds(Environment.TickCount64);
            var timeout = EffectiveTimeoutSeconds;

            // Check escalation thresholds (most urgent first)
            if (elapsed >= timeout)
            {
                // User-configurable timeout reached
                EnterHardKillFromTimeout();
            }
            else if (elapsed >= timeout - _config.OhCrapSecondsBeforeTimeout && State != PttState.OhCrap)
            {
                EnterOhCrap();
            }
            else if (elapsed >= timeout - _config.Warning2SecondsBeforeTimeout && State < PttState.Warning2)
            {
                EnterWarning2();
            }
            else if (elapsed >= timeout - _config.Warning1SecondsBeforeTimeout && State < PttState.Warning1)
            {
                EnterWarning1();
            }
        }

        private void EnterWarning1()
        {
            State = PttState.Warning1;
            ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.timeout_approaching"), VerbosityLevel.Critical);
            Tracing.TraceLine("PTT: Warning1 (10s beeps)", TraceLevel.Info);

            _beepTimer!.Interval = TimeSpan.FromSeconds(10);
            _beepTimer.Start();
            EarconPlayer.Warning1Beep();
        }

        private void EnterWarning2()
        {
            State = PttState.Warning2;
            // Was QUEUED, which meant this could wait behind stale slider or
            // meter values while the operator was locked down and had no idea
            // a timeout was coming. A warning that arrives after the thing it
            // warns about is not a warning.
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.ptt.timeout_soon"), Radios.Speech.SpeechIntent.Interrupt, VerbosityLevel.Critical);
            Tracing.TraceLine("PTT: Warning2 (5s beeps)", TraceLevel.Info);

            _beepTimer!.Stop();
            _beepTimer.Interval = TimeSpan.FromSeconds(5);
            _beepTimer.Start();
            EarconPlayer.Warning2Beep();
        }

        private void EnterOhCrap()
        {
            State = PttState.OhCrap;
            // URGENT: interrupt AND flush. Plain interrupt stops what is being
            // spoken but leaves the queue standing, so a stale meter or slider
            // readout could still play out ON TOP of a warning that the radio
            // is about to stop transmitting. This is the one place in the
            // application where that ordering is a safety question rather than
            // a tidiness one.
            // The subject is the time-limit incident, shared with the
            // announcement that the transmission HAS ended (below, through
            // GoIdle). Since an Urgent warning became a protected obligation,
            // a sentence with no subject can be retired by nothing — so this
            // one would have queued behind its own outcome instead of being
            // covered by it.
            // "About to end" is about THIS transmission while it is running;
            // its outcome supersedes it by subject, and a new transmission
            // withdraws an unheard copy the same way the cut's is (blocker 6).
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.ptt.timeout_ending_now"), Radios.Speech.SpeechIntent.Urgent, VerbosityLevel.Critical,
                subject: Radios.Speech.SpeechSubject.TransmitTimeLimit,
                stillValid: _transmitEpisode.StillThisTransmission());
            Tracing.TraceLine("PTT: OhCrap (1s beeps)", TraceLevel.Info);

            _beepTimer!.Stop();
            _beepTimer.Interval = TimeSpan.FromSeconds(1);
            _beepTimer.Start();
            EarconPlayer.OhCrapBeep();
        }

        private void EnterHardKillFromTimeout()
        {
            Tracing.TraceLine("PTT: Timeout hard kill", TraceLevel.Warning);
            EarconPlayer.HardKillTone();
            // The same owner as the "about to end" warning above: this is that
            // incident's outcome, and it covers the warning's fact rather than
            // queueing behind it.
            GoIdle(Lexicon.Get("audio.ptt.timed_out"), forceSpeech: true,
                   subject: Radios.Speech.SpeechSubject.TransmitTimeLimit);
        }

        private void BeepTimerTick(object? sender, EventArgs e)
        {
            if (State == PttState.Idle) return;

            switch (State)
            {
                case PttState.Warning1: EarconPlayer.Warning1Beep(); break;
                case PttState.Warning2: EarconPlayer.Warning2Beep(); break;
                case PttState.OhCrap: EarconPlayer.OhCrapBeep(); break;
            }
        }

        // -------------------------------------------------------------------
        // Hard kill timer (absolute 15-min, non-configurable)
        // -------------------------------------------------------------------

        private void StartHardKillTimer()
        {
            _hardKillTimer?.Stop();
            _hardKillTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(PttConfig.HardKillSeconds)
            };
            _hardKillTimer.Tick += (s, e) =>
            {
                _hardKillTimer?.Stop();
                if (State != PttState.Idle)
                {
                    Tracing.TraceLine("PTT: HARD KILL (15 min absolute)", TraceLevel.Warning);
                    EarconPlayer.HardKillTone();
                    GoIdle(Lexicon.Get("audio.ptt.hard_limit"), forceSpeech: true);
                }
            };
            _hardKillTimer.Start();
        }

        // -------------------------------------------------------------------
        // ALC auto-release monitoring
        // -------------------------------------------------------------------

        private void StartAlcTimer()
        {
            _alcTimer?.Stop();
            _alcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _alcTimer.Tick += AlcTimerTick;
            _alcTimer.Start();
        }

        private void AlcTimerTick(object? sender, EventArgs e)
        {
            if (State == PttState.Idle && !ExternalTransmitWatch)
            {
                _alcTimer?.Stop();
                return;
            }

            var rig = _getRigControl();
            if (rig == null) return;

            _healthTxSeconds++;

            // Runs in EVERY transmit state, held PTT included. Everything below
            // this line is about an unattended LOCK — the auto-release exists
            // because nobody has a finger on the key, and the audio-quality
            // warnings can afford to wait five seconds. Power coming back can
            // not: it is arriving at the finals now, and it does not care which
            // key the operator is holding.
            //
            // AND IT RUNS IN DUMMY LOAD MODE TOO. This line originally read
            // `if (!rig.DummyLoadMode)`, copied from the dead-carrier check
            // below where skipping is correct because drive genuinely behaves
            // differently. For reflected power it is backwards, and backwards
            // in the worst possible way: a declared dummy load is the case where
            // near-zero reflected power is MOST expected, so a high reading
            // there is MORE diagnostic, not less. On 2026-08-22 the fault was
            // precisely a dummy load connected to the port that was not
            // selected — so the original gate would have silenced this warning
            // in the exact scenario it was written for.
            CheckReflectedPower(rig);

            // An EXTERNAL transmit stops here: the watch exists for the live
            // reflected check alone. Everything below is the lock and hold
            // machinery of transmissions this controller owns, and applying
            // it to a transmit-check probe would fight the measurement.
            if (State == PttState.Idle) return;

            // A held PTT stops here. The state machine has always treated hold
            // as the operator's own hand on the key, and this preserves that.
            if (State == PttState.PttHold) return;

            // Skip dead-carrier monitoring in dummy load mode — drive behaves differently
            if (rig.DummyLoadMode) { _alcZeroConsecutiveSeconds = 0; return; }

            // Skip when disabled (0 = disabled)
            if (_config.AlcAutoReleaseSeconds <= 0) return;

            if (rig.SwAlcDb < NoSignalDbfs) // no modulation this second (was HWALC, always ~0)
            {
                _alcZeroConsecutiveSeconds++;
                if (_alcZeroConsecutiveSeconds >= _config.AlcAutoReleaseSeconds)
                {
                    Tracing.TraceLine($"PTT: ALC auto-release after {_alcZeroConsecutiveSeconds}s of zero signal", TraceLevel.Info);
                    GoIdle(Lexicon.Get("audio.ptt.no_signal_release"), forceSpeech: true);
                    return;
                }
            }
            else
            {
                _alcZeroConsecutiveSeconds = 0;
            }

            // TX health monitor
            _healthLockSeconds++;

            // Transmit audio, on its own schedule — see MonitorTransmitAudio.
            MonitorTransmitAudio(rig);

            if (_healthLockSeconds >= 5)
            {
                if (!_healthAlcHighWarned && rig.SwAlcDb > AlcHotDbfs)
                {
                    _healthAlcHighWarned = true;
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.mic_level_too_high"), VerbosityLevel.Critical);
                    Tracing.TraceLine($"PTT: Health warning — ALC pegging (SW ALC {rig.SwAlcDb:F1} dBFS)", TraceLevel.Info);
                }
            }
        }

        /// <summary>
        /// Everything that decides which path transmit audio takes, as one
        /// comparable string, so a proof taken on one path is never trusted on
        /// another.
        /// </summary>
        /// <remarks>
        /// <b>The Windows capture device is deliberately empty here, and that is
        /// a real gap worth knowing about.</b> Nothing reachable from this class
        /// reports the device the audio engine actually opened — the saved
        /// selection lives in <c>audioDevices.xml</c> and the live one is
        /// internal to JJPortaudio. The operator changing devices through the
        /// audio devices dialog is covered instead by an explicit
        /// <see cref="MicPathVerification.Invalidate"/> there, which is the
        /// route a change is actually made through. What is NOT covered is a
        /// microphone physically unplugged, or swapped in Windows' own sound
        /// settings, part-way through a proven ten minutes. The parameter is
        /// kept so that whoever exposes the live device has one place to put it.
        /// </remarks>
        private static string MicPathSignatureFor(FlexBase rig) =>
            TransmitSafety.MicPathSignature(
                rig.ConnectedSerial, rig.MicSource, rig.PCAudio, audioDeviceId: "");

        /// <summary>
        /// Watch whether transmit audio is arriving, and latch the SUCCESS
        /// (#459).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What was here before, and why it fired on a working station.</b>
        /// The check ran on the first tick where the locked window reached five
        /// seconds, asked whether the SC_MIC peak-hold had risen above
        /// <c>-45 dBFS</c>, and latched the answer forever. Two things follow
        /// from that, and both bit a real operator on the air.
        /// </para>
        /// <para>
        /// First, it was a LEVEL test standing in for a PRESENCE test. An
        /// operator measured at -92.59 dBFS while audible on the air and making
        /// contacts sits 47 dB below that threshold, so he was told his
        /// microphone was dead on every transmission. The meter already
        /// distinguishes the two faults: nothing arriving at all reads the
        /// -150 floor sentinel, and -92 is emphatically not -150. So the test
        /// is now "did anything arrive", and the level is a separate, quieter
        /// question answered at the end of the over.
        /// </para>
        /// <para>
        /// Second, it latched the FAILURE. Because the peak-hold only ever
        /// grows, a verdict of "silent" at five seconds could be false by six —
        /// the warning could be contradicted by the meter before the sentence
        /// finished being spoken. An operator who keys up, gathers his thoughts
        /// for five seconds and then talks for four minutes was told his
        /// microphone was dead. Latching the success instead cannot go wrong
        /// that way: once audio has arrived, that answer can never become
        /// false. So the window is watched to its end rather than judged at a
        /// moment, and it is ten seconds rather than five, because five is a
        /// normal amount of time to think before speaking.
        /// </para>
        /// <para>
        /// The proof then outlives the transmission for ten minutes
        /// (<see cref="MicPathVerification"/>), so a working station is not
        /// re-examined on every over — but only for as long as the audio path
        /// is unchanged.
        /// </para>
        /// </remarks>
        private void MonitorTransmitAudio(FlexBase rig)
        {
            if (_healthMicVerified || _healthSilentMicWarned) return;

            string signature = MicPathSignatureFor(rig);

            // Proven recently, on this same path: say nothing and stop looking.
            if (MicPathVerification.Holds(signature))
            {
                _healthMicVerified = true;
                return;
            }

            switch (TransmitSafety.JudgeMicPath(rig.ScMicMaxDb, _healthLockSeconds,
                                                 rig.ScMicReportedSinceReset))
            {
                case TransmitSafety.MicPathVerdict.Verified:
                    _healthMicVerified = true;
                    MicPathVerification.NoteVerified(signature);
                    Tracing.TraceLine(
                        $"PTT: transmit audio is arriving (SC_MIC peak {rig.ScMicMaxDb:F1} dBFS "
                        + $"after {_healthLockSeconds}s) — nothing said, and the path is proven "
                        + "until it changes", TraceLevel.Info);
                    break;

                case TransmitSafety.MicPathVerdict.NothingArrived:
                    _healthSilentMicWarned = true;
                    // Urgent, and worded for the fault it actually is: nothing
                    // reached the radio, which means the device, the profile or
                    // the microphone itself — not a level to nudge.
                    // Its own owner, not a shared safety subject: transmitting
                    // into silence and power coming back are different faults
                    // and neither covers the other.
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("audio.ptt.no_transmit_audio"),
                        Radios.Speech.SpeechIntent.Urgent,
                        VerbosityLevel.Critical,
                        subject: Radios.Speech.SpeechSubject.NoTransmitAudio);
                    Tracing.TraceLine(
                        $"PTT: no transmit audio at all — SC_MIC peak still at the "
                        + $"{TransmitSafety.MicNothingArrivedDbfs:F0} dBFS floor after "
                        + $"{_healthLockSeconds}s, from a meter copy that IS reporting. "
                        + rig.ScMicElectionText, TraceLevel.Warning);
                    break;

                case TransmitSafety.MicPathVerdict.NoTelemetry:
                    // Nothing is said. A floor from a meter that never reported
                    // is not silence, it is not being connected to the
                    // instrument — the warning that fired on Don's working
                    // station on 2026-09-01 (#502). Traced once, with the whole
                    // election, so the person reading the trace can see which
                    // copies exist and that none of them spoke.
                    if (_healthMicTelemetryMissingTraced) break;
                    _healthMicTelemetryMissingTraced = true;
                    Tracing.TraceLine(
                        $"PTT: cannot judge transmit audio — no SC_MIC sample since key-down "
                        + $"after {_healthLockSeconds}s, so the floor is not evidence and nothing is said. "
                        + rig.ScMicElectionText, TraceLevel.Warning);
                    break;
            }
        }

        /// <summary>
        /// The gain-staging advice, if this transmission earned it: audio DID
        /// arrive, so nothing is wrong with the path, but it never got anywhere
        /// near a usable level.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately at unkey rather than mid-transmission, and deliberately
        /// not urgent (#459). A level is something the operator adjusts next
        /// over; interrupting them to say so is the noise that teaches people to
        /// stop listening to this whole class of warning. Waiting to the end
        /// also means the peak-hold has seen the entire window, so the advice
        /// cannot be contradicted a second after it is given — which is the
        /// same mistake, in miniature, that the silent-mic warning was making.
        /// </para>
        /// <para>
        /// The threshold is still <see cref="SilentMicDbfs"/> and it is still
        /// -45. Noel ruled on 2026-09-01 that the shape is fixed now and the
        /// NUMBER is set later from measurement across both radios: the one
        /// spoken reading we hold came from a window that may have had very
        /// little talking in it, and guessing a second number is how the first
        /// one got here.
        /// </para>
        /// </remarks>
        private string MicLevelAdviceOrEmpty(FlexBase? rig, PttState wasState)
        {
            if (rig == null) return "";
            if (wasState != PttState.Locked) return "";
            if (_healthMicLevelAdvised || !_healthMicVerified) return "";
            // Too short a window to have heard the operator's real peaks.
            if (_healthLockSeconds < 5) return "";
            if (!TransmitSafety.ShouldAdviseMicLevel(rig.ScMicMaxDb, SilentMicDbfs)) return "";

            _healthMicLevelAdvised = true;
            Tracing.TraceLine(
                $"PTT: transmit audio arrived but stayed low (SC_MIC peak {rig.ScMicMaxDb:F1} "
                + $"dBFS over {_healthLockSeconds}s) — level advice at unkey, not an alarm",
                TraceLevel.Info);
            return Lexicon.Get("audio.ptt.mic_level_low");
        }

        /// <summary>
        /// Speaks once per transmission when most of the transmit power is
        /// arriving back instead of leaving.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This exists because on 2026-08-22 the bench 8600 transmitted into an
        /// EMPTY ANT1 connector — the dummy load was sitting on ANT2 — and threw
        /// 76 percent of its output straight back into the finals, while the
        /// radio's own SWR meter reported 1.008 and nothing in the app said a
        /// word. Two full sessions of measurements were taken through that
        /// silence. The operator is blind and cannot read the port labels on the
        /// back of the radio, which is precisely why the software has to be the
        /// thing that notices.
        /// </para>
        /// <para>
        /// It reads forward and reflected power rather than the SWR meter, for
        /// the reason above. It names the transmit antenna, because "check the
        /// antenna" is advice and "check ANT1" is an instruction. And it stands
        /// down while the tuner is running, since a tune cycle transmits into a
        /// deliberately bad match on purpose — a warning that fires on every
        /// routine tune-up is one the operator stops hearing before the day it
        /// matters.
        /// </para>
        /// <para>
        /// Once per transmission, not once per second. A warning that repeats
        /// while the operator is trying to act on it is noise.
        /// </para>
        /// </remarks>
        private void CheckReflectedPower(FlexBase rig)
        {
            // ONE reading, not two property gets (#453). Forward and reflected
            // arrive as two separate meter callbacks into two separate fields,
            // so reading them one after the other samples two different
            // instants — and on a speech envelope, where forward power plunges
            // toward zero between syllables many times a second, that puts a
            // small forward reading underneath a slightly older, larger
            // reflected one and spikes the ratio. It ended real transmissions
            // on a correctly matched antenna, and only ever on voice, never on
            // a tune: a tune is a steady carrier, so there is no envelope and
            // no skew.
            TransmitPowerReading reading = rig.ReadTransmitPower();

            // Tuning, read FRESH from the radio every tick and remembered by
            // nobody (#453, the manual-tune half). Two things count:
            //
            //  - The radio's own ATU cycle, as before.
            //  - The operator's tune carrier (Ctrl+Shift+T) while THIS
            //    controller owns the transmission. A tester's 6300 has no
            //    internal tuner; he drives a remote one with that carrier, so
            //    the ATU flag never fires on his station. The carrier state
            //    comes from the radio (FlexBase.TxTune reads FlexLib's TXTune)
            //    and NOT from the FlexAntTunerStartStop event, which carries a
            //    start for this path and no stop — the stop is raised only
            //    inside FlexTunerOn, which the carrier toggle does not go
            //    through. A flag latched from that event would silence this
            //    alarm for good the first time a carrier was dropped by the
            //    kill switch, the radio's own timeout or another client. The
            //    radio's state cannot latch: whatever drops the carrier clears
            //    it. Owned transmissions only, because during an external
            //    watch the carrier up is a transmit check's own probe, which
            //    is a measurement and must be judged.
            //
            // Unreadable means NOT tuning: an unknown must never silence a
            // safety alarm.
            bool tuneCarrierUp;
            try { tuneCarrierUp = rig.TxTune; } catch { tuneCarrierUp = false; }
            bool tuning = rig.ATUTuneInProgress || (State != PttState.Idle && tuneCarrierUp);

            int inconsistentBefore = _reflectedRun.IncoherentSamples;
            _reflectedRun.Observe(reading, _healthTxSeconds);
            if (_reflectedRun.IncoherentSamples == 1 && inconsistentBefore == 0)
                Tracing.TraceLine(
                    "PTT: declining to judge reflected power — " + reading.WhyNotCoherent,
                    TraceLevel.Info);

            // A sustained bad run that ended in a good sample: the match went
            // from bad to fine while the operator kept transmitting, which is
            // what a remote tuner finding its match looks like from here.
            // Nothing is said; the trace is the corroboration a tester's "my
            // tuner said 1.7" never had (#453).
            if (_reflectedRun.JustRecovered)
                Tracing.TraceLine(
                    "PTT: reflected power settled — " + _reflectedRun.LastRecovery
                    + " — nothing said; that is what a tuner finding its match looks like",
                    TraceLevel.Info);

            // The CUT (#224), on either of two rungs (#571 tier 1). The SHARE
            // rung: after the alarm has fired, a further bad sample at real
            // power ends the transmission — two distinct bad samples by
            // construction, since the warning latched on an earlier tick and
            // this reads the current one, so a key-down transient can never
            // cut. The WATTS rung: ten watts or more coming back on two
            // coherent samples in a row, whatever the share, because
            // reflected watts is what heats the finals and a voice trough
            // cannot fake it. Both only when the operator turned the setting
            // on. Only for transmissions THIS CONTROLLER owns: during an
            // external watch (a transmit-check probe) the state is Idle. That
            // is no longer a decision not to protect the probe — since #236
            // the checks carry the same cut, through the same TransmitSafety
            // rule, on TransmitKillSwitch's own thread, where it can actually
            // run while the stage blocks this one.
            TransmitSafety.ReflectedCut cut = State == PttState.Idle
                ? TransmitSafety.ReflectedCut.None
                : TransmitSafety.JudgeReflectedCut(
                    _config.CutTransmitOnReflectedAlarm, _healthReflectedWarned,
                    reading, _reflectedRun, tuning);
            if (cut != TransmitSafety.ReflectedCut.None)
            {
                Tracing.TraceLine(
                    $"PTT: reflected-power CUT on the {cut.ToString().ToLowerInvariant()} rung — "
                    + $"{reading.ReflectedWatts:F1} W back"
                    + (float.IsNaN(reading.ReflectedShare) ? "" : $" ({reading.ReflectedShare * 100f:F0}%)")
                    + $" at {reading.ForwardWatts:F1} W forward, setting is on", TraceLevel.Warning);
                // A blind operator has no visual cue their transmit ended and
                // will keep talking: warning earcon first, then GoIdle's
                // Urgent speech says what happened, why, and that they are no
                // longer on the air — in the rung's own unit, never the
                // other's (#237).
                EarconPlayer.WarningAlarmTone();
                string cutAntenna = rig.TXAntennaName ?? "";
                GoIdle(TransmitSafety.ReflectedCutTextFor(cut, reading, cutAntenna,
                           rig.CurrentVoxSettingIfOn() == FlexBase.VoxSetting.SimpleVox),
                       forceSpeech: true,
                       subject: Radios.Speech.SpeechSubject.ReflectedPowerCut);
                // And nothing re-keys into the same fault for the next few
                // seconds (#571). After GoIdle on purpose: the cut sentence is
                // Urgent and goes first; the hold's VOX note queues behind it.
                // Only this cut starts a hold — the timeout, the ALC release
                // and the hard kill do not (Noel, 2026-10-22 05:14).
                BeginCutHold(rig, cut, reading, cutAntenna);
                return;
            }

            TransmitSafety.ReflectedVerdict verdict = TransmitSafety.JudgeReflected(
                reading, _reflectedRun, _healthTxSeconds, tuning, _healthReflectedWarned);

            // The settling rule (#453): high, sustained, and still MOVING —
            // a tuner hunting, or something else moving the match. The alarm
            // is held off while the share changes, for up to the bound, and
            // then judged on level alone. Said once per streak, at Info, so a
            // bundle shows the alarm was held off on purpose rather than
            // asleep; the two look identical from the operator's chair.
            if (verdict == TransmitSafety.ReflectedVerdict.Deferred)
            {
                if (_reflectedRun.NoteDeferred() == 1)
                    Tracing.TraceLine(
                        $"PTT: reflected power is high but still CHANGING ({reading}, {_reflectedRun}) "
                        + "— alarm deferred while it settles, for up to "
                        + $"{TransmitSafety.ReflectedSettleBoundSeconds:F0} s; a tuner hunting looks "
                        + "like this and a bad antenna holds still",
                        TraceLevel.Info);
                return;
            }
            if (verdict != TransmitSafety.ReflectedVerdict.Warn) return;

            _healthReflectedWarned = true;

            float back = reading.ReflectedShare;
            string antenna = rig.TXAntennaName ?? "";

            EarconPlayer.WarningAlarmTone();
            // URGENT, the flushing tier — not a plain Critical utterance.
            //
            // Verified on the bench 2026-08-22, and the transcript is the
            // evidence. Key-down queued three things at 82,040 ms: the TX tone,
            // "Transmitting, locked", and the test-tone notice. This warning
            // arrived at 84,035 ms and had to WAIT for all of it. Noel missed it
            // entirely on the first transmission and had to key a second time,
            // which is a warning that failed at the only job it has.
            //
            // Urgent is what GoIdle already uses for transmit timeout, hard kill
            // and ALC release, on the grounds that a safety outcome outranks
            // whatever is mid-sentence. Power arriving back at the finals is
            // that class of thing. It cuts the preamble off mid-word, on
            // purpose.
            // cutDisarmed: with the operator's cutoff OFF (#224), this warning
            // is the moment the cut would have acted, so the sentence says out
            // loud that no cut is coming — otherwise a safety they disarmed
            // weeks ago is still silently trusted at exactly the wrong moment.
            // ReflectedPowerWarning, and deliberately NOT ReflectedPowerCut:
            // a later warning must not be able to retire an unheard sentence
            // saying the transmission was ended. Successive warnings on the
            // same run do cover one another, which is what this subject is
            // for — only the newest reading is true.
            // "Power is coming back" is a claim about THIS transmission: once it
            // has ended, or a new one begun, an unheard copy is withdrawn
            // rather than replayed (blocker 6). "This transmission" is the RF,
            // not the state: a hold that becomes a lock is the same one, and an
            // external transmit under watch is one even though State is Idle
            // (Astra's Track IJK2 review, blocker 1 and its producer-contract
            // follow-up).
            ScreenReaderOutput.Speak(
                TransmitSafety.ReflectedWarningText(back, antenna, rig.DummyLoadMode,
                    cutDisarmed: !_config.CutTransmitOnReflectedAlarm),
                Radios.Speech.SpeechIntent.Urgent,
                VerbosityLevel.Critical,
                subject: Radios.Speech.SpeechSubject.ReflectedPowerWarning,
                stillValid: _transmitEpisode.StillThisTransmission());
            Tracing.TraceLine(
                $"PTT: Health warning — reflected power {back * 100f:F0}% "
                + $"({reading}, {_reflectedRun}, "
                + $"computed SWR {rig.ComputedSWR:F2}, meter said {rig.SWRValue:F3}, "
                + $"antenna {(antenna.Length == 0 ? "unknown" : antenna)})",
                TraceLevel.Info);
        }

        // -------------------------------------------------------------------
        // Cleanup
        // -------------------------------------------------------------------

        private void StopAllTimers()
        {
            _warningTimer?.Stop();
            _beepTimer?.Stop();
            _hardKillTimer?.Stop();
            _alcTimer?.Stop();
        }

        /// <summary>
        /// Call on radio disconnect or app shutdown.
        /// </summary>
        public void Dispose()
        {
            if (State != PttState.Idle)
            {
                SetTx(false);
                State = PttState.Idle;
            }
            StopAllTimers();
            // The hold goes with its owner, and VOX comes back if this hold had
            // it off and the rig can still be written (#571) -- immediately,
            // because the command loop may already be stopping. If the rig is
            // already gone, VOX stays off: the accepted, safe-direction failure.
            ReleaseRigForTeardown("the PTT controller is going away");
        }
    }
}
