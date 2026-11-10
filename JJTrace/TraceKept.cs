using System;
using System.Collections.Generic;
using System.Threading;

namespace JJTrace
{
    public static partial class Tracing
    {
        // ── Kept lines: a record that must reach a log even if none is open yet ──
        //
        // Sprint 49 Track S, repair rounds 5 and 6 (#1019). The one-time noise
        // floor move runs inside the main window's first settings load, which
        // happens during startup before any diagnostic log opens, and a line
        // written with no sink is dropped by the router. Round 5 held such lines
        // in the noise journal and drained them from the boot sequence; Codex's
        // sixth check found three holes in that: a log opened LATER (a detailed
        // capture, the standing log switched on, a retry after a failed first
        // log) never drained them; a newer line could overtake the held tail;
        // and a full hold dropped lines without a sign. So the hold lives here,
        // beside the gate it depends on.
        //
        // One ordering rule, held by the coordinator's gate: every kept line is
        // QUEUED first, and the queue is consumed only by the gate's holder,
        // only into a live sink, front to back. A line never decides "write or
        // hold" on its own, from a health reading that can change before it
        // acts. So:
        //
        //  - With a log recording, the writer takes the gate and drains the
        //    queue, its own line last: exactly where a direct write would land.
        //  - With none, the line waits. EVERY transition drains on its way out
        //    (EndTransitionLocked, through DrainDeferredLocked), so whichever
        //    one opens a log — boot, a capture, a settings change, a restart,
        //    either instance — writes the queue before anything else can enter.
        //  - A writer that cannot take the gate hands the drain to a pool item
        //    that waits for it, so nothing is stranded behind a transition that
        //    had already drained when the line was queued.
        //
        // Lock order: the gate, then this queue's lock. The writer takes the
        // queue's lock alone and releases it before going for the gate; the
        // drain never calls Trace.WriteLine, so the framework's trace lock is
        // never taken under the gate.
        //
        // While no log opens all session, the lines are KEPT, at most
        // MaxKeptLines, until one does: a detailed capture started later still
        // gets the evidence, which is the point of keeping it (the move happens
        // once, so it cannot be recorded again). They are never discarded on a
        // timer; a process that never opens a log loses them at exit, as it
        // loses every line. Past the bound, lines are counted, and the drain
        // writes one line saying how many were left out, so a partial history
        // never reads as complete.

        /// <summary>The most lines kept while no log can take them.</summary>
        internal const int MaxKeptLines = 32;

        private readonly struct KeptLine
        {
            internal KeptLine(string text, long generation) { Text = text; Generation = generation; }
            internal string Text { get; }
            /// <summary>The sink generation live when it was recorded; 0 when none was.</summary>
            internal long Generation { get; }
        }

        private static readonly object _keptLock = new object();
        private static readonly Queue<KeptLine> _kept = new Queue<KeptLine>();
        private static int _keptLeftOut;

        /// <summary>Lines kept right now, waiting for a log.</summary>
        public static int KeptLinesWaiting { get { lock (_keptLock) return _kept.Count; } }

        /// <summary>
        /// Trace a line that must reach a diagnostic log: written now when one is
        /// recording, otherwise kept (at most <see cref="MaxKeptLines"/>) and
        /// written, in order and with its own timestamp, into the next log that
        /// opens in this process. Unconditional, like a direct
        /// <c>Trace.WriteLine</c>: no level, no emission gate.
        /// </summary>
        public static void TraceLineKept(string str)
        {
            if (str == null) return;
            try
            {
                string text = TracePrefix() + str;
                long generation = TraceCoordinator.LiveSinkGenerationWithoutWaiting();
                // The bound is for lines with NO log to go to. With a log live
                // or a transition under way (one may be opening a log), the
                // queue is only waiting for the gate and empties when it frees,
                // so it gets the deferred queue's far larger guard instead: a
                // burst during an open must not be cut to 32. This reading only
                // sets the bound; it never decides where or when a line is written.
                int bound = generation != 0 || TraceCoordinator.TransitionInProgress ? MaxDeferredLines : MaxKeptLines;
                lock (_keptLock)
                {
                    if (_kept.Count >= bound) _keptLeftOut++;
                    else _kept.Enqueue(new KeptLine(text, generation));
                }
                KeptLineProbeForTests?.Invoke(str);
                TraceCoordinator.DrainKeptSoon();
            }
            catch
            {
                // A trace line must never be the thing that fails its caller.
            }
        }

        /// <summary>
        /// Tests only: called on the writing thread after a kept line is queued
        /// and before it goes for the gate, so a test can open a log in exactly
        /// that gap. Null in production.
        /// </summary>
        internal static Action<string> KeptLineProbeForTests;

        /// <summary>
        /// Take every kept line and the count left out, as one step. Called ONLY
        /// with the coordinator's gate held, so no other drain can interleave.
        /// </summary>
        internal static List<(string Text, long Generation)> TakeKept(out int leftOut)
        {
            lock (_keptLock)
            {
                var lines = new List<(string, long)>(_kept.Count);
                while (_kept.Count > 0) { var k = _kept.Dequeue(); lines.Add((k.Text, k.Generation)); }
                leftOut = _keptLeftOut;
                _keptLeftOut = 0;
                return lines;
            }
        }

        /// <summary>Tests only: forget every kept line and the count left out.</summary>
        internal static void ResetKeptForTests()
        {
            lock (_keptLock) { _kept.Clear(); _keptLeftOut = 0; }
            KeptLineProbeForTests = null;
        }
    }

    public static partial class TraceCoordinator
    {
        /// <summary>
        /// The generation of a live, open sink, read without the gate; 0 when
        /// none is. Only labels a kept line; never decides where it goes.
        /// </summary>
        internal static long LiveSinkGenerationWithoutWaiting()
        {
            PublishedSink live = _sinkForReaders;
            return live?.Sink != null && !live.Sink.ClosedWithoutWaiting ? live.Generation : 0;
        }

        /// <summary>
        /// Drain the kept lines now if the gate is free, or hand the drain to a
        /// pool item that waits for it. Never waits on a transition itself.
        /// </summary>
        internal static void DrainKeptSoon()
        {
            if (TryEnterForWrite())
            {
                try { DrainKeptLocked(); }
                finally { Monitor.Exit(_gate); }
                return;
            }
            // A transition (or a stalled writer) holds the gate. A transition
            // drains on its way out, but it may already have drained before this
            // line was queued, so a pool item drains once the gate is free.
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { lock (_gate) { DrainKeptLocked(); } }
                catch { /* as any trace write: never the thing that fails */ }
            }, null);
        }

        /// <summary>
        /// Caller holds the gate. With a live sink, write every kept line in the
        /// order it was recorded, then one line for any left out at the bound.
        /// With none, leave them kept: unlike a deferred line, a kept line is
        /// not dropped for want of a log.
        /// </summary>
        private static void DrainKeptLocked()
        {
            if (_sink == null || _sink.IsClosed) return;
            var lines = Tracing.TakeKept(out int leftOut);
            if (lines.Count == 0 && leftOut == 0) return;
            foreach (var (text, generation) in lines)
                _sink.WriteLine(generation == _sinkGeneration ? text : text + " (recorded before this diagnostic log opened)");
            if (leftOut > 0)
                _sink.WriteLine(Tracing.TracePrefix() + "TraceKept: " + leftOut
                    + (leftOut == 1 ? " more line was" : " more lines were")
                    + " recorded while no diagnostic log could take them and left out; at most "
                    + Tracing.MaxKeptLines + " are kept.");
            _sink.Flush();
            NoticeSinkFaultLocked();
        }
    }
}
