using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.StationConnect;

namespace Radios
{
    public partial class FlexBase
    {
        // ── A cached frequency and the slice it belongs to, as one value ──────
        //
        // #642, repair round 2 (Codex's re-check). The receive and transmit
        // frequency caches were bare numbers, and which slice each described
        // was inferred elsewhere: the receive cache from "this slice is
        // Active", the transmit cache from a separate _txCacheSlice written
        // just before it. Selecting a slice moves RXVFO at once and queues
        // Active; FlexLib raises the slice's Active BEFORE the radio's
        // ActiveSlice, and only the latter refreshed the receive cache. In
        // between, the newly selected slice was Active while the cache still
        // held the old slice's frequency, and F, the title and the notice
        // before a door keys read one slice's letter with another's number.
        //
        // So each cache is now ONE immutable value carrying both the frequency
        // and the slice it was written for, published by a single reference
        // write. A reader takes it once and believes the number only for that
        // very slice; for any other slice it reads the slice's own frequency.
        // Optimistic tuning writes keep their value with the slice they were
        // written to, so a step still reads back at once.

        // Repair round 3 (Codex's second re-check, ruled by Noel 2026-11-04 to
        // be fixed in this round). Three more gaps in the same core:
        //
        // - A queued tune resolved its slice when it RAN (VFOToSlice(RXVFO)
        //   inside the closure), so a step pressed on A landed on B selected
        //   meanwhile. Every tune is now bound to the slice it was pressed on
        //   (WriteTuneTo), and a slice gone by then gets nothing.
        // - The echo guard armed against the raw receive cache, which during a
        //   selection still held the OLD slice's number, so the new slice's own
        //   stale report rewound a step. The guard is now one per slice
        //   (EchoGuardFor), armed against that slice's own value, and every
        //   refresh of a cache from a slice's own number asks it
        //   (ObservedOrPending), including the ActiveSlice refresh.
        // - The receive and transmit caches could hold different optimistic
        //   values for ONE slice, so F and the title disagreed. A tune now
        //   publishes the slice's one new value to every cache that describes
        //   it (PublishTuning), and a reader takes the newer of the two when
        //   both describe the slice it asks about (ValueOf), so one slice has
        //   one number by construction.

        // Repair round 4 (Codex's third re-check):
        //
        // - FlexLib's Freq setter raises the slice's Freq notification itself,
        //   synchronously, after SENDING and before any reply. The guard took
        //   that as the radio's confirmation, disarmed as the queue drained,
        //   and the radio's first delayed report then rewound a fully sent run
        //   of steps. WriteTuneTo marked its own write (a thread-static marker,
        //   gone in round 5 with the setter itself), the handler never settled
        //   the guard on it, and a refresh from the
        //   slice's own number (ObservedOrPending) only reads the pending
        //   value, never settles: the vendor's number is our own send by then.
        //   Only a report from the radio confirms.
        // - A slice's pending value is its value even when neither cache
        //   describes it any more (ValueOf falls back to the guard's pending
        //   value before the slice's own number), so returning to a slice with
        //   a step still on its way does not lose the step.
        // - The panadapter's goto and a slice copy tune through the same
        //   setters and the same WriteTuneTo, so they are bound to the
        //   operator's selection at the press and checked for the slice's
        //   lifetime at the write.

        // Repair round 5 (Codex's fourth re-check). Round 4's premise was that
        // only the radio's own report settles the guard, and the vendor does
        // not deliver that report in the commonest case: FlexLib's Freq setter
        // puts our value in its cache before sending, so the radio's report of
        // an ordinary success equals the cache and is skipped. The setter is
        // no longer used to tune. RequestTune owns the whole request (the
        // slice at the press, the lock, the guard's id, the one published
        // value), WriteTuneTo sends it through the reply-bearing command path
        // with that id, and the reply comes back to TuneReplied with it. So
        // FlexLib's cache holds only what the radio reported, every changed
        // value's report is raised and settles the guard, the reply is the
        // receipt when nothing changed, and a refusal (locked, gone, transport
        // down, error reply) concedes to the radio's report at once.

        private static long _cacheSeq;

