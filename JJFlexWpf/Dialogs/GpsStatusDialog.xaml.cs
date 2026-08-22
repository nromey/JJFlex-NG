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
    /// held steady long enough to be worth reporting.
    ///
    /// Third attempt (2026-08-07): the status fields were TextBlocks, which a
    /// screen reader cannot tab to or arrow through — Noel's live finding.
    /// Everything now renders into one <see cref="Controls.LiveStatusTextBox"/>:
    /// a read-only text box that IS a tab stop, holds the review caret in place
    /// across the 1 Hz refreshes, and skips rewrites when nothing changed so
    /// NVDA does not chatter.
    /// </summary>
    public partial class GpsStatusDialog : JJFlexDialog
    {
        private readonly FlexBase? _rig;
        private Action? _unsubscribe;
        private bool _loadingOscillator;
        private string _summary = string.Empty;

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
            JJTrace.Tracing.TraceLine($"GpsStatusDialog: open (rig={(rig == null ? "null" : "present")})", System.Diagnostics.TraceLevel.Info);
            InitializeComponent();

            foreach (var (value, label) in FlexBase.OscillatorChoices)
                OscillatorCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

            Refresh();

            Loaded += (s, e) =>
            {
                // Speak the summary once on open — this is the question the dialog
                // exists to answer, and making the user hunt for it defeats that.
                JJTrace.Tracing.TraceLine($"GpsStatusDialog: loaded, summary='{_summary}'", System.Diagnostics.TraceLevel.Info);
                ScreenReaderOutput.Speak(_summary, VerbosityLevel.Terse, interrupt: true);
            };

            _unsubscribe = _rig?.SubscribeGpsChanges(() => Dispatcher.BeginInvoke(() => Refresh()));
            Closed += (s, e) => { _unsubscribe?.Invoke(); _unsubscribe = null; };
        }

        private void Refresh()
        {
            var snapshot = _rig?.ReadGpsStatus() ?? new FlexBase.GpsStatusSnapshot();

            _summary = FlexBase.BuildGpsSpokenSummary(snapshot);

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

            StatusText.SetStatusText(BuildStatusText(snapshot));
            UpdateStateAnnouncement(snapshot);
        }

        /// <summary>
        /// The whole page as one plain-text document: summary first (the answer
        /// to the question the dialog exists for), then hardware, oscillator
        /// state, the per-field details, and the standing explanations. Sections
        /// are separated by blank lines; the LiveStatusTextBox normalizes those
        /// so NVDA reads them as "blank" instead of re-reading a neighbor.
        /// </summary>
        private string BuildStatusText(FlexBase.GpsStatusSnapshot s)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Lexicon.Get("connect.gps.section_summary"));
            sb.AppendLine(_summary);
            sb.AppendLine();

            sb.AppendLine(Lexicon.Get("connect.gps.section_hardware"));
            sb.AppendLine(FlexBase.DescribeInstalledReferences(s));
            sb.AppendLine();

            sb.AppendLine(Lexicon.Get("connect.gps.section_oscillator"));
            // Lock leads. It is the fact that decides whether the radio is
            // actually disciplined, and it can disagree with the GPS fix text
            // while a fix is being acquired — so it goes first here too, not
            // trailing the sentence as an afterthought.
            sb.AppendLine(s.RadioConnected
                ? Lexicon.Get("connect.gps.oscillator_line",
                    ("lock", Lexicon.Get(s.OscillatorLocked ? "connect.gps.locked" : "connect.gps.not_locked")),
                    ("oscillator", FlexBase.DescribeOscillatorInUse(s)),
                    ("error", FlexBase.FormatFreqErrorPpb(s.FreqErrorPpb)))
                : Lexicon.Get("connect.gps.no_radio"));
            sb.AppendLine(Lexicon.Get("connect.gps.oscillator_choice_note"));
            sb.AppendLine();

            string notReported = Lexicon.Get("connect.gps.not_reported");
            sb.AppendLine(Lexicon.Get("connect.gps.section_details"));
            sb.AppendLine(Lexicon.Get("connect.gps.detail_status", ("status", Or(s.Status, notReported))));
            sb.AppendLine(Lexicon.Get("connect.gps.detail_satellites",
                ("visible", Or(s.SatellitesVisible, notReported)),
                ("tracked", Or(s.SatellitesTracked, notReported))));
            sb.AppendLine(Lexicon.Get("connect.gps.detail_grid", ("grid", Or(s.Grid, notReported))));
            sb.AppendLine(Lexicon.Get("connect.gps.detail_position",
                ("latitude", Or(s.Latitude, notReported)), ("longitude", Or(s.Longitude, notReported))));
            sb.AppendLine(Lexicon.Get("connect.gps.detail_altitude", ("altitude", Or(s.Altitude, notReported))));
            sb.AppendLine(Lexicon.Get("connect.gps.detail_utc", ("utcTime", Or(s.UtcTime, notReported))));
            // Two different figures, so two labelled lines. The first is the
            // GPS receiver's own text, passed through unchanged and without an
            // invented unit; the second is the radio's clock correction, which
            // genuinely is parts per billion.
            sb.AppendLine(Lexicon.Get("connect.gps.detail_freq_error", ("freqError", Or(s.FreqError, notReported))));
            sb.AppendLine(Lexicon.Get("connect.gps.detail_clock_correction", ("freqErrorPpb", s.FreqErrorPpb)));
            sb.AppendLine();

            // The radio is an NTP server when it has a fix, not an NTP client.
            // Stated here because it is the single most misunderstood thing
            // about this feature.
            sb.AppendLine(Lexicon.Get("connect.gps.section_time"));
            sb.Append(Lexicon.Get("connect.gps.time_note"));
            return sb.ToString();
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
        /// Everything else — position, grid, altitude, UTC, frequency error,
        /// clock correction — never announces. It updates on screen for the
        /// review cursor.
        /// </summary>
        private void UpdateStateAnnouncement(FlexBase.GpsStatusSnapshot s)
        {
            if (StateAnnounceText == null || !s.RadioConnected) return;

            // A lock change, a reference handover or a GPS phase change is
            // always worth saying. Lock was NOT in this key until 2026-08-16,
            // so the single most load-bearing transition in the dialog — the
            // reference actually locking — went unannounced unless the GPS
            // status text happened to change in the same update.
            string stateKey = $"{s.OscillatorInUse}|{s.OscillatorLocked}|{s.Status}";
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

                // The separating space stays in code. It is composition, not
                // vocabulary, and a leading space inside a stored string is
                // invisible to the operator editing that file by ear.
                string sats = string.IsNullOrWhiteSpace(s.SatellitesTracked)
                    ? string.Empty
                    : " " + Lexicon.Get("connect.gps.sats_tracked", ("tracked", s.SatellitesTracked));

                // Lock first, then what it is running on, then the GPS phase.
                // The fix text used to lead, and it is the supporting detail —
                // the two can disagree while a fix is being acquired, and lock
                // is what decides whether the radio is disciplined at all.
                string gps = string.IsNullOrWhiteSpace(s.Status)
                    ? ""
                    : " " + Lexicon.Get("connect.gps.phase", ("status", s.Status));
                StateAnnounceText.Text = s.OscillatorLocked && s.OscillatorInUse.Equals("gpsdo", StringComparison.OrdinalIgnoreCase)
                    ? Lexicon.Get("connect.gps.reference_locked_gpsdo") + gps + sats
                    : Lexicon.Get(s.OscillatorLocked ? "connect.gps.reference_locked" : "connect.gps.reference_not_locked")
                      + " " + Lexicon.Get("connect.gps.running_on",
                          ("oscillator", FlexBase.DescribeOscillatorInUse(s))) + gps + sats;
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
                string unknown = Lexicon.Get("connect.gps.unknown");
                StateAnnounceText.Text = Lexicon.Get("connect.gps.sats_tracked_and_visible",
                    ("tracked", Or(tracked, unknown)), ("visible", Or(s.SatellitesVisible, unknown)));
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
                ScreenReaderOutput.Speak(
                    Lexicon.Get("connect.gps.reference_set", ("reference", item.Content)),
                    VerbosityLevel.Terse, interrupt: true);
                // The radio takes a moment to report back what it settled on, so
                // the state line updates on the next property change rather than
                // being guessed at here.
            }
            else
            {
                ScreenReaderOutput.Speak(Lexicon.Get("connect.gps.reference_change_failed"), VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void SpeakStatusButton_Click(object sender, RoutedEventArgs e)
        {
            ScreenReaderOutput.Speak(_summary, VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Copy everything as plain text. Useful for pasting into a message when
        /// asking someone else whether a fix looks right — and for the antenna
        /// question, where the installed-hardware line is the part worth sharing.
        /// </summary>
        private void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(Lexicon.Get("connect.gps.clipboard_heading") + "\r\n\r\n" + StatusText.Text);
                ScreenReaderOutput.Speak(Lexicon.Get("connect.gps.copied"), VerbosityLevel.Terse, interrupt: true);
            }
            catch
            {
                ScreenReaderOutput.Speak(Lexicon.Get("connect.gps.copy_failed"), VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
