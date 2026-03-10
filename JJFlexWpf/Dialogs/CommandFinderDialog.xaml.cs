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

        /// <summary>Screen reader reads this — must be meaningful, not the class name.</summary>
        public override string ToString() =>
            string.IsNullOrEmpty(KeyDisplay) ? Description : $"{Description}, {KeyDisplay}";
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

        /// <summary>
        /// Current UI mode name ("Classic", "Modern", or "Logging").
        /// Used for scope filtering — only shows commands relevant to the current mode.
        /// </summary>
        public string CurrentMode { get; set; } = "Modern";

        private List<CommandFinderItem> _allCommands = new();

        public CommandFinderDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private const string AllCategories = "All";

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _allCommands = GetCommands?.Invoke() ?? new();

            // Populate category combo from distinct Group values
            var groups = _allCommands
                .Select(c => c.Group)
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
            groups.Insert(0, AllCategories);
            CategoryCombo.ItemsSource = groups;
            CategoryCombo.SelectedIndex = 0;

            FilterResults("");
            SearchBox.Focus();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterResults(SearchBox.Text.Trim());
        }

        private void FilterResults(string query)
        {
            bool showAll = ShowAllScopesCheckBox.IsChecked == true;
            var selectedCategory = CategoryCombo.SelectedItem as string ?? AllCategories;
            var filtered = _allCommands
                .Where(c => (showAll || ScopeVisible(c.Scope))
                         && (selectedCategory == AllCategories ||
                             string.Equals(c.Group, selectedCategory, StringComparison.OrdinalIgnoreCase))
                         && (string.IsNullOrEmpty(query) || MatchesQuery(c, query)))
                .ToList();

            ResultsListView.ItemsSource = filtered;
            ResultCountLabel.Text = $"{filtered.Count} commands";
            SpeakText?.Invoke($"{filtered.Count} results");
        }

        /// <summary>
        /// Only filter when dropdown closes (selection committed) — not while arrowing
        /// through the combo. Prevents jabbering during keyboard navigation.
        /// </summary>
        private void CategoryCombo_SelectionCommitted(object? sender, EventArgs e)
        {
            if (_allCommands.Count > 0)
                FilterResults(SearchBox.Text.Trim());
        }

        private bool ScopeVisible(string scope)
        {
            return CurrentMode switch
            {
                "Classic" => scope is "Global" or "Radio" or "Classic",
                "Modern" => scope is "Global" or "Radio" or "Modern",
                "Logging" => scope is "Global" or "Logging",
                _ => true
            };
        }

        private void OnScopeFilterChanged(object sender, RoutedEventArgs e)
        {
            // Only filter if already loaded (checkbox fires during init too)
            if (_allCommands.Count > 0)
                FilterResults(SearchBox.Text.Trim());
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
