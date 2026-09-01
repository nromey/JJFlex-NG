using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Radios.ChainChecks;
using Xunit;
using static Radios.ChainChecks.TxProbeSet;
using Ladder = Radios.ChainChecks.TxToneLadder;

namespace Radios.Tests
{
    /// <summary>
    /// Three probes that fail differently, judged together.
    /// </summary>
    /// <remarks>
    /// The property under test is that DISAGREEMENT survives. Three probes are
    /// only worth more than one repeated three times if a split is reported as
    /// a split — averaging them into a single confident answer throws away the
    /// entire reason for running three.
    /// </remarks>
    public class TxProbeSetTests
    {
        private static ProbeResult R(Probe p, Outcome o, string detail = "d")
            => new ProbeResult(p, o, detail);

        private static List<ProbeResult> Set(params ProbeResult[] r) => r.ToList();

        [Fact]
        public void Three_agreeing_probes_agree()
        {
            Assert.Equal(Agreement.AllReached, Judge(Set(
                R(Probe.SingleTone, Outcome.ReachedRadio),
                R(Probe.ToneLadder, Outcome.ReachedRadio),
                R(Probe.Voice, Outcome.ReachedRadio))));

            Assert.Equal(Agreement.NoneReached, Judge(Set(
                R(Probe.SingleTone, Outcome.DidNotReach),
                R(Probe.ToneLadder, Outcome.DidNotReach),
                R(Probe.Voice, Outcome.DidNotReach))));
        }

        [Fact]
        public void Two_out_of_three_is_a_majority_and_says_which_way()
        {
            Assert.Equal(Agreement.MostlyReached, Judge(Set(
                R(Probe.SingleTone, Outcome.ReachedRadio),
                R(Probe.ToneLadder, Outcome.ReachedRadio),
                R(Probe.Voice, Outcome.DidNotReach))));

            Assert.Equal(Agreement.MostlyFailed, Judge(Set(
                R(Probe.SingleTone, Outcome.DidNotReach),
                R(Probe.ToneLadder, Outcome.DidNotReach),
                R(Probe.Voice, Outcome.ReachedRadio))));
        }

        [Fact]
        public void Two_probes_that_disagree_are_one_all_never_a_majority()
        {
            // THE test. With no voice on the machine there are only two probes,
            // and if they differ there is no vote to win. Reporting a winner
            // here would be inventing confidence out of a tie.
            var s = Set(
                R(Probe.SingleTone, Outcome.ReachedRadio),
                R(Probe.ToneLadder, Outcome.DidNotReach),
                R(Probe.Voice, Outcome.Unavailable, "no text-to-speech voice"));

            Assert.Equal(Agreement.EvenlySplit, Judge(s));
            Assert.Contains("no majority", OperatorSummary(s));
        }

        [Fact]
        public void An_unavailable_probe_is_not_a_failed_probe()
        {
            // A machine with no TTS has not failed a voice test — it has not
            // taken one. Counting Unavailable as DidNotReach would manufacture
            // a failure out of a missing feature.
            var s = Set(
                R(Probe.SingleTone, Outcome.ReachedRadio),
                R(Probe.ToneLadder, Outcome.ReachedRadio),
                R(Probe.Voice, Outcome.Unavailable, "no text-to-speech voice"));

            Assert.Equal(Agreement.AllReached, Judge(s));
            Assert.Contains("could not run on this computer", OperatorSummary(s));
        }

        [Fact]
        public void One_probe_alone_is_not_enough_to_compare()
        {
            Assert.Equal(Agreement.NothingToGoOn, Judge(Set(
                R(Probe.SingleTone, Outcome.ReachedRadio),
                R(Probe.ToneLadder, Outcome.NotAttempted),
                R(Probe.Voice, Outcome.Unavailable))));
        }

        [Fact]
        public void Nothing_at_all_does_not_throw()
        {
            Assert.Equal(Agreement.NothingToGoOn, Judge(null));
            Assert.Equal(Agreement.NothingToGoOn, Judge(Set()));
            Assert.Contains("nothing to compare", OperatorSummary(Set()));
        }

