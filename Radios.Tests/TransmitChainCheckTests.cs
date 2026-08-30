using System;
using System.Collections.Generic;
using System.IO;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The transmit walk has TWO DOORS AND ONE DEFINITION (#400).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receive half was joined on 2026-08-28 (#367). The transmit half was
    /// not, and the consequence was exact: the Fixer report of 2026-08-29 said
    /// <i>"Checks read from Receive audio check"</i> and carried no transmit walk
    /// at all, on a run diagnosing a transmit fault. Meanwhile the Audio
    /// Workshop, which owns the walk, cannot key a radio — so its report says
    /// "transmit and run the test again" three times on every run there has ever
    /// been.
    /// </para>
    /// <para>
    /// <b>The test of the design is a property, not a behaviour:</b> add a rule
    /// to <c>tx-chain-rules.txt</c> and it must show up at both doors with no
    /// second edit. That property cannot be asserted by exercising one door, so
    /// the first tests here assert it structurally, and the source scan at the
    /// bottom refuses the shape that would break it: a second implementation
    /// growing beside this one, which produces no merge conflict, no build error
    /// and no failing test on its own.
    /// </para>
    /// </remarks>
    public class TransmitChainCheckTests
    {
        // ── the shared definition ────────────────────────────────────────────

        /// <summary>
        /// Both doors' words come out of one assembly step, so a rule added to
        /// the file reaches both. Proved by adding a rule to a ruleset and
        /// finding its verdict in every piece each door renders.
        /// </summary>
        [Fact]
        public void A_rule_added_to_the_file_reaches_the_verdict_the_problems_and_the_walk()
        {
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(
                "ruleset: Transmit chain check\nchain: transmit\n"
                + "symptom: you are still not being heard\n"
                + "stage: 0 the kettle\n"
                + "rule: tx-invented-fault\nin-stage: 0\n"
                + "broken-when: kettle-plugged-in is no\n"
                + "verdict: The kettle is not plugged in.\n"
                + "fix: Plug the kettle in.\n");
            Assert.Empty(rules.Problems);

            var facts = new DiagnosticFacts();
            facts.Add(DiagnosticFact.Flag("kettle-plugged-in", "The kettle is plugged in", false));

            TransmitCheckResult tx = TransmitChainCheck.Using(rules, facts);

            // The Workshop's door renders Verdict, Census and Walk.
            Assert.Contains("The kettle is not plugged in.", tx.Verdict);
            Assert.Contains("the kettle", tx.Walk);

            // The Fixer's door renders Problems and Evidence.
            Assert.Contains(tx.Problems, p => p.Id == "tx-invented-fault"
                                              && p.WhatIsWrong.Contains("kettle")
                                              && p.WhatToDo.Contains("Plug the kettle in"));
            Assert.Contains("The kettle is not plugged in.", tx.Evidence);
        }

        /// <summary>
        /// The transmit stages carry the walk into the report in the operator's
        /// words, and the same three parts for both of them.
        /// </summary>
        [Fact]
        public void The_keying_stages_carry_the_walk_into_their_outcome()
        {
            TransmitCheckResult walk = Broken();

            FixerOutcome injected = TransmitStages.Injected(
                new InjectedTransmitFacts { ChainWalk = walk });
            FixerOutcome spoken = TransmitStages.Spoken(
                new SpokenTransmitFacts { Attempted = true, ChainWalk = walk }, null);

            foreach (FixerOutcome o in new[] { injected, spoken })
            {
                Assert.Contains(o.Findings, f => f.Id == "tx-invented-fault");
                Assert.Contains("Transmit chain, walked from your microphone", o.Evidence);
                Assert.Contains("The kettle is not plugged in.", o.Evidence);
            }
        }

        /// <summary>
        /// A stage that took no walk invents no chain answer — the same honesty
        /// every unwired delegate gets.
        /// </summary>
        [Fact]
        public void No_walk_leaves_the_keying_stages_saying_nothing_about_the_chain()
        {
            FixerOutcome injected = TransmitStages.Injected(new InjectedTransmitFacts());
            FixerOutcome spoken = TransmitStages.Spoken(
                new SpokenTransmitFacts { Attempted = true }, null);

            foreach (FixerOutcome o in new[] { injected, spoken })
            {
                Assert.Empty(o.Findings);
                Assert.DoesNotContain("Transmit chain, walked from your microphone", o.Evidence);
            }
        }

        /// <summary>
        /// "Nothing could be checked" is a THIRD ANSWER and travels as a
        /// problem, so no caller can let it read as good news (#370).
        /// </summary>
        [Fact]
        public void A_rule_file_that_loaded_nothing_is_not_reported_as_a_clean_bill_of_health()
        {
            TransmitCheckResult tx = TransmitChainCheck.Using(
                DiagnosticRuleSet.Parse("# nothing but a comment\n"), new DiagnosticFacts());

            Assert.True(tx.NothingCouldBeChecked);
            Assert.Contains(tx.Problems, p => p.Id == TransmitChainCheck.NothingCheckedId);

            // And it reaches the operator through a keying stage as a finding
            // nobody in the room can act on, rather than as silence.
            FixerOutcome o = TransmitStages.Injected(new InjectedTransmitFacts { ChainWalk = tx });
            Assert.Contains(o.Findings, f => f.Id == TransmitChainCheck.NothingCheckedId
                                             && f.Owner == FixOwner.NobodyHere);
        }

        /// <summary>
        /// The shipped rule set names the three stages that only a transmitting
        /// radio can answer — the whole reason the Fixer had to run this walk.
        /// </summary>
        /// <remarks>
        /// A POSITIVE CONTROL for the claim this task rests on. "Three stages
        /// need a transmission" is an assertion about the rule file, and the
        /// file can be edited; if those stages are ever renumbered or their
        /// gating changed, the sentence in every comment around this work stops
        /// being true and this is where it is caught.
        /// </remarks>
        [Fact]
        public void Three_shipped_stages_can_only_be_answered_while_transmitting()
        {
            DiagnosticRuleSet shipped = RuleSetLoader.TxChain();
            Assert.Empty(shipped.Problems);

            // A connected radio, taking transmit audio from this computer, and
            // NOT transmitting — which is every run the Audio Workshop has ever
            // been able to make, because nothing in that room can key a radio.
            var facts = new DiagnosticFacts();
            facts.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", true));
            facts.Add(DiagnosticFact.Text("mic-source", "Microphone input selected on the radio",
                                          "PC"));
            facts.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", true));
            facts.Add(DiagnosticFact.Flag("transmitting", "The radio is transmitting right now",
                                          false));

            TransmitCheckResult idle = TransmitChainCheck.From(facts);

            foreach (string stage in new[] { "the microphone actually capturing",
                                             "what the radio says it hears",
                                             "radio frequency out of the radio" })
            {
                Assert.Contains(stage, idle.Walk);
            }

            // Three stages, three refusals, all of them the same sentence — and
            // that sentence is the whole of #400 in the operator's own words.
            int asks = 0;
            int from = 0;
            while (true)
            {
                int at = idle.Walk.IndexOf("transmit and run the test again", from,
                                           StringComparison.Ordinal);
                if (at < 0) break;
                asks++;
                from = at + 1;
            }
            Assert.Equal(3, asks);
        }

        // ── the guard against a second implementation ───────────────────────

        /// <summary>
        /// Both doors call <c>TransmitChainCheck</c>, and neither walks the
        /// transmit rules itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the test that protects the whole design, and it has to read
        /// SOURCE because the failure it guards against compiles, runs, passes
        /// every other test and produces no merge conflict: a second author
        /// writing <c>ChainAnalyzer.Run(RuleSetLoader.TxChain(), …)</c> beside
        /// this one. Two homes for one idea, where one silently falls behind.
        /// </para>
        /// <para>
        /// <c>TransmitChainCheck</c> itself is the one place allowed to name the
        /// transmit ruleset.
        /// </para>
        /// </remarks>
        [Fact]
        public void Only_the_shared_check_walks_the_transmit_rules()
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
                if (string.Equals(name, "TransmitChainCheck.cs", StringComparison.OrdinalIgnoreCase))
                {
                    // POSITIVE CONTROL. "I looked and found nothing" also claims
                    // the scan would have SEEN it; this is the one file that
                    // must contain the string, so a scan that reads no files —
                    // wrong root, wrong extension, a project renamed — fails
                    // here instead of passing silently for ever.
                    sawTheSharedCall = text.Contains("RuleSetLoader.TxChain()",
                                                     StringComparison.Ordinal);
                    continue;
                }
                if (string.Equals(name, "RuleSetLoader.cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (text.Contains("RuleSetLoader.TxChain()", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(root, file));
            }

            Assert.True(sawTheSharedCall,
                "the scan never read TransmitChainCheck.cs, so its 'nothing found' result says "
                + "nothing at all — " + seen.Count + " files were read");
            Assert.Contains("AudioWorkshopDialog.Diagnostics.cs", seen);
            Assert.Contains("FixerTransmitAudioBoundary.cs", seen);

            Assert.True(offenders.Count == 0,
                "the transmit walk is built somewhere other than TransmitChainCheck, which is "
                + "how the Workshop's door and the Fixer's door start disagreeing: "
                + string.Join(", ", offenders));
        }

        /// <summary>
        /// Both doors are actually wired to it.
        /// </summary>
        /// <remarks>
        /// <b>NOT redundant with the scan above, and the distinction is the
        /// whole bug this task fixed.</b> A guard that only forbids a second
        /// implementation passes just as happily if the first one is never
        /// called — which is precisely the state the Fixer was in on
        /// 2026-08-29: one perfectly good thirteen-stage walk, in one place,
        /// reached by one surface, and that surface could not key a radio.
        /// </remarks>
        [Fact]
        public void Both_doors_call_the_shared_check()
        {
            string root = RepoRoot();

            // Door one: the Audio Workshop's Diagnostics tab.
            string workshop = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs",
                                                            "AudioWorkshopDialog.Diagnostics.cs"));
            Assert.Contains("TransmitChainCheck.Run(", workshop, StringComparison.Ordinal);

            // Door two: the Fixer's keying stages, INSIDE the keyed window. The
            // walk is worth nothing taken a moment later — the three stages that
            // needed a transmission would report "nothing to check" again — so
            // the call has to live in the boundary that holds the carrier up,
            // not in the dialog that asks for it.
            string boundary = File.ReadAllText(Path.Combine(root, "Radios", "ChainChecks",
                                                            "FixerTransmitAudioBoundary.cs"));
            Assert.Contains("TransmitChainCheck.Run(", boundary, StringComparison.Ordinal);

            // And the host actually supplies the computer's half, or the walk
            // arrives with four of its stages permanently unmeasured.
            string fixer = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs",
                                                         "FixerDialog.cs"));
            Assert.Contains("pcChainFacts", fixer, StringComparison.Ordinal);
        }

        /// <summary>
        /// The walk is taken while the radio is still keyed, in BOTH keying
        /// stages.
        /// </summary>
        /// <remarks>
        /// Structural, and it has to be: the difference between a walk that
        /// answers stages 2, 11 and 12 and one that answers none of them is a
        /// few lines of position, and moving it past the unkey breaks nothing
        /// that any other test can see. So this asserts the position — the call
        /// appears before the <c>finally</c> that drops the carrier — rather
        /// than a behaviour no test without a transmitter can observe.
        /// </remarks>
        [Fact]
        public void The_walk_is_taken_before_the_carrier_is_dropped()
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "ChainChecks",
                                                        "FixerTransmitAudioBoundary.cs"));

            int walks = 0;
            int from = 0;
            while (true)
            {
                int at = text.IndexOf("WalkWhileKeyed(rig)", from, StringComparison.Ordinal);
                if (at < 0) break;
                walks++;
                from = at + 1;

                // The unkey lives in a finally; every walk must come before one.
                int unkey = text.IndexOf("UnkeyMox(rig)", at, StringComparison.Ordinal);
                Assert.True(unkey > at,
                    "a chain walk at offset " + at + " is not followed by an unkey, so it is "
                    + "either outside the keyed window or the unkey has moved");
            }

            // Two calls: the injected stage and the spoken stage. Both key, both
            // carry audio, and a walk in only one of them would leave the other
            // stage's report unable to name the failing link.
            Assert.Equal(2, walks);
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static TransmitCheckResult Broken()
        {
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(
                "ruleset: Transmit chain check\nchain: transmit\n"
                + "stage: 0 the kettle\n"
                + "rule: tx-invented-fault\nin-stage: 0\n"
                + "broken-when: kettle-plugged-in is no\n"
                + "verdict: The kettle is not plugged in.\n"
                + "fix: Plug the kettle in.\n");
            var facts = new DiagnosticFacts();
            facts.Add(DiagnosticFact.Flag("kettle-plugged-in", "The kettle is plugged in", false));
            return TransmitChainCheck.Using(rules, facts);
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
