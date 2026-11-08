using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Audio → Recorder: "warn before a recording on the radio is
    /// overwritten" (#999), beside the other recorder settings. On by default and
    /// saved per radio; the warning's own "don't show this again" box turns this
    /// same setting off, and this is where it comes back on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Always shown, whatever the connection (repair round 1).</b> Settings
    /// are intents: never hidden on connection state, queued and applied. The
    /// box is for one radio, named beside it and in its screen-reader help: the
    /// connected radio; with none connected, the radio this program last
    /// connected to, else the radio most recently seen. With no radio ever used
    /// it says it applies to the next radio connected, and a choice made then
    /// waits for that radio (<see cref="RadioConfig.PendingWarnBeforeSliceRecordingOverwritten"/>).
    /// </para>
    /// <para>
    /// <b>An edit is kept for the radio it was made for</b>, across a disconnect
    /// or the target changing, and saved to that radio's config on OK or Apply
    /// (<see cref="SliceOverwriteSettingEdits"/>, tested without this window).
    /// </para>
    /// <para>PLACEHOLDER: Codex writes (#999) the label, the "applies to" lines and the Ctrl+F1 text.</para>
    /// </remarks>
    public partial class SettingsDialog
    {
        private CheckBox? _warnOverwriteCheck;
        private TextBlock? _warnOverwriteAppliesTo;
        private bool _warnOverwriteQuiet;
        private readonly SliceOverwriteSettingEdits _warnOverwriteEdits = new();

        /// <summary>Build the box from the Lexicon. Called once, from the constructor.</summary>
        private void InitializeSliceOverwriteSetting()
        {
            string label = Lexicon.Get("audio.recorder.overwrite.setting");
            _warnOverwriteCheck = new CheckBox { Content = label, Margin = new Thickness(0, 0, 0, 2) };
            AutomationProperties.SetName(_warnOverwriteCheck, label);
            JJFlexHelp.SetText(_warnOverwriteCheck, Lexicon.Get("audio.recorder.overwrite.setting_help"));
            _warnOverwriteCheck.Checked += (_, _) => NoteWarnOverwriteEdit();
            _warnOverwriteCheck.Unchecked += (_, _) => NoteWarnOverwriteEdit();
            _warnOverwriteAppliesTo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 8) };
            // After Recorder beeps and Recorder speech, before the playback level.
            int at = System.Math.Min(2, RecorderPanel.Children.Count);
            RecorderPanel.Children.Insert(at, _warnOverwriteCheck);
            RecorderPanel.Children.Insert(at + 1, _warnOverwriteAppliesTo);
            RefreshSliceOverwriteSetting();
        }

        private void NoteWarnOverwriteEdit()
        {
            if (_warnOverwriteQuiet || _warnOverwriteCheck == null) return;
            _warnOverwriteEdits.Edit(_warnOverwriteCheck.IsChecked == true);
        }

        /// <summary>
        /// Which radio the box is for: the connected one; else the one this
        /// program last connected to; else the one most recently seen; else none.
        /// </summary>
        private (string Serial, string Name) WarnOverwriteTarget()
        {
            var rig = _rig;
            string serial = rig != null && rig.IsConnected ? rig.TakeAdmissionFactsNow().Serial : "";
            if (string.IsNullOrEmpty(serial)) serial = rig?.SelectedRadioSerial ?? "";
            List<KnownRadioEntry> known;
            try { known = KnownRadioRoster.Load(); }
            catch (System.Exception ex)
            {
                JJTrace.Tracing.TraceLine("Settings: known radios: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
                known = new List<KnownRadioEntry>();
            }
            if (string.IsNullOrEmpty(serial))
                serial = known.OrderByDescending(e => e.LastSeenUtc).FirstOrDefault()?.Serial ?? "";
            if (string.IsNullOrEmpty(serial)) return ("", "");
            var entry = known.FirstOrDefault(e => string.Equals(e.Serial, serial, System.StringComparison.OrdinalIgnoreCase));
            string name = entry == null ? serial
                : !string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName
                : !string.IsNullOrWhiteSpace(entry.Model) ? entry.Model : serial;
            return (serial, name);
        }

        /// <summary>Show the box for its radio, keeping any edit made for that radio. Never hides it.</summary>
        private void RefreshSliceOverwriteSetting()
        {
            if (_warnOverwriteCheck == null || _warnOverwriteAppliesTo == null) return;
            var (serial, name) = WarnOverwriteTarget();
            if (serial.Length > 0) RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(serial);
            bool value = _warnOverwriteEdits.Show(serial,
                s => RadioConfig.LoadForRadio(s).WarnBeforeSliceRecordingOverwritten,
                RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
            _warnOverwriteQuiet = true;
            try { _warnOverwriteCheck.IsChecked = value; }
            finally { _warnOverwriteQuiet = false; }
            string applies = serial.Length > 0
                ? Lexicon.Get("audio.recorder.overwrite.setting_applies_to", ("radio", name))
                : Lexicon.Get("audio.recorder.overwrite.setting_applies_next");
            _warnOverwriteAppliesTo.Text = applies;
            // Read with the box when it takes focus, since the line beside it is not in the tab order.
            AutomationProperties.SetHelpText(_warnOverwriteCheck, applies);
        }

        /// <summary>Save every edit to the radio it was made for. Called on OK and Apply.</summary>
        private void CommitSliceOverwriteSetting() =>
            _warnOverwriteEdits.Commit(
                (serial, warn) =>
                {
                    bool saved = RadioConfig.LoadForRadio(serial).WarnBeforeSliceRecordingOverwritten == warn
                        || RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(serial, warn);
                    if (!saved)
                        JJTrace.Tracing.TraceLine("Settings: the overwrite warning setting could not be saved for " + serial,
                            System.Diagnostics.TraceLevel.Warning);
                    return saved;
                },
                warn => RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = warn);
    }
}
