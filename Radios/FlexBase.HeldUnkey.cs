using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
        // timeout's ordinary end, a check's planned stop) come here; the radio
        // stays keyed for the delay the chain reports times 1.1, then unkeys.
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
        // reduction (TxAudioConditioner.DrainInput); what comes out is the audio
        // captured before the release, delayed by the engines, then silence. The
        // first unkeyed buffer ends the drain. The release point is a capture
        // buffer, not a sample: the part of the buffer in flight at the release
        // is silenced with the rest, which loses at most one capture period of
        // the last word, as an unheld unkey always did.

        /// <summary>The margin the hold adds to the measured delay (S16: "plus say a 10%").</summary>
        public const double HeldUnkeyMargin = 1.1;

        private object _heldUnkeyLockObject;
        // Made on first use, so an object built without its constructor (the
        // windowless tests) still has one.
        private object _heldUnkeyLock =>
            Volatile.Read(ref _heldUnkeyLockObject)
            ?? Interlocked.CompareExchange(ref _heldUnkeyLockObject, new object(), null)
            ?? _heldUnkeyLockObject;
        private long _heldUnkeyGeneration;
        private bool _heldUnkeyPending;

        /// <summary>
        /// How a held unkey waits: run the action after the given milliseconds.
        /// Tests replace it to run the end by hand; the app waits on a timer.
        /// </summary>
        internal Action<int, Action> HeldUnkeyScheduler { get; set; }

        private static void ScheduleHeldUnkey(int ms, Action end) =>
            Task.Delay(ms).ContinueWith(_ => end(), TaskScheduler.Default);

        /// <summary>The hold for a measured delay: the delay times <see cref="HeldUnkeyMargin"/>, rounded up to a whole millisecond; none at zero.</summary>
        public static int HeldUnkeyMs(double delayMs) =>
            delayMs <= 0 || double.IsNaN(delayMs) ? 0 : (int)Math.Ceiling(Math.Round(delayMs * HeldUnkeyMargin, 6)); // rounded first: 100 x 1.1 is 110.00000000000001 in binary

        /// <summary>Whether an unkey is being held for the noise reduction's delay right now.</summary>
        public bool HeldUnkeyPending { get { lock (_heldUnkeyLock) return _heldUnkeyPending; } }

        /// <summary>The last hold begun, in milliseconds, for tests and the trace's neighbours.</summary>
        public int LastHeldUnkeyMs { get; private set; }

        /// <summary>
        /// Unkey our Mox as an ordinary end of an over (S16). When transmit
        /// noise reduction is adding delay to the audio going out now, the radio
        /// stays keyed for that delay times 1.1 while the chain drains, then
        /// unkeys; otherwise it unkeys at once, exactly as <c>Transmit =
        /// false</c>. A key-down during the hold ends it and the over carries on.
        /// One trace line per held unkey, with the hold in milliseconds.
        /// </summary>
        /// <param name="who">What released, for the trace.</param>
        public void UnkeyAfterNoiseDelay(string who)
        {
            double delayMs = 0;
            try { delayMs = txConditioner?.RecentNoiseDelayMs(Environment.TickCount64) ?? 0; }
            catch { delayMs = 0; }
            int hold = HeldUnkeyMs(delayMs);
            bool keyed;
            try { keyed = TransmitKeyedOrInterlock; } catch { keyed = false; }
            // A generated source that stands the chain aside (the calibrated
            // tone) is not the microphone's tail: nothing of it is delayed.
            bool bypassingSource = false;
            try { bypassingSource = TxInputSources != null && TxInputSources.Engaged && TxInputSources.BypassesConditioning; }
            catch { bypassingSource = false; }
            if (hold <= 0 || !keyed || bypassingSource || txConditioner == null)
            {
                Transmit = false;
                return;
            }
            long generation;
            lock (_heldUnkeyLock)
            {
                generation = ++_heldUnkeyGeneration;
                _heldUnkeyPending = true;
                LastHeldUnkeyMs = hold;
            }
            txConditioner.DrainInput = true;
            Tracing.TraceLine("FlexBase: unkey by " + who + " held " + hold + " ms: transmit noise reduction adds "
                + delayMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                + " ms, held for it plus 10% while the chain drains", TraceLevel.Info);
            (HeldUnkeyScheduler ?? ScheduleHeldUnkey)(hold, () => FinishHeldUnkey(generation));
        }

        /// <summary>The hold ran out: unkey, unless a key-down or a direct write ended the hold first.</summary>
        private void FinishHeldUnkey(long generation)
        {
            lock (_heldUnkeyLock)
            {
                if (generation != _heldUnkeyGeneration || !_heldUnkeyPending) return;
                _heldUnkeyPending = false;
                _heldUnkeyGeneration++;
                // Enqueued under the lock, so a key-down that arrives now is
                // ordered after this unkey and keys the radio again.
                try { q.Enqueue((FunctionDel)(() => { theRadio.Mox = false; }), "Mox"); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("FlexBase: the held unkey could not be sent: " + ex.Message, TraceLevel.Error);
                }
            }
            try { CancelSlicePlayback(); } catch { }
        }

        /// <summary>
        /// Every direct write to <see cref="Transmit"/> ends a held unkey: a
        /// key-down means the over carries on (the drain stops, so the
        /// microphone is heard again), and an immediate unkey goes now.
        /// </summary>
        private void EndHeldUnkeyBy(bool keyDown)
        {
            bool ended;
            lock (_heldUnkeyLock)
            {
                ended = _heldUnkeyPending;
                _heldUnkeyPending = false;
                _heldUnkeyGeneration++;
            }
            if (!ended) return;
            if (keyDown && txConditioner != null) txConditioner.DrainInput = false;
            Tracing.TraceLine(keyDown
                ? "FlexBase: a key-down during the held unkey; the over carries on"
                : "FlexBase: an immediate unkey ended the held unkey", TraceLevel.Info);
        }
    }
}
