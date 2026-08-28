using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace JJTrace
{
    public static partial class Tracing
    {
        public delegate bool awaitExp();
        /// <summary>
        /// Await the specified condition.
        /// </summary>
        /// <param name="exp">function that returns the condition</param>
        /// <param name="ms">milliseconds to wait.</param>
        /// <param name="interval">optional interval to check</param>
        /// <returns>true if condition met.</returns>
        /// <remarks>
        /// <para><b><paramref name="ms"/> is a DEADLINE, not a count of sleeps
        /// (task #293).</b> This was <c>sanity = ms / interval</c> and a
        /// <c>while (sanity-- &gt; 0)</c> loop — which runs the right NUMBER of
        /// turns, while each turn costs the sleep PLUS however long
        /// <paramref name="exp"/> takes. So "wait 20 seconds" was 20 seconds of
        /// sleeping plus 800 evaluations of the caller's condition, and the
        /// overshoot grew with anything that slowed a turn down. Four copies of
        /// this loop existed; the station-name wait's version was measured at
        /// 55.7 s against a declared 45 s.</para>
        /// <para>The condition is evaluated at least once, however small
        /// <paramref name="ms"/> is. Under the old arithmetic
        /// <c>await(exp, 10, 25)</c> returned false without ever asking.</para>
        /// </remarks>
        public static bool await(awaitExp exp, int ms, int interval)
        {
            long deadline = Environment.TickCount64 + ms;
            bool rv;
            while (true)
            {
                rv = exp();
                if (rv) break;
                if (Environment.TickCount64 >= deadline) break;
                Thread.Sleep(interval);
            }
            return rv;
        }
        public static bool await(awaitExp exp, int ms)
        {
            return await(exp, ms, 25);
        }
    }
}
