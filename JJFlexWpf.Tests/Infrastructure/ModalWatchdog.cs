using System.Runtime.InteropServices;
using System.Text;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// Kills modal Win32 dialogs that a dialog under test puts up while it is being
/// constructed.
///
/// <para><b>Why this is needed.</b> A message box runs its own modal loop on the
/// thread that raised it. When one of these dialogs shows one during construction
/// - and at least one does - the UI thread stops answering, the per-dialog
/// timeout returns to the test thread but the UI thread never comes back, and
/// every remaining dialog in the sweep times out behind it. Measured on
/// 2026-08-20: a single blocking modal turned a two-minute sweep into a
/// ten-minute hang with no output.</para>
///
/// <para>So a watchdog thread outside the Dispatcher watches a heartbeat. When
/// the heartbeat goes stale it posts WM_CLOSE to any window of class #32770 -
/// the Win32 dialog class, which is what MessageBox and the common file dialogs
/// use - owned by the UI thread. That unblocks the modal loop without touching
/// the WPF window under test. If the thread is still stuck a beat later, it
/// closes everything the thread owns.</para>
/// </summary>
public sealed class ModalWatchdog : IDisposable
{
    private const int WmClose = 0x0010;

    private delegate bool EnumThreadProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    private readonly uint _threadId;
    private readonly TimeSpan _patience;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancel = new();
    private long _lastBeatTicks = DateTime.UtcNow.Ticks;
    private int _escalations;

    /// <summary>Windows the watchdog had to close, for the report.</summary>
    public List<string> Interventions { get; } = new();

    public ModalWatchdog(uint uiThreadId, TimeSpan patience)
    {
        _threadId = uiThreadId;
        _patience = patience;
        _thread = new Thread(Loop) { IsBackground = true, Name = "JJFlex Tier1 watchdog" };
        _thread.Start();
    }

    /// <summary>Called from the UI thread whenever it is demonstrably still alive.</summary>
    public void Beat()
    {
        Interlocked.Exchange(ref _lastBeatTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _escalations, 0);
    }

    private void Loop()
    {
        while (!_cancel.IsCancellationRequested)
        {
            if (_cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2))) return;

            var last = new DateTime(Interlocked.Read(ref _lastBeatTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - last < _patience) continue;

            var escalation = Interlocked.Increment(ref _escalations);
            CloseThreadWindows(dialogsOnly: escalation < 3);
            Interlocked.Exchange(ref _lastBeatTicks, DateTime.UtcNow.Ticks);
        }
    }

    private void CloseThreadWindows(bool dialogsOnly)
    {
        try
        {
            EnumThreadWindows(_threadId, (hwnd, _) =>
            {
                var className = new StringBuilder(256);
                GetClassNameW(hwnd, className, className.Capacity);
                var name = className.ToString();

                if (dialogsOnly && !string.Equals(name, "#32770", StringComparison.Ordinal)) return true;
                if (name is "Default IME" or "MSCTFIME UI" or "IME") return true;

                lock (Interventions) Interventions.Add(name);
                PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // The watchdog must never be the thing that fails a run.
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _thread.Join(TimeSpan.FromSeconds(3));
        _cancel.Dispose();
    }
}
