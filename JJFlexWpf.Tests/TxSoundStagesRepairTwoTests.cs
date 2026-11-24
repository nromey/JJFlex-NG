using System;
using System.Reflection;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track I, repair round 2 (Codex's recheck of 0a54b6e0): the noise
/// layer's Escape owns each transmit stage on its own, so a change made
/// elsewhere to the de-esser never costs the layer its equaliser, or the
/// reverse, whether the put-back runs now or is held for the unkey; and a
/// change of look-ahead allocates nothing on the audio thread. Windowless:
/// MTA asserted; the real controller bridge on the owner's numerical chain.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxSoundStagesRepairTwoTests
{
    private const string Layer = NoiseController.LayerSource;
    private const string Home = "home";

    public TxSoundStagesRepairTwoTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private sealed class Bridge : IDisposable
    {
        public readonly TxGateOwnerTests.Context X = new();
        public readonly NoiseFixture F = new();
        public readonly NoiseLayerKeys Keys;
        public Bridge()
        {
            typeof(NoiseFixture).GetField("_transmit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(F, X.Chain);
            F.Noise.TransmitGate = new AppTransmitGate();
            TxAudioConditioning.ApplySetup(new TxNoiseSetup());
            X.Buffer();
            Keys = new NoiseLayerKeys(F.Noise);
        }
        public void Dispose() { F.Dispose(); X.Dispose(); }

        /// <summary>The layer turns the transmit equaliser on and loads Presence.</summary>
        public void LayerEqualizer()
        {
            Assert.True(Keys.Handle(NoiseLayerKeys.DirectionKey));
            Assert.True(Keys.Handle(NoiseLayerKeys.EqualizerKey));
            F.Noise.LoadEqualizerPreset(DenoiseDirection.Transmit, "presence", Layer);
        }

        public void Unkey() { X.Keyed = false; X.Chain.ApplyDeferred(); X.Buffer(); }
    }

    [Fact]
    public void The_layers_equaliser_goes_back_when_home_turned_the_de_esser_on_and_homes_de_esser_stays()
    {
        using var b = new Bridge();
        var snap = b.F.Noise.TakeSnapshot();
        b.LayerEqualizer();
        b.F.Noise.SetDeEsserOn(true, Home);
        Assert.True(b.F.Noise.RestoreSnapshot(snap, Layer));
        b.X.Buffer();
        Assert.False(TxAudioConditioning.EqSetup.Enabled);
        Assert.Empty(TxAudioConditioning.EqSetup.Bands);
        Assert.True(TxAudioConditioning.DeEsserSetup.Enabled);
        Assert.True(b.F.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void The_layers_de_esser_goes_back_when_home_changed_the_equaliser_and_homes_equaliser_stays()
    {
        using var b = new Bridge();
        var snap = b.F.Noise.TakeSnapshot();
        Assert.True(b.Keys.Handle(NoiseLayerKeys.DirectionKey));
        Assert.True(b.Keys.Handle(NoiseLayerKeys.DeEsserKey));
        b.F.Noise.SetEqualizerOn(DenoiseDirection.Transmit, true, Home);
        b.F.Noise.LoadEqualizerPreset(DenoiseDirection.Transmit, "presence", Home);
        Assert.True(b.F.Noise.RestoreSnapshot(snap, Layer));
        b.X.Buffer();
        Assert.False(TxAudioConditioning.DeEsserSetup.Enabled);
        Assert.True(TxAudioConditioning.EqSetup.Enabled);
        Assert.Equal("presence", TxAudioConditioning.EqSetup.PresetId);
    }

    [Fact]
    public void An_outside_change_to_the_same_stage_after_the_layers_is_kept()
    {
        using var b = new Bridge();
        var snap = b.F.Noise.TakeSnapshot();
        b.LayerEqualizer();
        b.F.Noise.LoadEqualizerPreset(DenoiseDirection.Transmit, "dx", Home);
        b.F.Noise.RestoreSnapshot(snap, Layer);
        b.X.Buffer();
        Assert.True(TxAudioConditioning.EqSetup.Enabled);
        Assert.Equal("dx", TxAudioConditioning.EqSetup.PresetId);
    }

    [Fact]
    public void A_genuinely_queued_layer_switch_is_withdrawn_and_a_queued_outside_switch_still_lands()
    {
        using var b = new Bridge();
        var snap = b.F.Noise.TakeSnapshot();
        b.X.Keyed = true;
        b.F.Noise.SetDeEsserOn(true, Home);                       // held: outside
        Assert.True(b.Keys.Handle(NoiseLayerKeys.DirectionKey));
        Assert.True(b.Keys.Handle(NoiseLayerKeys.EqualizerKey));  // held: the layer's
        Assert.True(b.X.Chain.HasDeferredChanges);
        Assert.False(TxAudioConditioning.EqSetup.Enabled);       // nothing landed yet
        Assert.False(TxAudioConditioning.DeEsserSetup.Enabled);
        Assert.True(b.F.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.False(TxAudioConditioning.EqSetup.Enabled);
        Assert.True(TxAudioConditioning.DeEsserSetup.Enabled);
    }

    [Fact]
    public void A_put_back_held_for_the_unkey_checks_each_stage_when_it_runs()
    {
        using var b = new Bridge();
        var snap = b.F.Noise.TakeSnapshot();
        b.LayerEqualizer();                                       // ran live, unkeyed
        b.X.Keyed = true;
        b.F.Noise.SetDeEsserOn(true, Home);                       // held ahead of the put-back
        Assert.True(b.F.Noise.RestoreSnapshot(snap, Layer));      // the put-back is held behind it
        Assert.Equal(TxChangeTiming.AtUnkey, b.F.Noise.LastRestoreTiming);
        Assert.True(b.X.Chain.HasDeferredChanges);
        Assert.True(TxAudioConditioning.EqSetup.Enabled);        // nothing put back yet
        b.Unkey();
        Assert.False(TxAudioConditioning.EqSetup.Enabled);       // the layer's stage went back
        Assert.True(TxAudioConditioning.DeEsserSetup.Enabled);   // the outside stage, landed first, kept
    }

    [Fact]
    public void Changing_the_look_ahead_allocates_nothing_on_the_audio_thread()
    {
        bool keyed = false;
        var c = new TxAudioConditioner { KeySource = () => keyed };
        c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, GateSetting = false, LookAheadMs = 3f });
        var buf = new float[960];
        for (int i = 0; i < 10; i++) c.Process(buf, buf.Length, 48000);
        Assert.Equal(TxNoiseGate.LookAheadFramesFor(3f, 48000), c.AppliedDelayFrames);
        c.RequestSwitch(g => g.LookAheadMs = 7f); // made off the audio thread, as the lever does
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) c.Process(buf, buf.Length, 48000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(TxNoiseGate.LookAheadFramesFor(7f, 48000), c.AppliedDelayFrames); // the change really landed
        Assert.Equal(0, allocated);
    }
}
