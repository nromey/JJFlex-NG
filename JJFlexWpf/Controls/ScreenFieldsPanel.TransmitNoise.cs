using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// Home's Transmission expander, its noise fields (Sprint 49 Track H2,
/// design part seven), after the TX EQ button.
/// </summary>
/// <remarks>
/// <para>
/// In Tab order: the Active readout, Side (Spectral, Neural), Engine, On, the
/// engine's normal levers (the noise floor among them where it is the main
/// lever), Advanced (its Advanced levers and Delay), Return to defaults,
/// Favourite; then the gate: On, Margin, the gate's Advanced (the automatic
/// threshold and, while it is off, the threshold; attack, hold, release,
/// depth; "Listen to cleanup" and its volume, moved here from the Audio
/// Workshop; the setup field and Save as setup), and the gate's Return to
/// defaults. One surface and one truth: the Workshop's PC Cleanup section is
/// now a door to these.
/// </para>
/// <para>
/// <b>Every field calls <see cref="NoiseController"/></b>'s transmit half, as
/// the Transmit menu and the noise layer after T do. A lever field lists
/// exactly what the layer's Up and Down step through.
/// </para>
/// </remarks>
public partial class ScreenFieldsPanel
{
    private const string TxNoiseSource = "home, transmission";

    private TextBlock _txNoiseActive = null!;
    private CycleFieldControl _txNoiseSide = null!;
    private CycleFieldControl _txNoiseEngine = null!;
    private CheckBox _txNoiseOn = null!;
    private StackPanel _txNoiseLevers = null!;
    private CheckBox _txNoiseAdvanced = null!;
    private StackPanel _txNoiseAdvancedLevers = null!;
    private TextBlock _txNoiseDelay = null!;
    private Button _txNoiseDefaults = null!;
    private CheckBox _txNoiseFavourite = null!;
    private CheckBox _txGateOn = null!;
    private CycleFieldControl _txGateMargin = null!;
    // Sprint 49 Track I: the expander's strength, its side-chain filter,
    // "Learn the room", the equaliser's flat rows and the de-esser. Flat rows
    // only: the fields panel's navigation is flat by construction (#489).
    private CycleFieldControl _txGateStrength = null!;
    private CheckBox _txSideChain = null!;
    private Button _txLearnRoom = null!;
    private CheckBox _txEqOn = null!;
    private Button _txEqEdit = null!;
    private TextBlock _txEqSummary = null!;
    private CheckBox _txDeEsserOn = null!;
    // Sprint 49 Track CL (#1278): the compressor and the limiter, as flat rows.
    private CheckBox _txCompressorOn = null!, _txLimiterOn = null!, _txHandoff = null!;
    private CycleFieldControl _txCompressorPreset = null!;
    private Button _txDynamicsEdit = null!;
    private TextBlock _txCompressorNow = null!, _txLimiterNow = null!;
    private readonly List<(CycleFieldControl Field, DynamicsLever Lever, IReadOnlyList<(double Value, string Text)> Steps)> _txDynamicsFields = new();
    private readonly List<(CycleFieldControl Field, DeEsserLever Lever, IReadOnlyList<(double Value, string Text)> Steps)> _txDeEsserFields = new();
    private CheckBox _txGateAdvanced = null!;
    private StackPanel _txGateAdvancedFields = null!;
    private CheckBox _txGateAuto = null!;
    private CycleFieldControl _txListen = null!;
    private CycleFieldControl _txSetup = null!;
    private Button _txSaveSetup = null!;
    private Button _txGateDefaults = null!;
    private IReadOnlyList<DenoiseEngineEntry> _txNoiseEngines = Array.Empty<DenoiseEngineEntry>();
    private string? _txNoiseFieldsFor;
    private IReadOnlyList<string> _txSetupNames = Array.Empty<string>();
    private bool _txNoiseRebuilding;
    private readonly List<(CycleFieldControl Field, NoiseLever Lever, IReadOnlyList<(double? Shown, string? Choice, string Text)> Steps)> _txNoiseLeverFields = new();
    private readonly List<(CycleFieldControl Field, TxGateLever Lever, IReadOnlyList<(double Value, string Text)> Steps)> _txGateFields = new();

    /// <summary>The config key the gate's own Advanced box is remembered under (beside the engines' own).</summary>
    private const string TxGateAdvancedKey = "tx:gate";

    /// <summary>The family behind each transmit Side position: transmit has the two computer slots only.</summary>
    private static readonly DenoiseFamily[] TxSideFamilies = { DenoiseFamily.Spectral, DenoiseFamily.Neural };

    private DenoiseFamily TxSideFamily => TxSideFamilies[Math.Clamp(_txNoiseSide.SelectedIndex, 0, TxSideFamilies.Length - 1)];

    private string? TxNoiseEngine =>
        _txNoiseEngines.Count == 0 ? null : _txNoiseEngines[Math.Clamp(_txNoiseEngine.SelectedIndex, 0, _txNoiseEngines.Count - 1)].Id;

