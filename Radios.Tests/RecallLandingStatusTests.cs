using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1131 (Sprint 49 Track N): the landing after a memory recall says what
    /// the recall asked the radio for, not what the radio reported before it
    /// answered ("Listening on 14.100 megahertz, USB" for a CW memory).
    /// </summary>
    public class RecallLandingStatusTests
    {
        private static RadioStatusSnapshot Before() => new()
        {
            IsConnected = true, HasActiveSlice = true, Mode = "USB",
            FrequencyDisplay = "14.100.000", FrequencySpoken = "14.100 megahertz",
            BandName = "20m", BandSpoken = "20 meter", SliceLetter = "A",
        };

        [Fact]
        public void The_recalled_memorys_mode_frequency_and_band_replace_what_the_radio_reported_before_it_answered()
        {
            var snap = Before();
            RadioStatusBuilder.ApplyAskedFor(snap, 7_030_000, "CW");
            Assert.Equal("CW", snap.Mode);
            Assert.Equal(RadioStatusBuilder.FrequencyToSpoken(7_030_000), snap.FrequencySpoken);
            Assert.Equal(RadioStatusBuilder.FormatFreqDisplay(7_030_000), snap.FrequencyDisplay);
            Assert.Equal("40 meter", snap.BandSpoken);
            Assert.Equal("A", snap.SliceLetter);
        }

        [Fact]
        public void Nothing_asked_changes_nothing_and_a_transmitting_radio_is_left_as_it_reports()
        {
            var snap = Before();
            RadioStatusBuilder.ApplyAskedFor(snap, null, null);
            Assert.Equal("USB", snap.Mode);
            Assert.Equal("14.100 megahertz", snap.FrequencySpoken);
            var keyed = Before();
            keyed.IsTransmitting = true;
            RadioStatusBuilder.ApplyAskedFor(keyed, 7_030_000, "CW");
            Assert.Equal("USB", keyed.Mode);
            Assert.Equal("14.100 megahertz", keyed.FrequencySpoken);
        }

        [Fact]
        public void The_memory_window_records_what_it_asked_for_and_the_landing_speaks_it()
        {
            // MainWindow cannot be built in a test: read the wiring from source.
            string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
            int select = main.IndexOf("dialog.SelectMemory = (memRef) =>", StringComparison.Ordinal);
            Assert.True(select >= 0);
            string body = main.Substring(select, main.IndexOf("dialog.FormatFrequency", select, StringComparison.Ordinal) - select);
            // Repair 2 (Codex's R1): the radio's reading is taken at the action,
            // before the request, and the recall bound to it after.
            int before = body.IndexOf("BuildDetailedStatus(rig)", StringComparison.Ordinal);
            int asked = body.IndexOf("AskRadioForMode(() => mem.Select());", StringComparison.Ordinal);
            int recorded = body.IndexOf("_pendingRecall = new Radios.PendingRecall(", StringComparison.Ordinal);
            Assert.True(before >= 0 && asked > before, "the recall reads where the radio is before it asks");
            Assert.True(recorded > asked, "and records itself after its own request");
            int landing = main.IndexOf("private void RunReturnToAppLanding(", StringComparison.Ordinal);
            Assert.True(landing >= 0);
            string land = main.Substring(landing, 6000);
            int take = land.IndexOf("var asked = TakeRecallAskedFor();", StringComparison.Ordinal);
            int firstReturn = land.IndexOf("return;", StringComparison.Ordinal);
            Assert.True(take >= 0 && take < firstReturn, "taken before any guard can return");
            // One capture builds the text and its replay answer (Sprint 49 Track N2 repair 1).
            Assert.Contains("CaptureSpokenStatus(rig, asked)", land);
        }

        private static string Source(params string[] path)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
        }
    }
}
