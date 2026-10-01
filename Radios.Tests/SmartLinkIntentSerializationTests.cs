#nullable enable

using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A stored SmartLink answer must survive every future build. The operator's
    /// "I never operate this radio from away" is written to the per-radio
    /// <c>config.xml</c> and never asked again, so a build that cannot read it
    /// back asks again as if they had never answered — and silently, because
    /// <see cref="RadioConfig.Load"/> answers an unreadable file with defaults.
    /// </summary>
    /// <remarks>
    /// <para><b>Why names and not numbers.</b> <c>XmlSerializer</c> writes an
    /// enum by MEMBER NAME — <c>&lt;SmartLinkIntent&gt;LocalOnly&lt;/SmartLinkIntent&gt;</c>
    /// — so the "numeric values are stable" note on
    /// <see cref="SmartLinkIntents"/> protects nothing here. Renaming a member
    /// to match nicer button text orphans every stored answer with that
    /// name. #352 records the trap, and Sol's review of Track L named it
    /// again. Track L did not rename anything — the enum has been unchanged
    /// since Sprint 30 — so these exist to catch the NEXT rename, including a
    /// rename of the default.</para>
    ///
    /// <para><b>The fixture is written by hand</b>, in the shape the
    /// pre-Track-L build wrote it, and read from a temporary folder. No
    /// operator's settings tree is read or touched.</para>
    /// </remarks>
    public sealed class SmartLinkIntentSerializationTests : IDisposable
    {
        private const string RadioId = "0000-1111-8600-2222";

        // A non-default value in the same file. If the file fails to parse,
        // Load returns defaults and this reads 0 — so asserting it is the
        // positive control that the file was really read, which matters most
        // for Undecided: a failed parse ALSO yields Undecided.
        private const int Sentinel = 4999;

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "jjflex-l2-intent-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* a temp folder left behind harms nothing */ }
        }

        private void WriteOldFormatConfig(string intentName)
        {
            var folder = Path.Combine(_dir, "radios", RadioId);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "config.xml"),
                "<?xml version=\"1.0\"?>\r\n"
                + "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n"
                + "  <Version>1</Version>\r\n"
                + "  <RadioId>" + RadioId + "</RadioId>\r\n"
                + "  <FixedHolePunchPort>" + Sentinel + "</FixedHolePunchPort>\r\n"
                + "  <SmartLinkIntent>" + intentName + "</SmartLinkIntent>\r\n"
                + "</RadioConfig>\r\n");
        }

        /// <summary>
        /// Every name a pre-Track-L build could have written reads back as the
        /// member it meant.
        /// </summary>
        [Theory]
        [InlineData("Undecided", SmartLinkIntents.Undecided)]
        [InlineData("LocalOnly", SmartLinkIntents.LocalOnly)]
        [InlineData("WantsSmartLink", SmartLinkIntents.WantsSmartLink)]
        public void A_stored_answer_from_an_older_build_reads_back(string storedName, SmartLinkIntents expected)
        {
            WriteOldFormatConfig(storedName);

            var cfg = RadioConfig.Load(_dir, RadioId);

            Assert.Equal(Sentinel, cfg.FixedHolePunchPort);
            Assert.Equal(expected, cfg.SmartLinkIntent);
        }

        /// <summary>
        /// And what this build writes is what an older build wrote, so an
        /// answer given today still reads in the build that follows it.
        /// </summary>
        [Fact]
        public void An_answer_saved_now_is_written_by_the_name_older_builds_read()
        {
            WriteOldFormatConfig("LocalOnly");
            var cfg = RadioConfig.Load(_dir, RadioId);
            Assert.Equal(Sentinel, cfg.FixedHolePunchPort);

            Assert.True(cfg.Save(_dir, RadioId));

            string written = File.ReadAllText(Path.Combine(_dir, "radios", RadioId, "config.xml"));
            Assert.Contains("<SmartLinkIntent>LocalOnly</SmartLinkIntent>", written, StringComparison.Ordinal);
            Assert.Equal(SmartLinkIntents.LocalOnly, RadioConfig.Load(_dir, RadioId).SmartLinkIntent);
        }
    }
}
