using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class ShowBandsDialog : JJFlexDialog
    {
        /// <summary>Band names list. Set before showing.</summary>
        public string[]? BandNames { get; set; }
        /// <summary>License class names. Set before showing.</summary>
        public string[]? LicenseNames { get; set; }
        /// <summary>Mode names. Set before showing.</summary>
        public string[]? ModeNames { get; set; }

        /// <summary>Initial band selection index. -1 for none.</summary>
        public int InitialBandIndex { get; set; } = -1;
        /// <summary>Initial license selection index.</summary>
        public int InitialLicenseIndex { get; set; } = -1;
        /// <summary>Initial mode selection index.</summary>
        public int InitialModeIndex { get; set; } = -1;

        /// <summary>
        /// Query delegate. Receives (bandIndex, licenseIndex, modeIndex).
        /// licenseIndex/modeIndex of 0 means "All" (no filter).
        /// Returns formatted result string, or null if band not selected.
        /// </summary>
        public Func<int, int, int, string?>? QueryBands { get; set; }

        public ShowBandsDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            BandBox.ItemsSource = BandNames;
            LicenseBox.ItemsSource = LicenseNames;
            ModeBox.ItemsSource = ModeNames;

            if (InitialBandIndex >= 0) BandBox.SelectedIndex = InitialBandIndex;
            if (InitialLicenseIndex >= 0) LicenseBox.SelectedIndex = InitialLicenseIndex;
            if (InitialModeIndex >= 0) ModeBox.SelectedIndex = InitialModeIndex;
        }

        private void ShowButton_Click(object sender, RoutedEventArgs e)
        {
            if (BandBox.SelectedIndex < 0)
            {
                MessageBox.Show("Select a band.", "Required",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                BandBox.Focus();
                return;
            }

            string? result = QueryBands?.Invoke(
                BandBox.SelectedIndex,
                LicenseBox.SelectedIndex,
                ModeBox.SelectedIndex);

            if (result != null)
            {
                ResultBox.Text = result;
                BandBox.SelectedIndex = -1;
                LicenseBox.SelectedIndex = -1;
                ModeBox.SelectedIndex = -1;
                ResultBox.Focus();
            }
        }
    }
}
