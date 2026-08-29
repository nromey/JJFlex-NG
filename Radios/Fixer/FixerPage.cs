using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Radios.Fixer
{
    /// <summary>
    /// Renders a run as the web page the operator actually meets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function from (stage set, run, page state) to markup. The host
    /// owns the WebView2 shell; on every action it performs, it renders again
    /// and navigates the fresh string. Statelessness here is what makes the
    /// DoD's static markup tests possible — and it is generic over the stage
    /// set for the same reason the engine is.
    /// </para>
    /// <para>
    /// <b>One document, no tablist (Sprint 35, from the 2026-08-25 session).</b>
    /// The page used to be an ARIA tab container — textbook markup, and wrong
    /// for a wizard: four of five panels carried <c>hidden</c>, so the tool
    /// could not be READ; the tablist had no heading, so H jumped over the
    /// only navigation; and the report's per-stage headings impersonated a
    /// stage list (#242, #248). Now every stage is always present, in order,
    /// as a section whose h2 CARRIES ITS STATUS — walking the headings is also
    /// the progress readout. Four landmarks with short names (how to use the
    /// page, the declarations region, "Stages", "Report"); h3 only for
    /// findings and the
    /// report's per-stage entries, so heading LEVEL disambiguates the two
    /// stage lists; F6 and Shift+F6 cycle the sections in focus mode, where
    /// heading navigation cannot reach.
    /// </para>
    /// <para>
    /// <b>The expander rule:</b> nothing operable is ever collapsed. Only the
    /// explanation sits behind a disclosure — <c>&lt;details&gt;</c> content
    /// is invisible to browse mode, heading navigation AND the Tab order, all
    /// three, which is exactly why whole stages must never collapse. The
    /// current stage's explanation opens by default until the stage has run; a
    /// toggle the operator makes is posted to the host and honoured across
    /// re-renders.
    /// </para>
    /// <para>
    /// <b>Forward motion:</b> a completed stage ends with a primary Next
    /// control that moves focus to the NEXT STAGE'S HEADING — never its Run
    /// button, because stages that transmit must be read before they are
    /// pressed (#248). The last stage's forward control goes to the report.
    /// Skip controls render ONLY on a stage with no result: offering skip on
    /// finished work destroyed the measurement (#249), and the engine now
    /// refuses that too.
    /// </para>
    /// <para>
    /// <b>Page-to-host wire:</b> every control posts
    /// <c>JSON.stringify({kind, ...})</c> via
    /// <c>window.chrome.webview.postMessage</c>, in exactly the shapes
    /// <see cref="FixerPageMessage"/> parses — <c>ready</c>,
    /// <c>declare-load</c> / <c>declare-hearing</c> (<c>what</c>: the chosen
    /// answer's own words; <c>choice</c>: its id), <c>run-stage</c>
    /// (<c>again: true</c> — real JSON true — for the deliberate repeat),
    /// <c>skip-stage</c> (<c>choice</c>: the skip choice id), <c>apply-fix</c>
    /// (<c>fix</c>: the FINDING id), <c>stop</c> (<c>source: "button"</c>),
    /// <c>copy-report</c> (the host owns the clipboard), <c>open-help</c>,
    /// <c>explain</c> (the disclosure toggle), and whatever bare kinds a
    /// stage's <see cref="FixerHostAction"/>s name. Safety facts have no field
    /// to travel in, by the parser's design. The Next control posts nothing —
    /// it is page-local focus movement.
    /// </para>
    /// <para>
    /// <b>Host-to-page wire:</b> the page defines
    /// <c>window.jjflex.receive(json)</c> for updates that must not cost a
    /// full re-render (which would move a screen reader's place). Kinds:
    /// <c>{"kind":"notice","stage":id,"text":t}</c> fills that stage's notice
    /// slot (a gate refusal, typically — near the run control, and NOT a
    /// result, because nothing ran); <c>{"kind":"critical","text":t}</c> fills
    /// the assertive region; <c>{"kind":"declared","declaration":id,"text":t}</c>
    /// fills a declaration's "You said" line; <c>{"kind":"status","text":t}</c>
    /// fills the polite status line. Unknown kinds are ignored here — the
    /// host traces its own sends.
    /// </para>
    /// <para>
    /// The Escape bridge is the host's, not the page's — Escape while keyed
    /// must not depend on web content having focus.
    /// </para>
    /// </remarks>
    public static class FixerPage
    {
        /// <summary>Render the whole document.</summary>
        /// <param name="run">The run to show. Its stage set supplies the copy.</param>
        /// <param name="selectedStageId">The CURRENT stage — its explanation
        /// opens by default until it runs, F6's stages stop lands on it, and
        /// the host focuses its heading after a re-render. Null or unknown
        /// falls back to the first stage — the default path starts at the
        /// beginning on purpose.</param>
        public static string Render(FixerRun run, string selectedStageId = null)
            => Render(run, new FixerPageState { SelectedStageId = selectedStageId });

        /// <summary>Render the whole document, with the host's view of the
        /// run-level state the engine does not hold.</summary>
        public static string Render(FixerRun run, FixerPageState state)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            state = state ?? new FixerPageState();
            FixerStageSet set = run.Set;

            FixerStage current = set.Find(state.SelectedStageId ?? "") ?? set.Stages[0];

            var sb = new StringBuilder(32 * 1024);
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            // Named by the TEST, not by a product noun (Noel, 2026-08-25).
            // "Fixer" remains the internal name; the operator reads
            // "Transmit tests", and a future set reads its own name with
            // nothing to rename.
            //
            // ── THE WORD IS "TEST" (#381) ────────────────────────────────
            //
            // This tool said "checks" here and "tests" in the exit prompt, in
            // the same session, about the same thing — the duplication defect
            // in the vocabulary layer, arrived by the ordinary route of each
            // string being written correctly on its own day. Noel's own usage
            // settled it: "stop tests and resume later", "run the tests".
            //
            // A STAGE OF A RUN IS A TEST. The analyzer's own counting keeps
            // the word CHECK for one evaluated rule — "Checked 14 of 19
            // checks" — because those are different things and one run of one
            // test makes many of them. Renaming those too would have traded a
            // duplication for an ambiguity.
            //
            // Deliberately mechanical, so reversing it is one pass: grep the
            // Fixer surfaces plus the Fix submenu for the word.
            sb.Append("<title>").Append(Esc(set.Name))
              .Append(" tests — Test ").Append(Esc(run.RunId)).AppendLine("</title>");
            sb.AppendLine("<style>").AppendLine(Css).AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.Append("<body data-run=\"").Append(Attr(run.RunId)).AppendLine("\">");

            Header(sb, run, state);
            HowToSection(sb, state);
            DeclarationsRegion(sb, set, state);

            sb.AppendLine("<main aria-label=\"Stages\">");
            foreach (FixerStage stage in set.Stages)
                StageCard(sb, run, stage, stage == current, state);
            sb.AppendLine("</main>");

            ReportSection(sb, run);

            sb.AppendLine("<script>").AppendLine(Script).AppendLine("</script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        // -------- focus landings and the spoken verdict (#373) --------
        //
        // THE RULE: after an action, focus goes to the NEXT ACTION, and the
        // verdict is spoken separately. Sprint 39 landed the operator on the
        // heading of the stage that had just finished — the right answer to
        // the wrong question: it fixed "where am I" and left "what now", so
        // every press still cost a walk. An announcement is not a focus
        // position; the host speaks the outcome through the status line and
        // puts the caret on the thing the operator has not dealt with yet.
        // These helpers live HERE, beside the markup that renders the ids,
        // so the host and the page cannot drift apart about them — and so
        // the rule is testable without a WebView.

        /// <summary>The id of a stage's run control — the landing after the
        /// operator answers a declaration or returns from the power window,
        /// where running the stage is the next action. Its description reads
        /// the stage's question and what pressing it will do.</summary>
        public static string RunControlId(string stageId) => "run-" + (stageId ?? "");

        /// <summary>The id of a completed stage's forward control — the
        /// landing after a stage runs clean or is skipped. Pressing it moves
        /// to the next stage's heading, so nothing is pressed unread.</summary>
        public static string NextControlId(string stageId) => "next-" + (stageId ?? "");

        /// <summary>The id of a finding's one-press fix button — the landing
        /// when a stage completes with something we can fix, because applying
        /// the fix is the next action and the button's description reads the
        /// finding.</summary>
        public static string FixControlId(string stageId, string findingId)
            => "fix-" + (stageId ?? "") + "-" + (findingId ?? "");

        /// <summary>
        /// Where focus lands after a stage produces a result (#373): the
        /// first fixable finding's button when there is one, the stage's own
        /// run control when the stage could not run (trying again once the
        /// cause is fixed is the only action that addresses that state), and
        /// the forward control otherwise — a pass and a deliberate skip are
        /// both finished business.
        /// </summary>
        public static string LandingAfterResult(FixerRun run, string stageId)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            FixerStageResult r = run.ResultFor(stageId ?? "");
            if (r == null) return "";

            if (r.Status == FixerStageStatus.CouldNotRun) return RunControlId(stageId);

            FixerFinding fixable = r.Findings.FirstOrDefault(f => f.Owner == FixOwner.Us);
            return fixable != null ? FixControlId(stageId, fixable.Id)
                                   : NextControlId(stageId);
        }

        /// <summary>
        /// The verdict of a stage, as one spoken sentence: the heading's own
        /// words — name and new status — then the answer, which is the whole
        /// product of running the stage. Composed here, from the same
        /// StatusOf and StatusPhrase the heading uses, so what is spoken and
        /// what the heading says cannot disagree. Empty when the stage has no
        /// result.
        /// </summary>
        public static string SpokenVerdict(FixerRun run, string stageId)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            FixerStage stage = run.Set.Find(stageId ?? "");
            FixerStageResult r = run.ResultFor(stageId ?? "");
            if (stage == null || r == null) return "";

            string verdict = StageLabel(stage) + " — "
                           + StatusPhrase(StatusOf(run, stage)) + ".";
            return r.Answer.Length > 0 ? verdict + " " + r.Answer : verdict;
        }

        // -------- top of page --------

        private static void Header(StringBuilder sb, FixerRun run, FixerPageState state)
        {
            sb.Append("<h1>").Append(Esc(run.Set.Name)).AppendLine(" tests</h1>");

            // The state of play in one sentence — the job the tablist did
            // badly, done in prose where a screen reader meets it first.
            sb.Append("<p id=\"run-summary\">").Append(Esc(Summary(run, state)))
              .AppendLine("</p>");

            sb.Append("<p>Your test ID is <strong>").Append(Esc(run.RunId))
              .AppendLine("</strong>. Everything this run records carries it, so keep it with "
                        + "any email about this problem.</p>");

            if (run.Set.Intro.Length > 0)
                sb.Append("<p>").Append(Esc(run.Set.Intro)).AppendLine("</p>");

            // The emergency out, not a fallback — before everything it might
            // need to stop, ahead of every other control, and never disabled.
            // "Stop everything", Noel's call 2026-08-28 (#377): he read plain
            // "Stop" as "stop the test run", calmly, with no RF in the air —
            // and this control aborts whatever is happening RIGHT NOW,
            // including a keyed transmit. The name has to survive that
            // misreading, and it has to stay distinct from the exit prompt's
            // "Stop tests and resume later", which is the calm, deliberate
            // decision this button must never sound like.
            sb.AppendLine("<p><button type=\"button\" data-action=\"stop\">"
                        + "Stop everything</button></p>");

            // The critical-warning carve-out. The page fills it; the host adds
            // the earcon.
            string critical = LatestCriticalWarning(run);
            sb.Append("<p aria-live=\"assertive\" id=\"critical-warning\">")
              .Append(Esc(critical)).AppendLine("</p>");

            // Quiet progress — "measuring…", "transmit finished" — pushed by
            // the host through the receive channel while a stage runs.
            sb.AppendLine("<p aria-live=\"polite\" id=\"status-line\"></p>");
        }

        /// <summary>
        /// "Five checks. Two passed, three not yet run. Nothing has keyed the
        /// radio." Counts by state, then the transmit fact a blind operator
        /// cannot glance at.
        /// </summary>
        private static string Summary(FixerRun run, FixerPageState state)
        {
            int passed = 0, problems = 0, skipped = 0, failed = 0, notRun = 0;
            foreach (FixerStage stage in run.Set.Stages)
            {
                switch (StatusOf(run, stage))
                {
                    case "passed": passed++; break;
                    case "problems": problems++; break;
                    case "skipped": skipped++; break;
                    case "failed": failed++; break;
                    default: notRun++; break;
                }
            }

            var parts = new List<string>();
            if (passed > 0) parts.Add(Count(passed) + " passed");
            if (problems > 0) parts.Add(Count(problems) + " with problems found");
            if (skipped > 0) parts.Add(Count(skipped) + " skipped");
            if (failed > 0) parts.Add(Count(failed) + " could not run");
            if (notRun > 0) parts.Add(Count(notRun) + " not yet run");

            string counts = Cap(Count(run.Set.Stages.Count)) + " tests"
                + (parts.Count > 0 ? ". " + Cap(string.Join(", ", parts)) + "." : ".");

            string keyed = state.TransmitCount <= 0
                ? " Nothing has keyed the radio."
                : state.TransmitCount == 1
                    ? " The radio has been keyed once this run."
                    : " The radio has been keyed " + Count(state.TransmitCount)
                      + " times this run.";

            return counts + keyed;
        }

        private static readonly string[] Small =
            { "zero", "one", "two", "three", "four", "five", "six", "seven",
              "eight", "nine", "ten", "eleven", "twelve" };

        private static string Count(int n)
            => n >= 0 && n < Small.Length ? Small[n]
                                          : n.ToString(CultureInfo.InvariantCulture);

        private static string Cap(string s)
            => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string LatestCriticalWarning(FixerRun run)
        {
            foreach (FixerStageResult r in run.ResultsInRunOrder.Reverse())
            {
                FixerFinding f = r.Findings.LastOrDefault(x => x.Critical);
                if (f != null) return f.WhatIsWrong + " " + f.WhatToDo;
            }
            return "";
        }

        // -------- how to use the page --------

        /// <summary>
        /// The pseudo-stage key the how-to disclosure reports its toggle
        /// under. Not a stage id; it rides the same explain wire and the same
        /// host-side memory as the stage explanations, so the operator's
        /// choice survives re-renders with no new machinery.
        /// </summary>
        public const string HowToUseKey = "how-to-use";

        /// <summary>
        /// How to move and how to leave, as a real F6 section ahead of the
        /// checks (#378). WCAG 3.3.2 — the one criterion the old page
        /// actually failed: the markup was correct and nothing told the
        /// operator how to use it. And Noel's question at the bench
        /// (2026-08-28) — "is the only way to exit then to escape?" — showed
        /// the ways OUT were just as undiscoverable as the ways around.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Behind a disclosure, because the two audiences want opposite
        /// things: a first-time operator needs it open, a tenth-run operator
        /// wants it gone. The host opens it by default only until a check run
        /// has ever been saved on this computer; the operator's own toggle,
        /// reported on the explain wire, wins thereafter for the life of the
        /// window. When collapsed, the summary line carries the promise —
        /// an operator must know what they are skipping past.
        /// </para>
        /// <para>
        /// Four bullets, deliberately. The Fixer diagnoses a radio; it does
        /// not teach its own UI, and a manual here would cost every operator
        /// a walk past it on every run.
        /// </para>
        /// </remarks>
        private static void HowToSection(StringBuilder sb, FixerPageState state)
        {
            bool open = state.ExplanationOpenFor(HowToUseKey,
                                                 fallback: state.HowToOpenByDefault);

            sb.AppendLine("<section class=\"howto\" aria-labelledby=\"howto-heading\">");
            sb.AppendLine("<h2 id=\"howto-heading\" tabindex=\"-1\">Using this page</h2>");
            sb.Append("<details data-stage=\"").Append(HowToUseKey).Append('"')
              .AppendLine(open ? " open>" : ">");
            sb.AppendLine("<summary>How to move through the tests, and how to leave"
                        + "</summary>");
            sb.AppendLine("<ul>");
            sb.AppendLine("<li>Each test is a heading. Your screen reader's heading keys "
                        + "move between them, Tab reaches the controls, and F6 or Shift+F6 "
                        + "jumps between the sections of the page.</li>");
            sb.AppendLine("<li>The tests run in order. Each answer you give and each "
                        + "test you run carries you to the next thing to do, so you can "
                        + "walk the whole run without going backwards.</li>");
            // The leave bullet states only what is true THIS run: the saved
            // wording is a promise, and a promise over a journal that never
            // opened would be silent data loss with a reassuring voice.
            sb.Append("<li>").Append(state.RunIsSaved
                ? "To leave, press Escape or close the window. Once anything has been "
                  + "recorded you choose on the way out: keep the run to pick up later "
                  + "from View or resume saved test runs, on the Fix menu, or leave "
                  + "without keeping it."
                : "To leave, press Escape or close the window. If the run has recorded "
                  + "anything, you are asked before it is lost.").AppendLine("</li>");
            sb.AppendLine("<li>Stop everything, above, is the emergency control: it stops "
                        + "whatever is happening right now, transmit included. If the "
                        + "radio is transmitting, the carrier drops first and questions "
                        + "come after.</li>");
            sb.AppendLine("</ul>");
            sb.AppendLine("</details>");
            sb.AppendLine("</section>");
        }

        // -------- run declarations --------

        private static void DeclarationsRegion(StringBuilder sb, FixerStageSet set,
                                               FixerPageState state)
        {
            if (set.RunDeclarations.Count == 0) return;

            // A named landmark with a SHORT name — NVDA speaks it on every
            // landmark jump. Its h2 doubles as the heading-navigation stop and
            // the F6 focus target.
            sb.AppendLine("<section class=\"decl\" aria-labelledby=\"decl-heading\">");
            sb.Append("<h2 id=\"decl-heading\" tabindex=\"-1\">")
              .Append(Esc(set.DeclarationsRegionName)).AppendLine("</h2>");

            foreach (FixerRunDeclaration decl in set.RunDeclarations)
                Declaration(sb, decl, state);

            sb.AppendLine("</section>");
        }

        private static void Declaration(StringBuilder sb, FixerRunDeclaration decl,
                                        FixerPageState state)
        {
            sb.AppendLine("<fieldset>");
            sb.Append("<legend>").Append(Esc(QuestionOf(decl))).AppendLine("</legend>");
            string why = WhyOf(decl);
            if (why.Length > 0)
                sb.Append("<p>").Append(Esc(why)).AppendLine("</p>");

            // What was said THIS run, as prose. The radios below are never
            // pre-checked, even after an answer — a new render must never
            // look like a remembered fact, because the station may have
            // been re-cabled and the whole point of the declaration is
            // that a person states it afresh. Always present so the host
            // can fill it through the receive channel too.
            string answered = state.DeclarationAnswerFor(decl.Id);
            sb.Append("<p id=\"declared-").Append(Attr(decl.Id)).Append("\">")
              .Append(answered.Length > 0 ? "You said: " + Esc(answered) : "")
              .AppendLine("</p>");

            foreach (FixerDeclarationChoice c in ChoicesOf(decl))
            {
                string id = "decl-" + Attr(decl.Id) + "-" + Attr(c.Id);
                // data-what carries the answer in the operator's own words:
                // the wire's "what" field is human text, not a choice id,
                // because it goes straight into the report and the gate's
                // record of what the load was. The choice id travels beside
                // it so the host can classify without parsing prose.
                sb.Append("<p><input type=\"radio\" name=\"decl-").Append(Attr(decl.Id))
                  .Append("\" id=\"").Append(id).Append("\" value=\"").Append(Attr(c.Id))
                  .Append("\" data-what=\"").Append(Attr(c.Label))
                  .Append("\"> <label for=\"").Append(id).Append("\">").Append(Esc(c.Label))
                  .AppendLine("</label></p>");
            }

            sb.Append("<p><button type=\"button\" data-action=\"declare\" data-arg=\"")
              .Append(Attr(decl.Id)).Append("\" data-kind=\"").Append(Attr(decl.MessageKind))
              .AppendLine("\">That is my answer</button></p>");
            sb.AppendLine("</fieldset>");
        }

        private static string QuestionOf(FixerRunDeclaration decl)
        {
            if (decl.QuestionNow != null)
            {
                try
                {
                    string live = decl.QuestionNow();
                    if (!string.IsNullOrWhiteSpace(live)) return live;
                }
                catch { /* fall back to the static question */ }
            }
            return decl.Question;
        }

        private static string WhyOf(FixerRunDeclaration decl)
        {
            if (decl.WhyItMattersNow != null)
            {
                try
                {
                    string live = decl.WhyItMattersNow();
                    if (!string.IsNullOrWhiteSpace(live)) return live;
                }
                catch { /* fall back to the static text */ }
            }
            return decl.WhyItMatters;
        }

        /// <summary>
        /// The choices as they stand right now. A live set that cannot be
        /// read falls back to the static one — the declaration must never
        /// render with no answers at all, because a question with no answers
        /// is a shut gate with no handle.
        /// </summary>
        private static IReadOnlyList<FixerDeclarationChoice> ChoicesOf(FixerRunDeclaration decl)
        {
            if (decl.ChoicesNow != null)
            {
                try
                {
                    IReadOnlyList<FixerDeclarationChoice> live = decl.ChoicesNow();
                    if (live != null && live.Count > 0) return live;
                }
                catch { /* fall back to the static choices */ }
            }
            return decl.Choices;
        }

        // -------- one stage's card --------

        private static string StageLabel(FixerStage stage)
            => "Stage " + stage.Number.ToString(CultureInfo.InvariantCulture) + ": " + stage.Title;

        /// <summary>
        /// The stage's state as a machine word for the stripe and badge:
        /// notrun, passed, problems, skipped, failed.
        /// </summary>
        private static string StatusOf(FixerRun run, FixerStage stage)
        {
            FixerStageResult r = run.ResultFor(stage.Id);
            if (r == null) return "notrun";
            switch (r.Status)
            {
                case FixerStageStatus.Skipped: return "skipped";
                case FixerStageStatus.CouldNotRun: return "failed";
                default: return r.Findings.Count > 0 ? "problems" : "passed";
            }
        }

        private static string StatusPhrase(string status)
        {
            switch (status)
            {
                case "passed": return "passed";
                case "problems": return "problems found";
                case "skipped": return "skipped";
                case "failed": return "could not run";
                default: return "not yet run";
            }
        }

        private static string StatusSymbol(string status)
        {
            switch (status)
            {
                case "passed": return "✔";     // check mark
                case "problems": return "✖";   // heavy x
                case "skipped": return "–";    // en dash
                case "failed": return "!";
                default: return "○";           // open circle
            }
        }

        private static void StageCard(StringBuilder sb, FixerRun run, FixerStage stage,
                                      bool current, FixerPageState state)
        {
            string status = StatusOf(run, stage);
            FixerStageResult result = run.ResultFor(stage.Id);

            sb.Append("<section class=\"stage\" id=\"stage-").Append(Attr(stage.Id))
              .Append("\" data-status=\"").Append(status).Append('"');
            if (current) sb.Append(" data-current=\"true\"");
            sb.AppendLine(">");

            // STATUS LIVES IN THE HEADING. Walking the stages with H is then
            // also the progress readout — name and state in one stop. The
            // heading is the focus target after a stage completes (hence
            // tabindex="-1", which is focusable-by-script, never a tab stop).
            sb.Append("<h2 id=\"stage-h-").Append(Attr(stage.Id))
              .Append("\" tabindex=\"-1\">").Append(Esc(StageLabel(stage)))
              .Append(" — ").Append(Esc(StatusPhrase(status))).AppendLine("</h2>");

            // The badge repeats the status for the eye — symbol and word, so
            // colour never carries the meaning alone. Hidden from assistive
            // tech because the identical words are in the heading one line
            // up; a screen reader hearing every status twice per stage would
            // pay for the stripe it cannot see.
            sb.Append("<p class=\"badge\" aria-hidden=\"true\">")
              .Append(StatusSymbol(status)).Append(' ')
              .Append(Esc(StatusPhrase(status))).AppendLine("</p>");

            // The question IS the stage, asked like a person. It also
            // describes the run control, so pressing Tab onto the button
            // reads the question back.
            sb.Append("<p id=\"q-").Append(Attr(stage.Id)).Append("\">")
              .Append(Esc(stage.Question)).AppendLine("</p>");

            string describedBy = "q-" + Attr(stage.Id);

            if (stage.Transmits)
            {
                sb.Append("<p id=\"tx-note-").Append(Attr(stage.Id))
                  .AppendLine("\">This test transmits.</p>");
                describedBy += " tx-note-" + Attr(stage.Id);
            }

            // What pressing Run will DO, with live facts — "at 25 watts into
            // ANT1" (#250). Rendered before the control and attached to it,
            // never behind a disclosure: a control that requires opening a
            // collapsed section before it is safe to press is a defect (#255).
            string willDo = DescribeRun(stage);
            if (willDo.Length > 0)
            {
                sb.Append("<p id=\"runwill-").Append(Attr(stage.Id)).Append("\">")
                  .Append(Esc(willDo)).AppendLine("</p>");
                describedBy += " runwill-" + Attr(stage.Id);
            }

            // In-card declarations — stage 0's "can you hear the radio?".
            foreach (FixerRunDeclaration decl in stage.Declarations)
                Declaration(sb, decl, state);

            // What the host had to say about this stage's last request — a
            // gate refusal, typically. Rendered here, where the operator is,
            // because a refusal that renders nowhere is exactly the silent
            // failure this tool exists to expose. NOT a stage result: nothing
            // ran, so it must not sit where a measurement would. Always
            // present so the receive channel can fill it without a re-render.
            //
            // LIVE, since Sprint 39. The receive channel fills this slot
            // without a re-render — that is the whole point of it — so
            // without a live region the host could write "Something is
            // already running" or "that check has already run" into the page
            // and a blind operator would never learn it had refused. The
            // page's other two host-filled slots (the critical warning and
            // the status line) were live from the day they were written; this
            // one, which carries every REFUSAL, was not. Polite, not
            // assertive: a refusal is not an alarm, and the assertive region
            // is reserved for a critical finding.
            sb.Append("<p id=\"notice-").Append(Attr(stage.Id))
              .Append("\" aria-live=\"polite\">")
              .Append(Esc(state.NoticeFor(stage.Id))).AppendLine("</p>");

            if (result == null)
            {
                sb.AppendLine("<p>Not run yet.</p>");
                RunButton(sb, stage, describedBy, again: false, primary: true);
                HostActionButtons(sb, stage);
                SkipControls(sb, stage);
            }
            else
            {
                ResultBlock(sb, stage, result);
                NextControl(sb, run.Set, stage);
                // Running again is a DISTINCT, deliberately-pressed action once
                // a stage has actually run. The host's gate refuses a second
                // plain run for a transmitting stage precisely because a
                // double-fired handler never announces itself as a repeat — so
                // the page keeps the two gestures apart. A skipped or
                // could-not-run stage has not run, so its control stays a
                // first run.
                bool ranBefore = result.Status == FixerStageStatus.Ran;
                RunButton(sb, stage, describedBy, again: ranBefore, primary: false);
                HostActionButtons(sb, stage);
                // No skip controls once a result exists: skipping a stage
                // that already produced one destroyed the measurement, and
                // on the transmit stages a measurement is paid for with RF
                // (#249). The engine refuses it too; this just stops the
                // page inviting it.
            }

            // Long text behind a disclosure: fully readable, zero tab cost
            // beyond the summary itself. Open by default only while this is
            // the current stage and it has not run; a toggle the operator
            // makes is posted to the host and wins thereafter.
            if (stage.Explanation.Length > 0)
            {
                bool open = state.ExplanationOpenFor(stage.Id,
                    fallback: current && result == null);
                sb.Append("<details data-stage=\"").Append(Attr(stage.Id)).Append('"')
                  .AppendLine(open ? " open>" : ">");
                sb.AppendLine("<summary>What this test does</summary>");
                sb.Append("<p>").Append(Esc(stage.Explanation)).AppendLine("</p>");
                sb.AppendLine("</details>");
            }

            if (stage.HelpTopic.Length > 0)
                sb.Append("<p><a href=\"jjflex-help:").Append(Attr(stage.HelpTopic))
                  .Append("\" data-topic=\"").Append(Attr(stage.HelpTopic))
                  .AppendLine("\">Help with this test</a></p>");

            sb.AppendLine("</section>");
        }

        private static string DescribeRun(FixerStage stage)
        {
            if (stage.DescribeRunAction == null) return "";
            try { return (stage.DescribeRunAction() ?? "").Trim(); }
            catch { return ""; }
        }

        private static void RunButton(StringBuilder sb, FixerStage stage, string describedBy,
                                      bool again, bool primary)
        {
            // The id is a focus landing (#373): the host puts the operator
            // here after they answer a declaration or return from the power
            // window, because from those gestures this control is the next
            // action. Same id for run and run-again — only one renders.
            sb.Append("<p><button type=\"button\" id=\"").Append(RunControlId(stage.Id))
              .Append("\" class=\"")
              .Append(primary ? "primary" : "quiet")
              .Append("\" data-action=\"").Append(again ? "rerun" : "run")
              .Append("\" data-arg=\"").Append(Attr(stage.Id))
              .Append("\" aria-describedby=\"").Append(describedBy)
              .Append("\">").Append(again ? "Run this test again" : "Run this test")
              .AppendLine("</button></p>");
        }

        /// <summary>
        /// The skip controls — rendered ONLY for a stage with no result, and
        /// after the run control, never before it. Skipping is a legitimate
        /// choice and a rare one; it used to be the most prominent thing on
        /// every stage in every state, including a stage that had just passed,
        /// where pressing it silently destroyed the measurement (#248, #249).
        /// </summary>
        private static void SkipControls(StringBuilder sb, FixerStage stage)
        {
            if (stage.SkipChoices.Count == 0) return;

            // "SAY WHY" WAS WRONG ABOUT THE INTERACTION, not merely clipped:
            // these are radio buttons, so the operator SELECTS a reason and
            // says nothing. Noel caught it reading the page (2026-08-25) and
            // thought he had corrected the same shape somewhere before.
            //
            // The sentence was also in the LEGEND, which NVDA announces on
            // entering the group and, in some modes, prepends to every option
            // in it. A legend is heard once per choice; it has to be short.
            //
            // So the legend is now a QUESTION and each option reads as an
            // answer to it, which is what a fieldset is for. The reason it
            // matters — and it does matter, or an operator picks the first one
            // to get past the screen — moves out into its own sentence above.
            sb.AppendLine("<p>The reason you choose changes what the report can say about "
                        + "the rest of the run, so pick the one that is actually true.</p>");
            sb.AppendLine("<fieldset class=\"skip\">");
            sb.AppendLine("<legend>Why are you skipping this stage?</legend>");

            foreach (FixerSkipChoice c in stage.SkipChoices)
            {
                string id = "skip-" + Attr(stage.Id) + "-" + Attr(c.Id);
                sb.Append("<p><input type=\"radio\" name=\"skipwhy-").Append(Attr(stage.Id))
                  .Append("\" id=\"").Append(id).Append("\" value=\"").Append(Attr(c.Id))
                  .Append("\"> <label for=\"").Append(id).Append("\">").Append(Esc(c.Label))
                  .AppendLine("</label></p>");
            }

            sb.Append("<p><button type=\"button\" class=\"quiet\" data-action=\"skip\" "
                    + "data-arg=\"")
              .Append(Attr(stage.Id))
              .AppendLine("\">Skip this stage</button></p>");
            sb.AppendLine("</fieldset>");
        }

        private static void HostActionButtons(StringBuilder sb, FixerStage stage)
        {
            foreach (FixerHostAction extra in stage.HostActions)
            {
                sb.Append("<p><button type=\"button\" data-action=\"host\" data-kind=\"")
                  .Append(Attr(extra.MessageKind)).Append("\">").Append(Esc(extra.Label))
                  .AppendLine("</button></p>");
            }
        }

        private static void ResultBlock(StringBuilder sb, FixerStage stage,
                                        FixerStageResult result)
        {
            // ONE PARAGRAPH PER BLOCK. Stage 0's answer is assembled from the
            // computer's half and the receive half (#367), and rendered as a
            // single <p> it was one unbroken run of six sentences with no way
            // for a screen reader user to step past the half they had already
            // heard. The split rule lives in Evidence.Paragraphs so this page
            // and the report cannot disagree about where the breaks fall.
            foreach (string block in Radios.Fixer.Evidence.Paragraphs.Split(result.Answer))
                sb.Append("<p>").Append(Esc(block)).AppendLine("</p>");

            if (result.Status == FixerStageStatus.Ran)
            {
                sb.Append("<p>Checked at ")
                  .Append(Esc(result.AtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'",
                                                    CultureInfo.InvariantCulture)))
                  .Append(result.WasReRun ? ". Re-run; this replaces an earlier result." : ".")
                  .AppendLine("</p>");
            }

            if (result.Findings.Count == 0) return;

            // Findings get their own heading — the only h3 inside a stage, so
            // NVDA's numbered heading keys give two navigation planes: 2 walks
            // the stages, 3 drills into what was found.
            sb.AppendLine("<h3>Findings</h3>");
            sb.AppendLine("<ul>");
            foreach (FixerFinding f in result.Findings)
            {
                sb.Append("<li><span id=\"find-").Append(Attr(stage.Id)).Append('-')
                  .Append(Attr(f.Id)).Append("\">").Append(Esc(f.WhatIsWrong)).Append("</span> ");

                switch (f.Owner)
                {
                    case FixOwner.Us:
                        // The fix at the point of detection. The button's
                        // description is the finding itself, so a screen
                        // reader landing on the button hears what it is for.
                        // The wire's "fix" field carries the FINDING id — the
                        // action id is a binding detail the page never sends.
                        // The id is a focus landing (#373): when a stage
                        // completes with a fixable finding, the host lands
                        // the operator on the first such button, whose
                        // description reads them the finding.
                        sb.Append("<button type=\"button\" id=\"")
                          .Append(FixControlId(stage.Id, f.Id))
                          .Append("\" data-action=\"fix\" data-stage=\"")
                          .Append(Attr(stage.Id)).Append("\" data-fix=\"").Append(Attr(f.Id))
                          .Append("\" aria-describedby=\"find-").Append(Attr(stage.Id))
                          .Append('-').Append(Attr(f.Id)).Append("\">")
                          .Append(Esc(f.WhatToDo)).Append("</button>");
                        break;
                    case FixOwner.Operator:
                        sb.Append("What to do: ").Append(Esc(f.WhatToDo));
                        break;
                    default:
                        sb.Append(Esc(f.WhatToDo));
                        break;
                }
                sb.AppendLine("</li>");
            }
            sb.AppendLine("</ul>");
        }

        /// <summary>
        /// The forward motion the intro promises (#248): a completed stage
        /// ends with a primary control to the NEXT STAGE'S HEADING — heading,
        /// not Run button, because landing on a button invites pressing
        /// before reading and stages 2 through 4 key the transmitter. The
        /// last stage's forward control goes to the report; the page never
        /// dead-ends.
        /// </summary>
        private static void NextControl(StringBuilder sb, FixerStageSet set, FixerStage stage)
        {
            int i = 0;
            while (i < set.Stages.Count && set.Stages[i] != stage) i++;

            // The id is a focus landing (#373): after a stage completes, the
            // host lands the operator on this control — the verdict is spoken
            // separately — so moving on is one press instead of a walk.
            sb.Append("<p><button type=\"button\" id=\"").Append(NextControlId(stage.Id))
              .Append("\" class=\"primary\" data-action=\"next\" "
                    + "data-arg=\"");
            if (i < set.Stages.Count - 1)
            {
                FixerStage next = set.Stages[i + 1];
                // data-stage carries the stage the operator is MOVING TO, so
                // the page can tell the host where they went (#365). Forward
                // motion is page-local and must stay that way — a re-render
                // per step would be its own defect — but the host still has
                // to know, because the current stage is what it focuses when
                // a render it did not cause comes back (returning from the
                // power window, most of all, which stages 2 through 4 use).
                // Absent on the report control: the report is not a stage.
                sb.Append("stage-h-").Append(Attr(next.Id))
                  .Append("\" data-stage=\"").Append(Attr(next.Id))
                  .Append("\">");
                // The label is built clear of the markup on purpose: glued to
                // the tag it is not a sentence, and tools/prose could not offer
                // it for editing. Two real operator phrases were invisible to
                // the editing surface for exactly that reason.
                sb.Append("Next: ").Append(Esc(StageLabel(next)));
            }
            else
            {
                sb.Append("report-heading\">");
                sb.Append("Go to the report");
            }
            sb.AppendLine("</button></p>");
        }

        // -------- the report --------

        private static void ReportSection(StringBuilder sb, FixerRun run)
        {
            // A named landmark with a SHORT name, set apart visually — a
            // distinct object, not a sixth stage.
            sb.AppendLine("<section class=\"report-region\" aria-labelledby=\"report-heading\">");
            sb.AppendLine("<h2 id=\"report-heading\" tabindex=\"-1\">Report</h2>");
            // WHAT TO DO WITH THE REPORT, next to the button that produces it.
            // Noel wrote this on 2026-08-25: "This may be way more detail than
            // what we need and you might want to reword it but it needed to be
            // said ... it's got to be near this paste button."
            //
            // The SmartSDR paragraph is the load-bearing one, and it is task
            // #217 in a sentence: evidence for Flex has to survive a reader who
            // distrusts our software entirely. An operator who reports a fault
            // from third-party software invites one reply — "does it happen in
            // SmartSDR?" — and the exchange costs days. Getting that answer
            // BEFORE sending turns the report from a complaint into a finding.
            //
            // Naming SmartSDR here is not naming a competitor: it is the
            // manufacturer's own client, and pointing at it is what makes the
            // report credible.
            sb.AppendLine("<p>This is the whole run as one document, every stage in order, "
                        + "with the test ID at the top. That ID belongs to this report alone, "
                        + "so quote it in any message about the problem.</p>");
            // "YOUR RADIO'S MANUFACTURER", Noel's own rewording 2026-08-28
            // (#374): change "email ready version, ready to send" to "email
            // ready version of the report which you can send to your radio's
            // manufacturer". It fixes the doubled "ready", and it retires the
            // REVISIT note that stood here — the paragraph used to say "Flex
            // support" outright (his 2026-08-25 call, right while Flex was
            // the only radio), and the note warned that the day a Hamlib rig
            // or the TS-590G connects, it would name the wrong manufacturer.
            // "Your radio's manufacturer" is true for a Flex, a Kenwood and
            // anything Hamlib reaches. Per-manufacturer text — a Flex
            // operator getting Flex-specific words again — is #375, and it
            // deliberately waits for a second radio to actually connect.
            sb.AppendLine("<p>Copy puts an email-ready version of the report on the "
                        + "clipboard, which you can send to your radio's manufacturer. It "
                        + "separates what was measured from what "
                        + "was concluded, so their staff can read the numbers without taking "
                        + "anything on trust.</p>");
            // "IF YOU ARE ABLE TO", not an instruction. Noel, minutes after this
            // shipped: "I don't have steps for how to check it in SmartSDR
            // right now with a screen reader ... I have no idea how to do it."
            // If the person who wrote this application cannot say how, no
            // operator can be assumed to know either.
            //
            // Task #220 called this in advance — "asking somebody to do
            // something and not telling them how is a question that trains
            // people to answer no" — and this paragraph was doing exactly that
            // within an hour of the task being written.
            //
            // NO HELP LINK HERE YET, deliberately: the page does not exist, and
            // pointing at one that does not is the drift this project keeps
            // paying for. When #220 lands, this sentence gets the route.
            // The SECOND paragraph the retired REVISIT note named (#374): it
            // pointed a TS-590G operator at SmartSDR — software they do not
            // own, which is worse than being wrong, it sends them looking for
            // a program they never installed. SmartSDR stays named, scoped as
            // the Flex example, because for today's operators it is the most
            // useful sentence on the page and #375 rules a Flex operator
            // keeps Flex-specific text.
            sb.AppendLine("<p>If you are able to transmit in your radio manufacturer's own "
                        + "software — for a Flex, that is SmartSDR — run the same test "
                        + "there before you send this, and see whether the problem follows "
                        + "you. If it "
                        + "does, say so in your message. A fault that shows up in the "
                        + "manufacturer's own "
                        + "software as well as in JJ Flexible Radio Access points at the "
                        + "radio or the station rather than at either program, and saying "
                        + "that up front will save you an exchange of emails.</p>");
            sb.AppendLine("<p><button type=\"button\" data-action=\"copy\">"
                        + "Copy the report as plain text</button></p>");

            // Prose only inside: however long it grows, zero tab stops. The
            // plain-text form Copy yields is built by the HOST from the same
            // FixerReport the fragment below comes from, so the two cannot
            // drift. Its per-stage headings are h3 — one level under this
            // section's h2 — which is what disambiguates them from the real
            // stages: level is a second navigation axis (#242).
            sb.AppendLine("<div id=\"report\">");
            sb.Append(FixerReport.HtmlFragment(run, headingLevel: 3));
            sb.AppendLine("</div>");
            sb.AppendLine("</section>");
        }

        // -------- escaping --------

        private static string Esc(string s)
            => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string Attr(string s)
            => Esc(s).Replace("\"", "&quot;");

        // -------- the page's own behaviour --------
        //
        // Deliberately small: posting wire messages to the host, the receive
        // channel for updates that must not cost a re-render, page-local focus
        // movement for Next and F6, and the disclosure-toggle report. No
        // Escape handling here — the Escape bridge is the host's, because
        // Escape while keyed must not depend on web content having focus.

        /// <summary>
        /// The page's own script, exposed so the wire contract can be tested.
        /// </summary>
        /// <remarks>
        /// Internal, not public: this is for FixerWireContractTests, which
        /// checks that every button the page renders is one this script
        /// handles, and that every message this script can post is one the host
        /// will parse. Those three names — the data-action, the message kind,
        /// and the parser's case label — are three separate strings that must
        /// agree, and when they stop agreeing NOTHING fails. The build passes,
        /// the page renders, the button looks like a button, and pressing it
        /// does nothing at all.
        /// </remarks>
        internal static string PageScript => Script;

        private const string Script = @"(function () {
  'use strict';
  var runId = (document.body && document.body.dataset && document.body.dataset.run) || '';
  function post(msg) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(JSON.stringify(msg));
    }
  }
  function setText(id, text) {
    var el = document.getElementById(id);
    if (el) { el.textContent = text; }
  }
  window.jjflex = window.jjflex || {};
  window.jjflex.receive = function (json) {
    var m;
    try { m = JSON.parse(json); } catch (err) { return; }
    if (!m || typeof m.kind !== 'string') { return; }
    if (m.kind === 'notice') { setText('notice-' + (m.stage || ''), m.text || ''); }
    else if (m.kind === 'critical') { setText('critical-warning', m.text || ''); }
    else if (m.kind === 'declared') {
      setText('declared-' + (m.declaration || ''), m.text ? 'You said: ' + m.text : '');
    }
    else if (m.kind === 'status') { setText('status-line', m.text || ''); }
  };
  function focusById(id) {
    var t = document.getElementById(id);
    if (t) { t.focus(); }
  }
  // F6 / Shift+F6 cycle the page's sections in focus mode, where heading
  // navigation cannot reach: how to use the page, declarations, the current
  // stage, the report.
  function sectionHeads() {
    var heads = [];
    var ht = document.getElementById('howto-heading');
    if (ht) { heads.push(ht); }
    var d = document.getElementById('decl-heading');
    if (d) { heads.push(d); }
    var cur = document.querySelector('main .stage[data-current] h2')
           || document.querySelector('main .stage h2');
    if (cur) { heads.push(cur); }
    var r = document.getElementById('report-heading');
    if (r) { heads.push(r); }
    return heads;
  }
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'F6') { return; }
    e.preventDefault();
    var heads = sectionHeads();
    if (!heads.length) { return; }
    var here = -1;
    var el = document.activeElement;
    while (el && here < 0) {
      for (var i = 0; i < heads.length; i++) {
        var sec = heads[i].closest('section');
        if (sec && sec === el) { here = i; }
      }
      el = el.parentElement;
    }
    var step = e.shiftKey ? heads.length - 1 : 1;
    var next = here < 0 ? (e.shiftKey ? heads.length - 1 : 0)
                        : (here + step) % heads.length;
    heads[next].focus();
  }, true);
  // The operator's disclosure choices are reported so a re-render honours
  // them instead of springing the prose back open.
  Array.prototype.forEach.call(document.querySelectorAll('details[data-stage]'),
    function (d) {
      d.addEventListener('toggle', function () {
        post({ kind: 'explain', run: runId, stage: d.dataset.stage || '', open: d.open });
      });
    });
  function checkedIn(button) {
    var scope = button.closest('fieldset');
    return scope ? scope.querySelector('input[type=""radio""]:checked') : null;
  }
  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[data-topic]') : null;
    if (a) {
      e.preventDefault();
      post({ kind: 'open-help', run: runId, topic: a.dataset.topic || '' });
      return;
    }
    var b = e.target && e.target.closest ? e.target.closest('button[data-action]') : null;
    if (!b) { return; }
    var action = b.dataset.action;
    if (action === 'stop') { post({ kind: 'stop', run: runId, source: 'button' }); }
    else if (action === 'run') { post({ kind: 'run-stage', run: runId, stage: b.dataset.arg || '' }); }
    else if (action === 'rerun') {
      post({ kind: 'run-stage', run: runId, stage: b.dataset.arg || '', again: true });
    }
    else if (action === 'skip') {
      var why = checkedIn(b);
      post({ kind: 'skip-stage', run: runId, stage: b.dataset.arg || '',
             choice: why ? why.value : '' });
    }
    else if (action === 'declare') {
      var pick = checkedIn(b);
      post({ kind: b.dataset.kind || 'declare-load', run: runId,
             what: pick ? (pick.dataset.what || '') : '',
             choice: pick ? (pick.value || '') : '' });
    }
    else if (action === 'fix') {
      post({ kind: 'apply-fix', run: runId, stage: b.dataset.stage || '',
             fix: b.dataset.fix || '' });
    }
    else if (action === 'copy') { post({ kind: 'copy-report', run: runId }); }
    else if (action === 'host') { post({ kind: b.dataset.kind || '', run: runId }); }
    else if (action === 'next') {
      // Page-local forward motion: focus the named heading. Headings, not
      // Run buttons, so nothing is pressed before it is read.
      focusById(b.dataset.arg || '');
      // Then say where the operator went, in both places that need it. The
      // data-current attribute is what F6's stages stop reads, so it moves
      // now rather than at the next render; the message keeps the HOST's
      // marker level, so a render it did not cause brings them back here
      // and not to the stage they left. Neither costs a re-render.
      var went = b.dataset.stage || '';
      if (went) {
        var was = document.querySelector('main .stage[data-current]');
        if (was) { was.removeAttribute('data-current'); }
        var now = document.getElementById('stage-' + went);
        if (now) { now.setAttribute('data-current', 'true'); }
        post({ kind: 'current-stage', run: runId, stage: went });
      }
    }
  });
  post({ kind: 'ready', run: runId });
})();";

        // The first visual pass (Sprint 35, from the 2026-08-25 design
        // session). The accessible structure IS the visual structure: the
        // stripe rides data-status, the card is the stage section, the badge
        // repeats the status as symbol-plus-word so colour never carries
        // meaning alone (WCAG 1.4.1). One readable column near 70 characters;
        // body size above browser default, because people read this while
        // something is wrong; a loud focus indicator; light and dark both
        // honoured via prefers-color-scheme.
        private const string Css = @":root { color-scheme: light dark;
  --page: #ffffff; --ink: #1b1b1b; --muted: #565656;
  --card: #fafafa; --edge: #cfcfcf;
  --ok: #17692f; --warn: #8a5c00; --bad: #b41f2a; --off: #5f6771;
  --accent: #0550ae; --accent-ink: #ffffff; --report-bg: #eef3f8; }
