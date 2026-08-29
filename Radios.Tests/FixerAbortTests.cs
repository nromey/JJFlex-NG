using System;
using System.Linq;
using Radios.ChainChecks;
using Xunit;
using static Radios.ChainChecks.FixerAbort;

namespace Radios.Tests
{
    /// <summary>
    /// Stopping the Fixer Tool, keyed and unkeyed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property under test is one sentence: <b>if the transmitter is
    /// keyed, nothing happens before the carrier comes down.</b> Not a prompt,
    /// not a question, not a confirmation. Everything else here is detail.
    /// </para>
    /// <para>
    /// This inverts the usual destructive-actions-confirm rule, deliberately.
    /// While transmitting, the dangerous thing is the delay rather than the
    /// action — a prompt between the operator and stopping their own
    /// transmission keeps RF going out while something waits for an answer.
    /// </para>
    /// </remarks>
    public class FixerAbortTests
    {
        private static readonly Source[] AllSources =
            (Source[])Enum.GetValues(typeof(Source));

        // ---- the invariant ----

        [Fact]
        public void Keyed_always_unkeys_first_whatever_asked_and_whatever_else_is_true()
        {
            // Exhaustive over every source and both run states. If this ever
            // fails, some path exists where a carrier stays up while the app
            // asks a question.
            foreach (Source s in AllSources)
            foreach (bool running in new[] { true, false })
            {
                Plan p = Decide(keyed: true, source: s, runInProgress: running);
                Assert.True(p.UnkeysFirst,
                    s + " with run=" + running + " did not unkey first");
                Assert.Equal(Step.UnkeyImmediately, p.Steps[0]);
            }
        }

        [Fact]
        public void Keyed_never_asks_before_unkeying()
        {
            // Stronger than "unkeys first": no question may appear anywhere
            // ahead of the unkey, including as a step zero someone adds later.
            foreach (Source s in AllSources)
            foreach (bool running in new[] { true, false })
            {
                Plan p = Decide(true, s, running);
                int unkey = Array.IndexOf(p.Steps, Step.UnkeyImmediately);
                int ask = Array.IndexOf(p.Steps, Step.AskAbandonOrContinue);
                if (ask >= 0)
                    Assert.True(unkey < ask, s + ": asked before unkeying");
            }
        }

        [Fact]
        public void Unkeyed_never_unkeys()
        {
            // Nothing to stop. An unkey step here would be a no-op at best and
            // a confusing announcement at worst.
            foreach (Source s in AllSources)
            foreach (bool running in new[] { true, false })
                Assert.DoesNotContain(Step.UnkeyImmediately,
                                      Decide(false, s, running).Steps);
        }

        // ---- the asymmetry, stated plainly ----

        [Fact]
        public void Keyed_with_a_run_going_unkeys_then_asks()
        {
            Plan p = Decide(keyed: true, Source.EscapeKey, runInProgress: true);
            Assert.Equal(new[] { Step.UnkeyImmediately, Step.AskAbandonOrContinue },
                         p.Steps);
        }

        [Fact]
        public void Unkeyed_with_a_run_going_only_offers()
        {
            // A multi-stage run is real investment; a stray keypress must not
            // throw it away.
            Plan p = Decide(keyed: false, Source.EscapeKey, runInProgress: true);
            Assert.Equal(new[] { Step.AskAbandonOrContinue }, p.Steps);
            Assert.True(p.Asks);
        }

        [Fact]
        public void Keyed_with_nothing_running_just_stops()
        {
            // There is no run to abandon, so asking would be a question with
            // one sensible answer.
            Plan p = Decide(keyed: true, Source.StopButton, runInProgress: false);
            Assert.Equal(new[] { Step.UnkeyImmediately }, p.Steps);
            Assert.False(p.Asks);
        }

        [Fact]
        public void Unkeyed_with_nothing_running_closes_without_ceremony()
        {
            Plan p = Decide(keyed: false, Source.StopButton, runInProgress: false);
            Assert.Equal(new[] { Step.AbandonNow }, p.Steps);
            Assert.False(p.Asks);
        }

        // ---- window closing is the one case that cannot wait for an answer ----

