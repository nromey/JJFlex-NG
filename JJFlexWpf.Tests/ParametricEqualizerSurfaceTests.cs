using System;
using System.Linq;
using System.Windows.Forms;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track I, item 3b and item 8: the parametric equaliser on
/// receive (where it sits in the receive pipeline, that it adds no delay, that
/// off is exact and a switch is click-free, that it is saved app-wide), its
/// windowless editing model, and the noise layer's equaliser keys working
/// with no window open. Windowless: MTA asserted; no dialog is built.
/// </summary>
public sealed class ParametricEqualizerSurfaceTests
{
    public ParametricEqualizerSurfaceTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static float[] Tone(double hz, double db, int frames, int channels = 2)
    {
        double a = Math.Pow(10, db / 20);
        var b = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++) b[f * channels + c] = (float)(a * Math.Sin(2 * Math.PI * hz * f / 48000));
        return b;
    }

    private static float[] Run(RxAudioPipeline p, float[] audio)
    {
        var out_ = (float[])audio.Clone();
        var buf = new float[960];
        for (int at = 0; at < out_.Length; at += 960)
        {
            Array.Copy(out_, at, buf, 0, 960);
            p.Process(buf);
            Array.Copy(buf, 0, out_, at, 960);
        }
        return out_;
    }

    private static ParametricEqSetup Boost(bool on = true) => new()
    {
        Enabled = on,
        Bands = { new() { Kind = ParametricEqKinds.Peaking, FrequencyHz = 1000, GainDb = 6, Q = 1 } },
    };

    [Fact]
    public void Receive_equaliser_off_is_bit_identical_to_no_equaliser_and_on_shapes_the_audio_by_its_band()
    {
        var audio = Tone(1000, -20, 48000);
        using var plain = new RxAudioPipeline();
        using var off = new RxAudioPipeline();
        off.SetEqualizer(Boost(on: false));
        Assert.Equal(Run(plain, audio), Run(off, audio));

        using var on = new RxAudioPipeline();
        on.SetEqualizer(Boost());
        var shaped = Run(on, audio);
        double inRms = Math.Sqrt(audio.Skip(48000).Take(40000).Average(s => (double)s * s));
        double outRms = Math.Sqrt(shaped.Skip(48000).Take(40000).Average(s => (double)s * s));
        Assert.InRange(20 * Math.Log10(outRms / inRms), 5.9, 6.1);
    }

    [Fact]
    public void The_receive_equaliser_adds_no_delay_so_the_slots_heard_delay_and_the_output_line_up_as_before()
    {
        var rng = new Random(5);
        var noise = Enumerable.Range(0, 96000).Select(_ => (float)((rng.NextDouble() * 2 - 1) * 0.1)).ToArray();
        using var p = new RxAudioPipeline();
        int before = p.SpectralSlot.HeardDelaySamples + p.NeuralSlot.HeardDelaySamples;
        p.SetEqualizer(new ParametricEqSetup { Enabled = true, Bands = { new() { Kind = ParametricEqKinds.Peaking, FrequencyHz = 1000, GainDb = 3, Q = 0.5f } } });
        var shaped = Run(p, noise);
        Assert.Equal(before, p.SpectralSlot.HeardDelaySamples + p.NeuralSlot.HeardDelaySamples);
        Assert.Equal(0, p.Equalizer.LatencyFrames);
        // Cross-correlation of the left channel: the peak is at lag zero.
        int best = 0; double bestScore = double.MinValue;
        for (int lag = 0; lag <= 100; lag++)
        {
            double sum = 0;
            for (int i = 20000; i < 90000; i += 2) sum += noise[i] * shaped[i + 2 * lag];
            if (sum > bestScore) { bestScore = sum; best = lag; }
        }
        Assert.Equal(0, best);
    }

    [Fact]
    public void Switching_the_receive_equaliser_on_fades_without_a_step()
    {
        using var p = new RxAudioPipeline();
        var audio = Tone(1000, -20, 48000);
        var buf = new float[960];
        float prev = 0, maxStep = 0;
        for (int at = 0, k = 0; at < audio.Length; at += 960, k++)
        {
            if (k == 20) p.SetEqualizer(new ParametricEqSetup { Enabled = true, Bands = { new() { Kind = ParametricEqKinds.Peaking, FrequencyHz = 1000, GainDb = 12, Q = 1 } } });
            Array.Copy(audio, at, buf, 0, 960);
            p.Process(buf);
            for (int i = 0; i < 960; i += 2) { if (at + i > 0) maxStep = Math.Max(maxStep, Math.Abs(buf[i] - prev)); prev = buf[i]; }
        }
        // A 1 kHz tone at -20 dBFS + 12 dB moves at most 2*pi*1000/48000*0.4 = 0.052 a sample; a click is far more.
        Assert.True(maxStep < 0.055f, $"largest step {maxStep:F4}");
    }

    [Fact]
    public void The_receive_equaliser_allocates_nothing_on_the_audio_thread()
    {
        using var p = new RxAudioPipeline();
        p.SetEqualizer(Boost());
        var buf = Tone(1000, -20, 480);
        for (int i = 0; i < 50; i++) p.Equalizer.Process(buf, buf.Length, 48000, false, 2); // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) p.Equalizer.Process(buf, buf.Length, 48000, false, 2);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void The_receive_equaliser_is_saved_and_restored_app_wide_with_the_receive_noise_choices()
    {
        using var p = new RxAudioPipeline();
        p.SetEqualizer(Boost());
        var config = new AudioOutputConfig();
        ReceiveDenoiseConfig.Map(config, p, save: true);
        Assert.NotNull(config.ReceiveEq);
        using var q = new RxAudioPipeline();
        ReceiveDenoiseConfig.Map(config, q, save: false);
        Assert.True(new TxNoiseSetup { Eq = q.EqualizerSetup }.SameContentAs(new TxNoiseSetup { Eq = Boost() }));
        // Through the file's own serializer, as audioConfig.xml carries it.
        var s = new System.Xml.Serialization.XmlSerializer(typeof(AudioOutputConfig));
        var w = new System.IO.StringWriter();
        s.Serialize(w, config);
        var back = (AudioOutputConfig)s.Deserialize(new System.IO.StringReader(w.ToString()))!;
        Assert.True(new TxNoiseSetup { Eq = back.ReceiveEq! }.SameContentAs(new TxNoiseSetup { Eq = Boost() }));
    }

    // The live state's version 1 upgrade is tested through AudioOutputConfig.Load,
    // in every element order, in TxSoundStagesRepairOneTests (repair 1, N1): the
    // upgrade now runs where each home loads, so a bare serializer no longer does it.

    // ── the editing model ──

    [Fact]
    public void The_model_adds_moves_adjusts_and_removes_bands_and_says_each()
    {
        var m = ParametricEqEditorModel.Detached(DenoiseDirection.Receive);
        Assert.NotEmpty(m.SelectBand(+1)); // no bands: said
        m.AddBand();
        Assert.Equal(1, m.BandCount);
        Assert.Equal(0, m.Band);
        m.SelectField(+1); // Frequency
        Assert.Equal(ParametricEqEditorModel.Field.Frequency, m.Current);
        m.Adjust(+1);
        Assert.Equal(1122f, m.Setting.Bands[0].FrequencyHz, 0); // a sixth of an octave up from 1000
        m.SelectField(+1); // Gain
        m.Adjust(+1); m.Adjust(+1);
        Assert.Equal(1f, m.Setting.Bands[0].GainDb);
        m.SelectField(-1); m.SelectField(-1); m.Adjust(+1); // Kind: bell to low shelf
        Assert.Equal(ParametricEqKinds.LowShelf, m.Setting.Bands[0].Kind);
        m.AddBand();
        Assert.Equal(2, m.BandCount);
        Assert.Equal(1, m.Band);
        Assert.NotEmpty(m.SelectBand(-1));
        Assert.Equal(0, m.Band);
        string said = m.RemoveBand();
        Assert.NotEmpty(said);
        Assert.Equal(1, m.BandCount);
        Assert.Equal(ParametricEqKinds.Peaking, m.Setting.Bands[0].Kind);
    }

    [Fact]
    public void A_preset_loads_as_its_bands_and_every_field_stays_editable()
    {
        using var f = new NoiseFixture();
        f.Noise.LoadEqualizerPreset(DenoiseDirection.Receive, "hiss_cut", "test");
        var eq = f.Noise.EqualizerSetup(DenoiseDirection.Receive);
        Assert.Equal("hiss_cut", eq.PresetId);
        Assert.Single(eq.Bands);
        var m = f.Noise.EqualizerEditor(DenoiseDirection.Receive, "test");
        m.Select(0);
        m.SelectField(+1); m.Adjust(-1);
        var after = f.Noise.EqualizerSetup(DenoiseDirection.Receive);
        Assert.NotEqual(eq.Bands[0].FrequencyHz, after.Bands[0].FrequencyHz);
        Assert.Equal("", after.PresetId); // edited: no longer the preset as shipped
        Assert.True(f.Persisted > 0);     // saved app-wide
    }

    // ── the noise layer, no window ──

    [Fact]
    public void The_noise_layer_switches_and_edits_the_receive_equaliser_with_no_window_open()
    {
        using var f = new NoiseFixture();
        var layer = new NoiseLayerKeys(f.Noise);
        Assert.True(layer.Handle(NoiseLayerKeys.EqualizerKey));
        Assert.True(f.Noise.EqualizerOn(DenoiseDirection.Receive));
        Assert.True(f.Pipeline.Equalizer.IsActive || f.Pipeline.EqualizerSetup.Enabled);
        Assert.True(layer.Handle(NoiseLayerKeys.AddBandKey));
        Assert.Single(f.Noise.EqualizerSetup(DenoiseDirection.Receive).Bands);
        Assert.True(layer.Handle(Keys.Tab));  // Frequency
        Assert.True(layer.Handle(Keys.Tab));  // Gain
        Assert.True(layer.Handle(Keys.Up));
        Assert.Equal(0.5f, f.Noise.EqualizerSetup(DenoiseDirection.Receive).Bands[0].GainDb);
        Assert.True(layer.Handle(Keys.Space));
        Assert.Contains("band 1", f.Spoken.Last, StringComparison.OrdinalIgnoreCase);
        Assert.True(layer.Handle(NoiseLayerKeys.RemoveBandKey));
        Assert.Empty(f.Noise.EqualizerSetup(DenoiseDirection.Receive).Bands);
        Assert.True(layer.Handle(NoiseLayerKeys.EqualizerKey));
        Assert.False(f.Noise.EqualizerOn(DenoiseDirection.Receive));
        // An engine key puts the arrows back on the engines.
        Assert.True(layer.Handle(Keys.Control | Keys.R));
    }

    [Fact]
    public void The_de_esser_key_in_the_receive_direction_says_it_is_transmit_only_and_changes_nothing()
    {
        using var f = new NoiseFixture();
        var layer = new NoiseLayerKeys(f.Noise);
        bool before = f.Noise.DeEsserOn;
        Assert.True(layer.Handle(NoiseLayerKeys.DeEsserKey));
        Assert.Equal(before, f.Noise.DeEsserOn);
        Assert.NotEmpty(f.Spoken.Last);
    }

    [Fact]
    public void Every_new_layer_key_has_an_inventory_row()
    {
        var rows = KeyInventory.LayerCommands(KeyInventory.NoiseLayerContext);
        // Ctrl+Shift+E is this equaliser's since 2026-11-26 (#490); Ctrl+E is the radio's own.
        foreach (string display in new[] { "Ctrl+Shift+E", "Ctrl+E", "Ctrl+Y", "Left / Right", "Insert", "Delete" })
            Assert.Contains(rows, r => r.KeyDisplay == display);
    }
}
