using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JJTrace;
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
            public SmartLinkIntents SmartLinkIntent;
            /// <summary>Whose radio this is (Sprint 31 Track S, #94).</summary>
            public RadioOwnership Ownership;
            /// <summary>"Change nothing on this radio" — the per-radio hold on
            /// every write to state the radio keeps (#403).</summary>
            public bool ChangeNothing;
            /// <summary>Read this radio's S-meter in dBm rather than S-units
            /// (Sprint 38 Track C, #337).</summary>
            public bool SmeterInDbm;
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
        ///
        /// <para>Declared in <see cref="Radios.AdvisoryKeys"/> since Sprint 36
        /// (#267), where the key and the words that name it in Settings are
        /// written in one expression. A bare const here could be silenced and
        /// never described again.</para>
        /// </summary>
        private static Radios.AdvisoryKey NoPhysicalAccessCascadeKey
            => Radios.AdvisoryKeys.NoPhysicalAccessCascade;

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
                        Display = string.IsNullOrEmpty(cfg.DisplayName)
                            ? id
                            : Lexicon.Get("settings.profile.picker_named",
                                ("displayName", cfg.DisplayName), ("id", id))
                    });
                }
            }

            // The connected radio belongs in the list even before its first
            // saved profile.
            var connected = _rig?.ConnectedSerial;
            if (!string.IsNullOrEmpty(connected) && items.All(i => i.Id != connected))
            {
                items.Insert(0, new RadioProfileItem
                {
                    Id = connected,
                    Display = Lexicon.Get("settings.profile.picker_connected", ("connected", connected)),
                });
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
                RadioProfileStatusText.Text = Lexicon.Get("settings.profile.none_known");
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
                SmartLinkIntent =
                    (SmartLinkIntents)Math.Max(0, RadioProfileSmartLinkIntentCombo.SelectedIndex),
                Ownership =
                    (RadioOwnership)Math.Max(0, RadioProfileOwnershipCombo.SelectedIndex),
                ChangeNothing = RadioProfileChangeNothingCheck.IsChecked == true,
                SmeterInDbm = RadioProfileSmeterUnitsCombo.SelectedIndex == 1,
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
                RadioProfileSmartLinkIntentCombo.SelectedIndex =
                    (int)(stashed?.SmartLinkIntent ?? cfg.SmartLinkIntent);

                // Ownership loads as SAVED, never as a guess (#94). The
                // no-physical-access box below pre-populates from a guess and
                // says so, and that is right for it — wrongly guessing
                // "reachable" only costs a suppressed warning. Guessing wrong
                // here would pre-arm writes to a radio that is not the
                // operator's, so this control shows exactly what was declared
                // and nothing else. The suggestion has a home: the ask dialog,
                // where it appears as a sentence saying it is a guess.
                RadioProfileOwnershipCombo.SelectedIndex =
                    (int)(stashed?.Ownership ?? cfg.Ownership);

                // The change-nothing hold (#403). Loads as saved, never as a
                // guess — pre-arming a hold the operator did not set would be
                // as wrong here as pre-arming writes would be above.
                RadioProfileChangeNothingCheck.IsChecked =
                    stashed?.ChangeNothing ?? cfg.ChangeNothingOnThisRadio;

                // S-meter unit (#337). Index 1 is dBm; the stored default,
                // false, is S-units and index 0. Loaded from the SAME per-radio
                // field the leader chord and the Operations menu write, so this
                // control can never show a unit the meter is not reading in.
                RadioProfileSmeterUnitsCombo.SelectedIndex =
                    (stashed?.SmeterInDbm ?? cfg.SmeterInDbm) ? 1 : 0;

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
                    RadioProfileNoPhysicalAccessHint.Text =
                        Lexicon.Get("settings.profile.no_physical_access_hint_saved");
                }
                else if (guess)
                {
                    RadioProfileNoPhysicalAccessHint.Text =
                        Lexicon.Get("settings.profile.no_physical_access_hint_guess_remote");
                }
                else
                {
                    RadioProfileNoPhysicalAccessHint.Text =
                        Lexicon.Get("settings.profile.no_physical_access_hint_guess_local");
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
                RadioConnectionPreference.ForwardOnly =>
                    Lexicon.Get("settings.profile.describe_mode_forward_only"),
                RadioConnectionPreference.HolePunch =>
                    Lexicon.Get("settings.profile.describe_mode_hole_punch"),
                _ => Lexicon.Get("settings.profile.describe_mode_automatic"),
            };
            string port = cfg.FixedHolePunchPort > 0
                ? Lexicon.Get("settings.profile.describe_fixed_punch_port",
                    ("port", cfg.FixedHolePunchPort))
                : "";
            // The waivers only speak when set — silence means the safe default,
            // and reciting two "not allowed" clauses on every radio would bury
            // the interesting part.
            string waivers =
                cfg.AllowRemotePortChanges && cfg.AllowRemoteFirmwareUpdates
                    ? Lexicon.Get("settings.profile.describe_waivers_both")
                : cfg.AllowRemotePortChanges
                    ? Lexicon.Get("settings.profile.describe_waivers_port")
                : cfg.AllowRemoteFirmwareUpdates
                    ? Lexicon.Get("settings.profile.describe_waivers_firmware")
                : "";
            string reach = cfg.NoPhysicalAccessDecided && cfg.NoPhysicalAccess
                ? Lexicon.Get("settings.profile.describe_no_physical_access")
                : "";
            string remOn = cfg.RemOnOnConnect switch
            {
                RemOnOnConnectModes.TurnOn =>
                    Lexicon.Get("settings.profile.describe_rem_on_turn_on"),
                RemOnOnConnectModes.TurnOff =>
                    Lexicon.Get("settings.profile.describe_rem_on_turn_off"),
                _ => "",
            };
            // Only the decided states speak. Undecided is the default on every
            // radio nobody has answered for, and reciting "not answered yet"
            // on each one would bury the radios that HAVE an answer.
            string smartLink = cfg.SmartLinkIntent switch
            {
                SmartLinkIntents.LocalOnly =>
                    Lexicon.Get("settings.profile.describe_smartlink_local_only"),
                SmartLinkIntents.WantsSmartLink =>
                    Lexicon.Get("settings.profile.describe_smartlink_wants"),
                _ => "",
            };
            // Same rule as the two above: only the answered states speak.
            // "Nobody has said whose this is" on every radio would bury the
            // ones that have an answer.
            string owned = cfg.Ownership switch
            {
                RadioOwnership.Mine => Lexicon.Get("settings.profile.describe_owned_mine"),
                RadioOwnership.SomeoneElses =>
                    Lexicon.Get("settings.profile.describe_owned_someone_elses"),
                _ => "",
            };
            // Same rule again: only the non-default speaks. S-units is what
            // every radio reads in until somebody says otherwise, and saying
            // so on each one would bury the radio that is set to dBm.
            string smeterUnits = cfg.SmeterInDbm
                ? Lexicon.Get("settings.profile.describe_smeter_dbm")
                : "";
            // Same rule as everything above: only the armed state speaks.
            string changeNothing = cfg.ChangeNothingOnThisRadio
                ? Lexicon.Get("settings.profile.describe_change_nothing")
                : "";
            return Lexicon.Get("settings.profile.describe",
                ("mode", mode), ("port", port), ("waivers", waivers), ("reach", reach),
                ("remOn", remOn), ("smartLink", smartLink), ("owned", owned),
                ("smeterUnits", smeterUnits), ("changeNothing", changeNothing));
        }

        private void RadioProfileMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            // "Press Apply or OK to keep it" matches the other controls here: a
            // change that SOUNDS applied but quietly needs a commit is the
            // dishonest-speech pattern this dialog exists to avoid.
            string announcement =
                RadioProfilePunchRadio?.IsChecked == true
                    ? Lexicon.Get("settings.profile.mode_hole_punch") :
                RadioProfileForwardRadio?.IsChecked == true
                    ? Lexicon.Get("settings.profile.mode_forward_only") :
                Lexicon.Get("settings.profile.mode_automatic");
            ScreenReaderOutput.Speak(
                announcement + " " + Lexicon.Get("settings.profile.press_apply_or_ok"),
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
                    Lexicon.Get("settings.profile.remote_admin_not_allowed",
                        ("what", isPort
                            ? Lexicon.Get("settings.profile.remote_port_changes_label")
                            : Lexicon.Get("settings.profile.remote_firmware_updates_label"))) +
                    Lexicon.Get("settings.profile.press_apply_or_ok"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            // Enabling gets the full consequences up front (Noel, 2026-08-06):
            // the person flipping this may be granting it months before the day
            // it matters, and no warning repeats at use time. The text also
            // lands in the status line so it can be re-read, not just heard.
            string warning = isPort
                ? Lexicon.Get("settings.profile.remote_port_allowed_warning")
                : Lexicon.Get("settings.profile.remote_firmware_allowed_warning");
            RadioProfileStatusText.Text = warning;
            ScreenReaderOutput.Speak(warning, VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// The change-nothing hold (#403). Arming gets the full consequences
        /// up front, in the status line as well as speech, following the
        /// remote-administration checkboxes above — the person arming it may
        /// be doing so minutes before the connect where it matters, and no
        /// warning repeats at use time (the connect announcement does).
        /// </summary>
        private void RadioProfileChangeNothing_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;

            bool on = RadioProfileChangeNothingCheck.IsChecked == true;
            string text = Lexicon.Get(on
                ? "settings.profile.change_nothing_on_warning"
                : "settings.profile.change_nothing_off");
            RadioProfileStatusText.Text = text;
            ScreenReaderOutput.Speak(text, VerbosityLevel.Terse, interrupt: true);
        }

        private void RadioProfileRemOnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            string what = RadioProfileRemOnCombo.SelectedIndex switch
            {
                (int)RemOnOnConnectModes.TurnOn =>
                    Lexicon.Get("settings.profile.rem_on_turn_on"),
                (int)RemOnOnConnectModes.TurnOff =>
                    Lexicon.Get("settings.profile.rem_on_turn_off"),
                _ => Lexicon.Get("settings.profile.rem_on_leave_alone"),
            };
            ScreenReaderOutput.Speak(
                what + " " + Lexicon.Get("settings.profile.press_apply_or_ok"),
                VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// The S-meter's unit for this radio (#337). Says which unit was
        /// chosen, in the same words the leader chord and the Operations menu
        /// use, so the three surfaces cannot teach three vocabularies for one
        /// switch.
        /// </summary>
        private void RadioProfileSmeterUnitsCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            string what = RadioProfileSmeterUnitsCombo.SelectedIndex == 1
                ? Lexicon.Get("audio.smeter.in_dbm")
                : Lexicon.Get("audio.smeter.in_s_units");
            ScreenReaderOutput.Speak(
                what + ". " + Lexicon.Get("settings.profile.press_apply_or_ok"),
                VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// The local-only answer, edited after the fact (Sprint 30 Track A).
        /// A connect offers this question once; this is where the answer lives
        /// afterwards, and where it can be taken back.
        /// </summary>
        private void RadioProfileSmartLinkIntentCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            string what = RadioProfileSmartLinkIntentCombo.SelectedIndex switch
            {
                (int)SmartLinkIntents.LocalOnly =>
                    Lexicon.Get("settings.profile.smartlink_local_only"),
                (int)SmartLinkIntents.WantsSmartLink =>
                    Lexicon.Get("settings.profile.smartlink_wants"),
                _ => Lexicon.Get("settings.profile.smartlink_unanswered"),
            };
            ScreenReaderOutput.Speak(
                what + " " + Lexicon.Get("settings.profile.press_apply_or_ok"),
                VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Whose radio this is (Sprint 31 Track S, #94). The standing surface
        /// for an answer that is otherwise only asked at the moment an action
        /// needs it — and, just as importantly, the place to take it back.
        ///
        /// <para>No cascade, no confirmation, no warning about marking a radio
        /// that is not yours. Ownership is a declaration of intent rather than
        /// a security control; challenging the operator here would be the app
        /// pretending to verify something it cannot see, and would tax the
        /// honest answer to inconvenience a dishonest one for about two
        /// seconds.</para>
        /// </summary>
        private void RadioProfileOwnershipCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRadioProfileEvents) return;
            string what = RadioProfileOwnershipCombo.SelectedIndex switch
            {
                (int)RadioOwnership.Mine =>
                    Lexicon.Get("settings.profile.ownership_mine"),
                (int)RadioOwnership.SomeoneElses =>
                    Lexicon.Get("settings.profile.ownership_someone_elses"),
                _ =>
                    Lexicon.Get("settings.profile.ownership_unanswered"),
            };
            ScreenReaderOutput.Speak(
                what + " " + Lexicon.Get("settings.profile.press_apply_or_ok"),
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
            RadioProfileNoPhysicalAccessHint.Text =
                Lexicon.Get("settings.profile.no_physical_access_hint_pending");

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
                string done = Lexicon.Get("settings.profile.cascade_on_already_set");
                RadioProfileStatusText.Text = done;
                ScreenReaderOutput.Speak(done, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var warnings = new List<string>();
            if (needRemOn)
                warnings.Add(Lexicon.Get("settings.profile.cascade_on_warning_rem_on"));
            if (needPort)
                warnings.Add(Lexicon.Get("settings.profile.cascade_on_warning_port"));
            if (needFw)
                warnings.Add(Lexicon.Get("settings.profile.cascade_on_warning_firmware"));

            bool proceed;
            if (AdvisorySuppression.IsSuppressed(NoPhysicalAccessCascadeKey))
            {
                proceed = true;
            }
            else
            {
                var confirm = new ConfirmActionDialog(
                    Lexicon.Get("settings.profile.cascade_on_confirm_title"),
                    Lexicon.Get("settings.profile.cascade_on_confirm_body"),
                    warnings,
                    question: Lexicon.Get("settings.profile.cascade_on_confirm_question"),
                    yesLabel: Lexicon.Get("settings.profile.cascade_on_confirm_yes"),
                    noLabel: Lexicon.Get("settings.profile.cascade_on_confirm_no"),
                    suppressKey: NoPhysicalAccessCascadeKey)
                {
                    Owner = this,
                };
                proceed = confirm.ShowDialog() == true;
            }

            if (!proceed)
            {
                string kept = Lexicon.Get("settings.profile.cascade_on_declined");
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
                    changed.Add(Lexicon.Get("settings.profile.cascade_on_changed_rem_on"));
                }
                if (needPort)
                {
                    RadioProfileAllowRemotePortCheck.IsChecked = true;
                    changed.Add(Lexicon.Get("settings.profile.cascade_on_changed_port"));
                }
                if (needFw)
                {
                    RadioProfileAllowRemoteFirmwareCheck.IsChecked = true;
                    changed.Add(Lexicon.Get("settings.profile.cascade_on_changed_firmware"));
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
                Lexicon.Get("settings.profile.cascade_on_receipt_title"),
                Lexicon.Get("settings.profile.cascade_on_receipt_lead")
                + string.Join("\n", changed)
                + Lexicon.Get("settings.profile.cascade_on_receipt_tail"));

            RadioProfileStatusText.Text =
                Lexicon.Get("settings.profile.cascade_on_status_lead")
                + string.Join(" ", changed)
                + Lexicon.Get("settings.profile.cascade_status_tail");
        }

        private void RunNoPhysicalAccessCascadeOff()
        {
            bool revRemOn = RadioProfileRemOnCombo.SelectedIndex == (int)RemOnOnConnectModes.TurnOn;
            bool revPort = RadioProfileAllowRemotePortCheck.IsChecked == true;
            bool revFw = RadioProfileAllowRemoteFirmwareCheck.IsChecked == true;

            if (!revRemOn && !revPort && !revFw)
            {
                string done = Lexicon.Get("settings.profile.cascade_off_nothing_to_undo");
                RadioProfileStatusText.Text = done;
                ScreenReaderOutput.Speak(done, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var warnings = new List<string>();
            if (revRemOn)
                warnings.Add(Lexicon.Get("settings.profile.cascade_off_warning_rem_on"));
            if (revPort)
                warnings.Add(Lexicon.Get("settings.profile.cascade_off_warning_port"));
            if (revFw)
                warnings.Add(Lexicon.Get("settings.profile.cascade_off_warning_firmware"));

            bool proceed;
            if (AdvisorySuppression.IsSuppressed(NoPhysicalAccessCascadeKey))
            {
                proceed = true;
            }
            else
            {
                var confirm = new ConfirmActionDialog(
                    Lexicon.Get("settings.profile.cascade_off_confirm_title"),
                    Lexicon.Get("settings.profile.cascade_off_confirm_body"),
                    warnings,
                    question: Lexicon.Get("settings.profile.cascade_off_confirm_question"),
                    yesLabel: Lexicon.Get("settings.profile.cascade_off_confirm_yes"),
                    noLabel: Lexicon.Get("settings.profile.cascade_off_confirm_no"),
                    suppressKey: NoPhysicalAccessCascadeKey)
                {
                    Owner = this,
                };
                proceed = confirm.ShowDialog() == true;
            }

            if (!proceed)
            {
                string kept = Lexicon.Get("settings.profile.cascade_off_declined");
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
                    changed.Add(Lexicon.Get("settings.profile.cascade_off_changed_rem_on"));
                }
                if (revPort)
                {
                    RadioProfileAllowRemotePortCheck.IsChecked = false;
                    changed.Add(Lexicon.Get("settings.profile.cascade_off_warning_port"));
                }
                if (revFw)
                {
                    RadioProfileAllowRemoteFirmwareCheck.IsChecked = false;
                    changed.Add(Lexicon.Get("settings.profile.cascade_off_warning_firmware"));
                }
            }
            finally
            {
                _suppressRadioProfileEvents = false;
            }

            AdvisoryDialog.Show(
                Lexicon.Get("settings.profile.cascade_off_receipt_title"),
                Lexicon.Get("settings.profile.cascade_off_receipt_lead")
                + string.Join("\n", changed)
                + Lexicon.Get("settings.profile.cascade_off_receipt_tail"));

            RadioProfileStatusText.Text =
                Lexicon.Get("settings.profile.cascade_off_status_lead")
                + string.Join(" ", changed)
                + Lexicon.Get("settings.profile.cascade_status_tail");
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
                        Lexicon.Get("settings.profile.punch_port_invalid", ("radioId", radioId));
                    ScreenReaderOutput.Speak(Lexicon.Get("settings.profile.punch_port_invalid_spoken"),
                        VerbosityLevel.Terse, interrupt: true);
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
                string disp = string.IsNullOrEmpty(cfg.DisplayName)
                    ? radioId
                    : Lexicon.Get("settings.profile.picker_named",
                        ("displayName", cfg.DisplayName), ("id", radioId));
                var notesQueued = new List<string>();
                var notesApplied = new List<string>();
                bool changed = false;

                // The validation pass above already rejected anything that is
                // not blank or a number in 1024-65535, so this parse cannot
                // fail in practice. That guarantee lives in another method
                // though, and 0 here would silently mean "no fixed port" — so
                // if it ever DOES fail, say so rather than quietly writing a
                // port the operator never chose.
                int punchPort = 0;
                if (!string.IsNullOrEmpty(edit.PunchPortText)
                    && !int.TryParse(edit.PunchPortText, out punchPort))
                {
                    Tracing.TraceLine(
                        $"RadioProfile: hole-punch port '{edit.PunchPortText}' reached the "
                        + "commit step unparseable — the validation pass should have caught "
                        + "this. Writing 0 (no fixed port).",
                        System.Diagnostics.TraceLevel.Error);
                }

                if (cfg.ConnectionPreference != edit.Preference || cfg.FixedHolePunchPort != punchPort)
                {
                    cfg.ConnectionPreference = edit.Preference;
                    cfg.FixedHolePunchPort = punchPort;
                    changed = true;
                    notesQueued.Add(Lexicon.Get("settings.profile.saved_connection_settings",
                        ("disp", disp)));
                }

                if (cfg.AllowRemotePortChanges != edit.AllowRemotePort)
                {
                    cfg.AllowRemotePortChanges = edit.AllowRemotePort;
                    changed = true;
                    notesApplied.Add(Lexicon.Get("settings.profile.saved_remote_port",
                        ("disp", disp),
                        ("state", edit.AllowRemotePort
                            ? Lexicon.Get("settings.profile.word_allowed")
                            : Lexicon.Get("settings.profile.word_not_allowed"))));
                }
                if (cfg.AllowRemoteFirmwareUpdates != edit.AllowRemoteFirmware)
                {
                    cfg.AllowRemoteFirmwareUpdates = edit.AllowRemoteFirmware;
                    changed = true;
                    notesApplied.Add(Lexicon.Get("settings.profile.saved_remote_firmware",
                        ("disp", disp),
                        ("state", edit.AllowRemoteFirmware
                            ? Lexicon.Get("settings.profile.word_allowed")
                            : Lexicon.Get("settings.profile.word_not_allowed"))));
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
                        ? Lexicon.Get("settings.profile.saved_no_physical_access", ("disp", disp))
                        : Lexicon.Get("settings.profile.saved_reachable_in_person", ("disp", disp)));
                }

                if (cfg.SmartLinkIntent != edit.SmartLinkIntent)
                {
                    cfg.SmartLinkIntent = edit.SmartLinkIntent;
                    changed = true;
                    notesApplied.Add(edit.SmartLinkIntent switch
                    {
                        SmartLinkIntents.LocalOnly =>
                            Lexicon.Get("settings.profile.saved_smartlink_local_only", ("disp", disp)),
                        SmartLinkIntents.WantsSmartLink =>
                            Lexicon.Get("settings.profile.saved_smartlink_wants", ("disp", disp)),
                        _ => Lexicon.Get("settings.profile.saved_smartlink_unanswered", ("disp", disp)),
                    });
                }

                if (cfg.Ownership != edit.Ownership)
                {
                    cfg.Ownership = edit.Ownership;
                    changed = true;
                    notesApplied.Add(edit.Ownership switch
                    {
                        RadioOwnership.Mine =>
                            Lexicon.Get("settings.profile.saved_owned_mine", ("disp", disp)),
                        RadioOwnership.SomeoneElses =>
                            Lexicon.Get("settings.profile.saved_owned_someone_elses", ("disp", disp)),
                        _ =>
                            Lexicon.Get("settings.profile.saved_owned_unanswered", ("disp", disp)),
                    });
                }

                // The change-nothing hold (#403). Applies NOW when this is the
                // radio in front of you — the rig caches the flag for its
                // writers, and a hold that waited for the next connect would
                // miss the session it was armed for.
                if (cfg.ChangeNothingOnThisRadio != edit.ChangeNothing)
                {
                    cfg.ChangeNothingOnThisRadio = edit.ChangeNothing;
                    changed = true;
                    if (connected) _rig!.SetChangeNothingActive(edit.ChangeNothing);
                    notesApplied.Add(Lexicon.Get(edit.ChangeNothing
                        ? "settings.profile.saved_change_nothing_on"
                        : "settings.profile.saved_change_nothing_off",
                        ("disp", disp)));
                }

                // S-meter unit (#337). Applies NOW when this is the radio in
                // front of you — assigning the rig's property is what writes
                // the same per-radio field, so the two cannot disagree — and
                // is simply stored for a radio you are not on. Either way the
                // next connect to that radio reads it back.
                if (cfg.SmeterInDbm != edit.SmeterInDbm)
                {
                    cfg.SmeterInDbm = edit.SmeterInDbm;
                    changed = true;
                    if (connected) _rig!.SmeterInDBM = edit.SmeterInDbm;
                    notesApplied.Add(Lexicon.Get("settings.profile.saved_smeter_units",
                        ("disp", disp),
                        ("units", edit.SmeterInDbm
                            ? Lexicon.Get("settings.profile.smeter_unit_dbm")
                            : Lexicon.Get("settings.profile.smeter_unit_s_units"))));
                }

                if (cfg.RemOnOnConnect != edit.RemOn)
                {
                    cfg.RemOnOnConnect = edit.RemOn;
                    changed = true;
                    if (edit.RemOn == RemOnOnConnectModes.LeaveAlone)
                    {
                        notesApplied.Add(Lexicon.Get("settings.profile.saved_rem_on_leave_alone",
                            ("disp", disp)));
                    }
                    else
                    {
                        bool wantOn = edit.RemOn == RemOnOnConnectModes.TurnOn;
                        if (connected && _rig!.ChangeNothingActive)
                        {
                            // The hold refuses the write (and the rig says so
                            // by name). The intent is still SAVED — it applies
                            // once the hold is lifted — and the note must say
                            // that, not claim a change that did not happen.
                            notesQueued.Add(Lexicon.Get("settings.profile.saved_rem_on_held",
                                ("disp", disp),
                                ("state", wantOn
                                    ? Lexicon.Get("settings.profile.word_on")
                                    : Lexicon.Get("settings.profile.word_off"))));
                        }
                        else if (connected)
                        {
                            // The radio is right here — a queued intent that can
                            // apply now, applies now, and keeps applying at each
                            // future connect.
                            _rig!.RemoteOnEnabled = wantOn;
                            notesApplied.Add(Lexicon.Get("settings.profile.saved_rem_on_applied_now",
                                ("disp", disp),
                                ("state", wantOn
                                    ? Lexicon.Get("settings.profile.word_on")
                                    : Lexicon.Get("settings.profile.word_off"))));
                        }
                        else
                        {
                            notesQueued.Add(Lexicon.Get("settings.profile.saved_rem_on_queued",
                                ("disp", disp),
                                ("state", wantOn
                                    ? Lexicon.Get("settings.profile.word_on")
                                    : Lexicon.Get("settings.profile.word_off")))
                                + (wantOn
                                    ? Lexicon.Get("settings.profile.saved_rem_on_queued_relay_note")
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
                    notesApplied.Add(Lexicon.Get("settings.profile.name_box_empty_kept",
                        ("disp", disp), ("displayName", cfg.DisplayName)));
                    if (string.Equals(_currentProfileRadioId, radioId, StringComparison.OrdinalIgnoreCase))
                        RadioProfileNicknameBox.Text = cfg.DisplayName;
                }
                else if (nicknameDirty && edit.NicknameText.Length > 0)
                {
                    if (connected && _rig!.ChangeNothingActive)
                    {
                        // Don't even ask the rig — its refusal would speak over
                        // the commit receipt for a question this note answers
                        // better: the LIST name (app-side) still changes, the
                        // radio's own name stands, and the reason has a name.
                        notesApplied.Add(Lexicon.Get("settings.profile.rename_held",
                            ("disp", disp), ("newName", edit.NicknameText)));
                    }
                    else if (connected)
                    {
                        if (_rig!.RenameRadio(edit.NicknameText))
                            notesApplied.Add(Lexicon.Get("settings.radio.name.renamed",
                                ("newName", edit.NicknameText)));
                        else
                            notesApplied.Add(
                                Lexicon.Get("settings.profile.rename_failed_locally_updated",
                                    ("disp", disp)));
                    }
                    else
                    {
                        notesQueued.Add(Lexicon.Get("settings.profile.rename_queued",
                            ("disp", disp), ("newName", edit.NicknameText)));
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
                    string failNote = Lexicon.Get("settings.profile.save_failed", ("disp", disp));
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
