using System.ComponentModel;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A radio list row tells its bindings when to re-read its text (#1008), so
/// the printed text and the accessible name, both bound to
/// <see cref="RadioListItem.DisplayText"/>, follow the row.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> A plain data object only, on an MTA thread (asserted
/// first), where WPF refuses to build any element. No dialog, no window.
/// </remarks>
public sealed class RadioListItemTextTests
{
    [Fact]
    public void A_row_raises_its_text_changed_and_the_text_is_the_rows_current_state()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        var row = new RadioListItem { Serial = "4925-1213-8600-6245", Name = "Bench", ModelName = "FLEX-8600" };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        string before = row.DisplayText;
        row.LanAvailable = true;
        row.NotifyDisplayTextChanged();

        Assert.Equal(new[] { nameof(RadioListItem.DisplayText) }, raised);
        Assert.Equal(row.ToString(), row.DisplayText);   // the accessible fallback reads the same text
        Assert.NotEqual(before, row.DisplayText);        // control: the change is one the text shows
    }

    // ---- #405 J10: the account a connect would use ----

    private const string Chosen = "chosen-elsewhere@example.com";   // not saved on this computer
    private const string LastSeen = "last-seen@example.com";         // saved here
    private const string Selector = "working-with@example.com";

    /// <summary>
    /// The rule: the connect's own answer when it names one, otherwise the
    /// choice, then the observation, as before.
    /// </summary>
    [Fact]
    public void The_bound_account_is_the_one_a_connect_would_use_when_the_plan_names_one()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(LastSeen, RadioListItem.AccountInPlay(LastSeen, Chosen, LastSeen));
        Assert.Equal(Chosen, RadioListItem.AccountInPlay("", Chosen, LastSeen));
        Assert.Equal(LastSeen, RadioListItem.AccountInPlay(null, "", LastSeen));
        Assert.Equal("", RadioListItem.AccountInPlay("", "", ""));
        Assert.True(RadioListItem.BoundElsewhere(LastSeen, Selector));
        Assert.False(RadioListItem.BoundElsewhere(LastSeen, LastSeen.ToUpperInvariant()));
        Assert.False(RadioListItem.BoundElsewhere("", Selector));
    }

    /// <summary>
    /// Ruled by Noel 2026-11-09 20:52 (J10): the chosen account is not saved
    /// here and the last-seen one is, so the row names the saved last-seen
    /// account, and Enter, which looks up <see cref="RadioListItem.BoundAccount"/>,
    /// switches to it. The control: with no account from the plan the row
    /// names the chosen one, as today.
    /// </summary>
    [Fact]
    public void A_row_whose_chosen_account_is_not_saved_names_the_saved_last_seen_one()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        var row = new RadioListItem
        {
            Serial = "0000-0000-6300-0001", Name = "Shack", ModelName = "FLEX-6300",
            PreferredAccount = Chosen, LastSeenViaAccount = LastSeen, ConnectAccount = LastSeen,
            DiscoverySettled = true,   // past "checking": a row nothing can see now
        };
        row.ForeignAccount = RadioListItem.BoundElsewhere(row.BoundAccount, Selector);
        Assert.True(row.ForeignAccount);
        Assert.Equal(LastSeen, row.BoundAccount);
        Assert.Contains(LastSeen, row.DisplayText);
        Assert.DoesNotContain(Chosen, row.DisplayText);

        row.ConnectAccount = "";                         // the plan names none: today's words
        row.NotifyDisplayTextChanged();
        Assert.Equal(Chosen, row.BoundAccount);
        Assert.Contains(Chosen, row.DisplayText);
    }

    /// <summary>
    /// The roster paint asks the connect's answer per radio BEFORE it takes
    /// the list lock (ConnectAccountFor reads disk and the held sessions), and
    /// derives the foreign flag from the same rule as BoundAccount; choosing
    /// an account asks it again.
    /// </summary>
    [Fact]
    public void The_paint_asks_the_connects_answer_outside_the_list_lock()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs"))
            .Replace("\r\n", "\n");
        int paint = src.IndexOf("private void PaintRoster(", System.StringComparison.Ordinal);
        int ask = src.IndexOf("connectBySerial[k.Serial] = Radios.FlexBase.ConnectAccountFor(k.Serial);", paint, System.StringComparison.Ordinal);
        int theLock = src.IndexOf("lock (_radiosLock)", paint, System.StringComparison.Ordinal);
        Assert.True(paint > 0 && ask > paint && theLock > ask, "ConnectAccountFor must be asked before the list lock");
        Assert.Contains("RadioListItem.AccountInPlay(connect, k.PreferredAccount, k.LastSeenViaAccount), accountEmail);", src, System.StringComparison.Ordinal);
        Assert.Contains("radio.ConnectAccount = Radios.FlexBase.ConnectAccountFor(radio.Serial);", src, System.StringComparison.Ordinal);
        Assert.Contains("GetAccountByEmail(radio.BoundAccount)", src, System.StringComparison.Ordinal);
    }
}
