using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Checkable item for the members list.
    /// </summary>
    public class CheckableItem
    {
        public string Name { get; set; } = "";
        public bool IsChecked { get; set; }
    }

    public partial class ManageGroupsEditDialog : JJFlexDialog
    {
        /// <summary>
        /// Set to an existing group for update mode. Null for add mode.
        /// </summary>
        public MemoryGroupInfo? ExistingGroup { get; set; }

        /// <summary>
        /// All available memory names. Set before showing.
        /// </summary>
        public List<string>? MemoryNames { get; set; }

        /// <summary>
        /// Existing group names for duplicate validation. Set before showing.
        /// </summary>
        public List<string>? ExistingGroupNames { get; set; }

        /// <summary>
        /// The result group. Set after OK is clicked.
        /// </summary>
        public MemoryGroupInfo? ResultGroup { get; private set; }

        private List<CheckableItem> _items = new();

        public ManageGroupsEditDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (MemoryNames == null) return;

            var existingMembers = ExistingGroup?.Members ?? new List<string>();

            _items = MemoryNames.Select(name => new CheckableItem
            {
                Name = name,
                IsChecked = existingMembers.Contains(name)
            }).ToList();

            MembersBox.ItemsSource = _items;

            if (ExistingGroup != null)
            {
                NameBox.Text = ExistingGroup.Name;
                Title = "Edit Memory Group";
            }
            else
            {
                Title = "Add Memory Group";
            }
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();

            // Validate name
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("The group must have a unique name.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }

            // Check for duplicate name (skip if same name on update)
            bool isNewName = ExistingGroup == null || name != ExistingGroup.Name;
            if (isNewName && ExistingGroupNames != null && ExistingGroupNames.Contains(name))
            {
                MessageBox.Show("The group must have a unique name.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }

            // Validate members
            var checkedMembers = _items.Where(i => i.IsChecked).Select(i => i.Name).ToList();
            if (checkedMembers.Count == 0)
            {
                MessageBox.Show("The group must have at least one member.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultGroup = new MemoryGroupInfo
            {
                Name = name,
                Members = checkedMembers
            };
            DialogResult = true;
            Close();
        }
    }
}
