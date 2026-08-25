using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The receive walk's shipped rules, parsed as the app parses them.
    /// </summary>
    /// <remarks>
    /// Written alongside the file itself, not after. The transmit rules went
    /// unguarded for weeks and a silently-dropped rule leaves a green suite and
    /// a check that never fires — see <see cref="ShippedRuleFileTests"/>.
    /// </remarks>
    public class ReceiveRuleFileTests
    {
        private static DiagnosticRuleSet Shipped() => RuleSetLoader.RxChain();

        [Fact]
        public void The_shipped_receive_rules_parse_with_no_problems()
        {
            DiagnosticRuleSet set = Shipped();
            Assert.True(set.Problems.Count == 0,
                        "the shipped receive rule file did not parse cleanly:\r\n"
                        + string.Join("\r\n", set.Problems));
        }

        [Fact]
        public void The_whole_ladder_that_used_to_be_hardcoded_survived_the_move()
        {
            // Every branch of FlexBase.SilentRadioAdvisory, by name. The move
            // from an if-ladder to a rule file is only a win if nothing was
            // lost on the way across, and "we ported it" is not evidence.
            DiagnosticRuleSet set = Shipped();
            foreach (string id in new[]
            {
                "rx-no-radio",
                "rx-everything-muted",
                "rx-headphone-and-lineout-muted",
                "rx-headphone-muted",
                "rx-lineout-muted",
                "rx-both-levels-zero",
                "rx-headphone-level-zero",
                "rx-lineout-level-zero",
                "rx-both-levels-very-low",
                "rx-pc-audio-off-on-remote",
            })
                Assert.True(set.Rules.Any(r => r.Id == id), "rule \"" + id + "\" is missing");
        }

        [Fact]
        public void The_worst_news_is_reported_before_the_lesser_news()
        {
            // ORDER IS THE POINT of the original ladder and the easiest thing to
            // lose in a port. An operator told "the headphone output is muted",
            // who fixes it and finds the line out was muted too, has been sent
            // round twice by a check that knew both answers.
            DiagnosticRuleSet set = Shipped();
            int Index(string id) => set.Rules.ToList().FindIndex(r => r.Id == id);

            Assert.True(Index("rx-everything-muted") < Index("rx-headphone-and-lineout-muted"));
            Assert.True(Index("rx-headphone-and-lineout-muted") < Index("rx-headphone-muted"));
            Assert.True(Index("rx-both-levels-zero") < Index("rx-headphone-level-zero"));
            Assert.True(Index("rx-both-levels-zero") < Index("rx-both-levels-very-low"));
            // Mutes before levels: a mute is more disabling than a low setting.
            Assert.True(Index("rx-headphone-muted") < Index("rx-headphone-level-zero"));
        }

        [Fact]
        public void Every_receive_rule_names_a_stage_that_exists_and_has_a_verdict()
        {
            DiagnosticRuleSet set = Shipped();
            foreach (DiagnosticRule rule in set.Rules)
            {
                Assert.True(set.Stages.Any(s => s.Number == rule.StageNumber),
                            "rule \"" + rule.Id + "\" is in-stage " + rule.StageNumber
                            + ", which no stage declares");
                Assert.False(string.IsNullOrWhiteSpace(rule.Verdict),
                             "rule \"" + rule.Id + "\" has no verdict");
            }
        }
    }
}
