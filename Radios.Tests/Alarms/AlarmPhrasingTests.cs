using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The ASSEMBLED sentences, with real values: degrees, decimals, negative
    /// numbers, source names. What a person hears end to end, not what the
    /// template looks like. Every sentence here is also marked for Noel's
    /// review in the lexicon; these tests pin the assembly, not the copy.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AlarmPhrasingTests
    {
        private const string Serial = "0000";

        private static readonly MeterDescriptor Pa = PaTemperatureReplayFixture.Meter;
        // The two inventories this build holds: a 6300 (trace of 2026-09-06)
        // and the bench 8600 (capture of 2026-09-07). Both radios publish
        // +13.8A and +13.8B, and they mean different places on each.
        private static readonly MeterDescriptor SupplyA6300 = new MeterDescriptor(208, "+13.8A", "Main radio input voltage before fuse", "RAD", 208, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor SupplyB6300 = new MeterDescriptor(210, "+13.8B", "Main radio input voltage after fuse", "RAD", 210, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor SupplyA8600 = new MeterDescriptor(2, "+13.8A", "+13.8V at PA", "RAD", 2, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor SupplyB8600 = new MeterDescriptor(3, "+13.8B", "+13.8V at CPU", "RAD", 3, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor SupplyUnmet = new MeterDescriptor(7, "+13.8C", "Main radio input voltage at the socket", "RAD", 7, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor Alc = new MeterDescriptor(30, "ALC", "Signal", "TX-", 0, MeterUnits.Dbfs, -50, 0);
        private static readonly MeterDescriptor Unitless = new MeterDescriptor(31, "CODEC", "Signal", "TX-", 0, MeterUnits.None, 0, 100);

        private static AlarmEvent Ev(AlarmDefinition def, MeterDescriptor meter, float value, bool tx,
            AlarmEventKind kind = AlarmEventKind.Fired, float change = float.NaN, double interval = double.NaN, double age = double.NaN)
        {
            var obs = MeterObservation.Measured(meter, value, 1, 1000, DateTime.UtcNow, 1, null);
            return new AlarmEvent
            {
                Kind = kind, Definition = def, EpisodeId = "ep", AtMs = 1000, Observation = obs, Value = value,
                Threshold = def.Threshold, Change = change, IntervalSeconds = interval, Transmitting = tx, AgeSeconds = age,
            };
        }

        [Fact]
        public void The_pa_preset_speaks_the_action_then_the_temperature_in_transmit_and_in_receive()
        {
            // Action first (Sol's language pass, adopted 2026-10-01): the first
            // complete clause still works if the rest is cut off.
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            Assert.Equal("Release transmit now. PA temperature is 63.5 degrees C.",
                AlarmPhrasing.Warning(Ev(def, Pa, 63.5f, tx: true)));
            Assert.Equal("Stay in receive. PA temperature is 60 degrees C. Let the radio cool.",
                AlarmPhrasing.Warning(Ev(def, Pa, 60f, tx: false)));
            Assert.DoesNotContain("cut", AlarmPhrasing.Warning(Ev(def, Pa, 63.5f, tx: true)), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_reminder_and_a_worsening_on_the_pa_preset_repeat_the_actionable_warning()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            Assert.Equal("Release transmit now. PA temperature is 65.2 degrees C.",
                AlarmPhrasing.Warning(Ev(def, Pa, 65.2f, tx: true, kind: AlarmEventKind.Worsened)));
            Assert.Equal("Stay in receive. PA temperature is 61 degrees C. Let the radio cool.",
                AlarmPhrasing.Warning(Ev(def, Pa, 61f, tx: false, kind: AlarmEventKind.Reminder)));
        }

        [Fact]
        public void The_investigation_rise_speaks_the_change_and_the_current_temperature()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaRiseFromBaseline, Pa, Serial, "rise");
            Assert.Equal("Release transmit now. PA temperature rose 5.2 degrees C from its baseline to 30.9 degrees C.",
                AlarmPhrasing.Warning(Ev(def, Pa, 30.890625f, tx: true, change: 5.1875f)));
        }

        [Fact]
        public void The_rising_fast_trend_speaks_the_actual_interval_not_ninety()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaRisingFast, Pa, Serial, "trend");
            string s = AlarmPhrasing.Warning(Ev(def, Pa, 42.9f, tx: true, change: 12.3f, interval: 90.3));
            // "now", not Sol's "to": a re-read reminder carries the rise from its
            // firing and the value from the current reading, so "rose 12.3 in 90
            // seconds to" a later value would be untrue.
            Assert.Equal("Release transmit now. PA temperature rose 12.3 degrees C in 90 seconds, now 42.9 degrees C. Check the radio's cooling.", s);
            string s2 = AlarmPhrasing.Warning(Ev(def, Pa, 42.9f, tx: false, change: 12.3f, interval: 86.6));
            Assert.Contains("in 87 seconds", s2);
            Assert.DoesNotContain("fan", s2, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_voltage_sentences_say_where_in_a_few_words_on_both_of_the_6300_supply_meters()
        {
            // Ruled by Noel 2026-10-01 07:17 (#566): "before THE fuse". The
            // sentence names low or high, and a fall keeps "from its baseline"
            // because it is measured from a captured baseline, not the last reading.
            var lowA = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "lo");
            Assert.Equal("Release transmit now. Low supply voltage before the fuse is 11.90 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true)));
            Assert.Equal("Stay in receive. Low supply voltage before the fuse is 11.90 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: false)));

            var highB = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyB6300, Serial, "hi");
            Assert.Equal("Stay in receive. High supply voltage after the fuse is 15.00 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(highB, SupplyB6300, 15.0f, tx: false)));

            var dropA = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyA6300, Serial, "drop");
            Assert.Equal("Release transmit now. Supply voltage before the fuse fell 0.52 volts from its baseline to 13.46 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(dropA, SupplyA6300, 13.46f, tx: true, change: -0.52f)));

            // The radio's own words are gone from the sentence, and no wording
            // beyond the place-phrase crept in with them.
            string s = AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true));
            Assert.DoesNotContain("Main radio input voltage", s);
            Assert.DoesNotContain("fuse socket", s);
        }

        [Fact]
        public void The_terse_warning_keeps_the_place_and_the_action_and_drops_only_the_advice()
        {
            // RULED by Noel 2026-10-01 07:17 and 07:18 (#566), superseding the
            // two-lengths part of 2026-09-23 04:42: the place phrases EVERYWHERE
            // are "before the fuse", "after the fuse", "at the PA" and "at the
            // CPU". Terse uses the SAME place phrase and is shortened by dropping
            // the follow-up advice, never the place. No commas still stands.
            var lowA = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "lo");
            string terseLow = AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true), terse: true);
            string normalLow = AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true), terse: false);
            Assert.Equal("Release transmit. Low supply voltage before the fuse: 11.90 volts.", terseLow);
            Assert.Equal("Release transmit now. Low supply voltage before the fuse is 11.90 volts. Have the supply path checked.", normalLow);

            var highB = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyB6300, Serial, "hi");
            Assert.Equal("Stay in receive. High supply voltage after the fuse: 15.00 volts.",
                AlarmPhrasing.Warning(Ev(highB, SupplyB6300, 15.0f, tx: false), terse: true));

            var lowPa = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA8600, Serial, "lo");
            Assert.Equal("Release transmit. Low supply voltage at the PA: 11.90 volts.",
                AlarmPhrasing.Warning(Ev(lowPa, SupplyA8600, 11.9f, tx: true), terse: true));
            var highCpu = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyB8600, Serial, "hi");
            Assert.Equal("Stay in receive. High supply voltage at the CPU: 15.00 volts.",
                AlarmPhrasing.Warning(Ev(highCpu, SupplyB8600, 15.0f, tx: false), terse: true));
            var dropCpu = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyB8600, Serial, "drop");
            Assert.Equal("Stay in receive. Supply voltage at the CPU fell 0.52 volts from baseline to 13.46 volts.",
                AlarmPhrasing.Warning(Ev(dropCpu, SupplyB8600, 13.46f, tx: false, change: -0.52f), terse: true));
            var dropA = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyA6300, Serial, "drop");
            Assert.Equal("Release transmit. Supply voltage before the fuse fell 0.63 volts from baseline to 13.36 volts.",
                AlarmPhrasing.Warning(Ev(dropA, SupplyA6300, 13.36f, tx: true, change: -0.63f), terse: true));

            // Genuinely shorter, with the same place, at every preset, radio and
            // action — and the advice is the only thing the terse form loses.
            foreach (var m in new[] { SupplyA6300, SupplyB6300, SupplyA8600, SupplyB8600, SupplyUnmet })
            foreach (string key in new[] { AlarmPresets.VoltageLow, AlarmPresets.VoltageHigh, AlarmPresets.VoltageDrop })
            foreach (bool tx in new[] { true, false })
            {
                var def = AlarmPresets.Build(key, m, Serial, "v");
                var e = Ev(def, m, 13.0f, tx: tx, change: -0.6f);
                string t = AlarmPhrasing.Warning(e, terse: true);
                string n = AlarmPhrasing.Warning(e, terse: false);
                Assert.True(t.Length < n.Length, t + " | " + n);
                Assert.Contains(AlarmPhrasing.MeasurementPoint(m), t);
                Assert.Contains(AlarmPhrasing.MeasurementPoint(m), n);
                Assert.StartsWith(tx ? "Release transmit." : "Stay in receive.", t);
                Assert.DoesNotContain("checked", t);
                Assert.EndsWith("Have the supply path checked.", n);
                if (key == AlarmPresets.VoltageDrop) Assert.Contains("from baseline", t);
            }

            // No comma anywhere in the four phrases, so no frame that leads with
            // a name can run into its verb.
            foreach (var m in new[] { SupplyA6300, SupplyB6300, SupplyA8600, SupplyB8600 })
            {
                Assert.DoesNotContain(",", AlarmPhrasing.MeasurementPoint(m));
                Assert.DoesNotContain(",", AlarmPresets.PresetName(AlarmPresets.VoltageLow, m));
            }
            Assert.Equal("Low supply voltage before the fuse", AlarmPresets.PresetName(AlarmPresets.VoltageLow, SupplyA6300));
            Assert.Equal("Delete the Low supply voltage before the fuse alarm? There is no undo.",
                Lexicon.Get("alarms.dialog.delete_confirm", ("alarm", AlarmPresets.PresetName(AlarmPresets.VoltageLow, SupplyA6300))));

            // An unmet meter is named by its own meter name, after "on meter".
            Assert.Equal("on meter +13.8C", AlarmPhrasing.MeasurementPoint(SupplyUnmet));
        }

        // ────────────────────────────────────────────────────────────────
        //  A re-read warning whose current reading is inside the clear margin
        //  (Astra's Track IJK2 review, blocker 3 — introduced on the sound-off
        //  route by the B3 fix, pre-existing on the tone route)
        //
        //  The refresh substitutes the CURRENT reading into the event the
        //  alarm fired with, keeping Kind=Fired, and the monitor keeps an
        //  episode active inside its margin by design. So a Heat alarm at 60
        //  with a 2-degree margin, fired at 61 and re-read at 59, said "is 59
        //  degrees C, at or above 60 degrees C" — a comparison false of the
        //  number it states. These are created-state tests through the same
        //  event shape Refresh builds, both directions. The first group covers
        //  values inside a REPRESENTABLE band (60/2 C, 12/0.2 V), where the
        //  clear is inclusive; it does not cover a definition with no
        //  representable band, which the branch also reaches and whose clear
        //  is strict — that is the no-band group below (Astra's Track IJK4
        //  review, blocker 1). The service-level cases are in
        //  AlarmDeliveryTests.
        // ────────────────────────────────────────────────────────────────

        private static AlarmDefinition Heat() =>
            AlarmDefinition.NewLevel("heat", "Heat", Serial, MeterSelector.From(Pa), AlarmDirection.AtOrAbove, 60, 2)
                with { Action = AlarmActionClass.NotifyOnly };

        private static AlarmDefinition LowVolts() =>
            AlarmDefinition.NewLevel("lv", "Low volts", Serial, MeterSelector.From(SupplyA8600), AlarmDirection.AtOrBelow, 12, 0.2)
                with { Action = AlarmActionClass.NotifyOnly };

        [Theory]
        [InlineData(58f, "58")]       // ON the clear line: one clearing sample in, the episode is still active, and the frame may not say "at or above 60"
        [InlineData(58.1f, "58.1")]
        [InlineData(59f, "59")]
        [InlineData(59.9f, "59.9")]
        public void A_level_warning_re_read_inside_its_margin_above_states_the_reading_and_what_clears_it(float value, string spoken)
        {
            string s = AlarmPhrasing.Warning(Ev(Heat(), Pa, value, tx: false));
            Assert.Equal("Your alarm named Heat is still active: PATEMP (PA Temperature) is " + spoken + " degrees C. The alarm clears at or below 58 degrees C.", s);
            Assert.DoesNotContain("at or above", s);
            Assert.DoesNotContain("fired", s);
        }

        [Theory]
        [InlineData(60f, "60")]       // the line itself is on the alarm side
        [InlineData(61f, "61")]
        [InlineData(63.5f, "63.5")]
        public void A_level_warning_re_read_on_the_alarm_side_above_keeps_the_fired_frame_whose_comparison_is_true(float value, string spoken)
        {
            Assert.Equal("Your alarm named Heat fired: PATEMP (PA Temperature) is " + spoken + " degrees C, at or above 60 degrees C.",
                AlarmPhrasing.Warning(Ev(Heat(), Pa, value, tx: false)));
        }

        [Theory]
        [InlineData(12.01f, "12.01")]
        [InlineData(12.1f, "12.10")]
        [InlineData(12.19f, "12.19")]
        [InlineData(12.2f, "12.20")]  // on the clear line, symmetric with 58 above
        public void A_level_warning_re_read_inside_its_margin_below_states_the_reading_and_what_clears_it(float value, string spoken)
        {
            string s = AlarmPhrasing.Warning(Ev(LowVolts(), SupplyA8600, value, tx: false));
            Assert.Equal("Your alarm named Low volts is still active: +13.8A (+13.8V at PA) is " + spoken + " volts. The alarm clears at or above 12.2 volts.", s);
            Assert.DoesNotContain("at or below", s);
        }

        [Theory]
        [InlineData(12f, "12.00")]
        [InlineData(11.9f, "11.90")]
        public void A_level_warning_re_read_on_the_alarm_side_below_keeps_the_fired_frame(float value, string spoken)
        {
            Assert.Equal("Your alarm named Low volts fired: +13.8A (+13.8V at PA) is " + spoken + " volts, at or below 12 volts.",
                AlarmPhrasing.Warning(Ev(LowVolts(), SupplyA8600, value, tx: false)));
        }

        // ────────────────────────────────────────────────────────────────
        //  The still-active frame on an alarm with NO representable clear
        //  margin (Astra's Track IJK4 review, blocker 1). Created-state tests.
        //
        //  With a zero margin, or one that collapses at the meter's float
        //  precision (0.0000001 at 60 and at 12 both round the clear boundary
        //  onto the line), the monitor clears only STRICTLY past the line —
        //  Noel's ruling, 2026-10-01, "less than 60". The frame said "clears at
        //  or below 60", a clearance the monitor never grants however many
        //  samples of exactly 60 arrive. The frame must state the strict
        //  boundary and the threshold.
        //
        //  The phrasing feeds def.Threshold, not ClearBoundary, in this
        //  branch, and a sentence test CAN see the difference. This section
        //  said it could not — "59.9999999 displays as 60, feeding
        //  ClearBoundary leaves every test green" — and that was a false
        //  coverage claim, true only of the integer fixtures here (Astra's
        //  Track IJK5 review, question 3). Two doubles with equal FLOAT casts
        //  are not the same decimal: 60.05000001 with a margin of 0.0000001
        //  has no representable band, and its clear boundary is 60.04999991.
        //  The guard below uses those values, so feeding the wrong field now
        //  fails a test rather than passing one.
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(0.0, 59f, "59")]
        [InlineData(0.0, 59.9f, "59.9")]
        [InlineData(0.0000001, 59f, "59")]
        [InlineData(0.0000001, 59.9f, "59.9")]
        public void A_still_active_warning_above_with_no_representable_margin_says_it_clears_strictly_below_the_line(double margin, float value, string spoken)
        {
            var def = AlarmDefinition.NewLevel("heat", "Heat", Serial, MeterSelector.From(Pa), AlarmDirection.AtOrAbove, 60, margin)
                with { Action = AlarmActionClass.NotifyOnly };
            Assert.False(def.HasRepresentableBand);

            string s = AlarmPhrasing.Warning(Ev(def, Pa, value, tx: false));
            Assert.Equal("Your alarm named Heat is still active: PATEMP (PA Temperature) is " + spoken + " degrees C. The alarm clears below 60 degrees C.", s);
            Assert.DoesNotContain("at or below", s);
            Assert.DoesNotContain("at or above", s);
            // The sentence's promise matches the monitor's rule at the stated line.
            Assert.False(def.IsBeyondClear(60));
        }

        [Theory]
        [InlineData(0.0, 12.1f, "12.10")]
        [InlineData(0.0, 12.01f, "12.01")]
        [InlineData(0.0000001, 12.1f, "12.10")]
        [InlineData(0.0000001, 12.01f, "12.01")]
        public void A_still_active_warning_below_with_no_representable_margin_says_it_clears_strictly_above_the_line(double margin, float value, string spoken)
        {
            var def = AlarmDefinition.NewLevel("lv", "Low volts", Serial, MeterSelector.From(SupplyA8600), AlarmDirection.AtOrBelow, 12, margin)
                with { Action = AlarmActionClass.NotifyOnly };
            Assert.False(def.HasRepresentableBand);

            string s = AlarmPhrasing.Warning(Ev(def, SupplyA8600, value, tx: false));
            Assert.Equal("Your alarm named Low volts is still active: +13.8A (+13.8V at PA) is " + spoken + " volts. The alarm clears above 12 volts.", s);
            Assert.DoesNotContain("at or above", s);
            Assert.DoesNotContain("at or below", s);
            Assert.False(def.IsBeyondClear(12));
        }

        [Fact]
        public void A_representable_band_still_says_at_or_below_and_at_or_above_its_clear_line()
        {
            // Controls for the pair choice: the inclusive frame stays where its
            // claim is true, including a band as small as the meter can hold.
            Assert.EndsWith("The alarm clears at or below 58 degrees C.", AlarmPhrasing.Warning(Ev(Heat(), Pa, 59f, tx: false)));
            Assert.EndsWith("The alarm clears at or above 12.2 volts.", AlarmPhrasing.Warning(Ev(LowVolts(), SupplyA8600, 12.1f, tx: false)));
            var tinyBand = AlarmDefinition.NewLevel("lv", "Low volts", Serial, MeterSelector.From(SupplyA8600), AlarmDirection.AtOrBelow, 12, 0.01)
                with { Action = AlarmActionClass.NotifyOnly };
            Assert.True(tinyBand.HasRepresentableBand);
            Assert.EndsWith("The alarm clears at or above 12.01 volts.", AlarmPhrasing.Warning(Ev(tinyBand, SupplyA8600, 12.005f, tx: false)));
        }

        /// <summary>
        /// The guard IJK5 deleted, restored with Astra's values. The no-band
        /// branch must state the THRESHOLD, which is the number the strict rule
        /// compares against. With a margin of 0.0000001 the clear boundary is
        /// the same float but a different decimal, so a sentence that fed it
        /// would say 60.04999991 — and this test would say so.
        /// </summary>
        [Fact]
        public void The_no_margin_frame_states_the_threshold_not_the_clear_boundary()
        {
            var heat = AlarmDefinition.NewLevel("heat", "Heat", Serial, MeterSelector.From(Pa), AlarmDirection.AtOrAbove, 60.05000001, 0.0000001)
                with { Action = AlarmActionClass.NotifyOnly };
            Assert.False(heat.HasRepresentableBand);
            Assert.Equal((float)heat.Threshold, (float)heat.ClearBoundary);      // the same float...
            Assert.NotEqual(AlarmPhrasing.AsEntered(heat.Threshold), AlarmPhrasing.AsEntered(heat.ClearBoundary));   // ...not the same decimal
            string s = AlarmPhrasing.Warning(Ev(heat, Pa, 60f, tx: false));
            Assert.Equal("Your alarm named Heat is still active: PATEMP (PA Temperature) is 60 degrees C. The alarm clears below 60.05000001 degrees C.", s);
            Assert.DoesNotContain("60.0499", s);

            var low = AlarmDefinition.NewLevel("lv", "Low volts", Serial, MeterSelector.From(SupplyA8600), AlarmDirection.AtOrBelow, 12.00500001, 0.0000001)
                with { Action = AlarmActionClass.NotifyOnly };
            Assert.False(low.HasRepresentableBand);
            string v = AlarmPhrasing.Warning(Ev(low, SupplyA8600, 12.1f, tx: false));
            Assert.Equal("Your alarm named Low volts is still active: +13.8A (+13.8V at PA) is 12.10 volts. The alarm clears above 12.00500001 volts.", v);
            Assert.DoesNotContain("12.0049", v);
        }

        // ────────────────────────────────────────────────────────────────
        //  A configured boundary is spoken as entered, a reading at display
        //  precision (Astra's Track IJK5 review, the formatting blocker — and
        //  the class it belongs to)
        //
        //  Every boundary the alarms spoke went through Value, the MEASUREMENT
        //  formatter: temperature to tenths, volts to hundredths. So a Heat
        //  alarm set at 60.04 said "clears below 60 degrees C" of a reading of
        //  60 that the monitor already counts as clearing, and a low line of
        //  11.996 volts was spoken "12.00". One shared substitution list feeds
        //  every generic frame, so the fix is there and these tests walk the
        //  frames: firing, still-active, reminder and worsened, both
        //  directions, and the list row and status line that speak the same
        //  number. The READING keeps display precision on purpose: it is a
        //  measurement, and "is 60 degrees C" of 60.0 is right.
        // ────────────────────────────────────────────────────────────────

        private static AlarmDefinition FineHeat(double margin) =>
            AlarmDefinition.NewLevel("heat", "Heat", Serial, MeterSelector.From(Pa), AlarmDirection.AtOrAbove, 60.04, margin)
                with { Action = AlarmActionClass.NotifyOnly };

        private static AlarmDefinition FineLowVolts(double margin) =>
            AlarmDefinition.NewLevel("lv", "Low volts", Serial, MeterSelector.From(SupplyA8600), AlarmDirection.AtOrBelow, 11.996, margin)
                with { Action = AlarmActionClass.NotifyOnly };

        /// <summary>Astra's case: a line of 60.04 with no margin, the reading at 60, which the monitor counts as a clearing sample.</summary>
        [Fact]
        public void A_threshold_of_60_point_04_is_spoken_as_60_point_04_in_every_frame_that_states_it()
        {
            var def = FineHeat(0);
            Assert.False(def.HasRepresentableBand);
            Assert.True(def.IsBeyondClear(60));            // 60 is on the clearing side of 60.04

            // Still active, Astra's sentence: not "clears below 60".
            Assert.Equal("Your alarm named Heat is still active: PATEMP (PA Temperature) is 60 degrees C. The alarm clears below 60.04 degrees C.",
                AlarmPhrasing.Warning(Ev(def, Pa, 60f, tx: false)));
            // Firing: the line is stated as entered, the reading at display precision.
            Assert.Equal("Your alarm named Heat fired: PATEMP (PA Temperature) is 61 degrees C, at or above 60.04 degrees C.",
                AlarmPhrasing.Warning(Ev(def, Pa, 61f, tx: false)));
            // Reminder and worsened state no boundary, and gain none.
            Assert.Equal("Heat: PATEMP (PA Temperature) still 61 degrees C.", AlarmPhrasing.Warning(Ev(def, Pa, 61f, tx: false, kind: AlarmEventKind.Reminder)));
            Assert.Equal("Heat: PATEMP (PA Temperature) now 61 degrees C.", AlarmPhrasing.Warning(Ev(def, Pa, 61f, tx: false, kind: AlarmEventKind.Worsened)));
            // The list row and the status line speak the same number.
            Assert.Equal("at or above 60.04 degrees C", AlarmPhrasing.Boundary(def));
            Assert.Contains("Line: at or above 60.04 degrees C.",
                AlarmPhrasing.Status(Snap(def with { Enabled = true }, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.None,
                    MeterSelectorStatus.Resolved, MeterObservation.Measured(Pa, 60f, 5, 1000, DateTime.UtcNow, 1, null), 3)));
            Assert.Contains(". at or above 60.04 degrees C. ",
                AlarmPhrasing.Row(Snap(def with { Enabled = true }, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.None,
                    MeterSelectorStatus.Resolved, MeterObservation.Measured(Pa, 60f, 5, 1000, DateTime.UtcNow, 1, null), 3)));
        }

        /// <summary>The mirror: a low line of 11.996 volts with no margin, the reading at 12, which clears.</summary>
        [Fact]
        public void A_low_line_of_11_point_996_volts_is_spoken_as_11_point_996_in_every_frame_that_states_it()
        {
            var def = FineLowVolts(0);
            Assert.False(def.HasRepresentableBand);
            Assert.True(def.IsBeyondClear(12));

            Assert.Equal("Your alarm named Low volts is still active: +13.8A (+13.8V at PA) is 12.00 volts. The alarm clears above 11.996 volts.",
                AlarmPhrasing.Warning(Ev(def, SupplyA8600, 12f, tx: false)));
            Assert.Equal("Your alarm named Low volts fired: +13.8A (+13.8V at PA) is 11.90 volts, at or below 11.996 volts.",
                AlarmPhrasing.Warning(Ev(def, SupplyA8600, 11.9f, tx: false)));
            Assert.Equal("Low volts: +13.8A (+13.8V at PA) still 11.90 volts.", AlarmPhrasing.Warning(Ev(def, SupplyA8600, 11.9f, tx: false, kind: AlarmEventKind.Reminder)));
            Assert.Equal("Low volts: +13.8A (+13.8V at PA) now 11.90 volts.", AlarmPhrasing.Warning(Ev(def, SupplyA8600, 11.9f, tx: false, kind: AlarmEventKind.Worsened)));
            Assert.Equal("at or below 11.996 volts", AlarmPhrasing.Boundary(def));
            Assert.Contains("Line: at or below 11.996 volts.",
                AlarmPhrasing.Status(Snap(def with { Enabled = true }, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.None,
                    MeterSelectorStatus.Resolved, MeterObservation.Measured(SupplyA8600, 12f, 5, 1000, DateTime.UtcNow, 1, null), 3, resolved: SupplyA8600)));
        }

        /// <summary>
        /// A representable band whose clear line is finer than the display
        /// precision: 60.04 with a 0.01 margin clears at 60.03, and 11.996 with
        /// 0.005 clears at 12.001. The clear line is spoken as the operator's
        /// two entries make it, with none of the arithmetic's residue
        /// (11.996 + 0.005 is 12.001000000000001 in double).
        /// </summary>
        [Fact]
        public void A_fine_clear_line_inside_a_representable_band_is_spoken_exactly()
        {
            var heat = FineHeat(0.01);
            Assert.True(heat.HasRepresentableBand);
            Assert.Equal("Your alarm named Heat is still active: PATEMP (PA Temperature) is 60 degrees C. The alarm clears at or below 60.03 degrees C.",
                AlarmPhrasing.Warning(Ev(heat, Pa, 60.03f, tx: false)));
            Assert.Equal("Your alarm named Heat fired: PATEMP (PA Temperature) is 60 degrees C, at or above 60.04 degrees C.",
                AlarmPhrasing.Warning(Ev(heat, Pa, 60.04f, tx: false)));   // 60.04 reads "60" at display precision; the line is still 60.04

            var low = FineLowVolts(0.005);
            Assert.True(low.HasRepresentableBand);
            string s = AlarmPhrasing.Warning(Ev(low, SupplyA8600, 11.998f, tx: false));
            Assert.Equal("Your alarm named Low volts is still active: +13.8A (+13.8V at PA) is 12.00 volts. The alarm clears at or above 12.001 volts.", s);
            Assert.DoesNotContain("12.001000000000001", s);
            Assert.Equal("Your alarm named Low volts fired: +13.8A (+13.8V at PA) is 11.99 volts, at or below 11.996 volts.",
                AlarmPhrasing.Warning(Ev(low, SupplyA8600, 11.99f, tx: false)));
        }

        /// <summary>
        /// The formatter itself, at its edges: what a person types comes back
        /// as typed, a computed line loses only the arithmetic's residue, a
        /// whole number has no padding, and nothing is ever spoken in
        /// scientific notation.
        /// </summary>
        [Theory]
        [InlineData(60.04, "60.04")]
        [InlineData(11.996, "11.996")]
        [InlineData(60.05000001, "60.05000001")]
        [InlineData(12.0, "12")]
        [InlineData(0.5, "0.5")]
        [InlineData(-3.5, "-3.5")]
        [InlineData(60.0 - 2.0, "58")]
        [InlineData(11.996 + 0.005, "12.001")]
        [InlineData(0.1 + 0.2, "0.3")]
        [InlineData(0.0000001, "0.0000001")]
        [InlineData(1e15, "1000000000000000")]
        [InlineData(-0.0, "0")]
        public void A_configured_line_is_spoken_as_entered(double value, string spoken)
        {
            Assert.Equal(spoken, AlarmPhrasing.AsEntered(value));
        }

        [Fact]
        public void The_still_active_frame_takes_the_action_first_like_every_other_warning()
        {
            var stop = Heat() with { Action = AlarmActionClass.StopTransmit };
            Assert.Equal("Release transmit now. Your alarm named Heat is still active: PATEMP (PA Temperature) is 59 degrees C. The alarm clears at or below 58 degrees C.",
                AlarmPhrasing.Warning(Ev(stop, Pa, 59f, tx: true)));
            Assert.Equal("Stay in receive. Your alarm named Heat is still active: PATEMP (PA Temperature) is 59 degrees C. The alarm clears at or below 58 degrees C.",
                AlarmPhrasing.Warning(Ev(stop, Pa, 59f, tx: false)));
        }

        [Fact]
        public void The_reminder_and_worsened_frames_assert_no_comparison_and_are_unchanged_inside_the_margin()
        {
            // Controls: these frames state a value and nothing about the line,
            // so a reading inside the margin was never false in them.
            Assert.Equal("Heat: PATEMP (PA Temperature) still 59 degrees C.",
                AlarmPhrasing.Warning(Ev(Heat(), Pa, 59f, tx: false, kind: AlarmEventKind.Reminder)));
            Assert.Equal("Heat: PATEMP (PA Temperature) now 59 degrees C.",
                AlarmPhrasing.Warning(Ev(Heat(), Pa, 59f, tx: false, kind: AlarmEventKind.Worsened)));
        }

        [Fact]
        public void An_edited_preset_keeps_its_wording_only_while_the_words_are_still_true()
        {
            // Astra's Track I review, finding 7: a PA preset edited onto a
            // voltage meter announced the volts as "PA temperature ... degrees
            // C"; one changed to notify-only still told the operator to
            // release transmit. The sentence was false, not awkward.
            var pa = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            Assert.True(AlarmPresets.WordingApplies(pa));

            // Same preset key, the meter changed to supply voltage and the line to 12 V.
            var onVolts = pa with { Selector = MeterSelector.From(SupplyA8600), Threshold = 12, Direction = AlarmDirection.AtOrBelow };
            Assert.False(AlarmPresets.WordingApplies(onVolts));
            string s = AlarmPhrasing.Warning(Ev(onVolts, SupplyA8600, 11.9f, tx: true));
            Assert.Equal("Release transmit now. Your alarm named High PA temperature fired: +13.8A (+13.8V at PA) is 11.90 volts, at or below 12 volts.", s);
            Assert.DoesNotContain("degrees", s);

            // Same preset, action changed to notify-only: no instruction at all.
            var notify = pa with { Action = AlarmActionClass.NotifyOnly };
            Assert.False(AlarmPresets.WordingApplies(notify));
            string n = AlarmPhrasing.Warning(Ev(notify, Pa, 63.5f, tx: true));
            Assert.Equal("Your alarm named High PA temperature fired: PATEMP (PA Temperature) is 63.5 degrees C, at or above 60 degrees C.", n);
            Assert.DoesNotContain("Release transmit", n);

            // Same preset, condition changed to a trend: the generic trend sentence, with the change it measured.
            var trend = pa with { Condition = AlarmCondition.RisingFast, Threshold = 12, TrendResetRise = 10 };
            Assert.False(AlarmPresets.WordingApplies(trend));
            Assert.Equal("Stay in receive. Your alarm named High PA temperature fired: PATEMP (PA Temperature) rose 12.3 degrees C in 90 seconds, now 42.9 degrees C.",
                AlarmPhrasing.Warning(Ev(trend, Pa, 42.9f, tx: false, change: 12.3f, interval: 90.3)));

            // Positive control: an untouched preset, and one whose edits leave
            // the words true (a different line, a different margin), keep them.
            Assert.True(AlarmPresets.WordingApplies(pa with { Threshold = 65, Hysteresis = 3, ReminderIntervalSeconds = 60 }));
            Assert.Equal("Release transmit now. PA temperature is 66 degrees C.",
                AlarmPhrasing.Warning(Ev(pa with { Threshold = 65 }, Pa, 66f, tx: true)));

            // And every voltage preset moved onto the PA meter loses its words
            // too: the generic frame names the METER and never says volts. (The
            // alarm's NAME still says "supply voltage", because a name is the
            // operator's data and is not rewritten for them.)
            foreach (string key in new[] { AlarmPresets.VoltageLow, AlarmPresets.VoltageHigh, AlarmPresets.VoltageDrop })
            {
                var v = AlarmPresets.Build(key, SupplyA8600, Serial, "v") with { Selector = MeterSelector.From(Pa) };
                Assert.False(AlarmPresets.WordingApplies(v));
                string w = AlarmPhrasing.Warning(Ev(v, Pa, 61f, tx: false, change: 5f));
                Assert.Contains("PATEMP (PA Temperature)", w);
                Assert.DoesNotContain("volts", w);
            }
        }

        [Fact]
        public void The_same_meter_names_mean_different_places_on_the_8600_and_the_sentence_says_each_radio_own()
        {
            // +13.8A and +13.8B are published by BOTH radios and mean different
            // points on each, so a place-phrase chosen by name alone would put
            // one radio's words in the other's mouth.
            var lowA = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA8600, Serial, "lo");
            Assert.Equal("Release transmit now. Low supply voltage at the PA is 11.90 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA8600, 11.9f, tx: true)));

            var highA = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyA8600, Serial, "hi");
            Assert.Equal("Stay in receive. High supply voltage at the PA is 15.00 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(highA, SupplyA8600, 15.0f, tx: false)));

            var dropB = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyB8600, Serial, "drop");
            Assert.Equal("Stay in receive. Supply voltage at the CPU fell 0.52 volts from its baseline to 13.46 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(dropB, SupplyB8600, 13.46f, tx: false, change: -0.52f)));

            var lowB = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyB8600, Serial, "lob");
            Assert.Equal("Stay in receive. Low supply voltage at the CPU is 13.20 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowB, SupplyB8600, 13.2f, tx: false)));
        }

        [Fact]
        public void A_supply_meter_we_have_not_met_is_named_by_its_own_meter_name()
        {
            var low = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyUnmet, Serial, "lo");
            Assert.Equal("Release transmit now. Low supply voltage on meter +13.8C is 11.90 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(low, SupplyUnmet, 11.9f, tx: true)));

            // No description at all: the meter's name, never an empty gap.
            var bare = SupplyUnmet with { Description = "" };
            var lowBare = AlarmPresets.Build(AlarmPresets.VoltageLow, bare, Serial, "bare");
            Assert.Equal("Release transmit now. Low supply voltage on meter +13.8C is 11.90 volts. Have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowBare, bare, 11.9f, tx: true)));
        }

        [Fact]
        public void A_generic_alarm_names_itself_and_its_meter_with_a_negative_decimal_value_and_the_chosen_action()
        {
            var def = AlarmDefinition.NewLevel("g", "Drive too hot", Serial, MeterSelector.From(Alc),
                AlarmDirection.AtOrAbove, -3.5, 1) with { Action = AlarmActionClass.StopTransmit };
            Assert.Equal("Release transmit now. Your alarm named Drive too hot fired: ALC (Signal) is -1.25 dBFS, at or above -3.5 dBFS.",
                AlarmPhrasing.Warning(Ev(def, Alc, -1.25f, tx: true)));

            var notify = def with { Action = AlarmActionClass.NotifyOnly, Direction = AlarmDirection.AtOrBelow, Threshold = -40 };
            Assert.Equal("Your alarm named Drive too hot fired: ALC (Signal) is -42.7 dBFS, at or below -40 dBFS.",
                AlarmPhrasing.Warning(Ev(notify, Alc, -42.7f, tx: false)));
        }

        [Fact]
        public void A_unitless_meter_reads_without_a_stumble()
        {
            var def = AlarmDefinition.NewLevel("u", "Codec watch", Serial, MeterSelector.From(Unitless),
                AlarmDirection.AtOrAbove, 80, 0) with { Action = AlarmActionClass.NotifyOnly };
            string s = AlarmPhrasing.Warning(Ev(def, Unitless, 85f, tx: false));
            Assert.Equal("Your alarm named Codec watch fired: CODEC (Signal) is 85, at or above 80.", s);
            Assert.DoesNotContain("  ", s);
        }

        [Fact]
        public void An_operator_name_is_data_and_cannot_become_a_template()
        {
            var def = AlarmDefinition.NewLevel("g", "{value} {alarm} <b>", Serial, MeterSelector.From(Alc),
                AlarmDirection.AtOrAbove, 0, 0) with { Action = AlarmActionClass.NotifyOnly };
            string s = AlarmPhrasing.Warning(Ev(def, Alc, 1f, tx: false));
            Assert.StartsWith("Your alarm named {value} {alarm} <b> fired: ALC", s);
        }

        [Fact]
        public void Data_lost_names_the_age_and_never_says_cleared()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            string tx = AlarmPhrasing.DataLost(Ev(def, Pa, float.NaN, tx: true, kind: AlarmEventKind.DataStale, age: 6.4));
            Assert.Equal("High PA temperature: no PATEMP (PA Temperature) reading for 6 seconds. It cannot be watched. Stop the transmission.", tx);
            string rx = AlarmPhrasing.DataLost(Ev(def, Pa, float.NaN, tx: false, kind: AlarmEventKind.DataStale, age: 12));
            Assert.Equal("High PA temperature: no PATEMP (PA Temperature) reading for 12 seconds. It cannot be watched.", rx);
            Assert.DoesNotContain("clear", tx + rx, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Cleared_is_a_state_update_with_the_value_and_no_permission()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            string s = AlarmPhrasing.Cleared(Ev(def, Pa, 57.5f, tx: false, kind: AlarmEventKind.Cleared));
            Assert.Equal("High PA temperature cleared. PATEMP (PA Temperature) 57.5 degrees C.", s);
            Assert.DoesNotContain("transmit", s, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_preview_is_labelled_a_test_and_carries_the_real_sentence_after_it()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            var e = Ev(def, Pa, 60f, tx: false) with { Detail = "preview" };
            string s = AlarmPhrasing.Preview(e, AlarmPhrasing.Warning(e));
            Assert.Equal("Test warning from the alarm High PA temperature. Nothing is wrong. A real warning would say: "
                + "Stay in receive. PA temperature is 60 degrees C. Let the radio cool.", s);
        }

        [Fact]
        public void Boundaries_read_as_inclusive_lines_in_the_meter_units()
        {
            Assert.Equal("at or above 60 degrees C", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "x")));
            Assert.Equal("at or below 12 volts", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "x")));
            Assert.Equal("a fall of 0.5 volts below the captured baseline", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyA6300, Serial, "x")));
            Assert.Equal("a rise of 5 degrees C above the captured baseline", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.PaRiseFromBaseline, Pa, Serial, "x")));
            Assert.Equal("a rise of 12 degrees C over about 90 seconds", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.PaRisingFast, Pa, Serial, "x")));
        }

        private static AlarmSnapshot Snap(AlarmDefinition def, AlarmDataState data, AlarmConditionState cond,
            AlarmNotificationState note, MeterSelectorStatus res, MeterObservation? last, double age,
            AlarmBaselineState baseline = AlarmBaselineState.NotApplicable, float baselineValue = float.NaN, double snooze = 0,
            MeterDescriptor? resolved = null)
            => new AlarmSnapshot(def, data, cond, note, baseline, baselineValue, res,
                res == MeterSelectorStatus.Resolved ? resolved ?? Pa : null, last, age, "ep", snooze, null, false, float.NaN, 0);

        [Fact]
        public void A_list_row_says_the_state_in_words()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true };
            var last = MeterObservation.Measured(Pa, 61f, 5, 1000, DateTime.UtcNow, 1, null);

            Assert.Equal("High PA temperature. PATEMP (PA Temperature). at or above 60 degrees C. active, acknowledged",
                AlarmPhrasing.Row(Snap(def, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Acknowledged, MeterSelectorStatus.Resolved, last, 1)));
            Assert.EndsWith("watching", AlarmPhrasing.Row(Snap(def, AlarmDataState.Fresh, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 1)));
            Assert.EndsWith("unavailable, no recent reading, cannot be watched", AlarmPhrasing.Row(Snap(def, AlarmDataState.Stale, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 9)));
            Assert.EndsWith("unavailable, more than one match, choose one", AlarmPhrasing.Row(Snap(def, AlarmDataState.Ambiguous, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Ambiguous, null, double.NaN)));
            Assert.EndsWith("disabled", AlarmPhrasing.Row(Snap(def with { Enabled = false }, AlarmDataState.Waiting, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Resolved, null, double.NaN)));
            Assert.EndsWith("was active, reading lost", AlarmPhrasing.Row(Snap(def, AlarmDataState.Stale, AlarmConditionState.LastKnownActive, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 30)));
        }

        [Fact]
        public void Read_status_distinguishes_enabled_watching_active_acknowledged_and_unavailable()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true };
            var last = MeterObservation.Measured(Pa, 61.25f, 5, 1000, DateTime.UtcNow, 1, null);
            // Three seconds, not one: the age words are MeterInventory.DescribeAge's,
            // and its singular ("1 second ago") is fixed on sprint45/integration
            // at 1be38b0f, not here. A plural age reads the same on both sides of
            // that merge, so this assertion is true before it and after it.
            string s = AlarmPhrasing.Status(Snap(def, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Acknowledged, MeterSelectorStatus.Resolved, last, 3));
            Assert.Equal("High PA temperature: enabled, watching, active. acknowledged. Latest reading 61.3 degrees C, 3 seconds ago. "
                + "Line: at or above 60 degrees C. Meter PATEMP (PA Temperature), found.", s);

            string snoozed = AlarmPhrasing.Status(Snap(def, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Snoozed, MeterSelectorStatus.Resolved, last, 1, snooze: 42));
            Assert.Contains("snoozed. Snoozed, 42 seconds left.", snoozed);

            string unavailable = AlarmPhrasing.Status(Snap(def, AlarmDataState.Missing, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Missing, null, double.NaN));
            Assert.Equal("High PA temperature: enabled, meter not available, normal. No reading yet. Line: at or above 60 degrees C. "
                + "Meter PATEMP, TX-:4, not published by this radio.", unavailable);

            var rise = AlarmPresets.Build(AlarmPresets.PaRiseFromBaseline, Pa, Serial, "r") with { Enabled = true };
            string noBaseline = AlarmPhrasing.Status(Snap(rise, AlarmDataState.Fresh, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 1, AlarmBaselineState.NotCaptured));
            Assert.Contains("No baseline captured. Capture one in receive.", noBaseline);
            string withBaseline = AlarmPhrasing.Status(Snap(rise, AlarmDataState.Fresh, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 1, AlarmBaselineState.Captured, 25.703125f));
            Assert.Contains("Baseline 25.7 degrees C.", withBaseline);
        }

        [Fact]
        public void Read_active_alarms_summarises_active_and_unavailable_and_the_disconnected_case()
        {
            var pa = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true };
            var lo = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "lo") with { Enabled = true };
            var last = MeterObservation.Measured(Pa, 61f, 5, 1000, DateTime.UtcNow, 1, null);
            var all = new List<AlarmSnapshot>
            {
                Snap(pa, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Unacknowledged, MeterSelectorStatus.Resolved, last, 1),
                Snap(lo, AlarmDataState.Missing, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Missing, null, double.NaN),
            };
            Assert.Equal("1 of 2 alarms active: High PA temperature 61 degrees C. 1 can't be watched right now.",
                AlarmPhrasing.ActiveSummary(all, connected: true, AlarmStoreState.Loaded, ""));
            Assert.Equal("No radio is connected, so nothing is being watched.",
                AlarmPhrasing.ActiveSummary(all, connected: false, AlarmStoreState.Loaded, ""));

            // Astra's prose qualification: "not active" must never read as
            // "watching successfully". An enabled, resolved alarm with no
            // reading yet, a baseline alarm with no baseline, and a last-known
            // episode each say what they are.
            var rise = AlarmPresets.Build(AlarmPresets.PaRiseFromBaseline, Pa, Serial, "rise") with { Enabled = true };
            var notReady = new List<AlarmSnapshot>
            {
                Snap(pa, AlarmDataState.Waiting, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Resolved, null, double.NaN),
                Snap(rise, AlarmDataState.Fresh, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 1,
                    baseline: AlarmBaselineState.NotCaptured),
            };
            // "Aren't ready to check their conditions", not "waiting for a
            // reading": the count also covers a missing baseline, waiting for
            // transmit and a trend still warming up (Sol's language pass).
            Assert.Equal("2 alarms, none active. 2 aren't ready to check their conditions yet.",
                AlarmPhrasing.ActiveSummary(notReady, connected: true, AlarmStoreState.Loaded, ""));
            Assert.Equal("2 alarms, none active. 1 isn't ready to check its condition yet.",
                AlarmPhrasing.ActiveSummary(new List<AlarmSnapshot> { notReady[0], Snap(rise, AlarmDataState.Fresh, AlarmConditionState.Normal,
                    AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 1, AlarmBaselineState.Captured, 25f) },
                    connected: true, AlarmStoreState.Loaded, ""));

            var lastKnown = new List<AlarmSnapshot>
            {
                Snap(pa, AlarmDataState.Stale, AlarmConditionState.LastKnownActive, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 9),
            };
            // A last-known alarm is NOT counted active: "1 of 1 alarms active"
            // was false once the meter stopped reporting. It has its own
            // sentence, and with nothing active now there is no head.
            Assert.Equal("The High PA temperature alarm was active when its reading stopped at 61 degrees C. 1 can't be watched right now.",
                AlarmPhrasing.ActiveSummary(lastKnown, connected: true, AlarmStoreState.Loaded, ""));

            // A meter lost outright leaves no reading to quote, and the sentence
            // ends cleanly rather than at a dangling "at".
            var lostOutright = new List<AlarmSnapshot>
            {
                Snap(pa, AlarmDataState.Missing, AlarmConditionState.LastKnownActive, AlarmNotificationState.None, MeterSelectorStatus.Resolved, null, double.NaN),
            };
            Assert.Equal("The High PA temperature alarm was active when its reading stopped. 1 can't be watched right now.",
                AlarmPhrasing.ActiveSummary(lostOutright, connected: true, AlarmStoreState.Loaded, ""));

            // Both kinds at once: the current one is counted and listed first,
            // the last-known one follows in its own sentence, then the counts.
            var mixed = new List<AlarmSnapshot>
            {
                Snap(lo, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Unacknowledged, MeterSelectorStatus.Resolved,
                    MeterObservation.Measured(SupplyA6300, 11.9f, 5, 1000, DateTime.UtcNow, 1, null), 2, resolved: SupplyA6300),
                Snap(pa, AlarmDataState.Stale, AlarmConditionState.LastKnownActive, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 9),
            };
            Assert.Equal("1 of 2 alarms active: Low supply voltage before the fuse 11.90 volts. "
                + "The High PA temperature alarm was active when its reading stopped at 61 degrees C. 1 can't be watched right now.",
                AlarmPhrasing.ActiveSummary(mixed, connected: true, AlarmStoreState.Loaded, ""));
            Assert.Equal("No alarms are defined for this radio.",
                AlarmPhrasing.ActiveSummary(new List<AlarmSnapshot>(), connected: true, AlarmStoreState.Empty, ""));
            Assert.StartsWith("The alarm configuration for this radio could not be read: not valid JSON",
                AlarmPhrasing.ActiveSummary(all, connected: true, AlarmStoreState.Unavailable, "not valid JSON: x"));
        }

        /// <summary>
        /// A supply preset's name carries the place-phrase, and every sentence
        /// that leads with the alarm's name inherits it. Read them assembled,
        /// because that is where a comma inside a NAME shows up.
        /// </summary>
        [Fact]
        public void The_supply_preset_name_carries_its_place_into_every_sentence_that_names_the_alarm()
        {
            var low = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "lo") with { Enabled = true };
            Assert.Equal("Low supply voltage before the fuse", low.Name);
            var fall = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyB8600, Serial, "fall") with { Enabled = true };
            Assert.Equal("Supply voltage fall at the CPU", fall.Name);

            var reading = MeterObservation.Measured(SupplyA6300, 12.6f, 5, 1000, DateTime.UtcNow, 1, null);

            Assert.Equal("Low supply voltage before the fuse. +13.8A (Main radio input voltage before fuse). at or below 12 volts. active",
                AlarmPhrasing.Row(Snap(low, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Unacknowledged,
                    MeterSelectorStatus.Resolved, reading, 2, resolved: SupplyA6300)));

            Assert.Equal("Low supply voltage before the fuse cleared. +13.8A (Main radio input voltage before fuse) 12.60 volts.",
                AlarmPhrasing.Cleared(Ev(low, SupplyA6300, 12.6f, tx: false, kind: AlarmEventKind.Cleared)));

            Assert.Equal("Low supply voltage before the fuse: no +13.8A (Main radio input voltage before fuse) reading for 6 seconds. It cannot be watched.",
                AlarmPhrasing.DataLost(Ev(low, SupplyA6300, float.NaN, tx: false, kind: AlarmEventKind.DataStale, age: 6)));

            // Two alarms active at once, one of each family.
            var pa = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true };
            var paReading = MeterObservation.Measured(Pa, 61f, 5, 1000, DateTime.UtcNow, 1, null);
            var both = new List<AlarmSnapshot>
            {
                Snap(pa, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Unacknowledged, MeterSelectorStatus.Resolved, paReading, 2),
                Snap(low, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Unacknowledged, MeterSelectorStatus.Resolved,
                    MeterObservation.Measured(SupplyA6300, 11.9f, 5, 1000, DateTime.UtcNow, 1, null), 2, resolved: SupplyA6300),
            };
            Assert.Equal("2 of 2 alarms active: High PA temperature 61 degrees C; Low supply voltage before the fuse 11.90 volts.",
                AlarmPhrasing.ActiveSummary(both, connected: true, AlarmStoreState.Loaded, ""));
        }

        [Fact]
        public void No_assembled_sentence_looks_like_a_key()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true };
            var last = MeterObservation.Measured(Pa, 61f, 5, 1000, DateTime.UtcNow, 1, null);
            foreach (AlarmEventKind kind in Enum.GetValues(typeof(AlarmEventKind)))
            {
                string words = AlarmPhrasing.EventInWords(Ev(def, Pa, 61f, tx: false, kind: kind, change: 1f, interval: 90, age: 3));
                Assert.False(Lexicon.LooksLikeKey(words), kind + " rendered as a key: " + words);
                Assert.DoesNotContain("alarms.", words);
            }
        }
    }
}
