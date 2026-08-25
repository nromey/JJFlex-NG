using System;
using System.Linq;
using Radios.ChainChecks;
using Xunit;
using static Radios.ChainChecks.TxToneLadder;

namespace Radios.Tests
{
    /// <summary>
    /// The ladder is built from the operator's MEASURED transmit passband, not
    /// from a remembered typical one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property under test is not "are there six tones." It is that
    /// <b>the control rungs are outside the passband and the measurement rungs
    /// are inside it, for every filter a radio can be set to</b> — because the
    /// controls are the whole basis for trusting the result, and a control that
    /// silently lands in-band turns the ladder into a confident wrong answer.
    /// </para>
    /// <para>
    /// The ladder previously hardcoded 200/300/700/1500/2400/3200 and stated
    /// "a standard SSB transmit filter runs roughly 300 Hz to 2.7 kHz" in its
    /// own documentation as though that were a fact. It is a setting, the radio
    /// reports it, and nobody asked. On a wide filter both controls fell inside
    /// the passband and the ladder reported a broken filter on a healthy radio
    /// (#221).
    /// </para>
    /// </remarks>
    public class TxToneLadderDerivationTests
    {
        // Filters a Flex can actually be set to, plus the ones that broke the
        // old ladder. TXFilterLow/High are documented as 0 to 10000 Hz.
        public static TheoryData<int, int, string> RealFilters => new TheoryData<int, int, string>
        {
            { 300, 2700, "the classic SSB filter the old ladder assumed" },
            { 100, 3500, "wide voice or DIGU — both old controls fell INSIDE this" },
            { 400, 2400, "narrow contest/DX — old in-band rungs fell OUTSIDE this" },
            { 200, 2800, "another common default" },
            { 100, 6000, "very wide, e.g. deliberately open for data" },
            { 500, 2000, "very narrow" },
        };

        [Theory]
        [MemberData(nameof(RealFilters))]
        public void Controls_are_outside_the_passband_for_every_real_filter(
            int low, int high, string why)
        {
            Rung[] rungs = DeriveRungs(Passband.Read(low, high));

            foreach (Rung r in rungs.Where(r => r.Placement == Placement.BelowPassband))
                Assert.True(r.Hz < low,
                    why + ": control at " + r.Hz + " Hz is not below " + low);

            foreach (Rung r in rungs.Where(r => r.Placement == Placement.AbovePassband))
                Assert.True(r.Hz > high,
                    why + ": control at " + r.Hz + " Hz is not above " + high);
        }

        [Theory]
        [MemberData(nameof(RealFilters))]
        public void In_passband_rungs_are_inside_the_passband_for_every_real_filter(
            int low, int high, string why)
        {
            Rung[] rungs = DeriveRungs(Passband.Read(low, high));

            Rung[] inBand = rungs.Where(r => r.Placement == Placement.InPassband).ToArray();
            Assert.NotEmpty(inBand);

            foreach (Rung r in inBand)
            {
                Assert.True(r.Hz > low, why + ": rung at " + r.Hz + " is not above " + low);
                Assert.True(r.Hz < high, why + ": rung at " + r.Hz + " is not below " + high);
            }
        }

        [Theory]
        [MemberData(nameof(RealFilters))]
        public void No_rung_sits_exactly_on_an_edge(int low, int high, string why)
        {
            // A rung on a corner is neither in nor out — it reads as whichever
            // the filter's skirt happens to make it, which is the one answer
            // that cannot be interpreted.
            foreach (Rung r in DeriveRungs(Passband.Read(low, high)))
            {
                Assert.NotEqual(low, r.Hz);
                Assert.NotEqual(high, r.Hz);
            }
        }

        [Fact]
        public void A_wide_filter_and_a_narrow_one_produce_different_ladders()
        {
            // The regression that matters. If these come out identical the
            // derivation is not reading the passband at all, and every test
            // above would still pass on a hardcoded list that happened to fit.
            int[] wide = DeriveRungs(Passband.Read(100, 3500)).Select(r => r.Hz).ToArray();
            int[] narrow = DeriveRungs(Passband.Read(400, 2400)).Select(r => r.Hz).ToArray();

            Assert.NotEqual(wide, narrow);
        }

