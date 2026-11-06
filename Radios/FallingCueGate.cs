#nullable enable
using System;
using System.Threading;

namespace Radios
{
    /// <summary>
    /// The boundary a falling transmit cue's last check shares with every new
    /// transmission this process asks for (#863, junk box repair round 6,
    /// Codex's fifth re-check S1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A falling cue means "really off the air". Its last check (receive
    /// reported for this transmission, on this connection, nothing keyed since)
    /// and its hand-over to the earcon player must be one step against
    /// whatever would make that false. Two kinds of writer can:
    /// </para>
    /// <list type="bullet">
    /// <item>A key this process asks for: push to talk, the lock, every send
    /// and check (all through <c>FlexBase.EnqueueKeyOn</c>), and the tune
    /// carrier. They count a new transmission HERE, under this gate, before
    /// the key is queued. A payment holding the gate finishes first; a key
    /// counted first makes the payment's check fail, and an older wait that
    /// sees the count move gives itself up.</item>
    /// <item>The radio's own reports (a hand mic, a foot switch, VOX, another
    /// client): the vendor parses them, and our interlock record is written,
    /// inside the command transport's dispatch, under its ConnectionSync. The
    /// payment takes that same ConnectionSync around its check and its
    /// hand-over (<c>InterlockReports.ConnectionSyncOf</c>), so a report is
    /// either seen by the check or recorded after the tone was handed over.
    /// The connection ending or being replaced is published under it too.</item>
    /// </list>
    /// <para>
    /// <b>Lock order, everywhere:</b> the transport's ConnectionSync, then this
    /// gate, then the push-to-talk controller's own tone lock. Nothing taken
    /// under this gate takes any other lock. The reader thread already holds
    /// ConnectionSync when its handlers reach the controller (a drop ends owed
    /// tones), which is the same order, so it cannot deadlock against a
    /// payment. What the payment does inside both is read state and queue a
    /// sample to the mixer, so a key or a status line waits at most for that.
    /// VOX itself is the radio's and is never delayed.
    /// </para>
    /// <para>
    /// <b>What it cannot cover:</b> a carrier the radio has not yet reported
    /// to this process. No client-side lock reaches the radio; the tone is
    /// truthful about everything this process has been told.
    /// </para>
    /// </remarks>
    internal static class FallingCueGate
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// A key is about to be asked for: count it in its rig's own counter
        /// (repair round 7: per rig, so one radio's keys never void another's
        /// tones), after any payment already handing its tone over.
        /// </summary>
        internal static void NewTransmissionAsked(ref long counter)
        {
            lock (Gate) Interlocked.Increment(ref counter);
        }

        /// <summary>
        /// Run <paramref name="act"/> only if <paramref name="stillTrue"/> holds,
        /// as one step against <see cref="NewTransmissionAsked"/>. The caller
        /// holds the transport's ConnectionSync around this.
        /// </summary>
        internal static bool RunIf(Func<bool> stillTrue, Action act)
        {
            lock (Gate)
            {
                if (!stillTrue()) return false;
                act();
                return true;
            }
        }
    }
}
