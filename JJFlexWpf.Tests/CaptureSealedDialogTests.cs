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
/// box actually carries when the sealed recording's tail is uncertain
/// (Sprint 45 Track H7, Astra's ruling on the pending-record failure; Track
/// H8 narrowed the caveat to the one predicate a committed archive can carry).
/// </summary>
/// <remarks>
/// <para><b>Written and compiled in Tracks H7 and H8; NOT RUN.</b> This project
/// constructs real dialogs and <see cref="DeskGuard"/> refuses it on a live
/// desk, correctly. The sentences themselves are pinned without a window in
/// <c>Radios.Tests.RecordingHealthNoticeTests</c>; what this adds is that the
/// window's explanation control is the thing carrying them, which only the
/// realised tree can show.</para>
/// </remarks>
public sealed class CaptureSealedDialogTests
{
    [Fact]
    public void The_explanation_box_carries_the_tail_caveat_when_the_notice_says_so()
    {
        var notice = new CaptureSealNotice("6300inshack",
            @"C:\Users\nrome\AppData\Roaming\JJFlexRadio\Traces\2026\09\trace-20260924-201500-connection_dropped.zip",
            successorOpened: true, archivedSessionId: Guid.NewGuid(), tailUncertain: true);

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
        Assert.Contains(notice.TailCaveat, outcome.Explanation, StringComparison.Ordinal);
        Assert.Equal(notice.Explanation, outcome.Explanation);
        // The path keeps its own control, whole.
        Assert.Equal(notice.ArchivePath, outcome.Path);
    }

    [Fact]
    public void The_explanation_box_is_unchanged_when_the_tail_is_certain()
    {
        var notice = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip",
            successorOpened: true, archivedSessionId: Guid.NewGuid(), tailUncertain: false);

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
        Assert.DoesNotContain("last lines", outcome.Explanation, StringComparison.Ordinal);
    }
}
