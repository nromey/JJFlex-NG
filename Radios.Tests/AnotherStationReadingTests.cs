#nullable enable

using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// When the radio clearly says another station holds its transmitter
    /// (#911): a transmitting client that is not this one AND an interlock
    /// reporting a transmission. Either alone is not enough, and nothing
    /// unread ever counts: unknown is never a reason to refuse a key.
    /// </summary>
    public sealed class AnotherStationReadingTests
    {
        private static TuneReading Reading(bool transmitting, bool busy, bool anotherClient) =>
            new(transmitting, busy, null, false, InterlockReason.None, PTTSource.None, anotherClient);

        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(false, true, true, true)]     // PTT requested by them, not yet transmitting
        [InlineData(true, false, true, true)]
        [InlineData(false, false, true, false)]   // a client handle left over from a transmission that ended
        [InlineData(true, true, false, false)]    // the transmission is ours, or nobody named
        [InlineData(false, false, false, false)]
        public void Another_station_is_transmitting_only_when_the_radio_says_both(
            bool transmitting, bool busy, bool another, bool expected)
        {
            Assert.Equal(expected, Reading(transmitting, busy, another).AnotherStationTransmitting);
        }

        [Fact]
        public void No_radio_to_ask_is_not_another_station()
        {
            Assert.False(TuneReading.Unknown.AnotherStationTransmitting);
        }
    }
}
