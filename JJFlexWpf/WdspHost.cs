using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace JJFlexWpf
{
    /// <summary>Where the WDSP helper process stands.</summary>
    public enum WdspHelperState
    {
        /// <summary>Not running, and nothing needs it.</summary>
        Idle,
        /// <summary>Launched; the handshake has not finished.</summary>
        Starting,
        /// <summary>Handshake done; channels open on it.</summary>
        Running,
        /// <summary>It stopped unexpectedly and a restart is scheduled.</summary>
        Restarting,
        /// <summary>It failed too many times running; no more automatic attempts until a retry.</summary>
        CapReached,
        /// <summary>The file is not where it should be. Never retried by itself.</summary>
        Missing,
        /// <summary>Windows or antivirus software refused to start it. Never retried by itself.</summary>
        Blocked,
        /// <summary>It is not the build this JJ Flexible shipped with. Never retried by itself.</summary>
        VersionMismatch,
        /// <summary>
        /// It exited in a way no restart can mend: a bad command line (exit 2),
        /// or its pipe's server was not JJ Flexible (exit 5). Given up at once,
        /// with no retries (ruled 2026-11-06); never retried by itself.
        /// </summary>
        GaveUp,
        /// <summary>
        /// It is installed but could not make its own folder its working
        /// directory (exit 8), so it can never run there: reported as missing,
        /// and reinstalling is the repair a later surface can offer from this
        /// state (ruled 2026-11-06). Never retried by itself.
        /// </summary>
        FolderUnusable,
    }

    /// <summary>
    /// The changes nobody asked for, which the operator should be told about.
    /// A restart that works is deliberately not one of them (ruled 2026-11-02).
    /// </summary>
    public enum WdspHelperStateChange
    {
        /// <summary>The restart cap was reached; the engines are passing audio through.</summary>
        CapReached,
        /// <summary>After the cap, a retry or a new selection brought the helper back, and at least one engine's channel really reopened.</summary>
        BackAfterCap,
    }

    public sealed class WdspHelperStateChangedEventArgs : EventArgs
    {
        public WdspHelperStateChange Change { get; }
        /// <summary>
        /// Catalog ids, in catalog order: for <see cref="WdspHelperStateChange.CapReached"/>
        /// the helper engines selected when it happened; for
        /// <see cref="WdspHelperStateChange.BackAfterCap"/> only those whose channels reopened.
        /// </summary>
        public IReadOnlyList<string> Engines { get; }
        public WdspHelperStateChangedEventArgs(WdspHelperStateChange change, IReadOnlyList<string> engines)
        { Change = change; Engines = engines; }
    }

    /// <summary>
    /// Every timing in the helper's lifecycle, in one place. The app uses
    /// <see cref="Default"/>; tests shorten them. Real waits, never a mocked clock.
    /// </summary>
    internal sealed record WdspHostPolicy
    {
        public const int MaxAutomaticRestarts = 3;
        public const int FirstRestartDelayMs = 500;
        public const int SecondRestartDelayMs = 2000;
        public const int ThirdRestartDelayMs = 5000;
        public const int CleanRunResetMinutes = 10;
        public const int LingerMs = 5000;
        public const int HandshakeDeadlineMs = 3000;
        public const int StallMs = 500;
        public const int HeartbeatMs = 250;
        public const int ConstructionWaitMs = 3000;
        public const int ShutdownWaitMs = 500;
        public const int OfflineFrameWaitMs = 2000;
        public const int ExitNamingWaitMs = 250;
        public const int MaxSettingsInFlightDefault = 256;

        /// <summary>One delay per automatic restart; its length is the cap.</summary>
        public IReadOnlyList<TimeSpan> RestartDelays { get; init; } = new[] {
            TimeSpan.FromMilliseconds(FirstRestartDelayMs), TimeSpan.FromMilliseconds(SecondRestartDelayMs),
            TimeSpan.FromMilliseconds(ThirdRestartDelayMs) };
        /// <summary>A helper that runs this long without failing gets its restart count back.</summary>
        public TimeSpan CleanRunReset { get; init; } = TimeSpan.FromMinutes(CleanRunResetMinutes);
        /// <summary>How long the helper outlives its last channel, so arrowing past an engine and back does not restart it.</summary>
        public TimeSpan Linger { get; init; } = TimeSpan.FromMilliseconds(LingerMs);
        public TimeSpan HandshakeDeadline { get; init; } = TimeSpan.FromMilliseconds(HandshakeDeadlineMs);
        /// <summary>Nothing at all from the helper for this long, once the handshake is done, is a stall.</summary>
        public TimeSpan Stall { get; init; } = TimeSpan.FromMilliseconds(StallMs);
        public TimeSpan Heartbeat { get; init; } = TimeSpan.FromMilliseconds(HeartbeatMs);
        /// <summary>How long an engine's constructor waits for its first channel, on the build worker.</summary>
        public TimeSpan ConstructionWait { get; init; } = TimeSpan.FromMilliseconds(ConstructionWaitMs);
        public TimeSpan ShutdownWait { get; init; } = TimeSpan.FromMilliseconds(ShutdownWaitMs);
        /// <summary>The offline bench's per-frame wait; the live path never waits.</summary>
        public TimeSpan OfflineFrameWait { get; init; } = TimeSpan.FromMilliseconds(OfflineFrameWaitMs);
        /// <summary>
        /// When the pipe ends, how long the reader waits for the helper to exit
        /// so the failure can name its exit code. The helper exits a few
        /// milliseconds after its pipe breaks; the audio meanwhile is the bank's
        /// delayed pass-through, as for any late frame.
        /// </summary>
        public TimeSpan ExitNamingWait { get; init; } = TimeSpan.FromMilliseconds(ExitNamingWaitMs);
        /// <summary>
        /// The most SetSettings one connection has sent and not yet had
        /// answered. The helper ends a connection that leaves more than 4,096
        /// settings and closes queued; holding back here, and sending only each
        /// setting's latest value when room comes, keeps every connection far
        /// below that however fast a value changes, and loses no value.
        /// </summary>
        public int MaxSettingsInFlight { get; init; } = MaxSettingsInFlightDefault;

        public const int MaxInFlightPerSettingDefault = 1;

        /// <summary>
        /// The most SetSettings for any one setting of one channel sent and not
        /// yet answered. One: a value changing faster than the helper answers
        /// goes as its latest form once its previous answer is in, so it can
        /// never take the room every other setting needs (Codex found a later
        /// setting starved behind an earlier one that never stopped changing,
        /// 2026-11-06). Tests raise it to exercise the connection's cap alone.
        /// </summary>
        public int MaxInFlightPerSetting { get; init; } = MaxInFlightPerSettingDefault;

        public static WdspHostPolicy Default { get; } = new();
    }

    /// <summary>One helper process: its pipe, job, threads and channel table.</summary>
    internal sealed class WdspConnection
    {
        public readonly int StartNumber;
        public readonly string PipeName;
        public readonly string CommandLine;
        public readonly NamedPipeServerStream Pipe;
        public readonly SafeProcessHandle Process;
        public readonly SafeFileHandle Job;
        public readonly int Pid;
        public readonly WaitHandle Exited;
        /// <summary>Wakes the writer. Never disposed: the audio thread sets it.</summary>
        public readonly AutoResetEvent Wake = new(false);
        public readonly ConcurrentQueue<byte[]> Control = new();
        public readonly ConcurrentDictionary<uint, WdspChannel> ById = new();
        public volatile WdspChannel[] Lives = Array.Empty<WdspChannel>();
        public readonly CancellationTokenSource Cancel = new();
        public uint NextChannelId = 1;
        public readonly long StartedAt;
        public long HelloAt;
        public long LastReceive;
        public volatile bool HelloDone, Stopping;
        public volatile object? HelloResult; // WdspHelloAck, WdspHelloReject, or a string saying why it is unusable
        public WdspHelloAck? Hello;
        public bool HelloProcessed;
        /// <summary>
        /// Why this connection broke, published once and whole: the record is
        /// built first and then made visible by one CompareExchange, so a
        /// reader that sees it at all sees its reason too.
        /// </summary>
        public WdspFailure? Failure;
        public bool IsBroken => Volatile.Read(ref Failure) != null;
        public long LastBadTrace;
        /// <summary>
        /// Set by the reader once the helper has connected. From then on the
        /// reader, not the control thread, names an exit: it sees the pipe
        /// end, waits for the code, and has read what the helper said first.
        /// </summary>
        public volatile bool Connected;
        /// <summary>Control thread only: when it first saw the process gone (0 until then).</summary>
        public long ExitSeenAt;
        /// <summary>What the helper said in a fatal Error (256 or 257) before it closed the pipe, if anything.</summary>
        public volatile string? HelperSaid;
        /// <summary>
        /// Set once the reader will read no more: it reached the end of the pipe
        /// (so <see cref="HelperSaid"/> is final) or stopped for any other reason.
        /// The writer waits on it, bounded, before naming an exit it found first.
        /// Never disposed, like <see cref="Wake"/>.
        /// </summary>
        public readonly ManualResetEventSlim ReaderDone = new(false);
        /// <summary>SetSettings sent on this connection and not yet answered. Interlocked; the writer adds, the reader takes away.</summary>
        public int SettingsInFlight;
        /// <summary>1 while the writer is holding settings back (for room, or for a setting's own answer), so an answer wakes it. Interlocked.</summary>
        public int SettingsHeld;
        /// <summary>
        /// Counts every answer that freed a setting's turn or the connection's
        /// room. Interlocked. The writer reads it before deciding what to hold and
        /// again after publishing <see cref="SettingsHeld"/>, so an answer between
        /// the two cannot be missed, and a setting held for its own answer does not
        /// make the writer spin.
        /// </summary>
        public long SettingProgress;
        /// <summary>Writer only: which channel's settings go first, turn about, so one busy channel cannot starve another.</summary>
        public int SettingRotation;

        public WdspConnection(int startNumber, string pipeName, string commandLine, NamedPipeServerStream pipe,
            WdspNative.Started started)
        {
            StartNumber = startNumber; PipeName = pipeName; CommandLine = commandLine; Pipe = pipe;
            Process = started.Process; Job = started.Job; Pid = started.ProcessId;
            Exited = new ProcessWait(started.Process);
            StartedAt = WdspHost.Now;
        }

        private sealed class ProcessWait : WaitHandle
        {
            public ProcessWait(SafeProcessHandle process) =>
                SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }

    /// <summary>A connection's failure: immutable, so publishing the reference publishes all of it.</summary>
    internal sealed record WdspFailure(WdspCondition Condition, string Detail);

    /// <summary>
    /// One channel's life: one engine instance on one helper connection, from
    /// OpenChannel to CloseChannel. Seq belongs to it. The audio thread is the
    /// only producer of frames; the writer the only consumer. The reader the
    /// only writer of results; the audio thread the only reader of them.
    /// </summary>
    internal sealed class WdspChannel
    {
        public const int SendCapacity = 8;
        private const int SendMask = SendCapacity - 1;

        public readonly WdspHelperEngine Engine;
        public readonly WdspConnection Conn;
        public readonly uint Id;

        // Audio thread only.
        public ulong NextSeq;

        // Frames out: single producer (the audio thread), single consumer (the writer).
        public readonly float[][] SendFrames = new float[SendCapacity][];
        public readonly ulong[] SendSeqs = new ulong[SendCapacity];
        public long SendHead, SendTail;

        // Results in: one slot per in-flight seq, under a per-slot version counter.
        public readonly float[][] Results;
        public readonly long[] ResultSeq;
        public readonly int[] ResultVersion;
        public readonly int ResultMask;
        public readonly AutoResetEvent? ResultArrived;

        public long DiscardBelow;
        public uint Latency;
        /// <summary>
        /// This channel's share of the connection's settings in flight.
        /// Interlocked. When its close is answered, whatever is left is taken
        /// off the connection at once: a setting the helper answers after the
        /// close is then not counted twice, and one it never answers leaks nothing.
        /// </summary>
        public int SettingsInFlight;
        public volatile bool Opened, Closed, OpenSent;
        /// <summary>Per setting, the asked-for version this channel already has. Writer thread only.</summary>
        public readonly int[] SentVersions;
        /// <summary>Per setting, SetSettings sent on this channel and not yet answered. Interlocked; the writer adds, the reader takes away.</summary>
        public readonly int[] SettingOutstanding;

        public WdspChannel(WdspHelperEngine engine, WdspConnection conn, uint id, int pipelineDepth, int[] sentVersions, bool offline)
        {
            Engine = engine; Conn = conn; Id = id; SentVersions = sentVersions;
            SettingOutstanding = new int[sentVersions.Length];
            for (int i = 0; i < SendCapacity; i++) SendFrames[i] = new float[WdspProtocol.FrameLength];
            int slots = 8;
            while (slots < pipelineDepth + 2) slots <<= 1;
            ResultMask = slots - 1;
            Results = new float[slots][];
            ResultSeq = new long[slots];
            ResultVersion = new int[slots];
            for (int i = 0; i < slots; i++) { Results[i] = new float[WdspProtocol.FrameLength]; ResultSeq[i] = -1; }
            if (offline) ResultArrived = new AutoResetEvent(false);
        }

        /// <summary>Audio thread: queue a frame for the writer. False when the ring is full (the frame is then late, not an error).</summary>
        public bool TrySend(ReadOnlySpan<float> input, ulong seq)
        {
            long head = SendHead;
            if (head - Volatile.Read(ref SendTail) >= SendCapacity) return false;
            int slot = (int)(head & SendMask);
            input.CopyTo(SendFrames[slot]);
            SendSeqs[slot] = seq;
            Volatile.Write(ref SendHead, head + 1);
            return true;
        }

        /// <summary>Writer thread: the next queued frame, if any.</summary>
        public bool TryPeekSend(out float[] frame, out ulong seq)
        {
            long tail = SendTail;
            if (tail >= Volatile.Read(ref SendHead)) { frame = null!; seq = 0; return false; }
            int slot = (int)(tail & SendMask);
            frame = SendFrames[slot]; seq = SendSeqs[slot];
            return true;
        }

        public void ConsumeSend() => Volatile.Write(ref SendTail, SendTail + 1);

        /// <summary>
        /// Reader thread: publish a checked FrameOut. The version is odd while
        /// the slot is being written, so the audio thread can tell a torn read.
        /// </summary>
        public void Publish(ulong seq, ReadOnlySpan<byte> payload)
        {
            int slot = (int)(seq & (ulong)ResultMask);
            Interlocked.Increment(ref ResultVersion[slot]);
            Volatile.Write(ref ResultSeq[slot], (long)seq);
            var dst = Results[slot];
            for (int i = 0; i < dst.Length; i++) dst[i] = WdspProtocol.FrameOutSample(payload, i);
            Interlocked.Increment(ref ResultVersion[slot]);
            ResultArrived?.Set();
        }

        /// <summary>
        /// Audio thread: copy out the result for exactly <paramref name="seq"/>.
        /// No wait, no lock, no allocation. False when it is not there yet,
        /// was overwritten, or was being written during the copy.
        /// </summary>
        public bool TryTake(ulong seq, Span<float> output)
        {
            int slot = (int)(seq & (ulong)ResultMask);
            int before = Volatile.Read(ref ResultVersion[slot]);
            if ((before & 1) != 0) return false;
            if (Volatile.Read(ref ResultSeq[slot]) != (long)seq) return false;
            Results[slot].AsSpan().CopyTo(output);
            Interlocked.MemoryBarrier();
            return Volatile.Read(ref ResultVersion[slot]) == before;
        }

        /// <summary>
        /// True when the result for <paramref name="seq"/> is in its slot and
        /// completely written (offline wait only). The seq is stored before the
        /// samples, so the seq alone would say yes in the middle of the copy.
        /// </summary>
        public bool Has(ulong seq)
        {
            int slot = (int)(seq & (ulong)ResultMask);
            int before = Volatile.Read(ref ResultVersion[slot]);
            return (before & 1) == 0 && Volatile.Read(ref ResultSeq[slot]) == (long)seq
                && Volatile.Read(ref ResultVersion[slot]) == before;
        }
    }

    /// <summary>
    /// Owns the WDSP helper process for one JJ Flexible process: its job
    /// object, the pipe, one reader and one writer thread, the liveness
    /// watchdog, the restart policy and the table of open channels. Engines
    /// are thin clients of it; NNR and NR2, both slots and both stereo
    /// channels share the one helper and the one pipe.
    /// </summary>
    /// <remarks>
    /// <para><b>Never orphaned.</b> The helper is created suspended, with no
    /// window, and already a member of a job whose last handle closing kills
    /// it (the job goes to CreateProcess itself, so there is no moment outside
    /// it), then resumed.
    /// The job handle exists only in this process, so when JJ Flexible exits
    /// for any reason Windows closes it and kills the helper; no code of ours
    /// has to run. The helper's own guards (parent handle, broken pipe) are the
    /// second line. And since a JJ Flexible that lingers after its window closes
    /// keeps its job open, the host also stops the helper itself once the last
    /// channel has closed and the linger has passed.</para>
    /// <para><b>Threads.</b> Lifecycle work happens on one control thread.
    /// Slow work (launching, waiting for a process to exit) happens outside the
    /// lock, so an engine's Dispose, which takes it briefly, is never held up.
    /// The audio thread never takes it.</para>
    /// </remarks>
    internal sealed class WdspHost : IDisposable
    {
        /// <summary>The engine ids JJ Flexible has catalog rows for; any other offer is ignored and traced.</summary>
        internal static readonly string[] KnownEngines = { "nnr", "nr2" };

        private static readonly object DefaultGate = new();
        private static WdspHost? _default;

        /// <summary>The app's one host. Creating it starts nothing.</summary>
        public static WdspHost Default
        {
            get { lock (DefaultGate) return _default ??= new WdspHost(null, WdspHostPolicy.Default); }
        }

        internal static WdspHost? DefaultIfCreated => Volatile.Read(ref _default);

        internal static long Now => Stopwatch.GetTimestamp();
        internal static long Ticks(TimeSpan t) => (long)(t.TotalSeconds * Stopwatch.Frequency);

        private readonly string? _explicitPath;
        private readonly Func<int, IReadOnlyList<string>>? _extraArgs;
        internal readonly object Gate = new();
        private readonly List<WdspHelperEngine> _wanted = new();
        private readonly AutoResetEvent _controlWake = new(false);
        private readonly List<int> _launched = new();
        private Thread? _control;
        private WdspConnection? _conn;
        private int _startNumber;
        private int _failures;
        private long _restartAt;
        private long _lingerUntil;
        private WdspHelperState _state;
        private string _detail = "";
        private bool _capPending;
        private bool _disposed;
        private WdspHelloAck? _lastHello;

        internal WdspHostPolicy Policy { get; }
        internal long SetSettingsSent;
        internal long SettingAcksReceived;
        internal long FramesSent;
        /// <summary>The most settings ever in flight on one connection at once.</summary>
        internal long PeakSettingsInFlight;
        /// <summary>SettingAcks for a channel already closed (sent before the close, answered after it): discarded, as expected.</summary>
        internal long SettingAcksAfterClose;
        /// <summary>SettingAcks for a channel this connection never opened: traced as a disagreement.</summary>
        internal long SettingAcksUnplaced;
        /// <summary>CloseChannels queued, one per channel ever.</summary>
        internal long CloseChannelsSent;
        /// <summary>CloseChannelAcks that settled a channel this end had closed (Ok, or UnknownChannel for one the helper had already let go).</summary>
        internal long CloseAcksSettled;
        /// <summary>CloseChannelAcks this end could not place: traced, and nothing else.</summary>
        internal long CloseAcksUnexpected;
        /// <summary>The condition of the last failure acted on, for the tests.</summary>
        internal WdspCondition? LastFailureCondition { get { lock (Gate) return _lastFailureCondition; } }
        private WdspCondition? _lastFailureCondition;
        internal string? LastCommandLine { get; private set; }
        internal string? LastPipeName { get; private set; }

        /// <summary>Raised off every audio and reader thread, once per change.</summary>
        public event EventHandler<WdspHelperStateChangedEventArgs>? StateChanged;

        /// <param name="helperPath">The helper's path, or null for the app's locator.</param>
        /// <param name="policy">Timings; null for the defaults.</param>
        /// <param name="extraArguments">Tests only: from start number (1 upwards) to extra command-line arguments.</param>
        internal WdspHost(string? helperPath, WdspHostPolicy? policy = null, Func<int, IReadOnlyList<string>>? extraArguments = null)
        {
            _explicitPath = helperPath;
            Policy = policy ?? WdspHostPolicy.Default;
            _extraArgs = extraArguments;
        }

        public WdspHelperState State { get { lock (Gate) return _state; } }
        internal int Failures { get { lock (Gate) return _failures; } }
        internal string Detail { get { lock (Gate) return _detail; } }
        internal int StartsAttempted { get { lock (Gate) return _startNumber; } }
        internal IReadOnlyList<int> LaunchedProcessIds { get { lock (Gate) return _launched.ToArray(); } }
        internal int? CurrentProcessId { get { lock (Gate) return _conn?.Pid; } }
        internal WdspConnection? CurrentConnection { get { lock (Gate) return _conn; } }

        /// <summary>
        /// Start again after the cap, or after a missing, blocked or mismatched
        /// helper: the restart count goes back to zero. Does nothing if no
        /// helper engine is selected; the next selection starts it.
        /// </summary>
        public void Retry()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _failures = 0;
                _restartAt = 0;
                if (_state is WdspHelperState.CapReached or WdspHelperState.Missing or WdspHelperState.Blocked
                    or WdspHelperState.VersionMismatch or WdspHelperState.Restarting or WdspHelperState.GaveUp
                    or WdspHelperState.FolderUnusable)
                    SetStateLocked(WdspHelperState.Idle, "retry");
                foreach (var e in _wanted) e.ClearFinalLocked();
                EnsureControlLocked();
            }
            _controlWake.Set();
        }

        /// <summary>
        /// What a catalog probe needs, answered from what is already known: it
        /// never starts a process and never opens the pipe.
        /// </summary>
        internal Unavailability ProbeState(string engineId, bool fileExists)
        {
            lock (Gate)
            {
                switch (_state)
                {
                    case WdspHelperState.CapReached: return Unavailability.HelperNotRunning;
                    case WdspHelperState.GaveUp: return Unavailability.HelperNotRunning;
                    case WdspHelperState.Blocked: return Unavailability.HelperMissing;
                    // Installed but unable to use its folder: missing to the operator, whatever the file.
                    case WdspHelperState.FolderUnusable: return Unavailability.HelperMissing;
                    case WdspHelperState.Missing: return fileExists ? Unavailability.None : Unavailability.HelperMissing;
                    case WdspHelperState.VersionMismatch: return Unavailability.HelperVersionMismatch;
                }
                if (_lastHello != null && OfferProblem(_lastHello, engineId, WdspProtocol.Receive) != null)
                    return Unavailability.HelperVersionMismatch;
                return Unavailability.None;
            }
        }

        /// <summary>The catalog probe for this host: the file, then what is already known. Starts nothing.</summary>
        internal Unavailability Probe(string engineId)
        {
            string? path = ResolvePath();
            if (path == null || !File.Exists(path)) return Unavailability.HelperMissing;
            return ProbeState(engineId, fileExists: true);
        }

        internal static string? OfferProblem(WdspHelloAck hello, string engineId, byte direction)
        {
            var offer = hello.Engines.FirstOrDefault(o => o.Id == engineId);
            if (offer == null) return $"this helper does not offer {engineId}";
            if ((offer.Directions & (1 << direction)) == 0) return $"{engineId} is not offered for that direction";
            if (offer.SampleRate != WdspProtocol.SampleRate) return $"{engineId} runs at {offer.SampleRate} Hz, not 48000";
            if (offer.FrameLength != WdspProtocol.FrameLength) return $"{engineId} takes {offer.FrameLength}-sample frames, not 480";
            return null;
        }

        // ------------------------------------------------------------------
        // Engines
        // ------------------------------------------------------------------

        /// <summary>A new engine wants a channel. A new selection is a retry: the count resets.</summary>
        internal void Attach(WdspHelperEngine engine)
        {
            lock (Gate)
            {
                if (_disposed)
                {
                    engine.SetUnavailableLocked(Unavailability.HelperNotRunning, "the helper host has shut down", final: true);
                    return;
                }
                _wanted.Add(engine);
                _lingerUntil = 0;
                _failures = 0;
                if (_state is WdspHelperState.CapReached or WdspHelperState.Missing or WdspHelperState.Blocked
                    or WdspHelperState.VersionMismatch or WdspHelperState.GaveUp or WdspHelperState.FolderUnusable)
                    SetStateLocked(WdspHelperState.Idle, "a new selection");
                engine.SetUnavailableLocked(Unavailability.HelperNotRunning, "starting", final: false);
                EnsureControlLocked();
            }
            _controlWake.Set();
        }

        /// <summary>An engine is done: close its channel without waiting for the ack.</summary>
        internal void Detach(WdspHelperEngine engine)
        {
            lock (Gate)
            {
                _wanted.Remove(engine);
                var ch = engine.Channel;
                if (ch != null) CloseLocked(ch);
                engine.SetChannelLocked(null);
                if (_wanted.Count == 0 && _conn != null && _lingerUntil == 0)
                    _lingerUntil = Now + Ticks(Policy.Linger);
            }
            _controlWake.Set();
        }

        /// <summary>Close this channel and open a new one, same engine and settings (Reset).</summary>
        internal void Reopen(WdspHelperEngine engine)
        {
            lock (Gate)
            {
                var ch = engine.Channel;
                if (ch != null) CloseLocked(ch);
                engine.SetChannelLocked(null);
            }
            _controlWake.Set();
        }

        internal void CloseLocked(WdspChannel ch)
        {
            if (ch.Closed) return;
            ch.Closed = true;
            var c = ch.Conn;
            c.Lives = c.Lives.Where(l => l != ch).ToArray();
            // Once per channel: Closed is set above, under the lock, so no path
            // sends a second close, and nothing anywhere waits for this one's ack.
            if (ch.OpenSent && !c.Stopping)
            {
                c.Control.Enqueue(WdspProtocol.Encode(new WdspCloseChannel(ch.Id)));
                Interlocked.Increment(ref CloseChannelsSent);
                c.Wake.Set();
            }
        }

        internal void WakeControl() => _controlWake.Set();

        private void EnsureControlLocked()
        {
            if (_control != null) return;
            _control = new Thread(ControlLoop) { IsBackground = true, Name = "WDSP helper control" };
            _control.Start();
        }

        private void SetStateLocked(WdspHelperState state, string detail)
        {
            if (_state != state) Trace.WriteLine($"WdspHost: {_state} -> {state} ({detail})");
            _state = state;
            _detail = detail;
        }

        private IReadOnlyList<string> WantedIdsLocked() =>
            KnownEngines.Where(id => _wanted.Any(e => e.Id == id)).ToArray();

        private void MarkEnginesLocked(Unavailability reason, string detail)
        {
            foreach (var e in _wanted) e.SetUnavailableLocked(reason, detail, final: false);
        }

        // ------------------------------------------------------------------
        // The control thread
        // ------------------------------------------------------------------

        private void ControlLoop()
        {
            var raise = new List<WdspHelperStateChangedEventArgs>();
            while (true)
            {
                WdspConnection? c;
                int wait;
                bool exitSeen;
                lock (Gate) { if (_disposed) break; c = _conn; wait = NextWaitMsLocked(); exitSeen = c != null && c.ExitSeenAt != 0; }
                try
                {
                    // Once the exit is seen, its handle stays signalled: wait on the wake alone, or this would spin.
                    if (c != null && !exitSeen) WaitHandle.WaitAny(new[] { _controlWake, c.Exited }, wait);
                    else _controlWake.WaitOne(wait);
                }
                catch (ObjectDisposedException) { }

                WdspConnection? stop = null;
                bool kill = false, start = false;
                lock (Gate)
                {
                    if (_disposed) break;
                    StepLocked(raise, ref stop, ref kill, ref start);
                }
                if (stop != null) Teardown(stop, kill);
                if (start) Launch();
                foreach (var args in raise)
                {
                    var a = args;
                    ThreadPool.QueueUserWorkItem(_ => StateChanged?.Invoke(this, a));
                }
                raise.Clear();
            }
            WdspConnection? last;
            lock (Gate) { last = _conn; _conn = null; }
            if (last != null) Teardown(last, kill: false);
        }

        /// <summary>
        /// How long the control thread leaves a connected helper's exit for the
        /// reader or writer to name: three times their wait, longer than the
        /// writer's whole wait (for the exit, then for the reader to finish).
        /// </summary>
        private long ExitGraceTicks => 3 * Ticks(Policy.ExitNamingWait);

        private int NextWaitMsLocked()
        {
            long now = Now, next = now + Stopwatch.Frequency; // at most a second between looks
            var c = _conn;
            if (c != null)
            {
                if (!c.HelloDone) next = Math.Min(next, c.StartedAt + Ticks(Policy.HandshakeDeadline));
                else next = Math.Min(next, Volatile.Read(ref c.LastReceive) + Ticks(Policy.Stall));
                if (_lingerUntil != 0) next = Math.Min(next, _lingerUntil);
                if (c.HelloDone && _failures > 0) next = Math.Min(next, c.HelloAt + Ticks(Policy.CleanRunReset));
                if (c.ExitSeenAt != 0) next = Math.Min(next, c.ExitSeenAt + ExitGraceTicks);
            }
            else if (_state == WdspHelperState.Restarting && _wanted.Count > 0) next = Math.Min(next, _restartAt);
            long ms = (next - now) * 1000 / Stopwatch.Frequency + 1;
            return (int)Math.Clamp(ms, 1, 1000);
        }

        private void StepLocked(List<WdspHelperStateChangedEventArgs> raise, ref WdspConnection? stop, ref bool kill, ref bool start)
        {
            long now = Now;
            var c = _conn;
            if (c != null)
            {
                // A definite answer to the handshake, a reject or a HelloAck that
                // cannot be used, wins over a pipe break published behind it: the
                // helper exits 7 once JJ Flexible closes after a reject, and that
                // is not a crash. (A fatal Error before the handshake is not such
                // an answer: it is a protocol error, the crash path, below.)
                if (!c.HelloProcessed && c.HelloResult is { } early and not WdspHelloAck)
                {
                    c.HelloProcessed = true;
                    MismatchLocked(c, early is WdspHelloReject reject
                        ? $"the helper rejected protocol 1; it speaks {reject.Lowest} to {reject.Highest} ({reject.Detail})"
                        : early as string ?? "unreadable handshake");
                    stop = c; return;
                }
                if (Volatile.Read(ref c.Failure) is { } failure)
                {
                    FailLocked(c, failure.Condition, failure.Detail, raise);
                    stop = c; kill = true; return;
                }
                if (c.Exited.WaitOne(0))
                {
                    if (c.ExitSeenAt == 0) c.ExitSeenAt = now;
                    // Connected: the reader will see the pipe end and name the
                    // exit with what the helper said; give it the time it takes.
                    if (c.Connected && now - c.ExitSeenAt < ExitGraceTicks) return;
                    uint? code = WdspNative.ExitCode(c.Process);
                    if (code is uint x) FailLocked(c, WdspProtocol.ConditionForExit(x), WdspProtocol.ExitDetail(x, c.HelperSaid), raise);
                    else FailLocked(c, WdspCondition.ProcessExited, "the helper exited; its exit code could not be read", raise);
                    stop = c; return;
                }
                if (!c.HelloProcessed && c.HelloResult == null && now - c.StartedAt > Ticks(Policy.HandshakeDeadline))
                {
                    FailLocked(c, WdspCondition.HandshakeDeadline, $"no handshake within {Policy.HandshakeDeadline.TotalMilliseconds} ms", raise);
                    stop = c; kill = true; return;
                }
                if (c.HelloDone && now - Volatile.Read(ref c.LastReceive) > Ticks(Policy.Stall))
                {
                    FailLocked(c, WdspCondition.Stall, $"nothing from the helper for {Policy.Stall.TotalMilliseconds} ms", raise);
                    stop = c; kill = true; return;
                }
                if (!c.HelloProcessed && c.HelloResult != null)
                {
                    c.HelloProcessed = true;
                    switch (c.HelloResult)
                    {
                        case WdspHelloAck ack:
                            TraceHello(ack);
                            c.Hello = ack;
                            _lastHello = ack;
                            c.HelloAt = now;
                            Volatile.Write(ref c.LastReceive, now);
                            c.HelloDone = true;
                            SetStateLocked(WdspHelperState.Running, $"helper {ack.HelperVersion}, pid {c.Pid}");
                            // Not "back" yet: a handshake reopens nothing. BackAfterCapLocked waits for the channels.
                            break;
                        // A reject or an unusable answer was handled first, above.
                    }
                }
                if (c.HelloDone)
                {
                    OpenWantedLocked(c);
                    BackAfterCapLocked(c, raise);
                }
                if (_wanted.Count == 0)
                {
                    if (_lingerUntil == 0) _lingerUntil = now + Ticks(Policy.Linger);
                    else if (now >= _lingerUntil)
                    {
                        DetachConnectionLocked(c);
                        SetStateLocked(WdspHelperState.Idle, "no helper engine is selected");
                        stop = c; return;
                    }
                }
                else _lingerUntil = 0;
                if (c.HelloDone && _failures > 0 && now - c.HelloAt >= Ticks(Policy.CleanRunReset))
                {
                    Trace.WriteLine($"WdspHost: ran cleanly for {Policy.CleanRunReset}; restart count {_failures} reset to 0");
                    _failures = 0;
                }
                return;
            }
            if (_wanted.Count == 0)
            {
                if (_state is WdspHelperState.Restarting or WdspHelperState.Starting) SetStateLocked(WdspHelperState.Idle, "nothing needs it");
                return;
            }
            if (_state == WdspHelperState.Idle || (_state == WdspHelperState.Restarting && now >= _restartAt))
            {
                SetStateLocked(WdspHelperState.Starting, "starting");
                start = true;
            }
        }

        private void TraceHello(WdspHelloAck ack)
        {
            Trace.WriteLine($"WdspHost: handshake: protocol {ack.Version}, helper {ack.HelperVersion}, WDSP {ack.WdspVersion} " +
                $"({ack.WdspCommit}), FFTW {ack.FftwVersion}, at most {ack.MaxChannels} channels");
            foreach (var f in ack.Fingerprints)
                Trace.WriteLine($"WdspHost: model {f.EngineId} slot {f.Slot} option '{f.Option}' sha256 {Convert.ToHexString(f.Sha256).ToLowerInvariant()}");
            foreach (var e in ack.Engines)
            {
                Trace.WriteLine($"WdspHost: offers {e.Id}: directions {e.Directions}, {e.SampleRate} Hz, {e.FrameLength}-sample frames, " +
                    $"latency {e.LatencySamples}, cost median {e.MedianNanos} ns, 99th percentile {e.P99Nanos} ns over {e.CalibrationFrames} frames, " +
                    $"{e.Settings.Count} settings");
                if (!KnownEngines.Contains(e.Id))
                    Trace.WriteLine($"WdspHost: ignoring offered engine {e.Id}: JJ Flexible has no catalog row for it");
            }
        }

        private void OpenWantedLocked(WdspConnection c)
        {
            foreach (var e in _wanted.ToArray())
            {
                if (e.Final || e.RefusedOn == c) continue;
                var ch = e.Channel;
                if (ch != null && ch.Conn == c) continue;
                string? problem = OfferProblem(c.Hello!, e.Id, e.Direction);
                if (problem != null)
                {
                    e.SetUnavailableLocked(Unavailability.HelperVersionMismatch, problem, final: true);
                    Trace.WriteLine("WdspHost: " + problem);
                    continue;
                }
                var offer = c.Hello!.Engines.First(o => o.Id == e.Id);
                e.AdoptDescriptorsLocked(offer.Settings);
                var initial = e.ComposeInitialLocked(out int[] versions);
                uint id = c.NextChannelId++;
                var life = new WdspChannel(e, c, id, e.PipelineDepth, versions, e.OfflineWait);
                c.ById[id] = life;
                c.Lives = c.Lives.Append(life).ToArray();
                e.SetChannelLocked(life);
                c.Control.Enqueue(WdspProtocol.Encode(new WdspOpenChannel(id, e.Direction, e.Id, initial)));
                life.OpenSent = true;
                c.Wake.Set();
            }
        }

        /// <summary>
        /// After the cap, raise the recovery event only once every selected
        /// engine's channel has settled on this connection, and only for the
        /// engines that actually reopened. A good handshake followed by a
        /// refused open, an engine no longer offered, or a reopen whose delay
        /// changed has brought nothing back, so nothing is said; the cap event
        /// still stands until something does come back.
        /// </summary>
        private void BackAfterCapLocked(WdspConnection c, List<WdspHelperStateChangedEventArgs> raise)
        {
            if (!_capPending || _wanted.Count == 0) return;
            List<WdspHelperEngine>? back = null;
            foreach (var e in _wanted)
            {
                var ch = e.Channel;
                if (!e.Final && ch != null && ch.Conn == c && ch.Opened && !ch.Closed) { (back ??= new()).Add(e); continue; }
                if (e.Final || e.RefusedOn == c) continue; // settled, and not back
                return; // still opening: wait for its ack
            }
            if (back == null) return;
            _capPending = false;
            raise.Add(new(WdspHelperStateChange.BackAfterCap, KnownEngines.Where(id => back.Any(e => e.Id == id)).ToArray()));
        }

        /// <summary>Take the connection away from the engines; teardown follows outside the lock.</summary>
        private void DetachConnectionLocked(WdspConnection c)
        {
            if (_conn == c) _conn = null;
            c.Stopping = true;
            c.Wake.Set();
            foreach (var e in _wanted)
                if (e.Channel?.Conn == c) e.SetChannelLocked(null);
            foreach (var ch in c.ById.Values) ch.Opened = false;
        }

        private void FailLocked(WdspConnection? c, WdspCondition condition, string detail, List<WdspHelperStateChangedEventArgs> raise)
        {
            if (c != null) DetachConnectionLocked(c);
            _lastFailureCondition = condition;
            Trace.WriteLine($"WdspHost: {condition}: {detail}");
            var reason = WdspProtocol.MapCondition(condition) ?? Unavailability.HelperNotRunning;
            if (!WdspProtocol.CountsTowardCap(condition))
            {
                TerminalLocked(condition, reason, detail);
                return;
            }
            if (_wanted.Count == 0)
            {
                SetStateLocked(WdspHelperState.Idle, detail);
                return;
            }
            _failures++;
            if (_failures > Policy.RestartDelays.Count)
            {
                SetStateLocked(WdspHelperState.CapReached, $"failed {_failures} times running: {detail}");
                MarkEnginesLocked(Unavailability.HelperNotRunning, "the helper's restart cap is reached; " + detail);
                foreach (var e in _wanted) e.ReleaseWaiter();
                if (!_capPending)
                {
                    _capPending = true;
                    raise.Add(new(WdspHelperStateChange.CapReached, WantedIdsLocked()));
                }
                return;
            }
            var delay = Policy.RestartDelays[_failures - 1];
            _restartAt = Now + Ticks(delay);
            SetStateLocked(WdspHelperState.Restarting, $"restart {_failures} of {Policy.RestartDelays.Count} in {delay.TotalMilliseconds} ms: {detail}");
            MarkEnginesLocked(Unavailability.HelperNotRunning, "restarting: " + detail);
        }

        private void MismatchLocked(WdspConnection c, string detail)
        {
            DetachConnectionLocked(c);
            TerminalLocked(WdspCondition.InvalidHelloAck, Unavailability.HelperVersionMismatch, detail);
        }

        private void TerminalLocked(WdspCondition condition, Unavailability reason, string detail)
        {
            var state = condition switch
            {
                WdspCondition.FileMissing => WdspHelperState.Missing,
                WdspCondition.StartBlocked => WdspHelperState.Blocked,
                WdspCondition.UnrecoverableExit => WdspHelperState.GaveUp,
                WdspCondition.NoWorkingDirectory => WdspHelperState.FolderUnusable,
                _ => WdspHelperState.VersionMismatch,
            };
            SetStateLocked(state, detail);
            foreach (var e in _wanted)
            {
                e.SetUnavailableLocked(reason, detail, final: false);
                e.ReleaseWaiter();
            }
        }

        // ------------------------------------------------------------------
        // Starting and stopping the process (outside the lock)
        // ------------------------------------------------------------------

        internal string? ResolvePath() => _explicitPath ?? WdspHelperEngine.LocateHelper();

        private void Launch()
        {
            int number;
            lock (Gate) number = ++_startNumber;
            var raise = new List<WdspHelperStateChangedEventArgs>();
            string? path = ResolvePath();
            if (path == null || !File.Exists(path))
            {
                lock (Gate) FailLocked(null, WdspCondition.FileMissing, $"the helper is not at {path ?? WdspHelperEngine.DefaultHelperPath()}", raise);
                return;
            }
            string pipeName = WdspNative.NewPipeName(Environment.ProcessId);
            string commandLine = WdspNative.CommandLine(path, pipeName, _extraArgs?.Invoke(number));
            NamedPipeServerStream pipe;
            WdspNative.Started started;
            try { pipe = WdspNative.CreatePipeServer(pipeName); }
            catch (Exception ex)
            {
                lock (Gate) FailLocked(null, WdspCondition.StartFailed, "the pipe could not be created: " + ex.Message, raise);
                RaiseAll(raise);
                return;
            }
            // The helper's standard error, recorded in the trace and never spoken
            // (ruled 2026-11-06, S13). Safe since Track A's third repair round: a
            // parent that stops reading it holds up only the helper's logger, never
            // its audio and never its exit. If the pipe cannot be made, the helper
            // still starts, with nowhere to write, as before.
            AnonymousPipeServerStream? stderr = null;
            try { stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None); }
            catch (Exception ex) { Trace.WriteLine("WdspHost: the helper's standard error cannot be recorded this start: " + ex.Message); }
            try { started = WdspNative.StartInJob(path, commandLine, standardError: stderr?.ClientSafePipeHandle); }
            catch (WdspNative.StartFailure f)
            {
                pipe.Dispose();
                stderr?.Dispose();
                var condition = f.NotThere ? WdspCondition.FileMissing : f.Blocked ? WdspCondition.StartBlocked : WdspCondition.StartFailed;
                lock (Gate) FailLocked(null, condition, (f.Blocked ? "Windows or antivirus software blocked the helper: " : "") + f.Message, raise);
                RaiseAll(raise);
                return;
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                stderr?.Dispose();
                lock (Gate) FailLocked(null, WdspCondition.StartFailed, ex.Message, raise);
                RaiseAll(raise);
                return;
            }
            if (stderr != null)
            {
                // Only the helper holds the write end now, so the pipe ends when it does.
                stderr.DisposeLocalCopyOfClientHandle();
                Interlocked.Increment(ref StderrReadersRunning);
                int pid = started.ProcessId;
                new Thread(() => StderrLoop(stderr, pid)) { IsBackground = true, Name = "WDSP helper stderr" }.Start();
            }
            var c = new WdspConnection(number, pipeName, commandLine, pipe, started);
            bool keep;
            lock (Gate)
            {
                _launched.Add(c.Pid);
                LastCommandLine = commandLine;
                LastPipeName = pipeName;
                keep = !_disposed && _wanted.Count > 0;
                if (keep) _conn = c;
                else SetStateLocked(WdspHelperState.Idle, "nothing needs it any more");
            }
            Trace.WriteLine($"WdspHost: start {number}: pid {c.Pid}, hidden, in its own kill-on-close job");
            if (!keep) { Teardown(c, kill: true); return; }
            new Thread(() => ReaderLoop(c)) { IsBackground = true, Name = "WDSP helper reader" }.Start();
            _controlWake.Set();
        }

        /// <summary>The most lines of one helper's standard error that are traced; the rest are counted.</summary>
        internal const int MaxStderrLinesTraced = 500;
        /// <summary>Standard-error readers still running: each ends when its helper's pipe ends.</summary>
        internal int StderrReadersRunning;
        /// <summary>Lines of helper standard error traced, over the host's life.</summary>
        internal long StderrLinesTraced;

        /// <summary>
        /// One helper's standard error, line by line, into the trace and nowhere
        /// else. Ends when the helper and every copy of the pipe's write end are
        /// gone. Bounded: a helper that writes without end costs a count, not the trace.
        /// </summary>
        private void StderrLoop(AnonymousPipeServerStream err, int pid)
        {
            int traced = 0, beyond = 0;
            try
            {
                using var reader = new StreamReader(err, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (traced >= MaxStderrLinesTraced) { beyond++; continue; }
                    traced++;
                    Interlocked.Increment(ref StderrLinesTraced);
                    Trace.WriteLine($"WdspHost: helper pid {pid} standard error: {line}");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WdspHost: reading helper pid {pid}'s standard error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (beyond > 0) Trace.WriteLine($"WdspHost: helper pid {pid} wrote {beyond} more lines of standard error, not traced");
                err.Dispose();
                Interlocked.Decrement(ref StderrReadersRunning);
            }
        }

        private void RaiseAll(List<WdspHelperStateChangedEventArgs> raise)
        {
            foreach (var args in raise)
            {
                var a = args;
                ThreadPool.QueueUserWorkItem(_ => StateChanged?.Invoke(this, a));
            }
        }

        /// <summary>
        /// Close the pipe, wait briefly for the helper to leave, then close the
        /// job, which kills it if it has not. The job handle never outlives this.
        /// </summary>
        private void Teardown(WdspConnection c, bool kill)
        {
            c.Stopping = true;
            c.Wake.Set();
            if (kill) WdspNative.Terminate(c.Process, 1);
            try { c.Cancel.Cancel(); } catch (ObjectDisposedException) { }
            try { c.Pipe.Dispose(); } catch (Exception ex) { Trace.WriteLine("WdspHost: closing the pipe: " + ex.Message); }
            bool exited = c.Exited.WaitOne(Policy.ShutdownWait);
            c.Job.Dispose();
            if (!exited) c.Exited.WaitOne(Policy.ShutdownWait);
            uint? code = WdspNative.ExitCode(c.Process);
            Trace.WriteLine($"WdspHost: pid {c.Pid} stopped, exit code " +
                (code is uint x ? $"{x} ({(kill && x == 1 ? "ended by JJ Flexible" : WdspProtocol.DescribeExit(x))})" : "pending") +
                (exited ? "" : " (ended by closing its job)"));
            c.Exited.Dispose();
            c.Process.Dispose();
        }

        /// <summary>Tests only: close the current helper's job handle and nothing else.</summary>
        internal void CloseJobForTest()
        {
            WdspConnection? c;
            lock (Gate) c = _conn;
            c?.Job.Dispose();
        }

        /// <summary>Tests only: whether the running helper is inside the job we made for it.</summary>
        internal bool CurrentHelperIsInItsJob()
        {
            lock (Gate) return _conn != null && WdspNative.InJob(_conn.Process, _conn.Job);
        }

        // ------------------------------------------------------------------
        // The reader thread
        // ------------------------------------------------------------------

        private static readonly string JJFlexibleVersion =
            (Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(WdspHost).Assembly.GetName().Version)?.ToString() ?? "0";

        /// <summary>Tests only: runs on the breaking thread the instant its failure is visible.</summary>
        internal Action<WdspConnection>? FailurePublishedForTest;

        /// <summary>Tests only: runs on the writer when it has found the pipe gone and begins waiting for the reader to finish reading.</summary>
        internal Action<WdspConnection>? WriterAwaitsReaderForTest;

        /// <summary>Tests only: break this connection as the reader or writer would.</summary>
        internal void BreakForTest(WdspConnection c, WdspCondition condition, string detail) => Break(c, condition, detail);

        private void Break(WdspConnection c, WdspCondition condition, string detail)
        {
            // The first failure wins, and it is visible only once it is complete.
            if (Interlocked.CompareExchange(ref c.Failure, new WdspFailure(condition, detail), null) != null) return;
            FailurePublishedForTest?.Invoke(c);
            _controlWake.Set();
        }

        private void ReaderLoop(WdspConnection c)
        {
            try
            {
                c.Pipe.WaitForConnectionAsync(c.Cancel.Token).GetAwaiter().GetResult();
                int? client = WdspNative.ClientProcessId(c.Pipe);
                if (client != c.Pid)
                {
                    Break(c, WdspCondition.BrokenPipe, $"the pipe's client is process {client}, not the helper {c.Pid}");
                    return;
                }
                Volatile.Write(ref c.LastReceive, Now);
                c.Connected = true;
                c.Control.Enqueue(WdspProtocol.Encode(new WdspHello(WdspProtocol.ProtocolVersion, WdspProtocol.ProtocolVersion, JJFlexibleVersion)));
                new Thread(() => WriterLoop(c)) { IsBackground = true, Name = "WDSP helper writer" }.Start();
                var reader = new WdspFrameReader();
                var buffer = new byte[65536];
                long skipped = 0;
                while (true)
                {
                    int n = c.Pipe.Read(buffer, 0, buffer.Length);
                    if (n == 0)
                    {
                        c.ReaderDone.Set(); // everything the helper wrote has been read, its last Error included
                        PipeEnded(c, "the helper closed the pipe", writer: false);
                        return;
                    }
                    ReadOnlySpan<byte> data = buffer.AsSpan(0, n);
                    while (data.Length > 0)
                    {
                        int used = reader.Push(data);
                        data = data.Slice(used);
                        if (reader.Failed)
                            throw new WdspProtocolException($"a message length of {reader.FailedLength}, outside 2 to 65,536");
                        if (reader.SkippedCount != skipped)
                        {
                            skipped = reader.SkippedCount;
                            Volatile.Write(ref c.LastReceive, Now);
                            Trace.WriteLine($"WdspHost: skipped a message of unknown type 0x{reader.LastSkippedType:X4}, {reader.LastSkippedLength} bytes");
                        }
                        if (reader.HasMessage)
                        {
                            Volatile.Write(ref c.LastReceive, Now);
                            Dispatch(c, reader.Type, reader.Payload);
                            reader.Release();
                        }
                    }
                }
            }
            catch (WdspProtocolException ex)
            {
                try { c.Control.Enqueue(WdspProtocol.Encode(new WdspError(0, ex.Code, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message))); c.Wake.Set(); }
                catch (ArgumentException) { }
                // Before the handshake as after it: Contract 2's Fatal rule is the
                // crash path. (An unreadable HelloAck never reaches here; Dispatch
                // reads it as an invalid HelloAck, which is a mismatch.)
                Break(c, WdspCondition.FatalProtocolError, (c.HelloResult == null ? "before the handshake: " : "") + ex.Message);
            }
            catch (Exception ex)
            {
                c.ReaderDone.Set();
                PipeEnded(c, ex.GetType().Name + ": " + ex.Message, writer: false);
            }
            finally
            {
                c.ReaderDone.Set();
            }
        }

        /// <summary>
        /// The pipe ended under the reader or the writer, whichever saw it
        /// first (both call this; the first to publish wins). The helper exits a few milliseconds
        /// after its pipe closes, so wait (bounded) for it and name the exit:
        /// 6 is the helper ending the connection for a protocol error, a flood
        /// included, and anything it said first goes with it. Still running
        /// after the wait: a broken pipe, and the control thread kills it.
        /// </summary>
        /// <remarks>
        /// The writer can find the end while the reader is still behind, with
        /// the helper's last Error unread in the pipe. A process that has exited
        /// proves nothing about what has been read, so the writer then also waits,
        /// bounded by the same time again, for the reader to reach the end of the
        /// pipe; the reason it publishes then carries what the helper said. If
        /// the reader is held longer than that, the exit is still named, without
        /// the text.
        /// </remarks>
        private void PipeEnded(WdspConnection c, string how, bool writer)
        {
            if (c.Stopping) return;
            uint? code = null;
            try
            {
                if (c.Exited.WaitOne(Policy.ExitNamingWait)) code = WdspNative.ExitCode(c.Process);
            }
            catch (ObjectDisposedException) { return; } // torn down meanwhile: nothing left to say
            if (writer && !c.ReaderDone.IsSet)
            {
                WriterAwaitsReaderForTest?.Invoke(c);
                c.ReaderDone.Wait(Policy.ExitNamingWait);
            }
            if (c.Stopping) return;
            if (code is uint x) Break(c, WdspProtocol.ConditionForExit(x), WdspProtocol.ExitDetail(x, c.HelperSaid));
            else Break(c, WdspCondition.BrokenPipe, how + (c.HelperSaid is { } said ? "; before closing it said " + said : "") +
                $"; the helper was still running {Policy.ExitNamingWait.TotalMilliseconds} ms later");
        }

        /// <summary>Tests only: runs on the reader before each message is handled (a test holds it to stop the reader reading).</summary>
        internal Action<WdspConnection>? DispatchHookForTest;

        private void Dispatch(WdspConnection c, WdspMessageType type, ReadOnlySpan<byte> payload)
        {
            DispatchHookForTest?.Invoke(c);
            if (type == WdspMessageType.FrameOut)
            {
                if (c.HelloResult == null) throw new WdspProtocolException("FrameOut before the handshake", WdspResult.UnexpectedMessage);
                OnFrameOut(c, payload);
                return;
            }
            object message;
            try { message = WdspProtocol.Decode(type, payload, WdspRole.JJFlexible); }
            catch (WdspProtocolException ex) when (c.HelloResult == null && type == WdspMessageType.HelloAck)
            {
                // A HelloAck that cannot be read is an invalid HelloAck: Contract 2
                // maps that to a mismatch, like one that reads but breaks a rule.
                c.HelloResult = "an invalid HelloAck: " + ex.Message;
                _controlWake.Set();
                return;
            }
            if (c.HelloResult == null)
            {
                switch (message)
                {
                    case WdspHelloAck ack:
                        string? bad = WdspProtocol.ValidateHelloAck(ack);
                        c.HelloResult = bad == null ? ack : "an invalid HelloAck: " + bad;
                        _controlWake.Set();
                        return;
                    case WdspHelloReject reject:
                        c.HelloResult = reject;
                        _controlWake.Set();
                        return;
                    case WdspError fatal when WdspProtocol.IsFatal(fatal.Code):
                        // A protocol error, the helper's side (Error 256 or 257, then
                        // the close and exit 6). Contract 2's Fatal rule sends it down
                        // the crash path; only a HelloReject is a mismatch. So it is
                        // kept, as after the handshake, and the pipe ending names it.
                        c.HelperSaid = WdspProtocol.DescribeError(fatal);
                        Trace.WriteLine("WdspHost: the helper is ending the connection before the handshake: " + c.HelperSaid);
                        return;
                    default:
                        // Out of handshake order, a non-fatal Error included: a protocol
                        // error found on this side, which is the crash path too.
                        throw new WdspProtocolException(type + " before the handshake", WdspResult.UnexpectedMessage);
                }
            }
            switch (message)
            {
                case WdspHelloAck or WdspHelloReject:
                    throw new WdspProtocolException("a second handshake", WdspResult.UnexpectedMessage);
                case WdspOpenChannelAck ack:
                    if (c.ById.TryGetValue(ack.Channel, out var opened)) opened.Engine.OnOpenAck(opened, ack);
                    else Trace.WriteLine($"WdspHost: discarded an OpenChannelAck for forgotten channel {ack.Channel}");
                    return;
                case WdspCloseChannelAck ack:
                    OnCloseAck(c, ack);
                    return;
                case WdspSettingAck ack:
                    Interlocked.Increment(ref SettingAcksReceived);
                    if (c.ById.TryGetValue(ack.Channel, out var target))
                    {
                        // The room first, then the setting's own turn, so the writer can
                        // never count this setting twice at once; SettingAnswered then
                        // counts the progress the writer looks for, after both.
                        bool counted = TakeOne(ref target.SettingsInFlight);
                        if (counted) Interlocked.Decrement(ref c.SettingsInFlight);
                        int slot = target.Engine.SettingIndex(ack.Name);
                        bool turn = slot >= 0 && slot < target.SettingOutstanding.Length && TakeOne(ref target.SettingOutstanding[slot]);
                        if (slot < 0) Trace.WriteLine($"WdspHost: a SettingAck on channel {ack.Channel} names {ack.Name}, which its engine does not have");
                        if (counted || turn) SettingAnswered(c, 0);
                        target.Engine.OnSettingAck(ack);
                    }
                    else if (ack.Channel != 0 && ack.Channel < Volatile.Read(ref c.NextChannelId))
                    {
                        // Sent before the close, answered after it (UnknownChannel): its
                        // count went when the close was answered.
                        Interlocked.Increment(ref SettingAcksAfterClose);
                        Trace.WriteLine($"WdspHost: discarded a SettingAck for channel {ack.Channel}, already closed: {ResultName(ack.Result)}");
                    }
                    else
                    {
                        Interlocked.Increment(ref SettingAcksUnplaced);
                        Trace.WriteLine($"WdspHost: discarded a SettingAck for channel {ack.Channel}, which this connection never opened");
                    }
                    return;
                case WdspHeartbeat:
                    return;
                case WdspError error when WdspProtocol.IsFatal(error.Code):
                    // Once per connection, never rate-limited: it is the reason the pipe is about to close.
                    c.HelperSaid = WdspProtocol.DescribeError(error);
                    Trace.WriteLine("WdspHost: the helper is ending the connection: " + c.HelperSaid);
                    return;
                case WdspError error:
                    if (RateAllows(c))
                        Trace.WriteLine($"WdspHost: helper error on channel {error.Channel}: " +
                            $"{(WdspProtocol.IsKnownResult(error.Code) ? error.Code.ToString() : "code " + (ushort)error.Code)}: {error.Text}");
                    return;
            }
        }

        /// <summary>
        /// A CloseChannelAck. JJ Flexible sends one close per channel and never
        /// waits for its answer. The helper, as repaired, gives a close repeated
        /// before its ack no answer of its own, and answers UnknownChannel to a
        /// close it reads after the channel has left its table: a channel closed
        /// while a build was failing, an open it refused, or a repeat read just
        /// after the ack was queued. So for a channel this connection has closed,
        /// Ok and UnknownChannel both simply settle it. Anything else means the
        /// two ends disagree about a channel, and is traced as that.
        /// </summary>
        private void OnCloseAck(WdspConnection c, WdspCloseChannelAck ack)
        {
            c.ById.TryGetValue(ack.Channel, out var ch);
            if (ch != null && !ch.Closed)
            {
                // Kept: the channel is still ours, and its frames are still wanted.
                Interlocked.Increment(ref CloseAcksUnexpected);
                Trace.WriteLine($"WdspHost: the helper answered a close for channel {ack.Channel}, which JJ Flexible has not closed: {ResultName(ack.Result)}");
                return;
            }
            if (ch != null)
            {
                c.ById.TryRemove(ack.Channel, out _);
                int left = Interlocked.Exchange(ref ch.SettingsInFlight, 0);
                if (left > 0) SettingAnswered(c, left);
            }
            bool issued = ack.Channel != 0 && ack.Channel < Volatile.Read(ref c.NextChannelId);
            bool settles = ack.Result == WdspResult.Ok ? ch != null : ack.Result == WdspResult.UnknownChannel && issued;
            if (!settles)
            {
                Interlocked.Increment(ref CloseAcksUnexpected);
                Trace.WriteLine($"WdspHost: a CloseChannelAck JJ Flexible cannot place, for channel {ack.Channel}: {ResultName(ack.Result)}");
                return;
            }
            Interlocked.Increment(ref CloseAcksSettled);
            if (ack.Result != WdspResult.Ok)
                Trace.WriteLine($"WdspHost: channel {ack.Channel}'s close was answered UnknownChannel, as expected: the helper had already let it go");
        }

        /// <summary>Take one from a count that must not go below zero (an answer nobody asked for takes nothing).</summary>
        private static bool TakeOne(ref int count)
        {
            int seen = Volatile.Read(ref count);
            while (seen > 0)
            {
                int was = Interlocked.CompareExchange(ref count, seen - 1, seen);
                if (was == seen) return true;
                seen = was;
            }
            return false;
        }

        /// <summary>
        /// Reader: settings answered; room or a setting's turn again, so wake a
        /// writer that was holding some back. The progress count is raised (a
        /// full fence) before the held flag is read, the mirror of the writer.
        /// </summary>
        private static void SettingAnswered(WdspConnection c, int count)
        {
            if (count != 0) Interlocked.Add(ref c.SettingsInFlight, -count);
            Interlocked.Increment(ref c.SettingProgress);
            if (Volatile.Read(ref c.SettingsHeld) != 0) c.Wake.Set();
        }

        private void NoteSettingsInFlight(int now)
        {
            long peak = Volatile.Read(ref PeakSettingsInFlight);
            while (now > peak)
            {
                long was = Interlocked.CompareExchange(ref PeakSettingsInFlight, now, peak);
                if (was == peak) return;
                peak = was;
            }
        }

        private static string ResultName(WdspResult result) =>
            WdspProtocol.IsKnownResult(result) ? result.ToString() : "code " + (ushort)result;

        private static bool RateAllows(WdspConnection c)
        {
            long now = Now;
            if (now - c.LastBadTrace < Stopwatch.Frequency) return false;
            c.LastBadTrace = now;
            return true;
        }

        private static void OnFrameOut(WdspConnection c, ReadOnlySpan<byte> payload)
        {
            WdspProtocol.ReadFrameOutHeader(payload, out uint channel, out ulong seq, out _, out int count);
            if (!c.ById.TryGetValue(channel, out var ch))
            {
                if (RateAllows(c)) Trace.WriteLine($"WdspHost: discarded a FrameOut for forgotten channel {channel}");
                return;
            }
            if (count != WdspProtocol.FrameLength)
            {
                if (RateAllows(c)) Trace.WriteLine($"WdspHost: discarded a {count}-sample FrameOut on channel {channel}");
                return;
            }
            for (int i = 0; i < count; i++)
            {
                if (!float.IsFinite(WdspProtocol.FrameOutSample(payload, i)))
                {
                    if (RateAllows(c)) Trace.WriteLine($"WdspHost: discarded a FrameOut with a non-finite sample on channel {channel}");
                    return;
                }
            }
            ch.Publish(seq, payload);
        }

        // ------------------------------------------------------------------
        // The writer thread
        // ------------------------------------------------------------------

        private void WriterLoop(WdspConnection c)
        {
            var frame = new byte[WdspProtocol.FrameSize(WdspProtocol.FrameLength)];
            var heartbeat = WdspProtocol.Encode(WdspHeartbeat.Instance);
            var settings = new List<byte[]>();
            long lastSend = Now, beat = Ticks(Policy.Heartbeat);
            try
            {
                while (!c.Stopping && !c.IsBroken)
                {
                    long due = lastSend + beat - Now;
                    c.Wake.WaitOne((int)Math.Clamp(due * 1000 / Stopwatch.Frequency, 0, int.MaxValue));
                    if (c.Stopping) break;
                    bool wrote = false;
                    while (c.Control.TryDequeue(out var message)) { c.Pipe.Write(message); wrote = true; }
                    if (c.HelloDone)
                    {
                        var lives = c.Lives;
                        foreach (var ch in lives)
                        {
                            if (!ch.Opened || ch.Closed) continue;
                            while (ch.TryPeekSend(out var samples, out ulong seq))
                            {
                                int n = WdspProtocol.EncodeFrame(frame, ch.Id, seq, samples);
                                ch.ConsumeSend();
                                c.Pipe.Write(frame, 0, n);
                                Interlocked.Increment(ref FramesSent);
                                wrote = true;
                            }
                        }
                        // Settings after the frames, within the room left, each channel
                        // first in turn, and no setting with an answer still owed. A
                        // value not sent now stays asked for, and its latest form goes
                        // when an answer makes room or gives that setting its turn.
                        bool held = false;
                        long progress = Interlocked.Read(ref c.SettingProgress);
                        int start = c.SettingRotation++;
                        for (int k = 0; k < lives.Length; k++)
                        {
                            var ch = lives[(int)((uint)(start + k) % (uint)lives.Length)];
                            if (!ch.Opened || ch.Closed) continue;
                            int room = Policy.MaxSettingsInFlight - Volatile.Read(ref c.SettingsInFlight);
                            if (ch.Engine.CollectChangedSettings(ch, settings, room, Policy.MaxInFlightPerSetting)) held = true;
                            foreach (var s in settings)
                            {
                                // Counted before it is written, so its answer can never arrive first.
                                NoteSettingsInFlight(Interlocked.Increment(ref c.SettingsInFlight));
                                Interlocked.Increment(ref ch.SettingsInFlight);
                                c.Pipe.Write(s);
                                Interlocked.Increment(ref SetSettingsSent);
                                wrote = true;
                            }
                            settings.Clear();
                        }
                        Interlocked.Exchange(ref c.SettingsHeld, held ? 1 : 0);
                        // An answer may have come between the look and the flag (it then
                        // saw no flag and woke nobody): look again now. Only an answer
                        // moves the count, so a setting waiting on its own answer
                        // does not make this spin.
                        if (held && Interlocked.Read(ref c.SettingProgress) != progress) c.Wake.Set();
                    }
                    if (wrote) lastSend = Now;
                    else if (Now - lastSend >= beat) { c.Pipe.Write(heartbeat); lastSend = Now; }
                }
            }
            catch (Exception ex)
            {
                // The writer can see the pipe end first; it names the exit just as the
                // reader does, once the reader has read what the helper said last.
                PipeEnded(c, "writing: " + ex.GetType().Name + ": " + ex.Message, writer: true);
            }
        }

        // ------------------------------------------------------------------

        public void Dispose()
        {
            Thread? control;
            WdspConnection? orphan = null;
            lock (Gate)
            {
                if (_disposed) return;
                _disposed = true;
                control = _control;
                foreach (var e in _wanted) e.SetUnavailableLocked(Unavailability.HelperNotRunning, "the helper host has shut down", final: true);
                if (control == null) { orphan = _conn; _conn = null; }
            }
            _controlWake.Set();
            if (control != null && !control.Join(TimeSpan.FromSeconds(5)))
                Trace.WriteLine("WdspHost: the control thread did not stop within 5 s");
            if (orphan != null) Teardown(orphan, kill: false);
        }
    }
}
