using System.Globalization;
using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The diagnostic reflected-power ladder has exactly one home,
    /// <see cref="SwrLadder"/>, stated in SWR, and every percentage of
    /// reflected power is derived from one of its rungs: the live PTT warning
    /// and the chain check's power-coming-back rule from the warn rung, the
    /// transmit-check tune probe's fallbacks from the suspect and abort rungs.
    /// This holds them together.
    /// </summary>
    /// <remarks>
    /// Task #237. The ladder was ruled by Noel on 2026-08-26 (SWR 2.5 suspect,
    /// 4 warn, 5 abort) and its warn rung extended to the live warning on
    /// 2026-11-02 ("Yes, SWR 4 everywhere"). Until then this class pinned a
    /// PLACEHOLDER (the probe at half of a 40 percent warning) while presenting
    /// itself as the guard; it was changed on purpose to pin the ruling.
    /// </remarks>
    public class ReflectedThresholdAgreementTests
    {
        [Fact]
        public void The_chain_rule_uses_the_same_threshold_as_the_live_warning()
        {
            // The rules file is data and cannot name the constant, so this
            // parses the SHIPPED file exactly as the app does and reads the
            // condition back.
            DiagnosticRuleSet set = RuleSetLoader.TxChain();
            DiagnosticRule rule = set.Rules.FirstOrDefault(r => r.Id == "power-coming-back");
            Assert.NotNull(rule);

            Condition threshold = rule.BrokenWhen.FirstOrDefault(
                c => c.FactName == "reflected-percent");
            Assert.NotNull(threshold);

            string expected = "reflected-percent above "
                + TransmitSafety.ReflectedWarnPercent.ToString("0.##",
                                                              CultureInfo.InvariantCulture);
            Assert.Equal(expected, threshold.Text);
        }

        [Fact]
        public void The_ladder_is_the_one_noel_ruled()
        {
            // Literals on purpose: this is the ruling, written down where a
            // reader of the test can check it against the register.
            Assert.Equal(2.5, SwrLadder.Suspect);
            Assert.Equal(4.0, SwrLadder.Warn);
            Assert.Equal(5.0, SwrLadder.Abort);

            // Suspect is reported, warn is spoken, abort is acted on: the
            // rungs climb, and the warn rung exists between the other two.
            Assert.True(SwrLadder.Suspect < SwrLadder.Warn);
            Assert.True(SwrLadder.Warn < SwrLadder.Abort);
        }

        [Fact]
        public void Each_percentage_is_its_rung_converted_not_a_literal_of_its_own()
        {
            // The conversion by hand, so a broken ReflectedPercentAt cannot
            // agree with itself: ((SWR - 1) / (SWR + 1)) squared.
            Assert.Equal(100.0 * (1.5 / 3.5) * (1.5 / 3.5), SwrLadder.SuspectReflectedPercent, 9);
            Assert.Equal(36.0, SwrLadder.WarnReflectedPercent, 9);
            Assert.Equal(100.0 * (4.0 / 6.0) * (4.0 / 6.0), SwrLadder.AbortReflectedPercent, 9);

            Assert.Equal(SwrLadder.ReflectedPercentAt(SwrLadder.Suspect), SwrLadder.SuspectReflectedPercent, 9);
            Assert.Equal(SwrLadder.ReflectedPercentAt(SwrLadder.Warn), SwrLadder.WarnReflectedPercent, 9);
            Assert.Equal(SwrLadder.ReflectedPercentAt(SwrLadder.Abort), SwrLadder.AbortReflectedPercent, 9);
        }

        [Fact]
        public void The_live_warning_is_the_warn_rung()
        {
            Assert.Equal(SwrLadder.WarnReflectedPercent, TransmitSafety.ReflectedWarnPercent, 9);
        }

        [Fact]
        public void The_probe_stands_on_the_same_ladder()
        {
            // SWR side and percent side of each probe threshold are one rung,
            // so the abort cannot again sit at SWR 5 on one side and 5.83 on
            // the other.
            Assert.Equal(SwrLadder.Suspect, TxTuneProbe.SwrSuspect);
            Assert.Equal(SwrLadder.Abort, TxTuneProbe.SwrAbort);
            Assert.Equal(SwrLadder.SuspectReflectedPercent, TxTuneProbe.ReflectedSuspectPercent, 9);
            Assert.Equal(SwrLadder.AbortReflectedPercent, TxTuneProbe.ReflectedAbortPercent, 9);

            // The unattended probe is more cautious than the warning a person
            // hears while holding the key, and its abort is past that warning.
            Assert.True(TxTuneProbe.ReflectedSuspectPercent < TransmitSafety.ReflectedWarnPercent);
            Assert.True(TxTuneProbe.ReflectedAbortPercent > TransmitSafety.ReflectedWarnPercent);
        }

        [Fact]
        public void The_conversion_refuses_what_is_not_an_swr()
        {
            Assert.True(double.IsNaN(SwrLadder.ReflectedPercentAt(double.NaN)));
            Assert.True(double.IsNaN(SwrLadder.ReflectedPercentAt(0.9)));
            Assert.Equal(0.0, SwrLadder.ReflectedPercentAt(1.0), 9);
            Assert.Equal(100.0, SwrLadder.ReflectedPercentAt(double.PositiveInfinity), 9);
        }

        [Fact]
        public void The_fraction_and_the_percent_are_the_same_number()
        {
            Assert.Equal((float)(TransmitSafety.ReflectedWarnPercent / 100.0),
                         TransmitSafety.ReflectedWarnFraction);
        }

        [Fact]
        public void The_cause_rule_still_fires_before_the_ratio_rule()
        {
            // Documented in the rules file: power-coming-back is listed BEFORE
            // high-swr so the analyzer names a cause rather than a ratio. The
            // ordering is load-bearing prose policy, so it is pinned here
            // rather than left to survive file edits by luck.
            DiagnosticRuleSet set = RuleSetLoader.TxChain();
            var stage12 = set.RulesFor(12);

            int cause = stage12.FindIndex(r => r.Id == "power-coming-back");
            int ratio = stage12.FindIndex(r => r.Id == "high-swr");

            Assert.True(cause >= 0, "power-coming-back is missing from stage 12");
            Assert.True(ratio >= 0, "high-swr is missing from stage 12");
            Assert.True(cause < ratio,
                "power-coming-back must be listed before high-swr, so the operator "
                + "hears a cause they can act on rather than a ratio to interpret");
        }

        [Fact]
        public void A_ratio_from_meaningless_forward_power_is_refused_not_invented()
        {
            // The auditor's open question from #237, settled: the probe's
            // reflected share now goes through TransmitSafety.ReflectedFractionOf,
            // whose low-forward guard refuses to divide by a meter wandering
            // around zero. Below the floor: no answer, rather than any answer.
            // The floor is THE absolute forward-power floor since Sprint 47
            // (#571) — this asserted a bare 0.04 against 0.05 until then, a
            // dead-key number the bench found thirty times too low.
            float floor = TransmitSafety.ForwardFloorWatts;
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(floor - 0.01f, 0.02f)));

            // At and above the floor the ratio is real, and it is clamped to 1
            // — a share of more than everything is a meter artefact, not a fact.
            Assert.False(float.IsNaN(TransmitSafety.ReflectedFractionOf(floor, 0.02f)));
            Assert.Equal(1f, TransmitSafety.ReflectedFractionOf(floor, floor * 3f));
        }
    }
}
