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
            Title = Radios.Lexicon.Get(editable ? "settings.keys.editor.title_editable" : "settings.keys.editor.title_readonly");

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
                Announce(Radios.Lexicon.Get("settings.keys.editor.built_in_cannot_change"));
                return;
            }
            _captureRow = row;
            _capturing = true;
            Announce(Radios.Lexicon.Get("settings.keys.editor.press_new_key", ("command", row.Description)));
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
                    Announce(Radios.Lexicon.Get("settings.keys.editor.cancelled"));
                    return;
                }

                var wf = WpfKeyConverter.ToWinFormsKeys(e);
                if ((wf & WinFormsKeys.KeyCode) == WinFormsKeys.None)
                {
                    Announce(Radios.Lexicon.Get("settings.keys.editor.key_unusable"));
                    return;
                }

                if (KeyInventory.IsReservedForCapture(wf, out var reason))
                {
                    Announce(Radios.Lexicon.Get("settings.keys.editor.key_reserved", ("reason", reason)));
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
                    Announce(Radios.Lexicon.Get("settings.keys.editor.bound",
                        ("command", row.Description), ("key", keyName)));
                }
                else
                {
                    Announce(Radios.Lexicon.Get("settings.keys.editor.bind_failed"));
                }
                return;
            }

            var unstealable = conflicts.FirstOrDefault(c => !c.CanSteal);
            if (unstealable != null)
            {
                Announce(Radios.Lexicon.Get("settings.keys.editor.owned_by_cw_messages",
                    ("key", keyName), ("command", unstealable.Description)));
                return;
            }

            // Name the collision and offer steal / cancel.
            string conflictNames = string.Join(" and ", conflicts.Select(
                c => Radios.Lexicon.Get("settings.keys.editor.conflict_scope_item",
                    ("command", c.Description), ("scope", c.Scope))));
            var result = MessageBox.Show(this,
                Radios.Lexicon.Get("settings.keys.editor.steal_confirm",
                    ("key", keyName), ("conflicts", conflictNames),
                    ("command", row.Description)),
                Radios.Lexicon.Get("settings.keys.editor.conflict_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                if (_commands.ApplyBinding(id, key, stealConflicts: true))
                {
                    RebuildRows();
                    RefreshList(id);
                    Announce(Radios.Lexicon.Get("settings.keys.editor.bound",
                            ("command", row.Description), ("key", keyName)) + " " +
                        string.Join(" ", conflicts.Select(
                            c => Radios.Lexicon.Get("settings.keys.editor.now_unbound_other",
                                ("command", c.Description)))));
                }
                else
                {
                    Announce(Radios.Lexicon.Get("settings.keys.editor.bind_failed"));
                }
            }
            else
            {
                Announce(Radios.Lexicon.Get("settings.keys.editor.cancelled"));
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
                Announce(Radios.Lexicon.Get("settings.keys.editor.unbound", ("command", row.Description)));
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
                    Announce(Radios.Lexicon.Get("settings.keys.editor.default_owned_by_cw_messages",
                        ("key", keyName), ("command", unstealable.Description)));
                    return;
                }
                string conflictNames = string.Join(" and ", conflicts.Select(
                    c => Radios.Lexicon.Get("settings.keys.editor.conflict_scope_item",
                        ("command", c.Description), ("scope", c.Scope))));
                var result = MessageBox.Show(this,
                    Radios.Lexicon.Get("settings.keys.editor.reclaim_default_confirm",
                        ("key", keyName), ("conflicts", conflictNames),
                        ("command", row.Description)),
                    Radios.Lexicon.Get("settings.keys.editor.conflict_title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    Announce(Radios.Lexicon.Get("settings.keys.editor.cancelled"));
                    return;
                }
                steal = true;
            }

            if (_commands.ApplyBinding(id, defKey, steal))
            {
                RebuildRows();
                RefreshList(id);
                Announce(defKey == WinFormsKeys.None
                    ? Radios.Lexicon.Get("settings.keys.editor.reset_unbound", ("command", row.Description))
                    : Radios.Lexicon.Get("settings.keys.editor.reset_to_key",
                        ("command", row.Description), ("key", keyName)));
            }
        }

        private void ResetAllButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                Radios.Lexicon.Get("settings.keys.editor.reset_all_confirm"),
                Radios.Lexicon.Get("settings.keys.editor.reset_all_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                Announce(Radios.Lexicon.Get("settings.keys.editor.cancelled"));
                return;
            }
            _commands.ResetAllBindingsToDefault();
            RebuildRows();
            RefreshList();
            Announce(Radios.Lexicon.Get("settings.keys.editor.all_reset"));
        }

        // ── Export ──

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = KeyManifest.WriteToFile(_commands);
                Announce(Radios.Lexicon.Get("settings.keys.editor.list_saved", ("path", path)));
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Announce(Radios.Lexicon.Get("settings.keys.editor.list_save_failed", ("error", ex.Message)));
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
