using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #801: the keyboard dispatcher must see the mode displayed by MainWindow.
/// An uninitialized MainWindow supplies only its mode property. No constructor,
/// XAML, Dispatcher, window, radio, or settings path is used.
/// </summary>
public sealed class LoggingModeKeyboardTests
{
    [Fact]
    public void Logging_scope_uses_the_live_window_mode_and_reaches_the_handler()
    {
        Assert.True(QuietRun.Silenced, QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, TestSettingsRoot.Failure);

        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetProperty(nameof(MainWindow.ActiveUIMode))!
            .SetValue(window, MainWindow.UIMode.Modern);
        int savedMode = (int)MainWindow.UIMode.Modern;
        int fullFormOpens = 0;

        // The old VB wiring kept returning the persisted mode even after the
        // window entered its session-only Logging overlay. Reproduce both the
        // lookup failure and the skipped handler through the real dispatcher.
        var stale = new KeyCommands(new KeyCommandContext
        {
            GetActiveUIMode = () => savedMode,
            LogOpenFullForm = () => fullFormOpens++,
        }, setDefault: true);
        typeof(MainWindow).GetProperty(nameof(MainWindow.ActiveUIMode))!
            .SetValue(window, MainWindow.UIMode.Logging);
        Assert.Null(stale.Lookup(Keys.L | Keys.Control | Keys.Alt));
        Assert.False(stale.DoCommand(Keys.L | Keys.Control | Keys.Alt));
        Assert.Equal(0, fullFormOpens);

        var live = new KeyCommands(new KeyCommandContext
        {
            GetActiveUIMode = () => (int)window.ActiveUIMode,
            LogOpenFullForm = () => fullFormOpens++,
        }, setDefault: true);
        Assert.Equal(CommandValues.LogOpenFullForm,
            live.Lookup(Keys.L | Keys.Control | Keys.Alt)?.KeyDef.Id);
        Assert.True(live.DoCommand(Keys.L | Keys.Control | Keys.Alt));
        Assert.Equal(1, fullFormOpens);

        // The WPF test project cannot reference the VB executable. Pin its
        // production delegate to the same live mode used in the route above.
        string globals = File.ReadAllText(FindRepoFile("globals.vb"));
        Assert.Contains(".GetActiveUIMode = Function() CInt(WpfMainWindow.ActiveUIMode)", globals);
    }

    [Fact]
    public void The_toggle_handler_uses_the_windows_own_mode_decision()
    {
        Assert.True(QuietRun.Silenced, QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, TestSettingsRoot.Failure);
        var commands = new KeyCommands(new KeyCommandContext(), setDefault: true);
        Assert.Equal(CommandValues.ToggleLoggingMode,
            commands.Lookup(Keys.L | Keys.Control | Keys.Shift)?.KeyDef.Id);

        string source = File.ReadAllText(FindRepoFile("JJFlexWpf", "KeyCommands.cs"));
        int start = source.IndexOf("private void ToggleLoggingModeHandler()", StringComparison.Ordinal);
        int end = source.IndexOf("private void ToggleFreqReadoutHandler()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("mw.ToggleLoggingMode();", source[start..end]);
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
