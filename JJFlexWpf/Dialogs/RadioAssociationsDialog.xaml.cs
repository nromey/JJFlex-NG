#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Phase 2 of the unified roster: the SmartLink Account Manager's view of
/// per-radio account binding — the door for thinking about an ACCOUNT,
/// where the radio selector's context menu is the door for thinking about
/// a RADIO. Both write the same per-serial PreferredAccount.
///
/// May view and rebind; must NEVER initiate a connect (ratified boundary —
/// connecting lives in exactly one surface). This is also the only surface
/// that can reach an orphan: a radio bound to an account that no longer
/// exists may not appear in the selector at all once its account is gone.
/// What happens to bound radios on account DELETION is deliberately not
/// implemented here — that rule is not yet ratified; orphans simply show
/// with "not saved on this computer" until Noel decides.
/// </summary>
public partial class RadioAssociationsDialog : JJFlexDialog
{
    private sealed class RadioRow
    {
        public KnownRadioEntry Entry { get; init; } = null!;

        /// <summary>
        /// The account a connect to this radio would use, by the connect's own
        /// plan, read once when the list loads; empty when the plan names none
        /// (#405).
        /// </summary>
        public string ConnectAccount { get; init; } = "";

        /// <summary>
        /// The saved account the radio list binds this radio's row to when
        /// every saved account's list has answered without the radio; empty
        /// otherwise (#405, night question J6, ruled 2026-11-11 11:54). Read
        /// with <see cref="ConnectAccount"/>, from the radio list's own
        /// resolver, so the two windows agree.
        /// </summary>
        public string NotListedAccount { get; init; } = "";

        public override string ToString()
        {
            var name = string.IsNullOrWhiteSpace(Entry.Nickname)
                ? Lexicon.Get("settings.associations.unnamed_radio") : Entry.Nickname;
            var model = string.IsNullOrWhiteSpace(Entry.Model)
                ? Lexicon.Get("settings.associations.unknown_model") : Entry.Model;

            var bound = Entry.ResolvedAccount;
            string account = AccountWords(ConnectAccount, Entry.PreferredAccount, Entry.LastSeenViaAccount,
                boundNotSaved: !string.IsNullOrWhiteSpace(bound)
                    && FlexBase.SharedAccountManager.GetAccountByEmail(bound) == null,
                notListedAccount: NotListedAccount);

            return Lexicon.Get("settings.associations.row",
                ("name", name), ("model", model), ("account", account));
        }
    }

    /// <summary>
    /// The account half of a row (#405). Ruled by Noel 2026-11-08 ~16:28: the
    /// row names "the account connecting will actually use (the first
    /// matching account saved on this computer), not the one chosen or last
    /// seen". So when the connect's plan names an account, the row names THAT
    /// one, said as the radio's chosen account when it is the chosen one and
    /// as registered to otherwise. When the plan names none (no saved
    /// account lists the radio, or the connect would sign in through the
    /// saved default), the row keeps today's words, chosen, then last seen,
    /// then automatic, with the not-on-this-computer note beside a binding
    /// this machine does not hold: DRAFT for Noel's sheet, because naming an
    /// account there would name one connecting will not use.
    /// </summary>
    /// <remarks>
    /// <para><b>When every saved list has answered without the radio</b>
    /// (<paramref name="notListedAccount"/> named; ruled by Noel 2026-11-11
    /// 11:54, night question J6), the row names the saved account the radio
    /// list names for it, marked as not listed right now, so the two windows
    /// agree: said as the chosen account when it is the chosen one, and as
    /// the account it was last seen under otherwise. It carries no
    /// not-on-this-computer note, because that account is saved here by
    /// definition. DRAFT wording, for Codex.</para>
    /// </remarks>
    internal static string AccountWords(string connectAccount, string preferred, string lastSeen, bool boundNotSaved,
        string notListedAccount = "")
    {
        if (!string.IsNullOrWhiteSpace(notListedAccount))
        {
            string named = string.Equals(notListedAccount, preferred, StringComparison.OrdinalIgnoreCase)
                ? Lexicon.Get("settings.associations.preferred_account", ("account", notListedAccount))
                : Lexicon.Get("settings.associations.last_seen_under", ("account", notListedAccount));
            return named + Lexicon.Get("settings.associations.not_listed_now");
        }

        if (!string.IsNullOrWhiteSpace(connectAccount))
        {
            return string.Equals(connectAccount, preferred, StringComparison.OrdinalIgnoreCase)
                ? Lexicon.Get("settings.associations.preferred_account", ("account", connectAccount))
                : Lexicon.Get("settings.associations.registered_to", ("account", connectAccount));
        }

        string account;
        if (!string.IsNullOrWhiteSpace(preferred))
            account = Lexicon.Get("settings.associations.preferred_account", ("account", preferred));
        else if (!string.IsNullOrWhiteSpace(lastSeen))
            account = Lexicon.Get("settings.associations.registered_to", ("account", lastSeen));
        else
            account = Lexicon.Get("settings.associations.automatic_lowercase");

        // Orphan-and-show: a binding to an account this machine no longer
        // holds is stated, not hidden and not silently cleared.
        if (boundNotSaved)
            account += Lexicon.Get("settings.associations.account_not_on_this_computer");
        return account;
    }