        [Fact]
        public void Closing_the_window_while_keyed_unkeys_and_abandons_without_asking()
        {
            // There is nowhere to put the answer. Ask nothing, but the carrier
            // still comes down first.
            Plan p = Decide(keyed: true, Source.WindowClosing, runInProgress: true);
            Assert.Equal(new[] { Step.UnkeyImmediately, Step.AbandonNow }, p.Steps);
            Assert.False(p.Asks);
        }

        [Fact]
        public void Closing_the_window_unkeyed_still_asks_before_losing_the_run()
        {
            Plan p = Decide(keyed: false, Source.WindowClosing, runInProgress: true);
            Assert.True(p.Asks);
        }

        // ---- recorded results are never discarded silently (#250) ----

        [Fact]
        public void Closing_with_recorded_results_asks_and_names_what_it_discards()
        {
            // The quiet moment between stages: no stage running, no carrier,
            // three measurements in the run. Closing here used to discard all
            // three without a word — and on the transmitting stages each one
            // was paid for with RF.
            Plan p = Decide(keyed: false, Source.WindowClosing, runInProgress: false,
                            resultsCollected: 3);

            Assert.Equal(new[] { Step.AskAbandonOrContinue }, p.Steps);
            Assert.Equal("This test has recorded three results. Ending it now discards "
                       + "them. Abandon the test?", p.Announcement);
        }

        [Fact]
        public void Stopping_with_recorded_results_asks_and_names_what_it_discards()
        {
            Plan p = Decide(keyed: false, Source.StopButton, runInProgress: false,
                            resultsCollected: 1);

            Assert.Equal(new[] { Step.AskAbandonOrContinue }, p.Steps);
            Assert.Equal("This test has recorded one result. Ending it now discards "
                       + "it. Do you want to stop the test?", p.Announcement);
        }

        [Fact]
        public void A_stop_mid_stage_still_names_the_results_already_recorded()
        {
            Plan viaButton = Decide(keyed: false, Source.EscapeKey, runInProgress: true,
                                    resultsCollected: 2);
            Assert.Equal("This test has recorded two results. Ending it now discards "
                       + "them. Do you want to stop the test?", viaButton.Announcement);

            Plan viaClose = Decide(keyed: false, Source.WindowClosing, runInProgress: true,
                                   resultsCollected: 2);
            Assert.Equal("The test has not finished, and it has recorded two results. "
                       + "Ending it now discards them. Abandon it?", viaClose.Announcement);
        }

