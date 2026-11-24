using System;
using System.Collections;
using System.Diagnostics;
using JJTrace;

namespace JJPortaudio
{
    /// <summary>The receive catch-up's mode for a launch (<c>JJFLEX_RX_CATCHUP</c>, test-only).</summary>
    public enum RxCatchUpMode
    {
        /// <summary>Today's trims and re-prime exactly; nothing is caught up.</summary>
        Off,
        /// <summary>Today's output, with every decision the shifter would make traced beside it.</summary>
        Shadow,
        /// <summary>The default: late audio caught up, a low queue stretched over, drift corrected, seams joined.</summary>
        On,
    }

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
    /// entry in this tree gives any other reason. <b>Copying, a backlog cannot
    /// be consumed away; it can only be discarded</b>, which is what
    /// <see cref="RxCallbackPlan.Discard"/> does on today's path.
    /// </para>
    /// <para>
    /// <b>Since Sprint 49 Track P2 (#473, #873 phase 2) the callback does not
    /// only copy.</b> With a reserve, and unless <c>JJFLEX_RX_CATCHUP</c> says
    /// off, it renders through the lag shifter (<see cref="RxLagShifter"/>),
    /// which CAN play a backlog slightly faster than nominal, by cutting whole
    /// periods and gap time in audio already in hand: so a backlog is caught
    /// up, not discarded, and a queue running low is stretched over. The
    /// paragraph above is still true of today's path, which the CW monitor
    /// and <c>JJFLEX_RX_PRIME_MS=0</c> take.
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

        // ══ Catch-up: the lag shifter at the playback queue (#473, #873 phase 2, #1154; owner two) ══
        //
        // With a reserve, and unless JJFLEX_RX_CATCHUP says off, the callback no
        // longer copies buffers straight out of the queue: it moves them into the
        // lag shifter's ring (RxLagShifter) and renders the device buffer from
        // there, and this controller steers the ring's depth toward the target.
        // A backlog is caught up (played slightly faster, gaps first) instead of
        // discarded; a queue running low is stretched over (played slightly
        // slower) instead of starving; a slow clock difference is corrected by a
        // period splice now and then; a join across lost audio is faded; and a
        // backlog over 1.5 s is cut once, at a quiet moment. The CW monitor (no
        // reserve), JJFLEX_RX_PRIME_MS=0 and JJFLEX_RX_CATCHUP=off take today's
        // path below, untouched, and so are sample for sample what shipped.

        /// <summary>Environment variable: <c>off</c>, <c>shadow</c> or <c>on</c> (the default). Test-only, never in any interface (Noel's S11: no operator switch).</summary>
        public const string CatchUpEnvironmentVariable = "JJFLEX_RX_CATCHUP";

        /// <summary>Below this excess nothing is done: one packet, below the queue's own granularity (first report, section 5).</summary>
        public const double DeadBandMilliseconds = 10;
        /// <summary>An excess between the dead band and this, held for <see cref="DriftHoldSeconds"/>, is clock drift (first report, section 5).</summary>
        public const double DriftCeilingMilliseconds = 30;
        /// <summary>How long a small excess must hold before it is called drift (first report, section 5).</summary>
        public const double DriftHoldSeconds = 30;
        /// <summary>Above this much late audio, one faded cut at a quiet moment, down to <see cref="CutLeaveMilliseconds"/> over target (Noel 2026-11-20 11:25, S9).</summary>
        public const double CutAboveMilliseconds = 1500;
        /// <summary>A cut leaves this much over target, caught up by speeding (first report, section 5).</summary>
        public const double CutLeaveMilliseconds = 200;
        /// <summary>Above this, something is broken: cut at the next buffer (first report, section 5).</summary>
        public const double CutNowMilliseconds = 3000;

        /// <summary>
        /// Whether a digital mode (DIGU, DIGL, RTTY: <see cref="RxContentClass.NoSplice"/>)
        /// takes the long-backlog cuts above (<see cref="CutAboveMilliseconds"/> and
        /// <see cref="CutNowMilliseconds"/>). False is the brief's "nothing is cut or
        /// repeated" in those modes; the follow-up research (2026-11-08, section 3)
        /// proposed keeping the cut above about 1.5 s there. Codex's check of
        /// 2567a596 (C1) found the two disagree, and the question is Noel's: the
        /// other answer is this one line. With false, a digital-mode backlog is held
        /// until the ring is full (4.5 s), where the ring's overflow drops the
        /// oldest audio.
        /// </summary>
        public const bool DigitalModesTakeTheLongCut = false;

        /// <summary>Tests only: the other answer to <see cref="DigitalModesTakeTheLongCut"/>, or null for the constant.</summary>
        internal static bool? DigitalModesTakeTheLongCutOverrideForTest { get; set; }
        /// <summary>How far ahead the quiet moment for a cut is looked for, and the gap that counts as one (first report, section 5).</summary>
        public const double QuietHorizonMilliseconds = 1000, QuietGapMilliseconds = 50;
        /// <summary>
        /// Bridging starts when the depth at a callback's entry is short of the
        /// target by more than this share of the reserve (my judgement: half, so
        /// ordinary arrival jitter inside the reserve does not set it off).
        /// </summary>
        public const double BridgeAtReserveFraction = 0.5;
        /// <summary>
        /// After running dry, play the moment audio returns, unless it ran dry
        /// once already within this long: then wait for the full reserve as today
        /// (Noel 2026-11-20 11:25, S8).
        /// </summary>
        public const double SecondDryWithinSeconds = 5;
        /// <summary>Tests only: false keeps today's wait after every dry spell (the other refill choice, rendered for Noel).</summary>
        internal bool PlayAtOnce { get; set; } = true;

