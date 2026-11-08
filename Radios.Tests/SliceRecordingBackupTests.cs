#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;
using Sink = Radios.Tests.UnifiedTakesRunTests.Sink;

namespace Radios.Tests;

/// <summary>
/// #999, step 4: backing up a slice recording. The radio plays it, the other
/// slices are muted, the playback is captured as a take of kind
/// <c>slice-recording</c>, and only a whole capture of the recording that is
/// still there marks it backed up. On a radio object over the vendor fixture,
/// with the test standing in for the recorder's second sink.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceRecordingBackupTests : IDisposable
{
    private const string Serial = "1234-5678-8600-0996";
    private readonly RadioConfigStaticsScope _scope = new(nameof(SliceRecordingBackupTests));
    private readonly string _root = UnifiedTakeFixtures.NewRoot();

    public void Dispose()
    {
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try { UnifiedTakeFixtures.Remove(_root); break; }
                catch (IOException) when (attempt < 50) { System.Threading.Thread.Sleep(50); }
            }
        }
        finally { _scope.Dispose(); }
    }

    private static async Task<T> Within<T>(RadioCase c, Task<T> task, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
            await Task.Delay(10);
        }
        Assert.True(task.IsCompleted, "did not finish in time");
        return await task;
    }

    private static async Task<bool> PlayRequested(RadioCase c) => await Within(c, Task.Run(async () =>
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!c.F.Vendor.Transport.Commands.Contains("slice set 1 play=1") && DateTime.UtcNow < deadline) await Task.Delay(10);
        return c.F.Vendor.Transport.Commands.Contains("slice set 1 play=1");
    }));

    /// <summary>Slice A (index 1) holds a two-second recording; 200 packets of 240 frames at 24 kHz is two seconds.</summary>
    private static RadioCase WithRecording(double seconds = 2.0)
    {
        var c = UnifiedTakesMuteTests.ThreeSlices(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 record_time=" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " play=0");
        return c;
    }

    private static string? Header(string folder, string key) =>
        TakeHeader.Read(Path.Combine(folder, "take.txt")).LastOrDefault(f => f.Key == key).Value;

    private async Task<SliceBackupOutcome> BackUpWhole(RadioCase c)
    {
        var sink = new Sink();
        var backup = SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root, sink.Start);
        Assert.True(await PlayRequested(c));
        sink.Plays(1);
        return await Within(c, backup);
    }

    [Fact]
    public async Task A_whole_backup_is_a_slice_recording_take_and_marks_the_recording_backed_up()
    {
        using var c = WithRecording();
        var outcome = await BackUpWhole(c);
        Assert.Equal(SliceBackupResult.BackedUp, outcome.Result);
        Assert.Equal("A", outcome.Letter);
        string folder = outcome.Directory!;

        var entry = Assert.Single(TakeCatalog.List(_root));
        Assert.Equal(TakeKind.SliceRecording, entry.Kind);
        Assert.Equal(PlaybackTakeRun.SliceBackupMethod, Header(folder, "check-method"));
        // Made outside this app: no stamp existed, so the backup made one.
        Assert.Equal(SliceTakeSource.Outside, Header(folder, SliceRecordingFields.Source));
        Assert.Equal("isolated", Header(folder, "capture-isolation"));
        // A backup is not an audio check, so it joins no test.
        Assert.Null(TakeSession.Membership(_root, folder));

        var stamp = RadioConfig.LoadForRadio(Serial).TrustedSliceTakeStamp("A", 2.0)!;
        Assert.Equal(SliceTakeSource.Outside, stamp.Source);
        Assert.Equal(folder, stamp.BackupTakeDirectory);
        Assert.Equal(SliceOverwriteDecision.WarnBackedUp,
            SliceOverwriteGate.Decide(c.F.Rig.SliceOverwriteFactsFor(SliceRecorderTarget.ActiveSlice)!));
        Assert.Null(SliceRecordingBackup.Running);
    }

    [Fact]
    public async Task A_backup_of_an_F11_recording_keeps_where_it_was_made()
    {
        using var c = WithRecording();
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 2.0, DateTime.UtcNow, 14.2, "USB", SliceTakeSource.F11));
        var outcome = await BackUpWhole(c);
        Assert.Equal(SliceBackupResult.BackedUp, outcome.Result);
        Assert.Equal(SliceTakeSource.F11, Header(outcome.Directory!, SliceRecordingFields.Source));
        Assert.Equal("14.2", Header(outcome.Directory!, SliceRecordingFields.FrequencyMHz));
        Assert.Equal("USB", Header(outcome.Directory!, "mode"));
        var stamp = RadioConfig.LoadForRadio(Serial).TrustedSliceTakeStamp("A", 2.0)!;
        Assert.Equal(SliceTakeSource.F11, stamp.Source);
        Assert.Equal(outcome.Directory, stamp.BackupTakeDirectory);
    }

    [Fact]
    public async Task Deleting_the_backup_re_arms_the_warning()
    {
        using var c = WithRecording();
        var outcome = await BackUpWhole(c);
        Assert.Equal(SliceBackupResult.BackedUp, outcome.Result);
        Directory.Delete(outcome.Directory!, recursive: true);
        Assert.Equal(SliceOverwriteDecision.WarnNotBackedUp,
            SliceOverwriteGate.Decide(c.F.Rig.SliceOverwriteFactsFor(SliceRecorderTarget.ActiveSlice)!));
    }

    [Fact]
    public async Task A_capture_shorter_than_the_recording_is_not_a_backup()
    {
        using var c = WithRecording(seconds: 2.0);
        var sink = new Sink();
        var backup = SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root, sink.Start);
        Assert.True(await PlayRequested(c));
        sink.Plays(1, packets: 40); // 0.4 seconds, then the radio reports the stop
        var outcome = await Within(c, backup);
        Assert.False(outcome.BackedUp);
        Assert.Equal(SliceBackupResult.Stopped, outcome.Result);
        Assert.Equal("", RadioConfig.LoadForRadio(Serial).TrustedSliceTakeStamp("A", 2.0)?.BackupTakeDirectory ?? "");
    }

    [Fact]
    public async Task Stop_ends_it_and_nothing_counts_as_backed_up()
    {
        using var c = WithRecording();
        var sink = new Sink();
        SliceRecordingBackup? running = null;
        var backup = SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root, sink.Start, b => running = b);
        Assert.True(await PlayRequested(c));
        sink.Plays(1, packets: 40, stop: false);
        Assert.NotNull(running);
        Assert.True(SliceRecordingBackup.StopRunning());
        var outcome = await Within(c, backup, seconds: 12);
        Assert.Equal(SliceBackupResult.Stopped, outcome.Result);
        Assert.Contains("slice set 1 play=0", c.F.Vendor.Transport.Commands);
        Assert.Equal(SliceOverwriteDecision.WarnNotBackedUp,
            SliceOverwriteGate.Decide(c.F.Rig.SliceOverwriteFactsFor(SliceRecorderTarget.ActiveSlice)!));
        Assert.Null(SliceRecordingBackup.Running);
    }

    [Fact]
    public async Task A_recording_replaced_while_it_played_is_not_marked_backed_up()
    {
        using var c = WithRecording();
        var sink = new Sink();
        var backup = SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root, sink.Start);
        Assert.True(await PlayRequested(c));
        c.F.Vendor.Status("slice 1 record_time=9.0");
        sink.Plays(1);
        var outcome = await Within(c, backup);
        Assert.Equal(SliceBackupResult.RecordingChanged, outcome.Result);
        Assert.Null(RadioConfig.LoadForRadio(Serial).TrustedSliceTakeStamp("A", 9.0));
    }

    [Fact]
    public async Task Nothing_on_the_slice_is_nothing_to_back_up_and_nothing_is_played()
    {
        using var c = UnifiedTakesMuteTests.ThreeSlices(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 play=disabled");
        var outcome = await Within(c, SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root, new Sink().Start));
        Assert.Equal(SliceBackupResult.NothingToBackUp, outcome.Result);
        Assert.DoesNotContain("slice set 1 play=1", c.F.Vendor.Transport.Commands);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateDirectories(_root).Any());
    }

    [Fact]
    public async Task A_capture_the_recorder_refuses_saves_nothing_and_plays_nothing()
    {
        using var c = WithRecording();
        var outcome = await Within(c, SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root,
            new Sink { Refuse = true }.Start));
        Assert.Equal(SliceBackupResult.NotSaved, outcome.Result);
        Assert.DoesNotContain("slice set 1 play=1", c.F.Vendor.Transport.Commands);
    }

    // ── A saved Radio recording playback counts as a backup (ruled 17:57) ──

    private async Task<string> SavedCheckPlayback(RadioCase c, int packets)
    {
        var run = PlaybackTakeRun.Admit(c.F.Rig, TakeKind.RadioRecording, SliceRecorderTarget.ActiveSlice, new TakeSeriesContext());
        run.NoteKeyed();
        var sink = new Sink();
        Assert.True(await Within(c, run.ArmAsync(_root, sink.Start, muteOthers: true)), run.NotSavedReason);
        sink.Plays(1, packets: packets);
        Assert.True(await Within(c, run.Finished));
        return run.DirectoryPath!;
    }

    [Fact]
    public async Task A_whole_saved_check_playback_marks_the_slice_recording_backed_up()
    {
        using var c = WithRecording();
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 2.0, DateTime.UtcNow, 14.2, "USB", SliceTakeSource.AudioCheck));
        string folder = await SavedCheckPlayback(c, packets: 200);
        Assert.True(SliceRecordingBackup.NoteSavedPlaybackAsBackup(c.F.Rig, SliceRecorderTarget.ActiveSlice, folder));
        var stamp = RadioConfig.LoadForRadio(Serial).TrustedSliceTakeStamp("A", 2.0)!;
        Assert.Equal(folder, stamp.BackupTakeDirectory);
        Assert.Equal(SliceTakeSource.AudioCheck, stamp.Source);
    }

    [Fact]
    public async Task A_saved_check_playback_stopped_early_is_not_a_backup()
    {
        using var c = WithRecording();
        string folder = await SavedCheckPlayback(c, packets: 40);
        Assert.False(SliceRecordingBackup.NoteSavedPlaybackAsBackup(c.F.Rig, SliceRecorderTarget.ActiveSlice, folder));
        Assert.Empty(RadioConfig.LoadForRadio(Serial).SliceTakeStamps);
    }

    // ── Comparison: refused beside any other kind, and with itself ──

    [Fact]
    public async Task A_slice_recording_take_is_never_compared()
    {
        using var c = WithRecording();
        var first = await BackUpWhole(c);
        Assert.True(first.BackedUp);
        lock (c.F.Vendor.Transport.Written) c.F.Vendor.Transport.Written.Clear();
        c.F.Vendor.Status("slice 1 play=0");
        var second = await BackUpWhole(c);
        Assert.True(second.Directory != null);

        var withItself = TakeAnalysis.CompareTakes(first.Directory!, second.Directory!);
        Assert.False(withItself.Comparable);
        Assert.Equal("unlike-takes:slice-recording", withItself.Reason);

        // A Radio recording take, through the check's own run.
        var run = PlaybackTakeRun.Admit(c.F.Rig, TakeKind.RadioRecording, SliceRecorderTarget.TransmitSlice, new TakeSeriesContext());
        run.NoteKeyed();
        var sink = new Sink();
        Assert.True(await Within(c, run.ArmAsync(_root, sink.Start, muteOthers: true)), run.NotSavedReason);
        sink.Plays(1);
        Assert.True(await Within(c, run.Finished));
        var mixed = TakeAnalysis.CompareTakes(first.Directory!, run.DirectoryPath!);
        Assert.False(mixed.Comparable);
        Assert.Equal("unlike-takes:kind", mixed.Reason);
        Assert.Equal(Lexicon.Get("audio.takes.mixed_nothing_fair",
                ("first", TakeKindWords.Phrase(TakeKind.SliceRecording)), ("second", TakeKindWords.Phrase(TakeKind.RadioRecording))),
            ListenbackResultText.AudioRoadSentence(mixed, chosen: true));
    }
}
