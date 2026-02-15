using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Watt meter configuration result.
    /// </summary>
    public class WattMeterConfig
    {
        public string Port { get; set; } = "";
        public int DispositionIndex { get; set; }
        public int PowerTypeIndex { get; set; }
    }

    public partial class WattMeterConfigDialog : JJFlexDialog
    {
        /// <summary>Available COM ports. Set before showing.</summary>
        public string[]? AvailablePorts { get; set; }
        /// <summary>Disposition/usage choices. Set before showing.</summary>
        public string[]? DispositionChoices { get; set; }
        /// <summary>Power type choices. Set before showing.</summary>
        public string[]? PowerTypeChoices { get; set; }

        /// <summary>Current port selection.</summary>
        public string CurrentPort { get; set; } = "";
        /// <summary>Current disposition index.</summary>
        public int CurrentDisposition { get; set; } = -1;
        /// <summary>Current power type index.</summary>
        public int CurrentPowerType { get; set; } = -1;

        /// <summary>The configured result. Set after Configure is clicked.</summary>
        public WattMeterConfig? Result { get; private set; }

        public WattMeterConfigDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PortList.ItemsSource = AvailablePorts;
            UsageList.ItemsSource = DispositionChoices;
            PowerTypeList.ItemsSource = PowerTypeChoices;

            // Pre-select current values
            if (AvailablePorts != null && !string.IsNullOrEmpty(CurrentPort))
            {
                int idx = Array.IndexOf(AvailablePorts, CurrentPort);
                if (idx >= 0) PortList.SelectedIndex = idx;
            }
            if (CurrentDisposition >= 0) UsageList.SelectedIndex = CurrentDisposition;
            if (CurrentPowerType >= 0) PowerTypeList.SelectedIndex = CurrentPowerType;
        }

        private void ConfigureButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate
            if (UsageList.SelectedIndex < 0)
            {
                MessageBox.Show("You must select a usage mode.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UsageList.Focus();
                return;
            }
            if (PowerTypeList.SelectedIndex < 0)
            {
                MessageBox.Show("You must select a power type.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                PowerTypeList.Focus();
                return;
            }
            // Port not required if disposition is "don't use" (typically index 0)
            if (UsageList.SelectedIndex > 0 && PortList.SelectedIndex < 0)
            {
                MessageBox.Show("You must select a COM port.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                PortList.Focus();
                return;
            }

            Result = new WattMeterConfig
            {
                Port = PortList.SelectedItem as string ?? "",
                DispositionIndex = UsageList.SelectedIndex,
                PowerTypeIndex = PowerTypeList.SelectedIndex
            };
            DialogResult = true;
            Close();
        }
    }
}
