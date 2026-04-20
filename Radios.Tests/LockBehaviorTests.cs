#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Regression guards for the R1 decision (Sprint 26 Phase 0.1): the
    /// <see cref="System.Threading.Lock"/> primitive must serialize concurrent
    /// callers AND be reentrant on the same thread. If either property
    /// changed, WanServerAdapter's lock strategy would need revisiting.
    /// </summary>
    public class LockBehaviorTests
    {
        [Fact]
        public void Lock_SerializesConcurrentCalls()
        {
            var gate = new System.Threading.Lock();
            int insideCount = 0;
            int maxObservedInside = 0;
            var sync = new object();

            void Hold(int ms)
            {
                lock (gate)
                {
                    int n = Interlocked.Increment(ref insideCount);
                    lock (sync)
                    {
                        if (n > maxObservedInside) maxObservedInside = n;
                    }
                    Thread.Sleep(ms);
                    Interlocked.Decrement(ref insideCount);
                }
            }

            var t1 = Task.Run(() => Hold(50));
            var t2 = Task.Run(() => Hold(50));
            var t3 = Task.Run(() => Hold(50));

            Task.WaitAll(t1, t2, t3);

            Assert.Equal(1, maxObservedInside);
        }

        [Fact]
        public void Lock_IsReentrantOnSameThread()
        {
            var gate = new System.Threading.Lock();
            bool entered = false;

            lock (gate)
            {
                // Re-enter from the same thread. With a non-reentrant primitive
                // (e.g., SemaphoreSlim) this line would block forever and the
                // test would time out. With lock (Monitor / System.Threading.Lock)
                // it proceeds.
                lock (gate)
                {
                    entered = true;
                }
            }

            Assert.True(entered);
        }
    }
}
