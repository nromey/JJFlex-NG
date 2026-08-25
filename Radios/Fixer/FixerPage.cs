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
    /// <b>The page speaks for itself.</b> No app speech drives this surface;
    /// WebView2 exposes real UIA and the screen reader reads where the
    /// operator is. The one carve-out is the assertive live region for
    /// critical warnings, which the host pairs with an earcon until testing
    /// proves the region fires reliably under both NVDA and JAWS.
    /// </para>
    /// <para>
    /// The web surface exists for browse mode: H moves between stages in the
    /// report, B between buttons, and explanatory prose costs zero tab stops.
    /// Hence the structural rules the tests hold: one heading level per
    /// nesting step and no skips, a real ARIA tablist with roving tabindex,
    /// <c>&lt;button&gt;</c> elements only, labels associated with controls,
    /// disclosures for long text, results in the DOM where their stage is,
    /// and NOTHING focusable that is only prose.
    /// </para>
    /// <para>
    /// <b>Page-to-host wire:</b> every control posts
    /// <c>JSON.stringify({kind, ...})</c> via
    /// <c>window.chrome.webview.postMessage</c>, in exactly the shapes
    /// <see cref="FixerPageMessage"/> parses — <c>ready</c>,
    /// <c>declare-load</c> (<c>what</c>: the chosen answer's own words),
    /// <c>run-stage</c> (<c>again: true</c> — real JSON true — for the
    /// deliberate repeat), <c>skip-stage</c> (<c>choice</c>: the skip choice
    /// id), <c>apply-fix</c> (<c>fix</c>: the FINDING id), <c>stop</c>
    /// (<c>source: "button"</c>), <c>copy-report</c> (the host owns the
    /// clipboard), <c>open-help</c>, and whatever bare kinds a stage's
    /// <see cref="FixerHostAction"/>s name. Safety facts have no field to
    /// travel in, by the parser's design. Tab selection posts nothing — it is
    /// page-local, and the host re-derives the current stage from the last
    /// stage-scoped message.
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
        /// <param name="selectedStageId">Which stage's tab is active; null or
        /// unknown falls back to the first stage — the default path starts at
        /// the beginning on purpose.</param>
        public static string Render(FixerRun run, string selectedStageId = null)
            => Render(run, new FixerPageState { SelectedStageId = selectedStageId });

        /// <summary>Render the whole document, with the host's view of the
        /// run-level state the engine does not hold.</summary>
        public static string Render(FixerRun run, FixerPageState state)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            state = state ?? new FixerPageState();
            FixerStageSet set = run.Set;

            FixerStage selected = set.Find(state.SelectedStageId ?? "") ?? set.Stages[0];

            var sb = new StringBuilder(32 * 1024);
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>JJ Flexible Fixer — ").Append(Esc(set.Name))
              .Append(" — Test ").Append(Esc(run.RunId)).AppendLine("</title>");
            sb.AppendLine("<style>").AppendLine(Css).AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.Append("<main data-run=\"").Append(Attr(run.RunId)).AppendLine("\">");

            Header(sb, run);
            Declarations(sb, set, state);
            Tablist(sb, set, selected);
            foreach (FixerStage stage in set.Stages)
                Panel(sb, run, stage, stage == selected, state);
            ReportSection(sb, run);

            sb.AppendLine("</main>");
            sb.AppendLine("<script>").AppendLine(Script).AppendLine("</script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        // -------- top of page --------

        private static void Header(StringBuilder sb, FixerRun run)
        {
            sb.Append("<h1>JJ Flexible Fixer — ").Append(Esc(run.Set.Name)).AppendLine("</h1>");

            sb.Append("<p>Your test ID is <strong>").Append(Esc(run.RunId))
              .AppendLine("</strong>. Everything this run records carries it, so keep it with "
                        + "any email about this problem.</p>");

            if (run.Set.Intro.Length > 0)
                sb.Append("<p>").Append(Esc(run.Set.Intro)).AppendLine("</p>");

            // The primary way out, not a fallback — before everything it might
            // need to stop, and never disabled.
            sb.AppendLine("<p><button type=\"button\" data-action=\"stop\">"
                        + "Stop</button></p>");

            // The critical-warning carve-out. The page fills it; the host adds
            // the earcon.
            string critical = LatestCriticalWarning(run);
            sb.Append("<p aria-live=\"assertive\" id=\"critical-warning\">")
              .Append(Esc(critical)).AppendLine("</p>");

            // Quiet progress — "measuring…", "transmit finished" — pushed by
            // the host through the receive channel while a stage runs.
            sb.AppendLine("<p aria-live=\"polite\" id=\"status-line\"></p>");
        }

        private static string LatestCriticalWarning(FixerRun run)
        {
            foreach (FixerStageResult r in run.ResultsInRunOrder.Reverse())
            {
                FixerFinding f = r.Findings.LastOrDefault(x => x.Critical);
                if (f != null) return f.WhatIsWrong + " " + f.WhatToDo;
            }
            return "";
        }

        // -------- run declarations --------

        private static void Declarations(StringBuilder sb, FixerStageSet set,
                                         FixerPageState state)
        {
            foreach (FixerRunDeclaration decl in set.RunDeclarations)
            {
                sb.AppendLine("<fieldset>");
                sb.Append("<legend>").Append(Esc(decl.Question)).AppendLine("</legend>");
                if (decl.WhyItMatters.Length > 0)
                    sb.Append("<p>").Append(Esc(decl.WhyItMatters)).AppendLine("</p>");

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

                foreach (FixerDeclarationChoice c in decl.Choices)
                {
                    string id = "decl-" + Attr(decl.Id) + "-" + Attr(c.Id);
                    // data-what carries the answer in the operator's own words:
                    // the wire's "what" field is human text, not a choice id,
                    // because it goes straight into the report and the gate's
                    // record of what the load was.
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
        }

        // -------- the tablist --------

        private static void Tablist(StringBuilder sb, FixerStageSet set, FixerStage selected)
        {
            sb.AppendLine("<div role=\"tablist\" aria-label=\"Stages\">");
            foreach (FixerStage stage in set.Stages)
            {
                bool on = stage == selected;
                sb.Append("<button type=\"button\" role=\"tab\" id=\"tab-").Append(Attr(stage.Id))
                  .Append("\" data-stage=\"").Append(Attr(stage.Id))
                  .Append("\" aria-controls=\"panel-").Append(Attr(stage.Id))
                  .Append("\" aria-selected=\"").Append(on ? "true" : "false")
                  .Append("\" tabindex=\"").Append(on ? "0" : "-1")
                  .Append("\">").Append(Esc(StageLabel(stage))).AppendLine("</button>");
            }
            sb.AppendLine("</div>");
        }

        private static string StageLabel(FixerStage stage)
            => "Stage " + stage.Number.ToString(CultureInfo.InvariantCulture) + ": " + stage.Title;

        // -------- one stage's panel --------

        private static void Panel(StringBuilder sb, FixerRun run, FixerStage stage,
                                  bool selected, FixerPageState state)
        {
            sb.Append("<section role=\"tabpanel\" id=\"panel-").Append(Attr(stage.Id))
              .Append("\" aria-labelledby=\"tab-").Append(Attr(stage.Id)).Append('"');
            if (!selected) sb.Append(" hidden");
            sb.AppendLine(">");

            sb.Append("<h2>").Append(Esc(StageLabel(stage))).AppendLine("</h2>");

            // The question IS the stage, asked like a person. It also
            // describes the run control, so pressing Tab onto the button
            // reads the question back.
            sb.Append("<p id=\"q-").Append(Attr(stage.Id)).Append("\">")
              .Append(Esc(stage.Question)).AppendLine("</p>");

            Result(sb, run, stage);

            // What the host had to say about this stage's last request — a
            // gate refusal, typically. Rendered here, where the operator is,
            // because a refusal that renders nowhere is exactly the silent
            // failure this tool exists to expose. NOT a stage result: nothing
            // ran, so it must not sit where a measurement would. Always
            // present so the receive channel can fill it without a re-render.
            sb.Append("<p id=\"notice-").Append(Attr(stage.Id)).Append("\">")
              .Append(Esc(state.NoticeFor(stage.Id))).AppendLine("</p>");

            RunControls(sb, run, stage);
            SkipControls(sb, stage);

            // Long text behind a disclosure: fully readable, zero tab cost
            // beyond the summary itself.
            if (stage.Explanation.Length > 0)
            {
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>What this stage does, and why</summary>");
                sb.Append("<p>").Append(Esc(stage.Explanation)).AppendLine("</p>");
                sb.AppendLine("</details>");
            }

            if (stage.HelpTopic.Length > 0)
                sb.Append("<p><a href=\"jjflex-help:").Append(Attr(stage.HelpTopic))
                  .Append("\" data-topic=\"").Append(Attr(stage.HelpTopic))
                  .AppendLine("\">Help with this stage</a></p>");

            PrevNext(sb, run.Set, stage);

            sb.AppendLine("</section>");
        }

        private static void Result(StringBuilder sb, FixerRun run, FixerStage stage)
        {
            FixerStageResult result = run.ResultFor(stage.Id);
            if (result == null)
            {
                sb.AppendLine("<p>Not checked yet.</p>");
                return;
            }

            switch (result.Status)
            {
                case FixerStageStatus.Skipped:
                case FixerStageStatus.CouldNotRun:
                    sb.Append("<p>").Append(Esc(result.Answer)).AppendLine("</p>");
                    return;
            }

            sb.Append("<p>").Append(Esc(result.Answer)).AppendLine("</p>");
            sb.Append("<p>Checked at ")
              .Append(Esc(result.AtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'",
                                                CultureInfo.InvariantCulture)))
              .Append(result.WasReRun ? ". Re-run; this replaces an earlier result." : ".")
              .AppendLine("</p>");

            if (result.Findings.Count == 0) return;

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
                        sb.Append("<button type=\"button\" data-action=\"fix\" data-stage=\"")
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

        private static void RunControls(StringBuilder sb, FixerRun run, FixerStage stage)
        {
            string describedBy = "q-" + Attr(stage.Id);
            if (stage.Transmits)
            {
                sb.Append("<p id=\"tx-note-").Append(Attr(stage.Id))
                  .AppendLine("\">This check transmits.</p>");
                describedBy += " tx-note-" + Attr(stage.Id);
            }

            // Running again is a DISTINCT, deliberately-pressed action once a
            // stage has actually run. The host's gate refuses a second plain
            // run for a transmitting stage precisely because a double-fired
            // handler never announces itself as a repeat — so the page keeps
            // the two gestures apart. A skipped or could-not-run stage has
            // not run, so its control stays a first run.
            bool ranBefore = run.ResultFor(stage.Id)?.Status == FixerStageStatus.Ran;
            sb.Append("<p><button type=\"button\" data-action=\"")
              .Append(ranBefore ? "rerun" : "run")
              .Append("\" data-arg=\"").Append(Attr(stage.Id))
              .Append("\" aria-describedby=\"").Append(describedBy)
              .Append("\">").Append(ranBefore ? "Run this check again" : "Run this check")
              .AppendLine("</button></p>");

            foreach (FixerHostAction extra in stage.HostActions)
            {
                sb.Append("<p><button type=\"button\" data-action=\"host\" data-kind=\"")
                  .Append(Attr(extra.MessageKind)).Append("\">").Append(Esc(extra.Label))
                  .AppendLine("</button></p>");
            }
        }

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
            sb.AppendLine("<fieldset>");
            sb.AppendLine("<legend>Why are you skipping this stage?</legend>");

            foreach (FixerSkipChoice c in stage.SkipChoices)
            {
                string id = "skip-" + Attr(stage.Id) + "-" + Attr(c.Id);
                sb.Append("<p><input type=\"radio\" name=\"skipwhy-").Append(Attr(stage.Id))
                  .Append("\" id=\"").Append(id).Append("\" value=\"").Append(Attr(c.Id))
                  .Append("\"> <label for=\"").Append(id).Append("\">").Append(Esc(c.Label))
                  .AppendLine("</label></p>");
            }

            sb.Append("<p><button type=\"button\" data-action=\"skip\" data-arg=\"")
              .Append(Attr(stage.Id))
              .AppendLine("\">Skip this stage</button></p>");
            sb.AppendLine("</fieldset>");
        }

        private static void PrevNext(StringBuilder sb, FixerStageSet set, FixerStage stage)
        {
            int i = 0;
            while (i < set.Stages.Count && set.Stages[i] != stage) i++;

            // Absent rather than disabled at the ends: a disabled control in
            // the tab order is a stop that does nothing.
            sb.Append("<p>");
            if (i > 0)
            {
                FixerStage prev = set.Stages[i - 1];
                sb.Append("<button type=\"button\" data-action=\"select\" data-arg=\"")
                  .Append(Attr(prev.Id)).Append("\">Back to ")
                  .Append(Esc(StageLabel(prev))).Append("</button> ");
            }
            if (i < set.Stages.Count - 1)
            {
                FixerStage next = set.Stages[i + 1];
                sb.Append("<button type=\"button\" data-action=\"select\" data-arg=\"")
                  .Append(Attr(next.Id)).Append("\">On to ")
                  .Append(Esc(StageLabel(next))).Append("</button>");
            }
            sb.AppendLine("</p>");
        }

        // -------- the report --------

        private static void ReportSection(StringBuilder sb, FixerRun run)
        {
            sb.AppendLine("<section aria-labelledby=\"report-heading\">");
            sb.AppendLine("<h2 id=\"report-heading\">The report</h2>");
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
            // SAYS "FLEX" OUTRIGHT, on Noel's call 2026-08-25: "Right now we
            // don't have other radios, so if you just want to mention Flex,
            // that's cool for now." The generic "your radio's manufacturer"
            // hedge bought nothing while Flex is the only radio supported, and
            // it cost the paragraph a conditional clause.
            //
            // REVISIT WHEN A NON-FLEX RADIO ARRIVES. Hamlib and the TS-590G are
            // real planned work, and on the day one of them connects, these two
            // paragraphs name the wrong manufacturer and point at software the
            // operator does not own. Recorded here rather than left to be
            // discovered by whoever ships it.
            sb.AppendLine("<p>Copy puts an email-ready version on the clipboard, ready to "
                        + "send to Flex support. It separates what was measured from what "
                        + "was concluded, so their staff can read the numbers without taking "
                        + "anything on trust.</p>");
            sb.AppendLine("<p>One thing worth doing before you send it: run the same test in "
                        + "SmartSDR and see whether the problem follows you there. If it "
                        + "does, say so in your message. A fault that shows up in Flex's own "
                        + "software as well as in JJ Flexible Radio Access points at the "
                        + "radio or the station rather than at either program, and saying "
                        + "that up front will save you an exchange of emails.</p>");
            sb.AppendLine("<p><button type=\"button\" data-action=\"copy\">"
                        + "Copy the report as plain text</button></p>");

            // Prose only inside: however long it grows, zero tab stops. The
            // plain-text form Copy yields is built by the HOST from the same
            // FixerReport the fragment below comes from, so the two cannot
            // drift.
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
        // Deliberately small: roving tabindex for the tablist, posting wire
        // messages to the host, and the receive channel for updates that must
        // not cost a re-render. Tab selection is page-local and posts nothing.
        // No Escape handling here — the Escape bridge is the host's, because
        // Escape while keyed must not depend on web content having focus.

        private const string Script = @"(function () {
  'use strict';
  var main = document.querySelector('main');
  var runId = (main && main.dataset && main.dataset.run) || '';
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
  var tabs = Array.prototype.slice.call(document.querySelectorAll('[role=""tab""]'));
  function select(tab, focus) {
    tabs.forEach(function (t) {
      var on = t === tab;
      t.setAttribute('aria-selected', on ? 'true' : 'false');
      t.tabIndex = on ? 0 : -1;
      var panel = document.getElementById(t.getAttribute('aria-controls'));
      if (panel) { panel.hidden = !on; }
    });
    if (focus) { tab.focus(); }
  }
  tabs.forEach(function (t, i) {
    t.addEventListener('click', function () { select(t, false); });
    t.addEventListener('keydown', function (e) {
      var j = -1;
      if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { j = (i + 1) % tabs.length; }
      else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { j = (i - 1 + tabs.length) % tabs.length; }
      else if (e.key === 'Home') { j = 0; }
      else if (e.key === 'End') { j = tabs.length - 1; }
      if (j >= 0) { e.preventDefault(); select(tabs[j], true); }
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
             what: pick ? (pick.dataset.what || '') : '' });
    }
    else if (action === 'fix') {
      post({ kind: 'apply-fix', run: runId, stage: b.dataset.stage || '',
             fix: b.dataset.fix || '' });
    }
    else if (action === 'copy') { post({ kind: 'copy-report', run: runId }); }
    else if (action === 'host') { post({ kind: b.dataset.kind || '', run: runId }); }
    else if (action === 'select') {
      var target = tabs.filter(function (t) { return t.dataset.stage === b.dataset.arg; })[0];
      if (target) { select(target, true); }
    }
  });
  post({ kind: 'ready', run: runId });
})();";

        private const string Css = @":root { color-scheme: light dark; }
