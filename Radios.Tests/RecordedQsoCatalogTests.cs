#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The recordings dialog's catalog, prune rules, Recycle Bin refusals and the
/// deletion-aware choice for play from computer (#787, Tape Deck track 4).
/// </summary>
/// <remarks>
/// What these cannot see: a real Recycle Bin (the move is replaced so no test
/// puts a Windows question on the desktop or a file in the operator's bin), a
/// real recording's audio (the files are header-only FLAC, which is all the
/// catalog reads), and anything a screen reader says.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class RecordedQsoCatalogTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(RecordedQsoCatalogTests));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-qsos-" + Guid.NewGuid().ToString("N"));

    public RecordedQsoCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ComputerRecorder.Install(null);
        ComputerRecorder.ChosenRecording = null;
        RecycleBin.Implementation = DefaultRecycle;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try
        {
            if (Directory.Exists(RecordedQsoCatalog.FolderPath))
                Directory.Delete(RecordedQsoCatalog.FolderPath, recursive: true);
        }
        catch (IOException) { }
        _scope.Dispose();
    }

    private static readonly Func<string, IntPtr, RecycleResult> DefaultRecycle = RecycleBin.Implementation;

    /// <summary>A FLAC file that is only its STREAMINFO header: all the catalog reads.</summary>
    internal static string WriteHeader(string folder, string name, int rate = 24000, int channels = 1, long frames = 24000)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("fLaC"u8.ToArray());
        w.Write((byte)0x80); // last metadata block, STREAMINFO
        w.Write(new byte[] { 0, 0, 34 });
        w.Write(new byte[] { 0x10, 0x00, 0x10, 0x00 }); // min and max block size
        w.Write(new byte[6]); // min and max frame size
        ulong packed = ((ulong)rate << 44) | ((ulong)(channels - 1) << 41) | (15UL << 36) | (ulong)frames;
        for (int i = 7; i >= 0; i--) w.Write((byte)(packed >> (i * 8)));
        w.Write(new byte[16]); // MD5
        return path;
    }

    private string Month(int year, int month) => Path.Combine(_root, year.ToString("0000"), month.ToString("00"));

    [Theory]
    [InlineData("QSO-2026-10-24-153012", true)]
    [InlineData("QSO-2026-10-24-153012-2", true)]
    [InlineData("QSO-2026-10-24-153012-trim", false)]
    [InlineData("QSO-2026-10-24-1530", false)]
    [InlineData("Contest pileup", false)]
    [InlineData("QSO-2026-13-24-153012", false)]
    public void The_recorders_own_name_is_recognised_and_nothing_else(string name, bool recognised)
    {
        Assert.Equal(recognised, RecordedQsoCatalog.TryParseRecorderName(name, out var at));
        if (recognised) Assert.Equal(new DateTime(2026, 10, 24, 15, 30, 12), at);
    }

    [Fact]
    public void A_scan_lists_newest_first_by_when_the_recording_began_across_month_folders()
    {
        WriteHeader(Month(2026, 9), "QSO-2026-09-30-235959.flac");
        WriteHeader(Month(2026, 10), "QSO-2026-10-24-153012.flac", frames: 24000 * 728);
        WriteHeader(Month(2026, 10), "QSO-2026-10-01-080000.flac");
        var scan = RecordedQsoCatalog.Scan(_root);
        Assert.Equal(0, scan.Unreadable);
        Assert.Equal(new[] { "QSO-2026-10-24-153012", "QSO-2026-10-01-080000", "QSO-2026-09-30-235959" },
            scan.Recordings.Select(r => r.Name));
        var newest = scan.Recordings[0];
        Assert.True(newest.NamedByRecorder);
        Assert.Equal(728, newest.Seconds);
        Assert.Equal(24000, newest.SampleRate);
    }

    [Fact]
    public void A_renamed_file_keeps_its_place_by_file_time_and_an_unfinished_one_has_no_length()
    {
        string renamed = WriteHeader(Month(2026, 10), "Contest pileup.flac");
        var then = new DateTime(2026, 10, 2, 9, 0, 0);
        File.SetCreationTime(renamed, then);
        File.SetLastWriteTime(renamed, then.AddMinutes(20));
        WriteHeader(Month(2026, 10), "QSO-2026-10-03-100000.flac", frames: 0);
        WriteHeader(Month(2026, 10), "QSO-2026-10-01-100000.flac");
        var rows = RecordedQsoCatalog.Scan(_root).Recordings;
        Assert.Equal(new[] { "QSO-2026-10-03-100000", "Contest pileup", "QSO-2026-10-01-100000" }, rows.Select(r => r.Name));
        Assert.False(rows[1].NamedByRecorder);
        Assert.Equal(then, rows[1].RecordedAt); // the earlier of creation and last write
        Assert.Null(rows[0].Seconds);
    }

    [Fact]
    public void An_unreadable_file_is_counted_never_dropped_silently_and_a_conversion_in_progress_is_not_listed()
    {
        WriteHeader(Month(2026, 10), "QSO-2026-10-24-153012.flac");
        File.WriteAllText(Path.Combine(Month(2026, 10), "QSO-2026-10-24-160000.flac"), "not a flac file");
        WriteHeader(Month(2026, 10), RecordedQsoCatalog.ExportTemporaryPrefix + "abc.flac");
        var scan = RecordedQsoCatalog.Scan(_root);
        Assert.Single(scan.Recordings);
        Assert.Equal(1, scan.Unreadable);
    }

    [Fact]
    public void A_missing_folder_is_an_empty_list_not_an_error()
    {
        var scan = RecordedQsoCatalog.Scan(Path.Combine(_root, "nothing-here"));
        Assert.Empty(scan.Recordings);
        Assert.Equal(0, scan.Unreadable);
    }

    private IReadOnlyList<RecordedQso> FiveDays()
    {
        for (int d = 1; d <= 5; d++)
            WriteHeader(Month(2026, 10), $"QSO-2026-10-{d:00}-120000.flac");
        return RecordedQsoCatalog.Scan(_root).Recordings;
    }

    [Fact]
    public void Prune_by_count_takes_the_oldest_and_never_swaps_in_a_newer_one_for_a_file_in_use()
    {
        var rows = FiveDays();
        var plan = RecordedQsoCatalog.PlanOldest(rows, 2, _ => false);
        Assert.Equal(new[] { "QSO-2026-10-01-120000", "QSO-2026-10-02-120000" }, plan.Move.Select(r => r.Name));
        Assert.Equal(plan.Move.Sum(r => r.Bytes), plan.Bytes);

        string busy = rows.Single(r => r.Name == "QSO-2026-10-01-120000").Path;
        var held = RecordedQsoCatalog.PlanOldest(rows, 2, p => p == busy);
        Assert.Equal(new[] { "QSO-2026-10-02-120000" }, held.Move.Select(r => r.Name));
        Assert.Equal(new[] { "QSO-2026-10-01-120000" }, held.InUse.Select(r => r.Name));
    }

    [Fact]
    public void Prune_by_age_takes_only_what_began_before_the_cutoff()
    {
        var rows = FiveDays();
        var plan = RecordedQsoCatalog.PlanOlderThan(rows, 2, new DateTime(2026, 10, 5, 13, 0, 0), _ => false);
        // Cutoff 2026-10-03 13:00: the 1st, 2nd and 3rd (noon) began before it.
        Assert.Equal(new[] { "QSO-2026-10-01-120000", "QSO-2026-10-02-120000", "QSO-2026-10-03-120000" },
            plan.Move.Select(r => r.Name));
        Assert.Empty(RecordedQsoCatalog.PlanOlderThan(rows, 30, new DateTime(2026, 10, 5), _ => false).Move);
    }

    [Theory]
    [InlineData(System.IO.DriveType.Network, true, false, null, false, 10L, RecycleRefusal.NoRecycleBin)]
    [InlineData(System.IO.DriveType.CDRom, true, false, null, false, 10L, RecycleRefusal.NoRecycleBin)]
    [InlineData(System.IO.DriveType.Fixed, false, false, null, false, 10L, RecycleRefusal.NoRecycleBin)]
    [InlineData(System.IO.DriveType.Fixed, true, true, null, false, 10L, RecycleRefusal.RecycleBinOff)]
    [InlineData(System.IO.DriveType.Fixed, true, false, null, true, 10L, RecycleRefusal.RecycleBinOff)]
    [InlineData(System.IO.DriveType.Fixed, true, false, 1L, false, 2L * 1024 * 1024, RecycleRefusal.TooLarge)]
    [InlineData(System.IO.DriveType.Fixed, true, false, 1L, false, 1024L, RecycleRefusal.None)]
    [InlineData(System.IO.DriveType.Removable, true, false, null, false, 10L, RecycleRefusal.None)]
    public void Every_case_the_Recycle_Bin_cannot_take_is_refused_before_anything_moves(
        System.IO.DriveType drive, bool bin, bool nuke, long? maxMb, bool policy, long bytes, RecycleRefusal expected)
    {
        Assert.Equal(expected, RecycleBin.Check(drive, bin, nuke, maxMb, policy, bytes));
    }

    [Fact]
    public void A_missing_file_and_a_throwing_move_are_refusals_and_never_reach_the_shell()
    {
        var calls = new List<string>();
        RecycleBin.Implementation = (p, _) => { calls.Add(p); throw new IOException("shell said no"); };
        Assert.Equal(RecycleRefusal.Missing, RecycleBin.Move(Path.Combine(_root, "gone.flac")).Refusal);
        Assert.Empty(calls);
        string real = WriteHeader(_root, "QSO-2026-10-24-153012.flac");
        var result = RecycleBin.Move(real);
        Assert.False(result.Moved);
        Assert.Equal(RecycleRefusal.Failed, result.Refusal);
        Assert.True(File.Exists(real), "a failed move must leave the file");
        Assert.Equal(new[] { real }, calls);
    }

    private sealed class Engine : IComputerRecorder
    {
        public bool IsRecording => false;
        public bool IsPlaying => false;
        public string? LastRecordingPath { get; set; }
        public readonly List<string> Played = new();
        public Task<RecorderOutcome> StartRecordingAsync(FlexBase rig) => throw new NotSupportedException();
        public Task<RecorderOutcome> StopRecordingAsync() => throw new NotSupportedException();
        public Task<RecorderOutcome> PlayAsync(string path)
        {
            Played.Add(path);
            return Task.FromResult(new RecorderOutcome(true, "", Speech.SpeechSubject.ComputerRecorder));
        }
        public Task<RecorderOutcome> StopPlaybackAsync() => throw new NotSupportedException();
    }

    [Fact]
    public async Task Play_from_computer_skips_a_deleted_choice_and_a_deleted_last_recording_for_the_newest_on_disk()
    {
        string folder = Path.Combine(RecordedQsoCatalog.FolderPath, "2026", "10");
        string older = WriteHeader(folder, "QSO-2026-10-01-120000.flac");
        string newest = WriteHeader(folder, "QSO-2026-10-02-120000.flac");
        string chosen = WriteHeader(folder, "QSO-2026-09-01-120000.flac");
        var engine = new Engine { LastRecordingPath = newest };
        ComputerRecorder.Install(engine);
        ComputerRecorder.ChosenRecording = chosen;

        Assert.Equal(chosen, ComputerRecorder.ChosenForPlayback());
        File.Delete(chosen);
        Assert.Equal(newest, ComputerRecorder.ChosenForPlayback());
        File.Delete(newest);
        // The engine's cached last path now names a deleted file; the newest
        // surviving recording on disk is played instead.
        Assert.Equal(older, ComputerRecorder.ChosenForPlayback());
        await ComputerRecorder.TogglePlaybackAsync(transmitting: false);
        Assert.Equal(new[] { older }, engine.Played);
        File.Delete(older);
        Assert.Null(ComputerRecorder.ChosenForPlayback());
    }
}
