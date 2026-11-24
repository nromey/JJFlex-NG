using System;
using System.Diagnostics;
using System.Threading;

namespace JJTrace
{
    /// <summary>
    /// Something an audio path records into and the off-audio writer writes
    /// out (Sprint 49 Track RT, #1232): a ring of trace lines, a mailbox of
    /// results to raise, banks to dispose. Each owner (a stream, a playback
    /// queue's stream, a noise slot, a pipeline's keep-up raiser) makes one,
    /// registers it with the scheduler it was GIVEN
    /// (<see cref="IOffAudioScheduler"/>), and unregisters it when it is done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The audio side never schedules anything.</b> It records, then calls
    /// <see cref="Signal"/>, which is one volatile write. The scheduler looks
    /// at <see cref="Pending"/> on its own period and drains what is pending.
    /// So nothing is queued from an audio thread, there are no redundant work
    /// items to latch, a record made by the very last buffer is written within
    /// one period with no further buffer, and a stream nobody is feeding is
    /// still written.
    /// </para>
    /// <para>
    /// <b>One drain at a time, and it looks again before it stands down.</b>
    /// This is Track P2's coalescing latch, kept as the drain's own discipline:
    /// <see cref="Drain"/> holds this work's lock (shared by the scheduler's
    /// tick and an owner's synchronous flush), clears the signal BEFORE it
    /// drains, and drains again while <see cref="Pending"/> says something
    /// arrived meanwhile. Nothing recorded during a drain waits for the next
    /// period.
    /// </para>
    /// </remarks>
    public abstract class OffAudioWork
    {
        /// <summary>Passes one <see cref="Drain"/> makes before it leaves the rest to the next period: a bound on a producer that never pauses, never reached in use.</summary>
        public const int MaxPassesPerDrain = 64;

        private int _signal;
        private readonly object _drainLock = new object();
        private long _drains;
        private int _lastDrainThread;

        /// <summary>
        /// The audio side: something was recorded. One volatile write; no
        /// lock, no allocation, no kernel call, nothing queued.
        /// </summary>
        public void Signal()
        {
            Volatile.Write(ref _signal, 1);
        }

        /// <summary>
        /// True when there is something to write. Any thread, cheap. An owner
        /// whose rings carry their own pending flags overrides this to include
        /// them, and must then clear them in <see cref="DrainCore"/>.
        /// </summary>
        public virtual bool Pending
        {
            get { return Volatile.Read(ref _signal) != 0; }
        }

        /// <summary>
        /// Write everything pending, on the calling thread: the scheduler's
        /// tick, or an owner's synchronous flush from a thread that is not
        /// real-time (disposal, a stream's close). Never from an audio thread.
        /// </summary>
        public void Drain()
        {
            lock (_drainLock)
            {
                Volatile.Write(ref _lastDrainThread, Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref _drains);
                for (int pass = 0; pass < MaxPassesPerDrain; pass++)
                {
                    Interlocked.Exchange(ref _signal, 0);
                    DrainCore();
                    if (!Pending) return;
                }
            }
        }

        /// <summary>The owner's writing: format and write, raise, dispose. Serialised by <see cref="Drain"/>.</summary>
        protected abstract void DrainCore();

        /// <summary>Drains run so far (tests).</summary>
        public long Drains { get { return Interlocked.Read(ref _drains); } }

        /// <summary>The managed thread the last drain ran on (tests).</summary>
        public int LastDrainThread { get { return Volatile.Read(ref _lastDrainThread); } }
    }

    /// <summary>
    /// The off-audio writers' shared words and their one way of writing a line
    /// (Track RT): one home each, so a correction reaches every owner.
    /// </summary>
    public static class OffAudioLines
    {
        /// <summary>The sentence for records a full ring dropped, after <paramref name="prefix"/> (which names the owner) and before nothing.</summary>
        public static string Lost(string prefix, long count, string kind)
        {
            return prefix + count + " " + kind + " line(s) were lost because nothing wrote them before the record ring filled";
        }

