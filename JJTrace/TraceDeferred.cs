using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace JJTrace
{
    /// <summary>
    /// One trace line waiting to be written by whoever next holds the gate.
    /// </summary>
    /// <remarks>
    /// <para><b>Two kinds, one queue.</b> A BOUND line names the session that
    /// was recording when it was formatted, and may only ever be written into
    /// that session's file: it is the drop's own record of the fall, and the
    /// design forbids it landing in a successor. An UNBOUND line
    /// (<see cref="BoundSession"/> empty) belongs to whichever session exists
    /// when it is drained — which is exactly where it would have landed had
    /// its writer waited for the gate, minus the wait.</para>
    /// </remarks>
    internal readonly struct DeferredTraceLine
    {
        internal DeferredTraceLine(Guid boundSession, string text, bool newLine, TraceRecordKind kind = null)
        {
            BoundSession = boundSession;
            Text = text;
            NewLine = newLine;
            Kind = kind;
        }

        /// <summary>The session this line may be written into, or
        /// <see cref="Guid.Empty"/> for whichever is current at the drain.</summary>
        internal Guid BoundSession { get; }

        internal string Text { get; }

        /// <summary>True for a whole line; false for a fragment from
        /// <c>Trace.Write</c>.</summary>
        internal bool NewLine { get; }

        /// <summary>
        /// The record kind the writer declared, captured at the moment the
        /// line was deferred, so a data record that waited out a transition
        /// still introduces itself in whichever file it lands in (#625). Null
        /// for an ordinary line.
        /// </summary>
        internal TraceRecordKind Kind { get; }
    }

    public static partial class Tracing
    {
        // ── Deferred lines, for a thread that must not wait on a file ──────
        //
        // Every ordinary TraceLine passes the trace coordinator's gate before
        // it reaches a sink, and that gate is held across a lifecycle
        // transition's flush, close, move, pending-record write and successor
        // open. On almost every thread, at almost every moment, that gate is
        // free and the write goes straight through.
        //
        // The exception is a lifecycle TRANSITION, which holds the gate across
        // file I/O. A disk that stalls under a transition would stall every
        // thread that traces — FlexLib's transport thread in the middle of a
        // teardown above all. The approved boundary design says the drop
        // callback "must not wait for the tracing lock, file operations,
        // compression, or UI dispatch" (Sol's review of H3, finding 1), and
        // the H7 review found that the claim alone returning promptly was not
        // enough: after our Connected handler returned, FlexLib's own
        // Radio.Disconnect raised further property changes on the same thread
        // and those traced through the gate as ordinary lines (Sol's review of
        // H6, finding 1, second paragraph).
        //
        // So there are two things here, and they are different:
        //
        //  1. TraceLineDeferred: a line that NEVER touches the gate, bound to
        //     the session that was recording when it was formatted. The fall's
        //     own lines use it. They are written into that session — by the
        //     archive that ends it, inside the gate, before its terminal records —
        //     or, if that session has already been archived by the time they
        //     drain, they are REFUSED: written into the current sink as an
        //     explicit refusal record naming both sessions, never as a bare
        //     line that would read as a statement about the current session.
        //     Until H7 these lines were unbound, and Sol showed the
        //     interleaving that put "Connected:False" into a successor.
        //
        //  2. The router's own policy (TraceCoordinator.RouteWriteLine): an
        //     ordinary write that finds a TRANSITION holding the gate is
        //     queued here UNBOUND rather than blocked, and the transition
        //     drains the queue into the resulting sink on its way out. That is
        //     what takes the whole of FlexLib's teardown — and every other
        //     thread — off the transition's file I/O, without marking threads
        //     or scoping anything: the read loop that raises the fall is a
        //     thread-pool task (TcpCommandCommunication.Connect), so there is
        //     no thread to mark and no end of teardown to observe.
        //
        // Ordering: deferred lines keep their order among themselves, and a
        // transition drains them before releasing the gate, so nothing written
        // directly afterwards can get in front of them. A line deferred for
        // any other reason can land after lines other threads wrote later,
        // which is why a TraceLine carries its own timestamp rather than
        // borrowing the write's. A direct System.Diagnostics.Trace line has no
        // timestamp of its own; when one is deferred its position is the only
        // clue to its moment, and that is a known cost of not blocking.

        private static readonly ConcurrentQueue<DeferredTraceLine> _deferred =
            new ConcurrentQueue<DeferredTraceLine>();
        private static int _deferredDrainScheduled;
        private static int _deferredCount;
        private static long _deferredRefused;
        private static long _deferredDroppedAtCap;

        /// <summary>
        /// The most lines the queue holds. A transition normally holds the gate
        /// for milliseconds; a stalled disk could hold it for minutes, and a
        /// Verbose session with the meter stream on writes tens of lines a
        /// second. A hundred thousand lines is a few megabytes and many minutes
        /// of stall; past it, lines are dropped and counted rather than growing
        /// the process without bound. A blocked thread was the old backpressure,
        /// and for the radio's own threads that was the worse failure.
        /// </summary>
        internal const int MaxDeferredLines = 100000;

        /// <summary>
        /// How many bound lines were refused because their session had been
        /// archived by the time they drained. A test's positive control that the
        /// refusal path exists; a diagnostic otherwise.
        /// </summary>
        public static long DeferredLinesRefused => Interlocked.Read(ref _deferredRefused);

        /// <summary>How many lines the cap discarded, ever, this process.</summary>
        public static long DeferredLinesDroppedAtCap => Interlocked.Read(ref _deferredDroppedAtCap);

        /// <summary>Lines waiting right now.</summary>
        public static int DeferredLinesQueued => Volatile.Read(ref _deferredCount);

        /// <summary>
        /// Trace a line WITHOUT waiting on the trace coordinator's gate, bound
        /// to the session recording at this moment. For a thread that must
        /// return promptly whatever the disk is doing — today, FlexLib's
        /// transport thread while our connection falls. Everywhere else, use
        /// <see cref="TraceLine(string, TraceLevel)"/>.
        ///
        /// <para>The binding is the published handle
        /// (<see cref="TraceCoordinator.CurrentHandle"/>), read without the
        /// gate. If that session has been archived by the time the line drains,
        /// the line is written into the current sink as a refusal record naming
        /// both sessions — see <see cref="TraceCoordinator"/>'s drain — so the
        /// evidence is kept and nothing reads as a statement about a session it
        /// does not describe.</para>
        /// </summary>
        public static void TraceLineDeferred(string str, TraceLevel lvl)
        {
            TraceSessionHandle bound;
            try { bound = TraceCoordinator.CurrentHandle; }
            catch { return; }
            TraceLineDeferred(str, lvl, bound);
        }

        /// <summary>
        /// The same, bound to a handle the CALLER read — so every line one
        /// event writes can be bound to the one session that event is about.
        ///
        /// <para>The two-argument form reads the published handle per call.
        /// A real fall emits several lines from several methods, and the archive
        /// request reads the handle once more, so a Stop completing between
        /// any two of those reads bound the fall's first line to one session
        /// and its later lines and its archive to the next: one fall, two
        /// identities (Sol's review of H7, the item for a harder reader).
        /// The fall now reads the handle ONCE, at its top, and passes it here
        /// and to the archive. Null binds nothing — the line lands wherever is
        /// current at the drain — which is what the per-call read did when
        /// nothing was recording, and is the right answer for a fall that
        /// began with nothing recording.</para>
        /// </summary>
        public static void TraceLineDeferred(string str, TraceLevel lvl, TraceSessionHandle boundTo)
        {
            if (!On) return;
            if (TheSwitch.Level < lvl) return;
            try
            {
                Enqueue(new DeferredTraceLine(boundTo?.SessionId ?? Guid.Empty, TracePrefix() + str, newLine: true,
                                              kind: PendingRecordKind));
                DeferredLineProbeForTests?.Invoke(str);
            }
            catch
            {
                // A trace line must never be the thing that fails a teardown.
            }
        }

        /// <summary>
        /// Tests only: called with the raw text after each deferred line is
        /// queued, on the queuing thread, so a test can hold a real callback
        /// between its first line and its later ones and complete a
        /// transition in the gap. Null in production.
        /// </summary>
        internal static Action<string> DeferredLineProbeForTests;

        /// <summary>
        /// Queue an ordinary write that found a transition holding the gate.
        /// Router use only; the line lands wherever is current at the drain.
        /// </summary>
        internal static void DeferUnbound(string text, bool newLine)
        {
            if (text == null) return;
            // The router defers on the writer's own thread, so the kind the
            // writer declared is still on this thread's static here.
            Enqueue(new DeferredTraceLine(Guid.Empty, text, newLine, PendingRecordKind));
        }

        private static void Enqueue(DeferredTraceLine line)
        {
            if (Volatile.Read(ref _deferredCount) >= MaxDeferredLines)
            {
                Interlocked.Increment(ref _deferredDroppedAtCap);
                return;
            }
            Interlocked.Increment(ref _deferredCount);
            _deferred.Enqueue(line);
            if (Interlocked.CompareExchange(ref _deferredDrainScheduled, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => DrainDeferred(), null);
            }
        }

        /// <summary>
        /// Take the next queued line. Called ONLY with the coordinator's gate
        /// held: the queue is consumed under the gate and nowhere else, so two
        /// consumers cannot interleave their writes, and a line dequeued for
        /// one session cannot wait behind an archive and then land in the next.
        /// </summary>
        internal static bool TryDequeueDeferred(out DeferredTraceLine line)
        {
            if (_deferred.TryDequeue(out line))
            {
                Interlocked.Decrement(ref _deferredCount);
                return true;
            }
            return false;
        }

        internal static void NoteDeferredRefused() => Interlocked.Increment(ref _deferredRefused);

        /// <summary>
        /// Write every deferred line still queued, on THIS thread, and return
        /// once they are written. Called at the start of work that must see the
        /// deferred lines in the file first — the drop's archive worker, so the
        /// lines describing the fall land in the session being archived rather
        /// than in its successor. May wait on the trace gate; never call it from
        /// a thread that must not.
        ///
        /// <para>Since H7 this is not what keeps a fall's lines in their own
        /// session — the binding does, and the archive drains them itself under
        /// the gate. It is still worth calling first so they read in order
        /// ahead of the worker's own lines.</para>
        /// </summary>
        public static void FlushDeferred()
        {
            try { TraceCoordinator.DrainDeferred(); }
            catch
            {
                // Same reasoning as above.
            }
        }

        private static void DrainDeferred()
        {
            FlushDeferred();
            Interlocked.Exchange(ref _deferredDrainScheduled, 0);
            // A line enqueued between the drain finishing and the flag clearing
            // found the flag still set and scheduled nothing — and a drain is
            // bounded to what was queued when it began, so a busy producer
            // can leave a remainder. Pick either up, on a fresh work item so
            // the gate is released between rounds and direct writers get in.
            if (!_deferred.IsEmpty
                && Interlocked.CompareExchange(ref _deferredDrainScheduled, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => DrainDeferred(), null);
            }
        }

        /// <summary>Tests only: forget the counters.</summary>
        internal static void ResetDeferredCountersForTests()
        {
            Interlocked.Exchange(ref _deferredRefused, 0);
            Interlocked.Exchange(ref _deferredDroppedAtCap, 0);
        }
    }
}
