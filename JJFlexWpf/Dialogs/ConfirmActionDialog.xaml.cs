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
        private readonly string? _radioModel;

        /// <param name="radioModel">
        /// The connected radio's model, for actions that ask the user to go and touch
        /// the radio. When a verified panel reference exists for it, the dialog grows
        /// a "Where are the jacks on my radio?" button — being told to key the
        /// microphone is not much help if nobody has said where the microphone plugs
        /// in. Left null, or set to a model we have not verified, the button stays
        /// hidden: a button that leads to an apology is worse than no button.
        /// </param>
        public ConfirmActionDialog(
            string title,
            string message,
            IReadOnlyList<string>? warnings = null,
            string question = "Continue?",
            string yesLabel = "_Yes",
            string noLabel = "_No",
            string? radioModel = null)
        {
            InitializeComponent();

            Title = title;
            AutomationProperties.SetName(this, title);
            MessageBlock.Text = message;
            QuestionBlock.Text = question;
            YesButton.Content = yesLabel;
            NoButton.Content = noLabel;
            _radioModel = radioModel;

            if (warnings != null && warnings.Count > 0)
            {
                WarningsList.ItemsSource = warnings;
                WarningsList.Visibility = Visibility.Visible;
            }

            if (RadioPanelGuide.HasGuide(radioModel))
                JacksButton.Visibility = Visibility.Visible;

            Loaded += (s, e) => NoButton.Focus();
        }

        /// <summary>
        /// Open the panel reference over this dialog. It stays open underneath, so
        /// the user reads, closes, and answers the question they were already on —
        /// looking something up should not cost them their place.
        /// </summary>
        private void JacksButton_Click(object sender, RoutedEventArgs e)
        {
            RadioPanelGuide.Show(_radioModel, this);
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
