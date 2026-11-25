using System;
using System.IO;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The startup reminder that no callsign is set (#1306). Ruled by Noel
/// 2026-11-24 13:43: a reminder with "don't show this again", about logging and
/// the other callsign features, not a transmit refusal.
/// </summary>
/// <remarks>
/// <para><b>Windowless.</b> MTA thread throughout. The decision is pure and is
/// run directly; the wiring around it (when it fires, the turn it takes, what
/// waits behind it) is read from source, because driving it would mean a real
/// main window. What a screen reader hears when it opens, and where focus lands
/// when it closes, is the harness's: a build under a throwaway settings folder
/// with an operator that has no callsign, walked with jjprobe.</para>
/// </remarks>
public sealed class NoCallsignReminderTests
{
    [Theory]
    [InlineData("", false, false, true)]          // no callsign: shown
    [InlineData("   ", false, false, true)]       // only spaces is no callsign
    [InlineData(null, false, false, true)]
    [InlineData("N0CALL", false, false, false)]   // a callsign: not shown
    [InlineData("", false, true, false)]          // silenced: not shown
    [InlineData("", true, false, false)]          // the launch that created the operator: not shown
    public void It_fires_only_with_no_callsign_not_silenced_and_not_on_the_first_run(
        string? callsign, bool createdThisLaunch, bool silenced, bool shown)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(shown, MainWindow.CallsignReminderApplies(callsign, createdThisLaunch, silenced));
    }

    [Fact]
    public void It_shows_whatever_the_callsign_check_says()
    {
        // About logging, not transmit: the switch is not one of its inputs.
        NoiseReductionPerChannelTests.AssertWindowless();
        string own = Src("JJFlexWpf", "MainWindow.CallsignReminder.cs");
        Assert.DoesNotContain("CallsignCheckOn", own, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireCallsignToTransmit", own, StringComparison.Ordinal);
    }

    [Fact]
    public void Its_dont_show_again_has_a_key_and_a_line_in_the_silenced_list()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        AdvisoryKey key = AdvisoryKeys.NoCallsignReminder;
        Assert.EndsWith("-v1", key.Value, StringComparison.Ordinal);
        Assert.Equal(Lexicon.Get("settings.silenced.no_callsign_reminder"), key.Label);
        Assert.Equal(key.Label, AdvisoryKeys.Describe(key.Value));
        // And the reminder is shown with it, so the box is offered.
        string own = Src("JJFlexWpf", "MainWindow.CallsignReminder.cs");
        Assert.Contains("AdvisoryKeys.NoCallsignReminder);", own, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_decided_at_the_welcome_before_the_focus_grab_and_parks_the_arrival_behind_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string main = Src("JJFlexWpf", "MainWindow.xaml.cs");
        string welcome = Body(main, "public void SpeakWelcome()");
        int decided = welcome.IndexOf("bool reminderFirst = TakeCallsignReminderTurnAtWelcome();", StringComparison.Ordinal);
        int grab = welcome.IndexOf("if (_advisorySequenceActive)", StringComparison.Ordinal);
        int parked = welcome.IndexOf("if (reminderFirst) _pendingArrival = Arrive;", StringComparison.Ordinal);
        Assert.True(decided > 0 && grab > decided && parked > grab,
            "the reminder must be decided before the focus grab, and the arrival parked behind it");
    }

    [Fact]
    public void In_a_connect_it_is_the_chains_last_advisory_and_the_chain_never_stacks_over_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string main = Src("JJFlexWpf", "MainWindow.xaml.cs");
        string chain = Body(main, "private async void RunStartupAdvisories()");
        int wait = chain.IndexOf("await WaitForCallsignReminderTurnAsync();", StringComparison.Ordinal);
        int active = chain.IndexOf("_advisorySequenceActive = true;", StringComparison.Ordinal);
        int firmware = chain.IndexOf("await SuggestFirmwareUpdateIfAvailableAsync();", StringComparison.Ordinal);
        int reminder = chain.IndexOf("await ShowCallsignReminderInChainAsync();", StringComparison.Ordinal);
        int replay = chain.IndexOf("ReplayParkedBringUp();", StringComparison.Ordinal);
        Assert.True(wait >= 0 && active > wait, "the chain waits for the reminder's own turn first");
        Assert.True(reminder > firmware && replay > reminder, "the reminder is the last advisory, before the parked speech");
    }

    [Fact]
    public void It_waits_for_our_dialogs_and_asks_again_at_the_moment_it_would_show()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string own = Src("JJFlexWpf", "MainWindow.CallsignReminder.cs");
        string show = Body(own, "private void ShowCallsignReminderIfDue()");
        Assert.Contains("if (!CallsignReminderAppliesNow())", show, StringComparison.Ordinal);
        string wait = Body(own, "private async Task WaitUntilNoDialogOfOursIsUpAsync()");
        Assert.Contains("WindowFocusForcer.AttentionWindowOpen", wait, StringComparison.Ordinal);
        Assert.Contains("AnotherOwnWindowHasForeground()", wait, StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_hands_over_the_operators_callsign_and_whether_this_launch_created_the_operator()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string g = Src("globals.vb");
        Assert.Contains("WpfMainWindow.OperatorCallsignSource = AddressOf currentOperatorCallsign", g, StringComparison.Ordinal);
        Assert.Contains("WpfMainWindow.OperatorCreatedThisLaunch = operatorCreatedThisLaunch", g, StringComparison.Ordinal);
        // Set inside the loop's "no default operator" branch.
        var loop = Regex.Match(g, @"While CurrentOp Is Nothing(?<body>.*?)End While", RegexOptions.Singleline);
        Assert.True(loop.Success);
        Assert.Contains("operatorCreatedThisLaunch = True", loop.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_first_run_creates_the_operator_inside_PersonalData_and_the_flag_starts_from_that()
    {
        // Repair round 3: on an empty Operators folder PersonalData's own load
        // opens the operator form, the new operator is the default when it
        // returns, and the loop above never runs. The bench saw the reminder
        // on two of two first runs while the loop pin stayed green.
        NoiseReductionPerChannelTests.AssertWindowless();
        string g = Src("globals.vb");
        const string init = "Dim operatorCreatedThisLaunch As Boolean = Operators.CreatedOperatorOnLoad";
        int load = g.IndexOf("Operators = New PersonalData(BaseConfigDir)", StringComparison.Ordinal);
        int flag = g.IndexOf(init, StringComparison.Ordinal);
        int loop = g.IndexOf("While CurrentOp Is Nothing", StringComparison.Ordinal);
        Assert.True(load >= 0 && flag > load && loop > flag,
            "the flag starts from what PersonalData's load did, after the load and before the default-operator loop");

        string p = Src("PersonalData.vb");
        string ctor = VbBlock(p, "Public Sub New(ByVal baseDir As String)", "End Sub");
        var firstRun = Regex.Match(ctor, @"If opFiles\.Length = 0 Then(?<body>.*?)Else", RegexOptions.Singleline);
        Assert.True(firstRun.Success, "the constructor's no-operator branch");
        Assert.Contains("createdOnLoad = (Add() >= 0)", firstRun.Groups["body"].Value, StringComparison.Ordinal);
        string prop = VbBlock(p, "Public ReadOnly Property CreatedOperatorOnLoad As Boolean", "End Property");
        Assert.Contains("Return createdOnLoad", prop, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string Src(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        string path = Path.Combine(d!.FullName, Path.Combine(parts));
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>The text of one VB member, from its signature to its first <paramref name="end"/>.</summary>
    private static string VbBlock(string src, string signature, string end)
    {
        int start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        int close = src.IndexOf("\n    " + end, start, StringComparison.Ordinal);
        Assert.True(close > start, signature + " has no " + end);
        return src.Substring(start, close - start);
    }

    /// <summary>The text of one method, from its signature to the next member at the same indent.</summary>
    private static string Body(string src, string signature)
    {
        int start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        int open = src.IndexOf("\n    {", start, StringComparison.Ordinal);
        int close = src.IndexOf("\n    }\n", open, StringComparison.Ordinal);
        Assert.True(open > start && close > open, signature + " has no body");
        return src.Substring(start, close - start);
    }
}
