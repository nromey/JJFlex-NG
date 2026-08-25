using System;
using System.Text.RegularExpressions;
using Radios.Fixer;
using Xunit;
using static Radios.Tests.FixerTestKit;

namespace Radios.Tests
{
    /// <summary>
    /// The report rules: the test ID at the top, findings before measurements,
    /// skips that cannot be misread as passes, the actual run order stated,
    /// timestamp spread named, partial runs called weaker, fixes recorded, and
    /// the two forms carrying the same content.
    /// </summary>
    public class FixerReportTests
    {
        // ---- the test ID leads ----

        [Fact]
        public void The_test_id_is_at_the_top_of_the_plain_text_form()
        {
            var run = new FixerRun(Kettle(Answering("wet"), Answering("hot")));
            string text = FixerReport.PlainText(run);

            int idAt = text.IndexOf(run.RunId, StringComparison.Ordinal);
            Assert.True(idAt >= 0, "the report never states the test ID");
            // Before any stage content, not buried under it.
            int firstStage = text.IndexOf("Stage 0", StringComparison.Ordinal);
            Assert.True(idAt < firstStage, "the test ID appears after the stage detail");
        }

        // ---- comprehension from the first screen ----

        [Fact]
        public void Findings_come_before_stage_measurements()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");

            string text = FixerReport.PlainText(run);
            int findings = text.IndexOf("What was found", StringComparison.Ordinal);
            int evidence = text.IndexOf("Water level: none.", StringComparison.Ordinal);

            Assert.True(findings >= 0, "no findings section");
            Assert.True(evidence >= 0, "the evidence is missing entirely");
            Assert.True(findings < evidence, "measurements lead and findings trail");
        }

        [Fact]
        public void The_three_fix_owners_read_differently()
        {
            var set = Kettle(fill: _ => new FixerOutcome
            {
                Answer = "Trouble.",
                Findings = new[]
                {
                    new FixerFinding("a", FixOwner.Us, "A is wrong.", "Fix A", "fix-a"),
                    new FixerFinding("b", FixOwner.Operator, "B is wrong.", "Do the B thing."),
                    new FixerFinding("c", FixOwner.NobodyHere, "C is wrong.",
                                     "Nothing here can change C."),
                },
            });
            var run = new FixerRun(set);
            run.RunStage("fill");
            string text = FixerReport.PlainText(run);

            // THE INVARIANT: three owners, three grammars, because three
            // different next moves. A finding we can fix NAMES the fix; one the
            // operator must fix says what to do; one nobody here can fix says
            // so plainly.
            //
            // Asserted on "press \"Fix A\"" until 2026-08-25. That broke when the
            // wording stopped telling the reader to press anything — because
            // this report's OTHER reader is Flex support, who are not looking
            // at our page, and imperative instructions about our buttons read
            // as our UI leaking into evidence. The invariant survived; only the
            // grammar changed.
            //
            // Checks that the fix is NAMED rather than that it is commanded.
            Assert.Contains("Fix A", text);
            Assert.Contains("one-press fix", text);
            Assert.Contains("What to do: Do the B thing.", text);
            Assert.Contains("Nothing here can change C.", text);
            Assert.DoesNotContain("What to do: Nothing here can change C.", text);
        }

        // ---- skipped is not passed ----

        [Fact]
        public void A_skipped_stage_reads_as_not_run_with_its_reason()
        {
            var run = new FixerRun(Kettle());
            run.SkipStage("fill", "no-tap");
            string text = FixerReport.PlainText(run);

            Assert.Contains("Not run", text);
            Assert.Contains("There is no tap here.", text);
            Assert.Contains("left open", text);
        }

        [Fact]
        public void The_two_skip_reasons_produce_visibly_different_reports()
        {
            var a = new FixerRun(Kettle());
            a.SkipStage("fill", "no-tap");
            var b = new FixerRun(Kettle());
            b.SkipStage("fill", "later");

            string reportA = FixerReport.PlainText(a);
            string reportB = FixerReport.PlainText(b);

            // Each report carries its own effect text and not the other's.
            Assert.Contains("left open", reportA);
            Assert.DoesNotContain("left open", reportB);
            Assert.Contains("weaker", reportB);
        }

        // ---- the actual run order ----

        [Fact]
        public void The_report_states_the_order_things_were_actually_done_in()
        {
            var run = new FixerRun(Kettle(Answering("wet"), Answering("hot")));
            run.RunStage("boil");
            run.RunStage("fill");
            string text = FixerReport.PlainText(run);

            Match m = Regex.Match(text, @"done in this order: (.+)");
            Assert.True(m.Success, "the report never states the run order");
            string order = m.Groups[1].Value;
            int boilAt = order.IndexOf("Boil", StringComparison.Ordinal);
            int fillAt = order.IndexOf("Fill", StringComparison.Ordinal);
            Assert.True(boilAt >= 0 && fillAt >= 0 && boilAt < fillAt,
                "the stated order is the listed order, not the actual one: " + order);
        }

        // ---- timestamp spread ----

