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
        private long _windowTick;
        private long _windowStarvations;
        private bool _firstStarvationLogged;

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

            long nowTick = Environment.TickCount64;
            if (_windowTick == 0) _windowTick = nowTick;
            _windowStarvations++;

            if (!_firstStarvationLogged)
            {
                _firstStarvationLogged = true;
                Tracing.TraceLine("audio " + _streamName + " stream: the playback queue ran dry "
                    + "mid-stream at callback " + _callbacks + " — " + ms.ToString("F0")
                    + " ms of the device buffer was filled with silence, audible as a gap with a "
                    + "click at each edge. PortAudio raises no flag for this (we supplied the zeros "
                    + "ourselves). Further occurrences are counted silently; totals logged when the "
                    + "stream closes.", TraceLevel.Error);
            }

            if (nowTick - _windowTick >= 1000)
            {
                Tracing.TraceLine("audio " + _streamName + " stream: "
                    + _windowStarvations + " starvation(s) in the last "
                    + (nowTick - _windowTick) + " ms (running total " + _starvations
                    + ", " + _starvationSilenceMs.ToString("F0")
                    + " ms of silence inserted and never reclaimed, callback " + _callbacks + ")",
                    TraceLevel.Error);
                _windowTick = nowTick;
                _windowStarvations = 0;
            }
        }

        /// <summary>
        /// The summary lines for this stream, written when its callback
        /// completes. Deliberately several short lines rather than one long one:
        /// they are read aloud.
        /// </summary>
        public void TraceSummary()
        {
            if (_windowStarvations > 0)
            {
                Tracing.TraceLine("audio " + _streamName + " stream: "
                    + _windowStarvations + " starvation(s) in the final partial second"
                    + " (callback " + _callbacks + ")", TraceLevel.Error);
                _windowStarvations = 0;
            }

            Tracing.TraceLine("audio " + _streamName + " queue policy: primed to "
                + PrimeTarget + " buffer(s) — " + _buffersPerCallback
                + " for the callback plus " + ReserveBuffers + " of reserve ("
                + (ReserveBuffers * _bufferMilliseconds).ToString("F0")
                + " ms) — trimming above " + DrainCeiling + " buffer(s)",
                TraceLevel.Info);

            Tracing.TraceLine("audio " + _streamName + " queue summary: "
                + _callbacks + " callbacks, " + _starvations + " mid-stream starvation(s) costing "
                + _starvationSilenceMs.ToString("F0") + " ms of silence, "
                + _primeEpisodes + " re-prime(s) after the first, "
                + _primeCallbacks + " priming callback(s) costing "
                + _primeSilenceMs.ToString("F0") + " ms"
                + (_starvations == 0 ? " (the queue never ran dry while playing)" : ""),
                _starvations == 0 ? TraceLevel.Info : TraceLevel.Error);

            // The one number that answers "how far behind the radio did this
            // session leave the operator". Silence inserted is never played
            // back: the callback cannot consume more than one device buffer per
            // call, so nothing anywhere runs faster afterwards to catch up.
            Tracing.TraceLine("audio " + _streamName + " standing latency: "
                + InsertedSilenceMilliseconds.ToString("F0")
                + " ms of silence inserted over the life of the stream, none of it ever "
                + "played back — the callback cannot consume faster than nominal",
                TraceLevel.Info);

            // Separately, and in the other direction: audio deliberately
            // discarded, which is the only way any standing delay is given back.
            Tracing.TraceLine("audio " + _streamName + " backlog trims: "
                + _trims + " above the " + DrainCeiling + "-buffer ceiling, discarding "
                + _trimmedBuffers + " buffer(s) — " + _trimmedMs.ToString("F0")
                + " ms of delay that would otherwise have stood in the queue for the rest of "
                + "the stream" + (_trims == 0 ? " (the backlog never ratcheted that far)" : "")
                + "; plus " + _releaseTrims + " routine trim(s) of "
                + _releaseTrimmedBuffers + " buffer(s) at prime release",
                _trims == 0 ? TraceLevel.Info : TraceLevel.Error);

            if (_everPlayed && _bufferMilliseconds > 0)
            {
                Tracing.TraceLine("audio " + _streamName + " queue depth: "
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
                Tracing.TraceLine("audio " + _streamName + " queue depth: nothing was ever played "
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
