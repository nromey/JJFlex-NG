using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JJFlexWpf;
using JJPortaudio;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Codex's check of 2567a596 (B1): the seam mark rode the buffer that came out of
/// the receive processing, but that buffer still held audio from before the lost
/// packets; with a 1,920-frame pad the mark was 40 ms ahead of the real
/// discontinuity. Here the mark goes in with the audio after the loss and must
/// come out exactly where that audio does: through both slots, their pads, their
/// drains and growths, turning on and off, and a pre-growth before a slower engine.
/// Windowless: a pipeline, buffers and arithmetic.
/// </summary>
public class ReceiveSeamTimelineTests
{
    private const int F = 480;
    private const long Scale = 1 << 20;
    private const int Lost = 2880;          // six 10 ms packets
    private const int SeamBuffer = 200;     // the first buffer after the loss, 2 s in
    private const int Lead = 100;           // a move starts 1 s before it, so it is part-way when the seam passes
    private readonly ITestOutputHelper _out;

    public ReceiveSeamTimelineTests(ITestOutputHelper output) { _out = output; NoiseReductionPerChannelTests.AssertWindowless(); }

    private static DenoiseEngineEntry Row(string id, DenoiseFamily family, int delay) =>
        new(id, DenoiseSide.Pc, family, "label", new[] { DenoiseDirection.Receive },
            (_, _) => new AlignedSwitchTests.DelayGainEngine(id, delay, 1f), () => Unavailability.None);

    private static SharedDelayPlan Plan(int samples, params string[] ids) =>
        new(samples, ids.Select(id => new ReceiveSharedDelay.Member(id, null)).ToArray());

    private static void WaitFor(Func<bool> done)
    {
        var t = Stopwatch.StartNew();
        while (!done() && t.ElapsedMilliseconds < 5000) Thread.Sleep(1);
        Assert.True(done(), "a bank was never delivered");
    }

    /// <summary>
    /// What happened to one seam: where the processing said it came out, where the
    /// audio after the loss really begins in the output, and the input frame the
    /// seam went in at.
    /// </summary>
    private sealed record Result(long Reported, long Actual, long In, string Note);

    /// <summary>
    /// A ramp whose value names its input frame, with <see cref="Lost"/> frames
    /// missing before <see cref="SeamBuffer"/>; <paramref name="setUp"/> configures
    /// the pipeline, <paramref name="atBuffer"/> acts on it before each buffer.
    /// </summary>
    private static Result Run(Action<RxAudioPipeline> setUp, Action<RxAudioPipeline, int>? atBuffer = null, int buffers = 600)
    {
        var catalog = new DenoiseEngineCatalog(new[]
        {
            Row("rnnoise", DenoiseFamily.Neural, 480),
            Row("b", DenoiseFamily.Neural, 3408),
            Row("sp", DenoiseFamily.Spectral, 960),
        });
        using var p = new RxAudioPipeline(catalog: catalog);
        p.NeuralSlot.MuteOverrideForTest = false;
        p.SpectralSlot.MuteOverrideForTest = false;
        p.NeuralSlot.CrossFadeOverrideForTest = true;
        p.SpectralSlot.CrossFadeOverrideForTest = true;
        p.NeuralSlot.SetStrength("rnnoise", 1); p.NeuralSlot.SetStrength("b", 1); p.SpectralSlot.SetStrength("sp", 1);
        setUp(p);
        var output = new List<float>();
        var audio = new float[2 * F];
        long reported = -1;
        long seamIn = (long)SeamBuffer * F;
        for (int b = 0; b < buffers; b++)
        {
            atBuffer?.Invoke(p, b);
            for (int i = 0; i < F; i++)
            {
                long n = (long)b * F + i;
                float v = (n + (n >= seamIn ? Lost : 0)) / (float)Scale;
                audio[2 * i] = audio[2 * i + 1] = v;
            }
            if (b == SeamBuffer) p.MarkSeamIn();
            p.Process(audio);
            if (p.LastBufferSeamAt >= 0)
            {
                Assert.True(reported < 0, "the seam came out twice");
                reported = (long)b * F + p.LastBufferSeamAt;
            }
            for (int i = 0; i < F; i++) output.Add(audio[2 * i]);
        }
        // The audio after the loss begins where the output first names an input frame
        // past the loss (the ramp's value), in a fade where the blend first does.
        long after = seamIn + Lost;
        long actual = -1;
        for (int k = 1; k < output.Count; k++)
            if (output[k] * (double)Scale >= after - 0.5 && output[k - 1] * (double)Scale < after - 0.5) { actual = k; break; }
        return new Result(reported, actual, seamIn, $"{p.NeuralSlot.LastTurn} {p.NeuralSlot.LastSwitch} {p.NeuralSlot.LastRetime}");
    }

