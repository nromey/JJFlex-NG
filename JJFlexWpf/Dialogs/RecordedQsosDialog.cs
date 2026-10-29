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
/// playback position; Set trim start; Set trim end; the selection line; Clear
/// selection; Save As; Delete; Prune; Close. A control that cannot act now is
/// disabled, which takes it out of the tab order. (The newest-recording
/// checkbox came out on the 2026-10-25 bench: the selected row is what Play
/// from computer plays.)
/// </para>
/// <para>
/// <b>Transport, as re-ruled at the 2026-10-25 bench</b> ("Enter says that it
/// stops, but it really pauses"): Enter starts or pauses playback, S stops and
/// returns the play head to the beginning of the file, and P is gone. Left
/// and Right move the play head by the selected step size while it plays too.
/// </para>
/// <para>
/// <b>Keys that work anywhere in the window, even while playing</b>: "[" marks
/// the trim start, "]" the end, Shift with either clears that mark, Backspace
/// clears both, S stops. None of them is Space or a Space chord: Ctrl+Space
/// stays push to talk and Shift+Space stays refused, through
/// <see cref="DialogTransmitKeys"/>, which sees every key before this window
/// does. The list's own letter search is switched off so "S" can never jump
/// to a row.
/// </para>
/// <para>
/// <b>Escape</b>: while a preview plays or is paused, Escape stops it, as S
/// does; the next Escape closes. While transmitting, the transmit door unkeys
/// first and this window never sees that Escape. Closing any other way stops
/// the preview too.
/// </para>
/// <para>
/// <b>The list follows the recorder.</b> A recording started or finished while
/// the window is open appears, and changes from "still recording" to its
/// length, without reopening (the 2026-10-25 bench found it frozen). The
/// window polls the recorder twice a second while it is the active window,
/// as Home polls it, because the recorder has no change event by design.
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
/// <para>
/// <b>The VOX send box (#835, fix round 2026-10-29).</b> With the box ticked
/// and VOX on, Enter is not a preview: it sends the selected recording through
/// Play from computer's send, keying the radio itself, and Enter again pauses
/// or carries on that send (<see cref="ChooseEnter"/>). Escape stops any send
/// in hand before it does anything else here, as in the main window.
/// </para>
/// </remarks>
public sealed class RecordedQsosDialog : JJFlexDialog
{
    private readonly FlexBase? _rig;
    private readonly RecordedQsoBrowser _browser = new();
    private readonly ListBox _list = new();
    private readonly TextBox _empty = new();
    private readonly TextBox _fileName = new();
    private readonly ComboBox _step = new();
    private readonly Button _play, _stop, _setStart, _setEnd, _clear, _saveAs, _delete, _prune, _close;
    private readonly PositionSlider _position = new();
    private readonly TextBox _selection = new();
    private readonly TextBlock _status = new();
    private readonly HashSet<string> _exporting = new(StringComparer.OrdinalIgnoreCase);

    // NVDA already announces this window's name on arrival; the base
    // dialog's own copy made it twice. Checked at the keyboard at the
    // 2026-10-25 bench, so this window opts out.
    protected internal override bool SpeaksOwnTitle => false;
    private IReadOnlyList<RecordedQso> _rows = Array.Empty<RecordedQso>();
    private string? _previewPath;
    private bool _quietSelection;
    private int _unreadable;
    private bool _starting;
    private bool _closed;
    private int _selectionVersion;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _seenRecording, _seenLast;

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
            _open.FollowRecorder(force: true);
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

        // The window's own description. Without one, NVDA builds a
        // description out of the window's plain text lines, which here are
        // the trim line and the status line, and reads them on arrival
        // (bench 2026-10-25). A description of our own replaces that
        // scraping; Noel chose the wording.
        AutomationProperties.SetHelpText(this, Lexicon.Get("audio.qsos.description"));
        Width = 640;
        Height = 560;
        ResizeMode = ResizeMode.CanResize;

