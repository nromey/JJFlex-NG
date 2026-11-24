using System.Windows;

namespace JJFlexWpf;

/// <summary>The noise surface's doors on the main window (Sprint 49 Track E).</summary>
public partial class MainWindow
{
    /// <summary>
    /// "Noise reduction settings on Home", "Advanced settings..." and the
    /// blanker rows' "settings on Home": open the Noise Reduction and DSP
    /// expander at an engine (or at Side for none), focus inside it.
    /// </summary>
    public void OpenNoiseOnHome(string? engineId, bool advanced)
    {
        Dispatcher.BeginInvoke(() =>
        {
            FocusHome();
            FieldsPanel.OpenNoise(engineId, advanced);
        });
    }

    /// <summary>Save the current settings as a preset (the menu, Home's Save as preset).</summary>
    public void ShowNoisePresetSave(bool fromNoise) => ShowNoisePresetSave(fromNoise, fromTransmit: false);

    /// <summary>Save the current settings as a preset, from a transmit road: only the transmit noise box starts ticked (Sprint 49 Track H2).</summary>
    public void ShowNoisePresetSave(bool fromNoise, bool fromTransmit) =>
        Dispatcher.BeginInvoke(() => Dialogs.NoisePresetSaveDialog.Show(Window.GetWindow(this), Noise, fromNoise, fromTransmit));

    /// <summary>Manage presets: apply, rename, delete.</summary>
    public void ShowNoisePresetManager() =>
        Dispatcher.BeginInvoke(() => Dialogs.NoisePresetManagerDialog.Show(Window.GetWindow(this), Noise));

    /// <summary>
    /// The Transmit menu's "settings on Home" and "Advanced settings..." rows
    /// (Sprint 49 Track H2): open the Transmission expander at its noise fields,
    /// at an engine (or at Side for none), or at the gate, with Advanced
    /// revealed when asked, and put focus there.
    /// </summary>
    public void OpenTransmitNoiseOnHome(string? engineId, bool advanced, bool gate = false)
    {
        Dispatcher.BeginInvoke(() =>
        {
            FocusHome();
            FieldsPanel.OpenTransmitNoise(engineId, advanced, gate);
        });
    }

    /// <summary>"Save current as setup...": name the live transmit chain and gate as one of this radio's setups (Sprint 49 Track H2).</summary>
    public void ShowTransmitSetupSave() =>
        Dispatcher.BeginInvoke(() => Dialogs.TransmitSetupSaveDialog.Show(Window.GetWindow(this), Noise));

    /// <summary>"Manage setups...": apply, rename, delete, and copy to another radio (Sprint 49 Track H2).</summary>
    public void ShowTransmitSetupManager() =>
        Dispatcher.BeginInvoke(() => Dialogs.TransmitSetupManagerDialog.Show(Window.GetWindow(this), Noise));

    /// <summary>
    /// "Edit equaliser...": the parametric equaliser's editor on one side
    /// (Sprint 49 Track I, #1240; one editor for both sides, ruled S2). The
    /// transmit menu, the DSP menu's receive side, Home and the noise layer
    /// all open it here.
    /// </summary>
    public void ShowEqualizerEditor(Radios.DenoiseDirection side)
    {
        Dispatcher.BeginInvoke(() =>
        {
            const string source = "equalizer editor";
            var dialog = new ParametricEqEditorDialog(Noise.EqualizerEditor(side, source),
                on => Noise.SetEqualizerOn(side, on, source), id => Noise.LoadEqualizerPreset(side, id, source))
            { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        });
    }
}