    /// <summary>A seam through a line that is moving: the delay it came out at lies strictly between where the move started and where it ends, so the move was part-way, and it came out with its audio.</summary>
    private void CheckMoving(string name, Result r, int from, int to)
    {
        Check(name, r, -1, RxLagShifter.MaxLagFramesAt(48000));
        long delay = r.Actual - r.In;
        Assert.True(delay > Math.Min(from, to) + F && delay < Math.Max(from, to) - F, $"{name}: the seam passed at a delay of {delay}, not part-way from {from} to {to}");
    }

    private void Check(string name, Result r, long expectedDelay, int tolerance = 0)
    {
        _out.WriteLine($"{name}: went in at {r.In}, reported at {r.Reported}, the audio after the loss begins at {r.Actual} (delay {r.Actual - r.In}); {r.Note}");
        Assert.True(r.Actual >= 0, "the audio after the loss never came out");
        Assert.True(r.Reported >= 0, "the seam never came out");
        // Positive control: the audio really was delayed by the processing, so a mark
        // riding the newest buffer (the defect) would be this far early.
        if (expectedDelay >= 0) Assert.Equal(expectedDelay, r.Actual - r.In);
        else Assert.True(r.Actual - r.In >= F, $"the processing delayed the seam only {r.Actual - r.In} frames, so nothing was tested");
        Assert.True(Math.Abs(r.Reported - r.Actual) <= tolerance, $"{name}: reported {r.Reported - r.Actual} frames from the audio seam");
    }

    [Fact]
    public void Off_the_seam_comes_out_where_it_went_in()
    {
        var r = Run(p => p.RnnEnabled = false);
        _out.WriteLine($"off: in {r.In}, reported {r.Reported}, actual {r.Actual}");
        Assert.Equal(r.In, r.Actual);
        Assert.Equal(r.Actual, r.Reported);
    }

    [Fact]
    public void Through_the_bank_alone_the_seam_comes_out_at_the_engine_s_delay()
    {
        Check("the bank alone", Run(p => p.RnnEnabled = true), 480);
    }