        // ── What a split actually points at ───────────────────────────────

        /// <summary>Tones through, voice dead — the split that names a setting.</summary>
        private static List<ProbeResult> TonesThroughVoiceDead() => Set(
            R(Probe.SingleTone, Outcome.ReachedRadio),
            R(Probe.ToneLadder, Outcome.ReachedRadio),
            R(Probe.Voice, Outcome.DidNotReach));

        [Fact]
        public void With_conditioning_ON_the_chain_is_named_as_the_likely_difference()
        {
            // Confident here because the mechanism is known rather than
            // guessed: tones BYPASS the conditioning chain by design, a voice
            // deliberately does not, and the chain is actually running.
            string e = ExplainSplit(TonesThroughVoiceDead(), conditioningActive: true);

            Assert.Contains("bypass", e);
            Assert.Contains("gate", e);
            Assert.Contains("IS on", e);
            Assert.Contains("again", e);   // a next step, not just a name
        }

        [Fact]
        public void With_conditioning_OFF_it_says_that_is_NOT_the_difference()
        {
            // THE test Noel caught before it ever ran. The gate, RNNoise and
            // spectral subtraction ALL default to off, and Don arrives at this
            // build having never seen it with a settings file that predates
            // every one of them. Sending him to turn off something already off
            // would look like an answer while being none — and would cost him
            // a trip into a tab he has never opened.
            string e = ExplainSplit(TonesThroughVoiceDead(), conditioningActive: false);

            Assert.Contains("NOT the difference", e);
            // Says what to check instead, rather than shrugging.
            Assert.Contains("rendered", e);
            Assert.Contains("level", e);
            // And must not send him to the controls that are already off.
            Assert.DoesNotContain("Turning those off", e);
        }

        [Fact]
        public void With_the_conditioning_state_unknown_it_hedges_both_ways()
        {
            // Not knowing is a third state, not a reason to guess. The caller
            // that cannot determine it gets text that is true either way.
            string e = ExplainSplit(TonesThroughVoiceDead());

            Assert.Contains("if ", e, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("both off", e);
        }

        [Fact]
        public void The_conditioning_state_reaches_the_operator_summary_too()
        {
            // The summary is what an operator actually reads; an explanation
            // that only got it right in the sub-function would still ship the
            // wrong advice.
            var s = TonesThroughVoiceDead();
            Assert.Contains("NOT the difference", OperatorSummary(s, conditioningActive: false));
            Assert.Contains("IS on", OperatorSummary(s, conditioningActive: true));
        }

        [Fact]
        public void Voice_through_and_tones_dead_is_reported_as_odd_not_explained()
        {
            // No known mechanism produces this. Inventing one would be worse
            // than admitting the surprise, and this text is read by somebody
            // deciding what to tell a vendor.
            var s = Set(
                R(Probe.SingleTone, Outcome.DidNotReach),
                R(Probe.ToneLadder, Outcome.DidNotReach),
                R(Probe.Voice, Outcome.ReachedRadio));

            string e = ExplainSplit(s);

            Assert.Contains("reverse of what any known mechanism", e);
            Assert.Contains("rather than acting on it", e);
        }

        [Fact]
        public void Tone_through_and_ladder_dead_points_at_frequency_and_defers()
        {
            var s = Set(
                R(Probe.SingleTone, Outcome.ReachedRadio),
                R(Probe.ToneLadder, Outcome.DidNotReach),
                R(Probe.Voice, Outcome.ReachedRadio));

            string e = ExplainSplit(s);

            Assert.Contains("frequency-dependent", e);
            // Defers to the ladder's own detail rather than concluding from a
            // single bit that the ladder itself can answer properly.
            Assert.Contains("rung-by-rung", e);
        }

        [Fact]
        public void Ladder_through_and_single_tone_dead_is_flagged_as_odd_and_asks_for_a_rerun()
        {
            // The ladder contains tones either side of the single tone's
            // frequency, so the single tone failing while the ladder passes has
            // no frequency explanation. Most likely the two runs caught the
            // chain in different states — which is a reason to run again, not a
            // reason to conclude anything.
            //
            // This case was originally written as the UNRECOGNISED example, and
            // it stopped being unrecognised when the tone-versus-tone check was
            // put ahead of the tones-versus-voice one (2026-08-25). The test was
            // repointed rather than deleted: the pattern is real and now has an
            // answer.
            var s = Set(
                R(Probe.SingleTone, Outcome.DidNotReach),
                R(Probe.ToneLadder, Outcome.ReachedRadio));

            string e = ExplainSplit(s);

            Assert.Contains("odd", e);
            Assert.Contains("Run them again", e);
        }

        [Fact]
        public void A_pattern_matching_no_rule_says_so_instead_of_inventing_a_cause()
        {
            // The fallback, reached when neither tone probe produced a result
            // and so nothing can be compared against the voice. It has to stay
            // even though the recognised rules now cover every ordinary split:
            // ExplainSplit is public, and a decision tree whose last branch
            // invents an explanation is worse than one that admits the gap.
            var s = Set(
                R(Probe.SingleTone, Outcome.Unavailable),
                R(Probe.ToneLadder, Outcome.NotAttempted),
                R(Probe.Voice, Outcome.DidNotReach));

            string e = ExplainSplit(s);

            Assert.Contains("does not match a known cause", e);
            Assert.Contains("report them as they are", e);
        }

        [Fact]
        public void All_failing_says_the_cause_is_common_to_all_of_them()
        {
            // The reasoning that makes three probes worth running: they fail in
            // different ways, so all of them failing localises the cause to
            // what they share.
            string s = OperatorSummary(Set(
                R(Probe.SingleTone, Outcome.DidNotReach),
                R(Probe.ToneLadder, Outcome.DidNotReach),
                R(Probe.Voice, Outcome.DidNotReach)));

            Assert.Contains("fail in different ways", s);
            Assert.Contains("downstream", s);
        }
    }

