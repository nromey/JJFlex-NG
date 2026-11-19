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
}
