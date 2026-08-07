using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Sprint 28 Phase 7 — confirmation dialog for Apply port forward. Defense-in-depth
    /// companion to the RequireOperatorPresence ownership check: presence-check catches
    /// "not authorized to do this"; this dialog catches "accidental button press."
    ///
    /// Default focus lands on No for conservative safety — a user who muscle-memories
    /// Enter past a dialog won't accidentally change port forward state. They have to
    /// explicitly Tab to Yes and commit.
    /// </summary>
    public partial class ConfirmPortForwardApplyDialog : JJFlexDialog
    {
        public ConfirmPortForwardApplyDialog(bool enabled, int tcp, int udp)
        {
            InitializeComponent();

            string actionDescription = enabled
                ? (tcp == udp
                    ? $"JJ Flex will tell the radio to listen for SmartLink on port {tcp} (both TCP and UDP)."
                    : $"JJ Flex will tell the radio to listen for SmartLink on TCP port {tcp} and UDP port {udp}.")
                : "JJ Flex will tell the radio to stop listening on a forwarded port (disable port forwarding).";

            BodyText.Text = ScreenReaderText.NormalizeLineBreaks(
                actionDescription +
                "\n\nThese settings persist on the radio across future connections by any client. Continue?");

            // Focus lands in the read-only text — conservative default for
            // destructive/persistent actions: Enter there does nothing, and the
            // user must Tab to Yes to commit.
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
