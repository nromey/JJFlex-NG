using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track H, item C: a transmit engine that fails or falls behind
/// mid-over sends the unprocessed microphone at the same delay for the rest of
/// the over (never silence, never frame by frame), the switch is faded, the
/// operator is told once until it recovers, the slot is rebuilt at the unkey,
/// and the notice's earcon never reaches a transmit frame. Windowless: MTA
/// thread asserted on construction, no control built.
/// </summary>
[Collection("Earcon player statics")]
public sealed class TxLatchTests
{
    public TxLatchTests() => NoiseReductionPerChannelTests.AssertWindowless();

    /// <summary>Output is its input delayed by <see cref="LatencySamples"/>, times a gain; it can be made late or dead.</summary>
    internal sealed class DelayEngine : IDenoiseEngine
    {
        private readonly float[] _line;
        private int _at;
        private readonly float _gain;
        public int Calls;
        public int LateAt = -1;            // this call returns false
        public volatile bool Dead;         // unavailable while true
        public string Id { get; }
        public bool IsAvailable => !Dead;
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public int LatencySamples => _line.Length;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public DelayEngine(string id, float gain, int latency) { Id = id; _gain = gain; _line = new float[latency]; }
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            int call = Calls++;
            for (int i = 0; i < input.Length; i++)
            {
                float x = input[i];
                if (_line.Length == 0) { output[i] = x * _gain; continue; }
                output[i] = _line[_at] * _gain;
                _line[_at] = x;
                if (++_at == _line.Length) _at = 0;
            }
            return call != LateAt;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private static float[] Ramp(int start, int n) => Enumerable.Range(start, n).Select(i => (float)Math.Sin(i * 0.01)).ToArray();

    [Fact]
    public void A_latched_bank_goes_dry_at_the_same_delay_and_stays_dry_when_the_engine_recovers()
    {
        var engine = new DelayEngine("e", 0.5f, 480) { LateAt = 5 };
        using var bank = new PerChannelDenoiserBank(() => engine, 1) { LatchFallback = true, LatchFadeSamples = 0 };
        var all = Ramp(0, 480 * 20);
        var outAll = new float[all.Length];
        for (int b = 0; b < 20; b++)
        {
            var buf = all.AsSpan(b * 480, 480).ToArray();
            bank.Process(buf, 1f);
            buf.CopyTo(outAll, b * 480);
        }
        Assert.True(bank.Latched);
        Assert.Equal(6, engine.Calls); // the engine is not asked again once latched
        Assert.Equal(1, bank.Latches);
        Assert.Equal(14, bank.LatchedFrames);
        // Frames 1-4: wet (half the input, 480 behind). Frame 5 on: the input itself, 480 behind: no jump, never silence.
        for (int i = 480; i < 5 * 480; i++) Assert.Equal(0.5f * all[i - 480], outAll[i], 5);
        for (int i = 5 * 480; i < all.Length; i++) Assert.Equal(all[i - 480], outAll[i], 5);

        bank.ReleaseLatch();
        var after = Ramp(20 * 480, 480);
        bank.Process(after, 1f);
        Assert.Equal(7, engine.Calls);
        Assert.False(bank.Latched);
    }

    [Fact]
    public void Without_the_latch_a_bank_still_falls_back_frame_by_frame()
    {
        var engine = new DelayEngine("e", 0.5f, 0) { LateAt = 2 };
        using var bank = new PerChannelDenoiserBank(() => engine, 1);
        var outs = new List<float>();
        for (int b = 0; b < 5; b++) { var buf = Enumerable.Repeat(1f, 480).ToArray(); bank.Process(buf, 1f); outs.Add(buf[0]); }
        Assert.Equal(new[] { 0.5f, 0.5f, 1f, 0.5f, 0.5f }, outs);
        Assert.False(bank.Latched);
        Assert.Equal(0, bank.Latches);
    }

    [Fact]
    public void The_switch_into_dry_is_faded_rather_than_cut()
    {
        float Step(int fade)
        {
            var engine = new DelayEngine("e", 0.5f, 0) { LateAt = 3 };
            using var bank = new PerChannelDenoiserBank(() => engine, 1) { LatchFallback = true, LatchFadeSamples = fade };
            float last = 0, worst = 0;
            for (int b = 0; b < 6; b++)
            {
                var buf = Enumerable.Repeat(1f, 480).ToArray();
                bank.Process(buf, 1f);
                foreach (var v in buf) { if (b > 0) worst = Math.Max(worst, Math.Abs(v - last)); last = v; }
            }
            Assert.True(bank.Latched);
            return worst;
        }
        Assert.Equal(0.5f, Step(0), 5);       // a hard cut steps by everything the engine was removing
        Assert.True(Step(PerChannelDenoiserBank.DefaultLatchFadeSamples) < 0.5f / 4); // spread over the fade, not one step
    }

