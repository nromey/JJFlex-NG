using System;

namespace Radios
{
    /// <summary>
    /// The fail-safe behind the system-wide push to talk (#307): a held
    /// transmit that nothing is holding must end on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a key-up is not enough.</b> The hook sees the release when the
    /// release reaches the hook. If Windows has silently removed the hook (it
    /// does, after repeated timeouts — see #402), if the process missed the
    /// edge, or if the UI thread that performs the unkey is blocked, the
    /// operator has let go and the radio is still on the air from another
    /// program's window, where nothing of ours is in front of them. #307 is
    /// explicit that this is the one binding in the product that puts RF on
    /// the air from another application, so a missed release must fail to
    /// UNKEYED.
    /// </para>
    /// <para>
    /// <b>Three verdicts, three backstops.</b>
    /// <see cref="Verdict.UnkeyMissedRelease"/>: the key has been physically
    /// up for longer than any synthesising screen reader's gap, and no up edge
    /// arrived — post the release ourselves.
    /// <see cref="Verdict.KillReleaseNotHonoured"/>: a release WAS posted and
    /// the controller is still holding seconds later — the UI thread is not
    /// processing it, so drop the carrier from this thread, the way
    /// <see cref="TransmitKillSwitch"/> does.
    /// <see cref="Verdict.KillHoldTooLong"/>: nobody holds push to talk for
    /// fifteen minutes; the in-app lock already dies there
    /// (<see cref="PttConfig.HardKillSeconds"/>), and a held key from another
    /// program gets the same ceiling.
    /// </para>
    /// <para>
    /// <b>The missed-release window is sized from the JAWS absorber, not
    /// invented.</b> Under JAWS a held key arrives as down/up PAIRS with the
    /// key physically "up" between them for up to the Windows repeat delay
    /// (#216), and <see cref="PttHoldFilter"/> bridges those gaps for as long
    /// as <see cref="PttHoldFilter.MaxFirstGapDeferMs"/>. A watchdog that
    /// read a shorter gap as a missed release would unkey a JAWS user's held
    /// transmit every half second — so this window is that constant, by
    /// reference, and can never be shorter than the gap it must tolerate.
    /// The release-not-honoured grace is the same bound plus every probe
    /// extension the filter may add, so a deferral still being corroborated
    /// is never mistaken for a UI thread that has stopped.
    /// </para>
    /// <para>
    /// Pure and host-clocked, in Radios, so the stream the probe measured can
    /// be replayed against it in a test with no keyboard.
    /// </para>
    /// </remarks>
    public sealed class SystemWidePttWatch
    {
        public enum Verdict
        {
            /// <summary>Nothing to do.</summary>
            Quiet,

            /// <summary>No up edge arrived and the key has been physically up long enough: release.</summary>
            UnkeyMissedRelease,

            /// <summary>A release was posted and transmit is still held: drop the carrier directly.</summary>
            KillReleaseNotHonoured,

            /// <summary>Held past the absolute ceiling: drop the carrier directly.</summary>
            KillHoldTooLong,
        }

        /// <summary>
        /// How long the key must read physically up, with no up edge seen,
        /// before the release is presumed missed. Equal to the JAWS absorber's
        /// longest bridge, so the two can never disagree about a gap.
        /// </summary>
        public const int MissedReleaseMs = PttHoldFilter.MaxFirstGapDeferMs;

        /// <summary>
        /// How long after a posted release the controller may still report a
        /// held push to talk before the carrier is dropped from the poll
        /// thread. The absorber's longest deferral plus every probe extension
        /// it may add, rounded up.
        /// </summary>
        public const int ReleaseGraceMs =
            PttHoldFilter.MaxFirstGapDeferMs
            + PttHoldFilter.MaxProbeExtensions * PttHoldFilter.ProbeRecheckMs
            + 400;

        /// <summary>The ceiling on any held push to talk, in milliseconds.</summary>
        public static readonly int MaxHoldMs = PttConfig.HardKillSeconds * 1000;

        /// <summary>How often the host should poll while anything is in flight.</summary>
        public const int PollMs = 50;

        private bool _held;
        private long _downAtMs;
        private long? _physicallyUpSinceMs;
        private long? _releasePostedAtMs;

        /// <summary>True while a hold or a posted-but-unconfirmed release is in flight.</summary>
        public bool Busy => _held || _releasePostedAtMs != null;

        /// <summary>The hook saw the push-to-talk chord go down.</summary>
        public void NoteDown(long nowMs)
        {
            _held = true;
            _downAtMs = nowMs;
            _physicallyUpSinceMs = null;
            _releasePostedAtMs = null;
        }

        /// <summary>The hook saw the push-to-talk key come up and posted the release.</summary>
        public void NoteUp(long nowMs)
        {
            _held = false;
            _physicallyUpSinceMs = null;
            _releasePostedAtMs = nowMs;
        }

        /// <summary>
        /// The periodic question. <paramref name="physicallyDown"/> is the
        /// operating system's answer about the chord's main key;
        /// <paramref name="controllerHolding"/> is whether the PTT safety
        /// controller still reports a held (not locked) transmit.
        /// </summary>
        public Verdict Poll(long nowMs, bool physicallyDown, bool controllerHolding)
        {
            if (_held)
            {
                if (nowMs - _downAtMs >= MaxHoldMs)
                {
                    _held = false;
                    _physicallyUpSinceMs = null;
                    _releasePostedAtMs = nowMs;
                    return Verdict.KillHoldTooLong;
                }

                if (physicallyDown)
                {
                    _physicallyUpSinceMs = null;
                    return Verdict.Quiet;
                }

                _physicallyUpSinceMs ??= nowMs;
                if (nowMs - _physicallyUpSinceMs.Value >= MissedReleaseMs)
                {
                    _held = false;
                    _physicallyUpSinceMs = null;
                    _releasePostedAtMs = nowMs;
                    return Verdict.UnkeyMissedRelease;
                }
                return Verdict.Quiet;
            }

            if (_releasePostedAtMs != null)
            {
                if (!controllerHolding)
                {
                    _releasePostedAtMs = null;   // honoured; nothing in flight
                    return Verdict.Quiet;
                }
                if (nowMs - _releasePostedAtMs.Value >= ReleaseGraceMs)
                {
                    _releasePostedAtMs = null;
                    return Verdict.KillReleaseNotHonoured;
                }
            }

            return Verdict.Quiet;
        }

        /// <summary>Forget everything in flight. Used at teardown.</summary>
        public void Reset()
        {
            _held = false;
            _physicallyUpSinceMs = null;
            _releasePostedAtMs = null;
        }
    }
}
