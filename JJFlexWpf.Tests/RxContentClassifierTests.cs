using System;
using System.Linq;
using JJPortaudio;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P2 (#873 phase 2): the content classifier the lag shifter
/// reads, on the stored RNNoise v0.1.1 voice branch with the loudness check
/// beside it (ruled 2026-11-20, S10). Windowless and silent: audio arrays in,
/// classes out, nothing played.
/// </summary>
public sealed class RxContentClassifierTests
{
    private const int Rate = 48000, F = 480;
    private readonly ITestOutputHelper _out;
    public RxContentClassifierTests(ITestOutputHelper output) { NoiseReductionPerChannelTests.AssertWindowless(); _out = output; }

    private static float[] Stereo(float[] mono)
    {
        var s = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) s[2 * i] = s[2 * i + 1] = mono[i];
        return s;
    }

    private static float[] BandNoise(int frames, int seed, float amp)
    {
        // White noise through a gentle band-pass (two one-pole sections): band noise, not hiss.
        var rng = new Random(seed);
        var a = new float[frames];
        double lp = 0, hpIn = 0, hp = 0;
        for (int i = 0; i < frames; i++)
        {
            double x = rng.NextDouble() * 2 - 1;
            lp += (x - lp) * 0.3;
            hp = 0.97 * (hp + lp - hpIn); hpIn = lp;
            a[i] = (float)(hp * amp);
        }
        return a;
    }

    private static (double Speech, double NotVoice, RxContentClass[] Classes) Run(RxContentClassifier c, float[] stereo)
    {
        var classes = new RxContentClass[stereo.Length / 2 / F];
        for (int b = 0; b < classes.Length; b++) classes[b] = c.Classify(stereo.AsSpan(b * 2 * F, 2 * F), 2);
        return (classes.Count(k => (k & RxContentClass.Speech) != 0) / (double)classes.Length,
                classes.Count(k => (k & RxContentClass.NotVoiceRun) != 0) / (double)classes.Length, classes);
    }

    [Fact]
    public void Voice_over_band_noise_reads_as_speech_and_band_noise_alone_reads_as_noise()
    {
        using var c = new RxContentClassifier();
        Assert.True(c.DetectorAvailable, c.UnavailableDetail);
        var voice = SharedDelayFiguresTests.ReferenceSpeech(startSeconds: 10, seconds: 8);
        var noise = BandNoise(voice.Length, 5, 0.02f);
        var mixed = voice.Select((v, i) => v + noise[i]).ToArray();
        var (speech, _, _) = Run(c, Stereo(mixed));
        using var c2 = new RxContentClassifier();
        var (speechInNoise, notVoice, _) = Run(c2, Stereo(BandNoise(Rate * 8, 6, 0.02f)));
        _out.WriteLine($"voice over band noise: {speech:P0} of blocks speech; band noise alone: {speechInNoise:P0} speech, {notVoice:P0} in a not-voice run");
        Assert.True(speech > 0.5, $"only {speech:P0} of the voice read as speech");
        Assert.True(speechInNoise < 0.1, $"{speechInNoise:P0} of plain band noise read as speech");
        Assert.True(notVoice > 0.8, $"only {notVoice:P0} of plain band noise got the noise policy");
    }

    /// <summary>
    /// A measured fact about the stored detector, not a wish: it calls a CLEAN
    /// steady 625 Hz carrier speech (probability 0.999), though with band noise
    /// about 20 dB down it mostly does not. The follow-up research measured
    /// "steady carrier 0" on noisy material, which this agrees with; the clean
    /// case is new. So the lag shifter takes tonal from its own
    /// periodicity and never from this flag (RxLagShifterTests,
    /// A_carrier_the_detector_calls_speech_is_still_tonal_and_never_takes_the_gap_path).
    /// If this starts failing, the detector changed, and that design note can be revisited.
    /// </summary>
    [Fact]
    public void The_stored_detector_calls_a_steady_carrier_speech_so_the_shifter_does_not_trust_it_for_tones()
    {
        var tone = Enumerable.Range(0, Rate * 4).Select(n => (float)(0.3 * Math.Sin(2 * Math.PI * 625 * n / Rate))).ToArray();
        using var c = new RxContentClassifier();
        var (speech, _, _) = Run(c, Stereo(tone));
        _out.WriteLine($"625 Hz carrier: {speech:P0} speech, last probability {c.LastProbability:0.000}");
        var rng = new Random(625);
        var noisy = tone.Select(v => v + (float)((rng.NextDouble() - 0.5) * 0.03)).ToArray();
        using var c2 = new RxContentClassifier();
        var (speech2, _, _) = Run(c2, Stereo(noisy));
        _out.WriteLine($"with noise about 20 dB down: {speech2:P0} speech, last probability {c2.LastProbability:0.000}");
        Assert.True(speech > 0.9, "the clean carrier is no longer called speech");
        Assert.True(speech2 < 0.5, "the noisy carrier is now called speech");
    }

    [Fact]
    public void The_slice_mode_marks_CW_and_the_digital_modes()
    {
        using var c = new RxContentClassifier();
        var noise = Stereo(BandNoise(F * 20, 7, 0.02f));
        foreach (var (mode, flag) in new[] { ("DIGU", RxContentClass.NoSplice), ("DIGL", RxContentClass.NoSplice), ("RTTY", RxContentClass.NoSplice), ("CW", RxContentClass.Cw) })
        {
            c.SetMode(mode);
            var k = c.Classify(noise.AsSpan(0, 2 * F), 2);
            Assert.True((k & flag) != 0, $"{mode} did not mark {flag}");
        }
        foreach (string mode in new[] { "USB", "LSB", "AM", "FM", "FDV", "" })
        {
            c.SetMode(mode);
            var k = c.Classify(noise.AsSpan(0, 2 * F), 2);
            Assert.True((k & (RxContentClass.NoSplice | RxContentClass.Cw)) == 0, $"{mode} marked a mode flag");
        }
    }

    [Fact]
    public void With_no_detector_every_block_is_unclassified_but_the_mode_still_counts()
    {
        using var c = new RxContentClassifier(libraryPath: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-detector.dll"));
        Assert.False(c.DetectorAvailable);
        var voice = Stereo(SharedDelayFiguresTests.ReferenceSpeech(startSeconds: 10, seconds: 2));
        var (_, _, classes) = Run(c, voice);
        Assert.All(classes, k => Assert.Equal(RxContentClass.Unclassified, k));
        c.SetMode("DIGU");
        Assert.Equal(RxContentClass.NoSplice, c.Classify(voice.AsSpan(0, 2 * F), 2));
    }

    [Fact]
    public void The_class_of_an_earlier_block_is_kept_for_audio_that_comes_out_of_a_delay_later()
    {
        using var c = new RxContentClassifier();
        var noise = Stereo(BandNoise(F * 30, 8, 0.02f));
        for (int b = 0; b < 30; b++) c.Classify(noise.AsSpan(b * 2 * F, 2 * F), 2);
        Assert.Equal(30, c.Blocks);
        Assert.NotEqual(RxContentClass.Unclassified, c.ClassOfBlock(29));
        Assert.NotEqual(RxContentClass.Unclassified, c.ClassOfBlock(0));
        Assert.Equal(RxContentClass.Unclassified, c.ClassOfBlock(30));   // not seen yet
        Assert.Equal(RxContentClass.Unclassified, c.ClassOfBlock(-1));
    }

    [Fact]
    public void Its_cost_per_block_is_measured_and_it_allocates_nothing()
    {
        using var c = new RxContentClassifier(timeDetector: true);
        var voice = Stereo(SharedDelayFiguresTests.ReferenceSpeech(startSeconds: 10, seconds: 10));
        int blocks = voice.Length / 2 / F;
        for (int b = 0; b < 20; b++) c.Classify(voice.AsSpan(b * 2 * F, 2 * F), 2);
        var sw = new System.Diagnostics.Stopwatch();
        long before = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        for (int b = 20; b < blocks; b++) c.Classify(voice.AsSpan(b * 2 * F, 2 * F), 2);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double perBlock = sw.Elapsed.TotalMilliseconds * 1000 / (blocks - 20);
        double detector = c.DetectorTime.TotalMilliseconds * 1000 / blocks;
        _out.WriteLine($"{Environment.Is64BitProcess switch { true => "x64", false => "x86" }}: {perBlock:0.0} µs per 10 ms block in all ({detector:0.0} µs in the detector), {perBlock / 100:0.00} percent of one core, one detector on the mix of both ears");
        Assert.Equal(0, allocated);
        Assert.True(perBlock < 500, $"{perBlock:0} µs per block");
    }
}
