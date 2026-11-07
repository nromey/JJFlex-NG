using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Holds a slice's frequency at the value we last asked for while that
    /// request is on its way, and keeps the radio's reports of anything else
    /// out of the reading until the radio has answered it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug (#266).</b> Tuning is a read-modify-write against a number
    /// the radio also reports. Over SmartLink a report of an OLDER value
    /// arrived after our newer request had been made, and a reader that
    /// believed it stepped from there: tuning under-travelled and went
    /// non-monotonic.
    /// </para>
    /// <para>
    /// <b>The model, as of the 2026-11-02 junk box's second rulings round
    /// (#642, the 8600 bench of 2026-11-06).</b> Rounds 8 to 11 assumed the
    /// radio reports a tune's new frequency to the client that sent it, no
    /// later than its reply, and waited for that report (an "AwaitingReport"
    /// state). <b>Measured on the radio, that report never comes</b>: a slice's
    /// new rf_frequency goes to every OTHER client, and the sender gets only
    /// its reply, "R|0|". So every step waited, gave up after 1.2 s, and read
    /// FlexLib's cached OLD frequency, which stayed until another client
    /// connected. The model now rests on the reply alone:
    /// </para>
    /// <list type="bullet">
    /// <item><b>A success reply is the receipt.</b> The owner puts the
    /// requested value into FlexLib's cache from the reply callback
    /// (<c>Slice.AcceptTuneReply</c>, MIGRATION.md item 26), and the latest
    /// request's window ends there. Nothing waits for a report of our own
    /// write.</item>
    /// <item><b>An error reply is a refusal</b>: the window ends and readers
    /// read FlexLib's cache, the radio's last answer or report.</item>
    /// <item><b>Order settles the rest.</b> Replies and statuses arrive on one
    /// connection in the order the radio sent them, and the reply callback runs
    /// on the reader in that order. A status that arrives BEFORE our reply
    /// reports a change the radio made before it ran our tune, which our tune
    /// then overrode; it is kept out of the reading while ours is unanswered,
    /// and the reply's value replaces it. A status that arrives AFTER the reply
    /// is a later change (another client, the knob) and wins as any report
    /// does.</item>
    /// </list>
    /// <para>
    /// <b>The states.</b> Idle, Queued (asked, not yet written) and Sent
    /// (written, not answered). Older requests' replies do not end the window,
    /// but each success still updates FlexLib's cache (the radio is there as of
    /// that reply), so a refused newest step reads the last step that took.
    /// </para>
    /// <para>
    /// <b>It always ends.</b> <see cref="GiveUpMs"/> after the write, or
    /// <see cref="UnsentGiveUpMs"/> after the request if it is never written,
    /// the guard is Idle and readers read FlexLib's cache. A reply that arrives
    /// later still updates that cache (and a late REFUSAL of the request given
    /// up on is reported to the owner, <see cref="Refused"/>). Holding a
    /// frequency the radio is not on, for a blind operator, is not a trade
    /// worth making for a race window.
    /// </para>
    /// <para>
    /// <b>One guard per slice</b> (repair round 3), and every trace is written
    /// after its lock is released (repair round 7): the UI reads this guard
    /// every 100 ms.
    /// </para>
    /// </remarks>
    internal sealed class FrequencyEchoGuard
    {
        /// <summary>
        /// How long after our write was sent to wait for the radio to answer
        /// it before reading FlexLib's cache instead.
        /// </summary>
        /// <remarks>
        /// Sized against a slow SmartLink round trip (commonly 100-300 ms), so
        /// roughly four times the worst case; it also bounds how long a lost
        /// reply can keep a reader on a number the radio may not be on. Kept
        /// until the bench measures SmartLink (J5, ruled by Noel 2026-11-06).
        /// </remarks>
        internal const int GiveUpMs = 1200;

        /// <summary>
        /// How long a request still waiting in our own command queue is held
        /// as the slice's value (repair round 5). It waits on our queue, not on
        /// the radio; this only bounds a queue that never runs it.
        /// </summary>
        internal const int UnsentGiveUpMs = 10_000;

        /// <summary>What a reader should take as a slice's frequency.</summary>
        internal enum Reading
        {
            /// <summary>Nothing of ours is outstanding: FlexLib's cache.</summary>
            Idle,
            /// <summary>Our latest request is unanswered: its value.</summary>
            Pending,
        }

        /// <summary>The guard's state. See the class remarks.</summary>
        internal enum State { Idle, Queued, Sent }

        private readonly object _lock = new object();
        private readonly string _name;
        private readonly Func<long> _clock;
        private readonly Action<ulong, string> _gaveUp;

        private State _state = State.Idle;
        private long _id;
        private ulong _requested;
        private long _requestedAt;
        private long _sentAt;

        /// <summary>The request given up on, so its late refusal is still reported; 0 for none.</summary>
        private long _gaveUpId;

        /// <summary>The newest request whose success reply has been put into FlexLib's cache.</summary>
        private long _settledId;

        /// <param name="name">Appears in traces, e.g. "slice A".</param>
        /// <param name="clock">
        /// Milliseconds from any fixed origin. Injectable so the give-up path can
        /// be tested without waiting for it.
        /// </param>
        /// <param name="gaveUp">
        /// Called (outside the guard's lock) with the request's value and the
        /// give-up's trace line when the window ends by running out of time
        /// (repair round 9), so what is derived from the frequency can follow
        /// FlexLib's cache. When it is given, the guard does NOT write that
        /// trace itself: the give-up may be noticed by a reader holding the
        /// roster's lock, and the owner writes the line later, off that lock
        /// (repair round 11). Without an owner the guard traces it at once.
        /// </param>
        internal FrequencyEchoGuard(string name, Func<long> clock = null, Action<ulong, string> gaveUp = null)
        {
            _name = name;
            _clock = clock ?? (() => Environment.TickCount64);
            _gaveUp = gaveUp;
        }

        /// <summary>The state now, expiry applied. For tests and traces.</summary>
        internal State Now
        {
            get
            {
                string gave; State st; ulong gaveUpOn = 0;
                lock (_lock) { gave = ExpireLocked(_clock(), ref gaveUpOn); st = _state; }
                Note(gave, gaveUpOn);
                return st;
            }
        }

        /// <summary>
        /// We have just asked for <paramref name="requested"/>: in any state,
        /// this becomes the latest request, Queued. Returns its id, which its
        /// send and its answer carry.
        /// </summary>
        internal long Requested(ulong requested)
        {
            lock (_lock)
            {
                _state = State.Queued;
                _requested = requested;
                _requestedAt = _clock();
                _gaveUpId = 0;
                return ++_id;
            }
        }

        /// <summary>
        /// Request <paramref name="id"/> is being written now. Queued becomes
        /// Sent when it is the latest; anything else is unchanged. Marked
        /// before the write, so its reply cannot arrive ahead of it.
        /// </summary>
        internal void Sending(long id)
        {
            string gave;
            ulong gaveUpOn = 0;
            lock (_lock)
            {
                gave = ExpireLocked(_clock(), ref gaveUpOn);
                if (_state == State.Queued && id == _id)
                {
                    _state = State.Sent;
                    _sentAt = _clock();
                }
            }
            Note(gave, gaveUpOn);
        }

        /// <summary>
        /// The radio answered request <paramref name="id"/> with success: that
        /// is its receipt (the radio reports the new frequency to every client
        /// but this one). For the latest request in Queued or Sent: Idle, and
        /// true. Anything else: no change, false. The owner puts the value into
        /// FlexLib's cache for every success, latest or not.
        /// </summary>
        internal bool Acknowledged(long id)
        {
            string line = null, gave;
            ulong gaveUpOn = 0;
            bool closed = false;
            lock (_lock)
            {
                gave = ExpireLocked(_clock(), ref gaveUpOn);
                if (id == _id && (_state == State.Queued || _state == State.Sent))
                {
                    _state = State.Idle;
                    closed = true;
                    line = "FrequencyEcho:" + _name + " answered after " + (_clock() - _requestedAt)
                        + " ms; settled at " + _requested + ", the radio's reply being its receipt";
                }
            }
            Note(gave, gaveUpOn);
            if (line != null) Tracing.TraceLine(line, TraceLevel.Verbose);
            return closed;
        }

        /// <summary>
        /// Whether the success reply to request <paramref name="id"/> is newer
        /// than every reply already put into FlexLib's cache, and so may be. One
        /// connection delivers replies in order; this keeps an out-of-order
        /// reply (a test can make one) from setting the cache back.
        /// </summary>
        internal bool NewestReply(long id)
        {
            lock (_lock)
            {
                if (id <= _settledId) return false;
                _settledId = id;
                return true;
            }
        }

        /// <summary>
        /// Request <paramref name="id"/> changed nothing: the slice was locked
        /// or gone, the command connection refused it, or the radio answered
        /// with an error. For the latest request in any state but Idle: Idle,
        /// and true. For the latest request already given up on: true, once,
        /// so its owner can say where the slice really is (a late refusal). An
        /// older id: no change, false.
        /// </summary>
        internal bool Refused(long id, string why)
        {
            string line = null;
            lock (_lock)
            {
                if (id == _id && _state != State.Idle)
                {
                    _state = State.Idle;
                    line = "FrequencyEcho:" + _name + " the tune to " + _requested + " was refused (" + why
                        + "); reading the radio's last answer";
                }
                else if (id == _id && _gaveUpId == id)
                {
                    _gaveUpId = 0;
                    line = "FrequencyEcho:" + _name + " the tune to " + _requested + " was refused late, after it was"
                        + " given up on (" + why + "); reading the radio's last answer";
                }
            }
            if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
            return line != null;
        }

        /// <summary>
        /// Should the radio's CHANGED report <paramref name="echoed"/> go into
        /// the caches? Idle: yes. Queued or Sent: only when it is the latest
        /// request's value; any other was made at the radio before our request
        /// ran, and our request overrides it there.
        /// </summary>
        internal bool Accept(ulong echoed)
        {
            string line = null, gave;
            ulong gaveUpOn = 0;
            bool accepted;
            lock (_lock)
            {
                gave = ExpireLocked(_clock(), ref gaveUpOn);
                if (_state == State.Idle) accepted = true;
                else
                {
                    accepted = echoed == _requested;
                    if (!accepted)
                        line = "FrequencyEcho:" + _name + " kept " + echoed + " out of the reading until "
                            + _requested + " is answered (" + (_clock() - _requestedAt) + " ms in)";
                }
            }
            Note(gave, gaveUpOn);
            if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
            return accepted;
        }

        /// <summary>
        /// What a reader should take as this slice's frequency, read once:
        /// Pending with the latest request's value while it is unanswered
        /// (Queued or Sent), else Idle (FlexLib's cache).
        /// </summary>
        internal Reading Read(out ulong pending)
        {
            string gave;
            ulong gaveUpOn = 0;
            Reading r;
            lock (_lock)
            {
                gave = ExpireLocked(_clock(), ref gaveUpOn);
                pending = _requested;
                r = _state == State.Idle ? Reading.Idle : Reading.Pending;
            }
            Note(gave, gaveUpOn);
            return r;
        }

        /// <summary>The latest request's value while it is unanswered.</summary>
        internal bool TryPending(out ulong requested) => Read(out requested) == Reading.Pending;

        /// <summary>
        /// Out of time: Queued <see cref="UnsentGiveUpMs"/> after the request,
        /// Sent <see cref="GiveUpMs"/> after the write. Call holding the lock.
        /// </summary>
        private string ExpireLocked(long now, ref ulong gaveUpOn)
        {
            bool expired = _state switch
            {
                State.Queued => now - _requestedAt >= UnsentGiveUpMs,
                State.Sent => now - _sentAt >= GiveUpMs,
                _ => false,
            };
            if (!expired) return null;
            string line = "FrequencyEcho:" + _name + " gave up on " + _requested + " ("
                + (_state == State.Queued ? "never written" : "no answer") + " after "
                + (now - (_state == State.Queued ? _requestedAt : _sentAt)) + " ms); reading the radio's last answer";
            _state = State.Idle;
            _gaveUpId = _id;
            gaveUpOn = _requested;
            return line;
        }

        /// <summary>
        /// A give-up: handed to the owner, with its trace line, outside the
        /// guard's lock. The owner writes the line (repair round 11); with no
        /// owner, the guard writes it.
        /// </summary>
        private void Note(string line, ulong gaveUpOn)
        {
            if (line == null) return;
            if (_gaveUp == null) { Tracing.TraceLine(line, TraceLevel.Warning); return; }
            _gaveUp(gaveUpOn, line);
        }
    }
}