    [Fact]
    public void Through_a_padded_line_the_seam_comes_out_at_the_shared_delay()
    {
        Check("padded to 3408", Run(p => { p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b"); p.RnnEnabled = true; }), 3408);
    }

    [Fact]
    public void Through_both_slots_the_seam_comes_out_at_the_sum_of_their_delays()
    {
        Check("both slots", Run(p =>
        {
            Assert.True(p.SelectPcEngine("sp"));
            p.SpectralEnabled = true;
            WaitFor(() => p.SpectralSlot.BuildDelivered("sp"));
            p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b");
            p.RnnEnabled = true;
        }), 960 + 3408);
    }

    [Fact]
    public void While_a_line_drains_to_a_new_plan_the_seam_comes_out_with_its_audio()
    {
        // Held at 3408, then a plan of 480 just before the seam: the running line drains
        // 2,928 frames by splices while the seam passes through it.
        var r = Run(p => { p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b"); p.RnnEnabled = true; },
            (p, b) => { if (b == SeamBuffer - Lead) p.NeuralSlot.SharedPlan = Plan(480, "rnnoise"); });
        CheckMoving("draining", r, 3408, 480);
    }

    [Fact]
    public void While_turning_off_the_seam_comes_out_with_the_dry_audio()
    {
        var r = Run(p => { p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b"); p.RnnEnabled = true; },
            (p, b) => { if (b == SeamBuffer - Lead) p.RnnEnabled = false; });
        CheckMoving("turning off", r, 3408, 0);
    }

    [Fact]
    public void A_seam_that_comes_out_in_the_turn_off_buffer_itself_is_where_the_audio_is()
    {
        // Held at 3408, the seam comes out 48 frames into buffer 207; turning off at that
        // buffer puts it inside the turn-off's own fade from the processed to the dry audio.
        int at = (int)(((long)SeamBuffer * F + 3408) / F);
        var r = Run(p => { p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b"); p.RnnEnabled = true; },
            (p, b) => { if (b == at) p.RnnEnabled = false; });
        Assert.Equal(at, (int)(r.Actual / F));
        Assert.True(r.Note.Contains("On = False"), "the turn-off did not happen: " + r.Note);
        Check("in the turn-off buffer", r, 3408);
    }

    [Fact]
    public void While_turning_on_the_seam_comes_out_with_the_dry_audio()
    {
        var r = Run(p => { p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b"); p.RnnEnabled = false; },
            (p, b) => { if (b == SeamBuffer - Lead) p.RnnEnabled = true; });
        CheckMoving("turning on", r, 0, 3408);
    }

    [Fact]
    public void While_the_running_line_grows_for_a_slower_engine_the_seam_comes_out_with_its_audio()
    {
        var r = Run(p => p.RnnEnabled = true,
            (p, b) =>
            {
                if (b != SeamBuffer - Lead) return;
                Assert.True(p.NeuralSlot.Select("b", out _));
                WaitFor(() => p.NeuralSlot.BuildDelivered("b"));
            });
        CheckMoving("a pre-growth", r, 480, 3408);
    }

    /// <summary>
    /// The class follows the same timeline (Codex's note with B1): the neural
    /// slot's input is the spectral slot's OUTPUT, so the class it is given must be
    /// the class of the audio the spectral slot let out, as many blocks back as that
    /// slot delays it, not the newest input's. A mode change marks the blocks: the
    /// neural line's read point turns digital exactly when the first digital block
    /// reaches it, 960 + 3408 frames after it went in, at buffer X + 9; given the
    /// newest class it turned two buffers early.
    /// </summary>
    [Fact]
    public void The_second_slot_is_given_the_class_of_what_the_first_slot_let_out()
    {
        var catalog = new DenoiseEngineCatalog(new[] { Row("rnnoise", DenoiseFamily.Neural, 480), Row("b", DenoiseFamily.Neural, 3408), Row("sp", DenoiseFamily.Spectral, 960) });
        using var p = new RxAudioPipeline(catalog: catalog);
        p.NeuralSlot.MuteOverrideForTest = false; p.SpectralSlot.MuteOverrideForTest = false;
        Assert.True(p.SelectPcEngine("sp"));
        p.SpectralEnabled = true;
        WaitFor(() => p.SpectralSlot.BuildDelivered("sp"));
        p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b");
        p.RnnEnabled = true;
        p.SetCurrentMode("USB");
        var rng = new Random(2);
        var audio = new float[2 * F];
        const int x = 60;
        int flipped = -1;
        for (int b = 0; b < 120 && flipped < 0; b++)
        {
            if (b == x) p.SetCurrentMode("DIGU");
            for (int i = 0; i < F; i++) audio[2 * i] = audio[2 * i + 1] = (float)(rng.NextDouble() - 0.5) * 0.1f;
            p.Process(audio);
            var lines = p.NeuralSlot.LinesForTest!.Value;
            if (b >= x && (lines.Run.ClassAtRead & RxContentClass.NoSplice) != 0) flipped = b;
        }
        _out.WriteLine($"the mode changed before buffer {x}; the neural line's read point turned digital after buffer {flipped}");
        Assert.Equal(x + 9, flipped);
    }

    /// <summary>
    /// Carrying a seam allocates nothing on the audio thread: the trail, the marks,
    /// the reports are all preallocated. Counted from well after start-up across the
    /// seam going in and coming out of both slots. The least of three fresh pipelines,
    /// because the counter is known to move by a few KB at intervals with nothing
    /// allocated (AlignedSwitchTests.LeastAllocated); a fresh pipeline each time means
    /// an allocation that belongs to the seam path shows on every one.
    /// </summary>
    [Fact]
    public void Carrying_a_seam_allocates_nothing()
    {
        long least = AlignedSwitchTests.LeastAllocated(() =>
        {
            var catalog = new DenoiseEngineCatalog(new[] { Row("rnnoise", DenoiseFamily.Neural, 480), Row("b", DenoiseFamily.Neural, 3408), Row("sp", DenoiseFamily.Spectral, 960) });
            using var p = new RxAudioPipeline(catalog: catalog);
            p.NeuralSlot.MuteOverrideForTest = false; p.SpectralSlot.MuteOverrideForTest = false;
            Assert.True(p.SelectPcEngine("sp"));
            p.SpectralEnabled = true;
            WaitFor(() => p.SpectralSlot.BuildDelivered("sp"));
            p.NeuralSlot.SharedPlan = Plan(3408, "rnnoise", "b");
            p.RnnEnabled = true;
            var audio = new float[2 * F];
            for (int b = 0; b < 60; b++) p.Process(audio);
            int came = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int b = 60; b < 90; b++)
            {
                if (b == 62) p.MarkSeamIn();
                p.Process(audio);
                if (p.LastBufferSeamAt >= 0) came++;
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(1, came);
            return bytes;
        }, attempts: 3);
        Assert.Equal(0, least);
    }

    /// <summary>
    /// One slot through an engine handover: rnnoise at <paramref name="from"/> frames,
    /// "b" at <paramref name="to"/> chosen before buffer 60. Returns the handover
    /// buffer, every position the slot reported a seam at, every position the output
    /// first carries audio from after the loss (a replay crosses twice), and the
    /// input frame heard 100 frames into the handover buffer.
    /// </summary>
    private static (int Handover, List<long> Reported, List<long> Crossings, long HeardAt) Handover(int from, int to, bool fade, long lossAt)
    {
        DenoiseEngineEntry R(string id, int d) => Row(id, DenoiseFamily.Neural, d);
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 2, new DenoiseEngineCatalog(new[] { R("rnnoise", from), R("b", to) }));
        try
        {
            slot.CrossFadeOverrideForTest = fade; slot.MuteOverrideForTest = false;
            slot.SetStrength("rnnoise", 1); slot.SetStrength("b", 1);
            slot.Enabled = true;
            var x = new float[2 * F];
            var reported = new List<long>(); var crossings = new List<long>();
            int handover = -1; long heardAt = -1;
            float prev = 0;
            for (int b = 0; b < 600; b++)
            {
                if (b == 60) { Assert.True(slot.Select("b", out _)); WaitFor(() => slot.BuildDelivered("b")); }
                for (int i = 0; i < F; i++)
                {
                    long n = (long)b * F + i;
                    x[2 * i] = x[2 * i + 1] = (n + (lossAt >= 0 && n >= lossAt ? Lost : 0)) / (float)Scale;
                }
                if (lossAt >= 0 && b == lossAt / F) slot.NoteInputSeam((int)(lossAt % F));
                var before = slot.LastSwitch;
                slot.Process(x, true, true, false, null, RxContentClass.Classified | RxContentClass.NotVoiceRun);
                if (handover < 0 && !Equals(before, slot.LastSwitch)) { handover = b; heardAt = (long)Math.Round(x[2 * 100] * (double)Scale); }
                for (int i = 0; i < slot.SeamsOut; i++) reported.Add((long)b * F + slot.SeamOut(i));
                if (lossAt >= 0)
                    for (int i = 0; i < F; i++)
                    {
                        float cur = x[2 * i];
                        if (prev * (double)Scale < lossAt + Lost - 0.5 && cur * (double)Scale >= lossAt + Lost - 0.5) crossings.Add((long)b * F + i);
                        prev = cur;
                    }
            }
            return (handover, reported, crossings, heardAt);
        }
        finally { slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    /// <summary>
    /// Codex's re-check of 5a0a8c93 (B1): with the cross-fade off, a handover played
    /// the incoming engine but reported the outgoing one's seams, so a seam coming out
    /// in the handover buffer was lost (switching to a faster engine), or its replay
    /// went unmarked (switching to a slower one). The seam is put where the incoming
    /// engine plays it, 100 frames into the handover buffer; every place the output
    /// crosses the loss must be reported, and nothing else.
    /// </summary>
    [Theory]
    [InlineData(3408, 480, false)]
    [InlineData(480, 3408, false)]
    [InlineData(3408, 480, true)]
    [InlineData(480, 3408, true)]
    public void A_seam_that_comes_out_in_the_handover_buffer_is_reported_where_it_is_heard(int from, int to, bool fade)
    {
        var probe = Handover(from, to, fade, -1);
        Assert.True(probe.Handover > 0, "no handover happened");
        var run = Handover(from, to, fade, probe.HeardAt);
        _out.WriteLine($"{from} to {to}, cross-fade {(fade ? "on" : "off")}: handover at buffer {run.Handover} (probe {probe.Handover}); the loss at input {probe.HeardAt}; "
            + $"crossings {string.Join(", ", run.Crossings)}; reported {string.Join(", ", run.Reported)}");
        // Positive control: the seam really comes out inside the handover buffer.
        Assert.Contains(run.Crossings, c => c / F == run.Handover);
        Assert.Equal(run.Crossings, run.Reported);
    }
}
