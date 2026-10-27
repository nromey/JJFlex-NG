using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Check your audio: the layout, the methods, and the session engine behind
/// Start. Every keying path here rides the PTT safety controller; nothing in
/// this file sets Transmit directly.
///
/// Moved from the Audio Workshop's Hear Yourself page (<c>AudioWorkshopDialog.AudioCheck.cs</c>)
/// in #835. The session engine is the same one; what changed is which methods
/// exist and when each control is shown.
/// </summary>
public partial class CheckYourAudioDialog
{
    #region Layout

    private Button? _startCheckButton;
    private CycleFieldControl? _listenMethodControl;
    private TextBox? _methodStatus;
    private GroupBox? _powerSection;
    private CycleFieldControl? _checkPowerControl;
    private ValueFieldControl? _checkWattsControl;
    private TextBox? _micReadingBox;
    private GroupBox? _resultsSection;
    private Button? _playTakeButton;

    /// <summary>The methods in the cycle right now, in its order. Index into this, never cast a SelectedIndex to the enum.</summary>
    private List<AudioCheckListenMethods> _offered = new(AudioCheckMethods.Offered(false, false, "", false));

    /// <summary>The method shown, which is what Start runs.</summary>
    private AudioCheckListenMethods SelectedMethod
    {
        get
        {
            int i = _listenMethodControl?.SelectedIndex ?? -1;
            return i >= 0 && i < _offered.Count ? _offered[i] : AudioCheckListenMethods.Monitor;
        }
    }

