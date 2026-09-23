using System;
using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>The owner's layout record: what case 2 reads back, because
    /// the radio cannot be asked for a saved profile's contents.</summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class StationLayoutRecordTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(StationLayoutRecordTests));
        public void Dispose() => _scope.Dispose();

        [Fact]
        public void ALayoutRoundTripsThroughThePerRadioConfig()
        {
            const string serial = "2222-3333-4444-5555";
            Assert.Null(RadioConfig.StationLayoutOf(serial));

            RadioConfig.RecordStationLayout(serial, new StationLayout
            {
                Slices = { new SliceLayoutEntry(14_250_000, "USB"), new SliceLayoutEntry(7_150_000, "LSB") },
                ProfileName = "K5NER",
            });

            var back = RadioConfig.StationLayoutOf(serial);
            Assert.NotNull(back);
            Assert.Equal(2, back.Slices.Count);
            Assert.Equal(14_250_000, back.Slices[0].FreqHz);
            Assert.Equal("LSB", back.Slices[1].Mode);
            Assert.Equal("K5NER", back.ProfileName);
            Assert.True(back.RecordedUtc > DateTime.UtcNow.AddMinutes(-5));
        }

        [Fact]
        public void AnEmptyLayoutIsNeverRecorded_AndAMalformedSerialIsRefused()
        {
            RadioConfig.RecordStationLayout("2222-3333-4444-6666", new StationLayout());
            Assert.Null(RadioConfig.StationLayoutOf("2222-3333-4444-6666"));
            RadioConfig.RecordStationLayout("not-a-serial", new StationLayout { Slices = { new SliceLayoutEntry(1, "AM") } });
            Assert.Null(RadioConfig.StationLayoutOf("not-a-serial"));
        }
    }
}
