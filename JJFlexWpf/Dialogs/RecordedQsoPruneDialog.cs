using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Prune recordings (#787, Tape Deck track 4), only when asked (Noel,
/// 2026-10-21 09:16) and never on a schedule. Two rules and no date picker
/// (2026-10-24 16:01: "I'd do the oldest 20 or days old. Picking a date can be
/// kind of finicky"): the oldest N recordings, or recordings older than N
/// days, each a number typed or stepped with the arrow keys.
/// </summary>
/// <remarks>
/// Nothing moves until the operator presses the button that names the count
/// AND answers the confirmation. Changing the rule or the number only changes
/// the preview: how many, how much space, the oldest and newest dates, and the
/// list of what would go. A recording in use (being made, playing, or being
/// saved as) is left where it is and named. Everything goes to the Recycle
/// Bin; the recordings dialog does the moving after this closes.
/// </remarks>
internal sealed class RecordedQsoPruneDialog : JJFlexDialog
{
    private readonly IReadOnlyList<RecordedQso> _all;
    private readonly Func<string, bool> _inUse;
    private readonly RadioButton _byCount = new();
    private readonly RadioButton _byAge = new();
    private readonly RadioButton _byKeep = new();
    private readonly TextBox _keep = new();
    private readonly TextBox _count = new();
    private readonly TextBox _days = new();
    private readonly TextBox _preview = new();
    private readonly ListBox _list = new();
    private readonly Button _move;
    private RecordedQsoPrunePlan _plan = new(Array.Empty<RecordedQso>(), Array.Empty<RecordedQso>(), 0);

    /// <summary>The recordings to move, set only when the operator confirmed.</summary>
    internal IReadOnlyList<RecordedQso> Chosen { get; private set; } = Array.Empty<RecordedQso>();

    /// <summary>The count the dialog opens with, from Noel's own example ("the oldest 20").</summary>
    internal const int DefaultCount = 20;

    /// <summary>
    /// The count to open with: 20, but never more than half of what there
    /// is, so a short list does not open already set to move every recording.
    /// Claude's choice, not ruled; the preview and the confirmation stand
    /// between it and anything moving either way.
    /// </summary>
    internal static int OpeningCount(int total) => Math.Max(1, Math.Min(DefaultCount, total / 2));

    /// <summary>The age the dialog opens with. Claude's choice, not ruled.</summary>
    internal const int DefaultDays = 30;

    internal RecordedQsoPruneDialog(IReadOnlyList<RecordedQso> all, Func<string, bool> inUse)
    {
        _all = all;
        _inUse = inUse;
        Title = Lexicon.Get("audio.qsos.prune_title");
        Width = 600;
        Height = 520;
        ResizeMode = ResizeMode.CanResize;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel();
        _byCount.Content = Lexicon.Get("audio.qsos.prune_by_count");
        _byAge.Content = Lexicon.Get("audio.qsos.prune_by_age");
        AutomationProperties.SetName(_byCount, Lexicon.Get("audio.qsos.prune_by_count"));
        AutomationProperties.SetName(_byAge, Lexicon.Get("audio.qsos.prune_by_age"));
        _byKeep.Content = Lexicon.Get("audio.qsos.prune_by_keep");
        AutomationProperties.SetName(_byKeep, Lexicon.Get("audio.qsos.prune_by_keep"));
        _byCount.GroupName = _byAge.GroupName = _byKeep.GroupName = "rule";
        _byCount.IsChecked = true;
        _byCount.Checked += (_, _) => Update();
        _byAge.Checked += (_, _) => Update();
        _byKeep.Checked += (_, _) => Update();

        // The two choices sit together, then their numbers. With a number box
        // between them, arrowing from the first choice went into the box and
        // never reached the second (retest 2026-10-25: "I can't arrow up and
        // down on these radio buttons").
        top.Children.Add(_byCount);
        top.Children.Add(_byAge);
        top.Children.Add(_byKeep);
        top.Children.Add(Number(_count, "audio.qsos.prune_count", OpeningCount(all.Count)));
        top.Children.Add(Number(_days, "audio.qsos.prune_days", DefaultDays));
        top.Children.Add(Number(_keep, "audio.qsos.prune_keep", OpeningCount(all.Count)));

        top.Children.Add(new Label { Content = Lexicon.Get("audio.qsos.prune_preview_label"), Target = _preview, Padding = new Thickness(0, 4, 0, 2) });
        AutomationProperties.SetName(_preview, Lexicon.Get("audio.qsos.prune_preview_label"));
        _preview.IsReadOnly = true;
        _preview.IsReadOnlyCaretVisible = true;
        _preview.TextWrapping = TextWrapping.Wrap;
        _preview.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(_preview);
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        AutomationProperties.SetName(_list, Lexicon.Get("audio.qsos.prune_list"));
        _list.IsTextSearchEnabled = false;
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        _move = new Button { MinWidth = 120, Height = 28, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 0, 8, 0) };
        _move.Click += (_, _) => Confirm();
        var cancel = new Button { Content = Lexicon.Get("audio.qsos.prune_cancel"), MinWidth = 80, Height = 28 };
        AutomationProperties.SetName(cancel, Lexicon.Get("audio.qsos.prune_cancel"));
        cancel.Click += (_, _) => CloseWithResult(false);
        buttons.Children.Add(_move);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;

        Update();
    }

    protected override void FocusFirstControl() => _byCount.Focus();

    private FrameworkElement Number(TextBox box, string labelKey, int value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 8) };
        row.Children.Add(new Label { Content = Lexicon.Get(labelKey), Target = box, Padding = new Thickness(0, 4, 8, 0) });
        AutomationProperties.SetName(box, Lexicon.Get(labelKey));
        box.MinWidth = 80;
        box.Text = value.ToString(CultureInfo.CurrentCulture);
        box.TextChanged += (_, _) => Update();
        box.PreviewKeyDown += (_, e) =>
        {
            // Up and Down step the number, so it can be spun as well as typed.
            if (e.Key is not (Key.Up or Key.Down) || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            int n = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) ? parsed : 0;
            n = Math.Max(1, n + (e.Key == Key.Up ? 1 : -1));
            box.Text = n.ToString(CultureInfo.CurrentCulture);
            box.CaretIndex = box.Text.Length;
        };
        row.Children.Add(box);
        return row;
    }

    private void Update()
    {
        if (_move == null) return; // during construction
        bool byCount = _byCount.IsChecked == true;
        bool byKeep = _byKeep.IsChecked == true;
        _count.IsEnabled = byCount;
        _keep.IsEnabled = byKeep;
        _days.IsEnabled = !byCount && !byKeep;
        int max = byCount || byKeep ? Math.Max(1, _all.Count) : 36500;
        var box = byCount ? _count : byKeep ? _keep : _days;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) || n < 1 || n > max)
        {
            _plan = new(Array.Empty<RecordedQso>(), Array.Empty<RecordedQso>(), 0);
            Show(Lexicon.Get("audio.qsos.prune_bad_number", ("max", max)));
            return;
        }
        _plan = byCount ? RecordedQsoCatalog.PlanOldest(_all, n, _inUse)
            : byKeep ? RecordedQsoCatalog.PlanKeepNewest(_all, n, _inUse)
            : RecordedQsoCatalog.PlanOlderThan(_all, n, DateTime.Now, _inUse);
        Show(Describe(_plan, DateTime.Now));
    }

    /// <summary>The preview, as one block of sentences. Internal so a test can read it assembled.</summary>
    // DRAFT PROSE (#787): awaiting Noel.
    internal static string Describe(RecordedQsoPrunePlan plan, DateTime now)
    {
        var parts = new List<string>();
        if (plan.Move.Count == 0) parts.Add(Lexicon.Get("audio.qsos.prune_preview_none"));
        else if (plan.Move.Count == 1)
            parts.Add(Lexicon.Get("audio.qsos.prune_preview_one", ("size", RecordedQsoWords.Size(plan.Bytes))));
        else
            parts.Add(Lexicon.Get("audio.qsos.prune_preview_many", ("count", plan.Move.Count),
                ("size", RecordedQsoWords.Size(plan.Bytes)),
                ("oldest", RecordedQsoWords.WhenInSentence(plan.Move.Min(r => r.RecordedAt), now)),
                ("newest", RecordedQsoWords.WhenInSentence(plan.Move.Max(r => r.RecordedAt), now))));
        foreach (var busy in plan.InUse)
            parts.Add(Lexicon.Get("audio.qsos.prune_in_use", ("name", RecordedQsoWords.Row(busy, now))));
        return string.Join(" ", parts);
    }

    private void Show(string preview)
    {
        _preview.Text = preview;
        DateTime now = DateTime.Now;
        _list.Items.Clear();
        foreach (var q in _plan.Move)
        {
            string text = RecordedQsoWords.Row(q, now);
            var item = new ListBoxItem { Content = text };
            AutomationProperties.SetName(item, text);
            _list.Items.Add(item);
        }
        _list.IsEnabled = _plan.Move.Count > 0;
        string label = _plan.Move.Count == 1 ? Lexicon.Get("audio.qsos.prune_move_one")
            : Lexicon.Get("audio.qsos.prune_move_many", ("count", _plan.Move.Count));
        _move.Content = label;
        AutomationProperties.SetName(_move, label);
        _move.IsEnabled = _plan.Move.Count > 0;
    }

    private void Confirm()
    {
        if (_plan.Move.Count == 0) return;
        string size = RecordedQsoWords.Size(_plan.Bytes);
        var confirm = new ConfirmActionDialog(Lexicon.Get("audio.qsos.prune_confirm_title"),
            _plan.Move.Count == 1
                ? Lexicon.Get("audio.qsos.prune_confirm_one", ("size", size))
                : Lexicon.Get("audio.qsos.prune_confirm_many", ("count", _plan.Move.Count), ("size", size)),
            question: Lexicon.Get(_plan.Move.Count == 1 ? "audio.qsos.delete_question" : "audio.qsos.prune_confirm_question"),
            yesLabel: _move.Content as string ?? "_Yes",
            noLabel: Lexicon.Get("audio.qsos.delete_no"))
        { Owner = this };
        if (confirm.ShowModalDialog() != true) { _move.Focus(); return; }
        Chosen = _plan.Move;
        CloseWithResult(true);
    }
}
