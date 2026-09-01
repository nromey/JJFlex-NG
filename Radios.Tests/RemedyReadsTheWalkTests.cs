using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A REMEDY MAY NOT NAME A CAUSE THE SAME WALK HAS ALREADY EXCLUDED (#448),
    /// AND A CONSEQUENCE MAY NOT BE REPORTED AS AN INDEPENDENT FAULT (#437).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both defects came off one run of the Fixer on Don's 6300, 31 August 2026,
    /// and both have one cause: a rule's remedy was a static string chosen by
    /// rule id, written with no access to anything the rest of the walk found.
    /// Stage 11 told him to check the mic profile and the microphone input, four
    /// lines under stage 9 and stage 8 reporting both <i>checked, nothing
    /// wrong</i>. Stage 12 told him to check that his power setting was not at
    /// the bottom, when its own guard only lets it fire when the setting is
    /// above zero, and when stage 2's tune carrier had already put 9.3 watts on
    /// the air at that same setting.
    /// </para>
    /// <para>
    /// <b>Nothing failed.</b> Every stage was right, every rule was right, every
    /// test passed, and the document contradicted itself twice. That is why this
    /// file drives the SHIPPED rule file against a reconstruction of his
    /// readings rather than testing the mechanism alone: a mechanism nobody
    /// applied is exactly the shape of defect being fixed here.
    /// </para>
    /// </remarks>
    public sealed class RemedyReadsTheWalkTests
    {
        private static DiagnosticRuleSet Shipped()
        {
            RuleSetLoader.Forget();
            return RuleSetLoader.TxChain();
        }

        private static StageResult Stage(ChainReport r, int number) =>
            r.Stages.First(s => s.Stage.Number == number);

        // ── the shipped file is readable ────────────────────────────────────

        /// <summary>
        /// The positive control for everything below. cleared-by resolves rule
        /// ids by name, and a typo in one is reported rather than silently
        /// meaning "never clears" — so if the shipped file has a problem line,
        /// every other test here would be measuring the wrong thing.
        /// </summary>
        [Fact]
        public void The_shipped_transmit_rules_still_load_with_nothing_unreadable()
        {
            Assert.Empty(Shipped().Problems);
        }

        // ── the mechanism ───────────────────────────────────────────────────

        private static DiagnosticRuleSet TwoStages(string extra)
            => DiagnosticRuleSet.Parse(
                "ruleset: Toy\nchain: transmit\nsymptom: nobody hears you\n"
                + "stage: 1 the upstream thing\n"
                + "stage: 2 the downstream thing\n"
                + "rule: upstream\nin-stage: 1\n"
                + "broken-when: upstream-broken is yes\n"
                + "verdict: The upstream thing is broken.\n"
                + "fix: Mend the upstream thing.\n"
                + "rule: downstream\nin-stage: 2\n"
                + "broken-when: downstream-broken is yes\n"
                + "verdict: The downstream thing is broken.\n"
                + "fix: Go and check the upstream thing.\n"
                + extra);

        private static DiagnosticFacts Facts(bool? upstream, bool downstream)
        {
            var f = new DiagnosticFacts();
            f.Add(upstream == null
                ? DiagnosticFact.Absent("upstream-broken", "The upstream thing is broken",
                                        "nobody could read it")
                : DiagnosticFact.Flag("upstream-broken", "The upstream thing is broken", upstream.Value));
            f.Add(DiagnosticFact.Flag("downstream-broken", "The downstream thing is broken", downstream));
            return f;
        }

        private const string Declared =
            "cleared-by: upstream\n"
            + "fix-when-cleared: The upstream thing was checked and is fine, so there is "
            + "fix-when-cleared: nothing left here to change.\n";

        [Fact]
        public void A_cause_the_walk_tested_and_cleared_gets_the_other_remedy()
        {
            ChainReport r = ChainAnalyzer.Run(TwoStages(Declared), Facts(upstream: false, downstream: true));

            StageResult s = Stage(r, 2);
            Assert.Equal(StageVerdict.Broken, s.Verdict);
            Assert.True(s.RemedyCleared);
            Assert.DoesNotContain("Go and check the upstream thing", s.Remedy);
            Assert.Contains("was checked and is fine", s.Remedy);
        }

        [Fact]
        public void A_cause_that_is_actually_broken_keeps_the_ordinary_remedy()
        {
            // Both stages fire. The downstream remedy points at the upstream
            // thing, and it is right to — this is the case it was written for.
            ChainReport r = ChainAnalyzer.Run(TwoStages(Declared), Facts(upstream: true, downstream: true));

            StageResult s = Stage(r, 2);
            Assert.False(s.RemedyCleared);
            Assert.Contains("Go and check the upstream thing", s.Remedy);
        }

        [Fact]
        public void A_cause_that_could_not_be_read_does_not_clear_anything()
        {
            // THE ONE THAT MATTERS MOST. A check we could not make must never be
            // spent as a check that passed — that is the rule the whole engine
            // is built on, and clearing on an unreadable answer would launder an
            // unmade check into a ruled-out cause.
            ChainReport r = ChainAnalyzer.Run(TwoStages(Declared), Facts(upstream: null, downstream: true));

            StageResult s = Stage(r, 2);
            Assert.False(s.RemedyCleared);
            Assert.Contains("Go and check the upstream thing", s.Remedy);
        }

        [Fact]
        public void A_cause_whose_rule_never_applied_does_not_clear_anything()
        {
            // The upstream rule is guarded off for this operator entirely, so it
            // excluded nothing. Not applicable is not the same as ruled out.
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(
                "ruleset: Toy\nchain: transmit\n"
                + "stage: 1 the upstream thing\n"
                + "stage: 2 the downstream thing\n"
                + "rule: upstream\nin-stage: 1\n"
                + "needs: upstream-applies is yes\n"
                + "broken-when: upstream-broken is yes\n"
                + "verdict: The upstream thing is broken.\nfix: Mend it.\n"
                + "rule: downstream\nin-stage: 2\n"
                + "broken-when: downstream-broken is yes\n"
                + "verdict: The downstream thing is broken.\n"
                + "fix: Go and check the upstream thing.\n" + Declared);
            Assert.Empty(rules.Problems);

            DiagnosticFacts f = Facts(upstream: false, downstream: true);
            f.Add(DiagnosticFact.Flag("upstream-applies", "The upstream rule applies", false));

            Assert.False(Stage(ChainAnalyzer.Run(rules, f), 2).RemedyCleared);
        }

        [Fact]
        public void Clearing_does_not_depend_on_which_stage_comes_first()
        {
            // The walk phrases its remedies only after every rule has answered,
            // so a stage 1 remedy may name a cause stage 2 clears. Without the
            // second pass this would silently depend on file order.
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(
                "ruleset: Toy\nchain: transmit\n"
                + "stage: 1 the upstream thing\n"
                + "stage: 2 the downstream thing\n"
                + "rule: upstream\nin-stage: 1\n"
                + "broken-when: upstream-broken is yes\n"
                + "verdict: The upstream thing is broken.\n"
                + "fix: Go and check the downstream thing.\n"
                + "cleared-by: downstream\n"
                + "fix-when-cleared: The downstream thing is already known to be fine.\n"
                + "rule: downstream\nin-stage: 2\n"
                + "broken-when: downstream-broken is yes\n"
                + "verdict: The downstream thing is broken.\nfix: Mend it.\n");
            Assert.Empty(rules.Problems);

            StageResult s = Stage(ChainAnalyzer.Run(rules, Facts(upstream: true, downstream: false)), 1);
            Assert.True(s.RemedyCleared);
            Assert.Contains("already known to be fine", s.Remedy);
        }

        // ── the rule file cannot declare it wrong in silence ─────────────────

        [Theory]
        [InlineData("cleared-by: nobody\nfix-when-cleared: All clear.\n", "not a rule in this file")]
        [InlineData("cleared-by: upstream\n", "nothing to say once those checks come back clean")]
        [InlineData("fix-when-cleared: All clear.\n", "nothing can ever make it the remedy")]
        [InlineData("cleared-by: downstream\nfix-when-cleared: All clear.\n", "names itself")]
        public void A_cleared_by_that_cannot_work_is_reported_rather_than_ignored(string extra, string says)
        {
            DiagnosticRuleSet rules = TwoStages(extra);
            Assert.Contains(rules.Problems, p => p.Contains(says));
        }

        // ── #448, against the shipped file and Don's readings ────────────────

        /// <summary>
        /// Don's 6300 on 31 August 2026: transmitting, computer audio on, the
        /// radio listening to PC, a mic profile loaded, and the radio's own
        /// microphone meter on the floor.
        /// </summary>
        private static DiagnosticFacts DonsRun(bool micProfileEmpty = false,
                                               string micSource = "PC",
                                               double scMicDb = -150,
                                               double forwardWatts = 0,
                                               string mode = "LSB")
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", true));
            f.Add(DiagnosticFact.Text("mic-source", "Microphone input selected on the radio", micSource));
            f.Add(DiagnosticFact.Text("mic-source-options", "Microphone inputs this radio offers",
                                      "MIC, ACC, PC"));
            f.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", true));
            f.Add(DiagnosticFact.Text("pc-tx-path-trouble", "Path trouble", ""));
            f.Add(DiagnosticFact.Text("pc-input-device", "Microphone chosen on this computer", "Yeti Nano"));
            f.Add(DiagnosticFact.Flag("pc-input-device-present", "The chosen microphone is present", true));
            f.Add(DiagnosticFact.Flag("pc-mic-muted", "Windows has the microphone muted", false));
            f.Add(DiagnosticFact.Measure("pc-mic-level", "Windows input level", 72, "percent"));
            f.Add(DiagnosticFact.Flag("pc-tx-audio-flowing", "Sound is reaching the transmit stream", true));
            f.Add(DiagnosticFact.Measure("pc-tx-loudness", "Transmit loudness on this computer", -14, "dBFS"));
            f.Add(DiagnosticFact.Flag("tx-stream-open", "A transmit audio stream is open", true));
            f.Add(DiagnosticFact.Flag("tx-stream-is-opus", "Opened as Opus", true));
            f.Add(DiagnosticFact.Text("tx-stream-compression", "How the radio opened it", "OPUS"));
            f.Add(DiagnosticFact.Flag("mic-profile-empty", "The radio has no mic profile selected",
                                      micProfileEmpty));
            f.Add(DiagnosticFact.Text("mic-profile", "Mic profile selected on the radio", "Default"));
            f.Add(DiagnosticFact.Text("mic-profile-suggested", "Mic profile the radio would load", "Default"));
            f.Add(DiagnosticFact.Measure("mic-profile-count", "Mic profiles this radio offers", 27, ""));
            f.Add(DiagnosticFact.Measure("mic-gain", "Mic gain on the radio", 35));
            f.Add(DiagnosticFact.Flag("transmitting", "The radio is transmitting right now", true));
            f.Add(DiagnosticFact.Measure("sc-mic-recent", "Transmit audio heard", scMicDb, "dBFS"));
            f.Add(DiagnosticFact.Measure("sc-mic-peak", "Loudest transmit audio", scMicDb, "dBFS"));
            f.Add(DiagnosticFact.Measure("sw-alc", "ALC", -150, "dBFS"));
            f.Add(DiagnosticFact.Measure("codec-mic", "Codec microphone meter", -120, "dBFS"));
            f.Add(DiagnosticFact.Measure("forward-power", "Forward power", forwardWatts, "watts"));
            f.Add(DiagnosticFact.Measure("reflected-power", "Reflected power", 0, "watts"));
            f.Add(DiagnosticFact.Measure("reflected-percent", "Power coming back", 0, "percent"));
            f.Add(DiagnosticFact.Measure("swr", "Standing wave ratio", 1.25, "to 1"));
            f.Add(DiagnosticFact.Measure("rf-power-setting", "Transmit power setting", 10,
                                         TxPowerPhrasing.SettingUnits));
            f.Add(DiagnosticFact.Flag("atu-tuning", "The antenna tuner is running a tune cycle", false));
            f.Add(DiagnosticFact.Flag("dummy-load", "Dummy load mode", false));
            f.Add(DiagnosticFact.Text("tx-antenna", "Transmit antenna port", "ANT1"));
            f.Add(DiagnosticFact.Text("rx-antenna", "Receive antenna port", "ANT1"));
            f.Add(DiagnosticFact.Flag("transverter-path", "Transmitting through a transverter", false));
            f.Add(DiagnosticFact.Text("tx-slice", "Transmit slice", "A"));
            f.Add(DiagnosticFact.Text("tx-mode", "Transmit mode", mode));
            f.Add(DiagnosticFact.Flag("tx-audio-mode", "This transmit mode carries audio",
                                      Radios.Fixer.TransmitStageSet.IsTransmitAudioMode(mode)));
            f.Add(DiagnosticFact.Text("ptt-source", "What is keying the transmitter", "the app"));
            return f;
        }

        [Fact]
        public void Stage_eleven_still_fires_on_Dons_readings()
        {
            // Positive control. If the radio-hears-nothing rule ever stops
            // firing, every assertion below about its remedy goes green for the
            // wrong reason.
            ChainReport r = ChainAnalyzer.Run(Shipped(), DonsRun());

            Assert.Equal(StageVerdict.Broken, Stage(r, 11).Verdict);
            Assert.Equal("radio-hears-nothing", Stage(r, 11).Rule?.Id);
        }

        [Fact]
        public void Stage_eleven_does_not_send_him_back_to_what_stages_eight_and_nine_cleared()
        {
            ChainReport r = ChainAnalyzer.Run(Shipped(), DonsRun());

            // The walk really did check both, and reported both clean.
            Assert.Equal(StageVerdict.Healthy, Stage(r, 8).Verdict);
            Assert.Equal(StageVerdict.Healthy, Stage(r, 9).Verdict);

            string remedy = Stage(r, 11).Remedy;
            Assert.True(Stage(r, 11).RemedyCleared);
            Assert.DoesNotContain("no mic profile selected", remedy);
            Assert.DoesNotContain("different microphone input", remedy);
            Assert.Contains("past what this computer can watch", remedy);
        }

        [Fact]
        public void Stage_eleven_keeps_its_ordinary_advice_while_a_cause_is_still_standing()
        {
            // With the mic profile actually empty, stage 9 fires, so the cause
            // is real and naming it is right. This is the sentence #448 must not
            // throw away in the course of fixing itself.
            ChainReport r = ChainAnalyzer.Run(Shipped(), DonsRun(micProfileEmpty: true));

            Assert.False(Stage(r, 11).RemedyCleared);
            Assert.Contains("no mic profile selected", Stage(r, 11).Remedy);
        }

        // ── #437, one fault reported once ───────────────────────────────────

        [Fact]
        public void Zero_power_with_nothing_to_modulate_is_reported_as_a_consequence()
        {
            ChainReport r = ChainAnalyzer.Run(Shipped(), DonsRun());

            StageResult s = Stage(r, 12);
            Assert.Equal("no-power-out-nothing-to-modulate", s.Rule?.Id);
            Assert.Contains("follows from the step above rather than being a second fault", s.Message);
            Assert.Contains("nothing to change here", s.Remedy);
        }

        [Fact]
        public void The_zero_power_advice_no_longer_names_a_setting_its_own_guard_excluded()
        {
            // Silence at the microphone is not the cause here — the radio IS
            // hearing audio — so the ordinary no-power-out rule speaks. Its
            // remedy used to say "check that your transmit power setting is not
            // at the bottom" while its own "rf-power-setting above 0" guard
            // meant it could only fire when the setting was off the bottom.
            ChainReport r = ChainAnalyzer.Run(Shipped(), DonsRun(scMicDb: -12));

            StageResult s = Stage(r, 12);
            Assert.Equal("no-power-out", s.Rule?.Id);
            Assert.DoesNotContain("not at the bottom", s.Remedy);
            Assert.Contains("band this radio can transmit on", s.Remedy);
        }

        [Fact]
        public void In_a_mode_that_makes_no_power_from_audio_the_consequence_is_not_claimed()
        {
            // CW has no transmit audio path at all, so "there is nothing to
            // modulate" is simply false there. The guard reads
            // TransmitStageSet.TransmitAudioModes rather than a second list.
            Assert.False(Radios.Fixer.TransmitStageSet.IsTransmitAudioMode("CW"));

            ChainReport r = ChainAnalyzer.Run(Shipped(), DonsRun(mode: "CW"));

            Assert.NotEqual("no-power-out-nothing-to-modulate", Stage(r, 12).Rule?.Id);
        }

        [Fact]
        public void An_unread_mode_leaves_the_consequence_unclaimed_rather_than_guessed()
        {
            DiagnosticFacts f = DonsRun();
            f.Add(DiagnosticFact.Absent("tx-audio-mode", "This transmit mode carries audio",
                                        "the transmit slice has not reported its mode yet"));

            ChainReport r = ChainAnalyzer.Run(Shipped(), f);

            Assert.NotEqual("no-power-out-nothing-to-modulate", Stage(r, 12).Rule?.Id);
        }
    }
}
