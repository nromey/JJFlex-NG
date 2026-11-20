#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The Audio Devices picker names the window that opened it, in its trace
/// line, from the window holding the keyboard (#1210). It asked the WPF
/// application object before, and this app has none, so the line always said
/// "no active window" even when a WPF dialog had opened it.
/// </summary>
/// <remarks>
/// Source test: the picker is a window and cannot be built here.
/// </remarks>
public sealed class AudioDevicesOpenerTests
{
    private static string Code(params string[] path)
    {
        string text = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), Path.Combine(path))).Replace("\r\n", "\n");
        // Comments may name the old route; only code counts.
        return Regex.Replace(text, @"//[^\n]*", "");
    }

    [Fact]
    public void The_picker_names_its_opener_from_the_window_holding_the_keyboard()
    {
        string dialog = Code("JJFlexWpf", "Dialogs", "AudioDevicesDialog.xaml.cs");
        Assert.DoesNotContain("Application.Current", dialog, StringComparison.Ordinal);
        Assert.Contains("?? WindowHoldingTheKeyboard()?.Title", dialog, StringComparison.Ordinal);
        Assert.Contains("Window.GetWindow(focused)", dialog, StringComparison.Ordinal);
        // It names a window; it does not start owning the picker from one.
        Assert.Single(Regex.Matches(dialog, @"dlg\.Owner\s*="));
        Assert.Contains("if (owner != null) dlg.Owner = owner;", dialog, StringComparison.Ordinal);
    }

    /// <summary>Positive control: the same scan finds the one deliberate use, in HostedDispatcher.</summary>
    [Fact]
    public void The_scan_sees_a_real_use()
    {
        Assert.Contains("Application.Current", Code("JJFlexWpf", "HostedDispatcher.cs"), StringComparison.Ordinal);
    }
}
