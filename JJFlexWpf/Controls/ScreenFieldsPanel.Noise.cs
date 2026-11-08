using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// The Home expander's noise reduction fields (Sprint 49 Track E, E9).
/// </summary>
/// <remarks>
/// <para>
/// Ruled shape: Side has three positions, Radio, Computer spectral and
/// Computer neural (2026-11-07, question 3); the arrows choose, Tab moves to
/// the chosen engine's options (2026-10-18 17:21: the per-reducer checkboxes
/// go); a level is never collapsed when its engine is off (S6).
/// </para>
/// <para>
/// <b>Every field calls <see cref="NoiseController"/></b>, as the Audio
/// menu and the noise layer do: nothing here decides a rule, so the three
/// roads cannot disagree. A lever field lists exactly the values the layer's
/// Up and Down step through (<see cref="NoiseController.LeverSteps"/>).
/// </para>
/// <para>
/// The radio's blankers, notches and Audio Peak Filter stay below these
/// fields, where they were (their move is held work), so the expander keeps
/// its name.
/// </para>
/// </remarks>
public partial class ScreenFieldsPanel
{
    private const string NoiseSource = "home";
    private TextBlock _noiseActive = null!;
    private CycleFieldControl _noiseSide = null!;
    private CycleFieldControl _noiseEngine = null!;
    private CheckBox _noiseOn = null!;
    private StackPanel _noiseLevers = null!;
    private CheckBox _noiseAdvanced = null!;
    private StackPanel _noiseAdvancedLevers = null!;
    private TextBlock _noiseDelay = null!;
    private Button _noiseDefaults = null!;
    private CheckBox _noiseFavourite = null!;
    private CycleFieldControl _noisePreset = null!;
    private Button _noiseSavePreset = null!;
    private ValueFieldControl _captureLength = null!;
    private readonly List<(CycleFieldControl Field, NoiseLever Lever, IReadOnlyList<(double? Shown, string? Choice, string Text)> Steps)> _noiseLeverFields = new();
    private IReadOnlyList<DenoiseEngineEntry> _noiseEngines = Array.Empty<DenoiseEngineEntry>();
    private string? _noiseFieldsFor;
    private IReadOnlyList<string> _noisePresetNames = Array.Empty<string>();
    private bool _noiseRebuilding;

    private NoiseController? NoiseCtl => FindMainWindow()?.Noise;

    /// <summary>The family behind each Side position; null is the radio.</summary>
    private static readonly DenoiseFamily?[] SideFamilies = { null, DenoiseFamily.Spectral, DenoiseFamily.Neural };

    private DenoiseFamily? CurrentSideFamily => SideFamilies[Math.Clamp(_noiseSide.SelectedIndex, 0, SideFamilies.Length - 1)];

    private string? CurrentNoiseEngine =>
        _noiseEngines.Count == 0 ? null : _noiseEngines[Math.Clamp(_noiseEngine.SelectedIndex, 0, _noiseEngines.Count - 1)].Id;

