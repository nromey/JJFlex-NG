using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The rules file that actually ships, parsed as the app parses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The loader does not throw on a bad rule — it records a problem and
    /// carries on.</b> That is the right behaviour for an operator whose own
    /// copy has a typo in it: losing one rule beats losing the whole tool. It
    /// is the wrong behaviour for us, because a rule that silently failed to
    /// parse leaves a green test suite and a check that never fires.
    /// </para>
    /// <para>
    /// So this asserts the shipped file parses with ZERO problems, and that
    /// named rules are actually present rather than merely written down.
    /// </para>
    /// </remarks>
    public class ShippedRuleFileTests
    {
        private static DiagnosticRuleSet Shipped() => RuleSetLoader.TxChain();

        [Fact]
        public void The_shipped_rules_parse_with_no_problems()
        {
            DiagnosticRuleSet set = Shipped();
            Assert.True(set.Problems.Count == 0,
                        "the shipped rule file did not parse cleanly:\r\n"
                        + string.Join("\r\n", set.Problems));
        }

        [Fact]
        public void Every_stage_of_the_transmit_walk_is_present()
        {
            // Thirteen, numbered 0 to 12, from the connection to RF out. A
            // stage vanishing is how a whole class of fault stops being
            // checked without anything failing.
            DiagnosticRuleSet set = Shipped();
            for (int n = 0; n <= 12; n++)
                Assert.True(set.Stages.Any(s => s.Number == n),
                            "stage " + n + " is missing from the transmit walk");
        }

        [Fact]
        public void The_radios_answer_to_the_audio_stream_is_checked_not_assumed()
        {
            // STAGE 7, and the reason this test exists by name.
            //
            // It was marked not-observable, with the note that the radio's
            // answer was "held privately inside the app and is not published
            // anywhere a check can read it". That stopped being true the day
            // Sprint 33 Track G recorded the answer — which went to a trace
            // file and nowhere else, so the stage went on reporting it could
            // not look at the single most likely cause of silent transmit.
            //
            // We encode Opus unconditionally. A radio that opened the stream
            // as anything else reads every packet as raw PCM: silent transmit,
            // no error, every setting on both sides looking correct. It cost
            // two days once.
            DiagnosticRuleSet set = Shipped();

            DiagnosticStage stage = set.Stages.FirstOrDefault(s => s.Number == 7);
            Assert.NotNull(stage);
            Assert.True(string.IsNullOrEmpty(stage.NotObservable),
                        "stage 7 is marked not-observable again: " + stage.NotObservable);

            Assert.Contains(set.Rules, r => r.Id == "tx-stream-not-opus");
            Assert.Contains(set.Rules, r => r.Id == "tx-stream-no-compression-key");
        }

        [Fact]
        public void Every_rule_names_a_stage_that_exists()
        {
            // A rule pointing at a stage number nobody declared never fires,
            // and nothing says so.
            DiagnosticRuleSet set = Shipped();
            foreach (DiagnosticRule rule in set.Rules)
                Assert.True(set.Stages.Any(s => s.Number == rule.StageNumber),
                            "rule \"" + rule.Id + "\" is in-stage " + rule.StageNumber
                            + ", which no stage declares");
        }

        [Fact]
        public void Every_rule_has_something_to_say_when_it_fires()
        {
            // A rule with no verdict fires into silence, which reads to the
            // operator as "nothing was wrong".
            DiagnosticRuleSet set = Shipped();
            foreach (DiagnosticRule rule in set.Rules)
                Assert.False(string.IsNullOrWhiteSpace(rule.Verdict),
                             "rule \"" + rule.Id + "\" has no verdict");
        }
    }
}
