#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
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

    private static void Set(FlexBase rig, string name, object value)
        => typeof(FlexBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, value);

    private static void Drain(FlexBase rig)
    {
        while (rig.q.Count > 0)
            if (rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
    }
}
