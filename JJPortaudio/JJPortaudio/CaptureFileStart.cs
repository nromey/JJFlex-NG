using System;

namespace JJPortaudio
{
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
    /// <b>Mapping a moment to a frame.</b> The microphone is read in blocks. A
    /// block cannot arrive before its last frame was captured plus the input
    /// latency the device reports, so for every block,
    /// <c>arrival - framesThroughIt / rate - inputLatency</c> is an upper bound
    /// on when frame 0 was captured; the smallest seen so far is the estimate,
    /// and a block that arrived late (a backlog when the recorder's sink was
    /// installed, a busy thread) only ever makes one bound looser, never the
    /// estimate. This also accounts for the capture's own start: frame 0 is
    /// the first frame THIS recorder was handed, after the device had already
    /// started, and the estimate is of when that frame was captured.
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

        private readonly object _sync = new object();
        private readonly int _rate;
        private readonly long _frequency;
        private readonly long _inputLatencyTicks;
        private readonly long _maxHeldFrames;

        private long _captured;
        private long _anchor = long.MaxValue;
        private bool _holding;
        private bool _beginAtFirstFrame;
        private long? _beginAt;
        private float[] _held = Array.Empty<float>();
        private int _heldFloats;
        private long _heldFrom;
        private int _perFrame = 1;
        private long _boundary = -1;
        private long _dropped;

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
        /// One block from the device, which arrived at <paramref name="arrival"/>
        /// on the same clock. Writes what of it is the file's; returns true when
        /// this block is the one that began the file.
        /// </summary>
        internal bool Block(WavWriter writer, float[] buffer, int count, int frames, long arrival)
        {
            if (frames <= 0 || count <= 0) return false;
            lock (_sync)
            {
                long after = _captured + frames;
                long bound = arrival - FramesToTicks(after);
                if (bound < _anchor) _anchor = bound;
                _perFrame = Math.Max(1, count / frames);
                if (!_holding)
                {
                    _captured = after;
                    writer.Write(buffer, 0, count);
                    return false;
                }
                Hold(buffer, count);
                _captured = after;
                return TryBegin(writer);
            }
        }

        /// <summary>The recording is stopping: write whatever of the held frames is the file's. Returns true when this began the file.</summary>
        internal bool Finish(WavWriter writer)
        {
            lock (_sync) return _holding && TryBegin(writer);
        }

        /// <summary>The frame the microphone captured at <paramref name="timestamp"/>, from the blocks seen so far; -1 before any.</summary>
        internal long FrameAt(long timestamp)
        {
            lock (_sync) return FrameAtLocked(timestamp);
        }

        private long FrameAtLocked(long timestamp)
        {
            if (_anchor == long.MaxValue) return -1;
            long frame0 = _anchor - _inputLatencyTicks;
            long ticks = timestamp - frame0;
            if (ticks <= 0) return 0;
            // Rounded UP: a frame captured after the moment is the file's first,
            // never one captured before it.
            return (long)Math.Ceiling((double)ticks * _rate / _frequency);
        }

        private bool TryBegin(WavWriter writer)
        {
            long boundary;
            if (_beginAtFirstFrame) boundary = 0;
            else if (_beginAt is long t)
            {
                boundary = FrameAtLocked(t);
                if (boundary < 0 || _captured < boundary) return false;   // not captured yet
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