    /// <summary>Build the fields, in Tab order, at the top of the expander.</summary>
    private void BuildNoiseFields()
    {
        _noiseActive = new TextBlock { Margin = new Thickness(4, 6, 4, 2), Focusable = true, IsHitTestVisible = true };
        _noiseActive.GotFocus += (s, e) =>
        {
            // The same summary the Space key and the menu item speak. The
            // screen reader reads the name on focus; nothing is spoken here.
            string text = Lexicon.Get("audio.noise_home.active", ("summary", NoiseCtl?.ActiveSummary() ?? ""));
            _noiseActive.Text = text;
            AutomationProperties.SetName(_noiseActive, text);
        };
        _noiseActive.Text = Lexicon.Get("audio.noise_home.active", ("summary", ""));
        AutomationProperties.SetName(_noiseActive, _noiseActive.Text);
        JJFlexHelp.SetText(_noiseActive, Lexicon.Get("audio.noise_help.home.active"));
        DspContent.Children.Add(_noiseActive);

        _noiseSide = MakeCycle(Lexicon.Get("audio.noise_home.side"), new[]
        {
            Lexicon.Get("audio.noise_home.side_radio"),
            Lexicon.Get("audio.noise_home.side_spectral"),
            Lexicon.Get("audio.noise_home.side_neural"),
        });
        _noiseSide.SelectionChanged += (s, i) =>
        {
            // Changing the side changes nothing on the radio or the computer;
            // it changes which engines the next field lists.
            if (_noiseRebuilding) return;
            ShowNoiseSide(selectEngine: null);
        };
        JJFlexHelp.SetText(_noiseSide, Lexicon.Get("audio.noise_help.home.side"));
        DspContent.Children.Add(_noiseSide);

        _noiseEngine = MakeCycle(Lexicon.Get("audio.noise_home.engine"), Array.Empty<string>());
        _noiseEngine.SelectionChanged += (s, i) =>
        {
            if (_noiseRebuilding || NoiseCtl is not { } noise || CurrentNoiseEngine is not string id) return;
            if (CurrentSideFamily is DenoiseFamily family)
            {
                // A computer side: arrowing chooses the slot's engine, applied
                // only once the choice settles (#769), so arrowing past an
                // engine never starts it.
                noise.NoteSlotChoicePending(family, id);
                noise.ChooseSlotEngine(family, id, NoiseSource);
            }
            ShowNoiseEngine(id);
        };
        JJFlexHelp.SetText(_noiseEngine, Lexicon.Get("audio.noise_help.home.engine"));
        DspContent.Children.Add(_noiseEngine);

        _noiseOn = MakeToggle(Lexicon.Get("audio.noise_home.on", ("engine", "")));
        _noiseOn.Checked += (s, e) => NoiseOnChanged(true);
        _noiseOn.Unchecked += (s, e) => NoiseOnChanged(false);
        JJFlexHelp.SetText(_noiseOn, Lexicon.Get("audio.noise_help.home.on"));
        DspContent.Children.Add(_noiseOn);

        _noiseLevers = new StackPanel();
        DspContent.Children.Add(_noiseLevers);

        _noiseAdvanced = MakeToggle(Lexicon.Get("audio.noise_home.advanced"));
        _noiseAdvanced.Checked += (s, e) => NoiseAdvancedChanged(true);
        _noiseAdvanced.Unchecked += (s, e) => NoiseAdvancedChanged(false);
        JJFlexHelp.SetText(_noiseAdvanced, Lexicon.Get("audio.noise_help.home.advanced"));
        DspContent.Children.Add(_noiseAdvanced);

        _noiseAdvancedLevers = new StackPanel();
        DspContent.Children.Add(_noiseAdvancedLevers);

        _noiseDelay = new TextBlock { Margin = new Thickness(4, 2, 4, 2), Focusable = true, IsHitTestVisible = true };
        _noiseDelay.GotFocus += (s, e) =>
        {
            if (CurrentNoiseEngine is string id && NoiseCtl is { } noise)
            {
                _noiseDelay.Text = noise.DelayText(id);
                AutomationProperties.SetName(_noiseDelay, _noiseDelay.Text);
            }
        };
        JJFlexHelp.SetText(_noiseDelay, Lexicon.Get("audio.noise_help.home.delay"));
        DspContent.Children.Add(_noiseDelay);

        _noiseDefaults = new Button
        {
            Content = Lexicon.Get("audio.noise_menu.defaults"),
            Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(_noiseDefaults, Lexicon.Get("audio.noise_menu.defaults"));
        _noiseDefaults.Click += (s, e) =>
        {
            if (CurrentNoiseEngine is string id) NoiseCtl?.ReturnToDefaults(id, NoiseSource);
            RefreshNoiseValues();
        };
        JJFlexHelp.SetText(_noiseDefaults, Lexicon.Get("audio.noise_help.home.defaults"));
        DspContent.Children.Add(_noiseDefaults);

        _noiseFavourite = MakeToggle(Lexicon.Get("audio.noise_home.favourite", ("engine", "")));
        _noiseFavourite.Checked += (s, e) => { if (!_polling && !_noiseRebuilding && CurrentNoiseEngine is string id) NoiseCtl?.SetFavourite(id, true, NoiseSource); };
        _noiseFavourite.Unchecked += (s, e) => { if (!_polling && !_noiseRebuilding && CurrentNoiseEngine is string id) NoiseCtl?.SetFavourite(id, false, NoiseSource); };
        JJFlexHelp.SetText(_noiseFavourite, Lexicon.Get("audio.noise_help.home.favourite"));
        DspContent.Children.Add(_noiseFavourite);

        _noisePreset = MakeCycle(Lexicon.Get("audio.noise_home.preset"), new[] { Lexicon.Get("audio.noise_home.preset_none") });
        _noisePreset.SelectionChanged += (s, i) =>
        {
            if (_noiseRebuilding || _polling || i <= 0 || i > _noisePresetNames.Count) return;
            NoiseCtl?.ApplyPreset(_noisePresetNames[i - 1], NoiseSource);
            RefreshNoiseValues();
        };
        JJFlexHelp.SetText(_noisePreset, Lexicon.Get("audio.noise_help.home.preset"));
        DspContent.Children.Add(_noisePreset);

        _noiseSavePreset = new Button
        {
            Content = Lexicon.Get("audio.noise_home.save_preset"),
            Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(_noiseSavePreset, Lexicon.Get("audio.noise_home.save_preset"));
        _noiseSavePreset.Click += (s, e) => FindMainWindow()?.ShowNoisePresetSave(fromNoise: true);
        JJFlexHelp.SetText(_noiseSavePreset, Lexicon.Get("audio.noise_help.home.save_preset"));
        DspContent.Children.Add(_noiseSavePreset);

        // Captured noise's capture length (the capture button, the profile
        // readout and the Noise Profiles button follow, as they always have).
        _captureLength = MakeValue(Lexicon.Get("audio.noise_home.capture_length"), 1, 5, 1);
        _captureLength.Unit = Lexicon.Get("audio.unit.seconds");
        _captureLength.ValueChanged += (s, v) =>
        {
            if (_polling || FindMainWindow() is not { } mw || mw.CurrentAudioConfig is not { } cfg) return;
            cfg.SpectralSubSampleDuration = v;
            mw.PersistDspSettings();
        };
        JJFlexHelp.SetText(_captureLength, Lexicon.Get("audio.noise_help.home.capture_length"));
        DspContent.Children.Add(_captureLength);
    }

    /// <summary>Fill the Engine field for the current side and show an engine's controls.</summary>
    private void ShowNoiseSide(string? selectEngine)
    {
        if (NoiseCtl is not { } noise) return;
        var family = CurrentSideFamily;
        _noiseEngines = noise.Engines(DenoiseGroup.NoiseReduction)
            .Where(e => family == null ? e.Side == DenoiseSide.Radio : e.Side == DenoiseSide.Pc && e.Family == family).ToList();
        string? id = selectEngine;
        if (id == null && family is DenoiseFamily f) id = noise.SlotEngine(f);
        int index = Math.Max(0, _noiseEngines.ToList().FindIndex(e => e.Id == id));
        _noiseRebuilding = true;
        try
        {
            _noiseEngine.Setup(Lexicon.Get("audio.noise_home.engine"),
                _noiseEngines.Select(e => EngineOption(noise, e)).ToArray(), index);
        }
        finally { _noiseRebuilding = false; }
        ShowNoiseEngine(CurrentNoiseEngine);
    }

    private static string EngineOption(NoiseController noise, DenoiseEngineEntry e) => noise.MarkedName(e.Id);

    /// <summary>Rebuild the engine's own fields: On's name, its levers, Advanced, defaults, favourite, presets.</summary>
    private void ShowNoiseEngine(string? id)
    {
        if (NoiseCtl is not { } noise || id == null) return;
        _noiseFieldsFor = id;
        _noiseRebuilding = true;
        try
        {
            var e = noise.Find(id)!;
            string name = Lexicon.Get(e.LabelKey);
            string onName = Lexicon.Get("audio.noise_home.on", ("engine", name));
            _noiseOn.Content = onName;
            AutomationProperties.SetName(_noiseOn, onName);
            _noiseOn.IsChecked = noise.IsOn(id);

            _noiseLeverFields.Clear();
            _noiseLevers.Children.Clear();
            _noiseAdvancedLevers.Children.Clear();
            foreach (var lever in noise.Levers(id, includeAdvanced: false))
                _noiseLevers.Children.Add(MakeLeverField(noise, lever));

            bool hasAdvanced = e.Side == DenoiseSide.Pc;
            _noiseAdvanced.Visibility = hasAdvanced ? Visibility.Visible : Visibility.Collapsed;
            bool advanced = hasAdvanced && noise.IsAdvanced(id);
            _noiseAdvanced.IsChecked = advanced;
            if (advanced)
                foreach (var lever in noise.Levers(id, includeAdvanced: true).Where(l => l.Advanced))
                    _noiseAdvancedLevers.Children.Add(MakeLeverField(noise, lever));
            _noiseDelay.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            _noiseDelay.Text = advanced ? noise.DelayText(id) : "";
            AutomationProperties.SetName(_noiseDelay, _noiseDelay.Text);

            // Return to defaults is not offered on a switch-only radio engine (D4).
            _noiseDefaults.Visibility = noise.OffersDefaults(id) ? Visibility.Visible : Visibility.Collapsed;

            string favName = Lexicon.Get("audio.noise_home.favourite", ("engine", name));
            _noiseFavourite.Content = favName;
            AutomationProperties.SetName(_noiseFavourite, favName);
            _noiseFavourite.IsChecked = noise.IsFavourite(id);

            IReadOnlyList<Radios.AudioChainPreset> presets;
            try { presets = noise.PresetsFor(id); } catch { presets = Array.Empty<Radios.AudioChainPreset>(); }
            _noisePresetNames = presets.Select(p => p.Name).ToList();
            _noisePreset.Setup(Lexicon.Get("audio.noise_home.preset"),
                new[] { Lexicon.Get("audio.noise_home.preset_none") }.Concat(_noisePresetNames).ToArray(), 0);

            _captureLength.Visibility = id == "spectral_capture" ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _noiseRebuilding = false; }
    }

    private CycleFieldControl MakeLeverField(NoiseController noise, NoiseLever lever)
    {
        var steps = noise.LeverSteps(lever);
        var field = MakeCycle(noise.LeverName(lever), steps.Select(s => s.Text).ToArray());
        // A lever is a range with two ends, not a ring: Up is more, an end
        // stays put, Home and End go to the ends.
        field.Wraps = false;
        field.HomeEndJump = true;
        field.SelectedIndex = CurrentStep(noise, lever, steps);
        JJFlexHelp.SetText(field, Lexicon.Get(NoiseController.HelpKey(lever)));
        field.SelectionChanged += (s, i) =>
        {
            if (_polling || _noiseRebuilding || i < 0 || i >= steps.Count) return;
            noise.Set(lever, steps[i].Shown, steps[i].Choice, NoiseSource);
            if (lever.Descriptor?.Name == "gain_method") ShowNoiseEngine(lever.EngineId); // the trained values come and go
        };
        _noiseLeverFields.Add((field, lever, steps));
        return field;
    }

    private static int CurrentStep(NoiseController noise, NoiseLever lever, IReadOnlyList<(double? Shown, string? Choice, string Text)> steps)
    {
        if (lever.Descriptor is { Kind: EngineSettingKind.Choice })
            return Math.Max(0, steps.ToList().FindIndex(s => s.Choice == noise.ChoiceOf(lever)));
        double? now = noise.NumberOf(lever);
        if (now == null || steps.Count == 0) return 0;
        int best = 0;
        for (int i = 1; i < steps.Count; i++)
            if (Math.Abs(steps[i].Shown!.Value - now.Value) < Math.Abs(steps[best].Shown!.Value - now.Value)) best = i;
        return best;
    }

    private void NoiseOnChanged(bool on)
    {
        if (_polling || _noiseRebuilding || NoiseCtl is not { } noise || CurrentNoiseEngine is not string id) return;
        if (CurrentSideFamily is DenoiseFamily family && FindMainWindow()?.FieldsPanel.AudioPipeline is { } p)
        {
            var slot = family == DenoiseFamily.Spectral ? p.SpectralSlot : p.NeuralSlot;
            // The slot's own switch. With no explicit choice made here, the
            // first time in a mode group decides the engine (its starting point).
            if (!on || slot.ChosenEngineId == id) noise.SetSlotOn(family, on, NoiseSource);
            else noise.SetOn(id, on, NoiseSource);
            ShowNoiseSide(selectEngine: noise.SlotEngine(family));
            return;
        }
        noise.SetOn(id, on, NoiseSource);
    }

    private void NoiseAdvancedChanged(bool on)
    {
        if (_polling || _noiseRebuilding || NoiseCtl is not { } noise || CurrentNoiseEngine is not string id) return;
        noise.SetAdvanced(id, on);
        ShowNoiseEngine(id);
    }

    /// <summary>Refresh every noise field's value from the controller, silently (the poll, and after an action).</summary>
    private void RefreshNoiseValues()
    {
        if (NoiseCtl is not { } noise || _noiseFieldsFor == null) return;
        bool wasPolling = _polling;
        _polling = true;
        try
        {
            _noiseOn.IsChecked = noise.IsOn(_noiseFieldsFor);
            _noiseFavourite.IsChecked = noise.IsFavourite(_noiseFieldsFor);
            foreach (var (field, lever, steps) in _noiseLeverFields)
            {
                field.SuppressEvents = true;
                try { field.SelectedIndex = CurrentStep(noise, lever, steps); }
                finally { field.SuppressEvents = false; }
            }
            if (FindMainWindow()?.CurrentAudioConfig is { } cfg) _captureLength.Value = Math.Clamp(cfg.SpectralSubSampleDuration, 1, 5);
        }
        finally { _polling = wasPolling; }
    }

    /// <summary>The poll's part: values follow the controller; the engine list follows availability.</summary>
    private void PollNoise()
    {
        if (NoiseCtl == null) return;
        if (_noiseFieldsFor == null) { ShowNoiseSide(selectEngine: null); return; }
        RefreshNoiseValues();
    }

    /// <summary>
    /// The menu's "settings on Home" rows and "Advanced settings...": open
    /// the expander at this engine (or at Side for none), with Advanced
    /// revealed when asked, and put focus there.
    /// </summary>
    public void OpenNoise(string? engineId, bool advanced)
    {
        DspExpander.IsExpanded = true;
        var noise = NoiseCtl;
        if (noise == null) return;
        if (engineId != null && noise.Find(engineId) is { } e && e.Group == DenoiseGroup.NoiseReduction)
        {
            _noiseRebuilding = true;
            try { _noiseSide.SelectedIndex = e.Side == DenoiseSide.Radio ? 0 : e.Family == DenoiseFamily.Spectral ? 1 : 2; }
            finally { _noiseRebuilding = false; }
            if (advanced && !noise.IsAdvanced(engineId)) noise.SetAdvanced(engineId, true);
            ShowNoiseSide(engineId);
            Dispatcher.BeginInvoke(() => (advanced && _noiseAdvancedLevers.Children.Count > 0
                ? (UIElement)_noiseAdvancedLevers.Children[0] : _noiseEngine).Focus());
            return;
        }
        if (engineId != null)
        {
            // A blanker or notch: those fields stay below, as they were.
            Dispatcher.BeginInvoke(() => _nbCheck.Focus());
            return;
        }
        ShowNoiseSide(selectEngine: null);
        Dispatcher.BeginInvoke(() => _noiseSide.Focus());
    }

    private readonly List<(CycleFieldControl Field, NoiseLever Lever, IReadOnlyList<(double? Shown, string? Choice, string Text)> Steps)> _radioExtraFields = new();
    private CheckBox? _standardNotchCheck;

    /// <summary>
    /// The rows beside the radio's blankers and notches that this build newly
    /// exposes: the Standard Auto-Notch switch and level, and the Legacy
    /// Auto-Notch and Audio Peak Filter levels. Through the controller, so
    /// they gate, step and speak as the layer's keys do.
    /// </summary>
    private void BuildRadioNoiseExtras()
    {
        if (NoiseCtl is not { } noise) return;
        _radioNoiseExtras.Children.Clear();
        _apfLevelHolder.Children.Clear();
        _radioExtraFields.Clear();
        string anfName = Lexicon.Get("audio.denoise.radio_anf");
        _standardNotchCheck = MakeToggle(anfName);
        _standardNotchCheck.IsChecked = noise.IsOn("radio_anf");
        _standardNotchCheck.Checked += (s, e) => { if (!_polling) NoiseCtl?.SetOn("radio_anf", true, NoiseSource); };
        _standardNotchCheck.Unchecked += (s, e) => { if (!_polling) NoiseCtl?.SetOn("radio_anf", false, NoiseSource); };
        _radioNoiseExtras.Children.Add(_standardNotchCheck);
        foreach (var (id, holder) in new[] { ("radio_anf", _radioNoiseExtras), ("radio_anfl", _radioNoiseExtras), ("radio_apf", _apfLevelHolder) })
        {
            var lever = noise.Levers(id, false).FirstOrDefault();
            if (lever == null) continue;
            var steps = noise.LeverSteps(lever);
            string label = Lexicon.Get("audio.noise_home.engine_level", ("engine", noise.NameOf(id)));
            var field = MakeCycle(label, steps.Select(x => x.Text).ToArray());
            field.Wraps = false;
            field.HomeEndJump = true;
            field.SelectedIndex = CurrentStep(noise, lever, steps);
            field.SelectionChanged += (s, i) =>
            {
                if (_polling || i < 0 || i >= steps.Count) return;
                NoiseCtl?.Set(lever, steps[i].Shown, steps[i].Choice, NoiseSource);
            };
            _radioExtraFields.Add((field, lever, steps));
            holder.Children.Add(field);
        }
    }

    /// <summary>
    /// Home's Audio Peak Filter checkbox (repair round 1, B8): the controller
    /// decides and speaks, refusing outside CW, and the box then shows what
    /// the radio holds, so it never sits ticked over a refused write.
    /// </summary>
    private void SetApfThroughController(bool on)
    {
        if (_polling || NoiseCtl is not { } noise) return;
        noise.SetOn("radio_apf", on, NoiseSource);
        bool wasPolling = _polling;
        _polling = true;
        try { _apfCheck.IsChecked = noise.IsOn("radio_apf"); }
        finally { _polling = wasPolling; }
    }

    private void PollRadioNoiseExtras()
    {
        if (NoiseCtl is not { } noise) return;
        if (_standardNotchCheck != null) _standardNotchCheck.IsChecked = noise.IsOn("radio_anf");
        foreach (var (field, lever, steps) in _radioExtraFields)
        {
            field.SuppressEvents = true;
            try { field.SelectedIndex = CurrentStep(noise, lever, steps); }
            finally { field.SuppressEvents = false; }
        }
    }

    /// <summary>Listen to what was removed is never left on: leaving the expander clears it.</summary>
    private void OnDspExpanderCollapsed() => NoiseCtl?.ClearListening();
}
