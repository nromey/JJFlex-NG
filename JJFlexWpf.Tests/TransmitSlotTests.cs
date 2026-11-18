using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track H, item A: one slot class serves both directions, the
/// catalog's factory is told which, and a transmit slot changes nothing
/// audible while keyed (item B's audio-side gate). Windowless: every test
/// asserts it on construction.
/// </summary>
public sealed class TransmitSlotTests
{
    public TransmitSlotTests() => NoiseReductionPerChannelTests.AssertWindowless();

    internal sealed class GainEngine : IDenoiseEngine
    {
        public string Id { get; }
        public readonly float Gain;
        public bool IsAvailable { get; set; } = true;
        public int LateFrom = int.MaxValue;
        public int Frames;
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public int LatencySamples { get; }
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public GainEngine(string id, float gain, int latency = 0) { Id = id; Gain = gain; LatencySamples = latency; }
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (++Frames >= LateFrom) return false;
            for (int i = 0; i < input.Length; i++) output[i] = input[i] * Gain;
            return true;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    internal sealed class Catalog
    {
        public readonly ConcurrentQueue<(string Id, DenoiseDirection Direction)> Built = new();
        public readonly ConcurrentDictionary<string, GainEngine> Last = new();
        public readonly DenoiseEngineCatalog Value;
        public Catalog(int latency = 0)
        {
            DenoiseEngineEntry Row(string id, DenoiseFamily family, float gain) =>
                new(id, DenoiseSide.Pc, family, "label", new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit },
                    (initial, direction) =>
                    {
                        if (id == "bad") throw new InvalidOperationException("will not start");
                        Built.Enqueue((id, direction));
                        var e = new GainEngine(id, gain, latency);
                        Last[id] = e;
                        return e;
                    },
                    () => Unavailability.None);
            Value = new DenoiseEngineCatalog(new[]
            {
                Row("rnnoise", DenoiseFamily.Neural, 2f), Row("b", DenoiseFamily.Neural, 3f), Row("bad", DenoiseFamily.Neural, 5f),
                Row("nr4", DenoiseFamily.Spectral, 0.5f),
            });
        }
    }

    private static float[] Ones() => Enumerable.Repeat(1f, 480).ToArray();

