using System;
using System.Drawing;
using System.Windows.Forms;

namespace Radios
{
    /// <summary>
    /// Result of the AutoConnectFailedDialog - indicates which action the user chose.
    /// </summary>
    public enum AutoConnectFailedResult
    {
        /// <summary>User clicked Cancel or closed the dialog.</summary>
        Cancel,
        /// <summary>User wants to retry the connection.</summary>
        TryAgain,
        /// <summary>User wants to disable auto-connect for this radio.</summary>
        DisableAutoConnect,
        /// <summary>User wants to choose a different radio.</summary>
        ChooseAnotherRadio
    }

    /// <summary>
    /// Friendly dialog shown when the auto-connect radio is offline.
    /// Presents clear options: Try Again, Disable Auto-Connect, Choose Another, Cancel.
    /// Accessible: proper tab order, AccessibleName on all controls, screen reader friendly.
    /// </summary>
    public class AutoConnectFailedDialog : Form
    {
        private Label messageLabel;
        private Button tryAgainButton;
        private Button disableButton;
        private Button chooseAnotherButton;
        private Button cancelButton;

        /// <summary>
        /// The action the user selected.
        /// </summary>
        public AutoConnectFailedResult Result { get; private set; } = AutoConnectFailedResult.Cancel;

        private readonly string _radioName;
        private readonly string _advice;

        /// <summary>
        /// Creates the dialog with the specified radio name.
        /// </summary>
        /// <param name="radioName">Name of the radio that couldn't be reached</param>
        /// <param name="advice">
        /// The classified failure evidence (FlexBase.LastConnectFailureAdvice),
        /// when a report exists. QB Track L: the dialog states WHY the connect
        /// failed instead of only naming the radio; bare wording only when the
        /// report is genuinely absent (Track D's model).
        /// </param>
        public AutoConnectFailedDialog(string radioName, string advice = null)
        {
            _radioName = string.IsNullOrWhiteSpace(radioName) ? "Your radio" : radioName;
            _advice = string.IsNullOrWhiteSpace(advice) ? string.Empty : advice.Trim();
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();

            // Form settings
            this.Text = "Auto-Connect Failed";
            this.Size = new Size(420, 200);
            this.MinimumSize = new Size(400, 190);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.CancelButton = cancelButton;

            // Accessible name for the form itself
            this.AccessibleName = $"Auto-connect failed. {_radioName} is not available.";
            this.AccessibleDescription = "Choose an action: Try Again, Disable Auto-Connect, Choose Another Radio, or Cancel.";

            // Message label. When classified failure evidence exists it goes in
            // the body — the user should read WHY, not just WHO (QB Track L).
            var message = string.IsNullOrEmpty(_advice)
                ? $"{_radioName} is not available.\n\nWhat would you like to do?"
                : $"{_radioName} is not available.\n\n{_advice}\n\nWhat would you like to do?";
            const int labelWidth = 380;
            // The advice can run several sentences (and may carry a router
            // rule), so measure instead of assuming the two-line height.
            int labelHeight = Math.Max(50,
                TextRenderer.MeasureText(message, this.Font,
                    new Size(labelWidth, 0), TextFormatFlags.WordBreak).Height + 8);
            messageLabel = new Label
            {
                Text = message,
                Location = new Point(16, 16),
                Size = new Size(labelWidth, labelHeight),
                AutoSize = false,
                AccessibleName = message.Replace("\n\n", " "),
                TabIndex = 0
            };

            // Button panel - arrange horizontally below the message
            int buttonY = 16 + labelHeight + 19;
            int buttonHeight = 32;
            int buttonSpacing = 8;
            int currentX = 16;

            // Try Again button
            tryAgainButton = new Button
            {
                Text = "&Try Again",
                Location = new Point(currentX, buttonY),
                Size = new Size(90, buttonHeight),
                AccessibleName = "Try Again - retry connecting to the radio",
                TabIndex = 1
            };
            tryAgainButton.Click += (s, e) =>
            {
                Result = AutoConnectFailedResult.TryAgain;
                DialogResult = DialogResult.OK;
                Close();
            };
            currentX += tryAgainButton.Width + buttonSpacing;

            // Disable Auto-Connect button
            disableButton = new Button
            {
                Text = "&Disable",
                Location = new Point(currentX, buttonY),
                Size = new Size(90, buttonHeight),
                AccessibleName = "Disable Auto-Connect for this radio",
                TabIndex = 2
            };
            disableButton.Click += (s, e) =>
            {
                Result = AutoConnectFailedResult.DisableAutoConnect;
                DialogResult = DialogResult.OK;
                Close();
            };
            currentX += disableButton.Width + buttonSpacing;

            // Choose Another Radio button
            chooseAnotherButton = new Button
            {
                Text = "C&hoose Another",
                Location = new Point(currentX, buttonY),
                Size = new Size(110, buttonHeight),
                AccessibleName = "Choose Another Radio - open radio selector",
                TabIndex = 3
            };
            chooseAnotherButton.Click += (s, e) =>
            {
                Result = AutoConnectFailedResult.ChooseAnotherRadio;
                DialogResult = DialogResult.OK;
                Close();
            };
            currentX += chooseAnotherButton.Width + buttonSpacing;

            // Cancel button
            cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(currentX, buttonY),
                Size = new Size(75, buttonHeight),
                AccessibleName = "Cancel - close this dialog and stay disconnected",
                DialogResult = DialogResult.Cancel,
                TabIndex = 4
            };
            cancelButton.Click += (s, e) =>
            {
                Result = AutoConnectFailedResult.Cancel;
                Close();
            };

            // Set form's cancel button now that it's created
            this.CancelButton = cancelButton;

            // Add controls
            this.Controls.Add(messageLabel);
            this.Controls.Add(tryAgainButton);
            this.Controls.Add(disableButton);
            this.Controls.Add(chooseAnotherButton);
            this.Controls.Add(cancelButton);

            // Fit the client area to the measured message — a long failure
            // report must never clip behind a fixed 200px form.
            this.ClientSize = new Size(412, buttonY + buttonHeight + 16);

            this.ResumeLayout(false);
        }

        /// <summary>
        /// Shows the dialog and returns the user's choice.
        /// Convenience method for common usage pattern.
        /// </summary>
        /// <param name="owner">Parent form</param>
        /// <param name="radioName">Name of the offline radio</param>
        /// <param name="advice">
        /// Classified failure evidence (FlexBase.LastConnectFailureAdvice) to
        /// show and speak, or null when no report exists. With advice, the
        /// announcement says "not available" plus the reason — "offline" is a
        /// guess we only make when the evidence is genuinely absent.
        /// </param>
        /// <returns>The action the user selected</returns>
        public static AutoConnectFailedResult ShowDialog(IWin32Window owner, string radioName,
            string advice = null)
        {
            // Announce for screen reader users
            var displayName = string.IsNullOrWhiteSpace(radioName) ? "Your radio" : radioName;
            ScreenReaderOutput.Speak(
                string.IsNullOrWhiteSpace(advice)
                    ? $"{displayName} is offline"
                    : $"{displayName} is not available. {advice.Trim()}",
                VerbosityLevel.Critical, true);

            using var dialog = new AutoConnectFailedDialog(radioName, advice);
            dialog.ShowDialog(owner);
            return dialog.Result;
        }
    }
}
