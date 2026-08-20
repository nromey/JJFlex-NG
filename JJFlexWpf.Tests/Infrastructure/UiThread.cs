using System.Windows.Threading;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// The one STA thread every Tier 1 test body runs on.
///
/// <para><b>Why a hand-rolled thread and not an STA test framework.</b> xunit
/// runs test bodies on thread-pool threads, which are MTA and have no
/// Dispatcher. WPF needs both. The options were an STA xunit extension package
/// or an explicit thread; this project uses an explicit thread so the suite has
/// no dependency beyond xunit itself and so the pumping behaviour is visible in
/// source rather than buried in an attribute. Everything that touches a
/// <see cref="System.Windows.Window"/> goes through <see cref="Run(Action)"/>.</para>
///
/// <para>One thread for the whole assembly, not one per test: WPF static state
/// (resource dictionaries, the Dispatcher, font caches) has thread affinity, and
/// tearing it down and rebuilding it per test is both slow and a source of
/// spurious failures. Parallelisation is disabled assembly-wide in
/// <c>AssemblyInfo.cs</c> for the same reason.</para>
/// </summary>
public static class UiThread
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static Thread? _thread;

    /// <summary>Records what happened when the thread tried to isolate itself onto a private desktop.</summary>
    public static DesktopIsolation Isolation { get; private set; } = DesktopIsolation.NotAttempted;

    /// <summary>
    /// Set this before the first <see cref="Run(Action)"/> to request that the UI
    /// thread move itself to a private, non-interactive desktop before WPF
    /// creates anything. Off by default: the window-handle strategy already
    /// keeps windows off the screen, and desktop switching is process-global
    /// enough to be worth opting into rather than out of.
    /// </summary>
    public static bool RequestPrivateDesktop { get; set; }
        = Environment.GetEnvironmentVariable("JJFLEX_TIER1_PRIVATE_DESKTOP") == "1";

    public static Dispatcher Dispatcher
    {
        get
        {
            EnsureStarted();
            return _dispatcher!;
        }
    }

    public static void EnsureStarted()
    {
        if (_dispatcher != null) return;
        lock (Gate)
        {
            if (_dispatcher != null) return;

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                if (RequestPrivateDesktop)
                    Isolation = PrivateDesktop.MoveCurrentThread();

                // Touching CurrentDispatcher creates it for this thread.
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "JJFlex Tier1 UI",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(30));
            _thread = thread;
        }
    }

    public static void Run(Action body)
    {
        EnsureStarted();
        _dispatcher!.Invoke(body, DispatcherPriority.Normal);
    }

    public static T Run<T>(Func<T> body)
    {
        EnsureStarted();
        return _dispatcher!.Invoke(body, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Same as <see cref="Run{T}(Func{T})"/> but gives up waiting after
    /// <paramref name="timeout"/>. A dialog that puts up a modal Win32 message
    /// box during construction would otherwise block the UI thread for ever;
    /// this turns that into one reported failure instead of a hung run.
    /// </summary>
    public static T RunWithTimeout<T>(Func<T> body, TimeSpan timeout)
    {
        EnsureStarted();
        return _dispatcher!.Invoke(body, DispatcherPriority.Normal, CancellationToken.None, timeout);
    }

    /// <summary>
    /// Runs the same body on a throwaway STA thread with its own Dispatcher.
    /// Used only by the strategy probe, where one candidate strategy
    /// (private desktop) must not contaminate the shared thread.
    /// </summary>
    public static T RunOnPrivateThread<T>(Func<T> body, bool privateDesktop, TimeSpan timeout)
    {
        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                if (privateDesktop) PrivateDesktop.MoveCurrentThread();
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "JJFlex Tier1 probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!done.Wait(timeout))
            throw new TimeoutException($"Probe thread did not finish within {timeout}.");
        if (failure != null)
            throw new InvalidOperationException("Probe thread threw.", failure);
        return result;
    }

    /// <summary>
    /// Drains the Dispatcher queue down to <paramref name="priority"/>. Must be
    /// called from the UI thread. This is how Loaded handlers, layout passes and
    /// anything the dialog posted to itself during construction get to run
    /// before the tree is walked - without it, half the dialogs in this app look
    /// empty because they populate themselves in Loaded.
    /// </summary>
    public static void Drain(DispatcherPriority priority = DispatcherPriority.SystemIdle, int passes = 3)
    {
        for (var i = 0; i < passes; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                priority,
                new DispatcherOperationCallback(f => { ((DispatcherFrame)f!).Continue = false; return null; }),
                frame);
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>True when the caller is already on the shared UI thread.</summary>
    public static bool IsOnUiThread => _thread != null && Thread.CurrentThread == _thread;
}
