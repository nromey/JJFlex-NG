using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, TX Audio tab: the reference audio section — a known
/// recording sent in place of the microphone, and the recorder that makes one
/// (Sprint 33 Track I).
/// </summary>
/// <remarks>
/// <para>
/// WHY IT SITS BETWEEN THE TEST TONE AND THE AUDIO CHECK. The test tone above
/// answers "is the path alive and calibrated". The Audio Check below answers
/// "how do I sound". This answers the question in between, which nothing has
/// answered until now: "did that change help?" A tone cannot tell you, because
/// nothing in a sine responds to a compressor. A person talking cannot tell
/// you either, because they say it differently every time — and two
/// measurements of two different signals are not a comparison. A known
/// recording is the only stimulus that makes the answer an observation instead
/// of an impression.
/// </para>
/// <para>
/// It follows the test tone's ARM model exactly, and that is a safety decision
/// as much as a consistency one: arming does not transmit. The operator keys
/// the radio themselves, and the recording rides that transmission the way the
/// tone does. Nothing here ever puts a signal on the air on its own.
/// </para>
/// <para>
/// The recorder in this section goes through <see cref="RecordingNarrator"/>,
/// which is the only thing in the application that can open a microphone for
/// recording, and which always says so. This section never touches
/// <c>MicRecorder</c> itself.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    #region Reference Audio Section (Sprint 33 Track I)

    private CycleFieldControl? _refPickControl;
    private CheckBox? _refArmCheck;
    private Button? _refRecordButton;
    private Button? _refFolderButton;
    private TextBlock? _refInfo;

    /// <summary>The choices behind <see cref="_refPickControl"/>, in order.</summary>
    private readonly List<string> _refPaths = new();

    /// <summary>The file currently handed to the rig's player, or empty.</summary>
    private string _refLoadedPath = "";

    /// <summary>True while the last armed pass has not yet been reported as finished.</summary>
    private bool _refPassRunning;

    private void BuildReferenceAudioSection()
    {
        AddRadioSection(HearYourselfContent, "Reference Audio");

        _refPickControl = MakeCycle("Reference recording", new[] { "(none available)" });
        _refPickControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            ReferenceSelectionChanged();
        };
        JJFlexHelp.SetText(_refPickControl,
            "Which known recording gets sent instead of your microphone. The "
            + "one that ships with JJ Flexible is the same audio on every "
            + "station, so a measurement here means the same thing as a "
            + "measurement there. A take you record yourself is the honest "
            + "reference for YOUR station — your microphone, your room, your "
            + "voice — and is the one to use when you want to know whether "
            + "your audio is better than it was last month.");
        AddToSection(HearYourselfContent, _refPickControl);

        _refArmCheck = MakeToggle("Send the reference instead of my microphone");
        _refArmCheck.Checked += (s, e) => ReferenceArmChanged(true);
        _refArmCheck.Unchecked += (s, e) => ReferenceArmChanged(false);
        JJFlexHelp.SetText(_refArmCheck,
            "Arms the recording. It does not transmit anything by itself — "
            + "you key the radio as usual and the recording goes out in place "
            + "of your microphone, through exactly the same processing your "
            + "voice goes through. It starts from the beginning each time you "
            + "key, and your microphone comes back when it finishes.");
        AddToSection(HearYourselfContent, _refArmCheck);

        _refRecordButton = new Button
        {
            Content = "Record a reference take",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_refRecordButton, "Record a reference take");
        _refRecordButton.Click += (s, e) => ReferenceRecordClicked();
        JJFlexHelp.SetText(_refRecordButton,
            "Records your microphone to a file you can send down the transmit "
            + "path afterwards. Nothing is transmitted while you record. It "
            + "says when it starts and when it stops, and the file is kept on "
            + "this computer — never on the radio. Read the shipped script and "
            + "say the same words if you want your take to compare with "
            + "everyone else's.");
        AddToSection(HearYourselfContent, _refRecordButton);

        _refFolderButton = new Button
        {
            Content = "Open recordings folder",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_refFolderButton, "Open recordings folder");
        _refFolderButton.Click += (s, e) =>
        {
            if (!RecordingStore.OpenFolder())
                ScreenReaderOutput.Speak(Lexicon.Get("audio.reference.folder_open_failed"),
                    VerbosityLevel.Critical, interrupt: true);
        };
        JJFlexHelp.SetText(_refFolderButton,
            "Opens the folder your recordings live in, so you can play, "
            + "rename, share or delete them with the tools you already use. "
            + "A WAV file you drop in there yourself shows up in the list "
            + "above with no importing.");
        AddToSection(HearYourselfContent, _refFolderButton);

        _refInfo = new TextBlock
        {
            Text = "",
            Margin = new Thickness(2, 2, 2, 4),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(_refInfo, "Reference audio status");
        AutomationProperties.SetLiveSetting(_refInfo, AutomationLiveSetting.Polite);
        AddToSection(HearYourselfContent, _refInfo);

        RecordingNarrator.StateChanged += OnRecordingStateChanged;
        RecordingNarrator.RecordingSaved += OnRecordingSaved;

        RefreshReferenceList(selectPath: null);
    }

    /// <summary>
    /// Rebuild the picker from what is actually on disk: the shipped
    /// reference, then the operator's own takes, newest first.
    /// </summary>
    private void RefreshReferenceList(string? selectPath)
    {
        if (_refPickControl == null) return;

        _refPaths.Clear();
        var labels = new List<string>();

        if (ReferenceVoice.IsInstalled)
        {
            _refPaths.Add(ReferenceVoice.FilePath);
            labels.Add("JJ Flexible reference voice (shipped)");
        }

        foreach (var rec in RecordingStore.Enumerate())
        {
            _refPaths.Add(rec.Path);
            labels.Add(rec.Describe());
        }

        if (labels.Count == 0)
        {
            labels.Add("(none available)");
            _refLoadedPath = "";
        }

        int want = 0;
        if (!string.IsNullOrEmpty(selectPath))
        {
            int found = _refPaths.IndexOf(selectPath!);
            if (found >= 0) want = found;
        }
        else if (!string.IsNullOrEmpty(_refLoadedPath))
        {
            int found = _refPaths.IndexOf(_refLoadedPath);
            if (found >= 0) want = found;
        }

        // One Setup call rather than Setup-then-select: the control raises a
        // UIA value-change event per call, and a picker that announces itself
        // twice every time the list is rebuilt is noise, not information.
        _polling = true;
        try { _refPickControl.Setup("Reference recording", labels.ToArray(), want); }
        finally { _polling = false; }

        UpdateReferenceStatus();
    }

    /// <summary>The file the picker currently names, or empty.</summary>
    private string SelectedReferencePath()
    {
        int idx = _refPickControl?.SelectedIndex ?? -1;
        return (idx >= 0 && idx < _refPaths.Count) ? _refPaths[idx] : "";
    }

    private void ReferenceSelectionChanged()
    {
        // Changing the recording under an armed pass would swap what is going
        // out mid-transmission, which is exactly the sort of surprise this
        // section exists to remove.
        if (_rig?.TxFilePlaying == true || _refArmCheck?.IsChecked == true)
            DisarmReference(speak: true);

        _refLoadedPath = "";
        UpdateReferenceStatus();
    }

    /// <summary>
    /// Decode the selected file into the rig's player. Returns false with a
    /// spoken reason.
    /// </summary>
    private bool LoadSelectedReference()
    {
        var rig = _rig;
        if (rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical, interrupt: true);
            return false;
        }

        string path = SelectedReferencePath();
        if (string.IsNullOrEmpty(path))
        {
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.reference.nothing_to_send"),
                VerbosityLevel.Critical, interrupt: true);
            return false;
        }

        if (string.Equals(path, _refLoadedPath, StringComparison.OrdinalIgnoreCase)
            && rig.TxFilePlayer.HasContent)
            return true;

        if (!TxAudioFile.TryLoadInto(rig, path, out TxAudioFile.Loaded? loaded, out string trouble))
        {
            EarconPlayer.Warning2Beep();
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.reference.load_failed", ("reason", trouble)),
                VerbosityLevel.Critical, interrupt: true);
            return false;
        }

        _refLoadedPath = path;
        _refLastDescription = loaded!.Describe();
        return true;
    }

    private string _refLastDescription = "";

    private void ReferenceArmChanged(bool armed)
    {
        if (_polling) return;

        if (!armed)
        {
            DisarmReference(speak: true);
            return;
        }

        var rig = _rig;
        if (rig == null)
        {
            SetReferenceArmSilently(false);
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical, interrupt: true);
            return;
        }

        // The reference rides the PC-audio transmit path, exactly as the test
        // tone does, so it needs the identical preconditions — and reuses the
        // rig's own answer rather than growing a second opinion about them.
        string pathTrouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(pathTrouble))
        {
            SetReferenceArmSilently(false);
            EarconPlayer.Warning2Beep();
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.reference.not_armed", ("reason", pathTrouble)),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        if (rig.TxToneEngaged)
        {
            SetReferenceArmSilently(false);
            EarconPlayer.Warning2Beep();
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.reference.not_armed",
                    ("reason", Lexicon.Get("audio.reference.tone_already_armed"))),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        if (!LoadSelectedReference())
        {
            SetReferenceArmSilently(false);
            return;
        }

        rig.TxFileStart();
        _refPassRunning = true;

        // #128 sweep audit (2026-08-21): arm answers back, mirroring the test
        // tone checkbox one tab over — the two arms are siblings on the same
        // transmit path and must sound alike. Only the success path tones;
        // every declined path above reverts the checkbox and warns instead.
        EarconPlayer.FeatureOnTone();
        ScreenReaderOutput.Speak(
            Lexicon.Get("audio.reference.armed", ("recording", _refLastDescription)),
            VerbosityLevel.Critical, interrupt: true);
        UpdateReferenceStatus();
    }

    /// <summary>
    /// Release the reference and restore the microphone. Runs on operator
    /// unarm, on the recording finishing, on dialog close, and on radio
    /// teardown (pass the departing rig for that case, where _rig is null).
    /// </summary>
    private void DisarmReference(bool speak, FlexBase? rig = null)
    {
        (rig ?? _rig)?.TxFileStop();
        _refPassRunning = false;
        SetReferenceArmSilently(false);
        // #128: tone only on the spoken (operator-visible) path — the silent
        // callers are the recording finishing on its own, dialog close, and
        // radio teardown, none of which is the operator toggling (#58 rule).
        if (speak)
        {
            EarconPlayer.FeatureOffTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.reference.disarmed"),
                VerbosityLevel.Critical, interrupt: true);
        }
        UpdateReferenceStatus();
    }

    /// <summary>Set the arm checkbox without firing its handlers.</summary>
    private void SetReferenceArmSilently(bool value)
    {
        if (_refArmCheck == null) return;
        _polling = true;
        try { _refArmCheck.IsChecked = value; }
        finally { _polling = false; }
    }

    private void ReferenceRecordClicked()
    {
        // Arming and recording at once would have the operator transmitting a
        // recording while making another one. Nothing breaks, but nobody means
        // to do it.
        if (!RecordingNarrator.IsRunning && _refArmCheck?.IsChecked == true)
            DisarmReference(speak: false);

        RecordingNarrator.Toggle(Lexicon.Get("audio.reference.recording_purpose"));
    }

    /// <summary>
    /// Follow the recorder's state so the button says what pressing it will
    /// do. The narrator has already spoken; this only relabels.
    /// </summary>
    private void OnRecordingStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_refRecordButton == null) return;
            bool running = RecordingNarrator.IsRunning;
            string label = running ? "Stop recording" : "Record a reference take";
            _refRecordButton.Content = label;
            AutomationProperties.SetName(_refRecordButton, label);
            UpdateReferenceStatus();
        }));
    }

    /// <summary>
    /// A take just landed. Put it in the list and select it, so the obvious
    /// next thing — sending it — is one arm away.
    /// </summary>
    private void OnRecordingSaved(string path)
    {
        Dispatcher.BeginInvoke(new Action(() => RefreshReferenceList(selectPath: path)));
    }

    /// <summary>
    /// Reference housekeeping, run from the meter tick on every tab: notice
    /// when a pass has played out so the operator hears that it finished
    /// rather than wondering, and keep the arm box honest against the engine.
    /// </summary>
    private void SyncReferenceUi()
    {
        var rig = _rig;
        if (rig == null || _refArmCheck == null) return;

        bool engaged = rig.TxFilePlaying;
        bool armed = _refArmCheck.IsChecked == true;

        if (_refPassRunning && !engaged && rig.TxFilePlayer.ReachedEnd)
        {
            // Played to the end. Say so and stand down — leaving the box armed
            // after the recording has run out would promise a second pass that
            // would happen, silently, at the next key-down.
            _refPassRunning = false;
            EarconPlayer.ConfirmTone();
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.reference.finished"),
                VerbosityLevel.Terse, interrupt: false);
            SetReferenceArmSilently(false);
            UpdateReferenceStatus();
            return;
        }

        if (armed != engaged && !_refPassRunning)
        {
            SetReferenceArmSilently(engaged);
            UpdateReferenceStatus();
        }
    }

    /// <summary>
    /// The status line: what is selected, whether it is armed, and anything
    /// standing between it and the transmitter.
    /// </summary>
    private void UpdateReferenceStatus()
    {
        if (_refInfo == null) return;

        if (RecordingNarrator.IsRunning)
        {
            _refInfo.Text = "Recording now — "
                + RecordingStore.DescribeLength(RecordingNarrator.ElapsedSeconds)
                + " so far. Nothing is being transmitted.";
            return;
        }

        string path = SelectedReferencePath();
        if (string.IsNullOrEmpty(path))
        {
            _refInfo.Text = "No reference recording yet. Record a take, or drop a WAV "
                + "file into the recordings folder.";
            return;
        }

        var line = new System.Text.StringBuilder();
        line.Append(Path.GetFileNameWithoutExtension(path));
        if (!string.IsNullOrEmpty(_refLastDescription)
            && string.Equals(path, _refLoadedPath, StringComparison.OrdinalIgnoreCase))
        {
            line.Append(" — ").Append(_refLastDescription);
        }

        var rig = _rig;
        if (rig != null)
        {
            if (rig.TxFilePlaying)
            {
                line.Append(". Sending now.");
            }
            else
            {
                string trouble = rig.TxTonePathTrouble;
                line.Append(string.IsNullOrEmpty(trouble)
                    ? ". Ready to arm."
                    : ". " + trouble);
            }
        }

        _refInfo.Text = line.ToString();
    }

    /// <summary>
    /// Let go of the narrator's events. Called from the dialog's teardown, so
    /// a closed workshop cannot keep relabelling a button that is gone.
    /// </summary>
    private void DetachReferenceAudio()
    {
        RecordingNarrator.StateChanged -= OnRecordingStateChanged;
        RecordingNarrator.RecordingSaved -= OnRecordingSaved;
    }

    #endregion
}
