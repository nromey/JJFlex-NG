using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class ShowHelpDialog : JJFlexDialog
    {
        /// <summary>
        /// The formatted help text to display. Caller builds the text
        /// from KeyCommands sorted by the desired help type.
        /// </summary>
        public string HelpText { get; set; } = "";

        public ShowHelpDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HelpTextBox.Text = HelpText;
            HelpTextBox.Focus();
        }
    }
}