        /// <summary>Write one line to the trace, and to a test's observer when one is set, on the calling (writer's) thread.</summary>
        public static void Write(string line, TraceLevel level, Action<string> observer)
        {
            Tracing.TraceLine(line, level);
            if (observer != null) observer(line);
        }
    }

    /// <summary>
    /// Where a <see cref="OffAudioWork"/> is drained. Given to each owner when
    /// it is built, so a test can hand one owner a
    /// <see cref="ManualOffAudioScheduler"/> and drain it exactly when it says,
    /// without changing anything any other owner does. There is deliberately
    /// no process-wide "manual mode": test classes run in parallel.
    /// </summary>
    public interface IOffAudioScheduler
    {
        /// <summary>Drain <paramref name="work"/> whenever it is pending, from now on.</summary>
        void Register(OffAudioWork work);

        /// <summary>
        /// Stop draining <paramref name="work"/>. A drain already begun may
        /// still finish, so an owner keeps its drain safe after this. Never
        /// waits for a drain.
        /// </summary>
        void Unregister(OffAudioWork work);

        /// <summary>
        /// Drain <paramref name="work"/> once more, whether or not it is
        /// pending, off the caller's thread, then stop draining it. For an
        /// owner whose last records must still be written but whose disposal
        /// must not wait for a slow writer.
        /// </summary>
        void RetireAfterDrain(OffAudioWork work);
    }

    /// <summary>
    /// The off-audio writer: ONE background thread, below normal priority,
    /// which every <see cref="OffAudioDrainer.Period"/> drains every
    /// registered <see cref="OffAudioWork"/> that is pending (Sprint 49 Track
    /// RT, #1232). It waits on its period while anything is registered and
    /// waits indefinitely when nothing is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a thread of its own and not the thread pool.</b> Track T and
    /// Track P2 each handed their writing to the pool from an audio thread,
    /// and Codex's re-checks found the class of trouble that brings: a
    /// blocked writer collected redundant work items (1,280 bytes allocated
    /// on the audio path), a record made by the last buffer waited for a
    /// buffer that might not come, and a muted stream wrote nothing until it
    /// was fed again. Here the audio side only sets a flag, so none of those
    /// can happen; and a blocked writer blocks this one thread, not the pool
    /// and not the audio.
    /// </para>
    /// <para>
    /// <b>The cost, said plainly:</b> every owner shares this thread, so a
    /// listener that blocks holds every other owner's lines until it
    /// returns. Trace listeners serialise on their own lock anyway, and the
    /// app's event listeners only post to the UI. A test that blocks a
    /// listener on purpose builds its owner with a scheduler of its own.
    /// </para>
    /// <para>
    /// A background thread: it never holds the process open. Lines still
    /// pending when the process ends are lost, exactly as pool work items
    /// were.
    /// </para>
    /// </remarks>
    public sealed class OffAudioDrainer : IOffAudioScheduler, IDisposable
    {
        /// <summary>The shared writer's period: 50 ms, under any line's useful lateness and twenty passes a second of a few flag reads each.</summary>
        public const int DefaultPeriodMilliseconds = 50;

        private static readonly Lazy<OffAudioDrainer> s_shared =
            new Lazy<OffAudioDrainer>(() => new OffAudioDrainer(DefaultPeriodMilliseconds, "JJFlex off-audio writer", ThreadPriority.BelowNormal));

        private static readonly Lazy<OffAudioDrainer> s_events =
            new Lazy<OffAudioDrainer>(() => new OffAudioDrainer(DefaultPeriodMilliseconds, "JJFlex off-audio events", ThreadPriority.Normal));

        /// <summary>
        /// The app's writer for DIAGNOSTICS: trace lines and summaries, at below
        /// normal priority, because a late line costs nothing. Made on first use;
        /// its thread starts at the first registration.
        /// </summary>
        public static OffAudioDrainer Shared { get { return s_shared.Value; } }

