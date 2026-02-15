using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class ShowStationNamesDialog : JJFlexDialog
    {
        /// <summary>
        /// List of station names to display. Set before calling ShowDialog().
        /// </summary>
        public List<string>? StationNames { get; set; }

        public ShowStationNamesDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (StationNames == null || StationNames.Count == 0)
            {
                DialogResult = false;
                Close();
                return;
            }
            StationsList.ItemsSource = StationNames;
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
