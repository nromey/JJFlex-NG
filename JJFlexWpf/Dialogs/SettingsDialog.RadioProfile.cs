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
    /// radio-side item here: applying it while connected to that radio renames
    /// the radio for real; offline it only updates the local label.
    ///
    /// Track C (settings that stick): the per-feature "Save profile" button is
    /// gone. Edits here are committed by the dialog's OK and Apply like every
    /// other setting — that is the fix for the radio name that did not save.
    /// Because the picker can move between radios mid-dialog, edits to a radio
    /// you navigated away from are stashed and committed together on OK/Apply;
    /// nothing typed on this tab is ever silently discarded except by Cancel.
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
        /// UI snapshot of one radio's edits, captured when the picker moves off
        /// the radio (and folded in again at commit time). Holding these in
        /// memory until OK/Apply is what makes Cancel actually discard and OK
        /// actually keep — the two halves of the convention.
        /// </summary>
        private sealed class RadioProfileEdit
        {
            public RadioConnectionPreference Preference;
            public string PunchPortText = "";
            public bool AllowRemotePort;
            public bool AllowRemoteFirmware;
            public bool NoPhysicalAccess;
            public bool NoPhysicalAccessTouched;
            public RemOnOnConnectModes RemOn;
            public string NicknameText = "";
            /// <summary>What the nickname box was loaded with, so "dirty" means
            /// "the user changed it", not "the stored mirror disagrees with the
            /// radio's live name".</summary>
            public string LoadedNickname = "";
        }

        /// <summary>
        /// True while this code is changing picker/mode controls itself, so the
        /// Checked/SelectionChanged handlers stay quiet — announcements are for
        /// the user's own actions, never for programmatic loads.
        /// </summary>
        private bool _suppressRadioProfileEvents;

        /// <summary>Radio id currently shown by the picker's detail controls.</summary>
        private string? _currentProfileRadioId;

        /// <summary>Nickname the current radio's box was loaded with.</summary>
        private string _currentProfileLoadedNickname = "";

        /// <summary>Whether the user toggled the no-physical-access box for the
        /// current radio this session (as opposed to the pre-populated guess).</summary>
        private bool _currentProfileNoPhysTouched;

        /// <summary>Edits for radios the picker has moved away from, keyed by
        /// radio id. Committed together on OK/Apply; discarded on Cancel.</summary>
        private readonly Dictionary<string, RadioProfileEdit> _pendingProfileEdits
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Suppression key for the no-physical-access cascade's teaching prompt.
        /// The v1 bundle: REM ON turn-on-at-connect, allow remote port changes,
        /// allow remote firmware updates. BUMP THE VERSION whenever the bundle's
        /// contents change — otherwise "don't show again" quietly becomes "never
        /// tell me about new things you do to my radio." The receipt dialog is
        /// never suppressed; only this explanation is.
        /// </summary>
        private const string NoPhysicalAccessCascadeKey = "no-physical-access-cascade-v1";

        private void RadioProfileSection_Loaded(object sender, RoutedEventArgs e)
        {
            // Loaded fires again on every return to this tab, and the reload
            // below rewrites the controls. Stash first, or edits made before a
            // tab switch would be clobbered on the way back — the silent-discard
            // defect this track exists to end. (The pre-Track-C tab actually
            // had that bug: switching tabs re-ran the load and lost edits.)
            StashCurrentProfileEdit();

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
                    "You can also type a radio's serial number above and press Apply or OK.";
            }
        }

        private void RadioProfilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;

            // Moving between radios must not cost the edits made so far —
            // stash them; OK/Apply commits the lot, Cancel discards the lot.
            StashCurrentProfileEdit();

            if (RadioProfilePicker.SelectedItem is RadioProfileItem item)
            {
                LoadRadioProfileIntoUi(item.Id, announce: true);
            }
        }

        /// <summary>
        /// Snapshot the on-screen controls into the pending-edit stash for the
        /// radio currently shown. Cheap and unconditional; commit skips radios
        /// whose snapshot matches what is already saved.
        /// </summary>
        private void StashCurrentProfileEdit()
        {
            if (string.IsNullOrEmpty(_currentProfileRadioId)) return;
            _pendingProfileEdits[_currentProfileRadioId] = new RadioProfileEdit
            {
                Preference =
                    RadioProfilePunchRadio.IsChecked == true ? RadioConnectionPreference.HolePunch :
                    RadioProfileForwardRadio.IsChecked == true ? RadioConnectionPreference.ForwardOnly :
                    RadioConnectionPreference.Auto,
                PunchPortText = RadioProfilePunchPortBox.Text?.Trim() ?? "",
                AllowRemotePort = RadioProfileAllowRemotePortCheck.IsChecked == true,
                AllowRemoteFirmware = RadioProfileAllowRemoteFirmwareCheck.IsChecked == true,
                NoPhysicalAccess = RadioProfileNoPhysicalAccessCheck.IsChecked == true,
                NoPhysicalAccessTouched = _currentProfileNoPhysTouched,
                RemOn = (RemOnOnConnectModes)Math.Max(0, RadioProfileRemOnCombo.SelectedIndex),
                NicknameText = RadioProfileNicknameBox.Text?.Trim() ?? "",
                LoadedNickname = _currentProfileLoadedNickname,
            };
        }

        private void LoadRadioProfileIntoUi(string radioId, bool announce)
        {
            var cfg = RadioConfig.LoadForRadio(radioId);
            _pendingProfileEdits.TryGetValue(radioId, out var stashed);

            bool wasSuppressed = _suppressRadioProfileEvents;
            _suppressRadioProfileEvents = true;
            try
            {
                var pref = stashed?.Preference ?? cfg.ConnectionPreference;
                RadioProfileAutoRadio.IsChecked = pref == RadioConnectionPreference.Auto;
                RadioProfileForwardRadio.IsChecked = pref == RadioConnectionPreference.ForwardOnly;
                RadioProfilePunchRadio.IsChecked = pref == RadioConnectionPreference.HolePunch;

                RadioProfilePunchPortBox.Text = stashed?.PunchPortText
                    ?? (cfg.FixedHolePunchPort > 0 ? cfg.FixedHolePunchPort.ToString() : string.Empty);
                RadioProfileAllowRemotePortCheck.IsChecked = stashed?.AllowRemotePort ?? cfg.AllowRemotePortChanges;
                RadioProfileAllowRemoteFirmwareCheck.IsChecked = stashed?.AllowRemoteFirmware ?? cfg.AllowRemoteFirmwareUpdates;
                RadioProfileRemOnCombo.SelectedIndex = (int)(stashed?.RemOn ?? cfg.RemOnOnConnect);

                // No-physical-access: an explicit choice loads as saved (or as
                // stashed). Before any explicit choice, pre-populate from the
                // path chain — the guess only SHOWS; nothing treats it as a
                // decision until the operator touches it or applies. The
                // asymmetry that justifies pre-checking: wrongly guessing
                // "reachable" suppresses a warning that would have saved you;
                // wrongly guessing "remote" merely shows a prompt you did not
                // need.
                bool decided = cfg.NoPhysicalAccessDecided;
                bool guess = cfg.LastSeenRemote
                             || cfg.ConnectionPreference == RadioConnectionPreference.HolePunch;
                bool noPhys = stashed?.NoPhysicalAccess ?? (decided ? cfg.NoPhysicalAccess : guess);
                RadioProfileNoPhysicalAccessCheck.IsChecked = noPhys;
                _currentProfileNoPhysTouched = stashed?.NoPhysicalAccessTouched ?? false;
                if (decided || _currentProfileNoPhysTouched)
                {
                    RadioProfileNoPhysicalAccessHint.Text = "Your choice, saved for this radio.";
                }
                else if (guess)
                {
                    RadioProfileNoPhysicalAccessHint.Text =
                        "Pre-set to checked because this radio was last reached remotely. " +
                        "Override it if that is wrong — the guess only counts once you change or apply it.";
                }
                else
                {
                    RadioProfileNoPhysicalAccessHint.Text =
                        "Pre-set to unchecked because this radio was last seen on your own network. " +
                        "Override it if that is wrong.";
                }

                // The operator's chosen name wins when one exists — this box
                // edits the CHOICE (task #75: choices must not lose to
                // observations). With no choice made, prefer the live value
                // over the stored observation when this radio is connected.
                string loadedNickname = !string.IsNullOrEmpty(cfg.UserNickname)
                    ? cfg.UserNickname
                    : IsConnectedTo(radioId) && !string.IsNullOrEmpty(_rig!.RadioNickname)
                        ? _rig.RadioNickname
                        : cfg.Nickname ?? string.Empty;
                // ...and an in-flight edit outranks all of it: a stash held
                // across a tab switch is the operator mid-sentence, which no
                // stored or observed value may interrupt.
                _currentProfileLoadedNickname = stashed?.LoadedNickname ?? loadedNickname;
                RadioProfileNicknameBox.Text = stashed?.NicknameText ?? loadedNickname;
            }
            finally
            {
                _suppressRadioProfileEvents = wasSuppressed;
            }

            _currentProfileRadioId = radioId;

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
            string reach = cfg.NoPhysicalAccessDecided && cfg.NoPhysicalAccess
                ? " Marked as operated remotely, no physical access."
                : "";
            string remOn = cfg.RemOnOnConnect switch
            {
                RemOnOnConnectModes.TurnOn => " REM ON turns on at connect.",
                RemOnOnConnectModes.TurnOff => " REM ON turns off at connect.",
                _ => "",
            };
            return $"Profile: {mode}{port}.{waivers}{reach}{remOn}";
        }

        private void RadioProfileMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            // "Press Apply or OK to keep it" matches the other controls here: a
            // change that SOUNDS applied but quietly needs a commit is the
            // dishonest-speech pattern this dialog exists to avoid.
            string announcement =
                RadioProfilePunchRadio?.IsChecked == true ? "Hole punch always." :
                RadioProfileForwardRadio?.IsChecked == true ? "Forwarded ports only." :
                "Automatic, follow what the radio reports.";
            ScreenReaderOutput.Speak(announcement + " Press Apply or OK to keep it.",
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
                    "Press Apply or OK to keep it.",
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
                  "while nobody is at the radio, hole punch remains the way back in. Press Apply or OK to keep it."
                : "Remote firmware updates allowed. Firmware can then be sent without anyone at the radio to " +
                  "confirm. An interrupted firmware update is the one thing that can leave a radio needing a " +
                  "service visit — and nobody will be there. Leave this off unless it is truly your only " +
                  "option. Press Apply or OK to keep it.";
            RadioProfileStatusText.Text = warning;
            ScreenReaderOutput.Speak(warning, VerbosityLevel.Terse, interrupt: true);
        }

        private void RadioProfileRemOnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            string what = RadioProfileRemOnCombo.SelectedIndex switch
            {
                (int)RemOnOnConnectModes.TurnOn =>
                    "REM ON will be turned on when you connect. It only works if the RCA jack is wired to a relay.",
                (int)RemOnOnConnectModes.TurnOff =>
                    "REM ON will be turned off when you connect.",
                _ => "REM ON left as the radio has it.",
            };
            ScreenReaderOutput.Speak(what + " Press Apply or OK to keep it.",
                VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// The no-physical-access cascade (Track C, 2026-08-16). Checking the
        /// box offers the consequential settings a truly unreachable radio
        /// needs — enumerated by name and value, never a bare yes/no, because
        /// "yes" to an unnamed set is not informed consent and teaches nothing.
        /// Unchecking asks again and reverses only the settings still in their
        /// bundle state, so a hand-tuned value is never silently clobbered.
        /// The teaching prompt is globally suppressible (versioned key); the
        /// receipt afterwards is an OK-only dialog and always fires — a spoken
        /// message is ephemeral, never reaches braille, and can be cut off,
        /// and suppressing a receipt would be a silent change.
        /// </summary>
        private void RadioProfileNoPhysicalAccess_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            _currentProfileNoPhysTouched = true;
            RadioProfileNoPhysicalAccessHint.Text = "Your choice, saved when you press Apply or OK.";

            bool nowChecked = RadioProfileNoPhysicalAccessCheck.IsChecked == true;
            if (nowChecked)
                RunNoPhysicalAccessCascadeOn();
            else
                RunNoPhysicalAccessCascadeOff();
        }

        private void RunNoPhysicalAccessCascadeOn()
        {
            bool needRemOn = RadioProfileRemOnCombo.SelectedIndex != (int)RemOnOnConnectModes.TurnOn;
            bool needPort = RadioProfileAllowRemotePortCheck.IsChecked != true;
            bool needFw = RadioProfileAllowRemoteFirmwareCheck.IsChecked != true;

            if (!needRemOn && !needPort && !needFw)
            {
                string done = "No physical access noted. The settings it implies are already in place. " +
                              "Press Apply or OK to keep the flag.";
                RadioProfileStatusText.Text = done;
                ScreenReaderOutput.Speak(done, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var warnings = new List<string>();
            if (needRemOn)
                warnings.Add(
                    "REM ON when connecting: turn on. REM ON is the only remote way back from a powered-off " +
                    "radio. Hardware matters — it does nothing unless the REM ON RCA jack on the back panel " +
                    "is wired to a relay or keying device. If yours is not wired, this setting alone will " +
                    "not save you.");
            if (needPort)
                warnings.Add(
                    "Allow changing port settings from a remote connection: on. Without it, port settings " +
                    "can only be changed by someone at the radio — which for this radio is nobody.");
            if (needFw)
                warnings.Add(
                    "Allow firmware updates without someone at the radio: on. This only unlocks the option; " +
                    "every update still asks first. An interrupted update is the one thing that can leave a " +
                    "radio needing a service visit, so treat remote updates as a last resort.");

            bool proceed;
            if (AdvisorySuppression.IsSuppressed(NoPhysicalAccessCascadeKey))
            {
                proceed = true;
            }
            else
            {
                var confirm = new ConfirmActionDialog(
                    "A Radio Nobody Can Walk To",
                    "You marked this radio as operated remotely, with no one able to reach its front panel. " +
                    "A radio like that needs its safety net set up in advance — once something goes wrong, " +
                    "there is no walking over to fix it. JJ Flex suggests these settings for it:",
                    warnings,
                    question: "Set them now? They save when you press Apply or OK.",
                    yesLabel: "_Set them",
                    noLabel: "_Just the flag",
                    suppressKey: NoPhysicalAccessCascadeKey)
                {
                    Owner = this,
                };
                proceed = confirm.ShowDialog() == true;
            }

            if (!proceed)
            {
                string kept = "Marked as no physical access. The suggested settings were left unchanged. " +
                              "Press Apply or OK to keep the flag.";
                RadioProfileStatusText.Text = kept;
                ScreenReaderOutput.Speak(kept, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var changed = new List<string>();
            _suppressRadioProfileEvents = true;
            try
            {
                if (needRemOn)
                {
                    RadioProfileRemOnCombo.SelectedIndex = (int)RemOnOnConnectModes.TurnOn;
                    changed.Add("REM ON when connecting: turn on. Only works if the RCA jack is wired to a relay.");
                }
                if (needPort)
                {
                    RadioProfileAllowRemotePortCheck.IsChecked = true;
                    changed.Add("Allow changing port settings from a remote connection: on.");
                }
                if (needFw)
                {
                    RadioProfileAllowRemoteFirmwareCheck.IsChecked = true;
                    changed.Add("Allow firmware updates without someone at the radio: on.");
                }
            }
            finally
            {
                _suppressRadioProfileEvents = false;
            }

            // The receipt: a real dialog object in the tree — re-readable,
            // braille-reachable, acknowledged. Fires per radio, every time,
            // even when the teaching above was suppressed.
            AdvisoryDialog.Show(
                "No Physical Access — What Changed",
                "Because you marked this radio as operated remotely, these settings were changed for it:\n\n"
                + string.Join("\n", changed)
                + "\n\nNothing is saved yet — they take effect when you press Apply or OK. "
                + "REM ON is applied at the next connection to this radio.");

            RadioProfileStatusText.Text =
                "Marked as no physical access. " + string.Join(" ", changed) + " Press Apply or OK to keep it all.";
        }

        private void RunNoPhysicalAccessCascadeOff()
        {
            bool revRemOn = RadioProfileRemOnCombo.SelectedIndex == (int)RemOnOnConnectModes.TurnOn;
            bool revPort = RadioProfileAllowRemotePortCheck.IsChecked == true;
            bool revFw = RadioProfileAllowRemoteFirmwareCheck.IsChecked == true;

            if (!revRemOn && !revPort && !revFw)
            {
                string done = "No longer marked as no physical access. Nothing else needed changing. " +
                              "Press Apply or OK to keep it.";
                RadioProfileStatusText.Text = done;
                ScreenReaderOutput.Speak(done, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var warnings = new List<string>();
            if (revRemOn)
                warnings.Add("REM ON when connecting: back to leaving it as the radio has it.");
            if (revPort)
                warnings.Add("Allow changing port settings from a remote connection: off.");
            if (revFw)
                warnings.Add("Allow firmware updates without someone at the radio: off.");

            bool proceed;
            if (AdvisorySuppression.IsSuppressed(NoPhysicalAccessCascadeKey))
            {
                proceed = true;
            }
            else
            {
                var confirm = new ConfirmActionDialog(
                    "Reachable Again",
                    "You un-marked this radio as operated remotely. JJ Flex can put the settings that came " +
                    "with the mark back to their defaults. Only the ones still in the marked state are " +
                    "listed — anything you tuned by hand since is not touched:",
                    warnings,
                    question: "Put them back? They save when you press Apply or OK.",
                    yesLabel: "_Put them back",
                    noLabel: "_Leave them set",
                    suppressKey: NoPhysicalAccessCascadeKey)
                {
                    Owner = this,
                };
                proceed = confirm.ShowDialog() == true;
            }

            if (!proceed)
            {
                string kept = "No longer marked as no physical access. The remote-operation settings stay " +
                              "as they are. Press Apply or OK to keep the flag change.";
                RadioProfileStatusText.Text = kept;
                ScreenReaderOutput.Speak(kept, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var changed = new List<string>();
            _suppressRadioProfileEvents = true;
            try
            {
                if (revRemOn)
                {
                    RadioProfileRemOnCombo.SelectedIndex = (int)RemOnOnConnectModes.LeaveAlone;
                    changed.Add("REM ON when connecting: leave it as the radio has it.");
                }
                if (revPort)
                {
                    RadioProfileAllowRemotePortCheck.IsChecked = false;
                    changed.Add("Allow changing port settings from a remote connection: off.");
                }
                if (revFw)
                {
                    RadioProfileAllowRemoteFirmwareCheck.IsChecked = false;
                    changed.Add("Allow firmware updates without someone at the radio: off.");
                }
            }
            finally
            {
                _suppressRadioProfileEvents = false;
            }

            AdvisoryDialog.Show(
                "Reachable Again — What Changed",
                "Because you un-marked this radio as operated remotely, these settings were put back:\n\n"
                + string.Join("\n", changed)
                + "\n\nNothing is saved yet — they take effect when you press Apply or OK.");

            RadioProfileStatusText.Text =
                "No longer marked as no physical access. " + string.Join(" ", changed) +
                " Press Apply or OK to keep it all.";
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

        /// <summary>
        /// Commit every pending radio-profile edit (Track C: the save half of
        /// the radio-name bug — Track A owns the display half in PaintRoster).
        /// Called from the dialog's OK and Apply paths. Returns false on a
        /// validation error, after navigating focus to the offending field.
        /// Change notes land in <paramref name="queued"/> for things that wait
        /// for a connection and <paramref name="applied"/> for things done now.
        /// </summary>
        private bool CommitRadioProfiles(List<string> queued, List<string> applied)
        {
            // The editable picker lets a serial be typed for a radio that has
            // never connected from this machine. When the typed id differs
            // from the radio the controls were loaded for, the on-screen
            // values describe the TYPED radio — the same reading the
            // pre-Track-C Save button gave that flow.
            var typedId = CurrentPickerRadioId();
            if (!string.IsNullOrEmpty(typedId)
                && !string.Equals(typedId, _currentProfileRadioId, StringComparison.OrdinalIgnoreCase))
            {
                var hold = _currentProfileRadioId;
                _currentProfileRadioId = typedId;
                StashCurrentProfileEdit();
                _currentProfileRadioId = hold;
            }
            else
            {
                StashCurrentProfileEdit();
            }
            if (_pendingProfileEdits.Count == 0) return true;

            // Validation pass first, so nothing is half-committed when one
            // radio's port is bad.
            foreach (var (radioId, edit) in _pendingProfileEdits)
            {
                if (!string.IsNullOrEmpty(edit.PunchPortText)
                    && (!int.TryParse(edit.PunchPortText, out int p) || p < 1024 || p > 65535))
                {
                    SelectTabByHeader("Radios");
                    if (!string.Equals(_currentProfileRadioId, radioId, StringComparison.OrdinalIgnoreCase))
                    {
                        var item = (RadioProfilePicker.ItemsSource as IEnumerable<RadioProfileItem>)
                            ?.FirstOrDefault(i => i.Id == radioId);
                        if (item != null) RadioProfilePicker.SelectedItem = item;
                    }
                    RadioProfileStatusText.Text =
                        $"The fixed hole-punch port for {radioId} must be a number between 1024 and 65535, or blank.";
                    ScreenReaderOutput.Speak("Invalid hole-punch port.", VerbosityLevel.Terse, interrupt: true);
                    RadioProfilePunchPortBox.Focus();
                    return false;
                }
            }

            bool anySaved = false;
            var tabNotes = new List<string>();
            foreach (var (radioId, edit) in _pendingProfileEdits.ToList())
            {
                var cfg = RadioConfig.LoadForRadio(radioId);
                bool connected = IsConnectedTo(radioId);
                string disp = string.IsNullOrEmpty(cfg.DisplayName) ? radioId : $"{cfg.DisplayName} ({radioId})";
                var notesQueued = new List<string>();
                var notesApplied = new List<string>();
                bool changed = false;

                int punchPort = 0;
                if (!string.IsNullOrEmpty(edit.PunchPortText))
                    int.TryParse(edit.PunchPortText, out punchPort);

                if (cfg.ConnectionPreference != edit.Preference || cfg.FixedHolePunchPort != punchPort)
                {
                    cfg.ConnectionPreference = edit.Preference;
                    cfg.FixedHolePunchPort = punchPort;
                    changed = true;
                    notesQueued.Add($"{disp}: connection settings saved. They apply from the next connection to it.");
                }

                if (cfg.AllowRemotePortChanges != edit.AllowRemotePort)
                {
                    cfg.AllowRemotePortChanges = edit.AllowRemotePort;
                    changed = true;
                    notesApplied.Add($"{disp}: remote port changes {(edit.AllowRemotePort ? "allowed" : "not allowed")}.");
                }
                if (cfg.AllowRemoteFirmwareUpdates != edit.AllowRemoteFirmware)
                {
                    cfg.AllowRemoteFirmwareUpdates = edit.AllowRemoteFirmware;
                    changed = true;
                    notesApplied.Add($"{disp}: remote firmware updates {(edit.AllowRemoteFirmware ? "allowed" : "not allowed")}.");
                }

                // The no-physical-access flag becomes a decision only when the
                // operator touched it (or had decided before) — a pre-populated
                // guess that was never confirmed stays a guess.
                if ((edit.NoPhysicalAccessTouched || cfg.NoPhysicalAccessDecided)
                    && (cfg.NoPhysicalAccess != edit.NoPhysicalAccess || !cfg.NoPhysicalAccessDecided))
                {
                    cfg.NoPhysicalAccess = edit.NoPhysicalAccess;
                    cfg.NoPhysicalAccessDecided = true;
                    changed = true;
                    notesApplied.Add(edit.NoPhysicalAccess
                        ? $"{disp}: marked as operated remotely, no physical access."
                        : $"{disp}: marked as reachable in person.");
                }

                if (cfg.RemOnOnConnect != edit.RemOn)
                {
                    cfg.RemOnOnConnect = edit.RemOn;
                    changed = true;
                    if (edit.RemOn == RemOnOnConnectModes.LeaveAlone)
                    {
                        notesApplied.Add($"{disp}: REM ON will be left as the radio has it.");
                    }
                    else
                    {
                        bool wantOn = edit.RemOn == RemOnOnConnectModes.TurnOn;
                        if (connected)
                        {
                            // The radio is right here — a queued intent that can
                            // apply now, applies now, and keeps applying at each
                            // future connect.
                            _rig!.RemoteOnEnabled = wantOn;
                            notesApplied.Add($"{disp}: REM ON turned {(wantOn ? "on" : "off")} on the radio now, " +
                                "and will be re-checked at each connect.");
                        }
                        else
                        {
                            notesQueued.Add($"{disp}: REM ON will be turned {(wantOn ? "on" : "off")} at the next " +
                                "connection to it." + (wantOn
                                    ? " It only works if the REM ON RCA jack is wired to a relay."
                                    : ""));
                        }
                    }
                }

                // Nickname: dirty means the USER changed the box, not that the
                // stored mirror disagrees with the radio's live name. An emptied
                // box deliberately does NOT blank the name — a radio with no
                // name shows as "Unknown" everywhere, and an accidental clear is
                // far more likely than a wish for that.
                bool nicknameDirty = edit.NicknameText != edit.LoadedNickname;
                if (nicknameDirty && edit.NicknameText.Length == 0 && !string.IsNullOrEmpty(cfg.DisplayName))
                {
                    notesApplied.Add($"{disp}: the name box was empty, so the radio keeps its name, {cfg.DisplayName} — " +
                        "a radio with no name would show as Unknown everywhere.");
                    if (string.Equals(_currentProfileRadioId, radioId, StringComparison.OrdinalIgnoreCase))
                        RadioProfileNicknameBox.Text = cfg.DisplayName;
                }
                else if (nicknameDirty && edit.NicknameText.Length > 0)
                {
                    if (connected)
                    {
                        if (_rig!.RenameRadio(edit.NicknameText))
                            notesApplied.Add($"The radio is now named {edit.NicknameText}.");
                        else
                            notesApplied.Add($"{disp}: the radio itself could not be renamed; the name shown in " +
                                "the radio list was updated.");
                    }
                    else
                    {
                        notesQueued.Add($"{disp}: shows as {edit.NicknameText} in your radio list now. The radio " +
                            "itself keeps its old name until you apply this while connected to it.");
                    }
                    // The typed name is a CHOICE. It survives sightings — the
                    // observation field (Nickname) keeps tracking what the
                    // radio broadcasts, and display prefers the choice.
                    cfg.UserNickname = edit.NicknameText;
                    changed = true;
                }
                else if (connected && !string.IsNullOrEmpty(_rig!.RadioNickname)
                         && cfg.Nickname != _rig.RadioNickname)
                {
                    // Silent mirror refresh: the radio's live name is the truth
                    // about what it BROADCASTS; keeping the stored observation
                    // current is bookkeeping, not a user action, so it gets no
                    // note. Safe to do silently only because UserNickname holds
                    // the operator's choice separately — under a single-field
                    // model this line would quietly destroy a chosen name.
                    cfg.Nickname = _rig.RadioNickname;
                    changed = true;
                }

                if (!changed) continue;

                if (cfg.SaveForRadio(radioId))
                {
                    anySaved = true;
                    queued.AddRange(notesQueued);
                    applied.AddRange(notesApplied);
                    tabNotes.AddRange(notesApplied);
                    tabNotes.AddRange(notesQueued);
                }
                else
                {
                    string failNote = $"{disp}: the profile could not be saved. See the trace file for details.";
                    applied.Add(failNote);
                    tabNotes.Add(failNote);
                }
            }

            _pendingProfileEdits.Clear();

            if (anySaved)
            {
                // Refresh the picker (a typed-in radio just became a known one,
                // nicknames may have changed) without losing the selection or
                // re-announcing it.
                string? keep = _currentProfileRadioId;
                _suppressRadioProfileEvents = true;
                try
                {
                    PopulateRadioProfilePicker();
                    var again = (RadioProfilePicker.ItemsSource as IEnumerable<RadioProfileItem>)
                        ?.FirstOrDefault(i => i.Id == keep);
                    if (again != null) RadioProfilePicker.SelectedItem = again;
                }
                finally
                {
                    _suppressRadioProfileEvents = false;
                }
                if (!string.IsNullOrEmpty(keep))
                    LoadRadioProfileIntoUi(keep!, announce: false);
            }

            // The tab's own status line carries the re-readable record while
            // the dialog stays open (Apply-and-stay); the OK path additionally
            // gets the receipt dialog for anything still waiting.
            if (tabNotes.Count > 0)
                RadioProfileStatusText.Text = string.Join(" ", tabNotes);

            return true;
        }
    }
}
