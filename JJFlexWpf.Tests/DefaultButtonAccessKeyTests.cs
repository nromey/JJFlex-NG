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