    /// <summary>
    /// The tone ladder across the speech band.
    /// </summary>
    /// <remarks>
    /// The rungs outside the passband are the controls, not decoration: a
    /// ladder where every rung reads alike is not a flat chain, it is a
    /// suspicious result, and the code has to say so rather than reporting a
    /// tidy in-band answer it cannot stand behind.
    /// </remarks>
    public class TxToneLadderTests
    {
        private const double Ref = -10.0;

        /// <summary>
        /// The filter these fixtures describe. Stated rather than assumed —
        /// the ladder is derived from the operator's real passband now, so a
        /// reading only means something alongside the cuts it was taken under.
        /// </summary>
        private static readonly Ladder.Passband Band = Ladder.Passband.Read(300, 2700);

        private static Ladder.Rung[] Rungs => Ladder.DeriveRungs(Band);

        private static Ladder.RungReading Read(int hz, double db, bool reported = true)
        {
            Ladder.Rung rung = Rungs.First(r => r.Hz == hz);
            return new Ladder.RungReading(rung, db, reported);
        }

        /// <summary>The nth rung's frequency, so fixtures do not hardcode tones
        /// the derivation is free to change.</summary>
        private static int Hz(int index) => Rungs[index].Hz;

        /// <summary>Every in-band rung flat at the reference, both ends well down.</summary>
        private static List<Ladder.RungReading> HealthyFilter() => new()
        {
            Read(Hz(0), Ref - 20),   // below the passband: well down
            Read(Hz(1), Ref - 1),
            Read(Hz(2), Ref),
            Read(Hz(3), Ref - 0.5),
            Read(Hz(4), Ref - 2),
            Read(Hz(5), Ref - 25),   // above the passband: well down
        };

        [Fact]
        public void The_ladder_brackets_the_passband_on_both_sides()
        {
            // Without a rung below AND above, there is no control on the filter
            // and the in-band numbers cannot be trusted to mean anything.
            Assert.Contains(Rungs, r => r.Placement == Ladder.Placement.BelowPassband);
            Assert.Contains(Rungs, r => r.Placement == Ladder.Placement.AbovePassband);
            Assert.True(Rungs.Count(r => r.Placement == Ladder.Placement.InPassband) >= 3,
                "too few in-band rungs to see a shape");
        }

