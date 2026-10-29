using System;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A hardware PTT pressed while the app's own key is up must be seen
    /// (#835, bench 2026-10-28 step 6). The radio reported <c>source=RCA,SW</c>;
    /// FlexLib parses a list as no source and keeps the old <c>SW</c>, so a box
    /// send was never taken over by the microphone's PTT.
    /// </summary>
    /// <remarks>
    /// The fixture feeds status lines through the vendored <c>Radio.ParseStatus</c>,
    /// so these tests read the line exactly where production does: FlexLib's own
    /// telnet trace of the <c>interlock</c> topic. What they cannot see is the
    /// radio: that it reports the list this way under a software key is the
    /// 2026-10-28 trace (<c>ParseInterlockStatus: Error - Invalid PTT Source
    /// (RCA,SW)</c>), not a test.
    /// </remarks>
    public sealed class PttSourceReadingTests
    {
        private static readonly MethodInfo NoteRadioMox =
            typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static RigOnVendorRadio Fixture()
        {
            var f = new RigOnVendorRadio("ptt-source-test");
            f.Vendor.Radio.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Mox") NoteRadioMox.Invoke(f.Rig, new object[] { f.Vendor.Radio.Mox });
            };
            return f;
        }

        private static void Interlock(RigOnVendorRadio f, string state, string source) =>
            f.Vendor.Status($"interlock state={state} source={source} tx_client_handle=0x{RigOnVendorRadio.OurHandle:X8}");

        [Theory]
        [InlineData("RCA,SW", true)]
        [InlineData("SW,RCA", true)]
        [InlineData("MIC,SW", true)]
        [InlineData("ACC,SW", true)]
        [InlineData("SW", false)]
        [InlineData("TUNE", false)]
        [InlineData("", false)]
        [InlineData("rca", true)]
        public void A_list_is_hardware_when_any_member_is_a_hardware_line(string raw, bool hardware)
        {
            // The parse FlexLib keeps for a list is the stale SW, so the raw word must win.
            Assert.Equal(hardware, PttSourceReading.IsHardware(PTTSource.SW, raw));
        }

        [Fact]
        public void With_no_raw_word_the_answer_is_flexlibs_parse_as_before()
        {
            Assert.True(PttSourceReading.IsHardware(PTTSource.RCA, null));
            Assert.True(PttSourceReading.IsHardware(PTTSource.Mic, null));
            Assert.True(PttSourceReading.IsHardware(PTTSource.ACC, null));
            Assert.False(PttSourceReading.IsHardware(PTTSource.SW, null));
            Assert.False(PttSourceReading.IsHardware(PTTSource.None, null));
        }

        [Theory]
        [InlineData("S1A2B3C4D|interlock state=TRANSMITTING source=RCA,SW reason= tx_allowed=1", "RCA,SW")]
        [InlineData("S0|interlock source=SW state=READY", "SW")]
        [InlineData("S0|interlock state=READY source=", "")]
        [InlineData("S0|interlock state=READY", null)]
        [InlineData("S0|transmit source=RCA", null)]
        [InlineData("C12|interlock source=RCA", null)]
        [InlineData("", null)]
        public void The_source_is_read_from_an_interlock_status_line_as_sent(string line, string? expected)
        {
            Assert.Equal(expected, PttSourceReading.SourceFromInterlockStatus(line));
        }

        /// <summary>
        /// The bench failure, end to end through the vendored parser: a box
        /// send's software key, then the microphone's PTT pressed under it.
        /// </summary>
        [Fact]
        public void A_hardware_line_reported_beside_the_sends_own_key_takes_over_a_box_send()
        {
            using var f = Fixture();
            using var watch = f.Rig.WatchPttSource();
            var takeover = BoxSendTakeover.For(f.Rig, () => false);
            Interlock(f, "TRANSMITTING", "SW");
            Assert.True(f.Rig.Transmit, "positive control: the interlock reached FlexBase.Transmit");
            Assert.Equal(BoxTakeover.None, takeover.Check());

            Interlock(f, "TRANSMITTING", "RCA,SW");

            Assert.Equal(PTTSource.SW, f.Vendor.Radio.PTTSource);   // FlexLib's stale value, the defect
            Assert.True(f.Rig.PttSourceIsHardware);
            Assert.Equal(BoxTakeover.Hardware, takeover.Check());
        }

        [Fact]
        public void The_line_dropping_back_to_the_sends_key_alone_reads_as_software_again()
        {
            using var f = Fixture();
            using var watch = f.Rig.WatchPttSource();
            Interlock(f, "TRANSMITTING", "RCA,SW");
            Assert.True(f.Rig.PttSourceIsHardware);
            Interlock(f, "TRANSMITTING", "SW");
            Assert.False(f.Rig.PttSourceIsHardware);
        }

        [Fact]
        public void An_interlock_line_without_a_source_keeps_the_last_one_as_flexlib_does()
        {
            using var f = Fixture();
            using var watch = f.Rig.WatchPttSource();
            Interlock(f, "TRANSMITTING", "RCA,SW");
            f.Vendor.Status("interlock state=TRANSMITTING");
            Assert.True(f.Rig.PttSourceIsHardware);
        }

        /// <summary>
        /// Outside a watch the reading is FlexLib's, unchanged, and the topic is
        /// not left selected: a selected topic costs the vendor an untrimmed
        /// list and a parse of every command's first word.
        /// </summary>
        [Fact]
        public void The_watch_selects_the_interlock_topic_only_while_held_and_nested_leases_share_it()
        {
            using var f = Fixture();
            Assert.DoesNotContain("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);

            var outer = f.Rig.WatchPttSource();
            var inner = f.Rig.WatchPttSource();
            Assert.Contains("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);
            inner.Dispose();
            inner.Dispose();   // twice is harmless and does not release the outer lease
            Assert.Contains("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);
            Interlock(f, "TRANSMITTING", "RCA,SW");
            Assert.True(f.Rig.PttSourceIsHardware);

            outer.Dispose();
            Assert.DoesNotContain("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);
            Assert.Null(f.Rig.RawPttSource);
            Assert.False(f.Rig.PttSourceIsHardware);   // FlexLib's SW again, as before this change
        }

        [Fact]
        public void A_source_from_before_the_watch_began_is_not_read_as_current()
        {
            using var f = Fixture();
            Interlock(f, "TRANSMITTING", "RCA,SW");   // nobody was watching
            using var watch = f.Rig.WatchPttSource();
            Assert.Null(f.Rig.RawPttSource);
        }

        [Fact]
        public void A_topic_someone_else_selected_is_left_selected_when_the_watch_ends()
        {
            using var f = Fixture();
            f.Vendor.Radio.AddTelnetTraceTopic("interlock");
            using (f.Rig.WatchPttSource()) { }
            Assert.Contains("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);
        }
    }
}
