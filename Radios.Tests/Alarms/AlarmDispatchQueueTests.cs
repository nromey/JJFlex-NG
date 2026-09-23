using System;
using System.Collections.Generic;
using System.Threading;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>The hand-off between the meter thread and everything slow: ordered, bounded, and loud when full.</summary>
    public sealed class AlarmDispatchQueueTests
    {
        [Fact]
        public void Items_are_handled_in_order_off_the_posting_thread()
        {
            var seen = new List<int>();
            int postingThread = Environment.CurrentManagedThreadId;
            int? handlerThread = null;
            using var q = new AlarmDispatchQueue<int>(i =>
            {
                handlerThread ??= Environment.CurrentManagedThreadId;
                lock (seen) seen.Add(i);
            }, "test");

            for (int i = 0; i < 20; i++) Assert.True(q.Post(i));
            Assert.True(q.Drain(2000));

            lock (seen) Assert.Equal(new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 }, seen);
            Assert.NotEqual(postingThread, handlerThread);
        }

        [Fact]
        public void A_full_queue_refuses_and_announces_once_rather_than_growing_or_dropping_silently()
        {
            var release = new ManualResetEventSlim(false);
            var started = new ManualResetEventSlim(false);
            long announced = -1;
            int announcements = 0;
            using var q = new AlarmDispatchQueue<int>(_ => { started.Set(); release.Wait(); }, "test", capacity: 4);
            q.Overflowed += n => { announcements++; announced = n; };

            // The worker takes the first item and blocks on it; four more fill the queue.
            Assert.True(q.Post(0));
            Assert.True(started.Wait(2000));
            for (int i = 1; i < 5; i++) Assert.True(q.Post(i));
            Assert.False(q.Post(99));
            Assert.False(q.Post(100));
            Assert.Equal(2, q.Dropped);
            Assert.Equal(1, announcements);
            Assert.Equal(1, announced);

            release.Set();
            Assert.True(q.Drain(2000));
        }

        [Fact]
        public void A_handler_that_throws_is_skipped_and_the_worker_carries_on()
        {
            var handled = new List<int>();
            using var q = new AlarmDispatchQueue<int>(i =>
            {
                if (i == 1) throw new InvalidOperationException("boom");
                lock (handled) handled.Add(i);
            }, "test");
            q.Post(0); q.Post(1); q.Post(2);
            Assert.True(q.Drain(2000));
            lock (handled) Assert.Equal(new List<int> { 0, 2 }, handled);
        }

        [Fact]
        public void The_high_water_mark_reports_the_deepest_the_queue_ever_got()
        {
            var release = new ManualResetEventSlim(false);
            using var q = new AlarmDispatchQueue<int>(_ => release.Wait(), "test");
            for (int i = 0; i < 6; i++) q.Post(i);
            Thread.Sleep(50);
            Assert.True(q.HighWaterMark >= 5);
            release.Set();
            q.Drain(2000);
        }
    }
}
