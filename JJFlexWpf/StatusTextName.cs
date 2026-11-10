using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf;

/// <summary>
/// A status line whose accessible name follows its text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A <see cref="TextBlock"/> with a fixed
/// <c>AutomationProperties.Name</c> is read by that name, never by its text.
/// The Radio profile tab's status line was named "Radio profile status", so a
/// screen reader landing on it said exactly that, and never the warning its
/// code puts there "so it can be re-read" (#1022 item 5). The same shape hid
/// the message of every status and result line that carried a fixed name.
/// </para>
/// <para>
/// <b>The rule.</b> Set <c>local:StatusTextName.Label="..."</c> instead of
/// <c>AutomationProperties.Name</c>. The name is the text whenever there is
/// text, and the label only while the line is empty (<see cref="NameFor"/>).
/// </para>
/// <para>
/// <b>Label then value.</b> A line that shows a field's value beside a
/// visible label ("Radio:", "From:") also sets
/// <c>local:StatusTextName.LabelFirst="True"</c>: its name is the label, a
/// comma, then the text ("Low frequency, 14.100"), and the label alone while
/// it is empty (#1022, ruled by Noel 2026-11-09 20:35). Same rule, same
/// <see cref="NameFor(string?, string?, bool)"/>.
/// </para>
/// <para>
/// <b>Never renamed under focus.</b> A screen reader announces a name change
/// on the focused element, so while the line has keyboard focus its name is
/// left alone and caught up when focus leaves it. The text itself still
/// changes at once.
/// </para>
/// </remarks>
public static class StatusTextName
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached(
            "Label",
            typeof(string),
            typeof(StatusTextName),
            new PropertyMetadata(null, OnLabelChanged));

    public static string? GetLabel(DependencyObject element) => (string?)element.GetValue(LabelProperty);

    public static void SetLabel(DependencyObject element, string? value) => element.SetValue(LabelProperty, value);

    /// <summary>
    /// True for a line that is a field's value: its name is read label first,
    /// then the value. Meaningful only beside <see cref="LabelProperty"/>.
    /// </summary>
    public static readonly DependencyProperty LabelFirstProperty =
        DependencyProperty.RegisterAttached(
            "LabelFirst",
            typeof(bool),
            typeof(StatusTextName),
            new PropertyMetadata(false, OnLabelFirstChanged));

    public static bool GetLabelFirst(DependencyObject element) => (bool)element.GetValue(LabelFirstProperty);

    public static void SetLabelFirst(DependencyObject element, bool value) => element.SetValue(LabelFirstProperty, value);

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    /// <summary>
    /// The name a status line carries: its text when it has any, otherwise its
    /// label. Pure, so the rule is tested without a window.
    /// </summary>
    internal static string NameFor(string? label, string? text)
        => NameFor(label, text, labelFirst: false);

    /// <summary>
    /// The same rule with the label-then-value mode: with
    /// <paramref name="labelFirst"/> and both present, "label, text". Pure.
    /// </summary>
    internal static string NameFor(string? label, string? text, bool labelFirst)
    {
        if (string.IsNullOrWhiteSpace(text)) return label ?? string.Empty;
        if (!labelFirst || string.IsNullOrWhiteSpace(label)) return text.Trim();
        return label.Trim() + ", " + text.Trim();
    }

    /// <summary>
    /// Whether a change of text renames the line now: not while it holds
    /// keyboard focus. Pure.
    /// </summary>
    internal static bool RenamesNow(bool hasKeyboardFocus) => !hasKeyboardFocus;

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock line) return;
        line.Loaded -= OnLoaded;
        line.Unloaded -= OnUnloaded;
        line.LostKeyboardFocus -= OnLostKeyboardFocus;
        TextDescriptor.RemoveValueChanged(line, OnTextChanged);
        if (e.NewValue == null) return;
        line.Loaded += OnLoaded;
        line.Unloaded += OnUnloaded;
        line.LostKeyboardFocus += OnLostKeyboardFocus;
        if (line.IsLoaded) TextDescriptor.AddValueChanged(line, OnTextChanged);
        Apply(line);
    }

    private static void OnLabelFirstChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The label's own hook-up does the watching; this only renames a line
        // that already has its label, so the order XAML sets them in is moot.
        if (d is TextBlock line && GetLabel(line) != null) Apply(line);
    }

    // Watched only while loaded: the descriptor holds the line strongly, so a
    // closed dialog's lines are let go on Unloaded.
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var line = (TextBlock)sender;
        TextDescriptor.RemoveValueChanged(line, OnTextChanged);
        TextDescriptor.AddValueChanged(line, OnTextChanged);
        Apply(line);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
        => TextDescriptor.RemoveValueChanged((TextBlock)sender, OnTextChanged);

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock line && RenamesNow(line.IsKeyboardFocused)) Apply(line);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Apply((TextBlock)sender);

    private static void Apply(TextBlock line)
    {
        string name = NameFor(GetLabel(line), line.Text, GetLabelFirst(line));
        if (!string.Equals(AutomationProperties.GetName(line), name, StringComparison.Ordinal))
            AutomationProperties.SetName(line, name);
    }
}
