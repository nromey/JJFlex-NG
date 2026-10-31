using System.Collections.Generic;
using System.Linq;
using System.Windows;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Audio → Alerts and CW Notifications → Alert sound delay
    /// (#855, step 4): the alert channel's output buffer as an operator
    /// setting, ruled by Noel 2026-10-30 01:44 with the default chosen by
    /// measurement (30 ms by 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four choices, not two numbers.</b> An operator choosing by ear wants
    /// "shorter" and "longer", and a pair of number boxes would invite the
    /// combinations nobody has measured. Each choice is a measured or
    /// conservative geometry; a file someone edited by hand to something else
    /// shows as its own fifth choice and is kept as it is unless another is
    /// picked. Whatever is chosen is clamped by
    /// <see cref="EarconPlayer.SetAlertBuffer"/> so it cannot stall the device.
    /// </para>
    /// <para>
    /// <b>Where it sits is Claude's choice, not a ruling:</b> just after the
    /// alert device, because it is a property of that device's queue.
    /// </para>
    /// <para>
    /// Committed on OK or Apply like its neighbours; the config's apply reopens
    /// the alert device with it at once, so no restart is needed.
    /// </para>
    /// </remarks>
    public partial class SettingsDialog
    {
        private CycleFieldControl? _alertBufferControl;
        private List<(int ms, int count)> _alertBufferChoices = new();

        /// <summary>The offered geometries, shortest first. 30 by 3 is the default.</summary>
        internal static readonly (int ms, int count, string key)[] AlertBufferPresets =
        {
            (20, 3, "audio.alert_buffer.shortest"),
            (EarconPlayer.DefaultAlertBufferMs, EarconPlayer.DefaultAlertBufferCount, "audio.alert_buffer.short_default"),
            (50, 3, "audio.alert_buffer.medium"),
            (100, 2, "audio.alert_buffer.long"),
        };

        /// <summary>Build the field from the saved setting. Called once, from the constructor.</summary>
        private void InitializeAlertBuffer()
        {
            // The label, the four choices and the Ctrl+F1 text are in
            // Radios/Lexicon/audio.json under audio.alert_buffer. The choices'
            // wording was ruled by Noel 2026-10-31: no trailing clauses.
            var saved = EarconPlayer.ClampAlertBuffer(_audioConfig.AlertBufferMs, _audioConfig.AlertBufferCount);
            var labels = new List<string>();
            _alertBufferChoices = new List<(int, int)>();
            foreach (var (ms, count, key) in AlertBufferPresets)
            {
                labels.Add(Lexicon.Get(key, ("ms", ms * count)));
                _alertBufferChoices.Add((ms, count));
            }
            int index = _alertBufferChoices.IndexOf(saved);
            if (index < 0)
            {
                labels.Add(Lexicon.Get("audio.alert_buffer.custom", ("ms", saved.ms * saved.count)));
                _alertBufferChoices.Add(saved);
                index = _alertBufferChoices.Count - 1;
            }

            _alertBufferControl = new CycleFieldControl { Margin = new Thickness(0, 0, 0, 8) };
            _alertBufferControl.Setup(Lexicon.Get("audio.alert_buffer.label"), labels.ToArray(), index);
            JJFlexHelp.SetText(_alertBufferControl, Lexicon.Get("audio.alert_buffer.help"));

            // Just after the alert device row.
            int at = AlertsPanel.Children.IndexOf(AlertDeviceRow);
            AlertsPanel.Children.Insert(at < 0 ? AlertsPanel.Children.Count : at + 1, _alertBufferControl);
        }

        /// <summary>The choice on screen, as a buffer geometry.</summary>
        internal (int ms, int count) ReadAlertBufferFromUi()
        {
            int i = _alertBufferControl?.SelectedIndex ?? -1;
            return i >= 0 && i < _alertBufferChoices.Count
                ? _alertBufferChoices[i]
                : (EarconPlayer.DefaultAlertBufferMs, EarconPlayer.DefaultAlertBufferCount);
        }

        /// <summary>Commit the field into the audio config. Called from ApplyAllSettings, before Apply.</summary>
        private void CommitAlertBuffer()
        {
            if (_alertBufferControl == null) return;
            (_audioConfig.AlertBufferMs, _audioConfig.AlertBufferCount) = ReadAlertBufferFromUi();
        }
    }
}
