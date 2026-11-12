using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1025, the remainder (ruled by Noel 2026-11-11 11:55): the digital
    /// modes are said the way the plain sidebands are, "switch to digital USB"
    /// and "switch to digital LSB", beside "switch to USB" and "switch to LSB";
    /// and "upper" and "lower" stay as search words. Read from the key table's
    /// own rows. These are the SHORT ACTION LABELS, which the no-radio refusal
    /// says; the Command Finder's rows are built from each row's help text and
    /// searched by its matcher, and are tested through both in
    /// JJFlexWpf.Tests' CommandFinderDigitalRowsTests (repair one).
    /// </summary>
    public class CommandFinderDigitalLabelsTests
    {
        private static string Row(string command)
        {
            string src = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "KeyCommands.cs"));
            int at = src.IndexOf("new(CommandValues." + command + ",", StringComparison.Ordinal);
            Assert.True(at >= 0, "no key table row for " + command);
            int end = src.IndexOf("new(CommandValues.", at + 1, StringComparison.Ordinal);
            return src.Substring(at, end - at);
        }

        [Theory]
        [InlineData("ModeDIGU", "switch to digital USB", "upper")]
        [InlineData("ModeDIGL", "switch to digital LSB", "lower")]
        public void The_digital_modes_are_labelled_like_the_sidebands_and_keep_their_old_search_word(string command, string label, string oldWord)
        {
            string row = Row(command);
            Assert.Contains("ShortActionLabel = \"" + label + "\"", row);
            Assert.Contains("\"" + oldWord + "\"", row.Substring(row.IndexOf("Keywords", StringComparison.Ordinal)));
            Assert.Contains("\"digital\"", row);
        }

        [Fact]
        public void The_plain_sidebands_they_follow_are_still_said_this_way()
        {
            Assert.Contains("ShortActionLabel = \"switch to USB\"", Row("ModeUSB"));
            Assert.Contains("ShortActionLabel = \"switch to LSB\"", Row("ModeLSB"));
        }
    }
}
