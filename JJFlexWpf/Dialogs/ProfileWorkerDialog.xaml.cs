using System;
using System.Collections.Generic;
using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Profile data returned by the dialog.
    /// </summary>
    public class ProfileWorkerResult
    {
        public string Name { get; set; } = "";
        public int ProfileTypeIndex { get; set; }
        public bool IsDefault { get; set; }
    }

    public partial class ProfileWorkerDialog : JJFlexDialog
    {
        private const string MustSpecifyName = "You must specify a name.";
        private const string MustBeUnique = "The name must be unique within this type.";
        private const string MustSelectType = "You must select a type.";

        private readonly bool _isUpdate;
        private readonly Func<int, IEnumerable<string>> _getProfileNamesByType;

        /// <summary>
        /// The profile data after dialog closes with OK.
        /// </summary>
        public ProfileWorkerResult Result { get; private set; }

        /// <summary>
        /// Creates the profile worker dialog.
        /// </summary>
        /// <param name="profileTypeNames">Names of available profile types</param>
        /// <param name="existingName">Name of existing profile (null for new)</param>
        /// <param name="existingTypeIndex">Type index of existing profile</param>
        /// <param name="existingDefault">Default flag of existing profile</param>
        /// <param name="getProfileNamesByType">Returns profile names for a given type index (for uniqueness check on add)</param>
        public ProfileWorkerDialog(
            string[] profileTypeNames,
            string? existingName,
            int existingTypeIndex,
            bool existingDefault,
            Func<int, IEnumerable<string>> getProfileNamesByType)
        {
            _isUpdate = existingName != null;
            _getProfileNamesByType = getProfileNamesByType;

            InitializeComponent();

            foreach (var name in profileTypeNames)
                TypeBox.Items.Add(name);

            if (_isUpdate)
            {
                NameBox.Text = existingName;
                TypeBox.SelectedIndex = existingTypeIndex;
                DefaultBox.IsChecked = existingDefault;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(MustSpecifyName, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }

            if (TypeBox.SelectedIndex == -1)
            {
                MessageBox.Show(MustSelectType, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                TypeBox.Focus();
                return;
            }

            // Check uniqueness on add
            if (!_isUpdate && _getProfileNamesByType != null)
            {
                foreach (var existingName in _getProfileNamesByType(TypeBox.SelectedIndex))
                {
                    if (existingName == name)
                    {
                        MessageBox.Show(MustBeUnique, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                        NameBox.Focus();
                        return;
                    }
                }
            }

            Result = new ProfileWorkerResult
            {
                Name = name,
                ProfileTypeIndex = TypeBox.SelectedIndex,
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
    }
}
