using System;

namespace JJPortaudio
{
    /// <summary>
    /// What the capture thread knew about one block as it handed it over
    /// (Sprint 49 Track Q3 repair 2, Codex's re-check of ca39306c, C1b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The clock is read BEFORE the backlog is asked (repair 3, Codex's
    /// re-check of b6543ac1, C1b).</b> <see cref="Arrival"/> is stamped right
    /// after the read returned and before the backlog is asked;
    /// <see cref="BacklogAnsweredAt"/> right after the answer. Repair 2 asked
    /// first and stamped after, so a thread paused between the two (Codex: one
    /// 500 ms pause) paired a truthful small backlog with a late clock reading,
    /// claimed a catch-up that never happened, and began the file at frame 0
    /// with all of the tone in it.
    /// </para>
    /// <para>
    /// <b>The direction, derived.</b> A block's bound on when frame 0 was
    /// captured is <c>Arrival - (framesThroughIt + backlog) / rate - latency</c>.
    /// Every frame that had reached the host buffer by <see cref="Arrival"/> is
    /// either delivered (through it) or still waiting, and still waiting when
    /// the backlog is asked, since nothing reads in between; so the count can
    /// only be too LARGE, by frames that arrived after the stamp. Too large a
    /// count puts frame 0 earlier than the truth, so every later moment maps to
    /// a LATER frame: the file can only begin later. A pause anywhere in the
    /// sequence is safe: before the stamp, the frames it let in are counted in
    /// the backlog, so the bound stays tight; between the stamp and the answer,
    /// they make the count too large, so the file begins later. A pause after
    /// the answer touches neither reading.
    /// </para>
    /// <para>
    /// <b>The gap between the two readings is bounded.</b> The pair counts
    /// toward establishing the timing only when the answer came within the
    /// block's own duration of the stamp. Otherwise the backlog is not paired
    /// with the stamp at all: the block's bound uses only the frames delivered
    /// through it, which is a true bound by itself, and it does not establish.
    /// With no answer time (a source that cannot say), the backlog is still
    /// taken off, which is safe in either order, but the block never
    /// establishes, so the stream-start fallback places the file.
    /// </para>
    /// <para>
    /// <see cref="StreamStartedAt"/> and <see cref="StreamFramesBefore"/> give a
    /// LOWER bound on when a frame was captured: no frame of a stream can be
    /// captured before the stream was asked to start, and frames that went
    /// before it in the stream took their own time to capture. Frames lost to an
    /// overflow only make the true moment later, so the bound still holds.
    /// </para>
    /// </remarks>
    internal readonly struct CaptureBlockTiming
    {
        /// <param name="arrival">When the block reached the recorder, read BEFORE the backlog was asked, on the clock <see cref="CaptureFileStart"/> was given.</param>
        /// <param name="backlogFrames">Frames still waiting to be read when the backlog was asked; -1 when the device would not say.</param>
        /// <param name="overflowed">True when the device reported input lost before this block.</param>
        /// <param name="streamStartedAt">When the stream was asked to start, on the same clock; null when unknown.</param>
        /// <param name="streamFramesBefore">Frames the stream delivered before this block, whether or not the recorder was listening.</param>
        /// <param name="backlogAnsweredAt">When the backlog's answer came, read right after it on the same clock; null when not known.</param>
        internal CaptureBlockTiming(long arrival, long backlogFrames, bool overflowed = false,
            long? streamStartedAt = null, long streamFramesBefore = 0, long? backlogAnsweredAt = null)
        {
            Arrival = arrival;
            BacklogAnsweredAt = backlogAnsweredAt;
            BacklogFrames = backlogFrames;
            Overflowed = overflowed;
            StreamStartedAt = streamStartedAt;
            StreamFramesBefore = Math.Max(0, streamFramesBefore);
        }

        internal long Arrival { get; }
        internal long BacklogFrames { get; }
        internal bool Overflowed { get; }
        internal long? StreamStartedAt { get; }
        internal long StreamFramesBefore { get; }
        internal long? BacklogAnsweredAt { get; }
    }

