using System;
using System.Threading;
using JJTrace;

namespace JJFlexWpf.Tests;

/// <summary>
/// An off-audio writer a test settles on demand (Sprint 49 Track RT, repair 2).
/// <see cref="Settle"/> runs one writer pass on a thread of this writer's own
/// (never the caller's, so "raised off the audio thread" stays true) and returns
/// when it has finished: everything published before the call has then been
/// written, raised or disposed. No period, no fixed wait.
/// </summary>
/// <remarks>
/// Why: the tests that pumped buffers and then waited a fixed time (100 to 500 ms)
/// for a keep-up result or a slot event were waiting on the app's shared writer,
/// one below-normal thread on a 50 ms period. Under sustained CPU load that thread
/// is starved for seconds (measured 2.4 s at worst on this machine with every core
/// busy), so the test read before the result landed. A test that owns the pipeline
/// gives it this writer and settles after each buffer instead.
/// </remarks>
internal sealed class SettledOffAudioWriter : IOffAudioScheduler, IDisposable
{
    private readonly ManualOffAudioScheduler _manual = new();
    private readonly Thread _thread;
    private readonly AutoResetEvent _go = new(false), _done = new(false);
    private volatile bool _stopping;
    private Exception? _fault;

    public SettledOffAudioWriter()
    {
        // Above normal (repair 3): a settle is a hop between two buffers, and under a
        // full CPU a hop that waits long enough reads as a gap in the stream to the
        // keep-up rule (KeepUpGapTicks, 100 ms) and restarts its grace. The writer's
        // own work is the app's; only the hop must not be starved.
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "settled off-audio writer (test)" };
        _thread.Start();
    }

    /// <summary>The writer's own thread, where everything it writes is written.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    public void Register(OffAudioWork work) => _manual.Register(work);
    public void Unregister(OffAudioWork work) => _manual.Unregister(work);
    public void RetireAfterDrain(OffAudioWork work) => _manual.RetireAfterDrain(work);

    /// <summary>One writer pass, on the writer's thread; returns when it is over. Rethrows what a drain threw.</summary>
    public void Settle()
    {
        _go.Set();
        if (!_done.WaitOne(TimeSpan.FromSeconds(60))) throw new TimeoutException("the settled writer did not finish a pass in 60 s");
        var fault = Interlocked.Exchange(ref _fault, null);
        if (fault != null) throw new InvalidOperationException("a drain threw on the settled writer", fault);
    }

    private void Run()
    {
        while (true)
        {
            _go.WaitOne();
            if (_stopping) return;
            try { _manual.Tick(); }
            catch (Exception ex) { _fault = ex; }
            _done.Set();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _go.Set();
        _thread.Join(2000);
    }
}
