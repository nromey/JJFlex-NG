using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Radios;

namespace JJFlexWpf
{
    public sealed record DenoiseSelectionResult(DenoiseDirection Direction, DenoiseFamily Family,
        string EngineId, bool Processing, string? Failure = null)
    {
        public bool SlotStopped { get; init; }
    }

    /// <summary>
    /// Control intent is published by reference; build/failure markers use
    /// atomic operations. Only audio owns running/warming banks. Builders transfer ownership through
    /// an interlocked mailbox; retirement transfers it out to a worker.
    /// The pipeline's process mark also protects this slot at disconnect.
    /// </summary>
    public sealed class PcDenoiseSlot
    {
        /// <summary>
        /// An engine's stored Strength and values. <paramref name="Live"/> is the
        /// values a RUNNING instance takes (every value but a rebuild setting's),
        /// worked out once on the control side when the record is made (repair
        /// round 2), so <see cref="ApplyParameters"/> walks an array on the audio
        /// thread with no enumerator, no LINQ and no allocation. Make one with
        /// <see cref="MakeParameters"/> whenever <paramref name="Values"/> changes;
        /// a <c>with</c> that changes only the Strength keeps the same Live.
        /// </summary>
        private sealed record Parameters(float Strength, IReadOnlyDictionary<string, EngineSettingValue> Values,
            KeyValuePair<string, EngineSettingValue>[] Live);

        /// <summary>A record for these values, with its live settings worked out here, off the audio thread.</summary>
        private Parameters MakeParameters(string id, float strength, IReadOnlyDictionary<string, EngineSettingValue> values)
        {
            var settings = SettingsFor(id);
            var live = new List<KeyValuePair<string, EngineSettingValue>>(values.Count);
            foreach (var pair in values)
                if (!settings.Any(s => s.Name == pair.Key && s.NeedsRebuild)) live.Add(pair);
            AddOmittedLiveDefaults(settings, values, live);
            return new Parameters(strength, values, live.ToArray());
        }

        /// <summary>
        /// Both directions (Sprint 49 Track H, repair round 2, R2, for transmit;
        /// Track N, #1147, for receive): the live array also carries every live
        /// setting the stored values OMIT, at its descriptor default. The store
        /// stays sparse (an untouched value is still not stored, #1019), but a
        /// running engine is told the default for a value a new record does not
        /// name, instead of keeping whatever was set before while the getter
        /// reads the default. On receive the record is a preset's engine entry,
        /// or Escape's: <see cref="RestoreSettings"/> already REPLACES that
        /// engine's stored values with the record's (an omitted value reads its
        /// default), so a receive preset replaces the live values of each engine
        /// it names, the store and the running engine alike; an engine it does
        /// not name keeps its own (part five: a preset slot holds its engine's
        /// whole settings).
        /// </summary>
        private void AddOmittedLiveDefaults(IReadOnlyList<EngineSettingDescriptor> settings,
            IReadOnlyDictionary<string, EngineSettingValue> values, List<KeyValuePair<string, EngineSettingValue>> live)
        {
            foreach (var d in settings)
                if (!d.NeedsRebuild && !values.ContainsKey(d.Name)) live.Add(new(d.Name, d.Default));
        }
        private sealed record Choice(long Generation, string Id, DenoiseDirection Direction = DenoiseDirection.Receive)
        {
            internal int BuildClaim;
            internal DenoiseEngineEntry? Entry;
            internal bool Force;
            internal string? Failure;
            internal int FailureReported;
        }
        private sealed record Built(Choice Choice, PerChannelDenoiserBank Bank)
        {
            internal Parameters? Applied;
            /// <summary>Made on the build worker, so the swap on the audio thread only publishes a reference.</summary>
            internal RunningBuild? Running;
        }
        /// <summary>
        /// The running bank, its id and the starting values it was built from,
        /// as ONE reference (repair round 5). Select reads it to tell whether
        /// arrowing back would keep an instance built with an out-of-date
        /// latency setting; the delay readout reads it to measure only the bank
        /// it names. Until round 5 the bank, the build and the id were three
        /// fields published one after another, so a readout taken during a swap
        /// to another engine could qualify the outgoing id and build and then
        /// read the incoming bank's delay. Made on the build worker (or in the
        /// constructor), never on the audio thread, which only swaps the
        /// reference. <paramref name="Bank"/> is null only after
        /// <see cref="DisposeOwned"/>, which keeps the id as it always did.
        /// </summary>
        private sealed record RunningBuild(string Id, IReadOnlyDictionary<string, EngineSettingValue> Initial,
            PerChannelDenoiserBank? Bank);
        private RunningBuild? _runningBuild;
        private readonly object _control = new();
        private readonly DenoiseEngineCatalog _catalog;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly ConcurrentDictionary<string, Parameters> _settings = new();
        private Choice _choice;
        private Built? _ready;
        private Built? _warming;
        private Parameters? _runningApplied;
        private int _warmSamples;
        private int _closed;
        private long _announced = -1;
        private int _enabled;
        private bool _wasActive;
        private float[] _nextAudio = Array.Empty<float>();
        public DenoiseFamily Family { get; }

        private readonly DenoiseDirection _direction;
        /// <summary>
        /// Which audio this slot cleans, fixed at construction (Sprint 49 Track
        /// H, item A): receive, or the microphone on transmit. One class serves
        /// both, so there is one chooser vocabulary, not two (ruled 2026-10-20).
        /// The catalog's factory is handed this direction, so a helper engine
        /// opens a channel of the matching kind. A transmit slot never warms up
        /// or cross-fades between engines: every change it would make waits for
        /// <see cref="SwapPermitted"/> (the unkey, item B), and is then a cut.
        /// </summary>
        public DenoiseDirection Direction => _direction;

        /// <summary>
        /// Transmit only (item B): asked once per buffer on the audio thread;
        /// while it answers false (the radio is keyed), the slot changes nothing
        /// anyone could hear. A delivered build waits in its mailbox, a failed
        /// choice leaves the running engine alone, and the swap happens on the
        /// first buffer it answers true. Null means always permitted. Ignored
        /// by a receive slot, whose changes go through the warm-up instead.
        /// </summary>
        internal Func<bool>? SwapPermitted { get; set; }

        /// <summary>
        /// Transmit only: the delay, in samples per channel, the last buffer
        /// this slot was given went out with; 0 when the slot passed it
        /// through untouched (off, nothing built). The transmit chain adds the
        /// two slots' figures for the conditioner's aligned residual monitor.
        /// </summary>
        internal int LastAppliedLatencySamples { get; private set; }

        /// <summary>Transmit only: whether the running engine's bank took the last buffer (on, permitted, built). False means it passed through untouched.</summary>
        internal bool LastBufferProcessed { get; private set; }

        /// <summary>Transmit only: the last buffer was processed and the engine itself produced every frame of it (none late, failed or latched). The chain's "it has recovered".</summary>
        internal bool LastBufferClean { get; private set; }
        public string ChosenEngineId => Volatile.Read(ref _choice).Id;
        public string RunningEngineId => Volatile.Read(ref _runningBuild)?.Id ?? "";

        private string _processingId = "";
        /// <summary>
        /// The engine that processed the last receive buffer this slot was given,
        /// or "" when that buffer went through unprocessed: the slot off, nothing
        /// built yet, a failed start, Captured noise with no profile, the spectral
        /// slot paused while a noise profile is being captured, or the engine
        /// passing audio through because it became unavailable (a helper that
        /// died). Written by the audio thread on every buffer, so it is what the
        /// operator actually hears processed. Not <see cref="RunningEngineId"/>,
        /// which is the bank the slot holds and survives every one of those gates
        /// (#1103 repair round 1, Codex: Captured noise kept its id with no
        /// profile and was read as working). Says nothing about buffers that never
        /// arrived: with no audio flowing it keeps the last buffer's answer, and
        /// a buffer too short to complete an engine frame keeps it too (it proves
        /// nothing). The most recent buffer's result, not a liveness guarantee.
        /// </summary>
        public string ProcessingEngineId => Volatile.Read(ref _processingId);
        /// <summary>
        /// What the running bank delays the audio by, in samples (engine plus any
        /// frame carry); 0 with nothing running. Says nothing about WHICH engine
        /// it is: a readout uses <see cref="CurrentLatencySamples"/>, which reads
        /// the engine and its delay from one snapshot.
        /// </summary>
        public int RunningLatencySamples => Volatile.Read(ref _runningBuild)?.Bank?.LatencySamples ?? 0;

