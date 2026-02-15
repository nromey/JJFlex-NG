using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Read-only memory group browser. Shows all groups and their members.
    /// For editing groups, use ManageGroupsDialog instead.
    /// </summary>
    public partial class MemoryGroupDialog : JJFlexDialog
    {
        /// <summary>
        /// Returns the list of all memory groups. Set before showing.
        /// </summary>
        public Func<List<MemoryGroupInfo>>? GetAllGroups { get; set; }

        /// <summary>
        /// The selected group when Done is clicked. Null if none selected.
        /// </summary>
        public MemoryGroupInfo? SelectedGroup { get; private set; }

        private List<MemoryGroupInfo> _groups = new();

        public MemoryGroupDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _groups = GetAllGroups?.Invoke() ?? new();
            GroupsListBox.ItemsSource = _groups;
            GroupsListBox.Focus();
        }

        private void GroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GroupsListBox.SelectedItem is MemoryGroupInfo group)
            {
                SelectedGroup = group;
                MembersListBox.ItemsSource = group.Members;
            }
            else
            {
                SelectedGroup = null;
                MembersListBox.ItemsSource = null;
            }
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
