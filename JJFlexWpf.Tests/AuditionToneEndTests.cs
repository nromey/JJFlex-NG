#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JJPortaudio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The audition's file begins where its start tone REALLY ends (Sprint 49
/// Track Q3 repair 1; Codex's check of 83932446, C1), and a take stopped
/// before its file began is not blamed on the microphone (item 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Adopted from Codex's reproduction</b> (<c>Q3ReviewTimingTests</c>, in the
/// check's evidence): the REAL start tone, rendered offline, heard by a
/// simulated microphone after 90 ms and after 800 ms of output lag. On
/// 83932446 the fixed 450 ms cut lost about 200 ms of speech at 90 ms and
/// kept the tone at 800 ms. Here the end is found the way the window finds
/// it, through the REAL <see cref="EarconPlayer.ToneEnd"/>, the REAL
/// <see cref="ChainAuditionTake.FileStartAfterTone"/> and the REAL recorder
/// file start (<see cref="CaptureFileStart"/>), with a capture that starts
/// after the device did (a late first block), reports an input latency, and
/// arrives jittered.
/// </para>
/// <para>
/// <b>One change to Codex's case, stated:</b> his speech began on the very
/// first sample after the tone. The file now begins
/// <see cref="ChainAuditionTake.ToneMarginMs"/> after the tone's end, so such
/// speech loses that margin. A person answers a tone in no less than about
/// 200 ms (the repair brief), so the speech here begins 150 ms after the tone,
/// faster than that; none of it may be lost.
/// </para>
/// <para>
/// Windowless and silent: the tone is rendered offline or into a mixer no
/// device reads; the microphone is a float array; no file outside the temp
/// folder is written.
/// </para>
/// </remarks>
[Collection("Earcon player statics")]
public sealed class AuditionToneEndTests
{
    private readonly ITestOutputHelper _out;
    public AuditionToneEndTests(ITestOutputHelper output) { _out = output; }

    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    private const int CaptureRate = 48000;
    private const int Block = 1024;               // MicProbe's read
    private const double InputLatencySeconds = 0.030;
    private static readonly long Q = Stopwatch.Frequency;
    private static long Ticks(double ms) => (long)Math.Round(ms * Q / 1000.0);

    /// <summary>The real start tone, as the timed path sounds it, mono at the mixer's rate.</summary>
    private static float[] RealTone()
    {
        bool beeps = EarconPlayer.RecorderBeepsEnabled, master = EarconPlayer.EarconsEnabled;
        float[] stereo;
        try
        {
            EarconPlayer.RecorderBeepsEnabled = true;
            EarconPlayer.EarconsEnabled = true;
            stereo = EarconPlayer.RenderOffline(() => EarconPlayer.RecorderStartBeepTimed());
        }
        finally { EarconPlayer.RecorderBeepsEnabled = beeps; EarconPlayer.EarconsEnabled = master; }
        var tone = Enumerable.Range(0, stereo.Length / 2).Select(i => stereo[i * 2]).ToArray();
        Assert.True(tone.Any(x => Math.Abs(x) > 0.001f), "positive control: the tone rendered");
        // The length the timed tone reports is the length that sounds.
        Assert.Equal(EarconPlayer.RecorderStartBeepSamples().Length, tone.Length);
        return tone;
    }

    private sealed record Outcome(double ToneMs, double TrueEndMs, double FileStartsMs, long ToneKept, long SpeechLost, long FileFrames);

