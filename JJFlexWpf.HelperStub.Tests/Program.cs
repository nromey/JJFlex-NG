// MIT. A stand-in for the WDSP helper, written from Contract 2 alone, for
// JJ Flexible's tests. It runs no noise reduction: it echoes each channel's
// audio delayed by a known number of samples that is not a multiple of 480,
// so every test can check alignment to the sample.
//
// Every exit goes through Environment.Exit with a code, never an unhandled
// exception, so Windows Error Reporting is never involved.
//
// Command line: --pipe <name> [test options], or --sleep <ms> to act as a
// short-lived hidden "parent" for the parent-exit test, or
// --sacrificial-parent <pid file> to start a child through JJ Flexible's own
// start code (WdspNative.StartInJob, linked) and freeze the moment the child
// exists, writing its pid, so a test can kill this process at exactly that
// point and see whether the child goes with it.
//   --no-server-check        skip the pipe-server-is-my-parent check (exit 5)
//   --delay <samples>        algorithmic delay of every engine (default 2448)
//   --slow-start <ms>        wait this long before answering Hello
//   --reject                 answer Hello with HelloReject (speaks 2 to 3)
//   --invalid-hello          answer with a HelloAck that breaks a rule
//   --omit <engine>          leave an engine out of the offer
//   --late <seq>:<ms>        hold the FrameOut for that seq back by ms
//   --stall-after <n>:<ms>   after n frames, send nothing at all for ms
//   --crash-after <n>:<code> after n frames, exit with that code
//   --break-after <n>        after n frames, close the pipe
//   --refuse-setting <name>  refuse every SetSetting for that name
//   --refuse-open <engine>   refuse every OpenChannel for that engine (ChannelBuildFailed)
//   --slow-open <engine>:<ms> hold every OpenChannelAck for that engine back by ms
//   --bad-applied <how>      answer every Ok OpenChannelAck with malformed applied values:
//                            missing, order, duplicate or refused
//   --exit-early <code>      exit with that code before connecting (the helper's 2, 3, 4 or 8)
//   --error-first            answer Hello with Error 257, then close and exit 6 (the
//                            helper refusing a first message)
//   --heartbeat-first        answer Hello with a Heartbeat before the HelloAck (out of
//                            handshake order: a protocol error JJ Flexible finds)
//   --garbled-hello          answer Hello with a HelloAck whose payload is one byte long
//                            (a HelloAck that cannot be read)
//   --fatal-after <n>        after n frames, send Error 257 naming a limit, close, exit 6
//                            (the helper's answer to a client that overflows a bound)
//   --fail-build <n>:<ms>    hold the n-th OpenChannel ms, then answer ChannelBuildFailed;
//                            closes read meanwhile get one CloseChannelAck UnknownChannel
//                            after it, as the helper answers a channel closed during a
//                            build that failed
//   --close-unknown          answer every CloseChannel UnknownChannel (the helper's answer
//                            to a close it reads after the channel left its table)
//   --swallow-closes         answer no CloseChannel at all (the helper's answer to a
//                            close repeated before its ack)
//   --slow-settings <ms>     take this long over each SetSetting
//   --request-limit <n>      more than n settings and closes waiting: Error 257, close,
//                            exit 6 (the helper's 4,096 limit, made small)
//   --hold-setting-acks <engine>:<answer|drop>
//                            hold that engine's SettingAcks until its channel closes; then
//                            send the CloseChannelAck and, for answer, each held setting as
//                            UnknownChannel after it (the shape the helper has today for a
//                            setting read after a close), or, for drop, nothing (the other
//                            reading of "nothing more for that channel")

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using JJFlexWpf;

namespace JJFlexWpf.HelperStub;

internal static class Program
{
    private const int ExitClean = 0, ExitBadCommandLine = 2, ExitNoParent = 3, ExitNoPipe = 4, ExitWrongServer = 5,
        ExitProtocol = 6, ExitRejected = 7, ExitHadAConsoleWindow = 40;

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    private const uint SYNCHRONIZE = 0x00100000;

