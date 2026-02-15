using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    #region Text Area Support — Phase 8.3

    /// <summary>
    /// Window IDs matching Form1.WindowIDs enum for text routing.
    /// </summary>
    public enum WindowIDs
    {
        ReceiveDataOut,
        SendDataOut
    }

    /// <summary>
    /// Map a WindowID to its TextBox control.
    /// Matches Form1.TBIDToTB().
    /// </summary>
    private TextBox WindowIdToTextBox(WindowIDs id)
    {
        return id switch
        {
            WindowIDs.ReceiveDataOut => ReceivedTextBox,
            WindowIDs.SendDataOut => SentTextBox,
            _ => ReceivedTextBox
        };
    }

    /// <summary>
    /// Write text to a text area. Thread-safe via Dispatcher.
    /// Matches Form1.WriteText/WriteTextX pattern.
    /// </summary>
    /// <param name="id">Which text area to write to</param>
    /// <param name="text">Text to write or append</param>
    /// <param name="cursor">Cursor position:
    ///   -1 = preserve current position,
    ///    0 = move to end,
    ///   &gt;0 = set to specific position,
    ///   &lt;-1 = buffer limit (negative of max length, trims from start)</param>
    /// <param name="clearFirst">True to replace all text, false to append</param>
    public void WriteText(WindowIDs id, string text, int cursor = 0, bool clearFirst = false)
    {
        if (_isClosing)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => WriteText(id, text, cursor, clearFirst));
            return;
        }

        try
        {
            var tb = WindowIdToTextBox(id);
            WriteToTextBox(tb, text, cursor, clearFirst);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"WriteText error: {ex.Message}", System.Diagnostics.TraceLevel.Error);
        }
    }

    /// <summary>
    /// Core text writing logic — matches Form1.toTextbox().
    /// Handles append, replace, cursor positioning, and buffer limiting.
    /// </summary>
    private static void WriteToTextBox(TextBox tb, string text, int cursor, bool clearFirst)
    {
        string finalText;
        if (clearFirst)
        {
            finalText = text;
        }
        else
        {
            finalText = tb.Text + text; // Append
        }

        // Handle cursor positioning
        if (cursor == -1)
        {
            // Preserve current position
            cursor = tb.SelectionStart;
        }
        else if (cursor < -1)
        {
            // Buffer limit: negative value = max length
            int maxLen = -cursor;
            if (finalText.Length > maxLen)
            {
                finalText = finalText.Substring(finalText.Length - maxLen);
            }
            cursor = finalText.Length;
        }
        else if (cursor == 0)
        {
            // Move to end
            cursor = finalText.Length;
        }

        tb.Text = finalText;

        // Set cursor and scroll into view
        if (cursor <= tb.Text.Length)
        {
            tb.SelectionStart = cursor;
            tb.SelectionLength = 0;
        }

        // Scroll to cursor (WPF equivalent of ScrollToCaret)
        tb.ScrollToLine(tb.GetLineIndexFromCharacterIndex(
            Math.Min(tb.SelectionStart, Math.Max(0, tb.Text.Length - 1))));
    }

    /// <summary>
    /// Set visibility of the text areas (hidden in Logging Mode).
    /// Matches Form1 show/hide pattern for ReceivedTextBox + SentTextBox.
    /// </summary>
    public void SetTextAreasVisible(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        ReceiveLabel.Visibility = vis;
        ReceivedTextBox.Visibility = vis;
        SendLabel.Visibility = vis;
        SentTextBox.Visibility = vis;

        // Tab stop matches visibility
        ReceivedTextBox.IsTabStop = visible;
        SentTextBox.IsTabStop = visible;
    }

    #endregion

    #region Text Area Event Handlers — Phase 8.3

    /// <summary>
    /// ReceivedTextBox keyboard handler — forwards function keys and modified keys.
    /// Clipboard operations (Ctrl+C, Ctrl+X) handled naturally by WPF TextBox.
    /// </summary>
    private void ReceivedTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys to keyboard routing (Phase 8.6)
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            // Phase 8.6: Commands.DoCommand(e)
            e.Handled = true;
        }
    }

    /// <summary>
    /// SentTextBox keyboard handler — handles CW shortcuts and function keys.
    /// Matches Form1.SentTextBox_KeyDown.
    /// </summary>
    private void SentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys to keyboard routing (Phase 8.6)
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            // Phase 8.6: Commands.DoCommand(e)
            e.Handled = true;
        }

        // Phase 8.4+: Ctrl+Enter sends CW, other shortcuts
    }

    /// <summary>
    /// SentTextBox text input handler — for CW character transmission.
    /// Matches Form1.SentTextBox_KeyPress for direct CW send.
    /// </summary>
    private void SentTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Phase 8.4+: Route typed characters to CW transmit
    }

    #endregion
}