        private readonly RxCatchUpMode _mode;
        private readonly RxLagShifter _shifter;
        private readonly int _fpb;
        private readonly long _targetFrames, _callbackFrames, _reserveFrames, _bridgeAtFrames;
        private readonly long _driftHoldCallbacks, _secondDryCallbacks, _minuteCallbacks;
        private readonly float[] _scratch;
        private readonly long[] _entryDepth;
        private int _entryAt, _entryCount;
        private long _driftSince = -1, _deficitSince = -1, _lastDry = long.MinValue / 2;
        private bool _waitFull;
        private long _minuteStart;
        private double _minuteMinMs = double.MaxValue, _minuteMaxMs = double.MinValue;
        private long _minuteRemoved, _minuteAdded, _minuteRemovedMark, _minuteAddedMark;
        private long _catchUps, _catchUpRemoved, _catchUpAdded, _cutFrames, _seamJoins;

        // The class and seam mark of each enqueued buffer, travelling beside it:
        // written by the thread that enqueues (before the enqueue), read by the
        // callback as it dequeues, matched by reference so a cleared queue only
        // leaves entries that are skipped. Preallocated; no allocation either side.
        private const int MetaCapacity = 512;
        private readonly float[][] _metaBuf = new float[MetaCapacity][];
        private readonly byte[] _metaCls = new byte[MetaCapacity];
        private readonly int[] _metaSeam = new int[MetaCapacity];   // frames into the buffer, or -1
        private long _metaHead, _metaTail, _metaDropped;

        /// <summary>The catch-up mode this queue runs in: off for a queue with no reserve, whatever the variable says.</summary>
        public RxCatchUpMode CatchUp => _mode;

        /// <summary>Tests only: the core this queue renders through (null on today's path).</summary>
        internal RxLagShifter ShifterForTest => _shifter;

        /// <summary>True when the ring and the queue hold nothing (today's path: the queue alone).</summary>
        public bool RingEmpty => _mode != RxCatchUpMode.On || _shifter.Available <= 0;

        /// <summary>Drift and catch-up totals for the summary.</summary>
        public long CatchUpEvents => _catchUps;
        public long CatchUpRemovedFrames => _catchUpRemoved;
        public long CatchUpAddedFrames => _catchUpAdded;
        public long CutFrames => _cutFrames;
        public long SeamJoins => _seamJoins;

        /// <summary>
        /// Called by the thread that feeds the queue, just BEFORE it enqueues
        /// <paramref name="buffer"/>: what the buffer is (the classifier's class)
        /// and where a seam (a join across lost audio) sits in it, in frames, or -1
        /// (Codex's check of 2567a596, B1: the receive processing delays the audio,
        /// so the seam can come out part-way into a buffer, or a buffer later).
        /// Lock-free and allocation-free; on today's path it does nothing.
        /// </summary>
        public void NoteEnqueued(float[] buffer, RxContentClass cls, int seamAt)
        {
            if (_mode == RxCatchUpMode.Off) return;
            long head = _metaHead;
            if (head - System.Threading.Volatile.Read(ref _metaTail) >= MetaCapacity) { System.Threading.Interlocked.Increment(ref _metaDropped); return; }
            int i = (int)(head % MetaCapacity);
            _metaBuf[i] = buffer; _metaCls[i] = (byte)cls; _metaSeam[i] = seamAt;
            System.Threading.Volatile.Write(ref _metaHead, head + 1);
        }

        /// <summary>As above, with a seam just before the buffer or none.</summary>
        public void NoteEnqueued(float[] buffer, RxContentClass cls, bool seamBefore) => NoteEnqueued(buffer, cls, seamBefore ? 0 : -1);

        /// <summary>The buffer's audio into the ring, its seam marked where it sits (never in a digital mode).</summary>
        private void WriteWithSeam(float[] b, RxContentClass cls, int seamAt)
        {
            if (seamAt >= 0 && (cls & RxContentClass.NoSplice) == 0) _shifter.MarkSeamAt(_shifter.WritePosition + seamAt);
            _shifter.Write(b, cls);
        }

        private (RxContentClass Cls, int Seam) TakeMeta(float[] buffer)
        {
            long head = System.Threading.Volatile.Read(ref _metaHead);
            while (_metaTail < head)
            {
                int i = (int)(_metaTail % MetaCapacity);
                var b = _metaBuf[i];
                var cls = (RxContentClass)_metaCls[i]; int seam = _metaSeam[i];
                _metaBuf[i] = null;
                System.Threading.Volatile.Write(ref _metaTail, _metaTail + 1);
                if (ReferenceEquals(b, buffer)) return (cls, seam);
            }
            return (RxContentClass.Unclassified, -1);
        }

