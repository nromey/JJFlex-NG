#nullable enable
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The meter picker chooses by category (ruled 2026-08-21): every meter
    /// the radio publishes lands in one of the six categories the register
    /// entry names, or in Other rather than in a wrong one.
    /// </summary>
    /// <remarks>
    /// The mapping is a DRAFT judgement; this pins it so a change to it is a
    /// visible decision. Names are the ones a FLEX-8600 and a FLEX-6300
    /// publish (<see cref="MeterInventory.Find"/> carries the census).
    /// </remarks>
    public class MeterCategoryTests
    {
        [Theory]
        // Power and SWR, wherever they come from
        [InlineData("TX-", "FWDPWR", "watts", MeterCategory.PowerAndSwr)]
        [InlineData("TX-", "REFPWR", "watts", MeterCategory.PowerAndSwr)]
        [InlineData("TX-", "SWR", "to 1", MeterCategory.PowerAndSwr)]
        [InlineData("RAD", "PAEFF", "percent", MeterCategory.PowerAndSwr)]
        // Heat, voltage and current
        [InlineData("RAD", "PATEMP", "degrees C", MeterCategory.TemperatureAndVoltage)]
        [InlineData("RAD", "+13.8A", "volts", MeterCategory.TemperatureAndVoltage)]
        [InlineData("RAD", "+13.8B", "volts", MeterCategory.TemperatureAndVoltage)]
        [InlineData("RAD", "MAINFAN", "", MeterCategory.TemperatureAndVoltage)]
        // A slice's meters are receive
        [InlineData("SLC", "LEVEL", "dBm", MeterCategory.Receive)]
        [InlineData("SLC", "AGC+", "dB", MeterCategory.Receive)]
        [InlineData("", "LEVEL", "dBm", MeterCategory.Receive)]
        // The transmit chain's drive and gain control
        [InlineData("TX-", "ALC", "", MeterCategory.Transmit)]
        [InlineData("RAD", "HWALC", "", MeterCategory.Transmit)]
        [InlineData("TX-", "TX_AGC", "dB", MeterCategory.Transmit)]
        [InlineData("TX-", "TXAGC", "dB", MeterCategory.Transmit)]
        [InlineData("TX-", "RM_TX_AGC", "dB", MeterCategory.Transmit)]
        [InlineData("TX-", "ATTN_FPGA", "dB", MeterCategory.Transmit)]
        // Microphone and transmit audio levels
        [InlineData("COD-", "MIC", "dBFS", MeterCategory.Audio)]
        [InlineData("COD-", "MICPEAK", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "COMPPEAK", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "SC_MIC", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "CODEC", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "SC_FILT_1", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "AFTEREQ", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "AFRAMP", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "B4RAMP", "dBFS", MeterCategory.Audio)]
        [InlineData("TX-", "POST_P", "dBFS", MeterCategory.Audio)]
        // An amplifier's or tuner's meters are the amplifier's, whatever they measure
        [InlineData("AMP", "FWD", "watts", MeterCategory.Amplifier)]
        [InlineData("AMP", "TEMP", "degrees C", MeterCategory.Amplifier)]
        // By units, for a name nobody listed
        [InlineData("RAD", "NEWPWR", "watts", MeterCategory.PowerAndSwr)]
        [InlineData("RAD", "NEWTEMP", "degrees F", MeterCategory.TemperatureAndVoltage)]
        [InlineData("RAD", "NEWLEVEL", "dBFS", MeterCategory.Audio)]
        // By source, for a name and units nobody listed
        [InlineData("TX-", "NEWTHING", "dB", MeterCategory.Transmit)]
        [InlineData("RX-", "NEWTHING", "dB", MeterCategory.Receive)]
        // Nothing to go on: Other, not a guess
        [InlineData("RAD", "NEWTHING", "", MeterCategory.Other)]
        [InlineData(null, null, null, MeterCategory.Other)]
        public void Each_meter_lands_in_its_category(string? source, string? name, string? units,
                                                     MeterCategory expected)
        {
            Assert.Equal(expected, MeterCategories.Of(source, name, units));
        }

        [Fact]
        public void The_picker_offers_the_six_named_categories_in_the_entrys_order_and_Other_last()
        {
            Assert.Equal(new[]
            {
                MeterCategory.Transmit, MeterCategory.Receive, MeterCategory.PowerAndSwr,
                MeterCategory.TemperatureAndVoltage, MeterCategory.Amplifier, MeterCategory.Audio,
                MeterCategory.Other,
            }, MeterCategories.InOrder);
        }
    }
}
