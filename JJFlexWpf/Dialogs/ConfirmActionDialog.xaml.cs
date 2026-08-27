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
    /// Same conservative posture as its siblings: Yes is not the default
    /// button, and focus lands in the read-only body text — where Enter does
    /// nothing at all — so a user who muscle-memories Enter past a dialog does
    /// not commit a change that needs someone at the radio to undo. Landing in
    /// the text rather than on No is deliberate (2026-08-07): the warnings are
    /// the highest-stakes text in these flows, and focus starting there is what
    /// makes a screen reader read them before anything is decided.
    /// </summary>
    public partial class ConfirmActionDialog : JJFlexDialog
    {
        private readonly string? _radioModel;
        private readonly Radios.AdvisoryKey? _suppressKey;

        /// <param name="radioModel">
        /// The connected radio's model, for actions that ask the user to go and touch
        /// the radio. When a verified panel reference exists for it, the dialog grows
        /// a "Where are the jacks on my radio?" button — being told to key the
        /// microphone is not much help if nobody has said where the microphone plugs
        /// in. Left null, or set to a model we have not verified, the button stays
        /// hidden: a button that leads to an apology is worse than no button.
        /// </param>
        /// <param name="suppressKey">
        /// Optional <see cref="Radios.AdvisorySuppression"/> key, declared in
        /// <see cref="Radios.AdvisoryKeys"/> so it arrives carrying the words that
        /// name it in Settings. When set, the dialog grows a "Don't show this
        /// explanation again" checkbox; answering Yes with it checked persists the
        /// key. Callers must check
        /// <see cref="Radios.AdvisorySuppression.IsSuppressed(Radios.AdvisoryKey)"/>
        /// themselves before showing (and decide what a suppressed prompt means —
        /// usually "treat as Yes").
        /// Only for teaching/consent text whose outcome is still reported some
        /// other re-readable way; suppressing a receipt would be a silent change.
        /// Version the key (e.g. "-v1") so the explanation returns when its
        /// contents change.
        /// <para>Since Sprint 36 (#267) this is reversible: everything silenced
        /// here is listed under Settings → Notifications → Messages You Have
        /// Silenced and can be restored one at a time. Before that it was not,
        /// which on a CONFIRMATION dialog meant one keypress could stop a
        /// destructive action asking, permanently, with no way back.</para>
        /// </param>
        public ConfirmActionDialog(
            string title,
            string message,
            IReadOnlyList<string>? warnings = null,
            string question = "Continue?",
            string yesLabel = "_Yes",
            string noLabel = "_No",
            string? radioModel = null,
            Radios.AdvisoryKey? suppressKey = null)
        {
            InitializeComponent();

            Title = title;
            AutomationProperties.SetName(this, title);
            YesButton.Content = yesLabel;
            NoButton.Content = noLabel;
            _radioModel = radioModel;
            _suppressKey = suppressKey;
            if (suppressKey != null)
            {
                DontShowAgainCheck.Visibility = Visibility.Visible;
                // Say where the way back is, at the moment the door is offered.
                // This dialog is a CONFIRMATION — the one place where silencing
                // something has real consequences — so the operator should not
                // have to guess whether the choice is final.
                JJFlexHelp.SetText(DontShowAgainCheck,
                    Radios.Lexicon.Get("settings.silenced.reversible_help"));
            }

            // One reviewable document: message, then each warning as its own
            // paragraph, then the question the buttons answer. Blank separator
            // lines get the standard NVDA treatment from NormalizeLineBreaks.
            var body = new System.Text.StringBuilder(message);
            if (warnings != null)
            {
                foreach (var warning in warnings)
                {
                    body.AppendLine();
                    body.AppendLine();
                    body.Append(warning);
                }
            }
            body.AppendLine();
            body.AppendLine();
            body.Append(question);
            BodyText.Text = ScreenReaderText.NormalizeLineBreaks(body.ToString());

            if (RadioPanelGuide.HasGuide(radioModel))
                JacksButton.Visibility = Visibility.Visible;

            Loaded += (s, e) =>
            {
                BodyText.CaretIndex = 0;
                BodyText.Focus();
            };
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
            // Suppression is only honored on Yes: a No with the box checked
            // would mean "never ask, and don't do it" — an unresolvable state
            // the next time the action is attempted.
            if (_suppressKey != null && DontShowAgainCheck.IsChecked == true)
                Radios.AdvisorySuppression.Suppress(_suppressKey);
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
