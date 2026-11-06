using System;
using System.Collections.Generic;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Radio Info tells the radio a callsign or a name only when the operator
/// changed that box in the dialog (#477, repair round 2, Codex B2). It used to
/// compare the box with the LIVE value on focus loss, so a box still holding
/// a value the radio had since corrected underneath was sent back to the
/// radio with nobody having typed anything. Windowless: the decision is a
/// pure function; no dialog is built.
/// </summary>
public sealed class RadioInfoIdentityEditTests
{
    [Fact]
    public void An_untouched_box_is_never_sent_even_when_the_radio_has_moved_on()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // Shown W0OLD when the dialog opened; the radio has since reported
        // K0NEW; the operator typed nothing. The old rule compared the box with
        // K0NEW and sent W0OLD back.
        Assert.Null(RadioInfoDialog.EditedIdentity(shown: "W0OLD", inBox: "W0OLD"));
    }

    [Fact]
    public void A_box_the_operator_changed_is_sent_as_typed()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal("K0ABC", RadioInfoDialog.EditedIdentity(shown: "W0OLD", inBox: "K0ABC"));
        Assert.Equal("", RadioInfoDialog.EditedIdentity(shown: "W0OLD", inBox: ""));
    }

    [Fact]
    public void Nothing_shown_and_nothing_typed_is_not_an_edit()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Null(RadioInfoDialog.EditedIdentity(shown: "", inBox: ""));
        Assert.Null(RadioInfoDialog.EditedIdentity(shown: null!, inBox: ""));
    }

    // ------------------------------------------------------------------
    // Repair round 3, Codex F3: the baseline moves only when the edit took
    // ------------------------------------------------------------------

    /// <summary>A stand-in radio: holds a callsign, and refuses while <see cref="Refusing"/> is set.</summary>
    private sealed class Radio
    {
        public string Held = "W0OLD";
        public bool Refusing;
        public readonly List<string> Sent = new();
        public void Send(string typed)
        {
            if (Refusing) return;
            Sent.Add(typed);
            Held = Radios.FlexBase.CallsignAsTheRadioKeepsIt(typed);
        }
        public string Commit(string shown, string inBox) => RadioInfoDialog.CommitIdentity(
            shown, inBox, Send, () => Held, Radios.FlexBase.CallsignAsTheRadioKeepsIt);
    }

    [Fact]
    public void A_refused_edit_stays_pending_and_the_same_text_is_sent_when_tried_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new Radio { Refusing = true };
        string shown = radio.Commit("W0OLD", "k0abc");
        Assert.Equal("W0OLD", shown);           // refused: still pending
        Assert.Empty(radio.Sent);

        radio.Refusing = false;
        shown = radio.Commit(shown, "k0abc");    // the same text, Enter again
        Assert.Equal(new[] { "k0abc" }, radio.Sent);
        Assert.Equal("k0abc", shown);           // took: the baseline is the edit

        shown = radio.Commit(shown, "k0abc");    // and once taken, not sent again
        Assert.Equal(new[] { "k0abc" }, radio.Sent);
    }

    [Fact]
    public void An_untouched_box_sends_nothing_whatever_the_radio_now_holds()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new Radio { Held = "K0NEW" };   // the radio moved on underneath
        Assert.Equal("W0OLD", radio.Commit("W0OLD", "W0OLD"));
        Assert.Empty(radio.Sent);
    }

    [Fact]
    public void An_edit_the_radio_already_holds_is_taken_without_sending()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // FlexLib's own setter sends nothing for a value it already holds.
        string shown = RadioInfoDialog.CommitIdentity("W0OLD", "k0new", _ => { }, () => "K0NEW",
            Radios.FlexBase.CallsignAsTheRadioKeepsIt);
        Assert.Equal("k0new", shown);
    }

    [Fact]
    public void A_name_is_compared_the_way_the_radio_keeps_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal("NewShack", Radios.FlexBase.NicknameAsTheRadioKeepsIt("  New Shack! "));
        Assert.Equal("K0ABC", Radios.FlexBase.CallsignAsTheRadioKeepsIt("k0 abc"));
        string shown = RadioInfoDialog.CommitIdentity("OldShack", " New Shack ", _ => { }, () => "NewShack",
            Radios.FlexBase.NicknameAsTheRadioKeepsIt);
        Assert.Equal(" New Shack ", shown);
    }
}
