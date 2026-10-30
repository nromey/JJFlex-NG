using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// The recorder on JJ Flexible Home (#787, Tape Deck track 2): the radio's
/// own per-slice recorder in the "Audio and Slice" group, beside the slice's
/// other controls. A partial of its own so the recorder lives in one place
/// and merges cleanly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a slice chooser and not only the active slice.</b> Noel, 2026-10-21
/// 11:00: <i>"On my radio however, technically we could record a second slice
/// but listen to the current slice."</i> The keys act on the active slice;
/// Home is where a different one can be chosen. The chooser starts on the
/// active slice and stays where the operator put it until that slice goes
/// away; the buttons always name the slice they act on, so a chooser left on
/// slice B after moving to slice A cannot surprise anyone.
/// </para>
/// <para>
/// <b>Both buttons stay in the tab order.</b> Play with nothing recorded says
/// so in a sentence rather than greying out, because a greyed button leaves
/// the tab order and an operator would never learn Play is there until after
/// a recording existed. (The Audio Workshop's Play recording button does
/// grey out; that one sits beside its own Record button in a dialog about
/// takes, which is a different place to learn it from.)
/// </para>
/// </remarks>
public partial class ScreenFieldsPanel
{
    private CycleFieldControl _recorderSliceControl = null!;
    private Button _homeSliceRecordButton = null!;
    private Button _homeSlicePlayButton = null!;
    private string _recorderLetter = "";
    private string[] _recorderLetters = Array.Empty<string>();

    private static Button MakeRecorderButton(string label)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static void Relabel(Button button, string label)
    {
        if ((button.Content as string) == label) return;
        button.Content = label;
        AutomationProperties.SetName(button, label);
    }

    private SliceRecorderTarget HomeRecorderTarget => _recorderLetter.Length > 0
        ? SliceRecorderTarget.Letter(_recorderLetter[0]) : SliceRecorderTarget.ActiveSlice;

    /// <summary>Appended to the Audio and Slice group, after the slice buttons.</summary>
    private void BuildSliceRecorderControls()
    {
        AudioContent.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        // DRAFT PROSE (#787): awaiting Noel — the chooser's label, its
        // options, the buttons' labels and the help text.
        _recorderSliceControl = MakeCycle(Lexicon.Get("audio.fields.recorder_slice"), Array.Empty<string>());
        JJFlexHelp.SetText(_recorderSliceControl, Lexicon.Get("audio.fields.recorder_help"));
        _recorderSliceControl.SelectionChanged += (s, idx) =>
        {
            if (_polling || idx < 0 || idx >= _recorderLetters.Length) return;
            _recorderLetter = _recorderLetters[idx];
            UpdateSliceRecorderButtons();
        };
        AudioContent.Children.Add(_recorderSliceControl);

        _homeSliceRecordButton = MakeRecorderButton(Lexicon.Get("audio.recorder.record_button", ("letter", "A")));
        JJFlexHelp.SetText(_homeSliceRecordButton, Lexicon.Get("audio.fields.recorder_help"));
        _homeSliceRecordButton.Click += async (s, e) =>
        {
            await RecorderControls.ToggleSliceRecordAsync(_rig, HomeRecorderTarget);
            UpdateSliceRecorderButtons();
        };
        AudioContent.Children.Add(_homeSliceRecordButton);

        _homeSlicePlayButton = MakeRecorderButton(Lexicon.Get("audio.recorder.play_button", ("letter", "A")));
        JJFlexHelp.SetText(_homeSlicePlayButton, Lexicon.Get("audio.fields.recorder_help"));
        _homeSlicePlayButton.Click += async (s, e) =>
        {
            await RecorderControls.ToggleSlicePlayAsync(_rig, HomeRecorderTarget);
            UpdateSliceRecorderButtons();
        };
        AudioContent.Children.Add(_homeSlicePlayButton);
    }

    /// <summary>
    /// Keep the chooser's slices and the buttons' words true. Runs inside the
    /// Audio and Slice group's poll, under <c>_polling</c>, so moving the
    /// chooser here never reads as the operator choosing.
    /// </summary>
    private void PollSliceRecorder()
    {
        if (_rig == null || _recorderSliceControl == null) return;

        int count = Math.Min(_rig.MyNumSlices, 8);
        var letters = Enumerable.Range(0, Math.Max(0, count)).Select(i => _rig.VFOToLetter(i)).ToArray();
        if (!letters.SequenceEqual(_recorderLetters))
        {
            _recorderLetters = letters;
            _recorderSliceControl.SetOptions(letters
                .Select(l => Lexicon.Get("audio.fields.recorder_slice_option", ("letter", l))).ToArray());
        }
        if (Array.IndexOf(_recorderLetters, _recorderLetter) < 0)
            _recorderLetter = _rig.ActiveSliceLetter is { Length: > 0 } active && Array.IndexOf(_recorderLetters, active) >= 0
                ? active : (_recorderLetters.FirstOrDefault() ?? "");
        int index = Array.IndexOf(_recorderLetters, _recorderLetter);
        if (index >= 0) _recorderSliceControl.SelectedIndex = index;
        UpdateSliceRecorderButtons();
    }

