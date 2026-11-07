#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radios.Fixer;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes part 3, test series (design section 6 with Noel's rulings
/// Q7, D3 and D5): TakeSession is the one owner; a test has a hidden
/// permanent ID and a readable, renamable name whose old forms stay aliases;
/// take numbers are never reused; the "start a new test or add to this one"
/// question is asked when the microphone stand-ins, the profile or the radio
/// changed, and "add to this one" silences it for that test.
/// </summary>
public sealed class UnifiedTakesSeriesTests
{
    private static TakeAdmissionFacts Facts(string serial = "same-radio", string profile = "Default",
        string input = "PC", string device = "Yeti") => new(serial, profile, input, device);

    /// <summary>Make a take in the context's series, as the capture will: reserve, write, mark made.</summary>
    private static string MakeTake(string root, TakeSeriesContext series, TakeAdmissionFacts facts)
    {
        string folder = AudioTakeWriter.NewDirectory(root);
        Directory.CreateDirectory(folder);
        var (test, _) = series.Reserve(root, folder);
        UnifiedTakeFixtures.AudioTake(root, name: Path.GetFileName(folder), facts: new Dictionary<string, string?>
        {
            ["serial"] = facts.Serial, ["mic-profile"] = facts.MicProfile,
            ["mic-input"] = facts.MicInput, ["pc-microphone-device"] = facts.PcMicrophoneDevice,
        });
        TakeSession.MarkReservation(root, test.Id, folder, TakeReservation.Made);
        return folder;
    }

    [Fact]
    public void A_new_test_is_named_by_the_fixers_readable_id_and_found_by_it()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var test = TakeSession.CreateTest(root);
            Assert.True(Guid.TryParse(test.Id, out _));
            Assert.Equal(FixerRunId.Length, test.ReadableId.Length);
            Assert.All(test.ReadableId.Replace("-", ""), c => Assert.Contains(c, FixerRunId.Alphabet));
            Assert.Equal(test.ReadableId, test.Name);
            Assert.Equal(test.Id, TakeSession.FindTest(root, test.ReadableId.ToLowerInvariant())!.Id);
            Assert.Equal(TakeTest.PolicyAsk, test.ChangePolicy);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_generated_id_that_collides_is_drawn_again()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var first = TakeSession.CreateTest(root, new Random(42));
            var second = TakeSession.CreateTest(root, new Random(42));
            Assert.NotEqual(first.ReadableId, second.ReadableId);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Take_numbers_rise_and_are_never_reused_after_a_failure_or_a_deletion()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var test = TakeSession.CreateTest(root);
            var folders = Enumerable.Range(0, 3).Select(i => Path.Combine(root, "take-" + i)).ToList();
            folders.ForEach(f => Directory.CreateDirectory(f));
            Assert.Equal(1, TakeSession.Reserve(root, test.Id, folders[0]));
            Assert.Equal(2, TakeSession.Reserve(root, test.Id, folders[1]));
            TakeSession.MarkReservation(root, test.Id, folders[1], TakeReservation.Failed);
            TakeSession.MarkReservation(root, test.Id, folders[0], TakeReservation.Deleted);
            Assert.Equal(3, TakeSession.Reserve(root, test.Id, folders[2]));
            var stored = TakeSession.Test(root, test.Id)!;
            Assert.Equal(new[] { 1, 2, 3 }, stored.Reservations.Select(r => r.Number));
            Assert.Equal(TakeReservation.Failed, stored.Reservations[1].State);
            Assert.Equal(3, TakeSession.Membership(root, folders[2])!.Value.Number);
            Assert.Throws<InvalidOperationException>(() => TakeSession.Reserve(root, test.Id, folders[2]));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Concurrent_reservations_never_share_a_number()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var test = TakeSession.CreateTest(root);
            var folders = Enumerable.Range(0, 20).Select(i => Path.Combine(root, "c-" + i)).ToList();
            folders.ForEach(f => Directory.CreateDirectory(f));
            var numbers = new int[folders.Count];
            Parallel.For(0, folders.Count, i => numbers[i] = TakeSession.Reserve(root, test.Id, folders[i]));
            Assert.Equal(Enumerable.Range(1, 20), numbers.OrderBy(n => n));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_rename_keeps_every_old_name_as_an_alias_and_touches_no_take()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var series = new TakeSeriesContext();
            string take = MakeTake(root, series, Facts());
            var test = TakeSession.Test(root, series.ActiveTestId!)!;
            string headerSum = TakeHeader.Checksum(Path.Combine(take, "take.txt"));
            string wavSum = TakeHeader.Checksum(Path.Combine(take, "heard.wav"));
            string metaSum = TakeHeader.Checksum(Path.Combine(take, TakeMetadata.FileName));

