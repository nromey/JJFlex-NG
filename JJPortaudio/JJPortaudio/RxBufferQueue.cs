using System;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>
    /// The buffers waiting between the thread that feeds an output stream and
    /// its PortAudio callback (Sprint 49 Track RT, #1232, item 6). A fixed ring
    /// with one producer and one consumer, so the callback takes no lock any
    /// other thread can hold and allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it replaced, and why.</b> Until Track RT this was
    /// <c>Queue.Synchronized(new Queue())</c>. Every <c>Count</c> and
    /// <c>Dequeue</c> the callback made took the queue's lock, which the
    /// receive loop's <c>Enqueue</c> takes too, so a receive loop preempted
    /// inside an enqueue held the audio callback. A synchronized queue also
    /// grows its array on the producer's side under that same lock.
    /// </para>
    /// <para>
    /// <b>One producer per stream, established before choosing this</b>: the
    /// receive stream is fed only by the admitted remote-audio run's own
    /// thread (<c>JJAudioStream.WriteOpus</c> and <c>FillGap</c>, through
    /// <see cref="RxOpusChain"/>); the CW monitor's stream only by
    /// <c>JJAudioStream.Write</c> from the radio's main thread, and that path
    /// is compiled out today (<c>#if CWMonitor</c>). One consumer: the
    /// stream's callback.
    /// </para>
    /// <para>
    /// <b>Clearing</b> comes from other threads (the stop path and the audio
    /// server's start), sometimes while the callback runs. So
    /// <see cref="Clear"/> does not touch the ring: it records "everything
    /// enqueued so far is gone", and the consumer applies that mark before it
    /// takes anything. <see cref="Count"/> already leaves the cleared buffers
    /// out.
    /// </para>
    /// <para>
    /// <b>Full</b> drops the NEW buffer and counts it (<see cref="Dropped"/>):
    /// the producer never waits and never overwrites a buffer the callback
    /// may be reading. <see cref="DefaultCapacity"/> holds twenty seconds of
    /// 10 ms packets, far past the queue's own ceiling trims and the catch-up's
    /// cut; only a stream nobody is playing could fill it.
    /// </para>
    /// </remarks>
    public sealed class RxBufferQueue
    {
        /// <summary>2,048 buffers: 16 KB of references on x64, so never a large object.</summary>
        public const int DefaultCapacity = 2048;

        private readonly float[][] _slots;
        private readonly int _capacity;
        private long _head;     // written by the producer only
        private long _tail;     // written by the consumer only
        private long _clearTo;  // raised by Clear, from any thread
        private long _dropped;

        public RxBufferQueue(int capacity = DefaultCapacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _slots = new float[capacity][];
        }

        /// <summary>Buffers the ring holds before the producer drops.</summary>
        public int Capacity => _capacity;

        /// <summary>Buffers dropped because the ring was full, over its life.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>
        /// The producer's side: add a buffer. No lock, no allocation, never
        /// waits. False when the ring is full: the buffer is dropped and counted.
        /// </summary>
        public bool Enqueue(float[] buffer)
        {
            long head = _head;
            if (head - Volatile.Read(ref _tail) >= _capacity)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _slots[head % _capacity] = buffer;
            Volatile.Write(ref _head, head + 1);
            return true;
        }

        /// <summary>Buffers waiting, leaving out any a <see cref="Clear"/> has discarded. Any thread; exact on the consumer's.</summary>
        public int Count
        {
            get
            {
                long head = Volatile.Read(ref _head);
                long from = Math.Max(Volatile.Read(ref _tail), Volatile.Read(ref _clearTo));
                return (int)Math.Max(0, head - from);
            }
        }

        /// <summary>The consumer's side: the oldest buffer, or false when none waits. No lock, no allocation.</summary>
        public bool TryDequeue(out float[] buffer)
        {
            ApplyClear();
            long tail = _tail;
            if (tail >= Volatile.Read(ref _head)) { buffer = null; return false; }
            int i = (int)(tail % _capacity);
            buffer = _slots[i];
            _slots[i] = null;
            Volatile.Write(ref _tail, tail + 1);
            return true;
        }

        /// <summary>The consumer's side: the oldest buffer. Throws when none waits, as <c>Queue.Dequeue</c> did.</summary>
        public float[] Dequeue()
        {
            if (!TryDequeue(out float[] buffer)) throw new InvalidOperationException("the queue is empty");
            return buffer;
        }

        /// <summary>
        /// Discard every buffer enqueued so far. Any thread, including while the
        /// callback runs: the consumer applies it before it next takes a buffer,
        /// and <see cref="Count"/> leaves them out at once. Never waits.
        /// </summary>
        public void Clear()
        {
            long head = Volatile.Read(ref _head);
            long seen = Volatile.Read(ref _clearTo);
            while (seen < head)
            {
                long was = Interlocked.CompareExchange(ref _clearTo, head, seen);
                if (was == seen) break;
                seen = was;
            }
        }

        private void ApplyClear()
        {
            long to = Volatile.Read(ref _clearTo);
            long tail = _tail;
            if (tail >= to) return;
            for (; tail < to; tail++) _slots[(int)(tail % _capacity)] = null;
            Volatile.Write(ref _tail, tail);
        }
    }
}
