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
    public void ShowNoisePresetSave(bool fromNoise) =>
        Dispatcher.BeginInvoke(() => Dialogs.NoisePresetSaveDialog.Show(Window.GetWindow(this), Noise, fromNoise));

    /// <summary>Manage presets: apply, rename, delete.</summary>
    public void ShowNoisePresetManager() =>
        Dispatcher.BeginInvoke(() => Dialogs.NoisePresetManagerDialog.Show(Window.GetWindow(this), Noise));
}
