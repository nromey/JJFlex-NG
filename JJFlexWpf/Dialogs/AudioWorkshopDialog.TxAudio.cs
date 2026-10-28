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

    private CheckBox? _micBoostCheck;
    private CheckBox? _micBiasCheck;

    // Mic Gain and the PC-source Windows input level (Track PC Gain,
    // 2026-08-13) live in TransmitGainControls since #835, so this window and
    // Check your audio host one implementation; see _gain.

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
        AddSectionHeader(TransmitSettingsContent, "Microphone");

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
        AddRadioControl(TransmitSettingsContent, _micSourceControl);

        // MIC GAIN AND THE WINDOWS INPUT LEVEL ARE BACK HERE (#835). They
        // moved to Hear Yourself on 2026-08-25 so the adjust-and-listen loop
        // never crossed a category. Hear Yourself is its own window now, Check
        // your audio, and it hosts the same two controls beside its reading;
        // this section keeps them because this is the room for adjusting. One
        // implementation (TransmitGainControls), two hosts, one setting each.
        // Only one of the two is ever visible: Mic Gain for the radio's jack,
        // the Windows level when this computer is the source.
        AddRadioControl(TransmitSettingsContent, _gain.MicGain);
        AddToSection(TransmitSettingsContent, _gain.PcLevel);
        AddToSection(TransmitSettingsContent, _gain.PcLevelNote);

        _micBoostCheck = MakeToggle(MicBoostLabel);
        _micBoostCheck.Checked += (s, e) => SetToggle("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, true);
        _micBoostCheck.Unchecked += (s, e) => SetToggle("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, false);
        JJFlexHelp.SetText(_micBoostCheck,
            "Adds a fixed twenty decibel lift ahead of Mic Gain, for quiet "
            + "dynamic microphones. If the mic check only reaches Good with "
            + "Mic Gain pushed near the top, turn this on and bring the gain "
            + "back down. If it reports Hot with the gain already low, turn "
            + "this off.");
        AddRadioControl(TransmitSettingsContent, _micBoostCheck);

        _micBiasCheck = MakeToggle(MicBiasLabel);
        _micBiasCheck.Checked += (s, e) => SetToggle("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, true);
        _micBiasCheck.Unchecked += (s, e) => SetToggle("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, false);
        JJFlexHelp.SetText(_micBiasCheck,
            "Sends the radio's low-voltage electret power up the mic cable. "
            + "Some headsets and desk mics need it to produce any audio at "
            + "all; it is not forty-eight volt phantom power for studio mics. "
            + "If your mic stays silent no matter the gain, try this.");
        AddRadioControl(TransmitSettingsContent, _micBiasCheck);

        // Track I: PC Cleanup sits between the Microphone (capture) and the
        // radio's Processing (sculpt) because that is where it runs — the
        // radio cannot clean the room before its chain hears the audio, and
        // this can.
        BuildTxCleanupSection();

        // Processing section
        AddRadioSection(TransmitSettingsContent, "Processing");

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
        AddToSection(TransmitSettingsContent, _companderCheck);

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
        AddToSection(TransmitSettingsContent, _companderLevelControl);

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
        AddToSection(TransmitSettingsContent, _processorCheck);

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
        AddToSection(TransmitSettingsContent, _processorSettingControl);

        // TX Filter section
        AddRadioSection(TransmitSettingsContent, "TX Filter");

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
        AddToSection(TransmitSettingsContent, _txFilterLowControl);

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
        AddToSection(TransmitSettingsContent, _txFilterHighControl);

        _filterWidthLabel = new TextBlock
        {
            Text = "Width: --",
            Margin = new Thickness(2, 4, 2, 4),
            FontSize = 12
        };
        AutomationProperties.SetName(_filterWidthLabel, "TX filter width");
        AutomationProperties.SetLiveSetting(_filterWidthLabel, AutomationLiveSetting.Polite);
        AddToSection(TransmitSettingsContent, _filterWidthLabel);

        // Monitor section. The header names the mode in phone modes so the
        // screen reader user knows which knob family they're on; in CW mode
        // today's behavior is unchanged (the CW monitor work is deferred
        // behind the CW pipeline rewrite).
        _monitorHeader = AddRadioSection(TransmitSettingsContent, "TX Monitor");

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
        AddToSection(TransmitSettingsContent, _monitorCheck);

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
        AddToSection(TransmitSettingsContent, _monitorLevelControl);

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
        AddToSection(TransmitSettingsContent, _monitorPanControl);

        // The test tone, the reference recording and the Audio Check came
        // next, as the Hear Yourself category, until #835 moved them into
        // Check your audio — the button at the end of the toolbar.
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
        AddSectionHeader(ThisComputerContent, "This Computer");

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
        AddToSection(ThisComputerContent, _deviceReadingBox);

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
        AddToSection(ThisComputerContent, deviceButton);

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
        AddToSection(ThisComputerContent, checkButton);

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
        // Every live binding, not only this window's: Check your audio has its
        // own (fix round 1).
        TransmitGainControls.MicrophoneChoiceMayHaveChanged(_gain);
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

    // Whether this computer is the transmit source is TransmitGainControls'
    // PcSourceActive since #835; this window kept a flag of its own until then.

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
        // outright rather than labelled as inapplicable (Noel, 2026-08-13: "might
        // want to hide those values if we're using pc as a source just to keep
        // it less confusing"). A live slider that changes nothing invites the
        // operator to solve their problem with it. Mic Gain and the Windows
        // level that stands in for it are TransmitGainControls' (#835); Mic
        // Boost and Mic Bias follow the same flip here.
        if (_gain.Poll(_rig, () => _micSourceControl?.Focus()))
        {
            bool pc = _gain.PcSourceActive;
            // Never leave a control about to hide holding focus.
            if (pc && (_micBoostCheck?.IsKeyboardFocusWithin == true || _micBiasCheck?.IsKeyboardFocusWithin == true))
                _micSourceControl?.Focus();
            var vis = pc ? Visibility.Collapsed : Visibility.Visible;
            if (_micBoostCheck != null) _micBoostCheck.Visibility = vis;
            if (_micBiasCheck != null) _micBiasCheck.Visibility = vis;
            SetToggleLabel(_micBoostCheck, MicBoostLabel);
            SetToggleLabel(_micBiasCheck, MicBiasLabel);
        }
    }

    private static void SetToggleLabel(CheckBox? cb, string label)
    {
        if (cb == null) return;
        cb.Content = label;
        AutomationProperties.SetName(cb, label);
    }

    #endregion
}