        /// <summary>
        /// The running bank's delay in samples when it is engine <paramref name="id"/>
        /// built from the settings chosen now (#1019); 0 otherwise. One reference
        /// read, so the id, the build and the delay all belong to the same bank,
        /// and the delay is read once (repair round 5).
        /// </summary>
        public int CurrentLatencySamples(string id)
        {
            var run = Volatile.Read(ref _runningBuild);
            return run?.Bank != null && BuiltFromChoiceNow(run, id) ? run.Bank.LatencySamples : 0;
        }

        /// <summary>Tests only: the published snapshot's id, starting values and bank, read as one reference.</summary>
        internal (string Id, IReadOnlyDictionary<string, EngineSettingValue> Initial, PerChannelDenoiserBank? Bank)? RunningSnapshotForTest =>
            Volatile.Read(ref _runningBuild) is { } run ? (run.Id, run.Initial, run.Bank) : null;
        private int _hasBeenEnabled;
        /// <summary>True once the slot has been turned on, or restored with a remembered choice. Read by the audio thread (E5's hold), so volatile.</summary>
        public bool HasBeenEnabled { get => Volatile.Read(ref _hasBeenEnabled) != 0; private set => Volatile.Write(ref _hasBeenEnabled, value ? 1 : 0); }
        internal void RememberUse() => HasBeenEnabled = true;
        public bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set { if (value) HasBeenEnabled = true; Volatile.Write(ref _enabled, value ? 1 : 0); }
        }
        public event EventHandler<DenoiseSelectionResult>? SelectionResult;

        private static int _crossFadeOff;
        /// <summary>
        /// Whether an engine swap cross-fades from the old engine to the new
        /// (#873; Settings, Advanced, default on). Off is the switch for a
        /// slower machine: the swap is then a straight cut on a buffer
        /// boundary. App-wide, read by the audio thread once per swap.
        /// </summary>
        public static bool CrossFadeEnabled
        {
            get => Volatile.Read(ref _crossFadeOff) == 0;
            set => Volatile.Write(ref _crossFadeOff, value ? 0 : 1);
        }

        private int _listenRemoved;
        /// <summary>
        /// Listen to what was removed (Sprint 49 Track E, E13; ruled 2026-11-07,
        /// question 13): while true the slot's output is what its engine took
        /// out — the aligned input minus the processed output — instead of the
        /// cleaned audio. Never saved; the controller clears it when the layer
        /// or the expander is left and on every connect. Read by the audio
        /// thread once per buffer.
        /// </summary>
        public bool ListenToRemoved
        {
            get => Volatile.Read(ref _listenRemoved) != 0;
            set => Volatile.Write(ref _listenRemoved, value ? 1 : 0);
        }
        internal Action<string>? InstallHook { get; set; }
        /// <summary>Tests only: the bank the audio thread is running now.</summary>
        internal PerChannelDenoiserBank? RunningBankForTest => Volatile.Read(ref _runningBuild)?.Bank;
        internal bool BuildDelivered(string id)
        {
            var choice = Volatile.Read(ref _choice);
            return choice.Id == id && (ReferenceEquals(Volatile.Read(ref _ready)?.Choice, choice) ||
                ReferenceEquals(Volatile.Read(ref _warming)?.Choice, choice));
        }

        internal PcDenoiseSlot(DenoiseFamily family, string initialId, int sampleRate, int channels,
            DenoiseEngineCatalog catalog, DenoiseDirection direction = DenoiseDirection.Receive)
        {
            // The direction first: the default parameters below read it.
            _direction = direction;
            Family = family; _catalog = catalog; _channels = channels; _sampleRate = sampleRate;
            if (direction == DenoiseDirection.Receive)
            {
                // The shared delay's lines, allocated here and never on the audio thread (#873).
                _runPad = new PadLine(MaxPadSamples, channels);
                _warmPad = new PadLine(MaxPadSamples, channels);
                _corr = new float[channels];
                _retimeCorr = new float[channels];
            }
            // Every engine's default parameters, made here rather than on the audio thread (repair round 1).
            foreach (var row in catalog.Entries) DefaultParameters(row.Id);
            _choice = new Choice(0, initialId, direction);
            var entry = catalog.Find(initialId);
            _choice.Entry = entry;
            if (entry?.Factory != null && entry.Family == family && entry.Serves(Direction))
            {
                // Only today's two engines are initialised synchronously.
                // Subsequent choices (including model hashing) build on workers.
                _choice.BuildClaim = 1;
                PerChannelDenoiserBank? initial = null;
                try
                {
                    initial = BuildBank(entry, out var builtFrom);
                    if (!initial.IsAvailable)
                        throw new InvalidOperationException(initial.EngineFor(0).UnavailabilityDetail);
                    _runningBuild = new RunningBuild(initialId, builtFrom, initial);
                    initial = null;
                }
                catch (Exception ex)
                {
                    // This bank has never been published to the audio thread.
                    initial?.Dispose();
                    Volatile.Write(ref _choice.Failure, ex.Message);
                }
            }
        }

        /// <summary>
        /// An engine's stored parameters, or its defaults read live when nobody
        /// has touched it (#1019). Reading stores NOTHING: until Sprint 49 Track S
        /// this was a GetOrAdd, so the first read wrote the default into the
        /// store, every setup saved it, and a new default could never reach an
        /// engine the operator had never touched.
        /// </summary>
        private Parameters ParametersFor(string id) =>
            _settings.TryGetValue(id, out var p) ? p : DefaultParameters(id);

        /// <summary>The one empty, read-only value set every untouched engine shares.</summary>
        private static readonly IReadOnlyDictionary<string, EngineSettingValue> NoValues =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, EngineSettingValue>(new Dictionary<string, EngineSettingValue>());

        /// <summary>
        /// One immutable default <see cref="Parameters"/> per engine, kept APART from
        /// the store so reading still stores nothing (#1019). Built for every catalog
        /// engine in the constructor, off the audio thread (repair round 1): the
        /// audio thread reads an untouched engine's Strength every buffer, and
        /// <see cref="ApplyParameters"/> compares by reference, so this must hand
        /// back the same instance every time and allocate nothing.
        /// </summary>
        private readonly ConcurrentDictionary<string, Parameters> _defaults = new();

        /// <summary>An untouched engine's parameters: its default Strength from the one source, no values. The same instance on every call.</summary>
        private Parameters DefaultParameters(string id)
        {
            // TryGetValue first: GetOrAdd with a lambda that captures this slot
            // would allocate a delegate on every call, on the audio thread too.
            if (_defaults.TryGetValue(id, out var cached)) return cached;
            var defaultsLive = new List<KeyValuePair<string, EngineSettingValue>>();
            AddOmittedLiveDefaults(SettingsFor(id), NoValues, defaultsLive);
            var made = new Parameters(NoiseFloor.DefaultStrength(id, Direction), NoValues, defaultsLive.ToArray());
            return _defaults.GetOrAdd(id, made);
        }

        // ── What the TRANSMIT audio thread reads (Sprint 49 Track H, repair round 3) ──
        //
        // A whole setup must not reach the air partway through an over. Its
        // engine and on-or-off already wait for an unkeyed buffer; its LIVE
        // values (each engine's strength and settings) did not: the control side
        // wrote them into the store, and a keyed buffer read them from there.
        // So on transmit the audio thread reads a published view instead: the
        // values in force now, plus a whole replacement waiting to be adopted.
        // The control side is the only writer of both and never blocks the audio
        // thread (a compare-and-swap, no lock); the audio thread adopts the
        // waiting replacement only on an unkeyed buffer, by swapping in a view
        // the control side already built. A lone strength or live-setting
        // request still reaches the view in force at once (strength is ruled
        // live; the other levers follow TxNoiseChain.ContinuousLeversLiveWhileKeyed),
        // and is applied to the waiting replacement too, so the later request
        // still wins once it is adopted. Receive reads the store, as before.

