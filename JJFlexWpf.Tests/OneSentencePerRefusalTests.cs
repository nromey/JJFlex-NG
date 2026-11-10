using System;
using System.IO;
using System.Linq;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// One sentence per refused or failed press (#486, and #1022's leftovers):
/// a refused firmware send over SmartLink is not followed by an interrupting
/// "Could not send the firmware"; a genuine port forwarding failure is said
/// once; the Full duplex menu item's refusal is said after the menu closes.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window: a static function and source reads only,
/// on an MTA thread, asserted in every test.
/// </remarks>
public sealed class OneSentencePerRefusalTests
{
    public OneSentencePerRefusalTests() => Lexicon.Load(Lexicon.Partitions);

    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    // ---- (b) the firmware send refused at the write over SmartLink ----

    [Fact]
    public void A_send_refused_over_SmartLink_puts_the_reason_on_the_line_and_speaks_nothing_more()
    {
        AssertWindowless();
        var (line, spoken) = SettingsDialog.FirmwareSendNotStarted(overSmartLink: true);
        Assert.Equal(Lexicon.Get("settings.radio.firmware.smartlink_cannot_carry"), line);
        Assert.Null(spoken);
    }

    [Fact]
    public void A_send_that_failed_on_the_local_network_still_says_so()
    {
        AssertWindowless();
        var (line, spoken) = SettingsDialog.FirmwareSendNotStarted(overSmartLink: false);
        Assert.Equal(Lexicon.Get("settings.radio.firmware.send_failed"), line);
        Assert.Equal(Lexicon.Get("settings.radio.firmware.send_failed_spoken"), spoken);
    }

    [Fact]
    public void The_dialog_asks_whether_the_connection_is_SmartLink_after_a_send_that_did_not_start()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs");
        int refusedArm = src.IndexOf("else if (started == GuardedOutcome.Refused)", StringComparison.Ordinal);
        Assert.True(refusedArm > 0);
        int other = src.IndexOf("FirmwareSendNotStarted(_rig.IsWanConnection)", refusedArm, StringComparison.Ordinal);
        Assert.True(other > refusedArm, "the arm after the hold's refusal decides by the connection");
        // Built in pieces so the lexicon scanner does not read a key here.
        Assert.DoesNotContain("ScreenReaderOutput.Speak(Lexicon" + ".Get(\"settings.radio.firmware.send_failed" + "_spoken\")", src, StringComparison.Ordinal);
    }

    // ---- (c) a genuine port forwarding failure ----

    [Fact]
    public void A_port_forwarding_failure_is_said_once_by_the_commit()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
        int at = src.IndexOf("private GuardedOutcome PerformPortForwardApply(", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = src.IndexOf("return outcome;", at, StringComparison.Ordinal);
        string body = src.Substring(at, end - at);
        int failureArm = body.LastIndexOf("else", StringComparison.Ordinal);
        Assert.DoesNotContain("Speak(", body.Substring(failureArm), StringComparison.Ordinal);
        Assert.DoesNotContain("settings.network.command_failed_spoken", body, StringComparison.Ordinal);
        // The commit's own note is what carries it.
        Assert.Contains("declineNote = Lexicon" + ".Get(\"settings.network.forwarding_command" + "_failed\");", src, StringComparison.Ordinal);
        Assert.Contains("queued.Add(declineNote);", src, StringComparison.Ordinal);
    }

    // ---- (d) the Full duplex menu item ----

    [Fact]
    public void The_full_duplex_menu_item_says_the_holds_refusal_after_the_menu_closes()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "NativeMenuBar.cs");
        int row = src.IndexOf("Radios.Lexicon" + ".Get(\"audio.full_duplex" + ".label\")", StringComparison.Ordinal);
        Assert.True(row > 0);
        int toggle = src.IndexOf("_ = FullDuplexControl.ToggleAsync(Rig);", row, StringComparison.Ordinal);
        string before = src.Substring(row, toggle - row);
        Assert.Contains("if (Rig?.ChangeNothingActive == true)", before, StringComparison.Ordinal);
        Assert.Contains("SpeakAfterMenuClose(Radios.Lexicon.Get(\"settings.guard.refused\"", before, StringComparison.Ordinal);
        Assert.Contains("\"settings.guard.action.full_duplex\"", before, StringComparison.Ordinal);
        Assert.Contains("ChangeNothing: refused settings.guard.action.full_duplex", before, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chord_keeps_its_own_path()
    {
        AssertWindowless();
        // Ctrl+J, Ctrl+X goes straight to the toggle, whose hold refusal is
        // heard from there (the harness passed it).
        string flex = Source("Radios", "FlexBase.FullDuplex.cs");
        Assert.Contains("if (GuardRefuses(\"settings.guard.action.full_duplex\")) return null;", flex, StringComparison.Ordinal);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}
