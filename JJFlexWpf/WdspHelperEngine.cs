using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// A noise reducer that runs in the WDSP helper (NNR or NR2), and looks to
    /// the rest of JJ Flexible exactly like an in-process engine. One instance
    /// is one mono channel on the helper's pipe; the bank makes two for stereo.
    /// This side is MIT and holds no GPL code: frames, names and numbers cross
    /// the pipe, nothing else.
    /// </summary>
    /// <remarks>
    /// <para><b>The audio thread never waits.</b> <see cref="ProcessFrame"/>
    /// hands frame n to the writer through a preallocated single-producer ring
    /// and wakes it with a kernel event, then looks for frame n minus k's
    /// answer in a ring the reader thread fills under a per-slot version
    /// counter. Not there, torn, or the wrong seq: it returns false, which the
    /// bank counts as a late frame. No lock, no wait, no I/O, no allocation, no
    /// trace.</para>
    /// <para><b>The delay is fixed for the instance's life</b>: the channel's
    /// algorithmic latency from the first successful open, plus k frames of
    /// round trip. It is reported even while the helper is down, so the bank's
    /// delayed pass-through lines up with what came before (no jump).</para>
    /// <para><b>A restart is invisible to the slot.</b> The host reopens this
    /// channel on the new helper with the current settings; this object, and
    /// so the slot's running engine and the operator's choice, never change.</para>
    /// </remarks>
    public sealed class WdspHelperEngine : IDenoiseEngine
    {
        public const string HelperFileName = "jjflex-wdsp-helper.exe";

        /// <summary>k: how many frames behind the input an answer is expected. Per instance, never on the wire.</summary>
        public const int DefaultPipelineDepth = 2;

        /// <summary>
        /// Where the app's copy of the helper lives. Set by
        /// <c>NativeLoader.Initialize</c>; when unset, or when it answers
        /// nothing, the default is <c>runtimes\win-{arch}\native</c> under the
        /// app base directory. No app-directory fallback.
        /// </summary>
        public static Func<string?>? HelperLocator { get; set; }

        public static string DefaultHelperPath() => Path.Combine(AppContext.BaseDirectory, "runtimes",
            "win-" + (Environment.Is64BitProcess ? "x64" : "x86"), "native", HelperFileName);

        internal static string LocateHelper()
        {
            try
            {
                string? fromLoader = HelperLocator?.Invoke();
                if (fromLoader != null) return fromLoader;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("WdspHelperEngine: helper locator failed: " + ex.Message);
            }
            return DefaultHelperPath();
        }

        /// <summary>
        /// The catalog's probe. Never starts a process and never opens the pipe,
        /// the same rule as building a menu never loading a native library: it
        /// answers from the file being there and what the host last knew.
        /// </summary>
        public static Unavailability Probe(string engineId)
        {
            var host = WdspHost.DefaultIfCreated;
            if (host != null) return host.Probe(engineId);
            return File.Exists(LocateHelper()) ? Unavailability.None : Unavailability.HelperMissing;
        }

        private sealed class Slot
        {
            public readonly EngineSettingDescriptor D;
            // Asked-for value. Written only by the owning thread, under a version
            // counter (odd while writing) so the writer thread reads it whole.
            public int Version;
            public double Number;
            public string? Choice;
            public bool Has;
            /// <summary>For a ChangesLatency setting: the value the instance was built with.</summary>
            public WdspValue? OpenedWith;
            /// <summary>The value in effect, from the helper's acks.</summary>
            public volatile StrongBox<EngineSettingValue>? Applied;

            public Slot(EngineSettingDescriptor d) => D = d;

            public void Write(EngineSettingValue v)
            {
                Interlocked.Increment(ref Version);
                Number = v.Number;
                Choice = v.Choice;
                Has = true;
                Interlocked.Increment(ref Version);
            }

            public bool Read(out int version, out EngineSettingValue value)
            {
                var spin = new SpinWait();
                while (true)
                {
                    int before = Volatile.Read(ref Version);
                    if ((before & 1) == 0)
                    {
                        bool has = Has;
                        value = new EngineSettingValue(Number, Choice);
                        Interlocked.MemoryBarrier();
                        if (Volatile.Read(ref Version) == before) { version = before; return has; }
                    }
                    spin.SpinOnce();
                }
            }

            public bool AskedIs(EngineSettingValue v) => Has && (v.Choice != null
                ? Choice != null && string.Equals(Choice, v.Choice, StringComparison.Ordinal)
                : Choice == null && BitConverter.DoubleToInt64Bits(Number) == BitConverter.DoubleToInt64Bits(v.Number));
        }

        internal WdspHost Host { get; }
        public string Id { get; }
        internal byte Direction { get; }
        internal int PipelineDepth { get; }
        internal bool OfflineWait { get; }
        public int SampleRate => WdspProtocol.SampleRate;
        public int FrameLength => WdspProtocol.FrameLength;

        private WdspChannel? _channel;
        private int _available;
        private int _reason = (int)Unavailability.HelperNotRunning;
        private string _detail = "starting";
        private int _latencySamples;
        private uint _firstLatency;
        private bool _opened;
        private IReadOnlyList<EngineSettingDescriptor> _settings = Array.Empty<EngineSettingDescriptor>();
        private volatile Slot[]? _slots;
        private readonly IReadOnlyDictionary<string, EngineSettingValue>? _starting;
        private int _disposed;
        private readonly ManualResetEventSlim _firstOpen = new(false);

        /// <summary>Set under the host's lock: not reopened automatically (mismatch, init failure).</summary>
        internal bool Final;
        /// <summary>Set under the host's lock: this connection refused to open the channel.</summary>
        internal WdspConnection? RefusedOn;

        /// <summary>
        /// A helper engine on the app's one helper. Waits, on the calling (build
        /// worker) thread and bounded, for its channel to open; if it does not,
        /// the engine is simply unavailable and the slot's build fails.
        /// </summary>
        /// <param name="engineId">The catalog id, "nnr" or "nr2".</param>
        /// <param name="direction">Only receive is wired.</param>
        /// <param name="starting">Starting values, sent as the channel's initial settings.</param>
        public WdspHelperEngine(string engineId, DenoiseDirection direction = DenoiseDirection.Receive,
            IReadOnlyDictionary<string, EngineSettingValue>? starting = null)
            : this(WdspHost.Default, engineId, direction, starting) { }

        /// <param name="offlineWait">
        /// The offline bench only, and never set by the catalog: <see cref="ProcessFrame"/>
        /// then waits, bounded, for frame n minus k's answer, so a faster-than-real-time
        /// caller gets exactly the live path's delay and never runs ahead of the helper.
        /// </param>
        internal WdspHelperEngine(WdspHost host, string engineId, DenoiseDirection direction,
            IReadOnlyDictionary<string, EngineSettingValue>? starting, int pipelineDepth = DefaultPipelineDepth,
            bool offlineWait = false)
        {
            Host = host;
            Id = engineId;
            Direction = direction == DenoiseDirection.Transmit ? WdspProtocol.Transmit : WdspProtocol.Receive;
            PipelineDepth = Math.Max(1, pipelineDepth);
            OfflineWait = offlineWait;
            _starting = starting;
            host.Attach(this);
            if (!_firstOpen.Wait(host.Policy.ConstructionWait))
                Trace.WriteLine($"WdspHelperEngine: {Id} did not open within {host.Policy.ConstructionWait.TotalMilliseconds} ms ({UnavailabilityDetail})");
        }

        // ------------------------------------------------------------------
        // IDenoiseEngine
        // ------------------------------------------------------------------

        /// <summary>True only while the helper is connected and this channel is open. A plain volatile read: the bank reads it per frame.</summary>
        public bool IsAvailable => Volatile.Read(ref _available) != 0;

        public Unavailability Unavailability => IsAvailable ? Unavailability.None : (Unavailability)Volatile.Read(ref _reason);

        /// <summary>For the trace only; never spoken.</summary>
        public string UnavailabilityDetail => IsAvailable ? "" : Volatile.Read(ref _detail);

        /// <summary>The last handshake's descriptors for this engine: one list object, the same on every call.</summary>
        public IReadOnlyList<EngineSettingDescriptor> Settings => Volatile.Read(ref _settings);

        /// <summary>Algorithmic latency from the first successful open, plus k frames. Constant; reported while unavailable; 0 if it never opened.</summary>
        public int LatencySamples => Volatile.Read(ref _latencySamples);

        internal WdspChannel? Channel => Volatile.Read(ref _channel);

        public EngineSettingValue GetSetting(string name)
        {
            var slots = _slots;
            if (slots != null)
                foreach (var s in slots)
                    if (s.D.Name == name)
                    {
                        if (s.Applied is { } applied) return applied.Value;
                        return s.Read(out _, out var asked) ? asked : s.D.Default;
                    }
            throw new ArgumentException("Unknown setting", nameof(name));
        }

        /// <summary>
        /// Runs on the audio thread, every buffer, for every stored value. So:
        /// no blocking, no allocation, and nothing at all when the value equals
        /// the last one asked for. A changed value is recorded and the writer
        /// woken; the ack later updates <see cref="GetSetting"/>. A latency
        /// setting is never sent to an open channel: it answers true only for
        /// the value the instance was built with.
        /// </summary>
        public bool TrySetSetting(string name, EngineSettingValue value)
        {
            var slots = _slots;
            if (slots == null) return false;
            Slot? s = null;
            for (int i = 0; i < slots.Length; i++)
                if (string.Equals(slots[i].D.Name, name, StringComparison.Ordinal)) { s = slots[i]; break; }
            if (s == null || !s.D.Accepts(value)) return false;
            if (s.D.ChangesLatency)
            {
                var built = s.OpenedWith;
                return built.HasValue ? built.Value.SameAs(WdspValue.From(value)) : s.AskedIs(value);
            }
            if (s.AskedIs(value)) return true;
            s.Write(value);
            Volatile.Read(ref _channel)?.Conn.Wake.Set();
            return true;
        }

        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            var ch = Volatile.Read(ref _channel);
            if (ch == null) return false;
            ulong n = ch.NextSeq++;
            if (ch.Opened && !ch.Closed && (ch.TrySend(input, n) || (OfflineWait && WaitToSend(ch, input, n)))) ch.Conn.Wake.Set();
            if (n < (ulong)PipelineDepth) return false;
            ulong want = n - (ulong)PipelineDepth;
            if ((long)want < Volatile.Read(ref ch.DiscardBelow)) return false;
            if (OfflineWait) WaitOffline(ch, want);
            return ch.TryTake(want, output);
        }

        /// <summary>Offline only: a caller faster than real time waits for room rather than dropping a frame.</summary>
        private bool WaitToSend(WdspChannel ch, ReadOnlySpan<float> input, ulong seq)
        {
            long deadline = WdspHost.Now + WdspHost.Ticks(Host.Policy.OfflineFrameWait);
            while (ch.Opened && !ch.Closed && WdspHost.Now < deadline)
            {
                ch.Conn.Wake.Set();
                Thread.Sleep(1);
                if (ch.TrySend(input, seq)) return true;
            }
            return false;
        }

        private void WaitOffline(WdspChannel ch, ulong want)
        {
            long deadline = WdspHost.Now + WdspHost.Ticks(Host.Policy.OfflineFrameWait);
            while (!ch.Has(want) && ch.Opened && !ch.Closed)
            {
                long left = deadline - WdspHost.Now;
                if (left <= 0) return;
                ch.ResultArrived!.WaitOne((int)Math.Max(1, left * 1000 / Stopwatch.Frequency));
            }
        }

        /// <summary>As if newly created: close this channel and open a new one, same engine, direction and settings. Frames are late meanwhile.</summary>
        public void Reset() => Host.Reopen(this);

        /// <summary>Queue the close and return. Never waits for the ack; safe from any thread, more than once, and while the reader is delivering.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Host.Detach(this);
        }

        // ------------------------------------------------------------------
        // Called by the host, under its lock unless named otherwise
        // ------------------------------------------------------------------

        internal void SetChannelLocked(WdspChannel? channel) => Volatile.Write(ref _channel, channel);

        internal void SetUnavailableLocked(Unavailability reason, string detail, bool final)
        {
            Volatile.Write(ref _available, 0);
            if (Final && !final) return; // a final reason outlives the host's later troubles
            Volatile.Write(ref _reason, (int)reason);
            Volatile.Write(ref _detail, detail);
            if (final) { Final = true; _firstOpen.Set(); }
        }

        internal void ClearFinalLocked() { Final = false; RefusedOn = null; }

        internal void ReleaseWaiter() => _firstOpen.Set();

        /// <summary>The first handshake's descriptors become this instance's for good; the starting values are checked against them.</summary>
        internal void AdoptDescriptorsLocked(IReadOnlyList<EngineSettingDescriptor> offered)
        {
            if (_slots != null) return;
            var slots = new Slot[offered.Count];
            for (int i = 0; i < slots.Length; i++) slots[i] = new Slot(offered[i]);
            if (_starting != null)
                foreach (var (name, value) in _starting)
                {
                    var s = Array.Find(slots, x => x.D.Name == name);
                    if (s == null) Trace.WriteLine($"WdspHelperEngine: {Id} has no setting {name}; starting value ignored");
                    else if (!s.D.Accepts(value)) Trace.WriteLine($"WdspHelperEngine: {Id} refuses starting value {value} for {name}; using the helper's default");
                    else s.Write(value);
                }
            Volatile.Write(ref _settings, offered);
            _slots = slots;
        }

        /// <summary>Every value this engine holds, for OpenChannel; and the versions this channel will therefore already have.</summary>
        internal List<WdspNamedValue> ComposeInitialLocked(out int[] versions)
        {
            var slots = _slots!;
            versions = new int[slots.Length];
            var list = new List<WdspNamedValue>();
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                bool has = s.Read(out versions[i], out var asked);
                if (s.D.ChangesLatency && s.OpenedWith is { } built) list.Add(new(s.D.Name, built));
                else if (has) list.Add(new(s.D.Name, WdspValue.From(asked)));
            }
            return list;
        }

        /// <summary>Writer thread: a SetSetting for every value asked for since this channel last heard.</summary>
        internal void CollectChangedSettings(WdspChannel ch, List<byte[]> into)
        {
            var slots = _slots;
            if (slots == null) return;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (s.D.ChangesLatency) continue;
                if (!s.Read(out int version, out var value) || version == ch.SentVersions[i]) continue;
                into.Add(WdspProtocol.Encode(new WdspSetSetting(ch.Id, new WdspNamedValue(s.D.Name, WdspValue.From(value)))));
                ch.SentVersions[i] = version;
            }
        }

        /// <summary>Reader thread: the helper answered an OpenChannel.</summary>
        internal void OnOpenAck(WdspChannel ch, WdspOpenChannelAck ack)
        {
            lock (Host.Gate)
            {
                if (ack.Result == WdspResult.Ok)
                {
                    if (ch.Closed || !ReferenceEquals(Volatile.Read(ref _channel), ch)) return;
                    var slots = _slots;
                    if (!_opened)
                    {
                        _opened = true;
                        _firstLatency = ack.LatencySamples;
                        Volatile.Write(ref _latencySamples, (int)ack.LatencySamples + PipelineDepth * WdspProtocol.FrameLength);
                    }
                    else if (ack.LatencySamples != _firstLatency)
                    {
                        string why = $"{Id} reopened with a latency of {ack.LatencySamples} samples; this instance was built on {_firstLatency}";
                        Trace.WriteLine("WdspHelperEngine: " + why);
                        SetUnavailableLocked(Unavailability.HelperVersionMismatch, why, final: true);
                        Host.CloseLocked(ch);
                        SetChannelLocked(null);
                        return;
                    }
                    if (slots != null)
                        foreach (var applied in ack.Applied)
                        {
                            int i = Array.FindIndex(slots, x => x.D.Name == applied.Name);
                            if (i < 0) continue;
                            var s = slots[i];
                            var v = applied.Value.ToSetting();
                            s.Applied = new StrongBox<EngineSettingValue>(v);
                            if (s.D.ChangesLatency) s.OpenedWith ??= applied.Value;
                            if (!s.Has) { s.Write(v); ch.SentVersions[i] = Volatile.Read(ref s.Version); }
                        }
                    ch.Latency = ack.LatencySamples;
                    Volatile.Write(ref ch.DiscardBelow, (ack.LatencySamples + WdspProtocol.FrameLength - 1) / WdspProtocol.FrameLength);
                    ch.Opened = true;
                    if (!Final)
                    {
                        Volatile.Write(ref _reason, (int)Unavailability.None);
                        Volatile.Write(ref _detail, "");
                        Volatile.Write(ref _available, 1);
                    }
                    _firstOpen.Set();
                    return;
                }
                var mapped = WdspProtocol.MapOpenResult(ack.Result);
                string detail = $"{Id}: the helper refused channel {ack.Channel}: " +
                    (WdspProtocol.IsKnownResult(ack.Result) ? ack.Result.ToString() : "code " + (ushort)ack.Result) +
                    (ack.Detail.Length > 0 ? " (" + ack.Detail + ")" : "");
                Trace.WriteLine("WdspHelperEngine: " + detail);
                ch.Closed = true; // nothing was created, so nothing to close
                ch.Conn.Lives = Array.FindAll(ch.Conn.Lives, l => l != ch);
                ch.Conn.ById.TryRemove(ch.Id, out _);
                if (ReferenceEquals(Volatile.Read(ref _channel), ch)) SetChannelLocked(null);
                RefusedOn = ch.Conn;
                if (mapped is { } reason && reason != Unavailability.None) SetUnavailableLocked(reason, detail, final: true);
                else Volatile.Write(ref _detail, detail); // JJ Flexible's own bug: traced, availability unchanged
                _firstOpen.Set();
            }
        }

        /// <summary>Reader thread: the value in effect after a SetSetting. A refusal is traced; GetSetting keeps the value in effect.</summary>
        internal void OnSettingAck(WdspSettingAck ack)
        {
            if (ack.Result != WdspResult.Ok)
                Trace.WriteLine($"WdspHelperEngine: {Id} setting {ack.Name} refused: {ack.Result}");
            var slots = _slots;
            if (slots == null || ack.Value.Kind == WdspValue.NoneKind) return;
            foreach (var s in slots)
                if (s.D.Name == ack.Name) { s.Applied = new StrongBox<EngineSettingValue>(ack.Value.ToSetting()); return; }
        }
    }
}
