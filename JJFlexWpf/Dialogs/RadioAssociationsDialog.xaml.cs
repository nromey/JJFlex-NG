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
        public override string ToString()
        {
            var name = string.IsNullOrWhiteSpace(Entry.Nickname)
                ? Lexicon.Get("settings.associations.unnamed_radio") : Entry.Nickname;
            var model = string.IsNullOrWhiteSpace(Entry.Model)
                ? Lexicon.Get("settings.associations.unknown_model") : Entry.Model;

            string account;
            if (!string.IsNullOrWhiteSpace(Entry.PreferredAccount))
                account = Lexicon.Get("settings.associations.preferred_account",
                    ("account", Entry.PreferredAccount));
            else if (!string.IsNullOrWhiteSpace(Entry.LastSeenViaAccount))
                account = Lexicon.Get("settings.associations.registered_to",
                    ("account", Entry.LastSeenViaAccount));
            else
                account = Lexicon.Get("settings.associations.automatic_lowercase");

            // Orphan-and-show: a binding to an account this machine no longer
            // holds is stated, not hidden and not silently cleared.
            var bound = Entry.ResolvedAccount;
            if (!string.IsNullOrWhiteSpace(bound)
                && FlexBase.SharedAccountManager.GetAccountByEmail(bound) == null)
            {
                account += Lexicon.Get("settings.associations.account_not_on_this_computer");
            }

            return Lexicon.Get("settings.associations.row",
                ("name", name), ("model", model), ("account", account));
        }
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
            RadioList.Items.Add(new RadioRow { Entry = entry });
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
