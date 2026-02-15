using System.Windows;
using Microsoft.Win32;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Log configuration result.
    /// </summary>
    public class LogConfigResult
    {
        public string FileName { get; set; } = "";
        public int FormIndex { get; set; }
        public int DupCheckIndex { get; set; }
        public int FirstSerial { get; set; } = 1;
        public int LookupIndex { get; set; }
    }

    public partial class LogCharacteristicsDialog : JJFlexDialog
    {
        /// <summary>Initial log file name. Set before showing.</summary>
        public string InitialFileName { get; set; } = "";
        /// <summary>Available log form names. Set before showing.</summary>
        public string[]? FormNames { get; set; }
        /// <summary>Available dup check types. Set before showing.</summary>
        public string[]? DupCheckTypes { get; set; }
        /// <summary>Available lookup choices. Set before showing.</summary>
        public string[]? LookupChoices { get; set; }
        /// <summary>Default form index.</summary>
        public int DefaultFormIndex { get; set; }
        /// <summary>Default dup check index.</summary>
        public int DefaultDupIndex { get; set; }
        /// <summary>Default lookup choice index.</summary>
        public int DefaultLookupIndex { get; set; } = 1;

        /// <summary>
        /// Called when a log file is selected to read its header and return config values.
        /// Receives file path, returns (formIndex, dupIndex, serial, lookupIndex) or null on error.
        /// </summary>
        public Func<string, (int formIndex, int dupIndex, int serial, int lookupIndex)?>? ReadLogHeader { get; set; }

        /// <summary>
        /// Recent log file paths for quick selection.
        /// </summary>
        public string[]? RecentFiles { get; set; }

        /// <summary>
        /// The result configuration. Set after OK is clicked.
        /// </summary>
        public LogConfigResult? Result { get; private set; }

        public LogCharacteristicsDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            NameBox.Text = InitialFileName;
            FormList.ItemsSource = FormNames;
            DupList.ItemsSource = DupCheckTypes;
            LookupList.ItemsSource = LookupChoices;

            // Try to read existing log header
            if (!string.IsNullOrEmpty(InitialFileName) && System.IO.File.Exists(InitialFileName))
            {
                var info = ReadLogHeader?.Invoke(InitialFileName);
                if (info != null)
                {
                    FormList.SelectedIndex = info.Value.formIndex;
                    FormList.IsEnabled = false;
                    DupList.SelectedIndex = info.Value.dupIndex;
                    FirstSerialBox.Text = info.Value.serial.ToString();
                    LookupList.SelectedIndex = info.Value.lookupIndex;
                }
            }
            else
            {
                FormList.IsEnabled = true;
                FormList.SelectedIndex = DefaultFormIndex;
                DupList.SelectedIndex = DefaultDupIndex;
                FirstSerialBox.Text = "1";
                LookupList.SelectedIndex = DefaultLookupIndex;
            }

            NameBox.Focus();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Log files (*.JRL)|*.JRL|All files (*.*)|*.*",
                Title = "Log Filename",
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (!string.IsNullOrEmpty(NameBox.Text))
            {
                ofd.FileName = System.IO.Path.GetFileName(NameBox.Text);
                string? dir = System.IO.Path.GetDirectoryName(NameBox.Text);
                if (dir != null) ofd.InitialDirectory = dir;
            }
            if (ofd.ShowDialog() == true)
            {
                NameBox.Text = ofd.FileName;
                LoadLogInfo(ofd.FileName);
            }
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            LoadLogInfo(NameBox.Text);
        }

        private void LoadLogInfo(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            var info = ReadLogHeader?.Invoke(path);
            if (info != null)
            {
                FormList.SelectedIndex = info.Value.formIndex;
                FormList.IsEnabled = false;
                DupList.SelectedIndex = info.Value.dupIndex;
                FirstSerialBox.Text = info.Value.serial.ToString();
                LookupList.SelectedIndex = info.Value.lookupIndex;
            }
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                MessageBox.Show("You must specify a valid file name.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }
            if (!int.TryParse(FirstSerialBox.Text, out int serial))
            {
                MessageBox.Show("The first serial number must be numeric.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                FirstSerialBox.Focus();
                return;
            }
            if (FormList.SelectedIndex < 0)
            {
                MessageBox.Show("You must select a log form.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                FormList.Focus();
                return;
            }

            Result = new LogConfigResult
            {
                FileName = NameBox.Text,
                FormIndex = FormList.SelectedIndex,
                DupCheckIndex = DupList.SelectedIndex >= 0 ? DupList.SelectedIndex : 0,
                FirstSerial = serial,
                LookupIndex = LookupList.SelectedIndex >= 0 ? LookupList.SelectedIndex : DefaultLookupIndex
            };
            DialogResult = true;
            Close();
        }
    }
}
