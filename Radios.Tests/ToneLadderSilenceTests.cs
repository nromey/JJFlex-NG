using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using Ladder = Radios.ChainChecks.TxToneLadder;

namespace Radios.Tests
{
    /// <summary>
    /// A LADDER NOTHING CAME BACK FROM SAYS SO ONCE (#443).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel, running stage 3 on Don's radio, 31 August 2026: <i>"the reference
    /// voice says that it's a reference and didn't transmit and starts reading
    /// hertz which is weird."</i> The report announced that no rung rose above
    /// the arrival line, then read out five rungs that were every one of them at
    /// the meter floor and identical to each other, then said it could not be
    /// read. One fact, three times, two of them made of numbers that carry none
    /// — the same shape as #437.
    /// </para>
    /// <para>
    /// <b>The dropped verdict sentence matters more than the dropped rungs.</b>
    /// It said <i>"run it again with the radio fully up, so its meters are
    /// reporting"</i>, and the meters WERE reporting: −150 dBFS is a reading.
    /// Sending an operator to re-key a transmitter on a false diagnosis costs
    /// them RF.
    /// </para>
    /// </remarks>
    public sealed class ToneLadderSilenceTests
    {
        private const double Ref = -10.0;
        private static readonly Ladder.Passband Band = Ladder.Passband.Read(300, 2700);
        private static Ladder.Rung[] Rungs => Ladder.DeriveRungs(Band);
        private static int Hz(int i) => Rungs[i].Hz;

        private static Ladder.RungReading Read(int hz, double db, bool reported = true)
            => new Ladder.RungReading(Rungs.First(r => r.Hz == hz), db, reported);

        /// <summary>Don's run: every rung reported, every one on the floor.</summary>
        private static List<Ladder.RungReading> AllAtTheFloor() =>
            Enumerable.Range(0, Rungs.Length).Select(i => Read(Hz(i), -150)).ToList();

        /// <summary>The same ladder with real audio in it, as a positive control.</summary>
        private static List<Ladder.RungReading> Healthy() => new()
        {
            Read(Hz(0), Ref - 20),
            Read(Hz(1), Ref - 1),
            Read(Hz(2), Ref),
            Read(Hz(3), Ref - 0.5),
            Read(Hz(4), Ref - 2),
            Read(Hz(5), Ref - 25),
        };

        [Fact]
        public void A_ladder_that_did_arrive_still_reads_out_every_rung()
        {
            // THE POSITIVE CONTROL. Suppression that fired on a working ladder
            // would delete the only measurement this stage exists to take, and
            // the test below would pass just as happily.
            string d = Ladder.Describe(Ref, Healthy());

            foreach (Ladder.Rung r in Rungs)
                Assert.Contains(r.Hz.ToString(CultureInfo.InvariantCulture) + " hertz: ", d);
        }

        [Fact]
        public void A_ladder_nothing_came_back_from_does_not_read_out_the_floor()
        {
            string d = Ladder.Describe(-150, AllAtTheFloor());

            Assert.DoesNotContain("against the measuring tone", d);
            Assert.DoesNotContain("inside your transmit filter", d);
            foreach (Ladder.Rung r in Rungs)
                Assert.DoesNotContain(r.Hz.ToString(CultureInfo.InvariantCulture) + " hertz: ", d);
        }

        [Fact]
        public void It_says_once_that_nothing_arrived_and_why_the_rungs_are_missing()
        {
            string d = Ladder.Describe(-150, AllAtTheFloor());

            Assert.Contains("Nothing came back from any rung", d);
            Assert.Contains("not listed one by one", d);
        }

        [Fact]
        public void It_does_not_blame_meters_that_were_reporting()
        {
            // The old trailing sentence. −150 is a reading, so "run it again
            // with the radio fully up, so its meters are reporting" was a
            // diagnosis the evidence beside it refuted.
            string d = Ladder.Describe(-150, AllAtTheFloor());

            Assert.DoesNotContain("meters are reporting", d);
            Assert.DoesNotContain("Not enough of the ladder came back", d);
        }

        [Fact]
        public void A_ladder_with_no_rungs_at_all_is_left_to_the_ordinary_wording()
        {
            // No rungs is a different thing from rungs that read nothing, and
            // "all 0 read at or below" would be nonsense.
            string d = Ladder.Describe(Ref, new List<Ladder.RungReading>());

            Assert.DoesNotContain("Nothing came back from any rung", d);
        }

        [Fact]
        public void The_word_reference_no_longer_names_the_measuring_tone()
        {
            // The other half of #443: "reference" meant the 1000 hertz yardstick
            // AND the shipped reference recording, a few lines apart in one
            // report. The recording keeps the name — it is a shipped file, a
            // picker label and a spoken script — and the tone gives it up.
            string d = Ladder.Describe(Ref, Healthy());

            Assert.Contains("measuring tone", d);
            Assert.DoesNotContain("Reference tone", d);
        }
    }
}
