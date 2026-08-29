using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Section = Radios.Fixer.Evidence.EvidenceSection;

namespace Radios.Fixer
{
    /// <summary>
    /// The run as one continuous document, in both of its forms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two readers, one document. The operator reads it to know what to do —
    /// so it LEADS with what was found and what to do, and a reader who stops
    /// after one screen still has the answer. FlexRadio reads it to decide
    /// whether anything is theirs — so the measurements follow in full, with
    /// our interpretation labelled as ours and placed after the observations
    /// (#217). Length is not the constraint; comprehension from the first
    /// screen is.
    /// </para>
    /// <para>
    /// Two forms from ONE content model, built once: the page renders the HTML
    /// form, and Copy yields the plain-text form for the email — nobody should
    /// have to paste rendered HTML into a mail client and hope. If the forms
    /// could drift apart, an operator and a support engineer would each be
    /// reading a different report with the same test ID on it.
    /// </para>
    /// </remarks>
    public static class FixerReport
    {
        /// <summary>
        /// Results further apart than this get the spread named in the report.
        /// Ten minutes: closer together than that, a setup change between
        /// stages is unlikely to be the story; beyond it, the operator may
        /// well have changed microphones, moved rooms, or come back tomorrow,
        /// and a reader comparing two stages needs to know they are not
        /// snapshots of one moment.
        /// </summary>
        public const int SpreadWorthNamingMinutes = 10;

        /// <summary>The plain-text form — what Copy puts on the clipboard.</summary>
        public static string PlainText(FixerRun run)
            => Evidence.EvidenceReportDocument.PlainText(Build(run));

        /// <summary>
        /// The HTML form, as a fragment for the page's report region. Prose
        /// only — no controls, no tabindex — so however long it grows it costs
        /// zero tab stops. Headings start at the given level so the page can
        /// slot it under its own hierarchy without a skip.
        /// </summary>
        public static string HtmlFragment(FixerRun run, int headingLevel = 3)
            => Evidence.EvidenceReportDocument.HtmlFragment(Build(run), headingLevel);

        // -------- the one content model both forms render --------
        // The model and both renderings live in EvidenceReportDocument, shared
        // with the QSO signal capture report; this class only builds sections.

        private static List<Section> Build(FixerRun run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));

            var sections = new List<Section>();
            IReadOnlyList<FixerStageResult> inRunOrder = run.ResultsInRunOrder;

            // ---- header: the test ID before anything else, because it is the
            // one thing every later conversation about this document needs ----
            var header = new Section();
            // Named by the CHECK. This document's other reader is FlexRadio
            // support, who have no reason to know an in-house product noun —
            // "JJ Flexible Transmit check report" tells them what it is.
            header.Para("JJ Flexible " + run.Set.Name + " test report");
            header.Para("Test ID: " + run.RunId);
            // "Put together" was clunky, and the two stamps sat in one sentence
            // where they are usually seconds apart, which made the second look
            // like padding. Written out, they answer two different questions:
            // when the measurements were taken, and how old this copy is.
            header.Para("The run started at " + Stamp(run.StartedUtc) + ".");
            header.Para("This copy of the report was written at " + Stamp(run.NowUtc) + ".");
            sections.Add(header);

            // ---- what was found, and what to do — first, so a reader who
            // stops after one screen still has the answer ----
            sections.Add(FoundSection(run, inRunOrder));

            // ---- how much of the test was done ----
            sections.Add(CoverageSection(run, inRunOrder));

            // ---- fixes applied mid-run ----
            if (run.FixesApplied.Count > 0)
                sections.Add(FixesSection(run));

            // ---- every stage, in listed order, one continuous document ----
            foreach (FixerStage stage in run.Set.Stages)
                sections.Add(StageSection(stage, run.ResultFor(stage.Id)));

