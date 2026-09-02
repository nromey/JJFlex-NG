using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// A navigable list of keys and what they do: one row per key, the list
    /// announces its count and position, and a letter jumps to the next row
    /// that starts with it. Serves the per-field Home help
    /// (<c>MainWindow.DisplayHelp</c>) and, since Sprint 44 Track K, the JJ
    /// key layer lists (<see cref="KeyLayerHelp"/>).
    /// </summary>
    public partial class ShowHelpDialog : JJFlexDialog
    {
        /// <summary>
        /// Legacy: pre-formatted help text. If set, each line becomes a ListBox item.
        /// </summary>
        public string HelpText { get; set; } = "";

        /// <summary>
        /// Structured help items: list of (key, description) pairs.
        /// If set, these are used instead of HelpText.
        /// </summary>
        public List<(string key, string description)>? HelpItems { get; set; }

        /// <summary>
        /// Title line shown as the first (non-selectable) item.
        /// </summary>
        public string HelpTitle { get; set; } = "";

        /// <summary>
        /// An optional second button beside Close — the door from a narrow
        /// surface to a wider one ("Explore the JJ key" from a layer's list).
        /// Underscore marks the access key. No label, no button.
        /// </summary>
        public string? SecondaryActionLabel { get; set; }

        /// <summary>
        /// What the second button does. Runs AFTER this dialog has closed and
        /// the queue has drained, the Command Finder's own deferral, so the
        /// next surface opens on a settled focus rather than mid-teardown.
        /// </summary>
        public Action? SecondaryAction { get; set; }

        public ShowHelpDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            KeyHelpSurfaces.Attach(this);
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (HelpListBox.Items.Count == 0)
            {
                if (HelpItems != null && HelpItems.Count > 0)
                {
                    // Structured mode: title + key-description pairs
                    if (!string.IsNullOrEmpty(HelpTitle))
                        HelpListBox.Items.Add(HelpTitle);

                    foreach (var (key, desc) in HelpItems)
                    {
                        string item = $"{key,-16} {desc}";
                        HelpListBox.Items.Add(item);
                    }
                }
                else if (!string.IsNullOrEmpty(HelpText))
                {
                    // Legacy mode: split pre-formatted text into lines
                    var lines = HelpText.Split('\n');
                    foreach (var line in lines)
                    {
                        string trimmed = line.TrimEnd('\r');
                        if (!string.IsNullOrEmpty(trimmed))
                            HelpListBox.Items.Add(trimmed);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(SecondaryActionLabel))
            {
                SecondaryButton.Content = SecondaryActionLabel;
                string plain = SecondaryActionLabel.Replace("_", "");
                AutomationProperties.SetName(SecondaryButton, plain);
                int idx = SecondaryActionLabel.IndexOf('_');
                if (idx >= 0 && idx < SecondaryActionLabel.Length - 1)
                {
                    string combo = "Alt+" + char.ToUpperInvariant(SecondaryActionLabel[idx + 1]);
                    AutomationProperties.SetAccessKey(SecondaryButton, combo);
                    AutomationProperties.SetAcceleratorKey(SecondaryButton, combo);
                }
                SecondaryButton.Visibility = Visibility.Visible;
            }

            // Focus and select first actionable item
            if (HelpListBox.Items.Count > 0)
            {
                int startIndex = string.IsNullOrEmpty(HelpTitle) ? 0 : (HelpListBox.Items.Count > 1 ? 1 : 0);
                HelpListBox.SelectedIndex = startIndex;
                HelpListBox.Focus();
            }

            // Set accessible name to include item count
            AutomationProperties.SetName(HelpListBox,
                $"Key commands, {HelpListBox.Items.Count} items");
        }

        /// <summary>
        /// A plain letter or digit jumps to the next row that starts with it,
        /// wrapping — the way a Windows list answers a keypress, and every row
        /// here starts with its key. Consumed whether or not it matched, so a
        /// persistent layer live underneath never sees the letter as one of
        /// its own (see <see cref="KeyHelpSurfaces"/>).
        /// </summary>
        private void HelpListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return;
            var raw = e.Key == Key.System ? e.SystemKey : e.Key;
            char letter;
            if (raw >= Key.A && raw <= Key.Z) letter = (char)('A' + (raw - Key.A));
            else if (raw >= Key.D0 && raw <= Key.D9) letter = (char)('0' + (raw - Key.D0));
            else return;

            int count = HelpListBox.Items.Count;
            if (count == 0) { e.Handled = true; return; }
            int start = HelpListBox.SelectedIndex;
            for (int step = 1; step <= count; step++)
            {
                int i = ((start < 0 ? -1 : start) + step) % count;
                string text = HelpListBox.Items[i]?.ToString() ?? "";
                if (text.Length > 0 && char.ToUpperInvariant(text[0]) == letter)
                {
                    HelpListBox.SelectedIndex = i;
                    HelpListBox.ScrollIntoView(HelpListBox.Items[i]);
                    if (HelpListBox.ItemContainerGenerator.ContainerFromIndex(i) is UIElement row)
                        row.Focus();
                    break;
                }
            }
            e.Handled = true;
        }

        private void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            var action = SecondaryAction;
            Close();
            if (action != null)
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, action);
        }
    }
}
