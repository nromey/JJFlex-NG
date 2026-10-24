using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using JJTrace;
using Radios;
using Radios.Speech;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Recorded QSOs (#787, Tape Deck track 4): the computer recordings, newest
/// first. Choose one for Play from computer, play it here, step through it,
/// trim it, save it as another format, move it to the Recycle Bin, or prune
/// old ones. Designed by Codex (2026-10-24) and built as amended by Noel's
/// rulings of the same afternoon.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tab order, as ruled:</b> the list; the step size, the very next stop
/// (Noel 16:03); Play or pause; Stop, only while a preview is running; the
/// playback position; Set trim start; Set trim end; the selection line; Use
/// whole recording; Save As; Delete; Prune; the newest-recording checkbox
/// (15:55); Close. A control that cannot act now is disabled, which takes it
/// out of the tab order.
/// </para>
/// <para>
/// <b>Keys that work anywhere in the window, even while playing</b> (Noel
/// 16:04-16:05): "[" marks the trim start, "]" the end, "P" pauses and
/// resumes. None of them is Space or a Space chord: Ctrl+Space stays push to
/// talk and Shift+Space stays refused, through <see cref="DialogTransmitKeys"/>,
/// which sees every key before this window does. The list's own letter search
/// is switched off so "P" can never jump to a row.
/// </para>
/// <para>
/// <b>Escape</b> (16:05): while a preview plays or is paused, Escape stops it;
/// the next Escape closes. While transmitting, the transmit door unkeys
/// first and this window never sees that Escape. Closing any other way stops
/// the preview too.
/// </para>
/// <para>
/// <b>What it says.</b> The list and the controls announce themselves; the
/// window speaks only the answer to a key the operator pressed (where a step
/// landed, a trim mark, a delete) under <see cref="SpeechSubject.RecordedQsos"/>,
/// so a newer answer replaces an unheard older one. All of it is DRAFT PROSE
/// (#787): awaiting Noel, in the lexicon under <c>audio.qsos.*</c>.
/// </para>
/// <para>
/// <b>VOX.</b> The preview plays to this computer's speakers and pauses VOX,
/// or CW break-in, for as long as it plays, through the radio's one VOX owner,
/// exactly as slice playback does (<see cref="LocalPlaybackVox"/>). With no
/// radio there is nothing to pause.
/// </para>
/// </remarks>
public sealed class RecordedQsosDialog : JJFlexDialog
{
    private readonly FlexBase? _rig;
    private readonly RecordedQsoBrowser _browser = new();
    private readonly ListBox _list = new();
    private readonly TextBox _empty = new();
    private readonly ComboBox _step = new();
    private readonly Button _play, _stop, _setStart, _setEnd, _useWhole, _saveAs, _delete, _prune, _close;
    private readonly PositionSlider _position = new();
    private readonly TextBox _selection = new();
    private readonly CheckBox _newest = new();
    private readonly TextBlock _status = new();
    private readonly HashSet<string> _exporting = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RecordedQso> _rows = Array.Empty<RecordedQso>();
    private string? _previewPath;
    private bool _quietSelection;
    private bool _quietCheck;
    private bool _choiceAnnounced;
    private int _unreadable;
    private bool _starting;

    private static RecordedQsosDialog? _open;

    /// <summary>
    /// Open the window, modally. Works with no radio. Pressed again while it
    /// is open (its keys are global, so they work inside it), it brings the
    /// open one forward rather than stacking a second copy on top.
    /// </summary>
    public static void Show(FlexBase? rig)
    {
        if (_open != null)
        {
            try { _open.Activate(); } catch (InvalidOperationException) { }
            return;
        }
        var dialog = new RecordedQsosDialog(rig);
        _open = dialog;
        try { dialog.ShowModalDialog(); }
        finally { if (ReferenceEquals(_open, dialog)) _open = null; }
    }

    public RecordedQsosDialog(FlexBase? rig)
    {
        _rig = rig;
        Title = Lexicon.Get("audio.qsos.title");
        Width = 640;
        Height = 560;
        ResizeMode = ResizeMode.CanResize;

        // Three rows: the list fills the window, the controls sit under it,
        // the status line last. Children are added in TAB ORDER, which is how
        // WPF walks them; no TabIndex is set, because TabIndex is compared only
        // among siblings and would not reorder across these panels.
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_status, 2);

        var controls = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(controls, 1);

        // The step size, the next stop after the list (Noel 16:03).
        var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        stepRow.Children.Add(new Label { Content = Lexicon.Get("audio.qsos.step"), Target = _step, Padding = new Thickness(0, 4, 8, 0) });
        AutomationProperties.SetName(_step, Lexicon.Get("audio.qsos.step"));
        JJFlexHelp.SetText(_step, Lexicon.Get("audio.qsos.step_help"));
        foreach (int s in RecordedQsoBrowser.Steps)
        {
            string label = RecordedQsoWords.Step(s);
            var item = new ComboBoxItem { Content = label, Tag = s };
            AutomationProperties.SetName(item, label);
            _step.Items.Add(item);
        }
        _step.SelectedIndex = Array.IndexOf(RecordedQsoBrowser.Steps, RecordedQsoBrowser.DefaultStep);
        _step.MinWidth = 140;
        _step.SelectionChanged += (_, _) =>
        {
            if (_step.SelectedItem is ComboBoxItem { Tag: int seconds }) _browser.StepSeconds = seconds;
        };
        stepRow.Children.Add(_step);
        controls.Children.Add(stepRow);

        var transport = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _play = AddButton(transport, Lexicon.Get("audio.qsos.play"), () => _ = PlayOrPauseAsync());
        _stop = AddButton(transport, Lexicon.Get("audio.qsos.stop"), () => StopPreview(speak: true));
        controls.Children.Add(transport);

        AutomationProperties.SetName(_position, Lexicon.Get("audio.qsos.position"));
        _position.Minimum = 0;
        _position.Maximum = 1;
        _position.IsTabStop = true;
        _position.Margin = new Thickness(0, 0, 0, 6);
        _position.PreviewKeyDown += OnPositionKeyDown;
        controls.Children.Add(_position);

        var trim = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _setStart = AddButton(trim, Lexicon.Get("audio.qsos.set_start"), () => MarkStart());
        _setEnd = AddButton(trim, Lexicon.Get("audio.qsos.set_end"), () => MarkEnd());
        controls.Children.Add(trim);

        AutomationProperties.SetName(_selection, Lexicon.Get("audio.qsos.selection_label"));
        _selection.IsReadOnly = true;
        _selection.IsReadOnlyCaretVisible = true;
        _selection.TextWrapping = TextWrapping.Wrap;
        _selection.Margin = new Thickness(0, 0, 0, 6);
        controls.Children.Add(_selection);

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _useWhole = AddButton(actions, Lexicon.Get("audio.qsos.use_whole"), () => Say(_browser.UseWhole(), refresh: true));
        _saveAs = AddButton(actions, Lexicon.Get("audio.qsos.save_as"), () => _ = SaveAsAsync());
        _delete = AddButton(actions, Lexicon.Get("audio.qsos.delete"), () => DeleteSelected());
        _prune = AddButton(actions, Lexicon.Get("audio.qsos.prune"), () => Prune());
        controls.Children.Add(actions);

        _newest.Content = Lexicon.Get("audio.qsos.newest");
        AutomationProperties.SetName(_newest, Lexicon.Get("audio.qsos.newest"));
        JJFlexHelp.SetText(_newest, Lexicon.Get("audio.qsos.newest_help"));
        _newest.Margin = new Thickness(0, 0, 0, 6);
        _newest.Checked += (_, _) => OnNewestChanged(true);
        _newest.Unchecked += (_, _) => OnNewestChanged(false);
        controls.Children.Add(_newest);

        var closeRow = new WrapPanel();
        _close = AddButton(closeRow, Lexicon.Get("audio.qsos.close"), () => CloseWithResult(true));
        controls.Children.Add(closeRow);

        var listArea = new Grid();
        Grid.SetRow(listArea, 0);
        AutomationProperties.SetName(_list, Lexicon.Get("audio.qsos.list"));
        JJFlexHelp.SetText(_list, Lexicon.Get("audio.qsos.list_help"));
        _list.SelectionMode = SelectionMode.Single;
        _list.IsTextSearchEnabled = false; // "P" must never jump to a row (Noel 16:05).
        _list.SelectionChanged += OnListSelectionChanged;
        _list.PreviewKeyDown += OnListKeyDown;
        _list.MouseDoubleClick += (_, _) => _ = TogglePlayAsync();
        listArea.Children.Add(_list);

        AutomationProperties.SetName(_empty, Lexicon.Get("audio.qsos.list"));
        _empty.IsReadOnly = true;
        _empty.IsReadOnlyCaretVisible = true;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Text = Lexicon.Get("audio.qsos.empty");
        _empty.Visibility = Visibility.Collapsed;
        listArea.Children.Add(_empty);

        root.Children.Add(listArea);
        root.Children.Add(controls);
        root.Children.Add(_status);
        Content = root;

        RecordingPlayer.StateChanged += OnPlayerStateChanged;
        RecordingPlayer.PlaybackCompleted += OnPlaybackCompleted;
        Closed += (_, _) =>
        {
            RecordingPlayer.StateChanged -= OnPlayerStateChanged;
            RecordingPlayer.PlaybackCompleted -= OnPlaybackCompleted;
            // Closing any way at all stops the preview (Noel 16:05). The
            // speech would be flushed by the window change anyway.
            if (PreviewActive) RecordingPlayer.StopWithoutSpeaking();
        };
        // The list is filled now, before the base class's Loaded focuses the
        // first control, so focus can land on the chosen row. The count of
        // unreadable files is said after the title, which Loaded queues first.
        Populate(opening: true);
        Loaded += (_, _) =>
        {
            if (_unreadable > 0)
                Say(_unreadable == 1 ? Lexicon.Get("audio.qsos.unreadable_one")
                    : Lexicon.Get("audio.qsos.unreadable_many", ("count", _unreadable)), queue: true);
        };
    }

    // ── the list ────────────────────────────────────────────────────────

    private void Populate(bool opening, string? keepPath = null, int fallbackIndex = 0)
    {
        var scan = RecordedQsoCatalog.Scan();
        _rows = scan.Recordings;
        DateTime now = DateTime.Now;
        string? recordingNow = CurrentRecordingPath();
        // Rebuilding the rows removes the one that has the keyboard; put it
        // back on the selected row afterwards, or the operator is left on
        // nothing after a delete, a prune or a Save As into this folder.
        bool listHadFocus = !opening && (_list.IsKeyboardFocusWithin || _empty.IsKeyboardFocusWithin);
        _quietSelection = true;
        try
        {
            _list.Items.Clear();
            foreach (var q in _rows)
            {
                string text = RecordedQsoWords.Row(q, now, SamePath(q.Path, recordingNow));
                var item = new ListBoxItem { Content = text, Tag = q };
                AutomationProperties.SetName(item, text);
                _list.Items.Add(item);
            }

            int index;
            if (opening)
            {
                var (open, newest) = RecordedQsoBrowser.Opening(_rows, ComputerRecorder.ChosenRecording);
                index = open;
                SetNewestQuietly(newest);
            }
            else
            {
                index = keepPath == null ? -1 : IndexOf(keepPath);
                if (index < 0) index = Math.Min(fallbackIndex, _rows.Count - 1);
            }
            _list.SelectedIndex = index;
            _browser.Select(index >= 0 ? _rows[index] : null);
        }
        finally { _quietSelection = false; }

        bool empty = _rows.Count == 0;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RefreshControls();
        if (listHadFocus) FocusFirstControl();

        _unreadable = scan.Unreadable;
    }

    private int IndexOf(string path)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (SamePath(_rows[i].Path, path)) return i;
        return -1;
    }

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    protected override void FocusFirstControl()
    {
        if (_rows.Count == 0)
        {
            _empty.Focus();
            return;
        }
        FocusRow();
    }

    private void FocusRow()
    {
        int index = Math.Max(0, _list.SelectedIndex);
        if (index >= _list.Items.Count) return;
        _list.UpdateLayout();
        if (_list.Items[index] is ListBoxItem item)
        {
            _list.ScrollIntoView(item);
            item.Focus();
        }
        else _list.Focus();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_quietSelection) return;
        var q = (_list.SelectedItem as ListBoxItem)?.Tag as RecordedQso;
        if (PreviewActive && !SamePath(q?.Path, _previewPath))
            RecordingPlayer.StopWithoutSpeaking(); // a new row is a new recording; its own preview starts on Enter
        _browser.Select(q);
        if (q != null)
        {
            // Arrowing to a recording makes it Play from computer's choice and
            // unticks the newest box (Noel 15:55-15:56). Said once per opening,
            // after the reader has read the row, so the change is never silent.
            ComputerRecorder.ChosenRecording = q.Path;
            if (_newest.IsChecked == true)
            {
                SetNewestQuietly(false);
                if (!_choiceAnnounced)
                {
                    _choiceAnnounced = true;
                    Say(Lexicon.Get("audio.qsos.choice_follows"), queue: true);
                }
            }
        }
        RefreshControls();
    }

    private void OnNewestChanged(bool ticked)
    {
        if (_quietCheck) return;
        if (ticked) ComputerRecorder.ChosenRecording = null;
        else if (_browser.Current != null) ComputerRecorder.ChosenRecording = _browser.Current.Path;
    }

    private void SetNewestQuietly(bool ticked)
    {
        _quietCheck = true;
        try { _newest.IsChecked = ticked; }
        finally { _quietCheck = false; }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = TogglePlayAsync();
                break;
            case Key.Delete:
                e.Handled = true;
                DeleteSelected();
                break;
            case Key.Left:
            case Key.Right:
                // Left and Right step through the chosen recording from the
                // list itself (Noel 16:03); Up and Down still choose.
                e.Handled = true;
                StepPosition(e.Key == Key.Right ? 1 : -1);
                break;
        }
    }

    private void OnPositionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                e.Handled = true;
                StepPosition(-1);
                break;
            case Key.Right:
            case Key.Up:
                e.Handled = true;
                StepPosition(1);
                break;
            case Key.Home:
            case Key.End:
                e.Handled = true;
                MoveToEdge(e.Key == Key.End);
                break;
            case Key.PageUp:
            case Key.PageDown:
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The window-wide keys (Noel 16:04-16:05) and the preview's Escape (16:05).
    /// Runs before the base class's Escape-closes handler, and after the
    /// transmit door, which has already unkeyed on an Escape while transmitting
    /// and marked it handled.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && PreviewActive)
        {
            e.Handled = true;
            StopPreview(speak: true);
            return;
        }
        if (key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            HelpLauncher.ShowHelp("RecordedQsos");
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (DialogTransmitKeys.IsEditableText(e.OriginalSource as DependencyObject)) return;
        switch (key)
        {
            case Key.OemOpenBrackets:
                e.Handled = true;
                MarkStart();
                break;
            case Key.OemCloseBrackets:
                e.Handled = true;
                MarkEnd();
                break;
            case Key.P:
                e.Handled = true;
                _ = PlayOrPauseAsync();
                break;
        }
    }

    // ── the preview ─────────────────────────────────────────────────────

    /// <summary>True while this window's preview is playing or paused.</summary>
    private bool PreviewActive =>
        _previewPath != null && RecordingPlayer.IsPlaying && SamePath(RecordingPlayer.CurrentPath, _previewPath);

    private bool CurrentIsStillRecording => _browser.Current != null && SamePath(_browser.Current.Path, CurrentRecordingPath());

    private static string? CurrentRecordingPath() => (ComputerRecorder.Current as ComputerFlacRecorder)?.CurrentRecordingPath;

    /// <summary>Enter: play from where you are, or stop.</summary>
    private async Task TogglePlayAsync()
    {
        if (PreviewActive) StopPreview(speak: true);
        else await StartPreviewAsync();
    }

    /// <summary>P and the Play button: play, pause, or resume.</summary>
    private async Task PlayOrPauseAsync()
    {
        if (!PreviewActive)
        {
            await StartPreviewAsync();
            return;
        }
        if (RecordingPlayer.IsPaused)
        {
            double at = RecordingPlayer.PositionSeconds ?? _browser.Cursor;
            if (RecordingPlayer.Resume())
                Say(Lexicon.Get("audio.qsos.resumed", ("position", RecordedQsoWords.Position(at, _browser.Duration))));
        }
        else if (RecordingPlayer.Pause())
        {
            double at = RecordingPlayer.PositionSeconds ?? _browser.Cursor;
            _browser.MoveTo(at);
            Say(Lexicon.Get("audio.qsos.paused", ("position", RecordedQsoWords.Position(at, _browser.Duration))));
        }
        RefreshControls();
    }

    private async Task StartPreviewAsync()
    {
        // A second press while the first is still waiting for the radio to
        // confirm VOX off would start a second preview over the first.
        if (_starting) return;
        _starting = true;
        try { await StartPreviewCoreAsync(); }
        finally { _starting = false; }
    }

    private async Task StartPreviewCoreAsync()
    {
        var q = _browser.Current;
        if (q == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        if (CurrentIsStillRecording) { Refuse(Lexicon.Get("audio.qsos.still_recording")); return; }
        if (_rig?.Transmit == true) { Refuse(Lexicon.Get("audio.qsos.play_refused_transmitting")); return; }

        var pause = await LocalPlaybackVox.PauseAsync(_rig);
        if (pause == null) { Refuse(Lexicon.Get("audio.qsos.vox_unconfirmed")); return; }
        await RecorderControls.PlayStartClickBeforeLocalAudioAsync();

        double from = _browser.Cursor;
        if (_browser.CanMove && from >= _browser.Duration!.Value - 0.5) from = 0;
        _browser.MoveTo(from);
        string announcement = !_browser.CanMove
            ? Lexicon.Get("audio.qsos.playing_unknown")
            : from <= 0
                ? Lexicon.Get("audio.qsos.playing_from_start", ("length", RecordedQsoWords.Length(_browser.Duration)))
                : Lexicon.Get("audio.qsos.playing_from", ("position", RecordedQsoWords.Position(from, _browser.Duration)),
                    ("length", RecordedQsoWords.Length(_browser.Duration)));

        _previewPath = q.Path;
        bool started = RecordingPlayer.PlayFrom(q.Path, from, announcement);
        if (started) LocalPlaybackVox.HoldUntilPlaybackEnds(pause);
        else
        {
            pause.Dispose();
            _previewPath = null;
        }
        RefreshControls();
    }

    private void StopPreview(bool speak)
    {
        if (!PreviewActive) return;
        double at = RecordingPlayer.PositionSeconds ?? _browser.Cursor;
        RecordingPlayer.StopWithoutSpeaking();
        _browser.MoveTo(at);
        if (speak) Say(Lexicon.Get("audio.qsos.stopped", ("position", RecordedQsoWords.Position(at, _browser.Duration))));
        RefreshControls();
    }

    private void OnPlayerStateChanged() => Dispatcher.BeginInvoke(new Action(RefreshControls));

    private void OnPlaybackCompleted(string path) => Dispatcher.BeginInvoke(new Action(() =>
    {
        // Played to the end: the next Enter starts again from the start.
        if (SamePath(path, _browser.Current?.Path)) _browser.MoveTo(0);
        RefreshControls();
    }));

    // ── stepping and trimming ───────────────────────────────────────────

    private void StepPosition(int direction)
    {
        if (_browser.Current == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        if (PreviewActive && !RecordingPlayer.IsPaused)
        {
            // Moving through sounding audio waits for a later round (Noel,
            // 2026-10-21 10:57); pausing first keeps the position exact.
            Refuse(Lexicon.Get("audio.qsos.pause_first"));
            return;
        }
        if (PreviewActive) _browser.MoveTo(RecordingPlayer.PositionSeconds ?? _browser.Cursor);
        string sentence = _browser.Step(direction);
        if (PreviewActive) RecordingPlayer.Seek(_browser.Cursor);
        if (_browser.CanMove) Say(sentence, refresh: true);
        else Refuse(sentence);
    }

    private void MoveToEdge(bool toEnd)
    {
        if (_browser.Current == null) return;
        if (PreviewActive && !RecordingPlayer.IsPaused) { Refuse(Lexicon.Get("audio.qsos.pause_first")); return; }
        string sentence = _browser.Edge(toEnd);
        if (PreviewActive) RecordingPlayer.Seek(_browser.Cursor);
        Say(sentence, refresh: true);
    }

    /// <summary>Where a mark goes: what the operator heard at the press while playing, else the position.</summary>
    private double MarkPoint() => PreviewActive ? RecordingPlayer.PositionSeconds ?? _browser.Cursor : _browser.Cursor;

    private void MarkStart()
    {
        if (!CanTrim()) return;
        var (changed, sentence) = _browser.SetStart(MarkPoint());
        if (changed) Say(sentence, refresh: true); else Refuse(sentence);
    }

    private void MarkEnd()
    {
        if (!CanTrim()) return;
        var (changed, sentence) = _browser.SetEnd(MarkPoint());
        if (changed) Say(sentence, refresh: true); else Refuse(sentence);
    }

    private bool CanTrim()
    {
        if (_browser.Current == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return false; }
        if (CurrentIsStillRecording) { Refuse(Lexicon.Get("audio.qsos.still_recording")); return false; }
        if (!_browser.CanMove) { Refuse(Lexicon.Get("audio.qsos.no_position")); return false; }
        return true;
    }

    // ── delete, prune, Save As ──────────────────────────────────────────

    private bool InUse(string path) =>
        SamePath(path, CurrentRecordingPath()) || _exporting.Contains(path) ||
        (RecordingPlayer.IsPlaying && SamePath(RecordingPlayer.CurrentPath, path));

    private void DeleteSelected()
    {
        var q = _browser.Current;
        if (q == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        if (CurrentIsStillRecording || _exporting.Contains(q.Path)) { Refuse(Lexicon.Get("audio.qsos.still_recording")); return; }
        bool fromList = _list.IsKeyboardFocusWithin;
        DateTime now = DateTime.Now;
        var confirm = new ConfirmActionDialog(
            Lexicon.Get("audio.qsos.delete_title"),
            Lexicon.Get("audio.qsos.delete_message", ("when_lower", RecordedQsoWords.WhenInSentence(q.RecordedAt, now)),
                ("length", RecordedQsoWords.Length(q.Seconds))),
            question: Lexicon.Get("audio.qsos.delete_question"),
            yesLabel: Lexicon.Get("audio.qsos.delete_yes"),
            noLabel: Lexicon.Get("audio.qsos.delete_no"))
        { Owner = this };
        bool confirmed = confirm.ShowModalDialog() == true;
        SettleFocus(fromList);
        if (!confirmed) return;

        // Deleting stops that recording's playback first, whoever started it.
        if (RecordingPlayer.IsPlaying && SamePath(RecordingPlayer.CurrentPath, q.Path))
            RecordingPlayer.StopWithoutSpeaking();
        int index = Math.Max(0, _list.SelectedIndex);
        bool wasChosen = SamePath(ComputerRecorder.ChosenRecording, q.Path);
        var result = RecycleBin.Move(q.Path, new WindowInteropHelper(this).Handle);
        if (!result.Moved)
        {
            Tracing.TraceLine("RecordedQsosDialog: " + q.Path + " not recycled: " + result.Refusal + " " + result.Detail,
                TraceLevel.Warning);
            if (result.Refusal == RecycleRefusal.Missing) Populate(opening: false, fallbackIndex: index);
            SayAfterFocusSettles(RefusalSentence(result.Refusal), refusal: true);
            return;
        }

        Populate(opening: false, fallbackIndex: index);
        var next = _browser.Current;
        if (wasChosen)
        {
            // The chosen file has gone: the next row becomes the choice, or,
            // with nothing left, Play from computer goes back to the newest.
            ComputerRecorder.ChosenRecording = next?.Path;
            SetNewestQuietly(next == null);
        }
        SayAfterFocusSettles(next == null
            ? Lexicon.Get("audio.qsos.deleted_last")
            : Lexicon.Get("audio.qsos.deleted_next", ("when_lower", RecordedQsoWords.WhenInSentence(next.RecordedAt, now))));
    }

    internal static string RefusalSentence(RecycleRefusal refusal) => refusal switch
    {
        RecycleRefusal.Missing => Lexicon.Get("audio.qsos.refused_missing"),
        RecycleRefusal.NoRecycleBin => Lexicon.Get("audio.qsos.refused_no_bin"),
        RecycleRefusal.RecycleBinOff => Lexicon.Get("audio.qsos.refused_bin_off"),
        RecycleRefusal.TooLarge => Lexicon.Get("audio.qsos.refused_too_large"),
        _ => Lexicon.Get("audio.qsos.refused_failed"),
    };

    private void Prune()
    {
        if (_rows.Count == 0) return;
        bool fromList = _list.IsKeyboardFocusWithin;
        var dialog = new RecordedQsoPruneDialog(_rows, InUse) { Owner = this };
        bool confirmed = dialog.ShowModalDialog() == true && dialog.Chosen.Count > 0;
        SettleFocus(fromList);
        if (!confirmed) return;
        if (PreviewActive) RecordingPlayer.StopWithoutSpeaking();
        string? keep = _browser.Current?.Path;
        int moved = 0;
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr owner = new WindowInteropHelper(this).Handle;
        foreach (var q in dialog.Chosen)
        {
            if (InUse(q.Path)) continue;
            var result = RecycleBin.Move(q.Path, owner);
            if (result.Moved) { moved++; gone.Add(q.Path); }
            else Tracing.TraceLine("RecordedQsosDialog prune: " + q.Path + " not recycled: " + result.Refusal + " "
                + result.Detail, TraceLevel.Warning);
        }
        if (ComputerRecorder.ChosenRecording != null && gone.Contains(ComputerRecorder.ChosenRecording))
            ComputerRecorder.ChosenRecording = null;
        Populate(opening: false, keepPath: keep != null && !gone.Contains(keep) ? keep : null);
        if (ComputerRecorder.ChosenRecording == null) SetNewestQuietly(true);
        int asked = dialog.Chosen.Count;
        SayAfterFocusSettles(moved == asked
                ? (moved == 1 ? Lexicon.Get("audio.qsos.pruned_one") : Lexicon.Get("audio.qsos.pruned", ("count", moved)))
                : Lexicon.Get("audio.qsos.pruned_partly", ("moved", moved), ("count", asked)),
            refusal: moved != asked);
    }

    private async Task SaveAsAsync()
    {
        var q = _browser.Current;
        if (q == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        if (CurrentIsStillRecording) { Refuse(Lexicon.Get("audio.qsos.still_recording")); return; }
        bool fromList = _list.IsKeyboardFocusWithin;
        var (startFrame, endFrame) = _browser.Frames();
        var dialog = new RecordedQsoSaveAsDialog(q, _browser.IsWhole || !_browser.CanMove, startFrame, endFrame,
            _browser.Selection(), _browser.CanMove ? _browser.SelectionSeconds : q.Seconds) { Owner = this };
        bool saved = dialog.ShowModalDialog() == true && dialog.Request != null;
        SettleFocus(fromList);
        if (!saved) return;
        var request = dialog.Request!;
        SayAfterFocusSettles(Lexicon.Get("audio.qsos.saveas_saving"));
        _exporting.Add(q.Path);
        RefreshControls();
        string? failure = null;
        try
        {
            await Task.Run(() => RecordedQsoExport.Convert(q.Path, request.Destination, request.Format,
                request.Channels, request.Bitrate, request.StartFrame, request.EndFrame));
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("RecordedQsosDialog Save As: " + ex, TraceLevel.Error);
            failure = ex is NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException
                ? ex.Message : Lexicon.Get("audio.recorder.failed");
        }
        finally { _exporting.Remove(q.Path); }

        string file = Path.GetFileName(request.Destination);
        string folder = Path.GetDirectoryName(request.Destination) ?? "";
        if (failure != null)
            Refuse(Lexicon.Get("audio.qsos.saveas_failed", ("reason", EndSentence(failure))), queue: true);
        else
            Say(Lexicon.Get("audio.qsos.saveas_saved", ("file", file), ("folder", folder),
                ("length", RecordedQsoWords.Length(request.Seconds))), queue: true);

        if (IsLoaded && failure == null && RecordedQsoCatalog.IsInFolder(request.Destination, RecordedQsoCatalog.FolderPath))
            Populate(opening: false, keepPath: q.Path);
        else if (IsLoaded) RefreshControls();
    }

    private static string EndSentence(string text)
    {
        text = text.Trim();
        return text.Length == 0 || ".!?".Contains(text[^1]) ? text : text + ".";
    }

    /// <summary>
    /// After a confirmation or a Save As window closes, put the keyboard back
    /// where the operator was: on the chosen row when they came from the
    /// list (its row may have been rebuilt), otherwise wherever WPF restored
    /// it, unless that control has just been disabled. Deferred, because WPF
    /// restores focus when this window is activated again, after the child's
    /// ShowDialog has already returned.
    /// </summary>
    private void SettleFocus(bool fromList)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var focused = Keyboard.FocusedElement as UIElement;
            if (fromList || focused == null || !focused.IsEnabled || !IsKeyboardFocusWithin) FocusFirstControl();
        }));
    }

    // ── the controls' state ─────────────────────────────────────────────

    private void RefreshControls()
    {
        var focused = Keyboard.FocusedElement as UIElement;
        bool has = _browser.Current != null;
        bool busy = CurrentIsStillRecording || (has && _exporting.Contains(_browser.Current!.Path));
        bool active = PreviewActive;
        bool paused = active && RecordingPlayer.IsPaused;
        string playLabel = Lexicon.Get(!active ? "audio.qsos.play" : paused ? "audio.qsos.resume" : "audio.qsos.pause");
        _play.Content = playLabel;
        AutomationProperties.SetName(_play, playLabel);
        _play.IsEnabled = has && !CurrentIsStillRecording;
        _stop.IsEnabled = active;
        bool canMove = has && _browser.CanMove && !CurrentIsStillRecording;
        _position.IsEnabled = canMove;
        _position.Maximum = Math.Max(1, _browser.Duration ?? 1);
        _position.Value = Math.Clamp(_browser.Cursor, 0, _position.Maximum);
        _position.ValueText = has ? _browser.PositionValue() : "";
        _setStart.IsEnabled = canMove;
        _setEnd.IsEnabled = canMove;
        _selection.Text = _browser.Selection();
        _selection.IsEnabled = has;
        _useWhole.IsEnabled = canMove && !_browser.IsWhole;
        _saveAs.IsEnabled = has && !busy;
        _delete.IsEnabled = has && !busy;
        _prune.IsEnabled = _rows.Count > 0;
        _newest.IsEnabled = _rows.Count > 0;

        // A control that just went out of the tab order must not take the
        // keyboard with it: a blind operator on Stop when the preview ends
        // would otherwise be left on nothing.
        if (focused != null && !focused.IsEnabled && IsActive)
        {
            if (_play.IsEnabled) _play.Focus();
            else if (_rows.Count > 0) FocusRow();
            else _empty.Focus();
        }
    }

    // ── speech ──────────────────────────────────────────────────────────

    private void Say(string text, bool refresh = false, bool queue = false)
    {
        if (refresh) RefreshControls();
        if (string.IsNullOrEmpty(text)) return;
        _status.Text = text;
        ScreenReaderOutput.Speak(text, queue ? SpeechIntent.Queue : SpeechIntent.Interrupt, VerbosityLevel.Terse,
            subject: SpeechSubject.RecordedQsos);
    }

    private void Refuse(string text, bool queue = false)
    {
        _status.Text = text;
        EarconPlayer.LeaderInvalidTone();
        ScreenReaderOutput.Speak(text, queue ? SpeechIntent.Queue : SpeechIntent.Interrupt, VerbosityLevel.Critical,
            subject: SpeechSubject.RecordedQsos);
    }

    /// <summary>
    /// Speak after a confirmation window has closed and the reader has
    /// announced where focus landed. Speech flushes on a window change, so a
    /// sentence spoken at the close itself is lost.
    /// </summary>
    private void SayAfterFocusSettles(string text, bool refusal = false)
    {
        _status.Text = text;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = SayLaterAsync(text, refusal)));
    }

    private async Task SayLaterAsync(string text, bool refusal)
    {
        await Task.Delay(300);
        if (refusal) Refuse(text, queue: true);
        else Say(text, queue: true);
    }

    private Button AddButton(Panel panel, string label, Action onClick)
    {
        var button = new Button { Content = label, MinWidth = 96, Height = 28, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("RecordedQsosDialog: " + label + " failed: " + ex, TraceLevel.Error);
                Refuse(Lexicon.Get("audio.recorder.failed"));
            }
        };
        panel.Children.Add(button);
        return button;
    }

    /// <summary>
    /// The playback position as a slider whose VALUE a screen reader reads in
    /// words ("1 minute 5 seconds of 12 minutes 8 seconds"), never as a bare
    /// number. It raises no value-changed event: a step's answer is the one
    /// sentence the window speaks, so the reader does not say it twice.
    /// </summary>
    internal sealed class PositionSlider : Slider
    {
        internal string ValueText { get; set; } = "";

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer : FrameworkElementAutomationPeer, IValueProvider
        {
            internal Peer(PositionSlider owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
            protected override string GetClassNameCore() => "Slider";
            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
            public bool IsReadOnly => true;
            public string Value => ((PositionSlider)Owner).ValueText;
            public void SetValue(string value) => throw new InvalidOperationException("The position is moved with the arrow keys.");
        }
    }
}
