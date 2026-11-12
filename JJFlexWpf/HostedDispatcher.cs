using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace JJFlexWpf;

/// <summary>
/// The dispatcher to wait on for "after the UI settles" speech, in an app whose
/// WPF windows are hosted by a WinForms shell. <c>Application.Current</c> is
/// always null here, because no WPF <c>Application</c> is ever made, so code
/// that waits on it alone never waits at all.
/// </summary>
internal static class HostedDispatcher
{
    /// <summary>
    /// The WPF application's dispatcher when there is one, else the calling
    /// thread's own. Call it ON the UI thread, before any await or hand-off:
    /// another thread has none. Null on a thread with no dispatcher; never
    /// creates one.
    /// </summary>
    internal static Dispatcher? OfThisThread() =>
        Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
}
