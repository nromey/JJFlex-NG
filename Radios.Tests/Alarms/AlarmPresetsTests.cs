using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The shipped presets are discovered per connection, offered unarmed, and
    /// named the way a ham says them, with each radio's own measurement point.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AlarmPresetsTests
    {
        private static MeterDescriptor D(int index, string name, string description, string source, int sourceIndex, MeterUnits units, double low, double high)
            => new MeterDescriptor(index, name, description, source, sourceIndex, units, low, high);

        /// <summary>Don's 6300 census of 2026-09-06, the three rows that matter, with his indices.</summary>
        private static readonly IReadOnlyList<MeterDescriptor> Don6300 = new[]
        {
            D(9, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120),
            D(208, "+13.8A", "Main radio input voltage before fuse", "RAD", 208, MeterUnits.Volts, 10.5, 15),
            D(210, "+13.8B", "Main radio input voltage after fuse", "RAD", 210, MeterUnits.Volts, 10.5, 15),
            D(1, "FWDPWR", "RF Power Forward", "TX-", 4, MeterUnits.Dbm, -30, 50),
        };

        private static int _ids;
        private static string NewId() => "id" + (++_ids);

        [Fact]
        public void All_six_presets_are_offered_against_the_6300_census_and_say_where_on_the_supply_they_watch()
        {
            var offers = AlarmPresets.Offer(Don6300, "0000", NewId);
            Assert.Equal(3 + 3 + 3, offers.Count);
            Assert.All(offers, o => Assert.True(o.IsAvailable));
            Assert.All(offers, o => Assert.False(o.Definition!.Enabled));

            // Ruled by Noel 2026-09-22 (#566): named the way a ham says them,
            // because the name is what every sentence about the alarm inherits.
            Assert.Equal("High PA temperature", offers.Single(o => o.Key == AlarmPresets.PaTemperature).Definition!.Name);
            Assert.Equal("PA temperature rise", offers.Single(o => o.Key == AlarmPresets.PaRiseFromBaseline).Definition!.Name);
            Assert.Equal("PA temperature rising fast", offers.Single(o => o.Key == AlarmPresets.PaRisingFast).Definition!.Name);

            var lowA = offers.Single(o => o.Key == AlarmPresets.VoltageLow && o.Meter.Name == "+13.8A");
            Assert.Equal("Low supply voltage, before the fuse", lowA.Definition!.Name);
            Assert.Equal(208, lowA.Definition.Selector.SourceIndex);
            Assert.Equal("High supply voltage, before the fuse",
                offers.Single(o => o.Key == AlarmPresets.VoltageHigh && o.Meter.Name == "+13.8A").Definition!.Name);
            Assert.Equal("Supply voltage fall, before the fuse",
                offers.Single(o => o.Key == AlarmPresets.VoltageDrop && o.Meter.Name == "+13.8A").Definition!.Name);

            Assert.Equal("Low supply voltage, after the fuse",
                offers.Single(o => o.Key == AlarmPresets.VoltageLow && o.Meter.Name == "+13.8B").Definition!.Name);
            Assert.Equal("High supply voltage, after the fuse",
                offers.Single(o => o.Key == AlarmPresets.VoltageHigh && o.Meter.Name == "+13.8B").Definition!.Name);
            Assert.Equal("Supply voltage fall, after the fuse",
                offers.Single(o => o.Key == AlarmPresets.VoltageDrop && o.Meter.Name == "+13.8B").Definition!.Name);
        }

        [Fact]
        public void The_pa_preset_carries_the_ruled_numbers()
        {
            var pa = AlarmPresets.Offer(Don6300, "0000", NewId).Single(o => o.Key == AlarmPresets.PaTemperature).Definition!;
            Assert.Equal(AlarmDirection.AtOrAbove, pa.Direction);
            Assert.Equal(60, pa.Threshold);
            Assert.Equal(2, pa.Hysteresis);
            Assert.Equal(58, pa.ClearBoundary);
            Assert.Equal(AlarmPersistence.FirstFreshSample, pa.Persistence);
            Assert.Equal(5, pa.FreshnessAllowanceSeconds);
            Assert.Equal(30, pa.ReminderIntervalSeconds);
            Assert.Equal(2, pa.WorseningStep);
            Assert.Equal(2, pa.ClearSamples);
            Assert.Equal(2, pa.ClearSeconds);
            Assert.Equal(AlarmScope.WheneverConnected, pa.Scope);
            Assert.Equal(AlarmActionClass.StopTransmit, pa.Action);
            Assert.Empty(pa.Validate());
        }

        [Fact]
        public void The_voltage_presets_carry_the_ruled_numbers()
        {
            var offers = AlarmPresets.Offer(Don6300, "0000", NewId);
            var low = offers.First(o => o.Key == AlarmPresets.VoltageLow).Definition!;
            var high = offers.First(o => o.Key == AlarmPresets.VoltageHigh).Definition!;
            var drop = offers.First(o => o.Key == AlarmPresets.VoltageDrop).Definition!;
            Assert.Equal(12.0, low.Threshold); Assert.Equal(AlarmDirection.AtOrBelow, low.Direction); Assert.Equal(0.2, low.Hysteresis);
            Assert.Equal(15.0, high.Threshold); Assert.Equal(AlarmDirection.AtOrAbove, high.Direction);
            Assert.Equal(0.5, drop.Threshold); Assert.Equal(0.1, drop.Hysteresis); Assert.Equal(AlarmCondition.RiseFromBaseline, drop.Condition);
            Assert.Equal(AlarmDirection.AtOrBelow, drop.Direction);
            Assert.Equal(0.2, low.WorseningStep);
            Assert.All(new[] { low, high, drop }, d => Assert.Empty(d.Validate()));
        }

        [Fact]
        public void A_radio_without_a_supply_meter_gets_only_the_pa_family_and_no_substitute()
        {
            var inventory = new[]
            {
                D(9, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120),
                D(3, "HWALC", "External ALC", "TX-", 4, MeterUnits.Dbfs, -50, 0),
            };
            var offers = AlarmPresets.Offer(inventory, "0000", NewId);
            Assert.Equal(3, offers.Count);
            Assert.All(offers, o => Assert.StartsWith("alarms.preset.pa", o.Key));
        }

        [Fact]
        public void Two_patemp_meters_make_the_pa_presets_unavailable_with_the_reason_and_nothing_chosen()
        {
            var inventory = new[]
            {
                D(9, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120),
                D(40, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120),
            };
            var offers = AlarmPresets.Offer(inventory, "0000", NewId);
            Assert.Equal(3, offers.Count);
            Assert.All(offers, o => Assert.False(o.IsAvailable));
            Assert.All(offers, o => Assert.Equal("alarms.preset.problem.ambiguous", o.Problem));
        }

        [Fact]
        public void The_8600_pair_keeps_its_own_measurement_points_and_indices()
        {
            var inventory = new[]
            {
                D(11, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120),
                D(2, "+13.8A", "+13.8V at PA", "RAD", 2, MeterUnits.Volts, 10.5, 15),
                D(3, "+13.8B", "+13.8V at CPU", "RAD", 3, MeterUnits.Volts, 10.5, 15),
            };
            var offers = AlarmPresets.Offer(inventory, "0000", NewId);

            // The 8600 publishes the same two meter NAMES as the 6300 and means
            // different places by them, so the fuse must not follow them here.
            var lowA = offers.Single(o => o.Key == AlarmPresets.VoltageLow && o.Meter.Name == "+13.8A");
            Assert.Equal("Low supply voltage, at the PA", lowA.Definition!.Name);
            var dropB = offers.Single(o => o.Key == AlarmPresets.VoltageDrop && o.Meter.Name == "+13.8B");
            Assert.Equal("Supply voltage fall, at the CPU", dropB.Definition!.Name);
            Assert.DoesNotContain("fuse", dropB.Definition.Name);
            Assert.Equal(3, dropB.Definition.Selector.SourceIndex);
        }

        /// <summary>
        /// A name is not an identity. A definition already saved under an older
        /// preset name keeps the name it was stored with — nothing migrates
        /// names, and nothing is keyed on one.
        /// </summary>
        [Fact]
        public void A_definition_saved_under_an_older_preset_name_keeps_its_stored_name()
        {
            var stored = AlarmPresets.Build(AlarmPresets.PaTemperature, Don6300[0], "0000", "x")
                with { Name = "PA temperature high" };
            Assert.Equal("PA temperature high", stored.Name);
            Assert.Equal(AlarmPresets.PaTemperature, stored.PresetKey);
            Assert.Equal("High PA temperature", AlarmPresets.PresetName(AlarmPresets.PaTemperature, Don6300[0]));
        }

        [Fact]
        public void Applying_a_preset_again_on_another_radio_takes_that_radio_identity()
        {
            var pa8600 = AlarmPresets.Build(AlarmPresets.PaTemperature,
                D(11, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120), "8600", "x");
            var pa6300 = AlarmPresets.Build(AlarmPresets.PaTemperature, Don6300[0], "6300", "y");
            Assert.Equal(pa8600.Threshold, pa6300.Threshold);
            Assert.Equal("8600", pa8600.RadioSerial);
            Assert.Equal("6300", pa6300.RadioSerial);
            // Identity is the selector, and the selector carries no numeric index.
            Assert.Equal(pa8600.Selector, pa6300.Selector);
        }

        [Fact]
        public void Preset_names_come_from_the_lexicon_not_from_code()
        {
            var pa = AlarmPresets.Build(AlarmPresets.PaTemperature, Don6300[0], "0000", "x");
            Assert.Equal(Lexicon.Get("alarms.preset.pa_temperature.name"), pa.Name);
            Assert.False(Lexicon.LooksLikeKey(pa.Name));
        }
    }
}
