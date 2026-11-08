using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E13: Listen to what was removed (ruled 2026-11-07,
/// question 13). One mechanism for every engine: the bank's dry history,
/// already delayed by the engine's own latency for the Strength blend, minus
/// the processed output. Windowless: fake engines with known delays.
/// </summary>
public sealed class NoiseResidueTests
{
    public NoiseResidueTests() => NoiseReductionPerChannelTests.AssertWindowless();

    /// <summary>An engine that delays its input by <c>actual</c> samples and halves it, while REPORTING <c>reported</c>.</summary>
    private sealed class DelayEngine : IDenoiseEngine
    {
        private readonly float[] _line;
        private int _at;
        private readonly float _gain;
        public DelayEngine(int reported, int actual, float gain = 0.5f)
        {
            LatencySamples = reported;
            _line = new float[actual];
            _gain = gain;
        }
        public string Id => "delay";
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public bool IsAvailable => true;
        public int LatencySamples { get; }
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++)
            {
                if (_line.Length == 0) { output[i] = input[i] * _gain; continue; }
                output[i] = _line[_at] * _gain;
                _line[_at] = input[i];
                if (++_at == _line.Length) _at = 0;
            }
            return true;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private static float[] Signal(int frames, int channels, int seed)
    {
        var r = new Random(seed);
        var s = new float[frames * channels];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(r.NextDouble() * 2 - 1) * 0.5f;
        return s;
    }

    /// <summary>
    /// Feeds a signal through a bank listening to what was removed, and
    /// returns the largest deviation of the output from the exact residue:
    /// half the input, delayed by the engine's TRUE delay.
    /// </summary>
    private static double ResidueError(int reported, int actual)
    {
        const int channels = 2, frames = 480 * 40;
        using var bank = new PerChannelDenoiserBank(() => new DelayEngine(reported, actual), channels) { ListenToRemoved = true };
        var input = Signal(frames, channels, 7);
        var output = new float[input.Length];
        for (int at = 0; at < frames; at += 480)
        {
            var buffer = input.AsSpan(at * channels, 480 * channels).ToArray();
            bank.Process(buffer, 0.8f); // Strength does not matter while listening
            buffer.CopyTo(output, at * channels);
        }
        double worst = 0;
        // Skip the warm-up: before the delay line is full the residue is of silence.
        for (int f = 4 * 480; f < frames; f++)
            for (int ch = 0; ch < channels; ch++)
            {
                float delayed = f - actual >= 0 ? input[(f - actual) * channels + ch] : 0f;
                float expected = delayed - delayed * 0.5f;
                worst = Math.Max(worst, Math.Abs(output[f * channels + ch] - expected));
            }
        return worst;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(480)]
    [InlineData(1440)]   // DeepFilterNet3's 30 ms
    [InlineData(1000)]   // not a whole frame
    public void The_residue_is_aligned_to_the_sample_for_any_engine_delay(int delay)
    {
        Assert.True(ResidueError(delay, delay) < 1e-6, "the residue is not the aligned input minus the output");
    }

    [Theory]
    [InlineData(480, 481)]
    [InlineData(1440, 1439)]
    public void A_residue_one_sample_out_of_alignment_fails_the_same_measure(int reported, int actual)
    {
        // Positive control: the measure above can see a one-sample error. An
        // engine that lies about its delay by one sample leaves its whole
        // signal in the "removed" audio.
        Assert.True(ResidueError(reported, actual) > 0.05, "a misaligned residue passed");
    }

    [Fact]
    public void Off_again_the_bank_blends_as_before()
    {
        using var bank = new PerChannelDenoiserBank(() => new DelayEngine(0, 0), 1);
        var buffer = Enumerable.Repeat(0.4f, 480).ToArray();
        bank.ListenToRemoved = true;
        bank.Process(buffer, 1f);
        Assert.All(buffer, v => Assert.Equal(0.2f, v, 5)); // removed: 0.4 - 0.2
        buffer = Enumerable.Repeat(0.4f, 480).ToArray();
        bank.ListenToRemoved = false;
        bank.Process(buffer, 1f);
        Assert.All(buffer, v => Assert.Equal(0.2f, v, 5)); // kept: 0.4 halved
        buffer = Enumerable.Repeat(0.4f, 480).ToArray();
        bank.Process(buffer, 0.5f);
        Assert.All(buffer, v => Assert.Equal(0.3f, v, 5)); // blended
    }

    [Fact]
    public void Listening_allocates_nothing_on_the_audio_thread()
    {
        using var bank = new PerChannelDenoiserBank(() => new DelayEngine(480, 480), 2) { ListenToRemoved = true };
        var buffer = new float[960];
        for (int i = 0; i < 50; i++) bank.Process(buffer, 1f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) bank.Process(buffer, 1f);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void The_slot_passes_listening_to_its_bank_and_the_controller_clears_it_and_never_saves_it()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr4", "test");
        f.Settle();
        var listen = f.Noise.Levers("nr4", true).Single(l => l.Kind == NoiseLeverKind.ListenRemoved);
        f.Noise.Set(listen, 1, null, "test");
        Assert.True(f.Pipeline.SpectralSlot.ListenToRemoved);
        Assert.StartsWith("You are now hearing what NR4 removed", f.Spoken.Last);
        var audio = Enumerable.Repeat(0.4f, 960).ToArray();
        f.Pipeline.Process(audio);
        Assert.All(audio, v => Assert.Equal(0.2f, v, 4)); // the fake halves: what it removed is the other half
        Assert.True(f.Pipeline.SpectralSlot.RunningBankForTest!.ListenToRemoved);

        // Never saved on: the receive config has no such field, and a connect or leaving clears it.
        var saved = new AudioOutputConfig();
        ReceiveDenoiseConfig.Map(saved, f.Pipeline, save: true);
        Assert.DoesNotContain("Listen", System.Text.Json.JsonSerializer.Serialize(saved.ReceiveSpectral), StringComparison.OrdinalIgnoreCase);
        f.Noise.ClearListening();
        Assert.False(f.Pipeline.SpectralSlot.ListenToRemoved);
        f.Noise.Set(listen, 1, null, "test");
        f.Noise.Attach("USB");
        Assert.False(f.Pipeline.SpectralSlot.ListenToRemoved);
    }
}
