using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Represents a command entry for the command finder.
    /// </summary>
    public class CommandFinderItem
    {
        public string Description { get; set; } = "";
        public string KeyDisplay { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Group { get; set; } = "";
        public string? MenuText { get; set; }
        public string[]? Keywords { get; set; }
        /// <summary>Opaque reference back to the original command for execution.</summary>
        public object? Tag { get; set; }
    }

    public partial class CommandFinderDialog : JJFlexDialog
    {
        /// <summary>
        /// Returns all available commands. Set before showing.
        /// </summary>
        public Func<List<CommandFinderItem>>? GetCommands { get; set; }

        /// <summary>
        /// Called to execute a command. Receives the Tag from CommandFinderItem.
        /// </summary>
        public Action<object>? ExecuteCommand { get; set; }

        /// <summary>
        /// Optional delegate to speak text for screen readers.
        /// </summary>
        public Action<string>? SpeakText { get; set; }

        private List<CommandFinderItem> _allCommands = new();

        public CommandFinderDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _allCommands = GetCommands?.Invoke() ?? new();
            FilterResults("");
            SearchBox.Focus();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterResults(SearchBox.Text.Trim());
        }

        private void FilterResults(string query)
        {
            var filtered = string.IsNullOrEmpty(query)
                ? _allCommands
                : _allCommands.Where(c => MatchesQuery(c, query)).ToList();

            ResultsListView.ItemsSource = filtered;
            ResultCountLabel.Text = $"{filtered.Count} commands";
            SpeakText?.Invoke($"{filtered.Count} results");
        }

        private static bool MatchesQuery(CommandFinderItem item, string query)
        {
            var q = query.ToLowerInvariant();
            if (item.Description.ToLowerInvariant().Contains(q)) return true;
            if (item.Group.ToLowerInvariant().Contains(q)) return true;
            if (item.MenuText?.ToLowerInvariant().Contains(q) == true) return true;
            if (item.Keywords != null)
            {
                foreach (var kw in item.Keywords)
                {
                    if (kw.ToLowerInvariant().Contains(q)) return true;
                }
            }
            return false;
        }

        private void ResultsListView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteSelectedCommand();
                e.Handled = true;
            }
        }

        private void ResultsListView_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            ExecuteSelectedCommand();
        }

        private void ExecuteSelectedCommand()
        {
            if (ResultsListView.SelectedItem is not CommandFinderItem item) return;
            if (item.Tag == null) return;
            Close();
            ExecuteCommand?.Invoke(item.Tag);
        }
    }
}
