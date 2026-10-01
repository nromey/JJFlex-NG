using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>A rig a test drives by hand: readings, census, connect, transmit.</summary>
    internal sealed class FakeAlarmFeed : IAlarmMeterFeed
    {
        public event Action<MeterDescriptor, float, int>? Reading;
        public event Action? InventoryChanged;
        public event Action<string>? Connected;
        public event Action? Disconnected;
        public event Action<bool>? TransmitChanged;

        public IReadOnlyList<MeterDescriptor> Inventory { get; set; } = Array.Empty<MeterDescriptor>();
        public int InventoryEpoch { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsTransmitting { get; private set; }
        public string ConnectedSerial { get; private set; } = "";

        public void Connect(string serial, params MeterDescriptor[] inventory)
        {
            Inventory = inventory;
            InventoryEpoch++;
            IsConnected = true;
            ConnectedSerial = serial;
            Connected?.Invoke(serial);
        }

        public void Disconnect()
        {
            IsConnected = false;
            Inventory = Array.Empty<MeterDescriptor>();
            InventoryEpoch++;
            Disconnected?.Invoke();
        }

        public void Publish(params MeterDescriptor[] inventory)
        {
            Inventory = inventory;
            InventoryEpoch++;
            InventoryChanged?.Invoke();
        }

        /// <summary>A reading from the CURRENT census, as the production feed delivers one.</summary>
        public void Deliver(MeterDescriptor meter, float value) => Reading?.Invoke(meter, value, InventoryEpoch);

        /// <summary>A reading stamped with an older census — the late callback of finding 3.</summary>
        public void DeliverFromEpoch(MeterDescriptor meter, float value, int epoch) => Reading?.Invoke(meter, value, epoch);

        public void Key(bool tx)
        {
            IsTransmitting = tx;
            TransmitChanged?.Invoke(tx);
        }
    }

    internal sealed class RecordingRecorder : IAlarmObservationRecorder
    {
        public readonly List<MeterObservation> Observations = new List<MeterObservation>();
        public readonly List<AlarmEvent> Events = new List<AlarmEvent>();
        public IReadOnlyList<MeterDescriptor> Recorded = Array.Empty<MeterDescriptor>();
        public void Record(in MeterObservation observation) { lock (Observations) Observations.Add(observation); }
        public void RecordEvent(AlarmEvent alarmEvent) { lock (Events) Events.Add(alarmEvent); }
        public void RecordedSetChanged(int connectionGeneration, IReadOnlyList<MeterDescriptor> recorded) => Recorded = recorded;
        public void ConnectionStarted(int connectionGeneration, string radioSerial) { }
        public void ConnectionEnded(int connectionGeneration, string why) { }
    }

    /// <summary>The live attachment over a fake rig: binding, generations, the recorded set, dispatch off-thread.</summary>
    public sealed class AlarmServiceTests : IDisposable
    {
        private const string Serial = "1234-5678-9012-3456";
        private static readonly MeterDescriptor Pa = new MeterDescriptor(11, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120);
        private static readonly MeterDescriptor PaMoved = new MeterDescriptor(9, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120);
        private static readonly MeterDescriptor Fwd = new MeterDescriptor(1, "FWDPWR", "RF Power Forward", "TX-", 4, MeterUnits.Dbm, -30, 50);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-alarmsvc-" + Guid.NewGuid().ToString("N"));
        private readonly FakeAlarmFeed _feed = new FakeAlarmFeed();
        private readonly ManualAlarmClock _clock = new ManualAlarmClock { NowMs = 10_000 };
        private readonly RecordingRecorder _recorder = new RecordingRecorder();
        private readonly List<AlarmEvent> _dispatched = new List<AlarmEvent>();
        private readonly List<int> _dispatchThreads = new List<int>();
        private AlarmService? _service;

        public void Dispose()
        {
            _service?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private AlarmService Service()
        {
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, _recorder, startWatchdog: false);
            _service.EventDispatched += e =>
            {
                lock (_dispatched) { _dispatched.Add(e); _dispatchThreads.Add(Environment.CurrentManagedThreadId); }
            };
            return _service;
        }

        private static AlarmDefinition PaLevel(double threshold = 60) =>
            AlarmDefinition.NewLevel("pa", "PA temperature high", Serial, MeterSelector.From(Pa),
                AlarmDirection.AtOrAbove, threshold, 2) with { Enabled = true };

        private List<AlarmEvent> Dispatched()
        {
            Assert.True(_service!.DrainDispatch(2000));
            lock (_dispatched) return _dispatched.ToList();
        }

        private void Step(long ms) => _clock.Advance(ms);

        [Fact]
        public void A_reading_at_the_line_fires_and_is_delivered_off_the_meter_thread()
        {
            var s = Service();
            _feed.Connect(Serial, Pa, Fwd);
            Assert.True(s.Add(PaLevel()));

            Step(1000);
            _feed.Deliver(Pa, 30f);
            Step(2000);
            _feed.Deliver(Pa, 61f);

            var events = Dispatched();
            Assert.Single(events, e => e.Kind == AlarmEventKind.Fired);
            Assert.All(_dispatchThreads, t => Assert.NotEqual(Environment.CurrentManagedThreadId, t));
            Assert.Equal(AlarmConditionState.Active, s.SnapshotOf("pa")!.Condition);
            Assert.Equal(MeterSelectorStatus.Resolved, s.SnapshotOf("pa")!.Resolution);
        }

        [Fact]
        public void Definitions_persist_and_come_back_on_the_next_connection_with_a_new_generation()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            Step(1000);
            _feed.Deliver(Pa, 61f);
            Assert.Equal(1, s.ConnectionGeneration);

            _feed.Disconnect();
            Assert.Equal(AlarmDataState.Missing, s.SnapshotOf("pa")!.Data);
            Assert.Equal(AlarmConditionState.LastKnownActive, s.SnapshotOf("pa")!.Condition);

            _feed.Connect(Serial, PaMoved);   // the index moved from 11 to 9
            Assert.Equal(2, s.ConnectionGeneration);
            Assert.True(s.SnapshotOf("pa")!.Definition.Enabled);
            Assert.Equal(9, s.SnapshotOf("pa")!.ResolvedMeter!.Index);

            Step(1000);
            _feed.Deliver(PaMoved, 61f);
            var events = Dispatched();
            Assert.Contains(events, e => e.Kind == AlarmEventKind.Reminder && e.ReminderReason == AlarmReminderReason.DataResumed);
        }

        [Fact]
        public void An_inventory_that_grows_late_binds_when_the_meter_appears()
        {
            var s = Service();
            _feed.Connect(Serial, Fwd);
            s.Add(PaLevel());
            Assert.Equal(MeterSelectorStatus.Missing, s.SnapshotOf("pa")!.Resolution);

            Step(500);
            _feed.Deliver(Pa, 70f);   // a reading for a meter not yet in the census: ignored, never judged
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Fired);

            _feed.Publish(Fwd, Pa);
            Assert.Equal(MeterSelectorStatus.Resolved, s.SnapshotOf("pa")!.Resolution);
            Step(500);
            _feed.Deliver(Pa, 70f);
            Assert.Contains(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
        }

        [Fact]
        public void Duplicate_descriptors_leave_the_alarm_ambiguous_and_nothing_fires_from_either_copy()
        {
            var s = Service();
            var copy = Pa with { Index = 40 };
            _feed.Connect(Serial, Pa, copy);
            s.Add(PaLevel());
            Assert.Equal(MeterSelectorStatus.Ambiguous, s.SnapshotOf("pa")!.Resolution);
            Assert.Equal(AlarmDataState.Ambiguous, s.SnapshotOf("pa")!.Data);

            Step(500);
            _feed.Deliver(Pa, 70f);
            _feed.Deliver(copy, 70f);
            var events = Dispatched();
            Assert.DoesNotContain(events, e => e.Kind == AlarmEventKind.Fired);
            Assert.Contains(events, e => e.Kind == AlarmEventKind.DataAmbiguous);
        }

        [Fact]
        public void A_meter_with_the_wrong_units_is_unavailable_not_judged()
        {
            var s = Service();
            _feed.Connect(Serial, Pa with { Units = MeterUnits.DegreesF });
            s.Add(PaLevel());
            Assert.Equal(MeterSelectorStatus.UnitChanged, s.SnapshotOf("pa")!.Resolution);
            Step(500);
            _feed.Deliver(Pa with { Units = MeterUnits.DegreesF }, 150f);
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
        }

        [Fact]
        public void Readings_after_disconnect_are_ignored_entirely()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            _feed.Disconnect();
            long before = s.ObservationCount;
            _feed.Deliver(Pa, 70f);   // a residual callback racing teardown
            Assert.Equal(before, s.ObservationCount);
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
        }

        [Fact]
        public void The_recorded_set_is_the_alarm_meters_plus_the_operator_extras_and_nothing_else()
        {
            var s = Service();
            var swr = new MeterDescriptor(5, "SWR", "SWR", "TX-", 4, MeterUnits.SWR, 1, 999);
            _feed.Connect(Serial, Pa, Fwd, swr);
            s.Add(PaLevel());
            Assert.True(s.SetRecordOnly(MeterSelector.From(Fwd), true));

            Assert.Equal(new[] { "PATEMP", "FWDPWR" }, s.RecordedMeters.Select(m => m.Name));
            // Resolved in the census's own order; SWR is not in it.
            Assert.Equal(new[] { "PATEMP", "FWDPWR" }, s.RecordedMetersResolved.Select(m => m.Name));

            Step(500);
            _feed.Deliver(Pa, 30f);
            _feed.Deliver(Fwd, 40f);
            _feed.Deliver(swr, 1.1f);
            lock (_recorder.Observations)
                Assert.Equal(new[] { "PATEMP", "FWDPWR" }, _recorder.Observations.Select(o => o.Meter.Name));

            // Persisted with the definitions, and back on reconnect.
            _feed.Disconnect();
            _feed.Connect(Serial, Pa, Fwd, swr);
            Assert.Contains(s.RecordOnlyMeters, m => m.Name == "FWDPWR");
        }

        [Fact]
        public void The_watchdog_reports_staleness_when_no_callback_arrives()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            Step(500);
            _feed.Deliver(Pa, 30f);
            for (int i = 0; i < 24; i++) { Step(250); s.Tick(_clock.NowMs); }
            Assert.Contains(Dispatched(), e => e.Kind == AlarmEventKind.DataStale);
            Assert.Equal(AlarmDataState.Stale, s.SnapshotOf("pa")!.Data);
        }

        [Fact]
        public void Transmit_state_reaches_every_monitor()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel() with { Scope = AlarmScope.TransmitOnly });
            Step(500);
            _feed.Deliver(Pa, 70f);
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
            _feed.Key(true);
            Step(500);
            _feed.Deliver(Pa, 70f);
            Assert.Contains(Dispatched(), e => e.Kind == AlarmEventKind.Fired && e.Transmitting);
        }

        [Fact]
        public void An_unreadable_store_refuses_to_add_rather_than_overwrite()
        {
            var store = new AlarmDefinitionStore(_root);
            string path = store.PathFor(Serial);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");

            var s = Service();
            _feed.Connect(Serial, Pa);
            Assert.Equal(AlarmStoreState.Unavailable, s.StoreState);
            Assert.False(s.Add(PaLevel()));
            Assert.Equal("{ not json", File.ReadAllText(path));
        }

        [Fact]
        public void A_late_reading_from_an_older_census_is_discarded_even_when_its_index_is_current()
        {
            // Astra's Track I review, finding 3: the service routed by numeric
            // index alone and stamped the connection generation after taking
            // its lock, so a stale callback with a reused index became a fresh
            // observation. The epoch travels with the reading now.
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            int oldEpoch = _feed.InventoryEpoch;
            _feed.Publish(Pa, Fwd);   // a rebuild: new epoch, same PATEMP at the same index

            Step(500);
            _feed.DeliverFromEpoch(Pa, 70f, oldEpoch);   // the late one
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
            Assert.Equal(1, s.RejectedStaleReadings);
            Assert.Equal(AlarmDataState.Waiting, s.SnapshotOf("pa")!.Data);   // not even counted as a sample

            // Positive control: the same value from the current census fires.
            _feed.Deliver(Pa, 70f);
            Assert.Contains(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
        }

        [Fact]
        public void A_reading_whose_descriptor_is_not_the_one_bound_at_that_index_is_discarded()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            Step(500);
            // Same index, same epoch, a different meter standing where PATEMP was bound.
            _feed.Deliver(Pa with { Name = "FWDPWR", Description = "RF Power Forward", Units = MeterUnits.Dbm }, 70f);
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
            Assert.Equal(1, s.RejectedStaleReadings);
        }

        [Fact]
        public void A_meter_replaced_at_rebind_loses_its_baseline_and_resumes_afresh_rather_than_continuing()
        {
            // The same selector resolves before and after, but to a different
            // descriptor: a baseline captured on the old object must not be
            // continued onto the new one as if nothing happened.
            var s = Service();
            _feed.Connect(Serial, Pa);
            var rise = AlarmDefinition.NewRiseFromBaseline("rise", "PA rise", Serial, MeterSelector.From(Pa), 5, 1) with { Enabled = true };
            Assert.True(s.Add(rise));
            Step(500);
            _feed.Deliver(Pa, 25f);
            Assert.Contains(s.CaptureBaseline("rise"), e => e.Kind == AlarmEventKind.BaselineCaptured);
            Assert.Equal(AlarmBaselineState.Captured, s.SnapshotOf("rise")!.Baseline);

            // Firmware moves PATEMP to another index with another range: same identity, different descriptor.
            _feed.Publish(PaMoved with { High = 150 });
            var events = Dispatched();
            Assert.Contains(events, e => e.Kind == AlarmEventKind.DataMissing && e.Detail.StartsWith("meter replaced"));
            Assert.Contains(events, e => e.Kind == AlarmEventKind.BaselineInvalidated);
            Assert.Equal(AlarmBaselineState.NotCaptured, s.SnapshotOf("rise")!.Baseline);

            // And the first reading on the new binding is a resumption, not a continuation.
            Step(500);
            _feed.Deliver(PaMoved with { High = 150 }, 31f);
            Assert.Contains(Dispatched(), e => e.Kind == AlarmEventKind.DataResumed);
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Fired);
        }

        [Fact]
        public void A_save_that_fails_leaves_the_running_episode_exactly_as_it_was()
        {
            // Astra's Track I review, finding 4's tail: Update and SetEnabled
            // mutated the monitor BEFORE persisting and rolled back the
            // definition on failure — but restoring a definition is not
            // restoring the episode, the acknowledgement and the baseline the
            // mutation had closed. The write now comes first, outside the
            // service lock, and a failure changes nothing that is running.
            var s = Service();
            _feed.Connect(Serial, Pa);
            Assert.True(s.Add(PaLevel()));
            Step(500);
            _feed.Deliver(Pa, 61f);
            Assert.Contains(s.Acknowledge("pa"), e => e.Kind == AlarmEventKind.Acknowledged);
            AlarmSnapshot before = s.SnapshotOf("pa")!;
            Assert.Equal(AlarmConditionState.Active, before.Condition);
            Assert.Equal(AlarmNotificationState.Acknowledged, before.Notification);
            Assert.True(s.DrainDispatch(2000));
            int eventsBefore; lock (_dispatched) eventsBefore = _dispatched.Count;

            // Make the next write fail: the file's path is now a directory, so
            // the atomic move cannot replace it.
            string path = new AlarmDefinitionStore(_root).PathFor(Serial);
            File.Delete(path);
            Directory.CreateDirectory(path);

            Assert.False(s.Update(PaLevel(70)));
            Assert.False(s.SetEnabled("pa", false));
            Assert.False(s.Remove("pa"));
            Assert.False(s.SetRecordOnly(MeterSelector.From(Fwd), true));

            AlarmSnapshot after = s.SnapshotOf("pa")!;
            Assert.Equal(before.Definition, after.Definition);
            Assert.Equal(before.EpisodeId, after.EpisodeId);
            Assert.Equal(AlarmConditionState.Active, after.Condition);
            Assert.Equal(AlarmNotificationState.Acknowledged, after.Notification);
            Assert.True(after.Definition.Enabled);
            Assert.Empty(s.RecordOnlyMeters);
            Assert.True(s.DrainDispatch(2000));
            lock (_dispatched) Assert.Equal(eventsBefore, _dispatched.Count);   // no ConfigurationChanged, no Disabled, nothing
        }

        [Fact]
        public void Preview_exercises_the_output_path_with_test_provenance_and_never_touches_the_monitor()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            AlarmEvent? preview = s.Preview("pa");
            Assert.NotNull(preview);
            Assert.Equal(ObservationProvenance.Preview, preview!.Observation!.Value.Provenance);
            Assert.Equal(60f, preview.Value);
            Assert.Contains(Dispatched(), e => e.Detail == "preview");
            Assert.Equal(AlarmConditionState.Normal, s.SnapshotOf("pa")!.Condition);
            Assert.Equal(AlarmDataState.Waiting, s.SnapshotOf("pa")!.Data);
        }

        [Fact]
        public void Update_makes_a_new_revision_and_the_old_episode_closes_as_configuration_changed()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            Step(500);
            _feed.Deliver(Pa, 61f);
            Assert.True(s.Update(PaLevel(70)));
            Assert.Equal(2, s.SnapshotOf("pa")!.Definition.Revision);
            Assert.Contains(Dispatched(), e => e.Kind == AlarmEventKind.ConfigurationChanged);
            Assert.DoesNotContain(Dispatched(), e => e.Kind == AlarmEventKind.Cleared);
            Assert.Equal(AlarmConditionState.Normal, s.SnapshotOf("pa")!.Condition);
        }

        [Fact]
        public void Operator_actions_reach_the_episode_and_are_dispatched()
        {
            var s = Service();
            _feed.Connect(Serial, Pa);
            s.Add(PaLevel());
            Step(500);
            _feed.Deliver(Pa, 61f);
            Assert.Contains(s.Acknowledge("pa"), e => e.Kind == AlarmEventKind.Acknowledged);
            Assert.Contains(s.Snooze("pa", 30), e => e.Kind == AlarmEventKind.Snoozed);
            Assert.Contains(s.Resume("pa"), e => e.Kind == AlarmEventKind.Resumed);
            var kinds = Dispatched().Select(e => e.Kind).ToList();
            Assert.Contains(AlarmEventKind.Acknowledged, kinds);
            Assert.Contains(AlarmEventKind.Snoozed, kinds);
            Assert.Contains(AlarmEventKind.Resumed, kinds);
        }
    }
}
