using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// The Transmit menu's noise rows (Sprint 49 Track H2, design part seven):
/// Radio, Transmit and Slice, Transmission both get them from
/// <see cref="BuildTransmitItems"/>, after the TX filter submenu and before
/// Dummy Load Mode, so an operator looking for "what is cleaning my
/// microphone" finds it beside mic gain and the compander.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every row calls <see cref="NoiseController"/></b>'s transmit half, as
/// the noise layer after T and Home's Transmission fields do, so the three
/// roads say the same sentence. Nothing here decides a rule.
/// </para>
/// <para>
/// <b>Held with or without a radio</b> (#1077, settings are intents): the
/// engines and the gate are this computer's, so their rows are never greyed
/// for want of a radio. Only the setups, which are kept per radio, and the
/// cleanup monitor, which taps a radio's transmit chain, say they need one
/// when used.
/// </para>
/// </remarks>
public partial class NativeMenuBar
{
    /// <summary>The source every transmit menu row journals under.</summary>
    private const string TransmitMenuSource = "transmit menu";

    /// <summary>Build the transmit noise rows into a Transmit submenu: the summary, the engines, the gate, the setups and the presets.</summary>
    private void BuildTransmitNoiseItems(IntPtr parent)
    {
        var noise = _window.Noise;
        AddWired(parent, Radios.Lexicon.Get("audio.noise_menu.tx.say_active"),
            () => AfterMenu(n => n.SpeakTransmitActive()));

        // ── Transmit Noise Reduction: the engines, the receive submenu's shape ──
        var nr = AddSubmenu(parent, Radios.Lexicon.Get("audio.noise_menu.tx.noise_reduction"));
        foreach (var e in noise.TransmitEngines())
        {
            string id = e.Id;
            string name = Radios.Lexicon.Get(e.LabelKey);
            var sub = AddSubmenu(nr, noise.TransmitMarkedName(id));
            AddNoiseToggle(sub, name + TransmitNoiseChordText(id),
                () => AfterMenu(n => n.TransmitToggle(id, TransmitMenuSource)), () => _window.Noise.TransmitIsOn(id));

            foreach (var lever in noise.Levers(id, includeAdvanced: false, DenoiseDirection.Transmit))
            {
                var l = lever;
                if (l.Descriptor is { Kind: EngineSettingKind.Choice })
                {
                    var choices = AddSubmenu(sub, noise.LeverName(l));
                    foreach (var option in noise.OptionsFor(l))
                    {
                        var o = option;
                        AddRadioChecked(choices, noise.OptionText(o),
                            () => AfterMenu(n => n.Set(l, null, o.Name, TransmitMenuSource)),
                            () => _window.Noise.ChoiceOf(l) == o.Name);
                    }
                    continue;
                }
                string leverName = noise.LeverName(l);
                AddWired(sub, Radios.Lexicon.Get("audio.noise_menu.up", ("lever", leverName)),
                    () => AfterMenu(n => n.Adjust(l, +1, TransmitMenuSource)));
                AddWired(sub, Radios.Lexicon.Get("audio.noise_menu.down", ("lever", leverName)),
                    () => AfterMenu(n => n.Adjust(l, -1, TransmitMenuSource)));
            }

            // Advanced levers live on Home only (ruled, question 15); the row opens them there.
            if (e.Factory != null && noise.HasAdvancedLevers(id, DenoiseDirection.Transmit))
                AddWired(sub, Radios.Lexicon.Get("audio.noise_menu.advanced"),
                    () => _window.OpenTransmitNoiseOnHome(id, advanced: true));
            AddWired(sub, Radios.Lexicon.Get("audio.noise_menu.defaults"),
                () => AfterMenu(n => n.TransmitReturnToDefaults(id, TransmitMenuSource)));
            AddNoiseToggle(sub, Radios.Lexicon.Get("audio.noise_menu.favourite"),
                () => AfterMenu(n => n.ToggleFavourite(id, TransmitMenuSource, DenoiseDirection.Transmit)),
                () => _window.Noise.IsFavourite(id, DenoiseDirection.Transmit));
            BuildTransmitPresetItems(AddSubmenu(sub, Radios.Lexicon.Get("audio.noise_menu.presets")), id);
        }
        AddWired(nr, Radios.Lexicon.Get("audio.noise_menu.tx.settings_on_home"),
            () => _window.OpenTransmitNoiseOnHome(null, advanced: false));

        // ── Noise Gate: On; Margin up and down; Advanced settings... ──
        var gate = AddSubmenu(parent, Radios.Lexicon.Get("audio.noise_menu.tx.gate"));
        AddNoiseToggle(gate, Radios.Lexicon.Get("audio.noise_menu.tx.gate_on"),
            () => AfterMenu(n => n.ToggleGate(TransmitMenuSource)), () => _window.Noise.TransmitGate.Enabled);
        if (noise.GateLever(TxGateLever.Margin) is { } margin)
        {
            string marginName = noise.GateLeverName(margin);
            AddWired(gate, Radios.Lexicon.Get("audio.noise_menu.up", ("lever", marginName)),
                () => AfterMenu(n => n.AdjustGate(margin, +1, TransmitMenuSource)));
            AddWired(gate, Radios.Lexicon.Get("audio.noise_menu.down", ("lever", marginName)),
                () => AfterMenu(n => n.AdjustGate(margin, -1, TransmitMenuSource)));
        }
        AddWired(gate, Radios.Lexicon.Get("audio.noise_menu.advanced"),
            () => _window.OpenTransmitNoiseOnHome(null, advanced: true, gate: true));

        // ── Transmit noise setups: this radio's, each applying; Save; Manage ──
        var setups = AddSubmenu(parent, Radios.Lexicon.Get("audio.noise_menu.tx.setups"));
        IReadOnlyList<string> names;
        try { names = noise.TransmitSetupNames(); }
        catch (Exception ex) { Tracing.TraceLine("NativeMenuBar: transmit setups unreadable: " + ex.Message, TraceLevel.Warning); names = Array.Empty<string>(); }
        foreach (var setupName in names)
        {
            string n2 = setupName;
            AddWired(setups, n2, () => AfterMenu(n => n.ApplyTransmitSetup(n2, TransmitMenuSource)));
        }
        AddWired(setups, Radios.Lexicon.Get("audio.noise_menu.tx.setup_save"), () => _window.ShowTransmitSetupSave());
        AddWired(setups, Radios.Lexicon.Get("audio.noise_menu.tx.setup_manage"), () => _window.ShowTransmitSetupManager());

        // ── Presets holding a transmit section ──
        BuildTransmitPresetItems(AddSubmenu(parent, Radios.Lexicon.Get("audio.noise_menu.presets")), null);
    }

