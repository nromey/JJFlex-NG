#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using JJTrace;
using Radios;
using Radios.Alarms;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Operator alarms (#566): this radio's alarm definitions with their state in
/// words, one detail field, and the actions in the design's tab order.
/// </summary>
/// <remarks>
/// <para>
/// A thin shell over <see cref="OperatorAlarmsModel"/>, which decides
/// everything and is tested without a window. Standard controls announce
/// themselves; nothing here speaks in a focus or selection handler. The
/// only speech is the receipt of an action the operator pressed, and the two
/// explicit reads, all at Critical because they were asked for (#322).
/// </para>
/// <para>
/// Reached through Command Finder (alarm, temperature, PA, voltage,
/// threshold, warning) and Tools menu. No JJ key letter: #518 is Noel's to
/// allocate. Escape closes and returns focus to the caller; it does not
/// silence an alarm, disable monitoring or unkey the radio.
/// </para>
/// <para>
/// Rows are rewritten only when the service says something changed — a
/// state transition, a save — and the selection index is kept, so a focused
/// row's accessible name is stable while readings move. The detail field
/// alone is refreshed once a second.
/// </para>
/// </remarks>
public sealed class OperatorAlarmsDialog : JJFlexDialog
{
    private readonly OperatorAlarmsModel? _model;
    private readonly ListBox _list = new();
    private readonly TextBox _details = new();
    private readonly TextBlock _status = new();
    private readonly Button _add, _edit, _enable, _readStatus, _readActive, _acknowledge, _snooze, _resume, _baseline, _history, _recorded;
    private IReadOnlyList<AlarmSnapshot> _rows = Array.Empty<AlarmSnapshot>();
    private readonly DispatcherTimer _tick;

    /// <summary>The window over the running subsystem. With no service attached it explains and offers nothing.</summary>
    public OperatorAlarmsDialog() : this(OperatorAlarmHost.Service, OperatorAlarmHost.Presets) { }

    public OperatorAlarmsDialog(AlarmService? service, OperatorPresetStore? presets)
    {
        _model = service == null ? null : new OperatorAlarmsModel(service, presets);

        Title = Lexicon.Get("alarms.dialog.title");
        Width = 720;
        Height = 560;
        ResizeMode = ResizeMode.CanResize;

        var root = new DockPanel { Margin = new Thickness(12) };

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        // Buttons in the design's order: Add, Edit, Enable/Disable, Read status,
        // Acknowledge, Snooze, Resume notifications, History, then the extras,
        // then Close. Unavailable ones are disabled, which takes them out of
        // the tab order; the detail field carries the explanation.
        //
        // Delete is NOT here. Ruled by Noel 2026-09-22 (#566): "move it in."
        // It lives inside the Edit form, after Save and Save as preset, so
        // this dialog is the list, Add, Edit and the status actions.
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        _add = Add(buttons, "alarms.dialog.add", AddAlarm);
        _edit = Add(buttons, "alarms.dialog.edit", EditAlarm);
        _enable = Add(buttons, "alarms.dialog.enable", ToggleEnabled);
        _readStatus = Add(buttons, "alarms.dialog.read_status", ReadStatus);
        _readActive = Add(buttons, "alarms.dialog.read_active", ReadActive);
        _acknowledge = Add(buttons, "alarms.dialog.acknowledge", Acknowledge);
        _snooze = Add(buttons, "alarms.dialog.snooze", OpenSnoozeMenu);
        _resume = Add(buttons, "alarms.dialog.resume", Resume);
        _baseline = Add(buttons, "alarms.dialog.capture_baseline", CaptureBaseline);
        _history = Add(buttons, "alarms.dialog.history", ShowHistory);
        _recorded = Add(buttons, "alarms.dialog.record_meters", ShowRecordedMeters);
        var close = Add(buttons, "alarms.dialog.close", () => CloseWithResult(true));
        close.IsCancel = true;
        root.Children.Add(buttons);

        AutomationProperties.SetName(_details, Lexicon.Get("alarms.dialog.details"));
        _details.IsReadOnly = true;
        _details.TextWrapping = TextWrapping.Wrap;
        _details.AcceptsReturn = true;
        _details.MinHeight = 90;
        _details.MaxHeight = 140;
        _details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _details.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_details, Dock.Bottom);
        root.Children.Add(_details);

        AutomationProperties.SetName(_list, Lexicon.Get("alarms.dialog.list"));
        JJFlexHelp.SetText(_list,
            "One line per alarm: its name, the meter it watches, the line, and its state in words. "
            + "The field below the list gives the latest reading, how old it is, the line, the data quality and the last event. "
            + "Enter opens the alarm for editing.");
        _list.SelectionChanged += (_, _) => RefreshDetails();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; EditAlarm(); }
        };
        _list.MouseDoubleClick += (_, _) => EditAlarm();
        root.Children.Add(_list);

        Content = root;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => RefreshDetails();
        Loaded += (_, _) => { RefreshRows(selectFirst: true); _tick.Start(); };
        Closed += (_, _) =>
        {
            _tick.Stop();
            if (_model != null)
            {
                _model.Service.Changed -= OnServiceChanged;
                _model.Service.EventDispatched -= OnEventDispatched;
            }
        };
        if (_model != null)
        {
            _model.Service.Changed += OnServiceChanged;
            _model.Service.EventDispatched += OnEventDispatched;
        }
    }

    /// <summary>Open over the running subsystem, modally, owned by the caller.</summary>
    public static void Show(Window? owner)
    {
        var dialog = new OperatorAlarmsDialog();
        if (owner != null) dialog.Owner = owner;
        dialog.ShowModalDialog();
    }

    private Button Add(Panel panel, string key, Action onClick)
    {
        string label = Lexicon.Get(key);
        var button = new Button { Content = label, MinWidth = 96, Height = 28, Margin = new Thickness(0, 0, 8, 6) };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("OperatorAlarmsDialog: " + label + " failed — " + ex.Message, TraceLevel.Error);
                Say(Lexicon.Get("alarms.dialog.save_failed"));
            }
        };
        panel.Children.Add(button);
        return button;
    }

    // ── the list and the detail field ──

    private void OnServiceChanged() => Dispatcher.BeginInvoke(() => RefreshRows(selectFirst: false));

    private void OnEventDispatched(AlarmEvent e)
    {
        // State transitions change a row's words; a tick does not.
        if (e.Kind is AlarmEventKind.Fired or AlarmEventKind.Cleared or AlarmEventKind.DataStale
            or AlarmEventKind.DataResumed or AlarmEventKind.DataMissing or AlarmEventKind.DataAmbiguous
            or AlarmEventKind.Acknowledged or AlarmEventKind.Snoozed or AlarmEventKind.SnoozeExpired
            or AlarmEventKind.Resumed or AlarmEventKind.FirstSample or AlarmEventKind.Enabled
            or AlarmEventKind.Disabled or AlarmEventKind.ConfigurationChanged or AlarmEventKind.ScopeEntered
            or AlarmEventKind.ScopeLeft or AlarmEventKind.Pending)
        {
            Dispatcher.BeginInvoke(() => RefreshRows(selectFirst: false));
        }
    }

    private void RefreshRows(bool selectFirst)
    {
        int previous = _list.SelectedIndex;
        string? previousId = Selected()?.Definition.Id;

        if (_model == null)
        {
            _rows = Array.Empty<AlarmSnapshot>();
            _list.Items.Clear();
            _status.Text = Lexicon.Get("alarms.dialog.no_radio");
            _details.Text = "";
            UpdateButtons(null);
            return;
        }

        _rows = _model.Rows();
        _list.Items.Clear();
        foreach (AlarmSnapshot s in _rows) _list.Items.Add(_model.RowText(s));
        _status.Text = _model.StatusText();

        if (_rows.Count > 0)
        {
            int index = -1;
            if (previousId != null)
                for (int i = 0; i < _rows.Count; i++) if (_rows[i].Definition.Id == previousId) { index = i; break; }
            if (index < 0) index = selectFirst || previous < 0 ? 0 : Math.Min(previous, _rows.Count - 1);
            _list.SelectedIndex = index;
        }
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        AlarmSnapshot? s = _model == null ? null : Selected(fresh: true);
        _details.Text = s == null ? "" : _model!.DetailText(s);
        UpdateButtons(s);
    }

    private AlarmSnapshot? Selected(bool fresh = false)
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _rows.Count) return null;
        if (!fresh || _model == null) return _rows[i];
        return _model.Service.SnapshotOf(_rows[i].Definition.Id) ?? _rows[i];
    }

    private void UpdateButtons(AlarmSnapshot? s)
    {
        bool have = s != null && _model != null;
        bool usable = _model != null && _model.Service.IsConnected && _model.Service.StoreState != AlarmStoreState.Unavailable;
        _add.IsEnabled = usable;
        _edit.IsEnabled = have && usable;
        _enable.IsEnabled = have && usable;
        _enable.Content = s != null && s.Definition.Enabled ? Lexicon.Get("alarms.dialog.disable") : Lexicon.Get("alarms.dialog.enable");
        AutomationProperties.SetName(_enable, (string)_enable.Content);
        _readStatus.IsEnabled = have;
        _readActive.IsEnabled = _model != null;
        _acknowledge.IsEnabled = have && OperatorAlarmsModel.CanAcknowledge(s!);
        _snooze.IsEnabled = have && OperatorAlarmsModel.CanSnooze(s!);
        _resume.IsEnabled = have && OperatorAlarmsModel.CanResume(s!);
        bool baselineAlarm = have && s!.Definition.Condition == AlarmCondition.RiseFromBaseline;
        _baseline.Visibility = baselineAlarm ? Visibility.Visible : Visibility.Collapsed;
        _baseline.IsEnabled = baselineAlarm && OperatorAlarmsModel.CanCaptureBaseline(s!);
        _history.IsEnabled = _model != null;
        _recorded.IsEnabled = usable;
    }

    // ── actions ──

    private void AddAlarm()
    {
        if (_model == null) return;
        AlarmEditorDialog.Show(this, _model, _model.NewEditor());
        RefreshRows(selectFirst: false);
    }

    /// <summary>
    /// Open the editor over the selected alarm. The editor is also where
    /// Delete lives, so it can come back having removed the row: focus returns
    /// to the list, on the row that took the deleted one's place — or the last
    /// row, or the empty list with its message — and the receipt is spoken
    /// once, from here, after the list has settled.
    /// </summary>
    private void EditAlarm()
    {
        AlarmSnapshot? s = Selected();
        if (_model == null || s == null) { Say(Lexicon.Get("alarms.dialog.select_one")); return; }
        string? deleted = AlarmEditorDialog.Show(this, _model, _model.EditorFor(s));
        RefreshRows(selectFirst: false);
        if (deleted == null) return;
        _list.Focus();
        Say(_rows.Count == 0 && _status.Text.Length != 0
            ? deleted + " " + _status.Text
            : deleted);
    }

    private void ToggleEnabled()
    {
        AlarmSnapshot? s = Selected();
        if (_model == null || s == null) { Say(Lexicon.Get("alarms.dialog.select_one")); return; }
        Say(_model.SetEnabled(s.Definition.Id, !s.Definition.Enabled));
        RefreshRows(selectFirst: false);
    }

    private void ReadStatus()
    {
        AlarmSnapshot? s = Selected(fresh: true);
        if (_model == null || s == null) { Say(Lexicon.Get("alarms.dialog.select_one")); return; }
        Say(_model.ReadStatus(s));
    }

    private void ReadActive()
    {
        if (_model == null) { Say(Lexicon.Get("alarms.summary.disconnected")); return; }
        Say(_model.ReadActive());
    }

    private void Acknowledge()
    {
        AlarmSnapshot? s = Selected();
        if (_model == null || s == null) return;
        Say(_model.Acknowledge(s.Definition.Id));
        RefreshRows(selectFirst: false);
    }

    private void OpenSnoozeMenu()
    {
        AlarmSnapshot? s = Selected();
        if (_model == null || s == null) return;
        var menu = new ContextMenu { PlacementTarget = _snooze, Placement = PlacementMode.Bottom };
        foreach (var (seconds, key) in new[]
                 {
                     (30, "alarms.dialog.snooze_30"),
                     (60, "alarms.dialog.snooze_60"),
                     (120, "alarms.dialog.snooze_120"),
                 })
        {
            string label = Lexicon.Get(key);
            var item = new MenuItem { Header = label };
            AutomationProperties.SetName(item, label);
            int chosen = seconds;
            item.Click += (_, _) => { Say(_model.Snooze(s.Definition.Id, chosen)); RefreshRows(selectFirst: false); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Resume()
    {
        AlarmSnapshot? s = Selected();
        if (_model == null || s == null) return;
        Say(_model.Resume(s.Definition.Id));
        RefreshRows(selectFirst: false);
    }

    private void CaptureBaseline()
    {
        AlarmSnapshot? s = Selected();
        if (_model == null || s == null) return;
        Say(_model.CaptureBaseline(s.Definition.Id));
        RefreshRows(selectFirst: false);
    }

    private void ShowHistory()
    {
        if (_model == null) return;
        var rows = new List<string>();
        foreach (AlarmEvent e in _model.History()) rows.Add(_model.HistoryRow(e));
        rows.Reverse();   // newest first
        SimpleListDialog.Show(this, Lexicon.Get("alarms.dialog.history_title"), Lexicon.Get("alarms.dialog.history_title"),
            rows, Lexicon.Get("alarms.dialog.history_empty"));
    }

    private void ShowRecordedMeters()
    {
        if (_model == null) return;
        RecordedMetersDialog.Show(this, _model);
    }

    /// <summary>Requested speech: Critical, interrupting, at every verbosity (#322).</summary>
    private static void Say(string sentence)
        => ScreenReaderOutput.Speak(sentence, VerbosityLevel.Critical, interrupt: true);
}
