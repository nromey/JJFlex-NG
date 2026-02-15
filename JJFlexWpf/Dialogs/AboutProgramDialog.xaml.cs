using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class AboutProgramDialog : JJFlexDialog
    {
        /// <summary>
        /// The about text to display. Set before calling ShowDialog().
        /// Caller should build version info, credits, DLL versions, etc.
        /// </summary>
        public string AboutText { get; set; } = "";

        public AboutProgramDialog()
        {
            InitializeComponent();
            // Override base class — About box should be resizable
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InfoBox.Text = AboutText;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
