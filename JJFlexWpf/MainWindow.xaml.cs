using System.ComponentModel;
using System.Windows;
using JJTrace;

namespace JJFlexWpf;

/// <summary>
/// JJFlexRadio main window — WPF replacement for WinForms Form1.
/// Sprint 8: Full WPF conversion, code-behind pattern (not MVVM).
///
/// Layout:
///   DockPanel
///     ├─ Menu (Top)           — 3 mode-specific menu sets (Classic/Modern/Logging)
///     ├─ StatusBar (Bottom)   — Radio status fields (Power, Memories, Scan, Knob, LogFile)
///     └─ Grid (Fill)
///          Row 0: RadioControlsPanel  — Frequency, mode, tuner, TX/antenna buttons
///          Row 1: ContentArea         — Received/Sent text, rig fields display
///          Row 2: LoggingPanel        — Logging Mode overlay (collapsed by default)
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Flag to prevent re-entrant close attempts (mirrors Form1.Ending).
    /// </summary>
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    /// <summary>
    /// Main initialization sequence — replaces Form1_Load.
    /// Called once when the window first renders.
    ///
    /// Init order (matches Form1_Load):
    ///   1. Screen reader greeting
    ///   2. Status bar setup              — Phase 8.2
    ///   3. Config load (GetConfigInfo)   — Phase 8.1 (wired here)
    ///   4. UI Mode upgrade prompt        — Phase 8.5
    ///   5. Station name / window title   — Phase 8.1 (wired here)
    ///   6. Operator change handler       — Phase 8.1
    ///   7. Radio open                    — Phase 8.2+
    ///   8. Menu construction             — Phase 8.5
    ///   9. Logging panel build           — Phase 8.8
    ///  10. Apply UI mode                 — Phase 8.5
    /// </summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Tracing.TraceLine("MainWindow_Loaded: starting init", System.Diagnostics.TraceLevel.Info);

        // 1. Screen reader greeting (matches Form1_Load line 206)
        Radios.ScreenReaderOutput.Speak("Welcome to JJ Flex");

        // 2-10. Remaining init steps are wired in as phases are completed.
        // Each phase adds its initialization here.

        // Update status
        StatusText.Text = "Ready — no radio connected";

        Tracing.TraceLine("MainWindow_Loaded: init complete", System.Diagnostics.TraceLevel.Info);
    }

    /// <summary>
    /// Clean shutdown — replaces Form1_FormClosing + FileExitToolStripMenuItem_Click.
    /// Prompts to save unsaved work, disconnects radio, cleans up resources.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
            return;

        _isClosing = true;
        Tracing.TraceLine("MainWindow_Closing: starting shutdown", System.Diagnostics.TraceLevel.Info);

        try
        {
            // Phase 8.8: Check logging panel for unsaved QSO
            // Phase 8.2+: Disconnect radio, close cluster, etc.
            // For now, just clean exit.

            Tracing.TraceLine("MainWindow_Closing: shutdown complete", System.Diagnostics.TraceLevel.Info);
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine($"MainWindow_Closing error: {ex.Message}", System.Diagnostics.TraceLevel.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #region Radio Control Button Handlers — Phase 8.2

    /// <summary>
    /// Antenna Tune button — toggles FlexTunerOn.
    /// Wired to RigControl in Phase 8.4+.
    /// </summary>
    private void AntennaTuneButton_Click(object sender, RoutedEventArgs e)
    {
        Tracing.TraceLine("AntennaTuneButton_Click", System.Diagnostics.TraceLevel.Info);
        // Phase 8.4+: RigControl.FlexTunerOn = !RigControl.FlexTunerOn
    }

    /// <summary>
    /// Show SWR/tuner status on hover (matches WinForms Enter/Leave pattern).
    /// </summary>
    private void AntennaTuneButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Phase 8.4+: Update button text with SWR or tuner status
    }

    private void AntennaTuneButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Phase 8.4+: Restore button text to "Tune"
        AntennaTuneButton.Content = "Tune";
    }

    /// <summary>
    /// Transmit button — toggles PTT.
    /// Wired to RigControl in Phase 8.4+.
    /// </summary>
    private void TransmitButton_Click(object sender, RoutedEventArgs e)
    {
        Tracing.TraceLine("TransmitButton_Click", System.Diagnostics.TraceLevel.Info);
        // Phase 8.4+: toggleTransmit()
    }

    #endregion
}
