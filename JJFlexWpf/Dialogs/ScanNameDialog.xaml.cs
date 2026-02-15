using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class ScanNameDialog : JJFlexDialog
    {
        /// <summary>
        /// The scan name entered by the user. Set after OK is clicked.
        /// </summary>
        public string ResultName { get; private set; } = "";

        public ScanNameDialog()
        {
            InitializeComponent();
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ScanNameBox.Text))
            {
                return;
            }
            ResultName = ScanNameBox.Text;
            DialogResult = true;
            Close();
        }
    }
}