    /// <summary>
    /// Build the window, in tab order (ruled 13:45): Start alone; the method and
    /// its own options; the sources; check power; the gain and the reading; the
    /// results once a take exists.
    /// </summary>
    private void BuildCheckDialog()
    {
        // DRAFT PROSE (#835): awaiting Noel — section names.
        _sections.OpenRadio(CheckContent, "Start");
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
        _sections.Add(CheckContent, _startCheckButton);

        _sections.OpenRadio(CheckContent, "Method");
        _listenMethodControl = MakeCycle("Method", MethodNames(_offered));
        // Before a radio is known, show the local default rather than the
        // first row; SetRig resolves the real choice.
        _listenMethodControl.SuppressEvents = true;
        _listenMethodControl.SelectedIndex = _offered.IndexOf(AudioCheckMethods.DefaultFor(remote: false));
        _listenMethodControl.SuppressEvents = false;
        _listenMethodControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            SavePerRadioPrefs(methodChosen: true);
            // A take's sentence describes the take it was about; a method the
            // operator has just chosen starts from its own description.
            _offAirStatus = "";
            ApplyMethodLayout();
            // The control announces its own value; this adds only what the tree
            // cannot carry, and only where it matters.
            if (SelectedMethod == AudioCheckListenMethods.Monitor && _rig?.RemoteRig == true)
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.check.remote_monitor_advice"),
                    VerbosityLevel.Terse);
        };
        JJFlexHelp.SetText(_listenMethodControl, Lexicon.Get("audio.check.method_help"));
        _sections.Add(CheckContent, _listenMethodControl);

        // One status line that describes the chosen method, and says in one
        // sentence why a method is missing (13:57). A read-only edit, so it is
        // a tab stop a review command can read, and it carries no live region.
        _methodStatus = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2),
            MinWidth = 300
        };
        AutomationProperties.SetName(_methodStatus, Lexicon.Get("audio.check.method_status_label"));
        _sections.Add(CheckContent, _methodStatus);

        // Off the air's own options sit in the method's section and appear only
        // while it is chosen.
        BuildOffAirOptions();

        // The sources. Arming never transmits; the operator keys.
        BuildTestToneSection();
        BuildReferenceAudioSection();

        // Track C-2 (Noel at the radio, 2026-08-11): the check defaults to DUMMY
        // LOAD, not low power. Every meter the check reads sits upstream of the
        // power amplifier, and with a tone armed a transmitting check puts a
        // steady carrier on whatever frequency the operator is tuned to. Low
        // power remains for confirming RF leaves the radio, with the cap
        // choosable. Shown only for the methods that obey it.
        _powerSection = _sections.OpenRadio(CheckContent, "Check power");
        _checkPowerControl = MakeCycle("Transmit power during checks",
            new[] { "Dummy load, no RF", "Low power" });
        _checkPowerControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            UpdateCheckWattsVisibility();
            SavePerRadioPrefs(methodChosen: false);
        };
        _sections.Add(CheckContent, _checkPowerControl);

        _checkWattsControl = new ValueFieldControl();
        _checkWattsControl.Setup("Low power level", 1, 100, 1, 10, 0, "watts");
        _checkWattsControl.Visibility = Visibility.Collapsed; // dummy load is the default
        _checkWattsControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            SavePerRadioPrefs(methodChosen: false);
        };
        _sections.Add(CheckContent, _checkWattsControl);

        // The gain and the reading: ONE LOOP — speak, read what arrived, nudge,
        // speak again. During a check focus sits on the gain, and the reading is
        // the very next Tab. Mic Gain and the Windows level are the same
        // settings, and the same implementation, as the Workshop's.
        _sections.Open(CheckContent, "Levels");
        _sections.AddRadio(CheckContent, _gain.MicGain);
        _sections.Add(CheckContent, _gain.PcLevel);
        _sections.Add(CheckContent, _gain.PcLevelNote);

        // The reading, as a read-only EDIT (Noel, 2026-08-11): focusable and
        // review-readable, so the screen reader's own read-current-control
        // command IS the "speak my level" feature. Multi-line since #835: Off
        // the air's measurements land here too, in the same shape (13:57).
        _micReadingBox = new TextBox
        {
            Text = "Mic audio: transmit to measure",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2),
            FontSize = 12
        };
        // Static accessible name, set ONCE. The 2 Hz refresh touches only the
        // text — no name changes, no live region — so NVDA stays quiet while the
        // value moves and a review command always reads fresh.
        AutomationProperties.SetName(_micReadingBox, "Mic audio reading");
        _sections.Add(CheckContent, _micReadingBox);

        // Results: only once a take exists (13:57).
        _resultsSection = _sections.OpenRadio(CheckContent, "Results");
        _playTakeButton = new Button
        {
            Content = "Play last take",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_playTakeButton, "Play last take");
        JJFlexHelp.SetText(_playTakeButton,
            "Plays the slice recording with Record yourself, or the off-air take "
            + "with Hear what you transmitted. Press again to stop.");
        _playTakeButton.Click += (s, e) => PlayLastTake();
        _playTakeButton.GotFocus += (s, e) => SyncPlayTakeLabel();
        _sections.Add(CheckContent, _playTakeButton);

        BuildOffAirResults();

        // Follow local playback so the button says what pressing it will do.
        RecordingPlayer.StateChanged += OnTakePlaybackStateChanged;

        ApplyMethodLayout();
    }

    /// <summary>The ruled names, in the order given. Each says what you hear and whether anything goes out.</summary>
    internal static string[] MethodNames(IReadOnlyList<AudioCheckListenMethods> methods) =>
        methods.Select(MethodName).ToArray();

    internal static string MethodName(AudioCheckListenMethods method) => method switch
    {
        // Ruled by Noel 2026-10-27 13:51, 13:57 and 14:57.
        AudioCheckListenMethods.MicTest => Lexicon.Get("audio.check.method.mic_test"),
        AudioCheckListenMethods.Monitor => Lexicon.Get("audio.check.method.monitor"),
        AudioCheckListenMethods.RecordPlayback => Lexicon.Get("audio.check.method.record"),
        AudioCheckListenMethods.OffAir => Lexicon.Get("audio.check.method.off_air"),
        AudioCheckListenMethods.Loopback => Lexicon.Get("audio.check.method.loopback"),
        _ => method.ToString()
    };

    /// <summary>
    /// Rebuild the method cycle when what the radio and connection offer has
    /// changed, keeping the shown method where it can.
    /// </summary>
    private void RefreshOfferedMethods()
    {
        if (_listenMethodControl == null) return;
        var rig = _rig;
        var offered = AudioCheckMethods.Offered(rig != null, rig?.RemoteRig == true,
            rig?.OffAirListenbackRefusalReason ?? "no-radio", rig?.LoopbackSupported == true);
        if (offered.SequenceEqual(_offered)) { UpdateMethodStatus(); return; }

        var wanted = SelectedMethod;
        // A method that left mid-check is not taken out from under the check:
        // the cycle is rebuilt when it ends.
        if (_session?.Active == true || _checkStarts.IsBusy) return;
        _offered = new List<AudioCheckListenMethods>(offered);
        // Every choice the operator makes is saved at once, so the saved
        // choice IS the operator's choice: resolving it again is what brings a
        // saved Off the air back once a late antenna list makes it offerable,
        // and what drops a connection's fallback when the connection changes.
        // Only with nowhere to save (no serial yet) does the shown one win.
        var show = _radioCfg == null && _offered.Contains(wanted) ? wanted : ResolvedSavedMethod();
        SetMethodSilently(show);
    }

    /// <summary>The saved method if it was chosen and this connection offers it; otherwise this connection's default. Never writes.</summary>
    private AudioCheckListenMethods ResolvedSavedMethod()
    {
        var cfg = _radioCfg;
        bool remote = _rig?.RemoteRig == true;
        if (cfg == null) return AudioCheckMethods.Resolve(AudioCheckListenMethods.Monitor, false, _offered, remote);
        return AudioCheckMethods.Resolve(cfg.AudioCheckListenMethod,
            AudioCheckMethods.WasChosen(cfg.AudioCheckListenMethod, cfg.AudioCheckListenMethodChosen),
            _offered, remote);
    }

    private void SetMethodSilently(AudioCheckListenMethods method)
    {
        if (_listenMethodControl == null) return;
        int index = Math.Max(0, _offered.IndexOf(method));
        bool focused = _listenMethodControl.IsKeyboardFocusWithin;
        _listenMethodControl.SuppressEvents = true;
        try { _listenMethodControl.Setup("Method", MethodNames(_offered), index); }
        finally { _listenMethodControl.SuppressEvents = false; }
        if (focused) _listenMethodControl.Focus();
        ApplyMethodLayout();
    }

    /// <summary>
    /// Show what belongs to the chosen method and nothing else (13:57). A
    /// collapsed control is out of the tab order, which is the house rule for
    /// controls that currently do nothing.
    /// </summary>
    private void ApplyMethodLayout()
    {
        var method = SelectedMethod;
        if (_powerSection != null)
        {
            // Moving focus off a section about to collapse, before it collapses.
            if (!AudioCheckMethods.UsesCheckPower(method) && _powerSection.IsKeyboardFocusWithin)
                _listenMethodControl?.Focus();
            _powerSection.Visibility = AudioCheckMethods.UsesCheckPower(method) ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplyOffAirLayout(method);
        SyncPlayTakeLabel();
        UpdateMethodStatus();
    }

    /// <summary>
    /// The one status line: what the chosen method does when you press Start,
    /// and in one sentence why a method is missing. The old idle sentence
    /// ("Off-air audio checks need two receivers and a transverter arrangement")
    /// is gone; it is what made a one-receiver operator hear "takes".
    /// </summary>
    private void UpdateMethodStatus()
    {
        if (_methodStatus == null) return;
        var rig = _rig;
        string text;
        if (rig == null) text = Lexicon.Get("audio.no_radio_connected") + ".";
        // An off-air take's own sentence (preparing, recording, its result)
        // takes the line while Off the air is chosen.
        else if (SelectedMethod == AudioCheckListenMethods.OffAir && _offAirStatus.Length > 0) text = _offAirStatus;
        else
        {
            text = Lexicon.Get(SelectedMethod switch
            {
                AudioCheckListenMethods.MicTest => "audio.check.status.mic_test",
                AudioCheckListenMethods.RecordPlayback => "audio.check.status.record",
                AudioCheckListenMethods.OffAir => "audio.check.status.off_air",
                AudioCheckListenMethods.Loopback => "audio.check.status.loopback",
                _ => "audio.check.status.monitor"
            });
            string missing = MissingMethodsSentence(rig);
            if (missing.Length > 0) text += " " + missing;
        }
        if (_methodStatus.Text == text) return;
        _methodStatus.Text = text;
    }

    /// <summary>One sentence for the methods this radio or connection leaves out, or empty when none is missing.</summary>
    /// <remarks>
    /// Off the air and Loopback are left out together or not at all: both need
    /// two receivers and a transverter port (<c>OffAirListenbackRefusalReason</c>
    /// and <c>LoopbackSupported</c> ask the same two facts), so one sentence
    /// covers both.
    /// </remarks>
    private string MissingMethodsSentence(FlexBase rig)
    {
        if (rig.RemoteRig) return Lexicon.Get("audio.check.missing.smartlink");
        if (_offered.Contains(AudioCheckListenMethods.OffAir) && _offered.Contains(AudioCheckListenMethods.Loopback)) return "";
        bool noPort = rig.OffAirListenbackRefusalReason == NoTransverterPort;
        string key = noPort ? "audio.check.missing.no_transverter" : "audio.check.missing.one_receiver";
        return Lexicon.Get(key);
    }

    /// <summary>FlexBase's refusal reason for a radio with no transverter transmit port.</summary>
    private const string NoTransverterPort = "no-transverter-port";

    #endregion

    #region Per-radio preferences

    /// <summary>
    /// Load per-radio Audio Check preferences (listen method, power mode and
    /// watts, and the "compare with my last take" tick) from the serial-keyed
    /// RadioConfig store and reflect them in the controls.
    /// </summary>
    private void LoadPerRadioPrefs()
    {
        if (_rig == null) return;
        string serial = _rig.SelectedRadioSerial;
        if (string.IsNullOrEmpty(serial)) return;
        if (_radioCfg != null && _radioCfgSerial == serial) return;

        _radioCfg = RadioConfig.LoadForRadio(serial);
        _radioCfgSerial = serial;

        // The offered set first, so the saved method is resolved against what
        // THIS connection offers. A saved method it does not offer falls back
        // to its default quietly, for this session, and is not overwritten.
        var rig = _rig;
        _offered = new List<AudioCheckListenMethods>(AudioCheckMethods.Offered(true, rig.RemoteRig,
            rig.OffAirListenbackRefusalReason, rig.LoopbackSupported));
        SetMethodSilently(ResolvedSavedMethod());

        if (_checkPowerControl != null)
        {
            _checkPowerControl.SuppressEvents = true;
            _checkPowerControl.SelectedIndex = (int)_radioCfg.AudioCheckPowerMode;
            _checkPowerControl.SuppressEvents = false;
        }
        if (_checkWattsControl != null)
        {
            _checkWattsControl.SuppressEvents = true;
            _checkWattsControl.Value = Math.Clamp(_radioCfg.AudioCheckLowPowerWatts, 1, 100);
            _checkWattsControl.SuppressEvents = false;
        }
        if (_iqAutoCompare != null)
        {
            // A CheckBox has no SuppressEvents; _polling is how every checkbox
            // here is set without its handler saving the value straight back.
            _polling = true;
            try { _iqAutoCompare.IsChecked = _radioCfg.AudioCheckCompareWithLastTake; }
            finally { _polling = false; }
        }
        UpdateCheckWattsVisibility();
    }

    /// <param name="methodChosen">True only when the operator changed the method. A method shown as a connection's fallback is not saved as their choice.</param>
    private void SavePerRadioPrefs(bool methodChosen)
    {
        if (_radioCfg == null || string.IsNullOrEmpty(_radioCfgSerial)) return;
        AudioCheckListenMethods? method = methodChosen && _listenMethodControl != null ? SelectedMethod : null;
        var power = _checkPowerControl == null ? (AudioCheckPowerModes?)null
            : (AudioCheckPowerModes)_checkPowerControl.SelectedIndex;
        var watts = _checkWattsControl?.Value;
        bool? compare = _iqAutoCompare?.IsChecked == null ? null : _iqAutoCompare.IsChecked == true;
        RadioConfig.RecordAudioCheckPrefs(_radioCfgSerial, method, power, watts, compare);
        // Keep the in-memory copy in step, so a rebuild of the cycle resolves
        // against what was just saved.
        if (method != null)
        {
            _radioCfg.AudioCheckListenMethod = method.Value;
            _radioCfg.AudioCheckListenMethodChosen = true;
        }
    }

    /// <summary>The watts field exists only while Low power is the selected check mode.</summary>
    private void UpdateCheckWattsVisibility()
    {
        if (_checkWattsControl == null) return;
        bool lowPower = _checkPowerControl?.SelectedIndex == (int)AudioCheckPowerModes.LowPower;
        _checkWattsControl.Visibility = lowPower ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Start and stop

    private bool _loopbackStarting;
    private string? _lastAudioCheckSource;
    private readonly AudioCheckStartGate _checkStarts = new();

    private void CancelCheckStart()
    {
        _checkStarts.Cancel();
        _iqCancellation?.Cancel();
    }

    /// <summary>
    /// The Loopback method: apply the verified recipe (full duplex on, TX to one
    /// XVT port, a listening slice on the other receive port, 1 watt, monitor
    /// off), then key through the same PttSafetyController path as every other
    /// check. Since #835 it records the listening slice while keyed and plays it
    /// back after the unkey, the way Record yourself does; that was the one job
    /// of the Workshop's Record and Play slice buttons that nothing else did.
    /// Teardown restores every saved value and removes the listening slice once
    /// the playback has finished or been stopped.
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
        SetStartButtonLabel("Stop Audio Check");
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

        // Power mode is irrelevant here — the loopback arrangement owns power
        // (1 W into the transverter port) and the session's power handling is
        // bypassed entirely in loopback mode.
        var session = new AudioCheckSession(this, rig, ptt,
            AudioCheckListenMethods.Loopback, AudioCheckPowerModes.LowPower, 1,
            loopback: true);
        if (await session.StartAsync(start))
        {
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            FocusGain();
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
            if (_session?.Active != true) SetStartButtonLabel("Start Audio Check");
        }
    }

    /// <summary>
    /// Start button, Ctrl+Enter and the Command Finder. Starts the chosen method
    /// when idle, stops the current check when one is running.
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
        // The Workshop's diagnostic take is a take too; the radio would refuse
        // the off-air one with "another IQ take is running", but an ordinary
        // check would key straight into it.
        if (AudioWorkshopDialog.ABenchTakeIsRunning) { IqStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        _lastAudioCheckSource = null;
        var method = SelectedMethod;
        if (method == AudioCheckListenMethods.OffAir) { _ = StartOffAirAsync(ptt); return; }
        if (method == AudioCheckListenMethods.Loopback) { _ = StartLoopbackCheckAsync(); return; }
        // Test your microphone ALWAYS runs at zero power, whatever the check
        // power says, so its name stays true (ruled 14:57).
        var powerMode = method == AudioCheckListenMethods.MicTest ? AudioCheckPowerModes.DummyLoad
            : (AudioCheckPowerModes)(_checkPowerControl?.SelectedIndex ?? (int)AudioCheckPowerModes.DummyLoad);
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
                // Land on the gain — arrows adjust immediately, one Tab reads
                // the live reading, Shift+Tab walks back to Start.
                FocusGain();
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

    /// <summary>Focus whichever gain applies to the current source.</summary>
    private void FocusGain()
    {
        if (_gain.MicGain.IsVisible && _gain.MicGain.Focus()) return;
        if (_gain.PcLevel.IsVisible && _gain.PcLevel.Focus()) return;
        if (_gain.PcLevelNote.IsVisible) _gain.PcLevelNote.Focus();
    }

    private void SetStartButtonLabel(string label)
    {
        if (_startCheckButton == null) return;
        _startCheckButton.Content = label;
        AutomationProperties.SetName(_startCheckButton, label);
    }

    /// <summary>
    /// Session ended (any path) — restore the button label, and if a loopback
    /// arrangement is up, tear it down and say so. Runs on every exit path
    /// because every exit path funnels through the session's End.
    /// </summary>
    private async void OnSessionEnded()
    {
        CancelCheckStart();
        UpdateMicReading();
        if (_micReadingBox?.IsVisible == true) _micReadingBox.Focus();
        SetStartButtonLabel("Start Audio Check");
        // A method that left the offer while the check ran comes off now.
        RefreshOfferedMethods();
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

    #endregion

    #region Play last take

    /// <summary>
    /// The slice the last check recorded on. Also tracks the slice chosen for
    /// playback so a second press stops the same buffer.
    /// </summary>
    private SliceRecorderTarget? _radioTakeTarget;
    private SliceRecorderTarget RadioTakeTarget => _radioTakeTarget ?? SliceRecorderTarget.TransmitSlice;

    /// <summary>
    /// Play the take for the selected method, or stop the one playing. Record
    /// yourself chooses a slice buffer; Off the air chooses an IQ take. The
    /// others leave nothing to play, and the button is not shown for them.
    /// </summary>
    private void PlayLastTake()
    {
        if (!AudioCheckMethods.MakesTake(SelectedMethod)) return;
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
        var method = SelectedMethod;
        if (!AudioCheckMethods.MakesTake(method))
            return (method, TakePlaybackChoice.None, null, null, null);
        var (radioTarget, radioTime) = method == AudioCheckListenMethods.RecordPlayback
            ? NewestRadioTake() : (null, null);
        string? iqDirectory = method == AudioCheckListenMethods.OffAir ? ListenbackTakes.NewestDirectory() : null;
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
    /// checks the radio's reply and pauses VOX for local playback.
    /// </summary>
    private async Task PlayRadioTakeAsync()
    {
        if (_rig == null) return;
        var rig = _rig;
        var target = FixedRecorderTarget(rig, RadioTakeTarget);
        if (_closing || !ReferenceEquals(rig, _rig)) return;
        RecorderControls.Observe(rig);
        RecorderControls.PlayStartClick();
        var outcome = await rig.StartSlicePlayAsync(target);
        if (outcome.Success)
        {
            if (_closing || !ReferenceEquals(rig, _rig))
            {
                await RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(target));
                return;
            }
            _checkPlayTargets.Add(target);
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
        _checkPlayTargets.Remove(target);
        SyncPlayTakeLabel();
    }

    /// <summary>Slice playbacks this window started, so closing it or a fresh Ctrl+Enter stops exactly those.</summary>
    private readonly HashSet<SliceRecorderTarget> _checkPlayTargets = new();

    private static SliceRecorderTarget FixedRecorderTarget(FlexBase rig, SliceRecorderTarget target)
    {
        string letter = rig.SliceRecorderLetter(target);
        return letter.Length > 0 ? SliceRecorderTarget.Letter(letter[0]) : target;
    }

    /// <summary>Stop the audio this window started before another Ctrl+Enter can start a check.</summary>
    private bool StopCheckPlayback()
    {
        bool stopped = false;
        if (RecordingPlayer.IsPlaying)
        {
            RecordingPlayer.Stop();
            stopped = true;
        }
        if (_checkPlayTargets.Count > 0 && _rig != null)
        {
            var targets = new List<SliceRecorderTarget>(_checkPlayTargets);
            _checkPlayTargets.Clear();
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
    /// Make the button say what pressing it will do, and show it only when the
    /// method makes takes AND a take exists to play (13:57: results appear only
    /// once a take exists). A button that says "Play last take" while a take is
    /// playing would be a control describing something other than what it does.
    /// </summary>
    private void SyncPlayTakeLabel()
    {
        if (_playTakeButton == null) return;
        bool playing = RecordingPlayer.IsPlaying ||
            _rig?.IsSlicePlaying(RadioTakeTarget) == true;
        var selected = SelectTakeForPlayback();
        bool available = AudioCheckMethods.MakesTake(SelectedMethod)
            && (playing || selected.Choice != TakePlaybackChoice.None);
        if (!available && _playTakeButton.IsKeyboardFocusWithin) _startCheckButton?.Focus();
        _playTakeButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SyncResultsSection();
        if (!available) return;
        string label = playing ? "Stop playing" : TakePlaybackSelector.PlayLabel(selected.MadeUtc, DateTime.Now);
        if ((_playTakeButton.Content as string) == label &&
            AutomationProperties.GetName(_playTakeButton) == label) return;
        _playTakeButton.Content = label;
        AutomationProperties.SetName(_playTakeButton, label);
    }

    /// <summary>The Results section is shown only while something in it is.</summary>
    private void SyncResultsSection()
    {
        if (_resultsSection?.Content is not Panel panel) return;
        bool any = panel.Children.OfType<UIElement>().Any(c => c.Visibility == Visibility.Visible);
        _resultsSection.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Stop following the player, and stop any playback this window started.
    /// A closed window must not go on relabelling a button that is gone, and a
    /// take must not outlive the window that played it.
    /// </summary>
    private void DetachTakePlayback()
    {
        CloseListenback();
        RecordingPlayer.StateChanged -= OnTakePlaybackStateChanged;
        StopCheckPlayback();
    }

    /// <summary>
    /// Refresh the reading: live recent-peak while transmitting, the whole
    /// transmit's peak after unkey, honest wording before any transmit, and
    /// since #835 Off the air's measurements underneath, in the same shape.
    /// Text only, assigned only on change: the name was set once and there is
    /// no live region, so a value moving twice a second never floods NVDA.
    /// </summary>
    private void UpdateMicReading()
    {
        if (_micReadingBox == null) return;
        var rig = _rig;
        string text;
        if (rig == null)
        {
            text = "Mic audio: no radio connected";
        }
        else
        {
            float recent = rig.ScMicRecentDb;
            float max = rig.ScMicMaxDb;
            if (rig.Transmit && recent > -140f)
                text = MicAudioReport.Compose(rig, "Mic audio now:", recent, live: true);
            else if (max > -140f)
                text = MicAudioReport.Compose(rig, "Mic audio last transmit:", max, live: false);
            else
                text = "Mic audio: transmit to measure";
            if (!rig.Transmit && _lastAudioCheckSource != null)
                text += Environment.NewLine + "Source: " + _lastAudioCheckSource;
            if (!rig.Transmit && SelectedMethod == AudioCheckListenMethods.OffAir && _offAirReading.Length > 0)
                text += Environment.NewLine + _offAirReading;
        }
        if (_micReadingBox.Text != text)
            _micReadingBox.Text = text;
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
    /// still-transmitting honesty check. This session alone stops a recording
    /// check (Record yourself, Loopback) at the radio's two-minute buffer limit.
    /// </summary>
    private sealed class AudioCheckSession
    {
        private enum Phase { Idle, Keyed, AwaitPlayback, PlayingBack, Done }

        private readonly CheckYourAudioDialog _owner;
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
        private int _playingBackTicks;
        private bool _sawPlaying;

        // Record and play back rides the one recorder surface (#787). The
        // slice is fixed at the start by its LETTER, so a slice change during
        // the check still stops and plays the slice that recorded. The Loopback
        // method records its listening slice, which the radio layer resolves by
        // the slice object it created, not by letter.
        private SliceRecorderTarget _recorderTarget = SliceRecorderTarget.TransmitSlice;
        private Task<RecorderOutcome>? _recordStop;
        private Task<RecorderOutcome>? _playStart;

        // State the session changed and must restore.
        private int _savedPower;
        private bool _powerTouched;
        private bool _monitorTouched; // we only ever turn monitor ON; restore = off
        private bool _dummyEngaged;   // WE turned dummy load on; disable restores power
        private int _dummySavedPower; // pre-engage watts, for the spoken restore line

        public bool Active { get; private set; }
        public bool IsLoopback => _loopback;

        /// <summary>True for the methods that record while keyed and play back after.</summary>
        private bool Records => _method == AudioCheckListenMethods.RecordPlayback
            || _method == AudioCheckListenMethods.Loopback;

        /// <summary>
        /// True while Escape must unkey rather than close the dialog. Covers
        /// the hardware-keying edge: if a hardware PTT line holds the rig in
        /// TX after software unkey, Escape keeps warning instead of closing.
        /// </summary>
        public bool EscapeStopsTransmit =>
            Active && (_ptt.IsTransmitting || Rig?.Transmit == true);

        private FlexBase? Rig => _owner._rig;

        public AudioCheckSession(CheckYourAudioDialog owner, FlexBase rig,
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
            bool recorderAlreadyRunning = false;

            // A recording check arms the radio's recorder FIRST, through the one
            // reply-checked surface (#787), and waits for the radio to say yes. A
            // recorder that did not start would make a take with nothing to
            // play, so the check refuses before anything is changed or keyed.
            if (Records)
            {
                if (_method == AudioCheckListenMethods.Loopback)
                    _recorderTarget = SliceRecorderTarget.LoopbackEars;
                else
                {
                    string letter = rig.SliceRecorderLetter(SliceRecorderTarget.TransmitSlice);
                    if (letter.Length > 0) _recorderTarget = SliceRecorderTarget.Letter(letter[0]);
                }
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
            if (!_loopback)
            {
                // Power handling BEFORE keying. Dummy load (the default, and
                // ALWAYS for Test your microphone) rides FlexBase.DummyLoadMode:
                // it zeroes transmit and tune power and restores both on
                // disable. If the operator engaged dummy load themselves, we
                // leave it theirs: the check neither re-engages nor releases it.
                int currentPower = rig.XmitPower;
                if (_powerMode == AudioCheckPowerModes.DummyLoad)
                {
                    if (!rig.DummyLoadMode)
                    {
                        _dummySavedPower = currentPower;
                        rig.DummyLoadMode = true;
                        _dummyEngaged = true;
                    }
                }
                else if (!rig.DummyLoadMode && currentPower > _lowPowerWatts)
                {
                    // Low power is a CAP: it only ever lowers power, never
                    // raises it — and it cannot override an active dummy load.
                    _savedPower = currentPower;
                    _powerTouched = true;
                    // The cap is the check's own mechanical act, not an
                    // operator settings change (#225). Same for the restores.
                    using (rig.AppInitiatedSettingChanges())
                        rig.XmitPower = _lowPowerWatts;
                }

                if (_method == AudioCheckListenMethods.Monitor &&
                    rig.Monitor != FlexBase.OffOnValues.on)
                {
                    _monitorTouched = true;
                    rig.Monitor = FlexBase.OffOnValues.on;
                }
            }

            // Key through the controller. Its own key-down announcement fires
            // first. Key-down is never silent even if this class faults.
            if (_method == AudioCheckListenMethods.OffAir) _owner._iqRun?.OwnKeying(true);
            if (_method == AudioCheckListenMethods.OffAir)
                ScreenReaderOutput.Supersede("audio.iq.preparing", "off-air take ready to start");
            _ptt.AudioCheckNarration = true;
            _ptt.PlannedStopSeconds = Records ? FlexBase.SliceRecordBufferSeconds : null;
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
                if (Records && !recorderAlreadyRunning)
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
                if (Records && !recorderAlreadyRunning)
                    await RecorderControls.ReportStopFailureAsync(rig.StopSliceRecordAsync(_recorderTarget));
                ScreenReaderOutput.Speak(Lexicon.Get("audio.check.could_not_start"),
                    VerbosityLevel.Critical);
                return false;
            }

            _owner._lastAudioCheckSource = rig.TxToneEngaged
                ? $"{(int)rig.TxToneFrequency} hertz test tone"
                : rig.TxFilePlaying ? "reference recording" : "microphone";

            // AudioCheckNarration quiets the controller's own key-down line
            // during a check, so this short confirmation is what is heard.
            // Until #835 this method also assembled a longer safety line that
            // nothing has spoken since recorder round 5 (c7c6539b); it is gone.
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
        public void ForceEnd(string? reason)
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
                if (rig != null && Records)
                    _ = RecorderControls.ReportStopFailureAsync(rig.StopSliceRecordAsync(_recorderTarget));
            }
            if (rig != null)
            {
                RestoreChangedState(rig, speak: false);
                // Only the take THIS check started is stopped; a playback
                // started from Home or a menu on another slice is left alone.
                // The Loopback method's listening slice is still arranged here,
                // so this reaches it; the arrangement comes down after End.
                if (Records)
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
        /// The unkey path every stop flows through: restores power and
        /// monitor, and hands recording checks to the auto-play flow. The
        /// verdict is the reading box's, which every method fills (13:50);
        /// key-up itself is quiet since recorder round 5.
        /// </summary>
        private void HandleUnkey(bool external)
        {
            if (!external && _ptt.IsTransmitting)
            {
                if (_method == AudioCheckListenMethods.OffAir) _owner._iqRun?.OwnKeying(false);
                _ptt.EscapeUnlock();
            } // controller speaks its own key-up line

            var rig = Rig;
            if (rig != null)
            {
                if (_dummyEngaged)
                {
                    // Disabling dummy load restores transmit AND tune power
                    // inside FlexBase.
                    rig.DummyLoadMode = false;
                    _dummyEngaged = false;
                }
                if (_powerTouched)
                {
                    using (rig.AppInitiatedSettingChanges())
                        rig.XmitPower = _savedPower;
                    _powerTouched = false;
                }
                if (_monitorTouched)
                {
                    rig.Monitor = FlexBase.OffOnValues.off;
                    _monitorTouched = false;
                }
                if (Records)
                {
                    _recordStop = rig.StopSliceRecordAsync(_recorderTarget);
                    if (_method == AudioCheckListenMethods.RecordPlayback)
                        _owner._radioTakeTarget = _recorderTarget;
                }
            }

            _postUnkeyTicks = 0;
            if (Records && rig != null)
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
                case Phase.PlayingBack:
                    TickPlayingBack(rig);
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

            if (Records && elapsed >= FlexBase.SliceRecordBufferSeconds)
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
                // timer ticks cannot make teardown safer.
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

            // Auto-play-on-unkey: unkey to playback within a second — never
            // demands talking and listening at once. Both halves ride the
            // recorder surface (#787): the stop sent at unkey must be confirmed
            // before playback is asked for, and the playback itself is
            // reply-checked and refused while transmitting.
            _awaitPlaybackTicks++;
            if (_playStart != null)
            {
                if (!_playStart.IsCompleted && _awaitPlaybackTicks < 8) return;
                var played = _playStart.IsCompletedSuccessfully ? _playStart.Result : null;
                if (played?.Success == true)
                {
                    _owner._checkPlayTargets.Add(_recorderTarget);
                    ScreenReaderOutput.Speak(
                        "Playing your take.",
                        Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Terse, subject: played.Subject);
                    // The Loopback method's take lives on the listening slice,
                    // which goes when the arrangement does. So the check stays
                    // up while it plays, and comes down when it finishes or is
                    // stopped; Record yourself's take stays on the radio and the
                    // check can end now.
                    if (_method == AudioCheckListenMethods.Loopback)
                    {
                        _phase = Phase.PlayingBack;
                        _playingBackTicks = 0;
                        _sawPlaying = false;
                        return;
                    }
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

        /// <summary>
        /// The Loopback method's playback: watch it to its end, then end the
        /// check so the arrangement comes down. Capped just past the radio's
        /// two-minute buffer, so a playback the radio never reports finished
        /// cannot hold the transverter arrangement up forever.
        /// </summary>
        private void TickPlayingBack(FlexBase rig)
        {
            _playingBackTicks++;
            bool playing = rig.IsSlicePlaying(_recorderTarget);
            _sawPlaying |= playing;
            // A radio that has said yes to playback but not yet reported it
            // playing gets three seconds, so the arrangement is not torn down
            // under a playback that is just starting.
            if (!_sawPlaying && _playingBackTicks < 3) return;
            if (playing && _playingBackTicks <= FlexBase.SliceRecordBufferSeconds + 5)
                return;
            _owner._checkPlayTargets.Remove(_recorderTarget);
            if (rig.IsSlicePlaying(_recorderTarget))
                _ = RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(_recorderTarget));
            End(null);
        }

        private async Task StopLatePlaybackAfterCloseAsync(Task<RecorderOutcome> start, FlexBase rig)
        {
            try
            {
                var outcome = await start;
                if (outcome.Success && _owner._closing)
                    await RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(_recorderTarget));
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Check your audio late playback stop: " + ex.Message,
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
    }

    #endregion
}
