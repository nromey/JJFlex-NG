using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Represents a SmartLink account for display.
    /// </summary>
    public class SmartLinkAccountInfo
    {
        public string FriendlyName { get; set; } = "";
        public string Email { get; set; } = "";
        public DateTime LastUsed { get; set; }
        public object AccountData { get; set; } = null!;
        public bool IsDefault { get; set; }

        /// <summary>Remote-first startup flag for this account.</summary>
        public bool AutoStartRemote { get; set; }

        public override string ToString()
        {
            string lastUsed = LastUsed > DateTime.MinValue
                ? LastUsed.ToLocalTime().ToString("g")
                : Radios.Lexicon.Get("connect.smartlink.account.never_used");
            string defaultTag = IsDefault ? Radios.Lexicon.Get("connect.smartlink.account.default_tag") : "";
            // Accounts whose friendly name IS the email (the default when no
            // name was chosen) read as "email (email)" through a screen
            // reader — say it once.
            string identity = string.Equals(FriendlyName, Email, StringComparison.OrdinalIgnoreCase)
                ? Email
                : Radios.Lexicon.Get("connect.smartlink.account.identity",
                    ("friendlyName", FriendlyName), ("email", Email));
            return Radios.Lexicon.Get("connect.smartlink.account.line",
                ("identity", identity), ("defaultTag", defaultTag), ("lastUsed", lastUsed));
        }
    }

    /// <summary>
    /// Callbacks for the SmartLink account dialog.
    /// </summary>
    public class SmartLinkAccountCallbacks
    {
        /// <summary>Returns the list of accounts, ordered by most recently used.</summary>
        public required Func<List<SmartLinkAccountInfo>> GetAccounts { get; init; }

        /// <summary>Rename an account. Returns true if successful.</summary>
        public required Func<string, string, bool> RenameAccount { get; init; }

        /// <summary>Delete an account by friendly name.</summary>
        public required Action<string> DeleteAccount { get; init; }

        /// <summary>
        /// Clear an account's saved sign-in data by friendly name, keeping the
        /// account and its settings. Returns true if the account was found.
        /// Optional so older callers compile unchanged; the button hides when
        /// this is not wired.
        /// </summary>
        public Func<string, bool>? ResetAccountSignIn { get; init; }

        /// <summary>
        /// Persist the remote-first startup flag for an account (friendly
        /// name, enabled). Optional; the checkbox hides when not wired.
        /// </summary>
        public Action<string, bool>? SetAutoStartRemote { get; init; }

        /// <summary>
        /// Start fresh with SmartLink: clear the saved sign-in for EVERY
        /// account, returning how many were cleared. The dialog follows a
        /// successful clear by requesting a clean sign-in (NewLoginRequested).
        /// Optional; the button hides when not wired. QB Track A, 2026-08-07.
        /// </summary>
        public Func<int>? StartFreshAllAccounts { get; init; }

        /// <summary>Screen reader speak delegate (message, interrupt).</summary>
        public Action<string, bool>? ScreenReaderSpeak { get; init; }
    }

    public partial class SmartLinkAccountDialog : JJFlexDialog
    {
        private readonly SmartLinkAccountCallbacks _callbacks;

        /// <summary>
        /// The selected account data, or null if cancelled or new login.
        /// </summary>
        public object? SelectedAccountData { get; private set; }

        /// <summary>
        /// True if user clicked "New Login".
        /// </summary>
        public bool NewLoginRequested { get; private set; }

        /// <summary>
        /// True if the user clicked "Create Account" — the caller opens the
        /// native signup dialog, then routes into sign-in on success.
        /// </summary>
        public bool CreateAccountRequested { get; private set; }

        /// <summary>
        /// True if the user clicked "Use Now" — use <see cref="SelectedAccountData"/>
        /// for this session only, without touching the saved default.
        /// </summary>
        public bool UseOnceRequested { get; private set; }

        /// <summary>
        /// Suppresses the AutoStartRemoteCheck Checked/Unchecked handler while
        /// we sync the box from the newly selected account — WPF raises those
        /// events on every programmatic IsChecked write, and without the guard
        /// each arrow-key move through the list would re-save (and announce)
        /// a setting the user never touched.
        /// </summary>
        private bool _suppressAutoStartEvent;

        public SmartLinkAccountDialog(SmartLinkAccountCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            InitializeComponent();
            LoadAccounts();
        }

        private void LoadAccounts()
        {
            AccountListBox.Items.Clear();
            var accounts = _callbacks.GetAccounts();
            foreach (var account in accounts)
                AccountListBox.Items.Add(account);

            if (AccountListBox.Items.Count > 0)
                AccountListBox.SelectedIndex = 0;

            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            bool hasSelection = AccountListBox.SelectedIndex >= 0;
            ConnectButton.IsEnabled = hasSelection;
            UseNowButton.IsEnabled = hasSelection;
            RenameButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
            // Hidden, not disabled, when the caller didn't wire it — an
            // always-gray button with no path to enablement is tab-order noise.
            ResetSignInButton.Visibility = _callbacks.ResetAccountSignIn != null
                ? Visibility.Visible : Visibility.Collapsed;
            ResetSignInButton.IsEnabled = hasSelection && _callbacks.ResetAccountSignIn != null;

            // Same hide-when-unwired rule as Reset Sign-In. Start Fresh works
            // on the whole list, so it needs accounts, not a selection.
            StartFreshButton.Visibility = _callbacks.StartFreshAllAccounts != null
                ? Visibility.Visible : Visibility.Collapsed;
            StartFreshButton.IsEnabled = _callbacks.StartFreshAllAccounts != null
                && AccountListBox.Items.Count > 0;

            AutoStartRemoteCheck.Visibility = _callbacks.SetAutoStartRemote != null
                ? Visibility.Visible : Visibility.Collapsed;
            AutoStartRemoteCheck.IsEnabled = hasSelection && _callbacks.SetAutoStartRemote != null;

            // Reflect the SELECTED account's flag without treating the
            // programmatic write as a user toggle.
            _suppressAutoStartEvent = true;
            AutoStartRemoteCheck.IsChecked = GetSelectedAccount()?.AutoStartRemote == true;
            _suppressAutoStartEvent = false;
        }

        private void AutoStartRemoteCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoStartEvent) return;
            var item = GetSelectedAccount();
            if (item == null || _callbacks.SetAutoStartRemote == null) return;

            bool enabled = AutoStartRemoteCheck.IsChecked == true;
            item.AutoStartRemote = enabled;
            _callbacks.SetAutoStartRemote(item.FriendlyName, enabled);
            // #128 sweep audit (2026-08-21): immediate-apply operator toggle
            // answers back. Tone before the sentence, per the sweep's ordering.
            EarconPlayer.ToggleTone(enabled);
            _callbacks.ScreenReaderSpeak?.Invoke(Radios.Lexicon.Get(enabled
                ? "connect.smartlink.account.autostart_on"
                : "connect.smartlink.account.autostart_off",
                ("friendlyName", item.FriendlyName)), true);
        }

        private SmartLinkAccountInfo? GetSelectedAccount()
        {
            return AccountListBox.SelectedItem as SmartLinkAccountInfo;
        }

        private void AccountListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtonStates();
        }

        private void AccountListBox_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AccountListBox.SelectedIndex >= 0)
                ConnectButton_Click(sender, e);
        }

        private void AccountListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && AccountListBox.SelectedIndex >= 0)
            {
                ConnectButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && AccountListBox.SelectedIndex >= 0)
            {
                DeleteButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedAccount();
            if (item != null)
            {
                SelectedAccountData = item.AccountData;
                NewLoginRequested = false;
                UseOnceRequested = false;
                DialogResult = true;
                Close();
            }
        }

        private void UseNowButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedAccount();
            if (item != null)
            {
                SelectedAccountData = item.AccountData;
                NewLoginRequested = false;
                UseOnceRequested = true;
                DialogResult = true;
                Close();
            }
        }

        private void NewLoginButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAccountData = null;
            NewLoginRequested = true;
            DialogResult = true;
            Close();
        }

        private void CreateAccountButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAccountData = null;
            CreateAccountRequested = true;
            DialogResult = true;
            Close();
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedAccount();
            if (item == null) return;

            var renameDialog = new RenameAccountDialog(item.FriendlyName);
            renameDialog.Owner = this;

            if (renameDialog.ShowDialog() == true)
            {
                var newName = renameDialog.NewName;
                if (!string.IsNullOrWhiteSpace(newName) && newName != item.FriendlyName)
                {
                    if (_callbacks.RenameAccount(item.FriendlyName, newName))
                    {
                        LoadAccounts();
                        _callbacks.ScreenReaderSpeak?.Invoke(
                            Radios.Lexicon.Get("connect.smartlink.account.renamed", ("newName", newName)), true);
                    }
                    else
                    {
                        AdvisoryDialog.Show(Radios.Lexicon.Get("connect.smartlink.account.rename_failed_title"),
                            Radios.Lexicon.Get("connect.smartlink.account.rename_failed_body"));
                    }
                }
            }
        }

        private void ResetSignInButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedAccount();
            if (item == null || _callbacks.ResetAccountSignIn == null) return;

            string who = string.Equals(item.FriendlyName, item.Email, StringComparison.OrdinalIgnoreCase)
                ? item.Email
                : Radios.Lexicon.Get("connect.smartlink.account.reset_who_quoted",
                    ("friendlyName", item.FriendlyName), ("email", item.Email));
            var confirm = new ConfirmActionDialog(
                Radios.Lexicon.Get("connect.smartlink.account.reset_title"),
                Radios.Lexicon.Get("connect.smartlink.account.reset_body", ("who", who)),
                question: Radios.Lexicon.Get("connect.smartlink.account.reset_question"),
                yesLabel: Radios.Lexicon.Get("connect.smartlink.account.reset_yes"));

            if (confirm.ShowDialog() != true) return;

            if (_callbacks.ResetAccountSignIn(item.FriendlyName))
            {
                LoadAccounts();
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Radios.Lexicon.Get("connect.smartlink.account.reset_done",
                        ("friendlyName", item.FriendlyName)), true);
            }
            else
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Radios.Lexicon.Get("connect.smartlink.account.reset_failed"), true);
            }
        }

        /// <summary>
        /// Phase 2, door two: view which radios each account covers and rebind
        /// them. Opens the associations dialog — which may view and rebind but
        /// never connect; connecting lives in the radio selector alone.
        /// </summary>
        private void AssociationsButton_Click(object sender, RoutedEventArgs e)
        {
            new RadioAssociationsDialog { Owner = this }.ShowDialog();
        }

        private void StartFreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_callbacks.StartFreshAllAccounts == null) return;

            int count = AccountListBox.Items.Count;
            string accountsWord = count == 1
                ? Radios.Lexicon.Get("connect.smartlink.account.start_fresh_one")
                : Radios.Lexicon.Get("connect.smartlink.account.start_fresh_many", ("count", count));
            var result = MessageBox.Show(
                Radios.Lexicon.Get("connect.smartlink.account.start_fresh_body",
                    ("accountsWord", accountsWord)),
                Radios.Lexicon.Get("connect.smartlink.account.start_fresh_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes) return;

            int cleared = _callbacks.StartFreshAllAccounts();
            LoadAccounts();
            string clearedWord = cleared == 1
                ? Radios.Lexicon.Get("connect.smartlink.account.start_fresh_cleared_one")
                : Radios.Lexicon.Get("connect.smartlink.account.start_fresh_cleared_many", ("cleared", cleared));
            _callbacks.ScreenReaderSpeak?.Invoke(
                Radios.Lexicon.Get("connect.smartlink.account.start_fresh_cleared",
                    ("clearedWord", clearedWord)),
                true);

            // Force the clean native sign-in through the same door as New
            // Login — the caller's loop opens the native form and returns
            // here afterwards.
            SelectedAccountData = null;
            NewLoginRequested = true;
            DialogResult = true;
            Close();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedAccount();
            if (item == null) return;

            var confirm = new ConfirmActionDialog(
                Radios.Lexicon.Get("connect.smartlink.account.delete_title"),
                Radios.Lexicon.Get("connect.smartlink.account.delete_body",
                    ("friendlyName", item.FriendlyName)),
                question: Radios.Lexicon.Get("connect.smartlink.account.delete_question"),
                yesLabel: Radios.Lexicon.Get("connect.smartlink.account.delete_yes"));

            if (confirm.ShowDialog() == true)
            {
                _callbacks.DeleteAccount(item.FriendlyName);
                LoadAccounts();
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Radios.Lexicon.Get("connect.smartlink.account.deleted"), true);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    /// <summary>
    /// Simple dialog for renaming an account.
    /// </summary>
    public partial class RenameAccountDialog : JJFlexDialog
    {
        public string NewName => NameBox.Text.Trim();

        private TextBox NameBox;

        public RenameAccountDialog(string currentName)
        {
            Title = Radios.Lexicon.Get("connect.smartlink.account.rename_dialog_title");
            Width = 350;
            Height = 140;

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = Radios.Lexicon.Get("connect.smartlink.account.rename_prompt"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(label, 0);

            NameBox = new TextBox
            {
                Text = currentName,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Automation.AutomationProperties.SetName(NameBox,
                Radios.Lexicon.Get("connect.smartlink.account.rename_box_name"));
            NameBox.SelectAll();
            Grid.SetRow(NameBox, 1);

            var buttonPanel = CreateButtonPanel(
                onOk: () => { /* validation could go here */ },
                onCancel: null);
            Grid.SetRow(buttonPanel, 2);

            grid.Children.Add(label);
            grid.Children.Add(NameBox);
            grid.Children.Add(buttonPanel);

            Content = grid;
        }

        protected override void FocusFirstControl()
        {
            NameBox?.Focus();
        }
    }
}
