#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes part 4, the capture engine (design sections 4 and 5): a
/// playback take starts on the radio's REPORTED start and ends on its
/// reported stop, never a timer; the fail-safes close it incomplete; gaps
/// are filled and recorded; the result is read back by the real reader.
/// The decoder is a stand-in that unpacks 16-bit stereo, so no codec runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every packet here is shaped the way the radio sends it</b> (round three,
/// after the 2026-11-04 bench). Until then these tests fed timestamps that
/// advanced 0.01 a packet, which the radio never sends, and so pinned the
/// capture's wrong reading of them as correct: on the bench it kept about one
/// packet in four and padded every second with 0.849 s of silence.
/// </para>
/// <list type="bullet">
/// <item><description>The radio timestamp is FlexLib's key, whole UTC
/// seconds plus a fractional half that is NOT a fraction of a second: it
/// advances about 0.00147 a packet and starts again at each second
/// (measured; see <c>ReceiveContinuityMeter</c>).</description></item>
/// <item><description>The sequence is the wire's 4-bit counter.</description></item>
/// <item><description>The arrival time is set by the test, ten milliseconds a
/// packet unless the test says otherwise, so a stall, a burst or a loss can
/// be told apart the way the live receive loop presents them.</description></item>
/// </list>
/// </remarks>
public sealed class UnifiedTakesCaptureTests
{
    private const int Slice = 1;
    private const int Frames = 240; // ten milliseconds at 24 kHz
    /// <summary>A real UTC second from the bench, so the integer half looks as it does on the wire.</summary>
    private const long Utc = 1_793_812_685;
    /// <summary>Packet index 0 lands 37 packets into a second, so every run crosses a second boundary.</summary>
    private const int SlotOffset = 37;
    private static readonly long T0 = Stopwatch.GetTimestamp();

    private static float[] Decode(byte[] packet)
    {
        var stereo = new float[packet.Length / 2];
        for (int i = 0; i < stereo.Length; i++) stereo[i] = BitConverter.ToInt16(packet, i * 2) / 32767f;
        return stereo;
    }

