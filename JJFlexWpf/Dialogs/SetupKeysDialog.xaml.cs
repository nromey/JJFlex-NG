using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Represents a key-to-action mapping.
    /// </summary>
    public class KeyActionItem
    {
        public string KeyName { get; set; } = "";
        public string KeyDescription { get; set; } = "";
        public string ActionName { get; set; } = "";
        public string ActionDescription { get; set; } = "";

        public override string ToString() => KeyDescription;
    }

    /// <summary>
    /// Represents an available action.
    /// </summary>
    public class ActionItem
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public override string ToString() => Description;
    }

    public partial class SetupKeysDialog : JJFlexDialog
    {
        /// <summary>Available keys to choose from. Set before showing.</summary>
        public List<KeyActionItem>? AvailableKeys { get; set; }

        /// <summary>Available actions. Set before showing.</summary>
        public List<ActionItem>? AvailableActions { get; set; }

        /// <summary>Current key-action mappings. Set before showing, read after OK.</summary>
        public List<KeyActionItem> KeyActions { get; set; } = new();

        /// <summary>Pre-select this key index on load.</summary>
        public int InitialKeyIndex { get; set; } = -1;

        public SetupKeysDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            KeysListBox.ItemsSource = AvailableKeys;
            ActionsBox.ItemsSource = AvailableActions;
            if (InitialKeyIndex >= 0 && AvailableKeys != null && InitialKeyIndex < AvailableKeys.Count)
                KeysListBox.SelectedIndex = InitialKeyIndex;
        }

        private void KeysListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KeysListBox.SelectedItem is KeyActionItem key)
            {
                // Find and select the current action for this key
                var existing = KeyActions.FirstOrDefault(k => k.KeyName == key.KeyName);
                if (existing != null && AvailableActions != null)
                {
                    var actionIdx = AvailableActions.FindIndex(a => a.Name == existing.ActionName);
                    ActionsBox.SelectedIndex = actionIdx;
                }
                else
                {
                    ActionsBox.SelectedIndex = -1;
                }
            }
        }

        private void ActionsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KeysListBox.SelectedItem is not KeyActionItem key) return;
            if (ActionsBox.SelectedItem is not ActionItem action) return;

            var existing = KeyActions.FirstOrDefault(k => k.KeyName == key.KeyName);
            if (existing != null)
            {
                existing.ActionName = action.Name;
                existing.ActionDescription = action.Description;
            }
            else
            {
                KeyActions.Add(new KeyActionItem
                {
                    KeyName = key.KeyName,
                    KeyDescription = key.KeyDescription,
                    ActionName = action.Name,
                    ActionDescription = action.Description
                });
            }
        }

        private void KeysListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Delete && KeysListBox.SelectedItem is KeyActionItem key)
            {
                var toRemove = KeyActions.FirstOrDefault(k => k.KeyName == key.KeyName);
                if (toRemove != null) KeyActions.Remove(toRemove);
                ActionsBox.SelectedIndex = -1;
                e.Handled = true;
            }
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
