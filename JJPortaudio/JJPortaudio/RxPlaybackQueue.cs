using System;
using System.Diagnostics;
using JJTrace;

namespace JJPortaudio
{
    /// <summary>
    /// What one output callback should do, decided before it touches the queue.
    /// </summary>
    public readonly struct RxCallbackPlan
    {
        /// <summary>Queued buffers to discard before playing (the ratchet trim).</summary>
        public readonly int Discard;

        /// <summary>
        /// True when the callback should output a whole buffer of silence and
        /// consume nothing, because the reserve is still being built.
        /// </summary>
        public readonly bool HoldForPrime;

        public RxCallbackPlan(int discard, bool holdForPrime)
        {
            Discard = discard;
            HoldForPrime = holdForPrime;
        }
    }

    /// <summary>
    /// The playback queue's policy and its meters, separated from the PortAudio
    /// callback so the arithmetic can be driven by a test instead of by a radio
    /// (#473).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The queue between the receive poll loop and the output callback IS
    /// the receive jitter buffer</b>, and until Sprint 43 nothing reported its
    /// depth. Sprint 43 Track J added the depth reading; this is the policy
    /// that reading argued for.
    /// </para>
    /// <para>
    /// <b>What the callback can and cannot do, established by reading it.</b>
    /// PortAudio hands the callback an output buffer sized for a fixed
    /// <c>frameCount</c>, so a callback physically cannot write more than one
    /// device buffer's worth however far behind it is. The loop in
    /// <c>outputCallback</c> already drains as many queued packets as fit — the
    /// "one buffer per call" cap is not a policy anybody wrote, it is the shape
    /// of the callback contract, and no comment, commit message or register
    /// entry in this tree gives any other reason. <b>So a backlog cannot be
    /// consumed away; it can only be discarded.</b> That is what
    /// <see cref="RxCallbackPlan.Discard"/> is for, and it is the only thing
    /// that turns the ratchet back into a queue.
    /// </para>
    /// <para>
    /// <b>Priming, and re-priming.</b> With no reserve the queue settles at
    /// exactly the depth one callback consumes: at that point every callback
    /// takes everything there is, so any late packet starves and the operator
    /// hears a gap. Priming builds a reserve before playback starts. Priming
    /// ONCE would not be enough — a starvation spends the reserve and nothing
    /// rebuilds it — so a starvation re-enters the priming state. The silence
    /// that costs is silence the operator was already getting; what it buys is
    /// not starving again on the next packet.
    /// </para>
    /// </remarks>
    public sealed class RxPlaybackQueue
    {
        /// <summary>
        /// Milliseconds of decoded audio held back as jitter reserve, over and
        /// above the buffer the imminent callback will consume.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>60 ms, derived from the field rather than chosen for roundness.</b>
        /// Six receive streams were captured at the radio on 2026-09-01, every
        /// one over SmartLink and every one at the shipped ten callbacks a
        /// second, so the depth one callback consumes is ten packets. Their
        /// reported queue-depth maxima were 11, 15, 16, 17, 21 and 29 buffers —
        /// surpluses of 1, 5, 6, 7, 11 and 19 packets above what a callback
        /// takes. A surplus is the tail of a burst and a burst is the mirror of
        /// the deficit that preceded it, so those are also the deficits the
        /// reserve would have had to absorb. Six packets — 60 ms — covers the
        /// median session outright.
        /// </para>
        /// <para>
        /// It deliberately does NOT cover the worst case. The reserve is
        /// rebuilt after every starvation rather than decaying to zero, so the
        /// worst case costs one re-prime instead of a permanent loss of margin,
        /// and buying the worst case up front would charge every operator —
        /// including the one on a wired LAN who has no jitter to absorb — for
        /// the worst link anybody has.
        /// </para>
        /// <para>
        /// Overridable per launch with <c>JJFLEX_RX_PRIME_MS</c>; zero disables
        /// priming and restores exactly the behaviour that shipped before this.
        /// </para>
        /// </remarks>
        public const double DefaultReserveMilliseconds = 60.0;

