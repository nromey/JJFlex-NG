using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// QB Track H (2026-08-07) — the ONE Keys surface, backed by the
    /// KeyCommands v5 registry. Replaces the orphaned ShowKeysDialog /
    /// SetupKeysDialog pair (Jim's pre-v5 key-action system, whose Update
    /// button saved into a never-wired callback).
    ///
    /// Two doors, one room: Tools → Hotkey Editor opens it editable;
    /// Help → Key Assignments opens it viewing. Views: by scope,
    /// alphabetical, by function group, and the read-only built-in key
    /// inventory (home fields, universals, filter chords, leader commands).
    ///
    /// Editing is live: a rebind takes effect immediately (the registry's
    /// dispatch dictionary is the single source of truth) and persists via
    /// the registry's own KeyDefs.xml writer.
    /// </summary>
    public partial class KeysDialog : JJFlexDialog
    {
        private readonly KeyCommands _commands;
        private readonly bool _editable;

        private List<KeyManifest.Row> _allRows = new();
        private bool _capturing;
        private KeyManifest.Row? _captureRow;
        private bool _loading = true;

        private const string ViewByScope = "By scope";
        private const string ViewAlphabetical = "Alphabetical";
        private const string ViewByGroup = "By function group";
        private const string ViewBuiltIn = "Built-in keys";

        // Scope buckets for the "By scope" view. Radio covers the Classic and
        // Modern sub-scopes — they're never active apart from Radio.
        private const string CatGlobal = "Global";
        private const string CatRadio = "Radio";
        private const string CatLogging = "Logging";

        public KeysDialog(KeyCommands commands, bool editable)
        {
            _commands = commands;
            _editable = editable;
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Title = editable ? "Hotkey Editor" : "Key Assignments";

            if (!editable)
            {
                EditPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                ViewHint.Visibility = Visibility.Collapsed;
            }

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RebuildRows();

            ViewCombo.ItemsSource = new[] { ViewByScope, ViewAlphabetical, ViewByGroup, ViewBuiltIn };
            ViewCombo.SelectedIndex = 0;
            _loading = false;
            RefreshCategories();
            RefreshList();
            KeysList.Focus();
            if (KeysList.Items.Count > 0)
                KeysList.SelectedIndex = 0;
        }

        protected override void FocusFirstControl()
        {
            // Land on the list — browsing assignments is the primary task.
            KeysList.Focus();
        }

        private void RebuildRows()
        {
            _allRows = KeyManifest.Build(_commands);
        }

        // ── View / category plumbing ──

        private string CurrentView => ViewCombo.SelectedItem as string ?? ViewByScope;

        private void ViewCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            RefreshCategories();
            RefreshList();
        }

        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            RefreshList();
        }

        private static string ScopeBucket(string scope) => scope switch
        {
            "Global" => CatGlobal,
            "Logging" => CatLogging,
            _ => CatRadio, // Radio, Classic, Modern
        };

        private IEnumerable<KeyManifest.Row> CommandRows() =>
            _allRows.Where(r => r.CommandId != null);

        private IEnumerable<KeyManifest.Row> BuiltInRows() =>
            _allRows.Where(r => r.CommandId == null);

        private void RefreshCategories()
        {
            switch (CurrentView)
            {
                case ViewByScope:
                    CategoryRow.Visibility = Visibility.Visible;
                    CategoryCombo.ItemsSource = new[] { CatGlobal, CatRadio, CatLogging };
                    CategoryCombo.SelectedIndex = 0;
                    break;
                case ViewByGroup:
                    CategoryRow.Visibility = Visibility.Visible;
                    CategoryCombo.ItemsSource = CommandRows()
                        .Select(r => r.Group)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    CategoryCombo.SelectedIndex = 0;
                    break;
                case ViewBuiltIn:
                    CategoryRow.Visibility = Visibility.Visible;
                    var contexts = new List<string> { "All" };
                    contexts.AddRange(BuiltInRows().Select(r => r.Source).Distinct());
                    CategoryCombo.ItemsSource = contexts;
                    CategoryCombo.SelectedIndex = 0;
                    break;
                default: // Alphabetical
                    CategoryRow.Visibility = Visibility.Collapsed;
                    CategoryCombo.ItemsSource = null;
                    break;
            }
        }

        private void RefreshList(CommandValues? reselect = null)
        {
            var category = CategoryCombo.SelectedItem as string;
            List<KeyManifest.Row> rows;
            switch (CurrentView)
            {
                case ViewByScope:
                    rows = CommandRows()
                        .Where(r => ScopeBucket(r.Scope) == (category ?? CatGlobal))
                        .OrderBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    break;
                case ViewByGroup:
                    rows = CommandRows()
                        .Where(r => string.Equals(r.Group, category, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    break;
                case ViewBuiltIn:
                    rows = BuiltInRows()
                        .Where(r => category == "All" || r.Source == category)
                        .ToList(); // inventory order is presentation order
                    break;
                default: // Alphabetical
                    rows = CommandRows()
                        .OrderBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    break;
            }

            KeysList.ItemsSource = rows;
            if (reselect != null)
            {
                var again = rows.FirstOrDefault(r => r.CommandId == reselect);
                if (again != null)
                {
                    KeysList.SelectedItem = again;
                    KeysList.ScrollIntoView(again);
                }
            }
            UpdateButtonStates();
        }

        private KeyManifest.Row? SelectedRow => KeysList.SelectedItem as KeyManifest.Row;

        private void KeysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            bool canEdit = _editable && SelectedRow is { Rebindable: true, CommandId: not null };
            ChangeKeyButton.IsEnabled = canEdit;
            UnbindButton.IsEnabled = canEdit && SelectedRow?.KeyDisplay != "not bound";
            ResetButton.IsEnabled = canEdit;
        }

        // ── Announcements ──

        private void Announce(string message)
        {
            StatusText.Text = message;

            // TEMPORARY EXPERIMENT (2026-08-18) - is a UIA live region actually
            // usable as an announcement channel in this application?
            //
            // StatusText carries AutomationProperties.LiveSetting="Polite" and
            // always has, but nothing in this codebase has ever raised
            // LiveRegionChanged, so the region has never announced anything.
            // Every word the operator has heard from this dialog came from the
            // Speak call below.
            //
            // With JJFLEX_UIA_LIVE_TEST=1 we swap the channels rather than
            // running both, so the result is unambiguous: hearing the status
            // proves the live region works; silence proves it does not.
            if (UiaLive.TestModeEnabled)
            {
                UiaLive.Announce(StatusText);
                return;
            }

            Radios.ScreenReaderOutput.Speak(message, Radios.VerbosityLevel.Terse, true);
        }

        // ── Key capture ──

        private void ChangeKeyButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row?.CommandId == null) return;
            if (!row.Rebindable)
            {
                Announce("That key is built in and cannot be changed.");
                return;
            }
            _captureRow = row;
            _capturing = true;
            Announce($"Press the new key for {row.Description}. Escape cancels.");
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_capturing)
            {
                // Consume everything while capturing — nothing may leak to the
                // list, the buttons, or the base dialog's Escape-close.
                e.Handled = true;
                var raw = e.Key == Key.System ? e.SystemKey : e.Key;
                if (raw is Key.LeftShift or Key.RightShift
                    or Key.LeftCtrl or Key.RightCtrl
                    or Key.LeftAlt or Key.RightAlt
                    or Key.LWin or Key.RWin or Key.Apps)
                {
                    return; // modifier down — keep waiting for the real key
                }

                if (raw == Key.Escape)
                {
                    EndCapture();
                    Announce("Cancelled. No change.");
                    return;
                }

                var wf = WpfKeyConverter.ToWinFormsKeys(e);
                if ((wf & WinFormsKeys.KeyCode) == WinFormsKeys.None)
                {
                    Announce("That key can't be used. Try another, or press Escape to cancel.");
                    return;
                }

                if (KeyInventory.IsReservedForCapture(wf, out var reason))
                {
                    Announce($"{reason}. Try another key, or press Escape to cancel.");
                    return;
                }

                AttemptBind(wf);
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private void EndCapture()
        {
            _capturing = false;
            _captureRow = null;
        }

        private void AttemptBind(WinFormsKeys key)
        {
            var row = _captureRow;
            EndCapture();
            if (row?.CommandId == null) return;
            var id = row.CommandId.Value;
            string keyName = KeyManifest.FormatKey(key);

            var conflicts = _commands.FindBindingConflicts(key, id);
            if (conflicts.Count == 0)
            {
                if (_commands.ApplyBinding(id, key, stealConflicts: false))
                {
                    RebuildRows();
                    RefreshList(id);
                    Announce($"{row.Description} is now {keyName}.");
                }
                else
                {
                    Announce("The key could not be changed.");
                }
                return;
            }

            var unstealable = conflicts.FirstOrDefault(c => !c.CanSteal);
            if (unstealable != null)
            {
                Announce($"{keyName} belongs to {unstealable.Description}, which is managed under CW Messages. No change.");
                return;
            }

            // Name the collision and offer steal / cancel.
            string conflictNames = string.Join(" and ",
                conflicts.Select(c => $"{c.Description} in {c.Scope} scope"));
            var result = MessageBox.Show(this,
                $"{keyName} is already assigned to {conflictNames}. " +
                $"Take the key away and give it to {row.Description}? " +
                $"The other command will be left without a key.",
                "Key conflict", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                if (_commands.ApplyBinding(id, key, stealConflicts: true))
                {
                    RebuildRows();
                    RefreshList(id);
                    Announce($"{row.Description} is now {keyName}. " +
                        string.Join(" ", conflicts.Select(c => $"{c.Description} is now unbound.")));
                }
                else
                {
                    Announce("The key could not be changed.");
                }
            }
            else
            {
                Announce("Cancelled. No change.");
            }
        }

        // ── Unbind / reset ──

        private void UnbindButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row?.CommandId == null || !row.Rebindable) return;
            var id = row.CommandId.Value;
            if (_commands.ApplyBinding(id, WinFormsKeys.None, stealConflicts: false))
            {
                RebuildRows();
                RefreshList(id);
                Announce($"{row.Description} is now unbound. It stays available in the Command Finder.");
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row?.CommandId == null || !row.Rebindable) return;
            var id = row.CommandId.Value;
            var defKey = _commands.GetDefaultKey(id)?.Key ?? WinFormsKeys.None;
            string keyName = KeyManifest.FormatKey(defKey);

            var conflicts = _commands.FindBindingConflicts(defKey, id);
            bool steal = false;
            if (conflicts.Count > 0)
            {
                var unstealable = conflicts.FirstOrDefault(c => !c.CanSteal);
                if (unstealable != null)
                {
                    Announce($"The default key {keyName} belongs to {unstealable.Description}, which is managed under CW Messages. No change.");
                    return;
                }
                string conflictNames = string.Join(" and ",
                    conflicts.Select(c => $"{c.Description} in {c.Scope} scope"));
                var result = MessageBox.Show(this,
                    $"The default key {keyName} is currently assigned to {conflictNames}. " +
                    $"Take it back for {row.Description}?",
                    "Key conflict", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    Announce("Cancelled. No change.");
                    return;
                }
                steal = true;
            }

            if (_commands.ApplyBinding(id, defKey, steal))
            {
                RebuildRows();
                RefreshList(id);
                Announce(defKey == WinFormsKeys.None
                    ? $"{row.Description} reset: no default key, now unbound."
                    : $"{row.Description} reset to {keyName}.");
            }
        }

        private void ResetAllButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                "Reset every key to its default? All of your custom key assignments will be lost.",
                "Reset all keys", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                Announce("Cancelled. No change.");
                return;
            }
            _commands.ResetAllBindingsToDefault();
            RebuildRows();
            RefreshList();
            Announce("All keys reset to defaults.");
        }

        // ── Export ──

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = KeyManifest.WriteToFile(_commands);
                Announce($"Key list saved to {path}.");
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Announce($"The key list could not be saved: {ex.Message}");
            }
        }

        // ── List conveniences ──

        private void KeysList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _editable && ChangeKeyButton.IsEnabled)
            {
                ChangeKeyButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
