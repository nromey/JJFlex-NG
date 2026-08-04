using System.Text;
using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// GPS / GNSS status and reference-oscillator selection.
    ///
    /// Built as a live view rather than a snapshot: a GPS acquiring a fix moves
    /// through several states over a few minutes, and the whole point of opening
    /// this dialog is usually to watch that happen. It subscribes to the radio's
    /// property changes and refreshes in place, so a screen reader user can leave
    /// it open and hear the summary line update as satellites come in.
    ///
    /// The view is deliberately SILENT while open. GPS status arrives once per
    /// second and most fields change on nearly every update, so any automatic
    /// announcement talks over itself and never completes. The screen refreshes
    /// at that rate instead and a review cursor reads it on demand. Speech
    /// happens only when the user presses Speak status.
    ///
    /// General rule this follows: standard controls and on-screen text where they
    /// work, Tolk only where they do not.
    /// </summary>
    public partial class GpsStatusDialog : JJFlexDialog
    {
        private readonly FlexBase? _rig;
        private Action? _unsubscribe;
        private bool _loadingOscillator;

        public GpsStatusDialog(FlexBase? rig)
        {
            _rig = rig;
            InitializeComponent();

            foreach (var (value, label) in FlexBase.OscillatorChoices)
                OscillatorCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

            Refresh();

            Loaded += (s, e) =>
            {
                // Speak the summary once on open — this is the question the dialog
                // exists to answer, and making the user hunt for it defeats that.
                ScreenReaderOutput.Speak(SummaryText.Text, VerbosityLevel.Terse, interrupt: true);
            };

            _unsubscribe = _rig?.SubscribeGpsChanges(() => Dispatcher.BeginInvoke(() => Refresh()));
            Closed += (s, e) => { _unsubscribe?.Invoke(); _unsubscribe = null; };
        }

        private void Refresh()
        {
            var snapshot = _rig?.ReadGpsStatus() ?? new FlexBase.GpsStatusSnapshot();

            string summary = FlexBase.BuildGpsSpokenSummary(snapshot);
            SummaryText.Text = summary;
            HardwareText.Text = FlexBase.DescribeInstalledReferences(snapshot);

            // Oscillator selection. Suppressed while loading so setting the combo
            // from the radio doesn't loop straight back into a set command.
            _loadingOscillator = true;
            try
            {
                OscillatorCombo.IsEnabled = snapshot.RadioConnected;
                for (int i = 0; i < OscillatorCombo.Items.Count; i++)
                {
                    if (OscillatorCombo.Items[i] is ComboBoxItem item
                        && string.Equals(item.Tag as string, snapshot.OscillatorSelected, StringComparison.OrdinalIgnoreCase))
                    {
                        OscillatorCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally { _loadingOscillator = false; }

            OscillatorStateText.Text = snapshot.RadioConnected
                ? $"Currently running on {FlexBase.DescribeOscillatorInUse(snapshot)}. " +
                  (snapshot.OscillatorLocked ? "It is locked." : "It is not locked.")
                : "No radio connected.";

            DetailFixText.Text = "GPS status: " + Or(snapshot.Status, "not reported");
            DetailSatellitesText.Text =
                $"Satellites visible: {Or(snapshot.SatellitesVisible, "not reported")}. " +
                $"Satellites tracked: {Or(snapshot.SatellitesTracked, "not reported")}.";
            DetailGridText.Text = "Grid square: " + Or(snapshot.Grid, "not reported");
            DetailPositionText.Text =
                $"Latitude: {Or(snapshot.Latitude, "not reported")}. Longitude: {Or(snapshot.Longitude, "not reported")}.";
            DetailAltitudeText.Text = "Altitude: " + Or(snapshot.Altitude, "not reported");
            DetailUtcText.Text = "GPS time, UTC: " + Or(snapshot.UtcTime, "not reported");
            DetailFreqErrorText.Text = "Frequency error: " + Or(snapshot.FreqError, "not reported");

            // Deliberately silent. GPS status arrives once a second and several
            // fields change on nearly every update, so ANY automatic speech here
            // talks over itself and never finishes a sentence. The screen is
            // refreshed at that same ~1 Hz instead, which a review cursor can
            // read at whatever pace the user wants. Speech is on request only,
            // via the Speak status button.
        }

        private static string Or(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;

        private void OscillatorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingOscillator || _rig == null) return;
            if (OscillatorCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string value) return;

            if (_rig.SetSelectedOscillator(value))
            {
                ScreenReaderOutput.Speak($"Reference set to {item.Content}.", VerbosityLevel.Terse, interrupt: true);
                // The radio takes a moment to report back what it settled on, so
                // the state line updates on the next property change rather than
                // being guessed at here.
            }
            else
            {
                ScreenReaderOutput.Speak("The reference could not be changed.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void SpeakStatusButton_Click(object sender, RoutedEventArgs e)
        {
            ScreenReaderOutput.Speak(SummaryText.Text, VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Copy everything as plain text. Useful for pasting into a message when
        /// asking someone else whether a fix looks right — and for the antenna
        /// question, where the installed-hardware line is the part worth sharing.
        /// </summary>
        private void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("GPS and reference status");
            sb.AppendLine();
            sb.AppendLine(SummaryText.Text);
            sb.AppendLine();
            sb.AppendLine(HardwareText.Text);
            sb.AppendLine();
            sb.AppendLine(OscillatorStateText.Text);
            sb.AppendLine();
            sb.AppendLine(DetailFixText.Text);
            sb.AppendLine(DetailSatellitesText.Text);
            sb.AppendLine(DetailGridText.Text);
            sb.AppendLine(DetailPositionText.Text);
            sb.AppendLine(DetailAltitudeText.Text);
            sb.AppendLine(DetailUtcText.Text);
            sb.AppendLine(DetailFreqErrorText.Text);

            try
            {
                Clipboard.SetText(sb.ToString());
                ScreenReaderOutput.Speak("Copied.", VerbosityLevel.Terse, interrupt: true);
            }
            catch
            {
                ScreenReaderOutput.Speak("Copy failed.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
