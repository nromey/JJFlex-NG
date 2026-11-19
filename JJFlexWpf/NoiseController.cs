using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>Who speaks a noise sentence. An interface so the controller's tests can read every sentence on the live desk without a screen reader.</summary>
    public interface INoiseSpeaker
    {
        VerbosityLevel Verbosity { get; }
        /// <param name="text">Already resolved at <see cref="Verbosity"/>.</param>
        /// <param name="subject">A <see cref="SpeechSubject"/> constant.</param>
        /// <param name="level">The level the sentence needs (Chatty for a Chatty-only clause, Critical for unasked news the operator must hear).</param>
        void Say(string text, string subject, VerbosityLevel level);

        /// <summary>
        /// Say a sentence nobody asked for AFTER whatever is being said, never
        /// cutting it: a result that arrives on its own, such as a slot's failed
        /// start. <see cref="Say"/> interrupts, which is right for the answer to
        /// a press and wrong here: the slot's "could not start" arrived 27 ms
        /// after the refusal it follows and cut it at its first word, so the
        /// operator never heard which engine had been pushed out (repair round
        /// 1, the Track E follow-up's harness check). Defaults to
        /// <see cref="Say"/> for a speaker with no queue.
        /// </summary>
        void SayAfter(string text, string subject, VerbosityLevel level) => Say(text, subject, level);

        /// <summary>
        /// Declare that whatever is still unheard on <paramref name="subject"/>
        /// is obsolete, so it is neither delivered late nor rescued after the
        /// next interrupt (repair round 2: a queued "could not start" outlived
        /// the selection it was about and was spoken 624 ms after "RNNoise on").
        /// <paramref name="why"/> is trace prose. A speaker with no queue has
        /// nothing to retire.
        /// </summary>
        void Retire(string subject, string why) { }
    }

    /// <summary>The app's speaker: the screen reader, keyed by subject so a newer noise sentence covers an unheard older one.</summary>
    public sealed class ScreenReaderNoiseSpeaker : INoiseSpeaker
    {
        public VerbosityLevel Verbosity => ScreenReaderOutput.CurrentVerbosity;
        public void Say(string text, string subject, VerbosityLevel level) =>
            ScreenReaderOutput.Speak(text, SpeechIntent.Latest, level, subject: subject);
        public void SayAfter(string text, string subject, VerbosityLevel level) =>
            ScreenReaderOutput.Speak(text, SpeechIntent.Queue, level, subject: subject);
        public void Retire(string subject, string why) => ScreenReaderOutput.Supersede(subject, why);
    }

    /// <summary>
    /// The noise controller's answer to a mode change, held for a surface that
    /// says the mode change itself, later (<see cref="NoiseController.HoldModeAnswer"/>).
    /// Disposing releases it, queued behind whatever is being said.
    /// </summary>
    public interface IModeAnswerHold : IDisposable
    {
        /// <summary>
        /// End the hold and take the held answer, to say it joined to the
        /// surface's own sentence under <paramref name="subject"/>; null when
        /// there is none to say (no group change, or it went stale). Once taken
        /// or disposed, a hold does nothing more.
        /// </summary>
        (string Text, VerbosityLevel Level)? TakeJoined(string subject);

        /// <summary>
        /// True once a later operator action has been answered since this hold
        /// was taken (<see cref="NoiseController.NoteOperatorAction"/>): any key
        /// the operator pressed after the click, or any noise action that
        /// answered. The surface's own late sentence is then stale and must not
        /// interrupt that newer answer (#1117, repair round 3: the menu's "Mode
        /// CW" cut a band announcement made 316 ms after the click).
        /// </summary>
        bool OutlivedByANewerAnswer { get; }
    }

    /// <summary>A delay the controller waits before acting (the settle before an engine field applies; the check for a sibling the radio switched off).</summary>
    public interface INoiseScheduler
    {
        IDisposable After(TimeSpan delay, Action action);
    }

    /// <summary>The app's scheduler: a one-shot timer, its action marshalled by the caller's own dispatch.</summary>
    public sealed class TimerNoiseScheduler : INoiseScheduler
    {
        private readonly Action<Action> _post;
        public TimerNoiseScheduler(Action<Action>? post = null) => _post = post ?? (a => a());
        public IDisposable After(TimeSpan delay, Action action)
        {
            Timer? timer = null;
            timer = new Timer(_ => { timer?.Dispose(); _post(action); }, null, delay, Timeout.InfiniteTimeSpan);
            return timer;
        }
    }

    /// <summary>
    /// What kind of thing a lever adjusts. <see cref="Floor"/> is a computer
    /// engine's noise floor (#1019): it was "Strength" until Sprint 49 Track S
    /// flipped it, and the stored quantity underneath is still the wet fraction.
    /// </summary>
    public enum NoiseLeverKind { RadioLevel, Floor, Setting, ListenRemoved }

    /// <summary>
    /// One lever of one engine, as every surface sees it: the radio's level,
    /// a computer engine's noise floor, one of its descriptor's settings, or the
    /// Listen to what was removed switch. The value lives in the radio or the
    /// slot; this only says where.
    /// </summary>
    /// <remarks>
    /// <see cref="Direction"/> (Sprint 49 Track H2): which chain the lever
    /// belongs to. A transmit lever reads and writes the transmit chain's slot
    /// for its engine (<see cref="TxNoiseChain"/>), through the chain's intent
    /// calls, and is spoken on the transmit subjects. Receive is the default,
    /// so every lever built before transmit existed means what it always did.
    /// </remarks>
    public sealed record NoiseLever(string EngineId, string Key, string LabelKey, NoiseLeverKind Kind,
        EngineSettingDescriptor? Descriptor = null, bool Advanced = false, DenoiseDirection Direction = DenoiseDirection.Receive)
    {
        /// <summary>NNR's Depth is the mask floor shown as a positive number, so up means more depth and a more negative floor (ruled S8 18:49).</summary>
        public bool Inverted => EngineId == "nnr" && Key == "mask_floor_db";
    }

    /// <summary>
    /// THE noise reduction controller (Sprint 49 Track E, E4). Owns the engine
    /// list in order, each engine's state and availability, switching with the
    /// same-family replace rule, levers, the settle delay, favourites, return
    /// to defaults, presets, the active summary, per-mode memory, the journal
    /// and every refusal. The Audio menu, the Home expander and the JJ key's
    /// noise layer call it, and none of them re-implements any of it, so the
    /// four roads agree by construction rather than by care.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No WPF type in here.</b> The radio is <see cref="INoiseRadio"/>, the
    /// speaker <see cref="INoiseSpeaker"/>, time <see cref="INoiseScheduler"/>,
    /// so every rule is testable windowless on the live desk.
    /// </para>
    /// <para>
    /// <b>Threading.</b> Called on the UI thread by every surface. The slots
    /// it drives are thread-safe by their own design; it never touches the
    /// audio thread.
    /// </para>
    /// </remarks>
    public sealed partial class NoiseController
    {
        /// <summary>
        /// How long a computer engine field waits after the last arrow before
        /// it applies the choice (#769): arrowing past NVIDIA NR must not
        /// start a graphics card's stack on the way past. Long enough for a
        /// quick arrow run, short enough that a deliberate choice feels
        /// immediate.
        /// </summary>
        public const int EngineSettleMs = 700;

        /// <summary>How long after a radio switch is turned on the controller looks for a sibling the radio turned off by itself (design part six; open fact 1).</summary>
        public const int SiblingCheckMs = 1500;

        /// <summary>How long a written radio value is believed over the radio's own echo, which arrives through the command queue.</summary>
        private const int ShadowMs = 2000;

        private readonly DenoiseEngineCatalog _catalog;
        private readonly Func<INoiseRadio?> _radio;
        private readonly Func<RxAudioPipeline?> _pipeline;
        private readonly Func<AudioOutputConfig?> _config;
        private readonly Action _persist;
        private readonly INoiseSpeaker _speaker;
        private readonly INoiseIntentStore _intents;
        private readonly INoiseScheduler _scheduler;
        private readonly INnrCwCostSource _cwCost;
        private readonly Func<long> _clock;

        // A shadow is a value the controller wrote, believed over the radio's
        // echo for ShadowMs. It belongs to the slice it was written on
        // (repair round 3): keyed by the radio's own slice object, and never
        // read for any other slice.
        private readonly Dictionary<string, (bool On, long Until, object? Slice)> _switchShadow = new();
        private readonly Dictionary<string, (int Level, long Until, object? Slice)> _levelShadow = new();

        /// <summary>A switch shadow still in force on the slice the radio reads now.</summary>
        private bool SwitchShadow(string id, INoiseRadio r, out bool on)
        {
            on = false;
            if (!_switchShadow.TryGetValue(id, out var shadow) || _clock() >= shadow.Until
                || !ReferenceEquals(shadow.Slice, r.SliceIdentity)) return false;
            on = shadow.On;
            return true;
        }

        /// <summary>A level shadow still in force on the slice the radio reads now.</summary>
        private bool LevelShadow(string id, INoiseRadio r, out int level)
        {
            level = 0;
            if (!_levelShadow.TryGetValue(id, out var shadow) || _clock() >= shadow.Until
                || !ReferenceEquals(shadow.Slice, r.SliceIdentity)) return false;
            level = shadow.Level;
            return true;
        }
        private readonly Dictionary<DenoiseFamily, IDisposable> _settling = new();
        private NoiseModeGroup? _group;
        private List<(DenoiseFamily Family, string EngineId)>? _zeroRestore;

        public NoiseController(DenoiseEngineCatalog catalog, Func<INoiseRadio?> radio, Func<RxAudioPipeline?> pipeline,
            Func<AudioOutputConfig?> config, Action persist, INoiseSpeaker speaker, INoiseIntentStore intents,
            INoiseScheduler scheduler, INnrCwCostSource? cwCost = null, Func<long>? clock = null, Func<TxNoiseChain?>? transmitChain = null)
        {
            _catalog = catalog; _radio = radio; _pipeline = pipeline; _config = config; _persist = persist;
            _speaker = speaker; _intents = intents; _scheduler = scheduler;
            _cwCost = cwCost ?? new UnmeasuredNnrCwCost();
            _clock = clock ?? (() => Environment.TickCount64);
            // The transmit chain is the session's own (Track H), radio or no
            // radio; asked for only when a transmit road is used, so a receive
            // test never builds one.
            _txChain = transmitChain ?? (() => TxAudioConditioning.Chain);
        }

        public DenoiseEngineCatalog Catalog => _catalog;

        /// <summary>
        /// The engine the noise layer last touched; its levers are what Up, Down
        /// and Tab work on. An assignment here points the cursor at RECEIVE; the
        /// transmit half points it with its direction (Track H2 repair round 1,
        /// item 2), and the layer acts on it only in that direction.
        /// </summary>
        public string? Current
        {
            get => _current;
            private set
            {
                // An engine this version cannot run is never the cursor, by any
                // road (Sprint 49 Track H2 bench rounds 1 and 2): its levers and
                // favourite would act on something that is not there. An engine that
                // is only unavailable now (no helper, a model missing) stays a valid
                // intent and may be.
                if (NotInThisVersion(value)) return;
                _current = value;
                _currentDirection = DenoiseDirection.Receive;
                // A newer cursor assignment, by any action, makes an older delayed
                // one stale (Track H2 repair round 2: Codex's same-session stress case).
                _cursorEpoch++;
            }
        }
        private string? _current;

        /// <summary>A computer engine this build has no factory for (NVIDIA NR today): refused, never a choice or the cursor.</summary>
        private bool NotInThisVersion(string? id) => id != null && _catalog.Find(id) is { Side: DenoiseSide.Pc, Factory: null };

        /// <summary>Which lever of <see cref="Current"/> the layer's arrows adjust; 0 is the primary lever.</summary>
        public int CurrentLeverIndex { get; private set; }

        // ────────────────────────────────────────────────────────────────
        //  The engine list
        // ────────────────────────────────────────────────────────────────

        /// <summary>Every engine in the order every surface lists them.</summary>
        public IReadOnlyList<DenoiseEngineEntry> Engines(DenoiseGroup? group = null) =>
            _catalog.InSurfaceOrder().Where(e => group == null || e.Group == group).Where(e => e.Serves(DenoiseDirection.Receive)).ToList();

        public DenoiseEngineEntry? Find(string id) => _catalog.Find(id);

        public string NameOf(string id) => _catalog.Find(id) is { } e ? Lexicon.Get(e.LabelKey) : id;

        private PcDenoiseSlot? SlotFor(DenoiseEngineEntry e) =>
            e.Side != DenoiseSide.Pc || _pipeline() is not { } p ? null
                : e.Family == DenoiseFamily.Spectral ? p.SpectralSlot : p.NeuralSlot;

        private PcDenoiseSlot? SlotOf(DenoiseFamily family) =>
            _pipeline() is not { } p ? null : family == DenoiseFamily.Spectral ? p.SpectralSlot : p.NeuralSlot;

        /// <summary>Whether an engine is on: the radio's switch, or a computer slot that is on with this engine chosen.</summary>
        public bool IsOn(string id)
        {
            var e = _catalog.Find(id);
            if (e == null) return false;
            if (e.Side == DenoiseSide.Radio)
            {
                var r = _radio();
                if (r == null) return false;
                FollowActiveSlice();   // reconcile BEFORE reading (round 3)
                if (SwitchShadow(id, r, out bool shadowOn)) return shadowOn;
                return r.ReadSwitch(e);
            }
            var slot = SlotFor(e);
            return slot != null && slot.Enabled && slot.ChosenEngineId == id;
        }

        /// <summary>
        /// Why an engine cannot run here right now, as a short marker for a
        /// menu row or list entry ("needs subscription", "helper not
        /// running"), or null when it can. Never hides anything: the row stays
        /// listed, live and self-explaining (#726).
        /// </summary>
        public string? Marker(string id)
        {
            var e = _catalog.Find(id);
            if (e == null) return null;
            if (e.Side == DenoiseSide.Radio)
            {
                var r = _radio();
                if (r == null || e.Capability is not RadioCapability cap || !r.CapabilityClosed(cap)) return null;
                return Lexicon.Get(r.ClosedByHardware(cap) ? "audio.noise_surface.marker.not_on_radio" : "audio.noise_surface.marker.needs_subscription");
            }
            if (e.Factory == null) return Lexicon.Get("audio.noise_surface.marker.not_in_version");
            var reason = SafeAvailability(e);
            return reason == Unavailability.None ? null : Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(reason));
        }

        /// <summary>
        /// An engine's name with its availability marker, as every list shows it
        /// (the menu, Home's engine field, Settings' favourites): "Spectral NR,
        /// needs subscription". The marker is text before any tab, never a
        /// greyed row (#726, #838 defect 6).
        /// </summary>
        public string MarkedName(string id)
        {
            string name = NameOf(id);
            string? marker = Marker(id);
            return string.IsNullOrEmpty(marker) ? name : name + ", " + char.ToLowerInvariant(marker[0]) + marker.Substring(1);
        }

        /// <summary>
        /// Whether an engine can run here, probing nothing that starts. An engine
        /// whose chosen model needs its own native file (RNNoise, #1019) is as
        /// available as that model: the engine follows the choice, so a missing
        /// tag library marks RNNoise only while the tag model is chosen.
        /// </summary>
        /// <param name="e">The engine.</param>
        /// <param name="wanted">The model a favourite will land on; null judges the model chosen now.</param>
        private Unavailability SafeAvailability(DenoiseEngineEntry e, string? wanted = null)
        {
            if (e.Side == DenoiseSide.Pc && e.Settings.FirstOrDefault(d => d.Name == "model") is { Kind: EngineSettingKind.Choice } model
                && model.Options?.Any(o => o.Probe != null) == true)
            {
                string? chosen = wanted ?? (SlotFor(e) is { } slot ? slot.GetSetting(e.Id, "model").Choice : model.Default.Choice);
                if (model.Options.FirstOrDefault(o => o.Name == chosen)?.Probe is { } probe) return SafeProbe(probe, e.Id + "." + chosen);
            }
            try { return e.Availability(); }
            catch (Exception ex) { Trace.WriteLine("NoiseController: probe for " + e.Id + " threw " + ex.Message); return Unavailability.InitFailed; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Saying things — the one speaker every surface shares
        // ────────────────────────────────────────────────────────────────

        private string L(string key, params (string, object?)[] args) => Lexicon.Get(key, _speaker.Verbosity, args);

        private int _quiet;

        /// <summary>
        /// Run an action with the controller's own sentences held back: for a
        /// surface whose control already announces its state (the Noise
        /// Profiles window's switches answer with a tone only, by #128's ruling).
        /// The rules, the journal and per-mode memory still run.
        /// </summary>
        public void Quietly(Action act)
        {
            _quiet++;
            try { act(); }
            finally { _quiet--; }
        }

        private void Say(string text, string subject, VerbosityLevel level = VerbosityLevel.Terse)
        {
            if (string.IsNullOrWhiteSpace(text) || _quiet > 0) return;
            if (_utterance != null) { _utterance.Add((text, subject, level, false)); return; }
            _speaker.Say(text, subject, level);
        }

        // ────────────────────────────────────────────────────────────────
        //  One action, one utterance (#1110)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The sentences an action has said so far, while it runs; null outside
        /// an action. Every <see cref="Say(string, string, VerbosityLevel)"/> is
        /// an interrupt, so two of them from one action meant the second cut the
        /// first: entering CW restored the CW group ("Back in CW: RNNoise off,
        /// NNR on.") and then switched NNR's model, and the model sentence cut
        /// the swap at its first word, so the operator never heard which engine
        /// went in (#1110, live on the 8600). Inside an action the sentences are
        /// gathered and said once, as one utterance, when the outermost action
        /// ends: nothing from the same action can cut anything else from it.
        /// A timer's or observer's firing is an action too (repair round 1,
        /// Codex: the sibling check said one interrupt per sibling the radio
        /// turned off, each cutting the last). <c>Queued</c> marks a part said
        /// with <see cref="SayAfter"/>.
        /// </summary>
        private List<(string Text, string Subject, VerbosityLevel Level, bool Queued)>? _utterance;
        private int _utteranceDepth;

        /// <summary>
        /// Set by the outermost action that answers something ANOTHER subsystem
        /// has just said (a mode change, whose mode word the mode path speaks):
        /// the action's one utterance goes out after what is being said, under
        /// this subject, never over it (#1117). Null for every other action,
        /// whose utterance is the answer to a press and interrupts.
        /// </summary>
        private string? _utteranceAfterUnder;

        /// <summary>
        /// Journal entries the running action owes but must record after its own
        /// (a model the mode's rule chose while an engine was being chosen,
        /// #1118): recorded in order when the outermost action ends, so the
        /// journal reads the cause first. Null outside an action.
        /// </summary>
        private List<Action>? _journalAfterAction;

        /// <summary>
        /// Start an action: every sentence said until the returned scope is
        /// disposed goes out as one utterance (<see cref="_utterance"/>). Nests:
        /// only the outermost scope speaks. Use with <c>using var oneUtterance = OneUtterance();</c>
        /// at the top of every entry point that changes noise settings.
        /// </summary>
        /// <param name="afterWhatIsSaidUnder">
        /// Only for an action that follows another subsystem's own sentence (a
        /// mode change): its utterance is queued behind what is being said, under
        /// this subject, and owned like the other queued notices
        /// (<see cref="_noticesOwed"/>). Read from the outermost scope only.
        /// </param>
        /// <summary>
        /// Says a sentence once a window that is closing has gone and the reader
        /// has announced where focus landed (Sprint 49 Track H2 bench round,
        /// item 2). Set by the main window to <see cref="WindowSettledSpeech.After"/>;
        /// null (a test) says it at once.
        /// </summary>
        public Action<Action>? AfterWindowSettles { get; set; }

        /// <summary>
        /// For a dialog whose action closes it, or returns to it from another
        /// window: everything the controller says until the hold is disposed is
        /// gathered as one utterance, as <see cref="OneUtterance"/> does; call
        /// <see cref="AnswerHold.AfterTheWindowCloses"/> when a window is about to
        /// change, and the utterance is said only once it has settled, queued,
        /// because a screen reader flushes its speech on a window change
        /// (project memory: speech flushes on window change). Left alone, the
        /// hold says it at once, as before.
        /// </summary>
        public AnswerHold HoldAnswer()
        {
            if (_utteranceDepth++ == 0) { _utterance = new(); _utteranceAfterUnder = null; _utteranceRefused = null; }
            return new AnswerHold(this);
        }

        /// <summary>The scope <see cref="HoldAnswer"/> returns.</summary>
        public sealed class AnswerHold : IDisposable
        {
            private readonly NoiseController _owner;
            private bool _afterWindow, _done;
            internal AnswerHold(NoiseController owner) => _owner = owner;

            /// <summary>The window is about to change: say the gathered answer once it has settled.</summary>
            public void AfterTheWindowCloses() => _afterWindow = true;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (_afterWindow && _owner._utteranceDepth == 1) _owner._utteranceAfterWindow = true;
                _owner.EndUtterance();
            }
        }

        /// <summary>Set for the outermost scope's end only: deliver its utterance after the window settles.</summary>
        private bool _utteranceAfterWindow;

        private UtteranceScope OneUtterance(string? afterWhatIsSaidUnder = null)
        {
            if (_utteranceDepth++ == 0) { _utterance = new(); _utteranceAfterUnder = afterWhatIsSaidUnder; _utteranceRefused = null; }
            return new UtteranceScope(this);
        }

        private readonly struct UtteranceScope : IDisposable
        {
            private readonly NoiseController _owner;
            public UtteranceScope(NoiseController owner) => _owner = owner;
            public void Dispose() => _owner?.EndUtterance();
        }

        /// <summary>
        /// The outermost action ended: say what it gathered, once. One sentence
        /// goes out exactly as it would have alone. Several are joined in the
        /// order they were said, under the first one's subject; a part this
        /// verbosity would not speak is left out of the joined text (and
        /// traced), as the screen reader would have dropped it on its own.
        /// When no part would be spoken, the joined text goes out at the most
        /// important level among them, so the speaker records it as held back
        /// exactly once. Words are never added: the parts are the lexicon's own.
        /// </summary>
        private void EndUtterance()
        {
            if (--_utteranceDepth > 0) return;
            var parts = _utterance ?? new();
            _utterance = null;
            string? afterUnder = _utteranceAfterUnder;
            _utteranceAfterUnder = null;
            bool afterWindow = _utteranceAfterWindow;
            _utteranceAfterWindow = false;
            var journal = _journalAfterAction;
            _journalAfterAction = null;
            if (journal != null) foreach (var record in journal) record();
            var refused = (IReadOnlyList<(string EngineId, Unavailability Reason)>?)_utteranceRefused ?? Array.Empty<(string, Unavailability)>();
            _utteranceRefused = null;
            // Before this action's own sentence: a queued mode sentence that now
            // states something false is retired, so this sentence's interrupt
            // cannot rescue it (repair round 1, B2).
            CheckModeNotice("what it stated changed");
            if (parts.Count == 0) return;
            var heard = parts.Count == 1 ? parts : parts.Where(p => (int)p.Level <= (int)_speaker.Verbosity).ToList();
            foreach (var held in parts.Except(heard))
                System.Diagnostics.Trace.WriteLine($"NoiseController: left out of the action's utterance at this verbosity ({held.Level}): '{held.Text}'");
            var said = heard.Count > 0 ? heard : parts;
            string text = said.Count == 1 ? said[0].Text : string.Join(" ", said.Select(p => p.Text.Trim()));
            var level = said.Min(p => p.Level);
            // A mode change's sentence follows the mode word, never cuts it (#1117),
            // and waits for a surface that says the mode change later (the menu).
            if (afterUnder != null) DeliverModeNotice(new ModeNotice(text, afterUnder, level, refused, SlotFacts()));
            // A dialog's answer as its window closes (bench round, item 2): said
            // once the window has gone and focus has landed, queued behind the
            // reader's own announcement of where it landed.
            else if (afterWindow && AfterWindowSettles is { } later)
            {
                var subject = said[0].Subject;
                later(() => Deliver(text, subject, level, queued: true));
            }
            // Queued only when every part was: an answer to a press still interrupts.
            else if (said.All(p => p.Queued)) Deliver(text, said[0].Subject, level, queued: true);
            else Deliver(text, said[0].Subject, level, queued: false);
        }

        private void Deliver(string text, string subject, VerbosityLevel level, bool queued)
        {
            if (!queued)
            {
                // An answer to an operator's action: a surface's late reply to an
                // older one must not interrupt it (repair round 3).
                _operatorActions++;
                _speaker.Say(text, subject, level);
                return;
            }
            _speaker.SayAfter(text, subject, level);
            if (subject is SpeechSubject.NoiseHelperState or SpeechSubject.NoiseRadioSibling or SpeechSubject.NoiseModeStart) _noticesOwed.Add(subject);
        }

        /// <summary>
        /// A model the mode's rule chose in place of the one asked for or held
        /// (NNR in CW), journalled under the road that caused it and after that
        /// road's own entry: choosing NNR in CW was journalled "from mode change",
        /// before the switch it belonged to (#1118). Inside an action it waits for
        /// the action to end (<see cref="_journalAfterAction"/>); outside one it is
        /// recorded at once.
        /// </summary>
        /// <param name="why">Why the rule chose that model, as the entry's label (Sprint 49 Track N2); empty for none.</param>
        private void JournalModelAdmission(string source, string engineId, string from, string to, string why = "")
        {
            void Record() => NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.Model, source, true,
                new[] { new NoiseJournalChange(engineId, "model", from, to) }, why));
            if (_utteranceDepth > 0) (_journalAfterAction ??= new()).Add(Record);
            else Record();
        }

        /// <summary>
        /// The CW model the measured rule picks, with one trace line saying why
        /// (Sprint 49 Track N2): the figure read against the line, or the
        /// unmeasured default. The figure is read once, so the trace and the
        /// choice cannot disagree. Every road that asks the rule asks here.
        /// Since Sprint 49 Track K2 the figure is the median of the last few
        /// measurements, and the line says of how many.
        /// </summary>
        private (string Model, bool Measured, bool Fast, string Why) ChooseCwModel(string road)
        {
            var cost = _cwCost.ReadPremiumCw();
            var (model, measured, fast) = NoiseModeRules.ChooseCwModel(cost.Ms);
            string why = NoiseModeRules.CwModelReason(cost.Ms, cost.Measurements);
            System.Diagnostics.Trace.WriteLine($"NoiseController: the CW model rule chose {model} ({road}): {why}");
            return (model, measured, fast, why);
        }

        /// <summary>As <see cref="Say(string, string, VerbosityLevel)"/>, but after whatever is being said, never cutting it (<see cref="INoiseSpeaker.SayAfter"/>).</summary>
        private void SayAfter(string text, string subject, VerbosityLevel level = VerbosityLevel.Terse)
        {
            if (string.IsNullOrWhiteSpace(text) || _quiet > 0) return;
            if (_utterance != null) { _utterance.Add((text, subject, level, true)); return; }
            Deliver(text, subject, level, queued: true);
        }

        /// <summary>
        /// The queued notices nobody asked for that may still be unheard, by
        /// subject: a slot's or the helper's (a failed start, the helper's cap,
        /// the helper back) and the radio's sibling observation ("The radio turned
        /// Standard NR off."). Each is about ONE picture of the noise settings and
        /// is retired the moment that picture changes (repair round 2 of the
        /// Track E follow-up for the helper's; repair round 2 of follow-up 2 for
        /// the sibling's, after a preset turned the engine back on and the queued
        /// "off" was rescued after the preset's summary). Since #1117 a mode
        /// change's own sentence is queued too, behind the mode word, on
        /// <see cref="SpeechSubject.NoiseModeStart"/>: it describes the computer
        /// slots as the new group left them. These are the only queued noise
        /// sentences, and every one has this owner.
        /// </summary>
        private readonly HashSet<string> _noticesOwed = new(StringComparer.Ordinal);

        /// <summary>
        /// The noise settings changed (an engine chosen, swapped or turned off, a
        /// selection that now processes, a mode group entered, a preset, Escape, a
        /// disconnect): every queued notice about the old picture is obsolete.
        /// Called before the change's own sentence, so the interrupt that sentence
        /// makes cannot rescue the old notice behind it. Never silences anything
        /// else, and does nothing when nothing is owed.
        /// </summary>
        /// <param name="why">Trace prose.</param>
        /// <param name="modeNoticeToo">
        /// False only for a selection that now processes: that makes a queued mode
        /// sentence ("Back in CW: ..., NNR on.") truer, not obsolete, and it can
        /// arrive within milliseconds, before the mode word has even finished
        /// (#1117).
        /// </param>
        private void RetireHelperNotice(string why, bool modeNoticeToo = true)
        {
            RetireNotice(SpeechSubject.NoiseHelperState, why);
            RetireNotice(SpeechSubject.NoiseRadioSibling, why);
            if (modeNoticeToo) RetireModeNotice(why);
        }

        /// <summary>
        /// A queued or held mode-change sentence (#1117) is obsolete: the computer
        /// slots it described were changed by the operator, a preset, Escape,
        /// another mode group or a disconnect, or something it states is no longer
        /// true (<see cref="CheckModeNotice"/>). A radio switch alone leaves it true.
        /// </summary>
        private void RetireModeNotice(string why)
        {
            _modeNotice = null;
            if (_heldModeNotice != null)
            {
                _heldModeNotice = null;
                System.Diagnostics.Trace.WriteLine("NoiseController: dropped the held mode-change sentence (" + why + ")");
            }
            RetireNotice(SpeechSubject.NoiseModeStart, why);
            // The menu's sentence carries the noise answer joined to it (repair round 2).
            RetireNotice(SpeechSubject.ModeChoice, why);
        }

        // ────────────────────────────────────────────────────────────────
        //  The mode-change sentence: what it states, and a surface that says
        //  the mode change after the radio confirmed it (#1117, repair rounds 1 and 2)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A mode change's one utterance, with the facts it states: <paramref name="SlotFacts"/>
        /// (<see cref="SlotFacts"/>, read when it was composed) and, for every
        /// engine it said could not run, the reason it SAID (repair round 2: a
        /// reason probed again later could already be another one).
        /// </summary>
        private sealed record ModeNotice(string Text, string Subject, VerbosityLevel Level,
            IReadOnlyList<(string EngineId, Unavailability Reason)> Refused, string SlotFacts);

        /// <summary>The queued mode sentence that may still be unheard, while it is owed (<see cref="_noticesOwed"/>).</summary>
        private ModeNotice? _modeNotice;

        /// <summary>A mode sentence held for a surface that says the mode change itself, later (<see cref="HoldModeAnswer"/>).</summary>
        private ModeNotice? _heldModeNotice;
        private int _modeAnswerHolds;

        /// <summary>
        /// Which connection the holds belong to. A disconnect clears every hold
        /// and moves this on, so a hold from before it, released late, can never
        /// release a hold taken after it (repair round 2, Codex).
        /// </summary>
        private int _modeAnswerHoldGeneration;

        /// <summary>
        /// The engines the running action said could not run, with the reason as
        /// it was said (<see cref="SpeakUnavailable"/>, and DeepFilterNet3 when a
        /// voice starting point falls back to RNNoise because of it).
        /// </summary>
        private List<(string EngineId, Unavailability Reason)>? _utteranceRefused;

        /// <summary>The longest a hold lasts if its surface never releases it; the menu releases it about half a second after the click.</summary>
        public const int ModeAnswerHoldMs = 3000;

        /// <summary>Record, for the action's sentence, that it says <paramref name="engineId"/> cannot run for <paramref name="reason"/>.</summary>
        private void NoteSaidCannotRun(string engineId, Unavailability reason)
        {
            if (_utterance != null) (_utteranceRefused ??= new()).Add((engineId, reason));
        }

        /// <summary>
        /// The computer slots as a mode sentence states them: each slot on or off,
        /// its chosen engine and every setting of that engine (the sentences name
        /// NNR's model and DeepFilterNet3's limit). Repair round 1, B2 (Codex).
        /// </summary>
        private string SlotFacts()
        {
            var facts = new System.Text.StringBuilder();
            if (_pipeline() is { } p)
                foreach (var slot in new[] { p.SpectralSlot, p.NeuralSlot })
                {
                    facts.Append(slot.Enabled ? "on " : "off ").Append(slot.ChosenEngineId);
                    if (_catalog.Find(slot.ChosenEngineId) is { } e)
                        foreach (var d in e.Settings)
                        {
                            try
                            {
                                var v = slot.GetSetting(e.Id, d.Name);
                                facts.Append(' ').Append(d.Name).Append('=').Append(v.Choice ?? v.Number.ToString("R", CultureInfo.InvariantCulture));
                            }
                            catch (ArgumentException) { }
                        }
                    facts.Append(';');
                }
            return facts.ToString();
        }

        /// <summary>
        /// Whether everything a mode sentence states still holds: the slots as it
        /// stated them, and every engine it said could not run still unable to,
        /// for the reason it gave. The reasons are compared with what is true
        /// NOW; the recorded ones are those the sentence was composed from, never
        /// a second probe (repair round 2: a helper recovering between composing
        /// and recording left a "not running" sentence recorded as "runs", so
        /// NNR's later processing did not retire it).
        /// </summary>
        private bool StillTrue(ModeNotice notice) =>
            SlotFacts() == notice.SlotFacts
            && notice.Refused.All(r => _catalog.Find(r.EngineId) is not { } e || SafeAvailability(e) == r.Reason);

        /// <summary>
        /// Retire the queued (or held) mode sentence if anything it states is no
        /// longer true, and keep it while all of it still holds, as a selection
        /// that starts the engine it named on does. Called at the end of every
        /// action, before that action's own sentence, and on every result nobody
        /// asked for (a slot's selection, the helper's state).
        /// </summary>
        private void CheckModeNotice(string why)
        {
            var notice = _heldModeNotice ?? (_modeNotice is { } owed && _noticesOwed.Contains(owed.Subject) ? owed : null);
            if (notice == null || StillTrue(notice)) return;
            RetireModeNotice(why);
        }

        private void DeliverModeNotice(ModeNotice notice)
        {
            if (_modeAnswerHolds > 0)
            {
                _heldModeNotice = notice;
                System.Diagnostics.Trace.WriteLine("NoiseController: holding the mode-change sentence for the surface that says the mode: '" + notice.Text + "'");
                return;
            }
            Deliver(notice.Text, notice.Subject, notice.Level, queued: true);
            _modeNotice = notice;
        }

        /// <summary>
        /// For a surface that writes a mode at the operator's action but says it
        /// later (the Slice menu's mode rows, once the menu has closed): the noise
        /// controller's answer to the mode change is held. The surface either
        /// takes it, to say it joined to its own sentence
        /// (<see cref="IModeAnswerHold.TakeJoined"/>), or disposes the hold, and
        /// the answer is then queued behind whatever is being said. A newer mode
        /// group, any change to what the held sentence states, or a disconnect
        /// drops it. Holds nest; one never released ends on its own after
        /// <see cref="ModeAnswerHoldMs"/>.
        /// </summary>
        public IModeAnswerHold HoldModeAnswer()
        {
            _modeAnswerHolds++;
            var hold = new ModeAnswerHold(this, _modeAnswerHoldGeneration, _operatorActions);
            hold.Expiry = _scheduler.After(TimeSpan.FromMilliseconds(ModeAnswerHoldMs), hold.Dispose);
            return hold;
        }

        /// <summary>A disconnect: no hold outlives the connection it was taken on (repair round 2).</summary>
        private void ClearModeAnswerHolds()
        {
            _modeAnswerHolds = 0;
            _modeAnswerHoldGeneration++;
            _heldModeNotice = null;
        }

        /// <summary>One hold ends. Returns the held sentence when this was the last hold and what it states still holds; null otherwise.</summary>
        private ModeNotice? EndModeAnswerHold(int generation)
        {
            if (generation != _modeAnswerHoldGeneration || _modeAnswerHolds == 0 || --_modeAnswerHolds > 0) return null;
            if (_heldModeNotice is not { } held) return null;
            _heldModeNotice = null;
            if (!StillTrue(held))
            {
                System.Diagnostics.Trace.WriteLine("NoiseController: dropped the held mode-change sentence (what it stated changed while it was held)");
                return null;
            }
            return held;
        }

        private void ReleaseModeAnswer(int generation)
        {
            if (EndModeAnswerHold(generation) is { } held) DeliverModeNotice(held);
        }

        /// <summary>
        /// The surface takes the held answer to say it joined to its own sentence,
        /// under <paramref name="subject"/>: one utterance, so nothing of ours
        /// waits queued behind the surface's sentence to be withdrawn if that
        /// sentence's own cancel lands on it (the NVDA race the harness saw on the
        /// menu routes). The controller still owns it under that subject and
        /// retires it when what it states changes.
        /// </summary>
        private (string Text, VerbosityLevel Level)? TakeModeAnswer(int generation, string subject)
        {
            if (EndModeAnswerHold(generation) is not { } held) return null;
            _modeNotice = held with { Subject = subject };
            _noticesOwed.Add(subject);
            System.Diagnostics.Trace.WriteLine("NoiseController: the mode-change sentence is said joined to the surface's own: '" + held.Text + "'");
            return (held.Text, held.Level);
        }

        /// <summary>
        /// How many operator actions have been answered (repair round 3): every
        /// key the operator presses, noted by the main window, and every noise
        /// action whose sentence answers it (<see cref="Deliver"/>, an
        /// interrupt). A hold remembers the count it was taken at.
        /// </summary>
        private long _operatorActions;

        /// <summary>
        /// The operator did something after a surface took a hold (the main
        /// window calls this for every key pressed): every hold taken before now
        /// is outlived, so the surface's late reply is dropped rather than
        /// interrupting the newer action's answer (#1117, repair round 3). The
        /// held noise answer is not dropped by this; it is still released, queued,
        /// behind whatever the newer action says.
        /// </summary>
        /// <param name="why">Trace prose.</param>
        public void NoteOperatorAction(string why)
        {
            _operatorActions++;
            if (_modeAnswerHolds > 0)
                System.Diagnostics.Trace.WriteLine("NoiseController: a pending mode reply is outlived (" + why + ")");
        }

        private sealed class ModeAnswerHold : IModeAnswerHold
        {
            private readonly NoiseController _owner;
            private readonly int _generation;
            private readonly long _actionsAtHold;
            private bool _released;
            public IDisposable? Expiry;
            public ModeAnswerHold(NoiseController owner, int generation, long actionsAtHold)
            {
                _owner = owner; _generation = generation; _actionsAtHold = actionsAtHold;
            }

            public bool OutlivedByANewerAnswer => _owner._operatorActions != _actionsAtHold;

            public (string Text, VerbosityLevel Level)? TakeJoined(string subject)
            {
                if (_released) return null;
                _released = true;
                Expiry?.Dispose();
                return _owner.TakeModeAnswer(_generation, subject);
            }

            public void Dispose()
            {
                if (_released) return;
                _released = true;
                Expiry?.Dispose();
                _owner.ReleaseModeAnswer(_generation);
            }
        }

        /// <summary>
        /// Only the radio's own noise state changed (one of its switches written,
        /// the slice the radio reads changed): the sibling observation is obsolete,
        /// and a computer slot's notice is still true.
        /// </summary>
        private void RetireSiblingNotice(string why) => RetireNotice(SpeechSubject.NoiseRadioSibling, why);

        private void RetireNotice(string subject, string why)
        {
            if (!_noticesOwed.Remove(subject)) return;
            _speaker.Retire(subject, why);
            System.Diagnostics.Trace.WriteLine("NoiseController: retired the queued noise notice on " + subject + " (" + why + ")");
        }

        private bool Chatty => _speaker.Verbosity >= VerbosityLevel.Chatty;

        /// <summary>A number with its unit at Chatty and bare at Terse ("25 decibels" / "25").</summary>
        private string WithUnit(string number, string? unitKey) =>
            unitKey == null || !Chatty ? number : number + " " + Lexicon.Get(unitKey);

        private void NoRadio() => Say(Lexicon.Get("connect.command_needs_radio"), SpeechSubject.NoiseUnavailable, VerbosityLevel.Critical);

        // ────────────────────────────────────────────────────────────────
        //  Switching
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The one switch every surface calls: a radio engine flips; a
        /// computer engine that is on turns off; a computer engine whose slot
        /// holds another engine switches to the pressed one and says what it
        /// replaced (S5); a computer engine whose slot is off turns on. One
        /// press never silences a family by surprise.
        /// </summary>
        public void Toggle(string id, string source)
        {
            using var oneUtterance = OneUtterance();
            using var ownedRx = OwnRx(source); // the layer's own receive change, for its Escape (#1150)
            var e = _catalog.Find(id);
            if (e == null) return;
            // An engine this version cannot run is refused and never becomes the
            // arrows' engine (Sprint 49 Track H2 bench round, item 9; the transmit
            // twin was seen at the bench, and receive had the same line).
            if (e.Side == DenoiseSide.Radio || e.Factory != null) { Current = id; CurrentLeverIndex = 0; }
            if (e.Side == DenoiseSide.Radio) { ToggleRadio(e, source); return; }
            bool on = IsOn(id);
            if (on) TurnComputerOff(e, source, speak: true);
            else TurnComputerOn(e, source, speak: true, model: null);
        }

        /// <summary>Turn an engine on (or leave it on), as Home's On checkbox and a menu's check item ask.</summary>
        public void SetOn(string id, bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            using var ownedRx = OwnRx(source); // the layer's own receive change, for its Escape (#1150)
            if (IsOn(id) == on) { Current = id; SpeakSwitchState(id); return; }
            Toggle(id, source);
        }

        private void SpeakSwitchState(string id) =>
            Say(L(IsOn(id) ? "audio.noise_surface.on" : "audio.noise_surface.off", ("engine", NameOf(id))), SpeechSubject.NoiseEngineSwitch);

        /// <summary>The refusal a radio engine's write would meet here, or null: the capability ladder, then the CW-only rule.</summary>
        private string? RadioRefusal(DenoiseEngineEntry e, INoiseRadio r, out bool cwOnly)
        {
            cwOnly = false;
            if (e.Capability is RadioCapability cap && r.CapabilityRefusal(cap) is string why) return why;
            if (e.CwOnly && NoiseModeRules.GroupOf(r.Mode) != NoiseModeGroup.Cw) { cwOnly = true; return ""; }
            return null;
        }

        private bool RefuseRadio(DenoiseEngineEntry e, INoiseRadio r)
        {
            string? why = RadioRefusal(e, r, out bool cwOnly);
            if (why == null) return false;
            string name = Lexicon.Get(e.LabelKey);
            // The ladder's reason usually names the engine already ("Neural NR is
            // not in this radio's subscription"); then it is said as it stands,
            // so the name is not heard twice.
            string reason = why.TrimEnd('.');
            string key = reason.Contains(name, StringComparison.OrdinalIgnoreCase)
                ? "audio.noise_surface.refused_radio_named" : "audio.noise_surface.refused_radio";
            Say(cwOnly ? L("audio.noise_surface.cw_only", ("engine", name))
                       : L(key, ("engine", name), ("reason", reason), ("short", Marker(e.Id) ?? why)),
                SpeechSubject.NoiseUnavailable);
            return true;
        }

        private void ToggleRadio(DenoiseEngineEntry e, string source)
        {
            var r = _radio();
            if (r == null) { NoRadio(); return; }
            if (RefuseRadio(e, r)) return;
            bool was = IsOn(e.Id);
            WriteRadioSwitch(e, r, !was, source, byApp: false);
            Say(L(was ? "audio.noise_surface.off" : "audio.noise_surface.on", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseEngineSwitch);
            if (!was) WatchSiblings(e, r);
        }

        private void WriteRadioSwitch(DenoiseEngineEntry e, INoiseRadio r, bool on, string source, bool byApp)
        {
            FollowActiveSlice();
            // Every radio switch written (the layer, the brackets, a preset, Escape)
            // makes a queued sibling observation about the old switches obsolete.
            RetireSiblingNotice("a radio noise switch was written");
            bool was = IsOn(e.Id);
            r.WriteSwitch(e, on);
            _switchShadow[e.Id] = (on, _clock() + ShadowMs, r.SliceIdentity);
            NoteRadioTouched(e.Id, level: false);
            if (was != on)
                NoiseSettingsJournal.Record(NoiseJournalKind.Switch, source, e.Id, "", Words(was), Words(on), byApp);
        }

        private static string Words(bool on) => on ? "on" : "off";

        /// <summary>
        /// Open fact 1: nobody knows whether the radio switches one of its own
        /// engines off when another goes on. If the status shows it did, say
        /// so once (design part six), so a sibling never goes quiet unannounced.
        /// </summary>
        private void WatchSiblings(DenoiseEngineEntry turnedOn, INoiseRadio r)
        {
            var before = _catalog.Entries.Where(x => x.Side == DenoiseSide.Radio && x.Id != turnedOn.Id && x.Group == turnedOn.Group && IsOn(x.Id))
                .Select(x => x.Id).ToList();
            if (before.Count == 0) return;
            // The observation belongs to the slice it was taken on (repair
            // round 4): the adapter's reads follow whatever slice is active
            // when the check fires, so after a jump it would compare this
            // slice's "before" with another slice's "after" and announce a
            // change the radio never made. Taken after IsOn above, which has
            // already reconciled the radio's actual slice.
            object? observedSlice = r.SliceIdentity;
            _scheduler.After(TimeSpan.FromMilliseconds(SiblingCheckMs), () =>
            {
                if (_radio() != r) return;
                if (!ReferenceEquals(r.SliceIdentity, observedSlice)) return; // another slice now: discarded, silently
                // One firing, one utterance (repair round 1, Codex): with two
                // siblings off, the second sentence cut the first. And it is a
                // result nobody asked for, so it waits behind whatever is being
                // said (as a slot's failed start does) rather than cutting the
                // answer to the press: 1.5 s does not prove that answer is over.
                using var oneUtterance = OneUtterance();
                foreach (string id in before)
                {
                    var e = _catalog.Find(id)!;
                    if (SwitchShadow(id, r, out _)) continue; // we changed it ourselves
                    if (!r.ReadSwitch(e))
                    {
                        // The radio did it in answer to our write, so Escape
                        // counts it among the layer's changes and puts it back.
                        NoteRadioTouched(id, level: false);
                        SayAfter(L("audio.noise_surface.radio_turned_off", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseRadioSibling);
                    }
                }
            });
        }

        private void TurnComputerOff(DenoiseEngineEntry e, string source, bool speak)
        {
            var slot = SlotFor(e);
            if (slot == null) { NoRadio(); return; }
            RetireHelperNotice("a computer noise engine turned off");
            slot.Enabled = false;
            NoiseSettingsJournal.Record(NoiseJournalKind.Switch, source, e.Id, "", "on", "off");
            RememberModeState();
            _persist();
            if (speak) Say(L("audio.noise_surface.off", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseEngineSwitch);
        }

        /// <summary>
        /// Turn a computer engine on in its family's slot, replacing whatever
        /// engine of that family was on there. Returns the id it replaced.
        /// An engine that cannot run is still chosen, as an intent, and says why.
        /// </summary>
        private string? TurnComputerOn(DenoiseEngineEntry e, string source, bool speak, string? model, bool byApp = false)
        {
            using var oneUtterance = OneUtterance();
            var slot = SlotFor(e);
            if (slot == null) { NoRadio(); return null; }
            if (e.Factory == null)
            {
                if (speak) Say(L("audio.noise_surface.not_in_version", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseUnavailable);
                return null;
            }
            string? replaced = slot.Enabled && slot.ChosenEngineId != e.Id && slot.ChosenEngineId.Length > 0 ? slot.ChosenEngineId : null;
            RetireHelperNotice("a computer noise engine was chosen");
            if (model != null) SetModelQuietly(e, slot, model, source);
            // The CW model the mode's rule chose is said AFTER the switch, in the
            // same utterance (#1110): the answer to the press comes first.
            string? cwModel = ApplyModeRulesForChoice(e, slot, source);
            slot.Select(e.Id, out _);
            slot.Enabled = true;
            // The engine pushed out of the slot is part of the same change (#1018
            // item 2): one entry, the engine turned on first, then the one it
            // replaced, so the journal never shows a swap as a bare "off -> on".
            if (replaced == null)
                NoiseSettingsJournal.Record(NoiseJournalKind.Switch, source, e.Id, "", "off", "on", byApp);
            else
                NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.Switch, source, byApp, new[]
                {
                    new NoiseJournalChange(e.Id, "", "off", "on"),
                    new NoiseJournalChange(replaced, "", "on", "off"),
                }));
            RememberModeState();
            _persist();
            if (speak)
            {
                SpeakComputerOn(e, replaced);
                if (cwModel != null) SpeakCwModel(cwModel);
            }
            return replaced;
        }

        private void SpeakComputerOn(DenoiseEngineEntry e, string? replaced)
        {
            string name = Lexicon.Get(e.LabelKey);
            var reason = SafeAvailability(e);
            // The two refusals below still put this engine in the slot, so the
            // engine it replaced has stopped: they name it too (#1018 item 2).
            if (reason != Unavailability.None) { SpeakUnavailable(e, reason, kept: true, replaced: replaced); return; }
            if (_radio() is { PcAudioOn: false })
            {
                Say(WithReplaced(L("audio.noise_surface.pc_audio_off"), replaced), SpeechSubject.NoiseUnavailable);
                return;
            }
            string sentence = replaced == null
                ? L("audio.noise_surface.on", ("engine", name))
                : L("audio.noise_surface.on_replacing", ("engine", name), ("other", NameOf(replaced)));
            string? delay = DelayClause(e);
            if (delay != null) sentence += " " + delay;
            Say(sentence, SpeechSubject.NoiseEngineSwitch);
        }

        /// <summary>
        /// The receive half of "Say delays when choosing an engine" (ruled,
        /// departure D3): a helper engine adds its delay at Chatty, unless the
        /// operator has switched the setting off.
        /// </summary>
        private string? DelayClause(DenoiseEngineEntry e)
        {
            if (!Chatty || _config()?.NoiseSayDelays == false) return null;
            string? model = e.Settings.Any(d => d.Name == "model") ? SlotFor(e)?.GetSetting(e.Id, "model").Choice : null;
            int? ms = DenoiseEngineCatalog.ExpectedHelperDelayMs(e.Id, model);
            return ms == null ? null : L("audio.noise_surface.delay_clause", ("ms", ms.Value));
        }

        /// <summary>
        /// A refusal that still put an engine in its slot, followed by the
        /// engine it pushed out, when there was one (#1018 item 2). At every
        /// verbosity: losing an engine is never only a Chatty detail.
        /// </summary>
        private string WithReplaced(string sentence, string? replaced) =>
            replaced == null ? sentence : sentence + " " + L("audio.noise_surface.replaced_clause", ("other", NameOf(replaced)));

        /// <summary>The refusal ladder for a computer engine that cannot run: the engine's name, the reason, the engine it replaced if any, the action; the kept clause at Chatty only where the choice can come true.</summary>
        private void SpeakUnavailable(DenoiseEngineEntry e, Unavailability reason, bool kept, string? replaced = null,
            DenoiseDirection direction = DenoiseDirection.Receive)
        {
            bool transmit = direction == DenoiseDirection.Transmit;
            string name = transmit ? TxName(e.Id) : Lexicon.Get(e.LabelKey);
            string key = reason switch
            {
                Unavailability.LibraryMissing => "audio.noise_surface.unavailable.librarymissing",
                Unavailability.ModelMissing => "audio.noise_surface.unavailable.modelmissing",
                Unavailability.HelperNotRunning => "audio.noise_surface.unavailable.helpernotrunning",
                Unavailability.HelperMissing => "audio.noise_surface.unavailable.helpermissing",
                Unavailability.HelperVersionMismatch => "audio.noise_surface.unavailable.helperversionmismatch",
                Unavailability.NoSupportedGpu => "audio.noise_surface.unavailable.nosupportedgpu",
                Unavailability.GpuTooOld => "audio.noise_surface.unavailable.gputooold",
                Unavailability.NotDownloaded => "audio.noise_surface.unavailable.notdownloaded",
                Unavailability.LicenceNotAccepted => "audio.noise_surface.unavailable.licencenotaccepted",
                Unavailability.NotOn32Bit => "audio.noise_surface.unavailable.noton32bit",
                Unavailability.None => "audio.noise_surface.unavailable.none",
                _ => "audio.noise_surface.unavailable.initfailed",
            };
            string text = WithReplaced(L(key, ("engine", name)), transmit && replaced != null ? null : replaced);
            if (transmit && replaced != null) text += " " + L("audio.noise_surface.replaced_clause", ("other", TxName(replaced)));
            bool canComeTrue = reason is Unavailability.HelperNotRunning or Unavailability.HelperMissing or Unavailability.HelperVersionMismatch
                or Unavailability.LibraryMissing or Unavailability.ModelMissing or Unavailability.NotDownloaded
                or Unavailability.LicenceNotAccepted or Unavailability.NoSupportedGpu;
            if (kept && canComeTrue && Chatty) text += " " + L("audio.noise_surface.choice_kept");
            // A transmit refusal is not a receive mode sentence's business (#1117's facts are receive's).
            if (!transmit) NoteSaidCannotRun(e.Id, reason);
            Say(text, transmit ? SpeechSubject.TransmitNoiseUnavailable : SpeechSubject.NoiseUnavailable);
        }

        /// <summary>
        /// Turn a computer SLOT on or off without naming an engine (Home's On
        /// checkbox on a computer side). The first time computer noise
        /// reduction is turned on in a mode group, the group's starting point
        /// decides the neural slot's engine (ruling 5; plan ruling 6).
        /// </summary>
        public void SetSlotOn(DenoiseFamily family, bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            using var ownedRx = OwnRx(source); // the layer's own receive change, for its Escape (#1150)
            var slot = SlotOf(family);
            if (slot == null) { NoRadio(); return; }
            if (!on)
            {
                if (!slot.Enabled) return;
                var e = _catalog.Find(slot.ChosenEngineId);
                if (e != null) TurnComputerOff(e, source, speak: true);
                else { slot.Enabled = false; RememberModeState(); _persist(); }
                return;
            }
            if (slot.Enabled) { SpeakSwitchState(slot.ChosenEngineId); return; }
            var group = CurrentGroup;
            var memory = Memory?.For(group);
            if (family == DenoiseFamily.Neural && memory is { Known: false } && group != NoiseModeGroup.Digital)
            {
                ApplyStartingPoint(group, source, onlyNeural: true);
                return;
            }
            var chosen = _catalog.Find(slot.ChosenEngineId);
            if (chosen == null) return;
            Current = chosen.Id;
            TurnComputerOn(chosen, source, speak: true, model: null);
        }

        // ────────────────────────────────────────────────────────────────
        //  Choosing an engine for a slot (Home's Engine field), with the settle
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The engine a slot will use, chosen from a list the operator arrows
        /// through. Nothing is built until the choice has settled for
        /// <see cref="EngineSettleMs"/> (#769), so arrowing past an engine never
        /// starts it. If the slot is on, the settled choice swaps live under the
        /// warm-up cross-fade; if it is off, the choice is kept for when it is
        /// turned on.
        /// </summary>
        public void ChooseSlotEngine(DenoiseFamily family, string id, string source)
        {
            if (_settling.TryGetValue(family, out var pending)) { pending.Dispose(); _settling.Remove(family); }
            // The cursor is pointed later only if no layer session or T has moved
            // its ownership since (Track H2 repair round 1, item 2).
            long epoch = _cursorEpoch;
            _settling[family] = _scheduler.After(TimeSpan.FromMilliseconds(EngineSettleMs), () =>
            {
                _settling.Remove(family);
                ApplySlotChoice(family, id, source, epoch);
            });
        }

        /// <summary>The engine a slot's field should show: a pending, unsettled choice first, else the slot's own.</summary>
        public string SlotEngine(DenoiseFamily family) => _pendingChoice.TryGetValue(family, out var id) ? id : SlotOf(family)?.ChosenEngineId ?? "";

        private readonly Dictionary<DenoiseFamily, string> _pendingChoice = new();

        public void NoteSlotChoicePending(DenoiseFamily family, string id) => _pendingChoice[family] = id;

        private void ApplySlotChoice(DenoiseFamily family, string id, string source, long epoch)
        {
            using var oneUtterance = OneUtterance();
            using var ownedRx = OwnRx(source); // the layer's own receive change, for its Escape (#1150)
            _pendingChoice.Remove(family);
            var slot = SlotOf(family);
            var e = _catalog.Find(id);
            if (slot == null || e == null || e.Family != family || slot.ChosenEngineId == id) return;
            PointLater(id, DenoiseDirection.Receive, epoch);
            if (slot.Enabled) { TurnComputerOn(e, source, speak: true, model: null); return; }
            if (e.Factory == null)
            {
                Say(L("audio.noise_surface.not_in_version", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseUnavailable);
                return;
            }
            // Off: remembered for when it is turned on. Selecting builds, so a
            // choice is only selected once it has settled. The mode group
            // remembers it too (repair round 1, B5): a choice made with the
            // slot off is still this group's choice, and leaving and coming
            // back must not put the older engine back.
            slot.Select(id, out _);
            RememberModeState();
            _persist();
        }

        // ────────────────────────────────────────────────────────────────
        //  The 0 key: the computer's engines off, and back (question 17)
        // ────────────────────────────────────────────────────────────────

        public void ComputerOffAndBack(string source)
        {
            using var oneUtterance = OneUtterance();
            using var ownedRx = OwnRx(source); // the layer's own receive change, for its Escape (#1150)
            var p = _pipeline();
            if (p == null) { NoRadio(); return; }
            RetireHelperNotice("the computer's noise engines were switched off or back");
            var on = new List<(DenoiseFamily, string)>();
            if (p.SpectralSlot.Enabled) on.Add((DenoiseFamily.Spectral, p.SpectralSlot.ChosenEngineId));
            if (p.NeuralSlot.Enabled) on.Add((DenoiseFamily.Neural, p.NeuralSlot.ChosenEngineId));
            if (on.Count > 0)
            {
                foreach (var (family, id) in on) { SlotOf(family)!.Enabled = false; NoiseSettingsJournal.Record(NoiseJournalKind.Switch, source, id, "", "on", "off"); }
                _zeroRestore = on;
                RememberModeState(); _persist();
                Say(L("audio.noise_surface.zero_off", ("engines", JoinNames(on.Select(x => x.Item2)))), SpeechSubject.NoiseEngineSwitch);
                return;
            }
            if (_zeroRestore is { Count: > 0 } back)
            {
                foreach (var (family, id) in back)
                {
                    var slot = SlotOf(family)!;
                    if (slot.ChosenEngineId != id) slot.Select(id, out _);
                    slot.Enabled = true;
                    NoiseSettingsJournal.Record(NoiseJournalKind.Switch, source, id, "", "off", "on");
                }
                _zeroRestore = null;
                RememberModeState(); _persist();
                Say(L("audio.noise_surface.zero_on", ("engines", JoinNames(back.Select(x => x.EngineId)))), SpeechSubject.NoiseEngineSwitch);
                return;
            }
            Say(L("audio.noise_surface.zero_already"), SpeechSubject.NoiseEngineSwitch);
        }

        private string JoinNames(IEnumerable<string> ids)
        {
            var names = ids.Select(NameOf).ToList();
            if (names.Count <= 1) return names.FirstOrDefault() ?? "";
            return string.Join(", ", names.Take(names.Count - 1)) + " " + Lexicon.Get("audio.noise_surface.and") + " " + names[^1];
        }

        // ────────────────────────────────────────────────────────────────
        //  Results nobody asked for (E5)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A slot's result, raised by the audio thread on the NEXT receive
        /// buffer after the build (never at once; with no audio flowing it
        /// waits). A failed start speaks Noel's S15 sentence, verbatim, once
        /// per result: it stays true because a new selection really retries
        /// (pinned by NoiseSlotResultTests). A slot nobody used never reaches
        /// here: the slot holds a generation-0 failure back at the source.
        /// The listener marshals to the UI thread before calling.
        /// </summary>
        public void OnSelectionResult(DenoiseSelectionResult result)
        {
            // A selection that now processes makes an earlier failure untrue (repair round 2).
            // A selection that now processes keeps a mode sentence that named its
            // engine on, and retires one whose refusal it made false (repair round 1, B2).
            CheckModeNotice("a noise engine's selection result changed what it stated");
            if (result.Processing) { RetireHelperNotice("a noise engine started processing", modeNoticeToo: false); return; }
            if (!result.SlotStopped) return;
            // Queued behind whatever is being said (repair round 1): on every
            // refusal path that still puts an engine in the slot, the refusal
            // and its replaced-engine clause are spoken first and this follows,
            // instead of cutting them at their first word.
            SayAfter(L("audio.noise_surface.start_failed"), SpeechSubject.NoiseHelperState, VerbosityLevel.Critical);
        }

        /// <summary>
        /// The helper's unasked changes. Reaching the restart cap means the
        /// engines pass unprocessed audio until the operator turns an engine
        /// on again, which retries: S15's sentence is exactly true. Coming back
        /// after the cap names the engines running again. A restart that works
        /// is deliberately silent (ruled 2026-11-02).
        /// </summary>
        public void OnHelperStateChanged(WdspHelperStateChange change, IReadOnlyList<string> engines)
        {
            CheckModeNotice("the noise helper's state changed what it stated");
            // Unasked, like a slot's result: after whatever is being said (repair round 1).
            if (change == WdspHelperStateChange.CapReached)
                SayAfter(L("audio.noise_surface.start_failed"), SpeechSubject.NoiseHelperState, VerbosityLevel.Critical);
            else if (engines.Count > 0)
                SayAfter(L("audio.noise_surface.helper_back", ("engines", JoinNames(engines))), SpeechSubject.NoiseHelperState);
        }

        /// <summary>Forget the 0 key's "back" lists, both directions' (the layer closed; the operator moved on; Track H2 repair round 1, item 3).</summary>
        public void ForgetZero() { _zeroRestore = null; _txZeroRestore = null; }

        /// <summary>
        /// The noise layer closed (Track H2 repair round 1, items 2 and 3): the
        /// layer's direction goes back to receive, both 0-key lists are
        /// forgotten, and any cursor assignment still to come from work scheduled
        /// during the session is stale.
        /// </summary>
        public void EndLayerSession()
        {
            ForgetZero();
            LayerDirection = DenoiseDirection.Receive;
            _cursorEpoch++;
        }
    }
}
