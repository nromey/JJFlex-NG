using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using JJTrace;

namespace JJFlexWpf;

/// <summary>
/// The Delete key on a list presses that list's own Remove, Delete or Forget
/// button for the selected item (#774). One behaviour a list opts into, not a
/// key handler copied into every dialog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled by Noel 2026-10-19:</b> <i>"There's no way to remove a notch with
/// the delete key. Any other functions where we remove items should also be
/// removable with del."</i>
/// </para>
/// <para>
/// <b>The rule.</b> Delete is a second way to press the SAME button, never a
/// faster or quieter one: it raises the button's own Click, so a removal that
/// asks "are you sure" still asks, and a button that is disabled or hidden is
/// not pressed. Bare Delete only, and only while the list itself has focus: a
/// text box keeps Delete for deleting text. Bulk actions that are not "remove
/// the item I am on" are never wired to this.
/// </para>
/// <para>
/// <b>Where focus lands.</b> When the item really went, the next item is
/// selected and focused, or the previous one when the last was removed, and
/// an emptied list keeps focus itself: never nowhere. When nothing went (the
/// operator said No, or the button refused), focus and selection are left as
/// they are. What is SPOKEN is the button's own business and the item focus
/// lands on, so a list says no new sentence of its own here.
/// </para>
/// </remarks>
public static class DeleteKeyRemoves
{
    /// <summary>The button Delete presses for a list. Set it in code once the button exists.</summary>
    public static readonly DependencyProperty ButtonProperty = DependencyProperty.RegisterAttached(
        "Button", typeof(ButtonBase), typeof(DeleteKeyRemoves),
        new PropertyMetadata(null, OnButtonChanged));

    public static ButtonBase? GetButton(DependencyObject list) => (ButtonBase?)list.GetValue(ButtonProperty);

    /// <summary>Make Delete on <paramref name="list"/> press <paramref name="button"/>.</summary>
    public static void SetButton(DependencyObject list, ButtonBase? button) => list.SetValue(ButtonProperty, button);

