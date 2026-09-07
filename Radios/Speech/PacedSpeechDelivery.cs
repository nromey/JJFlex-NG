#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// The dedicated delivery thread (#521): one utterance in flight, our
    /// queue holding the rest, the reader asked synchronously what became of
    /// each, and every answer reported back by ticket.
    ///
    /// <para><b>Why a thread of its own, and why this is the whole shape of
    /// the change.</b> <c>ScreenReaderOutput.EmitCore</c> called the backend
    /// synchronously on the caller's thread inside the backend lock, and the
    /// callers are UI handlers and radio-event threads. A blocking
    /// <c>speakSsml</c> there would freeze whichever of them spoke for the
    /// length of the sentence and hold the lock so <c>Silence()</c> could not
    /// run. So the blocking call lives here, on a thread that owns nothing
    /// anyone waits on, and the callers hand over and return in
    /// microseconds as they always did.</para>
    ///
    /// <para><b>Our queue IS the unspoken backlog.</b> Before, five
    /// utterances went into NVDA's queue in one millisecond and the arbiter
    /// lost sight of them; now they wait HERE, one is handed over at a time,
    /// and the marks say where that one is. An interrupt cancels the one in
    /// flight, withdraws the rest unspoken (the arbiter's ledger still holds
    /// them and its salvage rules decide their fate, exactly as before), and
    /// puts the interrupter at the head. Interrupt semantics are the
    /// arbiter's and are not changed here; only the mechanism under the sink
    /// is.</para>
    ///
    /// <para><b>The timeout has a real escape, in three stages.</b> NVDA
    /// 2024.1 through 2026.1 can hold the synchronous call forever. A
    /// deadline is sized from the utterance and EXTENDED while marks keep
    /// arriving — a reader still reporting progress is not hung, however
    /// long its own traffic delayed us. When the deadline passes, or when
    /// our own cancel is not answered inside <see cref="CancelGraceMs"/>, the
    /// RPC runtime is asked to cancel the blocked call
    /// (<c>RpcCancelThreadEx</c>). If the thread still does not return, it is
    /// ABANDONED — its generation is retired so a late return reports
    /// nothing — and a fresh one is started. Either way the in-flight
    /// utterance is reported <see cref="SpeechOutcomeKind.Unknown"/> and
    /// never Completed, and after an abandonment the channel is turned off
    /// for the session: one leaked thread is a cost, two is a policy.</para>
    ///
    /// <para><b>Refusals.</b> INVALID_PARAMETER is our escaping defect and
    /// the text is re-sent through Prism's plain path so the operator hears
    /// it, traced with the text. ACCESS_DENIED means NVDA is asleep for the
    /// focused application and would refuse the plain path too; it is
    /// reported and not retried. An absent interface or a reader that has
    /// gone falls through to Prism, whose own delivery check (#277) says
    /// what happened next.</para>
    /// </summary>
    internal sealed class PacedSpeechDelivery : IDisposable
    {
        // ── Timing policy ──

        /// <summary>After our own cancel, how long a still-blocked call may take to come back before it is treated as hung. The probe measured 18 ms.</summary>
        internal const int CancelGraceMs = 2000;

        /// <summary>After RpcCancelThreadEx, how long to wait for the thread to return before abandoning it.</summary>
        internal const int EscapeGraceMs = 2000;

        /// <summary>A mark inside this long of the deadline keeps the call alive: the reader is demonstrably still working through our words.</summary>
        internal const int MarkGraceMs = 8000;

        /// <summary>Bounds on the utterance-sized deadline.</summary>
        internal const int DeadlineFloorMs = 8000;
        internal const int DeadlineCapMs = 45000;

        /// <summary>
        /// Deadline for a quiet, uncancelled utterance: twice the ledger's
        /// own err-long estimate plus three seconds of head-of-queue slack,
        /// bounded. The estimate is already about 15% over the measured
        /// duration at the reference rate, so twice it covers a reader set
        /// at half that rate before the marks even have to extend it.
        /// </summary>
        internal static int DefaultDeadlineMs(string text) =>
            Math.Clamp(2 * SpeechArbiter.EstimateSpokenMs(text) + 3000, DeadlineFloorMs, DeadlineCapMs);

        // ── Wiring ──

        private readonly Func<string, bool, SpeechDelivery> _fallbackSpeak;
        private readonly Action<long, string, SpeechOutcome> _onOutcome;
        private readonly Func<string, int> _deadlineMsFor;
        private readonly int _cancelGraceMs;
        private readonly int _escapeGraceMs;
        private readonly int _markGraceMs;

        private sealed class Item
        {
            public Item(long ticket, ISpeechCompletionChannel channel, string text, bool interrupt)
            {
                Ticket = ticket; Channel = channel; Text = text; Interrupt = interrupt;
            }
            public long Ticket { get; }
            public ISpeechCompletionChannel Channel { get; }
            public string Text { get; }
            public bool Interrupt { get; }

            /// <summary>Set by an interrupt or a discard while this item was queued or in flight.</summary>
            public bool CancelRequested;

            public Stopwatch? Started;
            public long LastMarkAtMs = -1;
            public int Marks;
        }

        private readonly object _lock = new object();
        private readonly List<Item> _queue = new List<Item>();
        private Item? _inFlight;
        private Timer? _deadline;
        private readonly ManualResetEventSlim _returned = new ManualResetEventSlim(false);
        private int _generation;
        private Thread? _worker;
        private bool _disposed;
        private long _nextTicket;
        private int _escapesThatWorked;
        private int _abandoned;

        /// <summary>
        /// Raised while an Interrupt or Discard is between marking the queue
        /// and issuing the reader's cancel. The worker must not START a new
        /// utterance inside that gap: it would otherwise hand the reader the
        /// interrupter and then our own cancel would land on it, cutting the
        /// very words the interrupt exists to say. Found by a test the first
        /// time the pump ran (Interrupt_WithNothingInFlight_StillCutsTheReader
        /// went red once in three), which is a race a live desk would have
        /// produced as "the interrupter sometimes says nothing".
        /// </summary>
        private int _cancelInProgress;

        /// <param name="fallbackSpeak">
        /// The plain path — Prism's speak, under the backend lock — for
        /// utterances the channel would not or could not carry. Returns what
        /// the backend did so #277's delivery check can run on it.
        /// </param>
        /// <param name="onOutcome">Where every answer goes: the arbiter's <c>OnOutcome</c>, by ticket.</param>
        /// <param name="deadlineMsFor">Deadline per utterance; null for <see cref="DefaultDeadlineMs"/>. Tests shorten it.</param>
        public PacedSpeechDelivery(
            Func<string, bool, SpeechDelivery> fallbackSpeak,
            Action<long, string, SpeechOutcome> onOutcome,
            Func<string, int>? deadlineMsFor = null,
            int cancelGraceMs = CancelGraceMs,
            int escapeGraceMs = EscapeGraceMs,
            int markGraceMs = MarkGraceMs)
        {
            _fallbackSpeak = fallbackSpeak;
            _onOutcome = onOutcome;
            _deadlineMsFor = deadlineMsFor ?? DefaultDeadlineMs;
            _cancelGraceMs = cancelGraceMs;
            _escapeGraceMs = escapeGraceMs;
            _markGraceMs = markGraceMs;
        }

        /// <summary>How many utterances wait behind the one in flight.</summary>
        public int QueuedCount { get { lock (_lock) { return _queue.Count; } } }

        /// <summary>True while the reader has one of ours.</summary>
        public bool InFlight { get { lock (_lock) { return _inFlight != null; } } }

        /// <summary>Retired delivery threads — one per abandonment. Zero is the healthy number.</summary>
        public int AbandonedThreads { get { lock (_lock) { return _abandoned; } } }

        /// <summary>Queue an utterance behind whatever is waiting. Returns its ticket.</summary>
        public long Enqueue(ISpeechCompletionChannel channel, string text)
        {
            var item = new Item(NextTicket(), channel, text, interrupt: false);
            lock (_lock)
            {
                ThrowIfDisposed();
                _queue.Add(item);
                EnsureWorkerLocked();
                Monitor.PulseAll(_lock);
            }
            return item.Ticket;
        }

        /// <summary>
        /// The arbiter's interrupt, in this mechanism: cancel the utterance
        /// in flight, withdraw everything queued (unspoken — the arbiter's
        /// ledger still holds them and will judge them), and put this one at
        /// the head. Returns its ticket. The reader's own cancel runs outside
        /// the lock; it is an RPC.
        /// </summary>
        /// <summary>
        /// The reader was cancelled by something that is not us — the operator's
        /// Ctrl, or any other key. Give the queue back to the arbiter unspoken.
        /// #562.
        /// </summary>
        private void WithdrawForForeignCancel(long afterTicket)
        {
            List<Item> withdrawnItems;
            lock (_lock)
            {
                if (_disposed) return;
                if (_queue.Count == 0) return;
                withdrawnItems = new List<Item>(_queue);
                _queue.Clear();
            }
            int withdrawn = withdrawnItems.Count;

            // Every withdrawn item gets an OUTCOME. Without one the arbiter
            // never learns its fate: a tracked ledger entry carries
            // EstFinishUtc = MaxValue and is retired by OnOutcome, so an item
            // that is silently dropped here is never spoken, never judged and
            // never pruned @ it simply accumulates.
            //
            // Zero marks reached is the honest report: it was not cut off part
            // way, it was never begun. That is unambiguously unheard, so #503's
            // subject rules get to decide whether it earns another hearing @
            // which for a progress line means the newer one supersedes it, and
            // for the connect lead means the salvage may offer it again.
            foreach (var w in withdrawnItems)
                ReportWithdrawn(w, byUs: false);
            Tracing.TraceLine(
                $"PacedDelivery: #{afterTicket} was cancelled by something that is not us — "
                + $"{withdrawn} queued withdrawn unspoken for the arbiter to judge. The "
                + "operator asked for quiet.",
                TraceLevel.Verbose);
        }

        /// <summary>
        /// An item that never reached the reader at all. Zero marks, and the
        /// arbiter is told, so its ledger entry is retired rather than orphaned.
        /// </summary>
        private void ReportWithdrawn(Item item, bool byUs)
        {
            Report(item, SpeechOutcome.Cancelled(
                marksReached: 0,
                markCount: NvdaCompletionChannel.SplitWords(item.Text).Length,
                byUs: byUs,
                elapsedMs: 0));
        }

        public long Interrupt(ISpeechCompletionChannel channel, string text)
        {
            var item = new Item(NextTicket(), channel, text, interrupt: true);
            int withdrawn;
            List<Item> withdrawnByUs;
            Item? cut;
            lock (_lock)
            {
                ThrowIfDisposed();
                withdrawn = _queue.Count;
                withdrawnByUs = new List<Item>(_queue);
                _queue.Clear();
                cut = _inFlight;
                if (cut != null && !cut.CancelRequested)
                {
                    cut.CancelRequested = true;
                    // Our cancel should bring the blocked call back in tens
                    // of milliseconds. If it does not, the call is hung and
                    // the deadline handler is the escape.
                    RearmDeadlineLocked(_cancelGraceMs);
                }
                _queue.Insert(0, item);
                _cancelInProgress++;
                EnsureWorkerLocked();
            }

            // Cut the reader whether or not one of OURS was in flight: an
            // interrupt has always cut whatever the reader was saying,
            // including its own announcements. Same semantics as before.
            // The worker is held off the head of the queue until this has
            // gone out — see _cancelInProgress.
            try { channel.Cancel(); } catch (Exception ex) { Tracing.TraceLine($"PacedDelivery: cancel threw: {ex.Message}", TraceLevel.Warning); }
            finally
            {
                lock (_lock)
                {
                    _cancelInProgress--;
                    Monitor.PulseAll(_lock);
                }
            }

            // Same orphaning as the foreign-cancel path: an item withdrawn
            // for an interrupt has to be reported too, or the arbiter's ledger
            // keeps it forever. byUs, because we are the ones who did it.
            foreach (var w in withdrawnByUs)
                ReportWithdrawn(w, byUs: true);

            Tracing.TraceLine(
                $"PacedDelivery: interrupt #{item.Ticket} '{Clip(text)}' — "
                + (cut != null ? $"cancelled #{cut.Ticket} in flight, " : "nothing of ours in flight, ")
                + $"{withdrawn} queued withdrawn unspoken for the arbiter to judge",
                TraceLevel.Verbose);
            return item.Ticket;
        }

        /// <summary>
        /// Drop everything: the operator silenced speech, the reader is being
        /// re-bound, the app is shutting down. Queued items are withdrawn and
        /// the one in flight is cancelled; whatever the reader answers for it
        /// is still reported, because the answer is the truth about a
        /// delivery even when nobody is left to act on it.
        /// </summary>
        public void Discard(string reason)
        {
            int withdrawn;
            Item? cut;
            lock (_lock)
            {
                withdrawn = _queue.Count;
                _queue.Clear();
                cut = _inFlight;
                if (cut != null && !cut.CancelRequested)
                {
                    cut.CancelRequested = true;
                    RearmDeadlineLocked(_cancelGraceMs);
                }
                if (cut != null) _cancelInProgress++;
            }
            if (cut != null)
            {
                try { cut.Channel.Cancel(); } catch { /* best effort */ }
                finally
                {
                    lock (_lock)
                    {
                        _cancelInProgress--;
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            if (withdrawn > 0 || cut != null)
            {
                Tracing.TraceLine(
                    $"PacedDelivery: discarded {withdrawn} queued and "
                    + (cut != null ? $"cancelled #{cut.Ticket} in flight" : "nothing in flight")
                    + $" — {reason}",
                    TraceLevel.Info);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _queue.Clear();
                _deadline?.Dispose();
                _deadline = null;
                _generation++;
                Monitor.PulseAll(_lock);
            }
            // The worker, if blocked in the reader, is left to return on its
            // own; it is a background thread and will find its generation
            // retired. Nothing is joined: joining a thread that may be inside
            // a hung RPC would hang the shutdown.
        }

        // ── The worker ──

        private long NextTicket() => Interlocked.Increment(ref _nextTicket);

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PacedSpeechDelivery));
        }

        /// <summary>Start a worker for the current generation if none is running. Caller holds the lock.</summary>
        private void EnsureWorkerLocked()
        {
            if (_worker != null && _worker.IsAlive) return;
            int gen = _generation;
            _worker = new Thread(() => WorkerLoop(gen))
            {
                IsBackground = true,
                Name = $"JJFlex speech delivery (gen {gen})",
            };
            _worker.Start();
        }

        private void WorkerLoop(int gen)
        {
            try
            {
                while (true)
                {
                    Item item;
                    lock (_lock)
                    {
                        while ((_queue.Count == 0 || _cancelInProgress > 0) && !_disposed && gen == _generation)
                            Monitor.Wait(_lock);
                        if (_disposed || gen != _generation) return;
                        item = _queue[0];
                        _queue.RemoveAt(0);
                        _inFlight = item;
                        item.Started = Stopwatch.StartNew();
                        _returned.Reset();
                    }

                    SpeechOutcome outcome = Deliver(item);

                    bool stale;
                    lock (_lock)
                    {
                        stale = gen != _generation;
                        if (!stale)
                        {
                            DisarmDeadlineLocked();
                            _inFlight = null;
                            _returned.Set();
                        }
                    }
                    if (stale)
                    {
                        // Abandoned while blocked, and back now. The deadline
                        // handler already reported this ticket as Unknown; a
                        // second report would let a late Completed contradict
                        // it, which is exactly the false "heard" the type
                        // exists to forbid.
                        Tracing.TraceLine(
                            $"PacedDelivery: an abandoned delivery thread returned late for #{item.Ticket} ({outcome}); "
                            + "its answer is discarded because the ticket was already reported unknown.",
                            TraceLevel.Warning);
                        return;
                    }

                    // #562, ruled by Noel 2026-09-07: "ctrl always means
                    // silence when it comes to NVDA's shut up key."
                    //
                    // A cancel that was not OURS is the operator asking for
                    // quiet. We cannot tell Ctrl from any other key — both come
                    // back as 1223 with CancelledByUs false — and we should not
                    // try: from the operator's side "I pressed a key, it stopped,
                    // then it started talking again" is the same complaint
                    // whichever key it was.
                    //
                    // Withdrawn, not dropped. The arbiter's ledger still holds
                    // them and #503's subject rules decide what deserves saying
                    // again — and since #521 those rules are working from real
                    // delivery data instead of a guess, which is the difference
                    // between this and the pre-#521 behaviour that lost 89
                    // announcements in a day.
                    if (outcome.Kind == SpeechOutcomeKind.Cancelled && !outcome.CancelledByUs)
                        WithdrawForForeignCancel(item.Ticket);

                    Report(item, outcome);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PacedDelivery: worker (gen {gen}) died: {ex}", TraceLevel.Error);
                lock (_lock)
                {
                    if (gen == _generation)
                    {
                        _inFlight = null;
                        _returned.Set();
                        _worker = null;
                        // A new worker comes with the next Enqueue/Interrupt.
                    }
                }
            }
        }

        /// <summary>Hand one item to the reader (or to the plain path) and return what happened. Runs on the worker, outside the lock.</summary>
        private SpeechOutcome Deliver(Item item)
        {
            if (item.CancelRequested)
            {
                // Interrupted or discarded before it ever reached the reader:
                // cancelled at word zero, by us, and the reader never saw it.
                int words = NvdaCompletionChannel.SplitWords(item.Text).Length;
                return SpeechOutcome.Cancelled(0, words, byUs: true, 0);
            }

            if (!item.Channel.CanReportCompletion)
            {
                // The channel went off while this waited (a hang was
                // detected, or the interface is missing). The words still
                // matter; they go the way they always went, and the ledger
                // is told nobody can say what became of them.
                var d = _fallbackSpeak(item.Text, item.Interrupt);
                return SpeechOutcome.Unknown(SpeechUnknownReason.ChannelAbsent,
                    "channel off" + DescribeFallback(d), 0);
            }

            ArmDeadline(item);
            var outcome = item.Channel.SpeakAndWait(item.Text, item.Ticket, marks => OnMark(item, marks));

            if (outcome.Kind == SpeechOutcomeKind.Unknown)
            {
                switch (outcome.UnknownReason)
                {
                    case SpeechUnknownReason.InvalidSsml:
                    case SpeechUnknownReason.ChannelAbsent:
                    case SpeechUnknownReason.Other:
                        // Our escaping defect, a reader that has gone, or a
                        // code this build does not know: the plain path
                        // carries the words, and #277 reports if it cannot.
                        var d = _fallbackSpeak(item.Text, item.Interrupt);
                        outcome = outcome.WithDetail((outcome.Detail ?? string.Empty) + DescribeFallback(d));
                        break;
                    // Refused: NVDA asleep for the focused app; the plain
                    // path is refused the same way. Escaped / Timeout: the
                    // reader may well have said it; re-sending would be the
                    // repeat this whole change exists to end.
                }
            }
            return outcome;
        }

        private static string DescribeFallback(SpeechDelivery d) =>
            d.Delivered ? "; re-sent through the plain path, which took it"
            : d.Refused ? $"; re-sent through the plain path, which refused it: {d.Failure}"
            : "; the plain path had no backend to give it to";

        private void Report(Item item, SpeechOutcome outcome)
        {
            var level = outcome.Kind switch
            {
                SpeechOutcomeKind.Completed => TraceLevel.Verbose,
                SpeechOutcomeKind.Cancelled => TraceLevel.Info,
                _ => TraceLevel.Warning,
            };
            Tracing.TraceLine($"PacedDelivery: #{item.Ticket} {outcome} — '{Clip(item.Text)}'", level);
            try { _onOutcome(item.Ticket, item.Text, outcome); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PacedDelivery: outcome handler threw for #{item.Ticket}: {ex.Message}", TraceLevel.Warning);
            }
        }

        // ── Marks and the deadline ──

        private void OnMark(Item item, int marks)
        {
            lock (_lock)
            {
                if (_inFlight != item) return;
                item.Marks = marks;
                item.LastMarkAtMs = item.Started?.ElapsedMilliseconds ?? 0;
            }
        }

        private void ArmDeadline(Item item)
        {
            int due = Math.Max(1, _deadlineMsFor(item.Text));
            lock (_lock)
            {
                if (_inFlight != item) return;
                // An interrupt may already have asked for this item's cancel
                // in the gap between the worker taking it and arming here;
                // its short grace must win over the full deadline, or a hung
                // call would wait the long way round.
                if (item.CancelRequested) due = Math.Min(due, _cancelGraceMs);
                _deadline?.Dispose();
                _deadline = new Timer(_ => OnDeadline(item), null, due, Timeout.Infinite);
            }
        }

        /// <summary>Caller holds the lock. Re-arms the in-flight deadline (creating the timer if the item is in flight but not yet armed).</summary>
        private void RearmDeadlineLocked(int dueMs)
        {
            var item = _inFlight;
            if (item == null) return;
            if (_deadline == null) _deadline = new Timer(_ => OnDeadline(item), null, dueMs, Timeout.Infinite);
            else _deadline.Change(dueMs, Timeout.Infinite);
        }

        private void DisarmDeadlineLocked()
        {
            _deadline?.Dispose();
            _deadline = null;
        }

        /// <summary>
        /// The deadline passed for <paramref name="item"/>. Runs on a timer
        /// thread. Three stages: is the reader still making progress (keep
        /// waiting); ask the RPC runtime to free the call (wait a moment);
        /// abandon the thread and report Unknown.
        /// </summary>
        private void OnDeadline(Item item)
        {
            int elapsed;
            lock (_lock)
            {
                if (_inFlight != item || _disposed) return;
                elapsed = (int)(item.Started?.ElapsedMilliseconds ?? 0);

                // Still progressing, and not one we are trying to stop: a
                // reader that just reported a mark is not hung, whatever the
                // clock says. Extend, and look again.
                if (!item.CancelRequested && item.LastMarkAtMs >= 0
                    && elapsed - item.LastMarkAtMs < _markGraceMs)
                {
                    Tracing.TraceLine(
                        $"PacedDelivery: #{item.Ticket} past its deadline at {elapsed} ms but a mark arrived "
                        + $"{elapsed - item.LastMarkAtMs} ms ago ({item.Marks} so far); extending by {_markGraceMs} ms.",
                        TraceLevel.Verbose);
                    RearmDeadlineLocked(_markGraceMs);
                    return;
                }
            }

            Tracing.TraceLine(
                $"PacedDelivery: #{item.Ticket} has not returned after {elapsed} ms "
                + (item.CancelRequested ? "(our cancel went unanswered)" : "(no marks recently)")
                + $" — asking the RPC runtime to cancel the blocked call. '{Clip(item.Text)}'",
                TraceLevel.Warning);

            bool escaped = false;
            try { escaped = item.Channel.TryCancelBlockedCall(); } catch { /* treated as not escaped */ }
            bool returned = _returned.Wait(_escapeGraceMs);

            if (returned)
            {
                // The worker is back and has reported (or is reporting) the
                // call's own answer — RPC_S_CALL_CANCELLED as Unknown(Escaped),
                // typically. The channel proved it can hang; once is a
                // warning, twice is off.
                int n = Interlocked.Increment(ref _escapesThatWorked);
                Tracing.TraceLine(
                    $"PacedDelivery: the blocked call for #{item.Ticket} returned after the RPC cancel (escape {n} this session).",
                    TraceLevel.Warning);
                if (n >= 2)
                    item.Channel.Disable("speakSsml has now had to be freed by RPC cancel twice this session; this NVDA cannot be relied on to return");
                return;
            }

            // Abandon. The channel goes OFF FIRST, before a fresh worker can
            // exist: the first cut of this started the worker and then
            // disabled the channel, and the worker dequeued the next item in
            // that gap, found the channel still open, and handed it to the
            // same hung reader — two abandoned threads for one hang. Then
            // retire the generation so the late return, if it ever comes,
            // reports nothing; start a fresh worker for what is queued (it
            // will drain through the plain path); report this ticket Unknown.
            item.Channel.Disable(
                escaped
                    ? $"speakSsml did not return {_escapeGraceMs} ms after an accepted RPC cancel; the delivery thread was abandoned"
                    : "speakSsml did not return and the RPC runtime would not cancel it; the delivery thread was abandoned");

            lock (_lock)
            {
                if (_inFlight != item) return;     // returned in the gap
                _generation++;
                _abandoned++;
                _inFlight = null;
                DisarmDeadlineLocked();
                _worker = null;
                if (!_disposed && _queue.Count > 0) EnsureWorkerLocked();
            }

            Report(item, SpeechOutcome.Unknown(SpeechUnknownReason.Timeout,
                $"no return after {elapsed} ms; RPC cancel {(escaped ? "accepted but not honoured" : "refused")}; thread abandoned",
                elapsed, item.Marks, NvdaCompletionChannel.SplitWords(item.Text).Length));
        }

        private static string Clip(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }
}
