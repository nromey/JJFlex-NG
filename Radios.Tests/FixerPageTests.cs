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
    /// The rendered markup, inspected statically — heading hierarchy, the
    /// one-document structure, tab-stop accounting, and the wire the buttons
    /// speak.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page exists FOR browse mode: H between stages, B between buttons,
    /// prose that costs nothing. Every test here guards a way that promise
    /// quietly breaks — a skipped heading level, a clickable div, a tabindex
    /// on a paragraph, a result rendered away from its stage.
    /// </para>
    /// <para>
    /// <b>Sprint 35: the tablist is gone.</b> The page is one document —
    /// every stage always present as a section whose h2 carries its status,
    /// three named landmarks, forward motion on completed stages, and no
    /// skip control once a measurement exists. The tests that used to pin
    /// tab semantics now pin the document's promises instead, each of which
    /// answers a finding from the first real operator session (#242, #248,
    /// #249, #250).
    /// </para>
    /// </remarks>
    public class FixerPageTests
    {
        private static FixerRun SimpleRun()
            => new FixerRun(Kettle(Answering("Yes — wet."), Answering("Yes — it boils.")));

        // -------- helpers that read the markup --------

        private static List<int> HeadingLevels(string html)
            => Regex.Matches(html, @"<h([1-6])[\s>]").Select(m => int.Parse(m.Groups[1].Value))
                    .ToList();

        /// <summary>The card section for one stage, by id.</summary>
        private static string CardOf(string html, string stageId)
        {
            var m = Regex.Match(html,
                "<section class=\"stage\" id=\"stage-" + Regex.Escape(stageId)
                + "\".*?</section>", RegexOptions.Singleline);
            Assert.True(m.Success, "no card for stage " + stageId);
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
            // The report's per-stage entries sit at h3, one level under the
            // Report h2 — which is what disambiguates them from the REAL
            // stages at h2. Level is a second navigation axis (#242).
            string report = ReportRegion(FixerPage.Render(SimpleRun()));
            Assert.Contains("<h3>Stage 0: Fill</h3>", report);
            Assert.Contains("<h3>Stage 1: Boil</h3>", report);
        }

        // -------- one document, no tablist --------

        [Fact]
        public void Every_stage_is_always_present_and_nothing_is_hidden()
        {
            // The old tab container carried hidden on four of five panels, so
            // the tool could not be READ before it was used. One document now:
            // every stage rendered, none hidden, whatever the current stage.
            string html = FixerPage.Render(SimpleRun(), "boil");

            Assert.Contains("<section class=\"stage\" id=\"stage-fill\"", html);
            Assert.Contains("<section class=\"stage\" id=\"stage-boil\"", html);
            Assert.DoesNotContain(" hidden", html);
            Assert.DoesNotContain("role=\"tablist\"", html);
            Assert.DoesNotContain("role=\"tab\"", html);
            Assert.DoesNotContain("role=\"tabpanel\"", html);
        }

        [Fact]
        public void The_stage_heading_carries_its_status()
        {
            // Walking the stages with H is also the progress readout — name
            // and state in one stop.
            var run = SimpleRun();
            string html = FixerPage.Render(run);
            Assert.Contains("Stage 0: Fill — not yet run", html);

            run.RunStage("fill");
            html = FixerPage.Render(run);
            Assert.Contains("Stage 0: Fill — passed", html);

            run.SkipStage("boil", "later");
            html = FixerPage.Render(run);
            Assert.Contains("Stage 1: Boil — skipped", html);
        }

        [Fact]
        public void A_stage_with_findings_reads_problems_found_in_its_heading()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");

            Assert.Contains("Stage 0: Fill — problems found", FixerPage.Render(run));
        }

        [Fact]
        public void The_page_has_exactly_four_landmarks_with_short_names()
        {
            // How to use the page, main ("Stages"), the declarations region,
            // the report region. NVDA speaks a landmark's name on every jump,
            // so the names stay short — never a sentence.
            string html = FixerPage.Render(SimpleRun());

            Assert.Single(Regex.Matches(html, "<main[\\s>]"));
            Assert.Contains("<main aria-label=\"Stages\">", html);
            Assert.Contains("<section class=\"howto\" aria-labelledby=\"howto-heading\">", html);
            Assert.Contains("<h2 id=\"howto-heading\" tabindex=\"-1\">Using this page</h2>", html);
            Assert.Contains("<section class=\"decl\" aria-labelledby=\"decl-heading\">", html);
            Assert.Contains("<h2 id=\"decl-heading\" tabindex=\"-1\">Declarations</h2>", html);
            Assert.Contains("aria-labelledby=\"report-heading\"", html);
            Assert.Contains("<h2 id=\"report-heading\" tabindex=\"-1\">Report</h2>", html);
        }

        [Fact]
        public void The_current_stage_is_marked_and_an_unknown_selection_falls_back_to_the_first()
        {
            string html = FixerPage.Render(SimpleRun(), "boil");
            Assert.Contains("id=\"stage-boil\" data-status=\"notrun\" data-current=\"true\"", html);

            html = FixerPage.Render(SimpleRun(), "no-such-stage");
            Assert.Contains("id=\"stage-fill\" data-status=\"notrun\" data-current=\"true\"", html);
        }

        [Fact]
        public void The_summary_line_reports_the_state_of_play()
        {
            // The job the tablist did badly, done as a sentence at the top —
            // including the transmit fact a blind operator cannot glance at.
            var run = SimpleRun();
            string html = FixerPage.Render(run);
            Assert.Contains("Two checks. Two not yet run. Nothing has keyed the radio.", html);

            run.RunStage("fill");
            html = FixerPage.Render(run, new FixerPageState { TransmitCount = 1 });
            Assert.Contains("Two checks. One passed, one not yet run. "
                          + "The radio has been keyed once this run.", html);
        }

        [Fact]
        public void The_page_says_how_to_drive_it()
        {
            // WCAG 3.3.2 — the one criterion the old page actually failed.
            string html = FixerPage.Render(SimpleRun());
            Assert.Contains("heading keys move between them", html);
            Assert.Contains("F6", html);
        }

        // -------- the how-to section (#378) --------

        [Fact]
        public void The_how_to_section_says_how_to_leave_not_just_how_to_move()
        {
            // Noel's question at the bench — "is the only way to exit then to
            // escape?" — showed the ways out were undiscoverable. Every route
            // an operator can take is named: Escape, closing the window, and
            // what the emergency control actually does.
            string html = FixerPage.Render(SimpleRun());
            Assert.Contains("press Escape or close the window", html);
            Assert.Contains("Stop everything, above, is the emergency control", html);
            Assert.Contains("the carrier drops first", html);
        }

        [Fact]
        public void The_how_to_disclosure_is_closed_by_default_and_the_host_can_open_it()
        {
            // The host opens it for a first-time operator (no saved runs on
            // this computer); a returning one gets it folded away. The
            // section and its promise-carrying summary are present either
            // way — collapsed must still say what it is hiding.
            string closedHtml = FixerPage.Render(SimpleRun());
            var closedDetails = Regex.Match(closedHtml,
                "<details data-stage=\"how-to-use\"[^>]*>");
            Assert.True(closedDetails.Success, "no how-to disclosure");
            Assert.DoesNotContain("open", closedDetails.Value);
            Assert.Contains("How to move through the checks, and how to leave",
                            closedHtml);

            string openHtml = FixerPage.Render(SimpleRun(),
                new FixerPageState { HowToOpenByDefault = true });
            Assert.Matches("<details data-stage=\"how-to-use\" open>", openHtml);
        }

        [Fact]
        public void The_operators_own_how_to_toggle_beats_the_default()
        {
            // The toggle rides the same explain wire and the same host-side
            // memory as the stage explanations, under the pseudo-stage key.
            var closedByOperator = new FixerPageState
            {
                HowToOpenByDefault = true,
                ExplanationOpen = new Dictionary<string, bool>
                    { [FixerPage.HowToUseKey] = false },
            };
            Assert.DoesNotMatch(new Regex("<details data-stage=\"how-to-use\" open>"),
                                FixerPage.Render(SimpleRun(), closedByOperator));

            var openedByOperator = new FixerPageState
            {
                ExplanationOpen = new Dictionary<string, bool>
                    { [FixerPage.HowToUseKey] = true },
            };
            Assert.Matches("<details data-stage=\"how-to-use\" open>",
                           FixerPage.Render(SimpleRun(), openedByOperator));
        }

        [Fact]
        public void The_leaving_bullet_promises_saving_only_while_something_is_saving()
        {
            // The saved wording is a promise; over a journal that never
            // opened it would be silent data loss with a reassuring voice.
            string saved = FixerPage.Render(SimpleRun(),
                new FixerPageState { RunIsSaved = true });
            Assert.Contains("Saved check runs, on the Fix menu", saved);

            string unsaved = FixerPage.Render(SimpleRun());
            Assert.DoesNotContain("Saved check runs", unsaved);
            Assert.Contains("you are asked before it is lost", unsaved);
        }

        // -------- the emergency control's name (#377) --------

        [Fact]
        public void The_emergency_control_is_called_stop_everything()
        {
            // Plain "Stop" read as "stop the test run" to the most informed
            // operator alive, on a calm evening — and this control aborts a
            // keyed transmit. The name must survive that misreading, and it
            // must not sound like the exit prompt's stop-and-resume choice.
            string html = FixerPage.Render(SimpleRun());
            var stop = Regex.Match(html,
                "<button[^>]*data-action=\"stop\"[^>]*>([^<]*)</button>");
            Assert.True(stop.Success, "no stop control");
            Assert.Equal("Stop everything", stop.Groups[1].Value);
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
        public void Tabindex_appears_only_as_minus_one_on_section_headings()
        {
            // tabindex="-1" makes a heading focusable BY SCRIPT — the focus
            // target after a stage completes, and F6's landing spots — without
            // ever entering the Tab order. A positive or zero tabindex
            // anywhere would put prose in the Tab ring; the only elements
            // allowed to carry the attribute at all are the h2 focus targets.
            string html = FixerPage.Render(SimpleRun());

            Assert.DoesNotContain("tabindex=\"0\"", html);
            foreach (Match m in Regex.Matches(html, "<([a-z0-9]+)[^>]*tabindex[^>]*>"))
            {
                Assert.Equal("h2", m.Groups[1].Value);
                Assert.Contains("tabindex=\"-1\"", m.Value);
            }
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
            Assert.DoesNotMatch(new Regex("<(p|li|ul|h1|h3|pre|span|details|summary)[^>]*tabindex"),
                                html);
        }

        // -------- results live where their stage is --------

        [Fact]
        public void A_stage_result_renders_inside_that_stages_card()
        {
            var run = SimpleRun();
            run.RunStage("fill");

            string html = FixerPage.Render(run);
            Assert.Contains("Yes — wet.", CardOf(html, "fill"));
            Assert.DoesNotContain("Yes — wet.", CardOf(html, "boil"));
        }

        [Fact]
        public void A_skipped_stage_reads_as_not_run_in_its_card()
        {
            var run = SimpleRun();
            run.SkipStage("fill", "no-tap");

            string card = CardOf(FixerPage.Render(run), "fill");
            Assert.Contains("Not run", card);
            Assert.Contains("There is no tap here.", card);
        }

        [Fact]
        public void Findings_render_under_their_own_heading_with_the_fix_as_a_button()
        {
            FixerStageSet set = KettleWithDryFinding(out _);
            var run = new FixerRun(set);
            run.RunStage("fill");

            string card = CardOf(FixerPage.Render(run), "fill");
            Assert.Contains("<h3>Findings</h3>", card);
            Assert.Contains("The kettle is dry.", card);

            // The button carries the fix, is described by the finding, and
            // sends the FINDING id on the wire.
            var button = Regex.Match(card, "<button[^>]*data-action=\"fix\"[^>]*>");
            Assert.True(button.Success, "no fix button in the card");
            Assert.Contains("data-fix=\"dry\"", button.Value);
            Assert.Contains("data-stage=\"fill\"", button.Value);
            Assert.Contains("aria-describedby=\"find-fill-dry\"", button.Value);
        }

        [Fact]
        public void A_stage_without_findings_has_no_findings_heading()
        {
            var run = SimpleRun();
            run.RunStage("fill");
            Assert.DoesNotContain("<h3>Findings</h3>", CardOf(FixerPage.Render(run), "fill"));
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

            string card = CardOf(FixerPage.Render(run), "fill");
            Assert.Contains("What to do: Do the B thing.", card);
            Assert.Contains("Nothing here can change C.", card);
            Assert.DoesNotContain("data-action=\"fix\"", card);
        }

        // -------- stop, status, critical --------

        [Fact]
        public void The_stop_button_comes_before_everything_it_might_need_to_stop()
        {
            string html = FixerPage.Render(SimpleRun());
            int stop = html.IndexOf("data-action=\"stop\"", StringComparison.Ordinal);
            int firstStage = html.IndexOf("<section class=\"stage\"", StringComparison.Ordinal);
            Assert.True(stop >= 0 && firstStage > stop, "Stop is not ahead of the stages");
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
            string card = CardOf(FixerPage.Render(SimpleRun()), "fill");
            Assert.Contains("<details", card);
            Assert.Contains("<summary>", card);
            Assert.Contains("Boiling an empty kettle proves nothing about the tea.", card);
        }

        [Fact]
        public void The_current_unrun_stages_explanation_opens_and_the_operators_toggle_wins()
        {
            // A first-time operator gets the explanation without hunting; a
            // returning one collapses it and the choice survives re-renders,
            // because the page posts the toggle and the host hands it back.
            var run = SimpleRun();

            string current = CardOf(FixerPage.Render(run, "fill"), "fill");
            Assert.Contains(" open>", current);

            string other = CardOf(FixerPage.Render(run, "fill"), "boil");
            Assert.DoesNotContain(" open>", other);

            var closedByOperator = new FixerPageState
            {
                SelectedStageId = "fill",
                ExplanationOpen = new Dictionary<string, bool> { ["fill"] = false },
            };
            Assert.DoesNotContain(" open>",
                CardOf(FixerPage.Render(run, closedByOperator), "fill"));

            // And once the stage has run, the default is closed.
            run.RunStage("fill");
            Assert.DoesNotContain(" open>", CardOf(FixerPage.Render(run, "fill"), "fill"));
        }

        [Fact]
        public void Help_is_a_link_at_the_stage_with_its_topic_on_the_wire()
        {
            string card = CardOf(FixerPage.Render(SimpleRun()), "boil");
            Assert.Contains("href=\"jjflex-help:kettle/boil\"", card);
            Assert.Contains("data-topic=\"kettle/boil\"", card);
        }

        [Fact]
        public void A_transmitting_stage_says_so_next_to_its_run_control()
        {
            string boil = CardOf(FixerPage.Render(SimpleRun()), "boil");
            string fill = CardOf(FixerPage.Render(SimpleRun()), "fill");

            Assert.Contains("This check transmits.", boil);
            Assert.DoesNotContain("This check transmits.", fill);

            // And the run button is described by that warning, so a screen
            // reader hears it on the control itself.
            var runButton = Regex.Match(boil, "<button[^>]*data-action=\"run\"[^>]*>");
            Assert.True(runButton.Success);
            Assert.Contains("tx-note-boil", runButton.Value);
        }

        [Fact]
        public void What_run_will_do_is_stated_beside_the_control_and_describes_it()
        {
            // #250: the tool read tune power and the antenna port and showed
            // neither. The stage set supplies the sentence, evaluated at
            // render time so it carries live facts.
            var set = Kettle(Answering("wet"), Answering("hot"));
            set.Stages[1].DescribeRunAction =
                () => "This will heat one litre for two minutes.";

            string card = CardOf(FixerPage.Render(new FixerRun(set)), "boil");
            Assert.Contains("This will heat one litre for two minutes.", card);

            var runButton = Regex.Match(card, "<button[^>]*data-action=\"run\"[^>]*>");
            Assert.True(runButton.Success);
            Assert.Contains("runwill-boil", runButton.Value);
        }

        // -------- forward motion (#248) --------

        [Fact]
        public void A_completed_stage_offers_forward_motion_to_the_next_stages_heading()
        {
            var run = SimpleRun();
            run.RunStage("fill");

            string card = CardOf(FixerPage.Render(run), "fill");
            var next = Regex.Match(card, "<button[^>]*data-action=\"next\"[^>]*>([^<]*)</button>");
            Assert.True(next.Success, "no forward control on a completed stage");
            Assert.Contains("data-arg=\"stage-h-boil\"", next.Value);
            Assert.Equal("Next: Stage 1: Boil", next.Groups[1].Value);
        }

        [Fact]
        public void The_last_completed_stage_forwards_to_the_report()
        {
            var run = SimpleRun();
            run.RunStage("boil");

            string card = CardOf(FixerPage.Render(run), "boil");
            var next = Regex.Match(card, "<button[^>]*data-action=\"next\"[^>]*>([^<]*)</button>");
            Assert.True(next.Success, "the page dead-ends at the last stage");
            Assert.Contains("data-arg=\"report-heading\"", next.Value);
            Assert.Equal("Go to the report", next.Groups[1].Value);
        }

        [Fact]
        public void An_unrun_stage_offers_run_not_next()
        {
            string card = CardOf(FixerPage.Render(SimpleRun()), "fill");
            Assert.Contains("data-action=\"run\"", card);
            Assert.DoesNotContain("data-action=\"next\"", card);
        }

        // -------- focus landings and the spoken verdict (#373) --------

        [Fact]
        public void The_landing_ids_the_host_uses_exist_in_the_markup()
        {
            // The host focuses these by id after an action; an id that
            // renders under a different name silently reintroduces the
            // land-at-the-top defect this replaced. Run controls always
            // render; the next control needs a result; the fix button needs
            // a fixable finding.
            var run = new FixerRun(KettleWithDryFinding(out _));
            run.RunStage("fill");
            string html = FixerPage.Render(run);

            Assert.Contains("id=\"" + FixerPage.RunControlId("fill") + "\"", html);
            Assert.Contains("id=\"" + FixerPage.RunControlId("boil") + "\"", html);
            Assert.Contains("id=\"" + FixerPage.NextControlId("fill") + "\"", html);
            Assert.Contains("id=\"" + FixerPage.FixControlId("fill", "dry") + "\"", html);
        }

        [Fact]
        public void The_run_control_keeps_its_id_when_it_becomes_run_again()
        {
            // Same id for run and run-again — only one renders, and the
            // host's landing (a fix was applied; running again is the next
            // action) must find it in either state.
            var run = SimpleRun();
            run.RunStage("fill");
            string card = CardOf(FixerPage.Render(run), "fill");
            Assert.Contains("id=\"" + FixerPage.RunControlId("fill") + "\"", card);
            Assert.Contains("data-action=\"rerun\"", card);
        }

        [Fact]
        public void A_clean_pass_lands_on_the_forward_control()
        {
            var run = SimpleRun();
            run.RunStage("fill");
            Assert.Equal(FixerPage.NextControlId("fill"),
                         FixerPage.LandingAfterResult(run, "fill"));
        }

        [Fact]
        public void A_skip_lands_on_the_forward_control()
        {
            // A skip is a deliberate decision to move on; the landing agrees
            // with it, and the cost travels in the spoken verdict instead.
            var run = SimpleRun();
            run.SkipStage("fill", "no-tap");
            Assert.Equal(FixerPage.NextControlId("fill"),
                         FixerPage.LandingAfterResult(run, "fill"));
        }

        [Fact]
        public void A_fixable_finding_lands_on_its_own_fix_button()
        {
            // Applying the fix at the point of detection is the next action,
            // and the button's description reads the finding to whoever
            // lands on it.
            var run = new FixerRun(KettleWithDryFinding(out _));
            run.RunStage("fill");
            Assert.Equal(FixerPage.FixControlId("fill", "dry"),
                         FixerPage.LandingAfterResult(run, "fill"));
        }

        [Fact]
        public void Findings_nobody_here_can_fix_land_on_the_forward_control()
        {
            var set = Kettle(fill: _ => new FixerOutcome
            {
                Answer = "Trouble.",
                Findings = new[]
                {
                    new FixerFinding("b", FixOwner.Operator, "B is wrong.", "Do the B thing."),
                },
            });
            var run = new FixerRun(set);
            run.RunStage("fill");
            Assert.Equal(FixerPage.NextControlId("fill"),
                         FixerPage.LandingAfterResult(run, "fill"));
        }

        [Fact]
        public void A_stage_with_no_result_has_no_landing()
        {
            Assert.Equal("", FixerPage.LandingAfterResult(SimpleRun(), "fill"));
        }

        [Fact]
        public void The_spoken_verdict_is_the_headings_words_plus_the_answer()
        {
            // Composed from the same StatusOf and StatusPhrase the heading
            // uses, so what is spoken and what the heading says cannot
            // disagree — and the answer rides along, because it is the whole
            // product of running the stage.
            var run = SimpleRun();
            run.RunStage("fill");
            Assert.Equal("Stage 0: Fill — passed. Yes — wet.",
                         FixerPage.SpokenVerdict(run, "fill"));
        }

        [Fact]
        public void The_spoken_verdict_for_a_skip_carries_the_cost()
        {
            // The skip answer names the reason and what it costs the rest of
            // the run — the sentence Sprint 39 kept focus back for. It is
            // spoken now, so focus is free to go forward.
            var run = SimpleRun();
            run.SkipStage("fill", "no-tap");
            Assert.Equal("Stage 0: Fill — skipped. Not run. The reason given: "
                       + "\"There is no tap here.\" With no tap, whether the kettle "
                       + "holds water is left open.",
                         FixerPage.SpokenVerdict(run, "fill"));
        }

        [Fact]
        public void The_spoken_verdict_names_problems_found()
        {
            var run = new FixerRun(KettleWithDryFinding(out _));
            run.RunStage("fill");
            Assert.StartsWith("Stage 0: Fill — problems found.",
                              FixerPage.SpokenVerdict(run, "fill"));
        }

        [Fact]
        public void An_unrun_stage_has_no_verdict_to_speak()
        {
            Assert.Equal("", FixerPage.SpokenVerdict(SimpleRun(), "fill"));
        }

        // -------- skip is never offered over a measurement (#249) --------

        [Fact]
        public void Skip_is_offered_only_while_a_stage_has_no_result()
        {
            var run = SimpleRun();
            string before = CardOf(FixerPage.Render(run), "fill");
            Assert.Contains("data-action=\"skip\"", before);
            Assert.Contains("Why are you skipping this stage?", before);

            run.RunStage("fill");
            string after = CardOf(FixerPage.Render(run), "fill");
            Assert.DoesNotContain("data-action=\"skip\"", after);
            Assert.DoesNotContain("Why are you skipping", after);
        }

        [Fact]
        public void Skip_sits_after_the_run_control_never_before_it()
        {
            // The loudest affordance after a result used to be "why are you
            // giving up" — on a stage that had just PASSED. Skip is a rare,
            // legitimate choice and renders below the primary action.
            string card = CardOf(FixerPage.Render(SimpleRun()), "fill");
            int runAt = card.IndexOf("data-action=\"run\"", StringComparison.Ordinal);
            int skipAt = card.IndexOf("data-action=\"skip\"", StringComparison.Ordinal);
            Assert.True(runAt >= 0 && skipAt > runAt, "skip renders ahead of the run control");
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

        [Fact]
        public void A_stage_declaration_renders_inside_its_card_with_its_own_kind()
        {
            // Stage-scoped declarations are the operator-as-instrument slot
            // (#243): asked inside the stage's card, posted under the
            // declaration's own wire kind.
            var set = Kettle(Answering("wet"), Answering("hot"));
            set.Stages[0].Declarations = new[]
            {
                new FixerRunDeclaration("taste", "Can you taste the water?",
                    "You are the best instrument for this.",
                    new[] { new FixerDeclarationChoice("yes", "I can taste it") },
                    messageKind: "declare-hearing"),
            };

            string card = CardOf(FixerPage.Render(new FixerRun(set)), "fill");
            Assert.Contains("Can you taste the water?", card);
            Assert.Contains("data-kind=\"declare-hearing\"", card);
            Assert.Contains("id=\"declared-taste\"", card);
        }

        [Fact]
        public void A_live_question_overrides_the_static_one_when_supplied()
        {
            // The transmit set names the actual TX port and, for a remote
            // radio, says the question is about a station the operator is not
            // at (#244, #247). The delegate is read at render time.
            var set = Kettle(Answering("wet"), Answering("hot"));
            set.RunDeclarations[0].QuestionNow =
                () => "What is the kettle at THAT house plugged into?";

            string html = FixerPage.Render(new FixerRun(set));
            Assert.Contains("What is the kettle at THAT house plugged into?", html);
            Assert.DoesNotContain("What is the kettle plugged into right now?", html);
        }

        [Fact]
        public void Live_choices_override_the_static_ones_when_supplied()
        {
            // Remotely the honest ANSWERS change, not just the question
            // (#247): the transmit set swaps in choices that say whose word
            // the answer carries. Read at render time, like the question.
            var set = Kettle(Answering("wet"), Answering("hot"));
            set.RunDeclarations[0].ChoicesNow = () => new[]
            {
                new FixerDeclarationChoice("mains",
                    "The mains — someone at the house has confirmed it"),
            };

            string html = FixerPage.Render(new FixerRun(set));
            Assert.Contains("The mains — someone at the house has confirmed it", html);
            Assert.DoesNotContain("data-what=\"A generator\"", html);
        }

        [Fact]
        public void Choices_that_cannot_be_read_fall_back_to_the_static_ones()
        {
            // A question with no answers is a shut gate with no handle — the
            // declaration must never render answerless because a live read
            // threw or came back empty.
            var set = Kettle(Answering("wet"), Answering("hot"));
            set.RunDeclarations[0].ChoicesNow =
                () => throw new InvalidOperationException("radio gone");
            Assert.Contains("data-what=\"A generator\"",
                            FixerPage.Render(new FixerRun(set)));

            set.RunDeclarations[0].ChoicesNow = () => Array.Empty<FixerDeclarationChoice>();
            Assert.Contains("data-what=\"A generator\"",
                            FixerPage.Render(new FixerRun(set)));
        }

        [Fact]
        public void Live_why_text_overrides_the_static_one_when_supplied()
        {
            // The remote why-text tells the operator their answer goes in the
            // record with its provenance, before they give it (#247).
            var set = Kettle(Answering("wet"), Answering("hot"));
            set.RunDeclarations[0].WhyItMattersNow =
                () => "Your answer goes in the record, with whose word it is on.";

            string html = FixerPage.Render(new FixerRun(set));
            Assert.Contains("Your answer goes in the record, with whose word it is on.",
                            html);
        }

        // -------- host notices and run-versus-run-again --------

        [Fact]
        public void A_host_notice_renders_in_its_stages_card_without_being_a_result()
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
            string card = CardOf(FixerPage.Render(run, state), "boil");

            Assert.Contains("Nothing was transmitted", card);
            // Nothing ran: the stage still reads as unchecked, and the engine
            // holds no record.
            Assert.Contains("Not checked yet.", card);
            Assert.Null(run.ResultFor("boil"));
        }

        [Fact]
        public void Every_card_has_a_notice_slot_for_the_receive_channel()
        {
            string html = FixerPage.Render(SimpleRun());
            Assert.Contains("id=\"notice-fill\"", html);
            Assert.Contains("id=\"notice-boil\"", html);
        }

        [Fact]
        public void Running_again_is_a_distinct_deliberate_control()
        {
            var run = SimpleRun();
            string before = CardOf(FixerPage.Render(run), "boil");
            Assert.Contains("data-action=\"run\"", before);
            Assert.DoesNotContain("data-action=\"rerun\"", before);

            run.RunStage("boil");
            string after = CardOf(FixerPage.Render(run), "boil");
            Assert.Contains("data-action=\"rerun\"", after);
            Assert.Contains("Run this check again", after);
            Assert.DoesNotContain("data-action=\"run\"", after);
        }

        [Fact]
        public void A_skipped_stage_still_offers_a_first_run_not_a_repeat()
        {
            var run = SimpleRun();
            run.SkipStage("boil", "later");
            string card = CardOf(FixerPage.Render(run), "boil");
            Assert.Contains("data-action=\"run\"", card);
            Assert.DoesNotContain("data-action=\"rerun\"", card);
        }

        [Fact]
        public void A_host_action_is_a_hand_off_button_not_a_picker()
        {
            string card = CardOf(FixerPage.Render(SimpleRun()), "fill");
            var button = Regex.Match(card, "<button[^>]*data-action=\"host\"[^>]*>");
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
                       "'stop'", "'copy-report'", "'open-help'", "'explain'" })
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
            Assert.Contains("<body data-run=\"" + run.RunId + "\">", html);
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
