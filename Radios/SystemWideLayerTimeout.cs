using System;

namespace Radios
{
    /// <summary>
    /// The clock on the system-wide JJ key's bare waiting state (#307; ruled
    /// by Noel 2026-10-05, filed under #689): a layer that was opened from
    /// another program and then forgotten must stop eating that program's
    /// keys on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is guarded, and what is deliberately not.</b> The timeout
    /// exists to stop a FORGOTTEN layer swallowing a keystroke in someone
    /// else's program. A help list or a key explorer sitting open on screen
    /// cannot be forgotten, so there is nothing left to guard — and a timer
    /// there is a trapdoor under an operator doing exactly what the surface
    /// is for, at the pace a screen reader recites rows. So the clock runs
    /// only in the bare waiting state (layer entered, nothing on screen,
    /// waiting for a second key, or waiting for H, slash or Escape after an
    /// unknown key), every key press restarts it, and while a help surface is
    /// open it is suspended and starts fresh when the surface closes. This is
    /// deliberately NOT parity with the in-window layer, which has no timeout:
    /// inside the window a forgotten layer eats OUR keystroke, which we can
    /// see; outside it eats the logger's, which nobody can.
    /// </para>
    /// <para>
    /// <b>Why ten seconds.</b> Nobody ruled a number. The two failures it
    /// trades off: too short, and an operator who has just heard "JJ" and is
    /// recalling which digit sends the CQ loses the layer mid-thought, and
    /// their next key lands in the logger as a stray character (recoverable,
    /// and visible to a sighted operator, which is what makes it tolerable);
    /// too long, and a layer armed and then abandoned — the phone rang —
    /// swallows a keystroke minutes later, which is the fault the ruling
    /// exists to prevent and which nobody can see. Ten seconds is longer than
    /// any hesitation over a second key the probe or the bench has recorded,
    /// and shorter than the exchange that follows a CQ, so a forgotten layer
    /// is gone before the operator types the next report. It is a judgement,
    /// and the constant is public so the bench can move it.
    /// </para>
    /// <para>
    /// <b>Clock-free, like the decider.</b> The host passes the time and the
    /// two facts; this class only compares. It knows nothing about which keys
    /// are waiting — <see cref="SystemWideKeyDecider.Waiting"/> says that — so
    /// the decider stays "no Win32, no timers" and this stays testable from a
    /// number.
    /// </para>
    /// </remarks>
    public sealed class SystemWideLayerTimeout
    {
        /// <summary>How long a bare waiting state lives with no key press and no surface open.</summary>
        public const int TimeoutMs = 10_000;

        private long _lastActivityMs;

        /// <summary>
        /// Something happened that should restart the clock: the layer was
        /// entered, a key was pressed while it waited, or a help surface just
        /// closed.
        /// </summary>
        public void Touch(long nowMs) => _lastActivityMs = nowMs;

        /// <summary>
        /// The periodic question. <paramref name="waiting"/> is whether the
        /// decider is in a bare waiting state; <paramref name="surfaceOpen"/>
        /// is whether a help list or the key explorer is on screen. Returns
        /// true exactly when the host should expire the waiting state. While a
        /// surface is open the clock is held at now, so the moment it closes
        /// a fresh <see cref="TimeoutMs"/> begins.
        /// </summary>
        public bool Poll(long nowMs, bool waiting, bool surfaceOpen)
        {
            if (!waiting)
            {
                _lastActivityMs = nowMs;   // nothing to time; stay fresh for the next arm
                return false;
            }
            if (surfaceOpen)
            {
                _lastActivityMs = nowMs;   // suspended: starts fresh when the surface closes
                return false;
            }
            return nowMs - _lastActivityMs >= TimeoutMs;
        }
    }
}
