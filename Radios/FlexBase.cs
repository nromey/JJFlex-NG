//#define KeepAlive
//#define feedback // for testing mic input
//#define TwoSlices
//#define NoATU
#define CWMonitor
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using HamBands;
using JJPortaudio;
using JJTrace;
using Radios.DiscoveryChain;
using Radios.SmartLink;

namespace Radios
{
    // The MeterType enum and the MeterChanged event lived here until Sprint 32
    // Track B retired them — the hand-off this file's own meter section
    // describes. They named eight meters on a radio that reports over a
    // hundred, and everything above them was choosing from eight because
    // identity had already been destroyed by the adapter. The replacement is
    // FlexBase.MeterData (every reading of every meter, with the Meter itself)
    // and FlexBase.MeterInventory. MeterToneEngine was the only consumer.

    /// <summary>
    /// Tri-state outcome from ConnectToSmartLink so setupRemote can distinguish
    /// "we connected but the user has no radios available" from "we couldn't
    /// connect at all." Without the distinction setupRemote treated an empty
    /// radio list as a connection failure and retried with a fresh login,
    /// which then hit "Invalid state for application registration" on the
    /// already-registered session and silently timed out for 10 seconds per
    /// retry — see trace from 2026-05-11 (session a21ef183754d).
    /// </summary>
    internal enum SmartLinkConnectResult
    {
        /// <summary>Session connected, registered, and radio list contains at least one radio.</summary>
        Success,
        /// <summary>Session connected and registered successfully, but the server reported zero radios for this account. Don't retry — the remote rigs are simply off.</summary>
        NoRadios,
        /// <summary>
        /// Session failed at the transport / server level: TLS connect timed
        /// out, the radio list never came on a fresh session, or an exception
        /// fired. The user's SIGN-IN is not implicated — QB Track D: a retry
        /// (session cycle) is fair medicine, an interactive login form is not.
        /// Treating every one of these as auth-shaped is what used to summon
        /// pointless sign-in forms over healthy accounts.
        /// </summary>
        ConnectFailed,
        /// <summary>
        /// SmartLink explicitly rejected our authorization (session status
        /// AuthorizationExpired before or during registration). This is the
        /// ONE failure class where re-authentication — silent JWT refresh
        /// first, interactive login as last resort — is the right medicine.
        /// </summary>
        AuthFailed
    }

    /// <summary>
    /// Flex superclass
    /// </summary>
    public partial class FlexBase : AllRadios, IDisposable
    {
        // Lexicon-backed, so no longer const — a const must be a compile-time literal.
        private static string statusHdr => Lexicon.Get("settings.flexdb.status_header");
        private static string importedMsg => Lexicon.Get("settings.flexdb.imported");
        private static string importFailMsg => Lexicon.Get("settings.flexdb.import_failed");
        private const string noRXAnt = "no RX antenna";
        private const string noSlice = "didn't get a slice";
        private const string noStation = "Station name not set";

        /// <summary>
        /// Data describing a rig.
        /// </summary>
        public class RigData
        {
            public string Name;
            public string ModelName;
            public string Serial;
            public bool Remote { get; internal set; }

            /// <summary>
            /// True when local discovery can see this radio right now.
            /// </summary>
            public bool LanAvailable { get; internal set; }

            /// <summary>
            /// True when the SmartLink account's radio list carries this radio.
            /// <para>Independent of <see cref="LanAvailable"/> on purpose: a radio
            /// sitting on the operator's own LAN and registered with SmartLink is
            /// reachable BOTH ways, and until 2026-08-07 the selector could only
            /// ever say "local" for it — the LAN announcement wins the row and the
            /// WAN identity was never surfaced at all. Both flags true means the
            /// user gets to pick the path (Noel, 2026-08-07).</para>
            /// </summary>
            public bool WanAvailable { get; internal set; }

            /// <summary>True when the radio answers on both paths.</summary>
            public bool DualHomed => LanAvailable && WanAvailable;

            internal RigData() { }
        }
        public delegate void RadioFoundDel(object sender, RigData r);
        /// <summary>
        /// Radio found event, local or remote.
        /// </summary>
        public static event RadioFoundDel RadioFound;
        /// <summary>
        /// Raise RadioFound.
        /// </summary>
        /// <param name="sender">sending object, or null.</param>
        /// <param name="r">Radio object</param>
        internal static void RaiseRadioFound(object sender, RigData r)
        {
            if (RadioFound != null)
            {
                Tracing.TraceLine("RaiseRadioFound:" + r.Serial, TraceLevel.Info);
                RadioFound(sender, r);
            }
        }

        public delegate void RadioRemovedDel(object sender, string serial, string name);
        /// <summary>
        /// Raised when a previously listed WAN radio is absent from a freshly
        /// received SmartLink list — it went offline (or left the account).
        /// Lets the RigSelector drop ghost rows on a refresh instead of
        /// offering radios that will only fail to connect.
        /// </summary>
        public static event RadioRemovedDel RadioRemoved;
        internal static void RaiseRadioRemoved(object sender, string serial, string name)
        {
            Tracing.TraceLine("RaiseRadioRemoved:" + serial + " " + name, TraceLevel.Info);
            RadioRemoved?.Invoke(sender, serial, name);
        }

        private List<Radio> myRadioList = new List<Radio>();

        /// <summary>
        /// The WAN <see cref="Radio"/> objects the SmartLink list delivered, kept
        /// by serial. For a radio that is ONLY remote this is the same object
        /// <see cref="myRadioList"/> holds. For a DUAL-HOMED radio it is the only
        /// copy of the WAN identity that survives:
        /// <see cref="wanRadioListReceivedHandler"/> merges the server's fields
        /// into the already-known LAN object and drops the WAN one on the floor,
        /// so without this dictionary "connect over SmartLink even though it is
        /// local" has nothing to connect with.
        /// <para>Static because the selector builds a fresh FlexBase per open and
        /// the WAN session outlives it. Cleared whenever the session is cycled or
        /// discovery is force-restarted, so a stale handle can never be dialled.</para>
        /// </summary>
        private struct WanRadioEntry
        {
            public Radio Radio;
            /// <summary>
            /// Email of the SmartLink account whose list delivered this radio,
            /// or "" when unknown (a WAN radio that arrived outside an
            /// attributed list). Sprint 35 Track K (#259): with one session
            /// held per account, a fresh list from account A is the FULL truth
            /// about A and says NOTHING about B — sweeps must scope to this.
            /// </summary>
            public string AccountId;
        }

        private static readonly Dictionary<string, WanRadioEntry> _wanRadiosBySerial =
            new Dictionary<string, WanRadioEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _wanRadiosLock = new object();

        /// <summary>True when the SmartLink list has this radio right now.</summary>
        private static bool WanKnows(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            lock (_wanRadiosLock) { return _wanRadiosBySerial.ContainsKey(serial); }
        }

        private static void RememberWanRadio(Radio r, string accountId = null)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Serial)) return;
            lock (_wanRadiosLock)
            {
                // A caller without attribution (the API-added path) must not
                // erase attribution an attributed list already established.
                if (accountId == null
                    && _wanRadiosBySerial.TryGetValue(r.Serial, out var existing)
                    && !string.IsNullOrEmpty(existing.AccountId))
                {
                    accountId = existing.AccountId;
                }
                _wanRadiosBySerial[r.Serial] = new WanRadioEntry { Radio = r, AccountId = accountId ?? "" };
            }
        }

        private static void ForgetWanRadios(string reason)
        {
            lock (_wanRadiosLock)
            {
                if (_wanRadiosBySerial.Count == 0) return;
                Tracing.TraceLine($"ForgetWanRadios ({reason}): dropping {_wanRadiosBySerial.Count} WAN radio handle(s)", TraceLevel.Info);
                _wanRadiosBySerial.Clear();
            }
        }

        /// <summary>
        /// Drop only the WAN handles that belong to ONE account — cycling
        /// account A's session must not throw away the live handles account
        /// B's still-open session delivered. Unattributed handles ("" account)
        /// are dropped too: with the cycled session gone they cannot be
        /// re-verified, and a stale handle must never be dialled.
        /// </summary>
        private static void ForgetWanRadiosForAccount(string accountId, string reason)
        {
            lock (_wanRadiosLock)
            {
                var doomed = _wanRadiosBySerial
                    .Where(kv => string.IsNullOrEmpty(kv.Value.AccountId)
                        || string.Equals(kv.Value.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                if (doomed.Count == 0) return;
                Tracing.TraceLine($"ForgetWanRadiosForAccount ({reason}): dropping {doomed.Count} WAN radio handle(s) for {accountId}", TraceLevel.Info);
                foreach (var serial in doomed) _wanRadiosBySerial.Remove(serial);
            }
        }

        /// <summary>
        /// The account whose SmartLink list owns this serial, or "" when
        /// unknown. Lets the connect path dial a radio through the session of
        /// the account that can actually broker it.
        /// </summary>
        private static string GetWanAccountForSerial(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return "";
            lock (_wanRadiosLock)
            {
                return _wanRadiosBySerial.TryGetValue(serial, out var entry) ? entry.AccountId ?? "" : "";
            }
        }

        /// <summary>
        /// True when this WAN serial is attributable to the given account —
        /// including the unattributed ("") case, which can only exist in a
        /// world where a single account is in play and so belongs to it.
        /// </summary>
        private static bool WanRadioBelongsToAccount(string serial, string accountId)
        {
            var owner = GetWanAccountForSerial(serial);
            return string.IsNullOrEmpty(owner)
                || string.Equals(owner, accountId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The WAN-path <see cref="Radio"/> for this serial, or null when the
        /// SmartLink list does not currently carry it.
        /// </summary>
        private static Radio findWanRadio(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return null;
            lock (_wanRadiosLock)
            {
                return _wanRadiosBySerial.TryGetValue(serial, out var entry)
                    && entry.Radio != null && entry.Radio.IsWan ? entry.Radio : null;
            }
        }

        /// <summary>
        /// True when this radio is reachable both on the local network and
        /// through the current SmartLink account — the case where the operator
        /// gets to choose the path.
        /// </summary>
        public bool IsDualHomed(string serial)
        {
            var (lan, wan) = RadioAvailability(serial);
            return lan && wan;
        }

        /// <summary>
        /// Which paths reach this radio right now. The RadioRemoved event says a
        /// radio left without saying which home it left, so the selector asks
        /// this before deciding whether "went offline" is even true — a
        /// dual-homed radio dropping off the LAN is still perfectly reachable
        /// through SmartLink.
        /// </summary>
        public (bool lan, bool wan) RadioAvailability(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return (false, false);
            bool wan = findWanRadio(serial) != null;
            try
            {
                // ToList first: myRadioList is appended to from the discovery
                // thread, and this runs on the UI thread every time a radio
                // leaves. An enumeration that throws here would take the picker
                // down over a bookkeeping question.
                bool lan = myRadioList.ToList().Any(x => x.Serial == serial && !x.IsWan);
                return (lan, wan);
            }
            catch (InvalidOperationException ex)
            {
                Tracing.TraceLine($"RadioAvailability({serial}): list changed mid-read: {ex.Message}", TraceLevel.Warning);
                return (false, wan);
            }
        }

        private void radioAddedHandler(Radio r)
        {
            Tracing.TraceLine("radioAddedHandler:" + r.Serial, TraceLevel.Info);
            // Ignore entries without a serial; SmartLink returns unusable shells before auth completes.
            if (string.IsNullOrWhiteSpace(r.Serial))
            {
                Tracing.TraceLine("radioAddedHandler: ignored radio with empty serial", TraceLevel.Warning);
                return;
            }
            myRadioList.Add(r);
            if (r.IsWan) RememberWanRadio(r);
            RaiseRadioFound(null, BuildRigData(r));
        }

        /// <summary>
        /// Describe a radio for the selector, filling in BOTH homes. The
        /// discovering path tells us one of them; the WAN dictionary answers for
        /// the other, and it is consulted every time because a LAN radio
        /// re-announces itself long after the SmartLink list has landed.
        /// </summary>
        private static RigData BuildRigData(Radio r)
        {
            var rd = new RigData();
            rd.Name = string.IsNullOrWhiteSpace(r.Nickname) ? "Unknown" : r.Nickname;
            rd.ModelName = string.IsNullOrWhiteSpace(r.Model) ? "Unknown" : r.Model;
            rd.Serial = r.Serial;
            // Remote keeps its existing meaning — the path this row connects by
            // unless the user says otherwise — and LAN still wins, because it is
            // the better path.
            rd.Remote = r.IsWan;
            rd.LanAvailable = !r.IsWan;
            rd.WanAvailable = r.IsWan || WanKnows(r.Serial);
            return rd;
        }
        internal static bool _apiInit;

        /// <summary>
        /// True while a forced rescan is tearing down FlexLib's radio list.
        /// API.CloseSession fires RadioRemoved for every known radio; those
        /// are rescan bookkeeping, not radios going offline, and announcing
        /// "went offline" for the whole list on every selector open would be
        /// noise and a lie.
        /// </summary>
        private static bool _suppressApiRemovals;

        internal void apiInit(bool force = false)
        {
            Tracing.TraceLine("apiInit:" + force.ToString(), TraceLevel.Info);
            if (force)
            {
                // Always initialize.
                if (_apiInit)
                {
                    _suppressApiRemovals = true;
                    try { API.CloseSession(); }
                    finally { _suppressApiRemovals = false; }
                    // Force init.
                    _apiInit = false;
                    // The WAN handles belonged to the session we just tore down.
                    ForgetWanRadios("forced apiInit");
                }
            }
            // Won't init if !force and already inited.
            if (!_apiInit)
            {
                API.RadioAdded -= radioAddedHandler;
                API.RadioAdded += radioAddedHandler;
                // FlexLib's RadioListMaid evicts a LAN radio after
                // RADIOLIST_TIMEOUT_SECONDS (17s) without a discovery
                // announcement — the powered-off case. Nothing subscribed to
                // this before 2026-08-06, so a dark radio sat in the selector
                // forever (Noel's 8600 power-cycle test).
                API.RadioRemoved -= apiRadioRemovedHandler;
                API.RadioRemoved += apiRadioRemovedHandler;
                API.Init();
                _apiInit = true;
            }
        }

        private void apiRadioRemovedHandler(Radio r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Serial)) return;
            if (_suppressApiRemovals)
            {
                Tracing.TraceLine("apiRadioRemovedHandler: suppressed (rescan teardown) " + r.Serial, TraceLevel.Info);
                return;
            }
            Tracing.TraceLine($"apiRadioRemovedHandler: {r.Serial} ({r.Nickname}) gone from discovery — removing", TraceLevel.Info);
            var mine = myRadioList.FirstOrDefault(x => x.Serial == r.Serial);
            if (mine != null) myRadioList.Remove(mine);
            RaiseRadioRemoved(this, r.Serial, r.Nickname ?? "");
        }

        /// <summary>
        /// Re-raise <see cref="RadioFound"/> for every radio discovery has
        /// already seen.
        ///
        /// **Why a replay rather than a getter.** Discovery is an event stream,
        /// and anything that subscribes late has simply missed what came
        /// before. Until now that did not matter, because the radio selector
        /// subscribed before discovery started. Once discovery runs BEFORE the
        /// selector exists - so the operator meets a settled list instead of
        /// listening to one assemble itself - the selector arrives late by
        /// design and needs the backlog.
        ///
        /// Replaying through the same event keeps ONE code path into the
        /// selector's roster. A parallel "give me the current list" accessor
        /// would be a second path that has to be kept in step with the first,
        /// and the two would drift the first time either changed.
        ///
        /// Safe to call more than once: the selector keys rows by serial, so a
        /// replayed radio updates its row rather than adding another.
        /// </summary>
        public void ReplayDiscoveredRadios()
        {
            // ToList first - myRadioList is appended to from the discovery
            // thread while we walk it.
            var known = myRadioList.ToList();
            Tracing.TraceLine(
                $"ReplayDiscoveredRadios: replaying {known.Count} radio(s)",
                TraceLevel.Info);

            foreach (var r in known)
            {
                if (string.IsNullOrWhiteSpace(r.Serial)) continue;
                RaiseRadioFound(null, BuildRigData(r));
            }
        }

        private Radio findRadioInAPI(string serial)
        {
            foreach (Radio r in myRadioList)
            {
                if (r.Serial == serial) return r;
            }
            return null;
        }

        /// <summary>
        /// Resolve the <see cref="Radio"/> object a connect should actually use.
        /// <para><paramref name="preferWan"/> true means the operator explicitly
        /// asked for the SmartLink path on a radio that may also be local; it
        /// returns null rather than quietly handing back the LAN object, because
        /// connecting locally after the user chose SmartLink would be a lie the
        /// UI could not detect.</para>
        /// <para>preferWan false prefers a non-WAN entry when the list holds
        /// both, so "local network" means local network even if the SmartLink
        /// list happened to arrive first.</para>
        /// </summary>
        private Radio findRadioForConnect(string serial, bool preferWan)
        {
            if (preferWan) return findWanRadio(serial);
            foreach (Radio r in myRadioList)
            {
                if (r.Serial == serial && !r.IsWan) return r;
            }
            return findRadioInAPI(serial);
        }

        /// <summary>
        /// Public wrapper for findRadioInAPI — used by ConnectionTester to poll
        /// for radio discovery in ManualSimulation mode.
        /// </summary>
        public Radio FindRadioBySerial(string serial) => findRadioInAPI(serial);

        /// <summary>
        /// Provide a list of local radios through the RadioFound event.
        /// </summary>
        public void LocalRadios()
        {
            Tracing.TraceLine("LocalRadios", TraceLevel.Info);
            apiInit(true);
        }

        /// <summary>
        /// Provide a list of remote radios through the RadioFound event.
        /// </summary>
        public void RemoteRadios()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine("RemoteRadios: BEGIN", TraceLevel.Info);
            apiInit(); // don't force the init.
            Tracing.TraceLine($"RemoteRadios: apiInit done ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            bool stat = setupRemote();
            sw.Stop();
            Tracing.TraceLine($"RemoteRadios: END setupRemote={stat} (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
        }

        /// <summary>
        /// True when the app-global SmartLink session is connected. Distinct
        /// from <see cref="IsConnected"/>, which is RADIO-level and stays
        /// false after a successful radio-LIST pass — use this one as the
        /// "did the remote pass succeed" signal.
        /// </summary>
        public bool IsSmartLinkSessionLive =>
            Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.IsConnected == true;

        /// <summary>
        /// Refresh the remote radio list. History, because the reason changed
        /// (#259): this used to be the ONLY way to see a current list — the
        /// belief was "the server sends the radio list once per TLS session",
        /// so refresh meant kill the session and dial a new one. The
        /// 2026-08-25 capture disproved the belief: the server pushes updated
        /// lists for as long as a registered session lives (four pushes in 145
        /// seconds), and held-open per-account sessions now consume them, so
        /// the roster is already current without anyone pressing anything.
        /// The cycle is kept as the operator's honest escape hatch — an
        /// explicit refresh still tears down the CURRENT account's session and
        /// dials fresh, which recovers a session that is connected but wedged
        /// (TLS up, server gone quiet) in a way no amount of waiting would.
        /// Scoped to the current account: other accounts' held sessions and
        /// their radios are not touched, which is what keeps a refresh from
        /// re-ranking the whole list by whichever account was refreshed last.
        /// </summary>
        public void RefreshRemoteRadios()
        {
            Tracing.TraceLine("RefreshRemoteRadios: BEGIN", TraceLevel.Info);
            CycleWanSession("user refresh");
            RemoteRadios();
        }

        /// <summary>
        /// Disconnect the CURRENT account's WAN session so the next
        /// ConnectToSmartLink dials a fresh one. Also clears this instance's
        /// one-shot list latch so the fresh list is waited for rather than a
        /// stale cache accepted. Sprint 35 Track K: scoped — only the active
        /// session's account forgets its WAN handles; every other held
        /// session keeps delivering presence for its own account throughout.
        /// </summary>
        private void CycleWanSession(string reason)
        {
            string cycledAccount = null;
            try
            {
                var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
                if (session != null)
                {
                    cycledAccount = session.AccountId;
                    Tracing.TraceLine($"CycleWanSession ({reason}): disconnecting session {session.SessionId} for a fresh radio list", TraceLevel.Info);
                    Radios.SmartLink.SmartLinkServices.Coordinator.DisconnectSession(session.SessionId);
                }
                else
                {
                    Tracing.TraceLine($"CycleWanSession ({reason}): no active session; next connect dials fresh", TraceLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"CycleWanSession ({reason}): {ex.Message}", TraceLevel.Error);
            }
            radios = null;
            wanListReceived = false;
            // The WAN Radio objects belong to the session being cycled; a fresh
            // list repopulates them. Keeping them would let a path-choice
            // connect dial a handle the server has already forgotten. With no
            // session to name an account, fall back to forgetting everything —
            // a stale handle is worse than a briefly emptier list.
            if (!string.IsNullOrEmpty(cycledAccount))
                ForgetWanRadiosForAccount(cycledAccount, reason);
            else
                ForgetWanRadios(reason);
        }

        internal Radio theRadio;
        private FeatureLicense trackedFeatureLicense;
        public event EventHandler FeatureLicenseChanged;
        public string RadioModel => theRadio?.Model ?? string.Empty;
        /// <summary>
        /// Gets the connected radio's nickname (user-assigned name), or empty if not connected.
        /// </summary>
        public string RadioNickname => theRadio?.Nickname ?? string.Empty;

        /// <summary>
        /// Rename the connected radio. The name lives on the radio itself —
        /// discovery, SmartLink, and every client see it — so this needs a live
        /// connection. The per-radio profile's nickname mirror is refreshed in
        /// the same motion so the Settings picker shows the new name offline.
        /// </summary>
        public bool RenameRadio(string newName)
        {
            var radio = theRadio;
            if (radio == null || string.IsNullOrWhiteSpace(newName)) return false;

            Tracing.TraceLine($"RenameRadio: '{radio.Nickname}' -> '{newName}'", TraceLevel.Info);
            radio.Nickname = newName.Trim();

            var profile = RadioConfig.LoadForRadio(radio.Serial);
            // A rename through JJ Flex is a deliberate choice: record it in
            // both the observation mirror AND the choice field, so it keeps
            // winning if the hardware name later diverges.
            profile.Nickname = newName.Trim();
            profile.UserNickname = newName.Trim();
            profile.SaveForRadio(radio.Serial);

            RefreshAutoConnectDisplayName(radio.Serial, newName.Trim());
            return true;
        }

        /// <summary>
        /// After a rename, keep the auto-connect config's stored display name in
        /// step — serial is its key so nothing breaks without this, but startup
        /// speech ("Connecting to X") reads the stored name, and hearing the old
        /// name every morning after renaming would look like the rename failed.
        /// Best-effort: the auto-connect file is per-operator under the base
        /// config directory (the parent of the Radios directory Callouts hands
        /// us), and a radio that is not the auto-connect radio needs nothing.
        /// </summary>
        private void RefreshAutoConnectDisplayName(string serial, string newName)
        {
            try
            {
                string radiosDir = Callouts?.ConfigDirectory ?? string.Empty;
                string opName = Callouts?.OperatorName ?? string.Empty;
                if (radiosDir.Length == 0 || opName.Length == 0) return;

                // Callouts.ConfigDirectory is BaseConfigDir + "\Radios" (globals.vb
                // openTheRadio); AutoConnectConfig lives in BaseConfigDir itself.
                string baseDir = Path.GetDirectoryName(radiosDir.TrimEnd('\\')) ?? string.Empty;
                if (baseDir.Length == 0) return;

                var auto = AutoConnectConfig.Load(baseDir, opName);
                if (!auto.Enabled || auto.RadioSerial != serial || auto.RadioName == newName) return;

                auto.RadioName = newName;
                auto.Save(baseDir, opName);
                Tracing.TraceLine($"RenameRadio: auto-connect display name updated to '{newName}'", TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RenameRadio: auto-connect display name not updated: {ex.Message}", TraceLevel.Error);
            }
        }
        public bool NoiseReductionLicenseReported => theRadio?.FeatureLicense?.LicenseFeatNoiseReduction != null;
        /// <summary>
        /// True only when NR license is positively confirmed as enabled.
        /// False when license object is null (never reported) OR when explicitly disabled.
        /// Use this for gating — "if NOT licensed, block" is the safe default.
        /// </summary>
        public bool NoiseReductionLicensed => theRadio?.FeatureLicense?.LicenseFeatNoiseReduction?.FeatureEnabled == true;
        /// <summary>
        /// True only on radios with hardware capable of Neural/Spectral NR (8000 series, Aurora).
        /// 6000 series radios lack the DSP hardware even if a license is purchased.
        /// </summary>
        /// <summary>
        /// True when the radio hardware supports advanced NR (NRF, NRS, RNN).
        /// Only available on 8000-series and Aurora — requires their DSP hardware.
        /// 6000 series has Legacy NR and ANF only.
        /// </summary>
        public bool NeuralNRHardwareSupported
        {
            get
            {
                string model = RadioModel;
                return model.StartsWith("FLEX-8", StringComparison.OrdinalIgnoreCase) ||
                       model.StartsWith("AU-5", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Legacy alias — use NeuralNRHardwareSupported for Neural NR gating.
        /// Spectral NR is available on all models (subscription-gated, not hardware-gated).
        /// </summary>
        public bool AdvancedNRHardwareSupported => NeuralNRHardwareSupported;

        // --- PC-side audio processing pipeline ---

        /// <summary>
        /// Optional delegate for PC-side audio processing (RNNoise, spectral subtraction, etc.).
        /// Set by the UI layer (JJFlexWpf) which owns the processing pipeline.
        /// Forwarded to JJAudioStream.PostDecodeProcessor when the audio channel is created.
        /// Works on ALL radios — processing runs on the PC, not on radio hardware.
        /// </summary>
        private Action<float[]>? _audioPostProcessor;
        public Action<float[]>? AudioPostProcessor
        {
            get => _audioPostProcessor;
            set
            {
                _audioPostProcessor = value;
                // Forward to active audio stream if one exists
                if (opusOutputChannel?.PortAudioStream != null)
                    opusOutputChannel.PortAudioStream.PostDecodeProcessor = value;
            }
        }

        public bool DiversityLicenseReported => theRadio?.FeatureLicense?.LicenseFeatDivEsc != null;
        public bool DiversityLicensed => theRadio?.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled == true;
        public bool DiversityHardwareSupported => theRadio?.DiversityIsAllowed == true;
        /// <summary>
        /// True when the radio has an ATU (antenna tuner unit) present and enabled.
        /// FLEX-6300 has optional ATU — this detects actual hardware presence.
        /// </summary>
        public bool HasATU => theRadio?.ATUEnabled == true;

        /// <summary>
        /// True when the radio reported an ATU as physically FITTED, which is a
        /// different question from <see cref="HasATU"/> ("allowed to be used").
        ///
        /// <para>Sprint 31 Track R. The radio parses atu_present separately from
        /// the enable flag, and the two disagree in a real case worth telling
        /// the operator about: a tuner that exists but is switched off. Note
        /// that both are plain bools starting false, so a false here means
        /// "not reported", NOT "proven absent" — callers must not claim
        /// otherwise.</para>
        /// </summary>
        public bool ATUHardwarePresent => theRadio?.ATUPresent == true;

        /// <summary>
        /// True while the antenna tuner is actively running a tune cycle.
        /// <para>Exists because a tune cycle is the one time high reflected
        /// power is <b>expected</b>. The tuner transmits into a deliberately
        /// bad match and walks its way to a good one, so any rule that judges
        /// standing wave ratio or reflected power has to stand down while this
        /// is true or it will report a fault every single time the operator
        /// tunes up.</para>
        /// <para>Read live from the radio rather than from
        /// <see cref="FlexTunerOn"/>, which is a latch set by our own code and
        /// is known to stick true when the tuner is bypassed.</para>
        /// </summary>
        public bool ATUTuneInProgress => theRadio?.ATUTuneStatus == ATUTuneStatus.InProgress;

        // --- SmartLink manual port forwarding (Sprint 27 preview) ---

        /// <summary>
        /// The fixed TCP (TLS) port the radio itself listens on for SmartLink
        /// connections, on its LAN address. Router rule: external TCP port →
        /// radio's LAN IP, port 4994. Source: FlexRadio's own SmartLink
        /// port-forwarding setup guidance, confirmed live 2026-08-14 (Don's
        /// 6300: external 25678 TCP → 192.168.50.100:4994 answered). Distinct
        /// from the LAN-discovery path's TCP 4992 / UDP 4991 — do not
        /// generalize one set into the other.
        /// </summary>
        public const int SmartLinkRadioTlsPort = 4994;

        /// <summary>
        /// The fixed UDP port the radio itself listens on for SmartLink audio
        /// and data, on its LAN address. Router rule: external UDP port →
        /// radio's LAN IP, port 4993. Same sourcing as
        /// <see cref="SmartLinkRadioTlsPort"/>.
        /// </summary>
        public const int SmartLinkRadioUdpPort = 4993;

        /// <summary>True when the radio has manual port forwarding configured.</summary>
        public bool PortForwardingEnabled => theRadio?.IsPortForwardOn ?? false;

        /// <summary>The external (public) TCP port the radio advertises for SmartLink, or -1 if none.</summary>
        public int PortForwardingTcpPort => theRadio?.PublicTlsPort ?? -1;

        /// <summary>The external (public) UDP port the radio advertises for SmartLink, or -1 if none.</summary>
        public int PortForwardingUdpPort => theRadio?.PublicUdpPort ?? -1;

        /// <summary>
        /// Configure SmartLink manual port forwarding on the radio's firmware.
        /// The setting persists in the radio until changed again. Works when connected
        /// locally (LAN) or remotely. Radio must be connected.
        ///
        /// <para><paramref name="tcpPort"/> and <paramref name="udpPort"/> are the
        /// EXTERNAL (router WAN side) ports the radio advertises to SmartLink —
        /// the underlying command is <c>wan set public_tls_port / public_udp_port</c>.
        /// The radio does NOT listen on these ports. It always listens on its LAN
        /// address at TCP <see cref="SmartLinkRadioTlsPort"/> (4994) and UDP
        /// <see cref="SmartLinkRadioUdpPort"/> (4993), so the router rules are:
        /// external <paramref name="tcpPort"/> TCP → radio LAN IP port 4994, and
        /// external <paramref name="udpPort"/> UDP → radio LAN IP port 4993.</para>
        ///
        /// <para>This comment previously claimed the radio listens on the ports you
        /// pass in. That was wrong and misled a live debugging session (2026-08-14);
        /// see docs/planning/for-noel/2026-08-14-don-6300-rf-truth-test.md.</para>
        /// </summary>
        public bool SetSmartLinkPortForwarding(bool enabled, int tcpPort, int udpPort)
        {
            if (theRadio == null)
            {
                Tracing.TraceLine("SetSmartLinkPortForwarding: no radio connected", TraceLevel.Warning);
                return false;
            }
            try
            {
                if (enabled)
                    theRadio.WanSetForwardedPorts(true, tcpPort, udpPort);
                else
                    theRadio.WanSetForwardedPorts(false, -1, -1);
                Tracing.TraceLine($"SetSmartLinkPortForwarding: enabled={enabled} tcp={tcpPort} udp={udpPort}", TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SetSmartLinkPortForwarding failed: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// The radio's REM ON remote-power RCA jack enable (Track C, settings
        /// that stick). Radio-persistent: <c>radio set remote_on_enabled</c>.
        /// Hardware prerequisite the UI must state wherever this is offered:
        /// enabling it does nothing unless the RCA jack is actually wired to a
        /// relay. False when no radio is connected.
        /// </summary>
        public bool RemoteOnEnabled
        {
            get => theRadio?.RemoteOnEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("RemoteOnEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.RemoteOnEnabled = value;
                Tracing.TraceLine($"RemoteOnEnabled set: {value}", TraceLevel.Info);
            }
        }

        #region Transmit control settings (TX Controls dialog)

        // Sprint 33 Track J, #109. TXControlsDialog was built in Sprint 9 Track B
        // against fourteen FlexLib properties, but only RemoteOnEnabled above
        // ever got a wrapper, and theRadio is internal — so the app side had no
        // way to reach the other thirteen and the dialog was never wired up.
        // Same shape as RemoteOnEnabled: read through to the radio, return a
        // benign default when nothing is connected, trace every write.
        //
        // These are radio-persistent settings, not per-session state. The RCA
        // and ACC jacks they control only do anything if something is physically
        // wired to them, which the dialog cannot know and does not claim.

        /// <summary>TX request input on the RCA jack. False when no radio.</summary>
        public bool TXReqRCAEnabled
        {
            get => theRadio?.TXReqRCAEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqRCAEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqRCAEnabled = value;
                Tracing.TraceLine($"TXReqRCAEnabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX request RCA polarity. True = active high, false = active low.</summary>
        public bool TXReqRCAPolarity
        {
            get => theRadio?.TXReqRCAPolarity ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqRCAPolarity set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqRCAPolarity = value;
                Tracing.TraceLine($"TXReqRCAPolarity set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX request input on the accessory connector. False when no radio.</summary>
        public bool TXReqACCEnabled
        {
            get => theRadio?.TXReqACCEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqACCEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqACCEnabled = value;
                Tracing.TraceLine($"TXReqACCEnabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX request ACC polarity. True = active high, false = active low.</summary>
        public bool TXReqACCPolarity
        {
            get => theRadio?.TXReqACCPolarity ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqACCPolarity set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqACCPolarity = value;
                Tracing.TraceLine($"TXReqACCPolarity set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX1 RCA output enable. False when no radio.</summary>
        public bool TX1Enabled
        {
            get => theRadio?.TX1Enabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX1Enabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX1Enabled = value;
                Tracing.TraceLine($"TX1Enabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX1 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX1Delay
        {
            get => theRadio?.TX1Delay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX1Delay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX1Delay = value;
                Tracing.TraceLine($"TX1Delay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX2 RCA output enable. False when no radio.</summary>
        public bool TX2Enabled
        {
            get => theRadio?.TX2Enabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX2Enabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX2Enabled = value;
                Tracing.TraceLine($"TX2Enabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX2 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX2Delay
        {
            get => theRadio?.TX2Delay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX2Delay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX2Delay = value;
                Tracing.TraceLine($"TX2Delay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX3 RCA output enable. False when no radio.</summary>
        public bool TX3Enabled
        {
            get => theRadio?.TX3Enabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX3Enabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX3Enabled = value;
                Tracing.TraceLine($"TX3Enabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX3 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX3Delay
        {
            get => theRadio?.TX3Delay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX3Delay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX3Delay = value;
                Tracing.TraceLine($"TX3Delay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX accessory-connector output enable. False when no radio.</summary>
        public bool TXACCEnabled
        {
            get => theRadio?.TXACCEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXACCEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXACCEnabled = value;
                Tracing.TraceLine($"TXACCEnabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX accessory-connector output delay in milliseconds. Zero when no radio.</summary>
        public int TXACCDelay
        {
            get => theRadio?.TXACCDelay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXACCDelay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXACCDelay = value;
                Tracing.TraceLine($"TXACCDelay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>Hardware ALC (automatic level control) enable. False when no radio.</summary>
        public bool HWAlcEnabled
        {
            get => theRadio?.HWAlcEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("HWAlcEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.HWAlcEnabled = value;
                Tracing.TraceLine($"HWAlcEnabled set: {value}", TraceLevel.Info);
            }
        }

        #endregion

        /// <summary>
        /// Sprint 27 Track A / Phase A.3 — true when a SmartLink account is
        /// currently bound to this connection. UI code that persists per-
        /// account preferences must gate on this.
        /// </summary>
        public bool HasCurrentSmartLinkAccount => _currentAccount != null;

        /// <summary>
        /// Email of the SmartLink account currently bound to this connection,
        /// or empty string if none. Distinct from <see cref="CurrentSmartLinkEmail"/>
        /// only in that this exists on the same surface as
        /// <see cref="SaveCurrentAccountListenPort"/> for UI discoverability.
        /// </summary>
        public string CurrentSmartLinkAccountEmail => _currentAccount?.Email ?? string.Empty;

        /// <summary>
        /// Sprint 27 Track F. Current account's SmartLink connection mode, or
        /// null if no SmartLink account is bound. UI gates its mode selector
        /// on this being non-null; individual tier options enable/disable
        /// based on the enum value.
        /// </summary>
        public SmartLinkConnectionMode? CurrentAccountConnectionMode => _currentAccount?.ConnectionMode;

        /// <summary>
        /// Sprint 27 Track F — persists the SmartLink connection mode on the
        /// account currently bound to this connection. Saves to disk. Returns
        /// false when no account is bound. Note: this sets only the preference;
        /// actual UPnP / hole-punch behavior is applied on the next session
        /// connect (see the Track B.3 and F.2 hooks in FlexBase.Connect /
        /// sendRemoteConnect).
        /// </summary>
        public bool SaveCurrentAccountConnectionMode(SmartLinkConnectionMode mode)
        {
            if (_currentAccount == null)
            {
                Tracing.TraceLine("SaveCurrentAccountConnectionMode: no current account; skipping", TraceLevel.Warning);
                return false;
            }
            _currentAccount.ConnectionMode = mode;
            AccountManager.SaveAccounts();
            Tracing.TraceLine($"SaveCurrentAccountConnectionMode: saved mode={mode} for account={_currentAccount.Email}", TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Sprint 27 Track A / Phase A.3 — persists a SmartLink listen-port
        /// preference on the account currently bound to this connection, and
        /// refreshes the in-memory account reference so the next auto-apply
        /// on connect (see <see cref="ApplyAccountPortPreferenceIfAny"/>) uses
        /// the new value. Pass null to clear the preference (revert to FlexLib
        /// default behavior). Returns false if no account is bound or the port
        /// is invalid. Validation is delegated to
        /// <see cref="SmartLinkAccountManager.IsValidPort"/> so the UI can
        /// check the same rule before calling.
        ///
        /// <para>QB Track C — meaning contract: from this build on, the value
        /// saved here carries exactly ONE meaning — the RADIO-side forwarded
        /// port (Tier 1), written by the Network tab's port-forward Apply and
        /// re-applied on connect. The client-side hole-punch port, which an
        /// older build also wrote into this same field, lives per-radio in
        /// <see cref="RadioConfig.FixedHolePunchPort"/> now. Legacy punch
        /// values already on disk are still honored by sendRemoteConnect's
        /// account fallback until a per-radio profile takes over.</para>
        /// </summary>
        public bool SaveCurrentAccountListenPort(int? port)
        {
            if (_currentAccount == null)
            {
                Tracing.TraceLine("SaveCurrentAccountListenPort: no current account; skipping", TraceLevel.Warning);
                return false;
            }
            if (!SmartLinkAccountManager.IsValidPort(port))
            {
                Tracing.TraceLine($"SaveCurrentAccountListenPort: invalid port {port}", TraceLevel.Warning);
                return false;
            }
            _currentAccount.ConfiguredListenPort = port;
            AccountManager.SaveAccounts();
            Tracing.TraceLine($"SaveCurrentAccountListenPort: saved port={port} for account={_currentAccount.Email}", TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Sprint 27 Track A / Phase A.2 — auto-apply the active SmartLink
        /// account's saved listen-port preference (Tier 1) to the connected
        /// radio. Called from the post-connect success branch of
        /// <see cref="Connect(string, bool)"/> for remote (WAN) rigs only.
        ///
        /// <para>Silent no-op (trace only) in all of the following cases:</para>
        /// <list type="bullet">
        /// <item>No account is bound to the current connection.</item>
        /// <item>The account has no <see cref="SmartLinkAccount.ConfiguredListenPort"/> set.</item>
        /// <item>The radio's firmware already reports the account's configured port on TCP and UDP (nothing to change).</item>
        /// </list>
        ///
        /// <para>Only calls <see cref="SetSmartLinkPortForwarding"/> when a
        /// change is actually needed, to avoid spurious radio commands on every
        /// reconnect.</para>
        /// </summary>
        /// <summary>
        /// Sprint 27 Track D. Most-recent NetworkTest report for the active
        /// session (any radio, latest timestamp). Null if nothing cached.
        /// Used by Settings > Network diagnostic Copy / Save buttons without
        /// forcing a fresh probe.
        /// </summary>
        public Radios.SmartLink.NetworkDiagnosticReport? MostRecentNetworkReport =>
            Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.MostRecentNetworkReport;

        /// <summary>
        /// Sprint 27 Track D. Filename (bare, no path) of the help doc most
        /// relevant to the current session status + last network report +
        /// account mode. Null when no help doc applies (e.g., session is
        /// Connected and working fine). Callers prepend
        /// <c>help\networking\</c> and the app base directory.
        /// </summary>
        public string? CurrentHelpDocFileName
        {
            get
            {
                var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
                var status = session?.Status ?? Radios.SmartLink.SessionStatus.Disconnected;
                var report = session?.MostRecentNetworkReport;
                var mode = CurrentAccountConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly;
                return Radios.SmartLink.SessionStatusMessages.HelpDocFor(status, report, mode);
            }
        }

        /// <summary>
        /// Sprint 27 Track C / Phase C.3 — user-facing entry point for the
        /// "Test network" button in Settings. Runs a NetworkTest probe
        /// against the currently-connected radio via the active session's
        /// runner. Returns null if no session is active or no radio is
        /// connected (UI should display "no radio connected" in that case,
        /// same pattern as Apply/TestPort buttons). <paramref name="forceRefresh"/>
        /// true bypasses the runner's cache so the user always gets fresh data
        /// when they click the button.
        /// </summary>
        public async System.Threading.Tasks.Task<Radios.SmartLink.NetworkDiagnosticReport?> RunNetworkDiagnosticAsync(bool forceRefresh = true)
        {
            var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
            if (session == null)
            {
                Tracing.TraceLine("RunNetworkDiagnosticAsync: no SmartLink session; skipping", TraceLevel.Warning);
                return null;
            }

            // The probe only needs a serial and a SmartLink session — it asks Flex's
            // backend to look at the network from outside, which has nothing to do
            // with whether we currently hold a connection to the radio.
            //
            // Requiring a connected radio made the diagnostic useless in the one
            // case it exists for: you cannot connect, and you want to know why. So
            // fall back to the serial of whichever radio was selected, which is what
            // the discovery cache holds even when the connect attempt failed.
            string serial = theRadio?.Serial ?? SelectedRadioSerial;
            if (string.IsNullOrEmpty(serial))
            {
                Tracing.TraceLine("RunNetworkDiagnosticAsync: no radio serial available; skipping", TraceLevel.Warning);
                return null;
            }

            Tracing.TraceLine($"RunNetworkDiagnosticAsync: probing serial={serial} (connected={theRadio != null})", TraceLevel.Info);
            return await session.RunNetworkDiagnosticAsync(serial, forceRefresh).ConfigureAwait(false);
        }

        /// <summary>
        /// Sprint 27 Track C / Phase C.3 — fire-and-forget post-connect
        /// NetworkTest invocation. The session's NetworkTestRunner caches
        /// the result so a subsequent Settings "Test network" click or a
        /// post-disconnect heuristic sees the warm report instead of
        /// probing again. Any probe failure is swallowed here (logged only);
        /// UI consumers get the report via the runner's cache + event.
        /// </summary>
        private static void KickPostConnectNetworkTest(string serial)
        {
            var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
            if (session == null)
            {
                Tracing.TraceLine("KickPostConnectNetworkTest: no active session; skipping", TraceLevel.Info);
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var report = await session.RunNetworkDiagnosticAsync(serial).ConfigureAwait(false);
                    Tracing.TraceLine(
                        $"KickPostConnectNetworkTest: serial={serial} completed={report.ProbeCompleted} upnpTcp={report.UpnpTcpReachable} upnpUdp={report.UpnpUdpReachable} fwdTcp={report.ManualForwardTcpReachable} fwdUdp={report.ManualForwardUdpReachable} holePunch={report.NatSupportsHolePunch}",
                        TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"KickPostConnectNetworkTest: threw {ex.Message}", TraceLevel.Warning);
                }
            });
        }

        // Sprint 27 Track B — shared UPnP mapper instance (lazy). Per FlexBase
        // instance so each connection owns its own; the underlying COM object
        // is cheap to recreate and not worth pooling.
        private UPnPPortMapper? _upnpMapper;
        // Remember which ports we actually mapped so Disconnect can release them.
        private int? _upnpMappedTcpPort;
        private int? _upnpMappedUdpPort;

        /// <summary>
        /// Sprint 27 Track B / Phase B.3 — if the active account has Tier 2
        /// UPnP opt-in enabled and a configured port, ask the local router via
        /// UPnP to forward that port (TCP + UDP) to the radio's LAN IP.
        /// Silent no-op in all the following cases (trace only):
        /// no account, UPnP opt-in is false, no configured port, radio's IP
        /// is not a private / LAN address (we're roaming — UPnP would target
        /// the wrong router). Individual TCP / UDP failures don't disable the
        /// other; each is attempted independently and its result remembered
        /// for Disconnect cleanup.
        /// </summary>
        private void ApplyAccountUPnPPreferenceIfAny()
        {
            if (_currentAccount == null || _currentAccount.ConnectionMode < SmartLinkConnectionMode.ManualPlusUpnp)
            {
                Tracing.TraceLine("ApplyAccountUPnPPreferenceIfAny: UPnP tier not selected for this account", TraceLevel.Info);
                return;
            }
            if (_currentAccount.ConfiguredListenPort == null)
            {
                Tracing.TraceLine("ApplyAccountUPnPPreferenceIfAny: UPnP enabled but no port preference configured; skipping", TraceLevel.Warning);
                return;
            }
            if (theRadio == null) return;

            string radioIp = theRadio.IP?.ToString() ?? string.Empty;
            if (!IsPrivateIPv4(radioIp))
            {
                Tracing.TraceLine($"ApplyAccountUPnPPreferenceIfAny: radio IP '{radioIp}' is not a private LAN address; UPnP not applicable from this network (roaming). Relying on manual port forward only.", TraceLevel.Info);
                return;
            }

            int port = _currentAccount.ConfiguredListenPort.Value;
            _upnpMapper ??= new UPnPPortMapper();

            Tracing.TraceLine($"ApplyAccountUPnPPreferenceIfAny: attempting UPnP mapping port={port} radioIp={radioIp}", TraceLevel.Info);

            bool tcpOk = _upnpMapper.TryAddMapping(port, UPnPProtocol.Tcp, port, radioIp, "JJFlexRadio SmartLink TCP");
            if (tcpOk) _upnpMappedTcpPort = port;

            bool udpOk = _upnpMapper.TryAddMapping(port, UPnPProtocol.Udp, port, radioIp, "JJFlexRadio SmartLink UDP");
            if (udpOk) _upnpMappedUdpPort = port;

            Tracing.TraceLine($"ApplyAccountUPnPPreferenceIfAny: tcpMapped={tcpOk} udpMapped={udpOk}", TraceLevel.Info);
        }

        /// <summary>
        /// Sprint 27 Track B / Phase B.3 — release UPnP mappings we added in
        /// <see cref="ApplyAccountUPnPPreferenceIfAny"/>. Called from
        /// <see cref="Disconnect"/>. Safe to call when no mappings exist —
        /// the per-port trackers are nulled out on success and checked here.
        /// </summary>
        private void ReleaseUPnPMappingsIfAny()
        {
            if (_upnpMapper == null) return;
            if (_upnpMappedTcpPort is int tcp)
            {
                _upnpMapper.TryRemoveMapping(tcp, UPnPProtocol.Tcp);
                _upnpMappedTcpPort = null;
            }
            if (_upnpMappedUdpPort is int udp)
            {
                _upnpMapper.TryRemoveMapping(udp, UPnPProtocol.Udp);
                _upnpMappedUdpPort = null;
            }
        }

        /// <summary>
        /// Sprint 27 Track B — true only for RFC1918 IPv4 addresses (10/8,
        /// 172.16/12, 192.168/16). We gate UPnP attempts on this so roaming
        /// users don't have us punching holes in the wrong router; the
        /// user's manual port forward (configured from home) still works
        /// in that scenario.
        /// </summary>
        private static bool IsPrivateIPv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            if (!System.Net.IPAddress.TryParse(ip, out var addr)) return false;
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            byte[] b = addr.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        private void ApplyAccountPortPreferenceIfAny()
        {
            if (_currentAccount == null)
            {
                Tracing.TraceLine("ApplyAccountPortPreferenceIfAny: no current account; skipping", TraceLevel.Info);
                return;
            }
            int? preferred = _currentAccount.ConfiguredListenPort;
            if (!preferred.HasValue)
            {
                Tracing.TraceLine($"ApplyAccountPortPreferenceIfAny: account {_currentAccount.Email} has no preference; using FlexLib default", TraceLevel.Info);
                return;
            }
            if (theRadio == null)
            {
                Tracing.TraceLine("ApplyAccountPortPreferenceIfAny: theRadio is null; skipping", TraceLevel.Warning);
                return;
            }

            int port = preferred.Value;
            bool alreadyMatches =
                PortForwardingEnabled &&
                PortForwardingTcpPort == port &&
                PortForwardingUdpPort == port;
            if (alreadyMatches)
            {
                Tracing.TraceLine($"ApplyAccountPortPreferenceIfAny: radio already reports tcp={port} udp={port} enabled; no-op", TraceLevel.Info);
                return;
            }

            Tracing.TraceLine($"ApplyAccountPortPreferenceIfAny: account={_currentAccount.Email} applying port={port} (radio reports enabled={PortForwardingEnabled} tcp={PortForwardingTcpPort} udp={PortForwardingUdpPort})", TraceLevel.Info);
            bool ok = SetSmartLinkPortForwarding(true, port, port);
            if (!ok)
            {
                Tracing.TraceLine($"ApplyAccountPortPreferenceIfAny: SetSmartLinkPortForwarding returned false; preference not applied", TraceLevel.Warning);
            }
        }

        private Thread mainThread;

        // Track connection parameters for retry support.
        // Set in Connect() so the retry path in openTheRadio can recreate the connection.
        private string _connectedSerial;
        private bool _connectedLowBW;

        /// <summary>
        /// Serial number of the last radio we connected/attempted to connect to.
        /// Used by retry logic in openTheRadio to reconnect after SmartLink re-add failures.
        /// </summary>
        public string ConnectedSerial => _connectedSerial;

        /// <summary>
        /// Whether the last connection used low bandwidth mode.
        /// Used by retry logic in openTheRadio to reconnect after SmartLink re-add failures.
        /// </summary>
        public bool ConnectedLowBW => _connectedLowBW;

        /// <summary>
        /// Connect to the specified radio.
        /// </summary>
        /// <param name="serial">serial#</param>
        /// <param name="lowBW">true if low bandwidth connect</param>
        /// <param name="preferWanPath">
        /// True when the operator explicitly chose the SmartLink path for a radio
        /// that is ALSO on the local network. Default false keeps every existing
        /// caller on the historical resolution, where local wins because local is
        /// the better path.
        /// </param>
        public bool Connect(string serial, bool lowBW, bool preferWanPath = false)
        {
            Tracing.TraceLine($"Connect:{serial} preferWanPath={preferWanPath}", TraceLevel.Info);
            bool rv = true;

            // Save connection parameters for retry support
            _connectedSerial = serial;
            _connectedLowBW = lowBW;

            // Fresh attempt, fresh story — a stale failure report from a
            // previous attempt must never narrate this one.
            LastConnectFailureReport = null;

            // Fresh connection, fresh client identity: the previous session's
            // local-PTT observation must not vouch for this one.
            _lastAuthoritativeLocalPtt = null;

            ConnectionProfiler.Current?.RecordEvent("connect_begin", new Dictionary<string, object>
            {
                { "serial", serial },
                { "lowBW", lowBW },
                { "preferWanPath", preferWanPath }
            });

            theRadio = findRadioForConnect(serial, preferWanPath);
            if (theRadio == null)
            {
                // Deliberately no LAN fallback when the SmartLink path was asked
                // for: silently connecting the other way would make the selector's
                // spoken "over SmartLink" false, and this is the exact path Noel
                // uses to test WAN behaviour from inside his own shack.
                Tracing.TraceLine(
                    preferWanPath
                        ? "Connect: SmartLink path requested but the account's radio list has no such radio"
                        : "Connect didn't find radio",
                    TraceLevel.Error);
                RecordConnectFailure(new ConnectFailureReport
                {
                    Class = ConnectFailureClass.RadioNotFound,
                    SpokenSummary = preferWanPath
                        ? "The SmartLink radio list for this account no longer includes that radio. Refresh the radio list, or choose the local path if the radio is on your network."
                        : "The radio is no longer in the list of available radios. It may have gone offline — refresh the radio list and try again.",
                });
                return false;
            }

            ConnectionProfiler.Current?.RecordEvent("connect_radio_found", new Dictionary<string, object>
            {
                { "serial", theRadio.Serial },
                { "nickname", theRadio.Nickname ?? "" }
            });

            // Record this radio in the per-radio profile store no matter which
            // path the connect takes. The SmartLink path writes the same stub
            // at wan_connect_ready, but a LAN connect used to write nothing —
            // so a radio only ever used locally never appeared in the Settings
            // per-radio picker. Load/Save never throw.
            var knownRadioProfile = RadioConfig.LoadForRadio(theRadio.Serial);
            if (!string.IsNullOrEmpty(theRadio.Nickname))
            {
                knownRadioProfile.Nickname = theRadio.Nickname;
            }
            knownRadioProfile.SaveForRadio(theRadio.Serial);

            // add the handlers.
            theRadio.PropertyChanged += new PropertyChangedEventHandler(radioPropertyChangedHandler);
            theRadio.MessageReceived += new Radio.MessageReceivedEventHandler(messageReceivedHandler);
            theRadio.GUIClientAdded += new Radio.GUIClientAddedEventHandler(guiClientAdded);
            theRadio.GUIClientUpdated += new Radio.GUIClientUpdatedEventHandler(guiClientUpdated);
            theRadio.GUIClientRemoved += new Radio.GUIClientRemovedEventHandler(guiClientRemoved);
            theRadio.SliceAdded += new Radio.SliceAddedEventHandler(sliceAdded);
            theRadio.SliceRemoved += new Radio.SliceRemovedEventHandler(sliceRemoved);
            theRadio.PanadapterAdded += new Radio.PanadapterAddedEventHandler(panadapterAdded);
            theRadio.PanadapterRemoved += new Radio.PanadapterRemovedEventHandler(panAdapterRemoved);
            theRadio.WaterfallRemoved += new Radio.WaterfallRemovedEventHandler(waterfallRemoved);
            theRadio.TNFAdded += new Radio.TNFAddedEventHandler(tnfAdded);
            theRadio.TNFRemoved += new Radio.TNFRemovedEventHandler(tnfRemoved);
            theRadio.IsTNFSubscribed = true; // v2.0.19
            theRadio.TNFEnabled = true;
            theRadio.ForwardPowerDataReady += new Radio.MeterDataReadyEventHandler(forwardPowerData);
            theRadio.SWRDataReady += new Radio.MeterDataReadyEventHandler(sWRData);
            theRadio.MicDataReady += new Radio.MeterDataReadyEventHandler(micData);
            theRadio.MicPeakDataReady += new Radio.MeterDataReadyEventHandler(micPeakData);
            theRadio.CompPeakDataReady += new Radio.MeterDataReadyEventHandler(compPeakData);
            theRadio.PATempDataReady += new Radio.MeterDataReadyEventHandler(PATempDataHandler);
            theRadio.VoltsDataReady += new Radio.MeterDataReadyEventHandler(VoltsDataHandler);
            theRadio.HWAlcDataReady += new Radio.MeterDataReadyEventHandler(hwALCData);
            theRadio.ReflectedPowerDataReady += new Radio.MeterDataReadyEventHandler(reflectedPowerData);
            theRadio.PAEffDataReady += new Radio.MeterDataReadyEventHandler(paEffData);

            // Sprint 32 Track A: and now EVERY meter, not just the ten named
            // convenience events above. Fresh radio, fresh subscriptions. This
            // first pass usually finds the list still filling — meter
            // registration runs on after connect — which is exactly why the
            // reconcile is re-driven from every meter reading rather than
            // trusted once here.
            resetMeterInventory();
            syncMeterInventory();

            theRadio.TxBandSettingsAdded += new Radio.TxBandSettingsAddedEventHandler(txBandSettingsHandler);
            theRadio.RXRemoteAudioStreamAdded += new Radio.RXRemoteAudioStreamAddedEventHandler(opusOutputStreamAddedHandler);
            theRadio.TXRemoteAudioStreamAdded += new Radio.TXRemoteAudioStreamAddedEventHandler(opusInputStreamAddedHandler);
            // QB Track I — mirror the radio's transverter definitions (see the
            // Transverter (XVTR) Power region). Fresh radio, fresh list.
            lock (myXvtrs) myXvtrs.Clear();
            theRadio.XvtrAdded += new Radio.XvtrAddedEventHandler(xvtrAdded);
            theRadio.XvtrRemoved += new Radio.XvtrRemovedEventHandler(xvtrRemoved);
            HookFeatureLicense(theRadio);

            // Remembered so the network diagnostic still has something to probe
            // after a failed or dropped connect — which is precisely when someone
            // wants to run it.
            SelectedRadioSerial = theRadio.Serial;

            ConnectionProfiler.Current?.RecordEvent("connect_handlers_wired");

            theRadio.LowBandwidthConnect = lowBW;

            // Which stage failed decides what the failure MEANS: a
            // sendRemoteConnect failure is SmartLink/radio-side (the radio
            // never said "ready"); a Radio.Connect failure is transport
            // (the TCP/TLS path to the radio's port). Track it so the
            // failure report tells the right story.
            bool remoteHandshakeFailed = false;

            if (RemoteRig)
            {
                ConnectionProfiler.Current?.RecordEvent("send_remote_connect_begin");
                rv = sendRemoteConnect(theRadio);
                remoteHandshakeFailed = !rv;
                ConnectionProfiler.Current?.RecordEvent("send_remote_connect_end", new Dictionary<string, object>
                {
                    { "success", rv }
                });
            }

            if (rv)
            {
                Tracing.TraceLine($"Connect: IsWan={theRadio.IsWan} RequiresHolePunch={theRadio.RequiresHolePunch} PublicTlsPort={theRadio.PublicTlsPort} NegotiatedHolePunchPort={theRadio.NegotiatedHolePunchPort} IP={theRadio.IP}", TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("flexlib_connect_begin");
                rv = theRadio.Connect();
                ConnectionProfiler.Current?.RecordEvent("flexlib_connect_end", new Dictionary<string, object>
                {
                    { "success", rv }
                });
            }

            if (rv)
            {
                Tracing.TraceLine("Connect worked:" + theRadio.Serial, TraceLevel.Info);

                // 4.1-line cache writer: seed radioConnectionCacheV1.xml so the
                // 4.2-line discovery cascade (Rung 1a CachedLanIp) can short-circuit
                // UDP discovery on first launch after upgrade. See
                // memory/project_autoconnect_no_ip_dead_end.md for why this lives here.
                RecordConnectedRadioForCache();

                if (RemoteRig)
                {
                    //PCAudio = true;

                    // Sprint 27 Track A / Phase A.2 — auto-apply per-account
                    // listen-port preference (Tier 1). Silent no-op when the
                    // account has no preference, the radio's firmware already
                    // matches, or no account is bound to the session.
                    ApplyAccountPortPreferenceIfAny();

                    // Sprint 27 Track B / Phase B.3 — attempt UPnP mapping
                    // for Tier 2 opt-in accounts. Silent no-op when UPnP is
                    // not opted in, no port is configured, or the radio IP
                    // isn't a private (LAN) address. Failures fall back to
                    // Tier 1 behavior silently.
                    ApplyAccountUPnPPreferenceIfAny();

                    // Sprint 27 Track C / Phase C.3 — kick a NetworkTest in
                    // the background so the diagnostic report is warm by the
                    // time the user opens Settings or hits a problem. Fire-
                    // and-forget; the session's runner caches the result.
                    //
                    // NEVER on a hole-punched session (2026-08-05): the radio
                    // runs its port probes in response and tears down the live
                    // punched TCP session while doing so — Connected flipped
                    // false 5-60ms after TestConnectionResults in all three
                    // field-test sessions, across two builds. Port-forwarded
                    // and UPnP paths survive the probe; punch does not. Same
                    // family as registration/firmware needing a detached
                    // client. See research-queue "HOLE PUNCH" section.
                    if (theRadio.RequiresHolePunch)
                    {
                        Tracing.TraceLine(
                            "KickPostConnectNetworkTest: skipped — hole-punched session, radio-side probe would kill it",
                            TraceLevel.Info);
                    }
                    else
                    {
                        KickPostConnectNetworkTest(theRadio.Serial);
                    }
                }
                else
                {
                    // local audio on
                    theRadio.IsMuteLocalAudioWhenRemoteOn = false;
                }
            }
            else
            {
                Tracing.TraceLine("Connect failed", TraceLevel.Error);
                // Compose the evidence NOW, while the radio object still
                // carries the flags and address of the attempt. Callers
                // speak LastConnectFailureAdvice instead of a bare
                // "connection failed".
                try
                {
                    if (RemoteRig)
                    {
                        RecordConnectFailure(BuildRemoteConnectFailureReport(theRadio, remoteHandshakeFailed));
                    }
                    else
                    {
                        RecordConnectFailure(new ConnectFailureReport
                        {
                            Class = ConnectFailureClass.LocalConnectFailed,
                            SpokenSummary = $"Could not open the command channel to the radio at {theRadio?.IP?.ToString() ?? "its LAN address"}. The radio may have just powered off or changed address — refresh the radio list and try again.",
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Failure reporting must never turn a clean false into a throw.
                    Tracing.TraceLine($"Connect: failure-report composition threw: {ex.Message}", TraceLevel.Error);
                }
            }

            ConnectionProfiler.Current?.RecordEvent(rv ? "connect_success" : "connect_failed", new Dictionary<string, object>
            {
                { "serial", theRadio?.Serial ?? serial },
                { "connected", rv }
            });

            return rv;
        }

        /// <summary>
        /// Attempts to auto-connect to a radio based on saved configuration.
        /// For local radios: starts discovery, waits for radio, connects.
        /// For remote radios: authenticates silently, waits for radio, connects.
        /// </summary>
        /// <param name="config">Auto-connect configuration</param>
        /// <param name="timeoutMs">How long to wait for radio discovery (default 10 seconds)</param>
        /// <returns>True if connection succeeded, false otherwise</returns>
        public bool TryAutoConnect(AutoConnectConfig config, int timeoutMs = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (config == null || !config.ShouldAutoConnect)
            {
                Tracing.TraceLine("TryAutoConnect: no config or not enabled", TraceLevel.Info);
                return false;
            }

            Tracing.TraceLine($"TryAutoConnect: BEGIN {config.RadioName} ({config.RadioSerial}), remote={config.IsRemote}, timeout={timeoutMs}ms", TraceLevel.Info);
            if (!SuppressSpeech) ScreenReaderOutput.Speak(Lexicon.Get("connect.auto.connecting", ("radioName", config.RadioName)), VerbosityLevel.Critical, true);
            // AS prosign (wait / standing by) at connect-start — CW-flavored signal that we're
            // mid-handshake. Pair with BT which fires at connect-ready (MainWindow.PowerOn).
            if (ScreenReaderOutput.CwNotificationsEnabled) _ = ScreenReaderOutput.PlayCwAS?.Invoke();

            try
            {
                // The radio's stored path chain outranks the config's frozen
                // IsRemote bool: the chain is the operator's standing intent,
                // where IsRemote is a snapshot of wherever the radio happened
                // to live the day auto-connect was configured. A radio that
                // moved networks (Don's 6300 to Tony's; a rig at Field Day)
                // used to fail here on the stale path every startup with no
                // second try — this walk is the availability-expiry fix.
                var chain = new List<ConnectPathKind>();
                bool operatorPinned = false;
                try
                {
                    var stored = RadioConfig.LoadForRadio(config.RadioSerial).PathChain;
                    if (stored != null && stored.Count > 0)
                    {
                        chain.AddRange(stored);
                        operatorPinned = true;
                    }
                }
                catch (Exception cfgEx)
                {
                    Tracing.TraceLine($"TryAutoConnect: PathChain load failed: {cfgEx.Message}", TraceLevel.Warning);
                }
                if (!operatorPinned)
                {
                    // Derived chain: the configured path first, the other as
                    // fallback. An operator-stored one-entry chain means
                    // "this path only" and gets no fallback appended.
                    chain.Add(config.IsRemote ? ConnectPathKind.SmartLink : ConnectPathKind.Local);
                    chain.Add(config.IsRemote ? ConnectPathKind.Local : ConnectPathKind.SmartLink);
                }
                Tracing.TraceLine($"TryAutoConnect: path chain [{string.Join(", ", chain)}] pinned={operatorPinned}", TraceLevel.Info);

                for (int leg = 0; leg < chain.Count; leg++)
                {
                    var path = chain[leg];
                    bool lastLeg = leg == chain.Count - 1;

                    if (path == ConnectPathKind.SmartLink)
                    {
                        Tracing.TraceLine($"TryAutoConnect: leg {leg} SmartLink — calling TryAutoConnectRemote ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                        if (!TryAutoConnectRemote(config, timeoutMs, quietFailures: !lastLeg))
                        {
                            Tracing.TraceLine($"TryAutoConnect: leg {leg} SmartLink session FAILED ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                            if (!lastLeg && !SuppressSpeech)
                                ScreenReaderOutput.Speak(Lexicon.Get("connect.auto.smartlink_unreachable", ("radioName", config.RadioName)), VerbosityLevel.Critical, true);
                            continue;
                        }
                    }
                    else
                    {
                        Tracing.TraceLine($"TryAutoConnect: leg {leg} Local — starting local discovery ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                        LocalRadios();
                    }

                    // Wait for the radio to be discoverable on this leg.
                    Tracing.TraceLine($"TryAutoConnect: waiting for radio serial {config.RadioSerial} in myRadioList ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                    var startTime = DateTime.Now;
                    Radio foundRadio = null;
                    bool wantWan = path == ConnectPathKind.SmartLink;
                    while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
                    {
                        // Hold out for the leg's OWN identity: on the
                        // SmartLink leg a lingering LAN object must not end
                        // the wait, and vice versa.
                        foundRadio = myRadioList.FirstOrDefault(x => x.Serial == config.RadioSerial && x.IsWan == wantWan)
                                     ?? (lastLeg ? findRadioInAPI(config.RadioSerial) : null);
                        if (foundRadio != null) break;
                        System.Threading.Thread.Sleep(100);
                    }

                    if (foundRadio == null)
                    {
                        ConnectionHistory.Record(config.RadioSerial, path.ToString(), "not_found", (long)(DateTime.Now - startTime).TotalMilliseconds);
                        Tracing.TraceLine($"TryAutoConnect: leg {leg} radio serial {config.RadioSerial} NOT FOUND within {timeoutMs}ms. myRadioList has {myRadioList.Count} radios ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                        foreach (Radio r in myRadioList)
                        {
                            Tracing.TraceLine($"  myRadioList entry: serial={r.Serial} name={r.Nickname} status={r.Status}", TraceLevel.Info);
                        }
                        RecordConnectFailure(new ConnectFailureReport
                        {
                            Class = ConnectFailureClass.RadioNotFound,
                            SpokenSummary = (path == ConnectPathKind.SmartLink ? Lexicon.Get("connect.failure.radio_not_found_wan",
                                ("radioName", config.RadioName)) : Lexicon.Get("connect.failure.radio_not_found_lan",
                                ("radioName", config.RadioName))),
                        });
                        if (!lastLeg)
                        {
                            // No silent path substitution: the walk says so.
                            var next = (chain[leg + 1] == ConnectPathKind.SmartLink ? Lexicon.Get("connect.auto.next_smartlink") : Lexicon.Get("connect.auto.next_local"));
                            var here = (path == ConnectPathKind.SmartLink ? Lexicon.Get("connect.auto.here_smartlink") : Lexicon.Get("connect.auto.here_local"));
                            if (!SuppressSpeech)
                                ScreenReaderOutput.Speak(Lexicon.Get("connect.auto.not_found_here",
                                    ("radioName", config.RadioName), ("here", here), ("next", next)), VerbosityLevel.Critical, true);
                            continue;
                        }
                        if (!SuppressSpeech) ScreenReaderOutput.Speak(Lexicon.Get("connect.auto.not_found", ("radioName", config.RadioName)), VerbosityLevel.Critical, true);
                        return false;
                    }

                    // Connect on this leg.
                    Tracing.TraceLine($"TryAutoConnect: leg {leg} FOUND radio {foundRadio.Serial} ({foundRadio.Nickname}) isWan={foundRadio.IsWan}, connecting with lowBW={config.LowBandwidth} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                    var legSw = System.Diagnostics.Stopwatch.StartNew();
                    bool connected = Connect(config.RadioSerial, config.LowBandwidth, preferWanPath: wantWan && foundRadio.IsWan);
                    legSw.Stop();
                    ConnectionHistory.Record(config.RadioSerial, path.ToString(),
                        connected ? "connected" : (LastConnectFailureReport?.Class.ToString() ?? "failed"),
                        legSw.ElapsedMilliseconds);

                    if (connected)
                    {
                        sw.Stop();
                        Tracing.TraceLine($"TryAutoConnect: END connected successfully on leg {leg} ({path}) (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                        if (!SuppressSpeech) ScreenReaderOutput.Speak(Lexicon.Get("connect.auto.connected", ("radioName", config.RadioName)), VerbosityLevel.Critical, true);
                        // BT prosign moved to MainWindow.PowerOn so it fires AFTER the CW delegate is
                        // wired and CwNotificationsEnabled is loaded from config. Previous location
                        // (here) raced with MainWindow init -- PlayCwBT was null on first connect.
                        return true;
                    }

                    Tracing.TraceLine($"TryAutoConnect: leg {leg} Connect() FAILED ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                    if (!lastLeg)
                    {
                        var next = (chain[leg + 1] == ConnectPathKind.SmartLink ? Lexicon.Get("connect.auto.next_smartlink") : Lexicon.Get("connect.auto.next_local"));
                        if (!SuppressSpeech)
                            ScreenReaderOutput.Speak(Lexicon.Get("connect.auto.leg_failed",
                                ("radioName", config.RadioName), ("next", next)), VerbosityLevel.Critical, true);
                    }
                }

                // Chain exhausted. Speak the classified evidence, not just
                // the verdict — the last failing site filed a report.
                sw.Stop();
                Tracing.TraceLine($"TryAutoConnect: END chain exhausted (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                string? advice = LastConnectFailureAdvice;
                string failSpeech = string.IsNullOrEmpty(advice)
                    ? Lexicon.Get("connect.auto.failed", ("radioName", config.RadioName))
                    : Lexicon.Get("connect.auto.failed_with_advice",
                        ("radioName", config.RadioName), ("advice", advice));
                if (!SuppressSpeech) ScreenReaderOutput.Speak(failSpeech, VerbosityLevel.Critical, true);
                return false;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Tracing.TraceLine($"TryAutoConnect: EXCEPTION {ex.GetType().Name}: {ex.Message} (total {sw.ElapsedMilliseconds}ms)\n{ex.StackTrace}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Reconnects to a remote radio after a failed Start() due to SmartLink re-add timeout.
        /// Unlike TryAutoConnect, this works for manual connections — it uses setupRemote()
        /// directly (which calls ShowAccountSelector) and doesn't require auto-connect config.
        /// </summary>
        /// <param name="serial">Serial number of the radio to reconnect to</param>
        /// <param name="lowBW">Whether to use low bandwidth mode</param>
        /// <param name="timeoutMs">How long to wait for radio discovery (default 15 seconds)</param>
        /// <param name="forceWanPath">
        /// True when the operator chose "connect over SmartLink" for a radio that
        /// is also on the local network. Makes the wait hold out for the WAN
        /// identity specifically rather than settling for the LAN object that is
        /// already sitting in the list.
        /// </param>
        /// <returns>True if the radio was found and Connect() succeeded</returns>
        /// <param name="allowInteractiveLogin">
        /// False suppresses every sign-in form on this attempt (the auth
        /// ladder's walk-before-prompting rung — the caller still has another
        /// path in the chain). Auth failures then return false with a
        /// classified report instead of summoning a form.
        /// </param>
        public bool ReconnectRemote(string serial, bool lowBW, int timeoutMs = 15000, bool forceWanPath = false, bool allowInteractiveLogin = true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"ReconnectRemote: BEGIN serial={serial} lowBW={lowBW} timeout={timeoutMs}ms forceWanPath={forceWanPath} allowInteractiveLogin={allowInteractiveLogin}", TraceLevel.Info);

            try
            {
                // Skip auth if WAN is already connected from discovery phase.
                // GUIClient lifecycle issues are handled by RetryConnect if Start() fails.
                apiInit();
                bool wanAlreadyConnected = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.IsConnected == true;
                if (wanAlreadyConnected)
                {
                    Tracing.TraceLine($"ReconnectRemote: WAN already connected, skipping auth ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }
                else
                {
                    bool remoteOk = setupRemote(allowInteractiveLogin);
                    Tracing.TraceLine($"ReconnectRemote: setupRemote returned {remoteOk} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                    if (!remoteOk)
                    {
                        Tracing.TraceLine($"ReconnectRemote: setupRemote FAILED ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                        // setupRemote files its own classified report (auth vs
                        // server vs cancelled). Only file a generic one if it
                        // did not — a caller must always find SOMETHING here.
                        if (LastConnectFailureReport == null)
                        {
                            RecordConnectFailure(new ConnectFailureReport
                            {
                                Class = ConnectFailureClass.SessionSetupFailed,
                                SpokenSummary = "Could not sign in to SmartLink or reach its server, so the radio was never contacted.",
                            });
                        }
                        return false;
                    }
                }

                // Wait for the radio to appear in myRadioList.
                // SmartLink sends the radio list after registration; radioAddedHandler populates myRadioList.
                Tracing.TraceLine($"ReconnectRemote: waiting for radio {serial} in myRadioList ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                Radio foundRadio = null;
                var startTime = DateTime.Now;

                while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
                {
                    // With forceWanPath this holds out for the WAN identity: the
                    // LAN object for a dual-homed radio is present from the first
                    // millisecond and would end the wait before the fresh
                    // SmartLink list has even arrived.
                    foundRadio = findRadioForConnect(serial, forceWanPath);
                    if (foundRadio != null)
                        break;
                    Thread.Sleep(100);
                }

                if (foundRadio == null)
                {
                    Tracing.TraceLine($"ReconnectRemote: radio {serial} NOT FOUND within {timeoutMs}ms (forceWanPath={forceWanPath}). myRadioList has {myRadioList.Count} radios ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                    foreach (Radio r in myRadioList)
                    {
                        Tracing.TraceLine($"  myRadioList entry: serial={r.Serial} name={r.Nickname} status={r.Status}", TraceLevel.Info);
                    }
                    RecordConnectFailure(new ConnectFailureReport
                    {
                        Class = ConnectFailureClass.RadioNotFound,
                        SpokenSummary = "Signed in to SmartLink, but this radio never appeared in the account's radio list. The radio may be powered off, offline, or registered to a different SmartLink account.",
                    });
                    return false;
                }

                // Connect to the radio.
                Tracing.TraceLine($"ReconnectRemote: FOUND radio {foundRadio.Serial} ({foundRadio.Nickname}) isWan={foundRadio.IsWan}, connecting lowBW={lowBW} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                bool connected = Connect(serial, lowBW, forceWanPath);

                sw.Stop();
                Tracing.TraceLine($"ReconnectRemote: END connected={connected} (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                return connected;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Tracing.TraceLine($"ReconnectRemote: EXCEPTION {ex.GetType().Name}: {ex.Message} (total {sw.ElapsedMilliseconds}ms)\n{ex.StackTrace}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Lightweight retry: reconnect to the radio using existing WAN session.
        /// No re-auth, no handler re-wiring. Just sends remote connect + Radio.Connect().
        /// Used when Start() fails due to GUIClient lifecycle race.
        /// </summary>
        public bool RetryConnect()
        {
            if (theRadio == null)
            {
                Tracing.TraceLine("RetryConnect: no radio object", TraceLevel.Error);
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"RetryConnect: BEGIN serial={theRadio.Serial}", TraceLevel.Info);

            try
            {
                // Reset the client tracking flags for the new Start() attempt
                _clientRemovedDuringStart = false;
                _clientAddedDuringStart = false;

                bool rv = true;

                if (RemoteRig)
                {
                    rv = sendRemoteConnect(theRadio);
                    Tracing.TraceLine($"RetryConnect: sendRemoteConnect returned {rv} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }

                if (rv)
                {
                    rv = theRadio.Connect();
                    Tracing.TraceLine($"RetryConnect: Radio.Connect returned {rv} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

                    if (rv)
                    {
                        // Seed the 4.2-line discovery-chain cache on retry success too.
                        RecordConnectedRadioForCache();
                    }
                }

                return rv;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RetryConnect: EXCEPTION {ex.Message} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Handles remote radio auto-connect: silent authentication using saved account.
        /// </summary>
        /// <param name="quietFailures">True when the caller still has another
        /// path to walk — failure reports are still filed, but the spoken
        /// failure lines are left to the walk's own announcement so the user
        /// hears "trying the local network" instead of a premature verdict.</param>
        private bool TryAutoConnectRemote(AutoConnectConfig config, int timeoutMs, bool quietFailures = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"TryAutoConnectRemote: BEGIN radio={config.RadioName} serial={config.RadioSerial} email='{config.SmartLinkAccountEmail}'", TraceLevel.Info);

            // Find the saved SmartLink account
            var account = AccountManager.GetAccountByEmail(config.SmartLinkAccountEmail);
            Tracing.TraceLine($"TryAutoConnectRemote: GetAccountByEmail returned {(account != null ? account.Email : "null")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

            // Fallback: if email is empty in config (pre-fix configs), use the first saved account
            if (account == null && string.IsNullOrWhiteSpace(config.SmartLinkAccountEmail) && AccountManager.Accounts.Count > 0)
            {
                account = AccountManager.Accounts[0];
                Tracing.TraceLine($"TryAutoConnectRemote: config email was empty, falling back to first saved account: {account.Email}", TraceLevel.Info);
                config.SmartLinkAccountEmail = account.Email;
            }

            if (account == null)
            {
                Tracing.TraceLine($"TryAutoConnectRemote: no saved account for '{config.SmartLinkAccountEmail}', aborting ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                if (!SuppressSpeech && !quietFailures) ScreenReaderOutput.Speak(Lexicon.Get("connect.smartlink.account_not_found"), VerbosityLevel.Critical, true);
                return false;
            }

            Tracing.TraceLine($"TryAutoConnectRemote: account found: email={account.Email}, ExpiresAt={account.ExpiresAt}, hasRefreshToken={!string.IsNullOrEmpty(account.RefreshToken)}, hasIdToken={!string.IsNullOrEmpty(account.IdToken)}", TraceLevel.Info);

            // Always refresh the token on auto-connect startup.
            // Saved tokens may have been invalidated server-side by other login sessions.
            if (!string.IsNullOrEmpty(account.RefreshToken))
            {
                Tracing.TraceLine($"TryAutoConnectRemote: proactively refreshing token ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("auto_token_refresh_begin");

                bool refreshed = false;
                try
                {
                    refreshed = Task.Run(() => AccountManager.RefreshTokenAsync(account)).Result;
                }
                catch (AggregateException ex)
                {
                    Tracing.TraceLine($"TryAutoConnectRemote: token refresh exception: {ex.InnerException?.Message ?? ex.Message} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                    refreshed = false;
                }

                ConnectionProfiler.Current?.RecordEvent("auto_token_refresh_end", new Dictionary<string, object>
                {
                    { "refreshed", refreshed }
                });
                Tracing.TraceLine($"TryAutoConnectRemote: token refresh result={refreshed}, new ExpiresAt={account.ExpiresAt} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("TryAutoConnectRemote: no refresh token, skipping refresh", TraceLevel.Warning);
            }

            string jwt = account.IdToken;
            _currentAccount = account;

            // Check if the JWT's own exp claim has passed.
            bool isJwtExpired = string.IsNullOrEmpty(jwt) || SmartLinkAccountManager.IsJwtExpired(jwt);
            Tracing.TraceLine($"TryAutoConnectRemote: jwt empty={string.IsNullOrEmpty(jwt)}, isJwtExpired={isJwtExpired} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

            if (isJwtExpired)
            {
                Tracing.TraceLine($"TryAutoConnectRemote: JWT expired, performing silent re-login via WebView2 ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

                jwt = PerformNewLogin(title: "Connecting to Radio");
                Tracing.TraceLine($"TryAutoConnectRemote: PerformNewLogin returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (string.IsNullOrEmpty(jwt))
                {
                    Tracing.TraceLine($"TryAutoConnectRemote: re-login failed, aborting ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                    return false;
                }
            }

            // Connect to SmartLink server (this will trigger radio discovery)
            Tracing.TraceLine($"TryAutoConnectRemote: calling apiInit + ConnectToSmartLink ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            apiInit();
            ConnectionProfiler.Current?.RecordEvent("auto_smartlink_connect_begin");
            SmartLinkConnectResult connectResult = ConnectToSmartLink(jwt);
            bool connected = connectResult == SmartLinkConnectResult.Success;
            ConnectionProfiler.Current?.RecordEvent("auto_smartlink_connect_end", new Dictionary<string, object>
            {
                { "success", connected },
                { "result", connectResult.ToString() }
            });

            if (!connected)
            {
                sw.Stop();
                Tracing.TraceLine($"TryAutoConnectRemote: SmartLink connection FAILED with {connectResult} (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                if (connectResult == SmartLinkConnectResult.NoRadios)
                {
                    TraceSessionContext.MarkOutcome(TraceSessionOutcome.NoRadios,
                        "Auto-connect: SmartLink registered, server returned empty radio list");
                    TraceSessionContext.AddKeyEvent("smartlink_no_radios_auto");
                    if (!SuppressSpeech && !quietFailures)
                    {
                        ScreenReaderOutput.Speak(
                            "No SmartLink radios available. The remote radio may be turned off.",
                            VerbosityLevel.Critical, true);
                    }
                }
                else if (connectResult == SmartLinkConnectResult.AuthFailed)
                {
                    // QB Track D: auto-connect deliberately never pops a login
                    // form (it runs before the user has touched anything), but
                    // it can at least say that sign-in — not the network — is
                    // what needs attention.
                    RecordConnectFailure(new ConnectFailureReport
                    {
                        Class = ConnectFailureClass.AuthenticationFailed,
                        SpokenSummary = Lexicon.Get("connect.failure.auth_summary", ("email", config.SmartLinkAccountEmail)),
                    });
                    if (!SuppressSpeech && !quietFailures) ScreenReaderOutput.Speak(Lexicon.Get("connect.smartlink.signin_not_accepted"), VerbosityLevel.Critical, true);
                }
                else
                {
                    RecordConnectFailure(new ConnectFailureReport
                    {
                        Class = ConnectFailureClass.SessionSetupFailed,
                        SpokenSummary = Lexicon.Get("connect.failure.session_setup"),
                    });
                    if (!SuppressSpeech && !quietFailures) ScreenReaderOutput.Speak(Lexicon.Get("connect.smartlink.connection_failed"), VerbosityLevel.Critical, true);
                }
                return false;
            }

            sw.Stop();
            // A successful auto-connect is the operator genuinely using this
            // account (a standing choice, stamped into the auto-connect
            // record) — without this, someone who only ever auto-connects
            // would show a forever-stale LastUsed in the account manager.
            AccountManager.MarkAccountUsed(account);
            Tracing.TraceLine($"TryAutoConnectRemote: END success (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            return true;
        }

        /// <summary>Reason the last Start() call failed. Set before returning false.</summary>
        public string? LastStartFailureReason { get; private set; }

        /// <summary>Suppress screen reader speech. Set true for automated testing.</summary>
        public bool SuppressSpeech { get; set; }

        /// <summary>
        /// Start radio activity
        /// </summary>
        public bool Start()
        {
            // Reset flags early — stale removal events from Connect() phase
            // must not poison the station name wait loop.
            _clientRemovedDuringStart = false;
            _clientAddedDuringStart = false;
            _cancelRequested = false;

            LastStartFailureReason = null;
            ConnectionProfiler.Current?.RecordEvent("start_begin", new Dictionary<string, object>
            {
                { "clientRemovedAlready", _clientRemovedDuringStart },
                { "clientAddedAlready", _clientAddedDuringStart }
            });
            FilterObj = new WpfFilterAdapter(this);

            await(() =>
            {
                return initialFreeSlices != -1 || _cancelRequested;
            }, 5000);

            if (_cancelRequested)
            {
                Tracing.TraceLine("start: cancelled before slice check", TraceLevel.Info);
                LastStartFailureReason = "Cancelled by user";
                return false;
            }

            ConnectionProfiler.Current?.RecordEvent("start_slices_available", new Dictionary<string, object>
            {
                { "freeSlices", initialFreeSlices }
            });

            // Need at least 1 slice.
            if (initialFreeSlices <= 0)
            {
                // Include who has the slices so the user knows who to coordinate with
                string sliceMsg = noSlice;
                var others = OtherConnectedStations;
                if (others.Count > 0)
                    sliceMsg += " — in use by " + string.Join(", ", others);
                Tracing.TraceLine("start: couldn't get a slice", TraceLevel.Error);
                LastStartFailureReason = "No slices available";
                TraceSessionContext.MarkOutcome(TraceSessionOutcome.SliceUnavailable, sliceMsg);
                TraceSessionContext.AddKeyEvent("no_slices_available");
                raiseNoSliceError(sliceMsg);
                return false;
            }

            // Must have an antenna. Wait up to 20s — FlexLib fetches the list via an
            // async "ant list" command at connect time and populates RXAntList from the
            // reply. Over WAN that round trip routinely takes 5–15s (same timing profile
            // as the station-name wait below). LAN connections settle in <200ms.
            if (!await(() =>
            {
                return ((theRadio.RXAntList != null) && (theRadio.RXAntList.Length > 0)) || _cancelRequested;
            }, 20000))
            {
                // QB Track D (item 4): "no RX antenna" was a misleading verdict
                // for this timeout. Every Flex physically has RX antennas; when
                // this wait expires the real story is almost always that the
                // "ant list" REPLY never arrived — the command/data path came
                // up dead or the connection dropped mid-setup. Say the real
                // thing; only an actually-empty reply earns the antenna words.
                if (!IsConnected)
                {
                    Tracing.TraceLine("start: connection dropped during antenna-list wait", TraceLevel.Error);
                    LastStartFailureReason = "Connection lost during setup";
                    raiseNoSliceError("the connection to the radio dropped during setup");
                    return false;
                }
                if (theRadio.RXAntList == null)
                {
                    Tracing.TraceLine("start: antenna list never arrived — command/data path never came up", TraceLevel.Error);
                    LastStartFailureReason = "Radio setup stalled — the radio never sent its setup data";
                    raiseNoSliceError("the radio connected but never sent its setup data. "
                        + "This is a connection problem, not an antenna problem — try connecting again");
                    return false;
                }
                // RXAntList arrived but is empty: the radio genuinely reported
                // zero RX antennas. Vanishingly rare, but honest.
                Tracing.TraceLine("start: radio answered with an empty RX antenna list", TraceLevel.Error);
                LastStartFailureReason = "No RX antenna detected";
                raiseNoSliceError(noRXAnt);
                return false;
            }

            if (_cancelRequested)
            {
                Tracing.TraceLine("start: cancelled during antenna wait", TraceLevel.Info);
                LastStartFailureReason = "Cancelled by user";
                ConnectionProfiler.Current?.RecordAndSave("start_cancelled", new Dictionary<string, object>
                {
                    { "phase", "antenna_wait" }
                });
                try { theRadio?.Disconnect(); } catch { }
                return false;
            }

            ConnectionProfiler.Current?.RecordEvent("start_antenna_available");

            // Wait for the station name to be set on the GUIClient.
            // SmartLink removes and re-adds the GUIClient during connection setup.
            // We track the removal event for faster detection — if our client is removed
            // and not re-added within the grace period, disconnect and let caller retry.
            // A fresh reconnection is faster than waiting for a slow re-add cycle.
            bool stationNameSet = false;
            {
                int maxWaitMs = 45000;       // 45s overall timeout (SmartLink re-add can take 30s+ over WAN)
                int removalGraceMs = 15000;  // 15s grace after client removal (Don's 6300 over WAN needs 10s+)
                int earlyAbortMs = 1000;     // 1s: if removed without ever being added, abort fast for retry
                int interval = 25;
                int iterations = maxWaitMs / interval;
                _clientRemovedDuringStart = false;
                _clientAddedDuringStart = false;
                _startBeginTickCount = Environment.TickCount64;

                ConnectionProfiler.Current?.RecordEvent("start_station_name_wait_begin", new Dictionary<string, object>
                {
                    { "maxWaitMs", maxWaitMs },
                    { "earlyAbortMs", earlyAbortMs },
                    { "removalGraceMs", removalGraceMs }
                });

                while (iterations-- > 0)
                {
                    if (_cancelRequested)
                    {
                        ConnectionProfiler.Current?.RecordEvent("start_cancelled_in_station_wait", new Dictionary<string, object>
                        {
                            { "msSinceStartBegin", Environment.TickCount64 - _startBeginTickCount }
                        });
                        Tracing.TraceLine("start: cancelled during station-name wait", TraceLevel.Info);
                        break;
                    }
                    if (!IsConnected)
                    {
                        Tracing.TraceLine("start:connection dropped while waiting for station name", TraceLevel.Error);
                        break;
                    }
                    // Fast abort: if client was removed during Start() without ever being added
                    // during Start(), the re-add will never come on this connection.
                    // Profile data: every failure shows removal ~800ms into Start() with no prior add.
                    if (_clientRemovedDuringStart && !_clientAddedDuringStart &&
                        (Environment.TickCount64 - _clientRemovedTickCount) > earlyAbortMs)
                    {
                        ConnectionProfiler.Current?.RecordEvent("start_early_abort", new Dictionary<string, object>
                        {
                            { "msSinceRemoval", Environment.TickCount64 - _clientRemovedTickCount },
                            { "msSinceStartBegin", Environment.TickCount64 - _startBeginTickCount },
                            { "clientAddedDuringStart", _clientAddedDuringStart }
                        });
                        Tracing.TraceLine($"start:client removed without prior add during Start(), aborting after {earlyAbortMs}ms for retry", TraceLevel.Warning);
                        break;
                    }
                    // Standard grace: if client was added then removed, give it more time for re-add.
                    if (_clientRemovedDuringStart && _clientAddedDuringStart &&
                        (Environment.TickCount64 - _clientRemovedTickCount) > removalGraceMs)
                    {
                        ConnectionProfiler.Current?.RecordEvent("start_grace_abort", new Dictionary<string, object>
                        {
                            { "msSinceRemoval", Environment.TickCount64 - _clientRemovedTickCount },
                            { "msSinceStartBegin", Environment.TickCount64 - _startBeginTickCount }
                        });
                        Tracing.TraceLine($"start:client removed {removalGraceMs}ms ago without re-add, aborting for retry", TraceLevel.Warning);
                        break;
                    }
                    GUIClient client = TheGuiClient;
                    if (client != null && client.Station == Callouts.StationName)
                    {
                        stationNameSet = true;
                        break;
                    }
                    Thread.Sleep(interval);
                }
            }
            if (_cancelRequested)
            {
                // User cancelled during station-name wait. Clean up radio-side state
                // (FlexLib Disconnect releases the GUIClient registration) so the next
                // connect attempt doesn't trip the AS-retry path on stale state.
                Tracing.TraceLine("start: cancelled, disconnecting radio for clean cleanup", TraceLevel.Info);
                LastStartFailureReason = "Cancelled by user";
                ConnectionProfiler.Current?.RecordAndSave("start_cancelled", new Dictionary<string, object>
                {
                    { "phase", "station_name_wait" },
                    { "msSinceStartBegin", Environment.TickCount64 - _startBeginTickCount }
                });
                try { theRadio.Disconnect(); } catch { }
                return false;
            }
            if (stationNameSet)
            {
                Tracing.TraceLine("start:station name set " + Callouts.StationName, TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("station_name_set", new Dictionary<string, object>
                {
                    { "stationName", Callouts.StationName }
                });
            }
            else if (!IsConnected)
            {
                // Connection dropped during SmartLink re-add cycle.
                // Don't raise error — caller (openTheRadio) can retry the connection.
                Tracing.TraceLine("start:connection lost during station name wait, caller may retry", TraceLevel.Error);
                LastStartFailureReason = "Connection lost during setup";
                ConnectionProfiler.Current?.RecordAndSave("start_connection_lost");
                return false;
            }
            else
            {
                // Station name timeout — SmartLink re-add is too slow.
                // Disconnect cleanly and return false so caller can retry with a fresh connection.
                // Don't show error dialog — a fresh connection usually succeeds quickly.
                Tracing.TraceLine("start:station name timeout, disconnecting for retry", TraceLevel.Warning);
                LastStartFailureReason = _clientRemovedDuringStart
                    ? "Client removed during connection"
                    : "Station name timeout";
                ConnectionProfiler.Current?.RecordAndSave("station_name_timeout", new Dictionary<string, object>
                {
                    { "clientRemovedDuringStart", _clientRemovedDuringStart },
                    { "ticksSinceRemoval", _clientRemovedDuringStart ? (Environment.TickCount64 - _clientRemovedTickCount) : 0 }
                });
                if (!SuppressSpeech) ScreenReaderOutput.Speak(Lexicon.Get("connect.start.slow_retrying"), VerbosityLevel.Critical);
                if (ScreenReaderOutput.CwNotificationsEnabled) _ = ScreenReaderOutput.PlayCwAS?.Invoke();
                try { theRadio.Disconnect(); } catch { }
                return false;
            }
            mainThread = new Thread(mainThreadProc);
            mainThread.Name = "mainThread";
            mainThread.Start();
            Thread.Sleep(0);
            ConnectionProfiler.Current?.RecordAndSave("start_success");
            // BT prosign moved to MainWindow.PowerOn so it fires AFTER CW delegates are wired
            // and CwNotificationsEnabled is loaded. Previous location raced with init.
            return true;
        }

        internal bool Disconnecting = false;
        /// <summary>
        /// Disconnect from the connected radio.
        /// Also disconnects from the wan if appropriate.
        /// </summary>
        public void Disconnect()
        {
            Tracing.TraceLine("Disconnect:" + (string)((theRadio == null) ? "null" : theRadio.Serial), TraceLevel.Info);
            if (theRadio == null) return;
            // Signal the cancel flag too — if Start() is in-flight on another thread
            // (UI thread blocked in the station-name wait), it sees this and exits fast.
            _cancelRequested = true;
            Disconnecting = true;

            // 2026-04-28: announce disconnect via speech + CW (SK prosign) so the
            // user knows the radio is going away. Fires before the actual
            // disconnect work (which can take 3+ seconds) so feedback is
            // immediate. This is the user-initiated / clean disconnect path;
            // unexpected drops (radioPropertyChangedHandler when Connected
            // flips to false from external causes) get a different signal —
            // see project_stuck_modal_escape_design.md.
            if (!SuppressSpeech)
            {
                string msg = Lexicon.Get("connect.disconnect.announcement", ScreenReaderOutput.CurrentVerbosity);
                // INTERRUPT, deliberately - and this was briefly QUEUE on
                // 2026-08-18, which was wrong.
                //
                // Queueing looked right on paper: SelectRadio says
                // "Disconnecting from <radio>" and this says it happened, so
                // the pair reads as one sentence. But a disconnect is followed
                // immediately by the picker opening, and a window change makes
                // the screen reader flush whatever is queued. So the queued
                // half was never heard at all - worse than cutting the first
                // half, which at least reached the operator.
                //
                // On a deliberate radio SWITCH there is no collision to worry
                // about any more: SelectRadio sets SuppressSpeech before
                // closing, because its own announcement already covers it. This
                // line is therefore the voice of an UNEXPECTED drop, where it
                // is the only announcement there is - and must not wait.
                ScreenReaderOutput.Speak(
                    msg, Speech.SpeechIntent.Interrupt, VerbosityLevel.Critical);
            }
            // ── The exit farewell, and why this path has to WAIT for it ──
            //
            // Sprint 32 Track H. Until now this line read
            //
            //     _ = ScreenReaderOutput.PlayCwSK?.Invoke();
            //
            // which DISCARDS the task: nothing anywhere awaited the farewell it
            // started. The very next line sets SkAlreadyPlayedThisSession, and
            // that flag makes ApplicationEvents.MyApplication_Shutdown skip its
            // own PlayCwSK.Invoke().Wait(5000). So the only code that waited for
            // the farewell lived exclusively in the path the flag suppresses.
            //
            // The guard is not the bug and must stay — it was added for a real
            // complaint (hearing 73 twice when disconnecting from the menu and
            // then closing) and it works. What it did NOT do was inherit the
            // wait. Closing while CONNECTED therefore ran Alt+F4 →
            // ExitApplication → CloseTheRadio → here → fire-and-forget → teardown
            // straight through EarconPlayer.Dispose(), and Noel heard "dah dah":
            // the first two elements of the digit 7, roughly 150 ms of a
            // two-second string, before the audio device was destroyed under it.
            //
            // WHOEVER PLAYS IT OWNS WAITING FOR IT. The wait is deliberately not
            // hoisted somewhere shared, because the next path that decides to
            // play SK would inherit exactly this trap. There are two today;
            // assume a third.
            //
            // The wait is placed at the END of Disconnect rather than here, so
            // the farewell overlaps the disconnect work (main-thread join, radio
            // disconnect await) that this method has to do anyway. Practically
            // that costs no added latency at all, while still guaranteeing that
            // Disconnect does not return until its own farewell has finished or
            // the bound has expired. Bounded at 5000 ms, matching what Shutdown
            // already allows: a wedged audio device must never be able to stop a
            // disconnect, let alone an application exit.
            Task farewell = null;
            if (ScreenReaderOutput.CwNotificationsEnabled)
            {
                farewell = ScreenReaderOutput.PlayCwSK?.Invoke();
                // Mark SK played for this session so the app-exit Shutdown handler
                // skips its own SK call. Otherwise the user hears 73-SK twice when
                // they disconnect via menu and then close the app.
                ScreenReaderOutput.SkAlreadyPlayedThisSession = true;
            }

            try
            {
                if ((mainThread != null) && mainThread.IsAlive)
                {
                    // Stop the main thread.
                    Tracing.TraceLine("Disconnect:stopping main thread", TraceLevel.Info);
                    stopMainThread = true;
                    if (mainThread.Join(3000))
                    {
                        Tracing.TraceLine("Disconnect:main thread stopped", TraceLevel.Info);
                    }
                    else
                    {
                        // Thread.Abort() throws PlatformNotSupportedException on .NET 8.
                        // Log and abandon — the thread will exit on its own when it checks
                        // stopMainThread or hits the suppressed exception handler.
                        Tracing.TraceLine("Disconnect:main thread didn't stop within 3s, abandoning", TraceLevel.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Disconnect:mainThread:" + ex.Message, TraceLevel.Error);
            }
            mainThread = null;

            if (theRadio.Connected)
            {
                // Sprint 27 Track B / Phase B.3 — release any UPnP mappings
                // we added in Connect before we lose the radio-IP context.
                ReleaseUPnPMappingsIfAny();

                theRadio.Disconnect();
                if (!await(() =>
                {
                    return !theRadio.Connected;
                }, 30000))
                {
                    Tracing.TraceLine("Disconnect:the radio didn't disconnect", TraceLevel.Info);
                }

                PCAudio = false;

                // Sprint 26 Phase 4: SmartLink session is owned by the coordinator
                // and lives across radio connect/disconnect cycles. FlexBase no
                // longer tears down the session here; the user's next connect
                // reuses the live session (faster, fewer SSL handshakes, and
                // exactly the ownership fix that motivated this sprint).

                // The radio's Meter objects go with the radio. Forget the
                // subscriptions so the next connect hooks the new ones and the
                // inventory is re-announced rather than assumed unchanged.
                resetMeterInventory();

                theRadio = null;
            }

            // Now collect the farewell started at the top of this method. See the
            // long comment there: this path plays SK and suppresses the only
            // other path that knew how to wait, so it has to do the waiting
            // itself. Bounded, and never allowed to throw — a farewell must not
            // be able to fail a disconnect.
            if (farewell != null)
            {
                try
                {
                    if (!farewell.Wait(SkFarewellWaitMs))
                    {
                        Tracing.TraceLine(
                            "Disconnect:SK farewell did not finish within "
                            + SkFarewellWaitMs + "ms — continuing teardown",
                            TraceLevel.Warning);
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("Disconnect:SK farewell:" + ex.Message, TraceLevel.Warning);
                }
            }
        }

        /// <summary>
        /// How long any path that plays the SK farewell may wait for it before
        /// giving up and continuing. Matches the bound
        /// <c>ApplicationEvents.MyApplication_Shutdown</c> already applies to its
        /// own call, so the two SK paths cannot drift apart.
        /// </summary>
        internal const int SkFarewellWaitMs = 5000;

        private bool _IsConnected = false; // set in radioPropertyChangedHandler
        /// <summary>
        /// True if connected.
        /// </summary>
        public bool IsConnected
        {
            get { return _IsConnected; }
        }

        #region Static IP / DHCP

        /// <summary>
        /// Result of validating a proposed static-network configuration.
        /// </summary>
        public sealed class StaticIpCheck
        {
            public bool CanProceed { get; set; }
            public string BlockReason { get; set; } = string.Empty;
            public List<string> Warnings { get; } = new List<string>();
        }

        /// <summary>Current static IP the radio reports, or null when on DHCP.</summary>
        public System.Net.IPAddress CurrentStaticIP => theRadio?.StaticIP;

        /// <summary>Current static gateway the radio reports, or null.</summary>
        public System.Net.IPAddress CurrentStaticGateway => theRadio?.StaticGateway;

        /// <summary>Current static netmask the radio reports, or null.</summary>
        public System.Net.IPAddress CurrentStaticNetmask => theRadio?.StaticNetmask;

        /// <summary>The address we are actually talking to the radio on right now.</summary>
        public System.Net.IPAddress CurrentRadioIP => theRadio?.IP;

        /// <summary>
        /// Validate a proposed static-network configuration without applying it.
        ///
        /// This matters more than a normal settings validation. A wrong value here
        /// doesn't produce an error — it makes the radio unreachable at the next
        /// reboot, and the only recovery is physical access to the front panel. For a
        /// radio at a remote site that means someone has to travel. So we check the
        /// arithmetic properly and warn loudly when the proposed address wouldn't be
        /// reachable from where we're standing.
        /// </summary>
        public StaticIpCheck PreflightStaticIp(string ip, string gateway, string netmask)
        {
            var check = new StaticIpCheck();

            if (theRadio == null || !IsConnected)
            {
                check.BlockReason = "No radio is connected.";
                return check;
            }

            if (!System.Net.IPAddress.TryParse(ip, out var ipAddr)
                || ipAddr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                check.BlockReason = "The IP address is not a valid IPv4 address.";
                return check;
            }
            if (!System.Net.IPAddress.TryParse(gateway, out var gwAddr)
                || gwAddr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                check.BlockReason = "The gateway is not a valid IPv4 address.";
                return check;
            }
            if (!System.Net.IPAddress.TryParse(netmask, out var maskAddr)
                || maskAddr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                check.BlockReason = "The subnet mask is not a valid IPv4 address.";
                return check;
            }

            uint ipV = ToUInt(ipAddr), gwV = ToUInt(gwAddr), maskV = ToUInt(maskAddr);

            // A netmask must be a contiguous run of 1 bits. 255.255.0.255 is a
            // classic typo that parses fine and breaks routing.
            uint inverted = ~maskV;
            if ((inverted & (inverted + 1)) != 0)
            {
                check.BlockReason = "The subnet mask is not valid — the one bits must be contiguous (for example 255.255.255.0).";
                return check;
            }
            if (maskV == 0xFFFFFFFF || maskV == 0)
            {
                check.BlockReason = "The subnet mask must leave room for hosts (for example 255.255.255.0).";
                return check;
            }

            uint network = ipV & maskV;
            uint broadcast = network | ~maskV;

            if (ipV == network)
            {
                check.BlockReason = "That IP address is the network address for this subnet and cannot be assigned to the radio.";
                return check;
            }
            if (ipV == broadcast)
            {
                check.BlockReason = "That IP address is the broadcast address for this subnet and cannot be assigned to the radio.";
                return check;
            }
            if ((gwV & maskV) != network)
            {
                check.BlockReason = "The gateway is not on the same subnet as the IP address. The radio would have no route off its network.";
                return check;
            }
            if (gwV == ipV)
            {
                check.BlockReason = "The gateway cannot be the same address as the radio.";
                return check;
            }

            // The lock-yourself-out check: if the proposed address isn't on the same
            // subnet we're currently reaching the radio on, this is very likely a typo
            // — and if it isn't, whoever applies it needs to know they're changing
            // networks deliberately.
            var currentIp = theRadio.IP;
            if (currentIp != null && currentIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                uint curV = ToUInt(currentIp);
                if ((curV & maskV) != network)
                {
                    check.Warnings.Add(
                        $"The radio is reachable at {currentIp} today, which is on a different subnet than {ip}. " +
                        "If that is not deliberate, the radio will be unreachable after it restarts and will need " +
                        "someone at the radio to fix it.");
                }
                else if (curV != ipV)
                {
                    check.Warnings.Add(
                        $"The radio's address will change from {currentIp} to {ip}. You will need to reconnect after it restarts.");
                }
            }

            var others = OtherConnectedStations;
            if (others.Count > 0)
                check.Warnings.Add("Other stations are connected and will need to reconnect: " + string.Join(", ", others));

            check.CanProceed = true;
            return check;
        }

        private static uint ToUInt(System.Net.IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        /// <summary>
        /// A proposed static configuration built from what the radio is using right
        /// now. Backs the "use the current address" button.
        /// </summary>
        public sealed class SuggestedStaticConfig
        {
            public bool Available { get; set; }
            public string Reason { get; set; } = string.Empty;
            public string Ip { get; set; } = string.Empty;
            public string Gateway { get; set; } = string.Empty;
            public string Netmask { get; set; } = string.Empty;
            public List<string> Warnings { get; } = new List<string>();
        }

        /// <summary>
        /// Build a static configuration from the address the radio is currently
        /// using. This is the safest way to go static — you're pinning an address
        /// that demonstrably works on that network right now.
        ///
        /// Refuses over SmartLink. On a WAN connection <c>theRadio.IP</c> is the
        /// address we reach the radio *through*, not the radio's address on its own
        /// LAN. Pinning that as a static IP would make the radio unreachable and need
        /// physical access to undo — the exact outcome this whole feature exists to
        /// avoid.
        ///
        /// Gateway and netmask come from the radio when it reports them, otherwise
        /// they're inferred from whichever local adapter shares a subnet with the
        /// radio. Inference is flagged in Warnings so the user can sanity-check it.
        /// </summary>
        public SuggestedStaticConfig SuggestStaticFromCurrent()
        {
            var s = new SuggestedStaticConfig();

            if (theRadio == null || !IsConnected)
            {
                s.Reason = "No radio is connected.";
                return s;
            }

            if (theRadio.IsWan)
            {
                s.Reason =
                    "You are connected over SmartLink. The address JJ Flex sees is not the radio's address " +
                    "on its own network, so it cannot be used as a static IP. Connect on the same local " +
                    "network as the radio to use this.";
                return s;
            }

            var ip = theRadio.IP;
            if (ip == null || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                s.Reason = "The radio's current IPv4 address is not available.";
                return s;
            }
            s.Ip = ip.ToString();

            // Prefer whatever the radio itself reports.
            if (theRadio.StaticNetmask != null) s.Netmask = theRadio.StaticNetmask.ToString();
            if (theRadio.StaticGateway != null) s.Gateway = theRadio.StaticGateway.ToString();

            // Otherwise infer from the local adapter that shares the radio's subnet.
            if (string.IsNullOrEmpty(s.Netmask) || string.IsNullOrEmpty(s.Gateway))
            {
                try
                {
                    uint radioV = ToUInt(ip);
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        var props = nic.GetIPProperties();
                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            var mask = ua.IPv4Mask;
                            if (mask == null || ToUInt(mask) == 0) continue;
                            uint maskV = ToUInt(mask);
                            if ((ToUInt(ua.Address) & maskV) != (radioV & maskV)) continue;

                            if (string.IsNullOrEmpty(s.Netmask))
                            {
                                s.Netmask = mask.ToString();
                                s.Warnings.Add($"The subnet mask {s.Netmask} was taken from this computer's network settings, not from the radio.");
                            }
                            if (string.IsNullOrEmpty(s.Gateway))
                            {
                                foreach (var gw in props.GatewayAddresses)
                                {
                                    if (gw?.Address == null) continue;
                                    if (gw.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                                    if (ToUInt(gw.Address) == 0) continue;
                                    s.Gateway = gw.Address.ToString();
                                    s.Warnings.Add($"The gateway {s.Gateway} was taken from this computer's network settings, not from the radio.");
                                    break;
                                }
                            }
                            break;
                        }
                        if (!string.IsNullOrEmpty(s.Netmask) && !string.IsNullOrEmpty(s.Gateway)) break;
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"SuggestStaticFromCurrent: adapter probe failed: {ex.Message}", TraceLevel.Error);
                }
            }

            if (string.IsNullOrEmpty(s.Netmask) || string.IsNullOrEmpty(s.Gateway))
            {
                s.Reason =
                    "The radio's current address is " + s.Ip + ", but the gateway and subnet mask could not be " +
                    "determined automatically. Enter them by hand.";
                // Still hand back the IP so the field can be filled in.
                s.Available = false;
                return s;
            }

            s.Warnings.Add(
                "This address was assigned by DHCP. Pinning it as static works, but the router could later hand " +
                "the same address to another device. A DHCP reservation on the router is the tidier fix where that is possible.");

            s.Available = true;
            return s;
        }

        /// <summary>
        /// Apply a static network configuration. Call <see cref="PreflightStaticIp"/>
        /// first and only call this when it reports CanProceed.
        ///
        /// FlexLib reports the outcome through the radio's StaticIPSetSuccessful /
        /// StaticIPSetFailed events rather than a return value, so callers pass
        /// handlers. Both are unsubscribed after the first fire.
        /// </summary>
        public bool ApplyStaticIp(string ip, string gateway, string netmask, Action onSuccess, Action onFailure)
        {
            if (theRadio == null || !IsConnected) return false;

            try
            {
                var r = theRadio;
                EventHandler okHandler = null, failHandler = null;

                okHandler = (s, e) =>
                {
                    r.StaticIPSetSuccessful -= okHandler;
                    r.StaticIPSetFailed -= failHandler;
                    Tracing.TraceLine($"ApplyStaticIp: radio accepted {ip}/{netmask} gw {gateway}", TraceLevel.Info);
                    onSuccess?.Invoke();
                };
                failHandler = (s, e) =>
                {
                    r.StaticIPSetSuccessful -= okHandler;
                    r.StaticIPSetFailed -= failHandler;
                    Tracing.TraceLine("ApplyStaticIp: radio rejected the static network parameters", TraceLevel.Error);
                    onFailure?.Invoke();
                };

                r.StaticIPSetSuccessful += okHandler;
                r.StaticIPSetFailed += failHandler;

                r.StaticIP = System.Net.IPAddress.Parse(ip);
                r.StaticGateway = System.Net.IPAddress.Parse(gateway);
                r.StaticNetmask = System.Net.IPAddress.Parse(netmask);
                r.SetStaticNetworkParams();
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ApplyStaticIp: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Put the radio back on DHCP. Same event-based outcome reporting as
        /// <see cref="ApplyStaticIp"/>.
        /// </summary>
        public bool RevertToDhcp(Action onSuccess, Action onFailure)
        {
            if (theRadio == null || !IsConnected) return false;

            try
            {
                var r = theRadio;
                EventHandler okHandler = null, failHandler = null;

                okHandler = (s, e) =>
                {
                    r.DHCPSetSuccessful -= okHandler;
                    r.DHCPSetFailed -= failHandler;
                    Tracing.TraceLine("RevertToDhcp: radio accepted DHCP", TraceLevel.Info);
                    onSuccess?.Invoke();
                };
                failHandler = (s, e) =>
                {
                    r.DHCPSetSuccessful -= okHandler;
                    r.DHCPSetFailed -= failHandler;
                    Tracing.TraceLine("RevertToDhcp: radio rejected the DHCP reset", TraceLevel.Error);
                    onFailure?.Invoke();
                };

                r.DHCPSetSuccessful += okHandler;
                r.DHCPSetFailed += failHandler;
                r.SetNetworkToDCHP();
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RevertToDhcp: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Whether the radio refuses connections from non-private source addresses.
        /// Surfaced because Tailscale hands out CGNAT addresses (100.64.0.0/10), which
        /// are NOT RFC1918 — with this enabled a tailnet-attached client can be
        /// silently refused.
        /// </summary>
        public bool EnforcePrivateIPConnections
        {
            get
            {
                try { return theRadio != null && theRadio.EnforcePrivateIPConnections; }
                catch { return false; }
            }
            set
            {
                try
                {
                    if (theRadio == null) return;
                    theRadio.EnforcePrivateIPConnections = value;
                    Tracing.TraceLine($"EnforcePrivateIPConnections set to {value}", TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"EnforcePrivateIPConnections set failed: {ex.Message}", TraceLevel.Error);
                }
            }
        }

        #endregion

        #region SmartLink registration

        /// <summary>
        /// Result of validating whether this radio can be registered to a SmartLink
        /// account right now.
        /// </summary>
        public sealed class RegistrationCheck
        {
            public bool CanProceed { get; set; }
            public string BlockReason { get; set; } = string.Empty;
            public List<string> Warnings { get; } = new List<string>();
            /// <summary>Account the radio would be registered to.</summary>
            public string AccountEmail { get; set; } = string.Empty;
        }

        /// <summary>
        /// The radio's SmartLink ownership handshake state, in plain language.
        ///
        /// "WaitingForPTT" is the one that matters: the radio is asking for proof
        /// that a human is standing at it, and it will sit there until someone keys
        /// the mic or the attempt times out. That requirement is Flex's, not ours,
        /// and it is the reason a radio must be registered before it ships anywhere.
        /// </summary>
        public string RegistrationStateText
        {
            get
            {
                if (theRadio == null) return "No radio connected.";
                try
                {
                    return theRadio.WanOwnerHandshakeStatus switch
                    {
                        Radio.WanRadioRegistrationState.Undefined =>
                            "Not started. JJ Flex cannot tell from here whether this radio is already registered — if it shows up in your SmartLink radio list, it is.",
                        Radio.WanRadioRegistrationState.WaitingOnSmartLinkConnection =>
                            "The radio is contacting SmartLink.",
                        Radio.WanRadioRegistrationState.WaitingForPTT =>
                            "The radio is waiting for you to key the microphone or the CW key. Do that now, at the radio.",
                        Radio.WanRadioRegistrationState.WaitingOnServerConfirmation =>
                            "Keyed. Waiting for SmartLink to confirm.",
                        Radio.WanRadioRegistrationState.RegisterSuccess =>
                            "Registered. This radio is now tied to your SmartLink account and can be reached from away from home.",
                        Radio.WanRadioRegistrationState.UnregisterSuccess =>
                            "Unregistered. This radio is no longer tied to a SmartLink account.",
                        Radio.WanRadioRegistrationState.FailedPTT =>
                            "Failed — the radio did not see the microphone or key pressed in time. Try again and key it as soon as you are asked.",
                        Radio.WanRadioRegistrationState.FailedServerConnection =>
                            "Failed — the radio could not reach SmartLink. Check that the radio has a working internet connection.",
                        Radio.WanRadioRegistrationState.FailedServerConfirmation =>
                            "Failed — SmartLink did not confirm the registration.",
                        Radio.WanRadioRegistrationState.FailedNotLicensed =>
                            "Failed — this radio is not licensed for SmartLink.",
                        Radio.WanRadioRegistrationState.FailedUnknown =>
                            "Failed, with no reason given. See the trace file.",
                        _ => "Unknown.",
                    };
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"RegistrationStateText: {ex.Message}", TraceLevel.Error);
                    return "Unknown.";
                }
            }
        }

        /// <summary>True once the handshake has reported success this session.</summary>
        public bool RegistrationSucceeded
        {
            get
            {
                try
                {
                    return theRadio != null
                        && theRadio.WanOwnerHandshakeStatus == Radio.WanRadioRegistrationState.RegisterSuccess;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Check whether registration can be attempted, without attempting it.
        ///
        /// Registration is sent on the radio's own command channel and the radio
        /// then reaches out to SmartLink itself, so it needs a live local
        /// connection plus a SmartLink account to register *to*. Doing it over
        /// SmartLink would be circular — you cannot connect that way until the
        /// radio is registered.
        /// </summary>
        public RegistrationCheck PreflightSmartLinkRegistration()
        {
            var check = new RegistrationCheck();

            if (theRadio == null || !IsConnected)
            {
                check.BlockReason = "No radio is connected.";
                return check;
            }

            if (theRadio.IsWan)
            {
                check.BlockReason =
                    "You are connected over SmartLink, which means this radio is already registered. " +
                    "Registration has to be done from the same local network as the radio.";
                return check;
            }

            if (!TryLoadSavedAccount())
            {
                // Two different problems, two different fixes. With several
                // accounts saved and none chosen, "sign in first" is false
                // advice — the operator is signed in, possibly twice; what is
                // missing is a decision, and re-signing-in does not make one.
                check.BlockReason = AccountManager.Accounts.Count == 0
                    ? "No SmartLink account is signed in. Sign in to SmartLink first — the radio is registered to an account, " +
                      "and JJ Flex needs to know which one."
                    : "Several SmartLink accounts are saved and none is set as the default. Open Manage SmartLink Accounts " +
                      "and choose a default account — the radio is registered to an account, and JJ Flex needs to know " +
                      "which one to check against.";
                return check;
            }

            check.AccountEmail = _currentAccount.Email;

            var others = OtherConnectedStations;
            if (others.Count > 0)
                check.Warnings.Add("Other stations are connected to this radio: " + string.Join(", ", others));

            check.Warnings.Add(
                "The radio will ask you to key the microphone or the CW key to prove someone is standing at it. " +
                "That check is required by FlexRadio and cannot be skipped or done remotely — which is why a radio " +
                "must be registered before it is shipped anywhere.");

            string jacks = PhysicalKeyingGuidance();
            if (jacks != null)
                check.Warnings.Add(jacks);

            check.CanProceed = true;
            return check;
        }

        /// <summary>
        /// Tactile, model-specific directions for plugging in a microphone or
        /// CW key well enough to key the radio — for the registration
        /// proof-of-presence and for first PTT generally. "Key the mic" is an
        /// instruction that assumes eyes; this is the app doing the looking.
        ///
        /// FLEX-8400/8600 facts verified against the FlexRadio FLEX-8000
        /// Hardware Reference Manual v1.0 (11/2024), sections 1.4, 19.4-19.5,
        /// 20.6, including the rear-panel photographs: standard models have a
        /// bare front panel (power button only); MIC, KEY, PHONES and PTT are
        /// all rear-panel; the MIC jack carries NO PTT — the FHM-3's PTT
        /// button only works with its second, RCA plug in the PTT jack.
        ///
        /// Returns null for models we have not physically verified (6000
        /// series, Aurora): wrong tactile directions are worse than none.
        /// Aurora is "based on" the 8400/8600 but its panel has not been
        /// confirmed — do not fold it in without checking its manual.
        /// </summary>
        private string PhysicalKeyingGuidance()
        {
            string model = RadioModel ?? string.Empty;
            if (!model.Contains("8400") && !model.Contains("8600"))
                return null;

            return
                $"Where to plug in on a {model}: every jack is on the rear panel — the front has only the power button. " +
                "Facing the back, find the VGA-style accessory connector with its two screw posts, right of center; it is " +
                "the easiest landmark to identify by touch. Just left of it is a square of four identical small jacks in " +
                "two columns of two. The microphone jack is the bottom jack of the column nearest the accessory " +
                "connector, and the CW key jack is directly above it. " +
                "The hand mic that came with the radio has two plugs, and both matter: the small plug goes into that " +
                "microphone jack, and the RCA plug goes into the push-to-talk jack — the top-left RCA in the block of " +
                "eight RCA jacks just right of the accessory connector. Without the RCA plug, the mic's PTT button " +
                "does nothing at all. " +
                "A CW key or paddle plugged into the key jack also works for this, and needs only its one plug.";
        }

        /// <summary>Answer to "is the connected radio registered to the signed-in account?"</summary>
        public enum SmartLinkRegistrationQuery
        {
            /// <summary>Cannot be determined — accounts exist but none is signed in, or SmartLink unreachable.</summary>
            Unknown,
            Registered,
            /// <summary>Not in this account's radio list. It may still be registered to a different account.</summary>
            NotRegistered,
            /// <summary>
            /// No SmartLink account has ever been saved on this computer — the
            /// virgin-radio case. Distinct from Unknown so a caller can suggest
            /// setting SmartLink up instead of staying silent: silence is right
            /// when the server is unreachable, wrong when the user has simply
            /// never been told SmartLink exists.
            /// </summary>
            NoAccount,
        }

        /// <summary>
        /// Find out whether the connected radio is registered to the signed-in
        /// SmartLink account.
        ///
        /// The radio itself cannot answer this — probed 2026-08-03: there is no
        /// wan-status query command, the discovery packet carries nothing, and
        /// FlexLib's WanRadioAuthenticated is dead vendor code that nothing
        /// assigns. The authority is the SmartLink server's per-account radio
        /// list, which is also how SmartSDR knows. So this compares the radio's
        /// serial against that list, connecting the account's SmartLink session
        /// if one is not already up.
        ///
        /// Network-bound: may take several seconds when the session has to be
        /// established. Callers should treat it as background work, and treat
        /// Unknown as "say nothing" — a suggestion built on a guess is worse
        /// than no suggestion.
        /// </summary>
        public Task<SmartLinkRegistrationQuery> QuerySmartLinkRegistrationAsync()
        {
            var serial = theRadio?.Serial;
            if (string.IsNullOrEmpty(serial) || !IsConnected)
            {
                Tracing.TraceLine("QuerySmartLinkRegistration: no radio connected", TraceLevel.Info);
                return Task.FromResult(SmartLinkRegistrationQuery.Unknown);
            }

            // Connected over SmartLink is proof of registration by itself.
            if (theRadio.IsWan)
                return Task.FromResult(SmartLinkRegistrationQuery.Registered);

            // A local connection never loads the account on its own — fall back
            // to the saved one so the query can give a real answer instead of
            // an Unknown that silences the not-registered advisory.
            TryLoadSavedAccount();
            var account = _currentAccount;
            if (account == null)
            {
                bool anySaved = SmartLinkAccountManager.AnySavedAccounts();
                Tracing.TraceLine($"QuerySmartLinkRegistration: no current account, savedAccounts={anySaved}", TraceLevel.Info);
                return Task.FromResult(anySaved
                    ? SmartLinkRegistrationQuery.Unknown
                    : SmartLinkRegistrationQuery.NoAccount);
            }

            // Fresh list already in hand (e.g. the user browsed Remote radios
            // this session) — answer without touching the network.
            if (wanListReceived && radios != null)
                return Task.FromResult(SerialInWanList(serial));

            return Task.Run(() =>
            {
                try
                {
                    // Silent only: this runs unprompted at connect time (and from
                    // Radio Setup's status refresh). If the token cannot be
                    // refreshed without a login page, the answer is Unknown —
                    // never interrupt the user with auth UI they did not ask for.
                    // markUsed: false — this is background bookkeeping, not the
                    // operator using the account, and it must not perturb the
                    // LastUsed ordering shown in the account manager.
                    string jwt = GetJwtFromSavedAccount(account, allowInteractiveLogin: false, markUsed: false);
                    if (string.IsNullOrEmpty(jwt))
                    {
                        Tracing.TraceLine("QuerySmartLinkRegistration: no JWT available silently", TraceLevel.Info);
                        return SmartLinkRegistrationQuery.Unknown;
                    }

                    var result = ConnectToSmartLink(jwt);
                    // NoRadios is a definitive answer: the session is alive and the
                    // account simply has no radios — so this one is not registered.
                    if (result == SmartLinkConnectResult.NoRadios)
                        return SmartLinkRegistrationQuery.NotRegistered;
                    if (result != SmartLinkConnectResult.Success)
                    {
                        Tracing.TraceLine($"QuerySmartLinkRegistration: SmartLink connect result={result}", TraceLevel.Info);
                        return SmartLinkRegistrationQuery.Unknown;
                    }

                    return SerialInWanList(serial);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"QuerySmartLinkRegistration: {ex.Message}", TraceLevel.Error);
                    return SmartLinkRegistrationQuery.Unknown;
                }
            });
        }

        private SmartLinkRegistrationQuery SerialInWanList(string serial)
        {
            var list = radios;
            if (list == null) return SmartLinkRegistrationQuery.Unknown;
            foreach (var r in list)
            {
                if (string.Equals(r.Serial, serial, StringComparison.OrdinalIgnoreCase))
                    return SmartLinkRegistrationQuery.Registered;
            }
            return SmartLinkRegistrationQuery.NotRegistered;
        }

        /// <summary>
        /// Register this radio to the signed-in SmartLink account.
        ///
        /// Progress arrives through the radio's WanOwnerHandshakeStatus property, so
        /// the caller supplies a callback that fires on every state change with the
        /// text from <see cref="RegistrationStateText"/>. The subscription is dropped
        /// once the handshake reaches any terminal state.
        /// </summary>
        /// <returns>False if the command could not be sent at all.</returns>
        public bool BeginSmartLinkRegistration(Action<string, bool> onStateChange)
            => SendRegistrationCommand(register: true, onStateChange);

        /// <summary>
        /// Remove this radio's SmartLink registration.
        ///
        /// Dangerous in a way that is not obvious: re-registering requires physically
        /// keying the radio, so unregistering a radio you cannot reach strands it —
        /// it can never be reached over SmartLink again without someone travelling to
        /// it. Callers must warn about this in the strongest terms they have.
        /// </summary>
        public bool BeginSmartLinkUnregistration(Action<string, bool> onStateChange)
            => SendRegistrationCommand(register: false, onStateChange);

        private bool SendRegistrationCommand(bool register, Action<string, bool> onStateChange, int attempt = 1)
        {
            if (theRadio == null || !IsConnected || _currentAccount == null) return false;

            try
            {
                var r = theRadio;

                string jwt = GetJwtFromSavedAccount(_currentAccount);
                if (string.IsNullOrEmpty(jwt))
                {
                    Tracing.TraceLine("SendRegistrationCommand: no JWT available", TraceLevel.Error);
                    return false;
                }

                System.ComponentModel.PropertyChangedEventHandler handler = null;
                handler = (s, e) =>
                {
                    if (e.PropertyName != "WanOwnerHandshakeStatus") return;

                    var state = r.WanOwnerHandshakeStatus;
                    bool terminal =
                        state == Radio.WanRadioRegistrationState.RegisterSuccess
                        || state == Radio.WanRadioRegistrationState.UnregisterSuccess
                        || state == Radio.WanRadioRegistrationState.FailedPTT
                        || state == Radio.WanRadioRegistrationState.FailedServerConnection
                        || state == Radio.WanRadioRegistrationState.FailedServerConfirmation
                        || state == Radio.WanRadioRegistrationState.FailedNotLicensed
                        || state == Radio.WanRadioRegistrationState.FailedUnknown;

                    Tracing.TraceLine($"SendRegistrationCommand: state={state} terminal={terminal} attempt={attempt}", TraceLevel.Info);

                    // The SmartLink server itself is flaky: live run 2026-08-04,
                    // its first registration attempt was refused and the second
                    // (identical) one worked. Retry ONCE on the two server-side
                    // failures only — FailedPTT and FailedNotLicensed are radio-
                    // or account-definitive and repeating them cannot succeed.
                    bool serverSideFailure =
                        state == Radio.WanRadioRegistrationState.FailedServerConnection
                        || state == Radio.WanRadioRegistrationState.FailedServerConfirmation;
                    if (terminal && serverSideFailure && attempt == 1)
                    {
                        r.PropertyChanged -= handler;
                        Tracing.TraceLine($"SendRegistrationCommand: server-side failure ({state}), retrying once", TraceLevel.Warning);
                        onStateChange?.Invoke("The SmartLink server refused the first attempt. Trying again.", false);
                        Task.Run(() =>
                        {
                            Thread.Sleep(2000);
                            // Re-entering fetches a fresh JWT — SmartLink id_tokens
                            // live 60 seconds, so the first one may already be dead.
                            if (!SendRegistrationCommand(register, onStateChange, attempt: 2))
                                onStateChange?.Invoke("The retry could not be sent. See the trace file for details.", true);
                        });
                        return;
                    }

                    onStateChange?.Invoke(RegistrationStateText, terminal);

                    if (terminal) r.PropertyChanged -= handler;
                };

                r.PropertyChanged += handler;

                if (register) r.WanRegisterRadio(jwt);
                else r.WanUnregisterRadio(jwt);

                Tracing.TraceLine($"SendRegistrationCommand: sent {(register ? "register" : "unregister")} for account {_currentAccount.Email}", TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SendRegistrationCommand: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        #endregion

        #region SmartLink reachability — what the radio and SmartLink actually report

        /// <summary>
        /// True when SmartLink has told us this radio needs a UDP hole-punch,
        /// meaning neither a forwarded port nor UPnP gave it a way in.
        ///
        /// This is the flag that decides whether a hole-punch port gets chosen at
        /// connect time — not the user's tier preference. See
        /// <c>sendRemoteConnect</c> and the note there about
        /// <c>NegotiatedHolePunchPort</c> being a value the client supplies.
        /// </summary>
        public bool RadioRequiresHolePunch
        {
            get { try { return theRadio != null && theRadio.RequiresHolePunch; } catch { return false; } }
        }

        /// <summary>True when the radio reports a forwarded port is in play.</summary>
        public bool RadioPortForwardActive
        {
            get { try { return theRadio != null && theRadio.IsPortForwardOn; } catch { return false; } }
        }

        /// <summary>The public TCP port SmartLink says the radio is reachable on, or 0.</summary>
        public int RadioPublicTlsPort
        {
            get { try { return theRadio?.PublicTlsPort ?? 0; } catch { return 0; } }
        }

        /// <summary>The public UDP port SmartLink says the radio is reachable on, or 0.</summary>
        public int RadioPublicUdpPort
        {
            get { try { return theRadio?.PublicUdpPort ?? 0; } catch { return 0; } }
        }

        /// <summary>True when the current connection is going through SmartLink.</summary>
        public bool IsWanConnection
        {
            get { try { return theRadio != null && theRadio.IsWan; } catch { return false; } }
        }

        /// <summary>
        /// Serial of the radio most recently connected to, kept after the
        /// connection ends. The network diagnostic needs a serial and a SmartLink
        /// session but not a live radio, so this is what lets it run in the state
        /// it exists for — when the connect failed.
        /// </summary>
        public string SelectedRadioSerial { get; private set; } = string.Empty;

        /// <summary>
        /// The hole-punch port chosen for the most recent remote connect, or 0 if
        /// hole-punch was not used. Recorded so the Network tab can show what
        /// actually happened rather than what was configured — the two differ
        /// whenever the radio did not ask for a hole-punch.
        /// </summary>
        public int LastHolePunchPort { get; private set; }

        // QB Track C: ConfiguredHolePunchPort (=> _currentAccount?.ConfiguredListenPort)
        // was removed here. It read the account listen-port field under its PUNCH
        // meaning; that meaning now lives per-radio (RadioConfig.FixedHolePunchPort)
        // and the account field keeps only the forwarded-port meaning. Its one
        // consumer, the Network tab's account-level punch-port editor, is gone too.

        // ── QB Track D — connect-failure truth ─────────────────────────
        //
        // Why this exists: Don's traces read fwdTcp=False for hours while
        // humans guessed at router rules. The evidence was in hand and the
        // user heard "connection failed". These members hold the composed
        // story of the most recent failed connect so every caller can speak
        // the reason itself instead of a shrug.

        /// <summary>
        /// Classified evidence for the most recent failed connect attempt,
        /// or null when the last attempt succeeded (or none was made).
        /// Reset at the top of every <see cref="Connect(string, bool)"/>.
        /// </summary>
        public ConnectFailureReport? LastConnectFailureReport { get; private set; }

        /// <summary>
        /// The speakable form of <see cref="LastConnectFailureReport"/> —
        /// summary sentence(s) plus the verbatim router rule when the
        /// evidence points at the router. Null when there is nothing to say.
        ///
        /// MERGE SEAM (Track C → Track D, 2026-08-07): Track C's branch
        /// carries a string property of this same name, set only on its
        /// ForwardOnly pre-attempt fail-fast and cleared at Connect() /
        /// sendRemoteConnect() entry. At merge, THIS computed property owns
        /// the name; C's assignment sites become
        /// RecordConnectFailure(new ConnectFailureReport {
        ///   Class = ConnectFailureClass.PreflightRefused,
        ///   SpokenSummary = &lt;C's message text&gt; }).
        /// The Connect()-entry reset already exists here; add the same
        /// reset at sendRemoteConnect() entry if C's contract requires it.
        /// </summary>
        public string? LastConnectFailureAdvice => LastConnectFailureReport?.ComposeSpeech();

        /// <summary>
        /// Record a composed failure report and trace it. Internal seam so
        /// every failure site files its evidence the same way.
        /// </summary>
        internal void RecordConnectFailure(ConnectFailureReport report)
        {
            LastConnectFailureReport = report;
            report.Trace();
        }

        /// <summary>
        /// Cached SmartLink test_connection report for a radio, if one has
        /// been collected this app run. Cache read only — never triggers a
        /// probe, so it is safe to consult from any state including a live
        /// hole-punched session.
        /// </summary>
        public Radios.SmartLink.NetworkDiagnosticReport? LastNetworkReportFor(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return null;
            try
            {
                return Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.GetLastNetworkReport(serial);
            }
            catch { return null; }
        }

        /// <summary>
        /// The radio's last-known LAN address from the connection cache, or
        /// null if this machine has never seen it on the LAN. This is what
        /// lets the router-rule text name a real address for a radio we can
        /// currently only reach (or fail to reach) over SmartLink.
        /// </summary>
        public string? CachedLanIpFor(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return null;
            try
            {
                string? lanIp = GetRadioConnectionCache().Lookup(serial)?.LanIp;
                return string.IsNullOrWhiteSpace(lanIp) ? null : lanIp;
            }
            catch { return null; }
        }

        /// <summary>
        /// The exact router rule this radio needs, built entirely from
        /// radio-reported values: advertised external ports, cached LAN IP,
        /// fixed internal TCP 4994 / UDP 4993. Null when the radio has not
        /// advertised external ports (e.g. pure hole-punch site). Public so
        /// any settings / guidance surface can offer the same verbatim text.
        /// </summary>
        public string? BuildRouterRuleText()
        {
            var r = theRadio;
            if (r == null) return null;
            return RouterRuleAdvisor.BuildRouterRuleText(
                r.PublicTlsPort, r.IsWan ? r.PublicUdpPort : 0, CachedLanIpFor(r.Serial));
        }

        /// <summary>
        /// Compose the failure story for a failed REMOTE connect, from the
        /// evidence already in hand plus (when safe) evidence one probe away:
        ///
        /// 1. The SmartLink test_connection report — reachability ground
        ///    truth measured from OUTSIDE the user's network. Cached copy is
        ///    used when present; a fresh probe is fetched only on the
        ///    forwarded path. NEVER auto-run on a hole-punch radio — the
        ///    radio-side probe is at minimum useless there and was implicated
        ///    in killing punched sessions (the f842e93f gate's reasoning).
        /// 2. A client-side TCP classification of the radio's public port —
        ///    refused (router answered, nothing behind the rule) versus
        ///    timed out (packets never arrived) are different diseases with
        ///    different medicine, and both used to be "open failed".
        /// 3. Radio-reported flags (forwarding on, ports, punch required).
        /// </summary>
        private ConnectFailureReport BuildRemoteConnectFailureReport(Radio r, bool remoteHandshakeFailed)
        {
            var detail = new List<string>();
            bool punch = false;
            try { punch = r.RequiresHolePunch; } catch { }
            int tlsPort = 0, udpPort = 0;
            try { tlsPort = r.PublicTlsPort; udpPort = r.PublicUdpPort; } catch { }
            detail.Add($"radio-reported path: {(punch ? "hole punch" : "forwarded ports")}, TCP {tlsPort}, UDP {udpPort}, forwarding flag {(RadioPortForwardActive ? "on" : "off")}");

            string? ruleText = BuildRouterRuleText();

            // Evidence source 1 — SmartLink's own outside-in probe.
            Radios.SmartLink.NetworkDiagnosticReport? probe = LastNetworkReportFor(r.Serial);
            if (probe == null && !punch)
            {
                // Forwarded path with no cached report: fetch one now. The
                // connect already failed, so there is no live session to
                // endanger, and eight seconds of waiting buys the difference
                // between evidence and a shrug. Punch path: never — see the
                // method doc.
                try
                {
                    var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
                    if (session != null && session.IsConnected)
                    {
                        Tracing.TraceLine("BuildRemoteConnectFailureReport: fetching fresh test_connection (forwarded path, no cached report)", TraceLevel.Info);
                        var task = session.RunNetworkDiagnosticAsync(r.Serial, forceRefresh: false, timeout: TimeSpan.FromSeconds(8));
                        if (task.Wait(9000)) probe = task.Result;
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"BuildRemoteConnectFailureReport: probe fetch failed: {ex.Message}", TraceLevel.Warning);
                }
            }
            if (probe != null && probe.ProbeCompleted)
            {
                detail.Add($"SmartLink outside-in test ({probe.TimestampUtc:HH:mm:ss} UTC): forwarded TCP {YesNo(probe.ManualForwardTcpReachable)}, forwarded UDP {YesNo(probe.ManualForwardUdpReachable)}, UPnP TCP {YesNo(probe.UpnpTcpReachable)}, UPnP UDP {YesNo(probe.UpnpUdpReachable)}, NAT hole-punch support {YesNo(probe.NatSupportsHolePunch)}");
            }
            else if (probe != null)
            {
                detail.Add($"SmartLink outside-in test did not complete: {probe.ErrorDetail}");
            }

            // Evidence source 2 — our own TCP classification of the public
            // port. Forwarded path only; a punch radio has no listening
            // public TCP port to classify.
            TcpProbeResult? tcp = null;
            if (!punch && tlsPort > 0 && r.IP != null)
            {
                tcp = TcpReachabilityProbe.Classify(r.IP, tlsPort);
                detail.Add($"client-side TCP check of {r.IP}:{tlsPort}: {tcp.Outcome} after {tcp.ElapsedMs}ms ({tcp.Detail})");
            }

            // Compose the spoken story, most specific evidence first.
            ConnectFailureClass cls;
            string spoken;

            if (punch)
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = "Could not open a hole-punched connection to the radio. "
                    + "The radio has no forwarded ports, so the connection depends on both routers allowing the punch, and some networks never will.";
                if (probe?.ProbeCompleted == true && probe.NatSupportsHolePunch == false)
                {
                    spoken += " SmartLink's own network test reports this network does not support hole punch — a forwarded port on the radio's router is the reliable path.";
                }
                else
                {
                    spoken += " Trying again sometimes wins the timing; a forwarded port on the radio's router is the reliable fix.";
                }
                // A rule is still worth stating on the punch path when the
                // radio advertises nothing: the fix IS creating the rule.
                // But with no advertised external ports there are no honest
                // numbers to speak, so ruleText stays null unless the radio
                // provided them.
            }
            else if (tcp != null && tcp.Outcome == TcpProbeOutcome.Refused)
            {
                cls = ConnectFailureClass.TransportRefused;
                spoken = $"The radio's router answered on port {tlsPort} and refused the connection after {tcp.ElapsedMs} milliseconds — "
                    + "the port forward reaches the router, but nothing is listening behind the rule. "
                    + "The rule may point at the wrong LAN address, or the radio may be off.";
            }
            else if (tcp != null && (tcp.Outcome == TcpProbeOutcome.TimedOut || tcp.Outcome == TcpProbeOutcome.Unreachable))
            {
                cls = ConnectFailureClass.TransportTimedOut;
                spoken = $"Connection attempts to the radio's public port {tlsPort} got no answer at all — the packets never arrived. "
                    + "That usually means the router rule is missing, a firewall is dropping them, or the radio's public address has changed.";
                if (probe?.ProbeCompleted == true && probe.ManualForwardTcpReachable == false)
                {
                    spoken = $"SmartLink tested the radio's forwarded TCP port from the internet and could not reach it, and this computer's own check got no answer either. "
                        + "The router rule is the likely problem.";
                }
            }
            else if (probe?.ProbeCompleted == true && probe.ManualForwardTcpReachable == false && RadioPortForwardActive)
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = "The radio reports its forwarded TCP port is not reachable from the internet — SmartLink tested it from outside. Check the router rule.";
            }
            else if (remoteHandshakeFailed)
            {
                cls = ConnectFailureClass.RemoteHandshakeFailed;
                spoken = "SmartLink accepted the request, but the radio never reported ready to connect. The radio may be busy, restarting, or just dropped off the network. Trying again usually works.";
                ruleText = null; // no evidence pointing at the router — don't send anyone there
            }
            else if (tcp != null && tcp.Outcome == TcpProbeOutcome.Connected)
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = $"The radio's public port {tlsPort} answers from here, so the router rule looks right — the failure happened after the port, most likely in the secure handshake with the radio. Trying again usually works.";
                ruleText = null; // the rule is demonstrably fine
            }
            else
            {
                cls = ConnectFailureClass.TransportFailed;
                spoken = "Could not connect to the radio over SmartLink.";
                if (probe?.ProbeCompleted == true
                    && (probe.ManualForwardTcpReachable == true || probe.UpnpTcpReachable == true))
                {
                    spoken += " SmartLink's network test says the radio's port is reachable from the internet, so this looks like a passing problem — trying again usually works.";
                    ruleText = null;
                }
            }

            return new ConnectFailureReport
            {
                Class = cls,
                SpokenSummary = spoken,
                RouterRuleText = ruleText,
                DetailLines = detail,
                ProbeReport = probe,
                TcpProbe = tcp,
            };
        }

        private static string YesNo(bool? v) => v switch { true => "yes", false => "no", null => "unknown" };

        #endregion

        #region GPS / GNSS and reference oscillator

        /// <summary>
        /// One reading of everything the radio reports about its GPS receiver and
        /// its 10 MHz reference. Taken as a snapshot rather than read field by
        /// field so a spoken summary describes a single consistent moment.
        ///
        /// Every GPS field is a string because FlexLib passes the radio's own text
        /// through untouched. That is worth preserving — "no fix" and "3D fix" are
        /// more useful spoken than any number we would map them to.
        /// </summary>
        public sealed class GpsStatusSnapshot
        {
            public bool RadioConnected { get; set; }

            /// <summary>Whether a GPS unit is installed at all.</summary>
            public bool GpsInstalled { get; set; }

            // Reference hardware. These are independent flags, not a single choice
            // — an 8000-series radio can report more than one at once.
            public bool HasGpsdo { get; set; }
            public bool HasGnss { get; set; }
            public bool HasTcxo { get; set; }
            public bool HasExternalOscillator { get; set; }

            /// <summary>What the reference is set to (auto, external, gpsdo, tcxo).</summary>
            public string OscillatorSelected { get; set; } = string.Empty;
            /// <summary>What the radio actually settled on. Differs from the above under Auto.</summary>
            public string OscillatorInUse { get; set; } = string.Empty;
            public bool OscillatorLocked { get; set; }

            public string Status { get; set; } = string.Empty;
            public string SatellitesTracked { get; set; } = string.Empty;
            public string SatellitesVisible { get; set; } = string.Empty;
            public string Grid { get; set; } = string.Empty;
            public string Latitude { get; set; } = string.Empty;
            public string Longitude { get; set; } = string.Empty;
            public string Altitude { get; set; } = string.Empty;
            public string UtcTime { get; set; } = string.Empty;

            /// <summary>
            /// The GPS receiver's own frequency-error text, passed through
            /// verbatim. NOT the same figure as <see cref="FreqErrorPpb"/>, and
            /// deliberately not given a unit here — the radio supplies this as
            /// free text and inventing a unit for it would be exactly the kind
            /// of confident wrong readout this work exists to remove.
            /// </summary>
            public string FreqError { get; set; } = string.Empty;

            /// <summary>
            /// The radio's clock correction in parts per billion
            /// (<c>freq_error_ppb</c>). This is the reference's accuracy figure:
            /// how far off the radio believes its own oscillator is, and how
            /// much it is compensating. Zero is a real answer meaning no
            /// correction applied, not a missing one — FlexLib exposes an int
            /// with no reported/not-reported distinction, so we do not invent
            /// one.
            /// </summary>
            public int FreqErrorPpb { get; set; }

            public string Speed { get; set; } = string.Empty;
        }

        /// <summary>
        /// Read everything the radio currently reports about GPS and the reference
        /// oscillator. Cheap — these are all cached properties updated by radio
        /// status messages, so this can be called on a timer without cost.
        /// </summary>
        public GpsStatusSnapshot ReadGpsStatus()
        {
            var s = new GpsStatusSnapshot();
            if (theRadio == null || !IsConnected)
            {
                Tracing.TraceLine($"ReadGpsStatus: not connected (theRadio={(theRadio == null ? "null" : "present")}, IsConnected={IsConnected})", TraceLevel.Info);
                return s;
            }

            try
            {
                var r = theRadio;
                s.RadioConnected = true;
                s.GpsInstalled = r.GPSInstalled;
                s.HasGpsdo = r.IsGpsdoPresent;
                s.HasGnss = r.IsGnssPresent;
                s.HasTcxo = r.IsTcxoPresent;
                s.HasExternalOscillator = r.IsExternalOscillatorPresent;
                s.OscillatorSelected = r.SelectedOscillator.ToString();
                s.OscillatorInUse = r.OscillatorState ?? string.Empty;
                s.OscillatorLocked = r.IsOscillatorLocked;
                s.Status = r.GPSStatus ?? string.Empty;
                s.SatellitesTracked = r.GPSSatellitesTracked ?? string.Empty;
                s.SatellitesVisible = r.GPSSatellitesVisible ?? string.Empty;
                s.Grid = r.GPSGrid ?? string.Empty;
                s.Latitude = r.GPSLatitude ?? string.Empty;
                s.Longitude = r.GPSLongitude ?? string.Empty;
                s.Altitude = r.GPSAltitude ?? string.Empty;
                s.UtcTime = r.GPSUtcTime ?? string.Empty;
                s.FreqError = r.GPSFreqError ?? string.Empty;
                s.FreqErrorPpb = r.FreqErrorPPB;
                s.Speed = r.GPSSpeed ?? string.Empty;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ReadGpsStatus: {ex.Message}", TraceLevel.Error);
            }
            return s;
        }

        /// <summary>
        /// The one-line spoken answer to "is the GPS working?" — lock state,
        /// satellites, and which receiver is doing it, in that order because that
        /// is the order the question is actually asked in.
        /// </summary>
        public static string BuildGpsSpokenSummary(GpsStatusSnapshot s)
        {
            if (s == null || !s.RadioConnected) return "No radio connected.";
            if (!s.GpsInstalled) return "No GPS receiver is installed in this radio.";

            var parts = new List<string>();

            // Lock first. The oscillator lock is the fact that decides whether the
            // radio is actually disciplined; the GPS fix text is the supporting
            // detail, and the two can disagree while a fix is being acquired.
            parts.Add(s.OscillatorLocked
                ? "Reference locked."
                : "Reference not locked.");

            // The accuracy figure, right next to lock, because the two together
            // are the whole answer to "is my reference any good". Lock says the
            // radio is disciplined; PPB says how well. Zero is a real answer.
            parts.Add(FormatFreqErrorPpb(s.FreqErrorPpb) + ".");

            if (!string.IsNullOrWhiteSpace(s.Status))
                parts.Add($"GPS status {s.Status}.");

            if (!string.IsNullOrWhiteSpace(s.SatellitesVisible) || !string.IsNullOrWhiteSpace(s.SatellitesTracked))
            {
                string visible = string.IsNullOrWhiteSpace(s.SatellitesVisible) ? "unknown" : s.SatellitesVisible;
                string tracked = string.IsNullOrWhiteSpace(s.SatellitesTracked) ? "unknown" : s.SatellitesTracked;
                parts.Add($"{visible} satellites visible, {tracked} tracked.");
            }

            parts.Add("Reference in use " + DescribeOscillatorInUse(s) + ".");

            if (!string.IsNullOrWhiteSpace(s.Grid))
                parts.Add($"Grid {s.Grid}.");

            return string.Join(" ", parts);
        }

        /// <summary>
        /// The clock correction as a spoken phrase. Singular is worth the two
        /// lines: "1 parts per billion" through a screen reader is the kind of
        /// small wrongness that makes an instrument sound careless.
        /// </summary>
        public static string FormatFreqErrorPpb(int ppb)
        {
            if (ppb == 0) return "No clock correction applied";
            int magnitude = Math.Abs(ppb);
            return "Clock correction " + ppb + (magnitude == 1 ? " part" : " parts")
                + " per billion";
        }

        /// <summary>
        /// Name the reference the radio is actually running on.
        ///
        /// The awkward bit: FlexLib's Oscillator enum has only auto, external,
        /// gpsdo and tcxo — there is no separate "gnss" value, even though
        /// IsGnssPresent is reported independently of IsGpsdoPresent. So on a
        /// radio carrying both a legacy GPSDO and a newer GNSS module, the
        /// selection still reads "gpsdo" and the two presence flags are the only
        /// way to tell what hardware is actually in the box. Say so rather than
        /// guessing.
        /// </summary>
        public static string DescribeOscillatorInUse(GpsStatusSnapshot s)
        {
            string inUse = string.IsNullOrWhiteSpace(s.OscillatorInUse) ? s.OscillatorSelected : s.OscillatorInUse;
            if (string.IsNullOrWhiteSpace(inUse)) return "unknown";

            string label = inUse.ToLowerInvariant() switch
            {
                "gpsdo" => "the GPS-disciplined oscillator",
                "tcxo" => "the internal temperature-compensated oscillator",
                "external" => "an external 10 megahertz reference",
                "auto" => "automatic",
                _ => inUse,
            };

            // Only worth mentioning when it is genuinely ambiguous.
            if (inUse.Equals("gpsdo", StringComparison.OrdinalIgnoreCase) && s.HasGnss && s.HasGpsdo)
                label += ", and unusually this radio reports both receiver modules installed";

            return label;
        }

        /// <summary>
        /// Human-readable list of the reference hardware the radio reports as
        /// installed. This is the answer to "which receiver do I actually have".
        ///
        /// The GNSS (10-channel) and GPSDO (32-channel) modules are mutually
        /// exclusive in practice — verified on a FLEX-8600 ordered with the GPSDO
        /// option, which reports gpsdo_present=1 and gnss_present=0. So there is
        /// only ever one GPS antenna connector that matters. The both-present
        /// branch below is defensive only, not an expected configuration.
        /// </summary>
        public static string DescribeInstalledReferences(GpsStatusSnapshot s)
        {
            if (s == null || !s.RadioConnected) return "No radio connected.";

            var found = new List<string>();
            // The two flags name WHICH RECEIVER MODULE is fitted, not which
            // constellations it can hear. The GPSDO unit is the 32-channel
            // receiver and takes GLONASS, Galileo and BeiDou as well as GPS;
            // the GNSS flag is the 10-channel unit. Do not describe either as
            // "legacy" — the GPSDO is the better of the two.
            if (s.HasGnss) found.Add("a 10-channel GNSS receiver");
            if (s.HasGpsdo) found.Add("a 32-channel GPS-disciplined oscillator");
            if (s.HasTcxo) found.Add("a temperature-compensated oscillator");
            if (s.HasExternalOscillator) found.Add("an external 10 megahertz reference input in use");

            if (found.Count == 0)
                return "The radio reports no reference hardware installed beyond its standard oscillator.";

            string list = found.Count == 1
                ? found[0]
                : string.Join(", ", found.GetRange(0, found.Count - 1)) + " and " + found[found.Count - 1];

            string text = "The radio reports " + list + " installed.";

            if (s.HasGnss && s.HasGpsdo)
                text += " Unusually, both receiver modules are reported. Normally a radio has one or the other — " +
                        "ordering the 32-channel unit means it is fitted instead of the 10-channel one, not alongside it. " +
                        "If both really are present, JJ Flex cannot tell you which antenna connector feeds which — the radio does not report that.";

            return text;
        }

        /// <summary>
        /// The reference oscillator the user has asked for. Setting it sends
        /// <c>radio oscillator …</c> immediately.
        ///
        /// Accepted values are the FlexLib names: auto, external, gpsdo, tcxo.
        /// Returns empty when there is no radio.
        /// </summary>
        public string SelectedOscillator
        {
            get
            {
                try { return theRadio?.SelectedOscillator.ToString() ?? string.Empty; }
                catch { return string.Empty; }
            }
        }

        /// <summary>Available reference choices, in the order they should be offered.</summary>
        public static readonly (string Value, string Label)[] OscillatorChoices =
        {
            ("auto",     "Automatic (let the radio choose the best available)"),
            ("gpsdo",    "GPS-disciplined oscillator"),
            ("external", "External 10 MHz reference"),
            ("tcxo",     "Internal temperature-compensated oscillator"),
        };

        /// <summary>
        /// Ask the radio to use a particular reference. Returns false when the
        /// value is not one FlexLib knows or there is no radio.
        /// </summary>
        public bool SetSelectedOscillator(string value)
        {
            if (theRadio == null || !IsConnected) return false;
            try
            {
                if (!Enum.TryParse<Oscillator>(value, ignoreCase: true, out var osc))
                {
                    Tracing.TraceLine($"SetSelectedOscillator: unknown value '{value}'", TraceLevel.Error);
                    return false;
                }
                theRadio.SelectedOscillator = osc;
                Tracing.TraceLine($"SetSelectedOscillator: set to {osc}", TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SetSelectedOscillator: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Subscribe to radio property changes so a status view can update itself
        /// as the GPS acquires. Returns an unsubscribe action, or null when there
        /// is no radio — callers must invoke it when the view closes.
        /// </summary>
        public Action SubscribeGpsChanges(Action onChanged)
        {
            if (theRadio == null || onChanged == null) return null;

            var r = theRadio;
            System.ComponentModel.PropertyChangedEventHandler handler = (s, e) =>
            {
                if (e.PropertyName == null) return;
                if (e.PropertyName.StartsWith("GPS", StringComparison.Ordinal)
                    || e.PropertyName.Contains("Oscillator", StringComparison.Ordinal)
                    || e.PropertyName == nameof(Radio.IsGnssPresent)
                    || e.PropertyName == "IsGpsdoPresent")
                {
                    onChanged();
                }
            };

            r.PropertyChanged += handler;
            return () => { try { r.PropertyChanged -= handler; } catch { } };
        }

        #endregion

        #region Firmware update (Sprint 29 Phase D)

        /// <summary>
        /// Result of the pre-flight checks that run before a firmware image is sent.
        /// Deliberately separate from the upload itself so the UI can show the user
        /// exactly what it found — file size, computed hash, who else is connected —
        /// and let them confirm before anything touches the radio.
        /// </summary>
        public sealed class FirmwareUpdateCheck
        {
            /// <summary>True when nothing blocks the upload. Warnings may still be set.</summary>
            public bool CanProceed { get; set; }

            /// <summary>Why the upload is blocked. Empty when CanProceed is true.</summary>
            public string BlockReason { get; set; } = string.Empty;

            /// <summary>Non-blocking things the user should know before confirming.</summary>
            public List<string> Warnings { get; } = new List<string>();

            /// <summary>Size of the image on disk, in bytes.</summary>
            public long SizeBytes { get; set; }

            /// <summary>SHA256 of the image as found on disk, lower-case hex.</summary>
            public string ActualSha256 { get; set; } = string.Empty;

            /// <summary>File name that will be sent to the radio.</summary>
            public string FileName { get; set; } = string.Empty;
        }

        /// <summary>
        /// Verify a firmware image and the radio's state before uploading. Never
        /// modifies anything — safe to call as often as the UI likes.
        ///
        /// The integrity check has to live here rather than in FlexLib: the vendor's
        /// upload path carries a "TODO: verify file integrity" that was never
        /// implemented, and it swallows every failure with a Debug.WriteLine. Once
        /// bytes start moving there is no completion signal, so everything we can
        /// check has to be checked first.
        /// </summary>
        /// <param name="path">Full path to the .ssdr image.</param>
        /// <param name="expectedSha256">
        /// Known-good SHA256 (hex, case-insensitive). Pass null or empty to skip the
        /// comparison — the hash is still computed and reported so the caller can show it.
        /// </param>
        public FirmwareUpdateCheck PreflightFirmwareUpdate(string path, string expectedSha256 = null)
        {
            var check = new FirmwareUpdateCheck();

            if (theRadio == null || !IsConnected)
            {
                check.BlockReason = "No radio is connected.";
                return check;
            }

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                check.BlockReason = "The firmware file could not be found.";
                return check;
            }

            try
            {
                var info = new System.IO.FileInfo(path);
                check.FileName = info.Name;
                check.SizeBytes = info.Length;

                if (info.Length == 0)
                {
                    check.BlockReason = "The firmware file is empty.";
                    return check;
                }

                // Hash the whole file. On a ~60 MB image this is fast, and it doubles
                // as proof the file is readable end to end before FlexLib re-reads it.
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = System.IO.File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    check.ActualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PreflightFirmwareUpdate: {ex.Message}", TraceLevel.Error);
                check.BlockReason = "The firmware file could not be read: " + ex.Message;
                return check;
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string expected = expectedSha256.Trim().ToLowerInvariant();
                if (!string.Equals(expected, check.ActualSha256, StringComparison.Ordinal))
                {
                    check.BlockReason =
                        "The firmware file does not match its expected checksum. " +
                        "Do not send it to the radio — download it again.";
                    return check;
                }
            }
            else
            {
                check.Warnings.Add(
                    "No expected checksum was supplied, so the file's integrity could not be confirmed.");
            }

            // Transmitting during an update is a bad idea and easy to rule out.
            try
            {
                if (theRadio.Mox)
                {
                    check.BlockReason = "The radio is transmitting. Stop transmitting before updating firmware.";
                    return check;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PreflightFirmwareUpdate: Mox read failed: {ex.Message}", TraceLevel.Error);
            }

            // Other clients don't block the update, but the user should know they're
            // about to take the radio away from someone.
            var others = OtherConnectedStations;
            if (others.Count > 0)
            {
                check.Warnings.Add(
                    "Other stations are connected and will lose the radio: " + string.Join(", ", others));
            }

            check.CanProceed = true;
            return check;
        }

        /// <summary>The firmware version the radio is currently running.</summary>
        public string RadioFirmwareVersion
        {
            get
            {
                try
                {
                    if (theRadio == null || theRadio.Version == 0) return string.Empty;
                    return Flex.Util.FlexVersion.ToString(theRadio.Version);
                }
                catch { return string.Empty; }
            }
        }

        /// <summary>
        /// The firmware version the vendored FlexLib was built against.
        ///
        /// FlexLib demands an <b>exact</b> match — `_version != _req_version` sets
        /// UpdateRequired — so this is not a floor, it is a specific build. Ours is
        /// pinned well behind current firmware, which is expected while main still
        /// vendors the older FlexLib.
        /// </summary>
        public static string LibraryExpectedFirmwareVersion
        {
            get
            {
                try { return Flex.Util.FlexVersion.ToString(FirmwareRequiredVersion.RequiredVersion); }
                catch { return string.Empty; }
            }
        }

        /// <summary>
        /// True when the radio's firmware differs from what this build of FlexLib
        /// expects.
        ///
        /// Important: this does <b>not</b> block connecting. FlexLib only uses the
        /// mismatch to set <c>ConnectedState</c> to "Update" — nothing in
        /// <c>Connect()</c> refuses, and JJ Flex does not gate on it either. So a
        /// mismatch is worth reporting and is not worth panicking about.
        /// </summary>
        public bool FirmwareDiffersFromLibraryExpectation
        {
            get
            {
                try
                {
                    return theRadio != null
                        && theRadio.Version != 0
                        && theRadio.Version != FirmwareRequiredVersion.RequiredVersion;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// True when FlexRadio's own developer bypass file is present, which makes
        /// FlexLib skip the exact-version check entirely.
        ///
        /// The file is a marker only — its contents are never read. Path:
        /// <c>%APPDATA%\FlexRadio Systems\smoothlake_dev</c>. Surfaced so the UI can
        /// explain why a version mismatch is or is not being reported.
        /// </summary>
        public static bool FirmwareVersionCheckBypassed
        {
            get
            {
                try { return Radio.SmoothlakeDevFileExists(); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Create FlexRadio's developer bypass file so FlexLib stops flagging a
        /// firmware mismatch. Returns the path on success, empty on failure.
        ///
        /// This suppresses a label, not a safety check — FlexLib never refused to
        /// connect on version mismatch in the first place. It exists so a radio
        /// running newer firmware than the vendored library expects stops presenting
        /// itself as needing an update.
        /// </summary>
        public static string CreateFirmwareVersionCheckBypass()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FlexRadio Systems");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "smoothlake_dev");
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, string.Empty);
                Tracing.TraceLine($"CreateFirmwareVersionCheckBypass: {path}", TraceLevel.Info);
                return path;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"CreateFirmwareVersionCheckBypass: {ex.Message}", TraceLevel.Error);
                return string.Empty;
            }
        }

        /// <summary>
        /// Send a firmware image to the radio. Call
        /// <see cref="PreflightFirmwareUpdate"/> first and only call this when it
        /// reports CanProceed.
        ///
        /// Returns as soon as the transfer has been handed to FlexLib — the vendor
        /// provides no completion callback (4.1.x ran it on its own thread; 4.2.x
        /// returns a Task, but it resolves ~5 seconds after the bytes are sent, not
        /// when the radio has applied anything). Track real progress with
        /// <see cref="WatchFirmwareUpdateAsync"/>, which watches discovery across
        /// the reboot.
        /// </summary>
        /// <param name="path">Firmware image to send.</param>
        /// <param name="onTransferFault">
        /// Called (on a worker thread) if the transfer task faults — e.g. the radio
        /// resets the socket mid-upload while entering update mode. The argument is
        /// the low-level exception message. The announcement is spoken here either
        /// way; the callback is for UI text.
        /// </param>
        /// <returns>true if the transfer was started.</returns>
        public bool BeginFirmwareUpdate(string path, Action<string> onTransferFault = null)
        {
            if (theRadio == null || !IsConnected)
            {
                Tracing.TraceLine("BeginFirmwareUpdate: no radio connected", TraceLevel.Error);
                return false;
            }

            try
            {
                Tracing.TraceLine($"BeginFirmwareUpdate: sending {path}", TraceLevel.Info);
                // FlexLib 4.2.x made this async Task where 4.1.x was fire-and-forget
                // void. Completion is still watched via discovery, not this task —
                // but a faulted transfer means the image never arrived, and that
                // must be said out loud instead of letting the UI sit on "sending"
                // (live run 2026-08-05: radio RST the upload socket 1.4s in and
                // the flow sailed on to "waiting for restart").
                theRadio.SendUpdateFile(path).ContinueWith(
                    t =>
                    {
                        string detail = t.Exception?.GetBaseException().Message ?? Lexicon.Get("settings.firmware.unknown_error");
                        Tracing.TraceLine($"BeginFirmwareUpdate: transfer task faulted: {detail}", TraceLevel.Error);
                        ScreenReaderOutput.Speak(
                            Lexicon.Get("settings.firmware.transfer_fault"),
                            VerbosityLevel.Critical, true);
                        try { onTransferFault?.Invoke(detail); }
                        catch (Exception cbEx) { Tracing.TraceLine($"BeginFirmwareUpdate: fault callback threw: {cbEx.Message}", TraceLevel.Error); }
                    },
                    TaskContinuationOptions.OnlyOnFaulted);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"BeginFirmwareUpdate: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// True for the larger 8000-series radios, which take the FLEX-9600
        /// firmware image rather than the common FLEX-6x00 one. "BigBend" is
        /// FlexRadio's own internal codename for the platform.
        /// </summary>
        public bool RadioIsBigBend
        {
            get { try { return theRadio != null && theRadio.IsBigBend; } catch { return false; } }
        }

        #region Firmware update watcher

        /// <summary>Where an in-progress firmware update has got to.</summary>
        public enum FirmwareUpdatePhase
        {
            /// <summary>Image handed to FlexLib; radio still answering.</summary>
            Sending,
            /// <summary>The radio has dropped off the network. This is the expected, healthy sign that it took the image.</summary>
            RadioRestarting,
            /// <summary>The radio is back and reporting a version.</summary>
            RadioReturned,
            /// <summary>Back on a new version. Done.</summary>
            Verified,
            /// <summary>Back on the same version it started with — the update did not take.</summary>
            VersionUnchanged,
            /// <summary>Never left, or never came back, inside the time allowed.</summary>
            TimedOut,
        }

        public sealed class FirmwareUpdateProgress
        {
            public FirmwareUpdatePhase Phase { get; set; }
            public string Message { get; set; } = string.Empty;
            /// <summary>Version the radio was running before the update.</summary>
            public string PreviousVersion { get; set; } = string.Empty;
            /// <summary>Version now reported, once it is back.</summary>
            public string CurrentVersion { get; set; } = string.Empty;
            public bool IsTerminal { get; set; }
        }

        /// <summary>
        /// Watch a firmware update through to a verified answer.
        ///
        /// This exists because FlexLib's <c>SendUpdateFile</c> gives nothing back:
        /// no completion event, no error, every failure path is a Debug.WriteLine
        /// and a return. Without this the honest thing the UI could say was "sent,
        /// good luck".
        ///
        /// So instead of waiting to be told, watch the radio. Discovery packets
        /// carry the firmware version and arrive with no connection at all, which
        /// is what makes this work across the reboot — <c>theRadio</c> is useless
        /// once the radio drops, but <c>API.RadioList</c> keeps seeing it come back.
        ///
        /// The shape of a healthy update is: radio answers, radio disappears
        /// (this is the good sign, not a fault), radio reappears on a different
        /// version. Two failures are distinguishable and worth distinguishing:
        /// never disappearing means the upload silently did nothing, and coming
        /// back on the same version means it was rejected. Both are far more
        /// useful than a timeout.
        ///
        /// Runs on a background task and survives the dialog that started it, so
        /// the result still gets announced if the user closes Settings and goes to
        /// make coffee.
        /// </summary>
        /// <param name="serial">Radio serial to watch. Survives the connection dropping.</param>
        /// <param name="previousVersion">Version before the update, for comparison.</param>
        /// <param name="onProgress">Called on every phase change. May be null.</param>
        /// <param name="speakResult">Announce the terminal result at Critical verbosity.</param>
        public Task WatchFirmwareUpdateAsync(
            string serial,
            string previousVersion,
            Action<FirmwareUpdateProgress> onProgress = null,
            bool speakResult = true,
            System.Threading.CancellationToken cancellationToken = default)
        {
            // Generous ceilings. A normal update is a few minutes; being wrong in
            // the impatient direction means announcing failure on a radio that was
            // going to come back fine.
            TimeSpan maxWaitToLeave = TimeSpan.FromMinutes(5);
            TimeSpan maxWaitToReturn = TimeSpan.FromMinutes(15);
            TimeSpan pollInterval = TimeSpan.FromSeconds(5);

            return Task.Run(async () =>
            {
                void Report(FirmwareUpdatePhase phase, string message, string current = "", bool terminal = false)
                {
                    var p = new FirmwareUpdateProgress
                    {
                        Phase = phase,
                        Message = message,
                        PreviousVersion = previousVersion,
                        CurrentVersion = current,
                        IsTerminal = terminal,
                    };
                    Tracing.TraceLine($"WatchFirmwareUpdate: {phase} — {message}", TraceLevel.Info);
                    try { onProgress?.Invoke(p); } catch (Exception ex) { Tracing.TraceLine($"WatchFirmwareUpdate: progress callback threw: {ex.Message}", TraceLevel.Error); }

                    if (terminal && speakResult)
                        ScreenReaderOutput.Speak(message, VerbosityLevel.Critical, true);
                }

                try
                {
                    Report(FirmwareUpdatePhase.Sending,
                        Lexicon.Get("settings.firmware.sending"));

                    // Phase 1 — wait for the radio to drop off the network.
                    var start = DateTime.UtcNow;
                    bool left = false;
                    while (DateTime.UtcNow - start < maxWaitToLeave)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                        if (FindDiscoveredRadio(serial) == null) { left = true; break; }
                    }

                    if (!left)
                    {
                        Report(FirmwareUpdatePhase.TimedOut,
                            Lexicon.Get("settings.firmware.never_restarted", ("previousVersion", previousVersion)),
                            previousVersion, terminal: true);
                        return;
                    }

                    Report(FirmwareUpdatePhase.RadioRestarting,
                        Lexicon.Get("settings.firmware.restarting"));

                    // Phase 2 — wait for it to come back and tell us its version.
                    start = DateTime.UtcNow;
                    while (DateTime.UtcNow - start < maxWaitToReturn)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);

                        var found = FindDiscoveredRadio(serial);
                        if (found == null) continue;

                        string current = string.Empty;
                        try
                        {
                            if (found.Version != 0) current = Flex.Util.FlexVersion.ToString(found.Version);
                        }
                        catch { }

                        // Seen again but not yet reporting a version — keep waiting
                        // rather than declaring an unknown result.
                        if (string.IsNullOrEmpty(current)) continue;

                        if (!string.IsNullOrEmpty(previousVersion)
                            && string.Equals(current, previousVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            Report(FirmwareUpdatePhase.VersionUnchanged,
                                Lexicon.Get("settings.firmware.version_unchanged", ("current", current)),
                                current, terminal: true);
                            return;
                        }

                        Report(FirmwareUpdatePhase.Verified,
                            Lexicon.Get("settings.firmware.verified", ("current", current)),
                            current, terminal: true);
                        return;
                    }

                    Report(FirmwareUpdatePhase.TimedOut,
                        Lexicon.Get("settings.firmware.not_returned"),
                        terminal: true);
                }
                catch (OperationCanceledException)
                {
                    Tracing.TraceLine("WatchFirmwareUpdate: cancelled", TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"WatchFirmwareUpdate: {ex.Message}", TraceLevel.Error);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Look for a radio by serial in FlexLib's discovery list. Works with no
        /// connection — discovery packets carry the version, which is the whole
        /// reason the watcher can see across a reboot.
        /// </summary>
        private static Radio FindDiscoveredRadio(string serial)
        {
            try
            {
                foreach (var r in API.RadioList)
                {
                    if (r != null && string.Equals(r.Serial, serial, StringComparison.OrdinalIgnoreCase))
                        return r;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"FindDiscoveredRadio: {ex.Message}", TraceLevel.Error);
            }
            return null;
        }

        #endregion

        /// <summary>
        /// True when the radio reports it is in the middle of applying an update, or
        /// has fallen into recovery after a failed one. Recovery is retryable by
        /// sending the same image again — no physical access required, which is the
        /// whole reason it's surfaced.
        /// </summary>
        public bool IsInRecoveryState
        {
            get
            {
                try
                {
                    return theRadio != null
                        && string.Equals(theRadio.ConnectedState, "Update", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(theRadio.Status, "Recovery", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        #endregion

        /// <summary>
        /// Reboot the radio
        /// </summary>
        /// <param name="disconnect">true to disconnect first</param>
        public void Reboot(bool disconnect = false)
        {
            Tracing.TraceLine("Reboot:" + disconnect.ToString(), TraceLevel.Info);
            if (theRadio != null)
            {
                Radio r = theRadio;
                if (disconnect & IsConnected) Disconnect();
                r.RebootRadio();
            }
        }

        /// <summary>
        /// Clear the web cache.
        /// Sprint 10: WebBrowserHelper.ClearCache() removed — IE WebBrowser is replaced by WebView2.
        /// WebView2 manages its own cache. This method is kept as a no-op for API compatibility.
        /// </summary>
        public void ClearWebCache()
        {
            Tracing.TraceLine("ClearWebCache (no-op, WebView2 manages its own cache)", TraceLevel.Info);
        }

        // WAN routines.
        #region WAN
        private List<Radio> radios;
        private bool wanListReceived = false;

        /// <summary>
        /// Serializes list intake across sessions: with one held session per
        /// account (#259), pushes arrive concurrently on N SmartLink receive
        /// threads, and the merge below mutates shared state (myRadioList,
        /// the WAN handle map) that was only ever poked by one thread before.
        /// </summary>
        private static readonly object _wanIntakeLock = new object();

        /// <summary>
        /// The coordinator key for the account this instance's connect flow is
        /// working with — must match what <c>ConnectToSmartLink</c> passes to
        /// <c>EnsureSessionForAccount</c>, so the list-received latch is only
        /// satisfied by the account actually being waited on.
        /// </summary>
        private string CurrentSessionKey => _currentAccount?.Email ?? "default-account";

        /// <summary>
        /// Adapter from the coordinator's attributed list event. Replaces the
        /// old subscription to FlexLib's STATIC WanRadioRadioListRecieved,
        /// which could not say whose list had arrived — fatal once more than
        /// one account's session is open, because the ghost sweep treats a
        /// list as the full truth about its account and account A's list says
        /// nothing about account B's radios.
        /// </summary>
        private void sessionRadioListReceivedHandler(object sender, Radios.SmartLink.SessionRadioListEventArgs e)
        {
            wanRadioListReceivedHandler(e.AccountId ?? "", e.Radios);
        }

        private void wanRadioListReceivedHandler(string accountId, IReadOnlyList<Radio> lst)
        {
            try
            {
              lock (_wanIntakeLock)
              {
                Tracing.TraceLine($"wanRadioListReceivedHandler: account={accountId} count={lst.Count}", TraceLevel.Info);

                // The connect flow's one-shot latch belongs to the account it
                // is waiting on; a push from another held session must not
                // satisfy it with the wrong account's radios.
                if (string.Equals(accountId, CurrentSessionKey, StringComparison.OrdinalIgnoreCase))
                {
                    radios = lst.ToList();
                    wanListReceived = true;
                }

                // Ghost sweep, scoped to THIS account: the list is the
                // server's FULL current list for the account that sent it, so
                // any WAN radio attributed to that account and absent from it
                // has gone offline (or left the account). Radios attributed to
                // OTHER accounts are untouched — their own sessions vouch for
                // them. Unattributed WAN radios ("" — pre-attribution arrivals)
                // are swept by every list, matching the old whole-map behavior.
                var freshSerials = new HashSet<string>(lst.Select(x => x.Serial), StringComparer.OrdinalIgnoreCase);
                var gone = myRadioList.Where(x =>
                    x.IsWan && !freshSerials.Contains(x.Serial)
                    && WanRadioBelongsToAccount(x.Serial, accountId)).ToList();
                foreach (Radio g in gone)
                {
                    Tracing.TraceLine($"wanRadioListReceivedHandler: WAN radio {g.Serial} ({g.Nickname}) absent from {accountId}'s fresh list — removing", TraceLevel.Info);
                    myRadioList.Remove(g);
                    RaiseRadioRemoved(this, g.Serial, g.Nickname ?? "");
                }

                // The WAN identity of every listed radio, banked BEFORE the merge
                // loop below folds the server's fields into an already-known LAN
                // object and discards the WAN one. Dual-homing's path choice
                // connects through these. Also drop handles for radios that left
                // the list — those serials are dual-homed no longer. Same scope
                // rule as the ghost sweep: only this account's handles.
                lock (_wanRadiosLock)
                {
                    foreach (var stale in _wanRadiosBySerial
                        .Where(kv => !freshSerials.Contains(kv.Key)
                            && (string.IsNullOrEmpty(kv.Value.AccountId)
                                || string.Equals(kv.Value.AccountId, accountId, StringComparison.OrdinalIgnoreCase)))
                        .Select(kv => kv.Key).ToList())
                        _wanRadiosBySerial.Remove(stale);
                }
                foreach (Radio w in lst) RememberWanRadio(w, accountId);

                // Fast paint for next time: this account's radio list, on disk,
                // so the selector can speak the account's radios the instant it
                // opens instead of after a TLS round trip. Display only — see
                // RecordAccountRadioList, nothing connects from it. Attributed
                // to the account whose session DELIVERED the list — recording
                // a push from account B under the current account's email was
                // exactly the cross-account cache pollution #259 fights.
                try
                {
                    if (!string.IsNullOrWhiteSpace(accountId) && accountId.Contains('@'))
                        GetRadioConnectionCache().RecordAccountRadioList(accountId, lst.ToList());
                }
                catch (Exception cacheEx)
                {
                    Tracing.TraceLine($"wanRadioListReceivedHandler: account list cache failed: {cacheEx.Message}", TraceLevel.Warning);
                }

                foreach (Radio r in lst)
                {
                    Radio oldRadio = findRadioInAPI(r.Serial);
                    if (oldRadio == null)
                    {
                        // In v4 API the helper is private; directly raise our local handler.
                        radioAddedHandler(r);
                    }
                    else
                    {
                        // Radio already in myRadioList — update fields and re-raise RadioFound
                        // so the RigSelector dialog sees the radio on reconnect attempts.
                        // Without this, the second SmartLink discovery after disconnect
                        // silently updates the existing entry and the ConnectingForm never closes.
                        //
                        // This branch used to `break`, which abandoned the rest of
                        // the list the moment one radio was already known — with
                        // two SmartLink radios the second never got a RadioFound,
                        // and a dual-homed radio (always already known, because
                        // LAN found it first) killed the loop on the first
                        // iteration. `continue` is what was meant.
                        UpdateRadioDiscoveryFields(r, oldRadio);
                        RaiseRadioFound(null, BuildRigData(oldRadio));
                    }
                }
              } // _wanIntakeLock
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("wanRadioListReceivedHandler:exception:" + ex.Message, TraceLevel.Error);
            }
        }
        private void UpdateRadioDiscoveryFields(Radio newRadio, Radio oldRadio)
        {
            Tracing.TraceLine("UpdateRadioDiscoveryFields:" + newRadio.Nickname + ' ' + newRadio.Callsign, TraceLevel.Info);
            if (oldRadio.Nickname != newRadio.Nickname)
                oldRadio.Nickname = newRadio.Nickname;
            if (oldRadio.Callsign != newRadio.Callsign)
                oldRadio.Callsign = newRadio.Callsign;
            if (oldRadio.Status != newRadio.Status)
                oldRadio.Status = newRadio.Status;
            if (oldRadio.GuiClientIPs != newRadio.GuiClientIPs)
                oldRadio.GuiClientIPs = newRadio.GuiClientIPs;
            if (oldRadio.GuiClientHosts != newRadio.GuiClientHosts)
                oldRadio.GuiClientHosts = newRadio.GuiClientHosts;
            if (oldRadio.PublicTlsPort != newRadio.PublicTlsPort)
                oldRadio.PublicTlsPort = newRadio.PublicTlsPort;
            if (oldRadio.PublicUdpPort != newRadio.PublicUdpPort)
                oldRadio.PublicUdpPort = newRadio.PublicUdpPort;
            if (oldRadio.IsPortForwardOn != newRadio.IsPortForwardOn)
                oldRadio.IsPortForwardOn = newRadio.IsPortForwardOn;
            if (oldRadio.Version != newRadio.Version)
                oldRadio.Version = newRadio.Version;
            if (oldRadio.RequiresHolePunch != newRadio.RequiresHolePunch)
                oldRadio.RequiresHolePunch = newRadio.RequiresHolePunch;
            if (oldRadio.NegotiatedHolePunchPort != newRadio.NegotiatedHolePunchPort)
                oldRadio.NegotiatedHolePunchPort = newRadio.NegotiatedHolePunchPort;
            if (oldRadio.MaxLicensedVersion != newRadio.MaxLicensedVersion)
                oldRadio.MaxLicensedVersion = newRadio.MaxLicensedVersion;
            // RequiresAdditionalLicense removed in FlexLib v4.1.5
            if (oldRadio.RadioLicenseId != newRadio.RadioLicenseId)
                oldRadio.RadioLicenseId = newRadio.RadioLicenseId;
            if (oldRadio.LowBandwidthConnect != newRadio.LowBandwidthConnect)
                oldRadio.LowBandwidthConnect = newRadio.LowBandwidthConnect;
            oldRadio.UpdateGuiClientsList(newGuiClients: newRadio.GuiClients);
        }

        // Sprint 26 Phase 4 deleted the `wan` field and the PreserveWanForRetry /
        // RestoreWanFromRetry methods. The SmartLink session now lives inside
        // SmartLinkServices.Coordinator and survives radio connect/disconnect
        // cycles by design — the preserve/restore band-aid is no longer needed.

        // SmartLink account manager for saved credentials
        private static SmartLinkAccountManager _accountManager;
        private static SmartLinkAccountManager AccountManager
        {
            get
            {
                if (_accountManager == null)
                {
                    _accountManager = new SmartLinkAccountManager();
                    _accountManager.LoadAccounts();
                }
                return _accountManager;
            }
        }

        /// <summary>
        /// The process-wide account manager — the SAME instance the connect
        /// flows use. Every UI that edits accounts must go through this, never
        /// a freshly constructed manager. Lesson of 2026-08-06: the account
        /// dialog used its own instance, so Reset Sign-In cleared tokens on
        /// disk while the rig's in-memory copy stayed live, reconnected
        /// silently, and its next save wrote the old tokens right back —
        /// the reset silently undone. One instance, one truth.
        /// </summary>
        public static SmartLinkAccountManager SharedAccountManager => AccountManager;

        /// <summary>
        /// The one answer to "which SmartLink account is in play." Wired once
        /// at startup (GetConfigInfo in globals.vb) to the same resolver the
        /// account selector uses: sole saved account, then the session
        /// "Use Now" override, then the saved default — and null when several
        /// accounts are saved and none has been chosen, because guessing there
        /// is the 2026-08-10 bug: most-recently-used elected another
        /// operator's account and silently opened a SmartLink session on it
        /// every launch. Static so a single wiring covers every FlexBase
        /// instance, including auto-connect's. Null hook means Radios.dll is
        /// running standalone.
        /// </summary>
        public static Func<SmartLinkAccount> ResolveCurrentAccountHook { get; set; }

        // Current SmartLink account (for token refresh on re-auth)
        private SmartLinkAccount _currentAccount;

        /// <summary>
        /// Ensure a SmartLink account session exists, resolving through
        /// ResolveCurrentAccountHook. Every path that sets _currentAccount
        /// otherwise lives in the REMOTE connect flows, so on a purely local
        /// connection the account was never loaded and everything downstream
        /// (registration preflight, registration query) wrongly concluded
        /// "not signed in" — found live 2026-08-04 when the Register button
        /// stayed grayed on the exact connection type registration requires.
        /// Returns false, deliberately, when several accounts are saved and
        /// none has been chosen: no answer beats a wrong one, and each caller
        /// handles the ambiguity in its own honest way.
        /// </summary>
        private bool TryLoadSavedAccount()
        {
            if (_currentAccount != null) return true;
            try
            {
                var accounts = AccountManager.Accounts;
                if (accounts == null || accounts.Count == 0) return false;

                var hook = ResolveCurrentAccountHook;
                if (hook != null)
                {
                    var resolved = hook();
                    if (resolved == null)
                    {
                        Tracing.TraceLine("TryLoadSavedAccount: several accounts saved, none chosen — not guessing", TraceLevel.Info);
                        return false;
                    }
                    _currentAccount = resolved;
                    Tracing.TraceLine($"TryLoadSavedAccount: resolved account '{_currentAccount.Email}'", TraceLevel.Info);
                    return true;
                }

                // Hook unwired: Radios.dll standalone. Most-recently-used is a
                // guess — trace loudly so a wiring regression in the app is
                // visible instead of quietly reviving the old behaviour.
                _currentAccount = accounts.OrderByDescending(a => a.LastUsed).First();
                Tracing.TraceLine($"TryLoadSavedAccount: resolver hook UNWIRED — standalone fallback picked most-recently-used '{_currentAccount.Email}'", TraceLevel.Warning);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"TryLoadSavedAccount: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Adopt a just-signed-in SmartLink account as this session's current
        /// account — the mid-session sign-in propagation fix (found live
        /// 2026-08-04: _currentAccount is only loaded during connect, so a New
        /// Login while connected left registration preflight insisting nobody
        /// was signed in until the app restarted). Prefers the saved instance
        /// from the shared manager when one exists for the same email, so
        /// later token refreshes persist; an unsaved sign-in (Remember
        /// unchecked) is adopted as-is for this session only.
        /// </summary>
        public bool AdoptSignedInAccount(SmartLinkAccount account)
        {
            if (account == null || string.IsNullOrEmpty(account.Email)) return false;
            var saved = AccountManager.GetAccountByEmail(account.Email);
            _currentAccount = saved ?? account;
            Tracing.TraceLine(
                $"AdoptSignedInAccount: session account is now '{_currentAccount.Email}' ({(saved != null ? "saved" : "session-only")})",
                TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Gets the email address of the currently active SmartLink account, if any.
        /// Used by the rig selector to save the account email in auto-connect config.
        /// </summary>
        public string CurrentSmartLinkEmail => _currentAccount?.Email ?? "";

        [Obsolete("Use setupRemote() which handles accounts automatically")]
        private string[] tokens;

        /// <summary>
        /// Delegate to show the SmartLink account selector dialog. Wired externally.
        /// Sprint 10: Replaces direct SmartLinkAccountSelector form creation.
        /// Returns (newLogin, selectedAccount, ok) or null if cancelled.
        /// </summary>
        public Func<SmartLinkAccountManager, (bool newLogin, SmartLinkAccount selected, bool ok)?> ShowAccountSelector { get; set; }

        /// <param name="allowInteractive">
        /// False when a sign-in form must NOT appear — the auth ladder's
        /// walk-before-prompting rung: when the connect chain still holds
        /// another path to try, a silent auth failure walks to it, and only
        /// an exhausted chain earns the native sign-in form. True preserves
        /// the historical behaviour for every existing caller.
        /// </param>
        private bool setupRemote(bool allowInteractive = true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"setupRemote: BEGIN allowInteractive={allowInteractive}", TraceLevel.Info);
            ConnectionProfiler.Current?.RecordEvent("setup_remote_begin");
            bool rv = false;
            string jwt = null;

            // Check for saved accounts
            var accounts = AccountManager.Accounts;
            Tracing.TraceLine($"setupRemote: {accounts.Count} saved account(s) ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

            if (accounts.Count > 0)
            {
                // Sprint 10: Show account selector via delegate (decoupled from WinForms Form)
                Tracing.TraceLine($"setupRemote: ShowAccountSelector delegate is {(ShowAccountSelector != null ? "WIRED" : "NULL")}", TraceLevel.Info);
                var result = ShowAccountSelector?.Invoke(AccountManager);
                Tracing.TraceLine($"setupRemote: ShowAccountSelector returned {(result.HasValue ? $"newLogin={result.Value.newLogin}, selected={result.Value.selected?.Email ?? "null"}, ok={result.Value.ok}" : "NULL")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (result == null || !result.Value.ok)
                {
                    Tracing.TraceLine("setupRemote: user cancelled account selection", TraceLevel.Info);
                    goto setupRemoteDone;
                }

                if (result.Value.newLogin)
                {
                    // User wants to log in with a new account - force Auth0 to show login page
                    Tracing.TraceLine("setupRemote: performing new login (user requested)", TraceLevel.Info);
                    jwt = allowInteractive ? PerformNewLogin(forceNewLogin: true) : null;
                    Tracing.TraceLine($"setupRemote: PerformNewLogin returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }
                else if (result.Value.selected != null)
                {
                    // Use saved account
                    _currentAccount = result.Value.selected;
                    Tracing.TraceLine($"setupRemote: using saved account '{_currentAccount.Email}', calling GetJwtFromSavedAccount", TraceLevel.Info);
                    jwt = GetJwtFromSavedAccount(_currentAccount, allowInteractiveLogin: allowInteractive);
                    Tracing.TraceLine($"setupRemote: GetJwtFromSavedAccount returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }
            }
            else
            {
                // No saved accounts - go straight to login
                Tracing.TraceLine("setupRemote: no saved accounts, performing new login", TraceLevel.Info);
                jwt = allowInteractive ? PerformNewLogin() : null;
                Tracing.TraceLine($"setupRemote: PerformNewLogin returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            }

            if (string.IsNullOrEmpty(jwt))
            {
                Tracing.TraceLine($"setupRemote: no jwt obtained, aborting ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                goto setupRemoteDone;
            }

            // Connect to SmartLink server
            Tracing.TraceLine($"setupRemote: calling ConnectToSmartLink ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            ConnectionProfiler.Current?.RecordEvent("smartlink_connect_begin");
            SmartLinkConnectResult connectResult = ConnectToSmartLink(jwt);
            rv = connectResult == SmartLinkConnectResult.Success;
            ConnectionProfiler.Current?.RecordEvent("smartlink_connect_end", new Dictionary<string, object>
            {
                { "success", rv },
                { "result", connectResult.ToString() }
            });
            Tracing.TraceLine($"setupRemote: ConnectToSmartLink returned {connectResult} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

            if (connectResult == SmartLinkConnectResult.NoRadios)
            {
                // Session connected fine, the account simply has no radios online.
                // Re-logging in cannot fix this — the prior code did exactly that
                // and hit "Invalid state for application registration" because the
                // session was already registered, then silently waited the full
                // 10s timeout per retry. Tag the trace outcome so the Archive
                // Browser can filter for this pattern and tell the user fast.
                TraceSessionContext.MarkOutcome(TraceSessionOutcome.NoRadios,
                    "SmartLink registered, server returned empty radio list");
                TraceSessionContext.AddKeyEvent("smartlink_no_radios");
                if (!SuppressSpeech)
                {
                    ScreenReaderOutput.Speak(
                        "No SmartLink radios available. The remote radio may be turned off.",
                        VerbosityLevel.Critical, true);
                }
                goto setupRemoteDone;
            }

            // First medicine for any failure: cycle the WAN session and retry
            // with the CURRENT sign-in. Most non-auth failures here are the
            // pre-existing-session trap — the server sends the radio list once
            // per TLS session, so a re-entered connect over a live session can
            // time out (or worse) without any credential being wrong, and the
            // old response of popping an interactive login on a healthy account
            // was wrong medicine (Noel, 2026-08-06, trace 203418). Refresh the
            // JWT silently when we hold an account (id_tokens live 60 seconds;
            // native-lineage refresh takes ~250ms and no UI). The same cheap
            // silent pair is also the right FIRST medicine for AuthFailed — an
            // expired id_token refreshes without any form.
            if (connectResult == SmartLinkConnectResult.ConnectFailed
                || connectResult == SmartLinkConnectResult.AuthFailed)
            {
                Tracing.TraceLine($"setupRemote: {connectResult}; cycling WAN session and retrying with current sign-in ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                CycleWanSession("connect failed — possible stale pre-existing session");
                if (_currentAccount != null)
                {
                    string silentJwt = GetJwtFromSavedAccount(_currentAccount, allowInteractiveLogin: false);
                    if (!string.IsNullOrEmpty(silentJwt)) jwt = silentJwt;
                }
                connectResult = ConnectToSmartLink(jwt);
                rv = connectResult == SmartLinkConnectResult.Success;
                Tracing.TraceLine($"setupRemote: cycled retry returned {connectResult} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (connectResult == SmartLinkConnectResult.NoRadios)
                {
                    TraceSessionContext.MarkOutcome(TraceSessionOutcome.NoRadios,
                        "SmartLink registered after session cycle, server returned empty radio list");
                    TraceSessionContext.AddKeyEvent("smartlink_no_radios_after_cycle");
                    if (!SuppressSpeech)
                    {
                        ScreenReaderOutput.Speak(
                            "No SmartLink radios available. The remote radio may be turned off.",
                            VerbosityLevel.Critical, true);
                    }
                    goto setupRemoteDone;
                }
            }

            // QB Track D (item 6): the interactive sign-in form is the LAST
            // resort, and only for failures that are actually auth-shaped —
            // the server said AuthorizationExpired and a silent refresh did
            // not fix it. Transport/server failures (timeouts, exceptions,
            // list-never-came) must NOT summon a login form: the account is
            // healthy, and a form the user cannot fix anything with is worse
            // than an honest failure. (Also, historically: a fresh login +
            // ReRegister on an already-registered session triggers "Invalid
            // state for application registration" and a silent 10s hang.)
            if (connectResult == SmartLinkConnectResult.AuthFailed && !allowInteractive)
            {
                // Walk-before-prompting: the caller has another path to try,
                // so this failure stays silent and classified. The chain's
                // last SmartLink attempt comes back with interactive allowed.
                Tracing.TraceLine($"setupRemote: auth still failing after silent refresh; interactive login suppressed by caller ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
            }
            else if (connectResult == SmartLinkConnectResult.AuthFailed)
            {
                Tracing.TraceLine($"setupRemote: auth still failing after silent refresh, performing interactive login ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                jwt = PerformNewLogin();
                if (!string.IsNullOrEmpty(jwt))
                {
                    connectResult = ConnectToSmartLink(jwt);
                    rv = connectResult == SmartLinkConnectResult.Success;
                    Tracing.TraceLine($"setupRemote: retry ConnectToSmartLink returned {connectResult} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                    if (connectResult == SmartLinkConnectResult.NoRadios)
                    {
                        TraceSessionContext.MarkOutcome(TraceSessionOutcome.NoRadios,
                            "SmartLink registered after re-login, server returned empty radio list");
                        TraceSessionContext.AddKeyEvent("smartlink_no_radios_after_relogin");
                        if (!SuppressSpeech)
                        {
                            ScreenReaderOutput.Speak(
                                "No SmartLink radios available. The remote radio may be turned off.",
                                VerbosityLevel.Critical, true);
                        }
                    }
                }
            }

            // File the classified failure story so callers can speak the
            // reason itself. AuthFailed and ConnectFailed get different
            // words because they need different action from the user.
            if (connectResult == SmartLinkConnectResult.AuthFailed)
            {
                RecordConnectFailure(new ConnectFailureReport
                {
                    Class = ConnectFailureClass.AuthenticationFailed,
                    SpokenSummary = "SmartLink did not accept the sign-in for "
                        + (_currentAccount?.Email ?? "this account")
                        + ". Signing in again from the SmartLink account manager is the fix.",
                });
            }
            else if (connectResult == SmartLinkConnectResult.ConnectFailed)
            {
                RecordConnectFailure(new ConnectFailureReport
                {
                    Class = ConnectFailureClass.SessionSetupFailed,
                    SpokenSummary = "Could not reach the SmartLink server, or it stopped answering. "
                        + "Your sign-in is fine — this is a network or server problem. "
                        + "Check your internet connection and try again in a moment.",
                });
            }

            setupRemoteDone:
            // #259: the operator expressed remote intent this session, so from
            // here on hold a presence session per saved account — whatever this
            // particular pass's outcome. A failed pass on ONE account is no
            // reason to keep every other account's radios a guess.
            EngageSmartLinkPresence();
            sw.Stop();
            Tracing.TraceLine($"setupRemote: END result={rv} (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            return rv;
        }

        /// <summary>
        /// Sprint 35 Track K (#259) — wire the presence hooks (idempotent),
        /// make sure THIS instance's intake hears attributed lists, and hold
        /// one session per silently-signable saved account. Called at the end
        /// of every remote pass; deliberately NOT called from purely local
        /// flows or the background registration query, so a LAN-only operator
        /// never grows N background TLS connections they did not ask for.
        /// </summary>
        private void EngageSmartLinkPresence()
        {
            try
            {
                Radios.SmartLink.SmartLinkPresenceService.AccountsHook ??= () => AccountManager.Accounts;
                Radios.SmartLink.SmartLinkPresenceService.SilentJwtHook ??=
                    (acct, force) => TryGetJwtSilently(acct, force);

                // Presence pushes must update rows even when no connect flow is
                // in flight — subscribe the intake here as well as in
                // ConnectToSmartLink (defensively, so it is never doubled).
                Radios.SmartLink.SmartLinkServices.Coordinator.SessionRadioListReceived -= sessionRadioListReceivedHandler;
                Radios.SmartLink.SmartLinkServices.Coordinator.SessionRadioListReceived += sessionRadioListReceivedHandler;

                Radios.SmartLink.SmartLinkPresenceService.EnsureHeldSessions(API.ProgramName);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"EngageSmartLinkPresence: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Restore focus to the parent window after a modal dialog closes.
        /// Must marshal to UI thread since auth runs on SmartLink thread.
        /// </summary>
        private void RestoreParentFocus()
        {
            try
            {
                var parent = Callouts?.ParentWindow as Control;
                if (parent != null && parent.IsHandleCreated)
                {
                    parent.BeginInvoke(new Action(() =>
                    {
                        SetForegroundWindow(parent.Handle);
                        if (parent is Form f)
                        {
                            f.Activate();
                            f.BringToFront();
                        }
                    }));
                }
            }
            catch { }
        }

        /// <summary>
        /// Performs a new login via WebView2 and optionally saves the account.
        /// </summary>
        /// <param name="forceNewLogin">When true, forces Auth0 to show the login page
        /// even if a session already exists (used when adding a new account).</param>
        /// <param name="title">Custom title for the auth form window (e.g., "Connecting to Radio"
        /// for auto-connect flows instead of the default "SmartLink Authentication").</param>
        /// <summary>
        /// Interactive Auth0 login.
        /// </summary>
        /// <param name="expectedAccount">
        /// The saved account this login is FOR, when the caller is trying to
        /// authenticate a specific one. Two things depend on it: the WebView2
        /// cookie profile (each account gets its own, so a live session for
        /// account A cannot silently satisfy a login request for account B),
        /// and the post-login identity check below. Passing null means "no
        /// particular account expected" — a genuinely new sign-in.
        /// 2026-08-05 bug this fixes: every caller relied on the ambient
        /// _currentAccount, so asking for Don's account opened Noel's cookie
        /// profile, auto-logged-in as Noel without a prompt, and saved the
        /// result — leaving the app connected to the wrong SmartLink account
        /// with no indication anything had gone sideways.
        /// </param>
        private string PerformNewLogin(SmartLinkAccount expectedAccount = null, bool forceNewLogin = false, string title = null)
        {
            // Native-first sign-in (2026-08-06, Don's lockout). The native form
            // exchanges email+password directly via the resource-owner grant —
            // SmartSDR's own mechanics — so no browser, cookie, or WebView2
            // profile is involved, and the refresh tokens it mints actually
            // renew id_tokens later. The browser form below survives as the
            // fallback: "Use Browser Instead", or automatically for accounts
            // that require two-factor sign-in.
            var native = ShowNativeLoginDialog(expectedAccount, forceNewLogin);
            if (native.result == DialogResult.OK)
            {
                RestoreParentFocus();
                return FinishInteractiveLogin(native.email, native.idToken, native.refreshToken, native.expiresIn, expectedAccount,
                    friendlyNameFromForm: native.friendlyName, rememberSignIn: native.rememberSignIn);
            }
            if (native.result != DialogResult.Retry)
            {
                Tracing.TraceLine("PerformNewLogin: native sign-in cancelled", TraceLevel.Info);
                RestoreParentFocus();
                return null;
            }
            Tracing.TraceLine("PerformNewLogin: falling back to browser sign-in", TraceLevel.Info);

            string jwt = null;

            // Manual lifetime, not `using`: this method runs on the SmartLink
            // background thread, but ShowDialog is marshaled to the UI thread,
            // so the WebView2's COM objects live there. Disposing from this
            // thread throws InvalidCastException (E_NOINTERFACE) inside
            // WebView2's cleanup — an unhandled crash that took the whole
            // process down live on 2026-08-04. The finally marshals the
            // dispose back to the UI thread.
            // Constructed directly. This used to go through
            // AuthForm.CreateAuthForm(), a factory that existed to choose
            // between the IE-based form and this one — but it returned only
            // this type, and the caller immediately downcast to it. The IE form
            // is gone (2026-08-17), and with one implementation left the
            // factory was pure indirection.
            var form = new AuthFormWebView2();
            try
            {
                form.ForceNewLogin = forceNewLogin;
                // AccountEmail follows the account being authenticated, not
                // whichever account happens to be current. It selects the
                // per-account WebView2 cookie profile (re-added in 1957420b
                // after the 81b688fe revert — an earlier comment here claimed
                // profiles were gone, which Don's 2026-08-06 trace disproved).
                // Cross-account safety = per-account profiles + ForceNewLogin
                // cookie clearing + the identity check in FinishInteractiveLogin.
                form.AccountEmail = expectedAccount?.Email ?? _currentAccount?.Email ?? "";
                if (!string.IsNullOrEmpty(title))
                {
                    form.Text = title;
                }

                // Show dialog on the main UI thread so it gets foreground focus.
                // PerformNewLogin may be called from the SmartLink background thread.
                DialogResult dialogResult = DialogResult.Cancel;
                var parent = Callouts?.ParentWindow as Control;
                if (parent != null && parent.IsHandleCreated && parent.InvokeRequired)
                {
                    parent.Invoke(new Action(() => { dialogResult = form.ShowDialog(parent as IWin32Window); }));
                }
                else
                {
                    dialogResult = form.ShowDialog();
                }

                if (dialogResult != DialogResult.OK)
                {
                    Tracing.TraceLine("setupRemote: auth form cancelled or failed", TraceLevel.Info);
                    RestoreParentFocus();
                    return null;
                }

                // Restore focus to the main app window after auth form closes.
                // Without this, focus falls to whatever window is behind the app
                // because ShowDialog() runs without an owner (cross-thread unsafe).
                RestoreParentFocus();

                jwt = FinishInteractiveLogin(form.Email, form.IdToken, form.RefreshToken, form.ExpiresIn, expectedAccount);
            }
            finally
            {
                DisposeAuthFormOnUiThread(form);
            }

            return jwt;
        }

        /// <summary>
        /// Show the native SmartLink sign-in dialog on the UI thread and hand
        /// back its outcome. Retry means "open the browser form instead" —
        /// either the user asked for it or the account needs two-factor.
        /// </summary>
        private (DialogResult result, string email, string idToken, string refreshToken, int expiresIn, string friendlyName, bool rememberSignIn)
            ShowNativeLoginDialog(SmartLinkAccount expectedAccount, bool forceNewLogin)
        {
            // Adding a genuinely new account starts blank; re-authenticating a
            // known account starts on its email with focus in the password box.
            string prefill = forceNewLogin ? "" : (expectedAccount?.Email ?? _currentAccount?.Email ?? "");

            DialogResult dr = DialogResult.Cancel;
            string email = "", idToken = "", refreshToken = "", friendlyName = "";
            int expiresIn = 0;
            bool rememberSignIn = true;

            // The dialog gets its OWN STA thread, deliberately — never shown
            // via the SmartLink thread or marshaled onto the main window.
            // Lesson from the 2026-08-06 trace: pumping a modal on the
            // SmartLink thread executed queued UI work there (ApplyUIMode ran
            // on T21 mid-login) and the shell then failed with "Error creating
            // window handle" at Show. A dedicated thread pumps only its own
            // queue, so no shared control can get its handle created on the
            // wrong thread. Same pattern as the connecting form. IsBackground
            // guarantees this thread can never keep a closed app's process
            // alive (the 2026-08-06 zombie-process lesson).
            var dialogThread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var dlg = new SmartLinkLoginForm(AccountManager, prefill);
                    dr = dlg.ShowDialog();
                    email = dlg.Email;
                    idToken = dlg.IdToken;
                    refreshToken = dlg.RefreshToken;
                    expiresIn = dlg.ExpiresIn;
                    friendlyName = dlg.FriendlyName;
                    rememberSignIn = dlg.RememberSignIn;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"ShowNativeLoginDialog: dialog thread exception: {ex.Message}", TraceLevel.Error);
                    dr = DialogResult.Cancel;
                }
            });
            dialogThread.SetApartmentState(System.Threading.ApartmentState.STA);
            dialogThread.IsBackground = true;
            dialogThread.Start();
            dialogThread.Join();

            return (dr, email, idToken, refreshToken, expiresIn, friendlyName, rememberSignIn);
        }

        /// <summary>
        /// Shared tail of every interactive sign-in, native or browser:
        /// diagnostics, the cross-account identity check, and saving/updating
        /// the account. Returns the id_token, or null when the sign-in must be
        /// rejected.
        /// </summary>
        private string FinishInteractiveLogin(string email, string idToken, string refreshToken, int expiresIn, SmartLinkAccount expectedAccount,
            string friendlyNameFromForm = null, bool rememberSignIn = true)
        {
            // Diagnostic: log the exp claim from the fresh token
            if (!string.IsNullOrEmpty(idToken))
            {
                try
                {
                    var jwtParts = idToken.Split('.');
                    if (jwtParts.Length == 3)
                    {
                        var jwtPayload = jwtParts[1];
                        switch (jwtPayload.Length % 4)
                        {
                            case 2: jwtPayload += "=="; break;
                            case 3: jwtPayload += "="; break;
                        }
                        jwtPayload = jwtPayload.Replace('-', '+').Replace('_', '/');
                        var jwtJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(jwtPayload));
                        using var jwtDoc = System.Text.Json.JsonDocument.Parse(jwtJson);
                        if (jwtDoc.RootElement.TryGetProperty("exp", out var expEl))
                        {
                            var expUnix = expEl.GetInt64();
                            var expTime = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                            var delta = expTime - DateTime.UtcNow;
                            Tracing.TraceLine($"PerformNewLogin: fresh JWT exp={expTime:yyyy-MM-dd HH:mm:ss}Z, delta={delta.TotalMinutes:F1}min, ExpiresIn={expiresIn}s", TraceLevel.Info);
                        }
                        if (jwtDoc.RootElement.TryGetProperty("iat", out var iatEl))
                        {
                            var iatUnix = iatEl.GetInt64();
                            var iatTime = DateTimeOffset.FromUnixTimeSeconds(iatUnix).UtcDateTime;
                            Tracing.TraceLine($"PerformNewLogin: fresh JWT iat={iatTime:yyyy-MM-dd HH:mm:ss}Z", TraceLevel.Info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"PerformNewLogin: JWT diagnostic parse failed: {ex.Message}", TraceLevel.Warning);
                }
            }

            if (string.IsNullOrEmpty(idToken))
            {
                Tracing.TraceLine("setupRemote: no id_token from sign-in", TraceLevel.Error);
                return null;
            }

            // Identity check: if this login was for a specific saved account,
            // the token we got back must belong to that account. The browser
            // path can hand back a different identity when a cookie session
            // already exists; the native path canonicalizes the typed email.
            // Silently accepting a mismatch connects the user to someone
            // else's radios and reports confusing state everywhere after.
            if (expectedAccount != null
                && !string.IsNullOrEmpty(email)
                && !string.Equals(email, expectedAccount.Email, StringComparison.OrdinalIgnoreCase))
            {
                Tracing.TraceLine(
                    $"PerformNewLogin: identity mismatch — asked for {expectedAccount.Email}, Auth0 returned {email}; rejecting",
                    TraceLevel.Error);
                ScreenReaderOutput.Speak(
                    Lexicon.Get("connect.smartlink.identity_mismatch",
                        ("email", email), ("expectedEmail", expectedAccount.Email)),
                    VerbosityLevel.Critical, true);
                return null;
            }

            // Save or update the account. NO dialogs anywhere in this tail —
            // round 27 (Don, 2026-08-06): the old "Save this account?"
            // MessageBox popped ownerless behind the TopMost Connecting form,
            // unannounced, and the SmartLink thread blocked on it until Don
            // gave up. The remember decision now rides in from the sign-in
            // form itself; the browser path (no such field) saves and says
            // so — an unwanted account is a ten-second delete in the account
            // manager, a question nobody can perceive is a hang.
            if (!string.IsNullOrEmpty(refreshToken))
            {
                if (!rememberSignIn)
                {
                    Tracing.TraceLine($"setupRemote: user chose not to remember sign-in for {email}", TraceLevel.Info);
                    ScreenReaderOutput.Speak(Lexicon.Get("connect.smartlink.signed_in_not_remembered_during_connect"), VerbosityLevel.Terse, true);
                }
                else
                {
                    // Check if this email already has a saved account
                    var existingAccount = AccountManager.GetAccountByEmail(email);

                    if (existingAccount != null)
                    {
                        // Silently update the existing account's tokens
                        existingAccount.IdToken = idToken;
                        existingAccount.RefreshToken = refreshToken;
                        existingAccount.ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 86400);
                        // A name typed during re-auth is a rename request.
                        if (!string.IsNullOrEmpty(friendlyNameFromForm))
                        {
                            existingAccount.FriendlyName = friendlyNameFromForm;
                        }
                        AccountManager.SaveAccount(existingAccount);
                        _currentAccount = existingAccount;

                        Tracing.TraceLine($"setupRemote: updated existing account for {email}", TraceLevel.Info);
                    }
                    else
                    {
                        string friendlyName = !string.IsNullOrEmpty(friendlyNameFromForm)
                            ? friendlyNameFromForm
                            : email;

                        var account = new SmartLinkAccount
                        {
                            FriendlyName = friendlyName,
                            Email = email ?? string.Empty,
                            IdToken = idToken,
                            RefreshToken = refreshToken,
                            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 86400),
                            LastUsed = DateTime.UtcNow
                        };

                        AccountManager.SaveAccount(account);
                        _currentAccount = account;

                        ScreenReaderOutput.Speak(
                            Lexicon.Get("connect.smartlink.account_saved_during_connect", ("friendlyName", friendlyName)),
                            VerbosityLevel.Terse, true);
                        Tracing.TraceLine($"setupRemote: saved account for {email}", TraceLevel.Info);
                    }
                }
            }

            // Legacy compatibility
            #pragma warning disable CS0618
            tokens = new[] { $"id_token={idToken}" };
            #pragma warning restore CS0618

            return idToken;
        }

        /// <summary>
        /// Dispose the WebView2 auth form on the UI thread that owns its COM
        /// objects. See the lifetime note in PerformNewLogin.
        /// </summary>
        private void DisposeAuthFormOnUiThread(Form form)
        {
            if (form == null) return;
            try
            {
                var parent = Callouts?.ParentWindow as Control;
                if (parent != null && parent.IsHandleCreated && parent.InvokeRequired)
                {
                    parent.Invoke(new Action(() =>
                    {
                        try { form.Dispose(); }
                        catch (Exception ex) { Tracing.TraceLine($"DisposeAuthFormOnUiThread: {ex.Message}", TraceLevel.Error); }
                    }));
                }
                else
                {
                    form.Dispose();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"DisposeAuthFormOnUiThread: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>
        /// Gets JWT from a saved account, refreshing if necessary.
        /// </summary>
        /// <param name="allowInteractiveLogin">
        /// False for background callers (the connect-time registration query):
        /// when a silent refresh cannot produce a valid JWT, return null instead
        /// of popping the login dialog. A background check surprising the user
        /// with a login page — and hanging Settings behind it — is exactly what
        /// happened live on 2026-08-04.
        /// </param>
        private string GetJwtFromSavedAccount(SmartLinkAccount account, bool allowInteractiveLogin = true, bool markUsed = true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"GetJwtFromSavedAccount: BEGIN email={account.Email}, ExpiresAt={account.ExpiresAt}, now={DateTime.UtcNow}, interactive={allowInteractiveLogin}", TraceLevel.Info);

            // If we already have an active WAN connection, the previous JWT may have been
            // consumed by the server. Always refresh to get a fresh token for re-registration.
            bool needsRefresh = AccountManager.IsTokenExpired(account);
            bool wanActive = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.IsConnected == true;
            Tracing.TraceLine($"GetJwtFromSavedAccount: IsTokenExpired={needsRefresh}, wanActive={wanActive}", TraceLevel.Info);
            if (!needsRefresh && wanActive)
            {
                Tracing.TraceLine("GetJwtFromSavedAccount: existing WAN connection detected, forcing refresh for re-registration", TraceLevel.Info);
                needsRefresh = true;
            }

            // The whole silent machinery lives in TryGetJwtSilently — the ONE
            // token recipe, shared with the presence sessions' auto-register
            // (#259). This method only layers the interactive fallback and
            // the deliberate-use bookkeeping on top.
            string jwt = TryGetJwtSilently(account, forceRefresh: needsRefresh);

            if (string.IsNullOrEmpty(jwt))
            {
                Tracing.TraceLine($"GetJwtFromSavedAccount: no JWT available silently, interactive={allowInteractiveLogin} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                // Pass the account so the identity check applies, and force a
                // fresh session when we're authenticating an account other than
                // the one already signed in — WebView2 uses ONE shared cookie
                // store (per-account profiles were reverted in 81b688fe over
                // folder locks), so an existing Auth0 session would otherwise
                // silently satisfy a request for a different account.
                bool differentAccount =
                    _currentAccount != null
                    && !string.Equals(_currentAccount.Email, account.Email, StringComparison.OrdinalIgnoreCase);
                return allowInteractiveLogin
                    ? PerformNewLogin(account, forceNewLogin: differentAccount)
                    : null;
            }

            // LastUsed means "the operator deliberately used this account" —
            // connects and registration commands qualify; the silent
            // background registration query passes markUsed: false. Stamping
            // on every touch was half the latch that kept re-electing the
            // same account under the old most-recently-used resolution
            // (2026-08-10; the other half was the token-refresh stamp in
            // SmartLinkAccountManager).
            if (markUsed) AccountManager.MarkAccountUsed(account);

            sw.Stop();
            Tracing.TraceLine($"GetJwtFromSavedAccount: END returning jwt=yes (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            return jwt;
        }

        /// <summary>
        /// The silent JWT recipe, and the only one: refresh-token grant first
        /// when the stored id_token is dead (it lives 60 seconds on this
        /// tenant, so it essentially always is — SmartSDR refreshes before
        /// every registration and gets a fresh id_token back), an extra
        /// forced refresh when the caller says the current token may already
        /// be consumed, and null — NEVER a login form — when silence cannot
        /// produce a valid token. Serving both the interactive flow
        /// (<see cref="GetJwtFromSavedAccount"/> layers the login fallback on
        /// top) and the held presence sessions' auto-registration (#259),
        /// which wire it through
        /// <see cref="Radios.SmartLink.SmartLinkPresenceService.SilentJwtHook"/>.
        /// Safe on any thread; blocks on the token refresh HTTP round trip.
        /// </summary>
        internal static string TryGetJwtSilently(SmartLinkAccount account, bool forceRefresh)
        {
            if (account == null) return null;

            bool isJwtExpired = SmartLinkAccountManager.IsJwtExpired(account.IdToken);
            Tracing.TraceLine($"TryGetJwtSilently: email={account.Email} forceRefresh={forceRefresh} isJwtExpired={isJwtExpired} hasRefreshToken={!string.IsNullOrEmpty(account.RefreshToken)}", TraceLevel.Info);

            if (isJwtExpired && !string.IsNullOrEmpty(account.RefreshToken))
            {
                bool jitRefreshed = false;
                try
                {
                    jitRefreshed = Task.Run(() => AccountManager.RefreshTokenAsync(account)).Result;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"TryGetJwtSilently: JIT refresh threw: {ex.Message}", TraceLevel.Warning);
                }
                if (jitRefreshed)
                {
                    isJwtExpired = SmartLinkAccountManager.IsJwtExpired(account.IdToken);
                    Tracing.TraceLine($"TryGetJwtSilently: JIT refresh ok, isJwtExpired now {isJwtExpired}", TraceLevel.Info);
                }
            }

            if (isJwtExpired)
            {
                Tracing.TraceLine("TryGetJwtSilently: JWT still expired after JIT refresh — nothing silent left to try", TraceLevel.Info);
                return null;
            }

            if (forceRefresh)
            {
                // The manager serializes refreshes per account and satisfies a
                // second ask within seconds from the first one's result, so a
                // forced refresh right after the JIT one above costs nothing.
                bool refreshed = false;
                try
                {
                    refreshed = Task.Run(() => AccountManager.RefreshTokenAsync(account)).Result;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"TryGetJwtSilently: forced refresh threw: {ex.InnerException?.Message ?? ex.Message}", TraceLevel.Error);
                }
                if (!refreshed)
                {
                    Tracing.TraceLine("TryGetJwtSilently: forced refresh failed", TraceLevel.Warning);
                    return null;
                }
                if (SmartLinkAccountManager.IsJwtExpired(account.IdToken))
                {
                    Tracing.TraceLine("TryGetJwtSilently: JWT still expired after forced refresh", TraceLevel.Warning);
                    return null;
                }
            }

            return account.IdToken;
        }

        /// <summary>
        /// Prompts user for a friendly name for the account.
        /// </summary>
        private string PromptForAccountName(string defaultEmail)
        {
            using (var inputForm = new Form())
            {
                inputForm.Text = "Name This Account";
                inputForm.Size = new System.Drawing.Size(400, 150);
                inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                inputForm.StartPosition = FormStartPosition.CenterParent;
                inputForm.MaximizeBox = false;
                inputForm.MinimizeBox = false;

                var label = new Label
                {
                    Text = "Enter a friendly name for this account (e.g., \"Home Shack\"):",
                    Location = new System.Drawing.Point(12, 12),
                    AutoSize = true
                };

                var textBox = new TextBox
                {
                    Text = defaultEmail ?? "My SmartLink Account",
                    Location = new System.Drawing.Point(12, 35),
                    Size = new System.Drawing.Size(360, 23),
                    AccessibleName = "Account name"
                };
                textBox.SelectAll();

                var okButton = new Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(216, 70),
                    Size = new System.Drawing.Size(75, 28),
                    DialogResult = DialogResult.OK
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(297, 70),
                    Size = new System.Drawing.Size(75, 28),
                    DialogResult = DialogResult.Cancel
                };

                inputForm.Controls.AddRange(new Control[] { label, textBox, okButton, cancelButton });
                inputForm.AcceptButton = okButton;
                inputForm.CancelButton = cancelButton;

                if (inputForm.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    return textBox.Text.Trim();
                }
            }

            return defaultEmail ?? "SmartLink Account";
        }

        /// <summary>
        /// Connects to SmartLink server with the given JWT.
        ///
        /// <para>
        /// Sprint 26 Phase 2: this method no longer owns the WanServer directly.
        /// It asks <see cref="Radios.SmartLink.SmartLinkServices.Coordinator"/>
        /// for the session associated with the current account, drives
        /// <see cref="Radios.SmartLink.IWanSessionOwner.Connect"/>, waits for the
        /// session to report IsConnected, then calls ReRegister with the JWT.
        /// The monitor thread inside the session owner handles backoff, reconnect,
        /// and lifecycle — FlexBase never touches a WanServer directly after this
        /// change.
        /// </para>
        ///
        /// <para>
        /// Sprint 35 Track K (#259): radio-list discovery now runs through the
        /// coordinator's attributed <c>SessionRadioListReceived</c> event —
        /// each held session hears only its own account's lists (instance
        /// event in the vendored FlexLib, see MIGRATION.md item 13) and the
        /// coordinator re-raises them stamped with the account. Our handler
        /// populates <c>radios</c> + <c>wanListReceived</c> only for the
        /// account this flow is waiting on; the owner populates
        /// <c>session.AvailableRadios</c> for every account.
        /// </para>
        /// </summary>
        private SmartLinkConnectResult ConnectToSmartLink(string jwt)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"ConnectToSmartLink: BEGIN jwt length={jwt?.Length ?? 0}", TraceLevel.Info);
            ConnectionProfiler.Current?.RecordEvent("smartlink_connect_begin", new Dictionary<string, object>
            {
                { "jwtLength", jwt?.Length ?? 0 }
            });
            try
            {
                var accountEmail = _currentAccount?.Email ?? "default-account";
                var session = Radios.SmartLink.SmartLinkServices.Coordinator.EnsureSessionForAccount(accountEmail);

                // Ensure the attributed radio-list subscription is active so `radios`
                // and `wanListReceived` get populated alongside session.AvailableRadios.
                // Defensive unsubscribe-first to avoid duplicate registrations across
                // sign-in / sign-out cycles.
                Radios.SmartLink.SmartLinkServices.Coordinator.SessionRadioListReceived -= sessionRadioListReceivedHandler;
                Radios.SmartLink.SmartLinkServices.Coordinator.SessionRadioListReceived += sessionRadioListReceivedHandler;

                // Whether this TLS session was live BEFORE this call. The server
                // sends the radio list exactly once per TLS session, so on a
                // re-entry over a live session no new list is ever coming and
                // waiting for one is pure dead time (QB Track A, 2026-08-07,
                // trace 20260805-163019: re-clicking Remote sat through the
                // full wait for an event the server never re-sends).
                bool sessionWasAlreadyConnected = session.IsConnected;

                // Kick the monitor thread. If the session is already connected (e.g. we're
                // re-entering ConnectToSmartLink after a successful previous call), Connect
                // is a cheap no-op because _wan.IsConnected is already true.
                wanListReceived = false;
                session.Connect();

                Tracing.TraceLine($"ConnectToSmartLink: waiting up to 10s for session IsConnected ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (!await(() => session.IsConnected || session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired, 10000))
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session did not reach Connected within 10s (status={session.Status}) ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                    return SmartLinkConnectResult.ConnectFailed;
                }

                if (session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired)
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session reports AuthorizationExpired; setupRemote handles re-auth ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                    return SmartLinkConnectResult.AuthFailed;
                }

                // One registration per connection: the session's own
                // auto-register (held-open presence, #259) may already have
                // sent it, and a second registration is at best a pointless
                // server poke. TryClaimRegistration is the atomic answer to
                // who sends it; the claim resets when the connection drops.
                if (session.TryClaimRegistration())
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session connected; ReRegister {API.ProgramName} Win10 jwt={jwt.Substring(0, Math.Min(20, jwt.Length))}... ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                    session.ReRegister(API.ProgramName, "Win10", jwt);
                }
                else
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session already registered this connection — skipping duplicate registration ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }

                // Sprint 35 Track K (#259): a held session KEEPS its account's
                // last list (owner.AvailableRadios) across this instance's
                // whole lifetime — but a NEW FlexBase's own bookkeeping starts
                // empty and the next spontaneous push could be minutes away.
                // Replay the owner's cached list through the intake so this
                // instance — and the selector's rows — get the account's
                // current truth immediately instead of a 10s timeout followed
                // by a needless session cycle.
                if (sessionWasAlreadyConnected
                    && session.AvailableRadios.Count > 0
                    && !myRadioList.Any(r => r.IsWan && WanRadioBelongsToAccount(r.Serial, accountEmail)))
                {
                    Tracing.TraceLine(
                        $"ConnectToSmartLink: replaying held session's cached list ({session.AvailableRadios.Count} radio(s)) through the intake ({sw.ElapsedMilliseconds}ms)",
                        TraceLevel.Info);
                    wanRadioListReceivedHandler(accountEmail, session.AvailableRadios);
                }

                // When we already hold a radio list from this session, don't make
                // the user sit through the full 10s window on the off chance the
                // server volunteers a new one — it does not resend per session.
                // WAN entries only: myRadioList also accumulates LAN radios, and
                // a LAN-only cache says nothing about this SmartLink session.
                // Scoped to THIS account: with presence holding every account's
                // sessions, another account's radios in myRadioList say nothing
                // about the account this flow is connecting.
                bool haveCachedList = session.IsConnected
                    && myRadioList.Any(r => r.IsWan && WanRadioBelongsToAccount(r.Serial, accountEmail));

                // Re-entry over a session that was ALREADY live when this call
                // began: the one list this TLS session will ever send arrived
                // long ago, so satisfy the wait from the cache IMMEDIATELY
                // instead of burning even the short window (QB Track A). The
                // attributed SessionRadioListReceived subscription stays
                // active, so pushes keep landing as refreshes through
                // wanRadioListReceivedHandler exactly as the 2026-08-06
                // refresh/morph flow expects.
                if (sessionWasAlreadyConnected && haveCachedList)
                {
                    radios = myRadioList.Where(r => r.IsWan && WanRadioBelongsToAccount(r.Serial, accountEmail)).ToList();
                    Tracing.TraceLine(
                        $"ConnectToSmartLink: session was already live — satisfied immediately from {radios.Count} cached WAN radio(s), no list wait ({sw.ElapsedMilliseconds}ms)",
                        TraceLevel.Info);
                }
                else
                {
                int listWaitMs = haveCachedList ? 2000 : 10000;

                Tracing.TraceLine($"ConnectToSmartLink: registration sent, waiting up to {listWaitMs / 1000}s for radio list (cached={myRadioList.Count}) ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (!await(() => wanListReceived || session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired, listWaitMs))
                {
                    // The server sends the radio list once per TLS session. On a
                    // re-entry into ConnectToSmartLink over a session that is
                    // ALREADY connected, that list has long since arrived, so
                    // waiting for a fresh one waits forever — 10s per attempt,
                    // and setupRemote's retry doubles it. Noel hit ~80s of
                    // apparent hanging on 2026-08-05 (trace 20260805-171637)
                    // doing nothing more exotic than pressing Remote twice.
                    // Accept the list we already have rather than declaring
                    // failure on a healthy session.
                    if (haveCachedList)
                    {
                        // The cache check above reads myRadioList, but everything
                        // downstream reads `radios`, which only a fresh
                        // WanRadioRadioListRecieved event assigns. On a NEW
                        // FlexBase over the still-live app-global session —
                        // selector closed and reopened, which remote-first
                        // startup now does on every open — `radios` is null
                        // here: NRE, mapped to ConnectFailed, answered with a
                        // pointless interactive re-login on a healthy session
                        // (Noel, 2026-08-06, trace 203418). Rebuild it from the
                        // cache being accepted — scoped to this account, so
                        // another account's presence radios cannot stand in
                        // for a list this account never gave us. RadioFound for
                        // these entries already fired via radioAddedHandler at
                        // apiInit, so no re-announce is needed here.
                        radios = myRadioList.Where(r => r.IsWan && WanRadioBelongsToAccount(r.Serial, accountEmail)).ToList();
                        Tracing.TraceLine(
                            $"ConnectToSmartLink: no new radio list, session live with {radios.Count} cached WAN radio(s) — using those ({sw.ElapsedMilliseconds}ms)",
                            TraceLevel.Info);
                    }
                    else
                    {
                        Tracing.TraceLine($"ConnectToSmartLink: TIMED OUT waiting for radio list after {listWaitMs / 1000}s ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                        return SmartLinkConnectResult.ConnectFailed;
                    }
                }
                } // end fresh-session wait path

                if (session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired)
                {
                    Tracing.TraceLine($"ConnectToSmartLink: server rejected JWT during registration ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                    return SmartLinkConnectResult.AuthFailed;
                }

                Tracing.TraceLine($"ConnectToSmartLink: radio list received! {radios.Count} radio(s), myRadioList has {myRadioList.Count} entries ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("wan_radio_list", new Dictionary<string, object>
                {
                    { "count", radios.Count },
                    { "myRadioListCount", myRadioList.Count },
                    { "elapsedMs", sw.ElapsedMilliseconds }
                });
                foreach (var r in radios)
                {
                    // Ports and forwarding flags belong in this line: a radio that
                    // advertises a forwarded port nothing is listening behind
                    // fails as a bare "connect failed" 100ms later, with no clue
                    // that the router rule is the problem (Don's radio, 2026-08-05).
                    Tracing.TraceLine(
                        $"  WAN radio: serial={r.Serial} name={r.Nickname} status={r.Status} " +
                        $"tlsPort={r.PublicTlsPort} udpPort={r.PublicUdpPort} forwarded={r.IsPortForwardOn} punch={r.RequiresHolePunch}",
                        TraceLevel.Info);
                }

                if (radios.Count == 0)
                {
                    // Distinct from ConnectFailed: the session is alive and registered,
                    // the server simply has no radios for this account. Re-logging in
                    // can't fix this (and in fact triggers "Invalid state for
                    // application registration" because we're already registered).
                    Tracing.TraceLine($"ConnectToSmartLink: no radios in list ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                    return SmartLinkConnectResult.NoRadios;
                }

                sw.Stop();
                Tracing.TraceLine($"ConnectToSmartLink: END success (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                return SmartLinkConnectResult.Success;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Tracing.TraceLine($"ConnectToSmartLink: EXCEPTION {ex.GetType().Name}: {ex.Message} (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                return SmartLinkConnectResult.ConnectFailed;
            }
        }

        private bool sendRemoteConnect(Radio r)
        {
            Tracing.TraceLine("sendRemoteConnect: " + r.Serial, TraceLevel.Info);
            // Also cleared here because RetryConnect calls this without going
            // through Connect() — and each retry re-loads the per-radio profile,
            // so a Settings edit made between attempts is honored.
            LastConnectFailureReport = null;
            ConnectionProfiler.Current?.RecordEvent("send_remote_connect", new Dictionary<string, object>
            {
                { "serial", r.Serial }
            });

            var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;

            // Sprint 35 Track K (#259): a SmartLink connect must go through the
            // session of the account that OWNS the radio — the broker only
            // knows serials on the session's own account, so dialling Don's
            // radio through another account's session can only fail. With one
            // held session per account, the attributed handle map knows the
            // owner; prefer its live session over whatever happens to be
            // active. (This is the root of #203's two-Enter behaviour: the
            // first Enter used to have no session that could act on the row.)
            string owningAccount = GetWanAccountForSerial(r.Serial);
            if (!string.IsNullOrEmpty(owningAccount)
                && (session == null
                    || !string.Equals(session.AccountId, owningAccount, StringComparison.OrdinalIgnoreCase)))
            {
                var owningSession = Radios.SmartLink.SmartLinkServices.Coordinator.GetSessionForAccount(owningAccount);
                if (owningSession != null && owningSession.IsConnected)
                {
                    Tracing.TraceLine(
                        $"sendRemoteConnect: routing through owning account's session ({owningSession.SessionId}) instead of active",
                        TraceLevel.Info);
                    session = owningSession;
                }
            }

            if (session == null)
            {
                Tracing.TraceLine("sendRemoteConnect: no active session", TraceLevel.Error);
                return false;
            }

            // Hole-punch port selection.
            //
            // The radio itself tells us whether hole punch is required — SmartLink
            // sets Radio.RequiresHolePunch in the radio-list message. That flag, not
            // our local account config, is the authority: a network that needs hole
            // punch needs it regardless of which tier the user picked.
            //
            // IMPORTANT — "NegotiatedHolePunchPort" is a misnomer in FlexLib. Nothing
            // ever assigns it: it's initialised to -1 in WanServer's radio-list parse
            // and read (never written) by Radio.Connect and the VitaSocket ctor, in
            // both 4.0.1 and 4.2.20. Picking the port is the CLIENT's job. SmartSDR
            // does exactly this — `if (radio.RequiresHolePunch)
            // radio.NegotiatedHolePunchPort = random.Next(25000, 65000);` — then
            // advertises the same number. If we leave it at -1, FlexLib calls
            // Connect(ip, -1, -1) and the connect cannot succeed.
            //
            // A FRESH port per connect is deliberate, not lazy: reusing a port risks
            // colliding with a stale NAT mapping from a previous session, which makes
            // hole punch fail intermittently and unreproducibly.
            //
            // A configured listen port is for the port-FORWARD path (Tier 1/2), where
            // the user has told their router about a specific stable port. When
            // forwarding works SmartLink reports RequiresHolePunch = false, so the two
            // paths are mutually exclusive in practice. We honour an explicit
            // configured port if one exists, and randomise otherwise.
            // Per-radio profile (barefoot-punch-pathfinder Phase 1a). Reachability
            // is a property of the radio's SITE, not the operator's account —
            // Don's radio is forwarded while the 8600 needs punch, and one
            // account-level tier can't describe both. The serial-keyed profile
            // therefore outranks the account fields. Auto (the default, and the
            // state of every radio without a saved profile) follows the
            // radio-reported RequiresHolePunch flag exactly as before, so
            // pre-profile behavior is unchanged.
            var radioProfile = RadioConfig.LoadForRadio(r.Serial);
            if (radioProfile.ConnectionPreference == RadioConnectionPreference.HolePunch
                && !r.RequiresHolePunch)
            {
                Tracing.TraceLine(
                    "sendRemoteConnect: per-radio profile FORCES hole punch (radio reported RequiresHolePunch=false)",
                    TraceLevel.Warning);
                r.RequiresHolePunch = true;
            }
            else if (radioProfile.ConnectionPreference == RadioConnectionPreference.ForwardOnly
                && r.RequiresHolePunch)
            {
                if (r.PublicTlsPort <= 0)
                {
                    // Fail fast (QB Track C item 6): ForwardOnly forbids the punch
                    // path, and SmartLink advertises no public port for this radio —
                    // there is literally no address:port a forwarded connect could
                    // try. Attempting anyway is the old behavior: tens of seconds of
                    // silent grinding ending in a bare "connection failed". Refuse
                    // up front and say why, filed as a pre-attempt refusal in
                    // Track D's failure classification.
                    RecordConnectFailure(new ConnectFailureReport
                    {
                        Class = ConnectFailureClass.PreflightRefused,
                        SpokenSummary =
                            "Not connecting. This radio is set to use forwarded ports only, " +
                            "but SmartLink reports no forwarded port is reachable for it, so there is nothing to connect to. " +
                            "Set up port forwarding at the radio's site, or change this radio's connection setting " +
                            "on the Radios tab in Settings — hole punch there needs no port forwarding.",
                    });
                    Tracing.TraceLine(
                        "sendRemoteConnect: FAIL FAST — per-radio profile is ForwardOnly, radio reported RequiresHolePunch=true and no public TLS port; refusing the doomed attempt",
                        TraceLevel.Error);
                    ConnectionProfiler.Current?.RecordEvent("forward_only_fail_fast", new Dictionary<string, object>
                    {
                        { "serial", r.Serial },
                        { "publicTlsPort", r.PublicTlsPort },
                        { "publicUdpPort", r.PublicUdpPort }
                    });
                    return false;
                }

                // Public ports ARE advertised — the user said the radio's punch
                // flag is wrong and there is a real address:port to try, which is
                // exactly the escape hatch the mode's description promises.
                Tracing.TraceLine(
                    $"sendRemoteConnect: per-radio profile FORCES forwarded path (radio reported RequiresHolePunch=true, PublicTlsPort={r.PublicTlsPort}) — connect will fail if the radio has no reachable public ports",
                    TraceLevel.Warning);
                r.RequiresHolePunch = false;
            }

            int holePunchPort = 0;
            string holePunchPortSource = "none";
            if (r.RequiresHolePunch)
            {
                if (radioProfile.FixedHolePunchPort > 0)
                {
                    holePunchPort = radioProfile.FixedHolePunchPort;
                    holePunchPortSource = "radioProfile";
                    Tracing.TraceLine(
                        $"sendRemoteConnect: hole punch required — using per-radio fixed port {holePunchPort}",
                        TraceLevel.Info);
                }
                else if (_currentAccount != null
                    && _currentAccount.ConnectionMode == SmartLinkConnectionMode.AutomaticHolePunch
                    && _currentAccount.ConfiguredListenPort.HasValue)
                {
                    // Legacy account-level pin, kept as a fallback for configs
                    // written before per-radio profiles existed — including the
                    // documented hand-edit interim unblocker (connectionMode 2 +
                    // configuredListenPort in SmartLinkAccounts.json). No UI
                    // writes this meaning anymore (QB Track C untangle); the
                    // per-radio FixedHolePunchPort above outranks it whenever
                    // set. Known wart, accepted: a value written by port-forward
                    // Apply (the field's forward meaning) can land here as a
                    // punch port if the router rule later breaks — semantically
                    // wrong but functionally harmless, since any port number
                    // punches equally well.
                    holePunchPort = _currentAccount.ConfiguredListenPort.Value;
                    holePunchPortSource = "account";
                    Tracing.TraceLine(
                        $"sendRemoteConnect: hole punch required — using configured port {holePunchPort}",
                        TraceLevel.Info);
                }
                else
                {
                    holePunchPort = System.Random.Shared.Next(25000, 65000);
                    holePunchPortSource = "random";
                    Tracing.TraceLine(
                        $"sendRemoteConnect: hole punch required — auto-assigned port {holePunchPort}",
                        TraceLevel.Info);
                }

                // The value FlexLib actually reads at connect time. Without this the
                // whole hole-punch path is dead.
                r.NegotiatedHolePunchPort = holePunchPort;
            }

            else if (_currentAccount != null
                && _currentAccount.ConnectionMode == SmartLinkConnectionMode.AutomaticHolePunch
                && _currentAccount.ConfiguredListenPort.HasValue)
            {
                // Tier 3 selected by the user but the radio didn't ask for hole punch.
                // Advertise the configured port anyway — harmless, and preserves the
                // Sprint 27 Track F behaviour.
                holePunchPort = _currentAccount.ConfiguredListenPort.Value;
                Tracing.TraceLine(
                    $"sendRemoteConnect: Tier 3 mode, radio did not require hole punch — advertising {holePunchPort}",
                    TraceLevel.Info);
            }

            // Record what actually happened so the UI can report the real port
            // rather than the configured one — the two differ whenever the radio
            // did not ask for a hole-punch.
            LastHolePunchPort = holePunchPort;

            ConnectionProfiler.Current?.RecordEvent("hole_punch_port_selected", new Dictionary<string, object>
            {
                { "requiresHolePunch", r.RequiresHolePunch },
                { "holePunchPort", holePunchPort },
                { "portSource", holePunchPortSource },
                { "radioProfilePreference", radioProfile.ConnectionPreference.ToString() }
            });

            // session.ConnectToRadio returns Task<string?>: handle on success, null on timeout/failure.
            // We block synchronously to preserve the existing caller contract; the session owner's
            // internal TCS does the actual awaiting off-thread.
            var task = session.ConnectToRadio(r.Serial, holePunchPort);
            if (!task.Wait(5000))
            {
                Tracing.TraceLine("sendRemoteConnect:Radio not ready for connect (timeout).", TraceLevel.Error);
                ConnectionProfiler.Current?.RecordEvent("send_remote_connect_timeout", new Dictionary<string, object>
                {
                    { "serial", r.Serial }
                });
                return false;
            }

            var handle = task.Result;
            if (string.IsNullOrEmpty(handle))
            {
                Tracing.TraceLine("sendRemoteConnect:Radio connect returned null handle.", TraceLevel.Error);
                return false;
            }

            r.WANConnectionHandle = handle;

            // Refresh the per-radio profile stub on every successful WAN
            // connect: keeps the nickname current and guarantees every radio
            // ever connected shows up in the Settings per-radio picker with
            // zero setup. Load/Save never throw; an unset BaseDirectory just
            // traces and declines.
            var profileStub = RadioConfig.LoadForRadio(r.Serial);
            if (!string.IsNullOrEmpty(r.Nickname))
            {
                profileStub.Nickname = r.Nickname;
            }
            profileStub.SaveForRadio(r.Serial);

            ConnectionProfiler.Current?.RecordEvent("wan_connect_ready", new Dictionary<string, object>
            {
                { "serial", r.Serial }
            });
            return true;
        }
        #endregion

        // tools
        #region tools
        internal delegate bool awaitExp();
        /// <summary>
        /// Await the specified condition.
        /// </summary>
        /// <param name="exp">function that returns the condition</param>
        /// <param name="ms">milliseconds to wait.</param>
        /// <param name="interval">optional interval to check</param>
        /// <returns>true if condition met.</returns>
        internal static bool await(awaitExp exp, int ms, int interval)
        {
            int sanity = ms / interval;
            bool rv = false;
            while (sanity-- > 0)
            {
                rv = exp();
                if (rv) break;
                Thread.Sleep(interval);
            }
            return rv;
        }
        internal static bool await(awaitExp exp, int ms)
        {
            return await(exp, ms, 25);
        }

        internal delegate bool AssertDel();
        /// <summary>
        /// assert this condition, exception if fails.
        /// </summary>
        /// <param name="exp">condition</param>
        /// <param name="msg">exception text</param>
        internal static void Assert(AssertDel exp, string msg)
        {
            if (!exp())
            {
                throw new Exception(msg);
            }
        }

        internal static void DbgTrace(string text)
        {
#if DBGTrace
            Tracing.TraceLine(text);
#endif
        }
        #endregion

        // Implement Dispose().
        #region dispose
        private bool disposed = false;
        private Component component = new Component();
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            Tracing.TraceLine("FlexBase.Dispose:" + disposing.ToString(), TraceLevel.Info);
            if (!disposed)
            {
                if (disposing)
                {
                    component.Dispose();

                    // The slice-settle timer (Sprint 32 Track H). Stopped before
                    // Disconnect so a census cannot be scheduled onto a radio
                    // that is being torn down.
                    lock (_sliceSettleLock)
                    {
                        try { _sliceSettleTimer?.Dispose(); } catch { }
                        _sliceSettleTimer = null;
                    }
                }

                if (theRadio != null)
                {
                    saveNewGlobalProfile(); // if any

                    Disconnect();
                }

                // Sprint 26 Phase 4: coordinator owns session lifecycle; FlexBase
                // no longer tears down WanServer on Dispose. App-shutdown wiring
                // should Dispose the coordinator itself.

                if (_apiInit)
                {
                    _apiInit = false;
                    API.CloseSession();
                }

                if (RigFields != null)
                {
                    // The caller should have removed the user control from their form.
                    FilterObj?.Close(); // Remove event handlers
                    RigFields.Close();
                    RigFields = null;
                }


                disposed = true;
            }
        }

        ~FlexBase()
        {
            Dispose(false);
        }
        #endregion

        /// <summary>
        /// Off/On values for use by the rigs
        /// </summary>
        public enum OffOnValues
        {
            off,
            on
        }

        /// <summary>
        /// return the toggle of the OffOnValue
        /// </summary>
        /// <param name="value"></param>
        /// <returns>Toggled OffOnValue</returns>
        public OffOnValues ToggleOffOn(OffOnValues value)
        {
            return (value == OffOnValues.on) ? OffOnValues.off : OffOnValues.on;
        }

        public class ConnectedArg
        {
            public string Serial;
            public bool Connected;
            internal ConnectedArg(string serial, bool connected)
            {
                Serial = serial;
                Connected = connected;
            }
        }

        public delegate void ConnectedDel(object sender, ConnectedArg arg);
        public event ConnectedDel ConnectedEvent;
        private void raiseConnectedEvent(bool connected)
        {
            // #146: the notifier's "follow the radio's sidetone" option needs a
            // pitch the moment a radio arrives, not only when the operator next
            // changes it on the front panel. This is the one place both
            // transitions pass through, so it is the one place that has to
            // remember. Disconnect pushes null, which is how "there is nothing
            // to follow, use the configured tone" is said — a normal state, and
            // nothing announces anything about it.
            try
            {
                ScreenReaderOutput.RadioCwPitchChanged?.Invoke(
                    connected ? theRadio?.CWPitch : (int?)null);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("raiseConnectedEvent: CW pitch notify failed:" + ex.Message,
                    TraceLevel.Warning);
            }

            if (ConnectedEvent != null)
            {
                Tracing.TraceLine("raiseConnectedEvent:" + connected.ToString(), TraceLevel.Info);
                ConnectedEvent(this, new ConnectedArg(theRadio.Serial, connected));
            }
            else
            {
                Tracing.TraceLine("raiseConnectedEvent:not handled:" + connected.ToString(), TraceLevel.Info);
            }
        }

        internal delegate void UpdateConfiguredTNFsDel(TNF tnf);
        internal UpdateConfiguredTNFsDel UpdateConfiguredTNFs = null;

#if KeepAlive
        class keepAlive_t
        {
            private System.Threading.Timer theTimer;
            private FlexBase parent;
            private const int keepAlivePeriod = 1000 * 5; // 5 seconds
            public keepAlive_t(FlexBase p)
            {
                parent = p;
                theTimer = new System.Threading.Timer(timerCallback, null, keepAlivePeriod, keepAlivePeriod);
            }
            private void timerCallback(object state)
            {
                if (parent.Disconnecting) return;

                try
                {
                    if (!parent.IsConnected)
                    {
                        Tracing.TraceLine("keepAlive power off", TraceLevel.Info);
                        theTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        parent.raisePowerEvent(false);
                    }
                }
                catch(Exception ex)
                {
                    Tracing.TraceLine("keepAlive exception:" + ex.Message, TraceLevel.Error);
                }
            }
            public void Done()
            {
                Tracing.TraceLine("keepAlive_t.Done", TraceLevel.Info);
                theTimer.Dispose();
            }
        }
#endif

        private ATUTuneStatus originalATUStatus = ATUTuneStatus.None;
        private bool oldATUEnable = false; // false is the default, see Flex6300.
        private bool globalProfileLoaded; // see GetProfileInfo().
        private string globalProfileDesired; // see GetProfileInfo().
        internal bool ExportComplete;
        internal string ExportException;
        private void HookFeatureLicense(Radio radio)
        {
            if (trackedFeatureLicense != null)
            {
                trackedFeatureLicense.PropertyChanged -= FeatureLicense_PropertyChanged;
                trackedFeatureLicense = null;
            }

            var license = radio?.FeatureLicense;
            if (license != null)
            {
                trackedFeatureLicense = license;
                trackedFeatureLicense.PropertyChanged += FeatureLicense_PropertyChanged;
            }

            RaiseFeatureLicenseChanged();
        }

        private void FeatureLicense_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            RaiseFeatureLicenseChanged();
        }

        private void RaiseFeatureLicenseChanged()
        {
            FeatureLicenseChanged?.Invoke(this, EventArgs.Empty);
        }
        private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Tracing.TraceLine("propertyChanged:Radio:" + e.PropertyName, TraceLevel.Verbose);
            Radio r = (Radio)sender;
            if (!(r.ClientHandle != 0) & myClient(r.ClientHandle))
            {
                Tracing.TraceLine("propertyChanged:Radio:NotMine:" + e.PropertyName);
            }
            switch (e.PropertyName)
            {
                case "FeatureLicense":
                    HookFeatureLicense(r);
                    break;
                case "ActiveSlice":
                    {
                        Slice s = r.ActiveSlice;
                        if ((s != null) && myClient(s.ClientHandle))
                        {
                            Tracing.TraceLine("ActiveSlice:mine " + SliceToVFO(s), TraceLevel.Info);
                            _RXFrequency = LibFreqtoLong(s.Freq);
                            _RXMode = s.DemodMode;
                            _RXVFO = SliceToVFO(s);
                            s.Panadapter.GetRFGainInfo();
                            FilterObj.RXFreqChange(s);
#if CWMonitor
                            if (useCWMon && (s == VFOToSlice(TXVFO)) && (s.DemodMode == "CW"))
                            {
                                CWMonStart(); // ok if already started.
                            }
#endif
                        }
                        else
                        {
                            Tracing.TraceLine("ActiveSlice:none", TraceLevel.Info);
                        }
                    }
                    break;
                case "ATUEnabled":
                    {
                        Tracing.TraceLine("ATUEnabled:" + theRadio.ATUEnabled.ToString(), TraceLevel.Info);
                        if (oldATUEnable == r.ATUEnabled) return;
                        oldATUEnable = r.ATUEnabled;
                        bool wasEnabled = MyCaps.HasCap(RigCaps.Caps.ATGet);
#if !NoATU
                        if (r.ATUEnabled)
                        {
                            // indicate ATU capable.
                            MyCaps.getCaps = MyCaps.SetCap(MyCaps.getCaps, RigCaps.Caps.ATGet);
                            MyCaps.setCaps = MyCaps.SetCap(MyCaps.setCaps, RigCaps.Caps.ATSet);
                            MyCaps.getCaps = MyCaps.SetCap(MyCaps.getCaps, RigCaps.Caps.ATMems);
                            // Turn off the tuner if was bypassed.
                            // Note the bypass status might happen later.
                            if ((originalATUStatus == ATUTuneStatus.Bypass) |
                                (originalATUStatus == ATUTuneStatus.ManualBypass))
                            {
                                setFlexTunerTypeNotAuto();
                            }
                            else _FlexTunerType = FlexTunerTypes.auto;
                        }
                        else
#endif
                        {
                            // not atu capable, unless the above condition true.
                            MyCaps.getCaps = MyCaps.ResetCap(MyCaps.getCaps, RigCaps.Caps.ATGet);
                            MyCaps.setCaps = MyCaps.ResetCap(MyCaps.setCaps, RigCaps.Caps.ATSet);
                            MyCaps.getCaps = MyCaps.ResetCap(MyCaps.getCaps, RigCaps.Caps.ATMems);
                            setFlexTunerTypeNotAuto();
                        }
                        if (wasEnabled != MyCaps.HasCap(RigCaps.Caps.ATGet))
                        {
                            // enabled status changed.
                            raiseCapsChange(new CapsChangeArg(MyCaps));
                        }
                    }
                    break;
#if !NoATU
                case "ATUTuneStatus":
                    {
                        Tracing.TraceLine("ATUTuneStatus:" + theRadio.ATUTuneStatus.ToString(), TraceLevel.Info);
                        // set original status
                        if (originalATUStatus == ATUTuneStatus.None) originalATUStatus = r.ATUTuneStatus;
                        RaiseFlexAntTuneStartStop(new FlexAntTunerArg
                            (_FlexTunerType, r.ATUTuneStatus, _SWR));
                        switch (theRadio.ATUTuneStatus)
                        {
                            case ATUTuneStatus.NotStarted:
                                // turn off tuning.
                                FlexTunerOn = false;
                                break;
                            case ATUTuneStatus.Aborted:
                                // turn off tuning.
                                FlexTunerOn = false;
                                break;
                            case ATUTuneStatus.InProgress:
                                // nothing to do here.
                                break;
                            case ATUTuneStatus.Bypass:
                                // stop tuning if tuning
                                if (_FlexTunerOn) FlexTunerOn = false;
                                // Turn off autoTune.
                                setFlexTunerTypeNotAuto();
                                break;
                            case ATUTuneStatus.ManualBypass:
                                // Nothing to do
                                break;
                            case ATUTuneStatus.Successful:
                            case ATUTuneStatus.OK:
                                FlexTunerOn = false;
                                break;
                            case ATUTuneStatus.Fail:
                                FlexTunerOn = false;
                                // bypass the tuner
                                theRadio.ATUTuneBypass(); // will get manualBypass status
                                                          // Turn autotune off
                                setFlexTunerTypeNotAuto();
                                break;
                            case ATUTuneStatus.FailBypass:
                                // nothing to do
                                break;
                        }
                    }
                    break;
#endif
                case "Connected":
                    {
                        Tracing.TraceLine("Connected:" + r.Connected.ToString(), TraceLevel.Error);
                        _IsConnected = r.Connected;
                        ConnectionStateChanged?.Invoke(r.Connected);
#if zero
                        bool justReconnected = false;
                        if (!r.Connected &&
                            !Disconnecting &&
                            !string.IsNullOrEmpty(clientID))
                        {
                            justReconnected = true;
                            theRadio.Connect(clientID);
                        }
                        else
                        {
                            raiseConnectedEvent(r.Connected);
                        }
#endif
                        //raiseConnectedEvent(r.Connected);
                    }
                    break;
                case "CWBreakIn":
                    Tracing.TraceLine("CWBreakIn:" + r.CWBreakIn.ToString(), TraceLevel.Info);
                    break;
                case "CWIambic":
                    {
                        Tracing.TraceLine("CWIambic:" + r.CWIambic.ToString(), TraceLevel.Info);
                        if (r.CWIambic)
                        {
                            _Keyer = (r.CWIambicModeA) ? IambicValues.iambicA : IambicValues.iambicB;
                        }
                        else _Keyer = IambicValues.off;
                    }
                    break;
                case "CWIambicModeA":
                    {
                        Tracing.TraceLine("CWIambicModeA:" + r.CWIambicModeA.ToString(), TraceLevel.Info);
                        if (r.CWIambic & r.CWIambicModeA) _Keyer = IambicValues.iambicA;
                    }
                    break;
                case "CWIambicModeB":
                    {
                        Tracing.TraceLine("CWIambicModeB:" + r.CWIambicModeB.ToString(), TraceLevel.Info);
                        if (r.CWIambic & r.CWIambicModeB) _Keyer = IambicValues.iambicB;
                    }
                    break;
                case "CWPitch":
                    {
                        Tracing.TraceLine("CWPitch:" + r.CWPitch, TraceLevel.Info);
                        if (useCWMon) CWMon.Frequency = (uint)r.CWPitch;
                        // #146: the CW MONITOR has followed this pitch for
                        // years — the line above. The CW NOTIFIER was simply
                        // never wired to the same event. Announced rather than
                        // pushed at anyone in particular; whoever cares hooks it.
                        try { ScreenReaderOutput.RadioCwPitchChanged?.Invoke(r.CWPitch); }
                        catch (Exception ex)
                        {
                            Tracing.TraceLine("CWPitch notify failed:" + ex.Message, TraceLevel.Warning);
                        }
                    }
                    break;
                case "CWSpeed":
                    {
                        Tracing.TraceLine("CWSpeed:" + r.CWSpeed, TraceLevel.Info);
                        if (useCWMon) CWMon.Speed = (uint)r.CWSpeed;
                    }
                    break;
                case "CWSwapPaddles":
                    Tracing.TraceLine("CWSwapPaddles:" + r.CWSwapPaddles, TraceLevel.Info);
                    _CWReverse = r.CWSwapPaddles;
                    break;
                case "DatabaseExportComplete":
                    {
                        Tracing.TraceLine("DatabaseExportComplete:" + r.DatabaseExportComplete.ToString(), TraceLevel.Info);
                        ExportComplete = r.DatabaseExportComplete;
                    }
                    break;
                case "DatabaseExportException":
                    {
                        Tracing.TraceLine("DatabaseExportException:" + r.DatabaseExportException, TraceLevel.Info);
                        ExportException = r.DatabaseExportException;
                    }
                    break;
                case "DatabaseImportComplete":
                    {
                        Tracing.TraceLine("DatabaseImportComplete:mine " + r.DatabaseImportComplete.ToString(), TraceLevel.Info);
                        if (r.DatabaseImportComplete & !string.IsNullOrEmpty(importDir))
                        {
                            q.Enqueue((FunctionDel)(() => { GetProfileInfo(true); }), "GetProfileInfo");
                        }
                    }
                    break;
                case "DAXOn":
                    {
                        Tracing.TraceLine("DAXOn:" + r.DAXOn.ToString(), TraceLevel.Info);
                        _DAXOnOff = (r.DAXOn) ? OffOnValues.on : OffOnValues.off;
                    }
                    break;
                case "InterlockState":
                    {
                        Tracing.TraceLine("InterlockState:" + r.InterlockState.ToString(), TraceLevel.Info);
                    }
                    break;
                case "IsMuteLocalAudioWhenRemoteOn":
                    {
                        Tracing.TraceLine("IsMuteLocalAudioWhenRemoteOn:" + r.IsMuteLocalAudioWhenRemoteOn.ToString(), TraceLevel.Info);
                    }
                    break;
                case "LineoutMute":
                    {
                        Tracing.TraceLine("LineoutMute:" + r.LineoutMute.ToString(), TraceLevel.Info);
                    }
                    break;
                case "Mox":
                    {
                        Tracing.TraceLine("Mox:" + r.Mox.ToString(), TraceLevel.Info);
                        bool oldTransmit = _Transmit;
                        _Transmit = r.Mox;
                        if (_Transmit != oldTransmit)
                        {
                            raiseTransmitChange(_Transmit);
                        }
                    }
                    break;
                case "PanadaptersRemaining":
                    {
                        Tracing.TraceLine("PanadaptersRemaining:" + r.PanadaptersRemaining, TraceLevel.Info);
                        // First one will be the total slices.
                        if (initialFreeSlices == -1)
                        {
                            initialFreeSlices = r.PanadaptersRemaining;
                        }
                    }
                    break;
                case "PersistenceLoaded":
                    Tracing.TraceLine("PersistenceLoaded:" + r.PersistenceLoaded.ToString(), TraceLevel.Info);
                    break;
                case "ProfileGlobalList":
                    {
                        // See if desired global profile is loaded.
                        globalProfileLoaded = r.ProfileGlobalList.Contains(globalProfileDesired);
                        string line = "";
                        foreach (string str in r.ProfileGlobalList)
                        {
                            line += str + " ";
                        }
                        Tracing.TraceLine("ProfileGlobalList:" + line, TraceLevel.Info);
                    }
                    break;
                case "ProfileGlobalSelection":
                    {
                        Tracing.TraceLine("ProfileGlobalSelection:" + r.ProfileGlobalSelection.ToString(), TraceLevel.Info);
                    }
                    break;
                case "ProfileTXList":
                    {
                        string line = "";
                        foreach (string str in r.ProfileTXList)
                        {
                            line += str + " ";
                        }
                        Tracing.TraceLine("ProfileTXList:" + line, TraceLevel.Info);
                    }
                    break;
                case "ProfileTXSelection":
                    Tracing.TraceLine("ProfileTXSelection:" + r.ProfileTXSelection, TraceLevel.Info);
                    break;
                case "ProfileMICSelection":
                    {
                        // THE field that decides whether transmit audio modulates
                        // anything. A radio whose mic-profile selection is EMPTY has
                        // no transmit-audio DSP chain: SC_MIC pins at -120 and the
                        // operator keys up into silence. Established by pcap diff
                        // against the vendor client on the bench 8600, 2026-08-10 -
                        // every command matched except this one, "Default" there and
                        // empty here. See the ANNOUNCES-never-writes note further
                        // down this file for why we do not simply repair it.
                        //
                        // ADDED 2026-08-23, after a bench session found the VALUE was
                        // never recorded at all. ProfileTXSelection printed its value
                        // one case above; this printed only a bare property-changed
                        // notice. So the single most diagnostic field in the whole
                        // silent-transmit investigation was invisible in every trace
                        // anyone had ever collected, including the ones used to chase
                        // it for weeks.
                        //
                        // Empty is traced at ERROR on purpose: the one state that
                        // matters is the one that must not be quiet, and Error
                        // survives the Normal detail level a real session runs at.
                        string mic = r.ProfileMICSelection;
                        if (string.IsNullOrEmpty(mic))
                        {
                            Tracing.TraceLine("ProfileMICSelection:EMPTY - the radio has no"
                                + " transmit-audio DSP chain, so PC transmit audio will modulate"
                                + " nothing and SC_MIC will sit at -120", TraceLevel.Error);
                        }
                        else
                        {
                            Tracing.TraceLine("ProfileMICSelection:" + mic, TraceLevel.Info);
                        }
                    }
                    break;
                case "ProfileMICList":
                    {
                        // Recorded so an empty SELECTION can be told apart from a
                        // radio that has no mic profiles to select. Different
                        // problems, identical symptom.
                        string line = "";
                        if (r.ProfileMICList != null)
                        {
                            foreach (string str in r.ProfileMICList) line += str + " ";
                        }
                        Tracing.TraceLine("ProfileMICList:" + line, TraceLevel.Info);
                    }
                    break;
                case "PTTSource":
                    Tracing.TraceLine("PTTSource:" + r.PTTSource.ToString(), TraceLevel.Info);
                    break;
                case "RFPower":
                    Tracing.TraceLine("RFPower:" + theRadio.RFPower, TraceLevel.Info);
                    _XmitPower = theRadio.RFPower;
                    break;
                case "SimpleVOXEnable":
                    Tracing.TraceLine("SimpleVOXEnable:" + r.SimpleVOXEnable.ToString(), TraceLevel.Info);
                    break;
                case "Status":
                    string status = theRadio.Status;
                    Tracing.TraceLine("radio status:" + status, TraceLevel.Info);
                    break;
                case "TransmitSlice":
                    {
                        Slice s = r.TransmitSlice;
                        if (s == null)
                        {
                            Tracing.TraceLine("TransmitSlice:null", TraceLevel.Info);
                            return;
                        }
                        if (myClient(s.ClientHandle))
                        {
                            Tracing.TraceLine("TransmitSlice:mine " + SliceToVFO(s), TraceLevel.Info);
                        }
                        else
                        {
                            Tracing.TraceLine("TransmitSlice:not mine", TraceLevel.Info);
                        }
                    }
                    break;
                case "TunePower":
                    Tracing.TraceLine("TunePower:" + r.TunePower, TraceLevel.Info);
                    _TunePower = r.TunePower;
                    break;
                case "TX1Enabled":
                    Tracing.TraceLine("TX1Enabled:" + r.TX1Enabled.ToString(), TraceLevel.Info);
                    break;
                case "TX2Enabled":
                    Tracing.TraceLine("TX2Enabled:" + r.TX2Enabled.ToString(), TraceLevel.Info);
                    break;
                case "TX3Enabled":
                    Tracing.TraceLine("TX3Enabled:" + r.TX3Enabled.ToString(), TraceLevel.Info);
                    break;
                case "TXCWMonitorGain":
                    {
                        Tracing.TraceLine("TXCWMonitorGain:" + theRadio.TXCWMonitorGain, TraceLevel.Info);
#if CWMonitor
                        if (useCWMon) CWMon.Volume = theRadio.TXCWMonitorGain;
#endif
                    }
                    break;
                case "TXTune":
                    {
                        Tracing.TraceLine("TXTune:" + r.TXTune.ToString(), TraceLevel.Info);
                        if (r.TXTune)
                        {
                            // Report status if starting up.
                            ATUTuneStatus stat = ATUTuneStatus.InProgress;
                            RaiseFlexAntTuneStartStop(new FlexAntTunerArg
                                (FlexTunerType, stat, _SWR));
                        }
                    }
                    break;
            }
        }

        private void slicePropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Slice s = (Slice)sender;
            if (myClient(s.ClientHandle))
            {
                Tracing.TraceLine("propertyChanged:Slice:mine " + e.PropertyName, TraceLevel.Verbose);
                switch (e.PropertyName)
                {
                    case "Active":
                        {
                            Tracing.TraceLine("Active:slice " + s.Index.ToString() + " " + s.Active.ToString(), TraceLevel.Info);
                            // "or on moving to another slice" — the second half
                            // of Noel's spec. The radio confirming a slice as
                            // Active is the honest moment the operator arrived
                            // on it. See AnnounceSliceIdentity.
                            if (s.Active) AnnounceSliceIdentity(s);
                        }
                        break;
                    case "DemodMode":
                        {
                            Tracing.TraceLine("DemodMode:slice " + s.Index.ToString() + " " + s.DemodMode.ToString(), TraceLevel.Info);
                            if (s.Active) _RXMode = s.DemodMode;
                            if (s.IsTransmitSlice) _TXMode = s.DemodMode;
                            if (s.Active)
                            {
                                FilterObj.RXFreqChange(s);
                                ModeChanged?.Invoke(s.DemodMode);

                                // The CW mode announcement (#58). Runs alongside speech, not
                                // only when speech is off — CW is a parallel notification
                                // channel when CwNotificationsEnabled + CwModeAnnounceEnabled.
                                //
                                // Sprint 32 Track H REPLACED what stood here rather than
                                // repairing it. The line used to be a bare mode name gated on
                                // ReferenceEquals(s, theRadio?.ActiveSlice), and that guard was
                                // the wrong instrument for the wrong problem: it announced a
                                // PER-SLICE property during a BULK STATE REPLAY, so on connect
                                // four individually-correct announcements answered a question
                                // nobody had asked. Noel heard it live 2026-08-19 on the bench
                                // 8600: "usb usb usb fm", four slices, every connect.
                                //
                                // Filtering to one member and calling it representative is
                                // arbitrary. Summarising describes what actually happened. So
                                // connect gets a CENSUS of the set (AnnounceSliceCensus) and a
                                // slice or mode change gets an IDENTITY plus a STATE — which is
                                // this call. The letter is IN the message, so nothing has to
                                // nominate a representative slice at all.
                                //
                                // (The storm only became audible once the player was serialized
                                // by EarconCwOutput's single-reader FIFO. Before that the four
                                // announcements overlapped into garble that read as one broken
                                // noise rather than four correct ones. Fixing the garbling made
                                // the real defect legible; the count was always four.)
                                AnnounceSliceIdentity(s);

                                // Firmware leaves NRLOn flag set across mode round-trips but stops
                                // applying Legacy NR processing. The user-visible workaround is to
                                // uncheck then recheck the UI; this mimics that -- but back-to-back
                                // queued commands appear to be coalesced somewhere in FlexLib or
                                // firmware (a plain false-then-true does NOT re-apply). A real time
                                // gap between off and on is required, matching how the UI path
                                // naturally has click-react-click delay. 500 ms is our approximation
                                // of a human re-click interval.
                                if (s.NRLOn)
                                {
                                    Slice sliceRef = s;
                                    Task.Run(async () =>
                                    {
                                        try
                                        {
                                            // Let mode change settle on the radio side first.
                                            await Task.Delay(150);
                                            q.Enqueue((FunctionDel)(() => { sliceRef.NRLOn = false; }), "NRLOn-mode-reapply-off");
                                            // Human-click-interval between off and on so firmware doesn't collapse.
                                            await Task.Delay(500);
                                            q.Enqueue((FunctionDel)(() => { sliceRef.NRLOn = true; }), "NRLOn-mode-reapply-on");
                                        }
                                        catch (Exception ex)
                                        {
                                            Tracing.TraceLine("NRLOn-mode-reapply failed: " + ex.Message, TraceLevel.Warning);
                                        }
                                    });
                                }

                            }
#if CWMonitor
                            try
                            {
                                if (useCWMon && (s == VFOToSlice(TXVFO)))
                                {
                                    if (s.DemodMode == "CW") CWMonStart();
                                    else CWMonStop();
                                }
                            }
                            catch { }
#endif
                        }
                        break;
                    case "Freq":
                        {
                            Tracing.TraceLine("Freq:slice " + s.Index.ToString() + " " + s.Freq.ToString(), TraceLevel.Verbose);
                            if (s.Active)
                            {
                                _RXFrequency = LibFreqtoLong(s.Freq);
                                FilterObj.RXFreqChange(s);
                            }
                            if (s.IsTransmitSlice) _TXFrequency = LibFreqtoLong(s.Freq);
                        }
                        break;
                    case "IsTransmitSlice":
                        {
                            Tracing.TraceLine("IsTransmit:slice " + s.Index.ToString() + " " + s.IsTransmitSlice.ToString(), TraceLevel.Info);
                            int vfo = SliceToVFO(s);
                            if (s.IsTransmitSlice)
                            {
                                if (CanTransmit)
                                {
                                    _TXVFO = vfo;
                                    _TXFrequency = LibFreqtoLong(s.Freq);
                                    _TXMode = s.DemodMode;
                                }
                            }
                        }
                        break;
                    case "Mute":
                        {
                            Tracing.TraceLine("slicePropertyChangedHandler:Mute slice:" + s.Index + ' ' + s.Mute.ToString(), TraceLevel.Info);
                        }
                        break;
                    case "NBLevel":
                        {
                            Tracing.TraceLine("slice NBLevel:" + s.NBLevel.ToString(), TraceLevel.Info);
                            //s.Panadapter.NBLevel = s.NBLevel;
                        }
                        break;
                    case "NBOn":
                        {
                            Tracing.TraceLine("slice NBOn:" + s.NBOn.ToString(), TraceLevel.Info);
                            //s.Panadapter.NBOn = s.NBOn;
                        }
                        break;
                    case "RITOn":
                        {
                            Tracing.TraceLine("RITOn:" + s.RITOn.ToString(), TraceLevel.Info);
                            lock (_RIT)
                            {
                                _RIT.Active = s.RITOn;
                            }
                        }
                        break;
                    case "RITFreq":
                        {
                            Tracing.TraceLine("RITFreq:" + s.RITFreq.ToString(), TraceLevel.Info);
                            lock (_RIT)
                            {
                                _RIT.Value = s.RITFreq;
                            }
                        }
                        break;
                    case "XITOn":
                        {
                            Tracing.TraceLine("XITOn:" + s.XITOn.ToString(), TraceLevel.Info);
                            lock (_XIT)
                            {
                                _XIT.Active = s.XITOn;
                            }
                        }
                        break;
                    case "XITFreq":
                        {
                            Tracing.TraceLine("XITFreq:" + s.XITFreq.ToString(), TraceLevel.Info);
                            lock (_XIT)
                            {
                                _XIT.Value = s.XITFreq;
                            }
                        }
                        break;
#if zero
                    case "TXAntenna":
                        Tracing.TraceLine("TXAntenna:" + s.TXAnt, TraceLevel.Info);
                        // We always set the TXAnt for both slices, so we'll come through twice.
                        break;
#endif
                }
            }
            else
            {
                if (s.ClientHandle != 0) Tracing.TraceLine("propertyChanged:Slice:not mine " + e.PropertyName, TraceLevel.Info);
            }
        }

        private void panadapterPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Panadapter p = (Panadapter)sender;
            if (!(p.ClientHandle != 0) & myClient(p.ClientHandle))
            {
                Tracing.TraceLine("panadapterPropertyChangedHandler:not mine:" + e.PropertyName);
            }
            if (myClient(p.ClientHandle))
            {
                // The FFT packet counters change on every dropped or received
                // frame, so the blanket property-change line below traced them
                // at packet rate: 30,277 "panPropertyChanged:mine
                // FFTPacketErrorCount" lines in one 10 MB stretch of the
                // 2026-08-21 capture, second only to the meter stream. A line
                // saying "the packet counter changed" carries no information —
                // the dropped-frame evidence is the coalesced PanFrameGaps
                // summary the trace listener writes — so the two counters are
                // excluded here and every other property still traces.
                if (e.PropertyName != "FFTPacketErrorCount" && e.PropertyName != "FFTPacketTotalCount")
                {
                    Tracing.TraceLine("panPropertyChanged:mine " + e.PropertyName, TraceLevel.Verbose);
                }
                switch (e.PropertyName)
                {
                    case "Bandwidth":
                        Tracing.TraceLine("Bandwidth:" + p.Bandwidth.ToString(), TraceLevel.Verbose);
                        break;
                    case "CenterFreq":
                        Tracing.TraceLine("CenterFreq:" + p.CenterFreq.ToString(), TraceLevel.Info);
                        break;
                    case "FPS":
                        Tracing.TraceLine("FPS:" + p.FPS.ToString(), TraceLevel.Info);
                        break;
                    case "HighDbm":
                        Tracing.TraceLine("HighDBM:" + p.HighDbm.ToString(), TraceLevel.Verbose);
                        break;
                    case "LowDbm":
                        Tracing.TraceLine("LowDbm:" + p.LowDbm.ToString(), TraceLevel.Verbose);
                        break;
                    //case "Preamp":
                    //    Tracing.TraceLine("Preamp:" + p.Preamp, TraceLevel.Info);
                    //    break;
                    case "RFGain":
                        Tracing.TraceLine("panadapter RFGain:" + p.RFGain.ToString(), TraceLevel.Verbose);
                        //if (p == activePan) _PreAmp = (p.RFGain == PreAmpMax) ? OffOnValues.on : OffOnValues.off;
                        break;
                    case "RFGainLow":
                        Tracing.TraceLine("RFGainLow:" + p.RFGainLow, TraceLevel.Info);
                        RFGainMin = p.RFGainLow;
                        break;
                    case "RFGainHigh":
                        Tracing.TraceLine("RFGainHigh:" + p.RFGainHigh, TraceLevel.Info);
                        RFGainMax = p.RFGainHigh;
                        break;
                    case "RFGainStep":
                        Tracing.TraceLine("RFGainStep:" + p.RFGainStep, TraceLevel.Info);
                        RFGainIncrement = p.RFGainStep;
                        break;
                    case "RFGainMarkers":
                        {
                            string str = "";
                            foreach (int i in p.RFGainMarkers)
                            {
                                str += i.ToString() + ' ';
                            }
                            Tracing.TraceLine("RFGainMarkers:" + str, TraceLevel.Info);
                        }
                        break;
                }
            }
        }

        private void waterfallPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Waterfall w = (Waterfall)sender;
            if (!(w.ClientHandle != 0) & myClient(w.ClientHandle))
            {
                Tracing.TraceLine("waterfallPropertyChangedHandler:not mine:" + e.PropertyName);
            }
            if (myClient(w.ClientHandle))
            {
                Tracing.TraceLine("waterfallPropertyChanged:mine " + e.PropertyName, TraceLevel.Verbose);
                switch (e.PropertyName)
                {
                    case "FallLineDurationMs":
                        Tracing.TraceLine("FallLineDurationMs:" + w.FallLineDurationMs.ToString(), TraceLevel.Info);
                        break;
                }
            }
        }

        private void tnfPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            // See FlexTNF.cs.
            TNF tnf = (TNF)sender;
            Tracing.TraceLine("propertyChanged:TNF:" + e.PropertyName, TraceLevel.Verbose);
            if (UpdateConfiguredTNFs != null) UpdateConfiguredTNFs(tnf);
        }

        private void messageReceivedHandler(MessageSeverity severity, string message)
        {
            Tracing.TraceLine("message severity:" + severity.ToString() + " " + message, TraceLevel.Error);
        }

        private string clientID;
        private const uint noClient = 0xffffffff;
        private uint clientHandle = noClient;

        /// <summary>
        /// The last local-PTT value observed from an AUTHORITATIVE record for
        /// our own client — one FlexLib stamped IsThisClient or one carrying
        /// a real client_id. Null until the first such observation each
        /// connection. Exists because the radio's discovery broadcast can
        /// replace our GUIClient record with one whose IsLocalPtt is
        /// fabricated false (symptom 5), and the presence gate must read the
        /// radio's attestation, not the fabrication.
        /// </summary>
        private bool? _lastAuthoritativeLocalPtt;
        // Track GUIClient removal during SmartLink connection for faster timeout detection.
        // SmartLink removes and re-adds the GUIClient during setup — if the re-add is slow,
        // we detect it and disconnect for retry instead of waiting the full timeout.
        private volatile bool _clientRemovedDuringStart = false;
        private volatile bool _clientAddedDuringStart = false;
        private long _clientRemovedTickCount = 0;
        private long _startBeginTickCount = 0;

        // Stuck-modal escape: thread-safe cancel signal raised from the connecting
        // modal's Escape / X close handler (which runs on the modal's own message
        // pump thread, not the UI thread that's blocked in Start()'s wait loops).
        // Start() polls this in its await calls and the station-name-wait loop, then
        // exits with LastStartFailureReason="Cancelled by user" so the caller's
        // retry path does not re-attempt.
        private volatile bool _cancelRequested = false;
        public bool CancelRequested => _cancelRequested;
        /// <summary>
        /// Signal that the in-flight connect/start should abort at its next check
        /// point. Thread-safe, fast, non-blocking. Caller still owns the cleanup
        /// path (openTheRadio's Dispose-on-failure already runs when Start returns
        /// false).
        /// </summary>
        public void RequestCancel()
        {
            _cancelRequested = true;
            Tracing.TraceLine("RequestCancel: cancel flag set", TraceLevel.Info);
            ConnectionProfiler.Current?.RecordEvent("cancel_requested");
        }

        /// <summary>
        /// Fires when any MultiFlex GUI client is added, removed, or updated.
        /// Subscribers marshal to UI thread as needed — event fires on FlexLib's
        /// receive thread, same thread that invoked the underlying GUIClient event.
        /// </summary>
        public event Action? GuiClientChanged;

        /// <summary>
        /// Snapshot of each remote client's identity at the moment we first
        /// observed it (via <see cref="guiClientAdded"/>) or when its name
        /// changed (via <see cref="guiClientUpdated"/>). Used to resolve
        /// BUG-062 Symptom 6 (R2 decision 2026-04-20): FlexLib's
        /// <c>parseGuiClientStatus</c> mutates <c>GUIClient.Station</c> and
        /// <c>GUIClient.Program</c> before firing <c>OnGUIClientRemoved</c>,
        /// so the payload our remove handler sees can be blanked. We read
        /// from this snapshot for announcements so the correct callsign is
        /// spoken regardless of what FlexLib blanked upstream.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string Station, string Program)> _clientIdentitySnapshots = new();

        private void guiClientAdded(GUIClient client)
        {
            if (client == null) return;

            // Snapshot identity early — before any FlexLib-side mutations can blank it.
            // Also covers the case where another client's Station is empty at add time;
            // guiClientUpdated will refresh the snapshot when the Station populates.
            _clientIdentitySnapshots[client.ClientHandle] = (client.Station ?? "", client.Program ?? "");

            // Recognize our own client by HANDLE, not only by FlexLib's
            // IsThisClient flag. The trap (symptom 5, trace-proven on the
            // 8600): the radio's discovery broadcast can remove our record
            // mid-connect and re-add it rebuilt from discovery data —
            // Discovery.cs builds those with client_id null, is_local_ptt
            // false, and IsThisClient never set. Same ClientHandle, impostor
            // facts. Trusting IsThisClient alone meant the re-add of our own
            // client read as a stranger arriving: the station-name wait saw
            // the client as never returning, and the fabricated record's
            // IsLocalPtt=false later denied the port-settings authority gate
            // to an operator sitting at the radio.
            bool isMine = client.IsThisClient
                || (clientHandle != noClient && client.ClientHandle == clientHandle);

            // A record with no client_id that FlexLib did not stamp as ours
            // came from the discovery packet, which simply does not carry
            // identity facts — its IsLocalPtt is fabricated, not observed.
            bool fabricated = string.IsNullOrEmpty(client.ClientID) && !client.IsThisClient;

            if (isMine)
            {
                _clientRemovedDuringStart = false; // Client is back
                _clientAddedDuringStart = true;
                // Never let a fabricated record blank the real client id.
                if (!string.IsNullOrEmpty(client.ClientID)) clientID = client.ClientID;
                clientHandle = client.ClientHandle;
                lock (theRadio.GuiClientsLockObj)
                {
                    OnlyStation = (theRadio.GuiClients.Count == 1);
                }
                //CanTransmit = PrimaryStation;
                CanTransmit = true;

                if (string.IsNullOrEmpty(client.Station))
                {
                    // Ensure no duplicate name.
                    if (!OnlyStation)
                    {
                        foreach (GUIClient c in theRadio.GuiClients)
                        {
                            if (!myClient(c.ClientHandle) &
                                (c.Station == Callouts.StationName))
                            {
                                Callouts.StationName += '1';
                                Tracing.TraceLine("guiClientAdded:station now will be " + Callouts.StationName, TraceLevel.Error);
                            }
                        }
                    }
                    theRadio.SetClientStationName(Callouts.StationName);
                }

                client.PropertyChanged += new PropertyChangedEventHandler(guiClientPropertyChanged);

                // Local-PTT is accepted only from records that can actually
                // know it: FlexLib-stamped (IsThisClient) or carrying a real
                // client_id (the radio's own TCP status). A fabricated
                // discovery record must not downgrade an authoritative
                // observation — that downgrade is exactly what denied
                // RequirePortSettingsAuthority after the remove/re-add dance.
                if (!fabricated)
                {
                    _LocalPTT = client.IsLocalPtt;
                    _lastAuthoritativeLocalPtt = client.IsLocalPtt;
                }
                else
                {
                    Tracing.TraceLine(
                        $"guiClientAdded: adopted own handle {client.ClientHandle} from a fabricated record (empty client_id); keeping LocalPTT={_LocalPTT}",
                        TraceLevel.Info);
                }
            }

            // Notify when another client connects (not during initial startup).
            // isMine, not IsThisClient: the fabricated re-add of our OWN
            // client used to announce itself as "Another client connected".
            if (!isMine && _clientAddedDuringStart)
            {
                string who = !string.IsNullOrEmpty(client.Station) ? client.Station
                    : !string.IsNullOrEmpty(client.Program) ? client.Program
                    : Lexicon.Get("connect.client.unknown_added");
                ScreenReaderOutput.Speak(Lexicon.Get("connect.client.connected", ("who", who)), VerbosityLevel.Terse);
                ScreenReaderOutput.PlayClientConnectedEarcon?.Invoke();
            }

            Tracing.TraceLine("guiClientAdded:" +
                "id:" + client.ClientID +
                " my client:" + client.IsThisClient.ToString() +
                " handle:" + client.ClientHandle +
                " program:" + client.Program +
                " station:" + client.Station +
                " is local PTT:" + client.IsLocalPtt.ToString() +
                " is available:" + client.IsAvailable.ToString() +
                " Only:" + OnlyStation.ToString() +
                " CanTransmit:" + CanTransmit.ToString(), TraceLevel.Info);

            ConnectionProfiler.Current?.RecordEvent("gui_client_added", new Dictionary<string, object>
            {
                { "clientId", client.ClientID },
                { "handle", client.ClientHandle },
                { "station", client.Station ?? "" },
                { "isThisClient", client.IsThisClient },
                { "isAvailable", client.IsAvailable },
                { "msSinceStartBegin", _startBeginTickCount > 0 ? (Environment.TickCount64 - _startBeginTickCount) : -1 }
            });

            GuiClientChanged?.Invoke();
        }

        private bool myClient(uint handle)
        {
            return ((clientHandle == handle)) ? true : false;
        }

        /// <summary>
        /// Get a snapshot of connected MultiFlex GUI clients with their owned slices.
        /// Returns tuples: (program, station, handle, isThisClient, ownedSliceLetters).
        /// </summary>
        public List<(string program, string station, uint handle, bool isThisClient, string slices)> GetGuiClients()
        {
            var result = new List<(string, string, uint, bool, string)>();
            if (theRadio == null) return result;

            lock (theRadio.GuiClientsLockObj)
            {
                foreach (var gc in theRadio.GuiClients)
                {
                    var ownedSlices = new List<string>();
                    foreach (var s in theRadio.SliceList)
                    {
                        if (s.ClientHandle == gc.ClientHandle && !string.IsNullOrEmpty(s.Letter))
                            ownedSlices.Add(s.Letter);
                    }

                    result.Add((
                        gc.Program ?? "Unknown",
                        gc.Station ?? "",
                        gc.ClientHandle,
                        gc.IsThisClient,
                        string.Join(", ", ownedSlices)
                    ));
                }
            }
            return result;
        }

        /// <summary>
        /// Disconnect a MultiFlex GUI client by handle.
        /// </summary>
        public bool DisconnectGuiClient(uint handle)
        {
            if (theRadio == null || myClient(handle)) return false;
            try
            {
                theRadio.DisconnectClientByHandle(handle.ToString());
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"DisconnectGuiClient: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        internal GUIClient TheGuiClient
        {
            get
            {
                GUIClient rv = theRadio.FindGUIClientByClientHandle(clientHandle);
                return rv;
            }
        }

        /// <summary>
        /// Station names of every GUI client connected to this radio other than us.
        /// Empty when we're the only station, the radio is null, or the other clients
        /// haven't reported a station name yet.
        ///
        /// Callers use this to tell the user who else is affected before taking an
        /// action with radio-wide blast radius (reboot, firmware update, port-forward
        /// changes). On a MultiFlex radio "who else am I about to disconnect" is the
        /// single most useful thing to put in a confirmation prompt.
        ///
        /// Never throws — information gathering must not block the operation it's
        /// describing.
        /// </summary>
        public System.Collections.Generic.List<string> OtherConnectedStations
        {
            get
            {
                var others = new System.Collections.Generic.List<string>();
                try
                {
                    if (theRadio == null) return others;
                    lock (theRadio.GuiClientsLockObj)
                    {
                        foreach (GUIClient c in theRadio.GuiClients)
                        {
                            if (!myClient(c.ClientHandle) && !string.IsNullOrEmpty(c.Station))
                                others.Add(c.Station);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"OtherConnectedStations: {ex.Message}", TraceLevel.Error);
                }
                return others;
            }
        }

        #region Sprint 28 Phase 6 — RequireOperatorPresence primitive

        /// <summary>
        /// Strictness level for the <see cref="RequireOperatorPresence"/> authorization
        /// primitive. Callers pick the level appropriate to their operation's blast
        /// radius — port-forward changes use Passive, firmware-class operations use
        /// ActiveChallenge.
        /// </summary>
        public enum PresenceLevel
        {
            /// <summary>
            /// Check <see cref="Flex.Smoothlake.FlexLib.GUIClient.IsLocalPtt"/> on the
            /// current client. Passes when this client is the radio's primary operator
            /// right now (the client the radio routes physical mic/key PTT events to).
            /// Remote SmartLink clients that haven't been assigned IsLocalPtt fail this
            /// check, which is the intended gate for operations like SmartLink port
            /// config that change radio-persistent state.
            /// </summary>
            Passive,

            /// <summary>
            /// Deliberately stubbed. First caller will be firmware upload work (see
            /// memory <c>project_8600_unbox_firmware_trigger.md</c>) — the PTT-listen-
            /// with-timeout logic is easier to get right when a real caller drives the
            /// design. Using this level before implementation lands throws
            /// NotImplementedException intentionally.
            /// </summary>
            ActiveChallenge
        }

        /// <summary>
        /// Sprint 28 Phase 6 — authorization gate for destructive operations that
        /// change radio-persistent state. Use from call sites that want to prevent
        /// non-primary-operator clients from committing changes (port-forward config,
        /// future firmware upload, future factory reset, etc.).
        ///
        /// When the check passes, <paramref name="onConfirmed"/> is invoked. When it
        /// fails, <paramref name="onDenied"/> is invoked (if supplied) AND the denial
        /// is announced via <see cref="Radios.ScreenReaderOutput"/> so the user hears
        /// why their action didn't go through.
        ///
        /// Threat-proportional authorization: pick the <paramref name="level"/> that
        /// matches the consequence of your operation. See <see cref="PresenceLevel"/>
        /// for per-level semantics. Callers self-document their strictness choice at
        /// the call site rather than burying it in an internal lookup table.
        /// </summary>
        /// <param name="level">How strict the presence check should be.</param>
        /// <param name="reason">Short phrase describing what the user is trying to do.
        /// Embedded in the denial announcement (e.g., "change SmartLink port settings"
        /// -> "Cannot change SmartLink port settings &mdash; you must be the primary
        /// operator at the radio").</param>
        /// <param name="onConfirmed">Invoked synchronously when the check passes.</param>
        /// <param name="onDenied">Optional callback invoked after the denial speech.
        /// Leave null if the call site has nothing to do on denial beyond the
        /// announcement.</param>
        public void RequireOperatorPresence(PresenceLevel level, string reason, Action onConfirmed, Action onDenied = null)
        {
            switch (level)
            {
                case PresenceLevel.Passive:
                    if (IsCurrentClientLocalPtt())
                    {
                        onConfirmed?.Invoke();
                    }
                    else
                    {
                        string msg = Lexicon.Get("connect.presence.denied", ("reason", reason));
                        Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical, interrupt: true);
                        Tracing.TraceLine($"RequireOperatorPresence denied (Passive): {reason}", TraceLevel.Info);
                        onDenied?.Invoke();
                    }
                    break;

                case PresenceLevel.ActiveChallenge:
                    throw new NotImplementedException(
                        "PresenceLevel.ActiveChallenge is reserved for firmware-class " +
                        "operations. Implementation lands alongside firmware upload work " +
                        "(see memory project_8600_unbox_firmware_trigger.md). Do not use " +
                        "before that feature ships — it will not behave correctly.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level,
                        "Unknown PresenceLevel value.");
            }
        }

        /// <summary>
        /// Authorization gate for SmartLink port-settings changes (Noel's
        /// 2026-08-06 ownership decision). Two independent ways to pass:
        /// presence (primary operator at the radio, same as before), or the
        /// owner-declared per-radio waiver <see cref="RadioConfig.AllowRemotePortChanges"/>.
        /// The trust model behind the waiver: a valid SmartLink token for the
        /// radio's account IS the owner's grant — JJ Flex cannot distinguish
        /// the owner from someone the owner handed credentials to, and
        /// pretending otherwise just locks remote-base owners out of their own
        /// rigs. The waiver is per-radio, default off, and set by whoever runs
        /// this copy of JJ Flex. Firmware-class operations do NOT use this
        /// gate — they get PresenceLevel.ActiveChallenge when it ships, which
        /// must honor <see cref="RadioConfig.AllowRemoteFirmwareUpdates"/>.
        /// </summary>
        public void RequirePortSettingsAuthority(string reason, Action onConfirmed, Action onDenied = null)
        {
            if (IsCurrentClientLocalPtt())
            {
                Tracing.TraceLine($"RequirePortSettingsAuthority: presence pass ({reason})", TraceLevel.Info);
                onConfirmed?.Invoke();
                return;
            }

            string serial = theRadio?.Serial ?? string.Empty;
            if (serial.Length > 0 && RadioConfig.LoadForRadio(serial).AllowRemotePortChanges)
            {
                Tracing.TraceLine(
                    $"RequirePortSettingsAuthority: remote waiver pass for {serial} ({reason})",
                    TraceLevel.Info);
                onConfirmed?.Invoke();
                return;
            }

            string msg = Lexicon.Get("connect.presence.port_denied", ("reason", reason));
            Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical, interrupt: true);
            Tracing.TraceLine(
                $"RequirePortSettingsAuthority denied: {reason} serial={serial}", TraceLevel.Info);
            onDenied?.Invoke();
        }

        /// <summary>
        /// Sprint 28 Phase 6 — returns true when this connection's GUI client holds
        /// <see cref="Flex.Smoothlake.FlexLib.GUIClient.IsLocalPtt"/>. That flag is
        /// the radio's own attestation of "this is the primary operator client,"
        /// unfakable by remote SmartLink sessions.
        /// </summary>
        public bool IsCurrentClientLocalPtt()
        {
            try
            {
                var client = TheGuiClient;
                if (client == null) return false;

                // A record with no client_id that FlexLib did not stamp as
                // ours was rebuilt from a discovery packet, which carries no
                // local-PTT fact — its false is fabricated. Answer from the
                // last AUTHORITATIVE observation instead, so the presence
                // gate keeps telling the truth through the radio's
                // remove/re-add dance (symptom 5: the gate denied a local
                // operator because it read the impostor record).
                if (string.IsNullOrEmpty(client.ClientID) && !client.IsThisClient
                    && _lastAuthoritativeLocalPtt.HasValue)
                {
                    Tracing.TraceLine(
                        $"IsCurrentClientLocalPtt: record for handle {client.ClientHandle} carries no identity; answering from last authoritative observation ({_lastAuthoritativeLocalPtt.Value})",
                        TraceLevel.Info);
                    return _lastAuthoritativeLocalPtt.Value;
                }

                return client.IsLocalPtt;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"IsCurrentClientLocalPtt failed: {ex.Message}", TraceLevel.Warning);
                return false;
            }
        }

        #endregion

        private void guiClientUpdated(GUIClient client)
        {
            if (client == null) return;

            // Refresh the identity snapshot — Station/Program may have populated
            // or changed since the initial add. Used by the remove-path announcement
            // per BUG-062 Symptom 6 (R2 snapshot-at-subscribe).
            if (!string.IsNullOrEmpty(client.Station) || !string.IsNullOrEmpty(client.Program))
            {
                _clientIdentitySnapshots[client.ClientHandle] = (client.Station ?? "", client.Program ?? "");
            }

            // The radio's TCP "client connected" status updates the existing
            // record with the real client_id and local-PTT — the correction
            // that heals a fabricated discovery re-add. When it is OUR handle
            // and the record now carries identity, take the facts.
            if (clientHandle != noClient && client.ClientHandle == clientHandle
                && (client.IsThisClient || !string.IsNullOrEmpty(client.ClientID)))
            {
                if (!string.IsNullOrEmpty(client.ClientID)) clientID = client.ClientID;
                _LocalPTT = client.IsLocalPtt;
                _lastAuthoritativeLocalPtt = client.IsLocalPtt;
            }

            Tracing.TraceLine("guiClientUpdated:" +
                "id:" + client.ClientID +
                " my client:" + client.IsThisClient.ToString() +
                " handle:" + client.ClientHandle +
                " program:" + client.Program +
                " station:" + client.Station +
                " is local PTT:" + client.IsLocalPtt.ToString() +
                " is available:" + client.IsAvailable.ToString() +
                " Only:" + OnlyStation.ToString() +
                " CanTransmit:" + CanTransmit.ToString(), TraceLevel.Info);

            ConnectionProfiler.Current?.RecordEvent("gui_client_updated", new Dictionary<string, object>
            {
                { "clientId", client.ClientID },
                { "station", client.Station ?? "" },
                { "isThisClient", client.IsThisClient }
            });

            GuiClientChanged?.Invoke();
        }

        private void guiClientRemoved(GUIClient client)
        {
            if (client == null) return;

            if (myClient(client.ClientHandle))
            {
                _clientRemovedDuringStart = true;
                _clientRemovedTickCount = Environment.TickCount64;
                Tracing.TraceLine("guiClientRemoved:my client", TraceLevel.Info);
            }

            // Notify when another client disconnects.
            //
            // BUG-062 Symptom 6 fix (R2 snapshot-at-subscribe, 2026-04-20): the
            // `client` payload FlexLib hands us here may have been blanked by
            // parseGuiClientStatus before OnGUIClientRemoved fired, so we prefer
            // the snapshot captured at add/update time. We still fall back to
            // the event payload as a last resort (in case the snapshot was
            // never populated — e.g., a client that added and removed within
            // the same message).
            if (!myClient(client.ClientHandle))
            {
                _clientIdentitySnapshots.TryGetValue(client.ClientHandle, out var snapshot);
                string snapStation = snapshot.Station ?? "";
                string snapProgram = snapshot.Program ?? "";

                string who = !string.IsNullOrEmpty(snapStation) ? snapStation
                    : !string.IsNullOrEmpty(snapProgram) ? snapProgram
                    : !string.IsNullOrEmpty(client.Station) ? client.Station
                    : !string.IsNullOrEmpty(client.Program) ? client.Program
                    : Lexicon.Get("connect.client.unknown_removed");
                ScreenReaderOutput.Speak(Lexicon.Get("connect.client.disconnected", ("who", who)), VerbosityLevel.Terse);
                ScreenReaderOutput.PlayClientDisconnectedEarcon?.Invoke();
            }

            // Remove the snapshot — the client is gone.
            _clientIdentitySnapshots.TryRemove(client.ClientHandle, out _);

            Tracing.TraceLine("guiClientRemoved:" +
                "id:" + client.ClientID +
                " my client:" + client.IsThisClient.ToString() +
                " handle:" + client.ClientHandle +
                " program:" + client.Program +
                " station:" + client.Station +
                " is local PTT:" + client.IsLocalPtt.ToString() +
                " is available:" + client.IsAvailable.ToString() +
                " Only:" + OnlyStation.ToString() +
                " CanTransmit:" + CanTransmit.ToString(), TraceLevel.Info);

            ConnectionProfiler.Current?.RecordEvent("gui_client_removed", new Dictionary<string, object>
            {
                { "clientId", client.ClientID },
                { "handle", client.ClientHandle },
                { "isThisClient", myClient(client.ClientHandle) },
                { "station", client.Station ?? "" },
                { "msSinceStartBegin", _startBeginTickCount > 0 ? (Environment.TickCount64 - _startBeginTickCount) : -1 }
            });

            GuiClientChanged?.Invoke();
        }

        // These properties are for my client.
        private void guiClientPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Tracing.TraceLine("guiClientPropertyChanged:" + e.PropertyName, TraceLevel.Verbose);
            GUIClient client = TheGuiClient; // get my client
            switch (e.PropertyName)
            {
                case "IsLocalPtt":
                    {
                        Tracing.TraceLine("guiClientPropertyChanged:IsLocalPTT " + client.IsLocalPtt.ToString(), TraceLevel.Info);
                        _LocalPTT = client.IsLocalPtt;
                        // Only a record that can know the fact updates the
                        // authoritative memory; see IsCurrentClientLocalPtt.
                        if (client.IsThisClient || !string.IsNullOrEmpty(client.ClientID))
                        {
                            _lastAuthoritativeLocalPtt = client.IsLocalPtt;
                        }
                    }
                    break;
                case "Station":
                    Tracing.TraceLine("guiClientPropertyChanged:station " + client.Station, TraceLevel.Info);
                    break;
            }
        }

        private bool mySliceAdded;
        private bool mySliceRemoved;

        /// <summary>
        /// Fired when a slice is added or removed from this client.
        /// UI layers can subscribe to trigger menu/display rebuilds.
        /// </summary>
        public event Action SliceCountChanged;

        /// <summary>
        /// Fired when the active slice's demod mode changes (e.g., USB→CW).
        /// UI layers subscribe to force immediate DSP state refresh.
        /// </summary>
        public event Action<string> ModeChanged;

        /// <summary>
        /// Fired when the radio connection state changes (connected/disconnected).
        /// UI layers subscribe to rebuild menus and update connection-dependent controls.
        /// </summary>
        public event Action<bool> ConnectionStateChanged;

        private void sliceAdded(Slice slc)
        {
            if (myClient(slc.ClientHandle))
            {
                mySliceAdded = true;
                slc.PropertyChanged += new PropertyChangedEventHandler(slicePropertyChangedHandler);
                slc.MeterAdded += new Slice.MeterAddedEventHandler(meterAdded);
                sMeter_t sMeter = new sMeter_t(this, slc);
                slc.SMeterDataReady += sMeter.sMeterData;
                int ct;
                lock (mySlices)
                {
                    // The letter is the identity (QB Track J): keep mySlices
                    // sorted by radio slice index so position order always
                    // equals letter order, no matter what order slices were
                    // created or re-created in. Capture the current RX/TX
                    // slice OBJECTS first — inserting below them shifts their
                    // positions, and a stored position is only valid for one
                    // roster generation.
                    Slice rxSlice = ((_RXVFO >= 0) && (_RXVFO < mySlices.Count)) ? mySlices[_RXVFO] : null;
                    Slice txSlice = ((_TXVFO >= 0) && (_TXVFO < mySlices.Count)) ? mySlices[_TXVFO] : null;

                    int pos = 0;
                    while ((pos < mySlices.Count) && (mySlices[pos].Index < slc.Index)) pos++;
                    mySlices.Insert(pos, slc);
                    ct = mySlices.Count;

                    // Re-derive VFO positions from slice identity.
                    if (rxSlice != null) _RXVFO = mySlices.IndexOf(rxSlice);
                    if (txSlice != null) _TXVFO = mySlices.IndexOf(txSlice);
                }
                Tracing.TraceLine("sliceAdded:mine " + ct.ToString() + ':' + slc.ToString(), TraceLevel.Info);
                SliceCountChanged?.Invoke();
                // #58: opens the bulk window and restarts the settle timer, so
                // a connect that delivers four slices produces ONE census
                // instead of four per-slice announcements.
                NoteSliceSetChanged();
                if (slc.IsTransmitSlice)
                {
                    Tracing.TraceLine("sliceAdded:IsTransmitSlice", TraceLevel.Info);
                    _TXVFO = SliceToVFO(slc);
                }
                if (slc.Active)
                {
                    Tracing.TraceLine("sliceAdded:activeSlice", TraceLevel.Info);
                    _RXVFO = SliceToVFO(slc);
                }
            }
            else Tracing.TraceLine("sliceAdded:not mine " + slc.ToString(), TraceLevel.Info);
        }

        private void sliceRemoved(Slice slc)
        {
            if (myClient(slc.ClientHandle))
            {
                int ct;
                if (mySlices != null)
                {
                    lock (mySlices)
                    {
                        // Identity, not position (QB Track J): capture the
                        // RX/TX slice OBJECTS before mutating, then re-derive
                        // the stored positions afterwards by following the
                        // objects to their new positions. A stored position is
                        // only valid for one roster generation — positional
                        // decrement arithmetic silently retargets when it's
                        // stale (Don's intermittent VFO issue, Noel's 8600
                        // wrong-slice session of 2026-08-07).
                        Slice rxSlice = ((_RXVFO >= 0) && (_RXVFO < mySlices.Count)) ? mySlices[_RXVFO] : null;
                        Slice txSlice = ((_TXVFO >= 0) && (_TXVFO < mySlices.Count)) ? mySlices[_TXVFO] : null;
                        bool removed = mySlices.Remove(slc);
                        ct = mySlices.Count;

                        if (removed)
                        {
                            int oldRX = _RXVFO;
                            int oldTX = _TXVFO;

                            // Follow the same slice to its new position. If the
                            // RX/TX slice itself went away, fall back to the
                            // first remaining slice (lowest letter), matching
                            // the prior behavior.
                            if ((rxSlice != null) && (rxSlice != slc))
                                _RXVFO = mySlices.IndexOf(rxSlice);
                            else if (_RXVFO != noVFO)
                                _RXVFO = (ct > 0) ? 0 : noVFO;

                            if ((txSlice != null) && (txSlice != slc))
                                _TXVFO = mySlices.IndexOf(txSlice);
                            else if (_TXVFO != noVFO)
                                _TXVFO = (ct > 0) ? 0 : noVFO;

                            if (_RXVFO != oldRX || _TXVFO != oldTX)
                                Tracing.TraceLine($"sliceRemoved:VFO re-derive RXVFO {oldRX}→{_RXVFO} TXVFO {oldTX}→{_TXVFO}", TraceLevel.Info);
                        }
                    }
                    mySliceRemoved = true;
                    Tracing.TraceLine("sliceRemoved:mine, new count:" + ct.ToString() + ':' + slc.ToString(), TraceLevel.Info);
                    SliceCountChanged?.Invoke();
                    // #58 and #117: one census after the set settles, and — when
                    // the operator asked for it — the receipt saying the change
                    // is provisional. Releasing three slices in a burst still
                    // costs exactly one of each.
                    NoteSliceSetChanged();
                }
            }
            else Tracing.TraceLine("sliceRemoved:not mine" + slc.ToString(), TraceLevel.Info);
        }

        internal Panadapter Panadapter
        {
            get
            {
                return theRadio?.ActiveSlice?.Panadapter;
            }
        }
        internal List<Waterfall> waterfallList;
        internal Waterfall Waterfall
        {
            get
            {
                return GetPanadaptersWaterfall(Panadapter);
            }
        }
        internal Waterfall GetPanadaptersWaterfall(Panadapter pan)
        {
            Waterfall rv = null;
            if ((pan != null) && (waterfallList != null))
            {
                lock (waterfallList)
                {
                    foreach (Waterfall w in waterfallList)
                    {
                        if (w.StreamID == pan.ChildWaterfallStreamID)
                        {
                            rv = w;
                            break;
                        }
                    }
                }
            }
            return rv;
        }
        private List<Panadapter> myPanAdapters = new List<Panadapter>();
        private void panadapterAdded(Panadapter pan, Waterfall fall)
        {
            if (myClient(pan.ClientHandle))
            {
                if (waterfallList == null) waterfallList = new List<Waterfall>();
                lock (waterfallList)
                {
                    waterfallList.Add(fall);
                }
                pan.Width = 5000;
                pan.PropertyChanged += new PropertyChangedEventHandler(panadapterPropertyChangedHandler);
                int ct;
                lock (myPanAdapters)
                {
                    myPanAdapters.Add(pan);
                    ct = myPanAdapters.Count;
                }
                Tracing.TraceLine("panadapterAdded:mine " + ct.ToString() + ':' + pan.ToString(), TraceLevel.Info);
            }
            else Tracing.TraceLine("panadapterAdded:not mine " + pan.ToString(), TraceLevel.Info);
        }

        internal int MyNumPanadapters
        {
            get
            {
                lock (myPanAdapters)
                {
                    return myPanAdapters.Count;
                }
            }
        }

        private void panAdapterRemoved(Panadapter pan)
        {
            if (myClient(pan.ClientHandle))
            {
                int ct;
                lock (myPanAdapters)
                {
                    myPanAdapters.Remove(pan);
                    ct = myPanAdapters.Count;
                }
                Tracing.TraceLine("panadapterRemoved:new count:" + ct.ToString() + ':' + pan.ToString(), TraceLevel.Info);
            }
            else Tracing.TraceLine("panadapterRemoved:not mine", TraceLevel.Info);
        }
        private void waterfallRemoved(Waterfall fall)
        {
            Tracing.TraceLine("waterfallRemoved", TraceLevel.Info);
            if (waterfallList != null)
            {
                lock (waterfallList)
                {
                    if (waterfallList.Contains(fall))
                    {
                        waterfallList.Remove(fall);
                    }
                }
            }
        }

        internal List<TNF> TNFs
        {
            get { return theRadio.TNFList; }
        }
        private void tnfAdded(TNF tnf)
        {
            Tracing.TraceLine("tnfAdded:" + tnf.ToString(), TraceLevel.Info);
            tnf.PropertyChanged += new PropertyChangedEventHandler(tnfPropertyChangedHandler);
            // No need to call the update, since created TNFs are not permanent.
        }
        private void tnfRemoved(TNF tnf)
        {
            Tracing.TraceLine("tnfRemove:" + tnf.ID.ToString(), TraceLevel.Info);
            // Don't call UpdateConfiguredTNFs here.
            //if ((UpdateConfiguredTNFs != null) & !Closing) UpdateConfiguredTNFs(tnf, true);
        }

        /// <summary>
        /// dBm to whole watts. <b>Currently has no callers</b> — as of
        /// 2026-08-16 nothing in the repo invokes it — and it is the rounding
        /// this track exists to remove. Use <see cref="DBmToWatts"/>. Left in
        /// place only because deleting a protected member of a shared base
        /// class is a structural change; delete it once no track is in flight.
        /// </summary>
        protected int DBmToPower(float dbm)
        {
            return (int)(DBmToWatts(dbm) + 0.5f);
        }

        /// <summary>
        /// dBm to watts, without rounding to an integer. The whole reason this
        /// exists separately from <see cref="DBmToPower"/>.
        /// </summary>
        public static float DBmToWatts(float dbm)
        {
            return (float)(Math.Pow(10d, dbm / 10d) / 1000d);
        }

        // -150 dBm, not 0. Zero dBm is one milliwatt — a real, legitimate
        // reading — so a field defaulting to 0 claims the radio is making
        // power before any meter data has arrived. -150 matches the idle
        // value the SC_MIC / SW ALC fields already use for "nothing yet".
        protected float _PowerDBM = -150f;
        /// <summary>Forward power in dBm (raw from FlexLib).</summary>
        public float PowerDBM => _PowerDBM;

        /// <summary>
        /// Forward power in WATTS, as a float.
        /// <para>Exists because <see cref="SMeter"/> returns <c>int</c>, and on
        /// transmit it converts dBm to watts and truncates. Measured on a
        /// FLEX-8600 on 2026-08-16 with the radio's power set to its default of
        /// zero: three consecutive keyed samples read 17.0, 22.4 and 18.7 dBm —
        /// 50, 174 and 74 milliwatts of real RF leaving the radio — and all
        /// three displayed as <c>0 watts</c>, indistinguishable from not
        /// transmitting at all. Sub-watt is the normal operating point for
        /// transverter and QRP work, not a fault, so the instrument has to be
        /// able to show it.</para>
        /// <para><see cref="SMeter"/> is dual-purpose — watts on transmit,
        /// S-units on receive — and its S-unit callers legitimately want
        /// integers, so its contract is deliberately left alone. Transmit
        /// display paths use this instead.</para>
        /// </summary>
        public float ForwardPowerWatts => DBmToWatts(_PowerDBM);

        /// <summary>
        /// Forward power for a narrow display field — the Home S-meter column
        /// is four characters wide.
        /// <para>Precision follows magnitude: a hundred watts does not need
        /// decimals, and sub-watt is nothing BUT decimals. Under one watt the
        /// leading zero is dropped (".050", ".174") so three decimals still fit
        /// four columns, which is milliwatt resolution — enough for every drive
        /// level a transverter asks for.</para>
        /// </summary>
        public static string FormatForwardPowerCompact(float watts)
        {
            if (float.IsNaN(watts) || watts < 0.0005f) return "0";
            if (watts >= 100f) return watts.ToString("F0");   // "100", "1500"
            if (watts >= 1f) return watts.ToString("F1");     // "5.2", "12.5"
            string s = watts.ToString("F3");                  // "0.050"
            return s.Length > 1 && s[0] == '0' ? s.Substring(1) : s; // ".050"
        }

        /// <summary>
        /// Forward power for speech and for status text, with its unit.
        /// Same precision-follows-magnitude rule as
        /// <see cref="FormatForwardPowerCompact"/>, minus the four-column
        /// squeeze — so the leading zero stays (it reads better) and trailing
        /// zeros go ("0.05 watts", not "0.050 watts").
        /// </summary>
        public static string FormatForwardPowerSpoken(float watts)
        {
            if (float.IsNaN(watts) || watts < 0.0005f) return "0 watts";
            if (watts >= 100f) return watts.ToString("F0") + " watts";
            if (watts >= 1f) return watts.ToString("F1") + " watts";
            string s = watts.ToString("F3").TrimEnd('0');
            return s + " watts";
        }

        private void forwardPowerData(float data)
        {
            meterTrace.Report("forwardPower:", data);
            // The change guard here existed only to avoid re-raising
            // MeterChanged for a repeated value. With that event gone the
            // comparison decides nothing, so the assignment stands alone.
            _PowerDBM = data;
        }

        protected float _SWR;
        /// <summary>
        /// SWR exactly as the radio reported it.
        /// </summary>
        /// <remarks>
        /// <b>Do not trust this for a safety decision.</b> Measured on
        /// 2026-08-22 transmitting into an unterminated antenna port: forward
        /// 17.5 W, reflected 13.4 W — a true SWR near 15 — and this meter
        /// reported <b>1.008</b>, then its idle sentinel of −25 while transmit
        /// was still in progress. Use <see cref="ComputedSWR"/>.
        /// </remarks>
        public float SWRValue => _SWR;

        private void sWRData(float data)
        {
            meterTrace.Report("SWRData:", data);
            _SWR = data;
        }

        /// <summary>
        /// The SWR sentinel. The radio reports this when it has no reading,
        /// including part-way through a transmit — so it must never be read as
        /// a low SWR.
        /// </summary>
        public const float SWRNoReading = -25f;

        /// <summary>
        /// SWR derived from forward and reflected power, which are raw
        /// measurements rather than a derived number.
        /// </summary>
        /// <remarks>
        /// <para>
        /// From the reflection coefficient: Γ = √(Pr / Pf), SWR = (1+Γ)/(1−Γ).
        /// </para>
        /// <para>
        /// <b>Why this exists.</b> On 2026-08-22 the radio's own SWR meter read
        /// 1.008 while 76% of the power was coming back off an empty antenna
        /// port, and read 1.047 — correct to three decimals against this
        /// calculation — when a dummy load was properly connected. It is
        /// accurate when the antenna system is fine and wrong when it is not,
        /// which is precisely backwards: SWR is a number nobody consults until
        /// something has already gone wrong.
        /// </para>
        /// <para>
        /// A sighted operator sees the needle slam and knows. Ours was told
        /// "1.008", which is worse than no instrument because it reassures.
        /// </para>
        /// <para>
        /// Returns <see cref="float.NaN"/> when there is not enough forward
        /// power to derive anything — never a plausible-looking 1.0, because an
        /// invented good reading is the failure this replaces.
        /// </para>
        /// </remarks>
        public float ComputedSWR => SwrFromPower(_PowerDBM, _ReflectedPower);

        /// <summary>
        /// SWR from a forward and a reflected power reading, both in dBm.
        /// Pure, so it can be tested against measured pairs.
        /// </summary>
        /// <returns>
        /// The standing wave ratio, or NaN when forward power is too low to
        /// derive one.
        /// </returns>
        public static float SwrFromPower(float forwardDBm, float reflectedDBm)
        {
            if (float.IsNaN(forwardDBm) || float.IsNaN(reflectedDBm)) return float.NaN;

            float pf = DBmToWatts(forwardDBm);
            float pr = DBmToWatts(reflectedDBm);

            // Below this there is no transmit worth judging, and the ratio of
            // two tiny numbers is noise. A dead key measured 0.22 W on
            // 2026-08-22, so this sits well under any real keying.
            if (pf < 0.05f) return float.NaN;

            // Reflected above forward is not physical: it means one meter is
            // lying or they were sampled at different instants. Say "unknown"
            // rather than invent a number.
            if (pr >= pf) return float.NaN;

            double gamma = Math.Sqrt(pr / pf);
            return (float)((1.0 + gamma) / (1.0 - gamma));
        }

        /// <summary>
        /// The share of transmit power coming back, 0 to 1. The rawest
        /// available answer to "is this actually going into a load?", and the
        /// check a bench session should open with.
        /// </summary>
        /// <remarks>
        /// Measured 2026-08-22: a properly connected dummy load returned
        /// <b>0.0005</b> (0.054 W of 101.2 W). An empty antenna port returned
        /// <b>0.76</b>. That is three orders of magnitude, and it needs no
        /// calibration to interpret.
        /// </remarks>
        /// <summary>
        /// How much of the forward power is coming back, from 0 to 1, or NaN
        /// when there is too little power to judge.
        /// <para>The arithmetic lives in
        /// <see cref="TransmitSafety.ReflectedFractionOf"/> so that the live
        /// transmit warning and its tests share one definition with this
        /// property rather than drifting into two.</para>
        /// </summary>
        public float ReflectedFraction =>
            TransmitSafety.ReflectedFractionOf(ForwardPowerWatts, ReflectedPowerWatts);

        private string SWRText()
        {
            return _SWR.ToString("f1");
        }

        private float _MicData;
        /// <summary>
        /// Current microphone level from hardware meter. Updated by meter callback.
        /// </summary>
        public float MicData => _MicData;

        private void micData(float data)
        {
            _MicData = data;
            // Inventory first so the trace reads in order: what the radio has,
            // then which of the two TX meters we managed to hook out of it.
            syncMeterInventory(); // cheap no-op unless the meter set has changed
            hookTxMeters(); // lazy: SC_MIC / SW ALC meters register late
            meterTrace.Report("micData:", data);
        }

        internal float _MicPeakData;
        private void micPeakData(float data)
        {
            meterTrace.Report("micPeakData:", data);
            _MicPeakData = data;
        }

        private float _CompPeakData;
        /// <summary>Compression peak level.</summary>
        public float CompPeak => _CompPeakData;

        private void compPeakData(float data)
        {
            meterTrace.Report("compPeakData:", data);
            _CompPeakData = data;
        }

        private float _ALC;
        /// <summary>
        /// Current HARDWARE ALC — the voltage on the external-amplifier ALC RCA
        /// jack, dBFS. This is amp-overdrive-protection feedback (older amps use
        /// it), NOT the radio's transmit drive. Reads ~0 with no amp connected.
        /// For transmit-drive level use <see cref="SwAlcDb"/>. Kept and correctly
        /// scoped as of 2026-08-11 (it was previously mislabeled the TX "ALC").
        /// </summary>
        public float ALC => _ALC;

        private void hwALCData(float data)
        {
            _ALC = data;
            meterTrace.Report("hwALCData:", data);
        }

        // --- Transmit-audio meters (2026-08-11) ------------------------------
        // FlexLib raises no dedicated event for SC_MIC or the SW ALC meter, so
        // we hook their DataReady directly via FindMeterByName. Why these:
        //   * SC_MIC ("MIC output") sits downstream of mic SELECTION, so it
        //     reflects transmit audio from EITHER source — the analog mic AND
        //     PC/codec audio. MicData (the COD-/MIC meter, "MIC in CODEC") is the
        //     analog ADC path and reads -120 for PC audio, which is why the old
        //     "Check microphone" warning cried wolf on every PC-audio transmit.
        //   * SW ALC is the real transmit-drive meter (the ALC property above is
        //     HWALC, the external-amp jack).
        // Proven with a two-source meter truth table on the bench 2026-08-11.
        // All values dBFS.
        // Tracked independently, one flag per meter. They used to share a
        // single "hooked" flag that was only set when BOTH were found, so a
        // radio reporting one and not the other re-subscribed to the one it
        // DID find on every subsequent mic-meter event — an unbounded handler
        // leak, with every event firing N times and N growing forever. On the
        // bench 8600 both meters arrive in the same instant so it never bit
        // (two "NOT FOUND" passes, then "found, found"), which is exactly why
        // it survived: the failure needs a radio we have not tested on.
        private bool _scMicHooked;
        private bool _swAlcHooked;
        private bool _txMetersHooked => _scMicHooked && _swAlcHooked;
        private string _txMeterHookState = "";
        private float _scMicDb = -150f, _scMicMaxDb = -150f, _swAlcDb = -150f;
        private float _scMicRecentDb = -150f;
        private int _scMicRecentTime;
        /// <summary>Instantaneous SC_MIC — transmit mic audio from any source, dBFS.</summary>
        public float ScMicDb => _scMicDb;
        /// <summary>Max SC_MIC since the last <see cref="ResetScMicMax"/> — a peak-hold
        /// across a transmit window so the gaps between spoken words don't read as silence.
        /// Use for the silent-mic warning and end-of-check verdict.</summary>
        public float ScMicMaxDb => _scMicMaxDb;
        /// <summary>Peak SC_MIC over a rolling ~1.5 s window, dBFS. Follows the level down
        /// after ~1.5 s, so a LIVE "how's my audio" query tracks mic-gain changes mid-transmit
        /// (where <see cref="ScMicMaxDb"/> only ever grows).</summary>
        public float ScMicRecentDb => _scMicRecentDb;
        /// <summary>SW ALC — transmit drive after software ALC (SSB peak), dBFS. Distinct from
        /// <see cref="ALC"/> (=HWALC, the external-amplifier jack).</summary>
        public float SwAlcDb => _swAlcDb;
        /// <summary>Reset the SC_MIC peak-hold. Call at transmit start.</summary>
        public void ResetScMicMax() => _scMicMaxDb = -150f;
        private int _meterInventoryCount = -1;

        // --- The whole meter inventory, identity preserved (Sprint 32 Track A) ---
        //
        // FlexLib raises Meter.DataReady(Meter, float) for every meter the radio
        // publishes — the meter itself comes with the reading, carrying name,
        // source, source index, units and range. FlexBase historically subscribed
        // instead to ten NAMED convenience events (MicDataReady, SWRDataReady and
        // the rest), threw the meter away, and re-emitted MeterType, an eight-value
        // enum. An 8600 reports 102 meters. Everything past that boundary was
        // choosing from eight because identity had already been destroyed, and
        // nothing above a lossy adapter can recover what the adapter dropped.
        //
        // So: subscribe generically, once per meter, and re-raise with the meter
        // intact. MeterType and MeterChanged were left alive here as a shim and
        // then retired by Track B, the track that rebuilt the meters panel, once
        // MeterToneEngine — their only consumer — had moved onto MeterData.

        private readonly object _meterHookLock = new object();

        /// <summary>
        /// The opt-in, once-a-second, min/max/last meter stream — task #170.
        /// The eight per-packet meter handlers report through this instead of
        /// tracing raw lines; see MeterTraceStream for the incident and the
        /// measured numbers (49% of a 52 MB capture was these lines).
        /// </summary>
        private readonly MeterTraceStream meterTrace = new MeterTraceStream();

        /// <summary>Meters already subscribed, by object identity rather than
        /// index. A removed-then-re-added meter is a NEW Meter object that may
        /// reuse its index, and identity is the only comparison that hooks it.</summary>
        private readonly HashSet<Meter> _hookedMeters = new HashSet<Meter>();

        private int _meterSyncTime;

        /// <summary>Every meter the radio currently publishes, or an empty list
        /// when there is no radio. A snapshot: FlexLib copies under its own lock,
        /// so the returned list never mutates underneath a caller.</summary>
        public ImmutableList<Meter> RadioMeters =>
            theRadio?.GetMeters() ?? ImmutableList<Meter>.Empty;

        /// <summary>Any meter reported a value, with the meter itself.
        /// <para>Fires at meter rate for every meter the radio publishes, on
        /// FlexLib's meter thread. Handlers must be cheap and must not block.</para></summary>
        public delegate void MeterDataDel(object sender, Meter meter, float value);

        /// <summary>Raised for every reading of every meter, meter identity intact.
        /// The only meter feed there is: the older eight-value MeterChanged path
        /// was retired in Sprint 32 Track B.</summary>
        public event MeterDataDel MeterData;

        /// <summary>The SET of meters the radio publishes changed.
        /// <para>Load-bearing: FlexLib raises nothing when a meter appears, and the
        /// list GROWS DURING REGISTRATION — an early snapshot catches eleven meters
        /// with the TX-side ones still to arrive. Bind to this rather than sampling
        /// the inventory once at construction.</para></summary>
        public event EventHandler MeterInventoryChanged;

        /// <summary>
        /// Reconcile our subscriptions with the radio's meter list, and announce
        /// the list when it changes.
        /// <para>Throttled to twice a second. It is driven from every meter
        /// reading (see <see cref="onMeterDataReady"/>) so that ANY streaming
        /// meter keeps the inventory fresh — the census must not depend on one
        /// particular meter existing — and also from <c>micData</c>, which is what
        /// gets it started before anything is hooked.</para>
        /// </summary>
        private void syncMeterInventory()
        {
            Radio radio = theRadio;
            if (radio == null) return;

            int now = Environment.TickCount;
            if (_meterInventoryCount >= 0 && (now - _meterSyncTime) < 500) return;

            bool changed;
            ImmutableList<Meter> snapshot;
            try
            {
                lock (_meterHookLock)
                {
                    _meterSyncTime = now;
                    snapshot = radio.GetMeters();
                    changed = snapshot.Count != _meterInventoryCount;

                    foreach (Meter m in snapshot)
                    {
                        if (_hookedMeters.Add(m))
                        {
                            m.DataReady += onMeterDataReady;
                            changed = true;
                        }
                    }

                    if (!changed) return;
                    _meterInventoryCount = snapshot.Count;

                    // Meters that have gone away leave dead references behind, and
                    // this runs for the life of the connection. Prune on the same
                    // pass that noticed the change; nothing else can notice it.
                    if (_hookedMeters.Count != snapshot.Count)
                        _hookedMeters.IntersectWith(snapshot);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("syncMeterInventory: " + ex.Message, TraceLevel.Warning);
                return;
            }

            // Outside the lock. Tracing 102 lines is 102 file writes, and a
            // handler on MeterInventoryChanged is somebody else's code.
            traceMeterInventory(snapshot);
            MeterInventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// One handler for every meter on the radio. Re-raises with the meter
        /// intact, then lets the reading drive the next inventory reconcile —
        /// which is what makes late-arriving meters self-healing rather than
        /// dependent on a poll somebody remembered to start.
        /// </summary>
        private void onMeterDataReady(Meter meter, float data)
        {
            MeterData?.Invoke(this, meter, data);
            syncMeterInventory();
        }

        /// <summary>
        /// Forget every meter subscription. Called when the radio goes away: the
        /// Meter objects go with it, and a fresh connect publishes new ones.
        /// </summary>
        private void resetMeterInventory()
        {
            lock (_meterHookLock)
            {
                _hookedMeters.Clear();
                _meterInventoryCount = -1;
                _meterSyncTime = 0;
            }
        }

        /// <summary>
        /// Trace the meter inventory the radio reports about itself, once per
        /// connect.
        /// <para>The radio is asked for this already — <c>Radio.GetMeterList</c>
        /// sends a literal "meter list" command — but the reply handler parses
        /// the answer and traces nothing, so what a given radio actually offers
        /// has never been observable from a trace. That became a real gap on
        /// 2026-08-16: the meters subsystem is being designed against the eight
        /// readings on the Meters page, and those eight are a hand-picked
        /// hardcoded subset. What an 8600 running four slices reports — how
        /// many, under what names, in what units, and which are per-slice — is
        /// simply unknown.</para>
        /// <para>Enumerated through <c>Radio.GetMeters()</c>, the JJFlex patch in
        /// <c>FlexLib_API/FlexLib/Radio.cs</c> (MIGRATION.md reapply item 11).
        /// This method reached the same private field by reflection until
        /// 2026-08-19; the patch replaced it, and the reflection was deleted in
        /// the same commit, because two routes to one private field is how one
        /// of them rots unnoticed.</para>
        /// </summary>
        /// <remarks>
        /// <para>Called by <see cref="syncMeterInventory"/> only when the set has
        /// actually changed, and outside its lock. Re-logging whenever the set
        /// CHANGES rather than once is deliberate: the first version fired a
        /// single time off the first mic-meter event, which turned out to
        /// snapshot the radio mid-registration — eleven meters, with the TX-side
        /// ones still to arrive. A truncated census is worse than none, because
        /// the meters subsystem is designed against exactly this list.</para>
        /// </remarks>
        private void traceMeterInventory(ImmutableList<Meter> snapshot)
        {
            try
            {
                foreach (var m in snapshot)
                {
                    Tracing.TraceLine("meterInventory: [" + m.Index + "] " + m.Name
                        + " \"" + m.Description + "\""
                        + " src=" + m.Source + ":" + m.SourceIndex
                        + " range=" + m.Low + ".." + m.High
                        + " units=" + m.Units, TraceLevel.Info);
                }
                Tracing.TraceLine("meterInventory: " + snapshot.Count + " meters reported",
                    TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("meterInventory: " + ex.Message, TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Subscribe to SC_MIC and SW ALC, lazily — the TX meters register
        /// late, so this retries on each mic-meter event until both are found.
        /// <para>Each meter is claimed at most once, and only ever looked up
        /// while it is still missing. Anything else re-subscribes the meter
        /// that IS present every time the retry runs.</para>
        /// </summary>
        private void hookTxMeters()
        {
            if (_txMetersHooked || theRadio == null) return;

            // Look up only what is still missing, and claim it in the same
            // step as subscribing so a retry can never double-hook it.
            if (!_scMicHooked)
            {
                var sc = theRadio.FindMeterByName("SC_MIC");
                if (sc != null)
                {
                    _scMicHooked = true;
                    sc.DataReady += (m, d) =>
                    {
                        _scMicDb = d;
                        if (d > _scMicMaxDb) _scMicMaxDb = d;
                        int now = System.Environment.TickCount;
                        if (d >= _scMicRecentDb || (now - _scMicRecentTime) > 1500) { _scMicRecentDb = d; _scMicRecentTime = now; }
                        traceTxMeters();
                    };
                }
            }

            if (!_swAlcHooked)
            {
                var alc = theRadio.FindMeterByName("ALC");
                if (alc != null)
                {
                    _swAlcHooked = true;
                    alc.DataReady += (m, d) => { _swAlcDb = d; traceTxMeters(); };
                }
            }

            // Say plainly which of the two we actually found. On a FLEX-8600 the
            // radio's meter list carries MIC, MICPEAK and HWALC — a plain "ALC"
            // may not be there at all, in which case SwAlcDb never moves and the
            // "TX drive (ALC)" readout shows a number that will never change,
            // silently. Whichever way it goes, the trace should not make anyone
            // guess.
            //
            // Only when the answer CHANGES, though. This retry runs on every
            // mic-meter event, so an unconditional line here traces at meter
            // rate forever on a radio missing one of the two — the same flood
            // fixed in startOpusInputChannel, from a different direction.
            string state = (_scMicHooked ? "found" : "NOT FOUND")
                + "|" + (_swAlcHooked ? "found" : "NOT FOUND");
            if (state != _txMeterHookState)
            {
                _txMeterHookState = state;
                Tracing.TraceLine("hookTxMeters: SC_MIC " + (_scMicHooked ? "found" : "NOT FOUND")
                    + ", ALC " + (_swAlcHooked ? "found" : "NOT FOUND"), TraceLevel.Info);
            }
        }

        private int _txMeterTraceTime;

        /// <summary>
        /// A correlated SC_MIC / SW ALC / forward-power snapshot, at most once a
        /// second while transmitting.
        /// <para>Both handlers stored their value and traced nothing, so with PC
        /// audio running there was no way to tell from a trace whether the radio
        /// was seeing any transmit drive at all — a blind spot that cost a
        /// debugging session. Correlated on purpose: the three together say
        /// whether audio is arriving AND whether it is turning into RF.</para>
        /// <para>Throttled deliberately. These fire at meter rate, and an
        /// unthrottled TraceLine here would recreate precisely the flood being
        /// fixed in startOpusInputChannel.</para>
        /// </summary>
        private void traceTxMeters()
        {
            if (!Transmit) return;
            int now = System.Environment.TickCount;
            if ((now - _txMeterTraceTime) < 1000) return;
            _txMeterTraceTime = now;
            Tracing.TraceLine("txMeters: SC_MIC=" + _scMicDb.ToString("F1")
                + " (peak " + _scMicMaxDb.ToString("F1") + ")"
                + " SWALC=" + _swAlcDb.ToString("F1")
                + " fwd=" + _PowerDBM.ToString("F1") + " dBm", TraceLevel.Info);
        }

        // --- PC-side transmit loudness, LUFS (Engine Track, 2026-08-11) ------
        // ITU-R BS.1770-4 / EBU R128: K-weighted, gated, computed on the raw
        // mic float samples in JJPortaudio's input callback — pre-Opus, and
        // after the test-tone injection point, so it measures whatever is
        // actually transmitted, tone or mic. The integrated gate natively
        // drops the silent gaps between words that make an ungated meter
        // false-alarm on every breath; ScMicRecentDb's rolling peak-hold
        // solved that by hand, LUFS gating supersedes it with no custom logic.
        //
        // Division of labour, on purpose: LUFS says "you sound right"; the
        // radio's SW ALC (SwAlcDb) stays a hard guardrail saying "you are not
        // overdriving the transmitter". Two different failure modes — never
        // collapse them into one number.
        //
        // Measurement-point caveat: these figures exist only for the PC-audio
        // path. An analog mic at the radio produces no PC-side samples, so
        // consumers MUST check TxLufsAvailable and fall back to the
        // SC_MIC/ALC meters instead of reporting a stale or wrong number.
        //
        // Calibration anchor (bench, 2026-08-11): a tone injected at -10 dBFS
        // read -11 on the radio's SC_MIC meter — the chain is honest to about
        // a dB. The meter's channel model (mono mic duplicated to stereo,
        // channel powers summed per the spec) makes a -10 dBFS tone read
        // -10.0 LUFS, so LUFS and SC_MIC land within that same dB.
        private readonly JJPortaudio.LufsMeter txLufsMeter = new JJPortaudio.LufsMeter();

        /// <summary>Momentary transmit loudness (400 ms window), LUFS. -150 when
        /// silent or no PC TX audio is flowing. For live coaching. Raw figure —
        /// geeks get to read the number itself.</summary>
        public float TxLufsMomentary => txLufsMeter.MomentaryLufs;

        /// <summary>Short-term transmit loudness (3 s window), LUFS. -150 when
        /// silent or no PC TX audio is flowing. The "how's my audio" figure and
        /// the auto-set-mic-level target.</summary>
        public float TxLufsShortTerm => txLufsMeter.ShortTermLufs;

        /// <summary>Integrated (gated) transmit loudness since the last
        /// <see cref="ResetTxLufsIntegrated"/>, LUFS. The calibration-sample
        /// figure: speak for a few seconds and read one honest number, silence
        /// between words gated out per BS.1770.</summary>
        public float TxLufsIntegrated => txLufsMeter.IntegratedLufs;

        /// <summary>The integrated accumulation WITHOUT gating — what a naive
        /// meter would say. Diagnostic contrast for the verbose readout.</summary>
        public float TxLufsIntegratedUngated => txLufsMeter.IntegratedUngatedLufs;

        /// <summary>Start a fresh integrated measurement (momentary and
        /// short-term are unaffected). Call at the start of a calibration
        /// sample.</summary>
        public void ResetTxLufsIntegrated() => txLufsMeter.ResetIntegrated();

        /// <summary>
        /// True when the LUFS figures are measuring the real transmit feed:
        /// PC audio is on, the radio's transmit input is the PC, and samples
        /// have flowed within the last half second (the PC TX stream only runs
        /// while transmitting in a voice mode). When false, fall back to
        /// <see cref="ScMicDb"/>/<see cref="SwAlcDb"/> — the analog-mic path
        /// has no PC-side samples, and the PC mic stream can also run while
        /// the radio transmits a DIFFERENT source, so this gate is what keeps
        /// the number honest rather than merely present.
        /// </summary>
        public bool TxLufsAvailable =>
            PCAudio
            && string.Equals(MicSource, "PC", StringComparison.OrdinalIgnoreCase)
            && txLufsMeter.HasRecentData;

        /// <summary>
        /// True when the RETAINED figures — <see cref="TxLufsIntegrated"/> and
        /// <see cref="TxLoudnessProfile"/> — describe a real PC-audio sample.
        /// Deliberately NOT <see cref="TxLufsAvailable"/>: that one requires
        /// samples in the last half second, which is false the instant the
        /// operator unkeys, and the whole point of an integrated figure is
        /// that you read it AFTER the transmit it describes.
        /// </summary>
        public bool TxLufsSampleAvailable =>
            PCAudio
            && string.Equals(MicSource, "PC", StringComparison.OrdinalIgnoreCase)
            && txLufsMeter.IntegratedBlockCount > 0;

        /// <summary>
        /// Speech level, noise floor, and the gap between them for the current
        /// integrated sample. The gap is the figure with no prior consumer:
        /// LUFS gating ignores the quiet between words on purpose, but a noisy
        /// room produces no quiet, so continuous noise is measured as level.
        /// A healthy number with only a few LU of daylight underneath it is a
        /// microphone hearing a room rather than a person. Check
        /// <see cref="JJPortaudio.LufsMeter.LoudnessProfile.IsValid"/> — a
        /// sample too short to judge says so instead of guessing.
        /// </summary>
        public JJPortaudio.LufsMeter.LoudnessProfile TxLoudnessProfile => txLufsMeter.Profile;

        private void txBandSettingsHandler(TxBandSettings settings)
        {
            Tracing.TraceLine("txBandSettingsHandler:" + settings.BandName, TraceLevel.Info);
        }

        private float _PATempData;
        private void PATempDataHandler(float data)
        {
            // Reported through the coalesced meter stream, NOT as a Verbose
            // trace line. #196-era finding, 2026-08-22: this handler existed,
            // the property existed, and PATEMP appeared in the meter model and
            // in the transmit chain evidence — but the diagnostic capture
            // carried only seven meters and this was not one of them, because
            // Verbose lines are dropped at the Normal detail level a real
            // session runs at. So an entire bench evening produced no
            // temperature record at all.
            //
            // That mattered the moment unattended keying was authorised
            // (2026-08-22). #192 specifies that automated sweeps abort on
            // RISING PA temperature rather than on an elapsed-time count — a
            // timer assumes the thermal model, the temperature measures it —
            // and a sweep cannot abort on a meter nobody records. While a
            // human was present the gap was theoretical. Unattended, against a
            // load rated 2000 W for ONE MINUTE at tuning duty, it is the
            // actual safety mechanism.
            meterTrace.Report("paTemp:", data);
            _PATempData = data;
        }

        /// <summary>
        /// PA temperature in degrees C.
        /// <para>The abort signal for unattended transmit sweeps. Watch the
        /// TREND rather than an absolute: what matters is whether it is still
        /// climbing when a run wants to key again.</para>
        /// </summary>
        public float PATemp => _PATempData;

        private float _VoltsData;
        private void VoltsDataHandler(float data)
        {
            Tracing.TraceLine("VoltsDataHandler:" + data.ToString(), TraceLevel.Verbose);
            _VoltsData = data;
        }

        /// <summary>Supply voltage.</summary>
        public float Volts => _VoltsData;

        private float _ReflectedPower;
        /// <summary>
        /// Reflected power as the meter reports it, in <b>dBm</b> — NOT watts.
        /// <para>The name is the trap. It matches <c>forwardPower</c> in the
        /// meter trace, and on 2026-08-22 a whole bench session was analysed
        /// with these values read as watts: 50 dBm was reported as "50 watts"
        /// when it is 100, and the resulting "the radio will not make full
        /// power" conclusion was fabricated out of a unit error. Use
        /// <see cref="ReflectedPowerWatts"/> anywhere a human will see the
        /// number.</para>
        /// </summary>
        public float ReflectedPower => _ReflectedPower;

        /// <summary>
        /// Reflected power in watts, the companion to
        /// <see cref="ForwardPowerWatts"/>.
        /// <para>Exists so that no caller has to remember the dBm conversion,
        /// and so that forward and reflected are reached the same way. Into a
        /// good dummy load this is a rounding error — 0.054 W against 101.2 W
        /// forward, measured 2026-08-22. Into an open connector on the same
        /// radio minutes earlier it was 13.4 W against 17.5 W forward, which is
        /// most of the transmitter's output arriving back at the finals.</para>
        /// </summary>
        public float ReflectedPowerWatts => DBmToWatts(_ReflectedPower);

        private void reflectedPowerData(float data)
        {
            meterTrace.Report("reflectedPower:", data);
            _ReflectedPower = data;
        }

        private float _PAEffData;
        /// <summary>PA efficiency percentage.</summary>
        public float PAEfficiency => _PAEffData;

        private void paEffData(float data)
        {
            Tracing.TraceLine("paEffData:" + data.ToString(), TraceLevel.Verbose);
            _PAEffData = data;
        }

        private void meterAdded(Slice slc, Meter m)
        {
            Tracing.TraceLine("meterAdded:slice " + slc.Index.ToString() + ' ' + m.ToString(), TraceLevel.Info);
        }

        private void meterRemoved(Slice slc, Meter m)
        {
            Tracing.TraceLine("meterRemoved:" + m.ToString(), TraceLevel.Info);
        }

        private class sMeter_t
        {
            private Slice s;
            private FlexBase parent;

            // The channel key is built once here, not per reading: this handler
            // runs at meter rate, and the whole point of MeterTraceStream's
            // disabled fast path is that a reading costs one flag check — a
            // string concatenation per packet would give that back.
            private readonly string traceChannel;

            public void sMeterData(float data)
            {
                // Only report for the active slice.
                if (s.Active)
                {
                    parent.meterTrace.Report(traceChannel, data);
                    parent._SMeter = (int)data;
                }
            }

            internal sMeter_t(FlexBase p, Slice slc)
            {
                parent = p;
                s = slc;
                traceChannel = "sMeterData:" + slc.Index;
            }
        }

        private bool _Transmit;
        /// <summary>
        /// True if transmitting
        /// </summary>
        public bool Transmit
        {
            get
            {
                return _Transmit;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.Mox = value; }), "Mox");
            }
        }

        /// <summary>
        /// Toggle the tune carrier on/off. Puts radio into tune mode (low-power CW carrier).
        /// Sprint 22: Wraps FlexLib's TXTune property for UI/hotkey access.
        /// </summary>
        public bool TxTune
        {
            get => theRadio.TXTune;
            set { q.Enqueue((FunctionDel)(() => { theRadio.TXTune = value; }), "TXTune"); }
        }

        /// <summary>
        /// True if rig is on the WAN.
        /// </summary>
        public bool RemoteRig
        {
            // Null-conditional: read from the Audio Workshop session while the
            // radio tears down (same crash class as the TX getter family).
            get { return theRadio?.IsWan ?? false; }
        }

        private int firstCharID = -1;
        private StringBuilder sentChars = new StringBuilder();
        private void charSentHandler(int id)
        {
            Tracing.TraceLine("charSent:" + id, TraceLevel.Info);
            if (firstCharID == -1) firstCharID = id;
        }

        public bool SmeterInDBM = false;
        /// <summary>
        /// Calibrated S-Meter/power
        /// </summary>
        /// <remarks>
        /// <para>Dual-purpose: S-units (or dBm) on receive, whole watts on
        /// transmit. The S-unit callers legitimately want an integer, so the
        /// contract stays as it is.</para>
        /// <para><b>Do not use the transmit branch for a power readout.</b> It
        /// truncates, so anything under half a watt reads 0 — the same as not
        /// transmitting. Use <see cref="ForwardPowerWatts"/> with
        /// <see cref="FormatForwardPowerCompact"/> or
        /// <see cref="FormatForwardPowerSpoken"/>. The branch is kept only so
        /// existing integer callers keep compiling.</para>
        /// </remarks>
        private int _SMeter;
        public int SMeter
        {
            get
            {
                if (Transmit)
                {
                    // Whole watts. See the remarks above before reusing this.
                    return (int)(DBmToWatts(_PowerDBM) + 0.5f);
                }
                else
                {
                    if (SmeterInDBM)
                    {
                        return _SMeter;
                    }
                    // return s-units

                    int val = _SMeter + 127 - 3; // puts s0 at 0.
                    if (val < 0) val = 0;
                    int s = val / 6; // S-unit
                    // Perhaps indicate over S9.
                    val = (s <= 9) ? s : val - (9 * 6) + 9;
                    return val;
                }
            }
        }

        public bool HasActiveSlice
        {
            // Both hops null-conditional: Disconnect() nulls theRadio while
            // slice/menu events are still firing, so even this guard must not
            // assume a live radio (same teardown crash class as FilterLow).
            get { return (theRadio?.ActiveSlice != null); }
        }

        /// <summary>
        /// Letter of the active (RX) slice, e.g. "A". Empty if no active slice.
        /// </summary>
        public string ActiveSliceLetter => theRadio?.ActiveSlice?.Letter ?? "";

        /// <summary>
        /// Letter of the TX slice. Empty if no TX slice.
        /// </summary>
        public string TXSliceLetter => VFOToSlice(TXVFO)?.Letter ?? "";

        /// <summary>
        /// Convert a VFO index to its slice letter (A, B, C, D...).
        /// Falls back to the numeric index if the slice is not found.
        /// </summary>
        public string VFOToLetter(int vfo) => VFOToSlice(vfo)?.Letter ?? vfo.ToString();

        /// <summary>
        /// Get the frequency of a specific VFO index in MHz.
        /// Returns 0 if the VFO is invalid.
        /// </summary>
        public double GetVFOFrequency(int vfo)
        {
            lock (mySlices)
            {
                var slice = ValidVFO(vfo) ? mySlices[vfo] : null;
                return slice?.Freq ?? 0.0;
            }
        }

        /// <summary>
        /// Get the demodulation mode of a specific VFO index.
        /// Returns empty string if the VFO is invalid.
        /// </summary>
        public string GetVFOMode(int vfo)
        {
            lock (mySlices)
            {
                var slice = ValidVFO(vfo) ? mySlices[vfo] : null;
                return slice?.DemodMode ?? "";
            }
        }

        /// <summary>
        /// Get the owner description for a VFO index.
        /// Public wrapper for external callers who don't have access to Slice objects.
        /// </summary>
        public string GetSliceOwnerForVFO(int vfo)
        {
            var slice = VFOToSlice(vfo);
            return slice != null ? GetSliceOwnerName(slice.ClientHandle) : null;
        }

        /// <summary>
        /// Get the owner description for a slice by its client handle.
        /// Returns "yours" for our own slices, or the station name for others.
        /// Returns null if ownership info is unavailable.
        /// </summary>
        public string GetSliceOwnerName(uint clientHandle)
        {
            if (theRadio == null) return null;
            lock (theRadio.GuiClientsLockObj)
            {
                foreach (var client in theRadio.GuiClients)
                {
                    if (client.ClientHandle == clientHandle)
                    {
                        return client.IsThisClient ? "yours" : (client.Station ?? client.Program ?? "other");
                    }
                }
            }
            return null;
        }

        // Diversity readiness helper; ensure hardware, license, antennas, and slices
        public bool DiversityReady
        {
            get
            {
                if (theRadio == null) return false;
                bool hasHardware = theRadio.DiversityIsAllowed;
                bool hasLicense = (theRadio.FeatureLicense != null) &&
                                  (theRadio.FeatureLicense.LicenseFeatDivEsc != null) &&
                                  theRadio.FeatureLicense.LicenseFeatDivEsc.FeatureEnabled;
                bool hasAntennas = (theRadio.RXAntList != null) && (theRadio.RXAntList.Length >= 2);
                bool hasSlices = theRadio.AvailableSlices >= 2;
                return hasHardware && hasLicense && hasAntennas && hasSlices;
            }
        }

        public bool DiversityOn
        {
            get
            {
                return theRadio?.ActiveSlice?.DiversityOn ?? false;
            }
            set
            {
                if (!HasActiveSlice) return;
                if (!theRadio.DiversityIsAllowed) return;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.DiversityOn = value; }), "DiversityOn");
            }
        }

        /// <summary>
        /// Toggle diversity on/off for the active slice.
        /// Sprint 11: Replaces Flex6300Filters.ToggleDiversity().
        /// </summary>
        public void ToggleDiversity()
        {
            DiversityOn = !DiversityOn;
        }

        #region ESC — Enhanced Signal Clarity (Sprint 31 Track R)

        // ESC rides on the diversity pair and shares its licence
        // (LicenseFeatDivEsc), which is why it lives here rather than in a
        // region of its own. It is a per-SLICE setting with no radio-level
        // equivalent, and it must be applied to the same slice the Feature
        // Availability report reads it from — hence GetEscSlice below, which
        // was already here serving the report and is now serving the controls
        // that let an operator change what the report describes.
        //
        // Every accessor is null-safe and no-ops without a slice: ESC is only
        // reachable through a dialog that itself refuses to enable its controls
        // unless diversity is ready AND on, but the radio can disappear between
        // one and the other.

        private Slice EscSlice => theRadio == null ? null : GetEscSlice(theRadio.ActiveSlice);

        /// <summary>True when ESC is switched on for the slice carrying it.</summary>
        public bool EscEnabled
        {
            get => EscSlice?.ESCEnabled == true;
            set { var s = EscSlice; if (s != null) s.ESCEnabled = value; }
        }

        /// <summary>ESC phase shift in degrees, 0 to 360.</summary>
        public double EscPhaseShift
        {
            get => EscSlice?.ESCPhaseShift ?? 0.0;
            set { var s = EscSlice; if (s != null) s.ESCPhaseShift = value; }
        }

        /// <summary>ESC gain. FlexLib's own default is 1.0, not zero.</summary>
        public double EscGain
        {
            get => EscSlice?.ESCGain ?? 1.0;
            set { var s = EscSlice; if (s != null) s.ESCGain = value; }
        }

        #endregion

        public string DiversityGateMessage
        {
            get
            {
                if (theRadio == null) return "Radio not ready";
                if (!HasActiveSlice) return "Select a slice";
                if (!theRadio.DiversityIsAllowed) return "Model lacks diversity support";
                var licenseFeature = theRadio.FeatureLicense?.LicenseFeatDivEsc;
                if (licenseFeature == null) return "Diversity license status pending";
                if (!licenseFeature.FeatureEnabled)
                {
                    return licenseFeature.FeatureGatedMessage ?? "Purchase a diversity license to enable this feature";
                }
                if ((theRadio.RXAntList?.Length ?? 0) < 2) return "Need two RX antennas";
                if (theRadio.AvailableSlices < 2) return "Need two slices for diversity";
                return string.Empty;
            }
        }

        internal string DiversityStatus
        {
            get
            {
                string gate = DiversityGateMessage;
                if (!string.IsNullOrEmpty(gate)) return gate;
                string status = DiversityOn ? "Diversity active" : "Diversity ready";
                string ants = DiversityAntennas;
                return string.IsNullOrEmpty(ants) ? status : status + " (" + ants + ")";
            }
        }

        private string DiversityAntennas
        {
            get
            {
                if (!HasActiveSlice) return string.Empty;
                string primary = theRadio?.ActiveSlice?.RXAnt ?? string.Empty;
                string child = theRadio?.ActiveSlice?.DiversitySlicePartner?.RXAnt;
                if (string.IsNullOrEmpty(child)) return primary;
                if (string.Equals(primary, child, StringComparison.Ordinal)) return primary;
                if (string.IsNullOrEmpty(primary)) return child;
                return primary + "/" + child;
            }
        }

        #region Antenna Properties — Sprint 22

        /// <summary>Current RX antenna name for active slice (e.g. "ANT1", "ANT2", "RX_A").</summary>
        public string RXAntennaName
        {
            get => theRadio?.ActiveSlice?.RXAnt ?? "ANT1";
            set
            {
                var s = theRadio?.ActiveSlice;
                if (s != null) s.RXAnt = value;
            }
        }

        /// <summary>Current TX antenna name for active slice.</summary>
        public string TXAntennaName
        {
            get => theRadio?.ActiveSlice?.TXAnt ?? "ANT1";
            set
            {
                var s = theRadio?.ActiveSlice;
                if (s != null) s.TXAnt = value;
            }
        }

        /// <summary>Available RX antenna list from the active slice. Dynamic per radio model.</summary>
        public List<string> RXAntennaList =>
            theRadio?.ActiveSlice?.RXAntList?.ToList() ?? new List<string> { "ANT1", "ANT2" };

        /// <summary>Available TX antenna list from the active slice. Dynamic per radio model.</summary>
        public List<string> TXAntennaList =>
            theRadio?.ActiveSlice?.TXAntList?.ToList() ?? new List<string> { "ANT1", "ANT2" };

        #endregion

        #region Transverter (XVTR) Power — QB Track I

        // Transverter definitions reported by the radio ("sub xvtr all" is part
        // of the FlexLib connect sequence, so XvtrAdded/XvtrRemoved fire without
        // any extra subscription). Radio._xvtrs is private with no public list
        // accessor, so we mirror it here via the public events.
        private readonly List<Xvtr> myXvtrs = new List<Xvtr>();

        private void xvtrAdded(Xvtr xvtr)
        {
            lock (myXvtrs)
            {
                if (!myXvtrs.Contains(xvtr)) myXvtrs.Add(xvtr);
            }
            Tracing.TraceLine($"xvtrAdded: index={xvtr.Index} name={xvtr.Name} rf={xvtr.RFFreq}", TraceLevel.Info);
        }

        private void xvtrRemoved(Xvtr xvtr)
        {
            lock (myXvtrs)
            {
                myXvtrs.Remove(xvtr);
            }
            Tracing.TraceLine($"xvtrRemoved: index={xvtr.Index} name={xvtr.Name}", TraceLevel.Info);
        }

        /// <summary>
        /// True when the active slice's TX antenna is the transverter port.
        /// Power surfaces switch from watts to dBm drive in this state — mixer
        /// overdrive is the classic transverter killer, and the radio's own
        /// design puts fine drive control (hundredths of a dB) only here.
        /// </summary>
        public bool TXAntennaIsTransverter =>
            string.Equals(TXAntennaName, "XVTR", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The transverter definition covering the active slice's frequency, or
        /// null. Selection: among valid XVTRs whose RF start frequency is at or
        /// below the slice frequency, the highest start wins (an XVTR band has a
        /// start but no reported width). Falls back to the only defined XVTR
        /// when exactly one exists.
        /// </summary>
        public Xvtr ActiveXvtr
        {
            get
            {
                double freqMHz = theRadio?.ActiveSlice?.Freq ?? 0.0;
                lock (myXvtrs)
                {
                    if (myXvtrs.Count == 0) return null;
                    Xvtr best = null;
                    foreach (var x in myXvtrs)
                    {
                        if (!x.Valid) continue;
                        if (x.RFFreq > freqMHz + 0.000001) continue;
                        if (best == null || x.RFFreq > best.RFFreq) best = x;
                    }
                    if (best != null) return best;
                    // No band match — a single defined XVTR is unambiguous.
                    return (myXvtrs.Count == 1) ? myXvtrs[0] : null;
                }
            }
        }

        /// <summary>True when the TX antenna is the XVTR port AND we can resolve which transverter it drives.</summary>
        public bool XvtrPowerAvailable => TXAntennaIsTransverter && ActiveXvtr != null;

        /// <summary>Name of the active transverter definition ("2M", "70CM"...), empty if none.</summary>
        public string ActiveXvtrName => ActiveXvtr?.Name ?? string.Empty;

        // Drive is carried in hundredths of a dBm (centi-dBm) so integer-based
        // UI controls can adjust it; Xvtr.MaxPower itself is a double in dBm.
        public const int XvtrDriveMinCentiDbm = -1000;         // -10.00 dBm, FlexLib floor
        public const int XvtrDriveIncrementCentiDbm = 10;      // 0.10 dB per step

        /// <summary>
        /// Upper drive limit in centi-dBm, mirroring the FlexLib Xvtr.MaxPower
        /// clamp: IF below 80 MHz allows +10 dBm on 6400/6600 (else +15);
        /// IF at or above 80 MHz allows +8 dBm. The vendor setter clamps
        /// again, so this is a UI bound, not the enforcement point.
        /// </summary>
        public int XvtrDriveMaxCentiDbm
        {
            get
            {
                var x = ActiveXvtr;
                if (x == null) return 1000;
                string model = theRadio?.Model ?? string.Empty;
                double limit;
                if (x.IFFreq < 80.0)
                {
                    limit = (model == "FLEX-6400M" || model == "FLEX-6400"
                          || model == "FLEX-6600M" || model == "FLEX-6600") ? 10.0 : 15.0;
                }
                else
                {
                    limit = 8.0;
                }
                return (int)Math.Round(limit * 100.0);
            }
        }

        /// <summary>
        /// Transverter drive power in centi-dBm (e.g. 550 = 5.50 dBm). Reads and
        /// writes the active transverter's MaxPower. No-op when no transverter
        /// is resolvable.
        /// </summary>
        public int XvtrDrivePowerCentiDbm
        {
            get
            {
                var x = ActiveXvtr;
                return (x == null) ? 0 : (int)Math.Round(x.MaxPower * 100.0);
            }
            set
            {
                var x = ActiveXvtr;
                if (x == null) return;
                double dbm = value / 100.0;
                q.Enqueue((FunctionDel)(() => { x.MaxPower = dbm; }), "XvtrDrive");
            }
        }

        #endregion

        // A "VFO" is a POSITION in mySlices. The list is kept sorted by radio
        // slice index (see sliceAdded), so position order always equals letter
        // order. Positions still shift when the roster changes — never store a
        // VFO across an add/remove; re-derive it from the Slice object (the
        // letter is the identity). Letter-addressed entry points must resolve
        // through SliceIndexToVFO / LetterToVFO, never "letter - 'A'"
        // arithmetic on positions. (QB Track J)
        internal Slice VFOToSlice(int vfo)
        {
            Slice rv;
            lock (mySlices)
            {
                rv = (ValidVFO(vfo)) ? mySlices[vfo] : null;
            }
            if (rv == null && vfo != noVFO)
                Tracing.TraceLine($"VFOToSlice:null for vfo={vfo} slices={MyNumSlices}", TraceLevel.Warning);
            return rv;
        }

        internal int SliceToVFO(Slice s)
        {
            int rv = noVFO;
            lock (mySlices)
            {
                for (int i = 0; i < mySlices.Count; i++)
                {
                    if (s.Index == mySlices[i].Index)
                    {
                        rv = i;
                        break;
                    }
                }
            }

            if (rv == noVFO)
            {
                Tracing.TraceLine("SliceToVFO:Error", TraceLevel.Error);
            }
            return rv;
        }

        /// <summary>
        /// Resolve a RADIO slice index (0 = A, 1 = B, ...) to the VFO position
        /// of OUR slice carrying that index, or -1 if this client does not own
        /// a slice with that index. The letter is the identity: radio index n
        /// is letter ('A' + n) regardless of creation order, so this — not
        /// positional arithmetic — is the correct door for letter-addressed
        /// selection. Unlike SliceToVFO, absence is a normal answer here and
        /// is not traced as an error.
        /// </summary>
        public int SliceIndexToVFO(int radioIndex)
        {
            lock (mySlices)
            {
                for (int i = 0; i < mySlices.Count; i++)
                {
                    if (mySlices[i].Index == radioIndex) return i;
                }
            }
            return noVFO;
        }

        /// <summary>
        /// Resolve a slice letter ('A'-'H', case-insensitive) to the VFO
        /// position of our slice with that letter, or -1 if we don't own it.
        /// </summary>
        public int LetterToVFO(char letter)
        {
            return SliceIndexToVFO(char.ToUpperInvariant(letter) - 'A');
        }

        /// <summary>
        /// Radio slice index (identity; 0 = A) of the slice at a VFO position,
        /// or -1 if the position is invalid. The inverse of SliceIndexToVFO —
        /// use this to hold a durable reference to a slice across roster
        /// changes, since positions shift and radio indices don't.
        /// </summary>
        public int VFOToSliceIndex(int vfo)
        {
            Slice s = VFOToSlice(vfo);
            return (s != null) ? s.Index : -1;
        }

        /// <summary>
        /// True if a slice with this radio index exists on the radio but
        /// belongs to another client. Lets letter-addressed selection speak an
        /// honest "in use by another station" instead of "not created".
        /// </summary>
        public bool SliceIndexOwnedByOther(int radioIndex)
        {
            if (theRadio == null) return false;
            foreach (Slice s in theRadio.SliceList)
            {
                if (s.Index == radioIndex) return !myClient(s.ClientHandle);
            }
            return false;
        }

        /// <summary>
        /// The next VFO, wraps around.
        /// </summary>
        /// <param name="v">current VFO</param>
        public int NextVFO(int v)
        {
            return (MyNumSlices == 0) ? 0 : (v + 1) % MyNumSlices;
        }

        /// <summary>
        /// The previous VFO, wraps around.
        /// </summary>
        /// <param name="v">current VFO</param>
        public int PriorVFO(int v)
        {
            return (v > 0) ? v - 1 : MyNumSlices - 1;
        }

        // Region rig properties
        #region Rig properties
        private const int noVFO = -1;
        /// <summary>
        /// true if the VFO value is good.
        /// </summary>
        /// <param name="vfo">the VFO</param>
        public bool ValidVFO(int vfo)
        {
            return ((vfo >= 0) & (vfo < MyNumSlices));
        }

        // Note that VFOToSlice(RXVFO) is the ActiveSlice.
        internal int _RXVFO = noVFO;
        public int RXVFO
        {
            get { return _RXVFO; }
            set
            {
                if (_RXVFO != value)
                {
                    int old = _RXVFO;
                    _RXVFO = value;
                    Tracing.TraceLine($"RXVFO:{old}→{value} valid={ValidVFO(value)} slices={MyNumSlices}", TraceLevel.Info);
                    if (ValidVFO(value))
                    {
                        // Capture the slice at set time (identity): if the
                        // roster shifts before the queue runs, the command
                        // still lands on the slice the user addressed, not on
                        // whatever occupies that position later. (QB Track J)
                        Slice s = VFOToSlice(value);
                        if (s != null) q.Enqueue((FunctionDel)(() => { s.Active = true; }), "Active");
                        //await(() => { return (_RXVFO == value); }, 1000);
                    }
                    // else we don't reset it.
                }
            }
        }

        internal int _TXVFO = noVFO;
        public int TXVFO
        {
            get { return _TXVFO; }
            set
            {
                if (_TXVFO != value)
                {
                    _TXVFO = value;
                    if (ValidVFO(value))
                    {
                        // Capture at set time — see RXVFO. The old raw
                        // mySlices[value] inside the lambda resolved the
                        // position at queue-execution time, which could be a
                        // different slice (or out of range) after churn.
                        Slice s = VFOToSlice(value);
                        if (s != null) q.Enqueue((FunctionDel)(() => { s.IsTransmitSlice = true; }), "IsTransmitSlice");
                        //await(() => { return (_TXVFO == value); }, 1000);
                    }
                }
            }
        }

        /// <summary>
        /// True when some slice currently carries the transmit designation.
        /// </summary>
        public bool HasTransmitSlice => ValidVFO(TXVFO);

        /// <summary>
        /// QB Track I — clear the transmit designation entirely: no slice keys
        /// the radio until one is designated again. A legitimate radio state
        /// (slice set N tx=0 with no successor); doubles as a soft TX lockout.
        /// </summary>
        public void ClearTransmitSlice()
        {
            int vfo = _TXVFO;
            var s = VFOToSlice(vfo);
            if (s != null)
                q.Enqueue((FunctionDel)(() => { s.IsTransmitSlice = false; }), "ClearTransmitSlice");
            _TXVFO = noVFO;
            Tracing.TraceLine($"ClearTransmitSlice: was vfo={vfo}", TraceLevel.Info);
        }

        /// <summary>
        /// Get/set the current VFO in use.
        /// </summary>
        public int CurVFO
        {
            get
            {
                return (Transmit) ? TXVFO : RXVFO;
            }
            set
            {
                if (Transmit)
                {
                    TXVFO = value;
                }
                else
                {
                    RXVFO = value;
                }
            }
        }

        /// <summary>
        /// Get the VFO's (slice's) audio
        /// </summary>
        /// <param name="v">VFO or slice</param>
        /// <returns>true if on</returns>
        public bool GetVFOAudio(int v)
        {
            bool rv;
            lock (mySlices)
            {
                Slice s = VFOToSlice(v);
                rv = !((s != null) ? s.Mute : true);
            }
            return rv;
        }

        /// <summary>
        /// Turn audio on/off.
        /// </summary>
        /// <param name="v">VFO or slice id</param>
        /// <param name="on">true for on</param>
        public void SetVFOAudio(int v, bool on)
        {
            Tracing.TraceLine("SetVFOAudio:" + v + ' ' + on.ToString(), TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                lock (mySlices)
                {
                    Slice s = VFOToSlice(v);
                    if (s != null) s.Mute = !on;
                }
            }), "Mute");
        }

        /// <summary>
        /// get the audio pan value
        /// </summary>
        /// <param name="v">VFO or slice</param>
        public int GetVFOPan(int v)
        {
            int rv = (MaxPan - MinPan) / 2;
            lock (mySlices)
            {
                Slice s = VFOToSlice(v);
                if (s != null) rv = s.AudioPan;
            }
            return rv;
        }

        public const int MinPan = 0;
        public const int MaxPan = 100;
        public const int PanIncrement = 10;
        /// <summary>
        /// Adjust the slice audio panning
        /// </summary>
        /// <param name="v">VFO or slice</param>
        /// <param name="pan">pan value</param>
        public void SetVFOPan(int v, int pan)
        {
            Tracing.TraceLine("SetVFOPan:" + v + ' ' + pan, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                lock (mySlices)
                {
                    Slice s = VFOToSlice(v);
                    if (s != null) s.AudioPan = pan;
                }
            }), "AudioPan");
        }

        /// <summary>
        /// get the audio gain value
        /// </summary>
        /// <param name="v">VFO or slice</param>
        public int GetVFOGain(int v)
        {
            int rv = (MaxGain - MinGain) / 2;
            lock (mySlices)
            {
                Slice s = VFOToSlice(v);
                if (s != null) rv = s.AudioGain;
            }
            return rv;
        }

        public const int MinGain = 0;
        public const int MaxGain = 100;
        public const int GainIncrement = 10;
        /// <summary>
        /// Adjust the slice audio gain
        /// </summary>
        /// <param name="v">VFO or slice</param>
        /// <param name="gain">gain value</param>
        public void SetVFOGain(int v, int gain)
        {
            Tracing.TraceLine("SetVFOGain:" + v + ' ' + gain, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                lock (mySlices)
                {
                    Slice s = VFOToSlice(v);
                    if (s != null) s.AudioGain = gain;
                }
            }), "AudioGain");
        }

        // Can't add/remove VFOs during this.
        public void CopyVFO(int inv, int outv)
        {
            if (Transmit)
            {
                Tracing.TraceLine("CopyVFO:can't be transmitting", TraceLevel.Error);
                return;
            }
            if ((!ValidVFO(inv) | !ValidVFO(outv)) |
                (inv == outv))
            {
                Tracing.TraceLine("CopyVFO:bad VFO:" + inv.ToString() + " " + outv.ToString(), TraceLevel.Error);
                return;
            }

            Tracing.TraceLine("CopyVFO:" + inv.ToString() + " " + outv.ToString(), TraceLevel.Info);
            Slice inSlice = VFOToSlice(inv);
            Slice outSlice = VFOToSlice(outv);
            q.Enqueue((FunctionDel)null, "slice copy start");
            q.Enqueue((FunctionDel)(() => { outSlice.Freq = inSlice.Freq; }));
            q.Enqueue((FunctionDel)(() => { outSlice.DemodMode = inSlice.DemodMode; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AutoPan = inSlice.AutoPan; }));
            q.Enqueue((FunctionDel)(() => { outSlice.RTTYMark = inSlice.RTTYMark; }));
            q.Enqueue((FunctionDel)(() => { outSlice.RTTYShift = inSlice.RTTYShift; }));
            q.Enqueue((FunctionDel)(() => { outSlice.DIGLOffset = inSlice.DIGLOffset; }));
            q.Enqueue((FunctionDel)(() => { outSlice.DIGUOffset = inSlice.DIGUOffset; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FilterHigh = inSlice.FilterHigh; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FilterLow = inSlice.FilterLow; }));
            q.Enqueue((FunctionDel)(() => { outSlice.ANFOn = inSlice.ANFOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.APFOn = inSlice.APFOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.ANFLevel = inSlice.ANFLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.APFLevel = inSlice.APFLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.WNBOn = inSlice.WNBOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.WNBLevel = inSlice.WNBLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NBOn = inSlice.NBOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NBLevel = inSlice.NBLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NROn = inSlice.NROn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NRLevel = inSlice.NRLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AGCMode = inSlice.AGCMode; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AGCOffLevel = inSlice.AGCOffLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AGCThreshold = inSlice.AGCThreshold; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMDeviation = inSlice.FMDeviation; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMRepeaterOffsetFreq = inSlice.FMRepeaterOffsetFreq; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMToneValue = inSlice.FMToneValue; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMTX1750 = inSlice.FMTX1750; }));
            q.Enqueue((FunctionDel)(() => { outSlice.RepeaterOffsetDirection = inSlice.RepeaterOffsetDirection; }));

            List<Slice> sList = new List<Slice>();
            sList.Add(inSlice);
            sList.Add(outSlice);
            q.Enqueue((FunctionDel)(() =>
            {
                FilterObj.RXFreqChange(sList);
            }));
            q.Enqueue((FunctionDel)null, "slice copy done");
        }

        internal double LongFreqToLibFreq(ulong u)
        {
            return (double)u / 1000000d;
        }

        internal ulong LibFreqtoLong(double f)
        {
            return (ulong)(f * 1000000d);
        }

        private ulong _RXFrequency;
        public ulong RXFrequency
        {
            get
            {
                return _RXFrequency;
            }
            set
            {
                _RXFrequency = value;
                if (!ValidVFO(RXVFO))
                {
                    Tracing.TraceLine("RXFrequency: no valid RX slice", TraceLevel.Warning);
                    return;
                }
                var slice = VFOToSlice(RXVFO);
                if (slice == null)
                {
                    Tracing.TraceLine("RXFrequency: RX slice missing", TraceLevel.Warning);
                    return;
                }
                double freq = LongFreqToLibFreq(value);
                q.Enqueue((FunctionDel)(() =>
                {
                    var s = VFOToSlice(RXVFO);
                    if (s != null) s.Freq = freq;
                }), "RXFreq");
            }
        }

        private ulong _TXFrequency;
        public ulong TXFrequency
        {
            get
            {
                return _TXFrequency;
            }
            set
            {
                _TXFrequency = value;
                if (!ValidVFO(TXVFO))
                {
                    Tracing.TraceLine("TXFrequency: no valid TX slice", TraceLevel.Warning);
                    return;
                }
                var slice = VFOToSlice(TXVFO);
                if (slice == null)
                {
                    Tracing.TraceLine("TXFrequency: TX slice missing", TraceLevel.Warning);
                    return;
                }
                double freq = LongFreqToLibFreq(value);
                q.Enqueue((FunctionDel)(() =>
                {
                    var s = VFOToSlice(TXVFO);
                    if (s != null) s.Freq = freq;
                }), "TXFreq");
            }
        }

        /// <summary>
        /// current frequency
        /// </summary>
        public ulong Frequency
        {
            get { return (Transmit) ? TXFrequency : RXFrequency; }
            set
            {
                // Don't set if transmitting.
                if (Transmit)
                {
                    Tracing.TraceLine("Frequency:can't set it now", TraceLevel.Error);
                    return;
                }
                RXFrequency = value;
            }
        }

        /// <summary>
        /// showing XMIT frequency when split.
        /// </summary>
        public bool ShowingXmitFrequency
        {
            get; set;
        }

        /// <summary>
        /// Set frequency according to ShowingXmitFrequency.
        /// </summary>
        public ulong VirtualRXFrequency
        {
            get
            {
                return (ShowingXmitFrequency) ? TXFrequency : RXFrequency;
            }
            set
            {
                if (Transmit) return; // disallow set
                if (ShowingXmitFrequency) TXFrequency = value;
                else RXFrequency = value;
            }
        }

        private string _RXMode = "";
        /// <summary>
        /// RX mode
        /// </summary>
        public string RXMode
        {
            get
            {
                return _RXMode;
            }
            set
            {
                // QB Track J: target the slice the app has been announcing
                // (RXVFO → our slice), not theRadio.ActiveSlice. After slice
                // churn the two could diverge, and ActiveSlice may not even be
                // ours under MultiFlex — this is how the Slice menu's Mode
                // change landed on the wrong slice in Noel's 2026-08-07
                // session. Capture the slice at set time so the queued command
                // can't retarget either.
                Slice s = VFOToSlice(RXVFO);
                if (s == null)
                {
                    Slice act = theRadio?.ActiveSlice;
                    if ((act != null) && myClient(act.ClientHandle)) s = act;
                }
                if (s != null) q.Enqueue((FunctionDel)(() => { s.DemodMode = value; }), "RXDemodMode");
            }
        }

        private string _TXMode = "";
        /// <summary>
        /// TX mode
        /// </summary>
        public string TXMode
        {
            get
            {
                return _TXMode;
            }
            set
            {
                // Capture the TX slice at set time (identity) — resolving
                // TXVFO inside the queued lambda could land on a different
                // slice after roster churn. (QB Track J)
                Slice s = VFOToSlice(TXVFO);
                if (s != null)
                {
                    q.Enqueue((FunctionDel)(() => { s.DemodMode = value; }), "TXDemodMode");
                }
            }
        }

        /// <summary>
        /// current mode
        /// </summary>
        public string Mode
        {
            get { return (string)((Transmit) ? TXMode : RXMode); }
            set
            {
                // Can't set during transmit.
                if (Transmit) return;
                RXMode = value;
            }
        }

        public int FilterLow
        {
            get
            {
                // Null-conditional matters: the menu bar rebuilds on slice-count
                // changes, and a network drop fires those DURING teardown when
                // ActiveSlice is already gone. Unguarded, this getter crash-looped
                // on every slice event of a dead connection (2026-08-05, four
                // crash reports in 49 seconds when a VPN came up mid-session).
                return theRadio?.ActiveSlice?.FilterLow ?? 0;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterLow = value; }), "FilterLow");
            }
        }

        public int FilterHigh
        {
            get
            {
                return theRadio?.ActiveSlice?.FilterHigh ?? 0;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterHigh = value; }), "FilterHigh");
            }
        }

        /// <summary>
        /// Set both filter edges atomically via Slice.UpdateFilter().
        /// Avoids the race condition of setting FilterLow and FilterHigh separately
        /// through the command queue, where FlexLib clamps each edge against the
        /// other's stale value.
        /// </summary>
        public void SetFilter(int low, int high)
        {
            if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.UpdateFilter(low, high); }), "Filter");
        }

#if zero
        // TXAntenna must be set first.
        public bool RXAntenna
        {
            get
            {
                return (theRadio.ActiveSlice.RXAnt != VFOToSlice(RXVFO).TXAnt);
            }
            set
            {
                // Use the other antenna, 0 or 1, if true.
                int ant = (value) ? (TXAntenna + 1) % 2 : TXAntenna;
                foreach (Slice s in mySlices)
                {
                    q.Enqueue((FunctionDel)(() => { s.RXAnt = theRadio.RXAntList[ant]; }), "RXAnt");
                }
    }
}

        /// <summary>
        /// Set both the TX and RX antenna values.
        /// </summary>
        public int TXAntenna
        {
            get
            {
                int rv = -1; // Invalid if not found.
                int max = Math.Min(theRadio.RXAntList.Length - 1, 1);
                for (int id = 0; id <= max; id++)
                {
                    if (theRadio.ActiveSlice.TXAnt == theRadio.RXAntList[id])
                    {
                        rv = id;
                        break;
                    }
                }
                return rv;
            }
            set
            {
                Tracing.TraceLine("TXAntenna:" + value.ToString(), TraceLevel.Info);
                if (value < theRadio.RXAntList.Length)
                {
                    lock (mySlices)
                    {
                        foreach (Slice s in mySlices)
                        {
                            q.Enqueue((FunctionDel)(() => { s.TXAnt = theRadio.RXAntList[value]; }), "TXAnt");
                            q.Enqueue((FunctionDel)(() => { s.RXAnt = theRadio.RXAntList[value]; }), "RXAnt");
                        }
                    }
                }
            }
        }
#endif

        //
        internal const string RxAntDefault = null;
        //private string _RXAntenna;
        internal string RXAntenna
        {
            get
            {
                return theRadio?.ActiveSlice?.RXAnt ?? "";
            }
            set
            {
                if (theRadio?.ActiveSlice != null)
                {
                    if (value == RxAntDefault)
                    {
                        // same as TXAntenna
                        q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RXAnt = TXAntenna; }), "RXAnt");
                    }
                    else
                    {
                        q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RXAnt = value; }), "RXAnt");
                    }
                }
            }
        }

        internal string TXAntenna
        {
            get
            {
                return theRadio?.ActiveSlice?.TXAnt ?? "";
            }
            set
            {
                if (theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.TXAnt = value; }), "TXAnt");
                }
            }
        }

        /// <summary>
        /// Type of the Flex tuner in use
        /// </summary>
        public enum FlexTunerTypes
        {
            none,
            manual,
            auto,
        }

        private FlexTunerTypes _FlexTunerType;
        public FlexTunerTypes FlexTunerType
        {
            get { return _FlexTunerType; }
            set
            {
                // Set by the user only.
                Tracing.TraceLine("FlexTunerType:" + value.ToString() + ' ' +
                    _FlexTunerType.ToString() + ' ' + _FlexTunerOn.ToString(), TraceLevel.Info);
                if (value == _FlexTunerType) return;
                // Can't change while tuning.
                if (!_FlexTunerOn)
                {
                    if (value == FlexTunerTypes.auto) _FlexTunerType = value;
                    else
                    {
                        setFlexTunerTypeNotAuto();
                        // We were in autoTune mode.  Need to bypass.
                        theRadio.ATUTuneBypass();
                    }
                }
            }
        }

        protected void setFlexTunerTypeNotAuto()
        {
            _FlexTunerType = (MyCaps.HasCap(RigCaps.Caps.ManualATGet)) ?
                FlexTunerTypes.manual : FlexTunerTypes.none;
            Tracing.TraceLine("setFlexTunerTypeNotAuto:" + _FlexTunerType.ToString(), TraceLevel.Info);
        }

        private bool _FlexTunerOn;
        public bool FlexTunerOn
        {
            get { return _FlexTunerOn; }
            set
            {
                // set internally or by the user.
                Tracing.TraceLine("FlexTunerOn:" + value.ToString() + ' ' +
                    _FlexTunerOn.ToString() + ' ' + _FlexTunerType.ToString(), TraceLevel.Info);
                if (value == _FlexTunerOn) return;
                switch (_FlexTunerType)
                {
                    case FlexTunerTypes.manual:
                        {
                            // Raise status if turning off so we can report the SWR.
                            if (!value)
                            {
                                float highSWR;
                                // Look for minimum SWR.
                                do
                                {
                                    highSWR = _SWR;
                                    Thread.Sleep(100);
                                } while (highSWR > _SWR);
                                // Report status.
                                ATUTuneStatus stat = ATUTuneStatus.OK;
                                RaiseFlexAntTuneStartStop(new FlexAntTunerArg
                                    (FlexTunerType, stat, highSWR));
                            }
                            q.Enqueue((FunctionDel)(() => { theRadio.TXTune = value; }), "TXTune");
                        }
                        break;
                    case FlexTunerTypes.auto:
                        {
                            // Normally tuning stops automatically when finished.
                            q.Enqueue((FunctionDel)(() => { Transmit = value; }), "Transmit");
                            if (value)
                            {
                                q.Enqueue((FunctionDel)(() => { theRadio.ATUTuneStart(); }), "ATUTuneStart");
                            }
                        }
                        break;
                }
                _FlexTunerOn = value;
            }
        }

        /// <summary>
        /// Delegate to show the ATU Memories dialog. Wired externally.
        /// Sprint 10: Replaces direct FlexATUMemories form creation.
        /// </summary>
        public Action ShowATUMemoriesDialog { get; set; }

        public void AntennaTunerMemories()
        {
            ShowATUMemoriesDialog?.Invoke();
        }

        /// <summary>
        /// Delegate to show the Radio Info dialog. Returns nothing.
        /// Sprint 11: Replaces direct FlexInfo form creation.
        /// Parameter: tab index (0=General, 1=FeatureAvailability).
        /// QB Track L (2026-08-07): finally wired — MainWindow.OnRadioStarted
        /// assigns it (same pattern as ShowMemoriesDialog), backed by the
        /// Radio Info support members below.
        /// </summary>
        public Action<int> ShowRadioInfoDialog { get; set; }

        // ────────────────────────────────────────────────────────────────
        //  Radio Info dialog support (QB Track L, 2026-08-07).
        //  The WPF RadioInfoDialog (Sprint 11) needs radio-side accessors
        //  that only existed inside the deleted WinForms FlexInfo form —
        //  callsign, front-panel display mode, license refresh, and the
        //  feature-availability report. They live here because theRadio is
        //  internal to this assembly; the UI layer gets thin callbacks.
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The callsign stored on the radio itself (shown on M-model front
        /// panels and in discovery). Empty when no radio is connected;
        /// setting with no radio is a traced no-op, never a throw.
        /// </summary>
        public string RadioCallsign
        {
            get { return theRadio?.Callsign ?? string.Empty; }
            set
            {
                var r = theRadio;
                if (r == null)
                {
                    Tracing.TraceLine("RadioCallsign set ignored: no radio", TraceLevel.Warning);
                    return;
                }
                if (r.Callsign != value) r.Callsign = value;
            }
        }

        /// <summary>
        /// The front-panel display (screensaver) mode choices the radio
        /// understands, as displayable names. Model-independent — FlexLib
        /// defines one enum for the whole line.
        /// </summary>
        public string[] FrontPanelDisplayModes =>
            Enum.GetNames(typeof(ScreensaverMode));

        /// <summary>
        /// The current front-panel display mode by name (one of
        /// <see cref="FrontPanelDisplayModes"/>). Empty when no radio is
        /// connected; setting an unknown name or setting with no radio is a
        /// traced no-op.
        /// </summary>
        public string FrontPanelDisplayMode
        {
            get { return theRadio?.Screensaver.ToString() ?? string.Empty; }
            set
            {
                var r = theRadio;
                if (r == null)
                {
                    Tracing.TraceLine("FrontPanelDisplayMode set ignored: no radio", TraceLevel.Warning);
                    return;
                }
                if (Enum.TryParse<ScreensaverMode>(value, out var mode))
                {
                    if (r.Screensaver != mode) r.Screensaver = mode;
                }
                else
                {
                    Tracing.TraceLine($"FrontPanelDisplayMode set ignored: unknown mode '{value}'", TraceLevel.Warning);
                }
            }
        }

        /// <summary>
        /// Ask the radio to re-read its feature-license state. Failures are
        /// swallowed by design (the old FlexInfo behavior): the radio reports
        /// any valid status updates on its own, and a refresh that can't run
        /// simply leaves the current answer standing.
        /// </summary>
        public void RefreshLicenseState()
        {
            try { theRadio?.RefreshLicenseState(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RefreshLicenseState: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>
        /// The feature-availability report: one plain line per feature saying
        /// enabled / disabled / unavailable / unsubscribed / pending and WHY —
        /// the "explain why radio features are unavailable" surface. Ported
        /// from the deleted WinForms FlexInfo form (Sprint 11 → QB Track L).
        /// </summary>
        public string BuildFeatureAvailabilityText()
        {
            var radio = theRadio;
            if (radio == null) return "Radio: unavailable - radio not ready";

            var lines = new List<string>();
            lines.Add(BuildDiversityStatus(radio));
            lines.Add(BuildEscStatus(radio));
            lines.AddRange(BuildNoiseReductionStatuses(radio));
            lines.AddRange(BuildAutoNotchStatuses(radio));
            lines.Add(BuildCwAutotuneStatus());
            return string.Join(Environment.NewLine, lines);
        }

        private string BuildDiversityStatus(Radio radio)
        {
            string status;
            string reason = string.Empty;

            bool hasSlice = HasActiveSlice;
            bool hasHardware = DiversityHardwareSupported;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatDivEsc;
            bool licenseReported = licenseFeature != null;
            bool licenseEnabled = licenseFeature?.FeatureEnabled == true;
            bool hasAntennas = (radio.RXAntList?.Length ?? 0) >= 2;
            bool hasSlices = radio.AvailableSlices >= 2;

            if (!hasHardware) { status = "unavailable"; reason = "model lacks diversity support"; }
            else if (!licenseReported) { status = "pending"; reason = "license status pending"; }
            else if (!licenseEnabled) { status = "unsubscribed"; reason = licenseFeature.FeatureGatedMessage ?? "license disabled"; }
            else if (!hasSlice) { status = "unavailable"; reason = "select a slice"; }
            else if (!hasAntennas) { status = "unavailable"; reason = "need two RX antennas"; }
            else if (!hasSlices) { status = "unavailable"; reason = "need two slices"; }
            else
            {
                status = DiversityOn ? "enabled" : "disabled";
                if (!DiversityOn) reason = "diversity ready";
            }

            return BuildFeatureLine("Diversity", status, reason);
        }

        private string BuildEscStatus(Radio radio)
        {
            string status;
            string reason = string.Empty;

            bool hasSlice = HasActiveSlice;
            bool hasHardware = DiversityHardwareSupported;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatDivEsc;
            bool licenseReported = licenseFeature != null;
            bool licenseEnabled = licenseFeature?.FeatureEnabled == true;
            bool hasAntennas = (radio.RXAntList?.Length ?? 0) >= 2;
            bool hasSlices = radio.AvailableSlices >= 2;

            if (!hasHardware) { status = "unavailable"; reason = "model lacks diversity support"; }
            else if (!licenseReported) { status = "pending"; reason = "license status pending"; }
            else if (!licenseEnabled) { status = "unsubscribed"; reason = licenseFeature.FeatureGatedMessage ?? "license disabled"; }
            else if (!hasSlice) { status = "unavailable"; reason = "select a slice"; }
            else if (!hasAntennas) { status = "unavailable"; reason = "need two RX antennas"; }
            else if (!hasSlices) { status = "unavailable"; reason = "need two slices"; }
            else if (!DiversityOn) { status = "disabled"; reason = "enable diversity to use ESC"; }
            else
            {
                var escSlice = GetEscSlice(radio.ActiveSlice);
                if (escSlice == null) { status = "unavailable"; reason = "select a slice"; }
                else if (escSlice.ESCEnabled) { status = "enabled"; }
                else { status = "disabled"; reason = "ESC disabled"; }
            }

            return BuildFeatureLine("ESC", status, reason);
        }

        private static Slice GetEscSlice(Slice active)
        {
            if (active == null) return null;
            if (active.DiversityChild && active.DiversitySlicePartner != null)
                return active.DiversitySlicePartner;
            return active;
        }

        private List<string> BuildNoiseReductionStatuses(Radio radio)
        {
            var lines = new List<string>();

            bool hasSlice = HasActiveSlice;
            bool licenseReported = NoiseReductionLicenseReported;
            bool licenseEnabled = NoiseReductionLicensed;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatNoiseReduction;

            string mode = (Mode ?? string.Empty).ToLowerInvariant();
            bool cwOrFm = mode.StartsWith("cw") || mode.Contains("fm");
            bool nrModeAllowed = !cwOrFm;

            lines.Add(BuildBaseNoiseStatus("Noise Reduction (Basic NR)",
                NoiseReduction == OffOnValues.on,
                hasSlice, nrModeAllowed, "not available in CW or FM modes"));

            bool rnnSupported = IsRnnModel(radio);
            lines.Add(BuildAdvancedNoiseStatus("Noise Reduction (RNN)",
                NeuralNoiseReduction == OffOnValues.on,
                rnnSupported, rnnSupported ? "" : "requires 8000-series radio",
                hasSlice, nrModeAllowed, "not available in CW or FM modes",
                licenseFeature, licenseReported, licenseEnabled));

            lines.Add(BuildAdvancedNoiseStatus("Noise Reduction (NRF)",
                NoiseReductionFilter == OffOnValues.on,
                true, "",
                hasSlice, nrModeAllowed, "not available in CW or FM modes",
                licenseFeature, licenseReported, licenseEnabled));

            lines.Add(BuildAdvancedNoiseStatus("Noise Reduction (NRS)",
                SpectralNoiseReduction == OffOnValues.on,
                true, "",
                hasSlice, nrModeAllowed, "not available in CW or FM modes",
                licenseFeature, licenseReported, licenseEnabled));

            lines.Add(BuildAdvancedNoiseStatus("Noise Reduction (NRL)",
                NoiseReductionLegacy == OffOnValues.on,
                true, "",
                hasSlice, nrModeAllowed, "not available in CW or FM modes",
                licenseFeature, licenseReported, licenseEnabled));

            return lines;
        }

        private List<string> BuildAutoNotchStatuses(Radio radio)
        {
            var lines = new List<string>();

            bool hasSlice = HasActiveSlice;
            bool licenseReported = NoiseReductionLicenseReported;
            bool licenseEnabled = NoiseReductionLicensed;
            var licenseFeature = radio.FeatureLicense?.LicenseFeatNoiseReduction;

            string mode = (Mode ?? string.Empty).ToLowerInvariant();
            bool fmMode = mode.Contains("fm");
            bool anfModeAllowed = !fmMode;

            lines.Add(BuildBaseNoiseStatus("Auto Notch (Basic ANF)",
                ANF == OffOnValues.on,
                hasSlice, anfModeAllowed, "not available in FM mode"));

            lines.Add(BuildAdvancedNoiseStatus("Auto Notch (ANFT)",
                AutoNotchFFT == OffOnValues.on,
                true, "",
                hasSlice, anfModeAllowed, "not available in FM mode",
                licenseFeature, licenseReported, licenseEnabled));

            lines.Add(BuildAdvancedNoiseStatus("Auto Notch (ANFL)",
                AutoNotchLegacy == OffOnValues.on,
                true, "",
                hasSlice, anfModeAllowed, "not available in FM mode",
                licenseFeature, licenseReported, licenseEnabled));

            return lines;
        }

        private string BuildCwAutotuneStatus()
        {
            string status;
            string reason = string.Empty;

            bool hasSlice = HasActiveSlice;
            bool supported = SupportsCwAutotune;
            string mode = (Mode ?? string.Empty).ToLowerInvariant();
            bool cwMode = mode.StartsWith("cw");

            if (!supported) { status = "unavailable"; reason = "not supported on this radio"; }
            else if (!hasSlice) { status = "unavailable"; reason = "select a slice"; }
            else if (!cwMode) { status = "disabled"; reason = "switch to CW mode to use autotune"; }
            else { status = "enabled"; }

            return BuildFeatureLine("CW Autotune", status, reason);
        }

        private static string BuildFeatureLine(string feature, string status, string reason)
        {
            return string.IsNullOrEmpty(reason)
                ? feature + ": " + status
                : feature + ": " + status + " - " + reason;
        }

        private static string BuildBaseNoiseStatus(string feature, bool enabled,
            bool hasSlice, bool modeAllowed, string modeReason)
        {
            if (!hasSlice) return BuildFeatureLine(feature, "unavailable", "select a slice");
            if (!modeAllowed) return BuildFeatureLine(feature, "unavailable", modeReason);
            return BuildFeatureLine(feature, enabled ? "enabled" : "disabled", enabled ? "" : "available");
        }

        private static string BuildAdvancedNoiseStatus(string feature, bool enabled,
            bool modelSupported, string modelReason,
            bool hasSlice, bool modeAllowed, string modeReason,
            Feature licenseFeature, bool licenseReported, bool licenseEnabled)
        {
            if (!modelSupported) return BuildFeatureLine(feature, "unavailable", modelReason);
            if (!licenseReported) return BuildFeatureLine(feature, "pending", "license status pending");
            if (!licenseEnabled) return BuildFeatureLine(feature, "unsubscribed", licenseFeature?.FeatureGatedMessage ?? "license disabled");
            if (!hasSlice) return BuildFeatureLine(feature, "unavailable", "select a slice");
            if (!modeAllowed) return BuildFeatureLine(feature, "unavailable", modeReason);
            return BuildFeatureLine(feature, enabled ? "enabled" : "disabled", enabled ? "" : "available");
        }

        private static bool IsRnnModel(Radio radio)
        {
            var model = radio?.Model ?? string.Empty;
            // 8000 series and Aurora AU-520 (based on 8600) support RNN
            return model.StartsWith("FLEX-8", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("AU-52", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Delegate to show the Memories dialog. Wired externally.
        /// Sprint 11: Replaces direct FlexMemories form creation.
        /// </summary>
        public Action ShowMemoriesDialog { get; set; }

        /// <summary>
        /// Export the radio's profile database to a user-selected file.
        /// Sprint 16 Track C: Wraps FlexDB.Export() for external callers.
        /// </summary>
        public bool ExportProfileDatabase()
        {
            var db = new FlexDB(this);
            return db.Export();
        }

        /// <summary>
        /// Import radio profiles from a user-selected file.
        /// Sprint 20: Wraps FlexDB.Import() for external callers (Modern menu).
        /// </summary>
        public bool ImportProfileDatabase()
        {
            var db = new FlexDB(this);
            return db.Import();
        }

        /// <summary>
        /// Returns a snapshot of all meters currently known to the radio.
        /// Sprint 20: Exposes FlexLib's meter list for the profile report.
        /// </summary>
        public List<Flex.Smoothlake.FlexLib.Meter> GetAllMeters()
        {
            // GetAllMeters removed in FlexLib v4.1.5 — _meters is private now.
            // This method is unused but kept for API compatibility.
            return new List<Flex.Smoothlake.FlexLib.Meter>();
        }

        /// <summary>
        /// Delegate to show the TX Controls dialog. Wired externally.
        /// Sprint 11: Replaces direct TXControls form creation.
        /// </summary>
        public Action ShowTXControlsDialog { get; set; }

        public bool FlexTunerUsingMemoryNow
        {
            get
            {
                return ((_FlexTunerType == FlexTunerTypes.auto) &
                    (theRadio.ATUTuneStatus != ATUTuneStatus.Bypass) &
                    theRadio.ATUMemoriesEnabled & theRadio.ATUUsingMemory);
            }
        }

        /// <summary>
        /// Per-slice mute toggle. True = muted.
        /// </summary>
        public bool SliceMute
        {
            get
            {
                return theRadio?.ActiveSlice?.Mute ?? false;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.Mute = value; }), "SliceMute");
            }
        }

        /// <summary>
        /// True when every slice owned by this client is currently muted.
        /// False if any of my slices is unmuted, or if I have no slices at all.
        /// Used by the Shift+M universal Home key to decide whether the toggle
        /// action is mute-all or unmute-all.
        /// </summary>
        public bool AllMySlicesMuted
        {
            get
            {
                if (theRadio == null) return false;
                bool anyMine = false;
                foreach (var s in theRadio.SliceList)
                {
                    if (myClient(s.ClientHandle))
                    {
                        anyMine = true;
                        if (!s.Mute) return false;
                    }
                }
                return anyMine;
            }
        }

        /// <summary>
        /// Mute or unmute every slice owned by this client at once. Per-slice
        /// mute commands are queued through the same radio command queue as
        /// SliceMute so the radio processes the changes in order without
        /// blocking the UI thread.
        /// </summary>
        public void SetAllMySlicesMute(bool mute)
        {
            if (theRadio == null) return;
            foreach (var s in theRadio.SliceList)
            {
                if (myClient(s.ClientHandle))
                {
                    var slice = s; // capture for closure
                    q.Enqueue((FunctionDel)(() => { slice.Mute = mute; }), "SetAllMySlicesMute");
                }
            }
        }

        /// <summary>
        /// Release every slice owned by this client except the one the user is
        /// currently on, so the operator ends up cleanly on just their active
        /// slice. The radio requires at least one slice at all times — "release
        /// all" strictly means "release all the extras." If only one slice is
        /// active, this is a no-op and returns false.
        ///
        /// If the transmit slice differs from the receive slice, TXVFO is moved
        /// to the active RX slice first (so it survives the iteration) — the
        /// user ends up on a single-slice transceive configuration.
        /// </summary>
        /// <returns>true if any slice was released, false if nothing to do.</returns>
        public bool ReleaseAllExtraSlices()
        {
            if (theRadio == null) return false;
            if (MyNumSlices <= 1) return false;

            // Identity, not position (QB Track J): capture the slice the user
            // is ON as an object. Positions shift as removals land, so every
            // step below works on Slice objects — the user keeps THEIR slice,
            // whatever its letter, not whatever ends up at their old position.
            Slice keep = VFOToSlice(RXVFO);
            if (keep == null) return false;

            // If TX is on a different slice from RX, move TX to the kept slice
            // so the old TX slice can be released along with the other extras.
            // The user ends up with a single-slice transceive configuration.
            if (CanTransmit && (VFOToSlice(TXVFO) != keep)) TXVFO = SliceToVFO(keep);

            // Snapshot the extras, then release each by object so no list
            // shift can retarget a removal.
            List<Slice> extras = new List<Slice>();
            lock (mySlices)
            {
                foreach (Slice s in mySlices)
                {
                    if (s != keep) extras.Add(s);
                }
            }
            bool released = false;
            foreach (Slice s in extras)
            {
                if (RemoveSlice(s)) released = true;
            }
            return released;
        }

        internal const int AudioGainMinValue = 0;
        internal const int AudioGainMaxValue = 100;
        public int AudioGain
        {
            get
            {
                //return base.AudioGain;
                return theRadio?.ActiveSlice?.AudioGain ?? 0;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AudioGain = value; }), "AudioGain");
            }
        }

        internal const int AudioPanMinValue = 0;
        internal const int AudioPanMaxValue = 100;
        public int AudioPan
        {
            get
            {
                // 50 = centered; a 0 default would read as hard-left during teardown.
                return theRadio?.ActiveSlice?.AudioPan ?? 50;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AudioPan = value; }), "AudioPan");
            }
        }

        // LocalAudioMute(bool) used to live here. It ganged all three physical
        // outputs (lineout, headphone, front speaker) behind one flag and had
        // no live caller — every reference was already commented out. Deleted
        // 2026-08-07 (QB Track A); the individual mutes remain the API.

        internal const int LineoutGainMinValue = 0;
        internal const int LineoutGainMaxValue = 100;
        public int LineoutGain
        {
            get
            {
                // Guarded getter, per the 2026-08-05 ActiveSlice sweep: the
                // settings surface reads these while a radio can be going away
                // underneath it. 0 is the honest default — no radio, no output.
                return theRadio?.LineoutGain ?? 0;
            }
            set
            {
                if (theRadio == null) return;
                q.Enqueue((FunctionDel)(() => { theRadio.LineoutGain = value; }), "LineoutGain");
            }
        }

        internal const int HeadphoneGainMinValue = 0;
        internal const int HeadphoneGainMaxValue = 100;
        public int HeadphoneGain
        {
            get
            {
                return theRadio?.HeadphoneGain ?? 0;
            }
            set
            {
                if (theRadio == null) return;
                q.Enqueue((FunctionDel)(() => { theRadio.HeadphoneGain = value; }), "HeadphoneGain");
            }
        }

        // --- PC output volume ---------------------------------------------------
        // Audio Arc Track A, 2026-08-11. The PC-audio playback boost used to be a
        // hardcoded 4.0f in remoteAudioProc — an empirical patch for a stream
        // that arrives very quiet (raw decoded peaks measure roughly -35 to
        // -20 dBFS; why the source runs that low is a separate open question).
        // Now it is the operator's PC output volume: expressed in dB of boost
        // because dB is legible and speaks well, applied as a float multiplier
        // to the Opus output stream. +12 dB is the historical 4x, kept as the
        // default so upgrades sound unchanged. The value is app-level state
        // (a PC-side gain, not radio state), so it lives in a static backed by
        // AudioOutputConfig — every radio, every connection, one knob.

        public const int PcOutputVolumeDbMin = 0;
        public const int PcOutputVolumeDbMax = 24;
        /// <summary>+12 dB is the historical hardcoded 4x boost.</summary>
        public const int PcOutputVolumeDbDefault = 12;
        private static int _pcOutputVolumeDb = PcOutputVolumeDbDefault;

        /// <summary>
        /// The persisted app-level setting, reachable with no radio connected
        /// (config load at startup runs before any rig exists). Clamped.
        /// </summary>
        public static int PcOutputVolumeDbSetting
        {
            get { return _pcOutputVolumeDb; }
            set { _pcOutputVolumeDb = Math.Min(PcOutputVolumeDbMax, Math.Max(PcOutputVolumeDbMin, value)); }
        }

        /// <summary>The setting as the multiplier the audio stream applies.</summary>
        internal static float PcOutputGainFactor =>
            (float)Math.Pow(10.0, _pcOutputVolumeDb / 20.0);

        /// <summary>
        /// PC output volume in dB of boost (0 to +24, default +12). Setting it
        /// applies live to the running remote-audio stream — no reconnect. The
        /// stream itself hard-limits at full scale, so a hot setting clips
        /// rather than wrapping.
        /// </summary>
        public int PcOutputVolumeDb
        {
            get { return _pcOutputVolumeDb; }
            set
            {
                PcOutputVolumeDbSetting = value;
                var stream = opusOutputChannel?.PortAudioStream;
                if (stream != null) stream.OutputGain = PcOutputGainFactor;
            }
        }

        // --- Output mutes -----------------------------------------------------
        // QB Track B, 2026-08-07. FlexLib has carried these three all along;
        // JJ Flex only ever set them together, through LocalAudioMute, and never
        // read them back. On a radio without a front panel — which is every
        // non-M model — a muted output is invisible and inaudible at the same
        // time, and the operator has nothing to check. These are the read side.
        //
        // Default false on a null radio: "not muted" is the safe thing to report,
        // because the alternative invites someone to chase a mute that is not
        // there while the real problem is that nothing is connected.

        /// <summary>Headphone jack mute. False when no radio is connected.</summary>
        public bool HeadphoneMute
        {
            get { return theRadio?.HeadphoneMute ?? false; }
            set
            {
                if (theRadio == null) return;
                q.Enqueue((FunctionDel)(() => { theRadio.HeadphoneMute = value; }), "HeadphoneMute");
            }
        }

        /// <summary>Line out mute. False when no radio is connected.</summary>
        public bool LineoutMute
        {
            get { return theRadio?.LineoutMute ?? false; }
            set
            {
                if (theRadio == null) return;
                q.Enqueue((FunctionDel)(() => { theRadio.LineoutMute = value; }), "LineoutMute");
            }
        }

        /// <summary>Front panel speaker mute. False when no radio is connected.</summary>
        public bool FrontSpeakerMute
        {
            get { return theRadio?.FrontSpeakerMute ?? false; }
            set
            {
                if (theRadio == null) return;
                q.Enqueue((FunctionDel)(() => { theRadio.FrontSpeakerMute = value; }), "FrontSpeakerMute");
            }
        }

        /// <summary>
        /// The answer to "why is my radio silent", in the order the rungs
        /// actually bite. Null when nothing is obviously wrong.
        /// </summary>
        /// <remarks>
        /// QB Track B, 2026-08-07. Rung one is the one that catches people
        /// coming from a conventional rig, and it caught Noel on his own 8600:
        /// a Flex produces no audio at all — including at its own headphone
        /// jack — until a client is connected to it. The radio being powered on
        /// is not enough. Everything below that is the ordinary ladder: muted
        /// outputs, levels at the floor, and PC audio off when there is no local
        /// listening path.
        ///
        /// Ordered, and it stops at the first rung that fires, because a ladder
        /// read out in full is a list nobody finishes.
        /// </remarks>
        public string SilentRadioAdvisory()
        {
            if (theRadio == null || !IsConnected)
            {
                return "No radio is connected. A Flex makes no audio at all until a client connects to it — "
                     + "including at its own headphone jack. Connect first.";
            }

            bool hp = HeadphoneMute, lo = LineoutMute, fs = FrontSpeakerMute;
            if (hp && lo && fs)
            {
                return "Every radio output is muted: headphones, line out, and the front speaker.";
            }
            if (hp && lo)
            {
                return "The headphone and line out outputs are both muted.";
            }
            if (hp) return "The headphone output is muted.";
            if (lo) return "The line out output is muted.";

            int hg = HeadphoneGain, lg = LineoutGain;
            if (hg == 0 && lg == 0)
            {
                return "Both radio output levels are at zero. On a radio with no front panel knob, this is the only volume control there is.";
            }
            if (hg == 0) return "The headphone level is at zero.";
            if (lg == 0) return "The line out level is at zero.";
            if (hg <= 5 && lg <= 5) return "Both radio output levels are very low.";

            if (!PCAudio && RemoteRig)
            {
                return "Radio audio is not playing through this computer, and on a remote connection there is no other way to hear it.";
            }

            return null;
        }

        public OffOnValues Vox
        {
            get
            {
                bool val = false;
                if (ValidVFO(TXVFO))
                {
                    if (VFOToSlice(TXVFO).DemodMode == "CW") val = theRadio.CWBreakIn;
                    else val = theRadio.SimpleVOXEnable;
                }
                return (val) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                Slice s = VFOToSlice(TXVFO); // also tests the VFO
                bool val = (value == OffOnValues.on) ? true : false;
                if (s != null)
                {
                    if (s.DemodMode == "CW")
                    {
                        q.Enqueue((FunctionDel)(() => { theRadio.CWBreakIn = val; }), "BreakIn");
                    }
                    else
                    {
                        q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXEnable = val; }), "SimpleVOXEnable");
                    }
                }
            }
        }

        public OffOnValues NoiseBlanker
        {
            get
            {
                return (theRadio?.ActiveSlice?.NBOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBOn = (value == OffOnValues.on) ? true : false; }), "NBOn");
            }
        }

        // The values are the same for the wide band NB.
        internal const int NoiseBlankerValueMin = 0;
        internal const int NoiseBlankerValueMax = 100;
        internal const int NoiseBlankerValueIncrement = 5;
        public int NoiseBlankerLevel
        {
            get { return theRadio?.ActiveSlice?.NBLevel ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBLevel = value; }), "NBLevel"); }
        }

        public OffOnValues WidebandNoiseBlanker
        {
            get
            {
                return (theRadio?.ActiveSlice?.WNBOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBOn = (value == OffOnValues.on) ? true : false; }), "WNBOn");
            }
        }

        public int WidebandNoiseBlankerLevel
        {
            get { return theRadio?.ActiveSlice?.WNBLevel ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBLevel = value; }), "WNBLevel"); }
        }

        public OffOnValues NoiseReduction
        {
            get
            {
                return (theRadio?.ActiveSlice?.NROn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NROn = (value == OffOnValues.on) ? true : false; }));
            }
        }

        internal const int NoiseReductionValueMin = 0;
        internal const int NoiseReductionValueMax = 100;
        internal const int NoiseReductionValueIncrement = 5;
        internal int NoiseReductionLevel
        {
            get { return theRadio?.ActiveSlice?.NRLevel ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRLevel = value; })); }
        }

        // Advanced Noise Reduction algorithms (FlexLib v4.0.1)
        // Legacy LMS Noise Reduction (NRL)
        internal const int NoiseReductionLegacyValueMin = 0;
        internal const int NoiseReductionLegacyValueMax = 100;
        internal const int NoiseReductionLegacyValueIncrement = 5;
        public OffOnValues NoiseReductionLegacy
        {
            get
            {
                return (theRadio?.ActiveSlice?.NRLOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRLOn = (value == OffOnValues.on); }), "NRLOn");
                }
            }
        }
        public int NoiseReductionLegacyLevel
        {
            get { return theRadio?.ActiveSlice?.NRL_Level ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRL_Level = value; }), "NRL_Level"); }
        }

        // Spectral Subtraction Noise Reduction (NRS)
        internal const int SpectralNoiseReductionValueMin = 0;
        internal const int SpectralNoiseReductionValueMax = 100;
        internal const int SpectralNoiseReductionValueIncrement = 5;
        public OffOnValues SpectralNoiseReduction
        {
            get
            {
                return (theRadio?.ActiveSlice?.NRSOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRSOn = (value == OffOnValues.on); }), "NRSOn");
                }
            }
        }
        internal int SpectralNoiseReductionLevel
        {
            get { return theRadio?.ActiveSlice?.NRSLevel ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRSLevel = value; }), "NRSLevel"); }
        }

        // Noise Reduction with Filter (NRF)
        internal const int NoiseReductionFilterValueMin = 0;
        internal const int NoiseReductionFilterValueMax = 100;
        internal const int NoiseReductionFilterValueIncrement = 5;
        public OffOnValues NoiseReductionFilter
        {
            get
            {
                return (theRadio?.ActiveSlice?.NRFOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRFOn = (value == OffOnValues.on); }), "NRFOn");
                }
            }
        }
        internal int NoiseReductionFilterLevel
        {
            get { return theRadio?.ActiveSlice?.NRFLevel ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRFLevel = value; }), "NRFLevel"); }
        }

        // Neural noise reduction (RNN) - toggle only
        public OffOnValues NeuralNoiseReduction
        {
            get
            {
                return (theRadio?.ActiveSlice?.RNNOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RNNOn = (value == OffOnValues.on); }), "RNNOn");
                }
            }
        }

        // FFT-based Auto Notch Filter (ANFT) - toggle only
        public OffOnValues AutoNotchFFT
        {
            get
            {
                return (theRadio?.ActiveSlice?.ANFTOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFTOn = (value == OffOnValues.on); }), "ANFTOn");
                }
            }
        }

        // Legacy LMS Auto-Notch Filter (ANFL)
        internal const int AutoNotchLegacyLevelMin = 0;
        internal const int AutoNotchLegacyLevelMax = 100;
        internal const int AutoNotchLegacyLevelIncrement = 10;
        public OffOnValues AutoNotchLegacy
        {
            get
            {
                return (theRadio?.ActiveSlice?.ANFLOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFLOn = (value == OffOnValues.on); }), "ANFLOn");
                }
            }
        }
        internal int AutoNotchLegacyLevel
        {
            get { return theRadio?.ActiveSlice?.ANFL_Level ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFL_Level = value; }), "ANFL_Level"); }
        }

        /// <summary>
        /// AGC mode
        /// </summary>
        /// <remarks>Different from AllRadios</remarks>
        public AGCMode AGCSpeed
        {
            get {
                return theRadio?.ActiveSlice?.AGCMode ?? AGCMode.None;
            }
            set {
                if (HasActiveSlice)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AGCMode = value; }), "AGCMode");
                }
            }
        }

        public const int AGCThresholdMin = 0;
        public const int AGCThresholdMax = 100;
        public const int AGCThresholdIncrement = 5;
        public int AGCThreshold
        {
            get { return theRadio?.ActiveSlice?.AGCThreshold ?? 0; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AGCThreshold = value; })); }
        }

        /// <summary>
        /// data type for RIT/XIT.
        /// </summary>
        public class RITData
        {
            public bool Active;
            public int Value; // may be negative
            public RITData()
            {
                Active = false;
                Value = 0;
            }
            public RITData(RITData r)
            {
                Active = r.Active;
                Value = r.Value;
            }
        }
        private RITData _RIT = new RITData();
        public RITData RIT
        {
            get
            {
                lock (_RIT)
                {
                    return _RIT;
                }
            }
            set
            {
                // _RIT set in PropertyChangedHandler
                lock (_RIT)
                {
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RITOn = value.Active; }));
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RITFreq = value.Value; }));
                }
            }
        }

        private RITData _XIT = new RITData();
        public RITData XIT
        {
            get
            {
                lock (_XIT)
                {
                    return _XIT;
                }
            }
            set
            {
                // _XIT set in PropertyChangedHandler
                lock (_XIT)
                {
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.XITFreq = value.Value; }));
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.XITOn = value.Active; }));
                }
            }
        }

        internal const int BreakinDelayMin = 0;
        internal const int BreakinDelayMax = 2000;
        internal const int BreakinDelayIncrement = 50;
        internal int BreakinDelay
        {
            get { return theRadio.CWDelay; }
            set
            {
                cfgData.BreakinDelay = value;
                issue7620(true);
                i_BreakinDelay = value;
            }
        }
        private int i_BreakinDelay
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWDelay = value; }), "CWDelay");
                if (cwx != null) q.Enqueue((FunctionDel)(() => { cwx.Delay = value; }), "CWDelay");
            }
        }

        internal const int SidetonePitchMin = 0;
        internal const int SidetonePitchMax = 6000;
        internal const int SidetonePitchIncrement = 50;
        internal int SidetonePitch
        {
            get { return theRadio.CWPitch; }
            set
            {
                cfgData.SidetonePitch = value;
                issue7620(true);
                i_SidetonePitch = value;
            }
        }
        private int i_SidetonePitch
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWPitch = value; }), "CWPitch");
            }
        }

        internal const int SidetoneGainMin = 0;
        internal const int SidetoneGainMax = 100;
        internal const int SidetoneGainIncrement = 5;
        internal int SidetoneGain
        {
            get { return theRadio.TXCWMonitorGain; }
            set
            {
                cfgData.SidetoneGain = value;
                issue7620(true);
                i_SidetoneGain = value;
            }
        }
        private int i_SidetoneGain
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXCWMonitorGain = value; }), "TXCWMonitorGain");
#if CWMonitor
                if (useCWMon) q.Enqueue((FunctionDel)(() => { CWMon.Volume = value; }), "CWMonVolume");
#endif
            }
        }

        public enum IambicValues
        {
            off,
            iambicA,
            iambicB
        }

        private IambicValues _Keyer;
        internal IambicValues Keyer
        {
            get { return _Keyer; }
            set
            {
                cfgData.Keyer = _Keyer = value;
                issue7620(true);
                i_Keyer = value;
            }
        }
        private IambicValues i_Keyer
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWIambic = (value == IambicValues.off) ? false : true; }), "CWIambic");
                if (value != IambicValues.off)
                {
                    // Set iambic mode.
                    q.Enqueue((FunctionDel)(() => { theRadio.CWIambicModeA = (value == IambicValues.iambicA) ? true : false; }), "CWIambicA");
                    q.Enqueue((FunctionDel)(() => { theRadio.CWIambicModeB = (value == IambicValues.iambicB) ? true : false; }), "CWIambicB");
                }
            }
        }

        private bool _CWReverse;
        internal bool CWReverse
        {
            get { return _CWReverse; }
            set
            {
                cfgData.CWReverse = value;
                issue7620(true);
                i_CWReverse = value;
            }
        }
        private bool i_CWReverse
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWSwapPaddles = value; }));
            }
        }

        internal const int KeyerSpeedMin = 5;
        internal const int KeyerSpeedMax = 100;
        internal const int KeyerSpeedIncrement = 1;
        internal int KeyerSpeed
        {
            get { return theRadio.CWSpeed; }
            set
            {
                cfgData.KeyerSpeed = value;
                issue7620(true);
                i_KeyerSpeed = value;
            }
        }
        private int i_KeyerSpeed
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWSpeed = value; }));
                if (cwx != null) q.Enqueue((FunctionDel)(() => { cwx.Speed = value; }));
            }
        }

        internal OffOnValues CWL
        {
            get { return (theRadio.CWL_Enabled) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                cfgData.CWLEnabled = val;
                issue7620(true);
                i_CWL = value;
            }
        }
        private OffOnValues i_CWL
        {
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.CWL_Enabled = val; }));
            }
        }

        internal const int MonitorPanMin = 0;
        internal const int MonitorPanMax = 100;
        internal const int MonitorPanIncrement = 5;
        internal int MonitorPan
        {
            get { return theRadio.TXCWMonitorPan; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.TXCWMonitorPan = value; })); }
        }

        // TX getter family null guards (2026-08-07): the Audio Workshop's poll
        // timer races radio teardown at app close — MeterTimer_Tick →
        // PollTxAudio → get_MicGain NRE'd on the nulled theRadio (crash zip
        // JJFlexError-20260807-153513). Same teardown crash class as the
        // 2026-08-05 ActiveSlice sweep (f406b4cc), which covered slice-level
        // getters but not this radio-level TX family. Defaults follow that
        // sweep's conventions: toggles off, levels 0, pans 50 (centered).
        public const int MicGainMin = 0;
        public const int MicGainMax = 100;
        public const int MicGainIncrement = 1;
        public int MicGain
        {
            get { return theRadio?.MicLevel ?? 0; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.MicLevel = value; })); }
        }

        public OffOnValues ProcessorOn
        {
            get { return (theRadio?.SpeechProcessorEnable == true) ? OffOnValues.on : OffOnValues.off; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.SpeechProcessorEnable = (value == OffOnValues.on) ? true : false; })); }
        }

        public enum ProcessorSettings
        {
            NOR = 0,
            DX,
            DXX,
        }
        public ProcessorSettings ProcessorSetting
        {
            get { return (ProcessorSettings)(theRadio?.SpeechProcessorLevel ?? 0); }
            set { q.Enqueue((FunctionDel)(() => { theRadio.SpeechProcessorLevel = (uint)value; })); }
        }

        public OffOnValues Compander
        {
            get { return (theRadio?.CompanderOn == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.CompanderOn = val; }));
            }
        }

        public const int CompanderLevelMin = 0;
        public const int CompanderLevelMax = 100;
        public const int CompanderLevelIncrement = 5;
        public int CompanderLevel
        {
            get { return theRadio?.CompanderLevel ?? 0; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CompanderLevel = value; }));
            }
        }

        public int TXFilterLowMin = 0;
        public int TXFilterLowMax { get { return (TXFilterHigh - 50); } }
        public int TXFilterLowIncrement = 50;
        public int TXFilterLow
        {
            get { return (theRadio != null) ? theRadio.TXFilterLow : 0; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXFilterLow = value; }));
            }
        }

        public int TXFilterHighMin { get { return (TXFilterLow + 50); } }
        public int TXFilterHighMax = 10000;
        public int TXFilterHighIncrement = 50;
        public int TXFilterHigh
        {
            get { return (theRadio != null) ? theRadio.TXFilterHigh : 0; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXFilterHigh = value; }));
            }
        }

        public OffOnValues MicBoost
        {
            get { return (theRadio?.MicBoost == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.MicBoost = val; }));
            }
        }

        public OffOnValues MicBias
        {
            get { return (theRadio?.MicBias == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.MicBias = val; }));
            }
        }

        public OffOnValues Monitor
        {
            get { return (theRadio?.TXMonitor == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.TXMonitor = val; }));
            }
        }

        public const int SBMonitorLevelMin = 0;
        public const int SBMonitorLevelMax = 100;
        public const int SBMonitorLevelIncrement = 5;
        public int SBMonitorLevel
        {
            get { return theRadio?.TXSBMonitorGain ?? 0; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXSBMonitorGain = value; }));
            }
        }

        public const int SBMonitorPanMin = 0;
        public const int SBMonitorPanMax = 100;
        public const int SBMonitorPanIncrement = 5;
        public int SBMonitorPan
        {
            get { return theRadio?.TXSBMonitorPan ?? 50; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXSBMonitorPan = value; }));
            }
        }

        // ── TX equalizer (Track F, 2026-08-16) ──
        //
        // The TX EQ was the one piece of the radio's TX audio chain this class
        // never wrapped, which is why audio presets shipped without it (#50 —
        // "exported presets may be missing the TX EQ"; they were). FlexLib's
        // Equalizer object is created on demand and populated by an "eq txsc
        // info" round trip, so callers who want a capture should call
        // RequestTxEqualizer() early (the Audio Workshop does it when it gets
        // a rig) and treat a null GetTxEq() as "the radio has not answered
        // yet", not as "the radio has no EQ".

        /// <summary>
        /// A snapshot of the radio's TX equalizer: enabled plus the eight
        /// usable bands (63 Hz – 8 kHz), each in dB, radio range ±10. FlexLib
        /// carries a 32 Hz member too, but the radio itself does not honor it
        /// (FlexLib's own comment), so it is not modeled here.
        /// </summary>
        public sealed class TxEqSettings
        {
            public bool Enabled;
            public int Hz63;
            public int Hz125;
            public int Hz250;
            public int Hz500;
            public int Hz1000;
            public int Hz2000;
            public int Hz4000;
            public int Hz8000;
        }

        /// <summary>
        /// Ask the radio for its TX equalizer state. Idempotent; safe with no
        /// radio. Until the answer arrives, <see cref="GetTxEq"/> returns null.
        /// </summary>
        public void RequestTxEqualizer()
        {
            q.Enqueue((FunctionDel)(() =>
            {
                if (theRadio == null) return;
                var eq = theRadio.FindEqualizerByEQSelect(EqualizerSelect.TX);
                if (eq == null)
                {
                    // CreateEqualizer hands back an unregistered object; the
                    // RequestEqualizerFromRadio round trip is what registers
                    // it (and fills its levels) once the radio replies.
                    theRadio.CreateEqualizer(EqualizerSelect.TX)?.RequestEqualizerFromRadio();
                }
                else
                {
                    eq.RequestEqualizerInfo();
                }
            }), "RequestTxEqualizer");
        }

        /// <summary>
        /// Current TX equalizer state, or null when the radio has not
        /// reported it yet (see <see cref="RequestTxEqualizer"/>).
        /// </summary>
        public TxEqSettings GetTxEq()
        {
            var eq = theRadio?.FindEqualizerByEQSelect(EqualizerSelect.TX);
            if (eq == null) return null;
            return new TxEqSettings
            {
                Enabled = eq.EQ_enabled,
                Hz63 = eq.level_63Hz,
                Hz125 = eq.level_125Hz,
                Hz250 = eq.level_250Hz,
                Hz500 = eq.level_500Hz,
                Hz1000 = eq.level_1000Hz,
                Hz2000 = eq.level_2000Hz,
                Hz4000 = eq.level_4000Hz,
                Hz8000 = eq.level_8000Hz,
            };
        }

        /// <summary>
        /// Apply a TX equalizer snapshot. Returns false (and changes nothing)
        /// when the radio's equalizer object is not available yet — callers
        /// own saying so rather than pretending the EQ was set.
        /// </summary>
        public bool ApplyTxEq(TxEqSettings s)
        {
            if (s == null) return false;
            if (theRadio?.FindEqualizerByEQSelect(EqualizerSelect.TX) == null) return false;
            q.Enqueue((FunctionDel)(() =>
            {
                var eq = theRadio?.FindEqualizerByEQSelect(EqualizerSelect.TX);
                if (eq == null) return;
                static int Clamp(int v) => Math.Max(-10, Math.Min(10, v));
                eq.level_63Hz = Clamp(s.Hz63);
                eq.level_125Hz = Clamp(s.Hz125);
                eq.level_250Hz = Clamp(s.Hz250);
                eq.level_500Hz = Clamp(s.Hz500);
                eq.level_1000Hz = Clamp(s.Hz1000);
                eq.level_2000Hz = Clamp(s.Hz2000);
                eq.level_4000Hz = Clamp(s.Hz4000);
                eq.level_8000Hz = Clamp(s.Hz8000);
                eq.EQ_enabled = s.Enabled;
            }), "ApplyTxEq");
            return true;
        }

        // ── Audio Check / hear-yourself support (QB Track G, 2026-08-07) ──
        //
        // Public wrappers for the Audio Workshop's Audio Check session.
        // Internal OffOnValues-typed Play/Record wrappers already exist for
        // in-assembly callers; these bool-typed public ones serve JJFlexWpf
        // (separate assembly, no InternalsVisibleTo).

        /// <summary>
        /// Radio-reported TX mic input list (e.g. MIC, BAL, LINE, ACC, PC).
        /// Empty until the radio answers "mic list".
        /// </summary>
        public List<string> MicSourceList => theRadio?.MicInputList?.ToList() ?? new List<string>();

        /// <summary>
        /// The radio's selected TX mic input. Verified live (2026-08-07):
        /// MicGain acts on THIS selection, not on whatever is actually feeding
        /// the transmitter — a hand-mic PTT override keys from the mic jack
        /// regardless of this setting, and the gain knob then adjusts an idle
        /// stream. Note the PC-audio path silently forces this to "PC" when it
        /// starts (startOpusOutputChannel) and restores the prior value when
        /// it stops.
        /// </summary>
        public string MicSource
        {
            get { return theRadio?.MicInput ?? ""; }
            set
            {
                // An operator choosing a source through JJ Flex is intent, not
                // drift, so move what checkPcMicSelection expects along with
                // them: a deliberate switch to the analog mic is then honoured
                // in silence rather than fought and warned about. Only
                // meaningful while the PC audio channel is actually running —
                // that channel is what set the selection to PC to begin with.
                _pcMicExpected = opusOutputChannel != null
                    && opusOutputChannel.Started
                    && string.Equals(value, "PC", StringComparison.OrdinalIgnoreCase);
                _pcMicDiverged = false;
                q.Enqueue((FunctionDel)(() => { theRadio.MicInput = value; }), "MicInput");
            }
        }

        /// <summary>
        /// Full duplex: receivers stay alive while transmitting. Radio-wide
        /// flag, meaningful on 2-SCU radios; factory default off (keying mutes
        /// every RX). The loopback check sets it and MUST restore the prior
        /// value on teardown — never leave it changed.
        /// </summary>
        public bool FullDuplexEnabled
        {
            get { return theRadio?.FullDuplexEnabled ?? false; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.FullDuplexEnabled = value; }), "FullDuplexEnabled"); }
        }

        /// <summary>
        /// Active slice quick-record (SmartSDR's Quick Record). Verified
        /// telemetry (2026-08-07 live): buffer caps at 120 seconds and behaves
        /// ring-like at the cap (recent material kept); two takes can coexist.
        /// The record tap sits upstream of the TX audio mute, so it captures
        /// demodulated audio even with full duplex off. Callers MUST check
        /// state before re-arming — a live re-arm race nearly wiped takes.
        /// </summary>
        public bool SliceRecordOn
        {
            get { return theRadio?.ActiveSlice?.RecordOn == true; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RecordOn = value; }), "RecordOn"); }
        }

        /// <summary>Active slice quick-play of the record buffer.</summary>
        public bool SlicePlayOn
        {
            get { return theRadio?.ActiveSlice?.PlayOn == true; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.PlayOn = value; }), "PlayOn"); }
        }

        /// <summary>True when the record buffer has playable content.</summary>
        public bool SlicePlayEnabled => theRadio?.ActiveSlice?.PlayEnabled ?? false;

        /// <summary>
        /// The radio-reported PTT source ("SW", "Mic", "ACC", "RCA", "TUNE",
        /// or "None"), from the interlock status "source=" field.
        /// </summary>
        public string PttSourceName => theRadio?.PTTSource.ToString() ?? "None";

        /// <summary>
        /// True when the transmitter is keyed by a HARDWARE line (front-panel
        /// mic PTT, ACC, or rear RCA). Safety-critical honesty: software unkey
        /// correctly cannot override a hardware keying line — a hand mic on
        /// the rear RCA keeps the rig transmitting no matter what the app
        /// does, and the operator must be told so (2026-08-07 stuck-TX
        /// episode, interlock source=RCA).
        /// </summary>
        public bool PttSourceIsHardware =>
            theRadio?.PTTSource is PTTSource.Mic or PTTSource.ACC or PTTSource.RCA;

        // ── Loopback check plumbing (QB Track G, 2026-08-07) ──
        //
        // The transverter-port loopback, live-verified on the 8600: with full
        // duplex on, TX antenna XVT A, an "ears" slice listening on the same
        // XVT port at the same frequency/mode, 1 watt, and TX monitor OFF,
        // the operator hears their own transmitted signal demodulated inside
        // one radio with no antennas. HONESTY (ratified): raw adjacent-port
        // coupling massively overloads the receiver — what this yields is
        // presence/processing/rough-shape verification, NOT a faithful
        // off-air listen. Drive management below aims the coupling at the
        // receiver's linear range where a transverter band definition exists.

        private bool _loopbackArranged;
        private bool _lbSavedFdx;
        private string _lbSavedTxAnt = "";
        private bool _lbSavedMonitor;
        private int _lbSavedPower;
        private int _lbEarsVfo = -1;
        private Xvtr _lbDriveBand;
        private double _lbSavedDriveDbm;

        /// <summary>True while the loopback arrangement is applied.</summary>
        public bool LoopbackArranged => _loopbackArranged;

        /// <summary>
        /// Loopback needs two receive chains (2-SCU — during TX the radio
        /// borrows one), a free slice slot for the ears slice, and a
        /// transverter port in the TX antenna list.
        /// </summary>
        public bool LoopbackSupported =>
            theRadio?.DiversityIsAllowed == true &&
            TXAntennaList.Exists(a => a.StartsWith("XVT", StringComparison.OrdinalIgnoreCase));

        /// <summary>Why the loopback check is not available here, or "".</summary>
        public string LoopbackUnavailableReason
        {
            get
            {
                if (theRadio == null) return "No radio connected";
                if (theRadio.DiversityIsAllowed != true)
                    return "This radio has a single receiver, which the radio itself uses during transmit";
                if (!TXAntennaList.Exists(a => a.StartsWith("XVT", StringComparison.OrdinalIgnoreCase)))
                    return "No transverter port in this radio's transmit antenna list";
                return "";
            }
        }

        /// <summary>
        /// Apply the verified loopback recipe: snapshot full-duplex flag, TX
        /// antenna, monitor state, RF power and slice roster, then set FDX
        /// on, TX antenna to the first XVT port, 1 watt (power 0 is silent —
        /// verified), monitor OFF, and create the ears slice on the same
        /// port/frequency/mode. All radio writes ride the command queue, so a
        /// caller that keys immediately afterward is sequenced after the
        /// arrangement. Returns false with nothing changed when unsupported.
        /// </summary>
        public bool StartLoopbackArrangement()
        {
            if (_loopbackArranged) return true;
            if (theRadio == null || !HasActiveSlice || !LoopbackSupported) return false;
            if (theRadio.SliceList.Count >= TotalMaxSlices)
            {
                Tracing.TraceLine("StartLoopbackArrangement: no free slice slot", TraceLevel.Warning);
                return false;
            }

            string xvt = TXAntennaList.Find(a => a.StartsWith("XVT", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(xvt)) return false;

            // Snapshot.
            _lbSavedFdx = theRadio.FullDuplexEnabled;
            _lbSavedTxAnt = TXAntennaName;
            _lbSavedMonitor = theRadio.TXMonitor;
            _lbSavedPower = XmitPower;
            int preCount = MyNumSlices;

            // Apply the recipe (queue-sequenced).
            FullDuplexEnabled = true;
            TXAntennaName = xvt;                       // TX slice → XVT port
            XmitPower = 1;                             // integer floor above silent
            Monitor = OffOnValues.off;                 // monitor stacked over the
                                                       // delayed loop is an echo
            // Drive management: where a transverter band is defined, start at
            // maximum attenuation so the adjacent-port coupling lands as far
            // into the receiver's linear range as the hardware allows.
            _lbDriveBand = findAnyValidXvtr();
            if (_lbDriveBand != null)
            {
                _lbSavedDriveDbm = _lbDriveBand.MaxPower;
                var band = _lbDriveBand;
                q.Enqueue((FunctionDel)(() => { band.MaxPower = -10.0; }), "LoopbackDrive");
            }

            // Ears slice: create, then configure once it exists (the NewSlice
            // queue item awaits creation internally, so this enqueued config
            // runs after it).
            if (!NewSlice())
            {
                Tracing.TraceLine("StartLoopbackArrangement: NewSlice refused", TraceLevel.Error);
                rollbackLoopback();
                return false;
            }
            _lbEarsVfo = preCount;
            q.Enqueue((FunctionDel)(() =>
            {
                Slice ears = null;
                lock (mySlices)
                {
                    if (mySlices.Count > preCount) ears = mySlices[preCount];
                }
                var tx = theRadio?.ActiveSlice;
                if (ears == null || tx == null || ReferenceEquals(ears, tx))
                {
                    Tracing.TraceLine("Loopback ears slice config: slice missing", TraceLevel.Error);
                    return;
                }
                ears.Freq = tx.Freq;
                ears.DemodMode = tx.DemodMode;
                ears.RXAnt = xvt;   // same port worked at this power, verified
            }), "LoopbackEars");

            _loopbackArranged = true;
            Tracing.TraceLine($"Loopback arranged: xvt={xvt} savedFdx={_lbSavedFdx} savedAnt={_lbSavedTxAnt} savedPwr={_lbSavedPower} earsVfo={_lbEarsVfo}", TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Tear the loopback arrangement down: restore every saved value and
        /// remove the ears slice. Returns a short status suitable for speech
        /// ("" when everything restored cleanly).
        /// </summary>
        public string EndLoopbackArrangement()
        {
            if (!_loopbackArranged) return "";
            _loopbackArranged = false;

            string trouble = "";
            if (theRadio != null)
            {
                // Ears slice out first (can't remove the active VFO — if the
                // operator moved onto it, leave it and say so).
                if (_lbEarsVfo >= 0 && _lbEarsVfo < MyNumSlices)
                {
                    if (!RemoveSlice(_lbEarsVfo))
                        trouble = "The listening slice is your active slice, so it was kept. ";
                }
                TXAntennaName = _lbSavedTxAnt;
                XmitPower = _lbSavedPower;
                Monitor = _lbSavedMonitor ? OffOnValues.on : OffOnValues.off;
                FullDuplexEnabled = _lbSavedFdx;
                if (_lbDriveBand != null)
                {
                    var band = _lbDriveBand;
                    double dbm = _lbSavedDriveDbm;
                    q.Enqueue((FunctionDel)(() => { band.MaxPower = dbm; }), "LoopbackDriveRestore");
                }
            }
            _lbEarsVfo = -1;
            _lbDriveBand = null;
            Tracing.TraceLine("Loopback arrangement ended: " + (trouble == "" ? "clean" : trouble), TraceLevel.Info);
            return trouble;
        }

        private void rollbackLoopback()
        {
            if (theRadio == null) return;
            FullDuplexEnabled = _lbSavedFdx;
            TXAntennaName = _lbSavedTxAnt;
            XmitPower = _lbSavedPower;
            Monitor = _lbSavedMonitor ? OffOnValues.on : OffOnValues.off;
            if (_lbDriveBand != null)
            {
                var band = _lbDriveBand;
                double dbm = _lbSavedDriveDbm;
                q.Enqueue((FunctionDel)(() => { band.MaxPower = dbm; }), "LoopbackDriveRestore");
                _lbDriveBand = null;
            }
        }

        /// <summary>
        /// Best-effort transverter band probe. FlexLib keeps the Xvtr list
        /// private and exposes lookup by index only; defined bands get small
        /// indices. Whether dBm drive management can upgrade the loopback
        /// listen to clean demodulation is an OPEN question (plan section 4)
        /// — this is the mechanism, honestly gated on a band existing.
        /// </summary>
        private Xvtr findAnyValidXvtr()
        {
            if (theRadio == null) return null;
            for (int i = 0; i < 16; i++)
            {
                var x = theRadio.FindXvtrByIndex(i);
                if (x != null && x.Valid) return x;
            }
            return null;
        }

        /// <summary>True when loopback drive management found a transverter
        /// band to act on (informs the session's honesty copy).</summary>
        public bool LoopbackDriveManaged => _lbDriveBand != null;

        // Dummy Load Mode: zeroes power for safe PTT testing, restores on disable
        private bool _dummyLoadMode;
        private int _savedRFPower;
        private int _savedTunePower;

        public bool DummyLoadMode
        {
            get => _dummyLoadMode;
            set
            {
                if (value == _dummyLoadMode) return;

                if (value)
                {
                    _savedRFPower = XmitPower;
                    _savedTunePower = TunePower;
                    XmitPower = 0;
                    TunePower = 0;
                    _dummyLoadMode = true;
                }
                else
                {
                    _dummyLoadMode = false;
                    XmitPower = _savedRFPower;
                    TunePower = _savedTunePower;
                }
            }
        }

        // transmit power
        internal const int XmitPowerMin = 0;
        internal const int XmitPowerMax = 100;
        internal const int XmitPowerIncrement = 5;
        private int _XmitPower;
        public int XmitPower
        {
            get
            {
                return _XmitPower;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.RFPower = value; }));
            }
        }

        // Tuning power
        internal const int TunePowerMin = 0;
        internal const int TunePowerMax = 100;
        internal const int TunePowerIncrement = 1;
        private int _TunePower;
        public int TunePower
        {
            get
            {
                return _TunePower;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TunePower = value; }));
            }
        }

        // Vox delay is in MS, with 50 MS per step, see FlexLib.Radio.cs
        internal const int VoxDelayMin = 0;
        internal const int VoxDelayMax = 2000;
        internal const int VoxDelayIncrement = 100;
        internal const int VoxDelayMS = 50;
        internal int VoxDelay
        {
            get { return theRadio.SimpleVOXDelay * VoxDelayMS; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXDelay = value / VoxDelayMS; }));
            }
        }

        internal const int VoxGainMin = 0;
        internal const int VoxGainMax = 100;
        internal const int VoxGainIncrement = 5;
        internal int VoxGain
        {
            get { return theRadio.SimpleVOXLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXLevel = value; }));
            }
        }

        internal OffOnValues ANF
        {
            get { return (theRadio?.ActiveSlice?.ANFOn == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                if (HasActiveSlice)
                {
                    bool val = (value == OffOnValues.on) ? true : false;
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFOn = val; }));
                }
            }
        }

        internal const int AutoNotchLevelMin = 0;
        internal const int AutoNotchLevelMax = 100;
        internal const int AutoNotchLevelIncrement = 10;
        internal int AutoNotchLevel
        {
            get { return theRadio?.ActiveSlice?.ANFLevel ?? 0; }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFLevel = value; }));
            }
        }

        public OffOnValues APF
        {
            get { return (theRadio?.ActiveSlice?.APFOn == true) ? OffOnValues.on : OffOnValues.off; }
            //get { return (theRadio.APFMode) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFOn = val; }));
            }
        }

        internal const int AutoPeakLevelMin = 0;
        internal const int AutoPeakLevelMax = 100;
        internal const int AutoPeakLevelIncrement = 10;
        internal int AutoPeakLevel
        {
            get { return theRadio?.ActiveSlice?.APFLevel ?? 0; }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFLevel = value; }));
            }
        }

        private Panadapter activePan
        {
            get { return theRadio?.ActiveSlice?.Panadapter; }
        }

        public int RFGainMin = -10;
        public int RFGainMax = 30;
        public int RFGainIncrement = 10;
        public int RFGain
        {
            get { return (activePan != null) ? activePan.RFGain : 0; }
            set
            {
                if (activePan != null)
                {
                    q.Enqueue((FunctionDel)(() => { activePan.RFGain = value; }));
                }
            }
        }

#if zero
        private const int PreAmpMin = 0;
        private const int PreAmpMax = 20;
        private OffOnValues _PreAmp;
        internal OffOnValues PreAmp
        {
            get { return _PreAmp; }
            set
            {
                // _PreAmp changed by interrupt.
                activePan.RFGain = (value == OffOnValues.on) ? PreAmpMax : PreAmpMin;
            }
        }

        internal const int PanRFMin = 0;
        internal const int PanRFMax = 20;
        internal const int PanRFIncrement = 20;
        internal int PanRF
        {
            get { return theRadio.ActiveSlice.Panadapter.RFGain; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.Panadapter.RFGain = value; }));
            }
        }
#endif

        internal const int AutoPeakQMin = 0;
        internal const int AutoPeakQMax = 33;
        internal const int AutoPeakQIncrement = 1;
        internal int AutoPeakQ
        {
            get { return (int)theRadio.APFQFactor; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.APFQFactor = (double)value; }));
            }
        }

        internal bool TNF
        {
            get { return theRadio.TNFEnabled; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TNFEnabled = value; }));
            }
        }

        public OffOnValues Squelch
        {
            get { return (theRadio?.ActiveSlice?.SquelchOn == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.SquelchOn = (value == OffOnValues.on) ? true : false; }));
            }
        }

        public const int SquelchLevelMin = 0;
        public const int SquelchLevelMax = 100;
        public const int SquelchLevelIncrement = 5;
        public int SquelchLevel
        {
            get { return theRadio?.ActiveSlice?.SquelchLevel ?? 0; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.SquelchLevel = value; }));
            }
        }

        /// <summary>
        /// Offset direction values
        /// </summary>
        public enum OffsetDirections : byte
        {
            off, minus, plus, allTypes
        }

        internal OffsetDirections FlexOffsetDirectionToOffsetDirection(FMTXOffsetDirection dir)
        {
            OffsetDirections rv = OffsetDirections.off;
            switch (dir)
            {
                case FMTXOffsetDirection.Down: rv = OffsetDirections.minus; break;
                case FMTXOffsetDirection.Up: rv = OffsetDirections.plus; break;
            }
            return rv;
        }
        internal FMTXOffsetDirection OffsetDirectionToFlexOffsetDirection(OffsetDirections dir)
        {
            FMTXOffsetDirection rv = FMTXOffsetDirection.Simplex;
            switch (dir)
            {
                case OffsetDirections.minus: rv = FMTXOffsetDirection.Down; break;
                case OffsetDirections.plus: rv = FMTXOffsetDirection.Up; break;
            }
            return rv;
        }
        public OffsetDirections OffsetDirection
        {
            get
            {
                var slice = theRadio?.ActiveSlice;
                return (slice != null)
                    ? FlexOffsetDirectionToOffsetDirection(slice.RepeaterOffsetDirection)
                    : OffsetDirections.off;
            }
            set
            {
                if (theRadio?.ActiveSlice != null)
                {
                    FMTXOffsetDirection val = OffsetDirectionToFlexOffsetDirection(value);
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RepeaterOffsetDirection = val; }));
                }
            }
        }

        internal OffOnValues FMEmphasis
        {
            get { return (theRadio?.ActiveSlice?.DFMPreDeEmphasis == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.DFMPreDeEmphasis = val; }));
            }
        }

        // Note the Flex frequency is in MhZ, and ours in KHZ.
        internal const int offsetMin = 50;
        internal const int offsetMax = 2000;
        internal const int offsetIncrement = 50;
        public int OffsetFrequency
        {
            get { return (int)((theRadio?.ActiveSlice?.FMRepeaterOffsetFreq ?? 0) * 1e3); }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FMRepeaterOffsetFreq = (double)value / 1e3; }));
            }
        }

        // Valid FM tone modes for this rig, see FMToneMode in Memory.cs in FlexLib.
        /// <summary>
        /// Tone/CTSS value type
        /// </summary>
        public class ToneCTCSSValue
        {
            internal char value;
            private string name;
            internal ToneCTCSSValue(char val, string nam)
            {
                value = val;
                name = nam;
            }
            internal ToneCTCSSValue(char c)
            {
                value = c;
                name = "";
            }
            public static bool operator ==(ToneCTCSSValue val1, ToneCTCSSValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return true;
                if (((object)val1 == null) || ((object)val2 == null)) return false;
                return (val1.value == val2.value);
            }
            public static bool operator !=(ToneCTCSSValue val1, ToneCTCSSValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return false;
                if (((object)val1 == null) || ((object)val2 == null)) return true;
                return (val1.value != val2.value);
            }
            public override bool Equals(object obj)
            {
                bool rv;
                try { rv = (value == ((ToneCTCSSValue)obj).value); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("ToneCTCSSValue exception:" + ex.Message, TraceLevel.Error);
                    rv = false;
                }
                return rv;
            }
            public override int GetHashCode()
            {
                return (int)value;
            }
            public override string ToString()
            {
                return name;
            }
        }

        /// <summary>
        /// FM Tone modes
        /// </summary>
        public ToneCTCSSValue[] FMToneModes;

        internal static ToneCTCSSValue[] myFMToneModes =
        {
            new ToneCTCSSValue('0', "Off"),
            new ToneCTCSSValue('1', "CTCSS"),
        };
        internal ToneCTCSSValue ToneModeToToneCTCSS(FMToneMode mode)
        {
            return myFMToneModes[(int)mode];
        }
        internal FMToneMode ToneCTCSSToToneMode(ToneCTCSSValue val)
        {
            return (FMToneMode)(val.value - 0x30);
        }
        public ToneCTCSSValue ToneCTCSS
        {
            get { return ToneModeToToneCTCSS(theRadio?.ActiveSlice?.ToneMode ?? FMToneMode.Off); }
            set
            {
                FMToneMode val = ToneCTCSSToToneMode(value);
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ToneMode = val; }));
            }
        }

        internal float ToneValueToFloat(string val)
        {
            // 0 reads downstream as "no tone", which is the right answer for
            // something we could not parse - but a CTCSS tone silently becoming
            // no-tone is how an operator ends up transmitting into a repeater
            // that never opens, with nothing on screen suggesting why.
            if (!System.Single.TryParse(val, out float rv))
                Tracing.TraceLine(
                    $"ToneValueToFloat: '{val}' is not a number — reporting no tone.",
                    TraceLevel.Warning);
            return rv;
        }
        internal string FloatToToneValue(float val)
        {
            return val.ToString("F1");
        }
        public float ToneFrequency
        {
            get
            {
                return ToneValueToFloat(theRadio?.ActiveSlice?.FMToneValue);
            }
            set
            {
                string val = FloatToToneValue(value);
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FMToneValue = val; }));
            }
        }

        internal OffOnValues FM1750
        {
            get { return (theRadio?.ActiveSlice?.FMTX1750 == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FMTX1750 = val; }));
            }
        }

        internal OffOnValues Binaural
        {
            get { return (theRadio.BinauralRX) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.BinauralRX = val; }));
            }
        }

        internal OffOnValues Play
        {
            get { return (theRadio?.ActiveSlice?.PlayOn == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.PlayOn = val; }));
            }
        }

        internal OffOnValues Record
        {
            get { return (theRadio?.ActiveSlice?.RecordOn == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RecordOn = val; }));
            }
        }

        private OffOnValues _DAXOnOff;
        /// <summary>
        /// DAX audio, on or off.
        /// </summary>
        public OffOnValues DAXOnOff
        {
            get { return _DAXOnOff; }
            set
            {
                _DAXOnOff = value;
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.DAXOn = val; }));
            }
        }

        internal bool CanPlay { get { return theRadio?.ActiveSlice?.PlayEnabled ?? false; } }

        internal const int AMCarrierLevelMin = 0;
        internal const int AMCarrierLevelMax = 100;
        internal const int AMCarrierLevelIncrement = 5;
        internal int AMCarrierLevel
        {
            get { return theRadio.AMCarrierLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.AMCarrierLevel = value; }));
            }
        }

        internal void setNextValue1()
        {
            var slice = theRadio?.ActiveSlice;
            if (slice == null) return;
            if (slice.DemodMode == "CW")
            {
                slice.APFOn = !slice.APFOn;
            }
            else
            {
                slice.NROn = !slice.NROn;
            }
        }

        /// <summary>
        /// Rig's info (list of strings)
        /// </summary>
        public List<string> RigInfo
        {
            get
            {
                List<string> rv = new List<string>();
                rv.Add("Model:" + theRadio.Model);
                rv.Add("Version:" +
                    ((theRadio.Version & 0x00ff000000000000) / 0x0001000000000000).ToString() + '.' +
                    ((theRadio.Version & 0x0000ff0000000000) / 0x0000010000000000).ToString() + '.' +
                    ((theRadio.Version & 0x000000ff00000000) / 0x0000000100000000).ToString()
                    //((theRadio.Version & 0x00000000ffffffff)).ToString();
                    );
                rv.Add("Serial:" + theRadio.Serial);
                rv.Add("Call:" + theRadio.Callsign);
                rv.Add("Nickname:" + theRadio.Nickname);
                rv.Add("IP:" + theRadio.IP.ToString());
                return rv;
            }
        }

        /// <summary>
        /// List of connected stations
        /// </summary>
        public List<string> Stations
        {
            get
            {
                List<string> rv = new List<string>();
                rv.AddRange(theRadio.GuiClientStations.Split(new char[] { ',' }));
                return rv;
            }
        }

        public virtual void TestRoutine()
        {
            Tracing.TraceLine("TestRoutine", TraceLevel.Info);
            MessageBox.Show(RXFrequency.ToString() + ' ' + TXFrequency.ToString(), "msg", MessageBoxButtons.OK);
        }
        #endregion

        // region profile management
        #region profiles
        /// <summary>
        /// Get profiles by type.
        /// </summary>
        /// <param name="typ">ProfileType</param>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetProfilesByType(ProfileTypes typ, List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (p.ProfileType == typ)
                {
                    rv.Add(p);
                }
            }
            return rv;
        }

        /// <summary>
        /// Get profile by name.
        /// </summary>
        /// <param name="name">name</param>
        /// <param name="typ">ProfileType</param>
        /// <param name="lst">(optional) list of profiles</param>
        public Profile_t GetProfileByName(string name, ProfileTypes typ,
            List<Profile_t> lst = null)
        {
            Profile_t rv = null;
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in GetProfilesByType(typ, lst))
            {
                if (p.Name == name)
                {
                    rv = p;
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Get default profiles.
        /// </summary>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetDefaultProfiles(List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            // Any default profile must be in Callouts.Profiles.
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (p.Default)
                {
                    rv.Add(p);
                }
            }
            return rv;
        }

#if zero
        /// <summary>
        /// Get current profiles.
        /// </summary>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetCurrentProfiles(List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (Profile_t.Current(this, p))
                {
                    rv.Add(p);
                }
            }
            return rv;
        }
#endif

        /// <summary>
        /// Get all profiles on the radio not in provided list.
        /// </summary>
        /// <param name="lst">list of profiles )may be null)</param>
        public List<Profile_t> GetRigProfiles(List<Profile_t> lst)
        {
            Tracing.TraceLine("GetRigProfiles", TraceLevel.Info);
            List<Profile_t> rv = new List<Profile_t>();
            // Add any profiles not in the list.
            foreach (string name in theRadio.ProfileDisplayList)
            {
                // if no list or else display profile not in it
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.display, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.display,
                        (theRadio.ProfileDisplaySelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileGlobalList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.global, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.global,
                        (theRadio.ProfileGlobalSelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileMICList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.mic, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.mic,
                        (theRadio.ProfileMICSelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileTXList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.tx, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.tx,
                        (theRadio.ProfileTXSelection == name));
                    rv.Add(p);
                }
            }
            return rv;
        }

        /// <summary>
        /// Select the profile.
        /// </summary>
        /// <param name="prof">the profile</param>
        public bool SelectProfile(Profile_t prof)
        {
            Tracing.TraceLine("SelectProfile:" + prof.ToString(), TraceLevel.Info);
            bool rv = true;
            // select profiles, allowed before main loop.
            string str = "";
            switch (prof.ProfileType)
            {
                case ProfileTypes.display:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.ProfileDisplaySelection = prof.Name;
                    }), "ProfileDisplaySelection", true);
                    break;
                case ProfileTypes.global:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.ProfileGlobalSelection = prof.Name;
                    }), "ProfileGlobalSelection", true);
                    break;
                case ProfileTypes.tx:
                    {
                        q.Enqueue((FunctionDel)(() =>
                        {
                            if (!theRadio.ProfileTXList.Contains(prof.Name))
                            {
                                theRadio.CreateTXProfile(prof.Name);
                                str += "CreateTXProfile_";
                            }
                            theRadio.ProfileTXSelection = prof.Name;
                            str += "ProfileTXSelection";
                        }), str, true);
                    }
                    break;
                case ProfileTypes.mic:
                    {
                        q.Enqueue((FunctionDel)(() =>
                        {
                            if (!theRadio.ProfileMICList.Contains(prof.Name))
                            {
                                theRadio.CreateMICProfile(prof.Name);
                                str += "CreateMICProfile_";
                            }
                            theRadio.ProfileMICSelection = prof.Name;
                            str += "ProfileMICSelection";
                        }), str, true);
                    }
                    break;
                default:
                    Tracing.TraceLine("SelectProfile:not valid " + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Error);
                    rv = false;
                    break;
            }
            return rv;
        }

        // ── Mic-profile accessors (Track F, 2026-08-16) ──
        //
        // The radio owns its mic profiles — created, stored and autosaved on
        // the radio itself, shared with every other client. JJ Flexible's
        // microphone profiles REFERENCE these by name rather than copying
        // their contents (see Radios.MicrophoneProfile), so the app needs to
        // ask three small questions: what profiles exist, which is loaded,
        // and "load this one IF it exists". The existing SelectProfile
        // silently CREATES a missing mic profile before selecting it — right
        // for its original caller, wrong for applying a reference, where a
        // missing profile must be reported, never invented on someone's
        // radio.

        /// <summary>
        /// Mic-profile names the radio reports. Empty until the radio has
        /// answered the profile subscription.
        /// </summary>
        public List<string> MicProfileNames => theRadio?.ProfileMICList?.ToList() ?? new List<string>();

        /// <summary>The radio's currently loaded mic profile, or "".</summary>
        public string CurrentMicProfileName => theRadio?.ProfileMICSelection ?? "";

        /// <summary>
        /// Load a mic profile by name ONLY if the radio already has it.
        /// Returns false — and sends nothing — when it does not exist here,
        /// so the caller can say so instead of this class quietly creating a
        /// profile on somebody's radio.
        /// </summary>
        public bool SelectMicProfileIfPresent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var radio = theRadio;
            if (radio == null || !radio.ProfileMICList.Contains(name)) return false;
            q.Enqueue((FunctionDel)(() =>
            {
                theRadio.ProfileMICSelection = name;
            }), "SelectMicProfileIfPresent", true);
            return true;
        }

        // ── The silent-transmit check (Sprint 31 Track S, task #99) ──
        //
        // A radio whose mic-profile SELECTION is empty has no transmit-audio
        // DSP chain, so audio sent from this computer modulates nothing:
        // SC_MIC pins at -120 and the operator keys up into silence. It was
        // pcap-diffed against SmartSDR on the same 8600 on 2026-08-10 — every
        // command matched except the mic profile, "Default" there and empty
        // here. SmartSDR never lands in this state because it keeps "Default"
        // selected; JJ Flex could, because loading the JJRadioDefault GLOBAL
        // profile carries no mic profile with it.
        //
        // **This surface ANNOUNCES. It never writes.** ProfileMICSelection is
        // shared radio state — every client connected to the radio sees the
        // change, and an empty selection on somebody else's radio may be their
        // deliberate arrangement. Whether JJ Flex may repair it is the
        // ownership question (RadioConfig.RadioOwnership); saying the failure
        // out loud needs no permission from anyone.
        //
        // Why it stayed invisible for so long: a correctly set-up radio cannot
        // detect it. Noel's own 8600 has a mic profile selected, so his working
        // rig is blind to the whole class of bug — which is precisely the
        // argument for announcing rather than waiting to reproduce it.

        /// <summary>
        /// True when this radio has answered its profile subscription and
        /// reports NO mic profile loaded — the silent-transmit failure.
        /// </summary>
        /// <remarks>
        /// A non-empty <c>ProfileMICList</c> is required, and it is doing real
        /// work: it is positive proof that the radio has answered, so a slow
        /// subscription can never be mistaken for the failure. A radio that
        /// reports no mic profiles at all is a genuinely different (and
        /// unverified) state — traced, never announced.
        /// </remarks>
        public bool MicProfileSelectionEmpty
        {
            get
            {
                var radio = theRadio;
                return radio != null
                    && IsConnected
                    && radio.ProfileMICList != null
                    && radio.ProfileMICList.Count > 0
                    && string.IsNullOrEmpty(radio.ProfileMICSelection);
            }
        }

        /// <summary>
        /// The mic profile this radio would most likely want if the operator
        /// asks for the empty selection to be filled: "Default" when the radio
        /// offers it (what SmartSDR keeps selected), otherwise the first one it
        /// lists. Empty string when there is nothing to pick. Naming the
        /// candidate is READ-ONLY; nothing here selects it.
        /// </summary>
        public string SuggestedMicProfileName
        {
            get
            {
                var radio = theRadio;
                var list = radio?.ProfileMICList;
                if (list == null || list.Count == 0) return "";
                return list.Contains("Default") ? "Default" : list[0];
            }
        }

        /// <summary>
        /// The connect-time spoken form, sized to the operator's verbosity.
        /// Spoken at Critical so it survives "speech off" — an operator who
        /// cannot be heard on the air is the enum's own definition of a
        /// safety message — which is exactly why the off-level form is the
        /// shortest one that still carries the consequence.
        /// </summary>
        public static string SilentTxSpokenWarning(VerbosityLevel verbosity) =>
            // Noel's wording, 2026-08-19: "audio from your computer will [not] be
            // transmitted using your radio" in place of "will not go out". It
            // names both halves of the path — the computer that produced the
            // audio and the radio that is supposed to send it — so an operator
            // hearing this for the first time learns where the break is, not
            // just that there is one. "Your", not "this": the house voice.
            // The three tiers live in the store as a ladder: audio.silent_tx.warning.
            Lexicon.Get("audio.silent_tx.warning", verbosity);

        /// <summary>
        /// The re-readable form, for a status line the operator can arrow
        /// through at their own pace. Null when there is nothing wrong — the
        /// caller shows the line only when this returns text, so a healthy
        /// radio never grows a warning it has to dismiss.
        /// </summary>
        public string? SilentTxMicProfileAdvisory()
        {
            if (!MicProfileSelectionEmpty) return null;
            string pick = SuggestedMicProfileName;
            return Lexicon.Get("audio.silent_tx.advisory")
                 + (string.IsNullOrEmpty(pick)
                        ? ""
                        : Lexicon.Get("audio.silent_tx.advisory_suggestion", ("pick", pick)));
        }

        /// <summary>
        /// Run the check and, when the failure is present, say so once. Called
        /// from <see cref="GetProfileInfo"/>, which is the only moment the app
        /// reliably knows the radio's profile answers have landed.
        /// </summary>
        /// <remarks>
        /// The waits are ordered so that a healthy radio pays almost nothing:
        /// the first returns as soon as the profile list arrives (already true
        /// by this point on every radio observed), and the second returns the
        /// instant a selection is seen. Only the genuine failure case waits out
        /// the full settle window, and on that radio the wait is worth it.
        /// </remarks>
        private void CheckMicProfileForSilentTx()
        {
            if (theRadio == null) return;

            if (!await(() => theRadio != null && theRadio.ProfileMICList != null
                             && theRadio.ProfileMICList.Count > 0, 1500))
            {
                // Either the subscription has not answered or this radio truly
                // lists no mic profiles. Both are unverified states and neither
                // is the pcap-confirmed failure, so this is for the trace file
                // and no further.
                Tracing.TraceLine(
                    "SilentTxCheck: no mic profiles reported within the settle window — "
                    + "not announcing (this is not the confirmed empty-selection failure).",
                    TraceLevel.Warning);
                return;
            }

            // A selection arriving at any point inside the window means the
            // radio is healthy; only a window that expires still empty is the
            // failure.
            if (await(() => theRadio == null
                            || !string.IsNullOrEmpty(theRadio.ProfileMICSelection), 1500))
            {
                return;
            }

            var radio = theRadio;
            if (radio == null) return;

            // Ownership decides whether we repair it or only report it.
            //
            // Noel ruled 2026-08-19: "yes on silent fix to a known radio." So on
            // a radio the operator has marked as theirs, JJ Flex loads the
            // profile itself rather than making them press a button to fix a
            // failure they did not cause. On any other radio — including one
            // never answered for — it still only speaks, because the selection
            // is SHARED state and an empty one on somebody else's radio may be
            // their deliberate arrangement.
            //
            // SILENT MEANS WITHOUT ASKING, NOT WITHOUT SAYING. The repair is
            // still announced, because the alternative is JJ Flex changing a
            // setting on the operator's radio and never mentioning it — which
            // is the shape of thing this project exists not to do. What the
            // ownership answer buys is the absence of a QUESTION, not the
            // absence of information.
            var ownership = RadioConfig.OwnershipOf(radio.Serial);
            var candidate = SuggestedMicProfileName;

            if (ownership == RadioOwnership.Mine && !string.IsNullOrEmpty(candidate))
            {
                // SelectMicProfileIfPresent refuses to CREATE a profile — it
                // only selects one the radio already lists — so the worst case
                // here is that nothing happens and the warning still stands.
                bool applied = SelectMicProfileIfPresent(candidate);

                Tracing.TraceLine(
                    "SilentTxCheck: mic profile selection is EMPTY on a radio marked MINE. "
                    + $"Loading '{candidate}' without asking (Noel's ruling 2026-08-19). "
                    + $"applied={applied}. Radio offers: "
                    + string.Join(", ", radio.ProfileMICList),
                    TraceLevel.Warning);

                if (applied)
                {
                    if (SuppressSpeech) return;
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("audio.silent_tx.repaired", ("candidate", candidate)),
                        Speech.SpeechIntent.Queue, VerbosityLevel.Critical);
                    return;
                }

                // The write did not take. Fall through and warn — an operator
                // told "I fixed it" when nothing changed is worse off than one
                // simply told it is broken.
                Tracing.TraceLine(
                    "SilentTxCheck: the repair did not apply — falling back to the warning.",
                    TraceLevel.Error);
            }
            else
            {
                Tracing.TraceLine(
                    "SilentTxCheck: mic profile selection is EMPTY. Transmit audio from this "
                    + "computer will not modulate. Radio offers: "
                    + string.Join(", ", radio.ProfileMICList)
                    + $". Ownership={ownership} — ANNOUNCING ONLY, nothing is written to the "
                    + "radio. Repair is operator-initiated on any radio not marked mine.",
                    TraceLevel.Warning);
            }

            if (SuppressSpeech) return;

            // Alarm first, then the sentence — the same order DiagnosticOffer
            // uses, and for the same reason: the tone tells the operator that
            // what follows is not routine, so the sentence gets listened to
            // rather than filed with the rest of the connect chatter. Not on
            // the repair branch above: that one reports a fixed condition, and
            // an alarm for something already handled teaches the operator to
            // ignore alarms. Null when the WPF layer has not started — silence,
            // not a crash, on a connect path.
            ScreenReaderOutput.PlayWarningAlarmEarcon?.Invoke();

            // Queued, not interrupting: this is the tail of the connect series,
            // and cutting off "Connected to ..." to deliver it would cost the
            // operator the message they were actually waiting for.
            ScreenReaderOutput.Speak(
                SilentTxSpokenWarning(ScreenReaderOutput.CurrentVerbosity),
                Speech.SpeechIntent.Queue,
                VerbosityLevel.Critical);
        }

        // ── The radio's own save-on-change concept (Sprint 32 Track H, #117) ──
        //
        // READ ONLY, ON PURPOSE. This is deliberately a getter with no setter.
        //
        // The radio has an autosave concept of its own and REPORTS it: FlexLib
        // parses "radio auto_save=1|0" into Radio.ProfileAutoSave. That answers
        // the first question — whether the radio has the feature at all — from
        // the wire rather than from a guess, and it answers it without writing
        // anything to a radio that may have other clients on it.
        //
        // What it does NOT answer is what the radio actually DOES when the flag
        // is on: which profile is written, at what moment, and whether a second
        // MultiFlex client's state is folded in. Those are radio-side semantics.
        // FlexLib's setter is one line that sends a command, so no amount of
        // reading our source or theirs can tell us; it needs a bench session.
        // Until it has had one, nothing here turns it on.
        //
        // Note also that FlexLib carries TWO ways to send this and they do not
        // agree. Radio.ProfileAutoSave sends `profile autosave on|off`
        // unquoted, and is the half wired to the status parser. The older
        // Radio.AutoSaveProfile(string) sends `profile autosave "<state>"`,
        // quoted, with no status handling and no caller anywhere. Whether the
        // radio accepts the quoted form is itself unverified — so if autosave is
        // ever driven from here, drive it through the property.

        /// <summary>
        /// What the radio reports about its own profile autosave setting, or
        /// null when there is no radio to ask. Never written by this
        /// application.
        /// </summary>
        public bool? RadioProfileAutoSave => theRadio?.ProfileAutoSave;

        /// <summary>
        /// Save a global profile.
        /// </summary>
        /// <param name="p">the profile</param>
        /// <param name="immediately">(optional)true to save immediately, default is false</param>
        public bool SaveProfile(Profile_t p, bool immediately = false)
        {
            Tracing.TraceLine("SaveProfile:" + p.ToString(), TraceLevel.Info);
            bool commandDone = false;
            bool rv = false;
            if (p.ProfileType == ProfileTypes.global)
            {
                q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.SaveGlobalProfile(p.Name);
                        commandDone = true;
                    }), "save global", true);
                if (immediately)
                {
                    // await the command.
                    await(() =>
                    {
                        return commandDone;
                    }, 3000);
                }
                rv = true;
                // no need to save the newGlobalProfile.
                if (p.Name == newGlobalProfile) newGlobalProfile = null;
            }
            return rv;
        }

        // ── The one automatic write that already existed, and its one gap ──
        //
        // Sprint 33 Track K found this while auditing #117 and it is worth
        // stating plainly, because it is the only path in the application that
        // writes a global profile without anybody pressing anything.
        //
        // What arms it: GetProfileInfo, on connect, looks for the operator's
        // DEFAULT global profile on the radio. If the radio has it, it is
        // loaded. If the radio does NOT have it, the operator has named a
        // profile that does not exist yet, so newGlobalProfile records the name
        // and Dispose creates it on the way out. That is create-on-first-use,
        // it only ever writes a name the radio did not already have, and it
        // therefore cannot overwrite an existing profile. Good.
        //
        // THE GAP IS THE CONTENTS, NOT THE NAME. The profile is created from
        // whatever the station looks like at teardown, and under MultiFlex that
        // includes slices belonging to another operator who is still connected.
        // Nothing is overwritten today — but the operator's default profile is
        // now a snapshot of somebody else's station, and it gets loaded on every
        // subsequent connect. The damage is silent, permanent until noticed, and
        // arrives disguised as the feature working.
        //
        // So it takes the same refusal the operator-facing save takes. On a
        // single-operator station — the normal case, and the case this was
        // written for — behaviour is unchanged. The feature is not removed; it
        // is prevented from capturing a station it cannot describe correctly.
        private bool saveNewGlobalProfile()
        {
            Tracing.TraceLine("saveNewGlobalProfile", TraceLevel.Info);
            bool rv = false;

            if (!string.IsNullOrEmpty(newGlobalProfile) && !OnlyStation)
            {
                Tracing.TraceLine(
                    "saveNewGlobalProfile:skipped, another operator is connected; "
                    + "would have created " + newGlobalProfile
                    + " from a station that is not solely ours", TraceLevel.Warning);
                return false;
            }

            List<Profile_t> crnt = GetProfilesByType(ProfileTypes.global, GetDefaultProfiles());
            foreach (Profile_t p in crnt)
            {
                if (!string.IsNullOrEmpty(newGlobalProfile) && (p.Name == newGlobalProfile))
                {
                    SaveProfile(p, true);
                    rv = true;
                    break;
                }
            }
            // Don't save other profiles.
            return rv;
        }

        // ══ Saving the station layout: the one-step verb (Sprint 33 Track K) ══
        //
        // #117 and #59. Sprint 32 Track H established that the transport works,
        // un-stubbed the dialog's Add and Update verbs, and added the spoken
        // receipt that tells the operator a slice change is provisional. What
        // it did NOT close is the half Noel named himself: "I don't know ...
        // what I need to do in JJ Flexible to get it to stick in the radio."
        //
        // He owns the radio and could not name the procedure, which is the
        // finding. The procedure exists — open Radio ▸ Profiles, find the
        // global entry matching the profile the radio currently has loaded,
        // select it, press Save — and every step of it requires knowing
        // something the receipt does not say. A receipt that names a thing the
        // operator cannot find is a receipt for a dead end.
        //
        // So this is the verb that procedure was missing: save the global
        // profile the radio ALREADY HAS LOADED, under the name it already has,
        // without asking the operator to identify it first. That is what every
        // other radio means by "it saves stuff when you turn it off", minus the
        // automatic part, which is deliberately not here — see below.
        //
        // WHY THE NAME COMES FROM THE RADIO AND NOT FROM THE OPERATOR'S LIST.
        // ProfileGlobalSelection is the radio's own report of which global
        // profile is loaded. The operator's list is a list of names the
        // operator cares about and may be empty, may be stale, and under
        // MultiFlex may not describe what is loaded at all — another client can
        // have selected a different profile since we connected. Saving to the
        // loaded name is the only reading of "save this" that cannot silently
        // write over a profile the operator was not looking at.

        /// <summary>
        /// The global profile the radio currently has loaded, or null when
        /// there is no radio or the radio has not reported a selection.
        /// </summary>
        public string CurrentGlobalProfileName
        {
            get
            {
                string name = theRadio?.ProfileGlobalSelection;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }

        /// <summary>
        /// Set when this application changed the station's slice set during
        /// this connection, and NOT cleared by the spoken receipt. Distinct
        /// from the receipt's own flag on purpose: the receipt fires once per
        /// settled change and clears itself, whereas this has to survive until
        /// disconnect so the save offer can ask "did anything actually change?"
        /// and stay silent when the answer is no.
        /// </summary>
        public bool OperatorChangedStationThisSession { get; private set; }

        /// <summary>
        /// Why the station layout cannot be saved right now, or null when it
        /// can. A reason string rather than a bool because every caller here
        /// has to TELL the operator — a save verb that silently does nothing is
        /// the defect this track exists to close, not a pattern to repeat.
        /// </summary>
        public string StationLayoutSaveBlocker()
        {
            if (theRadio == null || !IsConnected)
                return "There is no radio connected to save to.";

            // The MultiFlex refusal, and it is the load-bearing one.
            //
            // A global profile is STATION state: one per radio, shared by
            // everyone who connects. Saving it captures the whole station as it
            // looks at this instant — including slices belonging to another
            // operator who is on the radio right now and did not ask for their
            // layout to be written into the owner's profile. There is no way to
            // save "only my part", because the radio has no such concept.
            //
            // Note the fail-safe direction: OnlyStation is false until the GUI
            // client list has been parsed at least once, so an early call
            // refuses rather than guesses. Refusing costs the operator one
            // retry; guessing wrong costs somebody their layout.
            if (!OnlyStation)
                return "Another operator is connected to this radio. "
                     + "Saving now would store their setup as well as yours.";

            if (CurrentGlobalProfileName == null)
                return "This radio has no global profile loaded, so there is "
                     + "nothing to save into. Use Radio, then Profiles, to make one.";

            return null;
        }

        /// <summary>
        /// Save the station layout into the global profile the radio currently
        /// has loaded. Returns null on success, or the reason it did not happen.
        /// Writes to the radio, and only ever when something asked it to —
        /// there is no caller on any automatic path.
        /// </summary>
        public string SaveCurrentStationLayout()
        {
            string blocker = StationLayoutSaveBlocker();
            if (blocker != null)
            {
                Tracing.TraceLine(
                    "SaveCurrentStationLayout:refused:" + blocker, TraceLevel.Info);
                return blocker;
            }

            string name = CurrentGlobalProfileName;
            Tracing.TraceLine(
                "SaveCurrentStationLayout:saving global profile " + name, TraceLevel.Info);

            // Default:false — this is the radio's loaded profile, and marking it
            // the operator's default is a separate decision they did not make
            // by pressing Save.
            // HOW MUCH THIS ACTUALLY CONFIRMS — read before trusting the
            // receipt this returns to.
            //
            // SaveProfile(immediately: true) waits for the queued command to be
            // ISSUED, not for the radio to acknowledge that it stored anything.
            // Its false return means only "that was not a global profile",
            // which cannot happen here. So a null return from this method means
            // "the save command went out", and the announcement built on it is
            // making a slightly stronger claim than the evidence supports.
            //
            // Compare DeleteProfile a few hundred lines up, which DOES confirm:
            // it awaits the name disappearing from ProfileGlobalList and
            // reports honestly when it does not. Save has no equivalent
            // readback in FlexLib today — ProfileGlobalList still contains the
            // name whether or not the contents were rewritten, and nothing
            // exposes a modified time.
            //
            // Flagged for the bench rather than guessed at: if the radio does
            // emit something observable on a successful global save, this is
            // where to await it, and the receipt wording should follow whatever
            // that turns out to allow. Until then the failure mode is a save
            // that is announced and did not happen, which is exactly the class
            // of thing this application says it does not do.
            if (!SaveProfile(new Profile_t(name, ProfileTypes.global, false), true))
                return "The radio did not accept the save.";

            // The layout on the radio now matches what the operator has, so the
            // offer has nothing left to ask about.
            OperatorChangedStationThisSession = false;
            return null;
        }

        // ══ The disconnect offer, and why it is OFF by default ══
        //
        // Noel, raising it and doubting it in the same breath: "We could also
        // have a setting that if there's been a change to the radio, it could
        // offer to save the profile. I'm not sure I'd do this, but generally,
        // if you tune the radio or any radio, when you turn it off, it saves
        // stuff."
        //
        // Both halves of that are right, which is why this is a SETTING and why
        // it ships off. The expectation is real — every other radio remembers
        // what you did to it, and an operator who releases a slice and finds it
        // back tomorrow concludes the application is broken. But Sprint 32
        // Track H considered a disconnect prompt and deliberately shipped a
        // spoken notification instead, on reasoning that still holds:
        //
        //   1. Disconnect is not power-off. A networked radio keeps running.
        //   2. Under MultiFlex somebody else may still be on it.
        //   3. A prompt that fires whether or not anything changed gets
        //      dismissed reflexively — and a prompt trained to be dismissed is
        //      worse than none, because it creates the belief that the operator
        //      was asked.
        //
        // This does not overturn that. Track H's notification remains what
        // every operator gets. What this adds is the switch Noel described, for
        // the operator who wants radio-like behaviour and says so — and the
        // gates below answer objections 2 and 3 outright rather than accepting
        // them. It never fires when another operator is connected, and it never
        // fires when nothing changed.
        //
        // OFFER, NEVER SAVE. Nothing in this application saves a global profile
        // because a session ended. A global profile is station state shared by
        // everyone who connects, so an automatic write at disconnect would let
        // whoever happened to leave last redefine the station for everybody,
        // silently, having never been asked. The setting controls whether the
        // QUESTION is asked. The answer is always the operator's.

        /// <summary>
        /// Whether the operator has asked to be offered a station-layout save
        /// when they disconnect. App-level, one knob for every radio and every
        /// connection, in the shape of the other cross-layer settings on this
        /// class. Default false: the shipped behaviour stays Sprint 32's spoken
        /// notification until somebody deliberately turns this on.
        /// </summary>
        public static bool OfferStationSaveOnDisconnect { get; set; }

        /// <summary>
        /// Whether to offer a station-layout save right now. Every condition
        /// must hold, and each one is a separate objection being answered.
        /// </summary>
        public bool ShouldOfferStationLayoutSave()
        {
            // The operator asked for the question.
            if (!OfferStationSaveOnDisconnect) return false;

            // Something to ask ABOUT. An offer on a session where the operator
            // never touched the slice set is the reflexive-dismissal trap, and
            // this is the line that keeps it shut.
            if (!OperatorChangedStationThisSession) return false;

            // Saveable at all: connected, sole operator, a profile loaded to
            // save into. Carries the MultiFlex refusal, so the offer cannot
            // become the trap it exists to avoid.
            if (StationLayoutSaveBlocker() != null) return false;

            // Ownership, and this gate belongs on the OFFER but not on the menu
            // item. RadioConfig.MayCreateRadioSideState governs writes "the
            // operator did not individually request" — a proactive prompt at
            // disconnect is exactly that, while choosing Save Station Setup
            // from a menu is the individual request itself. On a radio never
            // declared as theirs, JJ Flexible does not raise the subject.
            var radio = theRadio;
            if (radio == null) return false;
            if (RadioConfig.OwnershipOf(radio.Serial) != RadioOwnership.Mine)
            {
                Tracing.TraceLine(
                    "ShouldOfferStationLayoutSave:not offering, radio "
                    + radio.Serial + " is not declared as the operator's",
                    TraceLevel.Info);
                return false;
            }

            return true;
        }

        // ── The operator's profile list: add and update (Sprint 32 Track H) ──
        //
        // These are the two verbs the Profiles dialog has been stubbed on since
        // it was written — OnAdd and OnUpdate spoke "not yet available" AFTER
        // the operator had navigated to the button and pressed it. Without them
        // Save can only overwrite the profile you are already on, so there was
        // no way to keep a four-slice layout and build a one-slice layout beside
        // it.
        //
        // WHAT THESE DO NOT DO IS AS IMPORTANT AS WHAT THEY DO. They do not
        // write anything to the radio. In Jim's design — which the WinForms
        // Profile dialog still implements and which this restores rather than
        // replaces — the operator's list is a list of NAMES the operator cares
        // about, with at most one default per type; the radio-side write is
        // <see cref="SaveProfile"/>, reached from the dialog's own Save button.
        // So "add a global profile then save it" is the Save As that was
        // missing, in two deliberate steps, and neither step can surprise
        // somebody else's radio.
        //
        // One default per type is enforced on the way in, matching the WinForms
        // dialog. The alternative — two profiles both claiming default — makes
        // GetProfileInfo's crnt[0] pick silently arbitrary at the next connect.

        /// <summary>
        /// Add a profile to the operator's list, clearing any other default of
        /// the same type when this one is marked default. Writes nothing to the
        /// radio. Returns false when the name is already taken for that type.
        /// </summary>
        public bool AddOperatorProfile(Profile_t p, List<Profile_t> lst = null)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Name)) return false;
            if (lst == null) lst = Callouts?.Profiles;
            if (lst == null) return false;
            if (GetProfileByName(p.Name, p.ProfileType, lst) != null) return false;

            Tracing.TraceLine("AddOperatorProfile:" + p.ToString(), TraceLevel.Info);
            if (p.Default) ClearDefaultOfType(p.ProfileType, lst);
            lst.Add(p);
            PersistOperatorProfiles();
            return true;
        }

        /// <summary>
        /// Replace an entry in the operator's list. Writes nothing to the
        /// radio: renaming here renames the operator's REFERENCE, and a profile
        /// that already exists on the radio keeps its own name until something
        /// saves under the new one.
        /// </summary>
        public bool UpdateOperatorProfile(
            Profile_t original, Profile_t replacement, List<Profile_t> lst = null)
        {
            if (original == null || replacement == null) return false;
            if (string.IsNullOrWhiteSpace(replacement.Name)) return false;
            if (lst == null) lst = Callouts?.Profiles;
            if (lst == null) return false;

            // The entry may be a rig profile the operator has never adopted —
            // GetRigProfiles hands back fresh objects that are not in the list.
            // Updating one of those ADOPTS it, which is the useful reading of
            // the verb and matches what the operator just described in the
            // dialog.
            int at = lst.IndexOf(original);
            if (at < 0)
            {
                Profile_t existing = GetProfileByName(
                    original.Name, original.ProfileType, lst);
                at = existing != null ? lst.IndexOf(existing) : -1;
            }

            // Refuse a rename that collides with a different existing entry.
            Profile_t clash = GetProfileByName(
                replacement.Name, replacement.ProfileType, lst);
            if (clash != null && (at < 0 || !ReferenceEquals(clash, lst[at])))
                return false;

            Tracing.TraceLine(
                "UpdateOperatorProfile:" + original.ToString() + " -> " + replacement.ToString(),
                TraceLevel.Info);
            if (replacement.Default) ClearDefaultOfType(replacement.ProfileType, lst);
            if (at >= 0) lst[at] = replacement;
            else lst.Add(replacement);
            PersistOperatorProfiles();
            return true;
        }

        /// <summary>Clear the default flag on every profile of one type.</summary>
        private void ClearDefaultOfType(ProfileTypes typ, List<Profile_t> lst)
        {
            foreach (Profile_t p in GetProfilesByType(typ, lst)) p.Default = false;
        }

        /// <summary>
        /// Ask the application to write the operator's record to disk. A no-op
        /// when nothing wired the callout, so the Radios layer never has to know
        /// how the operator's file is stored.
        /// </summary>
        private void PersistOperatorProfiles()
        {
            try { Callouts?.SaveOperator?.Invoke(); }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    "PersistOperatorProfiles:" + ex.Message, TraceLevel.Error);
            }
        }

        public bool DeleteProfile(Profile_t prof, List<Profile_t> lst = null)
        {
            Tracing.TraceLine("DeleteProfile:" + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Info);
            bool rv = false;
            bool profileGone = false;
            if (lst == null) lst = Callouts.Profiles;
            switch (prof.ProfileType)
            {
                case ProfileTypes.global:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteGlobalProfile(prof.Name);
                        // await the deletion.  profileGone is true if deleted.
                        if (!(profileGone = await(() => { return !theRadio.ProfileGlobalList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteGlobalProfile");
                    break;
                case ProfileTypes.tx:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteTXProfile(prof.Name);
                        // await the deletion.
                        if (!(profileGone = await(() => { return !theRadio.ProfileTXList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteTXProfile");
                    break;
                case ProfileTypes.mic:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteMICProfile(prof.Name);
                        // await the deletion.
                        if (!(profileGone = await(() => { return !theRadio.ProfileMICList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteMICProfile");
                    break;
                default:
                    Tracing.TraceLine("DeleteProfile:not valid " + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Error);
                    break;
            }

            // wait for the queued deletion.
            rv = await(() => { return profileGone; }, 1000);

            Profile_t p = GetProfileByName(prof.Name, prof.ProfileType);
            if (rv & (p != null))
            {
                lst.Remove(p);
                // The WinForms dialog wrote the operator's record after every
                // delete; the WPF one never did, so a deleted profile came back
                // on the next launch. (Sprint 32 Track H.)
                PersistOperatorProfiles();
            }
            return rv;
        }
        #endregion

        // multi-user region
        #region multiUser
        private int initialFreeSlices = -1;
        private int _TotalNumSlices;
        /// <summary>
        /// Total panadapters and slices on the radio.
        /// </summary>
        public int TotalNumSlices
        {
            get { return _TotalNumSlices; }
        }

        /// <summary>
        /// Maximum slices currently available (from FlexLib discovery — unreliable, may be 0).
        /// Prefer TotalMaxSlices for capacity checks.
        /// </summary>
        public int MaxSlices => theRadio?.MaxSlices ?? 0;

        /// <summary>
        /// Total maximum slices for this radio model (from hardware specs, always correct).
        /// FlexLib's MaxSlices reports available-remaining which can be 0 at startup
        /// when a profile loads all slots. This gives the true model capacity.
        /// </summary>
        public int TotalMaxSlices
        {
            get
            {
                string model = theRadio?.Model ?? string.Empty;
                return model switch
                {
                    "FLEX-6300" => 2,
                    "FLEX-6400" or "FLEX-6400M" => 2,
                    "FLEX-6500" => 4,
                    "FLEX-6600" or "FLEX-6600M" => 4,
                    "FLEX-6700" or "FLEX-6700R" => 8,
                    "FLEX-8400" or "FLEX-8400M" => 2,
                    "FLEX-8600" or "FLEX-8600M" => 4,
                    "AU-510" or "AU-510M" => 2,
                    "AU-520" or "AU-520M" => 4,
                    _ => theRadio?.MaxSlices > 0 ? theRadio.MaxSlices : 2 // safe fallback
                };
            }
        }

        internal List<Slice> mySlices = new List<Slice>();

        /// <summary>
        /// Tracks slice removals that have been enqueued but not yet processed.
        /// Prevents NewSlice() from seeing stale SliceList.Count on the UI thread
        /// before the queue thread processes the Close() call. (BUG-049 fix)
        /// </summary>
        private volatile int _pendingRemovals;

        /// <summary>
        /// number of Panadapters and slices for this radio instance.
        /// </summary>
        public int MyNumSlices
        {
            get {
                int rv;
                lock (mySlices)
                {
                    rv = mySlices.Count;
                }
                return rv;
            }
        }

        /// <summary>
        /// number of slices used by others.
        /// </summary>
        public int OtherNumSlices
        {
            get
            {
                return theRadio.SliceList.Count - MyNumSlices;
            }
        }

        // SliceState(int) and its SliceStates enum were deleted here (QB
        // Track L, 2026-08-07; Track J's find): zero callers, and the
        // positional mine/others/available classification lies under
        // MultiFlex, where our slices need not start at radio index 0.

        /// <summary>
        /// Add a pan and slice.
        /// </summary>
        public bool NewSlice()
        {
            // Use model-based TotalMaxSlices (always correct) instead of theRadio.MaxSlices
            // which reports available-remaining (can be 0 at startup when profile fills all slots).
            // Subtract _pendingRemovals to account for queued-but-not-yet-processed Close() calls.
            int effectiveCount = (theRadio?.SliceList.Count ?? 0) - _pendingRemovals;
            Tracing.TraceLine($"NewSlice: effective={effectiveCount} totalMax={TotalMaxSlices}", TraceLevel.Info);
            if (theRadio == null || effectiveCount >= TotalMaxSlices) return false;

            // Capture the current RX/TX slices by IDENTITY (radio index) —
            // the new slice may insert below them and shift their positions,
            // so a stored position replayed after the add can land on the
            // wrong slice. (QB Track J)
            int myRXSliceIndex = VFOToSliceIndex(RXVFO);
            int myTXSliceIndex = VFOToSliceIndex(TXVFO);
            mySliceAdded = false; // need to know when slice added.
            // #117: this application asked, so the resulting layout is
            // provisional and the operator gets told so once it settles.
            NoteOperatorChangedSliceSet();
            q.Enqueue((FunctionDel)(() =>
            {
                theRadio.RequestPanafall();
                if (await(() =>
                {
                    // await both slice and panadapter.
                    return mySliceAdded & (MyNumPanadapters == MyNumSlices);
                }, 3000))
                {
                    // restore VFOs by identity.
                    int rxVfo = SliceIndexToVFO(myRXSliceIndex);
                    if (rxVfo >= 0) RXVFO = rxVfo;
                    if (CanTransmit)
                    {
                        int txVfo = SliceIndexToVFO(myTXSliceIndex);
                        if (txVfo >= 0) TXVFO = txVfo;
                    }
                }
                else
                {
                    Tracing.TraceLine("NewSlice:counts don't match", TraceLevel.Error);
                }
            }));
            return true;
        }

        /// <summary>
        /// Remove a pan and slice by VFO position. Resolves the position to a
        /// Slice object immediately — the queued close targets that slice no
        /// matter how the list shifts before the queue runs. (QB Track J)
        /// </summary>
        /// <param name="id">VFO position</param>
        /// <returns>true if id valid and removable</returns>
        public bool RemoveSlice(int id)
        {
            return RemoveSlice(VFOToSlice(id));
        }

        /// <summary>
        /// Remove a pan and slice by identity. Refuses the active (RX) slice
        /// and the transmit slice.
        /// </summary>
        internal bool RemoveSlice(Slice slc)
        {
            if (slc == null) return false;
            // Can't remove the active or transmit slice.
            if ((slc == VFOToSlice(RXVFO)) | (CanTransmit & (slc == VFOToSlice(TXVFO)))) return false;

            Panadapter pan = slc.Panadapter;

            Tracing.TraceLine($"RemoveSlice:letter={slc.Letter} count={MyNumSlices}", TraceLevel.Info);
            mySliceRemoved = false;
            // #117: the release will succeed and will not survive disconnect.
            // Noted here, spoken once the set settles — so "release all extras"
            // still costs one receipt, not one per slice.
            NoteOperatorChangedSliceSet();
            _pendingRemovals++;
            q.Enqueue((FunctionDel)(() =>
            {
                slc.Close();
                pan.Close();
                _pendingRemovals--;
                if (!await(() => mySliceRemoved, 3000))
                {
                    Tracing.TraceLine("RemoveSlice:slice removal not confirmed within timeout", TraceLevel.Error);
                }
                else
                {
                    Tracing.TraceLine($"RemoveSlice:confirmed, new count={MyNumSlices}", TraceLevel.Info);
                }
            }));
            return true;
        }

        // ══ The slice vocabulary, and the provisional-change receipt ══
        //
        // Sprint 32 Track H, #58 and #117. Two separate problems that turn out
        // to share one trigger, so they share one debounce.
        //
        // #58 is the CW vocabulary Noel specified on 2026-08-19: a CENSUS of
        // "<used>/<total>" when the slice set changes, and "SL <letter> <mode>"
        // when the operator moves to a slice or changes its mode. Both formats
        // are approved copy and are not to be reworded.
        //
        // #117 is the persistence receipt. Releasing a slice succeeds, sounds
        // successful, and is silently discarded at disconnect, because THE
        // SLICES ARE NOT OURS: this application contains zero slice-creation
        // calls, and the radio restores its slice layout from its own global
        // profile on the next connect. Slice create and delete work correctly —
        // the defect is that nothing tells the operator the change is
        // provisional.
        //
        // The receipt is deliberately NOT a prompt at disconnect, for two
        // reasons that were both worked out before any code was written. First,
        // the disconnect moment is not the power-off moment: a networked radio
        // does not power down when a client leaves, and under MultiFlex another
        // operator may still be on it, so an automatic save can capture
        // somebody else's layout and overwrite a global profile with a state
        // this operator never chose. Second, an unsaved-changes prompt fires
        // whether or not anything meaningful changed, so operators learn to
        // dismiss it reflexively — and a prompt trained to be dismissed is
        // worse than no prompt, because it creates the belief that the operator
        // was asked.
        //
        // Notify where there is context; prompt only where there is a real
        // choice. This is the notification.

        /// <summary>
        /// The trailing clause spoken after a provisional slice change. The
        /// operator has just heard the change itself from whichever surface
        /// they used ("Slice D released, 3 active"), so this adds only what
        /// none of those surfaces could know.
        /// </summary>
        private static string ProvisionalSliceChangeReceipt =>
            Lexicon.Get("settings.slice.provisional_change_receipt");

        /// <summary>
        /// How long the slice set must be quiet before it counts as settled.
        /// One connect delivers several slices in a burst and
        /// <see cref="GetProfileInfo"/> can add more over the following seconds,
        /// so the timer RESTARTS on every change and fires only after the last
        /// one. That is what turns four arrivals into one census.
        /// </summary>
        private const int SliceSetSettleMs = 1500;

        private System.Threading.Timer _sliceSettleTimer;
        private readonly object _sliceSettleLock = new object();

        /// <summary>
        /// True while the slice set is churning. Suppresses the per-slice
        /// announcement, so a bulk replay produces the census and nothing else.
        /// This is what replaces the old ActiveSlice guard: the announcement is
        /// silenced because the EVENT is bulk, not because one slice was picked
        /// out of it as representative.
        /// </summary>
        private volatile bool _sliceSetChurning;

        /// <summary>
        /// Set when this application asked for the slice change, cleared when
        /// the receipt has been given. Slices arriving from the radio's own
        /// profile on connect must never produce the receipt — nothing is
        /// provisional about the layout the radio just restored.
        /// </summary>
        private volatile bool _operatorChangedSliceSet;

        /// <summary>
        /// Note that the slice set has changed and (re)start the settle timer.
        /// Called from the slice added and removed handlers.
        /// </summary>
        private void NoteSliceSetChanged()
        {
            _sliceSetChurning = true;
            lock (_sliceSettleLock)
            {
                if (_sliceSettleTimer == null)
                {
                    _sliceSettleTimer = new System.Threading.Timer(
                        _ => OnSliceSetSettled(), null,
                        SliceSetSettleMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    try
                    {
                        _sliceSettleTimer.Change(
                            SliceSetSettleMs, System.Threading.Timeout.Infinite);
                    }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private void OnSliceSetSettled()
        {
            _sliceSetChurning = false;
            bool operatorDid = _operatorChangedSliceSet;
            _operatorChangedSliceSet = false;

            // Slices also disappear on the way out. Saying anything then is
            // noise at best and a crash at worst.
            if (Disconnecting || theRadio == null) return;

            AnnounceSliceCensus();
            if (operatorDid) SpeakProvisionalSliceChangeReceipt();
        }

        /// <summary>
        /// The census: "SL &lt;used&gt;/&lt;total&gt;" in CW. Three slices open on
        /// a four-slice radio sends "SL 3/4"; a full radio sends "SL 4/4".
        /// </summary>
        /// <remarks>
        /// Used-over-total rather than a bare free count, recorded so it is not
        /// re-litigated: THE DENOMINATOR VARIES BY MODEL — two slices on a 6300
        /// or 8400, four on a 6600 or 8600, eight on a 6700. "One free" means
        /// something very different on a 6700 than on an 8600 and forces the
        /// operator to remember which radio they are on to interpret it. "3/4"
        /// carries both numbers in one token, makes "4/4" read unmistakably as
        /// full, and leaves the free count trivially derivable.
        ///
        /// The total comes from <c>Radio.MaxSlices</c>, the radio's own report
        /// of its ceiling — NOT from <c>AvailableSlices</c>, which is remaining
        /// capacity and is the wrong number for a denominator. The one guard is
        /// for a radio that has not yet answered with its ceiling: rather than
        /// send "3/0" we fall back to <see cref="TotalMaxSlices"/>, the model
        /// table, which is right for every model listed there.
        ///
        /// The numerator matches the denominator's scope. MaxSlices is a
        /// property of the RADIO, so the count of slices in use has to be the
        /// radio's too — under MultiFlex a fraction mixing "mine" over "the
        /// radio's" would be incoherent.
        /// </remarks>
        internal void AnnounceSliceCensus()
        {
            var radio = theRadio;
            if (radio == null) return;

            int total = radio.MaxSlices > 0 ? radio.MaxSlices : TotalMaxSlices;
            int used = radio.SliceList?.Count ?? 0;
            if (total <= 0) return;

            Tracing.TraceLine(
                $"AnnounceSliceCensus:{used}/{total} (radio.MaxSlices={radio.MaxSlices} "
                + $"model={TotalMaxSlices} mine={MyNumSlices})", TraceLevel.Info);

            // SPOKEN CENSUS ADDED 2026-08-20 BY NOEL. Until now this fact
            // existed ONLY in Morse, and CW notifications are off by default —
            // so an operator who had not turned them on was never told their
            // slice count at all. The speech half is therefore NOT gated on the
            // CW settings; it stands on its own and the CW below is the extra.
            //
            // Two wordings, his: the fuller form is Chatty, and Terse gets the
            // compressed one. Same fact, and the level decides how much of the
            // sentence you have to listen to.
            //
            // Terse level (not Critical): the enum documents Terse as value
            // changes and band/mode, which is exactly what a slice count is.
            // THE ACTIVE SLICE RIDES ALONG, 2026-08-20, Noel. It says where you
            // have landed: the count alone tells you how many slices exist and
            // nothing about which one you are on or what it is doing.
            //
            // Read from the radio's own `active` slice status rather than
            // inferred, because a global profile can restore a non-A slice as
            // active — so this is real information, not a constant.
            //
            // WHAT THIS DOES NOT SOLVE, recorded so it is not miscredited later:
            // #59 was four slices on connect with slice D in FM, and slice A —
            // USB — was the active one. So an active-slice announcement would
            // have said "SL A USB" and the FM on D would have stayed exactly as
            // hidden as it was. The non-active slices remain unannounced, and
            // that gap is still open.
            Slice active = null;
            lock (mySlices)
                foreach (var s in mySlices) if (s != null && s.Active) { active = s; break; }

            string letter = active?.Letter;
            string mode = active?.DemodMode;
            bool haveActive = !string.IsNullOrEmpty(letter) && !string.IsNullOrEmpty(mode);

            if (!SuppressSpeech)
            {
                // Chatty gets the fuller sentence AND the active slice; Terse
                // gets the count alone. A transient no-active-slice state during
                // connect drops the clause silently rather than announcing an
                // absence nobody asked about.
                string spoken;
                if (ScreenReaderOutput.CurrentVerbosity >= VerbosityLevel.Chatty)
                {
                    spoken = (used == 1 ? Lexicon.Get("settings.slice.census_chatty_one",
                        ("used", used), ("total", total)) : Lexicon.Get("settings.slice.census_chatty_many",
                        ("used", used), ("total", total)));
                    if (haveActive)
                        spoken += Lexicon.Get("settings.slice.census_active_suffix",
                            ("letter", letter), ("mode", mode));
                }
                else
                {
                    spoken = (total == 1 ? Lexicon.Get("settings.slice.census_terse_one",
                        ("used", used), ("total", total)) : Lexicon.Get("settings.slice.census_terse_many",
                        ("used", used), ("total", total)));
                }
                ScreenReaderOutput.Speak(spoken, VerbosityLevel.Terse);
            }

            if (!ScreenReaderOutput.CwNotificationsEnabled) return;
            if (!ScreenReaderOutput.CwModeAnnounceEnabled) return;
            if (ScreenReaderOutput.PlayCwText == null) return;
            // SendCwText rather than the delegate directly (#153): it is the one
            // point where CW text reaches the notifier, so it is where the
            // repeat history is recorded. Calling the delegate would still make
            // the sound and would silently leave this message unrepeatable.
            //
            // "SL" PREFIX ADDED 2026-08-20 BY NOEL. A bare "4/4" is a fraction
            // with no subject: you hear it and have to already know what was
            // being counted. Prefixing makes the census self-describing, and it
            // matches AnnounceSliceIdentity's "SL A USB" so both halves of the
            // slice vocabulary open the same way. Two characters buys a message
            // that stands on its own.
            //
            // The active slice is appended as a SECOND "SL" group rather than
            // running on — "SL 4/4 SL A USB" reads as two facts, where
            // "SL 4/4 A USB" would read as one confused one.
            //
            // Sent as ONE string, not two calls: SendCwText records a repeat
            // history entry per call (#153), so two calls would mean pressing
            // Ctrl+J, E twice to hear back a single connect. One string, one
            // entry, whole summary.
            string cw = $"SL {used}/{total}";
            if (haveActive) cw += $" SL {letter} {mode}";
            _ = ScreenReaderOutput.SendCwText(cw);
        }

        /// <summary>
        /// "SL &lt;letter&gt; &lt;mode&gt;" in CW — an identity plus a state, for
        /// a single slice the operator just arrived on or just re-moded.
        /// Silent during a bulk change, where the census speaks instead.
        /// </summary>
        private void AnnounceSliceIdentity(Slice s)
        {
            if (s == null) return;
            if (_sliceSetChurning) return;
            if (!ScreenReaderOutput.CwNotificationsEnabled) return;
            if (!ScreenReaderOutput.CwModeAnnounceEnabled) return;
            var play = ScreenReaderOutput.PlayCwText;
            if (play == null) return;

            string letter = s.Letter;
            string mode = s.DemodMode;
            if (string.IsNullOrEmpty(letter) || string.IsNullOrEmpty(mode)) return;

            // See AnnounceSliceCensus — SendCwText is what puts this in the
            // repeat history (#153).
            _ = ScreenReaderOutput.SendCwText($"SL {letter} {mode}");
        }

        /// <summary>
        /// Say once, after a settled operator-initiated slice change, that the
        /// change is provisional. Queued rather than interrupting: the surface
        /// the operator used has already spoken the change itself, and this is
        /// the second half of that sentence, not a replacement for it.
        /// </summary>
        private void SpeakProvisionalSliceChangeReceipt()
        {
            if (SuppressSpeech) return;
            ScreenReaderOutput.Speak(
                ProvisionalSliceChangeReceipt,
                Speech.SpeechIntent.Queue,
                VerbosityLevel.Terse);
        }

        /// <summary>
        /// Record that THIS application asked for the slice set to change, so
        /// the settle handler knows to give the persistence receipt. Called by
        /// <see cref="NewSlice"/> and <see cref="RemoveSlice(Slice)"/> — never
        /// by the arrival handlers, which also fire for the radio's own
        /// profile restore on connect.
        /// </summary>
        private void NoteOperatorChangedSliceSet()
        {
            _operatorChangedSliceSet = true;

            // Sprint 33 Track K. The same trigger, recorded a second time with a
            // different lifetime: _operatorChangedSliceSet is consumed by the
            // next settled census and cleared, while this one has to last until
            // the operator disconnects, because that is when the save offer asks
            // whether there was anything to offer about. Set here rather than in
            // the arrival handlers for exactly the reason the flag above is —
            // slices restored by the radio's own profile on connect are not a
            // change this operator made, and must never make the offer appear.
            OperatorChangedStationThisSession = true;
        }

        /// <summary>
        /// true if can transmit (currently unused)
        /// </summary>
        public bool CanTransmit { get; internal set; }

        /// <summary>
        /// True if the only station.
        /// </summary>
        public bool OnlyStation { get; internal set; }

        public delegate void NoSliceErrorDel(object sender, string msg);
        /// <summary>
        /// No slice allocated to this instance.
        /// </summary>
        public event NoSliceErrorDel NoSliceError;
        private void raiseNoSliceError(string msg)
        {
            Tracing.TraceLine("raiseNoSliceError:" + msg, TraceLevel.Error);
            if (NoSliceError != null)
            {
                NoSliceError(this, msg);
            }
        }

        private bool _LocalPTT;
        /// <summary>
        /// True if local PTT, can only be set to true.
        /// </summary>
        public bool LocalPTT
        {
            get { return _LocalPTT; }
            set
            {
                if ((value != _LocalPTT) & value)
                {
                    _LocalPTT = value;
                    q.Enqueue((FunctionDel)(() => { theRadio.SetLocalPttForGuiClient(); }));
                }
            }
        }
        #endregion

        // region remote audio
        #region RemoteAudio
        // Note that here input and output refer to input and output from the rig.
        private JJPortaudio.Devices audioSystem;
        private JJPortaudio.Devices.Device remoteInputDevice, remoteOutputDevice;

        /// <summary>
        /// The rate the radio's own audio is decoded at. Fixed, and separate
        /// from the transmit rate below on purpose: this end of the link is
        /// the radio's to set, and an Opus decoder happily decodes any
        /// bitstream to whatever output rate it was built with.
        /// </summary>
        private const uint opusRxSampleRate = 48000;

        /// <summary>
        /// The sample rate the transmit encoder is built at, in hertz.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Track E, 2026-08-16 (#57, bandwidth adaptation). This was a
        /// hardcoded 48000 shared by both directions. Lowering it is the
        /// fallback for a link that cannot carry the full-rate stream — the
        /// frame duration stays 10 ms at every Opus rate, so the radio still
        /// receives the 100 frames a second it expects, and an Opus packet
        /// carries its own rate in the bitstream for the radio's decoder to
        /// follow.
        /// </para>
        /// <para>
        /// <b>It is a request, not a command.</b> The device gets the last word:
        /// <c>Audio.Open</c> settles the rate against the hardware before the
        /// encoder is built, so a 24 kHz request on a device that only does
        /// 48 kHz opens at 48 kHz and the encoder follows the stream, not the
        /// setting. In practice that means the lower rates bite under MME,
        /// which converts, and are usually refused under WASAPI, which does
        /// not. That is the same host-API trade as everything else in this
        /// track, which is why the setting lives next to the audio system in
        /// the Audio Devices dialog rather than off on its own.
        /// </para>
        /// <para>
        /// App-level state, like the PC output volume above: it describes this
        /// computer's link to the radio, not the rig, so it is a static backed
        /// by AudioOutputConfig and is in place before any radio connects.
        /// </para>
        /// </remarks>
        public const uint OpusTxSampleRateDefault = 48000;
        private static uint _opusTxSampleRate = OpusTxSampleRateDefault;

        /// <summary>
        /// The persisted transmit-rate setting. Anything that is not a rate
        /// Opus can encode is refused and the default kept — silently encoding
        /// at a rate the codec has no mode for is the exact defect the rate
        /// negotiation work exists to prevent.
        /// </summary>
        public static uint OpusTxSampleRateSetting
        {
            get { return _opusTxSampleRate; }
            set
            {
                if (!JJPortaudio.JJAudioStream.IsOpusRate(value))
                {
                    Tracing.TraceLine("FlexBase.OpusTxSampleRateSetting: " + value
                        + " Hz is not a rate Opus can encode; keeping "
                        + _opusTxSampleRate + " Hz", TraceLevel.Error);
                    return;
                }
                _opusTxSampleRate = value;
            }
        }

        /// <summary>
        /// Resolve one end of the PC-audio path, speaking whenever the answer is
        /// not the one the operator configured.
        /// </summary>
        /// <param name="type">input (computer microphone) or output (radio audio playback)</param>
        /// <param name="role">word for this end, used in speech: "microphone" / "playback"</param>
        /// <returns>a usable device, or null when nothing can be used.</returns>
        /// <remarks>
        /// QB Track B, 2026-08-07. Three outcomes, each audible except the one
        /// that means nothing changed:
        ///   - saved device present  → silent. A re-plug into a different USB
        ///     port still resolves here, because identity is name plus host API,
        ///     never the PortAudio index.
        ///   - saved device missing, or never configured → adopt the system
        ///     default, say so, keep going. Blocking the connect would punish the
        ///     ordinary "docked laptop left the USB hub" case.
        ///   - no device at all → say so and stop. The old code left only a trace
        ///     line here, which is how a dead audio path came to look like a
        ///     radio problem.
        /// </remarks>
        private JJPortaudio.Devices.Device ResolveAudioDevice(
            JJPortaudio.Devices.DeviceTypes type, string role)
        {
            var dev = audioSystem.GetConfiguredDevice(type);
            if (dev != null) return dev;

            bool wasConfigured = audioSystem.IsSavedDeviceMissing(type, out string savedName);

            var fallback = audioSystem.AdoptSystemDefault(type);
            if (fallback == null)
            {
                Tracing.TraceLine("ResolveAudioDevice:" + type + " no device available", TraceLevel.Error);
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.device.no_usable", ("role", role)),
                    VerbosityLevel.Critical, true);
                return null;
            }

            Tracing.TraceLine("ResolveAudioDevice:" + type + " fell back to system default "
                + fallback.Name, TraceLevel.Error);
            ScreenReaderOutput.Speak(
                (wasConfigured ? Lexicon.Get("audio.device.saved_missing_fallback",
                    ("role", role), ("savedName", savedName), ("fallbackName", fallback.Name)) : Lexicon.Get("audio.device.none_chosen_fallback",
                    ("role", role), ("savedName", savedName), ("fallbackName", fallback.Name))),
                VerbosityLevel.Critical, true);
            return fallback;
        }

        class audioChannelData
        {
            public string Name;
            private object radioStream; // the radio's stream
            // OpusStream for output
            public RXRemoteAudioStream OpusChannel
            {
                get { return (RXRemoteAudioStream)radioStream; }
                set { radioStream = value; }
            }
            // opus input
            public TXRemoteAudioStream TXOpusChannel
            {
                get { return (TXRemoteAudioStream)radioStream; }
                set { radioStream = value; }
            }
            public bool IsOpus;
            public bool IsInput;
            public JJAudioStream PortAudioStream;
            public bool Started;
            public bool JustStarted; // used by opus to ignore initial data after start

            // audioChannel for Opus output
            public audioChannelData(RXRemoteAudioStream stream, string name)
            {
                stream.IsCompressed = true;
                OpusChannel = stream;
                Name = name;
                IsOpus = true;
                IsInput = false;
            }
            // audioChannel for Opus input
            public audioChannelData(TXRemoteAudioStream stream, string name)
            {
                TXOpusChannel = stream;
                Name = name;
                IsOpus = true;
                IsInput = true;
            }
        }
        private audioChannelData opusOutputChannel;
        private audioChannelData opusInputChannel;
        // False when the microphone stream failed to open. Without it every
        // key-up re-attempts a start that cannot succeed and waits a second for
        // the answer, on the remote-audio thread.
        private bool opusInputAvailable = false;
#if CWMonitor
        private Morse CWMon = null;
        private bool useCWMon { get { return (CWMon != null); } }
#endif

        // for Opus output
        private RXRemoteAudioStream rxStream = null;
        private void opusOutputStreamAddedHandler(RXRemoteAudioStream stream)
        {
            if (!myClient(stream.ClientHandle))
            {
                Tracing.TraceLine("opusOutputStreamAddedHandler:not mine", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("opusOutputStreamAddedHandler:mine", TraceLevel.Info);
                rxStream = stream;
            }
        }

        private TXRemoteAudioStream txStream = null;
        private void opusInputStreamAddedHandler(TXRemoteAudioStream stream)
        {
            Tracing.TraceLine("opusInputStreamAddedHandler:" + stream.ClientHandle + ' ' + stream.StreamID.ToString(), TraceLevel.Info);
            // Sprint 33 Track G: record what the radio agreed to, in its own
            // words. We encode Opus unconditionally, so a stream the radio did
            // not open as opus means every packet we send is being read as raw
            // PCM at the far end — silent transmit with no other symptom. That
            // was a two-day suspect precisely because nothing ever printed this
            // line; it is cheap, it runs once per stream, and it turns an
            // assumption about a radio-side default into an observation.
            Tracing.TraceLine("opusInputStreamAddedHandler:radio opened the TX stream with compression="
                + (stream.CompressionSetting ?? "(the radio sent no compression key)")
                + ", status line was \"" + (stream.LastStatusLine ?? "") + "\"",
                stream.IsCompressed ? TraceLevel.Info : TraceLevel.Error);
            if (!stream.IsCompressed)
            {
                Tracing.TraceLine("opusInputStreamAddedHandler:the radio did NOT open this stream as opus,"
                    + " but every transmit packet we send is Opus — expect silent transmit",
                    TraceLevel.Error);
            }
            txStream = stream;
        }

        /// <summary>
        /// What the radio said when it opened our transmit audio stream — its
        /// own word, unparsed. Empty when no stream is open.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This was already being observed and only ever written to a trace
        /// file.</b> Sprint 33 Track G added the observation because the
        /// alternative had cost two days: we encode Opus unconditionally, so a
        /// stream the radio opened as anything else means every packet we send
        /// is read as raw PCM at the far end — silent transmit with no other
        /// symptom, no error, and every setting on both sides looking correct.
        /// </para>
        /// <para>
        /// It is exposed here so the transmit chain analyzer can read it.
        /// Stage 7 of <c>tx-chain-rules.txt</c> was marked
        /// <c>not-observable</c> with the words "the radio's answer is held
        /// privately inside the app and is not published anywhere a check can
        /// read it" — true when written, and untrue from the moment Track G
        /// landed. An operator with silent transmit was being told the tool
        /// could not look at the very thing most likely to be wrong.
        /// </para>
        /// </remarks>
        public string TxStreamCompression
        {
            get
            {
                try { return txStream?.CompressionSetting ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>
        /// Whether the radio opened our transmit stream as Opus. Null when no
        /// transmit stream is open, which is a different answer from "no" and
        /// must stay distinguishable: no stream is a stage-6 problem, a stream
        /// opened uncompressed is a stage-7 problem, and sending an operator
        /// after the wrong one costs them the afternoon.
        /// </summary>
        public bool? TxStreamIsOpus
        {
            get
            {
                try { return txStream == null ? (bool?)null : txStream.IsCompressed; }
                catch { return null; }
            }
        }

        /// <summary>The radio's raw status line for the transmit stream, kept
        /// for the evidence block: a reader at Flex who distrusts our
        /// interpretation can read what their own radio actually said.</summary>
        public string TxStreamStatusLine
        {
            get
            {
                try { return txStream?.LastStatusLine ?? ""; }
                catch { return ""; }
            }
        }

        private Thread remoteAudioThread = null;
        private bool stopRemoteAudio;

        private bool _PCAudio;
        /// <summary>
        /// Audio over PC
        /// </summary>
        public bool PCAudio
        {
            get { return _PCAudio; }
            set
            {
                Tracing.TraceLine("PCAudio:" + value.ToString(), TraceLevel.Info);
                if (_PCAudio != value)
                {
                    if (value)
                    {
                        startRemoteAudioThread();
                    }
                    else
                    {
                        stopRemoteAudioThread();
                    }
                    _PCAudio = value;
                }
            }
        }

        private void startRemoteAudioThread()
        {
            Tracing.TraceLine("startRemoteAudioThread", TraceLevel.Info);
            stopRemoteAudio = false;
            remoteAudioThread = new Thread(remoteAudioProc);
            remoteAudioThread.Name = "RemoteAudio";
            remoteAudioThread.Priority = ThreadPriority.Highest;
            // Engine Track (2026-08-11): background. When stopRemoteAudioThread's
            // 6 s Join fails it abandons this thread; as a foreground thread the
            // abandoned corpse pinned the whole process alive after the UI
            // exited — the field-confirmed orphan jjflexible.exe hang (four
            // ghosts in four launch/exit cycles, 2026-08-10), and the ghosts
            // then raced the live instance over the shared config file. The
            // orderly path is unchanged: Join still waits, cleanup still runs;
            // background only matters once the thread is already being
            // abandoned, and the alternative to a skipped PortAudio close at
            // process exit is a process that never exits.
            remoteAudioThread.IsBackground = true;
            remoteAudioThread.Start();
        }

        private void stopRemoteAudioThread()
        {
            if (PCAudio & !stopRemoteAudio)
            {
                Tracing.TraceLine("stopRemoteAudioThread", TraceLevel.Info);
                try
                {
                    stopRemoteAudio = true;
                    // Engine Track (2026-08-11): 10 s, up from 6. Teardown's
                    // waits are all bounded now, and the worst legitimate case
                    // (a wedged device: two ~2.2 s stream aborts inside the
                    // audio server, the Finished() polls around them, and a
                    // 1 s Pa_Terminate join) can honestly take 6-8 s.
                    // Abandoning is the ERROR path, not a normal outcome: with
                    // the bounds in place it is only reachable when a native
                    // PortAudio call itself is blocked inside a dead driver —
                    // and the thread is background now, so even then it can no
                    // longer pin an orphan jjflexible.exe alive after exit.
                    if (!remoteAudioThread.Join(10000))
                    {
                        // Thread.Abort() throws PlatformNotSupportedException on .NET 8.
                        // Log and abandon — the (background) thread dies with the
                        // process if its blocking native call never returns.
                        Tracing.TraceLine("stopRemoteAudioThread: thread didn't stop within 10s, abandoning (background thread, cannot pin the process)", TraceLevel.Error);
                    }
                }
                catch(Exception ex)
                {
                    Tracing.TraceLine("stopRemoteAudioThread exception:" + ex.Message, TraceLevel.Error);
                }
                remoteAudioThread = null;
            }
        }

        // ── Track B, 2026-08-18 (#29): receive-continuity meter state ──
        // One remoteAudioProc runs at a time, so plain fields are safe. Reset
        // at the top of every run; summarized at remoteDone. See the meter
        // itself in the polling loop for what these mean.
        private double _opusRxPrevTs;
        private double _opusRxMinDelta;
        private double _opusRxMaxDelta;
        private long _opusRxPacketCount;
        private long _opusRxGapCount;

        /// <summary>
        /// One line at stream shutdown: was the receive stream continuous?
        /// A zero-gap run is evidence too — it acquits the network and points
        /// the click hunt at the playback side (PortAudio statusFlags and the
        /// output queue's own silence counters cover that side).
        /// </summary>
        private void TraceOpusRxContinuitySummary()
        {
            if (_opusRxPacketCount == 0)
            {
                Tracing.TraceLine("remoteAudioProc continuity summary: no receive packets consumed",
                    TraceLevel.Info);
                return;
            }
            string nominal = _opusRxMinDelta < double.MaxValue
                ? (_opusRxMinDelta * 1000).ToString("F1") : "unknown";
            Tracing.TraceLine("remoteAudioProc continuity summary: "
                + _opusRxPacketCount + " packets consumed, nominal step " + nominal
                + " ms, largest step " + (_opusRxMaxDelta * 1000).ToString("F1") + " ms, "
                + _opusRxGapCount + " discontinuit" + (_opusRxGapCount == 1 ? "y" : "ies")
                + (_opusRxGapCount == 0 ? " (the network delivered a continuous stream)"
                    : " (each one splices non-adjacent audio and is audible as a click)"),
                _opusRxGapCount == 0 ? TraceLevel.Info : TraceLevel.Error);
        }

        private void remoteAudioProc()
        {
            Tracing.TraceLine("remoteAudioProc is WAN=" + RemoteRig.ToString(), TraceLevel.Info);
            opusOutputChannel = null;
            opusInputChannel = null;
            opusInputAvailable = false;
            _opusRxPrevTs = 0;
            _opusRxMinDelta = double.MaxValue;
            _opusRxMaxDelta = 0;
            _opusRxPacketCount = 0;
            _opusRxGapCount = 0;
#if CWMonitor
            CWMon = null;
#endif

#if zero
            // input is from pc.
            string oldMicInput = theRadio.MicInput;
            theRadio.MicInput = "PC";
            if (!await(() =>
            {
                return theRadio.RemoteTxOn;
            }, 1000))
            {
                Tracing.TraceLine("remoteAudioProc:remote tx should be on", TraceLevel.Error);
            }
#endif

            // QB Track B, 2026-08-07: this used to call GetConfiguredDevice with
            // getNew:true, which popped a modal WinForms picker from THIS thread —
            // a background, non-STA, ownerless audio thread, where the dialog can
            // land behind the main window and focus handoff to NVDA is unreliable.
            // That was the path a first-run machine actually hit at connect time.
            // Now: resolve silently if we can, fall back to the system default
            // with a spoken notice, and only give up — out loud — if there is no
            // sound device at all. Never silence.
            audioSystem = new JJPortaudio.Devices(Callouts.AudioDevicesFile);
            if (!audioSystem.Setup(out var audioEnumStatus, out string audioEnumMessage))
            {
                Tracing.TraceLine("remoteAudioProc:audio setup failed, " + audioEnumStatus, TraceLevel.Error);
                ScreenReaderOutput.Speak(
                    string.IsNullOrEmpty(audioEnumMessage)
                        ? Lexicon.Get("audio.startup.enumeration_failed")
                        : Lexicon.Get("audio.startup.enumeration_failed_detail", ("enumMessage", audioEnumMessage)),
                    VerbosityLevel.Critical, true);
                goto remoteDone;
            }

            remoteInputDevice = ResolveAudioDevice(JJPortaudio.Devices.DeviceTypes.input, Lexicon.Get("audio.device.role_microphone"));
            if (remoteInputDevice == null)
            {
                Tracing.TraceLine("remoteAudioProc:remoteInputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }
            remoteOutputDevice = ResolveAudioDevice(JJPortaudio.Devices.DeviceTypes.output, Lexicon.Get("audio.device.role_playback"));
            if (remoteOutputDevice == null)
            {
                Tracing.TraceLine("remoteAudioProc:remoteOutputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }

            // Start the audio subsystem.
            // Note: We're not using DAX any more.
            JJPortaudio.Audio.Initialize(remoteInputDevice, remoteOutputDevice);

            // Setup audio channels, output first.
            rxStream = null;
            theRadio.RequestRXRemoteAudioStream(true); // see opusOutputStreamAddedHandler
            if (!await(() =>
                {
                    return (rxStream != null) || Disconnecting || stopRemoteAudio;
                }, 10000))
            {
                Tracing.TraceLine("remoteAudioProc: opus output channel not added.", TraceLevel.Error);
                goto remoteDone;
            }
            theRadio.IsMuteLocalAudioWhenRemoteOn = true;
            opusOutputChannel = new audioChannelData(rxStream, "JJFlexRadio.OpusOutputChan");
            opusOutputChannel.PortAudioStream = new JJAudioStream();
            opusOutputChannel.PortAudioStream.OpenOpus(Devices.DeviceTypes.output, opusRxSampleRate);
            // Boost Opus output to compensate for low remote audio levels.
            // The Opus decode path bypasses FlexLib's RXGain scalar, so decoded audio
            // is at raw codec level which is typically too quiet for laptop speakers
            // (measured raw peaks ~0.02-0.10, about -35 to -20 dBFS).
            // This was a hardcoded 4.0f until Audio Arc Track A (2026-08-11); it is
            // now the operator's PC output volume — Audio menu > PC Audio, the Home
            // audio expander, or Ctrl+J V P. Default +12 dB = the historical 4x.
            //
            // The bypass claim above was VERIFIED against FlexLib, Track B
            // 2026-08-18 (#17), because it looked contradicted elsewhere: the
            // RXGain scalar (RXRemoteAudioStream.RXGain, 0-100 mapped to
            // -20..0 dB) is applied only in RXAudioStream.OnRXDataReady, on
            // the UNCOMPRESSED DataReady path. We never subscribe to
            // DataReady; the loop below polls _opusRXList — the raw VITA
            // packet payloads, untouched by any gain — and decodes with our
            // own codec. So RXGain genuinely cannot move our level in either
            // direction. Where the level actually comes from: the radio mixes
            // slices into the remote stream at each slice's radio-side
            // audio_level (the AudioGain control, 0-100), and SmartSDR's own
            // client expects to ATTENUATE what arrives (its RXGain spans
            // -20..0 dB) — so a stream that decodes below full scale is the
            // upstream design, not a defect on this side. The honest gain
            // stages are: slice AudioGain at the radio first, PC output
            // volume here second.
            opusOutputChannel.PortAudioStream.OutputGain = PcOutputGainFactor;
            // Wire PC-side audio processing if a pipeline has been configured
            opusOutputChannel.PortAudioStream.PostDecodeProcessor = _audioPostProcessor;
            Tracing.TraceLine("remoteAudioProc:opusOutputChannel:" + opusOutputChannel.Name + " setup, OutputGain=" + opusOutputChannel.PortAudioStream.OutputGain + ", PostDecodeProcessor=" + (_audioPostProcessor != null ? "yes" : "none"), TraceLevel.Info);

            if (!startOpusOutputChannel())
            {
                Tracing.TraceLine("remoteAudioProc: opus output channel not started.", TraceLevel.Error);
                goto remoteDone;
            }

            // Setup the transmit audio, after the rx audio, but don't start the I/O.
            txStream = null;
            // Sprint 33 Track G, 2026-08-20: declare the compression we are
            // actually going to send. Everything that reaches AddTXData below
            // comes out of the Opus encoder in sendOpusInput, so "opus" is a
            // statement of fact, not a preference. The bare create this used to
            // send left the radio to pick a default we never read back — see
            // the JJFlex patch note on Radio.RequestRemoteAudioTXStream.
            theRadio.RequestRemoteAudioTXStream(true); // see opusInputStreamAddedHandler
            if (!await(() =>
                {
                    return (txStream != null) || Disconnecting || stopRemoteAudio;
                }, 10000)
                && !Disconnecting && !stopRemoteAudio)
            {
                // Sprint 33 Track G: fall back to the vendor-stock bare create.
                //
                // `stream create` gets no reply handler, so a radio that
                // REFUSES the compression argument refuses it silently — the
                // stream simply never arrives and we land here. The failure
                // path below tears down the whole remote-audio session, receive
                // audio included, so an unaccepted parameter would cost an
                // operator all their audio rather than just transmit.
                //
                // A FLEX-8600 accepts it, but Don is on a 6300 and firmware
                // vintages vary, so this is deliberately not tested by assuming
                // the bench radio speaks for every radio. Retrying bare means
                // the worst case of this patch is exactly the behaviour that
                // shipped before it: whatever default the radio picks.
                Tracing.TraceLine("remoteAudioProc: no TX stream after asking for compression=opus;"
                    + " retrying with the bare create this radio may be expecting", TraceLevel.Error);
#pragma warning disable CS0618 // Deliberate: the point of the retry is to reproduce vendor-stock behaviour exactly.
                theRadio.RequestRemoteAudioTXStream();
#pragma warning restore CS0618
                if (!await(() =>
                    {
                        return (txStream != null) || Disconnecting || stopRemoteAudio;
                    }, 10000))
                {
                    Tracing.TraceLine("remoteAudioProc: didn't get RemoteAudioTXStream from radio"
                        + " (neither the explicit-compression nor the bare create was answered)",
                        TraceLevel.Error);
                    goto remoteDone;
                }
                Tracing.TraceLine("remoteAudioProc: this radio answered the bare create but not the"
                    + " explicit-compression one — report it, that is a firmware difference worth knowing",
                    TraceLevel.Error);
            }
            if (txStream == null)
            {
                // Disconnecting or stopping raced us; nothing to set up.
                Tracing.TraceLine("remoteAudioProc: TX stream setup abandoned", TraceLevel.Info);
                goto remoteDone;
            }
            opusInputChannel = new audioChannelData(txStream, "JJFlexRadio.OpusInputChan");
            opusInputChannel.PortAudioStream = new JJAudioStream();
            // The result of this open was discarded until 2026-08-13. A
            // microphone that would not open therefore produced no message of
            // any kind: startOpusInputChannel simply traced an error on every
            // key-up — and blocked the remote-audio thread for a second doing
            // it — while the operator transmitted silence and was told nothing.
            // Receive audio is already running by this point and deliberately
            // stays running; losing the radio because the microphone failed is
            // the wrong trade.
            uint txRate = OpusTxSampleRateSetting;
            opusInputAvailable = opusInputChannel.PortAudioStream.OpenOpus(
                Devices.DeviceTypes.input, txRate, sendOpusInput);
            if (!opusInputAvailable)
            {
                Tracing.TraceLine("remoteAudioProc:opus input channel did not open;"
                    + " computer transmit audio is unavailable this session", TraceLevel.Error);
                if (!SuppressSpeech) ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.mic.could_not_open"), VerbosityLevel.Critical, true);
            }
            else
            {
                // Always log what the transmit stream actually opened as,
                // whether or not it matches what was asked for. The device has
                // the last word on both the rate and the channel count, and a
                // line that only appears on divergence is a line nobody can
                // use as a baseline.
                Tracing.TraceLine("remoteAudioProc:opus input channel open at "
                    + opusInputChannel.PortAudioStream.SampleRate + " Hz, "
                    + opusInputChannel.PortAudioStream.Channels + " channel(s)"
                    + (opusInputChannel.PortAudioStream.SampleRate != txRate
                        ? " — the device refused the requested " + txRate + " Hz"
                        : ""), TraceLevel.Info);
            }
            // Audio Track C: hand the persistent TX injection sources to the
            // input stream so an engaged one replaces the mic at the encoder.
            // Sprint 33 Track I: this used to be the tone generator alone; it
            // is now the mux carrying the tone and the reference-voice player.
            opusInputChannel.PortAudioStream.InputSource = TxInputSources;
            // Track I: hand the persistent TX conditioning chain (NR + gate +
            // residual tap) to the same stream. It runs AFTER the tone
            // injection point (and is skipped entirely while a tone is
            // engaged — the tone is a calibrated reference) and BEFORE the
            // LUFS meter, so the meter keeps measuring what genuinely ships.
            opusInputChannel.PortAudioStream.InputProcessor = txConditioner.Process;
            // Engine Track: hand the persistent LUFS meter to the same stream.
            // It taps the callback AFTER the tone injection, so it measures
            // whatever is actually being encoded and sent — tone or mic.
            opusInputChannel.PortAudioStream.InputLufsMeter = txLufsMeter;
            Tracing.TraceLine("remoteAudioProc:Opus Input Channel setup", TraceLevel.Info);

#if CWMonitor
            // Also need a cw monitor
            CWMonInit();
#endif

            // Main audio loop.
            // Note that we must pole for opus output.
            while (!stopRemoteAudio)
            {
                // Idle if disconnecting. Engine Track: this was a bare
                // `continue` — a BUSY spin at Highest priority burning a full
                // core for the whole disconnect window (up to the 30 s radio
                // wait in Disconnect() before it flips PCAudio off). Sleep
                // instead; nothing here is latency-sensitive once we're
                // tearing down.
                if (Disconnecting)
                {
                    // The gate below never runs while disconnecting, so stop
                    // the self-clock here rather than leaving it feeding a
                    // channel that is being torn down. Idempotent, and the
                    // teardown path stops it again — belt and braces on the
                    // one thing that must not outlive the connection.
                    stopSelfClockedTxInput();
                    Thread.Sleep(10);
                    continue;
                }

                string mode = "";
                lock (mySlices)
                {
                    Slice s = VFOToSlice(TXVFO);
                    if (s != null) mode = s.DemodMode;
                }
                if (mode != "CW")
                {
                    if (Transmit)
                    {
                        // ── INNER GATE: which producer feeds the encoder ──
                        //
                        // A source that generates its own samples — the test
                        // tone, a voice file — has no clock, and until
                        // 2026-08-24 it borrowed the microphone's. That handed
                        // a synthesized signal every property of a device that
                        // contributes nothing to it, including the device's
                        // true rate. A capture device a fraction of a percent
                        // off nominal produces a CONSTANT rate error, and a
                        // constant rate error against the radio's jitter
                        // buffer is heard as a PERIODIC correction — which is
                        // why the fault sounded like a metronome rather than
                        // like something ragged. See #208.
                        //
                        // Idle, not Engaged, and the difference is the ten
                        // milliseconds after a release is requested: the
                        // source is no longer muting the microphone but is
                        // still ramping its own signal down, and cutting that
                        // short is the click the ramps exist to prevent.
                        if (!TxInputSources.Idle)
                        {
                            // Order is load-bearing. Both producers share ONE
                            // Opus encoder and Opus is stateful, so the
                            // capture stream must be fully stopped before the
                            // self-clock starts. StopAudio waits for the
                            // callback to quiesce, so there is no overlap.
                            stopOpusInputChannel();
                            startSelfClockedTxInput();
                        }
                        else
                        {
                            stopSelfClockedTxInput();
                            startOpusInputChannel(); // only starts it once
                        }
                        // Never stream transmit audio into a closed gate
                        // without saying so. Throttled internally.
                        checkPcMicSelection();
                    }
                    else
                    {
                        // ── OUTER GATE: unkey stops everything ──
                        //
                        // Ratified by Noel 2026-08-24: transmit stop stops
                        // everything, tone or microphone, no drain and no
                        // tail. Deliberately NOT conditional on which source
                        // was live — audio continuing past an unkey is a
                        // safety fault, not a cosmetic one, and a gate that
                        // has to know what it is gating is a gate that can be
                        // wrong about it.
                        stopSelfClockedTxInput();
                        stopOpusInputChannel(); // only stops it once.
                    }
                }

                // opus receive polling.
                // get opus data, even during transmit (for QSK).
                byte[] opusBuf = null;
                double consumedTs = -1;
                lock (opusOutputChannel)
                {
                    RXAudioStream stream = opusOutputChannel.OpusChannel;
                    lock (stream.OpusRXListLockObj)
                    {
                        int lastID = stream._opusRXList.Count - 1;
                        // ignore initial packets.
                        if (opusOutputChannel.JustStarted)
                        {
                            // Guarded (Track B, 2026-08-18): with no packet in
                            // the list yet, Keys[-1] throws. Leaving
                            // JustStarted true until the first packet exists
                            // preserves the skip-initial-packets semantics
                            // exactly.
                            if (lastID >= 0)
                            {
                                opusOutputChannel.JustStarted = false;
                                stream.LastOpusTimestampConsumed = stream._opusRXList.Keys[lastID];
                                _opusRxPrevTs = 0; // re-arm the continuity meter
                            }
                        }
                        else
                        {
                            for (int i = 0; i < stream._opusRXList.Count; i++)
                            {
                                if (stream.LastOpusTimestampConsumed <
                                    stream._opusRXList.Keys[i])
                                {
                                    opusBuf = stream._opusRXList.Values[i].payload;
                                    consumedTs = stream._opusRXList.Keys[i];
                                    stream.LastOpusTimestampConsumed = consumedTs;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (opusBuf != null)
                {
                    // ── Track B, 2026-08-18 (#29): receive-continuity meter ──
                    // The tone-monitor clicks appear only when TX and RX
                    // streams run together, and PortAudio's statusFlags can
                    // only see glitches PortAudio itself caused. A packet the
                    // NETWORK lost never reaches PortAudio: our decoder just
                    // splices two non-adjacent 10 ms packets together, and the
                    // waveform step at the splice IS a click — invisible to
                    // every instrument this path had. The radio's timestamps
                    // are media time, so consecutive consumed packets should
                    // step by exactly one packet duration; the smallest step
                    // seen this session estimates that duration, and any step
                    // over 1.5x it counts as a splice discontinuity. Totals at
                    // stream close; first occurrence logged so the trace shows
                    // WHEN it began (transmit start is the interesting case).
                    if (_opusRxPrevTs > 0 && consumedTs > _opusRxPrevTs)
                    {
                        double delta = consumedTs - _opusRxPrevTs;
                        _opusRxPacketCount++;
                        if (delta < _opusRxMinDelta) _opusRxMinDelta = delta;
                        if (delta > _opusRxMaxDelta) _opusRxMaxDelta = delta;
                        if (_opusRxMinDelta < double.MaxValue && delta > _opusRxMinDelta * 1.5)
                        {
                            _opusRxGapCount++;
                            if (_opusRxGapCount == 1)
                            {
                                Tracing.TraceLine("remoteAudioProc: first receive-stream "
                                    + "discontinuity — consumed packet timestamps stepped "
                                    + $"{delta * 1000:F1} ms where {_opusRxMinDelta * 1000:F1} ms is nominal. "
                                    + "Audio between those timestamps never arrived; the splice "
                                    + "is audible as a click. Further gaps are counted silently; "
                                    + "totals are logged when the stream stops.", TraceLevel.Error);
                            }
                        }
                    }
                    _opusRxPrevTs = consumedTs;
                    opusOutputChannel.PortAudioStream.WriteOpus(opusBuf);
                }
                else
                {
                    Thread.Yield();
                }
            }

            Tracing.TraceLine("remoteAudioProc:stopping remote audio", TraceLevel.Info);

            remoteDone:
            // Both exits pass through here, so the continuity totals are
            // reported for aborted runs too.
            TraceOpusRxContinuitySummary();
            // Note that theRadio may be null here.
#if CWMonitor
            if (useCWMon) CWMonDone();
#endif

            if (opusOutputChannel != null)
            {
                stopOpusOutputChannel();
                if (opusOutputChannel.PortAudioStream != null)
                {
                    opusOutputChannel.PortAudioStream.Close();
                    opusOutputChannel.PortAudioStream = null;
                }
                if (opusOutputChannel.OpusChannel != null)
                {
                    opusOutputChannel.OpusChannel.Close();
                    opusOutputChannel.OpusChannel = null;
                    rxStream = null;
                }
            }

            if (opusInputChannel != null)
            {
                stopOpusInputChannel();
                if (opusInputChannel.PortAudioStream != null)
                {
                    opusInputChannel.PortAudioStream.Close();
                    opusInputChannel.PortAudioStream = null;
                }
                if (opusInputChannel.TXOpusChannel != null)
                {
                    opusInputChannel.TXOpusChannel.Close();
                    opusInputChannel.TXOpusChannel = null;
                    txStream = null;
                }
            }

            Audio.Terminate();
            opusOutputChannel = null;
            opusInputChannel = null;
            opusInputAvailable = false;
#if CWMonitor
            CWMon = null;
#endif
#if zero
            // Restore mic input.
            theRadio.MicInput = oldMicInput;
#endif

            Tracing.TraceLine("remoteAudioProc exiting", TraceLevel.Info);
        }

        // Note:  Called from the audio callback.
        private void sendOpusInput(byte[] data)
        {
            if (data.Length > 0)
            {
                opusInputChannel.TXOpusChannel.AddTXData(data);
            }
            else { }
        }

        #region TX test tone (Audio Track C)
        // The generator is owned here (one per rig instance) and handed to the
        // Opus input stream when the PC-audio TX channel is created, so it
        // survives channel stop/start across key cycles. When engaged it
        // REPLACES the mic samples at the encoder — the mic is discarded
        // (muted), never mixed — and the tone rides the identical Opus
        // encode-and-send path the mic does.
        private readonly JJPortaudio.TxToneGenerator txToneGen = new JJPortaudio.TxToneGenerator();

        // Sprint 33 Track I: the reference-voice player, owned here for the
        // same reason the tone generator is — it must survive the TX channel
        // stopping and starting across key cycles, because a known file
        // played across two key-downs has to be the same file both times.
        private readonly JJPortaudio.TxFilePlayer txFilePlayer = new JJPortaudio.TxFilePlayer();

        // Both stand in for the microphone at the same point in the input
        // callback, so they share the slot through a mux. The tone is listed
        // first: it is a calibrated reference, and if somebody has both going
        // the calibrated thing is the one that should survive.
        // Built on demand rather than in a field initialiser, which cannot
        // reference the two fields it needs.
        private JJPortaudio.TxInputSourceMux _txInputSources;
        private JJPortaudio.TxInputSourceMux TxInputSources =>
            _txInputSources ??= new JJPortaudio.TxInputSourceMux(txToneGen, txFilePlayer);

        // Track I: the TX conditioning chain (noise reduction + gate +
        // residual monitor tap), owned here like the tone generator and the
        // LUFS meter so it survives channel stop/start across key cycles.
        // Handed to the Opus input stream when the PC-audio TX channel is
        // created. The UI assembly plugs the NR engine, the monitor sink and
        // the settings into it via TxConditioner.
        private readonly JJPortaudio.TxAudioConditioner txConditioner = new JJPortaudio.TxAudioConditioner();

        /// <summary>
        /// Track I: the persistent PC-side transmit conditioning chain for
        /// this rig — the gate, the pluggable noise-reduction slot, and the
        /// monitor tap that plays what the chain removed. The UI configures
        /// it here; the audio path picks it up automatically.
        /// </summary>
        public JJPortaudio.TxAudioConditioner TxConditioner => txConditioner;

        /// <summary>
        /// True while the TX test tone is engaged (replacing the microphone
        /// whenever PC TX audio flows).
        /// </summary>
        public bool TxToneEngaged => txToneGen.Engaged;

        /// <summary>
        /// Test tone frequency in hertz. Safe to change live; the generator
        /// is phase-continuous so there is no click.
        /// </summary>
        public float TxToneFrequency
        {
            get { return txToneGen.Frequency; }
            set { txToneGen.Frequency = value; }
        }

        /// <summary>Test tone level in dBFS (-60..0). Default -10.</summary>
        public float TxToneLevelDb
        {
            get { return txToneGen.LevelDb; }
            set { txToneGen.LevelDb = value; }
        }

        /// <summary>
        /// Engage the test tone: the microphone is muted and the tone takes
        /// its place in the TX stream. Takes effect immediately if
        /// transmitting, otherwise at the next key-down.
        /// </summary>
        public void TxToneStart()
        {
            Tracing.TraceLine("TxToneStart: " + txToneGen.Frequency + " Hz at " + txToneGen.LevelDb + " dBFS", TraceLevel.Info);
            txToneGen.Start();
        }

        /// <summary>Release the test tone and restore the microphone.</summary>
        public void TxToneStop()
        {
            Tracing.TraceLine("TxToneStop", TraceLevel.Info);
            txToneGen.Stop();
        }

        /// <summary>
        /// Plain-language reason the test tone cannot reach the transmitter
        /// right now, or the empty string when the path is good. The tone
        /// rides the PC-audio TX path, so it needs PC audio on, the radio's
        /// transmit input set to PC, and a voice mode (the PC TX stream does
        /// not run in CW).
        /// </summary>
        public string TxTonePathTrouble
        {
            get
            {
                if (!PCAudio)
                    return "PC audio is off. The test tone rides the PC audio path; turn on PC audio first.";
                if (!string.Equals(MicSource, "PC", StringComparison.OrdinalIgnoreCase))
                    return "Transmit audio is from the " + MicSource +
                        " input, not this computer. Set transmit audio from to PC first.";
                string mode = Mode ?? "";
                if (mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase))
                    return "The radio is in CW mode, where PC transmit audio does not run. Switch to a voice mode first.";
                return "";
            }
        }

        /// <summary>
        /// Sprint 33 Track I: the reference-voice player that stands in for
        /// the microphone in the transmit stream. The UI loads content into it
        /// and starts and stops it; the audio path picks it up automatically,
        /// exactly as it does the test tone.
        /// </summary>
        /// <remarks>
        /// Exposed as the object rather than mirrored property by property.
        /// The tone generator's surface here is a set of pass-through
        /// properties written before there was a second source, and repeating
        /// that for every future source is how a rig class turns into a
        /// forwarding table. What genuinely belongs to the rig — whether the
        /// path can carry audio at all — is
        /// <see cref="TxTonePathTrouble"/>, and it is already shared.
        /// </remarks>
        public JJPortaudio.TxFilePlayer TxFilePlayer => txFilePlayer;

        /// <summary>
        /// True while a known recording is being transmitted in place of the
        /// microphone.
        /// </summary>
        public bool TxFilePlaying => txFilePlayer.Engaged;

        /// <summary>
        /// Start transmitting the loaded recording in place of the
        /// microphone. Takes effect immediately if transmitting, otherwise at
        /// the next key-down.
        /// </summary>
        public void TxFileStart()
        {
            Tracing.TraceLine("TxFileStart: \"" + txFilePlayer.ContentName + "\", "
                + txFilePlayer.ContentSeconds.ToString("F1") + " s at "
                + txFilePlayer.ContentSampleRate + " Hz", TraceLevel.Info);
            txFilePlayer.Start();
        }

        /// <summary>Stop transmitting the recording and restore the microphone.</summary>
        public void TxFileStop()
        {
            Tracing.TraceLine("TxFileStop", TraceLevel.Info);
            txFilePlayer.Stop();
        }
        #endregion

        // Engine Track (2026-08-11): removed the dead #if opusToFile /
        // #if opusInputToFile debug blocks that hardcoded Jim's old user path
        // (c:\users\jjs\...). They have not compiled since the defines were
        // commented out, and a debug tap that writes to another machine's
        // home directory is not coming back.

        private string oldMicInput;
        private bool startOpusOutputChannel()
        {
            Tracing.TraceLine("startOpusOutputChannel:" +
                opusOutputChannel.Name + ' ' + opusOutputChannel.Started.ToString(), TraceLevel.Info);
            lock (opusOutputChannel)
            {
                opusOutputChannel.JustStarted = true; // set on each call
                opusOutputChannel.OpusChannel.RxMute = false;
                if (opusOutputChannel.Started) return true;
                oldMicInput = theRadio.MicInput;
                theRadio.MicInput = "PC";
                // Remember that WE asked for PC, so the loop can tell a silent
                // revert from a deliberate operator choice. See
                // checkPcMicSelection.
                _pcMicExpected = true;
                // `RXGain = 50` stood here until Track B 2026-08-18 (#17)
                // and was a no-op twice over: 50 is the property's default so
                // the setter's changed-guard did nothing, and the scalar it
                // feeds is applied only on FlexLib's uncompressed DataReady
                // path, which this app never subscribes to (we poll the raw
                // Opus packet list and decode ourselves — see the verified
                // note at the OutputGain setup above). Removed rather than
                // kept, because a line that reads like a volume control and
                // controls nothing is exactly the description drift this
                // codebase hunts.
                opusOutputChannel.Started = opusOutputChannel.PortAudioStream.StartAudio();
                if (!opusOutputChannel.Started)
                {
                    Tracing.TraceLine("startOpusOutputChannel portAudio didn't start", TraceLevel.Error);
                }
            }
            return opusOutputChannel.Started;
        }

        private void stopOpusOutputChannel()
        {
            Tracing.TraceLine("stopOpusOutputChannel:" +
                opusOutputChannel.Name + ' ' + opusOutputChannel.Started.ToString(), TraceLevel.Info);
            lock (opusOutputChannel)
            {
                opusOutputChannel.OpusChannel.RxMute = true;
                if (!opusOutputChannel.Started) return;
                _pcMicExpected = false; // we no longer own the mic selection
                try
                {
                    theRadio.MicInput = oldMicInput;
                }
                // ignore error.
                catch { }
                opusOutputChannel.Started = false;
                opusOutputChannel.PortAudioStream.StopAudio();
            }
        }

        // --- mic_selection assertion (Track B, 2026-08-16) --------------------
        //
        // The arc's thesis in one line: never stream transmit audio into a
        // closed gate without saying so.
        //
        // startOpusOutputChannel sets the radio's mic input to PC exactly once.
        // Nothing re-asserts it and nothing checks it, so a profile load
        // afterwards silently hands the transmitter back to the analog mic
        // jack while the PC audio path keeps encoding and sending. The operator
        // transmits, hears their own monitor, sees no warning, and puts out
        // nothing — which is precisely the shape of the 2026-08-14 session.
        //
        // Deliberately NOT a blind re-assert. Choosing the analog mic while PC
        // audio runs is a legitimate configuration (the Audio Workshop offers
        // it, and TxTonePathTrouble describes it as a normal state), so a
        // divergence the operator caused through JJ Flex is respected in
        // silence. _pcMicExpected records only what WE last asked for; it is
        // cleared when the operator picks something else through MicSource, so
        // the warning fires for reverts we did not make and nothing else.
        private volatile bool _pcMicExpected;
        private int _pcMicCheckTime;
        private bool _pcMicDiverged;

        /// <summary>
        /// While PC transmit audio is running, verify the radio still has its
        /// mic input set to PC — and if it has drifted behind our back, say so
        /// out loud and put it back.
        /// <para>Called from the transmit branch of the remote-audio loop and
        /// throttled to about once a second: the check is a cached property
        /// read, but the recovery is a radio command and the announcement is
        /// speech, neither of which belongs on a per-poll path.</para>
        /// </summary>
        private void checkPcMicSelection()
        {
            if (!_pcMicExpected || theRadio == null) return;

            int now = System.Environment.TickCount;
            if ((now - _pcMicCheckTime) < 1000) return;
            _pcMicCheckTime = now;

            string current = theRadio.MicInput ?? "";
            if (string.Equals(current, "PC", StringComparison.OrdinalIgnoreCase))
            {
                if (_pcMicDiverged)
                {
                    _pcMicDiverged = false;
                    Tracing.TraceLine("checkPcMicSelection: mic selection is PC again",
                        TraceLevel.Info);
                }
                return;
            }

            if (_pcMicDiverged) return; // already reported this episode

            _pcMicDiverged = true;
            Tracing.TraceLine("checkPcMicSelection: radio mic selection is '" + current
                + "', not PC, while computer transmit audio is running"
                + " — re-asserting PC", TraceLevel.Warning);
            if (!SuppressSpeech) ScreenReaderOutput.Speak(
                Lexicon.Get("audio.mic.diverged", ("current", current)),
                VerbosityLevel.Critical, true);
            try { theRadio.MicInput = "PC"; }
            catch (Exception ex)
            {
                Tracing.TraceLine("checkPcMicSelection: could not set mic input back to PC, "
                    + ex.Message, TraceLevel.Error);
            }
        }

        private bool _opusInputStartFailed;

        /// <summary>
        /// Start the PC transmit-audio channel. Idempotent — remoteAudioProc's
        /// main loop calls this on every iteration while transmitting.
        /// <para>Every trace here sits BELOW the already-started guard, the way
        /// <see cref="stopOpusInputChannel"/> ten lines down has always had it.
        /// A trace above the guard is one line per poll of a loop that only
        /// yields: on 2026-08-14 that was 3.36 million lines in four minutes,
        /// with trace parts rotating roughly every twenty seconds, which is how
        /// a transmit-audio debugging session ended up with no usable trace.</para>
        /// </summary>
        private bool startOpusInputChannel()
        {
            if (!opusInputAvailable) return false;
            lock (opusInputChannel)
            {
                if (opusInputChannel.Started) return true;
                opusInputChannel.Started = opusInputChannel.PortAudioStream.StartAudio();
                if (opusInputChannel.Started)
                {
                    _opusInputStartFailed = false;
                    Tracing.TraceLine("startOpusInputChannel:" + opusInputChannel.Name
                        + " started", TraceLevel.Info);
                }
                else if (!_opusInputStartFailed)
                {
                    // Only on the transition into failure. A start that keeps
                    // failing is retried on every poll, so an unconditional
                    // error line here is the same flood by another route.
                    _opusInputStartFailed = true;
                    Tracing.TraceLine("startOpusInputChannel:" + opusInputChannel.Name
                        + " portAudio didn't start", TraceLevel.Error);
                }
            }
            return opusInputChannel.Started;
        }

        private bool _selfClockedTxStartFailed;

        /// <summary>
        /// Start the self-clocked transmit source — frames paced by elapsed
        /// time rather than by the capture device (#208). Idempotent; the main
        /// loop calls this on every poll while a generated source is engaged.
        /// </summary>
        /// <remarks>
        /// The caller must have stopped the capture stream first: one Opus
        /// encoder is shared, and Opus is stateful.
        /// </remarks>
        private bool startSelfClockedTxInput()
        {
            if (!opusInputAvailable || opusInputChannel == null) return false;
            JJPortaudio.JJAudioStream stream = opusInputChannel.PortAudioStream;
            if (stream == null) return false;
            // Cheap first, and it is the answer on all but one poll in
            // thousands — the loop spins on Thread.Yield().
            if (stream.SelfClockedTxRunning) return true;

            bool started = stream.StartSelfClockedTx();
            if (started)
            {
                _selfClockedTxStartFailed = false;
            }
            else if (!_selfClockedTxStartFailed)
            {
                // Only on the transition into failure. A start that keeps
                // failing is retried on every poll, so an unconditional line
                // here is a trace flood by another route — the same lesson
                // startOpusInputChannel learned on 2026-08-14.
                _selfClockedTxStartFailed = true;
                Tracing.TraceLine("startSelfClockedTxInput: could not start the self-clocked"
                    + " transmit source; a generated source is engaged but nothing is pacing it,"
                    + " so this transmission will be silent", TraceLevel.Error);
            }
            return started;
        }

        /// <summary>
        /// Stop the self-clocked transmit source. Hard and immediate.
        /// Idempotent.
        /// </summary>
        private void stopSelfClockedTxInput()
        {
            JJPortaudio.JJAudioStream stream = opusInputChannel?.PortAudioStream;
            if (stream == null || !stream.SelfClockedTxRunning) return;
            stream.StopSelfClockedTx();
        }

        private void stopOpusInputChannel()
        {
            if (opusInputChannel == null)
            {
                return;
            }
            lock (opusInputChannel)
            {
                if (!opusInputChannel.Started)
                {
                    return;
                }
                Tracing.TraceLine("stopOpusInputChannel:" +
                    opusInputChannel.Name + ' ' + opusInputChannel.Started.ToString(), TraceLevel.Info);
                opusInputChannel.PortAudioStream.StopAudio();
                opusInputChannel.Started = false;
            }
        }

#if CWMonitor
        // Remote CW monitor
        private void CWMonInit()
        {
            Tracing.TraceLine("CWMonInit", TraceLevel.Info);
            CWMon = new Morse();
            CWMonStart();
        }

        private bool CWMonStart()
        {
            Tracing.TraceLine("CWMonStart", TraceLevel.Info);
            lock (CWMon)
            {
                if (CWMon.Start()) // Sets CWMon.Started.
                {
                    CWMon.Frequency = (uint)theRadio.CWPitch;
                    CWMon.Speed = (uint)theRadio.CWSpeed;
                    CWMon.Volume = theRadio.TXCWMonitorGain;
                }
            }
            return CWMon.Started;
        }

        private void CWMonStop()
        {
            Tracing.TraceLine("CWMonStop", TraceLevel.Info);
            lock (CWMon)
            {
                CWMon.Stop();
            }
        }

        private void CWMonDone()
        {
            Tracing.TraceLine("CWMonDone", TraceLevel.Info);
            lock (CWMon)
            {
                CWMonStop();
                CWMon.Close();
            }
        }
#endif
        #endregion

        // region - cw
        #region cw
        private enum cwBufferState
        {
            stop,
            normal,
            buffering
        }

        class cwText
        {
            public string Text;
            public cwBufferState State;

            public cwText() { }

            public cwText(cwBufferState s)
            {
                State = s;
            }

            public cwText(string str)
            {
                State = cwBufferState.normal;
                Text = str;
            }
        }

        private cwText cwBuffer = new cwText();

        public bool CWBuffering
        {
            get
            {
                lock (cwBuffer)
                {
                    return (cwBuffer.State == cwBufferState.buffering);
                }
            }
            set
            {
                lock (cwBuffer)
                {
                    if (value == CWBuffering) return; // no change
                    if (value)
                    {
                        cwBuffer.State = cwBufferState.buffering;
                    }
                    else
                    {
                        string temp = string.Copy(cwBuffer.Text);
                        cwBuffer.State = cwBufferState.normal;
                        cwBuffer.Text = "";
                        SendCW(temp);
                    }
                }
            }
        }

        /// <summary>
        /// Send or buffer cw text.
        /// </summary>
        /// <param name="str">the text string</param>
        public bool SendCW(string str)
        {
            lock (cwBuffer)
            {
                if (CWBuffering)
                {
                    cwBuffer.Text += str;
                }
                else
                {
                    q.Enqueue(new cwText(str), "SendCW");
                }
            }
            return true;
        }

        public bool SendCW(char c)
        {
            if (CWBuffering)
            {
                cwBuffer.Text += c;
            }
            else
            {
                q.Enqueue(c);
            }
            return true;
        }

        /// <summary>
        ///  Immediat cw stop, clears buffer, but leaves buffering on if was on.
        /// </summary>
        public void StopCW()
        {
            lock (cwBuffer)
            {
                cwBuffer.Text = "";
            }
            q.Enqueue(new cwText(cwBufferState.stop), "StopCW");
        }

        public void CWZeroBeat()
        {
            ulong freq = 0;
            freq = FilterObj.ZeroBeatFreq();
            Tracing.TraceLine("CWZeroBeatFreq:" + freq.ToString(), TraceLevel.Info);
            if (freq != 0)
            {
                RITData r = RIT;
                if (r.Active)
                {
                    r.Value = (int)((long)freq - (long)RXFrequency);
                    RIT = r;
                }
                else RXFrequency = freq;
            }
        }
        #endregion

        #region parms
        /// <summary>
        /// Receive decoded CW.
        /// </summary>
        /// <param name="txt">the text string</param>
        public delegate void DCWText(string txt);
        /// <summary>
        /// Format the frequency for display
        /// </summary>
        /// <param name="freq">a ulong</param>
        /// <returns>string to display</returns>
        public delegate string FormatFreqDel(ulong freq);
        /// <summary>
        /// format a frequency string for the radio.
        /// </summary>
        /// <param name="str"></param>
        /// <returns>a ulong frequency</returns>
        public delegate ulong FormatFreqForRadioDel(string str);
        /// <summary>
        /// Get the displayable SWR.
        /// </summary>
        /// <returns>SWR string</returns>
        public delegate string GetSWRTextDel();
        /// <summary>
        /// rig-dependent next value of this field.
        /// </summary>
        public delegate void NextValue1Del();

        /// <summary>
        /// Callout vector
        /// </summary>
        public class OpenParms
        {
            /// <summary>
            /// the program name.
            /// </summary>
            public string ProgramName;
            public DCWText CWTextReceiver { get; set; }
            internal void safeCWTextReceiver(string txt)
            {
                try { CWTextReceiver(txt); }
                catch (Exception ex)
                { Tracing.ErrMessageTrace(ex, false, false); }
            }
            /// <summary>
            /// Format a frequency for display
            /// </summary>
            public FormatFreqDel FormatFreq;
            /// <summary>
            /// format a string frequency for the radio
            /// </summary>
            public FormatFreqForRadioDel FormatFreqForRadio;
            /// <summary>
            /// Go to the home field.
            /// </summary>
            public delegate void GotoHomeDel();
            /// <summary>
            /// Go to the home field.
            /// </summary>
            public GotoHomeDel GotoHome;
            /// <summary>
            /// Configuration directory
            /// </summary>
            public string ConfigDirectory;
            /// <summary>
            /// Name of audio device selection file.
            /// </summary>
            public string AudioDevicesFile;
            public delegate string GetOperatorNameDel();
            /// <summary>
            /// Function to retrieve the current operator's name.
            /// </summary>
            public GetOperatorNameDel GetOperatorName;
            internal string OperatorName { get { return GetOperatorName(); } }
            /// <summary>
            /// Station name
            /// </summary>
            public string StationName;
            /// <summary>
            /// Braille display cells
            /// </summary>
            public int BrailleCells;
            /// <summary>
            /// Operator's license class.
            /// </summary>
            public Bands.Licenses License;
            /// <summary>
            /// Send CW with no preprocessing.
            /// </summary>
            public bool DirectSend;
            /// <summary>
            /// panning field
            /// </summary>
            public Control PanField;
            /// <summary>
            /// Parent window for modal dialogs (auth forms, errors).
            /// Ensures focus returns to the app after dialog closes.
            /// </summary>
            public System.Windows.Forms.IWin32Window ParentWindow;
            /// <summary>
            /// Get the displayable SWR.
            /// </summary>
            public GetSWRTextDel GetSWRText = null;
            /// <summary>
            /// rig-dependent next value.
            /// </summary>
            public NextValue1Del NextValue1;
            /// <summary>
            /// List of user's profiles
            /// </summary>
            public List<Profile_t> Profiles;

            /// <summary>
            /// Ask the application to persist the operator's record, after the
            /// radio layer has changed <see cref="Profiles"/>.
            ///
            /// Added Sprint 32 Track H. The list handed in above is the SAME
            /// object the application holds, so mutating it here is already
            /// visible to the app — but only in memory. The WinForms Profile
            /// dialog wrote the operator's file itself after every add, update
            /// and delete; the WPF one could not, because the Radios layer sits
            /// below the application and has no idea where that file lives.
            /// This is the one line that closes the gap. Optional: unwired means
            /// changes live for the session and no more.
            /// </summary>
            public Action SaveOperator;
        }
        /// <summary>
        /// Callout vector provided at open(). MUST be public: this field
        /// shadows <see cref="AllRadios.Callouts"/> (which is a different
        /// type — <see cref="AllRadios.OpenParms"/> vs this class's
        /// <see cref="FlexBase.OpenParms"/> — and is only ever set to a
        /// blank stub in the base constructor). If this field is less
        /// accessible than the base field, external callers' name lookup
        /// binds `rig.Callouts` to the blank base field instead of this
        /// wired one, and every `rig.Callouts.X(...)` delegate call NREs.
        /// Diagnosed 2026-04-16 (Don's BUG-058). Do not downgrade access.
        /// </summary>
        public OpenParms Callouts;
        internal string ConfigDirectory { get { return Callouts.ConfigDirectory; } }
        internal string OperatorName { get { return Callouts.OperatorName; } }
        /// <summary>
        /// Operator's directory for rig-specific stuff.
        /// </summary>
        internal string OperatorsDirectory { get { return ConfigDirectory + "\\" + OperatorName; } }

        // Discovery cascade cache — 4.1-line write-only backport. Populates
        // radioConnectionCacheV1.xml on every successful Connect so the
        // 4.2-line cascade (Rung 1a CachedLanIp) can short-circuit UDP
        // discovery on first launch after upgrade. See
        // memory/project_autoconnect_no_ip_dead_end.md.
        private RadioConnectionCache _radioConnectionCache;

        private RadioConnectionCache GetRadioConnectionCache()
        {
            if (_radioConnectionCache == null)
            {
                var dir = Callouts?.ConfigDirectory ?? "";
                _radioConnectionCache = new RadioConnectionCache(dir);
            }
            return _radioConnectionCache;
        }

        private void RecordConnectedRadioForCache()
        {
            try
            {
                if (theRadio == null) return;
                GetRadioConnectionCache().RecordConnectedRadio(theRadio);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RecordConnectedRadioForCache: {ex.GetType().Name} {ex.Message}", TraceLevel.Warning);
            }
        }

        // Formatters from callouts.
        internal static FormatFreqDel FormatFreq;

        /// <summary>
        /// handle an operator change
        /// </summary>
        public void OperatorChangeHandler()
        {
            FilterObj.OperatorChangeHandler();
        }
        #endregion

        /// <summary>
        /// Rig's capabilities
        /// </summary>
        public RigCaps MyCaps;

        /// <summary>
        /// True if the connected radio supports CW autotune.
        /// </summary>
        public bool SupportsCwAutotune => MyCaps?.HasCap(RigCaps.Caps.CWAutoTuneSet) == true;

        /// <summary>
        /// Invoke CW autotune on the active slice, if supported.
        /// </summary>
        /// <param name="isIntermittent">Optional intermittent flag (FlexLib slice auto_tune int=)</param>
        public void CWAutotune(bool? isIntermittent = null)
        {
            if (!SupportsCwAutotune) return;
            if (!HasActiveSlice) return;

            try
            {
                theRadio.ActiveSlice.SendCWAutotuneCommand(isIntermittent);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CWAutotune error: " + ex.Message, TraceLevel.Error);
            }
        }

        protected IFilterControl FilterObj;

        /// <summary>
        /// Public accessor for the filter control adapter (WpfFilterAdapter).
        /// Used by MainWindow to wire pan display callbacks. Sprint 12.
        /// </summary>
        public IFilterControl FilterControl => FilterObj;

        internal class q_t
        {
            //private Queue q;
            private BlockingCollection<object> q;
            public q_t()
            {
                //q = Queue.Synchronized(new Queue());
                q = new BlockingCollection<object>();
            }

            public bool MainLoop { get; set; }

            public int Count { get { return q.Count; } }

            internal class QItem_t
            {
                public string Name;
                public object Item;
                public QItem_t(string name, object item)
                {
                    Name = (name == null) ? "unnamed" : name;
                    Item = item;
                }
            }

            public void Enqueue(object o, string name = null, bool beforeMainLoop = false)
            {
                QItem_t item = new QItem_t(name, o);

                if (!MainLoop)
                {
                    // If can execute before the main loop, do it.
                    if (beforeMainLoop & (o is FunctionDel))
                    {
                        FunctionDel func = (FunctionDel)o;
                        DbgTrace("q.Enqueue:" + name);
                        if (func != null) func();
                        DbgTrace("q.Enqueue:done " + name);
                    }
                    else Tracing.TraceLine("q:outside main loop", TraceLevel.Error);
                }
                else
                {
                    q.Add(item);
                }
            }

            public QItem_t Dequeue()
            {
                return (QItem_t)q.Take();
            }
        }
        internal q_t q;

        public FlexBase(OpenParms p)
        {
            Tracing.TraceLine("Flex constructor", TraceLevel.Info);

            // Route FlexLib's UDP data-plane health messages (VitaSocket + the WAN
            // udp_register loop) into the JJ trace. Without this, WAN UDP failures
            // are only visible under a debugger — the 2026-08-05 hole-punch field
            // test failed with zero trace evidence because of exactly that gap.
            Vita.VitaSocket.TraceSink = s => Tracing.TraceLine("Vita: " + s, TraceLevel.Info);

            theRadio = null;
            _apiInit = false;

            Callouts = p;
            FormatFreq = p.FormatFreq;
            MyCaps = new RigCaps(RigCaps.DefaultCapsList);
            // default tuner type.
            setFlexTunerTypeNotAuto();

            FMToneModes = myFMToneModes;
            // Use the TS590 fm tone values.
            ToneFrequencyTable = myToneFrequencyTable;

            q = new q_t();

            API.ProgramName = p.ProgramName;
            API.IsGUI = true;

            p.NextValue1 = setNextValue1;
            p.GetSWRText = SWRText;

            // Built here, not on demand, and it lives as long as the rig does.
            // The meter inventory is a property of the radio rather than of any
            // window: it has to be following the set from the first connect,
            // because meters arrive late and nothing announces them.
            MeterInventory = new MeterInventory(this);
        }

        /// <summary>
        /// Every meter this radio publishes, with source, range, units, current
        /// value and last-update time, partitioned by source. Never null, and
        /// live from construction — bind to its InventoryChanged rather than
        /// reading it once, because the meter list grows during registration.
        /// </summary>
        public MeterInventory MeterInventory { get; }

        // main thread region
        #region mainThread
        private bool stopMainThread;
        internal delegate void FunctionDel();

#if zero
        private const string JJRadioDefault = "JJRadioDefault";
        internal string CurrentProfile; // profile in-use.
        private string preferredProfile = null;
        private List<string> defaultProfiles;
#endif
        private void mainThreadProc()
        {
            Tracing.TraceLine("mainThreadProc", TraceLevel.Info);
#if zero
            defaultProfiles = new List<string>();
            // add in order of preference.
            if (Callouts.Profiles != null)
            {
                foreach(Profile_t p in Callouts.Profiles)
                {
                    if (p.Default)
                    {
                        preferredProfile = p.Name;
                        break;
                    }
                }
            }
            if (preferredProfile == null) preferredProfile = Callouts.StationName + "profile";
            defaultProfiles.Add(preferredProfile);
            defaultProfiles.Add(JJRadioDefault);
#endif

            try
            {
                // Bail immediately if we're being disposed (test cycle)
                if (stopMainThread || theRadio == null) return;

                // If a default global profile, select it and await the pan and slices.
                if (GetProfileInfo(false))
                {
                    Tracing.TraceLine("flex open:got profile", TraceLevel.Info);
                }
                else
                {
                    setupFromScratch();
                }

                // Set these on every open.
                Tracing.TraceLine("flex open:#VFOs " + MyNumSlices, TraceLevel.Info);

                // Null-guard: theRadio can be nulled by Disconnect() during test cycles
                if (theRadio == null || stopMainThread) return;

                if (!RemoteRig)
                {
                    theRadio.MicInput = "mic";
                }
                if (!await(() =>
                {
                    return theRadio == null || !theRadio.RemoteTxOn;
                }, 1000))
                {
                    Tracing.TraceLine("Flex open:remote tx should be off", TraceLevel.Error);
                }

                if (theRadio == null || stopMainThread) return;

                // Turn the Vox off.
                theRadio.SimpleVOXEnable = false;
                theRadio.CWBreakIn = false;

                // Ok to queue commands now.
                q.MainLoop = true;
                Tracing.TraceLine("flex open:q.mainloop" + q.MainLoop.ToString(), TraceLevel.Info);

                if (theRadio == null || stopMainThread) return;

                cwx = theRadio.GetCWX();
                cwx.Delay = theRadio.CWDelay;
                cwx.Speed = theRadio.CWSpeed;
                cwx.CharSent += new CWX.CharSentEventHandler(charSentHandler);

                // temporary changes for Flex issue #7620.
                issue7620(false);

                if (RemoteRig & !PCAudio)
                {
                    PCAudio = true;
                }

                if (theRadio == null || stopMainThread) return;

                // Setup pan adapter display.
                FilterObj.PanSetup();
                FilterObj.RXFreqChange(theRadio.ActiveSlice);

                raisePowerEvent(true);

                // Enable TX1 RCA by default for compatibility.
                theRadio.TX1Enabled = true;

#if KeepAlive
                keepAlive_t keepAlive = new keepAlive_t(this);
#endif

                // Main loop.
                while (!stopMainThread)
                {
#if KeepAlive
                    if (q.Count != 0)
                    {
                        keepAlive.Done();
                        keepAlive = null;
                    }
#endif

                    while (q.Count > 0)
                    {
                        q_t.QItem_t el = q.Dequeue();
                        try
                        {
                            if (el.Item is FunctionDel)
                            {
                                Tracing.TraceLine("mainLoop:" + el.Name, TraceLevel.Info);
                                FunctionDel func = (FunctionDel)el.Item;
                                if (func != null) func();
                                Tracing.TraceLine("mainLoop:done " + el.Name, TraceLevel.Info);
                            }
                            else if (el.Item is cwText)
                            {
                                cwText cwt = (cwText)el.Item;
                                if (cwt.State == cwBufferState.stop)
                                {
                                    stopCW();
                                }
                                else
                                {
                                    sendCWString(cwt.Text);
                                }
                            }
                            else if (el.Item is char)
                            {
                                sendCWChar(((char)el.Item));
                            }
                        }
                        catch (Exception ex)
                        {
                            Tracing.TraceLine("mainLoop exception:" +
                                ex.Message + Environment.NewLine + ex.StackTrace, TraceLevel.Error);
                        }
                    }

#if KeepAlive
                    if (keepAlive == null)
                    {
                        keepAlive = new keepAlive_t(this);
                    }
#endif

                    Thread.Sleep(25);
                    //Thread.Yield();
                }
#if KeepAlive
                if (keepAlive != null)
                {
                    keepAlive.Done();
                    keepAlive = null;
                }
#endif
                q.MainLoop = false;

                raisePowerEvent(false);
            }
            catch (Exception ex)
            {
                if (SuppressSpeech)
                    Tracing.TraceLine($"mainThreadProc exception (suppressed): {ex.Message}\n{ex.StackTrace}", TraceLevel.Error);
                else
                    Tracing.ErrMessageTrace(ex, true);
            }
        }
        public class cfg7620
        {
            public int BreakinDelay = 300;
            public int SidetonePitch = 600;
            public int SidetoneGain = 55;
            public int TXCWMonitorGain = 55;
            public bool CWReverse = false;
            public bool CWLEnabled = false;
            public IambicValues Keyer = IambicValues.off;
            public int KeyerSpeed = 20;
        }
        private cfg7620 cfgData = new cfg7620();
        private void issue7620(bool writeFlag)
        {
            string fileName = OperatorsDirectory + '\\' + "issue7620.xml";
            Stream cfgStream = null;

            if (writeFlag)
            {
                Tracing.TraceLine("issue7620:write", TraceLevel.Info);
                try
                {
                    cfgStream = File.Open(fileName, FileMode.Create);
                    XmlSerializer xs = new XmlSerializer(typeof(cfg7620));
                    xs.Serialize(cfgStream, cfgData);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("issue7620:write:exception:" + ex.Message, TraceLevel.Error);
                }
                finally
                {
                    if (cfgStream != null) cfgStream.Dispose();
                }
            }
            else
            {
                Tracing.TraceLine("issue7620:read:" + File.Exists(fileName).ToString(), TraceLevel.Info);
                if (File.Exists(fileName))
                {
                    try
                    {
                        cfgStream = File.Open(fileName, FileMode.Open);
                        XmlSerializer xs = new XmlSerializer(typeof(cfg7620));
                        cfgData = (cfg7620)xs.Deserialize(cfgStream);
                        i_BreakinDelay = cfgData.BreakinDelay;
                        i_SidetoneGain = cfgData.SidetoneGain;
                        i_SidetonePitch = cfgData.SidetonePitch;
                        i_CWReverse = cfgData.CWReverse;
                        i_CWL = (cfgData.CWLEnabled) ?
                            OffOnValues.on : OffOnValues.off;
                        i_Keyer = cfgData.Keyer;
                        i_KeyerSpeed = cfgData.KeyerSpeed;
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("issue7620:read:exception:" + ex.Message, TraceLevel.Error);
                    }
                    finally
                    {
                        if (cfgStream != null) cfgStream.Dispose();
                    }
                }
            }
        }

        private string newGlobalProfile;
        /// <summary>
        /// Select the default profile if loaded.
        /// Before calling, call RaisePowerOff(), and PowerOn() when ready afterwards.
        /// </summary>
        /// <returns>true if selected and the info is loaded.</returns>
        /// <remarks>
        /// On an import, we'll wait for radio status of In_Use, then select the profile.
        /// </remarks>
        internal bool GetProfileInfo(bool postImport)
        {
            Tracing.TraceLine("getProfileInfo:" + postImport.ToString(), TraceLevel.Info);
            bool rv = true;

            // See if any default profiles.
            // Await to see if CurrentProfile is in the profile list.
            Tracing.TraceLine("getProfileInfo:awaiting default profile in GlobalProfileList", TraceLevel.Info);
            List<Profile_t> crnt = GetProfilesByType(ProfileTypes.global, GetDefaultProfiles());
            if ((crnt.Count > 0) && await(() =>
            {
                return (theRadio.ProfileGlobalList.Contains(crnt[0].Name));
            }, 3000))
            {
                // load the selected profile.
                Tracing.TraceLine("getProfileInfo:global profile present " + crnt[0].Name, TraceLevel.Info);
                // Select the current profile and wait til loaded.
                globalProfileDesired = crnt[0].Name;
                globalProfileLoaded = false;
                SelectProfile(crnt[0]);
                // Wait til loaded. (long wait)
                if (await(() =>
                {
                    return (globalProfileLoaded);
                }, 20000))
                {
                    Tracing.TraceLine("getProfileInfo:global profile loaded " + crnt[0].Name, TraceLevel.Info);
                }
            }
            else
            {
                if (crnt.Count > 0)
                {
                    // new profile, will get saved.
                    Tracing.TraceLine("GetProfileInfo:new profile" + crnt[0].Name, TraceLevel.Info);
                    newGlobalProfile = crnt[0].Name;
                }
            }

            // Load other profiles
            crnt = GetProfilesByType(ProfileTypes.tx, GetDefaultProfiles());
            if (crnt.Count > 0) SelectProfile(crnt[0]);

            crnt = GetProfilesByType(ProfileTypes.mic, GetDefaultProfiles());
            if (crnt.Count > 0) SelectProfile(crnt[0]);

            // The silent-transmit check (#99). This is where branch
            // diag/don-audio-708 SELECTED a profile when the selection came up
            // empty. That write is correct on your own radio and is an
            // unauthorised change to shared state on anyone else's, so what
            // ships here is the half that needs nobody's permission: say the
            // failure out loud. See CheckMicProfileForSilentTx.
            CheckMicProfileForSilentTx();

            // Record the radio's own autosave setting once per connect (Sprint 32
            // Track H, #117). Reading only — this is here so the question "does
            // this radio already save its own profiles?" can be answered from a
            // trace file the operator can send, instead of by turning the
            // feature on to find out. Cheap, and it is the observation the
            // autosave decision is waiting on.
            Tracing.TraceLine(
                "GetProfileInfo:radio profile autosave="
                + (RadioProfileAutoSave.HasValue
                    ? RadioProfileAutoSave.Value.ToString()
                    : "unknown")
                + ", global selection="
                + (theRadio?.ProfileGlobalSelection ?? "none"),
                TraceLevel.Info);

            // Allocate any free slices.
            if (MyNumSlices < initialFreeSlices)
            {
                Tracing.TraceLine("GetProfileInfo:allocating free slices " + theRadio.PanadaptersRemaining, TraceLevel.Info);
                // Capture the RX/TX slices by identity — the allocation below
                // may insert slices ahead of them, shifting positions.
                // (QB Track J)
                Slice oldRXSlice = VFOToSlice(RXVFO);
                Slice oldTXSlice = VFOToSlice(TXVFO);
                int oldNumSlices = MyNumSlices;
                while (MyNumSlices < initialFreeSlices)
                {
                    int n = MyNumSlices;
                    theRadio.RequestPanafall();
                    if (await(() =>
                    {
                        return (MyNumSlices > n);
                    }, 2000))
                    {
                        //Thread.Sleep(20); // wait a bit
                        //VFOToSlice(n).Mute = true;
                    }
                    else
                    {
                        // It might be there now.
                        if (MyNumSlices == n)
                        {
                            Tracing.TraceLine("GetProfileInfo:free slice not allocated", TraceLevel.Error);
                        }
                    }
                }

                // Restore by identity: follow the captured slice objects to
                // their current positions instead of replaying stored ints.
                // If there was no valid RX/TX slice before allocation, keep
                // whatever the slice-added handlers derived for the new ones.
                if (oldRXSlice != null)
                {
                    _RXVFO = SliceToVFO(oldRXSlice);
                    oldRXSlice.Active = true;
                }
                if (oldTXSlice != null)
                {
                    _TXVFO = SliceToVFO(oldTXSlice);
                    oldTXSlice.IsTransmitSlice = true;
                }
            }

            _TotalNumSlices = theRadio.SliceList.Count;

            if (postImport)
            {
                Tracing.TraceLine("flex import operation complete:" + rv.ToString(), TraceLevel.Info);
                PCAudio = wasPCAudio;
                if (theRadio.ActiveSlice != null)
                {
                    FilterObj.RXFreqChange(theRadio.ActiveSlice);
                }
                raisePowerEvent(true);
                Directory.Delete(importDir, true);
                string msg = (rv) ? importedMsg : importFailMsg;
                MessageBox.Show(msg, statusHdr, MessageBoxButtons.OK);
            }
            return rv;
        }

        /// <summary>
        /// Choose a transmit antenna for a slice on a scratch setup, without
        /// guessing from the receive list. Part of #205.
        /// </summary>
        /// <remarks>
        /// <para>Order of preference: whatever the radio already has, then the
        /// slice's own <c>TXAntList</c>. Never <c>Radio.RXAntList</c>, which is
        /// what this replaced — see the comment at the call site.</para>
        /// <para><c>Slice.TXAntList</c> never returns empty: when the radio has
        /// not sent a list, FlexLib substitutes ANT1/ANT2/XVTR. So a value here
        /// may be a vendor fallback rather than the radio's real capability,
        /// which is why the choice is traced rather than assumed correct.</para>
        /// </remarks>
        private void applyScratchTxAntenna(Slice s)
        {
            if (s == null) return;

            if (!string.IsNullOrEmpty(s.TXAnt))
            {
                Tracing.TraceLine("setupFromScratch:TXAnt already set to " + s.TXAnt
                    + ", leaving it alone", TraceLevel.Info);
                return;
            }

            string[] txList = s.TXAntList;
            if (txList != null && txList.Length > 0)
            {
                s.TXAnt = txList[0];
                Tracing.TraceLine("setupFromScratch:TXAnt was empty, took " + txList[0]
                    + " from the slice's TX antenna list (" + string.Join(",", txList) + ")",
                    TraceLevel.Info);
                return;
            }

            // Deliberately leaves TXAnt unset. An operator who cannot transmit
            // and is told why is better off than one transmitting into a
            // receive antenna nobody chose.
            Tracing.TraceLine("setupFromScratch:TXAnt is empty and the slice reports no TX"
                + " antenna list; leaving it unset rather than guessing (#205)", TraceLevel.Error);
        }

        private bool setupFromScratch()
        {
            bool rv;
            // Radio was reset or never used with JJRadio before this.
            Tracing.TraceLine("setupFromScratch:panadapters:" + theRadio.PanadapterList.Count, TraceLevel.Info);
            // function to get pan adapters.
            //while (theRadio.PanadaptersRemaining > 0)
            while (MyNumSlices != initialFreeSlices)
            {
                int rem = theRadio.PanadaptersRemaining;
                theRadio.RequestPanafall();
                // wait for at least one new pan adapter
                if (!await(() =>
                {
                    return ((theRadio.PanadaptersRemaining < rem) |
                             (MyNumSlices == initialFreeSlices));
                }, 5000))
                {
                    Tracing.TraceLine("setupFromScratch:didn't get a pan adapter " + theRadio.PanadaptersRemaining, TraceLevel.Error);
                    break;
                }
                else
                {
                    Tracing.TraceLine("setupFromScratch:got a pan adapter " + theRadio.PanadaptersRemaining + ' ' + MyNumSlices, TraceLevel.Error);
                }
            }
            rv = (theRadio.PanadaptersRemaining == 0);
            if (rv)
            {
                Tracing.TraceLine("setupFromScratch:have pan and slices:" + MyNumPanadapters, TraceLevel.Info);
                // We have pan adapters and slices, so we're done. Position 0 =
                // lowest letter (mySlices is sorted by radio index), so this
                // activates slice A on a scratch setup. Null-guarded: raw
                // positional access must never crash on a roster race.
                Slice first = VFOToSlice(0);
                if (first != null)
                {
                    first.Active = true;
                    first.Mute = false;
                }
                Tracing.TraceLine("setupFromScratch:have 1 active slice:" + (MyNumSlices - 1), TraceLevel.Info);
                for (int i = 1; i < MyNumSlices; i++)
                {
                    Slice extra = VFOToSlice(i);
                    if (extra != null) extra.Mute = true;
                }

                // --- Antenna and power on a scratch setup (#205) --------------
                //
                // FIXED 2026-08-23. This block did two unsafe things silently,
                // and Noel caught both at the bench: a trace showed his radio
                // going from RFPower:0 to 100 thirteen seconds into a connect
                // he had never configured. His words: "I actually don't think I
                // have power set to 100 in this profile, I never saved it."
                // He hadn't. This did.
                //
                // 1. The TRANSMIT antenna was taken from theRadio.RXAntList[0]
                //    — the RECEIVE list. Nothing makes the first receive
                //    antenna a safe transmit antenna, and on a station whose RX
                //    list begins with a beverage, a loop, or any receive-only
                //    wire, that puts power into something never built to take
                //    it. Slice.TXAntList is the correct source and FlexBase
                //    already reads it (see the TXAntennas property).
                //
                // 2. RFPower was forced to 100 — maximum — and TunePower
                //    followed to 100. Tune power is exactly the setting an
                //    operator keeps low, because a tune carrier into a bad
                //    match is how a finals stage or a tuner gets cooked.
                //
                // Neither was announced, so a blind operator had no way to
                // learn either had changed. Settings are intents: with no saved
                // profile there is no intent to act on, so leave the radio's
                // own persisted values alone and RECORD them instead of
                // overwriting them. See
                // memory/project_settings_are_intents_not_commands.
                Slice rxs = VFOToSlice(RXVFO);
                if (rxs != null) applyScratchTxAntenna(rxs);
                if (CanTransmit)
                {
                    _TXVFO = 0;
                    Slice txs = VFOToSlice(TXVFO);
                    if (txs != null)
                    {
                        txs.IsTransmitSlice = true;
                        applyScratchTxAntenna(txs);
                    }
                    // Was: theRadio.RFPower = 100. Report, do not overwrite.
                    Tracing.TraceLine("setupFromScratch:leaving transmit power as the radio has it"
                        + " — RFPower " + theRadio.RFPower
                        + ", TunePower " + theRadio.TunePower
                        + " (this used to be forced to 100 unconditionally, #205)",
                        TraceLevel.Info);
                    theRadio.CWBreakIn = false;
                    theRadio.CWIambic = false;
                    theRadio.SpeechProcessorEnable = true;
                    theRadio.SimpleVOXEnable = false;

#if zero
                    CurrentProfile = preferredProfile;
                    createProfile();
#endif
                }

                Tracing.TraceLine("setupFromScratch:radio setup", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("setupFromScratch:didn't get pans and slices:" + MyNumPanadapters + ' ' + MyNumSlices, TraceLevel.Error);
            }
            _TotalNumSlices = theRadio.SliceList.Count;
            return rv;
        }

#if zero
        private bool createProfile()
        {
            bool rv = true;
            Tracing.TraceLine("createProfile:" + CurrentProfile, TraceLevel.Info);
            theRadio.CreateTXProfile(CurrentProfile);
            if (!await(() =>
            {
                return theRadio.ProfileTXList.Contains(CurrentProfile);
            }, 2000))
            {
                Tracing.TraceLine("CreateProfile:TX profile not created", TraceLevel.Error);
                rv = false;
            }
            return rv;
        }
#endif

        /// <summary>
        /// Flex Antenna tuner start/stop interrupt argument
        /// </summary>
        public class FlexAntTunerArg
        {
            public string Type;
            public string Status;
            public string SWR; // Good when stopped
            public FlexAntTunerArg(FlexTunerTypes type, ATUTuneStatus status, float swr)
            {
                Type = type.ToString();
                Status = status.ToString();
                SWR = swr.ToString("f1");
            }
            // Used to send a message
            public FlexAntTunerArg(string status)
            {
                Status = status;
                Type = null;
                SWR = null;
            }
        }
        public delegate void FlexAntTunerStartStopDel(FlexAntTunerArg arg);
        /// <summary>
        /// Antenna tuner start/stop event
        /// </summary>
        public event FlexAntTunerStartStopDel FlexAntTunerStartStop;
        internal void RaiseFlexAntTuneStartStop(FlexAntTunerArg arg)
        {
            if (FlexAntTunerStartStop != null)
            {
                Tracing.TraceLine("FlexAntTunerStartStop raised:" + arg.Type + ' ' + arg.Status + ' ' + arg.SWR, TraceLevel.Info);
                FlexAntTunerStartStop(arg);
            }
            else Tracing.TraceLine("FlexAntTunerStartStop not raised", TraceLevel.Verbose);
        }

        /// <summary>
        /// Argument for CapsChangeEvent
        /// </summary>
        public class CapsChangeArg
        {
            public RigCaps NewCaps;
            internal CapsChangeArg(RigCaps caps)
            {
                NewCaps = caps;
            }
        }
        public delegate void CapsChangeDel(CapsChangeArg arg);
        /// <summary>
        /// Raised when rig's capabilities change.
        /// </summary>
        public event CapsChangeDel CapsChangeEvent;
        private void raiseCapsChange(CapsChangeArg arg)
        {
            if (CapsChangeEvent != null)
            {
                Tracing.TraceLine("raiseCapsChange arg:" + +' ' + ((ulong)arg.NewCaps.setCaps).ToString("x"), TraceLevel.Error);
                CapsChangeEvent(arg);
            }
            else Tracing.TraceLine("raiseCapsChange not raised", TraceLevel.Error);
        }

        /// <summary>
        /// FlexControlKnob status
        /// </summary>
        /// <remarks>
        /// This is reported with the KnobOnOffEvent
        /// </remarks>
        public bool KnobStatus { get; set; }

        public delegate void KnobOnOffEventDel(object sender, bool OnOff);
        /// <summary>
        /// FlexKnob on/off event
        /// </summary>
        public event KnobOnOffEventDel KnobOnOffEvent;
        internal void raiseKnobOnnOff(bool onOff)
        {
            KnobStatus = onOff;
            if (KnobOnOffEvent != null)
            {
                KnobOnOffEvent(this, onOff);
            }
        }

        /// <summary>
        /// power status event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="power">event argument</param>
        public delegate void PowerStatusHandler(object sender, bool power);
        /// <summary>
        /// power status event
        /// </summary>
        public event PowerStatusHandler PowerStatus;
        private void raisePowerEvent(bool on)
        {
            bool raise = (PowerStatus != null);
            Tracing.TraceLine("raisePowerEvent:" + on.ToString() + ' ' + raise.ToString(), TraceLevel.Info);
            if (raise)
            {
                PowerStatus(this, on);
            }
        }

        public delegate void TransmitChangeDel(object sender, bool value);
        /// <summary>
        /// Transmit status change event.
        /// </summary>
        public event TransmitChangeDel TransmitChange;
        private void raiseTransmitChange(bool status)
        {
            if (TransmitChange != null)
            {
                Tracing.TraceLine("raising TransmitChange:" + status.ToString(), TraceLevel.Info);
                TransmitChange(this, status);
            }
        }

        // MeterChangedDel / MeterChanged retired in Sprint 32 Track B. Meter
        // sonification subscribes to MeterData instead, which carries the Meter
        // itself; see the meter-inventory section above.

        /// <summary>Raw S-meter value in dBm (before S-unit conversion).</summary>
        public int SMeterRaw => _SMeter;

        private string importDir;
        private bool wasPCAudio;
        internal void ImportProfile(string name)
        {
            // Save the import temp directory.
            importDir = name.Substring(0, name.LastIndexOf('\\'));
            raisePowerEvent(false);

            // If remote audio was on, turn it off.
            wasPCAudio = PCAudio;
            PCAudio = false; // started again in GetProfileInfo().

            // Do the import.
            theRadio.DatabaseImportComplete = false;
            theRadio.SendDBImportFile(name);
        }

        private CWX cwx;
        private void sendCWChar(char c)
        {
            if (theRadio == null) return;
            // send only if in transmit mode or VOX is on
            if (!Transmit & (Vox == OffOnValues.off)) return;

            cwx.Send(c.ToString());
#if CWMonitor
            if (useCWMon)
            {
                CWMon.Send(c);
            }
#endif
        }
        private void sendCWString(string str)
        {
            if ((theRadio == null) | string.IsNullOrEmpty(str)) return;
            // send only if in transmit mode or VOX is on
            if (!Transmit & (Vox == OffOnValues.off)) return;

            cwx.Send(str);
#if CWMonitor
            //sentChars.Append(str);
            if (useCWMon)
            {
                CWMon.Send(str);
            }
#endif
        }

        private void stopCW()
        {
            cwx.ClearBuffer();
#if CWMonitor
            if (useCWMon)
            {
                CWMonStop();
                CWMonStart();
            }
#endif
        }
#endregion

        // region - Memory stuff
#region memories
        /// <summary>
        /// current memory channel or -1.
        /// </summary>
        public int CurrentMemoryChannel
        {
            get
            {
                return ((NumberOfMemories > 0) && (memoryHandling != null)) ?
                  memoryHandling.CurrentMemoryChannel : -1;
            }
            set
            {
                if (memoryHandling != null) memoryHandling.CurrentMemoryChannel = value;
            }
        }

        /// <summary>
        /// Number of memories
        /// </summary>
        public int NumberOfMemories
        {
            get { return (memoryHandling == null) ? 0 : memoryHandling.NumberOfMemories; }
        }

        /// <summary>
        /// Select CurrentMemoryChannel's memory.
        /// </summary>
        /// <returns>true on success</returns>
        public bool SelectMemory()
        {
            Tracing.TraceLine("SelectMemory:" + CurrentMemoryChannel, TraceLevel.Info);
            if (memoryHandling != null)
            {
                return memoryHandling.SelectMemory();
            }
            else return false;
        }

        /// <summary>
        /// Select the named memory.
        /// </summary>
        /// <returns>true on success</returns>
        public bool SelectMemoryByName(string name)
        {
            Tracing.TraceLine("SelectMemoryByName:" + name, TraceLevel.Info);
            if (memoryHandling != null)
            {
                return memoryHandling.SelectMemoryByName(name);
            }
            else return false;
        }

        internal static string FullMemoryName(Memory m)
        {
            string name = (string.IsNullOrEmpty(m.Name)) ? m.Freq.ToString("F6") : m.Name;
            string group = (string.IsNullOrEmpty(m.Group)) ? "" : m.Group + '.';
            return group + name;
        }

        /// <summary>
        /// Get sorted list of full memory names.
        /// </summary>
        public List<string> MemoryNames()
        {
            List<string> rv;
            if (memoryHandling != null)
            {
                rv = memoryHandling.MemoryNames();
            }
            else
            {
                rv = new List<string>();
            }
            return rv;
        }

        /// <summary>
        /// Memory scan group
        /// </summary>
        public class ScanGroup
        {
            public string Name { get; set; }
            public List<string> Members;
            public bool Readonly; // false for a user-group
            public ScanGroup() { }
            public ScanGroup(string name, List<string> members, bool rdonly = false)
            {
                Name = name;
                Members = members;
                Readonly = rdonly;
            }
            public ScanGroup(ScanGroup group, FlexBase parent)
            {
                Name = group.Name;
                Readonly = false; // a user group.
                Members = new List<string>();
                // Add any group member that's still valid.
                foreach (Memory m in parent.theRadio.MemoryList)
                {
                    if (group.Members.Contains(m.Name))
                    {
                        Members.Add(m.Name);
                    }
                }
            }
        }

        /// <summary>
        /// Get reserved scan groups, default is none.
        /// </summary>
        public List<ScanGroup> GetReservedGroups()
        {
            List<ScanGroup> rv = new List<ScanGroup>();
            if (memoryHandling == null) return rv;

            // Get list of all the rig's groups.
            List<string> myGroups = new List<string>();
            foreach(IMemoryElement el in memoryHandling.SortedMemories)
            {
                Memory m = el.Value;
                // if (!string.IsNullOrEmpty(m.Group) && !myGroups.Contains(m.Group))
                if (!myGroups.Contains(m.Group))
                {
                    myGroups.Add(m.Group);
                }
            }
            // Done if no memories.
            if (myGroups.Count == 0) return rv;

            // For each group, add the members.
            foreach(string group in myGroups)
            {
                List<string> memories = new List<string>();
                foreach(IMemoryElement el in memoryHandling.SortedMemories)
                {
                    Memory m = el.Value;
                    if (m.Group == group) memories.Add(FullMemoryName(m));
                }
                // Add the readOnly group.
                rv.Add(new ScanGroup(group, memories, true));
            }
            return rv;
        }
#endregion

        // Used for rig-specific functions.
        public delegate void updateDel();
        /// <summary>
        /// Allow the main program to access the radio's controls (see Flex6300Filter.cs)
        /// </summary>
        public class RigFields_t
        {
            /// <summary>
            /// RigFields form control (WinForms — null for WPF adapter).
            /// </summary>
            public Control RigControl;
            /// <summary>
            /// RigFields update function.
            /// </summary>
            public updateDel RigUpdate;
            /// <summary>
            /// Memory manager instance.
            /// </summary>
            public IMemoryManager Memories;
            /// <summary>
            /// Menu display form (unused).
            /// </summary>
            public Form Menus;
            /// <summary>
            /// Screen fields list (WinForms — null for WPF adapter).
            /// </summary>
            public Control[] ScreenFields;

            /// <summary>
            /// Simplified constructor for WPF adapters (no WinForms controls).
            /// </summary>
            public RigFields_t(updateDel rtn, IMemoryManager mem)
            {
                RigUpdate = rtn;
                Memories = mem;
            }

            /// <summary>
            /// Full constructor for WinForms (Flex6300Filters compatibility).
            /// </summary>
            internal RigFields_t(Control c, updateDel rtn, IMemoryManager mem, Form mnu,
                Control[] s)
            {
                RigControl = c;
                RigUpdate = rtn;
                Memories = mem;
                Menus = mnu;
                ScreenFields = s;
            }

            /// <summary>
            /// Close down resources.
            /// </summary>
            internal void Close()
            {
                if (RigControl != null)
                {
                    RigControl.Dispose();
                    RigControl = null;
                }
                if (Memories is IDisposable d)
                {
                    d.Dispose();
                }
                Memories = null;
                if (Menus != null)
                {
                    Menus.Dispose();
                    Menus = null;
                }
            }
        }
        /// <summary>
        /// Gets the rig-specific fields
        /// </summary>
        public RigFields_t RigFields
        {
            get;
            internal set;
        }

        private IMemoryManager memoryHandling
        {
            get { return (RigFields != null) ? RigFields.Memories : null; }
        }

        /// <summary>
        /// Tone frequencies
        /// </summary>
        public float[] ToneFrequencyTable;

        // Valid tone/CTSS frequencies
        private static float[] myToneFrequencyTable =
        {
            67.0F, 69.3F, 71.9F, 74.4F, 77.0F, 79.7F, 82.5F, 85.4F, 88.5F, 91.5F,
            94.8F, 97.4F, 100.0F, 103.5F, 107.2F, 110.9F, 114.8F, 118.8F, 123.0F,
            127.3F, 131.8F, 136.5F, 141.3F, 146.2F, 151.4F, 156.7F, 162.2F, 167.9F,
            173.8F, 179.9F, 186.2F, 192.8F, 203.5F, 206.5F, 210.7F, 218.1F, 225.7F,
            229.1F, 233.6F, 241.8F, 250.3F, 254.1F, 1750F
        };
    }
}
