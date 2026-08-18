using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// DSP controls track (2026-08-11) — the PC noise reduction room. The DSP
/// field group and the Ctrl+J layer are the fast in-context routes; this
/// dialog is the discoverable see-everything-at-once surface (the
/// AudioLevelsDialogs pattern): both PC-side engines with their strengths,
/// the capture duration, the capture button, and the saved-profile shelf —
/// load, save under a name, clear, and an Open Profiles Folder action so
/// ordinary file tools (and sharing with a friend) stay possible.
///
/// Opened from Slice menu > DSP > PC Noise Reduction > Noise Profiles, and
/// from the DSP field group's Noise Profiles button. Escape closes (house
/// rule via JJFlexDialog); every change applies and persists immediately —
/// there is no OK/Cancel because the pipeline is live and already saved.
/// </summary>
public sealed class NoiseProfilesDialog : JJFlexDialog
{
    private readonly FlexBase? _rig;
    private readonly RxAudioPipeline? _pipeline;
    private readonly Func<AudioOutputConfig?> _configSource;
    private readonly Action _persistDsp;

    private readonly CheckBox _rnnCheck;
    private readonly ValueFieldControl _rnnStrength;
    private readonly CheckBox _rnnVoiceOnly;
    private readonly CheckBox _subCheck;
    private readonly ValueFieldControl _subStrength;
    private readonly ValueFieldControl _subFloor;
    private readonly ValueFieldControl _duration;
    private readonly Button _captureButton;
    private readonly TextBlock _profileStatus;
    private readonly ListBox _profileList;
    private readonly TextBox _saveNameBox;

    private readonly DispatcherTimer _pollTimer;
    private bool _polling;

