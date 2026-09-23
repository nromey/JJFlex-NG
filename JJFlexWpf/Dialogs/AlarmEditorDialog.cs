#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJTrace;
using Radios;
using Radios.Alarms;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Add or Edit an alarm: the staged form in the design's field order, Basic
/// first and Advanced collapsed, Preview warning, Save, Save as preset, Cancel.
/// </summary>
/// <remarks>
/// <para>
/// Every numeric field carries its unit in its accessible name and accepts
/// what the operator types in their own locale. Validation names the field,
/// keeps the entered values and puts focus there. An unusual line outside
/// the published range is permitted with a review message, because it may be
/// intentional. Escape cancels; nothing is saved until Save.
/// </para>
/// <para>
/// A preset fills every field and leaves them inspectable. "Save as preset"
/// stores the form under the operator's own name on this computer; the Add
/// flow's picker lists shipped presets and theirs. Long explanations are in
/// the on-demand help, never HelpText, and nothing speaks on focus.
/// </para>
/// </remarks>
public sealed class AlarmEditorDialog : JJFlexDialog
{
    private readonly OperatorAlarmsModel _model;
    private readonly AlarmEditorModel _editor;
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);

    private readonly ComboBox _preset = new();
    private readonly TextBox _name = new();
    private readonly TextBox _meterSearch = new();
    private readonly ListBox _meters = new();
    private readonly ComboBox _condition = new();
    private readonly ComboBox _direction = new();
    private readonly TextBox _threshold = new();
    private readonly TextBox _hysteresis = new();
    private readonly ComboBox _action = new();
    private readonly CheckBox _enabled = new();
    private readonly ComboBox _persistence = new();
    private readonly TextBox _sustainedCount = new();
    private readonly TextBox _sustainedSeconds = new();
    private readonly ComboBox _scope = new();
    private readonly TextBox _freshness = new();
    private readonly TextBox _reminder = new();
    private readonly TextBox _worsening = new();
    private readonly CheckBox _isTest = new();
    private readonly TextBox _summary = new();
    private readonly TextBlock _review = new();
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.Ordinal);
    private IReadOnlyList<MeterChoice> _meterChoices = Array.Empty<MeterChoice>();
    private IReadOnlyList<PresetChoice> _presetChoices = Array.Empty<PresetChoice>();
    private bool _loading;

    /// <summary>The editor with nothing attached: what the desk-guarded sweep builds. It offers no meters and refuses to save.</summary>
    public AlarmEditorDialog() : this(OperatorAlarmsModel.Detached(), null) { }

    private AlarmEditorDialog(OperatorAlarmsModel model, AlarmEditorModel? editorOrNull)
    {
        AlarmEditorModel editor = editorOrNull ?? model.NewEditor();
        _model = model;
        _editor = editor;
        Title = Lexicon.Get(editor.IsNew ? "alarms.editor.title_add" : "alarms.editor.title_edit");
        Width = 720;
        Height = 640;
        ResizeMode = ResizeMode.CanResize;

        var root = new DockPanel { Margin = new Thickness(12) };

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(MakeButton("alarms.editor.preview", Preview));
        buttons.Children.Add(MakeButton("alarms.editor.save", Save, isDefault: true));
        buttons.Children.Add(MakeButton("alarms.editor.save_as_preset", SaveAsPreset));
        var cancel = MakeButton("alarms.editor.cancel", () => CloseWithResult(false));
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        var stack = new StackPanel();

        // Radio identity, read-only.
        stack.Children.Add(Labelled("alarms.editor.radio", ReadOnly(editor.RadioSerial.Length == 0 ? "—" : editor.RadioSerial)));

        // The preset picker, in the Add flow only.
        if (editor.IsNew)
        {
            _presetChoices = model.PresetChoices();
            foreach (PresetChoice c in _presetChoices) _preset.Items.Add(c.Label);
            _preset.SelectedIndex = 0;
            _preset.SelectionChanged += (_, _) => ApplyPreset();
            stack.Children.Add(Labelled("alarms.editor.preset", _preset));
        }

        stack.Children.Add(Labelled("alarms.editor.name", _name, "name"));
        _name.TextChanged += (_, _) => { if (!_loading) { _editor.Name = _name.Text; RefreshSummary(); } };

        // Meter: a search box over the searchable list.
        stack.Children.Add(Labelled("alarms.editor.meter_search", _meterSearch));
        _meterSearch.TextChanged += (_, _) => RefreshMeters();
        AutomationProperties.SetName(_meters, Lexicon.Get("alarms.editor.meter"));
        JJFlexHelp.SetText(_meters,
            "Every meter this radio publishes: name, the radio's description, source, units, and whether it has reported. "
            + "Two meters with one name are told apart by their session index, which is not saved as identity.");
        _meters.MaxHeight = 140;
        _meters.SelectionChanged += (_, _) =>
        {
            if (_loading || _meters.SelectedIndex < 0 || _meters.SelectedIndex >= _meterChoices.Count) return;
            _editor.ChooseMeter(_meterChoices[_meters.SelectedIndex].Meter);
            RefreshUnits();
            RefreshSummary();
        };
        _fields["meter"] = _meters;
        stack.Children.Add(_meters);

        stack.Children.Add(Labelled("alarms.editor.condition", Choices(_condition,
            ("alarms.editor.condition_level", () => { _editor.Condition = AlarmCondition.Level; }),
            ("alarms.editor.condition_rise", () => { _editor.Condition = AlarmCondition.RiseFromBaseline; _editor.Direction = AlarmDirection.AtOrAbove; }),
            ("alarms.editor.condition_fall", () => { _editor.Condition = AlarmCondition.RiseFromBaseline; _editor.Direction = AlarmDirection.AtOrBelow; }),
            ("alarms.editor.condition_trend", () => { _editor.Condition = AlarmCondition.RisingFast; _editor.Direction = AlarmDirection.AtOrAbove; }))));
        stack.Children.Add(Labelled("alarms.editor.direction", Choices(_direction,
            ("alarms.editor.direction_above", () => _editor.Direction = AlarmDirection.AtOrAbove),
            ("alarms.editor.direction_below", () => _editor.Direction = AlarmDirection.AtOrBelow))));
        stack.Children.Add(Labelled("alarms.editor.threshold", Number(_threshold, "threshold", v => _editor.Threshold = v)));
        stack.Children.Add(Labelled("alarms.editor.hysteresis", Number(_hysteresis, "hysteresis", v => _editor.Hysteresis = v)));
        stack.Children.Add(Labelled("alarms.editor.action", Choices(_action,
            ("alarms.editor.action_stop", () => _editor.Action = AlarmActionClass.StopTransmit),
            ("alarms.editor.action_notify", () => _editor.Action = AlarmActionClass.NotifyOnly))));
        _enabled.Content = Lexicon.Get("alarms.editor.enabled");
        AutomationProperties.SetName(_enabled, Lexicon.Get("alarms.editor.enabled"));
        _enabled.Checked += (_, _) => { if (!_loading) _editor.Enabled = true; };
        _enabled.Unchecked += (_, _) => { if (!_loading) _editor.Enabled = false; };
        stack.Children.Add(_enabled);

        // Advanced: timing and persistence, collapsed.
        var advanced = new StackPanel();
        advanced.Children.Add(Labelled("alarms.editor.persistence", Choices(_persistence,
            ("alarms.editor.persistence_first", () => _editor.Persistence = AlarmPersistence.FirstFreshSample),
            ("alarms.editor.persistence_sustained", () => _editor.Persistence = AlarmPersistence.Sustained))));
        advanced.Children.Add(Labelled("alarms.editor.sustained_count", Number(_sustainedCount, "persistence", v => _editor.SustainedCount = (int)v)));
        advanced.Children.Add(Labelled("alarms.editor.sustained_seconds", Number(_sustainedSeconds, "persistence", v => _editor.SustainedSeconds = v)));
        advanced.Children.Add(Labelled("alarms.editor.scope", Choices(_scope,
            ("alarms.editor.scope_always", () => _editor.Scope = AlarmScope.WheneverConnected),
            ("alarms.editor.scope_transmit", () => _editor.Scope = AlarmScope.TransmitOnly))));
        advanced.Children.Add(Labelled("alarms.editor.freshness", Number(_freshness, "freshness", v => _editor.FreshnessAllowanceSeconds = v)));
        advanced.Children.Add(Labelled("alarms.editor.reminder", Number(_reminder, "reminder", v => _editor.ReminderIntervalSeconds = v)));
        advanced.Children.Add(Labelled("alarms.editor.worsening", Number(_worsening, "worsening", v => _editor.WorseningStep = v)));
        _isTest.Content = Lexicon.Get("alarms.editor.is_test");
        AutomationProperties.SetName(_isTest, Lexicon.Get("alarms.editor.is_test"));
        _isTest.Checked += (_, _) => { if (!_loading) _editor.IsTest = true; };
        _isTest.Unchecked += (_, _) => { if (!_loading) _editor.IsTest = false; };
        advanced.Children.Add(_isTest);
        var expander = new Expander { Header = Lexicon.Get("alarms.editor.advanced"), IsExpanded = false, Content = advanced, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(expander, Lexicon.Get("alarms.editor.advanced"));
        stack.Children.Add(expander);

        _review.TextWrapping = TextWrapping.Wrap;
        _review.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_review);

        AutomationProperties.SetName(_summary, Lexicon.Get("alarms.editor.summary"));
        _summary.IsReadOnly = true;
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.AcceptsReturn = true;
        _summary.MinHeight = 60;
        _summary.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(Labelled("alarms.editor.summary", _summary));

        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        LoadFields();
    }

    public static void Show(Window owner, OperatorAlarmsModel model, AlarmEditorModel editor)
    {
        var d = new AlarmEditorDialog(model, editor) { Owner = owner };
        d.ShowModalDialog();
    }

    // ── building blocks ──

    /// <summary>
    /// A label above a control, and the same words as the control's accessible
    /// name. A unit-bearing label is rendered again by <see cref="RefreshUnits"/>
    /// once a meter is chosen; until then the "in {units}" tail is dropped
    /// rather than read as a dangling "in".
    /// </summary>
    private FrameworkElement Labelled(string key, Control control, string? field = null)
    {
        if (field != null) _fields[field] = control;
        string text = LabelText(key, _editor.UnitsText);
        AutomationProperties.SetName(control, text);
        var block = new TextBlock { Text = text };
        _labels[key] = block;
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(block);
        panel.Children.Add(control);
        return panel;
    }

    private static string LabelText(string key, string units)
    {
        string label = Lexicon.Get(key, ("units", units));
        if (units.Length == 0) label = label.Replace(", in ", "").Replace(" in ", " ");
        return AlarmPhrasing.Tidy(label);
    }

    private static TextBox ReadOnly(string text) => new TextBox { Text = text, IsReadOnly = true, IsTabStop = false };

    private TextBox Number(TextBox box, string field, Action<double> apply)
    {
        _fields[field] = box;
        box.LostFocus += (_, _) =>
        {
            if (_loading) return;
            if (AlarmEditorModel.TryParse(box.Text, out double v)) { apply(v); RefreshSummary(); }
        };
        return box;
    }

    private ComboBox Choices(ComboBox box, params (string Key, Action Apply)[] items)
    {
        foreach (var (key, _) in items) box.Items.Add(Lexicon.Get(key));
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0) return;
            items[box.SelectedIndex].Apply();
            RefreshSummary();
        };
        return box;
    }

    private Button MakeButton(string key, Action onClick, bool isDefault = false)
    {
        string label = Lexicon.Get(key);
        var button = new Button { Content = label, MinWidth = 110, Height = 28, Margin = new Thickness(0, 0, 8, 6), IsDefault = isDefault };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("AlarmEditorDialog: " + label + " failed — " + ex.Message, TraceLevel.Error);
                Say(Lexicon.Get("alarms.dialog.save_failed"));
            }
        };
        return button;
    }

    // ── loading the form from the model ──

    private void LoadFields()
    {
        _loading = true;
        try
        {
            _name.Text = _editor.Name;
            RefreshMeters();
            _condition.SelectedIndex = _editor.Condition switch
            {
                AlarmCondition.RiseFromBaseline => _editor.Direction == AlarmDirection.AtOrAbove ? 1 : 2,
                AlarmCondition.RisingFast => 3,
                _ => 0,
            };
            _direction.SelectedIndex = _editor.Direction == AlarmDirection.AtOrAbove ? 0 : 1;
            _threshold.Text = _editor.Threshold.ToString(CultureInfo.CurrentCulture);
            _hysteresis.Text = _editor.Hysteresis.ToString(CultureInfo.CurrentCulture);
            _action.SelectedIndex = _editor.Action == AlarmActionClass.StopTransmit ? 0 : 1;
            _enabled.IsChecked = _editor.Enabled;
            _persistence.SelectedIndex = _editor.Persistence == AlarmPersistence.FirstFreshSample ? 0 : 1;
            _sustainedCount.Text = _editor.SustainedCount.ToString(CultureInfo.CurrentCulture);
            _sustainedSeconds.Text = _editor.SustainedSeconds.ToString(CultureInfo.CurrentCulture);
            _scope.SelectedIndex = _editor.Scope == AlarmScope.WheneverConnected ? 0 : 1;
            _freshness.Text = _editor.FreshnessAllowanceSeconds.ToString(CultureInfo.CurrentCulture);
            _reminder.Text = _editor.ReminderIntervalSeconds.ToString(CultureInfo.CurrentCulture);
            _worsening.Text = _editor.WorseningStep.ToString(CultureInfo.CurrentCulture);
            _isTest.IsChecked = _editor.IsTest;
            RefreshUnits();
            RefreshSummary();
        }
        finally { _loading = false; }
    }

    private void RefreshMeters()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            _meterChoices = _editor.MeterChoices(_meterSearch.Text);
            _meters.Items.Clear();
            foreach (MeterChoice c in _meterChoices) _meters.Items.Add(c.Label);
            if (_meterChoices.Count == 0 && !_editor.HasInventory)
                _meters.Items.Add(Lexicon.Get("alarms.editor.no_inventory"));
            if (_editor.Selector != null)
                for (int i = 0; i < _meterChoices.Count; i++)
                    if (_editor.Selector.Matches(_meterChoices[i].Meter)) { _meters.SelectedIndex = i; break; }
        }
        finally { _loading = wasLoading; }
    }

    private void RefreshUnits()
    {
        string units = _editor.UnitsText;
        foreach (var (key, control) in new[]
                 {
                     ("alarms.editor.threshold", (Control)_threshold),
                     ("alarms.editor.hysteresis", _hysteresis),
                     ("alarms.editor.worsening", _worsening),
                 })
        {
            string text = LabelText(key, units);
            AutomationProperties.SetName(control, text);
            if (_labels.TryGetValue(key, out TextBlock? block)) block.Text = text;
        }
    }

    private void RefreshSummary()
    {
        _summary.Text = _editor.Summary();
        _review.Text = _editor.RangeReview() ?? "";
    }

    private void ApplyPreset()
    {
        if (_loading || _preset.SelectedIndex < 0 || _preset.SelectedIndex >= _presetChoices.Count) return;
        PresetChoice choice = _presetChoices[_preset.SelectedIndex];
        if (choice.Kind == PresetChoiceKind.None) return;
        if (!choice.IsAvailable) { Say(choice.Problem); return; }
        _editor.ApplyPreset(choice);
        LoadFields();
    }

    // ── the actions ──

    private bool Validated()
    {
        CommitNumbers();
        IReadOnlyList<AlarmValidationProblem> problems = _editor.Validate();
        if (problems.Count == 0) return true;
        AlarmValidationProblem first = problems[0];
        Say(Lexicon.Get(first.LexiconKey));
        if (_fields.TryGetValue(first.Field, out Control? control)) control.Focus();
        return false;
    }

    /// <summary>Numbers are applied on LostFocus; a button press from inside a field must not lose the last edit.</summary>
    private void CommitNumbers()
    {
        if (AlarmEditorModel.TryParse(_threshold.Text, out double t)) _editor.Threshold = t;
        if (AlarmEditorModel.TryParse(_hysteresis.Text, out double h)) _editor.Hysteresis = h;
        if (AlarmEditorModel.TryParse(_sustainedCount.Text, out double c)) _editor.SustainedCount = (int)c;
        if (AlarmEditorModel.TryParse(_sustainedSeconds.Text, out double ss)) _editor.SustainedSeconds = ss;
        if (AlarmEditorModel.TryParse(_freshness.Text, out double f)) _editor.FreshnessAllowanceSeconds = f;
        if (AlarmEditorModel.TryParse(_reminder.Text, out double r)) _editor.ReminderIntervalSeconds = r;
        if (AlarmEditorModel.TryParse(_worsening.Text, out double w)) _editor.WorseningStep = w;
        _editor.Name = _name.Text;
    }

    private void Preview()
    {
        if (!Validated()) return;
        _model.Service.PreviewDefinition(_editor.Build());
    }

    private void Save()
    {
        if (!Validated()) return;
        string receipt = _model.Save(_editor, out bool saved);
        Say(receipt);
        if (saved) CloseWithResult(true);
    }

    private void SaveAsPreset()
    {
        if (!Validated()) return;
        string? name = EvidenceRenameDialog.Ask(this, "preset", _editor.Name, _editor.Name);
        if (name == null) return;
        Say(_model.SaveAsPreset(_editor, name));
    }

    private static void Say(string sentence)
        => ScreenReaderOutput.Speak(sentence, VerbosityLevel.Critical, interrupt: true);
}
