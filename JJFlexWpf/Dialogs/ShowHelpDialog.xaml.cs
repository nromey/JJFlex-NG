using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
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

        public ShowHelpDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
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
    }
}
