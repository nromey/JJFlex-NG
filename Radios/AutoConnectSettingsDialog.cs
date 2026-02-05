using System;
using System.Drawing;
using System.Windows.Forms;

namespace Radios
{
    /// <summary>
    /// Dialog for reviewing and confirming auto-connect settings before saving.
    /// Shows what will be saved and lets user confirm or cancel.
    /// </summary>
    public class AutoConnectSettingsDialog : Form
    {
        private Label radioNameLabel;
        private Label radioNameValue;
        private CheckBox autoConnectCheckbox;
        private CheckBox lowBandwidthCheckbox;
        private Button okButton;
        private Button cancelButton;

        /// <summary>
        /// Whether auto-connect should be enabled for this radio.
        /// </summary>
        public bool AutoConnectEnabled { get; private set; }

        /// <summary>
        /// Whether to use low bandwidth mode.
        /// </summary>
        public bool LowBandwidth { get; private set; }

        /// <summary>
        /// Creates the settings dialog.
        /// </summary>
        /// <param name="radioName">Name of the radio being configured</param>
        /// <param name="currentAutoConnect">Current auto-connect state</param>
        /// <param name="currentLowBandwidth">Current low bandwidth state</param>
        public AutoConnectSettingsDialog(string radioName, bool currentAutoConnect, bool currentLowBandwidth)
        {
            AutoConnectEnabled = currentAutoConnect;
            LowBandwidth = currentLowBandwidth;
            InitializeComponent(radioName, currentAutoConnect, currentLowBandwidth);
        }

        private void InitializeComponent(string radioName, bool currentAutoConnect, bool currentLowBandwidth)
        {
            this.SuspendLayout();

            // Form settings
            this.Text = "Auto-Connect Settings";
            this.Size = new Size(350, 200);
            this.MinimumSize = new Size(300, 180);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;

            // Accessible description for the form
            this.AccessibleName = "Auto-connect settings for " + radioName;
            this.AccessibleDescription = "Configure auto-connect and low bandwidth settings for this radio";

            // Radio name label
            radioNameLabel = new Label
            {
                Text = "Radio:",
                Location = new Point(16, 16),
                Size = new Size(50, 20),
                AccessibleName = "Radio label",
                TabIndex = 0
            };

            // Radio name value
            radioNameValue = new Label
            {
                Text = radioName,
                Location = new Point(70, 16),
                Size = new Size(250, 20),
                Font = new Font(this.Font, FontStyle.Bold),
                AccessibleName = "Radio name: " + radioName,
                TabIndex = 1
            };

            // Auto-connect checkbox
            autoConnectCheckbox = new CheckBox
            {
                Text = "Enable auto-connect for this radio",
                Location = new Point(16, 50),
                Size = new Size(300, 24),
                Checked = currentAutoConnect,
                AccessibleName = "Enable auto-connect for this radio",
                TabIndex = 2
            };

            // Low bandwidth checkbox
            lowBandwidthCheckbox = new CheckBox
            {
                Text = "Use low bandwidth mode",
                Location = new Point(16, 80),
                Size = new Size(300, 24),
                Checked = currentLowBandwidth,
                AccessibleName = "Use low bandwidth mode",
                TabIndex = 3
            };

            // OK button
            okButton = new Button
            {
                Text = "Save",
                Location = new Point(160, 120),
                Size = new Size(80, 28),
                DialogResult = DialogResult.OK,
                AccessibleName = "Save auto-connect settings",
                TabIndex = 4
            };
            okButton.Click += (s, e) =>
            {
                AutoConnectEnabled = autoConnectCheckbox.Checked;
                LowBandwidth = lowBandwidthCheckbox.Checked;
            };

            // Cancel button
            cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(250, 120),
                Size = new Size(80, 28),
                DialogResult = DialogResult.Cancel,
                AccessibleName = "Cancel without saving",
                TabIndex = 5
            };

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;

            // Add controls
            this.Controls.Add(radioNameLabel);
            this.Controls.Add(radioNameValue);
            this.Controls.Add(autoConnectCheckbox);
            this.Controls.Add(lowBandwidthCheckbox);
            this.Controls.Add(okButton);
            this.Controls.Add(cancelButton);

            this.ResumeLayout(false);
        }

        /// <summary>
        /// Shows the dialog and returns true if user clicked Save.
        /// </summary>
        public static bool ShowSettingsDialog(IWin32Window owner, string radioName,
            ref bool autoConnect, ref bool lowBandwidth)
        {
            using var dialog = new AutoConnectSettingsDialog(radioName, autoConnect, lowBandwidth);
            if (dialog.ShowDialog(owner) == DialogResult.OK)
            {
                autoConnect = dialog.AutoConnectEnabled;
                lowBandwidth = dialog.LowBandwidth;
                return true;
            }
            return false;
        }
    }
}
