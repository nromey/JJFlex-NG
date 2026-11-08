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

    /// <summary>The dialog's commit, as RadioInfoDialog runs it.</summary>
    private static (string Shown, string? Refused) Commit(Radio radio, string shown, string? refused, string inBox, bool deliberate)
    {
        if (!deliberate && !RadioInfoDialog.CommitsOnFocusLoss(refused, inBox)) return (shown, refused);
        string after = RadioInfoDialog.CommitIdentity(shown, inBox, radio.Send, () => radio.Held,
            Radios.FlexBase.CallsignAsTheRadioKeepsIt);
        return (after, RadioInfoDialog.RefusedEdit(shown, inBox, after));
    }

    [Fact]
    public void A_refused_edit_is_said_once_not_on_every_focus_loss()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new Radio { Refusing = true };
        var (shown, refused) = Commit(radio, "W0OLD", null, "k0abc", deliberate: true);   // Enter: refused
        Assert.Single(radio.Sent);
        (shown, refused) = Commit(radio, shown, refused, "k0abc", deliberate: false);       // Tab away
        (shown, refused) = Commit(radio, shown, refused, "k0abc", deliberate: false);       // and again
        Assert.Single(radio.Sent);
        Assert.Equal("W0OLD", shown);
    }

    [Fact]
    public void Enter_still_tries_a_refused_edit_again_and_a_new_edit_is_sent_on_focus_loss()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var radio = new Radio { Refusing = true };
        var (shown, refused) = Commit(radio, "W0OLD", null, "k0abc", deliberate: false);   // focus loss: refused
        radio.Refusing = false;                                                             // the hold is lifted
        (shown, refused) = Commit(radio, shown, refused, "k0abc", deliberate: true);        // Enter tries again
        Assert.Equal(new[] { "k0abc", "k0abc" }, radio.Sent);
        Assert.Equal("k0abc", shown);
        Assert.Null(refused);

        radio.Refusing = true;
        (shown, refused) = Commit(radio, shown, refused, "k0xyz", deliberate: false);       // a different edit is sent
        Assert.Equal(3, radio.Sent.Count);
    }

    [Fact]
    public void The_dialog_commits_on_focus_loss_only_through_the_refusal_check()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "RadioInfoDialog.xaml.cs"));
        Assert.Contains("CallBox_LostFocus(object sender, RoutedEventArgs e) => CommitCallsignIfEdited(deliberate: false);", src);
        Assert.Contains("NameBox_LostFocus(object sender, RoutedEventArgs e) => CommitNicknameIfEdited(deliberate: false);", src);
        Assert.Contains("if (!deliberate && !CommitsOnFocusLoss(_refusedCallsign, CallBox.Text)) return;", src);
        Assert.Contains("if (!deliberate && !CommitsOnFocusLoss(_refusedNickname, NameBox.Text)) return;", src);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