body { font-family: system-ui, sans-serif; margin: 1rem auto; max-width: 46rem;
       padding: 0 1rem; line-height: 1.5; }
[hidden] { display: none !important; }
button { font: inherit; padding: 0.35rem 0.9rem; }
[role='tablist'] { display: flex; flex-wrap: wrap; gap: 0.4rem; margin: 1rem 0; }
[role='tab'][aria-selected='true'] { font-weight: bold; text-decoration: underline; }
fieldset { margin: 1rem 0; }
pre { white-space: pre-wrap; overflow-x: auto; }
#report { border-top: 1px solid; padding-top: 0.5rem; }";
    }

    /// <summary>
    /// The host's view of run-level state the engine deliberately does not
    /// hold — which tab is up, what the operator declared this run, and any
    /// speakable notice the host wants shown at a stage (a transmit-gate
    /// refusal, typically).
    /// </summary>
    public sealed class FixerPageState
    {
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

        internal string DeclarationAnswerFor(string declId)
            => DeclarationAnswers != null
               && DeclarationAnswers.TryGetValue(declId ?? "", out string v)
                ? (v ?? "") : "";

        internal string NoticeFor(string stageId)
            => StageNotices != null
               && StageNotices.TryGetValue(stageId ?? "", out string v)
                ? (v ?? "") : "";
    }
}
