#nullable enable
using System;
using System.Collections.Generic;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1335: zero beat's early exit. One frequency in half the samples
    /// settles it at once; until the fix the test read the newly made tally
    /// (always 1), so it never fired and every zero beat took all ten samples.
    /// Windowless and radio-less: the decision is fed a sample sequence.
    /// </summary>
    public sealed class ZeroBeatPickTests
    {
        private static (ulong Value, int Taken, int Pauses) Pick(params ulong[] samples)
        {
            int next = 0, pauses = 0;
            ulong value = PanAdapterManager.ZeroBeatPick(() => samples[next++], () => pauses++, out int taken);
            return (value, taken, pauses);
        }

        [Fact]
        public void One_frequency_in_half_the_samples_settles_it_at_once()
        {
            var r = Pick(7_074_500, 7_074_500, 7_074_500, 7_074_500, 7_074_500,
                         1, 1, 1, 1, 1);
            Assert.Equal(7_074_500UL, r.Value);
            Assert.Equal(5, r.Taken);          // the mutation: 10, the early exit dead
            Assert.Equal(4, r.Pauses);         // no wait after the settling sample
        }

        [Fact]
        public void With_no_frequency_in_half_the_samples_the_most_seen_wins_after_all_ten()
        {
            var r = Pick(10, 20, 20, 30, 20, 30, 40, 20, 50, 60);
            Assert.Equal(20UL, r.Value);
            Assert.Equal(10, r.Taken);
            Assert.Equal(10, r.Pauses);
        }

        [Fact]
        public void A_tie_in_the_most_seen_pass_goes_to_the_first_seen()
        {
            var r = Pick(10, 20, 30, 40, 50, 60, 70, 80, 90, 100);
            Assert.Equal(10UL, r.Value);
            Assert.Equal(10, r.Taken);
        }

        [Fact]
        public void In_a_five_to_five_tie_the_first_to_reach_five_now_wins()
        {
            // a b b a b a b a b a: b reaches five at the ninth sample. Before the
            // fix all ten were taken and the most-seen pass gave the first SEEN
            // of the tied pair, a. This is the one case where the answer moves.
            const ulong a = 14_200_000, b = 14_200_050;
            var r = Pick(a, b, b, a, b, a, b, a, b, a);
            Assert.Equal(b, r.Value);
            Assert.Equal(9, r.Taken);
        }

        [Fact]
        public void No_zero_beat_settled_still_falls_to_the_most_seen_pass()
        {
            var r = Pick(0, 0, 0, 0, 0, 3, 3, 3, 3, 3);
            Assert.Equal(0UL, r.Value);
            Assert.Equal(5, r.Taken);
        }
    }
}
