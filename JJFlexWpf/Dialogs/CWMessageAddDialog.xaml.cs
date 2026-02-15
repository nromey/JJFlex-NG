using System.Windows;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Data class for CW message add/update results.
    /// </summary>
    public class CWMessageData
    {
        public string KeyDisplay { get; set; } = "";
        public string Label { get; set; } = "";
        public string Message { get; set; } = "";
        /// <summary>
        /// Opaque key value — caller stores whatever key representation it needs.
        /// (e.g., System.Windows.Forms.Keys value for compatibility with KeyCommands)
        /// </summary>
        public object? KeyValue { get; set; }
        public bool KeySpecified { get; set; }
    }

    public partial class CWMessageAddDialog : JJFlexDialog
    {
        /// <summary>
        /// Set to an existing item for update mode. Null for add mode.
        /// </summary>
        public CWMessageData? ExistingItem { get; set; }

        /// <summary>
        /// Delegate to check if a key is already bound.
        /// Receives WPF Key + ModifierKeys, returns true if duplicate.
        /// </summary>
        public Func<Key, ModifierKeys, bool>? IsKeyDuplicate { get; set; }

        /// <summary>
        /// Delegate to format a key combo for display.
        /// Receives WPF Key + ModifierKeys, returns display string like "C-Control".
        /// </summary>
        public Func<Key, ModifierKeys, string>? FormatKey { get; set; }

        /// <summary>
        /// Delegate to convert WPF key to the opaque key value used by the app.
        /// </summary>
        public Func<Key, ModifierKeys, object?>? ConvertKey { get; set; }

        /// <summary>
        /// The result data. Set after OK is clicked.
        /// </summary>
        public CWMessageData? ResultItem { get; private set; }

        private bool _keySpecified;
        private Key _capturedKey;
        private ModifierKeys _capturedModifiers;

        public CWMessageAddDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ExistingItem != null)
            {
                Title = "Update this CW Message";
                KeyTextBox.Text = ExistingItem.KeyDisplay;
                LabelTextBox.Text = ExistingItem.Label;
                MessageTextBox.Text = ExistingItem.Message;
                _keySpecified = ExistingItem.KeySpecified;
            }
            LabelTextBox.Focus();
        }

        private void KeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Skip modifier-only presses and Tab
            if (e.Key == Key.Tab || e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.System)
            {
                return;
            }

            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;

            // Delete key clears the binding
            if (key == Key.Delete && modifiers == ModifierKeys.None)
            {
                KeyTextBox.Text = "";
                _keySpecified = false;
                _capturedKey = Key.None;
                _capturedModifiers = ModifierKeys.None;
                return;
            }

            // Check for duplicate
            if (IsKeyDuplicate != null && IsKeyDuplicate(key, modifiers))
            {
                MessageBox.Show("This key is already defined.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _capturedKey = key;
            _capturedModifiers = modifiers;
            _keySpecified = true;

            string display = FormatKey != null
                ? FormatKey(key, modifiers)
                : key.ToString();
            KeyTextBox.Text = display;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_keySpecified || string.IsNullOrWhiteSpace(MessageTextBox.Text) ||
                string.IsNullOrWhiteSpace(LabelTextBox.Text))
            {
                MessageBox.Show("You must specify a key, a label, and some text.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultItem = new CWMessageData
            {
                KeyDisplay = KeyTextBox.Text,
                Label = LabelTextBox.Text,
                Message = MessageTextBox.Text,
                KeySpecified = true,
                KeyValue = ConvertKey?.Invoke(_capturedKey, _capturedModifiers)
            };
            DialogResult = true;
            Close();
        }
    }
}
