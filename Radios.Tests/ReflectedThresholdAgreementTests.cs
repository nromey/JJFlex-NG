using System.Globalization;
using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The reflected-power threshold has exactly one home —
    /// <see cref="TransmitSafety.ReflectedWarnPercent"/> — and three consumers:
    /// the live PTT warning, the chain check's power-coming-back rule, and the
    /// transmit-check tune probe's fallback. This holds them together.
    /// </summary>
    /// <remarks>
    /// Task #237. The pairing between the first two was documented in both
    /// places and honoured for months; the third consumer was then written at
    /// 20 percent without reading either note. A comment asks future editors
    /// to keep figures in step; only a test notices when one does not.
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
        public void The_probe_fallback_is_pinned_to_the_live_warning()
        {
            // Deliberately stricter — half — and the ratio is a pin awaiting
            // Noel's ruling, not a ruling itself (#237). What this test
            // forbids is the two moving APART again: if either constant is
            // edited back to an independent literal, this is where it shows.
            Assert.Equal(TransmitSafety.ReflectedWarnPercent / 2.0,
                         TxTuneProbe.ReflectedSuspectPercent);

            // And stricter means stricter. A "more cautious" fallback that
            // drifted above the live warning would invert the reasoning the
            // constant's remarks give for existing at all.
            Assert.True(TxTuneProbe.ReflectedSuspectPercent
                        < TransmitSafety.ReflectedWarnPercent);
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
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(0.04f, 0.02f)));

            // At and above the floor the ratio is real, and it is clamped to 1
            // — a share of more than everything is a meter artefact, not a fact.
            Assert.False(float.IsNaN(TransmitSafety.ReflectedFractionOf(0.05f, 0.02f)));
            Assert.Equal(1f, TransmitSafety.ReflectedFractionOf(1f, 3f));
        }
    }
}
