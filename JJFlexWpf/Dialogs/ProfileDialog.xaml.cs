using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Profile data item for display in the list.
    /// </summary>
    public class ProfileDisplayItem
    {
        public string DisplayText { get; set; } = "";
        public object ProfileData { get; set; } = null!;

        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// Delegates for profile operations, keeping the dialog decoupled from FlexLib.
    /// </summary>
    public class ProfileDialogCallbacks
    {
        /// <summary>Returns list of profile display items for the list.</summary>
        public required Func<List<ProfileDisplayItem>> GetDisplayItems { get; init; }

        /// <summary>Returns profile type names for the ProfileWorker dialog.</summary>
        public required Func<string[]> GetProfileTypeNames { get; init; }

        /// <summary>Returns profile names for a given type index (uniqueness check).</summary>
        public required Func<int, IEnumerable<string>> GetProfileNamesByType { get; init; }

        /// <summary>Called when user adds a profile. Receives the ProfileWorkerResult.</summary>
        public required Action<ProfileWorkerResult> OnAdd { get; init; }

        /// <summary>Called when user updates a profile. Receives (original data, new ProfileWorkerResult).</summary>
        public required Action<object, ProfileWorkerResult> OnUpdate { get; init; }

        /// <summary>Called when user deletes a profile. Receives the profile data. Returns error message or null.</summary>
        public required Func<object, string?> OnDelete { get; init; }

        /// <summary>Called when user selects a profile. Receives profile data. Returns error message or null.</summary>
        public required Func<object, string?> OnSelect { get; init; }

        /// <summary>Called when user saves a profile (global only). Receives profile data.</summary>
        public required Action<object> OnSave { get; init; }

        /// <summary>Returns true if the profile is a global type (to show/hide Save button).</summary>
        public required Func<object, bool> IsGlobalProfile { get; init; }

        /// <summary>Returns (name, typeIndex, isDefault) for editing an existing profile.</summary>
        public required Func<object, (string name, int typeIndex, bool isDefault)> GetProfileEditData { get; init; }
    }

    public partial class ProfileDialog : JJFlexDialog
    {
        private const string MustSelectProfile = "You must select a profile.";

        private readonly ProfileDialogCallbacks _callbacks;

        public ProfileDialog(ProfileDialogCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            InitializeComponent();
            RefreshList();
        }

        private void RefreshList()
        {
            var selectedIndex = ProfilesListBox.SelectedIndex;
            var items = _callbacks.GetDisplayItems();
            ProfilesListBox.Items.Clear();
            foreach (var item in items)
                ProfilesListBox.Items.Add(item);

            if (selectedIndex >= 0 && selectedIndex < ProfilesListBox.Items.Count)
                ProfilesListBox.SelectedIndex = selectedIndex;

            ProfilesListBox.Focus();
        }

        private ProfileDisplayItem? GetSelectedItem()
        {
            return ProfilesListBox.SelectedItem as ProfileDisplayItem;
        }

        private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = GetSelectedItem();
            if (item != null && _callbacks.IsGlobalProfile(item.ProfileData))
            {
                SaveButton.Visibility = Visibility.Visible;
            }
            else
            {
                SaveButton.Visibility = Visibility.Collapsed;
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null)
            {
                MessageBox.Show(MustSelectProfile, "Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProfilesListBox.Focus();
                return;
            }

            var error = _callbacks.OnSelect(item.ProfileData);
            if (error != null)
            {
                MessageBox.Show(error, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProfilesListBox.Focus();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var worker = new ProfileWorkerDialog(
                _callbacks.GetProfileTypeNames(),
                null, 0, false,
                _callbacks.GetProfileNamesByType);

            if (worker.ShowDialog() == true && worker.Result != null)
            {
                _callbacks.OnAdd(worker.Result);
                RefreshList();
            }
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null)
            {
                MessageBox.Show(MustSelectProfile, "Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProfilesListBox.Focus();
                return;
            }

            var (name, typeIndex, isDefault) = _callbacks.GetProfileEditData(item.ProfileData);
            var worker = new ProfileWorkerDialog(
                _callbacks.GetProfileTypeNames(),
                name, typeIndex, isDefault,
                _callbacks.GetProfileNamesByType);

            if (worker.ShowDialog() == true && worker.Result != null)
            {
                _callbacks.OnUpdate(item.ProfileData, worker.Result);
                RefreshList();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null)
            {
                MessageBox.Show(MustSelectProfile, "Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProfilesListBox.Focus();
                return;
            }

            var error = _callbacks.OnDelete(item.ProfileData);
            if (error != null)
            {
                MessageBox.Show(error, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProfilesListBox.Focus();
                return;
            }

            RefreshList();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null)
            {
                MessageBox.Show(MustSelectProfile, "Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProfilesListBox.Focus();
                return;
            }

            _callbacks.OnSave(item.ProfileData);
            RefreshList();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
