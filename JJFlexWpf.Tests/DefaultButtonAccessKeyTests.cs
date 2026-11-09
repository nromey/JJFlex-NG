using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// No control character in the access key a screen reader is given for a
/// Cancel or default button (#1023 item 3: NVDA read
/// <c>No, cancel | button | \x1b</c> on the confirmation dialog).
/// </summary>
/// <remarks>
/// <para><b>What is asserted is what the automation PEER reports</b>, the
/// value a screen reader is handed, not the attached property: the framework
/// reports a registered key only while the property is empty, so a test of
/// the property alone would pass a fix that changes nothing heard.</para>
/// <para><b>Desk safety.</b> Each WPF case builds a lone <see cref="Button"/>
/// on a private STA thread: no window, no presentation source, nothing on the
/// desktop. The test thread itself is asserted MTA. <see cref="QuietRun"/> is
/// asserted silenced.</para>
/// </remarks>
public sealed class DefaultButtonAccessKeyTests
{
    private static string Reported(Button b) => UIElementAutomationPeer.CreatePeerForElement(b).GetAccessKey() ?? "";

    private static bool HasControlCharacter(string s) => s.Any(char.IsControl);

    [Fact]
    public void Before_the_repair_a_Cancel_button_reports_the_Escape_character()
    {
        // Positive control: the defect is reachable here, windowless.
        OnSta(() =>
        {
            var b = new Button { Content = "No, cancel", IsCancel = true };
            Assert.Contains('\x1b', Reported(b));
            var d = new Button { Content = "Yes", IsDefault = true };
            Assert.Contains('\r', Reported(d));
        });
    }

    [Fact]
    public void After_the_repair_no_Cancel_or_default_button_reports_a_control_character()
    {
        OnSta(() =>
        {
            foreach (var b in new[]
                     {
                         new Button { Content = "No, cancel", IsCancel = true },
                         new Button { Content = "Yes", IsDefault = true },
                         new Button { Content = "Close", IsCancel = true, IsDefault = true },
                         new Button { Content = "_Cancel", IsCancel = true },
                     })
            {
                DefaultButtonAccessKey.Repair(b);
                Assert.False(HasControlCharacter(Reported(b)), b.Content + " reports " + Escape(Reported(b)));
                // Escape and Enter still reach them: nothing was cleared.
                Assert.True(b.IsCancel || b.IsDefault);
            }
        });
    }

    [Fact]
    public void A_button_that_names_its_own_access_key_is_left_alone()
    {
        OnSta(() =>
        {
            var about = new Button { Content = "Close", IsCancel = true, IsDefault = true };
            AutomationProperties.SetAccessKey(about, "Alt+L");
            DefaultButtonAccessKey.Repair(about);
            Assert.Equal("Alt+L", AutomationProperties.GetAccessKey(about));
            Assert.False(HasControlCharacter(Reported(about)));
            // The framework fact the repair rests on, pinned so the day it
            // changes is noticed: with the property set, the peer reports
            // NOTHING, not the property's own value. So "Escape" and "Enter"
            // are not heard today, and neither is this dialog's "Alt+L".
            Assert.Equal("", Reported(about));

            var plain = new Button { Content = "Apply" };
            DefaultButtonAccessKey.Repair(plain);
            Assert.Equal("", AutomationProperties.GetAccessKey(plain));
        });
    }

    [Fact]
    public void The_rule_is_Cancel_then_default_and_never_over_an_own_key()
    {
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        Assert.Equal("Escape", DefaultButtonAccessKey.AccessKeyFor(isCancel: true, isDefault: false, ownAccessKey: null));
        Assert.Equal("Enter", DefaultButtonAccessKey.AccessKeyFor(isCancel: false, isDefault: true, ownAccessKey: ""));
        Assert.Equal("Escape", DefaultButtonAccessKey.AccessKeyFor(isCancel: true, isDefault: true, ownAccessKey: null));
        Assert.Null(DefaultButtonAccessKey.AccessKeyFor(isCancel: true, isDefault: true, ownAccessKey: "Alt+L"));
        Assert.Null(DefaultButtonAccessKey.AccessKeyFor(isCancel: false, isDefault: false, ownAccessKey: null));
    }

    // ---- repair one: through the installed handler, and after load ----

