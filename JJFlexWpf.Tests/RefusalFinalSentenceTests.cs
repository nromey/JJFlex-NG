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
    public void The_port_forward_refusal_reaches_the_commits_one_announcement()
    {
        AssertWindowless();
        string text = Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs").Replace("\r\n", "\n");

        // The refusal branch records the hold's sentence ...
        int at = text.IndexOf("var outcome = PerformPortForwardApply(wantEnabled, tcp, udp);", StringComparison.Ordinal);
        Assert.True(at > 0);
        string branch = text.Substring(at, 1000);
        int notRefused = branch.IndexOf("else if (outcome != GuardedOutcome.Refused)", StringComparison.Ordinal);
        int noted = branch.IndexOf("refused.Add(Lexicon.Get(\"settings.guard.refused\"", StringComparison.Ordinal);
        Assert.True(notRefused > 0 && noted > notRefused, "the hold's refusal is not carried out of the port-forward commit");

        // ... into the list the whole commit owns, and every final word
        // (advisory or spoken) comes from the one decision that reads it.
        string commit = Body(text, "private bool ApplyAllSettings(bool closing)");
        Assert.Contains("var refused = new List<string>();", commit, StringComparison.Ordinal);
        Assert.Contains("CommitPortForwardIfDirty(queued, applied, refused)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitAnnouncement(closing, pttDropped, refused, queued, applied)", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("some_items_wait_lead", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupt: true", commit, StringComparison.Ordinal);
        Assert.Equal(1, Count(commit, "AdvisoryDialog.Show("));
        Assert.Equal(1, Count(commit, "ScreenReaderOutput.Speak("));
    }

    // ---- repair two: OK with something else waiting ----

    private const string Queued = "Your connection preference is saved and takes effect on the next connect.";
    private static string Refusal => Lexicon.Get("settings.guard.refused", ("action", Lexicon.Get("settings.guard.action.ports")));

    [Fact]
    public void OK_with_another_setting_waiting_does_not_say_everything_was_saved_and_carries_the_refusal()
    {
        AssertWindowless();
        var said = SettingsDialog.CommitAnnouncement(closing: true, pttDropped: false,
            refused: new[] { Refusal }, queued: new[] { Queued }, applied: Array.Empty<string>());
        Assert.Null(said.Spoken);
        Assert.NotNull(said.Advisory);
        Assert.DoesNotContain("Everything you set was saved", said.Advisory!, StringComparison.Ordinal);
        Assert.StartsWith(Lexicon.Get("settings.commit.some_items_wait_lead_refused"), said.Advisory!, StringComparison.Ordinal);
        Assert.StartsWith("Your other settings were saved.", said.Advisory!, StringComparison.Ordinal);
        // The waiting preference's receipt is still there, and true.
        Assert.Contains(Queued, said.Advisory!, StringComparison.Ordinal);
        // And the refusal, re-readable, after the window change cut it off.
        Assert.Contains(Refusal, said.Advisory!, StringComparison.Ordinal);
    }

    [Fact]
    public void OK_with_something_waiting_and_no_refusal_reads_as_before()
    {
        AssertWindowless();
        var said = SettingsDialog.CommitAnnouncement(closing: true, pttDropped: false,
            refused: Array.Empty<string>(), queued: new[] { Queued }, applied: new[] { "Radio name saved." });
        Assert.StartsWith(Lexicon.Get("settings.commit.some_items_wait_lead"), said.Advisory!, StringComparison.Ordinal);
        Assert.Contains(Queued, said.Advisory!, StringComparison.Ordinal);
        Assert.Contains(Lexicon.Get("settings.commit.done_right_away_lead") + "Radio name saved.", said.Advisory!, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_with_something_waiting_and_a_refusal_speaks_for_the_other_settings_behind_the_hold()
    {
        AssertWindowless();
        var said = SettingsDialog.CommitAnnouncement(closing: false, pttDropped: false,
            refused: new[] { Refusal }, queued: new[] { Queued }, applied: Array.Empty<string>());
        Assert.Null(said.Advisory);
        Assert.False(said.Interrupt);
        Assert.StartsWith("Your other settings were applied.", said.Spoken!, StringComparison.Ordinal);
        Assert.Contains(Queued, said.Spoken!, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings applied.", said.Spoken!, StringComparison.Ordinal);
    }

    [Fact]
    public void OK_and_Apply_with_nothing_waiting_speak_as_repair_one_decided()
    {
        AssertWindowless();
        var ok = SettingsDialog.CommitAnnouncement(true, false, new[] { Refusal }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(("Your other settings were saved.", false), (ok.Spoken, ok.Interrupt));
        var plain = SettingsDialog.CommitAnnouncement(false, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(("Settings applied.", true), (plain.Spoken, plain.Interrupt));
    }

    // ---- the Audio Workshop's saved microphone setup ----

    private static MicProfileApplyResult HeldRadioHalf => new() { RadioHalfApplied = false, HoldRefused = true };

    private static (bool, string?) LevelSet(string target, int percent, bool boost, float db) => (true, null);

    /// <summary>The capture half, driven through its real decisions with
    /// writers that never touch this computer's audio devices.</summary>
    private static AudioWorkshopDialog.CaptureHalfResult Capture(MicCaptureSettings capture, string currentName,
        bool chainAttached = true, Func<string, int, bool, float, (bool, string?)>? setLevel = null)
    {
        var result = AudioWorkshopDialog.ApplyCaptureHalfTo(capture, currentName,
            _ => chainAttached,
            setLevel ?? LevelSet);
        return result;
    }

    [Fact]
    public void A_saved_setup_with_its_radio_half_refused_and_its_computer_half_applied_says_only_that()
    {
        AssertWindowless();
        var capture = Capture(new MicCaptureSettings { DeviceName = "Desk mic", InputLevelPercent = 40 }, "Desk mic");
        Assert.Equal(AudioWorkshopDialog.CaptureHalfOutcome.Applied, capture.Outcome);
        string said = AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic", HeldRadioHalf, capture);
        Assert.Equal("Microphone setup Desk mic: the computer settings were applied.", said);
        Assert.DoesNotContain(Lexicon.Get("audio.micprofile.applied", ("profile", "Desk mic")), said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_microphone_in_use_skips_the_computer_half_and_nothing_claims_it_applied()
    {
        AssertWindowless();
        // Made with input A, this computer is on input B: the level and the
        // cleanup are left alone, and neither writer is ever called.
        bool levelCalled = false;
        var capture = AudioWorkshopDialog.ApplyCaptureHalfTo(
            new MicCaptureSettings { DeviceName = "Desk mic", InputLevelPercent = 40, Conditioning = new TxConditioningSettings() },
            "Headset",
            _ => throw new InvalidOperationException("the cleanup was applied on a different microphone"),
            (t, p, b, d) => { levelCalled = true; return (true, null); });
        Assert.False(levelCalled);
        Assert.Equal(AudioWorkshopDialog.CaptureHalfOutcome.NothingApplied, capture.Outcome);
        Assert.Contains("Headset", capture.Notes, StringComparison.Ordinal);

        string said = AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic", HeldRadioHalf, capture);
        Assert.Equal(capture.Notes, said);
        Assert.DoesNotContain("were applied", said, StringComparison.Ordinal);
        Assert.DoesNotContain(" applied.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_level_Windows_would_not_set_is_not_claimed_as_applied()
    {
        AssertWindowless();
        // The Windows-level write fails, as the catch around it reports.
        var capture = Capture(new MicCaptureSettings { DeviceName = "Desk mic", InputLevelPercent = 40 }, "Desk mic",
            setLevel: (t, p, b, d) => (false, "Access is denied."));
        Assert.Equal(AudioWorkshopDialog.CaptureHalfOutcome.NothingApplied, capture.Outcome);
        Assert.Contains("Access is denied.", capture.Notes, StringComparison.Ordinal);
        string said = AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic", HeldRadioHalf, capture);
        Assert.DoesNotContain("were applied", said, StringComparison.Ordinal);
        Assert.Equal(capture.Notes, said);
    }

    [Fact]
    public void A_computer_half_that_applied_only_in_part_says_some_of_it_did()
    {
        AssertWindowless();
        // The cleanup went on the live chain; the level did not.
        var capture = Capture(new MicCaptureSettings { DeviceName = "Desk mic", InputLevelPercent = 40, Conditioning = new TxConditioningSettings() },
            "Desk mic", setLevel: (t, p, b, d) => (false, "Access is denied."));
        Assert.Equal(AudioWorkshopDialog.CaptureHalfOutcome.Partly, capture.Outcome);
        string said = AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic", HeldRadioHalf, capture);
        Assert.StartsWith("Microphone setup Desk mic: some of the computer settings were applied.", said, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", said, StringComparison.Ordinal);

        // A level set before the boost failed is still a change made.
        var boost = Capture(new MicCaptureSettings { DeviceName = "Desk mic", InputLevelPercent = 40 }, "Desk mic",
            setLevel: (t, p, b, d) => (true, "Boost refused."));
        Assert.Equal(AudioWorkshopDialog.CaptureHalfOutcome.Partly, boost.Outcome);
    }

    [Fact]
    public void Nothing_to_apply_and_no_radio_chain_claims_nothing()
    {
        AssertWindowless();
        var empty = Capture(new MicCaptureSettings(), "Desk mic");
        Assert.Equal(AudioWorkshopDialog.CaptureHalfOutcome.NothingApplied, empty.Outcome);
        Assert.Equal("", AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic", HeldRadioHalf, empty));

        var noChain = Capture(new MicCaptureSettings { Conditioning = new TxConditioningSettings() }, "Desk mic", chainAttached: false);
        Assert.Equal(AudioWorkshopDialog.CaptureHalfOutcome.NothingApplied, noChain.Outcome);
        Assert.DoesNotContain("were applied", AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic", HeldRadioHalf, noChain), StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_setup_that_loaded_still_says_it_was_applied()
    {
        AssertWindowless();
        var capture = Capture(new MicCaptureSettings { DeviceName = "Desk mic", InputLevelPercent = 40 }, "Desk mic");
        string said = AudioWorkshopDialog.MicProfileAppliedSentence("Desk mic",
            new MicProfileApplyResult { RadioHalfApplied = true, Message = "Radio mic profile Ragchew loaded." }, capture);
        Assert.Equal("Microphone setup Desk mic applied. Radio mic profile Ragchew loaded.", said);
    }

    [Fact]
    public void The_workshop_speaks_the_decided_sentence_queued()
    {
        AssertWindowless();
        string text = Read("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.MicProfiles.cs").Replace("\r\n", "\n");
        string apply = Body(text, "private void ApplyMicProfile()");
        Assert.Contains("var capture = ApplyCaptureHalf(profile.Capture);", apply, StringComparison.Ordinal);
        Assert.Contains("MicProfileAppliedSentence(profile.Name, radioResult, capture)", apply, StringComparison.Ordinal);
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

    private static int Count(string text, string what)
    {
        int n = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) n++;
        return n;
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
