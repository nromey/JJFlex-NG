using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
    public partial class WelcomeDialog : JJFlexDialog
    {
        private readonly string _docName;
        private readonly Func<bool> _importAction;

        /// <summary>
        /// Creates the Welcome dialog.
        /// </summary>
        /// <param name="docName">Path to the documentation file to open</param>
        /// <param name="importAction">Action to run when Import is clicked; returns true if import succeeded</param>
        public WelcomeDialog(string docName, Func<bool> importAction)
        {
            _docName = docName;
            _importAction = importAction;
            InitializeComponent();
        }

        protected override void FocusFirstControl()
        {
            WelcomeBox.Focus();
        }

        private void WelcomeDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = File.ReadAllText("Welcome.txt");
                WelcomeBox.Text = text;
                WelcomeBox.SelectionStart = 0;
                WelcomeBox.SelectionLength = 0;
            }
            catch (Exception ex)
            {
                AdvisoryDialog.Show("Welcome Text Not Available",
                    $"The welcome text could not be loaded.\n\n{ex.Message}");
                DialogResult = false;
                Close();
            }
        }

        private void DocButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _docName,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AdvisoryDialog.Show("Documentation Not Available",
                    $"The documentation could not be opened.\n\n{ex.Message}");
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_importAction?.Invoke() == true)
            {
                DialogResult = true;
                Close();
            }
        }

        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