    /// <summary>A transmit preset list: every preset holding transmit noise reduction (or, for an engine, those whose main transmit engine it is), each applying, then Save and Manage.</summary>
    private void BuildTransmitPresetItems(IntPtr parent, string? engineId)
    {
        var noise = _window.Noise;
        IReadOnlyList<Radios.AudioChainPreset> presets;
        try { presets = engineId == null ? noise.TransmitNoisePresets() : noise.TransmitPresetsFor(engineId); }
        catch (Exception ex) { Tracing.TraceLine("NativeMenuBar: transmit presets unreadable: " + ex.Message, TraceLevel.Warning); presets = Array.Empty<Radios.AudioChainPreset>(); }
        foreach (var preset in presets)
        {
            string presetName = preset.Name;
            AddWired(parent, presetName, () => AfterMenu(n => n.ApplyPreset(presetName, TransmitMenuSource, DenoiseDirection.Transmit)));
        }
        AddWired(parent, Radios.Lexicon.Get("audio.noise_menu.preset_save"), () => _window.ShowNoisePresetSave(fromNoise: false, fromTransmit: true));
        AddWired(parent, Radios.Lexicon.Get("audio.noise_menu.preset_manage"), () => _window.ShowNoisePresetManager());
    }

    /// <summary>
    /// A transmit engine's JJ key chord for the accelerator column: the noise
    /// layer's own inventory row for the engine, behind T ("Ctrl+J, N, T,
    /// Ctrl+R"), the layer's transmit form (part seven: "On rows chorded with
    /// the layer's T form").
    /// </summary>
    internal static string TransmitNoiseChordText(string id)
    {
        foreach (var (chord, engine) in NoiseLayerKeys.EngineKeys)
        {
            if (engine != id) continue;
            var row = KeyInventory.LayerCommands(KeyInventory.NoiseLayerContext)
                .FirstOrDefault(r => Radios.LeaderChordParser.TryParseChord(r.KeyDisplay, out var k) && k == chord);
            var direction = KeyInventory.LayerCommands(KeyInventory.NoiseLayerContext)
                .FirstOrDefault(r => Radios.LeaderChordParser.TryParseChord(r.KeyDisplay, out var k) && k == NoiseLayerKeys.DirectionKey);
            return row == null || direction == null ? "" : "\tCtrl+J, N, " + direction.KeyDisplay + ", " + row.KeyDisplay;
        }
        return "";
    }
}