    private static void Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done()) { Assert.True(DateTime.UtcNow < deadline, "timed out"); Thread.Sleep(5); }
    }

    [Fact]
    public void The_factory_is_told_the_slot_direction()
    {
        var c = new Catalog();
        _ = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 1, c.Value, DenoiseDirection.Transmit);
        _ = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 2, c.Value);
        var built = c.Built.ToArray();
        Assert.Equal(new[] { ("rnnoise", DenoiseDirection.Transmit), ("rnnoise", DenoiseDirection.Receive), ("rnnoise", DenoiseDirection.Receive) }, built);
    }

    [Fact]
    public void A_transmit_slot_reports_its_direction_and_its_defaults_read_the_transmit_floor()
    {
        var c = new Catalog();
        var tx = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 1, c.Value, DenoiseDirection.Transmit);
        var rx = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 1, c.Value);
        Assert.Equal(DenoiseDirection.Transmit, tx.Direction);
        Assert.Equal(DenoiseDirection.Receive, rx.Direction);
        Assert.Equal(NoiseFloor.ToStrength(NoiseFloor.TransmitRnnoiseDefault), tx.GetStrength("rnnoise"));
        Assert.Equal(NoiseFloor.ToStrength(NoiseFloor.ReceiveRnnoiseDefault), rx.GetStrength("rnnoise"));
        Assert.All(tx.ExportSettings(), s => Assert.Equal(DenoiseDirection.Transmit, s.Direction));
    }

    [Fact]
    public void The_real_catalog_builds_a_transmit_direction_helper_engine_for_a_transmit_slot()
    {
        var catalog = new DenoiseEngineCatalog();
        foreach (var id in new[] { "nnr", "nr2" })
        {
            using var tx = Assert.IsType<WdspHelperEngine>(catalog.Find(id)!.Factory!(new Dictionary<string, EngineSettingValue>(), DenoiseDirection.Transmit));
            using var rx = Assert.IsType<WdspHelperEngine>(catalog.Find(id)!.Factory!(new Dictionary<string, EngineSettingValue>(), DenoiseDirection.Receive));
            Assert.Equal(WdspProtocol.Transmit, tx.Direction);
            Assert.Equal(WdspProtocol.Receive, rx.Direction);
        }
    }

    [Fact]
    public void Every_computer_reducer_is_offered_on_transmit_and_captured_noise_is_not()
    {
        // S9 and the 08:26 ruling: the receive-trained models (NNR, the ham
        // RNNoise model) are OFFERED on transmit, never locked out; only
        // Captured noise is receive-only by its nature.
        var catalog = new DenoiseEngineCatalog();
        foreach (var id in new[] { "nr2", "nr4", "rnnoise", "deepfilternet3", "nnr", "nvidia_afx" })
            Assert.True(catalog.Find(id)!.Serves(DenoiseDirection.Transmit), id);
        Assert.False(catalog.Find("spectral_capture")!.Serves(DenoiseDirection.Transmit));
        Assert.All(catalog.Entries.Where(e => e.Side == DenoiseSide.Radio), e => Assert.False(e.Serves(DenoiseDirection.Transmit), e.Id));
    }

    [Fact]
    public void Every_engine_but_rnnoise_starts_on_transmit_at_its_receive_floor()
    {
        // S6 (2026-11-18 08:24) and S8: one place, NoiseFloor.DefaultFloor.
        foreach (var e in new DenoiseEngineCatalog().Entries.Where(e => e.Side == DenoiseSide.Pc && e.Serves(DenoiseDirection.Transmit)))
        {
            if (e.Id == "rnnoise") Assert.Equal(20, NoiseFloor.DefaultFloor(e.Id, DenoiseDirection.Transmit));
            else Assert.Equal(NoiseFloor.DefaultFloor(e.Id, DenoiseDirection.Receive), NoiseFloor.DefaultFloor(e.Id, DenoiseDirection.Transmit));
        }
    }

    [Fact]
    public void A_transmit_engine_change_waits_while_keyed_then_cuts_over_with_no_warm_up()
    {
        var c = new Catalog();
        bool keyed = false;
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 1, c.Value, DenoiseDirection.Transmit)
        { SwapPermitted = () => !Volatile.Read(ref keyed) };
        slot.SetStrength("rnnoise", 1f); slot.SetStrength("b", 1f);
        slot.Enabled = true;
        var audio = Ones();
        slot.Process(audio, true, true, false, null);
        Assert.Equal(2f, audio[0]);

        Volatile.Write(ref keyed, true);
        Assert.True(slot.Select("b", out _));
        Until(() => slot.BuildDelivered("b"));
        for (int i = 0; i < 20; i++)
        {
            audio = Ones();
            slot.Process(audio, true, true, false, null);
            Assert.All(audio, v => Assert.Equal(2f, v));
            Assert.Equal("rnnoise", slot.RunningEngineId);
        }

        Volatile.Write(ref keyed, false);
        audio = Ones();
        slot.Process(audio, true, true, false, null);
        // The very first unkeyed buffer is all the new engine: no warm-up
        // buffer of the old one and no cross-fade between them.
        Assert.All(audio, v => Assert.Equal(3f, v));
        Assert.Equal("b", slot.RunningEngineId);
    }

    [Fact]
    public void A_failed_choice_made_while_keyed_leaves_the_running_engine_until_the_unkey()
    {
        var c = new Catalog();
        bool keyed = true;
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 1, c.Value, DenoiseDirection.Transmit)
        { SwapPermitted = () => !Volatile.Read(ref keyed) };
        slot.SetStrength("rnnoise", 1f);
        slot.Enabled = true;
        var results = new ConcurrentQueue<DenoiseSelectionResult>();
        slot.SelectionResult += (_, r) => results.Enqueue(r);
        Assert.True(slot.Select("bad", out _));
        for (int i = 0; i < 50; i++)
        {
            var audio = Ones();
            slot.Process(audio, true, true, false, null);
            Assert.All(audio, v => Assert.Equal(2f, v));
            Thread.Sleep(2);
        }
        Assert.Equal("rnnoise", slot.RunningEngineId);
        Volatile.Write(ref keyed, false);
        var after = Ones();
        slot.Process(after, true, true, false, null);
        Assert.Equal("", slot.RunningEngineId);
        Until(() => results.Any(r => r.SlotStopped && r.EngineId == "bad"));
    }

    [Fact]
    public void A_transmit_slot_reports_the_delay_its_last_buffer_went_out_with()
    {
        var c = new Catalog(latency: 960);
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 1, c.Value, DenoiseDirection.Transmit);
        var audio = Ones();
        slot.Process(audio, true, true, false, null);
        Assert.Equal(0, slot.LastAppliedLatencySamples);
        slot.Enabled = true;
        slot.Process(audio, true, true, false, null);
        Assert.Equal(960, slot.LastAppliedLatencySamples);
        slot.Process(audio, false, true, false, null);
        Assert.Equal(0, slot.LastAppliedLatencySamples);
    }
}
