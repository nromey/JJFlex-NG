using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N, the receive noise round: #1124, #1126, #1131, #1148,
/// #1150 and #1152 (#1147 is pinned in <see cref="PcDenoiseSlotTests"/>).
/// </summary>
/// <remarks>
/// Windowless: the noise controller over <see cref="NoiseFixture"/>'s fakes,
/// on an MTA thread, with no window. Every test asserts the desk is safe first.
/// </remarks>
public sealed class ReceiveNoiseRoundTests
{
    public ReceiveNoiseRoundTests() => NoiseReductionPerChannelTests.AssertWindowless();

    // ── #1152: NR4 has a delay figure before it has run ──

    [Fact]
    public void NR4_has_a_delay_figure_before_it_has_run_in_both_directions()
    {
        using var f = new NoiseFixture();   // the fake NR4 reports no latency, as a real one does before it runs
        Assert.Equal(0, f.Pipeline.SpectralSlot.CurrentLatencySamples("nr4"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.delay", ("ms", 11)), f.Noise.DelayText("nr4"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.delay_figure", ("ms", 11)), f.Noise.DelayFigure("nr4"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.delay", ("ms", 11)), f.Noise.DelayText("nr4", DenoiseDirection.Transmit));
    }

    // ── #1126: the digital group's first visit is its first visit ──

    [Fact]
    public void A_first_digital_visit_with_nothing_on_is_remembered_so_a_later_visit_is_not_called_the_first()
    {
        // The e4 listen: DIGU from CW with nothing on said nothing; DIGL later,
        // from AM with RNNoise on, said "First time in a digital mode".
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.OnModeChanged("CW");
        f.Spoken.Clear();
        f.Noise.OnModeChanged("DIGU");
        Assert.Empty(f.Spoken.Said);                        // nothing changed, nothing said
        Assert.True(f.Config.NoiseModeMemory.Digital.Known);
        f.Noise.OnModeChanged("AM");
        f.Noise.Toggle("rnnoise", "test");
        f.Spoken.Clear();
        f.Noise.OnModeChanged("DIGL");
        Assert.False(f.Pipeline.NeuralSlot.Enabled);         // digital still turns it off
        Assert.DoesNotContain(f.Spoken.Said, s => s.Text.StartsWith("First time in a digital mode", StringComparison.Ordinal));
        Assert.Contains(f.Spoken.Said, s => s.Text.StartsWith("Back in", StringComparison.Ordinal) && s.Text.Contains("RNNoise off"));
        // CW keeps its rule: a first visit with nothing on stays unknown.
        Assert.False(f.Config.NoiseModeMemory.Cw.Known);
    }
}