        /// <summary>A frequency and the slice it belongs to. Immutable.</summary>
        private sealed class SliceHz
        {
            public SliceHz(Slice slice, ulong hz)
            {
                Slice = slice;
                Hz = hz;
                Seq = System.Threading.Interlocked.Increment(ref _cacheSeq);
            }
            /// <summary>The slice this frequency is about; null when written with no slice to name.</summary>
            public Slice Slice { get; }
            public ulong Hz { get; }
            /// <summary>Publication order, so a reader can tell which of two caches is newer.</summary>
            public long Seq { get; }
        }

        /// <summary>The receive cache: the active slice's frequency, or an operator's write to the selected slice.</summary>
        private volatile SliceHz _rxCache;

        /// <summary>The transmit caches' frequency and the designated slice it was read from.</summary>
        private volatile SliceHz _txCache;

        /// <summary>The receive cache's number, whichever slice it belongs to. For the echo guard's "what it was".</summary>
        private ulong _RXFrequency => _rxCache?.Hz ?? 0UL;

        /// <summary>The transmit cache's number, whichever slice it belongs to.</summary>
        private ulong _TXFrequency => _txCache?.Hz ?? 0UL;

        /// <summary>The slice the transmit caches describe.</summary>
        private Slice _txCacheSlice => _txCache?.Slice;

        /// <summary>Publish the receive cache for <paramref name="slice"/>, number and identity together.</summary>
        private void SetRxCache(Slice slice, ulong hz) => _rxCache = new SliceHz(slice, hz);

        /// <summary>Publish the transmit cache for <paramref name="slice"/>, number and identity together.</summary>
        private void SetTxCache(Slice slice, ulong hz) => _txCache = new SliceHz(slice, hz);

        /// <summary>
        /// <paramref name="slice"/>'s one frequency (repair round 8): our
        /// latest request's value while the radio has not answered it, else
        /// the radio's last report, which is FlexLib's own number now that
        /// tunes do not go through its setter. The guard is asked ONCE, so a
        /// reader on another thread never sees a request half published. The
        /// caches are not consulted for a slice: they hold only reports the
        /// guard let in and our own publications, both older than or equal
        /// to one of these two.
        /// </summary>
        private ulong ValueOf(Slice slice)
        {
            return EchoGuardFor(slice).Read(out ulong pending) == FrequencyEchoGuard.Reading.Pending
                ? pending : LibFreqtoLong(slice.Freq);
        }

        /// <summary>Each slice's own echo guard (#266, made per slice in repair round 3).</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Slice, FrequencyEchoGuard> _freqEcho = new();

        private FrequencyEchoGuard EchoGuardFor(Slice slice) =>
            _freqEcho.GetValue(slice, s => new FrequencyEchoGuard("slice " + (s.Letter ?? s.Index.ToString())));

        /// <summary>
        /// What a cache should hold for <paramref name="slice"/> when it is
        /// refreshed from the slice's own number <paramref name="reported"/>:
        /// our own tune still on its way while one is pending, otherwise the
        /// number. A refresh is not a report from the radio, so it never
        /// settles the guard; only the radio's own Freq report, or the tune's
        /// reply, does.
        /// </summary>
        private ulong ObservedOrPending(Slice slice, ulong reported)
        {
            return EchoGuardFor(slice).TryPending(out ulong pending) ? pending : reported;
        }

        /// <summary>
        /// A tune's optimistic value, published as the slice's ONE value: to
        /// the receive cache when this is the selected slice's tune or the
        /// receive cache already describes it, and to the transmit cache when
        /// the slice is designated for transmit.
        /// </summary>
        private void PublishTuning(Slice slice, ulong hz, bool selectedTune)
        {
            if (selectedTune || ReferenceEquals(_rxCache?.Slice, slice) || ReferenceEquals(SelectedSliceNow(), slice))
                SetRxCache(slice, hz);
            if (slice.IsTransmitSlice)
            {
                if (!ReferenceEquals(_txCacheSlice, slice)) NoteTransmitSliceAsFound(slice);
                SetTxCache(slice, hz);
            }
        }

