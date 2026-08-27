using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// <see cref="SMeterReading.FromDbm"/> — the dBm-to-S-unit calibration,
    /// extracted from the FlexBase.SMeter getter for the QSO signal analyzer
    /// (#271) so the live readout and the analysis share one arithmetic.
    /// </summary>
    /// <remarks>
    /// These values ARE the app's calibration, stated as numbers a reader can
    /// check: S0 at -124 dBm, 6 dB per S-unit, S9 at -70 dBm, dB-over-S9-plus-9
    /// above that. If a change here is intentional, the "How these numbers
    /// were taken" section of the capture report states the same figures and
    /// must move with it.
    /// </remarks>
    public sealed class SMeterFromDbmTests
    {
        [Theory]
        [InlineData(-124.0, 0)]  // S0
        [InlineData(-130.0, 0)]  // below S0 clamps, never negative
        [InlineData(-100.0, 4)]  // S4
        [InlineData(-79.0, 7)]   // S7
        [InlineData(-76.0, 8)]   // S8
        [InlineData(-70.0, 9)]   // S9 on this app's calibration
        [InlineData(-60.0, 19)]  // 10 dB over S9, returned as 10 + 9
        [InlineData(-40.0, 39)]  // 30 dB over S9
        public void CalibrationMatchesTheLiveSMeterGetter(double dbm, int expected)
        {
            Assert.Equal(expected, SMeterReading.FromDbm(dbm));
        }

        [Fact]
        public void TruncationMatchesTheLivePathNotRounding()
        {
            // The live path stores (int)data — truncation toward zero — before
            // converting. -70.9 dBm therefore reads as -70, which is S9. If
            // this ever fails because FromDbm learned to round, the analyzer
            // and the operator's Ctrl+S no longer agree by one.
            Assert.Equal(9, SMeterReading.FromDbm(-70.9));
            Assert.Equal(SMeterReading.FromDbm(-76.0), SMeterReading.FromDbm(-76.9));
        }

        [Fact]
        public void TheOverS9ExcessIsAlreadyDecibels()
        {
            // The class remarks' trap, checked through the new entry point: a
            // reading of 19 means ten decibels over S9, and the excess must
            // come back AS IS.
            int reading = SMeterReading.FromDbm(-60.0);
            Assert.True(SMeterReading.IsOverS9(reading));
            Assert.Equal(10, SMeterReading.ExcessOverS9(reading));
        }
    }
}
