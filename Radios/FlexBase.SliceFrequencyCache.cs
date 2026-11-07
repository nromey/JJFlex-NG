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

        /// <summary>
        /// The receive and transmit caches' numbers, whichever slice they
        /// belong to (for tests). Named apart from AllRadios' _RXFrequency and
        /// _TXFrequency, which the private shims of these names used to hide
        /// (integration fix, 2026-11-06).
        /// </summary>
        internal ulong RxCacheHz => _rxCache?.Hz ?? 0UL;
        internal ulong TxCacheHz => _txCache?.Hz ?? 0UL;

        /// <summary>The slice the transmit caches describe.</summary>
        private Slice _txCacheSlice => _txCache?.Slice;

        /// <summary>
        /// THE commit boundary for every fact cached about a slice (repair
        /// round 11): the receive and transmit frequency caches, the receive
        /// and transmit modes, RIT and XIT, and the role positions written
        /// with them. Takes the roster's lock, which <c>sliceRemoved</c> also
        /// takes, and runs <paramref name="commit"/> only while
        /// <paramref name="slice"/> is in the roster (or when it is null: a
        /// forgetting, or a number that names no slice, revives nothing).
        /// Returns whether it ran. A removal therefore either clears what this
        /// wrote, or this sees the slice gone and writes nothing.
        /// </summary>
        /// <remarks>
        /// <b>Everything the commit does must stay in memory.</b> It runs under
        /// the lock the UI's 100 ms frequency poll takes: no trace, no I/O, no
        /// event that does either, and no lock that is ever held while taking
        /// the roster's. The guard reads made inside it (<c>TryPending</c>,
        /// <c>ObservedOrPending</c>) may notice a give-up; the guard then hands
        /// its trace and the follow-up to the command queue instead of writing
        /// it there and then (repair round 11). Every write to these fields
        /// outside this helper is refused by
        /// <c>TuningReceiptTests.Every_slice_fact_is_written_through_the_one_commit</c>.
        /// </remarks>
        private bool CommitSliceFacts(Slice slice, System.Action commit)
        {
            var roster = mySlices;
            if (roster == null)
            {
                if (slice != null) return false;
                commit();
                return true;
            }
            lock (roster)
            {
                if (slice != null && !roster.Contains(slice)) return false;
                commit();
                return true;
            }
        }

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
            _freqEcho.GetValue(slice, s => new FrequencyEchoGuard("slice " + (s.Letter ?? s.Index.ToString()),
                gaveUp: (requested, line) => q.Enqueue((FunctionDel)(() =>
                {
                    // The give-up's trace is written here, on the command
                    // queue, never where it was noticed: that can be under the
                    // roster's lock (repair round 11).
                    Tracing.TraceLine(line, System.Diagnostics.TraceLevel.Warning);
                    // Never spoken (J4, round two): with no answer, where the
                    // radio is is not known; the late reply, or a late refusal,
                    // settles it (TuneReplied).
                    Reconcile(s, requested, null);
                }), "TuneReconcile")));

        // Repair rounds 9 to 11 subscribed here to FlexLib's Slice.FrequencyReported
        // (MIGRATION.md item 23), to see the radio's report of our own tune
        // arrive. The 8600 bench of 2026-11-06 measured that report never comes
        // to the client that tuned (Fail 1), so nothing here waits for it any
        // more and the subscription is gone: the reply is the receipt
        // (TuneReplied, Slice.AcceptTuneReply, item 26).

        /// <summary>How many times a reconcile has run the slice's handler. For tests.</summary>
        internal int ReconcileRuns;

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
        /// <returns>True when the tune was admitted and its write will run; false
        /// when it was refused here (a locked slice, one gone, or a command loop
        /// that will not run it), and then nothing was published (integration
        /// fix: a caller must not speak a frequency it did not tune to).</returns>
        private bool RequestTune(Slice slice, ulong value, bool selectedTune, string what)
        {
            var answered = TakeTuneAnswer();   // J28: a typed tune's waiter, or null
            if (slice.Lock)
            {
                Tracing.TraceLine(what + ": slice " + slice.Letter + " is locked, so its tune to " + value
                    + " was not sent", System.Diagnostics.TraceLevel.Info);
                if (answered != null) TellTuneAnswer(answered, TuneAnswer.Refused);
                return false;
            }
            long id = EchoGuardFor(slice).Requested(value);
            if (answered != null) answered.Hz = value;   // what the typed entry asked for (round five, T1)
            WaitForTuneAnswer(slice, id, answered);
            // The optimistic value is held WITH the slice it is written to, in
            // every cache that describes that slice, through the one commit
            // (repair round 11): a slice removed since the press gets nothing
            // published, and its request is refused rather than queued.
            if (!CommitSliceFacts(slice, () => PublishTuning(slice, value, selectedTune)))
            {
                EchoGuardFor(slice).Refused(id, "the slice is gone");
                AnswerTune(slice, id, TuneAnswer.Refused);
                return false;
            }
            double mhz = LongFreqToLibFreq(value);
            // The queue's own answer (integration fix): a write it will not run
            // ends the request at once, and the caches follow the radio again.
            if (q == null || !q.Enqueue((FunctionDel)(() => WriteTuneTo(slice, id, mhz, what)), what,
                    () =>
                    {
                        // Accepted, but the loop ended before it ran (integration
                        // fix 2): conceded, as a refusal is.
                        bool typed = AnswerTune(slice, id, TuneAnswer.Refused);
                        if (EchoGuardFor(slice).Refused(id, "the command loop ended before it ran"))
                            Reconcile(slice, value, typed ? null : "the command loop ended before it ran");
                    }))
            {
                Tracing.TraceLine(what + ": the command loop will not run the tune of slice " + slice.Letter
                    + " to " + value + ", so it was not sent", System.Diagnostics.TraceLevel.Info);
                // Refused at the press: nothing was spoken for it, so nothing is
                // corrected (J4); the caches still follow the radio.
                if (EchoGuardFor(slice).Refused(id, "the command loop is not running")) Reconcile(slice, value, null);
                AnswerTune(slice, id, TuneAnswer.Refused);
                return false;
            }
            // A newer tune of ours speaks its own number: an earlier correction
            // not yet heard is retired so it is not salvaged after it (R3).
            RetireTuneCorrection("a newer tune");
            return true;
        }

        /// <summary>
        /// Whether the calling thread's last <see cref="RXFrequency"/> set was
        /// admitted as a tune (integration fix): true, false when it was refused
        /// (a locked or gone slice, no slice, a loop that will not run it), null
        /// before any. A tuning key speaks the new frequency only when true.
        /// </summary>
        public bool? TuneAdmittedHere => t_tuneAdmitted;
        [System.ThreadStatic] private static bool? t_tuneAdmitted;

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
                bool typedRefused = AnswerTune(slice, id, TuneAnswer.Refused);
                if (guard.Refused(id, refused) && refused != "the slice is gone")
                    Reconcile(slice, LibFreqtoLong(mhz), typedRefused ? null : refused);
                return;
            }
            string command = "slice tune " + slice.Index + " " + Flex.Util.StringHelper.DoubleToString(mhz, "f6");
            if (!slice.AutoPan) command += " autopan=0";
            // Marked before the write, so the reply cannot come back ahead of it.
            guard.Sending(id);
            NoAnswerAfter(slice, id, FrequencyEchoGuard.GiveUpMs);   // J28: the guard's own bound
            // No lock of ours is held across this write (repair round 7).
            ulong hz = LibFreqtoLong(mhz);
            string refusal = SendRadioCommandWithReply(slice.Radio, command, reply => TuneReplied(slice, id, hz, reply));
            if (refusal != null)
            {
                Tracing.TraceLine(what + ": slice " + slice.Letter + "'s tune to " + mhz + " was not sent: " + refusal,
                    System.Diagnostics.TraceLevel.Info);
                bool typedRefused = AnswerTune(slice, id, TuneAnswer.Refused);
                if (guard.Refused(id, refusal)) Reconcile(slice, hz, typedRefused ? null : "not sent: " + refusal);
            }
        }

        /// <summary>
        /// Slices we have asked the radio to remove by a raw command (repair
        /// round 7). Each mark is one removal attempt: its own id and the time
        /// it was made (round 8 gave it a lifecycle, round 9 an identity), so
        /// an older attempt's late answer cannot clear a newer attempt's mark.
        /// </summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Slice, RemovalMark> _removingByUs = new();

        private sealed class RemovalMark
        {
            public RemovalMark(long id, long at) { Id = id; At = at; }
            public long Id { get; }
            public long At { get; }
        }

        private static long _removalMarkSeq;

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
        internal long NoteRemovingSlice(Slice slice)
        {
            if (slice == null) return 0;
            long id = System.Threading.Interlocked.Increment(ref _removalMarkSeq);
            lock (_removingByUs) _removingByUs.AddOrUpdate(slice, new RemovalMark(id, RemovalMarkClock()));
            return id;
        }

        /// <summary>
        /// Removal attempt <paramref name="mark"/> of <paramref name="slice"/>
        /// was rejected or never sent: if its mark is still the one standing,
        /// the slice may be tuned again (repair round 8). A newer attempt's
        /// mark is left alone (repair round 9).
        /// </summary>
        internal void ClearRemovingSlice(Slice slice, long mark)
        {
            if (slice == null) return;
            lock (_removingByUs)
            {
                if (_removingByUs.TryGetValue(slice, out RemovalMark current) && current.Id == mark)
                    _removingByUs.Remove(slice);
            }
        }

        private bool BeingRemovedByUs(Slice slice) =>
            _removingByUs.TryGetValue(slice, out RemovalMark m) && RemovalMarkClock() - m.At < RemovalMarkMs;

        /// <summary>
        /// The radio's reply to tune request <paramref name="id"/>, on the
        /// receive thread (repair round 8). Success is the receipt: the
        /// window ends at the value asked for (round two of the rulings round).
        /// An error means nothing changed: the slice's handler is run again so
        /// the caches, the filter edges and the where-you-are record follow the
        /// radio's last answer, and that number is said (J4).
        /// </summary>
        private void TuneReplied(Slice slice, long id, ulong requestedHz, CommandReply reply)
        {
            var guard = EchoGuardFor(slice);
            if (reply.Acknowledged)
            {
                // The receipt (round two of the rulings round, the 8600 bench's
                // Fail 1): the radio sends the new frequency to every OTHER client
                // and none to this one. The latest request's window ends, then
                // FlexLib's cache takes the value this reply answers, latest or
                // not (the radio is there as of this reply), and its Freq
                // notification brings everything derived along. This runs on the
                // reader, in order with the radio's statuses, so one sent after
                // this reply still wins.
                guard.Acknowledged(id);
                if (guard.NewestReply(id)) PublishReply(slice, LongFreqToLibFreq(requestedHz));
                AnswerTune(slice, id, TuneAnswer.Accepted);   // J28: after the caches, so a reader sees the new number
                return;
            }
            Tracing.TraceLine("tune reply: slice " + slice.Letter + " " + reply, System.Diagnostics.TraceLevel.Info);
            bool closed = guard.Refused(id, "the radio answered " + reply);
            // J28: a typed tune's refusal is the typed entry's to say ("Unable to
            // tune this frequency"); J4's bare number is then not added to it.
            bool typed = AnswerTune(slice, id, TuneAnswer.Refused);
            // A refusal's numeric body is the frequency the radio KEPT (round
            // three, Codex's B2; the vendor's own SetFreqReply reads it the same
            // way, and CommandReply.Text says so). Published with the same order
            // discipline as a success: only a reply newer than every one already
            // put into FlexLib's cache. A non-numeric body leaves the cache as the
            // radio last answered or reported.
            if (TryKeptFrequency(reply.Text, out double keptMhz) && guard.NewestReply(id))
                PublishReply(slice, keptMhz);
            if (closed) Reconcile(slice, requestedHz, typed ? null : "refused: " + reply);
        }

        /// <summary>
        /// A reply's frequency into FlexLib's cache (MIGRATION.md item 26). Its
        /// Freq notification runs our handlers; one that throws must not stop the
        /// refusal's reconcile and its correction behind it.
        /// </summary>
        private void PublishReply(Slice slice, double mhz)
        {
            try { slice.AcceptTuneReply(mhz); }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("tune reply: slice " + slice.Letter + ": publishing " + mhz + " threw: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>
        /// The frequency a refused tune's reply says the radio kept, in MHz, when
        /// its body is a number (round three, Codex's B2). False for an empty or
        /// non-numeric body, or one that is not a plausible frequency.
        /// </summary>
        internal static bool TryKeptFrequency(string text, out double mhz)
        {
            mhz = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!Flex.Util.StringHelper.TryParseDouble(text.Trim(), out double v)) return false;
            if (double.IsNaN(v) || v <= 0 || v > 100_000) return false;
            mhz = v;
            return true;
        }

        /// <summary>
        /// The window on <paramref name="slice"/> has just ended (repair round
        /// 8). If the radio's last report is not the number we asked for, run
        /// the slice's Freq handler again: the guard is Idle now, so the report
        /// is let in, and everything derived from it follows. Then (J4) the
        /// frequency it really ended on is spoken, unless
        /// <paramref name="ending"/> is null (a tune refused at its press, which
        /// spoke nothing). Returns whether a correction was spoken.
        /// </summary>
        private bool Reconcile(Slice slice, ulong requestedHz, string ending)
        {
            if (LibFreqtoLong(slice.Freq) == requestedHz) return false;
            // Only a slice still ours (repair round 9): a late answer for a
            // slice removed meanwhile must not run its handler, which would
            // put a removed transmitter's number back into the caches.
            var slices = mySlices;
            bool ours = false;
            if (slices != null) lock (slices) ours = slices.Contains(slice);
            if (!ours)
            {
                Tracing.TraceLine("tune reconcile: slice " + slice.Letter + " is no longer ours; nothing follows",
                    System.Diagnostics.TraceLevel.Info);
                return false;
            }
            System.Threading.Interlocked.Increment(ref ReconcileRuns);
            // The handler re-checks the roster under its lock before it writes
            // a cache (repair round 10); this check only saves the work.
            try { slicePropertyChangedHandler(slice, new System.ComponentModel.PropertyChangedEventArgs("Freq")); }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("tune reconcile: slice " + slice.Letter + ": " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            }
            return ending != null && TuneEndedElsewhere(slice, requestedHz, LibFreqtoLong(slice.Freq), ending);
        }

        // ── J4: when a tune ends somewhere other than the frequency spoken ──
        //
        // RULED by Noel 2026-11-06 11:06 to 11:12 (night question J4): "If
        // there's some error / a press is missed, then just speak the updated
        // frequency rather than agonizing on how to handle reply reading." So
        // when the window on our latest tune ends (answered, refused, given up,
        // or another client's move held back during it) and the radio is NOT on
        // the number we asked for, which is the number spoken, the number it is
        // on is said: the bare frequency, as F says it, no "corrected" label
        // ("I'd just speak the number personally rather than hearing
        // 'corrected' every ten seconds on a say satellite"). Coalescing queued
        // tunes was rejected by Noel (11:08) and is not done. The 1.2 s give-up
        // stands (J5) until the bench measures SmartLink.
        //
        // ROUND TWO (the 8600 bench, Fail 1): it is said ONLY when the radio's
        // position is known and differs from what was spoken, which is when our
        // latest tune is REFUSED (an error reply, not sent, locked at its
        // write, the loop ended, or refused late after a give-up). Then the
        // slice is on its last answer. A success reply means it is on what was
        // spoken. A give-up says nothing: with no answer, where the radio is is
        // not known, and round one's give-up correction would have spoken the
        // stale cached number the bench measured. Outside moves (another
        // client, Doppler tracking) are never a tune of ours ending elsewhere,
        // so they are never spoken, one or a stream; F and the title read them.
        // A move that arrived before our reply was overridden at the radio by
        // our tune, so nothing is left held back to correct.

        /// <summary>The last correction handed to speech, or null. Set before the speech gate. For tests.</summary>
        internal string LastTuneCorrection { get; private set; }

        /// <summary>How many corrections have been handed to speech. For tests.</summary>
        internal int TuneCorrections;

        /// <summary>What a live correction said, and about which slice (round three, Codex's B1).</summary>
        private sealed class SpokenCorrection
        {
            public SpokenCorrection(Slice slice, ulong hz) { Slice = slice; Hz = hz; }
            public Slice Slice { get; }
            public ulong Hz { get; }
        }

        /// <summary>The correction handed to speech and not retired since; null when none.</summary>
        private SpokenCorrection _liveCorrection;

        /// <summary>How many times a spoken correction has been retired. For tests.</summary>
        internal int TuneCorrectionsRetired;

        /// <summary>The return status (SpeechSubject.ReturnStatus) was handed to speech and not retired since.</summary>
        private int _returnStatusLive;

        /// <summary>Home's return status was just spoken; it describes the selection as it is now.</summary>
        public void NoteReturnStatusSpoken() => System.Threading.Interlocked.Exchange(ref _returnStatusLive, 1);

        // ── J28, ruled by Noel 2026-11-06 16:52: a typed frequency waits for the radio ──
        //
        // "Telling you that it has tuned to that frequency and then saying it's
        // invalid is misleading." The typed entry is told the radio's own answer
        // to ITS tune, and says nothing about the frequency until then. Every
        // tune funnels through RequestTune, which knows the request's id; the
        // reply, a refusal at the write, and the bounds all answer that id.

        /// <summary>The radio's answer to a typed tune (J28).</summary>
        public enum TuneAnswer
        {
            /// <summary>The radio acknowledged the tune: it is on the frequency asked for.</summary>
            Accepted,
            /// <summary>The radio refused it, or it could not be sent: the slice stays where it was.</summary>
            Refused,
            /// <summary>
            /// No answer within the guard's own bound (<see cref="FrequencyEchoGuard.GiveUpMs"/>
            /// after the write, or <see cref="FrequencyEchoGuard.UnsentGiveUpMs"/> if it is never
            /// written). Said once; the real answer is still handed over if it comes.
            /// </summary>
            NoAnswerYet,
        }

        /// <summary>
        /// One typed tune, and everything its answer is about (round five,
        /// Codex's re-check T1): this rig, its radio, the command connection and
        /// the falls counted when it was typed, and, once requested, the slice,
        /// the request and the frequency. An answer to an old request is never
        /// permission to describe or reconfigure what is current now: whoever
        /// delivers the answer asks <see cref="WhyNoLongerCurrent"/> first, at
        /// the moment it delivers, and acts only on <see cref="Rig"/>.
        /// </summary>
        public sealed class TypedTune
        {
            internal TypedTune(FlexBase rig, System.Action<TuneAnswer, TypedTune> answered)
            {
                Rig = rig;
                Answered = answered;
                Radio = rig.theRadio;
                Connection = Radio?.CurrentCommandConnection;
                Falls = System.Threading.Interlocked.Read(ref rig._connectionFalls);
                SelectedAtTyping = rig.SelectedSliceNow();
            }
            /// <summary>The selected slice when it was typed (round six, Codex's re-check T2).</summary>
            internal Slice SelectedAtTyping { get; }
            /// <summary>
            /// The slice it was tuned on is the selected one now (round six, T2).
            /// The side effects that act on the SELECTED slice (the 60 m mode
            /// correction, the band boundary) are run only when this is true;
            /// typed while the transmit frequency is shown, it tunes the transmit
            /// slice, and they would act on another.
            /// </summary>
            public bool TypedOnTheSelectedSlice =>
                Slice != null && ReferenceEquals(Rig.SelectedSliceNow(), Slice);
            /// <summary>The rig the frequency was typed on. The only one an answer may act on.</summary>
            public FlexBase Rig { get; }
            internal System.Action<TuneAnswer, TypedTune> Answered { get; }
            internal Radio Radio { get; }
            internal CommandConnection Connection { get; }
            internal long Falls { get; }
            internal Slice Slice { get; set; }
            internal long RequestId { get; set; }
            /// <summary>The frequency typed, in hertz; 0 when the tune was refused before it was requested.</summary>
            public ulong Hz { get; internal set; }
            internal bool ToldNoAnswer;
            /// <summary>Why <paramref name="answer"/> to this tune no longer describes the radio, or null. Never throws.</summary>
            public string WhyNoLongerCurrent(TuneAnswer answer)
            {
                try { return Rig.TypedTuneNoLongerCurrent(this, answer); }
                catch (System.Exception ex) { return "it could not be checked: " + ex.Message; }
            }
        }

        /// <summary>
        /// Why <paramref name="answer"/> to typed tune <paramref name="t"/> no
        /// longer describes the radio (round five, T1): the radio, its
        /// connection, a fall since, the slice gone, a newer tune of it asked
        /// for since, or, for an acceptance, the slice no longer on the
        /// frequency. Not for a lock-held caller: it takes the roster's lock.
        /// </summary>
        private string TypedTuneNoLongerCurrent(TypedTune t, TuneAnswer answer)
        {
            if (!ReferenceEquals(theRadio, t.Radio)) return "the radio it was typed for is no longer this one";
            if (t.Radio != null)
            {
                var now = t.Radio.CurrentCommandConnection;
                if (!ReferenceEquals(now, t.Connection) || now?.State != CommandConnectionState.Connected)
                    return "the radio's command connection has changed or ended since";
            }
            if (System.Threading.Interlocked.Read(ref _connectionFalls) != t.Falls) return "the connection has fallen since";
            var slice = t.Slice;
            if (slice == null) return null;   // refused before any request: it is about the request alone
            var slices = mySlices;
            bool ours = false;
            if (slices != null) lock (slices) ours = slices.Contains(slice);
            if (!ours) return "the slice it was typed for is gone";
            if (LatestTuneRequest(slice) != t.RequestId) return "a newer tune of the slice has been asked for since";
            if (answer == TuneAnswer.Accepted && LibFreqtoLong(slice.Freq) != t.Hz) return "the slice is no longer on that frequency";
            // The selection it was typed under (round six, Codex's re-check T2):
            // an acceptance describes and acts on that context, not another.
            if (answer == TuneAnswer.Accepted && !ReferenceEquals(SelectedSliceNow(), t.SelectedAtTyping))
                return "another slice has been selected since it was typed";
            return null;
        }

        /// <summary>Each slice's newest tune request (round five, T1): any tune, typed or not.</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Slice, System.Runtime.CompilerServices.StrongBox<long>> _latestTuneRequest = new();

        private long LatestTuneRequest(Slice slice) =>
            _latestTuneRequest.TryGetValue(slice, out var box) ? System.Threading.Interlocked.Read(ref box.Value) : 0;

        /// <summary>The typed tune waiting on one slice: the request it waits on, and who is told.</summary>
        private sealed class TuneWaiter
        {
            public TuneWaiter(long id, TypedTune typed) { Id = id; Typed = typed; }
            public long Id { get; }
            public TypedTune Typed { get; }
        }

        /// <summary>
        /// A slice was removed, or the connection fell (round five, T1): a typed
        /// tune waiting on it is answered no more. Null slice: every one.
        /// </summary>
        private void RetireTuneWaiters(Slice slice, string why)
        {
            int n;
            lock (_tuneWaiters)
            {
                if (slice == null) { n = _tuneWaiters.Count; _tuneWaiters.Clear(); }
                else n = _tuneWaiters.Remove(slice) ? 1 : 0;
            }
            if (n > 0)
                Tracing.TraceLine("TuneAwaitingAnswer: " + why + "; " + n + " typed tune(s) will be answered no more",
                    System.Diagnostics.TraceLevel.Info);
        }

        /// <summary>At most one waiter per slice: a newer tune of the slice supersedes it. Under its own lock.</summary>
        private readonly System.Collections.Generic.Dictionary<Slice, TuneWaiter> _tuneWaiters =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        /// <summary>The typed tune the next RequestTune on this thread belongs to (set only by <see cref="TuneAwaitingAnswer"/>).</summary>
        [System.ThreadStatic] private static TypedTune t_tuneAnswer;

        /// <summary>The bounds' wait. Tests park it so they can let a bound run out by hand.</summary>
        internal System.Func<int, System.Threading.Tasks.Task> TuneAnswerDelay = ms => System.Threading.Tasks.Task.Delay(ms);

        /// <summary>
        /// Tune exactly as the typed entry always has (<see cref="VirtualRXFrequency"/>)
        /// and be told the radio's answer to THAT tune (J28). <paramref name="answered"/>
        /// runs on whatever thread the answer arrives on, never under a lock of
        /// ours: marshal before touching a window. A tune refused before it is
        /// sent (transmitting, no slice, a locked slice) is answered at once.
        /// The answer comes with its <see cref="TypedTune"/>: ask its
        /// <see cref="TypedTune.WhyNoLongerCurrent"/> when it is delivered, and act
        /// only on its <see cref="TypedTune.Rig"/> (round five, T1).
        /// </summary>
        public void TuneAwaitingAnswer(ulong value, System.Action<TuneAnswer, TypedTune> answered)
        {
            if (answered == null) throw new System.ArgumentNullException(nameof(answered));
            t_tuneAnswer = new TypedTune(this, answered);
            try { VirtualRXFrequency = value; }
            finally
            {
                // Not taken by a request: nothing was asked of the radio.
                var untaken = t_tuneAnswer;
                t_tuneAnswer = null;
                if (untaken != null)
                {
                    Tracing.TraceLine("TuneAwaitingAnswer: the tune to " + value + " was not requested (transmitting, or no slice); refused",
                        System.Diagnostics.TraceLevel.Info);
                    TellTuneAnswer(untaken, TuneAnswer.Refused);
                }
            }
        }

        /// <summary>RequestTune takes the typed tune this thread brought, if any: once.</summary>
        private static TypedTune TakeTuneAnswer()
        {
            var a = t_tuneAnswer;
            t_tuneAnswer = null;
            return a;
        }

        /// <summary>
        /// Request <paramref name="id"/> on <paramref name="slice"/> now owns the
        /// slice's waiter: <paramref name="answered"/>'s, or none (a tune that is
        /// not typed supersedes a typed one still waiting, which is then told
        /// nothing more). The unsent bound starts here.
        /// </summary>
        private void WaitForTuneAnswer(Slice slice, long id, TypedTune answered)
        {
            _latestTuneRequest.AddOrUpdate(slice, new System.Runtime.CompilerServices.StrongBox<long>(id));
            lock (_tuneWaiters)
            {
                if (answered == null)
                {
                    if (_tuneWaiters.Remove(slice))
                        Tracing.TraceLine("TuneAwaitingAnswer: a newer tune of slice " + slice.Letter
                            + " supersedes the typed tune's answer", System.Diagnostics.TraceLevel.Info);
                    return;
                }
                answered.Slice = slice;
                answered.RequestId = id;
                _tuneWaiters[slice] = new TuneWaiter(id, answered);
            }
            NoAnswerAfter(slice, id, FrequencyEchoGuard.UnsentGiveUpMs);
        }

        /// <summary>Tell the slice's waiter NoAnswerYet after <paramref name="ms"/>, if it is still waiting on <paramref name="id"/>.</summary>
        private void NoAnswerAfter(Slice slice, long id, int ms)
        {
            lock (_tuneWaiters)
                if (!_tuneWaiters.TryGetValue(slice, out var w) || w.Id != id) return;
            System.Threading.Tasks.Task wait;
            try { wait = TuneAnswerDelay(ms); }
            catch (System.Exception) { return; }
            wait.ContinueWith(_ => AnswerTune(slice, id, TuneAnswer.NoAnswerYet),
                System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>
        /// Hand <paramref name="answer"/> for request <paramref name="id"/> to the
        /// slice's waiter, if it is waiting on that request. Accepted and Refused
        /// end the wait; NoAnswerYet is told once and the wait goes on. True when
        /// a waiter took a final answer, so the caller says nothing of its own
        /// about it (the typed entry speaks for its tune).
        /// </summary>
        private bool AnswerTune(Slice slice, long id, TuneAnswer answer)
        {
            TypedTune tell;
            lock (_tuneWaiters)
            {
                if (!_tuneWaiters.TryGetValue(slice, out var w) || w.Id != id) return false;
                if (answer == TuneAnswer.NoAnswerYet)
                {
                    if (w.Typed.ToldNoAnswer) return false;
                    w.Typed.ToldNoAnswer = true;
                }
                else _tuneWaiters.Remove(slice);
                tell = w.Typed;
            }
            Tracing.TraceLine("TuneAwaitingAnswer: slice " + slice.Letter + ", request " + id + ": " + answer,
                System.Diagnostics.TraceLevel.Info);
            TellTuneAnswer(tell, answer);
            return answer != TuneAnswer.NoAnswerYet;
        }

        private static void TellTuneAnswer(TypedTune tell, TuneAnswer answer)
        {
            try { tell.Answered(answer, tell); }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("TuneAwaitingAnswer: the answer's handler threw: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>How many times a connection has fallen, ours or not (round four, B3): a sentence about one connection is not true on the next.</summary>
        private long _connectionFalls;

        /// <summary>
        /// Home's return status, bound to everything it states (round four,
        /// Codex's re-check B3). "Listening on 14.100 megahertz, USB, 20 meter
        /// band, slice A" is true only while: this radio, on this live command
        /// connection, with no fall since; the selected slice is still that slice
        /// object (a removal, the ActiveSlice handler, the roster's
        /// re-derivations and the RXVFO setter all change that); it reports the
        /// frequency and mode the sentence says, under the same letter; and the
        /// radio is listening, not transmitting. Call it BEFORE building the
        /// sentence, so a change in between reads as no longer true.
        /// <para>
        /// The answer is asked inside the speech arbiter's lock, before any
        /// replay. So it reads plain fields only, and the roster's lock is TRIED,
        /// never waited for: code that holds the roster can speak, and waiting
        /// here could deadlock. A busy roster is "cannot say", and the copy is not
        /// replayed, which is the safe way round. A status said while
        /// transmitting states a power reading true only at its moment, and is
        /// never replayed.
        /// </para>
        /// </summary>
        public System.Func<bool?> BindReturnStatus()
        {
            var radio = theRadio;
            var connection = radio?.CurrentCommandConnection;
            long falls = System.Threading.Interlocked.Read(ref _connectionFalls);
            Slice slice = SelectedSliceNow();
            if (radio == null || slice == null || _Transmit) return () => (bool?)false;
            ulong hz = RXFrequency;
            string mode = RXMode;
            string letter = slice.Letter;
            return () => ReturnStatusStillTrue(radio, connection, falls, slice, hz, mode, letter);
        }

        /// <returns>True, false, or null for "cannot say yet": the roster's lock
        /// was busy (round five, Codex's B3a). The arbiter asks again at its next
        /// window, and drops the copy only on false or once its bound passes.</returns>
        private bool? ReturnStatusStillTrue(Radio radio, CommandConnection connection, long falls, Slice slice,
            ulong hz, string mode, string letter)
        {
            if (!ReferenceEquals(theRadio, radio) || !_IsConnected || _Transmit) return false;
            if (System.Threading.Interlocked.Read(ref _connectionFalls) != falls) return false;
            var now = radio.CurrentCommandConnection;
            if (!ReferenceEquals(now, connection) || now == null || now.State != CommandConnectionState.Connected) return false;
            var slices = mySlices;
            if (slices == null) return false;
            if (!System.Threading.Monitor.TryEnter(slices)) return null;   // busy: ask again (B3a)
            try
            {
                int rx = _RXVFO;
                if (rx < 0 || rx >= slices.Count || !ReferenceEquals(slices[rx], slice)) return false;
            }
            finally { System.Threading.Monitor.Exit(slices); }
            return LibFreqtoLong(slice.Freq) == hz
                && string.Equals(slice.DemodMode, mode, System.StringComparison.Ordinal)
                && string.Equals(slice.Letter, letter, System.StringComparison.Ordinal);
        }

        /// <summary>How many times an unheard return status has been retired. For tests.</summary>
        internal int ReturnStatusesRetired;

        /// <summary>
        /// The selection or the selected slice's frequency changed: an unheard
        /// return status now describes the past, and is retired (rulings round
        /// three, the bench's 4.1 replay; the same binding as B1). Cheap when
        /// none is live.
        /// </summary>
        internal void RetireReturnStatus(string by)
        {
            if (System.Threading.Interlocked.Exchange(ref _returnStatusLive, 0) == 0) return;
            System.Threading.Interlocked.Increment(ref ReturnStatusesRetired);
            try { ScreenReaderOutput.Supersede(Speech.SpeechSubject.ReturnStatus, by); }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("RetireReturnStatus: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>
        /// The fact a live correction states has changed (round three, Codex's
        /// B1): its slice now reads another number (another client, the knob, a
        /// later answer), or the slice is gone. An unheard copy is then false,
        /// and is retired so no interrupt salvages it. Cheap when none is live.
        /// </summary>
        private void RetireCorrectionIfNoLongerTrue(Slice slice, string why)
        {
            var live = System.Threading.Volatile.Read(ref _liveCorrection);
            if (live == null || !ReferenceEquals(live.Slice, slice)) return;
            bool stillTrue;
            try { stillTrue = why == null && LibFreqtoLong(slice.Freq) == live.Hz; }
            catch { stillTrue = false; }
            if (!stillTrue) RetireTuneCorrection(why ?? "the slice's frequency moved");
        }

        /// <summary>
        /// A spoken correction is about one slice, one request and one
        /// connection (Codex's R3 on the rulings round): a newer tune of ours
        /// (which speaks its own number), a change of selected slice, or the
        /// connection ending makes any copy of it not yet heard false, and an
        /// interrupt must not salvage it after the newer readback. Retired
        /// through the arbiter's supersession, which keeps an unheard copy from
        /// being rescued. Cheap when nothing is live.
        /// </summary>
        internal void RetireTuneCorrection(string by)
        {
            if (System.Threading.Interlocked.Exchange(ref _liveCorrection, null) == null) return;
            System.Threading.Interlocked.Increment(ref TuneCorrectionsRetired);
            try { ScreenReaderOutput.Supersede(Speech.SpeechSubject.TuneEndedElsewhere, by); }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("RetireTuneCorrection: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Our latest tune of <paramref name="slice"/> was refused, and the radio
        /// is on <paramref name="endedHz"/>, not <paramref name="requestedHz"/>.
        /// Says the bare number, unless a newer tune of ours is already
        /// outstanding (it speaks for itself), the slice is gone, or the
        /// connection is ending or narrated by someone else. Traced either way.
        /// Returns whether it was handed to speech.
        /// </summary>
        private bool TuneEndedElsewhere(Slice slice, ulong requestedHz, ulong endedHz, string ending)
        {
            try
            {
                if (endedHz == 0 || endedHz == requestedHz) return false;
                string letter = slice.Letter ?? slice.Index.ToString();
                string head = "TuneEndedElsewhere: slice " + letter + " asked " + requestedHz + ", ended on " + endedHz
                    + " (" + ending + "): ";
                string not = null;
                var guard = EchoGuardFor(slice);
                if (guard.Read(out _) != FrequencyEchoGuard.Reading.Idle) not = "a newer tune of ours is outstanding, and speaks for itself";
                else if (!IsConnected || Disconnecting) not = "the connection is ending";
                else
                {
                    var slices = mySlices;
                    bool ours = false;
                    if (slices != null) lock (slices) ours = slices.Contains(slice);
                    if (!ours) not = "the slice is no longer ours";
                }
                if (not != null)
                {
                    Tracing.TraceLine(head + "not spoken - " + not, System.Diagnostics.TraceLevel.Info);
                    return false;
                }
                var format = FormatFreq;
                string number = format != null ? format(endedHz) : RadioStatusBuilder.FormatFreqDisplay(endedHz);
                // DRAFT (J4): the bare number, Noel's ruling; the key exists so the
                // words can change without code. For a slice OTHER than the one
                // you are on, the slice is named (J24, ruled by Noel 2026-11-06
                // 16:43): a bare number there reads as your own slice's.
                string text = ReferenceEquals(slice, SelectedSliceNow())
                    ? Lexicon.Get("connect.home.frequency_ended_on", ("freqText", number))
                    : Lexicon.Get("connect.home.frequency_ended_on_slice", ("slice", letter), ("freqText", number));
                LastTuneCorrection = text;
                System.Threading.Interlocked.Increment(ref TuneCorrections);
                System.Threading.Interlocked.Exchange(ref _liveCorrection, new SpokenCorrection(slice, endedHz));
                Tracing.TraceLine(head + "spoken: " + text, System.Diagnostics.TraceLevel.Info);
                if (!SuppressSpeech)
                    ScreenReaderOutput.Speak(text, Speech.SpeechIntent.Queue, VerbosityLevel.Critical,
                        subject: Speech.SpeechSubject.TuneEndedElsewhere);
                return true;
            }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("TuneEndedElsewhere: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
                return false;
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
