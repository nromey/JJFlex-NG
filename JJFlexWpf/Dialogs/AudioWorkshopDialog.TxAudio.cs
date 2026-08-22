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
/// Audio Workshop, TX Audio tab: the setup walk-through from this
/// computer out to the radio to the air. Holds the tab's control fields,
/// its builders, the device section, and the polling that keeps the
/// radio-side controls honest.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A. The
/// move was mechanical: no member changed name, signature or body. The
/// single file had reached 4,866 lines while four tracks in one sprint
/// needed to edit different parts of it.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region TX Audio Controls

    /// <summary>
    /// The Mic Bias label, in one place because it is written twice (build and
    /// the label-restore in PollMicSource) and two copies of a sentence age
    /// apart. It said "phantom power" until 2026-08-13, and that was wrong in
    /// a way that could cost someone money: phantom power is 48 volts on an
    /// XLR, and a condenser microphone bought on that promise would sit on
    /// this jack silent. What the radio actually supplies is a low-voltage
    /// electret bias on the mic jack — the vendor's manuals say about 3 V on
    /// the 6000 series and about 3.5 V on the 8000 series, so the label
    /// claims the kind of power rather than a number that is right on only
    /// one model.
    /// </summary>
    private const string MicBiasLabel = "Mic Bias (low-voltage electret mic power — not 48-volt phantom)";

    private const string MicBoostLabel = "Mic Boost (+20 dB)";

    private ValueFieldControl? _micGainControl;
    private CheckBox? _micBoostCheck;
    private CheckBox? _micBiasCheck;

    // ── PC-source stage-one gain (Track PC Gain, 2026-08-13) ──

    /// <summary>
    /// The Windows input level for the computer's chosen microphone, standing
    /// in the spot Mic Gain occupies when the radio's transmit source is PC.
    /// The Microphone section always shows the gain that actually applies to
    /// the current source: stage one of the capture chain is the radio's Mic
    /// Gain when the jack feeds TX, and the Windows input level when the
    /// computer does (see project_capture_then_sculpt).
    /// </summary>
    private ValueFieldControl? _pcLevelControl;

    /// <summary>
    /// The always-current sentence under the PC level: which Windows device
    /// the control actually moves — the honesty guarantee WindowsMicLevel's
    /// matching rules exist to earn — with mute leading when it applies. On
    /// the failure paths it takes the control's place in the tab ring and
    /// carries the reason, so a blind operator tabbing through lands on the
    /// explanation exactly where the control would have been.
    /// </summary>
    private TextBox? _pcLevelNote;

    /// <summary>The bound Core Audio endpoint. Null whenever the control is absent or refused.</summary>
    private WindowsMicLevel? _pcMicLevel;

    /// <summary>Microphone profile picker (Track F). Options are the
    /// operator's saved microphone profiles; refreshed on SetRig and after
    /// every save or delete.</summary>
    private CycleFieldControl? _micProfileControl;

    /// <summary>
    /// The silent-transmit warning (Sprint 31 Track S, #99), present only
    /// while the radio reports an empty mic-profile selection. A read-only
    /// TextBox rather than a label so it can be arrowed through and re-read —
    /// the connect-time announcement is heard once and this is where it lives
    /// afterwards. Collapsed on a healthy radio, so nobody whose transmit
    /// audio works ever meets it.
    /// </summary>
    private TextBox? _silentTxNote;

    /// <summary>The operator-initiated repair for the empty selection. Never
    /// fires on its own; see <see cref="LoadMicProfileForSilentTx"/> for what
    /// the ownership flag gates.</summary>
    private Button? _silentTxFixButton;

    private CheckBox? _companderCheck;
    private ValueFieldControl? _companderLevelControl;
    private CheckBox? _processorCheck;
    private CycleFieldControl? _processorSettingControl;
    private ValueFieldControl? _txFilterLowControl;
    private ValueFieldControl? _txFilterHighControl;
    private TextBlock? _filterWidthLabel;
    private CheckBox? _monitorCheck;
    private ValueFieldControl? _monitorLevelControl;
    private ValueFieldControl? _monitorPanControl;

    // ── PC Cleanup (Track I: TX noise reduction + gate + residual monitor) ──
    private CheckBox? _txNrCheck;
    private ValueFieldControl? _txNrStrengthControl;
    private CheckBox? _txGateCheck;
    private ValueFieldControl? _txGateMarginControl;
    private ValueFieldControl? _txGateAttackControl;
    private ValueFieldControl? _txGateHoldControl;
    private ValueFieldControl? _txGateReleaseControl;
    private ValueFieldControl? _txGateRangeControl;
    private TextBox? _txCleanupStatusBox;
    private CycleFieldControl? _txCleanupMonitorControl;
    private ValueFieldControl? _txCleanupMonitorVolumeControl;

    #endregion

    #region Tab 1: TX Audio

    private void BuildTxAudioTab()
    {
        // Section order is the setup walk-through, running outward from this
        // computer to the radio to the air (Noel, 2026-08-12): choose a
        // microphone, tell the radio to listen to it, shape it, band-limit
        // it, decide how you hear yourself, then put a signal out — the test
        // tone first because it is a known quantity, then the keyed
        // end-to-end check.
        //
        // It ran the other way round until 2026-08-12: Audio Check stood at
        // the top and every control it proves came after it. That order
        // taught a first-time operator nothing, and the first thing in the
        // window keyed the transmitter. The express lane for an operator who
        // is already set up survives in focus and tab order rather than in
        // layout — see FocusFirstControl and ApplyTxAudioTabOrder.
        BuildDeviceSection();

        // Microphone profiles come right after the device choice: name the
        // microphone, then apply everything that microphone needs — the
        // capture half here, the radio half by reference (Track F).
        BuildMicProfileSection();

        // Microphone section
        AddSectionHeader(TxAudioContent, "Microphone");

        // Mic source picker — the precondition for every honest measurement
        // this dialog makes. Verified live (2026-08-07): MicGain acts on the
        // SELECTED input; with a hand-mic PTT override the knob silently
        // tunes an idle PC stream. The picker reads and sets the radio's own
        // selection; when TX audio is PC-sourced the jack-only controls hide
        // and the Windows input level stands in (see PollMicSource).
        _micSourceControl = MakeCycle("Transmit audio from", new[] { "(waiting for radio)" });
        _micSourceControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null || _polling) return;
            string choice = _micSourceControl!.SelectedOption;
            if (!string.IsNullOrEmpty(choice) && choice[0] != '(')
                _rig.MicSource = choice;
        };
        // Ctrl+F1 explanations (#73). On-demand only — JJFlexHelp.Text is
        // invisible to the screen reader on focus; see JJFlexHelp.
        JJFlexHelp.SetText(_micSourceControl,
            "Chooses which microphone the radio transmits: a mic plugged into "
            + "the radio, or this computer's mic carried over the network. "
            + "Every control below acts on whichever source is selected here, "
            + "so set this first.");
        AddRadioControl(TxAudioContent, _micSourceControl);

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
        AddRadioControl(TxAudioContent, _micGainControl);

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
        AddToSection(TxAudioContent, _pcLevelControl);

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
        AddToSection(TxAudioContent, _pcLevelNote);

        _micBoostCheck = MakeToggle(MicBoostLabel);
        _micBoostCheck.Checked += (s, e) => SetToggle("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, true);
        _micBoostCheck.Unchecked += (s, e) => SetToggle("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, false);
        JJFlexHelp.SetText(_micBoostCheck,
            "Adds a fixed twenty decibel lift ahead of Mic Gain, for quiet "
            + "dynamic microphones. If the mic check only reaches Good with "
            + "Mic Gain pushed near the top, turn this on and bring the gain "
            + "back down. If it reports Hot with the gain already low, turn "
            + "this off.");
        AddRadioControl(TxAudioContent, _micBoostCheck);

        _micBiasCheck = MakeToggle(MicBiasLabel);
        _micBiasCheck.Checked += (s, e) => SetToggle("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, true);
        _micBiasCheck.Unchecked += (s, e) => SetToggle("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, false);
        JJFlexHelp.SetText(_micBiasCheck,
            "Sends the radio's low-voltage electret power up the mic cable. "
            + "Some headsets and desk mics need it to produce any audio at "
            + "all; it is not forty-eight volt phantom power for studio mics. "
            + "If your mic stays silent no matter the gain, try this.");
        AddRadioControl(TxAudioContent, _micBiasCheck);

        // Track I: PC Cleanup sits between the Microphone (capture) and the
        // radio's Processing (sculpt) because that is where it runs — the
        // radio cannot clean the room before its chain hears the audio, and
        // this can.
        BuildTxCleanupSection();

        // Processing section
        AddRadioSection(TxAudioContent, "Processing");

        _companderCheck = MakeToggle("Compander");
        _companderCheck.Checked += (s, e) =>
        {
            SetToggle("Compander", v => { if (_rig != null) _rig.Compander = v; }, true);
            if (_companderLevelControl != null) _companderLevelControl.Visibility = Visibility.Visible;
        };
        _companderCheck.Unchecked += (s, e) =>
        {
            SetToggle("Compander", v => { if (_rig != null) _rig.Compander = v; }, false);
            if (_companderLevelControl != null) _companderLevelControl.Visibility = Visibility.Collapsed;
        };
        JJFlexHelp.SetText(_companderCheck,
            "Evens out the difference between your loud and soft syllables "
            + "before they reach the air, so your average power rises without "
            + "your peaks getting any hotter. Turn it on for voice work, then "
            + "set the level below while listening on the TX Monitor.");
        AddToSection(TxAudioContent, _companderCheck);

        _companderLevelControl = MakeValue("Compander Level", 0, 100, 5);
        _companderLevelControl.Visibility = Visibility.Collapsed;
        _companderLevelControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.CompanderLevel = v;
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tx.compander_level", ("value", v)), VerbosityLevel.Terse);
            }
        };
        JJFlexHelp.SetText(_companderLevelControl,
            "How firmly the compander squeezes. Higher carries your voice "
            + "further but flattens it. Listen on the TX Monitor while you "
            + "adjust, and stop at the last level that still sounds like you.");
        AddToSection(TxAudioContent, _companderLevelControl);

        _processorCheck = MakeToggle("Speech Processor");
        _processorCheck.Checked += (s, e) =>
        {
            SetToggle("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, true);
            if (_processorSettingControl != null) _processorSettingControl.Visibility = Visibility.Visible;
        };
        _processorCheck.Unchecked += (s, e) =>
        {
            SetToggle("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, false);
            if (_processorSettingControl != null) _processorSettingControl.Visibility = Visibility.Collapsed;
        };
        JJFlexHelp.SetText(_processorCheck,
            "The radio's punch control for voice: it raises your average "
            + "power so more of your signal survives the noise at the far "
            + "end. Turn it on for weak-signal or pileup work, and pick how "
            + "hard it works with Processor Mode below.");
        AddToSection(TxAudioContent, _processorCheck);

        _processorSettingControl = MakeCycle("Processor Mode", new[] { "Normal", "DX", "DX+" });
        _processorSettingControl.Visibility = Visibility.Collapsed;
        _processorSettingControl.SelectionChanged += (s, idx) =>
        {
            if (_rig != null && !_polling)
            {
                // No Speak here. The cycle control announces its own value
                // through the accessibility tree since 2026-08-13; saying it
                // again would be the double-speak the old design's interrupt
                // was quietly masking.
                _rig.ProcessorSetting = (FlexBase.ProcessorSettings)idx;
            }
        };
        JJFlexHelp.SetText(_processorSettingControl,
            "Normal for everyday contacts, DX when conditions are rough, DX "
            + "plus when you need every last bit of punch and can live with "
            + "sounding processed. Step up only as far as conditions demand.");
        AddToSection(TxAudioContent, _processorSettingControl);

        // TX Filter section
        AddRadioSection(TxAudioContent, "TX Filter");

        _txFilterLowControl = MakeValue("TX Filter Low", 0, 9950, 50);
        _txFilterLowControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.TXFilterLow = v;
                UpdateFilterWidth();
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tx.filter_low", ("value", v)), VerbosityLevel.Terse);
            }
        };
        JJFlexHelp.SetText(_txFilterLowControl,
            "Where your transmitted audio starts, in hertz. One hundred to "
            + "three hundred is the usual range for voice: lower sounds "
            + "fuller, higher trims rumble and puts more of your power into "
            + "the part of speech that carries. Pair it with TX Filter High "
            + "and check the width readout below.");
        AddToSection(TxAudioContent, _txFilterLowControl);

        _txFilterHighControl = MakeValue("TX Filter High", 50, 10000, 50);
        _txFilterHighControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.TXFilterHigh = v;
                UpdateFilterWidth();
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tx.filter_high", ("value", v)), VerbosityLevel.Terse);
            }
        };
        JJFlexHelp.SetText(_txFilterHighControl,
            "Where your transmitted audio stops, in hertz. Around twenty-nine "
            + "hundred is the usual voice ceiling: higher sounds airier but "
            + "spends power where it helps intelligibility least, and "
            + "narrower — twenty-four hundred — puts real punch in a pileup. "
            + "The width readout below shows what the two filter edges give "
            + "you together.");
        AddToSection(TxAudioContent, _txFilterHighControl);

        _filterWidthLabel = new TextBlock
        {
            Text = "Width: --",
            Margin = new Thickness(2, 4, 2, 4),
            FontSize = 12
        };
        AutomationProperties.SetName(_filterWidthLabel, "TX filter width");
        AutomationProperties.SetLiveSetting(_filterWidthLabel, AutomationLiveSetting.Polite);
        AddToSection(TxAudioContent, _filterWidthLabel);

        // Monitor section. The header names the mode in phone modes so the
        // screen reader user knows which knob family they're on; in CW mode
        // today's behavior is unchanged (the CW monitor work is deferred
        // behind the CW pipeline rewrite).
        _monitorHeader = AddRadioSection(TxAudioContent, "TX Monitor");

        _monitorCheck = MakeToggle("TX Monitor");
        _monitorCheck.Checked += (s, e) =>
        {
            SetToggle("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, true);
            if (_monitorLevelControl != null) _monitorLevelControl.Visibility = Visibility.Visible;
            if (_monitorPanControl != null) _monitorPanControl.Visibility = Visibility.Visible;
        };
        _monitorCheck.Unchecked += (s, e) =>
        {
            SetToggle("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, false);
            if (_monitorLevelControl != null) _monitorLevelControl.Visibility = Visibility.Collapsed;
            if (_monitorPanControl != null) _monitorPanControl.Visibility = Visibility.Collapsed;
        };
        JJFlexHelp.SetText(_monitorCheck,
            "Plays your own transmitted audio back to you while you talk — "
            + "the most honest way to hear what the compander, processor and "
            + "filters are doing to your voice. Set the level below to where "
            + "your own voice informs without distracting.");
        AddToSection(TxAudioContent, _monitorCheck);

        _monitorLevelControl = MakeValue("Monitor Level", 0, 100, 5);
        _monitorLevelControl.Visibility = Visibility.Collapsed;
        _monitorLevelControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.SBMonitorLevel = v;
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tx.monitor_level", ("value", v)), VerbosityLevel.Terse);
            }
        };
        JJFlexHelp.SetText(_monitorLevelControl,
            "How loud your own transmitted audio plays back to you. It only "
            + "changes what you hear — never what goes out on the air.");
        AddToSection(TxAudioContent, _monitorLevelControl);

        _monitorPanControl = MakeValue("Monitor Pan", 0, 100, 5);
        _monitorPanControl.Visibility = Visibility.Collapsed;
        _monitorPanControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.SBMonitorPan = v;
                ScreenReaderOutput.Speak(Lexicon.Get("audio.tx.monitor_pan", ("value", v)), VerbosityLevel.Terse);
            }
        };
        JJFlexHelp.SetText(_monitorPanControl,
            "Moves your monitored voice between your left and right ear — "
            + "handy for keeping the far station in one ear and yourself in "
            + "the other. Listening only; it changes nothing on the air.");
        AddToSection(TxAudioContent, _monitorPanControl);

        // Built-in test tone — the mic replacement (Audio Track C). Late in
        // the walk: it is the first thing here that reaches the air, and it
        // is what the Audio Check below sends when you have no voice to send.
        BuildTestToneSection();

        // Reference audio (Sprint 33 Track I) — the same mic replacement as
        // the tone above, carrying a known VOICE instead of a sine. It sits
        // between the two because it is what the Audio Check below wants sent
        // whenever the question is "did that change help": a tone cannot
        // answer that, since nothing in a sine responds to a compressor, and
        // a person talking cannot either, because they never say it the same
        // way twice.
        BuildReferenceAudioSection();

        // Audio Check session — the "hear yourself" loop (QB Track G). Last,
        // because it keys the transmitter and proves everything above it.
        BuildAudioCheckSection();
    }

    /// <summary>
    /// Step one of the walk: which microphone this computer is listening to.
    /// </summary>
    /// <remarks>
    /// The Audio Devices picker was reachable from the Audio menu, Settings'
    /// Audio tab and a key command, but not from the Workshop — the one
    /// surface whose entire job is getting your audio right (Noel,
    /// 2026-08-12). Every measurement below this section is downstream of the
    /// answer, so the answer belongs at the top and in words: a bare "Audio
    /// Devices..." button would make an operator open a dialog to find out
    /// what they already chose.
    ///
    /// The name is read from audioDevices.xml via LoadSavedSelection, which
    /// does NOT enumerate — naming the device costs a file read, not a
    /// Pa_Initialize/Pa_Terminate cycle.
    /// </remarks>
    private void BuildDeviceSection()
    {
        AddSectionHeader(TxAudioContent, "This Computer");

        // Read-only EDIT rather than a label, same reasoning as the mic
        // reading below: focusable, review-readable, and the screen reader's
        // own read-current-control command speaks it without an app hotkey.
        _deviceReadingBox = new TextBox
        {
            Text = Lexicon.Get("audio.tx.microphone_checking"),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Margin = new Thickness(2),
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(_deviceReadingBox, "Microphone this computer is using");
        AddToSection(TxAudioContent, _deviceReadingBox);

        var deviceButton = new Button
        {
            Content = "Change Audio Devices...",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(deviceButton, "Change Audio Devices");
        deviceButton.Click += (s, e) =>
        {
            var open = OpenAudioDevices;
            if (open == null)
            {
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.tx.devices_cannot_open_from_here"),
                    VerbosityLevel.Critical);
                return;
            }
            open();
            // The picker may have changed the selection — say what it is now
            // rather than leaving a stale name sitting above the controls.
            RefreshDeviceReading(announce: true);
        };
        AddToSection(TxAudioContent, deviceButton);

        // "Is it actually working?" is the question that follows "which
        // microphone", so it belongs in the same section and one key away.
        // This deliberately OPENS the check rather than reimplementing it:
        // there is exactly one microphone probe and one set of words for what
        // it hears, and a second live-reading surface here would be the same
        // drift this whole arc keeps finding — two descriptions of one thing,
        // aging apart. Costs a dialog; buys one truth.
        var checkButton = new Button
        {
            Content = "Check Microphone...",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(checkButton, "Check Microphone");
        JJFlexHelp.SetText(checkButton,
            "Opens the Audio Devices window and listens to your microphone. "
            + "The radio is not involved and nothing is transmitted.");
        checkButton.Click += (s, e) => OpenMicrophoneCheck();
        AddToSection(TxAudioContent, checkButton);

        RefreshDeviceReading(announce: false);
    }

    /// <summary>
    /// Open the Audio Devices window with the microphone check already
    /// running. Goes straight to <see cref="AudioDevicesDialog.ShowPicker"/>
    /// rather than through the menu callback, because that callback takes no
    /// arguments and cannot say "start the check" — and an operator who
    /// pressed Check Microphone should not have to ask a second time on
    /// arrival.
    /// </summary>
    private void OpenMicrophoneCheck()
    {
        string? path = AudioDevicesPath?.Invoke();
        if (string.IsNullOrEmpty(path))
        {
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.tx.mic_check_settings_file_missing"),
                VerbosityLevel.Critical);
            return;
        }

        try
        {
            var cfg = AudioConfigSource?.Invoke();
            AudioDevicesDialog.ShowPicker(this, path, cfg,
                AudioConfigSave, startMicCheck: true);
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine(
                "AudioWorkshop: the microphone check could not open — " + ex.Message,
                System.Diagnostics.TraceLevel.Error);
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.tx.mic_check_could_not_open", ("reason", ex.Message)),
                VerbosityLevel.Critical);
            return;
        }

        // The picker may have changed the selection on its way out.
        RefreshDeviceReading(announce: true);
    }

    /// <summary>
    /// Re-read the chosen input device and update the "This Computer" line.
    /// Never guesses: when the path or the file is missing it says so, because
    /// a confidently wrong device name here is worse than an admission.
    /// </summary>
    private void RefreshDeviceReading(bool announce)
    {
        if (_deviceReadingBox == null) return;

        string text;
        try
        {
            string? path = AudioDevicesPath?.Invoke();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                text = Lexicon.Get("audio.tx.microphone_none_chosen");
            }
            else
            {
                var devices = new JJPortaudio.Devices(path);
                devices.LoadSavedSelection();
                string? name = devices.InputDevice?.Name;
                text = string.IsNullOrWhiteSpace(name)
                    ? Lexicon.Get("audio.tx.microphone_none_chosen")
                    : Lexicon.Get("audio.tx.microphone_named", ("device", name));
            }
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine(
                "AudioWorkshop: could not read the chosen input device — "
                + ex.Message, System.Diagnostics.TraceLevel.Warning);
            text = Lexicon.Get("audio.tx.microphone_unreadable");
        }

        _deviceReadingBox.Text = text;
        AutomationProperties.SetName(_deviceReadingBox, text);
        if (announce) ScreenReaderOutput.Speak(text, VerbosityLevel.Terse);

        // Every call here means the picker may just have changed which
        // microphone is chosen — and if the Windows input level is standing
        // in for Mic Gain right now, it must follow the choice rather than
        // keep moving the previous device's level.
        if (_pcSourceActive) BindPcLevel();
    }


    /// <summary>
    /// True when no input device has been chosen on this computer — the
    /// first-run state, and the one where the walk-through is worth
    /// presenting. See <see cref="FocusFirstControl"/>.
    /// </summary>
    private static bool NoInputDeviceChosen()
    {
        try
        {
            string? path = AudioDevicesPath?.Invoke();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return true;
            var devices = new JJPortaudio.Devices(path);
            devices.LoadSavedSelection();
            return string.IsNullOrWhiteSpace(devices.InputDevice?.Name);
        }
        catch
        {
            // Unreadable is not the same as unconfigured, and sending a
            // set-up operator to step one on a file-read hiccup would be the
            // more annoying of the two mistakes.
            return false;
        }
    }


    private void UpdateFilterWidth()
    {
        if (_txFilterLowControl == null || _txFilterHighControl == null || _filterWidthLabel == null) return;
        int low = _txFilterLowControl.Value;
        int high = _txFilterHighControl.Value;
        int width = high - low;
        string widthStr = width >= 1000 ? $"{width / 1000.0:0.#} kHz" : $"{width} Hz";
        _filterWidthLabel.Text = $"Width: {low} to {high} Hz ({widthStr})";
    }

    private void PollTxAudio()
    {
        if (_rig == null) return;
        _polling = true;
        try
        {
            PollMicSource();

            if (_micGainControl != null) _micGainControl.Value = _rig.MicGain;
            if (_micBoostCheck != null) _micBoostCheck.IsChecked = _rig.MicBoost == FlexBase.OffOnValues.on;
            if (_micBiasCheck != null) _micBiasCheck.IsChecked = _rig.MicBias == FlexBase.OffOnValues.on;

            bool companderOn = _rig.Compander == FlexBase.OffOnValues.on;
            if (_companderCheck != null) _companderCheck.IsChecked = companderOn;
            if (_companderLevelControl != null)
            {
                _companderLevelControl.Visibility = companderOn ? Visibility.Visible : Visibility.Collapsed;
                if (companderOn) _companderLevelControl.Value = _rig.CompanderLevel;
            }

            bool processorOn = _rig.ProcessorOn == FlexBase.OffOnValues.on;
            if (_processorCheck != null) _processorCheck.IsChecked = processorOn;
            if (_processorSettingControl != null)
            {
                _processorSettingControl.Visibility = processorOn ? Visibility.Visible : Visibility.Collapsed;
                if (processorOn) _processorSettingControl.SelectedIndex = (int)_rig.ProcessorSetting;
            }

            if (_txFilterLowControl != null) _txFilterLowControl.Value = _rig.TXFilterLow;
            if (_txFilterHighControl != null) _txFilterHighControl.Value = _rig.TXFilterHigh;
            UpdateFilterWidth();

            // Mode-aware monitor header — phone modes only. CW mode keeps
            // today's behavior untouched (CW monitor work is deferred behind
            // the CW pipeline rewrite).
            if (_monitorHeader != null)
            {
                string mode = _rig.Mode ?? "";
                bool isCW = mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase);
                string hdr = (isCW || string.IsNullOrEmpty(mode))
                    ? "TX Monitor"
                    : $"TX Monitor — {mode}";
                if ((_monitorHeader.Header as string) != hdr)
                {
                    _monitorHeader.Header = hdr;
                    AutomationProperties.SetName(_monitorHeader, hdr);
                }
            }

            bool monitorOn = _rig.Monitor == FlexBase.OffOnValues.on;
            if (_monitorCheck != null) _monitorCheck.IsChecked = monitorOn;
            if (_monitorLevelControl != null)
            {
                _monitorLevelControl.Visibility = monitorOn ? Visibility.Visible : Visibility.Collapsed;
                if (monitorOn) _monitorLevelControl.Value = _rig.SBMonitorLevel;
            }
            if (_monitorPanControl != null)
            {
                _monitorPanControl.Visibility = monitorOn ? Visibility.Visible : Visibility.Collapsed;
                if (monitorOn) _monitorPanControl.Value = _rig.SBMonitorPan;
            }

            // Track I: PC Cleanup controls and status.
            PollTxCleanup();

            // #99: the radio's mic-profile selection can go empty at any time
            // (a global profile loaded from any client does it), so this is
            // checked on every poll rather than once at open.
            UpdateSilentTxNote();
        }
        finally
        {
            _polling = false;
        }
    }

    private string[] _micSourceOptions = Array.Empty<string>();

    /// <summary>
    /// True while the radio's transmit source is PC and the section is showing
    /// the Windows input level instead of the jack controls. (Named
    /// _jackAnnotated until 2026-08-13, after an annotation scheme that no
    /// longer exists — the description-drift pattern, caught here.)
    /// </summary>
    private bool _pcSourceActive;

    /// <summary>
    /// Keep the mic source picker synced with the radio-reported input list
    /// and selection, and swap the Microphone section's gain controls to
    /// follow the source: the radio's three for the jack, the Windows input
    /// level for PC audio. Runs inside PollTxAudio's _polling guard.
    /// </summary>
    private void PollMicSource()
    {
        if (_rig == null || _micSourceControl == null) return;

        var list = _rig.MicSourceList;
        if (list.Count > 0)
        {
            bool changed = list.Count != _micSourceOptions.Length;
            if (!changed)
            {
                for (int i = 0; i < list.Count; i++)
                    if (!string.Equals(list[i], _micSourceOptions[i], StringComparison.Ordinal))
                    { changed = true; break; }
            }
            if (changed)
            {
                _micSourceOptions = list.ToArray();
                _micSourceControl.SuppressEvents = true;
                _micSourceControl.Options = _micSourceOptions;
                _micSourceControl.SuppressEvents = false;
            }

            string current = _rig.MicSource;
            int idx = Array.FindIndex(_micSourceOptions,
                o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx != _micSourceControl.SelectedIndex)
            {
                _micSourceControl.SuppressEvents = true;
                _micSourceControl.SelectedIndex = idx;
                _micSourceControl.SuppressEvents = false;
            }
        }

        // The jack-only controls follow the SELECTED source (that is what they
        // act on — verified live 2026-08-07). On PC audio they are hidden
        // outright rather than labelled as inapplicable.
        //
        // Noel, 2026-08-13, using this to set a level for real: "Mic level
        // seems to be the radio mic level. If you're using PC Audio that isn't
        // affected, so might want to hide those values if we're using pc as a
        // source just to keep it less confusing."
        //
        // He was right, and the previous handling was worse than it looked.
        // Mic Boost and Mic Bias were suffixed "radio mic jack only, not in
        // use" — but MIC GAIN, the control that most reads like the thing you
        // reach for when your level is wrong, was left completely unmarked.
        // So the one control an operator would actually grab was the one that
        // said nothing about doing nothing. The comment above the section
        // claimed jack-only controls annotate themselves; two of the three did.
        //
        // Hiding beats labelling here, and matches the house rule that
        // controls which cannot act stay out of the tab order: a live,
        // adjustable slider that changes nothing is a worse experience than an
        // absent one, because it invites the operator to solve their problem
        // with it. With PC audio the stage-one gain lives on the computer, so
        // the hole Mic Gain leaves is filled with the Windows input level for
        // the computer's chosen microphone — the section always shows the
        // gain that actually applies. See project_capture_then_sculpt.
        bool pcSource = string.Equals(_rig.MicSource, "PC", StringComparison.OrdinalIgnoreCase);
        if (pcSource != _pcSourceActive)
        {
            _pcSourceActive = pcSource;

            // Where focus stands is asked BEFORE anything hides. WPF pulls
            // keyboard focus off a collapsing element on a later dispatcher
            // pass, so asking afterwards is a race we do not need to run.
            bool focusWasOnJackGain = _micGainControl?.IsKeyboardFocusWithin == true;
            bool focusWasOnPcLevel = _pcLevelControl?.IsKeyboardFocusWithin == true
                || _pcLevelNote?.IsKeyboardFocusWithin == true;

            var vis = pcSource ? Visibility.Collapsed : Visibility.Visible;
            if (_micGainControl != null) _micGainControl.Visibility = vis;
            if (_micBoostCheck != null) _micBoostCheck.Visibility = vis;
            if (_micBiasCheck != null) _micBiasCheck.Visibility = vis;

            // Restore the plain labels: the suffix was doing the work of the
            // visibility change and is now noise on a control you can see
            // precisely because it applies.
            SetToggleLabel(_micBoostCheck, MicBoostLabel);
            SetToggleLabel(_micBiasCheck, MicBiasLabel);

            if (pcSource)
            {
                BindPcLevel();

                // Never leave a hidden control holding focus — and the
                // operator standing on Mic Gain was adjusting their transmit
                // gain, so hand them the gain that now applies rather than
                // dumping them back at the Start button.
                if (focusWasOnJackGain)
                {
                    if (_pcLevelControl != null && _pcLevelControl.IsEnabled
                        && _pcLevelControl.Visibility == Visibility.Visible)
                        _pcLevelControl.Focus();
                    else if (_pcLevelNote != null && _pcLevelNote.Visibility == Visibility.Visible)
                        _pcLevelNote.Focus();
                    else
                        _startCheckButton?.Focus();
                }
            }
            else
            {
                HidePcLevel();
                if (focusWasOnPcLevel)
                {
                    if (_micGainControl == null || !_micGainControl.Focus())
                        _startCheckButton?.Focus();
                }
            }
        }
    }

    // ---------------------------------------------- PC-source Windows level

    /// <summary>
    /// Bind the Windows input level to the computer's chosen microphone, or
    /// show the focusable note with the reason no confident match exists.
    /// Called on the source flipping to PC and again whenever the Audio
    /// Devices picker may have changed the selection.
    /// </summary>
    /// <remarks>
    /// The device is identified by the SAVED name from audioDevices.xml — a
    /// file read, exactly like RefreshDeviceReading above — and matched
    /// through <see cref="WindowsMicLevel.TryFindByName"/>, which touches
    /// Core Audio only. The distinction is load-bearing: enumerating
    /// PortAudio from this dialog while a radio connection is live risks
    /// disturbing the audio streams that connection depends on, so the
    /// Workshop never runs a Pa_Initialize/Pa_Terminate cycle.
    /// </remarks>
    private void BindPcLevel()
    {
        ReleasePcLevel();

        string? name = null;
        int savedHostApiTypeId = -1;
        try
        {
            string? path = AudioDevicesPath?.Invoke();
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                var devices = new JJPortaudio.Devices(path);
                devices.LoadSavedSelection();
                name = devices.InputDevice?.Name;
                savedHostApiTypeId = devices.InputDevice?.hostApiTypeId ?? -1;
            }
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine(
                "AudioWorkshop: could not read the chosen input device for the level control — "
                + ex.Message, System.Diagnostics.TraceLevel.Warning);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowPcLevelUnavailable(
                "No microphone has been chosen on this computer yet. Use Change Audio "
                + "Devices above to pick one, and its Windows input level will appear here.");
            return;
        }

        var level = WindowsMicLevel.TryFindByName(name, savedHostApiTypeId, out string whyNot);
        if (level == null)
        {
            ShowPcLevelUnavailable(whyNot);
            return;
        }

        _pcMicLevel = level;
        level.VolumeChanged += OnPcLevelVolumeChanged;

        try
        {
            if (_pcLevelControl != null)
            {
                _pcLevelControl.SuppressEvents = true;
                try { _pcLevelControl.Value = (int)Math.Round(level.Percent); }
                finally { _pcLevelControl.SuppressEvents = false; }
                _pcLevelControl.IsEnabled = true;
                _pcLevelControl.Visibility = Visibility.Visible;
            }
            RefreshPcLevelNote();
        }
        catch (Exception ex)
        {
            // Matched and then gone — a device can vanish between the match
            // and the first read. The failure shape is the same honest one.
            PcLevelFailed(ex);
        }
    }

    /// <summary>
    /// The honest-failure shape: the level control absent (and therefore out
    /// of the tab order), the note carrying the reason in its place. Silently
    /// moving some other microphone's level would be far worse than offering
    /// nothing — the operator cannot glance at the screen to catch it.
    /// </summary>
    private void ShowPcLevelUnavailable(string reason)
    {
        ReleasePcLevel();
        bool hadFocus = _pcLevelControl?.IsKeyboardFocusWithin == true;
        if (_pcLevelControl != null)
        {
            _pcLevelControl.IsEnabled = false;
            _pcLevelControl.Visibility = Visibility.Collapsed;
        }
        SetPcLevelNoteText(reason);
        if (_pcLevelNote != null)
        {
            _pcLevelNote.Visibility = Visibility.Visible;
            // A mid-use failure lands the operator on the explanation, not
            // in a void.
            if (hadFocus) _pcLevelNote.Focus();
        }
    }

    /// <summary>Source flipped back to the jack: the computer's level no longer applies.</summary>
    private void HidePcLevel()
    {
        ReleasePcLevel();
        if (_pcLevelControl != null)
        {
            _pcLevelControl.IsEnabled = false;
            _pcLevelControl.Visibility = Visibility.Collapsed;
        }
        if (_pcLevelNote != null) _pcLevelNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Let go of the Core Audio endpoint. Safe to call in any state; the
    /// Closed handler calls it too, because a COM endpoint and its volume
    /// callback must never outlive the dialog that subscribed them.
    /// </summary>
    private void ReleasePcLevel()
    {
        var level = _pcMicLevel;
        _pcMicLevel = null;
        if (level == null) return;
        level.VolumeChanged -= OnPcLevelVolumeChanged;
        level.Dispose();
    }

    /// <summary>
    /// Core Audio raises volume notifications on a COM worker thread — for
    /// external changes (Windows Settings, the Audio Devices sliders, another
    /// app) and for our own writes echoing back alike. Hop to the UI thread
    /// and re-read; the echo arrives holding the value the control already
    /// shows and therefore moves nothing.
    /// </summary>
    private void OnPcLevelVolumeChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var level = _pcMicLevel;
            if (level == null || _pcLevelControl == null) return;
            try
            {
                _pcLevelControl.SuppressEvents = true;
                try { _pcLevelControl.Value = (int)Math.Round(level.Percent); }
                finally { _pcLevelControl.SuppressEvents = false; }
                RefreshPcLevelNote();
            }
            catch (Exception ex)
            {
                PcLevelFailed(ex);
            }
        }));
    }

    /// <summary>
    /// The always-current sentence under the level: which Windows device the
    /// control actually moves, with mute leading when it applies — a Windows
    /// mute wins over every level slider, and it is the precise cause of an
    /// "every sample is digital silence" reading. Reads from the endpoint may
    /// throw when the device has gone away; callers own turning that into the
    /// failure shape.
    /// </summary>
    private void RefreshPcLevelNote()
    {
        var level = _pcMicLevel;
        if (level == null || _pcLevelNote == null) return;

        bool muted = level.Muted;
        float boost = level.HasBoost ? level.BoostDb : 0f;

        string text;
        if (muted)
        {
            text = level.FriendlyName + " is muted in Windows — a mute wins over every level "
                + "slider. Unmute it with Change Audio Devices above, or in Windows Sound settings.";
        }
        else
        {
            text = level.FollowsWindowsDefault
                ? "This device follows your Windows default microphone. Right now that is "
                  + level.FriendlyName + ", and the level above moves its Windows input level."
                : "The level above moves the Windows input level for " + level.FriendlyName
                  + " — the same level as Windows Sound settings.";
            if (level.HasBoost && boost > 0f)
            {
                // The boost slider itself lives in the Audio Devices window;
                // naming it here matters because a boost left at +30 dB is
                // the classic cause of a pinned reading nothing visible
                // explains, and the modern Settings app does not show boost
                // at all.
                text += $" Microphone Boost is turned up, plus {boost:F0} dB — if you are "
                    + "coming in hot, lower the boost in Change Audio Devices first.";
            }
        }
        SetPcLevelNoteText(text);
        _pcLevelNote.Visibility = Visibility.Visible;
    }

    private void SetPcLevelNoteText(string text)
    {
        if (_pcLevelNote == null) return;
        // Assign only on change: volume notifications can arrive in bursts,
        // and rewriting identical text would reset a screen reader's review
        // position for nothing.
        if (_pcLevelNote.Text == text) return;
        _pcLevelNote.Text = text;
        AutomationProperties.SetName(_pcLevelNote, text);
    }

    private void PcLevelFailed(Exception ex)
    {
        JJTrace.Tracing.TraceLine("AudioWorkshop: Windows input level failed — " + ex.Message,
            System.Diagnostics.TraceLevel.Error);
        ShowPcLevelUnavailable(
            "Windows stopped answering for this microphone — it may have been unplugged. "
            + "Choose a device with Change Audio Devices above, or adjust the level in "
            + "Windows Sound settings.");
    }

    private static void SetToggleLabel(CheckBox? cb, string label)
    {
        if (cb == null) return;
        cb.Content = label;
        AutomationProperties.SetName(cb, label);
    }

    #endregion
}
