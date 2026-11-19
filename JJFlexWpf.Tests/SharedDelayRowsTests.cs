using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P, phase 0 (#873): Settings, Advanced, Measured delays says,
/// per computer engine, its own delay and its family's shared delay (Noel
/// 2026-11-04 19:57), with the set taken from the receive favourites (the
/// assumption until Noel rules which engines set it). Windowless: the rows are
/// built from the controller, never from a dialog.
/// </summary>
public sealed class SharedDelayRowsTests
{
    public SharedDelayRowsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
    }

    private static string Figure(int ms) => Lexicon.Get("audio.noise_surface.delay_figure", ("ms", ms));

    private static void Favour(NoiseFixture fx, string id, string? model = null) =>
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = id, Model = model });

    [Fact]
    public void The_shared_delay_is_the_slowest_favourite_of_the_family_and_each_row_says_its_own_and_the_padding()
    {
        using var fx = new NoiseFixture();
        Favour(fx, "rnnoise");
        Favour(fx, "deepfilternet3");
        Favour(fx, "nnr", "premium");
        Favour(fx, "radio_nr"); // a radio engine in the favourites sets nothing

        // NNR Premium's expected figure, before the helper has opened it: 81 ms, 3,888 samples.
        Assert.Equal(81 * 48, fx.Noise.SharedDelaySamples(DenoiseFamily.Neural));
        Assert.Equal(0, fx.Noise.SharedDelaySamples(DenoiseFamily.Spectral));

        Assert.Equal(Lexicon.Get("audio.noise_advanced.delay_line_shared", ("engine", fx.Noise.NameOf("rnnoise")),
                ("delay", Figure(20)), ("shared", Figure(81)), ("more", Figure(61))),
            fx.Noise.SharedDelayRow("rnnoise"));
        Assert.Equal(Lexicon.Get("audio.noise_advanced.delay_line_shared", ("engine", fx.Noise.NameOf("deepfilternet3")),
                ("delay", Figure(30)), ("shared", Figure(81)), ("more", Figure(51))),
            fx.Noise.SharedDelayRow("deepfilternet3"));
        // NNR is set to Standard now, and only Premium is a favourite: outside the set.
        Assert.Equal(Lexicon.Get("audio.noise_advanced.delay_line_outside", ("engine", fx.Noise.NameOf("nnr")),
                ("delay", Figure(71)), ("shared", Figure(81))),
            fx.Noise.SharedDelayRow("nnr"));
        // No spectral engine is a favourite: no shared delay for that family, and the row reads as it always did.
        Assert.Equal(Lexicon.Get("audio.noise_advanced.delay_line", ("engine", fx.Noise.NameOf("nr4")), ("delay", Figure(11))),
            fx.Noise.SharedDelayRow("nr4"));
        Assert.Equal("", fx.Noise.SharedDelayRow("radio_nr"));
    }

    [Fact]
    public void A_favourite_saved_without_a_model_stands_for_the_engine_in_whatever_model_it_is_set_to()
    {
        using var fx = new NoiseFixture();
        Favour(fx, "nnr");
        Favour(fx, "rnnoise");
        Assert.True(fx.Noise.InSharedDelaySet("nnr"));
        Assert.Equal(71 * 48, fx.Noise.SharedDelaySamples(DenoiseFamily.Neural));
        Assert.True(fx.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new EngineSettingValue(0, "premium")));
        Assert.True(fx.Noise.InSharedDelaySet("nnr"));
        Assert.Equal(81 * 48, fx.Noise.SharedDelaySamples(DenoiseFamily.Neural));
    }

    [Fact]
    public void A_running_engine_s_own_figure_is_used_and_remembered_after_it_stops()
    {
        using var fx = new NoiseFixture(rnnoiseLatency: 1000);
        Favour(fx, "rnnoise");
        fx.Pipeline.RnnEnabled = true;
        fx.Settle();
        Assert.Equal(1000, fx.Noise.SharedDelaySamples(DenoiseFamily.Neural));
        fx.Pipeline.SelectPcEngine("deepfilternet3");
        fx.Settle();
        Assert.Equal("deepfilternet3", fx.Pipeline.NeuralSlot.RunningEngineId);
        // RNNoise no longer runs; its measured 1,000 still stands, not the expected 960.
        Assert.Equal(1000, fx.Noise.SharedDelaySamples(DenoiseFamily.Neural));
    }

    [Fact]
    public void The_set_is_an_input_one_function_with_the_members_given()
    {
        var set = new List<ReceiveSharedDelay.Member> { new("a", null), new("b", "x"), new("c", null) };
        var figures = new Dictionary<string, int?> { ["a"] = 500, ["b"] = 3000, ["c"] = null };
        Assert.Equal(3000, ReceiveSharedDelay.SharedSamples(set, m => figures[m.EngineId]));
        Assert.Equal(500, ReceiveSharedDelay.SharedSamples(set.Where(m => m.EngineId != "b"), m => figures[m.EngineId]));
        Assert.Equal(0, ReceiveSharedDelay.SharedSamples(new ReceiveSharedDelay.Member[0], _ => 9));

        var plan = new SharedDelayPlan(3000, set);
        Assert.True(plan.Contains("a", "anything"));
        Assert.True(plan.Contains("b", "x"));
        Assert.False(plan.Contains("b", "y"));
        Assert.False(plan.Contains("d", null));
    }
}
