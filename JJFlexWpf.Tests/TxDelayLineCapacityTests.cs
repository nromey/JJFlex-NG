using System;
using JJPortaudio;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1293 (Sprint 49 Track I phase 2): the conditioner's delay line, which
/// keeps the residual's input copy aligned to the chain, holds the whole
/// chain's worst case, so reaching it allocates nothing on the audio thread.
/// The worst case is computed here from the app's own constants and the
/// helper's offered delays, so a deeper pipeline or a new stage that outgrows
/// the line fails this test rather than allocating on the air. Windowless:
/// MTA asserted; no helper, no window, no sound.
/// </summary>
public sealed class TxDelayLineCapacityTests
{
    public TxDelayLineCapacityTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private const int Frame = 480;
    private const int Carry = Frame - 1;                       // a bank's carry once its input is not frame-aligned
    // The helper's offered delays at 48 kHz (its README): NR2 4,064; NNR 2,454 (standard and premium models).
    private const int Nr2Algorithmic = 4064, NnrPremiumAlgorithmic = 2454;
    // dexp with its side-channel filter on (16 samples); the app never turns WDSP's audio delay on.
    private const int DexpAlgorithmic = 16;

    private static int TwoSlotsWorst =>
        Nr2Algorithmic + WdspPipelineDepth.Nr2 * Frame + Carry
        + NnrPremiumAlgorithmic + WdspPipelineDepth.NnrPremium * Frame + Carry;

    private static int ExpanderWorst =>
        Math.Max(TxNoiseGate.LookAheadFramesFor(TxNoiseGate.MaxLookAheadMs, 192000),
                 DexpAlgorithmic + WdspPipelineDepth.Dexp * Frame + Carry);

    [Fact]
    public void The_whole_chains_worst_case_fits_the_preallocated_line()
    {
        Assert.Equal(9876, TwoSlotsWorst);       // NR2 5,024 and NNR Premium 3,894, plus two carries
        Assert.Equal(1920, ExpanderWorst);        // the look-ahead at 192 kHz; WDSP's in its place is 1,455
        Assert.True(TwoSlotsWorst + ExpanderWorst <= TxAudioConditioner.DelayLineCapacityFrames,
            $"the worst case {TwoSlotsWorst + ExpanderWorst} frames outgrows the line's {TxAudioConditioner.DelayLineCapacityFrames}");
    }

    /// <summary>Run a conditioner to the given delays, then measure what one change to them allocates.</summary>
    private static (long Bytes, int Delay) Reach(int noiseDelay, bool wdspInPlace, uint rate)
    {
        int nr = 0, wdsp = 0;
        bool keyed = false;
        var c = new TxAudioConditioner { KeySource = () => keyed };
        c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, GateSetting = false, LookAheadMs = TxNoiseGate.MaxLookAheadMs });
        c.NoiseReducer = (b, n, r) => { };
        c.NoiseReducerAppliedLatencySamples = () => nr;
        if (wdspInPlace)
        {
            c.ExpanderReplacement = (b, n, r, k) => true;
            c.ExpanderReplacementAppliedLatencySamples = () => wdsp;
        }
        var buf = new float[960];
        for (int i = 0; i < 10; i++) c.Process(buf, buf.Length, rate);   // a small delay, laid out
        nr = noiseDelay;
        wdsp = DexpAlgorithmic + WdspPipelineDepth.Dexp * Frame + Carry;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) c.Process(buf, buf.Length, rate);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return (bytes, c.AppliedDelayFrames);
    }

    [Theory]
    [InlineData(false, 192000u)]
    [InlineData(false, 48000u)]
    [InlineData(true, 48000u)]
    public void Reaching_two_slow_slots_plus_the_expander_allocates_nothing_on_the_callback(bool wdspInPlace, uint rate)
    {
        var (bytes, delay) = Reach(TwoSlotsWorst, wdspInPlace, rate);
        int expander = wdspInPlace ? DexpAlgorithmic + WdspPipelineDepth.Dexp * Frame + Carry
            : TxNoiseGate.LookAheadFramesFor(TxNoiseGate.MaxLookAheadMs, rate);
        Assert.Equal(TwoSlotsWorst + expander, delay);           // the delay really reached the worst case
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Positive_control_a_delay_past_the_capacity_is_seen_allocating()
    {
        var (bytes, _) = Reach(TxAudioConditioner.DelayLineCapacityFrames + 1, false, 48000);
        Assert.True(bytes > 0, "the measurement cannot see the growth it guards against");
    }
}
