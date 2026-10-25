using System.Windows;
using System.Windows.Automation;
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

        /// <summary>
        /// True while the library holds no messages and the list shows the
        /// one empty-state row (#717). Update and Delete act on nothing then.
        /// </summary>
        private bool _listIsEmpty;

        public CWMessageUpdateDialog()
        {
            InitializeComponent();
            // #717: the list names the way out. Arrow keys never leave a
            // ListBox, so with messages present the buttons felt absent; Tab
            // reaches them and Escape closes. Initial focus stays on the list,
            // which is the right landing place.
            AutomationProperties.SetHelpText(KeysList, Radios.Lexicon.Get("settings.cw_message.list_help"));
            Loaded += OnLoaded;
        }

        /// <summary>
        /// The rows the list shows: the messages, or, when there are none, the
        /// one row that says so (#717). An empty ListBox reports nothing, so an
        /// empty library was indistinguishable from a dialog that failed to
        /// load. The message rows are passed through untouched: label, key and
        /// payload kind, deliberately (Sprint 48 Track A).
        /// </summary>
        internal static string[] RowsFor(string[]? labels, string emptyRow) =>
            labels == null || labels.Length == 0 ? new[] { emptyRow } : labels;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshList();
            KeysList.Focus();
        }

        private void RefreshList()
        {
            int oldIndex = KeysList.SelectedIndex;
            string[]? labels = GetMessageLabels?.Invoke();
            _listIsEmpty = labels == null || labels.Length == 0;
            KeysList.ItemsSource = null;
            KeysList.ItemsSource = RowsFor(labels, Radios.Lexicon.Get("settings.cw_message.list_empty"));
            // Nothing to update or delete: out of the tab order, as every
            // unavailable control is, and back the moment a message exists.
            UpdateButton.IsEnabled = !_listIsEmpty;
            DeleteButton.IsEnabled = !_listIsEmpty;
            KeysList.SelectedIndex = _listIsEmpty ? 0 : Math.Max(0, Math.Min(oldIndex, labels!.Length - 1));
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            AddMessage?.Invoke();
            RefreshList();
            KeysList.Focus();
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_listIsEmpty || KeysList.SelectedIndex < 0) return;
            UpdateMessage?.Invoke(KeysList.SelectedIndex);
            RefreshList();
            KeysList.Focus();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_listIsEmpty || KeysList.SelectedIndex < 0) return;
            int idx = KeysList.SelectedIndex;
            if (idx > 0) KeysList.SelectedIndex = idx - 1;
            DeleteMessage?.Invoke(idx);
            RefreshList();
            // #717: deleting the last message used to close the dialog with its
            // result set to false, so the one path that changed something
            // reported a cancellation. It now stays, on the list, which says
            // it is empty and how to add one; Close or Escape leaves.
            KeysList.Focus();
        }

        private void KeysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool onAMessage = !_listIsEmpty && KeysList.SelectedIndex >= 0;
            UpdateButton.IsDefault = onAMessage;
            AddButton.IsDefault = !onAMessage;
        }
    }
}
