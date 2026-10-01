using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The alarm state machine, driven by hand on a fake monotonic clock
    /// (#566, design section 2 and the test list in section 8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every test here feeds immutable observations with an explicit receipt
    /// time and asks what the monitor concluded. Nothing sleeps, nothing reads
    /// a wall clock, and a fake that re-stamps a cached value is not a
    /// freshness test — so the fixture builds each observation with its own
    /// sequence and receipt time and never re-reads a field.
    /// </para>
    /// <para>
    /// The four axes the design insists on — enabled intent, data
    /// availability, condition state, notification state — are asserted
    /// separately, because collapsing them is how "acknowledged" comes to
    /// read as "healthy".
    /// </para>
    /// </remarks>
    public sealed class AlarmMonitorTests
    {
        // ── fixture ──

        internal static readonly MeterDescriptor PaTemp = new MeterDescriptor(
            Index: 11, Name: "PATEMP", Description: "PA Temperature",
            Source: "TX-", SourceIndex: 4, Units: MeterUnits.DegreesC, Low: 0, High: 120);

        internal static AlarmDefinition Level(double threshold = 60, double hysteresis = 2,
            AlarmDirection direction = AlarmDirection.AtOrAbove,
            AlarmPersistence persistence = AlarmPersistence.FirstFreshSample,
            int sustainedCount = 2, double sustainedSeconds = 1,
            double freshness = 5, double reminder = 30, double worsening = 2,
            int clearSamples = 2, double clearSeconds = 2,
            AlarmScope scope = AlarmScope.WheneverConnected,
            double? sentinel = null)
            => AlarmDefinition.NewLevel("t", "Test alarm", "0000-0000-0000-0000",
                    MeterSelector.From(PaTemp), direction, threshold, hysteresis) with
               {
                   Persistence = persistence,
                   SustainedCount = sustainedCount,
                   SustainedSeconds = sustainedSeconds,
                   FreshnessAllowanceSeconds = freshness,
                   ReminderIntervalSeconds = reminder,
                   WorseningStep = worsening,
                   ClearSamples = clearSamples,
                   ClearSeconds = clearSeconds,
                   Scope = scope,
                   SentinelValue = sentinel,
                   Enabled = true,
               };

        /// <summary>
        /// A feed that mints observations the way the live attachment does:
        /// one sequence per delivery, receipt time as given, generation as
        /// given. Equal values get new sequences, which is the point.
        /// </summary>
        internal sealed class Feed
        {
            private long _seq;
            public int Generation = 1;
            public MeterDescriptor Meter = PaTemp;

            public MeterObservation At(long ms, float value, float? sentinel = null)
                => MeterObservation.Measured(Meter, value, ++_seq, ms,
                    new DateTime(2026, 8, 22, 1, 35, 46, DateTimeKind.Utc).AddMilliseconds(ms),
                    Generation, sentinel);
        }

        private static List<AlarmEvent> Run(AlarmMonitor m, params (long ms, float v)[] samples)
        {
            var feed = new Feed();
            var all = new List<AlarmEvent>();
            foreach (var (ms, v) in samples)
                all.AddRange(m.Observe(feed.At(ms, v), ms));
            return all;
        }

        private static int Count(IEnumerable<AlarmEvent> events, AlarmEventKind kind)
            => events.Count(e => e.Kind == kind);

        // ── above / below / equality ──

        [Fact]
        public void At_or_above_fires_on_equality_with_the_first_fresh_sample()
        {
            var m = new AlarmMonitor(Level(60));
            var events = Run(m, (1000, 59.9f), (3000, 60.0f));

            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.Active, m.Condition);
            Assert.Equal(60.0f, events.Single(e => e.Kind == AlarmEventKind.Fired).Value);
        }

        [Fact]
        public void At_or_below_fires_on_equality_and_never_on_a_value_above()
        {
            var m = new AlarmMonitor(Level(12.0, 0.2, AlarmDirection.AtOrBelow));
            var events = Run(m, (1000, 12.1f), (3000, 12.0f));

            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.Active, m.Condition);
        }

        [Fact]
        public void A_value_just_under_the_line_does_not_fire()
        {
            var m = new AlarmMonitor(Level(60));
            var events = Run(m, (1000, 59.99f));
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.Normal, m.Condition);
        }

        // ── initially-high arming ──

        [Fact]
        public void An_already_high_first_sample_after_arming_fires_without_a_prior_low_reading()
        {
            var m = new AlarmMonitor(Level(60));
            var events = Run(m, (1000, 63.5f));
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(AlarmDataState.Fresh, m.Data);
        }

        // ── out-of-range finite values ──

        [Fact]
        public void A_finite_value_outside_the_published_range_is_still_evidence_and_is_flagged()
        {
            var m = new AlarmMonitor(Level(60));
            var events = Run(m, (1000, 130f));   // range is 0..120

            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(1, Count(events, AlarmEventKind.OutOfRangeSample));
        }

        [Fact]
        public void A_dangerously_low_voltage_below_the_advertised_range_still_fires_a_below_alarm()
        {
            var supply = new MeterDescriptor(208, "+13.8A", "Main radio input voltage before fuse",
                "RAD", 208, MeterUnits.Volts, 10.5, 15);
            var def = AlarmDefinition.NewLevel("v", "Low supply", "0000",
                MeterSelector.From(supply), AlarmDirection.AtOrBelow, 12.0, 0.2) with { Enabled = true };
            var m = new AlarmMonitor(def);
            var feed = new Feed { Meter = supply };

            var events = m.Observe(feed.At(1000, 9.8f), 1000).ToList();
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(1, Count(events, AlarmEventKind.OutOfRangeSample));
        }

        // ── sentinels and invalid values ──

        [Fact]
        public void A_documented_sentinel_is_an_invalid_observation_not_a_reading()
        {
            var m = new AlarmMonitor(Level(-100, 1, AlarmDirection.AtOrBelow, sentinel: -150));
            var feed = new Feed();
            var events = m.Observe(feed.At(1000, -150f, sentinel: -150f), 1000).ToList();

            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.Equal(1, Count(events, AlarmEventKind.InvalidSample));
            Assert.Equal(AlarmDataState.Waiting, m.Data);
        }

        [Fact]
        public void A_non_finite_value_is_invalid_and_never_zero()
        {
            var m = new AlarmMonitor(Level(0, 1, AlarmDirection.AtOrBelow));
            var events = Run(m, (1000, float.NaN), (2000, float.PositiveInfinity));

            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.Equal(2, Count(events, AlarmEventKind.InvalidSample));
        }

        [Fact]
        public void An_invalid_sample_breaks_a_pending_sustained_trigger()
        {
            var m = new AlarmMonitor(Level(60, persistence: AlarmPersistence.Sustained,
                sustainedCount: 2, sustainedSeconds: 1));
            var events = Run(m, (1000, 61f), (2000, float.NaN), (3000, 61f));

            // Sample one starts the count, NaN breaks it, sample three restarts it —
            // so nothing has two fresh samples in a row and nothing fires.
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.Pending, m.Condition);
        }

        // ── hysteresis ──

        [Fact]
        public void Clearing_needs_two_fresh_samples_at_or_below_the_clear_line_two_seconds_apart()
        {
            var m = new AlarmMonitor(Level(60, 2, clearSamples: 2, clearSeconds: 2));
            var events = Run(m,
                (1000, 61f),     // fires
                (3000, 59f),     // in the band: still active
                (5000, 58f),     // first clear sample
                (6000, 58f),     // one second later: not yet two seconds
                (7100, 57.5f));  // 2.1 s after the first: cleared

            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(1, Count(events, AlarmEventKind.Cleared));
            Assert.Equal(7100, events.Single(e => e.Kind == AlarmEventKind.Cleared).AtMs);
            Assert.Equal(AlarmConditionState.Normal, m.Condition);
        }

        [Fact]
        public void A_reading_back_in_the_band_restarts_the_clear_count()
        {
            var m = new AlarmMonitor(Level(60, 2));
            var events = Run(m, (1000, 61f), (3000, 58f), (5000, 59f), (7000, 58f), (8000, 58f));

            // 58, then 59 (band), then 58, 58 one second apart: the two-second
            // span has not been met since the band reading.
            Assert.Equal(0, Count(events, AlarmEventKind.Cleared));
            Assert.Equal(AlarmConditionState.Active, m.Condition);
        }

        [Fact]
        public void Hysteresis_prevents_a_refire_inside_the_band_and_a_new_crossing_starts_a_new_episode()
        {
            var m = new AlarmMonitor(Level(60, 2));
            var events = Run(m, (1000, 61f), (3000, 58f), (6000, 58f), (8000, 59f), (10000, 61f));

            var fired = events.Where(e => e.Kind == AlarmEventKind.Fired).ToList();
            Assert.Equal(2, fired.Count);
            Assert.NotEqual(fired[0].EpisodeId, fired[1].EpisodeId);
        }

        [Fact]
        public void With_no_margin_a_constant_reading_exactly_at_the_line_fires_once_and_never_clears_in_either_direction()
        {
            // Astra's Track I review, finding 12 — a defect in the design's own
            // boundary specification. Both boundaries were inclusive, so with
            // zero hysteresis a value EQUAL to the line was on the alarm side
            // and beyond the clear at once: fire, clear two samples later,
            // fire again, for ever. A value at the line is on the alarm side
            // by definition; with no band, clearing needs it to have LEFT the
            // line. DRAFT rule for Noel to confirm or overturn.
            var above = new AlarmMonitor(Level(60, hysteresis: 0, clearSamples: 2, clearSeconds: 1));
            var events = Run(above, (1000, 60f), (3000, 60f), (5000, 60f), (7000, 60f), (9000, 60f));
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(0, Count(events, AlarmEventKind.Cleared));
            Assert.Equal(AlarmConditionState.Active, above.Condition);

            var below = new AlarmMonitor(Level(12, hysteresis: 0, direction: AlarmDirection.AtOrBelow, clearSamples: 2, clearSeconds: 1));
            var belowEvents = Run(below, (1000, 12f), (3000, 12f), (5000, 12f), (7000, 12f), (9000, 12f));
            Assert.Equal(1, Count(belowEvents, AlarmEventKind.Fired));
            Assert.Equal(0, Count(belowEvents, AlarmEventKind.Cleared));

            // Strictly past the line clears, in both directions.
            Assert.Equal(1, Count(Run(above, (11000, 59.9f), (13000, 59.9f)), AlarmEventKind.Cleared));
            Assert.Equal(1, Count(Run(below, (11000, 12.1f), (13000, 12.1f)), AlarmEventKind.Cleared));
        }

        [Fact]
        public void With_a_margin_equality_at_the_reset_line_still_clears_as_the_PA_preset_always_has()
        {
            // The positive control for the zero-margin rule: a positive margin
            // is untouched, and 58 still clears a 60-with-2 alarm.
            var m = new AlarmMonitor(Level(60, hysteresis: 2, clearSamples: 2, clearSeconds: 1));
            var events = Run(m, (1000, 61f), (3000, 58f), (5000, 58f));
            Assert.Equal(1, Count(events, AlarmEventKind.Cleared));
        }

        [Fact]
        public void A_margin_below_the_meters_precision_is_judged_as_no_margin_so_a_reading_at_the_line_fires_once_and_never_clears()
        {
            // Astra's Track IJK review, blocker 7 — introduced by the float
            // compare that repaired the 12.2 V reset. The margin was tested in
            // DOUBLE (Hysteresis > 0) while both boundaries were compared in
            // FLOAT, so a positive margin smaller than a float can resolve at
            // the line — 60 minus 0.0000001 is 60f exactly — passed validation,
            // chose the inclusive clear, and a constant 60 fired, cleared two
            // samples later and fired again: the zero-margin chatter, back
            // under a margin the editor accepted. The band is now judged at
            // the precision the question is asked in, and an empty band is no
            // band.
            var above = new AlarmMonitor(Level(60, hysteresis: 0.0000001, clearSamples: 2, clearSeconds: 1));
            var events = Run(above, (1000, 60f), (3000, 60f), (5000, 60f), (7000, 60f), (9000, 60f));
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(0, Count(events, AlarmEventKind.Cleared));
            Assert.Equal(AlarmConditionState.Active, above.Condition);

            var below = new AlarmMonitor(Level(12, hysteresis: 0.0000001, direction: AlarmDirection.AtOrBelow, clearSamples: 2, clearSeconds: 1));
            var belowEvents = Run(below, (1000, 12f), (3000, 12f), (5000, 12f), (7000, 12f), (9000, 12f));
            Assert.Equal(1, Count(belowEvents, AlarmEventKind.Fired));
            Assert.Equal(0, Count(belowEvents, AlarmEventKind.Cleared));

            // The positive control, in the same test: strictly past the line
            // still clears, so the silence above is the rule and not a dead
            // monitor.
            Assert.Equal(1, Count(Run(above, (11000, 59.9f), (13000, 59.9f)), AlarmEventKind.Cleared));
            Assert.Equal(1, Count(Run(below, (11000, 12.1f), (13000, 12.1f)), AlarmEventKind.Cleared));
        }

        [Fact]
        public void A_delta_alarm_with_a_margin_below_the_meters_precision_does_not_chatter_either()
        {
            var m = new AlarmMonitor(AlarmDefinition.NewRiseFromBaseline("r", "Rise", "0000", MeterSelector.From(PaTemp), 5, 0.0000001)
                with { Enabled = true, ClearSamples = 2, ClearSeconds = 1 });
            var feed = new Feed();
            m.Observe(feed.At(1000, 25f), 1000);
            m.CaptureBaseline(1001);
            var events = new List<AlarmEvent>();
            foreach (long t in new long[] { 3000, 5000, 7000, 9000 }) events.AddRange(m.Observe(feed.At(t, 30f), t));
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(0, Count(events, AlarmEventKind.Cleared));
        }

        [Fact]
        public void A_delta_alarm_with_no_margin_does_not_chatter_at_an_exact_rise_either()
        {
            var m = new AlarmMonitor(AlarmDefinition.NewRiseFromBaseline("r", "Rise", "0000", MeterSelector.From(PaTemp), 5, 0)
                with { Enabled = true, ClearSamples = 2, ClearSeconds = 1 });
            var feed = new Feed();
            m.Observe(feed.At(1000, 25f), 1000);
            m.CaptureBaseline(1001);
            var events = new List<AlarmEvent>();
            foreach (long t in new long[] { 3000, 5000, 7000, 9000 }) events.AddRange(m.Observe(feed.At(t, 30f), t));
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(0, Count(events, AlarmEventKind.Cleared));
        }

        // ── dwell versus count ──

        [Fact]
        public void Sustained_needs_both_the_count_and_the_span()
        {
            var m = new AlarmMonitor(Level(60, persistence: AlarmPersistence.Sustained,
                sustainedCount: 2, sustainedSeconds: 1));

            // Two samples 400 ms apart: count met, span not.
            var early = Run(m, (1000, 61f), (1400, 61f));
            Assert.Equal(0, Count(early, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.Pending, m.Condition);

            // Third sample at 2100: count 3, span 1.1 s → fires.
            var feed = new Feed();
            var later = m.Observe(feed.At(2100, 61f), 2100).ToList();
            Assert.Equal(1, Count(later, AlarmEventKind.Fired));
        }

        [Fact]
        public void A_fresh_sample_off_the_alarm_side_resets_a_pending_trigger()
        {
            var m = new AlarmMonitor(Level(60, persistence: AlarmPersistence.Sustained));
            var events = Run(m, (1000, 61f), (1500, 59f), (2500, 61f), (2900, 61f));
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
        }

        // ── equal-valued fresh samples ──

        [Fact]
        public void Two_equal_values_are_two_observations_and_both_count_toward_sustained()
        {
            var m = new AlarmMonitor(Level(60, persistence: AlarmPersistence.Sustained,
                sustainedCount: 2, sustainedSeconds: 1));
            var events = Run(m, (1000, 61f), (2100, 61f));
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
        }

        [Fact]
        public void An_equal_valued_sample_still_refreshes_the_data_and_carries_a_new_sequence()
        {
            var m = new AlarmMonitor(Level(60, freshness: 5));
            var feed = new Feed();
            m.Observe(feed.At(1000, 30f), 1000);
            m.Observe(feed.At(3000, 30f), 3000);
            Assert.Equal(2, m.LastFresh!.Value.Sequence);

            // 4.9 s after the second sample: still fresh. 5.1 s: stale.
            Assert.Empty(m.Tick(7900));
            var stale = m.Tick(8100).ToList();
            Assert.Equal(1, Count(stale, AlarmEventKind.DataStale));
            Assert.Equal(AlarmDataState.Stale, m.Data);
        }

        // ── freshness ──

        [Fact]
        public void No_first_sample_means_waiting_not_stale_and_not_healthy()
        {
            var m = new AlarmMonitor(Level(60));
            Assert.Equal(AlarmDataState.Waiting, m.Data);
            Assert.Empty(m.Tick(60_000));
            Assert.Equal(AlarmDataState.Waiting, m.Data);
        }

        [Fact]
        public void Only_one_sample_then_silence_goes_stale_after_the_allowance()
        {
            var m = new AlarmMonitor(Level(60, freshness: 5));
            Run(m, (1000, 30f));
            Assert.Empty(m.Tick(5999));
            var events = m.Tick(6001).ToList();
            Assert.Equal(1, Count(events, AlarmEventKind.DataStale));
            Assert.Empty(m.Tick(9000));   // no second stale event; transitions only
        }

        [Fact]
        public void A_gap_immediately_before_a_firing_still_fires_on_the_high_sample()
        {
            var m = new AlarmMonitor(Level(60, freshness: 5));
            Run(m, (1000, 30f));
            m.Tick(7000);   // stale
            var feed = new Feed(); feed.At(0, 0);
            var events = m.Observe(feed.At(20_000, 61f), 20_000).ToList();

            // A single high one fires immediately, even though unavailability
            // needs two fresh samples to be called resolved.
            Assert.Equal(1, Count(events, AlarmEventKind.Fired));
            Assert.Equal(1, Count(events, AlarmEventKind.DataResumed));
            Assert.Equal(AlarmDataState.Recovering, m.Data);
        }

        [Fact]
        public void A_gap_immediately_before_clearing_breaks_the_clear_count()
        {
            var m = new AlarmMonitor(Level(60, 2, freshness: 5));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.Observe(feed.At(3000, 58f), 3000);     // first clear sample
            m.Tick(9000);                            // stale: gap
            var events = m.Observe(feed.At(9500, 58f), 9500).ToList();
            var more = m.Observe(feed.At(10_000, 58f), 10_000).ToList();

            Assert.Equal(0, Count(events.Concat(more), AlarmEventKind.Cleared));
        }

        [Fact]
        public void Stale_while_active_keeps_the_episode_as_last_known_active_and_never_says_cleared()
        {
            var m = new AlarmMonitor(Level(60, freshness: 5));
            Run(m, (1000, 61f));
            var events = m.Tick(7000).ToList();

            var stale = events.Single(e => e.Kind == AlarmEventKind.DataStale);
            Assert.True(stale.WasActive);
            Assert.Equal(AlarmConditionState.LastKnownActive, m.Condition);
            Assert.Equal(0, Count(events, AlarmEventKind.Cleared));
        }

        [Fact]
        public void Recovery_from_stale_is_declared_only_after_two_fresh_samples()
        {
            var m = new AlarmMonitor(Level(60, freshness: 5));
            var feed = new Feed();
            m.Observe(feed.At(1000, 30f), 1000);
            m.Tick(7000);
            m.Observe(feed.At(20_000, 30f), 20_000);
            Assert.Equal(AlarmDataState.Recovering, m.Data);
            m.Observe(feed.At(22_000, 30f), 22_000);
            Assert.Equal(AlarmDataState.Fresh, m.Data);
        }

        [Fact]
        public void The_watchdog_notices_silence_when_no_callback_arrives_at_all()
        {
            // The whole point of the 250 ms tick: nothing else can report that
            // packets stopped, because reporting rides on packets.
            var m = new AlarmMonitor(Level(60, freshness: 5));
            Run(m, (1000, 30f));
            int staleEvents = 0;
            for (long t = 1250; t <= 8000; t += 250)
                staleEvents += Count(m.Tick(t), AlarmEventKind.DataStale);
            Assert.Equal(1, staleEvents);
        }

        [Fact]
        public void A_time_of_day_jump_does_not_change_freshness_because_the_monotonic_clock_governs()
        {
            var m = new AlarmMonitor(Level(60, freshness: 5));
            var meter = PaTemp;
            var o1 = MeterObservation.Measured(meter, 30f, 1, 1000,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, null);
            // The UTC stamp jumps an hour; the monotonic receipt is two seconds on.
            var o2 = MeterObservation.Measured(meter, 30f, 2, 3000,
                new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), 1, null);
            m.Observe(o1, 1000);
            m.Observe(o2, 3000);
            Assert.Empty(m.Tick(7900));
            Assert.Equal(AlarmDataState.Fresh, m.Data);
        }

        [Fact]
        public void A_suspend_and_resume_reads_as_one_long_gap()
        {
            var m = new AlarmMonitor(Level(60, freshness: 5));
            Run(m, (1000, 30f));
            var events = m.Tick(1000 + 3_600_000).ToList();   // an hour later, first tick after resume
            Assert.Equal(1, Count(events, AlarmEventKind.DataStale));
        }

        // ── scope ──

        [Fact]
        public void Transmit_only_scope_waits_for_transmit_and_fires_on_the_first_fresh_sample_after_entry()
        {
            var m = new AlarmMonitor(Level(60, scope: AlarmScope.TransmitOnly));
            var feed = new Feed();
            var rx = m.Observe(feed.At(1000, 65f), 1000).ToList();
            Assert.Equal(0, Count(rx, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.WaitingForScope, m.Condition);

            m.SetTransmit(true, 2000);
            var tx = m.Observe(feed.At(3000, 65f), 3000).ToList();
            Assert.Equal(1, Count(tx, AlarmEventKind.Fired));
        }

        [Fact]
        public void Leaving_scope_resets_pending_dwell_but_does_not_erase_an_active_episode()
        {
            var m = new AlarmMonitor(Level(60, scope: AlarmScope.TransmitOnly,
                persistence: AlarmPersistence.Sustained));
            var feed = new Feed();
            m.SetTransmit(true, 0);
            m.Observe(feed.At(1000, 65f), 1000);
            Assert.Equal(AlarmConditionState.Pending, m.Condition);
            m.SetTransmit(false, 1500);
            m.SetTransmit(true, 2000);
            var events = m.Observe(feed.At(2500, 65f), 2500).ToList();
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));   // dwell restarted

            var fired = m.Observe(feed.At(3600, 65f), 3600).ToList();
            Assert.Equal(1, Count(fired, AlarmEventKind.Fired));
            m.SetTransmit(false, 4000);
            Assert.Equal(AlarmConditionState.Active, m.Condition);
        }

        // ── reminders, worsening, acknowledgement, snooze ──

        [Fact]
        public void Reminders_come_on_fresh_samples_after_the_interval_never_from_a_timer()
        {
            var m = new AlarmMonitor(Level(60, reminder: 30));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            // Ticks alone never remind, however late.
            int reminders = 0;
            for (long t = 1250; t <= 40_000; t += 250) reminders += Count(m.Tick(t), AlarmEventKind.Reminder);
            Assert.Equal(0, reminders);

            // The data went stale in that silence; two fresh samples restore it,
            // and the reminder rides the next fresh sample after it is due.
            var a = m.Observe(feed.At(40_500, 61f), 40_500).ToList();
            Assert.Equal(1, Count(a, AlarmEventKind.Reminder));
        }

        /// <summary>A steady feed every two seconds — inside the five-second allowance, so nothing goes stale.</summary>
        private static List<AlarmEvent> Steady(AlarmMonitor m, Feed feed, long fromMs, long toMs, float value)
        {
            var all = new List<AlarmEvent>();
            for (long t = fromMs; t <= toMs; t += 2000)
                all.AddRange(m.Observe(feed.At(t, value), t));
            return all;
        }

        [Fact]
        public void A_reminder_waits_out_the_interval_on_a_fresh_feed()
        {
            var m = new AlarmMonitor(Level(60, reminder: 30));
            var feed = new Feed();
            var events = Steady(m, feed, 1000, 33_000, 61f);
            var reminders = events.Where(e => e.Kind == AlarmEventKind.Reminder).ToList();
            Assert.Single(reminders);
            Assert.Equal(31_000, reminders[0].AtMs);   // 30 s after the firing at 1000, on the sample that arrived then
        }

        [Fact]
        public void A_reminder_after_a_data_gap_says_why_and_bypasses_acknowledgement()
        {
            var m = new AlarmMonitor(Level(60, reminder: 30));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.Acknowledge(1500);
            m.Tick(7000);   // stale
            var events = m.Observe(feed.At(9000, 61f), 9000).ToList();
            var reminder = events.Single(e => e.Kind == AlarmEventKind.Reminder);
            Assert.Equal(AlarmReminderReason.DataResumed, reminder.ReminderReason);
        }

        [Fact]
        public void A_further_two_degree_rise_bypasses_the_reminder_interval_once_per_step()
        {
            var m = new AlarmMonitor(Level(60, reminder: 30, worsening: 2));
            var events = Run(m, (1000, 61f), (3000, 62.5f), (5000, 63.1f), (7000, 65.2f));

            var worse = events.Where(e => e.Kind == AlarmEventKind.Worsened).ToList();
            Assert.Equal(2, worse.Count);                 // 61→63.1, then 63.1→65.2
            Assert.Equal(5000, worse[0].AtMs);
            Assert.Equal(7000, worse[1].AtMs);
        }

        [Fact]
        public void Acknowledge_silences_routine_reminders_but_not_a_worsening_and_keeps_monitoring()
        {
            var m = new AlarmMonitor(Level(60, reminder: 30, worsening: 2));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            var ack = m.Acknowledge(2000).ToList();
            Assert.Equal(1, Count(ack, AlarmEventKind.Acknowledged));
            Assert.Equal(AlarmNotificationState.Acknowledged, m.Notification);
            Assert.Equal(AlarmConditionState.Active, m.Condition);   // not "safe"

            var quiet = Steady(m, feed, 3000, 35_000, 61f);
            Assert.Equal(0, Count(quiet, AlarmEventKind.Reminder));

            var worse = m.Observe(feed.At(37_000, 63.5f), 37_000).ToList();
            Assert.Equal(1, Count(worse, AlarmEventKind.Worsened));
        }

        [Fact]
        public void Acknowledgement_rearms_automatically_after_a_clear_and_a_new_crossing_is_a_new_episode()
        {
            var m = new AlarmMonitor(Level(60, 2));
            var feed = new Feed();
            var first = m.Observe(feed.At(1000, 61f), 1000).Single(e => e.Kind == AlarmEventKind.Fired);
            m.Acknowledge(1500);
            m.Observe(feed.At(3000, 58f), 3000);
            m.Observe(feed.At(5500, 58f), 5500);
            Assert.Equal(AlarmConditionState.Normal, m.Condition);
            Assert.Equal(AlarmNotificationState.None, m.Notification);

            var second = m.Observe(feed.At(9000, 61f), 9000).Single(e => e.Kind == AlarmEventKind.Fired);
            Assert.NotEqual(first.EpisodeId, second.EpisodeId);
        }

        [Fact]
        public void Snooze_expires_on_the_monotonic_clock_and_reminders_resume_on_the_next_fresh_sample()
        {
            var m = new AlarmMonitor(Level(60, reminder: 30));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.Snooze(60, 2000);
            Assert.Equal(AlarmNotificationState.Snoozed, m.Notification);

            var during = Steady(m, feed, 3000, 61_000, 61f);
            Assert.Equal(0, Count(during, AlarmEventKind.Reminder));

            var expiry = m.Tick(62_000).ToList();
            Assert.Equal(1, Count(expiry, AlarmEventKind.SnoozeExpired));

            var after = m.Observe(feed.At(62_500, 61f), 62_500).ToList();
            Assert.Equal(1, Count(after, AlarmEventKind.Reminder));
        }

        [Fact]
        public void Resume_ends_suppression_and_requests_the_current_warning_only_when_data_is_fresh()
        {
            var m = new AlarmMonitor(Level(60));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.Acknowledge(1500);
            var resumed = m.Resume(2000).ToList();
            Assert.Equal(1, Count(resumed, AlarmEventKind.Resumed));
            Assert.Equal(1, Count(resumed, AlarmEventKind.Reminder));

            m.Acknowledge(2500);
            m.Tick(9000);   // stale
            var resumedStale = m.Resume(9500).ToList();
            Assert.Equal(1, Count(resumedStale, AlarmEventKind.Resumed));
            Assert.Equal(0, Count(resumedStale, AlarmEventKind.Reminder));
        }

        [Fact]
        public void A_transmit_transition_while_high_and_acknowledged_warrants_a_new_warning()
        {
            var m = new AlarmMonitor(Level(60));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.Acknowledge(1500);
            m.SetTransmit(true, 2000);
            var events = m.Observe(feed.At(2500, 61f), 2500).ToList();
            Assert.Equal(1, Count(events, AlarmEventKind.Reminder));
            Assert.Equal(AlarmReminderReason.TransmitResumed,
                events.Single(e => e.Kind == AlarmEventKind.Reminder).ReminderReason);
        }

        // ── lifecycle ──

        [Fact]
        public void A_new_connection_generation_clears_observations_and_baselines_and_keeps_the_definition()
        {
            var m = new AlarmMonitor(Level(60));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.Acknowledge(1500);
            var events = m.NewConnection(2, 5000).ToList();

            Assert.Equal(AlarmDataState.Waiting, m.Data);
            Assert.Equal(AlarmNotificationState.None, m.Notification);
            Assert.Equal(AlarmConditionState.LastKnownActive, m.Condition);
            Assert.True(m.Definition.Enabled);
            Assert.Equal(1, Count(events, AlarmEventKind.DataMissing));
        }

        [Fact]
        public void A_reacquired_episode_after_reconnect_warns_once_for_the_data_and_then_keeps_reminding()
        {
            // Astra's Track I review, finding 2: the data loss set Notification
            // to None and nothing set it back when the episode was reacquired,
            // so the resumed warning was spoken once and every interval
            // reminder after it failed its Unacknowledged test for ever.
            var m = new AlarmMonitor(Level(60, reminder: 30));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.NewConnection(2, 5000);
            feed.Generation = 2;

            var resumed = m.Observe(feed.At(6000, 61f), 6000).ToList();
            Assert.Single(resumed, e => e.Kind == AlarmEventKind.Reminder && e.ReminderReason == AlarmReminderReason.DataResumed);
            Assert.Equal(AlarmConditionState.Active, m.Condition);
            Assert.Equal(AlarmNotificationState.Unacknowledged, m.Notification);

            // Flat and high every two seconds for the next ninety: three more
            // interval reminders, at the interval, not none.
            int reminders = 0;
            for (long t = 8000; t <= 96000; t += 2000)
                reminders += m.Observe(feed.At(t, 61f), t).Count(e => e.Kind == AlarmEventKind.Reminder && e.ReminderReason == AlarmReminderReason.Interval);
            Assert.Equal(3, reminders);
        }

        [Fact]
        public void A_reacquired_episode_that_returns_inside_the_band_is_active_and_unacknowledged_too()
        {
            // The sibling path: the first fresh sample after the loss sits in
            // the hysteresis band — still active, nothing to say yet — and the
            // notification state must still come back, or the next on-side
            // sample inherits the dead None.
            var m = new AlarmMonitor(Level(60, hysteresis: 2, reminder: 30));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            m.NewConnection(2, 5000);
            feed.Generation = 2;
            m.Observe(feed.At(6000, 59f), 6000);   // in the band: 58 to 60
            Assert.Equal(AlarmConditionState.Active, m.Condition);
            Assert.Equal(AlarmNotificationState.Unacknowledged, m.Notification);
        }

        [Fact]
        public void An_old_generation_callback_after_reconnect_is_discarded()
        {
            var m = new AlarmMonitor(Level(60));
            var feed = new Feed();
            m.NewConnection(2, 0);
            var events = m.Observe(feed.At(1000, 70f), 1000).ToList();   // feed still says generation 1
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.Equal(1, Count(events, AlarmEventKind.OldGenerationDiscarded));
        }

        [Fact]
        public void Disabling_keeps_the_definition_and_stops_firing_and_enabling_reacquires()
        {
            var m = new AlarmMonitor(Level(60));
            var feed = new Feed();
            m.SetEnabled(false, 0);
            var off = m.Observe(feed.At(1000, 70f), 1000).ToList();
            Assert.Equal(0, Count(off, AlarmEventKind.Fired));
            m.SetEnabled(true, 2000);
            var on = m.Observe(feed.At(3000, 70f), 3000).ToList();
            Assert.Equal(1, Count(on, AlarmEventKind.Fired));
        }

        [Fact]
        public void A_definition_revision_closes_the_old_episode_as_configuration_changed_and_judges_the_next_sample_afresh()
        {
            var m = new AlarmMonitor(Level(60));
            var feed = new Feed();
            m.Observe(feed.At(1000, 61f), 1000);
            var replaced = m.Replace(Level(70) with { Revision = 2 }, 2000).ToList();

            Assert.Equal(1, Count(replaced, AlarmEventKind.ConfigurationChanged));
            Assert.Equal(0, Count(replaced, AlarmEventKind.Cleared));   // never "cleared because the line moved"
            var next = m.Observe(feed.At(3000, 61f), 3000).ToList();
            Assert.Equal(0, Count(next, AlarmEventKind.Fired));
            Assert.Equal(AlarmConditionState.Normal, m.Condition);
        }

        // ── rise from a captured baseline ──

        [Fact]
        public void Rise_from_baseline_is_unavailable_until_the_operator_captures_one_in_receive()
        {
            var def = AlarmDefinition.NewRiseFromBaseline("r", "Investigation rise", "0000",
                MeterSelector.From(PaTemp), 5, 1) with { Enabled = true };
            var m = new AlarmMonitor(def);
            var feed = new Feed();
            var none = m.Observe(feed.At(1000, 40f), 1000).ToList();
            Assert.Equal(0, Count(none, AlarmEventKind.Fired));
            Assert.Equal(AlarmBaselineState.NotCaptured, m.Baseline);

            m.SetTransmit(true, 1500);
            var refused = m.CaptureBaseline(1600).ToList();
            Assert.Equal(1, Count(refused, AlarmEventKind.BaselineRefused));   // never at key-down
            m.SetTransmit(false, 1700);

            var captured = m.CaptureBaseline(1800).ToList();
            Assert.Equal(1, Count(captured, AlarmEventKind.BaselineCaptured));
            Assert.Equal(40f, m.BaselineValue);

            var fired = m.Observe(feed.At(3000, 45f), 3000).ToList();
            Assert.Equal(1, Count(fired, AlarmEventKind.Fired));
            Assert.Equal(5f, fired.Single(e => e.Kind == AlarmEventKind.Fired).Change, 3);
        }

        [Fact]
        public void The_baseline_is_invalidated_on_reconnect_and_is_never_recaptured_by_unkeying()
        {
            var def = AlarmDefinition.NewRiseFromBaseline("r", "Investigation rise", "0000",
                MeterSelector.From(PaTemp), 5, 1) with { Enabled = true };
            var m = new AlarmMonitor(def);
            var feed = new Feed();
            m.Observe(feed.At(1000, 40f), 1000);
            m.CaptureBaseline(1100);
            m.SetTransmit(true, 2000);
            m.SetTransmit(false, 5000);
            Assert.Equal(AlarmBaselineState.Captured, m.Baseline);
            Assert.Equal(40f, m.BaselineValue);

            var events = m.NewConnection(2, 6000).ToList();
            Assert.Equal(1, Count(events, AlarmEventKind.BaselineInvalidated));
            Assert.Equal(AlarmBaselineState.NotCaptured, m.Baseline);
        }

        [Fact]
        public void A_baseline_needs_a_fresh_sample_to_capture()
        {
            var def = AlarmDefinition.NewRiseFromBaseline("r", "Investigation rise", "0000",
                MeterSelector.From(PaTemp), 5, 1) with { Enabled = true, FreshnessAllowanceSeconds = 5 };
            var m = new AlarmMonitor(def);
            var feed = new Feed();
            m.Observe(feed.At(1000, 40f), 1000);
            m.Tick(7000);
            var refused = m.CaptureBaseline(7100).ToList();
            Assert.Equal(1, Count(refused, AlarmEventKind.BaselineRefused));
        }

        // ── rising fast (optional, disabled by default) ──

        [Fact]
        public void Rising_fast_reports_warming_up_until_a_full_window_exists_and_fires_on_a_twelve_degree_rise()
        {
            var def = AlarmDefinition.NewRisingFast("f", "Rising fast", "0000",
                MeterSelector.From(PaTemp)) with { Enabled = true };
            Assert.False(AlarmDefinition.NewRisingFast("x", "x", "0", MeterSelector.From(PaTemp)).Enabled);

            var m = new AlarmMonitor(def);
            var feed = new Feed();
            var events = new List<AlarmEvent>();
            // A synthetic quantised rise: 2.1 s cadence like the real series,
            // 30 C flat for 10 s, then climbing 0.3 C per sample.
            float v = 30f;
            long t = 0;
            for (int i = 0; i < 60; i++)
            {
                if (i > 5) v += 0.3f;
                t += 2100;
                events.AddRange(m.Observe(feed.At(t, v), t));
            }
            Assert.Equal(1, Count(events, AlarmEventKind.TrendWarmingUp));
            var fired = events.Where(e => e.Kind == AlarmEventKind.Fired).ToList();
            Assert.Single(fired);
            Assert.InRange(fired[0].IntervalSeconds, 85, 95);
            Assert.True(fired[0].Change >= 12f);
        }

        [Fact]
        public void Rising_fast_with_a_gap_in_the_window_reports_insufficient_coverage_and_does_not_fire()
        {
            var def = AlarmDefinition.NewRisingFast("f", "Rising fast", "0000",
                MeterSelector.From(PaTemp)) with { Enabled = true };
            var m = new AlarmMonitor(def);
            var feed = new Feed();
            var events = new List<AlarmEvent>();
            float v = 30f;
            long t = 0;
            for (int i = 0; i < 60; i++)
            {
                v += 0.4f;
                t += i == 30 ? 8000 : 2100;   // one gap over the allowance mid-window
                events.AddRange(m.Tick(t - 100));
                events.AddRange(m.Observe(feed.At(t, v), t));
            }
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
            Assert.True(Count(events, AlarmEventKind.TrendInsufficientCoverage) >= 1);
        }

        [Fact]
        public void Two_closely_spaced_samples_cannot_fire_the_trend_because_each_band_needs_three()
        {
            var def = AlarmDefinition.NewRisingFast("f", "Rising fast", "0000",
                MeterSelector.From(PaTemp)) with { Enabled = true };
            var m = new AlarmMonitor(def);
            var feed = new Feed();
            var events = new List<AlarmEvent>();
            events.AddRange(m.Observe(feed.At(1000, 30f), 1000));
            events.AddRange(m.Observe(feed.At(1500, 30f), 1500));
            events.AddRange(m.Observe(feed.At(91_000, 60f), 91_000));
            events.AddRange(m.Observe(feed.At(91_500, 60f), 91_500));
            Assert.Equal(0, Count(events, AlarmEventKind.Fired));
        }
    }
}