        /// <summary>
        /// One tune request, whole (repair round 5). Every route that tunes a
        /// slice comes here, so none of them can tune without the guard's
        /// admission. <paramref name="slice"/> is taken by the caller at the
        /// press. A locked slice is refused here and nothing is published. The
        /// guard is armed FIRST, so a reader on another thread sees the
        /// request as pending before any cache moves; then the value is
        /// published; then the send is queued carrying this request's id.
        /// </summary>
        private void RequestTune(Slice slice, ulong value, bool selectedTune, string what)
        {
            if (slice.Lock)
            {
                Tracing.TraceLine(what + ": slice " + slice.Letter + " is locked, so its tune to " + value
                    + " was not sent", System.Diagnostics.TraceLevel.Info);
                return;
            }
            long id = EchoGuardFor(slice).Requested(value);
            // The optimistic value is held WITH the slice it is written to, in
            // every cache that describes that slice.
            PublishTuning(slice, value, selectedTune);
            double mhz = LongFreqToLibFreq(value);
            q.Enqueue((FunctionDel)(() => WriteTuneTo(slice, id, mhz, what)), what);
        }

        /// <summary>
        /// The queued half of a tune, bound to the slice it was pressed on and
        /// to the request it belongs to. A slice no longer ours, or locked, by
        /// the time it runs gets nothing, and no other slice gets it either.
        /// It goes out through the reply-bearing path, not FlexLib's Freq
        /// setter (repair round 5): the command that setter would send,
        /// through the slice's own radio.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The roster's lock is held for the CHECK only, never across the
        /// write (repair round 7).</b> Round 6 held it through the socket
        /// write, so that a removal on another thread waited for the write.
        /// That bought nothing the radio can see and cost availability: the
        /// write is synchronous and unbounded (FlexLib's TCP and TLS writes
        /// have no timeout), and the UI's 100 ms frequency poll, F and slice
        /// removal all take this lock, so a stalled write stalled all three.
        /// </para>
        /// <para>
        /// <b>Why a client lock cannot close the radio's removal.</b> A
        /// removal we learn of from the radio's status has already happened
        /// at the radio before the status left it. Ordering our write against
        /// our PROCESSING of that status changes only which side of our own
        /// handler the write falls on; the radio receives the command after its
        /// removal either way, and may already have reused the index. That
        /// window is inherent and stays on the register.
        /// </para>
        /// <para>
        /// <b>The removals we start are ordered without a lock.</b> Our own
        /// slice close (<c>RemoveSlice</c>, the loopback ears' release) runs
        /// <c>Slice.Close</c> on this same command queue, and FlexLib removes
        /// the slice and raises our removal inside that call, so a tune queued
        /// after the close finds the slice gone and one queued before it is
        /// written first. A removal we send as a raw command (the listenback's
        /// restore of a slice it created) marks the slice first
        /// (<see cref="NoteRemovingSlice"/>), on the same queue, and a later
        /// tune of it is refused here.
        /// </para>
        /// </remarks>
        private void WriteTuneTo(Slice slice, long id, double mhz, string what)
        {
            var guard = EchoGuardFor(slice);
            string refused = null;
            var slices = mySlices;
            if (slices == null) refused = "the slice is gone";
            else
            {
                bool ours;
                lock (slices) ours = slices.Contains(slice);
                if (!ours) refused = "the slice is gone";
                else if (BeingRemovedByUs(slice)) refused = "the slice is being removed";
                else if (slice.Lock) refused = "the slice is locked";
            }
            if (refused != null)
            {
                Tracing.TraceLine(what + ": " + refused + " (slice " + slice.Letter + "), so its tune to " + mhz
                    + " was not sent to any slice", System.Diagnostics.TraceLevel.Info);
                if (guard.Refused(id, refused) && refused != "the slice is gone") Reconcile(slice, LibFreqtoLong(mhz));
                return;
            }
            string command = "slice tune " + slice.Index + " " + Flex.Util.StringHelper.DoubleToString(mhz, "f6");
            if (!slice.AutoPan) command += " autopan=0";
            // Marked before the write, so the reply cannot come back ahead of it.
            guard.Sending(id);
            // No lock of ours is held across this write (repair round 7).
            ulong hz = LibFreqtoLong(mhz);
            string refusal = SendRadioCommandWithReply(slice.Radio, command, reply => TuneReplied(slice, id, hz, reply));
            if (refusal != null)
            {
                Tracing.TraceLine(what + ": slice " + slice.Letter + "'s tune to " + mhz + " was not sent: " + refusal,
                    System.Diagnostics.TraceLevel.Info);
                if (guard.Refused(id, refusal)) Reconcile(slice, hz);
            }
        }

