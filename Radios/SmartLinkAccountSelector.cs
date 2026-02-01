using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Radios
{
    /// <summary>
    /// Dialog for selecting a saved SmartLink account or initiating new login.
    /// Accessible: proper tab order, AccessibleName on all controls.
    /// </summary>
    public class SmartLinkAccountSelector : Form
    {
        private ListBox accountListBox;
        private Button connectButton;
        private Button newLoginButton;
        private Button deleteButton;
        private Button renameButton;
        private Button cancelButton;
        private Label instructionLabel;

        private readonly SmartLinkAccountManager _accountManager;

        /// <summary>
        /// The selected account, or null if user chose "New Login" or cancelled.
        /// </summary>
        public SmartLinkAccount SelectedAccount { get; private set; }

        /// <summary>
        /// True if user clicked "New Login" to authenticate with a new account.
        /// </summary>
        public bool NewLoginRequested { get; private set; }

        public SmartLinkAccountSelector(SmartLinkAccountManager accountManager)
        {
            _accountManager = accountManager ?? throw new ArgumentNullException(nameof(accountManager));
            InitializeComponent();
            LoadAccounts();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();

            // Form settings
            this.Text = "Select SmartLink Account";
            this.Size = new Size(400, 350);
            this.MinimumSize = new Size(350, 300);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.AcceptButton = connectButton;
            this.CancelButton = cancelButton;

            // Instruction label
            instructionLabel = new Label
            {
                Text = "Select a saved account or log in with a new one:",
                Location = new Point(12, 12),
                Size = new Size(360, 20),
                AutoSize = false,
                AccessibleName = "Select a saved SmartLink account or log in with a new one",
                TabIndex = 0
            };

            // Account list
            accountListBox = new ListBox
            {
                Location = new Point(12, 36),
                Size = new Size(260, 220),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AccessibleName = "Saved SmartLink accounts",
                AccessibleDescription = "List of saved SmartLink accounts. Select one and press Connect, or choose New Login.",
                TabIndex = 1
            };
            accountListBox.SelectedIndexChanged += AccountListBox_SelectedIndexChanged;
            accountListBox.DoubleClick += AccountListBox_DoubleClick;
            accountListBox.KeyDown += AccountListBox_KeyDown;

            // Connect button
            connectButton = new Button
            {
                Text = "&Connect",
                Location = new Point(285, 36),
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AccessibleName = "Connect using selected account",
                Enabled = false,
                TabIndex = 2
            };
            connectButton.Click += ConnectButton_Click;

            // New Login button
            newLoginButton = new Button
            {
                Text = "&New Login",
                Location = new Point(285, 70),
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AccessibleName = "Log in with a new SmartLink account",
                TabIndex = 3
            };
            newLoginButton.Click += NewLoginButton_Click;

            // Rename button
            renameButton = new Button
            {
                Text = "&Rename",
                Location = new Point(285, 114),
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AccessibleName = "Rename selected account",
                Enabled = false,
                TabIndex = 4
            };
            renameButton.Click += RenameButton_Click;

            // Delete button
            deleteButton = new Button
            {
                Text = "&Delete",
                Location = new Point(285, 148),
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AccessibleName = "Delete selected account",
                Enabled = false,
                TabIndex = 5
            };
            deleteButton.Click += DeleteButton_Click;

            // Cancel button
            cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(285, 220),
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                AccessibleName = "Cancel and close dialog",
                DialogResult = DialogResult.Cancel,
                TabIndex = 6
            };

            // Add controls
            this.Controls.Add(instructionLabel);
            this.Controls.Add(accountListBox);
            this.Controls.Add(connectButton);
            this.Controls.Add(newLoginButton);
            this.Controls.Add(renameButton);
            this.Controls.Add(deleteButton);
            this.Controls.Add(cancelButton);

            this.AcceptButton = connectButton;
            this.CancelButton = cancelButton;

            this.ResumeLayout(false);
        }

        private void LoadAccounts()
        {
            accountListBox.Items.Clear();

            var accounts = _accountManager.Accounts
                .OrderByDescending(a => a.LastUsed)
                .ToList();

            foreach (var account in accounts)
            {
                accountListBox.Items.Add(new AccountListItem(account));
            }

            if (accountListBox.Items.Count > 0)
            {
                accountListBox.SelectedIndex = 0;
            }

            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            bool hasSelection = accountListBox.SelectedIndex >= 0;
            connectButton.Enabled = hasSelection;
            renameButton.Enabled = hasSelection;
            deleteButton.Enabled = hasSelection;
        }

        private void AccountListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateButtonStates();
        }

        private void AccountListBox_DoubleClick(object sender, EventArgs e)
        {
            if (accountListBox.SelectedIndex >= 0)
            {
                ConnectButton_Click(sender, e);
            }
        }

        private void AccountListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && accountListBox.SelectedIndex >= 0)
            {
                ConnectButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && accountListBox.SelectedIndex >= 0)
            {
                DeleteButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void ConnectButton_Click(object sender, EventArgs e)
        {
            if (accountListBox.SelectedItem is AccountListItem item)
            {
                SelectedAccount = item.Account;
                NewLoginRequested = false;
                DialogResult = DialogResult.OK;
            }
        }

        private void NewLoginButton_Click(object sender, EventArgs e)
        {
            SelectedAccount = null;
            NewLoginRequested = true;
            DialogResult = DialogResult.OK;
        }

        private void RenameButton_Click(object sender, EventArgs e)
        {
            if (!(accountListBox.SelectedItem is AccountListItem item))
                return;

            using (var inputDialog = new RenameAccountDialog(item.Account.FriendlyName))
            {
                if (inputDialog.ShowDialog(this) == DialogResult.OK)
                {
                    string newName = inputDialog.NewName;
                    if (!string.IsNullOrWhiteSpace(newName) && newName != item.Account.FriendlyName)
                    {
                        if (_accountManager.RenameAccount(item.Account.FriendlyName, newName))
                        {
                            LoadAccounts();
                            ScreenReaderOutput.Speak($"Account renamed to {newName}", true);
                        }
                        else
                        {
                            MessageBox.Show("Could not rename account. The name may already be in use.",
                                "Rename Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                }
            }
        }

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            if (!(accountListBox.SelectedItem is AccountListItem item))
                return;

            var result = MessageBox.Show(
                $"Are you sure you want to delete the saved account \"{item.Account.FriendlyName}\"?\n\n" +
                "You will need to log in again to use this account.",
                "Delete Account",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _accountManager.DeleteAccount(item.Account.FriendlyName);
                LoadAccounts();
                ScreenReaderOutput.Speak("Account deleted", true);
            }
        }

        /// <summary>
        /// Wrapper for displaying account in ListBox.
        /// </summary>
        private class AccountListItem
        {
            public SmartLinkAccount Account { get; }

            public AccountListItem(SmartLinkAccount account)
            {
                Account = account;
            }

            public override string ToString()
            {
                // Format: "FriendlyName (email) - Last used: date"
                string lastUsed = Account.LastUsed > DateTime.MinValue
                    ? Account.LastUsed.ToLocalTime().ToString("g")
                    : "Never";
                return $"{Account.FriendlyName} ({Account.Email}) - Last used: {lastUsed}";
            }
        }
    }

    /// <summary>
    /// Simple dialog for renaming an account.
    /// </summary>
    internal class RenameAccountDialog : Form
    {
        private TextBox nameTextBox;
        private Button okButton;
        private Button cancelButton;

        public string NewName => nameTextBox.Text.Trim();

        public RenameAccountDialog(string currentName)
        {
            this.Text = "Rename Account";
            this.Size = new Size(350, 130);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;

            var label = new Label
            {
                Text = "Enter new name:",
                Location = new Point(12, 12),
                AutoSize = true
            };

            nameTextBox = new TextBox
            {
                Text = currentName,
                Location = new Point(12, 32),
                Size = new Size(310, 23),
                AccessibleName = "New account name"
            };
            nameTextBox.SelectAll();

            okButton = new Button
            {
                Text = "OK",
                Location = new Point(166, 60),
                Size = new Size(75, 25),
                DialogResult = DialogResult.OK
            };

            cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(247, 60),
                Size = new Size(75, 25),
                DialogResult = DialogResult.Cancel
            };

            this.Controls.Add(label);
            this.Controls.Add(nameTextBox);
            this.Controls.Add(okButton);
            this.Controls.Add(cancelButton);

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }
}
