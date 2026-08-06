using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Radio display data for the selector list.
    /// </summary>
    public class RadioListItem
    {
        public string Serial { get; set; } = "";
        public string Name { get; set; } = "";
        public string ModelName { get; set; } = "";
        public bool IsRemote { get; set; }
        public bool AutoConnect { get; set; }
        public bool LowBW { get; set; }
        public object RigData { get; set; } = null!;

        public string DisplayText
        {
            get
            {
                var autoConn = AutoConnect ? "[AutoConnect] " : "";
                var lbw = LowBW ? "[LowBW] " : "";
                var namePart = string.IsNullOrWhiteSpace(Name) ? "Unnamed" : Name;
                var modelPart = string.IsNullOrWhiteSpace(ModelName) ? "Unknown model" : ModelName;
                // Source, not serial. Two radios that differ only by where they
                // are were indistinguishable by ear — an unnamed local rig and a
                // remote one read as near-identical rows of digits. The serial is
                // rarely what the user needs and never what they navigate by;
                // making the row configurable (and adding user notes keyed on
                // serial) is queued as a proper feature.
                var wherePart = IsRemote ? "remote" : "local";
                return $"{autoConn}{lbw}{namePart} {modelPart} {wherePart}";
            }
        }

        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// Callbacks for the RigSelector dialog.
    /// </summary>
    public class RigSelectorCallbacks
    {
        /// <summary>Start local radio discovery.</summary>
        public required Action StartLocalDiscovery { get; init; }

        /// <summary>Start remote (SmartLink) radio discovery. Callback fires when complete (true=success).</summary>
        public required Action<Action<bool>> StartRemoteDiscovery { get; init; }

        /// <summary>Register for radio-found events. Action receives RadioListItem.</summary>
        public required Action<Action<RadioListItem>> RegisterRadioFound { get; init; }

        /// <summary>Unregister from radio-found events.</summary>
        public required Action UnregisterRadioFound { get; init; }

        /// <summary>Auto-connect serial from saved config (empty if none).</summary>
        public string AutoConnectSerial { get; init; } = "";

        /// <summary>Whether auto-connect is desired for the saved radio.</summary>
        public bool AutoConnectDesired { get; init; }

        /// <summary>Whether low bandwidth is set for the saved auto-connect radio.</summary>
        public bool AutoConnectLowBW { get; init; }

        /// <summary>Whether this is the initial startup (enables auto-connect timer).</summary>
        public bool IsInitialBringup { get; init; }

        /// <summary>Whether global auto-connect on startup is enabled.</summary>
        public bool GlobalAutoConnectEnabled { get; init; }

        /// <summary>Save auto-connect settings. Params: serial, radioName, isRemote, lowBW, enabled.</summary>
        public required Action<string, string, bool, bool, bool> SaveAutoConnectSettings { get; init; }

        /// <summary>Save global auto-connect on startup setting.</summary>
        public required Action<bool> SaveGlobalAutoConnect { get; init; }

        /// <summary>Check if a different radio has auto-connect enabled. Returns (hasOther, otherName).</summary>
        public required Func<string, (bool hasOther, string otherName)> CheckOtherAutoConnect { get; init; }

        /// <summary>Screen reader speak (message, interrupt).</summary>
        public Action<string, bool>? ScreenReaderSpeak { get; init; }

        /// <summary>Current SmartLink email for config saving.</summary>
        public string CurrentSmartLinkEmail { get; init; } = "";

        /// <summary>OpenParms for creating test FlexBase instances.</summary>
        public FlexBase.OpenParms? OpenParms { get; init; }

        /// <summary>Show a WinForms connecting window (message). Returns an action to close it.</summary>
        public Func<string, Action>? ShowConnecting { get; init; }

        /// <summary>Open the SmartLink account manager to switch accounts.</summary>
        public Action? ShowSmartLinkAccountManager { get; init; }

        /// <summary>
        /// Remote-first startup: the SmartLink account that will be used has
        /// asked for Remote discovery to begin the moment the selector opens,
        /// instead of waiting for the Remote button. Per-account, opt-in
        /// (SmartLinkAccount.AutoStartRemote). Local discovery still runs —
        /// this setting adds radios, it never subtracts.
        /// </summary>
        public bool AutoStartRemote { get; init; }
    }

    public partial class RigSelectorDialog : JJFlexDialog
    {
        private const string MustSelect = "You must select a radio.";

        private readonly RigSelectorCallbacks _callbacks;
        private readonly List<RadioListItem> _radiosList = new();
        private readonly object _radiosLock = new();
        private readonly DispatcherTimer _autoConnectTimer;
        /// <summary>
        /// The selected radio data, or null if cancelled.
        /// </summary>
        public object? SelectedRigData { get; private set; }

        /// <summary>
        /// The serial number of the selected radio, or null if cancelled.
        /// </summary>
        public string? SelectedSerial { get; private set; }

        /// <summary>
        /// Whether low bandwidth was selected for the connection.
        /// </summary>
        public bool SelectedLowBW { get; private set; }

        /// <summary>
        /// Whether the selected radio is a remote (SmartLink) radio.
        /// </summary>
        public bool SelectedIsRemote { get; private set; }

        public RigSelectorDialog(RigSelectorCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));

            InitializeComponent();

            // Reflect the saved setting without firing Checked/Unchecked --
            // otherwise opening the selector announces "Auto-connect on
            // startup enabled" as if the user had just toggled it.
            _suppressGlobalAutoConnectEvent = true;
            GlobalAutoConnectCheckbox.IsChecked = callbacks.GlobalAutoConnectEnabled;
            _suppressGlobalAutoConnectEvent = false;

            // Set up auto-connect timer
            _autoConnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _autoConnectTimer.Tick += AutoConnectTimer_Tick;

            // Register for radio discovery events
            _callbacks.RegisterRadioFound(OnRadioFound);

            // Start local discovery
            _callbacks.StartLocalDiscovery();

            // An EMPTY focused ListBox with TabNavigation="Once" can swallow
            // Tab outright — WPF tries to move into the (nonexistent) items
            // and goes nowhere. Don hit this live on 2026-08-06: selector open
            // at startup, no radios found yet, Tab dead, couldn't reach the
            // Remote button without the mouse. When the list is empty, route
            // Tab out explicitly in the right direction.
            RadiosBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Tab && RadiosBox.Items.Count == 0)
                {
                    var direction =
                        (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0
                            ? System.Windows.Input.FocusNavigationDirection.Previous
                            : System.Windows.Input.FocusNavigationDirection.Next;
                    e.Handled = true;
                    RadiosBox.MoveFocus(new System.Windows.Input.TraversalRequest(direction));
                }
                // The single-radio auto-select announcement has promised
                // "Press Enter to connect" since it shipped, but no Enter
                // handler ever existed — Enter on the list did nothing, and
                // if focus was actually sitting on a button (where WPF's
                // focus-restore drops it after the connecting window closes),
                // Enter clicked THAT button instead. Noel hit exactly that on
                // 2026-08-06 (trace 164250): his first "connect" press
                // re-fired Remote discovery. Make the spoken promise true.
                else if (e.Key == System.Windows.Input.Key.Enter
                         && GetSelectedRadio() is RadioListItem selected)
                {
                    e.Handled = true;
                    DoConnect(selected);
                }
            };

            // Announce empty list after discovery settles (500ms)
            // Also force keyboard focus to the ListBox so Tab works even when empty
            Loaded += async (_, _) =>
            {
                // Ensure the empty ListBox is keyboard-focusable immediately
                RadiosBox.Focus();
                System.Windows.Input.Keyboard.Focus(RadiosBox);

                await System.Threading.Tasks.Task.Delay(500);
                // "Press Remote" would be stale advice while remote-first
                // startup is already running Remote for the user.
                if (RadiosBox.Items.Count == 0 && !_remoteDiscoveryInFlight)
                {
                    _callbacks.ScreenReaderSpeak?.Invoke("Radio list, empty. No radios found yet. Press Remote for remote radios.", false);
                }
            };

            // Remote-first startup: the account in use asked for Remote to
            // begin immediately. Fire after Loaded so the window is up and
            // announcing before the connecting window appears over it.
            if (callbacks.AutoStartRemote)
            {
                Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _callbacks.ScreenReaderSpeak?.Invoke("Starting remote radios for your account.", false);
                    StartRemoteFlow();
                }), DispatcherPriority.Background);
            }

            // Start auto-connect timer if appropriate
            if (callbacks.IsInitialBringup &&
                callbacks.GlobalAutoConnectEnabled &&
                !string.IsNullOrEmpty(callbacks.AutoConnectSerial) &&
                callbacks.AutoConnectDesired)
            {
                _autoConnectTimer.Start();
            }
        }

        private void OnRadioFound(RadioListItem radio)
        {
            // Apply saved auto-connect state
            if (_callbacks.AutoConnectSerial == radio.Serial)
            {
                radio.AutoConnect = _callbacks.AutoConnectDesired;
                radio.LowBW = _callbacks.AutoConnectLowBW;
            }

            lock (_radiosLock)
            {
                // Replace IN PLACE, never remove-then-append. A LAN radio
                // re-announces itself roughly once a second, and appending moved
                // it to the bottom of the list on every packet — so the list
                // silently reordered under the user between the moment a screen
                // reader announced a row and the moment they pressed Enter on it.
                // Noel hit exactly that on 2026-08-05: arrowed to Don's
                // 6300inshack, pressed Enter, connected to his own 8600.
                int existing = _radiosList.FindIndex(r => r.Serial == radio.Serial);
                if (existing >= 0)
                    _radiosList[existing] = radio;
                else
                    _radiosList.Add(radio);
            }

            Dispatcher.Invoke(() =>
            {
                // Close the connecting window — radios have arrived
                if (_closeConnecting != null)
                {
                    _closeConnecting();
                    _closeConnecting = null;

                    // Reclaim focus from the closing connecting form. List
                    // first so FocusRadioList has an item container to land on.
                    RefreshRadiosList();
                    FocusRadioList();
                }

                RefreshRadiosList();
            });
        }

        private void RefreshRadiosList()
        {
            // A LAN radio re-announces about once a second, and every
            // announcement used to tear the ListBox down and rebuild it —
            // destroying focused containers, firing spurious SelectionChanged
            // events with a null selection, and generally moving the floor
            // under a keyboard user for no visible gain. Rebuild only when the
            // rendered list actually differs.
            // Remote radios first (Noel, 2026-08-05): pressing Remote means "show
            // me my remote radios", so they must not sit below locally discovered
            // ones the user did not ask about. Stable within each group — a LAN
            // radio re-announcing itself must never reorder anything.
            lock (_radiosLock)
            {
                var ordered = _radiosList
                    .Select((r, i) => (radio: r, index: i))
                    .OrderByDescending(x => x.radio.IsRemote)
                    .ThenBy(x => x.index)
                    .Select(x => x.radio)
                    .ToList();
                if (!_radiosList.SequenceEqual(ordered))
                {
                    _radiosList.Clear();
                    _radiosList.AddRange(ordered);
                }
            }

            lock (_radiosLock)
            {
                if (RadiosBox.Items.Count == _radiosList.Count)
                {
                    bool identical = true;
                    for (int i = 0; i < _radiosList.Count; i++)
                    {
                        if (RadiosBox.Items[i] is not RadioListItem shown
                            || shown.Serial != _radiosList[i].Serial
                            || shown.DisplayText != _radiosList[i].DisplayText)
                        {
                            identical = false;
                            break;
                        }
                    }
                    if (identical)
                        return;
                }
            }

            var selectedSerial = (RadiosBox.SelectedItem as RadioListItem)?.Serial;
            RadiosBox.Items.Clear();
            lock (_radiosLock)
            {
                foreach (var radio in _radiosList)
                    RadiosBox.Items.Add(radio);
            }

            // Restore selection
            if (selectedSerial != null)
            {
                for (int i = 0; i < RadiosBox.Items.Count; i++)
                {
                    if (((RadioListItem)RadiosBox.Items[i]).Serial == selectedSerial)
                    {
                        RadiosBox.SelectedIndex = i;

                        // Restoring SelectedIndex does NOT restore keyboard focus:
                        // Items.Clear() destroyed the focused container, so the
                        // ListBox's arrow-key anchor falls back to the top of the
                        // list. The next Down arrow would then move from item 0
                        // rather than from where the user actually was — landing
                        // them on a different radio than the one just announced.
                        if (RadiosBox.IsKeyboardFocusWithin)
                        {
                            RadiosBox.UpdateLayout();
                            if (RadiosBox.ItemContainerGenerator.ContainerFromIndex(i)
                                    is System.Windows.Controls.ListBoxItem container)
                            {
                                container.Focus();
                            }
                        }
                        break;
                    }
                }
            }

            // Auto-select if there's only one radio in the list
            if (RadiosBox.SelectedIndex < 0 && RadiosBox.Items.Count == 1)
            {
                RadiosBox.SelectedIndex = 0;
                var radio = (RadioListItem)RadiosBox.Items[0];
                var name = string.IsNullOrWhiteSpace(radio.Name) ? "radio" : radio.Name;
                _callbacks.ScreenReaderSpeak?.Invoke($"{name} selected. Press Enter to connect.", false);
            }

            // Update accessible label for empty list
            if (RadiosBox.Items.Count == 0)
            {
                System.Windows.Automation.AutomationProperties.SetName(
                    RadiosBox, "Radio list, empty, no radios found");
            }
            else
            {
                System.Windows.Automation.AutomationProperties.SetName(
                    RadiosBox, "Available radios");
            }
        }

        private RadioListItem? GetSelectedRadio()
        {
            return RadiosBox.SelectedItem as RadioListItem;
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                if (RadiosBox.Items.Count == 0)
                    ShowNoRadiosGuidance();
                else
                {
                    new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                    RadiosBox.Focus();
                }
                return;
            }

            DoConnect(radio);
        }

        private void DoConnect(RadioListItem radio)
        {
            var radioName = string.IsNullOrWhiteSpace(radio.Name) ? "radio" : radio.Name;
            _callbacks.ScreenReaderSpeak?.Invoke($"Connecting to {radioName}", true);
            // AS prosign (wait / standing by) alongside the "Connecting to X" speech.
            // Pair with BT which fires at connect-ready in MainWindow.PowerOn.
            if (ScreenReaderOutput.CwNotificationsEnabled) _ = ScreenReaderOutput.PlayCwAS?.Invoke();

            SelectedRigData = radio.RigData;
            SelectedSerial = radio.Serial;
            SelectedLowBW = radio.LowBW;
            SelectedIsRemote = radio.IsRemote;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// When true, suppresses the LowBWCheckBox's Checked / Unchecked handler
        /// so we can programmatically sync the checkbox state from the newly
        /// selected radio without that programmatic IsChecked write being
        /// interpreted as a user toggle. WPF raises Checked / Unchecked on every
        /// IsChecked change, so without this flag SelectionChanged → set
        /// IsChecked → handler → re-write radio.LowBW would either no-op or
        /// silently clobber state we just read.
        /// </summary>
        private bool _suppressLowBWCheckboxEvent;
        private bool _suppressGlobalAutoConnectEvent;

        private void LowBWCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressLowBWCheckboxEvent) return;
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                // Defensive: checkbox should be disabled when nothing is selected,
                // but in case some focus/keyboard race fires the event anyway,
                // resync the visual to "off" without complaining at the user.
                _suppressLowBWCheckboxEvent = true;
                LowBWCheckBox.IsChecked = false;
                _suppressLowBWCheckboxEvent = false;
                return;
            }
            radio.LowBW = LowBWCheckBox.IsChecked == true;
            RefreshRadiosList();
            // RefreshRadiosList clears + re-adds; restore selection by serial so
            // arrow-key context isn't lost. SelectionChanged will re-sync the
            // checkbox from the (now updated) radio.LowBW — no churn because the
            // bit we just wrote is the same bit it'll read back.
            for (int i = 0; i < RadiosBox.Items.Count; i++)
            {
                if (RadiosBox.Items[i] is RadioListItem r && r.Serial == radio.Serial)
                {
                    RadiosBox.SelectedIndex = i;
                    break;
                }
            }
        }

        private Action? _closeConnecting;

        /// <summary>True while a Remote discovery pass is running.</summary>
        private bool _remoteDiscoveryInFlight;

        /// <summary>When the last Remote discovery pass completed (UTC).</summary>
        private DateTime _remoteDiscoveryCompletedUtc = DateTime.MinValue;

        private void RemoteButton_Click(object sender, RoutedEventArgs e)
        {
            StartRemoteFlow();
        }

        private void StartRemoteFlow()
        {
            // Re-entry guards (2026-08-06, trace 164250): a stray keypress
            // right after discovery completes used to re-run the whole flow —
            // list flicker, a redundant re-registration the SmartLink server
            // answers with "Invalid state for application registration", and
            // a ~15s detour for the user. A live, fresh result doesn't need
            // re-discovering; just put the user back on the list.
            if (_remoteDiscoveryInFlight)
            {
                _callbacks.ScreenReaderSpeak?.Invoke("Remote discovery is already running.", false);
                return;
            }
            bool haveRemoteRadios;
            lock (_radiosLock)
            {
                haveRemoteRadios = _radiosList.Exists(r => r.IsRemote);
            }
            if (haveRemoteRadios
                && (DateTime.UtcNow - _remoteDiscoveryCompletedUtc) < TimeSpan.FromSeconds(5))
            {
                _callbacks.ScreenReaderSpeak?.Invoke("Remote radios already listed.", false);
                FocusRadioList();
                return;
            }

            _remoteDiscoveryInFlight = true;

            // Show WinForms connecting window to hold focus while SmartLink auth runs.
            _closeConnecting = _callbacks.ShowConnecting?.Invoke("Connecting to SmartLink...");

            _callbacks.StartRemoteDiscovery((success) =>
            {
                // Called from SmartLink thread when discovery completes.
                _remoteDiscoveryInFlight = false;
                _remoteDiscoveryCompletedUtc = DateTime.UtcNow;
                // Close ConnectingForm first.
                if (_closeConnecting != null)
                {
                    _closeConnecting();
                    _closeConnecting = null;
                }
                // Activate the selector and put focus on the radio list. On a
                // zero-radio result the protocol layer (FlexBase.setupRemote)
                // already speaks "No SmartLink radios available — the remote
                // radio may be turned off" via ScreenReaderOutput.Speak with
                // VerbosityLevel.Critical + interrupt:true. The RadiosBox
                // additionally carries AutomationProperties.Name "Radio list,
                // empty, no radios found" so focus-landing alone re-confirms
                // the empty state. We deliberately do NOT pop a modal
                // "No Radios Found" MessageDialog here — that used to add a
                // ~9 second extra dismissal step (find OK via screen reader,
                // click) on top of the protocol-layer speech, which was
                // redundant and friction-tax-hostile for screen-reader users.
                // The dialog's empty state is now self-announcing.
                Dispatcher.BeginInvoke(() =>
                {
                    // No RadioFound events fired this round, so RefreshRadiosList
                    // wasn't called and the empty-list AccessibleName fallback
                    // didn't run. Set it explicitly so screen-reader
                    // focus-landing re-confirms the empty state.
                    if (RadiosBox.Items.Count == 0)
                    {
                        System.Windows.Automation.AutomationProperties.SetName(
                            RadiosBox, "Radio list, empty, no radios found");
                    }
                    FocusRadioList();
                });
            });
        }

        /// <summary>
        /// Land keyboard focus on the radio list — on an ITEM, not the bare
        /// ListBox, whenever items exist. Focusing the container alone left
        /// Enter with no target and, worse, WPF's focus-restore after the
        /// connecting window closed could quietly put focus back on the
        /// Remote button while speech said the list was ready (2026-08-06
        /// first-keypress race). Selecting the first radio and focusing its
        /// ListBoxItem makes what the screen reader announces and what Enter
        /// acts on the same thing.
        /// </summary>
        private void FocusRadioList()
        {
            Activate();
            if (RadiosBox.Items.Count == 0)
            {
                RadiosBox.Focus();
                System.Windows.Input.Keyboard.Focus(RadiosBox);
                return;
            }
            if (RadiosBox.SelectedIndex < 0)
                RadiosBox.SelectedIndex = 0;
            RadiosBox.UpdateLayout();
            if (RadiosBox.ItemContainerGenerator.ContainerFromIndex(RadiosBox.SelectedIndex)
                    is System.Windows.Controls.ListBoxItem container)
            {
                container.Focus();
            }
            else
            {
                RadiosBox.Focus();
            }
        }

        private void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
        {
            _callbacks.ShowSmartLinkAccountManager?.Invoke();
            _callbacks.ScreenReaderSpeak?.Invoke("Account updated. Press Remote to connect.", false);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AutoConnectTimer_Tick(object? sender, EventArgs e)
        {
            RadioListItem? radio = null;
            lock (_radiosLock)
            {
                radio = _radiosList.Find(r => r.Serial == _callbacks.AutoConnectSerial);
            }

            if (radio != null)
            {
                _autoConnectTimer.Stop();
                DoConnect(radio);
            }
        }

        private void AutoConnectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            // Check if another radio has auto-connect
            var (hasOther, otherName) = _callbacks.CheckOtherAutoConnect(radio.Serial);
            if (hasOther && !radio.AutoConnect)
            {
                var displayOther = string.IsNullOrEmpty(otherName) ? "Another radio" : otherName;
                var result = MessageBox.Show(
                    $"{displayOther} currently has auto-connect enabled.\n\nSwitch auto-connect to {radio.Name}?",
                    "Switch Auto-Connect",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // Show settings dialog
            var newAutoConnect = radio.AutoConnect;
            var newLowBW = radio.LowBW;

            if (AutoConnectSettingsDialog.ShowSettingsDialog(this, radio.Name, ref newAutoConnect, ref newLowBW))
            {
                // Clear auto-connect from other radios
                if (newAutoConnect)
                {
                    lock (_radiosLock)
                    {
                        foreach (var r in _radiosList)
                        {
                            if (r.Serial != radio.Serial)
                                r.AutoConnect = false;
                        }
                    }
                }

                radio.AutoConnect = newAutoConnect;
                radio.LowBW = newLowBW;

                // Save settings
                _callbacks.SaveAutoConnectSettings(
                    radio.Serial, radio.Name, radio.IsRemote,
                    newLowBW, newAutoConnect);

                RefreshRadiosList();

                if (newAutoConnect)
                    _callbacks.ScreenReaderSpeak?.Invoke($"Auto-connect set for {radio.Name}", true);
                else
                    _callbacks.ScreenReaderSpeak?.Invoke($"Auto-connect cleared for {radio.Name}", true);
            }
        }

        private void ConnectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton_Click(sender, e);
        }


        private void RadiosBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsDefault = true;
            UpdateRadioListAccessibility();
        }

        private void UpdateRadioListAccessibility()
        {
            int count = RadiosBox.Items.Count;
            if (count == 0)
            {
                System.Windows.Automation.AutomationProperties.SetName(RadiosBox, "Radio list, 0 items. Searching for radios.");
                ScreenReaderOutput.Speak("No radios found yet. Searching.", VerbosityLevel.Critical, true);
            }
            else
            {
                var selected = RadiosBox.SelectedItem as RadioListItem;
                int idx = RadiosBox.SelectedIndex + 1;
                string name = selected?.DisplayText ?? "none selected";
                System.Windows.Automation.AutomationProperties.SetName(RadiosBox, $"Radio list, {count} items. {name}, {idx} of {count}");
                ScreenReaderOutput.Speak($"{name}, {idx} of {count}", VerbosityLevel.Terse, true);
            }
        }

        private void RadiosBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsDefault = false;
        }

        private void GlobalAutoConnectCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressGlobalAutoConnectEvent) return;

            var enabled = GlobalAutoConnectCheckbox.IsChecked == true;
            _callbacks.SaveGlobalAutoConnect(enabled);

            if (enabled)
                _callbacks.ScreenReaderSpeak?.Invoke("Auto-connect on startup enabled", true);
            else
                _callbacks.ScreenReaderSpeak?.Invoke("Auto-connect on startup disabled", true);
        }

        private void RadiosBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // TestButton stays enabled for tab-order accessibility.
            // Click handler validates selection. Only disable during active test.

            var selected = RadiosBox.SelectedItem as RadioListItem;

            // Auto-connect button requires a selected radio
            AutoConnectButton.IsEnabled = selected != null;

            // Low BW checkbox tracks the selected radio's LowBW. Disabled when
            // nothing is selected so the screen reader announces "not available"
            // rather than offering a control with no target. Suppress the change
            // event so syncing IsChecked from the radio doesn't re-trigger
            // LowBWCheckBox_Changed (which would write back the same bit).
            _suppressLowBWCheckboxEvent = true;
            LowBWCheckBox.IsEnabled = selected != null;
            LowBWCheckBox.IsChecked = selected?.LowBW == true;
            _suppressLowBWCheckboxEvent = false;

            // Announce selected item if list has focus.
            // IsKeyboardFocusWithin, NOT IsFocused: once WPF realizes item
            // containers, keyboard focus lives on the ListBoxItem and the
            // ListBox itself reports IsFocused == false — which silently killed
            // every arrow-key announcement (Noel, 2026-08-05: "it's not
            // actually in the list").
            if (RadiosBox.IsKeyboardFocusWithin && selected != null)
            {
                int idx = RadiosBox.SelectedIndex + 1;
                int count = RadiosBox.Items.Count;
                ScreenReaderOutput.Speak($"{selected.DisplayText}, {idx} of {count}", VerbosityLevel.Terse, true);
            }
        }

        private void AutoConnectButton_Click(object sender, RoutedEventArgs e)
        {
            AutoConnectMenuItem_Click(sender, e);
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            if (_callbacks.OpenParms == null)
            {
                _callbacks.ScreenReaderSpeak?.Invoke("Connection testing not available", true);
                return;
            }

            // Launch standalone ConnectionTesterDialog
            var dialog = new ConnectionTesterDialog(
                radio.Name,
                radio.Serial,
                radio.IsRemote,
                radio.LowBW,
                _callbacks.OpenParms!,
                _callbacks.CurrentSmartLinkEmail)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }

        private void ShowNoRadiosGuidance()
        {
            new MessageDialog
            {
                Title = "No Radios Found",
                Message = "No radios found. Click SmartLink to discover remote radios.",
                Owner = this
            }.ShowDialog();
        }

        private void RigSelectorDialog_Closing(object? sender, CancelEventArgs e)
        {
            _autoConnectTimer.Stop();
            _callbacks.UnregisterRadioFound();
        }
    }
}
