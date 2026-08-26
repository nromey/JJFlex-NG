using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Stage 12 read through a transverter, where every threshold written for
    /// an antenna is off by three orders of magnitude.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect these guard, #163.</b> Legal transverter drive is 0.0001
    /// to 0.032 watts — FlexLib clamps <c>Xvtr.MaxPower</c> to -10.00 through
    /// +15.00 dBm — and the bench 8600 at its minimum power setting puts out
    /// 0.036 watts. The radio's minimum output IS the transverter drive spec;
    /// that is not a coincidence. Meanwhile stage 12's rules are guarded on
    /// "rf-power-setting above 0" and "forward-power at least 1", and a
    /// transverter operator lives at power setting 0 permanently. So all three
    /// checks were switched off for that operator, by side effect, and the
    /// stage reported HEALTHY.
    /// </para>
    /// <para>
    /// That is the false all-clear, and it is the reason the fix is not a
    /// lower threshold: 0.1 watts sits inside the legal transverter band and so
    /// does 0.01, so any single absolute figure is wrong for one path or the
    /// other. Today's guard fails silent; a lowered number would fail WRONG.
    /// </para>
    /// <para>
    /// Every test here is a positive control first — the antenna-path case is
    /// asserted alongside, so a rule that stopped firing altogether cannot pass
    /// these by being uniformly quiet.
    /// </para>
    /// </remarks>
    public sealed class TransverterStageTests
    {
        private static DiagnosticRuleSet Rules()
        {
            RuleSetLoader.Forget();
            return RuleSetLoader.TxChain();
        }

        /// <summary>
        /// A radio mid-transmission with everything before stage 12 in order,
        /// so whatever comes back is about the RF and not about the microphone.
        /// </summary>
        /// <param name="powerReadable">False models what the fact source now
        /// does on a transverter path below the meters' useful floor: the four
        /// power facts come back ABSENT with a reason rather than reporting a
        /// number the instrument cannot resolve.</param>
        private static DiagnosticFacts Transmitting(
            bool transverter, double forwardWatts, double reflectedWatts, double swr,
            bool powerReadable = true, int rfPowerSetting = 100)
        {
            const string XvtrFloor =
                "your transmit antenna is the transverter port. Legal transverter drive is "
                + "hundredths of a watt, and the radio's power meters read its own amplifier "
                + "in tens of watts, so a reading this low cannot tell transverter drive from "
                + "a dead key";

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

            f.Add(DiagnosticFact.Text("tx-antenna", "Transmit antenna port",
                                      transverter ? "XVTR" : "ANT1"));
            f.Add(DiagnosticFact.Text("rx-antenna", "Receive antenna port",
                                      transverter ? "XVTR" : "ANT1"));
            f.Add(DiagnosticFact.Flag("transverter-path", "Transmitting through a transverter",
                                      transverter));

            if (powerReadable)
            {
                f.Add(DiagnosticFact.Measure("forward-power", "Forward power", forwardWatts, "watts"));
                f.Add(DiagnosticFact.Measure("reflected-power", "Reflected power", reflectedWatts, "watts"));
                f.Add(DiagnosticFact.Measure("reflected-percent", "Power coming back",
                                             reflectedWatts / forwardWatts * 100.0, "percent"));
                f.Add(DiagnosticFact.Measure("swr", "Standing wave ratio", swr, "to 1"));
            }
            else
            {
                f.Add(DiagnosticFact.Absent("forward-power", "Forward power", XvtrFloor));
                f.Add(DiagnosticFact.Absent("reflected-power", "Reflected power", XvtrFloor));
                f.Add(DiagnosticFact.Absent("reflected-percent", "Power coming back", XvtrFloor));
                f.Add(DiagnosticFact.Absent("swr", "Standing wave ratio", XvtrFloor));
            }

            f.Add(DiagnosticFact.Measure("rf-power-setting", "Transmit power setting",
                                         rfPowerSetting, "percent"));
            f.Add(DiagnosticFact.Flag("atu-tuning", "The antenna tuner is running a tune cycle", false));
            f.Add(DiagnosticFact.Flag("dummy-load", "Dummy load mode", false));
            return f;
        }

        private static StageResult Stage(ChainReport r) =>
            r.Stages.First(s => s.Stage.Number == 12);

        // ── The false all-clear ───────────────────────────────────────────

        [Fact]
        public void A_transverter_path_below_the_meter_floor_is_not_reported_healthy()
        {
            // THE test in this file. An operator at power setting 0 driving a
            // transverter used to get "stage 12 healthy" out of three checks
            // that were never run. Not observable is the honest answer: the
            // meters cannot resolve a hundredth of a watt, and a check that
            // could not be made must never read as one that passed.
            ChainReport r = ChainAnalyzer.Run(Rules(),
                Transmitting(transverter: true, forwardWatts: 0, reflectedWatts: 0, swr: 1,
                             powerReadable: false, rfPowerSetting: 0));

            Assert.NotEqual(StageVerdict.Healthy, Stage(r).Verdict);
            Assert.Equal(StageVerdict.NotObservable, Stage(r).Verdict);
        }

        [Fact]
        public void The_operator_is_told_why_the_reading_could_not_be_used()
        {
            // An unreadable stage that does not say why sends the operator
            // hunting at the wrong end of the station, which is the failure
            // this whole analyzer exists to prevent.
            ChainReport r = ChainAnalyzer.Run(Rules(),
                Transmitting(transverter: true, forwardWatts: 0, reflectedWatts: 0, swr: 1,
                             powerReadable: false, rfPowerSetting: 0));

            string reasons = string.Join(" ", Stage(r).Reasons);
            Assert.Contains("transverter port", reasons);
        }

        [Fact]
        public void The_same_silence_on_an_antenna_path_still_fires_no_power_out()
        {
            // The positive control for the test above. If stage 12 had simply
            // stopped judging power at all, the first test would pass for the
            // wrong reason and nothing would say so.
            ChainReport r = ChainAnalyzer.Run(Rules(),
                Transmitting(transverter: false, forwardWatts: 0.0, reflectedWatts: 0.0, swr: 1));

            Assert.Equal(StageVerdict.Broken, Stage(r).Verdict);
            Assert.Equal("no-power-out", Stage(r).Rule.Id);
        }

        // ── The one thing that can be judged on this path ─────────────────

        [Fact]
        public void A_hundred_watts_into_the_transverter_port_is_the_fault()
        {
            // 100 W is roughly three thousand times the most drive FlexLib will
            // let a transverter be sent. There is no reading of the antenna
            // rules that makes this acceptable, and it is the case where being
            // wrong destroys hardware.
            ChainReport r = ChainAnalyzer.Run(Rules(),
                Transmitting(transverter: true, forwardWatts: 101.2, reflectedWatts: 0.054,
                             swr: 1.047));

            Assert.Equal(StageVerdict.Broken, Stage(r).Verdict);
            Assert.Equal("transverter-overdrive", Stage(r).Rule.Id);
        }

        [Fact]
        public void The_overdrive_verdict_says_stop_rather_than_quoting_a_ratio()
        {
            // Order is the whole point. At a hundred watts into a transverter
            // port, power-coming-back and high-swr can also apply, and both
            // would discuss the standing wave ratio while the thing on the end
            // of the cable is being destroyed.
            ChainReport r = ChainAnalyzer.Run(Rules(),
                Transmitting(transverter: true, forwardWatts: 101.2, reflectedWatts: 40.0,
                             swr: 9.0));

            Assert.Equal("transverter-overdrive", Stage(r).Rule.Id);
            Assert.Contains("Stop transmitting", Stage(r).Message);
        }

        [Fact]
        public void The_overdrive_rule_is_listed_before_the_antenna_rules_it_can_outrank()
        {
            // The analyzer reports the first rule that fires in FILE order, so
            // this ordering is behaviour, not tidiness. A later edit that moves
            // the block would silently change which sentence an operator hears
            // during the one fault that damages equipment.
            DiagnosticRuleSet set = Rules();
            string[] ids = set.Rules.Where(x => x.StageNumber == 12)
                                    .Select(x => x.Id).ToArray();

            int overdrive = System.Array.IndexOf(ids, "transverter-overdrive");
            Assert.True(overdrive >= 0, "the transverter-overdrive rule is missing from stage 12");
            Assert.True(overdrive < System.Array.IndexOf(ids, "power-coming-back"));
            Assert.True(overdrive < System.Array.IndexOf(ids, "high-swr"));
        }

        [Fact]
        public void The_overdrive_rule_does_not_apply_to_an_antenna_path()
        {
            // A hundred watts into an antenna is a radio working correctly.
            ChainReport r = ChainAnalyzer.Run(Rules(),
                Transmitting(transverter: false, forwardWatts: 101.2, reflectedWatts: 0.054,
                             swr: 1.047));

            Assert.Equal(StageVerdict.Healthy, Stage(r).Verdict);
        }

        // ── The port travels with the measurement (#188) ──────────────────

        [Fact]
        public void Every_stage_12_rule_carries_the_transmit_antenna_as_evidence()
        {
            // #188. A power or standing-wave figure quoted without the port it
            // left by cannot be interpreted by anybody — including the person
            // who captured it, ten minutes later. On 2026-08-22 that turned a
            // whole bench session's readings into numbers with no context.
            DiagnosticRuleSet set = Rules();

            foreach (DiagnosticRule rule in set.Rules.Where(x => x.StageNumber == 12))
                Assert.Contains("tx-antenna", rule.AllFactNames());
        }

        [Fact]
        public void The_shipped_file_still_parses_with_no_problems()
        {
            // Adding rules to a file the loader forgives is exactly how a rule
            // stops existing without anything failing.
            DiagnosticRuleSet set = Rules();
            Assert.True(set.Problems.Count == 0,
                        "the shipped rule file did not parse cleanly:\r\n"
                        + string.Join("\r\n", set.Problems));
        }
    }
}