    private static Button MakeTxButton(string labelKey, string helpKey)
    {
        var b = new Button
        {
            Content = Lexicon.Get(labelKey),
            Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(b, Lexicon.Get(labelKey));
        JJFlexHelp.SetText(b, Lexicon.Get(helpKey));
        return b;
    }

    /// <summary>Build the fields, in Tab order, after the TX EQ button.</summary>
    private void BuildTransmitNoiseFields()
    {
        _txNoiseActive = new TextBlock { Margin = new Thickness(4, 6, 4, 2), Focusable = true, IsHitTestVisible = true };
        _txNoiseActive.GotFocus += (s, e) =>
        {
            // The same summary the Transmit menu's first row and the layer's Space after T speak.
            string text = Lexicon.Get("audio.noise_home.tx.active", ("summary", NoiseCtl?.TransmitActiveSummary() ?? ""));
            _txNoiseActive.Text = text;
            AutomationProperties.SetName(_txNoiseActive, text);
        };
        _txNoiseActive.Text = Lexicon.Get("audio.noise_home.tx.active", ("summary", ""));
        AutomationProperties.SetName(_txNoiseActive, _txNoiseActive.Text);
        JJFlexHelp.SetText(_txNoiseActive, Lexicon.Get("audio.noise_help.tx_home.active"));
        TxContent.Children.Add(_txNoiseActive);

        _txNoiseSide = MakeCycle(Lexicon.Get("audio.noise_home.tx.side"), new[]
        {
            Lexicon.Get("audio.noise_home.tx.side_spectral"),
            Lexicon.Get("audio.noise_home.tx.side_neural"),
        });
        // Neural first in view: RNNoise there is transmit's default (ruled S8).
        _txNoiseSide.SelectedIndex = 1;
        _txNoiseSide.SelectionChanged += (s, i) =>
        {
            // Changing the side changes nothing; it changes which engines the next field lists.
            if (_txNoiseRebuilding) return;
            ShowTransmitNoiseSide(selectEngine: null);
        };
        JJFlexHelp.SetText(_txNoiseSide, Lexicon.Get("audio.noise_help.tx_home.side"));
        TxContent.Children.Add(_txNoiseSide);

        _txNoiseEngine = MakeCycle(Lexicon.Get("audio.noise_home.tx.engine"), Array.Empty<string>());
        _txNoiseEngine.SelectionChanged += (s, i) =>
        {
            if (_txNoiseRebuilding || NoiseCtl is not { } noise || TxNoiseEngine is not string id) return;
            // Arrowing chooses the slot's engine, applied only once the choice settles (#769).
            noise.TransmitNoteSlotChoicePending(TxSideFamily, id);
            noise.TransmitChooseSlotEngine(TxSideFamily, id, TxNoiseSource);
            ShowTransmitNoiseEngine(id);
        };
        JJFlexHelp.SetText(_txNoiseEngine, Lexicon.Get("audio.noise_help.tx_home.engine"));
        TxContent.Children.Add(_txNoiseEngine);

        _txNoiseOn = MakeToggle(Lexicon.Get("audio.noise_home.tx.on", ("engine", "")));
        _txNoiseOn.Checked += (s, e) => TransmitNoiseOnChanged(true);
        _txNoiseOn.Unchecked += (s, e) => TransmitNoiseOnChanged(false);
        JJFlexHelp.SetText(_txNoiseOn, Lexicon.Get("audio.noise_help.tx_home.on"));
        TxContent.Children.Add(_txNoiseOn);

        // The engine's delay, beside On, always (part seven: "each engine's
        // Delay readout is shown beside its On"; repair round 1, item 7). It was
        // under Advanced until then.
        _txNoiseDelay = new TextBlock { Margin = new Thickness(4, 2, 4, 2), Focusable = true, IsHitTestVisible = true };
        _txNoiseDelay.GotFocus += (s, e) =>
        {
            if (TxNoiseEngine is string id && NoiseCtl is { } noise)
            {
                _txNoiseDelay.Text = noise.DelayText(id, DenoiseDirection.Transmit);
                AutomationProperties.SetName(_txNoiseDelay, _txNoiseDelay.Text);
            }
        };
        JJFlexHelp.SetText(_txNoiseDelay, Lexicon.Get("audio.noise_help.tx_home.delay"));
        TxContent.Children.Add(_txNoiseDelay);

        _txNoiseLevers = new StackPanel();
        TxContent.Children.Add(_txNoiseLevers);

        _txNoiseAdvanced = MakeToggle(Lexicon.Get("audio.noise_home.tx.advanced"));
        _txNoiseAdvanced.Checked += (s, e) => TransmitNoiseAdvancedChanged(true);
        _txNoiseAdvanced.Unchecked += (s, e) => TransmitNoiseAdvancedChanged(false);
        JJFlexHelp.SetText(_txNoiseAdvanced, Lexicon.Get("audio.noise_help.tx_home.advanced"));
        TxContent.Children.Add(_txNoiseAdvanced);

        _txNoiseAdvancedLevers = new StackPanel();
        TxContent.Children.Add(_txNoiseAdvancedLevers);

        _txNoiseDefaults = MakeTxButton("audio.noise_menu.defaults", "audio.noise_help.tx_home.defaults");
        _txNoiseDefaults.Click += (s, e) =>
        {
            if (TxNoiseEngine is string id) NoiseCtl?.TransmitReturnToDefaults(id, TxNoiseSource);
            RefreshTransmitNoiseValues();
        };
        TxContent.Children.Add(_txNoiseDefaults);

        _txNoiseFavourite = MakeToggle(Lexicon.Get("audio.noise_home.favourite", ("engine", "")));
        _txNoiseFavourite.Checked += (s, e) => TransmitFavouriteChanged(true);
        _txNoiseFavourite.Unchecked += (s, e) => TransmitFavouriteChanged(false);
        JJFlexHelp.SetText(_txNoiseFavourite, Lexicon.Get("audio.noise_help.tx_home.favourite"));
        TxContent.Children.Add(_txNoiseFavourite);

        // ── The gate ──
        _txGateOn = MakeToggle(Lexicon.Get("audio.noise_home.tx.gate_on"));
        _txGateOn.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetGateOn(true, TxNoiseSource); };
        _txGateOn.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetGateOn(false, TxNoiseSource); };
        JJFlexHelp.SetText(_txGateOn, Lexicon.Get("audio.noise_help.tx_home.gate_on"));
        TxContent.Children.Add(_txGateOn);

        _txGateMargin = MakeCycle(Lexicon.Get("audio.noise_surface.tx.gate.margin"), Array.Empty<string>());
        WireGateField(_txGateMargin);
        TxContent.Children.Add(_txGateMargin);

        _txGateStrength = MakeCycle(Lexicon.Get("audio.noise_surface.tx.gate.strength"), Array.Empty<string>());
        WireGateField(_txGateStrength);
        TxContent.Children.Add(_txGateStrength);