        /// <summary>
        /// The app's writer for EVENTS (Sprint 49 Track RT, repair 3): work whose
        /// effect an operator hears, or that holds a resource. The keep-up
        /// warning, an engine's result (its confirmation, its "could not start"),
        /// a retired bank's disposal (a helper channel stays open until then), a
        /// build or a scratch asked for from the audio thread. NORMAL priority,
        /// because a below-normal thread is starved for seconds on a busy machine
        /// (measured: 95th percentile 491 ms, worst 2.4 s with every core busy,
        /// against 62 and 68 ms at normal), and the keep-up warning matters most
        /// exactly then. Same type, same discipline, still never an audio thread.
        /// </summary>
        public static OffAudioDrainer Events { get { return s_events.Value; } }

        private readonly object _gate = new object();
        private OffAudioWork[] _works = Array.Empty<OffAudioWork>();
        private OffAudioWork[] _retiring = Array.Empty<OffAudioWork>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly string _name;
        private readonly ThreadPriority _priority;
        private Thread _thread;
        private volatile bool _stopping;
        private long _ticks, _faults;

        /// <param name="periodMilliseconds">How often pending work is drained.</param>
        /// <param name="name">The thread's name, for a debugger and a crash dump.</param>
        /// <param name="priority">The thread's priority: below normal for diagnostics, normal for events (<see cref="Events"/>).</param>
        public OffAudioDrainer(int periodMilliseconds = DefaultPeriodMilliseconds, string name = "off-audio writer",
            ThreadPriority priority = ThreadPriority.BelowNormal)
        {
            if (periodMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(periodMilliseconds));
            Period = periodMilliseconds;
            _name = name ?? "off-audio writer";
            _priority = priority;
        }

        /// <summary>The priority this writer's thread runs at.</summary>
        public ThreadPriority Priority { get { return _priority; } }

        /// <summary>Milliseconds between passes while anything is registered.</summary>
        public int Period { get; }

        /// <summary>Passes made so far (tests).</summary>
        public long Ticks { get { return Interlocked.Read(ref _ticks); } }

        /// <summary>Drains that threw (each is traced once, and the thread carries on).</summary>
        public long Faults { get { return Interlocked.Read(ref _faults); } }

        /// <summary>The writer's managed thread id, or 0 before it starts (tests).</summary>
        public int ThreadId { get { var t = _thread; return t == null ? 0 : t.ManagedThreadId; } }

        /// <summary>Works registered now (tests).</summary>
        public int RegisteredCount { get { return Volatile.Read(ref _works).Length; } }

        /// <summary>True while <paramref name="work"/> is registered here.</summary>
        public bool IsRegistered(OffAudioWork work)
        {
            return Array.IndexOf(Volatile.Read(ref _works), work) >= 0;
        }

