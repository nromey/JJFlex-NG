using System.Windows.Forms;
using Xunit;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// The runtime half of #206: KeyInventory's near-miss lookup, against the
    /// REAL LeaderCommands table — the same table the Ctrl+J help, the Keys
    /// dialog and the Command Finder read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// LeaderLayerConsistencyTests (Radios.Tests) proves the advertised
    /// strings against the switch from SOURCE; this proves the built lookup
    /// answers the questions DoLeaderCommand's unknown-command arm actually
    /// asks. Pure static data — no window, no dispatcher, no desktop, so it
    /// runs green under DeskGuard on the interactive desktop.
    /// </para>
    /// <para>
    /// The Ctrl+G case is Noel's own press from 2026-08-23, transcript
    /// LeaderInvalidTone at 256371 ms: he meant Ctrl+J then G and got
    /// "Unknown command". The assertion that G's description comes back is
    /// the assertion that he now gets told what G does instead.
    /// </para>
    /// </remarks>
    public class LeaderNearMissTests
    {
        [Fact]
        public void Bare_G_names_Ctrl_G_and_what_it_does()
        {
            // Noel's press of 2026-08-23 was Ctrl+G, meaning the tone, when
            // the tone was bare G. Sprint 48 Track C put the tone ON Ctrl+G —
            // the grammar's chord for a toggle whose initial is G — so the
            // slip runs the other way now: a bare G is the near-miss, and the
            // answer names the chord his fingers already reached for.
            bool found = KeyInventory.TryFindLeaderNearMiss(
                Keys.G, out string key, out string what);

            Assert.True(found, "bare G is unbound and Ctrl+G arms the test tone — the near-miss must be found");
            Assert.Equal("Ctrl+G", key);
            Assert.Contains("test tone", what, System.StringComparison.OrdinalIgnoreCase);

            // Sprint 36 Track F: pinned exactly, because this is the sentence
            // #206 is actually about and the second half of the task was that
            // it be SHORT. The inventory description ends "(replaces your
            // microphone while transmitting)" — true, and not what someone
            // standing in the layer having just mistyped needs to hear. The
            // whole spoken line is "Ctrl+G: Arm or disarm the TX test tone".
            Assert.Equal("Arm or disarm the TX test tone", what);
        }

        [Fact]
        public void A_bare_letter_inside_the_slice_range_recovers_to_its_action_not_the_slice()
        {
            // Sprint 48 Track C. E is bound three ways — Shift+E slice E,
            // Ctrl+E the earcons, Alt+E the CW echo — and bare E is nothing.
            // Under the old candidate order (Shift first) a bare E would have
            // been told "Shift+E: jump to slice E", which nobody pressing E in
            // the JJ key layer meant. Ctrl comes first now, so the earcons
            // toggle is named; the point under test is that the slice is NOT.
            bool found = KeyInventory.TryFindLeaderNearMiss(Keys.E, out string key, out _);

            Assert.True(found);
            Assert.NotEqual("Shift+E", key);
            Assert.Equal("Ctrl+E", key);
        }

        [Fact]
        public void A_bare_letter_whose_only_chord_is_Alt_recovers_to_it()
        {
            // K carries nothing at bare, Ctrl or Shift; Alt+K is the mic
            // check. Before Sprint 48 Track C the candidate list had no Alt
            // tier at all, so an operator who pressed K — which WAS the mic
            // check until that track — would have heard only "Unknown key".
            bool found = KeyInventory.TryFindLeaderNearMiss(Keys.K, out string key, out string what);

            Assert.True(found);
            Assert.Equal("Alt+K", key);
            Assert.Contains("mic check", what, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_named_alternative_is_a_line_not_a_paragraph()
        {
            // Every near-miss the layer can produce, measured. The worst case
            // before briefing was "Say what is still running and what it is
            // costing — recording, captures, meter tones", which put the whole
            // spoken line past twenty words.
            var pressed = new[]
            {
                Keys.O | Keys.Shift, Keys.V | Keys.Control, Keys.K | Keys.Control,
                Keys.A | Keys.Alt, Keys.Q | Keys.Control, Keys.E | Keys.Control,
            };

            foreach (var chord in pressed)
            {
                if (!KeyInventory.TryFindLeaderNearMiss(chord, out _, out string what)) continue;
                Assert.True(what.Length <= 52,
                    $"the near-miss for {chord} says {what.Length} characters: \"{what}\"");
                Assert.DoesNotContain(" — ", what, System.StringComparison.Ordinal);
                Assert.DoesNotContain(" (", what, System.StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_bare_form_wins_when_it_is_bound()
        {
            // Alt+V is the version and bare V opens the audio layer; Ctrl+V is
            // nothing. A slipped Ctrl+V names the bare form first, because a
            // bare letter that IS bound is the likeliest intent of all.
            bool found = KeyInventory.TryFindLeaderNearMiss(
                Keys.V | Keys.Control, out string key, out string what);

            Assert.True(found);
            Assert.Equal("V", key);
            Assert.Contains("audio layer", what, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_slipped_modifier_on_a_moved_toggle_recovers_to_the_ctrl_form()
        {
            // T moved from bare to Ctrl+T in Sprint 48 Track C, and Shift+T
            // (the old earcons chord) is unbound. An Alt+T press — the wrong
            // modifier on the right letter — must be told the Ctrl form.
            bool found = KeyInventory.TryFindLeaderNearMiss(
                Keys.T | Keys.Alt, out string key, out string what);

            Assert.True(found);
            Assert.Equal("Ctrl+T", key);
            Assert.Contains("meter tones", what, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_chord_that_is_actually_bound_is_not_a_near_miss()
        {
            // Ctrl+G is bound (the test tone). The unknown-command arm never
            // runs for it — but if it were asked anyway, the answer must be
            // no, or a bug elsewhere would overwrite a real command's speech.
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.G | Keys.Control, out _, out _));
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.A | Keys.Control, out _, out _));
        }

        [Fact]
        public void A_letter_bound_at_no_tier_stays_a_plain_unknown()
        {
            // X carries nothing at bare, Shift or Ctrl. The generic
            // "Unknown command" message is then correct, and inventing an
            // alternative would be worse than silence.
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.X | Keys.Control, out _, out _));
        }

        [Fact]
        public void A_shifted_press_outside_the_slice_row_recovers_to_a_bound_tier()
        {
            // Shift+Q is unbound (Q sits outside the slice-jump row). Bare Q
            // is nothing since Sprint 48 Track C; Ctrl+Q is the QSO analyzer
            // and Alt+Q the noise capture, and Ctrl comes first. The answer
            // is one of the two Q chords — not silence.
            bool found = KeyInventory.TryFindLeaderNearMiss(
                Keys.Q | Keys.Shift, out string key, out string what);

            Assert.True(found);
            Assert.Equal("Ctrl+Q", key);
            Assert.Contains("signal analyzer", what, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_slice_jump_range_is_bound_data_not_a_near_miss()
        {
            // Shift+B lives inside the advertised Shift+A..Shift+H range, so
            // it must read as BOUND — the range expansion feeding the lookup
            // is exactly what the ExcludedKeys machinery protects.
            Assert.False(KeyInventory.TryFindLeaderNearMiss(Keys.B | Keys.Shift, out _, out _));
        }
    }

    /// <summary>
    /// The same question asked of a VALUE SUB-LAYER (#547), against the real
    /// AudioLayerCommands and FilterLayerCommands rows — the tables H lists
    /// and the explorer draws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bare B is Noel's own press: reaching for binaural, which is
    /// Ctrl+B, he got a bare B, and the layer closed and said nothing. It
    /// meant nothing outside either, so nothing happened at all — and the
    /// next keystroke landed somewhere other than where he believed he was.
    /// </para>
    /// <para>
    /// Static data only — no window, no dispatcher, no desktop, so this runs
    /// green under DeskGuard on the interactive desktop, like the class above.
    /// </para>
    /// </remarks>
    public class LayerNearMissTests
    {
        [Fact]
        public void A_bare_b_in_the_audio_layer_names_ctrl_b_and_binaural()
        {
            bool found = KeyInventory.TryFindLayerNearMiss(
                KeyInventory.AudioLayerContext, Keys.B, out string key, out string what);

            Assert.True(found, "Ctrl+B is binaural and a bare B is nothing — the near miss must be found");
            Assert.Equal("Ctrl+B", key);
            Assert.Equal("Binaural receive on or off", what);
        }

        [Fact]
        public void The_ctrl_form_beats_the_slice_jump_on_the_same_letter()
        {
            // Both Ctrl+B and Shift+B are one modifier from a bare B, and the
            // leader's ordering would name Shift+B — "jump to that slice" —
            // for every bare letter A through H. Inside a layer the Ctrl tier
            // is the same SUBJECT (#515), so it is the one worth naming, and
            // the letter this task was reported for is inside that range.
            KeyInventory.TryFindLayerNearMiss(
                KeyInventory.AudioLayerContext, Keys.B, out string key, out _);
            Assert.Equal("Ctrl+B", key);

            // ...and a letter with no Ctrl form still gets its slice jump,
            // because naming a real key beats an unannounced exit.
            Assert.True(KeyInventory.TryFindLayerNearMiss(
                KeyInventory.AudioLayerContext, Keys.F, out string alt, out _));
            Assert.Equal("Shift+F", alt);
        }

        [Fact]
        public void The_slip_is_caught_in_both_directions()
        {
            // The layer's grammar is plain-letter picks against Ctrl toggles,
            // so holding Ctrl by mistake is as likely as dropping it.
            Assert.True(KeyInventory.TryFindLayerNearMiss(
                KeyInventory.AudioLayerContext, Keys.V | Keys.Control, out string key, out string what));
            Assert.Equal("V", key);
            Assert.Equal("Slice volume", what);
        }

        [Fact]
        public void A_chord_the_layer_actually_has_is_not_a_near_miss()
        {
            // Ctrl+B and V are real chords here. The engine handles them long
            // before the near-miss question is asked, but a yes here would
            // mean a real command's speech could be overwritten by a hint.
            Assert.False(KeyInventory.TryFindLayerNearMiss(
                KeyInventory.AudioLayerContext, Keys.B | Keys.Control, out _, out _));
            Assert.False(KeyInventory.TryFindLayerNearMiss(
                KeyInventory.AudioLayerContext, Keys.V, out _, out _));
        }

        [Fact]
        public void A_letter_the_layer_carries_at_no_tier_is_a_plain_unknown()
        {
            // Z is nothing in the audio layer at any tier, so the old answer
            // stands: keep the value, say the layer closed, and let the key
            // travel. Inventing an alternative would be worse than leaving.
            Assert.False(KeyInventory.TryFindLayerNearMiss(
                KeyInventory.AudioLayerContext, Keys.Z, out _, out _));
        }

        [Fact]
        public void Only_letters_are_asked_about()
        {
            // The rule is scoped to the layers' letter grammar on purpose.
            // Ctrl+Home still means "top of the document" on its way out, and
            // no layer is in the business of refusing Tab or a function key —
            // meanings that live below us, which the registry cannot answer
            // for. Left to the help rows' punctuation this would be an
            // accident; it is stated instead.
            foreach (var chord in new[]
            {
                Keys.Home | Keys.Control, Keys.End | Keys.Shift, Keys.Tab,
                Keys.Space, Keys.F5, Keys.D0 | Keys.Control, Keys.Oem2 | Keys.Control,
            })
            {
                Assert.False(KeyInventory.TryFindLayerNearMiss(
                    KeyInventory.AudioLayerContext, chord, out _, out _),
                    chord + " is not a letter and must keep the unhandled-key answer");
            }
        }

        [Fact]
        public void The_filter_layer_answers_from_its_own_rows()
        {
            // Its letters are S, T and R. Ctrl+S is not one of them, and S —
            // speak the whole filter — is what the hand was reaching for.
            Assert.True(KeyInventory.TryFindLayerNearMiss(
                KeyInventory.FilterLayerContext, Keys.S | Keys.Control, out string key, out string what));
            Assert.Equal("S", key);
            Assert.Equal("Speak the whole filter", what);

            // ...and it does NOT borrow the audio layer's letters.
            Assert.False(KeyInventory.TryFindLayerNearMiss(
                KeyInventory.FilterLayerContext, Keys.V | Keys.Control, out _, out _));
        }

        [Fact]
        public void The_named_alternative_is_a_line_not_a_paragraph()
        {
            // #206's second half, which applies here for the same reason: this
            // fires when somebody has already made a mistake and does not want
            // a paragraph. Every near miss both layers can produce, measured.
            foreach (var context in new[]
            {
                KeyInventory.AudioLayerContext, KeyInventory.FilterLayerContext,
            })
            {
                for (var code = Keys.A; code <= Keys.Z; code++)
                {
                    foreach (var mods in new[] { Keys.None, Keys.Shift, Keys.Control })
                    {
                        if (!KeyInventory.TryFindLayerNearMiss(context, code | mods, out _, out string what))
                            continue;
                        Assert.True(what.Length <= 60,
                            $"the near-miss for {code | mods} in {context} says {what.Length} characters: \"{what}\"");
                        Assert.DoesNotContain(" — ", what, System.StringComparison.Ordinal);
                        Assert.DoesNotContain(" (", what, System.StringComparison.Ordinal);
                    }
                }
            }
        }
    }
}