        [Fact]
        public void The_old_hardcoded_controls_would_have_failed_on_a_wide_filter()
        {
            // Documents the bug this replaced, as an executable fact rather
            // than a comment. 200 and 3200 were the old controls; inside a
            // 100-3500 passband neither is a control at all.
            const int low = 100, high = 3500;
            Assert.True(200 > low, "the old low control sits INSIDE a wide passband");
            Assert.True(3200 < high, "the old high control sits INSIDE a wide passband");

            // And the derived ones do not.
            Rung[] rungs = DeriveRungs(Passband.Read(low, high));
            Assert.Contains(rungs, r => r.Placement == Placement.BelowPassband && r.Hz < low);
            Assert.Contains(rungs, r => r.Placement == Placement.AbovePassband && r.Hz > high);
        }

        [Fact]
        public void An_unread_passband_produces_no_ladder_rather_than_a_default_one()
        {
            // The silent-wrong-answer path. Falling back to a remembered ladder
            // when the real cuts cannot be read is exactly how the original bug
            // would come back, so absence must produce nothing to run.
            Assert.Empty(DeriveRungs(Passband.Unknown));
        }

        [Theory]
        [InlineData(2700, 300)]   // inverted
        [InlineData(1000, 1000)]  // zero width
        public void A_nonsensical_passband_produces_no_ladder(int low, int high)
        {
            Assert.Empty(DeriveRungs(Passband.Read(low, high)));
        }

        [Fact]
        public void Tones_stay_within_what_the_chain_can_carry()
        {
            // A filter opened to the documented extremes must not push a
            // control rung to something the transmit chain cannot produce.
            foreach (Rung r in DeriveRungs(Passband.Read(0, 10000)))
            {
                Assert.True(r.Hz >= MinToneHz, r.Hz + " is below the usable floor");
                Assert.True(r.Hz <= MaxToneHz, r.Hz + " is above the usable ceiling");
            }
        }

        [Fact]
        public void Rungs_are_ordered_low_to_high()
        {
            // The operator hears these in sequence and is told what to expect.
            // Out of order, the narration and the sound disagree.
            int[] hz = DeriveRungs(Passband.Read(300, 2700)).Select(r => r.Hz).ToArray();
            Assert.Equal(hz.OrderBy(x => x).ToArray(), hz);
        }

        [Fact]
        public void Every_rung_explains_itself_and_names_the_real_edge()
        {
            // The purpose text is read to the operator, and a control that says
            // "below your transmit filter" without saying where that edge is
            // cannot be checked by the person hearing it.
            Rung[] rungs = DeriveRungs(Passband.Read(400, 2400));

            foreach (Rung r in rungs)
                Assert.False(string.IsNullOrWhiteSpace(r.Purpose));

            Assert.Contains(rungs, r => r.Placement == Placement.BelowPassband
                                     && r.Purpose.Contains("400"));
            Assert.Contains(rungs, r => r.Placement == Placement.AbovePassband
                                     && r.Purpose.Contains("2400"));
        }

        [Fact]
        public void Airtime_is_computed_from_the_actual_ladder()
        {
            // The operator is told how long their radio will transmit before it
            // starts. A wide filter and a narrow one can yield different rung
            // counts, so a fixed figure would be a promise we might not keep.
            Rung[] rungs = DeriveRungs(Passband.Read(300, 2700));
            Assert.Equal((rungs.Length + 1) * RungMs, TotalMsFor(rungs));
            Assert.Equal(RungMs, TotalMsFor(Array.Empty<Rung>()));
        }

        [Fact]
        public void The_passband_records_whether_it_was_read()
        {
            // A ladder result without the passband it was measured against
            // cannot be re-read later or compared between two operators (#188).
            Assert.True(Passband.Read(300, 2700).Known);
            Assert.False(Passband.Unknown.Known);
            Assert.Equal("300 to 2700 Hz", Passband.Read(300, 2700).ToString());
            Assert.Equal("not read", Passband.Unknown.ToString());
        }
    }
}