    public NoiseProfilesDialog(FlexBase? rig, RxAudioPipeline? pipeline,
        Func<AudioOutputConfig?> configSource, Action persistDsp)
    {
        _rig = rig;
        _pipeline = pipeline;
        _configSource = configSource;
        _persistDsp = persistDsp;

        Title = "Noise Profiles";
        Width = 460;
        SizeToContent = SizeToContent.Height;

        var panel = new StackPanel { Margin = new Thickness(12) };
        Content = panel;

        // === The two PC-side engines, with the knobs that shape them ===
        _rnnCheck = AddToggle(panel, "PC Neural NR",
            on => { if (_pipeline != null) _pipeline.RnnEnabled = on; });
        _rnnStrength = AddValue(panel, "PC Neural NR Strength", 0, 100, 5,
            PercentOf(_pipeline?.RnnStrength ?? 0.8f), "%",
            v => { if (_pipeline != null) _pipeline.RnnStrength = v / 100f; });
        _rnnVoiceOnly = AddToggle(panel, "PC Neural NR Voice Modes Only",
            on => { if (_pipeline != null) _pipeline.RnnAutoDisableNonVoice = on; });

        _subCheck = AddToggle(panel, "PC Spectral NR",
            on => { if (_pipeline != null) _pipeline.SpectralEnabled = on; });
        _subStrength = AddValue(panel, "PC Spectral NR Strength", 0, 100, 5,
            PercentOf(_pipeline?.SpectralStrength ?? 0.7f), "%",
            v => { if (_pipeline != null) _pipeline.SpectralStrength = v / 100f; });
        _subFloor = AddValue(panel, "PC Spectral NR Floor", 0, 20, 1,
            PercentOf(_pipeline?.SpectralFloor ?? 0.02f), "%",
            v => { if (_pipeline != null) _pipeline.SpectralFloor = v / 100f; });

        // Recommended levels — the ratified presets. With both stages on the
        // combined wetness compounds, so the balanced trio is deliberately
        // gentler than either stage's solo default.
        var recommendButton = MakeButton("Apply Recommended Levels",
            "Set strength and floor to the recommended values for the stages that are on");
        recommendButton.Click += (s, e) => ApplyRecommendedLevels();
        panel.Children.Add(recommendButton);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        // === Capture ===
        var cfg = _configSource();
        _duration = AddValue(panel, "Capture Duration", 1, 5, 1,
            Math.Clamp(cfg?.SpectralSubSampleDuration ?? 3, 1, 5), "seconds",
            v =>
            {
                var c = _configSource();
                if (c != null)
                {
                    c.SpectralSubSampleDuration = v;
                    NoiseCaptureNarrator.AudioConfigSave?.Invoke();
                }
            });

        _captureButton = MakeButton("Capture Noise Profile",
            "Capture a noise profile from the current audio");
        _captureButton.Click += (s, e) =>
        {
            var c = _configSource();
            NoiseCaptureNarrator.Toggle(_rig, _pipeline,
                c?.SpectralSubSampleDuration ?? 3, onFinished: RefreshAll);
        };
        panel.Children.Add(_captureButton);
        NoiseCaptureNarrator.StateChanged += OnCaptureStateChanged;
        Closed += (s, e) => NoiseCaptureNarrator.StateChanged -= OnCaptureStateChanged;

        // Loaded-profile readout — focusable, speaks on entry.
        _profileStatus = new TextBlock
        {
            Margin = new Thickness(4, 6, 4, 2),
            Focusable = true,
            IsHitTestVisible = true,
            Text = "Noise profile: none"
        };
        _profileStatus.GotFocus += (s, e) =>
        {
            // Refresh the text so the accessible name is current when the
            // screen reader reads it on focus entry.
            //
            // DELETED 2026-08-18: speaking it too. This TextBlock is
            // Focusable with an AutomationProperties.Name kept up to date by
            // the poll, so the screen reader announces it on focus by itself.
            // Speaking the same text with interrupt cut that announcement
            // mid-word and then repeated it - the taxonomy's "speaking a
            // focused control's own name" case exactly.
            UpdateProfileStatus();
        };
        panel.Children.Add(_profileStatus);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        // === The saved-profile shelf ===
        var listLabel = new TextBlock { Text = "Saved profiles:", Margin = new Thickness(4, 0, 4, 2) };
        panel.Children.Add(listLabel);

        _profileList = new ListBox
        {
            MinHeight = 80,
            MaxHeight = 160,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_profileList, "Saved noise profiles");
        panel.Children.Add(_profileList);

        var loadButton = MakeButton("Load Selected Profile",
            "Load the selected noise profile into PC Spectral NR");
        loadButton.Click += (s, e) => LoadSelected();
        panel.Children.Add(loadButton);

        // Save-as: a name box and the button that uses it.
        var savePanel = new StackPanel { Orientation = Orientation.Horizontal };
        _saveNameBox = new TextBox
        {
            MinWidth = 200,
            Margin = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Text = SuggestProfileName()
        };
        AutomationProperties.SetName(_saveNameBox, "Profile name to save as");
        savePanel.Children.Add(_saveNameBox);
        var saveButton = MakeButton("Save Current As",
            "Save the current noise profile under the name in the profile name box");
        saveButton.Click += (s, e) => SaveCurrentAs();
        savePanel.Children.Add(saveButton);
        panel.Children.Add(savePanel);

        var clearButton = MakeButton("Clear Loaded Profile",
            "Clear the loaded noise profile so PC Spectral NR has nothing to subtract");
        clearButton.Click += (s, e) => ClearProfile();
        panel.Children.Add(clearButton);

        var folderButton = MakeButton("Open Profiles Folder",
            "Open the noise profiles folder in File Explorer");
        folderButton.Click += (s, e) =>
        {
            ScreenReaderOutput.Speak(NoiseProfileStore.OpenFolder()
                ? "Profiles folder opened in File Explorer"
                : "Could not open the profiles folder", VerbosityLevel.Terse, interrupt: true);
        };
        panel.Children.Add(folderButton);

        var closeButton = MakeButton("Close", "Close the Noise Profiles dialog");
        closeButton.IsCancel = true;
        closeButton.Click += (s, e) => Close();
        panel.Children.Add(closeButton);

        // 2 Hz poll keeps the fields honest when another surface (the DSP
        // field group, the Ctrl+J layer) moves a value while this sits open.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += (s, e) => PollValues();
        _pollTimer.Start();
        Closed += (s, e) => _pollTimer.Stop();

        RefreshAll();
    }

    private static int PercentOf(float value) => (int)Math.Round(Math.Clamp(value, 0f, 1f) * 100);

    // ── construction helpers ─────────────────────────────────────────

