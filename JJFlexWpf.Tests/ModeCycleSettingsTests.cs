using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Settings, Notifications, Mode Cycle: the list shows the cycle first in its
/// order and every other mode after it, the checkbox and the two buttons act
/// on the selected mode, and only OK or Apply writes the cycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> The Settings dialog is an uninitialized object
/// (<c>RuntimeHelpers.GetUninitializedObject</c>, as in
/// <c>CheckYourAudioCtrlF1Tests</c>): its constructor never runs, so no window
/// is built, shown or sourced. The section's six controls are real WPF
/// controls put into its fields by reflection and never placed in a window,
/// with the XAML's three handlers wired to them. The thread is STA because WPF
/// elements require it. The one spoken line goes to the silenced speech layer
/// (<c>Infrastructure/QuietRun</c>).
/// </para>
/// <para>
/// <b>What this cannot see:</b> the section inside the real tab, the tab
/// order, and what NVDA reads on each control. Those are a probe's or the
/// bench's.
/// </para>
/// </remarks>
public sealed class ModeCycleSettingsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void The_cycle_comes_first_in_its_order_then_every_other_mode_and_DSB_and_SAM_are_offered()
    {
        WithOperatorConfig(null, () =>
        {
            var (dialog, list, check, up, down) = Section();
            Assert.Equal(new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM", "DSB", "SAM" }, Rows(list));
            Assert.Contains("in the cycle", Text(list, 0));
            Assert.Contains("not in the cycle", Text(list, 7));
            Assert.DoesNotContain("nabled", Text(list, 7));

            // Untouched: OK writes nothing, so the setting stays "as shipped".
            Invoke(dialog, "CommitModeCycle");
            Assert.Null(AccessibilityConfig.Current.ModeCycleModes);
        });
    }

    [Fact]
    public void Checking_SAM_adds_it_to_the_end_of_the_cycle_and_OK_saves_it()
    {
        WithOperatorConfig(null, () =>
        {
            var (dialog, list, check, up, down) = Section();
            Select(list, "SAM");
            Assert.False(check.IsChecked);
            Assert.False(up.IsEnabled);     // reordering is for cycle members
            Assert.False(down.IsEnabled);

            check.IsChecked = true;

            Assert.Equal("SAM", Rows(list)[7]);
            Assert.Equal("SAM", Rows(list)[list.SelectedIndex]);   // the selection followed it
            Assert.True(up.IsEnabled);
            Assert.False(down.IsEnabled);

            Invoke(dialog, "CommitModeCycle");
            Assert.Equal(new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM", "SAM" },
                AccessibilityConfig.Current.ModeCycleModes);

            // ...and into the operator file the application loads, not only
            // the object in memory (Codex B1 on 908aa3da).
            string onDisk = File.ReadAllText(OperatorFile);
            Assert.Contains("<Mode>SAM</Mode>", onDisk);
        });
    }

    [Fact]
    public void Move_up_and_unchecking_change_the_saved_order()
    {
        WithOperatorConfig(new[] { "USB", "LSB", "CW" }, () =>
        {
            var (dialog, list, check, up, down) = Section();
            Select(list, "CW");
            up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { "USB", "CW", "LSB" }, Rows(list).Take(3));
            Assert.Equal("CW", Rows(list)[list.SelectedIndex]);

            Select(list, "USB");
            check.IsChecked = false;
            Assert.Equal(new[] { "CW", "LSB" }, Rows(list).Take(2));

            Invoke(dialog, "CommitModeCycle");
            Assert.Equal(new[] { "CW", "LSB" }, AccessibilityConfig.Current.ModeCycleModes);
        });
    }

    [Fact]
    public void Without_OK_nothing_is_written()
    {
        WithOperatorConfig(new[] { "CW" }, () =>
        {
            var (_, list, check, _, _) = Section();
            Select(list, "USB");
            check.IsChecked = true;
            // Cancel never calls CommitModeCycle.
            Assert.Equal(new[] { "CW" }, AccessibilityConfig.Current.ModeCycleModes);
        });
    }

    private static (SettingsDialog Dialog, ListBox List, CheckBox Check, Button Up, Button Down) Section()
    {
        var dialog = (SettingsDialog)RuntimeHelpers.GetUninitializedObject(typeof(SettingsDialog));
        var list = new ListBox();
        var check = new CheckBox();
        var up = new Button();
        var down = new Button();
        Put(dialog, "ModeCycleGroup", new GroupBox());
        Put(dialog, "ModeCycleIntro", new TextBlock());
        Put(dialog, "ModeCycleList", list);
        Put(dialog, "ModeCycleIncludeCheck", check);
        Put(dialog, "ModeCycleMoveUpButton", up);
        Put(dialog, "ModeCycleMoveDownButton", down);

        list.SelectionChanged += (SelectionChangedEventHandler)Handler(typeof(SelectionChangedEventHandler), dialog, "ModeCycleList_SelectionChanged");
        var toggled = (RoutedEventHandler)Handler(typeof(RoutedEventHandler), dialog, "ModeCycleIncludeCheck_Changed");
        check.Checked += toggled;
        check.Unchecked += toggled;
        up.Click += (RoutedEventHandler)Handler(typeof(RoutedEventHandler), dialog, "ModeCycleMoveUpButton_Click");
        down.Click += (RoutedEventHandler)Handler(typeof(RoutedEventHandler), dialog, "ModeCycleMoveDownButton_Click");

        Invoke(dialog, "InitializeModeCycle");
        return (dialog, list, check, up, down);
    }

    private static string[] Rows(ListBox list) =>
        list.Items.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToArray();

    private static string Text(ListBox list, int i) => (string)((ListBoxItem)list.Items[i]).Content;

    private static void Select(ListBox list, string mode) =>
        list.SelectedIndex = Array.IndexOf(Rows(list), mode);

    [ThreadStatic] private static string? _operatorFile;
    private static string OperatorFile => _operatorFile ?? throw new InvalidOperationException("no operator file");

    /// <summary>
    /// Give the run an operator file in a temporary folder, loaded through the
    /// application's own load, so a commit writes where start-up reads.
    /// </summary>
    private static void WithOperatorConfig(string[]? cycle, Action body)
    {
        var current = typeof(AccessibilityConfig).GetProperty(nameof(AccessibilityConfig.Current))!;
        var saved = current.GetValue(null);
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-modecycle-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ForgetHome();
            new AccessibilityConfig { ModeCycleModes = cycle }.Save(dir, "op");
            AccessibilityConfig.Load(dir, "op");
            string file = Directory.GetFiles(dir).Single();
            Run(() => { _operatorFile = file; body(); });
        }
        finally
        {
            ForgetHome();
            current.SetValue(null, saved);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void ForgetHome() =>
        typeof(AccessibilityConfig).GetMethod("ForgetHomeForTests", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);

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

    private static Delegate Handler(Type type, object target, string name) =>
        Delegate.CreateDelegate(type, target,
            typeof(SettingsDialog).GetMethod(name, Private)
            ?? throw new InvalidOperationException("SettingsDialog has no " + name));

    private static void Put(object target, string name, object? value) =>
        (typeof(SettingsDialog).GetField(name, Private | BindingFlags.Public)
            ?? throw new InvalidOperationException("SettingsDialog has no field " + name)).SetValue(target, value);

    private static void Invoke(object target, string name) =>
        (typeof(SettingsDialog).GetMethod(name, Private | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException("SettingsDialog has no " + name)).Invoke(target, null);
}
