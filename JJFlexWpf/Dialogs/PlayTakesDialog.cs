using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJTrace;
using Radios;
using Radios.Listenback;
using Radios.Speech;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Play takes (Unified takes, #864): every take on this computer, of every
/// kind and from every radio, in one list, newest first, one sentence per row.
/// Play, compare with a take you choose, details, a note, renaming the test,
/// saving a copy as WAV or FLAC, and delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own window</b> (Noel, 2026-11-02, Q6), reachable directly, not
/// buried in Check your audio. A Show selector, not tabs, because comparing
/// picks two takes that may be of different kinds: the first pick stays put
/// while Show changes to find the second.
/// </para>
/// <para>
/// <b>Never touches the radio.</b> Opening it never connects, starts a check
/// or keys anything. It works with no radio and over SmartLink: everything it
/// does reads files on this computer. Its logic lives in
/// <see cref="TakeListing"/>, <see cref="TakeAnalysis"/> and
/// <see cref="TakeDeletionGuard"/>, which are tested without this window.
/// </para>
/// <para>
/// <b>Not built here</b>, reported instead: "Add a take to this test",
/// because no take this build can make joins a test yet (the playback
/// capture is not wired into Check your audio).
/// </para>
/// </remarks>
public sealed class PlayTakesDialog : JJFlexDialog
{
    private readonly FlexBase? _rig;
    private readonly ComboBox _show = new() { MinWidth = 200 };
    private readonly ListBox _list = new();
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 90,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _note = new() { MinWidth = 240 };
    private readonly TextBox _testName = new() { MinWidth = 240 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _play, _choose, _compare, _detailsButton, _saveNote, _rename, _saveWav, _saveFlac, _delete;
    private IReadOnlyList<TakeRow> _rows = Array.Empty<TakeRow>();
    private TakeRow? _chosen;
    private bool _busy, _quiet;

    private static PlayTakesDialog? _open;

    protected internal override bool SpeaksOwnTitle => false;

    /// <summary>Open the window, modally, or bring the open one forward. Works with no radio.</summary>
    public static void Show(FlexBase? rig)
    {
        if (_open != null)
        {
            try { _open.Activate(); } catch (InvalidOperationException) { }
            return;
        }
        var dialog = new PlayTakesDialog(rig);
        _open = dialog;
        try { dialog.ShowModalDialog(); }
        finally { if (ReferenceEquals(_open, dialog)) _open = null; }
    }

    private PlayTakesDialog(FlexBase? rig)
    {
        _rig = rig;
        Title = Lexicon.Get("audio.takes.title");
        AutomationProperties.SetHelpText(this, Lexicon.Get("audio.takes.description"));
        Width = 720; Height = 600;
        ResizeMode = ResizeMode.CanResize;

        // Children in TAB ORDER: Show, the list, the actions, details, note, test name.
        var root = new DockPanel { Margin = new Thickness(12) };
        var showRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        showRow.Children.Add(new Label { Content = Lexicon.Get("audio.takes.show_label"), Target = _show });
        AutomationProperties.SetName(_show, Lexicon.Get("audio.takes.show_label"));
        JJFlexHelp.SetText(_show, Lexicon.Get("audio.takes.help.show"));
        _show.SelectionChanged += (_, _) => { if (!_quiet) Populate(keepSelection: true); };
        showRow.Children.Add(_show);
        DockPanel.SetDock(showRow, Dock.Top);
        root.Children.Add(showRow);

        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        var actions = new WrapPanel();
        _play = AddButton(actions, Lexicon.Get("audio.takes.play"), () => _ = PlayAsync(), "audio.takes.help.play");
        _choose = AddButton(actions, Lexicon.Get("audio.takes.choose_first"), Choose, "audio.takes.help.choose_first");
        _compare = AddButton(actions, Lexicon.Get("audio.takes.compare"), () => _ = CompareAsync(), "audio.takes.help.compare");
        _detailsButton = AddButton(actions, Lexicon.Get("audio.takes.details"), () => _ = DetailsAsync(), "audio.takes.help.details");
        _saveWav = AddButton(actions, Lexicon.Get("audio.takes.save_wav"), () => _ = SaveCopyAsync(flac: false), "audio.takes.help.save_copy");
        _saveFlac = AddButton(actions, Lexicon.Get("audio.takes.save_flac"), () => _ = SaveCopyAsync(flac: true), "audio.takes.help.save_copy");
        _delete = AddButton(actions, Lexicon.Get("audio.takes.delete"), Delete, "audio.takes.help.delete");
        bottom.Children.Add(actions);
        AutomationProperties.SetName(_details, Lexicon.Get("audio.takes.details"));
        JJFlexHelp.SetText(_details, Lexicon.Get("audio.takes.help.details"));
        bottom.Children.Add(_details);
        var noteRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        noteRow.Children.Add(new Label { Content = Lexicon.Get("audio.takes.note_label"), Target = _note });
        AutomationProperties.SetName(_note, Lexicon.Get("audio.takes.note_label"));
        JJFlexHelp.SetText(_note, Lexicon.Get("audio.takes.help.note"));
        noteRow.Children.Add(_note);
        _saveNote = AddButton(noteRow, Lexicon.Get("audio.takes.save_note"), SaveNote, "audio.takes.help.note");
        bottom.Children.Add(noteRow);
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        testRow.Children.Add(new Label { Content = Lexicon.Get("audio.takes.test_name_label"), Target = _testName });
        AutomationProperties.SetName(_testName, Lexicon.Get("audio.takes.test_name_label"));
        JJFlexHelp.SetText(_testName, Lexicon.Get("audio.takes.help.rename"));
        testRow.Children.Add(_testName);
        _rename = AddButton(testRow, Lexicon.Get("audio.takes.rename"), Rename, "audio.takes.help.rename");
        bottom.Children.Add(testRow);
        var close = AddButton(bottom, Lexicon.Get("audio.takes.close"), Close, "audio.takes.help.close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Children.Add(_status);
        root.Children.Add(bottom);

        AutomationProperties.SetName(_list, Lexicon.Get("audio.takes.list"));
        JJFlexHelp.SetText(_list, Lexicon.Get("audio.takes.help.list"));
        _list.SelectionChanged += (_, _) => { if (!_quiet) RefreshControls(); };
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Delete(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; _ = PlayAsync(); }
        };
        root.Children.Add(_list);
        Content = root;
        Populate(keepSelection: false);
    }

    protected override void FocusFirstControl()
    {
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = Math.Max(0, _list.SelectedIndex);
            (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) as ListBoxItem)?.Focus();
            if (!_list.IsKeyboardFocusWithin) _list.Focus();
        }
        else _show.Focus();
    }

    private TakeRow? Current => (_list.SelectedItem as ListBoxItem)?.Tag as TakeRow;
    private static string Root => ListenbackTakes.Root;
    private string ShowOption => (_show.SelectedItem as ComboBoxItem)?.Tag as string ?? TakeListing.ShowAll;

    /// <summary>Read the takes again and rebuild the Show choices and the list, keeping the selection where it can.</summary>
    private void Populate(bool keepSelection)
    {
        string? selected = keepSelection ? Current?.Directory : null;
        try { _rows = TakeListing.Rows(Root, DateTime.Now); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is System.Text.Json.JsonException)
        {
            Tracing.TraceLine("PlayTakesDialog: " + ex, TraceLevel.Error);
            _rows = Array.Empty<TakeRow>();
            _status.Text = Lexicon.Get("audio.takes.unreadable");
        }
        _quiet = true;
        try
        {
            string option = ShowOption;
            var options = TakeListing.ShowOptions(_rows);
            if (!options.Contains(option)) option = TakeListing.ShowAll;
            _show.Items.Clear();
            foreach (string o in options)
            {
                var item = new ComboBoxItem { Content = TakeListing.ShowName(o), Tag = o };
                AutomationProperties.SetName(item, TakeListing.ShowName(o));
                _show.Items.Add(item);
                if (o == option) _show.SelectedItem = item;
            }
            _list.Items.Clear();
            var shown = TakeListing.Filter(_rows, option).ToList();
            foreach (var row in shown)
            {
                var item = new ListBoxItem { Content = row.Sentence, Tag = row };
                AutomationProperties.SetName(item, row.Sentence);
                _list.Items.Add(item);
            }
            if (shown.Count == 0)
            {
                var empty = new ListBoxItem { Content = Lexicon.Get("audio.takes.empty"), IsEnabled = false };
                AutomationProperties.SetName(empty, Lexicon.Get("audio.takes.empty"));
                _list.Items.Add(empty);
            }
            int index = selected == null ? 0 : Math.Max(0, shown.FindIndex(r => r.Directory == selected));
            _list.SelectedIndex = shown.Count == 0 ? -1 : Math.Min(index, shown.Count - 1);
        }
        finally { _quiet = false; }
        RefreshControls();
    }

    /// <summary>
    /// Actions that do not apply leave the tab order (Collapsed), and the
    /// details line says why (accessibility rule: unsupported controls stay
    /// out of the tab order).
    /// </summary>
    private void RefreshControls()
    {
        var row = Current;
        bool any = row != null;
        bool readable = any && row!.Entry.HeaderReadable && row.Kind != TakeKind.Damaged;
        Shown(_play, readable);
        Shown(_choose, readable);
        Shown(_compare, readable && _chosen != null && _chosen.Directory != row!.Directory);
        Shown(_detailsButton, any);
        Shown(_saveWav, readable);
        Shown(_saveFlac, readable);
        Shown(_delete, any);
        Shown(_saveNote, any);
        _note.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        bool member = any && row!.TestId.Length > 0;
        Shown(_rename, member);
        _testName.Visibility = member ? Visibility.Visible : Visibility.Collapsed;
        if (any)
        {
            _note.Text = row!.Note;
            _testName.Text = row.TestName;
            _details.Text = readable ? "" : Lexicon.Get("audio.takes.cannot_read");
        }
    }

    private void Shown(Button button, bool shown)
    {
        if (!shown && button.IsKeyboardFocusWithin) _list.Focus();
        button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task PlayAsync()
    {
        var row = Current;
        if (row == null || _busy) return;
        if (RecordingPlayer.IsPlaying) { RecordingPlayer.Stop(); return; }
        _busy = true;
        try
        {
            var prepared = await ListenbackTakes.PreparePlaybackAsync(row.Directory, 0);
            if (prepared.Trouble != null) { Refuse(prepared.Trouble); return; }
            await ListenbackTakes.PlayAfterClickAsync(prepared.Path!, Lexicon.Get(prepared.AnnouncementKey), _rig,
                _ => IsVisible, offAirListeningGain: !prepared.Take.IsAudio);
        }
        catch (Exception ex) { Fail("play", ex); }
        finally { _busy = false; }
    }

    private void Choose()
    {
        var row = Current;
        if (row == null) return;
        _chosen = row;
        Say(Lexicon.Get("audio.takes.chosen", ("take", row.Sentence)));
        RefreshControls();
    }

    /// <summary>Compare the chosen take (the earlier side) with the selected one, through the one entry point.</summary>
    private async Task CompareAsync()
    {
        var row = Current;
        if (row == null || _busy) return;
        if (_chosen == null) { Refuse(Lexicon.Get("audio.takes.compare_none")); return; }
        if (_chosen.Directory == row.Directory) { Refuse(Lexicon.Get("audio.takes.compare_same")); return; }
        _busy = true;
        try
        {
            string first = _chosen.Directory, second = row.Directory;
            var difference = await Task.Run(() => TakeAnalysis.CompareTakes(first, second));
            string sentence = difference.Road == "audio" ? ListenbackResultText.AudioRoadSentence(difference)
                : difference.Comparable
                    ? ListenbackTakes.WithSetupChanges(difference, difference.Reason == "overall-average"
                        ? ListenbackTakes.AverageDifferenceSentence(difference) : ListenbackTakes.DifferenceSentence(difference))
                    : difference.Reason == "unlike-takes:source" ? ListenbackResultText.UnlikeSource(difference)
                    : Lexicon.Get("audio.iq.auto_unavailable");
            _details.Text = sentence;
            Say(sentence);
        }
        catch (Exception ex) { Fail("compare", ex); }
        finally { _busy = false; }
    }

    private async Task DetailsAsync()
    {
        var row = Current;
        if (row == null || _busy) return;
        _busy = true;
        try
        {
            var (take, stats) = await Task.Run(() =>
            {
                var t = TakeReader.Inspect(row.Directory);
                return (t, t.CanMeasure ? TakeStatistics.Measure(row.Directory, reuseRender: true) : null);
            });
            _details.Text = TakeListing.Details(row, take, stats);
            _details.Focus();
        }
        catch (Exception ex) { Fail("details", ex); }
        finally { _busy = false; }
    }

    private void SaveNote()
    {
        var row = Current;
        if (row == null) return;
        try
        {
            TakeSession.SetNote(Root, row.Directory, _note.Text);
            Populate(keepSelection: true);
            Say(Lexicon.Get("audio.takes.note_saved"));
        }
        catch (Exception ex) { Fail("note", ex); }
    }

    private void Rename()
    {
        var row = Current;
        if (row == null || row.TestId.Length == 0) return;
        try
        {
            string refusal = TakeSession.Rename(Root, row.TestId, _testName.Text);
            if (refusal.Length > 0)
            {
                string key = refusal switch
                {
                    TakeSession.RenameEmpty => "audio.takes.rename_empty",
                    TakeSession.RenameTooLong => "audio.takes.rename_too_long",
                    TakeSession.RenameTaken => "audio.takes.rename_taken",
                    _ => "audio.takes.rename_unknown"
                };
                Refuse(Lexicon.Get(key));
                return;
            }
            Populate(keepSelection: true);
            Say(Lexicon.Get("audio.takes.renamed", ("name", _testName.Text.Trim())));
        }
        catch (Exception ex) { Fail("rename", ex); }
    }

    private async Task SaveCopyAsync(bool flac)
    {
        var row = Current;
        if (row == null || _busy) return;
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = Lexicon.Get(flac ? "audio.takes.save_flac" : "audio.takes.save_wav"),
            FileName = Path.GetFileName(row.Directory) + (flac ? ".flac" : ".wav"),
            Filter = flac ? "FLAC (*.flac)|*.flac" : "WAV (*.wav)|*.wav",
            InitialDirectory = Path.Combine(RadioConfig.AppDataRoot, "Recordings"),
        };
        if (save.ShowDialog(this) != true) return;
        _busy = true;
        try
        {
            string directory = row.Directory, destination = save.FileName;
            bool saved = await Task.Run(() => TakeListing.SaveCopy(directory, destination, flac));
            if (saved) SayAfterFocusSettles(Lexicon.Get("audio.takes.saved_copy", ("file", Path.GetFileName(destination))));
            else SayAfterFocusSettles(Lexicon.Get("audio.takes.save_failed"), refusal: true);
        }
        catch (Exception ex) { Fail("save", ex); }
        finally { _busy = false; }
    }

    /// <summary>The Delete key and the Delete button share this one path and its confirmation.</summary>
    private void Delete()
    {
        var row = Current;
        if (row == null || _busy) return;
        var confirm = new ConfirmActionDialog(Lexicon.Get("audio.takes.delete_title"),
            Lexicon.Get("audio.takes.delete_message", ("take", row.Sentence)),
            question: Lexicon.Get("audio.takes.delete_question"),
            yesLabel: Lexicon.Get("audio.takes.delete_yes"), noLabel: Lexicon.Get("audio.takes.delete_no"),
            enterConfirms: true) { Owner = this };
        bool confirmed = confirm.ShowModalDialog() == true;
        _list.Focus();
        if (!confirmed) return;
        if (RecordingPlayer.IsPlaying) RecordingPlayer.StopWithoutSpeaking();
        var recorder = ComputerRecorder.Current as ComputerFlacRecorder;
        var verdict = TakeDeletionGuard.Delete(Root, row.Directory,
            Path.Combine(RadioConfig.AppDataRoot, "listenback-recovery"), new CheckRestoreReminderStore(RadioConfig.AppDataRoot),
            busy: folder => recorder?.IsCapturing(folder) == true || TakeObligations.IsOwed(folder));
        if (_chosen?.Directory == row.Directory && verdict.Allowed) _chosen = null;
        Populate(keepSelection: !verdict.Allowed);
        string key = verdict.Reason switch
        {
            "" => "audio.takes.deleted",
            TakeDeletionGuard.RestoreOwed or TakeDeletionGuard.PutBackOwed => "audio.takes.delete_refused_owed",
            TakeDeletionGuard.StoreUnreadable => "audio.takes.delete_refused_unreadable",
            TakeDeletionGuard.Busy => "audio.takes.delete_refused_busy",
            _ => "audio.takes.delete_failed"
        };
        SayAfterFocusSettles(Lexicon.Get(key), refusal: !verdict.Allowed);
    }

    // ── speech ──
    // DRAFT PROSE: awaiting Noel. Every sentence is an audio.takes.* lexicon key.

    private void Say(string text)
    {
        _status.Text = text;
        ScreenReaderOutput.Speak(text, SpeechIntent.Interrupt, VerbosityLevel.Terse, subject: SpeechSubject.PlayTakes);
    }

    private void Refuse(string text)
    {
        _status.Text = text;
        EarconPlayer.LeaderInvalidTone();
        ScreenReaderOutput.Speak(text, SpeechIntent.Interrupt, VerbosityLevel.Critical, subject: SpeechSubject.PlayTakes);
    }

    /// <summary>Speech flushes on a window change, so a sentence after a confirmation or save window waits for focus to land.</summary>
    private void SayAfterFocusSettles(string text, bool refusal = false)
    {
        _status.Text = text;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(300);
            if (refusal) Refuse(text); else Say(text);
        }));
    }

    private void Fail(string what, Exception ex)
    {
        Tracing.TraceLine("PlayTakesDialog: " + what + " failed: " + ex, TraceLevel.Error);
        Refuse(Lexicon.Get("audio.takes.failed"));
    }

    private Button AddButton(Panel panel, string label, Action onClick, string helpKey)
    {
        var button = new Button { Content = label, MinWidth = 96, Height = 28, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(button, label);
        JJFlexHelp.SetText(button, Lexicon.Get(helpKey));
        button.Click += (_, _) => onClick();
        panel.Children.Add(button);
        return button;
    }
}
