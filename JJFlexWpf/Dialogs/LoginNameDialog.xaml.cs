using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class LoginNameDialog : JJFlexDialog
    {
        private const string MustHaveAddress = "You must specify a cluster address.";
        private const string MustHaveName = "You must specify a login name.";

        private readonly Func<string, bool> _validateHostname;

        /// <summary>
        /// The cluster hostname entered by the user.
        /// </summary>
        public string ClusterAddress { get; private set; } = "";

        /// <summary>
        /// The login name entered by the user.
        /// </summary>
        public string ClusterLoginName { get; private set; } = "";

        /// <summary>
        /// Creates the login name dialog.
        /// </summary>
        /// <param name="currentAddress">Current cluster hostname</param>
        /// <param name="currentLoginName">Current login name</param>
        /// <param name="validateHostname">Delegate to validate hostname; returns true if valid</param>
        public LoginNameDialog(string currentAddress, string currentLoginName,
            Func<string, bool> validateHostname)
        {
            _validateHostname = validateHostname;
            InitializeComponent();
            AddressBox.Text = currentAddress ?? "";
            LoginBox.Text = currentLoginName ?? "";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var address = AddressBox.Text.Trim();
            var loginName = LoginBox.Text.Trim();

            if (_validateHostname != null && !_validateHostname(address))
            {
                MessageBox.Show(MustHaveAddress, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                AddressBox.Focus();
                return;
            }

            if (string.IsNullOrEmpty(loginName))
            {
                MessageBox.Show(MustHaveName, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                LoginBox.Focus();
                return;
            }

            ClusterAddress = address;
            ClusterLoginName = loginName;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
