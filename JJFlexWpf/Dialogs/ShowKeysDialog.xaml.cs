using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    public partial class ShowKeysDialog : JJFlexDialog
    {
        /// <summary>Current key-action mappings. Set before showing.</summary>
        public List<KeyActionItem> KeyActions { get; set; } = new();

        /// <summary>Available keys for the setup dialog. Set before showing.</summary>
        public List<KeyActionItem>? AvailableKeys { get; set; }

        /// <summary>Available actions for the setup dialog. Set before showing.</summary>
        public List<ActionItem>? AvailableActions { get; set; }

        public ShowKeysDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshList();
            DefinedKeysList.Focus();
        }

        private void RefreshList()
        {
            var configured = KeyActions.Where(k => !string.IsNullOrEmpty(k.ActionName)).ToList();
            DefinedKeysList.ItemsSource = null;
            DefinedKeysList.ItemsSource = configured;
            ActionBox.Text = "";
            ValueBox.Text = "";
        }

        private void DefinedKeysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DefinedKeysList.SelectedItem is KeyActionItem item)
            {
                ActionBox.Text = item.ActionDescription;
                ValueBox.Text = item.ActionName;
            }
            else
            {
                ActionBox.Text = "";
                ValueBox.Text = "";
            }
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            int selectedIndex = DefinedKeysList.SelectedIndex;
            var setupDialog = new SetupKeysDialog
            {
                AvailableKeys = AvailableKeys,
                AvailableActions = AvailableActions,
                KeyActions = new List<KeyActionItem>(KeyActions.Select(k => new KeyActionItem
                {
                    KeyName = k.KeyName,
                    KeyDescription = k.KeyDescription,
                    ActionName = k.ActionName,
                    ActionDescription = k.ActionDescription
                })),
                InitialKeyIndex = selectedIndex >= 0 ? selectedIndex : 0
            };
            if (setupDialog.ShowDialog() == true)
            {
                KeyActions = setupDialog.KeyActions;
                RefreshList();
            }
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
