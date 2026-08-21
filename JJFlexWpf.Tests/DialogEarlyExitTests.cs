using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Regression traps for #159: dialogs that decide at Loaded time that they have
/// nothing to do, and close themselves.
///
/// <para>The WinForms originals expressed that early exit by assigning
/// DialogResult from the Load handler, and the WPF ports copied the idiom. In
/// WPF that assignment throws InvalidOperationException on any window not
/// opened with ShowDialog() — and because it fired during realisation, it took
/// down whatever was hosting the window. It aborted this suite on 2026-08-20
/// and again on 2026-08-21, the second time leaving Export Log and ATU Memories
/// windows stranded on the operator's screen mid-session.</para>
///
/// <para>These tests take the early-exit route under a non-modal Show(), which
/// is exactly the condition that threw. They deliberately drive the
/// picker-cancelled path rather than the missing-log path: the missing-log path
/// raises a message box, which is a real visible window when the private
/// desktop is unavailable (it is not available under "dotnet test" — see
/// PrivateDesktop), and this machine belongs to an operator who is using it.
/// Both paths close through the same guarded route, so one of them is
/// evidence for both.</para>
/// </summary>
public sealed class DialogEarlyExitTests
{
    [Fact]
    public void Export_dialog_early_exit_survives_a_non_modal_show()
    {
        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new ExportDialog
            {
                // A log name, so the missing-log message box never comes up...
                GetLogFileName = () => "TestLog.adi",
                // ...and a cancelled output picker, which is the early exit
                // that assigned DialogResult from Loaded.
                PickOutputFile = _ => null,
            };

            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            UiThread.Drain();
            return (realized.LoadedFired, dialog.IsVisible);
        }, TimeSpan.FromSeconds(30));

        Assert.True(outcome.LoadedFired,
            "Loaded never fired, so the early-exit path was not exercised and this test proves nothing.");
        Assert.False(outcome.IsVisible,
            "The cancelled picker should have closed the dialog — the early-exit intent has been lost.");
    }

    [Fact]
    public void Import_dialog_early_exit_survives_a_non_modal_show()
    {
        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new ImportDialog
            {
                GetLogFileName = () => "TestLog.adi",
                PickInputFile = _ => null,
            };

            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            UiThread.Drain();
            return (realized.LoadedFired, dialog.IsVisible);
        }, TimeSpan.FromSeconds(30));

        Assert.True(outcome.LoadedFired,
            "Loaded never fired, so the early-exit path was not exercised and this test proves nothing.");
        Assert.False(outcome.IsVisible,
            "The cancelled picker should have closed the dialog — the early-exit intent has been lost.");
    }
}