        /// <summary>Shadow: the next enqueued buffer, from the meta ring alone (the real queue is today's path's).</summary>
        private bool TakeShadow(out float[] buffer, out RxContentClass cls, out int seam)
        {
            long head = System.Threading.Volatile.Read(ref _metaHead);
            if (_metaTail >= head) { buffer = null; cls = 0; seam = -1; return false; }
            int i = (int)(_metaTail % MetaCapacity);
            buffer = _metaBuf[i]; cls = (RxContentClass)_metaCls[i]; seam = _metaSeam[i];
            _metaBuf[i] = null;
            System.Threading.Volatile.Write(ref _metaTail, _metaTail + 1);
            return buffer != null;
        }

        // ── A third owner's step (item 6): SilkTune, #199's 2026-11-08 14:05 ruling ──
        private long _externalStep;
        private int _externalPending;

        /// <summary>
        /// A step handed in from outside the queue: the content moved by
        /// <paramref name="frames"/> (positive: there is that much more than the
        /// timeline needs, drain it; negative: that much less, grow it), on the
        /// engine-switch policy. Any thread; applied by the next callback, and the
        /// queue's own controller waits while it runs. A seam travels with its
        /// buffer instead (<c>JJAudioStream.MarkSeam</c>). Built for SilkTune
        /// (#199), which is not built here.
        /// </summary>
        public void ExternalStep(long frames)
        {
            if (_mode == RxCatchUpMode.Off) return;
            System.Threading.Interlocked.Exchange(ref _externalStep, frames);
            System.Threading.Volatile.Write(ref _externalPending, 1);
        }

        /// <summary>After the stream stops (AudioStream.StopAudio, which clears the queue too): forget the ring and the marks, so a restart plays nothing stale.</summary>
        public void ClearAfterStop()
        {
            if (_mode == RxCatchUpMode.Off) return;
            _shifter.Clear();
            long head = System.Threading.Volatile.Read(ref _metaHead);
            for (long t = _metaTail; t < head; t++) _metaBuf[t % MetaCapacity] = null;
            System.Threading.Volatile.Write(ref _metaTail, head);
            _entryCount = 0; _entryAt = 0;
            if (ReserveBuffers > 0) _priming = true;
        }

        /// <summary>
        /// One output callback's work, after the stream's own checks: decide,
        /// take from the queue, and fill <paramref name="output"/>. On today's
        /// path (no reserve, or catch-up off) this is exactly what
        /// <c>Audio.outputCallback</c> did before (moved here so tests can drive
        /// it); otherwise it renders through the lag shifter. Shadow runs today's
        /// path for what is heard and the shifter beside it, unheard, tracing
        /// what it would have done. Allocation-free.
        /// </summary>
        public void RenderCallback(RxBufferQueue queue, ref float[] buffer, ref uint offset, Span<float> output, bool monoOut,
            int streamChannels, Func<bool> active)
        {
            if (_mode != RxCatchUpMode.On)
            {
                RenderToday(this, queue, ref buffer, ref offset, output, monoOut, streamChannels, active);
                if (_mode == RxCatchUpMode.Shadow) ShadowStep(output.Length / (monoOut ? 1 : streamChannels));
                return;
            }
            BeginCallback();
            while (queue.TryDequeue(out var b))
            {
                var (cls, seam) = TakeMeta(b);
                WriteWithSeam(b, cls, seam);
            }
            int frames = output.Length / (monoOut ? 1 : streamChannels);
            long depth = _shifter.Available;
            if (_priming)
            {
                long need = (_waitFull || !_everPlayed) ? _targetFrames : 1;
                if (depth < need)
                {
                    output.Clear();
                    NotePrimingBuffer(frames);
                    return;
                }
                _priming = false;
                if (!_everPlayed && depth > _targetFrames)
                {
                    // The release trim at a stream's start stays: nothing has been
                    // played, so the cut falls inside silence (first report, section 5).
                    long drop = (depth - _targetFrames) / _fpb * _fpb;
                    if (drop > 0) { _shifter.SetDelay(depth - drop); _releaseTrims++; _releaseTrimmedBuffers += drop / _fpb; depth -= drop; }
                }
                _shifter.ArmFadeIn();
            }
            int buffersDeep = (int)(depth / _fpb);
            if (buffersDeep < _depthMin) _depthMin = buffersDeep;
            if (buffersDeep > _depthMax) _depthMax = buffersDeep;
            _depthLast = buffersDeep;
            NoteDepth(depth);
            Steer(depth, shadow: false);
            long silent;
            if (monoOut)
            {
                var stereo = new Span<float>(_scratch, 0, frames * 2);
                silent = _shifter.Render(stereo);
                for (int f = 0; f < frames; f++) output[f] = (stereo[2 * f] + stereo[2 * f + 1]) * 0.5f;
            }
            else silent = _shifter.Render(output.Slice(0, frames * 2));
            bool played = silent < frames;
            bool wasPriming = _priming;
            NotePlayed(played ? 1 : 0, silent);
            if (silent > 0 && _everPlayed && !_callbackAfterFeedEnded)
            {
                // A starvation spends the excess: abandon a drain (first report, section 5).
                if (_shifter.Pending > 0) _shifter.Cancel();
                // S8: play at once when audio returns, unless it ran dry within 5 s already.
                _waitFull = !PlayAtOnce || _callbacks - _lastDry <= _secondDryCallbacks;
                _lastDry = _callbacks;
            }
            TakeEvents(shadow: false);
        }

