#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
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
/// <b>Repair 2 (Codex's re-check of ca39306c):</b> his four reproductions are
/// adopted at the end of this class: a tone the mixer never takes must not
/// sound after the file began (C1a), a late first read with no position (C1a),
/// a capture reader behind through the lead (C1b, with his no-backlog control),
/// and a stop before the first block (item 3).
/// </para>
/// <para>
/// <b>Repair 3 (Codex's re-check of b6543ac1):</b> his final probes are
/// adopted with their positive controls, under "Repair 3" below: a partly
/// read tone that stalls must not resume into the file (C1a), and a thread
/// pause around the capture's backlog question must not establish a file
/// inside the tone (C1b).
/// </para>
/// <para>
/// <b>Repair 4 (Codex's re-check 3 of 46a481c7):</b> his final-mixer-read
/// probe and both no-stall controls are adopted under "Repair 4", with the
/// class behind it: a stall anywhere between the tone's last source read and
/// the output's handoff must not publish the end. The timed-tone tests now
/// read through the output's real handoff counter.
/// </para>
/// <para>
/// Windowless and silent: the tone is rendered offline or into a mixer no
/// device reads; the microphone is a float array; no file outside the temp
/// folder is written. The no-alert-mixer path is only exercised with every
/// sound rendered off, so no system beep can sound.
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
            () => new SoundTiming.DeviceSnapshot(1_000_000 + queued, outputReportsPosition ? 1_000_000 : -1, 30, 3, mixRate),
            clock: () => mixRead);
        // Repair 3: the end is settled by the read that emits the tone's last
        // sample; here the output takes the whole tone in that one read.
        Assert.Equal(tone.Length, new EarconPlayer.ToneEnd.Probe(new Samples(tone), end).Read(new float[tone.Length]));
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
                    // Repair 2: a block on time reports no backlog; a late one's
                    // backlog is not reported; the stream started 5 ms before frame 0.
                    int jitter = jitterMs[k % jitterMs.Length];
                    start.Block(w, heard.Skip(at).Take(n).ToArray(), n, n,
                        new CaptureBlockTiming(arrival, jitter == 0 ? 0 : -1, streamStartedAt: origin - Ticks(5),
                            backlogAnsweredAt: jitter == 0 ? arrival : null));
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
    public async Task The_timed_tone_reads_its_end_from_the_outputs_position_at_the_read_that_emits_its_last_sample()
    {
        AssertWindowless();
        int mixRate = EarconPlayer.MixerSampleRate;
        long queued = mixRate / 4;   // 250 ms ahead of it in the device
        await WithMixer(() => new SoundTiming.DeviceSnapshot(5000 + queued, 5000, 50, 4, mixRate), async mixer =>
        {
            var end = EarconPlayer.RecorderStartBeepTimed();
            Assert.Single(mixer.MixerInputs);
            Assert.False(end.IsCompleted, "the end was known before the output took the tone");
            var (before, after, _) = ReadTheToneThrough(mixer, end);
            long? at = await end.WaitAsync(TimeSpan.FromSeconds(10));
            // Repair 4: settled at the output's handoff, where the device's own
            // count already holds the last read, so nothing is added on top.
            long span = (long)Math.Round((double)queued * Q / mixRate);
            Assert.NotNull(at);
            Assert.InRange(at!.Value, before + span - 1, after + span + 1);
        });
    }

    [Fact]
    public async Task The_timed_tone_estimates_the_whole_chain_from_its_last_read_when_the_output_will_not_say_its_position()
    {
        // Repair 2: from a READ, never from the hand-over. Repair 3: from the
        // read that emits the last sample, never predicted at the first.
        AssertWindowless();
        int mixRate = EarconPlayer.MixerSampleRate;
        await WithMixer(() => new SoundTiming.DeviceSnapshot(5000, -1, 50, 4, mixRate), async mixer =>
        {
            var end = EarconPlayer.RecorderStartBeepTimed();
            var (before, after, lastFrames) = ReadTheToneThrough(mixer, end);
            long? at = await end.WaitAsync(TimeSpan.FromSeconds(10));
            long span = Ticks(EarconPlayer.AlertOutputLatencyMs) + (long)Math.Round((double)lastFrames * Q / mixRate);
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
            for (int k = 1; k <= 5; k++) start.Block(w, new float[Block], Block, Block, new CaptureBlockTiming(Ticks(k * 21.333 + 30), 0));
            start.BeginAt(Ticks(600));
            Assert.False(start.Finish(w));
            Assert.False(start.FileBegan);
            Assert.Equal(0, w.Frames);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
        double captured = (double)start.CapturedFrames / CaptureRate;
        Assert.True(captured > 0, "positive control: the microphone delivered");

        // The narrator's choice: the check's own words, not "check the microphone".
        Assert.Equal(RecordingNarrator.StopSaying.StoppedTooSoon,
            RecordingNarrator.StopKind(false, false, captured, checkHasWords: true, stoppedDuringLead: true));
        Assert.Equal(RecordingNarrator.StopSaying.StoppedTooSoon,
            RecordingNarrator.StopKind(false, false, captured, checkHasWords: true, stoppedDuringLead: false));
        // Repair 2 (item 3): a stop during the lead with nothing delivered yet is
        // still the operator's. A microphone that delivered nothing past the
        // file's start, or with no lead, is a capture fault and still says so;
        // the record key is unchanged.
        Assert.Equal(RecordingNarrator.StopSaying.StoppedTooSoon,
            RecordingNarrator.StopKind(false, false, 0, checkHasWords: true, stoppedDuringLead: true));
        Assert.Equal(RecordingNarrator.StopSaying.NothingCaptured,
            RecordingNarrator.StopKind(false, false, 0, checkHasWords: true, stoppedDuringLead: false));
        Assert.Equal(RecordingNarrator.StopSaying.NothingCaptured,
            RecordingNarrator.StopKind(false, false, captured, checkHasWords: false, stoppedDuringLead: true));
        Assert.Equal(RecordingNarrator.StopSaying.Saved,
            RecordingNarrator.StopKind(false, true, captured, checkHasWords: true, stoppedDuringLead: false));
        Assert.Equal(RecordingNarrator.StopSaying.Faulted,
            RecordingNarrator.StopKind(true, false, captured, checkHasWords: true, stoppedDuringLead: true));
    }

    // ── Repair 2: Codex's re-check of ca39306c, adopted ──────────────

    /// <summary>
    /// C1a (Codex's <c>A_tone_not_read_before_the_fallback_must_not_sound_after_file_start</c>):
    /// the REAL timed tone, into a mixer no device reads, with no position. On
    /// ca39306c the fallback published an assumed end and left the tone queued:
    /// the first read then emitted 4,060 tone samples 455.7 ms after the file
    /// had begun. Now the tone is withdrawn, the end is "no tone", so the file
    /// begins at its first frame, and the tone can never sound.
    /// </summary>
    [Fact]
    public async Task A_tone_the_mixer_never_took_is_withdrawn_and_never_sounds_after_the_file_began()
    {
        AssertWindowless();
        await WithMixer(() => default, async mixer =>
        {
            SoundTiming.AlertDevice = null;
            var result = EarconPlayer.RecorderStartBeepTimed();
            Assert.Single(mixer.MixerInputs);
            long? end = await result.WaitAsync(TimeSpan.FromSeconds(5));
            // Positive control: the watch has settled the start.
            Assert.True(result.IsCompleted);
            Assert.Null(end);   // no tone: the take begins its file at the first frame
            long released = Stopwatch.GetTimestamp();
            var data = new float[4096];
            int read = mixer.Read(data.AsSpan());
            int nonzero = data.Count(x => Math.Abs(x) > .001f);
            _out.WriteLine($"First mixer read {(Stopwatch.GetTimestamp() - released) * 1000.0 / Q:F1} ms after the start settled; {nonzero} tone samples emitted; inputs left {mixer.MixerInputs.Count()}");
            Assert.Equal(0, nonzero);
            Assert.Empty(mixer.MixerInputs);   // the mixer dropped it unheard
            // And a later read still emits nothing of it.
            Array.Clear(data);
            mixer.Read(data.AsSpan());
            Assert.Equal(0, data.Count(x => Math.Abs(x) > .001f));
        });
    }

    /// <summary>The withdrawal and the mixer's first read are one choice: whichever comes first wins, and the other does nothing.</summary>
    [Fact]
    public async Task Withdrawing_and_the_first_read_cannot_both_happen()
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        int frames = EarconPlayer.RecorderStartBeepSamples().Length;
        // Withdrawn first: the read plays nothing, and the end is "no tone".
        var a = new EarconPlayer.ToneEnd(frames, rate, 90, Ticks(1000), () => null);
        Assert.True(a.Withdraw());
        Assert.False(a.Take(Ticks(1100)));
        Assert.Null(await a.AudibleEnd);
        // Read first: the withdrawal does nothing. Repair 3: taking it settles
        // nothing; only the read of its last sample, or a cut, does.
        var b = new EarconPlayer.ToneEnd(frames, rate, 90, Ticks(1000), () => null, clock: () => Ticks(1300));
        Assert.True(b.Take(Ticks(1100)));
        Assert.False(b.Withdraw());
        Assert.True(b.Take(Ticks(1110)));   // later reads still play
        Assert.False(b.AudibleEnd.IsCompleted, "taking the tone predicted its end");
        Assert.True(b.CutNow());
        Assert.Equal(EarconPlayer.ToneEnd.Estimated(Ticks(1300), 90, 0, rate, Q), await b.AudibleEnd);
        Assert.False(b.Take(Ticks(1400)));  // cut: nothing more of it plays
        Assert.False(b.Withdraw());
        Assert.False(b.CutNow());
    }

    /// <summary>
    /// C1a (Codex's <c>No_position_and_a_late_first_read_must_not_put_the_boundary_before_the_tone</c>),
    /// unchanged: no position, a first read 400 ms after the hand-over and a
    /// 90 ms chain. On ca39306c the file started 310.023 ms after the hand-over,
    /// before the tone could even begin (it cannot end before 560.023 ms).
    /// </summary>
    [Fact]
    public void No_position_and_a_late_first_read_must_not_put_the_boundary_before_the_tone()
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        int frames = EarconPlayer.RecorderStartBeepSamples().Length;
        long handed = Ticks(1000), read = Ticks(1400);
        var end = new EarconPlayer.ToneEnd(frames, rate, 90, handed, () => null, clock: () => read);
        Assert.Equal(frames, new EarconPlayer.ToneEnd.Probe(new Samples(EarconPlayer.RecorderStartBeepSamples()), end).Read(new float[frames]));
        long begin = ChainAuditionTake.FileStartAfterTone(end.AudibleEnd.Result!.Value);
        long earliestEnd = read + (long)Math.Round((double)frames * Q / rate);
        _out.WriteLine($"File start {(begin - handed) * 1000.0 / Q:F3} ms after handoff; earliest tone end {(earliestEnd - handed) * 1000.0 / Q:F3} ms");
        Assert.True(begin >= earliestEnd, "the first read is late, but the end was counted from the earlier hand-over");
    }

    /// <summary>
    /// With no alert mixer and every sound rendered off, nothing can sound, so
    /// no tone is reported and no system beep is asked for. (With rendering on,
    /// the system beep's end is an ESTIMATE, <see cref="ChainAuditionTake.UnknownToneEndMs"/>
    /// after it was asked for; that branch makes a sound, so only its arithmetic is tested.)
    /// </summary>
    [Fact]
    public async Task With_no_alert_mixer_the_plain_beep_reports_no_tone_when_nothing_can_sound_and_an_estimate_otherwise()
    {
        AssertWindowless();
        OutputChannelRecorder.Configure(render: false, record: false);
        Assert.False(OutputChannelRecorder.RenderEnabled);
        bool beeps = EarconPlayer.RecorderBeepsEnabled, master = EarconPlayer.EarconsEnabled;
        try
        {
            EarconPlayer.RecorderBeepsEnabled = true;
            EarconPlayer.EarconsEnabled = true;
            EarconPlayer.TestAlertMixer = null;
            Assert.False(EarconPlayer.AlertChannelLive, "a test process has no alert output");
            long fallbacks = EarconPlayer.FallbackBeepsRequested;
            Assert.Null(await EarconPlayer.RecorderStartBeepTimed());
            Assert.Equal(fallbacks, EarconPlayer.FallbackBeepsRequested);
        }
        finally
        {
            EarconPlayer.RecorderBeepsEnabled = beeps;
            EarconPlayer.EarconsEnabled = master;
        }
        Assert.Equal(Ticks(5000) + Ticks(ChainAuditionTake.UnknownToneEndMs), EarconPlayer.ToneEnd.PlainBeepEstimate(Ticks(5000), Q));
    }

    /// <summary>
    /// C1b (Codex's <c>Capture_backlog_before_the_boundary_must_not_retain_the_real_tone</c>):
    /// the REAL rendered tone, the REAL <see cref="CaptureFileStart"/> and
    /// <see cref="WavWriter"/>, 48 kHz in 1024-frame reads, 30 ms of reported
    /// input latency, an exact tone end and the 60 ms margin. The reader is
    /// <paramref name="backlogMs"/> behind; on ca39306c a 120 ms backlog through
    /// the lead started the file at 215.042 ms and kept 2,137 tone samples (his
    /// no-backlog control: 335.042 ms, none kept).
    /// </summary>
    /// <remarks>
    /// Repair 2 gives each block what the device reports. The cases:
    /// <list type="bullet">
    /// <item>no backlog, reported (Codex's control);</item>
    /// <item>120 ms through the lead, REPORTED: the known backlog comes off each bound;</item>
    /// <item>120 ms through the lead, NOT reported, then caught up (Codex's model,
    /// with nothing said about the backlog): the file waits for a block that
    /// establishes the timing;</item>
    /// <item>120 ms throughout and never reported: the file begins by the
    /// stream's start, later, never earlier.</item>
    /// </list>
    /// Every case also checks that speech beginning 150 ms after the tone is
    /// kept whole, which a too-late boundary would lose.
    /// </remarks>
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(120, true, false)]
    [InlineData(120, false, false)]
    [InlineData(120, false, true)]
    public void Capture_backlog_before_the_boundary_must_not_retain_the_real_tone(int backlogMs, bool backlogReported, bool behindThroughout)
    {
        AssertWindowless();
        int rate = 48000, mixRate = EarconPlayer.MixerSampleRate;
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        Assert.True(tone.Any(x => Math.Abs(x) > .001f));
        long origin = Ticks(1000000), toneFrom = origin + Ticks(115);
        long toneEnd = toneFrom + (long)Math.Round((double)tone.Length * Q / mixRate);
        long speechFrom = toneEnd + Ticks(150);
        long begin = ChainAuditionTake.FileStartAfterTone(toneEnd);
        var start = new CaptureFileStart(rate, true, .030, Q);
        start.BeginAt(begin);
        string path = Path.Combine(Path.GetTempPath(), "q3-recheck-" + Guid.NewGuid().ToString("N") + ".wav");
        var heard = new float[rate * 2];
        var isSpeech = new bool[heard.Length];
        for (int i = 0; i < heard.Length; i++)
        {
            long when = origin + (long)Math.Round((double)i * Q / rate);
            long j = (long)Math.Floor((double)(when - toneFrom) * mixRate / Q);
            if (j >= 0 && j < tone.Length) heard[i] = tone[j];
            if (when >= speechFrom) { heard[i] = 0.125f; isSpeech[i] = true; }
        }
        try
        {
            using (var writer = new WavWriter(path, rate, 1))
            {
                long previousArrival = 0;
                for (int i = 0; i < heard.Length; i += 1024)
                {
                    int n = Math.Min(1024, heard.Length - i);
                    // The reader is behind during the lead (or throughout), then catches up.
                    int lag = behindThroughout || i < rate / 2 ? backlogMs : 0;
                    long arrival = origin + (long)Math.Round((double)(i + n) * Q / rate) + Ticks(30 + lag);
                    arrival = Math.Max(arrival, previousArrival + Ticks(.1));
                    previousArrival = arrival;
                    // What the device would report: every frame captured and through
                    // its input latency by the arrival, less what has been read. Not
                    // said during the lead unless the case says so; said once caught up.
                    long waiting = Math.Max(0, (long)Math.Floor((double)(arrival - Ticks(30) - origin) * rate / Q) - (i + n));
                    bool reported = backlogReported || (!behindThroughout && i >= rate / 2);
                    long backlog = reported ? waiting : -1;
                    // The stream started 10 ms before frame 0 was captured.
                    start.Block(writer, heard.Skip(i).Take(n).ToArray(), n, n,
                        new CaptureBlockTiming(arrival, backlog, streamStartedAt: origin - Ticks(10),
                            backlogAnsweredAt: backlog >= 0 ? arrival : null));
                }
                start.Finish(writer);
            }
            Assert.True(start.FileBegan);
            int kept = heard.Skip((int)start.BoundaryFrame).Count(x => Math.Abs(x) > .001f && x != 0.125f);
            long lost = Enumerable.Range(0, (int)start.BoundaryFrame).Count(i => isSpeech[i]);
            _out.WriteLine($"Backlog {backlogMs} ms, reported {backlogReported}, throughout {behindThroughout}: boundary {start.BoundaryFrame * 1000.0 / rate:F3} ms "
                + $"({start.HowPlaced}); tone samples kept {kept}; speech frames lost {lost}");
            Assert.Equal(0, kept);
            Assert.Equal(0, lost);
            Assert.Equal(!behindThroughout, start.TimingEstablished);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Item 3 (Codex's <c>A_check_stopped_during_the_lead_before_its_first_block_is_still_too_short</c>):
    /// the microphone reports itself open before its first 1024-frame read, so
    /// a Stop pressed at once can come with no block delivered. On ca39306c
    /// that said "nothing was captured, check the microphone". The recorder
    /// now knows the stop came during the lead, and the narrator says the
    /// check's too-short words.
    /// </summary>
    [Fact]
    public void A_check_stopped_during_the_lead_before_its_first_block_is_still_too_short()
    {
        AssertWindowless();
        var start = new CaptureFileStart(CaptureRate, hold: true, InputLatencySeconds, Q);
        // No block delivered, and the tone's end not known yet.
        Assert.True(start.StoppedDuringLead(Ticks(10)));
        // The tone's end is known and the stop came before the file's start.
        start.BeginAt(Ticks(400));
        Assert.True(start.StoppedDuringLead(Ticks(300)));
        Assert.Equal(0, start.CapturedFrames);
        Assert.Equal(RecordingNarrator.StopSaying.StoppedTooSoon,
            RecordingNarrator.StopKind(false, false, 0, checkHasWords: true, stoppedDuringLead: start.StoppedDuringLead(Ticks(300))));
        // A stop after the file's start with nothing ever delivered is a microphone that delivered nothing.
        Assert.False(start.StoppedDuringLead(Ticks(500)));
        Assert.Equal(RecordingNarrator.StopSaying.NothingCaptured,
            RecordingNarrator.StopKind(false, false, 0, checkHasWords: true, stoppedDuringLead: start.StoppedDuringLead(Ticks(500))));
        // No lead at all (no tone, or the record key): never "during the lead".
        var noTone = new CaptureFileStart(CaptureRate, hold: true, InputLatencySeconds, Q);
        noTone.BeginAt(null);
        Assert.False(noTone.StoppedDuringLead(Ticks(10)));
        Assert.False(new CaptureFileStart(CaptureRate, hold: false, InputLatencySeconds, Q).StoppedDuringLead(Ticks(10)));
    }

    // ── Repair 3: Codex's re-check of b6543ac1, adopted ──────────────

    /// <summary>
    /// Codex's positive control (<c>An_uninterrupted_tone_is_exhausted_before_its_predicted_boundary</c>):
    /// a tone read whole in one read is settled by that read, its boundary is
    /// still ahead, and nothing of it is left to play.
    /// </summary>
    [Fact]
    public async Task An_uninterrupted_tone_is_exhausted_before_its_boundary()
    {
        AssertWindowless();
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        var end = new EarconPlayer.ToneEnd(tone.Length, EarconPlayer.MixerSampleRate, 90, Stopwatch.GetTimestamp(), () => null);
        var probe = new EarconPlayer.ToneEnd.Probe(new Samples(tone), end);
        var output = new float[tone.Length];
        Assert.Equal(tone.Length, probe.Read(output));
        Assert.True(output.Any(x => Math.Abs(x) > .001f));
        long boundary = ChainAuditionTake.FileStartAfterTone((await end.AudibleEnd)!.Value);
        Assert.True(Stopwatch.GetTimestamp() < boundary);
        Assert.Equal(0, probe.Read(output));
    }

    /// <summary>
    /// C1a (Codex's <c>A_partly_read_tone_must_not_resume_into_the_file_after_a_stall</c>):
    /// the REAL timed tone and the REAL mixer read 10 ms of the cue, then no
    /// reads until after the boundary. On b6543ac1 the end was predicted at
    /// that first read, and the recovering mixer emitted 8,714 tone samples
    /// about 125 ms after the boundary, with a position report and without.
    /// Now nothing settles the end until the tone's last sample or its cut;
    /// the stalled tone is cut, and the late read emits none of it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_partly_read_tone_must_not_resume_into_the_file_after_a_stall(bool reportsPosition)
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { ReadFully = true };
        bool beeps = EarconPlayer.RecorderBeepsEnabled, master = EarconPlayer.EarconsEnabled;
        var savedDevice = SoundTiming.AlertDevice;
        try
        {
            EarconPlayer.RecorderBeepsEnabled = true;
            EarconPlayer.EarconsEnabled = true;
            EarconPlayer.TestAlertMixer = mixer;
            var output = new EarconPlayer.CountingSampleProvider(mixer);
            EarconPlayer.TestAlertCounter = output;   // repair 4: read through the output's handoff
            SoundTiming.AlertDevice = reportsPosition ? () => new SoundTiming.DeviceSnapshot(0, 0, 30, 3, rate) : null;
            var end = EarconPlayer.RecorderStartBeepTimed();
            var first = new float[(rate / 100) * 2];   // 10 ms of the actual 160 ms cue
            output.Read(first);
            Assert.True(first.Any(x => Math.Abs(x) > .001f), "positive control: the first part played");
            Assert.False(end.IsCompleted, "the end was predicted at the first read");
            long boundary = ChainAuditionTake.FileStartAfterTone((await end.WaitAsync(TimeSpan.FromSeconds(10)))!.Value);
            int wait = (int)Math.Max(1, (boundary - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency + 120);
            await Task.Delay(wait);   // no output reads during a device or thread stall
            long resumed = Stopwatch.GetTimestamp();
            Assert.True(resumed > boundary);
            var rest = new float[EarconPlayer.RecorderStartBeepSamples().Length * 2];
            mixer.Read(rest);
            int audible = rest.Count(x => Math.Abs(x) > .001f);
            _out.WriteLine($"Position={reportsPosition}; real mixer resumed {(resumed - boundary) * 1000.0 / Stopwatch.Frequency:F1} ms after boundary; late stereo tone samples={audible}");
            Assert.Equal(0, audible);
        }
        finally
        {
            EarconPlayer.TestAlertMixer = null;
            EarconPlayer.TestAlertCounter = null;
            SoundTiming.AlertDevice = savedDevice;
            EarconPlayer.RecorderBeepsEnabled = beeps;
            EarconPlayer.EarconsEnabled = master;
        }
    }

    /// <summary>
    /// The cut and a read are one choice: a cut waits for a read already in
    /// flight, and its end counts from after that read's samples were handed
    /// out, never from before; after it, nothing more of the tone plays.
    /// </summary>
    [Fact]
    public async Task A_cut_waits_for_a_read_in_flight_and_counts_its_end_from_after_that_read()
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        int piece = rate / 100;
        using var source = new GatedSamples(tone);
        var end = new EarconPlayer.ToneEnd(tone.Length, rate, 90, Stopwatch.GetTimestamp(), () => null);
        var probe = new EarconPlayer.ToneEnd.Probe(source, end);
        Assert.Equal(piece, probe.Read(new float[piece]));   // taken, not finished
        source.GateNext = true;
        var reader = Task.Run(() => probe.Read(new float[piece]));
        Assert.True(source.InRead.Wait(5000), "positive control: the read is in flight");
        var cutter = Task.Run(() => end.CutNow());
        await Task.Delay(100);
        Assert.False(cutter.IsCompleted, "the cut did not wait for the read in flight");
        long released = Stopwatch.GetTimestamp();
        source.Release.Set();
        Assert.Equal(piece, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await cutter.WaitAsync(TimeSpan.FromSeconds(5)));
        long at = (await end.AudibleEnd)!.Value;
        long least = released + Ticks(90) + (long)Math.Round((double)piece * Q / rate);
        _out.WriteLine($"Cut's end {(at - released) * 1000.0 / Q:F1} ms after the read in flight was released; at least {(least - released) * 1000.0 / Q:F1}");
        Assert.True(at >= least, "the cut's end counts from before the read in flight handed out its samples");
        var after = new float[piece];
        Assert.Equal(0, probe.Read(after));
        Assert.Equal(0, after.Count(x => x != 0));
    }

    /// <summary>
    /// A taken tone is cut only once the bound after its FIRST READ has
    /// passed (not the hand-over's, which a late read could already have
    /// passed), and the cut's end counts from the cut, plus the chain, plus
    /// the frames of the read before it.
    /// </summary>
    [Fact]
    public async Task A_taken_tone_is_cut_only_once_its_bound_after_the_first_read_has_passed()
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        int piece = rate / 100;
        long now = Ticks(1000);
        var end = new EarconPlayer.ToneEnd(tone.Length, rate, 90, Ticks(1000), () => null, clock: () => now);
        var probe = new EarconPlayer.ToneEnd.Probe(new Samples(tone), end);
        long boundMs = 90 + (long)tone.Length * 1000 / rate + EarconPlayer.ToneEnd.NoReadGraceMs;
        now = Ticks(1400);
        Assert.Equal(piece, probe.Read(new float[piece]));   // first read, 400 ms late
        Assert.False(end.AudibleEnd.IsCompleted);
        now = Ticks(1000 + boundMs) + 1;   // the hand-over's bound has passed; the read's has not
        end.Expire();
        Assert.False(end.IsCut);
        Assert.False(end.AudibleEnd.IsCompleted);
        now = Ticks(1400 + boundMs) + 1;
        end.Expire();
        Assert.True(end.IsCut);
        Assert.Equal(EarconPlayer.ToneEnd.Estimated(now, 90, piece, rate, Q), await end.AudibleEnd);
        Assert.Equal(0, probe.Read(new float[piece]));
    }

    /// <summary>
    /// The end reads the output's position FIRST and the clock AFTER it. The
    /// device here plays in real time, and every clock reading is followed by
    /// a 200 ms pause: the end must still be no earlier than the moment the
    /// tone's last sample is really heard.
    /// </summary>
    [Fact]
    public async Task The_end_reads_the_outputs_position_before_the_clock_so_a_pause_between_them_only_makes_it_later()
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        long now = Ticks(2000), playingFrom = Ticks(1000), produced = 2L * rate;
        long Clock() { long t = now; now += Ticks(200); return t; }
        SoundTiming.DeviceSnapshot? Device() => new SoundTiming.DeviceSnapshot(produced, (now - playingFrom) * rate / Q, 30, 3, rate);
        var end = new EarconPlayer.ToneEnd(tone.Length, rate, 90, Ticks(1000), Device, clock: Clock);
        Assert.Equal(tone.Length, new EarconPlayer.ToneEnd.Probe(new Samples(tone), end).Read(new float[tone.Length]));
        // Everything the device holds is heard by playingFrom + produced; the tone's frames follow.
        long heard = playingFrom + (long)Math.Round((double)(produced + tone.Length) * Q / rate);
        long at = (await end.AudibleEnd)!.Value;
        _out.WriteLine($"End {(at - heard) * 1000.0 / Q:F1} ms from when the tone's last sample is heard");
        Assert.True(at >= heard - 1, "the end was put before the tone's last sample is heard");
    }

    /// <summary>
    /// Codex's control (<c>A_wholly_withdrawn_tone_is_silent_and_continuing_from_frame_zero_is_safe</c>):
    /// a tone withdrawn before the mixer took it is silent, so continuing the
    /// take from frame zero, with no new words, is safe.
    /// </summary>
    [Fact]
    public async Task A_wholly_withdrawn_tone_is_silent_and_continuing_from_frame_zero_is_safe()
    {
        AssertWindowless();
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        var end = new EarconPlayer.ToneEnd(tone.Length, EarconPlayer.MixerSampleRate, 90, 0, () => null);
        var probe = new EarconPlayer.ToneEnd.Probe(new Samples(tone), end);
        Assert.True(end.Withdraw());
        Assert.Null(await end.AudibleEnd);
        Assert.Equal(0, probe.Read(new float[tone.Length]));
        string path = Path.Combine(Path.GetTempPath(), "q3r3-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using var w = new WavWriter(path, 1000, 1);
            var start = new CaptureFileStart(1000, true, 0, Q);
            start.Block(w, new float[100], 100, 100, new CaptureBlockTiming(Ticks(200), -1, streamStartedAt: Ticks(0)));
            start.BeginAt(null);
            start.Finish(w);
            Assert.Equal(0, start.BoundaryFrame);
            Assert.Equal(100, w.Frames);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Codex's controls (<c>A_capture_behind_from_its_first_block_and_never_caught_up_cannot_start_early</c>):
    /// a reader behind from its first block that never catches up, its backlog
    /// unreported or larger than a block, never establishes; the stream's
    /// start places the file at or after the true frame 337.
    /// </summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(120, 0)]
    [InlineData(-1, 100)]
    public void A_capture_behind_from_its_first_block_and_never_caught_up_cannot_start_early(int reported, int startupMs)
    {
        AssertWindowless();
        string path = Path.Combine(Path.GetTempPath(), "q3r3-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using var w = new WavWriter(path, 1000, 1);
            var start = new CaptureFileStart(1000, true, 0, Q);
            long origin = Ticks(10000), moment = origin + Ticks(337);
            start.BeginAt(moment);
            for (int at = 0; at < 2000; at += 100)
            {
                long arrival = origin + Ticks(at + 100 + 120);
                start.Block(w, new float[100], 100, 100, new CaptureBlockTiming(arrival, reported,
                    streamStartedAt: origin - Ticks(startupMs), backlogAnsweredAt: reported >= 0 ? arrival : null));
            }
            start.Finish(w);
            Assert.False(start.TimingEstablished);
            Assert.True(start.FileBegan);
            _out.WriteLine($"backlog report={reported}; stream start {startupMs} ms early; boundary={start.BoundaryFrame}; true boundary=337; frames={w.Frames}");
            Assert.True(start.BoundaryFrame >= 337);
            Assert.Equal(337 + startupMs, start.BoundaryFrame);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// C1b (Codex's <c>A_thread_pause_after_the_backlog_query_must_not_establish_a_file_inside_the_tone</c>):
    /// the REAL tone, 48 kHz, 1024-frame reads, 30 ms of input latency, an
    /// honest device (every backlog is what was waiting when it was asked).
    /// One 500 ms thread pause on the first block. On b6543ac1, which asked the
    /// backlog and then read the clock, the pause between them established
    /// the file at frame 0 and kept all 5,207 tone samples. "repair 2's order"
    /// is Codex's input verbatim: a backlog with no answer time no longer
    /// establishes. The other places are where the pause can fall now that the
    /// clock is read before the backlog is asked and again after the answer.
    /// The positive control is "none": the timing is still established, and
    /// the file begins exactly at the margin after the tone (335.042 ms).
    /// </summary>
    [Theory]
    [InlineData(0, "repair 2's order")]
    [InlineData(500, "repair 2's order")]
    [InlineData(0, "none")]
    [InlineData(500, "before the stamp")]
    [InlineData(500, "between the stamp and the answer")]
    [InlineData(500, "after the answer, before its reading")]
    public void A_thread_pause_around_the_backlog_question_must_not_establish_a_file_inside_the_tone(int pauseMs, string where)
    {
        AssertWindowless();
        int rate = 48000, mixRate = EarconPlayer.MixerSampleRate;
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        long origin = Ticks(1000000), toneFrom = origin + Ticks(115);
        long toneEnd = toneFrom + (long)Math.Round((double)tone.Length * Q / mixRate);
        var heard = new float[rate];
        for (int i = 0; i < heard.Length; i++)
        {
            long when = origin + (long)Math.Round((double)i * Q / rate);
            long j = (long)Math.Floor((double)(when - toneFrom) * mixRate / Q);
            if (j >= 0 && j < tone.Length) heard[i] = tone[j];
        }
        string path = Path.Combine(Path.GetTempPath(), "q3r3-" + Guid.NewGuid().ToString("N") + ".wav");
        long begin = ChainAuditionTake.FileStartAfterTone(toneEnd);
        var start = new CaptureFileStart(rate, true, .030, Q);
        start.BeginAt(begin);
        bool answerKnown = where != "repair 2's order";
        long pause = Ticks(pauseMs);
        try
        {
            using (var writer = new WavWriter(path, rate, 1))
            {
                long previous = 0;
                for (int at = 0; at < heard.Length; at += 1024)
                {
                    int n = Math.Min(1024, heard.Length - at);
                    long onTime = origin + (long)Math.Round((double)(at + n) * Q / rate) + Ticks(30);
                    long returned = at == 0 ? onTime : Math.Max(onTime, previous + Ticks(.1));
                    long stamp = returned, asked = returned, answered = returned;
                    if (at == 0)
                    {
                        switch (where)
                        {
                            // Codex's: the backlog asked first, then the pause, then the clock.
                            case "repair 2's order": stamp = returned + pause; break;
                            case "before the stamp": stamp = asked = answered = returned + pause; break;
                            case "between the stamp and the answer": asked = answered = returned + pause; break;
                            case "after the answer, before its reading": answered = returned + pause; break;
                        }
                    }
                    long backlog = Math.Max(0, (long)Math.Floor((double)(asked - Ticks(30) - origin) * rate / Q) - (at + n));
                    start.Block(writer, heard.Skip(at).Take(n).ToArray(), n, n,
                        new CaptureBlockTiming(stamp, backlog, streamStartedAt: origin - Ticks(10),
                            backlogAnsweredAt: answerKnown ? answered : null));
                    previous = Math.Max(stamp, answered);
                }
                start.Finish(writer);
            }
            Assert.True(start.FileBegan);
            int kept = heard.Skip((int)start.BoundaryFrame).Count(x => Math.Abs(x) > .001f);
            _out.WriteLine($"One {pauseMs} ms pause, {where}: established={start.TimingEstablished}; boundary={start.BoundaryFrame * 1000.0 / rate:F3} ms ({start.HowPlaced}); real tone samples retained={kept}");
            Assert.Equal(0, kept);
            long exact = (long)Math.Ceiling((double)(begin - origin) * rate / Q);
            // The device counts whole frames, so a block's bound is tight to
            // within one frame (21 microseconds), which the margin covers.
            Assert.True(start.BoundaryFrame >= exact - 1, "the file began before the margin after the tone");
            if (answerKnown)
            {
                Assert.True(start.TimingEstablished, "positive control: a caught-up block still establishes the timing");
                if (where == "none") Assert.Equal(exact, start.BoundaryFrame);
            }
            else Assert.False(start.TimingEstablished);
        }
        finally { File.Delete(path); }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    /// <summary>
    /// Codex's control (<c>A_fallback_beyond_the_stopped_capture_yields_no_file_and_the_too_short_answer</c>):
    /// a file start beyond what was captured when Stop runs begins no file,
    /// deletes the empty one, and the check says its too-short words.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_fallback_beyond_the_stopped_capture_yields_no_file_and_the_too_short_answer(bool zeroBlocks)
    {
        AssertWindowless();
        string path = Path.Combine(Path.GetTempPath(), "q3r3-" + Guid.NewGuid().ToString("N") + ".wav");
        using var rec = new MicRecorder();
        var w = new WavWriter(path, 1000, 1);
        var start = new CaptureFileStart(1000, true, 0, Q);
        long now = Stopwatch.GetTimestamp();
        start.BeginAt(now + Ticks(zeroBlocks ? 200 : -200));
        if (!zeroBlocks)
            start.Block(w, new float[100], 100, 100, new CaptureBlockTiming(now + Ticks(100), -1, streamStartedAt: now - Ticks(1000)));
        SetField(rec, "_writer", w);
        SetField(rec, "_fileStart", start);
        SetField(rec, "<Path>k__BackingField", path);
        SetField(rec, "<SampleRate>k__BackingField", 1000);
        Assert.Equal("", rec.Stop());
        Assert.False(File.Exists(path));
        Assert.True(rec.StoppedBeforeFileBegan);
        Assert.Equal(zeroBlocks, rec.StoppedDuringLead);
        Assert.Equal(MicRecorder.StopReason.Requested, rec.LastStopReason);
        Assert.Equal(RecordingNarrator.StopSaying.StoppedTooSoon,
            RecordingNarrator.StopKind(false, false, rec.CapturedSeconds, true, rec.StoppedDuringLead));
    }

    /// <summary>Codex's race (<c>Arming_the_watch_and_the_first_read_must_not_throw</c>): arming the bound and the first take, 30,000 times at once.</summary>
    [Fact]
    public void Arming_the_watch_and_the_first_read_must_not_throw()
    {
        AssertWindowless();
        var errors = new ConcurrentQueue<Exception>();
        var ends = new ConcurrentBag<EarconPlayer.ToneEnd>();
        Parallel.For(0, 30000, i =>
        {
            var end = new EarconPlayer.ToneEnd(7000, 44100, 90, Stopwatch.GetTimestamp(), () => null);
            ends.Add(end);
            Parallel.Invoke(
                () => { try { end.WatchForNoRead(); } catch (Exception ex) { errors.Enqueue(ex); } },
                () => { if ((i & 1) == 0) Thread.SpinWait(15); end.Take(Stopwatch.GetTimestamp()); });
        });
        // Settle them now, so no timer outlives the test.
        foreach (var end in ends) end.CutNow();
        _out.WriteLine($"Watch/read races threw {errors.Count} exceptions: {string.Join(", ", errors.Select(e => e.GetType().Name).Distinct())}");
        Assert.Empty(errors);
    }

    /// <summary>A mono source that, when asked, blocks inside its next read until released.</summary>
    private sealed class GatedSamples : ISampleProvider, IDisposable
    {
        private readonly float[] _data;
        private int _at;
        public GatedSamples(float[] data) => _data = data;
        public volatile bool GateNext;
        public ManualResetEventSlim InRead { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim Release { get; } = new ManualResetEventSlim();
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 1);
        public int Read(Span<float> buffer)
        {
            if (GateNext)
            {
                GateNext = false;
                InRead.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            int n = Math.Min(buffer.Length, _data.Length - _at);
            _data.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }
        public void Dispose() { InRead.Dispose(); Release.Dispose(); }
    }

    // ── Repair 4: Codex's re-check 3 of 46a481c7, adopted ────────────

    /// <summary>Holds the production "audibly over" trace line until released (Codex's gate).</summary>
    private sealed class GateTrace : TraceListener
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message?.StartsWith("EarconPlayer: the recorder's start tone is audibly over ") != true) return;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("review trace gate");
        }
    }

    /// <summary>
    /// A stage between the mixer and the output's counter that holds the read
    /// carrying the tone's last frame (or, with <see cref="HoldFirst"/>, the
    /// first read), after the mixer has returned it: what a stall in the
    /// mixer's own return path, or anything between it and the handoff, does.
    /// </summary>
    private sealed class HoldingStage : ISampleProvider, IDisposable
    {
        private readonly ISampleProvider _source;
        private readonly long _toneFrames;
        private long _passed;
        private bool _held;
        public HoldingStage(ISampleProvider source, long toneFrames) { _source = source; _toneFrames = toneFrames; }
        public bool Enabled;
        public bool HoldFirst;
        public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim Release { get; } = new ManualResetEventSlim();
        public WaveFormat WaveFormat => _source.WaveFormat;
        public int Read(Span<float> buffer)
        {
            int n = _source.Read(buffer);
            _passed += n / WaveFormat.Channels;
            if (Enabled && !_held && (HoldFirst || _passed >= _toneFrames))
            {
                _held = true;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return n;
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    /// <summary>
    /// C1a (Codex's <c>The_final_mixer_read_cannot_return_tone_samples_after_the_file_boundary</c>,
    /// and its two no-stall controls): the REAL timed tone and REAL mixer, read
    /// through the output's REAL handoff counter, the whole tone in one read or
    /// at the ordinary 100 ms cadence. Codex held the end's own production trace
    /// line; on 46a481c7 that held the final read inside our mixer after the end
    /// was published, and it returned 9,572 tone samples (whole tone) or 3,928
    /// (100 ms) about 120-130 ms AFTER the boundary. Adapted: the end is now
    /// published at the handoff and its trace written off the output's thread,
    /// so the positive control is that the trace line IS held, and the end and
    /// the final read are not.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public async Task The_final_mixer_read_cannot_return_tone_samples_after_the_file_boundary(int readMs, bool pause)
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        int toneFrames = EarconPlayer.RecorderStartBeepSamples().Length;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { ReadFully = true };
        var output = new EarconPlayer.CountingSampleProvider(mixer);
        bool beeps = EarconPlayer.RecorderBeepsEnabled, master = EarconPlayer.EarconsEnabled;
        var oldDevice = SoundTiming.AlertDevice;
        using var gate = new GateTrace();
        Task<(long Returned, int Audible)>? reader = null;
        try
        {
            EarconPlayer.RecorderBeepsEnabled = true;
            EarconPlayer.EarconsEnabled = true;
            EarconPlayer.TestAlertMixer = mixer;
            EarconPlayer.TestAlertCounter = output;
            SoundTiming.AlertDevice = () => new SoundTiming.DeviceSnapshot(0, 0, 30, 3, rate);
            if (pause) Trace.Listeners.Add(gate);
            Task<long?> end = EarconPlayer.RecorderStartBeepTimed();
            int frames = readMs == 0 ? toneFrames : rate * readMs / 1000;
            reader = Task.Run(() =>
            {
                var data = new float[frames * 2];
                while (true)
                {
                    output.Read(data);
                    if (end.IsCompleted) return (Stopwatch.GetTimestamp(), data.Count(x => Math.Abs(x) > .001f));
                    Thread.Sleep(readMs);   // the ordinary device cadence, no audio device
                }
            });
            long boundary = ChainAuditionTake.FileStartAfterTone((await end.WaitAsync(TimeSpan.FromSeconds(5)))!.Value);
            if (pause)
            {
                Assert.True(gate.Entered.Wait(5000), "positive control: the production trace line was reached and is held");
                await reader.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(gate.Release.IsSet, "positive control: the trace line was still held while the final read returned");
                int ms = (int)Math.Max(1, (boundary - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency + 120);
                await Task.Delay(ms);
                gate.Release.Set();
            }
            var result = await reader.WaitAsync(TimeSpan.FromSeconds(5));
            _out.WriteLine($"Read {readMs} ms; pause={pause}; final mixer read returned {(result.Returned - boundary) * 1000.0 / Stopwatch.Frequency:F1} ms from the file boundary with {result.Audible} nontrivial tone samples");
            Assert.True(result.Audible > 0, "positive control: the real tone was read");
            Assert.True(result.Returned <= boundary, "the real mixer handed out the tone after the published file boundary");
        }
        finally
        {
            gate.Release.Set();
            if (reader != null) await reader.WaitAsync(TimeSpan.FromSeconds(10));
            Trace.Listeners.Remove(gate);
            EarconPlayer.TestAlertMixer = null;
            EarconPlayer.TestAlertCounter = null;
            SoundTiming.AlertDevice = oldDevice;
            EarconPlayer.RecorderBeepsEnabled = beeps;
            EarconPlayer.EarconsEnabled = master;
        }
    }

    /// <summary>
    /// The CLASS behind Codex's probe: a stall ANYWHERE between the source's
    /// final read and the output's handoff, not just in one trace line. A
    /// stage between the mixer and the counter holds the read carrying the
    /// tone's last frame after the mixer has returned it. The end must not be
    /// published while it is held, and the final read must return before the
    /// boundary. With no stall (the control) the same holds.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public async Task A_stall_between_the_last_source_read_and_the_handoff_cannot_publish_the_end_early(int readMs, bool stall)
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        int toneFrames = EarconPlayer.RecorderStartBeepSamples().Length;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { ReadFully = true };
        using var stage = new HoldingStage(mixer, toneFrames) { Enabled = stall };
        var output = new EarconPlayer.CountingSampleProvider(stage);
        bool beeps = EarconPlayer.RecorderBeepsEnabled, master = EarconPlayer.EarconsEnabled;
        var oldDevice = SoundTiming.AlertDevice;
        Task<(long Returned, int Audible)>? reader = null;
        try
        {
            EarconPlayer.RecorderBeepsEnabled = true;
            EarconPlayer.EarconsEnabled = true;
            EarconPlayer.TestAlertMixer = mixer;
            EarconPlayer.TestAlertCounter = output;
            SoundTiming.AlertDevice = () => new SoundTiming.DeviceSnapshot(0, 0, 30, 3, rate);
            Task<long?> end = EarconPlayer.RecorderStartBeepTimed();
            int frames = readMs == 0 ? toneFrames : rate * readMs / 1000;
            reader = Task.Run(() =>
            {
                var data = new float[frames * 2];
                while (true)
                {
                    output.Read(data);
                    if (end.IsCompleted) return (Stopwatch.GetTimestamp(), data.Count(x => Math.Abs(x) > .001f));
                    Thread.Sleep(readMs);
                }
            });
            if (stall)
            {
                Assert.True(stage.Entered.Wait(5000), "positive control: the read carrying the last frame is held after the mixer returned it");
                bool publishedWhileHeld = await Task.WhenAny(end, Task.Delay(300)) == end;
                if (publishedWhileHeld)
                {
                    // What a premature end does: the take begins its file, then the held tone leaves.
                    long early = ChainAuditionTake.FileStartAfterTone(end.Result!.Value);
                    await Task.Delay((int)Math.Max(1, (early - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency + 120));
                }
                _out.WriteLine($"End published while the final read was held: {publishedWhileHeld}");
                stage.Release.Set();
            }
            long boundary = ChainAuditionTake.FileStartAfterTone((await end.WaitAsync(TimeSpan.FromSeconds(5)))!.Value);
            var result = await reader.WaitAsync(TimeSpan.FromSeconds(5));
            _out.WriteLine($"Read {readMs} ms; stall={stall}; final read returned {(result.Returned - boundary) * 1000.0 / Stopwatch.Frequency:F1} ms from the boundary with {result.Audible} tone samples");
            Assert.True(result.Audible > 0, "positive control: the real tone was read");
            Assert.True(result.Returned <= boundary, "the final read returned after the published file boundary");
        }
        finally
        {
            stage.Release.Set();
            if (reader != null) await reader.WaitAsync(TimeSpan.FromSeconds(10));
            EarconPlayer.TestAlertMixer = null;
            EarconPlayer.TestAlertCounter = null;
            SoundTiming.AlertDevice = oldDevice;
            EarconPlayer.RecorderBeepsEnabled = beeps;
            EarconPlayer.EarconsEnabled = master;
        }
    }

    /// <summary>
    /// A cut made while a device read is in flight above the tone settles only
    /// after that read has returned and been counted; nothing more of the tone
    /// plays.
    /// </summary>
    [Fact]
    public async Task A_cut_during_a_device_read_in_flight_is_published_only_after_that_handoff()
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        float[] tone = EarconPlayer.RecorderStartBeepSamples();
        int piece = rate / 100;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { ReadFully = true };
        using var stage = new HoldingStage(mixer, tone.Length) { Enabled = true, HoldFirst = true };
        var output = new EarconPlayer.CountingSampleProvider(stage);
        var end = new EarconPlayer.ToneEnd(tone.Length, rate, 90, Stopwatch.GetTimestamp(), () => null, afterHandoff: output.AfterThisHandoff);
        mixer.AddMixerInput(new MonoToStereoSampleProvider(new EarconPlayer.ToneEnd.Probe(new Samples(tone), end)));
        var reader = Task.Run(() => output.Read(new float[piece * 2]));
        Assert.True(stage.Entered.Wait(5000), "positive control: a device read carrying tone is in flight");
        Assert.True(end.CutNow());
        Assert.False(end.AudibleEnd.IsCompleted, "the cut was published before the read in flight returned");
        long released = Stopwatch.GetTimestamp();
        stage.Release.Set();
        Assert.Equal(piece * 2, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        long at = (await end.AudibleEnd.WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
        Assert.True(at >= released + Ticks(90) + (long)Math.Round((double)piece * Q / rate), "the cut's end counts from before the handoff");
        var after = new float[piece * 2];
        output.Read(after);
        Assert.Equal(0, after.Count(x => x != 0));
    }

    /// <summary>
    /// The handoff itself: with no device read in flight an action runs at
    /// once; during one, it runs once the mixer has returned and the frames
    /// are counted, and before the read returns to the device.
    /// </summary>
    [Fact]
    public void The_output_handoff_runs_its_actions_after_the_mixer_has_returned_and_the_frames_are_counted()
    {
        AssertWindowless();
        int rate = EarconPlayer.MixerSampleRate;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { ReadFully = true };
        EarconPlayer.CountingSampleProvider? output = null;
        long countedThen = -1;
        bool ranInside = false;
        var hook = new Hook(mixer, () => output!.AfterThisHandoff(() => countedThen = output.Frames), () => ranInside = countedThen >= 0);
        output = new EarconPlayer.CountingSampleProvider(hook);
        bool now = false;
        output.AfterThisHandoff(() => now = true);
        Assert.True(now, "with no read in flight the action runs at once");
        Assert.Equal(882, output.Read(new float[882]));
        Assert.False(ranInside, "the action ran inside the mixer's read");
        Assert.Equal(441, countedThen);
    }

    /// <summary>Calls <c>during</c> inside the read and <c>afterwards</c> just before returning it.</summary>
    private sealed class Hook : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Action _during, _afterwards;
        public Hook(ISampleProvider source, Action during, Action afterwards) { _source = source; _during = during; _afterwards = afterwards; }
        public WaveFormat WaveFormat => _source.WaveFormat;
        public int Read(Span<float> buffer)
        {
            _during();
            int n = _source.Read(buffer);
            _afterwards();
            return n;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>A mono provider over fixed samples, as the timed tone's own is.</summary>
    private sealed class Samples : ISampleProvider
    {
        private readonly float[] _data;
        private int _at;
        public Samples(float[] data) => _data = data;
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 1);
        public int Read(Span<float> buffer)
        {
            int n = Math.Min(buffer.Length, _data.Length - _at);
            _data.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }
    }

    /// <summary>
    /// Read the mixer, 1024 stereo frames at a time, until the timed tone's
    /// last sample is out; the end must not be settled before that read.
    /// Returns when that last read began and ended, and the tone frames it emitted.
    /// </summary>
    private static (long before, long after, int lastFrames) ReadTheToneThrough(MixingSampleProvider mixer, Task<long?> end)
    {
        int frames = EarconPlayer.RecorderStartBeepSamples().Length;
        ISampleProvider output = (ISampleProvider?)EarconPlayer.TestAlertCounter ?? mixer;
        var data = new float[2048];
        long before = 0, after = 0;
        int emitted = 0, last = 0;
        while (emitted < frames)
        {
            Assert.False(end.IsCompleted, "the end was settled before the tone's last sample was emitted");
            before = Stopwatch.GetTimestamp();
            output.Read(data.AsSpan());
            after = Stopwatch.GetTimestamp();
            last = Math.Min(1024, frames - emitted);
            emitted += last;
        }
        return (before, after, last);
    }

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
            EarconPlayer.TestAlertCounter = new EarconPlayer.CountingSampleProvider(mixer);   // repair 4
            SoundTiming.AlertDevice = device;
            await body(mixer);
        }
        finally
        {
            EarconPlayer.TestAlertMixer = null;
            EarconPlayer.TestAlertCounter = null;
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
