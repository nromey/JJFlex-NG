using System;
using System.IO;
using System.Threading.Tasks;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// One sentence per refused press: when the change-nothing hold refuses an
/// operator write it has already said so, naming the setting, and the caller
/// says nothing more (#486, and the 2026-11-08 harness findings on full
/// duplex and port forwarding).
/// </summary>
/// <remarks>
/// <para>Full duplex said the refusal and then "Full duplex was not changed"
/// 2 ms later, interrupting it; port forwarding said "The port forwarding
/// command failed" after a correct refusal, which was untrue. The writers now
/// return an outcome a caller can tell apart, and these pin both halves: the
/// rig returns Refused (or no key) under the hold, and every caller is silent
/// on it.</para>
/// <para>The behaviour half runs the real writers on a FlexBase over the
/// vendor Radio and its fake command transport, as
/// <see cref="OperatorHoldWriteTests"/> does: no radio, no network, no
/// window. The callers live in window classes, so they are pinned from
/// source.</para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class RefusalSaidOnceTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(RefusalSaidOnceTests));
    public void Dispose() => _scope.Dispose();

    // ---- the rig: an outcome the caller can tell apart ----

    [Fact]
    public async Task Full_duplex_under_the_hold_returns_no_second_sentence_and_sends_nothing()
    {
        using var c = new RadioCase();
        c.F.Rig.SuppressSpeech = true;
        c.F.Rig.WireStationHandlers(c.F.Vendor.Radio);
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(100);
        int sent = c.F.Vendor.Transport.Commands.Count;

        c.F.Rig.SetChangeNothingActive(true);
        Assert.Null(await c.F.Rig.ToggleFullDuplexAsync());
        Assert.Equal(sent, c.F.Vendor.Transport.Commands.Count);

        // Control: the same press with the hold off still says its sentence.
        c.F.Rig.SetChangeNothingActive(false);
        Assert.Equal("audio.full_duplex.on", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
    }

    [Fact]
    public void A_mic_profile_load_under_the_hold_is_refused_not_missing()
    {
        using var c = new RadioCase();
        c.F.Rig.SuppressSpeech = true;
        c.F.Vendor.Status("profile mic list=Default^Ragchew^");
        Assert.Contains("Ragchew", c.F.Rig.MicProfileNames);

        c.F.Rig.SetChangeNothingActive(true);
        Assert.Equal(GuardedOutcome.Refused, c.F.Rig.SelectMicProfileIfPresent("Ragchew"));
        Assert.Equal(0, c.F.Rig.q.Count);

        // Controls: a name the radio does not list is Skipped, hold or not,
        // and the listed one with the hold off is queued.
        Assert.Equal(GuardedOutcome.Skipped, c.F.Rig.SelectMicProfileIfPresent("Not on this radio"));
        c.F.Rig.SetChangeNothingActive(false);
        Assert.Equal(GuardedOutcome.Skipped, c.F.Rig.SelectMicProfileIfPresent("Not on this radio"));
        Assert.Equal(GuardedOutcome.Done, c.F.Rig.SelectMicProfileIfPresent("Ragchew"));
        Assert.Equal(1, c.F.Rig.q.Count);
    }

    [Fact]
    public void A_saved_mic_setup_under_the_hold_says_its_radio_half_was_refused_and_nothing_else()
    {
        // Repair one: the Workshop's saved setup told the refusal apart from
        // nothing, so it said "Microphone setup Ragchew applied." over a
        // refused radio half.
        using var c = new RadioCase();
        c.F.Rig.SuppressSpeech = true;
        c.F.Vendor.Status("profile mic list=Default^Ragchew^");
        var profile = new MicrophoneProfile { Name = "Desk mic" };
        profile.SetSetupFor(new RadioProfileReference { RadioId = "TEST-SERIAL", ProfileName = "Ragchew" });

        c.F.Rig.SetChangeNothingActive(true);
        var held = profile.ApplyRadioHalf(c.F.Rig, "TEST-SERIAL");
        Assert.True(held.HoldRefused);
        Assert.False(held.RadioHalfApplied);
        Assert.Equal("", held.Message);
        Assert.Equal(0, c.F.Rig.q.Count);

        // Control: the hold off, the same setup loads, and is not a refusal.
        c.F.Rig.SetChangeNothingActive(false);
        var loaded = profile.ApplyRadioHalf(c.F.Rig, "TEST-SERIAL");
        Assert.False(loaded.HoldRefused);
        Assert.True(loaded.RadioHalfApplied);
        // And a profile the radio does not list is "absent", not a refusal.
        profile.SetSetupFor(new RadioProfileReference { RadioId = "TEST-SERIAL", ProfileName = "Not on this radio" });
        c.F.Rig.SetChangeNothingActive(true);
        Assert.False(profile.ApplyRadioHalf(c.F.Rig, "TEST-SERIAL").HoldRefused);
    }

    [Fact]
    public void A_firmware_send_under_the_hold_is_refused()
    {
        using var c = new RadioCase();
        c.F.Rig.SuppressSpeech = true;
        typeof(FlexBase).GetField("_IsConnected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, true);
        c.F.Rig.SetChangeNothingActive(true);
        Assert.Equal(GuardedOutcome.Refused, c.F.Rig.BeginFirmwareUpdate(Path.Combine(Path.GetTempPath(), "no-such-image.ssdr")));
    }

    [Fact]
    public void A_registration_under_the_hold_is_refused_and_one_with_no_radio_failed()
    {
        using var c = new RadioCase();
        c.F.Rig.SuppressSpeech = true;
        typeof(FlexBase).GetField("_IsConnected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, true);
        c.F.Rig.SetChangeNothingActive(true);
        Assert.Equal(GuardedOutcome.Refused, c.F.Rig.BeginSmartLinkRegistration(null!, (_, _) => { }));
        Assert.Equal(GuardedOutcome.Refused, c.F.Rig.BeginSmartLinkUnregistration(null!, (_, _) => { }));

        // Control: with the hold off and no confirmed check, nothing goes out
        // and it is a failure, not a refusal.
        c.F.Rig.SetChangeNothingActive(false);
        Assert.Equal(GuardedOutcome.Failed, c.F.Rig.BeginSmartLinkRegistration(null!, (_, _) => { }));
    }

    // ---- the callers: silent on Refused ----

    [Fact]
    public void Full_duplex_says_nothing_after_the_holds_refusal()
    {
        string body = Body(Read("JJFlexWpf", "FullDuplexControl.cs"), "internal static async Task ToggleAsync(");
        int awaited = body.IndexOf("key = await request;", StringComparison.Ordinal);
        int silent = body.IndexOf("if (key == null) return;", StringComparison.Ordinal);
        int spoken = body.IndexOf("Speak(key);", StringComparison.Ordinal);
        Assert.True(awaited > 0 && silent > awaited && silent < spoken,
            "a refused full duplex toggle goes on to speak a second sentence");
        Assert.Contains("if (GuardRefuses(\"settings.guard.action.full_duplex\")) return null;",
            Read("Radios", "FlexBase.FullDuplex.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Port_forwarding_says_nothing_after_the_holds_refusal()
    {
        string text = Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
        int at = text.IndexOf("var outcome = PerformPortForwardApply(wantEnabled, tcp, udp);", StringComparison.Ordinal);
        Assert.True(at > 0);
        string branch = text.Substring(at, 600);
        Assert.Contains("else if (outcome != GuardedOutcome.Refused)", branch, StringComparison.Ordinal);
        int failed = branch.IndexOf("settings.network.forwarding_command_failed", StringComparison.Ordinal);
        Assert.True(failed > branch.IndexOf("else if (outcome != GuardedOutcome.Refused)", StringComparison.Ordinal),
            "\"The port forwarding command failed\" is reached by a refusal");

        string apply = Body(text, "private GuardedOutcome PerformPortForwardApply(");
        string refused = apply.Substring(apply.IndexOf("else if (outcome == GuardedOutcome.Refused)", StringComparison.Ordinal));
        refused = refused.Substring(0, refused.IndexOf("else", 10, StringComparison.Ordinal));
        Assert.DoesNotContain("ScreenReaderOutput.Speak", refused, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("JJFlexWpf/Dialogs/SettingsDialog.RadioSetup.cs", "if (sent == GuardedOutcome.Refused)", "else if (sent != GuardedOutcome.Done)")]
    [InlineData("JJFlexWpf/Dialogs/SettingsDialog.RadioSetup.cs", "else if (started == GuardedOutcome.Refused)", "else\n")]
    [InlineData("JJFlexWpf/Controls/StaticIpControl.xaml.cs", "private void ShowHoldRefusal()", "\n\n")]
    public void Each_caller_shows_the_refusal_and_speaks_nothing_on_it(string file, string start, string end)
    {
        string text = Read(file.Split('/')).Replace("\r\n", "\n");
        int at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at > 0, "not found in " + file + ": " + start);
        int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        string branch = text.Substring(at, stop - at);
        Assert.Contains("settings.guard.refused", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenReaderOutput.Speak", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("Report(", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_static_address_and_DHCP_callers_route_a_refusal_away_from_could_not_send()
    {
        string text = Read("JJFlexWpf", "Controls", "StaticIpControl.xaml.cs");
        foreach (string failed in new[] { "connect.network.fixed_send_failed_report", "connect.network.dhcp_send_failed_report" })
        {
            int f = text.IndexOf(failed, StringComparison.Ordinal);
            int refusal = text.LastIndexOf("if (sent == Radios.GuardedOutcome.Refused)", f, StringComparison.Ordinal);
            int notDone = text.LastIndexOf("else if (sent != Radios.GuardedOutcome.Done)", f, StringComparison.Ordinal);
            Assert.True(refusal > 0 && notDone > refusal && f - refusal < 400, failed + " is reached by a refusal");
        }
    }

    [Fact]
    public void The_mic_profile_callers_do_not_call_a_refusal_missing()
    {
        string workshop = Read("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.MicProfiles.cs");
        int listed = workshop.IndexOf("audio.micprofile.no_longer_listed", StringComparison.Ordinal);
        int guard = workshop.LastIndexOf("else if (selected != GuardedOutcome.Refused)", listed, StringComparison.Ordinal);
        Assert.True(guard > 0 && listed - guard < 300, "\"no longer listed\" is said after a refusal");

        string fixer = Read("JJFlexWpf", "Dialogs", "FixerFixActions.cs");
        int stopped = fixer.IndexOf("The radio stopped listing", StringComparison.Ordinal);
        int notDone = fixer.LastIndexOf("if (selected != GuardedOutcome.Done)", stopped, StringComparison.Ordinal);
        int refused = fixer.LastIndexOf("if (selected == GuardedOutcome.Refused)", stopped, StringComparison.Ordinal);
        Assert.True(refused > 0 && notDone > refused && stopped - notDone < 200,
            "\"stopped listing\" is reported for a refusal");
    }

    [Fact]
    public void A_refused_registration_retry_does_not_say_it_could_not_be_sent()
    {
        string text = Read("Radios", "FlexBase.SmartLink.cs");
        int retry = text.IndexOf("The retry could not be sent.", StringComparison.Ordinal);
        Assert.True(retry > 0);
        string before = text.Substring(retry - 250, 250);
        Assert.Contains("== GuardedOutcome.Failed)", before, StringComparison.Ordinal);
    }

    private static string Body(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, "not found: " + signature);
        int open = text.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(at, i - at + 1);
        }
        return text.Substring(at);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string[] all = new string[parts.Length + 1];
        all[0] = dir!.FullName;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return File.ReadAllText(Path.Combine(all));
    }
}