        [Fact]
        public void Results_far_apart_in_time_are_named_as_such()
        {
            // Two results, one clock step beyond the threshold apart. The
            // threshold itself comes from the code — asserting a literal here
            // would pin the example, not the property.
            var step = TimeSpan.FromMinutes(FixerReport.SpreadWorthNamingMinutes + 1);
            var run = new FixerRun(Kettle(Answering("wet"), Answering("hot")), Clock(step));
            run.RunStage("fill");
            run.RunStage("boil");

            Assert.Contains("span", FixerReport.PlainText(run));
        }

        [Fact]
        public void Results_close_together_are_not_flagged()
        {
            var run = new FixerRun(Kettle(Answering("wet"), Answering("hot")),
                                   Clock(TimeSpan.FromSeconds(30)));
            run.RunStage("fill");
            run.RunStage("boil");

            Assert.DoesNotContain("span", FixerReport.PlainText(run));
        }

        // ---- partial runs ----

        [Fact]
        public void A_partial_run_names_the_stages_not_done_and_calls_the_answer_weaker()
        {
            var run = new FixerRun(Kettle(Answering("wet"), Answering("hot")));
            run.RunStage("fill");
            string text = FixerReport.PlainText(run);

            Assert.Contains("Not attempted at all", text);
            Assert.Contains("Boil", text);
            // "weaken" matches both "weaker" and "weakens", so a rewrite
            // between the two does not break the guard. The invariant is that
            // a partial run SAYS its answer is worth less, which is the whole
            // point of listing what was skipped.
            Assert.Contains("weaken", text);
        }

        [Fact]
        public void An_unattempted_stage_still_appears_in_the_stage_detail()
        {
            // A gap in a numbered set reads as an omission; every stage gets
            // its section whether or not anything happened.
            var run = new FixerRun(Kettle());
            string text = FixerReport.PlainText(run);

            Assert.Contains("Stage 0: Fill", text);
            Assert.Contains("Stage 1: Boil", text);
            Assert.Contains("has not been run", text);
        }

        // ---- re-runs ----

        [Fact]
        public void A_rerun_stage_says_it_was_rerun_and_shows_only_the_latest_result()
        {
            int calls = 0;
            var run = new FixerRun(Kettle(
                fill: _ => new FixerOutcome { Answer = "attempt " + (++calls) }));
            run.RunStage("fill");
            run.RunStage("fill");
            string text = FixerReport.PlainText(run);

            Assert.Contains("re-run", text);
            Assert.Contains("attempt 2", text);
            Assert.DoesNotContain("attempt 1", text);
        }

        // ---- fixes applied ----

        [Fact]
        public void An_applied_fix_is_reported_with_before_after_when_and_what_ran_after()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set, Clock(TimeSpan.FromMinutes(1)));
            run.RunStage("fill");
            FixerFixRecord fix = run.ApplyFix("fill", "dry");
            run.RunStage("boil");

            string text = FixerReport.PlainText(run);
            Assert.Contains("Changes made during this run", text);
            Assert.Contains("The kettle is dry.", text);
            Assert.Contains("water flowing", text);
            Assert.Contains(fix.AtUtc.ToString("HH:mm"), text);
            Assert.Contains("recorded after this change", text);
            Assert.Contains("Boil", text.Substring(text.IndexOf("recorded after this change",
                                                                StringComparison.Ordinal)));
        }

        [Fact]
        public void A_failed_fix_is_reported_as_failed_not_omitted()
        {
            FixerStageSet set = KettleWithDryFinding(out _, bindAction: false);
            var run = new FixerRun(set);
            run.RunStage("fill");
            run.ApplyFix("fill", "dry");

            Assert.Contains("DID NOT succeed", FixerReport.PlainText(run));
        }

        [Fact]
        public void A_finding_fixed_during_the_run_is_marked_fixed_in_the_summary()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");
            run.ApplyFix("fill", "dry");

            string text = FixerReport.PlainText(run);
            Assert.Contains("FIXED during this run", text);
        }

        // ---- the two forms ----

        [Fact]
        public void The_plain_text_form_contains_no_markup()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");
            run.SkipStage("boil", "later");

            Assert.DoesNotMatch(new Regex("<[a-zA-Z/]"), FixerReport.PlainText(run));
        }

        [Fact]
        public void Both_forms_carry_the_same_id_answers_and_findings()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");

            string text = FixerReport.PlainText(run);
            string html = FixerReport.HtmlFragment(run);

            foreach (string mustCarry in new[]
                     { run.RunId, "No — the kettle is dry.", "The kettle is dry.",
                       "Water level: none." })
            {
                Assert.Contains(mustCarry, text);
                Assert.Contains(mustCarry, html);
            }
        }

        [Fact]
        public void Angle_brackets_in_measurements_survive_the_html_form_escaped()
        {
            var set = Kettle(fill: _ => new FixerOutcome
            {
                Answer = "Device <USB & Things> responded.",
            });
            var run = new FixerRun(set);
            run.RunStage("fill");

            string html = FixerReport.HtmlFragment(run);
            Assert.Contains("&lt;USB &amp; Things&gt;", html);
            Assert.DoesNotContain("<USB", html);
        }
    }
}
