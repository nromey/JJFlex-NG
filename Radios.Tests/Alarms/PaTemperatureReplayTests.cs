using System.Collections.Generic;
using System.Linq;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The PA presets against the real 2026-08-22 series, at recorded timing.
    /// Original preset expectations and lowered-test-line expectations are
    /// separate tests, and the lowered ones say so in their names.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class PaTemperatureReplayTests
    {
        private const string Serial = "0000-0000-0000-0000";

        private static List<AlarmEvent> Replay(AlarmMonitor m, bool tickBetweenRows = false)
        {
            var events = new List<AlarmEvent>();
            long last = 0;
            foreach (MeterObservation o in PaTemperatureReplayFixture.Observations())
            {
                if (tickBetweenRows && last != 0)
                    for (long t = last + 250; t < o.ReceiptMonotonicMs; t += 250) events.AddRange(m.Tick(t));
                events.AddRange(m.Observe(o, o.ReceiptMonotonicMs));
                last = o.ReceiptMonotonicMs;
            }
            return events;
        }

        private static int Count(IEnumerable<AlarmEvent> e, AlarmEventKind k) => e.Count(x => x.Kind == k);

        // ── the series itself, as evidence ──

        [Fact]
        public void The_fixture_is_the_series_the_design_measured()
        {
            Assert.Equal(49, PaTemperatureReplayFixture.Rows.Count);
            Assert.Equal(25.703125f, PaTemperatureReplayFixture.Rows[0].DegreesC);
            Assert.Equal(33.78125f, PaTemperatureReplayFixture.Rows[48].DegreesC);
            Assert.Equal(102600, PaTemperatureReplayFixture.Rows[48].Ms - PaTemperatureReplayFixture.Rows[0].Ms);
            Assert.Equal(25.671875f, PaTemperatureReplayFixture.Rows.Min(r => r.DegreesC));
            var (min, max) = PaTemperatureReplayFixture.Gaps();
            Assert.Equal(2110, min);
            Assert.Equal(2579, max);
        }

        [Fact]
        public void The_recorded_cadence_sits_inside_the_provisional_five_second_allowance_and_a_two_second_one_would_not()
        {
            // The design's 5 s is a hypothesis this series supports, with a
            // margin of about 2.4 s at the worst recorded gap. The second half
            // is the positive control on the number: an allowance the series
            // does NOT support must be seen to fail, or "green" says nothing.
            var five = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true });
            Assert.Equal(0, Count(Replay(five, tickBetweenRows: true), AlarmEventKind.DataStale));

            var two = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true, FreshnessAllowanceSeconds = 2 });
            Assert.True(Count(Replay(two, tickBetweenRows: true), AlarmEventKind.DataStale) > 0);
        }

        // ── the shipped preset, unchanged ──

        [Fact]
        public void The_sixty_degree_preset_never_fires_on_a_healthy_receive_sitting()
        {
            var m = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true });
            var events = Replay(m, tickBetweenRows: true);

            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.Equal(0, Count(events, AlarmEventKind.InvalidSample));
            Assert.Equal(0, Count(events, AlarmEventKind.OutOfRangeSample));
            Assert.Equal(AlarmDataState.Fresh, m.Data);
            Assert.Equal(AlarmConditionState.Normal, m.Condition);
            Assert.Equal(49, m.LastFresh!.Value.Sequence);
        }

        [Fact]
        public void The_investigation_rise_from_a_baseline_at_the_first_row_fires_when_the_rise_reaches_five_degrees()
        {
            var m = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaRiseFromBaseline, PaTemperatureReplayFixture.Meter, Serial, "rise")
                with { Enabled = true });
            var events = new List<AlarmEvent>();
            bool captured = false;
            foreach (MeterObservation o in PaTemperatureReplayFixture.Observations())
            {
                events.AddRange(m.Observe(o, o.ReceiptMonotonicMs));
                if (!captured)
                {
                    events.AddRange(m.CaptureBaseline(o.ReceiptMonotonicMs + 1));   // deliberately, in receive, from row one
                    captured = true;
                }
            }

            Assert.Equal(25.703125f, m.BaselineValue);
            var fired = events.Where(e => e.Kind == AlarmEventKind.Fired).ToList();
            Assert.NotEmpty(fired);
            // 30.578125 at tick 94717 is a rise of 4.875: NOT five. The next row,
            // 30.890625 at 96830, is 5.1875 and fires.
            Assert.Equal(96830, fired[0].AtMs);
            Assert.Equal(5.1875f, fired[0].Change, 4);
            // The endpoint rise the design quotes as about 8.08 C.
            Assert.Equal(8.078125f, PaTemperatureReplayFixture.Rows[48].DegreesC - m.BaselineValue, 4);
        }

        [Fact]
        public void The_rising_fast_trend_is_judgeable_at_the_last_two_rows_and_does_not_fire()
        {
            // 102.6 s of data against a 100 s window: the window is covered
            // from the first row at or past tick 56451 + 100000 = 156451, which
            // is the LAST TWO rows, 156941 and 159051 — not "exactly once at
            // the last row", as this test's name claimed until Astra's Track I
            // review recounted the windows against the archive. Both have four
            // early and five late samples in their ten-second bands; their
            // median observation times are 90.898 and 90.928 s apart, inside
            // 85 to 95; their median rises are 6.4375 and 6.7890625 C, well
            // under twelve. So: judgeable twice, warming up once, never fired.
            // The monitor emits coverage as TRANSITIONS, so the one WarmingUp
            // below is the first row and the absence of Insufficient says the
            // window never went uncovered once it was covered.
            var m = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaRisingFast, PaTemperatureReplayFixture.Meter, Serial, "trend")
                with { Enabled = true });
            var events = Replay(m);

            Assert.Equal(1, Count(events, AlarmEventKind.TrendWarmingUp));
            Assert.Equal(0, Count(events, AlarmEventKind.TrendInsufficientCoverage));
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.Normal, m.Condition);
        }

        // ── lowered test lines: rule testing, labelled as such ──

        [Fact]
        public void LOWERED_TEST_LINE_thirty_degrees_fires_on_the_first_row_at_the_line_and_clears_by_hysteresis()
        {
            // Rule testing on real quantised data. A 30 C line is not a preset
            // and not a fault; it is the design's "lowered configured threshold"
            // for exercising trigger and clear behaviour.
            var m = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true, Threshold = 30, IsTest = true });
            var events = Replay(m, tickBetweenRows: true);

            var fired = events.Where(e => e.Kind == AlarmEventKind.Fired).ToList();
            var cleared = events.Where(e => e.Kind == AlarmEventKind.Cleared).ToList();
            var worsened = events.Where(e => e.Kind == AlarmEventKind.Worsened).ToList();

            Assert.Equal(2, fired.Count);
            Assert.Equal(94717, fired[0].AtMs);          // 30.578125, the first row at or above 30
            Assert.Equal(30.578125f, fired[0].Value);
            Assert.Equal(1, fired[0].PersistenceSamples);

            // Clear at or below 28 after two fresh rows two seconds apart:
            // 27.953125 at 114389, then 27.859375 at 116501 (2.112 s later).
            Assert.Single(cleared);
            Assert.Equal(116501, cleared[0].AtMs);

            Assert.Equal(137733, fired[1].AtMs);         // 30.046875, the next crossing: a new episode
            Assert.NotEqual(fired[0].EpisodeId, fired[1].EpisodeId);

            // A further two degrees above the announced 30.046875: 32.53125 at 152644.
            Assert.Single(worsened);
            Assert.Equal(152644, worsened[0].AtMs);
            Assert.Equal(0, Count(events, AlarmEventKind.Reminder));   // 30 s never elapses inside an episode here
        }

        [Fact]
        public void LOWERED_TEST_LINE_sustained_two_over_one_second_fires_one_row_later_than_first_sample()
        {
            var m = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true, Threshold = 30, Persistence = AlarmPersistence.Sustained, SustainedCount = 2, SustainedSeconds = 1, IsTest = true });
            var events = Replay(m);
            var fired = events.Where(e => e.Kind == AlarmEventKind.Fired).ToList();
            Assert.NotEmpty(fired);
            Assert.Equal(96830, fired[0].AtMs);   // 30.578125 then 30.890625, 2.113 s apart
            Assert.Equal(2, fired[0].PersistenceSamples);
        }

        // ── synthetic supplements, labelled ──

        [Fact]
        public void SYNTHETIC_an_isolated_spike_fires_once_on_first_sample_and_clears_afterwards()
        {
            // The accepted cost of first-sample detection: a corrupt transient
            // warns once. It cannot cut transmit, and it clears by the rule.
            var m = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true });
            var feed = new AlarmMonitorTests.Feed { Meter = PaTemperatureReplayFixture.Meter };
            var events = new List<AlarmEvent>();
            long t = 0;
            foreach (float v in new[] { 30f, 30f, 61f, 30f, 30f, 30f })
            {
                t += 2100;
                events.AddRange(m.Observe(feed.At(t, v), t));
            }
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(1, Count(events, AlarmEventKind.Cleared));
        }

        [Fact]
        public void SYNTHETIC_a_clipped_high_value_at_the_range_endpoint_still_fires_and_is_not_out_of_range()
        {
            // 120 is the top of PATEMP's advertised range: a reading there
            // warns and is NOT flagged out of range; a reading past it is.
            var m = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true });
            var feed = new AlarmMonitorTests.Feed { Meter = PaTemperatureReplayFixture.Meter };
            var at = m.Observe(feed.At(1000, 120f), 1000).ToList();
            Assert.Equal(1, Count(at, AlarmEventKind.Fired));
            Assert.Equal(0, Count(at, AlarmEventKind.OutOfRangeSample));

            var m2 = new AlarmMonitor(AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, Serial, "pa")
                with { Enabled = true });
            var past = m2.Observe(feed.At(3000, 120.5f), 3000).ToList();
            Assert.Equal(1, Count(past, AlarmEventKind.Fired));
            Assert.Equal(1, Count(past, AlarmEventKind.OutOfRangeSample));
        }
    }
}
