using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track H, item B: the transmit chain holds every change as the
/// operator's intent and makes it audible only on an unkeyed buffer; strength
/// stays live; the chain runs one mono engine per slot; and the session keeps
/// choices made with no radio connected (#1077). Windowless: no control is
/// built, and every test asserts an MTA thread.
/// </summary>
public sealed class TxNoiseChainTests
{
    public TxNoiseChainTests() => NoiseReductionPerChannelTests.AssertWindowless();

    /// <summary>An engine whose output is its input times a gain, with one live and one rebuild setting.</summary>
    private sealed class Engine : IDenoiseEngine
    {
        public static readonly IReadOnlyList<EngineSettingDescriptor> Descriptors = new[]
        {
            new EngineSettingDescriptor("amount", "label", EngineSettingKind.Number, new(1), 0, 4, 0.5),
            new EngineSettingDescriptor("model", "label", EngineSettingKind.Number, new(1), 1, 4, 1, ChangesLatency: true),
        };
        public string Id { get; }
        private readonly float _gain;
        private float _amount = 1;
        private readonly float _model;
        public bool IsAvailable { get; set; } = true;
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public int LatencySamples => 0;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Descriptors;
        public Engine(string id, float gain, IReadOnlyDictionary<string, EngineSettingValue> initial)
        {
            Id = id; _gain = gain;
            _model = initial.TryGetValue("model", out var m) ? (float)m.Number : 1;
            if (initial.TryGetValue("amount", out var a)) _amount = (float)a.Number;
        }
        public EngineSettingValue GetSetting(string name) => name == "model" ? new(_model) : new(_amount);
        public bool TrySetSetting(string name, EngineSettingValue value) { if (name == "amount") { _amount = (float)value.Number; return true; } return false; }
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++) output[i] = input[i] * _gain * _amount * _model;
            return true;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    internal static DenoiseEngineCatalog Catalog()
    {
        DenoiseEngineEntry Row(string id, DenoiseFamily family, float gain, params DenoiseDirection[] directions) =>
            new(id, DenoiseSide.Pc, family, "label", directions.Length == 0 ? new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit } : directions,
                (initial, _) => new Engine(id, gain, initial), () => Unavailability.None, SettingDescriptors: Engine.Descriptors);
        return new DenoiseEngineCatalog(new[]
        {
            Row("nr4", DenoiseFamily.Spectral, 1f),
            Row("spectral_capture", DenoiseFamily.Spectral, 1f, DenoiseDirection.Receive),
            Row("rnnoise", DenoiseFamily.Neural, 2f),
            Row("nnr", DenoiseFamily.Neural, 3f),
        });
    }

    private sealed class Rig
    {
        public bool Keyed;
        public readonly TxNoiseChain Chain = new(Catalog());
        public Rig()
        {
            Chain.KeySource = () => Volatile.Read(ref Keyed);
            Chain.SetStrength("rnnoise", 1f);
            Chain.SetStrength("nnr", 1f);
            Chain.SetStrength("nr4", 1f);
        }

        /// <summary>One 480-frame stereo buffer of the given left and right values, through the chain; returns it.</summary>
        public float[] Run(float left = 1f, float right = 1f)
        {
            var buf = new float[960];
            for (int i = 0; i < buf.Length; i += 2) { buf[i] = left; buf[i + 1] = right; }
            Chain.Process(buf, buf.Length, 48000, Volatile.Read(ref Keyed));
            return buf;
        }
    }

    private static void Until(Func<bool> done, Action? step = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done()) { Assert.True(DateTime.UtcNow < deadline, "timed out"); step?.Invoke(); Thread.Sleep(2); }
    }

    [Fact]
    public void The_chain_starts_with_rnnoise_chosen_both_slots_off_and_one_mono_engine_each()
    {
        var chain = new TxNoiseChain(Catalog());
        Assert.Equal("rnnoise", chain.ChosenEngineId(DenoiseFamily.Neural));
        Assert.Equal(TxNoiseChain.DefaultSpectralEngine, chain.ChosenEngineId(DenoiseFamily.Spectral));
        Assert.False(chain.WantsEnabled(DenoiseFamily.Neural));
        Assert.False(chain.WantsEnabled(DenoiseFamily.Spectral));
        Assert.Equal(DenoiseDirection.Transmit, chain.NeuralSlot.Direction);
        Assert.Equal(DenoiseDirection.Transmit, chain.SpectralSlot.Direction);
        Assert.Equal(1, chain.NeuralSlot.RunningBank!.Channels);
        Assert.Equal(NoiseFloor.ToStrength(NoiseFloor.TransmitRnnoiseDefault), chain.GetStrength("rnnoise"));
    }

    [Fact]
    public void Turning_on_while_keyed_waits_for_the_unkey()
    {
        var rig = new Rig { Keyed = true };
        Assert.Equal(TxChangeTiming.AtUnkey, rig.Chain.SetEnabled(DenoiseFamily.Neural, true).Timing);
        Assert.True(rig.Chain.WantsEnabled(DenoiseFamily.Neural));
        Assert.True(rig.Chain.ChangesWaiting);
        for (int i = 0; i < 10; i++) Assert.All(rig.Run(), v => Assert.Equal(1f, v));
        Volatile.Write(ref rig.Keyed, false);
        Assert.All(rig.Run(), v => Assert.Equal(2f, v));
        Assert.False(rig.Chain.ChangesWaiting);

        // And off while keyed waits the same way.
        Volatile.Write(ref rig.Keyed, true);
        Assert.Equal(TxChangeTiming.AtUnkey, rig.Chain.SetEnabled(DenoiseFamily.Neural, false).Timing);
        Assert.All(rig.Run(), v => Assert.Equal(2f, v));
        Volatile.Write(ref rig.Keyed, false);
        Assert.All(rig.Run(), v => Assert.Equal(1f, v));
    }

    [Fact]
    public void Unkeyed_a_change_is_heard_on_the_next_buffer()
    {
        var rig = new Rig();
        Assert.Equal(TxChangeTiming.Now, rig.Chain.SetEnabled(DenoiseFamily.Neural, true).Timing);
        Assert.All(rig.Run(), v => Assert.Equal(2f, v));
    }

    [Fact]
    public void An_engine_change_while_keyed_waits_for_the_unkey()
    {
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        rig.Run();
        Volatile.Write(ref rig.Keyed, true);
        Assert.Equal(TxChangeTiming.AtUnkey, rig.Chain.ChooseEngine(DenoiseFamily.Neural, "nnr").Timing);
        Until(() => rig.Chain.NeuralSlot.BuildDelivered("nnr"));
        for (int i = 0; i < 10; i++) Assert.All(rig.Run(), v => Assert.Equal(2f, v));
        Volatile.Write(ref rig.Keyed, false);
        Assert.All(rig.Run(), v => Assert.Equal(3f, v));
    }

    [Fact]
    public void Strength_and_a_live_setting_change_at_once_while_keyed_and_a_rebuild_setting_waits()
    {
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        rig.Run();
        Volatile.Write(ref rig.Keyed, true);

        rig.Chain.SetStrength("rnnoise", 0.5f); // half wet: 0.5 * 1 + 0.5 * 2
        Assert.All(rig.Run(), v => Assert.Equal(1.5f, v));
        rig.Chain.SetStrength("rnnoise", 1f);

        Assert.Equal(TxChangeTiming.Now, rig.Chain.SetSetting("rnnoise", "amount", new(2)).Timing);
        Assert.All(rig.Run(), v => Assert.Equal(4f, v));

        Assert.Equal(TxChangeTiming.AtUnkey, rig.Chain.SetSetting("rnnoise", "model", new(3)).Timing);
        Until(() => rig.Chain.NeuralSlot.BuildDelivered("rnnoise"));
        for (int i = 0; i < 5; i++) Assert.All(rig.Run(), v => Assert.Equal(4f, v));
        Volatile.Write(ref rig.Keyed, false);
        Assert.All(rig.Run(), v => Assert.Equal(12f, v));
    }

    [Fact]
    public void Refusals_name_the_reason_and_change_nothing()
    {
        var chain = new TxNoiseChain(Catalog());
        Assert.Equal(("wrong-family", TxChangeTiming.Refused), (chain.ChooseEngine(DenoiseFamily.Neural, "nr4").Refusal, chain.ChooseEngine(DenoiseFamily.Neural, "nr4").Timing));
        Assert.Equal("wrong-direction", chain.ChooseEngine(DenoiseFamily.Spectral, "spectral_capture").Refusal);
        Assert.Equal("wrong-direction", chain.SetSetting("spectral_capture", "amount", new(2)).Refusal);
        Assert.Equal("rnnoise", chain.ChosenEngineId(DenoiseFamily.Neural));
    }

    [Fact]
    public void A_key_source_that_throws_reads_as_keyed()
    {
        var chain = new TxNoiseChain(Catalog()) { KeySource = () => throw new InvalidOperationException("radio gone") };
        Assert.True(chain.IsKeyed);
        Assert.Equal(TxChangeTiming.AtUnkey, chain.SetEnabled(DenoiseFamily.Neural, true).Timing);
        Assert.False(new TxNoiseChain(Catalog()).IsKeyed); // no radio: never keyed
    }

    [Fact]
    public void One_mono_engine_cleans_the_pair_and_a_buffer_nothing_processed_is_left_alone()
    {
        var rig = new Rig();
        var untouched = rig.Run(left: 0.2f, right: 0.6f);
        for (int i = 0; i < untouched.Length; i += 2) Assert.Equal((0.2f, 0.6f), (untouched[i], untouched[i + 1]));

        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        var cleaned = rig.Run(left: 0.2f, right: 0.6f);
        // The two channels averaged (0.4), cleaned once (x2), written to both.
        for (int i = 0; i < cleaned.Length; i += 2) Assert.Equal((0.8f, 0.8f), (cleaned[i], cleaned[i + 1]), new ApproxPair());
        Assert.Equal(1, rig.Chain.NeuralSlot.RunningBank!.Channels);
    }

    [Fact]
    public void Both_slots_run_spectral_then_neural_and_their_delays_add()
    {
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Spectral, true);
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        rig.Chain.SetSetting("nr4", "amount", new(1.5));
        Assert.All(rig.Run(), v => Assert.Equal(3f, v)); // 1 x 1.5 then x 2
        Assert.Equal(0, rig.Chain.AppliedLatencySamples);
    }

    [Fact]
    public void A_choice_made_with_no_radio_is_held_and_a_setup_round_trips()
    {
        var chain = new TxNoiseChain(Catalog());
        TxAudioConditioning.UseChainForTest(chain);
        bool gateBefore = TxAudioConditioning.GateEnabled;
        float holdBefore = TxAudioConditioning.GateHoldMs;
        try
        {
            Assert.Null(TxAudioConditioning.Conditioner);
            TxAudioConditioning.NrEnabled = true;          // #1077: kept, not refused
            TxAudioConditioning.GateEnabled = true;
            TxAudioConditioning.GateHoldMs = 400;
            Assert.True(chain.WantsEnabled(DenoiseFamily.Neural));
            Assert.True(TxAudioConditioning.GateEnabled);
            Assert.Equal(400, TxAudioConditioning.GateHoldMs);
            chain.ChooseEngine(DenoiseFamily.Neural, "nnr");
            chain.SetStrength("nnr", 0.3f);

            var setup = TxAudioConditioning.CaptureSetup("Studio");
            Assert.Equal(("Studio", "nnr", true), (setup.Name, setup.Neural.EngineId, setup.Neural.Enabled));
            Assert.Equal(0.3f, setup.Neural.Engines.Single(e => e.EngineId == "nnr").Strength);
            Assert.True(setup.Gate.Enabled);
            Assert.Equal(400, setup.Gate.HoldMs);

            var other = new TxNoiseChain(Catalog());
            TxAudioConditioning.UseChainForTest(other);
            TxAudioConditioning.GateEnabled = false;
            Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.ApplySetup(setup).Timing);
            Assert.Equal("nnr", other.ChosenEngineId(DenoiseFamily.Neural));
            Assert.True(other.WantsEnabled(DenoiseFamily.Neural));
            Assert.Equal(0.3f, other.GetStrength("nnr"));
            Assert.True(TxAudioConditioning.GateEnabled);
            Assert.True(TxAudioConditioning.CaptureSetup("Studio").SameContentAs(setup));
        }
        finally
        {
            TxAudioConditioning.UseChainForTest(null!);
            TxAudioConditioning.GateEnabled = gateBefore;
            TxAudioConditioning.GateHoldMs = holdBefore;
        }
    }

    [Fact]
    public void A_missing_named_setup_applies_nothing_and_is_said_the_first_time_only()
    {
        const string radio = "1234-5678-9012-3456";
        string root = Path.Combine(Path.GetTempPath(), "jjflex-txchain-" + Guid.NewGuid().ToString("N"));
        var store = new TxNoiseSetupStore(root);
        var chain = new TxNoiseChain(Catalog());
        TxAudioConditioning.UseChainForTest(chain);
        try
        {
            chain.ChooseEngine(DenoiseFamily.Neural, "nnr");
            string name = "Gone " + Guid.NewGuid().ToString("N");
            var first = TxAudioConditioning.ApplyNamedSetup(radio, name, store);
            var again = TxAudioConditioning.ApplyNamedSetup(radio, name, store);
            Assert.Equal((TxSetupApplyOutcome.Missing, true), (first.Outcome, first.FirstTime));
            Assert.Equal((TxSetupApplyOutcome.Missing, false), (again.Outcome, again.FirstTime));
            Assert.Equal("nnr", chain.ChosenEngineId(DenoiseFamily.Neural)); // the last values stay

            var setup = TxAudioConditioning.CaptureSetup(name);
            setup.Neural.EngineId = "rnnoise";
            store.Save(radio, setup);
            Assert.Equal(TxSetupApplyOutcome.Applied, TxAudioConditioning.ApplyNamedSetup(radio, name, store).Outcome);
            Assert.Equal("rnnoise", chain.ChosenEngineId(DenoiseFamily.Neural));
            Assert.Equal(TxSetupApplyOutcome.NoName, TxAudioConditioning.ApplyNamedSetup(radio, " ", store).Outcome);
        }
        finally
        {
            TxAudioConditioning.UseChainForTest(null!);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class ApproxPair : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => Math.Abs(a.Item1 - b.Item1) < 1e-6 && Math.Abs(a.Item2 - b.Item2) < 1e-6;
        public int GetHashCode((float, float) obj) => 0;
    }
}
