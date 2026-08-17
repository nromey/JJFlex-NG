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

        /// <summary>The radio's name as OBSERVED — its own broadcast
        /// nickname, refreshed by discovery. Display prefers
        /// <see cref="UserLabel"/> when the operator chose one.</summary>
        public string Name { get; set; } = "";

        /// <summary>The operator's chosen name for this radio (roster fact,
        /// set in per-radio settings). A choice: discovery never overwrites
        /// it, and it wins the row text — the operator typed it deliberately
        /// and recently (task #75).</summary>
        public string UserLabel { get; set; } = "";

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
        /// The operator's persisted, ordered chain of connection paths for
        /// this radio (<see cref="Radios.RadioConfig.PathChain"/>). Empty
        /// means no preference recorded — <see cref="EffectiveChain"/> then
        /// derives the order from availability, local first. This replaces
        /// the old session-only PreferRemotePath bool, which was erased on
        /// every discovery event and could not survive the selector closing,
        /// let alone the radio moving networks.
        /// </summary>
        public List<Radios.ConnectPathKind> PathChain { get; set; } = new();

        /// <summary>
        /// The chain a connect actually walks: the operator's stored chain
        /// when one exists, otherwise a derived default — local first when
        /// the radio's story is local (the historical behaviour, now an
        /// explicit default), SmartLink first when its story is remote.
        /// The derived chain always carries both paths, which is what makes
        /// automatic fallback ordinary list-walking instead of special-case
        /// logic. Only an operator-stored one-entry chain means "this path
        /// only".
        /// </summary>
        public List<Radios.ConnectPathKind> EffectiveChain
        {
            get
            {
                if (PathChain != null && PathChain.Count > 0)
                    return PathChain;
                bool remoteStory = WanAvailable ? !LanAvailable : (!LanAvailable && LastSeenRemote);
                return remoteStory
                    ? new List<Radios.ConnectPathKind> { Radios.ConnectPathKind.SmartLink, Radios.ConnectPathKind.Local }
                    : new List<Radios.ConnectPathKind> { Radios.ConnectPathKind.Local, Radios.ConnectPathKind.SmartLink };
            }
        }

        /// <summary>
        /// The path a connect would take right now: the first chain entry
        /// that is currently available, or the chain head when nothing is
        /// (an offline row's answer is the story of what would be tried
        /// first). One source of truth for the connect code, the
        /// announcement, and the row text.
        /// </summary>
        public Radios.ConnectPathKind ChosenPath
        {
            get
            {
                foreach (var p in EffectiveChain)
                {
                    if (p == Radios.ConnectPathKind.Local && LanAvailable) return p;
                    if (p == Radios.ConnectPathKind.SmartLink && WanAvailable) return p;
                }
                return EffectiveChain[0];
            }
        }

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

        /// <summary>The operator's chosen account for this radio (sticky,
        /// context-menu-set). Empty means automatic.</summary>
        public string PreferredAccount { get; set; } = "";

        /// <summary>Which account reaches this radio: the choice if made,
        /// otherwise the observation. Empty when neither exists — the
        /// preferred-account-for-new-connections covers that case at connect
        /// time.</summary>
        public string BoundAccount =>
            !string.IsNullOrWhiteSpace(PreferredAccount) ? PreferredAccount : LastSeenViaAccount;

        /// <summary>True when this row is bound to an account other than
        /// the one the selector is working with (or bound while no account is
        /// chosen). Set at roster paint. The one case where the operator
        /// genuinely needs the owner named — and the case where Enter switches
        /// accounts instead of hunting the radio on one that can never list
        /// it.</summary>
        public bool ForeignAccount { get; set; }

        /// <summary>Row came from the cached per-account radio list — a fast
        /// paint, honest about its provenance, never a connect authority.</summary>
        public bool FromAccountCache { get; set; }

        /// <summary>Set while a SmartLink pass is running, so a cached row can
        /// say "refreshing" instead of pretending to be current.</summary>
        public bool RefreshInFlight { get; set; }

        /// <summary>
        /// Whether connecting to this row travels the SmartLink path.
        /// Derived from <see cref="ChosenPath"/>, never stored — and unlike
        /// the old derivation, the operator's stored chain is consulted for
        /// EVERY row, not only dual-homed ones. That was the heart of
        /// symptom 1: the preference only existed on a branch most radios
        /// never reached.
        /// </summary>
        public bool IsRemote => ChosenPath == Radios.ConnectPathKind.SmartLink;

        /// <summary>Where this radio is, in words. Row text and the accessible
        /// name are the same string — what a sighted user reads and what a
        /// screen reader says must not diverge.</summary>
        public string WhereText
        {
            get
            {
                if (DualHomed)
                {
                    return IsRemote
                        ? "local network and SmartLink, using SmartLink"
                        : "local network and SmartLink, using local network";
                }
                if (LanAvailable) return "local network";
                if (WanAvailable) return "remote via SmartLink";

                // Roster row: say it is offline first, then how it was last seen.
                var age = string.IsNullOrEmpty(LastSeenText) ? "" : ", " + LastSeenText;

                // Another account's radio — the only case where naming the
                // owner is load-bearing. Before this branch, a foreign radio
                // read as an anonymous remote row while the operator's own
                // radios got their account named: inverted relative to need.
                // A set preference is a choice and reads as one; a bare
                // observation reads as what it is.
                if (ForeignAccount && !string.IsNullOrWhiteSpace(BoundAccount))
                {
                    return !string.IsNullOrWhiteSpace(PreferredAccount)
                        ? $"offline, preferred account {PreferredAccount}{age}"
                        : $"offline, registered to {LastSeenViaAccount}{age}";
                }

                if (FromAccountCache && !string.IsNullOrWhiteSpace(LastSeenViaAccount))
                {
                    var refreshing = RefreshInFlight ? ", refreshing" : "";
                    return $"offline, last known for {LastSeenViaAccount}{age}{refreshing}";
                }

                // 0.5c: LastSeenText carries its own "last seen" prefix, and
                // the old path wording repeated it ("last seen on the local
                // network, last seen 4 hours ago"). Fold path and age into one
                // sentence; an unknown age is omitted rather than spoken.
                var path = LastSeenRemote ? "remote via SmartLink" : "on the local network";
                var bareAge = LastSeenText.StartsWith("last seen ", StringComparison.OrdinalIgnoreCase)
                    ? LastSeenText.Substring("last seen ".Length)
                    : LastSeenText;
                return string.IsNullOrEmpty(bareAge) || bareAge == "unknown"
                    ? $"offline, last seen {path}"
                    : $"offline, last seen {path} {bareAge}";
            }
        }

        public string DisplayText
        {
            get
            {
                var fav = IsFavorite ? "Favorite, " : "";
                var autoConn = AutoConnect ? "[AutoConnect] " : "";
                var lbw = LowBW ? "[LowBW] " : "";
                // The operator's chosen label wins over the radio's broadcast
                // name — a choice outranks an observation (task #75).
                var shownName = !string.IsNullOrWhiteSpace(UserLabel) ? UserLabel : Name;
                var namePart = string.IsNullOrWhiteSpace(shownName) || shownName == "Unknown" ? "Unnamed" : shownName;
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

        /// <summary>
        /// Set the session-only SmartLink account override (the "Use Now"
        /// machinery). Phase 2 row activation uses this to switch to a row's
        /// bound account without touching the saved default — the sticky
        /// per-radio answer is PreferredAccount, never the app default.
        /// </summary>
        public Action<string>? SetSessionAccount { get; init; }
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
        /// True when the connect must travel the SmartLink path specifically —
        /// the operator forced it, or the chain chose SmartLink for a radio
        /// that is ALSO on the local network. The connect layer must not
        /// quietly substitute the LAN path when this is set.
        /// </summary>
        public bool SelectedPreferRemotePath { get; private set; }

        /// <summary>
        /// The chain entries remaining AFTER the chosen path — what the
        /// connect layer walks when the chosen path fails, announcing each
        /// move. Empty for a forced connect: force-remote is test equipment
        /// and a silent fallback would invalidate a hole-punch test by
        /// succeeding over the wrong path.
        /// </summary>
        public List<ConnectPathKind> SelectedFallbackPaths { get; private set; } = new();

        /// <summary>True when the operator forced this connect's path from
        /// the context menu — this path only, no fallback, prompt-if-needed.</summary>
        public bool SelectedPathForced { get; private set; }

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
                // Phase 0.5e (2026-08-10): Shift+F10 must open the row menu.
                // The automatic route never engaged — the keypress fell all
                // the way through to DefWindowProc, which popped the window's
                // SYSTEM menu instead (Noel's "system tree"). Whether WPF's
                // WM_CONTEXTMENU handling or the WinForms-pumped modal loop
                // eats it is moot from up here: PreviewKeyDown tunnels in
                // before either layer, so opening the menu deliberately makes
                // Shift+F10 and the Applications key one door by construction.
                // F10 arrives as Key.System with SystemKey carrying the real
                // key — checking e.Key == F10 alone silently never matches.
                else if (e.Key == System.Windows.Input.Key.Apps
                         || (e.Key == System.Windows.Input.Key.System
                             && e.SystemKey == System.Windows.Input.Key.F10
                             && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0))
                {
                    e.Handled = true;
                    OpenRadioContextMenuFromKeyboard();
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

                _localSettled = true;

                // A radio landed inside the window — its own arrival speech and
                // the auto-select line already told the user the interesting
                // part. The loaded-state line queues behind it (interrupt:
                // false), and its wording admits local never really finishes:
                // VITA discovery keeps listening the whole time the picker is
                // open, so "loaded" alone would quietly become a lie.
                if (_anyLiveRadioSeen)
                {
                    AnnounceLoadedState("Local loaded",
                        "Local connection list loaded, still listening");
                    return;
                }

                // "Press Remote" would be stale advice while remote-first
                // startup is already running Remote for the user.
                if (_remoteDiscoveryInFlight) return;

                AnnounceNothingLive();
            };

            // Phase 1: on-demand loaded-state query. An announcement is a
            // one-shot — if the screen reader was mid-sentence or the operator
            // alt-tabbed, it is gone, and they are back to inferring state
            // from list contents. F2 answers "which halves have loaded?" at
            // the moment the operator actually has the question.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.F2)
                {
                    e.Handled = true;
                    SpeakLoadedState();
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

            bool changed = false;
            lock (_radiosLock)
            {
                foreach (var k in known)
                {
                    var existing = _radiosList.FirstOrDefault(
                        r => string.Equals(r.Serial, k.Serial, StringComparison.OrdinalIgnoreCase));
                    bool foreign = !string.IsNullOrWhiteSpace(k.ResolvedAccount)
                        && !string.Equals(k.ResolvedAccount, accountEmail, StringComparison.OrdinalIgnoreCase);

                    if (existing != null)
                    {
                        // UNION, not exclusion: a row discovery already claimed
                        // still receives the roster's operator-owned facts —
                        // the chosen name, the favorite flag, the account
                        // choice, the path chain. Before this, every roster
                        // fact silently lost to a discovery fact for exactly
                        // the radios that were present (Root B).
                        existing.UserLabel = k.UserNickname;
                        existing.IsFavorite = k.IsFavorite;
                        existing.PreferredAccount = k.PreferredAccount;
                        existing.PathChain = k.PathChain ?? new List<ConnectPathKind>();
                        existing.ForeignAccount = foreign;
                        changed = true;
                        continue;
                    }

                    _radiosList.Add(new RadioListItem
                    {
                        Serial = k.Serial,
                        Name = k.Nickname,
                        UserLabel = k.UserNickname,
                        ModelName = k.Model,
                        IsFavorite = k.IsFavorite,
                        PathChain = k.PathChain ?? new List<ConnectPathKind>(),
                        LastSeenRemote = k.LastSeenRemote,
                        LastSeenText = KnownRadioRoster.DescribeAge(k.LastSeenUtc),
                        LastSeenViaAccount = k.LastSeenViaAccount,
                        PreferredAccount = k.PreferredAccount,
                        FromAccountCache = k.InAccountCache,
                        // Bound to some other account than the one in play (or
                        // bound while no account is chosen) — the row names
                        // its owner, and Enter switches instead of hunting.
                        // The operator's preference outranks the observation.
                        ForeignAccount = foreign,
                        AutoConnect = _callbacks.AutoConnectSerial == k.Serial && _callbacks.AutoConnectDesired,
                        LowBW = _callbacks.AutoConnectSerial == k.Serial && _callbacks.AutoConnectLowBW,
                    });
                    changed = true;
                }
            }

            if (changed) RefreshRadiosList();
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

        /// <summary>True once the initial local-discovery settle window has
        /// elapsed (or a live radio arrived sooner). Local discovery keeps
        /// listening for as long as the picker is open — this marks "the first
        /// wave is in," not "local is done."</summary>
        private bool _localSettled;

        /// <summary>
        /// Loaded-state announcements, Terse-gated, never interrupting: they
        /// queue behind whatever arrival or delta speech is already going out,
        /// which is the ordering requirement — a state line that clobbers the
        /// delta line would trade one lie for another.
        /// </summary>
        private static void AnnounceLoadedState(string terse, string chatty)
        {
            var text = Radios.ScreenReaderOutput.CurrentVerbosity == Radios.VerbosityLevel.Chatty
                ? chatty : terse;
            Radios.ScreenReaderOutput.Speak(text, Radios.VerbosityLevel.Terse, false);
        }

        /// <summary>
        /// F2: speak which halves of the roster have loaded, on demand.
        /// User-initiated, so it interrupts and bypasses the verbosity gate —
        /// a deliberate question deserves an answer even with speech turned
        /// down.
        /// </summary>
        private void SpeakLoadedState()
        {
            string local = _localSettled
                ? "Local loaded, still listening."
                : "Local still loading.";
            string remote = _remoteDiscoveryInFlight
                ? "Remote loading."
                : _remoteListLive ? "Remote loaded." : "Remote not loaded.";
            int live = LiveCount();
            string count = $" {live} radio{(live == 1 ? "" : "s")} online.";
            _callbacks.ScreenReaderSpeak?.Invoke($"{local} {remote}{count}", true);
        }

        private void AnnounceNothingLive()
        {
            int known;
            lock (_radiosLock) { known = _radiosList.Count; }

            if (known == 0)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    "Radio list, empty. No radios found yet. For SmartLink radios, press Shift F10 and choose Show Remote Radios.", false);
                return;
            }

            _callbacks.ScreenReaderSpeak?.Invoke(
                $"No radios online yet. {known} known radio{(known == 1 ? "" : "s")} listed, all offline. " +
                "Press Enter on a radio to connect — JJ Flexible looks for it where its connection path says to.", false);
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
                    // Note what is deliberately NOT touched: UserLabel,
                    // IsFavorite, PreferredAccount, PathChain — the
                    // operator-owned facts a discovery event knows nothing
                    // about. The old code cleared the path preference here
                    // whenever the radio was not dual-homed, which erased the
                    // choice for exactly the radios it mattered most for.
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

        /// <summary>Serial of the radio a connect intention is pending for
        /// while a SmartLink pass runs. The double-Enter fix: Enter on a
        /// radio that needs a SmartLink look no longer drops the connect on
        /// the floor after authenticating — the intention is carried and the
        /// walk resumes when the pass lands.</summary>
        private string? _pendingConnectSerial;

        /// <summary>The forced path of the pending intention, or null for a
        /// normal chain walk.</summary>
        private ConnectPathKind? _pendingConnectForced;

        private void DoConnect(RadioListItem radio) => DoConnect(radio, null);

        /// <summary>
        /// Connect to a radio by walking its path chain — or, when
        /// <paramref name="forcedPath"/> is set, by that path ONLY, with no
        /// fallback: force-remote is test equipment (the hole-punch test
        /// instrument and the rescue path), and a silent substitution would
        /// invalidate a punch test by succeeding over the wrong path.
        /// Never a dead end and never a connect from cache: when the chain
        /// wants SmartLink and this session has not looked yet, the walk
        /// opens a real SmartLink pass, carries the connect intention, and
        /// resumes when the list lands — one Enter, speaking at each stage.
        /// </summary>
        private void DoConnect(RadioListItem radio, ConnectPathKind? forcedPath)
        {
            var radioName = RowName(radio);

            // A row bound to another account never hunts on the current one —
            // that pass is a thirty-second authentication grind toward a
            // guaranteed empty answer (the 2026-08-09 wrong-account hunt).
            // The account is a property of the radio, so activation SWITCHES
            // to the row's account, announced before any session opens, and
            // runs the standard forced refresh under it. Session-only switch:
            // the saved default is untouched.
            if (!radio.IsLive && radio.ForeignAccount && !string.IsNullOrWhiteSpace(radio.BoundAccount))
            {
                var target = Radios.FlexBase.SharedAccountManager.GetAccountByEmail(radio.BoundAccount);
                if (target == null)
                {
                    _callbacks.ScreenReaderSpeak?.Invoke(
                        $"{radioName} is registered to {radio.BoundAccount}, and that account is not saved " +
                        "on this computer. Sign in to it from Manage SmartLink Accounts to reach this radio.", true);
                    return;
                }

                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"{radioName} uses account {target.Email}. Switching to {target.Email} and refreshing the radio list.", true);
                _callbacks.SetSessionAccount?.Invoke(target.Email);
                UpdateAccountAffordances();
                SwitchToAccount(CurrentAccountState());
                return;
            }

            if (forcedPath == ConnectPathKind.Local)
            {
                if (radio.LanAvailable)
                {
                    CompleteConnect(radio, ConnectPathKind.Local, forced: true,
                        fallbacks: new List<ConnectPathKind>());
                }
                else
                {
                    _callbacks.ScreenReaderSpeak?.Invoke(
                        $"{radioName} is not on the local network right now, and connect locally does not fall back. " +
                        "It may be powered off or on a different network.", true);
                }
                return;
            }

            if (forcedPath == ConnectPathKind.SmartLink)
            {
                if (radio.WanAvailable)
                {
                    CompleteConnect(radio, ConnectPathKind.SmartLink, forced: true,
                        fallbacks: new List<ConnectPathKind>());
                    return;
                }
                if (TryStartRemoteLookFor(radio, forced: ConnectPathKind.SmartLink, radioName)) return;

                var acct = string.IsNullOrWhiteSpace(radio.LastSeenViaAccount)
                    ? "this account" : radio.LastSeenViaAccount;
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"{radioName} is not in {acct}'s radio list, and connect over SmartLink does not fall back to local. " +
                    "It may be powered off, or registered to a different account.", true);
                return;
            }

            // The ordinary case: walk the radio's chain, first entry first.
            //
            // The skipped rungs are collected but NOT spoken on success. The
            // connect announcement names the path it took — "Connecting to X
            // over SmartLink" already says local did not happen — so narrating
            // the itinerary as well is a tax on the common case. For a radio
            // that normally lives remote, "not on the local network" announces
            // the EXPECTED state as news, on every single connect.
            //
            // The itinerary is only interesting when you arrive nowhere. There
            // the reasons are the whole content, so the exhaustion message below
            // spends them.
            var chain = radio.EffectiveChain;
            var notes = new List<string>();
            for (int i = 0; i < chain.Count; i++)
            {
                var path = chain[i];
                if (path == ConnectPathKind.Local)
                {
                    if (radio.LanAvailable)
                    {
                        CompleteConnect(radio, path, forced: false,
                            fallbacks: chain.Skip(i + 1).ToList());
                        return;
                    }
                    notes.Add("is not on the local network");
                    continue;
                }

                // SmartLink rung.
                if (radio.WanAvailable)
                {
                    CompleteConnect(radio, path, forced: false,
                        fallbacks: chain.Skip(i + 1).ToList());
                    return;
                }
                if (TryStartRemoteLookFor(radio, forced: null, radioName)) return;
                var acctName = string.IsNullOrWhiteSpace(radio.LastSeenViaAccount)
                    ? CurrentAccountEmail() : radio.LastSeenViaAccount;
                notes.Add(string.IsNullOrWhiteSpace(acctName)
                    ? "is not in the SmartLink radio list"
                    : $"is not in {acctName}'s radio list");
            }

            // Chain exhausted with nothing reachable. Notes are subject-less
            // predicates so the radio is named once at the front rather than
            // once per rung.
            _callbacks.ScreenReaderSpeak?.Invoke(
                notes.Count > 0
                    ? $"{radioName} {string.Join(", and ", notes)}. It may be powered off."
                    : $"{radioName} is not reachable right now. It may be powered off.",
                true);
        }

        /// <summary>
        /// Put a real SmartLink pass in flight for this radio (or note that
        /// one already is), carrying the connect intention so the walk
        /// resumes when the list lands. Returns false when a pass has
        /// already completed this session — the caller then knows the
        /// absence is an answer, not an unasked question.
        /// </summary>
        private bool TryStartRemoteLookFor(RadioListItem radio, ConnectPathKind? forced, string radioName)
        {
            if (_remoteDiscoveryInFlight)
            {
                _pendingConnectSerial = radio.Serial;
                _pendingConnectForced = forced;
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"Still looking for {radioName} over SmartLink. It will connect when found.", true);
                return true;
            }
            if (!_remoteListLive)
            {
                _pendingConnectSerial = radio.Serial;
                _pendingConnectForced = forced;
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"Signing in to SmartLink to look for {radioName}.", true);
                StartRemoteFlow();
                return true;
            }
            return false;
        }

        /// <summary>
        /// The chain (or the operator's force) has picked the path — announce
        /// it, stamp the dialog's outputs, and close. Spoken before the
        /// dialog closes and the session work starts, never after it
        /// succeeds. The account is named on EVERY SmartLink connect, not
        /// only cross-account ones — symmetry teaches the pattern (TX-safety:
        /// a unified list puts Don's production 6300 one arrow key from
        /// Noel's 8600).
        /// </summary>
        private void CompleteConnect(RadioListItem radio, ConnectPathKind path, bool forced,
            List<ConnectPathKind> fallbacks)
        {
            var radioName = RowName(radio);
            bool remote = path == ConnectPathKind.SmartLink;
            var acctEmail = CurrentAccountEmail();
            var via = remote
                ? string.IsNullOrWhiteSpace(acctEmail)
                    ? "over SmartLink" : $"over SmartLink as {acctEmail}"
                : "on the local network";
            _callbacks.ScreenReaderSpeak?.Invoke($"Connecting to {radioName} {via}", true);
            // AS prosign (wait / standing by) alongside the "Connecting to X" speech.
            // Pair with BT which fires at connect-ready in MainWindow.PowerOn.
            if (ScreenReaderOutput.CwNotificationsEnabled) _ = ScreenReaderOutput.PlayCwAS?.Invoke();

            SelectedRigData = radio.RigData;
            SelectedSerial = radio.Serial;
            SelectedLowBW = radio.LowBW;
            SelectedIsRemote = remote;
            SelectedPreferRemotePath = remote && (forced || radio.LanAvailable);
            SelectedPathForced = forced;
            SelectedFallbackPaths = forced ? new List<ConnectPathKind>() : fallbacks;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// A SmartLink pass finished while a connect intention was pending.
        /// Resume the walk: connect if the radio turned up, report honestly
        /// if it did not. Never re-queues a pass on failure — that would
        /// loop.
        /// </summary>
        private void ResumePendingConnect(bool success)
        {
            var serial = _pendingConnectSerial;
            var forced = _pendingConnectForced;
            _pendingConnectSerial = null;
            _pendingConnectForced = null;
            if (string.IsNullOrEmpty(serial)) return;

            // The pass can land after the operator cancelled the dialog;
            // setting DialogResult on a closed window throws.
            if (!IsLoaded) return;

            RadioListItem? row;
            lock (_radiosLock)
            {
                row = _radiosList.FirstOrDefault(r =>
                    string.Equals(r.Serial, serial, StringComparison.OrdinalIgnoreCase));
            }
            if (row == null) return;

            if (!success)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    $"Could not reach SmartLink to look for {RowName(row)}.", true);
                return;
            }

            // Re-enter the walk. With the list now live, every rung answers
            // immediately: found means connect, absent means the walk moves
            // on or reports — no second Enter required.
            DoConnect(row, forced);
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
        // Connection path preference (persisted per radio)
        // ------------------------------------------------------------------

        private const string PathAutomatic = "Automatic, local first";
        private const string PathLocalFirst = "Local network first";
        private const string PathSmartLinkFirst = "SmartLink first";

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
                : $"{r.Serial}|{string.Join(",", r.PathChain)}";
        }

        private string _pathAffordanceKey = "";

        /// <summary>
        /// Rebuild the path control for the current selection. It edits the
        /// PERSISTED per-radio preference now — the ordered chain a connect
        /// walks — and it is enabled for every known radio, not only
        /// dual-homed ones: the preference matters most for exactly the
        /// radios the app believes have one home (symptom 1: Don's radio,
        /// believed local, with no way to say "SmartLink first"). One store,
        /// two doors: this combo and the context menu's Default Connection
        /// Path submenu write the same chain.
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

                PathCombo.Items.Add(PathAutomatic);
                PathCombo.Items.Add(PathLocalFirst);
                PathCombo.Items.Add(PathSmartLinkFirst);
                PathCombo.SelectedIndex =
                    radio.PathChain.Count == 0 ? 0
                    : radio.PathChain[0] == ConnectPathKind.SmartLink ? 2
                    : 1;
                PathCombo.IsEnabled = true;
                System.Windows.Automation.AutomationProperties.SetName(
                    PathCombo,
                    "Connection path for this radio. Saved with the radio: Connect tries the chosen path first " +
                    "and falls back to the other, saying so. Automatic tries the local network first.");
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
            if (radio == null) return;

            var chain = PathCombo.SelectedIndex switch
            {
                1 => new List<ConnectPathKind> { ConnectPathKind.Local, ConnectPathKind.SmartLink },
                2 => new List<ConnectPathKind> { ConnectPathKind.SmartLink, ConnectPathKind.Local },
                _ => new List<ConnectPathKind>(),
            };
            SetPathChainForRow(radio, chain);
        }

        /// <summary>
        /// Persist a path-chain choice for one row and announce honestly —
        /// a spoken success over a declined save is a promise the next
        /// launch breaks. Shared by the combo and the context menu.
        /// </summary>
        private void SetPathChainForRow(RadioListItem radio, List<ConnectPathKind> chain)
        {
            bool same = radio.PathChain.SequenceEqual(chain);
            if (same) return;

            // The operator's choice is taken whether or not the disk agrees.
            // Refusing an intent because a file was locked hands our problem to
            // them, and "nothing was changed" reads as an error they caused and
            // could fix — which they cannot. So: apply it, then be honest about
            // how long it will last.
            bool persisted = KnownRadioRoster.SetPathChain(radio.Serial, chain);
            radio.PathChain = chain;
            // The combo already shows the new choice; re-syncing it from the
            // list refresh below would rip its items out from under the user's
            // focus for no change they can perceive.
            _pathAffordanceKey = PathKey();

            var rowName = RowName(radio);
            string speech = chain.Count == 0
                ? $"{rowName} connection path is automatic: local network first, then SmartLink."
                : chain[0] == ConnectPathKind.SmartLink
                    ? $"{rowName} will connect over SmartLink first, falling back to the local network."
                    : $"{rowName} will connect over the local network first, falling back to SmartLink.";
            // Only mention persistence when it failed. Saying "and it is saved"
            // on every success is noise; saying nothing when it did NOT save is
            // the lying-receipt bug. The reason lives in the trace file, which
            // is where a support conversation can actually use it.
            if (!persisted)
                speech += " This is in effect now, but it could not be written to disk,"
                        + " so it may not be here next time you start. Your trace file has the reason.";
            _callbacks.ScreenReaderSpeak?.Invoke(speech, true);
            RefreshRadiosList();
            ReselectBySerial(radio.Serial);
        }

        private static string RowName(RadioListItem r) =>
            !string.IsNullOrWhiteSpace(r.UserLabel) ? r.UserLabel
            : !string.IsNullOrWhiteSpace(r.Name) ? r.Name
            : "This radio";

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
            PrepareRadioContextMenu();
        }

        /// <summary>
        /// Per-open menu prep (favorite item retitle/enable). Shared between
        /// the automatic route (ContextMenuOpening, mouse right-click) and the
        /// keyboard route — a manual IsOpen = true does NOT raise
        /// ContextMenuOpening, so prep that lives only in that handler
        /// silently goes stale for keyboard users.
        /// </summary>
        private void PrepareRadioContextMenu()
        {
            var radio = GetSelectedRadio();
            bool fav = radio?.IsFavorite == true;
            FavoriteMenuItem.Header = fav ? "Remove from Favorites" : "Add to Favorites";
            System.Windows.Automation.AutomationProperties.SetName(FavoriteMenuItem,
                fav ? "Remove selected radio from favorites" : "Add selected radio to favorites");
            FavoriteMenuItem.IsEnabled = radio != null;
            ConnectLocalMenuItem.IsEnabled = radio != null;
            ConnectRemoteMenuItem.IsEnabled = radio != null;
            // The list item wears its state: before a successful pass it
            // shows the radios, after one it refreshes them (the server
            // sends its list once per TLS session, so a repeat is a
            // session-cycling refresh).
            RemoteListMenuItem.Header = _remoteListLive ? "Refresh Remote List" : "Show Remote Radios";
            System.Windows.Automation.AutomationProperties.SetName(RemoteListMenuItem,
                _remoteListLive
                    ? "Refresh Remote List. Reconnects to SmartLink and looks again, picking up radios that came online since."
                    : "Show this account's SmartLink radios");
            BuildPreferredAccountSubmenu(radio);
            BuildDefaultPathSubmenu(radio);
        }

        /// <summary>
        /// The per-row Default Connection Path submenu — door two to the same
        /// per-radio chain the path combo edits. Checkable so the current
        /// choice announces itself.
        /// </summary>
        private void BuildDefaultPathSubmenu(RadioListItem? radio)
        {
            DefaultPathMenuItem.Items.Clear();
            DefaultPathMenuItem.IsEnabled = radio != null;
            if (radio == null) return;

            void AddChoice(string header, string accessible, List<ConnectPathKind> chain, bool isChecked)
            {
                var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
                System.Windows.Automation.AutomationProperties.SetName(item, accessible);
                item.Click += (_, _) => SetPathChainForRow(radio, chain);
                DefaultPathMenuItem.Items.Add(item);
            }

            AddChoice("Automatic",
                "Automatic. Try the local network first, then SmartLink.",
                new List<ConnectPathKind>(),
                radio.PathChain.Count == 0);
            AddChoice("Local Network First",
                "Local network first, falling back to SmartLink.",
                new List<ConnectPathKind> { ConnectPathKind.Local, ConnectPathKind.SmartLink },
                radio.PathChain.Count > 0 && radio.PathChain[0] == ConnectPathKind.Local);
            AddChoice("SmartLink First",
                "SmartLink first, falling back to the local network.",
                new List<ConnectPathKind> { ConnectPathKind.SmartLink, ConnectPathKind.Local },
                radio.PathChain.Count > 0 && radio.PathChain[0] == ConnectPathKind.SmartLink);
        }

        private void ConnectLocalMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }
            DoConnect(radio, ConnectPathKind.Local);
        }

        private void ConnectRemoteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }
            DoConnect(radio, ConnectPathKind.SmartLink);
        }

        private void RemoteListMenuItem_Click(object sender, RoutedEventArgs e)
        {
            StartRemoteFlow();
        }

        /// <summary>
        /// Phase 2, door one: the per-row Preferred Account submenu. Rebuilt
        /// on every open because the saved-account roster can change while the
        /// selector is up. Checkable items so the current binding announces
        /// itself; "Automatic" clears the preference and resolution falls back
        /// to the observation, then the default.
        /// </summary>
        private void BuildPreferredAccountSubmenu(RadioListItem? radio)
        {
            PreferredAccountMenuItem.Items.Clear();
            PreferredAccountMenuItem.IsEnabled = radio != null;
            if (radio == null) return;

            var auto = new MenuItem
            {
                Header = "Automatic",
                IsCheckable = true,
                IsChecked = string.IsNullOrWhiteSpace(radio.PreferredAccount),
            };
            System.Windows.Automation.AutomationProperties.SetName(auto,
                "Automatic. Use the account that last listed this radio, or the default account.");
            auto.Click += (_, _) => SetPreferredAccountForRow(radio, "");
            PreferredAccountMenuItem.Items.Add(auto);

            foreach (var acct in Radios.FlexBase.SharedAccountManager.Accounts)
            {
                var email = acct.Email;
                if (string.IsNullOrWhiteSpace(email)) continue;
                var label = string.IsNullOrWhiteSpace(acct.FriendlyName)
                            || string.Equals(acct.FriendlyName, email, StringComparison.OrdinalIgnoreCase)
                    ? email
                    : $"{acct.FriendlyName} ({email})";
                var item = new MenuItem
                {
                    // "_" in a header is an access-key marker; emails keep theirs.
                    Header = label.Replace("_", "__"),
                    IsCheckable = true,
                    IsChecked = string.Equals(radio.PreferredAccount, email, StringComparison.OrdinalIgnoreCase),
                };
                System.Windows.Automation.AutomationProperties.SetName(item, label);
                item.Click += (_, _) => SetPreferredAccountForRow(radio, email);
                PreferredAccountMenuItem.Items.Add(item);
            }
        }

        /// <summary>
        /// Persist a preferred-account choice for one row. A choice, not an
        /// observation: sightings write LastSeenViaAccount and never touch
        /// this. Announces failure honestly — a spoken success over a
        /// declined save is a promise the next launch breaks.
        /// </summary>
        private void SetPreferredAccountForRow(RadioListItem radio, string email)
        {
            if (!KnownRadioRoster.SetPreferredAccount(radio.Serial, email))
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    "Could not save the preferred account. It would not survive a restart, so nothing was changed.",
                    true);
                return;
            }

            radio.PreferredAccount = email;
            var current = CurrentAccountEmail();
            radio.ForeignAccount = !string.IsNullOrWhiteSpace(radio.BoundAccount)
                && !string.Equals(radio.BoundAccount, current, StringComparison.OrdinalIgnoreCase);

            var rowName = RowName(radio);
            _callbacks.ScreenReaderSpeak?.Invoke(
                string.IsNullOrWhiteSpace(email)
                    ? $"{rowName} preferred account cleared. Automatic."
                    : $"{rowName} will connect as {email}.",
                true);
            RefreshRadiosList();
            ReselectBySerial(radio.Serial);
            if (RadiosBox.IsKeyboardFocusWithin) FocusRadioList();
        }

        /// <summary>
        /// Phase 0.5e: open the row context menu from Shift+F10 or the
        /// Applications key, anchored to the selected row so it appears where
        /// a sighted operator expects and where a magnifier user is looking.
        /// Escape closes it natively; WPF announces it as a menu.
        /// </summary>
        private void OpenRadioContextMenuFromKeyboard()
        {
            var menu = RadiosBox.ContextMenu;
            if (menu == null) return;

            PrepareRadioContextMenu();

            var anchor = RadiosBox.SelectedIndex >= 0
                ? RadiosBox.ItemContainerGenerator.ContainerFromIndex(RadiosBox.SelectedIndex) as System.Windows.UIElement
                : null;
            menu.PlacementTarget = anchor ?? RadiosBox;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
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
        /// From then on the context menu's remote-list item is "Refresh
        /// Remote List": with a live session, re-running discovery can never
        /// yield anything new — the server sends the radio list once per TLS
        /// session — so the only meaningful repeat action is a
        /// session-cycling refresh. No timer: "listed" is a state, not a
        /// five-second window (Noel, 2026-08-06).
        /// </summary>
        private bool _remoteListLive;

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
                    // The remote list is a state, not an event: the context
                    // menu's list item reads this to retitle itself.
                    _remoteListLive = true;
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
                    // Remote is a discrete event (the server sends its list
                    // once per TLS session), so "loaded" is true on every
                    // successful pass. Before AnnounceListDelta so the state
                    // line leads and the delta follows, both queued.
                    if (success)
                    {
                        AnnounceLoadedState("Remote loaded",
                            "Remote connection list loaded");
                    }
                    AnnounceListDelta(liveBefore, success);

                    // A connect intention that started this pass resumes
                    // here — the double-Enter fix: the walk continues into
                    // the connect the first Enter asked for. It may close
                    // the dialog, so focus only when it did not.
                    bool hadPending = _pendingConnectSerial != null;
                    if (hadPending) ResumePendingConnect(success);
                    if (DialogResult != true) FocusRadioList();
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

            // Show settings dialog. Track C: the commit runs on every OK or
            // Apply via the callback — Apply-and-stay saves for real, instead
            // of the old read-the-refs-after-close pattern that only OK could
            // satisfy.
            AutoConnectSettingsDialog.ShowSettingsDialog(this, radio.Name,
                radio.AutoConnect, radio.LowBW,
                (newAutoConnect, newLowBW) =>
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
                });
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
            // Name the control that actually exists. "Click SmartLink" sent
            // people hunting for a control this dialog has never had — and
            // the Remote button is gone (Connect opens SmartLink itself when
            // a radio's path chain asks for it).
            new MessageDialog
            {
                Title = "No Radios Found",
                Message = "No radios found on the local network yet. To look for radios through SmartLink, " +
                          "press Shift F10 on the radio list and choose Show Remote Radios.",
                Owner = this
            }.ShowDialog();
        }

        private void RigSelectorDialog_Closing(object? sender, CancelEventArgs e)
        {
            _autoConnectTimer.Stop();
            _pendingConnectSerial = null;
            _pendingConnectForced = null;
            _callbacks.UnregisterRadioFound();
            _callbacks.UnregisterRadioRemoved?.Invoke();
        }
    }
}
