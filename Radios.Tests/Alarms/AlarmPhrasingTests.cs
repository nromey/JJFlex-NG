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
        public void The_pa_preset_speaks_the_temperature_then_the_action_in_transmit_and_in_receive()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            Assert.Equal("PA temperature 63.5 degrees C. Release transmit now.",
                AlarmPhrasing.Warning(Ev(def, Pa, 63.5f, tx: true)));
            Assert.Equal("PA temperature 60 degrees C. Stay in receive and let the radio cool.",
                AlarmPhrasing.Warning(Ev(def, Pa, 60f, tx: false)));
            Assert.DoesNotContain("cut", AlarmPhrasing.Warning(Ev(def, Pa, 63.5f, tx: true)), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_reminder_and_a_worsening_on_the_pa_preset_repeat_the_actionable_warning()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            Assert.Equal("PA temperature 65.2 degrees C. Release transmit now.",
                AlarmPhrasing.Warning(Ev(def, Pa, 65.2f, tx: true, kind: AlarmEventKind.Worsened)));
            Assert.Equal("PA temperature 61 degrees C. Stay in receive and let the radio cool.",
                AlarmPhrasing.Warning(Ev(def, Pa, 61f, tx: false, kind: AlarmEventKind.Reminder)));
        }

        [Fact]
        public void The_investigation_rise_speaks_the_change_and_the_current_temperature()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaRiseFromBaseline, Pa, Serial, "rise");
            Assert.Equal("PA temperature up 5.2 degrees C from the baseline, now 30.9 degrees C. Release transmit now.",
                AlarmPhrasing.Warning(Ev(def, Pa, 30.890625f, tx: true, change: 5.1875f)));
        }

        [Fact]
        public void The_rising_fast_trend_speaks_the_actual_interval_not_ninety()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaRisingFast, Pa, Serial, "trend");
            string s = AlarmPhrasing.Warning(Ev(def, Pa, 42.9f, tx: true, change: 12.3f, interval: 90.3));
            Assert.Equal("PA temperature rose 12.3 degrees C in 90 seconds, now 42.9 degrees C. Stop transmitting and check the cooling.", s);
            string s2 = AlarmPhrasing.Warning(Ev(def, Pa, 42.9f, tx: false, change: 12.3f, interval: 86.6));
            Assert.Contains("in 87 seconds", s2);
            Assert.DoesNotContain("fan", s2, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_voltage_sentences_say_where_in_a_few_words_on_both_of_the_6300_supply_meters()
        {
            // Ruled by Noel 2026-09-22 (#566), on hearing the radio's own
            // description read out in full: "before the fuse."
            var lowA = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "lo");
            Assert.Equal("Supply voltage before fuse is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true)));
            Assert.Equal("Supply voltage before fuse is 11.90 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: false)));

            var highB = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyB6300, Serial, "hi");
            Assert.Equal("Supply voltage after fuse is 15.00 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(highB, SupplyB6300, 15.0f, tx: false)));

            var dropA = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyA6300, Serial, "drop");
            Assert.Equal("Supply voltage before fuse fell 0.52 volts from the baseline, now 13.46 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(dropA, SupplyA6300, 13.46f, tx: true, change: -0.52f)));

            // The radio's own words are gone from the sentence, and no wording
            // beyond the place-phrase crept in with them.
            string s = AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true));
            Assert.DoesNotContain("Main radio input voltage", s);
            Assert.DoesNotContain("fuse socket", s);
        }

        [Fact]
        public void The_place_phrase_has_two_lengths_and_no_comma_at_either()
        {
            // RULED by Noel 2026-09-23 04:42 (#566): "If verbosity is set to
            // terse I'd just say before and after, otherwise I'd just say
            // 'before fuse' and 'after fuse', no comma." Both lengths, both
            // radios, and the preset NAME always at the normal length because a
            // name is stored data.
            var lowA = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "lo");
            Assert.Equal("Supply voltage before is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true), tersePlace: true));
            Assert.Equal("Supply voltage before fuse is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA6300, 11.9f, tx: true), tersePlace: false));

            var highB = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyB6300, Serial, "hi");
            Assert.Equal("Supply voltage after is 15.00 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(highB, SupplyB6300, 15.0f, tx: false), tersePlace: true));

            var lowPa = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA8600, Serial, "lo");
            Assert.Equal("Supply voltage PA is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowPa, SupplyA8600, 11.9f, tx: true), tersePlace: true));
            var dropCpu = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyB8600, Serial, "drop");
            Assert.Equal("Supply voltage CPU fell 0.52 volts from the baseline, now 13.46 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(dropCpu, SupplyB8600, 13.46f, tx: false, change: -0.52f), tersePlace: true));

            // No comma anywhere in the four phrases at either length, so no
            // frame that leads with a name can run into its verb.
            foreach (var m in new[] { SupplyA6300, SupplyB6300, SupplyA8600, SupplyB8600 })
            {
                Assert.DoesNotContain(",", AlarmPhrasing.MeasurementPoint(m));
                Assert.DoesNotContain(",", AlarmPhrasing.MeasurementPoint(m, terse: true));
                Assert.DoesNotContain(",", AlarmPresets.PresetName(AlarmPresets.VoltageLow, m));
            }
            Assert.Equal("Low supply voltage before fuse", AlarmPresets.PresetName(AlarmPresets.VoltageLow, SupplyA6300));
            Assert.Equal("Delete the Low supply voltage before fuse alarm? There is no undo.",
                Lexicon.Get("alarms.dialog.delete_confirm", ("alarm", AlarmPresets.PresetName(AlarmPresets.VoltageLow, SupplyA6300))));

            // An unmet meter is named by its own meter name, at either length.
            Assert.Equal("+13.8C", AlarmPhrasing.MeasurementPoint(SupplyUnmet));
            Assert.Equal("+13.8C", AlarmPhrasing.MeasurementPoint(SupplyUnmet, terse: true));
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
            Assert.Equal("High PA temperature: +13.8A (+13.8V at PA) is 11.90 volts, at or below 12.00. Release transmit now.", s);
            Assert.DoesNotContain("degrees", s);

            // Same preset, action changed to notify-only: no instruction at all.
            var notify = pa with { Action = AlarmActionClass.NotifyOnly };
            Assert.False(AlarmPresets.WordingApplies(notify));
            string n = AlarmPhrasing.Warning(Ev(notify, Pa, 63.5f, tx: true));
            Assert.Equal("High PA temperature: PATEMP (PA Temperature) is 63.5 degrees C, at or above 60.", n);
            Assert.DoesNotContain("Release transmit", n);

            // Same preset, condition changed to a trend: the generic trend sentence, with the change it measured.
            var trend = pa with { Condition = AlarmCondition.RisingFast, Threshold = 12, TrendResetRise = 10 };
            Assert.False(AlarmPresets.WordingApplies(trend));
            Assert.StartsWith("High PA temperature: PATEMP (PA Temperature) rose 12.3 degrees C in 90 seconds, now 42.9 degrees C.",
                AlarmPhrasing.Warning(Ev(trend, Pa, 42.9f, tx: false, change: 12.3f, interval: 90.3)));

            // Positive control: an untouched preset, and one whose edits leave
            // the words true (a different line, a different margin), keep them.
            Assert.True(AlarmPresets.WordingApplies(pa with { Threshold = 65, Hysteresis = 3, ReminderIntervalSeconds = 60 }));
            Assert.Equal("PA temperature 66 degrees C. Release transmit now.",
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
            Assert.Equal("Supply voltage at PA is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowA, SupplyA8600, 11.9f, tx: true)));

            var highA = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyA8600, Serial, "hi");
            Assert.Equal("Supply voltage at PA is 15.00 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(highA, SupplyA8600, 15.0f, tx: false)));

            var dropB = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyB8600, Serial, "drop");
            Assert.Equal("Supply voltage at CPU fell 0.52 volts from the baseline, now 13.46 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(dropB, SupplyB8600, 13.46f, tx: false, change: -0.52f)));

            var lowB = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyB8600, Serial, "lob");
            Assert.Equal("Supply voltage at CPU is 13.20 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowB, SupplyB8600, 13.2f, tx: false)));
        }

        [Fact]
        public void A_supply_meter_we_have_not_met_keeps_the_radio_own_description()
        {
            var low = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyUnmet, Serial, "lo");
            Assert.Equal("Supply voltage +13.8C is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(low, SupplyUnmet, 11.9f, tx: true)));

            // No description at all: the meter's name, never an empty gap.
            var bare = SupplyUnmet with { Description = "" };
            var lowBare = AlarmPresets.Build(AlarmPresets.VoltageLow, bare, Serial, "bare");
            Assert.Equal("Supply voltage +13.8C is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowBare, bare, 11.9f, tx: true)));
        }

        [Fact]
        public void A_generic_alarm_names_itself_and_its_meter_with_a_negative_decimal_value_and_the_chosen_action()
        {
            var def = AlarmDefinition.NewLevel("g", "Drive too hot", Serial, MeterSelector.From(Alc),
                AlarmDirection.AtOrAbove, -3.5, 1) with { Action = AlarmActionClass.StopTransmit };
            Assert.Equal("Drive too hot: ALC (Signal) is -1.25 dBFS, at or above -3.5. Release transmit now.",
                AlarmPhrasing.Warning(Ev(def, Alc, -1.25f, tx: true)));

            var notify = def with { Action = AlarmActionClass.NotifyOnly, Direction = AlarmDirection.AtOrBelow, Threshold = -40 };
            Assert.Equal("Drive too hot: ALC (Signal) is -42.7 dBFS, at or below -40.",
                AlarmPhrasing.Warning(Ev(notify, Alc, -42.7f, tx: false)));
        }

        [Fact]
        public void A_unitless_meter_reads_without_a_stumble()
        {
            var def = AlarmDefinition.NewLevel("u", "Codec watch", Serial, MeterSelector.From(Unitless),
                AlarmDirection.AtOrAbove, 80, 0) with { Action = AlarmActionClass.NotifyOnly };
            string s = AlarmPhrasing.Warning(Ev(def, Unitless, 85f, tx: false));
            Assert.Equal("Codec watch: CODEC (Signal) is 85, at or above 80.", s);
            Assert.DoesNotContain("  ", s);
        }

        [Fact]
        public void An_operator_name_is_data_and_cannot_become_a_template()
        {
            var def = AlarmDefinition.NewLevel("g", "{value} {alarm} <b>", Serial, MeterSelector.From(Alc),
                AlarmDirection.AtOrAbove, 0, 0) with { Action = AlarmActionClass.NotifyOnly };
            string s = AlarmPhrasing.Warning(Ev(def, Alc, 1f, tx: false));
            Assert.StartsWith("{value} {alarm} <b>: ALC", s);
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
                + "PA temperature 60 degrees C. Stay in receive and let the radio cool.", s);
        }

        [Fact]
        public void Boundaries_read_as_inclusive_lines_in_the_meter_units()
        {
            Assert.Equal("at or above 60 degrees C", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "x")));
            Assert.Equal("at or below 12.00 volts", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "x")));
            Assert.Equal("a fall of 0.50 volts below the captured baseline", AlarmPhrasing.Boundary(AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyA6300, Serial, "x")));
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
            Assert.Equal("1 of 2 alarms active: High PA temperature 61 degrees C. 1 cannot be watched right now.",
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
            Assert.Equal("2 alarms, none active. 2 cannot judge yet.",
                AlarmPhrasing.ActiveSummary(notReady, connected: true, AlarmStoreState.Loaded, ""));

            var lastKnown = new List<AlarmSnapshot>
            {
                Snap(pa, AlarmDataState.Stale, AlarmConditionState.LastKnownActive, AlarmNotificationState.None, MeterSelectorStatus.Resolved, last, 9),
            };
            Assert.Equal("1 of 1 alarms active: High PA temperature was 61 degrees C when its reading stopped. 1 cannot be watched right now.",
                AlarmPhrasing.ActiveSummary(lastKnown, connected: true, AlarmStoreState.Loaded, ""));
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
            Assert.Equal("Low supply voltage before fuse", low.Name);
            var fall = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyB8600, Serial, "fall") with { Enabled = true };
            Assert.Equal("Supply voltage fall at CPU", fall.Name);

            var reading = MeterObservation.Measured(SupplyA6300, 12.6f, 5, 1000, DateTime.UtcNow, 1, null);

            Assert.Equal("Low supply voltage before fuse. +13.8A (Main radio input voltage before fuse). at or below 12.00 volts. active",
                AlarmPhrasing.Row(Snap(low, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Unacknowledged,
                    MeterSelectorStatus.Resolved, reading, 2, resolved: SupplyA6300)));

            Assert.Equal("Low supply voltage before fuse cleared. +13.8A (Main radio input voltage before fuse) 12.60 volts.",
                AlarmPhrasing.Cleared(Ev(low, SupplyA6300, 12.6f, tx: false, kind: AlarmEventKind.Cleared)));

            Assert.Equal("Low supply voltage before fuse: no +13.8A (Main radio input voltage before fuse) reading for 6 seconds. It cannot be watched.",
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
            Assert.Equal("2 of 2 alarms active: High PA temperature 61 degrees C; Low supply voltage before fuse 11.90 volts.",
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
