using System;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E6: per-mode memory replaces the "Voice Modes Only"
/// gate. Windowless.
/// </summary>
public sealed class NoiseModeMemoryTests
{
    public NoiseModeMemoryTests() => NoiseReductionPerChannelTests.AssertWindowless();

    [Theory]
    [InlineData("USB", NoiseModeGroup.Voice)]
    [InlineData("LSB", NoiseModeGroup.Voice)]
    [InlineData("AM", NoiseModeGroup.Voice)]
    [InlineData("FM", NoiseModeGroup.Voice)]
    [InlineData("CW", NoiseModeGroup.Cw)]
    [InlineData("CWL", NoiseModeGroup.Cw)]
    [InlineData("DIGU", NoiseModeGroup.Digital)]
    [InlineData("DIGL", NoiseModeGroup.Digital)]
    [InlineData("FDM", NoiseModeGroup.Digital)]
    [InlineData("RTTY", NoiseModeGroup.Digital)]
    [InlineData("", NoiseModeGroup.Voice)]
    public void Modes_fall_into_three_groups(string mode, NoiseModeGroup group) =>
        Assert.Equal(group, NoiseModeRules.GroupOf(mode));

    // ── Premium CW (question 11; question 4 ruled 21:10: unmeasured is Premium) ──

    [Theory]
    [InlineData(null, "premium_cw", false)]
    [InlineData(4.9, "premium_cw", true)]
    [InlineData(5.0, "standard_cw", true)]
    [InlineData(12.0, "standard_cw", true)]
    [InlineData(double.NaN, "premium_cw", false)]
    public void Premium_CW_is_chosen_when_measured_under_five_milliseconds_and_while_unmeasured(double? cost, string model, bool measured)
    {
        var (chosen, wasMeasured, _) = NoiseModeRules.ChooseCwModel(new NoiseFixture.CwCost { PremiumCw99thPercentileMs = cost });
        Assert.Equal(model, chosen);
        Assert.Equal(measured, wasMeasured);
        Assert.Equal("premium_cw", NoiseModeRules.UnmeasuredCwModel);
    }

    // ── Starting points ──