    /// <summary>
    /// One take, start to stop. Capture frame 0 is captured at <c>origin</c>;
    /// the tone is handed to the mixer 25 ms later, read by it 7 ms after that,
    /// and heard <paramref name="trueLagMs"/> after it was handed over. The
    /// output reports its position, or not (then the configured
    /// <paramref name="configuredLatencyMs"/> is assumed). Speech, a steady
    /// 0.125, begins <paramref name="reactionMs"/> after the tone is over.
    /// </summary>
    private Outcome Run(float[] tone, int trueLagMs, bool outputReportsPosition, int configuredLatencyMs, double reactionMs,
        double unseenMs = 0)
    {
        int mixRate = EarconPlayer.MixerSampleRate;
        long origin = Ticks(1_000_000);
        long handedOver = origin + Ticks(25);
        long mixRead = handedOver + Ticks(7);
        long queued = (long)Math.Round((trueLagMs - 7) * mixRate / 1000.0);

        var end = new EarconPlayer.ToneEnd(tone.Length, mixRate, configuredLatencyMs, handedOver,
            () => new SoundTiming.DeviceSnapshot(1_000_000 + queued, outputReportsPosition ? 1_000_000 : -1, 30, 3, mixRate));
        end.FirstRead(mixRead);
        Assert.True(end.AudibleEnd.IsCompleted);
        long fileStartsAt = ChainAuditionTake.FileStartAfterTone(end.AudibleEnd.Result!.Value);

        // What the microphone hears.
        // unseenMs: delay after the output's own buffers that its position cannot show.
        long audibleStart = handedOver + Ticks(trueLagMs + unseenMs);
        long trueEnd = audibleStart + (long)Math.Round((double)tone.Length * Q / mixRate);
        long speechFrom = trueEnd + Ticks(reactionMs);
        int frames = (int)((speechFrom + Ticks(1100) - origin) * CaptureRate / Q);
        var heard = new float[frames];
        var isTone = new bool[frames];
        var isSpeech = new bool[frames];
        for (int i = 0; i < frames; i++)
        {
            long t = origin + (long)((double)i * Q / CaptureRate);
            long j = (long)Math.Floor((double)(t - audibleStart) * mixRate / Q);
            if (j >= 0 && j < tone.Length)
            {
                heard[i] = tone[j];
                isTone[i] = Math.Abs(tone[j]) > 0.001f;
            }
            if (t >= speechFrom && t < speechFrom + Ticks(1000))
            {
                heard[i] = 0.125f;
                isSpeech[i] = true;
            }
        }

        // The recorder, block by block: a late first block (the sink is
        // installed after the device started), then jitter.
        int[] jitterMs = { 40, 0, 3, 1, 6, 2, 0, 4 };
        string path = Path.Combine(Path.GetTempPath(), "jjflex-q3-tone-end-" + Guid.NewGuid().ToString("N") + ".wav");
        var start = new CaptureFileStart(CaptureRate, hold: true, InputLatencySeconds, Q);
        long boundary;
        float[] written;
        try
        {
            using (var w = new WavWriter(path, CaptureRate, 1))
            {
                bool told = false;
                int k = 0;
                for (int at = 0; at < frames; at += Block, k++)
                {
                    int n = Math.Min(Block, frames - at);
                    long arrival = origin + (long)Math.Round((double)(at + n) * Q / CaptureRate)
                        + Ticks(InputLatencySeconds * 1000 + jitterMs[k % jitterMs.Length]);
                    // The take tells the recorder as the mixer reads the tone.
                    if (!told && arrival >= mixRead) { start.BeginAt(fileStartsAt); told = true; }
                    start.Block(w, heard.Skip(at).Take(n).ToArray(), n, n, arrival);
                }
                if (!told) start.BeginAt(fileStartsAt);
                start.Finish(w);
                Assert.True(start.FileBegan);
                boundary = start.BoundaryFrame;
                Assert.Equal(frames - boundary, w.Frames);
                w.Finish();
            }
            written = ReadMono(path);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }

        // What was written is what was heard from the boundary on.
        Assert.Equal(frames - boundary, written.Length);
        for (int i = 0; i < written.Length; i += 97)
            Assert.True(Math.Abs(written[i] - heard[boundary + i]) < 2e-4f, "the file is not the capture from its start");

        long toneKept = Enumerable.Range((int)boundary, (int)(frames - boundary)).Count(i => isTone[i]);
        long speechLost = Enumerable.Range(0, (int)boundary).Count(i => isSpeech[i]);
        var o = new Outcome(tone.Length * 1000.0 / mixRate, (trueEnd - origin) * 1000.0 / Q, boundary * 1000.0 / CaptureRate,
            toneKept, speechLost, frames - boundary);
        _out.WriteLine($"tone {o.ToneMs:F3} ms; true lag {trueLagMs} ms; position reported {outputReportsPosition}; configured {configuredLatencyMs} ms; "
            + $"tone over at {o.TrueEndMs:F3} ms; file starts {o.FileStartsMs:F3} ms; speech from {o.TrueEndMs + reactionMs:F3} ms; "
            + $"tone samples kept {o.ToneKept}; speech frames lost {o.SpeechLost}; file frames {o.FileFrames}");
        return o;
    }