        /// <summary>Today's callback body, exactly as it stood in <c>Audio.outputCallback</c> before Track P2 moved it here.</summary>
        internal static void RenderToday(RxPlaybackQueue q, RxBufferQueue queue, ref float[] buffer, ref uint offset, Span<float> output,
            bool monoOut, int streamChannels, Func<bool> active)
        {
            int floatsPerFrame = monoOut ? 1 : streamChannels;
            int outAt = 0, outEnd = output.Length;
            long silentFrames = 0;
            int buffersConsumed = 0;
            if (q != null)
            {
                RxCallbackPlan plan = q.Begin(queue.Count, offset != 0);
                for (int i = 0; i < plan.Discard && queue.TryDequeue(out _); i++) { }
                if (plan.HoldForPrime)
                {
                    long primingFrames = (outEnd - outAt) / floatsPerFrame;
                    while (outAt != outEnd) output[outAt++] = 0f;
                    q.NotePrimingBuffer(primingFrames);
                    return;
                }
            }
            while (active())
            {
                bool silence = false;
                if (offset == 0)
                {
                    // Track RT (#1232): the "silence" line that stood here is
                    // gone. Every callback that found the queue empty traced it
                    // synchronously at Verbose, so a detailed capture put a slow
                    // listener on the audio thread once per starved or idle
                    // callback (Codex measured 181 ms there). The silence is
                    // still counted (NotePlayed, below), and every starvation is
                    // still placed in time by this queue's own lines.
                    if (!queue.TryDequeue(out var next))
                    {
                        silence = true;
                        silentFrames += (outEnd - outAt) / floatsPerFrame;
                    }
                    else
                    {
                        buffer = next;
                        buffersConsumed++;
                    }
                }
                if (silence)
                {
                    while (outAt != outEnd) output[outAt++] = 0f;
                }
                else if (monoOut)
                {
                    while ((offset < buffer.Length) & (outAt != outEnd))
                    {
                        float l = buffer[offset++];
                        float r = (offset < buffer.Length) ? buffer[offset++] : l;
                        output[outAt++] = (l + r) * 0.5f;
                    }
                    if (offset >= buffer.Length) offset = 0;
                }
                else
                {
                    while ((offset < buffer.Length) & (outAt != outEnd))
                    {
                        output[outAt++] = buffer[offset++];
                    }
                    if (offset == buffer.Length) offset = 0;
                }
                if (outAt == outEnd) break;
            }
            q?.NotePlayed(buffersConsumed, silentFrames);
        }

        /// <summary>Shadow: the shifter runs beside today's path on the same arrivals, unheard; its decisions are traced.</summary>
        private void ShadowStep(int frames)
        {
            while (TakeShadow(out var b, out var cls, out int seam)) WriteWithSeam(b, cls, seam);
            long depth = _shifter.Available;
            if (_shadowPriming)
            {
                if (depth < _targetFrames) return;
                _shadowPriming = false;
                if (depth > _targetFrames) _shifter.SetDelay(_targetFrames);
                depth = _shifter.Available;
            }
            Steer(depth, shadow: true);
            long silent = _shifter.Render(new Span<float>(_scratch, 0, frames * 2));
            if (silent > 0) { if (_shifter.Pending > 0) _shifter.Cancel(); _shadowPriming = true; }
            TakeEvents(shadow: true);
        }
        private bool _shadowPriming = true;

