using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The Radio Associations row's account half (#405, ruled by Noel 2026-11-08
/// ~16:28): it names the account connecting will actually use when the
/// connect's plan names one, and keeps today's words when it names none.
/// </summary>
/// <remarks>
/// <b>Windowless.</b> A static decision on the dialog type; no dialog is
/// built. Asserts an MTA thread, where WPF refuses to build any element.
/// Example accounts only.
/// </remarks>
public sealed class AssociationsAccountWordsTests
{
    private const string Chosen = "chosen@example.com";
    private const string Seen = "seen@example.com";

    public AssociationsAccountWordsTests() => Lexicon.Load(Lexicon.Partitions);

    private static string Preferred(string a) => Lexicon.Get("settings.associations.preferred_account", ("account", a));
    private static string Registered(string a) => Lexicon.Get("settings.associations.registered_to", ("account", a));

    [Fact]
    public void A_chosen_account_not_saved_here_names_the_saved_one_connecting_uses()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // Before: "preferred account chosen@..., not on this computer".
        string words = RadioAssociationsDialog.AccountWords(Seen, Chosen, Seen, boundNotSaved: true);
        Assert.Equal(Registered(Seen), words);
        Assert.DoesNotContain(Chosen, words);
    }

    [Fact]
    public void A_connect_account_that_is_the_chosen_one_is_said_as_the_choice()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(Preferred(Chosen), RadioAssociationsDialog.AccountWords(Chosen, Chosen, Seen, boundNotSaved: false));
    }

    [Fact]
    public void With_no_plan_account_the_row_keeps_todays_words_and_its_note()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string note = Lexicon.Get("settings.associations.account_not_on_this_computer");
        Assert.Equal(Preferred(Chosen) + note, RadioAssociationsDialog.AccountWords("", Chosen, Seen, boundNotSaved: true));
        Assert.Equal(Registered(Seen), RadioAssociationsDialog.AccountWords("", "", Seen, boundNotSaved: false));
        Assert.Equal(Lexicon.Get("settings.associations.automatic_lowercase"),
            RadioAssociationsDialog.AccountWords("", "", "", boundNotSaved: false));
    }

    private static string NotListedNow => Lexicon.Get("settings.associations.not_listed_now");

    /// <summary>
    /// #405, night question J6 (ruled by Noel 2026-11-11 11:54): when every
    /// saved account has answered and none lists the radio, the row names the
    /// saved account the radio list names, marked not listed right now: as
    /// the account it was last seen under, or as the chosen account when it is
    /// the chosen one. Never "registered to", and no not-on-this-computer note
    /// (that account is saved here).
    /// </summary>
    [Fact]
    public void With_every_saved_list_answered_without_it_the_row_names_the_saved_account_as_not_listed()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string seen = RadioAssociationsDialog.AccountWords("", Chosen, Seen, boundNotSaved: true, notListedAccount: Seen);
        Assert.Equal(Lexicon.Get("settings.associations.last_seen_under", ("account", Seen)) + NotListedNow, seen);
        Assert.DoesNotContain(Registered(Seen), seen);
        Assert.DoesNotContain(Chosen, seen);
        Assert.DoesNotContain(Lexicon.Get("settings.associations.account_not_on_this_computer"), seen);

        Assert.Equal(Preferred(Chosen) + NotListedNow,
            RadioAssociationsDialog.AccountWords("", Chosen, Seen, boundNotSaved: false, notListedAccount: Chosen));

        // Control: with no such account the row keeps its words.
        Assert.Equal(Registered(Seen), RadioAssociationsDialog.AccountWords("", "", Seen, boundNotSaved: false, notListedAccount: ""));
    }

    /// <summary>
    /// The row asks the radio list's own resolver once, and takes its account
    /// as not listed only when that resolver says every saved list ruled the
    /// radio out: one rule for both windows, never a second.
    /// </summary>
    [Fact]
    public void The_row_reads_the_radio_lists_resolver()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "JJFlexWpf", "Dialogs", "RadioAssociationsDialog.xaml.cs"));
        Assert.Contains("string account = FlexBase.RadioListAccountFor(serial, out bool everySavedListRuledItOut);", src, System.StringComparison.Ordinal);
        Assert.Contains("return everySavedListRuledItOut ? (\"\", account) : (account, \"\");", src, System.StringComparison.Ordinal);
        Assert.Contains("var (connect, notListed) = RowAccounts(entry.Serial ?? \"\");", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("FlexBase.ConnectAccountFor(entry.Serial", src, System.StringComparison.Ordinal);
        Assert.Equal(("", ""), RadioAssociationsDialog.RowAccounts(""));
    }
}
