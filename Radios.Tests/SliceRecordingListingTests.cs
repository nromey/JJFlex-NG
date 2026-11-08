#nullable enable
using System;
using System.IO;
using System.Linq;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #999, step 5: the rows Play takes shows for the radio's slice recordings,
/// and the sentence each reads. Only a slice the radio reports a recording on
/// gets a row; a recording no stamp describes was made outside JJ Flexible (S1)
/// and is not backed up.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceRecordingListingTests : IDisposable
{
    private const string Serial = "1234-5678-8600-0994";
    private readonly RadioConfigStaticsScope _scope = new(nameof(SliceRecordingListingTests));
    public void Dispose() => _scope.Dispose();

    private static readonly DateTime Now = new(2026, 11, 7, 21, 0, 0, DateTimeKind.Local);

    private static RadioCase ThreeSlices()
    {
        var c = UnifiedTakesMuteTests.ThreeSlices(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        foreach (int index in new[] { 1, 2, 3 }) c.F.Vendor.Status($"slice {index} play=disabled");
        return c;
    }

    [Fact]
    public void No_radio_lists_nothing() =>
        Assert.Empty(SliceRecordingListing.Rows(null, Now));

    [Fact]
    public void Only_slices_the_radio_reports_a_recording_on_get_a_row()
    {
        using var c = ThreeSlices();
        Assert.Empty(SliceRecordingListing.Rows(c.F.Rig, Now));
        c.F.Vendor.Status("slice 2 record_time=12.0 play=0");
        var row = Assert.Single(SliceRecordingListing.Rows(c.F.Rig, Now));
        Assert.Equal("B", row.Letter);
        Assert.Equal(12.0, row.Seconds);
        Assert.Equal(SliceRecorderTarget.Letter('B'), row.Target);
    }

    [Fact]
    public void A_recording_no_stamp_describes_was_made_outside_and_is_not_backed_up()
    {
        using var c = ThreeSlices();
        c.F.Vendor.Status("slice 1 record_time=30.0 play=0");
        var row = Assert.Single(SliceRecordingListing.Rows(c.F.Rig, Now));
        Assert.True(row.MadeOutside);
        Assert.False(row.BackedUp);
        Assert.Contains(Lexicon.Get("audio.takes.slice_made_outside"), row.Sentence);
        Assert.Contains(Lexicon.Get("audio.takes.slice_not_backed_up"), row.Sentence);
    }

    [Fact]
    public void A_stamped_recording_says_when_and_where_it_was_made_and_whether_it_is_backed_up()
    {
        using var c = ThreeSlices();
        var made = new DateTime(2026, 11, 7, 20, 15, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 30.0, made, 14.2, "USB", SliceTakeSource.F11));
        c.F.Vendor.Status("slice 1 record_time=30.0 play=0");
        var row = Assert.Single(SliceRecordingListing.Rows(c.F.Rig, Now));
        Assert.False(row.MadeOutside);
        Assert.False(row.BackedUp);
        Assert.Equal(SliceRecordingListing.Sentence("A", 30, row.Stamp, false, Now), row.Sentence);
        Assert.Contains("14.2", row.Sentence);
        Assert.Contains("USB", row.Sentence);
        Assert.Contains(TakeListing.When(made, Now), row.Sentence);

        string folder = _scope.PathTo("takes", "a-backup");
        Directory.CreateDirectory(folder);
        Assert.True(RadioConfig.RecordSliceTakeBackup(Serial, "A", 30.0, folder));
        row = Assert.Single(SliceRecordingListing.Rows(c.F.Rig, Now));
        Assert.True(row.BackedUp);
        Assert.Contains(Lexicon.Get("audio.takes.slice_backed_up"), row.Sentence);
        Directory.Delete(folder);
        Assert.False(Assert.Single(SliceRecordingListing.Rows(c.F.Rig, Now)).BackedUp);
    }

    [Fact]
    public void A_stamp_for_a_recording_since_replaced_is_not_used()
    {
        using var c = ThreeSlices();
        Assert.True(RadioConfig.RecordSliceTake(Serial, "A", 30.0, DateTime.UtcNow, 14.2, "USB", SliceTakeSource.F11));
        c.F.Vendor.Status("slice 1 record_time=55.0 play=0");
        var row = Assert.Single(SliceRecordingListing.Rows(c.F.Rig, Now));
        Assert.Null(row.Stamp);
        Assert.True(row.MadeOutside);
    }

    [Fact]
    public void A_Check_your_audio_recording_says_so()
    {
        var made = DateTime.UtcNow;
        var stamp = new SliceTakeStamp { Letter = "A", RecordedSeconds = 8, MadeUtc = made, Source = SliceTakeSource.AudioCheck, FrequencyMHz = 7.15, Mode = "LSB" };
        string sentence = SliceRecordingListing.Sentence("A", 8, stamp, false, Now);
        Assert.Contains(Lexicon.Get("audio.takes.slice_made_check_at", ("when", TakeListing.When(made, Now)),
            ("frequency", 7.15.ToString("0.000###", System.Globalization.CultureInfo.CurrentCulture)), ("mode", "LSB")), sentence);
    }

    [Fact]
    public void Rows_come_in_slice_letter_order()
    {
        using var c = ThreeSlices();
        c.F.Vendor.Status("slice 3 record_time=5.0 play=0");
        c.F.Vendor.Status("slice 1 record_time=6.0 play=0");
        Assert.Equal(new[] { "A", "C" }, SliceRecordingListing.Rows(c.F.Rig, Now).Select(r => r.Letter));
    }
}
