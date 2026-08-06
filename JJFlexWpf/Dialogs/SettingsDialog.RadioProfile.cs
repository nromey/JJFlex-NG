using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Per-radio connection profile section of the Network tab
    /// (barefoot-punch-pathfinder Phase 1b). Reachability belongs to the
    /// radio's site, not the operator's account, so this section is keyed by
    /// radio serial and deliberately works with NO radio connected — its whole
    /// reason to exist is configuring how to reach a radio you cannot
    /// currently reach. The account-level tier group elsewhere on the tab
    /// survives as the legacy fallback for radios without a profile.
    /// </summary>
    public partial class SettingsDialog
    {
        private sealed class RadioProfileItem
        {
            public string Id = "";
            public string Display = "";
            public override string ToString() => Display;
        }

        /// <summary>
        /// True while this code is changing picker/mode controls itself, so the
        /// Checked/SelectionChanged handlers stay quiet — announcements are for
        /// the user's own actions, never for programmatic loads.
        /// </summary>
        private bool _suppressRadioProfileEvents;

        private void RadioProfileSection_Loaded(object sender, RoutedEventArgs e)
        {
            _suppressRadioProfileEvents = true;
            try
            {
                PopulateRadioProfilePicker();
            }
            finally
            {
                _suppressRadioProfileEvents = false;
            }

            if (RadioProfilePicker.SelectedItem is RadioProfileItem item)
            {
                LoadRadioProfileIntoUi(item.Id, announce: false);
            }
        }

        private void PopulateRadioProfilePicker()
        {
            var items = new List<RadioProfileItem>();
            var dir = RadioConfig.BaseDirectory;
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var id in RadioConfig.ListKnownRadioIds(dir))
                {
                    var cfg = RadioConfig.Load(dir, id);
                    items.Add(new RadioProfileItem
                    {
                        Id = id,
                        Display = string.IsNullOrEmpty(cfg.Nickname) ? id : $"{cfg.Nickname} ({id})"
                    });
                }
            }

            // The connected radio belongs in the list even before its first
            // saved profile.
            var connected = _rig?.ConnectedSerial;
            if (!string.IsNullOrEmpty(connected) && items.All(i => i.Id != connected))
            {
                items.Insert(0, new RadioProfileItem { Id = connected, Display = $"{connected} (connected)" });
            }

            RadioProfilePicker.ItemsSource = items;
            if (items.Count > 0)
            {
                RadioProfilePicker.SelectedItem =
                    items.FirstOrDefault(i => i.Id == connected) ?? items[0];
            }
            else
            {
                // An empty list must explain itself — a blank combo box reads
                // as broken, especially through a screen reader.
                RadioProfileStatusText.Text =
                    "No radios known yet. Radios are remembered here after you connect to them once. " +
                    "You can also type a radio's serial number above and choose Save profile.";
            }
        }

        private void RadioProfilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            if (RadioProfilePicker.SelectedItem is RadioProfileItem item)
            {
                LoadRadioProfileIntoUi(item.Id, announce: true);
            }
        }

        private void LoadRadioProfileIntoUi(string radioId, bool announce)
        {
            var cfg = RadioConfig.LoadForRadio(radioId);
            bool wasSuppressed = _suppressRadioProfileEvents;
            _suppressRadioProfileEvents = true;
            try
            {
                RadioProfileAutoRadio.IsChecked = cfg.ConnectionPreference == RadioConnectionPreference.Auto;
                RadioProfileForwardRadio.IsChecked = cfg.ConnectionPreference == RadioConnectionPreference.ForwardOnly;
                RadioProfilePunchRadio.IsChecked = cfg.ConnectionPreference == RadioConnectionPreference.HolePunch;
                RadioProfilePunchPortBox.Text = cfg.FixedHolePunchPort > 0
                    ? cfg.FixedHolePunchPort.ToString()
                    : string.Empty;
            }
            finally
            {
                _suppressRadioProfileEvents = wasSuppressed;
            }

            if (announce)
            {
                ScreenReaderOutput.Speak(DescribeRadioProfile(cfg), VerbosityLevel.Terse, interrupt: true);
            }
        }

        private static string DescribeRadioProfile(RadioConfig cfg)
        {
            string mode = cfg.ConnectionPreference switch
            {
                RadioConnectionPreference.ForwardOnly => "forwarded ports only",
                RadioConnectionPreference.HolePunch => "hole punch always",
                _ => "automatic",
            };
            string port = cfg.FixedHolePunchPort > 0
                ? $", fixed hole-punch port {cfg.FixedHolePunchPort}"
                : "";
            return $"Profile: {mode}{port}.";
        }

        private void RadioProfileMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            string announcement =
                RadioProfilePunchRadio?.IsChecked == true ? "Hole punch always." :
                RadioProfileForwardRadio?.IsChecked == true ? "Forwarded ports only." :
                "Automatic, follow what the radio reports.";
            ScreenReaderOutput.Speak(announcement, VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// The radio id currently named by the picker: the selected known item,
        /// or whatever the user typed (an editable combo lets a serial be
        /// entered for a radio that has never connected from this machine).
        /// </summary>
        private string? CurrentPickerRadioId()
        {
            if (RadioProfilePicker.SelectedItem is RadioProfileItem item) return item.Id;

            var text = RadioProfilePicker.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            var match = (RadioProfilePicker.ItemsSource as IEnumerable<RadioProfileItem>)
                ?.FirstOrDefault(i => i.Display == text || i.Id == text);
            return match?.Id ?? text;
        }

        private void SaveRadioProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var radioId = CurrentPickerRadioId();
            if (string.IsNullOrEmpty(radioId))
            {
                RadioProfileStatusText.Text = "Choose a radio, or type its serial number, before saving.";
                ScreenReaderOutput.Speak("No radio chosen.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            int port = 0;
            var portText = RadioProfilePunchPortBox.Text?.Trim();
            if (!string.IsNullOrEmpty(portText)
                && (!int.TryParse(portText, out port) || port < 1024 || port > 65535))
            {
                RadioProfileStatusText.Text = "The fixed hole-punch port must be a number between 1024 and 65535, or blank.";
                ScreenReaderOutput.Speak("Invalid port.", VerbosityLevel.Terse, interrupt: true);
                RadioProfilePunchPortBox.Focus();
                return;
            }

            var cfg = RadioConfig.LoadForRadio(radioId);
            cfg.ConnectionPreference =
                RadioProfilePunchRadio.IsChecked == true ? RadioConnectionPreference.HolePunch :
                RadioProfileForwardRadio.IsChecked == true ? RadioConnectionPreference.ForwardOnly :
                RadioConnectionPreference.Auto;
            cfg.FixedHolePunchPort = port;

            if (cfg.SaveForRadio(radioId))
            {
                RadioProfileStatusText.Text =
                    $"Saved. {DescribeRadioProfile(cfg)} Applies from the next connection to this radio.";
                ScreenReaderOutput.Speak("Profile saved.", VerbosityLevel.Terse, interrupt: true);

                // Refresh the picker (a typed-in radio just became a known one)
                // without losing the selection or re-announcing it.
                _suppressRadioProfileEvents = true;
                try
                {
                    PopulateRadioProfilePicker();
                    var again = (RadioProfilePicker.ItemsSource as IEnumerable<RadioProfileItem>)
                        ?.FirstOrDefault(i => i.Id == radioId);
                    if (again != null) RadioProfilePicker.SelectedItem = again;
                }
                finally
                {
                    _suppressRadioProfileEvents = false;
                }
            }
            else
            {
                RadioProfileStatusText.Text = "The profile could not be saved. See the trace file for details.";
                ScreenReaderOutput.Speak("Could not save the profile.", VerbosityLevel.Terse, interrupt: true);
            }
        }
    }
}
