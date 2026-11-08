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
    /// <b>Per radio, so it needs a radio to say which one.</b> Shown, and in the
    /// tab order, while a radio is connected, as the per-radio "PC audio on
    /// connect" choice on this tab is; collapsed otherwise. Committed on OK or
    /// Apply, for the radio it was shown for.
    /// </para>
    /// <para>PLACEHOLDER: Codex writes (#999) the label and its Ctrl+F1 text.</para>
    /// </remarks>
    public partial class SettingsDialog
    {
        private CheckBox? _warnOverwriteCheck;
        private string _warnOverwriteSerial = "";
        private bool _warnOverwriteLoaded;

        /// <summary>Build the box from the Lexicon. Called once, from the constructor.</summary>
        private void InitializeSliceOverwriteSetting()
        {
            string label = Lexicon.Get("audio.recorder.overwrite.setting");
            _warnOverwriteCheck = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetName(_warnOverwriteCheck, label);
            JJFlexHelp.SetText(_warnOverwriteCheck, Lexicon.Get("audio.recorder.overwrite.setting_help"));
            // After Recorder beeps and Recorder speech, before the playback level.
            RecorderPanel.Children.Insert(System.Math.Min(2, RecorderPanel.Children.Count), _warnOverwriteCheck);
            RefreshSliceOverwriteSetting();
        }

        /// <summary>
        /// Show the box for the connected radio, reading its saved value when the
        /// radio is new to this dialog; an unsaved change for the same radio is kept.
        /// </summary>
        private void RefreshSliceOverwriteSetting()
        {
            if (_warnOverwriteCheck == null) return;
            var rig = _rig;
            string serial = rig != null && rig.IsConnected ? rig.TakeAdmissionFactsNow().Serial : "";
            if (string.IsNullOrEmpty(serial))
            {
                _warnOverwriteSerial = "";
                _warnOverwriteLoaded = false;
                _warnOverwriteCheck.Visibility = Visibility.Collapsed;
                return;
            }
            if (!_warnOverwriteLoaded || _warnOverwriteSerial != serial)
            {
                _warnOverwriteSerial = serial;
                _warnOverwriteLoaded = true;
                _warnOverwriteCheck.IsChecked = RadioConfig.LoadForRadio(serial).WarnBeforeSliceRecordingOverwritten;
            }
            _warnOverwriteCheck.Visibility = Visibility.Visible;
        }

        /// <summary>Save the box for the radio it was shown for, when it changed. Called on OK and Apply.</summary>
        private void CommitSliceOverwriteSetting()
        {
            if (_warnOverwriteCheck == null || !_warnOverwriteLoaded || string.IsNullOrEmpty(_warnOverwriteSerial)) return;
            bool warn = _warnOverwriteCheck.IsChecked == true;
            if (RadioConfig.LoadForRadio(_warnOverwriteSerial).WarnBeforeSliceRecordingOverwritten == warn) return;
            if (!RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(_warnOverwriteSerial, warn))
                JJTrace.Tracing.TraceLine("Settings: the overwrite warning setting could not be saved",
                    System.Diagnostics.TraceLevel.Warning);
        }
    }
}
