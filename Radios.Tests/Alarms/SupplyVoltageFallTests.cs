using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// A FALLING supply voltage trips the alarm, on both descriptor sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel, 2026-09-30 (#566): <i>"Voltage must be covered, including
    /// decreases."</i> The replay fixture is a healthy keying — the supply sags
    /// a seventh of a volt and nothing fires, which is the right NEGATIVE
    /// result and the only thing the real data can show. A test that only
    /// ever trips on a rise proves nothing about a sag, and a sag is the
    /// failure an operator actually gets. So these take the real series — its
    /// timing, its receive rows, the transmit transition where the trace put
    /// it — and pull the under-load rows down to a sag that CROSSES the preset
    /// line. Everything synthetic is labelled.
    /// </para>
    /// <para>
    /// <b>Both descriptor sets, deliberately.</b> <c>+13.8A</c> and
    /// <c>+13.8B</c> exist on the 6300 and the 8600 and denote different
    /// points (before and after the fuse; at the PA and at the CPU). The alarm
    /// logic does not care, and these prove it does not: the same fall trips
    /// the same preset on all four, and only the place-phrase in the sentence
    /// differs. What the bench can confirm is the 8600 mapping; the 6300's
    /// needs that radio or its trace.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SupplyVoltageFallTests
    {
        private const string Serial = "0000-0000-0000-0000";

        private static readonly MeterDescriptor A6300 = new MeterDescriptor(208, "+13.8A", "Main radio input voltage before fuse", "RAD", 208, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor B6300 = new MeterDescriptor(210, "+13.8B", "Main radio input voltage after fuse", "RAD", 210, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor A8600 = SupplyVoltageReplayFixture.Meter;
        private static readonly MeterDescriptor B8600 = new MeterDescriptor(3, "+13.8B", "+13.8V at CPU", "RAD", 3, MeterUnits.Volts, 10.5, 15);

        public static IEnumerable<object[]> Descriptors()
        {
            yield return new object[] { A6300, "before the fuse" };
            yield return new object[] { B6300, "after the fuse" };
            yield return new object[] { A8600, "at the PA" };
            yield return new object[] { B8600, "at the CPU" };
        }

        /// <summary>
        /// SYNTHETIC: the real series, with every under-load row pulled down by
        /// <paramref name="sagVolts"/> so the supply falls when the transmitter
        /// loads it. The receive rows are untouched.
        /// </summary>
        private static IEnumerable<MeterObservation> SaggingReplay(MeterDescriptor meter, float sagVolts)
        {
            long seq = 0;
            var epoch = new DateTime(2026, 9, 7, 13, 9, 56, DateTimeKind.Utc);
            foreach (var (ms, v) in SupplyVoltageReplayFixture.Rows)
            {
                float value = ms > SupplyVoltageReplayFixture.TransmitAtMs ? v - sagVolts : v;
                yield return MeterObservation.Replayed(meter, value, ++seq, ms, epoch.AddMilliseconds(ms), 1, null);
            }
        }

        private static List<AlarmEvent> Replay(AlarmMonitor m, IEnumerable<MeterObservation> rows, bool captureBaselineInReceive = false)
        {
            var events = new List<AlarmEvent>();
            bool keyed = false, captured = false;
            foreach (MeterObservation o in rows)
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

        [Theory]
        [MemberData(nameof(Descriptors))]
        public void SYNTHETIC_a_supply_that_falls_to_the_low_line_under_load_trips_the_low_preset_on_the_first_row_there(MeterDescriptor meter, string place)
        {
            // 13.98 in receive; under load the real sag of 0.125 V plus a pulled
            // two volts puts the first transmit row at 11.73, below the 12.0 line.
            var low = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageLow, meter, Serial, "lo") with { Enabled = true });
            var events = Replay(low, SaggingReplay(meter, 2.0f));

            var fired = Assert.Single(events, e => e.Kind == AlarmEventKind.Fired);
            Assert.Equal(396176, fired.AtMs);                 // the first row after keying, at recorded timing
            Assert.True(fired.Transmitting);
            Assert.InRange(fired.Value, 11.7f, 11.9f);
            Assert.Equal(AlarmDirection.AtOrBelow, fired.Definition.Direction);
            Assert.Equal(AlarmConditionState.Active, low.Condition);

            // The action first, then the place without a comma, then the advice.
            string said = AlarmPhrasing.Warning(fired, terse: false);
            Assert.StartsWith("Release transmit now. Low supply voltage " + place + " is 11.", said);
            Assert.EndsWith("Have the supply path checked.", said);

            // Still low for the rest of the keying: thirty rows, no clear, one
            // interval reminder at thirty seconds, and the rest inside it.
            Assert.Equal(0, events.Count(e => e.Kind == AlarmEventKind.Cleared));
            Assert.Equal(2, events.Count(e => e.Kind == AlarmEventKind.Reminder && e.ReminderReason == AlarmReminderReason.Interval));
        }

        [Theory]
        [MemberData(nameof(Descriptors))]
        public void SYNTHETIC_a_fall_of_half_a_volt_from_the_receive_baseline_trips_the_drop_preset(MeterDescriptor meter, string place)
        {
            // The real sag is 0.125 V; pulled by another 0.5 V it is 0.625,
            // over the preset's half-volt line, on the first row under load.
            var drop = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageDrop, meter, Serial, "drop") with { Enabled = true });
            var events = Replay(drop, SaggingReplay(meter, 0.5f), captureBaselineInReceive: true);

            Assert.Equal(13.980469f, drop.BaselineValue);     // row one, in receive, before the fall
            var fired = Assert.Single(events, e => e.Kind == AlarmEventKind.Fired);
            Assert.Equal(396176, fired.AtMs);
            Assert.InRange(fired.Change, 0.62f, 0.63f);
            Assert.Equal(AlarmDirection.AtOrBelow, fired.Definition.Direction);

            string said = AlarmPhrasing.Warning(fired, terse: false);
            Assert.StartsWith("Release transmit now. Supply voltage " + place + " fell 0.63 volts from its baseline to 13.", said);
        }

        [Theory]
        [MemberData(nameof(Descriptors))]
        public void A_fall_that_stays_above_the_line_does_not_trip_and_a_recovery_clears_after_two_rows_past_the_margin(MeterDescriptor meter, string place)
        {
            _ = place;
            // Negative control on the real data first: the healthy 0.125 V sag
            // trips nothing on any descriptor.
            var low = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageLow, meter, Serial, "lo") with { Enabled = true });
            Assert.DoesNotContain(Replay(low, SaggingReplay(meter, 0f)), e => e.Kind == AlarmEventKind.Fired);

            // Then a fall that trips, followed by the supply coming back: two
            // fresh rows at or above the clear line (12.0 plus the 0.2 margin)
            // clear it, and the clearance names the value.
            var tripped = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.VoltageLow, meter, Serial, "lo") with { Enabled = true });
            var feed = new AlarmMonitorTests.Feed { Meter = meter };
            Assert.Single(tripped.Observe(feed.At(1000, 11.9f), 1000), e => e.Kind == AlarmEventKind.Fired);
            Assert.DoesNotContain(tripped.Observe(feed.At(3000, 12.1f), 3000), e => e.Kind == AlarmEventKind.Cleared);   // in the band
            Assert.DoesNotContain(tripped.Observe(feed.At(5000, 12.2f), 5000), e => e.Kind == AlarmEventKind.Cleared);   // first past the margin
            var cleared = Assert.Single(tripped.Observe(feed.At(7000, 12.3f), 7000), e => e.Kind == AlarmEventKind.Cleared);
            Assert.Equal(12.3f, cleared.Value);
            Assert.Equal(AlarmConditionState.Normal, tripped.Condition);
        }

        [Fact]
        public void The_four_descriptors_are_the_two_radios_two_supply_points_and_each_has_its_own_place_phrase()
        {
            // The fixture's own claim, pinned: these are the descriptor sets the
            // phrasing table holds, and no two share a place-phrase.
            var places = Descriptors().Select(d => AlarmPhrasing.MeasurementPoint((MeterDescriptor)d[0])).ToList();
            Assert.Equal(new[] { "before the fuse", "after the fuse", "at the PA", "at the CPU" }, places);
            Assert.Equal(4, places.Distinct().Count());
        }
    }
}