        [Fact]
        public void When_results_are_kept_the_question_says_saved_and_never_discarded()
        {
            // The fate claim rides the fact. The persistence layer journals
            // runs as they go WHEN it set up — so a caller whose journal is
            // live passes true and the question stops threatening data loss
            // that will not happen. It still asks: ending a run mid-way ends
            // the session either way. And because the ask is now presented
            // as labelled choices (#376), the kept question is open — it
            // states the situation, names the door back in, and asks what to
            // do rather than posing a yes-or-no over a compound sentence.
            Plan p = Decide(keyed: false, Source.WindowClosing, runInProgress: false,
                            resultsCollected: 3, resultsAreKept: true);
            Assert.Equal("This test has recorded three results, and they are saved "
                       + "under its test ID. A stopped test can be picked up later "
                       + "from Saved check runs, on the Fix menu. What would you "
                       + "like to do?", p.Announcement);

            foreach (Source s in AllSources)
            foreach (bool running in new[] { true, false })
            {
                Plan kept = Decide(false, s, running, resultsCollected: 2,
                                   resultsAreKept: true);
                Assert.DoesNotContain("discard", kept.Announcement,
                                      StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_single_kept_result_is_spoken_in_the_singular()
        {
            Plan p = Decide(keyed: false, Source.EscapeKey, runInProgress: false,
                            resultsCollected: 1, resultsAreKept: true);
            Assert.Contains("one result, and it is saved", p.Announcement);
        }

        // ---- the resume-later offer rides two facts, never fewer (#376) ----

        [Fact]
        public void Resume_later_is_offered_exactly_when_kept_results_meet_an_ask()
        {
            // The third choice — stop now, pick the run up later from the
            // saved list — was fully built and never offered. It may only be
            // offered over a run that is genuinely persisting AND holds at
            // least one result: anything less is a promise with a button on
            // it and nothing behind it.
            foreach (Source s in AllSources)
            foreach (bool keyed in new[] { true, false })
            foreach (bool running in new[] { true, false })
            foreach (int results in new[] { 0, 1, 3 })
            foreach (bool kept in new[] { true, false })
            {
                Plan p = Decide(keyed, s, running, results, kept);
                if (p.OffersResumeLater)
                {
                    Assert.True(p.Asks, "offered resume without asking anything");
                    Assert.True(kept, "offered resume over nothing persisting");
                    Assert.True(results > 0, "offered resume over an empty run");
                }
                if (p.Asks && kept && results > 0)
                    Assert.True(p.OffersResumeLater,
                        s + " keyed=" + keyed + " running=" + running
                        + ": a kept run with results asked without offering resume");
            }
        }

        [Fact]
        public void A_kept_ask_names_the_door_back_in()
        {
            // An option the operator cannot find afterwards is not an option:
            // every question that offers resuming names Saved check runs and
            // the menu it lives on.
            foreach (Source s in AllSources)
            foreach (bool keyed in new[] { true, false })
            foreach (bool running in new[] { true, false })
            {
                Plan p = Decide(keyed, s, running, resultsCollected: 2,
                                resultsAreKept: true);
                if (p.OffersResumeLater)
                {
                    Assert.Contains("Saved check runs", p.Announcement);
                    Assert.Contains("Fix menu", p.Announcement);
                }
            }
        }

        [Fact]
        public void The_keyed_kept_ask_still_says_transmitting_stopped_first()
        {
            // The resume offer changes the question, never the invariant: the
            // first thing the operator hears is that the carrier came down.
            Plan p = Decide(keyed: true, Source.EscapeKey, runInProgress: true,
                            resultsCollected: 2, resultsAreKept: true);
            Assert.True(p.UnkeysFirst);
            Assert.StartsWith("Stopped transmitting.", p.Announcement);
            Assert.True(p.OffersResumeLater);
        }

        [Fact]
        public void A_promise_of_keeping_is_never_made_unless_the_caller_asserts_it()
        {
            // The inverse: with nothing persisting, no wording may claim the
            // results are saved — a reassuring voice over silent data loss is
            // worse than the loss.
            foreach (Source s in AllSources)
            foreach (bool running in new[] { true, false })
            {
                Plan p = Decide(false, s, running, resultsCollected: 2);
                Assert.DoesNotContain("saved", p.Announcement,
                                      StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_run_with_nothing_recorded_still_closes_without_ceremony()
        {
            // The other half of the rule: a fresh run holds nothing, and a
            // question with one sensible answer trains the operator to stop
            // reading questions.
            Plan p = Decide(keyed: false, Source.WindowClosing, runInProgress: false,
                            resultsCollected: 0);
            Assert.Equal(new[] { Step.AbandonNow }, p.Steps);
            Assert.Equal("", p.Announcement);
        }

        [Fact]
        public void Recorded_results_never_weaken_the_keyed_invariant()
        {
            // Whatever the run holds, RF comes down first and no question
            // comes before the unkey.
            foreach (Source s in AllSources)
            foreach (bool running in new[] { true, false })
            foreach (int results in new[] { 0, 1, 5 })
            {
                Plan p = Decide(true, s, running, results);
                Assert.True(p.UnkeysFirst,
                    s + " run=" + running + " results=" + results
                    + " did not unkey first");
            }
        }

        // ---- every source is equal in authority ----

        [Fact]
        public void All_sources_are_equally_authoritative_while_keyed()
        {
            // The Stop button must work exactly as well as Escape. Escape may
            // be swallowed by a screen reader in browse mode, so the button is
            // the primary route, not a fallback — and a plan that treated it
            // as lesser would defeat that.
            Plan viaEscape = Decide(true, Source.EscapeKey, true);
            Plan viaButton = Decide(true, Source.StopButton, true);
            Plan viaChord = Decide(true, Source.HostChord, true);

            Assert.Equal(viaEscape.Steps, viaButton.Steps);
            Assert.Equal(viaEscape.Steps, viaChord.Steps);
        }

        [Fact]
        public void Any_keyed_stop_is_urgent()
        {
            Assert.True(IsUrgent(keyed: true));
            Assert.False(IsUrgent(keyed: false));
        }

        // ---- what the operator hears ----

        [Fact]
        public void Stopping_a_transmission_always_says_so()
        {
            // A blind operator has no other way to know the carrier came down.
            foreach (Source s in AllSources)
            foreach (bool running in new[] { true, false })
            {
                Plan p = Decide(true, s, running);
                Assert.Contains("Stopped transmitting", p.Announcement);
            }
        }

        [Fact]
        public void An_announcement_that_asks_something_ends_in_a_question()
        {
            // If the operator is expected to answer, it must sound like a
            // question — otherwise it reads as a statement and they will not
            // know a reply is wanted.
            foreach (Source s in AllSources)
            foreach (bool keyed in new[] { true, false })
            {
                Plan p = Decide(keyed, s, runInProgress: true);
                if (p.Asks && p.Announcement.Length > 0)
                    Assert.EndsWith("?", p.Announcement);
            }
        }

        [Fact]
        public void Nothing_happening_says_nothing()
        {
            // Closing an idle window should not announce anything at all.
            Assert.Equal("", Decide(false, Source.WindowClosing, false).Announcement);
        }

        // ---- the shape cannot degenerate ----

        [Fact]
        public void Every_plan_has_at_least_one_step()
        {
            // A stop request that produces no steps is a stop request that did
            // nothing, silently.
            foreach (Source s in AllSources)
            foreach (bool keyed in new[] { true, false })
            foreach (bool running in new[] { true, false })
                Assert.NotEmpty(Decide(keyed, s, running).Steps);
        }

        // ---- the page speaks strings; this side speaks an enum ----

        [Theory]
        [InlineData("escape", Source.EscapeKey)]
        [InlineData("ESCAPE", Source.EscapeKey)]
        [InlineData("  escape  ", Source.EscapeKey)]
        [InlineData("button", Source.StopButton)]
        [InlineData("host", Source.HostChord)]
        [InlineData("closing", Source.WindowClosing)]
        public void The_pages_own_words_for_a_stop_are_understood(string raw, Source expected)
        {
            Assert.Equal(expected, SourceFrom(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("who-knows")]
        [InlineData("esc")]
        public void An_unrecognised_stop_still_stops(string raw)
        {
            // The failure this forbids: ignoring a stop because its label was
            // not on a list. That is a stop that fails at exactly the moment
            // somebody wanted out. The source gates nothing, so there is no
            // safety in refusing to understand it.
            Assert.Equal(Source.StopButton, SourceFrom(raw));
        }

        [Fact]
        public void An_unknown_stop_is_not_attributed_to_Escape()
        {
            // Escape can be swallowed by a screen reader in browse mode, and
            // whether it reaches us at all is a thing we are trying to find out
            // from the trace. Filing unknown stops under Escape would
            // manufacture evidence for the very question being asked.
            Assert.NotEqual(Source.EscapeKey, SourceFrom("something else"));
        }

        [Fact]
        public void A_stop_from_any_recognised_source_still_unkeys_first_while_keyed()
        {
            // Ties the translation back to the invariant: whatever word arrived,
            // the carrier comes down before anything is asked.
            foreach (string raw in new[] { "escape", "button", "host", "closing", "gibberish" })
                Assert.True(Decide(true, SourceFrom(raw), true).UnkeysFirst, raw);
        }

        [Fact]
        public void No_plan_both_asks_and_abandons_without_asking()
        {
            // Contradictory instructions to the caller. Whichever it acted on
            // would be a coin toss.
            foreach (Source s in AllSources)
            foreach (bool keyed in new[] { true, false })
            foreach (bool running in new[] { true, false })
            {
                Plan p = Decide(keyed, s, running);
                bool asks = p.Steps.Contains(Step.AskAbandonOrContinue);
                bool abandons = p.Steps.Contains(Step.AbandonNow);
                Assert.False(asks && abandons, s + ": asks AND abandons");
            }
        }
    }
}
