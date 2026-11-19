using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// What the operator hears when a receive engine cannot keep up (Sprint 49
/// Track L, #866): said once, naming the engine, with the alert tone, queued
/// behind whatever is being said; again only after the engine has been seen to
/// keep up; and nothing is ever switched on the operator's behalf.
/// </summary>
public sealed class EngineKeepUpWarningTests
{
    public EngineKeepUpWarningTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static EngineKeepUpResult Result(string id, EngineKeepUpChange change) =>
        new(DenoiseFamily.Neural, id, change, false, change == EngineKeepUpChange.FellBehind ? 20 : 0, 100, 12, 10);

    [Fact]
    public void A_fall_is_said_once_naming_the_engine_with_the_tone_and_queued()
    {
        using var f = new NoiseFixture();
        int tones = 0;
        f.Noise.KeepUpTone = () => { tones++; return true; };
        f.Noise.OnKeepUpResult(Result("deepfilternet3", EngineKeepUpChange.FellBehind));
        var said = Assert.Single(f.Spoken.Said);
        Assert.Equal(SpeechSubject.NoiseKeepUp, said.Subject);
        Assert.Contains(Lexicon.Get("audio.denoise.deepfilternet3"), said.Text);
        Assert.NotEqual("audio.noise_surface.cannot_keep_up", said.Text);
        Assert.Contains(0, f.Spoken.SaidAfter);
        Assert.Equal(1, tones);

        // Falling behind again with no clean run in between: not said again, no tone.
        f.Noise.OnKeepUpResult(Result("deepfilternet3", EngineKeepUpChange.FellBehind));
        Assert.Single(f.Spoken.Said);
        Assert.Equal(1, tones);
    }

    [Fact]
    public void After_the_engine_is_seen_to_keep_up_a_fresh_fall_is_said_again_and_recovery_itself_is_silent()
    {
        foreach (var back in new[] { EngineKeepUpChange.Recovered, EngineKeepUpChange.KeepingUp })
        {
            using var f = new NoiseFixture();
            f.Noise.KeepUpTone = () => true;
            f.Noise.OnKeepUpResult(Result("nnr", EngineKeepUpChange.FellBehind));
            f.Noise.OnKeepUpResult(Result("nnr", back));
            Assert.Single(f.Spoken.Said);
            Assert.Empty(f.Noise.KeepUpWarned);
            f.Noise.OnKeepUpResult(Result("nnr", EngineKeepUpChange.FellBehind));
            Assert.Equal(2, f.Spoken.Said.Count);
        }
    }

    [Fact]
    public void Each_engine_has_its_own_once_and_keeping_up_alone_says_nothing()
    {
        using var f = new NoiseFixture();
        f.Noise.KeepUpTone = () => true;
        f.Noise.OnKeepUpResult(Result("rnnoise", EngineKeepUpChange.KeepingUp));
        Assert.Empty(f.Spoken.Said);
        f.Noise.OnKeepUpResult(Result("rnnoise", EngineKeepUpChange.FellBehind));
        f.Noise.OnKeepUpResult(Result("nr4", EngineKeepUpChange.FellBehind));
        Assert.Equal(2, f.Spoken.Said.Count);
        Assert.Contains(Lexicon.Get("audio.denoise.nr4"), f.Spoken.Said[1].Text);
    }

    [Fact]
    public void The_warning_never_switches_lightens_or_turns_off_an_engine()
    {
        using var f = new NoiseFixture();
        f.Noise.KeepUpTone = () => true;
        var p = f.Pipeline;
        Assert.True(p.SelectPcEngine("deepfilternet3", out var refusal), refusal);
        p.RnnEnabled = true; p.SpectralEnabled = true;
        Assert.True(p.SelectPcEngine("nr4", out refusal), refusal);
        f.Settle();
        float limit = (float)p.NeuralSlot.GetSetting("deepfilternet3", "attenuation_limit_db").Number;
        float strength = p.NeuralSlot.GetStrength("deepfilternet3");
        int persisted = f.Persisted;
        f.Noise.OnKeepUpResult(Result("deepfilternet3", EngineKeepUpChange.FellBehind));
        f.Noise.OnKeepUpResult(new EngineKeepUpResult(DenoiseFamily.Spectral, "nr4", EngineKeepUpChange.FellBehind, false, 20, 100, 12, 10));
        Assert.Equal(2, f.Spoken.Said.Count);
        Assert.Equal("deepfilternet3", p.NeuralSlot.ChosenEngineId);
        Assert.Equal("nr4", p.SpectralSlot.ChosenEngineId);
        Assert.True(p.RnnEnabled);
        Assert.True(p.SpectralEnabled);
        Assert.Equal(limit, (float)p.NeuralSlot.GetSetting("deepfilternet3", "attenuation_limit_db").Number);
        Assert.Equal(strength, p.NeuralSlot.GetStrength("deepfilternet3"));
        Assert.Equal(persisted, f.Persisted);
    }

    [Fact]
    public void A_tone_that_cannot_play_still_leaves_the_words()
    {
        using var f = new NoiseFixture();
        f.Noise.KeepUpTone = () => throw new System.InvalidOperationException("no alert device");
        f.Noise.OnKeepUpResult(Result("deepfilternet3", EngineKeepUpChange.FellBehind));
        Assert.Single(f.Spoken.Said);
    }
}
