using System.Threading;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// Who caused what the radio reports about its transmitter (Sprint 49 Track
    /// H2, join repair 3, Codex's P1). Our own writes are marked while they run
    /// (FlexLib raises <c>Mox</c> synchronously on the writing thread), so their
    /// echoes are ours and never come here. Everything else is STATUS, and
    /// status is late: a report can describe the radio before our request
    /// reached it. So each of our key requests opens a phase, and every report
    /// is read against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> FlexLib's <c>InterlockState</c> setter also moves <c>Mox</c>.
    /// A stale <c>Receive</c> followed by the <c>Transmitting</c> that confirms
    /// OUR earlier <c>xmit 1</c> read as someone else's unkey and key-down, and
    /// the queued unkey of our own over was withheld, leaving the radio keyed.
    /// The synchronous marker proves only the setter's own echo; it cannot say
    /// who caused a later status. This class can: a status our own request
    /// explains is never taken as someone else's over (see
    /// <c>memory/project_flex_status_echo_is_per_property.md</c>: the rule is
    /// per property, and here the property is the interlock state, whose
    /// sequence for a key request is <c>PTTRequested</c> then
    /// <c>Transmitting</c>, and for an unkey <c>UnkeyRequested</c> then
    /// receive).
    /// </para>
    /// <para>
    /// <b>The rule when in doubt is to unkey our own over.</b> A queued unkey is
    /// withheld only on positive evidence: the radio CONFIRMED our over,
    /// reported it ended, and then reported a key-down nothing of ours explains.
    /// A key request the radio never confirms keeps explaining what follows,
    /// so a foreign over that begins in that window is taken as ours and gets
    /// our unkey; that is the accepted cost of never leaving our own over keyed.
    /// </para>
    /// <para>Thread-safe: our writes come from the command queue's thread, the
    /// reports from FlexLib's receive thread, the tokens from the timer.</para>
    /// </remarks>
    internal sealed class TxOverProvenance
    {
        /// <summary>Where our latest request stands, as far as the radio has told us.</summary>
        internal enum Phase
        {
            /// <summary>No request of ours is outstanding: every report is the radio's or another client's.</summary>
            None,
            /// <summary>Our <c>xmit 1</c> is written and the radio has not yet confirmed it.</summary>
            KeyRequested,
            /// <summary>The radio confirmed our key request: the over on the air is ours.</summary>
            OnAir,
            /// <summary>Our <c>xmit 0</c> is written and the radio has not yet reported receive.</summary>
            UnkeyRequested,
        }

        /// <summary>How a report was read.</summary>
        internal enum Reading
        {
            /// <summary>Our own request explains it (a stale report, a confirmation, our unkey in progress).</summary>
            Explained,
            /// <summary>The radio ended the over on the air (ours, after it confirmed it, or someone else's).</summary>
            OverEnded,
            /// <summary>A key-down none of our requests explains.</summary>
            ForeignKeyDown,
        }

        /// <summary>The over a queued unkey belongs to: the counted reports when it was queued.</summary>
        internal readonly struct Token
        {
            public Token(long ends, long keyDowns) { Ends = ends; KeyDowns = keyDowns; }
            public long Ends { get; }
            public long KeyDowns { get; }
        }

        private readonly object _lock = new object();
        // Written under the lock, read lock-free where the audio thread asks (OwnKeyUnconfirmed).
        private volatile Phase _phase;
        private long _ends;
        private long _keyDowns;
        private long _endsAtLastKeyDown;

        /// <summary>Where our latest request stands.</summary>
        internal Phase Now { get { lock (_lock) return _phase; } }

        /// <summary>
        /// True while our key request is written and unconfirmed: the radio may
        /// be on the air for our over whatever its last report said (a stale
        /// receive). The transmit chain reads this as keyed.
        /// </summary>
        internal bool OwnKeyUnconfirmed => _phase == Phase.KeyRequested; // one volatile read: asked per buffer on the audio thread

        /// <summary>Counted ends of an over the radio reported (not ours to explain).</summary>
        internal long CountedEnds { get { lock (_lock) return _ends; } }

        /// <summary>Counted key-downs nothing of ours explains.</summary>
        internal long CountedKeyDowns { get { lock (_lock) return _keyDowns; } }

        /// <summary>Our <c>xmit 1</c> is being written (whether or not FlexLib's setter sends it).</summary>
        internal void OwnKeyDownWritten() { lock (_lock) _phase = Phase.KeyRequested; }

        /// <summary>Our <c>xmit 0</c> is being written.</summary>
        internal void OwnUnkeyWritten() { lock (_lock) _phase = Phase.UnkeyRequested; }

        /// <summary>
        /// A change of the radio's interlock state. Only the confirmation is
        /// read here (the change FlexLib raises before it moves <c>Mox</c>, and
        /// <c>PTTRequested</c> to <c>Transmitting</c> moves no <c>Mox</c> at
        /// all); ends and key-downs are counted from the <c>Mox</c> change, once.
        /// </summary>
        internal void InterlockReported(InterlockState state)
        {
            lock (_lock)
            {
                if (_phase == Phase.KeyRequested && Confirms(state)) _phase = Phase.OnAir;
            }
        }

        /// <summary>
        /// Would a receive report now be explained by our own unconfirmed key
        /// request? Asked before acting on a receive report (ending a held
        /// unkey): a stale report does not end our over.
        /// </summary>
        internal bool ReceiveReportIsStale(InterlockState state)
        {
            if (IsTransmitState(state)) return false;
            lock (_lock) return _phase == Phase.KeyRequested;
        }

        /// <summary>
        /// A <c>Mox</c> change the radio's status raised (never our own write's
        /// echo). <paramref name="state"/> is the interlock state the change
        /// came from.
        /// </summary>
        internal Reading MoxReported(bool mox, InterlockState state)
        {
            lock (_lock)
            {
                if (mox)
                {
                    switch (_phase)
                    {
                        case Phase.KeyRequested:
                            // The confirmation, unless it is the stale end of an earlier over.
                            if (state != InterlockState.UnkeyRequested) _phase = Phase.OnAir;
                            return Reading.Explained;
                        case Phase.OnAir:
                        case Phase.UnkeyRequested:
                            // Still ours: a stale confirmation, or our unkey in progress
                            // (FlexLib raises Mox true for UnkeyRequested).
                            return Reading.Explained;
                        default:
                            _endsAtLastKeyDown = _ends;
                            _keyDowns++;
                            return Reading.ForeignKeyDown;
                    }
                }
                switch (_phase)
                {
                    case Phase.KeyRequested:
                        return Reading.Explained; // stale: the radio before our request reached it
                    case Phase.UnkeyRequested:
                        _phase = Phase.None;      // our unkey done
                        return Reading.Explained;
                    case Phase.OnAir:
                        _phase = Phase.None;      // the radio ended our over
                        _ends++;
                        return Reading.OverEnded;
                    default:
                        _ends++;
                        return Reading.OverEnded;
                }
            }
        }

        /// <summary>The token a queued unkey carries.</summary>
        internal Token Capture() { lock (_lock) return new Token(_ends, _keyDowns); }

        /// <summary>True when, since <paramref name="token"/>, the radio reported an over ended and THEN a key-down nothing of ours explains.</summary>
        internal bool OverEndedAndForeignBegunSince(Token token)
        {
            lock (_lock) return _keyDowns != token.KeyDowns && _endsAtLastKeyDown > token.Ends;
        }

        /// <summary>The states FlexLib counts as Mox (<c>Radio.IsInterlockMox</c>).</summary>
        internal static bool IsTransmitState(InterlockState state) =>
            state == InterlockState.PTTRequested || state == InterlockState.Transmitting || state == InterlockState.UnkeyRequested;

        private static bool Confirms(InterlockState state) =>
            state == InterlockState.PTTRequested || state == InterlockState.Transmitting;
    }
}
