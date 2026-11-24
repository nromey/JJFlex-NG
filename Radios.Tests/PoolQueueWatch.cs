using System;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Threading;

namespace Radios.Tests
{
    /// <summary>
    /// Counts work queued to the thread pool FROM ONE THREAD (Sprint 49 Track
    /// RT, #1232): the thread that made the watch. The runtime announces every
    /// pool enqueue through its framework event source when a listener asks for
    /// it, on the enqueuing thread, so the count is that thread's own and other
    /// tests running in parallel do not move it. Dispose it in a finally.
    /// </summary>
    /// <remarks>
    /// Used to prove the audio path queues nothing (the old shapes queued the
    /// playback queue's drain, the slot's trace drain, the keep-up raiser, a
    /// slot's results and retirements, and its scratch). Every test that uses it
    /// shows first that it sees a queue made on purpose (<see cref="Probe"/>).
    /// </remarks>
    internal sealed class PoolQueueWatch : EventListener
    {
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private long _watched = -1;
        private int _count;

        public PoolQueueWatch()
        {
            // The pool reads whether anyone is listening when it next dispatches,
            // not when the listener asks: so one item is queued and run from
            // ANOTHER thread before the watch starts counting this one.
            var warm = new Thread(() =>
            {
                for (int i = 0; i < 2; i++)
                {
                    using var done = new ManualResetEventSlim(false);
                    ThreadPool.UnsafeQueueUserWorkItem(_ => done.Set(), null);
                    done.Wait(5000);
                }
            });
            warm.Start();
            warm.Join();
            Volatile.Write(ref _watched, GetCurrentThreadId());
        }

        /// <summary>Pool work queued from the watched thread since the last <see cref="Reset"/>.</summary>
        public int Count => Volatile.Read(ref _count);

        public void Reset() => Volatile.Write(ref _count, 0);

        /// <summary>The positive control: queue one item from this thread and wait until the watch has seen it. False when this runtime announces nothing.</summary>
        public bool Probe()
        {
            int before = Count;
            using var done = new ManualResetEventSlim(false);
            ThreadPool.UnsafeQueueUserWorkItem(_ => done.Set(), null);
            done.Wait(5000);
            for (int i = 0; i < 100 && Count == before; i++) Thread.Sleep(5);
            return Count > before;
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            // ThreadPool (2) and ThreadTransfer (16), at Verbose: what turns on the
            // per-item enqueue event in the pool's work queue.
            if (source.Name == "System.Diagnostics.Eventing.FrameworkEventSource")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)(0x0002 | 0x0010));

        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName == null || e.EventName.IndexOf("Enqueue", StringComparison.Ordinal) < 0) return;
            long watched = Volatile.Read(ref _watched);
            if (watched < 0) return;
            if ((long)GetCurrentThreadId() == watched || e.OSThreadId == watched) Interlocked.Increment(ref _count);
        }
    }
}