        /// <summary>
        /// Environment variable that overrides <see cref="DefaultReserveMilliseconds"/>
        /// for one launch. A testing lever, like <c>JJFLEX_CONFIG_DIR</c> — not
        /// a setting, not a UI toggle, and never something to tell an operator
        /// to set in ordinary use.
        /// </summary>
        public const string ReserveEnvironmentVariable = "JJFLEX_RX_PRIME_MS";

        private readonly int _buffersPerCallback;
        private readonly double _bufferMilliseconds;   // one queued packet
        private readonly uint _sampleRate;
        private readonly string _streamName;

        private bool _priming = true;
        private bool _everPlayed;

        // Meters. All of these are read on the callback thread and written
        // only there, except at close where the callback has already finished.
        private long _callbacks;
        private long _primeEpisodes;
        private long _primeCallbacks;
        private double _primeSilenceMs;
        private long _starvations;
        private double _starvationSilenceMs;
        private long _releaseTrims;          // routine: the overshoot at prime release
        private long _releaseTrimmedBuffers;
        private long _trims;                 // the ratchet trim, above the ceiling
        private long _trimmedBuffers;
        private double _trimmedMs;

        private int _depthMin = int.MaxValue;
        private int _depthMax;
        private int _depthLast;

        // #196's rate limit, kept: at most one "when" line per second, and only
        // in a second that actually had a starvation.
        //
        // #1229: the second is now counted in CALLBACKS rather than read from a
        // clock, and a second is written by the first callback after it ends
        // rather than by the next starvation. Until then a starvation after the
        // first was placed in time only if another one followed it more than a
        // second later: on the 2026-11-19 night bench the only such line read
        // "2 starvation(s) in the last 1029375 ms", so the gaps could not be
        // set against anything else in the trace. Each callback plays one
        // device buffer, so counting them is the playback clock, and no clock
        // is read on the audio thread at all.
        private readonly int _windowCallbacks;
        private long _windowStart;           // callback that opened the window; 0 when none is open
        private long _windowFirst, _windowLast;
        private long _windowStarvations;
        private double _windowSilenceMs;
        private bool _firstStarvationLogged;

        // The same for trims above the ceiling (#1229): the first is placed at
        // once, later ones get one line per second that had any.
        private long _trimWindowStart;
        private long _trimWindowFirst, _trimWindowLast;
        private long _trimWindowTrims, _trimWindowBuffers;
        private bool _firstTrimLogged;

        // #1229, the stop nit: once the producer has stopped feeding the queue,
        // an empty queue is the end of the audio, not a gap in it. Set from the
        // feeding thread, read by the callback.
        private int _feedEnded;
        private long _stopStarvations;
        private long _stopSilentCallbacks;
        private double _stopSilenceMs;

        public RxPlaybackQueue(string streamName, int buffersPerCallback,
            double bufferMilliseconds, uint sampleRate, double reserveMilliseconds)
        {
            _streamName = string.IsNullOrEmpty(streamName) ? "output" : streamName;
            _buffersPerCallback = Math.Max(1, buffersPerCallback);
            _bufferMilliseconds = (bufferMilliseconds > 0) ? bufferMilliseconds : 0;
            _sampleRate = sampleRate;

            int reserve = 0;
            if (reserveMilliseconds > 0 && _bufferMilliseconds > 0)
            {
                reserve = (int)Math.Ceiling(reserveMilliseconds / _bufferMilliseconds);
            }
            ReserveBuffers = reserve;
            PrimeTarget = _buffersPerCallback + reserve;
            // No reserve means no policy at all: no hold, no re-prime, no trim,
            // byte for byte the behaviour that shipped before this class
            // existed. That is what JJFLEX_RX_PRIME_MS=0 buys, and it is also
            // what the CW monitor gets — a sidetone held back to build a
            // reserve is a sidetone that arrives late, and trimming a queue
            // somebody is keying into would discard their own Morse.
            _priming = reserve > 0;
            // A whole extra callback beyond target before anything is thrown
            // away. FlexLib's own receive list clears itself outright above 30
            // packets (RXAudioStream.AddRXData), so an upstream ceiling already
            // exists and is far blunter than this one — trimming to target
            // discards the surplus, clearing discards everything.
            DrainCeiling = PrimeTarget + _buffersPerCallback;
            // One second of callbacks: the shipped receive stream plays ten
            // 10 ms buffers per callback, so ten callbacks.
            double callbackMs = _buffersPerCallback * _bufferMilliseconds;
            _windowCallbacks = callbackMs > 0 ? Math.Max(1, (int)Math.Ceiling(1000.0 / callbackMs)) : 10;
        }

