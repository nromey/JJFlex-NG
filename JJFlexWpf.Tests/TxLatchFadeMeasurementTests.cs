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
/// Sprint 49 Track H, item C: the fade into the unprocessed microphone is the
/// SHORTEST that does not click (Noel 2026-11-18 08:18: "the smallest fade
/// possible"), measured rather than chosen. The real RNNoise engine cleans the
/// reference voice in steady room noise; the bank latches dry at thirty
/// different frames; the energy above the speech band (the second difference
/// of the signal, which rises 12 dB an octave) in the 2 ms around each switch
/// is compared with the louder of the two signals switched between, unswitched,
/// over the same 2 ms. A click is broadband energy neither signal had.
/// Windowless: MTA asserted, no control built.
/// </summary>
public sealed class TxLatchFadeMeasurementTests
{
    private readonly ITestOutputHelper _output;
    public TxLatchFadeMeasurementTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _output = output;
    }

    /// <summary>
    /// Click-free means a longer fade no longer measurably helps: the worst of
    /// the thirty switches is within this much of the longest candidate's.
    /// The longest fade is the instrument's own floor (what a switch costs with
    /// no click at all), so this asks "is anything still there", not "is it
    /// small enough".
    /// </summary>
    internal const double AboveFloorDb = 0.1;

    /// <summary>
    /// Every whole length from 0 to 24 samples, then longer ones up to 5 ms
    /// (repair round 1, R5). The first search tried 0, 6, 12 and 24 only, and
    /// so "the shortest" meant the shortest of four; Codex's dense search on
    /// the same fixture found a shorter one.
    /// </summary>
    internal static readonly int[] Candidates = Enumerable.Range(0, 25).Concat(new[] { 36, 48, 72, 96, 144, 240 }).ToArray();

    private sealed class LateAt : IDenoiseEngine
    {
        private readonly IDenoiseEngine _inner;
        private readonly int _late;
        private int _frame;
        public LateAt(IDenoiseEngine inner, int late) { _inner = inner; _late = late; }
        public string Id => _inner.Id;
        public bool IsAvailable => _inner.IsAvailable;
        public int SampleRate => _inner.SampleRate;
        public int FrameLength => _inner.FrameLength;
        public int LatencySamples => _inner.LatencySamples;
        public IReadOnlyList<EngineSettingDescriptor> Settings => _inner.Settings;
        public EngineSettingValue GetSetting(string name) => _inner.GetSetting(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => _inner.TrySetSetting(name, value);
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output) => _inner.ProcessFrame(input, output) && _frame++ != _late;
        public void Reset() => _inner.Reset();
        public void Dispose() => _inner.Dispose();
    }

    private static float[] VoiceInRoomNoise()
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");
        using var reader = new WaveFileReader(path);
        var source = reader.ToSampleProvider();
        Assert.Equal(48000, source.WaveFormat.SampleRate);
        var all = new float[(int)reader.SampleCount];
        int at = 0, got;
        while (at < all.Length && (got = source.Read(all.AsSpan(at))) > 0) at += got;
        var voice = all.AsSpan(12 * 48000, 2 * 48000).ToArray();
        // A shack: a fan's rumble (brown noise, which is where a fan's energy
        // sits) and mains hum, under the voice. Low-frequency noise is the hard
        // case: what the engine removes is a smooth waveform whose value at the
        // switch can be large next to the signal's own sample-to-sample
        // movement, so a cut steps. (Steady WHITE noise was tried first and
        // measured nothing at any length, the hard cut included: a step the
        // size of white noise is lost in white noise. That instrument could
        // not see a click, so it was the wrong one.)
        var rng = new Random(1118);
        float peak = voice.Max(Math.Abs);
        double brown = 0;
        for (int i = 0; i < voice.Length; i++)
        {
            brown = 0.995 * brown + (rng.NextDouble() * 2 - 1) * 0.02;
            double hum = Math.Sin(2 * Math.PI * 60 * i / 48000.0) + 0.5 * Math.Sin(2 * Math.PI * 180 * i / 48000.0);
            voice[i] += (float)(peak * (0.15 * brown + 0.03 * hum));
        }
        return voice;
    }

    private static float[] Render(float[] input, int lateFrame, int fade, out int latency)
    {
        using var bank = new PerChannelDenoiserBank(() => new LateAt(new RnnoiseEngine(), lateFrame), 1)
        { LatchFallback = true, LatchFadeSamples = fade };
        latency = bank.EngineFor(0).LatencySamples;
        var output = new float[input.Length];
        int frames = lateFrame < 0 ? input.Length / 480 : Math.Min(input.Length / 480, lateFrame + 2);
        for (int f = 0; f < frames; f++)
        {
            var buf = input.AsSpan(f * 480, 480).ToArray();
            bank.Process(buf, 1f);
            buf.CopyTo(output, f * 480);
        }
        return output;
    }

    private static double HighBand(float[] x, int from, int length)
    {
        double sum = 1e-20;
        for (int i = Math.Max(2, from); i < from + length; i++)
        {
            double d = x[i] - 2.0 * x[i - 1] + x[i - 2];
            sum += d * d;
        }
        return sum;
    }

    /// <summary>The worst excess, in dB, over thirty switch positions, for each candidate fade length.</summary>
    internal static IReadOnlyDictionary<int, double> Measure(Action<string>? log = null)
    {
        var input = VoiceInRoomNoise();
        var wet = Render(input, -1, 0, out int latency);
        var dry = new float[input.Length];
        Array.Copy(input, 0, dry, latency, input.Length - latency);
        var positions = Enumerable.Range(0, 30).Select(i => 20 + i * 5).ToArray();
        var result = new Dictionary<int, double>();
        foreach (int fade in Candidates)
        {
            var excess = new List<double>();
            foreach (int p in positions)
            {
                var switched = Render(input, p, fade, out _);
                // The 2 ms around the switch: a click is short.
                int s = p * 480, from = s - 8, length = 96;
                double reference = Math.Max(HighBand(wet, from, length), HighBand(dry, from, length));
                excess.Add(10 * Math.Log10(HighBand(switched, from, length) / reference));
            }
            excess.Sort();
            result[fade] = excess[^1];
            log?.Invoke($"fade {fade} samples ({fade / 48.0:0.###} ms): worst excess {excess[^1]:0.00} dB, median {excess[excess.Count / 2]:0.00} dB");
        }
        return result;
    }

    [Fact]
    public void The_default_latch_fade_is_the_shortest_that_measures_click_free()
    {
        var rnn = new RnnoiseEngine();
        bool available = rnn.IsAvailable;
        string detail = rnn.UnavailabilityDetail ?? "";
        rnn.Dispose();
        if (!available && !Environment.Is64BitProcess)
        {
            // #739: the RNNoise package ships a 64-bit DLL as its 32-bit one, so
            // no 32-bit run can render RNNoise. The fade is the same arithmetic on
            // both architectures; the measurement is the x64 run's.
            _output.WriteLine("Not measured on x86: RNNoise cannot load here (#739). " + detail);
            return;
        }
        Assert.True(available, detail);
        var worst = Measure(_output.WriteLine);
        double floor = worst[Candidates[^1]];
        int shortest = Candidates.First(c => worst[c] <= floor + AboveFloorDb);
        _output.WriteLine($"Floor {floor:0.00} dB. Shortest click-free: {shortest} samples. Default: {PerChannelDenoiserBank.DefaultLatchFadeSamples}.");
        // The positive control: the instrument must see the hard cut's click.
        Assert.True(worst[0] > floor + 3, "a hard cut measured click-free, so the instrument cannot see a click");
        Assert.Equal(shortest, PerChannelDenoiserBank.DefaultLatchFadeSamples);
    }
}
