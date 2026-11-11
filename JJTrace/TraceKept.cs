using System;
using System.Collections.Generic;
using System.Threading;

namespace JJTrace
{
    public static partial class Tracing
    {
        // ── Kept lines: a record that must reach a log even if none is open yet ──
        //
        // Sprint 49 Track S, repair rounds 5 to 7 (#1019). The one-time noise
        // floor move runs inside the main window's first settings load, during
        // startup, before any diagnostic log opens, and a line written with no
        // sink is dropped. A KEPT line is one that must survive that.
        //
        // Round 7 (Codex's seventh check): ONE queue. A kept line is an ordinary
        // deferred line with Keep set: it joins the same FIFO the router defers
        // into while a transition holds the gate, so it shares that queue's
        // order with every other deferred line, its coalesced pool drain (at
        // most one drain item waits, however many writers contend), and its
        // drain at the end of every transition. Round 6 had a second queue
        // beside it, drained first, which let a kept line overtake an earlier
        // ordinary line, and queued one blocking pool item per contended write.
        //
        // The one difference is what happens with no live sink. The drain drops
        // an ordinary line then, as a direct write would be dropped; a kept line
        // moves to a short list owned by the gate (TraceCoordinator, at most
        // MaxKeptLines) and is written ahead of the queue by the next drain that
        // finds a live sink. Everything on that list was taken from the head of
        // the queue, so it is older than anything still queued, and order holds.
        //
        // With no log all session, kept lines stay on that list until one opens:
        // the move happens once, and a capture started later is the only place
        // its evidence can still land. Never discarded on a timer; lost at
        // process exit like every line.
        //
        // Losses are counted and written as TraceKept lines by the next drain
        // with a live sink: lines past the no-log bound; lines refused because
        // the deferred queue itself was full; and lines whose write or flush the
        // sink failed on. A failed line is NOT written again (whether its bytes
        // reached the disk is unknowable); lines after it that were never
        // attempted wait for the next healthy sink.

        /// <summary>The most kept lines held while no log can take them.</summary>
        internal const int MaxKeptLines = 32;

        private static int _keptQueued;
        private static long _keptRefusedAtQueueCap;

        /// <summary>Kept lines waiting right now: in the deferred queue, or held for a log.</summary>
        public static int KeptLinesWaiting => Volatile.Read(ref _keptQueued) + TraceCoordinator.KeptHeldCount;

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
                Enqueue(new DeferredTraceLine(Guid.Empty, TracePrefix() + str, newLine: true,
                    keep: true, generation: TraceCoordinator.LiveSinkGenerationWithoutWaiting()));
                KeptLineProbeForTests?.Invoke(str);
                // Written now if the gate is free; otherwise the queue's own
                // coalesced drain (scheduled by Enqueue) or the transition holding
                // the gate writes it, in queue order.
                TraceCoordinator.DrainDeferredIfFree();
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

        internal static void NoteKeptQueued() => Interlocked.Increment(ref _keptQueued);
        internal static void NoteKeptDequeued() => Interlocked.Decrement(ref _keptQueued);
        internal static void NoteKeptRefusedAtQueueCap() => Interlocked.Increment(ref _keptRefusedAtQueueCap);
        internal static long KeptRefusedAtQueueCap => Interlocked.Read(ref _keptRefusedAtQueueCap);
        internal static void SpendKeptRefusedAtQueueCap(long n) => Interlocked.Add(ref _keptRefusedAtQueueCap, -n);

        /// <summary>Tests only: forget every held kept line and every loss count.</summary>
        internal static void ResetKeptForTests()
        {
            TraceCoordinator.ResetKeptForTests();
            Interlocked.Exchange(ref _keptRefusedAtQueueCap, 0);
            KeptLineProbeForTests = null;
        }
    }

    public static partial class TraceCoordinator
    {
        /// <summary>Kept lines taken from the queue while no sink was live. Owned by the gate.</summary>
        private static readonly List<DeferredTraceLine> _keptHeld = new List<DeferredTraceLine>();
        private static volatile int _keptHeldCount;
        /// <summary>Kept lines past the no-log bound. Owned by the gate.</summary>
        private static long _keptLeftOutNoLog;
        /// <summary>Kept lines whose write or flush the sink failed on. Owned by the gate.</summary>
        private static long _keptFailedWrites;

