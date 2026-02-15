using System.Windows;

namespace JJFlexWpf;

/// <summary>
/// JJFlexRadio main window — WPF replacement for WinForms Form1.
/// Sprint 8: Full WPF conversion, code-behind pattern (not MVVM).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    /// <summary>
    /// Replaces Form1_Load. Phase 8.1+ will add full init sequence:
    /// screen reader greeting, status setup, config load, radio connect, etc.
    /// </summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Phase 8.0: Just prove the window loads and screen reader can announce it.
        // Phase 8.1+: Port Form1_Load init sequence here.
        StatusText.Text = "Ready — no radio connected";
    }

    /// <summary>
    /// Replaces Form1_Closing. Phase 8.1+ will add disconnect, save state, etc.
    /// </summary>
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Phase 8.1+: Disconnect radio, save operator prefs, etc.
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
