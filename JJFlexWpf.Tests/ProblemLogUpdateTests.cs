using System;
using System.Linq;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The Problems list can be told something different about a thing it
/// already lists, in place, without the list growing or the entry losing
/// its time (Sprint 45 Track H8, Sol's review of H7, finding 4).
/// </summary>
/// <remarks>
/// <para><b>Written and compiled in Track H8; NOT RUN.</b> <see cref="ProblemLog"/>
/// is a static list and this constructs no window, but the project as a
/// whole is refused on a live desk by <c>DeskGuard</c> and the track's brief
/// forbids running it. The reporting side of the same change — that an
/// update arrives keyed and marked as an update, and that a ticket is
/// reported as new exactly once — is pinned without a window in
/// <c>Radios.Tests.RecordingHealthNoticeTests</c>.</para>
/// </remarks>
public sealed class ProblemLogUpdateTests : IDisposable
{
    public ProblemLogUpdateTests() { ProblemLog.Clear(); }
    public void Dispose() { ProblemLog.Clear(); }

    [Fact]
    public void An_update_replaces_the_keyed_entry_in_place_and_keeps_its_time()
    {
        ProblemLog.Record(FailureKind.ConnectFailed, "Connection failed", "before", key: null);
        ProblemLog.Record(FailureKind.RecordingRecoveryAtRisk, "An earlier recording is not yet safely filed",
                          "still filing it in the background", key: "recording-recovery:abc");
        ProblemLog.Record(FailureKind.SettingNotSaved, "A setting was not saved", "after", key: null);
        DateTime when = ProblemLog.NewestFirst().Single(e => e.Key == "recording-recovery:abc").WhenLocal;

        bool replaced = ProblemLog.Update("recording-recovery:abc",
            "An earlier recording could not be filed", "it could not be compressed and filed");

        Assert.True(replaced);
        Assert.Equal(3, ProblemLog.Count);
        var entries = ProblemLog.NewestFirst();
        // Same position: newest first is still setting, recording, connect.
        Assert.Equal("A setting was not saved", entries[0].What);
        Assert.Equal("An earlier recording could not be filed", entries[1].What);
        Assert.Equal("it could not be compressed and filed", entries[1].Detail);
        Assert.Equal(when, entries[1].WhenLocal);
        Assert.Equal(FailureKind.RecordingRecoveryAtRisk, entries[1].Kind);
        Assert.Equal("recording-recovery:abc", entries[1].Key);
        Assert.Equal("Connection failed", entries[2].What);
        Assert.DoesNotContain(entries, e => e.Detail.Contains("still filing", StringComparison.Ordinal));
    }

    [Fact]
    public void An_update_for_a_key_nobody_recorded_says_so_and_changes_nothing()
    {
        ProblemLog.Record(FailureKind.ConnectFailed, "Connection failed", "x", key: null);
        Assert.False(ProblemLog.Update("recording-recovery:never", "what", "detail"));
        Assert.False(ProblemLog.Update("", "what", "detail"));
        Assert.Equal(1, ProblemLog.Count);
        Assert.Equal("Connection failed", ProblemLog.NewestFirst()[0].What);
    }

    [Fact]
    public void An_unkeyed_entry_is_never_matched_by_an_update()
    {
        ProblemLog.Record(FailureKind.ConnectFailed, "Connection failed", "x");
        Assert.Null(ProblemLog.NewestFirst()[0].Key);
        Assert.False(ProblemLog.Update("anything", "what", "detail"));
        Assert.Equal("Connection failed", ProblemLog.NewestFirst()[0].What);
    }

    /// <summary>
    /// <b>Written and compiled in Track H9; NOT RUN</b> (same reason as the
    /// class remark). A RESOLUTION through the offer replaces the keyed
    /// entry in place and, when no entry carries the key, records NOTHING —
    /// unlike an ordinary update, which is treated as new. The offer's
    /// announcement policy is never reached: nothing is spoken for a
    /// resolution, and this test constructs no window and plays no earcon
    /// because the return happens before either (Sol's review of H8,
    /// blocker 3). The reporting side is pinned and RUN in
    /// <c>Radios.Tests.RecordingHealthNoticeTests</c>.
    /// </summary>
    [Fact]
    public void A_resolution_replaces_the_keyed_entry_and_records_nothing_when_there_is_none()
    {
        DiagnosticOffer.Install();
        ProblemLog.Record(FailureKind.RecordingRecoveryAtRisk, "An earlier recording is not yet safely filed",
                          "still filing it in the background", key: "recording-recovery:abc");
        DateTime when = ProblemLog.NewestFirst()[0].WhenLocal;

        OperationFailure.ResolveKeyed(FailureKind.RecordingRecoveryAtRisk,
            "An earlier recording has now been filed", "filed", "recording-recovery:abc");

        Assert.Equal(1, ProblemLog.Count);
        ProblemEntry entry = ProblemLog.NewestFirst()[0];
        Assert.Equal("An earlier recording has now been filed", entry.What);
        Assert.Equal("filed", entry.Detail);
        Assert.Equal(when, entry.WhenLocal);
        Assert.Equal("recording-recovery:abc", entry.Key);

        // No entry for the key: nothing is recorded, because a resolution is
        // not a problem. (An ordinary update WOULD be recorded as new here.)
        OperationFailure.ResolveKeyed(FailureKind.RecordingRecoveryAtRisk,
            "An earlier recording has now been filed", "filed", "recording-recovery:never");
        Assert.Equal(1, ProblemLog.Count);
        Assert.DoesNotContain(ProblemLog.NewestFirst(), e => e.Key == "recording-recovery:never");
    }
}
