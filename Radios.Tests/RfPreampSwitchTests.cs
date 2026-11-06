#nullable enable

using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Where the radio offers RF gain as only two settings, 0 and one other,
    /// Home presents it as an RF preamp switch rather than a number field that
    /// offers two values and reads as broken (#889). The name is Noel's,
    /// 2026-11-05: "RF preamp". The dB figure is the radio's own, never the
    /// 6300's 20 assumed.
    /// </summary>
    public sealed class RfPreampSwitchTests : IDisposable
    {
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public void Dispose() => _rig.Dispose();

        [Theory]
        [InlineData(new[] { 0, 20 }, true, 20)]       // the 6300, as it reports it
        [InlineData(new[] { 20, 0 }, true, 20)]       // either order
        [InlineData(new[] { 0, 10 }, true, 10)]       // another pair: its own figure, not 20
        [InlineData(new[] { 0, 16, 32 }, false, 0)]   // the 8600: a real range
        [InlineData(new[] { 0, -10 }, false, 0)]      // an attenuator, not a preamp
        [InlineData(new[] { 10, 20 }, false, 0)]      // two settings, neither of them 0
        [InlineData(new[] { 0, 0 }, false, 0)]
        [InlineData(new int[0], false, 0)]            // not reported yet: the number field
        public void Two_settings_with_zero_as_one_are_an_rf_preamp(int[] markers, bool expected, int onDb)
        {
            Assert.Equal(expected, FlexBase.IsPreampSwitch(markers, out int db));
            Assert.Equal(onDb, db);
        }

        [Fact]
        public void Before_the_radio_reports_its_settings_the_number_field_stays()
        {
            Assert.Empty(_rig.RFGainMarkers);
            Assert.False(_rig.RFGainIsPreampSwitch(out _));
        }

        [Fact]
        public void The_rig_answers_from_the_markers_the_radio_reported()
        {
            _rig.RFGainMarkers = new[] { 0, 20 };
            Assert.True(_rig.RFGainIsPreampSwitch(out int db));
            Assert.Equal(20, db);

            // And back, when a later report has a real range.
            _rig.RFGainMarkers = new[] { 0, 16, 32 };
            Assert.False(_rig.RFGainIsPreampSwitch(out _));
        }

        /// <summary>
        /// The wiring, pinned at the source because Home is WPF and nothing
        /// here builds a window: the radio's report is stored, Home re-applies
        /// the shape and the bounds on every receiver poll as well as at
        /// connect, and the Radio menu's RF Gain items answer in the switch's
        /// words on a two-setting radio.
        /// </summary>
        [Fact]
        public void The_report_is_kept_and_home_and_the_menu_follow_it()
        {
            string flexBase = FlexBasePartials.JoinedText();
            Assert.Contains("RFGainMarkers = p.RFGainMarkers?.ToArray() ?? Array.Empty<int>();", flexBase, StringComparison.Ordinal);

            string panel = Read("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
            string poll = Body(panel, "private void PollReceiver()");
            Assert.Contains("ApplyRfGainShape(_rig);", poll, StringComparison.Ordinal);
            string shape = Body(panel, "private void ApplyRfGainShape(FlexBase rig)");
            Assert.Contains("_rfGainControl.Min = rig.RFGainMin", shape, StringComparison.Ordinal);
            Assert.Contains("_rfGainControl.Max = rig.RFGainMax", shape, StringComparison.Ordinal);
            Assert.Contains("_rfGainControl.Step = rig.RFGainIncrement", shape, StringComparison.Ordinal);
            Assert.Contains("rig.RFGainIsPreampSwitch(out _)", shape, StringComparison.Ordinal);
            Assert.Contains("ApplyRfGainShape(rig);", Body(panel, "public void Initialize("), StringComparison.Ordinal);

            string menu = Read("JJFlexWpf", "NativeMenuBar.cs");
            string adjust = Body(menu, "private void AdjustRfGain(bool up)");
            Assert.Contains("Rig.RFGainIsPreampSwitch(out int onDb)", adjust, StringComparison.Ordinal);
            Assert.Contains("\"audio.fields.rf_preamp_on\"", adjust, StringComparison.Ordinal);
        }

        private static string Read(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repository root above " + AppContext.BaseDirectory);
            string path = Path.Combine(dir!.FullName, Path.Combine(parts));
            Assert.True(File.Exists(path), path + " is not where this test reads it.");
            return File.ReadAllText(path);
        }

        private static string Body(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is not where this test reads it.");
            int open = text.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            Assert.Fail("The body of " + signature + " never closes.");
            return "";
        }
    }
}