    private static byte[] Packet(int index)
    {
        var bytes = new byte[Frames * 2 * 2];
        for (int i = 0; i < Frames; i++)
        {
            short value = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (index * Frames + i) / 24000.0));
            BitConverter.GetBytes(value).CopyTo(bytes, i * 4);
            BitConverter.GetBytes(value).CopyTo(bytes, i * 4 + 2);
        }
        return bytes;
    }

    /// <summary>
    /// The radio's key for packet <paramref name="index"/>: whole UTC seconds,
    /// plus 96/65536 a packet that starts again at each second.
    /// </summary>
    internal static double RadioKey(int index)
    {
        int slot = index + SlotOffset;
        return Utc + slot / 100 + (slot % 100) * 96 / 65536.0;
    }

    /// <summary>
    /// The radio's keys for a stream whose UTC seconds hold the given packet
    /// counts, in order. Over LAN the radio's seconds hold 99, 100 or 101
    /// packets (the 2026-11-04 evening bench: 213 of 306 seconds below that
    /// session's busiest of 101, with nothing lost), so a second's count jitters
    /// by one while the stream is whole.
    /// </summary>
    internal static double[] KeysForSeconds(params int[] perSecond)
    {
        var keys = new List<double>();
        for (int second = 0; second < perSecond.Length; second++)
            for (int i = 0; i < perSecond[second]; i++) keys.Add(Utc + second + i * 96 / 65536.0);
        return keys.ToArray();
    }

    /// <summary>The LAN shape: whole UTC seconds of 99, 100 and 101 packets, between two partial ones.</summary>
    internal static readonly int[] LanSeconds = { 63, 100, 101, 99, 100, 101, 100, 99, 101, 100, 41 };

    /// <summary>The wire's 4-bit counter for packet <paramref name="index"/>.</summary>
    internal static int Sequence(int index) => (index + SlotOffset) & 15;

    /// <summary>When the receive loop took packet <paramref name="index"/>: on time, plus any delay in milliseconds.</summary>
    internal static long Arrival(int index, double lateMs = 0) =>
        T0 + (long)Math.Round((index * 10.0 + lateMs) * Stopwatch.Frequency / 1000.0);

    private static ReceivedAudioPacket At(int index, long? arrival = null, bool muted = false) =>
        new(Packet(index), Sequence(index), RadioKey(index), arrival ?? Arrival(index), muted);

    private static SlicePlaybackObservation Edge(bool playing, int slice = Slice, string cause = "radio-status") =>
        new(slice, "B", playing, Stopwatch.GetTimestamp(), 0, cause, "connection");

    private static (PlaybackTakeCapture Capture, string Directory) Start(string root, TimeSpan? deadline = null, TimeSpan? cap = null)
    {
        var facts = UnifiedTakeFixtures.BaseFacts().Where(f => f.Value != null)
            .Select(f => new KeyValuePair<string, string>(f.Key, f.Value!));
        var writer = new AudioTakeWriter(AudioTakeWriter.NewDirectory(root), TakeKind.RadioRecording, facts);
        var capture = new PlaybackTakeCapture(writer, Slice, Decode, startDeadline: deadline, safetyCap: cap);
        capture.Arm();
        return (capture, writer.DirectoryPath);
    }

    private static async Task<bool> Closed(PlaybackTakeCapture capture)
    {
        var done = await Task.WhenAny(capture.Closed, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(done, capture.Closed), "the capture never closed");
        return await capture.Closed;
    }

    private static List<AudioGapRecord> GapRecords(TakeInspection take) => take.Fields
        .Where(f => f.Key == AudioTakeFields.AudioGap)
        .Select(f => System.Text.Json.JsonSerializer.Deserialize<AudioGapRecord>(f.Value)!).ToList();

    [Fact]
    public async Task Audio_before_the_reported_start_is_left_out_and_the_reported_stop_closes_it_clean()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            for (int i = 0; i < 5; i++) capture.Offer(At(i));
            capture.Observe(Edge(playing: true));
            // 250 packets: two and a half seconds, across three UTC second boundaries.
            for (int i = 5; i < 255; i++) capture.Offer(At(i));
            capture.Observe(Edge(playing: false));
            capture.Offer(At(255));
            Assert.True(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("clean", take.State);
            Assert.Equal("observed-stop", take.Get(AudioTakeFields.EndReason));
            Assert.Equal((250 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
            Assert.Empty(GapRecords(take));
            Assert.Equal(new TakeSpan(0, 250 * Frames), AudioTakeTimeline.PlaybackSpans(take, long.MaxValue).Single());
            Assert.Contains("\"PreStartPacketsDiscarded\":5", take.Get("capture-summary"));
            Assert.Contains("\"StalePacketsDiscarded\":0", take.Get("capture-summary"));
            Assert.Equal(AudioTakeFields.Unverified, take.Get(AudioTakeFields.BoundaryQuality));
            Assert.Equal("counter-and-arrival-estimate", take.Get("gap-clock"));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task No_reported_start_closes_the_take_incomplete_as_start_not_observed()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root, deadline: TimeSpan.FromMilliseconds(200));
            capture.Offer(At(0));
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.Equal("no-audio", take.Get(AudioTakeFields.EndReason));
            Assert.Contains("start-not-observed", take.Get("capture-summary"));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task No_reported_stop_is_ended_by_the_safety_cap_as_incomplete()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root, cap: TimeSpan.FromMilliseconds(300));
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 20; i++) capture.Offer(At(i));
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.Equal("safety-cap", take.Get(AudioTakeFields.EndReason));
            Assert.True(take.CanPlay);
            Assert.Equal((20 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_missing_run_of_packets_is_filled_where_it_was_and_makes_the_take_incomplete()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 10; i++) capture.Offer(At(i));
            for (int i = 15; i < 25; i++) capture.Offer(At(i)); // packets 10 to 14 never arrived
            capture.Observe(Edge(playing: false));
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.True(take.CanPlay);
            Assert.Equal(new TakeSpan(10 * Frames, 15 * Frames), AudioTakeTimeline.Gaps(take).Single());
            Assert.Equal("counter", GapRecords(take).Single().Reason);
            Assert.Equal((25 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
            // The measurable stretch excludes the gap.
            Assert.Equal(2, AudioTakeTimeline.MeasurableSpans(take, 25 * Frames).Count);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_missing_run_of_exactly_sixteen_packets_which_the_counter_cannot_see_is_found_by_arrival_time()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 20; i++) capture.Offer(At(i));
            for (int i = 36; i < 60; i++) capture.Offer(At(i)); // 20 to 35 lost: the 4-bit counter wraps exactly
            capture.Observe(Edge(playing: false));
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.Equal(new TakeSpan(20 * Frames, 36 * Frames), AudioTakeTimeline.Gaps(take).Single());
            Assert.Equal("counter-and-arrival-estimate", GapRecords(take).Single().Reason);
            Assert.Equal((60 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_late_or_repeated_packet_is_dropped_and_leaves_no_gap()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 10; i++) capture.Offer(At(i));
            capture.Offer(At(9, Arrival(9, 2)));  // repeated: the same radio time again
            capture.Offer(At(4, Arrival(9, 4)));  // late: older than what was already taken
            for (int i = 10; i < 20; i++) capture.Offer(At(i));
            capture.Observe(Edge(playing: false));
            Assert.True(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("clean", take.State);
            Assert.Empty(GapRecords(take));
            Assert.Equal((20 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
            Assert.Contains("\"StalePacketsDiscarded\":2", take.Get("capture-summary"));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_stalled_receive_loop_that_catches_up_loses_nothing_and_stays_clean()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 30; i++) capture.Offer(At(i));
            // The loop stalled for 190 ms, then took its 20-packet backlog in one
            // burst, ten microseconds apart, before packet 50 arrived on time.
            for (int i = 30; i < 50; i++) capture.Offer(At(i, Arrival(49) + (i - 30) * 100));
            for (int i = 50; i < 80; i++) capture.Offer(At(i));
            capture.Observe(Edge(playing: false));
            Assert.True(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("clean", take.State);
            Assert.Empty(GapRecords(take));
            Assert.Equal((80 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_loss_hidden_inside_a_stall_is_still_caught_by_counting_whole_seconds()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            // Four and a half seconds. Packets 200 to 231 are lost while the loop
            // is stalled (32 of them, so the counter reads contiguous), all inside
            // one UTC second, and the rest of the backlog arrives in a burst: the
            // shape of FlexLib's receive queue clearing itself under a stall.
            for (int i = 0; i < 200; i++) capture.Offer(At(i));
            for (int i = 232; i < 242; i++) capture.Offer(At(i, Arrival(241) + (i - 232) * 100));
            for (int i = 242; i < 450; i++) capture.Offer(At(i));
            capture.Observe(Edge(playing: false));
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.Empty(GapRecords(take));
            Assert.Equal("1", take.Get("unaccounted-short-seconds"));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_whole_stream_whose_seconds_hold_99_100_or_101_packets_closes_clean()
    {
        // Both LAN takes on the 2026-11-04 evening bench closed incomplete with
        // six "unaccounted" seconds while their audio was proven whole.
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            double[] keys = KeysForSeconds(LanSeconds);
            for (int i = 0; i < keys.Length; i++)
                capture.Offer(new ReceivedAudioPacket(Packet(i), Sequence(i), keys[i], Arrival(i), false));
            capture.Observe(Edge(playing: false));
            Assert.True(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("clean", take.State);
            Assert.Null(take.Get("unaccounted-short-seconds"));
            Assert.Equal((keys.Length * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task Receive_ending_closes_the_take_incomplete_and_another_slices_edges_are_ignored()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 10; i++) capture.Offer(At(i));
            capture.Observe(Edge(playing: false, slice: Slice + 1));
            capture.ReceiveEnded();
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("receive-ended", take.Get(AudioTakeFields.EndReason));
            Assert.Equal((10 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_locally_muted_packet_is_still_recorded_and_the_mute_is_said()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 10; i++) capture.Offer(At(i, muted: true));
            capture.Observe(Edge(playing: false));
            Assert.True(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("True", take.Get("local-mute-observed"));
            var (_, samples) = TakeAudio.ReadMono(Path.Combine(directory, "heard.wav"));
            Assert.True(samples.Max(Math.Abs) > 0.1f, "muted packets were recorded as silence");
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_jump_in_radio_time_too_long_to_fill_ends_the_take_as_a_discontinuity()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            capture.Offer(At(0));
            int later = (int)(PlaybackTakeCapture.LongestFillSeconds + 1) * 100;
            capture.Offer(At(later));
            Assert.False(await Closed(capture));
            Assert.Equal("clock-discontinuity", TakeReader.Inspect(directory).Get(AudioTakeFields.EndReason));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_radio_clock_that_steps_back_ends_the_take_as_a_discontinuity_rather_than_dropping_the_rest()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 300; i < 310; i++) capture.Offer(At(i, Arrival(i - 300)));
            capture.Offer(At(0, Arrival(10)));  // three seconds earlier than the packet before it
            capture.Observe(Edge(playing: false));
            Assert.False(await Closed(capture));
            Assert.Equal("clock-stepped-back", TakeReader.Inspect(directory).Get(AudioTakeFields.EndReason));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task Ending_a_capture_twice_or_after_it_closed_changes_nothing()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            capture.Offer(At(0));
            capture.Observe(Edge(playing: false));
            Assert.True(await Closed(capture));
            long length = new FileInfo(Path.Combine(directory, "take.txt")).Length;
            capture.End("late");
            capture.Offer(At(1));
            capture.Dispose();
            await Task.Delay(100);
            Assert.Equal(length, new FileInfo(Path.Combine(directory, "take.txt")).Length);
            Assert.Equal(PlaybackCaptureState.Closed, capture.State);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }
}
