using System;
using System.Collections.Generic;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>Sample-by-sample timing tests; never constructs UI or opens a device.</summary>
public sealed class DenoiserTimingTests
{
    private const int F = 480;
    public DenoiserTimingTests() => NoiseReductionPerChannelTests.AssertWindowless();
    private static float Sample(int position, int channel = 0) => position < 0 ? 0
        : (float)((position * 37 + channel * 113) % 1009 - 504) / 1024;

    // Absolute time lets unavailable frames advance without calling the engine.
    private sealed class TimelineEngine : IDenoiseEngine
    {
        internal int Position, Delay, Channel;
        internal string Failure = "";
        internal bool Down;
        public string Id => "timeline";
        public int SampleRate => 48000;
        public int FrameLength => F;
        public int LatencySamples => Down ? 0 : Delay; // dead helpers may report zero
        public bool IsAvailable => !Down;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (Failure == "throw") { output.Fill(float.NaN); throw new InvalidOperationException("test failure"); }
            if (Failure == "late") return false;
            for (int i = 0; i < F; i++) output[i] = Sample(Position + i - Delay, Channel);
            return true;
        }
        public void Reset() { Position = 0; }
        public void Dispose() { }
    }

    // This engine actually loses its delay history over a missed frame.
    private sealed class StatefulDelay : IDenoiseEngine
    {
        private readonly float[] _line;
        private int _at;
        public bool Down;
        public string Failure = "";
        public StatefulDelay(int delay) => _line = new float[delay];
        public string Id => "stateful-delay";
        public int SampleRate => 48000;
        public int FrameLength => F;
        public int LatencySamples => _line.Length;
        public bool IsAvailable => !Down;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (Failure == "throw") throw new InvalidOperationException("history lost");
            if (Failure == "late") return false;
            for (int i = 0; i < F; i++) {
                output[i] = _line[_at]; _line[_at] = input[i];
                if (++_at == _line.Length) _at = 0;
            }
            return true;
        }
        public void Reset() { Array.Clear(_line); _at = 0; }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(480, false)] [InlineData(1440, false)] [InlineData(2448, false)]
    [InlineData(480, true)] [InlineData(1440, true)] [InlineData(2448, true)]
    public void Half_strength_is_exactly_delayed_input_including_carry(int delay, bool odd)
    {
        // Stateful fake sees all frames here, including those assembled by carry.
        using var bank = new PerChannelDenoiserBank(() => new StatefulDelay(delay), 1);
        int total = F * 40;
        var stream = new float[total];
        for (int i = 0; i < total; i++) stream[i] = Sample(i);
        int packet = odd ? 137 : F;
        for (int at = 0; at < total; at += packet)
            bank.Process(stream.AsSpan(at, Math.Min(packet, total - at)), .5f);
        int expectedDelay = delay + (odd ? F - 1 : 0);
        for (int i = 0; i < total; i++) Assert.Equal(Sample(i - expectedDelay), stream[i]);
        Assert.Equal(expectedDelay, bank.LatencySamples);
    }

    [Theory]
    [InlineData("unavailable", 480)] [InlineData("throw", 480)] [InlineData("late", 480)]
    [InlineData("unavailable", 2448)] [InlineData("throw", 2448)] [InlineData("late", 2448)]
    public void Every_dropout_cause_keeps_each_channels_delay_through_recovery(string failure, int delay)
    {
        var engines = new List<TimelineEngine>();
        using var bank = new PerChannelDenoiserBank(() => {
            var e = new TimelineEngine { Delay = engines.Count == 0 ? delay : 1440, Channel = engines.Count };
            engines.Add(e); return e;
        }, 2);
        for (int frame = 0; frame < 35; frame++) {
            bool fail = frame >= 10 && frame < 14;
            engines[0].Down = fail && failure == "unavailable";
            engines[0].Failure = fail ? failure : "";
            foreach (var e in engines) e.Position = frame * F;
            var buffer = new float[F * 2];
            for (int i = 0; i < F; i++) for (int ch = 0; ch < 2; ch++) buffer[2 * i + ch] = Sample(frame * F + i, ch);
            bank.Process(buffer, .5f);
            for (int i = 0; i < F; i++) for (int ch = 0; ch < 2; ch++)
                Assert.Equal(Sample(frame * F + i - engines[ch].Delay, ch), buffer[2 * i + ch]);
            Assert.Equal(Math.Max(delay, 1440), bank.EngineLatencySamples);
        }
        Assert.Equal(failure == "late" ? 4 : 0, bank.LateFrames);
        Assert.Equal(failure == "late" ? 0 : 4, bank.FallbackFrames);
    }

    [Theory]
    [InlineData("unavailable")] [InlineData("throw")] [InlineData("late")]
    public void History_losing_engine_is_aligned_during_failure_and_after_its_history_refills(string failure)
    {
        const int delay = 2448;
        var e = new StatefulDelay(delay);
        using var bank = new PerChannelDenoiserBank(() => e, 1);
        for (int frame = 0; frame < 45; frame++) {
            bool fail = frame >= 12 && frame < 16;
            e.Down = fail && failure == "unavailable";
            e.Failure = fail ? failure : "";
            if (frame == 16) e.Reset();
            var buffer = new float[F];
            for (int i = 0; i < F; i++) buffer[i] = Sample(frame * F + i);
            bank.Process(buffer, .5f);
            for (int i = 0; i < F; i++)
                if (frame < 16 || frame * F + i >= 16 * F + delay)
                    Assert.Equal(Sample(frame * F + i - delay), buffer[i]);
        }
    }

    [Fact]
    public void Midstream_loss_of_all_engines_does_not_trigger_the_providers_step_aside()
    {
        var e = new TimelineEngine { Delay = 1440 };
        using var provider = new NoiseReductionProvider(48000, 1, () => e) { Enabled = true, Strength = .5f };
        for (int frame = 0; frame < 25; frame++) {
            e.Position = frame * F; e.Down = frame >= 10;
            var buffer = new float[F];
            for (int i = 0; i < F; i++) buffer[i] = Sample(frame * F + i);
            provider.ProcessInPlace(buffer, 0, F, 1);
            for (int i = 0; i < F; i++) Assert.Equal(Sample(frame * F + i - 1440), buffer[i]);
            Assert.Equal(1440, provider.AppliedLatencySamples);
        }
    }

    [Fact]
    public void Never_loaded_and_disabled_providers_leave_audio_untouched_and_report_zero_applied_delay()
    {
        var e = new TimelineEngine { Delay = 480, Down = true };
        using var provider = new NoiseReductionProvider(48000, 1, () => e) { Enabled = true };
        var samples = new float[F];
        for (int i = 0; i < F; i++) samples[i] = Sample(i);
        var before = (float[])samples.Clone();
        provider.ProcessInPlace(samples, 0, F, 1);
        Assert.Equal(before, samples); Assert.Equal(0, provider.AppliedLatencySamples);
        e.Down = false;
        provider.ProcessInPlace(samples, 0, F, 1);
        Assert.Equal(480, provider.AppliedLatencySamples);
        provider.Enabled = false; before.CopyTo(samples, 0);
        provider.ProcessInPlace(samples, 0, F, 1);
        Assert.Equal(before, samples); Assert.Equal(0, provider.AppliedLatencySamples);
        e.Down = true; provider.Enabled = true;
        provider.ProcessInPlace(samples, 0, F, 1);
        Assert.Equal(before, samples); Assert.Equal(0, provider.AppliedLatencySamples);
    }

    [Fact]
    public void Changing_latency_adopts_at_the_produced_frame_and_zeroes_the_new_dry_history()
    {
        var e = new TimelineEngine { Delay = 480 };
        using var bank = new PerChannelDenoiserBank(() => e, 1);
        for (int frame = 0; frame < 30; frame++) {
            if (frame == 8) e.Delay = 2448;
            e.Position = frame * F;
            var buffer = new float[F];
            for (int i = 0; i < F; i++) buffer[i] = Sample(frame * F + i);
            bank.Process(buffer, 0); // isolate dry, engine still runs
            int delay = frame >= 8 ? 2448 : 480;
            int historyStart = frame >= 8 ? 8 * F : 0;
            for (int i = 0; i < F; i++) {
                int raw = frame * F + i - delay;
                Assert.Equal(raw < historyStart ? 0 : Sample(raw), buffer[i]);
            }
            Assert.Equal(delay, bank.LatencySamples);
        }
    }

    [Fact]
    public void ClearCarry_keeps_engine_and_dry_history_while_Reset_zeroes_both()
    {
        using var bank = new PerChannelDenoiserBank(() => new StatefulDelay(480), 1);
        var samples = new float[F]; Array.Fill(samples, .25f);
        bank.Process(samples, .5f);
        bank.ClearCarry(); Array.Fill(samples, .75f); bank.Process(samples, .5f);
        Assert.All(samples, x => Assert.Equal(.25f, x));
        bank.Reset(); Array.Fill(samples, .75f); bank.Process(samples, .5f);
        Assert.All(samples, x => Assert.Equal(0, x));
    }

    [Fact]
    public void Strength_one_preserves_every_sample_the_fake_engine_produced()
    {
        var e = new TimelineEngine { Delay = 2448 };
        using var bank = new PerChannelDenoiserBank(() => e, 1);
        for (int frame = 0; frame < 25; frame++) {
            e.Position = frame * F;
            var buffer = new float[F];
            for (int i = 0; i < F; i++) buffer[i] = Sample(frame * F + i);
            bank.Process(buffer, 1);
            for (int i = 0; i < F; i++) Assert.Equal(Sample(frame * F + i - 2448), buffer[i]);
        }
    }

    [Fact]
    public void Steady_state_delay_lines_allocate_nothing_on_the_audio_thread()
    {
        using var bank = new PerChannelDenoiserBank(() => new StatefulDelay(2448), 2);
        var buffer = new float[960];
        for (int i = 0; i < 100; i++) bank.Process(buffer, .5f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) bank.Process(buffer, .5f);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
