using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Radios
{
    /// <summary>
    /// One raw PlayOn edge the radio reported for one of our slices, or the
    /// slice's retirement (Unified takes, design section 5). Raised by
    /// <see cref="FlexBase.SlicePlaybackObserved"/>.
    /// </summary>
    /// <param name="ArrivalTicks">When the report arrived here, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="Ordinal">Rising across every observation of this radio object.</param>
    /// <param name="Cause"><c>radio-status</c> for a reported edge, <c>slice-retired</c> when the slice went away.</param>
    public sealed record SlicePlaybackObservation(int SliceIndex, string SliceLetter, bool Playing,
        long ArrivalTicks, long Ordinal, string Cause, string ConnectionId);

    /// <summary>
    /// One received audio packet as the recorder's raw tap saw it, BEFORE the
    /// computer's mute, gain and noise reduction: the radio's full fractional
    /// timestamp, its sequence counter, and its monotonic arrival time.
    /// Immutable; every sink shares one copy.
    /// </summary>
    public sealed record ReceivedAudioPacket(byte[] Packet, int Sequence, double RadioTimestamp,
        long ArrivalTicks, bool LocallyMuted);
}

namespace Radios.Listenback
{
    /// <summary>The capture token's states (design section 5).</summary>
    public enum PlaybackCaptureState { Reserved, Armed, Capturing, Draining, Closed }

    /// <summary>
    /// A playback take's capture: from the radio's REPORTED start of playback
    /// to its reported stop, never a timer (the register's ask, design sections 4 and 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One queue, in arrival order.</b> Audio packets and PlayOn edges go
    /// through the same bounded channel, so the worker sees them in the order
    /// they arrived and a backlog cannot move a boundary. Nothing here runs on
    /// the receive thread except the copy into that channel.
    /// </para>
    /// <para>
    /// <b>The boundary is unverified.</b> Status and audio travel by different
    /// paths and nothing proves the last playback sample has arrived when
    /// PlayOn=false does, on LAN or SmartLink. The header says so
    /// (<c>boundary-basis=status-arrival</c>, <c>boundary-quality=unverified</c>)
    /// and no tail delay is invented. The edges, the first and last packets'
    /// radio timestamps and arrival ticks, and the decoded extent are written
    /// to the header and the trace so the bench measurement can settle it.
    /// </para>
    /// <para>
    /// <b>Fail-safes, not durations.</b> No raw start within
    /// <see cref="StartDeadline"/> of arming closes the take as
    /// <c>start-not-observed</c>; no stop within <see cref="SafetyCap"/> of the
    /// start (the radio's 120-second buffer plus five) closes it
    /// <c>safety-cap</c>. Both are incomplete takes, never clean ones.
    /// </para>
    /// <para>
    /// <b>Never sends a command and never touches the radio.</b> Stopping the
    /// radio's playback is the check's business.
    /// </para>
    /// </remarks>
    public sealed class PlaybackTakeCapture : IDisposable
    {
        public static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan SafetyCap = TimeSpan.FromSeconds(125);
        /// <summary>A forward jump in radio time longer than this is a discontinuity, not a gap to fill.</summary>
        public const double LongestFillSeconds = 5;

        private abstract record Item;
        private sealed record PacketItem(ReceivedAudioPacket Packet) : Item;
        private sealed record EdgeItem(SlicePlaybackObservation Edge) : Item;
        private sealed record EndItem(string Reason) : Item;

        private readonly AudioTakeWriter _writer;
        private readonly int _sliceIndex;
        private readonly Func<byte[], float[]> _decodeStereo;
        private readonly Func<long> _clock;
        private readonly TimeSpan _startDeadline, _safetyCap;
        private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(2000)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
        private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)PlaybackCaptureState.Reserved;
        private int _overflowed;
        private long _armedTicks, _startedTicks;
        private double? _lastTimestamp;
        private long _lastFrames, _gapSamples, _preStartPackets, _discards;
        private bool _sawStop, _sawLocalMute, _discontinuity;
        private ReceivedAudioPacket _firstPacket, _lastPacket;
        private Task _worker;

        public PlaybackTakeCapture(AudioTakeWriter writer, int sliceIndex, Func<byte[], float[]> decodeStereo,
            Func<long> clock = null, TimeSpan? startDeadline = null, TimeSpan? safetyCap = null)
        {
            _writer = writer; _sliceIndex = sliceIndex; _decodeStereo = decodeStereo;
            _clock = clock ?? Stopwatch.GetTimestamp;
            _startDeadline = startDeadline ?? StartDeadline;
            _safetyCap = safetyCap ?? SafetyCap;
        }