        internal static int KeptHeldCount => _keptHeldCount;

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
        /// Drain the deferred queue now if the gate is free. If it is not, the
        /// queue's own coalesced pool drain or the transition holding the gate
        /// does it; nothing extra is scheduled here.
        /// </summary>
        internal static void DrainDeferredIfFree()
        {
            if (!TryEnterForWrite()) return;
            try { DrainDeferredLocked(); }
            finally { Monitor.Exit(_gate); }
        }

        private static bool SinkLiveLocked() => _sink != null && !_sink.IsClosed;

        /// <summary>Caller holds the gate, no sink is live: keep this kept line for the next one, within the bound.</summary>
        private static void HoldKeptLocked(DeferredTraceLine line)
        {
            if (_keptHeld.Count >= Tracing.MaxKeptLines) { _keptLeftOutNoLog++; return; }
            _keptHeld.Add(line);
            _keptHeldCount = _keptHeld.Count;
        }

        /// <summary>
        /// Caller holds the gate and the sink is live. Write one kept line and
        /// flush it, so its outcome is known on its own. Returns false when the
        /// sink failed on it: the line is counted as a failed write, not retried.
        /// </summary>
        private static bool WriteKeptLocked(DeferredTraceLine line)
        {
            _sink.WriteLine(line.Generation == _sinkGeneration ? line.Text : line.Text + " (recorded before this diagnostic log opened)");
            _sink.Flush();
            if (!_sink.IsClosed) return true;
            _keptFailedWrites++;
            return false;
        }

        /// <summary>
        /// Caller holds the gate. With a live sink, write the held kept lines
        /// (older than anything queued), then one line per kind of loss counted
        /// since the last such report. Stops at a sink failure, leaving the lines
        /// it never attempted held. Returns true when it wrote anything.
        /// </summary>
        private static bool WriteHeldKeptLocked()
        {
            if (!SinkLiveLocked()) return false;
            long refused = Tracing.KeptRefusedAtQueueCap;
            if (_keptHeld.Count == 0 && _keptLeftOutNoLog == 0 && _keptFailedWrites == 0 && refused == 0) return false;
            int written = 0;
            while (written < _keptHeld.Count)
            {
                bool ok = WriteKeptLocked(_keptHeld[written]);
                written++;               // attempted: a failed one is counted, not retried
                if (!ok) break;
            }
            _keptHeld.RemoveRange(0, written);
            _keptHeldCount = _keptHeld.Count;
            if (!SinkLiveLocked()) return true;

            if (_keptLeftOutNoLog > 0 && WriteLossLocked(_keptLeftOutNoLog
                    + (_keptLeftOutNoLog == 1 ? " more line was" : " more lines were")
                    + " recorded while no diagnostic log could take them and left out; at most "
                    + Tracing.MaxKeptLines + " are kept."))
                _keptLeftOutNoLog = 0;
            if (refused > 0 && WriteLossLocked(refused
                    + (refused == 1 ? " kept line was" : " kept lines were")
                    + " left out because the queue of lines waiting for the log was full; at most "
                    + Tracing.MaxDeferredLines + " wait."))
                Tracing.SpendKeptRefusedAtQueueCap(refused);
            if (_keptFailedWrites > 0 && WriteLossLocked(_keptFailedWrites
                    + (_keptFailedWrites == 1 ? " kept line" : " kept lines")
                    + " may not have reached an earlier diagnostic log: it failed while writing them."))
                _keptFailedWrites = 0;
            return true;
        }

        /// <summary>Caller holds the gate, the sink is live. One loss line, flushed; true when the sink took it.</summary>
        private static bool WriteLossLocked(string what)
        {
            if (!SinkLiveLocked()) return false;
            _sink.WriteLine(Tracing.TracePrefix() + "TraceKept: " + what);
            _sink.Flush();
            return !_sink.IsClosed;
        }

        /// <summary>Tests only: forget every held kept line and the counts the gate owns.</summary>
        internal static void ResetKeptForTests()
        {
            lock (_gate)
            {
                _keptHeld.Clear();
                _keptHeldCount = 0;
                _keptLeftOutNoLog = 0;
                _keptFailedWrites = 0;
            }
        }
    }
}
