using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Simple data class for memory group info.
    /// </summary>
    public class MemoryGroupInfo
    {
        public string Name { get; set; } = "";
        public List<string> Members { get; set; } = new();
        public bool IsReadOnly { get; set; }

        public override string ToString() => Name;
    }

    public partial class ManageGroupsDialog : JJFlexDialog
    {
        /// <summary>
        /// Returns the list of user groups. Called to populate/refresh the list.
        /// </summary>
        public Func<List<MemoryGroupInfo>>? GetUserGroups { get; set; }

        /// <summary>
        /// Called to add a new group. Receives the new group info.
        /// </summary>
        public Action<MemoryGroupInfo>? OnAddGroup { get; set; }

        /// <summary>
        /// Called to update an existing group. Receives (oldGroup, newGroup).
        /// </summary>
        public Action<MemoryGroupInfo, MemoryGroupInfo>? OnUpdateGroup { get; set; }

        /// <summary>
        /// Called to delete a group.
        /// </summary>
        public Action<MemoryGroupInfo>? OnDeleteGroup { get; set; }

        /// <summary>
        /// Returns the list of all memory names for the group editor.
        /// </summary>
        public Func<List<string>>? GetMemoryNames { get; set; }

        /// <summary>
        /// Returns existing group names for duplicate validation.
        /// </summary>
        public Func<List<string>>? GetExistingGroupNames { get; set; }

        public ManageGroupsDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshGroups();
            GroupsListBox.Focus();
        }

        private void RefreshGroups()
        {
            var groups = GetUserGroups?.Invoke();
            GroupsListBox.ItemsSource = null;
            GroupsListBox.ItemsSource = groups;
            MembersListBox.ItemsSource = null;
        }

        private void GroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GroupsListBox.SelectedItem is MemoryGroupInfo group)
            {
                MembersListBox.ItemsSource = group.Members;
            }
            else
            {
                MembersListBox.ItemsSource = null;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var editDialog = new ManageGroupsEditDialog
            {
                MemoryNames = GetMemoryNames?.Invoke(),
                ExistingGroupNames = GetExistingGroupNames?.Invoke()
            };
            if (editDialog.ShowDialog() == true && editDialog.ResultGroup != null)
            {
                OnAddGroup?.Invoke(editDialog.ResultGroup);
                RefreshGroups();
            }
            GroupsListBox.Focus();
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (GroupsListBox.SelectedItem is not MemoryGroupInfo selected)
            {
                MessageBox.Show("You must select a group.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                GroupsListBox.Focus();
                return;
            }
            var editDialog = new ManageGroupsEditDialog
            {
                ExistingGroup = selected,
                MemoryNames = GetMemoryNames?.Invoke(),
                ExistingGroupNames = GetExistingGroupNames?.Invoke()
            };
            if (editDialog.ShowDialog() == true && editDialog.ResultGroup != null)
            {
                OnUpdateGroup?.Invoke(selected, editDialog.ResultGroup);
                RefreshGroups();
            }
            GroupsListBox.Focus();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (GroupsListBox.SelectedItem is not MemoryGroupInfo selected)
            {
                MessageBox.Show("You must select a group.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                GroupsListBox.Focus();
                return;
            }
            OnDeleteGroup?.Invoke(selected);
            RefreshGroups();
            GroupsListBox.Focus();
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
