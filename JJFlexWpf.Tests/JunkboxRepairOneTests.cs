using System;
using System.Collections.Generic;
using System.IO;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Repair round one of the third 2026-11-07 junk box batch: the findings an
/// independent check made on each fix, each driven through the production
/// decision it names.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> Every test asserts an MTA thread
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>) and drives
/// pure functions, stand-ins or source text; no dialog, control or window is
/// built, and nothing is spoken.
/// </remarks>
public sealed class JunkboxRepairOneTests
{
    // ------------------------------------------------------------------
    // Transmit Controls: an unchanged delay is not written on focus loss
    // ------------------------------------------------------------------

    /// <summary>A stand-in radio's delay, refusing while the hold is on.</summary>
    private sealed class DelayRadio
    {
        public int Value = 40;
        public bool Held;
        public int Attempts;   // under the hold, every attempt is a refusal said aloud
        public int Get() => Value;
        public bool Set(int v)
        {
            Attempts++;
            if (Held) return false;
            Value = v;
            return true;
        }
    }

    [Fact]
    public void Tabbing_past_an_untouched_delay_under_the_hold_writes_nothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new DelayRadio { Held = true };
        string? shown = TXControlsDialog.DelayAfterFocusLoss("40", radio.Get, radio.Set);
        Assert.Equal("40", shown);
        Assert.Equal(0, radio.Attempts);
    }

    [Fact]
    public void A_refused_arrow_edit_goes_back_and_the_focus_loss_after_it_writes_nothing_more()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new DelayRadio { Held = true };
        // Up arrow: 41 is asked for, refused, and the box goes back to 40.
        string shown = TXControlsDialog.DelayAfterWrite(41, radio.Get, radio.Set);
        Assert.Equal("40", shown);
        Assert.Equal(1, radio.Attempts);
        // Tab away from the restored box.
        Assert.Equal("40", TXControlsDialog.DelayAfterFocusLoss(shown, radio.Get, radio.Set));
        Assert.Equal(1, radio.Attempts);
    }

    [Fact]
    public void A_typed_edit_is_still_written_and_refused_it_goes_back()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var held = new DelayRadio { Held = true };
        Assert.Equal("40", TXControlsDialog.DelayAfterFocusLoss("75", held.Get, held.Set));
        Assert.Equal(1, held.Attempts);

        var free = new DelayRadio();                                   // control: hold off
        Assert.Equal("75", TXControlsDialog.DelayAfterFocusLoss("75", free.Get, free.Set));
        Assert.Equal(75, free.Value);
        Assert.Equal("500", TXControlsDialog.DelayAfterFocusLoss("9000", free.Get, free.Set));   // clamped
        Assert.Null(TXControlsDialog.DelayAfterFocusLoss("abc", free.Get, free.Set));
    }

    [Fact]
    public void The_delay_boxes_write_only_through_those_two_decisions()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "TXControlsDialog.xaml.cs"));
        Assert.Contains("string? shown = DelayAfterFocusLoss(box.Text, get, set);", Body(src, "private void ApplyDelayFromBox(TextBox box)"));
        Assert.Contains("box.Text = DelayAfterWrite(value, get, set);", Body(src, "private void ApplyDelay(TextBox box, int value)"));
        foreach (string box in new[] { "TX1", "TX2", "TX3", "TXACC" })
        {
            int handler = src.IndexOf("private void " + box + "DelayBox_LostFocus(", StringComparison.Ordinal);
            Assert.True(handler >= 0, box + "'s focus-loss handler moved");
            Assert.Contains("ApplyDelayFromBox(" + box + "DelayBox);", src.Substring(handler, 150));
        }
    }

    // ------------------------------------------------------------------
    // Tracking notches: the list's count stays true after Add and Delete
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, 3, true)]    // focus elsewhere: rename
    [InlineData(false, false, 0, true)]
    [InlineData(true, true, 3, true)]      // on the way in: rename
    [InlineData(true, false, 0, true)]     // the list itself focused and empty: "none" is the news
    [InlineData(true, false, 3, false)]    // the list itself focused with rows: owed, paid as focus moves
    public void The_notch_list_is_renamed_at_once_unless_the_reader_is_on_the_list_itself(
        bool listItselfFocused, bool namingOnFocus, int rows, bool now)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(now, ListNaming.RenameNow(listItselfFocused, namingOnFocus, rows));
    }

    [Fact]
    public void A_focused_row_never_holds_back_the_lists_name_and_an_owed_name_is_paid_when_focus_moves()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "TNFDialog.xaml.cs"));
        string name = Body(src, "private void NameTheList()");
        Assert.Contains("RenameNow(TNFList.IsKeyboardFocused, _namingOnFocus, TNFList.Items.Count)", name);
        Assert.DoesNotContain("IsKeyboardFocusWithin", name);   // the guard that kept a stale count (Codex B1)
        int lost = src.IndexOf("TNFList.LostKeyboardFocus +=", StringComparison.Ordinal);
        Assert.True(lost > 0, "nothing pays an owed name when the list's own focus moves");
        string paid = src.Substring(lost, 300);
        Assert.Contains("_nameOwed", paid);
        Assert.Contains("AutomationProperties.SetName(TNFList, ListName(TNFList.Items.Count));", paid);

        // Add rebuilds, and so renames, before focus goes in; Add and Remove
        // then put focus on a row, which ends any owed state.
        string add = Body(src, "private void AddButton_Click(");
        int rebuild = add.IndexOf("RefreshList();", StringComparison.Ordinal);
        Assert.True(rebuild > 0 && add.IndexOf("FocusRow(", StringComparison.Ordinal) > rebuild);
        Assert.True(add.IndexOf("TNFList.Focus();", StringComparison.Ordinal) is int f && (f < 0 || f > add.IndexOf("if (display == null)", StringComparison.Ordinal) && f < rebuild),
            "the list must not take focus before the rebuild on a successful add");
        Assert.Contains("FocusRow(TNFList.SelectedIndex);", Body(src, "private void RemoveButton_Click("));
    }

    // ------------------------------------------------------------------
    // Home's Mode field says the mode as every other announcement does
    // ------------------------------------------------------------------

    private static FrequencyDisplay.DisplayField ModeField(string shown) =>
        new("Mode", 4, " ", "") { Label = "Mode", Text = shown };

    [Theory]
    [InlineData("DIGU")]
    [InlineData("DIGL")]
    [InlineData("SAM")]
    [InlineData("NFM")]
    public void Homes_mode_field_says_a_mangled_mode_in_words_and_keeps_its_letters_on_display(string mode)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = ModeField(mode);
        string heard = FrequencyDisplay.FormatFieldAnnouncement(field);
        Assert.Contains(Radios.ModeWords.Spoken(mode), heard);
        Assert.NotEqual(mode, Radios.ModeWords.Spoken(mode));   // positive control: this mode has words
        Assert.DoesNotContain(mode, heard);
        Assert.Equal(mode, field.Text);                          // display and braille unchanged
    }

    [Theory]
    [InlineData("USB")]
    [InlineData("CW")]
    [InlineData("RTTY")]
    public void Homes_mode_field_keeps_the_letters_for_the_others(string mode)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Contains(mode, FrequencyDisplay.FormatFieldAnnouncement(ModeField(mode)));
    }

    // ------------------------------------------------------------------
    // "Use My Transmit Audio Here" needs a chosen profile that still exists
    // ------------------------------------------------------------------

    [Fact]
    public void A_chosen_transmit_audio_profile_deleted_afterwards_greys_use_my_transmit_audio_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-repair1-680-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string op = "repair-one";
            var store = new Radios.AudioChainPresets();
            store.Presets.Add(new Radios.AudioChainPreset("Ragchew"));
            store.Presets.Add(new Radios.AudioChainPreset("Contest"));
            Assert.True(store.Save(dir, op));
            List<string> Names() => Radios.AudioChainPresets.Load(dir, op).Presets.ConvertAll(p => p.Name);

            // Chosen, and still saved: the row can be armed (the control).
            string chosen = "Ragchew";
            Assert.Null(JJFlexWpf.NativeMenuBar.UseMyTransmitAudioNotYet(chosen, Names()));
            Assert.Null(JJFlexWpf.NativeMenuBar.UseMyTransmitAudioNotYet("ragchew", Names()));   // matched ignoring case

            // Deleted in the Audio Workshop: removed from the store and saved,
            // as its delete does. The per-radio choice is untouched by that.
            var loaded = Radios.AudioChainPresets.Load(dir, op);
            loaded.Presets.RemoveAt(loaded.Presets.FindIndex(p => p.Name == chosen));
            Assert.True(loaded.Save(dir, op));
            string? why = JJFlexWpf.NativeMenuBar.UseMyTransmitAudioNotYet(chosen, Names());
            Assert.NotNull(why);
            Assert.Contains(chosen, why);
            Assert.Contains("no longer saved", why);

            // And with none saved at all.
            loaded = Radios.AudioChainPresets.Load(dir, op);
            loaded.Presets.Clear();
            Assert.True(loaded.Save(dir, op));
            Assert.Empty(Names());
            Assert.Equal(", no transmit audio profiles saved yet", JJFlexWpf.NativeMenuBar.UseMyTransmitAudioNotYet(chosen, Names()));
            Assert.Equal(", choose one under Which Transmit Audio first",
                JJFlexWpf.NativeMenuBar.UseMyTransmitAudioNotYet("", new[] { "Contest" }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Arming_it_anyway_never_promises_a_profile_that_is_not_saved()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "NativeMenuBar.cs"));
        string handler = Body(src, "private void SetGuestProfileIntent(");
        int gate = handler.IndexOf("if (UseMyTransmitAudioNotYet(chosen, saved) != null)", StringComparison.Ordinal);
        int promise = handler.IndexOf("settings.profile_guest.live_audio_chosen", StringComparison.Ordinal);
        Assert.True(gate > 0 && promise > gate, "the promise must sit behind the same check the row is greyed by");
        Assert.Contains("settings.profile_guest.why.local_profile_missing", handler);
    }

    // ------------------------------------------------------------------
    // Diagnostics: an answered crash report leaves the list when it is
    // answered, and the tab's retention sentences follow the setting
    // ------------------------------------------------------------------

    [Fact]
    public void The_diagnostics_tab_rereads_its_list_when_a_report_is_answered_and_lets_go_on_close()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "SettingsDialog.Diagnostics.cs"));
        string loaded = Body(src, "private void DiagnosticsPanel_Loaded(");
        int sub = loaded.IndexOf("Radios.CrashReportStore.VerdictRecorded += OnCrashVerdictRecorded;", StringComparison.Ordinal);
        int unsub = loaded.IndexOf("Radios.CrashReportStore.VerdictRecorded -= OnCrashVerdictRecorded;", StringComparison.Ordinal);
        Assert.True(sub > 0 && unsub > sub, "the tab must hear an answer while open, and stop when it closes");
        Assert.Contains("private void OnCrashVerdictRecorded() => OnDiagnosticStateChanged(null, EventArgs.Empty);", src);
        // The safe dispatch the other state changes already use (an upload
        // completes on its own thread); RefreshDiagnosticsTab rebuilds the list.
        string changed = Body(src, "private void OnDiagnosticStateChanged(");
        Assert.Contains("else Dispatcher.BeginInvoke(RefreshDiagnosticsTab);", changed);
        Assert.Contains("RefreshPendingCrashes();", Body(src, "private void RefreshDiagnosticsTab()"));
        // And its count stays true with focus in the list.
        string refresh = Body(src, "private void RefreshPendingCrashes()");
        Assert.Contains("ListNaming.RenameNow(DiagPendingCrashList.IsKeyboardFocused, false, _pendingCrashes.Count)", refresh);
        Assert.DoesNotContain("IsKeyboardFocusWithin", refresh);
    }

    [Fact]
    public void The_retention_sentences_on_the_tab_follow_the_setting_when_it_changes_and_when_the_tab_fills()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "SettingsDialog.Diagnostics.cs"));
        Assert.Contains("ShowRetentionSentences(Radios.CrashReportStore.PolicyFrom(stored));",
            Body(src, "private void DiagCrashRetentionCombo_SelectionChanged("));
        Assert.Contains("ShowRetentionSentences(Radios.CrashReportStore.PolicyFrom(retention));",
            Body(src, "private void RefreshDiagnosticsTab()"));
        string show = Body(src, "private void ShowRetentionSentences(");
        Assert.Contains("DiagDiskNoteText.Text = Radios.CrashReportStore.DiskNote(policy);", show);
        Assert.Contains("DescribeCrashReports", show);

        string xaml = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
        Assert.Contains("x:Name=\"DiagDiskNoteText\"", xaml);
        Assert.DoesNotContain("is never deleted automatically", xaml);   // the fixed promise is gone from the window
    }

    /// <summary>A method's body, by brace matching from its signature.</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);
        int open = src.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
