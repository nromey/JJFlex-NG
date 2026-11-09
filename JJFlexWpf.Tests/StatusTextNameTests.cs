using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A status line's accessible name follows its text (#1022 item 5, and the
/// class): the Radio profile tab's status line was named "Radio profile
/// status", so a screen reader on it never read the warning it held.
/// </summary>
/// <remarks>
/// <para><b>Desk safety.</b> Pure functions and source only: no window, no
/// element is built, and every test asserts it runs on an MTA thread, where WPF
/// cannot build one. The realized check (focus the line, read its name) is the
/// harness's: <c>DialogCatalog</c> builds real windows and is not run here.</para>
/// <para><b>The sweep is a source scan</b>, the same one that found the class:
/// a named <c>TextBlock</c> in tracked XAML that carries a fixed
/// <c>AutomationProperties.Name</c> while code beside it sets its
/// <c>.Text</c>. Every hit is either moved to
/// <c>local:StatusTextName.Label</c> or named below with why it waits.</para>
/// </remarks>
public sealed class StatusTextNameTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    // ---- the rule ----

    [Fact]
    public void The_name_is_the_text_whenever_there_is_text()
    {
        AssertWindowless();
        const string warning = "Change nothing is on for this radio. Nothing you do here is sent to it.";
        Assert.Equal(warning, StatusTextName.NameFor("Radio profile status", warning));
        Assert.Equal("Ready", StatusTextName.NameFor("Test status", "  Ready  "));
    }

    [Fact]
    public void An_empty_line_keeps_its_label()
    {
        AssertWindowless();
        Assert.Equal("Radio profile status", StatusTextName.NameFor("Radio profile status", ""));
        Assert.Equal("Radio profile status", StatusTextName.NameFor("Radio profile status", null));
        Assert.Equal("Radio profile status", StatusTextName.NameFor("Radio profile status", "   "));
        Assert.Equal("", StatusTextName.NameFor(null, null));
    }

    [Fact]
    public void A_focused_line_is_not_renamed_until_focus_leaves()
    {
        AssertWindowless();
        Assert.False(StatusTextName.RenamesNow(hasKeyboardFocus: true));
        Assert.True(StatusTextName.RenamesNow(hasKeyboardFocus: false));
        string src = Read("JJFlexWpf", "StatusTextName.cs");
        Assert.Contains("if (sender is TextBlock line && RenamesNow(line.IsKeyboardFocused)) Apply(line);", src, StringComparison.Ordinal);
        Assert.Contains("line.LostKeyboardFocus += OnLostKeyboardFocus;", src, StringComparison.Ordinal);
    }

    [Fact]
    public void The_radio_profile_status_line_follows_its_text()
    {
        AssertWindowless();
        string xaml = Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml");
        string el = Element(xaml, "RadioProfileStatusText");
        Assert.Contains("local:StatusTextName.Label=\"Radio profile status\"", el, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=", el, StringComparison.Ordinal);
    }

    // ---- the behaviour, on a real line (repair one) ----

    [Fact]
    public void A_loaded_lines_name_follows_its_text_and_falls_back_to_the_label_while_empty()
    {
        // The checker found the seven tests above stayed green with both
        // text-change subscriptions removed. This one drives the attached
        // property on a lone TextBlock on a private STA thread: no window,
        // no presentation source. Loaded and Unloaded are raised by hand.
        OnSta(() =>
        {
            var line = new System.Windows.Controls.TextBlock();
            StatusTextName.SetLabel(line, "Radio profile status");
            Assert.Equal("Radio profile status", System.Windows.Automation.AutomationProperties.GetName(line));
            line.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent, line));
            try
            {
                const string warning = "Change nothing is on for this radio. Nothing you do here is sent to it.";
                line.Text = warning;
                Assert.Equal(warning, System.Windows.Automation.AutomationProperties.GetName(line));
                line.Text = "Ready";
                Assert.Equal("Ready", System.Windows.Automation.AutomationProperties.GetName(line));
                line.Text = "";
                Assert.Equal("Radio profile status", System.Windows.Automation.AutomationProperties.GetName(line));
            }
            finally
            {
                // Let the descriptor go, as a closed dialog's line does.
                line.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.UnloadedEvent, line));
            }
        });
    }

    private static void OnSta(Action body)
    {
        AssertWindowless();
        Assert.True(JJFlexWpf.Tests.Infrastructure.QuietRun.Silenced, "Refusing: " + JJFlexWpf.Tests.Infrastructure.QuietRun.Describe());
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The line test did not finish.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // ---- the class ----

    /// <summary>
    /// Waiting, with the reason. SettingsDialog.xaml is edited by Sprint 49
    /// and the recordings work; only the Radio profile line was ours to
    /// touch. The label and value pairs need a decision this batch did not
    /// have: their fixed name is a field's label ("Frequency", "Source log
    /// file"), so the right name carries both, not the value alone.
    /// </summary>
    private static readonly HashSet<string> Waiting = new(StringComparer.Ordinal)
    {
        // SettingsDialog.xaml, after Sprint 49 and the recordings work merge.
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:ReachabilityStatusText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupConnectStatus",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupRegisterStatus",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupFirmwareStatus",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupFirmwareProgressText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupFirmwareFileText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupAddressStatus",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupWayInStatus",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupCheckStatus",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SetupRestartStatus",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:SilencedCountText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:UpdateLastCheckText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:DiagStatusText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:DiagProblemCountText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:DiagPathText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:DiagStorageText",
        "JJFlexWpf/Dialogs/SettingsDialog.xaml:DiagCrashText",
        // Label and value pairs: a design decision, not built.
        "JJFlexWpf/Dialogs/AutoConnectSettingsDialog.xaml:RadioNameText",
        "JJFlexWpf/Dialogs/ConnectionTesterDialog.xaml:RadioInfoText",
        "JJFlexWpf/Dialogs/ExportDialog.xaml:FromName",
        "JJFlexWpf/Dialogs/ExportDialog.xaml:ToName",
        "JJFlexWpf/Dialogs/ImportDialog.xaml:FromName",
        "JJFlexWpf/Dialogs/ImportDialog.xaml:ToName",
        "JJFlexWpf/LogEntryControl.xaml:DupLabel",
        "JJFlexWpf/LogEntryControl.xaml:FreqLabel",
        "JJFlexWpf/LogEntryControl.xaml:ModeLabel",
        "JJFlexWpf/LogEntryControl.xaml:BandLabel",
        "JJFlexWpf/LogEntryControl.xaml:DateTimeLabel",
        "JJFlexWpf/LogEntryControl.xaml:SerialLabel",
        "JJFlexWpf/MainWindow.xaml:PanLowFreq",
        "JJFlexWpf/MainWindow.xaml:PanHighFreq",
    };

    [Fact]
    public void No_status_line_hides_its_text_behind_a_fixed_name()
    {
        AssertWindowless();
        var hits = FixedNamesOverCodeSetText(TrackedXaml());
        Assert.True(hits.Count >= Waiting.Count, "the scan found " + hits.Count + " hits; it is not reading the tree");
        var unexplained = hits.Where(h => !Waiting.Contains(h)).ToList();
        Assert.True(unexplained.Count == 0,
            "a text line with a fixed accessible name whose text is set in code; use local:StatusTextName.Label "
            + "instead, or name it in Waiting with why: " + string.Join(", ", unexplained));
    }

    [Fact]
    public void Nothing_waits_that_has_already_been_moved_or_gone()
    {
        AssertWindowless();
        var hits = FixedNamesOverCodeSetText(TrackedXaml()).ToHashSet(StringComparer.Ordinal);
        var stale = Waiting.Where(w => !hits.Contains(w)).ToList();
        Assert.True(stale.Count == 0, "named as waiting but no longer a hit; take it off the list: " + string.Join(", ", stale));
    }

    [Fact]
    public void The_scan_sees_a_planted_fixed_name()
    {
        AssertWindowless();
        // Positive control: the shape the Radio profile line had before.
        var planted = new List<(string File, string Xaml, string CodeBehind)>
        {
            ("JJFlexWpf/Dialogs/Planted.xaml",
             "<StackPanel><TextBlock x:Name=\"PlantedStatus\" Focusable=\"True\" AutomationProperties.Name=\"Planted status\"/></StackPanel>",
             "PlantedStatus.Text = warning;"),
            ("JJFlexWpf/Dialogs/Moved.xaml",
             "<StackPanel><TextBlock x:Name=\"MovedStatus\" local:StatusTextName.Label=\"Moved status\"/></StackPanel>",
             "MovedStatus.Text = warning;"),
        };
        var hits = FixedNamesOverCodeSetText(planted);
        Assert.Equal(new[] { "JJFlexWpf/Dialogs/Planted.xaml:PlantedStatus" }, hits);
    }

    // ---- the scan ----

    private static readonly Regex TextBlockElement = new(@"<TextBlock\b[^>]*?/?>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex XName = new("x:Name=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex FixedName = new("AutomationProperties\\.Name=\"[^\"{][^\"]*\"", RegexOptions.Compiled);

    private static List<string> FixedNamesOverCodeSetText(IEnumerable<(string File, string Xaml, string CodeBehind)> files)
    {
        var hits = new List<string>();
        foreach (var (file, xaml, code) in files)
        {
            foreach (Match el in TextBlockElement.Matches(xaml))
            {
                var name = XName.Match(el.Value);
                if (!name.Success || !FixedName.IsMatch(el.Value)) continue;
                if (Regex.IsMatch(code, @"\b" + Regex.Escape(name.Groups[1].Value) + @"\.Text\s*="))
                    hits.Add(file + ":" + name.Groups[1].Value);
            }
        }
        return hits;
    }

    private static IEnumerable<(string File, string Xaml, string CodeBehind)> TrackedXaml()
    {
        string root = RepoRoot();
        var psi = new ProcessStartInfo("git", "-C \"" + root + "\" ls-files -z -- JJFlexWpf")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var git = Process.Start(psi)!;
        string listing = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        var tracked = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(tracked.Length > 300, "git listed only " + tracked.Length + " files under JJFlexWpf");
        foreach (string rel in tracked.Where(f => f.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            string dir = Path.GetDirectoryName(rel)!.Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(rel);
            string code = string.Concat(tracked
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal)
                            && Path.GetDirectoryName(f)!.Replace('\\', '/') == dir
                            && Path.GetFileName(f).StartsWith(stem, StringComparison.Ordinal))
                .Select(f => File.ReadAllText(Path.Combine(root, f))));
            yield return (rel, File.ReadAllText(Path.Combine(root, rel)), code);
        }
    }

    private static string Element(string xaml, string name)
    {
        foreach (Match el in TextBlockElement.Matches(xaml))
            if (el.Value.Contains("x:Name=\"" + name + "\"", StringComparison.Ordinal)) return el.Value;
        throw new Xunit.Sdk.XunitException("no TextBlock named " + name);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