        /// <summary>Callbacks that make one second of playback: the length of a per-second window.</summary>
        public int WindowCallbacks { get { return _windowCallbacks; } }

        /// <summary>
        /// Tests only: sees every line this queue writes, beside the trace. Set
        /// before the stream runs, never while it runs.
        /// </summary>
        internal Action<string> LineObserver { get; set; }

        /// <summary>
        /// The producer has stopped feeding this queue for good (#1229). Called
        /// by the receive loop as it ends, before the stream itself is stopped.
        /// From then on a callback that finds the queue empty is the end of the
        /// audio, not a gap in it: it is counted "at stop", apart from the
        /// mid-stream figures, and writes no gap line.
        /// </summary>
        /// <remarks>
        /// Why, from the 2026-11-19 night bench's trace: each of four PC-audio
        /// offs wrote one "starvation" in the stream's last callbacks (for
        /// example callback 18600 of 18604). The receive loop ends first
        /// ("remoteAudioProc:stopping remote audio"); the CW monitor's stream is
        /// stopped next, which waits about 330 ms for its callback; only then is
        /// the receive stream stopped, about 560 ms after the loop ended. The
        /// queue ran dry 82 ms after the loop ended, while the device was still
        /// playing, and the summary counted it as a gap.
        /// </remarks>
        public void NoteFeedEnded()
        {
            System.Threading.Volatile.Write(ref _feedEnded, 1);
        }

        /// <summary>True once <see cref="NoteFeedEnded"/> has been called.</summary>
        public bool FeedEnded { get { return System.Threading.Volatile.Read(ref _feedEnded) != 0; } }

        /// <summary>Starvations after the feed ended: the stream's end, not a gap in it.</summary>
        public long StopStarvations { get { return _stopStarvations; } }

        /// <summary>Silent callbacks (starved or holding for a prime) after the feed ended.</summary>
        public long StopSilentCallbacks { get { return _stopSilentCallbacks; } }

        /// <summary>Silence played after the feed ended, in milliseconds.</summary>
        public double StopSilenceMilliseconds { get { return _stopSilenceMs; } }

        private void Write(string line, TraceLevel level)
        {
            Tracing.TraceLine(line, level);
            LineObserver?.Invoke(line);
        }

        /// <summary>Buffers of reserve over and above one callback's demand.</summary>
        public int ReserveBuffers { get; }

        /// <summary>Queue depth playback waits for before it starts, in buffers.</summary>
        public int PrimeTarget { get; }

        /// <summary>Depth above which the standing backlog is trimmed, in buffers.</summary>
        public int DrainCeiling { get; }

        /// <summary>True while the reserve is being (re)built and nothing is played.</summary>
        public bool Priming { get { return _priming; } }

        public long Callbacks { get { return _callbacks; } }
        public long PrimeEpisodes { get { return _primeEpisodes; } }
        public long PrimeCallbacks { get { return _primeCallbacks; } }
        public double PrimeSilenceMilliseconds { get { return _primeSilenceMs; } }
        public long Starvations { get { return _starvations; } }
        public double StarvationSilenceMilliseconds { get { return _starvationSilenceMs; } }
        /// <summary>Trims above the ceiling — the ratchet actually happening.</summary>
        public long Trims { get { return _trims; } }
        public long TrimmedBuffers { get { return _trimmedBuffers; } }
        public double TrimmedMilliseconds { get { return _trimmedMs; } }
        /// <summary>Routine trims of the overshoot when priming releases.</summary>
        public long ReleaseTrims { get { return _releaseTrims; } }
        public long ReleaseTrimmedBuffers { get { return _releaseTrimmedBuffers; } }
        public int DepthMin { get { return _depthMin == int.MaxValue ? 0 : _depthMin; } }
        public int DepthMax { get { return _depthMax; } }
        public int DepthLast { get { return _depthLast; } }
        public bool EverPlayed { get { return _everPlayed; } }

