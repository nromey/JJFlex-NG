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
    }

    /// <summary>The app's speaker: the screen reader, keyed by subject so a newer noise sentence covers an unheard older one.</summary>
    public sealed class ScreenReaderNoiseSpeaker : INoiseSpeaker
    {
        public VerbosityLevel Verbosity => ScreenReaderOutput.CurrentVerbosity;
        public void Say(string text, string subject, VerbosityLevel level) =>
            ScreenReaderOutput.Speak(text, SpeechIntent.Latest, level, subject: subject);
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

    /// <summary>What kind of thing a lever adjusts.</summary>
    public enum NoiseLeverKind { RadioLevel, Strength, Setting, ListenRemoved }

    /// <summary>
    /// One lever of one engine, as every surface sees it: the radio's level,
    /// a computer engine's Strength, one of its descriptor's settings, or the
    /// Listen to what was removed switch. The value lives in the radio or the
    /// slot; this only says where.
    /// </summary>
    public sealed record NoiseLever(string EngineId, string Key, string LabelKey, NoiseLeverKind Kind,
        EngineSettingDescriptor? Descriptor = null, bool Advanced = false)
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
            INoiseScheduler scheduler, INnrCwCostSource? cwCost = null, Func<long>? clock = null)
        {
            _catalog = catalog; _radio = radio; _pipeline = pipeline; _config = config; _persist = persist;
            _speaker = speaker; _intents = intents; _scheduler = scheduler;
            _cwCost = cwCost ?? new UnmeasuredNnrCwCost();
            _clock = clock ?? (() => Environment.TickCount64);
        }

        public DenoiseEngineCatalog Catalog => _catalog;

        /// <summary>The engine the noise layer last touched; its levers are what Up, Down and Tab work on.</summary>
        public string? Current { get; private set; }

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

        private static Unavailability SafeAvailability(DenoiseEngineEntry e)
        {
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
            _speaker.Say(text, subject, level);
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
            var e = _catalog.Find(id);
            if (e == null) return;
            Current = id; CurrentLeverIndex = 0;
            if (e.Side == DenoiseSide.Radio) { ToggleRadio(e, source); return; }
            bool on = IsOn(id);
            if (on) TurnComputerOff(e, source, speak: true);
            else TurnComputerOn(e, source, speak: true, model: null);
        }

        /// <summary>Turn an engine on (or leave it on), as Home's On checkbox and a menu's check item ask.</summary>
        public void SetOn(string id, bool on, string source)
        {
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
                foreach (string id in before)
                {
                    var e = _catalog.Find(id)!;
                    if (SwitchShadow(id, r, out _)) continue; // we changed it ourselves
                    if (!r.ReadSwitch(e))
                    {
                        // The radio did it in answer to our write, so Escape
                        // counts it among the layer's changes and puts it back.
                        NoteRadioTouched(id, level: false);
                        Say(L("audio.noise_surface.radio_turned_off", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseEngineSwitch);
                    }
                }
            });
        }

        private void TurnComputerOff(DenoiseEngineEntry e, string source, bool speak)
        {
            var slot = SlotFor(e);
            if (slot == null) { NoRadio(); return; }
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
            var slot = SlotFor(e);
            if (slot == null) { NoRadio(); return null; }
            if (e.Factory == null)
            {
                if (speak) Say(L("audio.noise_surface.not_in_version", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseUnavailable);
                return null;
            }
            string? replaced = slot.Enabled && slot.ChosenEngineId != e.Id && slot.ChosenEngineId.Length > 0 ? slot.ChosenEngineId : null;
            if (model != null) SetModelQuietly(e, slot, model);
            ApplyModeRulesForChoice(e, slot, speak);
            slot.Select(e.Id, out _);
            slot.Enabled = true;
            NoiseSettingsJournal.Record(NoiseJournalKind.Switch, source, e.Id, "", "off", "on", byApp);
            RememberModeState();
            _persist();
            if (speak) SpeakComputerOn(e, replaced);
            return replaced;
        }

        private void SpeakComputerOn(DenoiseEngineEntry e, string? replaced)
        {
            string name = Lexicon.Get(e.LabelKey);
            var reason = SafeAvailability(e);
            if (reason != Unavailability.None) { SpeakUnavailable(e, reason, kept: true); return; }
            if (_radio() is { PcAudioOn: false })
            {
                Say(L("audio.noise_surface.pc_audio_off"), SpeechSubject.NoiseUnavailable);
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

        /// <summary>The refusal ladder for a computer engine that cannot run: the engine's name, the reason, the action; the kept clause at Chatty only where the choice can come true.</summary>
        private void SpeakUnavailable(DenoiseEngineEntry e, Unavailability reason, bool kept)
        {
            string name = Lexicon.Get(e.LabelKey);
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
            string text = L(key, ("engine", name));
            bool canComeTrue = reason is Unavailability.HelperNotRunning or Unavailability.HelperMissing or Unavailability.HelperVersionMismatch
                or Unavailability.LibraryMissing or Unavailability.ModelMissing or Unavailability.NotDownloaded
                or Unavailability.LicenceNotAccepted or Unavailability.NoSupportedGpu;
            if (kept && canComeTrue && Chatty) text += " " + L("audio.noise_surface.choice_kept");
            Say(text, SpeechSubject.NoiseUnavailable);
        }

        /// <summary>
        /// Turn a computer SLOT on or off without naming an engine (Home's On
        /// checkbox on a computer side). The first time computer noise
        /// reduction is turned on in a mode group, the group's starting point
        /// decides the neural slot's engine (ruling 5; plan ruling 6).
        /// </summary>
        public void SetSlotOn(DenoiseFamily family, bool on, string source)
        {
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
            _settling[family] = _scheduler.After(TimeSpan.FromMilliseconds(EngineSettleMs), () =>
            {
                _settling.Remove(family);
                ApplySlotChoice(family, id, source);
            });
        }

        /// <summary>The engine a slot's field should show: a pending, unsettled choice first, else the slot's own.</summary>
        public string SlotEngine(DenoiseFamily family) => _pendingChoice.TryGetValue(family, out var id) ? id : SlotOf(family)?.ChosenEngineId ?? "";

        private readonly Dictionary<DenoiseFamily, string> _pendingChoice = new();

        public void NoteSlotChoicePending(DenoiseFamily family, string id) => _pendingChoice[family] = id;

        private void ApplySlotChoice(DenoiseFamily family, string id, string source)
        {
            _pendingChoice.Remove(family);
            var slot = SlotOf(family);
            var e = _catalog.Find(id);
            if (slot == null || e == null || e.Family != family || slot.ChosenEngineId == id) return;
            Current = id; CurrentLeverIndex = 0;
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
            var p = _pipeline();
            if (p == null) { NoRadio(); return; }
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
            if (result.Processing || !result.SlotStopped) return;
            Say(L("audio.noise_surface.start_failed"), SpeechSubject.NoiseHelperState, VerbosityLevel.Critical);
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
            if (change == WdspHelperStateChange.CapReached)
                Say(L("audio.noise_surface.start_failed"), SpeechSubject.NoiseHelperState, VerbosityLevel.Critical);
            else if (engines.Count > 0)
                Say(L("audio.noise_surface.helper_back", ("engines", JoinNames(engines))), SpeechSubject.NoiseHelperState);
        }

        /// <summary>Forget the 0 key's "back" list (the layer closed; the operator moved on).</summary>
        public void ForgetZero() => _zeroRestore = null;
    }
}
