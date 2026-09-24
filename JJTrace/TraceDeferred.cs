using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace JJTrace
{
    public static partial class Tracing
    {
        // ── Deferred lines, for a thread that must not wait on a file ──────
        //
        // Every ordinary TraceLine passes the trace coordinator's gate before
        // it reaches a sink, and that gate is held across a lifecycle
        // transition's flush, close, move, pending-record write and successor
        // open. On almost every thread that is the right trade: the wait is
        // short and it is what keeps a sink switch atomic with respect to
        // every writer.
        //
        // It is the wrong trade on FlexLib's transport thread while a
        // connection is falling. A disk that stalls under a transition would
        // stall the radio library's own teardown for as long as the disk takes
        // — and the drop's claim, which must be made at the moment of the drop,
        // would wait behind it. The approved boundary design says the drop
        // callback "must not wait for the tracing lock, file operations,
        // compression, or UI dispatch" (Sprint 45 Track H6, Sol's review
        // finding 1).
        //
        // So a line written from that path is formatted NOW, on the calling
        // thread — its timestamp and thread tag are the moment it describes —
        // and handed to a queue. Nothing on the calling thread takes a lock
        // shared with any file operation. The queue is drained by a pool
        // thread, or by whoever calls FlushDeferred first.
        //
        // Ordering: deferred lines keep their order among themselves. They can
        // land in the file after lines other threads wrote later, which is why
        // they carry their own timestamps rather than borrowing the write's.

        private static readonly ConcurrentQueue<string> _deferred = new ConcurrentQueue<string>();
        private static readonly object _deferredDrain = new object();
        private static int _deferredDrainScheduled;

        /// <summary>
        /// Trace a line WITHOUT waiting on the trace coordinator's gate. For a
        /// thread that must return promptly whatever the disk is doing — today,
        /// FlexLib's transport thread while our connection falls. Everywhere
        /// else, use <see cref="TraceLine(string, TraceLevel)"/>.
        /// </summary>
        public static void TraceLineDeferred(string str, TraceLevel lvl)
        {
            if (!On) return;
            if (TheSwitch.Level < lvl) return;
            try
            {
                _deferred.Enqueue(TracePrefix() + str);
                if (Interlocked.CompareExchange(ref _deferredDrainScheduled, 1, 0) == 0)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ => DrainDeferred(), null);
                }
            }
            catch
            {
                // A trace line must never be the thing that fails a teardown.
            }
        }

        /// <summary>
        /// Write every deferred line still queued, on THIS thread, and return
        /// once they are written. Called at the start of work that must see the
        /// deferred lines in the file first — the drop's seal worker, so the
        /// lines describing the fall land in the session being sealed rather
        /// than in its successor. May wait on the trace gate; never call it from
        /// a thread that must not.
        /// </summary>
        public static void FlushDeferred()
        {
            try
            {
                lock (_deferredDrain)
                {
                    while (_deferred.TryDequeue(out string line))
                    {
                        // Already prefixed. Straight to the listeners, not
                        // through Emit, which would prefix it again.
                        Trace.WriteLine(line);
                    }
                }
            }
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
            // found the flag still set and scheduled nothing. Pick it up.
            if (!_deferred.IsEmpty
                && Interlocked.CompareExchange(ref _deferredDrainScheduled, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => DrainDeferred(), null);
            }
        }
    }
}
