#nullable enable
using System;
using System.Collections.Generic;
using Xunit;

namespace Radios.Tests;

public sealed class VoxPauseOwnerTests
{
    private sealed class Radio
    {
        public bool Vox = true;
        public bool Cw = true;
        public bool Connected = true;
        public readonly List<(FlexBase.VoxSetting Setting, bool On, bool Immediate)> Writes = new();
        public VoxPauseOwner Owner { get; }
        public Radio()
        {
            Owner = new VoxPauseOwner(s => s == FlexBase.VoxSetting.SimpleVox ? Vox : Cw,
                (s, on, immediate) =>
                {
                    if (!Connected) return false;
                    Writes.Add((s, on, immediate));
                    if (s == FlexBase.VoxSetting.SimpleVox) Vox = on; else Cw = on;
                    return true;
                });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_distinct_holders_restore_only_on_the_last_release(bool safetyFirst)
    {
        var r = new Radio();
        var safety = r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        var other = r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        Assert.False((safetyFirst ? safety : other).Release());
        Assert.False(r.Vox);
        Assert.True((safetyFirst ? other : safety).Release());
        Assert.True(r.Vox);
        Assert.Equal(2, r.Writes.Count);
        safety.Dispose(); other.Dispose();
        Assert.Equal(2, r.Writes.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Operator_choice_cancels_every_holders_restore_even_when_the_value_did_not_change(bool on)
    {
        var r = new Radio();
        var safety = r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        var other = r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        r.Owner.OperatorChanged(FlexBase.VoxSetting.SimpleVox);
        r.Vox = on;
        safety.Dispose(); other.Dispose();
        Assert.Equal(on, r.Vox);
        Assert.Single(r.Writes);
    }

    [Fact]
    public void Simple_vox_and_break_in_are_independent_and_initially_off_is_never_enabled()
    {
        var r = new Radio { Vox = false };
        var voice = r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        var cw = r.Owner.Acquire(FlexBase.VoxSetting.CwBreakIn)!;
        Assert.False(voice.Paused);
        Assert.True(cw.Paused);
        r.Owner.OperatorChanged(FlexBase.VoxSetting.SimpleVox);
        voice.Dispose(); cw.Dispose();
        Assert.False(r.Vox);
        Assert.True(r.Cw);
        Assert.All(r.Writes, w => Assert.Equal(FlexBase.VoxSetting.CwBreakIn, w.Setting));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disconnect_invalidates_all_leases_and_never_writes_after_reconnect(bool transportStillUp)
    {
        var r = new Radio();
        var safety = r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        var other = r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        r.Connected = transportStillUp;
        r.Owner.Disconnect();
        Assert.Equal(transportStillUp, r.Vox);
        Assert.Equal(transportStillUp ? 2 : 1, r.Writes.Count);
        if (transportStillUp) Assert.True(r.Writes[1].Immediate);
        r.Connected = true;
        int before = r.Writes.Count;
        safety.Dispose(); other.Dispose(); r.Owner.Disconnect();
        Assert.Equal(before, r.Writes.Count);
        Assert.Null(r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox));
    }

    [Fact]
    public void A_failed_off_write_does_not_create_a_restore()
    {
        var r = new Radio { Connected = false };
        Assert.Null(r.Owner.Acquire(FlexBase.VoxSetting.SimpleVox));
        r.Owner.Disconnect();
        Assert.Empty(r.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Operator_choice_invalidates_a_write_already_waiting_in_the_queue(bool restorePending)
    {
        bool on = true;
        var queued = new List<Action>();
        var writes = new List<bool>();
        var owner = new VoxPauseOwner(_ => on, (_, value, _) =>
        {
            on = value; writes.Add(value); return true;
        }, action => { queued.Add(action); return true; });
        var lease = owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        if (restorePending)
        {
            queued[0]();
            Assert.True(lease.Release());
        }
        owner.OperatorChanged(FlexBase.VoxSetting.SimpleVox);
        on = false;
        int before = writes.Count;
        foreach (var action in queued) action();
        lease.Dispose();
        Assert.False(on);
        Assert.Equal(before, writes.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disconnect_resolves_pending_writes_once_and_leaves_callbacks_inert(bool restorePending)
    {
        bool on = true;
        var queued = new List<Action>();
        var writes = new List<bool>();
        var owner = new VoxPauseOwner(_ => on, (_, value, _) =>
        {
            on = value; writes.Add(value); return true;
        }, action => { queued.Add(action); return true; });
        var lease = owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        queued[0]();
        if (restorePending) Assert.True(lease.Release());
        owner.Disconnect();
        Assert.True(on);
        foreach (var action in queued) action();
        lease.Dispose();
        Assert.Equal(new[] { false, true }, writes);
    }

    [Fact]
    public void Different_radios_do_not_share_holders_or_operator_choices()
    {
        var a = new Radio(); var b = new Radio();
        using var aPause = a.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        using var bPause = b.Owner.Acquire(FlexBase.VoxSetting.SimpleVox)!;
        a.Owner.OperatorChanged(FlexBase.VoxSetting.SimpleVox);
        Assert.True(bPause.Release());
        Assert.False(a.Vox);
        Assert.True(b.Vox);
    }
}
