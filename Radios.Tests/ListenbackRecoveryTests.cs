using System;
using System.IO;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

public sealed class ListenbackRecoveryTests
{
    [Fact]
    public async Task Crash_snapshot_is_offered_without_writes_and_only_confirmed_restore_clears_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RadioRecoveryStore(root);
            Assert.Null(store.Find("radio"));
            var snapshot = new RadioRecoverySnapshot { Serial = "radio", TakeDirectory = "take" };
            snapshot.Settings.Add(new("full-duplex", "0", "radio set full_duplex_enabled=0", "radio", "full_duplex_enabled"));
            store.Save(snapshot);
            var reconnectedStore = new RadioRecoveryStore(root);
            var pending = reconnectedStore.Find("radio");
            Assert.NotNull(pending);
            int writes = 0;
            Assert.False(await reconnectedStore.RestoreAsync(pending, _ => { writes++; return Task.FromResult(true); }, false));
            Assert.Equal(0, writes);
            Assert.False(await reconnectedStore.RestoreAsync(pending, _ => Task.FromResult(false), true));
            Assert.NotNull(store.Find("radio"));
            Assert.True(await reconnectedStore.RestoreAsync(pending, _ => Task.FromResult(true), true));
            Assert.Null(store.Find("radio"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Unknown_created_resource_and_newer_snapshot_cannot_be_discarded()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RadioRecoveryStore(root);
            var old = new RadioRecoverySnapshot { Serial = "radio", TakeDirectory = "first", ResourceCreationPending = true };
            store.Save(old);
            Assert.False(await store.RestoreAsync(old, _ => Task.FromResult(true), true));
            store.Save(new() { Serial = "radio", TakeDirectory = "second" });
            Assert.False(await store.RestoreAsync(old, _ => throw new Exception("must not write"), true));
            Assert.Equal("second", store.Find("radio").TakeDirectory);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Restore_evidence_requires_the_matching_status_not_a_command_or_another_pan()
    {
        var setting = new SavedRadioSetting("pan", "0", "display pan set 0x4001 daxiq_channel=0", "display pan 0x4001", "daxiq_channel");
        Assert.False(ListenbackRestoreEvidence.Matches(setting, "C1|display pan 0x4001 daxiq_channel=0"));
        Assert.False(ListenbackRestoreEvidence.Matches(setting, "S0|display pan 0x4002 daxiq_channel=0"));
        Assert.False(ListenbackRestoreEvidence.Matches(setting, "S0|display pan 0x4001 daxiq_channel=1"));
        Assert.True(ListenbackRestoreEvidence.Matches(setting, "S0|display pan 0x00004001 daxiq_channel=0"));
    }
}