    /// <summary>
    /// A row's two accounts, from the radio list's own resolver
    /// (<see cref="FlexBase.RadioListAccountFor(string, out bool, IEnumerable{string})"/>),
    /// asked once: the account a connect would use, or, when every saved
    /// account's list has answered without the radio, the saved account the
    /// radio list names for it instead (#405 J6). Never a second rule: the
    /// resolver's answer outside that state is the connect's own plan
    /// (<see cref="FlexBase.ConnectAccountFor"/>).
    /// </summary>
    internal static (string Connect, string NotListed) RowAccounts(string serial)
    {
        string account = FlexBase.RadioListAccountFor(serial, out bool everySavedListRuledItOut);
        return everySavedListRuledItOut ? ("", account) : (account, "");
    }

    private static string AutomaticLabel => Lexicon.Get("settings.associations.automatic_label");

    public RadioAssociationsDialog()
    {
        InitializeComponent();
        ReloadRadios();
    }

    private void ReloadRadios(string? keepSerial = null)
    {
        List<KnownRadioEntry> known;
        try
        {
            known = KnownRadioRoster.Load();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"RadioAssociations: {ex.Message}");
            known = new List<KnownRadioEntry>();
        }

        RadioList.Items.Clear();
        foreach (var entry in known)
        {
            var (connect, notListed) = RowAccounts(entry.Serial ?? "");
            RadioList.Items.Add(new RadioRow
            {
                Entry = entry,
                ConnectAccount = connect,
                NotListedAccount = notListed,
            });
        }

        if (RadioList.Items.Count == 0)
        {
            RadioList.Items.Add(Lexicon.Get("settings.associations.none_known"));
            return;
        }

        int index = 0;
        if (!string.IsNullOrWhiteSpace(keepSerial))
        {
            for (int i = 0; i < RadioList.Items.Count; i++)
            {
                if (RadioList.Items[i] is RadioRow r
                    && string.Equals(r.Entry.Serial, keepSerial, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
        }
        RadioList.SelectedIndex = index;
    }

    private void RadioList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = RadioList.SelectedItem as RadioRow;
        bool haveRadio = row != null;

        AccountCombo.Items.Clear();
        AccountCombo.IsEnabled = haveRadio;
        BindButton.IsEnabled = haveRadio;
        if (row == null) return;

        AccountCombo.Items.Add(AutomaticLabel);
        int selected = 0;
        foreach (var acct in FlexBase.SharedAccountManager.Accounts)
        {
            if (string.IsNullOrWhiteSpace(acct.Email)) continue;
            AccountCombo.Items.Add(acct.Email);
            if (string.Equals(acct.Email, row.Entry.PreferredAccount, StringComparison.OrdinalIgnoreCase))
                selected = AccountCombo.Items.Count - 1;
        }
        AccountCombo.SelectedIndex = selected;
    }

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        if (RadioList.SelectedItem is not RadioRow row) return;

        var choice = AccountCombo.SelectedItem as string ?? AutomaticLabel;
        var email = string.Equals(choice, AutomaticLabel, StringComparison.OrdinalIgnoreCase) ? "" : choice;

        if (!KnownRadioRoster.SetPreferredAccount(row.Entry.Serial, email))
        {
            ScreenReaderOutput.Speak(
                Lexicon.Get("settings.associations.save_failed"),
                interrupt: true);
            return;
        }

        var name = string.IsNullOrWhiteSpace(row.Entry.Nickname) ? row.Entry.Serial : row.Entry.Nickname;
        ScreenReaderOutput.Speak(
            string.IsNullOrWhiteSpace(email)
                ? Lexicon.Get("settings.associations.preference_cleared", ("name", name))
                : Lexicon.Get("settings.associations.will_connect_as", ("name", name), ("email", email)),
            interrupt: true);

        ReloadRadios(row.Entry.Serial);
        RadioList.Focus();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
