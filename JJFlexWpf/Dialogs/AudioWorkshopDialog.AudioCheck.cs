using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, TX Audio tab: the Audio Check section and the session
/// engine behind it. Every keying path here rides the PTT safety
/// controller; nothing in this file sets Transmit directly.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Audio Check Section

    private void BuildAudioCheckSection()
    {
        AddRadioSection(HearYourselfContent, "Audio Check");

        // Order here is Start button first, then the live reading —
        // reordered by the Threads Track (2026-08-12, from Noel's field
        // report): during a check, focus sits on Mic Gain, and the old
        // reading-before-button placement left the reading several
        // Shift+Tabs away during the one activity that needs it. The tab
        // ring is now Start, reading, Mic Gain (see ApplyTxAudioTabOrder),
        // so the reading sits between the two controls a running check
        // actually uses and is one key from either.
        _startCheckButton = new Button
        {
            Content = "Start Audio Check",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_startCheckButton, "Start Audio Check");
        AutomationProperties.SetAcceleratorKey(_startCheckButton, "Ctrl+Enter");
        _startCheckButton.Click += (s, e) => ToggleAudioCheck();
        AddToSection(HearYourselfContent, _startCheckButton);

        // The live mic reading, as a read-only EDIT (Noel, 2026-08-11). An
        // edit is focusable and review-readable where a label gets skipped;
        // and because the value lives somewhere focusable, the screen
        // reader's own read-current-control command IS the "speak my
        // level" feature — no app hotkey needed.
        _micReadingBox = new TextBox
        {
            Text = "Mic audio: transmit to measure",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Margin = new Thickness(2),
            FontSize = 12
        };
        // Static accessible name, set ONCE. The 2 Hz refresh touches only
        // the text — no name changes, no live region — so NVDA stays quiet
        // while the value moves and a review command always reads fresh.
        // Same lesson Track A learned on the Home expander field.
        AutomationProperties.SetName(_micReadingBox, "Mic audio reading");
        AddToSection(HearYourselfContent, _micReadingBox);

        // THE GAIN SITS HERE, one stop from the reading above and the Start
        // button above that. It is built in this file rather than in the
        // Microphone section because those three controls are ONE LOOP:
        // speak, read what arrived, nudge, speak again. Splitting TX Audio
        // into three categories on 2026-08-25 would have put the gain in a
        // different category from its own measurement, and Tab does not cross
        // a category — every nudge would have cost a Ctrl+Tab each way.
        //
        // Only one of the two is ever visible: Mic Gain when the radio's own
        // jack is the source, the Windows input level when this computer is.
        // Whichever applies is the one the ring lands on (ApplyTxAudioTabOrder).

        _micGainControl = MakeValue("Mic Gain", 0, 100, 1);
        _micGainControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.MicGain = v;
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tx.mic_gain", ("value", v)), VerbosityLevel.Terse);
            }
        };
        JJFlexHelp.SetText(_micGainControl,
            "How hard the radio listens to its microphone jack. Run a mic "
            + "check, speak the way you actually operate, and nudge this "
            + "until the verdict says Good. Hot or Clipping means come down; "
            + "Quiet means come up. Small steps — a few points at a time.");
        AddRadioControl(HearYourselfContent, _micGainControl);

        // The PC-source stand-in for Mic Gain (Track PC Gain, 2026-08-13).
        // Hiding the jack controls on PC audio left a hole where the gain
        // was, and Noel asked for the obvious thing to fill it: "why not
        // still have computer mic adjustment available where mic gain is
        // when PC audio is selected." So the section always offers the gain
        // that actually applies — stage one lives on the computer when the
        // computer is the source. Bound in BindPcLevel, which reads the
        // saved device name from audioDevices.xml and matches it through
        // Core Audio only: this dialog must never enumerate PortAudio while
        // a radio connection may be live (see BuildDeviceSection).
        _pcLevelControl = MakeValue("Windows Input Level", 0, 100, 1);
        _pcLevelControl.Visibility = Visibility.Collapsed;
        _pcLevelControl.IsEnabled = false;
        _pcLevelControl.ValueChanged += (s, v) =>
        {
            var level = _pcMicLevel;
            if (level == null) return;
            try { level.Percent = v; }
            catch (Exception ex) { PcLevelFailed(ex); }
            // No app speech here, deliberately: the control announces its
            // own value on every adjustment, and repeating it would be the
            // same double-speak the Audio Devices sliders were built without.
        };
        JJFlexHelp.SetText(_pcLevelControl,
            "Stage one of your transmit audio when this computer's mic is the "
            + "source: Windows' own capture level for that microphone. Set it "
            + "with the mic check the same way as Mic Gain — capture cleanly "
            + "here first, then let the radio's Processing controls shape the "
            + "result.");
        AddToSection(HearYourselfContent, _pcLevelControl);

        // Read-only EDIT rather than a label, same reasoning as the device
        // reading above: focusable, review-readable, and the screen reader's
        // own read-current-control command speaks it without an app hotkey.
        _pcLevelNote = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2),
            MinWidth = 300,
            Visibility = Visibility.Collapsed
        };
        AddToSection(HearYourselfContent, _pcLevelNote);

        _listenMethodControl = MakeCycle("Listen method",
            new[] { "Monitor", "Record and play back" });
        _listenMethodControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            SavePerRadioPrefs();
            // The control announces its own value through the accessibility
                // tree; this adds only what the tree cannot carry.; add the remote
            // advisory only where it matters.
            if (idx == (int)AudioCheckListenMethods.Monitor && _rig?.RemoteRig == true)
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.remote_monitor_advice"),
                    VerbosityLevel.Terse);
        };
        AddToSection(HearYourselfContent, _listenMethodControl);

        // Track C-2 (Noel at the radio, 2026-08-11: "you have it at 10
        // watts. If you have no antenna, that's a bit high"): the check
        // defaults to DUMMY LOAD, not low power. Every meter the check
        // reads sits upstream of the power amplifier — proven live, a tone
        // at -10 dBFS read -11 on SC_MIC at zero watts — and with a tone
        // armed, a transmitting check puts a steady carrier on whatever
        // frequency the operator is tuned to. Low power remains for the
        // separate, deliberate act of confirming RF leaves the radio, with
        // the cap finally choosable ("so I can change it to 1 if I need
        // to").
        _checkPowerControl = MakeCycle("Transmit power during checks",
            new[] { "Dummy load, no RF", "Low power" });
        _checkPowerControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            UpdateCheckWattsVisibility();
            SavePerRadioPrefs();
        };
        AddToSection(HearYourselfContent, _checkPowerControl);

        _checkWattsControl = new ValueFieldControl();
        _checkWattsControl.Setup("Low power level", 1, 100, 1, 10, 0, "watts");
        _checkWattsControl.Visibility = Visibility.Collapsed; // dummy load is the default
        _checkWattsControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            SavePerRadioPrefs();
        };
        AddToSection(HearYourselfContent, _checkWattsControl);

        _playTakeButton = new Button
        {
            Content = "Play last take",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_playTakeButton, "Play last take");
        _playTakeButton.Click += (s, e) => PlayLastTake();
        AddToSection(HearYourselfContent, _playTakeButton);

        // Loopback check — real RF through the transverter port, inside one
        // radio, no antennas. Doubles as a transmitter self-test: "check my
        // audio" and "is my radio actually transmitting" are the same button.
        // Hidden (out of tab order) on radios that can't do it; the info line
        // below explains why.
        _loopbackButton = new Button
        {
            Content = "Loopback Check (transverter port)",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_loopbackButton, "Loopback Check, transverter port");
        _loopbackButton.Click += (s, e) => StartLoopbackCheck();
        AddToSection(HearYourselfContent, _loopbackButton);

        _loopbackInfo = new TextBlock
        {
            Text = "",
            Margin = new Thickness(2, 2, 2, 4),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        AddToSection(HearYourselfContent, _loopbackInfo);
    }

    /// <summary>
    /// Show the loopback button on capable radios; on the rest, show a
    /// de-emphasized explanation instead (the button stays out of the tab
    /// order — house rule for unsupported controls).
    /// </summary>
    private void UpdateLoopbackAvailability()
    {
        if (_loopbackButton == null || _loopbackInfo == null) return;
        bool supported = _rig?.LoopbackSupported == true;
        _loopbackButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (supported)
        {
            _loopbackInfo.Visibility = Visibility.Collapsed;
        }
        else
        {
            string reason = _rig?.LoopbackUnavailableReason ?? "No radio connected";
            string text = $"Loopback check not available: {reason}.";
            if (_loopbackInfo.Text != text)
            {
                _loopbackInfo.Text = text;
                AutomationProperties.SetName(_loopbackInfo, text);
            }
            _loopbackInfo.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The Loopback Check: apply the verified recipe (full duplex on, TX to
    /// the XVT port, ears slice on the same port, 1 watt, monitor off), then
    /// key through the same PttSafetyController path as every other check.
    /// Teardown restores every saved value and removes the ears slice.
    /// </summary>
    private void StartLoopbackCheck()
    {
        if (_session != null && _session.Active)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.stop_current_first"),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }
        if (_rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical);
            return;
        }
        var ptt = PttControllerSource?.Invoke();
        if (ptt == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.radio_not_powered_on"), VerbosityLevel.Critical);
            return;
        }
        if (ptt.IsTransmitting)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.already_transmitting"),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        if (!_rig.StartLoopbackArrangement())
        {
            string reason = _rig.LoopbackUnavailableReason;
            if (string.IsNullOrEmpty(reason)) reason = Lexicon.Get("audio.check.loopback_no_free_slice");
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.check.loopback_setup_failed", ("reason", reason)),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        // Power mode is irrelevant here — the loopback arrangement owns
        // power (1 W into the transverter port) and the session's power
        // handling is bypassed entirely in loopback mode.
        var session = new AudioCheckSession(this, _rig, ptt,
            AudioCheckListenMethods.Monitor, AudioCheckPowerModes.LowPower, 1,
            loopback: true);
        if (session.Start())
        {
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            _micGainControl?.Focus();
        }
        else
        {
            // Keying refused — take the arrangement back down.
            string trouble = _rig.EndLoopbackArrangement();
            if (!string.IsNullOrEmpty(trouble))
                ScreenReaderOutput.Speak(trouble, VerbosityLevel.Terse);
        }
    }

    /// <summary>
    /// Start button / Command Finder entry point. Starts a session when idle,
    /// stops the current one when active.
    /// </summary>
    public void ToggleAudioCheck()
    {
        if (_session != null && _session.Active)
        {
            _session.StopCheck();
            return;
        }

        if (_rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical);
            return;
        }

        var ptt = PttControllerSource?.Invoke();
        if (ptt == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.radio_not_powered_on"), VerbosityLevel.Critical);
            return;
        }
        if (ptt.IsTransmitting)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.already_transmitting"),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        var method = (AudioCheckListenMethods)(_listenMethodControl?.SelectedIndex ?? 0);
        var powerMode = (AudioCheckPowerModes)(_checkPowerControl?.SelectedIndex
            ?? (int)AudioCheckPowerModes.DummyLoad);
        int lowPowerWatts = Math.Clamp(_checkWattsControl?.Value ?? 10, 1, 100);

        var session = new AudioCheckSession(this, _rig, ptt, method, powerMode, lowPowerWatts);
        if (session.Start())
        {
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            // Land on Mic Gain — arrows adjust immediately, one Shift+Tab
            // reads the live mic reading, one more reaches Stop (the
            // three-stop check cluster, see ApplyTxAudioTabOrder).
            _micGainControl?.Focus();
        }
    }

    private void SetStartButtonLabel(string label)
    {
        if (_startCheckButton == null) return;
        _startCheckButton.Content = label;
        AutomationProperties.SetName(_startCheckButton, label);
    }

    /// <summary>
    /// Session ended (any path) — restore the button label, and if a
    /// loopback arrangement is up, tear it down and say so. Runs on every
    /// exit path because every exit path funnels through the session's End.
    /// </summary>
    private void OnSessionEnded()
    {
        SetStartButtonLabel("Start Audio Check");
        if (_rig != null && _rig.LoopbackArranged)
        {
            string trouble = _rig.EndLoopbackArrangement();
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.check.loopback_ended") + " " + trouble,
                VerbosityLevel.Terse);
        }
    }

    private void PlayLastTake()
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical);
            return;
        }
        if (_session != null && _session.EscapeStopsTransmit)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.still_transmitting"),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }
        if (_rig.SlicePlayOn)
        {
            _rig.SlicePlayOn = false;
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.playback_stopped"), VerbosityLevel.Terse, interrupt: true);
            return;
        }
        if (!_rig.SlicePlayEnabled)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.no_recording_yet"),
                VerbosityLevel.Terse, interrupt: true);
            return;
        }
        _rig.SlicePlayOn = true;
        ScreenReaderOutput.Speak(Lexicon.Get("audio.check.playing_take"), VerbosityLevel.Terse, interrupt: true);
    }

    #endregion

    #region Audio Check Session engine

    /// <summary>
    /// One "hear yourself" session: key the transmitter through the PTT
    /// safety controller, hold the adjust-and-listen loop open, and restore
    /// every state the session changed on the way out — unconditionally, on
    /// every exit path (Escape, Stop button, timeout, hard kill, ALC
    /// release, dialog close, radio disconnect).
    ///
    /// Safety architecture: the controller owns ALL keying and safety timers
    /// (warning ladder, license lockout, 15-minute hard kill). This class
    /// adds only session bookkeeping on a 1-second watcher: spoken elapsed
    /// reminders, the record/auto-play flow, state restoration, and the
    /// hardware-keying honesty check. It never keys the radio itself and
    /// never builds a second safety timer stack — the 3-minute check timeout
    /// rides the controller's SessionTimeoutOverrideSeconds hook.
    /// </summary>
    private sealed class AudioCheckSession
    {
        private enum Phase { Idle, Keyed, AwaitPlayback, Done }

        private readonly AudioWorkshopDialog _owner;
        private readonly PttSafetyController _ptt;
        private readonly AudioCheckListenMethods _method;
        private readonly AudioCheckPowerModes _powerMode;
        private readonly int _lowPowerWatts;
        private readonly bool _loopback;
        private readonly DispatcherTimer _watcher;

        private Phase _phase = Phase.Idle;
        private DateTime _keyedAt;
        private int _lastMinuteSpoken;
        private bool _bufferWarned;
        private bool _noKeyWarned;
        private bool _hardwareWarned;
        private int _postUnkeyTicks;
        private int _awaitPlaybackTicks;

        // State the session changed and must restore.
        private int _savedPower;
        private bool _powerTouched;
        private bool _monitorTouched; // we only ever turn monitor ON; restore = off
        private bool _dummyEngaged;   // WE turned dummy load on; disable restores power
        private int _dummySavedPower; // pre-engage watts, for the spoken restore line

        private const int CheckTimeoutSeconds = 180;   // 3-minute soft timeout
        private const int RecordBufferSeconds = 120;   // verified live cap

        public bool Active { get; private set; }

        /// <summary>
        /// True while Escape must unkey rather than close the dialog. Covers
        /// the hardware-keying edge: if a hardware PTT line holds the rig in
        /// TX after software unkey, Escape keeps warning instead of closing.
        /// </summary>
        public bool EscapeStopsTransmit =>
            Active && (_ptt.IsTransmitting || Rig?.Transmit == true);

        private FlexBase? Rig => _owner._rig;

        public AudioCheckSession(AudioWorkshopDialog owner, FlexBase rig,
            PttSafetyController ptt, AudioCheckListenMethods method,
            AudioCheckPowerModes powerMode, int lowPowerWatts,
            bool loopback = false)
        {
            _owner = owner;
            _ptt = ptt;
            _method = method;
            _powerMode = powerMode;
            _lowPowerWatts = Math.Clamp(lowPowerWatts, 1, 100);
            _loopback = loopback;
            _watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watcher.Tick += Watcher_Tick;
        }

        /// <summary>
        /// Start the check. Returns false (with everything restored and a
        /// spoken explanation) when keying did not happen.
        /// </summary>
        public bool Start()
        {
            var rig = Rig;
            if (rig == null) return false;

            // Loopback mode: the arrangement (FlexBase) already owns power
            // (1 W), monitor (off), antennas and full duplex — the session
            // must not double-manage them.
            bool monitorTurnedOn = false;
            bool recorderAlreadyRunning = false;
            int effectivePower = _loopback ? 1 : rig.XmitPower;

            if (!_loopback)
            {
                // Power handling BEFORE keying. Dummy load (the default)
                // rides FlexBase.DummyLoadMode — it zeroes transmit and
                // tune power and restores both on disable, and the PTT
                // safety controller already skips the ALC auto-release
                // while it is active. If the operator engaged dummy load
                // themselves (the Transmit menu toggle), we leave it
                // theirs: the check neither re-engages nor releases it.
                int currentPower = rig.XmitPower;
                if (_powerMode == AudioCheckPowerModes.DummyLoad)
                {
                    if (!rig.DummyLoadMode)
                    {
                        _dummySavedPower = currentPower;
                        rig.DummyLoadMode = true;
                        _dummyEngaged = true;
                    }
                    effectivePower = 0;
                }
                else if (!rig.DummyLoadMode && currentPower > _lowPowerWatts)
                {
                    // Low power is a CAP: it only ever lowers power, never
                    // raises it — and it cannot override an active dummy
                    // load (touching XmitPower under dummy load would
                    // corrupt its saved-power restore).
                    _savedPower = currentPower;
                    _powerTouched = true;
                    rig.XmitPower = _lowPowerWatts;
                    effectivePower = _lowPowerWatts;
                }

                if (_method == AudioCheckListenMethods.Monitor &&
                    rig.Monitor != FlexBase.OffOnValues.on)
                {
                    _monitorTouched = true;
                    monitorTurnedOn = true;
                    rig.Monitor = FlexBase.OffOnValues.on;
                }

                if (_method == AudioCheckListenMethods.RecordPlayback)
                {
                    // Re-arm race guard (a live re-arm nearly wiped an
                    // operator's takes): stop playback before arming, and
                    // never blind-toggle a recorder that is already running.
                    if (rig.SlicePlayOn) rig.SlicePlayOn = false;
                    if (rig.SliceRecordOn)
                        recorderAlreadyRunning = true;
                    else
                        rig.SliceRecordOn = true;
                }
            }

            // 3-minute soft timeout for the check — the controller's ladder
            // and hard kill continue to apply unchanged.
            _ptt.SessionTimeoutOverrideSeconds = CheckTimeoutSeconds;

            // Key through the controller. Its own key-down announcement fires
            // first; the full safety line follows with interrupt so the
            // complete utterance the operator hears is frequency, power,
            // source, and how to stop. Key-down is never silent even if this
            // class faults between the two.
            _ptt.ToggleLock();
            if (!_ptt.IsTransmitting)
            {
                // Refused (license lockout spoke its own warning, or no
                // power). Roll back everything we touched.
                _ptt.SessionTimeoutOverrideSeconds = null;
                RestoreChangedState(rig, speak: false);
                if (!_loopback && _method == AudioCheckListenMethods.RecordPlayback && !recorderAlreadyRunning)
                    rig.SliceRecordOn = false;
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.could_not_start"),
                    VerbosityLevel.Critical);
                return false;
            }

            var line = new StringBuilder();
            if (_loopback)
            {
                // HONESTY (ratified product framing): this is real RF through
                // a massively overloaded receiver. It proves presence,
                // processing, and rough shape — never claim a faithful
                // off-air listen. An SDR on a real antenna is ground truth.
                line.Append(Lexicon.Get("audio.check.loopback_line",
                    ("freq", FormatMHz(rig.TXFrequency)),
                    ("source", SourceFriendlyName(rig.MicSource))));
                line.Append(' ').Append(Lexicon.Get("audio.check.loopback_honesty"));
                if (rig.LoopbackDriveManaged)
                    line.Append(' ').Append(Lexicon.Get("audio.check.loopback_drive_reduced"));
                line.Append(' ').Append(Lexicon.Get("audio.check.loopback_proves_chain"));
            }
            else
            {
                // The safety line names the MODE, not just the number
                // (Noel, 2026-08-11): "transmitting at zero watts" is
                // technically true under dummy load and genuinely
                // confusing — it invites the operator to wonder what
                // failed. Checked against the rig's live DummyLoadMode,
                // not our own flag, so an operator-engaged dummy load is
                // named just as honestly as one this check engaged.
                if (rig.DummyLoadMode)
                {
                    line.Append(Lexicon.Get("audio.check.line_dummy_load",
                        ("freq", FormatMHz(rig.TXFrequency)),
                        ("source", SourceFriendlyName(rig.MicSource))));
                }
                else
                {
                    line.Append(Lexicon.Get("audio.check.line_transmitting",
                        ("freq", FormatMHz(rig.TXFrequency)),
                        ("watts", effectivePower),
                        ("unit", effectivePower == 1
                            ? Lexicon.Get("audio.unit.watt")
                            : Lexicon.Get("audio.unit.watts")),
                        ("source", SourceFriendlyName(rig.MicSource))));
                    if (_powerTouched)
                        line.Append(' ').Append(Lexicon.Get("audio.check.power_reduced_for_check",
                            ("watts", _savedPower)));
                }
                if (monitorTurnedOn)
                    line.Append(' ').Append(Lexicon.Get("audio.check.monitor_on"));
                if (recorderAlreadyRunning)
                    line.Append(' ').Append(Lexicon.Get("audio.check.recorder_already_running"));
                else if (_method == AudioCheckListenMethods.RecordPlayback)
                    line.Append(' ').Append(Lexicon.Get("audio.check.recording_plays_back"));
                if (rig.RemoteRig && _method == AudioCheckListenMethods.Monitor)
                    line.Append(' ').Append(Lexicon.Get("audio.check.remote_monitor_note"));
            }
            // Audio Track C: when the test tone is riding this transmission,
            // the safety line says so — including the passband warning if the
            // tone sits outside the TX filter (a check that transmits nothing
            // must never sound like a check).
            string? toneLine = _owner.BuildToneAnnouncement();
            if (!string.IsNullOrEmpty(toneLine))
                line.Append(' ').Append(toneLine);
            line.Append(' ').Append(Lexicon.Get("audio.check.escape_stops"));
            ScreenReaderOutput.Speak(line.ToString(), VerbosityLevel.Critical, interrupt: true);

            _keyedAt = DateTime.UtcNow;
            _phase = Phase.Keyed;
            Active = true;
            _watcher.Start();
            return true;
        }

        /// <summary>
        /// Operator-initiated stop (Escape stage one, Stop button). Unkeys
        /// through the controller and restores session state. The dialog
        /// stays open.
        /// </summary>
        public void StopCheck()
        {
            if (!Active) return;
            if (_phase == Phase.Keyed)
                HandleUnkey(external: false);
        }

        /// <summary>
        /// Terminal teardown: dialog closing or radio gone. Unkeys if still
        /// keyed, restores what can be restored, stops playback, ends the
        /// session. Safe to call repeatedly.
        /// </summary>
        public void ForceEnd(string reason)
        {
            if (!Active)
            {
                _watcher.Stop();
                return;
            }

            var rig = Rig;
            if (_phase == Phase.Keyed)
            {
                if (_ptt.IsTransmitting) _ptt.EscapeUnlock();
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.transmit_off"), VerbosityLevel.Critical);
                if (rig != null && _method == AudioCheckListenMethods.RecordPlayback)
                    rig.SliceRecordOn = false;
            }
            if (rig != null)
            {
                RestoreChangedState(rig, speak: false);
                if (rig.SlicePlayOn) rig.SlicePlayOn = false;
            }
            End(reason);
        }

        private void End(string? reason)
        {
            _phase = Phase.Done;
            Active = false;
            _watcher.Stop();
            _ptt.SessionTimeoutOverrideSeconds = null;
            if (!string.IsNullOrEmpty(reason))
                ScreenReaderOutput.Speak(reason, VerbosityLevel.Terse);
            _owner.OnSessionEnded();
        }

        /// <summary>
        /// The unkey path every stop flows through. Announces key-up
        /// unconditionally (safety-critical, not polish — live wire-keying
        /// once left an operator transmitting unaware), restores power and
        /// monitor, and hands record sessions to the auto-play flow.
        /// </summary>
        private void HandleUnkey(bool external)
        {
            if (!external && _ptt.IsTransmitting)
                _ptt.EscapeUnlock(); // controller speaks its own key-up line

            var rig = Rig;
            var msg = new StringBuilder(Lexicon.Get("audio.check.transmit_off"));

            if (rig != null)
            {
                if (_dummyEngaged)
                {
                    // Disabling dummy load restores transmit AND tune power
                    // inside FlexBase; the spoken value is the pre-engage
                    // reading (the live getter may not have echoed yet).
                    rig.DummyLoadMode = false;
                    msg.Append(' ').Append(Lexicon.Get("audio.check.dummy_load_released",
                        ("watts", _dummySavedPower)));
                    _dummyEngaged = false;
                }
                if (_powerTouched)
                {
                    rig.XmitPower = _savedPower;
                    msg.Append(' ').Append(Lexicon.Get("audio.check.power_restored",
                        ("watts", _savedPower)));
                    _powerTouched = false;
                }
                if (_monitorTouched)
                {
                    rig.Monitor = FlexBase.OffOnValues.off;
                    msg.Append(' ').Append(Lexicon.Get("audio.check.monitor_restored_off"));
                    _monitorTouched = false;
                }
                if (_method == AudioCheckListenMethods.RecordPlayback)
                {
                    rig.SliceRecordOn = false;
                    msg.Append(' ').Append(Lexicon.Get("audio.check.playing_take_shortly"));
                }

                // The verdict — the whole point of the check. Peak SC_MIC over
                // the keyed window (reset at key-down via ToggleLock); honest for
                // PC audio AND the analog mic. -140 guards "no meter yet".
                // The check is the longest deliberate transmit the operator
                // makes, so it is also where the room observation is most
                // likely to have enough audio to be worth saying.
                if (rig.ScMicMaxDb > -140f)
                {
                    string report = MicAudioReport.Compose(
                        rig, Lexicon.Get("audio.check.mic_audio_was"), rig.ScMicMaxDb, live: false);
                    msg.Append(' ').Append(report);
                    if (!report.EndsWith(".")) msg.Append('.');
                }
            }

            // Unconditional key-up announcement (interrupt false so the
            // controller's own "Receiving" isn't stomped when enabled).
            ScreenReaderOutput.Speak(msg.ToString(), VerbosityLevel.Critical);

            _postUnkeyTicks = 0;
            if (_method == AudioCheckListenMethods.RecordPlayback && rig != null)
            {
                _phase = Phase.AwaitPlayback;
                _awaitPlaybackTicks = 0;
            }
            else
            {
                // Stay alive briefly for the hardware-keying honesty check.
                _phase = Phase.AwaitPlayback; // reuse the post-unkey path
                _awaitPlaybackTicks = int.MinValue; // sentinel: no playback wanted
            }
        }

        private void Watcher_Tick(object? sender, EventArgs e)
        {
            var rig = Rig;
            if (rig == null)
            {
                ForceEnd(Lexicon.Get("audio.check.radio_disconnected"));
                return;
            }

            switch (_phase)
            {
                case Phase.Keyed:
                    TickKeyed(rig);
                    break;
                case Phase.AwaitPlayback:
                    TickPostUnkey(rig);
                    break;
                default:
                    _watcher.Stop();
                    break;
            }
        }

        private void TickKeyed(FlexBase rig)
        {
            // External unkey: timeout, hard kill, ALC auto-release, or a PTT
            // chord outside this dialog. The controller already spoke; we
            // restore state and finish the flow.
            if (!_ptt.IsTransmitting)
            {
                HandleUnkey(external: true);
                return;
            }

            var elapsed = (DateTime.UtcNow - _keyedAt).TotalSeconds;

            // Radio never actually keyed (interlock, wiring): say so once.
            if (!_noKeyWarned && elapsed >= 3 && !rig.Transmit)
            {
                _noKeyWarned = true;
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.radio_did_not_key"),
                    VerbosityLevel.Critical, interrupt: true);
            }

            // Spoken elapsed reminders, once per minute.
            int minutes = (int)(elapsed / 60);
            if (minutes > _lastMinuteSpoken)
            {
                _lastMinuteSpoken = minutes;
                ScreenReaderOutput.Speak(
                    minutes == 1
                        ? Lexicon.Get("audio.check.elapsed_one_minute")
                        : Lexicon.Get("audio.check.elapsed_minutes", ("minutes", minutes)),
                    VerbosityLevel.Terse);
            }

            // Record buffer is a 120-second ring — warn before the oldest
            // material starts falling off.
            if (_method == AudioCheckListenMethods.RecordPlayback &&
                !_bufferWarned && elapsed >= RecordBufferSeconds - 10)
            {
                _bufferWarned = true;
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.buffer_nearly_full"),
                    VerbosityLevel.Terse);
            }
        }

        private void TickPostUnkey(FlexBase rig)
        {
            _postUnkeyTicks++;

            // Hardware-keying honesty: software unkey cannot override a
            // hardware PTT line (front-panel mic, ACC, rear RCA). If the rig
            // is still transmitting after our unkey, say exactly that.
            if (!_hardwareWarned && _postUnkeyTicks >= 2 && rig.Transmit && !_ptt.IsTransmitting)
            {
                _hardwareWarned = true;
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.hardware_keying_active", ("source", rig.PttSourceName)),
                    VerbosityLevel.Critical, interrupt: true);
                return; // keep watching until the hardware line releases
            }
            if (_hardwareWarned && rig.Transmit)
                return; // still keyed by hardware — stay alive, Escape keeps warning
            if (_hardwareWarned && !rig.Transmit)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.transmitter_released"), VerbosityLevel.Critical);
                _hardwareWarned = false;
            }

            if (_awaitPlaybackTicks == int.MinValue)
            {
                // No playback wanted — just the post-unkey grace, then done.
                if (_postUnkeyTicks >= 3)
                    End(Lexicon.Get("audio.check.ended"));
                return;
            }

            // Auto-play-on-unkey (the default flow, performed live: unkey to
            // playback within a second — never demands talking and listening
            // at once).
            _awaitPlaybackTicks++;
            if (rig.SlicePlayEnabled)
            {
                rig.SlicePlayOn = true;
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.playing_take_full_chain"),
                    VerbosityLevel.Terse);
                End(null);
            }
            else if (_awaitPlaybackTicks >= 6)
            {
                End(Lexicon.Get("audio.check.no_recording_available"));
            }
        }

        private void RestoreChangedState(FlexBase rig, bool speak)
        {
            if (_dummyEngaged)
            {
                rig.DummyLoadMode = false;
                _dummyEngaged = false;
                if (speak)
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("audio.check.dummy_load_released", ("watts", _dummySavedPower)),
                        VerbosityLevel.Terse);
            }
            if (_powerTouched)
            {
                rig.XmitPower = _savedPower;
                _powerTouched = false;
                if (speak)
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("audio.check.power_restored", ("watts", _savedPower)),
                        VerbosityLevel.Terse);
            }
            if (_monitorTouched)
            {
                rig.Monitor = FlexBase.OffOnValues.off;
                _monitorTouched = false;
                if (speak)
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.check.monitor_restored_off"), VerbosityLevel.Terse);
            }
        }

        private static string FormatMHz(ulong hz)
        {
            return Lexicon.Get("audio.frequency_megahertz", ("mhz", $"{hz / 1e6:0.000###}"));
        }

        private static string SourceFriendlyName(string micSource)
        {
            if (string.IsNullOrEmpty(micSource)) return Lexicon.Get("audio.source.unknown");
            return micSource.Equals("PC", StringComparison.OrdinalIgnoreCase)
                ? Lexicon.Get("audio.source.this_computer")
                : Lexicon.Get("audio.source.named_input", ("source", micSource));
        }
    }

    #endregion
}