        public PlaybackCaptureState State => (PlaybackCaptureState)Volatile.Read(ref _state);
        public string DirectoryPath => _writer.DirectoryPath;
        /// <summary>Completes when the take has closed: true when it closed clean.</summary>
        public Task<bool> Closed => _closed.Task;

        /// <summary>
        /// Arm: from now the capture listens for its slice's raw start. Call
        /// BEFORE asking the radio to play, so a short playback cannot report
        /// both edges before anyone is listening.
        /// </summary>
        public void Arm()
        {
            if (Interlocked.CompareExchange(ref _state, (int)PlaybackCaptureState.Armed, (int)PlaybackCaptureState.Reserved)
                != (int)PlaybackCaptureState.Reserved) return;
            _armedTicks = _clock();
            _writer.Note("armed-ticks", _armedTicks);
            _worker = Task.Run(WorkAsync);
        }

        /// <summary>From the receive thread: copy only. Never blocks; an overflow closes this take alone.</summary>
        public void Offer(ReceivedAudioPacket packet)
        {
            if (State is PlaybackCaptureState.Closed or PlaybackCaptureState.Reserved) return;
            if (!_queue.Writer.TryWrite(new PacketItem(packet))) Interlocked.Exchange(ref _overflowed, 1);
        }

        /// <summary>A raw playback edge from the radio.</summary>
        public void Observe(SlicePlaybackObservation edge)
        {
            if (edge.SliceIndex != _sliceIndex || State is PlaybackCaptureState.Closed or PlaybackCaptureState.Reserved) return;
            if (!_queue.Writer.TryWrite(new EdgeItem(edge))) Interlocked.Exchange(ref _overflowed, 1);
        }

        /// <summary>Receive audio ended (disconnect, PC audio stopped): the take closes incomplete.</summary>
        public void ReceiveEnded() => End("receive-ended");

        /// <summary>End the capture for a stated reason (cancel, window closed, radio changed). Idempotent.</summary>
        public void End(string reason)
        {
            if (State == PlaybackCaptureState.Reserved)
            {
                if (Interlocked.CompareExchange(ref _state, (int)PlaybackCaptureState.Closed, (int)PlaybackCaptureState.Reserved)
                    == (int)PlaybackCaptureState.Reserved)
                    _closed.TrySetResult(_writer.Close(false, reason));
                return;
            }
            // Overflow cannot swallow an end: the worker also polls for a request.
            Volatile.Write(ref _endRequest, reason);
            _queue.Writer.TryWrite(new EndItem(reason));
        }
        private string _endRequest;

        public void Dispose() => End("disposed");

