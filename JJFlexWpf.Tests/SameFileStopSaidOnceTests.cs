using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #636, ruled by Noel 2026-09-26: "I'd silence the second." When the
/// diagnostic log's own file stops during a recording, "The diagnostic log
/// stopped being written" is said, and "An earlier recording may be missing
/// its last lines" about that same file is not. Both stay in the Problems
/// list, and a tail problem about a different file is still said.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> Every test runs on the test runner's MTA thread
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>), where WPF
/// cannot build an element, and nothing here asks it to. The offer is driven
/// through its own handler, <c>OnFailureReported</c>, reached by name: it is
/// not installed, so it has no dispatcher and announces inline, as it does
/// when the bridge is not wired. Announcing plays the problem earcon and
/// queues a sentence; <see cref="QuietRun"/> has render off before any of it,
/// so nothing sounds, and the sentence is read back from the speech layer's
/// recent messages.
/// </para>
/// <para>
/// The reports are built as <c>RecordingHealthWatch</c> builds them, which
/// <c>Radios.Tests.RecordingHealthNoticeTests</c> pins: the stop names the
/// recording session as its subject, and the tail follows that subject.
/// </para>
/// </remarks>
public sealed class SameFileStopSaidOnceTests : IDisposable
{
    private const string Stop = "The diagnostic log stopped being written";
    private const string Tail = "An earlier recording may be missing its last lines";

    public SameFileStopSaidOnceTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        ProblemLog.Clear();
        DiagnosticOffer.ResetSessionState();
        ResetSpeechState();
    }

    public void Dispose()
    {
        ProblemLog.Clear();
        DiagnosticOffer.ResetSessionState();
        ResetSpeechState();
    }

    /// <summary>
    /// The speech arbiter is process-wide; start and end from nothing so an
    /// earlier test's waiting speech cannot stand in for, or hide, these
    /// announcements. Internal to Radios, so reached by name, here only.
    /// </summary>
    private static void ResetSpeechState() =>
        typeof(ScreenReaderOutput).GetMethod("ResetTransientSpeechStateForTest", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    private static void Offer(OperationFailureEventArgs e) =>
        typeof(DiagnosticOffer).GetMethod("OnFailureReported", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { null, e });

    private static OperationFailureEventArgs StopOf(string session) =>
        new(FailureKind.RecordingStopped, Stop, "detail", key: null, isUpdate: false, isResolution: false,
            subject: "recording-session:" + session, followsSubject: false);

    private static OperationFailureEventArgs TailOf(string session) =>
        new(FailureKind.RecordingRecoveryAtRisk, Tail, "detail", key: "recording-recovery:" + Guid.NewGuid().ToString("N"),
            isUpdate: false, isResolution: false, subject: "recording-session:" + session, followsSubject: true);

    private static string Announced(string what) => Lexicon.Get("logging.problem.announcement", ("what", what));

    /// <summary>What the offer said after <paramref name="marker"/>, oldest first.</summary>
    private static List<string> SaidSince(string marker)
    {
        var said = new List<string>();
        foreach (string m in ScreenReaderOutput.RecentMessages)
        {
            if (m == marker) break;
            said.Add(m);
        }
        said.Reverse();
        return said;
    }

    private static string Marker()
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        return marker;
    }

    [Fact]
    public void The_log_stopping_is_said_and_the_same_files_short_tail_is_not_and_both_are_listed()
    {
        AssertTheDeskIsSafe();
        string marker = Marker();

        Offer(StopOf("a"));
        Offer(TailOf("a"));

        Assert.Equal(new[] { Announced(Stop) }, SaidSince(marker).ToArray());
        var listed = ProblemLog.NewestFirst();
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, e => e.What == Stop && e.Kind == FailureKind.RecordingStopped);
        Assert.Contains(listed, e => e.What == Tail && e.Kind == FailureKind.RecordingRecoveryAtRisk);
    }

    /// <summary>
    /// The condition is the subject, never the kind: a short tail about
    /// another file is said, and the silenced one did not use up its kind's
    /// one announcement.
    /// </summary>
    [Fact]
    public void A_short_tail_about_another_file_is_still_said()
    {
        AssertTheDeskIsSafe();
        string marker = Marker();

        Offer(StopOf("a"));
        Offer(TailOf("a"));
        Offer(TailOf("b"));

        Assert.Equal(new[] { Announced(Stop), Announced(Tail) }, SaidSince(marker).ToArray());
        Assert.Equal(3, ProblemLog.NewestFirst().Count);
    }

    /// <summary>
    /// Positive control for the first test: with no stop said about its
    /// file, the same tail report is said. So the silence is the subject's
    /// doing, not the tail report being unspeakable.
    /// </summary>
    [Fact]
    public void A_short_tail_is_said_when_nothing_about_its_file_was()
    {
        AssertTheDeskIsSafe();
        string marker = Marker();

        Offer(TailOf("a"));

        Assert.Equal(new[] { Announced(Tail) }, SaidSince(marker).ToArray());
    }

    /// <summary>
    /// "The file whose stop was just announced": a stop that was NOT said
    /// (the log had already stopped once this session, so its kind was used)
    /// silences nothing, and the tail about its file is the first word the
    /// operator hears about that file.
    /// </summary>
    [Fact]
    public void A_stop_that_was_not_said_does_not_silence_its_files_tail()
    {
        AssertTheDeskIsSafe();
        Offer(StopOf("earlier"));
        string marker = Marker();

        Offer(StopOf("a"));
        Offer(TailOf("a"));

        Assert.Equal(new[] { Announced(Tail) }, SaidSince(marker).ToArray());
    }
}
