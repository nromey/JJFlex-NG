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

        private static void SpeakKeyDownExtra()
        {
            string? extra = null;
            try { extra = KeyDownAnnouncementExtra?.Invoke(); }
            catch { /* never let an announcement hook break keying */ }
            if (!string.IsNullOrEmpty(extra))
                ScreenReaderOutput.Speak(extra, VerbosityLevel.Critical);
        }

        /// <summary>
        /// Session-scoped soft-timeout override in seconds (QB Track G,
        /// 2026-08-07). When non-null, the effective lock timeout is the
        /// SMALLER of the operator's configured timeout and this value; the
        /// existing warning ladder scales off the effective value unchanged
        /// and the 15-minute hard kill is untouched. Set by the Audio Check
        /// session on start (a check wants a short leash regardless of the
        /// operator's ragchew timeout) and cleared on session end. This is
        /// deliberately the minimal hook — the session must NOT grow its own
        /// safety timer stack.
        /// </summary>
        public int? SessionTimeoutOverrideSeconds { get; set; }

        private double EffectiveTimeoutSeconds =>
            SessionTimeoutOverrideSeconds is int o
                ? Math.Min(o, _config.TimeoutSeconds)
                : _config.TimeoutSeconds;

        // Timers
        private DispatcherTimer? _warningTimer;
        private DispatcherTimer? _beepTimer;
        private DispatcherTimer? _hardKillTimer;
        private DispatcherTimer? _alcTimer;

        // TX lock start time for timeout calculation
        private DateTime _lockStartTime;

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
        public void KillTransmitNow(string source)
        {
            bool owned = State != PttState.Idle;
            FlexBase? rig = null;
            try { rig = _getRigControl(); } catch { }

            Tracing.TraceLine("PTT: HARD KILL requested by " + source
                              + " (controller state " + State + ")", TraceLevel.Warning);

            // 1. RF off, here, now. Independently, so a throw on one carrier
            //    cannot skip the other.
            TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Mox);
            TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Tune);

            // 2. If a transmit check is armed, it owns the confirmation and the
            //    words — it is the thing that knows whether a carrier was ever
            //    raised. A no-op when nothing is armed.
            TransmitKillSwitch.Request(TransmitKillSwitch.Source.HostRequest);

            if (!owned) return;

            // 3. Our own bookkeeping, on the thread that owns the timers.
            if (_dispatcher.CheckAccess()) FinishOwnedKill();
            else _dispatcher.BeginInvoke(new Action(FinishOwnedKill));
        }

        private void FinishOwnedKill()
        {
            if (State == PttState.Idle) return;
            EarconPlayer.HardKillTone();
            GoIdle(Lexicon.Get("audio.ptt.kill_stopped"), forceSpeech: true);
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
            var elapsed = (DateTime.UtcNow - _lockStartTime).TotalSeconds;
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
                    + " holds the key. See task #216. Further occurrences counted, not logged.",
                    TraceLevel.Error);
            }
        }

        private void SetTx(bool on)
        {
            var rig = _getRigControl();
            if (rig != null)
                rig.Transmit = on;
        }

        // -------------------------------------------------------------------
        // Public actions (called from key handlers)
        // -------------------------------------------------------------------

        /// <summary>
        /// Ctrl+Space KeyDown — begin PTT hold (TX on while key held).
        /// </summary>
        public void PttDown()
        {
            if (!CanTransmit()) return;

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
                SetTx(true);
                StartFreshAudioSample();
                if (_config.ChirpEnabled) EarconPlayer.TxStartTone();
                _updateStatusDisplay?.Invoke(Lexicon.Get("audio.ptt.display_transmitting"));
                if (_config.SpeechEnabled)
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.announce_transmitting"), VerbosityLevel.Critical, interrupt: true);
                SpeakKeyDownExtra(); // armed test tone, etc. — always speaks
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
                GoIdle(Lexicon.Get("audio.ptt.announce_receiving"));
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

            if (State == PttState.Idle || State == PttState.PttHold)
            {
                EnterLocked();
            }
            else
            {
                // Any TX state — unlock
                GoIdle(Lexicon.Get("audio.ptt.announce_receiving"));
            }
        }

        /// <summary>
        /// Escape — unlock TX from any state.
        /// </summary>
        public void EscapeUnlock()
        {
            if (State != PttState.Idle)
            {
                GoIdle(Lexicon.Get("audio.ptt.announce_receiving"));
            }
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

            State = PttState.Locked;
            SetTx(true);
            if (_config.ChirpEnabled) EarconPlayer.TxStartTone();
            _lockStartTime = DateTime.UtcNow;
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
            if (_config.SpeechEnabled)
                ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.announce_locked"), VerbosityLevel.Critical, interrupt: true);
            SpeakKeyDownExtra(); // armed test tone, etc. — always speaks
            Tracing.TraceLine("PTT: Locked", TraceLevel.Info);

            StartWarningTimer();
            StartHardKillTimer();
            StartAlcTimer();
        }

        private void GoIdle(string speechMessage, bool forceSpeech = false)
        {
            var wasState = State;
            State = PttState.Idle;
            SetTx(false);
            if (_config.ChirpEnabled) EarconPlayer.TxStopTone();

            // Read the transmission's audio verdict BEFORE the counters below
            // are cleared. Only on an ordinary unkey: forceSpeech marks the
            // three paths that are a safety outcome — timeout, hard kill, ALC
            // release — and an operator who has just been unkeyed by the
            // software does not also need to hear about their microphone gain.
            string levelAdvice = forceSpeech ? "" : MicLevelAdviceOrEmpty(_getRigControl(), wasState);

            StopAllTimers();
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

            if ((forceSpeech || _config.SpeechEnabled) && !string.IsNullOrEmpty(unkeyMessage))
                ScreenReaderOutput.Speak(
                    unkeyMessage,
                    forceSpeech
                        ? Radios.Speech.SpeechIntent.Urgent
                        : Radios.Speech.SpeechIntent.Interrupt,
                    VerbosityLevel.Critical);

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

            var elapsed = (DateTime.UtcNow - _lockStartTime).TotalSeconds;
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
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.ptt.timeout_ending_now"), Radios.Speech.SpeechIntent.Urgent, VerbosityLevel.Critical);
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
            GoIdle(Lexicon.Get("audio.ptt.timed_out"), forceSpeech: true);
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
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("audio.ptt.no_transmit_audio"),
                        Radios.Speech.SpeechIntent.Urgent,
                        VerbosityLevel.Critical);
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

            // The CUT (#224): after the alarm has fired, a further bad sample
            // at real power ends the transmission — when, and only when, the
            // operator turned the setting on. Two distinct bad samples by
            // construction: the warning latched on an earlier tick, this
            // reads the current one, so a key-down transient can never cut.
            // Only for transmissions THIS CONTROLLER owns: during an external
            // watch (a transmit-check probe) the state is Idle. That is no
            // longer a decision not to protect the probe — since #236 the
            // checks carry the same cut, through the same TransmitSafety rule,
            // on TransmitKillSwitch's own thread, where it can actually run
            // while the stage blocks this one.
            if (State != PttState.Idle
                && TransmitSafety.ShouldCutReflected(
                    _config.CutTransmitOnReflectedAlarm, _healthReflectedWarned,
                    reading, tuning))
            {
                float cutBack = reading.ReflectedShare;
                Tracing.TraceLine(
                    $"PTT: reflected-power CUT — {cutBack * 100f:F0}% back at "
                    + $"{reading.ForwardWatts:F1} W forward, setting is on", TraceLevel.Warning);
                // A blind operator has no visual cue their transmit ended and
                // will keep talking: warning earcon first, then GoIdle's
                // Urgent speech says what happened, why, and that they are no
                // longer on the air.
                EarconPlayer.WarningAlarmTone();
                GoIdle(TransmitSafety.ReflectedCutText(cutBack, rig.TXAntennaName ?? ""),
                       forceSpeech: true);
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
            ScreenReaderOutput.Speak(
                TransmitSafety.ReflectedWarningText(back, antenna, rig.DummyLoadMode,
                    cutDisarmed: !_config.CutTransmitOnReflectedAlarm),
                Radios.Speech.SpeechIntent.Urgent,
                VerbosityLevel.Critical);
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
        }
    }
}
