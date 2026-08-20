using System;
using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 32 Track C. The rule engine and the shipped transmit chain
    /// ruleset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These run with no radio, no window and no thread, which is the whole
    /// reason the rules are data and the engine knows nothing about radios. A
    /// scenario here is a hand-built set of facts and an expected verdict, so
    /// every branch through the analyzer is reachable without a bench.
    /// </para>
    /// <para>
    /// The tests that matter most are the ones asserting what the analyzer must
    /// NOT do. A stage may never read healthy when no check was made; an absent
    /// meter may never fire the rule written for a silent one; and a fact the
    /// radio could not report may never default to zero and pass a comparison.
    /// Each of those would produce a confident wrong answer, which is worse than
    /// no diagnostic at all.
    /// </para>
    /// <para>
    /// They also load the REAL shipped rule file rather than a fixture. A
    /// ruleset that stopped parsing — a typo, a renamed embedded resource — is
    /// invisible to the compiler and would ship as an analyzer that silently
    /// ran no checks.
    /// </para>
    /// </remarks>
    public sealed class ChainAnalyzerTests
    {
        private static DiagnosticRuleSet Rules()
        {
            RuleSetLoader.Forget();
            return RuleSetLoader.TxChain();
        }

        /// <summary>A connected radio on computer audio with nothing wrong.</summary>
        private static DiagnosticFacts Healthy()
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
            f.Add(DiagnosticFact.Flag("transmitting", "The radio is transmitting right now", false));
            return f;
        }

        private static DiagnosticFacts Transmitting()
        {
            DiagnosticFacts f = Healthy();
            f.Add(DiagnosticFact.Flag("transmitting", "The radio is transmitting right now", true));
            f.Add(DiagnosticFact.Measure("sc-mic-recent", "Transmit audio heard", -8, "dBFS"));
            f.Add(DiagnosticFact.Measure("forward-power", "Forward power", 45, "watts"));
            f.Add(DiagnosticFact.Measure("swr", "Standing wave ratio", 1.2, "to 1"));
            f.Add(DiagnosticFact.Measure("rf-power-setting", "Transmit power setting", 50, "percent"));
            f.Add(DiagnosticFact.Flag("dummy-load", "Dummy load mode", false));
            return f;
        }

        private static StageResult Stage(ChainReport r, int number) =>
            r.Stages.First(s => s.Stage.Number == number);

        // ── The shipped rule file ─────────────────────────────────────────

        [Fact]
        public void The_shipped_ruleset_parses_with_no_problems()
        {
            DiagnosticRuleSet rules = Rules();
            Assert.Empty(rules.Problems);
            Assert.NotEmpty(rules.Stages);
            Assert.NotEmpty(rules.Rules);
        }

        [Fact]
        public void Every_rule_points_at_a_declared_stage_and_has_something_to_say()
        {
            DiagnosticRuleSet rules = Rules();
            foreach (DiagnosticRule r in rules.Rules)
            {
                Assert.Contains(rules.Stages, s => s.Number == r.StageNumber);
                Assert.NotEmpty(r.BrokenWhen);
                Assert.NotEmpty(r.Verdict);
            }
        }

        // ── The verdicts ──────────────────────────────────────────────────

        [Fact]
        public void With_no_radio_the_first_dead_stage_is_the_connection()
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", false));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal(0, r.FirstBroken?.Stage.Number);
            Assert.Contains("No radio is connected", r.Headline());
        }

        [Fact]
        public void With_no_radio_no_other_stage_claims_a_second_fault()
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", false));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            // Exactly one thing is wrong. A stage that read an unanswerable
            // question as a failure would stack an invented fault on the real
            // one and send the operator somewhere useless.
            Assert.Equal(1, r.StagesBroken);
        }

        [Fact]
        public void An_empty_mic_profile_is_reported_in_the_operators_own_words()
        {
            DiagnosticFacts f = Healthy();
            f.Add(DiagnosticFact.Flag("mic-profile-empty", "The radio has no mic profile selected", true));
            f.Add(DiagnosticFact.Text("mic-profile-suggested", "Mic profile the radio would load", "Default"));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal(9, r.FirstBroken?.Stage.Number);
            Assert.StartsWith("Your radio has no mic profile selected", r.Headline());
            // The remedy names the profile this radio actually offers, which is
            // the difference between an instruction and a hint.
            Assert.Contains("named Default", r.Headline());
        }

        [Fact]
        public void The_radio_hearing_nothing_while_transmitting_is_stage_eleven()
        {
            DiagnosticFacts f = Transmitting();
            f.Add(DiagnosticFact.Measure("sc-mic-recent", "Transmit audio heard", -150, "dBFS"));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal(11, r.FirstBroken?.Stage.Number);
        }

        [Fact]
        public void A_stopped_meter_speaks_before_the_value_it_is_holding()
        {
            // The floor reading and the stopped meter are both true at once.
            // The meter has to be believable before its number means anything,
            // so the meter rule must win — otherwise the operator is told their
            // radio hears nothing and goes to check their microphone.
            DiagnosticFacts f = Transmitting();
            f.Add(DiagnosticFact.Measure("sc-mic-recent", "Transmit audio heard", -150, "dBFS"));
            f.Add(DiagnosticFact.Silent("meter-sc-mic", "Radio transmit mic meter",
                                        "the radio lists it and has never sent a reading"));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal("radio-mic-meter-never-reported", r.FirstBroken?.Rule?.Id);
        }

        [Fact]
        public void High_swr_fires_and_the_operators_own_figure_is_in_the_sentence()
        {
            DiagnosticFacts f = Transmitting();
            f.Add(DiagnosticFact.Measure("forward-power", "Forward power", 40, "watts"));
            f.Add(DiagnosticFact.Measure("swr", "Standing wave ratio", 5.4, "to 1"));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal("high-swr", r.FirstBroken?.Rule?.Id);
            Assert.Contains("5.4", r.Headline());
        }

        [Fact]
        public void Dummy_load_mode_suppresses_the_power_rules()
        {
            // Without this guard a deliberate zero-watt bench session reads as
            // a dead transmitter.
            DiagnosticFacts f = Transmitting();
            f.Add(DiagnosticFact.Measure("forward-power", "Forward power", 0, "watts"));
            f.Add(DiagnosticFact.Measure("swr", "Standing wave ratio", 9.9, "to 1"));
            f.Add(DiagnosticFact.Flag("dummy-load", "Dummy load mode", true));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Null(r.FirstBroken);
        }

        [Fact]
        public void A_windows_mute_is_caught_on_the_computer_side()
        {
            DiagnosticFacts f = Healthy();
            f.Add(DiagnosticFact.Flag("pc-mic-muted", "Windows has the microphone muted", true));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal(3, r.FirstBroken?.Stage.Number);
        }

        // ── The three-state rule, which is the point of the whole design ──

        [Fact]
        public void A_healthy_radio_is_never_given_a_clean_bill_of_health_while_stages_are_unseen()
        {
            ChainReport r = ChainAnalyzer.Run(Rules(), Healthy());

            Assert.Null(r.FirstBroken);
            Assert.True(r.StagesBlind > 0, "the shipped ruleset declares stages it cannot see");
            Assert.DoesNotContain("nothing is wrong", r.Headline());
            Assert.Contains("not a clean bill of health", r.Headline());
        }

        [Fact]
        public void An_unreadable_fact_makes_its_stage_unobservable_never_healthy()
        {
            DiagnosticFacts f = Healthy();
            f.Add(DiagnosticFact.Absent("mic-profile-empty", "The radio has no mic profile selected",
                                        "the radio has not listed its mic profiles"));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal(StageVerdict.NotObservable, Stage(r, 9).Verdict);
        }

        [Fact]
        public void An_absent_meter_does_not_fire_the_rule_written_for_a_silent_one()
        {
            // Silent and absent are different states on purpose. A meter this
            // model does not have is not a meter that has gone quiet, and
            // saying so would send the operator to the wrong end of the radio.
            DiagnosticFacts f = Transmitting();
            f.Add(DiagnosticFact.Absent("meter-sc-mic", "Radio transmit mic meter",
                                        "this radio does not publish a meter named SC_MIC"));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.NotEqual("radio-mic-meter-never-reported", r.FirstBroken?.Rule?.Id);
            Assert.True(r.ChecksUnreadable > 0, "the checks against it are unmade, not passed");
        }

        [Fact]
        public void A_check_that_could_not_be_made_is_never_counted_as_one_that_passed()
        {
            DiagnosticFacts f = Healthy();
            f.Add(DiagnosticFact.Absent("pc-mic-muted", "Windows has the microphone muted",
                                        "the chosen microphone could not be found in Windows"));

            ChainReport before = ChainAnalyzer.Run(Rules(), Healthy());
            ChainReport after = ChainAnalyzer.Run(Rules(), f);

            Assert.True(after.ChecksUnreadable > before.ChecksUnreadable);
            Assert.True(after.ChecksMade < before.ChecksMade);
        }

        [Fact]
        public void Stages_off_the_operators_path_are_counted_apart_from_stages_we_could_not_read()
        {
            // A microphone in the radio's own jack means no computer audio and
            // no encoder. Reporting those as "could not check" would be true
            // and useless — it invites a hunt through a path nobody is using.
            DiagnosticFacts f = Healthy();
            f.Add(DiagnosticFact.Text("mic-source", "Microphone input selected on the radio", "MIC"));
            f.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", false));

            ChainReport jack = ChainAnalyzer.Run(Rules(), f);
            ChainReport pc = ChainAnalyzer.Run(Rules(), Healthy());

            Assert.True(jack.StagesNotInPath > 0);
            Assert.True(jack.StagesUnobservable < pc.StagesUnobservable);
            Assert.Contains("not in your transmit path", jack.Census());
        }

        [Fact]
        public void A_stage_with_nothing_to_check_yet_reads_differently_from_one_we_cannot_see()
        {
            ChainReport r = ChainAnalyzer.Run(Rules(), Healthy());

            Assert.True(r.StagesPending > 0);
            Assert.True(r.StagesBlind > 0);
            Assert.Equal(r.StagesUnobservable, r.StagesPending + r.StagesBlind);
            Assert.Contains("transmit and run the check again", Stage(r, 11).Line());
        }

        [Fact]
        public void A_fact_nobody_collects_is_unreadable_rather_than_false()
        {
            // A mistyped fact name in the rule file must degrade to a missing
            // check, never to a stage that silently always passes.
            var rules = DiagnosticRuleSet.Parse(
                "stage: 1 a stage\nrule: r\nin-stage: 1\nbroken-when: nobody-supplies-this is yes\n"
                + "verdict: something\n");
            Assert.Empty(rules.Problems);

            ChainReport r = ChainAnalyzer.Run(rules, new DiagnosticFacts());

            Assert.Equal(StageVerdict.NotObservable, r.Stages[0].Verdict);
            Assert.Equal(1, r.ChecksUnreadable);
            Assert.Equal(0, r.ChecksMade);
        }

        [Fact]
        public void An_unreadable_condition_beats_a_false_one_within_the_same_rule()
        {
            // The rule cannot fire, but it was also not fully evaluated. Half a
            // check is not a check.
            var rules = DiagnosticRuleSet.Parse(
                "stage: 1 a stage\nrule: r\nin-stage: 1\nbroken-when: a is yes\n"
                + "broken-when: b is yes\nverdict: something\n");
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("a", "A", false));
            f.Add(DiagnosticFact.Absent("b", "B", "not reported by this radio"));

            ChainReport r = ChainAnalyzer.Run(rules, f);

            Assert.Equal(StageVerdict.NotObservable, r.Stages[0].Verdict);
        }

        // ── Staleness ─────────────────────────────────────────────────────

        [Fact]
        public void A_reading_that_has_stopped_arriving_can_be_tested_for_age()
        {
            Condition c = Condition.Parse("m stale over 10 seconds", out string problem);
            Assert.Null(problem);

            var fresh = new DiagnosticFacts();
            fresh.Add(DiagnosticFact.Measure("m", "M", 1, "dBFS", "the radio", DateTime.UtcNow));
            Assert.Equal(Answer.No, c.Test(fresh, out _));

            var old = new DiagnosticFacts();
            old.Add(DiagnosticFact.Measure("m", "M", 1, "dBFS", "the radio",
                                           DateTime.UtcNow.AddMinutes(-5)));
            Assert.Equal(Answer.Yes, c.Test(old, out _));
        }

        [Fact]
        public void A_reading_with_no_timestamp_cannot_have_its_age_judged()
        {
            Condition c = Condition.Parse("m stale over 10 seconds", out _);
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Measure("m", "M", 1));

            Assert.Equal(Answer.Unreadable, c.Test(f, out _));
        }

        // ── The condition grammar ─────────────────────────────────────────

        [Theory]
        [InlineData("x is PC")]
        [InlineData("x is not PC")]
        [InlineData("x is empty")]
        [InlineData("x is not empty")]
        [InlineData("x contains something")]
        [InlineData("x below -100")]
        [InlineData("x above 3")]
        [InlineData("x at most 0")]
        [InlineData("x at least 1")]
        [InlineData("x silent")]
        [InlineData("x absent")]
        [InlineData("x readable")]
        [InlineData("x stale over 10 seconds")]
        public void Every_documented_test_parses(string line)
        {
            Assert.NotNull(Condition.Parse(line, out string problem));
            Assert.Null(problem);
        }

        [Theory]
        [InlineData("x wibbles 3")]
        [InlineData("x")]
        [InlineData("")]
        public void An_unrecognised_test_reports_a_problem_rather_than_throwing(string line)
        {
            Assert.Null(Condition.Parse(line, out string problem));
            Assert.False(string.IsNullOrEmpty(problem));
        }

        [Fact]
        public void Is_not_empty_beats_is_not_something()
        {
            // Longest match first, or "is not empty" would parse as a text
            // comparison against the literal word "empty".
            Condition c = Condition.Parse("x is not empty", out _);
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Text("x", "X", "empty"));

            // The value is the word "empty" and the fact is NOT empty, so the
            // test holds — which it would not if this had parsed as equality.
            Assert.Equal(Answer.Yes, c.Test(f, out _));
        }

        [Fact]
        public void A_boolean_reads_as_yes_or_no()
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("x", "X", true));
            Assert.Equal(Answer.Yes, Condition.Parse("x is yes", out _).Test(f, out _));
            Assert.Equal(Answer.No, Condition.Parse("x is no", out _).Test(f, out _));
        }

        // ── The rule file's own defences ──────────────────────────────────

        [Fact]
        public void A_rule_that_can_never_fire_is_reported_rather_than_shipped_silently()
        {
            var rules = DiagnosticRuleSet.Parse(
                "stage: 1 a stage\nrule: r\nin-stage: 1\nverdict: something\n");
            Assert.Contains(rules.Problems, p => p.Contains("never fire"));
        }

        [Fact]
        public void A_rule_pointing_at_a_stage_that_does_not_exist_is_reported()
        {
            var rules = DiagnosticRuleSet.Parse(
                "stage: 1 a stage\nrule: r\nin-stage: 7\nbroken-when: x is yes\nverdict: v\n");
            Assert.Contains(rules.Problems, p => p.Contains("not declared"));
        }

        [Fact]
        public void A_repeated_text_key_joins_rather_than_replacing()
        {
            var rules = DiagnosticRuleSet.Parse(
                "stage: 1 a stage\nrule: r\nin-stage: 1\nbroken-when: x is yes\n"
                + "verdict: One sentence.\nverdict: And another.\n");
            Assert.Equal("One sentence. And another.", rules.Rules[0].Verdict);
        }

        [Fact]
        public void Unparseable_text_yields_an_empty_ruleset_rather_than_an_exception()
        {
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse("this is not a ruleset at all");
            Assert.NotNull(rules);
            Assert.NotEmpty(rules.Problems);

            ChainReport r = ChainAnalyzer.Run(rules, Healthy());
            Assert.NotNull(r);
            Assert.Equal(0, r.ChecksApplicable);
        }

        [Fact]
        public void Running_with_nothing_at_all_does_not_throw()
        {
            ChainReport r = ChainAnalyzer.Run(null, null);
            Assert.NotNull(r);
            Assert.NotNull(r.Headline());
            Assert.NotNull(r.ToText());
            Assert.NotNull(r.EvidenceText());
        }

        // ── Facts ─────────────────────────────────────────────────────────

        [Fact]
        public void A_repeated_fact_replaces_its_value_and_keeps_its_place_in_the_reading_order()
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Text("a", "A", "first"));
            f.Add(DiagnosticFact.Text("b", "B", "second"));
            f.Add(DiagnosticFact.Text("a", "A", "revised"));

            Assert.Equal(2, f.All.Count);
            Assert.Equal("a", f.All[0].Name);
            Assert.Equal("revised", f.Find("A").TextValue);
        }

        [Fact]
        public void An_absent_fact_says_why_in_the_evidence_block()
        {
            DiagnosticFact f = DiagnosticFact.Absent("x", "The thing", "this radio does not report it");
            Assert.Contains("could not be read", f.EvidenceLine());
            Assert.Contains("this radio does not report it", f.EvidenceLine());
        }

        [Fact]
        public void Substitution_survives_an_unclosed_brace_and_an_unknown_name()
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Text("known", "Known", "yes"));

            Assert.Equal("yes", f.Fill("{known}"));
            Assert.Equal("not known", f.Fill("{missing}"));
            Assert.Equal("a {unclosed", f.Fill("a {unclosed"));
            Assert.Equal("", f.Fill(""));
            // Empty rather than null: this feeds straight into a report string,
            // and a null there would be a crash in a diagnostic.
            Assert.Equal("", f.Fill(null));
        }

        [Fact]
        public void Substitution_carries_the_units_with_the_number()
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Measure("p", "Power", 12.5, "watts"));
            Assert.Equal("12.5 watts", f.Fill("{p}"));
        }

        [Fact]
        public void The_evidence_block_carries_the_station_and_the_software()
        {
            ChainReport r = ChainAnalyzer.Run(Rules(), Healthy());
            string text = r.EvidenceText(new[] { "Model: FLEX-8600" }, new[] { "JJ Flexible version: 4.1.16" });

            Assert.Contains("Model: FLEX-8600", text);
            Assert.Contains("JJ Flexible version: 4.1.16", text);
            Assert.Contains("Readings, in signal-path order", text);
            // No tables anywhere: this gets read aloud.
            Assert.DoesNotContain("|", text);
        }
    }
}
