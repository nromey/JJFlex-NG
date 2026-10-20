using System;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.Vita;

namespace Radios.Listenback;

/// <summary>
/// Single-producer ingress; copies into preallocated storage on the VITA thread.
/// The worker owns fan-out and may allocate. Subscribers receive independently
/// owned, read-only blocks and may retain them. A slow subscriber consumes the
/// bounded queue budget; any overflow is counted, never hidden.
/// </summary>
public sealed class IqFeed : IDisposable
{
    private readonly float[] _samples;
    private readonly Entry[] _entries;
    private long _writtenSamples, _readSamples, _writtenEntries, _readEntries;
    private long _droppedPackets, _droppedPairs;
    private Action<IqBlock>[] _subscribers = Array.Empty<Action<IqBlock>>();
    private readonly object _subscriptionGate = new();
    private readonly Task _worker;
    private volatile bool _stopping;
    private long _subscriberFailures;
    private struct Entry
    {
        public IqPacketHeader Header;
        public int Count;
        public long Offset, DroppedPackets, DroppedPairs;
    }

    public IqFeed(int rate, int bufferSeconds = 30, int packetCapacity = 32768)
    {
        if (rate <= 0 || bufferSeconds <= 0 || packetCapacity <= 0) throw new ArgumentOutOfRangeException();
        _samples = new float[checked(rate * 2 * bufferSeconds)];
        _entries = new Entry[packetCapacity];
        _worker = Task.Factory.StartNew(Drain, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);
    public long DroppedPairs => Interlocked.Read(ref _droppedPairs);
    public long SubscriberFailures => Interlocked.Read(ref _subscriberFailures);

    public IDisposable Subscribe(Action<IqBlock> subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        lock (_subscriptionGate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(IqFeed));
            var next = new Action<IqBlock>[_subscribers.Length + 1];
            Array.Copy(_subscribers, next, _subscribers.Length);
            next[^1] = subscriber;
            Volatile.Write(ref _subscribers, next);
        }
        return new Subscription(this, subscriber);
    }
    private void Unsubscribe(Action<IqBlock> subscriber)
    {
        lock (_subscriptionGate)
        {
            int i = Array.IndexOf(_subscribers, subscriber);
            if (i < 0) return;
            var next = new Action<IqBlock>[_subscribers.Length - 1];
            Array.Copy(_subscribers, 0, next, 0, i);
            Array.Copy(_subscribers, i + 1, next, i, next.Length - i);
            Volatile.Write(ref _subscribers, next);
        }
    }

    public bool Offer(VitaIFDataPacket packet) => Offer(IqPacketHeader.Copy(packet), packet.payload);
    public bool Offer(IqPacketHeader header, float[] samples)
        => Offer(header, samples, samples?.Length ?? 0);
    public bool Offer(IqPacketHeader header, float[] samples, int count)
    {
        if (_stopping || samples == null || count <= 0 || count > samples.Length || (count & 1) != 0) return false;
        long entry = _writtenEntries, offset = _writtenSamples;
        if (entry - Volatile.Read(ref _readEntries) >= _entries.Length ||
            offset - Volatile.Read(ref _readSamples) + count > _samples.Length)
        {
            Interlocked.Increment(ref _droppedPackets);
            Interlocked.Add(ref _droppedPairs, count / 2);
            return false;
        }
        int start = (int)(offset % _samples.Length);
        int first = Math.Min(count, _samples.Length - start);
        Array.Copy(samples, 0, _samples, start, first);
        if (first < count) Array.Copy(samples, first, _samples, 0, count - first);
        _entries[entry % _entries.Length] = new Entry { Header = header, Count = count,
            Offset = offset, DroppedPackets = DroppedPackets, DroppedPairs = DroppedPairs };
        _writtenSamples = offset + count;
        Volatile.Write(ref _writtenEntries, entry + 1);
        return true;
    }
    private void Drain()
    {
        while (!_stopping || _readEntries < Volatile.Read(ref _writtenEntries))
        {
            long index = _readEntries;
            if (index == Volatile.Read(ref _writtenEntries)) { Thread.Sleep(2); continue; }
            Entry e = _entries[index % _entries.Length];
            var copy = new float[e.Count];
            int start = (int)(e.Offset % _samples.Length);
            int first = Math.Min(copy.Length, _samples.Length - start);
            Array.Copy(_samples, start, copy, 0, first);
            if (first < copy.Length) Array.Copy(_samples, 0, copy, first, copy.Length - first);
            Volatile.Write(ref _readSamples, e.Offset + e.Count);
            Volatile.Write(ref _readEntries, index + 1);
            var block = new IqBlock(e.Header, copy, e.DroppedPackets, e.DroppedPairs);
            foreach (var subscriber in Volatile.Read(ref _subscribers))
            {
                try { subscriber(block); }
                catch { Interlocked.Increment(ref _subscriberFailures); }
            }
        }
    }
    /// <summary>Unsubscribe from the radio and quiesce its producer before disposing.</summary>
    public void Dispose()
    {
        _stopping = true;
        _worker.GetAwaiter().GetResult();
    }
    private sealed class Subscription(IqFeed owner, Action<IqBlock> subscriber) : IDisposable
    {
        private IqFeed _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(subscriber);
    }
}
