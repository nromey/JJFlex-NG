using System;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    public partial class FlexBase
    {
        // ── S16, ruled by Noel 2026-11-18 21:34: hold our own unkey for the
        // transmit noise reduction's delay, plus 10% ──
        //
        // "we know how long NR should be delayed on send, unkey for that value
        // plus say a 10% engineering design margin to handle any anomalies".
        // Transmit noise reduction delays the audio by up to about 180 ms (both
        // slots together), so an unkey that lands at the release cuts the last
        // words of the over off the air. On JJ Flexible's OWN key, the ordinary
        // ends of an over (the hold key, the lock, Escape, the transmit
        // timeout's ordinary end, a check's planned stop) come here.
        //
        // THE HOLD FOLLOWS THE AUDIO, NOT A CLOCK (rulings repair 1, Codex's P1).
        // The capture hands over 100 ms (or 20 ms) at a time, so a timer of the
        // delay times 1.1 could end before the callback that drains the tail had
        // even run: 33 ms against a callback due at 100 ms. So the radio stays
        // keyed until BOTH of these are true:
        //   - the drained buffer that carries the last of the pre-release audio
        //     out of the reducer has been processed AND handed on to the radio
        //     (the conditioner's drain ticket says when the reducer has taken in
        //     the delay's worth of silence; sendOpusInput says when that buffer
        //     has been sent), plus the 10% margin on the delay;
        //   - the delay times 1.1 has passed since the release (the ruling's own
        //     figure, so the hold is never shorter than what was ruled).
        // So the hold is the delay times 1.1, or longer by up to one capture
        // period when the release falls just after a callback.
        //
        // A STALLED CAPTURE CAN NEVER HOLD THE RADIO KEYED: a backstop unkeys at
        // the delay times 1.1, plus one capture period, plus the device latency
        // the capture clock believes for this stream (at most
        // TxCaptureClock.MaxTrustedInputLatencyMs), plus
        // HeldUnkeyBackstopAllowanceMs, whatever the audio did. The latency is
        // in it (join repair 3, Codex's P1) because the block holding the
        // release is delivered up to one period plus that latency after the
        // release: a bound without it cut the last word on a device whose
        // timing the clock accepts.
        //
        // NEVER HERE: every safety unkey writes Transmit = false directly and
        // goes at once: the transmit timeout's 15-minute hard limit, the hard
        // kill and its carrier drop (the app closing, the system-wide watchdog),
        // the reflected-power cut, the silent-microphone release, the lost
        // connection's teardown. Any direct write also ends a hold already
        // running (the setter calls EndHeldUnkeyBy), so a safety unkey can never
        // be waiting behind one. Hardware PTT and VOX unkey the radio
        // themselves and are out of reach.
        //
        // WHAT GOES ON AIR DURING THE HOLD: the chain's drained tail and nothing
        // newer. The capture keeps running (#565), so everything CAPTURED from
        // the release on is silenced, BEFORE the noise reduction
        // (TxAudioConditioner.BeginDrain); what comes out is the audio captured
        // before the release, delayed by the engines, then silence. The release
        // is a point in the capture stream (rulings repair 2, Codex's P1): a
        // block delivered after the release keeps the part captured before it,
        // so the last word is not lost to the capture period. The pipeline
        // enforces that boundary outside the conditioning stage, so a stage
        // fault can never let new microphone audio through either.
        //
        // WHO ENDS THE DRAIN (rulings repair 1, Codex's P1): every admitted
        // key-down, whether or not a hold is still waiting; the radio reporting
        // its Mox off or its interlock at receive; a disconnect, a dispose, or
        // the remote audio stopping; and the first buffer the key reads up. All
        // of these, and the hold's arming, change it under the hold's one lock,
        // so a key-down can never be overwritten by an arming that raced it.
        //
        // WHAT ENDS THE HOLD ITSELF, besides its own end: a key-down (the over
        // carries on), any direct unkey (it goes now), the radio reporting Mox
        // off (it already unkeyed: nothing is written), the radio's interlock
        // reporting receive (unkeyed NOW, so a radio that resumes can never be
        // left keyed with nothing waiting to unkey it), a disconnect or the
        // remote audio stopping (unkeyed now, on the connection the hold began
        // on), and a dispose (nothing written). The queued unkey carries the
        // connection it was meant for and checks it when it runs.

        /// <summary>The margin the hold adds to the measured delay (S16: "plus say a 10%").</summary>
        public const double HeldUnkeyMargin = 1.1;

        /// <summary>The fixed part of the backstop, beyond the delay times 1.1 and one capture period: scheduling slack for the capture thread and the encoder.</summary>
        public const int HeldUnkeyBackstopAllowanceMs = 50;

        private object _heldUnkeyLockObject;
        // Made on first use, so an object built without its constructor (the
        // windowless tests) still has one.
        private object _heldUnkeyLock =>
            Volatile.Read(ref _heldUnkeyLockObject)
            ?? Interlocked.CompareExchange(ref _heldUnkeyLockObject, new object(), null)
            ?? _heldUnkeyLockObject;
        private long _heldUnkeyGeneration;
        private bool _heldUnkeyPending;
        private HeldUnkeyEpisode _heldUnkey;          // the hold now waiting, under the lock
        private HeldUnkeyEpisode _heldUnkeyTailWatch;  // what the send path looks at, or null (no allocation there)

        /// <summary>One held unkey: what it waits for, on which connection.</summary>
        private sealed class HeldUnkeyEpisode
        {
            public long Generation;
            public string Who = "";
            public Radio Radio;               // the connection it was begun on (null on an unconnected rig)
            public double DelayMs;
            public int MinMs;                 // the delay times 1.1
            public int MarginMs;              // the 10%
            public int BackstopMs;
            public long ArmedTimestamp;       // Stopwatch
            public JJPortaudio.TxAudioConditioner.DrainTicket Ticket;
            public int TailSeen;              // 0 until the send path saw the tail handed on
            public long TailHandedOnMs = -1;  // from the release
            public Timer TailTimer, BackstopTimer;
            public Action TailEnd, BackstopEnd;
        }

        /// <summary>
        /// How a held unkey waits: run the action after the given milliseconds.
        /// Tests replace it to run the end by hand; the app uses a timer per hold.
        /// Called once at the release (the backstop) and once when the tail has
        /// been handed on (the margin), possibly on the audio thread.
        /// </summary>
        internal Action<int, Action> HeldUnkeyScheduler { get; set; }

        /// <summary>The hold for a measured delay: the delay times <see cref="HeldUnkeyMargin"/>, rounded up to a whole millisecond; none at zero.</summary>
        public static int HeldUnkeyMs(double delayMs) =>
            delayMs <= 0 || double.IsNaN(delayMs) ? 0 : (int)Math.Ceiling(Math.Round(delayMs * HeldUnkeyMargin, 6)); // rounded first: 100 x 1.1 is 110.00000000000001 in binary

        /// <summary>The 10% margin alone, rounded up, at least 1 ms: what the hold waits after the tail has been handed on.</summary>
        public static int HeldUnkeyMarginMs(double delayMs) =>
            delayMs <= 0 || double.IsNaN(delayMs) ? 0 : Math.Max(1, (int)Math.Ceiling(Math.Round(delayMs * (HeldUnkeyMargin - 1), 6)));

        /// <summary>
        /// The backstop: the delay times 1.1, plus one capture period at
        /// <paramref name="callbacksPerSecond"/>, plus the capture latency the
        /// clock believes for this stream (clamped to 0 ..
        /// <see cref="JJPortaudio.TxCaptureClock.MaxTrustedInputLatencyMs"/>),
        /// plus <see cref="HeldUnkeyBackstopAllowanceMs"/>.
        /// </summary>
        public static int HeldUnkeyBackstopMs(double delayMs, int callbacksPerSecond, int captureLatencyMs = 0) =>
            HeldUnkeyMs(delayMs) + (int)Math.Ceiling(1000.0 / Math.Max(1, callbacksPerSecond))
            + Math.Clamp(captureLatencyMs, 0, JJPortaudio.TxCaptureClock.MaxTrustedInputLatencyMs) + HeldUnkeyBackstopAllowanceMs;

        /// <summary>Whether an unkey is being held for the noise reduction's delay right now.</summary>
        public bool HeldUnkeyPending { get { lock (_heldUnkeyLock) return _heldUnkeyPending; } }

        /// <summary>The last hold begun: the delay times 1.1, in milliseconds (its minimum), for tests and the trace's neighbours.</summary>
        public int LastHeldUnkeyMs { get; private set; }

        /// <summary>The last hold's backstop, in milliseconds.</summary>
        public int LastHeldUnkeyBackstopMs { get; private set; }

        /// <summary>Tests only: the capture latency the backstop budgets, in place of the running stream's.</summary>
        internal Func<int> TxCaptureLatencyMsForTest { get; set; }

        /// <summary>The device latency the capture clock believes for the transmit stream running now (0 when none).</summary>
        private int CaptureLatencyMsNow
        {
            get
            {
                var forTest = TxCaptureLatencyMsForTest;
                if (forTest != null) return forTest();
                try { return opusInputChannel?.PortAudioStream?.TransmitPipeline?.CaptureLatencyMs ?? 0; }
                catch { return 0; }
            }
        }

        /// <summary>The capture's callbacks a second for the stream running now.</summary>
        private int CaptureCallbacksPerSecondNow => _txCallbackRateThisStream > 0 ? _txCallbackRateThisStream : TxAudioCallbacksPerSecond;

        /// <summary>
        /// Unkey our Mox as an ordinary end of an over (S16). When transmit
        /// noise reduction is adding delay to the audio going out now, the radio
        /// stays keyed until the drained tail has been handed on plus the 10%
        /// margin, and at least the delay times 1.1, then unkeys; otherwise it
        /// unkeys at once, exactly as <c>Transmit = false</c>. A key-down during
        /// the hold ends it and the over carries on.
        /// </summary>
        /// <param name="who">What released, for the trace.</param>
        public void UnkeyAfterNoiseDelay(string who)
        {
            double delayMs = 0;
            int delaySamples = 0;
            long now = Environment.TickCount64;
            try
            {
                delayMs = txConditioner?.RecentNoiseDelayMs(now) ?? 0;
                delaySamples = txConditioner?.RecentNoiseDelaySamples(now) ?? 0;
            }
            catch { delayMs = 0; delaySamples = 0; }
            int hold = HeldUnkeyMs(delayMs);
            bool keyed;
            try { keyed = TransmitKeyedOrInterlock; } catch { keyed = false; }
            // A generated source that stands the chain aside (the calibrated
            // tone) is not the microphone's tail: nothing of it is delayed.
            bool bypassingSource = false;
            try { bypassingSource = TxInputSources != null && TxInputSources.Engaged && TxInputSources.BypassesConditioning; }
            catch { bypassingSource = false; }
            if (hold <= 0 || delaySamples <= 0 || !keyed || bypassingSource || txConditioner == null)
            {
                Transmit = false;
                return;
            }
            var e = new HeldUnkeyEpisode
            {
                Who = who ?? "",
                Radio = theRadio,
                DelayMs = delayMs,
                MinMs = hold,
                MarginMs = HeldUnkeyMarginMs(delayMs),
                BackstopMs = HeldUnkeyBackstopMs(delayMs, CaptureCallbacksPerSecondNow, CaptureLatencyMsNow),
                ArmedTimestamp = Stopwatch.GetTimestamp(),
            };
            e.TailEnd = () => FinishHeldUnkey(e, false);
            e.BackstopEnd = () => FinishHeldUnkey(e, true);
            var scheduler = HeldUnkeyScheduler;
            if (scheduler == null)
            {
                // Made here, on the caller's thread, so the send path only ever
                // calls Change on it and allocates nothing.
                e.TailTimer = new Timer(_ => e.TailEnd(), null, Timeout.Infinite, Timeout.Infinite);
            }
            HeldUnkeyEpisode replaced;
            lock (_heldUnkeyLock)
            {
                replaced = _heldUnkeyPending ? _heldUnkey : null;
                e.Generation = ++_heldUnkeyGeneration;
                _heldUnkeyPending = true;
                _heldUnkey = e;
                LastHeldUnkeyMs = hold;
                LastHeldUnkeyBackstopMs = e.BackstopMs;
                // Armed under the lock (Codex's P1): a key-down that arrives now
                // clears it under the same lock, after this, never before.
                e.Ticket = txConditioner.BeginDrain(delaySamples, e.ArmedTimestamp);
                Volatile.Write(ref _heldUnkeyTailWatch, e);
                if (scheduler == null)
                    e.BackstopTimer = new Timer(_ => e.BackstopEnd(), null, e.BackstopMs, Timeout.Infinite);
            }
            DisposeHeldUnkeyTimers(replaced);
            Tracing.TraceLine("FlexBase: unkey by " + e.Who + " held: transmit noise reduction adds "
                + delayMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                + " ms; the radio stays keyed until the drained tail is handed on plus "
                + e.MarginMs + " ms, and at least " + hold + " ms; backstop " + e.BackstopMs + " ms at "
                + CaptureCallbacksPerSecondNow + " callbacks a second and " + CaptureLatencyMsNow + " ms of capture latency", TraceLevel.Info);
            scheduler?.Invoke(e.BackstopMs, e.BackstopEnd);
        }

        /// <summary>
        /// The send path, after each transmit frame is handed to the radio
        /// (<c>sendOpusInput</c>, the audio thread). When a hold is waiting and
        /// the frame just sent carried the end of the drained tail, start the
        /// margin. Allocates nothing: two reads in the ordinary case.
        /// </summary>
        internal void NoteTxFrameHandedOn()
        {
            var e = Volatile.Read(ref _heldUnkeyTailWatch);
            if (e == null) return;
            var ticket = e.Ticket;
            if (ticket == null || !ticket.TailOut) return;
            if (Interlocked.Exchange(ref e.TailSeen, 1) != 0) return;
            Interlocked.CompareExchange(ref _heldUnkeyTailWatch, null, e);
            long sinceRelease = (Stopwatch.GetTimestamp() - e.ArmedTimestamp) * 1000 / Stopwatch.Frequency;
            Volatile.Write(ref e.TailHandedOnMs, sinceRelease);
            // The margin after the tail, but never sooner than the ruled delay times 1.1 from the release.
            int wait = (int)Math.Max(e.MarginMs, e.MinMs - sinceRelease);
            var scheduler = HeldUnkeyScheduler;
            if (scheduler != null) { scheduler(wait, e.TailEnd); return; }
            try { e.TailTimer?.Change(wait, Timeout.Infinite); }
            catch (ObjectDisposedException) { } // the hold ended meanwhile
        }

        /// <summary>The hold's end (the margin after the tail, or the backstop): unkey, unless something ended the hold first.</summary>
        private void FinishHeldUnkey(HeldUnkeyEpisode e, bool backstop)
        {
            string outcome;
            lock (_heldUnkeyLock)
            {
                if (!_heldUnkeyPending || !ReferenceEquals(_heldUnkey, e)) return;
                _heldUnkeyPending = false;
                _heldUnkey = null;
                _heldUnkeyGeneration++;
                Interlocked.CompareExchange(ref _heldUnkeyTailWatch, null, e);
                if (!HeldUnkeyConnectionCurrent(e))
                {
                    // The connection the hold began on is gone: nothing to unkey.
                    // The drain goes with it.
                    txConditioner.DrainInput = false;
                    outcome = "the connection it began on is gone; nothing sent";
                }
                else
                {
                    // Enqueued under the lock, so a key-down that arrives now is
                    // ordered after this unkey and keys the radio again. The
                    // drain stays until the radio reports it has unkeyed, a
                    // key-down, or an unkeyed buffer: the radio is still on the
                    // air until the unkey reaches it.
                    EnqueueHeldUnkey(e);
                    long elapsed = (Stopwatch.GetTimestamp() - e.ArmedTimestamp) * 1000 / Stopwatch.Frequency;
                    long tail = Volatile.Read(ref e.TailHandedOnMs);
                    outcome = backstop
                        ? "the BACKSTOP ended it after " + elapsed + " ms (" + (tail < 0 ? "the drained tail was never seen handed on" : "the tail was handed on at " + tail + " ms")
                          + "; drained " + (e.Ticket?.DrainedSamples ?? 0) + " of " + (e.Ticket?.NeededSamples ?? 0) + " samples)"
                        : "unkeyed after " + elapsed + " ms (the drained tail handed on at " + tail + " ms, plus the margin; at least " + e.MinMs + " ms)";
                }
            }
            DisposeHeldUnkeyTimers(e);
            Tracing.TraceLine("FlexBase: held unkey by " + e.Who + ": " + outcome, backstop ? TraceLevel.Warning : TraceLevel.Info);
            try { CancelSlicePlayback(); } catch { }
        }

        /// <summary>
        /// Whether the connection a hold began on is still this rig's: the same
        /// radio object and the rig not disposed. Deliberately NOT
        /// <c>Disconnecting</c>: that flag is set by Disconnect and never cleared,
        /// and an unkey must never be withheld while the radio may still be
        /// keyed; Disconnect has already ended the hold itself, unkeying.
        /// </summary>
        private bool HeldUnkeyConnectionCurrent(HeldUnkeyEpisode e) =>
            !disposed && ReferenceEquals(theRadio, e.Radio);

        /// <summary>
        /// Queue the hold's Mox off, bound to the connection the hold began on
        /// AND to its over: at its run it writes only if that radio is still this
        /// rig's, still connected, the rig is not disposed, and the radio has not
        /// meanwhile reported our over ended followed by a key-down nothing of
        /// ours explains (rulings repair 2, Codex's P2; join repair 3, Codex's
        /// P1: a report our own key request explains never counts). It never
        /// dereferences a radio that has gone.
        /// </summary>
        private void EnqueueHeldUnkey(HeldUnkeyEpisode e)
        {
            var radio = e.Radio;
            string who = e.Who;
            var over = TxOverProvenanceNow.Capture();
            try
            {
                q.Enqueue((FunctionDel)(() =>
                {
                    var now = theRadio;
                    if (radio == null || !ReferenceEquals(now, radio) || disposed || !radio.Connected)
                    {
                        Tracing.TraceLine("FlexBase: the held unkey by " + who + " found its connection gone at its run; nothing sent", TraceLevel.Info);
                        return;
                    }
                    if (TxOverProvenanceNow.OverEndedAndForeignBegunSince(over))
                    {
                        Tracing.TraceLine("FlexBase: the held unkey by " + who + " found its over ended by the radio and a new"
                            + " one begun that JJ Flexible did not key; nothing sent", TraceLevel.Info);
                        return;
                    }
                    WriteOwnUnkey(radio, "the held unkey by " + who);
                }), "Mox");
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FlexBase: the held unkey could not be sent: " + ex.Message, TraceLevel.Error);
            }
        }

        /// <summary>
        /// Our own unkey write, from the command queue's thread (join repair 3,
        /// invariant (c): our own over always gets our unkey). FlexLib's
        /// <c>Mox</c> setter sends <c>xmit 0</c> only when its cached Mox
        /// differs, and a STALE receive report (the radio before our key request
        /// reached it) clears that cache while the radio is about to go on the
        /// air for us. Then the setter would send nothing and our over would
        /// stay keyed. So when the cache already says off but our key request is
        /// still unconfirmed, the <c>xmit 0</c> is sent as a command of its own.
        /// When the cache says off and nothing of ours is outstanding, the radio
        /// is off and nothing is sent, as before.
        /// </summary>
        private void WriteOwnUnkey(Radio radio, string who)
        {
            var before = TxOverProvenanceNow.Now;
            int was = BeginOwnMoxWrite(false);
            try
            {
                if (radio.Mox) radio.Mox = false;
                else if (before == TxOverProvenance.Phase.KeyRequested || before == TxOverProvenance.Phase.OnAir)
                {
                    Tracing.TraceLine("FlexBase: " + who + ": the radio's last report said receive while our key request was"
                        + " unconfirmed (a stale report); xmit 0 sent as its own command", TraceLevel.Warning);
                    SendOwnUnkeyCommand(radio);
                }
            }
            finally { EndOwnMoxWrite(was); }
        }

        /// <summary>Tests only: stands in for the radio's command path when an unkey is sent as its own command.</summary>
        internal Action<Radio> OwnUnkeyCommandForTest { get; set; }

        private void SendOwnUnkeyCommand(Radio radio)
        {
            var forTest = OwnUnkeyCommandForTest;
            if (forTest != null) { forTest(radio); return; }
            try { radio.SendReplyCommand((seq, code, reply) => { }, "xmit 0"); }
            catch (Exception ex) { Tracing.TraceLine("FlexBase: xmit 0 could not be sent: " + ex.Message, TraceLevel.Error); }
        }

        private static void DisposeHeldUnkeyTimers(HeldUnkeyEpisode e)
        {
            if (e == null) return;
            try { e.TailTimer?.Dispose(); } catch { }
            try { e.BackstopTimer?.Dispose(); } catch { }
        }

        /// <summary>
        /// Every direct write to <see cref="Transmit"/> ends a held unkey: a
        /// key-down means the over carries on, and an immediate unkey goes now.
        /// A key-down ALWAYS reopens the microphone (Codex's P1): the drain is
        /// cleared whether or not a hold is still waiting, because a hold that
        /// already sent its unkey leaves the drain on until something observes
        /// receive, and a new over can begin before anything does.
        /// </summary>
        private void EndHeldUnkeyBy(bool keyDown)
        {
            HeldUnkeyEpisode ended;
            lock (_heldUnkeyLock)
            {
                ended = _heldUnkeyPending ? _heldUnkey : null;
                _heldUnkeyPending = false;
                _heldUnkey = null;
                _heldUnkeyGeneration++;
                Volatile.Write(ref _heldUnkeyTailWatch, null);
                if (keyDown && txConditioner != null) txConditioner.DrainInput = false;
            }
            DisposeHeldUnkeyTimers(ended);
            if (ended == null) return;
            Tracing.TraceLine(keyDown
                ? "FlexBase: a key-down during the held unkey; the over carries on"
                : "FlexBase: an immediate unkey ended the held unkey", TraceLevel.Info);
        }

        /// <summary>
        /// The hold's episode or connection ended (Codex's P2): the radio
        /// reported its unkey, its interlock went to receive, the connection is
        /// going, the rig is disposed, or the remote audio stopped. Any hold
        /// waiting ends now. With <paramref name="unkeyNow"/>, a hold that was
        /// waiting sends its unkey at once, on the connection it began on, if
        /// that is still this rig's; otherwise nothing is written.
        /// <para>The drain ends here only WITHOUT <paramref name="unkeyNow"/>
        /// (the radio reported itself off, or the rig is disposed), or when
        /// there is no connection at all. With it, while a connection exists, the
        /// radio may still be on the air until our unkey reaches it, so the drain
        /// stays (join repair 3, invariant (a)): nothing captured after the
        /// release goes out meanwhile. It ends as it always does, at the radio's
        /// report that it is off, a key-down, or the first buffer the key reads
        /// down.</para>
        /// </summary>
        internal void CancelHeldUnkey(string why, bool unkeyNow)
        {
            HeldUnkeyEpisode ended;
            bool sent = false;
            lock (_heldUnkeyLock)
            {
                ended = _heldUnkeyPending ? _heldUnkey : null;
                _heldUnkeyPending = false;
                _heldUnkey = null;
                _heldUnkeyGeneration++;
                Volatile.Write(ref _heldUnkeyTailWatch, null);
                try { if (txConditioner != null && !(unkeyNow && theRadio != null)) txConditioner.DrainInput = false; } catch { }
                if (ended != null && unkeyNow && ended.Radio != null && ReferenceEquals(theRadio, ended.Radio) && !disposed)
                {
                    EnqueueHeldUnkey(ended);
                    sent = true;
                }
            }
            DisposeHeldUnkeyTimers(ended);
            if (ended == null) return;
            Tracing.TraceLine("FlexBase: the held unkey by " + ended.Who + " ended: " + why
                + (sent ? "; unkeyed now" : "; nothing sent"), TraceLevel.Info);
        }

        /// <summary>The radio reported its Mox off. When we did not unkey it ourselves, a hold that was waiting is moot: nothing is written.</summary>
        private void NoteRadioUnkeyedForHold(string what) => CancelHeldUnkey("the radio reported " + what, unkeyNow: false);

        // ── A queued held unkey belongs to its over (rulings repair 2, Codex's
        // P2; join repair 3, Codex's P1) ──
        //
        // The command queue orders OUR writes, so a key-down of ours queued after
        // the unkey always runs after it. It does not order what the RADIO
        // reports: another client's MOX, or the radio itself, can end the over
        // and begin a new one while the unkey still waits in the queue. So the
        // queued unkey carries a token of the over it ends, and at its run it
        // writes nothing only on positive evidence that the over is someone
        // else's (TxOverProvenance).
        //
        // WHICH REPORTS ARE OURS. FlexLib's Mox setter raises its change on the
        // writing thread, synchronously (Radio.Mox), so an edge raised while one
        // of our own writes is on the stack is ours. That proves only the
        // setter's own echo. A later STATUS can still be ours: FlexLib's
        // InterlockState setter moves Mox too, and a stale receive followed by
        // the Transmitting that confirms our own xmit 1 used to read as someone
        // else's unkey and key-down, withholding our only unkey. Each status is
        // now read against our latest request: a report our request explains
        // (a stale receive, the confirmation, our unkey in progress) never
        // counts. When in doubt, the unkey goes.

        private int _ownMoxWriteThread;
        private TxOverProvenance _txOverProvenanceObject;

        /// <summary>Who caused what the radio reports. Made on first use, so an object built without its constructor (the windowless tests) still has one.</summary>
        internal TxOverProvenance TxOverProvenanceNow =>
            Volatile.Read(ref _txOverProvenanceObject)
            ?? Interlocked.CompareExchange(ref _txOverProvenanceObject, new TxOverProvenance(), null)
            ?? _txOverProvenanceObject;

        /// <summary>
        /// Mark our own Mox write as on the stack of this thread, and record it
        /// as our request (a key-down opens our over's phase; an unkey asks for
        /// its end). Returns what to restore.
        /// </summary>
        private int BeginOwnMoxWrite(bool keyDown)
        {
            if (keyDown) TxOverProvenanceNow.OwnKeyDownWritten();
            else TxOverProvenanceNow.OwnUnkeyWritten();
            int was = Volatile.Read(ref _ownMoxWriteThread);
            Volatile.Write(ref _ownMoxWriteThread, Environment.CurrentManagedThreadId);
            return was;
        }

        private void EndOwnMoxWrite(int was) => Volatile.Write(ref _ownMoxWriteThread, was);

        /// <summary>True while one of our own Mox writes is on this thread's stack: the change being raised is its echo.</summary>
        private bool OwnMoxWriteOnThisThread => Volatile.Read(ref _ownMoxWriteThread) == Environment.CurrentManagedThreadId;

        /// <summary>
        /// From noteRadioMox, on a real edge: read it against our request unless
        /// our own write raised it. Returns true when the edge is a receive
        /// report our own unconfirmed key request explains (stale): it does not
        /// end our over.
        /// </summary>
        private bool NoteTxMoxEdge(bool mox)
        {
            if (OwnMoxWriteOnThisThread) return false; // our own write's synchronous change
            InterlockState state;
            try { state = theRadio?.InterlockState ?? InterlockState.None; }
            catch { state = InterlockState.None; }
            var p = TxOverProvenanceNow;
            bool stale = !mox && p.ReceiveReportIsStale(state);
            var reading = p.MoxReported(mox, state);
            if (stale)
                Tracing.TraceLine("FlexBase: the radio reported its Mox off (interlock " + state + ") before confirming our key request;"
                    + " a stale report, so our over carries on", TraceLevel.Info);
            else if (reading != TxOverProvenance.Reading.Explained)
                Tracing.TraceLine("FlexBase: the radio reported " + (mox ? "a key-down" : "an over ended")
                    + " that JJ Flexible did not request (interlock " + state + ")", TraceLevel.Verbose);
            return stale;
        }

        /// <summary>
        /// The radio reported a change of its interlock state (the property
        /// handler's InterlockState case, extracted so it can be driven without
        /// a radio). A restore withheld while keyed may go at receive (#835); a
        /// receive report ends a held unkey, unkeying NOW (rulings repair 1,
        /// Codex's P2), unless it is a stale report our own unconfirmed key
        /// request explains (join repair 3): then our over carries on, and its
        /// own unkey (or the backstop) ends it.
        /// </summary>
        private void noteRadioInterlock(InterlockState state)
        {
            var p = TxOverProvenanceNow;
            bool stale = p.ReceiveReportIsStale(state);
            p.InterlockReported(state);
            if (TxOverProvenance.IsTransmitState(state)) return;
            _checkRestorations?.Kick();
            if (stale)
            {
                Tracing.TraceLine("FlexBase: interlock " + state + " reported before our key request was confirmed;"
                    + " a stale report, so a held unkey carries on", TraceLevel.Info);
                return;
            }
            CancelHeldUnkey("its interlock reported " + state, unkeyNow: true);
        }

        /// <summary>
        /// The transmit chain's key: our Mox or the radio's interlock, and also,
        /// while a held unkey's drain is in force, our own key request the radio
        /// has not yet confirmed (join repair 3): a stale receive report must not
        /// end the drain and let new microphone audio reach a radio that is about
        /// to be on the air for our over. Lock-free: asked per buffer.
        /// </summary>
        private bool TransmitChainKeyed
        {
            get
            {
                if (TransmitKeyedOrInterlock) return true;
                var cond = txConditioner;
                return cond != null && cond.DrainInput && TxOverProvenanceNow.OwnKeyUnconfirmed;
            }
        }
    }
}
