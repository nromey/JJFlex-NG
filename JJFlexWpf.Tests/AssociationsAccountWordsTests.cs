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
}