    /// <summary>The rule as the application installs it: JJFlexDialog's own
    /// static constructor, then a real Loaded event on the button.</summary>
    private static void InstallAndLoad(params Button[] buttons)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(JJFlexDialog).TypeHandle);
        foreach (var b in buttons) b.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent, b));
    }

    [Fact]
    public void A_button_that_becomes_default_or_Cancel_after_it_loaded_reports_no_control_character()
    {
        OnSta(() =>
        {
            // Positive control: a button the handler never saw reports the
            // carriage return when it becomes default.
            var unseen = new Button { Content = "Add" };
            unseen.IsDefault = true;
            Assert.Contains('\r', Reported(unseen));

            var add = new Button { Content = "Add" };
            InstallAndLoad(add);
            Assert.Equal("", Reported(add));

            add.IsDefault = true;
            Assert.False(HasControlCharacter(Reported(add)), "default after load reports " + Escape(Reported(add)));
            Assert.True(add.IsDefault);

            // No longer default: the rule's own value is taken back, so a
            // stale "Enter" is not left on a button that no longer has it.
            add.IsDefault = false;
            Assert.Equal("", AutomationProperties.GetAccessKey(add));

            add.IsCancel = true;
            Assert.False(HasControlCharacter(Reported(add)), "Cancel after load reports " + Escape(Reported(add)));
            Assert.True(add.IsCancel);
        });
    }

    [Fact]
    public void A_button_whose_own_access_key_is_set_keeps_it_through_flag_changes()
    {
        OnSta(() =>
        {
            var about = new Button { Content = "Close" };
            AutomationProperties.SetAccessKey(about, "Alt+L");
            InstallAndLoad(about);
            about.IsDefault = true;
            about.IsCancel = true;
            about.IsDefault = false;
            about.IsCancel = false;
            Assert.Equal("Alt+L", AutomationProperties.GetAccessKey(about));
        });
    }

    [Fact]
    public void Messages_moving_default_from_Update_to_Add_leaves_no_control_character_on_either()
    {
        OnSta(() =>
        {
            // Codex's probe, windowless: the real dialog type, uninitialized
            // (no Window constructor runs), with unhosted controls, and its
            // real selection handler.
            var dialog = (JJFlexWpf.Dialogs.CWMessageUpdateDialog)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(JJFlexWpf.Dialogs.CWMessageUpdateDialog));
            dialog.KeysList = new ListBox();
            dialog.AddButton = new Button { Content = "Add" };
            dialog.UpdateButton = new Button { Content = "Update" };
            dialog.DeleteButton = new Button { Content = "Delete" };
            var empty = typeof(JJFlexWpf.Dialogs.CWMessageUpdateDialog).GetField("_listIsEmpty",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var selectionChanged = typeof(JJFlexWpf.Dialogs.CWMessageUpdateDialog).GetMethod("KeysList_SelectionChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            // One message: Update is default when the buttons load.
            empty.SetValue(dialog, false);
            dialog.KeysList.ItemsSource = new[] { "CQ" };
            dialog.KeysList.SelectedIndex = 0;
            selectionChanged.Invoke(dialog, new object?[] { null, null });
            InstallAndLoad(dialog.AddButton, dialog.UpdateButton);
            Assert.True(dialog.UpdateButton.IsDefault);
            Assert.False(HasControlCharacter(Reported(dialog.UpdateButton)));

            // The last message deleted: Add becomes default after it loaded.
            empty.SetValue(dialog, true);
            dialog.KeysList.ItemsSource = new[] { "No messages yet" };
            dialog.KeysList.SelectedIndex = 0;
            selectionChanged.Invoke(dialog, new object?[] { null, null });
            Assert.True(dialog.AddButton.IsDefault);
            Assert.False(HasControlCharacter(Reported(dialog.AddButton)), "Add reports " + Escape(Reported(dialog.AddButton)));
            Assert.False(dialog.UpdateButton.IsDefault);
            Assert.Equal("", AutomationProperties.GetAccessKey(dialog.UpdateButton));

            // And back: the first message added makes Update default again.
            empty.SetValue(dialog, false);
            dialog.KeysList.ItemsSource = new[] { "CQ" };
            dialog.KeysList.SelectedIndex = 0;
            selectionChanged.Invoke(dialog, new object?[] { null, null });
            Assert.False(HasControlCharacter(Reported(dialog.UpdateButton)));
            Assert.False(HasControlCharacter(Reported(dialog.AddButton)));
        });
    }

    // ---- repair two: the real Loaded broadcast, not a hand-raised Loaded ----

    /// <summary>
    /// WPF's own Loaded broadcast, the walk a realized window runs
    /// (BroadcastEventHelper.BroadcastLoadedSynchronously, internal, reached
    /// by reflection), run on an unhosted tree: it decides who is handed
    /// Loaded exactly as in a window, without one.
    /// </summary>
    private static void BroadcastLoaded(System.Windows.DependencyObject root)
    {
        var helper = typeof(System.Windows.FrameworkElement).Assembly.GetType("System.Windows.BroadcastEventHelper", throwOnError: true)!;
        var broadcast = helper.GetMethod("BroadcastLoadedSynchronously",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        broadcast.Invoke(null, new object[] { root, false });
    }

    [Fact]
    public void The_real_Loaded_broadcast_never_hands_Loaded_to_a_button_with_no_handler_of_its_own()
    {
        // Positive control for the harness's finding: with the class handler
        // installed and the root's own Loaded handler firing, a plain default
        // button is never visited, so the class handler never repairs it.
        OnSta(() =>
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(JJFlexDialog).TypeHandle);
            var done = new Button { Content = "Done", IsDefault = true };
            var root = new StackPanel();
            root.Children.Add(done);
            bool rootLoaded = false;
            root.Loaded += (_, _) => rootLoaded = true;
            BroadcastLoaded(root);
            Assert.True(rootLoaded, "the broadcast did not run");
            Assert.Contains('\r', Reported(done));
        });
    }

    [Fact]
    public void A_watched_root_repairs_every_button_under_it_through_the_real_broadcast_and_keeps_watching()
    {
        OnSta(() =>
        {
            var update = new Button { Content = "Update", IsDefault = true };
            var add = new Button { Content = "Add" };
            var close = new Button { Content = "Close", IsCancel = true };
            var nested = new Border { Child = close };
            var root = new StackPanel();
            root.Children.Add(update);
            root.Children.Add(add);
            root.Children.Add(nested);
            DefaultButtonAccessKey.WatchTree(root);
            BroadcastLoaded(root);

            Assert.False(HasControlCharacter(Reported(update)), "Update reports " + Escape(Reported(update)));
            Assert.False(HasControlCharacter(Reported(close)), "Close reports " + Escape(Reported(close)));
            Assert.Equal("", Reported(add));

            // Messages emptied: the default moves to Add after load.
            update.IsDefault = false;
            add.IsDefault = true;
            Assert.False(HasControlCharacter(Reported(add)), "Add reports " + Escape(Reported(add)));
            Assert.Equal("", AutomationProperties.GetAccessKey(update));
            Assert.True(add.IsDefault && close.IsCancel);
        });
    }

    [Fact]
    public void Every_dialog_and_the_station_lookup_window_watch_their_own_tree()
    {
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        string root = RepoRoot();
        string dialog = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "JJFlexDialog.cs"));
        string ctor = dialog.Substring(dialog.IndexOf("public JJFlexDialog()", StringComparison.Ordinal));
        ctor = ctor.Substring(0, ctor.IndexOf("\n        }", StringComparison.Ordinal) + 1);
        Assert.Contains("DefaultButtonAccessKey.WatchTree(this);", ctor, StringComparison.Ordinal);
        string lookup = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "StationLookupWindow.xaml.cs"));
        Assert.Contains("DefaultButtonAccessKey.WatchTree(this);", lookup, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_dialog_installs_the_rule_before_its_first_button_loads()
    {
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        string root = RepoRoot();
        string dialog = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "JJFlexDialog.cs"));
        Assert.Contains("static JJFlexDialog() => DefaultButtonAccessKey.Install();", dialog, StringComparison.Ordinal);
        string rule = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "DefaultButtonAccessKey.cs"));
        Assert.Contains("EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent,", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCancel =", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDefault =", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("Unregister", rule, StringComparison.Ordinal);
    }

    private static string Escape(string s) => string.Concat(s.Select(c => char.IsControl(c) ? "\\x" + ((int)c).ToString("x2") : c.ToString()));

    private static void OnSta(Action body)
    {
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The button test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
