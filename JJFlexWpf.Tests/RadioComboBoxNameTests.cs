using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Home's Operating Mode combo is read by its name. The name was set on the
/// <see cref="RadioComboBox"/> UserControl, but a screen reader lands on the
/// combo inside it, which took a name only from a Header nobody set, so NVDA
/// said "combo box, USB, collapsed". The inner combo now takes the control's
/// own name when no Header names it; a Header still wins.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> Each case builds a lone <see cref="RadioComboBox"/> on a
/// private STA thread: no window, no presentation source, nothing on the
/// desktop. The test thread itself is asserted MTA, and <see cref="QuietRun"/>
/// is asserted silenced.
/// </remarks>
public sealed class RadioComboBoxNameTests
{
    private static string InnerName(RadioComboBox box) =>
        UIElementAutomationPeer.CreatePeerForElement(box.TheComboBox).GetName() ?? "";

    [Fact]
    public void With_no_name_and_no_header_the_inner_combo_is_unnamed()
    {
        // Positive control: the peer read below is the one that was empty.
        OnSta(() =>
        {
            var box = new RadioComboBox();
            Assert.Equal("", InnerName(box));
        });
    }

    [Fact]
    public void The_inner_combo_takes_the_controls_own_name()
    {
        OnSta(() =>
        {
            var box = new RadioComboBox();
            AutomationProperties.SetName(box, "Operating Mode");   // as MainWindow.xaml sets it
            Assert.Equal("Operating Mode", InnerName(box));
            Assert.Null(box.Parent);   // unhosted: nothing on the desk
        });
    }

    [Fact]
    public void A_header_still_wins_whichever_is_set_first()
    {
        OnSta(() =>
        {
            var nameFirst = new RadioComboBox();
            AutomationProperties.SetName(nameFirst, "Operating Mode");
            nameFirst.Header = "Mode";
            Assert.Equal("Mode", InnerName(nameFirst));

            var headerFirst = new RadioComboBox();
            headerFirst.Header = "Mode";
            AutomationProperties.SetName(headerFirst, "Operating Mode");
            Assert.Equal("Mode", InnerName(headerFirst));
        });
    }

    [Fact]
    public void An_empty_header_leaves_the_controls_own_name()
    {
        OnSta(() =>
        {
            var box = new RadioComboBox();
            AutomationProperties.SetName(box, "Transmit Tune");
            box.Header = "";
            Assert.Equal("Transmit Tune", InnerName(box));
        });
    }

    /// <summary>The premise, pinned: Home names both combos on the control and sets no Header.</summary>
    [Fact]
    public void Home_names_its_combos_on_the_control_and_sets_no_header()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml"));
        Assert.Matches(new Regex(@"RadioComboBox x:Name=""ModeControl""[^>]*AutomationProperties\.Name=""Operating Mode"""), xaml);
        Assert.Matches(new Regex(@"RadioComboBox x:Name=""TXTuneControl""[^>]*AutomationProperties\.Name=""Transmit Tune"""), xaml);
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        Assert.DoesNotMatch(new Regex(@"(ModeControl|TXTuneControl)\.Header\s*="), code);
    }

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The combo test did not finish.");
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
