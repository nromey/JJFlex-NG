using System;
using System.Collections.Generic;
using RNNoise.NET;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The per-channel bank behind <see cref="NoiseReductionProvider"/>: one
/// engine per channel, partial frames carried rather than passed through, and
/// a delay that is zero on frame-aligned input and fixed otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Most of these use a deterministic stand-in engine whose output depends on
/// the previous frame as well as the current one, so a frame processed out of
/// order, twice, on the wrong channel or not at all shows up as a wrong
/// number rather than passing by luck. The real RNNoise engine is run where
/// the claim is about it specifically.
/// </para>
/// <para>
/// <b>Windowless</b>: audio arithmetic only, on an MTA thread where WPF cannot
/// construct an element (asserted in each test).
/// </para>
/// </remarks>
public sealed class PerChannelDenoiserBankTests
{
    private const int F = 480;

    /// <summary>
    /// out[i] = -0.5 * in[i] + 0.25 * (last input sample of the previous
    /// frame) + channel tag. Stateful, channel-specific, and never equal to
    /// its input for non-trivial audio.
    /// </summary>
    private sealed class FakeEngine : INeuralDenoiseEngine
    {
        private float _carry;
        public readonly List<float[]> Seen = new();
        public float Tag;
        public string Id => "fake";
        public int SampleRate => 48000;
        public int FrameLength => F;
        public bool IsAvailable { get; set; } = true;
        public void ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            Seen.Add(input.ToArray());
            for (int i = 0; i < F; i++)
                output[i] = -0.5f * input[i] + 0.25f * _carry + Tag;
            _carry = input[F - 1];
        }
        public void Reset() { _carry = 0; Seen.Clear(); }
        public void Dispose() { }
    }

    /// <summary>The fake engine run offline over one whole channel: the reference every test compares against.</summary>
    private static float[] FakeReference(float[] mono, float tag)
    {
        var e = new FakeEngine { Tag = tag };
        var outp = new float[mono.Length / F * F];
        for (int pos = 0; pos + F <= mono.Length; pos += F)
            e.ProcessFrame(mono.AsSpan(pos, F), outp.AsSpan(pos, F));
        return outp;
    }

    private static Func<INeuralDenoiseEngine> FakeFactory(List<FakeEngine>? made = null)
    {
        int n = 0;
        return () =>
        {
            var e = new FakeEngine { Tag = 0.001f * (n++) };
            made?.Add(e);
            return e;
        };
    }

    private static float[] RandomStereo(int frames, int seed)
    {
        var rng = new Random(seed);
        var s = new float[frames * 2];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(rng.NextDouble() * 1.6 - 0.8);
        return s;
    }

    private static float[] Channel(float[] interleaved, int ch, int channels = 2)
    {
        var m = new float[interleaved.Length / channels];
        for (int i = 0; i < m.Length; i++) m[i] = interleaved[i * channels + ch];
        return m;
    }

    private static float[] RunChunks(PerChannelDenoiserBank bank, float[] input, IEnumerable<int> chunkFrames, float strength)
    {
        var output = (float[])input.Clone();
        int pos = 0;
        foreach (int frames in chunkFrames)
        {
            int len = Math.Min(frames * 2, output.Length - pos);
            if (len <= 0) break;
            bank.Process(output.AsSpan(pos, len), strength);
            pos += len;
        }
        Assert.Equal(output.Length, pos);
        return output;
    }

    // ------------------------------------------------------------------

    [Fact]
    public void FrameAlignedBuffersAddNoDelayAndMatchTheEngineFrameForFrame()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var bank = new PerChannelDenoiserBank(FakeFactory(), 2);
        var input = RandomStereo(F * 20, seed: 1);

        // The live receive path: one 10 ms packet (480 frames) per call.
        var output = RunChunks(bank, input, Repeat(F, 20), strength: 1f);

        Assert.Equal(0, bank.LatencySamples);
        for (int ch = 0; ch < 2; ch++)
        {
            var expected = FakeReference(Channel(input, ch), 0.001f * ch);
            Assert.Equal(expected, Channel(output, ch));
        }
    }

    [Fact]
    public void OddSizedBuffersGiveExactlyWhatOneLargeBufferGives()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        int frames = F * 23 + 211; // deliberately not a whole number of engine frames
        var input = RandomStereo(frames, seed: 2);

        using var whole = new PerChannelDenoiserBank(FakeFactory(), 2);
        var one = RunChunks(whole, input, new[] { frames }, strength: 0.8f);

        using var pieces = new PerChannelDenoiserBank(FakeFactory(), 2);
        var odd = RunChunks(pieces, input, Cycle(new[] { 100, 1000, 37, 1, 961, 479, 481, 3000 }), strength: 0.8f);

        Assert.Equal(F - 1, whole.LatencySamples);
        Assert.Equal(F - 1, pieces.LatencySamples);
        Assert.Equal(one, odd);
    }

    [Fact]
    public void OddSizedBuffersThroughRealRnnoiseMatchOneLargeBuffer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        int frames = F * 30 + 17;
        var input = RandomStereo(frames, seed: 3);
        for (int i = 0; i < input.Length; i++) input[i] *= 0.1f;

        using var whole = new PerChannelDenoiserBank(() => new RnnoiseEngine(), 2);
        using var pieces = new PerChannelDenoiserBank(() => new RnnoiseEngine(), 2);
        Assert.True(whole.IsAvailable, "RNNoise did not load, so this proves nothing");

        var one = RunChunks(whole, input, new[] { frames }, strength: 0.8f);
        var odd = RunChunks(pieces, input, Cycle(new[] { 333, 480, 1, 1200, 77 }), strength: 0.8f);

        for (int i = 0; i < one.Length; i++)
            Assert.True(Math.Abs(one[i] - odd[i]) <= 1e-6f, $"sample {i}: {one[i]} vs {odd[i]}");
    }

    [Fact]
    public void NothingPassesThroughUnprocessedWhateverTheBufferSizes()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        int frames = F * 40;
        var input = RandomStereo(frames, seed: 4);
        var rng = new Random(44);
        var sizes = new List<int>();
        for (int total = 0; total < frames; ) { int k = rng.Next(1, 1500); sizes.Add(k); total += k; }

        using var bank = new PerChannelDenoiserBank(FakeFactory(), 2);
        var output = RunChunks(bank, input, sizes, strength: 1f);
        int lat = bank.LatencySamples;
        Assert.Equal(F - 1, lat);

        for (int ch = 0; ch < 2; ch++)
        {
            var reference = FakeReference(Channel(input, ch), 0.001f * ch);
            var got = Channel(output, ch);
            // The silent lead, then every sample the engine's own output for
            // the input `lat` samples earlier. A sample passed through would
            // equal its input instead, and the fake engine never does that.
            for (int t = 0; t < lat; t++)
                Assert.Equal(0f, got[t]);
            for (int t = lat; t < got.Length; t++)
                Assert.Equal(reference[t - lat], got[t]);
        }
    }

    [Fact]
    public void StrengthZeroIsTheDryInputDelayedWithTheWet()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var input = RandomStereo(F * 6 + 5, seed: 5);
        using var bank = new PerChannelDenoiserBank(FakeFactory(), 2);
        var output = RunChunks(bank, input, Cycle(new[] { 250, 731 }), strength: 0f);
        int lat = bank.LatencySamples;
        for (int t = lat * 2; t < output.Length; t++)
            Assert.Equal(input[t - lat * 2], output[t]);
    }

    [Fact]
    public void EachChannelsEngineSeesOnlyItsOwnChannel()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var made = new List<FakeEngine>();
        using var bank = new PerChannelDenoiserBank(FakeFactory(made), 2);
        var input = new float[F * 2 * 3];
        for (int i = 0; i < F * 3; i++) { input[2 * i] = 0.25f; input[2 * i + 1] = -0.5f; }

        RunChunks(bank, input, Repeat(F, 3), strength: 1f);

        Assert.Equal(2, made.Count);
        Assert.All(made[0].Seen, frame => Assert.All(frame, s => Assert.Equal(0.25f, s)));
        Assert.All(made[1].Seen, frame => Assert.All(frame, s => Assert.Equal(-0.5f, s)));
        Assert.Equal(3, made[0].Seen.Count);
        Assert.Equal(3, made[1].Seen.Count);
    }

    [Fact]
    public void AMisalignedBufferMidStreamCostsOneGapThenAFixedDelay()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var input = RandomStereo(F * 10, seed: 6);
        using var bank = new PerChannelDenoiserBank(FakeFactory(), 2);

        // Four aligned packets, then a misaligned one, then the rest.
        var output = RunChunks(bank, input, new[] { F, F, F, F, 100, F * 6 - 100 }, strength: 1f);
        Assert.Equal(F - 1, bank.LatencySamples);

        var reference = FakeReference(Channel(input, 0), 0f);
        var got = Channel(output, 0);
        int switchAt = F * 4;
        for (int t = 0; t < switchAt; t++)
            Assert.Equal(reference[t], got[t]);            // zero delay while aligned
        for (int t = switchAt; t < switchAt + F - 1; t++)
            Assert.Equal(0f, got[t]);                      // the one gap
        for (int t = switchAt + F - 1; t < got.Length; t++)
            Assert.Equal(reference[t - (F - 1)], got[t]);  // fixed delay after
    }

    [Fact]
    public void ClearCarryReturnsToZeroDelay()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var bank = new PerChannelDenoiserBank(FakeFactory(), 2);
        var buf = RandomStereo(300, seed: 7);
        bank.Process(buf, 1f);
        Assert.Equal(F - 1, bank.LatencySamples);
        bank.ClearCarry();
        Assert.Equal(0, bank.LatencySamples);
    }

    [Fact]
    public void AnUnavailableEngineMakesTheBankUnavailable()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        int n = 0;
        using var bank = new PerChannelDenoiserBank(() => new FakeEngine { IsAvailable = n++ == 0 }, 2);
        Assert.False(bank.IsAvailable);
    }

    /// <summary>
    /// "Behaviour unchanged" for the case that was always right: with both
    /// channels identical (one slice, centred; and the transmit chain, which
    /// duplicates the mono microphone), the provider's output is bit-for-bit
    /// what the pre-Track-G code produced. The old algorithm is restated here
    /// verbatim against the same wrapper.
    /// </summary>
    [Fact]
    public void IdenticalChannelsComeOutBitForBitAsTheOldProviderMadeThem()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var rng = new Random(8);
        int frames = F * 25;
        var input = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(0.2 * Math.Sin(i * 0.05) + (rng.NextDouble() - 0.5) * 0.05);
            input[2 * i] = v;
            input[2 * i + 1] = v;
        }

        var expected = (float[])input.Clone();
        OldProcessSamples(expected, frames * 2, 2, strength: 0.8f);

        using var nr = new NoiseReductionProvider(48000, 2) { Enabled = true };
        var got = (float[])input.Clone();
        for (int pos = 0; pos < got.Length; pos += F * 2)
            nr.ProcessInPlace(got, pos, F * 2, 2);

        Assert.Equal(expected, got);
    }

    /// <summary>The pre-Track-G NoiseReductionProvider.ProcessSamples, restated.</summary>
    private static void OldProcessSamples(float[] buffer, int count, int channels, float strength)
    {
        using var denoiser = new Denoiser();
        var frameBuffer = new float[F];
        var originalBuffer = new float[F];
        int sampleIdx = 0, remaining = count;
        while (remaining >= F * channels)
        {
            for (int i = 0; i < F; i++)
            {
                float sample = buffer[sampleIdx + i * channels];
                originalBuffer[i] = sample;
                frameBuffer[i] = sample * 32767f;
            }
            denoiser.Denoise(new Span<float>(frameBuffer));
            for (int i = 0; i < F; i++)
            {
                float processed = frameBuffer[i] / 32767f;
                float blended = originalBuffer[i] * (1f - strength) + processed * strength;
                int dst = sampleIdx + i * channels;
                for (int ch = 0; ch < channels; ch++) buffer[dst + ch] = blended;
            }
            sampleIdx += F * channels;
            remaining -= F * channels;
        }
    }

    private static IEnumerable<int> Repeat(int value, int times)
    {
        for (int i = 0; i < times; i++) yield return value;
    }

    private static IEnumerable<int> Cycle(int[] values)
    {
        for (int i = 0; ; i++) yield return values[i % values.Length];
    }
}
