using System;
using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Operator data for the PersonalInfo dialog.
    /// </summary>
    public class OperatorData
    {
        public string FullName { get; set; } = "";
        public string CallSign { get; set; } = "";
        public string Handle { get; set; } = "";
        public string QTH { get; set; } = "";
        public string GridSquare { get; set; } = "";
        public int LicenseIndex { get; set; }
        public string BrailleDisplaySize { get; set; } = "";
        public string ClusterHostname { get; set; } = "";
        public string ClusterLoginName { get; set; } = "";
        public string CallbookLookupSource { get; set; } = "None";
        public string CallbookUsername { get; set; } = "";
        public string CallbookPassword { get; set; } = "";
        public int RecentQsoCount { get; set; } = 10;
        public bool QrzLogbookEnabled { get; set; }
        public string QrzLogbookApiKey { get; set; } = "";
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Callbacks for the PersonalInfo dialog.
    /// </summary>
    public class PersonalInfoCallbacks
    {
        /// <summary>License class names for the combo box.</summary>
        public required string[] LicenseNames { get; init; }

        /// <summary>Callbook source options (e.g. "None", "QRZ", "HamQTH").</summary>
        public required string[] CallbookSources { get; init; }

        /// <summary>Validate hostname; returns true if valid.</summary>
        public required Func<string, bool> ValidateHostname { get; init; }

        /// <summary>Check for duplicate operator (fullName, handle); returns true if dup found.</summary>
        public required Func<string, string, bool> IsDuplicate { get; init; }

        /// <summary>Validate callbook credentials (source, username, password); returns true if should proceed.</summary>
        public required Func<string, string, string, bool> ValidateCallbookCredentials { get; init; }

        /// <summary>Validate QRZ Logbook API key; shows result dialog.</summary>
        public required Action<string> ValidateQrzLogbookKey { get; init; }

        /// <summary>Open the log settings dialog.</summary>
        public required Action OpenLogSettings { get; init; }

        /// <summary>Max value for recent QSO count.</summary>
        public int MaxRecentQsoCount { get; init; } = 100;
    }

    public partial class PersonalInfoDialog : JJFlexDialog
    {
        private const string NoDupMsg = "There is already an operator with this name and handle.";
        private const string NoName = "The operator must have a name";
        private const string BrlDispError = "The braille display size must be a positive, nonzero, value.";
        private const string NotValidHost = "The cluster address is not a valid hostname.";

        private readonly PersonalInfoCallbacks _callbacks;
        private readonly bool _isUpdate;

        /// <summary>
        /// The operator data after OK is clicked.
        /// </summary>
        public OperatorData Result { get; private set; } = null!;

        /// <summary>
        /// Creates the PersonalInfo dialog.
        /// </summary>
        /// <param name="existingData">Existing operator data (null for new operator)</param>
        /// <param name="callbacks">Callbacks for validation and actions</param>
        public PersonalInfoDialog(OperatorData? existingData, PersonalInfoCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            _isUpdate = existingData != null;

            InitializeComponent();

            // Populate license list
            foreach (var name in callbacks.LicenseNames)
                LicenseList.Items.Add(name);

            // Populate callbook sources
            foreach (var src in callbacks.CallbookSources)
                CallbookSourceCombo.Items.Add(src);

            if (_isUpdate)
            {
                OKButton.Content = "Update";
                PopulateFields(existingData!);
            }
            else
            {
                OKButton.Content = "Add";
            }

            UpdateCallbookFieldsEnabled();
            UpdateQrzLogbookFieldsEnabled();
        }

        private void PopulateFields(OperatorData data)
        {
            FullNameBox.Text = data.FullName;
            CallSignBox.Text = data.CallSign;
            HandleBox.Text = data.Handle;
            QTHBox.Text = data.QTH;
            GridSquareBox.Text = data.GridSquare;
            LicenseList.SelectedIndex = data.LicenseIndex;
            BRLSizeBox.Text = data.BrailleDisplaySize;
            AddressBox.Text = data.ClusterHostname;
            ClusterLoginNameBox.Text = string.IsNullOrEmpty(data.ClusterLoginName)
                ? data.CallSign : data.ClusterLoginName;

            var srcIdx = -1;
            for (int i = 0; i < CallbookSourceCombo.Items.Count; i++)
            {
                if (CallbookSourceCombo.Items[i]?.ToString() == (data.CallbookLookupSource ?? "None"))
                {
                    srcIdx = i;
                    break;
                }
            }
            CallbookSourceCombo.SelectedIndex = srcIdx >= 0 ? srcIdx : 0;
            CallbookUsernameBox.Text = data.CallbookUsername;
            CallbookPasswordBox.Password = data.CallbookPassword;

            RecentQsoBox.Text = data.RecentQsoCount.ToString();
            QrzLogbookEnabledBox.IsChecked = data.QrzLogbookEnabled;
            QrzLogbookApiKeyBox.Text = data.QrzLogbookApiKey;
            DefaultBox.IsChecked = data.IsDefault;

            // Can't unset default on update if already default
            DefaultBox.IsEnabled = !(_isUpdate && data.IsDefault);
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            var fullName = FullNameBox.Text.Trim();
            if (string.IsNullOrEmpty(fullName))
            {
                MessageBox.Show(NoName, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                FullNameBox.Focus();
                return;
            }

            var handle = HandleBox.Text.Trim();
            if (_callbacks.IsDuplicate(fullName, handle))
            {
                MessageBox.Show(NoDupMsg, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                FullNameBox.Focus();
                return;
            }

            var clusterHost = AddressBox.Text.Trim();
            if (!string.IsNullOrEmpty(clusterHost) && !_callbacks.ValidateHostname(clusterHost))
            {
                MessageBox.Show(NotValidHost, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                AddressBox.Focus();
                return;
            }

            // Validate braille display size
            int brlSize = 0;
            var brlText = BRLSizeBox.Text.Trim();
            if (!string.IsNullOrEmpty(brlText))
            {
                if (!int.TryParse(brlText, out brlSize) || brlSize < 0)
                {
                    MessageBox.Show(BrlDispError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    BRLSizeBox.Focus();
                    return;
                }
            }

            // Validate callbook credentials
            var callbookSource = CallbookSourceCombo.SelectedItem?.ToString() ?? "None";
            var callbookUser = CallbookUsernameBox.Text.Trim();
            var callbookPass = CallbookPasswordBox.Password.Trim();
            if (callbookSource != "None" && !string.IsNullOrEmpty(callbookUser) && !string.IsNullOrEmpty(callbookPass))
            {
                if (!_callbacks.ValidateCallbookCredentials(callbookSource, callbookUser, callbookPass))
                {
                    CallbookUsernameBox.Focus();
                    return;
                }
            }

            // Parse recent QSO count
            int recentQsoCount = 10;
            if (int.TryParse(RecentQsoBox.Text.Trim(), out int parsed))
            {
                recentQsoCount = Math.Max(0, Math.Min(_callbacks.MaxRecentQsoCount, parsed));
            }

            Result = new OperatorData
            {
                FullName = fullName,
                CallSign = CallSignBox.Text.Trim(),
                Handle = handle,
                QTH = QTHBox.Text.Trim(),
                GridSquare = GridSquareBox.Text.Trim().ToUpper(),
                LicenseIndex = LicenseList.SelectedIndex >= 0 ? LicenseList.SelectedIndex : 0,
                BrailleDisplaySize = brlText,
                ClusterHostname = clusterHost,
                ClusterLoginName = ClusterLoginNameBox.Text.Trim(),
                CallbookLookupSource = callbookSource,
                CallbookUsername = callbookUser,
                CallbookPassword = callbookPass,
                RecentQsoCount = recentQsoCount,
                QrzLogbookEnabled = QrzLogbookEnabledBox.IsChecked == true,
                QrzLogbookApiKey = QrzLogbookApiKeyBox.Text.Trim(),
                IsDefault = DefaultBox.IsChecked == true
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ClusterLoginNameBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ClusterLoginNameBox.Text))
                ClusterLoginNameBox.Text = CallSignBox.Text;
        }

        private void UpdateCallbookFieldsEnabled()
        {
            var enabled = CallbookSourceCombo.SelectedItem?.ToString() != "None";
            CallbookUsernameBox.IsEnabled = enabled;
            CallbookPasswordBox.IsEnabled = enabled;
            CallbookUsernameLabel.IsEnabled = enabled;
            CallbookPasswordLabel.IsEnabled = enabled;
        }

        private void CallbookSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCallbookFieldsEnabled();
        }

        private void UpdateQrzLogbookFieldsEnabled()
        {
            var enabled = QrzLogbookEnabledBox.IsChecked == true;
            QrzLogbookApiKeyLabel.IsEnabled = enabled;
            QrzLogbookApiKeyBox.IsEnabled = enabled;
            QrzLogbookValidateButton.IsEnabled = enabled;
        }

        private void QrzLogbookEnabledBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateQrzLogbookFieldsEnabled();
        }

        private void QrzLogbookValidateButton_Click(object sender, RoutedEventArgs e)
        {
            var apiKey = QrzLogbookApiKeyBox.Text.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show("Enter an API key first.", "QRZ Logbook", MessageBoxButton.OK, MessageBoxImage.Information);
                QrzLogbookApiKeyBox.Focus();
                return;
            }

            _callbacks.ValidateQrzLogbookKey(apiKey);
        }

        private void LogButton_Click(object sender, RoutedEventArgs e)
        {
            _callbacks.OpenLogSettings();
        }
    }
}
