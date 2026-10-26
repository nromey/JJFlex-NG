#nullable enable
using System;
using System.Reflection;
using System.Threading.Tasks;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// A computer recording playing on the speakers holds VOX off through the
/// radio's one VOX owner (#787, Tape Deck track 4; the build condition from
/// the plain reading of the design, 2026-10-24).
/// </summary>
/// <remarks>
/// What these cannot see: a microphone hearing a speaker. They hold the
/// commands the radio is sent and the order of them, against a fake radio.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class LocalPlaybackVoxTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(LocalPlaybackVoxTests));
    public void Dispose() => _scope.Dispose();

    private static RigOnVendorRadio Radio(string name, bool voxOn)
    {
        var f = new RigOnVendorRadio(name);
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit vox_enable=" + (voxOn ? 1 : 0));
        return f;
    }

    private static async Task<T> Pumped<T>(RigOnVendorRadio f, Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Drain(f.Rig);
            await Task.Delay(1);
        }
        return await task;
    }

    [Fact]
    public async Task VOX_goes_off_and_is_confirmed_before_playback_and_comes_back_after_it()
    {
        using var f = Radio("local-play-vox", voxOn: true);
        f.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "transmit set vox_enable=0") f.Vendor.ReplyOk(f.Vendor.LastSequence);
        };
        var pause = await Pumped(f, f.Rig.PauseVoxForLocalPlaybackAsync());
        Assert.NotNull(pause);
        Assert.True(pause!.Holding);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);

        pause.Dispose();
        pause.Dispose(); // twice is harmless
        // The playback tail drains before VOX returns, and not at once.
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SimpleVOXEnable);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!f.Vendor.Radio.SimpleVOXEnable && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Drain(f.Rig);
        }
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
        Assert.Equal(new[] { "transmit set vox_enable=0", "transmit set vox_enable=1" }, f.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Playback_is_refused_when_the_radio_never_confirms_VOX_off()
    {
        using var f = Radio("local-play-vox-no-reply", voxOn: true);
        f.Rig.SliceVoxOffWait = TimeSpan.FromMilliseconds(30);
        var pause = await Pumped(f, f.Rig.PauseVoxForLocalPlaybackAsync());
        Assert.Null(pause);
        await Task.Delay(250);
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SimpleVOXEnable);
    }

    [Fact]
    public async Task With_VOX_already_off_nothing_is_written_and_the_pause_still_joins_the_owner()
    {
        using var f = Radio("local-play-vox-off", voxOn: false);
        f.Vendor.Transport.Written.Clear();
        var pause = await Pumped(f, f.Rig.PauseVoxForLocalPlaybackAsync());
        Assert.NotNull(pause);
        Assert.True(pause!.Holding); // a holder of the owner, so a later pause composes with it
        pause.Dispose();
        Drain(f.Rig);
        Assert.Empty(f.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task With_no_radio_there_is_nothing_to_pause()
    {
        using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var pause = await rig.PauseVoxForLocalPlaybackAsync();
        Assert.Same(FlexBase.LocalPlaybackVoxPause.NotNeeded, pause);
        Assert.False(pause!.Holding);
        pause.Dispose();
    }

    [Fact]
    public async Task Disconnect_during_local_playback_arms_the_reconnect_VOX_warning()
    {
        using var f = Radio("local-play-disconnect", voxOn: true);
        f.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "transmit set vox_enable=0") f.Vendor.ReplyOk(f.Vendor.LastSequence);
        };
        var pause = await Pumped(f, f.Rig.PauseVoxForLocalPlaybackAsync());
        Assert.NotNull(pause);
        Assert.True(pause!.Holding);

        typeof(FlexBase).GetMethod("DisconnectVoxPauses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Rig, null);
        Assert.Equal(f.Vendor.Radio.Serial,
            typeof(FlexBase).GetField("_voxPausedAtDisconnectSerial", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(f.Rig));
        pause.Dispose();
    }

    private static void Set(FlexBase rig, string name, object value)
        => typeof(FlexBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, value);

    private static void Drain(FlexBase rig)
    {
        while (rig.q.Count > 0)
            if (rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
    }
}
