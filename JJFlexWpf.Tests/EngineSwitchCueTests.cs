using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P, phase 1 (#873): the Speech and Beep switches for a receive
/// engine change (ruled 2026-11-08 12:57, three independent switches; Mute is
/// proved on the audio in <see cref="AlignedSwitchTests"/>), the outlier rule
/// for the shared delay (Noel 2026-11-04 19:58, applied 2026-11-19), and the
/// plan the controller hands the slots. Windowless; the beep is a counter.
/// </summary>
public sealed class EngineSwitchCueTests
{
    public EngineSwitchCueTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static (NoiseFixture Fx, int[] Beeps) Rig(bool speech, bool beep)
    {
        var fx = new NoiseFixture();
        fx.Config.NoiseSwitchSpeech = speech;
        fx.Config.NoiseSwitchBeep = beep;
        var beeps = new int[1];
        fx.Noise.SwitchBeep = () => beeps[0]++;
        fx.Noise.SetOn("rnnoise", true, "test");
        fx.Settle();
        fx.Spoken.Clear();
        return (fx, beeps);
    }

    private static string Replacing(NoiseFixture fx) =>
        Lexicon.Get("audio.noise_surface.on_replacing", ("engine", fx.Noise.NameOf("deepfilternet3")), ("other", fx.Noise.NameOf("rnnoise")));

    [Fact]
    public void Speech_on_says_the_switch_and_beep_off_plays_nothing_today_s_behaviour()
    {
        var (fx, beeps) = Rig(speech: true, beep: false);
        using (fx)
        {
            fx.Noise.SetOn("deepfilternet3", true, "test");
            Assert.Contains(fx.Spoken.Said, s => s.Subject == SpeechSubject.NoiseEngineSwitch && s.Text.StartsWith(Replacing(fx)));
            Assert.Equal(0, beeps[0]);
        }
    }

    [Fact]
    public void Speech_off_silences_only_the_switch_sentence_and_beep_marks_the_switch()
    {
        var (fx, beeps) = Rig(speech: false, beep: true);
        using (fx)
        {
            fx.Noise.SetOn("deepfilternet3", true, "test");
            Assert.DoesNotContain(fx.Spoken.Said, s => s.Subject == SpeechSubject.NoiseEngineSwitch);
            Assert.Equal(1, beeps[0]);
            // Turning an engine OFF is not a switch: still said.
            fx.Noise.SetOn("deepfilternet3", false, "test");
            Assert.Contains(fx.Spoken.Said, s => s.Subject == SpeechSubject.NoiseEngineSwitch);
            Assert.Equal(1, beeps[0]);
        }
    }

    [Fact]
    public void Turning_an_engine_on_with_nothing_to_replace_is_not_a_switch()
    {
        using var fx = new NoiseFixture();
        fx.Config.NoiseSwitchSpeech = false;
        fx.Config.NoiseSwitchBeep = true;
        int beeps = 0;
        fx.Noise.SwitchBeep = () => beeps++;
        fx.Noise.SetOn("rnnoise", true, "test");
        Assert.Contains(fx.Spoken.Said, s => s.Subject == SpeechSubject.NoiseEngineSwitch);
        Assert.Equal(0, beeps);
    }

    [Fact]
    public void A_new_setup_has_speech_on_and_beep_and_mute_off()
    {
        var c = new AudioOutputConfig();
        Assert.True(c.NoiseSwitchSpeech);
        Assert.False(c.NoiseSwitchBeep);
        Assert.False(c.NoiseSwitchMute);
    }

    // ── The outlier rule ──

    [Fact]
    public void An_engine_above_100_ms_never_sets_the_shared_delay_and_is_not_held_to_it()
    {
        Assert.False(ReceiveSharedDelay.IsOutlier(4800));
        Assert.True(ReceiveSharedDelay.IsOutlier(4801));
        Assert.False(ReceiveSharedDelay.IsOutlier(null));

        using var fx = new NoiseFixture();
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nr2" });
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nr4" });
        // NR2 (105 ms) is the outlier: the spectral family shares NR4's 512 samples.
        Assert.Equal(512, fx.Noise.SharedDelaySamples(DenoiseFamily.Spectral));
        Assert.False(fx.Noise.InSharedDelaySet("nr2"));
        Assert.True(fx.Noise.InSharedDelaySet("nr4"));
        Assert.Equal(Lexicon.Get("audio.noise_advanced.delay_line_outlier", ("engine", fx.Noise.NameOf("nr2")),
                ("delay", Lexicon.Get("audio.noise_surface.delay_figure", ("ms", 105)))),
            fx.Noise.SharedDelayRow("nr2"));
    }

    [Fact]
    public void The_same_test_would_catch_NNR_Premium_if_it_ever_stood_out_the_same_way()
    {
        var set = new[] { new ReceiveSharedDelay.Member("rnnoise", null), new ReceiveSharedDelay.Member("nnr", "premium") };
        var today = ReceiveSharedDelay.PlanFor(set, m => m.EngineId == "nnr" ? 3894 : 960, out var none);
        Assert.Empty(none);
        Assert.Equal(3894, today.Samples);
        var slower = ReceiveSharedDelay.PlanFor(set, m => m.EngineId == "nnr" ? 5000 : 960, out var outliers);
        Assert.Equal(new[] { new ReceiveSharedDelay.Member("nnr", "premium") }, outliers);
        Assert.Equal(960, slower.Samples);
        Assert.False(slower.Contains("nnr", "premium"));
    }

    // ── The plan reaches the slots ──

    [Fact]
    public void Marking_a_favourite_hands_the_slot_its_new_plan()
    {
        using var fx = new NoiseFixture();
        Assert.Empty(fx.Pipeline.NeuralSlot.SharedPlan.Members);
        fx.Noise.ToggleFavourite("deepfilternet3", "test");
        fx.Noise.ToggleFavourite("rnnoise", "test");
        var plan = fx.Pipeline.NeuralSlot.SharedPlan;
        Assert.True(plan.Contains("deepfilternet3", null));
        Assert.True(plan.Contains("rnnoise", RnnoiseModels.Default));
        Assert.Equal(1440, plan.Samples);
    }

    [Fact]
    public void Loading_a_receive_configuration_hands_both_slots_their_first_plan()
    {
        using var fx = new NoiseFixture();
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nr4" });
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nr2" });
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nnr", Model = "premium" });
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "deepfilternet3" });
        ReceiveSharedDelay.PublishExpected(fx.Pipeline, fx.Config.NoiseFavourites);
        Assert.Equal(512, fx.Pipeline.SpectralSlot.SharedPlan.Samples);
        Assert.False(fx.Pipeline.SpectralSlot.SharedPlan.Contains("nr2", null));
        Assert.Equal(81 * 48, fx.Pipeline.NeuralSlot.SharedPlan.Samples);
    }
}
