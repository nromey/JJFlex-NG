#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;
using Sink = Radios.Tests.UnifiedTakesRunTests.Sink;

namespace Radios.Tests;

/// <summary>
/// #999, repair round 1: Play takes' slice rows and the slice recording backup
/// listen, they never send. <see cref="FlexBase.StartSliceListenAsync"/> refuses
/// while the radio transmits, and a key that starts after the press cannot turn
/// the playback into a send. Behaviour on a radio object over the vendor
/// fixture, with the radio really keyed in a transmission the app would
/// otherwise play into.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceListenTests : IDisposable
{
    private const string Serial = "1234-5678-8600-0993";
    private readonly RadioConfigStaticsScope _scope = new(nameof(SliceListenTests));
    private readonly string _root = UnifiedTakeFixtures.NewRoot();

    public void Dispose()
    {
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try { UnifiedTakeFixtures.Remove(_root); break; }
                catch (System.IO.IOException) when (attempt < 50) { System.Threading.Thread.Sleep(50); }
            }
        }
        finally { _scope.Dispose(); }
    }

    private static void Key(RadioCase c, bool keyed)
    {
        Field(c.F.Vendor.Radio, "_mox", keyed);
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(c.F.Rig, new object[] { keyed });
    }

    /// <summary>Slice 1 holds a recording, and this app owns a permitted transmission: the case where Play WOULD go to the air.</summary>
    private static RadioCase OurPermittedTransmissionWithARecording()
    {
        var c = new RadioCase(Serial);
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Rig.CanTransmit = true;
        c.F.Rig.SlicePlaybackTransmitAllowed = () => true;
        Property(c.F.Vendor.Radio, "TXClientHandle", c.F.Vendor.Radio.ClientHandle);
        c.F.Vendor.Status("slice 1 record_time=2.0 play=0");
        return c;
    }

    private static bool PlayCommandSent(RadioCase c) => c.F.Vendor.Transport.Commands.Contains("slice set 1 play=1");

    [Fact]
    public async Task Positive_control_ordinary_play_does_go_into_a_permitted_transmission()
    {
        using var c = OurPermittedTransmissionWithARecording();
        Key(c, true);
        var played = await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(played.Success, played.Message);
        Assert.True(PlayCommandSent(c));
    }

    [Fact]
    public async Task Listen_is_refused_while_the_radio_is_keyed_and_sends_nothing()
    {
        using var c = OurPermittedTransmissionWithARecording();
        Key(c, true);
        var refused = await c.Pump(c.F.Rig.StartSliceListenAsync(SliceRecorderTarget.ActiveSlice));
        Assert.False(refused.Success);
        Assert.Equal(Lexicon.Get("audio.recorder.listen_refused_transmitting", ("letter", "B")), refused.Message);
        Assert.False(PlayCommandSent(c));
    }

    [Fact]
    public async Task A_key_after_the_press_refuses_the_listen_at_the_radio_command()
    {
        using var c = OurPermittedTransmissionWithARecording();
        var pending = c.F.Rig.StartSliceListenAsync(SliceRecorderTarget.ActiveSlice);
        Key(c, true);   // before the queued command is written
        var outcome = await c.Pump(pending);
        Assert.False(outcome.Success);
        Assert.False(PlayCommandSent(c));
    }

    /// <summary>
    /// Repair round 2 (Codex's re-verification): FlexLib's InterlockState setter
    /// raises InterlockState BEFORE it updates Mox. A queued listen run in that
    /// gap saw Transmitting with Mox and Transmit still false, and wrote play=1.
    /// The app's queue is drained exactly there, at the interlock notification.
    /// </summary>
    [Fact]
    public async Task A_transmitting_interlock_published_before_Mox_refuses_the_queued_listen()
    {
        using var c = OurPermittedTransmissionWithARecording();
        var pending = c.F.Rig.StartSliceListenAsync(SliceRecorderTarget.ActiveSlice);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (c.F.Rig.q.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(c.F.Rig.q.Count > 0, "the play command was never queued, so the order below would prove nothing");
        bool drainedAtInterlock = false, moxStillFalse = false;
        System.ComponentModel.PropertyChangedEventHandler atInterlock = (_, e) =>
        {
            if (e.PropertyName != "InterlockState" || drainedAtInterlock) return;
            drainedAtInterlock = true;
            moxStillFalse = !c.F.Vendor.Radio.Mox;
            while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
        };
        c.F.Vendor.Radio.PropertyChanged += atInterlock;
        try { Property(c.F.Vendor.Radio, "InterlockState", Flex.Smoothlake.FlexLib.InterlockState.Transmitting); }
        finally { c.F.Vendor.Radio.PropertyChanged -= atInterlock; }
        var outcome = await c.Pump(pending);
        Assert.True(drainedAtInterlock);
        Assert.True(moxStillFalse, "the queue must run while Mox is still unpublished, or this tests the Mox path instead");
        Assert.False(outcome.Success);
        Assert.False(PlayCommandSent(c));
    }

    [Fact]
    public async Task A_tune_that_began_after_the_press_refuses_the_queued_listen()
    {
        using var c = OurPermittedTransmissionWithARecording();
        var pending = c.F.Rig.StartSliceListenAsync(SliceRecorderTarget.ActiveSlice);
        // As FlexBase.TxTune's setter notes a tune it asked for, ahead of the queued write.
        typeof(FlexBase).GetField("_tuneRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c.F.Rig, true);
        typeof(FlexBase).GetField("_tuneRequestedAtMs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c.F.Rig, Environment.TickCount64);
        typeof(FlexBase).GetField("_tuneOnQueued", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c.F.Rig, true);
        Assert.True(c.F.Rig.TuningNow);
        var outcome = await c.Pump(pending);
        Assert.False(outcome.Success);
        Assert.False(PlayCommandSent(c));
    }

    [Fact]
    public async Task Positive_control_an_ordinary_on_air_play_is_unchanged_by_the_interlock_check()
    {
        using var c = OurPermittedTransmissionWithARecording();
        Key(c, true);
        Property(c.F.Vendor.Radio, "InterlockState", Flex.Smoothlake.FlexLib.InterlockState.Transmitting);
        var played = await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(played.Success, played.Message);
        Assert.True(PlayCommandSent(c));
    }

    [Fact]
    public async Task Listen_plays_locally_when_the_radio_is_not_keyed()
    {
        using var c = OurPermittedTransmissionWithARecording();
        var played = await c.Pump(c.F.Rig.StartSliceListenAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(played.Success, played.Message);
        Assert.True(PlayCommandSent(c));
    }

    [Fact]
    public async Task Stopping_a_running_playback_is_allowed_while_the_radio_reports_transmit()
    {
        using var c = OurPermittedTransmissionWithARecording();
        Assert.True((await c.Pump(c.F.Rig.StartSliceListenAsync(SliceRecorderTarget.ActiveSlice))).Success);
        // The radio reports transmit; no edge has reached the app's own key state yet.
        Field(c.F.Vendor.Radio, "_mox", true);
        var stopped = await c.Pump(c.F.Rig.ToggleSliceListenAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(stopped.Success, stopped.Message);
        Assert.Contains("slice set 1 play=0", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task A_backup_keyed_while_it_arms_plays_nothing_to_the_air()
    {
        using var c = OurPermittedTransmissionWithARecording();
        var sink = new Sink();
        var backup = SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root, sink.Start);
        Key(c, true);   // after the backup's own receive check, while it arms
        var outcome = await c.Pump(backup);
        Assert.False(outcome.BackedUp);
        Assert.False(PlayCommandSent(c));
    }

    [Fact]
    public async Task A_backup_is_refused_outright_while_the_radio_is_keyed()
    {
        using var c = OurPermittedTransmissionWithARecording();
        Key(c, true);
        var outcome = await c.Pump(SliceRecordingBackup.RunAsync(c.F.Rig, SliceRecorderTarget.ActiveSlice, _root, new Sink().Start));
        Assert.Equal(SliceBackupResult.Transmitting, outcome.Result);
        Assert.False(PlayCommandSent(c));
    }
}
