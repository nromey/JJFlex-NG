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
    ///
    /// <para>A row is one RADIO, not one sighting. A radio that answers on the
    /// local network and through SmartLink gets a single row with a path
    /// affordance, never two rows: screen-reader users arrow this list, and two
    /// rows carrying the same nickname read as two radios.</para>
    /// </summary>
    public class RadioListItem
    {
        public string Serial { get; set; } = "";
        public string Name { get; set; } = "";
        public string ModelName { get; set; } = "";
        public bool AutoConnect { get; set; }
        public bool LowBW { get; set; }
        public object RigData { get; set; } = null!;

        /// <summary>Local discovery can see this radio right now.</summary>
        public bool LanAvailable { get; set; }

        /// <summary>The SmartLink account's radio list carries it right now.</summary>
        public bool WanAvailable { get; set; }

        /// <summary>Reachable by at least one path this moment. A row that is not
        /// live is a ROSTER row — real history, nothing to dial.</summary>
        public bool IsLive => LanAvailable || WanAvailable;

        /// <summary>Answers on both paths, so the operator gets to choose.</summary>
        public bool DualHomed => LanAvailable && WanAvailable;

        /// <summary>
        /// The operator's explicit "connect over SmartLink even though it's
        /// local" choice, per connect, never persisted. Only consulted for a
        /// dual-homed radio; local is the default because local is the better
        /// path.
        /// </summary>
        public bool PreferRemotePath { get; set; }

        /// <summary>User-marked favorite; favorites sort to the top.</summary>
        public bool IsFavorite { get; set; }

        /// <summary>True for a roster row this install has seen before but
        /// nothing can see now.</summary>
        public bool KnownOffline => !IsLive;

        /// <summary>Last sighting arrived over SmartLink (roster rows only).</summary>
        public bool LastSeenRemote { get; set; }

        /// <summary>Pre-rendered spoken age ("last seen 3 days ago"), computed
        /// once when the roster loads. Recomputing it per read would make the
        /// row text drift minute by minute and force pointless list rebuilds.</summary>
        public string LastSeenText { get; set; } = "";

        /// <summary>SmartLink account that last listed this radio.</summary>
        public string LastSeenViaAccount { get; set; } = "";

        /// <summary>Row came from the cached per-account radio list — a fast
        /// paint, honest about its provenance, never a connect authority.</summary>
        public bool FromAccountCache { get; set; }

        /// <summary>Set while a SmartLink pass is running, so a cached row can
        /// say "refreshing" instead of pretending to be current.</summary>
        public bool RefreshInFlight { get; set; }

        /// <summary>
        /// Whether connecting to this row travels the SmartLink path. Derived,
        /// never stored: one source of truth for a question the connect code,
        /// the announcement, and the auto-connect record all ask separately.
        /// </summary>
        public bool IsRemote =>
            DualHomed ? PreferRemotePath
            : WanAvailable ? true
            : LanAvailable ? false
            : LastSeenRemote;

        /// <summary>Where this radio is, in words. Row text and the accessible
        /// name are the same string — what a sighted user reads and what a
        /// screen reader says must not diverge.</summary>
        public string WhereText
        {
            get
            {
                if (DualHomed)
                {
                    return PreferRemotePath
                        ? "local network and SmartLink, using SmartLink"
                        : "local network and SmartLink, using local network";
                }
                if (LanAvailable) return "local network";
                if (WanAvailable) return "remote via SmartLink";

                // Roster row: say it is offline first, then how it was last seen.
                var lastPath = LastSeenRemote ? "last seen remote via SmartLink" : "last seen on the local network";
                var age = string.IsNullOrEmpty(LastSeenText) ? "" : ", " + LastSeenText;
                if (FromAccountCache && !string.IsNullOrWhiteSpace(LastSeenViaAccount))
                {
                    var refreshing = RefreshInFlight ? ", refreshing" : "";
                    return $"offline, last known for {LastSeenViaAccount}{age}{refreshing}";
                }
                return $"offline, {lastPath}{age}";
            }
        }

        public string DisplayText
        {
            get
            {
                var fav = IsFavorite ? "Favorite, " : "";
                var autoConn = AutoConnect ? "[AutoConnect] " : "";
                var lbw = LowBW ? "[LowBW] " : "";
                var namePart = string.IsNullOrWhiteSpace(Name) || Name == "Unknown" ? "Unnamed" : Name;
                var modelPart = string.IsNullOrWhiteSpace(ModelName) || ModelName == "Unknown"
                    ? "Unknown model" : ModelName;
                // Source, not serial. Two radios that differ only by where they
                // are were indistinguishable by ear — an unnamed local rig and a
                // remote one read as near-identical rows of digits. The serial is
                // rarely what the user needs and never what they navigate by.
                return $"{fav}{autoConn}{lbw}{namePart}, {modelPart}, {WhereText}";
            }
        }

        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// Which SmartLink account the selector is working with, and how many are
    /// saved. Drives the account button's label, its accessible name, and the
    /// readable account line — one state, one helper, three surfaces.
    /// </summary>
    public sealed class SmartLinkAccountState
    {
        public int Count { get; set; }
        public string Email { get; set; } = "";
        public string FriendlyName { get; set; } = "";
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

        /// <summary>
        /// Refresh the remote radio list by cycling the SmartLink session
        /// (the server sends the list once per session, so a real refresh
        /// needs a fresh one). When wired, the Remote button morphs into
        /// "Refresh Remote List" after a successful remote pass.
        /// </summary>
        public Action<Action<bool>>? StartRemoteRefresh { get; init; }

        /// <summary>Register for radio-removed events (serial, name) — WAN radios that vanished from a fresh list.</summary>
        public Action<Action<string, string>>? RegisterRadioRemoved { get; init; }

        /// <summary>Unregister from radio-removed events.</summary>
        public Action? UnregisterRadioRemoved { get; init; }

        /// <summary>
        /// Which paths currently reach this radio. Asked after a radio-removed
        /// event, because the event says a radio left without saying WHICH home
        /// it left — a dual-homed radio dropping off the LAN is still perfectly
        /// reachable over SmartLink and must not be announced as gone.
        /// </summary>
        public Func<string, (bool lan, bool wan)>? GetRadioAvailability { get; init; }

        /// <summary>
        /// Live SmartLink account state (saved count plus the account that would
        /// actually be used). Read fresh rather than captured at construction —
        /// the account manager can change it while the selector is open.
        /// </summary>
        public Func<SmartLinkAccountState>? GetSmartLinkAccountState { get; init; }

        /// <summary>
        /// The app's current rig object, for the network identity card in the
        /// detail area (QB Track L). Read fresh on every refresh — the app can
        /// dispose and recreate its rig while the selector is open. Null or a
        /// not-yet-connected rig is the normal pre-connect state; the card says
        /// "No radio connected" rather than going blank.
        /// </summary>
        public Func<FlexBase?>? GetCurrentRig { get; init; }
    }

    public partial class RigSelectorDialog : JJFlexDialog
    {
        private const string MustSelect = "You must select a radio.";

        /// <summary>
        /// How long the empty-list announcement waits for discovery to land.
        /// The old 500ms lost the race constantly: "no radios found yet" would
        /// finish speaking just as the first radio appeared, so the dialog's
        /// last word contradicted its own contents. Announce only if the list
        /// is STILL empty after discovery has had a real chance.
        /// </summary>
        private const int DiscoverySettleMs = 2500;

        private readonly RigSelectorCallbacks _callbacks;
        private readonly List<RadioListItem> _radiosList = new();
        private readonly object _radiosLock = new();
        private readonly DispatcherTimer _autoConnectTimer;

        /// <summary>Serials whose sighting has already been written this session.
        /// A LAN radio re-announces about once a second and the roster stamp is
        /// read once per launch — one write per radio per open is plenty.</summary>
        private readonly HashSet<string> _sightingsRecorded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True once any radio has been seen live in this session.</summary>
        private bool _anyLiveRadioSeen;

        /// <summary>Guards the Shift+Tab focus redirect against re-entering
        /// itself if the item container cannot be realized.</summary>
        private bool _redirectingFocusToRow;

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

        /// <summary>
        /// True when the operator explicitly chose the SmartLink path for a
        /// radio that is ALSO on the local network. The connect layer must not
        /// quietly substitute the LAN path when this is set.
        /// </summary>
        public bool SelectedPreferRemotePath { get; private set; }

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

            UpdateAccountAffordances();

            // Network identity card (QB Track L). The card describes the rig
            // this app currently holds — before a connect that reads as
            // "No radio connected", which is the honest answer. A separate
            // event subscription (not a change to the selection handler) keeps
            // it current as the user moves through the list, and re-resolving
            // the rig on each refresh also picks up connects/disconnects that
            // happen while the selector is open.
            RefreshIdentityCard();
            RadiosBox.SelectionChanged += (_, _) => RefreshIdentityCard();

            // Set up auto-connect timer
            _autoConnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _autoConnectTimer.Tick += AutoConnectTimer_Tick;

            // Paint the known-radios roster BEFORE discovery starts. An install
            // that has met a radio before should never show an empty box while
            // UDP discovery warms up; the rows say "offline" until something
            // proves otherwise, which is honest and immediately speakable.
            PaintRoster(CurrentAccountEmail());

            // Register for radio discovery events
            _callbacks.RegisterRadioFound(OnRadioFound);
            _callbacks.RegisterRadioRemoved?.Invoke(OnRadioRemoved);

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

            // Announce an empty list only after discovery has had a real chance.
            // Also force keyboard focus to the ListBox so Tab works even when empty.
            Loaded += async (_, _) =>
            {
                // Ensure the empty ListBox is keyboard-focusable immediately
                RadiosBox.Focus();
                System.Windows.Input.Keyboard.Focus(RadiosBox);

                var deadline = DateTime.UtcNow.AddMilliseconds(DiscoverySettleMs);
                while (DateTime.UtcNow < deadline && !_anyLiveRadioSeen)
                {
                    await System.Threading.Tasks.Task.Delay(200);
                }

                // A radio landed inside the window — its own arrival speech and
                // the auto-select line already told the user. Saying "no radios
                // found" on top of that is the collision this window exists to
                // prevent.
                if (_anyLiveRadioSeen) return;

                // "Press Remote" would be stale advice while remote-first
                // startup is already running Remote for the user.
                if (_remoteDiscoveryInFlight) return;

                AnnounceNothingLive();
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

        // ------------------------------------------------------------------
        // Known-radios roster
        // ------------------------------------------------------------------

        /// <summary>
        /// Load every radio this install has met and put it in the list as an
        /// offline row. Discovery upgrades rows to live as it finds them; it
        /// never has to invent them.
        /// </summary>
        private void PaintRoster(string accountEmail)
        {
            List<KnownRadioEntry> known;
            try
            {
                known = KnownRadioRoster.Load(accountEmail);
            }
            catch (Exception ex)
            {
                // A roster that cannot be read is a missing convenience, never a
                // reason the picker fails to open.
                System.Diagnostics.Trace.WriteLine($"RigSelector.PaintRoster: {ex.Message}");
                return;
            }

            bool added = false;
            lock (_radiosLock)
            {
                foreach (var k in known)
                {
                    if (_radiosList.Any(r => string.Equals(r.Serial, k.Serial, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    _radiosList.Add(new RadioListItem
                    {
                        Serial = k.Serial,
                        Name = k.Nickname,
                        ModelName = k.Model,
                        IsFavorite = k.IsFavorite,
                        LastSeenRemote = k.LastSeenRemote,
                        LastSeenText = KnownRadioRoster.DescribeAge(k.LastSeenUtc),
                        LastSeenViaAccount = k.LastSeenViaAccount,
                        FromAccountCache = k.InAccountCache,
                        AutoConnect = _callbacks.AutoConnectSerial == k.Serial && _callbacks.AutoConnectDesired,
                        LowBW = _callbacks.AutoConnectSerial == k.Serial && _callbacks.AutoConnectLowBW,
                    });
                    added = true;
                }
            }

            if (added) RefreshRadiosList();
        }

        /// <summary>
        /// Drop every roster row that no live sighting has claimed, so a fresh
        /// paint for a different account does not stack one account's history on
        /// top of another's.
        /// </summary>
        private void ClearOfflineRows()
        {
            lock (_radiosLock)
            {
                _radiosList.RemoveAll(r => !r.IsLive);
            }
        }

        private int LiveCount()
        {
            lock (_radiosLock) { return _radiosList.Count(r => r.IsLive); }
        }

        private void AnnounceNothingLive()
        {
            int known;
            lock (_radiosLock) { known = _radiosList.Count; }

            if (known == 0)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    "Radio list, empty. No radios found yet. Press Remote for remote radios.", false);
                return;
            }

            _callbacks.ScreenReaderSpeak?.Invoke(
                $"No radios online yet. {known} known radio{(known == 1 ? "" : "s")} listed, all offline. " +
                "Press Remote for remote radios.", false);
        }

        // ------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------

        private void OnRadioFound(RadioListItem radio)
        {
            // Apply saved auto-connect state
            if (_callbacks.AutoConnectSerial == radio.Serial)
            {
                radio.AutoConnect = _callbacks.AutoConnectDesired;
                radio.LowBW = _callbacks.AutoConnectLowBW;
            }

            _anyLiveRadioSeen = true;

            lock (_radiosLock)
            {
                // Update IN PLACE, never remove-then-append and never swap the
                // object out. A LAN radio re-announces itself roughly once a
                // second; appending moved it to the bottom of the list on every
                // packet, so the list silently reordered under the user between
                // the moment a screen reader announced a row and the moment they
                // pressed Enter on it. Noel hit exactly that on 2026-08-05:
                // arrowed to Don's 6300inshack, pressed Enter, connected to his
                // own 8600.
                //
                // Keeping the same object also protects state the discovery
                // event knows nothing about — the favorite flag, the operator's
                // chosen connection path, the roster's last-seen wording.
                int existing = _radiosList.FindIndex(r => r.Serial == radio.Serial);
                if (existing >= 0)
                {
                    var row = _radiosList[existing];
                    row.Name = radio.Name;
                    row.ModelName = radio.ModelName;
                    row.RigData = radio.RigData;
                    row.LanAvailable = radio.LanAvailable;
                    row.WanAvailable = radio.WanAvailable;
                    row.AutoConnect = radio.AutoConnect;
                    row.LowBW = radio.LowBW;
                    row.FromAccountCache = false;
                    row.RefreshInFlight = false;
                    // A path preference only means anything while both homes are
                    // up; drop it the moment the radio stops being dual-homed so
                    // a stale choice can't outlive the situation that produced it.
                    if (!row.DualHomed) row.PreferRemotePath = false;
                }
                else
                {
                    // A radio with no roster row has never been seen on this
                    // install, so it cannot be a favorite. Reading the favorite
                    // flag from disk here would put file IO under this lock on
                    // the discovery thread for no possible gain.
                    _radiosList.Add(radio);
                }
            }

            RecordSightingOnce(radio);

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
                SyncPathAffordance();
            });
        }

        private void RecordSightingOnce(RadioListItem radio)
        {
            if (string.IsNullOrWhiteSpace(radio.Serial)) return;
            lock (_sightingsRecorded)
            {
                if (!_sightingsRecorded.Add(radio.Serial)) return;
            }
            try
            {
                KnownRadioRoster.RecordSighting(
                    radio.Serial, radio.Name, radio.ModelName,
                    radio.WanAvailable && !radio.LanAvailable,
                    CurrentAccountEmail());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"RigSelector.RecordSighting: {ex.Message}");
            }
        }

        /// <summary>
        /// A radio left one of its homes. The event does not say which, so ask —
        /// a dual-homed radio dropping off the LAN is still perfectly reachable
        /// over SmartLink, and announcing it as gone would be false.
        /// </summary>
        private void OnRadioRemoved(string serial, string name)
        {
            Dispatcher.Invoke(() =>
            {
                RadioListItem? row;
                lock (_radiosLock)
                {
                    row = _radiosList.FirstOrDefault(r => r.Serial == serial);
                }
                if (row == null || !row.IsLive) return;

                bool wasDual = row.DualHomed;
                bool hadLan = row.LanAvailable;
                bool hadWan = row.WanAvailable;
                var avail = _callbacks.GetRadioAvailability?.Invoke(serial) ?? (lan: false, wan: false);
                row.LanAvailable = avail.lan;
                row.WanAvailable = avail.wan;
                if (!row.DualHomed) row.PreferRemotePath = false;

                var who = string.IsNullOrWhiteSpace(name)
                    ? (string.IsNullOrWhiteSpace(row.Name) ? "A radio" : row.Name)
                    : name;

                if (row.IsLive)
                {
                    // Still reachable the other way. Say which door closed —
                    // "went offline" would be a lie the user could act on.
                    RefreshRadiosList();
                    if (wasDual)
                    {
                        _callbacks.ScreenReaderSpeak?.Invoke(
                            row.LanAvailable
                                ? $"{who} is no longer listed over SmartLink. Still on the local network."
                                : $"{who} left the local network. Still available over SmartLink.",
                            false);
                    }
                    return;
                }

                // Fully gone. The row STAYS as a roster row — this install has
                // met the radio, and dropping it would make the list forget
                // something the user still cares about. It reads as offline and
                // refuses to connect, which is what the ghost sweep was for.
                // A radio that had a local home is remembered as local, even if
                // SmartLink also listed it — "press Remote to look again" is
                // useless advice for a rig that was sitting on the same LAN.
                row.LastSeenRemote = hadWan && !hadLan;
                row.LastSeenText = "last seen just now";
                row.FromAccountCache = false;

                bool hadKeyboard = RadiosBox.IsKeyboardFocusWithin;
                RefreshRadiosList();
                if (RadiosBox.SelectedIndex < 0 && RadiosBox.Items.Count > 0)
                {
                    RadiosBox.SelectedIndex = 0;
                    if (hadKeyboard) FocusRadioList();
                }
                _callbacks.ScreenReaderSpeak?.Invoke($"{who} went offline.", false);
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
            //
            // Order: favorites first (that is what a favorites list means),
            // then live radios above roster rows, then remote-capable above
            // local-only — pressing Remote means "show me my remote radios", so
            // they must not sit below locally discovered ones the user did not
            // ask about (Noel, 2026-08-05). Stable within each group: a LAN
            // radio re-announcing itself must never reorder anything.
            lock (_radiosLock)
            {
                var ordered = _radiosList
                    .Select((r, i) => (radio: r, index: i))
                    .OrderByDescending(x => x.radio.IsFavorite)
                    .ThenByDescending(x => x.radio.IsLive)
                    .ThenByDescending(x => x.radio.WanAvailable)
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
                            || !ReferenceEquals(shown, _radiosList[i])
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

            // Auto-select when exactly one radio is actually reachable. Counting
            // ROWS would fire on a roster full of offline radios and promise
            // "press Enter to connect" about something that isn't there.
            if (RadiosBox.SelectedIndex < 0)
            {
                RadioListItem? onlyLive = null;
                lock (_radiosLock)
                {
                    var live = _radiosList.Where(r => r.IsLive).ToList();
                    if (live.Count == 1) onlyLive = live[0];
                }
                if (onlyLive != null)
                {
                    RadiosBox.SelectedIndex = RadiosBox.Items.IndexOf(onlyLive);
                    var name = string.IsNullOrWhiteSpace(onlyLive.Name) ? "radio" : onlyLive.Name;
                    _callbacks.ScreenReaderSpeak?.Invoke($"{name} selected. Press Enter to connect.", false);
                }
            }

            UpdateListAutomationName();
        }

        private void UpdateListAutomationName()
        {
            int count = RadiosBox.Items.Count;
            int live = LiveCount();
            if (count == 0)
            {
                System.Windows.Automation.AutomationProperties.SetName(
                    RadiosBox, "Radio list, empty, no radios found");
            }
            else if (live == 0)
            {
                System.Windows.Automation.AutomationProperties.SetName(
                    RadiosBox, $"Known radios, {count} listed, none online");
            }
            else
            {
                System.Windows.Automation.AutomationProperties.SetName(
                    RadiosBox, $"Available radios, {count} listed, {live} online");
            }
        }

        private RadioListItem? GetSelectedRadio()
        {
            return RadiosBox.SelectedItem as RadioListItem;
        }

        /// <summary>
        /// Point the identity card at the app's current rig and re-render.
        /// Setting <see cref="Controls.NetworkIdentityCard.Rig"/> refreshes;
        /// the card itself guards against stealing focus mid-read.
        /// </summary>
        private void RefreshIdentityCard()
        {
            try
            {
                IdentityCard.Rig = _callbacks.GetCurrentRig?.Invoke();
            }
            catch (Exception ex)
            {
                // A detail card must never break the picker.
                System.Diagnostics.Trace.WriteLine($"RigSelector.RefreshIdentityCard: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Connect
        // ------------------------------------------------------------------

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

            if (!radio.IsLive)
            {
                HandleOfflineConnectAttempt(radio, radioName);
                return;
            }

            var via = radio.IsRemote ? "over SmartLink" : "on the local network";
            _callbacks.ScreenReaderSpeak?.Invoke($"Connecting to {radioName} {via}", true);
            // AS prosign (wait / standing by) alongside the "Connecting to X" speech.
            // Pair with BT which fires at connect-ready in MainWindow.PowerOn.
            if (ScreenReaderOutput.CwNotificationsEnabled) _ = ScreenReaderOutput.PlayCwAS?.Invoke();

            SelectedRigData = radio.RigData;
            SelectedSerial = radio.Serial;
            SelectedLowBW = radio.LowBW;
            SelectedIsRemote = radio.IsRemote;
            SelectedPreferRemotePath = radio.DualHomed && radio.PreferRemotePath;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Enter on a roster row. Never a dead end and never a connect from
        /// cache: if the radio was last seen over SmartLink and this session has
        /// not looked yet, LOOKING is the useful answer, and it puts a real
        /// refresh in flight.
        /// </summary>
        private void HandleOfflineConnectAttempt(RadioListItem radio, string radioName)
        {
            bool remoteish = radio.LastSeenRemote || radio.FromAccountCache;

            if (remoteish && !_remoteListLive && !_remoteDiscoveryInFlight)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"{radioName} was last seen over SmartLink. Looking for it now.", true);
                StartRemoteFlow();
                return;
            }

            if (remoteish && _remoteDiscoveryInFlight)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"Still looking for {radioName} over SmartLink.", true);
                return;
            }

            if (remoteish)
            {
                var acct = string.IsNullOrWhiteSpace(radio.LastSeenViaAccount)
                    ? "this account" : radio.LastSeenViaAccount;
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"{radioName} is not in {acct}'s radio list right now. It may be powered off, " +
                    "or registered to a different account. Refresh Remote List to look again.", true);
                return;
            }

            _callbacks.ScreenReaderSpeak?.Invoke(
                $"{radioName} is not on the local network right now. It may be powered off.", true);
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
        private bool _suppressPathComboEvent;

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

        // ------------------------------------------------------------------
        // Dual-homing: connection path
        // ------------------------------------------------------------------

        private const string PathLocal = "Local network";
        private const string PathSmartLink = "Remote via SmartLink";

        /// <summary>
        /// What the path control should currently be showing. Compared before
        /// every rebuild because a LAN radio re-announces about once a second,
        /// and tearing the combo's items down that often would fight a user who
        /// is arrowing through it.
        /// </summary>
        private string PathKey()
        {
            var r = GetSelectedRadio();
            return r == null
                ? "<none>"
                : $"{r.Serial}|{r.LanAvailable}|{r.WanAvailable}|{r.PreferRemotePath}|{r.LastSeenRemote}";
        }

        private string _pathAffordanceKey = "";

        /// <summary>
        /// Rebuild the path affordance for the current selection. A radio with
        /// one home still gets the control filled in and disabled, so the answer
        /// to "how will this connect?" is always present rather than blank.
        /// </summary>
        private void SyncPathAffordance()
        {
            var key = PathKey();
            if (key == _pathAffordanceKey) return;
            _pathAffordanceKey = key;

            var radio = GetSelectedRadio();
            _suppressPathComboEvent = true;
            try
            {
                PathCombo.Items.Clear();
                if (radio == null)
                {
                    PathCombo.IsEnabled = false;
                    System.Windows.Automation.AutomationProperties.SetName(
                        PathCombo, "Connection path, no radio selected");
                    return;
                }

                if (radio.DualHomed)
                {
                    PathCombo.Items.Add(PathLocal);
                    PathCombo.Items.Add(PathSmartLink);
                    PathCombo.SelectedIndex = radio.PreferRemotePath ? 1 : 0;
                    PathCombo.IsEnabled = true;
                    System.Windows.Automation.AutomationProperties.SetName(
                        PathCombo,
                        "Connection path. This radio answers both on the local network and through SmartLink; " +
                        "local is the better path and the default.");
                    return;
                }

                string only =
                    radio.LanAvailable ? PathLocal :
                    radio.WanAvailable ? PathSmartLink :
                    radio.LastSeenRemote ? "Offline, last seen via SmartLink" :
                    "Offline, last seen on the local network";
                PathCombo.Items.Add(only);
                PathCombo.SelectedIndex = 0;
                PathCombo.IsEnabled = false;
                System.Windows.Automation.AutomationProperties.SetName(
                    PathCombo,
                    radio.IsLive
                        ? "Connection path, only one path available"
                        : "Connection path, radio is offline");
            }
            finally
            {
                _suppressPathComboEvent = false;
            }
        }

        private void PathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPathComboEvent) return;
            var radio = GetSelectedRadio();
            if (radio == null || !radio.DualHomed) return;

            bool preferRemote = PathCombo.SelectedIndex == 1;
            if (preferRemote == radio.PreferRemotePath) return;

            radio.PreferRemotePath = preferRemote;
            // The combo already shows the new choice; re-syncing it from the
            // list refresh below would rip its items out from under the user's
            // focus for no change they can perceive.
            _pathAffordanceKey = PathKey();
            _callbacks.ScreenReaderSpeak?.Invoke(
                preferRemote
                    ? $"{RowName(radio)} will connect over SmartLink, even though it is on your local network."
                    : $"{RowName(radio)} will connect over the local network.",
                true);
            RefreshRadiosList();
            ReselectBySerial(radio.Serial);
        }

        private static string RowName(RadioListItem r) =>
            string.IsNullOrWhiteSpace(r.Name) ? "This radio" : r.Name;

        private void ReselectBySerial(string serial)
        {
            for (int i = 0; i < RadiosBox.Items.Count; i++)
            {
                if (RadiosBox.Items[i] is RadioListItem r && r.Serial == serial)
                {
                    RadiosBox.SelectedIndex = i;
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // Favorites
        // ------------------------------------------------------------------

        private void RadiosBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var radio = GetSelectedRadio();
            bool fav = radio?.IsFavorite == true;
            FavoriteMenuItem.Header = fav ? "Remove from Favorites" : "Add to Favorites";
            System.Windows.Automation.AutomationProperties.SetName(FavoriteMenuItem,
                fav ? "Remove selected radio from favorites" : "Add selected radio to favorites");
            FavoriteMenuItem.IsEnabled = radio != null;
        }

        private void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            bool wanted = !radio.IsFavorite;
            if (!KnownRadioRoster.SetFavorite(radio.Serial, wanted))
            {
                // The store declined. Saying "added to favorites" here would be a
                // promise the next launch breaks.
                _callbacks.ScreenReaderSpeak?.Invoke(
                    "Could not save the favorite setting. It would not survive a restart, so nothing was changed.",
                    true);
                return;
            }

            radio.IsFavorite = wanted;
            _callbacks.ScreenReaderSpeak?.Invoke(
                wanted ? $"{RowName(radio)} added to favorites. Favorites sort to the top."
                       : $"{RowName(radio)} removed from favorites.",
                true);
            RefreshRadiosList();
            ReselectBySerial(radio.Serial);
            if (RadiosBox.IsKeyboardFocusWithin) FocusRadioList();
        }

        private Action? _closeConnecting;

        /// <summary>True while a Remote discovery pass is running.</summary>
        private bool _remoteDiscoveryInFlight;

        /// <summary>
        /// True once a remote pass has succeeded (SmartLink session live).
        /// From then on the Remote button is "Refresh Remote List": with a
        /// live session, re-running discovery can never yield anything new —
        /// the server sends the radio list once per TLS session — so the only
        /// meaningful repeat action is a session-cycling refresh. No timer:
        /// "listed" is a state, not a five-second window (Noel, 2026-08-06).
        /// </summary>
        private bool _remoteListLive;

        private void RemoteButton_Click(object sender, RoutedEventArgs e)
        {
            StartRemoteFlow();
        }

        /// <summary>
        /// Morph the Remote button into Refresh Remote List — same button,
        /// same spot, same Alt+R, new job. One control renaming itself keeps
        /// the tab order stable and announces its own state change on the
        /// next focus visit; hiding one button and showing another moves the
        /// floor under a keyboard user.
        /// </summary>
        private void MorphRemoteToRefresh()
        {
            if (_remoteListLive) return;
            _remoteListLive = true;
            if (_callbacks.StartRemoteRefresh == null) return;
            RemoteButton.Content = "_Refresh Remote List";
            System.Windows.Automation.AutomationProperties.SetName(RemoteButton,
                "Refresh Remote List. Reconnects to SmartLink and looks again, picking up radios that came online since.");
            _callbacks.ScreenReaderSpeak?.Invoke("The Remote button is now Refresh Remote List.", false);
        }

        /// <param name="forceSessionCycle">
        /// Take the session-cycling refresh path even if no remote pass has
        /// succeeded yet. An account switch needs this: the server sends its
        /// radio list once per TLS session, so reusing the previous account's
        /// live session would hand back the previous account's radios.
        /// </param>
        private void StartRemoteFlow(bool forceSessionCycle = false)
        {
            if (_remoteDiscoveryInFlight)
            {
                _callbacks.ScreenReaderSpeak?.Invoke("Remote discovery is already running.", false);
                return;
            }

            bool refreshing = (_remoteListLive || forceSessionCycle) && _callbacks.StartRemoteRefresh != null;
            _remoteDiscoveryInFlight = true;
            MarkCachedRowsRefreshing(true);
            RefreshRadiosList();

            // Say WHICH account is about to be used. Anyone with more than one
            // SmartLink login was previously left to infer it from whichever
            // radios turned up (C2 item 15).
            var state = CurrentAccountState();
            if (!string.IsNullOrWhiteSpace(state.Email))
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    refreshing
                        ? $"Refreshing the radio list for {state.Email}."
                        : $"Connecting to SmartLink as {state.Email}.",
                    false);
            }

            // Show WinForms connecting window to hold focus while SmartLink auth runs.
            _closeConnecting = _callbacks.ShowConnecting?.Invoke(
                refreshing ? "Refreshing remote radios..." : "Connecting to SmartLink...");

            var liveBefore = LiveSerialSet();

            var start = refreshing ? _callbacks.StartRemoteRefresh! : _callbacks.StartRemoteDiscovery;
            start((success) =>
            {
                // Called from SmartLink thread when discovery completes.
                _remoteDiscoveryInFlight = false;
                if (success)
                {
                    Dispatcher.BeginInvoke(() => MorphRemoteToRefresh());
                }
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
                    MarkCachedRowsRefreshing(false);
                    UpdateAccountAffordances();
                    RefreshRadiosList();
                    UpdateListAutomationName();
                    SyncPathAffordance();
                    AnnounceListDelta(liveBefore, success);
                    FocusRadioList();
                });
            });
        }

        private HashSet<string> LiveSerialSet()
        {
            lock (_radiosLock)
            {
                return new HashSet<string>(
                    _radiosList.Where(r => r.IsLive).Select(r => r.Serial),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// The cached rows say "refreshing" only while a fetch is genuinely in
        /// flight. Provenance beats a TTL: the row states what it is and what is
        /// happening to it, rather than expiring on a timer nobody can hear.
        /// </summary>
        private void MarkCachedRowsRefreshing(bool refreshing)
        {
            lock (_radiosLock)
            {
                foreach (var r in _radiosList)
                {
                    if (r.FromAccountCache) r.RefreshInFlight = refreshing;
                }
            }
        }

        private void AnnounceListDelta(HashSet<string> liveBefore, bool success)
        {
            var liveNow = LiveSerialSet();
            if (!success)
            {
                // The protocol layer owns the failure wording; do not paper over it.
                return;
            }

            if (liveNow.SetEquals(liveBefore))
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    liveNow.Count == 0
                        ? "Radio list updated. No radios online."
                        : $"Radio list unchanged. {liveNow.Count} online.",
                    false);
                return;
            }

            _callbacks.ScreenReaderSpeak?.Invoke(
                $"Radio list updated. {liveNow.Count} radio{(liveNow.Count == 1 ? "" : "s")} online.", false);
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

        // ------------------------------------------------------------------
        // SmartLink account affordances
        // ------------------------------------------------------------------

        private SmartLinkAccountState CurrentAccountState()
        {
            try
            {
                return _callbacks.GetSmartLinkAccountState?.Invoke()
                       ?? new SmartLinkAccountState { Email = _callbacks.CurrentSmartLinkEmail };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"RigSelector.CurrentAccountState: {ex.Message}");
                return new SmartLinkAccountState { Email = _callbacks.CurrentSmartLinkEmail };
            }
        }

        private string CurrentAccountEmail() => CurrentAccountState().Email ?? "";

        /// <summary>
        /// One helper drives the account button's label, its accessible name,
        /// and the readable account line. The label is state-driven because
        /// "Switch Account" is nonsense with zero saved accounts and misleading
        /// with one — and because the button that signs you in for the first
        /// time should say so.
        /// </summary>
        private void UpdateAccountAffordances()
        {
            var state = CurrentAccountState();
            string who = !string.IsNullOrWhiteSpace(state.FriendlyName) ? state.FriendlyName : state.Email;

            if (state.Count <= 0)
            {
                AccountButton.Content = "_Sign in to SmartLink";
                System.Windows.Automation.AutomationProperties.SetName(AccountButton,
                    "Sign in to SmartLink. No SmartLink account is saved on this computer yet.");
                AccountStatusText.Text = "SmartLink account: none saved. Use Sign in to SmartLink to add one.";
            }
            else if (state.Count == 1)
            {
                AccountButton.Content = "_SmartLink Account";
                System.Windows.Automation.AutomationProperties.SetName(AccountButton,
                    string.IsNullOrWhiteSpace(who)
                        ? "SmartLink Account. One account saved."
                        : $"SmartLink Account. Using {who}.");
                AccountStatusText.Text = string.IsNullOrWhiteSpace(who)
                    ? "SmartLink account: one saved"
                    : $"SmartLink account: {who}";
            }
            else
            {
                AccountButton.Content = "_Switch Account";
                System.Windows.Automation.AutomationProperties.SetName(AccountButton,
                    string.IsNullOrWhiteSpace(who)
                        ? $"Switch Account. {state.Count} accounts saved."
                        : $"Switch Account. Currently using {who}, {state.Count} accounts saved.");
                AccountStatusText.Text = string.IsNullOrWhiteSpace(who)
                    ? $"SmartLink account: {state.Count} saved, none chosen"
                    : $"SmartLink account: {who} ({state.Count} saved)";
            }

            if (!string.IsNullOrWhiteSpace(state.Email)
                && !string.Equals(state.Email, state.FriendlyName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(state.FriendlyName))
            {
                AccountStatusText.Text += $", {state.Email}";
            }
        }

        private void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
        {
            var before = CurrentAccountState();
            _callbacks.ShowSmartLinkAccountManager?.Invoke();
            var after = CurrentAccountState();

            UpdateAccountAffordances();

            bool accountChanged = !string.Equals(before.Email ?? "", after.Email ?? "",
                StringComparison.OrdinalIgnoreCase);
            bool rosterChanged = before.Count != after.Count;

            if (accountChanged)
            {
                // Fast paint: this account's last known radios, immediately
                // speakable, clearly labelled as last-known — then a live fetch
                // replaces them. Never connect from what is painted here.
                SwitchToAccount(after);
                return;
            }

            if (rosterChanged)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"Saved accounts updated. {after.Count} saved.", false);
                return;
            }

            // Nothing changed. Silence after a button press reads as a dead
            // control, so say what is still true.
            _callbacks.ScreenReaderSpeak?.Invoke(
                string.IsNullOrWhiteSpace(after.Email)
                    ? "No account change."
                    : $"No account change. Still using {after.Email}.",
                false);
        }

        private void SwitchToAccount(SmartLinkAccountState state)
        {
            ClearOfflineRows();
            PaintRoster(state.Email ?? "");
            RefreshRadiosList();
            SyncPathAffordance();

            int cached;
            lock (_radiosLock) { cached = _radiosList.Count(r => r.FromAccountCache); }

            if (cached > 0)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"Last known radios for {state.Email}: {cached} listed. Refreshing.", false);
            }
            else
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"Switched to {state.Email}. No saved radio list for this account yet. Fetching.", false);
            }

            // A live fetch, always. The cached rows are a fast paint and nothing
            // more; the rule is that nothing connects from cache without a
            // refresh in flight, so the switch itself starts one. Force the
            // session cycle — the old account's session would answer with the
            // old account's radios.
            _remoteListLive = false;
            StartRemoteFlow(forceSessionCycle: true);
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
                radio = _radiosList.Find(r =>
                    r.Serial == _callbacks.AutoConnectSerial && r.IsLive);
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

            // Shift+Tab back into the list must land on the row the user left,
            // not on the bare ListBox where arrows have no anchor and Enter has
            // no target. Only redirect when focus stopped on the ListBox itself.
            if (ReferenceEquals(e.OriginalSource, RadiosBox)
                && RadiosBox.Items.Count > 0
                && !_redirectingFocusToRow)
            {
                _redirectingFocusToRow = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, RadiosBox))
                            FocusRadioList();
                    }
                    finally { _redirectingFocusToRow = false; }
                }), DispatcherPriority.Input);
            }

            UpdateRadioListAccessibility();
        }

        private void UpdateRadioListAccessibility()
        {
            int count = RadiosBox.Items.Count;
            int live = LiveCount();
            UpdateListAutomationName();

            if (count == 0)
            {
                // Do NOT speak during the settle window — discovery lands within
                // it more often than not, and the announcement would be
                // contradicted a half-second later (C2 item 6).
                if (!_anyLiveRadioSeen && !_remoteDiscoveryInFlight)
                    ScreenReaderOutput.Speak("No radios found yet. Searching.", VerbosityLevel.Critical, true);
                return;
            }

            var selected = RadiosBox.SelectedItem as RadioListItem;
            if (selected == null)
            {
                // Focus landed on the bare ListBox with nothing selected. The
                // redirect above is about to put focus on a real row and that
                // row will announce itself; "none selected, 0 of 3" here would
                // just be a wrong answer arriving first.
                return;
            }

            int idx = RadiosBox.SelectedIndex + 1;
            ScreenReaderOutput.Speak(
                live == 0
                    ? $"{selected.DisplayText}, {idx} of {count}, none online"
                    : $"{selected.DisplayText}, {idx} of {count}",
                VerbosityLevel.Terse, true);
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

            SyncPathAffordance();

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

            if (!radio.IsLive)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"{RowName(radio)} is offline. There is nothing to test until it turns up.", true);
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
            // Name the button that actually exists. "Click SmartLink" sent people
            // hunting for a control this dialog has never had.
            new MessageDialog
            {
                Title = "No Radios Found",
                Message = "No radios found on the local network. Press Remote, Alt+R, to look for radios through SmartLink.",
                Owner = this
            }.ShowDialog();
        }

        private void RigSelectorDialog_Closing(object? sender, CancelEventArgs e)
        {
            _autoConnectTimer.Stop();
            _callbacks.UnregisterRadioFound();
            _callbacks.UnregisterRadioRemoved?.Invoke();
        }
    }
}
