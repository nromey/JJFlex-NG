using System;
using System.Collections.Generic;
using System.IO;
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
    private bool _loopbackStarting;
    private string? _lastAudioCheckSource;
    private readonly AudioCheckStartGate _checkStarts = new();

    private void CancelCheckStart()
    {
        _checkStarts.Cancel();
        _iqCancellation?.Cancel();
    }

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

        // "Tell me how my sound changed compared to my last take" comes just
        // before the listen method (Noel, 2026-10-22 07:01).
        BuildComparisonChoice();

        _listenMethodControl = MakeCycle("Listen method",
            new[] { "Monitor", "Record and play back", Lexicon.Get("audio.iq.listen_method") });
        _listenMethodControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            SavePerRadioPrefs();
            SyncPlayTakeLabel();
            // The control announces its own value through the accessibility
                // tree; this adds only what the tree cannot carry.; add the remote
            // advisory only where it matters.
            if (idx == (int)AudioCheckListenMethods.Monitor && _rig?.RemoteRig == true)
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.remote_monitor_advice"),
                    VerbosityLevel.Terse);
        };
        JJFlexHelp.SetText(_listenMethodControl, Lexicon.Get("audio.iq.listen_method_help"));
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
            Margin = new Thickness(2),
            Visibility = Visibility.Collapsed,
            IsTabStop = false
        };
        AutomationProperties.SetName(_playTakeButton, "Play last take");
        JJFlexHelp.SetText(_playTakeButton,
            "Plays the slice recording with Record and play back, or the off-air take "
            + "with Off the air. Press again to stop.");
        _playTakeButton.Click += (s, e) => PlayLastTake();
        _playTakeButton.GotFocus += (s, e) => SyncPlayTakeLabel();
        AddToSection(HearYourselfContent, _playTakeButton);
        SyncPlayTakeLabel();

        // Follow local playback so the button says what pressing it will do.
        // The radio-buffer half is followed from the meter tick, which is where
        // every other piece of radio state in this dialog is read.
        RecordingPlayer.StateChanged += OnTakePlaybackStateChanged;

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
        _loopbackButton.Click += async (s, e) =>
        {
            if (_loopbackStarting)
            {
                if (!_checkStarts.IsCancelled)
                {
                    CancelCheckStart();
                    // DRAFT PROSE (#787): awaiting Noel.
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.check.start_cancelled"), VerbosityLevel.Terse);
                }
                return;
            }
            if (_session?.Active == true && _session.IsLoopback) _session.StopCheck();
            else await StartLoopbackCheckAsync();
        };
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
        BuildListenbackControls();
    }

    /// <summary>
    /// Show the loopback button on capable radios; on the rest, show a
    /// de-emphasized explanation instead (the button stays out of the tab
    /// order — house rule for unsupported controls).
    /// </summary>
    private void UpdateLoopbackAvailability()
    {
        UpdateIqAvailability();
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
    /// one XVT port, ears slice on the other receive port, 1 watt, monitor off), then
    /// key through the same PttSafetyController path as every other check.
    /// Teardown restores every saved value and removes the ears slice.
    /// </summary>
    private async Task StartLoopbackCheckAsync()
    {
        if (_checkStarts.IsBusy)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.stop_current_first"),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }
        if (_iqBusy || _iqRun != null) { IqStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
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

        using var start = _checkStarts.TryBegin();
        if (start == null) return;
        var rig = _rig;
        _loopbackStarting = true;
        SetLoopbackButtonLabel(stopping: true);
        try
        {
        if (!await rig.StartLoopbackArrangementAsync())
        {
            string reason = rig.LoopbackUnavailableReason;
            if (string.IsNullOrEmpty(reason))
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.loopback_setup_unconfirmed"),
                    VerbosityLevel.Critical, interrupt: true);
                return;
            }
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.check.loopback_setup_failed", ("reason", reason)),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }
        if (!start.IsCurrent || _iqClosing || !ReferenceEquals(rig, _rig))
        {
            string trouble = await rig.EndLoopbackArrangementAsync();
            if (!string.IsNullOrEmpty(trouble))
                ScreenReaderOutput.Speak(trouble, VerbosityLevel.Critical);
            return;
        }

        // Power mode is irrelevant here — the loopback arrangement owns
        // power (1 W into the transverter port) and the session's power
        // handling is bypassed entirely in loopback mode.
        var session = new AudioCheckSession(this, rig, ptt,
            AudioCheckListenMethods.Monitor, AudioCheckPowerModes.LowPower, 1,
            loopback: true);
        if (await session.StartAsync(start))
        {
            _session = session;
            SetLoopbackButtonLabel(stopping: true);
        }
        else
        {
            // Keying refused — take the arrangement back down.
            string trouble = await rig.EndLoopbackArrangementAsync();
            if (!string.IsNullOrEmpty(trouble))
                ScreenReaderOutput.Speak(trouble, VerbosityLevel.Terse);
        }
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Loopback Check setup: " + ex, System.Diagnostics.TraceLevel.Error);
            if (rig.LoopbackArranged)
                try { await rig.EndLoopbackArrangementAsync(); }
                catch (Exception restore) { JJTrace.Tracing.TraceLine("Loopback Check rollback: " + restore, System.Diagnostics.TraceLevel.Error); }
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.loopback_setup_unconfirmed"),
                VerbosityLevel.Critical, interrupt: true);
        }
        finally
        {
            _loopbackStarting = false;
            if (_session?.Active != true) SetLoopbackButtonLabel(stopping: false);
        }
    }

    /// <summary>
    /// Start button / Command Finder entry point. Starts a session when idle,
    /// stops the current one when active.
    /// </summary>
    public void ToggleAudioCheck()
    {
        if (_checkStarts.IsBusy)
        {
            if (!_checkStarts.IsCancelled)
            {
                CancelCheckStart();
                // DRAFT PROSE (#787): awaiting Noel.
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.start_cancelled"), VerbosityLevel.Terse);
            }
            return;
        }
        if (_iqBusy) { _iqCancellation?.Cancel(); return; }
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

        if (_iqRun != null) { IqStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        _lastAudioCheckSource = null;
        var method = (AudioCheckListenMethods)(_listenMethodControl?.SelectedIndex ?? 0);
        if (method == AudioCheckListenMethods.OffAir) { _ = StartOffAirAsync(ptt); return; }
        var powerMode = (AudioCheckPowerModes)(_checkPowerControl?.SelectedIndex
            ?? (int)AudioCheckPowerModes.DummyLoad);
        int lowPowerWatts = Math.Clamp(_checkWattsControl?.Value ?? 10, 1, 100);

        _ = StartAudioCheckSessionAsync(new AudioCheckSession(this, _rig, ptt, method, powerMode, lowPowerWatts));
    }

    /// <summary>
    /// Start a session and adopt it. Async since #787: Record and play back
    /// waits for the radio to confirm its recorder before keying. A second
    /// Stop while that answer is pending cancels its right to key. All start
    /// doors share admission until the pending start has finished rollback.
    /// </summary>
    private async Task StartAudioCheckSessionAsync(AudioCheckSession session)
    {
        using var start = _checkStarts.TryBegin();
        if (start == null || _session?.Active == true) return;
        SetStartButtonLabel("Stop Audio Check");
        try
        {
            if (await session.StartAsync(start))
            {
                _session = session;
                SetStartButtonLabel("Stop Audio Check");
                // Land on Mic Gain — arrows adjust immediately, one Shift+Tab
                // reads the live mic reading, one more reaches Stop (the
                // three-stop check cluster, see ApplyTxAudioTabOrder).
                _micGainControl?.Focus();
            }
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Audio Check start: " + ex, System.Diagnostics.TraceLevel.Error);
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.could_not_start"), VerbosityLevel.Critical);
        }
        finally
        {
            if (_session?.Active != true) SetStartButtonLabel("Start Audio Check");
        }
    }

    private void SetStartButtonLabel(string label)
    {
        if (_startCheckButton == null) return;
        _startCheckButton.Content = label;
        AutomationProperties.SetName(_startCheckButton, label);
    }

    private void SetLoopbackButtonLabel(bool stopping)
    {
        if (_loopbackButton == null) return;
        _loopbackButton.Content = stopping ? "Stop Loopback Check" : "Loopback Check (transverter port)";
        AutomationProperties.SetName(_loopbackButton, stopping ? "Stop Loopback Check" : "Loopback Check, transverter port");
    }

    /// <summary>
    /// Session ended (any path) — restore the button label, and if a
    /// loopback arrangement is up, tear it down and say so. Runs on every
    /// exit path because every exit path funnels through the session's End.
    /// </summary>
    private async void OnSessionEnded()
    {
        CancelCheckStart();
        UpdateMicReading();
        if (_micReadingBox?.IsVisible == true) _micReadingBox.Focus();
        SetStartButtonLabel("Start Audio Check");
        SetLoopbackButtonLabel(stopping: false);
        // This stopped every recording the Workshop's buttons had started
        // until #787. It no longer stops anything: with one recorder behind
        // every door it would also have stopped the take this check had just
        // begun playing. The Loopback Check's listening slice is stopped by
        // EndLoopbackArrangementAsync below, which already says when that stop
        // was not confirmed, and closing the Workshop stops what the Workshop
        // started (CloseListenbackAsync).
        SyncSliceRecorderUi();
        if (_iqWasOffAir && _iqRun != null) { _ = FinishIqAsync("operator-stop"); return; }
        if (_rig != null && _rig.LoopbackArranged)
        {
            try
            {
                string trouble = await _rig.EndLoopbackArrangementAsync();
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.loopback_ended") + " " + trouble,
                    VerbosityLevel.Terse);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Loopback Check restore: " + ex, System.Diagnostics.TraceLevel.Error);
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.loopback_restore_unconfirmed"),
                    VerbosityLevel.Critical, interrupt: true);
            }
        }
    }

    /// <summary>
    /// The slice the last check recorded on. Also tracks the slice chosen for
    /// playback so a second press stops the same buffer.
    /// </summary>
    private SliceRecorderTarget? _radioTakeTarget;
    private SliceRecorderTarget RadioTakeTarget => _radioTakeTarget ?? SliceRecorderTarget.TransmitSlice;

    /// <summary>
    /// Play the take for the selected listen method, or stop the one playing.
    /// </summary>
    /// <remarks>
    /// The selected method owns the answer: Record and play back chooses a
    /// slice buffer; Off the air chooses an IQ take. Monitor has no play button.
    /// </remarks>
    private void PlayLastTake()
    {
        if ((AudioCheckListenMethods)(_listenMethodControl?.SelectedIndex ?? 0) ==
            AudioCheckListenMethods.Monitor) return;
        // Press again to stop, whichever thing is playing.
        if (RecordingPlayer.IsPlaying)
        {
            RecordingPlayer.Stop();
            return;
        }
        if (_rig != null && _rig.IsSlicePlaying(RadioTakeTarget))
        {
            _ = StopRadioTakeAsync();
            return;
        }

        if (_session != null && _session.EscapeStopsTransmit)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.still_transmitting"),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        var selected = SelectTakeForPlayback();
        switch (selected.Choice)
        {
            case TakePlaybackChoice.Slice:
                _radioTakeTarget = selected.RadioTarget;
                _ = PlayRadioTakeAsync();
                return;
            case TakePlaybackChoice.OffAir:
                _ = PlayIqAsync(selected.IqDirectory, 0);
                return;
            default:
                if (selected.Method == AudioCheckListenMethods.RecordPlayback) SayNoTake("slice");
                else if (selected.Method == AudioCheckListenMethods.OffAir) SayNoTake("off-air");
                return;
        }
    }

    private (AudioCheckListenMethods Method, TakePlaybackChoice Choice,
        SliceRecorderTarget? RadioTarget, string? IqDirectory, DateTime? MadeUtc) SelectTakeForPlayback()
    {
        var method = (AudioCheckListenMethods)(_listenMethodControl?.SelectedIndex
            ?? (int)(_radioCfg?.AudioCheckListenMethod ?? AudioCheckListenMethods.Monitor));
        if (method == AudioCheckListenMethods.Monitor)
            return (method, TakePlaybackChoice.None, null, null, null);
        var (radioTarget, radioTime) = method == AudioCheckListenMethods.RecordPlayback
            ? NewestRadioTake() : (null, null);
        string? iqDirectory = method == AudioCheckListenMethods.OffAir ? NewestIqDirectory() : null;
        DateTime? iqAt = iqDirectory != null && File.Exists(Path.Combine(iqDirectory, "take.txt"))
            ? File.GetLastWriteTimeUtc(Path.Combine(iqDirectory, "take.txt")) : null;
        var choice = TakePlaybackSelector.Choose(method, radioTarget != null, iqDirectory != null);
        DateTime? madeUtc = choice switch
        {
            TakePlaybackChoice.Slice => radioTime,
            TakePlaybackChoice.OffAir => iqAt,
            _ => null
        };
        return (method, choice, radioTarget, iqDirectory, madeUtc);
    }

    private (SliceRecorderTarget? Target, DateTime? MadeUtc) NewestRadioTake()
    {
        if (_rig == null) return (null, null);
        SliceRecorderTarget? fallback = null;
        SliceRecorderTarget? newest = null;
        DateTime? newestAt = null;
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            var target = SliceRecorderTarget.Letter(letter);
            if (!_rig.SliceHasRecording(target)) continue;
            fallback ??= target;
            if (target == RadioTakeTarget) fallback = target;
            DateTime? made = _rig.SliceTakeTime(target);
            if (made != null && (newestAt == null || made > newestAt))
            { newest = target; newestAt = made; }
        }
        return (newest ?? fallback, newestAt);
    }

    private static void SayNoTake(string kind) => ScreenReaderOutput.Speak(
        "There is no " + kind + " take to play.", Radios.Speech.SpeechIntent.Interrupt,
        VerbosityLevel.Terse);

    /// <summary>
    /// Play the radio's take through the one recorder surface (#787), which
    /// checks the radio's reply and pauses VOX for local playback. Success keeps
    /// the take's own sentence, which says it is the radio's take and not a
    /// file (#455); a refusal speaks the recorder's sentence.
    /// </summary>
    private async Task PlayRadioTakeAsync()
    {
        if (_rig == null) return;
        var rig = _rig;
        var target = FixedRecorderTarget(rig, RadioTakeTarget);
        if (_workshopClosing || !ReferenceEquals(rig, _rig)) return;
        RecorderControls.Observe(rig);
        RecorderControls.PlayStartClick();
        var outcome = await rig.StartSlicePlayAsync(target);
        if (outcome.Success)
        {
            if (_workshopClosing || !ReferenceEquals(rig, _rig))
            {
                await RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(target));
                return;
            }
            _workshopPlayTargets.Add(target);
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.playing_take"), Radios.Speech.SpeechIntent.Interrupt,
                VerbosityLevel.Terse, subject: outcome.Subject);
        }
        else RecorderControls.Speak(outcome);
        SyncPlayTakeLabel();
    }

    private async Task StopRadioTakeAsync()
    {
        if (_rig == null) return;
        var target = FixedRecorderTarget(_rig, RadioTakeTarget);
        RecorderControls.Speak(await _rig.StopSlicePlayAsync(target));
        _workshopPlayTargets.Remove(target);
        SyncPlayTakeLabel();
    }

    private readonly HashSet<SliceRecorderTarget> _workshopPlayTargets = new();
    private bool _workshopClosing;

    private static SliceRecorderTarget FixedRecorderTarget(FlexBase rig, SliceRecorderTarget target)
    {
        string letter = rig.SliceRecorderLetter(target);
        return letter.Length > 0 ? SliceRecorderTarget.Letter(letter[0]) : target;
    }

    /// <summary>Stop the audio this Workshop started before another Ctrl+Enter can start a check.</summary>
    private bool StopWorkshopPlayback()
    {
        bool stopped = false;
        if (RecordingPlayer.IsPlaying)
        {
            RecordingPlayer.Stop();
            stopped = true;
        }
        if (_workshopPlayTargets.Count > 0 && _rig != null)
        {
            var targets = new List<SliceRecorderTarget>(_workshopPlayTargets);
            _workshopPlayTargets.Clear();
            foreach (var target in targets)
                _ = RecorderControls.ReportStopFailureAsync(_rig.StopSlicePlayAsync(target));
            stopped = true;
        }
        if (_toneMonitorSounding)
        {
            EarconPlayer.StopTxToneMonitor();
            _toneMonitorSounding = false;
            stopped = true;
        }
        return stopped;
    }

    /// <summary>
    /// Local playback started or stopped. Relabel on the UI thread — the player
    /// raises this from whatever thread noticed, including the audio thread at
    /// end of file.
    /// </summary>
    private void OnTakePlaybackStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(SyncPlayTakeLabel));
    }

    /// <summary>
    /// Make the button say what pressing it will do. Both stores count: a
    /// button that says "Play last take" while a take is playing would be the
    /// third version of this dialog's recurring defect — a control describing
    /// something other than what it does.
    /// </summary>
    private void SyncPlayTakeLabel()
    {
        if (_playTakeButton == null) return;
        bool available = (AudioCheckListenMethods)(_listenMethodControl?.SelectedIndex ?? 0)
            != AudioCheckListenMethods.Monitor;
        _playTakeButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _playTakeButton.IsTabStop = available;
        if (!available) return;
        bool playing = RecordingPlayer.IsPlaying ||
            _rig?.IsSlicePlaying(RadioTakeTarget) == true;
        string label = playing ? "Stop playing" : TakePlaybackSelector.PlayLabel(
            SelectTakeForPlayback().MadeUtc, DateTime.Now);
        if ((_playTakeButton.Content as string) == label &&
            AutomationProperties.GetName(_playTakeButton) == label) return;
        _playTakeButton.Content = label;
        AutomationProperties.SetName(_playTakeButton, label);
    }

    /// <summary>
    /// Stop following the player, and stop any playback this dialog started.
    /// Called from the dialog's teardown: a closed Workshop must not go on
    /// relabelling a button that is gone, and a take must not outlive the
    /// window that played it.
    /// </summary>
    private void DetachTakePlayback()
    {
        CloseListenback();
        RecordingPlayer.StateChanged -= OnTakePlaybackStateChanged;
        StopWorkshopPlayback();
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
    /// still-transmitting honesty check. The controller owns all keying and
    /// the independent 15-minute hard kill. This session alone stops a
    /// Record and play back check at the radio's two-minute buffer limit.
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
        private bool _noKeyWarned;
        private bool _hardwareWarned;
        private int _postUnkeyTicks;
        private int _awaitPlaybackTicks;

        // Record and play back rides the one recorder surface (#787). The
        // slice is fixed at the start by its LETTER, so a slice change during
        // the check still stops and plays the slice that recorded.
        private SliceRecorderTarget _recorderTarget = SliceRecorderTarget.TransmitSlice;
        private Task<RecorderOutcome>? _recordStop;
        private Task<RecorderOutcome>? _playStart;

        // State the session changed and must restore.
        private int _savedPower;
        private bool _powerTouched;
        private bool _monitorTouched; // we only ever turn monitor ON; restore = off
        private bool _dummyEngaged;   // WE turned dummy load on; disable restores power
        private int _dummySavedPower; // pre-engage watts, for the spoken restore line

        // The recorder surface warns at ten seconds remaining. The check's
        // own watcher stops at 120 seconds; ordinary transmit timeout warnings
        // are suppressed during a check, while the independent hard kill and
        // live safety monitoring remain active.

        public bool Active { get; private set; }
        public bool IsLoopback => _loopback;

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
        public async Task<bool> StartAsync(AudioCheckStartGate.Lease start)
        {
            var rig = Rig;
            if (rig == null || !start.IsCurrent || _owner._iqClosing || _ptt.IsTransmitting || rig.Transmit) return false;

            // Loopback mode: the arrangement (FlexBase) already owns power
            // (1 W), monitor (off), antennas and full duplex — the session
            // must not double-manage them.
            bool monitorTurnedOn = false;
            bool recorderAlreadyRunning = false;

            // Record and play back arms the radio's recorder FIRST, through
            // the one reply-checked surface (#787), and waits for the radio to
            // say yes. A recorder that did not start would make a take with
            // nothing to play, so the check refuses before anything is changed
            // or keyed, rather than transmitting for nothing. Until #787 this
            // was a setter whose reply nobody read.
            if (!_loopback && _method == AudioCheckListenMethods.RecordPlayback)
            {
                string letter = rig.SliceRecorderLetter(SliceRecorderTarget.TransmitSlice);
                if (letter.Length > 0) _recorderTarget = SliceRecorderTarget.Letter(letter[0]);
                var armed = await rig.StartSliceRecordAsync(_recorderTarget);
                if (!armed.Success)
                {
                    if (start.IsCurrent && !_owner._iqClosing && ReferenceEquals(rig, Rig))
                        RecorderControls.Speak(armed);
                    return false;
                }
                // Never blind-toggle a recorder that is already running: a
                // live re-arm race once nearly wiped an operator's takes.
                recorderAlreadyRunning = armed.AlreadyInState;
                if (!start.IsCurrent || _owner._iqClosing || !ReferenceEquals(rig, Rig) || _ptt.IsTransmitting || rig.Transmit)
                {
                    // The dialog closed, the radio changed, or something else
                    // keyed while the radio was answering. Put the recorder back.
                    if (!recorderAlreadyRunning) await RecorderControls.ReportStopFailureAsync(rig.StopSliceRecordAsync(_recorderTarget));
                    if (_ptt.IsTransmitting)
                        ScreenReaderOutput.Speak(Lexicon.Get("audio.already_transmitting"),
                            VerbosityLevel.Critical, interrupt: true);
                    return false;
                }
            }
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
                    // The cap is the check's own mechanical act, not an
                    // operator settings change - it must not arm the
                    // provisional-change receipt or the disconnect save
                    // offer (#225). Same for the restores below.
                    using (rig.AppInitiatedSettingChanges())
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

            }

            // Record and play back stops itself at the recorder's 120-second
            // limit (TickKeyed below); while that comes first, the operator's
            // timeout ladder stands down. Monitor and off-air checks keep the
            // operator's timeout exactly as any other transmission does.

            // Key through the controller. Its own key-down announcement fires
            // first; the full safety line follows with interrupt so the
            // complete utterance the operator hears is frequency, power,
            // source, and how to stop. Key-down is never silent even if this
            // class faults between the two.
            if (_method == AudioCheckListenMethods.OffAir) _owner._iqRun?.OwnKeying(true);
            if (_method == AudioCheckListenMethods.OffAir)
                ScreenReaderOutput.Supersede("audio.iq.preparing", "off-air take ready to start");
            _ptt.AudioCheckNarration = true;
            _ptt.PlannedStopSeconds = _method == AudioCheckListenMethods.RecordPlayback
                ? FlexBase.SliceRecordBufferSeconds : null;
            bool admitted = start.TryRun(() =>
            {
                if (_owner._iqClosing || !ReferenceEquals(rig, Rig) || _ptt.IsTransmitting || rig.Transmit)
                    return false;
                _ptt.ToggleLock();
                return true;
            });
            if (!admitted)
            {
                _ptt.AudioCheckNarration = false;
                _ptt.PlannedStopSeconds = null;
                if (_method == AudioCheckListenMethods.OffAir) _owner._iqRun?.OwnKeying(false);
                RestoreChangedState(rig, speak: false);
                if (!_loopback && _method == AudioCheckListenMethods.RecordPlayback && !recorderAlreadyRunning)
                    await RecorderControls.ReportStopFailureAsync(rig.StopSliceRecordAsync(_recorderTarget));
                return false;
            }
            if (!_ptt.IsTransmitting)
            {
                _ptt.AudioCheckNarration = false;
                _ptt.PlannedStopSeconds = null;
                // Refused (license lockout spoke its own warning, or no
                // power). Roll back everything we touched.
                _ptt.SessionTimeoutOverrideSeconds = null;
                RestoreChangedState(rig, speak: false);
                if (!_loopback && _method == AudioCheckListenMethods.RecordPlayback && !recorderAlreadyRunning)
                    await RecorderControls.ReportStopFailureAsync(rig.StopSliceRecordAsync(_recorderTarget));
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.could_not_start"),
                    VerbosityLevel.Critical);
                return false;
            }

            _owner._lastAudioCheckSource = rig.TxToneEngaged
                ? $"{(int)rig.TxToneFrequency} hertz test tone"
                : rig.TxFilePlaying ? "reference recording" : "microphone";

            var line = new StringBuilder();
            if (_method == AudioCheckListenMethods.OffAir)
            {
                line.Append(Lexicon.Get("audio.iq.listener_added")).Append(' ');
                line.Append(Lexicon.Get("audio.iq.keyed_line", ("freq", FormatMHz(rig.TXFrequency)), ("source", SourceFriendlyName(rig.MicSource))));
            }
            else if (_loopback)
            {
                line.Append(Lexicon.Get("audio.check.loopback_ears_added")).Append(' ');
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
            line.Append(' ').Append(Lexicon.Get("audio.check.escape_stops"));
            ScreenReaderOutput.Speak("Transmitting", Radios.Speech.SpeechIntent.Queue,
                VerbosityLevel.Critical);

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
            _owner.CancelCheckStart();
            if (!Active) return;
            if (_phase == Phase.Keyed)
                HandleUnkey(external: false);
            else if (_ptt.IsTransmitting || Rig?.Transmit == true)
            {
                _ptt.EscapeUnlock();
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.still_transmitting"),
                    VerbosityLevel.Critical, interrupt: true);
            }
            else ForceEnd(null);
        }

        /// <summary>
        /// Terminal teardown: dialog closing or radio gone. Unkeys if still
        /// keyed, restores what can be restored, stops playback, ends the
        /// session. Safe to call repeatedly.
        /// </summary>
        public void ForceEnd(string reason)
        {
            _owner.CancelCheckStart();
            if (!Active)
            {
                _watcher.Stop();
                return;
            }

            var rig = Rig;
            if (_phase == Phase.Keyed)
            {
                if (_ptt.IsTransmitting)
                {
                    if (_method == AudioCheckListenMethods.OffAir) _owner._iqRun?.OwnKeying(false);
                    _ptt.EscapeUnlock();
                }
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.transmit_off"), VerbosityLevel.Critical);
                if (rig != null && _method == AudioCheckListenMethods.RecordPlayback)
                    _ = RecorderControls.ReportStopFailureAsync(rig.StopSliceRecordAsync(_recorderTarget));
            }
            if (rig != null)
            {
                RestoreChangedState(rig, speak: false);
                // Only the take THIS check started is stopped; a playback
                // started from Home or a menu on another slice is left alone.
                if (_method == AudioCheckListenMethods.RecordPlayback)
                    _ = RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(_recorderTarget));
            }
            End(reason);
        }

        private void End(string? reason)
        {
            _phase = Phase.Done;
            Active = false;
            _watcher.Stop();
            _ptt.SessionTimeoutOverrideSeconds = null;
            _ptt.AudioCheckNarration = false;
            _ptt.PlannedStopSeconds = null;
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
            {
                if (_method == AudioCheckListenMethods.OffAir) _owner._iqRun?.OwnKeying(false);
                _ptt.EscapeUnlock();
            } // controller speaks its own key-up line

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
                    using (rig.AppInitiatedSettingChanges())
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
                    _recordStop = rig.StopSliceRecordAsync(_recorderTarget);
                    msg.Append(' ').Append(Lexicon.Get("audio.check.playing_take_shortly"));
                    _owner._radioTakeTarget = _recorderTarget;
                }

                // The verdict — the whole point of the check. Peak SC_MIC over
                // the keyed window (reset at key-down via ToggleLock); honest for
                // PC audio AND the analog mic. -140 guards "no meter yet".
                // The check is the longest deliberate transmit the operator
                // makes, so it is also where the room observation is most
                // likely to have enough audio to be worth saying.
                if (_method != AudioCheckListenMethods.OffAir && rig.ScMicMaxDb > -140f)
                {
                    string report = MicAudioReport.Compose(
                        rig, Lexicon.Get("audio.check.mic_audio_was"), rig.ScMicMaxDb, live: false);
                    msg.Append(' ').Append(report);
                    if (!report.EndsWith(".")) msg.Append('.');
                }
            }

            // Unconditional key-up announcement (interrupt false so the
            // controller's own "Receiving" isn't stomped when enabled).
            // The results remain in the read-only box; ordinary key-up is quiet.

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

            if (_method == AudioCheckListenMethods.RecordPlayback &&
                elapsed >= FlexBase.SliceRecordBufferSeconds)
            {
                _ptt.RecorderLimitUnlock();
                HandleUnkey(external: true);
                return;
            }

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

            // The record buffer's ten-second warning used to be spoken here.
            // The recorder surface speaks it now, for every recording
            // (FlexBase.SliceRecorder.cs, ArmCapWarning), so it is said once
            // and the same way whichever door started the recording.
        }

        private void TickPostUnkey(FlexBase rig)
        {
            _postUnkeyTicks++;

            // The radio may still be keyed after our unkey. PTTSource SW also
            // names VOX, so it cannot establish a hardware line by itself.
            if (!_hardwareWarned && _postUnkeyTicks >= 2 && rig.Transmit && !_ptt.IsTransmitting)
            {
                _hardwareWarned = true;
                ScreenReaderOutput.Speak(
                    rig.CurrentVoxSettingIfOn() == FlexBase.VoxSetting.SimpleVox
                        ? "Warning: the radio is still transmitting. VOX is on and may be keying it. Check the radio before continuing."
                        : "Warning: the radio is still transmitting after the app unkeyed. Check P T T and the radio before continuing.",
                    VerbosityLevel.Critical, interrupt: true);
                return; // keep watching until the radio releases
            }
            if (_hardwareWarned && rig.Transmit)
                return; // still keyed — stay alive, Escape keeps warning
            if (_hardwareWarned && !rig.Transmit)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.transmitter_released"), VerbosityLevel.Critical);
                _hardwareWarned = false;
            }

            if (_awaitPlaybackTicks == int.MinValue)
            {
                // The off-air file has its own playback path. Once both the
                // controller and radio confirm key-up, waiting for two more
                // timer ticks cannot make teardown safer. Keep the hardware
                // PTT warning above active until the radio actually releases.
                if (_method == AudioCheckListenMethods.OffAir)
                {
                    if (!_ptt.IsTransmitting && !rig.Transmit) End(null);
                    return;
                }
                // Other no-playback methods retain their post-unkey grace.
                if (_postUnkeyTicks >= 3)
                    End(null);
                return;
            }

            // Auto-play-on-unkey (the default flow, performed live: unkey to
            // playback within a second — never demands talking and listening
            // at once).
            //
            // Both halves ride the recorder surface (#787): the stop sent at
            // unkey must be confirmed before playback is asked for, and the
            // playback itself is reply-checked and refused while transmitting.
            _awaitPlaybackTicks++;
            if (_playStart != null)
            {
                if (!_playStart.IsCompleted && _awaitPlaybackTicks < 8) return;
                var played = _playStart.IsCompletedSuccessfully ? _playStart.Result : null;
                if (played?.Success == true)
                {
                    _owner._workshopPlayTargets.Add(_recorderTarget);
                    ScreenReaderOutput.Speak(
                        "Playing your take.",
                        Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Terse, subject: played.Subject);
                    End(null);
                }
                else End(played?.Message ?? Lexicon.Get("audio.check.no_recording_available"));
                return;
            }
            if (_recordStop != null && !_recordStop.IsCompleted)
            {
                if (_awaitPlaybackTicks >= 6) End(Lexicon.Get("audio.check.no_recording_available"));
                return;
            }
            if (_recordStop != null && _recordStop.IsCompletedSuccessfully && !_recordStop.Result.Success)
            {
                End(_recordStop.Result.Message);
                return;
            }
            if (rig.SliceHasRecording(_recorderTarget))
            {
                RecorderControls.Observe(rig);
                RecorderControls.PlayStartClick();
                _playStart = rig.StartSlicePlayAsync(_recorderTarget);
                _ = StopLatePlaybackAfterCloseAsync(_playStart, rig);
            }
            else if (_awaitPlaybackTicks >= 6)
            {
                End(Lexicon.Get("audio.check.no_recording_available"));
            }
        }

        private async Task StopLatePlaybackAfterCloseAsync(Task<RecorderOutcome> start, FlexBase rig)
        {
            try
            {
                var outcome = await start;
                if (outcome.Success && _owner._workshopClosing)
                    await RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(_recorderTarget));
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Workshop late playback stop: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
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
                using (rig.AppInitiatedSettingChanges())
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