    private static NamedPipeClientStream _pipe = null!;
    private static readonly object WriteGate = new();
    // Bounded like the helper's frame queue, plus room for a request limit to be reached.
    private static BlockingCollection<object> Work = new(new ConcurrentQueue<object>(), 64);
    private static int _slowSettingsMs, _requestLimit = -1, _requestsWaiting;
    private static string? _holdAcksEngine;
    private static bool _holdAcksAnswer;
    /// <summary>Held SettingAcks by channel. Processing thread only.</summary>
    private static readonly Dictionary<uint, List<string>> HeldAcks = new();
    private static readonly Dictionary<uint, StubChannel> Channels = new();
    private static readonly HashSet<uint> UsedIds = new();
    private static int _delay = 2448, _slowStart, _stallAfter = -1, _stallMs, _crashAfter = -1, _crashCode, _breakAfter = -1;
    private static long _lateSeq = -1;
    private static int _lateMs;
    private static bool _reject, _invalidHello, _noServerCheck;
    private static readonly HashSet<string> Omit = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RefuseOpen = new(StringComparer.Ordinal);
    private static string? _refuseSetting;
    private static string? _badApplied;
    private static string? _slowOpenEngine;
    private static int _slowOpenMs;
    private static long _framesProcessed;
    private static bool _answered;
    private static bool _errorFirst, _heartbeatFirst, _garbledHello;
    private static int _fatalAfter = -1;
    private static int _failBuild = -1, _failBuildMs, _opens;
    private static bool _closeUnknown, _swallowCloses;
    /// <summary>Channels whose build is pending a failure, and how many closes each has had meanwhile. Under Channels' lock.</summary>
    private static readonly Dictionary<uint, int> Building = new();

    private sealed class StubChannel
    {
        public required uint Id;
        public required string Engine;
        public required int Latency;
        public required Dictionary<string, WdspValue> Values;
        public required float[] Line;
        public int Position;
        public long LastSeq = -1;
    }

