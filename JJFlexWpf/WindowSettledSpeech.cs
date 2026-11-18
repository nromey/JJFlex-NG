using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// Say something after a window has closed and the screen reader has
    /// announced where focus landed (Sprint 49 Track H2 bench round, item 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A screen reader flushes its speech on a window change, so a sentence
    /// handed over as a dialog closes is lost: "saved for this radio" reached
    /// ScreenReaderOutput and never NVDA (project memory: speech flushes on
    /// window change).
    /// </para>
    /// <para>
    /// <b>Not a new mechanism.</b> This is the Recorded QSOs window's
    /// <c>SayAfterFocusSettles</c>, which already solved the same problem for
    /// its confirmation windows: wait for the dispatcher to go idle (the close
    /// and the focus change have been processed), then a further 300 ms for the
    /// reader's own announcement to start, then say it queued behind that
    /// announcement. It was private to that window; it is shared here so the
    /// transmit noise setup dialogs and the Audio Workshop's preset save use
    /// the same timing rather than a guessed one. That window still has its own
    /// copy; moving it here is left for whoever next touches it.
    /// </para>
    /// </remarks>
    public static class WindowSettledSpeech
    {
        /// <summary>The wait after the dispatcher goes idle, as the Recorded QSOs window uses.</summary>
        public const int AfterIdleMs = 300;

        /// <summary>Run <paramref name="say"/> once the dispatcher is idle and <see cref="AfterIdleMs"/> more has passed, on the dispatcher's thread.</summary>
        public static void After(Dispatcher dispatcher, Action say)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(say);
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                await Task.Delay(AfterIdleMs);
                try { say(); }
                catch (Exception ex) { JJTrace.Tracing.TraceLine("WindowSettledSpeech: " + ex.Message, System.Diagnostics.TraceLevel.Error); }
            }));
        }
    }
}
