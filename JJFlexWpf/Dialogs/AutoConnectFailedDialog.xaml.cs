using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Result of the AutoConnectFailedDialog.
    /// </summary>
    public enum AutoConnectFailedResult
    {
        Cancel,
        TryAgain,
        DisableAutoConnect,
        ChooseAnotherRadio
    }

    public partial class AutoConnectFailedDialog : JJFlexDialog
    {
        /// <summary>
        /// The action the user selected.
        /// </summary>
        public AutoConnectFailedResult Result { get; private set; } = AutoConnectFailedResult.Cancel;

        private readonly Action<string, bool>? _screenReaderSpeak;

        /// <summary>
        /// Creates the auto-connect failed dialog.
        /// </summary>
        /// <param name="radioName">Name of the radio that couldn't be reached</param>
        /// <param name="screenReaderSpeak">Optional delegate for screen reader announcement (message, interrupt)</param>
        /// <param name="advice">
        /// Classified failure evidence (FlexBase.LastConnectFailureAdvice),
        /// when a report exists. QB Track L: shown in the body and spoken, so
        /// the user hears WHY. Bare wording only when the report is genuinely
        /// absent (Track D's model — "offline" is a guess we only make then).
        /// </param>
        public AutoConnectFailedDialog(string radioName, Action<string, bool>? screenReaderSpeak = null,
            string? advice = null)
        {
            _screenReaderSpeak = screenReaderSpeak;
            var displayName = string.IsNullOrWhiteSpace(radioName) ? "Your radio" : radioName;
            var trimmedAdvice = string.IsNullOrWhiteSpace(advice) ? null : advice.Trim();

            InitializeComponent();

            MessageText.Text = trimmedAdvice == null
                ? $"{displayName} is not available.\n\nWhat would you like to do?"
                : $"{displayName} is not available.\n\n{trimmedAdvice}\n\nWhat would you like to do?";
            AutomationProperties.SetName(this, $"Auto-connect failed. {displayName} is not available.");
            AutomationProperties.SetName(MessageText, MessageText.Text.Replace("\n\n", " "));

            _screenReaderSpeak?.Invoke(
                trimmedAdvice == null
                    ? $"{displayName} is offline"
                    : $"{displayName} is not available. {trimmedAdvice}",
                true);
        }

        private void TryAgainButton_Click(object sender, RoutedEventArgs e)
        {
            Result = AutoConnectFailedResult.TryAgain;
            DialogResult = true;
            Close();
        }

        private void DisableButton_Click(object sender, RoutedEventArgs e)
        {
            Result = AutoConnectFailedResult.DisableAutoConnect;
            DialogResult = true;
            Close();
        }

        private void ChooseAnotherButton_Click(object sender, RoutedEventArgs e)
        {
            Result = AutoConnectFailedResult.ChooseAnotherRadio;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = AutoConnectFailedResult.Cancel;
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Shows the dialog and returns the user's choice.
        /// </summary>
        public static AutoConnectFailedResult Show(System.Windows.Window? owner, string radioName,
            Action<string, bool>? screenReaderSpeak = null, string? advice = null)
        {
            var dialog = new AutoConnectFailedDialog(radioName, screenReaderSpeak, advice);
            if (owner != null)
            {
                try { dialog.Owner = owner; } catch { }
            }
            dialog.ShowDialog();
            return dialog.Result;
        }
    }
}