        /// <summary>
        /// The controller (owner two). The excess is the LEAST depth over the
        /// last second of callbacks, less the target: a burst that only refills
        /// the reserve is not surplus, and a retune's short pause and burst
        /// leave the least depth where it was (first report, section 1).
        /// </summary>
        private void Steer(long depth, bool shadow)
        {
            if (System.Threading.Interlocked.Exchange(ref _externalPending, 0) != 0)
                _shifter.Request(System.Threading.Interlocked.Read(ref _externalStep), LagPolicy.EngineSwitch, LagCause.External);
            // A step handed in from outside runs to its end before the controller steers again.
            if (_shifter.Pending != 0 && _shifter.Cause == LagCause.External) return;
            _entryDepth[_entryAt] = depth;
            _entryAt = (_entryAt + 1) % _entryDepth.Length;
            if (_entryCount < _entryDepth.Length) _entryCount++;
            long least = long.MaxValue;
            for (int i = 0; i < _entryCount; i++) if (_entryDepth[i] < least) least = _entryDepth[i];
            long excess = _entryCount >= _entryDepth.Length ? least - _targetFrames : 0;
            double ms = MillisecondsFor(Math.Abs(excess)) * Math.Sign(excess);
            if (!shadow) { if (ms < _minuteMinMs) _minuteMinMs = ms; if (ms > _minuteMaxMs) _minuteMaxMs = ms; }
            if (_shifter.CutPending) return;
            double over = MillisecondsFor(depth - _targetFrames);
            // A digital mode is never cut unless the constant says so (C1, Noel's to rule).
            bool mayCut = (DigitalModesTakeTheLongCutOverrideForTest ?? DigitalModesTakeTheLongCut)
                || (_shifter.ClassAtRead & RxContentClass.NoSplice) == 0;
            if (mayCut && depth > _targetFrames && over > CutNowMilliseconds)
            {
                _shifter.RequestCut(_shifter.ReadPosition, depth - _targetFrames - FramesFor(CutLeaveMilliseconds));
                RecordCut(depth - _targetFrames - FramesFor(CutLeaveMilliseconds), shadow);
                return;
            }
            if (mayCut && ms > CutAboveMilliseconds)
            {
                long at = _shifter.FindQuietMoment(FramesFor(QuietHorizonMilliseconds), FramesFor(QuietGapMilliseconds));
                _shifter.RequestCut(at, excess - FramesFor(CutLeaveMilliseconds));
                RecordCut(excess - FramesFor(CutLeaveMilliseconds), shadow);
                return;
            }
            if (ms >= DeadBandMilliseconds)
            {
                _deficitSince = -1;
                if (ms > DriftCeilingMilliseconds) { _driftSince = -1; _shifter.Request(excess, LagPolicy.CatchUp, LagCause.LateAudio); return; }
                if (_driftSince < 0) _driftSince = _callbacks;
                if (_shifter.Pending > 0 && _shifter.Cause == LagCause.LateAudio) { _shifter.Request(excess, LagPolicy.CatchUp, LagCause.LateAudio); return; }
                if (_callbacks - _driftSince >= _driftHoldCallbacks) _shifter.Request(excess, LagPolicy.Drift, LagCause.Drift);
                return;
            }
            _driftSince = -1;
            if (_everPlayed && depth < _bridgeAtFrames)
            {
                _deficitSince = -1;
                _shifter.Request(-(_targetFrames - depth), LagPolicy.CatchUp, LagCause.Bridge);
                return;
            }
            if (ms <= -DeadBandMilliseconds && ms >= -DriftCeilingMilliseconds)
            {
                if (_deficitSince < 0) _deficitSince = _callbacks;
                if (_callbacks - _deficitSince >= _driftHoldCallbacks) _shifter.Request(excess, LagPolicy.Drift, LagCause.Drift);
                return;
            }
            _deficitSince = -1;
            // Inside the dead band: a catch-up or a bridge in progress has done its work.
            if (_shifter.Pending != 0 && (_shifter.Cause == LagCause.LateAudio || (_shifter.Cause == LagCause.Bridge && depth >= _targetFrames)))
                _shifter.Request(0, LagPolicy.CatchUp, _shifter.Cause);
        }

        private long FramesFor(double ms) => (long)Math.Round(ms * _sampleRate / 1000.0);

        private void RecordCut(long frames, bool shadow)
        {
            var r = new LineRecord { Kind = LineKind.CatchUp, First = _callbacks, Cause = (byte)LagCause.Cut, Removed = frames, Shadow = shadow, Cuts = 1 };
            Record(ref r);
            if (!shadow) _cutFrames += frames;
        }

        private void TakeEvents(bool shadow)
        {
            if (!_shifter.TryTakeCompleted(out var e)) return;
            var r = new LineRecord
            {
                Kind = LineKind.CatchUp, First = _callbacks, Cause = (byte)e.Cause, Policy = (byte)e.Policy, Requested = e.Requested,
                Removed = e.Removed, Added = e.Added, Taken = e.FramesTaken, PeriodSplices = e.PeriodSplices, GapSplices = e.GapSplices,
                Cuts = e.Cuts, Seams = e.SeamJoins, Worst = e.WorstCorrelation, Abandoned = e.Abandoned, Remainder = e.Remainder, Shadow = shadow,
            };
            Record(ref r);
            if (!shadow) { _catchUps++; _catchUpRemoved += e.Removed; _catchUpAdded += e.Added; }
        }

        /// <summary>The per-minute depth line (item 7): drift shows as a slope across them. One line a minute for this queue.</summary>
        private void NoteDepth(long depthFrames)
        {
            if (_minuteStart == 0) _minuteStart = _callbacks;
            if (_mode != RxCatchUpMode.On)
            {
                double ms = MillisecondsFor(Math.Abs(depthFrames - _targetFrames)) * Math.Sign(depthFrames - _targetFrames);
                if (ms < _minuteMinMs) _minuteMinMs = ms;
                if (ms > _minuteMaxMs) _minuteMaxMs = ms;
            }
        }

        private void FlushMinute(bool final)
        {
            if (_minuteStart == 0 || _minuteMinMs == double.MaxValue) { _minuteStart = 0; return; }
            long removed = _mode == RxCatchUpMode.On ? _shifter.TotalRemoved + _shifter.TotalCutFrames : 0;
            long added = _mode == RxCatchUpMode.On ? _shifter.TotalAdded : 0;
            var r = new LineRecord
            {
                Kind = LineKind.MinuteDepth, First = _minuteStart, Last = _callbacks, Final = final,
                Ms = _minuteMinMs, RunningMs = _minuteMaxMs, Removed = removed - _minuteRemovedMark, Added = added - _minuteAddedMark,
            };
            Record(ref r);
            _minuteRemovedMark = removed; _minuteAddedMark = added;
            _minuteStart = 0; _minuteMinMs = double.MaxValue; _minuteMaxMs = double.MinValue;
        }

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
        // feeding thread, read by the callback ONCE, at the callback's entry
        // (Begin), and that reading classifies the whole callback (repair 1:
        // reading it again when the shortage is booked let a gap the callback
        // had already found be filed "at stop" if the mark landed in between).
        private int _feedEnded;
        private bool _callbackAfterFeedEnded;
        private long _stopStarvations;
        private long _stopSilentCallbacks;
        private double _stopSilenceMs;

