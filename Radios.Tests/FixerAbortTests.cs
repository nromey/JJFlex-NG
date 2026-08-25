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