        private sealed class TxView
        {
            public static readonly TxView Empty = new(new Dictionary<string, Parameters>(), null);
            public readonly Dictionary<string, Parameters> Current;
            public readonly Dictionary<string, Parameters>? Pending;
            /// <summary>The waiting replacement as the view in force, built here so adopting it allocates nothing on the audio thread.</summary>
            public readonly TxView? Adopted;
            public TxView(Dictionary<string, Parameters> current, Dictionary<string, Parameters>? pending)
            {
                Current = current; Pending = pending;
                Adopted = pending == null ? null : new TxView(pending, null);
            }
        }
        private TxView _txView = TxView.Empty;

        /// <summary>Transmit: what the audio thread uses for an engine now. Never allocates, never blocks.</summary>
        private Parameters ParametersForAudio(string id)
        {
            if (_direction != DenoiseDirection.Transmit) return ParametersFor(id);
            return Volatile.Read(ref _txView).Current.TryGetValue(id, out var p) ? p : DefaultParameters(id);
        }

        /// <summary>A lone live request: applied to the view in force and to any waiting replacement.</summary>
        private void PublishLive(string id, Func<Parameters, Parameters> change)
        {
            if (_direction != DenoiseDirection.Transmit) return;
            while (true)
            {
                var was = Volatile.Read(ref _txView);
                var current = new Dictionary<string, Parameters>(was.Current);
                current[id] = change(was.Current.TryGetValue(id, out var c) ? c : DefaultParameters(id));
                Dictionary<string, Parameters>? pending = null;
                if (was.Pending != null)
                {
                    pending = new Dictionary<string, Parameters>(was.Pending);
                    pending[id] = change(was.Pending.TryGetValue(id, out var q) ? q : DefaultParameters(id));
                }
                if (ReferenceEquals(Interlocked.CompareExchange(ref _txView, new TxView(current, pending), was), was)) return;
            }
        }