        // ── The lines, written off the playback callback (#1229 repair 1) ──
        //
        // The callback must not format or write a trace line: Tracing.TraceLine
        // reads clocks, takes the listeners' lock and flushes synchronously, and
        // Codex measured 155 ms inside Begin with a slow listener and 752 bytes
        // allocated with tracing off. So the callback only copies fixed-size
        // numbers into this ring, made here and never resized; a reader on
        // another thread (DrainLines) formats and writes them, each carrying the
        // callback it happened at. One writer, the callback; readers serialised
        // by a lock the callback never takes. A full ring drops a record and
        // counts it, never overwrites one being read.
        private enum LineKind : byte { FirstStarvation, StarvationSecond, FirstTrim, TrimSecond, CatchUp, MinuteDepth }

        private struct LineRecord
        {
            public LineKind Kind;
            public bool Final;
            public long First, Last, WrittenAt, Count, Buffers, RunningCount, RunningBuffers;
            public double Ms, RunningMs;
            // Track P2: one catch-up event, or a minute of depth.
            public byte Cause, Policy;
            public bool Abandoned, Shadow;
            public long Requested, Removed, Added, Taken, Remainder;
            public int PeriodSplices, GapSplices, Cuts, Seams;
            public float Worst;
        }

        // Track RT (#1232): the ring is the shared helper's (JJTrace.RecordRing),
        // and nothing on the feeding side schedules its writing any more: the
        // stream's own off-audio work drains it on the writer's period
        // (Audio.StreamLines), so a muted stream's lines are still written and
        // the last callback's lines need no further packet.
        internal const int LineCapacity = 64;
        private readonly RecordRing<LineRecord> _lines = new RecordRing<LineRecord>(LineCapacity);
        private readonly object _drainLock = new object();

        /// <summary>Tests only: the record ring's own array, to prove it is not a large object.</summary>
        internal object LineStorageForTest => _lines.Storage;

        public RxPlaybackQueue(string streamName, int buffersPerCallback,
            double bufferMilliseconds, uint sampleRate, double reserveMilliseconds, RxCatchUpMode? catchUp = null)
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

