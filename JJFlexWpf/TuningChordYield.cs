using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Where the four tuning chords give the arrows back (#703 J4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled by Noel 2026-11-09 20:40 (night question J4):</b> Ctrl and Shift
/// with Up and Down tune everywhere in the main window, EXCEPT where the arrows
/// change the focused thing's own value: Home's field-group values, lists and
/// text boxes keep their meaning. Logging mode's radio pane is unchanged
/// (the commands are bound at <see cref="KeyScope.Radio"/>, which Logging mode
/// does not run), and dialogs keep their own keys (Radio scope never
/// dispatches from a dialog).
/// </para>
/// <para>
/// <b>Asked in KeyCommands.DoCommand's registry branch, before the no-radio
/// guard and before the key is marked handled</b>, because a command handler
/// cannot yield: DoCommand has already consumed the key by the time it runs.
/// Returning there leaves the key to the focused control.
/// </para>
/// <para>
/// <b>Focus sits on a part, not the whole:</b> a list item, a grid cell, an
/// editable combo box's own text box, a child inside a value field. So the
/// question walks the focused element and its parents.
/// </para>
/// <para>
/// <b>The slice row's text box yields too, and that is right.</b>
/// FrequencyDisplay's text box is a TextBox, so on the slice row the chord goes
/// on to <c>FreqOutHandlers.TryHandleSliceRowKey</c>, which tunes by the same
/// step and says the slice row's no-radio sentence. Do not "fix" that by
/// exempting it: there is then one tuning path on the slice row, not two.
/// </para>
/// </remarks>
public static class TuningChordYield
{
    /// <summary>The four commands this governs.</summary>
    public static bool IsTuningChord(CommandValues id) =>
        id == CommandValues.TuneCoarseUp || id == CommandValues.TuneCoarseDown
        || id == CommandValues.TuneFineUp || id == CommandValues.TuneFineDown;

    /// <summary>
    /// Whether an element of this type keeps the arrows for its own value:
    /// Home's value and cycle fields, lists (a ListView is a ListBox), grids,
    /// trees, combo boxes and text boxes. Pure.
    /// </summary>
    public static bool OwnsArrows(DependencyObject element) =>
        element is ValueFieldControl
        || element is CycleFieldControl
        || element is ListBox
        || element is DataGrid
        || element is TreeView
        || element is ComboBox
        || element is TextBoxBase
        || element is PasswordBox;

    /// <summary>
    /// Whether the focused element, or any element it sits inside, keeps the
    /// arrows. Walks visual parents, and logical parents where there is no
    /// visual one (a list item not yet shown, a content element).
    /// </summary>
    public static bool FocusOwnsArrows(DependencyObject? focused)
    {
        for (DependencyObject? d = focused; d != null; d = ParentOf(d))
            if (OwnsArrows(d)) return true;
        return false;
    }

    /// <summary>The question DoCommand asks, for the element focused now.</summary>
    public static bool Yields(CommandValues id) =>
        IsTuningChord(id) && FocusOwnsArrows(Keyboard.FocusedElement as DependencyObject);

    private static DependencyObject? ParentOf(DependencyObject d)
    {
        DependencyObject? parent = null;
        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
            parent = VisualTreeHelper.GetParent(d);
        return parent ?? LogicalTreeHelper.GetParent(d);
    }
}
