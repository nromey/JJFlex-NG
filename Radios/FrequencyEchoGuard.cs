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
    /// the radio also reports. Over SmartLink the radio's report of an OLDER
    /// value (the one before our write, or an earlier step of a held key)
    /// arrives after our newer request has been made, and a reader that
    /// believed it stepped from there: tuning under-travelled and went
    /// non-monotonic.
    /// </para>
    /// <para>
    /// <b>The model (repair round 8, #642).</b> Rounds 3 to 7 patched a guard
    /// that tried to tell a stale report from a genuine one by remembering the
    /// numbers a chain had superseded, which numbers older requests had asked
    /// for, and which replies were outstanding. Every round found a state it
    /// got wrong: a genuine move to a remembered number was swallowed, a long
    /// burst outgrew the memory, a report the latest was waiting for was lost.
    /// This model does not try to classify a report at all. It rests on two
    /// facts:
    /// </para>
    /// <list type="bullet">
    /// <item>Tunes go out through the reply-bearing path, not FlexLib's
    /// setter (repair round 5), so FlexLib's own number for the slice is
    /// always the radio's last report, rejected or not. Nothing is lost by
    /// keeping a report out of the READING: it is still there to read
    /// afterwards.</item>
    /// <item>The radio answers each command with a reply, and sends a
    /// command's own status no later than its reply (the one premise; see
    /// below).</item>
    /// </list>
    /// <para>
    /// So: <b>while our latest request is unanswered, a reader reads that
    /// request's value, and a report is let into the caches only when it
    /// equals that value. Once the latest request is answered (or refused, or
    /// out of time), a reader reads the radio's last report.</b> Older
    /// requests and their replies do not matter at all: the latest request
    /// overrides them at the radio, and its reply comes after their statuses.
    /// A genuine change during the window (a clamp, the knob, another client)
    /// is therefore not believed at once, as #266's guard believed it, but one
    /// reply later; it is never lost.
    /// </para>
    /// <para>
    /// <b>The states.</b> Idle, Queued (asked, not yet written), Sent
    /// (written, not answered), and AwaitingReport (answered with success
    /// while the radio's last report is not our value AND no report at all
    /// has arrived since the write, the one situation in which the reply may
    /// have overtaken our own status). The full table, every state against
    /// every event, is in the round 8 report and is pinned cell by cell in
    /// <c>FrequencyEchoGuardTests</c>.
    /// </para>
    /// <para>
    /// <b>A report ARRIVING is an event of its own (repair round 9).</b>
    /// "Has the radio reported since our write?" must count every frequency
    /// report, including one equal to FlexLib's cached number, which FlexLib's
    /// Freq notification skips. So arrivals come from FlexLib's
    /// <c>Slice.FrequencyReported</c> (a JJFlex patch, MIGRATION.md item 22)
    /// into <see cref="Reported"/>, and <see cref="Accept"/> only decides
    /// whether a CHANGED value may enter the caches.
    /// </para>
    /// <para>
    /// <b>The premise.</b> That the radio sends a command's own status no
    /// later than its reply, on the one command connection, is not measured on
    /// a radio. If it were ever false, AwaitingReport keeps the reading on our
    /// value until the next report, so a single step still reads correctly;
    /// the trace distinguishes "answered" from "answered, waiting for its
    /// report".
    /// </para>
    /// <para>
    /// <b>It always ends.</b> <see cref="GiveUpMs"/> after the write, or
    /// <see cref="UnsentGiveUpMs"/> after the request if it is never written,
    /// the guard is Idle whatever arrived, and readers read the radio.
    /// Holding a frequency the radio is not on, for a blind operator, is not a
    /// trade worth making for a race window. The guard keeps no history, so
    /// nothing in it can grow or be outgrown.
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
        /// it before letting the radio's report win.
        /// </summary>
        /// <remarks>
        /// Sized against a slow SmartLink round trip (commonly 100-300 ms), so
        /// roughly four times the worst case; it also bounds how long a lost
        /// reply can keep a reader on a number the radio may not be on.
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
            /// <summary>Nothing of ours is outstanding: the radio's last report.</summary>
            Idle,
            /// <summary>Our latest request is unanswered: its value.</summary>
            Pending,
        }

        /// <summary>The guard's state (repair round 8). See the class remarks.</summary>
        internal enum State { Idle, Queued, Sent, AwaitingReport }

        private readonly object _lock = new object();
        private readonly string _name;
        private readonly Func<long> _clock;
        private readonly Action<ulong, string> _gaveUp;

        private State _state = State.Idle;
        private long _id;
        private ulong _requested;
        private long _requestedAt;
        private long _sentAt;
        private bool _reportSinceSend;

        /// <param name="name">Appears in traces, e.g. "slice A".</param>
        /// <param name="clock">
        /// Milliseconds from any fixed origin. Injectable so the give-up path can
        /// be tested without waiting for it.
        /// </param>
        /// <param name="gaveUp">
        /// Called (outside the guard's lock) with the request's value and the
        /// give-up's trace line when the window ends by running out of time
        /// (repair round 9), so what is derived from the frequency can follow
        /// the radio as it does after an answer. When it is given, the guard
        /// does NOT write that trace itself: the give-up may be noticed by a
        /// reader holding the roster's lock, and the owner writes the line
        /// later, off that lock (repair round 11). Without an owner the guard
        /// traces it at once.
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
                _reportSinceSend = false;
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
                    _reportSinceSend = false;
                }
            }
            Note(gave, gaveUpOn);
        }

        /// <summary>
        /// The radio answered request <paramref name="id"/> with success, and
        /// <paramref name="reportedHz"/> is its last report for the slice.
        /// For the latest request in Queued or Sent: Idle when the radio
        /// already reports our value or any report has arrived since the
        /// write (that report, or a later one, is the radio's answer);
        /// otherwise AwaitingReport. An older id, or any other state: no
        /// change. Returns true when this ended the window.
        /// </summary>
        internal bool Acknowledged(long id, ulong reportedHz)
        {
            string line = null, gave;
            ulong gaveUpOn = 0;
            bool closed = false;
            lock (_lock)
            {
                gave = ExpireLocked(_clock(), ref gaveUpOn);
                if (id == _id && (_state == State.Queued || _state == State.Sent))
                {
                    if (reportedHz == _requested || _reportSinceSend)
                    {
                        _state = State.Idle;
                        closed = true;
                        line = "FrequencyEcho:" + _name + " answered after " + (_clock() - _requestedAt)
                            + " ms; the radio reports " + reportedHz + " (asked " + _requested + ")";
                    }
                    else
                    {
                        _state = State.AwaitingReport;
                        line = "FrequencyEcho:" + _name + " answered after " + (_clock() - _requestedAt)
                            + " ms with no report of it yet; holding " + _requested + " for the radio's report";
                    }
                }
            }
            Note(gave, gaveUpOn);
            if (line != null) Tracing.TraceLine(line, TraceLevel.Verbose);
            return closed;
        }

        /// <summary>
        /// Request <paramref name="id"/> changed nothing: the slice was locked
        /// or gone, the command connection refused it, or the radio answered
        /// with an error. For the latest request in any state but Idle: Idle,
        /// and true. An older id: no change, false.
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
                        + "); reading the radio's own report";
                }
            }
            if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
            return line != null;
        }

        /// <summary>
        /// Should the radio's CHANGED report <paramref name="echoed"/> go into
        /// the caches? Idle and AwaitingReport: yes. Queued or Sent: only when
        /// it is the latest request's value. Changes no state: that the report
        /// arrived is <see cref="Reported"/>'s, which FlexLib raises for every
        /// report, changed or not, just before this (repair round 9).
        /// </summary>
        internal bool Accept(ulong echoed)
        {
            string line = null, gave;
            ulong gaveUpOn = 0;
            bool accepted;
            lock (_lock)
            {
                gave = ExpireLocked(_clock(), ref gaveUpOn);
                switch (_state)
                {
                    case State.Idle:
                        accepted = true;
                        break;
                    case State.AwaitingReport:
                        accepted = true;
                        break;
                    default:
                        accepted = echoed == _requested;
                        if (!accepted)
                            line = "FrequencyEcho:" + _name + " kept " + echoed + " out of the reading until "
                                + _requested + " is answered (" + (_clock() - _requestedAt) + " ms in)";
                        break;
                }
            }
            Note(gave, gaveUpOn);
            if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
            return accepted;
        }

        /// <summary>
        /// The radio reported this slice's frequency, <paramref name="reportedHz"/>,
        /// changed or not (repair round 9). Sent: noted as a report since the
        /// write. AwaitingReport: the awaited report; Idle, and true, with
        /// <paramref name="requested"/> the request it answered. Idle and
        /// Queued: no change (a report before the write is not an answer to it).
        /// </summary>
        internal bool Reported(ulong reportedHz, out ulong requested)
        {
            string line = null, gave;
            ulong gaveUpOn = 0;
            bool closed = false;
            lock (_lock)
            {
                gave = ExpireLocked(_clock(), ref gaveUpOn);
                requested = _requested;
                if (_state == State.Sent) _reportSinceSend = true;
                else if (_state == State.AwaitingReport)
                {
                    _state = State.Idle;
                    closed = true;
                    line = "FrequencyEcho:" + _name + " the radio's report after its answer: " + reportedHz;
                }
            }
            Note(gave, gaveUpOn);
            if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
            return closed;
        }

        /// <summary>
        /// What a reader should take as this slice's frequency, read once:
        /// Pending with the latest request's value while it is unanswered
        /// (Queued, Sent or AwaitingReport), else Idle (the radio's report).
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
        /// Sent or AwaitingReport <see cref="GiveUpMs"/> after the write.
        /// Call holding the lock.
        /// </summary>
        private string ExpireLocked(long now, ref ulong gaveUpOn)
        {
            bool expired = _state switch
            {
                State.Queued => now - _requestedAt >= UnsentGiveUpMs,
                State.Sent or State.AwaitingReport => now - _sentAt >= GiveUpMs,
                _ => false,
            };
            if (!expired) return null;
            string line = "FrequencyEcho:" + _name + " gave up on " + _requested + " ("
                + (_state == State.Queued ? "never written" : "no answer") + " after "
                + (now - (_state == State.Queued ? _requestedAt : _sentAt)) + " ms); reading the radio's own report";
            _state = State.Idle;
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
