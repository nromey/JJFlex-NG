using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// <see cref="SMeterReading.FromDbm"/> — the dBm-to-S-unit calibration
    /// every surface shares: the live Ctrl+S readout, the meter tones, the
    /// braille status line and the QSO signal analyzer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These values ARE the app's calibration, stated as numbers a reader can
    /// check. It is IARU R.1's: 6 dB per S-unit, S9 at -73 dBm below 30 MHz
    /// and -93 dBm at or above it, S0 nine S-units under whichever applies.
    /// </para>
    /// <para>
    /// <b>Both halves of that were wrong until Sprint 37 (#296).</b> The
    /// anchor carried a hand-written 3 dB shift dating to the repository's
    /// initial import, so S9 sat at -70; and nothing branched on frequency, so
    /// the HF scale was applied on 6 m, 2 m and 70 cm as well. Each half has
    /// its own regression test below, named for the error it catches.
    /// </para>
    /// <para>
    /// <b>Both errors ran the same way and the app read LOW, not high.</b> A
    /// weaker signal is needed to call S9 on the corrected scale, so every
    /// reading rises: about half an S-unit on HF, three to four whole
    /// S-units above 30 MHz. Worth stating plainly because the intuitive
    /// reading of "our S9 is 3 dB high" is the reverse — it is the dBm ANCHOR
    /// that was high, and a high anchor produces low S-numbers.
    /// </para>
    /// </remarks>
    public sealed class SMeterFromDbmTests
    {
        [Theory]
        [InlineData(-127.0, 0)]  // S0
        [InlineData(-133.0, 0)]  // below S0 clamps, never negative
        [InlineData(-103.0, 4)]  // S4
        [InlineData(-82.0, 7)]   // S7
        [InlineData(-79.0, 8)]   // S8
        [InlineData(-73.0, 9)]   // S9
        [InlineData(-63.0, 19)]  // 10 dB over S9, returned as 10 + 9
        [InlineData(-43.0, 39)]  // 30 dB over S9
        public void HfCalibrationIsTheIaruScale(double dbm, int expected)
        {
            Assert.Equal(expected, SMeterReading.FromDbm(dbm, SMeterReading.Band.Hf));
        }

        [Theory]
        [InlineData(-147.0, 0)]  // S0, twenty decibels below the HF S0
        [InlineData(-153.0, 0)]  // clamp
        [InlineData(-123.0, 4)]  // S4
        [InlineData(-93.0, 9)]   // S9 above 30 MHz
        [InlineData(-83.0, 19)]  // 10 dB over S9
        public void VhfCalibrationIsTwentyDecibelsWeaker(double dbm, int expected)
        {
            Assert.Equal(expected, SMeterReading.FromDbm(dbm, SMeterReading.Band.VhfAndAbove));
        }

        // ────────────────────────────────────────────────────────────────
        //  The two errors #296 fixed, one test each
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheThreeDecibelShiftIsGone()
        {
            // The constant read "+ 127 - 3" — the standard, deliberately
            // shifted, with no reason written down. Under that calibration
            // S9 began at -70 dBm and -73 dBm read as S8. If this fails,
            // the anchor has moved back.
            Assert.Equal(9, SMeterReading.FromDbm(-73.0, SMeterReading.Band.Hf));
            Assert.Equal(-73, SMeterReading.S9Dbm(SMeterReading.Band.Hf));
            Assert.Equal(-127,
                SMeterReading.S9Dbm(SMeterReading.Band.Hf)
                    - (SMeterReading.TopSUnit * SMeterReading.DbPerSUnit));
        }

        [Fact]
        public void TheSameSignalReadsFourSUnitsApartAcrossTheBandSplit()
        {
            // -93 dBm is exactly S9 above 30 MHz and a middling S5 below it.
            // Before the split, every VHF and UHF reading used the HF number
            // and came out more than three S-units LOW. This is the whole
            // defect in one assertion.
            Assert.Equal(9, SMeterReading.FromDbm(-93.0, SMeterReading.Band.VhfAndAbove));
            Assert.Equal(5, SMeterReading.FromDbm(-93.0, SMeterReading.Band.Hf));
        }

        [Theory]
        [InlineData(14_074_000UL, SMeterReading.Band.Hf)]          // 20 m
        [InlineData(29_999_999UL, SMeterReading.Band.Hf)]          // last hertz below the split
        [InlineData(30_000_000UL, SMeterReading.Band.VhfAndAbove)] // the split itself
        [InlineData(50_313_000UL, SMeterReading.Band.VhfAndAbove)] // 6 m — on the 6000 series
        [InlineData(144_174_000UL, SMeterReading.Band.VhfAndAbove)] // 2 m
        [InlineData(432_100_000UL, SMeterReading.Band.VhfAndAbove)] // 70 cm
        [InlineData(0UL, SMeterReading.Band.Hf)]                   // unknown answers HF
        public void BandForSplitsAtThirtyMegahertz(ulong hz, SMeterReading.Band expected)
        {
            Assert.Equal(expected, SMeterReading.BandFor(hz));
        }

        [Fact]
        public void TheFrequencyOverloadAgreesWithTheBandOverload()
        {
            // The two entry points must never drift: one is the live readout's
            // (it has a frequency), the other the analyzer's (it has a band).
            Assert.Equal(SMeterReading.FromDbm(-93.0, SMeterReading.Band.Hf),
                         SMeterReading.FromDbm(-93.0, 14_074_000UL));
            Assert.Equal(SMeterReading.FromDbm(-93.0, SMeterReading.Band.VhfAndAbove),
                         SMeterReading.FromDbm(-93.0, 144_174_000UL));
        }

        [Fact]
        public void TruncationMatchesTheLivePathNotRounding()
        {
            // The live path stores (int)data — truncation toward zero — before
            // converting. -73.9 dBm therefore reads as -73, which is S9. If
            // this ever fails because FromDbm learned to round, the analyzer
            // and the operator's Ctrl+S no longer agree by one.
            Assert.Equal(9, SMeterReading.FromDbm(-73.9, SMeterReading.Band.Hf));
            Assert.Equal(SMeterReading.FromDbm(-79.0, SMeterReading.Band.Hf),
                         SMeterReading.FromDbm(-79.9, SMeterReading.Band.Hf));
        }

        [Fact]
        public void TheOverS9ExcessIsAlreadyDecibels()
        {
            // The class remarks' trap, checked through the new entry point: a
            // reading of 19 means ten decibels over S9, and the excess must
            // come back AS IS.
            int reading = SMeterReading.FromDbm(-63.0, SMeterReading.Band.Hf);
            Assert.True(SMeterReading.IsOverS9(reading));
            Assert.Equal(10, SMeterReading.ExcessOverS9(reading));
        }
    }
}
