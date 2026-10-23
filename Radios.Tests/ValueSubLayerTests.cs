using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The value sub-layer pattern (#305) — the engine behind the audio
    /// layer (#514), the filter layer (#516) and the two-axis form the
    /// equalisers want — pinned so the next consumer extends a tested
    /// contract rather than a convention. Every decision the pattern
    /// settles once — the exits, cancel-restores, confirm-never-writes,
    /// words-or-numbers under verbosity, the coalesced move speech, the
    /// no-key-can-strand rule — is asserted here against each selection
    /// policy the engine offers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The exact-string assertions are the review surface</b>, in the
    /// TuningContextHelpTests tradition: everything asserted verbatim below
    /// is read aloud to an operator adjusting a radio by ear, and a failing
    /// diff here is a wording change Noel has not heard yet. The audio and
    /// filter transcripts are what the brief said he judges.
    /// </para>
    /// <para>
    /// The audio and filter definitions here mirror the ones
    /// KeyCommands.EnterAudioLayer and EnterFilterLayer build (Radios.Tests
    /// cannot load the WPF assembly). The source-scan tests at the bottom
    /// pin the mirrors to the shipped source — the same lexicon keys, the
    /// same selection policies, the same select chords, the same probe — so
    /// a mirror cannot drift silently.
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
        //  Harness: the speech seams captured, verbosity injected per test.
        // ────────────────────────────────────────────────────────────────

        private sealed class Harness
        {
            public readonly List<string> Said = new();
            public readonly List<(string Text, string Key)> Moves = new();
            public readonly List<(string Text, string Key)> Answers = new();
            public VerbosityLevel Verbosity = VerbosityLevel.Chatty;
            public ValueSubLayer Layer = null!;

            /// <summary>Everything spoken, in order — the transcript.</summary>
            public readonly List<string> Transcript = new();

            public string LastSaid => Said.Last();
            public string LastMove => Moves.Last().Text;
            public string LastAnswer => Answers.Last().Text;

            public void Open(ValueSubLayerDefinition def)
            {
                Layer = ValueSubLayer.EnterForTest(
                    def,
                    (text, key) => { Moves.Add((text, key)); Transcript.Add(text); },
                    (text, key) => { Answers.Add((text, key)); Transcript.Add(text); },
                    text => { Said.Add(text); Transcript.Add(text); },
                    () => Verbosity);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  PART ONE — the single-value façade (#187's transmit power shape)
        // ════════════════════════════════════════════════════════════════

        private sealed class SingleRig
        {
            public int Value;
            public readonly List<int> Writes = new();
        }

        private static (Harness h, SingleRig rig) OpenSingle(int value, VerbosityLevel verbosity)
        {
            var h = new Harness { Verbosity = verbosity };
            var rig = new SingleRig { Value = value };
            h.Open(new ValueSubLayerDefinition
            {
                Id = "power",
                Read = () => rig.Value,
                Apply = v => { rig.Value = v; rig.Writes.Add(v); },
                Min = 0,
                Max = 100,
                Step = 5,
                FineStep = 1,
                Axis = ValueLayerAxis.UpDown,
                Anchor = 50,
                AnchorKeys = new[] { Keys.C },
                Number = v => "Power " + v,
                Words = v => v < 50 ? "low" : v == 50 ? "half" : "high",
                DescribeEntry = (cur, entry) => "Power layer. Power " + cur + ".",
                DescribeHelp = (cur, entry) => "Power layer: power " + cur + ", entered at " + entry + ".",
                DescribeClosed = () => "Power layer closed",
                DescribeRestored = v => "Back to power " + v + ". Power layer closed",
                WrongAxisHint = () => "Power uses up and down",
                PassThroughKeys = k => k == (Keys.V | Keys.Control | Keys.Shift),
            });
            return (h, rig);
        }

        [Fact]
        public void Single_entry_announces_and_seeds_from_the_live_value()
        {
            var (h, _) = OpenSingle(40, VerbosityLevel.Terse);
            Assert.Equal("Power layer. Power 40.", Assert.Single(h.Said));
            Assert.Equal(40, h.Layer.EntryValue);
            Assert.Equal(40, h.Layer.CurrentValue);
        }

        [Fact]
        public void Single_nudge_applies_the_step_and_speaks_on_the_layer_subject()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            var r = h.Layer.HandleKey(Keys.Up);

            Assert.Equal(ValueLayerKeyResult.Handled, r);
            Assert.Equal(45, rig.Value);
            var move = Assert.Single(h.Moves);
            Assert.Equal("Power 45", move.Text);
            Assert.Equal("value-layer:power", move.Key);
        }

        [Fact]
        public void Single_speaks_the_number_at_every_tier_and_adds_the_word_at_chatty()
        {
            // #536: Chatty is ADDITIVE here, as everywhere else. It used to
            // replace the number with the word, which made the verbose tier
            // the one that told you less.
            var (h, _) = OpenSingle(40, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Up);
            h.Verbosity = VerbosityLevel.Terse;
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal("Power 45, low", h.Moves[0].Text);
            Assert.Equal("Power 50", h.Moves[1].Text);
        }

        [Fact]
        public void Single_shift_makes_the_step_fine()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up | Keys.Shift);
            Assert.Equal(41, rig.Value);
        }

        [Fact]
        public void Single_rail_is_stated_once_and_writes_nothing_more()
        {
            // "Still at the rail" is how an operator learns to stop pressing.
            // The engine re-emits the form and the coalescer drops the
            // identical repeat; nothing is written at the rail.
            var (h, rig) = OpenSingle(98, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up); // 100
            h.Layer.HandleKey(Keys.Up); // clamped
            Assert.Equal(100, rig.Value);
            Assert.Equal(new[] { 100 }, rig.Writes);
            Assert.Equal(2, h.Moves.Count);
            Assert.Equal("Power 100", h.Moves[1].Text);
        }

        [Fact]
        public void Single_zero_and_the_anchor_letter_jump_to_the_anchor()
        {
            // #522: 0 is the centre now, and Home is the minimum. The anchor
            // LETTER a target declares still works — it is a second key onto
            // the same place, not a different idea.
            var (h, rig) = OpenSingle(10, VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D0));
            Assert.Equal(50, rig.Value);
            h.Layer.HandleKey(Keys.Up);
            h.Layer.HandleKey(Keys.C);
            Assert.Equal(50, rig.Value);
            Assert.Equal("Power 50", h.LastMove);
        }

        [Fact]
        public void Single_home_is_hard_left_and_end_is_hard_right()
        {
            // The convention Windows already spends these keys on, and the
            // whole reason #522 took them off centre: every range control the
            // operator meets outside this layer answers Home and End this way.
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Home));
            Assert.Equal(0, rig.Value);
            Assert.Equal("Power 0", h.LastMove);

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.End));
            Assert.Equal(100, rig.Value);
            Assert.Equal("Power 100", h.LastMove);

            // And back to the centre, out of either end.
            h.Layer.HandleKey(Keys.D0);
            Assert.Equal(50, rig.Value);
            Assert.Equal("Power 50", h.LastMove);
        }

        [Fact]
        public void Single_escape_puts_back_a_value_a_jump_moved()
        {
            // A jump is a move like any other: it touches the target, so the
            // one guaranteed way back still works.
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.End);
            Assert.Equal(100, rig.Value);
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal(40, rig.Value);
            Assert.Equal("Back to power 40. Power layer closed", h.LastSaid);
        }

        [Fact]
        public void Single_zero_on_a_target_with_no_centre_declared_is_zero()
        {
            // "0 = centre, and on an unsigned target, zero" — a target that
            // declares no anchor lands on zero, clamped into its own range.
            var h = new Harness { Verbosity = VerbosityLevel.Terse };
            var rig = new SingleRig { Value = 40 };
            h.Open(new ValueSubLayerDefinition
            {
                Id = "power",
                Read = () => rig.Value,
                Apply = v => { rig.Value = v; rig.Writes.Add(v); },
                Min = 0, Max = 100, Step = 5, FineStep = 1,
                Number = v => "Power " + v,
                DescribeEntry = (cur, entry) => "Power layer.",
                DescribeClosed = () => "Power layer closed",
            });

            h.Layer.HandleKey(Keys.D0);
            Assert.Equal(0, rig.Value);
            Assert.Equal("Power 0", h.LastMove);
        }

        [Fact]
        public void Single_a_modified_jump_key_is_refused_and_the_layer_stays()
        {
            // Ctrl+Home means "top of the document" everywhere else; inside a
            // layer that does not bind it, it is simply not ours. Since #778b
            // (Noel, 2026-10-19 14:35) the only ways out are Enter, Escape and
            // the JJ key, so it is refused and the layer is exactly as it was.
            // Until then it confirmed, said the layer closed and travelled on,
            // and the "closed" was cut off by whatever the key said next.
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled,
                h.Layer.HandleKey(Keys.Home | Keys.Control));
            Assert.Equal(40, rig.Value);
            Assert.True(h.Layer.IsLive);
            Assert.DoesNotContain("Power layer closed", h.Said);
        }

        [Fact]
        public void Single_a_key_the_host_says_is_a_command_outside_is_refused_too()
        {
            // The other half of the ruling: the SAME chord, with a host that
            // answers "yes, that is a command out here", is refused just the
            // same. Whether a key means something outside no longer decides
            // whether it leaves; it decides only whether a near miss may be
            // offered for it. Real hotkeys such as Ctrl+F stay inside.
            var h = new Harness { Verbosity = VerbosityLevel.Terse };
            var rig = new SingleRig { Value = 40 };
            h.Open(new ValueSubLayerDefinition
            {
                Id = "power",
                Read = () => rig.Value,
                Apply = v => { rig.Value = v; rig.Writes.Add(v); },
                Min = 0, Max = 100, Step = 5, FineStep = 1,
                Number = v => "Power " + v,
                DescribeEntry = (cur, entry) => "Power layer.",
                DescribeClosed = () => "Power layer closed",
                MeansSomethingOutside = k => k == (Keys.Home | Keys.Control),
            });
            h.Layer.HandleKey(Keys.Up);

            Assert.Equal(ValueLayerKeyResult.Handled,
                h.Layer.HandleKey(Keys.Home | Keys.Control));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(45, rig.Value);
            Assert.DoesNotContain("Power layer closed", h.Said);
        }

        [Fact]
        public void Single_enter_confirms_keeps_and_writes_nothing()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            var r = h.Layer.HandleKey(Keys.Return);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.False(h.Layer.IsLive);
            Assert.Equal(new[] { 45 }, rig.Writes);
            Assert.Equal("Power layer closed", h.LastSaid);
        }

        [Fact]
        public void Single_escape_cancels_and_puts_the_entry_value_back_out_loud()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            var r = h.Layer.HandleKey(Keys.Escape);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.Equal(40, rig.Value);
            Assert.Equal(new[] { 45, 40 }, rig.Writes);
            Assert.Equal("Back to power 40. Power layer closed", h.LastSaid);
        }

        [Fact]
        public void Single_a_stuck_modifier_cannot_flip_cancel_into_keep()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(ValueLayerKeyResult.Closed, h.Layer.HandleKey(Keys.Escape | Keys.Shift));
            Assert.Equal(40, rig.Value);
        }

        [Fact]
        public void Single_an_unknown_letter_stays_and_the_arrows_still_work()
        {
            // #778. X is nothing in this layer and nothing outside it. It used
            // to confirm, announce the close and travel; now it is refused and
            // the layer is exactly as it was — the next arrow moves the same
            // value, and nothing was written on the way.
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            var r = h.Layer.HandleKey(Keys.X);

            Assert.Equal(ValueLayerKeyResult.Handled, r);
            Assert.True(h.Layer.IsLive);
            Assert.Equal(new[] { 45 }, rig.Writes);
            Assert.DoesNotContain("Power layer closed", h.Said);

            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(50, rig.Value);
        }

        [Fact]
        public void Single_ctrl_j_hands_off_silently_keeping_the_value()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            int saidBefore = h.Said.Count;
            var r = h.Layer.HandleKey(Keys.J | Keys.Control);

            Assert.Equal(ValueLayerKeyResult.ClosedHandOff, r);
            Assert.False(h.Layer.IsLive);
            Assert.Equal(saidBefore, h.Said.Count);
            Assert.Equal(new[] { 45 }, rig.Writes);
        }

        [Fact]
        public void Single_a_forced_drop_keeps_and_says_nothing()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            int saidBefore = h.Said.Count;
            h.Layer.Drop();

            Assert.False(h.Layer.IsLive);
            Assert.Equal(saidBefore, h.Said.Count);
            Assert.Equal(new[] { 45 }, rig.Writes);
        }

        [Theory]
        [InlineData(Keys.F4 | Keys.Alt)]
        [InlineData(Keys.F1)]
        [InlineData(Keys.V | Keys.Control | Keys.Shift)]
        public void Single_system_and_whitelisted_chords_pass_through_and_the_layer_stays(Keys k)
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.PassThrough, h.Layer.HandleKey(k));
            Assert.True(h.Layer.IsLive);
            Assert.Empty(rig.Writes);
        }

        [Fact]
        public void Single_the_wrong_arrow_pair_hints_and_never_ejects()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Left));
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Right | Keys.Shift));
            Assert.True(h.Layer.IsLive);
            Assert.Empty(rig.Writes);
            Assert.Equal("Power uses up and down", h.LastSaid);
        }

        [Theory]
        [InlineData(Keys.Oem2)]
        [InlineData(Keys.Oem2 | Keys.Shift)]
        [InlineData(Keys.H)]
        public void Single_help_speaks_state_without_changing_anything(Keys k)
        {
            var (h, rig) = OpenSingle(50, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Down);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(k));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(45, rig.Value);
            Assert.Equal("Power layer: power 45, entered at 50.", h.LastSaid);
        }

        [Fact]
        public void The_exit_hook_fires_on_every_close_path()
        {
            var seen = new List<ValueLayerExit>();
            ValueSubLayerDefinition Def() => new()
            {
                Id = "x", Read = () => 1, Apply = _ => { }, Number = v => v.ToString(),
                DescribeClosed = () => "closed", Exited = why => seen.Add(why),
            };
            var h1 = new Harness(); h1.Open(Def()); h1.Layer.HandleKey(Keys.Return);
            var h2 = new Harness(); h2.Open(Def()); h2.Layer.HandleKey(Keys.Escape);
            var h3 = new Harness(); h3.Open(Def()); h3.Layer.Drop();
            var h4 = new Harness(); h4.Open(Def()); h4.Layer.HandleKey(Keys.J | Keys.Control);
            Assert.Equal(new[] { ValueLayerExit.Confirmed, ValueLayerExit.Cancelled,
                                 ValueLayerExit.Dropped, ValueLayerExit.Confirmed }, seen);
        }

        // ════════════════════════════════════════════════════════════════
        //  PART TWO — the audio layer (#514): letters pick, arrows adjust,
        //  pan also answers Left/Right, Escape puts back everything moved
        // ════════════════════════════════════════════════════════════════

        private sealed class AudioRig
        {
            public int Headphone = 40, PcVolume = 12, Mic = 30, Lineout = 50, Compander = 20, Processor = 0;
            public bool CompanderOn = true, ProcessorOn = false;
            public string Slice = "A";
            public readonly Dictionary<string, int> Pan = new() { ["A"] = 40, ["C"] = 50 };
            public readonly Dictionary<string, int> Volume = new() { ["A"] = 60, ["C"] = 70 };
            public readonly Dictionary<string, bool> Muted = new() { ["A"] = false, ["C"] = false };
            public bool PcAudio, PcAudioCanStart = true, Binaural;
            public readonly List<string> Writes = new();
            public int Persisted;
            public readonly List<string> Jumps = new();
        }

        // ── The #547 near-miss hook, as the shipped hosts wire it ───────
        //
        // KeyCommands answers DescribeNearMiss out of KeyInventory's rows for
        // the layer — the same table H lists — through
        // LeaderChordParser.LayerNearMissCandidates. Radios.Tests cannot load
        // either, so the tables below stand in with the same shape and the
        // same words, and the ORDERING rule, which is the part that decides
        // which neighbour gets named, is exercised for real.

        private static readonly Dictionary<Keys, (string Key, string What)> AudioLetterChords = new()
        {
            [Keys.V] = ("V", "Slice volume"),
            [Keys.M] = ("M", "Mic level"),
            [Keys.C] = ("C", "Compander level"),
            [Keys.M | Keys.Control] = ("Ctrl+M", "Mute or unmute the slice you're on"),
            [Keys.B | Keys.Control] = ("Ctrl+B", "Binaural receive on or off"),
            [Keys.B | Keys.Shift] = ("Shift+B", "Jump to that slice without leaving the layer"),
            [Keys.F | Keys.Shift] = ("Shift+F", "Jump to that slice without leaving the layer"),
        };

        private static readonly Dictionary<Keys, (string Key, string What)> FilterLetterChords = new()
        {
            [Keys.S] = ("S", "Speak the whole filter"),
            [Keys.T] = ("T", "Work on the transmit filter"),
            [Keys.R] = ("R", "Work on the receive filter of the slice you're on"),
        };

        private static string? AudioLayerNearMiss(Keys pressed, VerbosityLevel verbosity)
            => LayerNearMiss(AudioLetterChords, "audio.audio_layer.name", pressed, verbosity);

        private static string? FilterLayerNearMiss(Keys pressed, VerbosityLevel verbosity)
            => LayerNearMiss(FilterLetterChords, "audio.filter_layer.name", pressed, verbosity);

        // ── The #778 unknown-key hook, as the shipped hosts wire it ──────
        //
        // KeyCommands.LayerUnknownKey names the layer through
        // KeyLayerHelp.LayerName and picks the lexicon line from the engine's
        // reason. The same two lexicon keys, the same layer-name argument.

        private static bool InPlaceChord(Keys k)
        {
            Keys code = k & Keys.KeyCode, mods = k & Keys.Modifiers;
            if (code == Keys.Space) return mods == Keys.Control || mods == Keys.Shift;
            return code == Keys.OemOpenBrackets || code == Keys.OemCloseBrackets;
        }

        private const string NotAKeyInAudio = "Not a key in the Audio layer. Press H to list available keys for this layer.";
        private const string TypedNumberInAudio = "A typed number does nothing in the Audio layer. Use arrow keys to change the selected number.";
        private const string FineStep = "Shift with the arrows moves it in fine steps.";

        private static string LayerUnknownKey(string layerNameKey, Keys pressed, ValueLayerRefusal why)
            => Lexicon.Get(why == ValueLayerRefusal.TypedEntry
                    ? "audio.value_layer.typed_number"
                    : "audio.value_layer.unknown_key",
                ("layer", Lexicon.Get(layerNameKey)));

        private static string? LayerNearMiss(Dictionary<Keys, (string Key, string What)> chords,
            string layerNameKey, Keys pressed, VerbosityLevel verbosity)
        {
            Keys code = pressed & Keys.KeyCode;
            if (code < Keys.A || code > Keys.Z) return null;
            if (chords.ContainsKey(pressed)) return null;

            foreach (var candidate in LeaderChordParser.LayerNearMissCandidates(pressed))
            {
                if (!chords.TryGetValue(candidate, out var hit)) continue;
                return Lexicon.Get("audio.value_layer.near_miss", verbosity,
                    ("pressed", KeyName(pressed)),
                    ("layer", Lexicon.Get(layerNameKey)),
                    ("alt", hit.Key), ("what", hit.What));
            }
            return null;
        }

        /// <summary>KeyManifest.FormatKey's answer for the letter chords, which
        /// is all this file needs and all it can reach.</summary>
        private static string KeyName(Keys k)
            => ((k & Keys.Control) != 0 ? "Ctrl+" : "")
             + ((k & Keys.Shift) != 0 ? "Shift+" : "")
             + (k & Keys.KeyCode).ToString();

        private static (Harness h, AudioRig rig) OpenAudio(VerbosityLevel verbosity, bool onPan = false,
            ValueLayerCues? cues = null)
        {
            var h = new Harness { Verbosity = verbosity };
            var rig = new AudioRig();
            bool pcTouched = false;
            int centre = 50;

            ValueTarget Level(string id, string nameKey, Keys select, Func<int> read, Action<int> apply,
                int min, int max, int step, string numberKey, string? selectedKey = null, Func<string>? note = null)
            {
                string name = Lexicon.Get(nameKey);
                return new ValueTarget
                {
                    Id = id, Name = name, SelectKey = select, Read = read, Apply = apply,
                    Min = min, Max = max, Step = step, FineStep = 1, Axes = ValueLayerAxes.UpDown,
                    Number = v => Lexicon.Get(numberKey, ("value", v)),
                    DescribeSelected = selectedKey == null ? null : v => Lexicon.Get(selectedKey, ("value", v)),
                    Note = note,
                    WrongAxisHint = () => Lexicon.Get("audio.audio_layer.uses_up_down", ("target", name)),
                };
            }

            var headphone = Level("headphone", "audio.audio_layer.name_headphone", Keys.H | Keys.Control,
                () => rig.Headphone, v => { rig.Headphone = v; rig.Writes.Add("headphone " + v); }, 0, 100, 5,
                "audio.audio_layer.headphone", "audio.audio_layer.headphone_selected");
            var pcOutput = Level("pc-output", "audio.audio_layer.name_pc_output", Keys.O,
                () => rig.PcVolume, v => { rig.PcVolume = v; rig.Writes.Add("pc " + v); pcTouched = true; }, 0, 24, 1,
                "audio.audio_layer.pc_output");
            var mic = Level("mic", "audio.audio_layer.name_mic", Keys.M,
                () => rig.Mic, v => { rig.Mic = v; rig.Writes.Add("mic " + v); }, 0, 100, 5,
                "audio.audio_layer.mic");
            var lineout = Level("lineout", "audio.audio_layer.name_lineout", Keys.L,
                () => rig.Lineout, v => { rig.Lineout = v; rig.Writes.Add("lineout " + v); }, 0, 100, 5,
                "audio.audio_layer.lineout", "audio.audio_layer.lineout_selected");
            var compander = Level("compander", "audio.audio_layer.name_compander", Keys.C,
                () => rig.Compander, v => { rig.Compander = v; rig.Writes.Add("compander " + v); }, 0, 100, 5,
                "audio.audio_layer.compander",
                note: () => rig.CompanderOn ? "" : Lexicon.Get("audio.audio_layer.compander_is_off_suffix"));
            string processorName = Lexicon.Get("audio.audio_layer.name_processor");
            var processor = new ValueTarget
            {
                Id = "processor", Name = processorName, SelectKey = Keys.S,
                Read = () => rig.Processor, Apply = v => { rig.Processor = v; rig.Writes.Add("processor " + v); },
                Min = 0, Max = 2, Step = 1, FineStep = 1, Axes = ValueLayerAxes.UpDown,
                Number = v => Lexicon.Get("audio.audio_layer.processor",
                    ("name", v == 1 ? "DX" : v == 2 ? "DX plus" : "Normal")),
                Note = () => rig.ProcessorOn ? "" : Lexicon.Get("audio.audio_layer.processor_is_off_suffix"),
                WrongAxisHint = () => Lexicon.Get("audio.audio_layer.uses_up_down", ("target", processorName)),
            };
            var pan = new ValueTarget
            {
                Id = "pan", Name = "", SelectKey = Keys.P, PerSlice = true,
                Read = () => rig.Pan[rig.Slice],
                Apply = v => { rig.Pan[rig.Slice] = v; rig.Writes.Add("pan " + rig.Slice + " " + v); },
                Min = 0, Max = 100, Step = 5, FineStep = 1, Axes = ValueLayerAxes.Both, Anchor = centre,
                Number = v => Lexicon.Get("settings.pan.level", ("level", v)),
                // No Words: #536 ruled the scale out entirely, 2026-09-06.
                DescribeSelected = v => Lexicon.Get("audio.audio_layer.pan_selected", h.Verbosity,
                    ("letter", rig.Slice), ("level", v)),
            };

            // Sprint 44 Track N (#524): slice volume, and the three switches.
            string sliceVolumeName = Lexicon.Get("audio.audio_layer.name_slice_volume");
            var sliceVolume = new ValueTarget
            {
                Id = "slice-volume", Name = sliceVolumeName, SelectKey = Keys.V, PerSlice = true,
                Read = () => rig.Volume[rig.Slice],
                Apply = v => { rig.Volume[rig.Slice] = v; rig.Writes.Add("volume " + rig.Slice + " " + v); },
                Min = 0, Max = 100, Step = 5, FineStep = 1, Axes = ValueLayerAxes.UpDown,
                Number = v => Lexicon.Get("audio.audio_layer.slice_volume", ("value", v)),
                DescribeSelected = v => Lexicon.Get("audio.audio_layer.slice_volume_selected", ("letter", rig.Slice), ("value", v)),
                WrongAxisHint = () => Lexicon.Get("audio.audio_layer.uses_up_down", ("target", sliceVolumeName)),
            };
            ValueTarget Switch(string id, Keys chord, Func<bool> read, Action<bool> apply,
                Func<bool, string> say, bool perSlice = false, Func<bool, string>? describe = null)
                => new ValueTarget
                {
                    Id = id, Name = "", ToggleKey = chord, PerSlice = perSlice,
                    Read = () => read() ? 1 : 0, Apply = v => apply(v == 1),
                    Min = 0, Max = 1, Step = 1, FineStep = 1, Axes = ValueLayerAxes.None,
                    Number = v => say(v == 1),
                    DescribeSelected = describe == null ? null : v => describe(v == 1),
                };
            var mute = Switch("mute", Keys.M | Keys.Control,
                () => rig.Muted[rig.Slice],
                on => { rig.Muted[rig.Slice] = on; rig.Writes.Add("mute " + rig.Slice + " " + on); },
                on => Lexicon.Get(on ? "audio.mute.slice_muted" : "audio.mute.slice_unmuted", ("letter", rig.Slice)),
                perSlice: true);
            var pcAudio = Switch("pc-audio", Keys.P | Keys.Control,
                () => rig.PcAudio,
                on => { if (rig.PcAudio != on) { rig.Writes.Add("pc audio " + on); if (!on || rig.PcAudioCanStart) rig.PcAudio = on; } },
                on => Lexicon.Get(on ? "audio.pc_audio.on" : "audio.pc_audio.off"),
                describe: wanted => rig.PcAudio ? Lexicon.Get("audio.pc_audio.on")
                    : wanted ? Lexicon.Get("audio.pc_audio.could_not_start")
                    : Lexicon.Get("audio.pc_audio.off"));
            var binaural = Switch("binaural", Keys.B | Keys.Control,
                () => rig.Binaural,
                on => { rig.Binaural = on; rig.Writes.Add("binaural " + on); },
                on => Lexicon.Get(on ? "audio.binaural.on" : "audio.binaural.off"));

            var targets = new List<ValueTarget>
            {
                sliceVolume, headphone, pcOutput, mic, lineout, compander, processor, pan,
                mute, pcAudio, binaural,
            };
            var def = new ValueSubLayerDefinition
            {
                Id = "audio",
                Selection = ValueLayerSelection.ByLetter,
                Targets = targets,
                InitialTarget = onPan ? targets.IndexOf(pan) : -1,
                DescribeLayerEntry = layer => layer.CurrentTarget == pan
                    ? Lexicon.Get("audio.audio_layer.entered_on_pan", h.Verbosity, ("target", layer.DescribeTarget(pan)))
                    : Lexicon.Get("audio.audio_layer.entered", h.Verbosity),
                // The shipped fallback is Track K's KeyLayerHelp.SpokenList,
                // which Radios.Tests cannot load; a stub stands in for it.
                DescribeLayerHelp = layer => "HELP: " + Lexicon.Get("audio.audio_layer.name"),
                DescribeClosed = () => Lexicon.Get("audio.audio_layer.closed"),
                DescribeLayerRestored = (layer, restored) => Lexicon.Get(
                    restored.Count == 0
                        ? "audio.audio_layer.restored_nothing"
                        : "audio.audio_layer.restored"),
                PickTargetHint = () => Lexicon.Get("audio.audio_layer.pick_target_first"),
                // #547. The shipped hooks read the live key registry and the
                // layer's inventory rows, neither of which Radios.Tests can
                // load; these stand in with the same shape and the same
                // answers for the chords the tests press. Ctrl+F is the
                // registry chord that proves a key WITH somewhere to go still
                // leaves.
                // Shift+M stands in for MuteAllSlices (Radio scope): the one
                // Shift-only LETTER the default table binds, so the letter
                // rule's registry check has something to find (#778).
                MeansSomethingOutside = k => k == (Keys.F | Keys.Control) || k == (Keys.M | Keys.Shift),
                DescribeNearMiss = k => AudioLayerNearMiss(k, h.Verbosity),
                DescribeUnknownKey = (k, why) => LayerUnknownKey("audio.audio_layer.name", k, why),
                DescribeFineStep = () => Lexicon.Get("audio.value_layer.fine_step"),
                // #778b: the shipped hosts' ActsInPlaceInsideALayer — the
                // verbosity cycle from the registry (Ctrl+Shift+V by default)
                // and KeyCommands.IsInPlaceChord: Ctrl+Space, Shift+Space and
                // the brackets in every form. JJFlexWpf.Tests'
                // ValueLayerRoutingTests drive the real one.
                PassThroughKeys = k => k == (Keys.V | Keys.Control | Keys.Shift) || InPlaceChord(k),
                HostKeys = k =>
                {
                    if ((k & Keys.Modifiers) != Keys.Shift) return false;
                    Keys code = k & Keys.KeyCode;
                    if (code < Keys.A || code > Keys.H) return false;
                    rig.Slice = ((char)('A' + (code - Keys.A))).ToString();
                    rig.Jumps.Add(rig.Slice);
                    h.Transcript.Add("Slice " + rig.Slice + " active");
                    h.Layer.Rebind(t => t.PerSlice);
                    return true;
                },
                Exited = why => { if (pcTouched) rig.Persisted++; },
                Cues = cues ?? new ValueLayerCues(),
            };
            h.Open(def);
            return (h, rig);
        }

        [Fact]
        public void Audio_entry_says_where_you_are_and_what_is_picked_and_stops()
        {
            // #524: entry was 175 characters, every time — helpful once,
            // punishing forever. Where you are, what is picked, and H does
            // the rest (#519). Noel ruled on 2026-09-02 that chatty need not
            // recite the keys either: it points at H, which is the surface
            // that lists them properly. So chatty is terse plus one sentence,
            // and the letters live in exactly one place.
            var (t, _) = OpenAudio(VerbosityLevel.Terse);
            Assert.Equal("Audio layer.", Assert.Single(t.Said));
            Assert.Null(t.Layer.CurrentTarget);

            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            Assert.Equal("Audio layer. Press H for a list of keys, Escape to revert changes, Enter to accept.",
                Assert.Single(h.Said));
            Assert.Null(h.Layer.CurrentTarget);
        }

        [Fact]
        public void Audio_entry_at_terse_says_where_you_are_and_no_more()
        {
            // #528: Terse is values and transitions, not hints. Entering a
            // layer is a transition, so Terse says where you are and what is
            // picked — nothing is picked yet, so it is the name alone. Which
            // letters pick a target is the lesson H exists to give; until
            // this it was recited on every entry at every verbosity, and
            // Track I's own transcript at "Terse" still named every letter.
            var (h, _) = OpenAudio(VerbosityLevel.Terse);
            Assert.Equal("Audio layer.", Assert.Single(h.Said));
        }

        [Fact]
        public void Audio_entry_at_off_would_say_the_same_as_terse_if_it_spoke_at_all()
        {
            // A level must never say more than the level above it. The entry
            // is emitted at Terse, so at Off the speech gate drops it and the
            // enter tone is the whole answer; the tier text is pinned anyway
            // so a future ladder edit cannot make Off wordier than Terse.
            Assert.Equal(
                Lexicon.Get("audio.audio_layer.entered", VerbosityLevel.Terse),
                Lexicon.Get("audio.audio_layer.entered", VerbosityLevel.Critical));
            Assert.Equal(
                Lexicon.Get("audio.filter_layer.entered", VerbosityLevel.Terse),
                Lexicon.Get("audio.filter_layer.entered", VerbosityLevel.Critical));
        }

        [Fact]
        public void Audio_an_arrow_before_a_letter_hints_and_stays()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Up));
            Assert.True(h.Layer.IsLive);
            Assert.Empty(rig.Writes);
            Assert.Equal("Pick a target first: V, Ctrl+H, O, M, L, C, S, X, D, or P.", h.LastSaid);
        }

        // ── Sprint 44 Track N (#524): slice volume and the switches ──

        [Fact]
        public void Audio_v_picks_slice_volume_and_up_adjusts_it()
        {
            // Slice volume is NOT the PC output volume: one is how loud the
            // slice sits in the mix, the other how loud this computer plays.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.V));
            Assert.Equal("Slice A volume 60", h.LastAnswer);
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal("Volume 65", h.LastMove);
            Assert.Equal(65, rig.Volume["A"]);
            Assert.Equal(12, rig.PcVolume);
        }

        [Fact]
        public void Audio_slice_volume_follows_a_slice_jump_like_pan()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.V); h.Layer.HandleKey(Keys.Up);              // A: 65
            h.Layer.HandleKey(Keys.C | Keys.Shift);
            Assert.Equal("Slice C volume 70", h.LastAnswer);
            h.Layer.HandleKey(Keys.Down);                                       // C: 65
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal(65, rig.Volume["A"]);                                  // kept — confirmed by leaving
            Assert.Equal(70, rig.Volume["C"]);                                  // restored
            Assert.Equal("All changes reverted. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_ctrl_m_flips_mute_in_one_press_and_leaves_the_pick_alone()
        {
            // Ctrl is the toggle tier (#515): a switch is one press, never
            // "pick it, then arrow", and the arrows still move what was
            // picked before.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.M);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.M | Keys.Control));
            Assert.Equal("Slice A muted", h.LastAnswer);
            Assert.True(rig.Muted["A"]);
            Assert.Equal("mic", h.Layer.CurrentTarget!.Id);
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(35, rig.Mic);
            h.Layer.HandleKey(Keys.M | Keys.Control);
            Assert.Equal("Slice A unmuted", h.LastAnswer);
            Assert.False(rig.Muted["A"]);
        }

        [Fact]
        public void Audio_a_switch_is_not_a_pick()
        {
            var (h, _) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.B | Keys.Control);
            Assert.Null(h.Layer.CurrentTarget);
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal("Pick a target first: V, Ctrl+H, O, M, L, C, S, X, D, or P.", h.LastSaid);
        }

        [Fact]
        public void Audio_escape_puts_the_switches_back_with_everything_else()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.M | Keys.Control);
            h.Layer.HandleKey(Keys.B | Keys.Control);
            h.Layer.HandleKey(Keys.V); h.Layer.HandleKey(Keys.Up);
            h.Layer.HandleKey(Keys.Escape);
            Assert.False(rig.Muted["A"]);
            Assert.False(rig.Binaural);
            Assert.Equal(60, rig.Volume["A"]);
            Assert.Equal("All changes reverted. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_enter_keeps_a_switch_and_writes_nothing_more()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.B | Keys.Control);
            Assert.Equal("Binaural on", h.LastAnswer);
            h.Layer.HandleKey(Keys.Return);
            Assert.True(rig.Binaural);
            Assert.Equal(new[] { "binaural True" }, rig.Writes);
        }

        [Fact]
        public void Audio_mute_follows_a_slice_jump()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.M | Keys.Control);                          // A muted
            h.Layer.HandleKey(Keys.C | Keys.Shift);
            h.Layer.HandleKey(Keys.M | Keys.Control);
            Assert.Equal("Slice C muted", h.LastAnswer);
            h.Layer.HandleKey(Keys.Escape);
            Assert.True(rig.Muted["A"]);                                        // kept — confirmed by leaving
            Assert.False(rig.Muted["C"]);                                       // restored
        }

        [Fact]
        public void Audio_pc_audio_answers_with_the_outcome_not_the_wish()
        {
            // Turning PC audio on can fail (no usable sound device). The
            // answer reads the rig back — PCAudioHandler's rule, kept in the
            // layer — so "PC audio on" is never said while nothing plays.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            rig.PcAudioCanStart = false;
            h.Layer.HandleKey(Keys.P | Keys.Control);
            Assert.Equal("PC audio could not start, still off", h.LastAnswer);
            Assert.False(rig.PcAudio);

            var (ok, okRig) = OpenAudio(VerbosityLevel.Terse);
            ok.Layer.HandleKey(Keys.P | Keys.Control);
            Assert.Equal("PC audio on", ok.LastAnswer);
            Assert.True(okRig.PcAudio);
            ok.Layer.HandleKey(Keys.Escape);
            Assert.False(okRig.PcAudio);
            Assert.Equal("All changes reverted. Audio layer closed", ok.LastSaid);
        }

        [Fact]
        public void Audio_the_toggle_api_flips_a_switch_for_a_knob_host()
        {
            // #200: the semantic surface, no keyboard.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            var mute = h.Layer.Targets.Single(t => t.Id == "mute");
            h.Layer.Toggle(mute);
            Assert.True(rig.Muted["A"]);
            Assert.Equal("Slice A muted", h.LastAnswer);
            h.Layer.Toggle(h.Layer.Targets.Single(t => t.Id == "mic"));   // not a switch: left alone
            Assert.Equal(30, rig.Mic);
        }

        [Fact]
        public void The_help_cue_plays_before_the_surface_opens()
        {
            // The host's list is MODAL: the cue must sound before it opens,
            // or the operator hears the "help" tone after they have already
            // arrowed through the list and closed it (#524).
            var order = new List<string>();
            var h = new Harness { Verbosity = VerbosityLevel.Terse };
            h.Open(new ValueSubLayerDefinition
            {
                Id = "t",
                Selection = ValueLayerSelection.ByLetter,
                Targets = new List<ValueTarget>
                {
                    new() { Id = "x", SelectKey = Keys.X, Read = () => 1, Apply = _ => { }, Number = v => "x " + v },
                },
                DescribeClosed = () => "closed",
                DescribeLayerHelp = _ => "spoken help",
                ListCommands = () => { order.Add("list"); return true; },
                OpenExplorer = () => { order.Add("explorer"); return false; },
                Cues = new ValueLayerCues { Help = () => order.Add("cue") },
            });
            h.Layer.HandleKey(Keys.H);
            Assert.Equal(new[] { "cue", "list" }, order);
            Assert.Empty(h.Said);                                               // the surface spoke, not the engine

            order.Clear();
            h.Layer.HandleKey(Keys.Oem2 | Keys.Shift);
            Assert.Equal(new[] { "cue", "explorer" }, order);
            Assert.Equal("spoken help", h.LastSaid);                            // no surface: the sentence, once
        }

        [Fact]
        public void Audio_ctrl_h_picks_headphone_and_up_adjusts_it()
        {
            // The transcript the brief asked for: enter, pick one target,
            // move it once, leave.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.H | Keys.Control));
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Up));
            Assert.Equal(ValueLayerKeyResult.Closed, h.Layer.HandleKey(Keys.Return));

            Assert.Equal(new[]
            {
                "Audio layer.",
                "On-radio headphone 40",
                "Headphone 45",
                "Audio layer closed",
            }, h.Transcript);
            Assert.Equal(45, rig.Headphone);
            Assert.Equal("value-layer:audio:headphone", h.Answers[0].Key);
            Assert.Equal("value-layer:audio:headphone", h.Moves[0].Key);
        }

        [Fact]
        public void Audio_plain_h_is_help_not_headphone()
        {
            // #514 reverses the old volume-mode decision: H is help in every
            // layer, and headphone wears Ctrl.
            var (h, _) = OpenAudio(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.H));
            Assert.Null(h.Layer.CurrentTarget);
            Assert.Equal("HELP: Audio layer", h.LastSaid);
        }

        [Fact]
        public void Audio_left_and_right_hint_on_a_target_that_only_takes_up_and_down()
        {
            // No tone is wired in this harness, so the words are the only
            // feedback and are spoken at every level — the never-silent half
            // of #528. The tone-wired half is the block that follows.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.M);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Left));
            Assert.Equal("Mic level uses up and down", h.LastSaid);
            Assert.Empty(rig.Writes);
        }

        // ────────────────────────────────────────────────────────────────
        //  #528 — a refusal scales by VERBOSITY, never by experience. The
        //  invalid tone always; the teaching sentence only at Chatty, unless
        //  the tone cannot sound, in which case the words stand in at every
        //  level. What HAPPENS never changes: the layer stays open, nothing
        //  is written. Ruled by Noel 2026-09-02.
        // ────────────────────────────────────────────────────────────────

        private sealed class ToneCounter
        {
            public int Invalid;
            public bool Audible = true;
            public ValueLayerCues Cues => new ValueLayerCues
            {
                Invalid = () => Invalid++,
                Audible = () => Audible,
            };
        }

        [Fact]
        public void Refusal_at_chatty_is_the_tone_and_the_sentence()
        {
            var tones = new ToneCounter();
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty, cues: tones.Cues);
            int saidBefore = h.Said.Count;

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Up));

            Assert.Equal(1, tones.Invalid);
            Assert.Equal("Pick a target first: V, Ctrl+H, O, M, L, C, S, X, D, or P.", h.LastSaid);
            Assert.Equal(saidBefore + 1, h.Said.Count);
            Assert.True(h.Layer.IsLive);
            Assert.Empty(rig.Writes);
        }

        [Theory]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Critical)]
        public void Refusal_below_chatty_is_the_tone_alone(VerbosityLevel level)
        {
            // Noel: "after a while, people will know that they need to press
            // H or slash to get info." The mechanism for "after a while" is
            // the operator turning verbosity down, not the app guessing.
            var tones = new ToneCounter();
            var (h, rig) = OpenAudio(level, cues: tones.Cues);
            int saidBefore = h.Said.Count;

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Up));
            h.Layer.HandleKey(Keys.M);
            saidBefore = h.Said.Count;   // picking M spoke the mic level; that is a value, not a refusal
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Left));

            Assert.Equal(2, tones.Invalid);
            Assert.Equal(saidBefore, h.Said.Count);
            Assert.DoesNotContain(h.Said, s => s.StartsWith("Pick a target", StringComparison.Ordinal));
            Assert.DoesNotContain(h.Said, s => s.EndsWith("uses up and down", StringComparison.Ordinal));
            Assert.True(h.Layer.IsLive);
            Assert.Empty(rig.Writes);
        }

        [Theory]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Critical)]
        public void Refusal_below_chatty_speaks_when_the_tone_cannot_sound(VerbosityLevel level)
        {
            // Earcons off, or their category off: the tone is wired but will
            // not be heard, so the words come back. A refused key that
            // produces nothing is the invisible failure — a key that
            // registered and a key that did not sound identical.
            var tones = new ToneCounter { Audible = false };
            var (h, rig) = OpenAudio(level, cues: tones.Cues);

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Up));

            Assert.Equal(1, tones.Invalid);   // still invoked; the player itself is what is gated
            Assert.Equal("Pick a target first: V, Ctrl+H, O, M, L, C, S, X, D, or P.", h.LastSaid);
            Assert.True(h.Layer.IsLive);
            Assert.Empty(rig.Writes);
        }

        [Fact]
        public void Refusal_never_says_more_at_a_lower_level_than_at_a_higher_one()
        {
            // The monotonic property the whole control rests on: turning
            // verbosity DOWN must never make the app say MORE. Measured as
            // words spoken by one refusal at each level, tone audible.
            int SaidBy(VerbosityLevel level)
            {
                var tones = new ToneCounter();
                var (h, _) = OpenAudio(level, cues: tones.Cues);
                int before = h.Said.Count;
                h.Layer.HandleKey(Keys.Up);
                return h.Said.Skip(before).Sum(s => s.Length);
            }

            int chatty = SaidBy(VerbosityLevel.Chatty);
            int terse = SaidBy(VerbosityLevel.Terse);
            int off = SaidBy(VerbosityLevel.Critical);
            Assert.True(chatty > terse, $"chatty {chatty} should exceed terse {terse}");
            Assert.True(terse >= off, $"terse {terse} should not be shorter than off {off}");
        }

        [Fact]
        public void Audio_pan_answers_both_arrow_pairs_and_zero_centres()
        {
            // #536: the number, at every tier, with nothing after it.
            // 45 and 49 were both "slightly left" — the word could not tell
            // them apart, which was the original complaint; the ruling of
            // 2026-09-06 then dropped the word rather than pairing it with
            // the figure, since pan is 0-100 like slice volume and neither
            // announces a unit. This test still pins 45/50/49 because those
            // are the values the old word collapsed together.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P);
            Assert.Equal("Pan, slice A, 40", h.LastAnswer);
            h.Layer.HandleKey(Keys.Right);   // 45
            h.Layer.HandleKey(Keys.Up);      // 50
            h.Layer.HandleKey(Keys.Down | Keys.Shift); // 49
            Assert.Equal(49, rig.Pan["A"]);
            Assert.Equal(new[] { "Pan 45", "Pan 50", "Pan 49" },
                h.Moves.Select(m => m.Text));
            h.Layer.HandleKey(Keys.D0);
            Assert.Equal(50, rig.Pan["A"]);
            Assert.Equal("Pan 50", h.LastMove);
        }

        [Theory]
        [InlineData(VerbosityLevel.Critical)]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Chatty)]
        public void Audio_pan_never_speaks_a_position_without_the_number(VerbosityLevel level)
        {
            // #536's guard, pointed the way the defect came from. Chatty used
            // to REPLACE the figure with a band name, so the tier that says
            // more said less — and a band name is not a place you can come
            // back to. Noel: "percent is always better." Whatever a tier adds
            // on top, the number is in it, at every level.
            var (h, _) = OpenAudio(level);
            h.Layer.HandleKey(Keys.P);
            Assert.Contains("40", h.LastAnswer);
            h.Layer.HandleKey(Keys.Right);
            Assert.Contains("45", h.LastMove);
        }

        [Fact]
        public void Audio_home_and_end_run_pan_to_both_extremes()
        {
            // #522. Pan is the target that made Home mean centre in the first
            // place, back when pan was its own mode, and it still shows the
            // general rule best: Home and End are the ENDS of the range, and
            // zero is the anchor. It used to say "hard left" and "hard right"
            // here; #536 dropped the words on 2026-09-06, so the extremes are
            // now named by the only thing that was ever precise about them.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P);

            h.Layer.HandleKey(Keys.Home);
            Assert.Equal(0, rig.Pan["A"]);
            Assert.Equal("Pan 0", h.LastMove);

            h.Layer.HandleKey(Keys.End);
            Assert.Equal(100, rig.Pan["A"]);
            Assert.Equal("Pan 100", h.LastMove);

            h.Layer.HandleKey(Keys.D0);
            Assert.Equal(50, rig.Pan["A"]);
            Assert.Equal("Pan 50", h.LastMove);
        }

        [Fact]
        public void Audio_the_same_three_keys_place_a_plain_level_too()
        {
            // "Never special-cased to pan, or we have invented a second
            // vocabulary for one idea." Headphone is an ordinary 0-to-100
            // level, so 0 and Home land in the same place — which is what
            // zero means on an unsigned target.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.H | Keys.Control);

            h.Layer.HandleKey(Keys.End);
            Assert.Equal(100, rig.Headphone);
            Assert.Equal("Headphone 100", h.LastMove);

            h.Layer.HandleKey(Keys.Home);
            Assert.Equal(0, rig.Headphone);
            Assert.Equal("Headphone 0", h.LastMove);

            h.Layer.HandleKey(Keys.End);
            h.Layer.HandleKey(Keys.D0);
            Assert.Equal(0, rig.Headphone);
        }

        [Fact]
        public void Audio_a_jump_before_a_letter_hints_and_stays()
        {
            // The same answer an arrow gets: a layer key that names no target
            // asks for one and never ejects the operator.
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.End));
            Assert.Equal("Pick a target first: V, Ctrl+H, O, M, L, C, S, X, D, or P.", h.LastSaid);
            Assert.True(h.Layer.IsLive);
            Assert.Empty(h.Moves);
        }

        [Fact]
        public void Audio_escape_puts_back_everything_a_jump_moved()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.H | Keys.Control);
            h.Layer.HandleKey(Keys.End);
            h.Layer.HandleKey(Keys.M);
            h.Layer.HandleKey(Keys.Home);
            Assert.Equal((100, 0), (rig.Headphone, rig.Mic));

            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal((40, 30), (rig.Headphone, rig.Mic));
        }

        [Fact]
        public void Audio_the_alt_p_door_opens_on_pan()
        {
            var (h, _) = OpenAudio(VerbosityLevel.Chatty, onPan: true);
            Assert.Equal("Audio layer. Pan, slice A, 40. Press H for a list of keys, Escape to revert changes, Enter to accept.",
                Assert.Single(h.Said));
            var (t, _) = OpenAudio(VerbosityLevel.Terse, onPan: true);
            Assert.Equal("Audio layer. Pan, slice A, 40.", Assert.Single(t.Said));
        }

        [Fact]
        public void Audio_escape_puts_back_everything_that_moved_in_order_and_says_so()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.H | Keys.Control); h.Layer.HandleKey(Keys.Up);          // headphone 45
            h.Layer.HandleKey(Keys.O);                h.Layer.HandleKey(Keys.Down);        // pc 11
            h.Layer.HandleKey(Keys.P); h.Layer.HandleKey(Keys.Right);       // pan 45
            h.Layer.HandleKey(Keys.M);                                                     // picked, not moved
            var r = h.Layer.HandleKey(Keys.Escape);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.Equal(40, rig.Headphone);
            Assert.Equal(12, rig.PcVolume);
            Assert.Equal(40, rig.Pan["A"]);
            Assert.Equal(new[] { "headphone 45", "pc 11", "pan A 45", "headphone 40", "pc 12", "pan A 40" }, rig.Writes);
            Assert.Equal("All changes reverted. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_escape_with_nothing_moved_says_so()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.L);
            h.Layer.HandleKey(Keys.Escape);
            Assert.Empty(rig.Writes);
            Assert.Equal("No audio changes made. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_escape_at_chatty_puts_pan_back_as_a_bare_number()
        {
            // #536, ruled 2026-09-06: no word after the figure, at any tier,
            // so Chatty reads exactly as Terse does here. This test kept its
            // Chatty setting on purpose — it is the tier that USED to add
            // "slightly left", and it is where a regression would show first.
            // The restore list is comma-joined, and each item is now a single
            // half, so one comma per item still separates them cleanly.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P);
            h.Layer.HandleKey(Keys.Right);
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal("All changes reverted. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_enter_keeps_everything_and_persists_the_pc_volume_once()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.O); h.Layer.HandleKey(Keys.Up); h.Layer.HandleKey(Keys.Up);
            h.Layer.HandleKey(Keys.Return);
            Assert.Equal(14, rig.PcVolume);
            Assert.Equal(1, rig.Persisted);
        }

        [Fact]
        public void Audio_ctrl_j_hands_off_and_still_persists_the_pc_volume()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.O); h.Layer.HandleKey(Keys.Up);
            Assert.Equal(ValueLayerKeyResult.ClosedHandOff, h.Layer.HandleKey(Keys.J | Keys.Control));
            Assert.Equal(1, rig.Persisted);
        }

        [Fact]
        public void Audio_the_processor_steps_by_name_and_clamps()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.S);
            Assert.Equal("Processor Normal, processor is off", h.LastAnswer);
            h.Layer.HandleKey(Keys.Up); h.Layer.HandleKey(Keys.Up); h.Layer.HandleKey(Keys.Up);
            Assert.Equal(2, rig.Processor);
            Assert.Equal(new[] { "Processor DX", "Processor DX plus", "Processor DX plus" },
                h.Moves.Select(m => m.Text));
        }

        [Fact]
        public void Audio_the_compander_says_when_it_is_off()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            rig.CompanderOn = false;
            h.Layer.HandleKey(Keys.C);
            Assert.Equal("Compander 20, compander is off", h.LastAnswer);
        }

        [Fact]
        public void Audio_shift_letter_jumps_to_that_slice_and_pan_follows()
        {
            // #515: Shift+letter jumps from inside a layer, so the layer never
            // spends A-F on slices. What was done on the old slice is kept.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P);
            h.Layer.HandleKey(Keys.Right);                       // A: 45
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.C | Keys.Shift));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(new[] { "C" }, rig.Jumps);
            Assert.Equal("Pan, slice C, 50", h.LastAnswer);
            h.Layer.HandleKey(Keys.Left);                        // C: 45
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal(45, rig.Pan["A"]);                      // kept — confirmed by leaving
            Assert.Equal(50, rig.Pan["C"]);                      // restored
            Assert.Equal("All changes reverted. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_shift_c_is_a_slice_not_the_compander()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.C | Keys.Shift);
            Assert.Null(h.Layer.CurrentTarget);
            Assert.Equal(new[] { "C" }, rig.Jumps);
        }

        [Fact]
        public void Audio_an_unknown_key_with_a_target_picked_stays_with_the_layers_own_line()
        {
            // #778. X is nothing in this mirror (the shipped layer has it as
            // VOX gain; the mirror predates that) and nothing outside. With
            // mic picked, the pick-a-target list would be the wrong sentence,
            // so the layer answers in its own voice and stays. Read as NVDA
            // speaks it: a sentence that names the layer, so an operator can
            // tell it from Home's "X does nothing on the Frequency field".
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M); h.Layer.HandleKey(Keys.Up);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.X));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(35, rig.Mic);
            Assert.Equal(NotAKeyInAudio, h.LastSaid);
            Assert.DoesNotContain("Audio layer closed", h.Said);

            // Still the layer it was: the arrows still move what was picked.
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(40, rig.Mic);
        }

        [Fact]
        public void Audio_an_unknown_key_below_chatty_is_the_tone_alone_and_stays()
        {
            // #528 governs this refusal like every other: below Chatty the
            // thunk is the whole answer — and the layer still stays, because
            // verbosity changes what is said, never what happens.
            var tones = new ToneCounter();
            var (h, _) = OpenAudio(VerbosityLevel.Terse, cues: tones.Cues);
            h.Layer.HandleKey(Keys.M);
            int saidBefore = h.Said.Count;

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.X));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(1, tones.Invalid);
            Assert.Equal(saidBefore, h.Said.Count);
        }

        // ── A slipped modifier is not an exit (#547) ────────────────────

        [Fact]
        public void Audio_a_bare_b_names_ctrl_b_and_the_layer_is_still_there()
        {
            // The report, verbatim: "I accidentally pressed b and it exited
            // and said nothing." B means nothing outside the layer either, so
            // there was nothing for the key to travel on and do — the operator
            // paid for a typo with the whole mode, and the next keystroke
            // landed somewhere other than where they believed they were.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            h.Layer.HandleKey(Keys.Up);

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.B));
            Assert.True(h.Layer.IsLive);
            Assert.Equal("Ctrl+B: Binaural receive on or off",
                h.LastSaid);

            // Still the layer it was: the arrows still move what was picked.
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(40, rig.Mic);
        }

        [Fact]
        public void Audio_the_named_alternative_is_the_ctrl_form_not_the_slice_jump()
        {
            // Both Ctrl+B (binaural) and Shift+B (jump to slice B) are one
            // modifier from a bare B. The Ctrl form is the one on the same
            // SUBJECT, and it is the one the operator was reaching for; the
            // slice jump is a different axis wearing the same letter (#515).
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.B);
            Assert.Contains("Ctrl+B", h.LastSaid);
            Assert.DoesNotContain("Shift+B", h.LastSaid);
        }

        [Fact]
        public void Audio_a_letter_with_only_a_slice_jump_still_gets_that_answer()
        {
            // F carries no target and no switch, but Shift+F jumps to slice F.
            // Naming it beats a silent exit, and it is a real thing to press.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.F));
            Assert.True(h.Layer.IsLive);
            Assert.Contains("Shift+F", h.LastSaid);
        }

        [Fact]
        public void Audio_a_near_miss_the_other_way_recovers_to_the_bare_letter()
        {
            // The slip runs both ways: the grammar is plain-letter picks
            // against Ctrl toggles, so holding Ctrl by mistake is as likely as
            // dropping it. Ctrl+V is not a chord here; V is slice volume.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.V | Keys.Control));
            Assert.True(h.Layer.IsLive);
            Assert.Equal("V: Slice volume", h.LastSaid);
        }

        [Fact]
        public void Audio_a_real_hotkey_is_refused_and_the_layer_stays()
        {
            // #778b, Noel 2026-10-19 14:35: "keep it so that only [the way out]
            // is enter, esc, and the jj key". Ctrl+F is a registry chord —
            // enter a frequency — and until this it confirmed, said the layer
            // closed (cut off by the frequency prompt sixteen milliseconds
            // later, so the operator could not know) and travelled. Now it is
            // refused like any key the layer does not take, with the layer's
            // plain line: it is a command out there, so no near miss is
            // guessed for it even though Shift+F is a slice jump in here.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            h.Layer.HandleKey(Keys.Up);

            Assert.Equal(ValueLayerKeyResult.Handled,
                h.Layer.HandleKey(Keys.F | Keys.Control));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(35, rig.Mic);
            Assert.Equal(NotAKeyInAudio, h.LastSaid);
            Assert.DoesNotContain("Audio layer closed", h.Said);

            // And the layer is the one it was.
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(40, rig.Mic);
        }

        [Fact]
        public void Audio_z_with_nothing_picked_stays_and_lists_the_letters()
        {
            // #778, Noel's capture of 2026-10-19 07:46:03 — JJ key, A, Z. The
            // layer closed, Z was forwarded, and the frequency field said "Z
            // does nothing on the Frequency field" sixteen milliseconds after
            // "Audio layer closed", cutting it off; from the chair the layer
            // sounded open while the app had closed it. Z is nothing here at
            // any tier and nothing outside, so there was never anywhere for
            // it to go. Now it stays, and with nothing picked the recovery is
            // the list of letters — the sentence Sprint 47's matrix expected
            // and the one this press should always have heard.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Z));
            Assert.True(h.Layer.IsLive);
            Assert.Equal("Pick a target first: V, Ctrl+H, O, M, L, C, S, X, D, or P.", h.LastSaid);
            Assert.DoesNotContain("Audio layer closed", h.Said);

            // His next key, A, got Home's "A does nothing" reply. In the layer
            // it is another unknown letter and gets the same list again.
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.A));
            Assert.True(h.Layer.IsLive);

            // Escape is the exit, and it still is.
            Assert.Equal(ValueLayerKeyResult.Closed, h.Layer.HandleKey(Keys.Escape));
            Assert.False(h.Layer.IsLive);
        }

        // ── A digit never leaves a value layer (#778, half one B) ───────

        [Fact]
        public void Audio_typing_a_value_and_enter_never_reaches_the_radio_as_a_frequency()
        {
            // The capture, 2026-10-19 07:41:58: VOX gain picked, then 4, 0.
            // The layer closed on the 4, both digits went to the frequency
            // field, and quick-type said "Invalid frequency" — only because a
            // no-decimal entry is kilohertz and 40 kHz is under the parser's
            // floor of 100. "4, 0, 0, Enter" meant as a 400 ms VOX delay is
            // 400 kHz, accepted, and a retune. Mic stands in for VOX gain here
            // (the mirror predates that target); the engine does not care
            // which level is picked.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            h.Layer.HandleKey(Keys.Up);                               // 35

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D1));
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D4));
            Assert.True(h.Layer.IsLive);
            // Noel's sentence, ruled 14:33-14:34, then the fine step: mic moves
            // by 5 and Shift moves it by 1, so the second sentence is true.
            Assert.Equal(TypedNumberInAudio + " " + FineStep, h.LastSaid);
            Assert.DoesNotContain("Audio layer closed", h.Said);

            // Enter now means what it always means in here: keep and leave.
            // Nothing about a frequency was ever in play.
            Assert.Equal(ValueLayerKeyResult.Closed, h.Layer.HandleKey(Keys.Return));
            Assert.Equal(35, rig.Mic);
            Assert.Equal("Audio layer closed", h.LastSaid);
        }

        [Theory]
        [InlineData(Keys.Control | Keys.Space)]                  // hold to talk
        [InlineData(Keys.Shift | Keys.Space)]                    // transmit lock, which also unlocks
        [InlineData(Keys.OemOpenBrackets)]                       // the bracket filter keys,
        [InlineData(Keys.OemCloseBrackets)]                      // every form the app binds
        [InlineData(Keys.OemOpenBrackets | Keys.Shift)]
        [InlineData(Keys.OemCloseBrackets | Keys.Shift)]
        [InlineData(Keys.OemOpenBrackets | Keys.Control)]
        [InlineData(Keys.OemCloseBrackets | Keys.Control)]
        [InlineData(Keys.OemOpenBrackets | Keys.Control | Keys.Shift)]   // TX filter low edge, a registry row
        [InlineData(Keys.V | Keys.Control | Keys.Shift)]         // the verbosity cycle
        public void Audio_a_key_that_acts_in_place_travels_on_and_the_layer_stays(Keys chord)
        {
            // #778 ruling 2, as filed (to confirm at the radio): push to talk,
            // the transmit lock and the bracket filter keys keep working inside
            // a layer because they do not leave it. The main window handles
            // them in branches the registry never lists, which is how #778's
            // first round came to swallow push to talk (Codex, B1). The host
            // names them; the engine lets them go UNCONSUMED with the layer
            // still live, so the main window's handler (or the dialog) gets the
            // key and the operator is still where they were.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            h.Layer.HandleKey(Keys.Up);                               // 35

            Assert.Equal(ValueLayerKeyResult.PassThrough, h.Layer.HandleKey(chord));
            Assert.True(h.Layer.IsLive);
            Assert.Equal("mic", h.Layer.CurrentTarget?.Id);
            Assert.DoesNotContain("Audio layer closed", h.Said);

            // Still the layer it was.
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(40, rig.Mic);
        }

        [Theory]
        [InlineData(VerbosityLevel.Chatty)]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Critical)]   // Off
        public void A_key_nothing_here_would_act_on_is_the_low_tone_at_every_level_and_words_only_at_chatty(VerbosityLevel level)
        {
            // #778b, ruled by Noel 2026-10-19 20:55: "I would speak it if on
            // chatty, play a low tone otherwise. If we speak it, make it a very
            // short message, the tone really should be enough, similar to the
            // DMR radios that beep when they can't transmit." The host says a
            // key would act in place elsewhere but nothing here would act on it
            // (push to talk pressed in a dialog). The engine refuses it, keeps
            // the layer, plays the refusal tone at EVERY level, Off included,
            // and says the host's sentence at Chatty only.
            var tones = new ToneCounter();
            var h = new Harness { Verbosity = level };
            int value = 40;
            h.Open(new ValueSubLayerDefinition
            {
                Id = "power",
                Read = () => value, Apply = v => value = v,
                Min = 0, Max = 100, Step = 5, FineStep = 1,
                Number = v => "Power " + v,
                DescribeEntry = (cur, entry) => "Power layer.",
                DescribeClosed = () => "Power layer closed",
                DescribeUnavailableHere = k => k == (Keys.Space | Keys.Control) ? "You can't use push to talk here." : null,
                Cues = tones.Cues,
            });
            int saidBefore = h.Said.Count;

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Space | Keys.Control));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(1, tones.Invalid);
            if (level == VerbosityLevel.Chatty)
                Assert.Equal("You can't use push to talk here.", h.LastSaid);
            else
                Assert.Equal(saidBefore, h.Said.Count);
        }

        [Fact]
        public void The_earcon_gate_is_the_sound_switches_and_not_the_speech_verbosity()
        {
            // Why the low tone above sounds at Off in the real app: every gated
            // earcon, the refusal tone (LeaderInvalidTone) among them, passes
            // only EarconPlayer.On, which is the master switch and the category
            // switch. Nothing in EarconPlayer consults the speech verbosity.
            string source = ReadSource("JJFlexWpf/EarconPlayer.cs");
            Assert.Contains("EarconsEnabled && _categoryEnabled[(int)category];", source);
            Assert.DoesNotContain("CurrentVerbosity", source);
            Assert.DoesNotContain("VerbosityLevel", source);
            Assert.Contains("public static void LeaderInvalidTone()", source);
            string commands = ReadSource("JJFlexWpf/KeyCommands.cs");
            Assert.Equal(3, Regex.Matches(commands, @"Invalid = EarconPlayer\.LeaderInvalidTone,").Count);
        }

        [Fact]
        public void A_layer_that_binds_a_bracket_itself_takes_it_ahead_of_the_in_place_list()
        {
            // Noel, 14:36: brackets move the filter inside a layer unless the
            // layer claims them itself; the planned mode layer uses them for
            // next and previous mode. "A layer's own key always wins." So the
            // host's in-place list is asked only after every key the layer
            // binds. A two-target layer selecting by [ and ] stands in for the
            // mode layer; the host names the brackets as in place, exactly as
            // the shipped hosts do.
            var h = new Harness { Verbosity = VerbosityLevel.Chatty };
            int a = 1, b = 2;
            h.Open(new ValueSubLayerDefinition
            {
                Id = "mode",
                Selection = ValueLayerSelection.ByLetter,
                Targets = new List<ValueTarget>
                {
                    new() { Id = "previous", Name = "Previous", SelectKey = Keys.OemOpenBrackets,
                            Read = () => a, Apply = v => a = v, Min = 0, Max = 9, Step = 1, FineStep = 1,
                            Axes = ValueLayerAxes.UpDown, Number = v => "previous " + v },
                    new() { Id = "next", Name = "Next", SelectKey = Keys.OemCloseBrackets,
                            Read = () => b, Apply = v => b = v, Min = 0, Max = 9, Step = 1, FineStep = 1,
                            Axes = ValueLayerAxes.UpDown, Number = v => "next " + v },
                },
                DescribeClosed = () => "Mode layer closed",
                PassThroughKeys = InPlaceChord,
            });

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.OemCloseBrackets));
            Assert.Equal("next", h.Layer.CurrentTarget?.Id);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.OemOpenBrackets));
            Assert.Equal("previous", h.Layer.CurrentTarget?.Id);

            // The control: a bracket form this layer does NOT bind is still the
            // host's, and travels on with the layer live.
            Assert.Equal(ValueLayerKeyResult.PassThrough, h.Layer.HandleKey(Keys.OemOpenBrackets | Keys.Shift));
            Assert.True(h.Layer.IsLive);
        }

        [Fact]
        public void Audio_a_digit_with_nothing_picked_gets_the_letter_list()
        {
            // No target yet, so "use the arrows" would point at nothing; the
            // list of letters is the recovery, as it is for any other key.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D1));
            Assert.True(h.Layer.IsLive);
            Assert.Equal("Pick a target first: V, Ctrl+H, O, M, L, C, S, X, D, or P.", h.LastSaid);
        }

        [Fact]
        public void Audio_the_keypad_digits_are_refused_like_the_top_row()
        {
            // CharFor in FreqOutHandlers turns NumPad0-9 into the same digit
            // characters, so the keypad starts frequency entry exactly as the
            // top row does. NumPad0 is here too — 0 on the top row is the
            // centre jump, but there is deliberately no numpad binding.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            foreach (var k in new[] { Keys.NumPad5, Keys.NumPad0, Keys.NumPad9 })
            {
                Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(k));
                Assert.True(h.Layer.IsLive);
                Assert.Equal(TypedNumberInAudio + " " + FineStep, h.LastSaid);
            }
            Assert.Equal(30, rig.Mic);
        }

        [Fact]
        public void Audio_a_shifted_digit_is_not_typed_entry_and_gets_the_plain_line()
        {
            // The typed-entry set is the main window's own: Shift+digit
            // produces no character there, so it starts nothing and is not a
            // "typed number". Since #778b it is refused like any other key the
            // layer does not take, with the layer's plain line.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D1 | Keys.Shift));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(NotAKeyInAudio, h.LastSaid);
        }

        [Fact]
        public void Audio_a_digit_on_a_target_with_no_finer_step_does_not_promise_one()
        {
            // The speech processor's three modes move by one and so does Shift:
            // there is no finer step, so "Shift with the arrows moves it in fine
            // steps" would be a sentence the next press contradicts. The ruled
            // sentence alone.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.S);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D2));
            Assert.Equal(TypedNumberInAudio, h.LastSaid);
        }

        // ── Everything else is refused (#778b) ──────────────────────────

        [Theory]
        [InlineData(Keys.Tab)]
        [InlineData(Keys.Tab | Keys.Shift)]
        [InlineData(Keys.Space)]
        [InlineData(Keys.PageUp)]
        [InlineData(Keys.PageDown)]
        [InlineData(Keys.Delete)]
        [InlineData(Keys.Insert)]
        [InlineData(Keys.F5)]                                    // a band jump, a registry row
        [InlineData(Keys.F | Keys.Control)]                      // type a frequency, a registry row
        [InlineData(Keys.D1 | Keys.Control)]                     // a message slot, configured or not
        [InlineData(Keys.Home | Keys.Control)]
        [InlineData(Keys.Z | Keys.Control)]
        [InlineData(Keys.Space | Keys.Control | Keys.Shift)]     // not a transmit chord
        [InlineData(Keys.OemPeriod)]
        [InlineData(Keys.OemMinus)]
        public void Audio_every_other_key_is_refused_and_the_layer_stays(Keys k)
        {
            // #778b. Round two of #778 let every key here travel, because the
            // registry is not the whole key map and the layer could not tell a
            // dead key from one the main window handles itself (Codex, B1).
            // That reasoning still holds, and is now answered differently: the
            // keys the main window handles in place are NAMED by the host (the
            // theory above), and everything else is refused, because Noel ruled
            // the only ways out are Enter, Escape and the JJ key. The
            // non-registry branches in MainWindow_PreviewKeyDown, as read for
            // this change, so the next reader knows they exist:
            //
            //   - Tab returns early so WPF and the child panels get it. ON THE
            //     MAIN WINDOW TAB THEREFORE NEVER REACHES A LAYER, refused or
            //     not; this refusal holds in a dialog, where the Window class
            //     handler routes the layer first. Reported, not changed here.
            //   - The universal Home-key no-radio guard (M, V, R, X, Q, =),
            //     only when RigControl is null; a layer is dropped then.
            //   - Escape stops an in-flight voice message, and cancels
            //     filter-edge mode and RIT/XIT scale-adjust.
            //   - The bracket filter keys, bare, Shift and Ctrl, go to
            //     FreqOutHandlers.HandleFilterHotkey.
            //   - Shift+Space toggles the transmit lock (and UNLOCKS it).
            //   - Ctrl+Space is hold-to-talk; Escape while transmitting unkeys.
            //   - Then step 4, the registry dispatch, which is where the main
            //     window first asks a layer anything, and step 5, the focused
            //     control.
            //
            // MainWindow_PreviewKeyUp takes the Space release for PTT; a key-up
            // never reaches a layer.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            h.Layer.HandleKey(Keys.Up);                               // 35

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(k));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(35, rig.Mic);
            Assert.Equal(NotAKeyInAudio, h.LastSaid);
            Assert.DoesNotContain("Audio layer closed", h.Said);
        }

        [Fact]
        public void Audio_shift_m_is_refused_with_the_plain_line_and_no_near_miss()
        {
            // Shift+M IS a registry chord (MuteAllSlices, Radio scope) and one
            // modifier from M, which picks mic here. Since #778b it no longer
            // leaves; and because it is a command out there it was pressed on
            // purpose, so the layer does not guess "M: Mic level" for it (#547
            // offers a near miss only for a key with no meaning outside). The
            // mirror's stub registry knows Shift+M for exactly this test.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.M | Keys.Shift));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(NotAKeyInAudio, h.LastSaid);
        }

        [Fact]
        public void Audio_an_unbound_letter_stays_bare_shifted_or_with_ctrl()
        {
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.M);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Z));
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Z | Keys.Shift));
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Z | Keys.Control));
            Assert.True(h.Layer.IsLive);
        }

        [Fact]
        public void Enter_escape_and_the_jj_key_are_still_the_exits()
        {
            var (enter, _) = OpenAudio(VerbosityLevel.Chatty);
            enter.Layer.HandleKey(Keys.M);
            Assert.Equal(ValueLayerKeyResult.Closed, enter.Layer.HandleKey(Keys.Return));
            Assert.False(enter.Layer.IsLive);

            var (escape, _) = OpenAudio(VerbosityLevel.Chatty);
            escape.Layer.HandleKey(Keys.M);
            Assert.Equal(ValueLayerKeyResult.Closed, escape.Layer.HandleKey(Keys.Escape));
            Assert.False(escape.Layer.IsLive);

            var (jj, _) = OpenAudio(VerbosityLevel.Chatty);
            jj.Layer.HandleKey(Keys.M);
            Assert.Equal(ValueLayerKeyResult.ClosedHandOff, jj.Layer.HandleKey(Keys.J | Keys.Control));
            Assert.False(jj.Layer.IsLive);
        }

        [Fact]
        public void Single_the_host_whitelist_runs_before_the_digit_refusal()
        {
            // Codex F1: PassThroughKeys is consulted before IsTypedEntryKey, so
            // a digit the operator has remapped onto the verbosity cycle
            // cycles verbosity with the layer live rather than being refused.
            // That command consumes the key, so it cannot start an entry; the
            // guarantee is "a digit never leaves to start typed entry", not
            // "a digit is always refused". Pinned so the precedence is stated
            // rather than discovered.
            var h = new Harness { Verbosity = VerbosityLevel.Terse };
            var rig = new SingleRig { Value = 40 };
            h.Open(new ValueSubLayerDefinition
            {
                Id = "power",
                Read = () => rig.Value,
                Apply = v => { rig.Value = v; rig.Writes.Add(v); },
                Min = 0, Max = 100, Step = 5, FineStep = 1,
                Number = v => "Power " + v,
                DescribeEntry = (cur, entry) => "Power layer.",
                DescribeClosed = () => "Power layer closed",
                PassThroughKeys = k => k == Keys.D5,
            });

            Assert.Equal(ValueLayerKeyResult.PassThrough, h.Layer.HandleKey(Keys.D5));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D6));
            Assert.True(h.Layer.IsLive);
        }

        [Fact]
        public void Audio_zero_on_the_top_row_is_still_the_centre_jump()
        {
            // The one digit the layer already spends (#522) keeps its meaning;
            // the typed-entry rule starts at 1.
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P);
            h.Layer.HandleKey(Keys.Right);                            // 45
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D0));
            Assert.Equal(50, rig.Pan["A"]);
        }

        [Fact]
        public void Single_a_digit_is_consumed_even_when_the_host_says_it_means_something_outside()
        {
            // A bare-digit chord added to the key table one day must not reopen
            // the fall-through. Since #778b a letter the host vouches for is
            // refused too, so the two are the same answer; the digit keeps its
            // own sentence, which the audio tests read.
            var h = new Harness { Verbosity = VerbosityLevel.Terse };
            var rig = new SingleRig { Value = 40 };
            h.Open(new ValueSubLayerDefinition
            {
                Id = "power",
                Read = () => rig.Value,
                Apply = v => { rig.Value = v; rig.Writes.Add(v); },
                Min = 0, Max = 100, Step = 5, FineStep = 1,
                Number = v => "Power " + v,
                DescribeEntry = (cur, entry) => "Power layer.",
                DescribeClosed = () => "Power layer closed",
                MeansSomethingOutside = k => true,
            });

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.D7));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.X));
            Assert.True(h.Layer.IsLive);
            Assert.Empty(rig.Writes);
        }

        [Fact]
        public void The_typed_entry_set_is_the_bare_digits_and_nothing_else()
        {
            foreach (Keys k in new[] { Keys.D1, Keys.D5, Keys.D9, Keys.NumPad0, Keys.NumPad9 })
                Assert.True(ValueSubLayer.IsTypedEntryKey(k), k.ToString());
            foreach (Keys k in new[]
            {
                Keys.D0,                       // the centre jump
                Keys.D1 | Keys.Shift,          // no character on the main window
                Keys.D1 | Keys.Control,        // a message slot chord
                Keys.NumPad5 | Keys.Alt,
                Keys.OemPeriod, Keys.Decimal,  // continue an entry, never start one
                Keys.OemMinus, Keys.Subtract,
                Keys.Z, Keys.Tab, Keys.Space,
            })
                Assert.False(ValueSubLayer.IsTypedEntryKey(k), k.ToString());
        }

        [Fact]
        public void Audio_the_near_miss_line_still_wins_over_the_letter_list()
        {
            // Both are recoveries for a key with nowhere to go; the near miss
            // is the more specific one and keeps first claim (#547). B with
            // nothing picked names Ctrl+B, not the ten letters.
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.B));
            Assert.Equal("Ctrl+B: Binaural receive on or off", h.LastSaid);
        }

        [Fact]
        public void Audio_a_near_miss_below_chatty_is_the_tone_alone()
        {
            // #528, unchanged and not re-decided here: a refusal is a refusal,
            // and the near miss goes through the same door as a wrong-axis
            // arrow. Below Chatty the thunk is the whole answer — and it is the
            // SAME thunk, which is what tells the operator they are still in
            // the layer without a word being said.
            var tones = new ToneCounter();
            var (h, _) = OpenAudio(VerbosityLevel.Terse, cues: tones.Cues);
            int saidBefore = h.Said.Count;

            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.B));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(1, tones.Invalid);
            Assert.Equal(saidBefore, h.Said.Count);
        }

        [Fact]
        public void Audio_a_near_miss_speaks_at_every_level_when_the_tone_cannot_sound()
        {
            // Earcons off: the words are the only feedback there is, so they
            // are spoken at Terse too. A refusal is never silent, which is the
            // whole complaint this task started from.
            //
            // #558: and what they say is the RECOVERY, not the refusal. With
            // the tone silenced this sentence is carrying both jobs, which is
            // exactly when leading with "B is not a key in the Audio layer"
            // was worst - it spent the only feedback available on the half the
            // operator already knew.
            var tones = new ToneCounter { Audible = false };
            var (h, _) = OpenAudio(VerbosityLevel.Terse, cues: tones.Cues);

            h.Layer.HandleKey(Keys.B);
            Assert.True(h.Layer.IsLive);
            Assert.Equal("Ctrl+B: Binaural receive on or off", h.LastSaid);
        }

        // ════════════════════════════════════════════════════════════════
        //  PART THREE — the filter layer (#516): the modifier picks the
        //  edge, the key picks the verb, T and R pick the side
        // ════════════════════════════════════════════════════════════════

        private sealed class FilterRig
        {
            public int RxLow = 100, RxHigh = 2800;
            public int TxLow = 300, TxHigh = 2700;
            public string Mode = "USB";
            public ShiftSide Shift = ShiftSide.None;
            public readonly List<string> Writes = new();
        }

        private sealed class Bank
        {
            public int Low, High, EntryLow, EntryHigh;
            public bool Touched;
            public int Width => High - Low;
            public void Seed(int low, int high) { Low = EntryLow = low; High = EntryHigh = high; Touched = false; }
        }

        /// <summary>
        /// The receive ladder this file mirrors, and — since #527 — the
        /// transmit one too. Radios.Tests cannot load the WPF assembly where
        /// the real <c>FreqOutHandlers.GetAdaptiveFilterStep</c> lives, so
        /// this is a copy, and a copy drifts;
        /// <see cref="The_shipped_step_ladder_is_the_one_this_file_mirrors"/>
        /// reads the shipped rungs out of source and refuses to let it.
        /// </summary>
        private static int AdaptiveStep(int low, int high)
        {
            int width = high - low;
            if (width < 200) return 10;
            if (width < 500) return 25;
            if (width < 3500) return 50;
            if (width < 5000) return 100;
            return 200;
        }

        private static (int lowMin, int highMax) BoundsFor(string mode) => mode switch
        {
            "LSB" or "DIGL" => (-12000, 0),
            "USB" or "DIGU" or "FDV" => (0, 12000),
            _ => (-12000, 12000),
        };

        private static int ClampSafe(int v, int min, int max) => max < min ? min : Math.Clamp(v, min, max);

        private static string Report(string key, int low, int high)
            => Lexicon.Get(key, ("low", low), ("high", high), ("widthKHz", ((high - low) / 1000.0).ToString("F1")));

        private static string AtLimit(string what) => Lexicon.Get("audio.filter_layer.at_limit", ("what", what));

        private sealed class Side
        {
            public string Group = ""; public string Prefix = ""; public bool PerSlice;
            public Bank Bank = new();
            public Func<int> LowMin = () => 0; public Func<int> HighMax = () => 0;
            public int MinWidth = 50; public Func<int> StepNow = () => 50;
            public Action<int, int> Apply = (l, h) => { };
            public Func<int, string> LowEdge = v => ""; public Func<int, string> HighEdge = v => "";
            public Func<string> Range = () => ""; public Func<string> Width = () => ""; public Func<string> Rep = () => "";
        }

        private static (int low, int high) EdgesForWidth(Side s, int width)
        {
            int centre = (s.Bank.Low + s.Bank.High) / 2;
            int half = width / 2;
            int low = centre - half, high = centre + (width - half);
            int lowMin = s.LowMin(), highMax = s.HighMax();
            // A width that will not fit about the centre SLIDES into the
            // bounds before it is truncated — see the shipped EdgesForWidth.
            if (low < lowMin) { high += lowMin - low; low = lowMin; }
            if (high > highMax) { low -= high - highMax; high = highMax; }
            if (low < lowMin) low = lowMin;
            if (high - low < s.MinWidth) { high = low + s.MinWidth; if (high > highMax) { high = highMax; low = high - s.MinWidth; } }
            return (low, high);
        }

        private static IEnumerable<ValueTarget> TargetsFor(Side s)
        {
            const int outer = 24000;
            yield return new ValueTarget
            {
                Id = s.Prefix + "-low", Group = s.Group, PerSlice = s.PerSlice, Linked = true,
                Axes = ValueLayerAxes.LeftRight, Shift = ShiftSide.Left, Min = -outer, Max = outer,
                StepNow = s.StepNow, Read = () => s.Bank.Low,
                Constrain = v => ClampSafe(v, s.LowMin(), s.Bank.High - s.MinWidth),
                Apply = v => s.Apply(v, s.Bank.High),
                Number = s.LowEdge, DescribeSelected = s.LowEdge, DescribeRail = v => AtLimit(s.LowEdge(v)),
            };
            yield return new ValueTarget
            {
                Id = s.Prefix + "-high", Group = s.Group, PerSlice = s.PerSlice, Linked = true,
                Axes = ValueLayerAxes.LeftRight, Shift = ShiftSide.Right, Min = -outer, Max = outer,
                StepNow = s.StepNow, Read = () => s.Bank.High,
                Constrain = v => ClampSafe(v, s.Bank.Low + s.MinWidth, s.HighMax()),
                Apply = v => s.Apply(s.Bank.Low, v),
                Number = s.HighEdge, DescribeSelected = s.HighEdge, DescribeRail = v => AtLimit(s.HighEdge(v)),
            };
            yield return new ValueTarget
            {
                Id = s.Prefix + "-filter", Group = s.Group, PerSlice = s.PerSlice, Linked = true,
                Axes = ValueLayerAxes.Both, Shift = ShiftSide.None, Min = -outer, Max = outer,
                StepNow = s.StepNow, Read = () => s.Bank.Low,
                Constrain = v => ClampSafe(v, s.LowMin(), s.HighMax() - s.Bank.Width),
                Apply = v => s.Apply(v, v + s.Bank.Width),
                Number = _ => s.Range(), DescribeSelected = _ => s.Rep(), DescribeRail = _ => AtLimit(s.Range()),
            };
            yield return new ValueTarget
            {
                Id = s.Prefix + "-width", Group = s.Group, PerSlice = s.PerSlice, Linked = true,
                Axes = ValueLayerAxes.UpDown, Ctrl = true, Min = s.MinWidth, Max = outer,
                StepNow = () => 2 * s.StepNow(), Read = () => s.Bank.Width,
                Constrain = v => { var (l, h) = EdgesForWidth(s, v); return h - l; },
                Apply = v => { var (l, h) = EdgesForWidth(s, v); s.Apply(l, h); },
                Number = _ => s.Width(), DescribeSelected = _ => s.Width(), DescribeRail = _ => AtLimit(s.Width()),
            };
        }

        private static (Harness h, FilterRig rig) OpenFilter(VerbosityLevel verbosity, int rxLow = 100, int rxHigh = 2800)
        {
            var h = new Harness { Verbosity = verbosity };
            var rig = new FilterRig { RxLow = rxLow, RxHigh = rxHigh };

            var rx = new Side
            {
                Group = "receive", Prefix = "rx", PerSlice = true,
                LowMin = () => BoundsFor(rig.Mode).lowMin, HighMax = () => BoundsFor(rig.Mode).highMax, MinWidth = 50,
            };
            rx.StepNow = () => AdaptiveStep(rx.Bank.Low, rx.Bank.High);
            rx.Apply = (l, hi) => { rx.Bank.Low = l; rx.Bank.High = hi; rx.Bank.Touched = true; rig.RxLow = l; rig.RxHigh = hi; rig.Writes.Add($"rx {l} {hi}"); };
            rx.LowEdge = v => Lexicon.Get("audio.filter.low_edge", ("low", v));
            rx.HighEdge = v => Lexicon.Get("audio.filter.high_edge", ("high", v));
            rx.Range = () => Lexicon.Get("audio.filter.range", ("low", rx.Bank.Low), ("high", rx.Bank.High));
            rx.Width = () => Lexicon.Get("audio.filter_layer.width", ("width", rx.Bank.Width), ("low", rx.Bank.Low), ("high", rx.Bank.High));
            rx.Rep = () => Report("audio.filter.rx_report", rx.Bank.Low, rx.Bank.High);

            var tx = new Side
            {
                Group = "transmit", Prefix = "tx", PerSlice = false,
                LowMin = () => 0, HighMax = () => 10000, MinWidth = 50,
            };
            // #527: one step rule for both sides. The transmit edges used to
            // walk by rig.TXFilterLowIncrement, a flat 50, so pressing T
            // silently changed what an arrow was worth inside one layer.
            tx.StepNow = () => AdaptiveStep(tx.Bank.Low, tx.Bank.High);
            tx.Apply = (l, hi) =>
            {
                // The shipped host writes the edge that opens the gap first;
                // the fake records the pair.
                tx.Bank.Low = l; tx.Bank.High = hi; tx.Bank.Touched = true;
                rig.TxLow = l; rig.TxHigh = hi; rig.Writes.Add($"tx {l} {hi}");
            };
            tx.LowEdge = v => Lexicon.Get("audio.tx.filter_low", ("value", v));
            tx.HighEdge = v => Lexicon.Get("audio.tx.filter_high", ("value", v));
            tx.Range = () => Lexicon.Get("audio.tx_filter.range", ("low", tx.Bank.Low), ("high", tx.Bank.High));
            tx.Width = () => Lexicon.Get("audio.filter_layer.tx_width", ("width", tx.Bank.Width), ("low", tx.Bank.Low), ("high", tx.Bank.High));
            tx.Rep = () => Report("audio.filter.tx_report", tx.Bank.Low, tx.Bank.High);

            var def = new ValueSubLayerDefinition
            {
                Id = "filter",
                Selection = ValueLayerSelection.ByModifier,
                Targets = TargetsFor(rx).Concat(TargetsFor(tx)).ToList(),
                GroupKeys = new Dictionary<Keys, string> { [Keys.R] = "receive", [Keys.T] = "transmit" },
                InitialGroup = "receive",
                DescribeGroup = g => g == "transmit" ? tx.Rep() : rx.Rep(),
                SpeakKey = Keys.S,
                ShiftSideNow = () => rig.Shift,
                Snapshot = () =>
                {
                    rx.Bank.Seed(rig.RxLow, rig.RxHigh);
                    tx.Bank.Seed(rig.TxLow, rig.TxHigh);
                    return () =>
                    {
                        if (rx.Bank.Touched) { rig.RxLow = rx.Bank.EntryLow; rig.RxHigh = rx.Bank.EntryHigh; rig.Writes.Add($"rx {rig.RxLow} {rig.RxHigh}"); }
                        if (tx.Bank.Touched) { rig.TxLow = tx.Bank.EntryLow; rig.TxHigh = tx.Bank.EntryHigh; rig.Writes.Add($"tx {rig.TxLow} {rig.TxHigh}"); }
                    };
                },
                DescribeLayerEntry = layer => Lexicon.Get("audio.filter_layer.entered", h.Verbosity, ("filter", rx.Rep())),
                DescribeLayerHelp = layer => "HELP: " + Lexicon.Get("audio.filter_layer.name") + ", "
                    + (layer.CurrentGroup == "transmit" ? tx.Rep() : rx.Rep()),
                DescribeClosed = () => Lexicon.Get("audio.filter_layer.closed"),
                DescribeLayerRestored = (layer, restored) => Lexicon.Get(
                    (rx.Bank.Touched || tx.Bank.Touched)
                        ? "audio.filter_layer.restored"
                        : "audio.filter_layer.restored_nothing"),
                WhichShiftHint = () => Lexicon.Get("audio.filter_layer.which_shift"),
                NoVerbHint = () => Lexicon.Get("audio.filter_layer.no_verb"),
                WrongAxisHint = () => Lexicon.Get("audio.filter_layer.no_verb"),
                // #547, the shipped shape. This layer's letters are S, T and R
                // plus the slice-jump range, so a slipped Ctrl recovers rather
                // than ending the mode without a word.
                MeansSomethingOutside = k => false,
                DescribeNearMiss = k => FilterLayerNearMiss(k, h.Verbosity),
                DescribeUnknownKey = (k, why) => LayerUnknownKey("audio.filter_layer.name", k, why),
                DescribeFineStep = () => Lexicon.Get("audio.value_layer.fine_step"),
            };
            h.Open(def);
            return (h, rig);
        }

        private static ValueLayerKeyResult Press(Harness h, FilterRig rig, Keys k, ShiftSide side)
        {
            rig.Shift = side;
            var r = h.Layer.HandleKey(k);
            rig.Shift = ShiftSide.None;
            return r;
        }

        [Fact]
        public void Filter_entry_lands_on_receive_and_points_at_H_at_chatty()
        {
            // Noel ruled on 2026-09-02 that chatty need not recite the keys:
            // it points at H, the surface that lists them properly. This
            // entry used to carry 260 characters of grammar lesson on every
            // press, which is the recitation #524 was raised about wearing a
            // different hat.
            var (h, _) = OpenFilter(VerbosityLevel.Chatty);
            Assert.Equal(
                "Filter layer. RX filter 100 to 2800, 2.7 kilohertz. Press H for a list of keys, Escape to revert changes, Enter to accept.",
                Assert.Single(h.Said));
            Assert.Equal("receive", h.Layer.CurrentGroup);
            Assert.Null(h.Layer.CurrentTarget);
        }

        [Fact]
        public void Filter_an_unknown_key_stays_in_the_filter_layers_own_voice()
        {
            // #778 in a ByModifier layer: nothing is ever picked, there is no
            // pick-a-target list, and Z is no near miss for S, T or R — so the
            // layer's own line is the recovery, and the layer stays.
            var (h, rig) = OpenFilter(VerbosityLevel.Chatty);
            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.Z, ShiftSide.None));
            Assert.True(h.Layer.IsLive);
            Assert.Equal("Not a key in the Filter layer. Press H to list available keys for this layer.", h.LastSaid);
            Assert.DoesNotContain(Lexicon.Get("audio.filter_layer.closed"), h.Said);
        }

        [Fact]
        public void Filter_a_digit_never_promises_a_fine_step()
        {
            // In the filter layer Shift chooses an edge (Left Shift the low,
            // Right Shift the high), so "Shift with the arrows moves it in fine
            // steps" would be false here. The engine says the ruled digit
            // sentence alone, however the host wires the fine step.
            var (h, rig) = OpenFilter(VerbosityLevel.Chatty);
            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.D4, ShiftSide.None));
            Assert.True(h.Layer.IsLive);
            Assert.Equal("A typed number does nothing in the Filter layer. Use arrow keys to change the selected number.",
                h.LastSaid);
            Assert.DoesNotContain(Lexicon.Get("audio.filter_layer.closed"), h.Said);
        }

        [Fact]
        public void Filter_entry_at_terse_is_the_report()
        {
            var (h, _) = OpenFilter(VerbosityLevel.Terse);
            Assert.Equal("Filter layer. RX filter 100 to 2800, 2.7 kilohertz.", Assert.Single(h.Said));
        }

        [Fact]
        public void Filter_left_shift_with_left_walks_the_low_edge_down_by_the_bracket_step()
        {
            // The transcript the brief asked for: enter, move one target, leave.
            // 100 to 2800 is 2.7 kHz wide, which since #526 is a 50 Hz rung —
            // it was 100 Hz, and every ordinary SSB filter was in that rung.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.Left | Keys.Shift, ShiftSide.Left));
            Assert.Equal(ValueLayerKeyResult.Closed, h.Layer.HandleKey(Keys.Return));
            Assert.Equal(new[]
            {
                "Filter layer. RX filter 100 to 2800, 2.7 kilohertz.",
                "Low edge 50",
                "Filter layer closed",
            }, h.Transcript);
            Assert.Equal((50, 2800), (rig.RxLow, rig.RxHigh));
            Assert.Equal("value-layer:filter:rx-low", h.Moves[0].Key);
        }

        [Fact]
        public void Filter_right_shift_with_right_walks_the_high_edge_up()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Right);
            Assert.Equal((100, 2850), (rig.RxLow, rig.RxHigh));
            Assert.Equal("High edge 2850", h.LastMove);
        }

        [Fact]
        public void Filter_the_low_edge_cannot_pass_the_high_edge_and_says_so_once()
        {
            // A 100 Hz filter: the step is 10 Hz, the floor width is 50 Hz,
            // so five presses reach the rail and the sixth is refused out
            // loud. Nothing is written at the rail.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse, rxLow: 2700, rxHigh: 2800);
            for (int i = 0; i < 6; i++) Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Left);
            Assert.Equal((2750, 2800), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Low edge 2750, at the limit", h.LastMove);
            Assert.Equal(5, rig.Writes.Count);
            Assert.Equal("Low edge 2750", h.Moves[4].Text);
        }

        [Fact]
        public void Filter_up_slides_the_whole_filter_with_its_width_intact()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal((150, 2850), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Filter 150 to 2850", h.LastMove);
            h.Layer.HandleKey(Keys.Left);
            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));
        }

        [Fact]
        public void Filter_ctrl_up_widens_about_the_centre_and_ctrl_down_narrows()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up | Keys.Control);
            Assert.Equal((50, 2850), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Width 2800, 50 to 2850", h.LastMove);
            h.Layer.HandleKey(Keys.Down | Keys.Control);
            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));
        }

        [Fact]
        public void Filter_the_whole_filter_stops_at_the_mode_bound_and_says_so()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Down);   // 50 → allowed
            Assert.Equal((50, 2750), (rig.RxLow, rig.RxHigh));
            h.Layer.HandleKey(Keys.Down);   // 0 → allowed, and exactly the USB floor
            Assert.Equal((0, 2700), (rig.RxLow, rig.RxHigh));
            h.Layer.HandleKey(Keys.Down);   // low would be -50 → USB floor refuses
            Assert.Equal((0, 2700), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Filter 0 to 2700, at the limit", h.LastMove);
        }

        [Fact]
        public void Filter_s_speaks_the_addressed_target_as_a_question()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Press(h, rig, Keys.S, ShiftSide.None);
            Assert.Equal("RX filter 100 to 2800, 2.7 kilohertz", h.LastAnswer);
            Press(h, rig, Keys.S | Keys.Shift, ShiftSide.Left);
            Assert.Equal("Low edge 100", h.LastAnswer);
            Press(h, rig, Keys.S | Keys.Shift, ShiftSide.Right);
            Assert.Equal("High edge 2800", h.LastAnswer);
            Assert.Equal(3, h.Answers.Count);
            Assert.Empty(rig.Writes);
            Assert.True(h.Layer.IsLive);
        }

        [Theory]
        [InlineData(ShiftSide.None)]
        [InlineData(ShiftSide.Both)]
        public void Filter_a_shift_whose_side_cannot_be_read_is_refused_not_guessed(ShiftSide side)
        {
            // The JAWS held-key divergence: the Shift bit arrived but the probe
            // cannot name a side. Moving the wrong edge silently is the failure
            // this refusal exists to make audible.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.Left | Keys.Shift, side));
            Assert.Empty(rig.Writes);
            Assert.Equal("Hold Left Shift for the low edge, or Right Shift for the high edge.", h.LastSaid);
            Assert.True(h.Layer.IsLive);
        }

        [Fact]
        public void Filter_a_modifier_that_names_no_target_hints_and_stays()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.Up | Keys.Shift, ShiftSide.Left));
            Assert.Empty(rig.Writes);
            Assert.Equal(
                "Up and down slide the whole filter, Control up and down set the width. Left and right with a Shift walk an edge.",
                h.LastSaid);
        }

        [Fact]
        public void Filter_t_visits_the_transmit_side_whose_rails_are_the_radios()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.T);
            Assert.Equal("transmit", h.Layer.CurrentGroup);
            Assert.Equal("TX filter 300 to 2700, 2.4 kilohertz", h.LastSaid);

            Press(h, rig, Keys.Left | Keys.Shift, ShiftSide.Left);
            Assert.Equal((250, 2700), (rig.TxLow, rig.TxHigh));
            Assert.Equal("TX low 250", h.LastMove);
            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));   // receive untouched

            // Narrow until the low edge meets TXFilterLowMax = high - 50.
            // Since #527 the transmit edges walk by the same width-adaptive
            // ladder the receive edges do, so the step TIGHTENS as the filter
            // closes — 50, then 25 under 500 of width, then 10 under 200 —
            // and the rail takes more presses to reach than a flat 50 would.
            for (int i = 0; i < 80; i++) Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Left);
            Assert.Equal((2650, 2700), (rig.TxLow, rig.TxHigh));
            Assert.Equal("TX low 2650, at the limit", h.LastMove);

            h.Layer.HandleKey(Keys.R);
            Assert.Equal("receive", h.Layer.CurrentGroup);
            Assert.Equal("RX filter 100 to 2800, 2.7 kilohertz", h.LastSaid);
        }

        [Fact]
        public void Filter_escape_puts_back_both_sides_through_one_snapshot()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Press(h, rig, Keys.Left | Keys.Shift, ShiftSide.Left);   // rx 50..2800
            h.Layer.HandleKey(Keys.Up);                                // rx 100..2850
            h.Layer.HandleKey(Keys.T);
            Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Right);  // tx 300..2750
            var r = h.Layer.HandleKey(Keys.Escape);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));
            Assert.Equal((300, 2700), (rig.TxLow, rig.TxHigh));
            Assert.Equal("rx 100 2800", rig.Writes[^2]);
            Assert.Equal("tx 300 2700", rig.Writes[^1]);
            Assert.Equal("All changes reverted. Filter layer closed", h.LastSaid);
        }

        [Fact]
        public void Filter_escape_with_nothing_moved_writes_nothing()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.T);
            h.Layer.HandleKey(Keys.Escape);
            Assert.Empty(rig.Writes);
            Assert.Equal("No filter changes made. Filter layer closed", h.LastSaid);
        }

        [Fact]
        public void Filter_enter_keeps_and_writes_nothing_more()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            h.Layer.HandleKey(Keys.Return);
            Assert.Equal(new[] { "rx 150 2850" }, rig.Writes);
            Assert.Equal("Filter layer closed", h.LastSaid);
        }

        [Theory]
        [InlineData(Keys.H)]
        [InlineData(Keys.Oem2 | Keys.Shift)]
        public void Filter_help_names_the_side_you_are_on(Keys k)
        {
            var (h, _) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.T);
            h.Layer.HandleKey(k);
            Assert.Equal("HELP: Filter layer, TX filter 300 to 2700, 2.4 kilohertz", h.LastSaid);
        }

        [Fact]
        public void Filter_a_shift_and_home_slams_the_addressed_edge_to_its_rail()
        {
            // #522's "the same mapping on EVERY target" reaches the layer
            // where nothing is ever selected: the modifier still names the
            // target, and the key is still the verb. Left Shift+Home is the
            // low edge at its floor, Right Shift+End the high edge at its
            // ceiling — and both stop where WALKING them would stop, because
            // a jump runs the same constraint a nudge does.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);

            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.Home | Keys.Shift, ShiftSide.Left));
            Assert.Equal((0, 2800), (rig.RxLow, rig.RxHigh));    // USB floor
            Assert.Equal("Low edge 0", h.LastMove);

            Press(h, rig, Keys.End | Keys.Shift, ShiftSide.Right);
            Assert.Equal((0, 12000), (rig.RxLow, rig.RxHigh));   // USB ceiling
            Assert.Equal("High edge 12000", h.LastMove);
        }

        [Fact]
        public void Filter_home_with_no_shift_slams_the_whole_filter_left_and_keeps_its_width()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Home);
            Assert.Equal((0, 2700), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Filter 0 to 2700", h.LastMove);

            h.Layer.HandleKey(Keys.End);
            Assert.Equal((9300, 12000), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Filter 9300 to 12000", h.LastMove);
        }

        [Fact]
        public void Filter_ctrl_home_and_ctrl_end_are_the_narrowest_and_widest()
        {
            // Ctrl addresses the width, exactly as it does for the arrows.
            // Narrowest is the floor width about the current centre; widest
            // is the whole span the mode allows, in ONE press — which is
            // what made the truncation in EdgesForWidth worth fixing.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Home | Keys.Control);
            Assert.Equal((1425, 1475), (rig.RxLow, rig.RxHigh));
            h.Layer.HandleKey(Keys.End | Keys.Control);
            Assert.Equal((0, 12000), (rig.RxLow, rig.RxHigh));
        }

        [Fact]
        public void Filter_widening_against_a_band_edge_grows_on_the_side_with_room()
        {
            // The same fix, seen from the arrows: a USB filter sitting on the
            // 0 Hz floor used to gain only half of each widen, because the
            // low edge stopped at the bound and the high edge kept its half.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse, rxLow: 0, rxHigh: 2700);
            h.Layer.HandleKey(Keys.Up | Keys.Control);   // width 2700 + 2 × 50
            Assert.Equal((0, 2800), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Width 2800, 0 to 2800", h.LastMove);
        }

        [Fact]
        public void Filter_zero_sends_what_you_are_holding_to_zero_hertz()
        {
            // "0 is the centre, and on a target with no centre, zero." A
            // filter edge declares no centre, so 0 is zero hertz — the
            // carrier. On USB that is also the floor, so the low edge lands
            // where Home would put it; the high edge cannot pass the low one
            // and stops at the minimum width, out loud, like any other rail.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse, rxLow: 300, rxHigh: 2700);

            Press(h, rig, Keys.D0 | Keys.Shift, ShiftSide.Left);
            Assert.Equal((0, 2700), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Low edge 0", h.LastMove);

            Press(h, rig, Keys.D0 | Keys.Shift, ShiftSide.Right);
            Assert.Equal((0, 50), (rig.RxLow, rig.RxHigh));
            Assert.Equal("High edge 50", h.LastMove);

            // And Escape is still the whole way back, from anywhere.
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal((300, 2700), (rig.RxLow, rig.RxHigh));
        }

        [Fact]
        public void Filter_a_jump_escapes_back_through_the_one_snapshot()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Press(h, rig, Keys.Home | Keys.Shift, ShiftSide.Left);
            h.Layer.HandleKey(Keys.T);
            Press(h, rig, Keys.End | Keys.Shift, ShiftSide.Right);
            h.Layer.HandleKey(Keys.Escape);

            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));
            Assert.Equal((300, 2700), (rig.TxLow, rig.TxHigh));
        }

        [Fact]
        public void Filter_a_jump_whose_shift_side_cannot_be_read_is_refused_not_guessed()
        {
            // The same refusal the arrows make, for the same reason: slamming
            // the WRONG edge to a rail without saying so would be worse than
            // walking it there.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.Home | Keys.Shift, ShiftSide.Both));
            Assert.Empty(rig.Writes);
            Assert.Equal("Hold Left Shift for the low edge, or Right Shift for the high edge.", h.LastSaid);
            Assert.True(h.Layer.IsLive);
        }

        [Fact]
        public void Filter_an_ordinary_ssb_filter_steps_by_fifty_not_a_hundred()
        {
            // #526, stated as the operator hears it. A 100–2,800 passband is
            // 2.7 kHz, and until the 50 Hz rung moved from 2,000 to 3,500 it
            // fell into the 100 Hz rung — so the ladder's most-used step was
            // set for a width almost nobody runs.
            Assert.Equal(50, AdaptiveStep(100, 2800));
            Assert.Equal(50, AdaptiveStep(300, 2700));    // a narrower SSB filter
            Assert.Equal(50, AdaptiveStep(200, 3600));    // 3.4 kHz, still 50

            // CW and AM are untouched, which is the point of moving one rung
            // rather than flattening the ladder.
            Assert.Equal(10, AdaptiveStep(2700, 2800));   // 100 Hz CW
            Assert.Equal(25, AdaptiveStep(2600, 3000));   // 400 Hz CW
            Assert.Equal(100, AdaptiveStep(0, 4000));     // 4 kHz
            Assert.Equal(200, AdaptiveStep(-6000, 6000)); // 12 kHz AM
        }

        [Fact]
        public void Filter_transmit_and_receive_edges_step_by_the_same_rule()
        {
            // #527. Pressing T used to change what an arrow was worth, inside
            // a single layer that presents both sides as the same thing: the
            // receive side walked the adaptive ladder and the transmit side a
            // flat TXFilterLowIncrement. Same width, same step, both sides.
            var (h, rig) = OpenFilter(VerbosityLevel.Terse, rxLow: 300, rxHigh: 2700);

            Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Right);
            int receiveStep = rig.RxHigh - 2700;

            h.Layer.HandleKey(Keys.T);
            Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Right);
            int transmitStep = rig.TxHigh - 2700;

            Assert.Equal(50, receiveStep);
            Assert.Equal(receiveStep, transmitStep);
        }

        [Fact]
        public void The_shipped_step_ladder_is_the_one_this_file_mirrors()
        {
            // Radios.Tests cannot load the WPF assembly, so AdaptiveStep above
            // is a COPY of FreqOutHandlers.GetAdaptiveFilterStep — and a copy
            // that nothing compares is a copy that drifts, which is this
            // project's dominant defect class. Read the shipped rungs out of
            // source and hold the mirror to them.
            string source = ReadSource("JJFlexWpf/FreqOutHandlers.cs");
            int start = source.IndexOf("int GetAdaptiveFilterStep(", StringComparison.Ordinal);
            Assert.True(start > 0, "GetAdaptiveFilterStep not found in FreqOutHandlers.cs");
            int end = source.IndexOf('}', source.IndexOf('{', start));
            string body = source.Substring(start, end - start);

            var rungs = Regex.Matches(body, @"if \(width < (\d+)\) return (\d+);")
                             .Select(m => (Width: int.Parse(m.Groups[1].Value), Step: int.Parse(m.Groups[2].Value)))
                             .ToList();
            var last = Regex.Match(body, @"\n\s*return (\d+);\s*$");

            // Prove the reader before trusting it: a scanner that sees nothing
            // reports perfect agreement.
            Assert.Equal(4, rungs.Count);
            Assert.True(last.Success, "the ladder's final rung was not read out of source");

            foreach (var (width, step) in rungs)
            {
                Assert.Equal(step, AdaptiveStep(0, width - 1));
                Assert.NotEqual(step, AdaptiveStep(0, width));   // the rung really ends there
            }
            Assert.Equal(int.Parse(last.Groups[1].Value), AdaptiveStep(0, rungs[^1].Width));

            // And the boundary #526 moved, named outright, so a revert reads
            // as a decision rather than an accident.
            Assert.Contains((3500, 50), rungs);
        }

        [Fact]
        public void Filter_a_linked_layer_must_supply_a_snapshot()
        {
            var def = new ValueSubLayerDefinition
            {
                Id = "x",
                Selection = ValueLayerSelection.ByModifier,
                ShiftSideNow = () => ShiftSide.None,
                DescribeClosed = () => "closed",
                Targets = { new ValueTarget { Id = "a", Linked = true, Read = () => 0, Apply = _ => { }, Number = v => "" } },
            };
            var ex = Assert.Throws<ArgumentException>(() => ValueSubLayer.EnterForTest(def, (_, _) => { }, (_, _) => { }, _ => { }, () => VerbosityLevel.Terse));
            Assert.Contains("Snapshot", ex.Message);
        }

        // ════════════════════════════════════════════════════════════════
        //  PART FOUR — the two-axis form (#515): Left/Right choose the
        //  target, Up/Down adjust — the equalisers' grammar
        // ════════════════════════════════════════════════════════════════

        private sealed class EqRig
        {
            public readonly int[] Bands = { 0, 3, -2 };
            public readonly List<string> Writes = new();
        }

        private static (Harness h, EqRig rig) OpenEq()
        {
            var h = new Harness { Verbosity = VerbosityLevel.Terse };
            var rig = new EqRig();
            string[] names = { "63 hertz", "125 hertz", "250 hertz" };
            var targets = new List<ValueTarget>();
            for (int i = 0; i < names.Length; i++)
            {
                int band = i;
                targets.Add(new ValueTarget
                {
                    Id = "band" + band, Name = names[band],
                    Read = () => rig.Bands[band],
                    Apply = v => { rig.Bands[band] = v; rig.Writes.Add(names[band] + " " + v); },
                    Min = -10, Max = 10, Step = 1, FineStep = 1, Axes = ValueLayerAxes.UpDown,
                    Number = v => names[band] + " " + v + " dB",
                });
            }
            h.Open(new ValueSubLayerDefinition
            {
                Id = "rx-eq",
                Selection = ValueLayerSelection.ByLeftRight,
                Targets = targets,
                DescribeLayerEntry = layer => "Receive equalizer. " + layer.DescribeTarget(layer.CurrentTarget!) + ".",
                DescribeClosed = () => "Receive equalizer closed",
                DescribeLayerRestored = (layer, restored) => "Put back " + string.Join(", ",
                    restored.Select(r => layer.FormOf(r.Target, r.RestoredTo))) + ". Receive equalizer closed",
                WrongAxisHint = () => "Left and right pick a band, up and down adjust it",
            });
            return (h, rig);
        }

        [Fact]
        public void TwoAxis_entry_selects_the_first_target()
        {
            var (h, _) = OpenEq();
            Assert.Equal("Receive equalizer. 63 hertz 0 dB.", Assert.Single(h.Said));
            Assert.Equal("band0", h.Layer.CurrentTarget!.Id);
        }

        [Fact]
        public void TwoAxis_right_steps_to_the_next_target_and_announces_it()
        {
            var (h, rig) = OpenEq();
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Right));
            Assert.Equal("125 hertz 3 dB", h.LastAnswer);
            Assert.Equal("value-layer:rx-eq:band1", h.Answers[0].Key);
            Assert.Empty(rig.Writes);
        }

        [Fact]
        public void TwoAxis_left_at_the_first_target_re_announces_it()
        {
            // The end of the row: stated once, then the coalescer drops it.
            var (h, _) = OpenEq();
            h.Layer.HandleKey(Keys.Left);
            Assert.Equal("63 hertz 0 dB", h.LastAnswer);
            Assert.Equal("band0", h.Layer.CurrentTarget!.Id);
        }

        [Fact]
        public void TwoAxis_up_adjusts_the_chosen_target()
        {
            var (h, rig) = OpenEq();
            h.Layer.HandleKey(Keys.Right);
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal(4, rig.Bands[1]);
            Assert.Equal("125 hertz 4 dB", h.LastMove);
            Assert.Equal("value-layer:rx-eq:band1", h.Moves[0].Key);
        }

        [Fact]
        public void TwoAxis_a_modified_left_or_right_hints_rather_than_moving_the_selection()
        {
            var (h, _) = OpenEq();
            h.Layer.HandleKey(Keys.Right | Keys.Shift);
            Assert.Equal("band0", h.Layer.CurrentTarget!.Id);
            Assert.Equal("Left and right pick a band, up and down adjust it", h.LastSaid);
        }

        [Fact]
        public void TwoAxis_escape_puts_back_every_band_moved_in_order()
        {
            var (h, rig) = OpenEq();
            h.Layer.HandleKey(Keys.Up);                               // band0: 1
            h.Layer.HandleKey(Keys.Right); h.Layer.HandleKey(Keys.Right);
            h.Layer.HandleKey(Keys.Down);                             // band2: -3
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal(new[] { 0, 3, -2 }, rig.Bands);
            Assert.Equal(new[] { "63 hertz 1", "250 hertz -3", "63 hertz 0", "250 hertz -2" }, rig.Writes);
            Assert.Equal("Put back 63 hertz 0 dB, 250 hertz -2 dB. Receive equalizer closed", h.LastSaid);
        }

        [Fact]
        public void TwoAxis_the_knob_surface_selects_and_nudges_without_keys()
        {
            // #200: a hardware host drives the semantic surface directly.
            var (h, rig) = OpenEq();
            h.Layer.SelectNext(+1);
            h.Layer.Nudge(-1, fine: false);
            Assert.Equal(2, rig.Bands[1]);
            Assert.Equal("125 hertz 2 dB", h.LastMove);
        }

        // ────────────────────────────────────────────────────────────────
        //  Pan says the number and nothing else
        // ────────────────────────────────────────────────────────────────

        // #536, ruled by Noel 2026-09-06. The nine-band words scale
        // (PanPhrase) is DELETED, not merely unused: pan is 0-100 like slice
        // volume, neither announces a unit, and a word after the figure was
        // paying a syllable on every step of the control for something the
        // scale already implies. The tests below replace the ones that used
        // to prove the scale had a single home.

        [Fact]
        public void The_slice_status_summary_speaks_pan_as_the_number()
        {
            string source = ReadSource("Radios/RadioStatusBuilder.cs");
            Assert.Contains("pan {pan}", source);

            // The failure this guards is a RETURN of the vocabulary, in any
            // form — the shared call, or a fresh hardcoded copy of the bands,
            // which is exactly what lived here before 2026-08-27.
            Assert.DoesNotContain("PanPhrase", source);
            foreach (string band in new[] { "hard left", "far left", "slightly left",
                                            "slightly right", "far right", "hard right" })
                Assert.DoesNotContain(band, source);
        }

        [Fact]
        public void No_pan_words_scale_survives_anywhere_in_the_product()
        {
            // Positive control: the scan must find a file it is meant to
            // read, or an empty result would look like a pass. Both of these
            // legitimately contain the word "pan".
            string layer  = ReadSource("JJFlexWpf/KeyCommands.cs");
            string status = ReadSource("Radios/RadioStatusBuilder.cs");
            Assert.Contains("pan", layer);
            Assert.Contains("pan", status);

            Assert.DoesNotContain("PanPhrase.Words(", layer);
            Assert.DoesNotContain("PanPhrase.Words(", status);
        }

        // ────────────────────────────────────────────────────────────────
        //  The shipped wiring names what this file exercises
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_shipped_audio_layer_matches_the_one_under_test()
        {
            string source = ReadSource("JJFlexWpf/KeyCommands.cs");

            // The doors an operator's fingers know still open it.
            Assert.Contains("case Keys.P | Keys.Alt:", source);
            Assert.Contains("EnterPanMode();", source);
            Assert.Contains("EnterVolumeMode();", source);
            Assert.Contains("EnterAudioLayer(onPan: true)", source);
            Assert.Contains("EnterAudioLayer(onPan: false)", source);

            Assert.Contains("Selection = Radios.ValueLayerSelection.ByLetter", source);
            Assert.Contains("\"audio.audio_layer.name_headphone\", Keys.H | Keys.Control,", source);   // headphone wears Ctrl: H is help
            Assert.Contains("SelectKey = Keys.P", source);   // pan wears Ctrl: P is PC output
            Assert.Contains("Axes = Radios.ValueLayerAxes.Both", source);   // pan answers both pairs
            Assert.Contains("HostKeys = LayerSliceJump", source);
            Assert.Contains("PersistPcOutputVolume()", source);

            // Sprint 44 Track N (#524): the four targets Noel found missing,
            // on the same rig members the mirror stands in for. The letters
            // are PROVISIONAL until Noel rules the map; a re-lettering
            // changes these four lines and the inventory rows together.
            Assert.Contains("SelectKey = Keys.V,", source);                  // slice volume
            Assert.Contains("Min = FlexBase.AudioGainMinValue", source);
            Assert.Contains("SwitchTarget(\"mute\", Keys.M | Keys.Control,", source);
            Assert.Contains("rig.SliceMute = on;", source);
            Assert.Contains("SwitchTarget(\"pc-audio\", Keys.P | Keys.Control,", source);
            Assert.Contains("if (rig.PCAudio != on) _context.PCAudioToggle();", source);
            Assert.Contains("SwitchTarget(\"binaural\", Keys.B | Keys.Control,", source);
            // Sprint 48 Track F (#518): the compander and processor switches,
            // beside their levels, on the shared switch builder.
            Assert.Contains("SwitchTarget(\"compander-switch\", Keys.C | Keys.Control,", source);
            Assert.Contains("SwitchTarget(\"processor-switch\", Keys.S | Keys.Control,", source);
            Assert.Contains("rig.Binaural = on ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;", source);
            Assert.Contains("ToggleKey = chord,", source);
            Assert.Contains("InitialTarget = onPan ? targets.IndexOf(pan) : -1", source);

            // H and Shift+slash go to Track K's surfaces — the list and the
            // explorer — and the fallback sentence is K's too. The
            // duplicate builder Track I wrote is gone (#524).
            Assert.Contains("KeyLayerHelp.Present(context)", source);
            Assert.Contains("Dialogs.KeyExplorerDialog.Open(context)", source);
            Assert.Contains("KeyLayerHelp.SpokenList(KeyInventory.AudioLayerContext)", source);
            Assert.Contains("KeyLayerHelp.SpokenList(KeyInventory.FilterLayerContext)", source);
            Assert.DoesNotContain("LayerHelpSpeech(", source);
            Assert.DoesNotContain("audio.audio_layer.help_no_target", source);

            // #528: the shipped layer tells the engine whether its tones can
            // be heard, so a refusal at Terse is the thunk alone only when
            // the thunk will sound. All three shipped layers wire it (the
            // noise layer since Sprint 48 Track F); a layer that forgot would
            // speak every hint at every level, which is safe and wrong.
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
                source, @"Audible = \(\) => EarconPlayer\.IsOn\(EarconPlayer\.EarconCategory\.CommandsAndConfirmations\)").Count);

            // #547: every shipped layer asks the registry whether a refused
            // key has anywhere to go, and answers the near miss from its own
            // inventory rows. A layer that wired only one of the pair would
            // either never catch a slip or catch keys that meant something —
            // and neither failure announces itself, which is why the count is
            // pinned rather than the presence. Three layers since Track F.
            Assert.Equal(3, Regex.Matches(
                source, @"MeansSomethingOutside = MeansSomethingOutsideTheLayer").Count);
            Assert.Equal(3, Regex.Matches(source, @"DescribeNearMiss = key => LayerNearMiss\(").Count);
            Assert.Contains("KeyLayerHelp.SpokenList(KeyInventory.NoiseLayerContext)", source);
            Assert.Contains("KeyInventory.TryFindLayerNearMiss(context, pressed", source);
            Assert.Contains("private bool MeansSomethingOutsideTheLayer(Keys k) => Lookup(k) != null;", source);

            // #778: every shipped layer also supplies the line for a key that
            // was refused and kept inside and is no near miss. A layer that
            // forgot would answer those keys with the tone alone, which is
            // safe and wrong in the same way as above, so the count is pinned.
            Assert.Equal(3, Regex.Matches(
                source, @"DescribeUnknownKey = \(key, why\) => LayerUnknownKey\(").Count);
            Assert.Contains("private static string LayerUnknownKey(string context, Keys pressed, Radios.ValueLayerRefusal why)", source);
            Assert.Contains("KeyLayerHelp.LayerName(context)", source);

            // #778b: every shipped layer names the keys that act in place
            // through the ONE host helper, and wires the fine-step sentence
            // (the engine decides where it is true). A layer that kept the old
            // verbosity-only lambda would refuse push to talk inside it, out
            // loud; the count is pinned for the same reason as above, and the
            // mirror's InPlaceChord is held to the shipped body here.
            Assert.Equal(3, Regex.Matches(source, @"PassThroughKeys = ActsInPlaceInsideALayer,").Count);
            Assert.DoesNotContain("PassThroughKeys = key => Lookup(key)?.KeyDef.Id == CommandValues.CycleVerbosity", source);
            Assert.Contains("=> InPlaceVerdictFor(k) == InPlaceVerdict.ActsHere;", source);
            // #778b: ONE verdict per key decides both the pass-through and the
            // refusal, so they cannot drift apart; since round three it asks
            // only THIS instance's own table (Codex B2: the log form's reduced
            // table), never the route.
            Assert.Contains("return row != null ? InPlaceVerdict.ActsHere : InPlaceVerdict.NotFromHere;", source);
            Assert.DoesNotContain("_layerKeyFromDialog", source);
            Assert.Equal(3, Regex.Matches(source, @"DescribeUnavailableHere = LayerKeyUnavailableHere,").Count);
            Assert.Contains("return mods == Keys.Control || mods == Keys.Shift;", source);
            Assert.Contains("return code == Keys.OemOpenBrackets || code == Keys.OemCloseBrackets;", source);
            Assert.Equal(3, Regex.Matches(source, @"DescribeFineStep = LayerFineStep,").Count);

            foreach (string key in new[]
            {
                "audio.value_layer.near_miss",
                "audio.value_layer.unknown_key",
                "audio.value_layer.typed_number",
                "audio.value_layer.fine_step",
                "audio.value_layer.ptt_not_here",
                "audio.value_layer.lock_not_here",
                "audio.value_layer.bracket_not_here",
                "audio.audio_layer.entered",
                "audio.audio_layer.entered_on_pan",
                "audio.audio_layer.closed",
                "audio.audio_layer.restored",
                "audio.audio_layer.restored_nothing",
                "audio.audio_layer.pick_target_first",
                "audio.audio_layer.uses_up_down",
                "audio.audio_layer.pan_selected",
                "audio.audio_layer.pan_no_slice",
                "audio.audio_layer.headphone_selected",
                "audio.audio_layer.pc_output",
                "audio.audio_layer.name_slice_volume",
                "audio.audio_layer.slice_volume",
                "audio.audio_layer.slice_volume_selected",
                "audio.audio_layer.slice_volume_no_slice",
                "audio.mute.slice_muted",          // the same words as M on Home — one vocabulary
                "audio.mute.slice_unmuted",
                "audio.pc_audio.on",               // the same words as the PC audio menu item
                "audio.pc_audio.off",
                "audio.pc_audio.could_not_start",
                "audio.binaural.on",
                "audio.binaural.off",
                "settings.pan.level",
            })
            {
                Assert.Contains("\"" + key + "\"", source);
            }

            // The hand-rolled mode is gone — one mechanism, not two.
            Assert.DoesNotContain("DoVolumeModeKey", source);
            Assert.DoesNotContain("_volumeModeActive", source);
            Assert.DoesNotContain("audio.volume_mode.", source);
            Assert.DoesNotContain("audio.pan_mode.", source);
        }

        [Fact]
        public void The_shipped_filter_layer_matches_the_one_under_test()
        {
            string source = ReadSource("JJFlexWpf/KeyCommands.cs");

            Assert.Contains("internal void EnterFilterLayer()", source);
            Assert.Contains("Selection = Radios.ValueLayerSelection.ByModifier", source);
            Assert.Contains("ShiftSideNow = PhysicalKeys.ShiftSideNow", source);
            Assert.Contains("SpeakKey = Keys.S", source);
            Assert.Contains("[Keys.R] = \"receive\"", source);
            Assert.Contains("[Keys.T] = \"transmit\"", source);
            Assert.Contains("InitialGroup = \"receive\"", source);   // lands on receive (#516)
            Assert.Contains("FreqOutHandlers.FilterBoundsForMode(", source);
            Assert.Contains("FreqOutHandlers.MinFilterWidthHz", source);
            Assert.Contains("rig.SetFilter(", source);

            // #527: ONE step rule, both sides and both doors. Three rules for
            // filter edges existed — the adaptive ladder, TXFilterLowIncrement
            // and the menu's hard-coded 50 — and at most two of those could be
            // right. TXFilterLowIncrement survives only as the transmit RAIL.
            Assert.Contains("rx.StepNow = () => FreqOutHandlers.GetAdaptiveFilterStep(rx.Bank.Low, rx.Bank.High);", source);
            Assert.Contains("tx.StepNow = () => FreqOutHandlers.GetAdaptiveFilterStep(tx.Bank.Low, tx.Bank.High);", source);
            Assert.Contains("MinWidth = rig.TXFilterLowIncrement", source);
            Assert.DoesNotContain("StepNow = () => rig.TXFilterLowIncrement", source);

            string menu = ReadSource("JJFlexWpf/NativeMenuBar.cs");
            Assert.Contains("FreqOutHandlers.TxFilterStep(Rig)", menu);
            Assert.DoesNotContain("const int txFilterStep", menu);

            // The FOURTH door the register's count of three had missed: the
            // flat Ctrl+Shift and Ctrl+Alt bracket chords, which moved a
            // transmit edge by a hard-coded 50 of their own. Every remaining
            // 50 in those four handlers is the RAIL, and each is written
            // against the OTHER edge, which is what tells them apart.
            var handlers = Regex.Match(source,
                @"#region TX Filter Handlers.*?#endregion", RegexOptions.Singleline);
            Assert.True(handlers.Success, "the TX Filter Handlers region was not found in KeyCommands.cs");
            Assert.Equal(4, Regex.Matches(handlers.Value, @"FreqOutHandlers\.TxFilterStep\(rig\)").Count);
            foreach (Match m in Regex.Matches(handlers.Value, @"[-+] 50\b"))
            {
                Assert.Contains("TXFilter", handlers.Value.Substring(
                    Math.Max(0, m.Index - 40), Math.Min(40, m.Index)));
            }

            foreach (string key in new[]
            {
                "audio.filter_layer.entered",
                "audio.filter_layer.closed",
                "audio.filter_layer.restored",
                "audio.filter_layer.which_shift",
                "audio.filter_layer.no_verb",
                "audio.filter_layer.at_limit",
                "audio.filter_layer.width",
                "audio.filter_layer.tx_width",
                "audio.filter.rx_report",
                "audio.filter.tx_report",
                "audio.filter.low_edge",
                "audio.filter.high_edge",
                "audio.tx.filter_low",
                "audio.tx.filter_high",
            })
            {
                Assert.Contains("\"" + key + "\"", source);
            }
        }

        [Fact]
        public void The_shift_side_probe_reads_the_instant_of_the_press_and_tracks_no_hold()
        {
            // The JAWS/NVDA held-key divergence (2026-08-25): a probe that
            // remembers a Shift going down has state a synthesised release
            // can corrupt. GetKeyState answers for the message being
            // processed and nothing here stores anything.
            string source = ReadSource("JJFlexWpf/PhysicalKeys.cs");
            Assert.Contains("GetKeyState", source);
            Assert.DoesNotContain("GetAsyncKeyState", source);
            Assert.DoesNotContain("static bool _", source);            // no remembered state
            Assert.DoesNotContain("static Radios.ShiftSide _", source);
            Assert.DoesNotContain("KeyEventArgs", source);            // no event handler, nothing to track
            Assert.DoesNotContain("+=", source);
        }

        [Fact]
        public void The_inventory_describes_both_layers_for_the_explorer()
        {
            // Track K's explorer reads the inventory; a key with no row here
            // works and is invisible. The rows are read from source because
            // Radios.Tests cannot load the WPF assembly.
            string source = ReadSource("JJFlexWpf/KeyInventory.cs");
            Assert.Contains("FixedKeyEntry[] AudioLayerCommands", source);
            Assert.Contains("FixedKeyEntry[] FilterLayerCommands", source);
            Assert.Contains("foreach (var e in AudioLayerCommands) yield return e;", source);
            // Every target the shipped layer builds has a row, in the words
            // the explorer and the H list read (#524's four included).
            foreach (string chord in new[] { "\"V\"", "\"Ctrl+M\"", "\"Ctrl+P\"", "\"Ctrl+B\"" })
                Assert.Contains("new(AudioLayerContext, \"Audio layer\", " + chord + ",", source);
            Assert.Contains("foreach (var e in FilterLayerCommands) yield return e;", source);
            Assert.DoesNotContain("VolumeModeCommands", source);
            Assert.DoesNotContain("PanModeCommands", source);
            foreach (string chord in new[]
            {
                "\"Ctrl+H\"", "\"Ctrl+P\"", "\"Left Shift + Left / Right\"", "\"Right Shift + Left / Right\"",
                "\"Ctrl+Up / Ctrl+Down\"", "\"Left Shift + S\"", "\"Right Shift + S\"",
            })
            {
                Assert.Contains(chord, source);
            }
        }

        [Fact]
        public void The_layer_near_miss_reads_the_help_rows_and_the_layer_ordering()
        {
            // #547. Two properties the mirror above cannot prove, both of them
            // the difference between a useful recovery and a wrong one.
            string source = ReadSource("JJFlexWpf/KeyInventory.cs");

            // The words come from the SAME rows H lists, briefed the same way
            // the leader's near miss briefs them — one vocabulary, not two.
            Assert.Contains("foreach (var e in LayerCommands(context))", source);
            Assert.Contains("Radios.LeaderPhrase.Brief(e.Description)", source);

            // The layer ordering, not the leader's: inside a layer the
            // neighbour of a bare letter is its Ctrl form, on the same
            // subject, and not its Shift form, which changes slice (#515).
            Assert.Contains("Radios.LeaderChordParser.LayerNearMissCandidates(pressed)", source);

            // Letters only, and stated as a rule rather than left to depend on
            // whether a help row's punctuation happens to parse.
            Assert.Equal(2, Regex.Matches(source,
                @"if \(code < WinFormsKeys\.A \|\| code > WinFormsKeys\.Z\)").Count);

            // The two orders, pinned. The leader's gained an Alt tier and moved
            // Shift last in Sprint 48 Track C (the chords moved onto Ctrl and
            // Alt, so those are the likely slips now); the layer's is unchanged
            // and has no Alt, because a value layer passes Alt chords through.
            string parser = ReadSource("Radios/LeaderChordParser.cs");
            Assert.Contains("Candidates(pressed, Keys.None, Keys.Control, Keys.Alt, Keys.Shift)", parser);
            Assert.Contains("Candidates(pressed, Keys.None, Keys.Control, Keys.Shift)", parser);
        }

        [Fact]
        public void The_layers_have_one_help_surface_not_two()
        {
            // #524: KeyInventory.LayerHelpSpeech was a second implementation
            // of "list this layer's keys", beside KeyLayerHelp, in a disjoint
            // file, merged with zero conflict — and the operator got the
            // worse one. One idea, one implementation.
            Assert.DoesNotContain("LayerHelpSpeech(", ReadSource("JJFlexWpf/KeyInventory.cs"));
            Assert.DoesNotContain("leader.layer_help", ReadSource("Radios/Lexicon/leader.json"));
        }

        [Fact]
        public void On_the_main_window_push_to_talk_and_the_brackets_are_handled_before_any_layer_is_asked()
        {
            // #778b, the routing fact under ruling 2. The main window is a
            // UserControl hosted in the shell's ElementHost, NOT a Window, so
            // the Window class handler that routes a live layer first in a
            // dialog (KeyCommands.AnyWindowPreviewKeyDown) is never on its
            // route. There the layer is first asked at step 4 of
            // MainWindow_PreviewKeyDown, the registry dispatch, and push to
            // talk, the transmit lock, Escape while transmitting and the
            // bracket filter keys are all handled at steps 2 and 3, before it.
            // So on Home those keys reach MainWindow's handlers with the layer
            // untouched. The engine's PassThrough for them (the theory above)
            // is what keeps them working everywhere else: a dialog, Logging
            // mode, or focus outside the Home fields, where steps 2 and 3 do
            // not fire. This pin fails if someone moves the registry dispatch
            // above them; that would not break the keys (the layer lets them
            // go), but it would change what this comment, and the report, say.
            string xaml = ReadSource("JJFlexWpf/MainWindow.xaml");
            Assert.StartsWith("<UserControl x:Class=\"JJFlexWpf.MainWindow\"", xaml.TrimStart());

            string source = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            int start = source.IndexOf("private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)", StringComparison.Ordinal);
            Assert.True(start >= 0);
            int step4 = source.IndexOf("DoCommandHandler(winFormsKey)", start, StringComparison.Ordinal);
            Assert.True(step4 > start);
            foreach (string branch in new[]
            {
                "_freqOutHandlers.HandleFilterHotkey(e);",
                "LockKeyDown(e.IsRepeat);",              // #800: Home's lock, one toggle per press, decided at key-down
                "PttKeyDownEdge(PttDoor.Home);",         // round two: the edge records which door keyed the hold
                "_pttController.EscapeUnlock();",
            })
            {
                int at = source.IndexOf(branch, start, StringComparison.Ordinal);
                Assert.True(at > start && at < step4, branch + " must come before the registry dispatch");
            }

            // Tab returns before ANY of it UNLESS a value layer is live (#778c;
            // Noel, 2026-10-21 06:32: "Once in a layer, tab gets refused").
            // Until this round the return was unconditional, so a layer on the
            // main window never saw Tab at all and Home's focus moved behind
            // an open layer. The gate is the owning instance's static, because
            // the main window has no KeyCommands of its own to ask. Ctrl+Tab
            // and Ctrl+Shift+Tab take the same route (Noel 07:41).
            int tab = source.IndexOf("if (rawKey == Key.Tab && !KeyCommands.AValueLayerIsLive)", start, StringComparison.Ordinal);
            Assert.True(tab > start && tab < step4, "the Tab return must be gated on a live layer and sit before the registry dispatch");
            Assert.DoesNotContain("if (rawKey == Key.Tab)\n", source.Substring(start, step4 - start).Replace("\r\n", "\n"));

            string commands = ReadSource("JJFlexWpf/KeyCommands.cs");
            Assert.Contains("internal static bool AValueLayerIsLive => _globalRoutingOwner?._valueLayer != null;", commands);
        }

        [Fact]
        public void In_a_dialog_the_transmit_keys_are_taken_ahead_of_the_layer_and_share_the_edges_with_home()
        {
            // #778c (Noel, 2026-10-21 06:30: Ctrl+Space and Shift+Space
            // "should never be used to activate a button in a dialog"). The
            // door is DialogTransmitKeys; this pins the three things its own
            // tests cannot see because they need a window.
            string commands = ReadSource("JJFlexWpf/KeyCommands.cs");

            // 1. The door runs in the Window class handler BEFORE the mode
            //    check, so a live layer no longer refuses push to talk from a
            //    dialog and no focused control sees the chord.
            int handler = commands.IndexOf("private static void AnyWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)", StringComparison.Ordinal);
            Assert.True(handler >= 0);
            int door = commands.IndexOf("if (DialogTransmitKeys.OnPreviewKeyDown(sender, e))", handler, StringComparison.Ordinal);
            int modeCheck = commands.IndexOf("if (!kc._leaderKeyActive && kc._valueLayer == null)", handler, StringComparison.Ordinal);
            Assert.True(door > handler && modeCheck > door, "the transmit door must run before the leader/layer check");

            // 2. The release has its own class handler: a key-up is never a
            //    command and the preview key-down route never sees one.
            Assert.Contains("System.Windows.Input.Keyboard.PreviewKeyUpEvent,", commands);
            Assert.Contains("new System.Windows.Input.KeyEventHandler(AnyWindowPreviewKeyUp));", commands);
            Assert.Contains("if (DialogTransmitKeys.OnPreviewKeyUp(e)) e.Handled = true;", commands);

            // 3. One pipeline, three doors: the dialog door's delegates point
            //    at MainWindow callers that share one gate with the
            //    system-wide door, and that gate ends in the same two edges
            //    Home's step 3 and PreviewKeyUp call. No second keying path.
            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            Assert.Contains("DialogTransmitKeys.PttEdge = HandleDialogPttEdge;", main);
            Assert.Contains("DialogTransmitKeys.HoldInFlight =", main);
            Assert.Contains("internal void HandleDialogPttEdge(bool down) => PttEdgeFromOutsideHome(down, PttDoor.Dialog);", main);
            Assert.Contains("internal void HandleSystemWidePttEdge(bool down) => PttEdgeFromOutsideHome(down, PttDoor.SystemWide);", main);
            int gate = main.IndexOf("private void PttEdgeFromOutsideHome(bool down, PttDoor door)", StringComparison.Ordinal);
            Assert.True(gate >= 0);
            string gateBody = main.Substring(gate, main.IndexOf("private void StartPttDeferTimer(int ms)", gate, StringComparison.Ordinal) - gate);
            Assert.Contains("if (down) PttKeyDownEdge(door); else PttKeyUpEdge();", gateBody);
            Assert.Contains("if (down) Radios.ScreenReaderOutput.SpeakNoRadioConnected();", gateBody);
            // Round three (Noel 10:55): the dialog door has no lock delegate
            // any more. Shift+Space in a dialog is consumed and refused inside
            // the door, through the same path as the Audio Check refusal.
            Assert.DoesNotContain("DialogTransmitKeys.ToggleLock", main);
            Assert.DoesNotContain("HandleDialogLockToggle", main);

            // Repair round one. B1: Escape from a dialog unkeys through the
            // controller, as Home's step 3 does (Noel 07:37). F2: window loss
            // releases NOW, dropping any deferred release, never through the
            // deferring key-up edge. B3: the door asks the Audio Workshop
            // whether a session is active, read-only, from outside the
            // AudioCheck partial (Noel 07:38).
            Assert.Contains("DialogTransmitKeys.EscapeUnkey = HandleDialogEscapeUnkey;", main);
            Assert.Contains("DialogTransmitKeys.ReleaseNow = HandleDialogHoldLost;", main);
            Assert.Contains("DialogTransmitKeys.SessionOwnsTransmitter = () => Dialogs.AudioWorkshopDialog.AnAudioCheckIsActive;", main);
            int escape = main.IndexOf("internal bool HandleDialogEscapeUnkey()", StringComparison.Ordinal);
            Assert.True(escape >= 0);
            string escapeBody = main.Substring(escape, 400);
            Assert.Contains("_pttController.EscapeUnlock();", escapeBody);
            string workshop = ReadSource("JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml.cs");
            Assert.Contains("internal static bool AnAudioCheckIsActive => _instance?._session?.Active == true;", workshop);

            // Repair round two (Noel 09:51, "simplify yes").
            // Codex B1 by class: every path that ends a transmission OUTSIDE
            // the key edges drops the edges' input state through one helper,
            // or a deferred release left pending swallows the next
            // Ctrl+Space. The dialog's Escape, Home's step-3 Escape
            // (PRE-EXISTING), window loss, the radio turning transmit off on
            // its own, and the radio closing. The helper stops the timer,
            // resets both press trackers and clears the guard and the door.
            static string After(string text, string marker)
            {
                int at = text.IndexOf(marker, StringComparison.Ordinal);
                Assert.True(at >= 0, "expected to find: " + marker);
                return text.Substring(at);
            }
            Assert.Contains("ResetPttInput();", After(escapeBody, "_pttController.EscapeUnlock();"));
            int helper = main.IndexOf("private void ResetPttInput()", StringComparison.Ordinal);
            Assert.True(helper >= 0);
            string helperBody = main.Substring(helper, 400);
            foreach (string line in new[] { "_pttDeferTimer?.Stop();", "_pttHoldFilter.Reset();", "_pttKeyDown = false;", "_pttHoldDoor = PttDoor.None;" })
                Assert.Contains(line, helperBody);
            int home = main.IndexOf("private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)", StringComparison.Ordinal);
            int homeStep4 = main.IndexOf("DoCommandHandler(winFormsKey)", home, StringComparison.Ordinal);
            Assert.True(home >= 0 && homeStep4 > home);
            int step3Escape = main.IndexOf("if (rawKey == Key.Escape && _pttController.IsTransmitting)", home, StringComparison.Ordinal);
            Assert.True(step3Escape > home && step3Escape < homeStep4);
            Assert.Contains("ResetPttInput();", After(main.Substring(step3Escape, 1200), "_pttController.EscapeUnlock();").Substring(0, 120));
            int txChange = main.IndexOf("private void TransmitChangeHandler(object sender, bool transmit)", StringComparison.Ordinal);
            Assert.True(txChange >= 0);
            int txChangeEnd = main.IndexOf("private void FlexAntTuneStartStopHandler(", txChange, StringComparison.Ordinal);
            Assert.True(txChangeEnd > txChange);
            Assert.Contains("ResetPttInput();", After(main.Substring(txChange, txChangeEnd - txChange), "_pttController.EscapeUnlock();"));
            int lost = main.IndexOf("internal void HandleDialogHoldLost()", StringComparison.Ordinal);
            Assert.True(lost >= 0);
            string lostBody = main.Substring(lost, 800);
            Assert.Contains("ResetPttInput();", lostBody);
            Assert.Contains("_pttController.PttUp();", lostBody);
            // Codex F2 by class: a dialog releases only a hold its own door
            // keyed, and a deferred release that runs to ground in the timer
            // tells the door the hold is over.
            Assert.Contains("_pttHoldDoor != PttDoor.Dialog", lostBody);
            int tick = main.IndexOf("if (_pttHoldFilter.DeferralElapsed(Environment.TickCount64))", StringComparison.Ordinal);
            Assert.True(tick >= 0);
            string tickBody = main.Substring(tick, 600);
            Assert.True(tickBody.IndexOf("DialogTransmitKeys.HoldReleased();", StringComparison.Ordinal)
                        > tickBody.IndexOf("_pttController?.PttUp();", StringComparison.Ordinal),
                "the timer must tell the dialog door the hold ended, after unkeying");
            // Codex B3 by class: a second workshop's close clears the tracked
            // pointer only when it is the tracked workshop.
            Assert.Contains("if (ReferenceEquals(_instance, this)) _instance = null;", workshop);
            Assert.DoesNotContain("            _instance = null;\n", workshop.Replace("\r\n", "\n"));

            // The door itself never touches the controller or the rig: it only
            // calls its three delegates. Nothing in it names PttSafetyController
            // as a call, FlexBase, or Transmit.
            string doorSource = ReadSource("JJFlexWpf/DialogTransmitKeys.cs");
            Assert.DoesNotContain("_pttController", doorSource);
            Assert.DoesNotContain(".PttDown()", doorSource);
            Assert.DoesNotContain(".PttUp()", doorSource);
            Assert.DoesNotContain(".Transmit =", doorSource);      // never rig.Transmit = ...
            Assert.DoesNotContain("rig.Transmit", doorSource);
            Assert.DoesNotContain("PttSafetyController.", doorSource.Replace("<see cref=\"PttSafetyController", ""));
            Assert.Equal(2, Regex.Matches(doorSource, @"PttEdge\?\.Invoke\(false\)").Count);   // key-up, and the window lost
            Assert.Equal(1, Regex.Matches(doorSource, @"PttEdge\?\.Invoke\(true\)").Count);
        }

        [Fact]
        public void Homes_lock_and_the_system_wide_lock_read_exactly_as_main_and_the_dialog_lock_is_refused_for_now()
        {
            // #778c repair round three. Noel, 2026-10-21 10:55: "wait for the
            // bench". Codex found (B5) that a lock press decided at its key-up
            // stayed pending through Escape at Idle, a close or a window
            // switch, and a later unrelated Space release locked the
            // transmitter. So the lock classifier is gone entirely, Home's
            // Shift+Space and the system-wide lock go back to what main does,
            // and Shift+Space in a dialog is consumed and refused until a
            // quick JAWS tap has been measured. This is a SOURCE PIN, stated
            // as one: Home's handlers cannot be driven without a window, so
            // the three snippets below are main's text (d9a079cf) verbatim,
            // and the pin fails if any of them drifts from it.
            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs").Replace("\r\n", "\n");
            int home = main.IndexOf("private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)", StringComparison.Ordinal);
            int homeStep4 = main.IndexOf("DoCommandHandler(winFormsKey)", home, StringComparison.Ordinal);
            Assert.True(home >= 0 && homeStep4 > home);

            // 1. Home's step 3. UNTIL #800 (2026-10-22) this pinned main's
            //    text, which called ToggleLock on EVERY key-down, repeats
            //    included — and so pinned #800's bug as the desired behaviour:
            //    on 2026-10-21 a Shift+Space held 0.8 s keyed and unkeyed the
            //    radio sixteen times in under a second. Noel, 20:46: "holding
            //    it should just lock and unlock it for sure." The pin now says
            //    the opposite: one toggle per PRESS, decided at key-down from
            //    the event's own repeat flag, with no state and nothing at
            //    key-up (Codex B5). The branch hands the flag to LockKeyDown,
            //    whose body is pinned beside it.
            const string step3LockOneTogglePerPress =
                "            if (rawKey == Key.Space && Keyboard.Modifiers == ModifierKeys.Shift)\n" +
                "            {\n" +
                "                LockKeyDown(e.IsRepeat);\n" +
                "                e.Handled = true;\n" +
                "                return;\n" +
                "            }\n";
            int step3Lock = main.IndexOf(step3LockOneTogglePerPress, home, StringComparison.Ordinal);
            Assert.True(step3Lock > home && step3Lock < homeStep4, "Home's Shift+Space branch must hand the repeat flag to LockKeyDown");
            const string lockKeyDownEdge =
                "    internal void LockKeyDown(bool isRepeat)\n" +
                "    {\n" +
                "        if (isRepeat) return;   // a held key is one press\n" +
                "        _lockPairDetector.NoteDown(Environment.TickCount64);\n" +
                "        _pttController?.ToggleLock();\n" +
                "    }\n";
            Assert.Contains(lockKeyDownEdge, main);
            Assert.Equal(1, Regex.Matches(main, @"\bLockKeyDown\(e\.IsRepeat\);").Count);

            // 2. The system-wide lock: main's handler, body and all.
            const string systemWideLockAsOnMain =
                "    /// <summary>The system-wide transmit lock: what Shift+Space does inside the window.</summary>\n" +
                "    internal void HandleSystemWideLockToggle()\n" +
                "    {\n" +
                "        if (_pttController == null || !_radioPowerOn)\n" +
                "        {\n" +
                "            Radios.ScreenReaderOutput.SpeakNoRadioConnected();\n" +
                "            return;\n" +
                "        }\n" +
                "        _pttController.ToggleLock();\n" +
                "    }\n";
            Assert.Contains(systemWideLockAsOnMain, main);

            // 3. The Space key-up on Home: main's body. Ahead of it, the
            //    round-three measurement trace (pinned below) and, since #800,
            //    the lock-key pair DETECTOR — a counter and a trace line, which
            //    decides nothing and acts on nothing (Phase 2 as settled
            //    2026-10-22: count and trace, build no guard). Those two lines
            //    are the only difference from main.
            const string keyUpAsOnMain =
                "            // Detection only (#800): a lock release too fast to be human is\n" +
                "            // counted and traced, and changes nothing.\n" +
                "            _lockPairDetector.NoteUp(Environment.TickCount64);\n" +
                "            // Read the state BEFORE the edge runs: a consumed release takes the\n" +
                "            // controller out of PttHold, and asking afterwards would let the\n" +
                "            // very key-up that unkeyed fall through to the focused control.\n" +
                "            bool wasHold = _pttController != null\n" +
                "                           && _pttController.State == PttSafetyController.PttState.PttHold;\n" +
                "            PttKeyUpEdge();\n" +
                "            if (wasHold) e.Handled = true;\n" +
                "        }\n";
            int keyUp = main.IndexOf("private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)", StringComparison.Ordinal);
            Assert.True(keyUp >= 0);
            Assert.Contains(keyUpAsOnMain, main.Substring(keyUp, 1400));

            // Nothing of the lock classifier, round two's edges, or round one's
            // hold filter survives, in MainWindow or in Radios.
            foreach (string gone in new[]
            {
                "LockPressClassifier", "_lockPresses", "BuildLockPressClassifier", "LockKeyDownEdge", "LockKeyUpEdge",
                "LockToggleFromOutsideHome", "HandleDialogLockToggle", "PttClock", "_lockHoldFilter", "_lockLastUpMs",
            })
                Assert.DoesNotContain(gone, main);
            Assert.False(File.Exists(Path.Combine(RepoRoot(), "Radios", "LockPressClassifier.cs")), "Radios/LockPressClassifier.cs must be gone");
            Assert.False(File.Exists(Path.Combine(RepoRoot(), "Radios.Tests", "LockPressClassifierTests.cs")), "its tests must be gone with it");

            // The dialog door: no lock delegate; Shift+Space on a non-text
            // control is refused through RefuseTransmitKey, the same path the
            // Audio Check refusal takes, and nothing else in the door speaks.
            string door = ReadSource("JJFlexWpf/DialogTransmitKeys.cs");
            Assert.DoesNotContain("ToggleLock", door);
            Assert.Contains("case Verdict.LockRefused:", door);
            Assert.Contains("RefuseTransmitKey(\"audio.value_layer.lock_not_here\");", door);
            Assert.Equal(1, Regex.Matches(door, @"private static void RefuseTransmitKey\(").Count);
            Assert.Equal(2, Regex.Matches(door, @"RefuseTransmitKey\(").Count - 1);   // the lock, and the push-to-talk refusal under an Audio Check

            // Item 5: the measurement trace. One line per Space key event, on
            // Home's two preview handlers and on the dialog door's two entries,
            // gated at TraceLevel.Verbose (the level StartDetailedCapture
            // raises the switch to), and nowhere else: no other key is traced.
            string trace = ReadSource("JJFlexWpf/SpaceKeyTimingTrace.cs");
            Assert.Contains("TraceLevel.Verbose", trace);
            Assert.Equal(1, Regex.Matches(trace, @"Tracing\.TraceLine\(").Count);
            string homeDown = main.Substring(home, homeStep4 - home);
            Assert.Contains("if (rawKey == Key.Space)\n            SpaceKeyTimingTrace.Note(down: true,", homeDown);
            Assert.True(homeDown.IndexOf("SpaceKeyTimingTrace.Note(down: true,", StringComparison.Ordinal)
                        < homeDown.IndexOf("if (rawKey == Key.Tab && !KeyCommands.AValueLayerIsLive)", StringComparison.Ordinal),
                "the Home key-down trace must run before any branch can return");
            Assert.Contains("SpaceKeyTimingTrace.Note(down: false,", main.Substring(keyUp, 1200));
            Assert.Equal(2, Regex.Matches(main, @"SpaceKeyTimingTrace\.Note\(").Count);
            Assert.Equal(2, Regex.Matches(main, @"SpaceKeyTimingTrace\.HomeDoor").Count);
            Assert.Equal(2, Regex.Matches(door, @"SpaceKeyTimingTrace\.Note\(").Count);
            Assert.Equal(2, Regex.Matches(door, @"SpaceKeyTimingTrace\.DialogDoor").Count);
            Assert.DoesNotContain("SpaceKeyTimingTrace", ReadSource("JJFlexWpf/KeyCommands.cs"));
        }

        [Fact]
        public void Only_the_dialog_window_handlers_pass_the_dialog_route_and_the_verdict_does_not_ask_it()
        {
            // #778b, Codex F1 (round two) and B2 (round three). Which route a
            // press comes by is the fromDialog argument: exactly two call sites
            // pass true, both reached only from the Window class handlers
            // (AnyWindowPreviewKeyDown and DispatchFromDialogWindow), which never
            // run for the main window, a UserControl in the shell's ElementHost.
            // The shell, the main window and the WinForms log form all call the
            // public one-argument DoCommand, which is fromDialog: false. Round
            // two decided "acts in place" from that flag, and the log form made
            // it false (B2): main route, no main-window handler. So since round
            // three the verdict does not ask the route at all; this pin records
            // who is on which route so nobody is tempted to bring it back.
            string commands = ReadSource("JJFlexWpf/KeyCommands.cs");
            Assert.Equal(2, Regex.Matches(commands, @"fromDialog: true\)").Count);
            Assert.Contains("public bool DoCommand(Keys k) => DoCommand(k, fromDialog: false);", commands);
            Assert.Contains("if (kc.DoCommand(WpfKeyConverter.ToWinFormsKeys(e), fromDialog: true))", commands);
            Assert.Contains("return DoCommand(k, fromDialog: true);", commands);

            foreach (string file in new[] { "BridgeForm.vb", "ApplicationEvents.vb", "LogEntry.vb", "JJFlexWpf/MainWindow.xaml.cs" })
                Assert.DoesNotContain("fromDialog", ReadSource(file));
            Assert.Contains("WpfContent.DoCommandHandler(keyData)", ReadSource("BridgeForm.vb"));
            Assert.Contains("WpfMainWindow.DoCommandHandler = AddressOf Commands.DoCommand", ReadSource("ApplicationEvents.vb"));
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
