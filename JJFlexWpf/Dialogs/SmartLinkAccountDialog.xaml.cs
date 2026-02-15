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

        public override string ToString()
        {
            string lastUsed = LastUsed > DateTime.MinValue
                ? LastUsed.ToLocalTime().ToString("g")
                : "Never";
            return $"{FriendlyName} ({Email}) - Last used: {lastUsed}";
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
            RenameButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
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
                        _callbacks.ScreenReaderSpeak?.Invoke($"Account renamed to {newName}", true);
                    }
                    else
                    {
                        MessageBox.Show("Could not rename account. The name may already be in use.",
                            "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedAccount();
            if (item == null) return;

            var result = MessageBox.Show(
                $"Are you sure you want to delete the saved account \"{item.FriendlyName}\"?\n\n" +
                "You will need to log in again to use this account.",
                "Delete Account",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                _callbacks.DeleteAccount(item.FriendlyName);
                LoadAccounts();
                _callbacks.ScreenReaderSpeak?.Invoke("Account deleted", true);
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
            Title = "Rename Account";
            Width = 350;
            Height = 140;

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Enter new name:",
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(label, 0);

            NameBox = new TextBox
            {
                Text = currentName,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Automation.AutomationProperties.SetName(NameBox, "New account name");
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
