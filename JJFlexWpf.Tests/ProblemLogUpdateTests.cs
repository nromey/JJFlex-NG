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
}