    /// <summary>
    /// Where a recording's FILE begins, when that is not the first frame the
    /// microphone delivers (Sprint 49 Track Q3 repair 1, Codex's check of
    /// 83932446, C1): a Check your audio take opens the microphone, then sounds
    /// its start tone, and the file must begin once that tone has really ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is a time and not a length.</b> Track Q3 dropped a fixed 450 ms
    /// of captured frames. The tone is about 160 ms long and reaches the
    /// speaker after whatever the alert output holds ahead of it, from about
    /// 60 ms to 800 ms by the operator's own setting, so a fixed cut either
    /// threw away the operator's first words (low latency) or kept the tone
    /// (high latency). The caller now says WHEN the tone ended, on the
    /// <see cref="System.Diagnostics.Stopwatch"/> clock, and this class finds the
    /// frame the microphone captured at that instant.
    /// </para>
    /// <para>
    /// <b>Two bounds on when frame 0 was captured.</b> A block cannot arrive
    /// before its newest frame was captured plus the input latency the device
    /// reports, so <c>arrival - (framesThroughIt + backlog) / rate - latency</c>
    /// is an UPPER bound; the smallest seen is the precise reading. The stream's
    /// start is a LOWER bound (<see cref="CaptureBlockTiming"/>). Placing the
    /// file by an upper bound puts its start EARLIER than the truth by however
    /// loose that bound is, which keeps the tone; placing it by the lower bound
    /// puts it later, which costs a little of what follows.
    /// </para>
    /// <para>
    /// <b>The precise reading is used only once it is ESTABLISHED (repair 2,
    /// Codex's re-check of ca39306c, C1b).</b> Repair 1 trusted the smallest
    /// upper bound as soon as the moment had been captured, so a reader running
    /// behind through the whole lead froze the file's start inside the tone
    /// (Codex measured 2,137 tone samples kept) before a tighter block could
    /// arrive. Now the reading counts as established only after a block arrives
    /// with a backlog the device itself reported as smaller than that block,
    /// answered within that block's duration of the block's clock reading
    /// (repair 3, <see cref="CaptureBlockTiming"/>), and no input lost: such a
    /// block was waited for, so its bound is tight. Every
    /// known backlog is also taken off its block's bound, which only ever makes
    /// the file start later. If no such block comes within
    /// <see cref="EstablishWithinSeconds"/> of capture past the moment, or the
    /// recording stops first, the file begins by the LOWER bound: later, never
    /// earlier. With no lower bound either, the file does not begin at all.
    /// </para>
    /// <para>
    /// <b>Until the moment is known, frames are held</b>, not written: the tone
    /// is placed on the output after the microphone opens, and its end is known
    /// only once the output has taken it. A recording stopped before its file
    /// began writes nothing, and says so through <see cref="FileBegan"/>.
    /// Held frames are bounded by <see cref="MaxHeldSeconds"/>; past that the
    /// oldest go, since they are before any start a tone could have.
    /// </para>
    /// <para>
    /// Thread-safe: blocks arrive on the capture thread, the moment from
    /// whatever thread learnt it.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFileStart
    {
        /// <summary>The most a recording holds while it waits to be told where its file begins.</summary>
        internal const double MaxHeldSeconds = 10;

        /// <summary>
        /// How much capture past the moment (placed by the lower bound) is
        /// waited for a block that establishes the precise reading, before the
        /// file begins by the lower bound instead (repair 2).
        /// </summary>
        internal const double EstablishWithinSeconds = 1.0;

        private readonly object _sync = new object();
        private readonly int _rate;
        private readonly long _frequency;
        private readonly long _inputLatencyTicks;
        private readonly long _maxHeldFrames;

        private long _captured;
        private long _anchor = long.MaxValue;
        private bool _established;
        private long? _lowerFrame0;
        private bool _firstBlockSeen;
        private bool _holding;
        private bool _beginAtFirstFrame;
        private long? _beginAt;
        private float[] _held = Array.Empty<float>();
        private int _heldFloats;
        private long _heldFrom;
        private int _perFrame = 1;
        private long _boundary = -1;
        private long _dropped;
        private string _how = "";

        /// <param name="rate">The device's sample rate.</param>
        /// <param name="hold">True to hold every frame until <see cref="BeginAt"/>; false writes from the first frame.</param>
        /// <param name="inputLatencySeconds">The input latency the device reports; zero or less when it reports none.</param>
        /// <param name="frequency">Ticks per second of the clock the arrival times and <see cref="BeginAt"/> use.</param>
        internal CaptureFileStart(int rate, bool hold, double inputLatencySeconds, long frequency)
        {
            if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
            if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
            _rate = rate;
            _frequency = frequency;
            _holding = hold;
            _boundary = hold ? -1 : 0;
            double latency = double.IsNaN(inputLatencySeconds) || inputLatencySeconds < 0 ? 0 : Math.Min(inputLatencySeconds, 2.0);
            _inputLatencyTicks = (long)Math.Round(latency * frequency);
            _maxHeldFrames = (long)(MaxHeldSeconds * rate);
        }

        /// <summary>Frames the device has delivered, written or not.</summary>
        internal long CapturedFrames { get { lock (_sync) return _captured; } }

        /// <summary>True once the file has begun: always, without a hold; with one, once the moment is known and captured.</summary>
        internal bool FileBegan { get { lock (_sync) return !_holding; } }

        /// <summary>The capture frame the file began at, or -1 while it has not.</summary>
        internal long BoundaryFrame { get { lock (_sync) return _boundary; } }

        /// <summary>Frames held beyond <see cref="MaxHeldSeconds"/> and let go before the file began.</summary>
        internal long DroppedFrames { get { lock (_sync) return _dropped; } }

        /// <summary>True once a block has established the precise reading of when frame 0 was captured (repair 2).</summary>
        internal bool TimingEstablished { get { lock (_sync) return _established; } }

        /// <summary>How the file's start was placed, for the trace; empty while it has not begun.</summary>
        internal string HowPlaced { get { lock (_sync) return _how; } }

        /// <summary>
        /// Say where the file begins: the frame the microphone captured at
        /// <paramref name="timestamp"/>, or, when null, the first frame of the
        /// capture (no tone sounded). Applied as the next block arrives, or by
        /// <see cref="Finish"/>; only the first call counts.
        /// </summary>
        internal void BeginAt(long? timestamp)
        {
            lock (_sync)
            {
                if (!_holding || _beginAtFirstFrame || _beginAt != null) return;
                if (timestamp is long t) _beginAt = t; else _beginAtFirstFrame = true;
            }
        }

        /// <summary>
        /// True when a recording stopped at <paramref name="stoppedAt"/> was
        /// stopped during its lead (repair 2, Codex's re-check, item 3): its file
        /// was held for a start that had not yet come, either because the tone's
        /// end was not yet known or because the stop came before it. Such a stop
        /// is the operator's, whether or not the microphone had delivered its
        /// first block yet; it says nothing about the microphone.
        /// </summary>
        internal bool StoppedDuringLead(long stoppedAt)
        {
            lock (_sync)
            {
                if (!_holding || _beginAtFirstFrame) return false;
                return _beginAt is not long t || stoppedAt < t;
            }
        }

        /// <summary>
        /// One block from the device. Writes what of it is the file's; returns
        /// true when this block is the one that began the file.
        /// </summary>
        internal bool Block(WavWriter writer, float[] buffer, int count, int frames, CaptureBlockTiming timing)
        {
            if (frames <= 0 || count <= 0) return false;
            lock (_sync)
            {
                if (!_firstBlockSeen)
                {
                    _firstBlockSeen = true;
                    if (timing.StreamStartedAt is long started)
                        _lowerFrame0 = started + FramesToTicks(timing.StreamFramesBefore);
                }
                long after = _captured + frames;
                // Repair 3: the backlog pairs with the clock reading taken before
                // it was asked, and only when its answer came within this block's
                // own duration (CaptureBlockTiming says why either way is safe).
                bool answerKnown = timing.BacklogAnsweredAt.HasValue;
                long gap = answerKnown ? timing.BacklogAnsweredAt!.Value - timing.Arrival : 0;
                bool paired = answerKnown && gap >= 0 && gap <= FramesToTicks(frames);
                long backlog = timing.BacklogFrames > 0 && (paired || !answerKnown) ? timing.BacklogFrames : 0;
                long bound = timing.Arrival - FramesToTicks(after + backlog);
                if (bound < _anchor) _anchor = bound;
                if (paired && timing.BacklogFrames >= 0 && timing.BacklogFrames < frames && !timing.Overflowed) _established = true;
                _perFrame = Math.Max(1, count / frames);
                if (!_holding)
                {
                    _captured = after;
                    writer.Write(buffer, 0, count);
                    return false;
                }
                Hold(buffer, count);
                _captured = after;
                return TryBegin(writer, stopping: false);
            }
        }

        /// <summary>The recording is stopping: write whatever of the held frames is the file's. Returns true when this began the file.</summary>
        internal bool Finish(WavWriter writer)
        {
            lock (_sync) return _holding && TryBegin(writer, stopping: true);
        }

        /// <summary>
        /// The frame the microphone captured at <paramref name="timestamp"/>, by
        /// the PRECISE reading (the smallest upper bound), established or not;
        /// -1 before any block.
        /// </summary>
        internal long FrameAt(long timestamp)
        {
            lock (_sync) return _anchor == long.MaxValue ? -1 : FrameFor(timestamp, _anchor - _inputLatencyTicks);
        }

        private long FrameFor(long timestamp, long frame0)
        {
            long ticks = timestamp - frame0;
            if (ticks <= 0) return 0;
            // Rounded UP: a frame captured after the moment is the file's first,
            // never one captured before it.
            return (long)Math.Ceiling((double)ticks * _rate / _frequency);
        }

        private bool TryBegin(WavWriter writer, bool stopping)
        {
            long boundary;
            if (_beginAtFirstFrame)
            {
                boundary = 0;
                _how = "at the first frame";
            }
            else if (_beginAt is long t)
            {
                if (_anchor == long.MaxValue) return false;
                long precise = _anchor - _inputLatencyTicks;
                if (_established)
                {
                    boundary = FrameFor(t, precise);
                    if (_captured < boundary) return false;   // not captured yet
                    _how = "by the established capture timing";
                }
                else
                {
                    // Not established: only the lower bound keeps the file from
                    // starting early. Without one, the file does not begin.
                    if (_lowerFrame0 is not long lower) return false;
                    boundary = FrameFor(t, Math.Min(lower, precise));
                    if (_captured < boundary) return false;
                    long waitFrames = (long)Math.Round(EstablishWithinSeconds * _rate);
                    if (!stopping && _captured < boundary + waitFrames) return false;   // still time for a block to establish it
                    _how = "by the stream's start, the capture timing not established";
                }
            }
            else return false;

            long first = Math.Max(boundary, _heldFrom);
            long skip = first - _heldFrom;
            int skipFloats = (int)Math.Min(_heldFloats, skip * _perFrame);
            if (_heldFloats - skipFloats > 0) writer.Write(_held, skipFloats, _heldFloats - skipFloats);
            _held = Array.Empty<float>();
            _heldFloats = 0;
            _holding = false;
            _boundary = boundary;
            return true;
        }

        private void Hold(float[] buffer, int count)
        {
            if (_heldFloats + count > _held.Length)
            {
                var grown = new float[Math.Max(_held.Length * 2, _heldFloats + count)];
                Array.Copy(_held, grown, _heldFloats);
                _held = grown;
            }
            Array.Copy(buffer, 0, _held, _heldFloats, count);
            _heldFloats += count;

            long heldFrames = _heldFloats / _perFrame;
            if (heldFrames > _maxHeldFrames)
            {
                long excess = heldFrames - _maxHeldFrames;
                int floats = (int)(excess * _perFrame);
                Array.Copy(_held, floats, _held, 0, _heldFloats - floats);
                _heldFloats -= floats;
                _heldFrom += excess;
                _dropped += excess;
            }
        }

        private long FramesToTicks(long frames) => (long)Math.Round((double)frames * _frequency / _rate);
    }
}