        /// <summary>
        /// Total silence this stream has inserted and will never reclaim, in
        /// milliseconds. <b>This is the ratchet, stated as one number.</b>
        /// </summary>
        public double InsertedSilenceMilliseconds
        {
            get { return _primeSilenceMs + _starvationSilenceMs; }
        }

        /// <summary>
        /// Decide what this callback does, given the queue depth read at entry.
        /// Allocation-free; safe from the realtime callback.
        /// </summary>
        /// <param name="queueDepth">Buffers standing in the queue right now.</param>
        /// <param name="midBuffer">
        /// True when a previous callback left a queued buffer part-played. Neither
        /// trimming nor holding is legal then — the part-played buffer has to
        /// finish, and a callback rate that does not divide the packet rate is
        /// exactly when this happens.
        /// </param>
        public RxCallbackPlan Begin(int queueDepth, bool midBuffer)
        {
            _callbacks++;
            // #1229: a second that has ended is written by the first callback
            // after it, whatever that callback goes on to do, so a lone event
            // is placed in time without waiting for another.
            if (_windowStart != 0 && _callbacks - _windowStart >= _windowCallbacks) FlushStarvationWindow(false);
            if (_trimWindowStart != 0 && _callbacks - _trimWindowStart >= _windowCallbacks) FlushTrimWindow(false);

            if (midBuffer) return new RxCallbackPlan(0, false);

            bool releasing = false;
            if (_priming)
            {
                if (queueDepth < PrimeTarget) return new RxCallbackPlan(0, true);
                _priming = false;
                releasing = true;
                // Depth stats describe PLAYBACK, so they start here. Sampling
                // them while priming is what pinned the reported minimum at
                // zero on every stream captured on 2026-09-01 — the first
                // callback of a stream necessarily sees an empty queue, so a
                // minimum that includes it can only ever be zero and the
                // spread it forms is not a reading of anything.
            }

            if (queueDepth < _depthMin) _depthMin = queueDepth;
            if (queueDepth > _depthMax) _depthMax = queueDepth;
            _depthLast = queueDepth;

            if (ReserveBuffers <= 0) return new RxCallbackPlan(0, false);

            // Two moments deserve a trim, and they are the same arithmetic.
            //
            // RELEASE. Priming ends on the first callback that SEES the target,
            // and a callback only looks once, so by then more has usually
            // arrived than was asked for — at ten callbacks a second the
            // overshoot can be a whole extra buffer. Trimming here makes the
            // realised reserve the configured one rather than "the configured
            // one plus however much luck put in", and it is the one moment when
            // discarding is free: nothing has been played yet, so the splice
            // falls inside silence the operator is already hearing.
            //
            // CEILING. A burst leaves a surplus that nothing ever drains,
            // because the callback cannot consume more than one device buffer
            // however far ahead the queue runs — that surplus is pure latency
            // and it is permanent. Above the ceiling it is given back.
            bool overCeiling = queueDepth > DrainCeiling;
            if ((releasing || overCeiling) && queueDepth > PrimeTarget)
            {
                int drop = queueDepth - PrimeTarget;
                // Counted apart, because they mean different things to whoever
                // reads the trace. A release trim happens once per prime and is
                // routine. A ceiling trim means the backlog genuinely ratcheted
                // — that is the event #473 predicted, and it should be rare.
                if (overCeiling)
                {
                    _trims++;
                    _trimmedBuffers += drop;
                    _trimmedMs += drop * _bufferMilliseconds;
                    NoteCeilingTrim(drop);
                }
                else
                {
                    _releaseTrims++;
                    _releaseTrimmedBuffers += drop;
                }
                return new RxCallbackPlan(drop, false);
            }

            return new RxCallbackPlan(0, false);
        }

