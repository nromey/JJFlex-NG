using System.Windows;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    public partial class SelectScanDialog : JJFlexDialog
    {
        /// <summary>
        /// Scan names to populate the list. Set before calling ShowDialog().
        /// </summary>
        public string[]? ScanNames { get; set; }

        /// <summary>
        /// The selected scan index. -1 if none selected.
        /// </summary>
        public int SelectedIndex { get; private set; } = -1;

        public SelectScanDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ScanNames == null || ScanNames.Length == 0)
            {
                MessageBox.Show("No scans were saved.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = false;
                Close();
                return;
            }
            NameListBox.ItemsSource = ScanNames;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (NameListBox.SelectedIndex < 0)
            {
                MessageBox.Show("You must select an item.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SelectedIndex = NameListBox.SelectedIndex;
            DialogResult = true;
            Close();
        }

        private void NameListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (NameListBox.SelectedIndex >= 0)
            {
                SelectedIndex = NameListBox.SelectedIndex;
                DialogResult = true;
                Close();
            }
        }
    }
}
