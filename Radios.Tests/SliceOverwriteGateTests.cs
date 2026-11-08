using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #999, step 2: whether starting a slice recording warns first, and which
/// warning. Every case of the decision, and every way "backed up" can fail,
/// since backed up is checked live and never stored as a flag.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceOverwriteGateTests : IDisposable
{
    private const string Serial = "1234-5678-8600-0998";
    private const string Folder = @"C:\takes\backup-of-b";
    private readonly RadioConfigStaticsScope _scope = new(nameof(SliceOverwriteGateTests));
    public void Dispose() => _scope.Dispose();

    private static SliceTakeStamp BackedUpStamp(double seconds = 30, string folder = Folder) => new()
    {
        Letter = "B", RecordedSeconds = seconds, MadeUtc = DateTime.UtcNow, Source = SliceTakeSource.F11,
        BackupTakeDirectory = folder,
    };

    private static bool Present(string path) => path == Folder;
    private static bool Gone(string path) => false;

    // ── The four decisions ──

    [Fact]
    public void No_recording_on_the_slice_records_at_once()
    {
        Assert.Equal(SliceOverwriteDecision.RecordNow, SliceOverwriteGate.Decide(false, null, null, Present, warn: true));
        Assert.Equal(SliceOverwriteDecision.RecordNow, SliceOverwriteGate.Decide(false, 30, BackedUpStamp(), Present, warn: true));
    }

    [Fact]
    public void The_setting_off_records_at_once_in_both_forms()
    {
        Assert.Equal(SliceOverwriteDecision.RecordNow, SliceOverwriteGate.Decide(true, 30, BackedUpStamp(), Present, warn: false));
        Assert.Equal(SliceOverwriteDecision.RecordNow, SliceOverwriteGate.Decide(true, 30, null, Present, warn: false));
    }

    [Fact]
    public void A_backed_up_recording_warns_in_the_backed_up_form()
    {
        Assert.Equal(SliceOverwriteDecision.WarnBackedUp, SliceOverwriteGate.Decide(true, 30.05, BackedUpStamp(), Present, warn: true));
    }

    [Fact]
    public void A_recording_with_no_backup_warns_in_the_other_form()
    {
        Assert.Equal(SliceOverwriteDecision.WarnNotBackedUp, SliceOverwriteGate.Decide(true, 30, null, Present, warn: true));
    }

    // ── Every way "backed up" fails ──

    [Fact]
    public void No_stamp_is_not_backed_up() =>
        Assert.False(SliceOverwriteGate.IsBackedUp(null, 30, Present));

    [Theory]
    [InlineData(30.11)]
    [InlineData(29.89)]
    [InlineData(55)]
    public void A_length_that_no_longer_matches_is_not_backed_up(double radioSeconds) =>
        Assert.False(SliceOverwriteGate.IsBackedUp(BackedUpStamp(), radioSeconds, Present));

    [Theory]
    [InlineData(30.09)]
    [InlineData(29.91)]
    public void A_length_within_the_tolerance_still_matches(double radioSeconds) =>
        Assert.True(SliceOverwriteGate.IsBackedUp(BackedUpStamp(), radioSeconds, Present));

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void A_length_the_radio_did_not_report_is_not_backed_up(double? radioSeconds) =>
        Assert.False(SliceOverwriteGate.IsBackedUp(BackedUpStamp(), radioSeconds, Present));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_stamp_naming_no_backup_folder_is_not_backed_up(string folder) =>
        Assert.False(SliceOverwriteGate.IsBackedUp(BackedUpStamp(folder: folder), 30, _ => true));

    [Fact]
    public void A_backup_folder_that_has_been_deleted_is_not_backed_up()
    {
        Assert.False(SliceOverwriteGate.IsBackedUp(BackedUpStamp(), 30, Gone));
        Assert.Equal(SliceOverwriteDecision.WarnNotBackedUp, SliceOverwriteGate.Decide(true, 30, BackedUpStamp(), Gone, warn: true));
    }

    [Fact]
    public void A_disk_that_throws_reads_as_not_backed_up() =>
        Assert.False(SliceOverwriteGate.IsBackedUp(BackedUpStamp(), 30, _ => throw new IOException("gone")));

    [Fact]
    public void The_real_disk_is_asked_for_the_folder()
    {
        string folder = Path.Combine(_scope.PathTo("takes"), "backup");
        Directory.CreateDirectory(folder);
        var facts = new SliceOverwriteFacts(Serial, "B", true, 30, BackedUpStamp(folder: folder), true);
        Assert.Equal(SliceOverwriteDecision.WarnBackedUp, SliceOverwriteGate.Decide(facts));
        Directory.Delete(folder);
        Assert.Equal(SliceOverwriteDecision.WarnNotBackedUp, SliceOverwriteGate.Decide(facts));
    }

    [Fact]
    public void No_facts_means_no_slice_of_ours_and_nothing_to_overwrite() =>
        Assert.Equal(SliceOverwriteDecision.RecordNow, SliceOverwriteGate.Decide((SliceOverwriteFacts)null!));

    // ── The setting ──

    [Fact]
    public void The_warning_is_on_by_default_and_saved_per_radio()
    {
        Assert.True(RadioConfig.LoadForRadio(Serial).WarnBeforeSliceRecordingOverwritten);
        Assert.True(RadioConfig.Update(Serial, c => c.Nickname = "bench"));
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(Serial, false));
        var saved = RadioConfig.LoadForRadio(Serial);
        Assert.False(saved.WarnBeforeSliceRecordingOverwritten);
        Assert.Equal("bench", saved.Nickname);
        Assert.True(RadioConfig.LoadForRadio("1234-5678-8600-0997").WarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void A_config_written_before_the_setting_reads_it_as_on()
    {
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(Serial, false));
        string path = _scope.PathTo("radios", Serial, "config.xml");
        string xml = File.ReadAllText(path);
        Assert.Contains("<WarnBeforeSliceRecordingOverwritten>", xml);
        File.WriteAllText(path, Regex.Replace(xml, @"\s*<WarnBeforeSliceRecordingOverwritten>[^<]*</WarnBeforeSliceRecordingOverwritten>", ""));
        Assert.True(RadioConfig.LoadForRadio(Serial).WarnBeforeSliceRecordingOverwritten);
    }
}
