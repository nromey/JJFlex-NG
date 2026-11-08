using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Listenback;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #999, step 3: the warning before a slice recording is overwritten, at the
/// one door every recorder door shares (<see cref="RecorderControls.ToggleSliceRecordAsync"/>)
/// and at Check your audio's start.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, an unconnected <see cref="FlexBase"/>, the
/// radio's report stood in for by <see cref="RecorderControls.FactsOverride"/>
/// (for this class's own rig only, so no other class's press can reach a
/// warning), and the warning itself answered by
/// <see cref="RecorderControls.AskOverride"/>, which is set before any fact
/// claims a recording and cleared after. No window is ever built. What these
/// cannot see is the dialog on screen and what the screen reader says as it
/// opens: bench steps in the #999 report.
/// </remarks>
[Collection("SliceOverwriteWarning")]
public sealed class SliceOverwriteWarningTests : IDisposable
{
    private const string Serial = "1234-5678-8600-0995";
    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly List<RecorderControls.SliceOverwriteQuestion> _asked = new();
    private readonly List<SliceRecorderTarget> _backedUp = new();
    private SliceOverwriteFacts? _facts;
    private SliceOverwriteReply _reply = new(SliceOverwriteAnswer.Record, false);
    private bool _backupSucceeds = true;
    private Func<Task>? _whileAsking;

