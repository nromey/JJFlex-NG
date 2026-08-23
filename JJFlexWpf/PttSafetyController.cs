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
        /// Seconds transmitting in ANY state, unlike <c>_healthLockSeconds</c>
        /// which counts only a locked transmission. The reflected-power warning
        /// runs on this one because a held PTT into a dead antenna port is
        /// exactly as bad for the finals as a locked one.
        /// </summary>
        private int _healthTxSeconds;
        private int _healthLockSeconds;

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
        }

        /// <summary>
        /// Update config (e.g. after operator switch).
        /// </summary>
        public void UpdateConfig(PttConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Whether the controller is in any transmitting state.
        /// </summary>
        public bool IsTransmitting => State != PttState.Idle;

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
                State = PttState.PttHold;
                SetTx(true);
                StartFreshAudioSample();
                if (_config.ChirpEnabled) EarconPlayer.TxStartTone();
                _updateStatusDisplay?.Invoke(Lexicon.Get("audio.ptt.display_transmitting"));
                if (_config.SpeechEnabled)
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.announce_transmitting"), VerbosityLevel.Critical, interrupt: true);
                SpeakKeyDownExtra(); // armed test tone, etc. — always speaks
                _healthReflectedWarned = false;
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

            StopAllTimers();
            _alcZeroConsecutiveSeconds = 0;
            _healthSilentMicWarned = false;
            _healthAlcHighWarned = false;
            _healthReflectedWarned = false;
            _healthTxSeconds = 0;
            _healthLockSeconds = 0;

            _updateStatusDisplay?.Invoke("");
            // forceSpeech marks the three paths that are NOT a normal unkey:
            // transmit timeout, hard kill, and ALC release. Those are safety
            // outcomes and get the flushing tier; an ordinary release does not
            // need to tear down the queue.
            if ((forceSpeech || _config.SpeechEnabled) && !string.IsNullOrEmpty(speechMessage))
                ScreenReaderOutput.Speak(
                    speechMessage,
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
            if (State == PttState.Idle)
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

            // TX health monitor — warn after 5 seconds of locked TX
            _healthLockSeconds++;
            if (_healthLockSeconds >= 5)
            {
                // Silent mic: the loudest moment of transmit audio over the whole
                // locked window never rose above the floor. SC_MIC reflects PC
                // audio AND the analog mic; the peak-hold ignores the quiet gaps
                // between words. (Old code read MicData — the COD-/MIC meter, dead
                // for PC audio — and compared dBFS against a linear 0.01, so it
                // cried wolf on every PC-audio transmit.)
                if (!_healthSilentMicWarned && rig.ScMicMaxDb < SilentMicDbfs)
                {
                    _healthSilentMicWarned = true;
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.check_microphone"), VerbosityLevel.Critical);
                    Tracing.TraceLine($"PTT: Health warning — silent mic (SC_MIC peak {rig.ScMicMaxDb:F1} dBFS)", TraceLevel.Info);
                }

                if (!_healthAlcHighWarned && rig.SwAlcDb > AlcHotDbfs)
                {
                    _healthAlcHighWarned = true;
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.ptt.mic_level_too_high"), VerbosityLevel.Critical);
                    Tracing.TraceLine($"PTT: Health warning — ALC pegging (SW ALC {rig.SwAlcDb:F1} dBFS)", TraceLevel.Info);
                }
            }
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
            float forward = rig.ForwardPowerWatts;
            float reflected = rig.ReflectedPowerWatts;

            if (!TransmitSafety.ShouldWarnReflected(
                    forward, reflected, _healthTxSeconds,
                    rig.ATUTuneInProgress, _healthReflectedWarned))
                return;

            _healthReflectedWarned = true;

            float back = TransmitSafety.ReflectedFractionOf(forward, reflected);
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
            ScreenReaderOutput.Speak(
                TransmitSafety.ReflectedWarningText(back, antenna, rig.DummyLoadMode),
                Radios.Speech.SpeechIntent.Urgent,
                VerbosityLevel.Critical);
            Tracing.TraceLine(
                $"PTT: Health warning — reflected power {back * 100f:F0}% "
                + $"(fwd {forward:F1} W, refl {reflected:F2} W, "
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
