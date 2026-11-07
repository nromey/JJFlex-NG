#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes part 7, the Play takes window's logic, tested without the
/// window (which is never constructed in a test): rows newest first, one
/// sentence each; Show offers All takes and only the kinds present; details
/// attribute figures to one take; a saved copy never touches the take.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesListingTests
{
    [Fact]
    public void Rows_are_newest_first_one_sentence_each_naming_kind_test_take_radio_profile_and_note()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var series = new TakeSeriesContext();
            string older = UnifiedTakeFixtures.IqTake(root, TakeKind.OffAir);
            // An unstamped folder is dated by take.txt; make it plainly older.
            File.SetLastWriteTimeUtc(Path.Combine(older, "take.txt"), DateTime.UtcNow.AddMinutes(-5));
            string folder = AudioTakeWriter.NewDirectory(root);
            Directory.CreateDirectory(folder);
            var (test, number) = series.Reserve(root, folder);
            UnifiedTakeFixtures.AudioTake(root, name: Path.GetFileName(folder));
            TakeSession.SetNote(root, folder, "new windscreen");

            var rows = TakeListing.Rows(root, DateTime.Now);
            Assert.Equal(folder, rows[0].Directory);
            Assert.Equal(older, rows[1].Directory);
            string sentence = rows[0].Sentence;
            Assert.StartsWith("Radio recording, earlier today at ", sentence);
            Assert.EndsWith(", " + test.Name + " take " + number + ", on FLEX-8600 Bench, mic profile Default. Note: new windscreen.", sentence);
            Assert.Contains("Heard on the air", rows[1].Sentence);
            Assert.Contains("no test", rows[1].Sentence);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Show_offers_all_takes_then_only_the_kinds_present_in_the_ruled_order()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            UnifiedTakeFixtures.AudioTake(root, TakeKind.LoopedBack);
            UnifiedTakeFixtures.IqTake(root, TakeKind.Diagnostic);
            UnifiedTakeFixtures.IqTake(root, TakeKind.OffAir);
            var rows = TakeListing.Rows(root, DateTime.Now);
            var options = TakeListing.ShowOptions(rows);
            Assert.Equal(new[] { TakeListing.ShowAll, TakeKind.OffAir, TakeKind.LoopedBack, TakeKind.Diagnostic }, options);
            Assert.Equal(new[] { "All takes", "Heard on the air", "Looped back", "Diagnostic" }, options.Select(TakeListing.ShowName));
            Assert.Single(TakeListing.Filter(rows, TakeKind.Diagnostic));
            Assert.Equal(3, TakeListing.Filter(rows, TakeListing.ShowAll).Count());
            Assert.Empty(TakeListing.Filter(rows, TakeKind.RadioRecording));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_playback_takes_details_say_what_it_is_and_attribute_its_figures_to_it_alone()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string folder = UnifiedTakeFixtures.AudioTake(root);
            var row = TakeListing.Rows(root, DateTime.Now).Single();
            var take = TakeReader.Inspect(folder);
            string details = TakeListing.Details(row, take, TakeStatistics.Measure(folder));
            Assert.Contains("Made on FLEX-8600 Bench, serial same-radio, over LAN.", details);
            Assert.Contains("This take is complete, 2.5 seconds long.", details);
            Assert.Contains("It is not an off-air measurement.", details);
            Assert.Contains("hasn't been measured yet", details);
            Assert.Contains("Its own figures: integrated loudness ", details);
            Assert.DoesNotContain("Splatter", details);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_damaged_folder_is_a_row_that_says_it_cannot_be_read()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string damaged = Path.Combine(root, "20261101-140000-000-00000006");
            Directory.CreateDirectory(damaged);
            File.WriteAllText(Path.Combine(damaged, "take.txt"), "no equals sign here\n");
            var row = TakeListing.Rows(root, DateTime.Now).Single();
            Assert.StartsWith("Damaged take, ", row.Sentence);
            Assert.EndsWith("It can't be read.", row.Sentence);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_a_copy_writes_elsewhere_and_leaves_the_take_untouched(bool flac)
    {
        // The FLAC library is resolved the way the FLAC tests resolve it, so
        // this test passes alone as well as in the whole suite.
        RecordedQsoFlacTests.NativeLibraryReady();
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string folder = UnifiedTakeFixtures.AudioTake(root);
            string header = TakeHeader.Checksum(Path.Combine(folder, "take.txt"));
            string wav = TakeHeader.Checksum(Path.Combine(folder, "heard.wav"));
            string destination = Path.Combine(root, flac ? "copy.flac" : "copy.wav");
            Assert.True(TakeListing.SaveCopy(folder, destination, flac));
            Assert.True(new FileInfo(destination).Length > 1000);
            if (!flac) Assert.Equal(wav, TakeHeader.Checksum(destination));
            Assert.Equal(header, TakeHeader.Checksum(Path.Combine(folder, "take.txt")));
            Assert.Equal(wav, TakeHeader.Checksum(Path.Combine(folder, "heard.wav")));
            Assert.Equal("clean", TakeReader.Inspect(folder).State);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }
}
