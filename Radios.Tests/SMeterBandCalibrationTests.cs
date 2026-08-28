using System;
using Radios;
using Radios.SignalCapture;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The band split as the operator meets it (#296): the same readings,
    /// captured on 20 metres and on 2 metres, must not produce the same
    /// S-units — and the report must SAY which scale it used.
    /// </summary>
    /// <remarks>
    /// <see cref="SMeterFromDbmTests"/> checks the arithmetic. This checks
    /// that the arithmetic is actually reached: the capture carries a
    /// frequency, the report picks its scale from it, and a capture whose
    /// frequency could not be read says so instead of quietly assuming HF.
    /// That silent assumption is the defect, not the arithmetic.
    /// </remarks>
    public sealed class SMeterBandCalibrationTests
    {
        private static readonly DateTime T0 =
            new(2026, 8, 27, 19, 0, 0, DateTimeKind.Utc);

        private static QsoSignalCaptureRecord Capture(ulong frequencyHz, double dbm)
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            for (int i = 0; i < 300; i++)
            {
                now = T0.AddSeconds(i * 0.1);
                session.Add(dbm, transmitting: false);
            }
            now = T0.AddSeconds(30);
            session.FrequencyHz = frequencyHz;
            session.FrequencyText = frequencyHz > 0 ? "test" : "";
            return session.Stop("stopped by you").Record;
        }

        [Fact]
        public void TheSameSignalReportsDifferentSUnitsOnHfAndOnTwoMetres()
        {
            // -93 dBm is exactly S9 above 30 MHz and a middling S5 below it.
            // Before the split every VHF reading used the HF number.
            Assert.Equal("S5", Capture(14_074_000UL, -93.0).PeakDisplay);
            Assert.Equal("S9", Capture(144_174_000UL, -93.0).PeakDisplay);
        }

        [Fact]
        public void SixMetresUsesTheAboveThirtyScale()
        {
            // The band that made this current before the 8000 series: the
            // 6000s are HF plus 6 m, and 6 m is above 30 MHz.
            Assert.Equal(SMeterReading.Band.VhfAndAbove,
                         SMeterReading.BandFor(50_313_000UL));
            Assert.Equal("S9", Capture(50_313_000UL, -93.0).PeakDisplay);
        }

        [Fact]
        public void TheReportStatesTheScaleItUsed()
        {
            // The report is self-describing on purpose (#217): a reader who
            // distrusts us entirely can check the arithmetic. A stored capture
            // taken before this change states its own older calibration, so
            // the discrepancy explains itself rather than looking like drift.
            string hf = Capture(14_074_000UL, -93.0).ReportText;
            Assert.Contains("S9 at minus 73 dBm", hf);
            Assert.Contains("S0 at minus 127 dBm", hf);
            Assert.Contains("below-30-MHz reference", hf);

            string vhf = Capture(144_174_000UL, -93.0).ReportText;
            Assert.Contains("S9 at minus 93 dBm", vhf);
            Assert.Contains("S0 at minus 147 dBm", vhf);
            Assert.Contains("above-30-MHz reference", vhf);
        }

        [Fact]
        public void AnUnreadableFrequencyIsNamedRatherThanAssumedAway()
        {
            // Empty means could-not-be-read, the record's own convention. The
            // HF scale still has to be used — an answer is required — but the
            // assumption is stated, because an unstated assumption is the
            // entire defect this task existed to end.
            string report = Capture(0UL, -93.0).ReportText;
            Assert.Contains("could not be read", report);
            Assert.Contains("above 30 MHz", report);
            Assert.Equal("S5", Capture(0UL, -93.0).PeakDisplay);
        }

        [Fact]
        public void OldRecordsDeserializeWithNoFrequencyRatherThanFailing()
        {
            // FrequencyHz was added at schema 1, not by bumping the schema:
            // captures saved before it must still load, and they load with 0 —
            // which routes them into the "could not be read" wording above.
            QsoSignalCaptureRecord? old = QsoSignalCaptureRecord.FromJson(
                "{\"Schema\":1,\"CaptureId\":\"A52-5T2\",\"FrequencyText\":\"14.24 MHz\"}");
            Assert.NotNull(old);
            Assert.Equal(0UL, old!.FrequencyHz);
        }

        // ────────────────────────────────────────────────────────────────
        //  #306 — the dBm reading, spoken
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void ADbmReadingIsSpokenWithItsSignInWordsAndItsUnit()
        {
            // "minus 97 dBm", never "-97" and never a bare number. A hyphen is
            // read differently by every voice and punctuation setting, and the
            // unit is the whole reason two keys read this meter.
            Assert.Equal("S meter minus 97 dBm", SMeterReading.SpokenDbm(-97));
            Assert.Equal("S meter minus 1 dBm", SMeterReading.SpokenDbm(-1));
            Assert.Equal("S meter 0 dBm", SMeterReading.SpokenDbm(0));
            Assert.Equal("S meter 12 dBm", SMeterReading.SpokenDbm(12));
        }

        [Theory]
        [InlineData(-97)]
        [InlineData(-127)]
        [InlineData(0)]
        [InlineData(5)]
        public void ADbmReadingNeverSpeaksAHyphenAndNeverOmitsTheUnit(int dbm)
        {
            string spoken = SMeterReading.SpokenDbm(dbm);
            Assert.DoesNotContain("-", spoken);
            Assert.EndsWith("dBm", spoken);
        }
    }
}
