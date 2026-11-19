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
[Collection("Transmit conditioning statics")]
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
    public void Strength_and_the_continuous_levers_stay_live_while_keyed_and_a_rebuild_setting_waits()
    {
        // The ruling: strength live, engine changes at the unkey. A setting that
        // needs no new instance is a continuous lever, live TODAY by the one
        // policy, TxNoiseChain.ContinuousLeversLiveWhileKeyed, while S15 asks
        // whether it is like strength (repair round 1, R3). If that flips, the
        // "amount" line below must expect AtUnkey.
        Assert.True(TxNoiseChain.ContinuousLeversLiveWhileKeyed);
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
        // Unequal, non-zero delays (repair round 1, R6): the spectral engine is
        // 100 samples late and halves, the neural one 260 late and triples, so
        // the output must be 1.5 times the input, 360 samples late, and the
        // chain must report 360. A sum hard-coded to anything else goes red.
        var catalog = new DenoiseEngineCatalog(new[]
        {
            new DenoiseEngineEntry("nr4", DenoiseSide.Pc, DenoiseFamily.Spectral, "label", new[] { DenoiseDirection.Transmit },
                (_, _) => new TxLatchTests.DelayEngine("nr4", 0.5f, 100), () => Unavailability.None),
            new DenoiseEngineEntry("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Transmit },
                (_, _) => new TxLatchTests.DelayEngine("rnnoise", 3f, 260), () => Unavailability.None),
        });
        var chain = new TxNoiseChain(catalog);
        chain.SetStrength("nr4", 1f); chain.SetStrength("rnnoise", 1f);
        chain.SetEnabled(DenoiseFamily.Spectral, true);
        chain.SetEnabled(DenoiseFamily.Neural, true);
        int frames = 480 * 4;
        var input = Enumerable.Range(0, frames).Select(i => (float)Math.Sin(i * 0.013) * 0.4f).ToArray();
        var output = new float[frames];
        for (int b = 0; b < 4; b++)
        {
            var buf = new float[960];
            for (int i = 0; i < 480; i++) buf[2 * i] = buf[2 * i + 1] = input[b * 480 + i];
            chain.Process(buf, buf.Length, 48000, false);
            for (int i = 0; i < 480; i++) output[b * 480 + i] = buf[2 * i];
            Assert.Equal(360, chain.AppliedLatencySamples);
        }
        for (int i = 0; i < 360; i++) Assert.Equal(0f, output[i]);
        for (int i = 360; i < frames; i++) Assert.Equal(1.5f * input[i - 360], output[i], 5);
    }

    // ── A setup replaces the slots (repair round 1, R2; Codex's two reproductions, made permanent) ──

    [Fact]
    public void An_untouched_saved_setup_restores_the_default_engine()
    {
        var chain = new TxNoiseChain(new TransmitSlotTests.Catalog().Value);
        var original = chain.CaptureSlots();
        Assert.Equal("", original.Neural.EngineId);
        chain.ChooseEngine(DenoiseFamily.Neural, "b");
        chain.SetEnabled(DenoiseFamily.Neural, true);
        chain.ApplySlots(original.Spectral, original.Neural);
        Assert.Equal(TxNoiseChain.DefaultNeuralEngine, chain.ChosenEngineId(DenoiseFamily.Neural));
        Assert.False(chain.WantsEnabled(DenoiseFamily.Neural));
    }

    [Fact]
    public void An_untouched_saved_setup_restores_the_default_strength()
    {
        var chain = new TxNoiseChain(new TransmitSlotTests.Catalog().Value);
        var original = chain.CaptureSlots();
        float initial = chain.GetStrength("rnnoise");
        chain.SetStrength("rnnoise", 0.123f);
        chain.ApplySlots(original.Spectral, original.Neural);
        Assert.Equal(initial, chain.GetStrength("rnnoise"));
    }

    [Fact]
    public void A_setup_brings_no_value_of_an_engine_it_does_not_name()
    {
        var chain = new TxNoiseChain(Catalog());
        chain.SetStrength("nnr", 0.25f);
        chain.SetSetting("nnr", "amount", new(3));
        var setup = new TxNoiseSlotSetup
        {
            Enabled = true, EngineId = "rnnoise",
            Engines = { new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 0.5f } },
        };
        chain.ApplySlots(null, setup);
        Assert.Equal(0.5f, chain.GetStrength("rnnoise"));
        Assert.Equal(NoiseFloor.DefaultStrength("nnr", DenoiseDirection.Transmit), chain.GetStrength("nnr"));
        Assert.Equal(1.0, chain.NeuralSlot.GetSetting("nnr", "amount").Number);
        Assert.Equal(TxNoiseChain.DefaultSpectralEngine, chain.ChosenEngineId(DenoiseFamily.Spectral)); // a missing slot is the default, off
        Assert.False(chain.WantsEnabled(DenoiseFamily.Spectral));
    }

    // ── A whole setup and the gate's on and off wait for the unkey (repair round 1, R3) ──

    [Fact]
    public void A_whole_setup_applied_while_keyed_waits_and_a_later_request_still_wins()
    {
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        rig.Run();
        Volatile.Write(ref rig.Keyed, true);
        var setup = new TxNoiseSlotSetup
        {
            Enabled = true, EngineId = "nnr",
            Engines = { new DenoiseEngineSettings { EngineId = "nnr", Direction = DenoiseDirection.Transmit, Strength = 0.5f } },
        };
        Assert.Equal(TxChangeTiming.AtUnkey, rig.Chain.ApplySlots(null, setup).Timing);
        Assert.True(rig.Chain.HasDeferredChanges);
        Assert.Equal("rnnoise", rig.Chain.ChosenEngineId(DenoiseFamily.Neural));      // nothing yet, not even the choice
        Assert.Equal(1f, rig.Chain.GetStrength("rnnoise"));
        // Asked for after the setup, so it must still win: it waits behind it.
        Assert.Equal(TxChangeTiming.AtUnkey, rig.Chain.SetEnabled(DenoiseFamily.Neural, false).Timing);
        rig.Chain.SetStrength("nnr", 0.75f);
        for (int i = 0; i < 5; i++) Assert.All(rig.Run(), v => Assert.Equal(2f, v));

        Volatile.Write(ref rig.Keyed, false);
        rig.Run();                                                    // the unkey edge runs the held changes
        Until(() => !rig.Chain.HasDeferredChanges);
        Assert.Equal("nnr", rig.Chain.ChosenEngineId(DenoiseFamily.Neural));
        Assert.False(rig.Chain.WantsEnabled(DenoiseFamily.Neural));
        Assert.Equal(0.75f, rig.Chain.GetStrength("nnr"));
    }

    [Fact]
    public void A_whole_setup_and_the_gates_on_and_off_wait_for_the_unkey_through_the_conditioning_owner()
    {
        var chain = new TxNoiseChain(Catalog());
        bool keyed = true;
        chain.KeySource = () => Volatile.Read(ref keyed);
        TxAudioConditioning.UseChainForTest(chain);
        bool gateBefore = TxAudioConditioning.GateEnabled;
        float holdBefore = TxAudioConditioning.GateHoldMs;
        try
        {
            Volatile.Write(ref keyed, false);
            TxAudioConditioning.GateEnabled = false;
            TxAudioConditioning.GateHoldMs = 150;
            Volatile.Write(ref keyed, true);

            Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetGateEnabled(true).Timing);
            Assert.False(TxAudioConditioning.GateEnabled);
            var setup = TxAudioConditioning.CaptureSetup("Loud room");
            setup.Gate.HoldMs = 500;
            setup.Neural.Enabled = true;
            Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.ApplySetup(setup).Timing);
            Assert.Equal(150, TxAudioConditioning.GateHoldMs);   // the whole setup waits, gate included
            Assert.False(chain.WantsEnabled(DenoiseFamily.Neural));

            Volatile.Write(ref keyed, false);
            chain.ApplyDeferred();
            Assert.Equal(500, TxAudioConditioning.GateHoldMs);
            Assert.True(chain.WantsEnabled(DenoiseFamily.Neural));
            // The setup came after the gate request, so its gate (off) wins.
            Assert.False(TxAudioConditioning.GateEnabled);
        }
        finally
        {
            chain.KeySource = null;
            TxAudioConditioning.GateEnabled = gateBefore;
            TxAudioConditioning.GateHoldMs = holdBefore;
            TxAudioConditioning.UseChainForTest(null!);
        }
    }

    [Fact]
    public void The_conditioner_adopts_a_requested_gate_on_or_off_only_on_an_unkeyed_buffer()
    {
        var cond = new JJPortaudio.TxAudioConditioner();
        bool keyed = true;
        cond.KeySource = () => keyed;
        var buf = new float[960];
        cond.RequestGateEnabled(true);
        for (int i = 0; i < 5; i++) { cond.Process(buf, buf.Length, 48000); Assert.False(cond.Gate.Enabled); }
        keyed = false;
        cond.Process(buf, buf.Length, 48000);
        Assert.True(cond.Gate.Enabled);
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

    [Fact]
    public void Engines_are_built_on_workers_never_on_the_thread_that_runs_the_audio()
    {
        // Item E: native construction (and DeepFilterNet3's hash check, which
        // lives in its constructor) stays off the audio thread, as on receive.
        var threads = new System.Collections.Concurrent.ConcurrentQueue<int>();
        DenoiseEngineEntry Row(string id, DenoiseFamily family) =>
            new(id, DenoiseSide.Pc, family, "label", new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit },
                (initial, _) => { threads.Enqueue(Environment.CurrentManagedThreadId); return new Engine(id, 1f, initial); },
                () => Unavailability.None, SettingDescriptors: Engine.Descriptors);
        var chain = new TxNoiseChain(new DenoiseEngineCatalog(new[] { Row("nr4", DenoiseFamily.Spectral), Row("rnnoise", DenoiseFamily.Neural), Row("nnr", DenoiseFamily.Neural) }));
        while (threads.TryDequeue(out _)) { } // the chain's first two engines are built where it is made, on the control side
        int audioThread = Environment.CurrentManagedThreadId;
        chain.SetEnabled(DenoiseFamily.Neural, true);
        chain.ChooseEngine(DenoiseFamily.Neural, "nnr");
        chain.SetSetting("nnr", "model", new(2));
        var buf = new float[960];
        Until(() => chain.NeuralSlot.RunningEngineId == "nnr" && chain.NeuralSlot.RunningIsCurrent("nnr"),
            () => chain.Process(buf, buf.Length, 48000, false));
        Assert.NotEmpty(threads);
        Assert.DoesNotContain(audioThread, threads);
    }

    [Fact]
    public void The_audio_stage_never_makes_the_chain()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var owner = typeof(TxAudioConditioning);
        var field = owner.GetField("_chain", flags)!;
        var previous = field.GetValue(null);
        try
        {
            TxAudioConditioning.UseChainForTest(null!);
            var stage = owner.GetMethod("NrStage", flags)!.CreateDelegate<Action<float[], int, uint>>();
            stage(new float[960], 960, 48000);
            Assert.Null(field.GetValue(null));
        }
        finally { field.SetValue(null, previous); }
    }

    [Fact]
    public void The_helpers_transmit_frame_cost_is_its_worst_known_transmit_engine()
    {
        // Item D: the first of the two measurements, the helper's own benchmark.
        var none = Array.Empty<EngineSettingDescriptor>();
        WdspHelloAck Ack(params WdspEngineOffer[] engines) =>
            new(1, "fixture", "2.11", "0123456789abcdef0123456789abcdef01234567", "fftw", 16, Array.Empty<WdspFingerprint>(), engines);
        Assert.Equal(2_500_000L, WdspHost.TransmitFrameCostP99(Ack(
            new WdspEngineOffer("nnr", 3, 48000, 480, 2448, 200, 1_000_000, 2_500_000, none),
            new WdspEngineOffer("nr2", 3, 48000, 480, 3840, 200, 200_000, 400_000, none),
            new WdspEngineOffer("nr2_rx_only", 1, 48000, 480, 1, 200, 1, 9_000_000, none),   // unknown engine: ignored
            new WdspEngineOffer("nr2", 1, 48000, 480, 1, 200, 1, 8_000_000, none))));       // receive only: ignored
        Assert.Null(WdspHost.TransmitFrameCostP99(Ack(new WdspEngineOffer("nnr", 1, 48000, 480, 2448, 200, 1, 9, none))));
    }

    [Fact]
    public void Each_over_reports_its_frame_counts_and_how_the_buffers_arrived()
    {
        // Item D, measure first: ten frames back to back per capture callback
        // must read as bursts of ten.
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rig.Chain.OverReport += reports.Enqueue;
        rig.Run();
        Volatile.Write(ref rig.Keyed, true);
        for (int callback = 0; callback < 3; callback++)
        {
            for (int frame = 0; frame < 10; frame++) rig.Run();
            Thread.Sleep(30);
        }
        Volatile.Write(ref rig.Keyed, false);
        rig.Run();
        Until(() => !reports.IsEmpty);
        string line = reports.Single();
        Assert.Contains("Neural rnnoise: engine frames 30, late 0, failed 0, latched dry 0", line);
        Assert.Contains("Spectral off", line);
        Assert.Contains("30 buffers", line);
        Assert.Contains("longest burst 10 buffers", line);
    }

    // ── Repair round 2: Codex's re-check probes, made permanent ──

    private static float[] RunUnkeyed(TxNoiseChain c)
    {
        var b = Enumerable.Repeat(1f, 960).ToArray();
        c.Process(b, b.Length, 48000, false);
        return b;
    }

    [Fact]
    public void Replacing_a_setup_resets_the_running_engines_omitted_live_values()
    {
        // R2: the store was cleared but the running engine kept the old value.
        var c = new TxNoiseChain(Catalog());
        c.SetStrength("rnnoise", 1f);
        c.SetEnabled(DenoiseFamily.Neural, true);
        var saved = c.CaptureSlots();
        Assert.All(RunUnkeyed(c), v => Assert.Equal(2f, v));
        c.SetSetting("rnnoise", "amount", new(3));
        Assert.All(RunUnkeyed(c), v => Assert.Equal(6f, v));
        c.ApplySlots(saved.Spectral, saved.Neural);
        Assert.Equal(1, c.NeuralSlot.GetSetting("rnnoise", "amount").Number);
        Assert.All(RunUnkeyed(c), v => Assert.Equal(2f, v));   // what is heard, not only the getter
    }

    [Fact]
    public void A_later_strength_asked_after_unkey_but_before_the_drain_is_not_lost()
    {
        // R3: the gap between the unkey and the worker's drain.
        var c = new TxNoiseChain(Catalog());
        bool keyed = true;
        c.KeySource = () => keyed;
        var setup = new TxNoiseSlotSetup { EngineId = "rnnoise", Enabled = true,
            Engines = { new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 0.5f } } };
        c.ApplySlots(null, setup);
        keyed = false;
        c.SetStrength("rnnoise", 0.75f);
        c.ApplyDeferred();
        Assert.Equal(0.75f, c.GetStrength("rnnoise"));
        Assert.False(c.HasDeferredChanges);
    }

    [Fact]
    public void A_continuous_lever_asked_in_the_same_gap_is_not_lost_either()
    {
        var c = new TxNoiseChain(Catalog());
        bool keyed = true;
        c.KeySource = () => keyed;
        var setup = new TxNoiseSlotSetup { EngineId = "rnnoise", Enabled = true,
            Engines = { new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 1f,
                Values = { new DenoiseNamedValue { Name = "amount", Number = 2 } } } } };
        c.ApplySlots(null, setup);
        keyed = false;
        c.SetSetting("rnnoise", "amount", new(3));
        c.ApplyDeferred();
        Assert.Equal(3, c.NeuralSlot.GetSetting("rnnoise", "amount").Number);
    }

    [Fact]
    public void The_drain_stops_when_another_over_starts_and_keeps_the_rest_in_order()
    {
        var c = new TxNoiseChain(Catalog());
        bool keyed = true;
        c.KeySource = () => keyed;
        c.SetStrength("rnnoise", 1f);
        int first = 0;
        c.RunOrDefer(() => { first++; keyed = true; }); // a key-down arriving while the first held change runs
        c.ApplySlots(null, new TxNoiseSlotSetup { EngineId = "rnnoise", Enabled = true,
            Engines = { new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 0.25f } } });
        keyed = false;
        c.ApplyDeferred();
        Assert.Equal(1, first);
        Assert.Equal(1f, c.GetStrength("rnnoise"));   // the whole setup did NOT land in the new over
        Assert.True(c.HasDeferredChanges);
        keyed = false;
        c.ApplyDeferred();
        Assert.Equal(1, first);
        Assert.Equal(0.25f, c.GetStrength("rnnoise"));
        Assert.False(c.HasDeferredChanges);
    }

    [Fact]
    public void Repeated_unkey_notifications_do_not_run_a_held_change_twice()
    {
        var c = new TxNoiseChain(Catalog());
        bool keyed = true;
        c.KeySource = () => keyed;
        int count = 0;
        c.RunOrDefer(() => count++);
        c.ApplyDeferred();
        Assert.Equal(0, count);
        keyed = false;
        c.ApplyDeferred(); c.ApplyDeferred();
        Assert.Equal(1, count);
    }

    // ── Repair round 3: a whole setup's live values never land mid-over (Codex's edge probes, made permanent) ──

    /// <summary>Pauses the control thread inside an admitted setup, without touching production code.</summary>
    private sealed class PausingDescriptors : IReadOnlyList<EngineSettingDescriptor>
    {
        private readonly IReadOnlyList<EngineSettingDescriptor> _inner;
        public Action? Once;
        public PausingDescriptors(IReadOnlyList<EngineSettingDescriptor> inner) => _inner = inner;
        public int Count => _inner.Count;
        public EngineSettingDescriptor this[int index] => _inner[index];
        public IEnumerator<EngineSettingDescriptor> GetEnumerator()
        {
            Interlocked.Exchange(ref Once, null)?.Invoke();
            return _inner.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_whole_setups_non_strength_value_never_lands_partway_through_a_new_over(bool viaCoordinator)
    {
        var entries = Catalog().Entries;
        var pause = new PausingDescriptors(entries.Single(e => e.Id == "nr4").Settings);
        var c = new TxNoiseChain(new DenoiseEngineCatalog(entries.Select(e => e.Id == "nr4" ? e with { SettingDescriptors = pause } : e)));
        bool keyed = false;
        c.KeySource = () => Volatile.Read(ref keyed);
        c.SetStrength("rnnoise", 1);
        c.SetEnabled(DenoiseFamily.Neural, true);
        float[] Run()
        {
            var b = Enumerable.Repeat(1f, 960).ToArray();
            c.Process(b, b.Length, 48000, Volatile.Read(ref keyed));
            return b;
        }
        Assert.All(Run(), v => Assert.Equal(2f, v));
        Volatile.Write(ref keyed, true);
        var setup = new TxNoiseSlotSetup { EngineId = "rnnoise", Enabled = true,
            Engines = { new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 1,
                Values = { new DenoiseNamedValue { Name = "amount", Number = 3 } } } } };
        if (viaCoordinator)
        {
            TxAudioConditioning.UseChainForTest(c);
            TxAudioConditioning.ApplySetup(new TxNoiseSetup { Neural = setup });
        }
        else c.ApplySlots(null, setup);
        Assert.True(c.HasDeferredChanges);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? workerError = null;
        pause.Once = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("release"); };
        Volatile.Write(ref keyed, false);
        var worker = new Thread(() => { try { c.ApplyDeferred(); } catch (Exception e) { workerError = e; } });
        worker.Start();
        var observed = new List<float>();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the setup was not admitted, so this proves nothing");
            Volatile.Write(ref keyed, true); // a NEW over, after the drain's key check
            for (int i = 0; i < 3; i++) Assert.All(Run(), v => Assert.Equal(2f, v));
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(workerError);
            for (int i = 0; i < 4; i++) observed.Add(Run()[0]);
            Assert.True(observed.All(v => v == 2f), "the whole setup's amount landed mid-over: " + string.Join(", ", observed));
            // And at the unkey it does land, whole.
            Volatile.Write(ref keyed, false);
            Assert.All(Run(), v => Assert.Equal(6f, v));
            Assert.False(c.NeuralSlot.HasPendingView);
        }
        finally
        {
            release.Set();
            if (worker.IsAlive) Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            if (viaCoordinator) TxAudioConditioning.UseChainForTest(null!);
        }
    }

    [Fact]
    public void A_lone_strength_stays_live_mid_over_and_still_wins_over_a_waiting_setup()
    {
        // The standalone policy is unchanged: strength live. A setup admitted
        // just before the over waits for the unkey; strength asked for during
        // the over is heard at once and is not undone when the setup lands.
        var c = new TxNoiseChain(Catalog());
        bool keyed = false;
        c.KeySource = () => Volatile.Read(ref keyed);
        c.SetStrength("rnnoise", 1);
        c.SetEnabled(DenoiseFamily.Neural, true);
        float Run() { var b = Enumerable.Repeat(1f, 960).ToArray(); c.Process(b, b.Length, 48000, Volatile.Read(ref keyed)); return b[0]; }
        Assert.Equal(2f, Run());
        c.ApplySlots(null, new TxNoiseSlotSetup { EngineId = "rnnoise", Enabled = true,
            Engines = { new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 1,
                Values = { new DenoiseNamedValue { Name = "amount", Number = 3 } } } } });
        Assert.True(c.NeuralSlot.HasPendingView);            // admitted unkeyed, waiting for an unkeyed BUFFER
        Volatile.Write(ref keyed, true);
        Assert.Equal(2f, Run());
        c.SetStrength("rnnoise", 0.5f);                       // half wet: 0.5 x 1 + 0.5 x 2
        Assert.Equal(1.5f, Run());
        Volatile.Write(ref keyed, false);
        Assert.Equal(0.5f + 0.5f * 6f, Run());                // the setup's amount, with the later strength
    }

    [Fact]
    public void A_whole_gate_staged_while_keyed_waits_for_an_unkeyed_buffer_and_a_later_lone_lever_still_wins()
    {
        var cond = new JJPortaudio.TxAudioConditioner();
        bool keyed = true;
        cond.KeySource = () => keyed;
        cond.Gate.HoldMs = 150;
        cond.Gate.RangeDb = 25;
        var buf = new float[960];
        cond.Process(buf, buf.Length, 48000);
        cond.StageGate(new JJPortaudio.TxAudioConditioner.StagedGate
        {
            Enabled = true, HoldMs = 500, AttackMs = 3, ReleaseMs = 200, RangeDb = 30,
            AutoThreshold = false, HasThreshold = true, ThresholdDb = -40, ThresholdMarginDb = 10,
        });
        for (int i = 0; i < 5; i++)
        {
            cond.Process(buf, buf.Length, 48000);
            Assert.Equal((false, 150f, 25f, true), (cond.Gate.Enabled, cond.Gate.HoldMs, cond.Gate.RangeDb, cond.AutoThresholdInForce));
        }
        cond.ChangeGate(g => g.RangeDb = 12);                 // a lone lever: in force, and in the staged set
        cond.Process(buf, buf.Length, 48000);                 // still keyed: the lever is live...
        Assert.Equal((12f, 150f, false), (cond.Gate.RangeDb, cond.Gate.HoldMs, cond.Gate.Enabled)); // ...the setup is not
        Assert.True(cond.HasStagedGate);
        keyed = false;
        cond.Process(buf, buf.Length, 48000);
        Assert.False(cond.HasStagedGate);
        Assert.Equal((true, 500f, 12f, -40f, false, 10f),
            (cond.Gate.Enabled, cond.Gate.HoldMs, cond.Gate.RangeDb, cond.Gate.ThresholdDb, cond.AutoThresholdInForce, cond.ThresholdMarginInForceDb));
    }

    private sealed class ApproxPair : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => Math.Abs(a.Item1 - b.Item1) < 1e-6 && Math.Abs(a.Item2 - b.Item2) < 1e-6;
        public int GetHashCode((float, float) obj) => 0;
    }

    // ── Sprint 49 Track H2 bench round, item 5: a file over still ends the over ──

    [Fact]
    public void A_file_over_ends_by_the_key_state_when_no_unkeyed_buffer_arrives()
    {
        // A reference recording stops the microphone capture, so after its over
        // no unkeyed buffer arrives: the over never ended and the next merged in.
        // The tick asks; the audio thread ends the over on its next buffer
        // (bench round 2), before any of the new over's audio.
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rig.Chain.OverReport += reports.Enqueue;
        rig.Run();
        Volatile.Write(ref rig.Keyed, true);
        for (int i = 0; i < 5; i++) rig.Run();                     // over one, from the file
        Assert.False(rig.Chain.EndOverByKeyState(0));               // still keyed: nothing
        Volatile.Write(ref rig.Keyed, false);                      // the unkey; no buffer follows
        Assert.False(rig.Chain.EndOverByKeyState(60_000));          // audio "still flowing": the buffer's job
        rig.Chain.SetEnabled(DenoiseFamily.Neural, false);          // asked for between the overs
        Assert.True(rig.Chain.EndOverByKeyState(0));
        Assert.False(rig.Chain.EndOverByKeyState(0));               // already asked about this buffer
        Assert.True(rig.Chain.NeuralSlot.Enabled);                  // the tick changes nothing the audio owns
        Volatile.Write(ref rig.Keyed, true);
        for (int i = 0; i < 3; i++) rig.Run();                     // over two: its first buffer ends over one
        Until(() => reports.Count == 1);
        Assert.Contains("5 buffers", reports.Single());
        Assert.False(rig.Chain.NeuralSlot.Enabled);                 // the unkey's work, done at that boundary
        Volatile.Write(ref rig.Keyed, false);
        Assert.True(rig.Chain.EndOverByKeyState(0));
        Volatile.Write(ref rig.Keyed, true);
        rig.Run();
        Until(() => reports.Count == 2);
        Assert.Contains("3 buffers", reports.ToArray()[1]);
        // The app's policy tick (twice a second) is what asks.
        Assert.Contains("_chain?.EndOverByKeyState();", System.IO.File.ReadAllText(System.IO.Path.Combine(
            JJFlexWpf.Tests.Infrastructure.RepoPaths.RequireRoot(), "JJFlexWpf", "TxAudioConditioning.cs")));
    }

    /// <summary>Holds the policy tick at its trace line, on its own thread only, so a test can start an over in between.</summary>
    private sealed class TickBarrier : System.Diagnostics.TraceListener
    {
        public int Thread;
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (Environment.CurrentManagedThreadId != Thread || message == null || !message.Contains("ends by the key state")) return;
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void An_over_that_starts_while_the_tick_is_deciding_is_never_changed_by_the_old_overs_end()
    {
        // Codex's restart-after-claim schedule (bench round 2, P2): the tick sees
        // the key up and no audio, and is paused there; capture starts the next
        // over; an off is asked for (it waits for the unkey); the tick resumes.
        // In round 1 the resumed tick turned the slot off inside the new over.
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        Until(() => rig.Chain.NeuralSlot.RunningIsCurrent("rnnoise") && rig.Chain.NeuralSlot.Enabled, () => rig.Run());
        Volatile.Write(ref rig.Keyed, true);
        Assert.All(rig.Run(), v => Assert.Equal(2f, v));
        Volatile.Write(ref rig.Keyed, false);
        var barrier = new TickBarrier();
        System.Diagnostics.Trace.Listeners.Add(barrier);
        try
        {
            bool asked = false;
            var tick = new Thread(() => asked = rig.Chain.EndOverByKeyState(0));
            barrier.Thread = tick.ManagedThreadId;
            tick.Start();
            Assert.True(barrier.Entered.Wait(TimeSpan.FromSeconds(10)), "the tick never reached its decision");
            Volatile.Write(ref rig.Keyed, true);                    // the next over starts
            Assert.All(rig.Run(), v => Assert.Equal(2f, v));
            Assert.Equal(TxChangeTiming.AtUnkey, rig.Chain.SetEnabled(DenoiseFamily.Neural, false).Timing);
            barrier.Release.Set();
            Assert.True(tick.Join(TimeSpan.FromSeconds(10)));
            Assert.True(asked);
            for (int i = 0; i < 3; i++) Assert.All(rig.Run(), v => Assert.Equal(2f, v));   // never off mid-over
            Assert.True(rig.Chain.NeuralSlot.Enabled);
        }
        finally
        {
            barrier.Release.Set();
            System.Diagnostics.Trace.Listeners.Remove(barrier);
        }
        Volatile.Write(ref rig.Keyed, false);                       // the real unkey
        rig.Run();
        Assert.False(rig.Chain.NeuralSlot.Enabled);
        Assert.All(rig.Run(), v => Assert.Equal(1f, v));
    }

    [Fact]
    public void After_an_over_the_key_state_ended_the_next_buffer_is_the_unkeys_boundary()
    {
        // An engine chosen while keyed is installed at that boundary, before the
        // new over's audio, and never mid-over.
        var rig = new Rig();
        rig.Chain.SetEnabled(DenoiseFamily.Neural, true);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rig.Chain.OverReport += reports.Enqueue;
        Until(() => rig.Chain.NeuralSlot.RunningIsCurrent("rnnoise") && rig.Chain.NeuralSlot.Enabled, () => rig.Run());
        Volatile.Write(ref rig.Keyed, true);
        Assert.All(rig.Run(), v => Assert.Equal(2f, v));            // RNNoise, gain 2
        rig.Chain.ChooseEngine(DenoiseFamily.Neural, "nnr");        // gain 3, chosen mid-over
        for (int i = 0; i < 30; i++) { Assert.All(rig.Run(), v => Assert.Equal(2f, v)); Thread.Sleep(10); }   // built meanwhile, never installed mid-over
        Volatile.Write(ref rig.Keyed, false);
        Assert.True(rig.Chain.EndOverByKeyState(0));
        Volatile.Write(ref rig.Keyed, true);                        // the next file over
        rig.Run();                                                  // the boundary buffer
        Assert.True(rig.Chain.NeuralSlot.RunningIsCurrent("nnr"), "the engine chosen during the last over was not installed at its unkey's boundary");
        Assert.All(rig.Run(), v => Assert.Equal(3f, v));
        // The new over's line names the engine that ran it, from its first buffer
        // (bench round 3, Codex: it named RNNoise with no frames).
        Volatile.Write(ref rig.Keyed, false);
        rig.Run();
        Until(() => reports.Count == 2);
        // The two lines are worded on pool threads, so they can arrive in either
        // order: pick the new over's by its size, never by its position.
        string line = Assert.Single(reports, l => l.Contains("; 2 buffers in "));
        Assert.Contains("Neural nnr: engine frames 2, late 0, failed 0, latched dry 0", line);
        Assert.DoesNotContain("rnnoise", line);
    }
}