        /// <summary>
        /// Record a callback that output a whole buffer of silence while the
        /// reserve was being built. Not a starvation: nothing was lost, playback
        /// simply has not started yet.
        /// </summary>
        public void NotePrimingBuffer(long silentFrames)
        {
            if (FeedEnded)
            {
                // Holding for a prime that will never come: the stream's end.
                _stopSilentCallbacks++;
                _stopSilenceMs += MillisecondsFor(silentFrames);
                return;
            }
            _primeCallbacks++;
            _primeSilenceMs += MillisecondsFor(silentFrames);
        }

        /// <summary>
        /// Record a callback that played. <paramref name="silentFrames"/> is the
        /// shortfall this callback had to fill with zeros — zero on a healthy
        /// callback, and the actual measured shortfall otherwise.
        /// </summary>
        /// <remarks>
        /// <b>The shortfall is measured, not assumed to be a whole buffer.</b>
        /// The claim this retires, and the evidence against it, are recorded
        /// once — in <see cref="AudioBuffering"/>'s remarks, where the claim
        /// itself used to live. What matters here is why nobody caught it: the
        /// old counter reported one "silent fill" whatever the size of the gap,
        /// so a 10 ms shortfall and a 100 ms shortfall were the same number in
        /// every trace anybody read.
        /// </remarks>
        public void NotePlayed(int buffersConsumed, long silentFrames)
        {
            if (buffersConsumed > 0) _everPlayed = true;
            if (silentFrames <= 0) return;

            double ms = MillisecondsFor(silentFrames);
            if (FeedEnded)
            {
                // #1229: the producer has stopped, so this is the end of the
                // audio, not a gap in it. Counted apart and never written as a
                // gap. The reserve is still rebuilt, as before, so the callbacks
                // left before the stream stops hold for a prime that never
                // comes rather than each starving again.
                _stopSilentCallbacks++;
                _stopSilenceMs += ms;
                if (_everPlayed)
                {
                    _stopStarvations++;
                    if (ReserveBuffers > 0) _priming = true;
                }
                return;
            }
            if (!_everPlayed)
            {
                // Nothing has ever played on this stream, so this is the wait
                // for the first audio rather than a gap in it. Exactly the
                // distinction the old OutputDataSeen flag drew, and the reason
                // the CW monitor's 1,277 silent buffers were rightly not called
                // starvation — it is fed only while somebody is keying.
                _primeCallbacks++;
                _primeSilenceMs += ms;
                return;
            }

            _starvations++;
            _starvationSilenceMs += ms;

            // The reserve has just been spent. Rebuild it, or the next late
            // packet starves on an empty margin exactly as this one did.
            if (ReserveBuffers > 0 && !_priming)
            {
                _priming = true;
                _primeEpisodes++;
            }

            if (!_firstStarvationLogged)
            {
                _firstStarvationLogged = true;
                Write("audio " + _streamName + " stream: the playback queue ran dry "
                    + "mid-stream at callback " + _callbacks + " — " + ms.ToString("F0")
                    + " ms of the device buffer was filled with silence, audible as a gap with a "
                    + "click at each edge. PortAudio raises no flag for this (we supplied the zeros "
                    + "ourselves). Each later second with any starvation gets one line, written as "
                    + "that second ends; totals logged when the stream closes.", TraceLevel.Error);
                return;
            }

            // Every later starvation joins the second it falls in, and that
            // second is written by the first callback after it ends (Begin).
            if (_windowStart == 0) { _windowStart = _callbacks; _windowFirst = _callbacks; }
            _windowLast = _callbacks;
            _windowStarvations++;
            _windowSilenceMs += ms;
        }

        private void FlushStarvationWindow(bool final)
        {
            if (_windowStarvations > 0)
            {
                Write("audio " + _streamName + " stream: " + _windowStarvations + " starvation(s) costing "
                    + _windowSilenceMs.ToString("F0") + " ms of silence"
                    + (final ? " in the final partial second" : " in the second")
                    + " from callback " + _windowFirst
                    + (_windowLast != _windowFirst ? " (the last at callback " + _windowLast + ")" : "")
                    + "; running total " + _starvations + ", " + _starvationSilenceMs.ToString("F0")
                    + " ms of silence inserted and never reclaimed (written at callback " + _callbacks + ")",
                    TraceLevel.Error);
            }
            _windowStart = 0;
            _windowStarvations = 0;
            _windowSilenceMs = 0;
        }

