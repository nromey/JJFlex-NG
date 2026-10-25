#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class VoxPauseRigTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(VoxPauseRigTests));
    public void Dispose() => _scope.Dispose();

    [Fact]
    public void Two_production_lever_sets_share_the_rig_owner_and_own_writes_do_not_lift_a_hold()
    {
        using var f = new RigOnVendorRadio("vox-owner-test");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
        f.Vendor.Transport.Written.Clear();
        var changes = new List<string>();
        f.Rig.OperatorChangedRadioSetting += changes.Add;
        var first = ReflectedCutHold.LeversFor(f.Rig, null);
        var second = ReflectedCutHold.LeversFor(f.Rig, null);
        Assert.Same(first.Owner, second.Owner);
        var a = first.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        var b = second.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        Assert.False(a.Release());
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        Assert.True(b.Release());
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
        Assert.Empty(changes);
        Assert.Equal(new[] { "transmit set vox_enable=0", "transmit set vox_enable=1" }, f.Vendor.Transport.Commands);
    }

    [Fact]
    public void The_operator_setter_cancels_all_restores_but_an_app_scoped_write_does_not()
    {
        using var f = new RigOnVendorRadio("vox-operator-test");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        var a = f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        var b = f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        Drain(f.Rig);
        using (f.Rig.AppInitiatedSettingChanges()) f.Rig.Vox = FlexBase.OffOnValues.off;
        Drain(f.Rig);
        Assert.True(a.Paused);
        f.Rig.Vox = FlexBase.OffOnValues.off; // unchanged value is still the operator's choice
        Drain(f.Rig);
        Assert.False(a.Paused);
        Assert.False(b.Paused);
        a.Dispose(); b.Dispose();
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
    }

    [Fact]
    public void A_new_holder_before_queued_restore_runs_never_briefly_enables_vox()
    {
        using var f = new RigOnVendorRadio("vox-queue-test");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        var first = f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        Drain(f.Rig);
        f.Vendor.Transport.Written.Clear();
        Assert.True(first.Release()); // on is queued, but the radio is still off
        var next = f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        Assert.DoesNotContain("transmit set vox_enable=1", f.Vendor.Transport.Commands);
        Assert.True(next.Release());
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
        Assert.Equal(new[] { "transmit set vox_enable=1" }, f.Vendor.Transport.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tone_and_cut_hold_share_the_one_vox_pause_in_either_release_order(bool toneReleasesFirst)
    {
        using var f = new RigOnVendorRadio("tone-vox-overlap");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        Assert.True(await StartTone(f));
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        var hold = f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        f.Rig.Vox = FlexBase.OffOnValues.on;
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        if (toneReleasesFirst) f.Rig.TxToneStop();
        else hold.Dispose();
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        if (toneReleasesFirst) hold.Dispose();
        else f.Rig.TxToneStop();
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
        Assert.Equal(new[] { "transmit set vox_enable=0", "transmit set vox_enable=1" }, f.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Disconnect_releases_the_tone_lease_and_restores_before_transport_teardown()
    {
        using var f = new RigOnVendorRadio("tone-disconnect");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        Assert.True(await StartTone(f));
        Drain(f.Rig);
        var departingOwner = f.Rig.VoxPauses;
        typeof(FlexBase).GetMethod("DisconnectVoxPauses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Rig, null);
        Assert.False(f.Rig.TxToneEngaged);
        f.Rig.TxToneStop();
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
        Assert.Contains("transmit set vox_enable=1", f.Vendor.Transport.Commands);
        Assert.NotSame(departingOwner, f.Rig.VoxPauses);
    }

    [Fact]
    public async Task Tone_does_not_engage_before_the_radio_confirms_VOX_off()
    {
        using var f = new RigOnVendorRadio("tone-vox-reply");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        var arm = f.Rig.TxToneStartAsync();
        Drain(f.Rig);
        Assert.Contains("transmit set vox_enable=0", f.Vendor.Transport.Commands);
        Assert.False(f.Rig.TxToneEngaged);
        f.Vendor.ReplyOk(f.Vendor.LastSequence);
        Assert.True(await arm);
        Assert.True(f.Rig.TxToneEngaged);
        f.Rig.TxToneStop();
        Drain(f.Rig);
    }

    [Fact]
    public async Task Tone_refuses_if_the_radio_never_confirms_VOX_off()
    {
        using var f = new RigOnVendorRadio("tone-vox-no-reply");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Rig.SliceVoxOffWait = TimeSpan.FromMilliseconds(30);
        f.Vendor.Status("transmit vox_enable=1");
        var arm = f.Rig.TxToneStartAsync();
        Drain(f.Rig);
        Assert.False(await arm);
        Assert.False(f.Rig.TxToneEngaged);
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
    }

    [Fact]
    public async Task Reference_stays_armed_across_transmissions_without_rekeying_on_vox()
    {
        using var f = new RigOnVendorRadio("reference-vox-rekey");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=1");
        f.Rig.TxFilePlayer.Load(new float[48000 * 4], 48000, "reference");
        f.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "transmit set vox_enable=0")
                f.Vendor.ReplyOk(f.Vendor.LastSequence);
        };

        var arm = f.Rig.TxReferenceStartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!arm.IsCompleted && DateTime.UtcNow < deadline)
        {
            Drain(f.Rig);
            await Task.Delay(1);
        }
        Assert.True(await arm);
        Assert.True(f.Rig.TxFilePlaying);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        f.Rig.Vox = FlexBase.OffOnValues.on;
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        Assert.DoesNotContain("transmit set vox_enable=1", f.Vendor.Transport.Commands);
        // The fake radio would key if an engaged source met enabled VOX.
        Assert.False(f.Rig.TxFilePlaying && f.Vendor.Radio.SimpleVOXEnable);
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Rig, new object[] { true });
        f.Rig.Transmit = false;
        Assert.True(f.Rig.TxFilePlaying);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Rig, new object[] { false });
        Assert.True(f.Rig.TxFilePlaying);
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        Assert.False(f.Rig.TxFilePlaying && f.Vendor.Radio.SimpleVOXEnable);
        f.Rig.TxFileStop();
        await Task.Delay(250);
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
    }

    [Fact]
    public async Task Reference_refuses_to_arm_if_vox_off_is_not_confirmed()
    {
        using var f = new RigOnVendorRadio("reference-vox-no-reply");
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Rig.SliceVoxOffWait = TimeSpan.FromMilliseconds(30);
        f.Vendor.Status("transmit vox_enable=1");
        f.Rig.TxFilePlayer.Load(new float[48000], 48000, "reference");
        var arm = f.Rig.TxReferenceStartAsync();
        Drain(f.Rig);
        Assert.False(await arm);
        Assert.False(f.Rig.TxFilePlaying);
    }

    private static void Set(FlexBase rig, string name, object value)
        => typeof(FlexBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, value);

    private static async Task<bool> StartTone(RigOnVendorRadio fixture)
    {
        fixture.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "transmit set vox_enable=0")
                fixture.Vendor.ReplyOk(fixture.Vendor.LastSequence);
        };
        var start = fixture.Rig.TxToneStartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!start.IsCompleted && DateTime.UtcNow < deadline)
        {
            Drain(fixture.Rig);
            await Task.Delay(1);
        }
        return await start;
    }

    private static void Drain(FlexBase rig)
    {
        while (rig.q.Count > 0)
            if (rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
    }
}