        /// <summary>
        /// Slices we have asked the radio to remove by a raw command, with the
        /// time the mark was made (repair round 7; given a lifecycle in round 8).
        /// </summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Slice, object> _removingByUs = new();

        /// <summary>
        /// How long a removal mark refuses tunes when the removal is never
        /// answered (repair round 8). A removal that takes effect takes the
        /// slice out of the roster long before this; one that never does is
        /// not allowed to ban the slice for good.
        /// </summary>
        internal const int RemovalMarkMs = 10_000;

        /// <summary>The removal marks' clock. Injectable for tests.</summary>
        internal System.Func<long> RemovalMarkClock = () => System.Environment.TickCount64;

        /// <summary>
        /// We are about to send the radio a command that removes
        /// <paramref name="slice"/> (repair round 7). Call on the command
        /// queue, before the command goes out, so any tune queued after it is
        /// refused rather than sent to an index the radio is about to free.
        /// The mark is cleared by <see cref="ClearRemovingSlice"/> when the
        /// removal is rejected or not sent, and lapses after
        /// <see cref="RemovalMarkMs"/> if it is never answered (repair round 8).
        /// </summary>
        internal void NoteRemovingSlice(Slice slice)
        {
            if (slice != null) _removingByUs.AddOrUpdate(slice, RemovalMarkClock());
        }

        /// <summary>The removal of <paramref name="slice"/> was rejected or never sent: it may be tuned again (repair round 8).</summary>
        internal void ClearRemovingSlice(Slice slice)
        {
            if (slice != null) _removingByUs.Remove(slice);
        }

        private bool BeingRemovedByUs(Slice slice) =>
            _removingByUs.TryGetValue(slice, out object at) && at is long t && RemovalMarkClock() - t < RemovalMarkMs;

        /// <summary>
        /// The radio's reply to tune request <paramref name="id"/>, on the
        /// receive thread (repair round 8). Success answers the request: the
        /// window ends, and readers read the radio's report, unless the radio
        /// has said nothing at all since the write and does not yet report our
        /// value, in which case the guard waits for that report. An error
        /// means nothing changed. When the window ends on a number other than
        /// ours, the slice's handler is run again so the caches, the filter
        /// edges and the where-you-are record follow the radio.
        /// </summary>
        private void TuneReplied(Slice slice, long id, ulong requestedHz, CommandReply reply)
        {
            var guard = EchoGuardFor(slice);
            bool closed;
            if (reply.Acknowledged) closed = guard.Acknowledged(id, LibFreqtoLong(slice.Freq));
            else
            {
                Tracing.TraceLine("tune reply: slice " + slice.Letter + " " + reply, System.Diagnostics.TraceLevel.Info);
                closed = guard.Refused(id, "the radio answered " + reply);
            }
            if (closed) Reconcile(slice, requestedHz);
        }

        /// <summary>
        /// The window on <paramref name="slice"/> has just ended (repair round
        /// 8). If the radio's last report is not the number we asked for, run
        /// the slice's Freq handler again: the guard is Idle now, so the report
        /// is let in, and everything derived from it follows.
        /// </summary>
        private void Reconcile(Slice slice, ulong requestedHz)
        {
            if (LibFreqtoLong(slice.Freq) == requestedHz) return;
            try { slicePropertyChangedHandler(slice, new System.ComponentModel.PropertyChangedEventArgs("Freq")); }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("tune reconcile: slice " + slice.Letter + ": " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>The slice at RXVFO, under the roster's lock; null when there is none.</summary>
        private Slice SelectedSliceNow()
        {
            var slices = mySlices;
            if (slices == null) return null;
            lock (slices)
            {
                int rx = _RXVFO;
                return (rx >= 0 && rx < slices.Count) ? slices[rx] : null;
            }
        }
    }
}
