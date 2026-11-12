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
        Assert.Equal("Your other settings were saved.", Lexicon.Get(apply.Key));
        Assert.Equal("Your other settings were saved.", Lexicon.Get(ok.Key));
    }

    [Fact]
    public void Without_a_refusal_the_commit_speaks_as_before()
    {
        AssertWindowless();
        Assert.Equal(("settings.commit.applied_spoken", true), SettingsDialog.CommitSummary(false, false, false));
        Assert.Equal(("settings.commit.saved_spoken", true), SettingsDialog.CommitSummary(true, false, false));
        // Push-to-talk changes with nowhere to go: CommitAnnouncement says
        // their sentence, and the summary is the ordinary one for the others
        // (the words round: the PTT key is the PTT outcome only now).
        Assert.Equal(("settings.commit.saved_others_spoken", true), SettingsDialog.CommitSummary(true, true, false));
        // With a refusal it waits its turn too.
        Assert.Equal(("settings.commit.applied_others_spoken", false), SettingsDialog.CommitSummary(false, true, true));
    }

    [Fact]
    public void The_port_forward_refusal_reaches_the_commits_one_announcement()
    {
        AssertWindowless();
        string text = Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs").Replace("\r\n", "\n");

        // The outcome is filed by the one router (#1088 repair), whose refusal
        // branch records the hold's sentence ...
        int at = text.IndexOf("var outcome = PerformPortForwardApply(wantEnabled, tcp, udp);", StringComparison.Ordinal);
        Assert.True(at > 0);
        string branch = text.Substring(at, 400);
        Assert.Contains("FilePortForwardOutcome(outcome, NetworkCurrentStateText.Text,", branch, StringComparison.Ordinal);
        string router = Body(text, "internal static string? FilePortForwardOutcome(");
        int isRefused = router.IndexOf("if (outcome == GuardedOutcome.Refused)", StringComparison.Ordinal);
        int noted = router.IndexOf("refused.Add(Lexicon.Get(\"settings.guard.refused\"", StringComparison.Ordinal);
        Assert.True(isRefused > 0 && noted > isRefused, "the hold's refusal is not carried out of the port-forward commit");

        // ... into the list the whole commit owns, and every final word
        // (advisory or spoken) comes from the one decision that reads it.
        string commit = Body(text, "private bool ApplyAllSettings(bool closing)");
        Assert.Contains("var refused = new List<string>();", commit, StringComparison.Ordinal);
        Assert.Contains("CommitPortForwardIfDirty(queued, applied, refused, failed)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitAnnouncement(closing, pttDropped, refused, queued, applied, failed)", commit, StringComparison.Ordinal);
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
        Assert.StartsWith("Some changes need your attention:", said.Advisory!, StringComparison.Ordinal);
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
    public void Apply_with_something_waiting_and_a_refusal_lets_the_waiting_note_speak_behind_the_hold()
    {
        AssertWindowless();
        var said = SettingsDialog.CommitAnnouncement(closing: false, pttDropped: false,
            refused: new[] { Refusal }, queued: new[] { Queued }, applied: Array.Empty<string>());
        Assert.Null(said.Advisory);
        Assert.False(said.Interrupt);
        // The words round (#1088): no aggregate summary over a waiting note.
        Assert.Equal(Lexicon.Get("settings.commit.waiting_lead").Trim() + " " + Queued, said.Spoken);
        Assert.DoesNotContain("Your other settings were", said.Spoken!, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings applied.", said.Spoken!, StringComparison.Ordinal);
    }

    [Fact]
    public void OK_with_a_refusal_folds_the_holds_sentence_into_the_one_final_sentence_that_survives_the_close()
    {
        AssertWindowless();
        // Repair two: Home's return cut the hold's sentence about 70 ms in,
        // and only the summary was replayed. On OK the summary now carries it.
        var ok = SettingsDialog.CommitAnnouncement(true, false, new[] { Refusal }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal((Refusal + " Your other settings were saved.", false), (ok.Spoken, ok.Interrupt));
        // Apply does not close: the hold's sentence is heard whole, so it is not repeated.
        var apply = SettingsDialog.CommitAnnouncement(false, false, new[] { Refusal }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(("Your other settings were saved.", false), (apply.Spoken, apply.Interrupt));
        var plain = SettingsDialog.CommitAnnouncement(false, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(("Settings applied.", true), (plain.Spoken, plain.Interrupt));
        var plainOk = SettingsDialog.CommitAnnouncement(true, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(("Settings saved.", true), (plainOk.Spoken, plainOk.Interrupt));
    }

    // ---- #1088: a fourth outcome, failed ----

    private static string KeyerNotSaved => Lexicon.Get("settings.keyer.speed_not_saved");

    [Fact]
    public void OK_with_nothing_waiting_says_the_failure_and_never_says_everything_was_saved()
    {
        AssertWindowless();
        var ok = SettingsDialog.CommitAnnouncement(true, false, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), failed: new[] { KeyerNotSaved });
        Assert.Null(ok.Advisory);
        Assert.Equal(KeyerNotSaved + " Your other settings were saved.", ok.Spoken);
        Assert.DoesNotContain("Settings saved.", ok.Spoken!, StringComparison.Ordinal);
    }

    [Fact]
    public void OK_with_something_waiting_carries_the_failure_and_never_under_done_right_away()
    {
        AssertWindowless();
        var said = SettingsDialog.CommitAnnouncement(true, false, Array.Empty<string>(), new[] { Queued },
            new[] { "Radio name saved." }, failed: new[] { KeyerNotSaved });
        Assert.NotNull(said.Advisory);
        Assert.StartsWith(Lexicon.Get("settings.commit.some_items_wait_lead_refused"), said.Advisory!, StringComparison.Ordinal);
        Assert.DoesNotContain("Everything you set was saved", said.Advisory!, StringComparison.Ordinal);
        Assert.Contains(KeyerNotSaved, said.Advisory!, StringComparison.Ordinal);
        int done = said.Advisory!.IndexOf(Lexicon.Get("settings.commit.done_right_away_lead"), StringComparison.Ordinal);
        Assert.True(done > 0);
        Assert.True(said.Advisory.IndexOf(KeyerNotSaved, StringComparison.Ordinal) < done,
            "the failure is listed under Done right away");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_says_the_failure_whether_or_not_something_waits(bool somethingWaits)
    {
        AssertWindowless();
        var apply = SettingsDialog.CommitAnnouncement(false, false, Array.Empty<string>(),
            somethingWaits ? new[] { Queued } : Array.Empty<string>(),
            new[] { "Radio name saved." }, failed: new[] { KeyerNotSaved });
        Assert.Null(apply.Advisory);
        Assert.DoesNotContain("Settings applied.", apply.Spoken!, StringComparison.Ordinal);
        if (somethingWaits)
        {
            // The words round (#1088): the failure, then the waiting note,
            // and no aggregate summary between them.
            Assert.Equal(KeyerNotSaved + Lexicon.Get("settings.commit.waiting_lead") + Queued, apply.Spoken);
            Assert.DoesNotContain("Your other settings were", apply.Spoken!, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith(KeyerNotSaved + " Your other settings were saved.", apply.Spoken!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_failure_with_a_refusal_says_both_and_counts_neither_as_saved()
    {
        AssertWindowless();
        var ok = SettingsDialog.CommitAnnouncement(true, false, new[] { Refusal }, Array.Empty<string>(),
            Array.Empty<string>(), failed: new[] { KeyerNotSaved });
        Assert.Equal(KeyerNotSaved + " " + Refusal + " Your other settings were saved.", ok.Spoken);
        Assert.False(ok.Interrupt);    // the hold has just spoken: the summary waits its turn, as before
    }

    [Fact]
    public void The_summary_speaks_for_the_others_when_something_failed()
    {
        AssertWindowless();
        Assert.Equal(("settings.commit.saved_others_spoken", true), SettingsDialog.CommitSummary(true, false, false, anyFailed: true));
        Assert.Equal(("settings.commit.applied_others_spoken", true), SettingsDialog.CommitSummary(false, false, false, anyFailed: true));
        Assert.Equal(("settings.commit.saved_others_spoken", true), SettingsDialog.CommitSummary(true, true, false, anyFailed: true));
    }

    // ---- #1088, the words round: the push-to-talk outcome in every
    // combination, and no aggregate summary over a waiting note on Apply ----

    private static string PttNotSaved => Lexicon.Get("settings.commit.ptt_not_saved");
    private static string ProfileNotSaved => "Workshop radio: profile not saved.";

    [Fact]
    public void OK_with_push_to_talk_dropped_and_something_waiting_says_the_push_to_talk_outcome()
    {
        AssertWindowless();
        // Push-to-talk edited with no radio, and a keyer speed saved for the
        // next connect: the advisory used to return before the PTT outcome
        // was read, and lead with "Everything you set was saved".
        var said = SettingsDialog.CommitAnnouncement(true, pttDropped: true, Array.Empty<string>(),
            new[] { Lexicon.Get("settings.keyer.speed_waits_for_radio") }, Array.Empty<string>());
        Assert.Null(said.Spoken);
        Assert.NotNull(said.Advisory);
        Assert.Contains(PttNotSaved, said.Advisory!, StringComparison.Ordinal);
        Assert.Contains(Lexicon.Get("settings.keyer.speed_waits_for_radio"), said.Advisory!, StringComparison.Ordinal);
        Assert.DoesNotContain("Everything you set was saved", said.Advisory!, StringComparison.Ordinal);
        Assert.StartsWith(Lexicon.Get("settings.commit.some_items_wait_lead_refused"), said.Advisory!, StringComparison.Ordinal);
    }

    [Fact]
    public void OK_with_push_to_talk_dropped_a_failure_and_something_waiting_says_both_outcomes()
    {
        AssertWindowless();
        var said = SettingsDialog.CommitAnnouncement(true, pttDropped: true, Array.Empty<string>(),
            new[] { Queued }, Array.Empty<string>(), failed: new[] { ProfileNotSaved });
        Assert.Contains(PttNotSaved, said.Advisory!, StringComparison.Ordinal);
        Assert.Contains(ProfileNotSaved, said.Advisory!, StringComparison.Ordinal);
        Assert.True(said.Advisory!.IndexOf(PttNotSaved, StringComparison.Ordinal)
                    < said.Advisory.IndexOf(ProfileNotSaved, StringComparison.Ordinal),
            "the push-to-talk outcome does not lead the failures");
    }

    [Fact]
    public void Apply_with_push_to_talk_dropped_and_something_waiting_says_the_push_to_talk_outcome_and_the_note()
    {
        AssertWindowless();
        var apply = SettingsDialog.CommitAnnouncement(false, pttDropped: true, Array.Empty<string>(),
            new[] { Queued }, new[] { "Radio name saved." });
        Assert.Null(apply.Advisory);
        Assert.Equal(PttNotSaved + Lexicon.Get("settings.commit.waiting_lead") + Queued, apply.Spoken);
        Assert.DoesNotContain("Your other settings were", apply.Spoken!, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_to_talk_dropped_with_nothing_waiting_says_its_outcome_then_the_others_once()
    {
        AssertWindowless();
        var ok = SettingsDialog.CommitAnnouncement(true, true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal((PttNotSaved + " Your other settings were saved.", true), (ok.Spoken, ok.Interrupt));
        var apply = SettingsDialog.CommitAnnouncement(false, true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal((PttNotSaved + " Your other settings were saved.", true), (apply.Spoken, apply.Interrupt));
        // With another failure, the summary is still said once, after both.
        var both = SettingsDialog.CommitAnnouncement(true, true, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), failed: new[] { KeyerNotSaved });
        Assert.Equal(PttNotSaved + " " + KeyerNotSaved + " Your other settings were saved.", both.Spoken);
    }

    [Fact]
    public void Apply_with_a_failure_and_a_setting_waiting_for_the_next_connection_never_says_the_others_were_applied()
    {
        AssertWindowless();
        string waits = Lexicon.Get("settings.keyer.speed_waits_for_radio");
        var apply = SettingsDialog.CommitAnnouncement(false, false, Array.Empty<string>(), new[] { waits },
            Array.Empty<string>(), failed: new[] { ProfileNotSaved });
        Assert.Equal(ProfileNotSaved + Lexicon.Get("settings.commit.waiting_lead") + waits, apply.Spoken);
        Assert.DoesNotContain("Your other settings were", apply.Spoken!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declined_port_change_is_never_covered_by_a_saved_or_applied_summary()
    {
        AssertWindowless();
        // Answered No: it sits in the waiting list, and was not saved at all.
        string declined = Lexicon.Get("settings.network.forwarding_declined");
        var apply = SettingsDialog.CommitAnnouncement(false, false, Array.Empty<string>(), new[] { declined },
            new[] { "Radio name saved." });
        Assert.Equal(Lexicon.Get("settings.commit.waiting_lead").Trim() + " " + declined, apply.Spoken);
        Assert.DoesNotContain("Settings applied.", apply.Spoken!, StringComparison.Ordinal);
        Assert.DoesNotContain("Your other settings were", apply.Spoken!, StringComparison.Ordinal);

        var ok = SettingsDialog.CommitAnnouncement(true, false, Array.Empty<string>(), new[] { declined },
            Array.Empty<string>());
        Assert.Contains(declined, ok.Advisory!, StringComparison.Ordinal);
        Assert.DoesNotContain("Everything you set was saved", ok.Advisory!, StringComparison.Ordinal);
        Assert.DoesNotContain("were saved", ok.Advisory!.Replace(declined, ""), StringComparison.Ordinal);
    }

    /// <summary>
    /// Source wiring only, and only for the six commits named here: each
    /// files its failure as failed, never as applied. It proves nothing about
    /// any other writer; the three routers below are tested by behaviour.
    /// </summary>
    [Fact]
    public void The_six_named_commits_are_wired_to_file_a_failure_as_failed()
    {
        AssertWindowless();
        string keyer = Read("JJFlexWpf", "Dialogs", "SettingsDialog.KeyerSpeed.cs");
        // Matched as patterns, so the key scan does not read these as keys.
        Assert.Matches(@"failed\.Add\(Lexicon\.Get\(.settings\.keyer\.speed_not_saved.\)\);", keyer);
        Assert.DoesNotMatch(@"applied\.Add\(Lexicon\.Get\(.settings\.keyer\.speed_not_saved", keyer);
        string setup = Read("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs");
        Assert.Matches(@"failed\.Add\(Lexicon\.Get\(.settings\.radio\.name\.rename_failed_note.\)\);", setup);
        string profile = Read("JJFlexWpf", "Dialogs", "SettingsDialog.RadioProfile.cs");
        Assert.Contains("failed.Add(failNote);", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("applied.Add(failNote);", profile, StringComparison.Ordinal);
        string commit = Body(Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs").Replace("\r\n", "\n"), "private bool ApplyAllSettings(bool closing)");
        Assert.Contains("CommitRadioProfiles(queued, applied, failed)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitSetupRadioNameIfDirty(applied, failed)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitKeyerSpeedIfChanged(queued, failed)", commit, StringComparison.Ordinal);
        // #1088 repair: the system-wide keys, the overwrite warning and the port forward.
        Assert.Contains("CommitSystemWideKeysIfDirty(applied, failed)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitSliceOverwriteSetting(failed)", commit, StringComparison.Ordinal);
        Assert.Contains("CommitPortForwardIfDirty(queued, applied, refused, failed)", commit, StringComparison.Ordinal);
        string keys = Read("JJFlexWpf", "Dialogs", "SettingsDialog.SystemWideKeys.cs");
        Assert.Contains("FileSystemWideKeysOutcome(_systemWideKeysPanel.CommitOutcome(), applied, failed);", keys, StringComparison.Ordinal);
        string overwrite = Read("JJFlexWpf", "Dialogs", "SettingsDialog.SliceOverwrite.cs");
        Assert.Contains("CommitSliceOverwriteEdits(_warnOverwriteEdits,", overwrite, StringComparison.Ordinal);
        string save = Body(Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs").Replace("\r\n", "\n"), "private bool SaveSettings()");
        Assert.DoesNotContain("CommitSliceOverwriteSetting(", save, StringComparison.Ordinal);
    }

    // ---- #1088 repair: the three callers Codex found, at their boundary ----

    private static string KeysNotSaved => Lexicon.Get("settings.systemwide_keys.save_failed");

    [Fact]
    public void A_system_wide_keys_file_that_was_not_written_is_failed_and_OK_never_says_saved()
    {
        AssertWindowless();
        var applied = new System.Collections.Generic.List<string>();
        var failed = new System.Collections.Generic.List<string>();
        SettingsDialog.FileSystemWideKeysOutcome(new SystemWideKeysCommitOutcome(KeysNotSaved, Saved: false), applied, failed);
        Assert.Empty(applied);
        Assert.Equal(new[] { KeysNotSaved }, failed);

        var ok = SettingsDialog.CommitAnnouncement(true, false, Array.Empty<string>(), Array.Empty<string>(), applied, failed);
        Assert.Equal(KeysNotSaved + " Your other settings were saved.", ok.Spoken);

        // With something waiting, it is never under Done right away.
        var waiting = SettingsDialog.CommitAnnouncement(true, false, Array.Empty<string>(), new[] { Queued }, applied, failed);
        Assert.DoesNotContain("Everything you set was saved", waiting.Advisory!, StringComparison.Ordinal);
        Assert.Contains(KeysNotSaved, waiting.Advisory!, StringComparison.Ordinal);
        Assert.DoesNotContain(Lexicon.Get("settings.commit.done_right_away_lead"), waiting.Advisory!, StringComparison.Ordinal);

        // Apply with something waiting says it too.
        var apply = SettingsDialog.CommitAnnouncement(false, false, Array.Empty<string>(), new[] { Queued }, applied, failed);
        Assert.StartsWith(KeysNotSaved, apply.Spoken!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_system_wide_keys_save_that_landed_is_still_applied()
    {
        AssertWindowless();
        var applied = new System.Collections.Generic.List<string>();
        var failed = new System.Collections.Generic.List<string>();
        string saved = Lexicon.Get("settings.systemwide_keys.saved");
        SettingsDialog.FileSystemWideKeysOutcome(new SystemWideKeysCommitOutcome(saved, Saved: true), applied, failed);
        SettingsDialog.FileSystemWideKeysOutcome(null, applied, failed);
        Assert.Equal(new[] { saved }, applied);
        Assert.Empty(failed);
    }

    [Fact]
    public void A_failed_port_forward_command_is_failed_and_never_waiting()
    {
        AssertWindowless();
        var applied = new System.Collections.Generic.List<string>();
        var refused = new System.Collections.Generic.List<string>();
        var failed = new System.Collections.Generic.List<string>();
        string? note = SettingsDialog.FilePortForwardOutcome(GuardedOutcome.Failed, "ports open", applied, refused, failed);
        string commandFailed = Lexicon.Get("settings.network.forwarding_command_failed");
        Assert.Equal(commandFailed, note);
        Assert.Equal(new[] { commandFailed }, failed);
        Assert.Empty(applied);
        Assert.Empty(refused);

        var ok = SettingsDialog.CommitAnnouncement(true, false, refused, Array.Empty<string>(), applied, failed);
        Assert.Null(ok.Advisory);
        Assert.Equal(commandFailed + " Your other settings were saved.", ok.Spoken);
        var apply = SettingsDialog.CommitAnnouncement(false, false, refused, Array.Empty<string>(), applied, failed);
        Assert.DoesNotContain(Lexicon.Get("settings.commit.waiting_lead").Trim(), apply.Spoken!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_forward_that_was_done_or_refused_is_never_failed()
    {
        AssertWindowless();
        var applied = new System.Collections.Generic.List<string>();
        var refused = new System.Collections.Generic.List<string>();
        var failed = new System.Collections.Generic.List<string>();
        Assert.Null(SettingsDialog.FilePortForwardOutcome(GuardedOutcome.Done, "ports open", applied, refused, failed));
        Assert.Null(SettingsDialog.FilePortForwardOutcome(GuardedOutcome.Refused, "ports open", applied, refused, failed));
        Assert.Equal(new[] { "ports open" }, applied);
        Assert.Equal(new[] { Refusal }, refused);
        Assert.Empty(failed);
    }

    [Fact]
    public void An_overwrite_warning_setting_that_was_not_written_is_failed_and_stays_pending()
    {
        AssertWindowless();
        var edits = new SliceOverwriteSettingEdits();
        Assert.Equal("SERIAL-ONE", edits.ResolveTarget("SERIAL-ONE"));
        Assert.True(edits.Show("SERIAL-ONE", _ => true, null));
        edits.Edit(false);
        var failed = new System.Collections.Generic.List<string>();
        int writes = 0;

        SettingsDialog.CommitSliceOverwriteEdits(edits, (_, _) => { writes++; return false; }, _ => "Bench radio", failed);

        string notSaved = Lexicon.Get("settings.commit.overwrite_warning_not_saved", ("radio", "Bench radio"));
        Assert.Equal(1, writes);
        Assert.Equal(new[] { notSaved }, failed);
        var ok = SettingsDialog.CommitAnnouncement(true, false, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), failed);
        Assert.Equal(notSaved + " Your other settings were saved.", ok.Spoken);

        // The edit is still held for the next OK, and a write that lands then is not failed.
        var again = new System.Collections.Generic.List<string>();
        SettingsDialog.CommitSliceOverwriteEdits(edits, (_, _) => { writes++; return true; }, _ => "Bench radio", again);
        Assert.Equal(2, writes);
        Assert.Empty(again);
    }

    [Fact]
    public void A_held_registration_says_no_opening_sentence_before_the_refusal()
    {
        AssertWindowless();
        string text = Read("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs").Replace("\r\n", "\n");
        string start = Body(text, "private void StartRegistration(bool register, FlexBase.RegistrationCheck confirmed)");
        int ask = start.IndexOf("if (!_rig.ChangeNothingActive)", StringComparison.Ordinal);
        int opening = start.IndexOf("ScreenReaderOutput.Speak(opening,", StringComparison.Ordinal);
        int send = start.IndexOf("_rig.BeginSmartLinkRegistration(confirmed, OnState)", StringComparison.Ordinal);
        Assert.True(ask > 0 && opening > ask && send > opening, "the opening sentence is said before the hold is asked");
        // The opening is inside the hold's question, not after it.
        string guarded = start.Substring(ask, start.IndexOf("\n            }", ask, StringComparison.Ordinal) - ask);
        Assert.Contains("ScreenReaderOutput.Speak(opening,", guarded, StringComparison.Ordinal);
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
