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
/// the Command Finder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> Each dialog is an uninitialized object
/// (<c>RuntimeHelpers.GetUninitializedObject</c>, as
/// <c>CommandFinderOpenTests</c> does): no constructor runs, no XAML loads,
/// no window is built, shown or sourced. Its named target is a real WPF
/// control put into its field by reflection and placed, after a button that
/// stands for the control the base would choose, in a focus scope that is
/// never shown. The thread is STA because WPF elements require it.
/// </para>
/// <para>
/// <b>What this cannot see:</b> real keyboard focus, which needs a window,
/// and whether WPF delivers Loaded and ContentRendered in the order the base
/// class assumes. The test reads LOGICAL focus in a focus scope and calls the
/// override the base calls at ContentRendered. Where focus lands in the
/// running app is the harness's to read.
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
    /// Menus pair is dead on both sides. Their fix waits for the day they are
    /// wired.
    /// </remarks>
    [Theory]
    [InlineData("ListerDialog", "ScreenList", "list")]
    [InlineData("ManageGroupsDialog", "GroupsListBox", "list")]
    [InlineData("MemoryGroupDialog", "GroupsListBox", "list")]
    [InlineData("MemoriesDialog", "MemoryList", "list")]
    [InlineData("MenusDialog", "MenuListBox", "list")]
    [InlineData("ShowHelpDialog", "HelpListBox", "list with rows")]
    [InlineData("LogCharacteristicsDialog", "NameBox", "text")]
    public void Focus_after_the_window_renders_is_where_loaded_put_it(string dialogName, string target, string kind)
    {
        Type dialogType = typeof(JJFlexDialog).Assembly.GetType("JJFlexWpf.Dialogs." + dialogName)
            ?? throw new InvalidOperationException("JJFlexWpf has no dialog " + dialogName);
        OnSta(() =>
        {
            object dialog = RuntimeHelpers.GetUninitializedObject(dialogType);
            Control control = kind switch
            {
                "text" => new TextBox(),
                "list with rows" => ListWithRows(),
                _ => new ListBox(),
            };
            FieldInfo field = dialogType.GetField(target, AnyInstance)
                ?? throw new InvalidOperationException(dialogType.Name + " has no field " + target);
            field.SetValue(dialog, control);

            // The control the base class would choose comes first, as it does
            // in each dialog's tab order.
            var scope = new StackPanel();
            FocusManager.SetIsFocusScope(scope, true);
            scope.Children.Add(new Button());
            scope.Children.Add(control);

            MethodInfo method = dialogType.GetMethod("FocusFirstControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(method.DeclaringType == dialogType,
                dialogType.Name + " does not say where focus belongs after the window renders, so the base class moves it to the first control");
            method.Invoke(dialog, null);

            Assert.Same(control, FocusManager.GetFocusedElement(scope));
        });
    }

    private static ListBox ListWithRows()
    {
        var list = new ListBox();
        list.Items.Add("F1        Help");
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
