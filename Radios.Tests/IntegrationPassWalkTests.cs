using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Radios.Fixer;
using Xunit;
using static Radios.Tests.IntegrationPass;

namespace Radios.Tests
{
    /// <summary>
    /// Pass 2 of the integration pass: the blind end-to-end walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not a code review, and the difference is the whole point.</b>
    /// An absence is invisible in a diff. "No stage offers a way to the next
    /// one" appears in no file, because it is a gap across a whole page — every
    /// individual file is correct and the composition is not. A reviewer
    /// reading the diff passes all of it, which is what happened on 2026-08-25:
    /// every component was individually right, every automated test passed, and
    /// a real operator found fourteen things in one evening.
    /// </para>
    /// <para>
    /// The question at every state is: <b>what can a person do next from here,
    /// and how would they know?</b>
    /// </para>
    /// <para>
    /// <b>It reads the RENDERED page, never the source.</b> Same reason
    /// <c>FixerWireContractTests</c> does: a source grep for
    /// <c>data-action</c> missed two values that are built by concatenation and
    /// reported a break that did not exist. Rendering is the honest instrument
    /// because it is what actually reaches the operator, with nothing added by
    /// an extractor and nothing missed by one.
    /// </para>
    /// <para>
    /// <b>Stages are found by their HEADING, not by a container id.</b> Track A
    /// is turning this page from a tablist into one document, so
    /// <c>panel-&lt;id&gt;</c> may not survive the sprint; an h2 per stage will,
    /// because heading navigation is how a screen reader user moves through it.
    /// Segmenting on the thing the operator navigates by is both more honest
    /// and more durable than segmenting on markup that happens to be there
    /// today.
    /// </para>
    /// </remarks>
    public class IntegrationPassWalkTests
    {
        // ═══════════════════════════════════════════════════════════════
        //  Reading the page
        // ═══════════════════════════════════════════════════════════════

        /// <summary>One rendered control: its attributes and its visible text.</summary>
        private sealed record Control(string Tag, IReadOnlyDictionary<string, string> Attributes,
                                      string Text, int At)
        {
            internal string Attr(string name)
                => Attributes.TryGetValue(name, out string? v) ? v : "";
        }

