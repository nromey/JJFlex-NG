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
}
