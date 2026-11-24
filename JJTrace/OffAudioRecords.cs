using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace JJTrace
{
    /// <summary>
    /// A fixed ring of fixed-size records, written by ONE audio thread and read
    /// off it (Sprint 49 Track RT, #1232). The one shape behind every
    /// "record now, write later" on the receive audio path: the playback
    /// callback's lines, the playback queue's timing lines, the noise slots'
    /// lag-shifter lines, the slots' switch results and the keep-up tallies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The producer's side</b> (<see cref="TryRecord"/>) copies the record
    /// into a slot made in the constructor, publishes it, and sets
    /// <see cref="Pending"/>: no lock, no allocation, no wait, nothing
    /// formatted, no clock read. A full ring drops the new record and counts it
    /// (<see cref="TakeLost"/>); it never overwrites a record a reader may be
    /// reading.
    /// </para>
    /// <para>
    /// <b>The reader's side</b> (<see cref="TakePending"/>, <see cref="TryTake"/>,
    /// <see cref="TakeLost"/>) must be serialised by the owner, by a lock the
    /// producer never takes. <see cref="OffAudioWork.Drain"/> is that lock for
    /// every owner that drains through <see cref="OffAudioDrainer"/>.
    /// </para>
    /// <para>
    /// <b>Never a large object.</b> The constructor refuses a capacity whose
    /// array would reach the runtime's large-object threshold: a large-object
    /// allocation can start a background collection, and a background
    /// collection makes every zero-allocation test in the process report bytes
    /// it never allocated (Track P2 found this the hard way, with the lag
    /// shifter's ring). <see cref="ArrayBytes"/> says what a capacity costs.
    /// </para>
    /// </remarks>
    public sealed class RecordRing<T> where T : struct
    {
        private readonly T[] _items;
        private readonly int _capacity;
        private long _head, _tail, _lost;
        private int _pending;

        /// <param name="capacity">Records held before the producer starts dropping; at least 1.</param>
        /// <exception cref="ArgumentOutOfRangeException">The array would be a large object.</exception>
        public RecordRing(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            long bytes = ArrayBytes(capacity);
            if (bytes >= LargeObjectThreshold)
                throw new ArgumentOutOfRangeException(nameof(capacity),
                    "a ring of " + capacity + " records of " + Unsafe.SizeOf<T>() + " bytes is " + bytes
                    + " bytes, a large object (the threshold is " + LargeObjectThreshold + ")");
            _capacity = capacity;
            _items = new T[capacity];
        }

        /// <summary>The runtime's large-object threshold, in bytes (85,000 unless configured).</summary>
        public static long LargeObjectThreshold
        {
            get
            {
                return GC.GetConfigurationVariables().TryGetValue("LOHThreshold", out object configured)
                    ? Convert.ToInt64(configured) : 85000;
            }
        }

        /// <summary>What an array of <paramref name="capacity"/> records takes on the heap: the elements plus the header, the method table and the length.</summary>
        public static long ArrayBytes(int capacity)
        {
            return (long)capacity * Unsafe.SizeOf<T>() + 3 * IntPtr.Size;
        }

        /// <summary>Records held before the producer drops.</summary>
        public int Capacity { get { return _capacity; } }

        /// <summary>The ring's own array, as a plain object (tests read its GC generation).</summary>
        public object Storage { get { return _items; } }

        /// <summary>
        /// The producer's side: copy <paramref name="record"/> in and publish it.
        /// One thread only. No lock, no allocation, no wait. False when the
        /// ring is full: the record is dropped and counted, and
        /// <see cref="Pending"/> is set so the reader reports the loss.
        /// </summary>
        public bool TryRecord(in T record)
        {
            long head = _head;
            if (head - Volatile.Read(ref _tail) >= _capacity)
            {
                Interlocked.Increment(ref _lost);
                Volatile.Write(ref _pending, 1);
                return false;
            }
            _items[head % _capacity] = record;
            Volatile.Write(ref _head, head + 1);
            Volatile.Write(ref _pending, 1);
            return true;
        }

        /// <summary>True when something was recorded (or dropped) since the reader last took the flag. Any thread.</summary>
        public bool Pending { get { return Volatile.Read(ref _pending) != 0; } }

        /// <summary>
        /// The reader's side: take the pending flag, before reading. Anything
        /// recorded after this sets it again, so a reader that looks at
        /// <see cref="Pending"/> after reading never leaves a record behind.
        /// </summary>
        public bool TakePending()
        {
            return Interlocked.Exchange(ref _pending, 0) != 0;
        }

        /// <summary>Records published and not yet taken. Any thread; a reading, not a promise.</summary>
        public int Count
        {
            get { return (int)Math.Max(0, Volatile.Read(ref _head) - Volatile.Read(ref _tail)); }
        }

        /// <summary>The reader's side: the oldest record not yet taken. Readers serialised by the owner.</summary>
        public bool TryTake(out T record)
        {
            long tail = _tail;
            if (tail >= Volatile.Read(ref _head)) { record = default(T); return false; }
            record = _items[tail % _capacity];
            // Clear what the slot holds, so a record carrying a reference does
            // not keep it alive until the slot is reused.
            _items[tail % _capacity] = default(T);
            Volatile.Write(ref _tail, tail + 1);
            return true;
        }

        /// <summary>The reader's side: records dropped because the ring was full, since the last call.</summary>
        public long TakeLost()
        {
            return Interlocked.Exchange(ref _lost, 0);
        }
    }
}
