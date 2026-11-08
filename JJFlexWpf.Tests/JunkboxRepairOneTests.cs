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
        Assert.Equal(now, TNFDialog.RenameNow(listItselfFocused, namingOnFocus, rows));
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

    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);
        return src.Substring(at, src.IndexOf("\n    }", at, StringComparison.Ordinal) - at);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