    [Theory]
    [InlineData(90)]
    [InlineData(800)]
    public void A_take_keeps_all_speech_after_the_real_tone_and_no_part_of_the_tone(int outputLagMs)
    {
        AssertWindowless();
        float[] tone = RealTone();
        var o = Run(tone, outputLagMs, outputReportsPosition: true, configuredLatencyMs: outputLagMs, reactionMs: 150);
        Assert.Equal(0, o.ToneKept);
        Assert.Equal(0, o.SpeechLost);
        // And it begins where it should: the margin after the tone is over, to the millisecond.
        Assert.InRange(o.FileStartsMs, o.TrueEndMs + ChainAuditionTake.ToneMarginMs - 1, o.TrueEndMs + ChainAuditionTake.ToneMarginMs + 1);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(800)]
    public void The_margin_covers_delay_the_outputs_position_cannot_see(int outputLagMs)
    {
        // The Windows audio engine and driver after the alert output's own
        // buffers, and the room: here 40 ms the position does not show. The
        // margin keeps the tone out, and still costs none of the speech.
        AssertWindowless();
        float[] tone = RealTone();
        var o = Run(tone, outputLagMs, outputReportsPosition: true, configuredLatencyMs: outputLagMs, reactionMs: 150, unseenMs: 40);
        Assert.Equal(0, o.ToneKept);
        Assert.Equal(0, o.SpeechLost);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(800)]
    public void With_no_output_position_the_assumed_end_still_keeps_the_tone_out(int configuredMs)
    {
        // The assumed end is the latest the tone could be heard: the whole
        // chain after it was handed over. At that latest, nothing of it is kept.
        AssertWindowless();
        float[] tone = RealTone();
        var o = Run(tone, configuredMs, outputReportsPosition: false, configuredLatencyMs: configuredMs, reactionMs: 150);
        Assert.Equal(0, o.ToneKept);
    }

    [Fact]
    public void With_no_output_position_and_the_default_buffer_a_tone_heard_early_still_costs_no_speech()
    {
        // The default chain, 30 ms by 3: heard as early as it could be (one
        // buffer sooner), and answered at 200 ms, nothing of the speech is lost.
        AssertWindowless();
        float[] tone = RealTone();
        var o = Run(tone, trueLagMs: 60, outputReportsPosition: false, configuredLatencyMs: 90, reactionMs: 200);
        Assert.Equal(0, o.ToneKept);
        Assert.Equal(0, o.SpeechLost);
    }

    [Fact]
    public async Task The_timed_tone_reads_its_end_from_the_outputs_position_at_the_mixers_first_read()
    {
        AssertWindowless();
        int mixRate = EarconPlayer.MixerSampleRate;
        long queued = mixRate / 4;   // 250 ms ahead of it in the device
        await WithMixer(() => new SoundTiming.DeviceSnapshot(5000 + queued, 5000, 50, 4, mixRate), async mixer =>
        {
            var end = EarconPlayer.RecorderStartBeepTimed();
            Assert.Single(mixer.MixerInputs);
            Assert.False(end.IsCompleted, "the end was known before the output took the tone");
            long before = Stopwatch.GetTimestamp();
            mixer.Read(new float[2048].AsSpan());
            long after = Stopwatch.GetTimestamp();
            long? at = await end.WaitAsync(TimeSpan.FromSeconds(10));
            long span = (long)Math.Round((double)(queued + EarconPlayer.RecorderStartBeepSamples().Length) * Q / mixRate);
            Assert.NotNull(at);
            Assert.InRange(at!.Value, before + span - 1, after + span + 1);
        });
    }

    [Fact]
    public async Task The_timed_tone_assumes_the_whole_chain_when_the_output_will_not_say_its_position()
    {
        AssertWindowless();
        int mixRate = EarconPlayer.MixerSampleRate;
        await WithMixer(() => new SoundTiming.DeviceSnapshot(5000, -1, 50, 4, mixRate), async mixer =>
        {
            long before = Stopwatch.GetTimestamp();
            var end = EarconPlayer.RecorderStartBeepTimed();
            long after = Stopwatch.GetTimestamp();
            mixer.Read(new float[2048].AsSpan());
            long? at = await end.WaitAsync(TimeSpan.FromSeconds(10));
            long span = Ticks(EarconPlayer.AlertOutputLatencyMs)
                + (long)Math.Round((double)EarconPlayer.RecorderStartBeepSamples().Length * Q / mixRate);
            Assert.InRange(at!.Value, before + span - 1, after + span + 1);
        });
    }