            // Catch-up (Track P2): only with a reserve, so the CW monitor and
            // JJFLEX_RX_PRIME_MS=0 always take today's path.
            _mode = reserve > 0 && _sampleRate > 0 ? (catchUp ?? ConfiguredCatchUp()) : RxCatchUpMode.Off;
            _fpb = Math.Max(1, (int)Math.Round(_bufferMilliseconds * _sampleRate / 1000.0));
            _callbackFrames = (long)_buffersPerCallback * _fpb;
            _reserveFrames = (long)reserve * _fpb;
            _targetFrames = _callbackFrames + _reserveFrames;
            _bridgeAtFrames = _callbackFrames + (long)(_reserveFrames * (1 - BridgeAtReserveFraction));
            _driftHoldCallbacks = (long)Math.Ceiling(DriftHoldSeconds * _windowCallbacks);
            _secondDryCallbacks = (long)Math.Ceiling(SecondDryWithinSeconds * _windowCallbacks);
            _minuteCallbacks = 60L * _windowCallbacks;
            _entryDepth = new long[_windowCallbacks];
            if (_mode != RxCatchUpMode.Off)
            {
                // 4.5 s: a backlog up to the 3 s cut-now line, the reserve, a callback and history.
                _shifter = new RxLagShifter(2, (int)(_sampleRate * 4.5), (int)_sampleRate) { DryFades = _mode == RxCatchUpMode.On };
                _scratch = new float[(int)(_callbackFrames * 2) + 2];
            }
            else _scratch = Array.Empty<float>();
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
            OffAudioLines.Write(line, level, LineObserver);
        }

        /// <summary>Callback side: copy one record into the ring. No allocation, no lock, no wait.</summary>
        private void Record(ref LineRecord r)
        {
            _lines.TryRecord(in r);
        }

        /// <summary>True when the callback has recorded lines nobody has written yet.</summary>
        public bool LinesPending { get { return _lines.Pending; } }

        /// <summary>
        /// Format and write every line the callback has recorded, in order. Never
        /// from the callback while the stream plays: the stream's off-audio work
        /// calls it on the writer's period (Track RT, #1232), and the summary at
        /// the stream's close calls it first, so the lines precede the totals.
        /// </summary>
        public void DrainLines()
        {
            lock (_drainLock)
            {
                _lines.TakePending();
                long dropped = _lines.TakeLost();
                if (dropped > 0)
                {
                    Write(OffAudioLines.Lost("audio " + _streamName + " stream: ", dropped, "timing"), TraceLevel.Error);
                }
                // What was recorded when this pass began; anything later is the
                // next pass's, after its own loss line (OffAudioWork looks again).
                for (int n = _lines.Count; n > 0 && _lines.TryTake(out LineRecord r); n--)
                {
                    Write(Format(ref r), TraceLevel.Error);
                }
            }
        }

        private string Format(ref LineRecord r)
        {
            switch (r.Kind)
            {
                case LineKind.FirstStarvation:
                    return "audio " + _streamName + " stream: the playback queue ran dry "
                        + "mid-stream at callback " + r.First + " — " + r.Ms.ToString("F0")
                        + " ms of the device buffer was filled with silence, audible as a gap with a "
                        + "click at each edge. PortAudio raises no flag for this (we supplied the zeros "
                        + "ourselves). Each later second with any starvation gets one line, written as "
                        + "that second ends; totals logged when the stream closes.";
                case LineKind.StarvationSecond:
                    return "audio " + _streamName + " stream: " + r.Count + " starvation(s) costing "
                        + r.Ms.ToString("F0") + " ms of silence"
                        + (r.Final ? " in the final partial second" : " in the second")
                        + " from callback " + r.First
                        + (r.Last != r.First ? " (the last at callback " + r.Last + ")" : "")
                        + "; running total " + r.RunningCount + ", " + r.RunningMs.ToString("F0")
                        + " ms of silence inserted and never reclaimed (second closed at callback " + r.WrittenAt + ")";
                case LineKind.FirstTrim:
                    return "audio " + _streamName + " stream: the backlog passed the " + DrainCeiling
                        + "-buffer ceiling at callback " + r.First + "; " + r.Buffers + " buffer(s) discarded, "
                        + (r.Buffers * _bufferMilliseconds).ToString("F0") + " ms. Each later second with any "
                        + "such trim gets one line, written as that second ends.";
                case LineKind.CatchUp:
                    return "audio " + _streamName + " stream" + (r.Shadow ? " (shadow: what catch-up WOULD have done; the output is untouched)" : "")
                        + ": " + (LagCause)r.Cause + " at callback " + r.First
                        + ((LagCause)r.Cause == LagCause.Cut
                            ? ", one faded cut of " + MillisecondsFor(r.Removed).ToString("F0") + " ms at a quiet moment"
                            // One home for the event's sentence: RxLagEvent.Describe, as the slot's trace uses.
                            : ": " + new RxLagEvent
                            {
                                Cause = (LagCause)r.Cause, Policy = (LagPolicyKind)r.Policy, Requested = r.Requested, Removed = r.Removed,
                                Added = r.Added, FramesTaken = r.Taken, PeriodSplices = r.PeriodSplices, GapSplices = r.GapSplices,
                                Cuts = r.Cuts, SeamJoins = r.Seams, WorstCorrelation = r.Worst, Abandoned = r.Abandoned, Remainder = r.Remainder,
                            }.Describe((int)_sampleRate));
                case LineKind.MinuteDepth:
                    return "audio " + _streamName + " depth" + (r.Final ? " in the final part-minute" : " this minute")
                        + " (callbacks " + r.First + " to " + r.Last + "): least excess over target "
                        + r.Ms.ToString("+0;-0;0") + " ms, most " + r.RunningMs.ToString("+0;-0;0") + " ms; caught up "
                        + MillisecondsFor(r.Removed).ToString("F0") + " ms, stretched " + MillisecondsFor(r.Added).ToString("F0")
                        + " ms (a steady slope across these lines is clock drift)";
                default:
                    return "audio " + _streamName + " stream: " + r.Count + " trim(s) above the "
                        + DrainCeiling + "-buffer ceiling"
                        + (r.Final ? " in the final partial second" : " in the second")
                        + " from callback " + r.First
                        + (r.Last != r.First ? " (the last at callback " + r.Last + ")" : "")
                        + ", discarding " + r.Buffers + " buffer(s), "
                        + (r.Buffers * _bufferMilliseconds).ToString("F0") + " ms; running total "
                        + r.RunningCount + " trim(s), " + r.RunningBuffers + " buffer(s) (second closed at callback "
                        + r.WrittenAt + ")";
            }
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
            BeginCallback();
            return Decide(queueDepth, midBuffer);
        }

        /// <summary>What every callback does first, on either path: count it, read the feed mark once, close any second (or minute) that ended.</summary>
        private void BeginCallback()
        {
            _callbacks++;
            // #1229 repair 1, the boundary policy: a callback is classified by
            // whether the feed had been marked ended when it BEGAN. A shortage
            // found by a callback that began while the feed was live is a gap,
            // even if the mark lands before the shortage is booked: a real gap
            // is never filed "at stop". The cost is the reverse race, a
            // callback that began a few microseconds before the mark and then
            // ran dry counting as a gap, which the producer's early mark makes
            // as unlikely as it can be.
            _callbackAfterFeedEnded = FeedEnded;
            // #1229: a second that has ended is written by the first callback
            // after it, whatever that callback goes on to do, so a lone event
            // is placed in time without waiting for another.
            if (_windowStart != 0 && _callbacks - _windowStart >= _windowCallbacks) FlushStarvationWindow(false);
            if (_trimWindowStart != 0 && _callbacks - _trimWindowStart >= _windowCallbacks) FlushTrimWindow(false);
            if (_minuteStart != 0 && _callbacks - _minuteStart >= _minuteCallbacks) FlushMinute(false);
        }

        /// <summary>Today's decision (the release and ceiling trims, the hold for a prime), on today's path.</summary>
        private RxCallbackPlan Decide(int queueDepth, bool midBuffer)
        {
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
            NoteDepth((long)queueDepth * _fpb);

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
            // CEILING. On today's path a burst leaves a surplus that nothing ever
            // drains, because copying cannot consume more than one device buffer
            // however far ahead the queue runs; above the ceiling it is given back.
            // With catch-up on (Track P2) this decision is not taken at all: the
            // surplus is played slightly faster instead (Steer).
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
            if (_callbackAfterFeedEnded)
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
            if (_callbackAfterFeedEnded)
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
                var first = new LineRecord { Kind = LineKind.FirstStarvation, First = _callbacks, Ms = ms };
                Record(ref first);
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
                var r = new LineRecord
                {
                    Kind = LineKind.StarvationSecond, Final = final, First = _windowFirst, Last = _windowLast,
                    WrittenAt = _callbacks, Count = _windowStarvations, Ms = _windowSilenceMs,
                    RunningCount = _starvations, RunningMs = _starvationSilenceMs,
                };
                Record(ref r);
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
                var first = new LineRecord { Kind = LineKind.FirstTrim, First = _callbacks, Buffers = dropped };
                Record(ref first);
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
                var r = new LineRecord
                {
                    Kind = LineKind.TrimSecond, Final = final, First = _trimWindowFirst, Last = _trimWindowLast,
                    WrittenAt = _callbacks, Count = _trimWindowTrims, Buffers = _trimWindowBuffers,
                    RunningCount = _trims, RunningBuffers = _trimmedBuffers,
                };
                Record(ref r);
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
        /// <remarks>
        /// Written once the stream's callback has finished for good (Track RT,
        /// #1232): by the off-audio writer after the last callback recorded that
        /// it completed, or by the audio server's stop path after the stream
        /// stopped, whichever comes first (Audio.StreamLines). Never on the
        /// callback any more: that is where a slow listener used to hold it. By
        /// then the callback plays nothing more, so the meters it owned are
        /// still. Any second still open is recorded as the final partial one,
        /// and every recorded line is written first, in order; nothing is
        /// written after the totals.
        /// </remarks>
        public void TraceSummary()
        {
            FlushStarvationWindow(true);
            FlushTrimWindow(true);
            if (ReserveBuffers > 0) FlushMinute(true);
            if (_mode != RxCatchUpMode.Off) TakeEvents(_mode == RxCatchUpMode.Shadow);
            DrainLines();

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
            // session leave the operator". On today's path silence inserted is
            // never played back: copying cannot consume faster than nominal. With
            // catch-up on (Track P2) the shifter plays late audio slightly faster,
            // so that is said instead, with what it took back.
            Write("audio " + _streamName + " standing latency: "
                + InsertedSilenceMilliseconds.ToString("F0")
                + " ms of silence inserted over the life of the stream"
                + (_mode == RxCatchUpMode.On
                    ? "; catch-up played late audio faster, taking back " + MillisecondsFor(_catchUpRemoved + _cutFrames).ToString("F0")
                        + " ms (" + MillisecondsFor(_cutFrames).ToString("F0") + " of it by a cut at a quiet moment) and stretching "
                        + MillisecondsFor(_catchUpAdded).ToString("F0") + " ms over a low queue, in " + _catchUps + " event(s); "
                        + _shifter.TotalSeamJoins + " seam(s) joined"
                    : ", none of it ever played back: on today's path the callback cannot consume faster than nominal"
                        + (_mode == RxCatchUpMode.Shadow ? " (shadow catch-up traced what it would have done)" : "")),
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

        /// <summary>
        /// The catch-up mode for this launch: on, unless <c>JJFLEX_RX_CATCHUP</c>
        /// says <c>off</c> or <c>shadow</c>. Read once and traced, like
        /// <c>JJFLEX_RX_PRIME_MS</c>. Test-only; never surfaced in any interface.
        /// </summary>
        public static RxCatchUpMode ConfiguredCatchUp()
        {
            if (_catchUpResolved) return _catchUp;
            _catchUp = RxCatchUpMode.On;
            string raw = null;
            try { raw = Environment.GetEnvironmentVariable(CatchUpEnvironmentVariable); }
            catch (Exception ex) { Tracing.TraceLine("RxPlaybackQueue: could not read " + CatchUpEnvironmentVariable + ": " + ex.Message, TraceLevel.Error); }
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string v = raw.Trim().ToLowerInvariant();
                _catchUp = v == "off" ? RxCatchUpMode.Off : v == "shadow" ? RxCatchUpMode.Shadow : RxCatchUpMode.On;
                Tracing.TraceLine("RxPlaybackQueue: test switch " + CatchUpEnvironmentVariable + "=" + raw + ": receive catch-up is "
                    + (_catchUp == RxCatchUpMode.Off ? "OFF this launch (today's trims and re-prime exactly)"
                        : _catchUp == RxCatchUpMode.Shadow ? "in SHADOW this launch (today's output; what it would do is traced)"
                        : "on (only off and shadow change it)"), TraceLevel.Error);
            }
            _catchUpResolved = true;
            return _catchUp;
        }

        /// <summary>True when a receive stream opened now would join seams: catch-up on, and a reserve to catch up with.</summary>
        public static bool JoinsSeams => ConfiguredCatchUp() == RxCatchUpMode.On && ConfiguredReserveMilliseconds() > 0;

        private static bool _catchUpResolved;
        private static RxCatchUpMode _catchUp = RxCatchUpMode.On;

        /// <summary>Forget the cached catch-up read. Tests only.</summary>
        public static void ResetConfiguredCatchUp() { _catchUpResolved = false; _catchUp = RxCatchUpMode.On; }

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