    /// <summary>A catalog whose neural "rnnoise" is a <see cref="DelayEngine"/> that tests can kill and count builds of.</summary>
    private sealed class Killable
    {
        public readonly ConcurrentQueue<DelayEngine> Made = new();
        public volatile bool Kill;
        public DenoiseEngineCatalog Catalog { get; }
        public DelayEngine Last => Made.Last();
        public Killable()
        {
            Catalog = new DenoiseEngineCatalog(new[]
            {
                new DenoiseEngineEntry("nr4", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.nr4",
                    new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit }, (_, _) => new DelayEngine("nr4", 1f, 0), () => Unavailability.None),
                new DenoiseEngineEntry("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.rnnoise",
                    new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit },
                    (_, _) => { var e = new DelayEngine("rnnoise", 2f, 0) { Dead = Kill }; Made.Enqueue(e); return e; },
                    () => Unavailability.None),
            });
        }
    }

    private static float[] Ones() => Enumerable.Repeat(1f, 960).ToArray();

    private static void Until(Func<bool> done, Action? step = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done()) { Assert.True(DateTime.UtcNow < deadline, "timed out"); step?.Invoke(); Thread.Sleep(2); }
    }

    [Fact]
    public void A_failure_mid_over_goes_dry_never_silent_is_said_once_and_the_slot_rebuilds_at_the_unkey()
    {
        var k = new Killable();
        var chain = new TxNoiseChain(k.Catalog);
        chain.SetStrength("rnnoise", 1f);
        chain.SetEnabled(DenoiseFamily.Neural, true);
        var told = new ConcurrentQueue<string>();
        chain.FellBack += (family, id) => told.Enqueue(family + ":" + id);
        var reports = new ConcurrentQueue<string>();
        chain.OverReport += reports.Enqueue;
        float[] Run(bool keyed) { var b = Ones(); chain.Process(b, b.Length, 48000, keyed); return b; }

        Assert.All(Run(false), v => Assert.Equal(2f, v));
        // Over one: the engine dies mid-over.
        Assert.All(Run(true), v => Assert.Equal(2f, v));
        int builtBefore = k.Made.Count;
        k.Last.Dead = true;
        var first = Run(true);
        Assert.All(first, v => Assert.InRange(v, 1f, 2f));                              // never 0: the fade, then dry
        Assert.All(first.Skip(2 * PerChannelDenoiserBank.DefaultLatchFadeSamples), v => Assert.Equal(1f, v, 4));
        for (int i = 0; i < 4; i++) Assert.All(Run(true), v => Assert.Equal(1f, v, 4)); // dry, never 0
        k.Last.Dead = false;                                                          // it comes back...
        for (int i = 0; i < 5; i++) Assert.All(Run(true), v => Assert.Equal(1f, v, 4)); // ...and the over stays dry
        Until(() => told.Count == 1);
        Assert.Equal("Neural:rnnoise", told.Single());

        // The unkey: released and rebuilt while receiving.
        Run(false);
        Until(() => k.Made.Count > builtBefore && chain.NeuralSlot.RunningBank != null && chain.NeuralSlot.RunningBank.EngineFor(0) == k.Last, () => Run(false));
        Assert.All(Run(false), v => Assert.Equal(2f, v));
        Until(() => reports.Any(r => r.Contains("latched this over")));

        // Over two: it fails again after recovering, so the operator is told again.
        Assert.All(Run(true), v => Assert.Equal(2f, v));
        k.Last.Dead = true;
        Run(true);
        Until(() => told.Count == 2);

        // Over three: it never recovered, so nothing more is said.
        k.Kill = true;
        Run(false);
        Until(() => k.Made.Count > 2, () => Run(false));
        for (int i = 0; i < 5; i++) Run(false);
        for (int i = 0; i < 5; i++) Assert.All(Run(true), v => Assert.NotEqual(0f, v));
        Thread.Sleep(100);
        Assert.Equal(2, told.Count);
    }

    [Fact]
    public void The_failure_notice_plays_on_the_alert_channel_and_never_reaches_a_transmit_frame()
    {
        var k = new Killable();
        var chain = new TxNoiseChain(k.Catalog);
        chain.SetStrength("rnnoise", 1f);
        chain.SetEnabled(DenoiseFamily.Neural, true);
        TxAudioConditioning.HookNotices(chain);
        var noticed = new ManualResetEventSlim();
        chain.FellBack += (_, _) => noticed.Set();

        var cond = new JJPortaudio.TxAudioConditioner();
        bool keyed = false;
        cond.KeySource = () => Volatile.Read(ref keyed);
        cond.NoiseReducer = (b, c, r) => chain.Process(b, c, r, cond.Keyed);
        float loudest = 0;
        var pipeline = new JJPortaudio.TxFramePipeline
        {
            Conditioner = cond.Process,
            Encode = frame => { foreach (var v in frame) loudest = Math.Max(loudest, Math.Abs(v)); return new byte[1]; },
        };

        var mixer = new NAudio.Wave.SampleProviders.MixingSampleProvider(
            NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 2));
        bool master = EarconPlayer.EarconsEnabled;
        bool warnings = EarconPlayer.GetCategoryEnabled(EarconPlayer.EarconCategory.Warnings);
        EarconPlayer.EarconsEnabled = true;
        EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Warnings, true);
        EarconPlayer.TestAlertMixer = mixer;
        try
        {
            // The microphone is silent throughout, so anything at all in a frame came from somewhere else.
            for (int i = 0; i < 5; i++) pipeline.Emit(new float[960], 960, 48000); // receiving: the slot turns on
            Volatile.Write(ref keyed, true);
            k.Last.Dead = true;
            for (int i = 0; i < 5; i++) pipeline.Emit(new float[960], 960, 48000);
            Assert.True(noticed.Wait(5000));
            Until(() => mixer.MixerInputs.Any());
            for (int i = 0; i < 50; i++) pipeline.Emit(new float[960], 960, 48000);
            Assert.Equal(0f, loudest);
            Assert.Equal(60L, pipeline.FramesSent);
        }
        finally
        {
            EarconPlayer.TestAlertMixer = null;
            EarconPlayer.EarconsEnabled = master;
            EarconPlayer.SetCategoryEnabled(EarconPlayer.EarconCategory.Warnings, warnings);
        }
    }
}
