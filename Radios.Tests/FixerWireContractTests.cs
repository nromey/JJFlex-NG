using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every button on the page reaches the host, and every message the page
    /// can send is one the host understands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written 2026-08-25, after Noel asked the only question that mattered:
    /// "Do we know if these buttons actually work — when they click, do they do
    /// stuff in the application?"</b> The honest answer was no. Every structural
    /// link existed — the page posts, the dialog subscribes, the handler
    /// dispatches, the fix delegates are assigned — and nothing checked that
    /// the two ends agreed on the WORDS.
    /// </para>
    /// <para>
    /// <b>That is the break this file exists to catch, and it fails silently in
    /// every other way.</b> The page emits <c>data-action="fix"</c>; the script
    /// turns that into <c>kind: "apply-fix"</c>; the parser matches
    /// <c>case "apply-fix"</c>. Rename any one of the three and the build still
    /// succeeds, the tests still pass, the page still renders, the button still
    /// looks like a button — and pressing it does nothing at all. No exception,
    /// no log line, no failing assertion. For a blind operator, a button that
    /// silently does nothing is indistinguishable from one that worked.
    /// </para>
    /// <para>
    /// <b>It reads the RENDERED page, not the source.</b> A first attempt
    /// grepped FixerPage.cs for <c>data-action</c> literals and MISSED two of
    /// them — "run" and "rerun" — because those are built by concatenation
    /// rather than written out. The grep therefore reported a break that did
    /// not exist. Rendering is the honest instrument here for the same reason
    /// it is for the prose: it is what actually reaches the operator, with
    /// nothing added by an extractor and nothing missed by one.
    /// </para>
    /// <para>
    /// <b>What this does NOT prove.</b> It does not press anything. It cannot
    /// tell you a fix delegate does the right thing to the radio, or that
    /// WebView2 delivers the message, or that the operator hears a result. It
    /// proves the two ends speak the same language, which was the one part
    /// nothing checked and the one part that breaks silently.
    /// </para>
    /// </remarks>
    public class FixerWireContractTests
    {
        /// <summary>A page with every control on it: stages, fixes, skips, the
        /// declaration and the report.</summary>
        private static string RenderedPage()
        {
            var hosts = new TransmitStageSet.Hosts
            {
                ReadLoadDeclaration = () => "A dummy load",
                ReadAudioSetup = () => new AudioSetupFacts
                {
                    OpenHostApi = "MME",
                    OpenInputDevice = "Microphone (USB Audio Device)",
                    OpenSampleRateHz = 44100,
                    OpenChannels = 2,
                    ConfiguredHostApi = "Windows WASAPI",
                    ConfiguredInputDevice = "Microphone (USB Audio Device)",
                    WasapiAvailable = true,
                    InputDeviceSelected = true,
                    WindowsInputMuted = true,
                    PcAudioOn = false,
                    RemoteRadio = true,
                    MicProfileEmpty = true,
                },
            };

            var run = new FixerRun(TransmitStageSet.Build(hosts));
            run.RunStage(TransmitStageSet.AudioSetup);
            return FixerPage.Render(run, new FixerPageState
            {
                SelectedStageId = TransmitStageSet.AudioSetup,
            });
        }

        private static ISet<string> Matches(string text, string pattern, int group = 1)
            => new HashSet<string>(Regex.Matches(text, pattern).Cast<Match>()
                                        .Select(m => m.Groups[group].Value));

        [Fact]
        public void Every_button_the_page_renders_is_one_the_page_script_handles()
        {
            string html = RenderedPage();

            ISet<string> emitted = Matches(html, "data-action=\"([^\"]+)\"");
            Assert.NotEmpty(emitted);   // a page with no buttons would pass vacuously

            ISet<string> handled = Matches(FixerPage.PageScript, @"action === '([^']+)'");

            string[] orphans = emitted.Except(handled).OrderBy(a => a).ToArray();
            Assert.True(orphans.Length == 0,
                "the page renders buttons its own script ignores, so pressing them does "
                + "nothing at all: " + string.Join(", ", orphans));
        }

        [Fact]
        public void Every_message_the_page_can_send_is_one_the_host_understands()
        {
            string html = RenderedPage();

            // Two sources: kinds the script writes as literals, and kinds the
            // HTML carries in data-kind for the generic "host" buttons.
            ISet<string> posted = Matches(FixerPage.PageScript, @"kind: '([^']+)'");
            foreach (string k in Matches(html, "data-kind=\"([^\"]+)\"")) posted.Add(k);
            Assert.NotEmpty(posted);

            // ONLY UnknownKind counts, and the distinction is the whole point.
            //
            // The first version of this asserted Problem == None and reported
            // five dead buttons that were not dead at all: apply-fix wants a
            // stage and a finding, run-stage wants a stage, declare-load wants
            // an answer. The probe message below carries none of those, so the
            // parser rightly refused them for a MISSING FIELD — which says
            // nothing about whether the kind is recognised.
            //
            // UnknownKind is the failure that means a button is dead: the page
            // posts a word the host has never heard of. A missing field means
            // this test was lazy, and FixerPageMessage's own tests already
            // cover required fields properly.
            //
            // Nearly "fixed" working code on the strength of my own bad
            // instrument, which is the third time today.
            string[] rejected = posted
                .Where(k => k.Length > 0 && FixerPageMessage.Parse(
                    "{\"kind\":\"" + k + "\",\"run\":\"AAA-AAA\"}")
                    .Problem == FixerPageMessage.Fault.UnknownKind)
                .OrderBy(k => k).ToArray();

            Assert.True(rejected.Length == 0,
                "the page can post messages the host will not parse, so those buttons "
                + "are dead on arrival: " + string.Join(", ", rejected));
        }

        [Fact]
        public void The_script_handles_nothing_the_page_never_renders()
        {
            // The reverse direction, and a much softer failure: dead script is
            // only clutter. It is worth knowing because it usually means a
            // control was REMOVED and its handler was left, which is the
            // description-drift shape this project keeps paying for.
            string html = RenderedPage();
            ISet<string> emitted = Matches(html, "data-action=\"([^\"]+)\"");
            ISet<string> handled = Matches(FixerPage.PageScript, @"action === '([^']+)'");

            string[] dead = handled.Except(emitted).OrderBy(a => a).ToArray();
            Assert.True(dead.Length == 0,
                "the page script handles actions nothing renders any more: "
                + string.Join(", ", dead));
        }

        // ---- the focus contract (#365) ----
        //
        // THE SAME SILENT BREAK AS THE BUTTON WIRE, one layer over. After
        // every action the host re-renders the whole document and then names
        // an element for the page to focus, by id. Those ids are built on one
        // side by FixerDialog and on the other by FixerPage, out of separate
        // string literals, and if they stop agreeing NOTHING fails: the
        // script's getElementById returns null, focus falls through to the
        // h1, and the operator is thrown to the top of the page on every
        // press — which is precisely what Noel reported from the bench on
        // 2026-08-28. A build, a render and a page full of working buttons
        // are all compatible with it.

        [Fact]
        public void Every_stage_has_the_heading_id_the_host_focuses_after_a_run()
        {
            string html = RenderedPage();
            FixerStageSet set = TransmitStageSet.Build(null);

            string[] missing = set.Stages
                .Select(s => "stage-h-" + s.Id)
                .Where(id => !html.Contains("id=\"" + id + "\"", StringComparison.Ordinal))
                .ToArray();

            Assert.True(missing.Length == 0,
                "the host focuses these ids after a stage runs and the page renders none "
                + "of them, so every run would drop the operator at the top of the page: "
                + string.Join(", ", missing));
        }

        [Fact]
        public void Every_declaration_has_the_answer_line_id_the_host_focuses()
        {
            string html = RenderedPage();
            FixerStageSet set = TransmitStageSet.Build(null);

            // Run-level declarations and in-card ones alike: the load
            // declaration lives in the declarations region, the hearing
            // declaration (#243) inside stage 0, and answering either one
            // lands the operator on its "You said" line.
            string[] declarations = set.RunDeclarations.Select(d => d.Id)
                .Concat(set.Stages.SelectMany(s => s.Declarations).Select(d => d.Id))
                .Distinct().ToArray();
            Assert.NotEmpty(declarations);

            string[] missing = declarations
                .Select(id => "declared-" + id)
                .Where(id => !html.Contains("id=\"" + id + "\"", StringComparison.Ordinal))
                .ToArray();

            Assert.True(missing.Length == 0,
                "answering a declaration lands focus on these ids and the page renders none "
                + "of them: " + string.Join(", ", missing));
        }

        [Fact]
        public void Every_stage_notice_slot_is_a_live_region()
        {
            // The slot the host writes a REFUSAL into — "something is already
            // running", "that check has already run", what a fix became. It
            // is filled through the receive channel WITHOUT a re-render, on
            // purpose, so nothing but a live region can make it heard. It was
            // an inert paragraph until Sprint 39: written to the page and
            // spoken by nobody.
            string html = RenderedPage();
            FixerStageSet set = TransmitStageSet.Build(null);

            string[] inert = set.Stages
                .Where(s => !Regex.IsMatch(
                    html, "id=\"notice-" + Regex.Escape(s.Id) + "\"[^>]*aria-live=\"polite\""))
                .Select(s => s.Id)
                .ToArray();

            Assert.True(inert.Length == 0,
                "these stages' notice slots are not live regions, so a refusal written into "
                + "them is never announced: " + string.Join(", ", inert));
        }
    }
}