    private ValueFieldControl AddValue(StackPanel panel, string label, int min, int max,
        int step, int initial, string unit, Action<int> setter)
    {
        var ctl = new ValueFieldControl();
        ctl.Setup(label, min, max, step, initial, 0, unit);
        ctl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            setter(v);
            _persistDsp();
        };
        panel.Children.Add(ctl);
        return ctl;
    }

    private CheckBox AddToggle(StackPanel panel, string label, Action<bool> setter)
    {
        var cb = new CheckBox
        {
            Content = label,
            Margin = new Thickness(2, 4, 2, 4),
            FontSize = 12
        };
        AutomationProperties.SetName(cb, label);
        cb.Checked += (s, e) => Toggle(label, true, setter);
        cb.Unchecked += (s, e) => Toggle(label, false, setter);
        panel.Children.Add(cb);
        return cb;
    }

    private void Toggle(string label, bool on, Action<bool> setter)
    {
        if (_polling || _pipeline == null) return;
        setter(on);

        // DELETED: the CheckBox already announces its own new state through
        // UIA, on the control that has focus. Speaking "{label} on" as well
        // meant NVDA's announcement was cut off mid-word and then restated -
        // cutoff plus double-speak on every toggle. Surveyed 2026-08-18, same
        // defect class as the device picker rows (task #63).
        _persistDsp();
    }

    private static Button MakeButton(string content, string accessibleName)
    {
        var b = new Button
        {
            Content = content,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(b, accessibleName);
        return b;
    }

    // ── actions ──────────────────────────────────────────────────────

    private void ApplyRecommendedLevels()
    {
        if (_pipeline == null) { ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Terse, interrupt: true); return; }
        bool rnn = _pipeline.RnnEnabled;
        bool sub = _pipeline.SpectralEnabled;
        string spoken;
        if (rnn && sub)
        {
            // Both stages on: re-balanced trio — combined wetness compounds,
            // and aggressive subtraction pushes the neural stage off its
            // training distribution (multithreading memo, 2026-05-04).
            _pipeline.SpectralStrength = 0.45f;
            _pipeline.SpectralFloor = 0.04f;
            _pipeline.RnnStrength = 0.65f;
            spoken = "Balanced for both stages: spectral strength 45 percent, floor 4 percent, neural strength 65 percent";
        }
        else if (sub)
        {
            _pipeline.SpectralStrength = 0.7f;
            _pipeline.SpectralFloor = 0.02f;
            spoken = "Spectral strength 70 percent, floor 2 percent";
        }
        else if (rnn)
        {
            _pipeline.RnnStrength = 0.8f;
            spoken = "Neural strength 80 percent";
        }
        else
        {
            ScreenReaderOutput.Speak("Turn on a PC noise reduction stage first",
                VerbosityLevel.Terse, interrupt: true);
            return;
        }
        _persistDsp();
        PollValues();
        EarconPlayer.ConfirmTone();
        ScreenReaderOutput.Speak(spoken, VerbosityLevel.Terse, interrupt: true);
    }

    private void LoadSelected()
    {
        if (_pipeline == null) { ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Terse, interrupt: true); return; }
        if (_profileList.SelectedItem is not ListBoxItem item ||
            item.Tag is not NoiseProfileStore.ProfileFile profile)
        {
            ScreenReaderOutput.Speak("Select a profile in the list first", VerbosityLevel.Terse, interrupt: true);
            return;
        }
        if (_pipeline.LoadNoiseProfile(profile.Path))
        {
            RememberProfilePath(profile.Path);
            string tail = _pipeline.SpectralEnabled
                ? ""
                : " PC Spectral NR is off — Control J then Shift S turns it on.";
            EarconPlayer.ConfirmTone();
            ScreenReaderOutput.Speak($"Loaded noise profile: {profile.Describe()}.{tail}",
                VerbosityLevel.Terse, interrupt: true);
        }
        else
        {
            EarconPlayer.Warning1Beep();
            ScreenReaderOutput.Speak("Could not load that profile", VerbosityLevel.Terse, interrupt: true);
        }
        RefreshAll();
    }

    private void SaveCurrentAs()
    {
        if (_pipeline == null) { ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Terse, interrupt: true); return; }
        if (!_pipeline.HasNoiseProfile)
        {
            EarconPlayer.Warning1Beep();
            ScreenReaderOutput.Speak("No noise profile to save. Capture one first.",
                VerbosityLevel.Terse, interrupt: true);
            return;
        }
        string name = _saveNameBox.Text.Trim();
        if (name.Length == 0) name = SuggestProfileName();

        // Metadata: prefer what the capture stamped this session, fall back
        // to the rig's current band and antenna.
        string band = NoiseCaptureNarrator.LastCaptureBand;
        string antenna = NoiseCaptureNarrator.LastCaptureAntenna;
        if (string.IsNullOrEmpty(band) && _rig != null) band = NoiseProfileStore.BandLabelFor(_rig);
        if (string.IsNullOrEmpty(antenna) && _rig != null) antenna = _rig.RXAntennaName ?? "";

        string path = NoiseProfileStore.PathForName(name);
        if (_pipeline.SaveNoiseProfile(path, name, band, antenna))
        {
            // Round-trip through load so the in-memory profile carries the
            // saved name (the engine only names profiles on load).
            _pipeline.LoadNoiseProfile(path);
            RememberProfilePath(path);
            EarconPlayer.ConfirmTone();
            ScreenReaderOutput.Speak($"Saved noise profile {name}", VerbosityLevel.Terse, interrupt: true);
        }
        else
        {
            EarconPlayer.Warning1Beep();
            ScreenReaderOutput.Speak("Could not save the profile", VerbosityLevel.Terse, interrupt: true);
        }
        RefreshAll();
    }

    private void ClearProfile()
    {
        if (_pipeline == null) { ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Terse, interrupt: true); return; }
        if (!_pipeline.HasNoiseProfile)
        {
            ScreenReaderOutput.Speak("No noise profile is loaded", VerbosityLevel.Terse, interrupt: true);
            return;
        }
        _pipeline.ClearNoiseProfile();
        RememberProfilePath("");
        EarconPlayer.ConfirmTone();
        ScreenReaderOutput.Speak("Noise profile cleared", VerbosityLevel.Terse, interrupt: true);
        RefreshAll();
    }

    private void RememberProfilePath(string path)
    {
        var cfg = _configSource();
        if (cfg != null)
        {
            cfg.NoiseProfileLastPath = path;
            NoiseCaptureNarrator.AudioConfigSave?.Invoke();
        }
    }

    private string SuggestProfileName()
    {
        string band = _rig != null ? NoiseProfileStore.BandLabelFor(_rig) : "";
        string antenna = _rig?.RXAntennaName ?? "";
        string name = (band + " " + antenna).Trim();
        return name.Length > 0 ? name : "Noise profile " + DateTime.Now.ToString("yyyy-MM-dd");
    }

    // ── refresh ──────────────────────────────────────────────────────

    private void OnCaptureStateChanged()
    {
        bool running = NoiseCaptureNarrator.IsRunning;
        _captureButton.Content = running ? "Cancel Noise Capture" : "Capture Noise Profile";
        AutomationProperties.SetName(_captureButton,
            running ? "Cancel the noise capture in progress"
                    : "Capture a noise profile from the current audio");
        UpdateProfileStatus();
    }

    private void UpdateProfileStatus()
    {
        string text;
        if (NoiseCaptureNarrator.IsRunning)
            text = "Noise profile: capturing now";
        else if (_pipeline == null)
            text = "Noise profile: no radio connected";
        else if (_pipeline.HasNoiseProfile)
        {
            string name = _pipeline.NoiseProfileName;
            text = string.IsNullOrEmpty(name)
                ? "Noise profile: captured this session"
                : $"Noise profile: {name}";
        }
        else
            text = "Noise profile: none. Capture one, or load a saved profile.";

        if (text != _profileStatus.Text)
        {
            _profileStatus.Text = text;
            if (!_profileStatus.IsKeyboardFocused)
                AutomationProperties.SetName(_profileStatus, text);
        }
    }

    private void RefreshProfileList()
    {
        _profileList.Items.Clear();
        foreach (var profile in NoiseProfileStore.Enumerate())
        {
            var item = new ListBoxItem { Content = profile.Describe(), Tag = profile };
            AutomationProperties.SetName(item, profile.Describe());
            _profileList.Items.Add(item);
        }
        if (_profileList.Items.Count == 0)
        {
            var none = new ListBoxItem { Content = "No saved profiles yet", IsEnabled = false };
            AutomationProperties.SetName(none, "No saved profiles yet");
            _profileList.Items.Add(none);
        }
    }

    private void RefreshAll()
    {
        UpdateProfileStatus();
        RefreshProfileList();
        PollValues();
    }

    /// <summary>Sync every control from the live pipeline, silently.</summary>
    private void PollValues()
    {
        if (_pipeline == null) return;
        _polling = true;
        try
        {
            SyncToggle(_rnnCheck, _pipeline.RnnEnabled);
            SyncValue(_rnnStrength, PercentOf(_pipeline.RnnStrength));
            SyncToggle(_rnnVoiceOnly, _pipeline.RnnAutoDisableNonVoice);
            SyncToggle(_subCheck, _pipeline.SpectralEnabled);
            SyncValue(_subStrength, PercentOf(_pipeline.SpectralStrength));
            SyncValue(_subFloor, PercentOf(_pipeline.SpectralFloor));
            UpdateProfileStatus();
        }
        finally
        {
            _polling = false;
        }
    }

    private static void SyncValue(ValueFieldControl ctl, int value)
    {
        ctl.SuppressEvents = true;
        ctl.Value = value;
        ctl.SuppressEvents = false;
    }

    private static void SyncToggle(CheckBox cb, bool value)
    {
        if (cb.IsChecked != value) cb.IsChecked = value;
    }
}