            return sections;
        }

        private static Section FoundSection(FixerRun run, IReadOnlyList<FixerStageResult> results)
        {
            var s = new Section { Title = "What was found, and what to do" };

            var findings = results.SelectMany(r => r.Findings.Select(f => (Result: r, Finding: f)))
                                  .ToList();

            if (results.Count == 0)
            {
                s.Para("No stages have been run yet, so there is nothing to report. Start at "
                     + "the first stage.");
                return s;
            }

            if (findings.Count == 0)
            {
                s.Para("Nothing that ran found a problem it could name. The stage-by-stage "
                     + "detail below says what was actually measured.");
                return s;
            }

            foreach ((FixerStageResult result, FixerFinding f) in findings)
            {
                FixerStage stage = run.Set.Find(result.StageId);
                string where = stage == null ? result.StageId
                    : "Stage " + stage.Number.ToString(CultureInfo.InvariantCulture)
                      + ", " + stage.Title;

                FixerFixRecord fixed_ = run.FixesApplied.LastOrDefault(x =>
                    x.Succeeded
                    && string.Equals(x.StageId, result.StageId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.FindingId, f.Id, StringComparison.OrdinalIgnoreCase));

                string line = where + ": " + f.WhatIsWrong + " ";
                if (fixed_ != null)
                    line += "FIXED during this run at " + Stamp(fixed_.AtUtc) + " "
                                                                              + "— "
                                                                              + "after "
                                                                              + "the "
                                                                              + "fix: "
                          + fixed_.WhatItBecame;
                else
                    line += f.Owner switch
                    {
                        FixOwner.Us => "JJ Flexible offers a one-press fix for this ("
                                       + f.WhatToDo + ").",
                        FixOwner.Operator => "What to do: " + f.WhatToDo,
                        _ => f.WhatToDo,
                    };
                s.Bullet(line);
            }
            return s;
        }

        private static Section CoverageSection(FixerRun run, IReadOnlyList<FixerStageResult> results)
        {
            var s = new Section { Title = "How much of the test was done" };

            if (results.Count > 0)
            {
                // The actual order, which is usually not the listed order.
                s.Para("The stages were done in this order: "
                    + string.Join(", then ", results.Select(r => Describe(run, r))) + ".");
            }

            var notAttempted = run.Set.Stages.Where(st => run.ResultFor(st.Id) == null).ToList();
            if (notAttempted.Count > 0)
            {
                s.Para("Not attempted at all: "
                    + string.Join("; ", notAttempted.Select(st =>
                        "stage " + st.Number.ToString(CultureInfo.InvariantCulture)
                        + " (" + st.Title + ")"))
                    + ". That weakens the overall answer: each stage rules something in "
                    + "or out, and the stages after it depend on knowing that.");
            }

            foreach (FixerStageResult r in results.Where(r => r.Status == FixerStageStatus.Skipped))
            {
                FixerStage st = run.Set.Find(r.StageId);
                s.Para("Stage " + (st?.Number.ToString(CultureInfo.InvariantCulture) ?? r.StageId)
                    + (st != null ? " (" + st.Title + ")" : "")
                    + " was not run. The reason given: \"" + r.Skip.Label + "\" "
                    + r.Skip.EffectText);
            }

            // Results far apart in time cannot be read as one snapshot.
            var ran = results.Where(r => r.Status == FixerStageStatus.Ran)
                             .OrderBy(r => r.AtUtc).ToList();
            if (ran.Count >= 2)
            {
                FixerStageResult oldest = ran.First(), newest = ran.Last();
                double minutes = (newest.AtUtc - oldest.AtUtc).TotalMinutes;
                if (minutes >= SpreadWorthNamingMinutes)
                {
                    s.Para("These results span " + Math.Round(minutes)
                        .ToString(CultureInfo.InvariantCulture)
                        + " minutes: the oldest is " + Describe(run, oldest) + " at "
                        + Stamp(oldest.AtUtc) + " and the newest is " + Describe(run, newest)
                        + " at " + Stamp(newest.AtUtc) + ". Things may have changed in "
                        + "between — a microphone swapped, a setting altered — so do not read "
                        + "them as one snapshot.");
                }
            }

            if (s.Items.Count == 0)
                s.Para("Nothing has been done yet.");
            return s;
        }

        private static Section FixesSection(FixerRun run)
        {
            var s = new Section { Title = "Changes made during this run" };
            s.Para("These settings were changed while the test was running, each one offered "
                 + "on the page and applied on a press — never silently. Results recorded "
                 + "after a change describe the changed setup, not the one the run started "
                 + "with.");

            foreach (FixerFixRecord fix in run.FixesApplied)
            {
                FixerStage st = run.Set.Find(fix.StageId);
                string line = Stamp(fix.AtUtc) + " — "
                    + (st != null ? "stage " + st.Number.ToString(CultureInfo.InvariantCulture)
                                    + " (" + st.Title + ")" : fix.StageId) + ": "
                    + fix.WhatWasWrong + " ";
                line += fix.Succeeded
                    ? "After the fix: " + fix.WhatItBecame
                    : "The fix was attempted and DID NOT succeed: " + fix.WhatItBecame;

                var after = run.ResultsAfter(fix);
                if (after.Count > 0)
                    line += " Stages recorded after this change: "
                         + string.Join(", ", after.Select(r => Describe(run, r))) + ".";
                s.Bullet(line);
            }
            return s;
        }

        private static Section StageSection(FixerStage stage, FixerStageResult result)
        {
            var s = new Section
            {
                Title = "Stage " + stage.Number.ToString(CultureInfo.InvariantCulture)
                      + ": " + stage.Title,
            };

            s.Para(stage.Question);

            if (result == null)
            {
                s.Para("This stage has not been run.");
                return s;
            }

            switch (result.Status)
            {
                case FixerStageStatus.Skipped:
                    // Unmistakably not a pass: leads with "Not run", carries
                    // the reason, and shows no measurement.
                    s.Paras(result.Answer);
                    return s;

                case FixerStageStatus.CouldNotRun:
                    s.Para("Attempted at " + Stamp(result.AtUtc) + " and could not run.");
                    s.Paras(result.Answer);
                    return s;
            }

            s.Para("Run at " + Stamp(result.AtUtc)
                + (result.WasReRun
                    ? ". This stage was re-run; this result replaces an earlier one."
                    : "."));
            s.Paras(result.Answer);

            foreach (FixerFinding f in result.Findings)
            {
                s.Bullet(f.WhatIsWrong + " " + f.Owner switch
                {
                    FixOwner.Us => "JJ Flexible offers a one-press fix for this ("
                                   + f.WhatToDo + ").",
                    FixOwner.Operator => "What to do: " + f.WhatToDo,
                    _ => f.WhatToDo,
                });
            }

            if (result.Evidence.Length > 0)
                s.Pre(result.Evidence);

            return s;
        }

        // -------- helpers --------

        private static string Describe(FixerRun run, FixerStageResult r)
        {
            FixerStage st = run.Set.Find(r.StageId);
            string name = st == null ? r.StageId
                : "stage " + st.Number.ToString(CultureInfo.InvariantCulture)
                  + " (" + st.Title + ")";
            return r.Status switch
            {
                FixerStageStatus.Skipped => name + ", skipped",
                FixerStageStatus.CouldNotRun => name + ", could not run",
                _ => name,
            };
        }

        private static string Stamp(DateTime utc)
            => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }
}
