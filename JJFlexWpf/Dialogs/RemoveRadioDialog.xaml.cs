using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// "Take this radio off my list" — with the scope of the removal chosen
    /// inside the confirmation rather than by picking one of two menu items
    /// (task #98, shape ratified by Noel).
    ///
    /// <para><b>Why one dialog and not two commands.</b> One thing to find, so
    /// the Delete key and one menu item cover the whole feature. The
    /// consequences sit beside the choice, where they are read at the moment
    /// the choice is made rather than remembered from a menu. And the safe
    /// scope is the default, checked before the operator touches anything, so
    /// the fast path through this dialog is the one that deletes nothing.</para>
    ///
    /// <para><b>Why an accidental removal is survivable</b>, which is what
    /// makes the Delete key defensible at all: a legitimate radio that is
    /// online gets re-discovered, and the safe scope keeps its settings, so
    /// the radio and everything about it comes straight back. That property is
    /// stated to the operator, not just relied on — and the destructive scope,
    /// which does not have it, says so in as many words.</para>
    ///
    /// <para>Until this existed there was NO way to remove a radio. The only
    /// escape was hand-editing AppData, which is not a thing a blind operator
    /// should ever be asked to do.</para>
    /// </summary>
    public partial class RemoveRadioDialog : JJFlexDialog
    {
        /// <summary>True when the operator chose the destructive scope.
        /// Meaningful only when the dialog returned true.</summary>
        public bool DeleteSettings { get; private set; }

        /// <param name="radioName">The radio as the operator knows it — the
        /// name they gave it, or the one it broadcasts. Never the bare serial
        /// when anything better exists; a list of digits is not what anyone
        /// navigates by.</param>
        /// <param name="isOnline">Whether anything can see this radio right
        /// now. Changes what the safe scope HONESTLY promises, so it changes
        /// the text rather than being quietly ignored.</param>
        /// <param name="isAutoConnect">Whether this radio is the one
        /// auto-connect starts. Removing it clears that, and a setting that
        /// disappears without being mentioned is a setting the operator will
        /// rediscover at the worst moment.</param>
        public RemoveRadioDialog(string radioName, bool isOnline, bool isAutoConnect)
        {
            InitializeComponent();

            AutomationProperties.SetName(this, Title);

            var body = new StringBuilder();
            body.Append("Remove ").Append(radioName).Append(" from your radio list?");
            body.AppendLine();
            body.AppendLine();
            body.Append(
                "There are two ways to do it, and they differ in what happens to everything you "
                + "have set up for this radio.");
            body.AppendLine();
            body.AppendLine();
            body.Append(
                // Noel, 2026-08-19: "keeps all of it" had no antecedent you were
                // still holding - the previous paragraph ends on "everything you
                // have set up for this radio", and by the time the reader reaches
                // "it" the subject has become a button name. It also garden-paths:
                // "Remove from the list only keeps..." parses first as "only
                // keeps", as though something were being withheld. Naming the
                // thing beats pointing at it, and quoting the label stops it
                // reading as the sentence's verb.
                "Choosing \"Remove from the list only\" keeps every setting you have for this "
                + "radio: the name you gave it, whether it is a favourite, its connection path "
                + "and connection history, which SmartLink account it belongs to, whether you "
                + "want it reachable from away at all, what it should do with the REM ON jack, "
                + "and any microphone profile bound to it. Nothing is deleted. The radio simply "
                + "stops appearing.");
            body.AppendLine();
            body.AppendLine();
            body.Append(
                // Same treatment: "those" was doing the job "it" was doing above.
                "Choosing \"Remove the radio and its settings\" deletes all of them, permanently "
                + "and with no undo. The radio itself can come back the moment something sees it "
                + "again — the settings cannot, and you would be setting it up from scratch.");

            if (isOnline)
            {
                body.AppendLine();
                body.AppendLine();
                // The honest wrinkle, said out loud. Promising that a live
                // radio will stay gone would be a promise the next discovery
                // sweep breaks about a second later.
                body.Append(
                    "Worth knowing: this radio is reachable right now. Removing it from the list "
                    + "will not keep it away — the next time JJ Flexible looks for radios it will "
                    + "find this one and list it again, with its settings intact. Removing a radio "
                    + "from the list is really for an entry that will never answer again. "
                    + "Removing the settings still deletes the settings.");
            }

            if (isAutoConnect)
            {
                body.AppendLine();
                body.AppendLine();
                body.Append(
                    "This is also the radio JJ Flexible connects to on startup. Removing it turns "
                    + "auto-connect off, whichever way you remove it.");
            }

            BodyText.Text = ScreenReaderText.NormalizeLineBreaks(body.ToString());

            Loaded += (_, _) =>
            {
                BodyText.CaretIndex = 0;
                BodyText.Focus();
            };
        }

        /// <summary>
        /// Focus the body, not the first control in tab order.
        ///
        /// The base walks tab order and takes the first focusable element,
        /// which happens to be the body today and would stop being so the
        /// moment anything focusable is added above it. Naming the target
        /// removes the fragility — the same reason the radio picker overrides
        /// this method.
        /// </summary>
        protected override void FocusFirstControl()
        {
            if (BodyText == null)
            {
                base.FocusFirstControl();
                return;
            }
            BodyText.CaretIndex = 0;
            BodyText.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteSettings = ScopeEverythingRadio.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
