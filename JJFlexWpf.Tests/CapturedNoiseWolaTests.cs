using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The captured-noise subtraction's rewrite (Sprint 49 Track C, Part 2): a
/// 960-point frame, a 480 hop, the square root of the periodic Hann on both
/// sides, its delay reported and constant, old profiles converted. Measured
/// against the old engine it is built beside, which is left untouched.
/// </summary>
/// <remarks>
/// <b>Windowless and silent</b>: arrays in, arrays out, on an MTA thread
/// (asserted in the constructor). The reference voice is read from disk.
/// This class makes its own signals and does not depend on the NR4 tests.
/// </remarks>
public sealed class CapturedNoiseWolaTests
{
    private const int Rate = 48000;
    private const int Block = 480;
    private readonly ITestOutputHelper _output;

    public CapturedNoiseWolaTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _output = output;
    }

    // ---- The transform and the window ----

    [Fact]
    public void TheTransformIsAnExact960PointDft()
    {
        var rng = new Random(960);
        var re = Enumerable.Range(0, 960).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var im = Enumerable.Range(0, 960).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var fr = (double[])re.Clone(); var fi = (double[])im.Clone();
        var fft = new CapturedNoiseWolaEngine.Fft960();
        fft.Forward(fr, fi, new double[960], new double[960]);
        double worst = 0;
        foreach (int k in new[] { 0, 1, 2, 3, 5, 7, 31, 64, 100, 239, 240, 319, 479, 480, 481, 700, 959 })
        {
            double sr = 0, si = 0;
            for (int n = 0; n < 960; n++)
            {
                double a = -2 * Math.PI * k * n / 960;
                sr += re[n] * Math.Cos(a) - im[n] * Math.Sin(a);
                si += re[n] * Math.Sin(a) + im[n] * Math.Cos(a);
            }
            worst = Math.Max(worst, Math.Max(Math.Abs(sr - fr[k]), Math.Abs(si - fi[k])));
        }
        Assert.True(worst < 1e-9, $"the 960-point transform is off a direct DFT by {worst:G3}");
        fft.Inverse(fr, fi, new double[960], new double[960]);
        for (int n = 0; n < 960; n++) { Assert.Equal(re[n], fr[n], 10); Assert.Equal(im[n], fi[n], 10); }
    }

    [Fact]
    public void TheWindowsOverlapSumsToExactlyOne()
    {
        double worst = 0;
        for (int n = 0; n < 480; n++) worst = Math.Max(worst, Math.Abs(CapturedNoiseWolaEngine.OverlapSum(n) - 1));
        Assert.True(worst < 1e-12, $"sqrt periodic Hann, applied twice, overlaps to 1 within {worst:G3}");

        // What the symmetric window would have left (why periodic): the square
        // root of a 960-point symmetric Hann, applied twice, at the same hop.
        double sMax = double.MinValue, sMin = double.MaxValue;
        for (int n = 0; n < 480; n++)
        {
            double a = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / 959), b = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (n + 480) / 959);
            sMax = Math.Max(sMax, a + b); sMin = Math.Min(sMin, a + b);
        }
        _output.WriteLine($"Overlap sum: periodic within {worst:G3} of 1; symmetric would swing {20 * Math.Log10(sMax / sMin):F4} dB.");
        Assert.True(20 * Math.Log10(sMax / sMin) > 0.0001);
    }

    // ---- The ripple, measured both ways ----

    [Fact]
    public void TheOldEngineRipplesAndTheNewOneDoesNot()
    {
        float[] sine = Sine(seconds: 2, hz: 1000, amplitude: 0.25);
        var profile = Enumerable.Repeat(0.01, 1024).ToArray();

        using var old = new SpectralCaptureEngine();
        old.SetProfile(profile);
        Assert.True(old.TrySetSetting("subtraction_amount", new(0)));
        var oldOut = Run(old, sine);

        using var neu = new CapturedNoiseWolaEngine();
        neu.SetProfile(profile);
        Assert.True(neu.TrySetSetting("subtraction_amount", new(0)));
        var newOut = Run(neu, sine);

        var (oldRipple, oldPeriod) = Ripple(oldOut, from: Rate / 4, to: oldOut.Length - Rate / 4);
        var (newRipple, _) = Ripple(newOut, from: Rate / 4, to: newOut.Length - Rate / 4);
        _output.WriteLine($"Ripple on a steady 1 kHz sine, profile loaded, subtraction 0: old engine {oldRipple:F2} dB (period about {oldPeriod:F0} samples, {Rate / oldPeriod:F1} Hz), new engine {newRipple:F4} dB.");
        Assert.InRange(oldRipple, 5, 7);
        Assert.InRange(oldPeriod, 500, 524);
        Assert.True(newRipple < 0.1, $"the new engine ripples {newRipple:F3} dB");
    }

    // ---- Reconstruction and delay ----

    [Fact]
    public void AtNoSubtractionItReturnsItsInputExactly480SamplesLate()
    {
        float[] noise = WhiteNoise(seconds: 5, rms: 0.1, seed: 48);
        using var e = new CapturedNoiseWolaEngine();
        e.SetProfile(Enumerable.Repeat(0.5, 1024).ToArray());
        Assert.True(e.TrySetSetting("subtraction_amount", new(0)));
        var output = Run(e, noise);
        double worst = 0;
        for (int i = 480; i < output.Length; i++) worst = Math.Max(worst, Math.Abs(output[i] - noise[i - 480]));
        _output.WriteLine($"Reconstruction at subtraction 0 over 5 s: worst error {worst:G3}.");
        Assert.True(worst < 1e-5, $"reconstruction is off by {worst:G3}");
        Assert.All(output.Take(480), s => Assert.True(Math.Abs(s) < 1e-9, $"the first hop should be silence, not {s:G3}"));
    }

    [Fact]
    public void WithNoProfileItIsStraightThrough480SamplesLate()
    {
        float[] noise = WhiteNoise(seconds: 2, rms: 0.1, seed: 49);
        using var e = new CapturedNoiseWolaEngine();
        Assert.False(e.HasProfile);
        var output = Run(e, noise);
        for (int i = 480; i < output.Length; i++) Assert.Equal(noise[i - 480], output[i], 6);
    }

    [Fact]
    public void ItsDelayIs480WithAProfileAndWithout()
    {
        float[] speech = ReferenceSpeech(startSeconds: 12, seconds: 4);
        foreach (bool withProfile in new[] { false, true })
        {
            using var e = new CapturedNoiseWolaEngine();
            if (withProfile) e.SetProfile(CaptureProfile(WhiteNoise(seconds: 2, rms: 0.002, seed: 5)));
            Assert.Equal(withProfile, e.HasProfile);
            Assert.Equal(480, e.LatencySamples);
            int measured = PeakLag(speech, Run(e, speech), maxLag: 4800);
            _output.WriteLine($"New engine, {(withProfile ? "with" : "without")} a profile: reports {e.LatencySamples}, measured {measured}.");
            Assert.InRange(measured, 480 - 2, 480 + 2);
        }
        using var old = new SpectralCaptureEngine();
        old.SetProfile(CaptureProfile(WhiteNoise(seconds: 2, rms: 0.002, seed: 5)));
        _output.WriteLine($"Old engine with a profile, for the record: reports {old.LatencySamples}, measured {PeakLag(speech, Run(old, speech), maxLag: 4800)}.");
    }

    // ---- Profiles ----

    /// <summary>
    /// The brief's open question, settled by measurement: the capture stores
    /// the FULL 1,024-element spectrum FftSharp returns, symmetric, not 513
    /// bins, and the saved file says FftSize 1024.
    /// </summary>
    [Fact]
    public void TheCaptureStoresTheFullSymmetric1024PointSpectrum()
    {
        var p = CaptureProfile(WhiteNoise(seconds: 1.5, rms: 0.05, seed: 6));
        Assert.Equal(1024, p.Length);
        for (int k = 1; k < 512; k++) Assert.Equal(p[k], p[1024 - k], 9);

        string dir = Path.Combine(Path.GetTempPath(), "jjflex-wola-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new SpectralSubtractionProvider(48000, 1);
            provider.SetChannelProfile(0, p);
            string file = Path.Combine(dir, "saved.xml");
            Assert.True(provider.SaveProfile(file, "saved"));
            var data = Load(file);
            Assert.Equal(1024, data.FftSize);
            Assert.Equal(1024, data.Magnitudes!.Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// White noise captured through the real capture path, converted, against
    /// the mean magnitude this engine's own window and transform measure on
    /// the same noise. The positive control drops the window correction.
    /// </summary>
    [Fact]
    public void AConvertedProfileMatchesWhatTheNewEngineMeasuresInLevel()
    {
        float[] noise = WhiteNoise(seconds: 3, rms: 0.05, seed: 2026);
        var capture = CaptureProfile(noise, seconds: 3);
        var converted = CapturedNoiseWolaEngine.ConvertProfile(capture, 1024);
        var native = CapturedNoiseWolaEngine.MeasureMeanMagnitudes(noise);
        double correction = CapturedNoiseWolaEngine.WindowCorrection(1024);

        var (mean, meanAbs, worst) = Compare(converted, native);
        var uncorrected = converted.Select(x => x / correction).ToArray();
        var (meanU, meanAbsU, worstU) = Compare(uncorrected, native);
        _output.WriteLine($"Window correction {correction:F4} ({20 * Math.Log10(correction):F2} dB). Converted against native: mean {mean:+0.00;-0.00;0.00} dB, mean absolute {meanAbs:F2} dB, worst bin {worst:F2} dB. Without the correction: mean {meanU:+0.00;-0.00;0.00} dB, mean absolute {meanAbsU:F2} dB, worst {worstU:F2} dB.");
        Assert.True(Math.Abs(mean) <= 0.5 && meanAbs <= 0.5, $"converted profile is {mean:F2} dB off on average");
        Assert.True(worst <= WorstBinBoundDb, $"one bin is {worst:F2} dB off");
        Assert.False(Math.Abs(meanU) <= 0.5 && meanAbsU <= 0.5, "the uncorrected profile also passed: the test cannot see the correction");
    }

    private const double WorstBinBoundDb = 2.0;

    /// <summary>
    /// White noise cannot tell "by frequency" from "by bin number", because
    /// it is flat. A steady 3 kHz whistle in the captured noise can: it sits
    /// in capture bin 64 (3000 / 46.875) and must land in new bin 60
    /// (3000 / 50), where the new engine's own measurement puts it.
    /// </summary>
    [Fact]
    public void ConversionIsByFrequencyNotByBinNumber()
    {
        var noise = WhiteNoise(seconds: 3, rms: 0.01, seed: 3000);
        for (int n = 0; n < noise.Length; n++) noise[n] += (float)(0.1 * Math.Sin(2 * Math.PI * 3000 * n / Rate));
        var capture = CaptureProfile(noise, seconds: 3);
        var converted = CapturedNoiseWolaEngine.ConvertProfile(capture, 1024);
        var native = CapturedNoiseWolaEngine.MeasureMeanMagnitudes(noise);
        int ArgMax(double[] a) => Array.IndexOf(a, a.Take(481).Max());
        Assert.Equal(64, ArgMax(capture));
        Assert.Equal(60, ArgMax(native));
        Assert.Equal(60, ArgMax(converted));
        double peak = 20 * Math.Log10(converted[60] / native[60]);
        _output.WriteLine($"A 3 kHz whistle: capture bin {ArgMax(capture)}, native bin {ArgMax(native)}, converted bin {ArgMax(converted)}; converted peak {peak:+0.00;-0.00;0.00} dB against native.");
        Assert.InRange(peak, -3, 3);
    }

    [Fact]
    public void AnOldSavedProfileWithNoFftSizeLoadsAndConverts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-wola-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mags = CaptureProfile(WhiteNoise(seconds: 1.5, rms: 0.05, seed: 7));
            string file = Path.Combine(dir, "old.xml");
            // An older writer's shape: no FftSize element at all.
            File.WriteAllText(file,
                "<?xml version=\"1.0\"?>\n<NoiseProfile><Name>old</Name><Band>40m</Band><Antenna /><CapturedUtc>2026-03-20T00:00:00Z</CapturedUtc><SampleRate>48000</SampleRate><Magnitudes>"
                + string.Concat(mags.Select(m => "<double>" + m.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "</double>"))
                + "</Magnitudes></NoiseProfile>");
            var data = Load(file);
            Assert.Equal(0, data.FftSize);
            Assert.Equal(mags, data.Magnitudes);
            var converted = CapturedNoiseWolaEngine.ConvertProfile(data.Magnitudes!, data.FftSize);
            Assert.Equal(CapturedNoiseWolaEngine.ConvertProfile(mags, 1024), converted);
            Assert.Equal(481, converted.Length);

            // And through the path the app uses: the capture provider loads it,
            // the engine takes the array it holds.
            var provider = new SpectralSubtractionProvider(48000, 1);
            Assert.True(provider.LoadProfile(file));
            using var e = new CapturedNoiseWolaEngine();
            e.SetProfile(provider.GetChannelProfile(0));
            Assert.True(e.HasProfile);

            // A half spectrum (513 values) is understood as well.
            var halfSpectrum = mags.Take(513).ToArray();
            Assert.Equal(converted, CapturedNoiseWolaEngine.ConvertProfile(halfSpectrum, 1024));
            Assert.Equal(converted, CapturedNoiseWolaEngine.ConvertProfile(halfSpectrum, 0));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- Noise removal, old against new ----

    [Fact]
    public void ItRemovesNoiseAtLeastAsWellAsTheOldEngine()
    {
        var signal = ToneBurstsInNoise(seconds: 10, out bool[] toneOn);
        var profile = CaptureProfile(WhiteNoise(seconds: 3, rms: 0.02, seed: 1808), seconds: 3);

        var oldEngine = new SpectralCaptureEngine(); oldEngine.SetProfile(profile);
        var newEngine = new CapturedNoiseWolaEngine(); newEngine.SetProfile(profile);
        var oldAt0 = new SpectralCaptureEngine(); oldAt0.SetProfile(profile); oldAt0.TrySetSetting("subtraction_amount", new(0));
        var newAt0 = new CapturedNoiseWolaEngine(); newAt0.SetProfile(profile); newAt0.TrySetSetting("subtraction_amount", new(0));
        int oldDelay = PeakLag(signal, RunCopy(new SpectralCaptureEngine(), profile, signal), 4800);
        var o = Measure(oldEngine, signal, toneOn, delay: oldDelay);
        var n = Measure(newEngine, signal, toneOn, delay: 480);
        var o0 = Measure(oldAt0, signal, toneOn, delay: oldDelay);
        var n0 = Measure(newAt0, signal, toneOn, delay: 480);
        _output.WriteLine($"Default settings (0.7, 0.02): old engine takes the noise between bursts down {o.NoiseDropDb:F1} dB, tone {o.ToneChangeDb:+0.00;-0.00;0.00} dB; new engine {n.NoiseDropDb:F1} dB, tone {n.ToneChangeDb:+0.00;-0.00;0.00} dB.");
        _output.WriteLine($"At subtraction 0, for the baseline: old {o0.NoiseDropDb:F1} dB, tone {o0.ToneChangeDb:+0.00;-0.00;0.00} dB (its ripple alone); new {n0.NoiseDropDb:F1} dB, tone {n0.ToneChangeDb:+0.00;-0.00;0.00} dB.");
        // The fair comparison is how far the noise falls RELATIVE TO THE TONE:
        // the old engine's ripple takes about 2.5 dB off everything it passes,
        // the tone included, so its raw noise figure is flattered by a loss
        // the wanted signal pays too. Both raw figures are printed above and
        // recorded in the track report; the raw comparison favours the old
        // engine, and that is a finding, not something this test hides.
        double oldSeparation = o.NoiseDropDb + o.ToneChangeDb, newSeparation = n.NoiseDropDb + n.ToneChangeDb;
        _output.WriteLine($"Noise against tone (noise drop minus tone loss): old {oldSeparation:F1} dB, new {newSeparation:F1} dB.");
        Assert.True(newSeparation >= oldSeparation - 0.5, $"the new engine separates noise from tone less well than the old one ({newSeparation:F1} against {oldSeparation:F1} dB)");
        Assert.True(n.NoiseDropDb >= 3, "the new engine does not remove noise");
        Assert.InRange(n.ToneChangeDb, -1, 1);
        Assert.InRange(n0.NoiseDropDb, -0.1, 0.1);
    }

    // ---- Real time, settings, lifetime ----

    [Fact]
    public void NothingIsAllocatedPerFrame()
    {
        using var e = new CapturedNoiseWolaEngine();
        var profile = Enumerable.Repeat(0.01, 1024).ToArray();
        e.SetProfile(profile);
        var noise = WhiteNoise(seconds: 1, rms: 0.05, seed: 3);
        var output = new float[Block];
        void OneBuffer(int i)
        {
            e.SetProfile(profile); // the pipeline offers the same array on every buffer
            e.TrySetSetting("subtraction_amount", new(0.7));
            e.TrySetSetting("spectral_floor", new(0.02));
            e.ProcessFrame(noise.AsSpan((i % 90) * Block, Block), output);
        }
        for (int i = 0; i < 50; i++) OneBuffer(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 3000; i++) OneBuffer(i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        // Without a profile too.
        e.SetProfile(null);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) e.ProcessFrame(noise.AsSpan((i % 90) * Block, Block), output);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void CpuPerFrameIsReported()
    {
        using var e = new CapturedNoiseWolaEngine();
        e.SetProfile(Enumerable.Repeat(0.01, 1024).ToArray());
        var noise = WhiteNoise(seconds: 10, rms: 0.05, seed: 12);
        var output = new float[Block];
        for (int pos = 0; pos + Block <= Rate; pos += Block) e.ProcessFrame(noise.AsSpan(pos, Block), output);
        var watch = Stopwatch.StartNew();
        int frames = 0;
        for (int pos = 0; pos + Block <= noise.Length; pos += Block, frames++) e.ProcessFrame(noise.AsSpan(pos, Block), output);
        watch.Stop();
        double us = watch.Elapsed.TotalMilliseconds * 1000 / frames;
        _output.WriteLine($"New captured-noise engine CPU: {us:F1} microseconds per 10 ms frame, {(Environment.Is64BitProcess ? "x64" : "x86")}, one channel.");
        Assert.True(us < 10_000);
    }

    [Fact]
    public void ItsSettingsAreTheOldEnginesAndRefuseTheSameThings()
    {
        Assert.Equal(SpectralCaptureEngine.SettingDescriptors, CapturedNoiseWolaEngine.SettingDescriptors);
        using var e = new CapturedNoiseWolaEngine();
        Assert.Equal("spectral_capture", e.Id);
        Assert.Equal(480, e.FrameLength);
        var before = e.GetSetting("subtraction_amount");
        Assert.False(e.TrySetSetting("unknown", new(0.5)));
        Assert.False(e.TrySetSetting("subtraction_amount", new(-1)));
        Assert.False(e.TrySetSetting("subtraction_amount", new(0.705)));
        Assert.False(e.TrySetSetting("subtraction_amount", new(double.NaN)));
        Assert.False(e.TrySetSetting("subtraction_amount", new(0, "wrong")));
        Assert.False(e.TrySetSetting("spectral_floor", new(0.3)));
        Assert.Throws<ArgumentException>(() => e.GetSetting("unknown"));
        Assert.Equal(before, e.GetSetting("subtraction_amount"));
        Assert.True(e.TrySetSetting("subtraction_amount", new(0.5)));
        Assert.Equal(0.5, e.GetSetting("subtraction_amount").Number, 6);
    }

    [Fact]
    public void TheSameProfileArrayIsNeverConvertedTwice()
    {
        using var e = new CapturedNoiseWolaEngine();
        var profile = Enumerable.Repeat(0.01, 1024).ToArray();
        e.SetProfile(profile);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) e.SetProfile(profile);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        e.SetProfile((double[])profile.Clone()); // a new array is a new profile
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before > 0);
        e.SetProfile(null);
        Assert.False(e.HasProfile);
    }

    // ---- Helpers ----

    private static NoiseProfileData Load(string file)
    {
        using var stream = File.OpenRead(file);
        return (NoiseProfileData)new XmlSerializer(typeof(NoiseProfileData)).Deserialize(stream)!;
    }

    /// <summary>A profile captured through the real capture path (the step zero provider's sampling).</summary>
    private static double[] CaptureProfile(float[] noise, int seconds = 1)
    {
        var provider = new SpectralSubtractionProvider(48000, 1);
        provider.StartSampling(seconds);
        var buffer = new float[Block];
        for (int pos = 0; pos + Block <= noise.Length && provider.IsSampling; pos += Block)
        {
            noise.AsSpan(pos, Block).CopyTo(buffer);
            provider.ProcessInPlace(buffer, 0, Block, 1);
        }
        Assert.False(provider.IsSampling, "the capture did not finish on the noise given");
        return provider.GetChannelProfile(0)!;
    }

    private static (double Mean, double MeanAbs, double Worst) Compare(double[] a, double[] b)
    {
        double sum = 0, sumAbs = 0, worst = 0; int n = 0;
        // Skip DC and Nyquist: real-valued bins, a different distribution, and
        // nothing an operator hears.
        for (int k = 1; k < a.Length - 1; k++)
        {
            double d = 20 * Math.Log10(a[k] / b[k]);
            sum += d; sumAbs += Math.Abs(d); worst = Math.Max(worst, Math.Abs(d)); n++;
        }
        return (sum / n, sumAbs / n, worst);
    }

    private static float[] RunCopy(IDenoiseEngine e, double[] profile, float[] input)
    {
        using (e) { ((SpectralCaptureEngine)e).SetProfile(profile); return Run(e, input); }
    }

    private static float[] Run(IDenoiseEngine e, float[] input)
    {
        var output = new float[input.Length / Block * Block];
        for (int pos = 0; pos + Block <= output.Length; pos += Block)
            e.ProcessFrame(input.AsSpan(pos, Block), output.AsSpan(pos, Block));
        return output;
    }

    private static float[] Sine(double seconds, double hz, double amplitude)
    {
        var a = new float[(int)(seconds * Rate)];
        for (int n = 0; n < a.Length; n++) a[n] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * n / Rate));
        return a;
    }

    private static float[] WhiteNoise(double seconds, double rms, int seed)
    {
        var rng = new Random(seed);
        var a = new float[(int)(seconds * Rate)];
        double scale = rms * Math.Sqrt(12);
        for (int i = 0; i < a.Length; i++) a[i] = (float)((rng.NextDouble() - 0.5) * scale);
        return a;
    }

    /// <summary>A 1 kHz tone at 0.2 peak, half a second on and half off, over white noise at 0.02 RMS.</summary>
    private static float[] ToneBurstsInNoise(double seconds, out bool[] toneOn)
    {
        var a = WhiteNoise(seconds, 0.02, seed: 808);
        toneOn = new bool[a.Length];
        for (int n = 0; n < a.Length; n++)
        {
            toneOn[n] = (n / (Rate / 2)) % 2 == 0;
            if (toneOn[n]) a[n] += (float)(0.2 * Math.Sin(2 * Math.PI * 1000 * n / Rate));
        }
        return a;
    }

    /// <summary>
    /// The envelope of a steady sine, one 48-sample cycle at a time: its
    /// swing in dB, and the period of that swing in samples (from the
    /// envelope's strongest repeat between 200 and 2,000 samples).
    /// </summary>
    private static (double SwingDb, double PeriodSamples) Ripple(float[] x, int from, int to)
    {
        int cycle = 48;
        var env = new System.Collections.Generic.List<double>();
        for (int s = from; s + cycle <= to; s += cycle)
        {
            double sum = 0;
            for (int i = 0; i < cycle; i++) sum += (double)x[s + i] * x[s + i];
            env.Add(Math.Sqrt(2 * sum / cycle));
        }
        double max = env.Max(), min = env.Min();
        double mean = env.Average();
        int bestLag = 0; double bestV = double.MinValue;
        for (int lag = 200 / cycle; lag <= 2000 / cycle; lag++)
        {
            double v = 0;
            for (int i = 0; i + lag < env.Count; i++) v += (env[i] - mean) * (env[i + lag] - mean);
            if (v > bestV) { bestV = v; bestLag = lag; }
        }
        // Refine the period: count envelope minima over the span.
        int minima = 0;
        for (int i = 1; i + 1 < env.Count; i++) if (env[i] < env[i - 1] && env[i] <= env[i + 1] && env[i] < mean) minima++;
        double period = minima > 1 ? (double)(env.Count * cycle) / minima : bestLag * cycle;
        return (20 * Math.Log10(max / Math.Max(min, 1e-12)), period);
    }

    private static (double NoiseDropDb, double ToneChangeDb) Measure(IDenoiseEngine e, float[] input, bool[] toneOn, int delay)
    {
        float[] output;
        using (e) output = Run(e, input);
        double gapIn = 0, gapOut = 0; int gapN = 0;
        double toneIn = 0, toneOut = 0; int bursts = 0;
        int half = Rate / 2, guard = Rate / 10;
        for (int start = input.Length / 2 / half * half; start + half + delay <= output.Length; start += half)
        {
            int from = start + guard, len = half - 2 * guard;
            if (toneOn[start])
            {
                toneIn += Goertzel(input, from, len, 1000);
                toneOut += Goertzel(output, from + delay, len, 1000);
                bursts++;
            }
            else
            {
                for (int i = 0; i < len; i++)
                {
                    gapIn += (double)input[from + i] * input[from + i];
                    gapOut += (double)output[from + delay + i] * output[from + delay + i];
                }
                gapN += len;
            }
        }
        Assert.True(gapN > 0 && bursts > 0);
        return (10 * Math.Log10(gapIn / Math.Max(gapOut, 1e-30)), 20 * Math.Log10(toneOut / toneIn));
    }

    private static double Goertzel(float[] x, int from, int len, double hz)
    {
        double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = 0; i < len; i++) { double s0 = x[from + i] + c * s1 - s2; s2 = s1; s1 = s0; }
        return 2 * Math.Sqrt(Math.Max(s1 * s1 + s2 * s2 - c * s1 * s2, 0)) / len;
    }

    private static float[] ReferenceSpeech(double startSeconds, double seconds)
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");
        using var reader = new WaveFileReader(path);
        var sp = reader.ToSampleProvider();
        var all = new float[(int)reader.SampleCount];
        int got = 0, n;
        while (got < all.Length && (n = sp.Read(all.AsSpan(got))) > 0) got += n;
        int start = (int)(startSeconds * 48000);
        int len = Math.Min((int)(seconds * 48000), got - start);
        return all.AsSpan(start, len).ToArray();
    }

    private static int PeakLag(float[] a, float[] b, int maxLag)
    {
        int n = Math.Min(a.Length, b.Length) - maxLag;
        int best = 0; double bestV = double.MinValue;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (double)a[i] * b[i + lag];
            if (sum > bestV) { bestV = sum; best = lag; }
        }
        return best;
    }
}
