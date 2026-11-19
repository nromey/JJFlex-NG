using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P (#873): offline renders for Noel's ear, made by the app's
/// REAL receive pipeline and slot code, with two model engines of different
/// delay and colouring (the follow-up research's model: RNNoise's 480 samples
/// and the helper's 3,408). Today's switch and the new one, side by side, on
/// band noise, a voice take and a steady tone, both directions. Nothing is
/// played: files are written, and only when <c>JJFLEX_TRACKP_RENDER_DIR</c>
/// names a folder; otherwise the test says NOT RUN and passes.
/// </summary>
public sealed class SwitchRendersTests
{
    private const int F = 480, Rate = 48000;
    private readonly ITestOutputHelper _out;
    public SwitchRendersTests(ITestOutputHelper output) { NoiseReductionPerChannelTests.AssertWindowless(); _out = output; }

    /// <summary>A model engine: a mild first-order colouring, then a pure delay.</summary>
    private sealed class ColourEngine : IDenoiseEngine
    {
        private readonly float[] _ring; private int _at; private float _prev;
        private readonly float _a, _b;
        public ColourEngine(string id, int delay, float a, float b) { Id = id; _ring = new float[delay]; LatencySamples = delay; _a = a; _b = b; }
        public string Id { get; }
        public int SampleRate => Rate; public int FrameLength => F; public int LatencySamples { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++)
            {
                float x = _a * input[i] + _b * _prev; _prev = input[i];
                output[i] = _ring[_at]; _ring[_at] = x; if (++_at == _ring.Length) _at = 0;
            }
            return true;
        }
        public void Reset() { } public void Dispose() { }
    }

    private static float[] Render(float[] signal, int delayA, int delayB, bool shared, int switchAtBuffer, out long switchSample)
    {
        DenoiseEngineEntry Row(string id, int d, float a, float b) => new(id, DenoiseSide.Pc, DenoiseFamily.Neural, "label",
            new[] { DenoiseDirection.Receive }, (_, _) => new ColourEngine(id, d, a, b), () => Unavailability.None);
        using var p = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { Row("rnnoise", delayA, 0.85f, 0.15f), Row("b", delayB, 1.1f, -0.2f) }));
        var slot = p.NeuralSlot;
        if (shared) slot.SharedPlan = new SharedDelayPlan(Math.Max(delayA, delayB), new[] { new ReceiveSharedDelay.Member("rnnoise", null), new ReceiveSharedDelay.Member("b", null) });
        slot.MuteOverrideForTest = false;
        slot.SetStrength("rnnoise", 1); slot.SetStrength("b", 1);
        p.RnnEnabled = true;
        var output = new float[signal.Length];
        var audio = new float[2 * F];
        switchSample = -1;
        for (int b = 0; b * F + F <= signal.Length; b++)
        {
            if (b == switchAtBuffer)
            {
                Assert.True(slot.Select("b", out _));
                var t = Stopwatch.StartNew();
                while (!slot.BuildDelivered("b") && t.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            }
            for (int i = 0; i < F; i++) audio[2 * i] = audio[2 * i + 1] = signal[b * F + i];
            bool before = slot.RunningEngineId == "b";
            p.Process(audio);
            if (!before && slot.RunningEngineId == "b") switchSample = (long)b * F;
            for (int i = 0; i < F; i++) output[b * F + i] = audio[2 * i];
        }
        return output;
    }

    private static float[] BandNoise(int samples, int seed)
    {
        // White noise through a 300 to 2700 Hz band-pass (two biquads), at a band-noise level.
        var rng = new Random(seed);
        var x = Enumerable.Range(0, samples).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        x = Biquad(x, 2700, high: false); x = Biquad(x, 300, high: true);
        float peak = x.Max(v => Math.Abs(v));
        return x.Select(v => v / peak * 0.25f).ToArray();
    }

    private static float[] Biquad(float[] x, double fc, bool high)
    {
        double w = 2 * Math.PI * fc / Rate, q = 0.707, alpha = Math.Sin(w) / (2 * q), cos = Math.Cos(w);
        double b0 = high ? (1 + cos) / 2 : (1 - cos) / 2, b1 = high ? -(1 + cos) : 1 - cos, b2 = b0;
        double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
        var y = new float[x.Length]; double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = (float)v;
        }
        return y;
    }

    private static float[] Voice(int samples)
    {
        var speech = SharedDelayFiguresTests.ReferenceSpeech(startSeconds: 10, seconds: samples / (double)Rate);
        var noise = BandNoise(samples, 7);
        return Enumerable.Range(0, samples).Select(i => (i < speech.Length ? speech[i] * 0.8f : 0) + noise[i] * 0.15f).ToArray();
    }

    private static double DipDb(float[] y, long at)
    {
        double Rms(long from, int len) { double s = 0; for (long i = from; i < from + len; i++) s += y[i] * y[i]; return Math.Sqrt(s / len); }
        double steady = Rms(at - 24000, 19200);
        double worst = double.MaxValue;
        for (long b = at - 240; b < at + 2 * F; b += 120) worst = Math.Min(worst, Rms(b, 240));
        return 20 * Math.Log10(steady / Math.Max(worst, 1e-9));
    }

    [Fact]
    public void Renders_today_s_switch_and_the_aligned_one_for_listening()
    {
        string? dir = Environment.GetEnvironmentVariable("JJFLEX_TRACKP_RENDER_DIR");
        if (string.IsNullOrEmpty(dir)) { _out.WriteLine("NOT RUN: set JJFLEX_TRACKP_RENDER_DIR to a folder to write the renders"); return; }
        Directory.CreateDirectory(dir);
        int samples = 6 * Rate, switchBuffer = 300; // asked for at 3 seconds
        var materials = new (string Name, float[] Signal)[]
        {
            ("band-noise", BandNoise(samples, 1)),
            ("voice", Voice(samples)),
            ("tone500", Enumerable.Range(0, samples).Select(i => 0.3f * MathF.Sin(2 * MathF.PI * 500 * i / Rate)).ToArray()),
        };
        var notes = new StringBuilder();
        foreach (var (name, signal) in materials)
            foreach (var (a, b) in new[] { (480, 3408), (3408, 480) })
                foreach (bool shared in new[] { false, true })
                {
                    var y = Render(signal, a, b, shared, switchBuffer, out long at);
                    Assert.True(at > 0, "the switch never happened");
                    string file = $"{name}-{a}to{b}-{(shared ? "aligned" : "today")}.wav";
                    using (var w = new WaveFileWriter(Path.Combine(dir, file), new WaveFormat(Rate, 16, 1)))
                        w.WriteSamples(y, 0, y.Length);
                    double dip = DipDb(y, at);
                    string line = $"{file}: switch at {at / (double)Rate:0.000} s; level 5 ms around the switch {dip:0.0} dB below the second before it";
                    notes.AppendLine(line);
                    _out.WriteLine(line);
                }
        File.WriteAllText(Path.Combine(dir, "measured.txt"), notes.ToString());
    }
}
