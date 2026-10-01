#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios.Alarms
{
    /// <summary>One alarm as the dialog sees it at one instant. Built under the lock, read anywhere.</summary>
    public sealed record AlarmSnapshot(
        AlarmDefinition Definition,
        AlarmDataState Data,
        AlarmConditionState Condition,
        AlarmNotificationState Notification,
        AlarmBaselineState Baseline,
        float BaselineValue,
        MeterSelectorStatus Resolution,
        MeterDescriptor? ResolvedMeter,
        MeterObservation? LastFresh,
        double LastFreshAgeSeconds,
        string EpisodeId,
        double SnoozeRemainingSeconds,
        AlarmEvent? LastEvent,
        bool Transmitting,
        float LastAnnouncedValue,
        int NotificationRevision,
        bool SilencedUntilWorse = false);

    /// <summary>
    /// The live attachment: one per rig, alive whether or not any window is
    /// open. Turns the feed's readings into observations, runs every monitor
    /// on the meter thread in a few compares, and hands the conclusions to a
    /// bounded worker for speech, sound, the list and the journal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The meter thread does exactly three things here:</b> look up which
    /// monitors are bound to this meter's index, build one immutable
    /// observation, and run those monitors. Everything that can be slow —
    /// speech, the tone, the UI, files — happens on the dispatch worker. The
    /// recorder is handed the observation on the meter thread and must only
    /// enqueue (<see cref="IAlarmObservationRecorder"/>).
    /// </para>
    /// <para>
    /// <b>Identity is rebound on every inventory change and every connection</b>
    /// through <see cref="MeterSelector.Resolve"/> over the feed's census. The
    /// binding table is index-keyed for THIS generation only; a reconnect
    /// starts a new generation, and a reading from an old one is discarded by
    /// the monitor before it is judged.
    /// </para>
    /// <para>
    /// <b>The recorded set is one set</b> (#566, ruled 2026-09-22): every
    /// meter an alarm selects, plus the meters the operator adds for the
    /// capture without an alarm on them. There is no hardcoded list.
    /// </para>
    /// </remarks>
    public sealed class AlarmService : IDisposable
    {
        /// <summary>The watchdog cadence. Notices silence when no callback arrives at all.</summary>
        public const int WatchdogIntervalMs = 250;

        /// <summary>How many past events the dialog's History can browse.</summary>
        public const int RecentEventsKept = 200;

        private sealed class Entry
        {
            public Entry(AlarmMonitor monitor) { Monitor = monitor; }
            public AlarmMonitor Monitor;
            public MeterSelectorResolution Resolution = new MeterSelectorResolution(MeterSelectorStatus.Missing, null, Array.Empty<MeterDescriptor>());
            public int BoundIndex = -1;
            public AlarmEvent? LastEvent;
        }

        private readonly object _gate = new object();
        private readonly IAlarmMeterFeed _feed;
        private readonly AlarmDefinitionStore? _store;
        private readonly IAlarmClock _clock;
        private readonly IAlarmObservationRecorder _recorder;
        private readonly AlarmDispatchQueue<AlarmEvent> _queue;
        private readonly Timer? _watchdog;

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<int, List<Entry>> _byIndex = new Dictionary<int, List<Entry>>();
        private readonly HashSet<int> _recordedIndices = new HashSet<int>();
        private readonly List<MeterSelector> _recordOnly = new List<MeterSelector>();
        private readonly List<AlarmEvent> _recent = new List<AlarmEvent>();

        private int _generation;
        private bool _connected;
        private string _serial = "";
        private long _sequence;
        private AlarmStoreState _storeState = AlarmStoreState.Empty;
        private string _storeProblem = "";
        private bool _disposed;

        /// <param name="dispatchCapacity">
        /// The dispatch queue's cap. Production takes the default; a test
        /// passes a small number so the overflow path can be driven without
        /// five hundred events.
        /// </param>
        public AlarmService(IAlarmMeterFeed feed, AlarmDefinitionStore? store, IAlarmClock clock,
            IAlarmObservationRecorder? recorder = null, bool startWatchdog = true,
            int dispatchCapacity = AlarmDispatchQueue<AlarmEvent>.DefaultCapacity)
        {
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _store = store;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _recorder = recorder ?? NullAlarmObservationRecorder.Instance;
            _queue = new AlarmDispatchQueue<AlarmEvent>(Deliver, "AlarmDispatch", dispatchCapacity);
            _queue.Overflowed += OnDispatchOverflow;

            _feed.Reading += OnReading;
            _feed.InventoryChanged += OnInventoryChanged;
            _feed.Connected += OnConnected;
            _feed.Disconnected += OnDisconnected;
            _feed.TransmitChanged += OnTransmitChanged;

            if (_feed.IsConnected) OnConnected(_feed.ConnectedSerial);

            if (startWatchdog)
                _watchdog = new Timer(_ => Tick(_clock.NowMs), null, WatchdogIntervalMs, WatchdogIntervalMs);
        }

        // ── outward ──

        /// <summary>Every event, in order, on the dispatch worker. Delivery and the dialog subscribe here.</summary>
        public event Action<AlarmEvent>? EventDispatched;

        /// <summary>Definitions, bindings or the recorded set changed. Raised on the calling thread.</summary>
        public event Action? Changed;

        /// <summary>The dispatch queue overflowed: monitoring delivery is unavailable while it lasts.</summary>
        public event Action<long>? DeliveryUnavailable;

        public string RadioSerial { get { lock (_gate) return _serial; } }
        public bool IsConnected { get { lock (_gate) return _connected; } }
        public int ConnectionGeneration { get { lock (_gate) return _generation; } }
        public AlarmStoreState StoreState { get { lock (_gate) return _storeState; } }
        public string StoreProblem { get { lock (_gate) return _storeProblem; } }
        public long ObservationCount => Interlocked.Read(ref _sequence);
        public long DispatchDropped => _queue.Dropped;
        public int DispatchHighWaterMark => _queue.HighWaterMark;
        public IReadOnlyList<MeterDescriptor> Inventory => _feed.Inventory;
        public IAlarmClock Clock => _clock;

        public IReadOnlyList<AlarmSnapshot> Snapshot()
        {
            lock (_gate)
            {
                long now = _clock.NowMs;
                var list = new List<AlarmSnapshot>(_entries.Count);
                foreach (Entry e in _entries.Values) list.Add(SnapshotOf(e, now));
                list.Sort((a, b) => string.Compare(a.Definition.Name, b.Definition.Name, StringComparison.CurrentCultureIgnoreCase));
                return list;
            }
        }

        public AlarmSnapshot? SnapshotOf(string alarmId)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(alarmId, out Entry? e) ? SnapshotOf(e, _clock.NowMs) : null;
            }
        }

        private AlarmSnapshot SnapshotOf(Entry e, long now)
        {
            AlarmMonitor m = e.Monitor;
            return new AlarmSnapshot(m.Definition, m.Data, m.Condition, m.Notification, m.Baseline, m.BaselineValue,
                e.Resolution.Status, e.Resolution.Match, m.LastFresh,
                m.LastFresh.HasValue ? m.LastFresh.Value.AgeSeconds(now) : double.NaN,
                m.EpisodeId, m.SnoozeRemainingSeconds(now), e.LastEvent, m.IsTransmitting, m.LastAnnouncedValue,
                m.NotificationRevision, m.SilencedUntilWorse);
        }

        /// <summary>The last <see cref="RecentEventsKept"/> events, oldest first.</summary>
        public IReadOnlyList<AlarmEvent> RecentEvents()
        {
            lock (_gate) return _recent.ToArray();
        }

        /// <summary>Meters recorded for the capture without an alarm on them.</summary>
        public IReadOnlyList<MeterSelector> RecordOnlyMeters
        {
            get { lock (_gate) return _recordOnly.ToArray(); }
        }

        /// <summary>
        /// The one recorded set: every ENABLED alarm's meter plus the
        /// record-only ones, as selectors. A disabled alarm records nothing —
        /// the dialog says "while the alarm is enabled", and until Astra's
        /// Track I review (finding 11) that sentence was false: every
        /// definition was in the set whatever its state, so disabling an alarm
        /// left its meter being written per reading with no alarm to justify
        /// it. The operator's own recording choices are theirs and are not
        /// touched by an alarm's state.
        /// </summary>
        public IReadOnlyList<MeterSelector> RecordedMeters
        {
            get
            {
                lock (_gate)
                {
                    var set = new List<MeterSelector>();
                    foreach (Entry e in _entries.Values)
                        if (e.Monitor.Enabled && !set.Contains(e.Monitor.Definition.Selector)) set.Add(e.Monitor.Definition.Selector);
                    foreach (MeterSelector s in _recordOnly)
                        if (!set.Contains(s)) set.Add(s);
                    return set;
                }
            }
        }

        /// <summary>The recorded set resolved on this connection — what the journal actually receives.</summary>
        public IReadOnlyList<MeterDescriptor> RecordedMetersResolved
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<MeterDescriptor>();
                    foreach (MeterDescriptor d in _feed.Inventory)
                        if (_recordedIndices.Contains(d.Index)) list.Add(d);
                    return list;
                }
            }
        }

        // ── definitions ──
        //
        // Every edit below has one shape, and the shape is the fix for the
        // last of Astra's Track I review finding 4 and its "failed save
        // mutates monitor state" observation: build the file the edit WOULD
        // produce, write it with no lock the meter thread needs, and only then
        // change the running monitors. The old order mutated first and rolled
        // back on failure — but restoring a definition is not restoring the
        // episode, the acknowledgement and the baseline it had closed on the
        // way, and the write itself sat under the service lock, so a slow
        // disk stalled every meter callback behind it. _saveGate serialises
        // the operator's edits against each other; _gate is held only for the
        // two short critical sections either side of the write.

        /// <summary>Serialises configuration edits. Never taken by the meter thread, and never held under <see cref="_gate"/>.</summary>
        private readonly object _saveGate = new object();

        /// <summary>Add a definition, save it, and start monitoring it. False when it is invalid or the store refused.</summary>
        public bool Add(AlarmDefinition definition)
        {
            if (definition == null || definition.Validate().Count != 0) return false;
            lock (_saveGate)
            {
                AlarmDefinitionFile file;
                int generation;
                lock (_gate)
                {
                    if (_entries.ContainsKey(definition.Id)) return false;
                    definition = definition with { RadioSerial = _serial };
                    file = FileLocked(f => f.Definitions.Add(definition));
                    generation = _generation;
                }
                if (!Save(file)) return false;   // nothing running has moved
                lock (_gate)
                {
                    if (generation != _generation) return true;   // a reconnect reloaded the saved file meanwhile
                    var entry = new Entry(new AlarmMonitor(definition));
                    _entries[definition.Id] = entry;
                    entry.Monitor.NewConnection(_generation, _clock.NowMs);
                    Rebind();
                    if (_feed.IsTransmitting) Post(entry, entry.Monitor.SetTransmit(true, _clock.NowMs));
                }
            }
            RaiseChanged();
            return true;
        }

        /// <summary>Replace a definition with its next revision, save, and rejudge the next sample under it.</summary>
        public bool Update(AlarmDefinition definition)
        {
            if (definition == null || definition.Validate().Count != 0) return false;
            lock (_saveGate)
            {
                AlarmDefinition next;
                AlarmDefinitionFile file;
                int generation;
                lock (_gate)
                {
                    if (!_entries.TryGetValue(definition.Id, out Entry? current)) return false;
                    next = definition with
                    {
                        Revision = current.Monitor.Definition.Revision + 1,
                        RadioSerial = _serial,
                    };
                    file = FileLocked(f =>
                    {
                        int at = f.Definitions.FindIndex(d => d.Id == next.Id);
                        if (at >= 0) f.Definitions[at] = next; else f.Definitions.Add(next);
                    });
                    generation = _generation;
                }
                if (!Save(file)) return false;   // the running episode, acknowledgement and baseline are untouched
                lock (_gate)
                {
                    if (generation != _generation) return true;
                    if (!_entries.TryGetValue(definition.Id, out Entry? entry)) return true;
                    Post(entry, entry.Monitor.Replace(next, _clock.NowMs));
                    Rebind();
                }
            }
            RaiseChanged();
            return true;
        }

        public bool Remove(string alarmId)
        {
            lock (_saveGate)
            {
                AlarmDefinitionFile file;
                int generation;
                lock (_gate)
                {
                    if (!_entries.ContainsKey(alarmId)) return false;
                    file = FileLocked(f => f.Definitions.RemoveAll(d => d.Id == alarmId));
                    generation = _generation;
                }
                if (!Save(file)) return false;
                lock (_gate)
                {
                    if (generation != _generation) return true;
                    _entries.Remove(alarmId);
                    Rebind();
                }
            }
            RaiseChanged();
            return true;
        }

        public bool SetEnabled(string alarmId, bool enabled)
        {
            lock (_saveGate)
            {
                AlarmDefinitionFile file;
                int generation;
                lock (_gate)
                {
                    if (!_entries.TryGetValue(alarmId, out Entry? current)) return false;
                    AlarmDefinition flipped = current.Monitor.Definition with { Enabled = enabled };
                    file = FileLocked(f =>
                    {
                        int at = f.Definitions.FindIndex(d => d.Id == alarmId);
                        if (at >= 0) f.Definitions[at] = flipped;
                    });
                    generation = _generation;
                }
                if (!Save(file)) return false;
                lock (_gate)
                {
                    if (generation != _generation) return true;
                    if (!_entries.TryGetValue(alarmId, out Entry? entry)) return true;
                    Post(entry, entry.Monitor.SetEnabled(enabled, _clock.NowMs));
                    // The recorded set follows the enabled state (finding 11).
                    Rebind();
                }
            }
            RaiseChanged();
            return true;
        }

        /// <summary>Add or remove a meter from the capture without an alarm on it.</summary>
        public bool SetRecordOnly(MeterSelector selector, bool record)
        {
            if (selector == null) return false;
            lock (_saveGate)
            {
                AlarmDefinitionFile file;
                int generation;
                lock (_gate)
                {
                    bool present = _recordOnly.Contains(selector);
                    if (record == present) return true;
                    file = FileLocked(f =>
                    {
                        f.RecordOnlyMeters.RemoveAll(s => s.Equals(selector));
                        if (record) f.RecordOnlyMeters.Add(selector);
                    });
                    generation = _generation;
                }
                if (!Save(file)) return false;
                lock (_gate)
                {
                    if (generation != _generation) return true;
                    _recordOnly.RemoveAll(s => s.Equals(selector));
                    if (record) _recordOnly.Add(selector);
                    Rebind();
                }
            }
            RaiseChanged();
            return true;
        }

        /// <summary>The file this radio's configuration would be, with one edit applied. Under the lock; reads only.</summary>
        private AlarmDefinitionFile FileLocked(Action<AlarmDefinitionFile> edit)
        {
            var file = new AlarmDefinitionFile { RadioSerial = _serial };
            foreach (Entry e in _entries.Values) file.Definitions.Add(e.Monitor.Definition);
            file.RecordOnlyMeters.AddRange(_recordOnly);
            edit(file);
            return file;
        }

        /// <summary>
        /// Write a configuration file. Called with <see cref="_saveGate"/> held
        /// and <see cref="_gate"/> NOT held, so disk IO never stalls a meter
        /// callback. Refuses over an unavailable store: saving over a file the
        /// operator has not seen the problem with is a deliberate act the
        /// dialog asks about, not a side effect of an edit.
        /// </summary>
        private bool Save(AlarmDefinitionFile file)
        {
            AlarmStoreState state;
            string problem;
            lock (_gate) { state = _storeState; problem = _storeProblem; }
            if (_store == null || state == AlarmStoreState.Unavailable)
            {
                Tracing.TraceLine("AlarmService: refusing to save over an unavailable configuration ("
                    + problem + ")", TraceLevel.Warning);
                return false;
            }
            bool ok = _store.Save(file);
            if (ok) lock (_gate) _storeState = AlarmStoreState.Loaded;
            return ok;
        }

        // ── operator actions on an episode ──

        public IReadOnlyList<AlarmEvent> Acknowledge(string alarmId) => Act(alarmId, (m, now) => m.Acknowledge(now));
        public IReadOnlyList<AlarmEvent> Snooze(string alarmId, double seconds) => Act(alarmId, (m, now) => m.Snooze(seconds, now));
        public IReadOnlyList<AlarmEvent> Resume(string alarmId) => Act(alarmId, (m, now) => m.Resume(now));
        public IReadOnlyList<AlarmEvent> CaptureBaseline(string alarmId) => Act(alarmId, (m, now) => m.CaptureBaseline(now));

        private IReadOnlyList<AlarmEvent> Act(string alarmId, Func<AlarmMonitor, long, IReadOnlyList<AlarmEvent>> action)
        {
            IReadOnlyList<AlarmEvent> events;
            lock (_gate)
            {
                if (!_entries.TryGetValue(alarmId, out Entry? entry)) return Array.Empty<AlarmEvent>();
                events = action(entry.Monitor, _clock.NowMs);
                Post(entry, events);
            }
            return events;
        }

        /// <summary>
        /// Exercise the real output path with a synthetic event that carries
        /// test provenance. It never reaches a monitor and never injects a
        /// live meter value; the value it carries is the threshold, so the
        /// sentence reads as the real one would.
        /// </summary>
        public AlarmEvent? Preview(string alarmId)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(alarmId, out Entry? entry)) return null;
                return PreviewLocked(entry.Monitor.Definition, entry.Resolution.Match, entry.Monitor.BaselineValue,
                    entry.Monitor.IsTransmitting);
            }
        }

        /// <summary>The same, for a definition the editor has not saved yet.</summary>
        public AlarmEvent PreviewDefinition(AlarmDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            lock (_gate)
            {
                MeterSelectorResolution r = definition.Selector.Resolve(_feed.Inventory);
                return PreviewLocked(definition, r.Match, float.NaN, _feed.IsTransmitting);
            }
        }

        private AlarmEvent PreviewLocked(AlarmDefinition def, MeterDescriptor? resolved, float baseline, bool transmitting)
        {
            MeterDescriptor meter = resolved ?? new MeterDescriptor(-1, def.Selector.Name,
                def.Selector.Description, def.Selector.Source, def.Selector.SourceIndex, def.Selector.Units, 0, 0);
            long now = _clock.NowMs;
            float value = (float)def.Threshold;
            if (def.Condition == AlarmCondition.RiseFromBaseline)
                value = float.IsNaN(baseline) ? value : baseline + (float)def.Threshold;
            var obs = MeterObservation.PreviewOf(meter, value, now, _clock.UtcNow, _generation);
            var preview = new AlarmEvent
            {
                Kind = AlarmEventKind.Fired,
                Definition = def,
                EpisodeId = "preview",
                AtMs = now,
                Observation = obs,
                Value = value,
                Threshold = def.Threshold,
                Change = (float)def.Threshold,
                Baseline = baseline,
                IntervalSeconds = def.Condition == AlarmCondition.RisingFast ? 90 : double.NaN,
                Transmitting = transmitting,
                Detail = "preview",
            };
            Remember(preview);
            _queue.Post(preview);
            return preview;
        }

        /// <summary>Whether this meter has delivered at least one reading on this connection — "reporting" against "no reading yet".</summary>
        public bool HasReported(int meterIndex)
        {
            lock (_gate) return _reported.Contains(meterIndex);
        }

        private readonly HashSet<int> _reported = new HashSet<int>();

        /// <summary>Wall time for a monotonic instant, for history rows. Approximate to the clock's own drift.</summary>
        public DateTime UtcAt(long atMs) => _clock.UtcNow.AddMilliseconds(atMs - _clock.NowMs);

        // ── the feed ──

        private void OnReading(MeterDescriptor meter, float value, int inventoryEpoch)
        {
            // THE meter-thread path. Build one observation, run the bound
            // monitors, post the conclusions. No speech, no marshal, no file,
            // no radio call — and the recorder only enqueues.
            //
            // Receipt is stamped BEFORE the lock (finding 3): a callback that
            // waits behind a configuration edit must not be given the time it
            // finally got in, which would hide its age from the freshness rule.
            long now = _clock.NowMs;
            DateTime nowUtc = _clock.UtcNow;

            List<(Entry, IReadOnlyList<AlarmEvent>)>? results = null;
            lock (_gate)
            {
                if (!_connected) return;

                // **Identity, not just an index (finding 3).** The reading must
                // come from the census the bindings were made against, and the
                // descriptor at that index must be the one bound there. A late
                // callback from an old meter object whose index was reused fails
                // one or the other and is discarded with a line, instead of
                // being judged as a fresh observation of the current meter.
                if (inventoryEpoch != _boundEpoch)
                {
                    Interlocked.Increment(ref _rejectedStale);
                    Tracing.TraceLine("AlarmService: discarded a reading from inventory epoch " + inventoryEpoch
                        + " (bound under " + _boundEpoch + "): " + meter.Name + " index " + meter.Index + " = " + value,
                        TraceLevel.Verbose);
                    return;
                }

                long seq = ++_sequence;
                _reported.Add(meter.Index);
                var obs = MeterObservation.Measured(meter, value, seq, now, nowUtc, _generation, null);

                if (_recordedIndices.Contains(meter.Index)) _recorder.Record(obs);

                if (!_byIndex.TryGetValue(meter.Index, out List<Entry>? bound)) return;
                foreach (Entry e in bound)
                {
                    if (!meter.Equals(e.Resolution.Match))
                    {
                        Interlocked.Increment(ref _rejectedStale);
                        Tracing.TraceLine("AlarmService: discarded a reading whose descriptor is not the one bound at index "
                            + meter.Index + " for '" + e.Monitor.Definition.Name + "': got " + meter.Label + ", bound "
                            + (e.Resolution.Match?.Label ?? "nothing"), TraceLevel.Warning);
                        continue;
                    }
                    IReadOnlyList<AlarmEvent> events = e.Monitor.Observe(
                        obs.ClassifiedAgainst(e.Monitor.Definition.SentinelValue), now);
                    if (events.Count == 0) continue;
                    results ??= new List<(Entry, IReadOnlyList<AlarmEvent>)>();
                    results.Add((e, events));
                }
                if (results != null)
                    foreach (var (e, events) in results) Post(e, events);
            }
        }

        /// <summary>The feed epoch the current bindings were made against; a reading from another is discarded.</summary>
        private int _boundEpoch;
        private long _rejectedStale;

        /// <summary>Readings discarded for carrying an old inventory epoch or a descriptor other than the bound one. Diagnostics.</summary>
        public long RejectedStaleReadings => Interlocked.Read(ref _rejectedStale);

        /// <summary>The watchdog. Public so a harness can drive it on a manual clock.</summary>
        public void Tick(long nowMs)
        {
            if (_disposed) return;
            lock (_gate)
            {
                if (!_connected) return;
                foreach (Entry e in _entries.Values)
                {
                    IReadOnlyList<AlarmEvent> events = e.Monitor.Tick(nowMs);
                    if (events.Count != 0) Post(e, events);
                }
            }
        }

        private void OnInventoryChanged()
        {
            lock (_gate)
            {
                if (!_connected) return;
                Rebind();
            }
            RaiseChanged();
        }

        private void OnConnected(string serial)
        {
            lock (_gate)
            {
                _generation++;
                _connected = true;
                string previousSerial = _serial;
                _serial = serial ?? "";
                _byIndex.Clear();
                _recordedIndices.Clear();
                _reported.Clear();

                try { LoadDefinitions(previousSerial); }
                catch (Exception ex)
                {
                    // The feed swallows a listener's exception so FlexLib's
                    // thread survives, which means a failure HERE would
                    // otherwise leave the previous radio's monitors running
                    // under a store that reports whatever it last reported
                    // (finding 13). The configuration is unavailable, and says
                    // so, rather than partly loaded and apparently healthy.
                    _entries.Clear();
                    _recordOnly.Clear();
                    _storeState = AlarmStoreState.Unavailable;
                    _storeProblem = "loading the alarm definitions failed: " + ex.Message;
                    Tracing.TraceLine("AlarmService: " + _storeProblem, TraceLevel.Error);
                }
                long now = _clock.NowMs;
                foreach (Entry e in _entries.Values)
                {
                    Post(e, e.Monitor.NewConnection(_generation, now));
                    if (_feed.IsTransmitting) Post(e, e.Monitor.SetTransmit(true, now));
                }
                _recorder.ConnectionStarted(_generation, _serial);
                Rebind();
                Tracing.TraceLine("AlarmService: connection generation " + _generation + " to " + _serial + " with "
                    + _entries.Count + " definition(s), store " + _storeState, TraceLevel.Info);
            }
            RaiseChanged();
        }

        private void OnDisconnected()
        {
            lock (_gate)
            {
                if (!_connected) return;
                _connected = false;
                long now = _clock.NowMs;
                foreach (Entry e in _entries.Values)
                {
                    Post(e, e.Monitor.MarkMissing(now, "disconnected"));
                    Post(e, e.Monitor.SetTransmit(false, now));
                    e.BoundIndex = -1;
                }
                _byIndex.Clear();
                _recordedIndices.Clear();
                _recorder.ConnectionEnded(_generation, "disconnected");
                Tracing.TraceLine("AlarmService: generation " + _generation + " ended; " + _entries.Count
                    + " definition(s) now unavailable", TraceLevel.Info);
            }
            RaiseChanged();
        }

        private void OnTransmitChanged(bool transmitting)
        {
            lock (_gate)
            {
                if (!_connected) return;
                long now = _clock.NowMs;
                foreach (Entry e in _entries.Values) Post(e, e.Monitor.SetTransmit(transmitting, now));
            }
        }

        // ── binding and persistence (under the lock) ──

        /// <summary>
        /// Read this radio's file and reconcile it with the monitors already
        /// running. A monitor whose definition is still in the file is KEPT —
        /// a reconnect preserves the last-known episode and its history
        /// (design section 2) — and replaced only when the file carries a
        /// newer revision. Monitors for definitions no longer in the file go;
        /// definitions new to the file get fresh monitors. A different radio's
        /// serial means none of the old monitors apply.
        /// </summary>
        private void LoadDefinitions(string previousSerial)
        {
            _recordOnly.Clear();
            if (!string.Equals(previousSerial, _serial, StringComparison.Ordinal)) _entries.Clear();

            if (_store == null)
            {
                _entries.Clear();
                _storeState = AlarmStoreState.Unavailable;
                _storeProblem = "the settings folder could not be resolved";
                return;
            }
            AlarmStoreLoad load = _store.Load(_serial);
            _storeState = load.State;
            _storeProblem = load.Problem;
            if (!load.IsUsable) { _entries.Clear(); return; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            long now = _clock.NowMs;
            foreach (AlarmDefinition d in load.File.Definitions)
            {
                if (string.IsNullOrEmpty(d.Id) || !seen.Add(d.Id)) continue;
                if (_entries.TryGetValue(d.Id, out Entry? existing))
                {
                    if (d.Revision > existing.Monitor.Definition.Revision)
                        Post(existing, existing.Monitor.Replace(d, now));
                }
                else
                {
                    _entries[d.Id] = new Entry(new AlarmMonitor(d));
                }
            }
            foreach (string id in new List<string>(_entries.Keys))
                if (!seen.Contains(id)) _entries.Remove(id);

            _recordOnly.AddRange(load.File.RecordOnlyMeters);
        }

        /// <summary>Resolve every selector against the current census and rebuild the index tables.</summary>
        private void Rebind()
        {
            _byIndex.Clear();
            _recordedIndices.Clear();
            // Epoch first, inventory second: if a rebuild lands between the two
            // reads the epoch is OLDER than the list, so the next reading (which
            // carries the newer epoch) forces a rebind rather than slipping
            // through. The other order could bind a new list under an old epoch.
            _boundEpoch = _feed.InventoryEpoch;
            IReadOnlyList<MeterDescriptor> inventory = _feed.Inventory;
            long now = _clock.NowMs;

            foreach (Entry e in _entries.Values)
            {
                MeterDescriptor? wasBoundTo = e.Resolution.IsResolved ? e.Resolution.Match : null;
                MeterSelectorResolution r = e.Monitor.Definition.Selector.Resolve(inventory);
                e.Resolution = r;

                // **A replaced meter is a lost meter (finding 3).** The selector
                // resolved before and resolves now, but to a DIFFERENT descriptor
                // — another index, another description, another range. The
                // readings that follow are from a different object, and the
                // baseline, dwell and trend history built on the old one must not
                // be continued onto it as if nothing happened. The monitor is told
                // the data went missing; the first reading on the new binding is
                // then a resumption, judged afresh.
                if (wasBoundTo != null && r.IsResolved && !wasBoundTo.Equals(r.Match))
                {
                    Post(e, e.Monitor.MarkMissing(now, "meter replaced: was " + wasBoundTo.Label + " at index " + wasBoundTo.Index
                        + ", now " + r.Match!.Label + " at index " + r.Match.Index));
                }

                if (r.IsResolved)
                {
                    int index = r.Match!.Index;
                    e.BoundIndex = index;
                    if (!_byIndex.TryGetValue(index, out List<Entry>? list)) _byIndex[index] = list = new List<Entry>();
                    list.Add(e);
                    // Bound whether or not it is enabled, so enabling later
                    // needs no rebind; RECORDED only while enabled (finding 11).
                    if (e.Monitor.Enabled) _recordedIndices.Add(index);
                }
                else
                {
                    e.BoundIndex = -1;
                    if (_connected && e.Monitor.Enabled)
                    {
                        Post(e, r.Status == MeterSelectorStatus.Ambiguous
                            ? e.Monitor.MarkAmbiguous(now, r.Candidates.Count + " meters match " + e.Monitor.Definition.Selector.Label)
                            : e.Monitor.MarkMissing(now, r.Status.ToString()));
                    }
                }
            }

            if (_connected && _storeState == AlarmStoreState.Empty) SeedDefaultRecordOnlyLocked(inventory);

            foreach (MeterSelector s in _recordOnly)
            {
                MeterSelectorResolution r = s.Resolve(inventory);
                if (r.IsResolved) _recordedIndices.Add(r.Match!.Index);
            }

            var recorded = new List<MeterDescriptor>();
            foreach (MeterDescriptor d in inventory)
                if (_recordedIndices.Contains(d.Index)) recorded.Add(d);
            _recorder.RecordedSetChanged(_generation, recorded);
        }

        /// <summary>
        /// The recorded set a radio starts with before the operator has saved
        /// anything: its PA temperature meter and its supply-voltage meters,
        /// whichever of them it publishes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why there is a default at all (#566, the one-selection ruling,
        /// folded 2026-10-01).</b> Noel ruled that ONE operator-chosen set
        /// feeds both the alarms and the diagnostic capture, with no hardcoded
        /// list in either place. Until this fold the capture had its own list
        /// in <c>CaptureMeterSet</c> — PA temperature and <c>+13.8A</c> — put
        /// there by a measurement: a bench capture across a real transmission
        /// carried 258 power lines and zero temperature lines, because the
        /// only path to temperature was an opt-in switch nobody knew about
        /// (#494). An operator who has never opened the alarms dialog must not
        /// lose that evidence again. So a radio with no alarm file yet starts
        /// with those meters in its record-only set, discovered from its own
        /// census rather than named model-wide.
        /// </para>
        /// <para>
        /// <b>A default, not a list.</b> It is seeded only while the store is
        /// Empty — before anything has ever been saved for this radio — and it
        /// is visible and untickable in the Recorded meters dialog. The first
        /// save writes whatever the operator left ticked, the store is then
        /// Loaded, and nothing is seeded again: unticking supply voltage stays
        /// unticked across reconnects. Seeding writes no file by itself.
        /// </para>
        /// </remarks>
        private void SeedDefaultRecordOnlyLocked(IReadOnlyList<MeterDescriptor> inventory)
        {
            foreach (MeterDescriptor d in inventory)
            {
                if (!AlarmPresets.IsPaTemperature(d) && !AlarmPresets.IsSupplyVoltage(d)) continue;
                MeterSelector s = MeterSelector.From(d);
                if (!_recordOnly.Contains(s) && s.Resolve(inventory).IsResolved) _recordOnly.Add(s);
            }
        }

        private void Post(Entry entry, IReadOnlyList<AlarmEvent> events)
        {
            foreach (AlarmEvent e in events)
            {
                if (e.Kind != AlarmEventKind.OldGenerationDiscarded) entry.LastEvent = e;
                Remember(e);
                if (_queue.Post(e)) continue;

                // Refused for lack of room. A state event can wait for the
                // list to catch up, but a WARNING refused here was never
                // heard, and with reminders off nothing would ever say it: the
                // monitor is told, and the next fresh sample on the alarm side
                // says it again (Astra's Track I review, finding 4). Under the
                // lock already, and the monitor only sets a flag.
                if (e.IsWarning)
                {
                    entry.Monitor.WarningNotDelivered(_clock.NowMs);
                    Tracing.TraceLine("AlarmService: a WARNING was refused by the full dispatch queue and will be said "
                        + "again on the next fresh reading: " + e, TraceLevel.Error);
                }
            }
        }

        /// <summary>
        /// The speech layer could not deliver this alarm's warning — it let a
        /// waiting warning go, or the reader took nothing. The monitor says it
        /// again on the next fresh sample on the alarm side. Called from the
        /// speech layer's own thread; takes the service lock briefly and
        /// speaks nothing.
        /// </summary>
        public void WarningNotDelivered(string alarmId)
        {
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(alarmId, out Entry? entry)) return;
                entry.Monitor.WarningNotDelivered(_clock.NowMs);
            }
        }

        /// <summary>
        /// The operator silenced this alarm's warning — the speech layer stood
        /// its sounding or waiting attempt down, or the delivery stood the
        /// sentence down during the tone. The monitor withholds the routine
        /// interval reminder until the reading worsens (#617); the Silenced
        /// event goes to the journal and the list like any other. Called from
        /// the speech layer's or the delivery's thread; takes the service lock
        /// briefly and speaks nothing.
        /// </summary>
        public void WarningSilenced(string alarmId)
        {
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(alarmId, out Entry? entry)) return;
                Post(entry, entry.Monitor.WarningSilenced(_clock.NowMs));
            }
            RaiseChanged();
        }

        private void Remember(AlarmEvent e)
        {
            _recent.Add(e);
            if (_recent.Count > RecentEventsKept) _recent.RemoveAt(0);
        }

        // ── the worker side ──

        private void Deliver(AlarmEvent e)
        {
            try { _recorder.RecordEvent(e); }
            catch (Exception ex) { Tracing.TraceLine("AlarmService: the recorder threw — " + ex.Message, TraceLevel.Warning); }
            try { EventDispatched?.Invoke(e); }
            catch (Exception ex) { Tracing.TraceLine("AlarmService: an event listener threw — " + ex.Message, TraceLevel.Warning); }
        }

        private void OnDispatchOverflow(long dropped)
        {
            try { DeliveryUnavailable?.Invoke(dropped); } catch { /* the meter thread must not see it */ }
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Tracing.TraceLine("AlarmService: a change listener threw — " + ex.Message, TraceLevel.Warning); }
        }

        /// <summary>Wait for everything posted so far to be delivered. Tests.</summary>
        public bool DrainDispatch(int timeoutMs) => _queue.Drain(timeoutMs);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watchdog?.Dispose();
            _feed.Reading -= OnReading;
            _feed.InventoryChanged -= OnInventoryChanged;
            _feed.Connected -= OnConnected;
            _feed.Disconnected -= OnDisconnected;
            _feed.TransmitChanged -= OnTransmitChanged;
            _queue.Dispose();
        }
    }
}
