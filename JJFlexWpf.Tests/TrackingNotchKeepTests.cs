using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The tracking notch's keep-it choice is a checkbox: it shows the selected
/// notch's flag, and checking or unchecking it writes that flag for that
/// notch, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> The dialog is an uninitialized object
/// (<c>RuntimeHelpers.GetUninitializedObject</c>, as in
/// <c>CheckYourAudioCtrlF1Tests</c>): its constructor never runs, so no window
/// is built, shown or sourced. Its named controls are real WPF controls put
/// into its fields by reflection and never placed in a window. The thread is
/// STA because WPF elements require it. The toggle tone goes through the
/// earcon player, which this assembly's module initializer has silenced
/// (<c>Infrastructure/QuietRun</c>). No radio: the notch list is three fakes.
/// </para>
/// <para>
/// The checkbox's <c>Checked</c> and <c>Unchecked</c> are wired to the
/// dialog's own handler, as the XAML wires them. <b>What this cannot see:</b>
/// what NVDA says on Space, and the radio's reply. Those are the bench's.
/// </para>
/// </remarks>
public sealed class TrackingNotchKeepTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void TheCheckboxShowsTheSelectedNotchsFlagWithoutWritingIt()
    {
        Run(() =>
        {
            var (dialog, list, check, writes) = Dialog(new[] { false, true, false });

            list.SelectedIndex = 1;
            Invoke(dialog, "UpdateTNFProperties");
            Assert.True(check.IsChecked);

            list.SelectedIndex = 0;
            Invoke(dialog, "UpdateTNFProperties");
            Assert.False(check.IsChecked);

            // Showing a notch's flag is not the operator changing it.
            Assert.Empty(writes);
        });
    }

    [Fact]
    public void CheckingAndUncheckingWritesTheSelectedNotchsFlag()
    {
        Run(() =>
        {
            var (dialog, list, check, writes) = Dialog(new[] { false, false, false });
            list.SelectedIndex = 2;
            Invoke(dialog, "UpdateTNFProperties");

            check.IsChecked = true;
            check.IsChecked = false;

            Assert.Equal(new[] { (2, true), (2, false) }, writes.ToArray());
        });
    }

    [Fact]
    public void WithNothingSelectedTheCheckboxIsOutOfTheTabOrder()
    {
        Run(() =>
        {
            var (dialog, list, check, writes) = Dialog(Array.Empty<bool>());
            list.SelectedIndex = -1;
            Invoke(dialog, "UpdateTNFProperties");

            Assert.False(check.IsEnabled);
            Assert.False(check.IsChecked);
            Assert.Empty(writes);
        });
    }

    private static (TNFDialog Dialog, ListBox List, CheckBox Check, List<(int, bool)> Writes) Dialog(bool[] kept)
    {
        var dialog = (TNFDialog)RuntimeHelpers.GetUninitializedObject(typeof(TNFDialog));
        var writes = new List<(int, bool)>();
        dialog.GetTNFCount = () => kept.Length;
        dialog.GetTNFWidth = _ => 100;
        dialog.GetTNFDepth = _ => 2;
        dialog.GetTNFPermanent = i => kept[i];
        dialog.SetTNFPermanent = (i, p) => writes.Add((i, p));

        var list = new ListBox();
        for (int i = 0; i < kept.Length; i++) list.Items.Add("14.10" + i);
        var check = new CheckBox();
        Put(dialog, "TNFList", list);
        Put(dialog, "WidthBox", new TextBox());
        Put(dialog, "DepthBox", new TextBox());
        Put(dialog, "PermanentCheck", check);
        Put(dialog, "RemoveButton", new Button());

        var handler = typeof(TNFDialog).GetMethod("PermanentCheck_Changed", Private)
            ?? throw new InvalidOperationException("TNFDialog has no PermanentCheck_Changed handler");
        var routed = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), dialog, handler);
        check.Checked += routed;
        check.Unchecked += routed;
        return (dialog, list, check, writes);
    }

    private static void Run(Action body)
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

    private static void Put(object target, string name, object? value) =>
        (typeof(TNFDialog).GetField(name, Private | BindingFlags.Public)
            ?? throw new InvalidOperationException("TNFDialog has no field " + name)).SetValue(target, value);

    private static void Invoke(object target, string name) =>
        typeof(TNFDialog).GetMethod(name, Private | BindingFlags.DeclaredOnly)!.Invoke(target, null);
}