        _txLearnRoom = MakeTxButton("audio.noise_home.tx.learn_room", "audio.noise_help.tx_home.learn_room");
        _txLearnRoom.Click += (s, e) => NoiseCtl?.LearnTheRoom(TxNoiseSource);
        TxContent.Children.Add(_txLearnRoom);

        _txGateAdvanced = MakeToggle(Lexicon.Get("audio.noise_home.tx.gate_advanced"));
        _txGateAdvanced.Checked += (s, e) => TransmitGateAdvancedChanged(true);
        _txGateAdvanced.Unchecked += (s, e) => TransmitGateAdvancedChanged(false);
        JJFlexHelp.SetText(_txGateAdvanced, Lexicon.Get("audio.noise_help.tx_home.gate_advanced"));
        TxContent.Children.Add(_txGateAdvanced);

        _txGateAdvancedFields = new StackPanel();
        TxContent.Children.Add(_txGateAdvancedFields);

        _txGateAuto = MakeToggle(Lexicon.Get("audio.noise_home.tx.gate_auto"));
        _txGateAuto.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetGateAutoThreshold(true, TxNoiseSource); BuildTransmitGateFields(); } };
        _txGateAuto.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetGateAutoThreshold(false, TxNoiseSource); BuildTransmitGateFields(); } };
        JJFlexHelp.SetText(_txGateAuto, Lexicon.Get("audio.noise_help.tx_home.gate_auto"));

        _txListen = MakeCycle(Lexicon.Get("audio.noise_home.tx.listen"),
            NoiseController.ListenToCleanupKeys.Select(k => Lexicon.Get(k)).ToArray());
        _txListen.SelectionChanged += (s, i) =>
        {
            if (_polling || _txNoiseRebuilding) return;
            NoiseCtl?.SetListenToCleanup(i, TxNoiseSource);
            RefreshTransmitNoiseValues();
        };
        JJFlexHelp.SetText(_txListen, Lexicon.Get("audio.noise_help.tx_home.listen"));

        _txSetup = MakeCycle(Lexicon.Get("audio.noise_home.tx.setup"), new[] { Lexicon.Get("audio.noise_home.tx.setup_none") });
        _txSetup.SelectionChanged += (s, i) =>
        {
            if (_polling || _txNoiseRebuilding || i <= 0 || i > _txSetupNames.Count) return;
            NoiseCtl?.ApplyTransmitSetup(_txSetupNames[i - 1], TxNoiseSource);
            ShowTransmitNoiseSide(selectEngine: null);
        };
        JJFlexHelp.SetText(_txSetup, Lexicon.Get("audio.noise_help.tx_home.setup"));

        _txSaveSetup = MakeTxButton("audio.noise_home.tx.save_setup", "audio.noise_help.tx_home.save_setup");
        _txSaveSetup.Click += (s, e) => FindMainWindow()?.ShowTransmitSetupSave();

        _txSideChain = MakeToggle(Lexicon.Get("audio.noise_home.tx.side_chain"));
        _txSideChain.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetSideChainOn(true, TxNoiseSource); };
        _txSideChain.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetSideChainOn(false, TxNoiseSource); };
        JJFlexHelp.SetText(_txSideChain, Lexicon.Get("audio.noise_help.tx_home.side_chain"));

        // ── The parametric equaliser (Sprint 49 Track I, #1240): on, a door to
        //    the editor, and a one-line summary ──
        _txEqOn = MakeToggle(Lexicon.Get("audio.noise_home.tx.eq_on"));
        _txEqOn.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetEqualizerOn(DenoiseDirection.Transmit, true, TxNoiseSource); RefreshTransmitStageValues(); } };
        _txEqOn.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetEqualizerOn(DenoiseDirection.Transmit, false, TxNoiseSource); RefreshTransmitStageValues(); } };
        JJFlexHelp.SetText(_txEqOn, Lexicon.Get("audio.noise_help.tx_home.eq_on"));
        TxContent.Children.Add(_txEqOn);
        _txEqEdit = MakeTxButton("audio.noise_home.tx.eq_edit", "audio.noise_help.tx_home.eq_edit");
        _txEqEdit.Click += (s, e) => FindMainWindow()?.ShowEqualizerEditor(DenoiseDirection.Transmit);
        TxContent.Children.Add(_txEqEdit);
        _txEqSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Focusable = true };
        JJFlexHelp.SetText(_txEqSummary, Lexicon.Get("audio.noise_help.tx_home.eq_summary"));
        TxContent.Children.Add(_txEqSummary);

        // ── The de-esser (Sprint 49 Track I): on, then its three levers ──
        _txDeEsserOn = MakeToggle(Lexicon.Get("audio.noise_home.tx.deesser_on"));
        _txDeEsserOn.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetDeEsserOn(true, TxNoiseSource); };
        _txDeEsserOn.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetDeEsserOn(false, TxNoiseSource); };
        JJFlexHelp.SetText(_txDeEsserOn, Lexicon.Get("audio.noise_help.tx_home.deesser_on"));
        TxContent.Children.Add(_txDeEsserOn);
        foreach (var lever in NoiseController.DeEsserLevers)
        {
            var l = lever;
            var field = MakeCycle(Lexicon.Get(l.LabelKey), Array.Empty<string>());
            field.SelectionChanged += (s, i) =>
            {
                if (_polling || _txNoiseRebuilding || i < 0) return;
                var entry = _txDeEsserFields.FirstOrDefault(f => ReferenceEquals(f.Field, field));
                if (entry.Field == null || i >= entry.Steps.Count) return;
                NoiseCtl?.SetDeEsser(entry.Lever, entry.Steps[i].Value, TxNoiseSource);
            };
            JJFlexHelp.SetText(field, Lexicon.Get(l.Key switch
            {
                DeEsserLever.Frequency => "audio.noise_help.tx_home.deesser.frequency",
                DeEsserLever.Amount => "audio.noise_help.tx_home.deesser.amount",
                _ => "audio.noise_help.tx_home.deesser.sensitivity",
            }));
            _txDeEsserFields.Add((field, l, Array.Empty<(double, string)>()));
            TxContent.Children.Add(field);
        }

        BuildTransmitDynamicsRows();

        _txGateDefaults = MakeTxButton("audio.noise_home.tx.gate_defaults", "audio.noise_help.tx_home.gate_defaults");
        _txGateDefaults.Click += (s, e) =>
        {
            NoiseCtl?.GateReturnToDefaults(TxNoiseSource);
            BuildTransmitGateFields();
        };
        TxContent.Children.Add(_txGateDefaults);
        StartTransmitNoiseNoRadioPoll();
    }

    /// <summary>Fill the Engine field for the current transmit side and show the engine's controls.</summary>
    private void ShowTransmitNoiseSide(string? selectEngine)
    {
        if (NoiseCtl is not { } noise) return;
        var family = TxSideFamily;
        _txNoiseEngines = noise.TransmitEngines(family);
        string id = selectEngine ?? noise.TransmitSlotEngine(family);
        int index = Math.Max(0, _txNoiseEngines.ToList().FindIndex(e => e.Id == id));
        _txNoiseRebuilding = true;
        try
        {
            _txNoiseEngine.Setup(Lexicon.Get("audio.noise_home.tx.engine"),
                _txNoiseEngines.Select(e => noise.TransmitMarkedName(e.Id)).ToArray(), index);
        }
        finally { _txNoiseRebuilding = false; }
        ShowTransmitNoiseEngine(TxNoiseEngine);
        // The gate's fields do not depend on the engine: never rebuild them out
        // from under a focused gate field (bench round 2, Codex's P3; connected
        // and disconnected alike). Owed instead, and done by the poll once focus
        // has left them.
        if (_txGateAdvancedFields.IsKeyboardFocusWithin) _txGateRebuildOwed = true;
        else BuildTransmitGateFields();
    }

    /// <summary>A gate rebuild put off because focus was inside the gate's Advanced fields.</summary>
    private bool _txGateRebuildOwed;

    /// <summary>Rebuild the engine's own fields: On's name, its levers, Advanced, defaults, favourite.</summary>
    private void ShowTransmitNoiseEngine(string? id)
    {
        if (NoiseCtl is not { } noise || id == null) return;
        _txNoiseFieldsFor = id;
        _txNoiseRebuilding = true;
        try
        {
            string name = noise.NameOf(id);
            string onName = Lexicon.Get("audio.noise_home.tx.on", ("engine", name));
            _txNoiseOn.Content = onName;
            AutomationProperties.SetName(_txNoiseOn, onName);
            _txNoiseOn.IsChecked = noise.TransmitIsOn(id);

            _txNoiseLeverFields.Clear();
            _txNoiseLevers.Children.Clear();
            _txNoiseAdvancedLevers.Children.Clear();
            foreach (var lever in noise.Levers(id, includeAdvanced: false, DenoiseDirection.Transmit))
                _txNoiseLevers.Children.Add(MakeTransmitLeverField(noise, lever));

            bool advanced = noise.IsAdvanced(id, DenoiseDirection.Transmit);
            _txNoiseAdvanced.IsChecked = advanced;
            if (advanced)
                foreach (var lever in noise.Levers(id, includeAdvanced: true, DenoiseDirection.Transmit).Where(l => l.Advanced))
                    _txNoiseAdvancedLevers.Children.Add(MakeTransmitLeverField(noise, lever));
            _txNoiseDelay.Text = noise.DelayText(id, DenoiseDirection.Transmit);
            AutomationProperties.SetName(_txNoiseDelay, _txNoiseDelay.Text);

            string favName = Lexicon.Get("audio.noise_home.favourite", ("engine", name));
            _txNoiseFavourite.Content = favName;
            AutomationProperties.SetName(_txNoiseFavourite, favName);
            _txNoiseFavourite.IsChecked = noise.IsFavourite(id, DenoiseDirection.Transmit);
        }
        finally { _txNoiseRebuilding = false; }
    }

    private CycleFieldControl MakeTransmitLeverField(NoiseController noise, NoiseLever lever)
    {
        var steps = noise.LeverSteps(lever);
        var field = MakeCycle(noise.LeverName(lever), steps.Select(s => s.Text).ToArray());
        field.Wraps = false;
        field.HomeEndJump = true;
        field.SelectedIndex = CurrentStep(noise, lever, steps);
        JJFlexHelp.SetText(field, Lexicon.Get(NoiseController.HelpKey(lever)));
        field.SelectionChanged += (s, i) =>
        {
            if (_polling || _txNoiseRebuilding || i < 0 || i >= steps.Count) return;
            noise.Set(lever, steps[i].Shown, steps[i].Choice, TxNoiseSource);
            if (lever.Descriptor?.Name == "gain_method") ShowTransmitNoiseEngine(lever.EngineId);
        };
        _txNoiseLeverFields.Add((field, lever, steps));
        return field;
    }

    /// <summary>
    /// The gate's fields: Margin always; with the gate's Advanced box, the
    /// automatic threshold box, the threshold while it is off, attack, hold,
    /// release and depth, "Listen to cleanup" and its volume, and the setup
    /// field with Save as setup. Rebuilt when the automatic threshold flips, so
    /// the threshold field is only ever there when it acts.
    /// </summary>
    private void BuildTransmitGateFields()
    {
        if (NoiseCtl is not { } noise) return;
        _txNoiseRebuilding = true;
        try
        {
            _txGateRebuildOwed = false;
            _txGateFields.Clear();
            _txGateOn.IsChecked = noise.TransmitGate.Enabled;
            if (noise.GateLever(TxGateLever.Margin) is { } margin) SetupGateField(_txGateMargin, noise, margin);
            if (noise.GateLever(TxGateLever.Strength) is { } strength) SetupGateField(_txGateStrength, noise, strength);
            SetupTransmitStageFields(noise);

            _txGateAdvancedFields.Children.Clear();
            // Radio or no radio (bench round, item 1): the controller holds the
            // open sections itself while no radio's config is loaded.
            bool advanced = noise.AdvancedShown(TxGateAdvancedKey);
            _txGateAdvanced.IsChecked = advanced;
            _txGateBuiltAuto = noise.TransmitGate.AutoThreshold;
            _txSetupNames = noise.TransmitSetupNames();
            if (!advanced) return;
            _txGateAuto.IsChecked = noise.TransmitGate.AutoThreshold;
            _txGateAdvancedFields.Children.Add(_txGateAuto);
            _txSideChain.IsChecked = noise.TransmitGate.SideChainFilter;
            _txGateAdvancedFields.Children.Add(_txSideChain);
            foreach (var lever in noise.GateLevers(includeAdvanced: true).Where(l => l.Advanced))
            {
                var field = MakeCycle(noise.GateLeverName(lever), Array.Empty<string>());
                WireGateField(field);
                SetupGateField(field, noise, lever);
                _txGateAdvancedFields.Children.Add(field);
            }
            _txListen.SelectedIndex = noise.TransmitGate.MonitorMode;
            _txGateAdvancedFields.Children.Add(_txListen);
            if (noise.GateLever(TxGateLever.MonitorVolume) is { } volume)
            {
                var field = MakeCycle(noise.GateLeverName(volume), Array.Empty<string>());
                WireGateField(field);
                SetupGateField(field, noise, volume);
                _txGateAdvancedFields.Children.Add(field);
            }
            _txSetup.Setup(Lexicon.Get("audio.noise_home.tx.setup"),
                new[] { Lexicon.Get("audio.noise_home.tx.setup_none") }.Concat(_txSetupNames).ToArray(), 0);
            _txGateAdvancedFields.Children.Add(_txSetup);
            _txGateAdvancedFields.Children.Add(_txSaveSetup);
        }
        finally { _txNoiseRebuilding = false; }
    }

    /// <summary>
    /// The compressor's and the limiter's flat rows (Sprint 49 Track CL,
    /// #1278): each switch, the compressor's preset, the levers Home carries
    /// (threshold, ratio and makeup; the limiter's ceiling), each stage's
    /// "now" reading, the setting about the radio's speech processor, and the
    /// door to the editor that holds every parameter. Flat rows only (#489).
    /// </summary>
    private void BuildTransmitDynamicsRows()
    {
        _txCompressorOn = MakeToggle(Lexicon.Get("audio.dynamics_home.compressor_on"));
        _txCompressorOn.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetCompressorOn(true, TxNoiseSource); RefreshTransmitDynamicsValues(); } };
        _txCompressorOn.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetCompressorOn(false, TxNoiseSource); RefreshTransmitDynamicsValues(); } };
        JJFlexHelp.SetText(_txCompressorOn, Lexicon.Get("audio.dynamics_help.compressor_on"));
        TxContent.Children.Add(_txCompressorOn);

        var presetNames = new[] { Lexicon.Get("audio.compressor.preset_none") }
            .Concat(NoiseController.CompressorPresets.Select(NoiseController.CompressorPresetName)).ToArray();
        _txCompressorPreset = MakeCycle(Lexicon.Get("audio.dynamics_home.preset"), presetNames);
        _txCompressorPreset.SelectionChanged += (s, i) =>
        {
            if (_polling || _txNoiseRebuilding || i <= 0 || i > NoiseController.CompressorPresets.Count) return;
            NoiseCtl?.LoadCompressorPreset(NoiseController.CompressorPresets[i - 1].Id, TxNoiseSource);
            RefreshTransmitDynamicsValues();
        };
        JJFlexHelp.SetText(_txCompressorPreset, Lexicon.Get("audio.dynamics_help.preset"));
        TxContent.Children.Add(_txCompressorPreset);

        void AddLeverRows(IEnumerable<DynamicsLever> levers)
        {
            foreach (var lever in levers)
            {
                var l = lever;
                var field = MakeCycle(Lexicon.Get(l.LabelKey), Array.Empty<string>());
                field.SelectionChanged += (s, i) =>
                {
                    if (_polling || _txNoiseRebuilding || i < 0) return;
                    var entry = _txDynamicsFields.FirstOrDefault(f => ReferenceEquals(f.Field, field));
                    if (entry.Field == null || i >= entry.Steps.Count) return;
                    NoiseCtl?.SetDynamics(entry.Lever, entry.Steps[i].Value, TxNoiseSource);
                };
                JJFlexHelp.SetText(field, Lexicon.Get(l.HelpKey));
                _txDynamicsFields.Add((field, l, Array.Empty<(double, string)>()));
                TxContent.Children.Add(field);
            }
        }
        AddLeverRows(NoiseController.CompressorLevers.Where(l => l.OnHome));

        _txCompressorNow = new TextBlock { TextWrapping = TextWrapping.Wrap, Focusable = true };
        JJFlexHelp.SetText(_txCompressorNow, Lexicon.Get("audio.dynamics_help.compressor_now"));
        TxContent.Children.Add(_txCompressorNow);

        _txHandoff = MakeToggle(Lexicon.Get("audio.dynamics_home.handoff"));
        _txHandoff.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetProcessorHandoffSetting(true, TxNoiseSource); };
        _txHandoff.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) NoiseCtl?.SetProcessorHandoffSetting(false, TxNoiseSource); };
        JJFlexHelp.SetText(_txHandoff, Lexicon.Get("audio.dynamics_help.handoff"));
        TxContent.Children.Add(_txHandoff);

        _txLimiterOn = MakeToggle(Lexicon.Get("audio.dynamics_home.limiter_on"));
        _txLimiterOn.Checked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetLimiterOn(true, TxNoiseSource); RefreshTransmitDynamicsValues(); } };
        _txLimiterOn.Unchecked += (s, e) => { if (!_polling && !_txNoiseRebuilding) { NoiseCtl?.SetLimiterOn(false, TxNoiseSource); RefreshTransmitDynamicsValues(); } };
        JJFlexHelp.SetText(_txLimiterOn, Lexicon.Get("audio.dynamics_help.limiter_on"));
        TxContent.Children.Add(_txLimiterOn);
        AddLeverRows(NoiseController.LimiterLevers.Where(l => l.OnHome));

        _txLimiterNow = new TextBlock { TextWrapping = TextWrapping.Wrap, Focusable = true };
        JJFlexHelp.SetText(_txLimiterNow, Lexicon.Get("audio.dynamics_help.limiter_now"));
        TxContent.Children.Add(_txLimiterNow);

        _txDynamicsEdit = MakeTxButton("audio.dynamics_home.edit", "audio.dynamics_help.edit");
        _txDynamicsEdit.Click += (s, e) => FindMainWindow()?.ShowDynamicsEditor();
        TxContent.Children.Add(_txDynamicsEdit);
    }

    /// <summary>The compressor's and the limiter's rows' steps, set from the controller (Sprint 49 Track CL).</summary>
    private void SetupTransmitDynamicsFields(NoiseController noise)
    {
        for (int k = 0; k < _txDynamicsFields.Count; k++)
        {
            var (field, lever, _) = _txDynamicsFields[k];
            var steps = noise.DynamicsSteps(lever);
            field.Setup(noise.DynamicsLeverName(lever), steps.Select(s => s.Text).ToArray(), NearestStep(steps, noise.DynamicsNumber(lever)));
            field.Wraps = false;
            field.HomeEndJump = true;
            _txDynamicsFields[k] = (field, lever, steps);
        }
        RefreshTransmitDynamicsValues();
    }

    /// <summary>The compressor's and the limiter's rows' values, from the controller: the switches, the preset in place, the levers and the two "now" readings.</summary>
    private void RefreshTransmitDynamicsValues()
    {
        if (NoiseCtl is not { } noise || _txCompressorOn == null) return;
        bool wasPolling = _polling;
        _polling = true;
        try
        {
            _txCompressorOn.IsChecked = noise.CompressorOn;
            _txLimiterOn.IsChecked = noise.LimiterOn;
            _txHandoff.IsChecked = noise.ProcessorHandoffSetting;
            var inPlace = noise.CompressorPresetInPlace;
            int presetIndex = 0;
            for (int i = 0; i < NoiseController.CompressorPresets.Count; i++)
                if (ReferenceEquals(NoiseController.CompressorPresets[i], inPlace)) presetIndex = i + 1;
            _txCompressorPreset.SuppressEvents = true;
            try { _txCompressorPreset.SelectedIndex = presetIndex; }
            finally { _txCompressorPreset.SuppressEvents = false; }
            foreach (var (field, lever, steps) in _txDynamicsFields)
            {
                if (steps.Count == 0) continue;
                field.SuppressEvents = true;
                try { field.SelectedIndex = NearestStep(steps, noise.DynamicsNumber(lever)); }
                finally { field.SuppressEvents = false; }
            }
            string compressorNow = noise.CompressorNowSentence();
            if (_txCompressorNow.Text != compressorNow)
            {
                _txCompressorNow.Text = compressorNow;
                AutomationProperties.SetName(_txCompressorNow, compressorNow);
            }
            string limiterNow = noise.LimiterNowSentence();
            if (_txLimiterNow.Text != limiterNow)
            {
                _txLimiterNow.Text = limiterNow;
                AutomationProperties.SetName(_txLimiterNow, limiterNow);
            }
        }
        finally { _polling = wasPolling; }
    }

    /// <summary>The de-esser's fields' steps and the stages' toggles and summary, set from the controller (Sprint 49 Track I).</summary>
    private void SetupTransmitStageFields(NoiseController noise)
    {
        for (int k = 0; k < _txDeEsserFields.Count; k++)
        {
            var (field, lever, _) = _txDeEsserFields[k];
            var steps = noise.DeEsserSteps(lever);
            field.Setup(noise.DeEsserLeverName(lever), steps.Select(s => s.Text).ToArray(), NearestStep(steps, noise.DeEsserNumber(lever)));
            field.Wraps = false;
            field.HomeEndJump = true;
            _txDeEsserFields[k] = (field, lever, steps);
        }
        RefreshTransmitStageValues();
        SetupTransmitDynamicsFields(noise);
    }

    /// <summary>The Track I rows' values, from the controller: equaliser and de-esser on, the de-esser's levers, the side-chain filter, the summary.</summary>
    private void RefreshTransmitStageValues()
    {
        RefreshTransmitDynamicsValues();
        if (NoiseCtl is not { } noise || _txEqOn == null) return;
        bool wasPolling = _polling;
        _polling = true;
        try
        {
            _txEqOn.IsChecked = noise.EqualizerOn(DenoiseDirection.Transmit);
            string summary = noise.EqualizerSummary(DenoiseDirection.Transmit);
            if (_txEqSummary.Text != summary)
            {
                _txEqSummary.Text = summary;
                AutomationProperties.SetName(_txEqSummary, summary);
            }
            _txDeEsserOn.IsChecked = noise.DeEsserOn;
            _txSideChain.IsChecked = noise.TransmitGate.SideChainFilter;
            foreach (var (field, lever, steps) in _txDeEsserFields)
            {
                if (steps.Count == 0) continue;
                field.SuppressEvents = true;
                try { field.SelectedIndex = NearestStep(steps, noise.DeEsserNumber(lever)); }
                finally { field.SuppressEvents = false; }
            }
        }
        finally { _polling = wasPolling; }
    }

    private static int NearestStep(IReadOnlyList<(double Value, string Text)> steps, double now)
    {
        int best = 0;
        for (int i = 1; i < steps.Count; i++)
            if (Math.Abs(steps[i].Value - now) < Math.Abs(steps[best].Value - now)) best = i;
        return best;
    }

    private void SetupGateField(CycleFieldControl field, NoiseController noise, TxGateLever lever)
    {
        var steps = noise.GateSteps(lever);
        field.Setup(noise.GateLeverName(lever), steps.Select(s => s.Text).ToArray(), NearestGateStep(noise, lever, steps));
        field.Wraps = false;
        field.HomeEndJump = true;
        JJFlexHelp.SetText(field, Lexicon.Get(GateHelpKey(lever)));
        _txGateFields.RemoveAll(f => ReferenceEquals(f.Field, field));
        _txGateFields.Add((field, lever, steps));
    }

    /// <summary>A gate field's one handler, attached once when the field is made: it reads the lever and steps the field holds now.</summary>
    private void WireGateField(CycleFieldControl field)
    {
        field.SelectionChanged += (s, i) =>
        {
            if (_polling || _txNoiseRebuilding || i < 0) return;
            var entry = _txGateFields.FirstOrDefault(f => ReferenceEquals(f.Field, field));
            if (entry.Field == null || i >= entry.Steps.Count) return;
            NoiseCtl?.SetGate(entry.Lever, entry.Steps[i].Value, TxNoiseSource);
        };
    }

    /// <summary>A gate lever's Ctrl+F1 line, as a whole key so the coverage scan sees each one.</summary>
    private static string GateHelpKey(TxGateLever lever) => lever.Key switch
    {
        TxGateLever.Margin => "audio.noise_help.tx_home.gate.margin",
        TxGateLever.Threshold => "audio.noise_help.tx_home.gate.threshold",
        TxGateLever.Attack => "audio.noise_help.tx_home.gate.attack",
        TxGateLever.Hold => "audio.noise_help.tx_home.gate.hold",
        TxGateLever.Release => "audio.noise_help.tx_home.gate.release",
        TxGateLever.Depth => "audio.noise_help.tx_home.gate.depth",
        TxGateLever.Strength => "audio.noise_help.tx_home.gate.strength",
        TxGateLever.SideChainLow => "audio.noise_help.tx_home.gate.sidechain_low",
        TxGateLever.SideChainHigh => "audio.noise_help.tx_home.gate.sidechain_high",
        TxGateLever.LookAhead => "audio.noise_help.tx_home.gate.lookahead",
        _ => "audio.noise_help.tx_home.gate.monitor_volume",
    };

    private static int NearestGateStep(NoiseController noise, TxGateLever lever, IReadOnlyList<(double Value, string Text)> steps)
    {
        double now = noise.GateNumber(lever);
        int best = 0;
        for (int i = 1; i < steps.Count; i++)
            if (Math.Abs(steps[i].Value - now) < Math.Abs(steps[best].Value - now)) best = i;
        return best;
    }

    /// <summary>
    /// On acts on the engine the fields SHOW (repair round 1, item 1): the
    /// controller turns that engine on, superseding an unsettled choice, or
    /// turns the slot off. The fields then show the slot as it now stands.
    /// </summary>
    private void TransmitNoiseOnChanged(bool on)
    {
        if (_polling || _txNoiseRebuilding || NoiseCtl is not { } noise || TxNoiseEngine is not string id) return;
        var family = TxSideFamily;
        noise.TransmitSetShownOn(family, id, on, TxNoiseSource);
        ShowTransmitNoiseSide(selectEngine: noise.TransmitSlotEngine(family));
    }

    private void TransmitNoiseAdvancedChanged(bool on)
    {
        if (_polling || _txNoiseRebuilding || NoiseCtl is not { } noise || TxNoiseEngine is not string id) return;
        noise.SetAdvanced(id, on, DenoiseDirection.Transmit);
        ShowTransmitNoiseEngine(id);
    }

    private void TransmitFavouriteChanged(bool favourite)
    {
        if (_polling || _txNoiseRebuilding || NoiseCtl is not { } noise || TxNoiseEngine is not string id) return;
        if (noise.IsFavourite(id, DenoiseDirection.Transmit) != favourite) noise.ToggleFavourite(id, TxNoiseSource, DenoiseDirection.Transmit);
    }

    private void TransmitGateAdvancedChanged(bool on)
    {
        // No radio is not "nothing works" (bench round, item 1): this needed the
        // radio's audio config, so with no radio the box ticked and revealed nothing.
        if (_polling || _txNoiseRebuilding || NoiseCtl is not { } noise) return;
        noise.ShowAdvanced(TxGateAdvancedKey, on);
        BuildTransmitGateFields();
    }

    /// <summary>Refresh every transmit noise field's value from the controller, silently (the poll, and after an action).</summary>
    private void RefreshTransmitNoiseValues()
    {
        if (NoiseCtl is not { } noise || _txNoiseFieldsFor == null) return;
        bool wasPolling = _polling;
        _polling = true;
        try
        {
            _txNoiseOn.IsChecked = noise.TransmitIsOn(_txNoiseFieldsFor);
            _txNoiseFavourite.IsChecked = noise.IsFavourite(_txNoiseFieldsFor, DenoiseDirection.Transmit);
            foreach (var (field, lever, steps) in _txNoiseLeverFields)
            {
                field.SuppressEvents = true;
                try { field.SelectedIndex = CurrentStep(noise, lever, steps); }
                finally { field.SuppressEvents = false; }
            }
            _txGateOn.IsChecked = noise.TransmitGate.Enabled;
            _txGateAuto.IsChecked = noise.TransmitGate.AutoThreshold;
            foreach (var (field, lever, steps) in _txGateFields)
            {
                field.SuppressEvents = true;
                try { field.SelectedIndex = NearestGateStep(noise, lever, steps); }
                finally { field.SuppressEvents = false; }
            }
            _txListen.SuppressEvents = true;
            try { _txListen.SelectedIndex = noise.TransmitGate.MonitorMode; }
            finally { _txListen.SuppressEvents = false; }
            RefreshTransmitStageValues();
            // The Delay readout follows the running engine (bench round, item 4):
            // it was set only when the engine was shown, so it went on saying
            // "measured once it is running" after the engine had run.
            string delay = noise.DelayText(_txNoiseFieldsFor, DenoiseDirection.Transmit);
            if (_txNoiseDelay.Text != delay)
            {
                _txNoiseDelay.Text = delay;
                AutomationProperties.SetName(_txNoiseDelay, delay);
            }
        }
        finally { _polling = wasPolling; }
    }

    /// <summary>
    /// The transmit fields' poll while no radio is connected (bench round, item
    /// 1): the panel's own poll reads the radio and stops without one, so a
    /// choice made on the Transmit menu or in the noise layer left Home saying
    /// another engine was on. The transmit noise settings are this computer's,
    /// so they are followed radio or no radio. Runs only while the Transmission
    /// section is open with no radio; the radio's poll does it otherwise.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _txNoRadioPoll;

    private void StartTransmitNoiseNoRadioPoll()
    {
        _txNoRadioPoll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _txNoRadioPoll.Tick += (s, e) => PollTransmitNoiseWithoutRadio();
        TxExpander.Expanded += (s, e) => _txNoRadioPoll.Start();
        TxExpander.Collapsed += (s, e) => _txNoRadioPoll.Stop();
        if (TxExpander.IsExpanded) _txNoRadioPoll.Start();
    }

    private void PollTransmitNoiseWithoutRadio()
    {
        if (_rig != null || !IsVisible || !TxExpander.IsExpanded || _polling) return;
        _polling = true;
        try { PollTransmitNoise(); }
        finally { _polling = false; }
    }

    /// <summary>The automatic threshold the gate fields were last built for; a change from elsewhere rebuilds them.</summary>
    private bool _txGateBuiltAuto = true;
    private int _txPollCount;

    /// <summary>
    /// The poll's part (repair round 1, item 1): the fields follow the
    /// controller, whatever road changed it. The engine shown follows the slot
    /// (a choice still settling is the slot's to the fields, so an arrow is never
    /// undone); the engine's lever set follows its settings (NR2's trained
    /// values come and go); the gate's conditional fields follow the automatic
    /// threshold and this radio's setups. A rebuild that would take away the
    /// field the operator is standing on waits for them to leave it.
    /// </summary>
    private void PollTransmitNoise()
    {
        if (NoiseCtl is not { } noise) return;
        if (_txNoiseFieldsFor == null) { ShowTransmitNoiseSide(selectEngine: null); return; }
        string want = noise.TransmitSlotEngine(TxSideFamily);
        bool standingInLevers = _txNoiseLevers.IsKeyboardFocusWithin || _txNoiseAdvancedLevers.IsKeyboardFocusWithin;
        bool leversChanged = !_txNoiseLeverFields.Select(f => f.Lever.Key)
            .SequenceEqual(noise.Levers(_txNoiseFieldsFor, includeAdvanced: false, DenoiseDirection.Transmit).Select(l => l.Key)
                .Concat(noise.IsAdvanced(_txNoiseFieldsFor, DenoiseDirection.Transmit)
                    ? noise.Levers(_txNoiseFieldsFor, includeAdvanced: true, DenoiseDirection.Transmit).Where(l => l.Advanced).Select(l => l.Key)
                    : Enumerable.Empty<string>()));
        if (!standingInLevers && want.Length > 0 && want != _txNoiseFieldsFor) { ShowTransmitNoiseSide(selectEngine: want); return; }
        if (!standingInLevers && leversChanged) ShowTransmitNoiseEngine(_txNoiseFieldsFor);
        // The setups are read from disk, so their list is checked every twentieth poll: about two seconds
        // on the radio's 100 ms poll, five on the disconnected 250 ms one.
        bool setupsChanged = ++_txPollCount % 20 == 0 && !noise.TransmitSetupNames().SequenceEqual(_txSetupNames);
        if (!_txGateAdvancedFields.IsKeyboardFocusWithin && (noise.TransmitGate.AutoThreshold != _txGateBuiltAuto || setupsChanged || _txGateRebuildOwed))
            BuildTransmitGateFields();
        RefreshTransmitNoiseValues();
    }

    /// <summary>
    /// The Transmit menu's "settings on Home" and "Advanced settings..." rows,
    /// and the Audio Workshop's door: open the Transmission expander at its
    /// noise fields, at an engine (or at Side), or at the gate, with Advanced
    /// revealed when asked, and focus there once laid out.
    /// </summary>
    public void OpenTransmitNoise(string? engineId, bool advanced, bool gate)
    {
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        TxExpander.IsExpanded = true;
        var noise = NoiseCtl;
        if (noise == null) return;
        UIElement target;
        if (gate)
        {
            if (advanced && !noise.AdvancedShown(TxGateAdvancedKey)) noise.ShowAdvanced(TxGateAdvancedKey, true);
            ShowTransmitNoiseSide(selectEngine: null);
            target = advanced && _txGateAdvancedFields.Children.Count > 0 ? _txGateAdvancedFields.Children[0] : _txGateOn;
        }
        else if (engineId != null && noise.Find(engineId) is { Family: DenoiseFamily family })
        {
            _txNoiseRebuilding = true;
            try { _txNoiseSide.SelectedIndex = Array.IndexOf(TxSideFamilies, family); }
            finally { _txNoiseRebuilding = false; }
            if (advanced && !noise.IsAdvanced(engineId, DenoiseDirection.Transmit)) noise.SetAdvanced(engineId, true, DenoiseDirection.Transmit);
            ShowTransmitNoiseSide(engineId);
            target = advanced && _txNoiseAdvancedLevers.Children.Count > 0 ? _txNoiseAdvancedLevers.Children[0] : _txNoiseEngine;
        }
        else
        {
            ShowTransmitNoiseSide(selectEngine: null);
            target = _txNoiseSide;
        }
        // Focus waits for layout, as the receive expander's does (E3): a field
        // built just now is not focusable until WPF has laid it out.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            UpdateLayout();
            if (!target.Focus())
                JJTrace.Tracing.TraceLine("ScreenFieldsPanel: the transmit noise field did not take focus", System.Diagnostics.TraceLevel.Info);
        });
    }
}