        // Three rows: the list fills the window, the controls sit under it,
        // the status line last. Children are added in TAB ORDER, which is how
        // WPF walks them; no TabIndex is set, because TabIndex is compared only
        // among siblings and would not reorder across these panels.
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The selected recording's file name, read-only, one Shift+Tab before
        // the list (Noel, retest 2026-10-25), so the rows can leave names off and
        // the name is still there to read character by character or copy. The
        // window still opens on the list (FocusFirstControl).
        var fileRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var fileLabel = new Label { Content = Lexicon.Get("audio.qsos.file_name"), Target = _fileName, Padding = new Thickness(0, 4, 8, 0) };
        DockPanel.SetDock(fileLabel, Dock.Left);
        fileRow.Children.Add(fileLabel);
        AutomationProperties.SetName(_fileName, Lexicon.Get("audio.qsos.file_name"));
        JJFlexHelp.SetText(_fileName, Lexicon.Get("audio.qsos.file_name_help"));
        _fileName.IsReadOnly = true;
        _fileName.IsReadOnlyCaretVisible = true;
        fileRow.Children.Add(_fileName);
        Grid.SetRow(fileRow, 0);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_status, 3);

        var controls = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(controls, 2);

        // The step size, the next stop after the list (Noel 16:03).
        var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        stepRow.Children.Add(new Label { Content = Lexicon.Get("audio.qsos.step"), Target = _step, Padding = new Thickness(0, 4, 8, 0) });
        AutomationProperties.SetName(_step, Lexicon.Get("audio.qsos.step"));
        JJFlexHelp.SetText(_step, Lexicon.Get("audio.qsos.step_help"));
        foreach (double s in RecordedQsoBrowser.Steps)
        {
            string label = RecordedQsoWords.Step(s);
            var item = new ComboBoxItem { Content = label, Tag = s };
            AutomationProperties.SetName(item, label);
            _step.Items.Add(item);
        }
        _browser.StepSeconds = RememberedStep();
        _step.SelectedIndex = Array.IndexOf(RecordedQsoBrowser.Steps, _browser.StepSeconds);
        _step.MinWidth = 140;
        _step.SelectionChanged += (_, _) =>
        {
            if (_step.SelectedItem is not ComboBoxItem { Tag: double seconds }) return;
            _browser.StepSeconds = seconds;
            RememberStep(seconds);
        };
        stepRow.Children.Add(_step);
        controls.Children.Add(stepRow);

        var transport = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        // DRAFT PROSE (#835): awaiting Noel. play_help gained the VOX send box sentence (fix round 2026-10-29).
        _play = AddButton(transport, Lexicon.Get("audio.qsos.play"), () => _ = PlayOrPauseAsync(), "audio.qsos.play_help");
        _stop = AddButton(transport, Lexicon.Get("audio.qsos.stop"), () => StopToStart(speak: true), "audio.qsos.stop_help");
        controls.Children.Add(transport);

        AutomationProperties.SetName(_position, Lexicon.Get("audio.qsos.position"));
        JJFlexHelp.SetText(_position, Lexicon.Get("audio.qsos.position_help"));
        _position.Minimum = 0;
        _position.Maximum = 1;
        _position.IsTabStop = true;
        _position.Margin = new Thickness(0, 0, 0, 6);
        _position.PreviewKeyDown += OnPositionKeyDown;
        controls.Children.Add(_position);

        var trim = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _setStart = AddButton(trim, Lexicon.Get("audio.qsos.set_start"), () => MarkStart(), "audio.qsos.set_start_help");
        _setEnd = AddButton(trim, Lexicon.Get("audio.qsos.set_end"), () => MarkEnd(), "audio.qsos.set_end_help");
        controls.Children.Add(trim);

        AutomationProperties.SetName(_selection, Lexicon.Get("audio.qsos.selection_label"));
        JJFlexHelp.SetText(_selection, Lexicon.Get("audio.qsos.selection_help"));
        _selection.IsReadOnly = true;
        _selection.IsReadOnlyCaretVisible = true;
        _selection.TextWrapping = TextWrapping.Wrap;
        _selection.Margin = new Thickness(0, 0, 0, 6);
        controls.Children.Add(_selection);

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _clear = AddButton(actions, Lexicon.Get("audio.qsos.clear_selection"), () => ClearSelection(), "audio.qsos.clear_selection_help");
        _saveAs = AddButton(actions, Lexicon.Get("audio.qsos.save_as"), () => _ = SaveAsAsync(), "audio.qsos.save_as_help");
        _delete = AddButton(actions, Lexicon.Get("audio.qsos.delete"), () => DeleteSelected(), "audio.qsos.delete_help");
        _prune = AddButton(actions, Lexicon.Get("audio.qsos.prune"), () => Prune(), "audio.qsos.prune_help");
        controls.Children.Add(actions);

        var closeRow = new WrapPanel();
        _close = AddButton(closeRow, Lexicon.Get("audio.qsos.close"), () => CloseWithResult(true), "audio.qsos.close_help");
        controls.Children.Add(closeRow);

        var listArea = new Grid();
        Grid.SetRow(listArea, 1);
        AutomationProperties.SetName(_list, Lexicon.Get("audio.qsos.list"));
        // DRAFT PROSE (#835): awaiting Noel. list_help gained the VOX send box sentence (fix round 2026-10-29).
        JJFlexHelp.SetText(_list, Lexicon.Get("audio.qsos.list_help"));
        _list.SelectionMode = SelectionMode.Single;
        _list.IsTextSearchEnabled = false; // "S" must never jump to a row (Noel 16:05, then P; S since 2026-10-25).
        _list.SelectionChanged += OnListSelectionChanged;
        _list.PreviewKeyDown += OnListKeyDown;
        _list.MouseDoubleClick += (_, _) => _ = PlayOrPauseAsync();
        listArea.Children.Add(_list);

        AutomationProperties.SetName(_empty, Lexicon.Get("audio.qsos.list"));
        _empty.IsReadOnly = true;
        _empty.IsReadOnlyCaretVisible = true;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Text = Lexicon.Get("audio.qsos.empty");
        _empty.Visibility = Visibility.Collapsed;
        listArea.Children.Add(_empty);

        root.Children.Add(fileRow);
        root.Children.Add(listArea);
        root.Children.Add(controls);
        root.Children.Add(_status);
        Content = root;

        RecordingPlayer.StateChanged += OnPlayerStateChanged;
        RecordingPlayer.PlaybackCompleted += OnPlaybackCompleted;
        Closed += (_, _) =>
        {
            _closed = true;
            _watch.Stop();
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
        _watch.Tick += (_, _) =>
        {
            FollowRecorder(force: false);
            if (!_closed && PreviewActive && !RecordingPlayer.IsPaused) ShowPlayHead();
        };
        Activated += (_, _) => FollowRecorder(force: false);
        Loaded += (_, _) =>
        {
            NoteRecorder();
            _watch.Start();
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
                // The row shown selected is the file Play from computer sends
                // (#835 fix round): open on what Play plays now, and make the
                // opening row Play's choice only when they would disagree.
                var opened = RecordedQsoBrowser.OpeningAgreeingWithPlay(_rows, ComputerRecorder.ChosenForPlayback());
                index = opened.Index;
                if (opened.NewChoice != null) ComputerRecorder.ChosenRecording = opened.NewChoice;
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
        _selectionVersion++;
        var q = (_list.SelectedItem as ListBoxItem)?.Tag as RecordedQso;
        if (PreviewActive && !SamePath(q?.Path, _previewPath))
            RecordingPlayer.StopWithoutSpeaking(); // a new row is a new recording; its own preview starts on Enter
        _browser.Select(q);
        // Selecting a recording makes it Play from computer's choice, silently:
        // "just enter plays the currently selected" (Noel, 2026-10-25), and the
        // sentence that used to announce it was ruled out the same morning.
        if (q != null) ComputerRecorder.ChosenRecording = q.Path;
        RefreshControls();
    }

    /// <summary>
    /// Follow the recorder: when a recording starts or finishes, rebuild the
    /// list on the same row. Only while this is the active window, so the
    /// rows never change under a confirmation or Save As it opened; becoming
    /// active again catches up.
    /// </summary>
    private void FollowRecorder(bool force)
    {
        if (_closed || (!force && !IsActive)) return;
        string? recording = CurrentRecordingPath();
        string? last = ComputerRecorder.Current.LastRecordingPath;
        if (!force && SamePath2(recording, _seenRecording) && SamePath2(last, _seenLast)) return;
        NoteRecorder();
        Populate(opening: false, keepPath: _browser.Current?.Path);
    }

    private void NoteRecorder()
    {
        _seenRecording = CurrentRecordingPath();
        _seenLast = ComputerRecorder.Current.LastRecordingPath;
    }

    private static bool SamePath2(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The step size the operator last chose, from the audio settings; 5 seconds the first time.</summary>
    private static double RememberedStep()
    {
        try { return RecordedQsoBrowser.ValidStep(AudioOutputConfig.Load(RadioConfig.AppDataRoot).RecordedQsosStepSeconds); }
        catch (Exception ex)
        {
            Tracing.TraceLine("RecordedQsosDialog: step size not read: " + ex.Message, TraceLevel.Warning);
            return RecordedQsoBrowser.DefaultStep;
        }
    }

    /// <summary>Remember the step size across restarts (Noel, 2026-10-25), the way the earcon keys save their switch.</summary>
    private static void RememberStep(double seconds)
    {
        try
        {
            var config = AudioOutputConfig.Load(RadioConfig.AppDataRoot);
            if (config.RecordedQsosStepSeconds == seconds) return;
            config.RecordedQsosStepSeconds = seconds;
            config.Save(RadioConfig.AppDataRoot);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("RecordedQsosDialog: step size not saved: " + ex.Message, TraceLevel.Warning);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = PlayOrPauseAsync();
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
        // A send in hand stops first, as in the main window: RF before the
        // preview and before closing (#835, fix round 2026-10-29). Enter can
        // start one here now, under the VOX send box.
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && StopSendOnEscape())
        {
            e.Handled = true;
            return;
        }
        // Escape stops only what you can HEAR. Paused, it closes the window
        // and the preview ends silently with it (Noel, retest 2026-10-25: a
        // paused preview answered Escape with "Stopped", which was weird for
        // something not playing; "I'd just close the window").
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && PreviewActive && !RecordingPlayer.IsPaused)
        {
            e.Handled = true;
            StopToStart(speak: true);
            return;
        }
        // Ctrl+Left and Ctrl+Right move the play head from anywhere in the
        // window, as [ and ] mark from anywhere (Noel, retest 2026-10-25: plain
        // arrows on the step size or a button did their own job, not a step).
        if ((key == Key.Left || key == Key.Right) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            StepPosition(key == Key.Right ? 1 : -1);
            return;
        }
        if (key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            HelpLauncher.ShowHelp("RecordedQsos");
            return;
        }
        var modifiers = Keyboard.Modifiers;
        if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift) return;
        if (DialogTransmitKeys.IsEditableText(e.OriginalSource as DependencyObject)) return;
        bool shift = modifiers == ModifierKeys.Shift;
        switch (key)
        {
            case Key.OemOpenBrackets:
                e.Handled = true;
                if (shift) ClearStartMark(); else MarkStart();
                break;
            case Key.OemCloseBrackets:
                e.Handled = true;
                if (shift) ClearEndMark(); else MarkEnd();
                break;
            case Key.Back when !shift:
                e.Handled = true;
                ClearSelection();
                break;
            case Key.S when !shift:
                e.Handled = true;
                StopToStart(speak: true);
                break;
        }
    }

    // ── the preview ─────────────────────────────────────────────────────

    /// <summary>True while this window's preview is playing or paused.</summary>
    private bool PreviewActive =>
        _previewPath != null && RecordingPlayer.IsPlaying && SamePath(RecordingPlayer.CurrentPath, _previewPath);

    private bool CurrentIsStillRecording => _browser.Current != null && SamePath(_browser.Current.Path, CurrentRecordingPath());

    private static string? CurrentRecordingPath() => (ComputerRecorder.Current as ComputerFlacRecorder)?.CurrentRecordingPath;

    // ── the VOX send box (#835, fix round 2026-10-29) ───────────────────

    /// <summary>
    /// The send doors Enter and Escape use in this window: the very ones Play
    /// from computer uses (<see cref="RecorderControls.ToggleComputerPlayAsync"/>),
    /// so the box rule has one home. A seam only so a windowless test can
    /// hold the rule without a radio; production never replaces it.
    /// </summary>
    internal interface ISendDoors
    {
        /// <summary>Which way a send pressed now goes (<see cref="VoiceMessageSender.RouteFor"/>).</summary>
        SendRoute RouteFor(FlexBase rig);
        /// <summary>A computer recording's send is going out, getting ready, or paused.</summary>
        bool ComputerRecordingInHand { get; }
        /// <summary>Any send is going out, getting ready, or paused.</summary>
        bool InFlight { get; }
        void SendComputerRecording(FlexBase rig, string path, SendRoute route);
        /// <summary>Pause, carry on, or stop the send in hand, as Play from computer does.</summary>
        bool AnswerPlayFromComputer();
        void Stop();
    }

    private sealed class LiveSendDoors : ISendDoors
    {
        public SendRoute RouteFor(FlexBase rig) => VoiceMessageSender.RouteFor(rig);
        public bool ComputerRecordingInHand => VoiceMessageSender.ComputerRecordingInHand;
        public bool InFlight => VoiceMessageSender.InFlight;
        public void SendComputerRecording(FlexBase rig, string path, SendRoute route) =>
            VoiceMessageSender.SendComputerRecording(rig, path, route);
        public bool AnswerPlayFromComputer() => VoiceMessageSender.AnswerPlayFromComputer();
        public void Stop() => VoiceMessageSender.Stop();
    }

    internal static ISendDoors SendDoors { get; set; } = new LiveSendDoors();

    /// <summary>What Enter (and the Play button) does, decided once per press.</summary>
    internal enum EnterAct
    {
        /// <summary>This window's preview is playing or paused: pause it, or carry it on.</summary>
        PreviewTransport,
        /// <summary>A computer recording's send is in hand: pause it, or carry it on, as Play from computer does.</summary>
        AnswerSend,
        /// <summary>The box is ticked and VOX is on: send the selected recording to the air.</summary>
        Send,
        /// <summary>Everything else: preview on this computer's speakers, as before the box.</summary>
        Preview,
    }

    /// <summary>
    /// Enter follows the box rule like Play from computer (Noel, 2026-10-29
    /// 01:40-01:42: <i>"I pressed enter from within the computer play dialog,
    /// That should send as well"</i>). With the box ticked and VOX on, Play IS
    /// the send (2026-10-27 15:22), so Enter here is too; otherwise it previews
    /// on the speakers as it always has. A send Enter (or Play from computer)
    /// started is paused and carried on by Enter, the one meaning the key has
    /// in this window. <paramref name="route"/> is null with no radio.
    /// </summary>
    internal static EnterAct ChooseEnter(bool previewActive, bool computerSendInHand, SendRoute? route)
    {
        if (previewActive) return EnterAct.PreviewTransport;
        if (computerSendInHand) return EnterAct.AnswerSend;
        if (route == SendRoute.Box) return EnterAct.Send;
        return EnterAct.Preview;
    }

    /// <summary>
    /// Send the selected row through Play from computer's send, under the box.
    /// The send says what it is doing itself, under the message key's subject,
    /// so this window says nothing unless it refuses. The whole recording goes,
    /// from its start, as Play from computer sends it: the play head and the
    /// trim marks are this window's preview and Save As, not the send's.
    /// </summary>
    private void SendSelected(ISendDoors doors)
    {
        var q = _browser.Current;
        if (q == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        if (CurrentIsStillRecording) { Refuse(Lexicon.Get("audio.qsos.still_recording")); return; }
        Tracing.TraceLine("RecordedQsosDialog: Enter sends " + Path.GetFileName(q.Path)
            + " under the VOX send box", TraceLevel.Info);
        doors.SendComputerRecording(_rig!, q.Path, SendRoute.Box);
    }

    /// <summary>
    /// Escape while a send is in hand stops it, as Escape does in the main
    /// window (RF first), and is consumed so the window stays open; the next
    /// Escape closes. The box send keys the radio itself, so the PTT
    /// controller is idle and the dialog transmit door leaves this Escape to
    /// the window. True when there was a send to stop.
    /// </summary>
    internal bool StopSendOnEscape()
    {
        var doors = SendDoors;
        if (!doors.InFlight) return false;
        Tracing.TraceLine("RecordedQsosDialog: Escape stops the send in hand", TraceLevel.Info);
        doors.Stop();
        return true;
    }

    /// <summary>Enter and the Play button: play from the play head, pause, or resume.</summary>
    private async Task PlayOrPauseAsync()
    {
        // The box rule first (#835, fix round 2026-10-29): the same question
        // Play from computer asks, read live once per press.
        var doors = SendDoors;
        bool previewActive = PreviewActive;
        SendRoute? route = !previewActive && _rig != null ? doors.RouteFor(_rig) : null;
        switch (ChooseEnter(previewActive, doors.ComputerRecordingInHand, route))
        {
            case EnterAct.AnswerSend:
                doors.AnswerPlayFromComputer();
                return;
            case EnterAct.Send:
                SendSelected(doors);
                return;
            case EnterAct.Preview:
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
        int selectionVersion = _selectionVersion;
        if (q == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        if (CurrentIsStillRecording) { Refuse(Lexicon.Get("audio.qsos.still_recording")); return; }
        if (_rig?.Transmit == true) { Refuse(Lexicon.Get("audio.qsos.play_refused_transmitting")); return; }

        var pause = await LocalPlaybackVox.PauseAsync(_rig);
        if (pause == null)
        {
            if (!_closed && selectionVersion == _selectionVersion)
                Refuse(Lexicon.Get("audio.qsos.vox_unconfirmed"));
            return;
        }
        try
        {
            if (_closed || selectionVersion != _selectionVersion || !SamePath(q.Path, _browser.Current?.Path)) return;
            await RecorderControls.PlayStartClickBeforeLocalAudioAsync();
            if (_closed || selectionVersion != _selectionVersion || !SamePath(q.Path, _browser.Current?.Path)) return;

            double from = _browser.Cursor;
            if (_browser.CanMove && from >= _browser.Duration!.Value - 0.05) from = 0;
            _browser.MoveTo(from);
            string announcement = !_browser.CanMove
                ? Lexicon.Get("audio.qsos.playing_unknown")
                : from <= 0
                    ? Lexicon.Get("audio.qsos.playing_from_start", ("length", RecordedQsoWords.Length(_browser.Duration)))
                    : Lexicon.Get("audio.qsos.playing_from", ("position", RecordedQsoWords.Position(from, _browser.Duration)),
                        ("length", RecordedQsoWords.Length(_browser.Duration)));

            _previewPath = q.Path;
            bool started = RecordingPlayer.PlayFrom(q.Path, from, announcement);
            if (started)
            {
                LocalPlaybackVox.HoldUntilPlaybackEnds(pause);
                pause = null;
            }
            else _previewPath = null;
            RefreshControls();
        }
        finally { pause?.Dispose(); }
    }

    /// <summary>
    /// S, the Stop button and Escape while playing: stop, and return the play
    /// head to the beginning of the file (Noel, 2026-10-25). Pausing is
    /// Enter's job.
    /// </summary>
    private void StopToStart(bool speak)
    {
        if (_browser.Current == null) { if (speak) Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        bool was = PreviewActive;
        if (was) RecordingPlayer.StopWithoutSpeaking();
        _browser.MoveTo(0);
        if (speak) Say(Lexicon.Get(was ? "audio.qsos.stopped_to_start" : "audio.qsos.play_head_start"));
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
        // While it plays too (Noel, 2026-10-25: "I'd make it so it can jump
        // ahead and back"): the step starts from what is sounding now.
        if (PreviewActive) _browser.MoveTo(RecordingPlayer.PositionSeconds ?? _browser.Cursor);
        string sentence = _browser.Step(direction);
        if (!SeekPreview()) return;
        if (_browser.CanMove) Say(sentence, refresh: true);
        else Refuse(sentence);
    }

    private void MoveToEdge(bool toEnd)
    {
        if (_browser.Current == null) return;
        string sentence = _browser.Edge(toEnd);
        if (!SeekPreview()) return;
        Say(sentence, refresh: true);
    }

    /// <summary>
    /// Take a playing or paused preview to the new play head. True when there
    /// is no preview or the player moved; on a refusal the play head goes back
    /// to what is sounding and the window says so, rather than announcing a
    /// position the audio never reached. Until 2026-10-25 the result was
    /// ignored, and that is how the refusal to seek while sounding went
    /// unheard: the window spoke the new position either way.
    /// </summary>
    private bool SeekPreview()
    {
        if (!PreviewActive || RecordingPlayer.Seek(_browser.Cursor)) return true;
        _browser.MoveTo(RecordingPlayer.PositionSeconds ?? _browser.Cursor);
        Refuse(Lexicon.Get("audio.qsos.move_failed"));
        return false;
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
        if (!_browser.Current.IsFlac) { Refuse(Lexicon.Get("audio.qsos.flac_only")); return false; }
        if (!_browser.CanMove) { Refuse(Lexicon.Get("audio.qsos.no_position")); return false; }
        return true;
    }

    private void ClearStartMark()
    {
        if (!CanTrim()) return;
        var (changed, sentence) = _browser.ClearStart();
        if (changed) Say(sentence, refresh: true); else Refuse(sentence);
    }

    private void ClearEndMark()
    {
        if (!CanTrim()) return;
        var (changed, sentence) = _browser.ClearEnd();
        if (changed) Say(sentence, refresh: true); else Refuse(sentence);
    }

    /// <summary>Backspace and Clear selection: both marks go, and the whole recording is selected.</summary>
    private void ClearSelection()
    {
        if (!CanTrim()) return;
        Say(_browser.ClearSelection(), refresh: true);
    }

    // ── delete, prune, Save As ──────────────────────────────────────────

    private bool InUse(string path) =>
        SamePath(path, CurrentRecordingPath()) || _exporting.Contains(path) ||
        (RecordingPlayer.IsPlaying && SamePath(RecordingPlayer.CurrentPath, path));

    private void DeleteSelected()
    {
        var q = _browser.Current;
        if (q == null) { Refuse(Lexicon.Get("audio.qsos.nothing_selected")); return; }
        if (CurrentIsStillRecording) { Refuse(Lexicon.Get("audio.qsos.still_recording")); return; }
        if (_exporting.Contains(q.Path)) { Refuse(Lexicon.Get("audio.qsos.being_saved")); return; }
        bool fromList = _list.IsKeyboardFocusWithin;
        DateTime now = DateTime.Now;
        var confirm = new ConfirmActionDialog(
            Lexicon.Get("audio.qsos.delete_title"),
            Lexicon.Get("audio.qsos.delete_message", ("when_lower", RecordedQsoWords.WhenInSentence(q.RecordedAt, now)),
                ("length", RecordedQsoWords.Length(q.Seconds))),
            question: Lexicon.Get("audio.qsos.delete_question"),
            yesLabel: Lexicon.Get("audio.qsos.delete_yes"),
            noLabel: Lexicon.Get("audio.qsos.delete_no"),
            enterConfirms: true)
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
            // The chosen file has gone: the next row, now selected, becomes the
            // choice, or, with nothing left, Play from computer goes back to the newest.
            ComputerRecorder.ChosenRecording = next?.Path;
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
        RecycleRefusal.OutsideArchive => Lexicon.Get("audio.qsos.refused_outside"),
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
        if (!q.IsFlac) { Refuse(Lexicon.Get("audio.qsos.flac_only")); return; }
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
            Say(SavedSentence(file, folder, q.Path, request.Seconds), queue: true);

        if (IsLoaded && failure == null && RecordedQsoCatalog.IsInFolder(request.Destination, RecordedQsoCatalog.FolderPath))
            Populate(opening: false, keepPath: q.Path);
        else if (IsLoaded) RefreshControls();
    }

    /// <summary>
    /// "Saved X in the October 2026 folder" when the file went into the
    /// recording's own month folder; the full path only when another folder
    /// was chosen (Noel, 2026-10-25).
    /// </summary>
    internal static string SavedSentence(string file, string folder, string source, double? seconds)
    {
        string length = seconds is double s ? RecordedQsoWords.Precise(s) : RecordedQsoWords.Length(null);
        string? month = MonthFolder(folder, source);
        return month != null
            ? Lexicon.Get("audio.qsos.saveas_saved_month", ("file", file), ("month", month), ("length", length))
            : Lexicon.Get("audio.qsos.saveas_saved", ("file", file), ("folder", folder), ("length", length));
    }

    /// <summary>"October 2026" when <paramref name="folder"/> is the source recording's own year\month folder, else null.</summary>
    internal static string? MonthFolder(string folder, string source)
    {
        string? own = Path.GetDirectoryName(source);
        if (own == null || !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(own).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)) return null;
        string monthPart = Path.GetFileName(own);
        string yearPart = Path.GetFileName(Path.GetDirectoryName(own) ?? "");
        if (!int.TryParse(yearPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(monthPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int month) ||
            month < 1 || month > 12 || year < 2000) return null;
        return new DateTime(year, month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
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

    /// <summary>
    /// Put the position control where the play head is: what is sounding
    /// while a preview plays, otherwise the browser's position. The window's
    /// poll calls this too, so the slider follows playback.
    /// </summary>
    /// <remarks>
    /// QUIETLY (Noel, bench 2026-10-25: "follows quietly"). The control's peer
    /// is a plain element peer with a read-only Value pattern, not a range-value
    /// peer, so moving Value and ValueText raises no automation event and NVDA
    /// says nothing; the operator hears the exact position on asking (NVDA+Tab),
    /// or when pausing or stepping. Never raise a value-changed event from here:
    /// twice a second over the recording is the chatter that ruling refused.
    /// </remarks>
    private void ShowPlayHead()
    {
        if (_browser.Current == null) { _position.Value = 0; _position.ValueText = ""; return; }
        double at = PreviewActive ? RecordingPlayer.PositionSeconds ?? _browser.Cursor : _browser.Cursor;
        _position.Value = Math.Clamp(at, 0, _position.Maximum);
        _position.ValueText = _browser.PositionValue(at);
    }

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
        ShowPlayHead();
        _fileName.Text = has ? System.IO.Path.GetFileName(_browser.Current!.Path) : "";
        bool flac = has && _browser.Current!.IsFlac;
        _setStart.IsEnabled = canMove && flac;
        _setEnd.IsEnabled = canMove && flac;
        _selection.Text = CurrentIsStillRecording ? Lexicon.Get("audio.qsos.recording_selection") : _browser.Selection();
        _selection.IsEnabled = has;
        _clear.IsEnabled = canMove && flac && !_browser.IsWhole;
        _saveAs.IsEnabled = has && !busy && flac;
        _delete.IsEnabled = has && !busy;
        _prune.IsEnabled = _rows.Count > 0;

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

    // DRAFT PROSE (#787): awaiting Noel. Every sentence this window speaks is an audio.qsos.* lexicon key.
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

    private Button AddButton(Panel panel, string label, Action onClick, string helpKey)
    {
        var button = new Button { Content = label, MinWidth = 96, Height = 28, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(button, label);
        // Every button explains itself on Ctrl+F1 (Noel, 2026-10-25: "there's
        // no help for save as or delete").
        JJFlexHelp.SetText(button, Lexicon.Get(helpKey));
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
