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

        /// <summary>
        /// Mute and the slice letter survive the trip to disk and back (#762).
        /// </summary>
        /// <remarks>
        /// <b>This guards a dropper, not a feature.</b> Until 2026-10-18
        /// <c>RecordStationLayout</c> rebuilt every entry as
        /// <c>new SliceLayoutEntry(s.FreqHz, s.Mode)</c>, so a field populated
        /// at the flush was discarded on the way to the config file while the
        /// trace line printed it correctly. The build passed, the log looked
        /// right, and the record was wrong. If somebody shortens that
        /// projection again, this test is what notices.
        /// </remarks>
        [Fact]
        public void MuteAndTheSliceLetterSurviveTheRoundTrip()
        {
            const string serial = "2222-3333-4444-7777";
            RadioConfig.RecordStationLayout(serial, new StationLayout
            {
                Slices =
                {
                    new SliceLayoutEntry(21_195_000, "USB", "A", SliceMuteState.Muted),
                    new SliceLayoutEntry(7_175_000, "LSB", "B", SliceMuteState.Unmuted),
                },
                ProfileName = "JJRadioDefault",
            });

            var back = RadioConfig.StationLayoutOf(serial);
            Assert.NotNull(back);
            Assert.Equal(2, back.Slices.Count);

            Assert.Equal("A", back.Slices[0].Letter);
            Assert.Equal(SliceMuteState.Muted, back.Slices[0].Mute);
            Assert.Equal("B", back.Slices[1].Letter);
            Assert.Equal(SliceMuteState.Unmuted, back.Slices[1].Mute);

            // And it reads the way an operator would say it, because the whole
            // point of #762 is that a person can match a recorded line against
            // "A and C are muted".
            Assert.Contains("A 21195000 Hz USB muted", back.ToString());
            Assert.Contains("B 7175000 Hz LSB unmuted", back.ToString());
        }

        /// <summary>
        /// A change to mute ALONE is written (#762).
        /// </summary>
        /// <remarks>
        /// The second dropper. <c>RecordStationLayout</c> skips the disk write
        /// when the layout is unchanged, and that comparison tested frequency
        /// and mode only — so muting a slice and changing nothing else
        /// compared equal and was never recorded. A test that only writes once
        /// cannot see this: the bug lives in the SECOND write.
        /// </remarks>
        [Fact]
        public void AMuteOnlyChangeIsStillWritten()
        {
            const string serial = "2222-3333-4444-8888";

            RadioConfig.RecordStationLayout(serial, new StationLayout
            {
                Slices = { new SliceLayoutEntry(14_100_000, "USB", "A", SliceMuteState.Unmuted) },
                ProfileName = "JJRadioDefault",
            });
            Assert.Equal(SliceMuteState.Unmuted, RadioConfig.StationLayoutOf(serial).Slices[0].Mute);

            // Same frequency, same mode, same profile, same letter. Only mute moves.
            RadioConfig.RecordStationLayout(serial, new StationLayout
            {
                Slices = { new SliceLayoutEntry(14_100_000, "USB", "A", SliceMuteState.Muted) },
                ProfileName = "JJRadioDefault",
            });

            Assert.Equal(SliceMuteState.Muted, RadioConfig.StationLayoutOf(serial).Slices[0].Mute);
        }

        /// <summary>
        /// A layout written before mute was captured reads as NOT RECORDED,
        /// never as unmuted (#762).
        /// </summary>
        /// <remarks>
        /// <c>SliceMuteState.NotRecorded</c> is deliberately zero so that an
        /// older config file, and the two-argument constructor the rest of the
        /// code still uses, both deserialise to "we never looked". A plain
        /// <c>bool</c> would make every historic record assert that every slice
        /// was audible — a claim nobody made — and #499's put-back would act on
        /// it and restore a station with the wrong slices live. Same rule the
        /// licence ladder settled on the same day: silence is not a no, and the
        /// distinction has to survive into storage or it is lost at the first
        /// restart.
        /// </remarks>
        [Fact]
        public void ALayoutFromBeforeMuteWasCapturedDoesNotClaimTheSliceWasAudible()
        {
            const string serial = "2222-3333-4444-9999";
            RadioConfig.RecordStationLayout(serial, new StationLayout
            {
                Slices = { new SliceLayoutEntry(10_120_000, "DIGU") },
                ProfileName = "SO2RDefault",
            });

            var back = RadioConfig.StationLayoutOf(serial);
            Assert.Equal(SliceMuteState.NotRecorded, back.Slices[0].Mute);
            Assert.NotEqual(SliceMuteState.Unmuted, back.Slices[0].Mute);
            Assert.Contains("mute not recorded", back.ToString());

            // Zero is load-bearing: it is what an absent XML element reads as.
            Assert.Equal(0, (int)SliceMuteState.NotRecorded);
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
