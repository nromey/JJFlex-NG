using System;
using System.Windows.Controls;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The window an operator meets after a connection drop archived their
/// recording, realised under the guard, and read for what its explanation
/// box actually carries when the archived recording's tail is uncertain
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
public sealed class CaptureArchivedDialogTests
{
    [Fact]
    public void The_explanation_box_carries_the_tail_caveat_when_the_notice_says_so()
    {
        var notice = new CaptureArchiveNotice("6300inshack",
            @"C:\Users\nrome\AppData\Roaming\JJFlexRadio\Traces\2026\09\trace-20260924-201500-connection_dropped.zip",
            successorOpened: true, archivedSessionId: Guid.NewGuid(), tailUncertain: true);

        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new CaptureArchivedDialog(notice);
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
        var notice = new CaptureArchiveNotice("6300inshack", @"C:\Traces\one.zip",
            successorOpened: true, archivedSessionId: Guid.NewGuid(), tailUncertain: false);

        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new CaptureArchivedDialog(notice);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            UiThread.Drain();
            var panel = (StackPanel)dialog.Content;
            return (LoadedFired: realized.LoadedFired, Explanation: ((TextBox)panel.Children[0]).Text);
        }, TimeSpan.FromSeconds(30));

        Assert.True(outcome.LoadedFired);
        Assert.Equal(notice.Explanation, outcome.Explanation);
        Assert.DoesNotContain("last lines", outcome.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Sol's review of H10, blocker 2 — written and compiled in Track
    /// H11; NOT RUN.</b> The recording state changes after the notice
    /// exists and before the window is constructed — the dispatcher queue
    /// between <c>CaptureArchiveWatch</c>'s BeginInvoke and the constructor —
    /// and the explanation box carries the sentence for the state at
    /// construction, not the state the worker saw. The headless half of
    /// this is <c>Radios.Tests.DropNoticeStateTests</c>; what this adds is
    /// that the realised control is what carries it.
    /// </summary>
    [Fact]
    public void The_explanation_box_carries_the_recording_state_at_construction_not_at_the_archive()
    {
        bool recording = true;
        var notice = new CaptureArchiveNotice("6300inshack", @"C:\Traces\one.zip",
            successorOpened: true, archivedSessionId: Guid.NewGuid(),
            tailUncertain: false, sinkFailedBeforeDrop: false,
            recordingNow: () => recording, fileFacts: null);
        // The worker's moment: the ordinary promise. Positive control.
        Assert.Contains("is being kept too", notice.Explanation, StringComparison.Ordinal);

        // The queue: the log goes off before the dispatched action runs.
        recording = false;

        var outcome = UiThread.RunWithTimeout(() =>
        {
            var dialog = new CaptureArchivedDialog(notice);
            using var realized = RealizedDialog.Realize(dialog, Sweep.Strategy);
            UiThread.Drain();
            var panel = (StackPanel)dialog.Content;
            return (LoadedFired: realized.LoadedFired, Explanation: ((TextBox)panel.Children[0]).Text);
        }, TimeSpan.FromSeconds(30));

        Assert.True(outcome.LoadedFired, "Loaded never fired, so the tree was not realised and this proves nothing.");
        Assert.DoesNotContain("is being kept too", outcome.Explanation, StringComparison.Ordinal);
        Assert.Contains("did start recording again after the connection went, but it is not recording now",
                        outcome.Explanation, StringComparison.Ordinal);
        Assert.Equal(notice.Explanation, outcome.Explanation);
    }
}
