using System;
using System.Threading;
using System.Windows.Forms;

namespace RadioInTheLoop;

/// <summary>
/// The stand-in for the operator's UI thread: a real STA thread running a real
/// WinForms message pump, with a heartbeat that can only tick while messages
/// are actually being pumped. No window is ever created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the instrument, and why it is honest.</b> The 2026-08-30
/// fault was a connect path holding the application's UI thread for 45
/// seconds; speech died, typing died, and nothing measured it. The heartbeat
/// here is a WinForms timer whose ticks are WM_TIMER messages: they are
/// delivered only when the thread is pumping, exactly like the app's own
/// input. If anything blocks this thread, the beats stop, and a watcher on
/// another thread measures the gap. There is no way to fake a beat while
/// blocked and no way to miss a block longer than the sampling interval.
/// </para>
/// <para>
/// Work is placed onto the pumped thread the same way the application places
/// connect work onto its UI thread, and the time it holds the thread is
/// measured directly. Phases the app runs on a worker (Start(), per
/// globals.vb's RunConnectPhaseOffUiThread) run on a worker here too, while
/// the heartbeat proves the pumped thread stayed alive underneath them.
/// </para>
/// <para>
/// The instrument proves itself before it is trusted: the harness posts a
/// deliberate block and requires the watcher to see it (a negative result
/// needs a positive control). A heartbeat that cannot see a planted 400 ms
/// block has no business acquitting a connect.
/// </para>
/// </remarks>
internal sealed class PumpedDesk : IDisposable
{
    public const int HeartbeatIntervalMs = 50;

    private Thread? _pumpThread;
    private Thread? _watcherThread;
    private SynchronizationContext? _sync;
    private System.Windows.Forms.Timer? _timer;

    private long _lastBeat;
    private long _beatCount;
    private volatile bool _stopWatcher;

    private readonly object _windowLock = new();
    private bool _windowOpen;
    private long _windowMaxGapMs;
    private string _windowName = "";

    /// <summary>Start the pump and heartbeat; returns once both are proven alive.</summary>
    /// <returns>Null on success, otherwise a sentence saying what failed.</returns>
    public string? Start()
    {
        var ready = new ManualResetEventSlim(false);

        _pumpThread = new Thread(() =>
        {
            // The same pump arrangement as the application's UI thread.
            SynchronizationContext.SetSynchronizationContext(
                new WindowsFormsSynchronizationContext());
            _sync = SynchronizationContext.Current;

            _timer = new System.Windows.Forms.Timer { Interval = HeartbeatIntervalMs };
            _timer.Tick += (s, e) =>
            {
                Volatile.Write(ref _lastBeat, Environment.TickCount64);
                Interlocked.Increment(ref _beatCount);
            };
            _timer.Start();

            ready.Set();
            Application.Run();   // pumps, windowless, until ExitThread
        });
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.IsBackground = true;
        _pumpThread.Name = "Harness:PumpedDesk";
        _pumpThread.Start();

        if (!ready.Wait(5000))
            return "the pumped thread did not start within 5 seconds";

        // Prove the heart is beating before anything trusts it.
        long deadline = Environment.TickCount64 + 3000;
        while (Interlocked.Read(ref _beatCount) < 3)
        {
            if (Environment.TickCount64 >= deadline)
                return "the heartbeat never ticked - the message pump is not delivering timer messages";
            Thread.Sleep(20);
        }

        _watcherThread = new Thread(WatchProc)
        {
            IsBackground = true,
            Name = "Harness:HeartbeatWatcher",
        };
        _watcherThread.Start();
        return null;
    }