    private static void OnButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Selector list) return;
        list.PreviewKeyDown -= OnPreviewKeyDown;
        list.PreviewKeyUp -= OnPreviewKeyUp;
        list.PreviewMouseDown -= OnPreviewMouseDown;
        list.IsKeyboardFocusWithinChanged -= OnFocusWithinChanged;
        if (e.NewValue != null)
        {
            list.PreviewKeyDown += OnPreviewKeyDown;
            list.PreviewKeyUp += OnPreviewKeyUp;
            list.PreviewMouseDown += OnPreviewMouseDown;
            list.IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Selector list) Clicked(GateFor(list), Environment.TickCount64);
    }

    /// <summary>
    /// Focus leaving the list (a question opening, the operator moving away)
    /// and coming back (repair round 2): a Delete still held as a question
    /// closes cannot act again.
    /// </summary>
    private static void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Selector list) return;
        var gate = GateFor(list);
        if (e.NewValue is true) gate.Resumed(Environment.TickCount64);
        else gate.Interrupted(Environment.TickCount64);
    }

    /// <summary>Each list's own one-press-one-removal filter (Codex's check of #774, blocker 1).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Selector, Radios.DeleteKeyGate> Gates = new();

    private static Radios.DeleteKeyGate GateFor(Selector list) =>
        Gates.GetValue(list, _ => new Radios.DeleteKeyGate(Radios.DeleteKeyGate.GapForKeyboard(
            Radios.KeyRepeatTiming.DelayMsFromSetting(SystemParameters.KeyboardDelay),
            Radios.KeyRepeatTiming.RepeatPeriodMsFromSetting(SystemParameters.KeyboardSpeed))));

    /// <summary>
    /// One Delete key-down, decided: true when it presses the button. Every
    /// bare Delete down outside a text box is reported to the gate, so a
    /// held key (NVDA's repeats, JAWS's down/up pairs) and a removal still
    /// landing press nothing. Pure apart from the gate, so a test replays
    /// both readers' streams through the same decision the list makes.
    /// </summary>
    internal static bool Decide(Radios.DeleteKeyGate gate, Key key, ModifierKeys mods, bool isRepeat, bool fromTextEntry,
        int selectedIndex, bool buttonPresent, bool buttonEnabled, bool buttonVisible, long nowMs)
    {
        if (key == Key.Delete && (mods != ModifierKeys.None || fromTextEntry))
        {
            // Still the Delete key: it continues a hold and never ends one
            // (repair round 3).
            gate.DeleteThatDoesNotPress(nowMs);
            return false;
        }
        if (key != Key.Delete)
        {
            // Another key on the list: once Delete has come up, a new intent
            // (repair round 2), so a deliberate press on the next item counts.
            gate.OtherInput(nowMs);
            return false;
        }
        bool press = gate.Down(isRepeat, nowMs);
        return press && Presses(key, mods, fromTextEntry, selectedIndex, buttonPresent, buttonEnabled, buttonVisible);
    }

    /// <summary>
    /// A mouse press on the list, decided: a new intent only once Delete has
    /// really been released (repair round 3, ruled by Noel 2026-11-04; see
    /// <see cref="Radios.DeleteKeyGate.Pointer"/>).
    /// </summary>
    internal static void Clicked(Radios.DeleteKeyGate gate, long nowMs) => gate.Pointer(nowMs);

    private static void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Selector list && e.Key == Key.Delete) GateFor(list).Up(Environment.TickCount64);
    }

    /// <summary>
    /// Whether Delete on a list should press its button: bare Delete, an item
    /// selected, a button there to press, and the key not typed into a text
    /// box inside the list. Pure, so a test can hold the rule without a window.
    /// </summary>
    internal static bool Presses(Key key, ModifierKeys mods, bool fromTextEntry, int selectedIndex,
        bool buttonPresent, bool buttonEnabled, bool buttonVisible) =>
        key == Key.Delete && mods == ModifierKeys.None && !fromTextEntry
        && selectedIndex >= 0 && buttonPresent && buttonEnabled && buttonVisible;

    /// <summary>
    /// Where selection goes after a press, or -1 for "leave it": nothing was
    /// removed, or the list is now empty. Next item, else the previous one.
    /// </summary>
    internal static int LandingIndex(int removedAt, int countBefore, int countAfter) =>
        Radios.DeleteKeyGate.LandingIndex(removedAt, countBefore, countAfter);

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Selector list) return;
        var button = GetButton(list);
        bool fromText = e.OriginalSource is TextBoxBase || e.OriginalSource is PasswordBox
                        || (e.OriginalSource is ComboBox combo && combo.IsEditable);
        var gate = GateFor(list);
        if (!Decide(gate, e.Key, Keyboard.Modifiers, e.IsRepeat, fromText, list.SelectedIndex,
                button != null, button?.IsEnabled == true, button?.IsVisible == true, Environment.TickCount64))
            return;

        e.Handled = true;
        gate.Busy = true;
        int at = list.SelectedIndex;
        int before = list.Items.Count;
        Tracing.TraceLine("DeleteKeyRemoves: Delete on " + Describe(list) + " presses its "
            + Describe(button!) + " button for item " + at, TraceLevel.Info);
        try
        {
            button!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            if (button.Command != null && button.Command.CanExecute(button.CommandParameter))
                button.Command.Execute(button.CommandParameter);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("DeleteKeyRemoves: the button threw: " + ex.Message, TraceLevel.Error);
            gate.Busy = false;
            return;
        }

        // After the button's own work, including any list refresh it queued.
        // The gate stays busy until then, so a second Delete cannot press
        // the button again on an item that is going.
        list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { Land(list, at, before); }
            finally { gate.Busy = false; }
        }));
    }

    private static void Land(Selector list, int at, int before)
    {
        int after = list.Items.Count;
        if (after >= before) return;   // nothing went: leave focus alone
        int landing = LandingIndex(at, before, after);
        if (landing < 0)
        {
            list.Focus();   // emptied: the list keeps focus, never nowhere
            return;
        }
        list.SelectedIndex = landing;
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(landing) is UIElement item && item.Focusable)
            item.Focus();
        else
            list.Focus();
    }

    private static string Describe(FrameworkElement e) =>
        string.IsNullOrEmpty(e.Name) ? e.GetType().Name : e.Name;
}
