using System;
using System.Windows;
using System.Windows.Threading;
using JJTrace;

namespace JJFlexWpf
{
    /// <summary>
    /// Closes one of our windows only once the next one is up, so the
    /// foreground passes from one window of ours to another and never through
    /// the desktop underneath.
    ///
    /// <para><b>Why this exists.</b> Measured 2026-09-05 at 60 ms resolution
    /// (Sprint 45 Track A): "Searching for radios" closed, and 155 ms later
    /// "Select Radio" opened. In between, the process owned no visible window,
    /// so Windows handed the foreground down the Z-order to File Explorer. A
    /// screen reader flushes its speech on every foreground change and starts
    /// announcing the new window, so the operator heard the start of a File
    /// Explorer title, cut off by our next dialog. The same gap appeared again
    /// between the picker and the Connecting window (75 ms), and a third time -
    /// to nobody at all, <c>GetForegroundWindow() == 0</c> - when Connecting
    /// closed.</para>
    ///
    /// <para>The fix is ordering, not timing: the outgoing window stays on
    /// screen until the successor has rendered and taken the foreground. Its
    /// close then changes nothing, because it was not the foreground window
    /// when it went. Nothing here shortens a sentence or delays a close by a
    /// guessed number of milliseconds; it waits for the one event that means
    /// "the next window is really there".</para>
    ///
    /// <para><b>Two rules for callers.</b> The outgoing window must be shown
    /// with <c>Show()</c> and must not close itself - see
    /// <see cref="Dialogs.DiscoveringRadiosWindow.ShowAndWaitForSettle"/> for
    /// the pattern. And the successor's owner must never be the outgoing
    /// window: Windows destroys owned windows with their owner, so a picker
    /// owned by the window that is about to close would die with it. That is
    /// why <see cref="JJFlexDialog.OwnerHandleProvider"/> exists.</para>
    /// </summary>
    public static class WindowHandoff
    {
        /// <summary>
        /// Close <paramref name="outgoing"/> once <paramref name="successor"/>
        /// has rendered - or, if it never renders, once it has closed, because
        /// an outgoing window with no successor must not be left standing.
        /// </summary>
        public static void CloseAfterSuccessorShown(Window outgoing, Window successor)
        {
            ArgumentNullException.ThrowIfNull(outgoing);
            ArgumentNullException.ThrowIfNull(successor);

            bool done = false;

            void Finish(string how)
            {
                if (done) return;
                done = true;
                try
                {
                    if (!outgoing.IsLoaded)
                    {
                        // Already gone - closed by its caller's own cleanup.
                        return;
                    }

                    Tracing.TraceLine(
                        $"WindowHandoff: '{successor.Title}' {how}; closing '{outgoing.Title}' behind it",
                        System.Diagnostics.TraceLevel.Info);
                    outgoing.Close();

                    // Belt. ShowWindow normally activated the successor before
                    // its first render, so the outgoing window was not the
                    // foreground when it closed and nothing moved. If Windows
                    // refused that activation, its close just handed the
                    // foreground to whoever was next, and the successor is the
                    // window the operator should be in.
                    if (successor.IsLoaded && !successor.IsActive)
                    {
                        try { successor.Activate(); } catch { /* best effort */ }
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(
                        $"WindowHandoff: closing '{outgoing.Title}' failed: {ex.Message}",
                        System.Diagnostics.TraceLevel.Warning);
                }
            }

            // Posted at Background rather than run inline: ContentRendered is
            // raised from the START of JJFlexDialog.OnContentRendered, and the
            // foreground grab and Activate() come AFTER the event in that
            // override. By the time a Background-priority operation runs, the
            // successor has finished taking the foreground and the outgoing
            // window's close is invisible to the screen reader.
            successor.ContentRendered += (_, _) =>
                successor.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => Finish("has rendered")));

            successor.Closed += (_, _) => Finish("closed before it rendered");
        }
    }
}
