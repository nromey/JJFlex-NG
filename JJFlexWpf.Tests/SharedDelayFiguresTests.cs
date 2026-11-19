using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P, phase 0 (#873): every computer engine's receive delay,
/// MEASURED from the engine itself, and the shared delay each family would be
/// padded to (Noel 2026-11-04 19:57: "the adjusted delay vs. individual
/// delays"). For each engine: the delay it reports, the delay a bank of two
/// channels on 10 ms buffers adds, and the content lag found by
/// cross-correlating real speech in and out; for a helper engine also its
/// buffering (k frames) and the algorithmic part the helper answered with.
/// The figures are written to the test output for the track's report; the
/// assertions are only that each figure the app uses agrees with what was
/// measured. Windowless and silent: engines and the hidden helper only.
/// </summary>
public sealed class SharedDelayFiguresTests
{
    private readonly ITestOutputHelper _out;
    public SharedDelayFiguresTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private sealed record Figure(string Family, string Engine, string Model, int Reported, int Bank, int? ContentLag, string Note);

    [Fact]
    public void Every_receive_engine_s_delay_measured_and_the_shared_delay_per_family()
    {
        var figures = new List<Figure>();
        float[] speech = ReferenceSpeech(startSeconds: 12, seconds: 4);

        foreach (string model in RnnoiseModels.All)
            Measure(figures, "Neural", "rnnoise", model, () => new RnnoiseEngine(model: model), speech);
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string native = Path.Combine(RepoPaths.RequireRoot(), "runtimes", $"win-{arch}", "native");
        Measure(figures, "Neural", "deepfilternet3", "", () => new DeepFilterNet3Engine(libraryPath: Path.Combine(native, DeepFilterNet3Engine.LibraryFileName)), speech);
        Measure(figures, "Spectral", "nr4", "", () => new SpecbleachEngine(libraryPath: Path.Combine(native, SpecbleachEngine.LibraryFileName)), speech);
        Measure(figures, "Spectral", "spectral_capture", "with a profile", () =>
        {
            var e = new SpectralCaptureEngine();
            e.SetProfile(Enumerable.Repeat(1e-6, 1024).ToArray());
            return e;
        }, speech);

        string? helper = HelperLifecycleTests.RealHelper();
        if (helper == null) _out.WriteLine("HELPER NOT MEASURED: set JJFLEX_WDSP_HELPER to the helper's path");
        else
        {
            using var rig = new HelperTestRig(path: helper);
            foreach (string model in new[] { "standard", "premium", "standard_cw", "premium_cw" })
                MeasureHelper(figures, rig, "Neural", "nnr", model, speech);
            MeasureHelper(figures, rig, "Spectral", "nr2", null, speech);
        }

        _out.WriteLine("ENGINE FIGURES (samples at 48 kHz; ms rounded)");
        foreach (var f in figures)
            _out.WriteLine($"  {f.Family} {f.Engine} {f.Model}: reported {f.Reported} ({Ms(f.Reported)} ms), bank on 10 ms buffers {f.Bank} ({Ms(f.Bank)} ms), " +
                $"content lag {(f.ContentLag is int c ? c + $" ({Ms(c)} ms)" : "not measured")}{(f.Note.Length > 0 ? "; " + f.Note : "")}");

        foreach (string family in new[] { "Neural", "Spectral" })
        {
            var members = figures.Where(f => f.Family == family && f.Bank > 0).ToList();
            if (members.Count == 0) continue;
            int shared = members.Max(f => f.Bank);
            var slowest = members.First(f => f.Bank == shared);
            _out.WriteLine($"SHARED DELAY, {family}, if every measured engine were in the set: {shared} ({Ms(shared)} ms), set by {slowest.Engine} {slowest.Model}");
            foreach (var f in members)
                _out.WriteLine($"  {f.Engine} {f.Model}: own {Ms(f.Bank)} ms, padded by {shared - f.Bank} samples ({Ms(shared - f.Bank)} ms)");
            var withoutSlowest = members.Where(f => f.Engine != slowest.Engine).ToList();
            if (withoutSlowest.Count > 0)
            {
                int next = withoutSlowest.Max(f => f.Bank);
                _out.WriteLine($"  without {slowest.Engine}: {next} ({Ms(next)} ms)");
            }
        }

        // What the app uses before an engine runs agrees with the engine itself.
        foreach (var f in figures)
        {
            int? expected = ReceiveSharedDelay.ExpectedSamples(f.Engine, f.Model.Length == 0 || f.Engine == "spectral_capture" ? null : f.Model);
            // Exact for the in-process engines; a helper engine's expected figure
            // is rounded milliseconds, so within half a millisecond either way.
            int slack = f.Engine is "nnr" or "nr2" ? ReceiveSharedDelay.RoundingSamples : 0;
            if (expected is int e)
                Assert.InRange(f.Reported, e - slack, e + slack);
            // Two channels on 10 ms buffers add no carry: the bank's delay IS the engine's.
            Assert.Equal(f.Reported, f.Bank);
            if (f.ContentLag is int lag)
                Assert.InRange(lag, f.Reported - 24, f.Reported + 24);
        }
        Assert.Contains(figures, f => f.Engine == "rnnoise");
    }

    private static int Ms(int samples) => ReceiveSharedDelay.Milliseconds(samples);

    private void Measure(List<Figure> figures, string family, string id, string model, Func<IDenoiseEngine> make, float[] speech)
    {
        IDenoiseEngine? probe;
        try { probe = make(); }
        catch (Exception ex) { _out.WriteLine($"{id} {model}: could not start ({ex.GetType().Name}: {ex.Message})"); return; }
        using (probe)
        {
            if (!probe.IsAvailable) { _out.WriteLine($"{id} {model}: unavailable here ({probe.UnavailabilityDetail})"); return; }
            int reported = probe.LatencySamples;
            int? lag = id == "spectral_capture" ? null : ContentLag(probe, speech);
            int bank = BankDelay(make);
            figures.Add(new Figure(family, id, model, reported, bank, lag, id == "spectral_capture" ? "0 with no profile loaded" : ""));
        }
    }

    private void MeasureHelper(List<Figure> figures, HelperTestRig rig, string family, string id, string? model, float[] speech)
    {
        var starting = model == null ? null : new Dictionary<string, EngineSettingValue> { ["model"] = new(0, model) };
        using var e = rig.Engine(id, starting);
        if (!e.IsAvailable) { _out.WriteLine($"{id} {model}: unavailable ({e.UnavailabilityDetail})"); return; }
        int reported = e.LatencySamples;
        int k = e.PipelineDepth;
        int algorithmic = reported - k * WdspProtocol.FrameLength;
        int lag = ContentLag(e, speech, paced: true);
        figures.Add(new Figure(family, id, model ?? "", reported, reported, lag,
            $"helper buffering k={k} frames ({k * WdspProtocol.FrameLength} samples), algorithmic {algorithmic} ({Ms(algorithmic)} ms)"));
    }

    /// <summary>What a two-channel bank on 480-frame buffers says it delays by, after a second of audio.</summary>
    private static int BankDelay(Func<IDenoiseEngine> make)
    {
        using var bank = new PerChannelDenoiserBank(make, 2);
        var buffer = new float[960];
        var rng = new Random(873);
        for (int b = 0; b < 100; b++)
        {
            for (int i = 0; i < buffer.Length; i++) buffer[i] = (float)(rng.NextDouble() - 0.5) * 0.05f;
            bank.Process(buffer, 1f);
        }
        return bank.LatencySamples;
    }

    private static int ContentLag(IDenoiseEngine e, float[] speech, bool paced = false)
    {
        int f = e.FrameLength;
        var output = new float[speech.Length / f * f];
        for (int pos = 0; pos + f <= output.Length; pos += f)
        {
            if (paced && e is WdspHelperEngine h) HelperTestRig.Pace(h);
            if (!e.ProcessFrame(speech.AsSpan(pos, f), output.AsSpan(pos, f))) output.AsSpan(pos, f).Clear();
        }
        return PeakLag(speech, output, maxLag: 6000);
    }

    internal static float[] ReferenceSpeech(double startSeconds, double seconds)
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");
        using var reader = new WaveFileReader(path);
        var sp = reader.ToSampleProvider();
        Assert.Equal(48000, sp.WaveFormat.SampleRate);
        Assert.Equal(1, sp.WaveFormat.Channels);
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
        int best = 0;
        double bestV = double.MinValue;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (double)a[i] * b[i + lag];
            if (sum > bestV) { bestV = sum; best = lag; }
        }
        return best;
    }
}
