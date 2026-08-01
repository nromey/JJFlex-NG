using System.Collections.Generic;
using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Confirmation dialog for Reboot Radio. Sibling of
    /// <see cref="ConfirmPortForwardApplyDialog"/> and follows the same
    /// defense-in-depth split: the RequireOperatorPresence check at the call site
    /// catches "not authorized to do this"; this dialog catches "accidental
    /// keypress."
    ///
    /// Reboot has a wider blast radius than port-forward — it drops every client
    /// on the radio, not just this one. So the dialog names the other stations it
    /// is about to disconnect. On a MultiFlex radio that is the single most
    /// decision-relevant fact, and JJ Flex already knows it.
    ///
    /// Default focus lands on No, matching the port-forward dialog: a user who
    /// muscle-memories Enter past a dialog must not take the radio down.
    /// </summary>
    public partial class ConfirmRebootDialog : JJFlexDialog
    {
        public ConfirmRebootDialog(IReadOnlyList<string> otherStations)
        {
            InitializeComponent();

            MessageBlock.Text =
                "JJ Flex will restart the radio. Any transmission in progress will stop.";

            int count = otherStations?.Count ?? 0;
            if (count > 0)
            {
                // Name names. "2 other stations" is less useful than "Don and Justin".
                string stations = string.Join(", ", otherStations);
                OtherStationsBlock.Text = count == 1
                    ? $"This will also disconnect {stations}, who is connected to this radio."
                    : $"This will also disconnect {count} other stations connected to this radio: {stations}.";
                OtherStationsBlock.Visibility = Visibility.Visible;
            }

            // Conservative default for a destructive action — user must Tab to Yes.
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
