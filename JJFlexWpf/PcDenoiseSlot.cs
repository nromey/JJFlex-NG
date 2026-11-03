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
        private sealed record Parameters(float Strength, IReadOnlyDictionary<string, EngineSettingValue> Values);
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
            internal IReadOnlyDictionary<string, EngineSettingValue>? Initial;
        }
        /// <summary>
        /// What the running bank was built from: its id and the starting values
        /// it got, published together so Select can tell whether arrowing back
        /// to it would keep an instance built with an out-of-date latency setting.
        /// </summary>
        private sealed record RunningBuild(string Id, IReadOnlyDictionary<string, EngineSettingValue> Initial);
        private RunningBuild? _runningBuild;
        private readonly object _control = new();
        private readonly DenoiseEngineCatalog _catalog;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly ConcurrentDictionary<string, Parameters> _settings = new();
        private Choice _choice;
        private Built? _ready;
        private Built? _warming;
        private PerChannelDenoiserBank? _running;
        private Parameters? _runningApplied;
        private int _warmSamples;
        private int _closed;
        private long _announced = -1;
        private int _enabled;
        private bool _wasActive;
        private string _runningId = "";
        private float[] _nextAudio = Array.Empty<float>();
        public DenoiseFamily Family { get; }
        public DenoiseDirection Direction => DenoiseDirection.Receive;
        public string ChosenEngineId => Volatile.Read(ref _choice).Id;
        public string RunningEngineId => Volatile.Read(ref _runningId);
        public bool HasBeenEnabled { get; private set; }
        internal void RememberUse() => HasBeenEnabled = true;
        public bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set { if (value) HasBeenEnabled = true; Volatile.Write(ref _enabled, value ? 1 : 0); }
        }
        public event EventHandler<DenoiseSelectionResult>? SelectionResult;
        internal Action<string>? InstallHook { get; set; }
        internal bool BuildDelivered(string id)
        {
            var choice = Volatile.Read(ref _choice);
            return choice.Id == id && (ReferenceEquals(Volatile.Read(ref _ready)?.Choice, choice) ||
                ReferenceEquals(Volatile.Read(ref _warming)?.Choice, choice));
        }

        internal PcDenoiseSlot(DenoiseFamily family, string initialId, int sampleRate, int channels,
            DenoiseEngineCatalog catalog)
        {
            Family = family; _catalog = catalog; _channels = channels; _sampleRate = sampleRate;
            _choice = new Choice(0, initialId);
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
                    _running = initial;
                    _runningId = initialId;
                    _runningBuild = new RunningBuild(initialId, builtFrom);
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

        private Parameters ParametersFor(string id) => _settings.GetOrAdd(id,
            key => new Parameters(key == "rnnoise" ? 0.8f : 1f, new Dictionary<string, EngineSettingValue>()));
        public float GetStrength(string id) => ParametersFor(id).Strength;
        public void SetStrength(string id, float value)
        {
            if (!float.IsFinite(value)) return;
            _settings.AddOrUpdate(id, _ => new Parameters(Math.Clamp(value, 0, 1), new Dictionary<string, EngineSettingValue>()),
                (_, p) => p with { Strength = Math.Clamp(value, 0, 1) });
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
            _settings.AddOrUpdate(id, _ => new Parameters(1, new Dictionary<string, EngineSettingValue> { [name] = value }),
                (_, p) => p with { Values = new Dictionary<string, EngineSettingValue>(p.Values) { [name] = value } });
            if (descriptor.ChangesLatency && ChosenEngineId == id) Select(id, out _, force: true);
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
                choice = new Choice(_choice.Generation + 1, id) { Entry = entry, Force = force };
                Volatile.Write(ref _choice, choice);
                var abandoned = Interlocked.Exchange(ref _ready, null);
                abandoned?.Bank.Dispose(); // mailbox ownership is now ours, never the audio thread's
            }
            // Arrowing away and back keeps the existing native state. Audio
            // cancels its warming bank and acknowledges on its next buffer.
            // Not when a latency setting changed since that bank was built: it
            // can change only through a new build, and the change was stored
            // while another engine was chosen, so nothing has rebuilt it yet.
            if (!force && RunningEngineId == id && RunningLatencyCurrent(id)) return true;
            RequestBuild(choice);
            return true;
        }

        private bool RunningLatencyCurrent(string id)
        {
            var built = Volatile.Read(ref _runningBuild);
            if (built == null || built.Id != id) return false;
            foreach (var descriptor in SettingsFor(id))
            {
                if (!descriptor.ChangesLatency) continue;
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
                    var previous = Interlocked.Exchange(ref _ready, new Built(choice, bank) { Initial = builtFrom });
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
            var bank = new PerChannelDenoiserBank(() => entry.Factory!(initial), _channels);
            try
            {
                Parameters? applied = null;
                ApplyParameters(bank, entry.Id, ref applied);
                return bank;
            }
            catch { bank.Dispose(); throw; }
        }
        private void ApplyParameters(PerChannelDenoiserBank bank, string id, ref Parameters? applied)
        {
            var p = ParametersFor(id);
            if (ReferenceEquals(p, applied)) return;
            for (int ch = 0; ch < bank.Channels; ch++)
                foreach (var setting in p.Values)
                    if (!SettingsFor(id).Any(s => s.Name == setting.Key && s.ChangesLatency))
                        bank.EngineFor(ch).TrySetSetting(setting.Key, setting.Value);
            applied = p;
        }

        internal void Process(float[] audio, bool permitted, bool profilePresent, bool capturing,
            Action<PerChannelDenoiserBank>? prepareSpectral)
        {
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
            if (_running != null) prepareSpectral?.Invoke(_running);
            if (_warming != null) prepareSpectral?.Invoke(_warming.Bank);
            bool active = Enabled && permitted;
            if (Family == DenoiseFamily.Spectral && capturing) active = false;
            if (choice.Id == "spectral_capture" && !profilePresent) active = false;
            if (!active)
            {
                if (_wasActive) { _running?.ClearCarry(); _warming?.Bank.ClearCarry(); }
                _wasActive = false;
                // Keep the built choice, but do not warm or announce it with no audio.
                return;
            }
            _wasActive = true;
            if (_running != null) ApplyParameters(_running, RunningEngineId, ref _runningApplied);
            if (_warming != null)
            {
                ApplyParameters(_warming.Bank, _warming.Choice.Id, ref _warming.Applied);
                if (_nextAudio.Length != audio.Length) _nextAudio = new float[audio.Length];
                audio.CopyTo(_nextAudio, 0);
                long late = _warming.Bank.LateFrames, failed = _warming.Bank.FallbackFrames;
                _warming.Bank.Process(_nextAudio, GetStrength(_warming.Choice.Id));
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
                if (_running != null) _running.Process(audio, GetStrength(RunningEngineId));
                if (produced) _warmSamples += audio.Length / _channels;
                else _warmSamples = 0;
                if (_warmSamples >= _sampleRate / 10 && ReferenceEquals(choice, Volatile.Read(ref _choice)))
                {
                    InstallHook?.Invoke("before-running-id");
                    CrossFade(audio, _nextAudio, _channels);
                    var old = Interlocked.Exchange(ref _running, _warming.Bank);
                    Volatile.Write(ref _runningBuild, _warming.Initial == null ? null : new RunningBuild(_warming.Choice.Id, _warming.Initial));
                    Volatile.Write(ref _runningId, _warming.Choice.Id);
                    InstallHook?.Invoke("after-running-id");
                    _runningApplied = _warming.Applied;
                    _warming = null;
                    if (old != null) Retire(old);
                    _announced = choice.Generation;
                    Publish(choice, true);
                }
                return;
            }
            if (_running != null)
            {
                long late = _running.LateFrames, failed = _running.FallbackFrames;
                _running.Process(audio, GetStrength(RunningEngineId));
                if (choice.Id == RunningEngineId && !choice.Force && _announced != choice.Generation &&
                    late == _running.LateFrames && failed == _running.FallbackFrames)
                { _announced = choice.Generation; Publish(choice, true); }
            }
        }

        private void StopFailedChoice(Choice choice)
        {
            // Only this audio thread releases the audible bank. A builder
            // leaves a failure on its choice; the next buffer applies it,
            // including while disabled, before reporting that the slot stopped.
            var old = Interlocked.Exchange(ref _running, null);
            if (old != null) Retire(old);
            Volatile.Write(ref _runningBuild, null);
            Volatile.Write(ref _runningId, "");
            _runningApplied = null;
            if (_warming != null) { Retire(_warming.Bank); _warming = null; }
            _warmSamples = 0; _wasActive = false;
            if (Interlocked.Exchange(ref choice.FailureReported, 1) == 0)
                Publish(choice, false, Volatile.Read(ref choice.Failure), slotStopped: true);
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
        private void Publish(Choice choice, bool processing, string? failure = null, bool slotStopped = false) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
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
            _running?.Dispose(); _running = null;
            _warming?.Bank.Dispose(); _warming = null;
        }

        internal DenoiseEngineSettings[] ExportSettings() => _settings.Select(pair => new DenoiseEngineSettings {
            EngineId = pair.Key, Direction = Direction, Strength = pair.Value.Strength,
            Values = pair.Value.Values.Select(v => new DenoiseNamedValue { Name = v.Key, Number = v.Value.Number, Choice = v.Value.Choice }).ToList()
        }).ToArray();
        internal void RestoreSettings(IEnumerable<DenoiseEngineSettings> records)
        {
            foreach (var record in records)
            {
                var row = _catalog.Find(record.EngineId);
                if (record.Direction != Direction || row?.Family != Family || !row.Serves(Direction)) continue;
                _settings[record.EngineId] = new Parameters(float.IsFinite(record.Strength) ? Math.Clamp(record.Strength, 0, 1) : ParametersFor(record.EngineId).Strength,
                    record.Values.GroupBy(v => v.Name).ToDictionary(v => v.Key, v => new EngineSettingValue(v.Last().Number, v.Last().Choice)));
            }
        }
    }
}
