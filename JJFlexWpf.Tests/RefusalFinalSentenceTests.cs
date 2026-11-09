using System;
using System.IO;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// One refusal sentence per press, carried to the callers' FINAL sentence
/// (#486, repair one). The first round silenced the helpers; the Settings
/// commit then still said "Settings applied." over the hold's refusal,
/// interrupting it, and the Audio Workshop's saved microphone setup said
/// "Microphone setup X applied." over a refused radio half.
/// </summary>
/// <remarks>
/// <para><b>Desk safety.</b> Pure decisions and source only: no window or
/// element is built, and every test asserts it runs on an MTA thread, where
/// WPF cannot build one. The rig half (that a refused radio half is reported
/// as a refusal) is in Radios.Tests' <c>RefusalSaidOnceTests</c>, on a
/// FlexBase over the fake transport.</para>
/// </remarks>
public sealed class RefusalFinalSentenceTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    // ---- Settings, Apply and OK ----

    [Fact]
    public void A_refused_radio_change_is_not_announced_as_applied_and_the_holds_sentence_is_not_cut()
    {
        AssertWindowless();
        var apply = SettingsDialog.CommitSummary(closing: false, pttDropped: false, holdRefused: true);
        Assert.Equal("settings.commit.applied_others_spoken", apply.Key);
        Assert.False(apply.Interrupt);
        var ok = SettingsDialog.CommitSummary(closing: true, pttDropped: false, holdRefused: true);
        Assert.Equal("settings.commit.saved_others_spoken", ok.Key);
        Assert.False(ok.Interrupt);
        // The other settings' sentence is true for them, and never claims the
        // radio change.
        Assert.Equal("Your other settings were applied.", Lexicon.Get(apply.Key));
        Assert.Equal("Your other settings were saved.", Lexicon.Get(ok.Key));
    }

    [Fact]
    public void Without_a_refusal_the_commit_speaks_as_before()
    {
        AssertWindowless();
        Assert.Equal(("settings.commit.applied_spoken", true), SettingsDialog.CommitSummary(false, false, false));
        Assert.Equal(("settings.commit.saved_spoken", true), SettingsDialog.CommitSummary(true, false, false));
        Assert.Equal(("settings.commit.ptt_not_saved", true), SettingsDialog.CommitSummary(true, true, false));
        // The PTT sentence already says the other settings were saved; with a
        // refusal it waits its turn too.
        Assert.Equal(("settings.commit.ptt_not_saved", false), SettingsDialog.CommitSummary(false, true, true));
    }

    [Fact]
    public void The_port_forward_refusal_reaches_the_commits_summary()
    {
        AssertWindowless();
        string text = Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs").Replace("\r\n", "\n");

        // The refusal branch notes it ...
        int at = text.IndexOf("var outcome = PerformPortForwardApply(wantEnabled, tcp, udp);", StringComparison.Ordinal);
        Assert.True(at > 0);
        string branch = text.Substring(at, 900);
        int notRefused = branch.IndexOf("else if (outcome != GuardedOutcome.Refused)", StringComparison.Ordinal);
        int noted = branch.IndexOf("noteRefused();", StringComparison.Ordinal);
        Assert.True(notRefused > 0 && noted > notRefused, "the hold's refusal is not carried out of the port-forward commit");

        // ... and the whole commit hands it to both of its final sentences.
        string commit = Body(text, "private bool ApplyAllSettings(bool closing)");
        Assert.Contains("CommitPortForwardIfDirty(queued, applied, out bool holdRefused)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitSummary(closing: true, pttDropped, holdRefused)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitSummary(closing: false, pttDropped, holdRefused)", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupt: true", commit, StringComparison.Ordinal);
    }

    // ---- the Audio Workshop's saved microphone setup ----

    [Fact]
    public void A_saved_setup_with_its_radio_half_refused_claims_only_the_computer_half()
    {
        AssertWindowless();
        string said = AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic",
            new MicProfileApplyResult { RadioHalfApplied = false, HoldRefused = true }, captureNotes: "");
        Assert.Equal("Microphone setup Desk mic: the computer settings were applied.", said);
        Assert.DoesNotContain(Lexicon.Get("audio.micprofile.applied", ("profile", "Desk mic")), said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_setup_that_loaded_still_says_it_was_applied()
    {
        AssertWindowless();
        string said = AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic",
            new MicProfileApplyResult { RadioHalfApplied = true, Message = "Radio mic profile Ragchew loaded." }, "");
        Assert.Equal("Microphone setup Desk mic applied. Radio mic profile Ragchew loaded.", said);
    }

    [Fact]
    public void The_workshop_speaks_the_decided_sentence_queued()
    {
        AssertWindowless();
        string text = Read("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.MicProfiles.cs").Replace("\r\n", "\n");
        string apply = Body(text, "private void ApplyMicProfile()");
        Assert.Contains("MicProfileAppliedSentence(profile.Name, radioResult, captureNotes)", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupt:", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("audio.micprofile.applied", apply, StringComparison.Ordinal);
    }

    // ---- registration's re-readable reason ----

    [Fact]
    public void A_refused_registration_keeps_the_holds_sentence_on_its_status_line()
    {
        AssertWindowless();
        string text = Read("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs").Replace("\r\n", "\n");
        int at = text.IndexOf("if (sent == GuardedOutcome.Refused)", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = text.IndexOf("else if (sent != GuardedOutcome.Done)", at, StringComparison.Ordinal);
        string branch = text.Substring(at, end - at);
        int refresh = branch.IndexOf("RefreshSetupStatuses(startARegistrationCheck: false);", StringComparison.Ordinal);
        int reason = branch.IndexOf("SetupRegisterStatus.Text = Lexicon.Get(\"settings.guard.refused\"", StringComparison.Ordinal);
        Assert.True(refresh > 0 && reason > refresh,
            "the refresh rewrites the registration line after the hold's reason is written");
        Assert.Equal(-1, branch.IndexOf("RefreshSetupStatuses", refresh + 1, StringComparison.Ordinal));
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
        throw new InvalidOperationException("unbalanced: " + signature);
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
