using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Data class for message add/update results.
    /// </summary>
    public class CWMessageData
    {
        public string KeyDisplay { get; set; } = "";
        public string Label { get; set; } = "";
        public string Message { get; set; } = "";
        /// <summary>
        /// The recording sent in a voice mode: its name inside the recordings
        /// folder, without extension. Empty for a CW-only message (#151).
        /// </summary>
        public string Audio { get; set; } = "";
        /// <summary>
        /// Opaque key value — caller stores whatever key representation it needs.
        /// (e.g., System.Windows.Forms.Keys value for compatibility with KeyCommands)
        /// </summary>
        public object? KeyValue { get; set; }
        public bool KeySpecified { get; set; }
    }

    /// <summary>
    /// The editor for one message: its label, the key that sends it, and its
    /// two optional payloads — CW text for CW, a recording for voice modes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sprint 48 Track A (#151): the dialog grew the recording picker. Either
    /// payload may be empty, but not both; the key says plainly at send time
    /// when a slot has nothing for the radio's current mode.
    /// </para>
    /// <para>
    /// <b>The recordings are the operator's own, read straight from the
    /// folder.</b> No import step and no second store: a take recorded in the
    /// Audio Workshop and a WAV dropped into the folder by hand are both
    /// first-class here, which is <see cref="RecordingStore"/>'s stated
    /// contract. A recording the message names that is no longer in the
    /// folder is still shown, labelled as missing, so updating the label of
    /// a message cannot silently drop its recording.
    /// </para>
    /// <para>
    /// <b>An update keeps the key unless a new one is pressed.</b> Before
    /// this track the dialog converted whatever key it had CAPTURED, which in
    /// update mode was none, so changing a message's wording unbound it. The
    /// existing key now rides along as the opaque value and is handed back
    /// untouched when nothing new was pressed. In add mode the caller may
    /// suggest a key — the next free Ctrl+digit — so the common case needs no
    /// keypress at all.
    /// </para>
    /// </remarks>
    public partial class CWMessageAddDialog : JJFlexDialog
    {
        /// <summary>
        /// Set to an existing item for update mode. Null for add mode.
        /// </summary>
        public CWMessageData? ExistingItem { get; set; }

        /// <summary>
        /// Add mode only: a key to offer before the operator presses one, with
        /// <see cref="CWMessageData.KeyValue"/> and <see cref="CWMessageData.KeyDisplay"/>
        /// filled in. Null to offer nothing.
        /// </summary>
        public CWMessageData? SuggestedKey { get; set; }

        /// <summary>
        /// Delegate to check if a key is already bound.
        /// Receives WPF Key + ModifierKeys, returns true if duplicate.
        /// </summary>
        public Func<Key, ModifierKeys, bool>? IsKeyDuplicate { get; set; }

        /// <summary>
        /// Delegate to format a key combo for display.
        /// Receives WPF Key + ModifierKeys, returns display string like "Ctrl+1".
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
        /// <summary>True once the operator has pressed a key in this dialog, so
        /// the captured pair is the one to convert rather than the carried value.</summary>
        private bool _keyCaptured;
        /// <summary>The key carried in from the existing item or the suggestion.</summary>
        private object? _carriedKeyValue;

        /// <summary>Recording name per combo index; null for "No recording".</summary>
        private readonly List<string?> _recordingNames = new();
        /// <summary>Full path per combo index; null when there is no file to play.</summary>
        private readonly List<string?> _recordingPaths = new();

        public CWMessageAddDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ExistingItem != null)
            {
                Title = Radios.Lexicon.Get("settings.cw_message.update_title");
                KeyTextBox.Text = ExistingItem.KeyDisplay;
                LabelTextBox.Text = ExistingItem.Label;
                MessageTextBox.Text = ExistingItem.Message;
                _keySpecified = ExistingItem.KeySpecified;
                _carriedKeyValue = ExistingItem.KeyValue;
            }
            else if (SuggestedKey != null && SuggestedKey.KeyValue != null)
            {
                KeyTextBox.Text = SuggestedKey.KeyDisplay;
                _keySpecified = true;
                _carriedKeyValue = SuggestedKey.KeyValue;
            }

            FillRecordings(ExistingItem?.Audio ?? "");
            RecordingPlayer.StateChanged += OnPlayerStateChanged;
            UpdateListenButton();
            LabelTextBox.Focus();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            RecordingPlayer.StateChanged -= OnPlayerStateChanged;
            // Never leave a recording playing behind a closed editor.
            string? ours = SelectedPath();
            if (ours != null && RecordingPlayer.IsPlaying
                && string.Equals(RecordingPlayer.CurrentPath, ours, StringComparison.OrdinalIgnoreCase))
            {
                RecordingPlayer.Stop();
            }
        }

        /// <summary>
        /// List the recordings folder, newest first, after "No recording".
        /// A recording the item names that is not in the folder is appended,
        /// labelled as missing, and selected.
        /// </summary>
        private void FillRecordings(string selectedName)
        {
            _recordingNames.Clear();
            _recordingPaths.Clear();
            RecordingCombo.Items.Clear();

            _recordingNames.Add(null);
            _recordingPaths.Add(null);
            RecordingCombo.Items.Add(Radios.Lexicon.Get("settings.cw_message.no_recording"));

            int select = 0;
            foreach (var rec in RecordingStore.Enumerate())
            {
                _recordingNames.Add(rec.Name);
                _recordingPaths.Add(rec.Path);
                RecordingCombo.Items.Add(rec.Describe());
                if (!string.IsNullOrEmpty(selectedName)
                    && string.Equals(rec.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                {
                    select = RecordingCombo.Items.Count - 1;
                }
            }

            if (!string.IsNullOrEmpty(selectedName) && select == 0)
            {
                _recordingNames.Add(selectedName);
                _recordingPaths.Add(null);
                RecordingCombo.Items.Add(Radios.Lexicon.Get(
                    "settings.cw_message.recording_not_in_folder", ("name", selectedName)));
                select = RecordingCombo.Items.Count - 1;
            }

            RecordingCombo.SelectedIndex = select;
        }

        private string? SelectedName()
        {
            int i = RecordingCombo.SelectedIndex;
            return i >= 0 && i < _recordingNames.Count ? _recordingNames[i] : null;
        }

        private string? SelectedPath()
        {
            int i = RecordingCombo.SelectedIndex;
            return i >= 0 && i < _recordingPaths.Count ? _recordingPaths[i] : null;
        }

        private void RecordingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateListenButton();
        }

        private void OnPlayerStateChanged()
        {
            Dispatcher.BeginInvoke(new Action(UpdateListenButton));
        }

        /// <summary>
        /// The button says what pressing it will do, and is disabled when
        /// there is nothing on disk to play.
        /// </summary>
        private void UpdateListenButton()
        {
            if (ListenButton == null) return;
            string? path = SelectedPath();
            bool playingOurs = path != null && RecordingPlayer.IsPlaying
                && string.Equals(RecordingPlayer.CurrentPath, path, StringComparison.OrdinalIgnoreCase);
            string label = playingOurs ? "Stop listening" : "Listen";
            ListenButton.Content = "_" + label;
            AutomationProperties.SetName(ListenButton, label);
            ListenButton.IsEnabled = path != null;
        }

        private void ListenButton_Click(object sender, RoutedEventArgs e)
        {
            string? path = SelectedPath();
            if (path == null) return;
            RecordingPlayer.Toggle(path);
            UpdateListenButton();
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
                _keyCaptured = false;
                _carriedKeyValue = null;
                _capturedKey = Key.None;
                _capturedModifiers = ModifierKeys.None;
                return;
            }

            // Check for duplicate
            if (IsKeyDuplicate != null && IsKeyDuplicate(key, modifiers))
            {
                MessageBox.Show(Radios.Lexicon.Get("settings.cw_message.key_already_defined"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _capturedKey = key;
            _capturedModifiers = modifiers;
            _keySpecified = true;
            _keyCaptured = true;

            string display = FormatKey != null
                ? FormatKey(key, modifiers)
                : key.ToString();
            KeyTextBox.Text = display;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            string? audio = SelectedName();
            bool hasText = !string.IsNullOrWhiteSpace(MessageTextBox.Text);
            bool hasAudio = !string.IsNullOrEmpty(audio);
            if (!_keySpecified || string.IsNullOrWhiteSpace(LabelTextBox.Text) || !(hasText || hasAudio))
            {
                MessageBox.Show(Radios.Lexicon.Get("settings.cw_message.needs_key_label_and_payload"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            object? keyValue = _keyCaptured
                ? ConvertKey?.Invoke(_capturedKey, _capturedModifiers)
                : _carriedKeyValue;

            ResultItem = new CWMessageData
            {
                KeyDisplay = KeyTextBox.Text,
                Label = LabelTextBox.Text,
                Message = MessageTextBox.Text,
                Audio = audio ?? "",
                KeySpecified = true,
                KeyValue = keyValue,
            };
            DialogResult = true;
            Close();
        }
    }
}
