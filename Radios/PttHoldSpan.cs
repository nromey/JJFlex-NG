#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// How long a held push to talk has been held, counted from its FIRST
    /// press, so a screen reader that delivers a held key as release-and-press
    /// pairs is one hold and not many (#1020; ruled by Noel 2026-11-09 20:27).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule, and the only one.</b> A down that comes less than
    /// <see cref="ContinuesWithinMs"/> after the last release continues the
    /// hold that release ended: its first press, and so its limit, stay where
    /// they were. So does a down with no release seen since. Any later down
    /// is a new hold. A hold ended any other way
    /// (the app's own kill, Escape, the lock taking over, a teardown) is
    /// <see cref="Forget"/>-ed, so the next down is new whatever the gap.
    /// </para>
    /// <para>
    /// <b>The bound is the JAWS absorber's, by reference.</b> Under JAWS a held
    /// key arrives as down/up pairs about 250 ms apart, the first gap as long
    /// as the Windows repeat delay, and <see cref="PttHoldFilter"/> bridges a
    /// gap for at most <see cref="PttHoldFilter.MaxFirstGapDeferMs"/>. The
    /// system-wide watchdog's missed-release window is the same constant
    /// (<see cref="SystemWidePttWatch.MissedReleaseMs"/>). So the clock, the
    /// absorber and the watchdog can never disagree about which gap is still
    /// a hold.
    /// </para>
    /// <para>
    /// <b>It never decides a release.</b> Whether an up is the operator
    /// letting go is the hold filter's question, and it stays there. This only
    /// decides which clock a hold's limit is measured on, and continuing a
    /// clock can only bring a limit sooner, never put it off.
    /// </para>
    /// <para>Pure: the caller passes the time, so a test can replay any stream.</para>
    /// </remarks>
    public sealed class PttHoldSpan
    {
        /// <summary>A down this soon after a release continues the hold it ended.</summary>
        public const int ContinuesWithinMs = PttHoldFilter.MaxFirstGapDeferMs;

        private long? _firstPressMs;
        private long? _releasedAtMs;

        /// <summary>The hold's first press, or null when no hold is being counted.</summary>
        public long? FirstPressMs => _firstPressMs;

        /// <summary>
        /// A push-to-talk down. True when it continues the hold the last
        /// release ended (its first press is kept); false when it starts a new
        /// hold (its first press is now).
        /// </summary>
        public bool NoteDown(long nowMs)
        {
            // A down with no release since (a missed up) cannot start a new
            // hold either: the key never let go as far as anyone saw.
            if (_firstPressMs != null && (_releasedAtMs is not long up || nowMs - up < ContinuesWithinMs))
            {
                _releasedAtMs = null;
                return true;
            }
            _firstPressMs = nowMs;
            _releasedAtMs = null;
            return false;
        }

        /// <summary>
        /// A push-to-talk up that ended the hold. A down inside
        /// <see cref="ContinuesWithinMs"/> continues it; after that, the hold
        /// is over. A no-op when no hold is being counted.
        /// </summary>
        public void NoteRelease(long nowMs)
        {
            if (_firstPressMs == null) return;
            _releasedAtMs = nowMs;
        }

        /// <summary>The hold ended some way other than a release: the next down is new.</summary>
        public void Forget()
        {
            _firstPressMs = null;
            _releasedAtMs = null;
        }

        /// <summary>How long the hold has run from its first press, or 0 when none is counted.</summary>
        public long HeldMs(long nowMs) => _firstPressMs is long first ? Math.Max(0, nowMs - first) : 0;
    }
}