        private async Task WorkAsync()
        {
            string reason = null;
            bool clean = false;
            try
            {
                while (reason == null)
                {
                    if (Volatile.Read(ref _overflowed) != 0) { reason = "queue-overflow"; break; }
                    if (Volatile.Read(ref _endRequest) is { } requested && !_queue.Reader.TryPeek(out _)) { reason = requested; break; }
                    long now = _clock();
                    if (State == PlaybackCaptureState.Armed && Elapsed(_armedTicks, now) > _startDeadline)
                    { reason = "start-not-observed"; break; }
                    if (State == PlaybackCaptureState.Capturing && Elapsed(_startedTicks, now) > _safetyCap)
                    { reason = "safety-cap"; break; }
                    if (!_queue.Reader.TryRead(out var item))
                    {
                        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                        try { await _queue.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        continue;
                    }
                    switch (item)
                    {
                        case EdgeItem { Edge: var edge }:
                            reason = Edge(edge);
                            break;
                        case PacketItem { Packet: var packet }:
                            reason = Packet(packet);
                            break;
                        case EndItem { Reason: var ended }:
                            reason = ended;
                            break;
                    }
                }
                clean = reason == "observed-stop" && !_discontinuity && _gapSamples == 0 && Volatile.Read(ref _overflowed) == 0;
            }
            catch (Exception ex)
            {
                reason = "writer-" + ex.GetType().Name;
                JJTrace.Tracing.TraceLine("PlaybackTakeCapture: " + ex, TraceLevel.Error);
            }
            Interlocked.Exchange(ref _state, (int)PlaybackCaptureState.Draining);
            _queue.Writer.TryComplete();
            bool result = false;
            try
            {
                _writer.NoteEvent("capture-summary", new
                {
                    EndReason = reason, PreStartPacketsDiscarded = _preStartPackets, StalePacketsDiscarded = _discards,
                    GapSamples = _gapSamples, LocalMuteObserved = _sawLocalMute, StopObserved = _sawStop,
                    FirstPacket = _firstPacket == null ? null : new { _firstPacket.RadioTimestamp, _firstPacket.ArrivalTicks, _firstPacket.Sequence },
                    LastPacket = _lastPacket == null ? null : new { _lastPacket.RadioTimestamp, _lastPacket.ArrivalTicks, _lastPacket.Sequence },
                    DecodedSamples = _writer.SamplesWritten, TickFrequency = Stopwatch.Frequency,
                });
                string firstTs = _firstPacket?.RadioTimestamp.ToString("R", CultureInfo.InvariantCulture) ?? "none";
                string lastTs = _lastPacket?.RadioTimestamp.ToString("R", CultureInfo.InvariantCulture) ?? "none";
                JJTrace.Tracing.TraceLine(string.Create(CultureInfo.InvariantCulture,
                    $"PlaybackTakeCapture: closing {System.IO.Path.GetFileName(_writer.DirectoryPath)} reason={reason} firstTs={firstTs} firstArrival={_firstPacket?.ArrivalTicks} lastTs={lastTs} lastArrival={_lastPacket?.ArrivalTicks} decodedSamples={_writer.SamplesWritten} gapSamples={_gapSamples} preStart={_preStartPackets}"),
                    TraceLevel.Info);
                result = _writer.Close(clean, reason ?? "unknown");
            }
            catch (Exception ex) { JJTrace.Tracing.TraceLine("PlaybackTakeCapture close: " + ex, TraceLevel.Error); }
            finally
            {
                Interlocked.Exchange(ref _state, (int)PlaybackCaptureState.Closed);
                _closed.TrySetResult(result);
            }
        }

        private static TimeSpan Elapsed(long from, long to) => TimeSpan.FromSeconds((to - from) / (double)Stopwatch.Frequency);

        private string Edge(SlicePlaybackObservation edge)
        {
            var state = State;
            _writer.NoteEvent(edge.Playing ? "playback-start" : "playback-stop", edge);
            if (edge.Cause == "slice-retired") return "slice-retired";
            if (edge.Playing && state == PlaybackCaptureState.Armed)
            {
                _startedTicks = edge.ArrivalTicks;
                Interlocked.Exchange(ref _state, (int)PlaybackCaptureState.Capturing);
                _writer.BeginPlayback();
                return null;
            }
            if (!edge.Playing && state == PlaybackCaptureState.Capturing)
            {
                _sawStop = true;
                _writer.EndPlayback();
                return "observed-stop";
            }
            return null;
        }

        private string Packet(ReceivedAudioPacket packet)
        {
            if (State != PlaybackCaptureState.Capturing) { _preStartPackets++; return null; }
            // A packet that arrived after the start edge but carries a radio
            // time before the previous one is stale or a duplicate.
            if (_lastTimestamp is { } last)
            {
                double expected = last + _lastFrames / (double)_writer.Rate;
                double jump = packet.RadioTimestamp - expected;
                if (jump < -0.5 / 100) { _discards++; return null; }
                if (jump > LongestFillSeconds) { _discontinuity = true; return "clock-discontinuity"; }
                long missing = (long)Math.Round(jump * _writer.Rate);
                if (missing > _writer.Rate / 200)
                {
                    _writer.Gap(missing, "radio-timestamp");
                    _gapSamples += missing;
                }
            }
            if (packet.LocallyMuted && !_sawLocalMute)
            {
                // The raw packet still exists at this tap; record it, and say
                // the computer's own output was muted (design section 4).
                _sawLocalMute = true;
                _writer.Note("local-mute-observed", "True");
            }
            float[] stereo = _decodeStereo(packet.Packet) ?? Array.Empty<float>();
            var mono = new float[stereo.Length / 2];
            for (int i = 0; i < mono.Length; i++) mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * .5f;
            _writer.Write(mono, mono.Length);
            _firstPacket ??= packet;
            _lastPacket = packet;
            _lastTimestamp = packet.RadioTimestamp;
            _lastFrames = mono.Length;
            return null;
        }
    }
}