@media (prefers-color-scheme: dark) {
  :root { --page: #1e1e1e; --ink: #e8e8e8; --muted: #b4b4b4;
    --card: #252526; --edge: #45484d;
    --ok: #4ac26b; --warn: #d4a72c; --bad: #ff8489; --off: #9aa2ab;
    --accent: #6cb6ff; --accent-ink: #10233a; --report-bg: #20262e; } }
body { background: var(--page); color: var(--ink);
  font-family: system-ui, sans-serif; font-size: 1.06rem; line-height: 1.55;
  margin: 1rem auto; max-width: 70ch; padding: 0 1rem; }
:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
button { font: inherit; padding: 0.35rem 0.9rem; border-radius: 6px;
  border: 1px solid var(--edge); background: var(--card); color: var(--ink); }
button.primary { background: var(--accent); border-color: var(--accent);
  color: var(--accent-ink); font-weight: 600; }
button.quiet { background: transparent; }
section.stage { border: 1px solid var(--edge); border-left: 6px solid var(--warn);
  border-radius: 8px; background: var(--card);
  padding: 0.25rem 1rem 0.75rem; margin: 1.25rem 0; }
section.stage[data-status='passed'] { border-left-color: var(--ok); }
section.stage[data-status='problems'] { border-left-color: var(--bad); }
section.stage[data-status='skipped'] { border-left-color: var(--off); }
.badge { display: inline-block; font-size: 0.85em; font-weight: 600;
  padding: 0.05rem 0.6rem; border-radius: 999px;
  border: 1px solid var(--warn); color: var(--warn); }
[data-status='passed'] .badge { border-color: var(--ok); color: var(--ok); }
[data-status='problems'] .badge { border-color: var(--bad); color: var(--bad); }
[data-status='skipped'] .badge { border-color: var(--off); color: var(--off); }
h2 { margin-bottom: 0.25rem; }
fieldset { margin: 1rem 0; border: 1px solid var(--edge); border-radius: 6px; }
details { margin: 0.75rem 0; }
summary { color: var(--muted); cursor: pointer; }
pre { white-space: pre-wrap; overflow-x: auto; }
section.decl { border: 1px solid var(--edge); border-radius: 8px;
  padding: 0.25rem 1rem 0.5rem; margin: 1.25rem 0; }
section.howto { border: 1px solid var(--edge); border-radius: 8px;
  padding: 0.25rem 1rem 0.5rem; margin: 1.25rem 0; }
section.report-region { background: var(--report-bg); border-radius: 8px;
  padding: 0.25rem 1rem 1rem; margin: 1.5rem 0; }
#report { border-top: 1px solid var(--edge); padding-top: 0.5rem; }";
    }

    /// <summary>
    /// The host's view of run-level state the engine deliberately does not
    /// hold — the current stage, what the operator declared this run, any
    /// speakable notice the host wants shown at a stage (a transmit-gate
    /// refusal, typically), how many transmits the gate has counted, and
    /// which explanations the operator has opened or closed.
    /// </summary>
    public sealed class FixerPageState
    {
        /// <summary>The CURRENT stage: its explanation opens by default until
        /// it runs, F6's stages stop lands on it, and the host focuses its
        /// heading after a re-render.</summary>
        public string SelectedStageId { get; set; }

        /// <summary>Declaration id to the answer AS TEXT TO SHOW ("A dummy
        /// load"). Empty each new run: the gate forgets the declaration on
        /// BeginRun because the station may have been re-cabled, and the page
        /// must forget with it.</summary>
        public IReadOnlyDictionary<string, string> DeclarationAnswers { get; set; }

        /// <summary>Stage id to a speakable notice rendered by that stage's
        /// run control. A refusal is not a result — nothing ran — so it lives
        /// here rather than in the run's records.</summary>
        public IReadOnlyDictionary<string, string> StageNotices { get; set; }

        /// <summary>How many transmits the gate has counted this run, for the
        /// summary line's "nothing has keyed the radio" — the fact a blind
        /// operator cannot glance at.</summary>
        public int TransmitCount { get; set; }

        /// <summary>Stage id to the explanation-disclosure state the OPERATOR
        /// chose, reported by the page's toggle message. Absent means they
        /// have not touched it and the render's default applies. The how-to
        /// section's toggle lives here too, under
        /// <see cref="FixerPage.HowToUseKey"/>.</summary>
        public IReadOnlyDictionary<string, bool> ExplanationOpen { get; set; }

        /// <summary>Whether the how-to section opens by default (#378). The
        /// host passes true until a check run has ever been saved on this
        /// computer — a first-time operator needs the instructions open, a
        /// returning one wants them folded away. The operator's own toggle,
        /// held in <see cref="ExplanationOpen"/>, wins over this.</summary>
        public bool HowToOpenByDefault { get; set; }

        /// <summary>True when the evidence layer is actually persisting this
        /// run. The how-to section's leaving bullet rides this fact: it may
        /// only promise "pick it up later" while something is really writing
        /// the run to disk.</summary>
        public bool RunIsSaved { get; set; }

        internal string DeclarationAnswerFor(string declId)
            => DeclarationAnswers != null
               && DeclarationAnswers.TryGetValue(declId ?? "", out string v)
                ? (v ?? "") : "";

        internal string NoticeFor(string stageId)
            => StageNotices != null
               && StageNotices.TryGetValue(stageId ?? "", out string v)
                ? (v ?? "") : "";

        internal bool ExplanationOpenFor(string stageId, bool fallback)
            => ExplanationOpen != null
               && ExplanationOpen.TryGetValue(stageId ?? "", out bool open)
                ? open : fallback;
    }
}
