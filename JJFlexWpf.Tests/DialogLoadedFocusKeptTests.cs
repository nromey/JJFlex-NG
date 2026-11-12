using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #996: a dialog that puts focus on a control in its own Loaded handler
/// keeps it after the window renders. <c>JJFlexDialog.OnContentRendered</c>
/// calls <c>FocusFirstControl</c> again once the window is active, and the
/// base version moves to the first control in tab order, so each dialog whose
/// Loaded target is not first now says where focus belongs, as #935 did for
/// the Command Finder. A list dialog lands on its SELECTED ROW, not on the
/// list itself (#1018 item 3, ruled 2026-11-09 for the Keys Listing, and the
/// project's list pattern since 2026-08-18), through <c>ListNaming.FocusRow</c>;
/// on the list itself only when nothing is selected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> Each dialog is an uninitialized object
/// (<c>RuntimeHelpers.GetUninitializedObject</c>, as
/// <c>CommandFinderOpenTests</c> does): no constructor runs, no XAML loads,
/// no window is built, shown or sourced. Its named target is a real WPF
/// control put into its field by reflection and placed, after a button that
/// stands for the control the base would choose, in a focus scope that is
/// never shown. The scope is measured and arranged by hand, which is what
/// makes an unhosted list generate its row containers (the window's layout
/// pass does that in the app); each row test first checks the row exists,
/// so a green result cannot come from a list that had no row to focus. The
/// thread is STA because WPF elements require it.
/// </para>
/// <para>
/// <b>What this cannot see:</b> real keyboard focus, which needs a window,
/// and whether WPF delivers Loaded and ContentRendered in the order the base
/// class assumes. The test reads LOGICAL focus in a focus scope: with no
/// window, <c>Focus()</c> on the row cannot take keyboard focus and instead
/// makes the row its scope's focused element, and <c>FocusRow</c>'s fallback
/// call on the list then leaves it there, because WPF never moves logical
/// focus off an element a scope already holds. So "the row holds logical
/// focus" is exactly what the override chose. Where keyboard focus lands in
/// the running app, and where Tab and Shift+Tab go from it, are the
/// harness's to read. Not covered: the Keys Listing with an empty list, which
/// takes the base's <c>MoveFocus</c> on a window this test never builds.
/// </para>
/// </remarks>
public sealed class DialogLoadedFocusKeptTests
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <remarks>
    /// The dialogs are named as strings, never as types. Radios.Tests'
    /// <c>DialogReachabilityTests</c> reads every source file in the repository
    /// for a dialog's name to decide whether something opens it, and a test
    /// that named <c>ListerDialog</c>, <c>ManageGroupsDialog</c>,
    /// <c>MemoryGroupDialog</c> or <c>MenusDialog</c> would make those
    /// unwired dialogs look reachable. They are not: the first three are WPF
    /// replacements whose WinForms originals are still the live ones, and the
    /// Menus pair is dead on both sides. The same is true of
    /// <c>LogCharacteristicsDialog</c>: the app opens the WinForms
    /// <c>LogCharacteristics</c> form. Only Memories and the Keys Listing are
    /// opened by the app today.
    /// </remarks>
    [Theory]
    [InlineData("ListerDialog", "ScreenList")]
    [InlineData("ManageGroupsDialog", "GroupsListBox")]
    [InlineData("MemoryGroupDialog", "GroupsListBox")]
    [InlineData("MemoriesDialog", "MemoryList")]
    [InlineData("MenusDialog", "MenuListBox")]
    [InlineData("ShowHelpDialog", "HelpListBox")]
    public void A_list_dialog_lands_on_its_selected_row(string dialogName, string target)
    {
        OnSta(() =>
        {
            // Three rows with the second selected, as the Keys Listing selects
            // the first key after its title row.
            var list = ListWithRows(3);
            list.SelectedIndex = 1;
            var (dialog, scope) = Place(dialogName, target, list);

            var row = list.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
            Assert.True(row != null, "The unhosted list generated no row container, so this test could not show where focus lands");

            CallOverride(dialog);

            object? focused = FocusManager.GetFocusedElement(scope);
            Assert.True(ReferenceEquals(row, focused),
                dialogName + " put focus on " + Describe(focused, list) + ", not on its selected row");
        });
    }

    [Theory]
    [InlineData("ListerDialog", "ScreenList", 3)]
    [InlineData("ManageGroupsDialog", "GroupsListBox", 3)]
    [InlineData("MemoryGroupDialog", "GroupsListBox", 3)]
    [InlineData("MemoriesDialog", "MemoryList", 3)]
    [InlineData("MemoriesDialog", "MemoryList", 0)]
    [InlineData("MenusDialog", "MenuListBox", 3)]
    [InlineData("ShowHelpDialog", "HelpListBox", 3)]
    public void With_nothing_selected_a_list_dialog_lands_on_the_list(string dialogName, string target, int rows)
    {
        OnSta(() =>
        {
            var list = ListWithRows(rows);
            var (dialog, scope) = Place(dialogName, target, list);

            CallOverride(dialog);

            object? focused = FocusManager.GetFocusedElement(scope);
            Assert.True(ReferenceEquals(list, focused),
                dialogName + " put focus on " + Describe(focused, list) + " with nothing selected, not on the list");
            Assert.Equal(-1, list.SelectedIndex);
        });
    }

    [Fact]
    public void Log_characteristics_lands_on_the_file_name()
    {
        OnSta(() =>
        {
            var box = new TextBox();
            var (dialog, scope) = Place("LogCharacteristicsDialog", "NameBox", box);
            CallOverride(dialog);
            Assert.Same(box, FocusManager.GetFocusedElement(scope));
        });
    }

    private static (object dialog, StackPanel scope) Place(string dialogName, string target, Control control)
    {
        Type dialogType = typeof(JJFlexDialog).Assembly.GetType("JJFlexWpf.Dialogs." + dialogName)
            ?? throw new InvalidOperationException("JJFlexWpf has no dialog " + dialogName);
        object dialog = RuntimeHelpers.GetUninitializedObject(dialogType);
        FieldInfo field = dialogType.GetField(target, AnyInstance)
            ?? throw new InvalidOperationException(dialogType.Name + " has no field " + target);
        field.SetValue(dialog, control);

        // The control the base class would choose comes first, as it does in
        // each dialog's tab order.
        var scope = new StackPanel();
        FocusManager.SetIsFocusScope(scope, true);
        scope.Children.Add(new Button());
        scope.Children.Add(control);
        scope.Measure(new Size(400, 400));
        scope.Arrange(new Rect(0, 0, 400, 400));
        return (dialog, scope);
    }

    private static void CallOverride(object dialog)
    {
        Type dialogType = dialog.GetType();
        MethodInfo method = dialogType.GetMethod("FocusFirstControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(method.DeclaringType == dialogType,
            dialogType.Name + " does not say where focus belongs after the window renders, so the base class moves it to the first control");
        method.Invoke(dialog, null);
    }

    private static string Describe(object? focused, ListBox list) => focused switch
    {
        null => "nothing",
        ListBoxItem item => "row " + list.ItemContainerGenerator.IndexFromContainer(item),
        ListBox => "the list itself",
        _ => focused.GetType().Name,
    };

    private static ListBox ListWithRows(int rows)
    {
        var list = new ListBox();
        for (int i = 0; i < rows; i++) list.Items.Add("F" + (i + 1) + "        Help");
        return list;
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }
}
