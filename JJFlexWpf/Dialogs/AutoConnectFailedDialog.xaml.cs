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
        public AutoConnectFailedDialog(string radioName, Action<string, bool>? screenReaderSpeak = null)
        {
            _screenReaderSpeak = screenReaderSpeak;
            var displayName = string.IsNullOrWhiteSpace(radioName) ? "Your radio" : radioName;

            InitializeComponent();

            MessageText.Text = $"{displayName} is not available.\n\nWhat would you like to do?";
            AutomationProperties.SetName(this, $"Auto-connect failed. {displayName} is not available.");
            AutomationProperties.SetName(MessageText, $"{displayName} is not available. What would you like to do?");

            _screenReaderSpeak?.Invoke($"{displayName} is offline", true);
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
            Action<string, bool>? screenReaderSpeak = null)
        {
            var dialog = new AutoConnectFailedDialog(radioName, screenReaderSpeak);
            if (owner != null)
            {
                try { dialog.Owner = owner; } catch { }
            }
            dialog.ShowDialog();
            return dialog.Result;
        }
    }
}
