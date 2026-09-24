using System;
using System.Windows.Controls;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The window an operator meets after a connection drop sealed their
/// recording, realised under the guard, and read for what its explanation
/// box actually carries when the sealed recording's recovery is at risk
/// (Sprint 45 Track H7, Astra's ruling on the pending-record failure).
/// </summary>
/// <remarks>
/// <para><b>Written and compiled in Track H7; NOT RUN.</b> This project
/// constructs real dialogs and <see cref="DeskGuard"/> refuses it on a live
/// desk, correctly. The sentences themselves are pinned without a window in
/// <c>Radios.Tests.RecordingHealthNoticeTests</c>; what this adds is that the
/// window's explanation control is the thing carrying them, which only the
/// realised tree can show.</para>
/// </remarks>
public sealed class CaptureSealedDialogTests
{
    [Fact]
    public void The_explanation_box_carries_the_recovery_caveat_when_the_notice_says_so()
    {
        var notice = new CaptureSealNotice("6300inshack",
            @"C:\Users\nrome\AppData\Roaming\JJFlexRadio\Traces\2026\09\trace-20260924-201500-connection_dropped.zip",
            successorOpened: true, archivedSessionId: Guid.NewGuid(), recoveryAtRisk: true);

        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new CaptureSealedDialog(notice);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            UiThread.Drain();
            var panel = (StackPanel)dialog.Content;
            var explanation = (TextBox)panel.Children[0];
            var pathBox = (TextBox)panel.Children[2];
            return (LoadedFired: realized.LoadedFired, Explanation: explanation.Text, Path: pathBox.Text);
        }, TimeSpan.FromSeconds(30));

        Assert.True(outcome.LoadedFired, "Loaded never fired, so the tree was not realised and this proves nothing.");
        Assert.Contains(notice.RecoveryCaveat, outcome.Explanation, StringComparison.Ordinal);
        Assert.Equal(notice.Explanation, outcome.Explanation);
        // The path keeps its own control, whole.
        Assert.Equal(notice.ArchivePath, outcome.Path);
    }

    [Fact]
    public void The_explanation_box_is_unchanged_when_recovery_is_not_at_risk()
    {
        var notice = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip",
            successorOpened: true, archivedSessionId: Guid.NewGuid(), recoveryAtRisk: false);

        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new CaptureSealedDialog(notice);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            UiThread.Drain();
            var panel = (StackPanel)dialog.Content;
            return (LoadedFired: realized.LoadedFired, Explanation: ((TextBox)panel.Children[0]).Text);
        }, TimeSpan.FromSeconds(30));

        Assert.True(outcome.LoadedFired);
        Assert.Equal(notice.Explanation, outcome.Explanation);
        Assert.DoesNotContain("index file", outcome.Explanation, StringComparison.Ordinal);
    }
}
