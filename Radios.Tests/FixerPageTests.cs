using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Radios.Fixer;
using Xunit;
using static Radios.Tests.FixerTestKit;

namespace Radios.Tests
{
    /// <summary>
    /// The rendered markup, inspected statically — heading hierarchy, tablist
    /// semantics, tab-stop accounting, and the wire the buttons speak.
    /// </summary>
    /// <remarks>
    /// The page exists FOR browse mode: H between stages, B between buttons,
    /// prose that costs nothing. Every test here guards a way that promise
    /// quietly breaks — a skipped heading level, a clickable div, a tabindex
    /// on a paragraph, a result rendered away from its stage.
    /// </remarks>
    public class FixerPageTests
    {
        private static FixerRun SimpleRun()
            => new FixerRun(Kettle(Answering("Yes — wet."), Answering("Yes — it boils.")));

        // -------- helpers that read the markup --------

        private static List<int> HeadingLevels(string html)
            => Regex.Matches(html, @"<h([1-6])[\s>]").Select(m => int.Parse(m.Groups[1].Value))
                    .ToList();

        /// <summary>The panel section for one stage, by id.</summary>
        private static string PanelOf(string html, string stageId)
        {
            var m = Regex.Match(html,
                "<section role=\"tabpanel\" id=\"panel-" + Regex.Escape(stageId)
                + "\".*?</section>", RegexOptions.Singleline);
            Assert.True(m.Success, "no panel for stage " + stageId);
            return m.Value;
        }

        private static string ReportRegion(string html)
        {
            var m = Regex.Match(html, "<div id=\"report\">.*?</div>", RegexOptions.Singleline);
            Assert.True(m.Success, "no report region");
            return m.Value;
        }

        // -------- heading hierarchy --------

        [Fact]
        public void One_h1_and_no_skipped_heading_levels()
        {
            List<int> levels = HeadingLevels(FixerPage.Render(SimpleRun()));

            Assert.Equal(1, levels.Count(l => l == 1));
            Assert.Equal(1, levels[0]);
            for (int i = 1; i < levels.Count; i++)
                Assert.True(levels[i] <= levels[i - 1] + 1,
                    "heading level jumps from h" + levels[i - 1] + " to h" + levels[i]
                    + " at position " + i);
        }

        [Fact]
        public void The_report_gives_every_stage_a_heading_for_H_navigation()
        {
            // H between stages is the whole argument for the web surface, and
            // the report is the continuous document it happens in.
            string report = ReportRegion(FixerPage.Render(SimpleRun()));
            Assert.Contains("<h3>Stage 0: Fill</h3>", report);
            Assert.Contains("<h3>Stage 1: Boil</h3>", report);
        }

        // -------- tablist semantics --------

        [Fact]
        public void The_tablist_is_real_aria()
        {
            var run = SimpleRun();
            string html = FixerPage.Render(run);

            Assert.Contains("role=\"tablist\" aria-label=", html);

            var tabs = Regex.Matches(html, "<button[^>]*role=\"tab\"[^>]*>")
                            .Select(m => m.Value).ToList();
            Assert.Equal(run.Set.Stages.Count, tabs.Count);

            // Roving tabindex: exactly one tab reachable by Tab, the rest by
            // arrows. And exactly one is selected.
            Assert.Equal(1, tabs.Count(t => t.Contains("tabindex=\"0\"")));
            Assert.Equal(1, tabs.Count(t => t.Contains("aria-selected=\"true\"")));
            Assert.All(tabs, t => Assert.Matches("tabindex=\"(0|-1)\"", t));

            // Every tab controls a real panel that points back at it.
            foreach (Match m in Regex.Matches(html, "aria-controls=\"(panel-[^\"]+)\""))
            {
                string panelId = m.Groups[1].Value;
                Assert.Contains("<section role=\"tabpanel\" id=\"" + panelId + "\"", html);
            }
            foreach (Match m in Regex.Matches(html,
                "<section role=\"tabpanel\" id=\"panel-([^\"]+)\"[^>]*aria-labelledby=\"(tab-[^\"]+)\""))
            {
                Assert.Equal("tab-" + m.Groups[1].Value, m.Groups[2].Value);
            }
        }

        [Fact]
        public void Unselected_panels_are_hidden_and_the_selected_one_is_not()
        {
            string html = FixerPage.Render(SimpleRun(), "boil");

            Assert.Matches("<section role=\"tabpanel\" id=\"panel-fill\"[^>]*hidden", html);
            Assert.DoesNotMatch("<section role=\"tabpanel\" id=\"panel-boil\"[^>]*hidden", html);
        }

