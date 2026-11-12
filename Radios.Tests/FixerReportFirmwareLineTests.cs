using System;
using System.IO;
using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #438: the Fixer report no longer prints "Firmware this app expects", a
    /// vendor constant (3.3.32.8203) that read as a mismatch warning directly
    /// under the radio's real firmware. The radio's own version stays. Radio
    /// Setup's framed firmware line is untouched.
    /// </summary>
    public class FixerReportFirmwareLineTests
    {
        [Fact]
        public void The_station_lines_give_the_radios_firmware_and_not_the_vendor_constant()
        {
            using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            var lines = TxChainFacts.StationLines(rig);
            Assert.Contains(lines, l => l.StartsWith("Firmware version: ", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("this app expects", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(lines, l => l.Contains(FlexBase.LibraryExpectedFirmwareVersion, StringComparison.Ordinal)
                                              && FlexBase.LibraryExpectedFirmwareVersion.Length > 0);
        }

        [Fact]
        public void Radio_Setup_fills_the_expected_placeholder_with_the_librarys_version()
        {
            string setup = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs"));
            Assert.Contains("(\"expected\", FlexBase.LibraryExpectedFirmwareVersion)", setup);
        }
    }
}
