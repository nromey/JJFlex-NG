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
/// <b>Repair 2 (Codex's re-check of ca39306c):</b> his four reproductions are
/// adopted at the end of this class: a tone the mixer never takes must not
/// sound after the file began (C1a), a late first read with no position (C1a),
/// a capture reader behind through the lead (C1b, with his no-backlog control),
/// and a stop before the first block (item 3).
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
                    // Repair 2: a block on time reports no backlog; a late one's
                    // backlog is not reported; the stream started 5 ms before frame 0.
                    int jitter = jitterMs[k % jitterMs.Length];
                    start.Block(w, heard.Skip(at).Take(n).ToArray(), n, n,
                        new CaptureBlockTiming(arrival, jitter == 0 ? 0 : -1, streamStartedAt: origin - Ticks(5)));
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
    public async Task The_timed_tone_estimates_the_whole_chain_from_its_first_read_when_the_output_will_not_say_its_position()
    {
        // Repair 2: from the mixer's first READ, never from the hand-over.
        AssertWindowless();
        int mixRate = EarconPlayer.MixerSampleRate;
        await WithMixer(() => new SoundTiming.DeviceSnapshot(5000, -1, 50, 4, mixRate), async mixer =>
        {
            var end = EarconPlayer.RecorderStartBeepTimed();
            long before = Stopwatch.GetTimestamp();
            mixer.Read(new float[2048].AsSpan());
            long after = Stopwatch.GetTimestamp();
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
        // Read first: the withdrawal does nothing, and the end is the read's.
        var b = new EarconPlayer.ToneEnd(frames, rate, 90, Ticks(1000), () => null);
        Assert.True(b.Take(Ticks(1100)));
        Assert.False(b.Withdraw());
        Assert.True(b.Take(Ticks(1110)));   // later reads still play
        Assert.Equal(EarconPlayer.ToneEnd.Estimated(Ticks(1100), 90, frames, rate, Q), await b.AudibleEnd);
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
        var end = new EarconPlayer.ToneEnd(frames, rate, 90, handed, () => null);
        end.FirstRead(read);
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
                        new CaptureBlockTiming(arrival, backlog, streamStartedAt: origin - Ticks(10)));
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