        /// <summary>A whole replacement: the store as it now stands, waiting for an unkeyed buffer.</summary>
        private void PublishWhole()
        {
            if (_direction != DenoiseDirection.Transmit) return;
            var snapshot = new Dictionary<string, Parameters>(_settings);
            while (true)
            {
                var was = Volatile.Read(ref _txView);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _txView, new TxView(was.Current, snapshot), was), was)) return;
            }
        }

        /// <summary>On the audio thread, unkeyed: adopt a waiting replacement, if any. No allocation, no lock.</summary>
        private void AdoptPendingView()
        {
            var was = Volatile.Read(ref _txView);
            if (was.Adopted != null) Interlocked.CompareExchange(ref _txView, was.Adopted, was);
        }

        /// <summary>Tests only: whether a whole replacement is waiting for an unkeyed buffer.</summary>
        internal bool HasPendingView => Volatile.Read(ref _txView).Pending != null;

        /// <summary>Tests only: the parameters an engine reads now, to prove an untouched engine's are one instance.</summary>
        internal object ParametersForTest(string id) => ParametersFor(id);

        /// <summary>The stored wet fraction, 0 to 1. Surfaces show it as a noise floor through <see cref="NoiseController"/>'s lever, never by their own arithmetic.</summary>
        public float GetStrength(string id) => ParametersFor(id).Strength;
        public void SetStrength(string id, float value)
        {
            if (!float.IsFinite(value)) return;
            _settings.AddOrUpdate(id, key => DefaultParameters(key) with { Strength = Math.Clamp(value, 0, 1) },
                (_, p) => p with { Strength = Math.Clamp(value, 0, 1) });
            PublishLive(id, p => p with { Strength = Math.Clamp(value, 0, 1) });
        }
        public IReadOnlyList<EngineSettingDescriptor> SettingsFor(string id)
        {
            var row = _catalog.Find(id);
            return row?.Side == DenoiseSide.Pc && row.Family == Family && row.Serves(Direction)
                ? row.Settings : Array.Empty<EngineSettingDescriptor>();
        }
        public EngineSettingValue GetSetting(string id, string name)
        {
            var descriptor = SettingsFor(id).FirstOrDefault(s => s.Name == name);
            if (descriptor == null) throw new ArgumentException("Unknown setting", nameof(name));
            return _settings.TryGetValue(id, out var p) && p.Values.TryGetValue(name, out var value) && descriptor.Accepts(value)
                ? value : descriptor.Default;
        }
        public bool TrySetSetting(string id, string name, EngineSettingValue value)
        {
            var descriptor = SettingsFor(id).FirstOrDefault(s => s.Name == name);
            if (descriptor == null || !descriptor.Accepts(value)) return false;
            // A first setting on an untouched engine keeps its default Strength
            // (#1019): this used to create the entry at Strength 1 whatever the
            // engine, so choosing RNNoise's model on a fresh setup would have
            // silently set its floor to 0.
            _settings.AddOrUpdate(id, key => MakeParameters(key, DefaultParameters(key).Strength, new Dictionary<string, EngineSettingValue> { [name] = value }),
                (key, p) => MakeParameters(key, p.Strength, new Dictionary<string, EngineSettingValue>(p.Values) { [name] = value }));
            PublishLive(id, p => MakeParameters(id, p.Strength, new Dictionary<string, EngineSettingValue>(p.Values) { [name] = value }));
            // A rebuild setting (a new delay, or NR4's estimator) only takes
            // effect in a new instance, built under the warm-up cross-fade.
            if (descriptor.NeedsRebuild && ChosenEngineId == id) Select(id, out _, force: true);
            return true;
        }

        internal bool Select(string id, out string? refusal, bool force = false)
        {
            var entry = _catalog.Find(id);
            refusal = entry == null ? "unknown-engine" : entry.Side != DenoiseSide.Pc ? "radio-engine"
                : !entry.Serves(Direction) ? "wrong-direction" : entry.Family != Family ? "wrong-family"
                : entry.Factory == null ? "not-in-build" : null;
            if (refusal != null) return false;
            Choice choice;
            lock (_control)
            {
                if (_closed != 0) { refusal = "disposed"; return false; }
                choice = new Choice(_choice.Generation + 1, id, _direction) { Entry = entry, Force = force };
                Volatile.Write(ref _choice, choice);
                var abandoned = Interlocked.Exchange(ref _ready, null);
                abandoned?.Bank.Dispose(); // mailbox ownership is now ours, never the audio thread's
            }
            // Arrowing away and back keeps the existing native state. Audio
            // cancels its warming bank and acknowledges on its next buffer.
            // Not when a latency setting changed since that bank was built: it
            // can change only through a new build, and the change was stored
            // while another engine was chosen, so nothing has rebuilt it yet.
            if (!force && RunningIsCurrent(id)) return true;
            RequestBuild(choice);
            return true;
        }

        /// <summary>
        /// Whether the running bank is this engine, built from the settings chosen now.
        /// False after a model change until audio has carried the new build in, so a
        /// delay readout does not report the old build's figure as measured (#1019).
        /// </summary>
        public bool RunningIsCurrent(string id) => BuiltFromChoiceNow(Volatile.Read(ref _runningBuild), id);

        /// <summary>Whether this one snapshot is engine <paramref name="id"/>, built from the rebuild settings chosen now.</summary>
        private bool BuiltFromChoiceNow(RunningBuild? built, string id)
        {
            if (built == null || built.Id != id) return false;
            foreach (var descriptor in SettingsFor(id))
            {
                if (!descriptor.NeedsRebuild) continue;
                var wanted = GetSetting(id, descriptor.Name);
                var had = built.Initial.TryGetValue(descriptor.Name, out var v) ? v : descriptor.Default;
                if (!string.Equals(wanted.Choice, had.Choice, StringComparison.Ordinal) ||
                    BitConverter.DoubleToInt64Bits(wanted.Number) != BitConverter.DoubleToInt64Bits(had.Number)) return false;
            }
            return true;
        }

        private void RequestBuild(Choice choice)
        {
            var entry = choice.Entry;
            if (entry?.Factory == null || entry.Side != DenoiseSide.Pc || entry.Family != Family ||
                !entry.Serves(Direction) || Volatile.Read(ref _closed) != 0 ||
                Interlocked.CompareExchange(ref choice.BuildClaim, 1, 0) != 0) return;
            QueueBuild(choice, entry);
        }

        private void QueueBuild(Choice choice, DenoiseEngineEntry entry) =>
            _ = Task.Run(() => BuildChoice(choice, entry));

        private void BuildChoice(Choice choice, DenoiseEngineEntry entry)
        {
            PerChannelDenoiserBank? bank = null;
            try
            {
                bank = BuildBank(entry!, out var builtFrom);
                if (!bank.IsAvailable) throw new InvalidOperationException(bank.EngineFor(0).UnavailabilityDetail);
                lock (_control)
                {
                    if (_closed != 0 || !ReferenceEquals(choice, _choice)) { bank.Dispose(); return; }
                    var previous = Interlocked.Exchange(ref _ready, new Built(choice, bank) { Running = new RunningBuild(choice.Id, builtFrom, bank) });
                    bank = null;
                    previous?.Bank.Dispose();
                }
            }
            catch (Exception ex)
            {
                bank?.Dispose();
                Volatile.Write(ref choice.Failure, ex.Message);
            }
        }

        private PerChannelDenoiserBank BuildBank(DenoiseEngineEntry entry, out IReadOnlyDictionary<string, EngineSettingValue> builtFrom)
        {
            var parameters = ParametersFor(entry.Id);
            var values = entry.Settings.ToDictionary(s => s.Name, s => s.Default);
            foreach (var setting in parameters.Values)
            {
                var descriptor = entry.Settings.FirstOrDefault(s => s.Name == setting.Key);
                if (descriptor != null && descriptor.Accepts(setting.Value)) values[setting.Key] = setting.Value;
            }
            var initial = new System.Collections.ObjectModel.ReadOnlyDictionary<string, EngineSettingValue>(values);
            builtFrom = initial;
            var direction = _direction;
            var bank = new PerChannelDenoiserBank(() => entry.Factory!(initial, direction), _channels);
            try
            {
                Parameters? applied = null;
                ApplyParameters(bank, entry.Id, ref applied);
                return bank;
            }
            catch { bank.Dispose(); throw; }
        }
        private void ApplyParameters(PerChannelDenoiserBank bank, string id, ref Parameters? applied) =>
            ApplyParameters(bank, ParametersFor(id), ref applied);

        private static void ApplyParameters(PerChannelDenoiserBank bank, Parameters p, ref Parameters? applied)
        {
            if (ReferenceEquals(p, applied)) return;
            // On the audio thread: an array and two for loops, nothing else (repair round 2).
            var live = p.Live;
            for (int ch = 0; ch < bank.Channels; ch++)
                for (int i = 0; i < live.Length; i++)
                    bank.EngineFor(ch).TrySetSetting(live[i].Key, live[i].Value);
            applied = p;
        }

        internal void Process(float[] audio, bool permitted, bool profilePresent, bool capturing,
            Action<PerChannelDenoiserBank>? prepareSpectral)
        {
            if (_direction == DenoiseDirection.Transmit) { ProcessTransmit(audio, permitted); return; }
            var choice = Volatile.Read(ref _choice);
            if (Volatile.Read(ref choice.Failure) != null)
            {
                StopFailedChoice(choice);
                return;
            }
            // Every buffer rechecks: Select may have observed A immediately
            // before this thread installed B. The shared claim prevents two starters.
            if (choice.Id != RunningEngineId) RequestBuild(choice);
            if (_warming != null && !ReferenceEquals(_warming.Choice, choice))
            { Retire(_warming.Bank); _warming = null; _warmSamples = 0; }
            InstallHook?.Invoke("before-ready-take");
            var ready = Interlocked.Exchange(ref _ready, null);
            if (ready != null)
            {
                // A newer choice may have delivered since this buffer's first
                // snapshot. Never discard its one claimed build using that snapshot.
                choice = Volatile.Read(ref _choice);
                if (!ReferenceEquals(ready.Choice, choice)) Retire(ready.Bank);
                else { if (_warming != null) Retire(_warming.Bank); _warming = ready; _warmSamples = 0; }
                // The reread may be newer than the snapshot the stale-warming check used above.
                if (_warming != null && !ReferenceEquals(_warming.Choice, choice))
                { Retire(_warming.Bank); _warming = null; _warmSamples = 0; }
            }
            // The running bank and its id, from one snapshot. Only this thread
            // swaps it, and not before the warm handover below.
            var run = Volatile.Read(ref _runningBuild);
            var running = run?.Bank;
            if (running != null) prepareSpectral?.Invoke(running);
            if (_warming != null) prepareSpectral?.Invoke(_warming.Bank);
            bool active = Enabled && permitted;
            if (Family == DenoiseFamily.Spectral && capturing) active = false;
            if (choice.Id == "spectral_capture" && !profilePresent) active = false;
            if (!active)
            {
                if (_wasActive) { running?.ClearCarry(); _warming?.Bank.ClearCarry(); }
                _wasActive = false;
                Volatile.Write(ref _processingId, "");
                // Keep the built choice, but do not warm or announce it with no audio.
                return;
            }
            if (!_wasActive) BeginActive(run, running);
            _wasActive = true;
            bool listen = ListenToRemoved;
            if (running != null) { running.ListenToRemoved = listen; ApplyParameters(running, run!.Id, ref _runningApplied); }
            if (_warming != null) _warming.Bank.ListenToRemoved = listen;
            if (_warming != null)
            {
                ApplyParameters(_warming.Bank, _warming.Choice.Id, ref _warming.Applied);
                // Decided when the warm-up starts, again if a new plan arrives while it warms (repair 1, B2),
                // and again after the running engine retimed under it (repair 2, R1-B1).
                var warmPlan = SharedPlan;
                if (!ReferenceEquals(_warmDecidedFor, _warming) || !ReferenceEquals(_warmPlan, warmPlan) || _warmStale) DecideWarm(_warming, run, running, warmPlan);
                if (_nextAudio.Length != audio.Length) _nextAudio = new float[audio.Length];
                audio.CopyTo(_nextAudio, 0);
                long late = _warming.Bank.LateFrames, failed = _warming.Bank.FallbackFrames;
                _warming.Bank.Process(_nextAudio, GetStrength(_warming.Choice.Id));
                if (_warmTarget >= 0) _warmPad!.Process(_nextAudio.AsSpan(0, audio.Length), _warmTarget - _warming.Bank.LatencySamples);
                bool produced = late == _warming.Bank.LateFrames && failed == _warming.Bank.FallbackFrames;
                if (failed != _warming.Bank.FallbackFrames)
                {
                    // Lateness is expected during helper start-up. A throw or
                    // lost availability is a failed start, not endless warming.
                    var failedBuild = _warming;
                    _warming = null;
                    Retire(failedBuild.Bank);
                    Volatile.Write(ref failedBuild.Choice.Failure, "processing-failed");
                    StopFailedChoice(failedBuild.Choice);
                    return;
                }
                string? processedBy = running == null ? "" : null;
                bool retimed = false;
                if (running != null)
                {
                    long runningTook = running.EngineFrames, runningFailed = running.FallbackFrames;
                    running.Process(audio, GetStrength(run!.Id));
                    // The audible engine follows a new plan while another warms, exactly
                    // as when nothing warms (repair 2, R1-B1). A warm-up can last as long
                    // as the incoming engine stays late, which has no bound, so a plan
                    // waiting for the switch could wait forever.
                    int heldWas = _held, basisWas = _heldBasis;
                    CheckPlan(run!, running);
                    PadRunning(audio, running);
                    retimed = _held != heldWas || _heldBasis != basisWas;
                    processedBy = ProcessedBy(running, run.Id, runningTook, runningFailed);
                }
                // The hold the warm-up was decided against has moved: decide it again
                // on the next buffer, with a fresh correlation, and do not switch on
                // this one (its running output is the retime's fade).
                if (retimed) { _warmStale = true; _haveCorrelation = false; }
                if (produced) _warmSamples += audio.Length / _channels;
                else _warmSamples = 0;
                // Long enough that the incoming bank's padded output is real audio
                // for the measured window and the fade (#873: at least the delay
                // plus the window, never under today's 100 ms).
                int need = _sampleRate / 10;
                if (_warmTarget >= 0) need = Math.Max(need, _warmTarget + 2 * (audio.Length / _channels));
                if (!retimed && _warmSamples >= need && (!_warmAligned || _haveCorrelation) && ReferenceEquals(choice, Volatile.Read(ref _choice)))
                {
                    InstallHook?.Invoke("before-running-id");
                    bool mute = (MuteOverrideForTest ?? SwitchMuteEnabled) && running != null;
                    bool aligned = _warmAligned && !mute && CrossFadeEnabled;
                    if (mute) FadeOutForMute(audio);
                    else if (!CrossFadeEnabled) _nextAudio.AsSpan(0, audio.Length).CopyTo(audio);
                    else if (aligned) AlignedSwitch.Fade(audio, _nextAudio.AsSpan(0, audio.Length), _channels, _corr);
                    else CrossFade(audio, _nextAudio, _channels);
                    var note = new SwitchNote(aligned, _channels > 0 ? _corr[0] : 0, _channels > 1 ? _corr[1] : 0, _warmTarget, mute);
                    LastSwitch = note;
                    // The incoming bank's pad line becomes the running one, so the
                    // padded timeline carries straight on (#873).
                    (_runPad, _warmPad) = (_warmPad, _runPad);
                    Volatile.Write(ref _held, _warmTarget);
                    // The hold now rests on the plan the warm-up was decided on, which is the one in force (repair 1, B2).
                    _heldBasis = _warmBasis;
                    _planSeen = _warmPlan;
                    // The switch lands the plan it was decided on, so a retime still waiting is dropped.
                    _retimePending = false;
                    if (mute) { _muteLeft = MuteSamples; _rampLeft = MuteRampSamples; }
                    // One reference: the bank, its id and its build, made on the
                    // worker, so nothing is allocated here and no reader can see
                    // the incoming bank under the outgoing id (repair round 5).
                    Volatile.Write(ref _runningBuild, _warming.Running);
                    var old = running;
                    InstallHook?.Invoke("after-running-id");
                    _runningApplied = _warming.Applied;
                    _warming = null;
                    if (old != null) Retire(old);
                    _announced = choice.Generation;
                    processedBy = choice.Id;
                    Publish(choice, true, note: note);
                    if (processedBy != null) Volatile.Write(ref _processingId, processedBy);
                    return;
                }
                if (_warmAligned && !retimed) { AlignedSwitch.Correlation(audio, _nextAudio.AsSpan(0, audio.Length), _channels, _corr); _haveCorrelation = true; }
                ApplyMute(audio);
                if (processedBy != null) Volatile.Write(ref _processingId, processedBy);
                return;
            }
            if (running != null)
            {
                long late = running.LateFrames, failed = running.FallbackFrames, took = running.EngineFrames;
                running.Process(audio, GetStrength(run!.Id));
                CheckPlan(run, running);
                PadRunning(audio, running);
                ApplyMute(audio);
                if (ProcessedBy(running, run.Id, took, failed) is { } processedBy) Volatile.Write(ref _processingId, processedBy);
                if (choice.Id == run.Id && !choice.Force && _announced != choice.Generation &&
                    late == running.LateFrames && failed == running.FallbackFrames)
                { _announced = choice.Generation; Publish(choice, true); }
            }
            else Volatile.Write(ref _processingId, "");
        }

        /// <summary>
        /// The transmit path (Sprint 49 Track H, items A and B). No warm-up and
        /// no engine-to-engine cross-fade, both deliberately: a transmit change
        /// is applied only while unkeyed (<see cref="SwapPermitted"/>), so there
        /// is nobody on the air to smooth it for, and a warm-up would only delay
        /// the change into the next over. A delivered build is installed with a
        /// cut on the first permitted buffer. While keyed, nothing that changes
        /// what goes out happens here: not a swap, not a failed choice stopping
        /// the running engine. The bank's own fallback (a late or failed frame
        /// goes out as the dry signal at the same delay) still protects the
        /// over, and the transmit chain latches it (item C).
        /// </summary>
        private void ProcessTransmit(float[] audio, bool permitted)
        {
            LastAppliedLatencySamples = 0;
            LastBufferProcessed = false;
            LastBufferClean = false;
            bool swap = SwapPermitted?.Invoke() ?? true;
            var choice = Volatile.Read(ref _choice);
            if (Volatile.Read(ref choice.Failure) != null)
            {
                // Keyed: the failure waits for the unkey, and the engine that
                // was running keeps running.
                if (swap) { StopFailedChoice(choice); return; }
            }
            else if (choice.Id != RunningEngineId) RequestBuild(choice);
            // Repair round 3: a whole setup's live values are adopted here, on
            // an unkeyed buffer, with its engine swap, and never mid-over.
            if (swap) AdoptPendingView();
            if (swap)
            {
                InstallHook?.Invoke("before-ready-take");
                var ready = Interlocked.Exchange(ref _ready, null);
                if (ready != null)
                {
                    choice = Volatile.Read(ref _choice);
                    if (!ReferenceEquals(ready.Choice, choice)) Retire(ready.Bank);
                    else
                    {
                        var outgoing = Volatile.Read(ref _runningBuild)?.Bank;
                        // One reference, made on the worker (repair round 5's rule).
                        Volatile.Write(ref _runningBuild, ready.Running);
                        _runningApplied = ready.Applied;
                        if (outgoing != null) Retire(outgoing);
                        _announced = choice.Generation;
                        Publish(choice, true);
                    }
                }
            }
            var run = Volatile.Read(ref _runningBuild);
            var running = run?.Bank;
            bool active = Enabled && permitted;
            if (!active || running == null)
            {
                if (_wasActive) running?.ClearCarry();
                _wasActive = false;
                Volatile.Write(ref _processingId, "");
                return;
            }
            _wasActive = true;
            running.ListenToRemoved = false;
            // Keyed (no swap permitted): a late or failed frame latches the
            // bank dry for the rest of the over, never frame by frame (item C).
            running.LatchFallback = !swap;
            var inForce = ParametersForAudio(run!.Id);
            ApplyParameters(running, inForce, ref _runningApplied);
            long late = running.LateFrames, failed = running.FallbackFrames, took = running.EngineFrames, latched = running.LatchedFrames;
            running.Process(audio, inForce.Strength);
            LastBufferProcessed = true;
            LastBufferClean = running.EngineFrames != took && late == running.LateFrames
                && failed == running.FallbackFrames && latched == running.LatchedFrames && !running.Latched;
            LastAppliedLatencySamples = running.LatencySamples;
            if (ProcessedBy(running, run.Id, took, failed) is { } processedBy) Volatile.Write(ref _processingId, processedBy);
            if (choice.Id == run.Id && !choice.Force && _announced != choice.Generation &&
                late == running.LateFrames && failed == running.FallbackFrames)
            { _announced = choice.Generation; Publish(choice, true); }
        }

        /// <summary>Transmit only (item C): the bank the audio thread runs now, for the chain's latch and counts. Read on the audio thread.</summary>
        internal PerChannelDenoiserBank? RunningBank => Volatile.Read(ref _runningBuild)?.Bank;

        /// <summary>
        /// The failure the current choice's build left, or null while it is
        /// building, built or never failed. Read only, any thread (Sprint 49 Track
        /// Q phase B: the audition's twin reports a failed build at once rather
        /// than at its time limit).
        /// </summary>
        internal string? ChoiceFailure => Volatile.Read(ref Volatile.Read(ref _choice).Failure);

        /// <summary>
        /// What one buffer through <paramref name="bank"/> proves: "" when any frame
        /// passed through because the engine was unavailable (the id stays, the
        /// processing does not); <paramref name="id"/> when the engine itself took
        /// a frame; null when the buffer completed no frame at all, which proves
        /// nothing, so the last answer stands (repair round 2, Codex: a partial
        /// buffer made a dead engine's id reappear). Late frames count as taken,
        /// so a helper's start-up lateness does not flicker.
        /// </summary>
        private static string? ProcessedBy(PerChannelDenoiserBank bank, string id, long tookBefore, long failedBefore) =>
            bank.FallbackFrames != failedBefore ? "" : bank.EngineFrames != tookBefore ? id : null;

        private void StopFailedChoice(Choice choice)
        {
            // Only this audio thread releases the audible bank. A builder
            // leaves a failure on its choice; the next buffer applies it,
            // including while disabled, before reporting that the slot stopped.
            var old = Interlocked.Exchange(ref _runningBuild, null)?.Bank;
            Volatile.Write(ref _processingId, "");
            if (old != null) Retire(old);
            _runningApplied = null;
            if (_warming != null) { Retire(_warming.Bank); _warming = null; }
            _warmSamples = 0; _wasActive = false;
            // Never speak a result for a slot nobody used (Sprint 49 Track E,
            // E5). The constructor's default engine (generation 0) can fail to
            // build on a machine that cannot run it (every x86 launch until
            // #739, an install missing rnnoise.dll), and this runs on the first
            // receive buffer whether or not the slot was ever enabled. Held at
            // the source: the failure is NOT marked reported, so if the
            // operator later turns this slot on with that choice still in
            // place, the next buffer reports it then, once.
            if (choice.Generation == 0 && !HasBeenEnabled) return;
            if (Interlocked.Exchange(ref choice.FailureReported, 1) == 0)
                Publish(choice, false, Volatile.Read(ref choice.Failure), slotStopped: true);
        }

        // ── The shared delay, the aligned switch and the mute (#873, Sprint 49 Track P) ──
        //
        // Receive only. A transmit slot never warms, never pads and never mutes:
        // ProcessTransmit touches none of this, and its lines are not allocated.

        /// <summary>The longest pad a receive slot holds: 250 ms. A plan that would need more is treated as outside the set.</summary>
        public const int MaxPadSamples = 12000;

        /// <summary>The Mute switch's planned gap: about 100 ms (#873, Noel 2026-11-08 12:48).</summary>
        public const int MuteSamples = 4800;

        /// <summary>How the new engine comes back after the Mute gap: 5 ms on the shared ramp.</summary>
        public const int MuteRampSamples = 240;

        private SharedDelayPlan _plan = SharedDelayPlan.None;

        /// <summary>
        /// This family's shared delay and the engines that share it, published by
        /// the noise controller off the audio thread; read by the audio thread when
        /// the slot turns on and when a switch begins warming.
        /// </summary>
        public SharedDelayPlan SharedPlan
        {
            get => Volatile.Read(ref _plan);
            set => Volatile.Write(ref _plan, value ?? SharedDelayPlan.None);
        }

        private static int _switchMute;

        /// <summary>
        /// The Mute switch (ruled 2026-11-08 12:57; default off): a receive engine
        /// change fades the old engine out across the switch buffer, holds
        /// <see cref="MuteSamples"/> of silence, then fades the new engine in.
        /// App-wide, read by the audio thread once per switch.
        /// </summary>
        public static bool SwitchMuteEnabled
        {
            get => Volatile.Read(ref _switchMute) != 0;
            set => Volatile.Write(ref _switchMute, value ? 1 : 0);
        }

        /// <summary>Tests only: this slot's Mute switch, instead of the app-wide one, so a test never changes what another test's slots do.</summary>
        internal bool? MuteOverrideForTest { get; set; }

        /// <summary>What one switch did, for the trace and for tests: aligned or not, the measured correlation per channel (first two), the delay then held, and whether it was a muted switch.</summary>
        internal readonly record struct SwitchNote(bool Aligned, float R0, float R1, int Held, bool Mute);

        /// <summary>Tests only: the last switch this slot made.</summary>
        internal SwitchNote? LastSwitch { get; private set; }

        /// <summary>
        /// The delay the slot's output is held at now, in samples; -1 when it
        /// holds none (the running engine is outside the set). What is IN FORCE,
        /// not what the latest plan asks for: a new plan reaches the running
        /// engine at its next safe point (repair 1, B2), and until then this still
        /// says the old figure. Read by the Advanced tab's readout off the audio thread.
        /// </summary>
        internal int HeldSamples => Volatile.Read(ref _held);

        /// <summary>What one retime did (repair 1, B2): the delay it moved from and to (the engine's own delay when unpadded), the correlation of the two taps across the fade, and whether it was faded or cut (the cross-fade switch off).</summary>
        internal readonly record struct RetimeNote(int From, int To, float R0, float R1, bool Faded);

        /// <summary>Tests only: the last retime this slot made.</summary>
        internal RetimeNote? LastRetime { get; private set; }

        private PadLine? _runPad, _warmPad;
        private int _held = -1, _warmTarget = -1;
        private Built? _warmDecidedFor;
        private bool _warmAligned, _haveCorrelation, _warmStale;
        private float[] _corr = Array.Empty<float>();
        // The retime's own scratch (repair 2): a retime can now run while a switch
        // warms, and must not overwrite the incoming engine's output or its correlation.
        private float[] _retimeAudio = Array.Empty<float>();
        private float[] _retimeCorr = Array.Empty<float>();
        private int _muteLeft, _rampLeft;

        // Which plan the hold was decided on (repair 1, B2). The BASIS is what a
        // plan said about the running engine: its shared figure when the engine
        // is a member, -1 when it is not. A newly published plan that says the
        // same thing changes nothing (the controller republishes often); one that
        // says something else makes the hold stale, and the running engine moves
        // to the new target at its next safe point. A stale hold is never
        // inherited by the next switch.
        private int _heldBasis = -1, _warmBasis = -1;
        private SharedDelayPlan? _planSeen, _warmPlan;
        private bool _retimePending;
        private int _retimeTarget = -1, _retimeBasis = -1;
        private readonly RetimeTrace _retimeTrace = new();

        private static string? ModelOf(IReadOnlyDictionary<string, EngineSettingValue>? values) =>
            values != null && values.TryGetValue("model", out var v) ? v.Choice : null;

        /// <summary>What a plan says about an engine: its shared figure when the engine is a member, -1 when it is not.</summary>
        private static int Basis(SharedDelayPlan plan, string id, string? model) => plan.Contains(id, model) ? plan.Samples : -1;

        /// <summary>The delay an engine's output actually comes out at: the hold, or its own delay when it holds none.</summary>
        private static int Effective(int held, int own) => held >= 0 ? Math.Max(held, own) : own;

        /// <summary>The slot turns on: forget the old timeline and decide what the running engine is held at, from the plan in force now.</summary>
        private void BeginActive(RunningBuild? run, PerChannelDenoiserBank? running)
        {
            var plan = SharedPlan;
            _planSeen = plan;
            _runPad?.Clear();
            if (run != null && running != null)
            {
                string? model = ModelOf(run.Initial);
                _held = HoldFor(plan, run.Id, model, running.LatencySamples);
                _heldBasis = Basis(plan, run.Id, model);
            }
            else { _held = -1; _heldBasis = -1; }
            _retimePending = false;
            _warmDecidedFor = null;
            _warmStale = false;
            _muteLeft = 0; _rampLeft = 0;
        }

        /// <summary>The delay to hold an engine at when it is the one running: the plan's shared figure (or its own, if larger) when it is in the plan; -1 otherwise.</summary>
        private int HoldFor(SharedDelayPlan plan, string id, string? model, int own)
        {
            if (_runPad == null || !plan.Contains(id, model)) return -1;
            int held = Math.Max(plan.Samples, own);
            return held - own <= MaxPadSamples ? held : -1;
        }

        /// <summary>
        /// A switch begins warming, or the plan changed while it warms: decide
        /// what the incoming bank is padded to, and whether the switch is aligned.
        /// Aligned when both engines are in the plan and the delay held now is the
        /// one this plan asks for: the incoming one is padded to exactly that
        /// delay, so the two outputs carry the same content and no time moves. A
        /// helper figure read before its first open can be half a millisecond out
        /// (<see cref="ReceiveSharedDelay.RoundingSamples"/>), so an incoming
        /// engine up to that much slower is still aligned and moves time by that
        /// much; anything more is today's switch.
        /// <para>
        /// A hold from an EARLIER plan is never inherited (repair 1, B2): the
        /// incoming engine takes the current plan's figure, and the switch is
        /// aligned only if that happens to be the delay held now. Otherwise the
        /// switch itself moves time, once, as today's switch does.
        /// </para>
        /// <para>
        /// A pending retime is NOT dropped here (repair 2, R1-B1): the running
        /// engine keeps following the plan while this engine warms, because a
        /// late incoming engine can keep the warm-up going indefinitely. When that
        /// retime lands, the decision is made again against the hold it left, so
        /// the incoming target and the aligned decision agree with what is heard.
        /// The switch itself drops a retime still waiting when it completes.
        /// </para>
        /// </summary>
        private void DecideWarm(Built warming, RunningBuild? run, PerChannelDenoiserBank? running, SharedDelayPlan plan)
        {
            bool first = !ReferenceEquals(_warmDecidedFor, warming);
            _warmDecidedFor = warming;
            _warmPlan = plan;
            _warmStale = false;
            int own = warming.Bank.LatencySamples;
            string? model = ModelOf(warming.Running?.Initial);
            bool incomingIn = _warmPad != null && plan.Contains(warming.Choice.Id, model);
            bool holdCurrent = run != null && Basis(plan, run.Id, ModelOf(run.Initial)) == _heldBasis;
            int target;
            bool aligned = false;
            if (running != null && _held >= 0 && holdCurrent && incomingIn && own <= _held + ReceiveSharedDelay.RoundingSamples)
            {
                target = Math.Max(_held, own);
                aligned = true;
            }
            else
            {
                target = incomingIn ? HoldFor(plan, warming.Choice.Id, model, own) : -1;
                if (running != null && _held >= 0 && target >= _held && target <= _held + ReceiveSharedDelay.RoundingSamples) aligned = true;
            }
            int basis = Basis(plan, warming.Choice.Id, model);
            if (!first && target == _warmTarget && aligned == _warmAligned) { _warmBasis = basis; return; }
            _warmTarget = target;
            _warmAligned = aligned;
            _warmBasis = basis;
            _haveCorrelation = false;
            if (_warmTarget >= 0) { _warmPad!.Clear(); _warmSamples = 0; }
        }

        /// <summary>
        /// The running engine alone, after its bank: notice a new plan (repair 1,
        /// B2). If it says the same about this engine as the plan the hold was
        /// decided on, nothing changes. If not, the new target is pending, and
        /// <see cref="PadRunning"/> moves to it at the next safe point. An empty
        /// plan, or one this engine has left, means no padding.
        /// <para>
        /// A pending retime keeps what its line has gathered when another plan
        /// arrives (repair 3, R2-B1). The line holds this engine's own output,
        /// whatever delay it is being gathered for, so a plan asking for the same
        /// target again, or for a different one, carries on from it. The line is
        /// cleared only when it STARTS being fed (no hold and nothing pending
        /// before), so its history is this engine's alone. Clearing it on every
        /// new plan object, as repairs 1 and 2 did while the engine held nothing,
        /// let a controller that republishes on every favourite change, engine
        /// choice and selection result restart the retime indefinitely.
        /// </para>
        /// </summary>
        private void CheckPlan(RunningBuild run, PerChannelDenoiserBank running)
        {
            var plan = SharedPlan;
            if (ReferenceEquals(plan, _planSeen)) return;
            _planSeen = plan;
            string? model = ModelOf(run.Initial);
            int basis = Basis(plan, run.Id, model);
            if (basis == _heldBasis) { _retimePending = false; return; }
            int own = running.LatencySamples;
            int target = HoldFor(plan, run.Id, model, own);
            // Whether PadRunning has been feeding the line: it does while a hold
            // is in force or a retime is pending, and only then.
            bool fed = _held >= 0 || _retimePending;
            if (Effective(target, own) == Effective(_held, own))
            {
                // Nothing moves in time: adopt it. A pad line that starts being
                // fed starts empty, so its history is this engine's alone.
                if (!fed && target >= 0) _runPad!.Clear();
                Volatile.Write(ref _held, target);
                _heldBasis = basis;
                _retimePending = false;
                return;
            }
            if (!fed) _runPad!.Clear();
            _retimeTarget = target;
            _retimeBasis = basis;
            _retimePending = true;
        }

        /// <summary>
        /// Pad the running engine's output to the delay held. With a retime
        /// pending, the line is fed (at no pad, if none is held) until it holds
        /// enough of this engine's own output for the new delay; that buffer is
        /// the safe point: both delays are read from the one line, and the old is
        /// faded into the new across the buffer on the shared correlation-matched
        /// ramp (a cut when the cross-fade switch is off). Time moves once, by
        /// the difference, and is faded, never jumped. On the audio thread: no
        /// allocation beyond the existing switch buffer, no wait.
        /// </summary>
        private void PadRunning(float[] audio, PerChannelDenoiserBank running)
        {
            if (_runPad == null) return;
            int own = running.LatencySamples;
            if (_retimePending)
            {
                int oldPad = Effective(_held, own) - own, newPad = Effective(_retimeTarget, own) - own;
                if (_runPad.Filled >= newPad)
                {
                    if (_retimeAudio.Length != audio.Length) _retimeAudio = new float[audio.Length];
                    var next = _retimeAudio.AsSpan(0, audio.Length);
                    _runPad.ProcessTwo(audio, oldPad, next, newPad);
                    AlignedSwitch.Correlation(audio, next, _channels, _retimeCorr);
                    bool faded = CrossFadeEnabled;
                    if (faded) AlignedSwitch.Fade(audio, next, _channels, _retimeCorr);
                    else next.CopyTo(audio);
                    var note = new RetimeNote(Effective(_held, own), Effective(_retimeTarget, own),
                        _channels > 0 ? _retimeCorr[0] : 0, _channels > 1 ? _retimeCorr[1] : 0, faded);
                    LastRetime = note;
                    Volatile.Write(ref _held, _retimeTarget);
                    _heldBasis = _retimeBasis;
                    _retimePending = false;
                    _retimeTrace.Queue(Family, note);
                    return;
                }
                _runPad.Process(audio, oldPad);
                return;
            }
            if (_held >= 0) _runPad.Process(audio, _held - own);
        }

        /// <summary>
        /// The retime's trace line, written on a pool thread from one work item
        /// made with the slot, so the audio thread allocates nothing for it. A
        /// second retime while the first line is still being written is not
        /// traced (they are seconds apart in practice; the note is kept either way).
        /// </summary>
        private sealed class RetimeTrace : IThreadPoolWorkItem
        {
            private int _busy;
            private DenoiseFamily _family;
            private RetimeNote _note;

            public void Queue(DenoiseFamily family, RetimeNote note)
            {
                if (Interlocked.Exchange(ref _busy, 1) != 0) return;
                _family = family; _note = note;
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }

            public void Execute()
            {
                var n = _note;
                var family = _family;
                Volatile.Write(ref _busy, 0);
                Trace.WriteLine($"PcDenoiseSlot: {family} retimed to a new shared delay: from {n.From} to {n.To} samples, " +
                    (n.Faded ? "faded" : "cut") + $", correlation {n.R0:0.000} and {n.R1:0.000}");
            }
        }

        /// <summary>The Mute switch's first part: the outgoing engine fades out across the switch buffer.</summary>
        private void FadeOutForMute(Span<float> audio)
        {
            int frames = audio.Length / _channels;
            for (int f = 0; f < frames; f++)
            {
                float g = CrossFadeCurve.Out((f + 1f) / frames);
                for (int c = 0; c < _channels; c++) audio[f * _channels + c] *= g;
            }
        }

        /// <summary>The Mute switch's gap, then the new engine fading in. Nothing at all when no mute is running.</summary>
        private void ApplyMute(Span<float> audio)
        {
            if (_muteLeft <= 0 && _rampLeft <= 0) return;
            int frames = audio.Length / _channels;
            for (int f = 0; f < frames; f++)
            {
                float g;
                if (_muteLeft > 0) { g = 0f; _muteLeft--; }
                else if (_rampLeft > 0) { g = CrossFadeCurve.In((MuteRampSamples - _rampLeft + 1f) / MuteRampSamples); _rampLeft--; }
                else break;
                for (int c = 0; c < _channels; c++) audio[f * _channels + c] *= g;
            }
        }

        internal static void CrossFade(Span<float> oldAudio, ReadOnlySpan<float> newAudio, int channels)
        {
            int frames = oldAudio.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                float gain = (f + 1f) / frames;
                for (int ch = 0; ch < channels; ch++)
                { int i = f * channels + ch; oldAudio[i] = oldAudio[i] * (1 - gain) + newAudio[i] * gain; }
            }
        }
        private void Publish(Choice choice, bool processing, string? failure = null, bool slotStopped = false, SwitchNote? note = null) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // The switch's record, written here off the audio thread (#873).
                if (note is { } n)
                    Trace.WriteLine($"PcDenoiseSlot: {Family} switched to {choice.Id}: " +
                        (n.Mute ? "muted switch" : n.Aligned ? "aligned fade" : "unaligned switch") +
                        $", correlation {n.R0:0.000} and {n.R1:0.000}, held at {n.Held} samples");
                EventHandler<DenoiseSelectionResult>? handler;
                lock (_control)
                {
                    if (_closed != 0 || !ReferenceEquals(choice, _choice)) return;
                    handler = SelectionResult;
                }
                // A listener may marshal to the UI, which may be disconnecting.
                // Never hold the admission lock while invoking external code.
                handler?.Invoke(this, new(choice.Direction, Family, choice.Id, processing, failure) { SlotStopped = slotStopped });
            });
        private static void Retire(PerChannelDenoiserBank bank) =>
            ThreadPool.QueueUserWorkItem(_ => bank.Dispose());

        // Admission closes before the pipeline waits for its process mark.
        // The interlocked mailbox gives its taker exclusive ownership. Only
        // DisposeOwned requires the mark; a timeout deliberately keeps the
        // running/warming banks alive instead of freeing them inside a frame.
        internal void CloseAdmission()
        {
            lock (_control)
            {
                _closed = 1;
                Interlocked.Exchange(ref _ready, null)?.Bank.Dispose();
            }
        }
        internal void DisposeOwned()
        {
            // The id stays, as it always did; only the bank goes, so the delay reads 0.
            var run = Volatile.Read(ref _runningBuild);
            if (run?.Bank != null) { run.Bank.Dispose(); Volatile.Write(ref _runningBuild, run with { Bank = null }); }
            _warming?.Bank.Dispose(); _warming = null;
        }

        internal DenoiseEngineSettings[] ExportSettings() => _settings.Select(pair => new DenoiseEngineSettings {
            EngineId = pair.Key, Direction = Direction, Strength = pair.Value.Strength,
            Values = pair.Value.Values.Select(v => new DenoiseNamedValue { Name = v.Key, Number = v.Value.Number, Choice = v.Value.Choice }).ToList()
        }).ToArray();
        /// <summary>
        /// Put back stored settings (a preset, Escape) and choose
        /// <paramref name="id"/>, building at most once (Sprint 49 Track E
        /// repair round 1, B3). Another engine builds from the restored values,
        /// as any choice does. The engine already chosen rebuilds, under the
        /// warm-up cross-fade, when a setting only a new instance can take
        /// (<see cref="EngineSettingDescriptor.NeedsRebuild"/>: NNR's model,
        /// NR4's estimator, a new delay) now differs from what was stored
        /// before, as <see cref="TrySetSetting"/> does for one setting; without
        /// that, the stored value and the engine you hear disagree, because
        /// <see cref="ApplyParameters"/> deliberately never pushes a rebuild
        /// setting into a running instance. Returns true when the chosen engine
        /// changed.
        /// </summary>
        internal bool RestoreAndSelect(IEnumerable<DenoiseEngineSettings> records, string id)
        {
            string chosen = ChosenEngineId;
            var before = RebuildValues(chosen);
            RestoreSettings(records);
            if (id.Length > 0 && id != chosen)
            {
                Select(id, out _);
                return true;
            }
            if (chosen.Length == 0 || before.SequenceEqual(RebuildValues(chosen))) return false;
            // A rebuild unless the running instance was built with exactly
            // these values already (Escape put back what is playing): then the
            // new choice only retires a warming build of the value undone.
            Select(chosen, out _, force: !RunningIsCurrent(chosen));
            return false;
        }

        /// <summary>
        /// Replace EVERY stored engine setting with exactly <paramref name="records"/>
        /// (an engine or a value not in them goes back to its default) and choose
        /// <paramref name="id"/>; returns false, choosing nothing, when the slot
        /// refuses it. For a whole transmit setup (Sprint 49 Track H, repair round
        /// 1, R2): a setup stores only what was touched, so overlaying it on the
        /// slot, as <see cref="RestoreAndSelect"/> does for a receive preset, let
        /// the previous setup's engine and values leak into the new one (Codex's
        /// check, 2026-11-18). The chosen engine rebuilds when a rebuild setting
        /// now differs from what it was built with, as there.
        /// </summary>
        internal bool ReplaceSettingsAndSelect(IEnumerable<DenoiseEngineSettings> records, string id)
        {
            string chosen = ChosenEngineId;
            var before = RebuildValues(chosen);
            _settings.Clear();
            RestoreSettings(records);
            // Transmit: the whole replacement waits for an unkeyed buffer (repair round 3).
            PublishWhole();
            if (id != chosen) return Select(id, out _);
            if (!before.SequenceEqual(RebuildValues(chosen))) Select(chosen, out _, force: !RunningIsCurrent(chosen));
            return true;
        }

        private List<string> RebuildValues(string id) => SettingsFor(id).Where(d => d.NeedsRebuild)
            .Select(d => { var v = GetSetting(id, d.Name); return d.Name + "=" + (v.Choice ?? v.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); })
            .ToList();

        internal void RestoreSettings(IEnumerable<DenoiseEngineSettings> records)
        {
            foreach (var record in records)
            {
                var row = _catalog.Find(record.EngineId);
                if (record.Direction != Direction || row?.Family != Family || !row.Serves(Direction)) continue;
                // A stored value this build's descriptor does not accept as it
                // stands snaps to the nearest one it does (DeepFilterNet3's old
                // step-1 limits land on the nearest stop), so nothing an operator
                // stored is silently reset to a default. A name this build does
                // not know is kept as it was, for a newer build to read back.
                var descriptors = row.Settings;
                _settings[record.EngineId] = MakeParameters(record.EngineId,
                    float.IsFinite(record.Strength) ? Math.Clamp(record.Strength, 0, 1) : ParametersFor(record.EngineId).Strength,
                    record.Values.GroupBy(v => v.Name).ToDictionary(v => v.Key, v =>
                    {
                        var stored = new EngineSettingValue(v.Last().Number, v.Last().Choice);
                        var d = descriptors.FirstOrDefault(x => x.Name == v.Key);
                        return d == null ? stored : d.Snap(stored);
                    }));
            }
        }
    }
}
