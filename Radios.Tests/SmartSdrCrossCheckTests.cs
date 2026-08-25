using System;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The question asked before a transmit diagnosis goes to FlexRadio.
    /// </summary>
    /// <remarks>
    /// The property that matters is that the app NEVER states the operator's
    /// claim for them, and that the third answer — works fine in SmartSDR —
    /// routes them away from the vendor rather than into a support queue with
    /// a bug of ours.
    /// </remarks>
    public class SmartSdrCrossCheckTests
    {
        [Fact]
        public void We_never_write_the_claim_when_it_has_not_been_made()
        {
            // THE test. The claim reframes the whole document, so an
            // unverifiable version in our voice would poison the section a
            // vendor is most likely to act on. Not asked means nothing said.
            Assert.Equal("", SmartSdrCrossCheck.EvidenceLine(SmartSdrCrossCheck.Answer.NotAsked));
            Assert.Equal("", SmartSdrCrossCheck.EvidenceLine(SmartSdrCrossCheck.Answer.WorksInSmartSdr));
        }

        [Fact]
        public void A_confirmed_cross_check_is_attributed_to_the_operator()
        {
            // Positive control for the test above, and the attribution is the
            // point: it is their observation, reported as theirs.
            string line = SmartSdrCrossCheck.EvidenceLine(SmartSdrCrossCheck.Answer.SameInSmartSdr);

            Assert.Contains("The operator reports", line);
            Assert.Contains("SmartSDR", line);
        }

        [Fact]
        public void Not_having_tried_is_stated_rather_than_left_silent()
        {
            // Omitting the section entirely reads as though the question never
            // arose. Saying "not tried" tells a support engineer how much
            // weight to give the rest — the same courtesy the chain check
            // already extends by reporting what it could not see.
            string line = SmartSdrCrossCheck.EvidenceLine(SmartSdrCrossCheck.Answer.NotTested);

            Assert.Contains("NOT tried", line);
            Assert.Contains("not yet known", line);
        }

        [Fact]
        public void Working_in_SmartSDR_means_do_not_send_it_to_the_radio_maker()
        {
            // THE branch worth building for. If it works in their client and
            // not in ours, the fault is OURS. Letting the operator send that to
            // FlexRadio wastes their time, wastes a support engineer's, and
            // spends credibility the next genuine report will need.
            Assert.False(SmartSdrCrossCheck.WorthSendingToFlex(SmartSdrCrossCheck.Answer.WorksInSmartSdr));

            string g = SmartSdrCrossCheck.OperatorGuidance(SmartSdrCrossCheck.Answer.WorksInSmartSdr);
            Assert.Contains("do NOT send this to FlexRadio", g);
            Assert.Contains("ours to fix", g);
            // And redirects rather than just refusing.
            Assert.Contains("Send it to us", g);
        }

        [Fact]
        public void Every_other_answer_still_leaves_the_report_worth_sending()
        {
            // Negative control for the branch above. A check that returned
            // false for everything would pass that test and block every report.
            Assert.True(SmartSdrCrossCheck.WorthSendingToFlex(SmartSdrCrossCheck.Answer.SameInSmartSdr));
            Assert.True(SmartSdrCrossCheck.WorthSendingToFlex(SmartSdrCrossCheck.Answer.NotTested));
            Assert.True(SmartSdrCrossCheck.WorthSendingToFlex(SmartSdrCrossCheck.Answer.NotAsked));
        }

        [Fact]
        public void Every_answer_gets_a_next_step_not_just_a_verdict()
        {
            // This is our own operator, so guidance says what it thinks and
            // tells them what to do about it. The reticence rule governs the
            // VENDOR's document, not how we talk to the person in front of us.
            foreach (SmartSdrCrossCheck.Answer a in Enum.GetValues<SmartSdrCrossCheck.Answer>())
            {
                string g = SmartSdrCrossCheck.OperatorGuidance(a);
                Assert.False(string.IsNullOrWhiteSpace(g), "no guidance for " + a);
                Assert.True(g.Length > 40, "guidance for " + a + " is too thin to act on");
            }
        }

        [Fact]
        public void Not_tested_is_encouraged_rather_than_scolded()
        {
            // An operator who has not done the cross-check has done nothing
            // wrong. The report still stands on its measurements, and the
            // prompt exists to teach, not to gate.
            string g = SmartSdrCrossCheck.OperatorGuidance(SmartSdrCrossCheck.Answer.NotTested);

            Assert.Contains("That is fine", g);
            Assert.Contains("still stands", g);
            Assert.Contains("more convincing", g);
        }

        [Fact]
        public void The_question_can_be_answered_either_way_meaningfully()
        {
            // A prompt phrased so that only one answer is useful trains people
            // to give that answer. Both a yes and a no here change what the
            // report says.
            Assert.Contains("SmartSDR", SmartSdrCrossCheck.Question);
            Assert.NotEqual(SmartSdrCrossCheck.EvidenceLine(SmartSdrCrossCheck.Answer.SameInSmartSdr), SmartSdrCrossCheck.EvidenceLine(SmartSdrCrossCheck.Answer.NotTested));
        }
    }
}
