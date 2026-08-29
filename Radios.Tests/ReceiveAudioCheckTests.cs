using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The receive check has TWO DOORS AND ONE DEFINITION (#367).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel's ruling, 2026-08-28: "if someone has to add a test, it needs to end
    /// up on the same test if that makes sense, so as stage 0, it does rx audio
    /// as well, but you can just go to the other submenu option if you just
    /// wanted to test rx audio."
    /// </para>
    /// <para>
    /// <b>The test of the design is a property, not a behaviour:</b> add a rule
    /// to <c>rx-chain-rules.txt</c> and it must show up at both doors with no
    /// second edit. That property cannot be asserted by exercising one door, so
    /// the first two tests here assert it structurally — one rule file, one
    /// analyzer call, one phrasing — and the source scan at the bottom refuses
    /// the shape that would break it: a second implementation growing beside
    /// this one, which is this project's dominant defect and produces no merge
    /// conflict, no build error and no failing test on its own.
    /// </para>
    /// </remarks>
    public class ReceiveAudioCheckTests
    {
        // ── the shared definition ────────────────────────────────────────────

        /// <summary>
        /// Both doors' words come out of one assembly step, so a rule added to
        /// the file reaches both. Proved by adding a rule to a ruleset and
        /// finding its verdict in the pieces each door renders.
        /// </summary>
        [Fact]
        public void A_rule_added_to_the_file_reaches_the_verdict_the_problems_and_the_walk()
        {
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(
                "ruleset: Receive audio test\nchain: receive\n"
                + "symptom: you still cannot hear the radio\n"
                + "stage: 0 the kettle\n"
                + "rule: rx-invented-fault\nin-stage: 0\n"
                + "broken-when: kettle-plugged-in is no\n"
                + "verdict: The kettle is not plugged in.\n"
                + "fix: Plug the kettle in.\n");
            Assert.Empty(rules.Problems);

            var facts = new DiagnosticFacts();
            facts.Add(DiagnosticFact.Flag("kettle-plugged-in", "The kettle is plugged in", false));

            ReceiveCheckResult rx = ReceiveAudioCheck.Using(rules, facts);

            // The Workshop's door renders Verdict and Walk.
            Assert.Contains("The kettle is not plugged in.", rx.Verdict);
            Assert.Contains("Plug the kettle in.", rx.Verdict);
            Assert.Contains("the kettle", rx.Walk);

            // The Fixer's door renders Problems, as findings on stage 0.
            ReceiveProblem p = Assert.Single(rx.Problems);
            Assert.Equal("rx-invented-fault", p.Id);
            Assert.Equal("The kettle is not plugged in.", p.WhatIsWrong);
            Assert.Equal("Plug the kettle in.", p.WhatToDo);

            FixerOutcome stage0 = AudioSetupCheck.Analyze(new AudioSetupFacts(), rx);
            FixerFinding f = Assert.Single(stage0.Findings, x => x.Id == "rx-invented-fault");
            Assert.Equal(FixOwner.Operator, f.Owner);
            Assert.Equal("The kettle is not plugged in.", f.WhatIsWrong);
            Assert.Equal("Plug the kettle in.", f.WhatToDo);
        }

        /// <summary>
        /// The shipped rule file is what both doors actually walk, and it still
        /// parses with the slice rung in it.
        /// </summary>
        [Fact]
        public void The_shipped_receive_rules_carry_the_slice_rung_and_parse_clean()
        {
            RuleSetLoader.Forget();
            DiagnosticRuleSet set = RuleSetLoader.RxChain();

            Assert.True(set.Problems.Count == 0,
                        "the shipped receive rule file did not parse cleanly:" + Environment.NewLine
                        + string.Join(Environment.NewLine, set.Problems));
            Assert.Equal("receive", set.ChainName);
            Assert.Equal("you still cannot hear the radio", set.Symptom);

            foreach (string id in new[] { "rx-slice-muted", "rx-slice-level-zero",
                                          "rx-slice-level-very-low" })
                Assert.True(set.Rules.Any(r => r.Id == id), "rule \"" + id + "\" is missing");
        }

        /// <summary>
        /// Every slice rule is gated on there BEING a slice, at rule level.
        /// </summary>
        /// <remarks>
        /// Two things at once. The gate itself: <c>FlexBase.AudioGain</c> reads
        /// zero when no slice is active, so an ungated level rule would tell a
        /// radio with no slice that its volume is at zero — a control that is
        /// not there, reported as a control set wrongly.
        /// <para>
        /// And the LEVEL of the gate. A stage-level <c>needs:</c> on a flag
        /// currently renders as "not part of your receive path, because a slice
        /// is receiving is no" (#371), so the gate stays on the rules and the
        /// stage says it properly in its own nothing-to-check line.
        /// </para>
        /// </remarks>
        [Fact]
        public void Every_slice_rule_is_gated_on_a_slice_existing_and_the_stage_is_not()
        {
            RuleSetLoader.Forget();
            DiagnosticRuleSet set = RuleSetLoader.RxChain();

            DiagnosticStage slice = set.Stages.Single(s => s.Number == 1);
            Assert.Empty(slice.Needs);
            Assert.Contains("no slice is receiving", slice.NothingToCheck);

            foreach (DiagnosticRule rule in set.RulesFor(1))
            {
                Assert.True(rule.Needs.Any(c => string.Equals(c.FactName, "active-slice",
                                                              StringComparison.OrdinalIgnoreCase)),
                            "rule \"" + rule.Id + "\" reads the slice without checking that "
                            + "there is one");
            }
        }

        /// <summary>
        /// A muted slice is the answer even when an output is muted too: the
        /// chain fails at its earliest break, and the slice sits ahead of every
        /// output the radio has.
        /// </summary>
        [Fact]
        public void A_muted_slice_speaks_before_a_muted_output_does()
        {
            RuleSetLoader.Forget();
            DiagnosticFacts f = Connected();
            f.Add(DiagnosticFact.Flag("slice-muted", "The slice you are listening to is muted", true));
            f.Add(DiagnosticFact.Flag("headphone-muted", "The headphone output is muted", true));

            ReceiveCheckResult rx = ReceiveAudioCheck.From(f);

            Assert.Equal(1, rx.Report.FirstBroken?.Stage.Number);
            Assert.Contains("The slice you are listening to is muted.", rx.Verdict);
        }

        // ── the third answer (#370) ─────────────────────────────────────────

        /// <summary>
        /// Rules that could not be loaded must never read as a clean radio.
        /// </summary>
        /// <remarks>
        /// The call site this replaced said
        /// <c>StagesBroken &gt; 0 ? report : allClear</c>, so a missing embedded
        /// copy, an unreadable override, or an override that is empty or all
        /// comments all reported as good news — the exact failure a diagnostic
        /// exists to prevent, arriving inside the diagnostic.
        /// </remarks>
        [Fact]
        public void Rules_that_could_not_be_loaded_are_a_problem_and_never_an_all_clear()
        {
            ReceiveCheckResult rx = ReceiveAudioCheck.Using(new DiagnosticRuleSet(), Connected());

            Assert.True(rx.NothingCouldBeChecked);
            Assert.Contains("Nothing was checked", rx.Verdict);
            Assert.DoesNotContain("nothing is wrong", rx.Verdict, StringComparison.OrdinalIgnoreCase);

            ReceiveProblem p = Assert.Single(rx.Problems);
            Assert.Equal(ReceiveAudioCheck.NothingCheckedId, p.Id);

            // And it reaches the Fixer's stage 0 as a finding nobody here can
            // fix, rather than as silence.
            FixerOutcome stage0 = AudioSetupCheck.Analyze(new AudioSetupFacts(), rx);
            FixerFinding f = Assert.Single(stage0.Findings,
                x => x.Id == ReceiveAudioCheck.NothingCheckedId);
            Assert.Equal(FixOwner.NobodyHere, f.Owner);
        }

        /// <summary>
        /// The verdict is spoken by the stage's answer only when no finding
        /// already carries those exact words — but "nothing is wrong" and "not a
        /// clean bill of health" have no finding to carry them and must be said.
        /// </summary>
        [Fact]
        public void The_verdict_is_said_once_never_twice_and_never_not_at_all()
        {
            RuleSetLoader.Forget();

            // Broken: the finding says it, so the answer does not repeat it.
            DiagnosticFacts broken = Connected();
            broken.Add(DiagnosticFact.Flag("slice-muted",
                                           "The slice you are listening to is muted", true));
            ReceiveCheckResult bad = ReceiveAudioCheck.From(broken);
            Assert.NotEmpty(bad.Verdict);
            Assert.Equal("", bad.VerdictNotCarriedByProblems);

            // Clean: nothing carries it, so it is said.
            ReceiveCheckResult good = ReceiveAudioCheck.From(Connected());
            Assert.Equal(good.Verdict, good.VerdictNotCarriedByProblems);
            Assert.NotEmpty(good.VerdictNotCarriedByProblems);
        }

        /// <summary>
        /// The engine's own "still wrong" sentence names the operator's
        /// complaint, and on a receive walk that is not "still not being heard".
        /// </summary>
        [Fact]
        public void The_unclean_bill_of_health_names_the_receive_complaint()
        {
            RuleSetLoader.Forget();

            // A connected radio with the traffic facts missing: nothing fires,
            // and the measurement stage cannot be read, so the report refuses to
            // call it clean.
            DiagnosticFacts f = Connected();
            f.Add(DiagnosticFact.Absent("rx-audio-kbps", "Audio arriving over the network "
                                        + "from the radio", "the sampler could not be read"));

            ReceiveCheckResult rx = ReceiveAudioCheck.From(f);

            Assert.Contains("not a clean bill of health", rx.Verdict);
            Assert.Contains("you still cannot hear the radio", rx.Verdict);
            Assert.DoesNotContain("not being heard", rx.Verdict);
        }

        // ── the fold into stage 0 ───────────────────────────────────────────

        /// <summary>
        /// One cause, one entry. The receive rules' PC-audio rung and stage 0's
        /// own PC-audio finding fire on the same state, and only ours carries a
        /// button.
        /// </summary>
        [Fact]
        public void The_pc_audio_rung_is_not_reported_twice_when_we_offer_the_button()
        {
            RuleSetLoader.Forget();

            DiagnosticFacts f = Connected(pcAudio: false, remote: true);
            ReceiveCheckResult rx = ReceiveAudioCheck.From(f);
            Assert.Contains(rx.Problems, p => p.Id == AudioSetupCheck.RxPcAudioOffRule);

            var setup = new AudioSetupFacts { RemoteRadio = true, PcAudioOn = false,
                                              InputDeviceSelected = true };
            FixerOutcome outcome = AudioSetupCheck.Analyze(setup, rx);

            Assert.Contains(outcome.Findings, x => x.Id == AudioSetupCheck.PcAudioOff);
            Assert.DoesNotContain(outcome.Findings, x => x.Id == AudioSetupCheck.RxPcAudioOffRule);
        }

        /// <summary>
        /// A local radio raises no PC-audio finding of ours, so the receive
        /// rung — which is gated on being remote and therefore does not fire
        /// either — leaves exactly nothing behind. The suppression must not
        /// swallow a rung that had something to say.
        /// </summary>
        [Fact]
        public void The_suppression_only_applies_where_our_own_finding_actually_fired()
        {
            RuleSetLoader.Forget();

            // Invented rule with the same id, on a fact that is true, so it
            // fires with no PC-audio finding of ours in play.
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(
                "stage: 0 routing\n"
                + "rule: " + AudioSetupCheck.RxPcAudioOffRule + "\nin-stage: 0\n"
                + "broken-when: pc-audio is no\n"
                + "verdict: PC audio is off.\nfix: Turn it on.\n");
            var facts = new DiagnosticFacts();
            facts.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", false));

            ReceiveCheckResult rx = ReceiveAudioCheck.Using(rules, facts);

            // A LOCAL radio: AudioSetupCheck raises no pc-audio-off finding.
            var setup = new AudioSetupFacts { RemoteRadio = false, PcAudioOn = false,
                                              InputDeviceSelected = true };
            FixerOutcome outcome = AudioSetupCheck.Analyze(setup, rx);

            Assert.DoesNotContain(outcome.Findings, x => x.Id == AudioSetupCheck.PcAudioOff);
            Assert.Contains(outcome.Findings, x => x.Id == AudioSetupCheck.RxPcAudioOffRule);
        }

        /// <summary>
        /// The measurement — the one fact in stage 0 that is about the radio
        /// rather than about a switch of ours — reaches the answer, and every
        /// reading reaches the evidence the report carries to a manufacturer.
        /// </summary>
        [Fact]
        public void The_arrival_measurement_reaches_the_answer_and_the_readings_reach_the_evidence()
        {
            RuleSetLoader.Forget();

            DiagnosticFacts f = Connected();
            f.Add(DiagnosticFact.Measure("rx-audio-kbps", "Audio arriving over the network from "
                                         + "the radio", 42, "kilobits per second", "the radio"));
            ReceiveCheckResult rx = ReceiveAudioCheck.From(f);
            Assert.Contains("Audio arriving from the radio", rx.Arrival);

            FixerOutcome outcome = AudioSetupCheck.Analyze(new AudioSetupFacts(), rx);

            Assert.Contains("Audio arriving from the radio", outcome.Answer);
            Assert.Contains("Receive audio, walked from the radio's outputs", outcome.Evidence);
            Assert.Contains("Radio audio through this computer", outcome.Evidence);
            // The computer's own half is still first: the evidence block reads
            // as a walk and it starts at this end.
            Assert.True(outcome.Evidence.IndexOf("Audio setup, read from what is actually open",
                                                 StringComparison.Ordinal)
                        < outcome.Evidence.IndexOf("Receive audio, walked from",
                                                   StringComparison.Ordinal));
        }

        /// <summary>
        /// A host that wired no receive walk gets the computer's half and no
        /// invented receive answer — the same honesty every unwired delegate
        /// gets.
        /// </summary>
        [Fact]
        public void No_receive_walk_leaves_stage_0_saying_nothing_about_receive()
        {
            FixerOutcome outcome = AudioSetupCheck.Analyze(new AudioSetupFacts(), null);

            Assert.DoesNotContain("Audio arriving from the radio", outcome.Answer);
            Assert.DoesNotContain("Receive audio, walked from", outcome.Evidence);
        }

        // ── the guard against a second implementation ───────────────────────

        /// <summary>
        /// Both doors call <c>ReceiveAudioCheck.Run</c>, and neither builds the
        /// receive walk itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the test that protects the whole design, and it has to read
        /// SOURCE because the failure it guards against compiles, runs, passes
        /// every other test and produces no merge conflict: a second author
        /// writing <c>ChainAnalyzer.Run(RuleSetLoader.RxChain(), ...)</c> beside
        /// this one. Two homes for one idea, where one silently falls behind —
        /// which is exactly what the Workshop and the Fixer were before #367.
        /// </para>
        /// <para>
        /// <c>ReceiveAudioCheck</c> itself is the one place allowed to name the
        /// receive ruleset.
        /// </para>
        /// </remarks>
        [Fact]
        public void Only_the_shared_check_walks_the_receive_rules()
        {
            string root = RepoRoot();
            var offenders = new List<string>();
            var seen = new List<string>();
            bool sawTheSharedCall = false;

            foreach (string file in SourceFiles(root))
            {
                string name = Path.GetFileName(file);
                seen.Add(name);

                string text = File.ReadAllText(file);
                if (string.Equals(name, "ReceiveAudioCheck.cs", StringComparison.OrdinalIgnoreCase))
                {
                    // POSITIVE CONTROL. "I looked and found nothing" also claims
                    // the scan would have SEEN it; this is the one file that
                    // must contain the string, so a scan that reads no files —
                    // wrong root, wrong extension, a project renamed — fails
                    // here instead of passing silently for ever.
                    sawTheSharedCall = text.Contains("RuleSetLoader.RxChain()",
                                                     StringComparison.Ordinal);
                    continue;
                }
                if (string.Equals(name, "RuleSetLoader.cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (text.Contains("RuleSetLoader.RxChain()", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(root, file));
            }

            Assert.True(sawTheSharedCall,
                "the scan never read ReceiveAudioCheck.cs, so its 'nothing found' result says "
                + "nothing at all — " + seen.Count + " files were read");
            Assert.Contains("AudioWorkshopDialog.Diagnostics.cs", seen);
            Assert.Contains("FixerDialog.cs", seen);

            Assert.True(offenders.Count == 0,
                "the receive walk is built somewhere other than ReceiveAudioCheck, which is how "
                + "the Workshop's door and the Fixer's door start disagreeing: "
                + string.Join(", ", offenders));
        }

        /// <summary>
        /// Both doors are actually wired to it — a guard that only forbade a
        /// second implementation would pass just as happily if the first one
        /// were never called.
        /// </summary>
        [Fact]
        public void Both_doors_call_the_shared_check()
        {
            string root = RepoRoot();

            string workshop = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs",
                                                            "AudioWorkshopDialog.Diagnostics.cs"));
            Assert.Contains("ReceiveAudioCheck.Run(", workshop, StringComparison.Ordinal);

            string fixer = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs",
                                                         "FixerDialog.cs"));
            Assert.Contains("ReceiveAudioCheck.Run(", fixer, StringComparison.Ordinal);
            Assert.Contains("ReadReceiveChain", fixer, StringComparison.Ordinal);
        }

        // ── helpers ─────────────────────────────────────────────────────────

        /// <summary>A connected radio with nothing wrong on the settings side,
        /// including the slice rung.</summary>
        private static DiagnosticFacts Connected(bool pcAudio = true, bool remote = false)
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", true));
            f.Add(DiagnosticFact.Flag("active-slice", "A slice is receiving", true));
            f.Add(DiagnosticFact.Flag("slice-muted", "The slice you are listening to is muted", false));
            f.Add(DiagnosticFact.Measure("slice-level", "Slice volume", 60));
            f.Add(DiagnosticFact.Flag("headphone-muted", "The headphone output is muted", false));
            f.Add(DiagnosticFact.Flag("lineout-muted", "The line out output is muted", false));
            f.Add(DiagnosticFact.Flag("front-speaker-muted", "The front speaker is muted", false));
            f.Add(DiagnosticFact.Measure("headphone-level", "Headphone level", 60));
            f.Add(DiagnosticFact.Measure("lineout-level", "Line out level", 60));
            f.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", pcAudio));
            f.Add(DiagnosticFact.Flag("remote-radio", "Connected remotely", remote));
            return f;
        }

        private static IEnumerable<string> SourceFiles(string root)
        {
            foreach (string project in new[] { "Radios", "JJFlexWpf" })
            {
                string dir = Path.Combine(root, project);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*.cs",
                                                                 SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, file);
                    if (rel.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                                     StringComparison.Ordinal)
                        || rel.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                                        StringComparison.Ordinal))
                        continue;
                    yield return file;
                }
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
