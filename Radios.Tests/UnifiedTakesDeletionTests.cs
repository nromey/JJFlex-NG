#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes part 8, the delete guard (design section 8): a take is not
/// deleted while a put-back is owed for it on any radio, an unreadable store
/// refuses, only the chosen folder goes, and its number stays held.
/// </summary>
public sealed class UnifiedTakesDeletionTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = UnifiedTakeFixtures.NewRoot();
        public string Takes => Path.Combine(Root, "takes");
        public string Recovery => Path.Combine(Root, "listenback-recovery");
        public CheckRestoreReminderStore Reminders => new(Root);
        public string Take() => UnifiedTakeFixtures.AudioTake(Takes);
        public void Record(string serial, string takeFolder, string suffix = "")
        {
            Directory.CreateDirectory(Recovery);
            var snapshot = new RadioRecoverySnapshot { Serial = serial, TakeDirectory = takeFolder };
            File.WriteAllText(new RadioRecoveryStore(Recovery).PathFor(serial) + suffix, JsonSerializer.Serialize(snapshot));
        }
        public TakeDeletionVerdict Check(string folder, Func<string, bool>? busy = null) =>
            TakeDeletionGuard.Check(Takes, folder, Recovery, Reminders, busy);
        public void Dispose() => UnifiedTakeFixtures.Remove(Root);
    }

    [Fact]
    public void A_take_nothing_is_owed_for_is_deleted_alone_and_its_number_stays_held()
    {
        using var f = new Fixture();
        var series = new TakeSeriesContext();
        string folder = AudioTakeWriter.NewDirectory(f.Takes);
        Directory.CreateDirectory(folder);
        var (test, number) = series.Reserve(f.Takes, folder);
        UnifiedTakeFixtures.AudioTake(f.Takes, name: Path.GetFileName(folder));
        string neighbour = f.Take();
        f.Record("other-radio", neighbour);

        Assert.Equal(new TakeDeletionVerdict(true, ""), TakeDeletionGuard.Delete(f.Takes, folder, f.Recovery, f.Reminders));
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(neighbour));
        Assert.True(File.Exists(new RadioRecoveryStore(f.Recovery).PathFor("other-radio")));
        var held = TakeSession.Test(f.Takes, test.Id)!.Reservations.Find(r => r.Number == number)!;
        Assert.Equal(TakeReservation.Deleted, held.State);
    }

    [Fact]
    public void An_active_recovery_record_for_the_take_refuses_on_any_radio_connected_or_not()
    {
        using var f = new Fixture();
        string folder = f.Take();
        f.Record("radio-not-connected", folder + Path.DirectorySeparatorChar);
        Assert.Equal(new TakeDeletionVerdict(false, TakeDeletionGuard.RestoreOwed), f.Check(folder));
        Assert.Equal(TakeDeletionGuard.RestoreOwed, TakeDeletionGuard.Delete(f.Takes, folder, f.Recovery, f.Reminders).Reason);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void A_record_taken_into_put_back_blocks_while_that_radio_still_has_settings_to_put_back()
    {
        using var f = new Fixture();
        string folder = f.Take();
        f.Record("radio-A", folder, ".put-back-" + Guid.NewGuid().ToString("N"));
        f.Reminders.AddPutBacks("radio-A", new[] { new CheckPutBack(CheckPutBack.TxAntenna, "slice 0", "ANT1", "XVTA") });
        Assert.Equal(TakeDeletionGuard.PutBackOwed, f.Check(folder).Reason);
        f.Reminders.RemovePutBack("radio-A", new CheckPutBack(CheckPutBack.TxAntenna, "slice 0", "ANT1", "XVTA"));
        Assert.True(f.Check(folder).Allowed);
    }

    [Fact]
    public void Retired_archives_do_not_block_and_a_half_written_record_does()
    {
        using var f = new Fixture();
        string folder = f.Take();
        f.Record("radio-A", folder, ".acknowledged-" + Guid.NewGuid().ToString("N"));
        f.Record("radio-B", folder, ".unreadable-" + Guid.NewGuid().ToString("N"));
        Assert.True(f.Check(folder).Allowed);
        File.WriteAllText(new RadioRecoveryStore(f.Recovery).PathFor("radio-C") + ".pending", "{");
        Assert.Equal(TakeDeletionGuard.StoreUnreadable, f.Check(folder).Reason);
    }

    [Fact]
    public void An_unreadable_store_refuses_rather_than_reading_as_nothing_owed()
    {
        using var f = new Fixture();
        string folder = f.Take();
        Directory.CreateDirectory(f.Recovery);
        File.WriteAllText(new RadioRecoveryStore(f.Recovery).PathFor("radio-A"), "{ not json");
        Assert.Equal(TakeDeletionGuard.StoreUnreadable, f.Check(folder).Reason);

        using var g = new Fixture();
        string other = g.Take();
        g.Record("radio-A", other, ".put-back-" + Guid.NewGuid().ToString("N"));
        // The put-back file cannot be read (a folder where the file should be).
        Directory.CreateDirectory(Path.Combine(g.Root, "checkRestoreReminders.txt"));
        Assert.Equal(TakeDeletionGuard.StoreUnreadable, g.Check(other).Reason);
    }

    [Fact]
    public void Only_a_take_folder_directly_under_the_root_can_be_deleted_and_a_busy_one_cannot()
    {
        using var f = new Fixture();
        string folder = f.Take();
        Assert.Equal(TakeDeletionGuard.OutsideRoot, f.Check(f.Takes).Reason);
        Assert.Equal(TakeDeletionGuard.OutsideRoot, f.Check(Path.Combine(f.Takes, TakeCatalog.TestsFolder)).Reason);
        Assert.Equal(TakeDeletionGuard.OutsideRoot, f.Check(f.Root).Reason);
        Assert.Equal(TakeDeletionGuard.OutsideRoot, f.Check(Path.Combine(folder, "..", "..", "elsewhere")).Reason);
        Assert.Equal(TakeDeletionGuard.Missing, f.Check(Path.Combine(f.Takes, "20260101-000000-000-deadbeef")).Reason);
        Assert.Equal(TakeDeletionGuard.Busy, f.Check(folder, busy: d => d == Path.GetFullPath(folder)).Reason);
        Assert.True(f.Check(folder).Allowed);
    }
}
