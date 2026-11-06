// MIT. The WDSP helper's writer-queue accounting, written from the helper's
// published behaviour (its README's "Limits", and the rule as the helper's
// send_buf and writer_thread apply it), for the stub's --reply-queue and for
// JJ Flexible's tests. No helper source is copied here: only the numbers and
// the order of the checks, restated.
//
// Two counters, as the helper keeps them:
//   raw bytes   the queued messages' own bytes;
//   charge      the same, plus 64 bytes of bookkeeping per message.
// A FrameOut or Heartbeat is dropped when RAW bytes are already above the drop
// point (16 MiB in the helper, strictly greater). A reply that would take the
// CHARGE past the limit (32 MiB in the helper, strictly greater) ends the
// connection. The one fatal Error is never dropped and never bounded. A
// message leaves both counters when the writer takes it off the queue, before
// its blocking write, not when the write completes.
//
// The stub scales the limit down; the drop point stays half of it, as 16 MiB
// is half of 32 MiB.

using System.Collections.Generic;

namespace JJFlexWpf.HelperStub;

internal sealed class WriterQueueModel
{
    /// <summary>The bookkeeping charged per message on top of its bytes.</summary>
    public const long Overhead = 64;

    /// <summary>The helper's own figures, for a model at full scale.</summary>
    public const long HelperReplyLimit = 32L * 1024 * 1024;

    public enum Kind { Reply, Droppable, Final }
    public enum Outcome { Queued, Dropped, LimitReached }

    private readonly Queue<(byte[] Bytes, Kind Kind)> _queue = new();

    /// <summary>Past this charge a reply ends the connection.</summary>
    public long Limit { get; }
    /// <summary>Above this many raw bytes a droppable message is dropped.</summary>
    public long DropAbove { get; }
    public long RawBytes { get; private set; }
    public long Charge { get; private set; }
    /// <summary>Not one of the helper's counters: the charge of the replies alone, which the stub reports.</summary>
    public long ReplyCharge { get; private set; }
    public long ReplyChargePeak { get; private set; }
    public long Dropped { get; private set; }
    public int Count => _queue.Count;

    public WriterQueueModel(long limit)
    {
        Limit = limit;
        DropAbove = limit / 2;
    }

    /// <summary>Queue one message, as the helper's send does. Not thread-safe: the caller holds its lock.</summary>
    public Outcome Enqueue(byte[] bytes, Kind kind)
    {
        long n = bytes.Length;
        if (kind == Kind.Droppable && RawBytes > DropAbove) { Dropped++; return Outcome.Dropped; }
        if (kind == Kind.Reply && Charge + n + Overhead > Limit) return Outcome.LimitReached;
        _queue.Enqueue((bytes, kind));
        RawBytes += n;
        Charge += n + Overhead;
        if (kind != Kind.Droppable)
        {
            ReplyCharge += n + Overhead;
            if (ReplyCharge > ReplyChargePeak) ReplyChargePeak = ReplyCharge;
        }
        return Outcome.Queued;
    }

    /// <summary>
    /// The writer takes the next message: it leaves both counters now, before
    /// the caller's blocking write, as in the helper. Null when empty.
    /// </summary>
    public byte[]? TakeNext()
    {
        if (_queue.Count == 0) return null;
        var (bytes, kind) = _queue.Dequeue();
        RawBytes -= bytes.Length;
        Charge -= bytes.Length + Overhead;
        if (kind != Kind.Droppable) ReplyCharge -= bytes.Length + Overhead;
        return bytes;
    }
}
