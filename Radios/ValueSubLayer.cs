using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The arrow pair a SINGLE-value layer adjusts with: left/right for a
    /// spatial value, up/down for a magnitude. The single-value façade of
    /// <see cref="ValueSubLayerDefinition"/> uses this; multi-target layers
    /// declare <see cref="ValueLayerAxes"/> per target instead.
    /// </summary>
    public enum ValueLayerAxis
    {
        LeftRight,
        UpDown,
    }

    /// <summary>
    /// Which arrow pairs adjust one target. A target that accepts both is
    /// the audio layer's pan — Up/Down adjusts everything in that layer, and
    /// Left/Right ALSO adjusts pan because for that one target direction
    /// means something real (#514). The other pair is never silently dead:
    /// it speaks the target's wrong-axis hint.
    /// </summary>
    [Flags]
    public enum ValueLayerAxes
    {
        None = 0,
        LeftRight = 1,
        UpDown = 2,
        Both = LeftRight | UpDown,
    }

    /// <summary>
    /// Which physical Shift key is down. <c>System.Windows.Forms.Keys</c>
    /// carries one Shift bit and no side; the filter layer (#516) needs the
    /// side — Left Shift grabs the low edge, Right Shift the high edge — so
    /// the host supplies a probe (<see cref="ValueSubLayerDefinition.ShiftSideNow"/>)
    /// that the engine asks at the instant of the press.
    /// </summary>
    public enum ShiftSide
    {
        None,
        Left,
        Right,
        Both,
    }

    /// <summary>
    /// How a layer decides WHICH target a press adjusts. One layer, one
    /// policy; the policy is the whole difference between the consumers.
    /// </summary>
    public enum ValueLayerSelection
    {
        /// <summary>
        /// One value; the definition's own Read/Apply/Axis fields describe it.
        /// Pan was this before it joined the audio layer; #187's transmit
        /// power is this.
        /// </summary>
        Single,

        /// <summary>
        /// Letters pick the target (<see cref="ValueTarget.SelectKey"/>);
        /// Up/Down adjusts the picked one, and Left/Right adjusts it too when
        /// it declares that axis. The audio layer (#514).
        /// </summary>
        ByLetter,

        /// <summary>
        /// Left/Right step through the targets in order and Up/Down adjust —
        /// the TWO-AXIS form Noel described for the equalisers on 2026-09-01:
        /// "right and left arrow would go from setting to setting". A target
        /// is always selected; the first one at entry.
        /// </summary>
        ByLeftRight,

        /// <summary>
        /// Nothing stays selected. Each press names its own target by the
        /// modifier held at that instant — which Shift, whether Ctrl — and
        /// the key itself is the verb. The filter layer (#516): Left Shift
        /// means the low edge, Right Shift the high edge, no modifier the
        /// whole filter.
        /// </summary>
        ByModifier,
    }

    /// <summary>
    /// Where a jump key PLACES a value, rather than hunting for it with the
    /// arrows (#522). Home is the minimum, End the maximum, 0 the centre —
    /// and on a target with no centre declared, zero. The same three keys on
    /// every target in every layer: <c>Home</c> and <c>End</c> already mean
    /// minimum and maximum on every range control in Windows, including
    /// every slider in this app, so spending them on a bespoke meaning
    /// inside one layer would charge the operator for knowledge they
    /// already have.
    /// </summary>
    public enum ValueLayerJump
    {
        /// <summary>Hard left: <see cref="ValueTarget.Min"/>, or whatever
        /// <see cref="ValueTarget.Constrain"/> allows below it.</summary>
        Minimum,

        /// <summary>Hard right: <see cref="ValueTarget.Max"/>, constrained.</summary>
        Maximum,

        /// <summary>The value the operator returns to:
        /// <see cref="ValueTarget.Anchor"/>, or zero where none is declared.</summary>
        Centre,
    }

    /// <summary>Why a layer closed — for the host's exit hook.</summary>
    public enum ValueLayerExit
    {
        Confirmed,
        Cancelled,
        Dropped,
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
        /// there is no key that can strand an operator here, because every
        /// key either works or leaves.
        /// </summary>
        ClosedPassThrough,

        /// <summary>
        /// Ctrl+J inside the layer: confirmed and closed silently; the host
        /// should arm a fresh leader chord.
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

        /// <summary>The in-layer help spoke or opened (LeaderHelpTone).</summary>
        public Action? Help;

        /// <summary>
        /// A nudge could not move the target — the rail. Optional; a layer
        /// whose rails are audible by number alone leaves it null.
        /// </summary>
        public Action? Rail;
    }

    /// <summary>
    /// One adjustable thing inside a layer: how to read and write it, its
    /// range and steps, which keys reach it, and its spoken forms. A layer
    /// is a list of these plus a <see cref="ValueLayerSelection"/> policy.
    /// </summary>
    public sealed class ValueTarget
    {
        /// <summary>Short stable id ("pan", "low-edge"). Part of the speech
        /// subject and the trace tag. Not spoken.</summary>
        public string Id = "";

        /// <summary>The spoken name, for selection announcements and hints
        /// when no <see cref="DescribeSelected"/> is given ("Headphone").</summary>
        public string Name = "";

        /// <summary>
        /// The exact chord that selects this target in a
        /// <see cref="ValueLayerSelection.ByLetter"/> layer — a plain letter,
        /// or Ctrl+letter where the plain letter is already spoken for
        /// (headphone is Ctrl+H because plain H is help, #514). Keys.None for
        /// layers that select another way.
        /// </summary>
        public Keys SelectKey = Keys.None;

        /// <summary>
        /// The exact chord that FLIPS this target rather than picking it —
        /// the Ctrl tier of the four-tier grammar (#515): a plain letter
        /// picks a level, Ctrl+letter toggles a switch. A toggle holds
        /// <see cref="Min"/> (off) or <see cref="Max"/> (on), is never the
        /// current target — the pick is untouched and the arrows still move
        /// what was picked — and joins the touched list like any other
        /// target, so Escape puts it back and the restore sentence names it
        /// (#524: slice mute, PC audio, binaural). Keys.None for a target
        /// that is adjusted rather than switched.
        /// </summary>
        public Keys ToggleKey = Keys.None;

        /// <summary>Reached by <see cref="ToggleKey"/>, never picked and nudged.</summary>
        public bool IsToggle => ToggleKey != Keys.None;

        /// <summary>Reads the live value, to seed the shadow when the target is
        /// first reached — and before every step when <see cref="Linked"/>.</summary>
        public Func<int>? Read;

        /// <summary>
        /// Writes a value. Called on every nudge that moves, and on cancel's
        /// restore — never on confirm. Any safety gate belongs inside this
        /// delegate, where it guards nudges and restore alike.
        /// </summary>
        public Action<int>? Apply;

        public int Min;
        public int Max = 100;

        /// <summary>Plain-arrow step.</summary>
        public int Step = 5;

        /// <summary>
        /// The step evaluated at each press, when it depends on state
        /// (the receive filter steps by its current width). Null = <see cref="Step"/>.
        /// </summary>
        public Func<int>? StepNow;

        /// <summary>Shift-arrow step. Plain is coarse, modified is fine — the
        /// same one-sentence rule Modern tuning uses. Unreachable in a
        /// <see cref="ValueLayerSelection.ByModifier"/> layer, where Shift is
        /// the selector; those layers step explicitly, on purpose (#516).</summary>
        public int FineStep = 1;

        /// <summary>Which arrow pairs adjust this target.</summary>
        public ValueLayerAxes Axes = ValueLayerAxes.UpDown;

        /// <summary>
        /// <see cref="ValueLayerSelection.ByModifier"/> only: the Shift key
        /// that addresses this target. Left for a low edge, Right for a high
        /// edge, None for the whole.
        /// </summary>
        public ShiftSide Shift = ShiftSide.None;

        /// <summary>
        /// <see cref="ValueLayerSelection.ByModifier"/> only: Ctrl addresses
        /// this target (width, about the centre).
        /// </summary>
        public bool Ctrl;

        /// <summary>
        /// This target is a VIEW over state the host holds — one of several
        /// coordinates on a compound value, like the edges, position and
        /// width of one filter. The engine re-reads it before every step, so
        /// moving one coordinate cannot leave another's shadow stale. The
        /// host's Read must return the host's own shadow and never a radio
        /// read-back, which is exactly what the engine's honesty rule
        /// forbids; and a layer with any linked target must supply
        /// <see cref="ValueSubLayerDefinition.Snapshot"/>, because restoring
        /// coordinates one at a time is nonsense.
        /// </summary>
        public bool Linked;

        /// <summary>
        /// The rail, when <see cref="Min"/> and <see cref="Max"/> cannot
        /// express it — a transmit edge is constrained against the OTHER
        /// edge. Runs after the clamp; returns the value that may actually be
        /// applied. A step that comes back unchanged is a rail hit and speaks
        /// <see cref="DescribeRail"/>.
        /// </summary>
        public Func<int, int>? Constrain;

        /// <summary>
        /// The value the operator returns to (pan: centre). <c>0</c> jumps
        /// here, plus any <see cref="AnchorKeys"/>. Null = zero, which is the
        /// centre of an unsigned target anyway (#522). Home and End need no
        /// declaration: they are <see cref="Min"/> and <see cref="Max"/>.
        /// </summary>
        public int? Anchor;

        /// <summary>Extra bare keys that jump to the anchor, beside <c>0</c>.</summary>
        public Keys[] AnchorKeys = Array.Empty<Keys>();

        /// <summary>The words form — interpretable, spoken at Chatty. Null =
        /// the number form serves every verbosity.</summary>
        public Func<int, string>? Words;

        /// <summary>The number form — precise and repeatable, spoken at Terse.</summary>
        public Func<int, string>? Number;

        /// <summary>
        /// The sentence spoken when this target is selected or asked about:
        /// (value) → "On-radio headphone 40". Null = the name and the
        /// verbosity-chosen form.
        /// </summary>
        public Func<int, string>? DescribeSelected;

        /// <summary>
        /// Spoken instead of the form when a step could not move the target:
        /// (value) → "Low edge 2650, at the limit". Null = the form repeats,
        /// which the coalescer states once and then drops (the settled rail
        /// behaviour). Without sight, a control that silently refuses to move
        /// is indistinguishable from a broken one (#516).
        /// </summary>
        public Func<int, string>? DescribeRail;

        /// <summary>A suffix appended to the selection sentence, evaluated at
        /// the time (", compander is off"). Null = nothing.</summary>
        public Func<string>? Note;

        /// <summary>Spoken when the wrong arrow pair is pressed on this
        /// target. Null = the definition's hint.</summary>
        public Func<string>? WrongAxisHint;

        /// <summary>
        /// Group this target belongs to, for layers with
        /// <see cref="ValueSubLayerDefinition.GroupKeys"/> (the filter
        /// layer's receive and transmit sides). Only the active group's
        /// targets answer a press. Null = every group.
        /// </summary>
        public string? Group;

        /// <summary>
        /// Bound to the active slice. Purely descriptive to the engine; the
        /// host re-binds these after a slice jump with
        /// <see cref="ValueSubLayer.Rebind"/>, and the inventory says so.
        /// </summary>
        public bool PerSlice;
    }

    /// <summary>One target put back by cancel, and to what.</summary>
    public readonly record struct ValueTargetRestore(ValueTarget Target, int RestoredTo);

    /// <summary>
    /// Everything a layer contributes to the engine: its targets (or, for a
    /// single value, that value), how targets are chosen, its sentences and
    /// its host hooks. The ENGINE owns everything the pattern settles once —
    /// see <see cref="ValueSubLayer"/> — and a definition deliberately cannot
    /// override any of it.
    /// </summary>
    public sealed class ValueSubLayerDefinition
    {
        /// <summary>
        /// Short stable id ("audio", "filter", "pan"). Becomes the speech
        /// subject and the trace tag. Not spoken.
        /// </summary>
        public string Id = "";

        /// <summary>How targets are chosen. Default Single.</summary>
        public ValueLayerSelection Selection = ValueLayerSelection.Single;

        /// <summary>The targets of a multi-target layer, in presentation order.</summary>
        public List<ValueTarget> Targets = new List<ValueTarget>();

        /// <summary>
        /// Index of the target selected at entry: ByLeftRight defaults to 0
        /// (a target is always selected), ByLetter to -1 (none until a letter
        /// is pressed) unless the door pre-selects one — Ctrl+J, Alt+P opens
        /// the audio layer on pan.
        /// </summary>
        public int InitialTarget = -1;

        // ── Single-value façade. Used when Selection is Single and Targets
        //    is empty: the engine builds one target from these. ──

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

        /// <summary>Shift-arrow step.</summary>
        public int FineStep = 1;

        /// <summary>Which arrow pair adjusts the single value.</summary>
        public ValueLayerAxis Axis = ValueLayerAxis.UpDown;

        /// <summary>The single value's anchor, which <c>0</c> jumps to. Null
        /// = zero (#522).</summary>
        public int? Anchor;

        /// <summary>Extra bare keys that jump to the anchor, beside <c>0</c>.</summary>
        public Keys[] AnchorKeys = Array.Empty<Keys>();

        /// <summary>The words form of the single value (Chatty).</summary>
        public Func<int, string>? Words;

        /// <summary>The number form of the single value (Terse).</summary>
        public Func<int, string>? Number;

        /// <summary>Single value: spoken once on entry, (current, entry) → sentence.</summary>
        public Func<int, int, string>? DescribeEntry;

        /// <summary>Single value: the in-layer help, (current, entry) → sentence.</summary>
        public Func<int, int, string>? DescribeHelp;

        /// <summary>
        /// Single value: cancel's sentence, (restored value) → what came back
        /// and that the layer closed. Null falls back to <see cref="DescribeClosed"/>.
        /// </summary>
        public Func<int, string>? DescribeRestored;

        // ── Sentences every layer needs ──

        /// <summary>The closed marker ("Audio layer closed"). Required.</summary>
        public Func<string>? DescribeClosed;

        /// <summary>Spoken when the wrong arrow pair is pressed and the target
        /// has no hint of its own.</summary>
        public Func<string>? WrongAxisHint;

        // ── Multi-target sentences. Each receives the live layer so the host
        //    can read the current target, its value and its entry value. ──

        /// <summary>Multi-target: spoken once on entry.</summary>
        public Func<ValueSubLayer, string>? DescribeLayerEntry;

        /// <summary>Multi-target: the in-layer help, spoken when no navigable
        /// surface is available. Say the count first (#519).</summary>
        public Func<ValueSubLayer, string>? DescribeLayerHelp;

        /// <summary>
        /// Multi-target: cancel's sentence, given what was put back (possibly
        /// nothing). Null falls back to <see cref="DescribeClosed"/>.
        /// </summary>
        public Func<ValueSubLayer, IReadOnlyList<ValueTargetRestore>, string>? DescribeLayerRestored;

        /// <summary>ByLetter: spoken when an arrow is pressed before any
        /// target is picked.</summary>
        public Func<string>? PickTargetHint;

        /// <summary>
        /// ByModifier: spoken when the Shift bit arrived but no single side
        /// could be read — both Shifts down, or a Shift that a screen reader
        /// released between its synthesised pairs. The engine REFUSES rather
        /// than guessing an edge: moving the wrong edge silently is the
        /// failure this hint exists to make audible.
        /// </summary>
        public Func<string>? WhichShiftHint;

        /// <summary>ByModifier: spoken when the modifier and key name no
        /// target (Left Shift with Up). Null = <see cref="WrongAxisHint"/>.</summary>
        public Func<string>? NoVerbHint;

        // ── Groups (the filter layer's receive and transmit sides) ──

        /// <summary>Exact chord → group id. T for transmit, R for receive.</summary>
        public Dictionary<Keys, string> GroupKeys = new Dictionary<Keys, string>();

        /// <summary>The group active at entry. The filter layer lands on receive (#516).</summary>
        public string? InitialGroup;

        /// <summary>Spoken when a group is switched to, or re-asked for: (group) → sentence.</summary>
        public Func<string, string>? DescribeGroup;

        // ── Verbs ──

        /// <summary>
        /// ByModifier: the key that speaks the addressed target without
        /// moving it (S in the filter layer). A speak key is a QUESTION, not a
        /// sweep — it answers now, and a re-press answers again (#264).
        /// </summary>
        public Keys SpeakKey = Keys.None;

        // ── Host hooks ──

        /// <summary>
        /// Which Shift is physically down right now. Required by ByModifier;
        /// the host reads it at the instant of the press and never tracks a
        /// hold (the JAWS held-key divergence).
        /// </summary>
        public Func<ShiftSide>? ShiftSideNow;

        /// <summary>
        /// Keys the HOST handles while the layer stays live, consumed by the
        /// engine when the host returns true. The universal slice jump lives
        /// here: Shift+letter inside a layer jumps to that slice (#515), and a
        /// layer never invents its own slice-selection mechanism.
        /// </summary>
        public Func<Keys, bool>? HostKeys;

        /// <summary>
        /// Host whitelist for keys that must travel on while the layer stays
        /// live — the verbosity cycle chord, looked up from the live registry
        /// so remaps are honoured. Alt chords and F1 pass through without
        /// being asked.
        /// </summary>
        public Func<Keys, bool>? PassThroughKeys;

        /// <summary>
        /// H: show this layer's commands as a NAVIGABLE LIST (#519). Returns
        /// true when a surface was shown; false or null falls back to the
        /// spoken help, count first.
        /// </summary>
        public Func<bool>? ListCommands;

        /// <summary>
        /// Shift+slash: open the JJ key tree explorer on this layer (#519).
        /// Returns true when a surface was shown; false or null falls back to
        /// the spoken help, so Shift+slash stays help in every layer (#158).
        /// </summary>
        public Func<bool>? OpenExplorer;

        /// <summary>
        /// Called once at entry; returns the action that puts everything this
        /// layer can touch back the way it was. When supplied, cancel calls
        /// it instead of restoring targets one by one. Required when any
        /// target is <see cref="ValueTarget.Linked"/>.
        /// </summary>
        public Func<Action>? Snapshot;

        /// <summary>
        /// Fires on EVERY close, spoken or silent, including a forced drop —
        /// the host's chance to persist an app-level setting the layer moved
        /// (the PC output volume), which is not a radio write and is safe on
        /// every path.
        /// </summary>
        public Action<ValueLayerExit>? Exited;

        public ValueLayerCues Cues = new ValueLayerCues();
    }

    /// <summary>
    /// The value sub-layer pattern (#305), extracted from its first real
    /// consumer, pan (#304), and extended in Sprint 44 Track I to hold MANY
    /// targets under one contract: the audio layer (#514, pan and volume
    /// merged), the filter layer (#516), and the two-axis form the equalisers
    /// wanted. One mechanism for every value an operator hunts for by ear:
    /// the layer stays live, arrows move the value and it speaks as it
    /// moves, and a deliberate key confirms or cancels. #187 (transmit power)
    /// and #200 (the knob) extend this; they must not re-decide anything
    /// below.
    ///
    /// <para><b>What the pattern settles ONCE — the #305 list:</b></para>
    ///
    /// <para><b>How you get out.</b> Enter confirms and closes. Escape cancels:
    /// everything the layer moved is written back, out loud, and the layer
    /// closes. Ctrl+J confirms silently and hands off to a fresh leader
    /// chord. Any UNHANDLED key confirms, announces the close, and travels on
    /// to mean what it always means — the layer cannot strand anyone, because
    /// every key either works or leaves. Alt chords, F1 and the host's
    /// whitelist (the verbosity cycle) travel on with the layer still live,
    /// so the operator can flip words-versus-numbers mid-hunt. Confirm never
    /// writes; only cancel writes. (Volume mode predated this pattern and
    /// differed — its Escape kept the adjustments and its unknown keys were
    /// swallowed with a hint. That divergence was named as debt in #305 and
    /// is closed: volume is now the audio layer, on this contract.)</para>
    ///
    /// <para><b>Cancel restores.</b> The layer holds each touched target's
    /// entry value for as long as it is live. An operator who overshoots
    /// always has the way back, and it is always Escape. A layer whose
    /// targets are coordinates on one compound value restores through one
    /// snapshot, because coordinates put back one at a time are nonsense.</para>
    ///
    /// <para><b>What it speaks while moving, and how often.</b> Every nudge
    /// speaks through the speech coalescer (#264, SpeechIntent.Latest, keyed
    /// per target): a held arrow is the sweep case and the tail value wins.
    /// Selecting a target, or asking about one, is a QUESTION and answers
    /// now. A rail is stated once and then dropped; a target with a rail
    /// sentence of its own says why it stopped.</para>
    ///
    /// <para><b>Words or numbers, under verbosity.</b> Ruled by Noel
    /// 2026-08-27 for pan, generalised here per target: at Chatty the words
    /// form (interpretable — moving it by ear), at Terse the number form
    /// (precise and repeatable — recreating a known arrangement). The form is
    /// chosen at speak time, so cycling verbosity mid-layer switches forms
    /// immediately.</para>
    ///
    /// <para><b>The operator is told they are in it.</b> Entry and every
    /// closing path announce themselves, entry and close carry earcons, and
    /// the in-layer help — plain H in every layer, Shift+slash too — lists
    /// the keys, count first, without changing anything. Where the host has
    /// a navigable surface (#519) H opens it; otherwise the list is spoken.
    /// The help cue plays BEFORE the surface opens: the surface is modal and
    /// a cue after it closes is a cue for nothing.</para>
    ///
    /// <para><b>A switch is one press.</b> A target with a
    /// <see cref="ValueTarget.ToggleKey"/> flips on that exact chord — the
    /// Ctrl tier of the four-tier grammar (#515) — says its new state, and
    /// leaves the pick alone. It is restored by Escape like anything else
    /// the layer moved, so "everything you moved" stays true (#524).</para>
    ///
    /// <para><b>If the value changes underneath</b> (another client, the front
    /// panel): two hands on one knob means last writer wins. The layer speaks
    /// what IT set — a shadow seeded when the target is first reached and
    /// stepped locally — never a read-back, because the radio setters apply
    /// asynchronously and a read-after-write announces the stale value. The
    /// next nudge moves from the shadow; the by-ear loop self-corrects.</para>
    ///
    /// <para><b>Forced drop.</b> <see cref="Drop"/> closes with no write and
    /// no speech, for the PTT carve-out and a vanished radio. During transmit
    /// a restore write is the wrong side of every safety argument — #187
    /// especially — so a forced drop keeps, never restores.</para>
    ///
    /// <para><b>Placing a value rather than hunting for it.</b> Home is hard
    /// left, End is hard right, and 0 is the centre — the same three keys on
    /// every target, addressed exactly as the arrows address one, so the
    /// filter layer's Left Shift still names the low edge (#522). They are
    /// what Windows already spends those keys on, everywhere else the
    /// operator goes. There is deliberately NO numpad binding: both NVDA and
    /// JAWS claim the pad in their default desktop layouts, and a key
    /// reference that lies to most of its readers is worse than a missing
    /// row.</para>
    ///
    /// <para><b>For #200:</b> the knob host skips <see cref="HandleKey"/> and
    /// drives the semantic surface directly — <see cref="Nudge"/>,
    /// <see cref="Select"/>, <see cref="SelectNext"/>, <see cref="JumpTo(ValueLayerJump)"/>,
    /// <see cref="Confirm"/>, <see cref="Cancel"/>, <see cref="SpeakHelp"/> —
    /// so hardware layers and keyboard layers share one set of decisions.</para>
    /// </summary>
    public sealed class ValueSubLayer
    {
        private sealed class Slot
        {
            public ValueTarget Def = null!;
            public int Shadow;
            public int Entry;
            public bool Seeded;
            public bool Touched;
        }

        private readonly ValueSubLayerDefinition _def;
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly List<Slot> _touchOrder = new List<Slot>();
        private readonly Action? _restore;
        private Slot? _current;
        private string? _group;
        private bool _live;

        /// <summary>The value at entry — what Escape restores. Single value:
        /// the one target; otherwise the current target, or 0.</summary>
        public int EntryValue => _current?.Entry ?? 0;

        /// <summary>The layer's local shadow of the current target — what it
        /// last set (or seeded).</summary>
        public int CurrentValue => _current?.Shadow ?? 0;

        /// <summary>False once any close path has run.</summary>
        public bool IsLive => _live;

        /// <summary>The targets, in presentation order.</summary>
        public IReadOnlyList<ValueTarget> Targets => _slots.Select(s => s.Def).ToList();

        /// <summary>The selected target, or null when none is (a ByLetter
        /// layer before a letter, every ByModifier layer).</summary>
        public ValueTarget? CurrentTarget => _current?.Def;

        /// <summary>The active group, or null for a layer without groups.</summary>
        public string? CurrentGroup => _group;

        /// <summary>Every target moved since entry (or since its last rebind), in touch order.</summary>
        public IReadOnlyList<ValueTarget> TouchedTargets => _touchOrder.Select(s => s.Def).ToList();

        // ── Test seams. Production defaults emit through ScreenReaderOutput;
        //    tests replace these to read the exact operator-facing sentences
        //    without a speech backend. The POLICY (which form, which intent,
        //    which key) stays in this class either way.
        internal Action<string, string> EmitMove;    // (text, subject) — a sweep, tail wins
        internal Action<string, string> EmitAnswer;  // (text, subject) — a question, answers now
        internal Action<string> EmitSay;             // interrupting one-shot on the layer's status subject
        internal Func<VerbosityLevel> VerbosityNow;

        private ValueSubLayer(ValueSubLayerDefinition def)
        {
            _def = def;
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
            // fix is a rail-specific sentence (ValueTarget.DescribeRail), not
            // a coalescing exemption.
            EmitMove = (text, subject) => ScreenReaderOutput.Speak(
                text,
                Speech.SpeechIntent.Latest,
                VerbosityLevel.Terse,
                coalesceKey: subject,
                kind: Speech.SpeechCoalesceKind.Value,
                subject: subject);
            EmitAnswer = (text, subject) => ScreenReaderOutput.Speak(
                text,
                Speech.SpeechIntent.Latest,
                VerbosityLevel.Terse,
                coalesceKey: subject,
                kind: Speech.SpeechCoalesceKind.Query,
                subject: subject);
            EmitSay = text => ScreenReaderOutput.Speak(
                text,
                Speech.SpeechIntent.Interrupt,
                VerbosityLevel.Terse,
                subject: Speech.SpeechSubject.ValueLayerStatus(_def.Id));
            VerbosityNow = () => ScreenReaderOutput.CurrentVerbosity;

            _restore = def.Snapshot?.Invoke();
        }

        /// <summary>
        /// Open a layer: seed, cue and announce. A single-value definition
        /// must carry Read, Apply, Number and DescribeClosed; a multi-target
        /// one, at least one target with Read, Apply and Number, plus
        /// DescribeClosed. Anything else is optional.
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
            Action<string, string> emitAnswer,
            Action<string> emitSay,
            Func<VerbosityLevel> verbosityNow)
        {
            var layer = Seed(def);
            layer.EmitMove = emitMove;
            layer.EmitAnswer = emitAnswer;
            layer.EmitSay = emitSay;
            layer.VerbosityNow = verbosityNow;
            layer.AnnounceEntry();
            return layer;
        }

        private static ValueSubLayer Seed(ValueSubLayerDefinition def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            if (def.DescribeClosed == null)
                throw new ArgumentException("ValueSubLayer definition is missing DescribeClosed", nameof(def));

            var layer = new ValueSubLayer(def);

            if (def.Selection == ValueLayerSelection.Single && def.Targets.Count == 0)
            {
                if (def.Read == null || def.Apply == null || def.Number == null)
                    throw new ArgumentException("ValueSubLayer definition is missing Read, Apply or Number", nameof(def));
                layer._slots.Add(new Slot
                {
                    Def = new ValueTarget
                    {
                        Id = "",
                        Read = def.Read,
                        Apply = def.Apply,
                        Min = def.Min,
                        Max = def.Max,
                        Step = def.Step,
                        FineStep = def.FineStep,
                        Axes = def.Axis == ValueLayerAxis.LeftRight ? ValueLayerAxes.LeftRight : ValueLayerAxes.UpDown,
                        Anchor = def.Anchor,
                        AnchorKeys = def.AnchorKeys,
                        Words = def.Words,
                        Number = def.Number,
                        WrongAxisHint = def.WrongAxisHint,
                    },
                });
            }
            else
            {
                if (def.Targets.Count == 0)
                    throw new ArgumentException("ValueSubLayer definition has no targets", nameof(def));
                foreach (var t in def.Targets)
                {
                    if (t.Read == null || t.Apply == null || t.Number == null)
                        throw new ArgumentException("ValueSubLayer target '" + t.Id + "' is missing Read, Apply or Number", nameof(def));
                    if (t.Linked && def.Snapshot == null)
                        throw new ArgumentException("ValueSubLayer target '" + t.Id + "' is linked but the definition has no Snapshot to restore from", nameof(def));
                    layer._slots.Add(new Slot { Def = t });
                }
                if (def.Selection == ValueLayerSelection.ByModifier && def.ShiftSideNow == null)
                    throw new ArgumentException("A ByModifier ValueSubLayer needs a ShiftSideNow probe", nameof(def));
            }

            layer._group = def.InitialGroup;

            int initial = def.Selection switch
            {
                ValueLayerSelection.Single => 0,
                ValueLayerSelection.ByLeftRight => Math.Max(0, def.InitialTarget),
                ValueLayerSelection.ByLetter => def.InitialTarget,
                _ => -1,
            };
            if (initial >= 0 && initial < layer._slots.Count)
            {
                layer._current = layer._slots[initial];
                layer.EnsureSeeded(layer._current);
            }
            return layer;
        }

        private void AnnounceEntry()
        {
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): entered"
                + (_current != null ? " on " + Tag(_current) + " at " + _current.Shadow : "")
                + (_group != null ? " group " + _group : ""), TraceLevel.Info);
            _def.Cues.Entered?.Invoke();
            if (_def.DescribeLayerEntry != null)
                EmitSay(_def.DescribeLayerEntry(this));
            else if (_def.DescribeEntry != null && _current != null)
                EmitSay(_def.DescribeEntry(_current.Shadow, _current.Entry));
        }

        // ────────────────────────────────────────────────────────────────
        //  The keyboard face
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The keyboard face of the layer. Every decision here is the pattern;
        /// see the class remarks before changing any arm.
        /// </summary>
        public ValueLayerKeyResult HandleKey(Keys k)
        {
            if (!_live) return ValueLayerKeyResult.PassThrough;

            Keys code = k & Keys.KeyCode;
            Keys mods = k & Keys.Modifiers;

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

            // Host-handled keys with the layer live: the universal slice jump
            // (#515). The host re-binds per-slice targets afterwards.
            if (_def.HostKeys != null && _def.HostKeys(k))
                return ValueLayerKeyResult.Handled;

            // Group switch (T / R in the filter layer). Re-pressing the active
            // group's key re-asks for it — a question, answered again.
            if (_def.GroupKeys.TryGetValue(k, out string? group))
            {
                SwitchGroup(group);
                return ValueLayerKeyResult.Handled;
            }

            // Arrows are the layer's home turf. ANY arrow press that names no
            // target hints and stays — an arrow must never eject the operator.
            if (code == Keys.Up || code == Keys.Down || code == Keys.Left || code == Keys.Right)
            {
                HandleArrow(code, mods);
                return ValueLayerKeyResult.Handled;
            }

            // Home, End and 0 PLACE a value instead of hunting for it (#522).
            // Home is hard left / minimum, End is hard right / maximum, 0 is
            // the centre — or zero, on a target that declares no centre. The
            // same three keys on EVERY target, never special-cased to pan,
            // and addressed exactly the way the arrows address one, so the
            // filter layer's Left Shift still names the low edge and
            // Left Shift+Home slams it to its rail.
            //
            // Home used to be the anchor here, which was right while pan was
            // its own mode and Home had no competing meaning. Six of the
            // audio layer's seven targets are ordinary ranges on which
            // Windows already spends Home and End, so the layer was leaving
            // knowledge the operator already had unused, and charging them
            // for a bespoke one instead. NO numpad binding, deliberately:
            // both NVDA and JAWS own the pad in their default desktop
            // layouts (numpad 5 is NVDA's read-current-word, measured at the
            // keyboard), so a Key.Clear row in the key reference would lie to
            // most of the people reading it.
            if (code == Keys.Home || code == Keys.End || code == Keys.D0)
            {
                ValueLayerJump where = code == Keys.Home ? ValueLayerJump.Minimum
                    : code == Keys.End ? ValueLayerJump.Maximum
                    : ValueLayerJump.Centre;

                if (_def.Selection == ValueLayerSelection.ByModifier)
                {
                    // No axis: Home is not a direction, and every target has
                    // a minimum whichever arrow pair moves it.
                    var addressed = AddressedSlot(mods, axis: null);
                    if (addressed != null) JumpSlot(addressed, where);
                    return ValueLayerKeyResult.Handled;
                }
                if (mods == Keys.None)
                {
                    if (_current != null) JumpSlot(_current, where);
                    else Refuse(_def.PickTargetHint?.Invoke());
                    return ValueLayerKeyResult.Handled;
                }
            }

            // Any anchor letter the current target declares — a second bare
            // key onto the centre 0 already reaches.
            if (_current != null && Array.IndexOf(_current.Def.AnchorKeys, k) >= 0)
            {
                JumpSlot(_current, ValueLayerJump.Centre);
                return ValueLayerKeyResult.Handled;
            }

            // The speak verb (ByModifier): which target is a question of
            // which Shift is down right now.
            if (_def.SpeakKey != Keys.None && code == (_def.SpeakKey & Keys.KeyCode)
                && (mods & Keys.Control) == 0)
            {
                SpeakAddressed(mods);
                return ValueLayerKeyResult.Handled;
            }

            // Letter selection (ByLetter): the exact chord, so Ctrl+H picks
            // headphone while plain H is still help.
            if (_def.Selection == ValueLayerSelection.ByLetter)
            {
                var pick = _slots.FirstOrDefault(s => s.Def.SelectKey != Keys.None && s.Def.SelectKey == k);
                if (pick != null)
                {
                    SelectSlot(pick, announce: true);
                    return ValueLayerKeyResult.Handled;
                }
            }

            // Toggles: the exact chord flips the target and says so, and the
            // pick is untouched — Ctrl is the toggle tier (#515), so a switch
            // inside a layer is one press, never "pick it, then arrow".
            var flip = _slots.FirstOrDefault(s => InGroup(s) && s.Def.IsToggle && s.Def.ToggleKey == k);
            if (flip != null)
            {
                ToggleSlot(flip);
                return ValueLayerKeyResult.Handled;
            }

            // Help: plain H lists this layer's commands, as a navigable list
            // where the host has one (#519) and spoken otherwise. Shift+slash
            // opens the tree explorer where the host has one and is help
            // otherwise (#158) — both Oem2 forms, because the bare case alone
            // never fires for "?", the #183 lesson. The cue plays FIRST: the
            // host's surface is modal, and a cue sounded after it closes is
            // the cue for nothing — an operator has already arrowed through
            // the list by then (#524).
            if (k == Keys.H)
            {
                _def.Cues.Help?.Invoke();
                if (_def.ListCommands?.Invoke() != true) SpeakHelpSentence();
                return ValueLayerKeyResult.Handled;
            }
            if (code == Keys.Oem2 && (mods & Keys.Control) == 0)
            {
                _def.Cues.Help?.Invoke();
                if (_def.OpenExplorer?.Invoke() != true) SpeakHelpSentence();
                return ValueLayerKeyResult.Handled;
            }

            // Everything else: keep the value, close out loud, and let the
            // key travel on to mean what it always means.
            Confirm(speak: true);
            return ValueLayerKeyResult.ClosedPassThrough;
        }

        private void HandleArrow(Keys code, Keys mods)
        {
            ValueLayerAxes axis = (code == Keys.Left || code == Keys.Right) ? ValueLayerAxes.LeftRight : ValueLayerAxes.UpDown;
            int direction = (code == Keys.Right || code == Keys.Up) ? +1 : -1;
            bool plain = mods == Keys.None;
            bool shiftOnly = mods == Keys.Shift;

            switch (_def.Selection)
            {
                case ValueLayerSelection.ByModifier:
                {
                    var slot = AddressedSlot(mods, axis);
                    if (slot != null) NudgeSlot(slot, direction, fine: false);
                    return;
                }

                case ValueLayerSelection.ByLeftRight:
                    if (axis == ValueLayerAxes.LeftRight)
                    {
                        if (!plain) { Refuse(HintFor(_current)); return; }
                        SelectNext(direction);
                        return;
                    }
                    if (!(plain || shiftOnly) || _current == null) { Refuse(HintFor(_current)); return; }
                    NudgeSlot(_current, direction, fine: shiftOnly);
                    return;

                case ValueLayerSelection.ByLetter:
                    if (_current == null)
                    {
                        Refuse(_def.PickTargetHint?.Invoke());
                        return;
                    }
                    if (!(plain || shiftOnly) || (_current.Def.Axes & axis) == 0)
                    {
                        Refuse(HintFor(_current));
                        return;
                    }
                    NudgeSlot(_current, direction, fine: shiftOnly);
                    return;

                default: // Single
                    if (_current != null && (plain || shiftOnly) && (_current.Def.Axes & axis) != 0)
                    {
                        NudgeSlot(_current, direction, fine: shiftOnly);
                        return;
                    }
                    Refuse(HintFor(_current));
                    return;
            }
        }

        /// <summary>
        /// The target a <see cref="ValueLayerSelection.ByModifier"/> press
        /// names: which Shift is physically down picks the edge, Ctrl picks
        /// the width, neither picks the whole. <paramref name="axis"/>
        /// narrows it to the targets that arrow pair moves; a JUMP passes
        /// null, because Home is not a direction and every target has a
        /// minimum whichever pair walks it.
        /// </summary>
        /// <returns>
        /// Null when nothing was named — having already refused OUT LOUD, so
        /// the caller only has to decide what to do with a target it got.
        /// </returns>
        private Slot? AddressedSlot(Keys mods, ValueLayerAxes? axis)
        {
            bool ctrl = (mods & Keys.Control) != 0;
            ShiftSide side = ShiftSide.None;
            if ((mods & Keys.Shift) != 0)
            {
                side = _def.ShiftSideNow!();
                if (side == ShiftSide.None || side == ShiftSide.Both)
                {
                    // The Shift bit says a Shift was down; the probe cannot
                    // say which. Refuse and say so — moving the wrong edge
                    // silently is the invisible failure.
                    Refuse(_def.WhichShiftHint?.Invoke());
                    return null;
                }
            }
            var slot = _slots.FirstOrDefault(s => InGroup(s)
                && (axis == null || (s.Def.Axes & axis.Value) != 0)
                && s.Def.Shift == side && s.Def.Ctrl == ctrl);
            if (slot == null) Refuse(_def.NoVerbHint?.Invoke() ?? _def.WrongAxisHint?.Invoke());
            return slot;
        }

        private void Refuse(string? hint)
        {
            _def.Cues.Invalid?.Invoke();
            if (!string.IsNullOrEmpty(hint)) EmitSay(hint!);
        }

        private string? HintFor(Slot? s)
            => s?.Def.WrongAxisHint?.Invoke() ?? _def.WrongAxisHint?.Invoke();

        // ────────────────────────────────────────────────────────────────
        //  The semantic surface — what a knob host drives directly (#200)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Move the current target one step and speak the new position.
        /// Clamps at the rails; the rail is stated once.
        /// </summary>
        public void Nudge(int direction, bool fine)
        {
            if (!_live || _current == null) return;
            NudgeSlot(_current, direction, fine);
        }

        /// <summary>Move a named target one step, whether or not it is selected.</summary>
        public void Nudge(ValueTarget target, int direction, bool fine)
        {
            if (!_live) return;
            var slot = SlotOf(target);
            if (slot != null) NudgeSlot(slot, direction, fine);
        }

        /// <summary>Make a target current and announce it, as its letter would.</summary>
        public void Select(ValueTarget target)
        {
            if (!_live) return;
            var slot = SlotOf(target);
            if (slot != null) SelectSlot(slot, announce: true);
        }

        /// <summary>
        /// Step the selection through the targets in order (the two-axis
        /// form). Clamps at the ends: the end target is announced again,
        /// which the coalescer states once.
        /// </summary>
        public void SelectNext(int direction)
        {
            if (!_live || _slots.Count == 0) return;
            // A toggle is flipped by its chord, never stepped onto: it has
            // no arrows to offer once selected.
            var visible = _slots.Where(s => InGroup(s) && !s.Def.IsToggle).ToList();
            if (visible.Count == 0) return;
            int index = _current == null ? -1 : visible.IndexOf(_current);
            int next = Math.Clamp(index + direction, 0, visible.Count - 1);
            SelectSlot(visible[next], announce: true);
        }

        /// <summary>Switch the active group and announce it.</summary>
        public void SwitchGroup(string group)
        {
            if (!_live) return;
            _group = group;
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): group " + group, TraceLevel.Info);
            if (_current != null && !InGroup(_current)) _current = null;
            if (_def.DescribeGroup != null) EmitSay(_def.DescribeGroup(group));
        }

        /// <summary>
        /// Place the current target at one end of its range, or at its
        /// centre, and speak where it landed (#522). A jump is not a nudge:
        /// it lands ON the rail on purpose, so it never sounds the rail cue.
        /// </summary>
        public void JumpTo(ValueLayerJump where)
        {
            if (!_live || _current == null) return;
            JumpSlot(_current, where);
        }

        /// <summary>Place a named target, whether or not it is selected.</summary>
        public void JumpTo(ValueTarget target, ValueLayerJump where)
        {
            if (!_live) return;
            var slot = SlotOf(target);
            if (slot != null) JumpSlot(slot, where);
        }

        /// <summary>Jump the current target to its centre (pan: centre) and speak it.</summary>
        public void JumpToAnchor() => JumpTo(ValueLayerJump.Centre);

        /// <summary>
        /// Speak the current state and the keys without changing anything —
        /// the on-demand answer to "which control am I holding?" (#200).
        /// </summary>
        public void SpeakHelp()
        {
            if (!_live) return;
            _def.Cues.Help?.Invoke();
            SpeakHelpSentence();
        }

        /// <summary>
        /// Flip a toggle target and speak its new state, as its chord would
        /// (#200). A target without a <see cref="ValueTarget.ToggleKey"/> is
        /// not a switch and is left alone.
        /// </summary>
        public void Toggle(ValueTarget target)
        {
            if (!_live) return;
            var slot = SlotOf(target);
            if (slot != null && slot.Def.IsToggle) ToggleSlot(slot);
        }

        /// <summary>The spoken help, with no cue — the keyboard face plays the
        /// cue itself, ahead of whatever surface the host opens.</summary>
        private void SpeakHelpSentence()
        {
            if (_def.DescribeLayerHelp != null)
                EmitSay(_def.DescribeLayerHelp(this));
            else if (_def.DescribeHelp != null && _current != null)
                EmitSay(_def.DescribeHelp(_current.Shadow, _current.Entry));
        }

        /// <summary>Speak one target's selection sentence without moving it — a question.</summary>
        public void SpeakTarget(ValueTarget target)
        {
            if (!_live) return;
            var slot = SlotOf(target);
            if (slot == null) return;
            EnsureSeeded(slot);
            if (slot.Def.Linked) slot.Shadow = slot.Def.Read!();
            EmitAnswer(DescribeSelection(slot), SubjectOf(slot));
        }

        /// <summary>
        /// Keep the current values and close. Writes nothing — everything is
        /// already applied — which is what makes confirm safe on every path,
        /// including mid-transmit.
        /// </summary>
        public void Confirm(bool speak)
        {
            if (!_live) return;
            _live = false;
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): confirmed"
                + (_current != null ? " at " + _current.Shadow : "")
                + ", touched " + _touchOrder.Count, TraceLevel.Info);
            if (speak)
            {
                _def.Cues.Closed?.Invoke();
                EmitSay(_def.DescribeClosed!());
            }
            _def.Exited?.Invoke(ValueLayerExit.Confirmed);
        }

        /// <summary>
        /// Put every touched target back to its entry value, out loud, and
        /// close. The only path that writes on the way out.
        /// </summary>
        public void Cancel(bool speak)
        {
            if (!_live) return;
            _live = false;

            var restored = new List<ValueTargetRestore>();
            if (_restore != null)
            {
                _restore();
                foreach (var s in _touchOrder)
                {
                    s.Shadow = s.Entry;
                    restored.Add(new ValueTargetRestore(s.Def, s.Entry));
                }
            }
            else
            {
                foreach (var s in _touchOrder)
                {
                    s.Shadow = s.Entry;
                    s.Def.Apply!(s.Entry);
                    restored.Add(new ValueTargetRestore(s.Def, s.Entry));
                }
            }
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): cancelled, restored " + restored.Count, TraceLevel.Info);

            if (speak)
            {
                _def.Cues.Closed?.Invoke();
                string text;
                if (_def.DescribeLayerRestored != null)
                    text = _def.DescribeLayerRestored(this, restored);
                else if (_def.DescribeRestored != null && _current != null)
                    text = _def.DescribeRestored(_current.Entry);
                else
                    text = _def.DescribeClosed!();
                EmitSay(text);
            }
            _def.Exited?.Invoke(ValueLayerExit.Cancelled);
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
            Tracing.TraceLine("ValueSubLayer(" + _def.Id + "): dropped", TraceLevel.Info);
            _def.Exited?.Invoke(ValueLayerExit.Dropped);
        }

        /// <summary>
        /// Forget what the matching targets held — after a slice jump, the
        /// per-slice ones — so they re-seed from the new context. What was
        /// done to them before the jump is kept, not restored: the old slice
        /// was confirmed the moment the operator left it. The current target,
        /// if it re-binds, is announced again on its new context.
        /// </summary>
        public void Rebind(Func<ValueTarget, bool> affected)
        {
            if (!_live) return;
            foreach (var s in _slots.Where(s => affected(s.Def)).ToList())
            {
                s.Seeded = false;
                s.Touched = false;
                _touchOrder.Remove(s);
                if (s == _current)
                {
                    EnsureSeeded(s);
                    EmitAnswer(DescribeSelection(s), SubjectOf(s));
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Reading the layer, for the host's sentences
        // ────────────────────────────────────────────────────────────────

        /// <summary>The layer's shadow of a target (seeding it if it has not been reached).</summary>
        public int ValueOf(ValueTarget target)
        {
            var s = SlotOf(target);
            if (s == null) return 0;
            EnsureSeeded(s);
            return s.Def.Linked ? s.Def.Read!() : s.Shadow;
        }

        /// <summary>What Escape would put a target back to.</summary>
        public int EntryOf(ValueTarget target)
        {
            var s = SlotOf(target);
            if (s == null) return 0;
            EnsureSeeded(s);
            return s.Entry;
        }

        /// <summary>Has this target been moved since entry (or its last rebind)?</summary>
        public bool WasTouched(ValueTarget target) => SlotOf(target)?.Touched ?? false;

        /// <summary>
        /// The current target's value in the form the operator's verbosity
        /// asks for: words at Chatty and above (interpretable), the number
        /// at Terse (precise, repeatable). A target with no words form gets
        /// numbers everywhere.
        /// </summary>
        public string FormValue() => _current == null ? "" : FormOf(_current, _current.Shadow);

        /// <summary>The verbosity-chosen form of any value on the current target's scale.</summary>
        public string FormOf(int value) => _current == null ? value.ToString() : FormOf(_current, value);

        /// <summary>The verbosity-chosen form of a value on a named target's scale.</summary>
        public string FormOf(ValueTarget target, int value)
        {
            var s = SlotOf(target);
            return s == null ? value.ToString() : FormOf(s, value);
        }

        /// <summary>The selection sentence for a target, as its letter would say it.</summary>
        public string DescribeTarget(ValueTarget target)
        {
            var s = SlotOf(target);
            if (s == null) return "";
            EnsureSeeded(s);
            if (s.Def.Linked) s.Shadow = s.Def.Read!();
            return DescribeSelection(s);
        }

        // ────────────────────────────────────────────────────────────────
        //  Internals
        // ────────────────────────────────────────────────────────────────

        private Slot? SlotOf(ValueTarget target) => _slots.FirstOrDefault(s => ReferenceEquals(s.Def, target));

        private bool InGroup(Slot s) => _group == null || s.Def.Group == null || s.Def.Group == _group;

        private string Tag(Slot s) => string.IsNullOrEmpty(s.Def.Id) ? _def.Id : _def.Id + ":" + s.Def.Id;

        private string SubjectOf(Slot s) => Speech.SpeechSubject.ValueLayer(_def.Id, s.Def.Id);

        private void EnsureSeeded(Slot s)
        {
            if (s.Seeded) return;
            s.Shadow = Math.Clamp(s.Def.Read!(), s.Def.Min, s.Def.Max);
            s.Entry = s.Shadow;
            s.Seeded = true;
        }

        private void Touch(Slot s)
        {
            if (!s.Touched)
            {
                s.Touched = true;
                _touchOrder.Add(s);
            }
        }

        private void SelectSlot(Slot s, bool announce)
        {
            _current = s;
            EnsureSeeded(s);
            if (s.Def.Linked) s.Shadow = s.Def.Read!();
            Tracing.TraceLine("ValueSubLayer(" + Tag(s) + "): selected at " + s.Shadow, TraceLevel.Info);
            if (announce) EmitAnswer(DescribeSelection(s), SubjectOf(s));
        }

        private void NudgeSlot(Slot s, int direction, bool fine)
        {
            EnsureSeeded(s);
            if (s.Def.Linked) s.Shadow = s.Def.Read!();

            int step = fine ? s.Def.FineStep : (s.Def.StepNow?.Invoke() ?? s.Def.Step);
            int before = s.Shadow;
            int next = Math.Clamp(s.Shadow + direction * step, s.Def.Min, s.Def.Max);
            if (s.Def.Constrain != null) next = s.Def.Constrain(next);

            if (next != before)
            {
                s.Shadow = next;
                s.Def.Apply!(next);
                Touch(s);
                EmitMove(FormOf(s, next), SubjectOf(s));
                return;
            }

            // The rail. Stated once — the coalescer drops the identical
            // repeat — unless the target has a rail sentence, which says why.
            _def.Cues.Rail?.Invoke();
            EmitMove(s.Def.DescribeRail?.Invoke(next) ?? FormOf(s, next), SubjectOf(s));
        }

        /// <summary>
        /// Place one target rather than stepping it (#522). The clamp and
        /// the constraint are the nudge's own, so a jump can never land
        /// somewhere a nudge could not reach — a transmit edge asked for its
        /// maximum stops against the other edge, exactly as walking it does.
        /// </summary>
        /// <remarks>
        /// It speaks the plain form and never the rail sentence: landing on
        /// the rail is the whole point of Home and End, and "at the limit"
        /// reports a refusal. Nothing sounds the rail cue for the same
        /// reason.
        /// </remarks>
        private void JumpSlot(Slot s, ValueLayerJump where)
        {
            EnsureSeeded(s);
            if (s.Def.Linked) s.Shadow = s.Def.Read!();

            int wanted = where switch
            {
                ValueLayerJump.Minimum => s.Def.Min,
                ValueLayerJump.Maximum => s.Def.Max,
                _ => s.Def.Anchor ?? 0,
            };
            int before = s.Shadow;
            int next = Math.Clamp(wanted, s.Def.Min, s.Def.Max);
            if (s.Def.Constrain != null) next = s.Def.Constrain(next);

            s.Shadow = next;
            if (next != before || !s.Def.Linked)
            {
                s.Def.Apply!(next);
                Touch(s);
            }
            Tracing.TraceLine("ValueSubLayer(" + Tag(s) + "): jumped " + where + " to " + next, TraceLevel.Info);
            EmitMove(FormOf(s, next), SubjectOf(s));
        }

        /// <summary>
        /// Flip a switch target (#524). Off is the minimum, on is the
        /// maximum; the new state is spoken as an answer rather than a
        /// sweep, so two presses are two states and both are heard.
        /// </summary>
        private void ToggleSlot(Slot s)
        {
            EnsureSeeded(s);
            if (s.Def.Linked) s.Shadow = s.Def.Read!();
            // Off is Min, on is Max; a seed that clamped to anything else
            // reads as on, so the first press turns it off.
            s.Shadow = s.Shadow == s.Def.Min ? s.Def.Max : s.Def.Min;
            s.Def.Apply!(s.Shadow);
            Touch(s);
            Tracing.TraceLine("ValueSubLayer(" + Tag(s) + "): toggled to " + s.Shadow, TraceLevel.Info);
            // A flip is a question answered now, not a sweep: two presses in
            // a row are two states, and both are heard.
            EmitAnswer(DescribeSelection(s), SubjectOf(s));
        }

        private void SpeakAddressed(Keys mods)
        {
            ShiftSide side = ShiftSide.None;
            if ((mods & Keys.Shift) != 0)
            {
                side = _def.ShiftSideNow?.Invoke() ?? ShiftSide.None;
                if (side == ShiftSide.None || side == ShiftSide.Both)
                {
                    Refuse(_def.WhichShiftHint?.Invoke());
                    return;
                }
            }
            // The addressed target is the one the same Shift would WALK: the
            // walkable (Left/Right, no Ctrl) target on that side.
            var slot = _slots.FirstOrDefault(s => InGroup(s)
                && (s.Def.Axes & ValueLayerAxes.LeftRight) != 0 && !s.Def.Ctrl && s.Def.Shift == side)
                ?? _current;
            if (slot == null)
            {
                Refuse(_def.NoVerbHint?.Invoke());
                return;
            }
            EnsureSeeded(slot);
            if (slot.Def.Linked) slot.Shadow = slot.Def.Read!();
            EmitAnswer(DescribeSelection(slot), SubjectOf(slot));
        }

        /// <summary>
        /// The selection sentence: the target's own if it has one, else the
        /// verbosity-chosen form — which already names the target ("Compander
        /// 20"), so nothing is prefixed. <see cref="ValueTarget.Name"/> is
        /// for hints, not for this.
        /// </summary>
        private string DescribeSelection(Slot s)
        {
            string core = s.Def.DescribeSelected != null
                ? s.Def.DescribeSelected(s.Shadow)
                : FormOf(s, s.Shadow);
            return core + (s.Def.Note?.Invoke() ?? "");
        }

        private string FormOf(Slot s, int value)
        {
            if (s.Def.Words != null && (int)VerbosityNow() >= (int)VerbosityLevel.Chatty)
                return s.Def.Words(value);
            return s.Def.Number!(value);
        }
    }
}