    private Button _recordToFileButton = null!;
    private Button _playFromComputerButton = null!;
    private CheckBox _recordHeardCheck = null!;
    private CheckBox _recordStereoCheck = null!;
    private bool _updatingRecorderOptions;

    /// <summary>
    /// Record to file and play from computer, at the end of the PC audio
    /// group (Noel, 2026-10-21 11:01: computer recording goes under Audio).
    /// Two plain buttons and no chooser: there is one computer recorder, and
    /// which recording plays is the recordings dialog's choice.
    /// </summary>
    private void BuildComputerRecorderControls()
    {
        // DRAFT PROSE (#787): awaiting Noel
        _recordHeardCheck = MakeToggle(Lexicon.Get("audio.recorder.record_heard"));
        _recordStereoCheck = MakeToggle(Lexicon.Get("audio.recorder.stereo"));
        foreach (var check in new[] { _recordHeardCheck, _recordStereoCheck })
        {
            JJFlexHelp.SetText(check, Lexicon.Get("audio.recorder.options_help"));
            check.Checked += (_, _) => ApplyComputerRecordingOptions();
            check.Unchecked += (_, _) => ApplyComputerRecordingOptions();
            AudioContent.Children.Add(check);
        }
        // DRAFT PROSE (#787): awaiting Noel
        _recordToFileButton = MakeRecorderButton(Lexicon.Get("audio.recorder.record_to_file_button"));
        JJFlexHelp.SetText(_recordToFileButton, Lexicon.Get("audio.fields.computer_recorder_help"));
        _recordToFileButton.Click += async (s, e) =>
        {
            await RecorderControls.ToggleComputerRecordAsync(_rig);
            UpdateComputerRecorderButtons();
        };
        AudioContent.Children.Add(_recordToFileButton);

        _playFromComputerButton = MakeRecorderButton(Lexicon.Get("audio.recorder.play_from_computer_button"));
        JJFlexHelp.SetText(_playFromComputerButton, Lexicon.Get("audio.fields.computer_recorder_help"));
        _playFromComputerButton.Click += async (s, e) =>
        {
            await RecorderControls.ToggleComputerPlayAsync(_rig);
            UpdateComputerRecorderButtons();
        };
        AudioContent.Children.Add(_playFromComputerButton);
    }

    private void ApplyComputerRecordingOptions()
    {
        if (_polling || _updatingRecorderOptions || ComputerRecorder.Current is not ComputerFlacRecorder engine || engine.IsRecording) return;
        engine.RecordWhatYouHeard = _recordHeardCheck.IsChecked == true;
        engine.Stereo = _recordStereoCheck.IsChecked == true;
    }

    private void UpdateComputerRecorderButtons()
    {
        if (_recordToFileButton == null || _playFromComputerButton == null) return;
        var recorder = ComputerRecorder.Current;
        if (_recordHeardCheck != null && recorder is ComputerFlacRecorder engine)
        {
            _updatingRecorderOptions = true;
            try
            {
                _recordHeardCheck.IsChecked = engine.RecordWhatYouHeard;
                _recordStereoCheck.IsChecked = engine.Stereo;
            }
            finally { _updatingRecorderOptions = false; }
            _recordHeardCheck.IsEnabled = _recordStereoCheck.IsEnabled = !recorder.IsRecording;
            _recordHeardCheck.IsTabStop = _recordStereoCheck.IsTabStop = !recorder.IsRecording;
        }
        Relabel(_recordToFileButton, Lexicon.Get(recorder.IsRecording
            ? "audio.recorder.stop_record_to_file_button" : "audio.recorder.record_to_file_button"));
        // DRAFT PROSE (#835): awaiting Noel. Play pauses and carries on now,
        // so the button says what pressing it does.
        Relabel(_playFromComputerButton, Lexicon.Get(!recorder.IsPlaying
            ? "audio.recorder.play_from_computer_button"
            : RecordingPlayer.IsPaused ? "audio.recorder.carry_on_play_from_computer_button"
            : "audio.recorder.pause_play_from_computer_button"));
    }

    private void UpdateSliceRecorderButtons()
    {
        if (_homeSliceRecordButton == null || _homeSlicePlayButton == null) return;
        string letter = _recorderLetter.Length > 0 ? _recorderLetter : (_rig?.ActiveSliceLetter ?? "");
        var target = HomeRecorderTarget;
        bool recording = _rig?.IsSliceRecording(target) == true;
        Relabel(_homeSliceRecordButton, Lexicon.Get(recording ? "audio.recorder.stop_record_button"
            : "audio.recorder.record_button", ("letter", letter)));
        Relabel(_homeSlicePlayButton, RecorderControls.SlicePlayLabel(_rig, target));
    }
}