        private static readonly Regex ElementRx = new(
            @"<(button|a|input|select|textarea)\b([^>]*?)(/?)>(?:(.*?)</\1>)?",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex AttrRx = new(@"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*""([^""]*)""");

        private static readonly Regex HeadingRx = new(
            @"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static IReadOnlyList<Control> ControlsIn(string html)
            => ElementRx.Matches(html).Select(m => new Control(
                    m.Groups[1].Value.ToLowerInvariant(),
                    AttrRx.Matches(m.Groups[2].Value)
                          .ToDictionary(a => a.Groups[1].Value.ToLowerInvariant(),
                                        a => a.Groups[2].Value),
                    Strip(m.Groups[4].Value),
                    m.Index))
                .ToList();

        private static string Strip(string html)
            => Regex.Replace(html ?? "", "<[^>]*>", " ")
                    .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"")
                    .Trim();

        /// <summary>
        /// The slice of the document belonging to one stage: from that stage's
        /// heading to the next heading at the same level or higher. This is
        /// exactly the span a screen reader user moves through after pressing H
        /// once, which is why it is the right unit for "what can I do from
        /// here".
        /// </summary>
        private static string SegmentFor(string html, FixerStage stage)
        {
            MatchCollection headings = HeadingRx.Matches(html);
            for (int i = 0; i < headings.Count; i++)
            {
                string text = Strip(headings[i].Groups[2].Value);
                if (text.IndexOf(stage.Title, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (text.IndexOf("report", StringComparison.OrdinalIgnoreCase) >= 0
                    && text.IndexOf(stage.Title, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int level = int.Parse(headings[i].Groups[1].Value);
                int start = headings[i].Index;
                for (int j = i + 1; j < headings.Count; j++)
                    if (int.Parse(headings[j].Groups[1].Value) <= level)
                        return html.Substring(start, headings[j].Index - start);
                return html.Substring(start);
            }
            return "";
        }

        // ═══════════════════════════════════════════════════════════════
        //  The instrument has to work before its silence means anything
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Positive control for the whole walk. Every rule below reports by
        /// staying quiet, so a parser that reads nothing would report a perfect
        /// page. This is the one test that fails when the reader breaks.
        /// </summary>
        [Fact]
        public void The_walk_can_actually_read_the_page()
        {
            foreach (FixerReviewState state in FixerStates.All())
            {
                string html = state.Html;

                IReadOnlyList<Control> controls = ControlsIn(html);
                Assert.True(controls.Count > 10,
                    state.Name + ": only " + controls.Count + " controls were parsed out of "
                    + html.Length + " characters of markup, so every rule in this file is "
                    + "looking at an empty page.");

                Assert.True(controls.Any(c => c.Tag == "button" && c.Attr("data-action").Length > 0),
                    state.Name + ": no button carrying a data-action was found, so the walk "
                    + "cannot see what the operator can do.");

                Assert.True(HeadingRx.Matches(html).Count >= 3,
                    state.Name + ": fewer than three headings were parsed, so stage segmentation "
                    + "has stopped working.");

                foreach (FixerStage stage in state.Run.Set.Stages)
                    Assert.True(SegmentFor(html, stage).Length > 0,
                        state.Name + ": no heading was found for stage \"" + stage.Title
                        + "\". Either the page has stopped giving each stage a heading — which "
                        + "would break heading navigation for the operator — or this walk has "
                        + "stopped being able to find them, and would then report every "
                        + "per-stage rule as clean.");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  The rules the evening of 2026-08-25 wrote for us
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// No stage offers a way to discard its own measurement once it has one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The skip control was rendered on a stage that had already passed.
        /// Pressing it there does not "skip" anything — the stage already ran —
        /// it replaces a measurement with a skip record, silently. On stages 2
        /// to 4 the measurement it discards was paid for with RF.
        /// </para>
        /// <para>
        /// <b>Scoped to stages that RAN, deliberately.</b> A stage already
        /// skipped has a result too, and offering skip again there merely lets
        /// the operator change their stated reason, which is not destructive.
        /// Widening this rule to every result would make it flag a control that
        /// is doing something useful.
        /// </para>
        /// </remarks>
        [Fact]
        public void No_stage_offers_to_discard_a_measurement_it_has_already_taken()
        {
            Gate(Rules.SkipAfterResult,
                 "A finished step must not still offer the control that throws its result away.",
                 FixerStates.All().SelectMany(
                     s => SkipAfterResultFindings(s.Name, s.Html, s.Run)));
        }

        private static IEnumerable<Finding> SkipAfterResultFindings(
            string stateName, string html, FixerRun run)
        {
            IReadOnlyList<Control> controls = ControlsIn(html);

            foreach (FixerStage stage in run.Set.Stages)
            {
                FixerStageResult? result = run.ResultFor(stage.Id);
                if (result?.Status != FixerStageStatus.Ran) continue;

                bool offered = controls.Any(
                    c => c.Attr("data-action") == "skip"
                      && string.Equals(c.Attr("data-arg"), stage.Id, StringComparison.OrdinalIgnoreCase));
                if (!offered) continue;

                yield return new Finding(Rules.SkipAfterResult, stateName + "/" + stage.Id,
                    "Stage " + stage.Number + " (" + stage.Title + ") has a result and still "
                    + "renders a skip control. Pressing it replaces the measurement with a "
                    + "skip record and says nothing"
                    + (stage.Transmits ? ", and this stage keys the radio, so what it "
                                       + "discards cost a transmission." : "."));
            }
        }

        /// <summary>
        /// Every stage says where to go next.
        /// </summary>
        /// <remarks>
        /// An operator who has just finished a stage needs the next move to be
        /// present where they are standing, not inferable from a tablist they
        /// would have to leave the stage to reach. The check is deliberately
        /// generous about HOW — a button, a link, an anchor — because Track A
        /// is changing the mechanism this sprint and the requirement is that
        /// something offers the move, not that it is a particular element.
        /// </remarks>
        [Fact]
        public void Every_stage_offers_a_way_on_to_the_next_one()
        {
            Gate(Rules.ForwardAffordance,
                 "Every step should carry the next one with it. A page where the way on exists "
                 + "somewhere else is a page an operator stops halfway down.",
                 FixerStates.All().SelectMany(
                     s => ForwardAffordanceFindings(s.Name, s.Html, s.Run.Set.Stages)));
        }

        private static IEnumerable<Finding> ForwardAffordanceFindings(
            string stateName, string html, IReadOnlyList<FixerStage> stages)
        {
            for (int i = 0; i < stages.Count - 1; i++)
            {
                FixerStage here = stages[i], next = stages[i + 1];
                string segment = SegmentFor(html, here);

                bool onward = ControlsIn(segment).Any(
                    c => Mentions(c.Attr("data-arg"), next.Id)
                      || Mentions(c.Attr("data-stage"), next.Id)
                      || Mentions(c.Attr("href"), next.Id));
                if (onward) continue;

                // A stage that has not run yet offers its way past DELIBERATELY,
                // and it is the skip fieldset: choose a reason, and the report
                // can then say what it is unable to conclude and why. A bare
                // "Next" beside it would be a way to walk the whole chain
                // without ever recording that anything was missed — which is
                // the one thing that quietly ruins the report.
                //
                // So a skip declaration counts as a way on. This narrows the
                // rule to what its own remark above already says: the stage
                // that MUST carry the next one is the stage you have FINISHED.
                // #249's fix means a completed stage never renders skip, so a
                // finished stage still has to produce a real forward control.
                // A stage with NEITHER is a dead end and is still reported.
                //
                // Settled in the Sprint 35 merge, where this rule and
                // FixerPageTests.An_unrun_stage_offers_run_not_next asserted
                // opposite things. Neither track could see the other.
                bool canDeclareSkip = ControlsIn(segment).Any(
                    c => c.Attr("name").StartsWith("skipwhy-", StringComparison.Ordinal));
                if (canDeclareSkip) continue;

                yield return new Finding(Rules.ForwardAffordance, stateName + "/" + here.Id,
                    "Nothing within stage " + here.Number + " (" + here.Title + ") offers the "
                    + "move to stage " + next.Number + " (" + next.Title + "). The operator "
                    + "has to work out for themselves that there is more, and where it is.");
            }
        }

        private static bool Mentions(string attribute, string stageId)
            => attribute.IndexOf(stageId, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Heading levels step by one.
        /// </summary>
        /// <remarks>
        /// A skipped level tells a screen reader user there is a container they
        /// missed. It is the cheapest structural lie a page can tell, and the
        /// page is being restructured this sprint by a track whose whole design
        /// is "h2 per stage, h3 only for Findings" — so this is the assertion
        /// that keeps that promise honest after the fact.
        /// </remarks>
        [Fact]
        public void Heading_levels_never_skip()
        {
            Gate(Rules.HeadingLevels,
                 "Heading levels are the page's structure spoken aloud; a skipped level is a "
                 + "container the reader is told about and cannot reach.",
                 FixerStates.All().SelectMany(s => HeadingLevelFindings(s.Name, s.Html)));
        }

        private static IEnumerable<Finding> HeadingLevelFindings(string stateName, string html)
        {
            int previous = 0;
            foreach (Match m in HeadingRx.Matches(html))
            {
                int level = int.Parse(m.Groups[1].Value);
                string text = Strip(m.Groups[2].Value);

                if (previous > 0 && level > previous + 1)
                    yield return new Finding(Rules.HeadingLevels,
                        stateName + "/h" + level + ":" + text,
                        "an h" + level + " (\"" + text + "\") follows an h" + previous
                        + ", so a level is missing. A screen reader user hears a container "
                        + "they cannot get to.");

                previous = level;
            }
        }

        /// <summary>
        /// Everything operable says what it is.
        /// </summary>
        /// <remarks>
        /// A button whose accessible name is empty is announced as "button",
        /// which for a blind operator is indistinguishable from a button that
        /// does nothing. Inputs are held to the stricter rule — a real
        /// <c>label for=</c>, because that is what also makes the label a click
        /// target and what the page's own structural tests already assume.
        /// </remarks>
        [Fact]
        public void Every_operable_control_has_a_name()
        {
            Gate(Rules.UnnamedControl,
                 "Anything the operator can act on has to say what it is, because for a blind "
                 + "operator an unnamed control and a dead one sound identical.",
                 FixerStates.All().SelectMany(s => UnnamedControlFindings(s.Name, s.Html)));
        }

        private static IEnumerable<Finding> UnnamedControlFindings(string stateName, string html)
        {
            var labelled = new HashSet<string>(
                Regex.Matches(html, @"<label\b[^>]*\bfor=""([^""]+)""")
                     .Select(m => m.Groups[1].Value), StringComparer.Ordinal);

            foreach (Control c in ControlsIn(html))
            {
                if (c.Tag == "a") continue;               // links carry their own text
                string named = c.Text.Length > 0 ? c.Text : c.Attr("aria-label");

                if (c.Tag == "input" || c.Tag == "select" || c.Tag == "textarea")
                {
                    string id = c.Attr("id");
                    if (id.Length > 0 && labelled.Contains(id)) continue;
                    if (named.Length > 0) continue;
                    yield return new Finding(Rules.UnnamedControl,
                        stateName + "/" + c.Tag + ":" + (id.Length > 0 ? id : "at " + c.At),
                        "an " + c.Tag + " with no label associated to it and no aria-label; "
                        + "a screen reader announces its type and nothing else.");
                    continue;
                }

                if (named.Length > 0) continue;
                yield return new Finding(Rules.UnnamedControl,
                    stateName + "/button:" + (c.Attr("data-action").Length > 0
                                              ? c.Attr("data-action") + ":" + c.Attr("data-arg")
                                              : "at " + c.At),
                    "a button with no text and no aria-label. It is announced as \"button\", "
                    + "which is what a broken button sounds like too.");
            }
        }

        /// <summary>
        /// Nothing that is only prose takes a tab stop.
        /// </summary>
        /// <remarks>
        /// The web surface exists so that explanation costs zero tab stops — a
        /// paragraph made focusable throws that away and puts a stop in the
        /// operator's path that does nothing when they arrive at it.
        /// </remarks>
        [Fact]
        public void Nothing_focusable_is_only_prose()
        {
            Gate(Rules.FocusableProse,
                 "A tab stop is a promise that something happens there.",
                 FixerStates.All().SelectMany(s => FocusableProseFindings(s.Name, s.Html)));
        }

        private static IEnumerable<Finding> FocusableProseFindings(string stateName, string html)
        {
            string[] operable = { "button", "a", "input", "select", "textarea", "details", "summary" };

            foreach (Match m in Regex.Matches(html, @"<([a-zA-Z][-a-zA-Z0-9]*)\b([^>]*)>"))
            {
                string tag = m.Groups[1].Value.ToLowerInvariant();
                string attrs = m.Groups[2].Value;
                if (!Regex.IsMatch(attrs, @"\btabindex\s*=\s*""0""")) continue;
                if (operable.Contains(tag)) continue;
                if (Regex.IsMatch(attrs, @"\brole\s*=\s*""(tab|button|link|checkbox|radio)""")) continue;

                yield return new Finding(Rules.FocusableProse,
                    stateName + "/" + tag + " at " + m.Index,
                    "<" + tag + "> takes a tab stop without being operable. Explanatory text "
                    + "on this page is meant to cost nothing to tab past.");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Proof that the quiet rules can speak
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Every rule above now finds nothing on the real page. This is what
        /// makes that silence worth anything.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A detector that never fires and a clean page produce identical
        /// output.</b> Until #249 closed in the Sprint 35 merge, the skip rule
        /// carried its own control — six real findings, every run. The fix
        /// took them away, which is the point of a fix, and quietly demoted
        /// that rule to the same standing as the other four: a pure negative
        /// result, which also claims the instrument WOULD have seen it. This
        /// remark said the skip rule "needs no control of this kind" for a
        /// day after that stopped being true — drift in the file about drift —
        /// so the skip rule is now handed a broken page like everybody else.
        /// </para>
        /// <para>
        /// So they are handed pages built to be wrong in exactly the ways
        /// each rule exists to catch: an h2 followed by an h4, a paragraph
        /// holding a tab stop, a button with no text, an input with no label,
        /// no route from the first stage to the second, and a skip control on
        /// a stage that has already measured. Each rule must report its own
        /// fault and, just as importantly, must not report anybody else's.
        /// </para>
        /// </remarks>
        [Fact]
        public void Each_quiet_rule_reports_a_page_built_to_break_it()
        {
            const string broken = @"<!doctype html><html><body>
<h1>Transmit tests</h1>
<h2>Stage 0: Alpha</h2>
<p tabindex=""0"">Prose that has taken a tab stop it cannot use.</p>
<button type=""button"" data-action=""run"" data-arg=""alpha""></button>
<h4>Stage 1: Bravo</h4>
<input type=""radio"" id=""orphan"">
</body></html>";

            var stages = new List<FixerStage>
            {
                new FixerStage { Id = "alpha", Number = 0, Title = "Alpha" },
                new FixerStage { Id = "bravo", Number = 1, Title = "Bravo" },
            };

            Finding[] onward = ForwardAffordanceFindings("broken", broken, stages).ToArray();
            Assert.True(onward.Length == 1,
                "the forward-affordance rule reported " + onward.Length + " finding(s) on a page "
                + "with no route from Alpha to Bravo. Its silence on the real page means nothing "
                + "until it can say this.");
            Assert.Equal("broken/alpha", onward[0].Where);

            Finding[] headings = HeadingLevelFindings("broken", broken).ToArray();
            Assert.True(headings.Length == 1,
                "the heading rule reported " + headings.Length + " finding(s) on a page whose h2 "
                + "is followed by an h4.");
            Assert.Contains("h4", headings[0].Where);

            Finding[] unnamed = UnnamedControlFindings("broken", broken).ToArray();
            Assert.True(unnamed.Length == 2,
                "the naming rule reported " + unnamed.Length + " finding(s) on a page with one "
                + "empty button and one unlabelled input: "
                + string.Join("; ", unnamed.Select(f => f.Where)));
            Assert.Contains(unnamed, f => f.Where.Contains("button"));
            Assert.Contains(unnamed, f => f.Where.Contains("orphan"));

            Finding[] prose = FocusableProseFindings("broken", broken).ToArray();
            Assert.True(prose.Length == 1,
                "the focusable-prose rule reported " + prose.Length + " finding(s) on a page with "
                + "a tabindex on a paragraph.");
            Assert.StartsWith("broken/p", prose[0].Where);

            // AND THE OTHER HALF OF THE CONTROL: a page that is right must come
            // back clean from all four, or "reports something" would be all any
            // of them can do.
            const string sound = @"<!doctype html><html><body>
<h1>Transmit tests</h1>
<h2>Stage 0: Alpha</h2>
<p>Prose that costs nothing to tab past.</p>
<button type=""button"" data-action=""select"" data-arg=""bravo"">On to Stage 1: Bravo</button>
<h2>Stage 1: Bravo</h2>
<p><input type=""radio"" id=""why""> <label for=""why"">A reason</label></p>
</body></html>";

            Assert.Empty(ForwardAffordanceFindings("sound", sound, stages));
            // AND THE BOUNDARY OF THE NARROWING: a stage whose only forward
            // motion is the skip fieldset PASSES, because declaring a reason
            // for moving on is the honest way past an unmeasured stage. The
            // "broken" page above is the other half of this control — it has
            // neither a next control nor a skip, and is still reported. If
            // this pair ever both pass or both fail, the rule has stopped
            // discriminating and is no longer worth reading.
            const string skipOnly = @"<!doctype html><html><body>
<h1>Transmit tests</h1>
<h2>Stage 0: Alpha</h2>
<button type=""button"" data-action=""run"" data-arg=""alpha"">Run this check</button>
<fieldset class=""skip""><legend>Why are you skipping this stage?</legend>
<p><input type=""radio"" name=""skipwhy-alpha"" id=""s1"" value=""nogear"">
 <label for=""s1"">I do not have the equipment</label></p>
</fieldset>
<h2>Stage 1: Bravo</h2>
</body></html>";

            Assert.Empty(ForwardAffordanceFindings("skip-only", skipOnly, stages));

            Assert.Empty(HeadingLevelFindings("sound", sound));
            Assert.Empty(UnnamedControlFindings("sound", sound));
            Assert.Empty(FocusableProseFindings("sound", sound));

            // THE SKIP RULE'S CONTROL, added when #249's fix took away the six
            // real findings that used to prove it could see. A run whose first
            // stage has genuinely measured, rendered by a page that still
            // offers to skip that stage — the exact composition the real page
            // produced until the Sprint 35 merge, and the rule must still be
            // able to say so.
            FixerRun measured = new FixerRun(
                FixerTestKit.Kettle(FixerTestKit.Answering("Yes — wet.")));
            measured.RunStage("fill");

            const string skipAfterResult = @"<!doctype html><html><body>
<h1>Kettle tests</h1>
<h2>Stage 0: Fill</h2>
<p>Yes — wet.</p>
<fieldset class=""skip""><legend>Why are you skipping this stage?</legend>
<p><input type=""radio"" name=""skipwhy-fill"" id=""k1"" value=""later"">
 <label for=""k1"">I'll do it later.</label></p>
<button type=""button"" data-action=""skip"" data-arg=""fill"">Skip this stage</button>
</fieldset>
<h2>Stage 1: Boil</h2>
</body></html>";

            Finding[] discarding =
                SkipAfterResultFindings("broken", skipAfterResult, measured).ToArray();
            Assert.True(discarding.Length == 1,
                "the skip rule reported " + discarding.Length + " finding(s) on a page that "
                + "offers to skip a stage with a recorded measurement. Its silence on the "
                + "real page means nothing until it can say this.");
            Assert.Equal("broken/fill", discarding[0].Where);

            // And the other half: the REAL renderer, on the same run, no
            // longer offers that control — which is #249's fix, seen by the
            // same instrument that would catch its return.
            Assert.Empty(SkipAfterResultFindings("sound", FixerPage.Render(measured), measured));
        }
    }
}
