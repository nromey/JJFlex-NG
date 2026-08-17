using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The Radios tab (barefoot-punch-pathfinder Phase 1b): everything about a
    /// particular radio, keyed by serial. Reachability belongs to the radio's
    /// site, not the operator's account, so this tab deliberately works with
    /// NO radio connected — its whole reason to exist is configuring how to
    /// reach a radio you cannot currently reach. The nickname is the one
    /// radio-side item here: saving it while connected to that radio renames
    /// the radio for real; offline it only updates the local label. The
    /// account-level tier group on the Network tab survives as the legacy
    /// fallback for radios without a profile.
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
                        Display = string.IsNullOrEmpty(cfg.DisplayName) ? id : $"{cfg.DisplayName} ({id})"
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
                RadioProfileAllowRemotePortCheck.IsChecked = cfg.AllowRemotePortChanges;
                RadioProfileAllowRemoteFirmwareCheck.IsChecked = cfg.AllowRemoteFirmwareUpdates;

                // The operator's chosen name wins when one exists — this box
                // edits the CHOICE (task #75: choices must not lose to
                // observations). With no choice made, prefer the live value
                // over the stored observation when this radio is connected.
                string nickname = !string.IsNullOrEmpty(cfg.UserNickname)
                    ? cfg.UserNickname
                    : IsConnectedTo(radioId) && !string.IsNullOrEmpty(_rig!.RadioNickname)
                        ? _rig.RadioNickname
                        : cfg.Nickname ?? string.Empty;
                RadioProfileNicknameBox.Text = nickname;
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
            // The waivers only speak when set — silence means the safe default,
            // and reciting two "not allowed" clauses on every radio would bury
            // the interesting part.
            string waivers =
                cfg.AllowRemotePortChanges && cfg.AllowRemoteFirmwareUpdates
                    ? " Remote port changes and remote firmware updates are allowed."
                : cfg.AllowRemotePortChanges
                    ? " Remote port changes are allowed."
                : cfg.AllowRemoteFirmwareUpdates
                    ? " Remote firmware updates are allowed."
                : "";
            return $"Profile: {mode}{port}.{waivers}";
        }

        private void RadioProfileMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            // "Choose save profile to keep it" matches the waiver toggles below:
            // a mode change that SOUNDS applied but quietly needs a save button
            // is the dishonest-speech pattern this dialog exists to avoid.
            string announcement =
                RadioProfilePunchRadio?.IsChecked == true ? "Hole punch always." :
                RadioProfileForwardRadio?.IsChecked == true ? "Forwarded ports only." :
                "Automatic, follow what the radio reports.";
            ScreenReaderOutput.Speak(announcement + " Choose save profile to keep it.",
                VerbosityLevel.Terse, interrupt: true);
        }

        private void RadioProfileRemoteAdmin_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            if (sender is not CheckBox box) return;

            bool on = box.IsChecked == true;
            bool isPort = ReferenceEquals(box, RadioProfileAllowRemotePortCheck);

            if (!on)
            {
                ScreenReaderOutput.Speak(
                    $"{(isPort ? "Remote port changes" : "Remote firmware updates")} not allowed. " +
                    "Choose save profile to keep it.",
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            // Enabling gets the full consequences up front (Noel, 2026-08-06):
            // the person flipping this may be granting it months before the day
            // it matters, and no warning repeats at use time. The text also
            // lands in the status line so it can be re-read, not just heard.
            string warning = isPort
                ? "Remote port changes allowed. Anyone who connects to this radio through its SmartLink " +
                  "account will be able to change its port settings from anywhere. If a change goes wrong " +
                  "while nobody is at the radio, hole punch remains the way back in. Choose save profile to keep it."
                : "Remote firmware updates allowed. Firmware can then be sent without anyone at the radio to " +
                  "confirm. An interrupted firmware update is the one thing that can leave a radio needing a " +
                  "service visit — and nobody will be there. Leave this off unless it is truly your only " +
                  "option. Choose save profile to keep it.";
            RadioProfileStatusText.Text = warning;
            ScreenReaderOutput.Speak(warning, VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// True when the live connection is to the radio the picker is showing —
        /// the condition under which radio-side items (the nickname) can be
        /// pushed to the radio itself rather than just stored locally.
        /// </summary>
        private bool IsConnectedTo(string radioId) =>
            _rig != null && _rig.IsConnected && _rig.SelectedRadioSerial == radioId;

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
            cfg.AllowRemotePortChanges = RadioProfileAllowRemotePortCheck.IsChecked == true;
            cfg.AllowRemoteFirmwareUpdates = RadioProfileAllowRemoteFirmwareCheck.IsChecked == true;

            // Nickname: the name lives on the radio, so push it there when the
            // live connection is to this radio; otherwise only the local label
            // changes, and the status text says so instead of implying more.
            // An emptied box deliberately does NOT blank the name — a radio
            // with no name shows as "Unknown" everywhere, and an accidental
            // clear is far more likely than a wish for that. But ignoring the
            // empty box silently would leave the UI disagreeing with reality,
            // so the kept name goes back into the box and the status says so.
            string newNickname = RadioProfileNicknameBox.Text?.Trim() ?? string.Empty;
            string renameNote = "";
            if (newNickname.Length == 0 && !string.IsNullOrEmpty(cfg.DisplayName))
            {
                RadioProfileNicknameBox.Text = cfg.DisplayName;
                renameNote =
                    $" The name box was empty, so the radio keeps its name, {cfg.DisplayName} — a radio with no name would show as Unknown everywhere.";
            }
            else if (newNickname.Length > 0 && newNickname != (cfg.DisplayName ?? string.Empty))
            {
                if (IsConnectedTo(radioId))
                {
                    renameNote = _rig!.RenameRadio(newNickname)
                        ? $" The radio is now named {newNickname}."
                        : " The radio itself could not be renamed; the name shown here was updated.";
                }
                else
                {
                    renameNote =
                        $" This radio will show as {newNickname} in JJ Flexible from now on, even when its own broadcast name differs; the radio itself keeps its old name until you save this while connected to it.";
                }
                // The typed name is a CHOICE. It survives sightings — the
                // observation field (Nickname) keeps tracking what the radio
                // broadcasts, and display prefers the choice.
                cfg.UserNickname = newNickname;
            }

            if (cfg.SaveForRadio(radioId))
            {
                RadioProfileStatusText.Text =
                    $"Saved. {DescribeRadioProfile(cfg)} Applies from the next connection to this radio.{renameNote}";
                // The applies-on-next-connect clause is SPOKEN, not just written
                // to the status line: an offline edit that sounds live-applied
                // would send someone hunting a change that hasn't happened yet.
                ScreenReaderOutput.Speak(
                    (renameNote.Length > 0 ? "Saved." + renameNote : "Profile saved.") +
                    " Applies from the next connection to this radio.",
                    VerbosityLevel.Terse, interrupt: true);

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
