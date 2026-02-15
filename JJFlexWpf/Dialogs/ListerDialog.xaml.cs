using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Display item for the lister.
    /// </summary>
    public class ListerItem
    {
        public string Display { get; set; } = "";
        public bool IsSelected { get; set; }
        public object? Tag { get; set; }

        public override string ToString() => Display;
    }

    public partial class ListerDialog : JJFlexDialog
    {
        /// <summary>Returns the list of items to display.</summary>
        public Func<List<ListerItem>>? GetItems { get; set; }

        /// <summary>Called to add a new item. Returns the index of the new item, or -1.</summary>
        public Func<int>? AddItem { get; set; }

        /// <summary>Called to update an item at the given index.</summary>
        public Action<int>? UpdateItem { get; set; }

        /// <summary>Called to delete an item at the given index. Returns true if deleted.</summary>
        public Func<int, bool>? DeleteItem { get; set; }

        /// <summary>Called when selection changes. Receives the selected index.</summary>
        public Action<int>? OnSelectionChanged { get; set; }

        /// <summary>The index of the currently active/selected item (radio-button style).</summary>
        public int ActiveIndex { get; set; } = -1;

        public ListerDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshList(-1);
            ScreenList.Focus();
        }

        private void RefreshList(int selectIndex)
        {
            var items = GetItems?.Invoke() ?? new();
            ScreenList.ItemsSource = null;
            ScreenList.ItemsSource = items;
            if (selectIndex >= 0 && selectIndex < items.Count)
                ScreenList.SelectedIndex = selectIndex;
            else if (items.Count > 0)
                ScreenList.SelectedIndex = 0;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            int newIndex = AddItem?.Invoke() ?? -1;
            RefreshList(newIndex >= 0 ? newIndex : ScreenList.SelectedIndex);
            ScreenList.Focus();
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = ScreenList.SelectedIndex;
            if (idx < 0)
            {
                MessageBox.Show("You must select an item.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            UpdateItem?.Invoke(idx);
            RefreshList(idx);
            ScreenList.Focus();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = ScreenList.SelectedIndex;
            if (idx < 0)
            {
                MessageBox.Show("You must select an item.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (idx == ActiveIndex)
            {
                MessageBox.Show("You may not remove the currently selected item.",
                    "Removing Selected Item", MessageBoxButton.OK, MessageBoxImage.Warning);
                ScreenList.Focus();
                return;
            }
            bool deleted = DeleteItem?.Invoke(idx) ?? false;
            if (deleted)
            {
                if (idx <= ActiveIndex) ActiveIndex--;
                RefreshList(Math.Min(idx, (ScreenList.Items.Count - 2)));
            }
            ScreenList.Focus();
        }

        private void ScreenList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScreenList.SelectedIndex >= 0)
                OnSelectionChanged?.Invoke(ScreenList.SelectedIndex);
        }
    }
}
