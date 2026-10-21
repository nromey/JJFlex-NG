using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Radios.Listenback;

/// <summary>Disk consumer of the shared feed; the VITA callback never calls this.</summary>
public sealed class IqTakeWriter : IDisposable
{
    private readonly FileStream _iq;
    private readonly IqGapLedger _ledger = new();
    private readonly float[] _classifier = new float[16384];
    private int _classifierCount;
    private readonly int _rate;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IqPacketHeader? _first, _last;
    private long _pairs, _fill, _gaps, _discards, _overruns, _accountedDrops;
    private int _lastPacketPairs;
    private long _classifierStart;
    private double _maxArrivalJitter;
    private bool _failed, _finished;
    private ushort? _lastClass;
    private IqBlock _leading, _following;
    private bool _startupSettled;
    private long _startupDiscards;
    private bool _startupSignatureDetected;
    private readonly object _gate = new();
    public string DirectoryPath { get; }
    public TakeHeader Header { get; }
    public Task<bool> Ready => _ready.Task;
    public bool IsReady => _ready.Task.IsCompletedSuccessfully && _ready.Task.Result;
    public long PairsWritten => _pairs;
    public long FillPairs => _fill;
    public bool StartupSignatureDetected => _startupSignatureDetected;
    public string Failure { get; private set; }
    public Task<string> Failed => _failure.Task;
    private readonly TaskCompletionSource<string> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int EchoedRate { get; set; }
    public bool Active { get; set; }
    public (double Seconds, double Ratio, double Jitter, double Age, long Gaps, long Fill) Health()
    {
        lock (_gate)
        {
            double duration = _first is { } first && _last is { } last
                ? (last.ArrivalTicks - first.ArrivalTicks) / (double)Stopwatch.Frequency + _lastPacketPairs / (double)_rate : 0;
            return (duration, duration > 0 ? _pairs / (duration * _rate) : 0, _maxArrivalJitter,
                _last is { } end ? (Stopwatch.GetTimestamp() - end.ArrivalTicks) / (double)Stopwatch.Frequency : double.PositiveInfinity, _gaps, _fill);
        }
    }

