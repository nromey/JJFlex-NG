using System;
using System.Linq;
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
/// Opened from Audio menu > DSP > PC Noise Reduction > Noise Profiles, and
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
    private readonly NoiseController? _noise;
    private readonly Controls.CycleFieldControl _spectralEngine;
    private readonly Controls.CycleFieldControl _neuralEngine;
    private string[] _spectralIds = Array.Empty<string>();
    private string[] _neuralIds = Array.Empty<string>();

    private readonly CheckBox _rnnCheck;
    private readonly ValueFieldControl _rnnStrength;
    private readonly CheckBox _subCheck;
    private readonly ValueFieldControl _subStrength;
    private readonly ValueFieldControl _subFloor;
    private readonly ValueFieldControl _capturedFloor;
    private readonly ValueFieldControl _duration;
    private readonly Button _captureButton;
    private readonly TextBlock _profileStatus;
    private readonly ListBox _profileList;
    private readonly TextBox _saveNameBox;

    private readonly DispatcherTimer _pollTimer;
    private bool _polling;

    public NoiseProfilesDialog(FlexBase? rig, RxAudioPipeline? pipeline,
        Func<AudioOutputConfig?> configSource, Action persistDsp, NoiseController? noise = null)
    {
        _noise = noise;
        _rig = rig;
        _pipeline = pipeline;
        _configSource = configSource;
        _persistDsp = persistDsp;

        Title = "Noise Profiles";
        Width = 460;
        SizeToContent = SizeToContent.Height;

        var panel = new StackPanel { Margin = new Thickness(12) };
        Content = panel;

        // === The computer's two slots, in the same shape as Home's (Sprint 49
        // Track E, E11): each slot's engine, its On, its Strength, so this
        // window cannot disagree with the expander. Then Captured noise's own
        // two levers, since this is its window. "Apply Recommended Levels" is
        // gone: it is the "Balanced capture" preset now (ruled, departure D3).
        // The switches still answer with a tone only (#128): the controller
        // runs them quietly, and the checkbox says its own state.
        _spectralEngine = AddEngineField(panel, Lexicon.Get("audio.noise_profiles_window.spectral_engine"), DenoiseFamily.Spectral, out _spectralIds);
        _subCheck = AddToggle(panel, Lexicon.Get("audio.noise_profiles_window.spectral_on"),
            on => _noise?.Quietly(() => _noise.SetSlotOn(DenoiseFamily.Spectral, on, "noise profiles")));
        _subStrength = AddValue(panel, Lexicon.Get("audio.noise_profiles_window.spectral_strength"), 0, 100, 5,
            SlotStrength(DenoiseFamily.Spectral), "%",
            v => SetSlotStrength(DenoiseFamily.Spectral, v));
        _neuralEngine = AddEngineField(panel, Lexicon.Get("audio.noise_profiles_window.neural_engine"), DenoiseFamily.Neural, out _neuralIds);
        _rnnCheck = AddToggle(panel, Lexicon.Get("audio.noise_profiles_window.neural_on"),
            on => _noise?.Quietly(() => _noise.SetSlotOn(DenoiseFamily.Neural, on, "noise profiles")));
        _rnnStrength = AddValue(panel, Lexicon.Get("audio.noise_profiles_window.neural_strength"), 0, 100, 5,
            SlotStrength(DenoiseFamily.Neural), "%",
            v => SetSlotStrength(DenoiseFamily.Neural, v));
        _subFloor = AddValue(panel, Lexicon.Get("audio.noise_profiles_window.captured_amount"), 0, 100, 1,
            PercentOf(_pipeline?.SpectralStrength ?? 0.7f), "%",
            v => { if (_pipeline != null) _pipeline.SpectralStrength = v / 100f; });
        _capturedFloor = AddValue(panel, Lexicon.Get("audio.noise_profiles_window.captured_floor"), 0, 20, 1,
            PercentOf(_pipeline?.SpectralFloor ?? 0.02f), "%",
            v => { if (_pipeline != null) _pipeline.SpectralFloor = v / 100f; });

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
                ? Lexicon.Get("audio.noise.folder_opened")
                : Lexicon.Get("audio.noise.folder_open_failed"),
                VerbosityLevel.Terse, interrupt: true);
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

        // Sprint 32 Track E, #128. These same PC noise-reduction switches tone
        // from the Home panel and from the Ctrl+J chords; this dialog was the
        // silent third road. The tone is not a substitute for the speech
        // deleted below — that was removed because it DUPLICATED what the
        // checkbox already says, and a tone duplicates nothing.
        EarconPlayer.ToggleTone(on);

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

    private Controls.CycleFieldControl AddEngineField(StackPanel panel, string label, DenoiseFamily family, out string[] ids)
    {
        var field = new Controls.CycleFieldControl();
        var engines = _noise?.Engines(DenoiseGroup.NoiseReduction).Where(e => e.Side == DenoiseSide.Pc && e.Family == family).ToArray()
            ?? Array.Empty<DenoiseEngineEntry>();
        ids = engines.Select(e => e.Id).ToArray();
        string chosen = _noise?.SlotEngine(family) ?? "";
        field.Setup(label, engines.Select(e => _noise!.MarkedName(e.Id)).ToArray(), Math.Max(0, Array.IndexOf(ids, chosen)));
        var list = ids;
        field.SelectionChanged += (s, i) =>
        {
            if (_polling || _noise == null || i < 0 || i >= list.Length) return;
            _noise.NoteSlotChoicePending(family, list[i]);
            _noise.ChooseSlotEngine(family, list[i], "noise profiles");
        };
        panel.Children.Add(field);
        return field;
    }

    private int SlotStrength(DenoiseFamily family)
    {
        var slot = family == DenoiseFamily.Spectral ? _pipeline?.SpectralSlot : _pipeline?.NeuralSlot;
        return slot == null ? 100 : PercentOf(slot.GetStrength(slot.ChosenEngineId));
    }

    private void SetSlotStrength(DenoiseFamily family, int percent)
    {
        var slot = family == DenoiseFamily.Spectral ? _pipeline?.SpectralSlot : _pipeline?.NeuralSlot;
        if (slot == null || _noise == null) return;
        var lever = _noise.Levers(slot.ChosenEngineId, false).FirstOrDefault(l => l.Kind == NoiseLeverKind.Strength);
        if (lever != null) _noise.Quietly(() => _noise.Set(lever, percent, null, "noise profiles"));
    }

    private void LoadSelected()
    {
        if (_pipeline == null) { ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Terse, interrupt: true); return; }
        if (_profileList.SelectedItem is not ListBoxItem item ||
            item.Tag is not NoiseProfileStore.ProfileFile profile)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise.select_a_profile_first"), VerbosityLevel.Terse, interrupt: true);
            return;
        }
        if (_pipeline.LoadNoiseProfile(profile.Path))
        {
            RememberProfilePath(profile.Path);
            string tail = _pipeline.SpectralEnabled
                ? ""
                : " " + Lexicon.Get("audio.noise.spectral_is_off_hint");
            EarconPlayer.ConfirmTone();
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.noise.profile_loaded", ("profile", profile.Describe())) + tail,
                VerbosityLevel.Terse, interrupt: true);
        }
        else
        {
            EarconPlayer.Warning1Beep();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise.load_failed"), VerbosityLevel.Terse, interrupt: true);
        }
        RefreshAll();
    }

    private void SaveCurrentAs()
    {
        if (_pipeline == null) { ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Terse, interrupt: true); return; }
        if (!_pipeline.HasNoiseProfile)
        {
            EarconPlayer.Warning1Beep();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise.nothing_to_save"),
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
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise.profile_saved", ("profile", name)),
                VerbosityLevel.Terse, interrupt: true);
        }
        else
        {
            EarconPlayer.Warning1Beep();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise.save_failed"), VerbosityLevel.Terse, interrupt: true);
        }
        RefreshAll();
    }

    private void ClearProfile()
    {
        if (_pipeline == null) { ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Terse, interrupt: true); return; }
        if (!_pipeline.HasNoiseProfile)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise.none_loaded"), VerbosityLevel.Terse, interrupt: true);
            return;
        }
        _pipeline.ClearNoiseProfile();
        RememberProfilePath("");
        EarconPlayer.ConfirmTone();
        ScreenReaderOutput.Speak(Lexicon.Get("audio.noise.profile_cleared"), VerbosityLevel.Terse, interrupt: true);
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
            SyncValue(_rnnStrength, SlotStrength(DenoiseFamily.Neural));
            SyncToggle(_subCheck, _pipeline.SpectralEnabled);
            SyncValue(_subStrength, SlotStrength(DenoiseFamily.Spectral));
            SyncValue(_subFloor, PercentOf(_pipeline.SpectralStrength));
            SyncValue(_capturedFloor, PercentOf(_pipeline.SpectralFloor));
            SyncEngine(_spectralEngine, _spectralIds, DenoiseFamily.Spectral);
            SyncEngine(_neuralEngine, _neuralIds, DenoiseFamily.Neural);
            UpdateProfileStatus();
        }
        finally
        {
            _polling = false;
        }
    }

    private void SyncEngine(Controls.CycleFieldControl field, string[] ids, DenoiseFamily family)
    {
        if (_noise == null) return;
        int at = Array.IndexOf(ids, _noise.SlotEngine(family));
        if (at < 0 || at == field.SelectedIndex) return;
        field.SuppressEvents = true;
        field.SelectedIndex = at;
        field.SuppressEvents = false;
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