        public void Register(OffAudioWork work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            lock (_gate)
            {
                if (_stopping) return;
                if (Array.IndexOf(_works, work) < 0) Volatile.Write(ref _works, Append(_works, work));
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Priority = _priority, Name = _name };
                    _thread.Start();
                }
            }
            _wake.Set();
        }

        public void Unregister(OffAudioWork work)
        {
            if (work == null) return;
            lock (_gate) Volatile.Write(ref _works, Remove(_works, work));
        }

        public void RetireAfterDrain(OffAudioWork work)
        {
            if (work == null) return;
            lock (_gate)
            {
                Volatile.Write(ref _works, Remove(_works, work));
                if (_stopping || _thread == null) { DrainSafely(work); return; }
                if (Array.IndexOf(_retiring, work) < 0) _retiring = Append(_retiring, work);
            }
            _wake.Set();
        }

        private void Run()
        {
            while (!_stopping)
            {
                OffAudioWork[] works = Volatile.Read(ref _works);
                for (int i = 0; i < works.Length; i++)
                    if (works[i].Pending) DrainSafely(works[i]);
                OffAudioWork[] retiring;
                lock (_gate) { retiring = _retiring; _retiring = Array.Empty<OffAudioWork>(); }
                for (int i = 0; i < retiring.Length; i++) DrainSafely(retiring[i]);
                Interlocked.Increment(ref _ticks);
                bool idle;
                lock (_gate) idle = _works.Length == 0 && _retiring.Length == 0;
                if (idle) _wake.WaitOne();
                else _wake.WaitOne(Period);
            }
        }

        private void DrainSafely(OffAudioWork work)
        {
            try { work.Drain(); }
            catch (Exception ex)
            {
                // A writer that throws must not end the thread every other owner shares.
                Interlocked.Increment(ref _faults);
                try { Trace.WriteLine("OffAudioDrainer: a drain threw (" + ex.GetType().Name + ": " + ex.Message + "); the writer carries on"); }
                catch { }
            }
        }

        /// <summary>Stop the thread (a test's own drainer). The shared one is never disposed.</summary>
        public void Dispose()
        {
            Thread t;
            lock (_gate) { _stopping = true; t = _thread; }
            _wake.Set();
            if (t != null && t != Thread.CurrentThread) t.Join(2000);
        }

        internal static OffAudioWork[] Append(OffAudioWork[] works, OffAudioWork work)
        {
            var grown = new OffAudioWork[works.Length + 1];
            Array.Copy(works, grown, works.Length);
            grown[works.Length] = work;
            return grown;
        }

        internal static OffAudioWork[] Remove(OffAudioWork[] works, OffAudioWork work)
        {
            int at = Array.IndexOf(works, work);
            if (at < 0) return works;
            var shrunk = new OffAudioWork[works.Length - 1];
            Array.Copy(works, 0, shrunk, 0, at);
            Array.Copy(works, at + 1, shrunk, at, works.Length - at - 1);
            return shrunk;
        }
    }

    /// <summary>
    /// A scheduler a test drives by hand: nothing is drained until the test
    /// calls <see cref="Tick"/>, and then on the test's own thread. Given to
    /// one owner; every other owner keeps its own (Sprint 49 Track RT).
    /// </summary>
    public sealed class ManualOffAudioScheduler : IOffAudioScheduler
    {
        private readonly object _gate = new object();
        private OffAudioWork[] _works = Array.Empty<OffAudioWork>();
        private OffAudioWork[] _retiring = Array.Empty<OffAudioWork>();

        public void Register(OffAudioWork work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            lock (_gate) if (Array.IndexOf(_works, work) < 0) _works = OffAudioDrainer.Append(_works, work);
        }

        public void Unregister(OffAudioWork work)
        {
            lock (_gate) _works = OffAudioDrainer.Remove(_works, work);
        }

        public void RetireAfterDrain(OffAudioWork work)
        {
            lock (_gate)
            {
                _works = OffAudioDrainer.Remove(_works, work);
                if (Array.IndexOf(_retiring, work) < 0) _retiring = OffAudioDrainer.Append(_retiring, work);
            }
        }

        /// <summary>Works registered now.</summary>
        public int RegisteredCount { get { lock (_gate) return _works.Length; } }

        /// <summary>True while <paramref name="work"/> is registered here.</summary>
        public bool IsRegistered(OffAudioWork work) { lock (_gate) return Array.IndexOf(_works, work) >= 0; }

        /// <summary>Works waiting for their last drain.</summary>
        public int RetiringCount { get { lock (_gate) return _retiring.Length; } }

        /// <summary>
        /// One pass, exactly what <see cref="OffAudioDrainer"/> does each
        /// period, on the calling thread: every registered work that is
        /// pending is drained, then every retiring one. Returns the drains run.
        /// </summary>
        public int Tick()
        {
            OffAudioWork[] works, retiring;
            lock (_gate) { works = _works; retiring = _retiring; _retiring = Array.Empty<OffAudioWork>(); }
            int drained = 0;
            foreach (var w in works) if (w.Pending) { w.Drain(); drained++; }
            foreach (var w in retiring) { w.Drain(); drained++; }
            return drained;
        }
    }
}
