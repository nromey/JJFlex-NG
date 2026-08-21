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
                // Never a bare DialogResult assignment from Loaded: that threw
                // on windows realised with Show() and aborted the Tier 1 dialog
                // suite on 2026-08-20/21 — see JJFlexDialog.CloseWithResult (#159).
                // Note this exit is SILENT — the window opens and vanishes with
                // no explanation. Tolerable only because the caller is expected
                // to guard; if that ever proves false, say why, the way
                // SelectScanDialog announces "No scans were saved."
                CloseWithResult(false);
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
