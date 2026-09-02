using System;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The rule for choosing among identical copies of a transmit-chain meter
    /// (#502), against the two meter layouts that have actually been observed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These inventories are constructed, not measured live, and that is the
    /// only way the multi-copy case can be tested from here: the bench 8600
    /// on its own publishes ONE copy, and the radio that publishes three is
    /// Don's 6300 over SmartLink. The labels and indices are the ones the
    /// traces recorded — <c>[17] TX-:8</c>, <c>[21] TX-:8</c>, <c>[43] TX-:9</c>
    /// on 2026-09-01, and indices 24, 48, 72, 91 all <c>TX-:0</c> on
    /// 2026-08-20.
    /// </para>
    /// <para>
    /// The first test is the regression: a copy that never reports must never
    /// be the one believed, whatever its position in the list. Everything
    /// after it is the rule's shape — signal beats liveness, liveness beats
    /// nothing, and a packet's head start after key-down beats neither.
    /// </para>
    /// </remarks>
    public class TransmitMeterElectionTests
    {
        private const float Floor = -150f;
        private const float DonSpeaking = -92.59f;   // audible on the air, 2026-08-31 Fixer run
        private const float BenchKeying = -10.8f;    // the 2026-08-20 real keying on the 8600
        private const int Quiet = TransmitMeterElection.QuietMs;

        /// <summary>Don's 6300, as the meter inventory trace recorded it.</summary>
        private static TransmitMeterElection DonsRadio()
        {
            var e = new TransmitMeterElection("SC_MIC");
            e.Register("m17", "[17] TX-:8", 17);
            e.Register("m21", "[21] TX-:8", 21);
            e.Register("m43", "[43] TX-:9", 43);
            return e;
        }

        /// <summary>The bench 8600 with a station client connected and four slices open.</summary>
        private static TransmitMeterElection BenchRadio()
        {
            var e = new TransmitMeterElection("SC_MIC");
            foreach (int i in new[] { 24, 48, 72, 91 })
                e.Register("m" + i, "[" + i + "] TX-:0", i);
            return e;
        }

        // ---- the regression ----

        [Fact]
        public void The_first_listed_copy_that_never_reports_is_never_believed()
        {
            // #502 exactly. FlexLib's FindMeterByName handed back [17], which
            // never delivered a sample; [43] carried his voice. The old code
            // read [17]'s floor as "no transmit audio" for a whole evening.
            var e = DonsRadio();

            Assert.Equal(TransmitMeterElection.Outcome.Elected, e.Report("m43", DonSpeaking, nowTick: 0));

            Assert.NotNull(e.Elected);
            Assert.Equal("[43] TX-:9", e.Elected!.Label);
            Assert.Equal(DonSpeaking, e.ElectedLast);
            Assert.Equal(DonSpeaking, e.ElectedPeakSinceReset);
            Assert.True(e.ElectedReportedSinceReset);

            string text = e.Describe(0);
            Assert.Contains("Elected [43] TX-:9", text);
            Assert.Contains("[17] TX-:8 never reported", text);
        }

        [Fact]
        public void Nothing_is_believed_until_a_copy_reports()
        {
            // The other half of #502 and the whole of #459's telemetry rule: a
            // floor from here, before any copy has spoken, is not a reading.
            var e = DonsRadio();

            Assert.False(e.HasElected);
            Assert.Null(e.Elected);
            Assert.False(e.ElectedReportedSinceReset);
            Assert.True(float.IsNaN(e.ElectedLast));
            Assert.True(float.IsNaN(e.ElectedPeakSinceReset));

            string text = e.Describe(0);
            Assert.Contains("3 copies", text);
            Assert.Contains("nothing elected", text);
            Assert.Contains("fabrication", text);
        }

        // ---- the single-copy radio: behaviour unchanged ----

        [Fact]
        public void A_single_copy_is_simply_the_meter()
        {
            var e = new TransmitMeterElection("SC_MIC");
            e.Register("only", "[24] TX-:0", 24);

            Assert.Equal(TransmitMeterElection.Outcome.Elected, e.Report("only", Floor, 0));
            Assert.Equal(TransmitMeterElection.Outcome.Accepted, e.Report("only", BenchKeying, 50));

            Assert.Equal(BenchKeying, e.ElectedLast);
            Assert.Equal(BenchKeying, e.ElectedPeakSinceReset);
            Assert.Contains("the only copy", e.LastElectionReason);
        }

        // ---- the rule's shape ----

        [Fact]
        public void A_floor_streaming_copy_yields_to_the_copy_carrying_audio()
        {
            // The 8600 layout: whichever copy reported first is provisional.
            // Once keyed, the copy that actually rises takes over — and the
            // reason names both readings so a trace reader can see why.
            var e = BenchRadio();
            Assert.Equal(TransmitMeterElection.Outcome.Elected, e.Report("m24", Floor, 0));

            e.ResetPeaks();                                                    // key-down
            Assert.Equal(TransmitMeterElection.Outcome.Accepted, e.Report("m24", Floor, 1010));
            Assert.Equal(TransmitMeterElection.Outcome.Displaced, e.Report("m72", BenchKeying, 1020));

            Assert.Equal("[72] TX-:0", e.Elected!.Label);
            Assert.Equal(BenchKeying, e.ElectedPeakSinceReset);
            Assert.Contains("[24] TX-:0", e.LastElectionReason);
            Assert.Contains("-150.0", e.LastElectionReason);
        }

        [Fact]
        public void Arrival_order_after_key_down_does_not_flip_the_election()
        {
            // Right after a reset every copy's peak is unknown, so without this
            // rule the first packet through would win by arrival order on every
            // key-down — an implicit ordering, which is the defect in another coat.
            var e = BenchRadio();
            e.Report("m24", Floor, 0);
            e.ResetPeaks();

            Assert.Equal(TransmitMeterElection.Outcome.Ignored, e.Report("m72", Floor, 20));
            Assert.Equal(TransmitMeterElection.Outcome.Accepted, e.Report("m24", Floor, 30));
            Assert.Equal("[24] TX-:0", e.Elected!.Label);
        }

        [Fact]
        public void A_copy_that_stopped_streaming_is_displaced_by_one_that_is()
        {
            var e = BenchRadio();
            e.Report("m24", Floor, 0);
            e.ResetPeaks();

            Assert.Equal(TransmitMeterElection.Outcome.Displaced, e.Report("m72", Floor, Quiet + 1));
            Assert.Equal("[72] TX-:0", e.Elected!.Label);
            Assert.Contains("quiet", e.LastElectionReason);
        }

        [Fact]
        public void Signal_outranks_liveness()
        {
            // A copy that carried real audio and then went quiet is not handed
            // over to a copy streaming the floor: that would turn a proven
            // reading into a fabricated silence.
            var e = BenchRadio();
            e.Report("m24", BenchKeying, 0);

            Assert.Equal(TransmitMeterElection.Outcome.Ignored, e.Report("m72", Floor, Quiet * 5));
            Assert.Equal("[24] TX-:0", e.Elected!.Label);
            Assert.Equal(BenchKeying, e.ElectedPeakSinceReset);
        }

        [Fact]
        public void Samples_from_copies_that_are_not_elected_are_not_published()
        {
            var e = DonsRadio();
            e.Report("m43", DonSpeaking, 0);

            Assert.Equal(TransmitMeterElection.Outcome.Ignored, e.Report("m21", Floor, 5));
            Assert.Equal(DonSpeaking, e.ElectedLast);
            Assert.Equal(DonSpeaking, e.ElectedPeakSinceReset);
        }

        // ---- telemetry is per transmission ----

        [Fact]
        public void Telemetry_since_key_down_starts_over_at_each_reset()
        {
            var e = DonsRadio();
            e.Report("m43", DonSpeaking, 0);
            Assert.True(e.ElectedReportedSinceReset);

            e.ResetPeaks();
            Assert.True(e.HasElected, "the election survives a key-down; only the window starts over");
            Assert.False(e.ElectedReportedSinceReset);
            Assert.True(float.IsNaN(e.ElectedPeakSinceReset));
            Assert.Equal(DonSpeaking, e.ElectedLast);

            e.Report("m43", Floor, 10);
            Assert.True(e.ElectedReportedSinceReset);
            Assert.Equal(Floor, e.ElectedPeakSinceReset);
        }

        // ---- housekeeping ----

        [Fact]
        public void An_unregistered_copy_is_reported_as_unknown_not_dropped_silently()
        {
            var e = DonsRadio();
            Assert.Equal(TransmitMeterElection.Outcome.Unknown, e.Report("m99", Floor, 0));
            Assert.False(e.HasElected);
        }

        [Fact]
        public void Withdrawing_the_elected_copy_clears_the_election()
        {
            var e = DonsRadio();
            e.Report("m43", DonSpeaking, 0);

            e.KeepOnly(k => !Equals(k, "m43"));

            Assert.False(e.HasElected);
            Assert.Equal(2, e.CandidateCount);
            Assert.Contains("withdrawn", e.LastElectionReason);
            Assert.Contains("withdrawn", e.Describe(0));
        }

        [Fact]
        public void Registration_is_idempotent()
        {
            var e = DonsRadio();
            e.Register("m43", "[43] TX-:9", 43);
            Assert.Equal(3, e.CandidateCount);
        }

        // ---- what a human reads ----

        [Fact]
        public void The_census_names_every_copy_and_admits_they_are_identical()
        {
            string dons = DonsRadio().Census();
            Assert.Contains("3 copies", dons);
            Assert.Contains("[17] TX-:8", dons);
            Assert.Contains("[21] TX-:8", dons);
            Assert.Contains("[43] TX-:9", dons);
            Assert.Contains("identical", dons);

            var one = new TransmitMeterElection("ALC");
            one.Register("a", "[30] TX-:0", 30);
            Assert.Contains("1 copy", one.Census());
            Assert.DoesNotContain("identical", one.Census());

            Assert.Contains("NOT FOUND", new TransmitMeterElection("ALC").Census());
        }

        [Fact]
        public void Describe_names_the_choice_and_what_every_other_copy_did()
        {
            var e = DonsRadio();
            e.Report("m43", DonSpeaking, 0);
            e.Report("m21", Floor, 10);

            string text = e.Describe(10);
            Assert.Contains("Elected [43] TX-:9", text);
            Assert.Contains("[17] TX-:8 never reported", text);
            Assert.Contains("[21] TX-:8 1 sample since key-down", text);
            Assert.Contains("-92.6", text);
        }

        [Fact]
        public void Candidates_are_listed_in_registration_order()
        {
            Assert.Equal(new[] { 17, 21, 43 }, DonsRadio().Candidates.Select(c => c.Index).ToArray());
        }
    }
}
