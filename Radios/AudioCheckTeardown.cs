using System;
using System.Threading.Tasks;

namespace Radios
{
    /// <summary>How an Audio Check's teardown before a disconnect ended.</summary>
    /// <param name="Completed">Every change was put back and the command queue ran past it, inside the bound.</param>
    /// <param name="Stage">
    /// Why not, or empty: <c>still-transmitting</c> (restores withheld because
    /// the radio kept reporting transmit; correct, and not a restore),
    /// <c>restore</c> (a restore did not finish in time), <c>faulted</c> (a
    /// restore failed), <c>queue</c> (the command queue did not run past them),
    /// <c>failed</c> (the teardown itself threw).
    /// </param>
    public sealed record AudioCheckTeardownResult(bool Completed, string Stage);

    /// <summary>
    /// The wait a teardown makes before the radio goes (#835, fix rounds 2 and
    /// 3): every change any check made put back, then the command queue drained
    /// past them, under one bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Round 2 waited on the window's session and its own guess at whether
    /// there was work; round 3 waits on the radio's
    /// <see cref="CheckRestorations"/>, which holds every change whether or not
    /// its session or window still exists. The receive rule lives there: a
    /// restore runs only while the radio reports receive.
    /// </para>
    /// <para>
    /// <b>A failure is never a completion.</b> A restore that threw, or a
    /// completion task that faulted or was cancelled, makes the result
    /// unconfirmed.
    /// </para>
    /// </remarks>
    public static class AudioCheckTeardown
    {
        public static async Task<AudioCheckTeardownResult> RunAsync(
            Func<Task<bool>> settled, Func<bool> withheld,
            Func<TimeSpan, Task<bool>> drain, TimeSpan bound)
        {
            var deadline = DateTime.UtcNow + bound;
            TimeSpan Left() => deadline - DateTime.UtcNow;

            Task<bool> done;
            try { done = settled() ?? Task.FromResult(true); }
            catch (Exception) { return new(false, "faulted"); }
            if (!done.IsCompleted)
            {
                var left = Left();
                if (left > TimeSpan.Zero) await Task.WhenAny(done, Task.Delay(left)).ConfigureAwait(false);
                if (!done.IsCompleted) return new(false, withheld() ? "still-transmitting" : "restore");
            }
            if (!done.IsCompletedSuccessfully || !done.Result) return new(false, "faulted");

            var remaining = Left();
            bool drained;
            try { drained = remaining > TimeSpan.Zero && await drain(remaining).ConfigureAwait(false); }
            catch (Exception) { drained = false; }
            return drained ? new(true, "") : new(false, "queue");
        }
    }
}
