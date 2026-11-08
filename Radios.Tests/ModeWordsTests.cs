using System;
using System.IO;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// How an announcement says a mode (J41, ruled by Noel 2026-11-07): only
    /// the modes a synthesizer mangles get words; the rest stay letters; the
    /// station check's fuller words come from the same place. And the direct
    /// mode keys and the Mode menu read the radio's own mode list (#994).
    /// </summary>
    public class ModeWordsTests
    {
        [Theory]
        [InlineData("DIGU", "Digital upper")]
        [InlineData("digl", "Digital lower")]
        [InlineData("SAM", "Synchronous AM")]
        [InlineData("NFM", "Narrow FM")]
        public void A_mode_a_synthesizer_mangles_is_said_in_words(string mode, string spoken)
        {
            Assert.Equal(spoken, ModeWords.Spoken(mode));
        }

        [Theory]
        [InlineData("USB")]
        [InlineData("LSB")]
        [InlineData("AM")]
        [InlineData("FM")]
        [InlineData("CW")]
        [InlineData("DSB")]
        [InlineData("RTTY")]
        [InlineData("DFM")]   // left as letters until what DFM is on Flex is verified
        public void Every_other_mode_stays_letters(string mode)
        {
            Assert.Equal(mode, ModeWords.Spoken(mode));
        }

        [Fact]
        public void The_station_checks_words_are_unchanged_and_come_from_the_same_place()
        {
            Assert.Equal("upper sideband", StationConditions.ModeInWords("USB"));
            Assert.Equal("lower sideband", StationConditions.ModeInWords("lsb"));
            Assert.Equal("digital upper sideband", StationConditions.ModeInWords("DIGU"));
            Assert.Equal("digital lower sideband", StationConditions.ModeInWords("DIGL"));
            Assert.Equal("CW", StationConditions.ModeInWords("CW"));
            Assert.Equal("", StationConditions.ModeInWords(""));
            Assert.Equal(ModeWords.InWords("DIGU"), StationConditions.ModeInWords("DIGU"));
        }

        [Fact]
        public void A_direct_key_asks_the_radios_own_list()
        {
            var noDsb = new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "SAM", "FM", "NFM", "DFM", "RTTY" };
            Assert.True(ModeCycle.Contains(ModeCycle.KnownModes(noDsb), "digu"));
            Assert.False(ModeCycle.Contains(ModeCycle.KnownModes(noDsb), "DSB"));  // a radio that lists no DSB
            Assert.True(ModeCycle.Contains(ModeCycle.KnownModes(null), "DSB"));    // no list yet: FlexLib's set
            foreach (string key in new[] { "USB", "LSB", "CW", "AM", "FM", "DIGU", "DIGL" })
                Assert.True(ModeCycle.Contains(ModeCycle.KnownModes(null), key), key + " would be refused before a radio reports");
        }

        /// <summary>
        /// Every place a mode change is announced says it through
        /// <see cref="ModeWords.Spoken"/>, and the direct keys check the radio's
        /// list before writing.
        /// </summary>
        [Fact]
        public void The_mode_announcements_and_the_direct_keys_use_the_one_source()
        {
            string main = Read("JJFlexWpf", "MainWindow.xaml.cs");
            int at = main.IndexOf("public void SetMode(string mode)", StringComparison.Ordinal);
            Assert.True(at >= 0, "SetMode is gone");
            int end = main.IndexOf("#endregion", at, StringComparison.Ordinal);
            string setMode = main.Substring(at, end - at);
            Assert.Contains("!Radios.ModeCycle.Contains(Radios.ModeCycle.KnownModes(RigControl.RadioModeList), mode)", setMode);
            Assert.Contains("\"settings.mode.not_on_radio\"", setMode);
            Assert.Contains("RigControl.Mode = mode;", setMode);
            Assert.Contains("Speak(Radios.ModeWords.Spoken(mode)", setMode);
            Assert.DoesNotContain("Speak(mode,", setMode);

            string menu = Read("JJFlexWpf", "NativeMenuBar.cs");
            Assert.Contains("(\"settings.mode.selected\", (\"m\", Radios.ModeWords.Spoken(m)))", menu);

            Assert.Contains("(\"mode\", Radios.ModeWords.Spoken(mode))", Read("JJFlexWpf", "FreqOutHandlers.cs"));
            string multi = Read("Radios", "FlexBase.MultiUser.cs");
            Assert.Contains("(\"letter\", letter), (\"mode\", ModeWords.Spoken(mode))", multi);
            Assert.Contains("(\"mode\", ModeWords.Spoken(_lastPlaceStashMode))", multi);
            Assert.Contains("(\"mode\", ModeWords.Spoken(correction.RequiredMode))", Read("Radios", "SixtyMeterModeDelivery.cs"));
        }

        private static string Read(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d!.FullName, Path.Combine(parts)));
        }
    }
}
