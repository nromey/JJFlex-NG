using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Rejects the one stale echo that would rewind a frequency we have just
    /// written, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug (#266).</b> Tuning is a read-modify-write against a cache the
    /// radio also writes. A press reads <c>_RXFrequency</c>, adds a step and
    /// writes it back; the setter updates the cache immediately and queues the
    /// real write to the radio. Meanwhile an in-flight <c>Freq</c> notification —
    /// carrying the value from BEFORE our write, because the radio had not seen
    /// it yet — lands in the slice handler and puts the old number back. The next
    /// press reads that old number and steps from there, so the dial
    /// under-travels; and because the ordering of echoes against presses varies
    /// with latency, it also goes non-monotonic.
    /// </para>
    /// <para>
    /// <b>Why it is a remote-only symptom.</b> On a LAN the round trip is short
    /// enough that the window barely exists. Over SmartLink it is tens to
    /// hundreds of milliseconds wide, which is longer than an operator holding a
    /// tuning key takes to press again.
    /// </para>
    /// <para>
    /// <b>What this rejects, precisely.</b> Only an echo equal to the value our
    /// write replaced. That is a deliberately narrow rule and the narrowness is
    /// the point: a clamp at a band edge, another MultiFlex client tuning, or the
    /// operator turning the physical knob all report a value we did not replace,
    /// so they are accepted at once. A guard that swallowed every unexpected echo
    /// would make the radio stop being the authority, which is a worse bug than
    /// the one being fixed and a far quieter one.
    /// </para>
    /// <para>
    /// <b>A run of steps (repair round 3, #642).</b> A held tuning key writes
    /// a chain: 14.100 to 14.1001 to 14.1002 to 14.1003, each queued. As the
    /// queue drains, each write's own report of ITS value arrives while a
    /// newer one is pending. Remembering only the last write's predecessor let
    /// the first of those reports in as "not ours", rewinding the cache to
    /// 14.1001, and a step pressed then stepped from there and lost the
    /// others. So the guard remembers every value its own chain superseded,
    /// within the give-up window, and rejects each. A value outside the chain
    /// is still believed at once.
    /// </para>
    /// <para>
    /// <b>What confirms a tune (repair round 5, #642).</b> Until this round
    /// every tune went through FlexLib's <c>Slice.Freq</c> setter, which
    /// writes the vendor's cache to our value BEFORE it sends, discards the
    /// radio's success reply, and then skips the radio's report as "no
    /// change" because the cache already holds it. So an ordinary successful
    /// tune produced no confirmation at all, and a guard waiting for one
    /// rejected the next genuine move back to a value it remembered. That
    /// premise was wrong, not incomplete. Tunes now go out through the
    /// reply-bearing command path instead (the one station connect already
    /// uses; see <c>Radios.StationConnect.CommandReply</c>), so the vendor's
    /// cache holds only what the radio reported. The radio's report of any
    /// CHANGED value is then raised and settles the guard in
    /// <see cref="Accept"/>; when there is no change left to report (we asked
    /// for the number the radio already reports) the command's own reply is
    /// the receipt (<see cref="Acknowledged"/>). Each request carries an id
    /// into its send and its reply, so a reply or a refusal is only ever about
    /// the request it answers.
    /// </para>
    /// <para>
    /// <b>A report settles only the LATEST request (repair round 6).</b> A
    /// number is not a request's identity: step up, up again and back down,
    /// and the first and third requests ask for the same number, so the radio's
    /// report of the FIRST arrives looking exactly like the third's
    /// confirmation. Statuses carry no command id, but replies do, and the
    /// radio answers commands in order. So the guard keeps every request of the
    /// chain the radio has not yet answered (<c>_unanswered</c>), and a report
    /// equal to the latest request does not settle it while an older,
    /// unanswered request asked for that same number: it is that older
    /// request's report, stale like any other.
    /// </para>
    /// <para>
    /// <b>Nothing obsolete can hold the latest waiting (repair round 7).</b>
    /// The radio answers its commands in order, so the reply to any request
    /// retires every older one still listed unanswered (<see cref="Replied"/>):
    /// its reply was lost, or will turn up late and change nothing. The
    /// latest's own reply therefore always settles it when the radio already
    /// reports its number, whatever order older replies arrive in, and the
    /// give-up window still bounds the case where no reply comes at all.
    /// </para>
    /// <para>
    /// <b>A refused tune concedes at once.</b> A locked slice, a slice gone,
    /// a fallen command connection or an error reply means nothing changed,
    /// so readers read the radio's last report straight away
    /// (<see cref="Refused"/>) rather than holding our number until a timeout
    /// that, for a locked slice, never came.
    /// </para>
    /// <para>
    /// <b>One guard per slice.</b> FlexBase keeps one for each slice (it was
    /// one for receive and one for transmit), because a write goes to a slice,
    /// and a guard shared across slices armed itself against one slice's
    /// number and judged another slice's echo by it.
    /// </para>
    /// <para>
    /// <b>It always gives up.</b> If our write is dropped, the radio keeps
    /// reporting the old value truthfully and there is no confirmation coming.
    /// <see cref="GiveUpMs"/> after the write was SENT (repair round 5: it
    /// counted from the press, so a write that waited in our own queue had
    /// almost no grace left once it went out), the guard accepts whatever the
    /// radio says and traces that it did, and readers read the radio's last
    /// report. A write still waiting in our own queue is our request on its
    /// way and is held, up to <see cref="UnsentGiveUpMs"/>. Holding a wrong
    /// cache forever — telling a blind operator they are on a frequency they
    /// are not on — is not a trade worth making for a race window.
    /// </para>
    /// </remarks>
    internal sealed class FrequencyEchoGuard
    {
        /// <summary>
        /// How long after our write was sent to wait for the radio to confirm
        /// it before deciding it never arrived and letting the radio win.
        /// </summary>
        /// <remarks>
        /// Sized against the thing being defended: a slow SmartLink round trip is
        /// commonly 100-300 ms, so this is roughly four times the worst case. It
        /// also bounds the damage of a dropped write, because until it expires we
        /// are reporting a frequency the radio is not on. Both pressures point
        /// the same way and this is the compromise.
        /// </remarks>
        internal const int GiveUpMs = 1200;

        /// <summary>
        /// How long a write still waiting in our own command queue is held as
        /// the slice's value (repair round 5). It waits on our queue, not on
        /// the radio, so it is not the round trip's timeout; it only bounds a
        /// queue that never runs it.
        /// </summary>
        internal const int UnsentGiveUpMs = 10_000;

        /// <summary>What a reader should take as a slice's frequency.</summary>
        internal enum Reading
        {
            /// <summary>Nothing of ours is outstanding: the caches, else the radio's report.</summary>
            Idle,
            /// <summary>Our request is on its way: its value.</summary>
            Pending,
            /// <summary>Our request was refused, or nothing confirmed it in time: the radio's last report.</summary>
            Conceded,
        }

        private readonly object _lock = new object();
        private readonly string _name;
        private readonly Func<long> _clock;

        private bool _armed;
        private bool _refused;
        private long _id;
        private ulong _replaced;
        private ulong _requested;
        private long _armedAt;
        private long _sentAt = -1;

        /// <summary>
        /// Every value the current chain of our own writes superseded (repair
        /// round 3), oldest first, never the value now requested. Bounded so a
        /// very long hold cannot grow it without end.
        /// </summary>
        private readonly System.Collections.Generic.List<ulong> _superseded = new();
        internal const int ChainLimit = 64;

        /// <summary>
        /// The chain's requests the radio has not answered yet, oldest first
        /// (repair round 6): their id and the number each asked for. Bounded
        /// with the chain.
        /// </summary>
        private readonly System.Collections.Generic.List<(long Id, ulong Hz)> _unanswered = new();

        /// <param name="name">Appears in traces, e.g. "slice A".</param>
        /// <param name="clock">
        /// Milliseconds from any fixed origin. Injectable so the give-up path can
        /// be tested without waiting for it, which is the only way that path ever
        /// gets exercised deliberately.
        /// </param>
        internal FrequencyEchoGuard(string name, Func<long> clock = null)
        {
            _name = name;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        /// <summary>
        /// Record that we have just asked for <paramref name="requested"/> over
        /// <paramref name="replaced"/>, and return this request's id, which its
        /// send (<see cref="Sending"/>) and its answer (<see cref="Acknowledged"/>,
        /// <see cref="Refused"/>) carry. Call this only where the cache is
        /// updated ahead of the radio; a path that lets the echo be the truth
        /// must not arm, or it would reject its own answer.
        /// </summary>
        internal long Requested(ulong replaced, ulong requested)
        {
            lock (_lock)
            {
                long now = _clock();
                bool chain = _armed && !ExpiredLocked(now);
                if (!chain) { _superseded.Clear(); _unanswered.Clear(); }
                else Supersede(_requested);
                Supersede(replaced);
                _superseded.Remove(requested);
                _armed = true;
                _refused = false;
                _replaced = replaced;
                _requested = requested;
                _armedAt = now;
                _sentAt = -1;
                long id = ++_id;
                if (_unanswered.Count >= ChainLimit) _unanswered.RemoveAt(0);
                _unanswered.Add((id, requested));
                return id;
            }
        }

        /// <summary>
        /// Request <paramref name="id"/> is being written to the radio now
        /// (repair round 5): its give-up window starts here, not at the press.
        /// Marked before the write, so its reply can never arrive ahead of it.
        /// </summary>
        internal void Sending(long id)
        {
            lock (_lock)
            {
                if (_armed && id == _id && _sentAt < 0) _sentAt = _clock();
            }
        }

        /// <summary>
        /// Request <paramref name="id"/> changed nothing (repair round 5): the
        /// slice was locked or gone, the command connection refused it, or the
        /// radio answered with an error. If it is still the latest request,
        /// readers read the radio's last report from now on, and true is
        /// returned. A refusal of an older request says nothing about a newer
        /// one, which stays pending.
        /// </summary>
        internal bool Refused(long id, string why)
        {
            string line = null;
            bool latest;
            lock (_lock)
            {
                Answered(id);
                latest = _armed && id == _id && !_refused;
                if (latest)
                {
                    _refused = true;
                    _superseded.Clear();
                    _unanswered.Clear();
                    line = "FrequencyEcho:" + _name + " the tune to " + _requested + " was refused (" + why
                        + "); reading the radio's own report";
                }
            }
            if (line != null) Tracing.TraceLine(line, TraceLevel.Info);
            return latest;
        }

        /// <summary>
        /// The radio has replied to request <paramref name="id"/>, with success
        /// or with an error (repair round 7). The radio answers its commands in
        /// order, so a reply to this request means every OLDER request has been
        /// answered too: any of those still listed as unanswered had its reply
        /// lost or delayed past this one, and is retired now. Nothing can then
        /// hold a later confirmation waiting on it, so no chain of obsolete
        /// requests can keep the guard armed.
        /// </summary>
        internal void Replied(long id)
        {
            lock (_lock) Retire(id);
        }

        /// <summary>
        /// The radio answered request <paramref name="id"/> with success, and
        /// <paramref name="reportedHz"/> is what it last reported for the slice
        /// (repair round 5). When that is the value asked for there is no
        /// change left for the radio to report, so this reply is the
        /// confirmation and the guard settles. Otherwise the radio's report of
        /// the change is still to come, and settles the guard when it arrives.
        /// A reply to an older request settles nothing, and since repair round
        /// 7 it no longer has anything to unblock either: the latest's reply
        /// has already retired every older request (<see cref="Replied"/>).
        /// </summary>
        /// <remarks>
        /// This relies on the radio sending a command's own status no later
        /// than its reply, on the one command connection: the same ordering
        /// #266's guard has always relied on among statuses. Not measured on a
        /// radio; the trace line below is how a session would show it.
        /// </remarks>
        internal void Acknowledged(long id, ulong reportedHz)
        {
            string line = null;
            lock (_lock)
            {
                Retire(id);
                if (!_armed || id != _id || _refused) return;
                if (reportedHz != _requested) return;
                _armed = false;
                _superseded.Clear();
                _unanswered.Clear();
                line = "FrequencyEcho:" + _name + " confirmed at " + reportedHz + " by the radio's reply after "
                    + (_clock() - _armedAt) + " ms";
            }
            Tracing.TraceLine(line, TraceLevel.Verbose);
        }

        /// <summary>The radio has answered request <paramref name="id"/>. Call holding the lock.</summary>
        private void Answered(long id) => _unanswered.RemoveAll(r => r.Id == id);

        /// <summary>
        /// The radio has replied to request <paramref name="id"/>, so it has
        /// answered every older one (repair round 7). Call holding the lock.
        /// </summary>
        private void Retire(long id) => _unanswered.RemoveAll(r => r.Id <= id);

        /// <summary>
        /// An older request of the chain, not yet answered, asked for
        /// <paramref name="hz"/> too, so a report of it may be that request's
        /// (repair round 6). Call holding the lock.
        /// </summary>
        private bool OlderUnansweredAsked(ulong hz)
        {
            foreach (var r in _unanswered)
                if (r.Id < _id && r.Hz == hz) return true;
            return false;
        }

        private void Supersede(ulong value)
        {
            if (_superseded.Contains(value)) return;
            if (_superseded.Count >= ChainLimit) _superseded.RemoveAt(0);
            _superseded.Add(value);
        }

        /// <summary>
        /// Refused, or out of time: <see cref="GiveUpMs"/> after the send, or
        /// <see cref="UnsentGiveUpMs"/> after the request while it has not
        /// been sent. Call holding the lock.
        /// </summary>
        private bool ExpiredLocked(long now) =>
            _refused || (_sentAt >= 0 ? now - _sentAt >= GiveUpMs : now - _armedAt >= UnsentGiveUpMs);

        /// <summary>
        /// What a reader should take as this slice's frequency, read once
        /// (repair round 5). <paramref name="pending"/> is the requested value
        /// when the answer is <see cref="Reading.Pending"/>.
        /// </summary>
        internal Reading Read(out ulong pending)
        {
            lock (_lock)
            {
                pending = _requested;
                if (!_armed) return Reading.Idle;
                return ExpiredLocked(_clock()) ? Reading.Conceded : Reading.Pending;
            }
        }

        /// <summary>
        /// The value our pending write asked for, while one is pending and not
        /// given up on. A cache refreshed from the slice's own (still old)
        /// number keeps this instead (repair round 3).
        /// </summary>
        internal bool TryPending(out ulong requested) => Read(out requested) == Reading.Pending;

        /// <summary>
        /// Should <paramref name="echoed"/> be written into the cache? True for
        /// everything except a stale report of a value our pending chain
        /// replaced.
        /// </summary>
        /// <remarks>
        /// Every trace is written AFTER the guard's lock is released (repair
        /// round 7): the UI reads this guard every 100 ms, and a trace can wait
        /// on the trace file's gate, so nothing here holds the lock across it.
        /// </remarks>
        internal bool Accept(ulong echoed)
        {
            string line = null;
            TraceLevel level = TraceLevel.Info;
            bool accepted;
            lock (_lock)
            {
                accepted = Decide(echoed, ref line, ref level);
            }
            if (line != null) Tracing.TraceLine(line, level);
            return accepted;
        }

        /// <summary>The decision behind <see cref="Accept"/>. Call holding the lock; it only describes its trace.</summary>
        private bool Decide(ulong echoed, ref string line, ref TraceLevel level)
        {
            if (!_armed) return true;

            if (echoed == _requested && !_refused && OlderUnansweredAsked(echoed))
            {
                // The latest request's number, but an older request still
                // unanswered asked for it too: this is that one's report
                // (repair round 6), and it settles nothing. The latest's own
                // reply settles it (repair round 7), whatever order the older
                // replies come back in, or the give-up window ends it.
                line = "FrequencyEcho:" + _name + " ignored a report of " + echoed
                    + " from an older request still unanswered; the latest asks for the same number";
                return false;
            }

            if (echoed == _requested && !_refused)
            {
                // Settled. The radio has our value; there is nothing left to
                // protect.
                //
                // TRACED EVEN THOUGH NOTHING WENT WRONG, because the age is
                // the round trip, and the round trip is what decides whether
                // the race can happen at all. Without this line a log with no
                // rejections in it is ambiguous — it could mean the guard is
                // holding the line, or it could mean the path under test had
                // no latency to race against and proved nothing. Reading a
                // settle of 4 ms tells you the second; 180 ms tells you the
                // window was wide open and there was simply nothing to reject.
                _armed = false;
                _superseded.Clear();
                _unanswered.Clear();
                line = "FrequencyEcho:" + _name + " settled at " + echoed + " after "
                    + (_clock() - _armedAt) + " ms";
                level = TraceLevel.Verbose;
                return true;
            }

            long now = _clock();
            if (ExpiredLocked(now))
            {
                _armed = false;
                _superseded.Clear();
                _unanswered.Clear();
                if (_refused)
                    line = "FrequencyEcho:" + _name + " accepting " + echoed + " after our refused tune to " + _requested;
                else
                {
                    line = "FrequencyEcho:" + _name + " gave up "
                        + (_sentAt >= 0 ? (now - _sentAt) + " ms after sending " : (now - _armedAt) + " ms after asking for ")
                        + _requested + " with nothing confirmed; accepting " + echoed
                        + ". The write was probably dropped.";
                    level = TraceLevel.Warning;
                }
                return true;
            }

            if (echoed == _replaced || _superseded.Contains(echoed))
            {
                line = "FrequencyEcho:" + _name + " ignored a stale echo of " + echoed
                    + " that would have rewound " + _requested + " (" + (now - _armedAt) + " ms in)";
                return false;
            }

            // Not ours and not stale: a band-edge clamp, another client, or
            // the front-panel knob. The radio is the authority.
            _armed = false;
            _superseded.Clear();
            _unanswered.Clear();
            return true;
        }
    }
}
