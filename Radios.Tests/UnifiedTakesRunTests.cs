#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4d: one saved playback from recording
/// admission to its put-back, on a radio object over the vendor fixture,
/// with a stand-in decoder. Every exit closes the capture and puts the other
/// slices' mutes back; the take is written, reserved, listed and compared.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesRunTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(UnifiedTakesRunTests));
    private readonly string _root = UnifiedTakeFixtures.NewRoot();
    private readonly TakeSeriesContext _series = new();
    public void Dispose()
    {
        try
        {
            // A put-back's sidecar write can still be finishing in the background.
            for (int attempt = 0; ; attempt++)
            {
                try { UnifiedTakeFixtures.Remove(_root); break; }
                catch (IOException) when (attempt < 50) { System.Threading.Thread.Sleep(50); }
            }
        }
        finally { _scope.Dispose(); }
    }

    internal const int Frames = 240;

    internal static float[] Decode(byte[] packet)
    {
        var stereo = new float[packet.Length / 2];
        for (int i = 0; i < stereo.Length; i++) stereo[i] = BitConverter.ToInt16(packet, i * 2) / 32767f;
        return stereo;
    }

    internal static ReceivedAudioPacket At(int index, double seed = 1)
    {
        var bytes = new byte[Frames * 4];
        var speech = UnifiedTakeFixtures.Speechlike((int)(seed * 1000) + index, Frames, highGain: seed);
        for (int i = 0; i < Frames; i++)
        {
            short value = (short)(32767 * speech[i]);
            BitConverter.GetBytes(value).CopyTo(bytes, i * 4);
            BitConverter.GetBytes(value).CopyTo(bytes, i * 4 + 2);
        }
        return new(bytes, index & 15, 100 + index * 0.01, Stopwatch.GetTimestamp(), false);
    }

    internal static SlicePlaybackObservation Edge(int slice, bool playing) =>
        new(slice, "A", playing, Stopwatch.GetTimestamp(), 0, "radio-status", "connection");

    /// <summary>The stand-in for the recorder's second sink: the real capture token, fed by the test.</summary>
    internal sealed class Sink
    {
        public PlaybackTakeCapture? Capture;
        public TimeSpan? SafetyCap;
        public bool Refuse;
        public PlaybackTakeCapture Start(AudioTakeWriter writer, int slice)
        {
            if (Refuse) throw new InvalidOperationException("take-capture-running");
            Capture = new PlaybackTakeCapture(writer, slice, Decode, safetyCap: SafetyCap);
            Capture.Arm();
            return Capture;
        }
        /// <summary>The radio reports the playback, two seconds of it, and its end.</summary>
        public void Plays(int slice, int packets = 200, bool stop = true, double seed = 1)
        {
            Capture!.Observe(Edge(slice, true));
            for (int i = 0; i < packets; i++) Capture.Offer(At(i, seed));
            if (stop) Capture.Observe(Edge(slice, false));
        }
    }

    private static void Mox(FlexBase rig, bool on) =>
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, new object[] { on });

    private PlaybackTakeRun Admit(RadioCase c, string kind = TakeKind.RadioRecording, SliceRecorderTarget? target = null)
    {
        var run = PlaybackTakeRun.Admit(c.F.Rig, kind, target ?? SliceRecorderTarget.TransmitSlice, _series);
        run.NoteKeyed();
        return run;
    }

    private static async Task<T> Within<T>(RadioCase c, Task<T> task, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
            await Task.Delay(10);
        }
        Assert.True(task.IsCompleted, "did not finish in time");
        return await task;
    }

    private static string Header(string folder, string key) =>
        TakeHeader.Read(Path.Combine(folder, "take.txt")).LastOrDefault(f => f.Key == key).Value;

    private async Task<(RadioCase C, PlaybackTakeRun Run, Sink Sink)> Armed(RadioCase? existing = null)
    {
        var c = existing ?? UnifiedTakesMuteTests.ThreeSlices();
        var run = Admit(c);
        var sink = new Sink();
        Assert.True(await Within(c, run.ArmAsync(_root, sink.Start, muteOthers: true)), run.NotSavedReason);
        return (c, run, sink);
    }

    [Fact]
    public async Task A_saved_playback_is_written_reserved_listed_and_its_mutes_put_back_when_it_ends()
    {
        var (c, run, sink) = await Armed();
        using var _ = c;
        Assert.True(c.HeldMute[2]); // muted for the playback
        Assert.True(TakeObligations.IsOwed(run.DirectoryPath!));
        sink.Plays(1);
        Assert.True(await Within(c, run.Finished));
        Assert.True(await Within(c, run.Mutes.Restored));
        Assert.False(c.HeldMute[2]);
        Assert.True(c.HeldMute[3]);

        string folder = run.DirectoryPath!;
        Assert.Equal("isolated", Header(folder, "capture-isolation"));
        Assert.Equal(TakeSetup.Voice, Header(folder, "source"));
        Assert.Equal("clean", Header(folder, "closed"));
        string mutes = Header(folder, "other-slice-mutes");
        Assert.Contains("\"SliceIndex\":2", mutes);
        Assert.Contains("\"MutedForTake\":true", mutes);
        var entry = Assert.Single(TakeCatalog.List(_root));
        Assert.Equal(TakeKind.RadioRecording, entry.Kind);
        Assert.True(TakeReader.Inspect(folder).CanMeasure);
        var member = TakeSession.Membership(_root, folder)!.Value;
        Assert.Equal(1, member.Number);
        Assert.Equal(TakeReservation.Made, member.Test.Reservations.Single().State);
        await Task.Delay(100);
        Assert.Equal("done", TakeSession.Metadata(folder)!.MuteRestore);
        Assert.False(TakeObligations.IsOwed(folder));
    }

    [Fact]
    public async Task An_operator_stop_closes_it_on_the_reported_stop_and_puts_the_mutes_back()
    {
        var (c, run, sink) = await Armed();
        using var _ = c;
        sink.Plays(1, packets: 60, stop: false);
        run.StopRequested("operator-stop");
        sink.Capture!.Observe(Edge(1, false));
        Assert.True(await Within(c, run.Finished));
        Assert.True(await Within(c, run.Mutes.Restored));
        Assert.Contains("operator-stop", Header(run.DirectoryPath!, "stop-requested"));
        Assert.Equal("observed-stop", Header(run.DirectoryPath!, AudioTakeFields.EndReason));
    }

    [Fact]
    public async Task A_stop_the_radio_never_reports_closes_it_incomplete_and_still_puts_the_mutes_back()
    {
        var (c, run, sink) = await Armed();
        using var _ = c;
        sink.Plays(1, packets: 60, stop: false);
        run.StopRequested("window-closed");
        Assert.True(await Within(c, run.Finished, seconds: 8));
        Assert.True(await Within(c, run.Mutes.Restored));
        Assert.Equal("stop-not-confirmed", Header(run.DirectoryPath!, AudioTakeFields.EndReason));
        Assert.Equal("incomplete", Header(run.DirectoryPath!, "closed"));
        Assert.False(c.HeldMute[2]);
    }

    [Fact]
    public async Task A_radio_change_ends_it_incomplete_and_puts_the_mutes_back()
    {
        var (c, run, sink) = await Armed();
        using var _ = c;
        sink.Plays(1, packets: 60, stop: false);
        run.End("radio-changed");
        Assert.True(await Within(c, run.Finished));
        Assert.True(await Within(c, run.Mutes.Restored));
        Assert.Equal("radio-changed", Header(run.DirectoryPath!, AudioTakeFields.EndReason));
        Assert.False(c.HeldMute[2]);
    }

    [Fact]
    public async Task The_safety_cap_closes_it_and_puts_the_mutes_back()
    {
        var c = UnifiedTakesMuteTests.ThreeSlices();
        using var _ = c;
        var run = Admit(c);
        var sink = new Sink { SafetyCap = TimeSpan.FromMilliseconds(300) };
        Assert.True(await Within(c, run.ArmAsync(_root, sink.Start, muteOthers: true)));
        sink.Plays(1, packets: 20, stop: false);
        Assert.True(await Within(c, run.Finished, seconds: 8));
        Assert.True(await Within(c, run.Mutes.Restored));
        Assert.Equal("safety-cap", Header(run.DirectoryPath!, AudioTakeFields.EndReason));
        Assert.False(c.HeldMute[2]);
    }

    [Fact]
    public async Task A_capture_the_recorder_refuses_saves_nothing_and_puts_the_mutes_back_at_once()
    {
        var c = UnifiedTakesMuteTests.ThreeSlices();
        using var _ = c;
        var run = Admit(c);
        var sink = new Sink { Refuse = true };
        Assert.False(await Within(c, run.ArmAsync(_root, sink.Start, muteOthers: true)));
        Assert.Equal("capture-refused", run.NotSavedReason);
        Assert.True(await Within(c, run.Mutes.Restored));
        Assert.False(c.HeldMute[2]);
        var test = TakeSession.Tests(_root).Single();
        Assert.Equal(TakeReservation.Failed, test.Reservations.Single().State);
    }

    [Fact]
    public async Task Keyed_during_the_playback_the_take_is_marked_and_the_mutes_wait_for_receive()
    {
        var (c, run, sink) = await Armed();
        using var _ = c;
        sink.Plays(1, packets: 60, stop: false);
        await Task.Delay(200); // the worker has passed the start edge
        Mox(c.F.Rig, true);
        sink.Capture!.Observe(Edge(1, false));
        Assert.True(await Within(c, run.Finished));
        await Task.Delay(500);
        Assert.False(run.Mutes.Restored.IsCompleted);
        Assert.True(c.HeldMute[2]);
        Mox(c.F.Rig, false);
        Assert.True(await Within(c, run.Mutes.Restored));
        Assert.False(c.HeldMute[2]);
        Assert.Equal("contaminated", Header(run.DirectoryPath!, "capture-isolation"));
        Assert.Equal("True", Header(run.DirectoryPath!, "keyed-during-playback"));
    }

    [Fact]
    public async Task A_take_that_could_not_mute_the_other_slices_is_kept_but_never_compared()
    {
        var c = UnifiedTakesMuteTests.ThreeSlices();
        using var _ = c;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 2 audio_mute=1") { c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000016, ""); return; }
            c.Respond(command);
        };
        var run = Admit(c);
        var sink = new Sink();
        Assert.True(await Within(c, run.ArmAsync(_root, sink.Start, muteOthers: true)));
        Assert.False(run.Mutes.Isolated);
        sink.Plays(1);
        Assert.True(await Within(c, run.Finished));
        Assert.Equal("contaminated", Header(run.DirectoryPath!, "capture-isolation"));

        string clean = UnifiedTakeFixtures.AudioTake(_root, facts: new Dictionary<string, string?>
            { ["serial"] = c.F.Vendor.Radio.Serial, ["tx-antenna"] = "ANT1" });
        var difference = TakeAnalysis.CompareTakes(clean, run.DirectoryPath!);
        Assert.False(difference.Comparable);
        Assert.Equal("unlike-takes:contaminated", difference.Reason);
        Assert.Equal("One of these takes also caught other audio while it played, so they can't be compared fairly.",
            ListenbackResultText.AudioRoadSentence(difference));
    }

    [Fact]
    public async Task A_muted_playing_slice_is_recorded_so_the_details_can_say_so()
    {
        var c = UnifiedTakesMuteTests.ThreeSlices();
        using var _ = c;
        ListenbackRoundTwoTests.Field(c.Tx, "_mute", true); c.HeldMute[1] = true;
        var (__, run, sink) = await Armed(c);
        sink.Plays(1);
        Assert.True(await Within(c, run.Finished));
        Assert.Equal("True", Header(run.DirectoryPath!, "playback-slice-muted"));
    }

    [Fact]
    public async Task Two_saved_playbacks_join_one_test_numbered_in_order()
    {
        var (c, first, sink) = await Armed();
        using var _ = c;
        sink.Plays(1, seed: 1);
        Assert.True(await Within(c, first.Finished));
        Assert.True(await Within(c, first.Mutes.Restored));
        var second = Admit(c);
        var sink2 = new Sink();
        Assert.True(await Within(c, second.ArmAsync(_root, sink2.Start, muteOthers: true)));
        sink2.Plays(1, seed: 2);
        Assert.True(await Within(c, second.Finished));
        Assert.Equal(first.Test!.Id, second.Test!.Id);
        Assert.Equal(2, second.Number);
        var difference = TakeAnalysis.CompareTakes(first.DirectoryPath!, second.DirectoryPath!);
        Assert.True(difference.Comparable, difference.Reason);
        Assert.Equal("boundary", difference.LevelWithheld);
    }

    [Fact]
    public async Task A_looped_back_take_records_the_arrangements_mutes_and_mutes_nothing_again()
    {
        using var c = UnifiedTakesMuteTests.Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        UnifiedTakesMuteTests.RunQueue(c);
        var run = PlaybackTakeRun.Admit(c.F.Rig, TakeKind.LoopedBack, SliceRecorderTarget.LoopbackEars, _series);
        run.NoteKeyed();
        int mutesBefore = c.F.Vendor.Transport.Commands.Count(x => x.EndsWith("audio_mute=1", StringComparison.Ordinal));
        var sink = new Sink();
        Assert.True(await Within(c, run.ArmAsync(_root, sink.Start, muteOthers: false)), run.NotSavedReason);
        Assert.Equal(mutesBefore, c.F.Vendor.Transport.Commands.Count(x => x.EndsWith("audio_mute=1", StringComparison.Ordinal)));
        Assert.False(run.Mutes.Owes);
        sink.Plays(c.Ears!.Index);
        Assert.True(await Within(c, run.Finished));
        string folder = run.DirectoryPath!;
        Assert.Equal(TakeKind.LoopedBack, TakeCatalog.Read(folder).Kind);
        Assert.Equal("isolated", Header(folder, "capture-isolation"));
        string mutes = Header(folder, "other-slice-mutes");
        Assert.Contains("\"SliceIndex\":1", mutes);
        Assert.Contains("\"SliceIndex\":2", mutes);
        Assert.Equal("loopback", Header(folder, "check-method"));
        // The arrangement still puts its own mutes back, as it always has.
        var outcome = await c.Pump(c.F.Rig.EndLoopbackArrangementWithOutcomeAsync());
        Assert.Contains(LoopbackEndOutcome.Mutes, outcome.Restored);
        Assert.False(c.HeldMute[2]);
    }
}
