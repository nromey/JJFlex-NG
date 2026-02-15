using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    public partial class CWMessageUpdateDialog : JJFlexDialog
    {
        /// <summary>
        /// Returns the current list of CW message labels.
        /// </summary>
        public Func<string[]>? GetMessageLabels { get; set; }

        /// <summary>
        /// Called to add a new CW message. May show a sub-dialog internally.
        /// </summary>
        public Action? AddMessage { get; set; }

        /// <summary>
        /// Called to update a CW message at the given index. May show a sub-dialog internally.
        /// </summary>
        public Action<int>? UpdateMessage { get; set; }

        /// <summary>
        /// Called to delete a CW message at the given index.
        /// </summary>
        public Action<int>? DeleteMessage { get; set; }

        public CWMessageUpdateDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshList();
            KeysList.Focus();
        }

        private void RefreshList()
        {
            int oldIndex = KeysList.SelectedIndex;
            string[]? labels = GetMessageLabels?.Invoke();
            KeysList.ItemsSource = null;
            KeysList.ItemsSource = labels;
            if (labels != null && labels.Length > 0)
            {
                KeysList.SelectedIndex = Math.Min(oldIndex, labels.Length - 1);
                if (KeysList.SelectedIndex < 0) KeysList.SelectedIndex = 0;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            AddMessage?.Invoke();
            RefreshList();
            KeysList.Focus();
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (KeysList.SelectedIndex < 0) return;
            UpdateMessage?.Invoke(KeysList.SelectedIndex);
            RefreshList();
            KeysList.Focus();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (KeysList.SelectedIndex < 0) return;
            int idx = KeysList.SelectedIndex;
            if (idx > 0) KeysList.SelectedIndex = idx - 1;
            DeleteMessage?.Invoke(idx);
            RefreshList();
            string[]? labels = GetMessageLabels?.Invoke();
            if (labels == null || labels.Length == 0)
            {
                DialogResult = false;
                Close();
                return;
            }
            KeysList.Focus();
        }

        private void KeysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButton.IsDefault = KeysList.SelectedIndex >= 0;
            AddButton.IsDefault = KeysList.SelectedIndex < 0;
        }
    }
}
