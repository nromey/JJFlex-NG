using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 39 Track E. The chain names itself, so a shared analyzer stops
    /// telling receive operators about their transmit path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ChainAnalyzer"/> is walked by both directions and hardcoded
    /// the word "transmit" in three sentences it writes for itself: a stage
    /// that is not in the path, a walk that could check nothing, and a walk
    /// that checked everything. The receive walk went through all three.
    /// </para>
    /// <para>
    /// <b>It did not fire, and only by accident.</b> Stage 4 of the receive
    /// rules is gated rule by rule rather than with a stage-level "needs:", so
    /// the PC-audio-off case comes back as "nothing to check" instead of "not
    /// in path". One "needs:" line — the natural way to say the same thing,
    /// and the way the next receive stage will be written — would have put
    /// "not part of your transmit path" in front of an operator asking why
    /// their radio is silent. These tests are what stops that line from being
    /// dangerous to write.
    /// </para>
    /// <para>
    /// The receive scenarios build their own ruleset rather than editing the
    /// shipped one, so they lock the behaviour independently of which stages
    /// the receive file happens to gate today.
    /// </para>
    /// </remarks>
    public sealed class ChainNameTests
    {
        /// <summary>
        /// A two-stage walk whose second stage is off this operator's path,
        /// which is the only way to reach the "not in path" sentence.
        /// </summary>
        private static DiagnosticRuleSet Walk(string chainLine)
        {
            return DiagnosticRuleSet.Parse(
                "ruleset: A walk\n"
                + chainLine
                + "stage: 1 the first step\n"
                + "stage: 2 the second step\n"
                + "needs: on-this-path is yes\n"
                + "rule: r\n"
                + "in-stage: 1\n"
                + "broken-when: something is yes\n"
                + "verdict: v\n");
        }

        private static DiagnosticFacts OffPath()
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("on-this-path", "This step is in use", false));
            f.Add(DiagnosticFact.Flag("something", "Something", false));
            return f;
        }

        // ── The two shipped walks ─────────────────────────────────────────

        [Fact]
        public void The_shipped_transmit_rules_are_called_the_transmit_chain()
        {
            RuleSetLoader.Forget();
            Assert.Equal("transmit", RuleSetLoader.TxChain().ChainName);
        }

        [Fact]
        public void The_shipped_receive_rules_are_called_the_receive_chain()
        {
            // The whole point. Everything else here is machinery for this line.
            RuleSetLoader.Forget();
            Assert.Equal("receive", RuleSetLoader.RxChain().ChainName);
        }

        [Fact]
        public void The_transmit_walk_still_says_transmit_in_the_operators_sentences()
        {
            // The fix would be worthless if it made the transmit report vaguer.
            RuleSetLoader.Forget();
            ChainReport r = ChainAnalyzer.Run(RuleSetLoader.TxChain(), OffPath());

            Assert.Equal("transmit", r.ChainName);
            Assert.Contains("transmit", r.Headline());
        }

        // ── The sentence that was wrong ───────────────────────────────────

        [Fact]
        public void A_stage_off_a_receive_walk_is_not_part_of_your_receive_path()
        {
            ChainReport r = ChainAnalyzer.Run(Walk("chain: receive\n"), OffPath());
            string line = r.Stages[1].Line();

            Assert.Equal(StageVerdict.NotInPath, r.Stages[1].Verdict);
            Assert.Contains("not part of your receive path", line);
            Assert.DoesNotContain("transmit", line);
        }

        [Fact]
        public void The_census_of_a_receive_walk_counts_stages_off_your_receive_path()
        {
            ChainReport r = ChainAnalyzer.Run(Walk("chain: receive\n"), OffPath());

            Assert.Contains("not in your receive path", r.Census());
            Assert.DoesNotContain("transmit", r.Census());
        }

        [Fact]
        public void A_receive_walk_with_nothing_wrong_says_so_about_the_receive_chain()
        {
            ChainReport r = ChainAnalyzer.Run(Walk("chain: receive\n"), OffPath());

            Assert.Contains("your receive chain", r.Headline());
            Assert.DoesNotContain("transmit", r.Headline());
        }

        [Fact]
        public void A_receive_walk_that_lost_its_rules_says_so_about_the_receive_chain()
        {
            // The branch a missing embedded resource or an empty override
            // reaches, and the one sentence an operator gets when the check
            // could not run at all. It has to be about the walk they asked for.
            var empty = new DiagnosticRuleSet { ChainName = "receive" };

            ChainReport r = ChainAnalyzer.Run(empty, new DiagnosticFacts());

            Assert.Contains("Nothing was checked", r.Headline());
            Assert.Contains("your receive chain", r.Headline());
            Assert.DoesNotContain("transmit", r.Headline());
        }

        [Fact]
        public void No_sentence_a_receive_report_writes_for_itself_mentions_transmitting()
        {
            // The whole rendered report, not one method. An absence is what is
            // being asserted, so the positive control below matters as much as
            // this: the same walk named "transmit" must fail this assertion.
            ChainReport r = ChainAnalyzer.Run(Walk("chain: receive\n"), OffPath());

            Assert.DoesNotContain("transmit", r.ToText());
            Assert.DoesNotContain("transmit", r.EvidenceText());
        }

        [Fact]
        public void Positive_control_the_same_walk_named_transmit_does_say_transmit()
        {
            // Without this, the test above would pass just as happily if the
            // report had stopped naming the chain at all.
            ChainReport r = ChainAnalyzer.Run(Walk("chain: transmit\n"), OffPath());

            Assert.Contains("not part of your transmit path", r.ToText());
            Assert.Contains("not in your transmit path", r.Census());
        }

        [Fact]
        public void One_stage_off_the_path_is_reported_in_the_singular()
        {
            // Found while rewriting the sentence: the census counted stages off
            // the path and then said "1 are not in your transmit path". Every
            // clause beside it already agreed with its number. It reads as a
            // stumble aloud, and the receive walk is small enough that one is
            // the commonest answer it will ever give.
            ChainReport one = ChainAnalyzer.Run(Walk("chain: receive\n"), OffPath());

            Assert.Equal(1, one.StagesNotInPath);
            Assert.Contains("1 is not in your receive path", one.Census());
        }

        [Fact]
        public void Two_stages_off_the_path_are_reported_in_the_plural()
        {
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(
                "chain: receive\n"
                + "stage: 1 the first step\nneeds: on-this-path is yes\n"
                + "stage: 2 the second step\nneeds: on-this-path is yes\n");

            ChainReport two = ChainAnalyzer.Run(rules, OffPath());

            Assert.Equal(2, two.StagesNotInPath);
            Assert.Contains("2 are not in your receive path", two.Census());
        }

        // ── Where the name comes from ─────────────────────────────────────

        [Fact]
        public void A_file_that_names_itself_beats_the_name_the_loader_supplies()
        {
            // The escape hatch for a walk that is neither of the two we ship,
            // and for an operator's own file that walks something else.
            DiagnosticRuleSet set = DiagnosticRuleSet.Parse(
                "chain: amplifier\nstage: 1 a step\n", "test", "transmit");

            Assert.Equal("amplifier", set.ChainName);
        }

        [Fact]
        public void A_file_that_does_not_name_itself_takes_the_name_it_was_loaded_as()
        {
            // An override written before the chain key existed still replaces
            // the receive rules ENTIRELY, and must still call itself receive.
            DiagnosticRuleSet set = DiagnosticRuleSet.Parse("stage: 1 a step\n", "test", "receive");

            Assert.Equal("receive", set.ChainName);
        }

        [Fact]
        public void A_ruleset_nobody_names_claims_neither_direction()
        {
            DiagnosticRuleSet set = DiagnosticRuleSet.Parse("stage: 1 a step\n");

            Assert.Equal(DiagnosticRuleSet.DefaultChainName, set.ChainName);
            Assert.DoesNotContain("transmit", set.ChainName);
            Assert.DoesNotContain("receive", set.ChainName);

            // And it still reads as a sentence rather than as a hole.
            ChainReport r = ChainAnalyzer.Run(Walk(""), OffPath());
            Assert.Contains("not part of your signal path", r.Stages[1].Line());
        }

        [Fact]
        public void An_empty_chain_line_is_refused_rather_than_leaving_a_hole_in_the_sentence()
        {
            // "not part of your  path" is worse than a wrong word: it reads as
            // a stumble aloud and tells the operator nothing at all.
            DiagnosticRuleSet set = DiagnosticRuleSet.Parse("chain:\nstage: 1 a step\n", "test", "receive");

            Assert.Equal("receive", set.ChainName);
            Assert.Contains(set.Problems, p => p.Contains("chain wants"));
        }

        [Fact]
        public void A_stage_carries_its_chains_name_down_from_the_ruleset()
        {
            // The stage sentence is written by StageResult, which has no
            // ruleset — so the name has to travel with it, or that one line
            // would need its own copy of the vocabulary.
            ChainReport r = ChainAnalyzer.Run(Walk("chain: receive\n"), OffPath());

            foreach (StageResult s in r.Stages) Assert.Equal("receive", s.ChainName);
        }
    }
}
