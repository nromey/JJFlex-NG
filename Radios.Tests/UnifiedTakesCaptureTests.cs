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
public sealed class UnifiedTakesCaptureTests
{
    private const int Slice = 1;
    private const int Frames = 240; // ten milliseconds at 24 kHz

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

    private static ReceivedAudioPacket At(int index, double timestamp, bool muted = false) =>
        new(Packet(index), index & 15, timestamp, Stopwatch.GetTimestamp(), muted);

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

    [Fact]
    public async Task Audio_before_the_reported_start_is_left_out_and_the_reported_stop_closes_it_clean()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            for (int i = 0; i < 5; i++) capture.Offer(At(i, 100 + i * 0.01));
            capture.Observe(Edge(playing: true));
            for (int i = 5; i < 105; i++) capture.Offer(At(i, 100 + i * 0.01));
            capture.Observe(Edge(playing: false));
            capture.Offer(At(105, 100 + 105 * 0.01));
            Assert.True(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("clean", take.State);
            Assert.Equal("observed-stop", take.Get(AudioTakeFields.EndReason));
            Assert.Equal((100 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
            Assert.Equal(new TakeSpan(0, 100 * Frames), AudioTakeTimeline.PlaybackSpans(take, long.MaxValue).Single());
            Assert.Contains("\"PreStartPacketsDiscarded\":5", take.Get("capture-summary"));
            Assert.Equal(AudioTakeFields.Unverified, take.Get(AudioTakeFields.BoundaryQuality));
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
            capture.Offer(At(0, 1));
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
            for (int i = 0; i < 20; i++) capture.Offer(At(i, 5 + i * 0.01));
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.Equal("safety-cap", take.Get(AudioTakeFields.EndReason));
            Assert.True(take.CanPlay);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public async Task A_transport_gap_is_filled_recorded_and_makes_the_take_incomplete_and_stale_packets_are_dropped()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var (capture, directory) = Start(root);
            capture.Observe(Edge(playing: true));
            for (int i = 0; i < 10; i++) capture.Offer(At(i, 10 + i * 0.01));
            capture.Offer(At(3, 10.03));                        // stale: a duplicate
            for (int i = 15; i < 25; i++) capture.Offer(At(i, 10 + i * 0.01)); // five packets missing
            capture.Observe(Edge(playing: false));
            Assert.False(await Closed(capture));
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.True(take.CanPlay);
            Assert.Equal(new TakeSpan(10 * Frames, 15 * Frames), AudioTakeTimeline.Gaps(take).Single());
            Assert.Equal((25 * Frames).ToString(), take.Get(AudioTakeFields.SamplesWritten));
            Assert.Contains("\"StalePacketsDiscarded\":1", take.Get("capture-summary"));
            // The measurable stretch excludes the gap.
            Assert.Equal(2, AudioTakeTimeline.MeasurableSpans(take, 25 * Frames).Count);
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
            for (int i = 0; i < 10; i++) capture.Offer(At(i, 1 + i * 0.01));
            capture.Observe(Edge(playing: false, slice: Slice + 1));
            capture.ReceiveEnded();
            Assert.False(await Closed(capture));
            Assert.Equal("receive-ended", TakeReader.Inspect(directory).Get(AudioTakeFields.EndReason));
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
            for (int i = 0; i < 10; i++) capture.Offer(At(i, 1 + i * 0.01, muted: true));
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
            capture.Offer(At(0, 1));
            capture.Offer(At(1, 1 + PlaybackTakeCapture.LongestFillSeconds + 1));
            Assert.False(await Closed(capture));
            Assert.Equal("clock-discontinuity", TakeReader.Inspect(directory).Get(AudioTakeFields.EndReason));
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
            capture.Offer(At(0, 1));
            capture.Observe(Edge(playing: false));
            Assert.True(await Closed(capture));
            long length = new FileInfo(Path.Combine(directory, "take.txt")).Length;
            capture.End("late");
            capture.Offer(At(1, 1.01));
            capture.Dispose();
            await Task.Delay(100);
            Assert.Equal(length, new FileInfo(Path.Combine(directory, "take.txt")).Length);
            Assert.Equal(PlaybackCaptureState.Closed, capture.State);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }
}
