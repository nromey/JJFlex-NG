#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios.Alarms
{
    /// <summary>
    /// The bounded hand-off between the meter thread and everything that is
    /// allowed to be slow: speech, sound, the UI, the journal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing on the meter thread speaks, marshals, writes or calls the
    /// radio.</b> FlexLib's meter thread delivers every reading of every meter
    /// — a hundred-odd on an 8600 — and a handler that blocks it delays every
    /// other meter's delivery, including the one the reflected-power cut reads.
    /// So the meter-thread side of an alarm is: build an observation, run the
    /// state machine (a few compares), and <see cref="Post"/> the conclusions
    /// here. This worker does the rest.
    /// </para>
    /// <para>
    /// <b>Bounded, and an overflow is a named event, never silence.</b> A
    /// queue that grows without limit under a stalled consumer is a memory
    /// leak with a delay; one that drops silently is monitoring that quietly
    /// stopped. When the cap is hit the item is refused, the count is kept,
    /// and <see cref="Overflowed"/> is raised once per overflow run so the
    /// caller can say "monitoring delivery is unavailable" out loud (design
    /// section 2).
    /// </para>
    /// <para>
    /// The cap is generous for the traffic it carries: events are transitions,
    /// not samples, so a healthy session posts a few a minute. A full queue
    /// means the consumer is wedged, and that is what the event reports.
    /// </para>
    /// </remarks>
    public sealed class AlarmDispatchQueue<T> : IDisposable
    {
        public const int DefaultCapacity = 512;

        private readonly Queue<T> _items = new Queue<T>();
        private readonly object _gate = new object();
        private readonly Action<T> _handler;
        private readonly int _capacity;
        private readonly Thread _worker;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private volatile bool _stopping;
        private long _dropped;
        private bool _inOverflow;
        private int _highWater;

        /// <param name="handler">Runs on the worker for each item, in order. A throw is traced and the worker carries on.</param>
        /// <param name="name">For the thread and the trace.</param>
        /// <param name="capacity">Items held before refusal.</param>
        public AlarmDispatchQueue(Action<T> handler, string name, int capacity = DefaultCapacity)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _capacity = capacity <= 0 ? DefaultCapacity : capacity;
            _worker = new Thread(Run)
            {
                IsBackground = true,
                Name = name ?? "AlarmDispatch",
                Priority = ThreadPriority.AboveNormal,
            };
            _worker.Start();
        }

        /// <summary>Raised on the posting thread the first time a run of overflow begins; the count says how many were refused so far.</summary>
        public event Action<long>? Overflowed;

        /// <summary>Items refused for lack of room, over the life of the queue.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>The most items ever waiting at once — the health figure the journal heartbeat reports.</summary>
        public int HighWaterMark { get { lock (_gate) return _highWater; } }

        /// <summary>Items waiting now.</summary>
        public int Pending { get { lock (_gate) return _items.Count; } }

        /// <summary>
        /// Hand an item to the worker. Returns false when refused for lack of
        /// room; the refusal is counted and announced through <see cref="Overflowed"/>.
        /// Cheap and non-blocking: a lock around a queue push and a signal.
        /// </summary>
        public bool Post(T item)
        {
            bool announce = false;
            long dropped = 0;
            lock (_gate)
            {
                if (_stopping) return false;
                if (_items.Count >= _capacity)
                {
                    dropped = Interlocked.Increment(ref _dropped);
                    if (!_inOverflow) { _inOverflow = true; announce = true; }
                }
                else
                {
                    _items.Enqueue(item);
                    if (_items.Count > _highWater) _highWater = _items.Count;
                    if (_inOverflow && _items.Count < _capacity / 2) _inOverflow = false;
                }
            }
            if (announce)
            {
                Tracing.TraceLine(
                    "AlarmDispatchQueue: full at " + _capacity + " — refusing items until the worker catches up; "
                    + "monitoring delivery is unavailable while this lasts",
                    TraceLevel.Error);
                try { Overflowed?.Invoke(dropped); } catch { /* a listener's failure must not reach the meter thread */ }
                return false;
            }
            if (dropped != 0) return false;
            _signal.Set();
            return true;
        }

        private void Run()
        {
            while (!_stopping)
            {
                T item;
                bool have;
                lock (_gate)
                {
                    have = _items.Count > 0;
                    item = have ? _items.Dequeue() : default!;
                    _working = have;
                }
                if (!have)
                {
                    _signal.WaitOne(250);
                    continue;
                }
                try { _handler(item); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("AlarmDispatchQueue: a handler threw and was skipped — " + ex.Message,
                        TraceLevel.Warning);
                }
                finally { _working = false; }
            }
        }

        /// <summary>Wait until every item posted so far has been handled, or the timeout passes. Tests and shutdown.</summary>
        public bool Drain(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_gate) { if (_items.Count == 0 && !_working) return true; }
                Thread.Sleep(5);
            }
            lock (_gate) return _items.Count == 0 && !_working;
        }

        private volatile bool _working;

        public void Dispose()
        {
            _stopping = true;
            _signal.Set();
            try { if (!_worker.Join(1000)) Tracing.TraceLine("AlarmDispatchQueue: worker did not stop within a second", TraceLevel.Warning); }
            catch { /* shutting down */ }
            _signal.Dispose();
        }
    }
}
