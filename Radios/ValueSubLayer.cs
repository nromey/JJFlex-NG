using System;
using System.Diagnostics;
using System.Windows.Forms;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// How a value sub-layer's adjust keys are laid out: a left/right pair for
    /// spatial values (pan), an up/down pair for magnitude values (volume,
    /// transmit power). The other pair is never silently dead — it speaks the
    /// layer's wrong-axis hint so a habit from one layer cannot become a
    /// mystery in another.
    /// </summary>
    public enum ValueLayerAxis
    {
        LeftRight,
        UpDown,
    }

    /// <summary>
    /// What <see cref="ValueSubLayer.HandleKey"/> did with a key, so the host
    /// dispatcher knows whether to consume it, let it travel, or re-arm the
    /// leader. The host owns the plumbing; the layer owns the decision.
    /// </summary>
    public enum ValueLayerKeyResult
    {
        /// <summary>Consumed; the layer is still live.</summary>
        Handled,

        /// <summary>
        /// Not consumed and the layer is STILL LIVE. Alt chords, F1 and any
        /// host-whitelisted key (the verbosity cycle) travel on so the app
        /// keeps working under the layer — the volume-mode precedent.
        /// </summary>
        PassThrough,

        /// <summary>Consumed; the layer closed (Enter confirm or Escape cancel).</summary>
        Closed,

        /// <summary>
        /// The layer confirmed and closed, and the key was NOT consumed: an
        /// unhandled key keeps the value and travels on to mean what it always
        /// means. This is the pattern's answer to the stuck-modal problem —
        /// there is no key that can strand an operator here, because every key
        /// either works or leaves.
        /// </summary>
        ClosedPassThrough,

        /// <summary>
        /// Ctrl+J inside the layer: confirmed and closed silently; the host
        /// should arm a fresh leader chord, exactly as volume mode does.
        /// </summary>
        ClosedHandOff,
    }

    /// <summary>
    /// Earcon hooks. The engine lives below JJFlexWpf, where EarconPlayer
    /// lives, so the host wires these at Enter time. All optional; speech is
    /// the engine's own and does not route through here.
    /// </summary>
    public sealed class ValueLayerCues
    {
        /// <summary>Layer opened (LeaderEnterTone at the keyboard host).</summary>
        public Action? Entered;

        /// <summary>Layer closed by any spoken path (LeaderCancelTone).</summary>
        public Action? Closed;

        /// <summary>A key was refused but the layer stays (LeaderInvalidTone).</summary>
        public Action? Invalid;

        /// <summary>The in-layer help spoke (LeaderHelpTone).</summary>
        public Action? Help;
    }

    /// <summary>
    /// Everything one value contributes to its sub-layer: how to read and
    /// write it, its range and steps, its two spoken forms, and its sentences.
    /// The ENGINE owns everything the pattern settles once — see
    /// <see cref="ValueSubLayer"/> — and a definition deliberately cannot
    /// override any of it.
    /// </summary>
    public sealed class ValueSubLayerDefinition
    {
        /// <summary>
        /// Short stable id ("pan"). Becomes the speech coalesce key
        /// ("valuelayer:pan") and the trace tag. Not spoken.
        /// </summary>
        public string Id = "";

        /// <summary>Reads the live value once, at entry, to seed the shadow.</summary>
        public Func<int>? Read;

        /// <summary>
        /// Writes a value to the radio. Called on every nudge and on cancel's
        /// restore — never on confirm, which keeps what is already applied and
        /// writes nothing. Any safety gate (#187's load-state rule) belongs
        /// inside this delegate, where it guards nudges and restore alike.
        /// </summary>
        public Action<int>? Apply;

        public int Min;
        public int Max = 100;

        /// <summary>Plain-arrow step.</summary>
        public int Step = 5;

        /// <summary>Shift-arrow step. Plain is coarse, modified is fine — the
        /// same one-sentence rule Modern tuning uses.</summary>
        public int FineStep = 1;

        /// <summary>Which arrow pair adjusts.</summary>
        public ValueLayerAxis Axis = ValueLayerAxis.UpDown;

        /// <summary>
        /// The value the operator returns to (pan: 50, centre). Home jumps
        /// here, plus any <see cref="AnchorKeys"/>. Null = no anchor; Home is
        /// then an unhandled key.
        /// </summary>
        public int? Anchor;

        /// <summary>Extra bare keys that jump to the anchor (pan: C).</summary>
        public Keys[] AnchorKeys = Array.Empty<Keys>();

        /// <summary>
        /// The words form — interpretable, for moving the value by ear
        /// ("slightly left"). Spoken at Chatty. Null = the number form serves
        /// every verbosity.
        /// </summary>
        public Func<int, string>? Words;

        /// <summary>
        /// The number form — precise and repeatable, for recreating a known
        /// arrangement ("Pan 40"). Spoken at Terse.
        /// </summary>
        public Func<int, string>? Number;

        /// <summary>Spoken once on entry: (current, entry) → sentence.</summary>
        public Func<int, int, string>? DescribeEntry;

        /// <summary>The in-layer help: (current, entry) → sentence.</summary>
        public Func<int, int, string>? DescribeHelp;

        /// <summary>The closed marker ("Pan mode closed").</summary>
        public Func<string>? DescribeClosed;

        /// <summary>
        /// Cancel's sentence: (restored value) → what came back and that the
        /// layer closed. Null falls back to <see cref="DescribeClosed"/>.
        /// </summary>
        public Func<int, string>? DescribeRestored;

        /// <summary>Spoken when the wrong arrow pair is pressed.</summary>
        public Func<string>? WrongAxisHint;

        /// <summary>Treat H as a help key too. Default true; a layer that
        /// claims H for something else (volume mode's headphone) sets false —
        /// Shift+slash stays help in every layer regardless (#158).</summary>
        public bool HelpOnH = true;

        /// <summary>
        /// Host whitelist for keys that must travel on while the layer stays
        /// live — the verbosity cycle chord, looked up from the live registry
        /// so remaps are honoured. Alt chords and F1 pass through without
        /// being asked.
        /// </summary>
        public Func<Keys, bool>? PassThroughKeys;

        public ValueLayerCues Cues = new ValueLayerCues();
    }

    /// <summary>
    /// The value sub-layer pattern (#305), extracted from its first real
    /// consumer, the pan layer (#304). One mechanism for every value an
    /// operator hunts for by ear: the layer stays live, arrows move the value
    /// and it speaks as it moves, and a deliberate key confirms or cancels.
    /// #187 (transmit power) and #200 (the knob) extend this; they must not
    /// re-decide anything below.
    ///
    /// <para><b>What the pattern settles ONCE — the #305 list:</b></para>
    ///
    /// <para><b>How you get out.</b> Enter confirms and closes. Escape cancels:
    /// the entry value is written back, out loud, and the layer closes. Ctrl+J
    /// confirms silently and hands off to a fresh leader chord. Any UNHANDLED
    /// key confirms, announces the close, and travels on to mean what it
    /// always means — the layer cannot strand anyone, because every key either
    /// works or leaves. Alt chords, F1 and the host's whitelist (the verbosity
    /// cycle) travel on with the layer still live, so the operator can flip
    /// words-versus-numbers mid-hunt. Confirm never writes; only cancel
    /// writes. (Volume mode predates this pattern and differs: its Escape
    /// keeps the adjustments and its unknown keys are swallowed with a hint.
    /// That divergence is named, not silently mirrored — migrating volume mode
    /// onto the pattern is follow-up work, not this file's.)</para>
    ///
    /// <para><b>Cancel restores.</b> The layer holds the entry value for as
    /// long as it is live. An operator who overshoots always has the way
    /// back, and it is always Escape.</para>
    ///
    /// <para><b>What it speaks while moving, and how often.</b> Every nudge
    /// speaks through the speech coalescer (#264, SpeechIntent.Latest with a
    /// per-layer key): a held arrow is the sweep case, the tail value wins,
    /// and repeatWhileHeld is set because "still at minimum" is how an
    /// operator learns the rail is reached.</para>
    ///
    /// <para><b>Words or numbers, under verbosity.</b> Ruled by Noel
    /// 2026-08-27 for pan, generalised here: at Chatty the layer speaks the
    /// words form (interpretable — moving it by ear), at Terse the number
    /// form (precise and repeatable — recreating a known arrangement). They
    /// do different jobs, which is why offering both is not redundant. The
    /// form is chosen at speak time, so cycling verbosity mid-layer switches
    /// forms immediately.</para>
    ///
    /// <para><b>The operator is told they are in it.</b> Entry and every
    /// closing path announce themselves, entry and close carry earcons, and
    /// the in-layer help (Shift+slash, plus H where free) re-speaks the
    /// current value, the entry value and the keys without changing
    /// anything — #200's "speakable on demand" requirement.</para>
    ///
    /// <para><b>If the value changes underneath</b> (another client, the front
    /// panel): two hands on one knob means last writer wins. The layer speaks
    /// what IT set — a shadow seeded at entry and stepped locally — never a
    /// read-back, because the radio setters apply asynchronously and a
    /// read-after-write announces the stale value (the volume-mode lesson).
    /// The next nudge moves from the shadow; the by-ear loop self-corrects.
    /// The layer never claims to display live state.</para>
    ///
    /// <para><b>Forced drop.</b> <see cref="Drop"/> closes with no write and
    /// no speech, for the PTT carve-out and a vanished radio. During transmit
    /// a restore write is the wrong side of every safety argument — #187
    /// especially — so a forced drop keeps, never restores.</para>
    ///
    /// <para><b>For #200:</b> the knob host skips <see cref="HandleKey"/> and
    /// drives the semantic surface directly — <see cref="Nudge"/>,
    /// <see cref="JumpToAnchor"/>, <see cref="Confirm"/>, <see cref="Cancel"/>,
    /// <see cref="SpeakHelp"/> — so hardware layers and keyboard layers share
    /// one set of decisions.</para>
    /// </summary>
    public sealed class ValueSubLayer
    {
        private readonly ValueSubLayerDefinition _def;
        private int _shadow;
        private bool _live;

        /// <summary>The value at entry — what Escape restores.</summary>
        public int EntryValue { get; }

        /// <summary>The layer's local shadow — what it last set (or seeded).</summary>
        public int CurrentValue => _shadow;

        /// <summary>False once any close path has run.</summary>
        public bool IsLive => _live;

        // ── Test seams. Production defaults emit through ScreenReaderOutput;
        //    tests replace these to read the exact operator-facing sentences
        //    without a speech backend. The POLICY (which form, which intent,
        //    which key) stays in this class either way.
        internal Action<string, string> EmitMove;   // (text, coalesceKey)
        internal Action<string> EmitSay;            // interrupting one-shot, Terse level
        internal Func<VerbosityLevel> VerbosityNow;

        private ValueSubLayer(ValueSubLayerDefinition def, int seed)
        {
            _def = def;
            _shadow = seed;
            EntryValue = seed;
            _live = true;

            // A value layer is the genuine SWEEP case — held-arrow nudges
            // settle to the tail — so this is Value, not Query. Query answers
            // immediately, which is right for a key that ASKS something and
            // wrong for a value being moved.
            //
            // MERGE RECORD (Sprint 37, Track C ← Track A): this was
            // repeatWhileHeld: true, which Track A removed in favour of the
            // kind. ONE BEHAVIOUR CHANGED and it is worth knowing at the bench:
            // repeatWhileHeld spoke an identical repeat, Value DROPS it. So
            // holding an arrow against a rail now announces the rail ONCE and
            // then goes quiet, rather than restating it. That is the same
            // answer the tuning-step ladder arrived at independently (#302),
            // and the anti-clip gap is what makes a held key a cadence rather
            // than a stutter either way. If a rail should keep speaking, the
            // fix is a rail-specific sentence, not a coalescing exemption.
            EmitMove = (text, key) => ScreenReaderOutput.Speak(
                text,
                Speech.SpeechIntent.Latest,
                VerbosityLevel.Terse,
                coalesceKey: key,
                kind: Speech.SpeechCoalesceKind.Value);
            EmitSay = text => ScreenReaderOutput.Speak(text, VerbosityLevel.Terse, true);
            VerbosityNow = () => ScreenReaderOutput.CurrentVerbosity;
        }

        /// <summary>
        /// Open a layer: seed the shadow from the live value, cue and announce.
        /// The definition must carry Read, Apply, Number and DescribeClosed;
        /// anything else is optional.
        /// </summary>
        public static ValueSubLayer Enter(ValueSubLayerDefinition def)
        {
            var layer = Seed(def);
            layer.AnnounceEntry();
            return layer;
        }

        /// <summary>
        /// Test entry: identical to <see cref="Enter"/> except the speech
        /// seams and the verbosity source are swapped BEFORE the entry
        /// announcement, so the exact entry sentence is capturable. The
        /// policy under test is the production policy — only the emitters
        /// differ.
        /// </summary>
        internal static ValueSubLayer EnterForTest(
            ValueSubLayerDefinition def,
            Action<string, string> emitMove,
            Action<string> emitSay,
            Func<VerbosityLevel> verbosityNow)
        {
            var layer = Seed(def);
            layer.EmitMove = emitMove;
            layer.EmitSay = emitSay;
            layer.VerbosityNow = verbosityNow;
            layer.AnnounceEntry();
            return layer;
        }

        private static ValueSubLayer Seed(ValueSubLayerDefinition def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            if (def.Read == null || def.Apply == null || def.Number == null || def.DescribeClosed == null)
                throw new ArgumentException("ValueSubLayer definition is missing Read, Apply, Number or DescribeClosed", nameof(def));

            int seed = Math.Clamp(def.Read(), def.Min, def.Max);
            return new ValueSubLayer(def, seed);
        }

        private void AnnounceEntry()
        {
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): entered at " + _shadow, TraceLevel.Info);
            _def.Cues.Entered?.Invoke();
            if (_def.DescribeEntry != null)
                EmitSay(_def.DescribeEntry(_shadow, EntryValue));
        }

        /// <summary>
        /// The keyboard face of the layer. Every decision here is the pattern;
        /// see the class remarks before changing any arm.
        /// </summary>
        public ValueLayerKeyResult HandleKey(Keys k)
        {
            if (!_live) return ValueLayerKeyResult.PassThrough;

            Keys code = k & Keys.KeyCode;

            // Bare modifiers never mean anything (the dispatcher filters them
            // too; this is belt and braces for non-keyboard hosts).
            if (code == Keys.None || code == Keys.Menu || code == Keys.ControlKey || code == Keys.ShiftKey)
                return ValueLayerKeyResult.PassThrough;

            // System chords travel on with the layer live: Alt combos
            // (Alt+F4, menu accelerators) and F1 help — the volume-mode rule.
            if ((k & Keys.Alt) != 0 || code == Keys.F1)
                return ValueLayerKeyResult.PassThrough;

            // Host whitelist — the verbosity cycle, looked up from the live
            // registry so a remap is honoured. Travels on, layer stays, and
            // the next nudge speaks in the other form.
            if (_def.PassThroughKeys != null && _def.PassThroughKeys(k))
                return ValueLayerKeyResult.PassThrough;

            // Ctrl+J hands off to a fresh leader chord: confirm silently, the
            // host arms and announces the leader.
            if (k == (Keys.J | Keys.Control))
            {
                Confirm(speak: false);
                return ValueLayerKeyResult.ClosedHandOff;
            }

            // Escape cancels — by keycode, so a stuck modifier cannot flip
            // "put it back" into "keep it". The one guaranteed exit.
            if (code == Keys.Escape)
            {
                Cancel(speak: true);
                return ValueLayerKeyResult.Closed;
            }

            // Enter confirms — keycode for the same reason.
            if (code == Keys.Return)
            {
                Confirm(speak: true);
                return ValueLayerKeyResult.Closed;
            }

            // Arrows are the layer's home turf: the right pair adjusts (Shift
            // = fine), and ANY other arrow press — wrong pair, wrong modifier
            // — hints and stays. An arrow must never eject the operator.
            if (code == Keys.Up || code == Keys.Down || code == Keys.Left || code == Keys.Right)
            {
                bool shiftOnly = (k & Keys.Modifiers) == Keys.Shift;
                bool plain = (k & Keys.Modifiers) == Keys.None;
                if (plain || shiftOnly)
                {
                    int direction = 0;
                    if (_def.Axis == ValueLayerAxis.LeftRight)
                    {
                        if (code == Keys.Right) direction = +1;
                        else if (code == Keys.Left) direction = -1;
                    }
                    else
                    {
                        if (code == Keys.Up) direction = +1;
                        else if (code == Keys.Down) direction = -1;
                    }
                    if (direction != 0)
                    {
                        Nudge(direction, fine: shiftOnly);
                        return ValueLayerKeyResult.Handled;
                    }
                }
                _def.Cues.Invalid?.Invoke();
                if (_def.WrongAxisHint != null) EmitSay(_def.WrongAxisHint());
                return ValueLayerKeyResult.Handled;
            }

            // Home — and any layer-declared anchor letter — jumps to the
            // anchor. Centre is one key away (#304).
            if (_def.Anchor.HasValue &&
                (code == Keys.Home && (k & Keys.Modifiers) == Keys.None
                 || Array.IndexOf(_def.AnchorKeys, k) >= 0))
            {
                JumpToAnchor();
                return ValueLayerKeyResult.Handled;
            }

            // Help: Shift+slash in every layer (#158), H where the layer has
            // not claimed it. Both Oem2 forms — the bare case alone never
            // fires for "?", the #183 lesson.
            if (code == Keys.Oem2 || (_def.HelpOnH && k == Keys.H))
            {
                SpeakHelp();
                return ValueLayerKeyResult.Handled;
            }

            // Everything else: keep the value, close out loud, and let the
            // key travel on to mean what it always means.
            Confirm(speak: true);
            return ValueLayerKeyResult.ClosedPassThrough;
        }

        /// <summary>
        /// Move the value one step and speak the new position — the semantic
        /// surface #200's knob drives directly. Clamps at the rails; the
        /// clamped announcement repeats on purpose, because "still at the
        /// rail" is how the operator learns to stop pressing.
        /// </summary>
        public void Nudge(int direction, bool fine)
        {
            if (!_live) return;
            int step = fine ? _def.FineStep : _def.Step;
            _shadow = Math.Clamp(_shadow + direction * step, _def.Min, _def.Max);
            _def.Apply!(_shadow);
            SpeakMove();
        }

        /// <summary>Jump to the anchor value (pan: centre) and speak it.</summary>
        public void JumpToAnchor()
        {
            if (!_live || !_def.Anchor.HasValue) return;
            _shadow = Math.Clamp(_def.Anchor.Value, _def.Min, _def.Max);
            _def.Apply!(_shadow);
            SpeakMove();
        }

        /// <summary>
        /// Keep the current value and close. Writes nothing — everything is
        /// already applied — which is what makes confirm safe on every path,
        /// including mid-transmit.
        /// </summary>
        public void Confirm(bool speak)
        {
            if (!_live) return;
            _live = false;
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): confirmed at " + _shadow, TraceLevel.Info);
            if (speak)
            {
                _def.Cues.Closed?.Invoke();
                EmitSay(_def.DescribeClosed!());
            }
        }

        /// <summary>
        /// Put the entry value back, out loud, and close. The only path that
        /// writes on the way out.
        /// </summary>
        public void Cancel(bool speak)
        {
            if (!_live) return;
            _live = false;
            _shadow = EntryValue;
            _def.Apply!(EntryValue);
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): cancelled, restored " + EntryValue, TraceLevel.Info);
            if (speak)
            {
                _def.Cues.Closed?.Invoke();
                string text = _def.DescribeRestored != null
                    ? _def.DescribeRestored(EntryValue)
                    : _def.DescribeClosed!();
                EmitSay(text);
            }
        }

        /// <summary>
        /// Close with no write and no speech: the radio went away, or PTT
        /// safety took Escape. A forced drop keeps, never restores — a
        /// restore is a write, and mid-transmit is no time to write.
        /// </summary>
        public void Drop()
        {
            if (!_live) return;
            _live = false;
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): dropped at " + _shadow, TraceLevel.Info);
        }

        /// <summary>
        /// Speak the current state and the keys without changing anything —
        /// the on-demand answer to "which control am I holding?" (#200).
        /// </summary>
        public void SpeakHelp()
        {
            if (!_live) return;
            _def.Cues.Help?.Invoke();
            if (_def.DescribeHelp != null)
                EmitSay(_def.DescribeHelp(_shadow, EntryValue));
        }

        /// <summary>
        /// The current value in the form the operator's verbosity asks for:
        /// words at Chatty and above (interpretable), the number at Terse
        /// (precise, repeatable). A layer with no words form gets numbers
        /// everywhere.
        /// </summary>
        public string FormValue() => FormOf(_shadow);

        /// <summary>The verbosity-chosen form of any value on this layer's scale.</summary>
        public string FormOf(int value)
        {
            if (_def.Words != null && (int)VerbosityNow() >= (int)VerbosityLevel.Chatty)
                return _def.Words(value);
            return _def.Number!(value);
        }

        private void SpeakMove()
        {
            EmitMove(FormValue(), "valuelayer:" + _def.Id);
        }
    }
}
