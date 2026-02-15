using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Checkable group item for memory scan selection.
    /// </summary>
    public class CheckableGroupItem
    {
        public string Name { get; set; } = "";
        public bool IsChecked { get; set; }
        public List<string> Members { get; set; } = new();
    }

    public partial class MemoryScanDialog : JJFlexDialog
    {
        /// <summary>
        /// Returns the list of available memory groups with their members.
        /// </summary>
        public Func<List<CheckableGroupItem>>? GetGroups { get; set; }

        /// <summary>
        /// Called to start the scan. Receives (selectedMembers, speedValue).
        /// </summary>
        public Action<List<string>, int>? StartScan { get; set; }

        /// <summary>
        /// Called when the user clicks "Manage Groups". May show ManageGroupsDialog.
        /// </summary>
        public Action? ManageGroups { get; set; }

        private List<CheckableGroupItem> _groups = new();

        public MemoryScanDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshGroups();
            GroupsCheckBox.Focus();
        }

        private void RefreshGroups()
        {
            _groups = GetGroups?.Invoke() ?? new();
            GroupsCheckBox.ItemsSource = null;
            GroupsCheckBox.ItemsSource = _groups;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate groups selected
            var checkedGroups = _groups.Where(g => g.IsChecked).ToList();
            if (checkedGroups.Count == 0)
            {
                MessageBox.Show("At least one group must be selected.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                GroupsCheckBox.Focus();
                return;
            }

            // Validate speed
            if (!int.TryParse(SpeedBox.Text, out int speed) || speed < 1 || speed > 600)
            {
                MessageBox.Show("Speed must be between 1 and 600.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SpeedBox.Focus();
                return;
            }

            // Collect all members from checked groups
            var members = new List<string>();
            foreach (var group in checkedGroups)
            {
                members.AddRange(group.Members);
            }

            if (members.Count == 0)
            {
                MessageBox.Show("There are no memories to scan.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                GroupsCheckBox.Focus();
                return;
            }

            StartScan?.Invoke(members, speed);
            DialogResult = true;
            Close();
        }

        private void ManageButton_Click(object sender, RoutedEventArgs e)
        {
            ManageGroups?.Invoke();
            RefreshGroups();
        }
    }
}
