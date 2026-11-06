#nullable enable

using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Filter edge mode is entered on purpose and says where you are (#748).
    /// Measured 2026-10-17: three accidental entries in about a minute while
    /// widening a filter, because a held bracket repeats at about the speed of
    /// a deliberate double-tap; and in the mode, Shift and Ctrl with a bracket
    /// still moved the whole filter.
    /// </summary>
    /// <remarks>
    /// <para>Noel's rulings: 2026-10-17, retire the double-tap (4) and grab the
    /// edges with Alt+Shift and a bracket (5); 2026-11-06 (night question J2),
    /// the held edge's key again leaves, the other edge's key switches, and
    /// Shift or Ctrl with a bracket changes nothing and says which edge is
    /// held, at Chatty with how to do what the key suggests.</para>
    /// <para>EVERY EXPECTED STRING HERE IS A HAND-WRITTEN LITERAL, the
    /// assembled sentence as the operator hears it, and every one is DRAFT
    /// wording for Noel. A failing diff here is a wording change he has not
    /// heard yet.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FilterEdgeGrabTests
    {
        private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        private static string Body(string text, string signature)
        {
            int at = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " is not where this test reads it.");
            int open = text.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(at, i - at + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        private static BracketDecision D(FilterEdge held, bool close, bool shift = false, bool ctrl = false, bool alt = false)
            => FilterBracketKeys.Decide(held, close, shift, ctrl, alt);

        // ------------------------------------------------------------------
        // The grammar
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(false, FilterEdge.Low)]
        [InlineData(true, FilterEdge.High)]
        public void Alt_shift_with_a_bracket_grabs_that_edge(bool close, FilterEdge edge)
        {
            var d = D(FilterEdge.None, close, shift: true, alt: true);
            Assert.Equal(BracketAction.GrabEdge, d.Action);
            Assert.Equal(edge, d.Edge);
        }

        [Theory]
        [InlineData(FilterEdge.Low, false)]
        [InlineData(FilterEdge.High, true)]
        public void The_held_edges_own_grab_key_leaves_edge_mode(FilterEdge held, bool close)
            => Assert.Equal(BracketAction.LeaveEdgeMode, D(held, close, shift: true, alt: true).Action);

        [Theory]
        [InlineData(FilterEdge.Low, true, FilterEdge.High)]
        [InlineData(FilterEdge.High, false, FilterEdge.Low)]
        public void The_other_edges_grab_key_switches_edges(FilterEdge held, bool close, FilterEdge now)
        {
            var d = D(held, close, shift: true, alt: true);
            Assert.Equal(BracketAction.SwitchEdge, d.Action);
            Assert.Equal(now, d.Edge);
        }

        /// <summary>
        /// The ruling's (c): in edge mode Shift and Ctrl with a bracket change
        /// nothing. They used to move the whole filter, because their branches
        /// ran before anything looked at the mode.
        /// </summary>
        [Theory]
        [InlineData(FilterEdge.Low, false, true, false, BracketAction.Slide)]
        [InlineData(FilterEdge.Low, true, true, false, BracketAction.Slide)]
        [InlineData(FilterEdge.High, false, false, true, BracketAction.Squeeze)]
        [InlineData(FilterEdge.High, true, false, true, BracketAction.Pull)]
        [InlineData(FilterEdge.Low, false, false, true, BracketAction.Squeeze)]
        [InlineData(FilterEdge.High, true, true, false, BracketAction.Slide)]
        public void Shift_or_ctrl_with_a_bracket_while_an_edge_is_held_changes_nothing(FilterEdge held, bool close, bool shift, bool ctrl, BracketAction suggested)
        {
            var d = D(held, close, shift: shift, ctrl: ctrl);
            Assert.Equal(BracketAction.RefusedWhileHeld, d.Action);
            Assert.Equal(held, d.Edge);
            Assert.Equal(suggested, d.WouldHaveDone);
        }

        [Fact]
        public void Outside_edge_mode_the_bracket_keys_do_what_they_always_did()
        {
            Assert.Equal(BracketAction.Widen, D(FilterEdge.None, false).Action);
            Assert.Equal(BracketAction.Widen, D(FilterEdge.None, true).Action);
            Assert.Equal(BracketAction.Slide, D(FilterEdge.None, false, shift: true).Action);
            Assert.Equal(BracketAction.Squeeze, D(FilterEdge.None, false, ctrl: true).Action);
            Assert.Equal(BracketAction.Pull, D(FilterEdge.None, true, ctrl: true).Action);
            Assert.Equal(BracketAction.PresetCycle, D(FilterEdge.None, true, alt: true).Action);
        }

        [Theory]
        [InlineData(FilterEdge.Low)]
        [InlineData(FilterEdge.High)]
        public void In_edge_mode_a_plain_bracket_moves_the_held_edge(FilterEdge held)
        {
            Assert.Equal(BracketAction.MoveHeldEdge, D(held, false).Action);
            Assert.Equal(BracketAction.MoveHeldEdge, D(held, true).Action);
        }

        /// <summary>
        /// A plain bracket NEVER enters edge mode, however fast it repeats:
        /// the decision takes no time and no previous key, so a double-tap has
        /// nothing to be made of.
        /// </summary>
        [Fact]
        public void A_plain_bracket_never_grabs_an_edge()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(BracketAction.Widen, D(FilterEdge.None, false).Action);
                Assert.Equal(BracketAction.Widen, D(FilterEdge.None, true).Action);
            }
        }

        [Theory]
        [InlineData(FilterEdge.None)]
        [InlineData(FilterEdge.Low)]
        [InlineData(FilterEdge.High)]
        public void The_transmit_filters_chords_are_left_for_the_registry(FilterEdge held)
        {
            // Ctrl+Shift with a bracket is the transmit filter's low edge and
            // Ctrl+Alt its high edge, in KeyCommands' table.
            Assert.Equal(BracketAction.NotOurs, D(held, false, shift: true, ctrl: true).Action);
            Assert.Equal(BracketAction.NotOurs, D(held, true, ctrl: true, alt: true).Action);
            Assert.Equal(BracketAction.NotOurs, D(held, true, shift: true, ctrl: true, alt: true).Action);
        }

        // ------------------------------------------------------------------
        // What it says, assembled (DRAFT)
        // ------------------------------------------------------------------

        [Fact]
        public void Entering_names_the_mode_and_the_edge()
        {
            Assert.Equal("Filter edge mode, low edge held.", FilterBracketKeys.EntrySentence(FilterEdge.Low, VerbosityLevel.Terse));
            Assert.Equal("Filter edge mode, high edge held.", FilterBracketKeys.EntrySentence(FilterEdge.High, VerbosityLevel.Critical));
            Assert.Equal("Filter edge mode, low edge held. Brackets move it. Alt Shift left bracket again, or Escape, leaves.",
                FilterBracketKeys.EntrySentence(FilterEdge.Low, VerbosityLevel.Chatty));
            Assert.Equal("Filter edge mode, high edge held. Brackets move it. Alt Shift right bracket again, or Escape, leaves.",
                FilterBracketKeys.EntrySentence(FilterEdge.High, VerbosityLevel.Diagnostic));
        }

        [Fact]
        public void Leaving_says_off_not_cancelled()
        {
            Assert.Equal("Filter edge mode off", FilterBracketKeys.OffSentence());
            Assert.DoesNotContain("settings.filter.edge_mode_cancelled", Src("Radios", "Lexicon", "settings.json"), StringComparison.Ordinal);
        }

        [Fact]
        public void Switching_says_the_edge_now_held()
        {
            Assert.Equal("Low edge held.", FilterBracketKeys.HeldSentence(FilterEdge.Low));
            Assert.Equal("High edge held.", FilterBracketKeys.HeldSentence(FilterEdge.High));
        }

        [Theory]
        [InlineData(FilterEdge.Low, BracketAction.Slide, "Low edge held. To shift the whole filter, press Alt Shift left bracket to leave edge mode first.")]
        [InlineData(FilterEdge.High, BracketAction.Slide, "High edge held. To shift the whole filter, press Alt Shift right bracket to leave edge mode first.")]
        [InlineData(FilterEdge.Low, BracketAction.Squeeze, "Low edge held. To narrow both edges, press Alt Shift left bracket to leave edge mode first.")]
        [InlineData(FilterEdge.High, BracketAction.Squeeze, "High edge held. To narrow both edges, press Alt Shift right bracket to leave edge mode first.")]
        [InlineData(FilterEdge.Low, BracketAction.Pull, "Low edge held. To widen both edges, press Alt Shift left bracket to leave edge mode first.")]
        [InlineData(FilterEdge.High, BracketAction.Pull, "High edge held. To widen both edges, press Alt Shift right bracket to leave edge mode first.")]
        public void A_refusal_says_the_held_edge_and_at_chatty_how_to_do_what_the_key_suggests(FilterEdge held, BracketAction wouldHaveDone, string chatty)
        {
            string terse = held == FilterEdge.High ? "High edge held." : "Low edge held.";
            Assert.Equal(terse, FilterBracketKeys.RefusalSentence(held, wouldHaveDone, VerbosityLevel.Terse));
            Assert.Equal(terse, FilterBracketKeys.RefusalSentence(held, wouldHaveDone, VerbosityLevel.Critical));
            Assert.Equal(chatty, FilterBracketKeys.RefusalSentence(held, wouldHaveDone, VerbosityLevel.Chatty));
            Assert.Equal(chatty, FilterBracketKeys.RefusalSentence(held, wouldHaveDone, VerbosityLevel.Diagnostic));
        }

        [Fact]
        public void Each_step_in_edge_mode_names_the_held_edge_at_chatty()
        {
            const string filter = "Filter 100 to 2900, 2800 hertz wide";
            Assert.Equal(filter, FilterBracketKeys.HeldStepSentence(FilterEdge.Low, filter, VerbosityLevel.Terse));
            Assert.Equal("Low edge held. " + filter, FilterBracketKeys.HeldStepSentence(FilterEdge.Low, filter, VerbosityLevel.Chatty));
            Assert.Equal("High edge held. " + filter, FilterBracketKeys.HeldStepSentence(FilterEdge.High, filter, VerbosityLevel.Chatty));
        }

        // ------------------------------------------------------------------
        // The wiring, pinned at the source: FreqOutHandlers is WPF and nothing
        // here builds a window.
        // ------------------------------------------------------------------

        [Fact]
        public void The_bracket_handler_carries_out_the_decision_and_the_double_tap_is_gone()
        {
            string handlers = Src("JJFlexWpf", "FreqOutHandlers.cs");
            string handler = Body(handlers, "public void HandleFilterHotkey(KeyEventArgs e)");

            // The double-tap is retired: no timing, no remembered key.
            Assert.DoesNotContain("DoubleTapToleranceMs", handlers, StringComparison.Ordinal);
            Assert.DoesNotContain("_lastBracketKey", handlers, StringComparison.Ordinal);
            Assert.DoesNotContain("_lastBracketTime", handlers, StringComparison.Ordinal);

            // One decision, taken before anything moves.
            int decide = handler.IndexOf("Radios.FilterBracketKeys.Decide(_filterEdgeMode,", StringComparison.Ordinal);
            Assert.True(decide >= 0, "HandleFilterHotkey must decide through FilterBracketKeys.");
            int setFilter = handler.IndexOf("Rig.SetFilter(low, high);", StringComparison.Ordinal);
            Assert.True(setFilter > decide);

            // The refusal and the grab keys return before the filter is read,
            // so nothing they do can reach SetFilter: each case's own section
            // ends in a return, not a break that falls on to the filter.
            int readFilter = handler.IndexOf("int low = Rig.FilterLow;", StringComparison.Ordinal);
            Assert.True(readFilter > decide && readFilter < setFilter);
            foreach (string action in new[] { "RefusedWhileHeld", "GrabEdge", "LeaveEdgeMode", "SwitchEdge" })
            {
                int at = handler.IndexOf("case Radios.BracketAction." + action + ":", StringComparison.Ordinal);
                Assert.True(at > decide && at < readFilter, action + " is not handled in the decision switch.");
                int next = handler.IndexOf("case Radios.BracketAction.", at + 1, StringComparison.Ordinal);
                int end = next > at && next < readFilter ? next : handler.LastIndexOf('}', readFilter);
                string section = handler.Substring(at, end - at).TrimEnd();
                Assert.True(section.EndsWith("return;", StringComparison.Ordinal),
                    action + " must return before the filter is read: " + section);
            }

            // The whole-filter branches answer the decision, not the raw modifiers.
            Assert.DoesNotContain("if (shift && !ctrl && !alt)", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("else if (ctrl && !shift && !alt)", handler, StringComparison.Ordinal);
            Assert.Contains("decision.Action == Radios.BracketAction.Slide", handler, StringComparison.Ordinal);
        }

        [Fact]
        public void Escape_and_the_held_edges_key_leave_through_one_door_that_says_off()
        {
            string handlers = Src("JJFlexWpf", "FreqOutHandlers.cs");
            string cancel = Body(handlers, "public void CancelFilterEdgeMode()");
            Assert.Contains("_filterEdgeSticky?.Exit(Radios.FilterBracketKeys.OffSentence());", cancel, StringComparison.Ordinal);
            string handler = Body(handlers, "public void HandleFilterHotkey(KeyEventArgs e)");
            int leave = handler.IndexOf("case Radios.BracketAction.LeaveEdgeMode:", StringComparison.Ordinal);
            Assert.True(leave >= 0);
            Assert.Contains("CancelFilterEdgeMode();", handler.Substring(leave, handler.IndexOf("return;", leave, StringComparison.Ordinal) - leave), StringComparison.Ordinal);
        }

        [Fact]
        public void The_key_inventory_lists_the_grab_keys_and_not_the_double_tap()
        {
            string inventory = Src("JJFlexWpf", "KeyInventory.cs");
            Assert.DoesNotContain("\"[[ or ]]\"", inventory, StringComparison.Ordinal);
            Assert.Contains("new(\"Filter\", \"Anywhere in radio modes\", \"Alt+Shift+[ Alt+Shift+]\",", inventory, StringComparison.Ordinal);
        }

        /// <summary>
        /// Round two (Codex F3-R): the ten-second inactivity exit said
        /// "Filter edge mode ended" while every other exit says "Filter edge
        /// mode off". Every exit says off now.
        /// </summary>
        [Fact]
        public void The_inactivity_exit_says_off_too()
        {
            string handlers = Src("JJFlexWpf", "FreqOutHandlers.cs");
            string sticky = Body(handlers, "private StickyAnnouncedMode EnsureFilterEdgeStickyMode()");
            Assert.Contains("Radios.FilterBracketKeys.OffSentence()", sticky, StringComparison.Ordinal);
            Assert.DoesNotContain("settings.filter.edge_mode_ended", handlers, StringComparison.Ordinal);
            Assert.DoesNotContain("settings.filter.edge_mode_ended", Src("Radios", "Lexicon", "settings.json"), StringComparison.Ordinal);
        }
    }
}
