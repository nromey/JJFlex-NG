using System;
using System.Threading.Tasks;

namespace Radios
{
    /// <summary>How an Audio Check's teardown before a disconnect ended.</summary>
    /// <param name="Completed">Every stage finished inside the bound.</param>
    /// <param name="Stage">The stage the bound ran out in, or empty: <c>still-transmitting</c>, <c>session-ending</c>, <c>restore</c> or <c>queue</c>.</param>
    public sealed record AudioCheckTeardownResult(bool Completed, string Stage);

    /// <summary>
    /// The order a check is put back in before the shell tears the radio down
    /// (#835, fix round 2), with one bound over the whole of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an order, and why waiting.</b> Ending a check starts work that
    /// finishes later: the radio has to report it is receiving before power may
    /// come back (a hardware PTT line can still be holding it); the Loopback
    /// arrangement's teardown and an off-air take's restore wait on the radio's
    /// replies; and every write rides the command queue, which a disconnect
    /// stops with no final drain. Calling the teardown and then disposing the
    /// rig at once let the disconnect outrun all of it.
    /// </para>
    /// <para>
    /// <b>The stages, in order:</b> the radio receiving; the check's own ending
    /// (the Loopback teardown, the off-air finish); nothing left to restore;
    /// and the command queue drained past it all. A stage that cannot finish
    /// inside the bound is named in the result, and the caller says so rather
    /// than hanging the close.
    /// </para>
    /// </remarks>
    public static class AudioCheckTeardown
    {
        /// <summary>How often a waited-on condition is asked again.</summary>
        public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

        public static async Task<AudioCheckTeardownResult> RunAsync(
            Func<bool> transmitting, Func<Task> sessionEnding, Func<bool> restorePending,
            Func<TimeSpan, Task<bool>> drain, TimeSpan bound)
        {
            var deadline = DateTime.UtcNow + bound;
            TimeSpan Left() => deadline - DateTime.UtcNow;

            while (transmitting())
            {
                if (Left() <= TimeSpan.Zero) return new(false, "still-transmitting");
                await Task.Delay(Poll);
            }

            var ending = sessionEnding() ?? Task.CompletedTask;
            if (!ending.IsCompleted)
            {
                var left = Left();
                if (left <= TimeSpan.Zero || await Task.WhenAny(ending, Task.Delay(left)) != ending)
                    return new(false, "session-ending");
            }

            while (restorePending())
            {
                if (Left() <= TimeSpan.Zero) return new(false, "restore");
                await Task.Delay(Poll);
            }

            var remaining = Left();
            if (remaining <= TimeSpan.Zero || !await drain(remaining)) return new(false, "queue");
            return new(true, "");
        }
    }
}