        [Fact]
        public void An_unknown_selection_falls_back_to_the_first_stage()
        {
            // The default path starts at the beginning on purpose.
            string html = FixerPage.Render(SimpleRun(), "no-such-stage");
            Assert.DoesNotMatch("<section role=\"tabpanel\" id=\"panel-fill\"[^>]*hidden", html);
        }

        // -------- controls are real controls --------

        [Fact]
        public void Every_button_is_a_button_element_and_nothing_is_a_fake_one()
        {
            string html = FixerPage.Render(SimpleRun());

            Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role=\"button\"", html);
            foreach (Match m in Regex.Matches(html, "<button[^>]*>"))
                Assert.Contains("type=\"button\"", m.Value);
        }

        [Fact]
        public void Every_input_has_an_associated_label()
        {
            string html = FixerPage.Render(SimpleRun());
            foreach (Match m in Regex.Matches(html, "<input[^>]*id=\"([^\"]+)\""))
                Assert.Contains("<label for=\"" + m.Groups[1].Value + "\">", html);
        }

        [Fact]
        public void Every_aria_describedby_points_at_an_element_that_exists()
        {
            string html = FixerPage.Render(SimpleRun());
            foreach (Match m in Regex.Matches(html, "aria-describedby=\"([^\"]+)\""))
            foreach (string id in m.Groups[1].Value.Split(' '))
                Assert.Contains("id=\"" + id + "\"", html);
        }

        // -------- tab-stop accounting --------

        [Fact]
        public void No_tabindex_appears_outside_the_tablist()
        {
            // Prose must never be a tab stop, and nothing but the roving
            // tablist has any business setting tabindex at all.
            string html = FixerPage.Render(SimpleRun());
            foreach (Match m in Regex.Matches(html, "<([a-z0-9]+)[^>]*tabindex[^>]*>"))
                Assert.Contains("role=\"tab\"", m.Value);
        }

        [Fact]
        public void The_report_region_costs_zero_tab_stops()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");
            run.SkipStage("boil", "later");