    public IqTakeWriter(string directory, int requestedRate, IReadOnlyDictionary<string, string> provenance)
    {
        DirectoryPath = directory;
        _rate = requestedRate;
        Directory.CreateDirectory(directory);
        Header = new TakeHeader(Path.Combine(directory, "take.txt"));
        try
        {
            Header.Write("created-utc", DateTimeOffset.UtcNow.ToString("O"));
            Header.Write("rate-requested", requestedRate);
            Header.Write("iq-units", "radio-float32-little-endian; FlexLib samples multiplied by 32768");
            foreach (var pair in provenance) Header.Write(pair.Key, pair.Value);
            _iq = new FileStream(Path.Combine(directory, "rf.iq"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 65536);
        }
        catch { Header.Dispose(); throw; }
    }

    public void Accept(IqBlock block)
    {
        lock (_gate)
        {
            if (_finished || _failed) return;
            try { AcceptCore(block); }
            catch (Exception ex) { Fail("writer-" + ex.GetType().Name); }
        }
    }
    private void AcceptCore(IqBlock block)
    {
        var h = block.Header;
        if (_lastClass != h.ClassCode)
        {
            Header.Event("packet-class", h);
            _lastClass = h.ClassCode;
            if (_first == null)
            {
                _classifierCount = 0;
                foreach (var held in new[] { _leading, _following })
                    if (held != null)
                    {
                        Header.Event("startup-discard", new { Reason = "packet-class-changed-before-clock-established", held.Header, Pairs = held.Samples.Length / 2 });
                        _startupDiscards++;
                    }
                _leading = _following = null;
            }
        }
        if (!Active || EchoedRate != _rate || h.Rate != EchoedRate)
        {
            Header.Event("packet-rejected", new { h, Active, EchoedRate });
            if (_first != null) Fail("stream-state-or-rate-changed");
            return;
        }
        ReadOnlySpan<float> data = block.Samples.Span;
        if (data.Length == 0 || (data.Length & 1) != 0) { Fail("invalid-payload-length"); return; }
        for (int i = 0; i < data.Length; ++i)
            if (!float.IsFinite(data[i]) || !float.IsFinite(data[i] * 32768f)) { Fail("non-finite-payload"); return; }
        // Hold the head until another packet establishes its clock. A large
        // forward jump alone cannot prove which packet is stale: require a
        // third, contiguous timestamp and counter before discarding the head.
        if (!_startupSettled && _leading == null && !h.HasRadioClock) _startupSettled = true;
        if (!_startupSettled)
        {
            if (_leading == null) { _leading = block with { Samples = block.Samples.ToArray() }; return; }
            var first = _leading.Header;
            double radioJump = h.SecondsSince(first);
            double arrivalGap = (h.ArrivalTicks - first.ArrivalTicks) / (double)Stopwatch.Frequency;
            // A burst can deliver two genuine packets milliseconds apart even
            // though their radio clocks are one full packet apart. Suspect the
            // head only when its successor is NOT contiguous by radio time and
            // the unexplained jump is much larger than the arrival gap.
            bool contiguousClock = Math.Abs(radioJump * _rate - _leading.Samples.Length / 2) < .75;
            bool suspect = first.HasRadioClock && h.HasRadioClock && first.IntegerKind == h.IntegerKind &&
                !contiguousClock && radioJump > arrivalGap + .05 && arrivalGap is >= 0 and < 1 &&
                block.DroppedPackets == 0 && _leading.DroppedPackets == 0;
            if (_following == null && suspect) { _following = block with { Samples = block.Samples.ToArray() }; return; }
            _startupSettled = true;
            if (_following != null)
            {
                var second = _following.Header;
                bool continuous = h.HasRadioClock && h.IntegerKind == second.IntegerKind &&
                    h.Count == (second.Count + 1) % 16 && block.DroppedPackets == 0 &&
                    Math.Abs(h.SecondsSince(second) * _rate - _following.Samples.Length / 2) < .75 &&
                    (h.ArrivalTicks - second.ArrivalTicks) / (double)Stopwatch.Frequency is >= 0 and < 1;
                if (!continuous) { Fail("timestamp-discontinuity"); return; }
                Header.Event("startup-discard", new { Reason = "isolated-stale-leading-clock; following two packets contiguous",
                    Header = first, Pairs = _leading.Samples.Length / 2, Following = second, Confirming = h });
                _startupDiscards++;
                WriteBlock(_following);
            }
            else WriteBlock(_leading);
            _leading = _following = null;
        }
        if (!_failed) WriteBlock(block);
    }
    private void WriteBlock(IqBlock block)
    {
        var h = block.Header;
        ReadOnlySpan<float> data = block.Samples.Span;
        var gap = _ledger.Observe(h, data.Length / 2, block.DroppedPairs);
        if (!gap.Exact || gap.FillPairs != 0 || gap.Discard)
            Header.Event("gap", new { AtPair = _pairs, Header = h, gap.FillPairs, gap.Discard, gap.Exact, gap.Basis });
        if (gap.Exact && !gap.Discard && gap.Basis == "radio-timestamp" && gap.FillPairs == 4 &&
            _pairs == 128 && _lastPacketPairs == 128 && _gaps == 0 &&
            block.DroppedPackets == 0 && block.DroppedPairs == 0)
        {
            _startupSignatureDetected = true;
            Header.Event("startup-signature-gap", new { AtPair = _pairs, gap.FillPairs, gap.Exact, gap.Basis });
            Header.Write("startup-signature", "detected");
        }
        if (gap.Discard)
        {
            _discards++;
            if (gap.Basis.EndsWith("discontinuity")) Fail(gap.Basis);
            return;
        }
        if (_first == null)
        {
            _first = h;
            Header.Event("first-packet", h);
            Header.Write("rate-echoed", EchoedRate);
            Header.Write("gap-clock", h.HasRadioClock ? "radio-timestamp" : "counter-and-arrival-estimate");
        }
        if (gap.FillPairs > 0)
        {
            byte[] zero = new byte[8192];
            long remaining = gap.FillPairs * 8;
            while (remaining > 0) { int n = (int)Math.Min(zero.Length, remaining); _iq.Write(zero, 0, n); remaining -= n; }
            _gaps++; _fill += gap.FillPairs; _pairs += gap.FillPairs;
            // A classifier window is never permitted to straddle missing data.
            if (!_ready.Task.IsCompleted) _classifierCount = 0;
        }
        var raw = new float[data.Length];
        for (int i = 0; i < raw.Length; ++i) raw[i] = data[i] * 32768f;
        var bytes = new byte[raw.Length * sizeof(float)];
        Buffer.BlockCopy(raw, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
        _iq.Write(bytes);
        double observedStep = double.PositiveInfinity;
        foreach (float value in raw) if (value != 0) observedStep = Math.Min(observedStep, Math.Abs(value));
        if (!double.IsFinite(observedStep)) observedStep = 0;
        double grid = observedStep;
        foreach (float value in raw)
            if (grid <= 0 || Math.Abs(value / grid - Math.Round(value / grid)) > 1e-6) { grid = 0; break; }
        Header.Event("payload-block", new { AtPair = _pairs, Floats = raw.Length, ObservedStep = observedStep, GridStep = grid });
        _pairs += raw.Length / 2;
        if (_last is { } previous)
            _maxArrivalJitter = Math.Max(_maxArrivalJitter, Math.Abs((h.ArrivalTicks - previous.ArrivalTicks) /
                (double)Stopwatch.Frequency - _lastPacketPairs / (double)_rate));
        _last = h;
        _overruns = block.DroppedPackets;
        if (!_ready.Task.IsCompleted)
        {
            if (_classifierCount == 0) _classifierStart = _pairs - raw.Length / 2;
            int n = Math.Min(raw.Length, _classifier.Length - _classifierCount);
            Array.Copy(raw, 0, _classifier, _classifierCount, n);
            _classifierCount += n;
            if (_classifierCount == _classifier.Length)
            {
                var verdict = IqClassifier.Classify(_classifier);
                Header.Write("classifier", verdict.Verdict);
                Header.Event("classifier-details", verdict);
                Header.Write("quantization-start-pair", _classifierStart);
                Header.Write("quantization-floats", _classifierCount);
                // The observed minimum is not a promise about future signal
                // levels. Pin only the actual classifier window's grid.
                double step = verdict.ObservedStep;
                foreach (float v in _classifier)
                    if (step <= 0 || Math.Abs(v / step - Math.Round(v / step)) > 1e-6) { step = 0; break; }
                Header.Write("quantization-step", step);
                if (verdict.Verdict != "noise-like") Fail(verdict.Verdict);
                else _ready.TrySetResult(true);
            }
        }
        _accountedDrops = block.DroppedPairs;
        _lastPacketPairs = data.Length / 2;
    }
    private void Fail(string reason)
    {
        _failed = true; Failure = reason; _ready.TrySetResult(false);
        _failure.TrySetResult(reason);
        try { Header.Write("failure", reason); } catch { }
    }
    public void Mark(string source, bool keyed)
    {
        long ticks = Stopwatch.GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        lock (_gate)
            if (!_finished) Header.Event("keying", new { Source = source, Keyed = keyed,
                AtPair = _pairs, ArrivalClockPair = _first is { } first
                    ? Math.Max(0, (long)Math.Round((ticks - first.ArrivalTicks) * (double)_rate / Stopwatch.Frequency)) : 0,
                Ticks = ticks, Utc = utc });
    }
    public void MarkTone(bool engaged, double frequency)
    { lock (_gate) if (!_finished) Header.Event("tone-at-keying", new { Engaged = engaged, Frequency = frequency }); }
    public void Finish(string reason, long droppedPackets = 0, long droppedPairs = 0, long subscriberFailures = 0)
    {
        lock (_gate)
        {
            if (_finished) return;
            // A single packet carries no contradictory clock evidence. Preserve
            // it on stop; a suspected discontinuity without confirmation fails.
            if (!_startupSettled && !_failed)
            {
                if (_following != null) Fail("timestamp-discontinuity");
                else if (_leading != null) WriteBlock(_leading);
                _leading = _following = null;
            }
            _finished = true;
            _ready.TrySetResult(false);
            try
            {
                long tail = _first != null ? Math.Max(0, droppedPairs - _accountedDrops) : 0;
                if (tail > 0)
                {
                    Header.Event("gap", new { AtPair = _pairs, FillPairs = tail, Exact = true, Basis = "terminal-application-overrun" });
                    byte[] zeros = new byte[8192];
                    long bytes = checked(tail * 8);
                    while (bytes > 0) { int n = (int)Math.Min(bytes, zeros.Length); _iq.Write(zeros, 0, n); bytes -= n; }
                    _pairs += tail; _fill += tail; _gaps++;
                }
                try { _iq.Flush(true); }
                finally { _iq.Dispose(); }
                Header.Write("end-reason", Failure ?? reason);
                if (_last is { } last) Header.Event("last-packet", last);
                double seconds = _first is { } first && _last is { } end
                    ? (end.ArrivalTicks - first.ArrivalTicks) / (double)Stopwatch.Frequency + (_lastPacketPairs + tail) / (double)_rate : 0;
                Header.Write("arrival-span-seconds", seconds);
                Header.Write("maximum-arrival-jitter-seconds", _maxArrivalJitter);
                Header.Write("pairs-expected-arrival-span", seconds * _rate);
                Header.Write("pairs-written", _pairs);
                Header.Write("pairs-per-arrival-span", seconds > 0 ? _pairs / (seconds * _rate) : 0);
                Header.Write("gaps", _gaps);
                Header.Write("fill-pairs", _fill);
                Header.Write("discarded-packets", _discards);
                Header.Write("startup-discarded-packets", _startupDiscards);
                Header.Write("overrun-packets", Math.Max(_overruns, droppedPackets));
                Header.Write("overrun-pairs", droppedPairs);
                Header.Write("subscriber-failures", subscriberFailures);
                Header.Write("sha256", TakeHeader.Checksum(Path.Combine(DirectoryPath, "rf.iq")));
                bool clean = IsReady && !_failed && _gaps == (_startupSignatureDetected ? 1 : 0) && _discards == 0 &&
                    droppedPackets == 0 && _overruns == 0 && subscriberFailures == 0 && reason == "operator-stop";
                Header.Write("closed", clean ? "clean" : "incomplete");
            }
            finally { try { _iq.Dispose(); } finally { Header.Dispose(); } }
        }
    }
    public void Dispose() => Finish("disposed");
}
