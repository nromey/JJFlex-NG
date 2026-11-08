using System;
using System.Collections.Generic;
using System.IO;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The tracking notch list says what it holds and how many (#941), the notch
/// width is said through the one width formatter (#983), and Radio Info no
/// longer re-sends, and so re-speaks, a refused name or callsign every time
/// focus leaves the box (#983).
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> MTA thread throughout: pure functions and
/// source reads; no dialog is built.
/// </remarks>
public sealed class NotchListAndIdentityRefusalTests
{
    [Fact]
    public void The_notch_list_names_its_count_and_never_as_a_position()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string none = TNFDialog.ListName(0);
        string three = TNFDialog.ListName(3);
        Assert.Contains("none", none, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", three);
        Assert.Contains("notches", three, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\b\d+\s+of\s+\d+\b", three);
        Assert.NotEqual(TNFDialog.ListName(1), TNFDialog.ListName(2));
    }

    [Fact]
    public void The_list_is_renamed_after_every_rebuild_and_the_width_goes_through_the_formatter()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "TNFDialog.xaml.cs"));
        int refresh = src.IndexOf("private void RefreshList()", StringComparison.Ordinal);
        int next = src.IndexOf("UpdateTNFProperties();", refresh, StringComparison.Ordinal);
        Assert.Contains("NameTheList();", src.Substring(refresh, next - refresh));
        int adjust = src.IndexOf("private void AdjustWidth(", StringComparison.Ordinal);
        string body = src.Substring(adjust, src.IndexOf("\n    }", adjust, StringComparison.Ordinal) - adjust);
        Assert.Contains("Radios.HertzSpeech.Width(newVal,", body);
        Assert.DoesNotContain("SpeakValue(WidthLabel, newVal.ToString())", body);
    }

    /// <summary>A stand-in radio that refuses while <see cref="Refusing"/> is set.</summary>
    private sealed class Radio
    {
        public string Held = "W0OLD";
        public bool Refusing;
        public readonly List<string> Sent = new();
        public void Send(string typed)
        {
            Sent.Add(typed);   // every attempt is a refusal said aloud when Refusing
            if (!Refusing) Held = Radios.FlexBase.CallsignAsTheRadioKeepsIt(typed);
        }
    }

    /// <summary>The dialog's commit, through the decision RadioInfoDialog
    /// runs: the new baseline and what the box shows. Enter and focus loss
    /// commit the same way.</summary>
    private static (string Shown, string Box) Commit(Radio radio, string shown, string inBox)
        => RadioInfoDialog.CommitAndShow(shown, inBox, radio.Send, () => radio.Held,
            Radios.FlexBase.CallsignAsTheRadioKeepsIt);

    [Fact]
    public void A_refused_edit_is_said_once_and_the_box_goes_back_to_the_radios_value()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new Radio { Refusing = true };
        var (shown, box) = Commit(radio, "W0OLD", "k0abc");            // Enter or Tab: refused
        Assert.Single(radio.Sent);
        Assert.Equal("W0OLD", box);                                   // the box says what the radio has
        Assert.Equal("W0OLD", shown);
        (shown, box) = Commit(radio, shown, box);                     // Tab away again
        (shown, box) = Commit(radio, shown, box);                     // and again
        Assert.Single(radio.Sent);                                    // nothing more sent, nothing more said
    }

    [Fact]
    public void A_refused_box_shows_the_radios_value_even_when_it_moved_underneath()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new Radio { Refusing = true, Held = "W0NEW" };   // the radio changed since the dialog filled
        var (shown, box) = Commit(radio, "W0OLD", "k0abc");
        Assert.Equal("W0NEW", box);
        Assert.Equal("W0NEW", shown);
    }

    [Fact]
    public void A_taken_edit_stays_and_a_new_edit_after_a_refusal_is_sent()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new Radio { Refusing = true };
        var (shown, box) = Commit(radio, "W0OLD", "k0abc");           // refused, box back to W0OLD
        radio.Refusing = false;                                       // the hold is lifted
        (shown, box) = Commit(radio, shown, "k0abc");                 // typed again
        Assert.Equal(new[] { "k0abc", "k0abc" }, radio.Sent);
        Assert.Equal("k0abc", box);
        Assert.Equal("k0abc", shown);
    }

    [Fact]
    public void The_dialog_commits_through_the_decision_and_shows_what_it_returns()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "RadioInfoDialog.xaml.cs"));
        Assert.Contains("CallBox_LostFocus(object sender, RoutedEventArgs e) => CommitCallsignIfEdited();", src);
        Assert.Contains("NameBox_LostFocus(object sender, RoutedEventArgs e) => CommitNicknameIfEdited();", src);
        Assert.Contains("if (!string.Equals(CallBox.Text, box, StringComparison.Ordinal)) CallBox.Text = box;", src);
        Assert.Contains("if (!string.Equals(NameBox.Text, box, StringComparison.Ordinal)) NameBox.Text = box;", src);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
