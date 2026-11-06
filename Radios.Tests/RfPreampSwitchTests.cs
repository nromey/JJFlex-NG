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
        [InlineData(0, 20, 20, new[] { 0, 20 }, true, 20)]        // the 6300, as it reports it
        [InlineData(0, 20, 20, new[] { 20, 0 }, true, 20)]        // either order
        [InlineData(0, 10, 10, new[] { 0, 10 }, true, 10)]        // another pair: its own figure, not 20
        [InlineData(-8, 32, 8, new[] { 0, 16, 32 }, false, 0)]    // the 8600: a real range
        [InlineData(-10, 0, 10, new[] { 0, -10 }, false, 0)]      // an attenuator, not a preamp
        [InlineData(10, 20, 10, new[] { 10, 20 }, false, 0)]      // two settings, neither of them 0
        [InlineData(0, 0, 0, new[] { 0, 0 }, false, 0)]
        [InlineData(-10, 30, 10, new int[0], false, 0)]           // not reported yet: the number field
        [InlineData(0, 20, 5, new[] { 0, 20 }, false, 0)]         // two labels over five settings (repair round 1)
        [InlineData(0, 30, 30, new[] { 0, 20 }, false, 0)]        // markers and range disagree
        [InlineData(-10, 20, 30, new[] { 0, 20 }, false, 0)]      // a range whose low end is not 0
        public void Two_settings_with_zero_as_one_are_an_rf_preamp(int min, int max, int step, int[] markers, bool expected, int onDb)
        {
            Assert.Equal(expected, FlexBase.IsPreampSwitch(new FlexBase.RFGainReport(min, max, step, markers), out int db));
            Assert.Equal(onDb, db);
        }

        /// <summary>
        /// Repair round 1: the report Home and the menu use is the ACTIVE
        /// panadapter's own when it has one, not the last stored.
        /// </summary>
        [Fact]
        public void The_active_panadapters_report_wins_over_the_last_stored()
        {
            var stored = new FlexBase.RFGainReport(-8, 32, 8, new[] { 0, 16, 32 });
            var active = new FlexBase.RFGainReport(0, 20, 20, new[] { 0, 20 });
            Assert.Equal(active, FlexBase.ChooseRFGainReport(active, stored));
            Assert.Equal(stored, FlexBase.ChooseRFGainReport(null, stored));
        }

        /// <summary>
        /// Repair round 1 (Codex follow-up): markers are labels, not the list
        /// of settings. Markers of 0 and 20 over a range of 0 to 20 in steps of
        /// 5 are five settings; a switch would hide 5, 10 and 15.
        /// </summary>
        [Fact]
        public void Two_markers_over_a_finer_range_keep_the_number_field()
        {
            _rig.RFGainMin = 0;
            _rig.RFGainMax = 20;
            _rig.RFGainIncrement = 5;
            _rig.RFGainMarkers = new[] { 0, 20 };
            Assert.False(_rig.RFGainIsPreampSwitch(out _));

            // The control: the same markers over the 6300's exact range are a switch.
            _rig.RFGainIncrement = 20;
            Assert.True(_rig.RFGainIsPreampSwitch(out int db));
            Assert.Equal(20, db);
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
            _rig.RFGainMin = 0;
            _rig.RFGainMax = 20;
            _rig.RFGainIncrement = 20;
            _rig.RFGainMarkers = new[] { 0, 20 };
            Assert.True(_rig.RFGainIsPreampSwitch(out int db));
            Assert.Equal(20, db);

            // And back, when a later report has a real range.
            _rig.RFGainMin = -8;
            _rig.RFGainMax = 32;
            _rig.RFGainIncrement = 8;
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
            Assert.Contains("var report = rig.ActiveRFGainReport;", shape, StringComparison.Ordinal);
            Assert.Contains("_rfGainControl.Min = report.Min", shape, StringComparison.Ordinal);
            Assert.Contains("_rfGainControl.Max = report.Max", shape, StringComparison.Ordinal);
            Assert.Contains("_rfGainControl.Step = report.Step", shape, StringComparison.Ordinal);
            Assert.Contains("FlexBase.IsPreampSwitch(report, out _)", shape, StringComparison.Ordinal);
            // Exactly one control visible, complementary, and focus handed to
            // the replacement when the focused one goes (repair round 1).
            Assert.Contains("var fieldWanted = asSwitch ? Visibility.Collapsed : Visibility.Visible;", shape, StringComparison.Ordinal);
            Assert.Contains("var switchWanted = asSwitch ? Visibility.Visible : Visibility.Collapsed;", shape, StringComparison.Ordinal);
            Assert.Contains("_rfGainControl.Visibility = fieldWanted;", shape, StringComparison.Ordinal);
            Assert.Contains("_rfPreampCheck.Visibility = switchWanted;", shape, StringComparison.Ordinal);
            Assert.Contains("if (asSwitch && fieldHadFocus) HandRfGainFocusTo(_rfPreampCheck);", shape, StringComparison.Ordinal);
            Assert.Contains("else if (!asSwitch && switchHadFocus) HandRfGainFocusTo(_rfGainControl);", shape, StringComparison.Ordinal);

            // The switch writes the radio's own "on" setting or 0, and only on
            // an operator press: both events wired, the poll guard first.
            Assert.Contains("_rfPreampCheck.Checked += (s, e) => ToggleRfPreamp(true);", panel, StringComparison.Ordinal);
            Assert.Contains("_rfPreampCheck.Unchecked += (s, e) => ToggleRfPreamp(false);", panel, StringComparison.Ordinal);
            string toggle = Body(panel, "private void ToggleRfPreamp(bool on)");
            Assert.Contains("if (_polling || _rig == null) return;", toggle, StringComparison.Ordinal);
            Assert.Contains("_rig.RFGain = v == FlexBase.OffOnValues.on ? onDb : 0;", toggle, StringComparison.Ordinal);
            Assert.Contains("ApplyRfGainShape(rig);", Body(panel, "public void Initialize("), StringComparison.Ordinal);

            string menu = Read("JJFlexWpf", "NativeMenuBar.cs");
            string adjust = Body(menu, "private void AdjustRfGain(bool up)");
            Assert.Contains("Rig.RFGainIsPreampSwitch(out int onDb)", adjust, StringComparison.Ordinal);
            Assert.Contains("int value = up ? onDb : 0;", adjust, StringComparison.Ordinal);
            Assert.Contains("Rig.RFGain = value;", adjust, StringComparison.Ordinal);
            Assert.Contains("\"audio.fields.rf_preamp_off\"", adjust, StringComparison.Ordinal);
            Assert.Contains("var report = Rig.ActiveRFGainReport;", adjust, StringComparison.Ordinal);
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
