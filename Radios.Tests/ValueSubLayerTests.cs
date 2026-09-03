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
        public void Single_speaks_words_at_chatty_and_numbers_at_terse()
        {
            var (h, _) = OpenSingle(40, VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.Up);
            h.Verbosity = VerbosityLevel.Terse;
            h.Layer.HandleKey(Keys.Up);
            Assert.Equal("low", h.Moves[0].Text);
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
        public void Single_home_and_the_anchor_letter_jump_to_the_anchor()
        {
            var (h, rig) = OpenSingle(10, VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.Home));
            Assert.Equal(50, rig.Value);
            h.Layer.HandleKey(Keys.Up);
            h.Layer.HandleKey(Keys.C);
            Assert.Equal(50, rig.Value);
            Assert.Equal("Power 50", h.LastMove);
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
        public void Single_an_unknown_key_confirms_announces_and_travels_on()
        {
            var (h, rig) = OpenSingle(40, VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            var r = h.Layer.HandleKey(Keys.X);

            Assert.Equal(ValueLayerKeyResult.ClosedPassThrough, r);
            Assert.False(h.Layer.IsLive);
            Assert.Equal(new[] { 45 }, rig.Writes);
            Assert.Equal("Power layer closed", h.LastSaid);
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
            public readonly List<string> Writes = new();
            public int Persisted;
            public readonly List<string> Jumps = new();
        }

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
            var pcOutput = Level("pc-output", "audio.audio_layer.name_pc_output", Keys.P,
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
                Id = "pan", Name = "", SelectKey = Keys.P | Keys.Control, PerSlice = true,
                Read = () => rig.Pan[rig.Slice],
                Apply = v => { rig.Pan[rig.Slice] = v; rig.Writes.Add("pan " + rig.Slice + " " + v); },
                Min = 0, Max = 100, Step = 5, FineStep = 1, Axes = ValueLayerAxes.Both, Anchor = centre,
                Number = v => Lexicon.Get("settings.pan.level", ("level", v)),
                Words = PanPhrase.Words,
                DescribeSelected = v => Lexicon.Get("audio.audio_layer.pan_selected", h.Verbosity,
                    ("letter", rig.Slice), ("level", v), ("position", PanPhrase.Words(v))),
            };

            var def = new ValueSubLayerDefinition
            {
                Id = "audio",
                Selection = ValueLayerSelection.ByLetter,
                Targets = new List<ValueTarget> { headphone, pcOutput, mic, lineout, compander, processor, pan },
                InitialTarget = onPan ? 6 : -1,
                DescribeLayerEntry = layer => layer.CurrentTarget == pan
                    ? Lexicon.Get("audio.audio_layer.entered_on_pan", h.Verbosity, ("target", layer.DescribeTarget(pan)))
                    : Lexicon.Get("audio.audio_layer.entered", h.Verbosity),
                DescribeLayerHelp = layer => "HELP: " + Lexicon.Get("audio.audio_layer.name") + ", "
                    + (layer.CurrentTarget != null ? layer.DescribeTarget(layer.CurrentTarget)
                                                   : Lexicon.Get("audio.audio_layer.help_no_target")),
                DescribeClosed = () => Lexicon.Get("audio.audio_layer.closed"),
                DescribeLayerRestored = (layer, restored) => restored.Count == 0
                    ? Lexicon.Get("audio.audio_layer.restored_nothing")
                    : Lexicon.Get("audio.audio_layer.restored", ("list", string.Join(", ",
                        restored.Select(r => r.Target == pan
                            ? Lexicon.Get("audio.audio_layer.pan_restore_item", h.Verbosity,
                                ("level", r.RestoredTo), ("position", PanPhrase.Words(r.RestoredTo)))
                            : layer.FormOf(r.Target, r.RestoredTo))))),
                PickTargetHint = () => Lexicon.Get("audio.audio_layer.pick_target_first"),
                PassThroughKeys = k => k == (Keys.V | Keys.Control | Keys.Shift),
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
        public void Audio_entry_at_chatty_teaches_the_letters()
        {
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            Assert.Equal(
                "Audio layer. Ctrl+H headphone, P PC output, M mic, L line out, C compander, S processor, "
                + "Ctrl+P pan. Up and down adjust. Enter keeps it, Escape puts it back, H lists the keys.",
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
            Assert.Equal("Pick a target first: Ctrl+H, P, M, L, C, S, or Ctrl+P.", h.LastSaid);
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
            Assert.Equal("HELP: Audio layer, no target picked", h.LastSaid);
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
            Assert.Equal("Pick a target first: Ctrl+H, P, M, L, C, S, or Ctrl+P.", h.LastSaid);
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
            Assert.Equal("Pick a target first: Ctrl+H, P, M, L, C, S, or Ctrl+P.", h.LastSaid);
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
        public void Audio_pan_answers_both_arrow_pairs_and_home_centres()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P | Keys.Control);
            Assert.Equal("Pan, slice A, slightly left", h.LastAnswer);
            h.Layer.HandleKey(Keys.Right);   // 45
            h.Layer.HandleKey(Keys.Up);      // 50
            h.Layer.HandleKey(Keys.Down | Keys.Shift); // 49
            Assert.Equal(49, rig.Pan["A"]);
            Assert.Equal(new[] { "slightly left", "center", "slightly left" },
                h.Moves.Select(m => m.Text));
            h.Layer.HandleKey(Keys.Home);
            Assert.Equal(50, rig.Pan["A"]);
            Assert.Equal("center", h.LastMove);
        }

        [Fact]
        public void Audio_the_alt_p_door_opens_on_pan()
        {
            var (h, _) = OpenAudio(VerbosityLevel.Chatty, onPan: true);
            Assert.Equal(
                "Audio layer. Pan, slice A, slightly left. Left and right or up and down adjust, "
                + "Shift moves by one, Home centers. Enter keeps it, Escape puts it back, H lists the keys.",
                Assert.Single(h.Said));
            var (t, _) = OpenAudio(VerbosityLevel.Terse, onPan: true);
            Assert.Equal("Audio layer. Pan, slice A, pan 40.", Assert.Single(t.Said));
        }

        [Fact]
        public void Audio_escape_puts_back_everything_that_moved_in_order_and_says_so()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.H | Keys.Control); h.Layer.HandleKey(Keys.Up);          // headphone 45
            h.Layer.HandleKey(Keys.P);                h.Layer.HandleKey(Keys.Down);        // pc 11
            h.Layer.HandleKey(Keys.P | Keys.Control); h.Layer.HandleKey(Keys.Right);       // pan 45
            h.Layer.HandleKey(Keys.M);                                                     // picked, not moved
            var r = h.Layer.HandleKey(Keys.Escape);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.Equal(40, rig.Headphone);
            Assert.Equal(12, rig.PcVolume);
            Assert.Equal(40, rig.Pan["A"]);
            Assert.Equal(new[] { "headphone 45", "pc 11", "pan A 45", "headphone 40", "pc 12", "pan A 40" }, rig.Writes);
            Assert.Equal("Put back Headphone 40, PC volume 12 dB, pan 40. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_escape_with_nothing_moved_says_so()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.L);
            h.Layer.HandleKey(Keys.Escape);
            Assert.Empty(rig.Writes);
            Assert.Equal("Nothing moved. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_escape_at_chatty_puts_pan_back_in_words()
        {
            var (h, _) = OpenAudio(VerbosityLevel.Chatty);
            h.Layer.HandleKey(Keys.P | Keys.Control);
            h.Layer.HandleKey(Keys.Right);
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal("Put back pan slightly left. Audio layer closed", h.LastSaid);
        }

        [Fact]
        public void Audio_enter_keeps_everything_and_persists_the_pc_volume_once()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.P); h.Layer.HandleKey(Keys.Up); h.Layer.HandleKey(Keys.Up);
            h.Layer.HandleKey(Keys.Return);
            Assert.Equal(14, rig.PcVolume);
            Assert.Equal(1, rig.Persisted);
        }

        [Fact]
        public void Audio_ctrl_j_hands_off_and_still_persists_the_pc_volume()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.P); h.Layer.HandleKey(Keys.Up);
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
            h.Layer.HandleKey(Keys.P | Keys.Control);
            h.Layer.HandleKey(Keys.Right);                       // A: 45
            Assert.Equal(ValueLayerKeyResult.Handled, h.Layer.HandleKey(Keys.C | Keys.Shift));
            Assert.True(h.Layer.IsLive);
            Assert.Equal(new[] { "C" }, rig.Jumps);
            Assert.Equal("Pan, slice C, center", h.LastAnswer);
            h.Layer.HandleKey(Keys.Left);                        // C: 45
            h.Layer.HandleKey(Keys.Escape);
            Assert.Equal(45, rig.Pan["A"]);                      // kept — confirmed by leaving
            Assert.Equal(50, rig.Pan["C"]);                      // restored
            Assert.Equal("Put back pan center. Audio layer closed", h.LastSaid);
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
        public void Audio_an_unknown_key_confirms_and_travels_on()
        {
            var (h, rig) = OpenAudio(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.M); h.Layer.HandleKey(Keys.Up);
            Assert.Equal(ValueLayerKeyResult.ClosedPassThrough, h.Layer.HandleKey(Keys.X));
            Assert.Equal(35, rig.Mic);
            Assert.Equal("Audio layer closed", h.LastSaid);
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

        private static int AdaptiveStep(int low, int high)
        {
            int width = high - low;
            if (width < 200) return 10;
            if (width < 500) return 25;
            if (width < 2000) return 50;
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
            if (low < lowMin) low = lowMin;
            if (high > highMax) high = highMax;
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
                LowMin = () => 0, HighMax = () => 10000, MinWidth = 50, StepNow = () => 50,
            };
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
                DescribeLayerRestored = (layer, restored) =>
                {
                    var parts = new List<string>();
                    if (rx.Bank.Touched) parts.Add(Lexicon.Get("audio.filter_layer.restored_receive", ("low", rx.Bank.EntryLow), ("high", rx.Bank.EntryHigh)));
                    if (tx.Bank.Touched) parts.Add(Lexicon.Get("audio.filter_layer.restored_transmit", ("low", tx.Bank.EntryLow), ("high", tx.Bank.EntryHigh)));
                    return parts.Count == 0
                        ? Lexicon.Get("audio.filter_layer.restored_nothing")
                        : Lexicon.Get("audio.filter_layer.restored", ("list", string.Join(", ", parts)));
                },
                WhichShiftHint = () => Lexicon.Get("audio.filter_layer.which_shift"),
                NoVerbHint = () => Lexicon.Get("audio.filter_layer.no_verb"),
                WrongAxisHint = () => Lexicon.Get("audio.filter_layer.no_verb"),
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
        public void Filter_entry_lands_on_receive_and_teaches_the_grammar_at_chatty()
        {
            var (h, _) = OpenFilter(VerbosityLevel.Chatty);
            Assert.Equal(
                "Filter layer. RX filter 100 to 2800, 2.7 kilohertz. Left Shift with Left and Right walks the low edge, "
                + "Right Shift the high edge. Up and down slide the whole filter, Control up and down set the width. "
                + "S speaks it, T transmit, R receive. Enter keeps it, Escape puts it back, H lists the keys.",
                Assert.Single(h.Said));
            Assert.Equal("receive", h.Layer.CurrentGroup);
            Assert.Null(h.Layer.CurrentTarget);
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
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Assert.Equal(ValueLayerKeyResult.Handled, Press(h, rig, Keys.Left | Keys.Shift, ShiftSide.Left));
            Assert.Equal(ValueLayerKeyResult.Closed, h.Layer.HandleKey(Keys.Return));
            Assert.Equal(new[]
            {
                "Filter layer. RX filter 100 to 2800, 2.7 kilohertz.",
                "Low edge 0",
                "Filter layer closed",
            }, h.Transcript);
            Assert.Equal((0, 2800), (rig.RxLow, rig.RxHigh));
            Assert.Equal("value-layer:filter:rx-low", h.Moves[0].Key);
        }

        [Fact]
        public void Filter_right_shift_with_right_walks_the_high_edge_up()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Right);
            Assert.Equal((100, 2900), (rig.RxLow, rig.RxHigh));
            Assert.Equal("High edge 2900", h.LastMove);
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
            Assert.Equal((200, 2900), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Filter 200 to 2900", h.LastMove);
            h.Layer.HandleKey(Keys.Left);
            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));
        }

        [Fact]
        public void Filter_ctrl_up_widens_about_the_centre_and_ctrl_down_narrows()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up | Keys.Control);
            Assert.Equal((0, 2900), (rig.RxLow, rig.RxHigh));
            Assert.Equal("Width 2900, 0 to 2900", h.LastMove);
            h.Layer.HandleKey(Keys.Down | Keys.Control);
            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));
        }

        [Fact]
        public void Filter_the_whole_filter_stops_at_the_mode_bound_and_says_so()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Down);   // low would be 0 → allowed
            Assert.Equal((0, 2700), (rig.RxLow, rig.RxHigh));
            h.Layer.HandleKey(Keys.Down);   // low would be -100 → USB floor
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
            for (int i = 0; i < 60; i++) Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Left);
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
            Press(h, rig, Keys.Left | Keys.Shift, ShiftSide.Left);   // rx 0..2800
            h.Layer.HandleKey(Keys.Up);                                // rx 100..2900
            h.Layer.HandleKey(Keys.T);
            Press(h, rig, Keys.Right | Keys.Shift, ShiftSide.Right);  // tx 300..2750
            var r = h.Layer.HandleKey(Keys.Escape);

            Assert.Equal(ValueLayerKeyResult.Closed, r);
            Assert.Equal((100, 2800), (rig.RxLow, rig.RxHigh));
            Assert.Equal((300, 2700), (rig.TxLow, rig.TxHigh));
            Assert.Equal("rx 100 2800", rig.Writes[^2]);
            Assert.Equal("tx 300 2700", rig.Writes[^1]);
            Assert.Equal("Put back receive filter 100 to 2800, transmit filter 300 to 2700. Filter layer closed", h.LastSaid);
        }

        [Fact]
        public void Filter_escape_with_nothing_moved_writes_nothing()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.T);
            h.Layer.HandleKey(Keys.Escape);
            Assert.Empty(rig.Writes);
            Assert.Equal("Nothing moved. Filter layer closed", h.LastSaid);
        }

        [Fact]
        public void Filter_enter_keeps_and_writes_nothing_more()
        {
            var (h, rig) = OpenFilter(VerbosityLevel.Terse);
            h.Layer.HandleKey(Keys.Up);
            h.Layer.HandleKey(Keys.Return);
            Assert.Equal(new[] { "rx 200 2900" }, rig.Writes);
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
            string source = ReadSource("Radios/RadioStatusBuilder.cs");
            Assert.Contains("PanPhrase.Words(", source);
            Assert.DoesNotContain("\"pan slightly left\"", source);
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
            Assert.Contains("SelectKey = Keys.P | Keys.Control", source);   // pan wears Ctrl: P is PC output
            Assert.Contains("Axes = Radios.ValueLayerAxes.Both", source);   // pan answers both pairs
            Assert.Contains("HostKeys = LayerSliceJump", source);
            Assert.Contains("PersistPcOutputVolume()", source);
            Assert.Contains("KeyInventory.LayerHelpSpeech(", source);

            // #528: the shipped layer tells the engine whether its tones can
            // be heard, so a refusal at Terse is the thunk alone only when
            // the thunk will sound. Both shipped layers wire it; a layer
            // that forgot would speak every hint at every level, which is
            // safe and wrong.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
                source, @"Audible = \(\) => EarconPlayer\.IsOn\(EarconPlayer\.EarconCategory\.CommandsAndConfirmations\)").Count);

            foreach (string key in new[]
            {
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
            Assert.Contains("FreqOutHandlers.GetAdaptiveFilterStep(", source);   // one step rule, two doors
            Assert.Contains("FreqOutHandlers.FilterBoundsForMode(", source);
            Assert.Contains("FreqOutHandlers.MinFilterWidthHz", source);
            Assert.Contains("rig.TXFilterLowIncrement", source);
            Assert.Contains("rig.SetFilter(", source);

            foreach (string key in new[]
            {
                "audio.filter_layer.entered",
                "audio.filter_layer.closed",
                "audio.filter_layer.restored",
                "audio.filter_layer.restored_receive",
                "audio.filter_layer.restored_transmit",
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
