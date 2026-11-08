using System;
using System.Collections.Generic;
using System.IO;
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
