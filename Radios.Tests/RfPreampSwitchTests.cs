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

        // The through-the-rig cases (an active panadapter's own report, the
        // finer range, before and after a report) moved to
        // RfGainDestinationTests in repair round 2, where a real vendor
        // panadapter is the active one: the rig no longer answers from
        // rig-wide fields any panadapter writes.

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
            // No rig-wide copy of any panadapter's report (removed in the
            // 2026-11-06 integration pass): the per-panadapter report is the
            // one vocabulary.
            Assert.DoesNotContain("RFGainMarkers = p.RFGainMarkers", flexBase, StringComparison.Ordinal);
            Assert.DoesNotContain("public bool RFGainIsPreampSwitch(", flexBase, StringComparison.Ordinal);
            // The completed report is recorded per panadapter at the vendor's
            // last notification (repair round 2).
            Assert.Contains("RecordRFGainReport(p);", flexBase, StringComparison.Ordinal);
            string active = Body(flexBase, "internal RFGainReport ReportForPanadapter(Panadapter pan)");
            Assert.DoesNotContain(".RFGainLow", active, StringComparison.Ordinal);
            Assert.DoesNotContain(".RFGainMarkers", active, StringComparison.Ordinal);

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
            Assert.Contains("if (asSwitch && fieldHadFocus) HandRfGainFocusTo(_rfPreampCheck, _rfGainControl);", shape, StringComparison.Ordinal);
            Assert.Contains("else if (!asSwitch && switchHadFocus) HandRfGainFocusTo(_rfGainControl, _rfPreampCheck);", shape, StringComparison.Ordinal);
            Assert.Contains("_rfGainShownPan = report.Pan;", shape, StringComparison.Ordinal);
            // The hand-off acts only if nothing has moved focus on, and only
            // for the latest request (repair round 2).
            string handOff = Body(panel, "private void HandRfGainFocusTo(UIElement incoming, UIElement outgoing)");
            Assert.Contains("if (generation != _rfFocusGeneration || !ReferenceEquals(rig, _rig)) return;", handOff, StringComparison.Ordinal);
            Assert.Contains("if (!RfGainFocusStillAt(System.Windows.Input.Keyboard.FocusedElement as DependencyObject, outgoing)) return;", handOff, StringComparison.Ordinal);
            Assert.Contains("incoming.Focus();", handOff, StringComparison.Ordinal);
            // The field writes to the panadapter whose report it shows.
            Assert.Contains("_rig.SetRFGain(_rfGainShownPan, v)", panel, StringComparison.Ordinal);

            // The switch writes the radio's own "on" setting or 0, and only on
            // an operator press: both events wired, the poll guard first.
            Assert.Contains("_rfPreampCheck.Checked += (s, e) => ToggleRfPreamp(true);", panel, StringComparison.Ordinal);
            Assert.Contains("_rfPreampCheck.Unchecked += (s, e) => ToggleRfPreamp(false);", panel, StringComparison.Ordinal);
            string toggle = Body(panel, "private void ToggleRfPreamp(bool on)");
            Assert.Contains("if (_polling || _rig == null) return;", toggle, StringComparison.Ordinal);
            Assert.Contains("var report = _rig.ActiveRFGainReport;", toggle, StringComparison.Ordinal);
            Assert.Contains("_rig.SetRFGain(report.Pan, v == FlexBase.OffOnValues.on ? onDb : 0);", toggle, StringComparison.Ordinal);
            Assert.Contains("ApplyRfGainShape(rig);", Body(panel, "public void Initialize("), StringComparison.Ordinal);

            string menu = Read("JJFlexWpf", "NativeMenuBar.cs");
            string adjust = Body(menu, "private void AdjustRfGain(bool up)");
            Assert.Contains("FlexBase.IsPreampSwitch(report, out int onDb)", adjust, StringComparison.Ordinal);
            Assert.Contains("int value = up ? onDb : 0;", adjust, StringComparison.Ordinal);
            Assert.Contains("Rig.SetRFGain(report.Pan, value);", adjust, StringComparison.Ordinal);
            Assert.Contains("v => Rig.SetRFGain(report.Pan, v)", adjust, StringComparison.Ordinal);
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