    private void WatchProc()
    {
        while (!_stopWatcher)
        {
            long beat = Volatile.Read(ref _lastBeat);
            long gap = Environment.TickCount64 - beat;
            lock (_windowLock)
            {
                if (_windowOpen && gap > _windowMaxGapMs) _windowMaxGapMs = gap;
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Begin measuring the worst heartbeat gap. One window at a time.
    /// </summary>
    public void BeginWindow(string name)
    {
        lock (_windowLock)
        {
            if (_windowOpen)
                throw new InvalidOperationException(
                    "measurement window '" + _windowName + "' is still open");
            _windowOpen = true;
            _windowMaxGapMs = 0;
            _windowName = name;
        }
    }

    /// <summary>End the window and report the worst gap seen, in ms.</summary>
    public long EndWindow()
    {
        lock (_windowLock)
        {
            _windowOpen = false;
            return _windowMaxGapMs;
        }
    }

    /// <summary>The outcome of one piece of work placed on the pumped thread.</summary>
    public sealed class DeskCall
    {
        /// <summary>The work ran to completion (well or badly) within the ceiling.</summary>
        public bool Completed;
        /// <summary>It at least STARTED - false means the pump was already wedged.</summary>
        public bool Started;
        /// <summary>How long it held the pumped thread, ms. -1 if it never started,
        /// and a live lower bound if it is still holding the thread now.</summary>
        public long HeldMs = -1;
        /// <summary>The exception it threw, if any. Recorded, never rethrown here.</summary>
        public Exception? Error;
    }

    /// <summary>
    /// Run work ON the pumped thread - the way the application runs Connect()
    /// on its UI thread - and measure how long it held the pump hostage.
    /// Waits up to <paramref name="ceilingMs"/> for it to finish, in short
    /// slices so <paramref name="bailEarly"/> (the operator's Ctrl+C) is
    /// honored promptly; the work itself is never aborted, so on a ceiling
    /// breach the caller learns the thread is still held (Completed false,
    /// HeldMs a live lower bound).
    /// </summary>
    public DeskCall RunOnDesk(string name, Action work, int ceilingMs,
                              Func<bool>? bailEarly = null)
    {
        var call = new DeskCall();
        if (_sync == null) return call;

        var started = new ManualResetEventSlim(false);
        var finished = new ManualResetEventSlim(false);
        long t0 = 0;

        _sync.Post(_ =>
        {
            t0 = Environment.TickCount64;
            call.Started = true;
            started.Set();
            try { work(); }
            catch (Exception ex) { call.Error = ex; }
            finally
            {
                call.HeldMs = Environment.TickCount64 - t0;
                call.Completed = true;
                finished.Set();
            }
        }, null);

        long waitDeadline = Environment.TickCount64 + ceilingMs;
        while (!finished.Wait(100))
        {
            if (Environment.TickCount64 >= waitDeadline) break;
            if (bailEarly != null && bailEarly()) break;
        }
        if (!finished.IsSet && started.IsSet)
            call.HeldMs = Environment.TickCount64 - t0;
        return call;
    }

    /// <summary>
    /// The positive control: plant a block of a known size on the pumped
    /// thread and require the watcher to see it. Returns null when the
    /// instrument proved itself, otherwise what went wrong.
    /// </summary>
    public string? SelfCheck(int plantedBlockMs = 400)
    {
        BeginWindow("instrument self-check");
        var call = RunOnDesk("planted block", () => Thread.Sleep(plantedBlockMs), plantedBlockMs + 5000);
        long seen = EndWindow();

        if (!call.Completed)
            return "the planted block never completed";
        if (seen < plantedBlockMs - 100)
            return "a planted " + plantedBlockMs + " ms block was measured as only "
                 + seen + " ms - the watcher is not seeing real gaps";
        return null;
    }

    /// <summary>Stop the pump. Safe to call more than once.</summary>
    public void Dispose()
    {
        _stopWatcher = true;
        var sync = _sync;
        if (sync != null)
        {
            _sync = null;
            sync.Post(_ =>
            {
                try { _timer?.Stop(); _timer?.Dispose(); } catch { }
                Application.ExitThread();
            }, null);
            _pumpThread?.Join(5000);
        }
        _watcherThread?.Join(1000);
    }
}