        [Fact]
        public void Every_rung_says_what_it_is_for()
        {
            // The purpose text is read by an operator deciding whether a result
            // matters. A rung with no stated purpose is a number nobody can use.
            Assert.All(Rungs, r => Assert.False(string.IsNullOrWhiteSpace(r.Purpose)));
        }

        [Fact]
        public void Flat_in_band_with_both_ends_down_reads_as_a_working_filter()
        {
            Assert.Equal(Ladder.LadderVerdict.LooksLikeAFilter, Ladder.Read(Ref, HealthyFilter()));
        }

        [Fact]
        public void Ends_that_do_not_attenuate_are_called_out_rather_than_ignored()
        {
            // THE control. If the out-of-band rungs come back at full level, the
            // in-band numbers may be perfectly consistent and still be measuring
            // something other than the transmitted signal. Reporting a tidy
            // in-band result there is the confident wrong answer this project
            // keeps finding.
            var flatEverywhere = new List<Ladder.RungReading>
            {
                Read(Hz(0), Ref), Read(Hz(1), Ref), Read(Hz(2), Ref),
                Read(Hz(3), Ref), Read(Hz(4), Ref), Read(Hz(5), Ref),
            };

            Assert.Equal(Ladder.LadderVerdict.NoFilterSeen, Ladder.Read(Ref, flatEverywhere));

            string d = Ladder.Describe(Ref, flatEverywhere);
            Assert.Contains("should have made them quieter", d);
            Assert.Contains("caution", d);
        }

        [Fact]
        public void Uneven_in_band_rungs_report_shaping()
        {
            var shaped = HealthyFilter();
            shaped[3] = Read(Hz(3), Ref - 12);   // a big dip mid-passband

            Assert.Equal(Ladder.LadderVerdict.ShapedInBand, Ladder.Read(Ref, shaped));
            Assert.Contains("shaping your audio", Ladder.Describe(Ref, shaped));
        }

        [Fact]
        public void Too_few_readings_is_incomplete_not_a_verdict()
        {
            var thin = new List<Ladder.RungReading> { Read(Hz(2), Ref), Read(Hz(3), Ref) };
            Assert.Equal(Ladder.LadderVerdict.Incomplete, Ladder.Read(Ref, thin));
        }

        [Fact]
        public void An_unreported_rung_does_not_count_toward_a_verdict()
        {
            var partial = HealthyFilter();
            partial[0] = Read(Hz(0), 0, reported: false);
            partial[5] = Read(Hz(5), 0, reported: false);

            // Both controls gone, so no verdict, however tidy the middle looks.
            Assert.Equal(Ladder.LadderVerdict.Incomplete, Ladder.Read(Ref, partial));
        }

        [Fact]
        public void The_description_gives_each_rung_relative_to_the_reference()
        {
            // An absolute dBFS number means little; how far it sits from the
            // reference is the reading an operator can act on.
            string d = Ladder.Describe(Ref, HealthyFilter());

            // "measuring tone", not "reference" — the word was doing two jobs
            // a few lines apart in one report (#443).
            Assert.Contains("against the measuring tone", d);
            // The control frequencies are derived from the operator's filter,
            // so name them from the ladder rather than from memory — that is
            // the whole point of the change (#221).
            Assert.Contains(Hz(0).ToString(CultureInfo.InvariantCulture) + " hertz", d);
            Assert.Contains(Hz(5).ToString(CultureInfo.InvariantCulture) + " hertz", d);
        }

        [Fact]
        public void A_full_ladder_is_short_enough_to_hold_a_key_through()
        {
            // Seven steps including the reference. Long enough for the radio's
            // meters to settle on each, short enough that nobody's arm aches
            // and no transmit timeout wakes up.
            int total = Ladder.TotalMsFor(Rungs);
            Assert.True(total <= 15000,
                "a ladder of " + total + " ms is too long to hold a PTT key through");
        }
    }
}
