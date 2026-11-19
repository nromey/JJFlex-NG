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
        // the delay times 1.1, plus one capture period, plus
        // HeldUnkeyBackstopAllowanceMs, whatever the audio did.
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
        // newer. The capture keeps running (#565), so the conditioner silences
        // every buffer that reaches it from the release on, BEFORE the noise
        // reduction (TxAudioConditioner.BeginDrain); what comes out is the audio
        // captured before the release, delayed by the engines, then silence. The
        // release point is a capture buffer, not a sample: the part of the
        // buffer in flight at the release is silenced with the rest, which loses
        // at most one capture period of the last word, as an unheld unkey always
        // did.
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

        /// <summary>The backstop: the delay times 1.1, plus one capture period at <paramref name="callbacksPerSecond"/>, plus <see cref="HeldUnkeyBackstopAllowanceMs"/>.</summary>
        public static int HeldUnkeyBackstopMs(double delayMs, int callbacksPerSecond) =>
            HeldUnkeyMs(delayMs) + (int)Math.Ceiling(1000.0 / Math.Max(1, callbacksPerSecond)) + HeldUnkeyBackstopAllowanceMs;

        /// <summary>Whether an unkey is being held for the noise reduction's delay right now.</summary>
        public bool HeldUnkeyPending { get { lock (_heldUnkeyLock) return _heldUnkeyPending; } }

        /// <summary>The last hold begun: the delay times 1.1, in milliseconds (its minimum), for tests and the trace's neighbours.</summary>
        public int LastHeldUnkeyMs { get; private set; }

        /// <summary>The last hold's backstop, in milliseconds.</summary>
        public int LastHeldUnkeyBackstopMs { get; private set; }

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
                BackstopMs = HeldUnkeyBackstopMs(delayMs, CaptureCallbacksPerSecondNow),
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
                e.Ticket = txConditioner.BeginDrain(delaySamples);
                Volatile.Write(ref _heldUnkeyTailWatch, e);
                if (scheduler == null)
                    e.BackstopTimer = new Timer(_ => e.BackstopEnd(), null, e.BackstopMs, Timeout.Infinite);
            }
            DisposeHeldUnkeyTimers(replaced);
            Tracing.TraceLine("FlexBase: unkey by " + e.Who + " held: transmit noise reduction adds "
                + delayMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                + " ms; the radio stays keyed until the drained tail is handed on plus "
                + e.MarginMs + " ms, and at least " + hold + " ms; backstop " + e.BackstopMs + " ms at "
                + CaptureCallbacksPerSecondNow + " callbacks a second", TraceLevel.Info);
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
        /// Queue the hold's Mox off, bound to the connection the hold began on:
        /// at its run it writes only if that radio is still this rig's, still
        /// connected, and the rig is not disposed. It never dereferences a radio
        /// that has gone (Codex's P2).
        /// </summary>
        private void EnqueueHeldUnkey(HeldUnkeyEpisode e)
        {
            var radio = e.Radio;
            string who = e.Who;
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
                    radio.Mox = false;
                }), "Mox");
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FlexBase: the held unkey could not be sent: " + ex.Message, TraceLevel.Error);
            }
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
        /// waiting ends now and the drain with it. With <paramref name="unkeyNow"/>,
        /// a hold that was waiting sends its unkey at once, on the connection it
        /// began on, if that is still this rig's; otherwise nothing is written.
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
                try { if (txConditioner != null) txConditioner.DrainInput = false; } catch { }
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
    }
}
