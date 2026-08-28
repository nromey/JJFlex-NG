using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The value sub-layer pattern (#305), tested against its first real
    /// consumer, pan (#304). Every decision the pattern settles once — the
    /// exits, cancel-restores, confirm-never-writes, words-or-numbers under
    /// verbosity, the coalesced move speech, the no-key-can-strand rule — is
    /// pinned here so #187 and #200 extend a tested contract rather than a
    /// convention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The exact-string assertions are the review surface</b>, in the
    /// TuningContextHelpTests tradition: everything asserted verbatim below
    /// is read aloud to an operator adjusting a radio by ear, and a failing
    /// diff here is a wording change Noel has not heard yet.
    /// </para>
    /// <para>
    /// The pan definition here mirrors the one KeyCommands.EnterPanMode
    /// builds (Radios.Tests cannot load the WPF assembly). The source-scan
    /// test at the bottom pins the two together: the shipped definition must
    /// name the same lexicon keys, the same anchor letter and the same entry
    /// chord this file exercises, so the mirror cannot drift silently.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ValueSubLayerTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ValueSubLayerTests));

        public ValueSubLayerTests()
        {
            Lexicon.Load(Lexicon.Partitions);
        }

        public void Dispose() => _scope.Dispose();

        // ────────────────────────────────────────────────────────────────
        //  Harness: a pan-shaped layer over a fake radio value, with the
        //  speech seams captured. Verbosity is injected per test.
        // ────────────────────────────────────────────────────────────────

        private sealed class Rig
        {
            public int Pan;
            public readonly List<int> Writes = new();
        }

        private sealed class Harness
        {
            public readonly Rig Rig = new();
            public readonly List<string> Said = new();
            public readonly List<(string Text, string Key)> Moves = new();
            public VerbosityLevel Verbosity = VerbosityLevel.Chatty;
            public ValueSubLayer Layer = null!;

            public List<string> Everything =>
                Said.Concat(Moves.Select(m => m.Text)).ToList();
        }

        private static Harness Open(int pan, VerbosityLevel verbosity)
        {
            var h = new Harness { Verbosity = verbosity };
            h.Rig.Pan = pan;

            var def = new ValueSubLayerDefinition
            {
                Id = "pan",
                Read = () => h.Rig.Pan,
                Apply = v => { h.Rig.Pan = v; h.Rig.Writes.Add(v); },
                Min = 0,
                Max = 100,
                Step = 5,
                FineStep = 1,
                Axis = ValueLayerAxis.LeftRight,
                Anchor = 50,
                AnchorKeys = new[] { Keys.C },
                Number = v => Lexicon.Get("settings.pan.level", ("level", v)),
                Words = PanPhrase.Words,
                DescribeEntry = (cur, entry) => Lexicon.Get(
                    "audio.pan_mode.entered", h.Verbosity,
                    ("letter", "A"), ("level", cur),
                    ("position", PanPhrase.Words(cur))),
                DescribeHelp = (cur, entry) => Lexicon.Get(
                    "audio.pan_mode.help", h.Verbosity,
                    ("letter", "A"), ("level", cur),
                    ("position", PanPhrase.Words(cur)),
                    ("entryLevel", entry),
                    ("entryPosition", PanPhrase.Words(entry))),
                DescribeClosed = () => Lexicon.Get("audio.pan_mode.closed"),
                DescribeRestored = v => Lexicon.Get(
                    "audio.pan_mode.restored", h.Verbosity,
                    ("level", v), ("position", PanPhrase.Words(v))),
                WrongAxisHint = () => Lexicon.Get("audio.pan_mode.wrong_axis"),
                PassThroughKeys = k => k == (Keys.V | Keys.Control | Keys.Shift),
            };

            h.Layer = ValueSubLayer.EnterForTest(
                def,
                (text, key) => h.Moves.Add((text, key)),
                text => h.Said.Add(text),
                () => h.Verbosity);
            return h;
        }

        // ────────────────────────────────────────────────────────────────
        //  The words scale — one home, shared with the slice status summary
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(0, "hard left")]
        [InlineData(2, "hard left")]
        [InlineData(3, "far left")]
        [InlineData(14, "far left")]
        [InlineData(15, "left")]
        [InlineData(34, "left")]
        [InlineData(35, "slightly left")]
        [InlineData(49, "slightly left")]
        [InlineData(50, "center")]
        [InlineData(51, "slightly right")]
        [InlineData(65, "slightly right")]
        [InlineData(66, "right")]
        [InlineData(85, "right")]
        [InlineData(86, "far right")]
        [InlineData(97, "far right")]
        [InlineData(98, "hard right")]
        [InlineData(100, "hard right")]
        public void The_words_scale_names_every_band(int pan, string expected)
        {
            Assert.Equal(expected, PanPhrase.Words(pan));
        }

        [Fact]
        public void The_slice_status_summary_uses_the_same_scale()
        {
            // RadioStatusBuilder carried its own hardcoded copy of the bands
            // until 2026-08-27; it must call PanPhrase now, so one value can
            // never be "center" on one surface and "slightly left" on another.
            string source = ReadSource("Radios/RadioStatusBuilder.cs");
            Assert.Contains("PanPhrase.Words(", source);
            Assert.DoesNotContain("\"pan slightly left\"", source);
        }

        // ────────────────────────────────────────────────────────────────
        //  Entry — the operator is told they are in it, in their form
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Entry_at_chatty_teaches_in_words()
        {
            var h = Open(40, VerbosityLevel.Chatty);
            Assert.Equal(
                "Pan mode. Slice A, slightly left. Left and right arrows adjust, "
                + "Shift moves by one, Home or C centers. Enter keeps it, Escape puts it back.",
                Assert.Single(h.Said));
        }

        [Fact]
        public void Entry_at_terse_states_the_number()
        {
            var h = Open(40, VerbosityLevel.Terse);
            Assert.Equal("Pan mode. Slice A, pan 40.", Assert.Single(h.Said));
        }

        // ────────────────────────────────────────────────────────────────
        //  Moving — words or numbers under verbosity, through the coalescer
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_nudge_applies_the_step_and_speaks_words_at_chatty()
        {
            var h = Open(40, VerbosityLevel.Chatty);
            var r = h.Layer.HandleKey(Keys.Right);

            Assert.Equal(ValueLayerKeyResult.Handled, r);
            Assert.Equal(45, h.Rig.Pan);
            var move = Assert.Single(h.Moves);
            Assert.Equal("slightly left", move.Text);
            Assert.Equal("valuelayer:pan", move.Key);
        }

        [Fact]
        public void A_nudge_speaks_the_number_at_terse()
        {
            var h = Open(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Left);

            Assert.Equal(35, h.Rig.Pan);
            Assert.Equal("Pan 35", Assert.Single(h.Moves).Text);
        }

        [Fact]
        public void Shift_makes_the_step_fine()
        {
            var h = Open(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Right | Keys.Shift);
            Assert.Equal(41, h.Rig.Pan);
            Assert.Equal("Pan 41", Assert.Single(h.Moves).Text);
        }

        [Fact]
        public void The_rail_clamps_and_still_speaks()
        {
            // "Still at the rail" is how an operator learns to stop pressing —
            // the announcement must repeat rather than fall silent.
            var h = Open(98, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Right); // 100
            h.Layer.HandleKey(Keys.Right); // clamped, still 100
            Assert.Equal(100, h.Rig.Pan);
            Assert.Equal(2, h.Moves.Count);
            Assert.Equal("Pan 100", h.Moves[1].Text);
        }

        [Fact]
        public void Cycling_verbosity_mid_layer_switches_the_form_immediately()
        {
            var h = Open(40, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Right);
            h.Verbosity = VerbosityLevel.Terse;
            h.Layer.HandleKey(Keys.Right);

            Assert.Equal("slightly left", h.Moves[0].Text);
            Assert.Equal("Pan 50", h.Moves[1].Text);
        }

        // ────────────────────────────────────────────────────────────────
        //  Centre is one key away
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Home_centres()
        {
            var h = Open(10, VerbosityLevel.Chatty);
            var r = h.Layer.HandleKey(Keys.Home);
            Assert.Equal(ValueLayerKeyResult.Handled, r);
            Assert.Equal(50, h.Rig.Pan);
            Assert.Equal("center", Assert.Single(h.Moves).Text);
        }

        [Fact]
        public void C_centres_too()
        {
            var h = Open(90, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.C);
            Assert.Equal(50, h.Rig.Pan);
            Assert.Equal("Pan 50", Assert.Single(h.Moves).Text);
        }

        // ────────────────────────────────────────────────────────────────
        //  Getting out — the part that must be the same everywhere
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Enter_confirms_keeps_and_writes_nothing()
        {
            var h = Open(40, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Right);           // 45, one write
            var r = h.Layer.HandleKey(Keys.Return);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.False(h.Layer.IsLive);
            Assert.Equal(new[] { 45 }, h.Rig.Writes); // confirm never writes
            Assert.Equal("Pan mode closed", h.Said.Last());
        }

        [Fact]
        public void Escape_cancels_and_puts_the_entry_value_back_out_loud()
        {
            var h = Open(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Right);           // 45
            var r = h.Layer.HandleKey(Keys.Escape);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.Equal(40, h.Rig.Pan);
            Assert.Equal(new[] { 45, 40 }, h.Rig.Writes);
            Assert.Equal("Back to pan 40. Pan mode closed", h.Said.Last());
        }

        [Fact]
        public void Escape_at_chatty_restores_in_words()
        {
            var h = Open(50, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Left);            // 45
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal("Back to center. Pan mode closed", h.Said.Last());
        }

        [Fact]
        public void A_stuck_modifier_cannot_flip_cancel_into_keep()
        {
            var h = Open(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Right);
            var r = h.Layer.HandleKey(Keys.Escape | Keys.Shift);
            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.Equal(40, h.Rig.Pan);
        }

        [Fact]
        public void An_unknown_key_confirms_announces_and_travels_on()
        {
            // The no-key-can-strand rule: every key either works or leaves.
            var h = Open(40, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Right);
            var r = h.Layer.HandleKey(Keys.X);

            Assert.Equal(ValueLayerKeyResult.ClosedPassThrough, r);
            Assert.False(h.Layer.IsLive);
            Assert.Equal(new[] { 45 }, h.Rig.Writes); // kept, not restored
            Assert.Equal("Pan mode closed", h.Said.Last());
        }

        [Fact]
        public void Ctrl_J_hands_off_silently_keeping_the_value()
        {
            var h = Open(40, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Right);
            int saidBefore = h.Said.Count;
            var r = h.Layer.HandleKey(Keys.J | Keys.Control);

            Assert.Equal(ValueLayerKeyResult.ClosedHandOff, r);
            Assert.False(h.Layer.IsLive);
            Assert.Equal(saidBefore, h.Said.Count);   // the host announces the leader
            Assert.Equal(new[] { 45 }, h.Rig.Writes);
        }

        [Fact]
        public void A_forced_drop_keeps_and_says_nothing()
        {
            // The PTT carve-out and the vanished radio: no write, no speech.
            var h = Open(40, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Right);
            int saidBefore = h.Said.Count;
            h.Layer.Drop();

            Assert.False(h.Layer.IsLive);
            Assert.Equal(saidBefore, h.Said.Count);
            Assert.Equal(new[] { 45 }, h.Rig.Writes);
        }

        // ────────────────────────────────────────────────────────────────
        //  Keys that travel with the layer still live
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(Keys.F4 | Keys.Alt)]
        [InlineData(Keys.F1)]
        [InlineData(Keys.V | Keys.Control | Keys.Shift)] // whitelisted verbosity cycle
        public void System_and_whitelisted_chords_pass_through_and_the_layer_stays(Keys k)
        {
            var h = Open(40, VerbosityLevel.Chatty);
            var r = h.Layer.HandleKey(k);
            Assert.Equal(ValueLayerKeyResult.PassThrough, r);
            Assert.True(h.Layer.IsLive);
            Assert.Empty(h.Rig.Writes);
        }

        [Fact]
        public void The_wrong_arrow_pair_hints_and_never_ejects()
        {
            var h = Open(40, VerbosityLevel.Chatty);
            var r1 = h.Layer.HandleKey(Keys.Up);
            var r2 = h.Layer.HandleKey(Keys.Down | Keys.Shift);

            Assert.Equal(ValueLayerKeyResult.Handled, r1);
            Assert.Equal(ValueLayerKeyResult.Handled, r2);
            Assert.True(h.Layer.IsLive);
            Assert.Empty(h.Rig.Writes);
            Assert.Equal("Pan uses left and right", h.Said.Last());
        }

        // ────────────────────────────────────────────────────────────────
        //  Help — on demand, changing nothing
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(Keys.Oem2)]                 // "/" — forgiving
        [InlineData(Keys.Oem2 | Keys.Shift)]    // "?" — the #183 lesson: this form is the one that fires
        [InlineData(Keys.H)]
        public void Help_speaks_state_and_keys_without_changing_anything(Keys k)
        {
            var h = Open(50, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Left);           // 45 — so current and entry differ
            var r = h.Layer.HandleKey(k);

            Assert.Equal(ValueLayerKeyResult.Handled, r);
            Assert.True(h.Layer.IsLive);
            Assert.Equal(45, h.Rig.Pan);
            Assert.Equal(
                "Pan mode, slice A: slightly left. Left and right arrows adjust, "
                + "Shift moves by one, Home or C centers. Enter keeps the new pan. "
                + "Escape puts it back to center. Any other key keeps the pan and does its normal job.",
                h.Said.Last());
        }

        [Fact]
        public void Help_at_terse_is_numbers_end_to_end()
        {
            var h = Open(50, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Left);           // 45
            h.Layer.HandleKey(Keys.Oem2 | Keys.Shift);
            Assert.Equal(
                "Pan mode, slice A: pan 45. Left and right adjust, Shift by one, "
                + "Home or C centers. Enter keeps it, Escape restores pan 50.",
                h.Said.Last());
        }

        // ────────────────────────────────────────────────────────────────
        //  The shipped wiring names what this file exercises
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_shipped_pan_definition_matches_the_one_under_test()
        {
            // Radios.Tests cannot load the WPF assembly, so the harness above
            // mirrors KeyCommands.EnterPanMode. This scan pins the mirror to
            // the shipped source: same entry chord, same lexicon keys, same
            // anchor letter, same engine. If EnterPanMode drifts, this fails
            // and the mirror is updated CONSCIOUSLY.
            string source = ReadSource("JJFlexWpf/KeyCommands.cs");

            Assert.Contains("case Keys.P | Keys.Alt:", source);
            Assert.Contains("EnterPanMode();", source);
            Assert.Contains("Radios.ValueSubLayer.Enter(", source);
            Assert.Contains("AnchorKeys = new[] { Keys.C }", source);
            Assert.Contains("Axis = Radios.ValueLayerAxis.LeftRight", source);
            foreach (string key in new[]
            {
                "audio.pan_mode.entered",
                "audio.pan_mode.help",
                "audio.pan_mode.closed",
                "audio.pan_mode.restored",
                "audio.pan_mode.wrong_axis",
                "audio.pan_mode.no_slice",
                "settings.pan.level",
            })
            {
                Assert.Contains("\"" + key + "\"", source);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Plumbing
        // ────────────────────────────────────────────────────────────────

        private static string ReadSource(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