    private static int Main(string[] args)
    {
        // The flag that keeps a console window from ever appearing is
        // CREATE_NO_WINDOW, set by the host. If it is ever missing, this stub
        // says so by failing to start, rather than leaving a window up.
        if (GetConsoleWindow() != IntPtr.Zero) Environment.Exit(ExitHadAConsoleWindow);
        string? pipeName = null;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException(args[i] + " needs a value");
                (int, int) Pair() { var p = Next().Split(':'); return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)); }
                switch (args[i])
                {
                    case "--sleep": Thread.Sleep(int.Parse(Next(), CultureInfo.InvariantCulture)); Environment.Exit(ExitClean); break;
                    case "--sacrificial-parent": SacrificialParent(Next()); break;
                    case "--pipe": pipeName = Next(); break;
                    case "--no-server-check": _noServerCheck = true; break;
                    case "--delay": _delay = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--slow-start": _slowStart = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--reject": _reject = true; break;
                    case "--invalid-hello": _invalidHello = true; break;
                    case "--omit": Omit.Add(Next()); break;
                    case "--late": { var (s, ms) = Pair(); _lateSeq = s; _lateMs = ms; break; }
                    case "--stall-after": (_stallAfter, _stallMs) = Pair(); break;
                    case "--crash-after": (_crashAfter, _crashCode) = Pair(); break;
                    case "--break-after": _breakAfter = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--refuse-setting": _refuseSetting = Next(); break;
                    case "--refuse-open": RefuseOpen.Add(Next()); break;
                    case "--bad-applied": _badApplied = Next(); break;
                    case "--exit-early": Environment.Exit(int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--error-first": _errorFirst = true; break;
                    case "--heartbeat-first": _heartbeatFirst = true; break;
                    case "--garbled-hello": _garbledHello = true; break;
                    case "--fatal-after": _fatalAfter = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--fail-build": (_failBuild, _failBuildMs) = Pair(); break;
                    case "--close-unknown": _closeUnknown = true; break;
                    case "--swallow-closes": _swallowCloses = true; break;
                    case "--hold-setting-acks":
                    {
                        var p = Next().Split(':');
                        _holdAcksEngine = p[0];
                        _holdAcksAnswer = p[1] == "answer";
                        break;
                    }
                    case "--slow-settings": _slowSettingsMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--request-limit":
                        _requestLimit = int.Parse(Next(), CultureInfo.InvariantCulture);
                        Work = new(new ConcurrentQueue<object>(), 64 + _requestLimit + 1);
                        break;
                    case "--slow-open": { var p = Next().Split(':'); _slowOpenEngine = p[0]; _slowOpenMs = int.Parse(p[1], CultureInfo.InvariantCulture); break; }
                    default: throw new FormatException("unknown argument " + args[i]);
                }
            }
        }
        catch (FormatException) { Environment.Exit(ExitBadCommandLine); }
        const string prefix = @"\\.\pipe\jjflex-wdsp-";
        if (pipeName == null || !pipeName.StartsWith(prefix, StringComparison.Ordinal)) Environment.Exit(ExitBadCommandLine);
        var rest = pipeName![prefix.Length..];
        int dash = rest.IndexOf('-');
        int parentPid = 0;
        if (dash < 1 || !int.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid)
            || rest.Length - dash - 1 != 32) Environment.Exit(ExitBadCommandLine);

        IntPtr parent = OpenProcess(SYNCHRONIZE, false, parentPid);
        if (parent == IntPtr.Zero) Environment.Exit(ExitNoParent);
        new Thread(() => { WaitForSingleObject(parent, uint.MaxValue); Environment.Exit(ExitClean); }) { IsBackground = true }.Start();

        _pipe = new NamedPipeClientStream(".", pipeName[@"\\.\pipe\".Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { _pipe.Connect(5000); } catch (Exception) { Environment.Exit(ExitNoPipe); }
        if (!_noServerCheck && (!GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out uint server) || server != parentPid))
            Environment.Exit(ExitWrongServer);

        new Thread(ProcessLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
        ReadLoop();
        return ExitClean;
    }

    /// <summary>
    /// Start a child exactly as JJ Flexible starts its helper, and stop dead the
    /// moment CreateProcess has returned: the pid goes to the file (written
    /// whole, then renamed, so a reader never sees half of it) and this thread
    /// never returns. The child is this same stub told to sleep a minute and
    /// exit, so even a child that somehow ran would end by itself; it never
    /// runs, because it is still suspended.
    /// </summary>
    private static void SacrificialParent(string pidFile)
    {
        string? self = Environment.ProcessPath;
        if (self == null) { Environment.Exit(ExitBadCommandLine); return; }
        WdspNative.StartInJob(self, "\"" + self + "\" --sleep 60000", pid =>
        {
            string partial = pidFile + ".partial";
            File.WriteAllText(partial, pid.ToString(CultureInfo.InvariantCulture));
            File.Move(partial, pidFile);
            Thread.Sleep(Timeout.Infinite);
        });
        Environment.Exit(ExitClean);
    }

    private static void Send(object message)
    {
        var bytes = WdspProtocol.Encode(message);
        lock (WriteGate)
        {
            try { _pipe.Write(bytes); }
            catch (IOException) { Environment.Exit(ExitClean); }
            catch (ObjectDisposedException) { Environment.Exit(ExitClean); }
        }
    }

    private static void ProtocolError(string why, WdspResult code = WdspResult.MalformedMessage)
    {
        try { Send(new WdspError(0, code, why)); } catch (ArgumentException) { }
        Environment.Exit(ExitProtocol);
    }

    // ------------------------------------------------------------------ offer

    private static EngineSettingDescriptor[] NnrSettings() => new EngineSettingDescriptor[] {
        new("model", "audio.denoise.nnr.model", EngineSettingKind.Choice, new EngineSettingValue(0, "standard"),
            Options: new[] { new EngineSettingOption("standard", "audio.denoise.nnr.model.standard"),
                             new EngineSettingOption("premium", "audio.denoise.nnr.model.premium"),
                             new EngineSettingOption("standard_cw", "audio.denoise.nnr.model.standard_cw"),
                             new EngineSettingOption("premium_cw", "audio.denoise.nnr.model.premium_cw") }, ChangesLatency: true),
        new("mask_floor_db", "audio.denoise.nnr.mask_floor_db", EngineSettingKind.Number, new EngineSettingValue(-25),
            -40, -10, 0, "audio.unit.decibels", new double[] { -40, -35, -30, -27, -25, -22, -20, -15, -10 }),
    };

    private static EngineSettingDescriptor[] Nr2Settings() => new EngineSettingDescriptor[] {
        new("gain_method", "audio.denoise.nr2.gain_method", EngineSettingKind.Choice, new EngineSettingValue(0, "trained"),
            Options: new[] { "linear", "log", "trained" }.Select(o => new EngineSettingOption(o, "audio.denoise.nr2.gain_method." + o)).ToArray()),
        new("trained_t2", "audio.denoise.nr2.trained_t2", EngineSettingKind.Number, new EngineSettingValue(0.2), 0.02, 0.3, 0.01),
    };

    private static readonly Dictionary<string, EngineSettingDescriptor[]> Engines = new(StringComparer.Ordinal)
    {
        ["nnr"] = NnrSettings(),
        ["nr2"] = Nr2Settings(),
    };

    /// <summary>The channel's delay: the stub's base delay, one frame more for NNR's two premium models.</summary>
    private static int LatencyFor(string engine, IReadOnlyDictionary<string, WdspValue> values) =>
        _delay + (engine == "nnr" && values.TryGetValue("model", out var m) && m.Choice is "premium" or "premium_cw" ? 480 : 0);

    private static WdspHelloAck Offer()
    {
        var offers = Engines.Where(e => !Omit.Contains(e.Key))
            .Select(e => new WdspEngineOffer(e.Key, 3, 48000, 480, (uint)_delay, 100, 50_000, 90_000, e.Value)).ToArray();
        return new WdspHelloAck(1, "stub-1", "0.0", new string('0', 40), "none", (ushort)(_invalidHello ? 4 : 16),
            Array.Empty<WdspFingerprint>(), offers);
    }

    // ------------------------------------------------------------------ reading

    private static void ReadLoop()
    {
        var reader = new WdspFrameReader();
        var buffer = new byte[65536];
        bool helloSeen = false;
        while (true)
        {
            int n;
            try { n = _pipe.Read(buffer, 0, buffer.Length); }
            catch (Exception) { Environment.Exit(ExitClean); return; }
            if (n == 0) Environment.Exit(_reject ? ExitRejected : ExitClean);
            ReadOnlySpan<byte> data = buffer.AsSpan(0, n);
            while (data.Length > 0)
            {
                int used = reader.Push(data);
                data = data.Slice(used);
                if (reader.Failed) ProtocolError("bad length");
                if (!reader.HasMessage) continue;
                object message;
                try { message = WdspProtocol.Decode(reader.Type, reader.Payload, WdspRole.Helper); }
                catch (WdspProtocolException ex) { ProtocolError(ex.Message, ex.Code); return; }
                reader.Release();
                if (!helloSeen)
                {
                    if (message is not WdspHello hello) { ProtocolError("expected Hello", WdspResult.UnexpectedMessage); return; }
                    helloSeen = true;
                    if (_errorFirst) { ProtocolError("refused by the test's command line", WdspResult.UnexpectedMessage); return; }
                    if (_slowStart > 0) Thread.Sleep(_slowStart);
                    if (_heartbeatFirst) Send(WdspHeartbeat.Instance);
                    if (_garbledHello)
                    {
                        // length 3, type 0x0002, one byte of payload: too short for any HelloAck.
                        lock (WriteGate) { _pipe.Write(new byte[] { 3, 0, 0, 0, 0x02, 0x00, 0x01 }); }
                        continue;
                    }
                    if (_reject || hello.Lowest > 1 || hello.Highest < 1)
                    {
                        Send(new WdspHelloReject(2, 3, "this helper speaks protocol 2 to 3"));
                        _reject = true;
                        continue;
                    }
                    Send(Offer());
                    Volatile.Write(ref _answered, true);
                    continue;
                }
                if (_reject) continue;
                Handle(message);
            }
        }
    }

    private static void Handle(object message)
    {
        switch (message)
        {
            case WdspHello: ProtocolError("a second Hello", WdspResult.UnexpectedMessage); return;
            case WdspOpenChannel open: Open(open); return;
            case WdspFrame frame:
                lock (Channels)
                {
                    if (!Channels.TryGetValue(frame.Channel, out var ch)) { Send(new WdspError(frame.Channel, WdspResult.UnknownChannel, "no such channel")); return; }
                    if (frame.Samples.Length != 480) { Send(new WdspError(frame.Channel, WdspResult.WrongFrameLength, "frames are 480 samples")); return; }
                    if (frame.Samples.Any(s => !float.IsFinite(s))) { Send(new WdspError(frame.Channel, WdspResult.InvalidSample, "a non-finite sample")); return; }
                    if ((long)frame.Seq <= ch.LastSeq) { Send(new WdspError(frame.Channel, WdspResult.SeqNotIncreasing, "seq must rise")); return; }
                    ch.LastSeq = (long)frame.Seq;
                }
                Work.Add(frame);
                return;
            case WdspCloseChannel close:
                lock (Channels)
                    if (Building.TryGetValue(close.Channel, out int held)) { Building[close.Channel] = held + 1; return; }
                if (_swallowCloses) return;
                AddRequest(message);
                return;
            case WdspSetSetting: AddRequest(message); return;
            case WdspHeartbeat or WdspError: return;
        }
    }

    /// <summary>A setting or a close joins the queue; past the limit, the connection ends as the helper ends it.</summary>
    private static void AddRequest(object message)
    {
        if (Interlocked.Increment(ref _requestsWaiting) > _requestLimit && _requestLimit >= 0)
            ProtocolError($"more than {_requestLimit} settings and closes queued", WdspResult.UnexpectedMessage);
        Work.Add(message);
    }

    private static void Open(WdspOpenChannel open)
    {
        WdspOpenChannelAck Refuse(WdspResult result, string detail) =>
            new(open.Channel, result, 0, Array.Empty<WdspNamedValue>(), detail);
        WdspOpenChannelAck ack;
        lock (Channels)
        {
            if (++_opens == _failBuild && open.Channel != 0 && UsedIds.Add(open.Channel))
            {
                Building[open.Channel] = 0;
                new Thread(() =>
                {
                    Thread.Sleep(_failBuildMs);
                    int closes;
                    lock (Channels) { closes = Building[open.Channel]; Building.Remove(open.Channel); }
                    Send(Refuse(WdspResult.ChannelBuildFailed, "the build failed, by the test's command line"));
                    if (closes > 0) Send(new WdspCloseChannelAck(open.Channel, WdspResult.UnknownChannel));
                }) { IsBackground = true }.Start();
                return;
            }
            if (open.Channel == 0 || !UsedIds.Add(open.Channel)) ack = Refuse(WdspResult.ChannelIdInUse, "id in use");
            else if (!Engines.TryGetValue(open.EngineId, out var descriptors) || Omit.Contains(open.EngineId)) ack = Refuse(WdspResult.EngineNotOffered, $"engine {open.EngineId} is not offered");
            else if (open.Direction > 1) ack = Refuse(WdspResult.DirectionNotServed, "unknown direction");
            else if (RefuseOpen.Contains(open.EngineId)) ack = Refuse(WdspResult.ChannelBuildFailed, "refused by the test's command line");
            else
            {
                var values = descriptors.ToDictionary(d => d.Name, d => WdspValue.From(d.Default), StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                WdspOpenChannelAck? refusal = null;
                foreach (var v in open.Initial)
                {
                    var d = descriptors.FirstOrDefault(x => x.Name == v.Name);
                    if (d == null) { refusal = Refuse(WdspResult.UnknownSetting, "no setting " + v.Name); break; }
                    if (!seen.Add(v.Name) || !d.Accepts(v.Value.ToSetting())) { refusal = Refuse(WdspResult.InvalidValue, "bad value for " + v.Name); break; }
                    values[v.Name] = v.Value;
                }
                if (refusal != null) ack = refusal;
                else
                {
                    int latency = LatencyFor(open.EngineId, values);
                    Channels[open.Channel] = new StubChannel
                    {
                        Id = open.Channel, Engine = open.EngineId, Latency = latency, Values = values, Line = new float[latency],
                    };
                    ack = new WdspOpenChannelAck(open.Channel, WdspResult.Ok, (uint)latency,
                        Malformed(descriptors, descriptors.Select(d => new WdspNamedValue(d.Name, values[d.Name])).ToArray()), "");
                }
            }
        }
        if (open.EngineId == _slowOpenEngine && ack.Result == WdspResult.Ok)
            new Thread(() => { Thread.Sleep(_slowOpenMs); Send(ack); }) { IsBackground = true }.Start();
        else Send(ack);
    }

    /// <summary>The applied values, broken the way --bad-applied asks (unchanged when it does not).</summary>
    private static WdspNamedValue[] Malformed(EngineSettingDescriptor[] descriptors, WdspNamedValue[] applied) => _badApplied switch
    {
        "missing" => applied[..^1],
        "order" => applied.Reverse().ToArray(),
        "duplicate" => applied[..^1].Append(applied[0]).ToArray(),
        "refused" => applied.Select((v, i) => i != 0 ? v : new WdspNamedValue(v.Name, descriptors[0].Kind == EngineSettingKind.Choice
            ? new WdspValue(WdspValue.ChoiceKind, 0, "no_such_option") : new WdspValue(WdspValue.NumberKind, descriptors[0].Max + 1000, null))).ToArray(),
        _ => applied,
    };

    // ------------------------------------------------------------------ processing

    private static void ProcessLoop()
    {
        var output = new float[480];
        var lastEmit = Stopwatch.StartNew();
        while (true)
        {
            if (!Work.TryTake(out var item, 50))
            {
                // Nothing at all before the handshake: the helper speaks only after reading Hello.
                if (Volatile.Read(ref _answered) && lastEmit.ElapsedMilliseconds >= 250) { Send(WdspHeartbeat.Instance); lastEmit.Restart(); }
                continue;
            }
            switch (item)
            {
                case WdspFrame frame:
                {
                    StubChannel? ch;
                    lock (Channels) Channels.TryGetValue(frame.Channel, out ch);
                    if (ch == null) break;
                    long count = Interlocked.Increment(ref _framesProcessed);
                    if (count == _crashAfter) Environment.Exit(_crashCode);
                    if (count == _fatalAfter) { ProtocolError("the client queued more than the stub's limit of settings and closes", WdspResult.UnexpectedMessage); return; }
                    if (count == _breakAfter) { lock (WriteGate) _pipe.Dispose(); Environment.Exit(ExitClean); }
                    if (count == _stallAfter) Thread.Sleep(_stallMs);
                    var timer = Stopwatch.StartNew();
                    for (int i = 0; i < 480; i++)
                    {
                        output[i] = ch.Line[ch.Position];
                        ch.Line[ch.Position] = frame.Samples[i];
                        ch.Position = (ch.Position + 1) % ch.Line.Length;
                    }
                    uint nanos = (uint)Math.Min(uint.MaxValue, timer.Elapsed.TotalMilliseconds * 1_000_000);
                    if ((long)frame.Seq == _lateSeq) Thread.Sleep(_lateMs);
                    Send(new WdspFrameOut(frame.Channel, frame.Seq, nanos, output));
                    lastEmit.Restart();
                    break;
                }
                case WdspSetSetting set:
                {
                    Interlocked.Decrement(ref _requestsWaiting);
                    if (_slowSettingsMs > 0) Thread.Sleep(_slowSettingsMs);
                    StubChannel? ch;
                    lock (Channels) Channels.TryGetValue(set.Channel, out ch);
                    if (ch == null) { Send(new WdspSettingAck(set.Channel, WdspResult.UnknownChannel, set.Setting.Name, WdspValue.None)); break; }
                    if (ch.Engine == _holdAcksEngine)
                    {
                        if (!HeldAcks.TryGetValue(ch.Id, out var held)) HeldAcks[ch.Id] = held = new List<string>();
                        held.Add(set.Setting.Name);
                        break;
                    }
                    var d = Engines[ch.Engine].FirstOrDefault(x => x.Name == set.Setting.Name);
                    if (d == null) Send(new WdspSettingAck(set.Channel, WdspResult.UnknownSetting, set.Setting.Name, WdspValue.None));
                    else if (d.ChangesLatency) Send(new WdspSettingAck(set.Channel, WdspResult.SettingNeedsNewChannel, d.Name, ch.Values[d.Name]));
                    else if (d.Name == _refuseSetting || !d.Accepts(set.Setting.Value.ToSetting()))
                        Send(new WdspSettingAck(set.Channel, WdspResult.InvalidValue, d.Name, ch.Values[d.Name]));
                    else
                    {
                        ch.Values[d.Name] = set.Setting.Value;
                        Send(new WdspSettingAck(set.Channel, WdspResult.Ok, d.Name, set.Setting.Value));
                    }
                    break;
                }
                case WdspCloseChannel close:
                {
                    Interlocked.Decrement(ref _requestsWaiting);
                    bool had;
                    lock (Channels) had = Channels.Remove(close.Channel);
                    Send(new WdspCloseChannelAck(close.Channel, had && !_closeUnknown ? WdspResult.Ok : WdspResult.UnknownChannel));
                    if (HeldAcks.Remove(close.Channel, out var late) && _holdAcksAnswer)
                        foreach (var name in late) Send(new WdspSettingAck(close.Channel, WdspResult.UnknownChannel, name, WdspValue.None));
                    break;
                }
            }
        }
    }
}
