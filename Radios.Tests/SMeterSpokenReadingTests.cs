using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 43 Track E, #353. One composer answers "what is the meter
    /// reading?", so the Home S-meter field and Ctrl+S cannot say different
    /// things about the same measurement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was wrong.</b> Ctrl+S composed the reading properly. The Home
    /// S-meter field read its own DISPLAY back into "S meter {reading}", so in
    /// dBm it announced "S meter -97" — a hyphen-minus, spoken as "minus 97",
    /// "dash 97" or "97" depending on a punctuation setting this app does not
    /// control. A reading of "97" that means minus 97 dBm is not a degraded
    /// announcement, it is a wrong one. Above S9 the same read-back produced
    /// "S meter plus 4": four of what, over what.
    /// </para>
    /// <para>
    /// <b>Why it matters now and did not before.</b> dBm used to be a mode
    /// with no key, no Settings control and no persistence. Since #337 it
    /// persists per radio, so an operator who prefers it lives there for
    /// months and pays this on every press of Space.
    /// </para>
    /// </remarks>
    public sealed class SMeterSpokenReadingTests
    {
        private const string Power = "50 watts";

        [Fact]
        public void ReceivingInDbmNamesTheUnitAndSpellsTheSignAsAWord()
        {
            string spoken = SMeterReading.Spoken(
                transmitting: false, spokenForwardPower: Power,
                inDbm: true, rawDbm: -97, sUnits: 3);

            Assert.Equal("S meter minus 97 dBm", spoken);
            Assert.DoesNotContain("-", spoken);
        }

        [Fact]
        public void ReceivingInSUnitsBelowS9SpeaksTheSUnit()
        {
            Assert.Equal("S 5", SMeterReading.Spoken(
                transmitting: false, spokenForwardPower: Power,
                inDbm: false, rawDbm: -97, sUnits: 5));
        }

        [Fact]
        public void ReceivingAboveS9SpeaksTheExcessAgainstItsBase()
        {
            // 13 in the app's encoding is four decibels over S9 — the trap
            // documented on SMeterReading itself. The point of this assertion
            // is the words "S 9 plus": the Home field used to say "plus 4"
            // with nothing to measure it against.
            string spoken = SMeterReading.Spoken(
                transmitting: false, spokenForwardPower: Power,
                inDbm: false, rawDbm: -60, sUnits: 13);

            Assert.Equal("S 9 plus 4 dB", spoken);
        }

        [Fact]
        public void TransmittingReportsRealPowerRegardlessOfTheUnitSetting()
        {
            // Keyed, the meter is not describing anyone's signal. Both unit
            // settings must give the same honest answer, and it must carry a
            // unit — this surface once read "S meter .050" while the radio put
            // out 50 milliwatts.
            foreach (bool inDbm in new[] { true, false })
            {
                string spoken = SMeterReading.Spoken(
                    transmitting: true, spokenForwardPower: Power,
                    inDbm: inDbm, rawDbm: -97, sUnits: 5);

                Assert.Equal("Power 50 watts", spoken);
            }
        }

        [Fact]
        public void NoReadingIsEverABareNumber()
        {
            // The whole defect in one assertion: every state names either an
            // S-unit, a decibel or a watt. A number on its own is what the
            // Home field used to hand the operator.
            var states = new[]
            {
                SMeterReading.Spoken(false, Power, inDbm: true,  rawDbm: -97, sUnits: 3),
                SMeterReading.Spoken(false, Power, inDbm: true,  rawDbm: 0,   sUnits: 3),
                SMeterReading.Spoken(false, Power, inDbm: false, rawDbm: -97, sUnits: 0),
                SMeterReading.Spoken(false, Power, inDbm: false, rawDbm: -97, sUnits: 9),
                SMeterReading.Spoken(false, Power, inDbm: false, rawDbm: -60, sUnits: 15),
                SMeterReading.Spoken(true,  Power, inDbm: false, rawDbm: -97, sUnits: 3),
            };

            foreach (string spoken in states)
            {
                Assert.False(double.TryParse(spoken, out _),
                    "\"" + spoken + "\" is a bare number. An operator who cannot see the "
                    + "screen has no way to know what it counts.");
            }
        }

        /// <summary>
        /// The positive control on the whole file, and the thing that actually
        /// closes #353: the Home S-meter field must go through this composer
        /// rather than reading its own display back. Source-scanned because
        /// the handler lives in JJFlexWpf, which this project must not
        /// reference — constructing that assembly's types is what puts dialogs
        /// on the operator's desktop.
        /// </summary>
        [Fact]
        public void BothSMeterSurfacesGoThroughTheOneComposer()
        {
            string handlers = Read("JJFlexWpf/FreqOutHandlers.cs");
            string commands = Read("JJFlexWpf/KeyCommands.cs");

            Assert.Contains("SMeterReading.Spoken(", handlers, StringComparison.Ordinal);
            Assert.Contains("SMeterReading.Spoken(", commands, StringComparison.Ordinal);

            // And the read-back that caused it is gone. This key rendered the
            // DISPLAY into a sentence; nothing should reach for it again.
            Assert.DoesNotContain("settings.home.smeter_reading", handlers, StringComparison.Ordinal);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject passes every absence check it makes.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
