using System.Linq;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The parser that turns the leader inventory's human-readable chord
    /// strings into the <see cref="Keys"/> values the dispatcher switches on.
    /// Two consumers stand on it: the #206 near-miss lookup at runtime, and
    /// the #183 leader-layer consistency sweep — so a parsing hole here would
    /// quietly blind both.
    /// </summary>
    public class LeaderChordParserTests
    {
        // ── Single chords ───────────────────────────────────────────────

        [Theory]
        [InlineData("N", Keys.N)]
        [InlineData("Shift+N", Keys.N | Keys.Shift)]
        [InlineData("Ctrl+A", Keys.A | Keys.Control)]
        [InlineData("Control+A", Keys.A | Keys.Control)]
        [InlineData("Ctrl+Shift+T", Keys.T | Keys.Control | Keys.Shift)]
        [InlineData("Escape", Keys.Escape)]
        [InlineData("Ctrl+J, Shift+F", Keys.F | Keys.Shift)]   // prefix stripped
        [InlineData("/", Keys.Oem2)]                            // the explorer's door (#519)
        [InlineData("Ctrl+J, /", Keys.Oem2)]
        public void A_chord_string_parses_to_its_keys_value(string text, Keys expected)
        {
            Assert.True(LeaderChordParser.TryParseChord(text, out Keys chord), text);
            Assert.Equal(expected, chord);
        }

        [Fact]
        public void The_question_mark_parses_as_its_us_layout_arrival_form()
        {
            // "?" reaches the dispatcher as Oem2|Shift on a US keyboard —
            // the exact fact whose absence kept JJ ? dead (#183's origin).
            Assert.True(LeaderChordParser.TryParseChord("?", out Keys chord));
            Assert.Equal(Keys.Oem2 | Keys.Shift, chord);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Frob+N")]
        [InlineData("NotAKeyName")]
        [InlineData("Shift+")]
        public void Garbage_is_refused_not_guessed(string text)
        {
            Assert.False(LeaderChordParser.TryParseChord(text, out _), text);
        }

        // ── Display strings ─────────────────────────────────────────────

        [Fact]
        public void A_plain_entry_yields_one_chord()
        {
            var chords = LeaderChordParser.ParseDisplay("Ctrl+J, N");
            Assert.Equal(new[] { Keys.N }, chords);
        }

        [Fact]
        public void An_alternate_entry_yields_every_alternate()
        {
            // "H or ?" — and "?" contributes BOTH its shifted arrival form and
            // the bare key, because the dispatcher deliberately accepts both.
            var chords = LeaderChordParser.ParseDisplay("Ctrl+J, H or ?");

            Assert.Contains(Keys.H, chords);
            Assert.Contains(Keys.Oem2 | Keys.Shift, chords);
            Assert.Contains(Keys.Oem2, chords);
            Assert.Equal(3, chords.Count);
        }

        [Fact]
        public void The_slash_key_yields_both_arrival_forms_like_the_question_mark()
        {
            // "/" is the explorer's door (#519). It is the same physical key
            // as "?", the dispatcher carries both cases, and the consistency
            // sweep must see both advertised — or the Shift form would read as
            // handled-but-unadvertised on every run.
            var chords = LeaderChordParser.ParseDisplay("Ctrl+J, /");

            Assert.Contains(Keys.Oem2, chords);
            Assert.Contains(Keys.Oem2 | Keys.Shift, chords);
            Assert.Equal(2, chords.Count);
        }

        [Fact]
        public void A_range_expands_and_honours_its_exclusions()
        {
            var chords = LeaderChordParser.ParseDisplay(
                "Ctrl+J, Shift+A through Shift+H", new[] { "Ctrl+J, Shift+F" });

            Assert.Equal(7, chords.Count);
            Assert.Contains(Keys.A | Keys.Shift, chords);
            Assert.Contains(Keys.H | Keys.Shift, chords);
            Assert.DoesNotContain(Keys.F | Keys.Shift, chords);
        }

        [Fact]
        public void An_unparseable_display_contributes_nothing_rather_than_throwing()
        {
            Assert.Empty(LeaderChordParser.ParseDisplay("Ctrl+J, V is the fast route"));
            Assert.Empty(LeaderChordParser.ParseDisplay(""));
        }

        // ── Near-miss candidates (#206) ─────────────────────────────────

        [Fact]
        public void Candidates_lead_with_the_bare_form()
        {
            // The task's rule verbatim: bare first, it is the most likely
            // intent. Ctrl+G was the press that motivated all of this.
            var c = LeaderChordParser.NearMissCandidates(Keys.G | Keys.Control);

            Assert.Equal(new[] { Keys.G, Keys.G | Keys.Shift }, c);
        }

        [Fact]
        public void The_pressed_chord_is_never_its_own_candidate()
        {
            var c = LeaderChordParser.NearMissCandidates(Keys.G);

            Assert.DoesNotContain(Keys.G, c);
            Assert.Equal(new[] { Keys.G | Keys.Shift, Keys.G | Keys.Control }, c);
        }

        [Fact]
        public void A_modifier_only_press_has_no_candidates()
        {
            Assert.Empty(LeaderChordParser.NearMissCandidates(Keys.Control));
            Assert.Empty(LeaderChordParser.NearMissCandidates(Keys.None));
        }
    }
}
