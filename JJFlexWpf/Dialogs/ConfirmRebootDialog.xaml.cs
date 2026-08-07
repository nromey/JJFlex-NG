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
    /// Focus lands in the read-only body text, where Enter does nothing, and
    /// Yes is not the default: a user who muscle-memories Enter past a dialog
    /// must not take the radio down — and the text a screen reader starts on
    /// is the text that says who else gets dropped.
    /// </summary>
    public partial class ConfirmRebootDialog : JJFlexDialog
    {
        public ConfirmRebootDialog(IReadOnlyList<string> otherStations)
        {
            InitializeComponent();

            var body = new System.Text.StringBuilder(
                "JJ Flex will restart the radio. Any transmission in progress will stop.");

            int count = otherStations?.Count ?? 0;
            if (count > 0)
            {
                // Name names. "2 other stations" is less useful than "Don and Justin".
                string stations = string.Join(", ", otherStations!);
                body.AppendLine();
                body.AppendLine();
                body.Append(count == 1
                    ? $"This will also disconnect {stations}, who is connected to this radio."
                    : $"This will also disconnect {count} other stations connected to this radio: {stations}.");
            }

            body.AppendLine();
            body.AppendLine();
            body.Append("The radio will be unreachable for several minutes while it restarts. Continue?");
            BodyText.Text = ScreenReaderText.NormalizeLineBreaks(body.ToString());

            Loaded += (s, e) =>
            {
                BodyText.CaretIndex = 0;
                BodyText.Focus();
            };
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
