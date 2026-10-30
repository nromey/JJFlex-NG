using System;
using System.Threading.Tasks;

namespace Radios;

public partial class FlexBase
{
    /// <summary>
    /// Wait until every radio write queued before this call has run, or until
    /// <paramref name="bound"/> passes. True when the queue reached this point;
    /// false when the command loop is not running or the bound ran out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why (#835, fix round 2).</b> A clean disconnect stops the command loop
    /// with no final drain, so writes queued just before it, an unkey or a
    /// check putting the operator's power back, can be dropped. Check your
    /// audio waits on this before the shell disposes the rig. The queue is
    /// first in, first out, so a no-op item running means everything ahead of
    /// it has run.
    /// </para>
    /// <para>
    /// Never throws, never blocks the caller's thread, and never cancels the
    /// item: if the bound runs out, the marker still runs later and completes
    /// nothing anyone is waiting on.
    /// </para>
    /// </remarks>
    public async Task<bool> DrainCommandQueueAsync(TimeSpan bound)
    {
        var queue = q;
        if (queue == null || !queue.MainLoop) return false;
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { queue.Enqueue((FunctionDel)(() => reached.TrySetResult(true)), "DrainCommandQueue"); }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("DrainCommandQueueAsync: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            return false;
        }
        var finished = await Task.WhenAny(reached.Task, Task.Delay(bound)).ConfigureAwait(false);
        return ReferenceEquals(finished, reached.Task);
    }
}
