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
        string EngineId, bool Processing, string? Failure = null);

    /// <summary>
    /// Control state is immutable and published by reference. Only the audio
    /// thread owns running/warming banks. Builders transfer ownership through
    /// an interlocked mailbox; retirement transfers it out to a worker.
    /// The pipeline's process mark also protects this slot at disconnect.
    /// </summary>
    public sealed class PcDenoiseSlot
    {
        private sealed record Parameters(float Strength, IReadOnlyDictionary<string, EngineSettingValue> Values);
        private sealed record Choice(long Generation, string Id, DenoiseDirection Direction = DenoiseDirection.Receive);
        private sealed record Built(Choice Choice, PerChannelDenoiserBank Bank);
        private readonly object _control = new();
        private readonly DenoiseEngineCatalog _catalog;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly ConcurrentDictionary<string, Parameters> _settings = new();
        private readonly ConcurrentDictionary<string, IReadOnlyList<EngineSettingDescriptor>> _schemas = new();
        private Choice _choice;
        private Built? _ready;
        private Built? _warming;
        private PerChannelDenoiserBank? _running;
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

        internal PcDenoiseSlot(DenoiseFamily family, string initialId, int sampleRate, int channels,
            DenoiseEngineCatalog catalog)
        {
            Family = family; _catalog = catalog; _channels = channels; _sampleRate = sampleRate;
            _choice = new Choice(0, initialId);
            var entry = catalog.Find(initialId);
            if (entry?.Factory != null && entry.Family == family && entry.Serves(Direction))
            {
                // Only today's two engines are initialised synchronously.
                // Subsequent choices (including model hashing) build on workers.
                _running = BuildBank(entry);
                _runningId = initialId;
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
        public IReadOnlyList<EngineSettingDescriptor> SettingsFor(string id) =>
            _schemas.TryGetValue(id, out var descriptors) ? descriptors : Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string id, string name)
        {
            var p = ParametersFor(id);
            var descriptor = SettingsFor(id).FirstOrDefault(s => s.Name == name);
            if (descriptor == null) throw new ArgumentException("Unknown setting", nameof(name));
            return p.Values.TryGetValue(name, out var value) && descriptor.Accepts(value) ? value : descriptor.Default;
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
                choice = new Choice(_choice.Generation + 1, id);
                Volatile.Write(ref _choice, choice);
                var abandoned = Interlocked.Exchange(ref _ready, null);
                abandoned?.Bank.Dispose(); // mailbox ownership is now ours, never the audio thread's
            }
            // Arrowing away and back keeps the existing native state. Audio
            // cancels its warming bank and acknowledges on its next buffer.
            if (!force && RunningEngineId == id) return true;
            _ = Task.Run(() =>
            {
                PerChannelDenoiserBank? bank = null;
                try
                {
                    bank = BuildBank(entry!);
                    if (!bank.IsAvailable) throw new InvalidOperationException(bank.EngineFor(0).UnavailabilityDetail);
                    lock (_control)
                    {
                        if (_closed != 0 || !ReferenceEquals(choice, _choice)) { bank.Dispose(); return; }
                        var previous = Interlocked.Exchange(ref _ready, new Built(choice, bank));
                        bank = null;
                        previous?.Bank.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    bank?.Dispose();
                    Publish(choice, false, ex.Message);
                }
            });
            return true;
        }

        private PerChannelDenoiserBank BuildBank(DenoiseEngineEntry entry)
        {
            var bank = new PerChannelDenoiserBank(entry.Factory!, _channels);
            _schemas[entry.Id] = bank.EngineFor(0).Settings;
            ApplyParameters(bank, building: true);
            return bank;
        }
        private void ApplyParameters(PerChannelDenoiserBank bank, bool building = false)
        {
            var p = ParametersFor(bank.EngineFor(0).Id);
            for (int ch = 0; ch < bank.Channels; ch++)
                foreach (var setting in p.Values)
                    if (building || !bank.EngineFor(ch).Settings.Any(s => s.Name == setting.Key && s.ChangesLatency))
                        bank.EngineFor(ch).TrySetSetting(setting.Key, setting.Value);
        }

        internal void Process(float[] audio, bool permitted, bool profilePresent, bool capturing,
            Action<PerChannelDenoiserBank>? prepareSpectral)
        {
            var choice = Volatile.Read(ref _choice);
            if (_warming != null && !ReferenceEquals(_warming.Choice, choice))
            { Retire(_warming.Bank); _warming = null; _warmSamples = 0; }
            var ready = Interlocked.Exchange(ref _ready, null);
            if (ready != null)
            {
                if (!ReferenceEquals(ready.Choice, choice)) Retire(ready.Bank);
                else { if (_warming != null) Retire(_warming.Bank); _warming = ready; _warmSamples = 0; }
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
            if (_running != null) ApplyParameters(_running);
            if (_warming != null)
            {
                ApplyParameters(_warming.Bank);
                if (_nextAudio.Length != audio.Length) _nextAudio = new float[audio.Length];
                audio.CopyTo(_nextAudio, 0);
                long late = _warming.Bank.LateFrames, failed = _warming.Bank.FallbackFrames;
                _warming.Bank.Process(_nextAudio, GetStrength(_warming.Choice.Id));
                bool produced = late == _warming.Bank.LateFrames && failed == _warming.Bank.FallbackFrames;
                if (_running != null) _running.Process(audio, GetStrength(RunningEngineId));
                if (failed != _warming.Bank.FallbackFrames)
                {
                    // Lateness is expected during helper start-up. A throw or
                    // lost availability is a failed start, not endless warming.
                    var failedBuild = _warming;
                    _warming = null;
                    Retire(failedBuild.Bank);
                    Publish(failedBuild.Choice, false, "processing-failed");
                    return;
                }
                if (produced) _warmSamples += audio.Length / _channels;
                else _warmSamples = 0;
                if (_warmSamples >= _sampleRate / 10 && ReferenceEquals(choice, Volatile.Read(ref _choice)))
                {
                    CrossFade(audio, _nextAudio, _channels);
                    var old = Interlocked.Exchange(ref _running, _warming.Bank);
                    Volatile.Write(ref _runningId, _warming.Choice.Id);
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
                if (choice.Id == RunningEngineId && _announced != choice.Generation &&
                    late == _running.LateFrames && failed == _running.FallbackFrames)
                { _announced = choice.Generation; Publish(choice, true); }
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
        private void Publish(Choice choice, bool processing, string? failure = null) =>
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
                handler?.Invoke(this, new(choice.Direction, Family, choice.Id, processing, failure));
            });
        private static void Retire(PerChannelDenoiserBank bank) =>
            ThreadPool.QueueUserWorkItem(_ => bank.Dispose());

        // Called only after the pipeline acquired its process mark. A timed
        // out disconnect closes admission but deliberately leaves these owned
        // banks alive rather than freeing a native engine inside ProcessFrame.
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
