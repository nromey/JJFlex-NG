using System;
using System.Threading;

namespace JJTrace
{
    /// <summary>
    /// One standing boot-time question: is a debug monitor registered on this
    /// machine, and is <c>OutputDebugString</c> therefore on a ten-second
    /// timeout?
    ///
    /// **Why this exists (#434).** A launch that took ten seconds per trace line
    /// was diagnosed only by reconstructing tick deltas out of the trace after
    /// the fact, and the answer turned out to be environmental — outside the
    /// process, outside the repo, and gone by the time anyone looked.
    /// <c>OutputDebugStringW</c> waits on the <c>DBWIN_BUFFER_READY</c> event
    /// with a fixed 10,000 ms timeout when the debug-monitor objects exist, so a
    /// monitor that registered and stopped servicing the buffer makes that call
    /// cost exactly ten seconds in every process on the machine. Reproduced
    /// deliberately 2026-09-01: 0.020 ms per call at baseline, 10,003.542 ms
    /// with the event created and unserviced, 0.024 ms after release.
    ///
    /// One line at boot means a future stall trace carries the answer with no
    /// reproduction needed.
    ///
    /// **The mutex is not the object to look at.** <c>DBWinMutex</c> exists on
    /// an ordinary machine — fifteen unrelated processes held it on the dev box
    /// while <c>OutputDebugString</c> cost 0.018 ms a call — because callers
    /// cache the handle. It proves nothing. The object that gates the wait is
    /// <c>DBWIN_BUFFER_READY</c>, and that is the only one this probe reads.
    ///
    /// **This opens; it never creates.** Creating the objects is what induces
    /// the fault, and it would stall every <c>OutputDebugString</c> caller on
    /// the machine — the operator's screen reader host among them — for ten
    /// seconds a call. <see cref="EventWaitHandle.OpenExisting(string)"/>
    /// cannot create, which is why it is the call used here. Nothing in this
    /// file may ever be changed to a constructor or to
    /// <c>OpenOrCreate</c>-shaped code.
    /// </summary>
    public static class DebugMonitorProbe
    {
        private const string BufferReadyEvent = "DBWIN_BUFFER_READY";

        /// <summary>
        /// One trace-ready sentence describing the debug-monitor state of this
        /// machine and whether this process can be affected by it. Never
        /// throws.
        /// </summary>
        public static string Describe()
        {
            string local = ProbeState(BufferReadyEvent);
            string global = ProbeState(@"Global\" + BufferReadyEvent);

            bool present = local == "present" || global == "present";

            string verdict = present
                ? "a debug monitor is registered, so every OutputDebugString call in "
                  + "this process would wait up to 10,000 ms on it"
                : "no debug monitor is registered, so OutputDebugString is unblocked";

            string immunity = Tracing.DefaultListenerDetached
                ? "DefaultTraceListener is detached, so tracing does not call OutputDebugString at all"
                : "DefaultTraceListener is STILL ATTACHED, so tracing calls OutputDebugString on every line";

            return "DebugMonitor: " + BufferReadyEvent + " local=" + local
                 + " global=" + global + " - " + verdict + "; " + immunity + " (#434).";
        }

        /// <summary>
        /// "present", "absent", or a short reason the question could not be
        /// answered. Opens and immediately disposes; never waits, never sets,
        /// never creates.
        /// </summary>
        private static string ProbeState(string name)
        {
            try
            {
                using (EventWaitHandle h = EventWaitHandle.OpenExisting(name))
                {
                    return "present";
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return "absent";
            }
            catch (UnauthorizedAccessException)
            {
                // It exists but this token cannot open it. "Exists" is the fact
                // that matters, and saying so is better than reporting absent.
                return "present (no access)";
            }
            catch (Exception ex)
            {
                return "unknown (" + ex.GetType().Name + ")";
            }
        }
    }
}
