using System.Threading;
using Xunit;

namespace Radios.Tests
{
    /// <summary>The instrument first (Sprint 49 Track RT): the watch sees a queue made on purpose from its own thread, and not one made from another thread.</summary>
    public class PoolQueueWatchTests
    {
        [Fact]
        public void The_watch_counts_this_thread_s_pool_work_and_not_another_thread_s()
        {
            using var watch = new PoolQueueWatch();
            Assert.True(watch.Probe(), "the runtime announced no pool enqueue, so the watch can prove nothing");
            watch.Reset();
            var other = new Thread(() =>
            {
                using var done = new ManualResetEventSlim(false);
                ThreadPool.UnsafeQueueUserWorkItem(_ => done.Set(), null);
                done.Wait(5000);
            });
            other.Start();
            other.Join();
            Thread.Sleep(50);
            Assert.Equal(0, watch.Count);
            Assert.True(watch.Probe());
            Assert.Equal(1, watch.Count);
        }
    }
}
