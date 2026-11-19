using System;
using System.IO;
using System.Text.RegularExpressions;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track H2, rulings repair 1: #1177, the Slice menu's plain
/// "Release Slice" row with the last slice active. Pure arithmetic and a source
/// check; no window, no radio.
/// </summary>
public sealed class RulingsRepairOneTests
{
    [Fact]
    public void Releasing_the_active_last_slice_moves_the_active_slice_off_it_first()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // Two slices, B (the last) active and transmitting: both move to A.
        Assert.Equal((0, 0), SliceReleasePlan.Before(1, 2, active: 1, transmit: 1, canTransmit: true));
        // B active, A transmitting: only the active slice moves.
        Assert.Equal((0, 0), SliceReleasePlan.Before(1, 2, active: 1, transmit: 0, canTransmit: true));
        // Four slices, D released while B is active: nothing moves.
        Assert.Equal((1, 2), SliceReleasePlan.Before(3, 4, active: 1, transmit: 2, canTransmit: true));
        // No transmit (a receive-only client): the transmit slice is left alone.
        Assert.Equal((0, 1), SliceReleasePlan.Before(1, 2, active: 1, transmit: 1, canTransmit: false));
        // Release Active Slice's own case: A released while active goes to B.
        Assert.Equal((1, 1), SliceReleasePlan.Before(0, 2, active: 0, transmit: 0, canTransmit: true));
        // Nowhere to move with one slice.
        Assert.Null(SliceReleasePlan.Before(0, 1, active: 0, transmit: 0, canTransmit: true));
    }

    /// <summary>
    /// #1149: the transmit callback path allocates nothing a buffer. One frame
    /// through the pipeline every producer uses (injection, the conditioner with
    /// the noise chain as its reducer, the meter's slot, encode, send), unkeyed
    /// and keyed, chain off, and with a held unkey's drain running. Measured on
    /// this thread with the runtime's own count of bytes allocated.
    /// </summary>
    [Fact]
    public void The_transmit_callback_path_allocates_nothing_a_buffer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var chain = new TxNoiseChain(TxNoiseChainTests.Catalog());
        bool keyed = false;
        var c = new JJPortaudio.TxAudioConditioner();
        Func<bool> key = () => keyed;
        c.KeySource = key;
        chain.KeySource = key;
        c.NoiseReducer = (b, n, r) => chain.Process(b, n, r, c.Keyed);
        c.NoiseReducerAppliedLatencySamples = () => chain.AppliedLatencySamples;
        var encoded = new byte[40];
        var pipeline = new JJPortaudio.TxFramePipeline
        {
            Conditioner = c.Process,
            KeySource = key,
            Encode = _ => encoded,
            Handler = _ => { },
        };
        var buffer = new float[960];
        void Run(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                for (int s = 0; s < buffer.Length; s++) buffer[s] = 0.25f;
                pipeline.Emit(buffer, buffer.Length, 48000);
            }
        }
        long Measure(int frames)
        {
            Run(5); // first frames size the scratch buffers
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(frames);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, Measure(101));     // unkeyed (receiving), the case #1149 measured
        keyed = true;
        Assert.Equal(0, Measure(101));     // keyed
        c.BeginDrain(1440);                // a held unkey draining (made on the control side)
        Assert.Equal(0, Measure(101));
        Assert.True(c.Drain!.TailOut);     // positive control: the drain really ran through these frames
        keyed = false;
        Assert.Equal(0, Measure(101));     // and back to receive
    }

    [Fact]
    public void Both_release_rows_move_off_the_slice_through_the_one_plan_before_releasing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "NativeMenuBar.cs"));
        foreach (string row in new[] { "\"Release Slice\"", "\"Release Active Slice\"" })
        {
            int at = src.IndexOf("AddWired(", src.IndexOf(row, System.StringComparison.Ordinal) - 40, System.StringComparison.Ordinal);
            int remove = src.IndexOf("rig.RemoveSlice(toRemove)", at, System.StringComparison.Ordinal);
            Assert.True(at >= 0 && remove > at, row + " not found");
            string body = src.Substring(at, remove - at);
            Assert.Contains("MoveOffBeforeRelease(rig, toRemove);", body);
            // Nothing else in the row writes the active or transmit slice.
            Assert.DoesNotMatch(new Regex(@"rig\.(RXVFO|TXVFO)\s*="), body);
        }
    }
}