            string report = ReportRegion(FixerPage.Render(run));
            Assert.DoesNotContain("<button", report);
            Assert.DoesNotContain("<a ", report);
            Assert.DoesNotContain("<input", report);
            Assert.DoesNotContain("tabindex", report);
        }

        [Fact]
        public void Read_only_prose_carries_no_tabindex_anywhere()
        {
            string html = FixerPage.Render(SimpleRun());
            Assert.DoesNotMatch(new Regex("<(p|li|ul|h[1-6]|pre|span|details|summary)[^>]*tabindex"),
                                html);
        }

        // -------- results live where their stage is --------

        [Fact]
        public void A_stage_result_renders_inside_that_stages_panel()
        {
            var run = SimpleRun();
            run.RunStage("fill");

            string html = FixerPage.Render(run);
            Assert.Contains("Yes — wet.", PanelOf(html, "fill"));
            Assert.DoesNotContain("Yes — wet.", PanelOf(html, "boil"));
        }

        [Fact]
        public void A_skipped_stage_reads_as_not_run_in_its_panel()
        {
            var run = SimpleRun();
            run.SkipStage("fill", "no-tap");

            string panel = PanelOf(FixerPage.Render(run), "fill");
            Assert.Contains("Not run", panel);
            Assert.Contains("There is no tap here.", panel);
        }

        [Fact]
        public void Findings_render_at_the_point_of_detection_with_the_fix_as_a_button()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");

            string panel = PanelOf(FixerPage.Render(run), "fill");
            Assert.Contains("The kettle is dry.", panel);

            // The button carries the fix, is described by the finding, and
            // sends the FINDING id on the wire.
            var button = Regex.Match(panel, "<button[^>]*data-action=\"fix\"[^>]*>");
            Assert.True(button.Success, "no fix button in the panel");
            Assert.Contains("data-fix=\"dry\"", button.Value);
            Assert.Contains("data-stage=\"fill\"", button.Value);
            Assert.Contains("aria-describedby=\"find-fill-dry\"", button.Value);
        }

        [Fact]
        public void Operator_and_nobody_findings_render_as_prose_not_buttons()
        {
            var set = Kettle(fill: _ => new FixerOutcome
            {
                Answer = "Trouble.",
                Findings = new[]
                {
                    new FixerFinding("b", FixOwner.Operator, "B is wrong.", "Do the B thing."),
                    new FixerFinding("c", FixOwner.NobodyHere, "C is wrong.",
                                     "Nothing here can change C."),
                },
            });
            var run = new FixerRun(set);
            run.RunStage("fill");

            string panel = PanelOf(FixerPage.Render(run), "fill");
            Assert.Contains("What to do: Do the B thing.", panel);
            Assert.Contains("Nothing here can change C.", panel);
            Assert.DoesNotContain("data-action=\"fix\"", panel);
        }

        // -------- stop, status, critical --------

        [Fact]
        public void The_stop_button_comes_before_everything_it_might_need_to_stop()
        {
            string html = FixerPage.Render(SimpleRun());
            int stop = html.IndexOf("data-action=\"stop\"", StringComparison.Ordinal);
            int tablist = html.IndexOf("role=\"tablist\"", StringComparison.Ordinal);
            Assert.True(stop >= 0 && tablist > stop, "Stop is not ahead of the stages");
        }

        [Fact]
        public void A_critical_finding_lands_in_the_assertive_live_region()
        {
            FixerStageSet set = KettleWithDryFinding(out _, critical: true);
            var run = new FixerRun(set);
            run.RunStage("fill");

            string html = FixerPage.Render(run);
            var region = Regex.Match(html,
                "<p aria-live=\"assertive\" id=\"critical-warning\">([^<]*)</p>");
            Assert.True(region.Success);
            Assert.Contains("The kettle is dry.", region.Groups[1].Value);
        }

        [Fact]
        public void Without_a_critical_finding_the_assertive_region_is_present_but_empty()
        {
            string html = FixerPage.Render(SimpleRun());
            Assert.Contains("<p aria-live=\"assertive\" id=\"critical-warning\"></p>", html);
        }

        [Fact]
        public void A_polite_status_line_exists_for_progress()
        {
            Assert.Contains("aria-live=\"polite\" id=\"status-line\"", FixerPage.Render(SimpleRun()));
        }

        // -------- disclosure, help, guidance --------

        [Fact]
        public void Long_explanations_sit_behind_a_disclosure()
        {
            string panel = PanelOf(FixerPage.Render(SimpleRun()), "fill");
            Assert.Contains("<details>", panel);
            Assert.Contains("<summary>", panel);
            Assert.Contains("Boiling an empty kettle proves nothing about the tea.", panel);
        }

        [Fact]
        public void Help_is_a_link_at_the_stage_with_its_topic_on_the_wire()
        {
            string panel = PanelOf(FixerPage.Render(SimpleRun()), "boil");
            Assert.Contains("href=\"jjflex-help:kettle/boil\"", panel);
            Assert.Contains("data-topic=\"kettle/boil\"", panel);
        }

        [Fact]
        public void A_transmitting_stage_says_so_next_to_its_run_control()
        {
            string boil = PanelOf(FixerPage.Render(SimpleRun()), "boil");
            string fill = PanelOf(FixerPage.Render(SimpleRun()), "fill");

            Assert.Contains("This check transmits.", boil);
            Assert.DoesNotContain("This check transmits.", fill);

            // And the run button is described by that warning, so a screen
            // reader hears it on the control itself.
            var runButton = Regex.Match(boil, "<button[^>]*data-action=\"run\"[^>]*>");
            Assert.True(runButton.Success);
            Assert.Contains("tx-note-boil", runButton.Value);
        }

        [Fact]
        public void First_and_last_stages_have_no_dangling_prev_next()
        {
            string html = FixerPage.Render(SimpleRun());
            Assert.DoesNotContain("Back to", PanelOf(html, "fill"));
            Assert.Contains("On to Stage 1: Boil", PanelOf(html, "fill"));
            Assert.Contains("Back to Stage 0: Fill", PanelOf(html, "boil"));
            Assert.DoesNotContain("On to", PanelOf(html, "boil"));
        }

        // -------- run declarations --------

        [Fact]
        public void The_run_declaration_is_asked_with_real_radios_and_its_own_send()
        {
            string html = FixerPage.Render(SimpleRun());

            Assert.Contains("What is the kettle plugged into right now?", html);
            Assert.Contains("name=\"decl-power-source\"", html);
            Assert.Contains("<label for=\"decl-power-source-mains\">The mains</label>", html);

            // Its own message, never bundled into a stage request.
            var button = Regex.Match(html, "<button[^>]*data-action=\"declare\"[^>]*>");
            Assert.True(button.Success);
            Assert.Contains("data-kind=\"declare-load\"", button.Value);
        }

        [Fact]
        public void Declaration_radios_are_never_prechecked_even_after_an_answer()
        {
            // A new render must never look like a remembered fact — the
            // station may have been re-cabled, and a person states it afresh.
            var state = new FixerPageState
            {
                DeclarationAnswers = new Dictionary<string, string>
                    { ["power-source"] = "The mains" },
            };
            string html = FixerPage.Render(SimpleRun(), state);

            Assert.DoesNotMatch(new Regex("<input[^>]*\\schecked"), html);
            Assert.Contains("You said: The mains", html);
        }

        [Fact]
        public void The_answer_carried_on_the_wire_is_the_choices_own_words()
        {
            // declare-load's "what" goes into the gate's record and the
            // report; a choice id like "mains" is not an answer a person gave.
            string html = FixerPage.Render(SimpleRun());
            Assert.Contains("data-what=\"The mains\"", html);
            Assert.Contains("data-what=\"A generator\"", html);
        }

        // -------- host notices and run-versus-run-again --------

        [Fact]
        public void A_host_notice_renders_in_its_stages_panel_without_being_a_result()
        {
            var state = new FixerPageState
            {
                StageNotices = new Dictionary<string, string>
                {
                    ["boil"] = "Nothing was transmitted, because you have not said yet what "
                             + "the antenna socket is connected to.",
                },
            };
            var run = SimpleRun();
            string panel = PanelOf(FixerPage.Render(run, state), "boil");

            Assert.Contains("Nothing was transmitted", panel);
            // Nothing ran: the stage still reads as unchecked, and the engine
            // holds no record.
            Assert.Contains("Not checked yet.", panel);
            Assert.Null(run.ResultFor("boil"));
        }

        [Fact]
        public void Every_panel_has_a_notice_slot_for_the_receive_channel()
        {
            string html = FixerPage.Render(SimpleRun());
            Assert.Contains("id=\"notice-fill\"", html);
            Assert.Contains("id=\"notice-boil\"", html);
        }

        [Fact]
        public void Running_again_is_a_distinct_deliberate_control()
        {
            var run = SimpleRun();
            string before = PanelOf(FixerPage.Render(run), "boil");
            Assert.Contains("data-action=\"run\"", before);
            Assert.DoesNotContain("data-action=\"rerun\"", before);

            run.RunStage("boil");
            string after = PanelOf(FixerPage.Render(run), "boil");
            Assert.Contains("data-action=\"rerun\"", after);
            Assert.Contains("Run this check again", after);
            Assert.DoesNotContain("data-action=\"run\"", after);
        }

        [Fact]
        public void A_skipped_stage_still_offers_a_first_run_not_a_repeat()
        {
            var run = SimpleRun();
            run.SkipStage("boil", "later");
            string panel = PanelOf(FixerPage.Render(run), "boil");
            Assert.Contains("data-action=\"run\"", panel);
            Assert.DoesNotContain("data-action=\"rerun\"", panel);
        }

        [Fact]
        public void A_host_action_is_a_hand_off_button_not_a_picker()
        {
            string panel = PanelOf(FixerPage.Render(SimpleRun()), "fill");
            var button = Regex.Match(panel, "<button[^>]*data-action=\"host\"[^>]*>");
            Assert.True(button.Success);
            Assert.Contains("data-kind=\"open-device-picker\"", button.Value);
        }

        // -------- the wire the script speaks --------

        [Fact]
        public void The_script_speaks_only_wire_kinds_the_host_parses()
        {
            // The exact strings are the page-host contract, same standing as
            // the REFPWR meter name: change either side alone and the halves
            // no longer meet.
            string html = FixerPage.Render(SimpleRun());
            foreach (string kind in new[]
                     { "'ready'", "'run-stage'", "'skip-stage'", "'apply-fix'",
                       "'stop'", "'copy-report'", "'open-help'" })
                Assert.Contains("kind: " + kind, html);

            // The deliberate repeat is a real JSON true, not a string.
            Assert.Contains("again: true", html);

            // And everything goes out as a JSON string.
            Assert.Contains("postMessage(JSON.stringify", html);
        }

        [Fact]
        public void The_page_defines_the_receive_channel()
        {
            string html = FixerPage.Render(SimpleRun());
            Assert.Contains("window.jjflex.receive", html);
            foreach (string kind in new[] { "'notice'", "'critical'", "'declared'", "'status'" })
                Assert.Contains(kind, html);
        }

        [Fact]
        public void The_run_id_is_on_the_page_and_on_the_wire_root()
        {
            var run = SimpleRun();
            string html = FixerPage.Render(run);
            Assert.Contains("<main data-run=\"" + run.RunId + "\">", html);
            Assert.Contains("<strong>" + run.RunId + "</strong>", html);
        }

        // -------- content is escaped --------

        [Fact]
        public void Hostile_text_in_answers_cannot_become_markup()
        {
            var run = new FixerRun(Kettle(
                fill: Answering("<script>alert('hi')</script> & so on")));
            run.RunStage("fill");

            string html = FixerPage.Render(run);
            Assert.DoesNotContain("<script>alert", html);
            Assert.Contains("&lt;script&gt;", html);
        }
    }
}