    [Fact]
    public async Task No_tone_sounds_and_none_is_reported_with_the_recorder_beeps_or_every_sound_off()
    {
        AssertWindowless();
        await WithMixer(() => new SoundTiming.DeviceSnapshot(0, 0, 50, 4, EarconPlayer.MixerSampleRate), mixer =>
        {
            EarconPlayer.RecorderBeepsEnabled = false;
            var end = EarconPlayer.RecorderStartBeepTimed();
            Assert.True(end.IsCompleted);
            Assert.Null(end.Result);
            EarconPlayer.RecorderBeepsEnabled = true;
            EarconPlayer.EarconsEnabled = false;
            end = EarconPlayer.RecorderStartBeepTimed();
            Assert.Null(end.Result);
            Assert.Empty(mixer.MixerInputs);
            return Task.CompletedTask;
        });
    }

    // ── Item 3: stopped before the file began ─────────────────────────

    [Fact]
    public void A_take_stopped_before_its_file_began_is_too_short_not_a_microphone_fault()
    {
        AssertWindowless();
        // The recorder's side: held for the tone's end, stopped before it.
        string path = Path.Combine(Path.GetTempPath(), "jjflex-q3-early-" + Guid.NewGuid().ToString("N") + ".wav");
        var start = new CaptureFileStart(CaptureRate, hold: true, InputLatencySeconds, Q);
        try
        {
            using var w = new WavWriter(path, CaptureRate, 1);
            for (int k = 1; k <= 5; k++) start.Block(w, new float[Block], Block, Block, Ticks(k * 21.333 + 30));
            start.BeginAt(Ticks(600));
            Assert.False(start.Finish(w));
            Assert.False(start.FileBegan);
            Assert.Equal(0, w.Frames);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
        double captured = (double)start.CapturedFrames / CaptureRate;
        Assert.True(captured > 0, "positive control: the microphone delivered");

        // The narrator's choice: the check's own words, not "check the microphone".
        Assert.Equal(RecordingNarrator.StopSaying.StoppedTooSoon, RecordingNarrator.StopKind(false, false, captured, checkHasWords: true));
        // A microphone that delivered nothing still says so; the record key is unchanged.
        Assert.Equal(RecordingNarrator.StopSaying.NothingCaptured, RecordingNarrator.StopKind(false, false, 0, checkHasWords: true));
        Assert.Equal(RecordingNarrator.StopSaying.NothingCaptured, RecordingNarrator.StopKind(false, false, captured, checkHasWords: false));
        Assert.Equal(RecordingNarrator.StopSaying.Saved, RecordingNarrator.StopKind(false, true, captured, checkHasWords: true));
        Assert.Equal(RecordingNarrator.StopSaying.Faulted, RecordingNarrator.StopKind(true, false, captured, checkHasWords: true));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>A mixer standing in for the alert channel's (no device reads it), with the output's state stubbed; every switch restored.</summary>
    private static async Task WithMixer(Func<SoundTiming.DeviceSnapshot> device, Func<MixingSampleProvider, Task> body)
    {
        bool beeps = EarconPlayer.RecorderBeepsEnabled, master = EarconPlayer.EarconsEnabled;
        var savedDevice = SoundTiming.AlertDevice;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 2)) { ReadFully = true };
        try
        {
            EarconPlayer.RecorderBeepsEnabled = true;
            EarconPlayer.EarconsEnabled = true;
            EarconPlayer.TestAlertMixer = mixer;
            SoundTiming.AlertDevice = device;
            await body(mixer);
        }
        finally
        {
            EarconPlayer.TestAlertMixer = null;
            SoundTiming.AlertDevice = savedDevice;
            EarconPlayer.RecorderBeepsEnabled = beeps;
            EarconPlayer.EarconsEnabled = master;
        }
    }

    /// <summary>A 16-bit mono WAV from <see cref="WavWriter"/>, back to samples.</summary>
    private static float[] ReadMono(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int frames = (bytes.Length - 44) / 2;
        var x = new float[frames];
        for (int i = 0; i < frames; i++) x[i] = BitConverter.ToInt16(bytes, 44 + i * 2) / 32768f;
        return x;
    }
}
