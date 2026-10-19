using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radios.Listenback;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackLiveRecoveryTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackLiveRecoveryTests));
    public void Dispose() => _scope.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_restore_waits_for_status_and_reconnect_never_removes_a_reused_slice(bool reused)
    {
        using var fixture = new RigOnVendorRadio("listenback-restore-" + Guid.NewGuid().ToString("N"));
        fixture.Rig.q.MainLoop = true;
        var snapshot = new RadioRecoverySnapshot { Serial = fixture.Vendor.Radio.Serial, TakeDirectory = "old-take", ConnectionId = Guid.NewGuid() };
        snapshot.Settings.Add(new("full-duplex", "0", "radio set full_duplex_enabled=0", "radio", "full_duplex_enabled"));
        if (reused)
        {
            var other = fixture.AddOwnSlice(0, 7.1, "LSB");
            fixture.Vendor.Radio.SliceList.Add(other);
            snapshot.Settings.Add(new("created-slice", "removed", "slice remove 0", "slice 0", "", "slice", 0));
        }
        var store = new RadioRecoveryStore(Path.Combine(RadioConfig.AppDataRoot, "listenback-recovery"));
        store.Save(snapshot);
        fixture.Vendor.Transport.Written.Clear();
        Assert.False(await fixture.Rig.RestoreListenbackSnapshotAsync(snapshot, false));
        Assert.Empty(fixture.Vendor.Transport.Commands);
        var restore = fixture.Rig.RestoreListenbackSnapshotAsync(snapshot, true);
        Assert.True(fixture.Rig.q.Count > 0);
        while (fixture.Rig.q.Count > 0)
            if (fixture.Rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
        Assert.False(restore.IsCompleted);
        fixture.Vendor.ReplyOk(fixture.Vendor.LastSequence);
        Assert.False(restore.IsCompleted); // command success alone is insufficient
        fixture.Vendor.Status("radio full_duplex_enabled=0");
        Assert.Equal(!reused, await restore.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("slice remove 0", fixture.Vendor.Transport.Commands);
        Assert.Equal(reused, store.Find(snapshot.Serial) != null);
    }
}