        private void NoteCeilingTrim(int dropped)
        {
            if (!_firstTrimLogged)
            {
                _firstTrimLogged = true;
                Write("audio " + _streamName + " stream: the backlog passed the " + DrainCeiling
                    + "-buffer ceiling at callback " + _callbacks + "; " + dropped + " buffer(s) discarded, "
                    + (dropped * _bufferMilliseconds).ToString("F0") + " ms. Each later second with any "
                    + "such trim gets one line, written as that second ends.", TraceLevel.Error);
                return;
            }
            if (_trimWindowStart == 0) { _trimWindowStart = _callbacks; _trimWindowFirst = _callbacks; }
            _trimWindowLast = _callbacks;
            _trimWindowTrims++;
            _trimWindowBuffers += dropped;
        }

        private void FlushTrimWindow(bool final)
        {
            if (_trimWindowTrims > 0)
            {
                Write("audio " + _streamName + " stream: " + _trimWindowTrims + " trim(s) above the "
                    + DrainCeiling + "-buffer ceiling"
                    + (final ? " in the final partial second" : " in the second")
                    + " from callback " + _trimWindowFirst
                    + (_trimWindowLast != _trimWindowFirst ? " (the last at callback " + _trimWindowLast + ")" : "")
                    + ", discarding " + _trimWindowBuffers + " buffer(s), "
                    + (_trimWindowBuffers * _bufferMilliseconds).ToString("F0") + " ms; running total "
                    + _trims + " trim(s), " + _trimmedBuffers + " buffer(s) (written at callback " + _callbacks + ")",
                    TraceLevel.Error);
            }
            _trimWindowStart = 0;
            _trimWindowTrims = 0;
            _trimWindowBuffers = 0;
        }

        /// <summary>
        /// The summary lines for this stream, written when its callback
        /// completes. Deliberately several short lines rather than one long one:
        /// they are read aloud.
        /// </summary>
        public void TraceSummary()
        {
            FlushStarvationWindow(true);
            FlushTrimWindow(true);

            Write("audio " + _streamName + " queue policy: primed to "
                + PrimeTarget + " buffer(s) — " + _buffersPerCallback
                + " for the callback plus " + ReserveBuffers + " of reserve ("
                + (ReserveBuffers * _bufferMilliseconds).ToString("F0")
                + " ms) — trimming above " + DrainCeiling + " buffer(s)",
                TraceLevel.Info);

            Write("audio " + _streamName + " queue summary: "
                + _callbacks + " callbacks, " + _starvations + " mid-stream starvation(s) costing "
                + _starvationSilenceMs.ToString("F0") + " ms of silence, "
                + _primeEpisodes + " re-prime(s) after the first, "
                + _primeCallbacks + " priming callback(s) costing "
                + _primeSilenceMs.ToString("F0") + " ms"
                + (_starvations == 0 ? " (the queue never ran dry while playing)" : ""),
                _starvations == 0 ? TraceLevel.Info : TraceLevel.Error);

            // #1229: the stream's end, kept apart from every figure above.
            if (FeedEnded)
            {
                Write("audio " + _streamName + " at stop: the feed ended before the stream did; "
                    + _stopStarvations + " starvation(s) at stop and " + _stopSilentCallbacks
                    + " callback(s) with silence in them after it ended, " + _stopSilenceMs.ToString("F0")
                    + " ms; none of it a gap in the audio and none of it counted above", TraceLevel.Info);
            }

            // The one number that answers "how far behind the radio did this
            // session leave the operator". Silence inserted is never played
            // back: the callback cannot consume more than one device buffer per
            // call, so nothing anywhere runs faster afterwards to catch up.
            Write("audio " + _streamName + " standing latency: "
                + InsertedSilenceMilliseconds.ToString("F0")
                + " ms of silence inserted over the life of the stream, none of it ever "
                + "played back — the callback cannot consume faster than nominal",
                TraceLevel.Info);

            // Separately, and in the other direction: audio deliberately
            // discarded, which is the only way any standing delay is given back.
            Write("audio " + _streamName + " backlog trims: "
                + _trims + " above the " + DrainCeiling + "-buffer ceiling, discarding "
                + _trimmedBuffers + " buffer(s) — " + _trimmedMs.ToString("F0")
                + " ms of delay that would otherwise have stood in the queue for the rest of "
                + "the stream" + (_trims == 0 ? " (the backlog never ratcheted that far)" : "")
                + "; plus " + _releaseTrims + " routine trim(s) of "
                + _releaseTrimmedBuffers + " buffer(s) at prime release",
                _trims == 0 ? TraceLevel.Info : TraceLevel.Error);

            if (_everPlayed && _bufferMilliseconds > 0)
            {
                Write("audio " + _streamName + " queue depth: "
                    + DepthMin + " to " + _depthMax + " buffers standing at callback entry, "
                    + _depthLast + " at the last playing callback — "
                    + (DepthMin * _bufferMilliseconds).ToString("F0") + " to "
                    + (_depthMax * _bufferMilliseconds).ToString("F0")
                    + " ms of receive latency, ending at "
                    + (_depthLast * _bufferMilliseconds).ToString("F0")
                    + " ms (priming callbacks excluded — including them pins the minimum at zero)",
                    TraceLevel.Info);
            }
            else
            {
                Write("audio " + _streamName + " queue depth: nothing was ever played "
                    + "on this stream, so there is no depth to report", TraceLevel.Info);
            }
        }

