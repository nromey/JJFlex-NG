using System.Collections.Generic;
using System.Linq;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The voltage presets against the real 2026-09-07 keying, at recorded
    /// timing, with the transmit transition where the trace put it.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SupplyVoltageReplayTests
    {
        private const string Serial = "0000-0000-0000-0000";

        private static List<AlarmEvent> Replay(AlarmMonitor m, bool captureBaselineInReceive)
        {
            var events = new List<AlarmEvent>();
            bool keyed = false, captured = false;
            foreach (MeterObservation o in SupplyVoltageReplayFixture.Observations())
            {
                if (!keyed && o.ReceiptMonotonicMs > SupplyVoltageReplayFixture.TransmitAtMs)
                {
                    events.AddRange(m.SetTransmit(true, SupplyVoltageReplayFixture.TransmitAtMs));
                    keyed = true;
                }
                events.AddRange(m.Observe(o, o.ReceiptMonotonicMs));
                if (captureBaselineInReceive && !captured)
                {
                    events.AddRange(m.CaptureBaseline(o.ReceiptMonotonicMs + 1));
                    captured = true;
                }
            }
            return events;
        }

        private static int Count(IEnumerable<AlarmEvent> e, AlarmEventKind k) => e.Count(x => x.Kind == k);

        [Fact]
        public void The_fixture_carries_a_healthy_sag_of_about_a_seventh_of_a_volt_under_load()
        {
            float receive = SupplyVoltageReplayFixture.Rows.Where(r => r.Ms < SupplyVoltageReplayFixture.TransmitAtMs).Max(r => r.Volts);
            float lowest = SupplyVoltageReplayFixture.Rows.Where(r => r.Ms > SupplyVoltageReplayFixture.TransmitAtMs).Min(r => r.Volts);
            Assert.Equal(13.984375f, receive);
            Assert.Equal(13.839844f, lowest);
            Assert.InRange(receive - lowest, 0.13f, 0.15f);
        }

        [Fact]
        public void Neither_the_low_nor_the_high_preset_fires_on_a_healthy_supply()
        {
            var low = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyVoltageReplayFixture.Meter, Serial, "lo") with { Enabled = true });
            var high = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyVoltageReplayFixture.Meter, Serial, "hi") with { Enabled = true });
            Assert.Equal(0, Count(Replay(low, false), AlarmEventKind.Fired));
            Assert.Equal(0, Count(Replay(high, false), AlarmEventKind.Fired));
            Assert.Equal(AlarmDataState.Fresh, low.Data);
        }

        [Fact]
        public void The_half_volt_drop_preset_stays_quiet_on_the_normal_transmit_sag()
        {
            var drop = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyVoltageReplayFixture.Meter, Serial, "drop") with { Enabled = true });
            var events = Replay(drop, captureBaselineInReceive: true);
            Assert.Equal(1, Count(events, AlarmEventKind.BaselineCaptured));
            Assert.Equal(13.980469f, drop.BaselineValue);
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.True(drop.IsTransmitting);
        }

        [Fact]
        public void LOWERED_TEST_DELTA_a_tenth_of_a_volt_fires_on_the_first_row_after_keying()
        {
            // Positive control on the drop rule against the same data: a 0.1 V
            // test delta must see the sag. 13.855469 at 396176 is 0.125 below
            // the 13.980469 baseline.
            var drop = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyVoltageReplayFixture.Meter, Serial, "drop")
                with { Enabled = true, Threshold = 0.1, Hysteresis = 0.02, IsTest = true });
            var events = Replay(drop, captureBaselineInReceive: true);
            var fired = events.Where(e => e.Kind == AlarmEventKind.Fired).ToList();
            Assert.Single(fired);
            Assert.Equal(396176, fired[0].AtMs);
            Assert.True(fired[0].Transmitting);
            Assert.InRange(fired[0].Change, 0.12f, 0.13f);
        }

        [Fact]
        public void A_baseline_captured_before_keying_is_not_recaptured_by_the_transmit_transition()
        {
            var drop = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyVoltageReplayFixture.Meter, Serial, "drop") with { Enabled = true });
            Replay(drop, captureBaselineInReceive: true);
            Assert.Equal(13.980469f, drop.BaselineValue);   // row one, not the sagged value
        }

        [Fact]
        public void SYNTHETIC_reaching_fifteen_volts_warns_even_though_it_is_the_range_top()
        {
            var high = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyVoltageReplayFixture.Meter, Serial, "hi") with { Enabled = true });
            var feed = new AlarmMonitorTests.Feed { Meter = SupplyVoltageReplayFixture.Meter };
            var events = high.Observe(feed.At(1000, 15.0f), 1000).ToList();
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(0, Count(events, AlarmEventKind.OutOfRangeSample));
        }

        [Fact]
        public void SYNTHETIC_a_further_fifth_of_a_volt_worse_bypasses_acknowledgement()
        {
            var low = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyVoltageReplayFixture.Meter, Serial, "lo") with { Enabled = true });
            var feed = new AlarmMonitorTests.Feed { Meter = SupplyVoltageReplayFixture.Meter };
            low.Observe(feed.At(1000, 11.9f), 1000);
            low.Acknowledge(1500);
            var quiet = low.Observe(feed.At(3000, 11.75f), 3000).ToList();   // 0.15 worse: not yet
            Assert.Equal(0, Count(quiet, AlarmEventKind.Worsened));
            var worse = low.Observe(feed.At(5000, 11.69f), 5000).ToList();   // 0.21 worse than 11.9
            Assert.Equal(1, Count(worse, AlarmEventKind.Worsened));
        }
    }
}
