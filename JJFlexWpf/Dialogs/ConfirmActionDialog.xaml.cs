using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// General-purpose confirmation dialog for actions that carry warnings the
    /// user needs to read before committing.
    ///
    /// Sibling of <see cref="ConfirmRebootDialog"/> and
    /// <see cref="ConfirmPortForwardApplyDialog"/>, which stay bespoke because
    /// their message text is specific enough to be worth hand-writing. This one
    /// exists so that every *new* guarded action does not spawn another
    /// near-identical dialog file — the network-settings work alone would have
    /// produced three.
    ///
    /// Same conservative posture as its siblings: focus lands on No, and Yes is
    /// not the default button, so a user who muscle-memories Enter past a dialog
    /// does not commit a change that needs someone at the radio to undo.
    /// </summary>
    public partial class ConfirmActionDialog : JJFlexDialog
    {
        public ConfirmActionDialog(
            string title,
            string message,
            IReadOnlyList<string>? warnings = null,
            string question = "Continue?",
            string yesLabel = "_Yes",
            string noLabel = "_No")
        {
            InitializeComponent();

            Title = title;
            AutomationProperties.SetName(this, title);
            MessageBlock.Text = message;
            QuestionBlock.Text = question;
            YesButton.Content = yesLabel;
            NoButton.Content = noLabel;

            if (warnings != null && warnings.Count > 0)
            {
                WarningsList.ItemsSource = warnings;
                WarningsList.Visibility = Visibility.Visible;
            }

            Loaded += (s, e) => NoButton.Focus();
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
