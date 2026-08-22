using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JJTrace;
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
        /// What this radio's connection history suggests, or null when it
        /// suggests nothing (task #79). Filled in once per row from
        /// <see cref="Radios.ConnectPathPolicy.LearnForRadioUsingSettings"/> — read from
        /// disk when the row is built, never per list refresh.
        ///
        /// <para>A PREFILL and nothing more. <see cref="EffectiveChain"/>
        /// consults it only when <see cref="PathChain"/> is empty, so a stored
        /// explicit choice is never influenced, reordered or overwritten by
        /// it. That precedence is enforced in one place, and tested there:
        /// see ConnectPathPolicy.Resolve.</para>
        /// </summary>
        public Radios.ConnectPathKind? LearnedPath { get; set; }

        /// <summary>True when the chain this row would walk was ordered by the
        /// learned trend rather than by a stored choice or the plain default.
        /// Drives the "learned" wording on the path affordance — a prefill the
        /// operator cannot see is a prefill they cannot disagree with.</summary>
        public bool ChainIsLearned =>
            (PathChain == null || PathChain.Count == 0) && LearnedPath.HasValue;

        /// <summary>
        /// The chain a connect actually walks. The precedence — stored choice,
        /// then learned trend, then derived default — lives in
        /// <see cref="Radios.ConnectPathPolicy.Resolve"/> so it is testable
        /// away from WPF; this property is only the row's view of it.
        /// </summary>
        public List<Radios.ConnectPathKind> EffectiveChain =>
            Radios.ConnectPathPolicy.Resolve(
                PathChain, LearnedPath, LanAvailable, WanAvailable, LastSeenRemote);

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

        /// <summary>
        /// True once the first local discovery pass has settled. Until then a
        /// row that has not answered is UNKNOWN, not offline: UDP discovery
        /// takes a second or two to warm up, so the selector opens with every
        /// roster row looking absent. Stamped from the dialog's _localSettled
        /// on each refresh.
        /// </summary>
        public bool DiscoverySettled { get; set; }

        /// <summary>Where this radio is, in words. Row text and the accessible
        /// name are the same string — what a sighted user reads and what a
        /// screen reader says must not diverge.</summary>
        public string WhereText
        {
            get
            {
                if (DualHomed)
                {
                    return (IsRemote ? Lexicon.Get("connect.row.dual_using_smartlink") : Lexicon.Get("connect.row.dual_using_local"));
                }
                if (LanAvailable) return Lexicon.Get("connect.row.local");
                if (WanAvailable) return Lexicon.Get("connect.row.remote");

                // Nothing has answered YET is not the same as nothing is there.
                // Discovery needs a second or two, so until it settles this row
                // says so instead of asserting an absence it cannot know about.
                // Noel, 2026-08-17, landing on his own live radio and hearing it
                // called offline: "which isn't true."
                if (!DiscoverySettled) return Lexicon.Get("connect.row.checking");

                // Roster row: say it is offline first, then how it was last seen.
                var age = string.IsNullOrEmpty(LastSeenText)
                    ? ""
                    : Lexicon.Get("connect.row.age_suffix", ("lastSeenText", LastSeenText));

                // Another account's radio — the only case where naming the
                // owner is load-bearing. Before this branch, a foreign radio
                // read as an anonymous remote row while the operator's own
                // radios got their account named: inverted relative to need.
                // A set preference is a choice and reads as one; a bare
                // observation reads as what it is.
                if (ForeignAccount && !string.IsNullOrWhiteSpace(BoundAccount))
                {
                    return !string.IsNullOrWhiteSpace(PreferredAccount)
                        ? Lexicon.Get("connect.row.offline_preferred_account",
                            ("preferredAccount", PreferredAccount), ("age", age))
                        : Lexicon.Get("connect.row.offline_registered_to",
                            ("lastSeenViaAccount", LastSeenViaAccount), ("age", age));
                }

                if (FromAccountCache && !string.IsNullOrWhiteSpace(LastSeenViaAccount))
                {
                    var refreshing = RefreshInFlight ? Lexicon.Get("connect.row.refreshing_suffix") : "";
                    return Lexicon.Get("connect.row.offline_last_known_for",
                        ("lastSeenViaAccount", LastSeenViaAccount), ("age", age), ("refreshing", refreshing));
                }

                // 0.5c: LastSeenText carries its own "last seen" prefix, and
                // the old path wording repeated it ("last seen on the local
                // network, last seen 4 hours ago"). Fold path and age into one
                // sentence; an unknown age is omitted rather than spoken.
                var path = (LastSeenRemote ? Lexicon.Get("connect.row.remote") : Lexicon.Get("connect.row.last_seen_local"));
                var bareAge = LastSeenText.StartsWith("last seen ", StringComparison.OrdinalIgnoreCase)
                    ? LastSeenText.Substring("last seen ".Length)
                    : LastSeenText;
                return string.IsNullOrEmpty(bareAge) || bareAge == "unknown"
                    ? Lexicon.Get("connect.row.offline_last_seen", ("path", path))
                    : Lexicon.Get("connect.row.offline_last_seen_age",
                        ("path", path), ("bareAge", bareAge));
            }
        }

        /// <summary>
        /// A name field carries no usable name. Blank and the literal sentinel
        /// "Unknown" mean the same thing — the radio reports "Unknown" when it
        /// has none. Shared so every reader agrees: RowName did NOT know about
        /// the sentinel while DisplayText did, so a connect announced
        /// "Connecting to Unknown" for a radio the operator picked by name.
        /// </summary>
        public static bool NameIsMissing(string s) =>
            string.IsNullOrWhiteSpace(s)
            || string.Equals(s, "Unknown", StringComparison.OrdinalIgnoreCase);

        public string DisplayText
        {
            get
            {
                var fav = IsFavorite ? Lexicon.Get("connect.row.favorite_prefix") : "";
                var autoConn = AutoConnect ? Lexicon.Get("connect.row.autoconnect_marker") : "";
                var lbw = LowBW ? Lexicon.Get("connect.row.lowbw_marker") : "";
                // The operator's chosen label wins over the radio's broadcast
                // name — a choice outranks an observation (task #75).
                var shownName = !string.IsNullOrWhiteSpace(UserLabel) ? UserLabel : Name;
                var namePart = NameIsMissing(shownName) ? Lexicon.Get("connect.row.unnamed") : shownName;
                var modelPart = string.IsNullOrWhiteSpace(ModelName) || ModelName == "Unknown"
                    ? Lexicon.Get("connect.row.unknown_model") : ModelName;
                // Source, not serial. Two radios that differ only by where they
                // are were indistinguishable by ear — an unnamed local rig and a
                // remote one read as near-identical rows of digits. The serial is
                // rarely what the user needs and never what they navigate by.
                return Lexicon.Get("connect.row.display",
                    ("fav", fav), ("autoConn", autoConn), ("lbw", lbw),
                    ("namePart", namePart), ("modelPart", modelPart), ("whereText", WhereText));
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

        /// <summary>
        /// Re-raise RadioFound for radios discovery already found before this
        /// dialog existed. Optional: an older host that starts discovery only
        /// when the dialog opens has no backlog to replay.
        /// </summary>
        public Action? ReplayDiscoveredRadios { get; init; }

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
        // Lexicon-backed, so no longer const: a const must be a compile-time
        // literal and Lexicon.Get is resolved at run time.
        private static string MustSelect => Lexicon.Get("connect.selector.must_select");

        private static string SelectRadioTitle => Lexicon.Get("connect.selector.select_radio_title");

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

            // Restore the Network Identity panel to however the operator left
            // it. Collapsed by default - see the XAML for why.
            try
            {
                IdentityExpander.IsExpanded = AudioOutputConfig.GetNetworkIdentityExpanded();
                IdentityExpander.Expanded += (_, _) =>
                    AudioOutputConfig.SetNetworkIdentityExpanded(true);
                IdentityExpander.Collapsed += (_, _) =>
                    AudioOutputConfig.SetNetworkIdentityExpanded(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"RigSelector: identity expander state: {ex.Message}");
            }

            // Register for radio discovery events
            _callbacks.RegisterRadioFound(OnRadioFound);
            _callbacks.RegisterRadioRemoved?.Invoke(OnRadioRemoved);

            // Collect the backlog FIRST. Discovery now runs before this dialog
            // is created, so that the operator meets a settled list instead of
            // listening to one assemble itself - which means we subscribed too
            // late to have heard about anything found during that window.
            //
            // Without this the rows would sit at "checking" until the radios
            // happened to re-announce, and the churn we just moved out of the
            // way would walk straight back in.
            _callbacks.ReplayDiscoveredRadios?.Invoke();

            // Keep discovery running while the picker is open - a radio powered
            // on now should still appear.
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
                // Task #98: Delete removes the selected radio. THE key, not
                // just a menu item — the roster held five entries including two
                // pieces of test junk, and the only way to be rid of any of
                // them was to hand-edit AppData, which is not something a blind
                // operator should ever be asked to do. Delete is where every
                // list in Windows puts this, so it needs no menu hunting.
                //
                // Safe as a bare keypress because it opens a confirmation whose
                // default scope deletes nothing, and because an accidental
                // removal of a real radio is self-healing: a radio that is
                // online gets re-discovered, settings and all.
                else if (e.Key == System.Windows.Input.Key.Delete
                         && GetSelectedRadio() is RadioListItem toRemove)
                {
                    e.Handled = true;
                    RemoveRadio(toRemove);
                }
            };

            // Shift+Tab out of the radio list, handled at the WINDOW.
            //
            // The list is TabNavigation="Once" — a navigation group — so WPF
            // resolves Previous inside it, finds nothing before the list's
            // contents, and stops instead of escaping to the window's Cycle.
            // Focusing an item rather than the container did not help; nor did
            // routing MoveFocus(Last) from the ListBox's own PreviewKeyDown,
            // which is why this lives on the window, where the key cannot be
            // swallowed by the group first.
            //
            // The destination is NAMED rather than navigated to. MoveFocus
            // depends on the same tab-order resolution that is failing, so
            // asking for "Last" is asking the broken thing for an answer.
            // IdentityExpander is the final tab stop by construction — the last
            // element in the Grid.
            //
            // *** THIS WAS DELETED ONCE. DO NOT DELETE IT AGAIN. ***
            // Commit 808127d8 (2026-08-18) removed it, asserting "Shift+Tab
            // from the radio list worked natively" and that Cycle on the window
            // already wraps at both ends. That assertion was WRONG, and it was
            // never tested — it was reasoning from the framework's documented
            // behaviour rather than from a keypress. Noel confirmed at the
            // keyboard on 2026-08-19: with this handler gone, Shift+Tab from
            // the list does nothing and says nothing, while forward Tab works.
            // Cycle does wrap at the window level; it never gets the chance,
            // because the navigation group resolves the key first and stops.
            //
            // Restored 2026-08-19 as attempt five, and the only one grounded in
            // an observation rather than a theory. If you are about to remove
            // this because the framework "should" handle it: press the key
            // first. See task #89.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Tab) return;
                if ((System.Windows.Input.Keyboard.Modifiers
                     & System.Windows.Input.ModifierKeys.Shift) == 0) return;
                if (!RadiosBox.IsKeyboardFocusWithin) return;

                e.Handled = true;
                // Focus the Expander's HEADER, not the Expander itself.
                //
                // Found by Noel at the keyboard 2026-08-19, immediately after
                // the Shift+Tab restore above started working: landing on the
                // expander was SILENT when collapsed, and Space did not toggle
                // it (Enter did). One cause, two symptoms — see ExpanderFocus,
                // which now owns the fix for every expander in the app. This
                // dialog and ScreenFieldsPanel had each derived it separately
                // without knowing about the other (task #105).
                bool got = ExpanderFocus.FocusHeader(IdentityExpander);
                JJTrace.Tracing.TraceLine(
                    $"RigSelector: Shift+Tab from list -> IdentityExpander focus={got}",
                    System.Diagnostics.TraceLevel.Info);
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

                // Re-render now that "checking" has an answer. Without this the
                // rows keep saying "checking" for as long as the picker is open,
                // because nothing else necessarily triggers a refresh once
                // discovery goes quiet.
                RefreshRadiosList();

                // A radio landed inside the window — its own arrival speech and
                // the auto-select line already told the user the interesting
                // part. The loaded-state line queues behind it (interrupt:
                // false), and its wording admits local never really finishes:
                // VITA discovery keeps listening the whole time the picker is
                // open, so "loaded" alone would quietly become a lie.
                if (_anyLiveRadioSeen)
                {
                    AnnounceLoadedState(
                        Lexicon.Get("connect.selector.local_loaded_terse"),
                        Lexicon.Get("connect.selector.local_loaded_chatty"));
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

            // Remote-first startup: the account in use asked, ONCE and in
            // Settings, for the remote list to be fetched whenever this picker
            // opens. That is a standing instruction, not a request made now —
            // so the pass runs as background work and is treated as such.
            //
            // Task #85: this chain is why a purely LOCAL connect narrated
            // SmartLink. It spoke "Starting remote radios for your account",
            // then "Connecting to SmartLink as <email>", then put a window
            // titled "Connecting to SmartLink..." over the radio list the
            // operator had just arrived at. Every word of that is true and
            // none of it was asked for at that moment.
            //
            // The window was the loudest of the three, and for a reason worth
            // recording: an arriving window's title is announced by
            // definition, while the two utterances immediately before it sat
            // in a speech queue that the same window's arrival FLUSHES. So the
            // one part of the chain guaranteed to be heard was the part
            // nobody chose the wording of.
            if (callbacks.AutoStartRemote)
            {
                Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    Tracing.TraceLine(
                        "RigSelector: AutoStartRemote pass beginning — background, not operator-initiated (#85)",
                        System.Diagnostics.TraceLevel.Info);
                    Radios.ScreenReaderOutput.Speak(
                        Lexicon.Get("connect.selector.autostart_remote"),
                        Radios.VerbosityLevel.Diagnostic, false);
                    StartRemoteFlow(operatorInitiated: false);
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

            // Ask the history what it suggests for each known radio BEFORE
            // taking _radiosLock: this reads one JSON file per radio, and
            // file IO under the list lock is what the discovery path is
            // careful to avoid. Memoized, so a re-paint costs nothing.
            var learnedBySerial = new Dictionary<string, ConnectPathKind?>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var k in known)
            {
                if (!string.IsNullOrWhiteSpace(k.Serial))
                    learnedBySerial[k.Serial] = LearnedPathFor(k.Serial);
            }

            bool changed = false;
            lock (_radiosLock)
            {
                foreach (var k in known)
                {
                    ConnectPathKind? learned = null;
                    if (!string.IsNullOrWhiteSpace(k.Serial))
                        learnedBySerial.TryGetValue(k.Serial, out learned);

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
                        existing.LearnedPath = learned;
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
                        LearnedPath = learned,
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
            string local = (_localSettled ? Lexicon.Get("connect.selector.f2_local_loaded") : Lexicon.Get("connect.selector.f2_local_loading"));
            string remote = _remoteDiscoveryInFlight
                ? Lexicon.Get("connect.selector.f2_remote_loading")
                : _remoteListLive
                    ? Lexicon.Get("connect.selector.f2_remote_loaded")
                    : Lexicon.Get("connect.selector.f2_remote_not_loaded");
            int live = LiveCount();
            string count = (live == 1 ? Lexicon.Get("connect.selector.f2_count_one",
                ("live", live)) : Lexicon.Get("connect.selector.f2_count_many",
                ("live", live)));
            _callbacks.ScreenReaderSpeak?.Invoke(
                Lexicon.Get("connect.selector.f2_summary",
                    ("local", local), ("remote", remote), ("count", count)), true);
        }

        /// <summary>
        /// Focus belongs on the radio list, always.
        ///
        /// The base implementation walks tab order and takes the first
        /// focusable element, which happens to be the list today and would stop
        /// being so the moment anything focusable is added above it. On
        /// 2026-08-18 the operator arrived on the network identity card at the
        /// very bottom of the dialog instead - a fallback WPF chose because the
        /// window had not been activated when focus was first set. Naming the
        /// target removes both the fragility and the fallback.
        /// </summary>
        protected override void FocusFirstControl()
        {
            if (RadiosBox == null)
            {
                base.FocusFirstControl();
                return;
            }

            // Delegate to FocusRadioList, which lands on the ListBoxITEM rather
            // than the bare container - see its own summary for why that
            // matters to Enter and to focus-restore.
            //
            // The first version of this override focused the container
            // directly and reintroduced two problems that method already
            // solved. One of them was new: with TabNavigation="Once" on the
            // list, Shift+Tab from the CONTAINER resolves inside the group,
            // finds nothing before it, and stops dead - so the operator could
            // not wrap backwards to the end of the dialog, which is where the
            // Network Identity expander now lives. Reported 2026-08-18.
            FocusRadioList();
        }

        private void AnnounceNothingLive()
        {
            int known;
            lock (_radiosLock) { known = _radiosList.Count; }

            // Report the ACTIVITY, not a conclusion.
            //
            // This used to say "No radios online yet ... all offline". It fires
            // when the settle window expires having seen nothing - but that
            // window is a guess about how long discovery usually takes, and a
            // radio arriving a second later made the dialog correct itself out
            // loud: "all offline" followed immediately by "1 radio online".
            //
            // The branch above already knows better. Its comment says local
            // discovery "never really finishes: VITA discovery keeps listening
            // the whole time the picker is open, so 'loaded' alone would
            // quietly become a lie." Same lie, different sentence - there is no
            // instant at which "all offline" is a settled fact, so we must not
            // speak as though there were. Same fix as the per-row "checking"
            // state on 2026-08-17.
            if (known == 0)
            {
                AnnounceLoadedState(
                    Lexicon.Get("connect.selector.discovering_terse"),
                    Lexicon.Get("connect.selector.discovering_none_chatty"));
                return;
            }

            AnnounceLoadedState(
                Lexicon.Get("connect.selector.discovering_terse"),
                (known == 1 ? Lexicon.Get("connect.selector.discovering_known_chatty_one",
                    ("known", known)) : Lexicon.Get("connect.selector.discovering_known_chatty_many",
                    ("known", known))));
        }

        // ------------------------------------------------------------------
        // Learned connection path (task #79)
        // ------------------------------------------------------------------

        /// <summary>
        /// Learned-path answers already worked out this picker session, keyed
        /// by serial. A LAN radio re-announces itself about once a second, so
        /// without a memo the trend lookup would re-read a JSON file per radio
        /// per packet — and read it on the discovery thread at that.
        ///
        /// <para>Caching for the life of the dialog is safe because the input
        /// only changes when a connect is recorded, and a connect closes this
        /// dialog.</para>
        /// </summary>
        private readonly Dictionary<string, ConnectPathKind?> _learnedPaths
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What this radio's connection history suggests, memoized.
        /// Null for an unknown serial, an unreadable store, or no trend.</summary>
        private ConnectPathKind? LearnedPathFor(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return null;
            lock (_learnedPaths)
            {
                if (_learnedPaths.TryGetValue(serial, out var cached)) return cached;
            }
            // ...UsingSettings, so the operator's own threshold applies and the
            // off switch actually switches it off (task #102).
            var learned = ConnectPathPolicy.LearnForRadioUsingSettings(serial);
            lock (_learnedPaths) { _learnedPaths[serial] = learned; }
            return learned;
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

            // Ask the history what it suggests BEFORE taking _radiosLock. This
            // runs on the discovery thread; the memo makes it one file read per
            // radio per session, and the lock stays free of IO either way.
            radio.LearnedPath = LearnedPathFor(radio.Serial);

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
                    row.LearnedPath = radio.LearnedPath;
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
                    ? (string.IsNullOrWhiteSpace(row.Name) ? Lexicon.Get("connect.selector.a_radio") : row.Name)
                    : name;

                if (row.IsLive)
                {
                    // Still reachable the other way. Say which door closed —
                    // "went offline" would be a lie the user could act on.
                    RefreshRadiosList();
                    if (wasDual)
                    {
                        _callbacks.ScreenReaderSpeak?.Invoke(
                            (row.LanAvailable ? Lexicon.Get("connect.selector.left_smartlink",
                                ("who", who)) : Lexicon.Get("connect.selector.left_local",
                                ("who", who))),
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
                row.LastSeenText = Lexicon.Get("connect.selector.last_seen_just_now");
                row.FromAccountCache = false;

                bool hadKeyboard = RadiosBox.IsKeyboardFocusWithin;
                RefreshRadiosList();
                if (RadiosBox.SelectedIndex < 0 && RadiosBox.Items.Count > 0)
                {
                    RadiosBox.SelectedIndex = 0;
                    if (hadKeyboard) FocusRadioList();
                }
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.went_offline", ("who", who)), false);
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
                // Stamp every row with whether discovery has settled, so a row
                // that has not answered yet says "checking" rather than
                // asserting "offline". DisplayText changes as a result, which
                // is what makes the comparison below rebuild the list when the
                // pass completes.
                foreach (var r in _radiosList) r.DiscoverySettled = _localSettled;

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
                    var name = string.IsNullOrWhiteSpace(onlyLive.Name)
                        ? Lexicon.Get("connect.selector.default_radio_word")
                        : onlyLive.Name;
                    _callbacks.ScreenReaderSpeak?.Invoke(
                        Lexicon.Get("connect.selector.only_live_selected", ("name", name)), false);
                }
            }

            UpdateListAutomationName();
        }

        /// <summary>
        /// The radio list's accessible name - what a screen reader reads when
        /// focus lands on the list itself.
        ///
        /// **This is a second announcement channel and it has to tell the same
        /// story as the rows.** It used to read "Known radios, 2 listed, none
        /// online" while discovery was still running, which is the same
        /// premature verdict the per-row state carried until 2026-08-17 and the
        /// spoken summary carried until 2026-08-18. Both of those were fixed
        /// and this one was missed, because it is UIA rather than a Speak call
        /// and so does not turn up when you audit speech call sites.
        ///
        /// While discovery is unsettled the wording describes the ACTIVITY.
        /// "None online" is only said once it is actually known.
        /// </summary>
        private void UpdateListAutomationName()
        {
            int count = RadiosBox.Items.Count;
            int live = LiveCount();

            string name;
            if (count == 0)
            {
                name = (_localSettled ? Lexicon.Get("connect.selector.list_empty_settled") : Lexicon.Get("connect.selector.list_empty_discovering"));
            }
            else if (live == 0)
            {
                name = (_localSettled ? Lexicon.Get("connect.selector.list_none_online",
                    ("count", count)) : Lexicon.Get("connect.selector.list_discovering",
                    ("count", count)));
            }
            else
            {
                name = Lexicon.Get("connect.selector.list_available",
                    ("count", count), ("live", live));
            }

            System.Windows.Automation.AutomationProperties.SetName(RadiosBox, name);
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
                    new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
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
                        Lexicon.Get("connect.selector.foreign_account_not_saved",
                            ("radioName", radioName), ("boundAccount", radio.BoundAccount)), true);
                    return;
                }

                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.switching_account",
                        ("radioName", radioName), ("email", target.Email)), true);
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
                        Lexicon.Get("connect.selector.force_local_unavailable",
                            ("radioName", radioName)), true);
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
                    ? Lexicon.Get("connect.selector.this_account") : radio.LastSeenViaAccount;
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.force_remote_unavailable",
                        ("radioName", radioName), ("acct", acct)), true);
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
                    notes.Add(Lexicon.Get("connect.selector.note_not_local"));
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
                    ? Lexicon.Get("connect.selector.note_not_in_smartlink_list")
                    : Lexicon.Get("connect.selector.note_not_in_account_list", ("acctName", acctName)));
            }

            // Chain exhausted with nothing reachable. Notes are subject-less
            // predicates so the radio is named once at the front rather than
            // once per rung.
            _callbacks.ScreenReaderSpeak?.Invoke(
                notes.Count > 0
                    ? Lexicon.Get("connect.selector.chain_exhausted",
                        ("radioName", radioName),
                        ("notes", string.Join(Lexicon.Get("connect.selector.notes_join"), notes)))
                    : Lexicon.Get("connect.selector.not_reachable", ("radioName", radioName)),
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
                    Lexicon.Get("connect.selector.still_looking", ("radioName", radioName)), true);
                return true;
            }
            if (!_remoteListLive)
            {
                _pendingConnectSerial = radio.Serial;
                _pendingConnectForced = forced;
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.signing_in_to_look", ("radioName", radioName)), true);
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
                    ? Lexicon.Get("connect.selector.via_smartlink")
                    : Lexicon.Get("connect.selector.via_smartlink_as", ("acctEmail", acctEmail))
                : Lexicon.Get("connect.selector.via_local");
            _callbacks.ScreenReaderSpeak?.Invoke(
                Lexicon.Get("connect.selector.connecting", ("radioName", radioName), ("via", via)), true);
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
                    Lexicon.Get("connect.selector.smartlink_unreachable", ("radioName", RowName(row))), true);
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
            // #128: an operator-facing boolean answers back. The defensive
            // resync path above returns before this line, so a checkbox the
            // code snapped back to off never claims a toggle happened.
            EarconPlayer.ToggleTone(radio.LowBW);
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

        // The option labels ARE the explanation (task #107).
        //
        // Each of these used to be a short identifier ("Local network first")
        // followed, on every arrow press, by a spoken sentence saying what it
        // meant ("This radio will connect over the local network first, falling
        // back to SmartLink."). NVDA reads the item itself, so that was the
        // same fact twice, and the second copy took four times as long to say.
        //
        // Fold the meaning into the label and the arrow announcement IS the
        // answer — one utterance, chosen by us, spoken by the screen reader the
        // operator already tuned to their own rate. Same precedent as commit
        // d09f0e50: names are identifiers, and where the identifier can carry
        // the meaning honestly, nothing else needs to say it.
        //
        // Keep these in step with the context menu's Default Connection Path
        // submenu, which writes the same store — ONE vocabulary, so a setting
        // changed by one door reads identically at the other.
        // Lexicon-backed, so no longer const — a const must be a compile-time
        // literal. Still one vocabulary shared by the combo, the menu and speech.
        private static string PathAutomatic => Lexicon.Get("connect.selector.path_automatic");
        private static string PathLocalFirst => Lexicon.Get("connect.selector.path_local_first");
        private static string PathSmartLinkFirst => Lexicon.Get("connect.selector.path_smartlink_first");

        /// <summary>
        /// What the path control should currently be showing. Compared before
        /// every rebuild because a LAN radio re-announces about once a second,
        /// and tearing the combo's items down that often would fight a user who
        /// is arrowing through it.
        /// </summary>
        private string PathKey()
        {
            var r = GetSelectedRadio();
            // The learned value is part of the key because it changes the
            // automatic option's WORDING. Leaving it out meant a row whose
            // trend arrived after the combo was first built kept saying plain
            // "Automatic" while walking the learned order.
            return r == null
                ? "<none>"
                : $"{r.Serial}|{string.Join(",", r.PathChain)}|{r.LearnedPath}";
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
                        PathCombo, Lexicon.Get("connect.selector.path_combo_name_none"));
                    JJFlexHelp.SetText(PathCombo, PathComboHelp + " " + DescribeLearningState());
                    return;
                }

                // Task #79: when the trend has prefilled the order, the
                // automatic option SAYS SO. A prefill the operator cannot
                // perceive is a prefill they cannot disagree with, which is
                // how a learned value quietly becomes a decision nobody made.
                // To reject it, pick one of the explicit orders below — those
                // are stored choices and a stored choice always wins.
                PathCombo.Items.Add(
                    radio.ChainIsLearned
                        ? (radio.LearnedPath == ConnectPathKind.SmartLink ? Lexicon.Get("connect.selector.path_learned_smartlink") : Lexicon.Get("connect.selector.path_learned_local"))
                        : PathAutomatic);
                PathCombo.Items.Add(PathLocalFirst);
                PathCombo.Items.Add(PathSmartLinkFirst);
                PathCombo.SelectedIndex =
                    radio.PathChain.Count == 0 ? 0
                    : radio.PathChain[0] == ConnectPathKind.SmartLink ? 2
                    : 1;
                PathCombo.IsEnabled = true;
                // Name is a LABEL, not documentation. This used to carry a
                // 29-word paragraph, which a screen reader reads in full on
                // every single focus — before it ever says "combo box". The
                // explanation it held is already in keyboard-reference.md under
                // Alt+P, nearly word for word, and that reaches the operator
                // through F1 when they ask for it rather than every time they
                // arrow past. Cost of a Name is paid on every visit; value is
                // paid once.
                System.Windows.Automation.AutomationProperties.SetName(
                    PathCombo, Lexicon.Get("connect.selector.path_combo_name"));

                // The Ctrl+F1 explanation, composed here rather than in XAML
                // because its last sentence has to report the CURRENT learning
                // setting — including the off state, which is the one most
                // easily left unsaid (task #102). On-demand, so a sentence
                // about a setting most operators never touch costs nothing on
                // focus.
                JJFlexHelp.SetText(PathCombo, PathComboHelp + " " + DescribeLearningState());
            }
            finally
            {
                _suppressPathComboEvent = false;
            }
        }

        private static string PathComboHelp => Lexicon.Get("connect.selector.path_combo_help");

        /// <summary>
        /// Where path learning currently stands, in one sentence, honest about
        /// the OFF state. Says where to change it, because the setting lives in
        /// Settings and the question gets asked here.
        /// </summary>
        private static string DescribeLearningState()
        {
            var cfg = ConnectPathLearningConfig.Current;
            return cfg.LearnFromHistory
                ? Lexicon.Get("connect.selector.learning_on",
                    ("trendThreshold", cfg.TrendThreshold))
                : Lexicon.Get("connect.selector.learning_off");
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
            // No confirmation from this door. The screen reader has just read
            // the combo item, whose label now carries the whole meaning — see
            // the label constants. Task #107.
            SetPathChainForRow(radio, chain, confirm: false);
        }

        /// <summary>
        /// Persist a path-chain choice for one row and announce honestly —
        /// a spoken success over a declined save is a promise the next
        /// launch breaks. Shared by the combo and the context menu.
        /// </summary>
        /// <param name="confirm">
        /// Whether to speak a confirmation on SUCCESS. False for the combo,
        /// where the screen reader has just read the chosen item and a spoken
        /// restatement is the same fact twice (#107). True for the context
        /// menu, where the menu closes on click and the operator lands back on
        /// the radio list with nothing on screen to say what happened.
        /// <para>A FAILED save always speaks, whatever this says: silence over
        /// a setting that did not stick is the lying-receipt bug.</para>
        /// </param>
        private void SetPathChainForRow(
            RadioListItem radio, List<ConnectPathKind> chain, bool confirm)
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

            // The chosen order, in the SAME words the combo item and the menu
            // item use. Dropped from this: the "This radio will..." prefix the
            // sentences used to carry — the operator is looking at that radio's
            // row and just acted on that radio's menu, so naming it again is
            // the third time in one gesture.
            string order = DescribePathChain(chain);

            // Only mention persistence when it failed. Saying "and it is saved"
            // on every success is noise; saying nothing when it did NOT save is
            // the lying-receipt bug. The reason lives in the trace file, which
            // is where a support conversation can actually use it.
            if (!persisted)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.path_not_persisted", ("order", order)),
                    true);
            }
            else if (confirm)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.path_confirmed", ("order", order)), true);
            }

            RefreshRadiosList();
            ReselectBySerial(radio.Serial);
        }

        /// <summary>
        /// One chain, in words — the single vocabulary shared by the combo
        /// items, the context-menu items and the spoken confirmation. Three
        /// surfaces described this in three slightly different ways before
        /// #107, which is how "falling back to" and "then" ended up meaning the
        /// same thing in one dialog.
        /// </summary>
        private static string DescribePathChain(IReadOnlyList<ConnectPathKind> chain)
        {
            if (chain.Count == 0) return PathAutomatic;

            // A one-entry chain means "this path only, never fall back" — the
            // thing that makes force-remote a valid hole-punch test instrument.
            // No door in this dialog stores one today, but describing it as
            // "first, then the other" would be a flat lie if one ever did.
            if (chain.Count == 1)
            {
                return (chain[0] == ConnectPathKind.SmartLink ? Lexicon.Get("connect.selector.path_smartlink_only") : Lexicon.Get("connect.selector.path_local_only"));
            }

            return chain[0] == ConnectPathKind.SmartLink ? PathSmartLinkFirst : PathLocalFirst;
        }

        /// <summary>
        /// The radio's name for SPEECH. Blank and the literal sentinel
        /// "Unknown" both mean "no name" — the radio reports "Unknown" when it
        /// has none, and this used to pass it straight through, so a connect
        /// announced "Connecting to Unknown on the local network" and then
        /// "Connected to Unknown. Waiting for slice..." for a radio the
        /// operator had just picked by name (found 2026-08-17 in a verbose
        /// speech trace, which then said "Connected to FLEX-8600" three lines
        /// later once the real name resolved).
        ///
        /// DisplayText already guarded against the same sentinel and this did
        /// not — two readers of one field, one of them informed. Kept in sync
        /// via NameIsMissing so the next reader cannot drift again.
        /// </summary>
        private static string RowName(RadioListItem r) =>
            !RadioListItem.NameIsMissing(r.UserLabel) ? r.UserLabel
            : !RadioListItem.NameIsMissing(r.Name) ? r.Name
            : Lexicon.Get("connect.selector.this_radio");

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
            FavoriteMenuItem.Header = (fav ? Lexicon.Get("connect.selector.menu_remove_favorite") : Lexicon.Get("connect.selector.menu_add_favorite"));
            System.Windows.Automation.AutomationProperties.SetName(FavoriteMenuItem,
                (fav ? Lexicon.Get("connect.selector.menu_remove_favorite_name") : Lexicon.Get("connect.selector.menu_add_favorite_name")));
            FavoriteMenuItem.IsEnabled = radio != null;
            ConnectLocalMenuItem.IsEnabled = radio != null;
            ConnectRemoteMenuItem.IsEnabled = radio != null;
            // The list item wears its state: before a successful pass it
            // shows the radios, after one it refreshes them (the server
            // sends its list once per TLS session, so a repeat is a
            // session-cycling refresh).
            RemoteListMenuItem.Header = (_remoteListLive ? Lexicon.Get("connect.selector.menu_refresh_remote") : Lexicon.Get("connect.selector.menu_show_remote"));
            System.Windows.Automation.AutomationProperties.SetName(RemoteListMenuItem,
                (_remoteListLive ? Lexicon.Get("connect.selector.menu_refresh_remote_name") : Lexicon.Get("connect.selector.menu_show_remote_name")));
            // Task #102: only offer to forget something there is something to
            // forget. An always-enabled item that answers "there was nothing
            // learned for this radio" is an item that teaches the operator to
            // stop trusting it.
            RemoveRadioMenuItem.IsEnabled = radio != null;

            bool hasLearned = radio?.LearnedPath.HasValue == true;
            ForgetLearnedPathMenuItem.IsEnabled = hasLearned;
            System.Windows.Automation.AutomationProperties.SetName(ForgetLearnedPathMenuItem,
                (hasLearned ? Lexicon.Get("connect.selector.menu_forget_learned_name") : Lexicon.Get("connect.selector.menu_forget_learned_name_none")));

            BuildPreferredAccountSubmenu(radio);
            BuildDefaultPathSubmenu(radio);
        }

        /// <summary>
        /// Forget what this radio's history taught (task #102), door one of
        /// two — Settings does every radio at once.
        ///
        /// <para><b>What it clears, and why there is no smaller option:</b> the
        /// radio's connection history ring. A learned path is not stored
        /// anywhere; <see cref="ConnectPathPolicy"/> derives it from that ring
        /// each time it is asked. "Forget the conclusion but keep the evidence"
        /// would leave the conclusion to be re-derived within milliseconds, so
        /// offering it would be offering a lie. The confirmation says so, and
        /// names the diagnostic value that goes with it.</para>
        /// </summary>
        private void ForgetLearnedPathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            var rowName = RowName(radio);
            var confirm = new ConfirmActionDialog(
                Lexicon.Get("connect.selector.forget_title"),
                Lexicon.Get("connect.selector.forget_body", ("rowName", rowName)),
                new[]
                {
                    Lexicon.Get("connect.selector.forget_warning_history"),
                    Lexicon.Get("connect.selector.forget_warning_choice"),
                    Lexicon.Get("connect.selector.forget_warning_restart"),
                },
                question: Lexicon.Get("connect.selector.forget_question", ("rowName", rowName)),
                yesLabel: Lexicon.Get("connect.selector.forget_yes"),
                noLabel: Lexicon.Get("connect.selector.forget_no"))
            {
                Owner = this,
            };

            if (confirm.ShowDialog() != true)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(Lexicon.Get("connect.selector.forget_declined"), true);
                FocusRadioList();
                return;
            }

            if (!ConnectionHistory.Clear(radio.Serial))
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.forget_failed"), true);
                FocusRadioList();
                return;
            }

            // Drop the memo as well as the file. It is cached for the life of
            // this dialog precisely so a LAN radio's once-a-second announcement
            // does not re-read a JSON file — which also means a stale entry
            // here would keep the row saying "learned" over a store that no
            // longer says anything.
            lock (_learnedPaths) { _learnedPaths.Remove(radio.Serial); }
            radio.LearnedPath = null;
            _pathAffordanceKey = "";
            SyncPathAffordance();

            _callbacks.ScreenReaderSpeak?.Invoke(
                Lexicon.Get("connect.selector.forget_done", ("rowName", rowName)),
                true);
            RefreshRadiosList();
            ReselectBySerial(radio.Serial);
            FocusRadioList();
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

            // ONE label, used as the header AND as the accessible name. They
            // were different strings until #107 — "Local Network First" on
            // screen, "Local network first, falling back to SmartLink." by
            // ear — and the row text elsewhere in this file already carries the
            // rule those two broke: what a sighted user reads and what a screen
            // reader says must not diverge. Same constants as the combo, so a
            // path set at one door reads identically at the other.
            void AddChoice(string label, List<ConnectPathKind> chain, bool isChecked)
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = isChecked };
                System.Windows.Automation.AutomationProperties.SetName(item, label);
                // Confirm here: clicking closes the menu and drops the operator
                // back on the radio list, so nothing on screen would otherwise
                // say what the click did.
                item.Click += (_, _) => SetPathChainForRow(radio, chain, confirm: true);
                DefaultPathMenuItem.Items.Add(item);
            }

            AddChoice(PathAutomatic,
                new List<ConnectPathKind>(),
                radio.PathChain.Count == 0);
            AddChoice(PathLocalFirst,
                new List<ConnectPathKind> { ConnectPathKind.Local, ConnectPathKind.SmartLink },
                radio.PathChain.Count > 0 && radio.PathChain[0] == ConnectPathKind.Local);
            AddChoice(PathSmartLinkFirst,
                new List<ConnectPathKind> { ConnectPathKind.SmartLink, ConnectPathKind.Local },
                radio.PathChain.Count > 0 && radio.PathChain[0] == ConnectPathKind.SmartLink);
        }

        private void ConnectLocalMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
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
                new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
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
                Header = Lexicon.Get("connect.selector.menu_account_automatic"),
                IsCheckable = true,
                IsChecked = string.IsNullOrWhiteSpace(radio.PreferredAccount),
            };
            System.Windows.Automation.AutomationProperties.SetName(auto,
                Lexicon.Get("connect.selector.menu_account_automatic_name"));
            auto.Click += (_, _) => SetPreferredAccountForRow(radio, "");
            PreferredAccountMenuItem.Items.Add(auto);

            foreach (var acct in Radios.FlexBase.SharedAccountManager.Accounts)
            {
                var email = acct.Email;
                if (string.IsNullOrWhiteSpace(email)) continue;
                var label = string.IsNullOrWhiteSpace(acct.FriendlyName)
                            || string.Equals(acct.FriendlyName, email, StringComparison.OrdinalIgnoreCase)
                    ? email
                    : Lexicon.Get("connect.selector.account_label",
                        ("friendlyName", acct.FriendlyName), ("email", email));
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
                    Lexicon.Get("connect.selector.preferred_account_save_failed"),
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
                    ? Lexicon.Get("connect.selector.preferred_account_cleared", ("rowName", rowName))
                    : Lexicon.Get("connect.selector.preferred_account_set",
                        ("rowName", rowName), ("email", email)),
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
                new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            bool wanted = !radio.IsFavorite;
            if (!KnownRadioRoster.SetFavorite(radio.Serial, wanted))
            {
                // The store declined. Saying "added to favorites" here would be a
                // promise the next launch breaks.
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.favorite_save_failed"),
                    true);
                return;
            }

            radio.IsFavorite = wanted;
            _callbacks.ScreenReaderSpeak?.Invoke(
                (wanted ? Lexicon.Get("connect.selector.favorite_added",
                    ("rowName", RowName(radio))) : Lexicon.Get("connect.selector.favorite_removed",
                    ("rowName", RowName(radio)))),
                true);
            RefreshRadiosList();
            ReselectBySerial(radio.Serial);
            if (RadiosBox.IsKeyboardFocusWithin) FocusRadioList();
        }

        // ------------------------------------------------------------------
        // Removing a radio (task #98)
        // ------------------------------------------------------------------

        private void RemoveRadioMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }
            RemoveRadio(radio);
        }

        /// <summary>
        /// Take a radio off the list, at a scope the operator chooses inside
        /// the confirmation. Shared by the Delete key and the context menu —
        /// two doors, one behaviour, by construction rather than by discipline.
        /// </summary>
        private void RemoveRadio(RadioListItem radio)
        {
            var rowName = RowName(radio);

            // The radio you are USING is not removable. Deleting its settings
            // out from under a live session would have the app writing per-radio
            // state back to a directory it just deleted, and hiding a radio that
            // is plainly on screen and connected is incoherent. Refuse and say
            // what to do instead — a refusal with a route is help; a refusal
            // without one is a wall.
            var rig = _callbacks.GetCurrentRig?.Invoke();
            bool connectedToThis = rig != null && rig.IsConnected
                && (string.Equals(rig.ConnectedSerial, radio.Serial, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rig.SelectedRadioSerial, radio.Serial, StringComparison.OrdinalIgnoreCase));
            if (connectedToThis)
            {
                new MessageDialog
                {
                    Title = Lexicon.Get("connect.selector.in_use_title"),
                    Message = Lexicon.Get("connect.selector.in_use_body", ("rowName", rowName)),
                    Owner = this,
                }.ShowDialog();
                FocusRadioList();
                return;
            }

            var dialog = new RemoveRadioDialog(rowName, radio.IsLive, radio.AutoConnect)
            {
                Owner = this,
            };
            if (dialog.ShowDialog() != true)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.remove_declined", ("rowName", rowName)), true);
                FocusRadioList();
                return;
            }

            bool deleteSettings = dialog.DeleteSettings;

            // Auto-connect first, and whatever else happens. A startup that
            // hunts for a radio the operator just removed is the app arguing
            // with them once a day, and the roster row carrying the
            // "[AutoConnect]" marker is about to disappear along with the only
            // place that setting was visible.
            if (radio.AutoConnect)
            {
                try
                {
                    _callbacks.SaveAutoConnectSettings(
                        radio.Serial, radio.Name, radio.IsRemote, radio.LowBW, false);
                    radio.AutoConnect = false;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(
                        $"RigSelector.RemoveRadio({radio.Serial}): clearing auto-connect: {ex.Message}",
                        System.Diagnostics.TraceLevel.Warning);
                }
            }

            bool ok = KnownRadioRoster.Remove(radio.Serial, deleteSettings);

            // Drop the row whether or not the store cooperated: the operator
            // asked, and refusing an intent because a file was locked hands our
            // problem to them. What we owe is the truth about how long it will
            // last, which the speech below tells them.
            lock (_radiosLock)
            {
                _radiosList.RemoveAll(r =>
                    string.Equals(r.Serial, radio.Serial, StringComparison.OrdinalIgnoreCase));
            }
            lock (_learnedPaths) { _learnedPaths.Remove(radio.Serial); }
            // Let a fresh sighting re-record. That is what clears the hidden
            // flag for a radio that turns out to still be there — without
            // this, the once-per-session guard would suppress the very write
            // that brings a live radio back.
            lock (_sightingsRecorded) { _sightingsRecorded.Remove(radio.Serial); }
            _pathAffordanceKey = "";
            RefreshRadiosList();
            SyncPathAffordance();

            string speech;
            if (!ok)
            {
                speech = Lexicon.Get("connect.selector.remove_not_persisted", ("rowName", rowName));
            }
            else if (deleteSettings)
            {
                speech = Lexicon.Get("connect.selector.remove_with_settings", ("rowName", rowName));
            }
            else if (radio.IsLive)
            {
                // Say it BEFORE it happens rather than letting the operator
                // discover it. The dialog warned; this is the receipt matching
                // the warning.
                speech = Lexicon.Get("connect.selector.remove_live", ("rowName", rowName));
            }
            else
            {
                speech = Lexicon.Get("connect.selector.remove_offline", ("rowName", rowName));
            }

            _callbacks.ScreenReaderSpeak?.Invoke(speech, true);

            // Land somewhere real. The row that had focus no longer exists, and
            // WPF's fallback after a modal closes is the top of the tab order,
            // which is not where the operator was.
            if (RadiosBox.Items.Count > 0) FocusRadioList();
            else RadiosBox.Focus();
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
        /// <param name="operatorInitiated">
        /// True when the operator asked for this pass right now - the context
        /// menu, the Remote key, a connect that needs SmartLink. False for the
        /// AutoStartRemote pass, which runs because of a standing preference
        /// rather than a decision made at this moment (task #85).
        ///
        /// <para>It changes two things and nothing else. A background pass does
        /// not put a window over the radio list the operator is reading, and it
        /// narrates itself at Diagnostic rather than unconditionally. The work,
        /// the fallbacks, the list delta and the failure reporting are
        /// identical - a background pass that fails still says so.</para>
        /// </param>
        private void StartRemoteFlow(bool forceSessionCycle = false, bool operatorInitiated = true)
        {
            if (_remoteDiscoveryInFlight)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.remote_already_running"), false);
                return;
            }

            bool refreshing = (_remoteListLive || forceSessionCycle) && _callbacks.StartRemoteRefresh != null;
            _remoteDiscoveryInFlight = true;
            MarkCachedRowsRefreshing(true);
            RefreshRadiosList();

            Tracing.TraceLine(
                $"RigSelector.StartRemoteFlow: operatorInitiated={operatorInitiated} "
                + $"refreshing={refreshing} - #85 attribution trace",
                System.Diagnostics.TraceLevel.Info);

            // Say WHICH account is about to be used. Anyone with more than one
            // SmartLink login was previously left to infer it from whichever
            // radios turned up (C2 item 15).
            //
            // Task #85: this went through the legacy ungated overload, so it
            // was spoken at every verbosity, Off included. On a pass the
            // operator did not start it is precisely what ScreenReaderOutput's
            // own enum calls Diagnostic - "which account a session used, what a
            // background task is doing". Gated, not deleted: a tester chasing
            // an account problem still hears it.
            var state = CurrentAccountState();
            if (!string.IsNullOrWhiteSpace(state.Email))
            {
                string accountLine = (refreshing ? Lexicon.Get("connect.selector.refreshing_for_account",
                    ("email", state.Email)) : Lexicon.Get("connect.selector.connecting_as_account",
                    ("email", state.Email)));
                if (operatorInitiated)
                    _callbacks.ScreenReaderSpeak?.Invoke(accountLine, false);
                else
                    Radios.ScreenReaderOutput.Speak(accountLine, Radios.VerbosityLevel.Diagnostic, false);
            }

            // Show WinForms connecting window to hold focus while SmartLink
            // auth runs - but ONLY for a pass the operator started.
            //
            // On the AutoStartRemote pass this window was the loudest part of
            // task #85. It arrives over the radio list a fraction of a second
            // after the operator gets there, takes the foreground, flushes
            // whatever the screen reader was saying, and announces "Connecting
            // to SmartLink..." to somebody who came here to pick the radio in
            // the next room. Background work does not get the foreground. If
            // the pass needs interactive sign-in, that flow brings its own
            // window and owns its own announcement.
            _closeConnecting = operatorInitiated
                ? _callbacks.ShowConnecting?.Invoke(
                    (refreshing ? Lexicon.Get("connect.selector.connecting_window_refresh") : Lexicon.Get("connect.selector.connecting_window")))
                : null;

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
                        AnnounceLoadedState(
                            Lexicon.Get("connect.selector.remote_loaded_terse"),
                            Lexicon.Get("connect.selector.remote_loaded_chatty"));
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
                        ? Lexicon.Get("connect.selector.delta_none_online")
                        : Lexicon.Get("connect.selector.delta_unchanged", ("count", liveNow.Count)),
                    false);
                return;
            }

            _callbacks.ScreenReaderSpeak?.Invoke(
                (liveNow.Count == 1 ? Lexicon.Get("connect.selector.delta_updated_one",
                    ("count", liveNow.Count)) : Lexicon.Get("connect.selector.delta_updated_many",
                    ("count", liveNow.Count))), false);
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

            // The account line is COLLAPSED unless SmartLink is actually in
            // play for this session.
            //
            // A screen reader reads a dialog's static content when the dialog
            // opens, so this line was announced on every launch, in the middle
            // of the startup sequence, whether or not the operator was going
            // anywhere near SmartLink. Reported 2026-08-18: heard as part of
            // the connect narration, before discovery had even settled.
            //
            // Gating on "an account is saved" would not have helped - the
            // operator who is bothered by this HAS an account. What matters is
            // whether THIS connect involves SmartLink at all. Collapsed rather
            // than merely non-tab-stop, because collapsed leaves the UIA tree
            // entirely and so is not read as dialog content either.
            bool smartLinkEngaged = _remoteListLive || _remoteDiscoveryInFlight;
            AccountStatusText.Visibility = smartLinkEngaged
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

            if (state.Count <= 0)
            {
                AccountButton.Content = Lexicon.Get("connect.selector.account_button_signin");
                System.Windows.Automation.AutomationProperties.SetName(AccountButton,
                    Lexicon.Get("connect.selector.account_button_signin_name"));
                AccountStatusText.Text = Lexicon.Get("connect.selector.account_status_none");
            }
            else if (state.Count == 1)
            {
                AccountButton.Content = Lexicon.Get("connect.selector.account_button_one");
                System.Windows.Automation.AutomationProperties.SetName(AccountButton,
                    string.IsNullOrWhiteSpace(who)
                        ? Lexicon.Get("connect.selector.account_button_one_name")
                        : Lexicon.Get("connect.selector.account_button_one_name_who", ("who", who)));
                AccountStatusText.Text = string.IsNullOrWhiteSpace(who)
                    ? Lexicon.Get("connect.selector.account_status_one")
                    : Lexicon.Get("connect.selector.account_status_one_who", ("who", who));
            }
            else
            {
                AccountButton.Content = Lexicon.Get("connect.selector.account_button_switch");
                System.Windows.Automation.AutomationProperties.SetName(AccountButton,
                    string.IsNullOrWhiteSpace(who)
                        ? Lexicon.Get("connect.selector.account_button_switch_name", ("count", state.Count))
                        : Lexicon.Get("connect.selector.account_button_switch_name_who",
                            ("who", who), ("count", state.Count)));
                AccountStatusText.Text = string.IsNullOrWhiteSpace(who)
                    ? Lexicon.Get("connect.selector.account_status_many", ("count", state.Count))
                    : Lexicon.Get("connect.selector.account_status_many_who",
                        ("who", who), ("count", state.Count));
            }

            // The full address is deliberately NOT appended here. When a
            // friendly name exists it already identifies the account, and
            // reading "Contest Station, nromey@fastmail.com" spends a second of
            // speech to add nothing the operator did not already know. The
            // address is still on the Account button's own name, one Tab away,
            // for the moment it is actually the question.
            //
            // When no friendly name is set, `who` IS the address, so the
            // account remains identifiable either way.
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
                    Lexicon.Get("connect.selector.accounts_updated", ("count", after.Count)), false);
                return;
            }

            // Nothing changed. Silence after a button press reads as a dead
            // control, so say what is still true.
            _callbacks.ScreenReaderSpeak?.Invoke(
                string.IsNullOrWhiteSpace(after.Email)
                    ? Lexicon.Get("connect.selector.no_account_change")
                    : Lexicon.Get("connect.selector.no_account_change_who", ("email", after.Email)),
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
                    Lexicon.Get("connect.selector.switch_cached",
                        ("email", state.Email), ("cached", cached)), false);
            }
            else
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.switch_no_cache", ("email", state.Email)), false);
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
                new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            // Check if another radio has auto-connect
            var (hasOther, otherName) = _callbacks.CheckOtherAutoConnect(radio.Serial);
            if (hasOther && !radio.AutoConnect)
            {
                var displayOther = string.IsNullOrEmpty(otherName)
                    ? Lexicon.Get("connect.selector.autoconnect_other")
                    : otherName;
                var result = MessageBox.Show(
                    Lexicon.Get("connect.selector.autoconnect_switch_body",
                        ("displayOther", displayOther), ("radioName", radio.Name)),
                    Lexicon.Get("connect.selector.autoconnect_switch_title"),
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

                    _callbacks.ScreenReaderSpeak?.Invoke(
                        (newAutoConnect ? Lexicon.Get("connect.selector.autoconnect_set",
                            ("radioName", radio.Name)) : Lexicon.Get("connect.selector.autoconnect_cleared",
                            ("radioName", radio.Name))), true);
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
                    ScreenReaderOutput.Speak(Lexicon.Get("connect.selector.no_radios_yet"), VerbosityLevel.Critical, true);
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

            // Deliberately SILENT. RadioListItem.ToString() returns DisplayText,
            // so the ListBox item's accessible name is already that exact
            // string — the screen reader announces the row on every arrow, and
            // announces "N of M" for list items by itself. Speaking it again
            // here meant hearing each row twice, which is what Noel reported on
            // 2026-08-17 ("still seeing double speaking as I arrow").
            //
            // This is the same defect already fixed in the device picker
            // (task #63, "delete the redundant utterance"); the radio selector
            // was never given the same treatment.
            //
            // "none online" is not lost: it is a property of the LIST, not of
            // the row, and repeating it on every arrow was noise. It is spoken
            // by AnnounceNothingLive when discovery settles, and F2 reports the
            // loaded state on demand.
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

            // #128 sweep audit (2026-08-21): this checkbox is the THIRD road
            // into the same auto-connect flag the two menu items flip through
            // MainWindow.ToggleAutoConnect, which has toned since Sprint 32
            // Track E — and this road was silent. It cannot share that choke
            // (the state lives in AutoConnectConfig in Radios.dll, which
            // cannot reach EarconPlayer, and this dialog sets an explicit
            // value rather than toggling), so the tone rides the handler.
            EarconPlayer.ToggleTone(enabled);

            // No SPEECH, deliberately. This is a real CheckBox with the
            // accessible name "Enable auto-connect on startup", and the screen
            // reader announces its checked state on every toggle. The
            // suppression flag above means this handler only runs on a USER
            // toggle — exactly the moment the reader is already speaking — so
            // saying it again was a guaranteed double. A tone duplicates
            // nothing: it confirms the SAVE happened, which the checkbox
            // cannot show.
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

            // Deliberately SILENT on arrow keys. RadioListItem.ToString()
            // returns DisplayText and the ListBox has no ItemTemplate, so the
            // item's accessible name IS that string — the screen reader
            // announces it, and announces "N of M" position, by itself. This
            // spoke the identical sentence on top of that, which is the double
            // speech Noel reported on 2026-08-17.
            //
            // HISTORY, because this line has been wrong in BOTH directions and
            // the next person deserves the whole story: it was added after
            // 2026-08-05, when arrow announcements went missing entirely
            // ("it's not actually in the list"). The real defect then was that
            // this handler tested IsFocused, which is false once WPF realises
            // item containers and moves keyboard focus onto the ListBoxItem —
            // so the app said nothing AND the screen reader was, at that
            // moment, the only thing that should have been speaking anyway.
            // The IsKeyboardFocusWithin fix made the app speak again, which
            // restored the announcement and reintroduced the duplicate.
            //
            // If arrow keys ever go quiet again, the answer is NOT to restore
            // this Speak. It is that the item's accessible name broke — check
            // ToString() and that no ItemTemplate was added without an
            // AutomationProperties.Name on it.
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
                new MessageDialog { Title = SelectRadioTitle, Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            if (!radio.IsLive)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(
                    Lexicon.Get("connect.selector.test_offline", ("rowName", RowName(radio))), true);
                return;
            }

            if (_callbacks.OpenParms == null)
            {
                _callbacks.ScreenReaderSpeak?.Invoke(Lexicon.Get("connect.selector.test_unavailable"), true);
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
                Title = Lexicon.Get("connect.selector.no_radios_title"),
                Message = Lexicon.Get("connect.selector.no_radios_body"),
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