    [Fact]
    public void Turning_the_neural_slot_on_the_first_time_in_voice_starts_DeepFilterNet3()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.SetSlotOn(DenoiseFamily.Neural, true, "home");
        Assert.True(f.Noise.IsOn("deepfilternet3"));
        Assert.StartsWith("First time in a voice mode: DeepFilterNet3 is on with the limit at 9 decibels.", f.Spoken.Last);
        Assert.Equal(SpeechSubject.NoiseModeStart, f.Spoken.Said[^1].Subject);
        Assert.True(f.Config.NoiseModeMemory.Voice.Known);
        // The second time it is the operator's own choice.
        f.Noise.SetSlotOn(DenoiseFamily.Neural, false, "home");
        f.Noise.Toggle("rnnoise", "home");
        f.Noise.SetSlotOn(DenoiseFamily.Neural, false, "home");
        f.Noise.SetSlotOn(DenoiseFamily.Neural, true, "home");
        Assert.True(f.Noise.IsOn("rnnoise"));
    }

    [Fact]
    public void Where_DeepFilterNet3_cannot_run_voice_starts_RNNoise()
    {
        using var f = new NoiseFixture();
        f.Unavailable["deepfilternet3"] = Unavailability.ModelMissing;
        f.Noise.Attach("LSB");
        f.Noise.SetSlotOn(DenoiseFamily.Neural, true, "home");
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.StartsWith("First time in a voice mode: RNNoise is on", f.Spoken.Last);
    }

    [Fact]
    public void The_first_time_in_CW_starts_NNR_on_Premium_CW_while_unmeasured_and_turns_spectral_off()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Toggle("nr2", "test");
        f.Spoken.Clear();
        f.Radio.Mode = "CW";
        f.Noise.OnModeChanged("CW");
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.Equal("premium_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.False(f.Pipeline.SpectralSlot.Enabled);
        Assert.Contains(f.Spoken.Said, s => s.Text.StartsWith("First time in CW: NNR Premium CW is on. This computer's speed has not been measured yet."));
        Assert.True(f.Config.NoiseModeMemory.Cw.Known);
    }

    [Fact]
    public void A_measured_slow_computer_starts_CW_on_Standard_CW_and_says_why()
    {
        using var f = new NoiseFixture();
        f.Cost.PremiumCw99thPercentileMs = 8;
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.OnModeChanged("CW");
        Assert.Equal("standard_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.Contains(f.Spoken.Said, s => s.Text.Contains("measured too slow for Premium CW"));
    }

    [Fact]
    public void The_first_time_in_digital_turns_the_computers_engines_off_and_says_so()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Toggle("nr4", "test");
        f.Noise.OnModeChanged("DIGU");
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
        Assert.False(f.Pipeline.SpectralSlot.Enabled);
        Assert.StartsWith("First time in a digital mode", f.Spoken.Last);
        // And anything the operator turns on in digital is allowed.
        f.Noise.Toggle("rnnoise", "test");
        Assert.True(f.Noise.IsOn("rnnoise"));
    }

    [Fact]
    public void Entering_a_group_with_nothing_on_applies_nothing_and_says_nothing()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Spoken.Clear();
        f.Noise.OnModeChanged("CW");
        Assert.Empty(f.Spoken.Said);
        Assert.False(f.Config.NoiseModeMemory.Cw.Known);
    }

    // ── Restore on return ──

    [Fact]
    public void Each_group_remembers_what_was_last_used_in_it_and_says_only_what_changed()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");           // voice: RNNoise
        f.Noise.OnModeChanged("CW");                 // CW: starting point, NNR
        f.Noise.Toggle("nr4", "test");               // CW: NR4 too
        f.Spoken.Clear();
        f.Noise.OnModeChanged("USB");
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.False(f.Pipeline.SpectralSlot.Enabled);
        Assert.Equal("Back in a voice mode: NR4 off, RNNoise on.", f.Spoken.Last);
        f.Noise.OnModeChanged("CW");
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.True(f.Noise.IsOn("nr4"));
        // A change within the same group says nothing.
        f.Spoken.Clear();
        f.Noise.OnModeChanged("CWL");
        Assert.Empty(f.Spoken.Said);
    }

    // ── NNR's CW models, NR2's tracking ──

    [Fact]
    public void Entering_CW_with_a_voice_NNR_model_swaps_to_a_CW_model_and_leaving_gives_the_voice_model_back()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
        f.Noise.Toggle("nnr", "test");
        f.Noise.OnModeChanged("CW");          // the first time: CW's starting point, on a CW model
        Assert.Equal("premium_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        f.Noise.OnModeChanged("USB");         // the voice model comes back
        Assert.Equal("premium", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        f.Spoken.Clear();
        f.Noise.OnModeChanged("CW");          // remembered now: the swap is said on its own
        Assert.Equal("premium_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.Contains(f.Spoken.Said, s => s.Text == "NNR switched to Premium CW for CW; the voice model comes back when you leave CW.");
        // In CW the model lever offers only the CW models.
        var model = f.Noise.Levers("nnr", false).Single(l => l.Key == "model");
        Assert.Equal(new[] { "standard_cw", "premium_cw" }, f.Noise.OptionsFor(model).Select(o => o.Name));
        f.Noise.OnModeChanged("USB");
        Assert.Equal("premium", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.Equal(4, f.Noise.OptionsFor(model).Count);
    }

    [Fact]
    public void NR2_tracks_Steady_in_CW_and_the_operators_own_choice_sticks_per_side()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        Assert.Equal("osms", f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
        f.Noise.OnModeChanged("CW");
        Assert.Equal("nstat", f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
        var tracking = f.Noise.Levers("nr2", true).Single(l => l.Key == "noise_estimator");
        f.Noise.Set(tracking, null, "mmse", "test");
        f.Noise.OnModeChanged("USB");
        Assert.Equal("osms", f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
        f.Noise.OnModeChanged("CW");
        Assert.Equal("mmse", f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
        // Return to defaults in CW goes to Steady.
        f.Noise.ReturnToDefaults("nr2", "test");
        Assert.Equal("nstat", f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
    }

    // ── The upgrade path (question 6, ruled 21:18) ──

    [Fact]
    public void An_operator_upgrading_from_a_version_1_receive_config_gets_each_groups_starting_point_once()
    {
        using var f = new NoiseFixture();
        var old = new AudioOutputConfig
        {
            ReceiveDenoiseVersion = 1,
            ReceiveVoiceModesOnly = true,
            ReceiveNeural = new ReceiveDenoiseSlotConfig { Enabled = true, EngineId = "rnnoise" },
        };
        ReceiveDenoiseConfig.Map(old, f.Pipeline, save: false);
        f.Config.ReceiveNeural = old.ReceiveNeural;
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.False(f.Config.NoiseModeMemory.Cw.Known);
        f.Radio.Mode = "CW";
        f.Noise.Attach("CW");
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.True(f.Config.NoiseModeMemory.Cw.Known);
        Assert.Contains(f.Spoken.Said, s => s.Text.StartsWith("First time in CW"));
        // Nobody hears RNNoise in CW after an upgrade; digital switches the engines off.
        f.Noise.OnModeChanged("DIGU");
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
    }

    [Theory]
    [InlineData("USB")]
    [InlineData("CW")]
    [InlineData("DIGU")]
    [InlineData("RTTY")]
    public void No_mode_holds_a_computer_engine_back(string mode)
    {
        // Replaces the retired gate's test: RNNoise (the fake halves the
        // audio) and NR4 both process in every mode, even from a config that
        // had "Voice Modes Only" on.
        using var f = new NoiseFixture();
        ReceiveDenoiseConfig.Map(new AudioOutputConfig { ReceiveDenoiseVersion = 1, ReceiveVoiceModesOnly = true,
            ReceiveNeural = new ReceiveDenoiseSlotConfig { Enabled = true, EngineId = "rnnoise" },
            ReceiveSpectral = new ReceiveDenoiseSlotConfig { Enabled = true, EngineId = "nr4" } }, f.Pipeline, save: false);
        f.Pipeline.SetCurrentMode(mode);
        f.Pipeline.NeuralSlot.SetStrength("rnnoise", 1);
        f.Settle();
        var audio = Enumerable.Repeat(0.4f, 960).ToArray();
        f.Pipeline.Process(audio);
        Assert.All(audio, v => Assert.Equal(0.1f, v, 4)); // 0.4 halved twice
    }
}
