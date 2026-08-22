using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, TX Audio tab: the microphone profile section — save,
/// load, apply and delete a named capture-plus-sculpt setup, plus the
/// silent-transmit note and its operator-initiated repair.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Microphone Profiles (Track F, 2026-08-16)

    // A microphone profile is MICROPHONE-first: "what does this mic need",
    // not "what does this radio remember" (see Radios.MicrophoneProfile for
    // the model rationale). The capture half — device identity, Windows
    // input level, boost, the gate to come — is applied here, because this
    // dialog owns the Windows endpoints. The radio half is a per-radio
    // binding: on a Flex it REFERENCES one of the radio's own mic profiles
    // by name (the radio already stores its TX chain; not ours to
    // duplicate), and only a radio with a binding gets touched at all —
    // which is exactly the guest-operator rule, falling out of the
    // architecture rather than being enforced by it.

    private static string NoMicProfilesOption => Lexicon.Get("audio.micprofile.none_saved_yet");

    // ── Ownership, and what it gates (Sprint 31 Track S, #94) ──
    //
    // Two destinations, two verbs. Saving a microphone profile stays PC-side
    // and is safe on anybody's radio — it is always offered, on every radio,
    // with no question asked. CREATING something on the radio itself is a
    // different act with a different name, and it is the one ownership gates.
    //
    // Applying an existing binding is deliberately NOT gated: the binding was
    // made by this operator, on this radio, earlier, and that is the consent.
    // See RadioConfig.MayCreateRadioSideState.

    /// <summary>How to name the current radio in a question about it.</summary>
    private string CurrentRadioLabel()
    {
        string serial = _rig?.SelectedRadioSerial ?? "";
        if (string.IsNullOrEmpty(serial)) return Lexicon.Get("audio.micprofile.this_radio");
        string name = RadioConfig.LoadForRadio(serial).DisplayName;
        if (string.IsNullOrWhiteSpace(name)) name = _rig?.RadioNickname ?? "";
        return string.IsNullOrWhiteSpace(name) ? Lexicon.Get("audio.micprofile.this_radio") : name;
    }

    /// <summary>
    /// May JJ Flex create new state on this radio right now? Asks once if the
    /// question has never been answered for it, and takes no for an answer.
    /// </summary>
    /// <remarks>
    /// The account the current session signed in with is deliberately NOT
    /// passed as the operator's own account. That is the exact derivation this
    /// design rejects: Noel connected to Margaret's radio using Margaret's
    /// account, so to SmartLink he WAS the owner, and feeding the session's
    /// account back in would make every radio suggest "yours". The suggestion
    /// falls back to a weaker, honest signal instead.
    /// </remarks>
    private bool MayCreateOnRadio(string radioId, string reason)
    {
        if (string.IsNullOrEmpty(radioId)) return false;
        var cfg = RadioConfig.LoadForRadio(radioId);
        if (cfg.Ownership == RadioOwnership.Mine) return true;
        if (cfg.Ownership == RadioOwnership.SomeoneElses) return false;
        return RadioOwnershipDialog.Ask(radioId, CurrentRadioLabel(), reason)
               == RadioOwnership.Mine;
    }

    private void BuildMicProfileSection()
    {
        AddSectionHeader(TxAudioContent, "Microphone Profiles");

        // The silent-transmit warning (#99) leads the section, because when it
        // applies it outranks everything below it: no microphone profile the
        // operator applies here can produce transmit audio while the radio has
        // no mic profile of its own loaded. Collapsed unless the radio reports
        // that state, so a working setup never sees it.
        _silentTxNote = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Margin = new Thickness(2),
            MinWidth = 300,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_silentTxNote, "Transmit audio warning");
        JJFlexHelp.SetText(_silentTxNote,
            "A Flex keeps its transmit-audio settings — mic gain, equaliser, "
            + "compander, processor — in named mic profiles that live on the "
            + "radio itself and are shared with every program connected to it. "
            + "When none of them is loaded, the transmit chain is unconfigured, "
            + "and audio arriving from this computer has nothing to travel "
            + "through. It is not a fault in your microphone, your levels, or "
            + "your network. A radio can come out of the box this way, and "
            + "loading a global profile that was saved without a mic profile "
            + "leaves it this way too. Other programs never show it because "
            + "they keep a profile named Default selected at all times.");
        AddToSection(TxAudioContent, _silentTxNote);

        // The repair, and it is a BUTTON on purpose (#94/#99). Loading a mic
        // profile writes ProfileMICSelection, which is shared radio state, so
        // it happens because the operator pressed something — never at connect,
        // never on its own, on any radio, owned or not.
        _silentTxFixButton = new Button
        {
            Content = "Load a Mic Profile on the Radio",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 4),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_silentTxFixButton, "Load a mic profile on the radio");
        JJFlexHelp.SetText(_silentTxFixButton,
            "Tells the radio to load one of the mic profiles it already has, "
            + "which is what gives transmit audio from this computer a chain to "
            + "travel through. It creates nothing — the profile already exists "
            + "on the radio. It does change a setting the radio shares with "
            + "every program connected to it, so on a radio JJ Flex does not "
            + "know to be yours it asks first.");
        _silentTxFixButton.Click += (s, e) => LoadMicProfileForSilentTx();
        AddToSection(TxAudioContent, _silentTxFixButton);

        _micProfileControl = MakeCycle("Microphone profile", new[] { NoMicProfilesOption });
        JJFlexHelp.SetText(_micProfileControl,
            "A saved setup for one microphone: its computer settings plus, per "
            + "radio, the radio's own mic profile to load. Apply puts it into "
            + "effect; nothing changes until you do.");
        AddToSection(TxAudioContent, _micProfileControl);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };

        // No Alt mnemonics on any of these, deliberately — WPF access keys
        // also match with Shift held, and every new letter here would need
        // auditing against the global Alt+Shift chords (see the toolbar note
        // at the top of the XAML).
        var applyBtn = new Button { Content = "Apply Profile", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0) };
        AutomationProperties.SetName(applyBtn, "Apply microphone profile");
        applyBtn.Click += (s, e) => ApplyMicProfile();
        buttons.Children.Add(applyBtn);

        var saveBtn = new Button { Content = "Save Profile...", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0) };
        AutomationProperties.SetName(saveBtn, "Save microphone profile");
        saveBtn.Click += (s, e) => SaveMicProfile();
        buttons.Children.Add(saveBtn);

        var deleteBtn = new Button { Content = "Delete Profile", Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(deleteBtn, "Delete microphone profile");
        deleteBtn.Click += (s, e) => DeleteMicProfile();
        buttons.Children.Add(deleteBtn);

        AddToSection(TxAudioContent, buttons);
        RefreshMicProfileOptions();
    }

    /// <summary>
    /// Show or hide the silent-transmit warning to match what the radio is
    /// reporting right now (#99). Announce-only: this method reads the radio
    /// and writes nothing to it.
    /// </summary>
    /// <remarks>
    /// Called from every poll, so the note appears the moment the radio's
    /// answers land and disappears the moment a profile is loaded — including
    /// when the operator loads one from SmartSDR on another screen. The text is
    /// compared before it is assigned: reassigning identical text to a control
    /// with an automation name is a UIA property change, and a live region
    /// re-announcing the same warning every second is worse than no warning.
    /// </remarks>
    private void UpdateSilentTxNote()
    {
        if (_silentTxNote == null) return;

        string? advisory = _rig?.SilentTxMicProfileAdvisory();
        if (string.IsNullOrEmpty(advisory))
        {
            if (_silentTxNote.Visibility != Visibility.Collapsed)
            {
                _silentTxNote.Visibility = Visibility.Collapsed;
                _silentTxNote.Text = "";
            }
            HideSilentTxFixButton();
            return;
        }

        if (_silentTxNote.Text != advisory)
        {
            _silentTxNote.Text = advisory;
            AutomationProperties.SetName(_silentTxNote, advisory);
        }
        _silentTxNote.Visibility = Visibility.Visible;
        ShowSilentTxFixButton();
    }

    /// <summary>
    /// Offer the repair. Present on every radio, owned or not — the failure is
    /// real either way and hiding the fix from a guest operator helps nobody.
    /// What ownership changes is how much is asked before it runs, never
    /// whether the offer exists.
    /// </summary>
    private void ShowSilentTxFixButton()
    {
        if (_silentTxFixButton == null || _rig == null) return;
        string pick = _rig.SuggestedMicProfileName;
        if (string.IsNullOrEmpty(pick)) { HideSilentTxFixButton(); return; }

        // Naming the profile in the label is the difference between "press
        // this and something happens to your radio" and a decision the
        // operator can make from the button alone.
        string label = $"Load Mic Profile {pick} on the Radio";
        if ((_silentTxFixButton.Content as string) != label)
        {
            _silentTxFixButton.Content = label;
            AutomationProperties.SetName(_silentTxFixButton, label);
        }
        _silentTxFixButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Withdraw the offer, keeping focus somewhere real. A control that
    /// vanishes from under the keyboard leaves focus nowhere, and a screen
    /// reader then has nothing to read — the operator presses a key and the
    /// application appears to have died.
    /// </summary>
    private void HideSilentTxFixButton()
    {
        if (_silentTxFixButton == null) return;
        if (_silentTxFixButton.Visibility == Visibility.Collapsed) return;
        bool hadFocus = _silentTxFixButton.IsKeyboardFocusWithin;
        _silentTxFixButton.Visibility = Visibility.Collapsed;
        if (hadFocus) _micProfileControl?.Focus();
    }

    /// <summary>
    /// The silent-transmit repair, ownership-gated per the design ratified
    /// 2026-08-19.
    /// </summary>
    /// <remarks>
    /// <para><b>Operator-initiated, always.</b> Branch diag/don-audio-708 did
    /// this same write automatically inside GetProfileInfo, and its mechanism
    /// is right — pcap-diffed against SmartSDR on the same radio. What is not
    /// right is doing it at connect: ProfileMICSelection is shared radio state,
    /// so on a guest connection that silently rearranges someone else's
    /// transmit chain, and an empty selection on their radio may be their
    /// deliberate arrangement. So the mechanism ships behind a press.</para>
    ///
    /// <para><b>What the flag changes.</b> On a radio marked yours it runs on
    /// the press, with a receipt and no further question — housekeeping on your
    /// own equipment. On a radio that has never been asked about, the ownership
    /// question comes first. On a radio marked someone else's, the write is
    /// still available (this is a declaration of intent, not a lock) but it is
    /// confirmed first, with the consequence named: everyone connected to that
    /// radio gets the change.</para>
    /// </remarks>
    private void LoadMicProfileForSilentTx()
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical);
            return;
        }

        string pick = _rig.SuggestedMicProfileName;
        string radioId = _rig.SelectedRadioSerial ?? "";
        if (string.IsNullOrEmpty(pick))
        {
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.micprofile.radio_offers_none"), VerbosityLevel.Critical);
            return;
        }

        if (!MayCreateOnRadio(radioId,
                Lexicon.Get("audio.micprofile.load_reason",
                    ("profile", pick), ("radio", CurrentRadioLabel()))))
        {
            // Not marked as this operator's radio — offered, never silent.
            var confirm = new ConfirmActionDialog(
                Lexicon.Get("audio.micprofile.someone_elses_title"),
                Lexicon.Get("audio.micprofile.someone_elses_body", ("profile", pick)),
                new[]
                {
                    Lexicon.Get("audio.micprofile.someone_elses_shared"),
                    Lexicon.Get("audio.micprofile.someone_elses_deliberate"),
                },
                question: Lexicon.Get("audio.micprofile.someone_elses_question", ("profile", pick)),
                yesLabel: Lexicon.Get("audio.micprofile.someone_elses_yes"),
                noLabel: Lexicon.Get("audio.micprofile.someone_elses_no"))
            {
                Owner = this,
            };
            if (confirm.ShowDialog() != true)
            {
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.micprofile.radio_left_alone"), VerbosityLevel.Terse);
                return;
            }
        }

        if (_rig.SelectMicProfileIfPresent(pick))
        {
            JJTrace.Tracing.TraceLine(
                $"SilentTxFix: operator asked for mic profile '{pick}' on {radioId}.",
                System.Diagnostics.TraceLevel.Info);
            // The radio answers on its own schedule, so the receipt says what
            // was ASKED FOR. The warning line above clears itself on the next
            // poll, when the radio confirms — which is the honest order.
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.micprofile.loaded_on_radio", ("profile", pick)),
                VerbosityLevel.Critical);
            PollTxAudio();
        }
        else
        {
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.micprofile.no_longer_listed", ("profile", pick)),
                VerbosityLevel.Critical);
        }
    }

    /// <summary>
    /// Re-read the store and rebuild the picker's options, keeping (or
    /// setting) the selection by name where possible.
    /// </summary>
    private void RefreshMicProfileOptions(string? selectName = null)
    {
        if (_micProfileControl == null) return;
        selectName ??= _micProfileControl.SelectedOption;

        var store = GetMicProfilesCallback?.Invoke() ?? new MicrophoneProfileStore();
        _micProfileControl.SuppressEvents = true;
        try
        {
            if (store.Profiles.Count == 0)
            {
                _micProfileControl.SetOptions(new[] { NoMicProfilesOption });
                return;
            }
            var names = new string[store.Profiles.Count];
            for (int i = 0; i < store.Profiles.Count; i++)
                names[i] = store.Profiles[i].Name;
            _micProfileControl.SetOptions(names);
            int idx = Array.FindIndex(names, n =>
                string.Equals(n, selectName, StringComparison.OrdinalIgnoreCase));
            if (idx > 0) _micProfileControl.SelectedIndex = idx;
        }
        finally
        {
            _micProfileControl.SuppressEvents = false;
        }
    }

    /// <summary>The profile the picker currently names, freshly loaded, or null.</summary>
    private MicrophoneProfile? SelectedMicProfile()
    {
        string name = _micProfileControl?.SelectedOption ?? "";
        if (string.IsNullOrEmpty(name) || name == NoMicProfilesOption) return null;
        var store = GetMicProfilesCallback?.Invoke();
        return store?.FindByName(name);
    }

    /// <summary>
    /// Apply the selected profile: capture half always (it is this
    /// computer's to set), radio half only where a binding for this radio
    /// exists — and every "could not" is said out loud, never guessed
    /// around.
    /// </summary>
    private void ApplyMicProfile()
    {
        var profile = SelectedMicProfile();
        if (profile == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.micprofile.none_selected_save_one"),
                VerbosityLevel.Terse);
            return;
        }

        string captureNotes = ApplyCaptureHalf(profile.Capture);
        var radioResult = profile.ApplyRadioHalf(_rig, _rig?.SelectedRadioSerial ?? "");
        if (radioResult.RadioHalfApplied) PollTxAudio();

        var parts = new List<string> { Lexicon.Get("audio.micprofile.applied", ("profile", profile.Name)) };
        if (!string.IsNullOrEmpty(radioResult.Message)) parts.Add(radioResult.Message);
        if (!string.IsNullOrEmpty(captureNotes)) parts.Add(captureNotes);
        ScreenReaderOutput.Speak(string.Join(" ", parts),
            radioResult.Warning ? VerbosityLevel.Critical : VerbosityLevel.Terse);
    }

    /// <summary>
    /// Save (or update) a microphone profile. The capture half is read from
    /// this computer now; the radio half is the operator's explicit choice —
    /// including whether to CREATE a profile on the radio, which is offered
    /// and never done automatically, because that is writing to someone's
    /// equipment.
    /// </summary>
    private void SaveMicProfile()
    {
        var dialog = new JJFlexDialog { Title = "Save Microphone Profile", Width = 480, Height = 320 };
        dialog.ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };

        var prompt = new TextBlock
        {
            Text = "Name this microphone (an existing name updates that profile):",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(prompt);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(nameBox, "Microphone profile name");
        string current = _micProfileControl?.SelectedOption ?? "";
        if (!string.IsNullOrEmpty(current) && current != NoMicProfilesOption)
            nameBox.Text = current;
        panel.Children.Add(nameBox);

        // The radio-half choice. Options depend on what is true right now:
        // with no radio there is nothing to offer beyond the computer half,
        // and the reference option names the actual profile it would bind.
        var choiceLabel = new TextBlock
        {
            Text = "Radio settings to store for this radio:",
            Margin = new Thickness(0, 4, 0, 4),
        };
        panel.Children.Add(choiceLabel);

        string radioProfileName = _rig?.CurrentMicProfileName ?? "";
        bool haveRig = _rig != null;

        RadioButton? referenceOption = null;
        RadioButton? createOption = null;
        RadioButton? snapshotOption = null;
        var pcOnlyOption = new RadioButton
        {
            Content = "Computer settings only (leave any stored radio half as it is)",
            Margin = new Thickness(0, 2, 0, 2),
            GroupName = "MicProfileRadioHalf",
        };

        // Marked as somebody else's radio, which changes two things below: the
        // create-on-radio option is not offered, and the conservative choice
        // becomes the default (#94).
        bool notMyRadio = haveRig
            && RadioConfig.LoadForRadio(_rig!.SelectedRadioSerial).Ownership
               == RadioOwnership.SomeoneElses;

        if (haveRig)
        {
            if (!string.IsNullOrEmpty(radioProfileName))
            {
                referenceOption = new RadioButton
                {
                    Content = $"Reference the radio's current mic profile: {radioProfileName}",
                    Margin = new Thickness(0, 2, 0, 2),
                    GroupName = "MicProfileRadioHalf",
                    IsChecked = true,
                };
                JJFlexHelp.SetText(referenceOption,
                    "The radio keeps its own mic profile; this profile just names "
                    + "which one to load. Nothing is copied, so other clients and "
                    + "this app always agree.");
                panel.Children.Add(referenceOption);
            }
            else if (notMyRadio)
            {
                // Marked as somebody else's: the option is not offered at all
                // (#94 — writing to the radio is surfaced only on radios the
                // operator has marked as theirs). A read-only line takes its
                // place so the absence is explained rather than merely felt,
                // and it names where the answer can be changed. A disabled
                // radio button would sit in the visual order saying nothing.
                var notOffered = new TextBox
                {
                    Text = "Creating a mic profile on the radio is not offered here, because "
                         + "you have marked this radio as someone else's. A mic profile lives "
                         + "on the radio and is shared with everyone connected to it. Your "
                         + "computer settings still save normally. Settings, Radios tab is "
                         + "where that answer can be changed.",
                    IsReadOnly = true,
                    IsReadOnlyCaretVisible = true,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                AutomationProperties.SetName(notOffered,
                    "Why creating a mic profile on the radio is not offered");
                panel.Children.Add(notOffered);
            }
            else
            {
                createOption = new RadioButton
                {
                    Content = "Create a mic profile ON THE RADIO with this name, and reference it",
                    Margin = new Thickness(0, 2, 0, 2),
                    GroupName = "MicProfileRadioHalf",
                };
                JJFlexHelp.SetText(createOption,
                    "Writes a new mic profile to the radio itself, holding its "
                    + "current TX settings. Offered because no radio mic profile "
                    + "is loaded right now — done only if you choose it here. "
                    + "A mic profile lives on the radio and is shared with every "
                    + "program connected to it, so on a radio JJ Flex does not yet "
                    + "know to be yours, it asks whose it is before creating one.");
                panel.Children.Add(createOption);
            }

            snapshotOption = new RadioButton
            {
                Content = "Snapshot the radio's TX settings into this profile",
                Margin = new Thickness(0, 2, 0, 2),
                GroupName = "MicProfileRadioHalf",
                // Snapshotting writes nothing to the radio — it copies the
                // radio's values into OUR file. But a stored-values binding is
                // applied back to the radio later, and bindings are ungated by
                // design, so on a radio the operator has told us is not theirs
                // it should not be the answer they get by pressing OK without
                // reading. Still offered; just not pre-chosen.
                IsChecked = referenceOption == null && !notMyRadio,
            };
            JJFlexHelp.SetText(snapshotOption,
                "Copies mic gain, EQ, compander, processor and filter values "
                + "into the profile file. Nothing is written to the radio by "
                + "saving — but applying this profile later would set those "
                + "values on the radio, so on a radio that is not yours the "
                + "computer-settings-only choice is the safe one. The shape "
                + "used for radios that have no profile system of their own; "
                + "on a Flex, referencing is usually the better choice.");
            panel.Children.Add(snapshotOption);
        }

        if (!haveRig || (referenceOption == null && notMyRadio))
        {
            pcOnlyOption.IsChecked = true;
        }
        panel.Children.Add(pcOnlyOption);

        var okBtn = new Button { Content = "OK", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80, Height = 28, IsCancel = true };
        AutomationProperties.SetName(okBtn, "OK");
        AutomationProperties.SetName(cancelBtn, "Cancel");
        okBtn.Click += (s2, e2) =>
        {
            string name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.please_enter_a_name"), VerbosityLevel.Terse);
                return;
            }

            var store = GetMicProfilesCallback?.Invoke() ?? new MicrophoneProfileStore();
            var profile = store.FindByName(name);
            bool isNew = profile == null;
            if (profile == null)
            {
                profile = new MicrophoneProfile { Name = name };
                store.Profiles.Add(profile);
            }

            profile.Capture = CaptureCurrentPcSettings();

            string radioHalfSpoken = "";
            string radioId = _rig?.SelectedRadioSerial ?? "";
            if (_rig != null && !string.IsNullOrEmpty(radioId))
            {
                if (referenceOption?.IsChecked == true)
                {
                    profile.SetSetupFor(new RadioProfileReference
                    {
                        RadioId = radioId,
                        RadioModel = _rig.RadioModel,
                        ProfileName = radioProfileName,
                    });
                    radioHalfSpoken = Lexicon.Get("audio.micprofile.references_radio_profile",
                        ("profile", radioProfileName));
                }
                else if (createOption?.IsChecked == true)
                {
                    // The explicit offer, taken — and the first moment the app
                    // needs to know whose radio this is (#94). The question is
                    // asked here rather than when the option was ticked, so it
                    // fires exactly once per save instead of on every change of
                    // mind, and so it arrives attached to a commit the operator
                    // just made.
                    if (MayCreateOnRadio(radioId,
                            Lexicon.Get("audio.micprofile.create_reason", ("profile", name))))
                    {
                        // SelectProfile's mic case creates the profile on the
                        // radio when it is missing and loads it; the radio
                        // autosaves its current TX settings into it from here on.
                        _rig.SelectProfile(new Profile_t(name, ProfileTypes.mic, false));
                        profile.SetSetupFor(new RadioProfileReference
                        {
                            RadioId = radioId,
                            RadioModel = _rig.RadioModel,
                            ProfileName = name,
                        });
                        radioHalfSpoken = Lexicon.Get("audio.micprofile.created_on_radio", ("profile", name));
                    }
                    else
                    {
                        // Declined, or the radio is marked as someone else's.
                        // The computer half still saved — refusing the whole
                        // save would punish the operator for answering
                        // honestly — and the radio was not touched. Say both.
                        radioHalfSpoken = Lexicon.Get("audio.micprofile.nothing_created_on_radio");
                    }
                }
                else if (snapshotOption?.IsChecked == true)
                {
                    profile.SetSetupFor(new RadioTxValues
                    {
                        RadioId = radioId,
                        RadioModel = _rig.RadioModel,
                        Values = AudioChainPreset.CaptureFrom(_rig, name, ReadSavedPcInputName()),
                    });
                    radioHalfSpoken = Lexicon.Get("audio.micprofile.snapshotted");
                }
                // pcOnly: existing bindings deliberately untouched.
            }

            bool saved = SaveMicProfilesCallback?.Invoke(store) ?? false;
            if (saved)
            {
                RefreshMicProfileOptions(selectName: name);
                string verb = Lexicon.Get(isNew ? "audio.micprofile.verb_saved" : "audio.micprofile.verb_updated");
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.micprofile.save_receipt", ("profile", name), ("verb", verb)) +
                    (string.IsNullOrEmpty(radioHalfSpoken) ? "" : " " + radioHalfSpoken),
                    VerbosityLevel.Terse);
            }
            else
            {
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.micprofile.save_failed",
                        ("profile", name), ("reason", PresetSaveFailed)),
                    VerbosityLevel.Critical);
            }
            dialog.Close();
        };
        cancelBtn.Click += (s2, e2) => dialog.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void DeleteMicProfile()
    {
        var profile = SelectedMicProfile();
        if (profile == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.micprofile.none_selected"), VerbosityLevel.Terse);
            return;
        }

        var confirm = new ConfirmActionDialog(
            Lexicon.Get("audio.micprofile.delete_title"),
            Lexicon.Get("audio.micprofile.delete_body", ("profile", profile.Name)),
            question: Lexicon.Get("audio.micprofile.delete_question"),
            yesLabel: Lexicon.Get("audio.micprofile.delete_yes_label"));
        if (confirm.ShowDialog() != true) return;

        var store = GetMicProfilesCallback?.Invoke() ?? new MicrophoneProfileStore();
        store.Profiles.RemoveAll(p =>
            string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));

        bool saved = SaveMicProfilesCallback?.Invoke(store) ?? false;
        RefreshMicProfileOptions();
        if (saved)
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.micprofile.deleted", ("profile", profile.Name)),
                VerbosityLevel.Terse);
        else
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.micprofile.deleted_but_not_saved",
                    ("profile", profile.Name), ("reason", PresetSaveFailed)),
                VerbosityLevel.Critical);
    }

    /// <summary>
    /// The chosen capture device from audioDevices.xml — name plus the host
    /// API id WindowsMicLevel's matcher wants. A file read, never a
    /// PortAudio enumeration.
    /// </summary>
    private (string Name, int HostApiTypeId) ReadSavedPcInput()
    {
        try
        {
            string? path = AudioDevicesPath?.Invoke();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return ("", -1);
            var devices = new JJPortaudio.Devices(path);
            devices.LoadSavedSelection();
            return (devices.InputDevice?.Name ?? "", devices.InputDevice?.hostApiTypeId ?? -1);
        }
        catch
        {
            return ("", -1);
        }
    }

    /// <summary>
    /// Read this computer's current capture settings for the chosen device:
    /// the stage-one half of a microphone profile. Missing pieces are
    /// recorded as missing (level -1, boost unrecorded), never as zeros.
    /// </summary>
    private MicCaptureSettings CaptureCurrentPcSettings()
    {
        var (name, hostApi) = ReadSavedPcInput();
        var capture = new MicCaptureSettings { DeviceName = name };

        // The transmit cleanup chain (PC NR + gate), captured only while a
        // chain is attached — detached, TxAudioConditioning answers with
        // defaults that describe nothing, and recording those as this
        // microphone's tuning would be the confident lie. Null means "not
        // recorded", same contract as level -1 and boost unrecorded below.
        // (Track B, 2026-08-18, #44 — until this line the cleanup knobs were
        // session-only and every restart lost them.)
        if (TxAudioConditioning.Conditioner != null)
            capture.Conditioning = TxAudioConditioning.CaptureSettings();

        if (string.IsNullOrEmpty(name)) return capture;

        var level = WindowsMicLevel.TryFindByName(name, hostApi, out _);
        if (level == null) return capture;
        try
        {
            capture.InputLevelPercent = (int)Math.Round(level.Percent);
            if (level.HasBoost)
            {
                capture.BoostRecorded = true;
                capture.BoostDb = level.BoostDb;
            }
        }
        catch
        {
            // The device vanished between match and read; identity alone is
            // still worth keeping.
        }
        finally
        {
            level.Dispose();
        }
        return capture;
    }

    /// <summary>
    /// Apply the capture half to this computer. Returns spoken-ready notes
    /// for anything that was NOT done — a different device in use (identity
    /// is never switched from here), a level that could not be set — and ""
    /// when everything landed quietly. Gate settings are carried, not yet
    /// driven: the gate engine is transmit-conditioning work and reads them
    /// from the profile when it arrives.
    /// </summary>
    private string ApplyCaptureHalf(MicCaptureSettings capture)
    {
        if (capture == null) return "";
        var notes = new List<string>();
        var (currentName, hostApi) = ReadSavedPcInput();

        bool deviceMismatch = !string.IsNullOrEmpty(capture.DeviceName)
            && !string.IsNullOrEmpty(currentName)
            && !string.Equals(capture.DeviceName, currentName, StringComparison.OrdinalIgnoreCase);
        if (deviceMismatch)
        {
            // A level tuned for one microphone means nothing on another —
            // moving the current device's level to the old device's number
            // would be confidently wrong. Say it, do not do it. The same
            // argument covers the cleanup chain below: a gate tuned for
            // that microphone's room noise is not this microphone's gate.
            // Two whole sentences rather than one with a spliced clause: a
            // fragment like " and the transmit cleanup settings were" cannot be
            // translated, or even read, on its own. Same words either way.
            notes.Add(Lexicon.Get(
                capture.Conditioning != null
                    ? "audio.micprofile.device_mismatch_with_cleanup"
                    : "audio.micprofile.device_mismatch",
                ("device", capture.DeviceName), ("current", currentName)));
            return string.Join(" ", notes);
        }

        // The transmit cleanup half (PC NR + gate). Applied to the live
        // chain when one is attached; the chain only exists while a radio is
        // connected, so with no radio the truth is "not now", said out loud
        // rather than silently no-opped — TxAudioConditioning's setters
        // swallow writes when detached, which is exactly the kind of
        // confident silence this dialog exists to end.
        // (Track B, 2026-08-18, #44.)
        if (capture.Conditioning != null)
        {
            if (TxAudioConditioning.Conditioner != null)
            {
                TxAudioConditioning.ApplySettings(capture.Conditioning);
                PollTxCleanup();
            }
            else
            {
                notes.Add(Lexicon.Get("audio.micprofile.cleanup_needs_a_radio"));
            }
        }

        string targetName = !string.IsNullOrEmpty(currentName) ? currentName : capture.DeviceName;
        if (capture.InputLevelPercent >= 0 && !string.IsNullOrEmpty(targetName))
        {
            var level = WindowsMicLevel.TryFindByName(targetName, hostApi, out string whyNot);
            if (level == null)
            {
                notes.Add(Lexicon.Get("audio.micprofile.level_not_set", ("reason", whyNot)));
            }
            else
            {
                try
                {
                    level.Percent = Math.Clamp(capture.InputLevelPercent, 0, 100);
                    if (capture.BoostRecorded && level.HasBoost)
                        level.BoostDb = capture.BoostDb;
                }
                catch (Exception ex)
                {
                    notes.Add(Lexicon.Get("audio.micprofile.level_not_set", ("reason", ex.Message)));
                }
                finally
                {
                    level.Dispose();
                }
            }
        }

        return string.Join(" ", notes);
    }

    #endregion
}
