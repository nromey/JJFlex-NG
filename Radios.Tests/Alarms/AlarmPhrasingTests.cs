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
        private static readonly MeterDescriptor SupplyA6300 = new MeterDescriptor(208, "+13.8A", "Main radio input voltage before fuse", "RAD", 208, MeterUnits.Volts, 10.5, 15);
        private static readonly MeterDescriptor SupplyB8600 = new MeterDescriptor(3, "+13.8B", "+13.8V at CPU", "RAD", 3, MeterUnits.Volts, 10.5, 15);
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
        public void The_voltage_presets_name_the_radio_own_measurement_point()
        {
            var lowDon = AlarmPresets.Build(AlarmPresets.VoltageLow, SupplyA6300, Serial, "lo");
            Assert.Equal("Supply voltage Main radio input voltage before fuse is 11.90 volts. Release transmit and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(lowDon, SupplyA6300, 11.9f, tx: true)));

            var drop8600 = AlarmPresets.Build(AlarmPresets.VoltageDrop, SupplyB8600, Serial, "drop");
            Assert.Equal("Supply voltage +13.8V at CPU fell 0.52 volts from the baseline, now 13.46 volts. Stay in receive and have the supply path checked.",
                AlarmPhrasing.Warning(Ev(drop8600, SupplyB8600, 13.46f, tx: false, change: -0.52f)));

            var high = AlarmPresets.Build(AlarmPresets.VoltageHigh, SupplyA6300, Serial, "hi");
            Assert.Contains("is 15.00 volts", AlarmPhrasing.Warning(Ev(high, SupplyA6300, 15.0f, tx: false)));
            Assert.DoesNotContain("fuse socket", AlarmPhrasing.Warning(Ev(high, SupplyA6300, 15.0f, tx: false)));
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
            Assert.Equal("PA temperature high: no PATEMP (PA Temperature) reading for 6 seconds. It cannot be watched. Stop the transmission.", tx);
            string rx = AlarmPhrasing.DataLost(Ev(def, Pa, float.NaN, tx: false, kind: AlarmEventKind.DataStale, age: 12));
            Assert.Equal("PA temperature high: no PATEMP (PA Temperature) reading for 12 seconds. It cannot be watched.", rx);
            Assert.DoesNotContain("clear", tx + rx, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Cleared_is_a_state_update_with_the_value_and_no_permission()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            string s = AlarmPhrasing.Cleared(Ev(def, Pa, 57.5f, tx: false, kind: AlarmEventKind.Cleared));
            Assert.Equal("PA temperature high cleared. PATEMP (PA Temperature) 57.5 degrees C.", s);
            Assert.DoesNotContain("transmit", s, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_preview_is_labelled_a_test_and_carries_the_real_sentence_after_it()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa");
            var e = Ev(def, Pa, 60f, tx: false) with { Detail = "preview" };
            string s = AlarmPhrasing.Preview(e, AlarmPhrasing.Warning(e));
            Assert.Equal("Test warning from the alarm PA temperature high. Nothing is wrong. A real warning would say: "
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
            AlarmBaselineState baseline = AlarmBaselineState.NotApplicable, float baselineValue = float.NaN, double snooze = 0)
            => new AlarmSnapshot(def, data, cond, note, baseline, baselineValue, res,
                res == MeterSelectorStatus.Resolved ? Pa : null, last, age, "ep", snooze, null, false, float.NaN);

        [Fact]
        public void A_list_row_says_the_state_in_words()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true };
            var last = MeterObservation.Measured(Pa, 61f, 5, 1000, DateTime.UtcNow, 1, null);

            Assert.Equal("PA temperature high. PATEMP (PA Temperature). at or above 60 degrees C. active, acknowledged",
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
            string s = AlarmPhrasing.Status(Snap(def, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Acknowledged, MeterSelectorStatus.Resolved, last, 1.4));
            Assert.Equal("PA temperature high: enabled, watching, active. acknowledged. Latest reading 61.3 degrees C, 1 seconds ago. "
                + "Line: at or above 60 degrees C. Meter PATEMP (PA Temperature), found.", s);

            string snoozed = AlarmPhrasing.Status(Snap(def, AlarmDataState.Fresh, AlarmConditionState.Active, AlarmNotificationState.Snoozed, MeterSelectorStatus.Resolved, last, 1, snooze: 42));
            Assert.Contains("snoozed. Snoozed, 42 seconds left.", snoozed);

            string unavailable = AlarmPhrasing.Status(Snap(def, AlarmDataState.Missing, AlarmConditionState.Normal, AlarmNotificationState.None, MeterSelectorStatus.Missing, null, double.NaN));
            Assert.Equal("PA temperature high: enabled, meter not available, normal. No reading yet. Line: at or above 60 degrees C. "
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
            Assert.Equal("1 of 2 alarms active: PA temperature high 61 degrees C. 1 cannot be watched right now.",
                AlarmPhrasing.ActiveSummary(all, connected: true, AlarmStoreState.Loaded, ""));
            Assert.Equal("No radio is connected, so nothing is being watched.",
                AlarmPhrasing.ActiveSummary(all, connected: false, AlarmStoreState.Loaded, ""));
            Assert.Equal("No alarms are defined for this radio.",
                AlarmPhrasing.ActiveSummary(new List<AlarmSnapshot>(), connected: true, AlarmStoreState.Empty, ""));
            Assert.StartsWith("The alarm configuration for this radio could not be read: not valid JSON",
                AlarmPhrasing.ActiveSummary(all, connected: true, AlarmStoreState.Unavailable, "not valid JSON: x"));
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