    public SliceOverwriteWarningTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        RecorderControls.AskOverride = question =>
        {
            _asked.Add(question);
            _whileAsking?.Invoke().GetAwaiter().GetResult();
            return _reply;
        };
        RecorderControls.BackUpOverride = (rig, target) =>
        {
            _backedUp.Add(target);
            return Task.FromResult<SliceBackupOutcome?>(new(_backupSucceeds ? SliceBackupResult.BackedUp : SliceBackupResult.Stopped,
                _facts?.Letter ?? "", 30));
        };
        RecorderControls.FactsOverride = (rig, target) => ReferenceEquals(rig, _rig) ? _facts : rig.SliceOverwriteFactsFor(target);
        TuneDoors.UpOverride = _ => false;
        RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(Serial, true);
    }

    public void Dispose()
    {
        RecorderControls.FactsOverride = null;
        RecorderControls.BackUpOverride = null;
        RecorderControls.AskOverride = null;
        TuneDoors.UpOverride = null;
        try { _rig.Dispose(); } catch { }
    }

    private static SliceTakeStamp Stamp(string folder) => new()
    {
        Letter = "B", RecordedSeconds = 30, MadeUtc = DateTime.UtcNow, Source = SliceTakeSource.F11, BackupTakeDirectory = folder,
    };

    private void SliceHolds(bool recording, bool backedUp = false, bool warn = true)
    {
        string folder = Path.Combine(Path.GetTempPath(), "jjflex-999-" + Guid.NewGuid().ToString("N"));
        if (backedUp) WriteATake(folder);
        _facts = new SliceOverwriteFacts(Serial, "B", recording, recording ? 30 : null, backedUp ? Stamp(folder) : null, warn);
    }

    /// <summary>A real, complete take: the live backup test now reads the take file, not just the folder.</summary>
    private static void WriteATake(string folder)
    {
        using var writer = new AudioTakeWriter(folder, TakeKind.SliceRecording,
            new[] { new KeyValuePair<string, string>("serial", Serial) });
        var audio = new float[AudioTakeFields.Rate];
        for (int i = 0; i < audio.Length; i++) audio[i] = 0.1f * (float)Math.Sin(i * 0.05);
        writer.BeginPlayback();
        writer.Write(audio, audio.Length);
        writer.EndPlayback();
        writer.Close(true, "observed-stop");
    }

    private RecorderOutcome PressF11() =>
        RecorderControls.ToggleSliceRecordAsync(_rig, SliceRecorderTarget.ActiveSlice, speak: _ => { }).GetAwaiter().GetResult();

    /// <summary>The press reached the radio's recorder: an unconnected rig answers that no radio is connected.</summary>
    private static bool ReachedTheRecorder(RecorderOutcome outcome) => outcome.Message == Lexicon.Get("audio.no_radio_connected");

    [Fact]
    public void An_empty_slice_records_without_a_question()
    {
        SliceHolds(recording: false);
        Assert.True(ReachedTheRecorder(PressF11()));
        Assert.Empty(_asked);
    }

    [Fact]
    public void The_setting_off_records_without_a_question()
    {
        SliceHolds(recording: true, warn: false);
        Assert.True(ReachedTheRecorder(PressF11()));
        Assert.Empty(_asked);
    }

    [Fact]
    public void A_recording_not_backed_up_asks_in_the_not_backed_up_form_and_Record_records()
    {
        SliceHolds(recording: true);
        Assert.True(ReachedTheRecorder(PressF11()));
        var question = Assert.Single(_asked);
        Assert.False(question.BackedUp);
        Assert.Equal("B", question.Letter);
        Assert.Equal(30, question.Seconds);
        Assert.Empty(_backedUp);
    }

    [Fact]
    public void A_backed_up_recording_asks_in_the_backed_up_form()
    {
        SliceHolds(recording: true, backedUp: true);
        Assert.True(ReachedTheRecorder(PressF11()));
        Assert.True(Assert.Single(_asked).BackedUp);
    }

    [Fact]
    public void Cancel_records_nothing_and_leaves_the_setting_alone()
    {
        SliceHolds(recording: true);
        _reply = new(SliceOverwriteAnswer.Cancel, false);
        var outcome = PressF11();
        Assert.False(ReachedTheRecorder(outcome));
        Assert.False(outcome.Success);
        Assert.Single(_asked);
        Assert.True(RadioConfig.LoadForRadio(Serial).WarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void Dont_show_again_with_Record_turns_the_setting_off()
    {
        SliceHolds(recording: true);
        _reply = new(SliceOverwriteAnswer.Record, true);
        Assert.True(ReachedTheRecorder(PressF11()));
        Assert.False(RadioConfig.LoadForRadio(Serial).WarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void Back_up_first_backs_up_the_named_slice_then_records_on_it()
    {
        SliceHolds(recording: true);
        _reply = new(SliceOverwriteAnswer.BackUpFirst, false);
        Assert.True(ReachedTheRecorder(PressF11()));
        Assert.Equal(SliceRecorderTarget.Letter('B'), Assert.Single(_backedUp));
    }

    [Fact]
    public void A_backup_that_did_not_finish_records_nothing()
    {
        SliceHolds(recording: true);
        _reply = new(SliceOverwriteAnswer.BackUpFirst, false);
        _backupSucceeds = false;
        Assert.False(ReachedTheRecorder(PressF11()));
        Assert.Single(_backedUp);
    }

    [Fact]
    public void A_second_press_while_the_warning_is_open_answers_nothing()
    {
        SliceHolds(recording: true);
        RecorderOutcome? second = null;
        _whileAsking = async () =>
        {
            _whileAsking = null;
            second = await RecorderControls.ToggleSliceRecordAsync(_rig, SliceRecorderTarget.ActiveSlice, speak: _ => { });
        };
        Assert.True(ReachedTheRecorder(PressF11()));
        Assert.Single(_asked);
        Assert.NotNull(second);
        Assert.False(ReachedTheRecorder(second!));
    }

    [Fact]
    public void Check_your_audio_asks_the_same_question_for_its_slice()
    {
        SliceHolds(recording: true);
        Assert.True(RecorderControls.NeedsOverwriteQuestion(_rig, SliceRecorderTarget.TransmitSlice));
        SliceHolds(recording: false);
        Assert.False(RecorderControls.NeedsOverwriteQuestion(_rig, SliceRecorderTarget.TransmitSlice));
    }

    // ── Where the gate sits, read from the source ──

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + signature);
        int depth = 0;
        for (int i = source.IndexOf('{', start); i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }
        return source[start..];
    }

    [Fact]
    public void The_shared_record_door_asks_only_when_starting_and_before_the_recorder()
    {
        string door = Body(Source("JJFlexWpf", "RecorderControls.cs"), "public static async Task<RecorderOutcome> ToggleSliceRecordAsync(");
        int starting = door.IndexOf("if (rig != null && !rig.IsSliceRecording(target))\n", StringComparison.Ordinal);
        if (starting < 0) starting = door.IndexOf("if (rig != null && !rig.IsSliceRecording(target))\r\n", StringComparison.Ordinal);
        int asks = door.IndexOf("ConfirmOverwriteAsync(rig, target)", StringComparison.Ordinal);
        int records = door.IndexOf("rig.ToggleSliceRecordAsync(target, owner)", StringComparison.Ordinal);
        Assert.True(starting > 0 && asks > starting && records > asks);
    }

    [Theory]
    [InlineData("JJFlexWpf", "FreqOutHandlers.cs")]
    [InlineData("JJFlexWpf", "KeyCommands.cs")]
    [InlineData("JJFlexWpf", "NativeMenuBar.cs")]
    [InlineData("JJFlexWpf", "Controls", "ScreenFieldsPanel.Recorder.cs")]
    public void Every_record_door_goes_through_the_shared_door(params string[] file)
    {
        string source = Source(file);
        Assert.Contains("RecorderControls.ToggleSliceRecordAsync(", source);
        // Positive control: no door reaches the radio's recorder around it.
        Assert.DoesNotContain(".ToggleSliceRecordAsync(target", source.Replace("RecorderControls.ToggleSliceRecordAsync(", ""));
    }

    [Fact]
    public void Check_your_audio_asks_before_its_countdown()
    {
        string toggle = Body(Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"), "public void ToggleAudioCheck()");
        int asks = toggle.IndexOf("RecorderControls.NeedsOverwriteQuestion(", StringComparison.Ordinal);
        int admits = toggle.IndexOf("AdmitTakeBeforeCountdown(", StringComparison.Ordinal);
        int radioRecording = toggle.IndexOf("SelectedMethod == AudioCheckListenMethods.RecordPlayback", StringComparison.Ordinal);
        Assert.True(radioRecording > 0 && asks > radioRecording && admits > asks);
    }

    [Fact]
    public void Play_takes_offers_only_Play_and_Back_up_on_a_slice_recording_row()
    {
        string dialog = Source("JJFlexWpf", "Dialogs", "PlayTakesDialog.cs");
        string refresh = Body(dialog, "private void RefreshControls()");
        int slice = refresh.IndexOf("if (CurrentSlice != null)", StringComparison.Ordinal);
        int end = refresh.IndexOf("return;", slice, StringComparison.Ordinal);
        Assert.True(slice > 0 && end > slice);
        string branch = refresh.Substring(slice, end - slice);
        Assert.Contains("Shown(_play, true);", branch);
        Assert.Contains("Shown(_backUp, true);", branch);
        foreach (string hidden in new[] { "_choose", "_compare", "_detailsButton", "_saveWav", "_saveFlac", "_delete", "_saveNote", "_rename", "_addToTest" })
            Assert.Contains(hidden, branch.Substring(branch.IndexOf("new[] {", StringComparison.Ordinal)));
        // No radio, no slice rows: only the radio can say what it holds.
        Assert.Contains("_rig?.IsConnected == true ? SliceRecordingListing.Rows(_rig, DateTime.Now)", dialog);
        // Play goes through the listen-only door; what that door does with the
        // radio keyed is the behaviour test below, not this line.
        Assert.Contains("RecorderControls.ToggleSliceListenAsync(_rig, sliceRow.Target, owner: this)", dialog);
        Assert.DoesNotContain("ToggleSlicePlayAsync(_rig, sliceRow.Target", dialog);
    }

    /// <summary>Key this class's rig as the radio's own MOX report does, with no radio object behind it.</summary>
    private void KeyTheRig(bool keyed) =>
        typeof(FlexBase).GetMethod("noteRadioMox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_rig, new object[] { keyed });

    [Fact]
    public void Play_takes_slice_Play_is_refused_with_the_radio_already_keyed()
    {
        KeyTheRig(true);
        Assert.True(_rig.Transmit);
        var refused = RecorderControls.ToggleSliceListenAsync(_rig, SliceRecorderTarget.Letter('B'), speak: _ => { }, owner: this)
            .GetAwaiter().GetResult();
        Assert.False(refused.Success);
        Assert.Equal(Lexicon.Get("audio.recorder.listen_refused_transmitting", ("letter", "")), refused.Message);
        // Positive control: unkeyed, the same press goes on to the recorder, which
        // on this unconnected rig answers that no radio is connected.
        KeyTheRig(false);
        var reached = RecorderControls.ToggleSliceListenAsync(_rig, SliceRecorderTarget.Letter('B'), speak: _ => { }, owner: this)
            .GetAwaiter().GetResult();
        Assert.True(ReachedTheRecorder(reached));
    }

    [Fact]
    public void The_setting_sits_under_Recorder_and_is_saved_on_OK_and_Apply()
    {
        string xaml = Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml");
        int group = xaml.IndexOf("<GroupBox Header=\"Recorder\"", StringComparison.Ordinal);
        int panel = xaml.IndexOf("x:Name=\"RecorderPanel\"", StringComparison.Ordinal);
        Assert.True(group > 0 && panel > group && panel - group < 600);
        string part = Source("JJFlexWpf", "Dialogs", "SettingsDialog.SliceOverwrite.cs");
        Assert.Contains("RecorderPanel.Children.Insert(", part);
        Assert.Contains("RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(serial, warn)", part);
        string settings = Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
        Assert.Contains("InitializeSliceOverwriteSetting();", settings);
        Assert.Contains("CommitSliceOverwriteSetting();", settings);
    }

    // ── The setting is an intent: never hidden, kept across a disconnect (repair round 1) ──

    [Fact]
    public void An_edit_survives_a_disconnect_and_is_saved_to_its_radio()
    {
        var edits = new SliceOverwriteSettingEdits();
        var saved = new Dictionary<string, bool>();
        Assert.True(edits.Show("A-SERIAL", _ => true, null));        // connected to A
        edits.Edit(false);
        // Disconnected: the box still shows A, the radio last connected, with the edit.
        Assert.False(edits.Show("A-SERIAL", _ => true, null));
        edits.Commit((serial, warn) => { saved[serial] = warn; return true; }, _ => throw new InvalidOperationException());
        Assert.Equal(new Dictionary<string, bool> { ["A-SERIAL"] = false }, saved);
        Assert.Empty(edits.Pending);
    }

    [Fact]
    public void With_no_radio_ever_used_the_box_shows_on_and_a_choice_waits_for_the_next_radio()
    {
        var edits = new SliceOverwriteSettingEdits();
        Assert.True(edits.Show("", _ => throw new InvalidOperationException(), null));
        edits.Edit(false);
        bool? held = null;
        edits.Commit((_, _) => throw new InvalidOperationException(), warn => held = warn);
        Assert.False(held);
        // Opened again before any radio: it shows the choice held for the next one.
        Assert.False(new SliceOverwriteSettingEdits().Show("", _ => true, held));
    }

    [Fact]
    public void An_edit_made_before_any_radio_moves_to_the_first_radio_known()
    {
        var edits = new SliceOverwriteSettingEdits();
        edits.Show("", _ => true, null);
        edits.Edit(false);
        Assert.False(edits.Show("B-SERIAL", _ => true, null));
        var saved = new Dictionary<string, bool>();
        edits.Commit((serial, warn) => { saved[serial] = warn; return true; }, _ => throw new InvalidOperationException());
        Assert.Equal(new Dictionary<string, bool> { ["B-SERIAL"] = false }, saved);
    }

    [Fact]
    public void An_edit_stays_with_the_radio_it_was_made_for_when_the_target_changes()
    {
        var edits = new SliceOverwriteSettingEdits();
        edits.Show("A-SERIAL", _ => true, null);
        edits.Edit(false);
        Assert.True(edits.Show("B-SERIAL", _ => true, null));        // B shows its own saved value
        var saved = new Dictionary<string, bool>();
        edits.Commit((serial, warn) => { saved[serial] = warn; return true; }, _ => { });
        Assert.Equal(new Dictionary<string, bool> { ["A-SERIAL"] = false }, saved);
    }

    [Fact]
    public void An_edit_that_could_not_be_saved_stays_pending()
    {
        var edits = new SliceOverwriteSettingEdits();
        edits.Show("A-SERIAL", _ => true, null);
        edits.Edit(false);
        edits.Commit((_, _) => false, _ => { });
        Assert.False(edits.Pending["A-SERIAL"]);
    }

    [Fact]
    public void The_setting_box_is_never_hidden()
    {
        string part = Source("JJFlexWpf", "Dialogs", "SettingsDialog.SliceOverwrite.cs");
        Assert.DoesNotContain("Visibility.Collapsed", part);
        Assert.DoesNotContain("Visibility.Hidden", part);
        Assert.Contains("audio.recorder.overwrite.setting_applies_next", part);
    }

    [Fact]
    public void The_warning_puts_Record_first_and_default_and_swallows_F11()
    {
        string dialog = Source("JJFlexWpf", "Dialogs", "SliceOverwriteWarningDialog.cs");
        int record = dialog.IndexOf("\"audio.recorder.overwrite.record\"", StringComparison.Ordinal);
        int backUp = dialog.IndexOf("\"audio.recorder.overwrite.back_up_first\"", StringComparison.Ordinal);
        int cancel = dialog.IndexOf("\"audio.recorder.overwrite.cancel\"", StringComparison.Ordinal);
        int box = dialog.IndexOf("root.Children.Add(_dontShow)", StringComparison.Ordinal);
        Assert.True(record > 0 && backUp > record && cancel > backUp && box > cancel);
        Assert.Contains("_record.IsDefault = true;", dialog);
        Assert.Contains("protected override void FocusFirstControl() => _record.Focus();", dialog);
        Assert.Contains("e.Key == Key.F11", dialog);
        Assert.Contains("answer != SliceOverwriteAnswer.Cancel && _dontShow.IsChecked == true", dialog);
    }
}
