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
    /// Announcement policy, which took two attempts to get right. GPS status
    /// arrives once per second and most fields change on nearly every update, so
    /// marking them live meant NVDA never finished a sentence. But going fully
    /// silent loses the transitions someone actually opened this dialog to wait
    /// for. So there is exactly ONE live region, and it receives text only when
    /// the reference hands over, the GPS phase changes, or a satellite count has
    /// held steady long enough to be worth reporting. Every other field is plain
    /// text, refreshed at the full rate for the review cursor.
    ///
    /// General rule this follows: standard controls and on-screen text where they
    /// work, Tolk only where they do not.
    /// </summary>
    public partial class GpsStatusDialog : JJFlexDialog
    {
        private readonly FlexBase? _rig;
        private Action? _unsubscribe;
        private bool _loadingOscillator;

        // Transition tracking for the single live region. Announcing every
        // change would be as bad as the live-region-on-everything bug this
        // replaces, so state changes announce immediately and satellite counts
        // must hold still first.
        private string _lastAnnouncedState = string.Empty;
        private string _lastAnnouncedSats = string.Empty;
        private string _pendingSats = string.Empty;
        private DateTime _pendingSatsSince = DateTime.MinValue;

        /// <summary>
        /// How long a satellite count must stay put before it is worth saying.
        /// The receiver bounces its tracked count every second or two, so
        /// without this the live region would chatter continuously.
        /// </summary>
        private static readonly TimeSpan SatelliteSettleTime = TimeSpan.FromSeconds(15);

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

            UpdateStateAnnouncement(snapshot);
        }

        /// <summary>
        /// Decide whether anything happened worth interrupting the user for, and
        /// if so put it in the live region.
        ///
        /// Two classes of event qualify. A change in the reference or the GPS
        /// phase is announced straight away — those are the transitions someone
        /// watching this dialog is actually waiting on. A change in satellites
        /// tracked only announces once it has held for
        /// <see cref="SatelliteSettleTime"/>, because the receiver bounces that
        /// number constantly and reporting each bounce is the very noise this
        /// design exists to avoid.
        ///
        /// Everything else — position, grid, altitude, UTC, frequency error —
        /// never announces. It updates on screen for the review cursor.
        /// </summary>
        private void UpdateStateAnnouncement(FlexBase.GpsStatusSnapshot s)
        {
            if (StateAnnounceText == null || !s.RadioConnected) return;

            // A reference handover or a GPS phase change is always worth saying.
            string stateKey = $"{s.OscillatorInUse}|{s.Status}";
            if (stateKey != _lastAnnouncedState)
            {
                bool first = _lastAnnouncedState.Length == 0;
                _lastAnnouncedState = stateKey;
                _lastAnnouncedSats = s.SatellitesTracked;
                _pendingSats = s.SatellitesTracked;
                _pendingSatsSince = DateTime.UtcNow;

                // Nothing to announce on the very first pass — that is just the
                // dialog opening, and the summary already covers it.
                if (first) return;

                string sats = string.IsNullOrWhiteSpace(s.SatellitesTracked)
                    ? string.Empty
                    : $" {s.SatellitesTracked} satellites tracked.";

                StateAnnounceText.Text = s.OscillatorLocked && s.OscillatorInUse.Equals("gpsdo", StringComparison.OrdinalIgnoreCase)
                    ? $"GPS {s.Status}. The radio is now using the GPS reference.{sats}"
                    : $"GPS {s.Status}. Running on {FlexBase.DescribeOscillatorInUse(s)}.{sats}";
                return;
            }

            // Satellite count: only once it has stopped moving.
            string tracked = s.SatellitesTracked ?? string.Empty;
            if (tracked != _pendingSats)
            {
                _pendingSats = tracked;
                _pendingSatsSince = DateTime.UtcNow;
                return;
            }

            if (tracked != _lastAnnouncedSats
                && DateTime.UtcNow - _pendingSatsSince >= SatelliteSettleTime)
            {
                _lastAnnouncedSats = tracked;
                StateAnnounceText.Text =
                    $"{Or(tracked, "unknown")} satellites tracked, {Or(s.SatellitesVisible, "unknown")} visible.";
            }
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