            Assert.Equal("", TakeSession.Rename(root, test.Id, "  Heil test  "));
            Assert.Equal("", TakeSession.Rename(root, test.Id, "Heil test, boom arm"));
            var renamed = TakeSession.Test(root, test.Id)!;
            Assert.Equal("Heil test, boom arm", renamed.Name);
            Assert.Equal(test.ReadableId, renamed.ReadableId);
            Assert.Equal(new[] { test.ReadableId, "Heil test" }, renamed.Aliases);
            foreach (string name in new[] { test.ReadableId, "heil TEST", "Heil test, boom arm" })
                Assert.Equal(test.Id, TakeSession.FindTest(root, name)!.Id);
            // Composed and decomposed spellings are one name.
            Assert.Equal("", TakeSession.Rename(root, test.Id, "Café mic"));
            Assert.Equal(test.Id, TakeSession.FindTest(root, "Café mic")!.Id);

            Assert.Equal(headerSum, TakeHeader.Checksum(Path.Combine(take, "take.txt")));
            Assert.Equal(wavSum, TakeHeader.Checksum(Path.Combine(take, "heard.wav")));
            Assert.Equal(metaSum, TakeHeader.Checksum(Path.Combine(take, TakeMetadata.FileName)));
            Assert.Equal(test.Id, TakeSession.Membership(root, take)!.Value.Test.Id);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_rename_is_refused_for_empty_control_only_overlong_or_another_tests_names()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var mine = TakeSession.CreateTest(root);
            var theirs = TakeSession.CreateTest(root);
            Assert.Equal("", TakeSession.Rename(root, theirs.Id, "Their test"));
            Assert.Equal(TakeSession.RenameEmpty, TakeSession.Rename(root, mine.Id, "   "));
            Assert.Equal(TakeSession.RenameEmpty, TakeSession.Rename(root, mine.Id, "\t\u0007"));
            Assert.Equal(TakeSession.RenameTooLong, TakeSession.Rename(root, mine.Id, new string('a', TakeSession.NameMaximum + 1)));
            Assert.Equal(TakeSession.RenameTaken, TakeSession.Rename(root, mine.Id, "their TEST"));
            Assert.Equal(TakeSession.RenameTaken, TakeSession.Rename(root, mine.Id, theirs.ReadableId));
            Assert.Equal(TakeSession.RenameUnknown, TakeSession.Rename(root, Guid.NewGuid().ToString(), "Anything"));
            Assert.Equal(mine.ReadableId, TakeSession.Test(root, mine.Id)!.Name);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_reconnect_to_the_same_radio_keeps_the_test_and_another_radio_starts_a_new_one_without_asking()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var series = new TakeSeriesContext();
            series.Connected("radio-A");
            MakeTake(root, series, Facts("radio-A"));
            string first = series.ActiveTestId!;
            // A SmartLink dropout and reconnect to the same radio (D3).
            series.Connected("radio-A");
            Assert.False(series.Admit(root, Facts("radio-A")).Ask);
            MakeTake(root, series, Facts("radio-A"));
            Assert.Equal(first, series.ActiveTestId);
            Assert.Equal(2, TakeSession.Test(root, first)!.Reservations.Count);
            // A different radio: a new test at its first take, and no question.
            series.Connected("radio-B");
            Assert.Null(series.ActiveTestId);
            Assert.False(series.Admit(root, Facts("radio-B")).Ask);
            MakeTake(root, series, Facts("radio-B"));
            Assert.NotEqual(first, series.ActiveTestId);
            // A new run of the app starts a new test too.
            series.Reset();
            Assert.Null(series.ActiveTestId);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Theory]
    [InlineData("mic-profile")]
    [InlineData("mic-input")]
    [InlineData("pc-microphone-device")]
    [InlineData("serial")]
    public void A_changed_microphone_stand_in_profile_or_radio_asks_before_the_next_take(string changed)
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var series = new TakeSeriesContext();
            series.Connected("radio-A");
            MakeTake(root, series, Facts());
            Assert.False(series.Admit(root, Facts()).Ask);
            var next = changed switch
            {
                "mic-profile" => Facts(profile: "Heil PR 781"),
                "mic-input" => Facts(input: "MIC"),
                "pc-microphone-device" => Facts(device: "Shure MV7"),
                _ => Facts(serial: "radio-B"),
            };
            var admission = series.Admit(root, next);
            Assert.True(admission.Ask);
            Assert.Equal(new[] { changed }, admission.Changed);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Add_to_this_one_silences_the_question_for_that_test_even_after_it_is_resumed()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var series = new TakeSeriesContext();
            MakeTake(root, series, Facts());
            string id = series.ActiveTestId!;
            Assert.True(series.Admit(root, Facts(profile: "B")).Ask);
            var kept = series.Answer(root, startNewTest: false);
            Assert.Equal(id, kept.Id);
            Assert.False(series.Admit(root, Facts(profile: "B")).Ask);
            MakeTake(root, series, Facts(profile: "B"));
            Assert.False(series.Admit(root, Facts(profile: "C")).Ask);
            // A new run, resumed from Play takes: still silenced.
            series.Reset();
            series.Resume(id);
            Assert.False(series.Admit(root, Facts(profile: "D", serial: "radio-Z")).Ask);
            Assert.Equal(TakeTest.PolicyAddToThisTest, TakeSession.Test(root, id)!.ChangePolicy);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Start_a_new_test_makes_it_active_and_its_question_is_fresh()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var series = new TakeSeriesContext();
            MakeTake(root, series, Facts());
            string old = series.ActiveTestId!;
            var created = series.Answer(root, startNewTest: true);
            Assert.NotEqual(old, created.Id);
            Assert.Equal(created.Id, series.ActiveTestId);
            Assert.Equal(TakeTest.PolicyAsk, created.ChangePolicy);
            MakeTake(root, series, Facts(profile: "B"));
            Assert.Equal(1, TakeSession.Membership(root, TakeSession.Test(root, created.Id)!.Reservations
                .Select(r => Path.Combine(root, r.Folder)).Single())!.Value.Number);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_note_on_an_old_take_writes_only_its_sidecar_and_invents_no_test()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string take = UnifiedTakeFixtures.AudioTake(root);
            string headerSum = TakeHeader.Checksum(Path.Combine(take, "take.txt"));
            TakeSession.SetNote(root, take, "With the new windscreen");
            Assert.Equal("With the new windscreen", TakeSession.Metadata(take)!.Note);
            Assert.Equal("", TakeSession.Metadata(take)!.TestId);
            Assert.Null(TakeSession.Membership(root, take));
            Assert.Empty(TakeSession.Tests(root));
            Assert.Equal(headerSum, TakeHeader.Checksum(Path.Combine(take, "take.txt")));
            Assert.Equal("clean", TakeReader.Inspect(take).State);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void An_unreadable_test_record_is_an_error_and_never_reads_as_no_tests()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var test = TakeSession.CreateTest(root);
            File.WriteAllText(Path.Combine(root, TakeCatalog.TestsFolder, test.Id, "test.json"), "{ not json");
            Assert.ThrowsAny<Exception>(() => TakeSession.Tests(root));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }
}
