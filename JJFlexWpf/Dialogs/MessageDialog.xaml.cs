using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    public partial class MessageDialog : JJFlexDialog
    {
        /// <summary>Message text to display.</summary>
        public string Message { get; set; } = "";

        /// <summary>Optional custom content control to host below the message.</summary>
        public UIElement? CustomControl { get; set; }

        /// <summary>Show the "Don't show again" checkbox.</summary>
        public bool ShowDontShowAgain { get; set; }

        /// <summary>Show a Cancel button (makes this a confirm dialog).</summary>
        public bool ShowCancel { get; set; }

        /// <summary>True if the user checked "Don't show again".</summary>
        public bool DontShowAgainChecked { get; private set; }

        /// <summary>
        /// Tag identifier for persistence of "Don't show again" preference.
        /// </summary>
        public string? PersistenceTag { get; set; }

        /// <summary>
        /// Delegate to check if this message was previously suppressed.
        /// Returns true if should be suppressed (auto-OK).
        /// </summary>
        public Func<string, bool>? CheckSuppressed { get; set; }

        /// <summary>
        /// Delegate to save "Don't show again" preference.
        /// Receives (tag, suppress).
        /// </summary>
        public Action<string, bool>? SaveSuppressed { get; set; }

        public MessageDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Check if previously suppressed
            if (PersistenceTag != null && CheckSuppressed?.Invoke(PersistenceTag) == true)
            {
                DialogResult = true;
                Close();
                return;
            }

            MessageText.Text = Message;
            if (string.IsNullOrEmpty(Message))
                MessageText.Visibility = Visibility.Collapsed;

            if (CustomControl != null)
                CustomContent.Content = CustomControl;

            if (ShowDontShowAgain)
                DontShowBox.Visibility = Visibility.Visible;

            if (ShowCancel)
                CancelBtn.Visibility = Visibility.Visible;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            DontShowAgainChecked = DontShowBox.IsChecked == true;
            if (DontShowAgainChecked && PersistenceTag != null)
                SaveSuppressed?.Invoke(PersistenceTag, true);

            DialogResult = true;
            Close();
        }
    }
}