        /// <summary>
        /// Frames — samples per channel — to milliseconds. Frames rather than
        /// floats on purpose: a mono playback device is handed half as many
        /// floats for the same span of audio, and a silence meter that counted
        /// floats would report half the gap on somebody's only speaker.
        /// </summary>
        private double MillisecondsFor(long frames)
        {
            if (_sampleRate == 0 || frames <= 0) return 0;
            return frames * 1000.0 / _sampleRate;
        }

        /// <summary>
        /// The jitter reserve this launch is using, in milliseconds:
        /// <see cref="DefaultReserveMilliseconds"/> unless
        /// <c>JJFLEX_RX_PRIME_MS</c> names something else. Read once and traced,
        /// because a knob whose value is invisible is a knob nobody can report
        /// a measurement against.
        /// </summary>
        public static double ConfiguredReserveMilliseconds()
        {
            if (_reserveResolved) return _reserveMs;
            _reserveMs = DefaultReserveMilliseconds;
            string raw = null;
            try { raw = Environment.GetEnvironmentVariable(ReserveEnvironmentVariable); }
            catch (Exception ex)
            {
                Tracing.TraceLine("RxPlaybackQueue: could not read "
                    + ReserveEnvironmentVariable + ", using the default: " + ex.Message,
                    TraceLevel.Error);
            }
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (double.TryParse(raw.Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double ms)
                    && ms >= 0 && ms <= 1000)
                {
                    _reserveMs = ms;
                    Tracing.TraceLine("RxPlaybackQueue: " + ReserveEnvironmentVariable
                        + " set the receive jitter reserve to " + ms.ToString("F0")
                        + " ms for this launch (default is "
                        + DefaultReserveMilliseconds.ToString("F0") + " ms)"
                        + (ms == 0 ? " — zero disables priming entirely" : ""),
                        TraceLevel.Error);
                }
                else
                {
                    Tracing.TraceLine("RxPlaybackQueue: " + ReserveEnvironmentVariable + "=\""
                        + raw + "\" is not a number of milliseconds between 0 and 1000; using the "
                        + "default " + DefaultReserveMilliseconds.ToString("F0") + " ms",
                        TraceLevel.Error);
                }
            }
            _reserveResolved = true;
            return _reserveMs;
        }

        private static bool _reserveResolved;
        private static double _reserveMs = DefaultReserveMilliseconds;

        /// <summary>Forget the cached environment read. Tests only.</summary>
        public static void ResetConfiguredReserve()
        {
            _reserveResolved = false;
            _reserveMs = DefaultReserveMilliseconds;
        }
    }
}
