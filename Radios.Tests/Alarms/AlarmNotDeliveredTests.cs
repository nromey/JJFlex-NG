using System;
using System.IO;
using System.Linq;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// A warning that was never heard is not a repeat: when delivery fails —
    /// the dispatch queue refuses it, or the speech layer gives it up — the
    /// alarm's own next fresh sample says it again, inside the interval
    /// (Astra's Track I review, findings 4 and 5).
    /// </summary>
    public sealed class AlarmNotDeliveredTests : IDisposable
    {
        private const string Serial = "1234-5678-9012-3456";
        private static readonly MeterDescriptor Pa = AlarmMonitorTests.PaTemp;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-alarmnd-" + Guid.NewGuid().ToString("N"));
        private AlarmService? _service;

        public void Dispose()
        {
            _service?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ── the monitor ──

        [Fact]
        public void TheMonitor_SaysTheWarningAgainOnTheNextFreshSample_WhenToldItWasNotDelivered()
        {
            var m = new AlarmMonitor(AlarmMonitorTests.Level(reminder: 30));
            var feed = new AlarmMonitorTests.Feed();
            var fired = m.Observe(feed.At(1000, 61f), 1000);
            Assert.Single(fired, e => e.Kind == AlarmEventKind.Fired);

            m.WarningNotDelivered(1500);

            var again = m.Observe(feed.At(3000, 61f), 3000).ToList();
            var reminder = Assert.Single(again, e => e.Kind == AlarmEventKind.Reminder);
            Assert.Equal(AlarmReminderReason.DeliveryRetried, reminder.ReminderReason);
            Assert.Equal(61f, reminder.Value);

            // Once. The sample after that is inside the interval and says nothing.
            Assert.DoesNotContain(m.Observe(feed.At(5000, 61f), 5000), e => e.IsWarning);
        }

        [Fact]
        public void PositiveControl_WithoutBeingTold_TheNextSampleInsideTheIntervalSaysNothing()
        {
            var m = new AlarmMonitor(AlarmMonitorTests.Level(reminder: 30));
            var feed = new AlarmMonitorTests.Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            Assert.DoesNotContain(m.Observe(feed.At(3000, 61f), 3000), e => e.IsWarning);
        }

        [Fact]
        public void TheMonitor_IgnoresTheCallWhenNoEpisodeIsActive()
        {
            var m = new AlarmMonitor(AlarmMonitorTests.Level());
            var feed = new AlarmMonitorTests.Feed();
            m.Observe(feed.At(1000, 30f), 1000);
            m.WarningNotDelivered(1500);
            Assert.DoesNotContain(m.Observe(feed.At(3000, 30f), 3000), e => e.IsWarning);
        }

        [Fact]
        public void ARetriedWarning_BypassesAcknowledgement_BecauseNobodyHeardTheFirst()
        {
            var m = new AlarmMonitor(AlarmMonitorTests.Level(reminder: 30));
            var feed = new AlarmMonitorTests.Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.Acknowledge(1200);
            m.WarningNotDelivered(1500);
            var again = m.Observe(feed.At(3000, 61f), 3000).ToList();
            Assert.Single(again, e => e.Kind == AlarmEventKind.Reminder && e.ReminderReason == AlarmReminderReason.DeliveryRetried);
        }

        // ── the service, with a dispatch queue that is actually full ──

        [Fact]
        public void AWarningRefusedByAFullDispatchQueue_IsSaidAgainOnTheNextFreshReading()
        {
            var feed = new FakeAlarmFeed();
            var clock = new ManualAlarmClock { NowMs = 10_000 };
            var gate = new ManualResetEventSlim(false);
            var dispatched = new System.Collections.Generic.List<AlarmEvent>();

            // Capacity ONE: the worker takes the first event and blocks in the
            // handler, the second waits in the one slot, the third is refused.
            _service = new AlarmService(feed, new AlarmDefinitionStore(_root), clock, null, startWatchdog: false,
                dispatchCapacity: 1);
            _service.EventDispatched += e =>
            {
                lock (dispatched) dispatched.Add(e);
                if (e.Kind == AlarmEventKind.FirstSample) gate.Wait(5000);
            };
            feed.Connect(Serial, Pa);
            Assert.True(_service.Add(AlarmMonitorTests.Level(reminder: 30) with { Id = "pa", RadioSerial = Serial }));

            clock.Advance(1000); feed.Deliver(Pa, 30f);     // FirstSample: taken, handler blocks
            Thread.Sleep(100);
            clock.Advance(2000); feed.Deliver(Pa, 61f);     // Fired: fills the one slot
            clock.Advance(2000); feed.Deliver(Pa, 63.5f);   // Worsened: REFUSED — the warning nobody heard
            Assert.True(_service.DispatchDropped >= 1);

            gate.Set();
            Assert.True(_service.DrainDispatch(5000));

            // The next fresh reading says the warning again, inside the
            // thirty-second interval, because the monitor was told.
            clock.Advance(2000); feed.Deliver(Pa, 63.5f);
            Assert.True(_service.DrainDispatch(5000));
            AlarmEvent retried;
            lock (dispatched)
                retried = Assert.Single(dispatched, e => e.Kind == AlarmEventKind.Reminder
                                                      && e.ReminderReason == AlarmReminderReason.DeliveryRetried);
            Assert.Equal(63.5f, retried.Value);
        }

        [Fact]
        public void TheServiceForwardsASpeechLayerNotDeliveredToTheMonitor()
        {
            var feed = new FakeAlarmFeed();
            var clock = new ManualAlarmClock { NowMs = 10_000 };
            var dispatched = new System.Collections.Generic.List<AlarmEvent>();
            _service = new AlarmService(feed, new AlarmDefinitionStore(_root), clock, null, startWatchdog: false);
            _service.EventDispatched += e => { lock (dispatched) dispatched.Add(e); };
            feed.Connect(Serial, Pa);
            Assert.True(_service.Add(AlarmMonitorTests.Level(reminder: 30) with { Id = "pa", RadioSerial = Serial }));

            clock.Advance(1000); feed.Deliver(Pa, 61f);
            Assert.True(_service.DrainDispatch(2000));

            _service.WarningNotDelivered("pa");

            clock.Advance(2000); feed.Deliver(Pa, 61f);
            Assert.True(_service.DrainDispatch(2000));
            lock (dispatched)
                Assert.Single(dispatched, e => e.Kind == AlarmEventKind.Reminder && e.ReminderReason == AlarmReminderReason.DeliveryRetried);
        }
    }
}
