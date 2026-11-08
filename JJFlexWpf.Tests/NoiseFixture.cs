using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A noise controller over fakes, for Track E's windowless test classes: a
/// fake radio, a speaker that records every sentence, a hand-cranked
/// scheduler, an in-memory intent store, and a receive pipeline whose
/// computer engines are fake engines carrying the REAL descriptors.
/// </summary>
internal sealed class NoiseFixture : IDisposable
{
    internal sealed class FakeRadio : INoiseRadio
    {
        public string Serial { get; set; } = "1234-5678";
        public string Mode { get; set; } = "USB";
        /// <summary>The slice the fake reads; a test switches it, with the values, to model the radio activating another slice.</summary>
        public object? SliceIdentity { get; set; } = "slice A";
        public bool PcAudioOn { get; set; } = true;
        public Dictionary<string, bool> Switches { get; } = new();
        public Dictionary<string, int> Levels { get; } = new();
        public List<string> Writes { get; } = new();
        public Dictionary<RadioCapability, string> Refused { get; } = new();
        public bool ReadSwitch(DenoiseEngineEntry e) => Switches.TryGetValue(e.Id, out var on) && on;
        public void WriteSwitch(DenoiseEngineEntry e, bool on) { Writes.Add(e.Id + "=" + (on ? "on" : "off")); Switches[e.Id] = on; }
        public int ReadLevel(DenoiseEngineEntry e) => Levels.TryGetValue(e.Id, out var l) ? l : 0;
        public void WriteLevel(DenoiseEngineEntry e, int level) { Writes.Add(e.Id + ".level=" + level); Levels[e.Id] = level; }
        public string? CapabilityRefusal(RadioCapability c) => Refused.TryGetValue(c, out var why) ? why : null;
        public bool CapabilityClosed(RadioCapability c) => Refused.ContainsKey(c);
        public bool ClosedByHardware(RadioCapability c) => Refused.TryGetValue(c, out var why) && why.Contains("model");
    }

    internal sealed class Speaker : INoiseSpeaker
    {
        public VerbosityLevel Verbosity { get; set; } = VerbosityLevel.Chatty;
        public List<(string Text, string Subject, VerbosityLevel Level)> Said { get; } = new();
        public void Say(string text, string subject, VerbosityLevel level) => Said.Add((text, subject, level));
        public string Last => Said.Count == 0 ? "" : Said[^1].Text;
        public void Clear() => Said.Clear();
    }

    internal sealed class Scheduler : INoiseScheduler
    {
        private readonly List<(Action Action, Handle Handle)> _queue = new();
        private sealed class Handle : IDisposable { public bool Cancelled; public void Dispose() => Cancelled = true; }
        public IDisposable After(TimeSpan delay, Action action) { var h = new Handle(); _queue.Add((action, h)); return h; }
        public int Pending => _queue.Count(q => !q.Handle.Cancelled);
        public void RunAll()
        {
            var now = _queue.ToList(); _queue.Clear();
            foreach (var (action, handle) in now) if (!handle.Cancelled) action();
        }
    }

    internal sealed class Intents : INoiseIntentStore
    {
        public Dictionary<(string, string), int> Held { get; } = new();
        public int? Get(string serial, string id) => Held.TryGetValue((serial, id), out var l) ? l : null;
        public void Set(string serial, string id, int level) => Held[(serial, id)] = level;
    }

    internal sealed class CwCost : INnrCwCostSource
    {
        public double? PremiumCw99thPercentileMs { get; set; }
    }

    internal sealed class Engine : IDenoiseEngine
    {
        private readonly Dictionary<string, EngineSettingValue> _values = new();
        public Engine(string id, IReadOnlyList<EngineSettingDescriptor> settings, IReadOnlyDictionary<string, EngineSettingValue> initial, int latency)
        {
            Id = id; Settings = settings; LatencySamples = latency;
            foreach (var d in settings) _values[d.Name] = initial.TryGetValue(d.Name, out var v) ? v : d.Default;
        }
        public string Id { get; }
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public bool IsAvailable => true;
        public int LatencySamples { get; }
        public IReadOnlyList<EngineSettingDescriptor> Settings { get; }
        public EngineSettingValue GetSetting(string name) => _values[name];
        public bool TrySetSetting(string name, EngineSettingValue value)
        {
            var d = Settings.FirstOrDefault(x => x.Name == name);
            if (d == null || !d.Accepts(value)) return false;
            _values[name] = value; return true;
        }
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++) output[i] = input[i] * 0.5f;
            return true;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    public FakeRadio Radio { get; } = new();
    public Speaker Spoken { get; } = new();
    public Scheduler Clock { get; } = new();
    public Intents Held { get; } = new();
    public CwCost Cost { get; } = new();
    public AudioOutputConfig Config { get; } = new();
    public RxAudioPipeline Pipeline { get; }
    public NoiseController Noise { get; }
    public DenoiseEngineCatalog Catalog { get; }
    public int Persisted;
    public bool RadioPresent = true;
    public Dictionary<string, Unavailability> Unavailable { get; } = new();
    public ConcurrentBag<string> Built { get; } = new();

    public NoiseFixture()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var real = new DenoiseEngineCatalog();
        DenoiseEngineEntry Pc(string id, int latency = 0)
        {
            var row = real.Find(id)!;
            return row with
            {
                Factory = row.Factory == null ? null : initial => { Built.Add(id); return new Engine(id, row.Settings, initial, latency); },
                Probe = () => Unavailable.TryGetValue(id, out var why) ? why : Unavailability.None,
            };
        }
        var rows = real.Entries.Where(e => e.Side == DenoiseSide.Radio).ToList();
        rows.AddRange(new[] { Pc("nr2"), Pc("nr4"), Pc("spectral_capture"), Pc("rnnoise"), Pc("deepfilternet3"), Pc("nnr"), Pc("nvidia_afx") });
        Catalog = new DenoiseEngineCatalog(rows);
        Pipeline = new RxAudioPipeline(catalog: Catalog);
        Noise = new NoiseController(Catalog, () => RadioPresent ? Radio : null, () => Pipeline, () => Config,
            () => Persisted++, Spoken, Held, Clock, Cost);
    }

    /// <summary>Feed audio until a slot runs the engine it has chosen.</summary>
    public void Settle(int buffers = 200)
    {
        var audio = new float[960];
        var time = Stopwatch.StartNew();
        while (time.ElapsedMilliseconds < 5000)
        {
            bool done = (!Pipeline.NeuralSlot.Enabled || Pipeline.NeuralSlot.RunningEngineId == Pipeline.NeuralSlot.ChosenEngineId)
                && (!Pipeline.SpectralSlot.Enabled || Pipeline.SpectralSlot.RunningEngineId == Pipeline.SpectralSlot.ChosenEngineId);
            if (done) return;
            Array.Fill(audio, 0.1f);
            Pipeline.Process(audio);
            Thread.Sleep(1);
        }
    }

    public void Dispose() => Pipeline.Dispose();
}
