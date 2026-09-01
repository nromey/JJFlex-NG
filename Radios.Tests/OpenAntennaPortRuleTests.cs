using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The stage 12 rules that judge what the antenna did with the power, run
    /// against the readings a real 8600 produced on 2026-08-22.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these exist as a separate file.</b> A diagnostic rule that cannot
    /// fire is indistinguishable, from the outside, from a station with nothing
    /// wrong. That is not a hypothetical: on 2026-08-22 the bench 8600
    /// transmitted into an EMPTY antenna connector — the dummy load was on the
    /// other port — and returned 76 percent of its power straight back into the
    /// finals, while its own SWR meter reported 1.008 and the high-swr rule,
    /// testing "above 3", never fired. The stage read healthy. Two full sessions
    /// of measurements were taken through that silence before anyone noticed the
    /// load was never getting warm.
    /// </para>
    /// <para>
    /// So every test here is a positive control first. The point is not that the
    /// rules are wired up; it is that they FIRE on numbers we watched a radio
    /// produce, and stay quiet on numbers from the same radio minutes later with
    /// the load actually in circuit.
    /// </para>
    /// </remarks>
    public sealed class OpenAntennaPortRuleTests
    {
        private static DiagnosticRuleSet Rules()
        {
            RuleSetLoader.Forget();
            return RuleSetLoader.TxChain();
        }

        /// <summary>
        /// A radio mid-transmission with everything before stage 12 in order, so
        /// that whatever the analyzer reports is about the antenna and not about
        /// something further up the chain.
        /// </summary>
        private static DiagnosticFacts Transmitting(
            double forwardWatts, double reflectedWatts, double swr, bool tuning = false)
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", true));
            f.Add(DiagnosticFact.Text("mic-source", "Microphone input selected on the radio", "PC"));
            f.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", true));
            f.Add(DiagnosticFact.Text("pc-tx-path-trouble", "Path trouble", ""));
            f.Add(DiagnosticFact.Text("pc-input-device", "Microphone chosen on this computer", "Yeti Nano"));
            f.Add(DiagnosticFact.Flag("pc-input-device-present", "The chosen microphone is present", true));
            f.Add(DiagnosticFact.Flag("pc-mic-muted", "Windows has the microphone muted", false));
            f.Add(DiagnosticFact.Measure("pc-mic-level", "Windows input level", 72, "percent"));
            f.Add(DiagnosticFact.Flag("pc-tx-audio-flowing", "Sound is reaching the transmit stream", true));
            f.Add(DiagnosticFact.Flag("mic-profile-empty", "The radio has no mic profile selected", false));
            f.Add(DiagnosticFact.Measure("mic-gain", "Mic gain on the radio", 35));
            f.Add(DiagnosticFact.Flag("transmitting", "The radio is transmitting right now", true));
            f.Add(DiagnosticFact.Measure("sc-mic-recent", "Transmit audio heard", -8, "dBFS"));

            f.Add(DiagnosticFact.Measure("forward-power", "Forward power", forwardWatts, "watts"));
            f.Add(DiagnosticFact.Measure("reflected-power", "Reflected power", reflectedWatts, "watts"));
            f.Add(DiagnosticFact.Measure("reflected-percent", "Power coming back",
                                         reflectedWatts / forwardWatts * 100.0, "percent"));
            f.Add(DiagnosticFact.Measure("swr", "Standing wave ratio", swr, "to 1"));
            f.Add(DiagnosticFact.Measure("rf-power-setting", "Transmit power setting", 100, "percent"));
            f.Add(DiagnosticFact.Flag("atu-tuning", "The antenna tuner is running a tune cycle", tuning));
            f.Add(DiagnosticFact.Flag("dummy-load", "Dummy load mode", false));
            // The readings in this file came off ANT1 and ANT2 on 2026-08-22,
            // and until #188 nothing recorded which. Stage 12 now asks, and a
            // fixture that does not answer describes a radio whose transmit
            // path could not be identified — see TransverterStageTests.
            f.Add(DiagnosticFact.Text("tx-antenna", "Transmit antenna port", "ANT1"));
            f.Add(DiagnosticFact.Text("rx-antenna", "Receive antenna port", "ANT1"));
            f.Add(DiagnosticFact.Flag("transverter-path", "Transmitting through a transverter", false));
            // Voice mode, so stage 12 can judge whether zero power is a
            // consequence of silence upstream rather than a fault (#437).
            f.Add(DiagnosticFact.Flag("tx-audio-mode", "This transmit mode carries audio", true));
            return f;
        }

        /// <summary>The empty ANT1 connector, 2026-08-22. 17.5 W out, 13.4 W back.</summary>
        private static DiagnosticFacts OpenPort(bool tuning = false) =>
            Transmitting(17.5, 13.4, 14.9, tuning);

        /// <summary>The dummy load on ANT2, same radio, minutes later. 101.2 W out, 0.054 W back.</summary>
        private static DiagnosticFacts GoodLoad() =>
            Transmitting(101.2, 0.054, 1.047);

        private static StageResult Stage(ChainReport r, int number) =>
            r.Stages.First(s => s.Stage.Number == number);

        [Fact]
        public void The_open_antenna_port_is_reported_as_broken()
        {
            // THE positive control for this whole file. If this ever goes green
            // by not firing, the rule has stopped working and the failure is
            // silent — which is precisely the defect being fixed.
            ChainReport r = ChainAnalyzer.Run(Rules(), OpenPort());

            Assert.Equal(StageVerdict.Broken, Stage(r, 12).Verdict);
        }

        [Fact]
        public void The_verdict_names_the_disconnected_port_rather_than_a_ratio()
        {
            // An operator who is told "your standing wave ratio is 14.9" has to
            // know what that means before they can act. An operator told the
            // antenna port may have nothing connected to it can go and check.
            ChainReport r = ChainAnalyzer.Run(Rules(), OpenPort());

            Assert.Equal("power-coming-back", Stage(r, 12).Rule?.Id);
            Assert.Contains("coming straight back", r.Headline());
            Assert.Contains("nothing connected", r.Headline());
        }

        [Fact]
        public void The_open_port_verdict_wins_over_the_ratio_when_both_apply()
        {
            // Both rules genuinely fire on these numbers — 76 percent back is
            // also an SWR of nearly 15. The analyzer reports the first rule in
            // file order, so power-coming-back is deliberately listed first.
            // If someone reorders the file, this test says so.
            ChainReport r = ChainAnalyzer.Run(Rules(), OpenPort());

            Assert.NotEqual("high-swr", Stage(r, 12).Rule?.Id);
        }

        [Fact]
        public void The_good_dummy_load_leaves_the_antenna_stage_healthy()
        {
            // The negative control, and the half without which the test above
            // proves nothing. A rule that fired on everything would also fire on
            // the open port.
            ChainReport r = ChainAnalyzer.Run(Rules(), GoodLoad());

            Assert.Equal(StageVerdict.Healthy, Stage(r, 12).Verdict);
        }

        [Fact]
        public void A_running_tune_cycle_does_not_report_a_broken_antenna()
        {
            // A tuner transmits into a deliberately bad match and walks toward a
            // good one, so high reflected power during a tune is the tuner doing
            // its job. Without this guard, every single tune-up would announce a
            // disconnected antenna — and a warning that cries wolf on a routine
            // action is a warning the operator learns to ignore, which costs
            // more than never having built it.
            ChainReport r = ChainAnalyzer.Run(Rules(), OpenPort(tuning: true));

            Assert.NotEqual("power-coming-back", Stage(r, 12).Rule?.Id);
            Assert.NotEqual("high-swr", Stage(r, 12).Rule?.Id);

            // And this is the assertion that makes the two above mean anything.
            // A typo in the atu-tuning fact name would also make both rules stop
            // firing — by making them unevaluatable rather than by suppressing
            // them — and the stage would come back NOT OBSERVABLE. Demanding
            // HEALTHY proves the stage still ran its other checks and passed
            // them, which is the difference between a guard and a hole.
            Assert.Equal(StageVerdict.Healthy, Stage(r, 12).Verdict);
        }

        [Fact]
        public void A_mismatch_short_of_disconnection_still_reports_the_ratio()
        {
            // SWR 4 is about 36 percent reflected — under the 40 percent gate on
            // power-coming-back, over the 3 to 1 gate on high-swr. The two rules
            // are meant to cover different territory rather than one shadowing
            // the other, and this is the band that proves it.
            ChainReport r = ChainAnalyzer.Run(Rules(), Transmitting(100, 36, 4.0));

            Assert.Equal("high-swr", Stage(r, 12).Rule?.Id);
        }

        [Fact]
        public void The_evidence_carries_the_reflected_power_the_operator_never_saw()
        {
            // The number that would have ended the 2026-08-22 confusion in
            // seconds was reflected power, which nothing surfaced. Whatever else
            // changes, it belongs in the report.
            ChainReport r = ChainAnalyzer.Run(Rules(), OpenPort());
            string report = r.EvidenceText();

            Assert.Contains("Reflected power", report);
        }
    }
}
