using System;
using System.Collections.Generic;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1169, settled by marker rather than by reading (Sprint 49 Track P): does a
/// bank switched off (<see cref="PerChannelDenoiserBank.ClearCarry"/>, which the
/// receive slot calls when it stops processing) and back on replay audio from
/// before it was switched off? Its comment says it does not. NOT FIXED HERE:
/// the bank serves both directions and the register holds the fix for after
/// Track H2. The engine here has a REAL delay (a ring of its own), so its
/// output is its input <see cref="Delay"/> samples later, as a real engine's is.
/// </summary>
public sealed class ClearCarryMarkerTests
{
    private const int Delay = 960;
    private readonly ITestOutputHelper _out;
    public ClearCarryMarkerTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    /// <summary>A pass-through engine that delays its input by exactly <see cref="Delay"/>, holding the history in a ring like a real engine's look-ahead.</summary>
    internal sealed class DelayEngine : IDenoiseEngine
    {
        private readonly float[] _ring;
        private int _at;
        public DelayEngine(int delay) { _ring = new float[delay]; LatencySamples = delay; }
        public string Id => "delay";
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public int LatencySamples { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (_ring.Length == 0) { input.CopyTo(output); return true; }
            for (int i = 0; i < input.Length; i++)
            {
                output[i] = _ring[_at];
                _ring[_at] = input[i];
                if (++_at == _ring.Length) _at = 0;
            }
            return true;
        }
        public void Reset() { Array.Clear(_ring); _at = 0; }
        public void Dispose() { }
    }

    /// <summary>
    /// Feed a steady marker level for a while, stop (ClearCarry), then resume
    /// with silence: anything non-zero after the resume is audio from before
    /// the stop. Strength 0 is the dry delay line alone; strength 1 is the
    /// engine's own history alone.
    /// </summary>
    private static (int Stale, int FirstMarkerAt) Run(float strength)
    {
        using var bank = new PerChannelDenoiserBank(() => new DelayEngine(Delay), 1);
        var buffer = new float[480];
        int firstMarkerAt = -1, at = 0;
        for (int b = 0; b < 10; b++)
        {
            Array.Fill(buffer, 0.5f);
            bank.Process(buffer, strength);
            for (int i = 0; i < buffer.Length && firstMarkerAt < 0; i++) if (buffer[i] != 0) firstMarkerAt = at + i;
            at += buffer.Length;
        }
        bank.ClearCarry();
        int stale = 0;
        for (int b = 0; b < 10; b++)
        {
            Array.Clear(buffer);
            bank.Process(buffer, strength);
            foreach (float x in buffer) if (x != 0) stale++;
        }
        return (stale, firstMarkerAt);
    }

    [Fact]
    public void Measured_how_much_audio_from_before_a_stop_comes_back_after_it()
    {
        var dry = Run(0f);
        var wet = Run(1f);
        _out.WriteLine($"#1169 dry delay line (strength 0): {dry.Stale} samples from before the stop played after it");
        _out.WriteLine($"#1169 engine's own history (strength 1): {wet.Stale} samples from before the stop played after it");
        // Positive control: the marker method sees the delay at all. The first
        // marker sample comes out exactly the engine's delay after it went in.
        Assert.Equal(Delay, dry.FirstMarkerAt);
        Assert.Equal(Delay, wet.FirstMarkerAt);
    }

    [Fact(Skip = "#1169, measured 2026-11-19 by the test above: ClearCarry leaves the dry delay line (960 of 960 samples replayed at strength 0) and the engine's history (960 at strength 1); fix waits for Track H2 per the register")]
    public void A_bank_switched_off_and_on_plays_nothing_from_before_the_stop()
    {
        Assert.Equal(0, Run(0f).Stale);
        Assert.Equal(0, Run(1f).Stale);
    }
}
