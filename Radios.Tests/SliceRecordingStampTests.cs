using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #999, step 1: the stamp a slice recording leaves in the radio's config.
/// F11 saves the radio, slice, frequency, mode, time, length and where the
/// recording came from; a backup writes its take folder in; a config written
/// before any of that reads cleanly with the new fields empty.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceRecordingStampTests : IDisposable
{
    private const string Serial = "1234-5678-8600-0999";
    private readonly RadioConfigStaticsScope _scope = new(nameof(SliceRecordingStampTests));
    public void Dispose() => _scope.Dispose();

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        return condition();
    }

    private static SliceTakeStamp? Stamp(string letter, double seconds) =>
        RadioConfig.LoadForRadio(Serial).TrustedSliceTakeStamp(letter, seconds);

    [Fact]
    public async Task An_F11_recording_saves_its_frequency_mode_and_source()
    {
        using var c = new RadioCase(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 play=disabled");
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Vendor.Status("slice 1 record_time=12.5 play=0");
        Assert.True(await c.Pump(WaitUntil(() => Stamp("B", 12.5) != null)));
        var stamp = Stamp("B", 12.5)!;
        Assert.Equal(14.2, stamp.FrequencyMHz, 6);
        Assert.Equal("USB", stamp.Mode);
        Assert.Equal(SliceTakeSource.F11, stamp.Source);
        Assert.Equal("", stamp.BackupTakeDirectory);
        Assert.Equal(DateTimeKind.Utc, stamp.MadeUtc.Kind);
    }

    [Fact]
    public async Task A_recording_Check_your_audio_marks_is_stamped_as_an_audio_check()
    {
        using var c = new RadioCase(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 play=disabled");
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Rig.MarkSliceTakeSource(SliceRecorderTarget.ActiveSlice, SliceTakeSource.AudioCheck);
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Vendor.Status("slice 1 record_time=8.0 play=0");
        Assert.True(await c.Pump(WaitUntil(() => Stamp("B", 8.0) != null)));
        Assert.Equal(SliceTakeSource.AudioCheck, Stamp("B", 8.0)!.Source);
    }

    [Fact]
    public async Task The_next_recording_on_the_slice_is_the_operators_own_again()
    {
        using var c = new RadioCase(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 play=disabled");
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Rig.MarkSliceTakeSource(SliceRecorderTarget.ActiveSlice, SliceTakeSource.AudioCheck);
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Vendor.Status("slice 1 record_time=8.0 play=0");
        Assert.True(await c.Pump(WaitUntil(() => Stamp("B", 8.0) != null)));
        // The fixture echoes no record_time while recording; a radio replaces the old length.
        c.F.Vendor.Status("slice 1 play=disabled");
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Vendor.Status("slice 1 record_time=20.0 play=0");
        Assert.True(await c.Pump(WaitUntil(() => Stamp("B", 20.0) != null)));
        Assert.Equal(SliceTakeSource.F11, Stamp("B", 20.0)!.Source);
    }

    [Fact]
    public async Task The_gate_sees_the_radio_the_stamp_and_the_backup_folder()
    {
        using var c = new RadioCase(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 play=disabled");
        // Nothing on the slice: nothing to overwrite.
        Assert.Equal(SliceOverwriteDecision.RecordNow,
            SliceOverwriteGate.Decide(c.F.Rig.SliceOverwriteFactsFor(SliceRecorderTarget.ActiveSlice)!));
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Vendor.Status("slice 1 record_time=12.5 play=0");
        Assert.True(await c.Pump(WaitUntil(() => Stamp("B", 12.5) != null)));
        var facts = c.F.Rig.SliceOverwriteFactsFor(SliceRecorderTarget.ActiveSlice)!;
        Assert.Equal("B", facts.Letter);
        Assert.Equal(12.5, facts.RadioSeconds);
        Assert.Equal(SliceOverwriteDecision.WarnNotBackedUp, SliceOverwriteGate.Decide(facts));

        string folder = Path.Combine(_scope.PathTo("takes"), "slice-b");
        Directory.CreateDirectory(folder);
        Assert.True(RadioConfig.RecordSliceTakeBackup(Serial, "B", 12.5, folder));
        Assert.Equal(SliceOverwriteDecision.WarnBackedUp,
            SliceOverwriteGate.Decide(c.F.Rig.SliceOverwriteFactsFor(SliceRecorderTarget.ActiveSlice)!));
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(Serial, false));
        Assert.Equal(SliceOverwriteDecision.RecordNow,
            SliceOverwriteGate.Decide(c.F.Rig.SliceOverwriteFactsFor(SliceRecorderTarget.ActiveSlice)!));
    }

    [Fact]
    public void Marking_a_slice_that_is_not_recording_changes_nothing()
    {
        using var c = new RadioCase(Serial);
        c.F.Rig.MarkSliceTakeSource(SliceRecorderTarget.ActiveSlice, SliceTakeSource.AudioCheck);
        Assert.Empty(RadioConfig.LoadForRadio(Serial).SliceTakeStamps);
    }

    [Fact]
    public void A_config_written_before_999_reads_the_new_fields_as_empty()
    {
        var made = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 30, made, 7.2, "LSB", SliceTakeSource.F11));
        string path = _scope.PathTo("radios", Serial, "config.xml");
        string xml = File.ReadAllText(path);
        // Positive control: the new fields really were written, so removing them is a real change.
        Assert.Contains("<FrequencyMHz>", xml);
        string old = Regex.Replace(xml, @"\s*<(FrequencyMHz|Mode|Source|BackupTakeDirectory)>[^<]*</\1>", "");
        old = Regex.Replace(old, @"\s*<(Mode|Source|BackupTakeDirectory)\s*/>", "");
        Assert.DoesNotContain("<FrequencyMHz>", old);
        File.WriteAllText(path, old);

        var saved = RadioConfig.LoadForRadio(Serial);
        Assert.Null(saved.ReadFailure);
        var stamp = saved.TrustedSliceTakeStamp("A", 30)!;
        Assert.Equal(made, saved.TrustedSliceTakeTime("A", 30));
        Assert.Equal(0, stamp.FrequencyMHz);
        Assert.Equal("", stamp.Mode);
        Assert.Equal("", stamp.Source);
        Assert.Equal("", stamp.BackupTakeDirectory);
    }

    [Fact]
    public void A_backup_of_the_recording_the_stamp_describes_is_written_into_it()
    {
        var made = DateTime.UtcNow;
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 30, made, 14.2, "USB", SliceTakeSource.F11));
        Assert.True(RadioConfig.RecordSliceTakeBackup(Serial, "A", 30.05, @"C:\takes\one"));
        var stamp = Stamp("A", 30)!;
        Assert.Equal(@"C:\takes\one", stamp.BackupTakeDirectory);
        Assert.Equal(SliceTakeSource.F11, stamp.Source);
        Assert.Equal(14.2, stamp.FrequencyMHz, 6);
    }

    [Fact]
    public void A_backup_of_a_recording_this_app_did_not_make_stamps_it_as_made_outside()
    {
        Assert.True(RadioConfig.RecordSliceTakeBackup(Serial, "C", 44, @"C:\takes\two"));
        var saved = RadioConfig.LoadForRadio(Serial);
        var stamp = saved.TrustedSliceTakeStamp("C", 44)!;
        Assert.Equal(SliceTakeSource.Outside, stamp.Source);
        Assert.Equal(@"C:\takes\two", stamp.BackupTakeDirectory);
        // No time is known for it, so no time is claimed.
        Assert.Null(saved.TrustedSliceTakeTime("C", 44));
    }

    [Fact]
    public void A_backup_of_a_recording_that_replaced_ours_replaces_our_stamp()
    {
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 30, DateTime.UtcNow, 14.2, "USB", SliceTakeSource.F11));
        Assert.True(RadioConfig.RecordSliceTakeBackup(Serial, "A", 55, @"C:\takes\three"));
        var saved = RadioConfig.LoadForRadio(Serial);
        Assert.Null(saved.TrustedSliceTakeStamp("A", 30));
        var stamp = saved.TrustedSliceTakeStamp("A", 55)!;
        Assert.Equal(SliceTakeSource.Outside, stamp.Source);
        Assert.Single(saved.SliceTakeStamps, s => s.Letter == "A");
    }

    [Fact]
    public void A_new_recording_on_the_slice_drops_the_old_ones_backup()
    {
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 30, DateTime.UtcNow, 14.2, "USB", SliceTakeSource.F11));
        Assert.True(RadioConfig.RecordSliceTakeBackup(Serial, "A", 30, @"C:\takes\four"));
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 30.02, DateTime.UtcNow, 14.2, "USB", SliceTakeSource.F11));
        Assert.Equal("", Stamp("A", 30.02)!.BackupTakeDirectory);
    }

    [Theory]
    [InlineData("", 10, @"C:\t")]
    [InlineData("A", 0, @"C:\t")]
    [InlineData("A", double.NaN, @"C:\t")]
    [InlineData("A", 10, "")]
    public void A_backup_with_nothing_to_record_is_refused(string letter, double seconds, string folder)
    {
        Assert.False(RadioConfig.RecordSliceTakeBackup(Serial, letter, seconds, folder));
    }
}
