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
using Radios.StationConnect;

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

            /// <summary>
            /// The home this ROW was discovered through — true when the radio
            /// object behind it was a WAN one.
            /// </summary>
            /// <remarks>
            /// <para><b>Not "is the current connection remote", and reading it
            /// that way cost a day (task #288).</b> A dual-homed radio picked
            /// from a row that local discovery built carries
            /// <c>Remote = false</c> however the connect actually travels, so
            /// <c>CurrentRig.Remote</c> reported false for a live SmartLink
            /// connection and the retry ladder concluded there was no remote
            /// path to retry. The field trace said "no retry path available
            /// (local or no serial)" during a failure where a local path plainly
            /// existed.</para>
            /// <para>For the LIVE connection's own answer, ask
            /// <see cref="FlexBase.RemoteRig"/> (<c>theRadio.IsWan</c>). This
            /// property describes a picker row and nothing else.</para>
            /// </remarks>
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

            /// <summary>
            /// Station names of the GUI clients the radio reports connected
            /// RIGHT NOW — one entry per client, "" for a client that has not
            /// asserted a station name yet (the name always arrives in a
            /// later update, #402). Carried pre-connect on BOTH sighting
            /// channels: the LAN discovery broadcast and the SmartLink radio
            /// list each deliver gui_client_stations/handles/programs, which
            /// FlexLib parses into <c>Radio.GuiClients</c>.
            /// </summary>
            /// <remarks>
            /// An empty list means the radio reports no GUI clients — or the
            /// sighting channel did not carry client facts (discovery protocol
            /// v1, which no supported firmware still speaks). Both read as
            /// silence on the roster row, which fails in the safe direction:
            /// a false "someone is on it" costs a glance, a false "nobody is
            /// on it" invites keying a transmitter whose owner holds the
            /// transmit slice (#394 — transmit is a mutex).
            /// </remarks>
            public IReadOnlyList<string> GuiClientStations { get; internal set; } =
                Array.Empty<string>();

            /// <summary>
            /// The SmartLink list this sighting was raised from — the session
            /// and the connection generation the list was born on — when the
            /// WAN intake raised it, or when the picker's replay re-raises a
            /// SmartLink row the intake took (the replay carries the list the
            /// intake recorded for that row, since Track L9 — Sol's review of
            /// L8 found it raising the row bare, which read as a fresh live
            /// sighting after any drop). Null for every other sighting: a
            /// local discovery broadcast, a LAN radio's per-broadcast roster
            /// refresh, and the replay of a LAN row. Those carry no WAN
            /// generation, so <see cref="StillCurrent"/> has nothing to ask
            /// and answers yes.
            /// </summary>
            internal WanListProvenance? FromWanList { get; set; }

            /// <summary>
            /// True for a replayed SmartLink row that no list is recorded as
            /// having described — nothing this rig holds vouches that it is
            /// current, so <see cref="StillCurrent"/> answers no and the
            /// picker shows it as last seen (#619, Noel's ruling of
            /// 2026-09-30). Every WAN row reaches <c>myRadioList</c> through
            /// the intake, which records the list first, so this is the
            /// defensive answer for a row that somehow did not; it is never
            /// set on a live raise.
            /// </summary>
            internal bool HeldWithoutAList { get; set; }

            /// <summary>
            /// Whether the list behind this sighting is still the session's
            /// current knowledge, asked of the session NOW — at the moment
            /// the consumer decides, not the moment the intake checked
            /// (#619, Sol's review of L7).
            /// </summary>
            /// <remarks>
            /// <para><b>Why the consumer asks and not only the intake.</b> The
            /// intake asks this same question before it writes anything, and
            /// again after the connect latch; but the ghost sweep, the WAN
            /// bank, the account cache and this event all follow that last
            /// check, and a dial or a transport's death can land between the
            /// check and the raise. Three review rounds each closed one more
            /// consumer that treated the sighting as live without asking.
            /// The rule that ends the class: <b>a consumer that treats a
            /// sighting as LIVE asks at its own decision.</b> The picker's
            /// arrival announcement, its "radios have arrived" close of the
            /// connecting window, its live-row rewrite and the discovering
            /// window's "something answered" are the live treatments; each
            /// refuses a sighting this answers no for, exactly as the intake
            /// would have refused the list had the change landed an instant
            /// earlier. A held row is not touched by a refusal: the rows keep
            /// describing the last list taken, which is what any drop leaves,
            /// and the next current list brings them up to date.</para>
            /// <para>Three of the four clauses behind the answer are one-way:
            /// a generation is never dialed twice, a retired connection is
            /// never un-retired, and a dead transport never reports itself
            /// alive again. The fourth is the operator's intent, which
            /// Disconnect withdraws and Connect restores; if the two land
            /// inside the monitor's wake latency the same connection stays up
            /// and the answer goes no and then yes again. A sighting refused
            /// in that instant costs a held row one push's update, which the
            /// next push brings — the same cost as the intake refusing the
            /// list at its own check in that instant, which it also does.</para>
            /// </remarks>
            public bool StillCurrent()
            {
                if (HeldWithoutAList) return false;
                var from = FromWanList;
                return from == null || from.Value.Session.ListIsCurrent(from.Value.ConnectionGeneration);
            }

            /// <summary>Which list this sighting speaks for, for a trace line:
            /// the session and connection, or "local discovery".</summary>
            public string Origin => FromWanList?.ToString()
                ?? (HeldWithoutAList ? "a held SmartLink row with no list recorded" : "local discovery");

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
        /// discovery is force-restarted. It is NOT cleared when a transport dies
        /// or the session redials: a session keeps its list across a drop for
        /// display, by design, and so does this bank. That is why every entry
        /// records the list that wrote it (<see cref="WanRadioEntry.From"/>),
        /// and why every reader that would treat an entry as news about now
        /// asks that list whether it is still current (#619).</para>
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

            /// <summary>
            /// The SmartLink list that wrote this entry — the session and the
            /// connection generation it was born on — or null when the writer
            /// could not name one. The same record the intake carries with
            /// every list and every sighting carries out
            /// (<see cref="RigData.FromWanList"/>); there is no second kind.
            /// </summary>
            /// <remarks>
            /// <para><b>Why the bank carries it (#619, Sol's review of
            /// L11).</b> The bank outlives the rig that filled it, and a push
            /// nobody consumed still refreshes it. A picker opened later on a
            /// new rig asked the bank "is this radio on SmartLink?" and was
            /// told yes with nothing to question, so after a drop that row read
            /// online, kept its occupancy clause, and the auto-connect timer
            /// could choose it. Every other path to a live-looking SmartLink
            /// row had been given a list to ask; this one had not.</para>
            /// </remarks>
            public WanListProvenance? From;

            /// <summary>
            /// The entry as a sighting a consumer can question: a
            /// <see cref="RigData"/> whose <see cref="RigData.FromWanList"/> is
            /// <see cref="From"/>, so <see cref="RigData.StillCurrent"/> asks
            /// the bank's list exactly as it asks an intake sighting's. Built
            /// once, when the entry is written, so every reader of one entry
            /// questions the same object.
            /// </summary>
            public RigData Sighting;
        }

        private static readonly Dictionary<string, WanRadioEntry> _wanRadiosBySerial =
            new Dictionary<string, WanRadioEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _wanRadiosLock = new object();

        /// <summary>True when the bank holds a SmartLink handle for this
        /// radio — a leg a connect may try. Whether the list behind it is
        /// still current is the bank sighting's question
        /// (<see cref="RadioAvailability(string, out RigData)"/>).</summary>
        private static bool WanKnows(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            lock (_wanRadiosLock) { return _wanRadiosBySerial.ContainsKey(serial); }
        }

        /// <param name="from">The list this radio arrived on. Every writer
        /// that has a list names it. Null records an entry no list vouches
        /// for, which every reader then treats as last seen — the same
        /// defensive answer as a held SmartLink row with no list recorded
        /// (<see cref="RigData.HeldWithoutAList"/>). Unlike the account, a
        /// missing provenance is never filled from the entry it replaces:
        /// the older list did not describe this handle.</param>
        private static void RememberWanRadio(Radio r, string accountId, WanListProvenance? from)
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
                _wanRadiosBySerial[r.Serial] = new WanRadioEntry
                {
                    Radio = r,
                    AccountId = accountId ?? "",
                    From = from,
                    Sighting = BankSighting(r, from),
                };
            }
        }

        /// <summary>
        /// A banked handle as a sighting: the SmartLink half of the radio, as
        /// of the list that wrote the entry. Never raised as an event — it is
        /// handed to a consumer that asks the bank, so the consumer can ask
        /// the list behind the answer whether it is still current. Built
        /// lean, without <see cref="BuildRigData"/>'s per-sighting occupancy
        /// trace: the bank's occupancy has its own reader
        /// (<see cref="TryGetWanGuiClientStations"/>), which asks the same
        /// question first.
        /// </summary>
        private static RigData BankSighting(Radio r, WanListProvenance? from) => new RigData
        {
            Name = string.IsNullOrWhiteSpace(r.Nickname) ? "Unknown" : r.Nickname,
            ModelName = string.IsNullOrWhiteSpace(r.Model) ? "Unknown" : r.Model,
            Serial = r.Serial,
            Remote = true,
            LanAvailable = false,
            WanAvailable = true,
            FromWanList = from,
            HeldWithoutAList = from == null,
        };

        /// <summary>
        /// The banked SmartLink handle for this serial and the sighting that
        /// says which list it came from, read together under the bank's lock
        /// so the two always describe the same entry. Null handle and null
        /// sighting when the bank holds no WAN handle for it.
        /// </summary>
        private static Radio findWanEntry(string serial, out RigData sighting)
        {
            sighting = null;
            if (string.IsNullOrWhiteSpace(serial)) return null;
            lock (_wanRadiosLock)
            {
                if (!_wanRadiosBySerial.TryGetValue(serial, out var entry)
                    || entry.Radio == null || !entry.Radio.IsWan)
                    return null;
                sighting = entry.Sighting;
                return entry.Radio;
            }
        }

        /// <summary>
        /// The banked SmartLink handle for this serial, only when the list
        /// that wrote it is still the session's current knowledge — asked now,
        /// outside the bank's lock. Null otherwise. For a decision that treats
        /// the handle as the radio being on the account's current list.
        /// </summary>
        internal static Radio findCurrentWanRadio(string serial)
        {
            var radio = findWanEntry(serial, out var sighting);
            return radio != null && sighting != null && sighting.StillCurrent() ? radio : null;
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
        /// Which SmartLink account LISTED this serial — "" when no session has.
        /// </summary>
        /// <remarks>
        /// <para><b>Public since #382, and the reason is a data-integrity one.</b>
        /// Held presence sessions mean a radio can arrive from an account other
        /// than the one the operator is working with, and a consumer recording
        /// that sighting has to attribute it to the session that DELIVERED it.
        /// Attributing it to the current account rewrites the roster's
        /// account-per-radio answer to the wrong address, on disk, silently —
        /// and every later decision that reads it (which row is foreign, which
        /// account Enter switches to, which account presence dials next launch)
        /// then follows the wrong answer.</para>
        /// <para>Empty is a real answer and means "no SmartLink session has
        /// listed this radio" — a caller must not read it as the current
        /// account.</para>
        /// </remarks>
        public static string WanAccountForSerial(string serial) => GetWanAccountForSerial(serial);

        /// <summary>
        /// Who was on a WAN radio as of the last SmartLink list that mentioned
        /// it — the roster row's pre-connect occupancy source when no sighting
        /// event has delivered (#394). False when no list currently carries
        /// the serial.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the roster cannot rely on events alone.</b> Presence
        /// pushes are consumed by exactly ONE rig (#386), and between a
        /// teardown and the next engage there is no intake at all — every push
        /// is dropped. But a dropped push still refreshes the static WAN bank
        /// (#402), and <see cref="RememberWanRadio"/> banks the freshly PARSED
        /// objects, GuiClients included. Noel's field trace of 2026-08-30
        /// (17:20, build 4.1.16.1736) caught the cost of reading only half of
        /// that: "OpenTheRadio:rig's open failed" had released the intake,
        /// "list from dbreda@example.com with no intake — dropped" repeated for
        /// the life of the session, and the roster row rendered Don's radio
        /// "remote via SmartLink" — live off this bank, via availability —
        /// with no occupancy clause while Don sat on it. The live-ness and
        /// the client list come from the SAME pushed objects; this accessor
        /// is the half nothing was reading.</para>
        /// <para>A snapshot as of the last push: exactly as stale or as fresh
        /// as the WAN availability verdict built from the same entries, which
        /// is the honest best a pre-connect surface can do.</para>
        /// <para><b>Delivered only while that push's list is current (#619,
        /// Sol's review of L11).</b> A client list is a statement about who is
        /// on the radio NOW; the bank keeps its entries across a SmartLink
        /// drop, and an occupancy count read from a list nobody holds any
        /// more would be offered as current at the exact moment an operator
        /// is deciding whether to key a transmitter. So this asks the entry's
        /// list, at the moment it is called, and answers false — not
        /// delivered — when it is no longer current. The row then says
        /// nothing about occupancy, which is what a last-seen row says.</para>
        /// </remarks>
        public static bool TryGetWanGuiClientStations(string serial, out IReadOnlyList<string> stations)
        {
            stations = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(serial)) return false;
            Radio banked;
            RigData sighting;
            lock (_wanRadiosLock)
            {
                if (!_wanRadiosBySerial.TryGetValue(serial, out var entry) || entry.Radio == null)
                    return false;
                banked = entry.Radio;
                sighting = entry.Sighting;
            }
            // Asked outside the bank's lock: the question takes the session's.
            if (sighting == null || !sighting.StillCurrent())
            {
                Tracing.TraceLine(
                    $"occupancy[bank]: {serial} banked from {sighting?.Origin ?? "no list"}, which is no longer that session's current knowledge — not delivered",
                    TraceLevel.Info);
                return false;
            }
            lock (banked.GuiClientsLockObj)
            {
                stations = banked.GuiClients.Select(c => c.Station ?? "").ToList();
            }
            Tracing.TraceLine($"occupancy[bank]: {serial} stations={stations.Count}", TraceLevel.Info);
            return true;
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
        /// The WAN-path <see cref="Radio"/> the bank holds for this serial, or
        /// null when it holds none. A handle to DIAL, not evidence that the
        /// radio is on the account's list now: the bank keeps its entries
        /// across a drop. A decision that needs the latter asks
        /// <see cref="findCurrentWanRadio"/>.
        /// </summary>
        private static Radio findWanRadio(string serial) => findWanEntry(serial, out _);

        /// <summary>
        /// True when this radio is reachable both on the local network and
        /// through the current SmartLink account — the case where the operator
        /// gets to choose the path. "Reachable through SmartLink" is a claim
        /// about now, so the bank's list is asked whether it is still current
        /// (#619): a handle left from a dropped session is a leg to try, not
        /// a second home.
        /// </summary>
        public bool IsDualHomed(string serial)
        {
            var (lan, wan) = RadioAvailability(serial, out var wanSighting);
            return lan && wan && wanSighting != null && wanSighting.StillCurrent();
        }

        /// <summary>
        /// Serial → <see cref="Environment.TickCount64"/> of the last LAN
        /// discovery evidence for that radio: seeded when the LAN sighting is
        /// added, refreshed on every discovery broadcast (FlexLib re-raises
        /// GuiClients per packet, about once a second), and retired only by a
        /// genuine discovery loss — never by our own disconnect's lifecycle
        /// removal. This is what keeps <see cref="RadioAvailability"/> honest
        /// through the hole our own teardown tears in <c>myRadioList</c>: on
        /// 2026-08-30 that hole was 119 ms wide, the connect walk asked inside
        /// it, and a radio whose broadcast arrived 100 ms later was declared
        /// "not on the LAN" (#402).
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lanLastSeenTicks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How long a LAN sighting stays credible with no list entry behind
        /// it. Discovery broadcasts arrive about once a second and FlexLib's
        /// own discovery timeout is 17 s (API.RADIOLIST_TIMEOUT_SECONDS), so
        /// ten seconds bridges every bookkeeping hole while staying inside
        /// the window FlexLib itself considers current.
        /// </summary>
        internal const int LanRecencyWindowMs = 10000;

        /// <summary>Pure so the window arithmetic is pinned by tests.
        /// A zero <paramref name="lastSeenTick"/> means never seen.</summary>
        internal static bool LanSeenRecently(long lastSeenTick, long nowTick, int windowMs = LanRecencyWindowMs)
            => lastSeenTick > 0 && nowTick >= lastSeenTick && (nowTick - lastSeenTick) <= windowMs;

        /// <summary>
        /// Which paths reach this radio right now. The RadioRemoved event says a
        /// radio left without saying which home it left, so the selector asks
        /// this before deciding whether "went offline" is even true — a
        /// dual-homed radio dropping off the LAN is still perfectly reachable
        /// through SmartLink.
        /// </summary>
        /// <remarks>
        /// <c>wan</c> means the bank holds a SmartLink handle — a leg a connect
        /// may try. It does NOT say the list behind that handle is current; a
        /// caller that would treat the SmartLink half as online asks
        /// <see cref="RadioAvailability(string, out RigData)"/> for the list
        /// too. Every caller of this form reads <c>lan</c>, or only needs a leg.
        /// </remarks>
        public (bool lan, bool wan) RadioAvailability(string serial) => RadioAvailability(serial, out _);

        /// <summary>
        /// Which paths reach this radio, and — for the SmartLink path — which
        /// list says so.
        /// </summary>
        /// <param name="serial">The radio.</param>
        /// <param name="wanSighting">When <c>wan</c> is true, the bank's
        /// sighting for this radio: <see cref="RigData.FromWanList"/> is the
        /// list that wrote the entry, so <see cref="RigData.StillCurrent"/>
        /// answers whether that list is still the session's current knowledge,
        /// asked at the moment the caller decides. Null when <c>wan</c> is
        /// false.</param>
        /// <remarks>
        /// <para><b>Why the SmartLink answer carries its list (#619, Sol's
        /// review of L11).</b> The bank outlives the rig that filled it and
        /// keeps its entries across a drop, and a push no rig consumed still
        /// refreshes it. So a picker opened on a NEW rig, whose own replay
        /// raises nothing for a radio, got <c>wan=true</c> here for its roster
        /// row with nothing to question. After the drop that row read online,
        /// kept its occupancy clause, and the auto-connect timer could choose
        /// it — the one path to a live-looking SmartLink row that had not been
        /// given a list to ask. Noel's ruling of 2026-09-30 applies to it as
        /// to every other: a radio from a list that is no longer current reads
        /// as LAST SEEN, never online, and stays selectable. <c>wan</c> stays
        /// true, because the leg is still one a connect may try; the sighting
        /// is what lets the caller tell last seen from online.</para>
        /// </remarks>
        public (bool lan, bool wan) RadioAvailability(string serial, out RigData wanSighting)
        {
            wanSighting = null;
            if (string.IsNullOrWhiteSpace(serial)) return (false, false);
            bool wan = findWanEntry(serial, out wanSighting) != null;
            bool recent = _lanLastSeenTicks.TryGetValue(serial, out var seen)
                && LanSeenRecently(seen, Environment.TickCount64);
            try
            {
                // ToList first: myRadioList is appended to from the discovery
                // thread, and this runs on the UI thread every time a radio
                // leaves. An enumeration that throws here would take the picker
                // down over a bookkeeping question.
                bool lan = myRadioList.ToList().Any(x => x.Serial == serial && !x.IsWan);
                if (!lan && recent)
                {
                    // The list is momentarily empty of this serial but the
                    // radio's own broadcasts are current — the empty list is
                    // bookkeeping (our own disconnect's lifecycle removal),
                    // not network truth. Say so, because the connect walk
                    // reads this answer to decide whether a local leg exists.
                    Tracing.TraceLine(
                        $"RadioAvailability({serial}): no list entry but LAN discovery evidence is {Environment.TickCount64 - seen} ms old — reporting lan=true",
                        TraceLevel.Info);
                    lan = true;
                }
                return (lan, wan);
            }
            catch (InvalidOperationException ex)
            {
                Tracing.TraceLine($"RadioAvailability({serial}): list changed mid-read: {ex.Message}", TraceLevel.Warning);
                return (recent, wan);
            }
        }

        /// <summary>FlexLib's RadioAdded, for a radio local discovery found.
        /// The shape the vendor's delegate needs; the sighting carries no WAN
        /// list.</summary>
        private void radioAddedHandler(Radio r) => radioAddedHandler(r, null);

        /// <param name="fromWanList">The SmartLink list this radio arrived on,
        /// when the WAN intake is adding it; null for local discovery. Carried
        /// on the sighting so its consumers can ask whether that list is
        /// still current when they decide (#619).</param>
        private void radioAddedHandler(Radio r, WanListProvenance? fromWanList)
        {
            Tracing.TraceLine("radioAddedHandler:" + r.Serial, TraceLevel.Info);
            // Ignore entries without a serial; SmartLink returns unusable shells before auth completes.
            if (string.IsNullOrWhiteSpace(r.Serial))
            {
                Tracing.TraceLine("radioAddedHandler: ignored radio with empty serial", TraceLevel.Warning);
                return;
            }
            myRadioList.Add(r);
            // The list it arrived on, so the bank can be asked about it later
            // exactly as this sighting can (#619). Only the WAN intake adds a
            // WAN radio here, and it always names its list.
            if (r.IsWan) RememberWanRadio(r, null, fromWanList);
            else
            {
                _lanLastSeenTicks[r.Serial] = Environment.TickCount64;
                WatchDiscoveryGuiClients(r);
            }
            RaiseRadioFound(null, BuildRigData(r, fromWanList));
        }

        /// <summary>
        /// Serial → the last GUI-client roll call this instance raised to the
        /// roster, so the per-broadcast GuiClients refresh (FlexLib re-merges
        /// the list on every discovery packet, roughly once a second per
        /// radio) only re-raises RadioFound when membership or a station name
        /// actually changed.
        /// </summary>
        private readonly Dictionary<string, string> _occupancyRaised =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Keep a LAN radio's roster row honest about who is on it while the
        /// picker sits open (#394). A LAN radio raises RadioFound once, at
        /// first sighting; without this watch, an owner connecting five
        /// minutes later changes <c>Radio.GuiClients</c> (FlexLib merges every
        /// broadcast) and the row keeps reporting the radio empty — a stale
        /// answer to exactly the question the operator reads before keying.
        /// </summary>
        /// <remarks>
        /// LAN only, on purpose: a WAN radio's GuiClients only ever change
        /// inside <c>wanRadioListReceivedHandler</c>, which already re-raises
        /// RadioFound for the merged object on every server push; watching it
        /// here too would raise the same fact twice.
        /// </remarks>
        private void WatchDiscoveryGuiClients(Radio r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Serial)) return;
            // Seed with the state the add-time RadioFound is about to carry,
            // so the first per-broadcast refresh does not re-raise an
            // unchanged fact.
            lock (_occupancyRaised) { _occupancyRaised[r.Serial] = OccupancySignature(r); }
            r.PropertyChanged -= discoveryGuiClientsChangedHandler;
            r.PropertyChanged += discoveryGuiClientsChangedHandler;
        }

        private void UnwatchDiscoveryGuiClients(Radio r)
        {
            if (r == null) return;
            try { r.PropertyChanged -= discoveryGuiClientsChangedHandler; } catch { }
            if (!string.IsNullOrWhiteSpace(r.Serial))
                lock (_occupancyRaised) { _occupancyRaised.Remove(r.Serial); }
        }

        /// <summary>Handle:station per client — the facts the roster row
        /// speaks, and nothing else, so cosmetic churn cannot re-raise.</summary>
        private static string OccupancySignature(Radio r)
        {
            lock (r.GuiClientsLockObj)
            {
                return string.Join("|",
                    r.GuiClients.Select(c => c.ClientHandle.ToString() + ':' + (c.Station ?? "")));
            }
        }

        private void discoveryGuiClientsChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            // UpdateGuiClientsList raises this name unconditionally on every
            // discovery broadcast; the signature gate below turns that into
            // "raise only when the answer changed".
            if (e.PropertyName != "GuiClients") return;
            if (!(sender is Radio r) || string.IsNullOrWhiteSpace(r.Serial)) return;
            // Every broadcast lands here (the raise is unconditional), so this
            // is the LAN-recency heartbeat — refreshed BEFORE the signature
            // gate, which only decides whether the roster needs re-telling.
            _lanLastSeenTicks[r.Serial] = Environment.TickCount64;
            string sig = OccupancySignature(r);
            lock (_occupancyRaised)
            {
                if (_occupancyRaised.TryGetValue(r.Serial, out var prev) && prev == sig) return;
                _occupancyRaised[r.Serial] = sig;
            }
            Tracing.TraceLine(
                $"discoveryGuiClientsChanged: {r.Serial} clients now [{sig}] — re-raising RadioFound so the roster row says so",
                TraceLevel.Info);
            RaiseRadioFound(null, BuildRigData(r));
        }

        /// <summary>
        /// Describe a radio for the selector, filling in BOTH homes. The
        /// discovering path tells us one of them; the WAN dictionary answers for
        /// the other, and it is consulted every time because a LAN radio
        /// re-announces itself long after the SmartLink list has landed.
        /// </summary>
        /// <param name="fromWanList">The SmartLink list this sighting speaks
        /// for, when the WAN intake is raising it; null for every other
        /// sighting. See <see cref="RigData.StillCurrent"/>.</param>
        private static RigData BuildRigData(Radio r, WanListProvenance? fromWanList = null)
        {
            var rd = new RigData();
            rd.FromWanList = fromWanList;
            rd.Name = string.IsNullOrWhiteSpace(r.Nickname) ? "Unknown" : r.Nickname;
            rd.ModelName = string.IsNullOrWhiteSpace(r.Model) ? "Unknown" : r.Model;
            rd.Serial = r.Serial;
            // Remote keeps its existing meaning — the path this row connects by
            // unless the user says otherwise — and LAN still wins, because it is
            // the better path.
            rd.Remote = r.IsWan;
            rd.LanAvailable = !r.IsWan;
            rd.WanAvailable = r.IsWan || WanKnows(r.Serial);
            // Who is on it, as of this sighting. For a dual-homed radio the
            // object here is the merged one — wanRadioListReceivedHandler
            // folds the server's GuiClients into the LAN object — so one read
            // covers both channels.
            lock (r.GuiClientsLockObj)
            {
                rd.GuiClientStations = r.GuiClients.Select(c => c.Station ?? "").ToList();
            }
            // One counted line per hop the occupancy fact travels (#394): a
            // dropped baton along this chain reads exactly like "nobody is on
            // it", and the 2026-08-30 field trace is what a silent drop costs.
            Tracing.TraceLine(
                $"occupancy[rigdata]: {r.Serial} stations={rd.GuiClientStations.Count}",
                TraceLevel.Info);
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

        /// <summary>
        /// Why a <see cref="Radio"/> object left FlexLib's list. FlexLib raises
        /// ONE event for three different stories, and until #402 this class
        /// told all three as "gone from discovery" — false for two of them.
        ///
        /// <para>The tell, from FlexLib itself: the discovery-timeout sweep
        /// (<c>API.RadioListMaid</c>) only ever removes radios with
        /// <c>Connected == false</c>. A radio WE hold a connection to can only
        /// leave the list through <c>Radio.Disconnect()</c> — whose tail
        /// unconditionally calls <c>API.RemoveRadio(this)</c> — and that
        /// Disconnect is either ours (teardown, retry, dispose) or FlexLib's
        /// own reaction to the socket dropping. Neither says ANYTHING about
        /// discovery: on 2026-08-30 the 8600's broadcasts kept arriving the
        /// whole time, and the LAN sighting was re-added 119 ms later.</para>
        /// </summary>
        internal enum RadioRemovalKind
        {
            /// <summary>Our own teardown called Disconnect; the removal is
            /// bookkeeping we caused. Not a discovery statement.</summary>
            SelfInitiated,
            /// <summary>The object we were connected to was retired after its
            /// connection closed unexpectedly. The radio may well still be on
            /// the air; discovery will say within a second.</summary>
            ConnectionLostOurRadio,
            /// <summary>A radio we were NOT connected to aged out of
            /// discovery. The one story the old wording was true for.</summary>
            DiscoveryLoss,
        }

        /// <summary>
        /// Pure classification so the truth table is pinned by tests.
        /// <paramref name="disconnectingFlag"/> is a one-way latch (never
        /// reset for the life of this instance), so it only counts together
        /// with reference identity — never by serial, or a later, genuine
        /// discovery loss of the re-added sighting would be mislabelled and
        /// swallowed.
        /// </summary>
        internal static RadioRemovalKind ClassifyRadioRemoval(
            bool selfDisconnectActive, bool disconnectingFlag, bool sameObject)
        {
            if (sameObject && (selfDisconnectActive || disconnectingFlag))
                return RadioRemovalKind.SelfInitiated;
            if (sameObject)
                return RadioRemovalKind.ConnectionLostOurRadio;
            return RadioRemovalKind.DiscoveryLoss;
        }

        /// <summary>
        /// Only a self-initiated removal is kept away from the roster. Tonight
        /// (#402) the row for a radio broadcasting a few feet away vanished
        /// because our own timeout teardown hung up on it, and 3 ms later the
        /// connect walk read the empty roster as "radio not on the LAN" and
        /// denied the retry its local leg. A radio does not leave the shack
        /// because we hung up on it. The other two kinds still raise: the
        /// roster consumer consults <see cref="RadioAvailability"/> for the
        /// truth, and a genuine discovery loss must keep reaching it.
        /// </summary>
        internal static bool RemovalSuppressesRosterRaise(RadioRemovalKind kind)
            => kind == RadioRemovalKind.SelfInitiated;

        /// <summary>
        /// Only a radio-side loss of OUR radio archives the running diagnostic
        /// capture as <c>connection_dropped</c> (#566's bridge, Sprint 45 Track
        /// H). The operator's own hang-up must not: it is
        /// <see cref="RadioRemovalKind.SelfInitiated"/>, the manifest already
        /// distinguishes the two, and tagging a deliberate disconnect as a drop
        /// would poison the one query the outcome exists to answer. A discovery
        /// loss of some OTHER radio says nothing about our session at all.
        ///
        /// <para>Pure, and pinned by <c>Radios.Tests</c>, for the same reason
        /// as its two neighbours: this truth table is consulted at a moment
        /// nobody is watching and must not rot silently. See
        /// <see cref="CaptureArchive"/> for what the archive does.</para>
        /// </summary>
        internal static bool RemovalArchivesTheCapture(RadioRemovalKind kind)
            => kind == RadioRemovalKind.ConnectionLostOurRadio;

        /// <summary>Nonzero while OUR code is inside a deliberate
        /// <c>theRadio.Disconnect()</c> — FlexLib raises the removal
        /// synchronously on the same thread, so the window is exact.</summary>
        private int _selfDisconnects;

        /// <summary>
        /// This rig's current lifetime, minted from the command transport's identity.
        /// </summary>
        private ConnectionLifetime.Token _connectionToken;
        public RadioConnectionBinding CurrentConnectionBinding { get; private set; }
        public event Action<ConnectionClientReport> ConnectionClientReported;
        private readonly Dictionary<Radio, Action<CommandConnectionChanged>> _connectionHandlers = new();
        private readonly Dictionary<Radio, Action<RadioClientReport>> _clientReportHandlers = new();
        private sealed record CommandRoute(Radio Radio, string Handle, string AccountId);
        private CommandRoute _commandRoute;

        // Capture the executed broker result, never the selected account or a later list lookup.
        private void rememberCommandRoute(Radio radio, string handle, string accountId)
        {
            _commandRoute = new CommandRoute(radio, handle, accountId);
        }

        private void onCommandConnectionChanged(Radio radio, CommandConnectionChanged report)
        {
            if (!ReferenceEquals(report.Connection, radio.CurrentCommandConnection)) return;
            if (report.State == CommandConnectionState.Connecting)
            {
                if (!ReferenceEquals(radio, theRadio)) return;
                _connectionToken = ConnectionLifetime.Bind(report.Connection,
                    radio.Serial + " " + (radio.Nickname ?? ""), out var outcome);
                var route = _commandRoute;
                string account = radio.IsWan && route != null && ReferenceEquals(radio, route.Radio)
                    && string.Equals(radio.WANConnectionHandle, route.Handle, StringComparison.Ordinal)
                    ? route.AccountId : null;
                CurrentConnectionBinding = new RadioConnectionBinding(report.Connection, _connectionToken,
                    radio.Serial, radio.IsWan, account);
                // A new transport's reports are the only ones that describe
                // it: what the last one confirmed is not evidence here (#634).
                resetClientEvidenceForNewConnection();
                ConnectionLifetime.TraceBindOutcome(outcome, _connectionToken, "transport attempt");
                return;
            }
            // A failed attempt never became a connection to lose.
            if (report.State == CommandConnectionState.Disconnected && !report.WasConnected) return;
            var binding = CurrentConnectionBinding;
            // A saved callback for an old connection cannot change or claim the live one.
            if (ReferenceEquals(radio, theRadio)
                && (binding == null || !ReferenceEquals(report.Connection, binding.Connection))) return;
            onRadioConnectedChanged(radio, report, binding?.Lifetime);
        }

        private void onRadioClientReported(Radio radio, RadioClientReport report)
        {
            var binding = CurrentConnectionBinding;
            if (!ReferenceEquals(radio, theRadio) || binding == null
                || !ReferenceEquals(report.Connection, radio.CurrentCommandConnection)
                || !ReferenceEquals(report.Connection, binding.Connection)) return;
            var evidence = new ConnectionClientReport(binding, report);
            // This rig is the first consumer of its own event: the radio's
            // report is what drives the connect's retry flags, the client
            // announcements and the "confirmed by the radio" label (#634,
            // Track L6). Consumed before the event is raised, so a subscriber
            // reading this rig's state after the event sees it already
            // applied.
            consumeClientReport(evidence);
            ConnectionClientReported?.Invoke(evidence);
        }

        // ══ CLIENT EVIDENCE FROM THE RADIO ITSELF (#634, Track L6) ═════════
        //
        // Ruled by Noel 2026-09-26: the radio's own client events drive
        // announcements and retries; the SmartLink list updates what is
        // displayed and the roster, and clients seen only in that list are
        // labelled as such. The vendor raises ONE set of events —
        // GUIClientAdded/Removed/Updated — for every mutation of its client
        // list, whether the radio's TCP status caused it, a LAN discovery
        // broadcast, or the SmartLink list we merge in ourselves, so the
        // handlers on those events cannot tell the radio speaking from a list
        // describing it. Track H gave the producer identity (#637): the
        // command parser now emits RadioClientReport BEFORE it mutates the
        // shared list, only for a line the radio sent on this connection,
        // never for a list or a broadcast. That is the authority here.
        //
        // Everything the ruling names as the radio's business reads THIS
        // state and nothing else: _clientAddedDuringStart and
        // _clientRemovedDuringStart (the station-name wait's abort paths),
        // the "{who} connected/disconnected" announcements, the set of
        // handles the radio has confirmed, and the two facts the narrowed
        // station-name rescue needs — what the radio said our station is
        // called, and whether it reported us gone. Per connection: reset when
        // the transport attempt begins (onCommandConnectionChanged), because
        // a retry opens a new transport and its reports are the only ones
        // that describe it.

        private readonly object _clientEvidenceLock = new object();

        /// <summary>
        /// Handles the radio has reported connected on THIS command
        /// connection, and not since reported disconnected. Membership in
        /// the vendor's list says a client was seen by something; membership
        /// here says the radio itself said so.
        /// </summary>
        private readonly HashSet<uint> _radioConfirmedClients = new HashSet<uint>();

        /// <summary>The station the radio's own Connected report for our
        /// handle carried, or null while the radio has not reported one on
        /// this connection. A different name here means the wait can never
        /// be finished by any overlay.</summary>
        private volatile string _ownStationReportedByRadio;

        /// <summary>The radio has reported our own handle disconnected on
        /// this connection, and not since reported it connected.</summary>
        private volatile bool _ownClientReportedDeparture;

        /// <summary>The station a SmartLink list — from the account this
        /// connection was brokered through — most recently wrote for our
        /// handle into the connected radio object, or null. Names the source
        /// when the rescue, not the radio, finishes the name wait.</summary>
        private volatile string _ownStationSeenInSmartLinkList;

        private void resetClientEvidenceForNewConnection()
        {
            lock (_clientEvidenceLock) _radioConfirmedClients.Clear();
            _ownStationReportedByRadio = null;
            _ownClientReportedDeparture = false;
            _ownStationSeenInSmartLinkList = null;
            StationNameSource = StationNameEvidence.None;
            StationNameRescueSentence = null;
        }

        /// <summary>True when the radio's own status has reported this
        /// handle connected on the current connection and not gone since.</summary>
        internal bool RadioHasConfirmedClient(uint handle)
        {
            lock (_clientEvidenceLock) return _radioConfirmedClients.Contains(handle);
        }

        // ── WHO REPORTED A CLIENT THE RADIO HAS NOT CONFIRMED ──────────────
        //
        // A roster row the radio has not confirmed says which source reported
        // it (#634, Sol's review of L6). Until Track L7 every such row said
        // SmartLink, and a client only a LAN discovery broadcast had listed
        // was shown to an ordinary LAN operator as reported by SmartLink.
        //
        // What is recorded is the source that ADDED the record, per attempt
        // generation and handle, because an add is the only moment the
        // vendor tells us anything: a source that re-lists a record it did
        // not add updates it in place and raises nothing. So:
        //
        //   * added during a SmartLink list merge (the merge's thread-static
        //     flag says so) — SmartLink;
        //   * added outside a merge with no client_id — a discovery
        //     broadcast, because the radio's own status always carries one
        //     and a broadcast never does.
        //
        // A client with no recorded adder — one already in the vendor
        // object's list when we attached (SeedRosterFrom), or a record that
        // carried a client_id without the radio's report on this connection
        // — has an UNKNOWN source, and its row names none (#634, Sol's
        // review of L7). Track L7 named it by the object's own channel — a
        // SmartLink list fills a WAN object, a broadcast a LAN one — which
        // asserted a report nobody observed: the radio's own TCP status adds
        // a client after the session's one list, deliberately unrecorded
        // because it carries a client_id, the vendor keeps it on the reused
        // WAN object across a command connection, and the next attempt's
        // seed then said SmartLink had reported a client SmartLink never
        // listed. A channel is where a record COULD have come from, not
        // where it did. The one thing known about such a client is that the
        // radio has not confirmed it on this connection, so that is all the
        // row says.
        //
        // WHEN ONE CLIENT HAS BOTH SOURCES — a dual-homed radio hears the
        // broadcast and the SmartLink list — the row names the one that
        // added it, the latest to add it since it was last absent. That
        // source really did report the client, so the sentence is true; the
        // other one's re-listing is invisible, and claiming it would be
        // naming a report nobody observed. The radio's own confirmation
        // outranks both.

        private readonly object _clientReportersLock = new object();
        private int _clientReportersAttempt = int.MinValue;
        private readonly Dictionary<uint, ClientRowSource> _clientAddedBy = new Dictionary<uint, ClientRowSource>();

        /// <summary>Record that <paramref name="source"/> added
        /// <paramref name="handle"/> during attempt
        /// <paramref name="attemptGeneration"/>. A newer attempt starts the
        /// record afresh; an older one's note is ignored.</summary>
        private void NoteClientAddedBy(int attemptGeneration, uint handle, ClientRowSource source)
        {
            lock (_clientReportersLock)
            {
                if (attemptGeneration < _clientReportersAttempt) return;
                if (attemptGeneration > _clientReportersAttempt)
                {
                    _clientAddedBy.Clear();
                    _clientReportersAttempt = attemptGeneration;
                }
                _clientAddedBy[handle] = source;
            }
        }

        /// <summary>
        /// The source a row names: the radio when it has confirmed the
        /// client on this connection, otherwise whichever of a SmartLink list
        /// or a discovery broadcast was observed adding it, otherwise
        /// <see cref="ClientRowSource.Unknown"/> — never inferred from the
        /// radio object's channel (see the block comment above).
        /// </summary>
        private ClientRowSource SourceOfClient(uint handle, int attemptGeneration)
        {
            if (RadioHasConfirmedClient(handle)) return ClientRowSource.Radio;
            lock (_clientReportersLock)
            {
                if (attemptGeneration == _clientReportersAttempt
                    && _clientAddedBy.TryGetValue(handle, out var added))
                    return added;
            }
            return ClientRowSource.Unknown;
        }

        /// <summary>
        /// Apply one of the radio's own client reports for this connection.
        /// Runs on the command reader thread, before the vendor mutates its
        /// list for the same line; must not block.
        /// </summary>
        private void consumeClientReport(ConnectionClientReport evidence)
        {
            var report = evidence.Report;
            int gen = AttemptGen;
            switch (report.Kind)
            {
                case RadioClientReportKind.Handle:
                    // The radio has told us which handle is ours. Established
                    // here first, so the station-name wait reads our own
                    // record by the radio's word rather than by a list's.
                    if (clientHandle != report.Handle)
                    {
                        clientHandle = report.Handle;
                        Tracing.TraceLine($"clientReport: our handle is {report.Handle} (radio's own report)", TraceLevel.Info);
                    }
                    RosterTracker.OwnHandleEstablished(report.Handle, gen);
                    // A held post-connect fence may now count our slices (#782).
                    tryRecordConnectFence("our handle being established");
                    break;

                case RadioClientReportKind.Connected:
                {
                    bool mine = clientHandle != noClient && report.Handle == clientHandle;
                    bool alreadyConfirmed;
                    lock (_clientEvidenceLock) alreadyConfirmed = !_radioConfirmedClients.Add(report.Handle);
                    if (!string.IsNullOrEmpty(report.Station) || !string.IsNullOrEmpty(report.Program))
                        _clientIdentitySnapshots[report.Handle] = (report.Station ?? "", report.Program ?? "");

                    if (mine)
                    {
                        // Our own client, by the radio: back, named, and
                        // authoritative about local PTT.
                        _clientRemovedDuringStart = false;
                        _clientAddedDuringStart = true;
                        _ownClientReportedDeparture = false;
                        _ownStationReportedByRadio = report.Station ?? "";
                        if (!string.IsNullOrEmpty(report.ClientId)) clientID = report.ClientId;
                        noteOwnClientIdReported(report.ClientId, "the radio's connected status");
                        _LocalPTT = report.LocalPtt;
                        _lastAuthoritativeLocalPtt = report.LocalPtt;
                        CanTransmit = true;
                        Tracing.TraceLine(
                            $"clientReport: radio reports OUR client {report.Handle} connected station='{report.Station}' localPtt={report.LocalPtt}",
                            TraceLevel.Info);
                    }
                    else if (_clientAddedDuringStart && !alreadyConfirmed)
                    {
                        // Somebody else, by the radio's own word, arriving
                        // after our own client was established. The list and
                        // the broadcast never reach this line (#634).
                        string who = !string.IsNullOrEmpty(report.Station) ? report.Station
                            : !string.IsNullOrEmpty(report.Program) ? report.Program
                            : Lexicon.Get("connect.client.unknown_added");
                        ScreenReaderOutput.Speak(
                            Lexicon.Get("connect.client.connected", ("who", who)),
                            Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                            subject: Speech.SpeechSubject.ClientPresence);
                        ScreenReaderOutput.PlayClientConnectedEarcon?.Invoke();
                    }
                    GuiClientChanged?.Invoke();
                    break;
                }

                case RadioClientReportKind.Disconnected:
                {
                    bool mine = clientHandle != noClient && report.Handle == clientHandle;
                    bool wasConfirmed;
                    lock (_clientEvidenceLock) wasConfirmed = _radioConfirmedClients.Remove(report.Handle);

                    // The radio's own status is the one origin that removes a
                    // roster entry outright. The vendor also removes its
                    // GUIClient for this line, when it still has one, and the
                    // handler on that raise feeds the roster too — but when
                    // a discovery packet had already removed the record, no
                    // raise follows, and this is the only place the departure
                    // reaches the roster (Astra's blocker on #634, answered by
                    // #637).
                    RosterTracker.ClientRemoved(report.Handle, gen, RosterRemovalOrigin.RadioStatus);

                    if (mine)
                    {
                        _clientRemovedDuringStart = true;
                        _clientRemovedTickCount = Environment.TickCount64;
                        _ownClientReportedDeparture = true;
                        Tracing.TraceLine("clientReport: radio reports OUR client disconnected", TraceLevel.Info);
                    }
                    else
                    {
                        _clientIdentitySnapshots.TryGetValue(report.Handle, out var snapshot);
                        string who = !string.IsNullOrEmpty(snapshot.Station) ? snapshot.Station
                            : !string.IsNullOrEmpty(snapshot.Program) ? snapshot.Program
                            : Lexicon.Get("connect.client.unknown_removed");
                        if (wasConfirmed || snapshot.Station != null)
                        {
                            ScreenReaderOutput.Speak(
                                Lexicon.Get("connect.client.disconnected", ("who", who)),
                                Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                                subject: Speech.SpeechSubject.ClientPresence);
                            ScreenReaderOutput.PlayClientDisconnectedEarcon?.Invoke();
                        }
                        _clientIdentitySnapshots.TryRemove(report.Handle, out _);
                    }
                    GuiClientChanged?.Invoke();
                    break;
                }
            }
        }

        /// <summary>Who supplied the station name that finished the wait.</summary>
        public enum StationNameEvidence
        {
            /// <summary>No wait has finished on this connection.</summary>
            None,
            /// <summary>The radio's own client report carried the name.</summary>
            RadioReport,
            /// <summary>A SmartLink list from the account this connection
            /// was brokered through wrote the name into our record before
            /// the radio's report arrived (the narrowed rescue, #634).</summary>
            SmartLinkList,
            /// <summary>A local discovery broadcast wrote the name into our
            /// record before the radio's report arrived (the narrowed
            /// rescue, #634).</summary>
            LocalDiscovery,
        }

        /// <summary>
        /// What finished the last station-name wait on this connection.
        /// Readable state, so it is not lost when speech is (#643); recorded
        /// in the connect profile and the trace as well.
        /// </summary>
        public StationNameEvidence StationNameSource { get; private set; }

        /// <summary>
        /// The operator's sentence for a wait the rescue finished — null when
        /// the radio's own report did, which needs no caveat. DRAFT wording
        /// (Noel's, not ruled): <c>connect.start.station_from_smartlink</c>
        /// and <c>connect.start.station_from_local_network</c>.
        /// </summary>
        public string StationNameRescueSentence { get; private set; }

        /// <summary>
        /// The station-name wait's decision for one poll, pure over the
        /// evidence this connection holds. <paramref name="overlayStation"/>
        /// is what our record on the radio object says right now, whoever
        /// wrote it; the radio's own word is read from the report state.
        /// </summary>
        /// <remarks>
        /// <para>The order is the ruling's (#634). The radio's own report
        /// wins outright: the name it carries finishes the wait, a different
        /// non-empty name means no overlay may finish it (the equality can
        /// never hold — the #402 diagnostic), and an empty name is the radio
        /// enumerating us before it applied anything, which is not a
        /// different name. Departure reported by the radio blocks the rescue
        /// until the radio reports us back. Only then does the overlay
        /// count, and which source wrote it is read from what the list merge
        /// recorded for our handle.</para>
        /// </remarks>
        internal (bool Finished, StationNameEvidence Source, string RescueSentence) StationNameWaitDecision(
            string overlayStation, string requested)
        {
            string radioSaid = _ownStationReportedByRadio;
            if (!string.IsNullOrEmpty(radioSaid))
            {
                return radioSaid == requested
                    ? (true, StationNameEvidence.RadioReport, null)
                    : (false, StationNameEvidence.None, null);
            }
            if (_ownClientReportedDeparture) return (false, StationNameEvidence.None, null);
            if (overlayStation == null || overlayStation != requested) return (false, StationNameEvidence.None, null);

            if (_ownStationSeenInSmartLinkList == requested)
            {
                // FOR NOEL'S PROSE REVIEW: draft 10 of the 2026-09-26 file.
                return (true, StationNameEvidence.SmartLinkList, Lexicon.Get("connect.start.station_from_smartlink"));
            }
            // FOR NOEL'S PROSE REVIEW: draft 11 of the 2026-09-26 file.
            return (true, StationNameEvidence.LocalDiscovery, Lexicon.Get("connect.start.station_from_local_network"));
        }

        /// <summary>
        /// A deliberate teardown disconnect, marked so the synchronous
        /// <c>API.RadioRemoved</c> it triggers is classified as ours
        /// (<see cref="RadioRemovalKind.SelfInitiated"/>) instead of being
        /// read as the radio leaving the network.
        /// </summary>
        private void teardownDisconnect(string reason)
        {
            Tracing.TraceLine($"teardownDisconnect ({reason})", TraceLevel.Info);
            CancelStationAttempt("teardown: " + reason);
            System.Threading.Interlocked.Increment(ref _selfDisconnects);
            try { theRadio?.Disconnect(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"teardownDisconnect ({reason}): {ex.Message}", TraceLevel.Warning);
            }
            finally { System.Threading.Interlocked.Decrement(ref _selfDisconnects); }
        }

        /// <summary>
        /// Set when we have handed the radio a firmware image, so the
        /// connection falling while it restarts is not read as a drop.
        ///
        /// <para><b>On its own it proves nothing, and it no longer exempts
        /// anything on its own</b> (Sprint 45 Track H6, Sol's review finding
        /// 6). It is set before <c>SendUpdateFile</c>, and FlexLib's
        /// <c>SendUpdateFile</c> returns WITHOUT THROWING when the file is
        /// missing or the upgrade port will not parse, and catches a failed
        /// transfer itself — so a completed task is not evidence an image went
        /// anywhere. Kept only until the next <see cref="Connect"/>, the flag
        /// used to misfile a genuine later drop on the same connection as a
        /// firmware restart and archive nothing. So the exemption now needs BOTH
        /// this and FlexLib's own confirmation that an update is in progress
        /// (<see cref="RadioReportsUpdating"/>), and this is cleared on every
        /// path that ends without one: the transfer settling with FlexLib not
        /// updating, the transfer faulting, the call throwing, the exempted
        /// restart itself, and the next <see cref="Connect"/>.</para>
        ///
        /// <para><b>Why this exists at all.</b> While the archive hung off
        /// <c>API.RadioRemoved</c>, a firmware update never reached it:
        /// FlexLib's <c>RemoveRadio</c> returns early for a radio that is
        /// updating. The <c>Connected</c> fall has no such exemption, so moving
        /// the trigger there would have started archiving every firmware update
        /// the operator asked for as <c>connection_dropped</c>, and showing the
        /// drop window over it. FlexLib's own updating flag is internal, so we
        /// keep ours.</para>
        /// </summary>
        private volatile bool _firmwareUpdateSent;

        /// <summary>
        /// Completes when the last firmware transfer's outcome has been
        /// settled against <see cref="_firmwareUpdateSent"/>. Tests await it;
        /// nothing in production does.
        /// </summary>
        private Task _firmwareTransferSettled = Task.CompletedTask;

        /// <summary>
        /// FlexLib's own "this radio is updating" flag: <c>Radio.Updating</c>,
        /// which is internal. Set only once the radio has handed back an
        /// upgrade port and the image is about to go; cleared by FlexLib itself
        /// when the transfer throws. It is the exact condition the OLD trigger
        /// honoured — <c>API.RemoveRadio</c> returns early for an updating
        /// radio — so it is the confirmation the exemption was missing.
        ///
        /// <para>Read by reflection because it is internal and vendor files are
        /// not edited without a ruling. If a FlexLib upgrade renames it this
        /// answers false — no exemption, so a firmware restart would be archived
        /// as a drop, the visible and recoverable failure rather than a silent
        /// one — and <c>ConnectionFallArchiveTests</c> goes red the same
        /// day.</para>
        /// </summary>
        private static readonly System.Reflection.PropertyInfo _flexLibUpdating =
            typeof(Radio).GetProperty("Updating",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>True when FlexLib reports this radio mid-update.</summary>
        internal static bool RadioReportsUpdating(Radio r)
        {
            if (r == null || _flexLibUpdating == null) return false;
            try { return (bool)_flexLibUpdating.GetValue(r); }
            catch { return false; }
        }

        /// <summary>
        /// Whether our connection falling archives the capture. The same
        /// classification the removal handler uses — a hang-up is ours and
        /// does not archive, another object's fall says nothing about our
        /// session — with one more deliberate case: a radio restarting because
        /// we sent it firmware AND FlexLib confirms it is updating. Either half
        /// alone is not a firmware restart. Pure, so the truth table is pinned
        /// by tests.
        /// </summary>
        internal static bool ConnectionFallArchivesTheCapture(RadioRemovalKind kind,
                                                           bool firmwareUpdateSent,
                                                           bool radioUpdating)
            => RemovalArchivesTheCapture(kind) && !(firmwareUpdateSent && radioUpdating);

        /// <summary>
        /// The Radio's <c>Connected</c> property has just gone false. If it was
        /// OUR connection and we did not ask for it, archive the running capture as
        /// <c>connection_dropped</c> (#566's bridge).
        ///
        /// <para><b>THIS IS THE ONE PLACE THE ARCHIVE IS TAKEN, and it is here
        /// because this is the only signal FlexLib raises on every path.</b> On
        /// a transport loss FlexLib sets <c>Connected</c> false, then calls its
        /// own <c>Disconnect</c>, which calls <c>API.RemoveRadio</c> — and
        /// <c>RemoveRadio</c> raises <c>RadioRemoved</c> only for a serial in
        /// its LAN discovery dictionary. A radio reached only through SmartLink
        /// is never in it. Tracks H, H2 and H3 archived on <c>RadioRemoved</c>,
        /// so on Don's path no drop was ever archived; the 8600 on the bench is
        /// on the LAN too, which is why it looked right there.</para>
        ///
        /// <para><b>Exactly once on a dual-homed radio.</b> Both signals arrive
        /// there, <c>Connected</c> false first and <c>RadioRemoved</c> after it,
        /// on the same thread. The removal handler's drop arm is roster
        /// bookkeeping only, so it never asks for an archive; and any repeat of this
        /// call for the same connection is refused by the connection lifetime's
        /// claim inside <see cref="CaptureArchive.AfterConnectionDrop"/>.</para>
        ///
        /// <para>The qualified transport event survives Radio reuse. Its lifetime
        /// token travels into the archive; no lookup by the reused object occurs.</para>
        /// </summary>
        /// <param name="r">The Radio whose <c>Connected</c> fell.</param>
        /// <param name="fall">The session read ONCE at the top of the fall, or
        /// null when nothing was recording then. Every line here and the archive
        /// are bound to it. See onRadioConnectedChanged.</param>
        private void archiveIfOurConnectionDropped(Radio r, ConnectionLifetime.Token token, JJTrace.TraceSessionHandle fall)
        {
            if (r == null) return;
            var kind = ClassifyRadioRemoval(
                selfDisconnectActive: System.Threading.Volatile.Read(ref _selfDisconnects) > 0,
                disconnectingFlag: Disconnecting,
                sameObject: ReferenceEquals(r, theRadio));
            bool firmwareSent = _firmwareUpdateSent;
            bool updating = firmwareSent && RadioReportsUpdating(r);

            // Deferred, all of them: this is FlexLib's transport thread, and
            // nothing on it may wait on the trace gate before the claim. See
            // onRadioConnectedChanged. And bound to the fall's one handle.
            if (!ConnectionFallArchivesTheCapture(kind, firmwareSent, updating))
            {
                bool firmwareRestart = RemovalArchivesTheCapture(kind);
                // The exemption covers ONE restart. Used here, it is spent: a
                // later fall of this object is a drop again.
                if (firmwareRestart) _firmwareUpdateSent = false;
                Tracing.TraceLineDeferred(
                    $"connection fell: {r.Serial} ({r.Nickname}) — {kind}"
                    + (firmwareRestart ? ", the radio restarting for the firmware update we sent (FlexLib reports it updating)" : "")
                    + "; not a drop, nothing archived",
                    TraceLevel.Info, fall);
                return;
            }

            if (firmwareSent)
            {
                // Said out loud because it is exactly the case the old flag got
                // wrong: an image was handed over, but FlexLib says no update is
                // in progress, so this is a real drop and it archives.
                Tracing.TraceLineDeferred(
                    $"connection fell: {r.Serial} — a firmware image was sent on this connection, but FlexLib"
                    + " reports no update in progress, so this is a drop, not a restart",
                    TraceLevel.Warning, fall);
            }

            Tracing.TraceLineDeferred(
                $"connection fell: {r.Serial} ({r.Nickname}) — our connection dropped without us asking; archiving the capture",
                TraceLevel.Warning, fall);

            // Returns at once; the zip happens off this thread, which is
            // FlexLib's own transport thread in the middle of a teardown.
            //
            // The producer token identifies the drop even when the Radio is reused.
            //
            // CLAIM FIRST, THEN COLLECT (#618). The meter window is rendered by
            // the closure, which AfterConnectionDrop calls only after winning
            // the claim; the line then travels as data into the trace boundary
            // and is written to the accepted session or to nothing at all.
            //
            // The fall's one handle goes with it, so the archive names the session
            // the fall's lines were bound to — the four-argument overload,
            // which never reads the handle again; null means nothing was
            // recording when the fall began, and nothing is archived.
            CaptureArchive.AfterConnectionDrop(
                token, r.Nickname ?? "",
                () => collectCaptureMeterFlush(CaptureMeterSet.PartialConnectionDropped),
                fall);
        }

        /// <summary>
        /// Subscribe this rig to a Radio's QUALIFIED producer events — the
        /// connection-state and client reports that carry the
        /// <see cref="CommandConnection"/> they came from — at most once per
        /// radio object. Idempotent on purpose: a Radio object can come back
        /// to <see cref="Connect"/> without this rig having disconnected it —
        /// a failed leg of the connect walk, or a SmartLink reconnect handed
        /// the same object — and a second subscription dispatches every
        /// report twice, the connection falling included.
        ///
        /// <para><b>The radio's ordinary <c>PropertyChanged</c> is NOT wired
        /// here.</b> It belongs to the STATION ATTEMPT and is wired by
        /// <see cref="WireStationHandlers"/> through the attempt's immutable
        /// binding, one wiring per rig, the previous one unwired first.
        /// Until Track H met Track G at the integration merge this helper
        /// subscribed <c>radioPropertyChangedHandler</c> raw as well; the
        /// merged tree would have dispatched every property change twice,
        /// which is the very stacking this helper's idempotency exists to
        /// prevent. Two seams, two helpers: see the comment at the call site
        /// in <see cref="Connect"/> for what each identity is for.</para>
        /// </summary>
        private void wireProducerHandlers(Radio radio)
        {
            if (radio == null) return;
            if (!_connectionHandlers.ContainsKey(radio))
            {
                Action<CommandConnectionChanged> connection = report => onCommandConnectionChanged(radio, report);
                Action<RadioClientReport> client = report => onRadioClientReported(radio, report);
                _connectionHandlers.Add(radio, connection);
                _clientReportHandlers.Add(radio, client);
                radio.CommandConnectionChanged += connection;
                radio.ClientReported += client;
            }
            // The radio's one refusal of a presented GUI client id (#782);
            // idempotent per radio object like the two above.
            wireClientIdentityHandlers(radio);
        }

        /// <summary>
        /// Take this rig's producer handlers off a Radio it is letting go of,
        /// so a later connection on the same object does not carry this one's
        /// handlers as well. The station wiring on the same object is let go
        /// of beside it, by <see cref="UnwireStationHandlers"/>.
        /// </summary>
        private void unwireProducerHandlers(Radio radio)
        {
            if (radio == null) return;
            if (_connectionHandlers.Remove(radio, out var connection)) radio.CommandConnectionChanged -= connection;
            if (_clientReportHandlers.Remove(radio, out var client)) radio.ClientReported -= client;
            unwireClientIdentityHandlers(radio);
        }

        private void apiRadioRemovedHandler(Radio r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Serial)) return;
            if (_suppressApiRemovals)
            {
                Tracing.TraceLine("apiRadioRemovedHandler: suppressed (rescan teardown) " + r.Serial, TraceLevel.Info);
                return;
            }

            var kind = ClassifyRadioRemoval(
                selfDisconnectActive: System.Threading.Volatile.Read(ref _selfDisconnects) > 0,
                disconnectingFlag: Disconnecting,
                sameObject: ReferenceEquals(r, theRadio));

            switch (kind)
            {
                case RadioRemovalKind.SelfInitiated:
                    Tracing.TraceLine(
                        $"apiRadioRemovedHandler: {r.Serial} ({r.Nickname}) released by our own disconnect — FlexLib lifecycle removal, NOT a discovery loss; roster keeps the row",
                        TraceLevel.Info);
                    // THE CONNECTION LIFETIME IS DELIBERATELY NOT TOUCHED HERE.
                    //
                    // The first shape of this retired the lifetime on a
                    // deliberate disconnect, reasoning that a hang-up ends a
                    // connection. It does — but "retired" in this design means
                    // TERMINALLY retired by a claimed LOSS, and that carries the
                    // rule that the object is never rebound. Applying it to a
                    // hang-up made the unresolved SmartLink case reachable by an
                    // ordinary sequence: disconnect, reconnect over SmartLink
                    // (whose handle bank can hand back the same object), radio
                    // dies — and the drop would not have archived, silently losing
                    // the evidence this whole bridge exists to produce.
                    //
                    // Nothing is needed here anyway. A self-initiated removal
                    // never reaches the archive, because RemovalArchivesTheCapture
                    // answers only for ConnectionLostOurRadio. The lifetime
                    // simply spans the hang-up and the reconnect, unclaimed, so
                    // a genuine later drop can still claim it exactly once.
                    break;
                case RadioRemovalKind.ConnectionLostOurRadio:
                    Tracing.TraceLine(
                        $"apiRadioRemovedHandler: {r.Serial} ({r.Nickname}) retired after its connection dropped — the radio may still be on the air; discovery will say",
                        TraceLevel.Info);
                    // ROSTER BOOKKEEPING ONLY. THE ARCHIVE DOES NOT LIVE HERE ANY
                    // MORE, and it must not come back.
                    //
                    // Tracks H, H2 and H3 archived the capture in this arm. FlexLib
                    // never raises RadioRemoved for a radio reached only through
                    // SmartLink: API.RemoveRadio returns without an event when
                    // the serial is not in its LAN discovery dictionary, and only
                    // LAN discovery writes that dictionary. So on a SmartLink-only
                    // path — Don's 6300 — this arm is never reached and no drop
                    // was ever archived, first loss included. It worked on the bench
                    // only because the 8600 is on the LAN as well.
                    //
                    // The archive is taken where the drop is signalled on EVERY path:
                    // the Radio's Connected property falling, in
                    // radioPropertyChangedHandler, which FlexLib raises on this
                    // same thread just BEFORE it calls RemoveRadio. On a
                    // dual-homed radio this arm still arrives afterwards, and
                    // leaving the archive out of it is what keeps that to one archive.
                    // See archiveIfOurConnectionDropped.
                    break;
                default:
                    Tracing.TraceLine($"apiRadioRemovedHandler: {r.Serial} ({r.Nickname}) gone from discovery — removing", TraceLevel.Info);
                    // Only a genuine discovery loss retires the LAN-recency
                    // evidence; our own hang-up says nothing about the LAN.
                    _lanLastSeenTicks.TryRemove(r.Serial, out _);
                    break;
            }

            var mine = myRadioList.FirstOrDefault(x => x.Serial == r.Serial);
            if (mine != null) myRadioList.Remove(mine);
            // Both objects, deliberately: the subscription rides whichever
            // Radio object was handed to radioAddedHandler, and `mine` is not
            // guaranteed to be the same instance as `r`.
            UnwatchDiscoveryGuiClients(r);
            if (!ReferenceEquals(mine, r)) UnwatchDiscoveryGuiClients(mine);
            if (!RemovalSuppressesRosterRaise(kind))
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
        /// <remarks>
        /// <para><b>A SmartLink row is replayed WITH the list it came from
        /// (#619, Sol's review of L8; Noel's ruling of 2026-09-30).</b> The
        /// replay used to raise every held row bare, and a bare sighting has
        /// no WAN generation to ask about, so it answered "current" — and a
        /// WAN row kept across a SmartLink drop reached the picker as a fresh
        /// live sighting: online, the connecting window closed, eligible for
        /// an arrival. Each SmartLink row now carries the list the intake
        /// recorded for it (<see cref="_wanRowFrom"/>), so a consumer asks
        /// that list, at its own decision, whether it is still current. Noel
        /// ruled what the picker does with the answer: the row stays, reads
        /// as last seen, and stays selectable, because connecting is itself
        /// the check. A SmartLink row with no recorded list is marked
        /// historical outright (<see cref="RigData.HeldWithoutAList"/>) —
        /// nothing vouches for it.</para>
        /// <para>A LAN row is replayed bare, as before: it is held only while
        /// discovery keeps hearing it. For a dual-homed radio the row object
        /// is the LAN one, so its SmartLink half still reads from the WAN bank
        /// as it always has; that half is not addressed here.</para>
        /// <para>The recorded list is read under its own small lock, never the
        /// intake's. The intake raises into the picker while holding its
        /// lock and the picker marshals to the UI thread, and this runs ON the
        /// UI thread, so waiting for the intake's lock here could deadlock.</para>
        /// </remarks>
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
                if (!r.IsWan)
                {
                    RaiseRadioFound(null, BuildRigData(r));
                    continue;
                }

                WanListProvenance from = default;
                bool recorded;
                lock (_wanRowFromLock) { recorded = _wanRowFrom.TryGetValue(r.Serial, out from); }
                var rd = BuildRigData(r, recorded ? from : (WanListProvenance?)null);
                if (!recorded) rd.HeldWithoutAList = true;
                Tracing.TraceLine(
                    $"ReplayDiscoveredRadios: {r.Serial} is a held SmartLink row from {rd.Origin} — replayed with that list, so the picker asks whether it is still current",
                    TraceLevel.Info);
                RaiseRadioFound(null, rd);
            }
        }

        /// <summary>
        /// Serial → the SmartLink list this rig's row for that radio was last
        /// taken from. Written by the intake for every radio in a list it
        /// consumes, under <see cref="_wanIntakeLock"/> and then this lock;
        /// read by <see cref="ReplayDiscoveredRadios"/> under this lock alone
        /// (see its remarks for why never the intake's). Per serial rather
        /// than per account (<see cref="_wanRowsFrom"/>) so the replay asks
        /// exactly the list that last described the row, with no attribution
        /// lookup in between. An entry for a serial no longer held is never
        /// read: the replay walks only rows <c>myRadioList</c> still holds.
        /// </summary>
        private readonly Dictionary<string, WanListProvenance> _wanRowFrom =
            new Dictionary<string, WanListProvenance>(StringComparer.OrdinalIgnoreCase);
        private readonly object _wanRowFromLock = new object();

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
            lock (_wanIntakeLock) { _serverListThisCall = null; }
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

        #region Change nothing on this radio (#403)

        // The per-radio hold from RadioConfig.ChangeNothingOnThisRadio, cached
        // here at connect so every writer can consult it without a disk read.
        // See the RadioConfig block for what it is and why it exists. The two
        // helpers below are the only two shapes a gated writer takes:
        //
        //   GuardRefuses — an OPERATOR asked for something and it is not
        //   happening. Speaks, naming this setting, because a refusal the
        //   operator cannot hear reads as the app being broken.
        //
        //   GuardSkips — an AUTOMATIC write on the connect or audio path is
        //   being left undone. Trace-only: the connect-time announcement in
        //   mainThreadProc has already told the operator once, and narrating
        //   every skipped housekeeping write would bury it.

        /// <summary>
        /// True while the connected radio's "Change nothing on this radio"
        /// setting is armed. Loaded from the per-radio config at connect;
        /// refreshed by <see cref="SetChangeNothingActive"/> when the operator
        /// changes the answer mid-session from the Settings Radios tab.
        /// </summary>
        public bool ChangeNothingActive { get; private set; }

        /// <summary>
        /// Arm or lift the change-nothing hold for the live session. Called at
        /// connect (from the per-radio config, before any connect-path write
        /// runs) and from the Settings Radios tab when the operator changes it
        /// while connected to this radio.
        /// </summary>
        public void SetChangeNothingActive(bool active)
        {
            if (ChangeNothingActive == active) return;
            ChangeNothingActive = active;
            Tracing.TraceLine(
                "ChangeNothing: " + (active
                    ? "armed — JJ Flex will not change anything this radio keeps"
                    : "lifted — this radio may be changed normally"),
                TraceLevel.Warning);
        }

        /// <summary>
        /// The operator-action gate. True when the hold is armed, in which
        /// case the refusal has already been spoken — naming the setting and
        /// the action it stopped — and traced. Callers simply return.
        /// </summary>
        /// <param name="actionKey">Lexicon key for the action phrase that
        /// completes "JJ Flex did not {action}" — e.g.
        /// settings.guard.action.rename.</param>
        private bool GuardRefuses(string actionKey)
        {
            if (!ChangeNothingActive) return false;
            Tracing.TraceLine("ChangeNothing: refused " + actionKey, TraceLevel.Warning);
            if (!SuppressSpeech)
            {
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.guard.refused",
                        ("action", Lexicon.Get(actionKey))),
                    VerbosityLevel.Critical, true);
            }
            return true;
        }

        /// <summary>
        /// The automatic-write gate. True when the hold is armed; traces what
        /// was left undone and nothing more — the connect announcement has
        /// already said the radio is being left alone.
        /// </summary>
        private bool GuardSkips(string what)
        {
            if (!ChangeNothingActive) return false;
            Tracing.TraceLine("ChangeNothing: skipped " + what, TraceLevel.Info);
            return true;
        }

        #endregion

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
            if (GuardRefuses("settings.guard.action.rename")) return false;

            Tracing.TraceLine($"RenameRadio: '{radio.Nickname}' -> '{newName}'", TraceLevel.Info);
            radio.Nickname = newName.Trim();

            // A rename through JJ Flex is a deliberate choice: record it in
            // both the observation mirror AND the choice field, so it keeps
            // winning if the hardware name later diverges.
            string chosenName = newName.Trim();
            RadioConfig.Update(radio.Serial, profile => {
                profile.Nickname = chosenName;
                profile.UserNickname = chosenName;
            });

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
        /// True when the radio hardware supports the advanced NR family (NRF, NRS, RNN).
        /// 8000-series and Aurora only — the 6000 series lacks the DSP hardware, and a
        /// SmartSDR+ licence does not change that. The 6000s get NRL, ANFL and ANFT.
        /// <para>
        /// THIS IS THE hardware predicate for the family. Do not write a second one.
        /// FlexRadio's SmartSDR v4.1.3 release notes say "Only available on 8000 Series
        /// Radios" of NRF, NRS and RNN alike, and mark all three footnote 1 (Aurora,
        /// FLEX-8000) while NRL, ANFL and ANFT carry footnote 1,2 (plus FLEX-6000).
        /// The AU-520-only feature is Enhanced Signal Clarity, which needs dual SCUs —
        /// not RNN. A predicate matching AU-52* has confused the two before (#729).
        /// Working: JJFlex-private/planning/research/flexradio-noise-mitigation-model-matrix.md
        /// </para>
        /// </summary>
        public bool NeuralNRHardwareSupported
            => CapabilityHardware.SupportsAdvancedNoiseReduction(RadioModel);

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
        /// Whether the tuner remembers its solutions per frequency.
        /// </summary>
        /// <remarks>
        /// Sprint 48 Track D (#700's neighbour, #110). Exposed for the ATU
        /// Memories dialog, whose door - <see cref="ShowATUMemoriesDialog"/> -
        /// was declared in Sprint 10, invoked by <see cref="AntennaTunerMemories"/>,
        /// and assigned by nothing until now. False also when no radio or no
        /// ATU is reported, which is "not reported", not "proven off" - the
        /// same caveat as <see cref="ATUHardwarePresent"/>.
        /// </remarks>
        public bool ATUMemoriesEnabled
        {
            get => theRadio?.ATUMemoriesEnabled == true;
            set { var r = theRadio; if (r != null) r.ATUMemoriesEnabled = value; }
        }

        /// <summary>Forget every stored tuner solution. No-op without a radio.</summary>
        public void ClearATUMemories() => theRadio?.ATUClearMemories();

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
        /// pass in. That was wrong and misled a live debugging session on
        /// 2026-08-14; the RF truth test written up that day is the record, and
        /// it lives in the private planning estate rather than this repository.
        /// The paragraph above is the finding, so nothing here depends on
        /// reaching it.</para>
        /// </summary>
        public bool SetSmartLinkPortForwarding(bool enabled, int tcpPort, int udpPort)
        {
            if (theRadio == null)
            {
                Tracing.TraceLine("SetSmartLinkPortForwarding: no radio connected", TraceLevel.Warning);
                return false;
            }
            // On the method rather than only in the UI: the 2026-08-29 port
            // incident bypassed the UI gate by calling this directly (#397).
            if (GuardRefuses("settings.guard.action.ports")) return false;
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
                if (GuardRefuses("settings.guard.action.rem_on")) return;
                theRadio.RemoteOnEnabled = value;
                Tracing.TraceLine($"RemoteOnEnabled set: {value}", TraceLevel.Info);
            }
        }

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
        /// Sprint 27 Track D. The help key (for <c>HelpLauncher.ShowHelp</c>) of
        /// the help page most relevant to the current session status + last
        /// network report + account mode. Null when no help page applies (e.g.,
        /// session is Connected and working fine). Despite its name this is a
        /// key, not a filename, since the 2026-10-27 help rework (#826); the
        /// name is kept so a parallel branch using it still compiles.
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
            // A guarded radio's SITE is in the hold too: this writes a
            // forward rule into the router in front of the radio, from an
            // account-scoped value — the port incident's shape one box over
            // (audit 1.9). First, before the tier checks, so the answer does
            // not depend on which account happens to be current.
            if (GuardSkips("UPnP router mapping on connect")) return;
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

            // Fresh connection, fresh autosave knowledge. The radio re-reports
            // its own profile-autosave setting after "sub radio all"; until it
            // does, we must not treat a default-false as a reported-false on a
            // radio that is not ours (#499). See _radioReportedAutosave.
            _radioReportedAutosave = false;

            // Fresh connection: whatever firmware we once sent is behind us, and
            // this connection falling is a drop again.
            _firmwareUpdateSent = false;

            // Fresh connection: no mic input saved from an earlier session is
            // ever put back on this one, and the healer starts a new episode
            // (#686, repair round two; see FlexBase.RemoteAudio.cs).
            ResetPcMicSession("connect to " + serial);

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
            // The slice recorder's status tap is attached only after Connect
            // returns (below): it turns on FlexLib's telnet tracing, whose
            // SendCommand takes s.Substring(0, s.IndexOf(' ')) and throws on a
            // command with no space, such as the "info" that Connect itself
            // sends. Attached here, every connect failed (bench, 2026-10-23).

            // Each transport attempt will bind its own producer identity when it starts.
            rememberCommandRoute(null, null, null);

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
            string observedNickname = theRadio.Nickname;
            RadioConfig.Update(theRadio.Serial, profile => {
                if (!string.IsNullOrEmpty(observedNickname))
                    profile.Nickname = observedNickname;
            });
            var knownRadioProfile = RadioConfig.LoadForRadio(theRadio.Serial);

            // Arm the change-nothing hold BEFORE the handler wiring below —
            // the first connect-path write (TNFEnabled) is twenty lines down,
            // and a guard loaded after the writes it governs is scenery (#403).
            SetChangeNothingActive(knownRadioProfile.ChangeNothingOnThisRadio);

            // Forget the last radio's meters BEFORE anything is subscribed on
            // this one (#625, H18 — Sol's H17 review, blocker 3). This ran
            // after the meter handlers below were subscribed, so a forward,
            // reflected or SWR reading that arrived in between was stored and
            // then disowned by the reset: a later txMeters line said no-sample
            // for a value this connection had reported. Reset first, and
            // nothing that arrives on this connection can be erased by it.
            // Read from the code, not observed: the old order also let
            // micData's inventory pass hook the radio's meters before the
            // reset forgot them, so the next pass would hook them a second
            // time (see syncMeterInventory). It sits ahead of the station
            // attempt as well, because a same-object retry rewires the radio's
            // PropertyChanged inside BeginStationAttempt, and that is a
            // subscription.
            resetMeterInventory();

            // TWO IDENTITIES ARE MINTED FROM HERE, FOR TWO DIFFERENT THINGS,
            // and neither stands in for the other (Sprint 45, Tracks G and H,
            // reconciled at the integration merge).
            //
            //  - The STATION ATTEMPT (Track G, #590): one per Connect or
            //    RetryConnect, the application's unit of "establish this
            //    operator's station on this radio". ConnectionAttempt and the
            //    immutable ObservationBinding scope OBSERVATIONS — the roster,
            //    our own slices, profile evidence — and the operations that
            //    wait on them. Begun here; ended by Disconnect, by a teardown,
            //    or by the fall of this rig's own connection.
            //  - The TRANSPORT CONNECTION (Track H, #637): one per socket the
            //    vendor opens, and there may be several inside one station
            //    attempt (a failed LAN leg, then the WAN leg). CommandConnection
            //    and its ConnectionLifetime token scope the connection's RISE
            //    and FALL and the client reports it carries. The vendor mints
            //    them when a transport attempt starts; they reach this rig
            //    through the qualified events wired below, never through
            //    PropertyChanged.
            //
            // So "which connection does this callback belong to?" has two
            // answers on purpose. A property, roster, slice or panadapter
            // callback belongs to a STATION ATTEMPT and is judged by the
            // binding its closure was wired with (IsCurrentBinding). A
            // connected-state or client report belongs to a TRANSPORT
            // CONNECTION and is judged against CurrentConnectionBinding.
            // Connected itself is answered ONLY by the transport seam: see
            // onRadioConnectedChanged, which is also where a fall cancels the
            // station attempt.

            // Begin the station-connect attempt BEFORE the handlers are wired
            // and before Connect(): observation is subscribed before any
            // command is sent, and every callback from here is stamped with
            // this attempt's generation (design step 1).
            BeginStationAttempt(theRadio, "Connect");

            // add the handlers. The station-observation handlers — the radio's
            // PropertyChanged among them — are wired through the IMMUTABLE
            // binding minted for this attempt just above (WireStationHandlers):
            // each closure carries it, so a callback raised by this object is
            // stamped with the attempt the closure was wired for — never with
            // whatever attempt is current when the callback runs — and a
            // closure whose binding is no longer current drops its callback
            // whole. A retry replaces the closures rather than re-pointing the
            // binding, and the previous wiring is unwired first, so one radio
            // object never carries two of them. FlexLib never unwires these by
            // itself and discovery keeps updating every radio object it ever
            // built.
            WireStationHandlers(theRadio);
            // The qualified transport seam: the producer's connection-state
            // and client reports, each carrying the CommandConnection it came
            // from. Idempotent per radio object — this carries the
            // connection-fall archive, and a second copy would dispatch it
            // twice.
            wireProducerHandlers(theRadio);
            theRadio.MessageReceived += new Radio.MessageReceivedEventHandler(messageReceivedHandler);
            theRadio.WaterfallRemoved += new Radio.WaterfallRemovedEventHandler(waterfallRemoved);
            theRadio.TNFAdded += new Radio.TNFAddedEventHandler(tnfAdded);
            theRadio.TNFRemoved += new Radio.TNFRemovedEventHandler(tnfRemoved);
            theRadio.IsTNFSubscribed = true; // v2.0.19
            // radio set tnf_enabled=1 used to be written here, before Connect
            // — where FlexLib's SendCommand returns 0 on a disconnected
            // transport and no fact about the radio existed yet. It is the
            // owner's station-global write and lives in RunOwnerInitialization
            // under the full gate (Track G3). The subscription above is
            // read-only and stays.
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
            // convenience events above. Fresh radio, fresh subscriptions: the
            // inventory was reset before any handler was hooked, above. This
            // first pass usually finds the list still filling — meter
            // registration runs on after connect — which is exactly why the
            // reconcile is re-driven from every meter reading rather than
            // trusted once here.
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

            // ── Receive traffic sampler (#350) ──────────────────────────────
            // The whole of this lives in FlexBase.ReceiveTraffic.cs; this is the
            // only line it adds to this file. It starts a one-a-second reading of
            // how much the radio is actually sending us, so the receive report
            // can state a measurement rather than only the settings we made. It
            // stops itself when the radio goes away.
            StartRxTrafficWatch();

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
                // FlexLib's Connect sends its own `profile global info`; record
                // it first, so its answer is never taken as the radio's own (#639).
                NoteFlexLibConnectTimeProfileAsk("Connect");
                // The stored GUI client id for this radio and instance, or null
                // for "radio, mint one" (#782). FlexLib sends `client gui <id>`
                // or a bare `client gui` accordingly.
                rv = theRadio.Connect(prepareClientIdentityForConnect("Connect"));
                ConnectionProfiler.Current?.RecordEvent("flexlib_connect_end", new Dictionary<string, object>
                {
                    { "success", rv }
                });
            }

            if (rv)
            {
                Tracing.TraceLine("Connect worked:" + theRadio.Serial, TraceLevel.Info);
                AttachSliceRecorderStatus(theRadio);
                // The post-connect fence: its reply marks the end of whatever
                // the radio restored for a returning client (#782).
                sendConnectFence("Connect");

                // 4.1-line cache writer: seed radioConnectionCacheV1.xml so the
                // 4.2-line discovery cascade (Rung 1a CachedLanIp) can short-circuit
                // UDP discovery on first launch after upgrade. See
                // memory/project_autoconnect_no_ip_dead_end.md for why this lives here.
                RecordConnectedRadioForCache();

                if (RemoteRig)
                {
                    //PCAudio = true;

                    // REMOVED 2026-08-29, Noel's ruling: the forwarded port is
                    // NEVER written on connect. It is written only when the
                    // operator presses Apply on the Network tab, and never at any
                    // other time.
                    //
                    // WHAT THIS COST. Sprint 27 Phase A.2 auto-applied the
                    // per-account port on every remote connect. On 2026-08-29 at
                    // 14:11 Noel connected to DON'S 6300 and this line wrote HIS
                    // OWN port into it:
                    //
                    //   ApplyAccountPortPreferenceIfAny: account=noel@example.com
                    //     applying port=25678 (radio reports enabled=True tcp=4992 udp=4992)
                    //   SetSmartLinkPortForwarding: enabled=True tcp=25678 udp=25678
                    //
                    // `wan set public_tls_port` PERSISTS IN THE RADIO. Don's radio
                    // then advertised 25678 to SmartLink, nothing forwards that
                    // port at his site, and it became unreachable to every client
                    // — Don included — until someone with physical access changed
                    // it back. Five hours of debugging, and it damaged hardware we
                    // do not own at a site we cannot reach.
                    //
                    // WHY AN OWNERSHIP CHECK IS NOT THE FIX. The obvious repair is
                    // to compare the radio's owning account against _currentAccount
                    // and skip when they differ. Noel rejected it, correctly: the
                    // current account is whatever happens to be default, and if
                    // Don's account were ever made default the check passes and we
                    // write to his radio anyway — with his consent nowhere in the
                    // picture. A guard that depends on a mutable default is not a
                    // guarantee.
                    //
                    // THE RULE: writing persistent configuration into radio
                    // firmware is an operator action with a person behind it. It is
                    // never a side effect of connecting. If a future build wants to
                    // offer "apply my port on connect", the owner of THAT radio has
                    // to have said yes to it, on that radio.
                    //
                    // ApplyAccountPortPreferenceIfAny() now has NO CALLERS. The
                    // Network tab writes through FlexBase.SetSmartLinkPortForwarding
                    // directly (SettingsDialog.xaml.cs:841), which is the operator
                    // action described above. The method is left in place, unused,
                    // only so this comment has something to point at; deleting it is
                    // safe and should happen in a quieter moment. Do NOT re-add the
                    // call from here.

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
                    // local audio on, so PC audio is not in use: the shack
                    // speaker is not muted. radio set
                    // mute_local_audio_when_remote (audit 1.5).
                    //
                    // RULED by Noel 2026-09-22 21:33, in his words: "mute the
                    // shack speaker if you're going PC audio, unmute it if
                    // you're not using it. If for some really weird reason you
                    // want to have the speaker unmuted while you're PC audio
                    // connected, then cool. Why make it complicated."
                    //
                    // So this is NOT a shared write and takes no ownership or
                    // roster gate. Track G3 put it behind the owner gate,
                    // reading it as shared state; that is reverted. The
                    // speaker is shared between the ROOM and everyone remote,
                    // not between operators, and a remote client cannot know
                    // who is in the room. The hold still applies, because it
                    // applied here before Track G3 too.
                    if (!GuardSkips("IsMuteLocalAudioWhenRemoteOn=false on local connect"))
                    {
                        theRadio.IsMuteLocalAudioWhenRemoteOn = false;
                    }
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

                    // A LEG THAT CONNECTS IS NOT A LEG THAT WORKED (tasks #284,
                    // #286). Connect() returning true means the SESSION came up;
                    // the radio has not opened yet and will not for up to another
                    // minute. Writing "connected" here is the loop that fed
                    // itself: on 2026-08-26 four SmartLink attempts that all died
                    // in the station-name wait were written into the ring as four
                    // successes — 341, 1334, 350 and 913 ms — and three in a row
                    // is a trend, so the path policy then steered the NEXT connect
                    // down the path that had just failed four times. Every failure
                    // made the next one more likely and the store showed an
                    // unbroken run of success.
                    //
                    // The manual path was fixed in Sprint 36; this one was missed
                    // because it walks its own legs instead of going through the
                    // selector. So: ARM here, and let whoever learns the open's
                    // fate commit it. openTheRadio calls CommitPendingOutcome with
                    // the result of Start(), which is the moment the outcome is
                    // actually known.
                    //
                    // A connect FAILURE is knowable now, so it still records
                    // immediately. Auto-connect is never a forced path — a force
                    // comes from the picker's context menu (#287) — so these
                    // records are ordinary evidence.
                    if (connected)
                    {
                        ConnectionHistory.ArmPendingOutcome(config.RadioSerial, path.ToString(),
                            legSw.ElapsedMilliseconds);
                    }
                    else
                    {
                        ConnectionHistory.Record(config.RadioSerial, path.ToString(),
                            LastConnectFailureReport?.Class.ToString() ?? "failed",
                            legSw.ElapsedMilliseconds);
                    }

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
                    //
                    // DELIBERATELY the held handle, NOT findCurrentWanRadio —
                    // do not "fix" this to ask whether the handle's list is
                    // current. Noel's ruling on #619 (2026-09-30): a last-seen
                    // radio stays selectable BECAUSE connecting is itself the
                    // check, and a recovery route when refresh is broken. This
                    // is the wait an operator's own choice of a row reaches.
                    // L12 made it take only a handle from a current list, and
                    // Sol's verification of L12 found the consequence: when the
                    // refresh is the thing that is broken, the wait times out
                    // with "never appeared" and no dial is ever attempted, so
                    // the selectable row led nowhere. Restored in L13. Whether
                    // a handle is CURRENT is still asked everywhere it is news
                    // about now — the picker's row, occupancy, IsDualHomed,
                    // the auto-connect timer — just not here, where the
                    // operator has asked us to try.
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

            // A retry that opens another transport gets another producer identity.
            rememberCommandRoute(null, null, null);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"RetryConnect: BEGIN serial={theRadio.Serial}", TraceLevel.Info);

            try
            {
                // Reset the client tracking flags for the new Start() attempt
                _clientRemovedDuringStart = false;
                _clientAddedDuringStart = false;

                // A retry is a new connection: new attempt generation, so a
                // late callback from the failed one cannot complete a wait on
                // this one even though the serial is identical.
                BeginStationAttempt(theRadio, "RetryConnect");

                bool rv = true;

                if (RemoteRig)
                {
                    rv = sendRemoteConnect(theRadio);
                    Tracing.TraceLine($"RetryConnect: sendRemoteConnect returned {rv} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }

                if (rv)
                {
                    // FlexLib's Connect sends its own `profile global info` (#639).
                    NoteFlexLibConnectTimeProfileAsk("RetryConnect");
                    // The same stored id as the first attempt presented, unless
                    // the radio refused it as a duplicate in between (#782).
                    rv = theRadio.Connect(prepareClientIdentityForConnect("RetryConnect"));
                    Tracing.TraceLine($"RetryConnect: Radio.Connect returned {rv} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

                    if (rv)
                    {
                        // Seed the 4.2-line discovery-chain cache on retry success too.
                        RecordConnectedRadioForCache();
                        sendConnectFence("RetryConnect");
                        AttachSliceRecorderStatus(theRadio);
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
                            Lexicon.Get("connect.smartlink.no_radios"),
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

        /// <summary>One mismatch trace per Start() — see the wait loop (#402).</summary>
        private bool _stationMismatchTraced;

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
                teardownDisconnect("cancel during antenna wait");
                return false;
            }

            ConnectionProfiler.Current?.RecordEvent("start_antenna_available");

            // Wait for the station name to be set on the GUIClient.
            // SmartLink removes and re-adds the GUIClient during connection setup.
            // We track the removal event for faster detection — if our client is removed
            // and not re-added within the grace period, disconnect and let caller retry.
            // A fresh reconnection is faster than waiting for a slow re-add cycle.
            bool stationNameSet = false;
            _stationMismatchTraced = false;
            {
                int maxWaitMs = 45000;       // 45s overall timeout (SmartLink re-add can take 30s+ over WAN)
                int removalGraceMs = 15000;  // 15s grace after client removal (Don's 6300 over WAN needs 10s+)
                int earlyAbortMs = 1000;     // 1s: if removed without ever being added, abort fast for retry
                int interval = 25;
                // A DEADLINE, not a count of sleeps (task #293).
                //
                // This used to be `iterations = maxWaitMs / interval` and a
                // `while (iterations-- > 0)` loop that slept 25 ms per turn. The
                // loop then ran the right NUMBER of times, but every turn cost
                // 25 ms of sleep PLUS whatever the work in it cost — so the
                // declared 45,000 ms budget elapsed in about 56 seconds of wall
                // clock, measured at 55.7 s in the 2026-08-26 field trace. The
                // error is not a constant offset either: it scales with how busy
                // the machine and the radio are, which means it grows exactly
                // when a connect is already struggling and the number matters
                // most.
                //
                // It had already misled a caller. ConnectNarrator needed a
                // ceiling for the #212 heartbeat, read maxWaitMs as a duration,
                // and would have gone silent with ten seconds of this wait still
                // to run — recreating the silence it exists to remove, at the
                // worst possible moment. It was covered with a 1.5 margin, which
                // is a fudge factor over an arithmetic error that the next caller
                // has no way to know about.
                //
                // So the budget is now the budget: read the clock, compare
                // against a deadline. maxWaitMs means 45 seconds to anyone
                // reading it here, in the profiler payload, or in a trace.
                long stationWaitDeadline = Environment.TickCount64 + maxWaitMs;
                _clientRemovedDuringStart = false;
                _clientAddedDuringStart = false;
                _startBeginTickCount = Environment.TickCount64;

                ConnectionProfiler.Current?.RecordEvent("start_station_name_wait_begin", new Dictionary<string, object>
                {
                    { "maxWaitMs", maxWaitMs },
                    { "earlyAbortMs", earlyAbortMs },
                    { "removalGraceMs", removalGraceMs }
                });

                while (Environment.TickCount64 < stationWaitDeadline)
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
                    // The radio's own report finishes this wait when it
                    // carries the name. A matching name that only a SmartLink
                    // list or a discovery broadcast wrote into our record —
                    // the rescue — may still finish it, narrowed (#634, ruled
                    // 2026-09-26): only for the handle this connection was
                    // given (TheGuiClient reads by that handle), only from
                    // the account this connection was brokered through (the
                    // merge is gated on it), and only while the radio has
                    // reported neither a different name nor our departure.
                    // It finishes the name wait and nothing else.
                    var decision = StationNameWaitDecision(client?.Station, Callouts.StationName);
                    if (decision.Finished)
                    {
                        stationNameSet = true;
                        StationNameSource = decision.Source;
                        StationNameRescueSentence = decision.RescueSentence;
                        break;
                    }
                    // Diagnostic for the case the strict equality hides: the
                    // radio answered with a DIFFERENT name than we asked for
                    // (its own dedupe, or a stale merge). Without this line a
                    // radio-side rename and total radio silence produce the
                    // same 45 seconds of nothing in the trace (#402).
                    if (client != null && !string.IsNullOrEmpty(client.Station)
                        && !_stationMismatchTraced)
                    {
                        _stationMismatchTraced = true;
                        Tracing.TraceLine(
                            $"start: radio reports our station as '{client.Station}' but we are waiting for '{Callouts.StationName}' — a rename happened somewhere and the wait cannot succeed",
                            TraceLevel.Error);
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
                releaseGuiClientRegistration("cancel during station-name wait");
                teardownDisconnect("cancel during station-name wait");
                return false;
            }
            if (stationNameSet)
            {
                Tracing.TraceLine("start:station name set " + Callouts.StationName
                    + " (source: " + StationNameSource + ")"
                    + (StationNameRescueSentence == null ? "" : " — " + StationNameRescueSentence), TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("station_name_set", new Dictionary<string, object>
                {
                    { "stationName", Callouts.StationName },
                    { "source", StationNameSource.ToString() },
                    { "detail", StationNameRescueSentence ?? "" }
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
                // Station name timeout.
                // Disconnect cleanly and return false so caller can retry with a fresh connection.
                // Don't show error dialog — a fresh connection usually succeeds quickly.
                Tracing.TraceLine("start:station name timeout, disconnecting for retry", TraceLevel.Warning);
                // The discriminating instrument (#402): the radio's own LAN
                // discovery broadcasts carry the station roll call, on a UDP
                // path that does not depend on our TCP receive channel. On
                // 2026-08-30 they showed our client as 'k5ner' 122 ms into a
                // wait that then starved for 45 s — proof the radio APPLIED
                // the name and OUR receive path never delivered the echo
                // (the read loop was wedged behind a synchronous marshal to
                // the blocked UI thread). Without this line, a silent radio
                // and a self-inflicted stall produce the same trace.
                var (discoverySawHandle, discoveryStation) = DiscoveryStationEvidence();
                Tracing.TraceLine(
                    "start:station name timeout — " +
                    StationEvidenceVerdict(discoverySawHandle, discoveryStation, Callouts.StationName),
                    TraceLevel.Error);
                LastStartFailureReason = _clientRemovedDuringStart
                    ? "Client removed during connection"
                    : "Station name timeout";
                ConnectionProfiler.Current?.RecordAndSave("station_name_timeout", new Dictionary<string, object>
                {
                    { "clientRemovedDuringStart", _clientRemovedDuringStart },
                    { "ticksSinceRemoval", _clientRemovedDuringStart ? (Environment.TickCount64 - _clientRemovedTickCount) : 0 },
                    { "discoverySawOurHandle", discoverySawHandle },
                    { "discoveryStation", discoveryStation }
                });
                if (!SuppressSpeech) ScreenReaderOutput.Speak(Lexicon.Get("connect.start.slow_retrying"), VerbosityLevel.Critical);
                if (ScreenReaderOutput.CwNotificationsEnabled) _ = ScreenReaderOutput.PlayCwAS?.Invoke();
                // Tell the radio to drop this registration before the retry
                // reconnects — a lingering registration is exactly the state
                // the failing #402 reconnects kept landing on.
                releaseGuiClientRegistration("station-name timeout, retrying");
                teardownDisconnect("station-name timeout, retrying");
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
            AttachSliceRecorderStatus(null);

            // Held for the end of this method, which lets go of this rig's
            // handlers on the object once its fall has been seen. theRadio
            // itself is cleared part way through.
            Radio releasing = theRadio;

            // The teardown is its own OPERATION, begun first: every earlier
            // operation on this connection ends now, so any automatic work it
            // queued (a load, a selection, an allocation, a live-audio
            // setter) refuses when the loop reaches it, while the teardown's
            // own work — the create and the put-back — runs under an
            // operation that is live until CancelStationAttempt below. Until
            // Track G2 older automatic work stayed permitted throughout
            // teardown because cancellation came only after it (review
            // section 8).
            BeginTeardownOperation("Disconnect");
            DisconnectVoxPauses();

            // The deferred create of a global that was definitely missing at
            // connect (#578, design step 11), on the CLEAN disconnect and
            // before the put-back so the two cannot fight over the global
            // selection. Every condition is revalidated inside; a refusal
            // traces its reason. Idempotent: the pending intent is cleared
            // once decided, so the Dispose path reaching here later is a no-op.
            try { saveNewGlobalProfile(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Disconnect: the deferred global create threw: " + ex.Message, TraceLevel.Error);
            }

            // Put this radio's own profiles back, FIRST, while the link is
            // certainly still up and before the cancel flag below starts
            // shortening waits (#450). Idempotent: the record is cleared once
            // used, so the Dispose path reaching here later is a no-op.
            //
            // BEST-EFFORT AND NOTHING MAY ASSUME IT RAN. This is the clean
            // disconnect; an unexpected drop never reaches this line at all,
            // which is exactly why the restore point lives on the RADIO.
            try { PutProfilesBackOnDisconnect(); }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    "Disconnect: putting profiles back threw: " + ex.Message
                    + ". The restore points stay on the radio.", TraceLevel.Error);
            }

            // The connection attempt ends here: every wait in the station
            // coordinator sees the cancellation, pending queue work is
            // invalidated by generation, and the next connection starts clean.
            CancelStationAttempt("Disconnect");

            // Signal the cancel flag too — if Start() is in-flight on another thread
            // (UI thread blocked in the station-name wait), it sees this and exits fast.
            _cancelRequested = true;
            Disconnecting = true;

            // Final where-you-left-it flush (#226): the debounce may still be
            // pending, and the whole point of the record is the next connect.
            // Never allowed to slow or fail a disconnect.
            try { FlushOperatorPlace(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Disconnect:place flush:" + ex.Message, TraceLevel.Warning);
            }

            // Say goodbye properly (#402): FlexLib's Disconnect only closes
            // the socket, so the radio kept our GUI-client registration after
            // every disconnect — and the very next connect kept meeting it,
            // as a ghost in the server's list snapshot and as the stale
            // radio-side state the failing reconnects landed on. Sent early,
            // while the socket is certainly still alive; TCP ordering puts it
            // ahead of our FIN. No-op on an unexpected drop (socket already
            // dead) — there is nobody left to say goodbye to.
            releaseGuiClientRegistration("clean disconnect");

            // 2026-04-28: announce disconnect via speech + CW (SK prosign) so the
            // user knows the radio is going away. Fires before the actual
            // disconnect work (which can take 3+ seconds) so feedback is
            // immediate. This is the user-initiated / clean disconnect path;
            // unexpected drops (onRadioConnectedChanged when this rig's own
            // connection falls from external causes) get a different signal —
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

            // #161's trigger rule compares each CW send against what was last
            // sent and drops repeats. That comparison ends with the session:
            // reconnecting deserves its opening census even when the numbers
            // happen to match last session's, so forget the last send here.
            // Outside the CwNotificationsEnabled guard on purpose — the
            // setting can be toggled between sessions, and a stale "last
            // sent" must not survive into a session where CW is newly on.
            ScreenReaderOutput.ResetCwLastSent();

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

            // Let go of this rig's handlers on the object — the producer seam
            // and the station wiring both — so a later connection handed this
            // same Radio object carries one set rather than two. Only once the
            // object reports itself disconnected: its fall is what sets
            // IsConnected false (onRadioConnectedChanged), so a radio that has
            // not yet let go keeps its handlers until it does. That fall is
            // ours and archives nothing, because Disconnecting is already set;
            // and the station attempt it would cancel was cancelled above.
            if (!releasing.Connected)
            {
                unwireProducerHandlers(releasing);
                UnwireStationHandlers(releasing);
            }
            else
                Tracing.TraceLine("Disconnect: the radio still reports connected; its handlers stay until it lets go", TraceLevel.Info);

            // Now collect the farewell started at the top of this method. See the
            // long comment there: this path plays SK and suppresses the only
            // other path that knew how to wait, so it has to do the waiting
            // itself. Bounded, and never allowed to throw — a farewell must not
            // be able to fail a disconnect.
            if (farewell != null)
            {
                try
                {
                    // Read once — the trace must report the bound that was
                    // actually applied, not a second evaluation of it.
                    int waitMs = SkFarewellWaitMs();
                    if (!farewell.Wait(waitMs))
                    {
                        Tracing.TraceLine(
                            "Disconnect:SK farewell did not finish within "
                            + waitMs + "ms — continuing teardown",
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
        /// The wait used when nothing has reported a farewell duration — the
        /// flat figure both SK paths used before #143.
        /// </summary>
        /// <remarks>
        /// Also the FLOOR of the computed wait, so this change can only ever
        /// lengthen the window, never shorten it. A speed that fits today must
        /// keep fitting.
        /// </remarks>
        public const int SkFarewellFallbackMs = 5000;

        /// <summary>
        /// The hard ceiling. A farewell must never be able to hang a
        /// disconnect or an exit, whatever the keying speed or the state of
        /// the sound card.
        /// </summary>
        /// <remarks>
        /// Comfortably clear of the worst honest case — the short string at
        /// the slowest allowed speed, 10 WPM, is around 7.6 seconds — so this
        /// bounds a wedged device rather than a slow operator. The real
        /// governor is <c>EarconCwOutput.WaitForDrain</c>, which observes the
        /// device and bounds itself; this is the backstop behind it.
        /// </remarks>
        public const int SkFarewellCeilingMs = 15000;

        /// <summary>
        /// How long any path that plays the SK farewell may wait for it before
        /// giving up and continuing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Derived, not chosen.</b> Until 2026-08-26 this was a flat 5000
        /// ms, and the comment behind it named exactly the right case — "the
        /// richer '73 de JJF SK' farewell at speed >= 25 WPM" — while the
        /// arithmetic undershot it by about a second. Two bands were being
        /// truncated: roughly 10-15 WPM on the short string, and 25-31 on the
        /// long one. Between and above, it fitted. Testing one speed in the
        /// middle showed a clean pass, which is how it survived.
        /// </para>
        /// <para>
        /// The string's length is not a function of speed alone: at 25 WPM the
        /// farewell also GROWS, from roughly 63 PARIS units to roughly 129. So
        /// the number comes from the CW side, which knows both the text and
        /// the speed, rather than from a second calculation here.
        /// </para>
        /// <para>
        /// <b>This is a different defect from the drain race, with the same
        /// symptom.</b> That one signalled completion on a computed duration,
        /// so the wait was satisfied EARLY and teardown cut the tail; raising
        /// a timeout does nothing for it, and it was fixed in Sprint 32 Track
        /// H by observing the device instead of predicting it. This one
        /// genuinely EXPIRES at the speed extremes. Do not let a fix for one
        /// look like a fix for the other.
        /// </para>
        /// <para>
        /// Both SK paths call this, so they cannot drift apart — which was the
        /// point of the shared constant it replaces.
        /// </para>
        /// </remarks>
        public static int SkFarewellWaitMs()
        {
            int budget;
            try
            {
                budget = ScreenReaderOutput.CwFarewellBudgetMs?.Invoke() ?? 0;
            }
            catch
            {
                // A farewell must not be able to fail a disconnect, and that
                // includes failing to work out how long to wait for one.
                budget = 0;
            }

            if (budget <= 0) return SkFarewellFallbackMs;
            return Math.Clamp(budget, SkFarewellFallbackMs, SkFarewellCeilingMs);
        }

        private bool _IsConnected = false; // set in onRadioConnectedChanged, from the qualified transport event
        /// <summary>
        /// True if connected.
        /// </summary>
        public bool IsConnected
        {
            get { return _IsConnected; }
        }

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
        /// <remarks>
        /// <paramref name="ms"/> is a DEADLINE, not a count of sleeps — see
        /// <c>JJTrace.Tracing.await</c>, which carries the reasoning (task
        /// #293). This is the copy behind the antenna-list wait in
        /// <c>Start()</c>, whose own comment already noted it shares a timing
        /// profile with the station-name wait; it shared the arithmetic error
        /// too.
        /// </remarks>
        internal static bool await(awaitExp exp, int ms, int interval)
        {
            long deadline = Environment.TickCount64 + ms;
            bool rv;
            while (true)
            {
                rv = exp();
                if (rv) break;
                if (Environment.TickCount64 >= deadline) break;
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

            // #835: an Audio Check restore still waiting on this radio can no
            // longer happen; the owner says so in the trace and stops polling.
            DisposeCheckRestorations();

            // FIRST, and outside the disposed guard. A rig being torn down must
            // stop consuming presence pushes even if the teardown below throws
            // half way, and even if Dispose is somehow reached twice — a dead
            // rig in the push path would ghost-sweep a stale radio list and
            // raise RadioRemoved into whatever picker is open (#386).
            //
            // This is a complement to the swap-on-engage rule, not the fix
            // itself: it cannot be relied on (a caller can always skip Dispose,
            // and see the header at the intake for why that used to be
            // unrecoverable), but it narrows the window where a doomed rig is
            // still the intake from "until someone else engages" to "now".
            resignAsThePresenceIntake();
            DisconnectVoxPauses();

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

                // Stop watching discovery GuiClients on every radio this
                // instance subscribed (#394) — a disposed rig re-raising
                // RadioFound into whatever picker is open would be the same
                // ghost-push problem the intake resignation above closes.
                foreach (var watched in myRadioList.ToList())
                    UnwatchDiscoveryGuiClients(watched);

                if (theRadio != null)
                {
                    // #423. Teardown must not be able to abandon itself half
                    // way, and on the finalizer thread it must not be able to
                    // take the process with it. The measured case: theRadio
                    // non-null with Callouts.Profiles null sent
                    // saveNewGlobalProfile into GetDefaultProfiles, which
                    // walked a null list — and when that fired from the
                    // finalizer it took down the whole test host. 2098 tests
                    // never saw it because no prior test had attached a radio
                    // to a FlexBase.
                    //
                    // The null-list confusion itself is fixed at its source in
                    // GetDefaultProfiles. This is the second lock: whatever
                    // else these two steps ever throw, the rest of Dispose
                    // still runs, `disposed` is still set, and the failure is
                    // named rather than being an unattributable death on a
                    // thread nobody was watching.
                    try
                    {
                        BeginTeardownOperation("Dispose");
                        saveNewGlobalProfile(); // if any

                        // Put this radio's own profiles back BEFORE the connection
                        // goes away, and after saveNewGlobalProfile so the two
                        // cannot fight over the global selection (#450).
                        //
                        // BEST-EFFORT, AND NOTHING MAY ASSUME IT RAN. A remote
                        // client cannot guarantee cleanup, because the process
                        // that must clean up is the one that died - a power cut,
                        // a killed process, a dropped link and this line is never
                        // reached. That is the whole reason the restore point sits
                        // on the RADIO: the next JJ Flexible client to connect,
                        // ours or anyone's, sees it in the profile list and can
                        // offer to put things right.
                        try { PutProfilesBackOnDisconnect(); }
                        catch (Exception ex)
                        {
                            Tracing.TraceLine(
                                "FlexBase.Dispose: putting profiles back threw: " + ex.Message
                                + ". The restore points stay on the radio.", TraceLevel.Error);
                        }

                        Disconnect();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Tracing.TraceLine("FlexBase.Dispose: radio teardown failed and was "
                                + "contained so the rest of Dispose could finish (disposing="
                                + disposing.ToString() + "). " + ex, TraceLevel.Error);
                        }
                        catch { /* a trace failing here must not become the fault */ }
                    }
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

        /// <summary>
        /// #423. Nothing may leave a finalizer.
        /// </summary>
        /// <remarks>
        /// <para>An exception escaping here is unhandled ON THE GC THREAD: the
        /// process dies, there is no window to say so, no speech, and the
        /// stack belongs to the collector rather than to whatever caused it.
        /// <b>Unrecoverable and unattributable</b> is the worst pair of
        /// properties a failure can have, and it is the pair a blind operator
        /// is least able to work around.</para>
        ///
        /// <para>So this catches everything, and the trace is wrapped too —
        /// finalization can run at process exit with the trace file already
        /// closed, and a diagnostic that throws while reporting a fault would
        /// reproduce the exact failure it exists to prevent.</para>
        ///
        /// <para><b>Reported, not fixed here (out of this track's region):</b>
        /// <c>Dispose(false)</c> reaches a great deal of managed work —
        /// <c>saveNewGlobalProfile</c> talks to the radio, <c>Disconnect</c>
        /// and <c>API.CloseSession</c> do network I/O, <c>RigFields.Close</c>
        /// touches UI. A finalizer is not the place for any of that, and it may
        /// be running against objects that were already collected. This guard
        /// stops that being fatal; it does not make it correct.</para>
        /// </remarks>
        ~FlexBase()
        {
            try
            {
                Dispose(false);
            }
            catch (Exception ex)
            {
                try
                {
                    Tracing.TraceLine("FlexBase finalizer: exception contained on the GC thread. "
                        + "Unhandled here it would have ended the process with no window, no "
                        + "speech and no attribution. " + ex, TraceLevel.Error);
                }
                catch { /* the report must never become the fault */ }
            }
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
        /// <summary>
        /// Whether a <c>Connected</c> change reported by a Radio object may
        /// change this rig's own connection state. Pure, so the table is pinned
        /// by tests.
        ///
        /// <para>Yes for the rig's own Radio. No for any other object — a
        /// handler left on an abandoned object must not speak for the live
        /// connection. One deliberate exception: when the rig has no Radio at
        /// all, a FALL may still apply. That is the object
        /// <see cref="Disconnect"/> is letting go of when the radio took longer
        /// than its wait to let go, and "disconnected" is the only true state
        /// for a rig with no radio; a RISE from a stranger never is.</para>
        /// </summary>
        internal static bool ConnectionStateAppliesToRig(bool fromOurRadio, bool rigHasRadio, bool nowConnected)
            => fromOurRadio || (!rigHasRadio && !nowConnected);

        private int _strandedConnectionChangesIgnored;

        /// <summary>How many Connected changes from objects that are not this
        /// rig's connection were ignored. Diagnostic, and the tests' positive
        /// control that such a change really reached the handler.</summary>
        internal int StrandedConnectionChangesIgnored => Volatile.Read(ref _strandedConnectionChangesIgnored);

        /// <summary>
        /// Our Radio reported a qualified transport transition. The producer identity
        /// and state travel with the callback instead of being read from a reused Radio.
        ///
        /// <para><b>On a fall, nothing here waits on the trace gate before the
        /// drop is claimed</b> (Sprint 45 Track H6, Sol's review of H3, finding
        /// 1). FlexLib raises this on its own transport thread, mid-teardown.
        /// An ordinary <c>Tracing.TraceLine</c> passes the trace coordinator's
        /// gate, and that gate is held across a lifecycle transition's flush,
        /// close, move and successor open — so a disk that stalled under a
        /// capture stop would have stalled FlexLib's teardown, and the drop's
        /// claim with it. The lines a fall writes are therefore DEFERRED:
        /// formatted now, with this moment's timestamp and thread, and written
        /// by the archive worker before it archives, so they still land in the
        /// session they describe. A rise is not a loss path and traces
        /// normally.</para>
        ///
        /// <para>What runs AFTER the archive is queued — the
        /// <see cref="ConnectionStateChanged"/> subscribers — is ordinary
        /// application code and traces as it always has.</para>
        /// </summary>
        private void onRadioConnectedChanged(Radio r, CommandConnectionChanged report, ConnectionLifetime.Token token)
        {
            // The immutable event describes the transition, even when its delivery was delayed.
            bool nowConnected = report.State == CommandConnectionState.Connected;

            // AND THE SESSION IS READ ONCE TOO, here, at the top of the fall
            // (Sprint 45 Track H8; Sol's review of H7, the item for a harder
            // reader). Every deferred line this fall writes — here, in
            // archiveIfOurConnectionDropped, and inside CaptureArchive — and the
            // archive request itself are bound to THIS handle. Read per line, as
            // they were, a Stop completing between two lines bound the first
            // to the old session and the rest, and the archive, to its
            // successor: one fall with two identities. Null when nothing is
            // recording, which binds nothing and archives nothing; a session
            // that opens during the fall is not this fall's.
            JJTrace.TraceSessionHandle fall = nowConnected ? null : JJTrace.TraceCoordinator.CurrentHandle;
            Action<string, TraceLevel> trace = nowConnected
                ? (Action<string, TraceLevel>)Tracing.TraceLine
                : (s, l) => Tracing.TraceLineDeferred(s, l, fall);

            trace("propertyChanged:Radio:Connected", TraceLevel.Verbose);
            if (!(r.ClientHandle != 0) & myClient(r.ClientHandle))
            {
                // TraceLevel.Off passes every switch level: this line was
                // unconditional before it moved here, and still is.
                trace("propertyChanged:Radio:NotMine:Connected", TraceLevel.Off);
            }

            // A STRANDED HANDLER MUST NOT FLIP THE LIVE RIG'S STATE (Sprint 45
            // Track H6, Sol's review finding 7). This used to set IsConnected
            // and raise ConnectionStateChanged for a change on ANY object
            // carrying this handler — a failed leg of the connect walk that
            // Connect moved away from, say — so an abandoned object falling
            // told the whole application the live radio had gone. The archive
            // already checked the object; the rig's state now does too.
            //
            // AND THE BARE "Connected:False" LINE COMES AFTER THIS CHECK (Sol's
            // review of H6, finding 7). Written before it, a stranded object's
            // fall left an unqualified connected-state line in the live trace
            // — reading exactly like the live radio going — while the rig
            // correctly ignored it. The ignored branch writes its own line,
            // naming the object; the unqualified line is now only ever about
            // this rig's connection.
            Radio live = theRadio;
            if (!ConnectionStateAppliesToRig(ReferenceEquals(r, live), live != null, nowConnected))
            {
                Interlocked.Increment(ref _strandedConnectionChangesIgnored);
                trace($"Connected:{nowConnected} on {r.Serial}, which is not this rig's connection — ignored;"
                      + " the live connection's state is unchanged", TraceLevel.Info);
                return;
            }
            trace("Connected:" + nowConnected.ToString(), TraceLevel.Error);

            bool wasConnected = _IsConnected;
            _IsConnected = nowConnected;
            if (nowConnected && !wasConnected)
            {
                // Capture on the qualified transition. Disk I/O belongs on
                // the worker, away from FlexLib's sequential receive loop.
                string serial = r.Serial;
                string nickname = r.Nickname;
                string model = r.Model;
                bool remote = r.IsWan;
                string account = remote ? WanAccountForSerial(serial) : "";
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    KnownRadioRoster.RecordSighting(serial, nickname, model, remote, account));
            }
            // The archive is taken BEFORE ConnectionStateChanged, so a subscriber
            // that throws cannot cost the evidence.
            if (!nowConnected) archiveIfOurConnectionDropped(r, token, fall);
            // A drop invalidates the STATION ATTEMPT at once (Track G, #590):
            // a coordinator blocked in a wait ends Cancelled rather than
            // running out its deadline against a dead link. It lives here and
            // not in the property switch, because this is the one place a
            // fall of THIS rig's connection is established — a stranded
            // object's fall returned above, and a stale transport's fall on
            // this object was refused in onCommandConnectionChanged, so
            // neither can cancel a live attempt. After the archive, because
            // CancelStationAttempt traces through the ordinary gate the fall's
            // claim must not wait on; before ConnectionStateChanged, so its
            // subscribers see the attempt already ended, as they did when the
            // cancel sat in the switch.
            if (!nowConnected) CancelStationAttempt("connection dropped");
            ConnectionStateChanged?.Invoke(nowConnected);
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

        private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e, ObservationBinding binding)
        {
            Radio r = (Radio)sender;
            // THE CONNECTION CHANGING IS HANDLED BEFORE ANY ORDINARY TRACE LINE.
            // FlexLib raises it on its transport thread in the middle of a
            // teardown, and an ordinary line waits on the trace gate, which a
            // lifecycle transition holds across file I/O. See
            // onRadioConnectedChanged.
            if (e.PropertyName == "Connected")
            {
                // Connection state is consumed only from the qualified producer event.
                return;
            }
            Tracing.TraceLine("propertyChanged:Radio:" + e.PropertyName, TraceLevel.Verbose);
            if (!(r.ClientHandle != 0) & myClient(r.ClientHandle))
            {
                Tracing.TraceLine("propertyChanged:Radio:NotMine:" + e.PropertyName);
            }

            // Station-first connect: every profile-related notification is
            // logged with its PROVENANCE (radio-reported, or our own setter's
            // synchronous echo) and the attempt generation, before the
            // per-property handling below. Collects facts and wakes the
            // coordinator; never blocks this receive thread.
            ObserveRadioProfileProperty(binding, r, e.PropertyName);

            switch (e.PropertyName)
            {
                case "FullDuplexEnabled":
                    if (!ReferenceEquals(r, theRadio)) break;
                    _fullDuplexReportedRadio = r;
                    _fullDuplexReportedConnection = r.CurrentCommandConnection?.Id ?? Guid.Empty;
                    Volatile.Write(ref _fullDuplexDecision, null); // a fresh radio report supersedes a local reply
                    break;
                case "FeatureLicense":
                    HookFeatureLicense(r);
                    break;
                case "ProfileAutoSave":
                    // The radio has told us its own profile-autosave setting.
                    // FlexLib has no "reported" flag of its own — it collapses
                    // "never said" to the default false — so this event is our
                    // only positive proof the value is real, which the guest
                    // path needs before it turns autosave off on a radio that
                    // is not ours (#499).
                    //
                    // Provenance matters here (design section 3): our OWN
                    // setter raises this same notification synchronously. Only
                    // a radio-reported observation counts, which is what the
                    // evidence log records and ReadProfileSituation reads.
                    if (ProvenanceNow() == ObservationProvenance.RadioReported) _radioReportedAutosave = true;
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
                            EscStateChanged?.Invoke();
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
                // "Connected" is not handled here: it returns early, above,
                // into onRadioConnectedChanged — which sets IsConnected, takes
                // the drop's archive, cancels the station attempt on a fall,
                // and raises ConnectionStateChanged.
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
                case "MaxPowerLevel":
                    // #608. The radio reports a transmit power CEILING, and
                    // until 2026-09-24 we discarded it: FlexLib raised this
                    // property, the switch had no case, and it fell through in
                    // silence. So the Power dialog offered nought to a hundred
                    // watts whatever the radio was prepared to deliver.
                    //
                    // Don, on a 6300: switching to AM leaves the reading at a
                    // hundred while the radio transmits about twenty-five. This
                    // case is the instrument that says which of three things is
                    // happening — the radio lowers rfpower, the radio lowers
                    // this ceiling, or the radio clamps internally and reports
                    // neither. It is also the fix for the second of those.
                    //
                    // Traced unconditionally, because a ceiling that never
                    // moves is as much of an answer as one that does.
                    Tracing.TraceLine("MaxPowerLevel:" + theRadio.MaxPowerLevel, TraceLevel.Info);
                    _MaxXmitPower = theRadio.MaxPowerLevel;
                    break;
                case "Mox":
                    {
                        Tracing.TraceLine("Mox:" + r.Mox.ToString(), TraceLevel.Info);
                        // The transmit state, and the txMeters line's evidence
                        // window on its rising edge (#625, H18).
                        noteRadioMox(r.Mox);
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
                case "SliceList":
                    // The vendor's list changed (a slice REPORTED, ready or
                    // not, or removed): on a returning connect the arrival is
                    // logged for the bench, and a held post-connect fence
                    // re-counts (#782).
                    noteArrivals();
                    tryRecordConnectFence("the vendor's slice list changing");
                    break;
                case "PanadapterList":
                    noteArrivals();
                    break;
                case "ProfileGlobalList":
                    {
                        // #579: this used to set globalProfileLoaded from
                        // ProfileGlobalList.Contains(desired) — an INVENTORY
                        // membership test standing in for station completion.
                        // The list is now an observation in the evidence log
                        // (above), and completion is the completion policy's
                        // to judge from evidence, not this handler's.
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
                        // Both edges, so a manual carrier gets a tuneResult line.
                        // Only the rising edge raises FlexAntTuneStartStop below.
                        noteTuneCarrier(r.TXTune);
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
                            if (s.Active)
                            {
                                AnnounceSliceIdentity(s);
                                // Arriving on a slice is a place change (#226).
                                NoteOperatorPlace(s);
                            }
                        }
                        break;
                    case "ESCEnabled":
                    case "ESCPhaseShift":
                    case "ESCGain":
                        EscStateChanged?.Invoke();
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
                                // Where-you-are memory (#226): "14.243 USB" is
                                // a place, and mode is half of it.
                                NoteOperatorPlace(s);

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
                            ulong echoed = LibFreqtoLong(s.Freq);
                            if (s.Active && _rxFreqEcho.Accept(echoed))
                            {
                                _RXFrequency = echoed;
                                // The filter edges and the where-you-are memory
                                // are both derived from this number, so a
                                // rejected echo must not move them either — a
                                // stale place recorded here is a wrong answer
                                // hours later, when nobody can connect it back.
                                FilterObj.RXFreqChange(s);
                                // Where-you-are memory (#226) — debounced, so
                                // a tuning sweep costs one write, not one per
                                // click.
                                NoteOperatorPlace(s);
                            }
                            if (s.IsTransmitSlice && _txFreqEcho.Accept(echoed)) _TXFrequency = echoed;
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
                    // #188 — the antenna report-back, RX and TX kept separate.
                    //
                    // What stood here was a `#if zero` block on case
                    // "TXAntenna", and it had TWO faults, either of which alone
                    // made it dead: it was compiled out, and FlexLib raises
                    // "TXAnt", never "TXAntenna" (Slice._UpdateTXAnt). So even
                    // enabling it would have matched nothing. Restored under
                    // the names FlexLib actually raises, with RX added — the
                    // receive port was never traced in any form.
                    //
                    // See the ReportAntennaChange remarks for what a silent
                    // radio does and does not prove.
                    case "RXAnt":
                        ReportAntennaChange(s, "RX", s.RXAnt, _rxAntRequested, _rxAntRequestedSlice);
                        break;

                    case "TXAnt":
                        ReportAntennaChange(s, "TX", s.TXAnt, _txAntRequested, _txAntRequestedSlice);
                        break;
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

        private void guiClientAdded(GUIClient client, ObservationBinding binding)
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
                // The start flags are NOT touched here any more (#634, Track
                // L6). This raise fires for a record from the radio's TCP
                // status, from a LAN discovery broadcast and from a SmartLink
                // list alike, and a stale list re-adding our own record used
                // to read as the radio saying we were back — and a list
                // omitting it, as the radio saying we had gone, which aborted
                // a live connect for a retry. The radio's own Connected and
                // Disconnected reports set them now (consumeClientReport).
                // Never let a fabricated record blank the real client id.
                if (!string.IsNullOrEmpty(client.ClientID)) clientID = client.ClientID;
                noteOwnClientIdReported(client.ClientID, "GUIClientAdded");
                clientHandle = client.ClientHandle;
                //CanTransmit = PrimaryStation;
                CanTransmit = true;
            }

            // #577. This used to assign OnlyStation = (GuiClients.Count == 1)
            // here, once, when OUR client was added, and nothing ever
            // recomputed it. The roster tracker is the live input now: every
            // add, update, remove and own-handle establishment recomputes,
            // and OnlyStation reads the current verdict. Fed BEFORE the
            // duplicate-name check below, which reads it.
            ObserveClientAdded(binding, client, isMine);

            // Who added it, for the row's sentence (#634, Track L7). A list
            // merge says so on this thread; otherwise a record without a
            // client_id was built from a discovery broadcast, because the
            // radio's own status always carries one.
            if (_mergingSmartLinkList)
                NoteClientAddedBy(binding.Generation, client.ClientHandle, ClientRowSource.SmartLinkList);
            else if (string.IsNullOrEmpty(client.ClientID) && !client.IsThisClient)
                NoteClientAddedBy(binding.Generation, client.ClientHandle, ClientRowSource.LocalDiscovery);

            if (isMine)
            {

                if (string.IsNullOrEmpty(client.Station))
                {
                    // Ensure no duplicate name — against records that can
                    // actually PROVE a duplicate. A fabricated record (no
                    // client_id, not FlexLib-stamped) came from discovery or
                    // the SmartLink server's list snapshot, and #402 caught
                    // that snapshot carrying OUR OWN just-disconnected
                    // session: the 2026-08-29 18:14 trace shows a ghost
                    // "k5ner" with id EMPTY — us, one connect ago — renaming
                    // us to "k5ner1" for a collision that did not exist. The
                    // radio's own TCP status is the authority on live
                    // clients, and every live client's record carries a
                    // client_id; a record without one is stale scenery, not
                    // a station to yield to.
                    if (!OnlyStation)
                    {
                        foreach (GUIClient c in theRadio.GuiClients)
                        {
                            if (myClient(c.ClientHandle)) continue;
                            bool identityBearing = c.IsThisClient || !string.IsNullOrEmpty(c.ClientID);
                            if (!identityBearing)
                            {
                                if (c.Station == Callouts.StationName)
                                    Tracing.TraceLine(
                                        $"guiClientAdded: ignoring fabricated record (handle {c.ClientHandle}, no client_id) holding station '{c.Station}' — stale list data, not a live duplicate",
                                        TraceLevel.Warning);
                                continue;
                            }
                            if (c.Station == Callouts.StationName)
                            {
                                Callouts.StationName += '1';
                                Tracing.TraceLine("guiClientAdded:station now will be " + Callouts.StationName, TraceLevel.Error);
                            }
                        }
                    }
                    requestStationName(Callouts.StationName);
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

            // "{who} connected" is NOT announced from here any more (#634,
            // Track L6): this raise cannot tell the radio's own status from a
            // SmartLink list or a discovery broadcast, and a stale list
            // announced arrivals that had not happened. The radio's own
            // Connected report announces (consumeClientReport); a client seen
            // only in a list is shown, labelled as reported, and not spoken.

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

            // A SmartLink list merge raises this per client; the merge itself
            // refreshes once, when the answer changed (#634).
            if (!_mergingSmartLinkList) GuiClientChanged?.Invoke();
        }

        /// <summary>
        /// What the radio's own DISCOVERY BROADCASTS say about our client at
        /// this moment — evidence that arrives over UDP, independent of the
        /// TCP receive path the station-name wait depends on. The API-side
        /// sighting (found by serial) is deliberately consulted rather than
        /// <c>theRadio</c>: on a WAN-path connect they can be different Radio
        /// objects, and it is the LAN sighting that FlexLib re-merges with
        /// every broadcast's station roll call.
        /// </summary>
        private (bool sawHandle, string station) DiscoveryStationEvidence()
        {
            try
            {
                var serial = theRadio?.Serial;
                if (string.IsNullOrWhiteSpace(serial) || clientHandle == noClient) return (false, "");
                var apiRadio = findRadioInAPI(serial);
                if (apiRadio == null) return (false, "");
                lock (apiRadio.GuiClientsLockObj)
                {
                    foreach (GUIClient c in apiRadio.GuiClients)
                    {
                        if (c.ClientHandle == clientHandle) return (true, c.Station ?? "");
                    }
                }
                return (false, "");
            }
            catch { return (false, ""); }
        }

        /// <summary>
        /// Turn the discovery evidence into the sentence the timeout trace
        /// needs. Pure, so the four verdicts are pinned by tests. The verdict
        /// distinguishes what 45 identical seconds of silence never could:
        /// whose defect this is.
        /// </summary>
        internal static string StationEvidenceVerdict(bool sawHandle, string discoveryStation, string requestedName)
        {
            if (!sawHandle)
                return "discovery carries no record of our client — the radio may never have processed the registration, or the sighting is gone";
            if (string.IsNullOrEmpty(discoveryStation))
                return "discovery shows our client with NO station name — the radio never applied it; the silence is radio-side";
            if (discoveryStation == requestedName)
                return $"discovery shows our client as '{discoveryStation}' — the radio APPLIED the name and the TCP echo was never read; the receive path stalled in-process, which is OUR defect, not the radio's";
            return $"discovery shows our client as '{discoveryStation}' where we asked for '{requestedName}' — a radio-side rename; the equality wait can never succeed";
        }

        /// <summary>
        /// Ask the radio to set our station name — and TRACE ITS ANSWER.
        ///
        /// <para>This replaces a fire-and-forget
        /// <c>theRadio.SetClientStationName(...)</c>. The #402 field traces
        /// show connects where the radio simply never applied the name: no
        /// TCP echo, no server-list change, 45 seconds of silence, three
        /// times over. With the old call the radio's reply — including an
        /// explicit refusal — was discarded unread, so a refusing radio and
        /// a dead send were indistinguishable. Now the reply (or a 10 s
        /// timeout) lands in the trace, which is the discriminating
        /// instrument the next field failure needs.</para>
        ///
        /// <para>The encoding matches FlexLib's <c>SetClientStationName</c>
        /// exactly: sanitize, then spaces to U+007F.</para>
        /// </summary>
        private void requestStationName(string name)
        {
            try
            {
                string encoded = Flex.Util.StringHelper.SanitizeInvalidRadioChars(name).Replace(' ', '\u007f');
                var reply = theRadio.SendCommandAsync("client station " + encoded);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var done = await Task.WhenAny(reply, Task.Delay(10000)).ConfigureAwait(false);
                        if (done != reply)
                        {
                            // Wording matters here (#402): this line used to
                            // claim the command "reached the wire but nothing
                            // acknowledged it", which reads as a radio-side
                            // silence. The 2026-08-30 traces disproved that
                            // reading — the radio HAD processed the command
                            // (its discovery broadcasts showed the name
                            // applied) and the reply sat unread behind a
                            // wedged receive loop. From this vantage the two
                            // are indistinguishable; say so.
                            Tracing.TraceLine($"requestStationName('{name}'): no reply was READ within 10s — either the radio stayed silent, or the reply arrived and our receive path never delivered it; the two look identical from here", TraceLevel.Error);
                            return;
                        }
                        var info = await reply.ConfigureAwait(false);
                        Tracing.TraceLine($"requestStationName('{name}'): radio acknowledged{(string.IsNullOrEmpty(info) ? "" : " — " + info)}", TraceLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine($"requestStationName('{name}'): radio REFUSED — {ex.Message}", TraceLevel.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"requestStationName('{name}'): send failed: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>
        /// Tell the radio to drop OUR GUI-client registration, by handle.
        ///
        /// <para>FlexLib's <c>Radio.Disconnect()</c> only closes the TCP
        /// socket — nothing ever tells the radio to release the
        /// registration, so it lingers radio-side (and in the SmartLink
        /// server's list snapshot) after every disconnect. #402's traces
        /// show both halves of the cost: the server's stale snapshot handed
        /// the next connect a ghost of our own dead session, and every
        /// quick reconnect landed on a radio still holding our previous
        /// registration — the window in which the station name was never
        /// applied. An explicit release is how a client says goodbye.</para>
        ///
        /// <para>Deliberately NOT <c>DisconnectClientByHandle</c>: that path
        /// sets FlexLib's <c>_ignoreConnectedEvents</c> and only a reply —
        /// which a teardown may never read — clears it, leaving the reused
        /// Radio object deaf to real connection drops. A bare
        /// <c>client disconnect</c> (no handle) is never sent — the radio
        /// reads that as "disconnect every GUI client".</para>
        ///
        /// <para>Fire-and-forget by design; never allowed to slow or fail a
        /// teardown. TCP ordering guarantees the command precedes our FIN.</para>
        /// </summary>
        private void releaseGuiClientRegistration(string reason)
        {
            try
            {
                if (theRadio == null || clientHandle == noClient) return;
                if (!theRadio.Connected) return;
                Tracing.TraceLine($"releaseGuiClientRegistration ({reason}): client disconnect {clientHandle}", TraceLevel.Info);
                _ = theRadio.SendCommandAsync("client disconnect " + clientHandle);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"releaseGuiClientRegistration ({reason}): {ex.Message}", TraceLevel.Warning);
            }
        }

        private bool myClient(uint handle)
        {
            return ((clientHandle == handle)) ? true : false;
        }

        /// <summary>
        /// Get a snapshot of the MultiFlex GUI clients the roster holds, with
        /// their owned slices and where the knowledge of each came from
        /// (<see cref="ClientRow"/>).
        /// </summary>
        /// <remarks>
        /// <para>Read from the roster tracker (#577) rather than the vendor's
        /// list, because the ruling (#634) says the SmartLink list updates
        /// the display AND the roster, and the roster is where a client that
        /// a list or broadcast stopped listing is KEPT, marked, until the
        /// radio's own status says otherwise — the vendor's list has already
        /// dropped it. <c>Source</c> is the radio when its own status reported
        /// the handle connected on this connection and not gone since;
        /// otherwise it names which source was observed reporting the row —
        /// a SmartLink list or a local discovery broadcast (Track L7) — and
        /// when none was, it is unknown and the row names no source (Track
        /// L8; see <see cref="SourceOfClient"/>). <c>mayHaveLeft</c> is the
        /// marked entry: reported earlier, and something has since omitted
        /// it.</para>
        /// </remarks>
        public List<ClientRow> GetGuiClients()
        {
            var result = new List<ClientRow>();
            var radio = theRadio;
            if (radio == null) return result;

            var snapshot = RosterTracker.Snapshot();
            foreach (var entry in snapshot.Entries)
            {
                var ownedSlices = new List<string>();
                lock (radio.GuiClientsLockObj)
                {
                    foreach (var s in radio.SliceList)
                    {
                        if (s.ClientHandle == entry.Handle && !string.IsNullOrEmpty(s.Letter))
                            ownedSlices.Add(s.Letter);
                    }
                }

                result.Add(new ClientRow(
                    string.IsNullOrEmpty(entry.Program) ? "Unknown" : entry.Program,
                    entry.Station ?? "",
                    entry.Handle,
                    entry.IsThisClient || myClient(entry.Handle),
                    string.Join(", ", ownedSlices),
                    SourceOfClient(entry.Handle, snapshot.AttemptGeneration),
                    entry.ReportedGoneByDiscovery));
            }
            return result;
        }

        /// <summary>
        /// True when this rig cannot yet say who is on the radio: our own
        /// handle is not established, or the roster does not hold it. The
        /// MultiFlex view says so rather than showing an empty list that
        /// reads as an empty radio (#634).
        /// </summary>
        public bool ClientInformationUnavailable =>
            theRadio == null || OtherOperatorPresence == RosterVerdict.Unknown;

        /// <summary>
        /// Disconnect a MultiFlex GUI client by handle. Refused for a handle
        /// the radio has not itself reported connected on this connection: a
        /// row that only a SmartLink list or a broadcast supplied cannot be
        /// disconnected from that row until the radio identifies it — an
        /// accepted cost of the ruling (#634).
        /// </summary>
        public bool DisconnectGuiClient(uint handle)
        {
            if (theRadio == null || myClient(handle)) return false;
            if (!RadioHasConfirmedClient(handle))
            {
                Tracing.TraceLine(
                    $"DisconnectGuiClient: refused for handle {handle} — the radio has not reported this client connected on this connection; only a list or a broadcast has",
                    TraceLevel.Info);
                return false;
            }
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
        /// The other clients the RADIO has reported connected on this
        /// connection, and that nothing has since stopped listing — the only
        /// clients a confirmation may name in a definite claim: "are
        /// connected", "will disconnect", "will lose the radio". Named by
        /// station, else program, else the unknown-client word. Empty when
        /// we're the only station, the radio is null, or no other client is
        /// confirmed.
        ///
        /// Callers use this to tell the user who else is affected before taking an
        /// action with radio-wide blast radius (reboot, firmware update, port-forward
        /// changes). On a MultiFlex radio "who else am I about to disconnect" is the
        /// single most useful thing to put in a confirmation prompt.
        ///
        /// <para><b>From the roster's rows, not the vendor's list (#634, Sol's
        /// review of L6).</b> Until Track L7 this read the vendor's merged
        /// client list, so a client only a stale SmartLink list or a broadcast
        /// mentioned was named here as connected, on the decisions that
        /// restart or reconfigure a shared radio; L6's caveat after the claim
        /// could not make the claim true. Those clients are in
        /// <see cref="UnconfirmedOtherStations"/> now, and only there. One
        /// split, <see cref="ClientRowPhrase.Company"/>, feeds both.</para>
        ///
        /// Never throws — information gathering must not block the operation it's
        /// describing.
        /// </summary>
        public System.Collections.Generic.List<string> OtherConnectedStations
        {
            get
            {
                try
                {
                    return ClientRowPhrase.Company(GetGuiClients()).Confirmed.ToList();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"OtherConnectedStations: {ex.Message}", TraceLevel.Error);
                    return new System.Collections.Generic.List<string>();
                }
            }
        }

        /// <summary>
        /// The other clients the roster holds that the radio has NOT itself
        /// reported connected on this connection — rows a SmartLink list or a
        /// discovery broadcast supplied, and rows marked as possibly gone.
        /// Named by station, else program, else the unknown-client word.
        /// Every blast-radius confirmation adds a caveat from this list —
        /// "may be affected" — and never names these clients in its definite
        /// claim (<see cref="OtherConnectedStations"/>), so no client is in
        /// both (#634, Track L7). Never throws.
        /// </summary>
        public System.Collections.Generic.List<string> UnconfirmedOtherStations
        {
            get
            {
                try
                {
                    return ClientRowPhrase.Company(GetGuiClients()).Reported.ToList();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"UnconfirmedOtherStations: {ex.Message}", TraceLevel.Error);
                    return new System.Collections.Generic.List<string>();
                }
            }
        }

        /// <summary>
        /// The caveat a confirmation adds when some of the company it names
        /// was reported by a list or a broadcast rather than by the radio, or
        /// null when every other client is radio-confirmed. DRAFT wording
        /// (Noel's, not ruled): <c>connect.client.unconfirmed_affected</c>.
        /// </summary>
        public string UnconfirmedCompanyCaveat
        {
            get
            {
                var unconfirmed = UnconfirmedOtherStations;
                if (unconfirmed.Count == 0) return null;
                // FOR NOEL'S PROSE REVIEW: draft 9 of the 2026-09-26 file.
                return Lexicon.Get("connect.client.unconfirmed_affected", ("clients", string.Join(", ", unconfirmed)));
            }
        }

        private void guiClientUpdated(GUIClient client, ObservationBinding binding)
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
                noteOwnClientIdReported(client.ClientID, "GUIClientUpdated");
                _LocalPTT = client.IsLocalPtt;
                _lastAuthoritativeLocalPtt = client.IsLocalPtt;
            }

            ObserveClientUpdated(binding, client);

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

            // A SmartLink list merge raises this per client; the merge itself
            // refreshes once, when the answer changed (#634).
            if (!_mergingSmartLinkList) GuiClientChanged?.Invoke();
        }

        private void guiClientRemoved(GUIClient client, ObservationBinding binding)
        {
            if (client == null) return;

            if (myClient(client.ClientHandle))
            {
                // Not the retry flag: a SmartLink list or a discovery packet
                // omitting our record raises this exactly as the radio's own
                // "client disconnected" does, and a stale list used to abort
                // a live connect this way (#634, Track L6). The radio's own
                // Disconnected report sets the flag (consumeClientReport).
                Tracing.TraceLine("guiClientRemoved:my client (as some source listed it; the radio's own report decides the retry)", TraceLevel.Info);
            }

            // #577: a removal recomputes the roster. FlexLib raises the
            // discovery-driven removal while holding its roster lock, so this
            // publishes and returns; the tracker never blocks. The origin
            // travels with it: a list or broadcast omission KEEPS the entry,
            // marked, so the display can say the client may have left rather
            // than that it did.
            ObserveClientRemoved(binding, client);

            // "{who} disconnected" is NOT announced from here any more (#634,
            // Track L6): the radio's own Disconnected report announces it
            // (consumeClientReport), and that path also removes the identity
            // snapshot. A list-only removal leaves the snapshot in place —
            // the client is treated as present until the radio speaks, and
            // when it does, the name must still be there to speak.

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

            // A SmartLink list merge raises this per client; the merge itself
            // refreshes once, when the answer changed (#634).
            if (!_mergingSmartLinkList) GuiClientChanged?.Invoke();
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
        /// Fired when what the Enhanced Signal Clarity dialog shows may have
        /// changed under it: the active slice moved, or the radio reported
        /// ESC on or off, phase or gain for one of our slices.
        /// </summary>
        /// <remarks>
        /// Sprint 48 Track D (#700's neighbour). <c>EscDialog</c> had asked
        /// for a refresh callback since Sprint 9 and nothing had ever been
        /// wired to call it, so a change made from another client, or a
        /// slice change, left the dialog showing stale values until it was
        /// reopened. This is the signal NativeMenuBar's wiring subscribes.
        /// </remarks>
        public event Action EscStateChanged;

        /// <summary>
        /// Fired when the radio connection state changes (connected/disconnected).
        /// UI layers subscribe to rebuild menus and update connection-dependent controls.
        /// </summary>
        public event Action<bool> ConnectionStateChanged;

        private void sliceAdded(Slice slc, ObservationBinding binding)
        {
            if (myClient(slc.ClientHandle))
            {
                mySliceAdded = true;
                slc.PropertyChanged += new PropertyChangedEventHandler(slicePropertyChangedHandler);
                slc.PropertyChanged += new PropertyChangedEventHandler((s2, e2) =>
                {
                    // One field per notification: a mode report never blesses
                    // a frequency, and the reverse (Track G3).
                    ObserveOwnSliceReported(binding, (Slice)s2, e2.PropertyName);
                });
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
                // Own-station evidence for the station coordinator. FlexLib
                // fires this only once the slice is READY (CheckReady: full
                // status and a ready panadapter, or explicitly headless), so
                // the observation carries a resolved association.
                ObserveOwnSliceAdded(binding, slc);
                // On a returning connect the arrival log attributes the slice
                // (its handle is parsed by now), and a held post-connect fence
                // re-checks readiness here (#782).
                noteArrivals();
                tryRecordConnectFence("own slice " + slc.Index + " becoming ready");
                SliceCountChanged?.Invoke();
                // #58: opens the bulk window and restarts the settle timer, so
                // a connect that delivers four slices produces ONE census
                // instead of four per-slice announcements.
                NoteSliceSetChanged(slc, added: true);
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

        private void sliceRemoved(Slice slc, ObservationBinding binding)
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
                    ObserveOwnSliceRemoved(binding, slc);
                    SliceCountChanged?.Invoke();
                    // #58 and #117: one census after the set settles, and — when
                    // the operator asked for it — the receipt saying the change
                    // is provisional. Releasing three slices in a burst still
                    // costs exactly one of each.
                    bool diagnosticRemoval;
                    DiagnosticSlicePurpose diagnosticPurpose;
                    lock (_sliceSettleLock)
                        diagnosticRemoval = _diagnosticSlices.TryGetValue(slc, out diagnosticPurpose);
                    if (diagnosticRemoval && !SuppressSpeech)
                        ScreenReaderOutput.Speak(Lexicon.Get(diagnosticPurpose switch
                        {
                            DiagnosticSlicePurpose.Loopback => "audio.check.loopback_removing_ears",
                            DiagnosticSlicePurpose.OffAir => "audio.iq.offair_removing_listener",
                            _ => "audio.iq.removing_listener"
                        }), VerbosityLevel.Terse);
                    NoteSliceSetChanged(slc, added: false);
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
        private void panadapterAdded(Panadapter pan, Waterfall fall, ObservationBinding binding)
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
                ObserveOwnPanadapterAdded(binding, pan);
                Tracing.TraceLine("panadapterAdded:mine " + ct.ToString() + ':' + pan.ToString(), TraceLevel.Info);
                noteArrivals(); // attribute this pan's arrival on a returning connect (#782)
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

        private void panAdapterRemoved(Panadapter pan, ObservationBinding binding)
        {
            if (myClient(pan.ClientHandle))
            {
                int ct;
                lock (myPanAdapters)
                {
                    myPanAdapters.Remove(pan);
                    ct = myPanAdapters.Count;
                }
                ObserveOwnPanadapterRemoved(binding, pan);
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
            // The window this reading arrived in, taken BEFORE it is stored:
            // see beginTxLineWindow (#625, H18).
            int txLineWindow = txLineWindowNow();
            meterTrace.Report("forwardPower:", data);
            // The change guard here existed only to avoid re-raising
            // MeterChanged for a repeated value. With that event gone the
            // comparison decides nothing, so the assignment stands alone.
            //
            // Stamped under the pair lock (#453): the value alone cannot tell a
            // consumer whether it was sampled with its reflected counterpart or
            // a burst earlier, and on a speech envelope that is the difference
            // between a reflected share and an artefact. See
            // ReadTransmitPower.
            lock (_powerPairLock)
            {
                _PowerDBM = data;
                _forwardStamp = Stopwatch.GetTimestamp();
            }
            Volatile.Write(ref _txLineFwdWindow, txLineWindow);   // after the value: see traceTxMeters
            // Sprint 44 Track E — the pulse that makes txMeters fire during a
            // TUNE. SC_MIC and ALC drive traceTxMeters during keyed transmit,
            // and neither of them moves while the ATU sweeps or a bare carrier
            // is up, so the whole 75 m tuning walk on 2026-09-01 produced no
            // meter data at all. Forward power streams throughout a tune, which
            // makes it the honest clock for that state. Outside the lock on
            // purpose: ReflectedFraction and ComputedSWR read the pair.
            if (_tuneCycleActive) traceTxMeters();
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
            int txLineWindow = txLineWindowNow();   // before the value: see forwardPowerData
            meterTrace.Report("SWRData:", data);
            _SWR = data;
            Volatile.Write(ref _txLineSwrWindow, txLineWindow);   // after the value: see traceTxMeters
            // Latch the settled value while the tune is still running; reading
            // it afterwards gives the meter's idle rest value. See noteTuneSwr.
            noteTuneSwr(data);
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
        public float ComputedSWR
        {
            get
            {
                // #453. Measured 2026-09-07 on the bench, 100 W into a
                // 400 W dummy load reading 1.01 on the radio's own meter:
                //
                //   fwdW = 44.67  reflW = 0.031  ->  ours said 1.05  (right)
                //   fwdW =  0.48  reflW = 0.083  ->  ours said 2.43  (wrong)
                //
                // Reflected ROSE while forward fell ninetyfold. Reflected
                // power cannot do that: it was a stale sample from the peak,
                // divided by a forward reading that had already moved on.
                // Across the run ours ranged 1.04 to 4.84 while the radio held
                // 1.01 to 1.22. Don's alarm trips at 1.7.
                //
                // The rule was already written down, one screen up from here:
                // "anything JUDGING the two together takes them from here
                // instead" — and the safety paths obey it. This property, the
                // one an OPERATOR sees, read the two fields raw.
                //
                // So it now asks the same question the kill switch asks. An
                // incoherent pair is NaN, which every caller already renders as
                // "no reading" — the honest answer, and the one this file
                // already gives when reflected exceeds forward, which is the
                // extreme case of exactly this.
                TransmitPowerReading pair = ReadTransmitPower();
                if (!pair.IsCoherent) return float.NaN;

                // And the floor, which is what was ACTUALLY wrong (#453,
                // measured 2026-09-07 across 194 samples at the bench):
                //
                //   every false high — all seven, no exceptions — came from a
                //   sample below 1.5 W forward, against a mean of 39 W and
                //   peaks of 107 W with 100 W commanded.
                //
                // The coherence gate above rejected 1 of those 194. Skew was
                // never the mechanism: the two meters arrive together, and the
                // ratio of two numbers near the coupler's resolution is noise.
                //
                // A FRACTION OF COMMANDED POWER, not an absolute. Commanded
                // power is a setting, so it does not chase the operator's voice
                // the way forward power does, and a floor built from it scales
                // with what was asked for instead of being tuned to one radio.
                // The speech processor and compander were ON for that run,
                // which REDUCES dynamic range — so those troughs are real
                // silences between words, and the floor is safer than the
                // numbers alone suggest.
                //
                // The pair carries the commanded power it was taken against
                // (CommandedWatts, filled by ReadTransmitPower from
                // CommandedPowerWatts), so the floor and the reading are from
                // one instant.
                if (pair.ForwardWatts < MinBelievableForwardWatts(pair.CommandedWatts))
                    return float.NaN;

                return SwrFromPower(_PowerDBM, _ReflectedPower);
            }
        }

        /// <summary>
        /// The lowest forward power at which a forward/reflected pair means
        /// anything to the DISPLAY, for a given COMMANDED power (#453).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one floor — <see cref="TransmitSafety.BelievableForwardFloorWatts"/>,
        /// with its one absolute gate — asked with no peak. Until Sprint 47 it
        /// had its own fraction (a twentieth of commanded, which moved to
        /// <see cref="TransmitSafety.ForwardFloorShareOfCommanded"/> unchanged)
        /// and its own absolute floor (0.25 W, chosen so a one-watt carrier
        /// would still be judged, not from any coupler data), which was one of
        /// three absolute floors for one physical question. It is 1 W now,
        /// with the other two; a carrier under a watt reads "not measured",
        /// which is honest, where it used to read a number of unknown worth.
        /// </para>
        /// <para>
        /// <b>No peak, on purpose.</b> The peak term exists for foldback and
        /// is weakest at the start of a transmission, while the peak is still
        /// climbing; the alarm can afford that because a settle time and a
        /// three-sample persistence rule stand behind it, and the display has
        /// neither. On the 2026-09-07 run the commanded term alone rejects
        /// every false high. #571's gated integrator, which will hold a window
        /// and a peak, is where the display gets its second gate.
        /// </para>
        /// </remarks>
        public static float MinBelievableForwardWatts(int commandedWatts) =>
            TransmitSafety.BelievableForwardFloorWatts(commandedWatts, float.NaN);

        /// <summary>
        /// The power the operator actually asked for RIGHT NOW — tune power
        /// during a tune carrier, RF power otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A tune does not transmit at <c>RFPower</c>.</b> It transmits at
        /// <c>TunePower</c>, and the two are independent settings — 100 W RF
        /// with a 2 W tune is an ordinary way to set a radio up.
        /// <c>FixerTransmitBoundary.ReadTransmitPowerWatts</c> has always drawn
        /// this distinction (<c>tuneCarrier ? rig.TunePower : rig.XmitPower</c>);
        /// the #453 floor was written reading <c>XmitPower</c> unconditionally
        /// and inherited the bug the same day it landed.
        /// </para>
        /// <para>
        /// It matters because <see cref="TuneCycleSettledComputedSwr"/> latches
        /// <see cref="ComputedSWR"/> DURING the carrier (#570). Against an
        /// RF-power floor, an operator whose tune power is under five percent
        /// of their RF power would hear "SWR not measured" after every single
        /// tune — at the exact moment they asked the question. The bench could
        /// not have shown it: every trace from 2026-09-07 reads
        /// <c>RFPower:100 TunePower:99</c>.
        /// </para>
        /// <para>
        /// <b>An ATU tune is deliberately NOT included.</b> Whether the ATU
        /// runs its own internal level rather than <c>TunePower</c> is not
        /// established, and guessing low would lower the floor exactly when the
        /// match is sweeping. See #453.
        /// </para>
        /// </remarks>
        public int CommandedPowerWatts
        {
            get
            {
                bool tuning;
                // Same crash class as the TX getter family: the radio can tear
                // down under a meter callback.
                try { tuning = theRadio?.TXTune ?? false; }
                catch { tuning = false; }
                return tuning ? _TunePower : _XmitPower;
            }
        }

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

            // Below the absolute forward-power floor there is no transmit
            // worth judging, and the ratio of two tiny numbers is noise. This
            // was a bare 0.05 W until Sprint 47 — measured against a dead key,
            // thirty times under where the bench found noise — and one of
            // three absolute floors for one question. Defence in depth only:
            // ComputedSWR stands behind the full floor first.
            if (pf < TransmitSafety.ForwardFloorWatts) return float.NaN;

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

        // --- Forward and reflected as ONE reading (#453) ---------------------
        //
        // The two meters arrive as two separate FlexLib callbacks, each
        // assigning its own field. Reading ForwardPowerWatts and then
        // ReflectedPowerWatts is therefore NOT a sample of one instant, and on
        // a speech envelope — where forward power plunges toward zero between
        // syllables many times a second — a small forward reading divided into
        // a slightly older, larger reflected one spikes the ratio past any
        // threshold. It ended real transmissions on a correctly matched
        // antenna. The two properties above stay exactly as they are (plenty of
        // callers legitimately want one number); anything JUDGING the two
        // together takes them from here instead.
        private readonly object _powerPairLock = new object();
        private long _forwardStamp;     // Stopwatch ticks, 0 = never reported
        private long _reflectedStamp;

        /// <summary>
        /// Forward and reflected transmit power as ONE reading, carrying the
        /// skew between the two meter samples and their age.
        /// </summary>
        /// <remarks>
        /// A method rather than a property because it takes a lock and reads a
        /// clock. Returns <see cref="TransmitPowerReading.None"/> until both
        /// meters have reported at least once, so a radio that never sends one
        /// of them judges nothing rather than judging a default.
        /// </remarks>
        public TransmitPowerReading ReadTransmitPower()
        {
            float fwdDbm, refDbm;
            long fwdAt, refAt;
            lock (_powerPairLock)
            {
                fwdDbm = _PowerDBM;
                refDbm = _ReflectedPower;
                fwdAt = _forwardStamp;
                refAt = _reflectedStamp;
            }
            if (fwdAt == 0 || refAt == 0) return TransmitPowerReading.None;

            double perMs = Stopwatch.Frequency / 1000.0;
            double skew = Math.Abs(fwdAt - refAt) / perMs;
            double age = (Stopwatch.GetTimestamp() - Math.Max(fwdAt, refAt)) / perMs;
            // What was asked for travels with the pair, so the forward-power
            // floor is judged against the setting in force at this instant.
            return new TransmitPowerReading(
                DBmToWatts(fwdDbm), DBmToWatts(refDbm), (float)skew, (float)age,
                commandedWatts: CommandedPowerWatts);
        }

        /// <summary>
        /// The SWR shown to the operator on the manual-tuner button.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two things were wrong here and the second was the larger (#454).</b>
        /// This was <c>_SWR.ToString("f1")</c> — the raw radio meter, straight
        /// to a string, with no test for the sentinel. <see cref="SWRNoReading"/>
        /// is −25, the radio reports it whenever it has no reading including
        /// part-way through a transmit, and its own comment says it must never
        /// be read as a low SWR. It was rendered as <b>"-25.0"</b>. A standing
        /// wave ratio cannot be negative; anything below 1 is by construction
        /// not a measurement.
        /// </para>
        /// <para>
        /// And the raw meter should not have been the source at all. It is
        /// documented as untrustworthy exactly where it matters — measured on
        /// 2026-08-22 reporting <b>1.008</b> while 76 percent of the power was
        /// coming back off an empty port. <see cref="ComputedSWR"/> exists
        /// because of that measurement and the display never adopted it. The
        /// meters panel already made this move; this was the last raw-meter
        /// reader left.
        /// </para>
        /// <para>
        /// NaN — not enough forward power to derive anything — says so in
        /// words. Silence would be ambiguous and an invented 1.0 is the failure
        /// being replaced.
        /// </para>
        /// </remarks>
        private string SWRText()
        {
            float swr = ComputedSWR;
            if (float.IsNaN(swr) || swr < 1f)
                return Lexicon.Get("audio.tune.swr_no_reading");
            return swr.ToString("f1");
        }

        private float _MicData;
        /// <summary>
        /// Current microphone level from hardware meter. Updated by meter callback.
        /// </summary>
        public float MicData => _MicData;

        private void micData(float data)
        {
            _MicData = data;
            // The inventory reconcile is also what registers every SC_MIC and
            // ALC copy with the transmit-meter elections — there is no separate
            // FindMeterByName hook any more (#502).
            syncMeterInventory(); // cheap no-op unless the meter set has changed
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

        // --- Transmit-audio meters (2026-08-11; elections 2026-09-02) --------
        // Why these two meters:
        //   * SC_MIC ("MIC output") sits downstream of mic SELECTION, so it
        //     reflects transmit audio from EITHER source — the analog mic AND
        //     PC/codec audio. MicData (the COD-/MIC meter, "MIC in CODEC") is the
        //     analog ADC path and reads -120 for PC audio, which is why the old
        //     "Check microphone" warning cried wolf on every PC-audio transmit.
        //   * SW ALC is the real transmit-drive meter (the ALC property above is
        //     HWALC, the external-amp jack).
        // Proven with a two-source meter truth table on the bench 2026-08-11.
        // All values dBFS.
        //
        // The radio publishes each of them ONCE PER TRANSMIT SOURCE, with
        // byte-identical descriptors, and this code used to subscribe to the
        // first copy FlexLib's FindMeterByName returned. On Don's 6300 that
        // copy never delivers a sample (#502): three SC_MIC copies, the first
        // silent, and the operator was told no transmit audio was reaching the
        // radio while the transmit monitor was playing his voice back to him.
        // MeterInventory.Find's remarks predicted exactly this on 2026-08-20.
        //
        // So every copy is now registered with a TransmitMeterElection (fed
        // from the one generic meter feed, onMeterDataReady), and the values
        // below are the ELECTED copy's — the one that streams while keyed.
        // Until some copy reports, nothing is elected and the readings sit at
        // the -150 floor, which is why ScMicReportedSinceReset exists: the mic
        // warning must not read that floor as silence (#459).
        private readonly TransmitMeterElection _scMicElection = new TransmitMeterElection("SC_MIC");
        private readonly TransmitMeterElection _swAlcElection = new TransmitMeterElection("ALC");
        private string _txMeterCensus = "";
        private float _scMicRecentDb = -150f;
        private int _scMicRecentTime;
        private const float TxMeterFloorDbfs = -150f;
        private static float floorIfNone(float v) => float.IsNaN(v) ? TxMeterFloorDbfs : v;
        /// <summary>Instantaneous SC_MIC — transmit mic audio from any source, dBFS,
        /// from the elected copy. -150 until a copy reports.</summary>
        public float ScMicDb => floorIfNone(_scMicElection.ElectedLast);
        /// <summary>Max SC_MIC since the last <see cref="ResetScMicMax"/> — a peak-hold
        /// across a transmit window so the gaps between spoken words don't read as silence.
        /// Use for the silent-mic warning and end-of-check verdict — together with
        /// <see cref="ScMicReportedSinceReset"/>, because -150 here means "no sample",
        /// not "silence".</summary>
        public float ScMicMaxDb => floorIfNone(_scMicElection.ElectedPeakSinceReset);
        /// <summary>Peak SC_MIC over a rolling ~1.5 s window, dBFS. Follows the level down
        /// after ~1.5 s, so a LIVE "how's my audio" query tracks mic-gain changes mid-transmit
        /// (where <see cref="ScMicMaxDb"/> only ever grows).</summary>
        public float ScMicRecentDb => _scMicRecentDb;
        /// <summary>SW ALC — transmit drive after software ALC (SSB peak), dBFS, from the
        /// elected copy. Distinct from <see cref="ALC"/> (=HWALC, the external-amplifier jack).</summary>
        public float SwAlcDb => floorIfNone(_swAlcElection.ElectedLast);
        /// <summary>True once some SC_MIC copy has ever delivered a sample — the gate for
        /// publishing <see cref="ScMicDb"/> and friends as measurements at all.</summary>
        public bool ScMicHasReported => _scMicElection.HasElected;
        /// <summary>True when the elected SC_MIC copy has delivered a sample since the last
        /// <see cref="ResetScMicMax"/> — since key-down. <b>The telemetry test</b> (#459, #502):
        /// without it a floor reading is not evidence of silence, only of not being
        /// connected to the instrument, and the mic warning must not fire on it.</summary>
        public bool ScMicReportedSinceReset => _scMicElection.ElectedReportedSinceReset;
        /// <summary>True once some plain ALC copy has ever delivered a sample.</summary>
        public bool SwAlcHasReported => _swAlcElection.HasElected;
        /// <summary>How many SC_MIC copies the radio publishes: one on the bench 8600 alone,
        /// four with a station client and four slices open, three on Don's 6300.</summary>
        public int ScMicCopies => _scMicElection.CandidateCount;
        /// <summary>The SC_MIC election in words: every copy, which is believed, and why.
        /// For traces and diagnostic reports; never spoken.</summary>
        public string ScMicElectionText => _scMicElection.Describe(System.Environment.TickCount);
        /// <summary>The ALC election in words. See <see cref="ScMicElectionText"/>.</summary>
        public string SwAlcElectionText => _swAlcElection.Describe(System.Environment.TickCount);
        /// <summary>Reset the SC_MIC peak-hold and the since-key-down counts of every
        /// transmit-meter copy. Call at transmit start.</summary>
        public void ResetScMicMax()
        {
            _scMicElection.ResetPeaks();
            _swAlcElection.ResetPeaks();
            _scMicRecentDb = -150f;
        }
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
            reconcileTxMeterCandidates(snapshot);
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
            routeTxMeterSample(meter, data);
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
            _scMicElection.Clear();
            _swAlcElection.Clear();
            _txMeterCensus = "";
            // A new connection opens a new txMeters evidence window, like the
            // elections cleared above, so a value left over from the last radio
            // is never written as this one's reading (#625, H17; a window
            // rather than flags since H18).
            beginTxLineWindow();
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

        private static bool isScMic(Meter m) =>
            string.Equals(m?.Name, "SC_MIC", StringComparison.OrdinalIgnoreCase);
        private static bool isSwAlc(Meter m) =>
            string.Equals(m?.Name, "ALC", StringComparison.OrdinalIgnoreCase);
        private static string txMeterLabel(Meter m) =>
            "[" + m.Index + "] " + m.Source + ":" + m.SourceIndex;

        /// <summary>
        /// Register every SC_MIC and plain-ALC copy in the radio's meter list
        /// with its election, drop the ones the radio withdrew, and trace the
        /// census whenever it changes.
        /// <para>This replaces hookTxMeters, which looked each meter up ONCE
        /// through FlexLib's FindMeterByName — a FirstOrDefault — and subscribed
        /// to whichever copy came first. The generic feed already subscribes to
        /// every meter (syncMeterInventory), so the copies need only be told
        /// apart, not hooked again.</para>
        /// <para>Case-insensitive on purpose. FindMeterByName was case-sensitive
        /// while MeterInventory is not, and the diagnostic gate and the value it
        /// gated were therefore answering two different questions.</para>
        /// </summary>
        private void reconcileTxMeterCandidates(ImmutableList<Meter> snapshot)
        {
            try
            {
                var present = new HashSet<Meter>();
                foreach (Meter m in snapshot)
                {
                    if (m == null) continue;
                    if (isScMic(m)) { present.Add(m); _scMicElection.Register(m, txMeterLabel(m), m.Index); }
                    else if (isSwAlc(m)) { present.Add(m); _swAlcElection.Register(m, txMeterLabel(m), m.Index); }
                }
                _scMicElection.KeepOnly(k => k is Meter m && present.Contains(m));
                _swAlcElection.KeepOnly(k => k is Meter m && present.Contains(m));

                // Say plainly what the radio offers, once per change. A radio
                // may carry MIC, MICPEAK and HWALC and no plain ALC at all, in
                // which case SwAlcDb never moves; the trace should not make
                // anyone guess why. And on a radio with several copies, this is
                // the line that tells a reader which labels to look for in the
                // election traces that follow.
                string census = _scMicElection.Census() + "; " + _swAlcElection.Census();
                if (census != _txMeterCensus)
                {
                    _txMeterCensus = census;
                    Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, "txMeters: " + census, TraceLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("reconcileTxMeterCandidates: " + ex.Message, TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Feed one sample to the election it belongs to and publish it only
        /// if it came from the elected copy. Runs for every sample of every
        /// meter, on FlexLib's meter thread: two short string compares for the
        /// hundred-odd meters that are neither.
        /// </summary>
        private void routeTxMeterSample(Meter meter, float data)
        {
            if (meter == null) return;
            bool scMic = isScMic(meter);
            if (!scMic && !isSwAlc(meter)) return;

            TransmitMeterElection election = scMic ? _scMicElection : _swAlcElection;
            int now = System.Environment.TickCount;
            // The txMeters window, taken before the election stores the value:
            // see beginTxLineWindow (#625, H18).
            int txLineWindow = txLineWindowNow();
            var outcome = election.Report(meter, data, now);
            if (outcome == TransmitMeterElection.Outcome.Unknown)
            {
                // A sample can arrive before the inventory pass that registers
                // the copy. Introduce it and try again — dropping it would be
                // harmless, but a copy must never be silently uncounted.
                election.Register(meter, txMeterLabel(meter), meter.Index);
                outcome = election.Report(meter, data, now);
            }
            if (outcome == TransmitMeterElection.Outcome.Ignored) return;

            // Published: the elected copy's value, arrived in this window.
            bool newCopy = outcome == TransmitMeterElection.Outcome.Elected
                || outcome == TransmitMeterElection.Outcome.Displaced;
            if (scMic) noteTxLineScMic(txLineWindow, data, newCopy);
            else Volatile.Write(ref _txLineSwAlcWindow, txLineWindow);

            if (outcome == TransmitMeterElection.Outcome.Elected
                || outcome == TransmitMeterElection.Outcome.Displaced)
            {
                // The choice, explicitly, every time it is made. This is the
                // line the MeterInventory remarks asked for: a human can read
                // which copy is believed and why, rather than inferring it from
                // an ordering.
                Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, "txMeters: " + election.MeterName + " "
                    + (outcome == TransmitMeterElection.Outcome.Elected ? "elected " : "re-elected ")
                    + election.Elected?.Label + " — " + election.LastElectionReason
                    + ". " + election.Describe(now), TraceLevel.Info);
                if (scMic) { _scMicRecentDb = data; _scMicRecentTime = now; }
            }
            else if (scMic)
            {
                if (data >= _scMicRecentDb || (now - _scMicRecentTime) > 1500) { _scMicRecentDb = data; _scMicRecentTime = now; }
            }
            traceTxMeters();
        }

        // When traceTxMeters last wrote, on a 64-BIT clock (#625, H18). This
        // was an int compared against Environment.TickCount, starting at 0.
        // TickCount is negative for the second half of every 49.7-day cycle
        // of Windows uptime, so on a machine up between about 24.9 and 49.7
        // days "now - 0" was negative, always under the interval, and the
        // field never advanced: the app wrote no txMeters line at all, and
        // TxFactAudit read the silence as no transmission. TickCount64 does
        // not go negative for 292 million years. The clock is a field only so
        // a test can stand the writer on the uptime that used to silence it.
        private long _txMeterTraceTime = TxMeterLineNeverWritten;
        private Func<long> _txMeterTraceClock = () => System.Environment.TickCount64;

        /// <summary>The writer has not written a <c>txMeters:</c> line yet, so
        /// the next one is due whatever the clock says.</summary>
        private const long TxMeterLineNeverWritten = long.MinValue;

        /// <summary>Whether the <c>txMeters:</c> rate limit lets a line be
        /// written now. 64-bit throughout; see <see cref="_txMeterTraceTime"/>.</summary>
        internal static bool TxMeterLineDue(long nowMs, long lastWrittenMs, int intervalMs) =>
            lastWrittenMs == TxMeterLineNeverWritten || nowMs - lastWrittenMs >= intervalMs;

        // ── The txMeters line's evidence window (#625, H17 then H18) ────────
        //
        // Every number on a txMeters line is one the radio sent DURING THE
        // CURRENT TRANSMISSION OR TUNE. H17 gated each value on "reported
        // since this connection", which Sol's H17 review showed was the wrong
        // window for a transmit snapshot: the election keeps SC_MIC's last
        // value across key-downs on purpose (other readers want it), so a
        // second transmission's first line, driven by a SWALC sample before
        // SC_MIC had spoken, printed the FIRST transmission's mic level as
        // SC_MIC=<number> beside "(peak no-sample)" — two fields, two windows,
        // one line. The same held for the powers.
        //
        // So the writer keeps its own window, and solves it here rather than
        // in TransmitMeterElection or the PTT safety controller, which retain
        // what they retain for other readers. A window opens when the radio
        // becomes keyed — it reports Mox, or a tune cycle begins, whichever
        // comes first — and when a new connection's meters are about to be
        // hooked. Each value the line prints records the window it arrived
        // in, captured BEFORE the value is stored, so a stamp that matches the
        // current window can only belong to a value that arrived inside it.
        // The window is the radio's keyed state, not the PTT controller's
        // key-down, because a transmission can be keyed without it (a foot
        // switch, the radio's own PTT, CW, another client): the controller's
        // peak reset never runs for those, and its peak then spans every
        // transmission since the last one it did key.
        //
        // Separate from _forwardStamp/_reflectedStamp on purpose: those feed
        // ReadTransmitPower and the transmit safety paths, which are not this
        // line's to change.
        private int _txLineWindow;
        private int _txLineFwdWindow = TxLineNoWindow;
        private int _txLineReflWindow = TxLineNoWindow;
        private int _txLineSwrWindow = TxLineNoWindow;
        private int _txLineSwAlcWindow = TxLineNoWindow;

        // SC_MIC's window and the line's own peak move together, under one
        // lock, because the peak is only meaningful inside its window. The
        // peak is the writer's, not the election's ElectedPeakSinceReset:
        // that one resets only when the PTT controller keys, so on any other
        // keying it would print an earlier transmission's peak. It restarts
        // when the believed copy changes, so the peak and the copy named
        // after "via" are always the same copy.
        private readonly object _txLineScMicLock = new object();
        private int _txLineScMicWindow = TxLineNoWindow;
        private float _txLineScMicPeak = float.NaN;

        /// <summary>A value stamped with this has not arrived in any window.</summary>
        private const int TxLineNoWindow = -1;

        /// <summary>The txMeters evidence window in force now.</summary>
        private int txLineWindowNow() => Volatile.Read(ref _txLineWindow);

        /// <summary>
        /// Open a new evidence window: from here on, nothing that arrived
        /// before counts as a reading on a txMeters line. Called BEFORE the
        /// state that makes the writer run is set, so a writer that sees the
        /// radio keyed already sees the new window.
        /// </summary>
        private void beginTxLineWindow()
        {
            // Never TxLineNoWindow, however many windows a session opens.
            int next = Interlocked.Increment(ref _txLineWindow);
            if (next == TxLineNoWindow) Interlocked.Increment(ref _txLineWindow);
        }

        /// <summary>
        /// The radio reported its transmit state. The txMeters line opens a new
        /// evidence window on the rising edge, unless a tune cycle already
        /// opened it: an ATU sweep that keys Mox is one keyed stretch, not two.
        /// Extracted from the property handler's Mox case so the edge can be
        /// driven without a radio.
        /// </summary>
        private void noteRadioMox(bool mox)
        {
            bool oldTransmit = _Transmit;
            if (mox && !oldTransmit && !_tuneCycleActive) beginTxLineWindow();
            if (mox && !oldTransmit && TxReferenceArmed)
                txFilePlayer.RestartOnNextBuffer();
            _Transmit = mox;
            if (oldTransmit && !mox)
            {
                _lastTransmitEndMs = Environment.TickCount64;
                if (TxReferenceArmed)
                {
                    if (txFilePlayer.Engaged) txFilePlayer.RestartOnNextBuffer();
                    else TxFileStop();
                }
            }
            if (_Transmit != oldTransmit)
            {
                raiseTransmitChange(_Transmit);
            }
        }

        /// <summary>SC_MIC's elected copy published <paramref name="data"/>,
        /// which arrived in <paramref name="window"/>. Keeps the line's peak
        /// for that window and that copy.</summary>
        private void noteTxLineScMic(int window, float data, bool newCopy)
        {
            lock (_txLineScMicLock)
            {
                bool restart = newCopy || window != _txLineScMicWindow || float.IsNaN(_txLineScMicPeak);
                if (restart || data > _txLineScMicPeak) _txLineScMicPeak = data;
                _txLineScMicWindow = window;
            }
        }

        /// <summary>The <c>txMeters:</c> rendering of one value: the number, or
        /// <see cref="CaptureMeterSet.NoSample"/> when the radio has not
        /// reported it. NaN is the no-sample signal the elections use.
        /// <para><b>Invariant culture, always (#625, H18).</b> The number went
        /// through the machine's own culture, so a German or Swedish machine
        /// wrote <c>-18,0</c> or <c>−18,0</c>, and TxFactAudit, reading
        /// digits and periods, silently dropped every such line and then told
        /// its reader the radio had not transmitted. A diagnostic file leaves
        /// the machine it was written on and is read somewhere else, so its
        /// numbers are written one way everywhere. Sol's blocker 2.</para></summary>
        private static string txMeterField(float value, string format, string unit = "")
        {
            return float.IsNaN(value) ? CaptureMeterSet.NoSample : txMeterNumber(value, format) + unit;
        }

        /// <summary>A <c>txMeters:</c> number, in the invariant culture. See
        /// <see cref="txMeterField"/>.</summary>
        private static string txMeterNumber(float value, string format) =>
            value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// A correlated SC_MIC / SW ALC / forward-power snapshot, at most once a
        /// second while transmitting and at most once every 250 ms during a tune
        /// cycle. The line names which of the two it was sampled in.
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
            // #Track E — a TUNE counts, and until 2026-09-02 it did not.
            // "if (!Transmit) return" is true throughout an ATU sweep and
            // throughout a manual carrier, because neither raises Mox, so the
            // one state where reflected power is most interesting was the one
            // state carrying no meter data whatsoever.
            bool tuning = _tuneCycleActive;
            if (!Transmit && !tuning) return;

            // Rate-limited by STATE, chosen rather than inherited. Keyed
            // transmit can run for minutes and one line a second is plenty; a
            // tune is over in a few seconds, and at one line a second a whole
            // tune is three or four samples — too coarse to see a match
            // converging. 250 ms during a tune gives roughly 12-40 lines for a
            // typical cycle, which is a bounded burst and not a stream.
            int interval = tuning ? 250 : 1000;
            long now = _txMeterTraceClock();
            if (!TxMeterLineDue(now, _txMeterTraceTime, interval)) return;
            _txMeterTraceTime = now;
            // Reflected, both SWR figures and the derived share are here
            // BECAUSE THEY WERE NOT, and their absence cost an evening.
            // On 2026-09-01 four transmissions were captured at Verbose while
            // hunting a reflected-power alarm that never fired, and this line
            // — the transmit diagnostic — printed forward power and omitted the
            // one number the alarm actually judges. An instrument that shows
            // everything except the quantity under investigation sends the
            // reader off inventing mechanisms; it produced three wrong
            // hypotheses before the operator's own memory corrected it.
            //
            // Raw AND computed SWR, deliberately: the radio's own meter reads
            // 1.008 with 76% of the power coming back (see ComputedSWR), so
            // the two disagreeing IS the diagnosis, and one alone hides it.
            //
            // NO NUMBER FOR A VALUE THAT HAS NOT ARRIVED (#625, H17). Every
            // field below used to be printed whether or not the radio had
            // reported it: forward power as its -150 dBm "nothing yet"
            // initialiser, reflected power as a default zero — which is one
            // milliwatt, a real-looking reading — SWRraw as 0.00, SC_MIC and
            // SWALC at their -150 floor. The line's own introduction calls
            // these readings, and Sol's H16 review showed the writer could
            // emit it before either power meter had spoken (it is driven by
            // SC_MIC and SWALC samples). The writer is the thing that knows
            // whether a value arrived, so it says so here: a value the radio
            // has not reported is written as CaptureMeterSet.NoSample, and so
            // is anything derived from it.
            // "n/a" keeps its old meaning — the powers arrived but too little
            // forward power, or two samples too far apart, to work it out.
            //
            // AND ONLY FROM THIS TRANSMISSION OR TUNE (#625, H18). H17 gated
            // each value on "reported since this connection"; a second
            // transmission's first line could then print the first one's
            // readings. Every value below must have arrived in the current
            // evidence window; see beginTxLineWindow. The window is read once,
            // so every field on one line is judged against the same window.
            int window = txLineWindowNow();
            bool fwdOk = Volatile.Read(ref _txLineFwdWindow) == window;
            bool reflOk = Volatile.Read(ref _txLineReflWindow) == window;
            bool swrOk = Volatile.Read(ref _txLineSwrWindow) == window;
            float scMicPeak = float.NaN;
            lock (_txLineScMicLock)
            {
                if (_txLineScMicWindow == window) scMicPeak = _txLineScMicPeak;
            }
            // The last value is the election's, so it and the "via" label name
            // the same copy; it counts only when the copy spoke in this window.
            // A copy the radio withdrew leaves NaN, and the peak goes with it.
            float scMic = float.IsNaN(scMicPeak) ? float.NaN : _scMicElection.ElectedLast;
            if (float.IsNaN(scMic)) scMicPeak = float.NaN;
            float swAlc = Volatile.Read(ref _txLineSwAlcWindow) == window ? _swAlcElection.ElectedLast : float.NaN;
            float fwd = fwdOk ? _PowerDBM : float.NaN;
            float refl = reflOk ? _ReflectedPower : float.NaN;
            float fwdW = fwdOk ? ForwardPowerWatts : float.NaN;
            float reflW = reflOk ? ReflectedPowerWatts : float.NaN;
            float back = ReflectedFraction;
            float swrCalc = ComputedSWR;
            string backText = !(fwdOk && reflOk) ? CaptureMeterSet.NoSample
                : float.IsNaN(back) ? "n/a" : txMeterNumber(back * 100f, "F1") + "%";
            string swrCalcText = !(fwdOk && reflOk) ? CaptureMeterSet.NoSample
                : float.IsNaN(swrCalc) ? "n/a" : txMeterNumber(swrCalc, "F2");
            // The state is ON THE LINE. Two states now feed this one format,
            // and a reader who cannot tell a tune sample from a transmit sample
            // will read a tune's reflected power as a transmit fault. SC_MIC
            // and SWALC are also meaningless during a tune — nobody is talking
            // — and naming the state is what stops them being read as a dead
            // mic. state=tune+tx is both at once: an ATU sweep that keyed Mox.
            //
            // The readings are the ELECTED copy's, not the raw fields: on a
            // radio publishing several SC_MIC copies the raw field is whichever
            // one FlexLib happened to return first, which is the #502 defect
            // this line exists to make visible. "via" names the copy so a
            // floor reading can be told from a floor METER.
            string state = (Transmit && tuning) ? "tune+tx" : (tuning ? "tune" : "tx");
            // A data record with its kind (#625): the file introduces this
            // line where it first appears, in CaptureMeterSet's words, which
            // name exactly the fields formatted here.
            //
            // SC_MIC and SWALC come from the elections RAW — NaN until the
            // elected copy reports — rather than through ScMicDb / SwAlcDb,
            // whose -150 floor is exactly the placeholder this line must not
            // print as a reading; and only when they spoke in this window.
            Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, "txMeters: state=" + state
                + " SC_MIC=" + txMeterField(scMic, "F1")
                + " (peak " + txMeterField(scMicPeak, "F1") + ")"
                + " via " + (_scMicElection.Elected?.Label ?? "no copy has reported")
                + " SWALC=" + txMeterField(swAlc, "F1")
                + " fwd=" + txMeterField(fwd, "F1", " dBm")
                + " refl=" + txMeterField(refl, "F1", " dBm")
                + " fwdW=" + txMeterField(fwdW, "F2")
                + " reflW=" + txMeterField(reflW, "F3")
                + " back=" + backText
                + " SWRraw=" + txMeterField(swrOk ? _SWR : float.NaN, "F2")
                + " SWRcalc=" + swrCalcText,
                TraceLevel.Info);
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
            // TWO destinations, and the second one is the repair.
            //
            // #196-era finding, 2026-08-22: this handler existed, the property
            // existed, and PATEMP appeared in the meter model and in the
            // transmit chain evidence — but the diagnostic capture carried only
            // seven meters and this was not one of them. So an entire bench
            // evening produced no temperature record at all.
            //
            // That mattered the moment unattended keying was authorised
            // (2026-08-22). #192 specifies that automated sweeps abort on
            // RISING PA temperature rather than on an elapsed-time count — a
            // timer assumes the thermal model, the temperature measures it —
            // and a sweep cannot abort on a meter nobody records. While a
            // human was present the gap was theoretical. Unattended, against a
            // load rated 2000 W for ONE MINUTE at tuning duty, it is the
            // actual safety mechanism.
            //
            // THE FIX MADE THEN DID NOT REACH AN ORDINARY CAPTURE, and the
            // explanation written here was wrong about why. It said Verbose
            // lines are dropped at Normal detail — true of the raw lines this
            // replaced, and not the mechanism since. meterTrace is gated on
            // DiagnosticsConfig.RecordMeterStream, an opt-in switch that is OFF
            // by default and independent of the detail level, so temperature
            // was absent even from a Ctrl+J Ctrl+D capture at Verbose. Measured
            // 2026-09-22 against trace-20260907-080956: 258 txMeters lines
            // carrying reflW=, zero paTemp. See CaptureMeterSet.
            meterTrace.Report("paTemp:", data);
            recordCaptureMeters(data);
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
            // A measurement line, so it declares its kind and the file
            // explains it where it first appears (#625, H17). It was a bare
            // TraceLine until Sol's H16 review found it: a real reading, in
            // every detailed capture, that the file never described. The text
            // written is unchanged; only its kind travels with it now.
            Tracing.TraceRecord(SupplyVoltsRecord, "VoltsDataHandler:" + data.ToString(System.Globalization.CultureInfo.InvariantCulture), TraceLevel.Verbose);
            _VoltsData = data;
            // The ordinary capture's window, when supply voltage is recorded
            // without PA temperature (#566; Astra's Track IJK review, blocker
            // 4). A no-op while temperature is selected — see the method.
            recordCaptureMetersFromVolts();
        }

        /// <summary>
        /// What a <c>VoltsDataHandler:</c> line is, in the words of the handler
        /// that writes it. The radio's name for the meter comes from the one
        /// FlexLib subscribes this event to (<c>+13.8A</c>, "before the fuse").
        /// DRAFT for Noel.
        /// </summary>
        internal static readonly TraceRecordKind SupplyVoltsRecord = new TraceRecordKind(
            "VoltsDataHandler",
            "lines that begin 'VoltsDataHandler:' are the radio's supply voltage in volts, from its +13.8A meter,"
            + " which measures before the fuse. There is one line for every reading the radio sent, and they are"
            + " written only when the recording is at its most detailed level.");

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
            int txLineWindow = txLineWindowNow();   // before the value: see forwardPowerData
            meterTrace.Report("reflectedPower:", data);
            // Stamped under the pair lock — see forwardPowerData and
            // ReadTransmitPower (#453).
            lock (_powerPairLock)
            {
                _ReflectedPower = data;
                _reflectedStamp = Stopwatch.GetTimestamp();
            }
            Volatile.Write(ref _txLineReflWindow, txLineWindow);   // after the value: see traceTxMeters
        }

        private float _PAEffData;
        /// <summary>PA efficiency percentage.</summary>
        public float PAEfficiency => _PAEffData;

        private void paEffData(float data)
        {
            // A measurement line: declares its kind like VoltsDataHandler
            // above (#625, H17). Text unchanged.
            Tracing.TraceRecord(PaEfficiencyRecord, "paEffData:" + data.ToString(System.Globalization.CultureInfo.InvariantCulture), TraceLevel.Verbose);
            _PAEffData = data;
        }

        /// <summary>
        /// What a <c>paEffData:</c> line is, in the words of the handler that
        /// writes it. It names the meter and no unit: the radio's meter list
        /// carries the unit, and this handler never reads it. DRAFT for Noel.
        /// </summary>
        internal static readonly TraceRecordKind PaEfficiencyRecord = new TraceRecordKind(
            "paEffData",
            "lines that begin 'paEffData:' are readings of the radio's power amplifier efficiency meter, PAEFF,"
            + " exactly as the radio sent them. There is one line for every reading, and they are written only"
            + " when the recording is at its most detailed level.");

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
                // #835: a key-up is refused while an Audio Check teardown holds
                // admission, here and again when the queued write runs; an
                // unkey never is. See FlexBase.CheckRestore.cs for the boundary.
                if (value && RefusedForCheckTeardown("transmit")) return;
                if (!value) CancelSlicePlayback();
                if (value && TxReferenceArmed) txFilePlayer.RestartOnNextBuffer();
                q.Enqueue((FunctionDel)(() =>
                {
                    if (value && TeardownHeldSilently("transmit")) return;
                    theRadio.Mox = value;
                }), "Mox");
            }
        }

        /// <summary>
        /// Toggle the tune carrier on/off. Puts radio into tune mode (low-power CW carrier).
        /// Sprint 22: Wraps FlexLib's TXTune property for UI/hotkey access.
        /// </summary>
        public bool TxTune
        {
            get => theRadio.TXTune;
            set
            {
                // #835: as Transmit above; turning tune off is never refused.
                if (value && RefusedForCheckTeardown("tune")) return;
                q.Enqueue((FunctionDel)(() =>
                {
                    if (value && TeardownHeldSilently("tune")) return;
                    theRadio.TXTune = value;
                }), "TXTune");
            }
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

        // ── S-meter units: one switch, remembered per radio (#337) ──
        //
        // This was a BARE PUBLIC FIELD until Sprint 38 Track C — written by
        // one handler, read by four places, saved by nothing. Flip it,
        // restart, and it was gone; Noel remembered a toggle that really
        // existed and could not find his way back to it, because nothing kept
        // the answer and nothing but pressing the key would tell him which
        // unit he was in.
        //
        // A property rather than a field so that EVERY route through it
        // persists — the leader chord, the Operations menu and the Settings
        // control all assign this one member, and none of them has to
        // remember to save. The store is the per-radio config, keyed by
        // serial, so the answer follows the station rather than the operator.
        //
        // The read is cached against the serial it was loaded for: SMeter's
        // getter runs at meter rate, and going to disk there would be absurd.
        // A changed serial (a different radio this session) reloads exactly
        // once. Locked because meter data arrives on background threads while
        // the toggle runs on the UI one, and a half-updated cache would hand
        // out one reading in the wrong unit.
        private bool _smeterInDbm;
        private string _smeterInDbmSerial = "never loaded";
        private readonly object _smeterUnitLock = new object();

        /// <summary>
        /// True when this radio's S-meter reads in dBm rather than S-units.
        /// Persisted per radio; assigning it here is what remembers it.
        /// </summary>
        /// <remarks>
        /// With no radio selected there is no config to key on, so the value
        /// lives for the session only. That is the honest answer rather than
        /// writing a preference to a radio nobody named.
        /// </remarks>
        public bool SmeterInDBM
        {
            get
            {
                string serial = SelectedRadioSerial ?? string.Empty;
                lock (_smeterUnitLock)
                {
                    if (!string.Equals(_smeterInDbmSerial, serial, StringComparison.Ordinal))
                    {
                        _smeterInDbm = RadioConfig.SmeterInDbmOf(serial);
                        _smeterInDbmSerial = serial;
                    }
                    return _smeterInDbm;
                }
            }
            set
            {
                string serial = SelectedRadioSerial ?? string.Empty;
                lock (_smeterUnitLock)
                {
                    _smeterInDbm = value;
                    _smeterInDbmSerial = serial;
                }
                RadioConfig.RecordSmeterInDbm(serial, value);
            }
        }

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
        // NO private _SMeter field here, and that absence is load-bearing
        // (#295). FlexBase used to declare one, which SHADOWED
        // AllRadios._SMeter without `new` or `override`. The meter handler
        // wrote the shadow; AllRadios.RawSMeter read the base field, which
        // nothing on a Flex ever wrote; and RawSMeter was therefore
        // permanently zero on every radio this application supports. Writing
        // the base field is what makes one value serve every reader.
        public override int SMeter
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
                    // S-units (dB-over-S9 plus 9 above S9). The arithmetic
                    // lives in SMeterReading.FromDbm — one home, shared with
                    // the QSO signal analyzer, so a recorded dBm and this
                    // live reading can never disagree by a constant.
                    //
                    // The frequency is an argument because the calibration
                    // depends on it: IARU R.1 puts S9 twenty decibels lower
                    // above 30 MHz, so 6 m, 2 m and 70 cm need the other
                    // reference (#296). RXFrequency is the right one to pass —
                    // the meter stream this reads is the ACTIVE slice's, and
                    // RXFrequency tracks that same slice.
                    return SMeterReading.FromDbm(_SMeter, RXFrequency);
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

        /// <summary>
        /// Whether diversity can be offered: two-SCU hardware, two RX antenna
        /// ports, and room for the pair. <b>No licence.</b>
        /// </summary>
        /// <remarks>
        /// <para>Delegates to the capability ladder rather than restating it.
        /// This property used to hand-roll its own three terms, and the two
        /// implementations drifted in both directions at once (#745, #765): it
        /// required a licence the radio does not enforce, while the ladder's
        /// resource rung already defended against the slice number this one
        /// trusted. One gate, one implementation.</para>
        /// </remarks>
        public bool DiversityReady
        {
            get
            {
                if (theRadio == null) return false;
                bool ready = EvaluateCapability(RadioCapability.DiversityReception).Allowed;

                // This area cost a morning at the radio to diagnose, twice,
                // because every surface that could have reported it was reading
                // a value it had written itself. One line per menu build is a
                // cheap standing answer to "what did the gate actually see?".
                Tracing.TraceLine(
                    "diversityGate: "
                    + $"hardware={theRadio.DiversityIsAllowed} "
                    + $"licenceReported={DiversityLicenseReported} "
                    + $"licenceEnabled={DiversityLicensed} (not gated) "
                    + $"antennas={theRadio.RXAntList?.Length ?? 0} "
                    + $"free={TotalMaxSlices - (theRadio.SliceList?.Count ?? 0)} "
                    + $"(maxModel={TotalMaxSlices} live={theRadio.SliceList?.Count ?? -1}) "
                    + $"availableReported={theRadio.AvailableSlices} (not gated) "
                    + $"-> ready={ready}",
                    TraceLevel.Info);
                return ready;
            }
        }

        /// <summary>
        /// Why Enhanced Signal Clarity cannot be offered, or null when it can.
        /// ESC is the LICENSED half of the diversity pair -- a v4 feature, so
        /// SmartSDR+ -- and it has its own ladder on purpose. See the comment
        /// on the Esc row in <c>CapabilityTable</c> before merging it back into
        /// diversity's.
        /// </summary>
        public string EscGateMessage
            => EvaluateCapability(RadioCapability.Esc, CapabilityWording.Long).Message;

        /// <summary>Whether ESC can be offered: diversity's hardware and
        /// resources, PLUS the div_esc licence.</summary>
        public bool EscReady => EvaluateCapability(RadioCapability.Esc).Allowed;

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
        public bool ToggleDiversity()
        {
            bool target = !DiversityOn;
            TraceDiversityPair("before", target);
            DiversityOn = target;

            // Sampled off the UI thread and after a beat, because the write
            // goes through the command queue and the radio's reply arrives
            // asynchronously. 1500 ms is long enough for a LAN round trip with
            // room to spare; a WAN radio may need more.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                TraceDiversityPair("after", target);
            });

            // The setter above enqueues the radio write. Return the requested
            // state so immediate feedback never reads the old state back.
            return target;
        }

        /// <summary>
        /// Trace the slice-pair state the RADIO owns, which is the only
        /// evidence available that a diversity command was ACCEPTED.
        /// </summary>
        /// <remarks>
        /// <para>Do not be tempted to read <see cref="DiversityOn"/> for this.
        /// FlexLib's slice setter writes its cache BEFORE it sends the command,
        /// and its status parser for <c>diversity</c> returns early when the
        /// radio's report matches the cache -- so a confirmation raises no
        /// event, a refusal raises no event, and the property afterwards holds
        /// whatever we wrote in either case. Reading it back measures our own
        /// write. Immediate feedback must therefore use the target returned by
        /// <see cref="ToggleDiversity"/>, not read this cache back.</para>
        /// <para><c>diversity_child</c> and <c>diversity_index</c> are
        /// different in kind: nothing on this side ever writes them, and the
        /// radio populates them only when it really has paired two slices. A
        /// change there cannot be manufactured by our own command, which is
        /// the entire reason this method reads them instead.</para>
        /// </remarks>
        private void TraceDiversityPair(string phase, bool target)
        {
            try
            {
                var r = theRadio;
                if (r == null)
                {
                    Tracing.TraceLine($"diversityPair[{phase}]: no radio", TraceLevel.Info);
                    return;
                }

                var parts = new List<string>();
                foreach (Slice s in r.SliceList)
                {
                    string partner = s.DiversitySlicePartner == null
                        ? "none"
                        : s.DiversitySlicePartner.Index.ToString();
                    parts.Add($"slice{s.Index}[on={s.DiversityOn} child={s.DiversityChild} "
                            + $"idx={s.DiversityIndex} partner={partner}]");
                }

                Tracing.TraceLine(
                    $"diversityPair[{phase}]: target={target} hardware={r.DiversityIsAllowed} "
                    + $"licenceReported={DiversityLicenseReported} licenceEnabled={DiversityLicensed} "
                    + $"sliceCount={r.SliceList.Count} " + string.Join(" ", parts),
                    TraceLevel.Info);
            }
            catch (Exception ex)
            {
                // The slice list can change under us; a failed reading is
                // reported rather than swallowed, so an empty result is never
                // mistaken for a refusal.
                Tracing.TraceLine($"diversityPair[{phase}]: read failed: {ex.Message}", TraceLevel.Warning);
            }
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

        /// <summary>
        /// Why diversity cannot be used, or empty when it can.
        /// </summary>
        /// <remarks>
        /// <b>This was the template and is now a reader of the thing it taught.</b>
        /// Its ladder - connection, operator context, hardware, licence not
        /// reported, licence disabled, then operator-fixable resources - is the
        /// order <see cref="CapabilityBlock"/> encodes for every capability, and
        /// the wording below moved into the descriptor table unchanged. Keeping
        /// this property is deliberate: it is read from several places and is
        /// the name people look for.
        /// </remarks>
        public string DiversityGateMessage
            => EvaluateCapability(RadioCapability.DiversityReception, CapabilityWording.Long).Message;

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

        // #266. Both setters below write the cache before the radio has seen
        // the value, so both need a guard against the radio's own in-flight
        // report of what the frequency WAS. See FrequencyEchoGuard.
        private readonly FrequencyEchoGuard _rxFreqEcho = new FrequencyEchoGuard("RX");
        private readonly FrequencyEchoGuard _txFreqEcho = new FrequencyEchoGuard("TX");

        private ulong _RXFrequency;
        public ulong RXFrequency
        {
            get
            {
                return _RXFrequency;
            }
            set
            {
                ulong replaced = _RXFrequency;
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
                // Arm HERE, not at the top of the setter: the early returns
                // above leave without writing anything to the radio, and a
                // guard armed for a write that never went out would reject the
                // radio's perfectly truthful report of where it still is.
                _rxFreqEcho.Requested(replaced, value);
                q.Enqueue((FunctionDel)(() =>
                {
                    var s = VFOToSlice(RXVFO);
                    if (s != null) s.Freq = freq;
                }), "RXFreq");
            }
        }

        /// <summary>
        /// What the RADIO says its transmit slice is on, read from the slice
        /// itself rather than from our cached echo. Zero when there is no
        /// transmit slice to ask — and zero is an honest answer, not a failure.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><see cref="TXFrequency"/> is optimistic and this is not.</b> The
        /// setter records the requested value immediately so the UI stays
        /// responsive, and the radio's echo overwrites it later if the change
        /// lands. If the radio never applies the write — acknowledged with
        /// error code 0 and ignored, which #164 measured — no echo arrives and
        /// the cached request simply stays there, indistinguishable from
        /// success.
        /// </para>
        /// <para>
        /// <b>So anything CONFIRMING a change to an operator must read this,
        /// not that.</b> Found by the Sprint 42 integration pass: the Fixer's
        /// post-change line said "the radio now reports X" and was reading back
        /// our own write, in the one place added specifically to be honest
        /// about #164. Reporting a frequency that did not take is worse than
        /// reporting nothing, because the operator then keys believing they
        /// have moved.
        /// </para>
        /// </remarks>
        public ulong TXFrequencyAsReported
        {
            get
            {
                try
                {
                    if (!ValidVFO(TXVFO)) return 0UL;
                    var s = VFOToSlice(TXVFO);
                    if (s == null) return 0UL;
                    return LibFreqtoLong(s.Freq);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("TXFrequencyAsReported: " + ex.Message, TraceLevel.Warning);
                    return 0UL;
                }
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
                ulong replaced = _TXFrequency;
                _TXFrequency = value;

                // A REFUSED WRITE MUST NOT LEAVE ITS VALUE BEHIND. Both bails
                // below used to return with _TXFrequency holding a frequency
                // this radio was never asked for, and the getter then handed it
                // back to anyone who asked "what is the radio on?" — including
                // the Fixer's post-change confirmation, whose whole job is to
                // report radio truth rather than our request (#164). The
                // operator was told a frequency change applied, and could key
                // believing they had moved.
                if (!ValidVFO(TXVFO))
                {
                    _TXFrequency = replaced;
                    Tracing.TraceLine("TXFrequency: no valid TX slice, so the requested "
                                      + value + " was NOT applied and was not kept",
                                      TraceLevel.Warning);
                    return;
                }
                var slice = VFOToSlice(TXVFO);
                if (slice == null)
                {
                    _TXFrequency = replaced;
                    Tracing.TraceLine("TXFrequency: TX slice missing, so the requested "
                                      + value + " was NOT applied and was not kept",
                                      TraceLevel.Warning);
                    return;
                }
                double freq = LongFreqToLibFreq(value);
                _txFreqEcho.Requested(replaced, value);
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
                Slice s = RXModeTarget();
                if (s != null) q.Enqueue((FunctionDel)(() => { s.DemodMode = value; }), "RXDemodMode");
            }
        }

        /// <summary>
        /// The slice an RX mode write lands on: our RX VFO's slice, else the
        /// active slice if it is ours, else none. Shared by <see cref="RXMode"/>
        /// and <see cref="RequestMode"/> so the two can never disagree about
        /// which slice they mean (#312).
        /// </summary>
        private Slice RXModeTarget()
        {
            Slice s = VFOToSlice(RXVFO);
            if (s == null)
            {
                Slice act = theRadio?.ActiveSlice;
                if ((act != null) && myClient(act.ClientHandle)) s = act;
            }
            return s;
        }

        /// <summary>
        /// Ask for an RX mode change and be told whether the request went out
        /// (#312, repair round one). <see cref="Mode"/>'s setter returns
        /// silently while transmitting and <see cref="RXMode"/>'s silently when
        /// there is no slice to write — correct for their callers, and
        /// unchanged, but a caller that has to SAY the mode changed cannot tell
        /// from them whether anything happened.
        /// </summary>
        /// <remarks>
        /// <para><b>Sent is not applied.</b> It means the write was queued to
        /// the slice, and nothing more. Reading the slice's DemodMode back does
        /// not prove more: FlexLib's setter writes that field before the
        /// command goes out, so a read-back is our own echo (Codex, #312 repair
        /// round one). Say "switching", never "set" (#164).</para>
        /// </remarks>
        public ModeRequestOutcome RequestMode(string value)
        {
            if (Transmit)
            {
                Tracing.TraceLine("RequestMode: transmitting, so \"" + value + "\" was NOT requested", TraceLevel.Warning);
                return ModeRequestOutcome.RefusedTransmitting;
            }
            Slice s = RXModeTarget();
            if (s == null)
            {
                Tracing.TraceLine("RequestMode: no slice of ours to write, so \"" + value + "\" was NOT requested", TraceLevel.Warning);
                return ModeRequestOutcome.RefusedNoSlice;
            }
            if (q == null || !q.MainLoop)
            {
                // q.Enqueue would only trace "outside main loop" and drop it.
                Tracing.TraceLine("RequestMode: the radio's command loop is not running, so \"" + value + "\" was NOT requested", TraceLevel.Warning);
                return ModeRequestOutcome.RefusedNotRunning;
            }
            q.Enqueue((FunctionDel)(() => { s.DemodMode = value; }), "RXDemodMode");
            return ModeRequestOutcome.Sent;
        }

        /// <summary>
        /// The transmit mode the radio ITSELF reports right now — the TX
        /// slice's live demod mode, not our cache and never our request.
        /// Empty when there is no TX slice to ask.
        /// </summary>
        /// <remarks>
        /// The mode sibling of <see cref="TXFrequencyAsReported"/>, for the
        /// same caller and the same reason (#164, #411): a mode write is
        /// enqueued and the radio has been observed acknowledging transmit
        /// writes it never applied, so anything that confirms a change to the
        /// operator must read THIS, poll briefly, and say plainly when the
        /// radio has not agreed. <see cref="TXMode"/>'s cache is echo-only —
        /// the setter never touches it — but it can sit stale after slice
        /// churn, and stale is exactly what a confirmation must not read.
        /// </remarks>
        public string TXModeAsReported
        {
            get
            {
                try
                {
                    if (!ValidVFO(TXVFO)) return "";
                    var s = VFOToSlice(TXVFO);
                    if (s == null) return "";
                    return s.DemodMode ?? "";
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("TXModeAsReported: " + ex.Message, TraceLevel.Warning);
                    return "";
                }
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
                else
                {
                    // Traced rather than silently dropped, the TXFrequency
                    // lesson one property over: a caller that trusts a silent
                    // setter will tell the operator a change happened.
                    Tracing.TraceLine("TXMode: TX slice missing, so the requested \""
                                      + value + "\" was NOT applied", TraceLevel.Warning);
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
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("FilterLow");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterLow = value; }), "FilterLow");
                }
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
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("FilterHigh");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterHigh = value; }), "FilterHigh");
                }
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
            if (HasActiveSlice)
            {
                NoteOperatorChangedRadioSetting("SetFilter");
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.UpdateFilter(low, high); }), "Filter");
            }
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
                if (r.Callsign == value) return;
                if (GuardRefuses("settings.guard.action.callsign")) return;
                r.Callsign = value;
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
                    if (r.Screensaver == mode) return;
                    if (GuardRefuses("settings.guard.action.front_panel")) return;
                    r.Screensaver = mode;
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
            var verdict = EvaluateCapability(
                RadioCapability.DiversityReception, CapabilityWording.Long);
            string status = verdict.Allowed
                ? (DiversityOn ? "enabled" : "disabled")
                : "unavailable";
            string reason = verdict.Allowed && !DiversityOn
                ? "diversity ready"
                : verdict.Message;

            return BuildFeatureLine("Diversity", status, reason);
        }

        private string BuildEscStatus(Radio radio)
        {
            string status;
            string reason = string.Empty;
            var verdict = EvaluateCapability(RadioCapability.Esc, CapabilityWording.Long);

            if (verdict.Block == CapabilityBlock.LicenceNotReported)
            {
                status = "pending";
                reason = verdict.Message;
            }
            else if (verdict.Block == CapabilityBlock.LicenceDisabled)
            {
                status = LicenceDisabledStatusPendingNoel;
                reason = verdict.Message;
            }
            else if (!verdict.Allowed)
            {
                status = "unavailable";
                reason = verdict.Message;
            }
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

            // NRF, NRS and RNN share ONE hardware requirement — 8000-series or Aurora DSP —
            // so they share one predicate. They did not until 2026-10-17: RNN tested a
            // second, stricter predicate that excluded the AU-510, while NRF and NRS
            // claimed no hardware requirement at all and so read as available on every
            // 6000-series radio that holds a licence. Three answers, one requirement (#729).
            // NRL below is correctly unconditional — it runs on the 6000s too.
            bool advancedNrSupported = NeuralNRHardwareSupported;
            string advancedNrReason = advancedNrSupported ? "" : "requires 8000-series radio";

            lines.Add(BuildAdvancedNoiseStatus("Noise Reduction (RNN)",
                NeuralNoiseReduction == OffOnValues.on,
                advancedNrSupported, advancedNrReason,
                hasSlice, nrModeAllowed, "not available in CW or FM modes",
                licenseFeature, licenseReported, licenseEnabled));

            lines.Add(BuildAdvancedNoiseStatus("Noise Reduction (NRF)",
                NoiseReductionFilter == OffOnValues.on,
                advancedNrSupported, advancedNrReason,
                hasSlice, nrModeAllowed, "not available in CW or FM modes",
                licenseFeature, licenseReported, licenseEnabled));

            lines.Add(BuildAdvancedNoiseStatus("Noise Reduction (NRS)",
                SpectralNoiseReduction == OffOnValues.on,
                advancedNrSupported, advancedNrReason,
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

        // PLACEHOLDER #720/#752: Noel has not reviewed the Feature Availability state
        // word yet. Keep today's rendering until he supplies its replacement;
        // the appended reason already has the tier-aware seam.
        private const string LicenceDisabledStatusPendingNoel = "unsubscribed";

        private string BuildAdvancedNoiseStatus(string feature, bool enabled,
            bool modelSupported, string modelReason,
            bool hasSlice, bool modeAllowed, string modeReason,
            Feature licenseFeature, bool licenseReported, bool licenseEnabled)
        {
            if (!modelSupported) return BuildFeatureLine(feature, "unavailable", modelReason);
            if (!licenseReported) return BuildFeatureLine(feature, "pending", "license status pending");
            if (!licenseEnabled) return BuildFeatureLine(feature, LicenceDisabledStatusPendingNoel,
                LicenceDisabledMessage(null, licenseFeature, CapabilityWording.Long,
                    feature, "license disabled"));
            if (!hasSlice) return BuildFeatureLine(feature, "unavailable", "select a slice");
            if (!modeAllowed) return BuildFeatureLine(feature, "unavailable", modeReason);
            return BuildFeatureLine(feature, enabled ? "enabled" : "disabled", enabled ? "" : "available");
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

        // Public since Sprint 44 Track N: the audio layer's slice-volume
        // target (JJFlexWpf) reads its rails from here rather than repeating
        // the numbers (#524).
        public const int AudioGainMinValue = 0;
        public const int AudioGainMaxValue = 100;
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
                if (val && (TxToneEngaged || TxReferenceArmed))
                {
                    ScreenReaderOutput.Speak(TxReferenceArmed
                        ? "VOX disabled when reference is enabled."
                        : "VOX disabled when tone is enabled.", VerbosityLevel.Critical, interrupt: true);
                    return;
                }
                if (s != null)
                {
                    NoteOperatorChangedRadioSetting("Vox");
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
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("NoiseBlanker");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBOn = (value == OffOnValues.on) ? true : false; }), "NBOn");
                }
            }
        }

        // The values are the same for the wide band NB.
        internal const int NoiseBlankerValueMin = 0;
        internal const int NoiseBlankerValueMax = 100;
        internal const int NoiseBlankerValueIncrement = 5;
        public int NoiseBlankerLevel
        {
            get { return theRadio?.ActiveSlice?.NBLevel ?? 0; }
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("NoiseBlankerLevel"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBLevel = value; }), "NBLevel"); } }
        }

        public OffOnValues WidebandNoiseBlanker
        {
            get
            {
                return (theRadio?.ActiveSlice?.WNBOn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("WidebandNoiseBlanker");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBOn = (value == OffOnValues.on) ? true : false; }), "WNBOn");
                }
            }
        }

        public int WidebandNoiseBlankerLevel
        {
            get { return theRadio?.ActiveSlice?.WNBLevel ?? 0; }
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("WidebandNoiseBlankerLevel"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBLevel = value; }), "WNBLevel"); } }
        }

        public OffOnValues NoiseReduction
        {
            get
            {
                return (theRadio?.ActiveSlice?.NROn == true) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("NoiseReduction");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NROn = (value == OffOnValues.on) ? true : false; }));
                }
            }
        }

        internal const int NoiseReductionValueMin = 0;
        internal const int NoiseReductionValueMax = 100;
        internal const int NoiseReductionValueIncrement = 5;
        internal int NoiseReductionLevel
        {
            get { return theRadio?.ActiveSlice?.NRLevel ?? 0; }
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("NoiseReductionLevel"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRLevel = value; })); } }
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
                    NoteOperatorChangedRadioSetting("NoiseReductionLegacy");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRLOn = (value == OffOnValues.on); }), "NRLOn");
                }
            }
        }
        public int NoiseReductionLegacyLevel
        {
            get { return theRadio?.ActiveSlice?.NRL_Level ?? 0; }
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("NoiseReductionLegacyLevel"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRL_Level = value; }), "NRL_Level"); } }
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
                if (RefuseCapabilityWrite(RadioCapability.NoiseReductionSpectral)) return;
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    NoteOperatorChangedRadioSetting("SpectralNoiseReduction");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRSOn = (value == OffOnValues.on); }), "NRSOn");
                }
            }
        }
        internal int SpectralNoiseReductionLevel
        {
            get { return theRadio?.ActiveSlice?.NRSLevel ?? 0; }
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("SpectralNoiseReductionLevel"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRSLevel = value; }), "NRSLevel"); } }
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
                if (RefuseCapabilityWrite(RadioCapability.NoiseReductionFilter)) return;
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    NoteOperatorChangedRadioSetting("NoiseReductionFilter");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRFOn = (value == OffOnValues.on); }), "NRFOn");
                }
            }
        }
        internal int NoiseReductionFilterLevel
        {
            get { return theRadio?.ActiveSlice?.NRFLevel ?? 0; }
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("NoiseReductionFilterLevel"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRFLevel = value; }), "NRFLevel"); } }
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
                if (RefuseCapabilityWrite(RadioCapability.NoiseReductionNeural)) return;
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    NoteOperatorChangedRadioSetting("NeuralNoiseReduction");
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
                    NoteOperatorChangedRadioSetting("AutoNotchFFT");
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
                    NoteOperatorChangedRadioSetting("AutoNotchLegacy");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFLOn = (value == OffOnValues.on); }), "ANFLOn");
                }
            }
        }
        internal int AutoNotchLegacyLevel
        {
            get { return theRadio?.ActiveSlice?.ANFL_Level ?? 0; }
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("AutoNotchLegacyLevel"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFL_Level = value; }), "ANFL_Level"); } }
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
                    NoteOperatorChangedRadioSetting("AGCSpeed");
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
            set { if (HasActiveSlice) { NoteOperatorChangedRadioSetting("AGCThreshold"); q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AGCThreshold = value; })); } }
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
                // _XIT set in PropertyChangedHandler.
                // The transmit offset is a transmit-chain setting, so it goes
                // to the transmit slice (#496 class): XIT on a slice that is
                // not transmitting moves nothing on the air. Captured at set
                // time, like the antenna, so the queued command cannot retarget.
                Slice s = SliceForTransmitSetting(out string basis);
                if (s == null) return;
                Tracing.TraceLine($"XIT request slice {s.Index} ({basis}): on={value.Active} offset={value.Value}",
                                  TraceLevel.Info);
                lock (_XIT)
                {
                    q.Enqueue((FunctionDel)(() => { s.XITFreq = value.Value; }), "XITFreq");
                    q.Enqueue((FunctionDel)(() => { s.XITOn = value.Active; }), "XITOn");
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
            set { NoteOperatorChangedRadioSetting("MicGain"); q.Enqueue((FunctionDel)(() => { theRadio.MicLevel = value; })); }
        }

        public OffOnValues ProcessorOn
        {
            get { return (theRadio?.SpeechProcessorEnable == true) ? OffOnValues.on : OffOnValues.off; }
            set { NoteOperatorChangedRadioSetting("ProcessorOn"); q.Enqueue((FunctionDel)(() => { theRadio.SpeechProcessorEnable = (value == OffOnValues.on) ? true : false; })); }
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
            set { NoteOperatorChangedRadioSetting("ProcessorSetting"); q.Enqueue((FunctionDel)(() => { theRadio.SpeechProcessorLevel = (uint)value; })); }
        }

        public OffOnValues Compander
        {
            get { return (theRadio?.CompanderOn == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                NoteOperatorChangedRadioSetting("Compander");
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
                NoteOperatorChangedRadioSetting("CompanderLevel");
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
                NoteOperatorChangedRadioSetting("TXFilterLow");
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
                NoteOperatorChangedRadioSetting("TXFilterHigh");
                q.Enqueue((FunctionDel)(() => { theRadio.TXFilterHigh = value; }));
            }
        }

        public OffOnValues MicBoost
        {
            get { return (theRadio?.MicBoost == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                NoteOperatorChangedRadioSetting("MicBoost");
                q.Enqueue((FunctionDel)(() => { theRadio.MicBoost = val; }));
            }
        }

        public OffOnValues MicBias
        {
            get { return (theRadio?.MicBias == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                NoteOperatorChangedRadioSetting("MicBias");
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

        // ── Equalizers, TX and RX (Track F 2026-08-16; RX and 32 Hz added
        //    Sprint 43 Track C, 2026-09-01, #431/#457) ──
        //
        // The TX EQ was the one piece of the radio's TX audio chain this class
        // never wrapped, which is why audio presets shipped without it (#50 —
        // "exported presets may be missing the TX EQ"; they were). FlexLib's
        // Equalizer object is created on demand and populated by an "eq txsc
        // info" round trip, so callers who want a capture should call
        // RequestTxEqualizer() early (the Audio Workshop does it when it gets
        // a rig) and treat a null GetTxEq() as "the radio has not answered
        // yet", not as "the radio has no EQ". All of that is equally true of
        // the RX side, which the radio carries identically and this class did
        // not wrap at all until now.
        //
        // NINE bands, not eight — ruled by Noel 2026-09-01. The reasoning is
        // not that 32 Hz is useful for voice (on transmit it sits below what
        // an SSB filter passes): it is that "the surface covers the radio" is
        // a rule anyone can verify, and "we left one out on purpose" is a
        // decision nobody remembers in six months.
        //
        // WHAT THIS REGION USED TO CLAIM, AND WHY IT IS NOT REPEATED HERE.
        // It said the radio "does not honor" 32 Hz, citing FlexLib. That
        // citation is real — Equalizer.cs's level_32Hz setter carries
        // "Currently 32Hz is not a valid EQ Control in the radio" — but it is
        // a FlexRadio comment in a file stamped 2012-2017, and the same file
        // both SENDS "eq <sel>sc 32Hz=" and PARSES a "32Hz" key back out of
        // the radio's own status reply. A parser is written for a key
        // something sends. So the vendor comment and the vendor code
        // disagree, and neither is a measurement.
        //
        // UNVERIFIED, DELIBERATELY: nobody here has watched a radio answer.
        // Track C had no authorised radio. If a Flex silently drops the
        // 32 Hz write, this wrapper reports the value we sent rather than
        // the value the radio kept — FlexLib updates its own field
        // optimistically in the setter, so a readback cannot tell us. The
        // honest test is one line on a bench: set 32 Hz away from zero,
        // call RequestTxEqualizer(), and see whether the status reply comes
        // back with what was sent. Until someone does that, treat the bottom
        // band as "sent, not confirmed".

        /// <summary>
        /// The nine equalizer bands the radio carries — 32 Hz through 8 kHz,
        /// each in dB over the radio's ±10 range — plus whether the equalizer
        /// is switched on. Shared shape; the TX and RX snapshots below are
        /// distinct types so an RX curve cannot be handed to the TX apply by
        /// accident.
        /// </summary>
        public abstract class EqSettings
        {
            public bool Enabled;
            public int Hz32;
            public int Hz63;
            public int Hz125;
            public int Hz250;
            public int Hz500;
            public int Hz1000;
            public int Hz2000;
            public int Hz4000;
            public int Hz8000;
        }

        /// <summary>A snapshot of the radio's TRANSMIT equalizer.</summary>
        public sealed class TxEqSettings : EqSettings
        {
        }

        /// <summary>A snapshot of the radio's RECEIVE equalizer.</summary>
        public sealed class RxEqSettings : EqSettings
        {
        }

        /// <summary>
        /// The nine band centre frequencies in Hz, in the order every EQ
        /// surface presents them — lowest first. One list so a dialog, a
        /// preset and a test cannot drift into disagreeing about what the
        /// bands are or how many there are.
        /// </summary>
        public static readonly int[] EqBandHz =
            { 32, 63, 125, 250, 500, 1000, 2000, 4000, 8000 };

        /// <summary>Lowest level the radio accepts on any band, in dB.</summary>
        public const int EqLevelMin = -10;

        /// <summary>Highest level the radio accepts on any band, in dB.</summary>
        public const int EqLevelMax = 10;

        /// <summary>
        /// Read a band by its index into <see cref="EqBandHz"/>. Out-of-range
        /// indexes read 0 rather than throwing — a UI asking for a band that
        /// does not exist is a bug to find in a test, not a crash to hand an
        /// operator mid-QSO.
        /// </summary>
        public static int GetEqBand(EqSettings s, int index)
        {
            if (s == null) return 0;
            switch (index)
            {
                case 0: return s.Hz32;
                case 1: return s.Hz63;
                case 2: return s.Hz125;
                case 3: return s.Hz250;
                case 4: return s.Hz500;
                case 5: return s.Hz1000;
                case 6: return s.Hz2000;
                case 7: return s.Hz4000;
                case 8: return s.Hz8000;
                default: return 0;
            }
        }

        /// <summary>
        /// Write a band by its index into <see cref="EqBandHz"/>, clamped to
        /// the radio's range. Out-of-range indexes are ignored.
        /// </summary>
        public static void SetEqBand(EqSettings s, int index, int level)
        {
            if (s == null) return;
            int v = Math.Max(EqLevelMin, Math.Min(EqLevelMax, level));
            switch (index)
            {
                case 0: s.Hz32 = v; break;
                case 1: s.Hz63 = v; break;
                case 2: s.Hz125 = v; break;
                case 3: s.Hz250 = v; break;
                case 4: s.Hz500 = v; break;
                case 5: s.Hz1000 = v; break;
                case 6: s.Hz2000 = v; break;
                case 7: s.Hz4000 = v; break;
                case 8: s.Hz8000 = v; break;
            }
        }

        // ── The one place the FlexLib mapping is written ──
        // Every band name appears exactly twice below, once each way. TX and
        // RX both go through here, so a band cannot be wired on one side and
        // forgotten on the other — which is the shape the 32 Hz gap had.

        private static void FillEqFrom(Equalizer eq, EqSettings s)
        {
            s.Enabled = eq.EQ_enabled;
            s.Hz32 = eq.level_32Hz;
            s.Hz63 = eq.level_63Hz;
            s.Hz125 = eq.level_125Hz;
            s.Hz250 = eq.level_250Hz;
            s.Hz500 = eq.level_500Hz;
            s.Hz1000 = eq.level_1000Hz;
            s.Hz2000 = eq.level_2000Hz;
            s.Hz4000 = eq.level_4000Hz;
            s.Hz8000 = eq.level_8000Hz;
        }

        private static void PushEqTo(Equalizer eq, EqSettings s)
        {
            static int Clamp(int v) => Math.Max(EqLevelMin, Math.Min(EqLevelMax, v));
            eq.level_32Hz = Clamp(s.Hz32);
            eq.level_63Hz = Clamp(s.Hz63);
            eq.level_125Hz = Clamp(s.Hz125);
            eq.level_250Hz = Clamp(s.Hz250);
            eq.level_500Hz = Clamp(s.Hz500);
            eq.level_1000Hz = Clamp(s.Hz1000);
            eq.level_2000Hz = Clamp(s.Hz2000);
            eq.level_4000Hz = Clamp(s.Hz4000);
            eq.level_8000Hz = Clamp(s.Hz8000);
            eq.EQ_enabled = s.Enabled;
        }

        private void RequestEqualizer(EqualizerSelect which, string traceName)
        {
            q.Enqueue((FunctionDel)(() =>
            {
                if (theRadio == null) return;
                var eq = theRadio.FindEqualizerByEQSelect(which);
                if (eq == null)
                {
                    // CreateEqualizer hands back an unregistered object; the
                    // RequestEqualizerFromRadio round trip is what registers
                    // it (and fills its levels) once the radio replies.
                    theRadio.CreateEqualizer(which)?.RequestEqualizerFromRadio();
                }
                else
                {
                    eq.RequestEqualizerInfo();
                }
            }), traceName);
        }

        private T GetEqualizerSnapshot<T>(EqualizerSelect which) where T : EqSettings, new()
        {
            var eq = theRadio?.FindEqualizerByEQSelect(which);
            if (eq == null) return null;
            var s = new T();
            FillEqFrom(eq, s);
            return s;
        }

        private bool ApplyEqualizerSnapshot(EqualizerSelect which, EqSettings s, string traceName)
        {
            if (s == null) return false;
            if (theRadio?.FindEqualizerByEQSelect(which) == null) return false;
            q.Enqueue((FunctionDel)(() =>
            {
                var eq = theRadio?.FindEqualizerByEQSelect(which);
                if (eq == null) return;
                PushEqTo(eq, s);
            }), traceName);
            return true;
        }

        /// <summary>
        /// Ask the radio for its TX equalizer state. Idempotent; safe with no
        /// radio. Until the answer arrives, <see cref="GetTxEq"/> returns null.
        /// </summary>
        public void RequestTxEqualizer()
        {
            RequestEqualizer(EqualizerSelect.TX, "RequestTxEqualizer");
        }

        /// <summary>
        /// Current TX equalizer state, or null when the radio has not
        /// reported it yet (see <see cref="RequestTxEqualizer"/>).
        /// </summary>
        public TxEqSettings GetTxEq()
        {
            return GetEqualizerSnapshot<TxEqSettings>(EqualizerSelect.TX);
        }

        /// <summary>
        /// Apply a TX equalizer snapshot. Returns false (and changes nothing)
        /// when the radio's equalizer object is not available yet — callers
        /// own saying so rather than pretending the EQ was set.
        /// </summary>
        public bool ApplyTxEq(TxEqSettings s)
        {
            return ApplyEqualizerSnapshot(EqualizerSelect.TX, s, "ApplyTxEq");
        }

        /// <summary>
        /// Ask the radio for its RX equalizer state. Idempotent; safe with no
        /// radio. Until the answer arrives, <see cref="GetRxEq"/> returns null.
        /// </summary>
        public void RequestRxEqualizer()
        {
            RequestEqualizer(EqualizerSelect.RX, "RequestRxEqualizer");
        }

        /// <summary>
        /// Current RX equalizer state, or null when the radio has not
        /// reported it yet (see <see cref="RequestRxEqualizer"/>).
        /// </summary>
        public RxEqSettings GetRxEq()
        {
            return GetEqualizerSnapshot<RxEqSettings>(EqualizerSelect.RX);
        }

        /// <summary>
        /// Apply an RX equalizer snapshot. Returns false (and changes nothing)
        /// when the radio's equalizer object is not available yet — callers
        /// own saying so rather than pretending the EQ was set.
        /// </summary>
        public bool ApplyRxEq(RxEqSettings s)
        {
            return ApplyEqualizerSnapshot(EqualizerSelect.RX, s, "ApplyRxEq");
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
            get { return theRadio == null ? false : FullDuplexState(theRadio, theRadio.CurrentCommandConnection); }
            set { _ = SetFullDuplexFromPropertyAsync(value); }
        }

        // SliceRecordOn, SlicePlayOn and SlicePlayEnabled lived here until
        // Tape Deck track 2 (#787). They drove the radio's slice recorder with
        // no reply check and no refusal while transmitting, beside a second,
        // reply-checked path that did both. The one recorder surface is now
        // FlexBase.SliceRecorder.cs, which carries their remarks (the 120-second
        // ring-like buffer, the re-arm race); do not bring a setter back.

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
        // The 8600 same-port listener returned exact zeros during transmit.
        // The controlled Loopback Check therefore sends on one XVT input and
        // listens on another at the same frequency/mode, with other owned
        // receive slices muted, 1 watt, RF gain 32 and TX monitor off. Noel's
        // keyed bench must still verify this corrected arrangement. Adjacent
        // port coupling can overload the receiver; this is a rough chain check.

        private bool _loopbackArranged;
        private bool _lbSavedFdx;
        private string _lbSavedTxAnt = "";
        private bool _lbSavedMonitor;
        private int _lbSavedPower;
        private LoopbackSliceResources _lbResources;
        private Xvtr _lbDriveBand;
        private double _lbSavedDriveDbm;
        private List<(Slice Slice, bool Muted)> _lbSavedMutes;
        private Slice _lbSavedActive;

        private string LoopbackOtherReceivePort(string transmitAntenna)
        {
            // The transmit pan is already at the frequency/mode the new ears
            // pan will use. Its ant_list status is the same contextual receive
            // list the off-air listen-back reads from its listening pan.
            try { return OtherTransverterReceiveInput(SliceForTransmitSetting()?.Panadapter?.RXAntennaList, transmitAntenna); }
            catch (NullReferenceException) { return null; } // vendor getter clones an unset list
        }

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
                string transmitAntenna = TXAntennaList.Find(a => a.StartsWith("XVT", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(LoopbackOtherReceivePort(transmitAntenna)))
                    return Lexicon.Get("audio.check.loopback_no_other_port");
                if (theRadio.SliceList.Count >= TotalMaxSlices)
                    return "No free slice for the listening receiver";
                return "";
            }
        }

        /// <summary>
        private Radio _lbArrangementRadio;
        private CommandConnection _lbArrangementConnection;
        private int _lbArrangementStarting;
        private int _lbArrangementEnding;
        private string _lbSetupFailure;

        /// Apply the verified loopback recipe: snapshot full-duplex flag, TX
        /// antenna, monitor state, RF power and slice roster, then set FDX
        /// on, TX antenna to the first XVT port, 1 watt (power 0 is silent —
        /// verified), monitor OFF, and create the ears slice on the other
        /// receive XVT port at the same frequency/mode. All radio writes ride
        /// the command queue and setup waits for replies before keying. Returns false
        /// when unsupported or when the radio cannot confirm its state.
        /// </summary>
        public Task<bool> StartLoopbackArrangementAsync() =>
            ArrangeLoopbackAsync(null, earsAudible: true);

        /// <param name="earsAudible">True for the Loopback Check, whose whole
        /// point is hearing the listening slice. The radio creates that slice
        /// carrying the mute of the slice it copies, and the arrangement has
        /// just muted every owned slice, so on the 2026-10-25 bench the ears
        /// arrived muted and the check was silent until Noel unmuted slice B by
        /// hand. The off-air take passes false: it decides the ears' mute
        /// itself ("Hear yourself during the take", off by default).</param>
        private async Task<bool> ArrangeLoopbackAsync(bool? confirmedPriorFullDuplex, bool earsAudible)
        {
            if (_loopbackArranged) return true;
            if (System.Threading.Volatile.Read(ref _lbArrangementEnding) != 0) return false;
            if (System.Threading.Interlocked.Exchange(ref _lbArrangementStarting, 1) != 0) return false;
            _lbSetupFailure = null;
            bool setupChanged = false;
            Radio arrangementRadio = null;
            CommandConnection arrangementConnection = null;
            try
            {
            if (theRadio == null || !HasActiveSlice || !LoopbackSupported) return false;
            if (theRadio.SliceList.Count >= TotalMaxSlices)
            {
                Tracing.TraceLine("StartLoopbackArrangement: no free slice slot", TraceLevel.Warning);
                return false;
            }

            string xvt = TXAntennaList.Find(a => a.StartsWith("XVT", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(xvt)) return false;
            string earsAntenna = LoopbackOtherReceivePort(xvt);
            if (string.IsNullOrEmpty(earsAntenna))
            {
                Tracing.TraceLine("StartLoopbackArrangement: no second transverter receive input", TraceLevel.Warning);
                return false;
            }

            // Snapshot.
            arrangementRadio = theRadio;
            arrangementConnection = arrangementRadio.CurrentCommandConnection;
            _lbSavedFdx = confirmedPriorFullDuplex ?? FullDuplexState(arrangementRadio, arrangementConnection);
            var tx = SliceForTransmitSetting();
            _lbSavedTxAnt = ListenbackPrior("tx-antenna", TXAntennaName, tx);
            _lbSavedMonitor = ListenbackPrior("monitor", theRadio.TXMonitor ? "1" : "0") == "1";
            _lbSavedPower = int.Parse(ListenbackPrior("rf-power", XmitPower.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                System.Globalization.CultureInfo.InvariantCulture);
            List<Slice> ownedAtStart;
            lock (mySlices) ownedAtStart = mySlices.ToList();
            _lbSavedActive = VFOToSlice(RXVFO);
            var savedMutes = new List<(Slice Slice, bool Muted)>();
            foreach (var owned in ownedAtStart)
                savedMutes.Add((owned, await PriorSliceMuteAsync(arrangementRadio, arrangementConnection, owned).ConfigureAwait(false)));
            _lbSavedMutes = savedMutes;
            LoopbackSliceResources resources;
            lock (mySlices)
                resources = new LoopbackSliceResources(mySlices, arrangementRadio.PanadapterList);
            _lbResources = resources;

            // Apply the recipe (queue-sequenced).
            try
            {
                if (!await SetFullDuplexConfirmedAsync(true, arrangementRadio, arrangementConnection,
                    note => Tracing.TraceLine("Loopback full duplex setup: " + note, TraceLevel.Info)).ConfigureAwait(false))
                    throw new InvalidOperationException("full-duplex-connection-changed");
                setupChanged = true;
            }
            catch (Exception ex)
            {
                _lbSetupFailure = ex.Message;
                Tracing.TraceLine("StartLoopbackArrangement full duplex: " + ex.Message, TraceLevel.Error);
                try { await SetFullDuplexConfirmedAsync(_lbSavedFdx, arrangementRadio, arrangementConnection,
                    note => Tracing.TraceLine("Loopback full duplex setup rollback: " + note, TraceLevel.Info)).ConfigureAwait(false); }
                catch (Exception restore) { Tracing.TraceLine("StartLoopbackArrangement full duplex rollback: " + restore.Message, TraceLevel.Error); }
                _lbResources = null;
                _lbSavedMutes = null;
                return false;
            }
            if (!ReferenceEquals(theRadio, arrangementRadio) ||
                !ReferenceEquals(arrangementConnection, arrangementRadio.CurrentCommandConnection))
            {
                _lbResources = null;
                _lbSavedMutes = null;
                return false;
            }
            if (confirmedPriorFullDuplex.HasValue)
            {
                // Off-air takes restore by raw reply. Cache-sensitive setters
                // can skip these writes on the very next take.
                if (tx == null || !await LoopbackSliceCommandAsync(arrangementRadio, arrangementConnection,
                    tx, $"slice set {tx.Index} txant={xvt}", "tx antenna setup").ConfigureAwait(false))
                    throw new InvalidOperationException("tx-antenna-setup-unconfirmed");
                if (!await LoopbackRadioCommandAsync(arrangementRadio, arrangementConnection,
                    "transmit set rfpower=1", "RF power setup").ConfigureAwait(false))
                    throw new InvalidOperationException("rf-power-setup-unconfirmed");
                if (!await LoopbackRadioCommandAsync(arrangementRadio, arrangementConnection,
                    "transmit set mon=0", "monitor setup").ConfigureAwait(false))
                    throw new InvalidOperationException("monitor-setup-unconfirmed");
            }
            else
            {
                TXAntennaName = xvt;
                XmitPower = 1;
                Monitor = OffOnValues.off;
            }
            // Drive management: where a transverter band is defined, start at
            // maximum attenuation so the adjacent-port coupling lands as far
            // into the receiver's linear range as the hardware allows.
            _lbDriveBand = findAnyValidXvtr();
            if (_lbDriveBand != null)
            {
                _lbSavedDriveDbm = double.Parse(ListenbackPrior("xvtr-drive",
                    _lbDriveBand.MaxPower.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    System.Globalization.CultureInfo.InvariantCulture);
                var band = _lbDriveBand;
                if (confirmedPriorFullDuplex.HasValue)
                {
                    if (!await LoopbackRadioCommandAsync(arrangementRadio, arrangementConnection,
                        $"xvtr set {band.Index} max_power=-10", "transverter drive setup").ConfigureAwait(false))
                        throw new InvalidOperationException("xvtr-drive-setup-unconfirmed");
                }
                else q.Enqueue((FunctionDel)(() => { band.MaxPower = -10.0; }), "LoopbackDrive");
            }

            foreach (var saved in _lbSavedMutes)
            {
                Tracing.TraceLine($"Loopback prior mute: slice={saved.Slice.Index} muted={saved.Muted}", TraceLevel.Info);
                if (!saved.Muted && !await LoopbackSliceCommandAsync(arrangementRadio, arrangementConnection,
                    saved.Slice, $"slice set {saved.Slice.Index} audio_mute=1", "mute setup").ConfigureAwait(false))
                    throw new InvalidOperationException("slice-mute-setup-unconfirmed");
            }

            // NewSlice's queued creation waits for the owned pan and slice.
            // Do not report readiness until configuration and gain have replies.
            if (!NewSlice(resources, DiagnosticSlicePurpose.Loopback))
            {
                Tracing.TraceLine("StartLoopbackArrangement: NewSlice refused", TraceLevel.Error);
                throw new InvalidOperationException("loopback-new-slice-refused");
            }
            Slice ears = null;
            await IqQueue(() =>
            {
                if (!ReferenceEquals(theRadio, arrangementRadio)) throw new InvalidOperationException("connection-ended");
                lock (mySlices)
                {
                    if (!resources.Capture(mySlices, arrangementRadio.PanadapterList))
                        throw new InvalidOperationException("loopback-ears-missing");
                    string[] earsInputs;
                    try { earsInputs = resources.Pan.RXAntennaList; }
                    catch (NullReferenceException) { earsInputs = null; }
                    if (!(earsInputs?.Contains(earsAntenna) ?? false))
                        throw new InvalidOperationException("loopback-ears-port-unavailable");
                    if (
                        !resources.Configure(mySlices, arrangementRadio.PanadapterList,
                            SliceForTransmitSetting(), earsAntenna))
                        throw new InvalidOperationException("loopback-ears-missing");
                    ears = resources.Slice;
                }
            }).ConfigureAwait(false);
            if (!await LoopbackSliceCommandAsync(arrangementRadio, arrangementConnection, ears,
                $"slice set {ears.Index} rxant={earsAntenna}", "ears antenna setup").ConfigureAwait(false))
                throw new InvalidOperationException("loopback-ears-setting-refused");
            resources.EarsRFGain = await LoopbackEarsRFGainAsync(arrangementRadio, arrangementConnection,
                ears, resources.Pan).ConfigureAwait(false);
            if (!await LoopbackSliceCommandAsync(arrangementRadio, arrangementConnection, ears,
                $"slice set {ears.Index} rfgain={resources.EarsRFGain}", "ears RF gain setup").ConfigureAwait(false))
                throw new InvalidOperationException("loopback-ears-setting-refused");
            // Sent unconditionally: the cache may not yet hold the mute the
            // radio gave the new slice, and the slice is removed at the end,
            // so there is nothing of the operator's to restore.
            if (earsAudible && !await LoopbackSliceCommandAsync(arrangementRadio, arrangementConnection, ears,
                $"slice set {ears.Index} audio_mute=0", "ears unmute").ConfigureAwait(false))
                throw new InvalidOperationException("loopback-ears-setting-refused");

            _loopbackArranged = true;
            _lbArrangementRadio = arrangementRadio;
            _lbArrangementConnection = arrangementConnection;
            Tracing.TraceLine($"Loopback arranged: txXvt={xvt} earsRx={earsAntenna} savedFdx={_lbSavedFdx} savedAnt={_lbSavedTxAnt} savedPwr={_lbSavedPower} earsSlice={resources.Slice?.Index}", TraceLevel.Info);
            return true;
            }
            catch (Exception ex)
            {
                _lbSetupFailure = ex.Message;
                Tracing.TraceLine("StartLoopbackArrangement failed: " + ex, TraceLevel.Error);
                if (setupChanged)
                    try
                    {
                        _loopbackArranged = true;
                        _lbArrangementRadio = arrangementRadio;
                        _lbArrangementConnection = arrangementConnection;
                        string trouble = await EndLoopbackArrangementAsync().ConfigureAwait(false);
                        if (trouble.Length != 0) Tracing.TraceLine("StartLoopbackArrangement rollback: " + trouble, TraceLevel.Error);
                    }
                    catch (Exception restore) { Tracing.TraceLine("StartLoopbackArrangement rollback: " + restore, TraceLevel.Error); }
                _lbResources = null;
                return false;
            }
            finally { System.Threading.Volatile.Write(ref _lbArrangementStarting, 0); }
        }

        private async Task<int> LoopbackEarsRFGainAsync(Radio radio, CommandConnection connection,
            Slice ears, Panadapter pan)
        {
            const int fallback = 32;
            string command = $"display pan rfgain_info 0x{pan.StreamID:X}";
            var reply = new TaskCompletionSource<(uint Code, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await IqQueue(() =>
            {
                if (!ReferenceEquals(theRadio, radio) || !radio.Connected ||
                    !ReferenceEquals(radio.CurrentCommandConnection, connection) ||
                    !radio.SliceList.Contains(ears) || !mySlices.Contains(ears) ||
                    !radio.PanadapterList.Contains(pan) || !ReferenceEquals(ears.Panadapter, pan))
                    throw new InvalidOperationException("loopback-connection-or-slice-changed");
                int sent = radio.SendReplyCommand((sequence, code, response) =>
                {
                    Tracing.TraceLine($"Loopback ears RF gain info: reply {sequence} {code:X8} {response}", TraceLevel.Info);
                    reply.TrySetResult((code, response));
                }, command);
                Tracing.TraceLine($"Loopback ears RF gain info: sent {sent} {command}", TraceLevel.Info);
            }).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(ListenbackConfirmationWait);
            (uint Code, string Text) result;
            try { result = await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Tracing.TraceLine($"Loopback ears RF gain info: no reply; sending {fallback}", TraceLevel.Warning);
                return fallback;
            }
            string[] range = result.Text?.Split(',') ?? Array.Empty<string>();
            int low = 0, high = 0, step = 0;
            bool parsed = result.Code == 0 && range.Length >= 3 &&
                int.TryParse(range[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out low) &&
                int.TryParse(range[1].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out high) &&
                int.TryParse(range[2].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out step);
            int gain = parsed ? Math.Max(fallback, high) : fallback;
            Tracing.TraceLine(parsed
                ? $"Loopback ears RF gain: radio reports low={low} high={high} step={step}; sending {gain}"
                : $"Loopback ears RF gain: unusable radio reply; sending {gain}", TraceLevel.Info);
            return gain;
        }

        private async Task<bool> LoopbackRadioCommandAsync(Radio radio, CommandConnection connection,
            string command, string purpose)
        {
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await IqQueue(() =>
            {
                if (!ReferenceEquals(theRadio, radio) || !radio.Connected ||
                    !ReferenceEquals(radio.CurrentCommandConnection, connection))
                    throw new InvalidOperationException("loopback-connection-changed");
                int sent = radio.SendReplyCommand((sequence, code, response) =>
                {
                    Tracing.TraceLine($"Loopback {purpose}: reply {sequence} {code:X8} {response}", TraceLevel.Info);
                    reply.TrySetResult(code == 0);
                }, command);
                Tracing.TraceLine($"Loopback {purpose}: sent {sent} {command}", TraceLevel.Info);
            }).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(ListenbackConfirmationWait);
            try { return await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        private async Task<bool> LoopbackSliceCommandAsync(Radio radio, CommandConnection connection,
            Slice slice, string command, string purpose)
        {
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await IqQueue(() =>
            {
                if (!ReferenceEquals(theRadio, radio) || !radio.Connected ||
                    !ReferenceEquals(radio.CurrentCommandConnection, connection) ||
                    !radio.SliceList.Contains(slice) || !mySlices.Contains(slice))
                    throw new InvalidOperationException("loopback-connection-or-slice-changed");
                int sent = radio.SendReplyCommand((sequence, code, response) =>
                {
                    Tracing.TraceLine($"Loopback {purpose}: reply {sequence} {code:X8} {response}", TraceLevel.Info);
                    reply.TrySetResult(code == 0);
                }, command);
                Tracing.TraceLine($"Loopback {purpose}: sent {sent} {command}", TraceLevel.Info);
            }).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(ListenbackConfirmationWait);
            try { return await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Tracing.TraceLine($"Loopback {purpose}: reply timed out for {command}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Tear the loopback arrangement down: restore every saved value and
        /// remove the ears slice. Returns a short status suitable for speech
        /// ("" when everything restored cleanly).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every step goes through <see cref="CheckRestorations"/> (#835, fix
        /// round 3).</b> They are registered here, synchronously, before this
        /// method first awaits, in the order they always ran: the listening
        /// slice's recorder, the return from and removal of the listening slice,
        /// the slice mutes, the transmit antenna, power, monitor, full duplex and
        /// transverter drive. The owner runs each only while the radio reports
        /// receive, asked again before each one. Until then this restored the
        /// antenna and the operator's power with no such question, so a hardware
        /// PTT line still holding the radio keyed got full power on the prior
        /// antenna the moment the arrangement came down.
        /// </para>
        /// <para>
        /// Because the steps belong to the radio's owner, they still run, and a
        /// teardown still waits for them, after the session that began them and
        /// the window that showed it have gone. This method's own task completes
        /// when they have; while the radio keeps transmitting, it does not.
        /// </para>
        /// </remarks>
        public Task<string> EndLoopbackArrangementAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _lbArrangementEnding, 1) != 0)
                return Task.FromResult("Loopback restoration is still running. ");
            if (!_loopbackArranged)
            {
                System.Threading.Volatile.Write(ref _lbArrangementEnding, 0);
                return Task.FromResult("");
            }

            // What this arrangement saved, held here: the last step forgets the
            // fields, and a later arrangement must not be restored by this one.
            var resources = _lbResources;
            var savedMutes = _lbSavedMutes;
            var savedActive = _lbSavedActive;
            var arrangementRadio = _lbArrangementRadio;
            var arrangementConnection = _lbArrangementConnection;
            string savedTxAnt = _lbSavedTxAnt;
            int savedPower = _lbSavedPower;
            bool savedMonitor = _lbSavedMonitor;
            bool savedFdx = _lbSavedFdx;
            var driveBand = _lbDriveBand;
            double savedDriveDbm = _lbSavedDriveDbm;

            var trouble = new System.Text.StringBuilder();
            void Trouble(string text) { lock (trouble) trouble.Append(text); }
            // Asked by each step at its own run: the same radio, on the same
            // connection, that this arrangement was made on.
            bool Same() => theRadio != null && ReferenceEquals(theRadio, arrangementRadio) &&
                ReferenceEquals(theRadio.CurrentCommandConnection, arrangementConnection);

            var owner = CheckRestorations;
            var steps = new List<CheckRestorations.Entry>
            {
                // Every step reports whether it really put its part back (fix
                // round 4): a refusal, an unconfirmed reply or a changed
                // connection is a failure the owner carries, not a normal return.
                owner.Register("loopback: listening slice recorder", async () =>
                {
                    // The listening slice must stop its own buffer before removal.
                    bool recorderStopped = await StopSliceRecorderAsync(SliceRecorderTarget.LoopbackEars).ConfigureAwait(false);
                    _loopbackArranged = false;
                    if (!recorderStopped) Trouble("The listening slice recorder stop was not confirmed. ");
                    if (!Same()) Trouble("The radio connection changed before restoration could be confirmed. ");
                    return recorderStopped && Same();
                }),
                // The return to, and removal of, the listening slice, then each
                // mute on its own: separate entries, so the radio is asked
                // again between them (fix round 4).
                owner.Register("loopback: return from the listening slice", async () =>
                {
                    if (!Same()) return false;
                    // An operator who moved to the listening slice during the check
                    // (Noel did, to find its mute) would otherwise keep it: the
                    // removal refuses the active slice. Go back to where the check
                    // started, silently, so the removal can proceed.
                    if (resources?.Slice != null && ReferenceEquals(resources.Slice, VFOToSlice(RXVFO)) &&
                        !ReferenceEquals(resources.Slice, SliceForTransmitSetting()))
                        await ReturnFromListeningSliceAsync(arrangementRadio, arrangementConnection,
                            resources.Slice, savedActive).ConfigureAwait(false);
                    return true;
                }),
                owner.Register("loopback: listening slice removal", () =>
                {
                    if (!Same()) return Task.FromResult(false);
                    bool ok = true;
                    if (resources?.Slice != null &&
                        (ReferenceEquals(resources.Slice, VFOToSlice(RXVFO)) ||
                         ReferenceEquals(resources.Slice, SliceForTransmitSetting())))
                    {
                        Trouble("The listening slice is your active slice, so it was kept. ");
                        ok = false;
                    }
                    bool reservedRemoval = resources != null && (resources.Slice == null ||
                        (!ReferenceEquals(resources.Slice, VFOToSlice(RXVFO)) && !ReferenceEquals(resources.Slice, SliceForTransmitSetting())));
                    if (reservedRemoval) System.Threading.Interlocked.Increment(ref _pendingRemovals);
                    q.Enqueue((FunctionDel)(() =>
                    {
                        try
                        {
                            if (!ReferenceEquals(theRadio, arrangementRadio) || resources == null) return;
                            lock (mySlices)
                            {
                                mySliceRemoved = false;
                                if (!resources.Release(mySlices, arrangementRadio.PanadapterList,
                                    VFOToSlice(RXVFO), SliceForTransmitSetting())) return;
                            }
                            if (!FlexBase.@await(() => mySliceRemoved, 3000))
                                Tracing.TraceLine("Loopback ears removal: radio confirmation timed out", TraceLevel.Error);
                        }
                        finally { if (reservedRemoval) System.Threading.Interlocked.Decrement(ref _pendingRemovals); }
                    }), "LoopbackEarsRemove");
                    return Task.FromResult(ok);
                }),
            };
            // Each saved mute is its own entry, registered here so the FIFO
            // order stays the one the steps were written in.
            foreach (var saved in savedMutes ?? new List<(Slice Slice, bool Muted)>())
            {
                steps.Add(owner.Register($"loopback: slice {saved.Slice.Index} mute", async () =>
                {
                    if (!Same()) return false;
                    try
                    {
                        if (!await LoopbackSliceCommandAsync(arrangementRadio, arrangementConnection,
                            saved.Slice, $"slice set {saved.Slice.Index} audio_mute={(saved.Muted ? 1 : 0)}", "mute restore").ConfigureAwait(false))
                        {
                            Trouble($"Slice {saved.Slice.Index} mute restoration was not confirmed. ");
                            return false;
                        }
                        // The reply alone left FlexLib's Slice.Mute stale on the
                        // bench, and the station layout and the next check both
                        // read it. A fresh report corrects the cache and proves
                        // the restore.
                        if (await ReadSliceMuteAsync(arrangementRadio, arrangementConnection,
                            saved.Slice).ConfigureAwait(false) is bool read && read != saved.Muted)
                        {
                            Trouble($"Slice {saved.Slice.Index} mute restoration was not confirmed. ");
                            return false;
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("Loopback mute restore: " + ex.Message, TraceLevel.Error);
                        Trouble($"Slice {saved.Slice.Index} mute restoration was not confirmed. ");
                        return false;
                    }
                }));
            }
            steps.AddRange(new[]
            {
                owner.Register("loopback: transmit antenna", () =>
                {
                    if (!Same()) return Task.FromResult(false);
                    TXAntennaName = savedTxAnt;
                    return Task.FromResult(true);
                }),
                owner.Register("loopback: power", () => Same()
                    ? RestoreWritesAtReceiveAsync("loopback power", () => XmitPower = savedPower)
                    : Task.FromResult(RestoreOutcome.Failed)),
                owner.Register("loopback: monitor", () => Same()
                    ? RestoreWritesAtReceiveAsync("loopback monitor", () => Monitor = savedMonitor ? OffOnValues.on : OffOnValues.off)
                    : Task.FromResult(RestoreOutcome.Failed)),
                owner.Register("loopback: full duplex", async () =>
                {
                    if (!Same()) return false;
                    try
                    {
                        if (await SetFullDuplexConfirmedAsync(savedFdx, arrangementRadio,
                            arrangementConnection, note => Tracing.TraceLine("Loopback full duplex restore: " + note, TraceLevel.Info)).ConfigureAwait(false))
                            return true;
                        Trouble("Full duplex restoration was not confirmed. ");
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("Loopback full duplex restore: " + ex.Message, TraceLevel.Error);
                        Trouble("Full duplex restoration was not confirmed. ");
                    }
                    return false;
                }),
                owner.Register("loopback: transverter drive", () =>
                {
                    if (driveBand == null) return Task.FromResult(RestoreOutcome.Done);
                    if (!Same()) return Task.FromResult(RestoreOutcome.Failed);
                    var band = driveBand;
                    double dbm = savedDriveDbm;
                    return RestoreWritesAtReceiveAsync("loopback transverter drive",
                        () => q.Enqueue((FunctionDel)(() => { band.MaxPower = dbm; }), "LoopbackDriveRestore"));
                }),
                owner.Register("loopback: forget the arrangement", () =>
                {
                    if (ReferenceEquals(_lbResources, resources)) _lbResources = null;
                    if (ReferenceEquals(_lbSavedMutes, savedMutes)) _lbSavedMutes = null;
                    if (ReferenceEquals(_lbSavedActive, savedActive)) _lbSavedActive = null;
                    if (ReferenceEquals(_lbDriveBand, driveBand)) _lbDriveBand = null;
                    if (ReferenceEquals(_lbArrangementRadio, arrangementRadio)) _lbArrangementRadio = null;
                    if (ReferenceEquals(_lbArrangementConnection, arrangementConnection)) _lbArrangementConnection = null;
                }),
            });
            return FinishLoopbackEndAsync(steps, trouble);
        }

        private async Task<string> FinishLoopbackEndAsync(List<CheckRestorations.Entry> steps, System.Text.StringBuilder trouble)
        {
            try
            {
                var results = await Task.WhenAll(steps.Select(st => st.Task)).ConfigureAwait(false);
                if (results.Any(r => !r)) lock (trouble) trouble.Append("Some loopback settings could not be restored; details are in the trace. ");
                string text;
                lock (trouble) text = trouble.ToString();
                Tracing.TraceLine("Loopback arrangement ended: " + (text == "" ? "clean" : text), TraceLevel.Info);
                return text;
            }
            finally { System.Threading.Volatile.Write(ref _lbArrangementEnding, 0); }
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

                // The zero-and-restore pair is mechanical, not an operator
                // settings change — it must not arm the provisional-change
                // receipt or the disconnect save offer (#225). Whether it is
                // the OPERATOR'S command is not this setter's to say: the
                // Transmit menu's item is, and the Audio Check's own engage is
                // not (#571, Opus N1 and R1). The caller that is the operator's
                // command wraps this in OperatorChoseThisChange; the setter
                // itself declares nothing, so the Audio Check's writes stay
                // app-originated and cannot lift the hold after a cut.
                using (AppInitiatedSettingChanges())
                {
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
                NoteRadioSettingWritten("XmitPower", value.ToString());   // the hold after a cut needs to know whose write this is (#571)
                q.Enqueue((FunctionDel)(() => { theRadio.RFPower = value; }));
            }
        }

        // #608. The radio's own PA cap, as it reports it.
        //
        // *** IT IS NOT ON THE SAME SCALE AS XmitPower. DO NOT COMPARE THEM. ***
        //
        // #195 established this before #608 existed, and the first version of
        // this comment ignored it: FlexLib documents MaxPowerLevel as a
        // RELATIVE, NON-LINEAR scale capping the PA, while RFPower is a level
        // whose top is the mode's maximum. Both arrive as ints from nought to a
        // hundred, both look like percentages, and they measure different
        // things. #195 names that collision as the same species as reading dBm
        // as watts, which cost a morning on 2026-08-22.
        //
        // So this is NOT a ceiling to clamp XmitPower against, NOT a number to
        // show beside it, and NOT convertible to watts.
        //
        // Seeded from nothing: like _XmitPower above, this is written only by
        // the property-change case, which fires during the status flood at
        // connect. Zero therefore means "the radio has not told us yet", NOT
        // "no power allowed" — so a caller must treat zero as unknown and fall
        // back to XmitPowerMax rather than believing it. MaxXmitPowerKnown says
        // which of the two it is, so no caller has to encode that rule twice.
        //
        // NOT consulted by any display, and #608's measurements are why it
        // should stay that way until somebody has a real use for it: with the
        // TX slice in AM this value did not move, so it is NOT the channel the
        // radio uses to express the AM carrier limit. It was captured because
        // discarding a status the radio sends is indefensible, not because a
        // display wanted it.
        private int _MaxXmitPower;

        /// <summary>
        /// The PA cap the radio reports, on its OWN relative and non-linear
        /// scale — read the comment above before using this for anything.
        /// <para><b>Not comparable to <see cref="XmitPower"/>, not a clamp for
        /// it, and not watts.</b> Returns <see cref="XmitPowerMax"/> when the
        /// radio has reported nothing, which is a "no cap known" sentinel rather
        /// than a measurement.</para>
        /// </summary>
        public int MaxXmitPower => _MaxXmitPower > 0 ? _MaxXmitPower : XmitPowerMax;

        /// <summary>
        /// Whether the radio has actually reported a cap, as distinct from
        /// <see cref="MaxXmitPower"/> having fallen back to its sentinel.
        /// </summary>
        public bool MaxXmitPowerKnown => _MaxXmitPower > 0;

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
                NoteRadioSettingWritten("TunePower", value.ToString());   // the hold after a cut needs to know whose write this is (#571)
                q.Enqueue((FunctionDel)(() => { theRadio.TunePower = value; }));
            }
        }

        // VOX delay is the hang time in milliseconds. The radio takes it as a
        // raw 0-100 step count and FlexLib says each step is 20 ms
        // (Radio.SimpleVOXDelay: "The delay will be (value * 20)
        // milliseconds"), so the radio's whole scale is 0-2000 ms — exactly
        // the range Jim wrote below. His divisor was 50 from the first
        // commit (#565): with it, 2000 ms reached only raw 40, the top 60
        // percent of the radio's scale was unreachable from here, and a
        // radio sitting at its genuine maximum read back as 5000 ms. The
        // range, the 100 ms press and the milliseconds are his design and
        // stay; only the constant was wrong. VoxLimitAgreementTests pins
        // VoxDelayMax / VoxDelayMS to FlexLib's clamp so it cannot drift back.
        public const int VoxDelayMin = 0;
        public const int VoxDelayMax = 2000;
        public const int VoxDelayIncrement = 100;
        public const int VoxDelayMS = 20;
        // Null guards match MicGain's, and for the same reason: this is the
        // radio-level TX getter family, polled from the fields panel, and a
        // poll racing radio teardown must read a default rather than throw.
        public int VoxDelay
        {
            get { return (theRadio?.SimpleVOXDelay ?? 0) * VoxDelayMS; }
            set
            {
                NoteOperatorChangedRadioSetting("VoxDelay");
                q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXDelay = value / VoxDelayMS; }), "SimpleVOXDelay");
            }
        }

        // VOX gain is the threshold: how loud the mic must be before the
        // radio decides it is hearing speech and keys. Raw 0-100, no scaling.
        public const int VoxGainMin = 0;
        public const int VoxGainMax = 100;
        public const int VoxGainIncrement = 5;
        public int VoxGain
        {
            get { return theRadio?.SimpleVOXLevel ?? 0; }
            set
            {
                NoteOperatorChangedRadioSetting("VoxGain");
                q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXLevel = value; }), "SimpleVOXLevel");
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
                    NoteOperatorChangedRadioSetting("ANF");
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
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("AutoNotchLevel");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFLevel = value; }));
                }
            }
        }

        public OffOnValues APF
        {
            get { return (theRadio?.ActiveSlice?.APFOn == true) ? OffOnValues.on : OffOnValues.off; }
            //get { return (theRadio.APFMode) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("APF");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFOn = val; }));
                }
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
                if (HasActiveSlice)
                {
                    NoteOperatorChangedRadioSetting("AutoPeakLevel");
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFLevel = value; }));
                }
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
                    NoteOperatorChangedRadioSetting("RFGain");
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
                NoteOperatorChangedRadioSetting("Squelch");
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
                NoteOperatorChangedRadioSetting("SquelchLevel");
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

        /// <summary>
        /// Binaural receive — the radio's own stereo widening of the slice
        /// audio (FlexLib <c>BinauralRX</c>). Jim had a BinauralControl for
        /// it; the control did not survive the WPF migration and the
        /// capability did (#524). Public since Sprint 44 Track N, which gave
        /// it the audio layer's Ctrl+B; Sprint 45 Track C gave it a visual
        /// surface again (#537) — a checkbox in Home's audio expander and a
        /// row on the Audio menu. All three speak
        /// <c>audio.binaural.on</c> / <c>audio.binaural.off</c>, and
        /// <c>BinauralOneVocabularyTests</c> keeps it that way.
        ///
        /// Radio-wide, not per-slice: this changes every slice at once.
        /// </summary>
        public OffOnValues Binaural
        {
            get { return (theRadio?.BinauralRX == true) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.BinauralRX = val; }));
            }
        }

        // Two more doors to the radio's slice recorder, internal Play and
        // Record properties from the original JJFlexRadio, stood here until
        // Tape Deck track 2 (#787) found them: unused, unchecked, and a third
        // way to drive the recorder. FlexBase.SliceRecorder.cs is the only one.

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
            // The hold after a reflected-power cut (#571; Noel 21:16, "nothing
            // re-keys"): a CW message keys the radio through break-in, and
            // break-in may still be on in a mode the cut did not pause. The
            // hook speaks the refusal; nothing is buffered or queued.
            if (CwKeyingRefused("a CW message")) return false;
            if (RefusedForCheckTeardown("a CW message")) return false;   // #835
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
            if (CwKeyingRefused("CW typing")) return false;   // #571, as the string form above
            if (RefusedForCheckTeardown("CW typing")) return false;   // #835
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
            // CW Auto Tune is a SmartSDR+ feature, so the caps table is only
            // half the question. This path checked the hardware and never the
            // licence - #725's defect in a second feature, found by enumerating
            // the thirteen licence features for #726 rather than by a report.
            if (RefuseCapabilityWrite(RadioCapability.CwAutotune)) return;
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
                // A write queued under an ambient QueuedWriteGate is asked
                // again at its RUN whether it may still go out (Track G2,
                // review step 9: validation inside every queued setter).
                var gate = Radios.StationConnect.QueuedWriteGate.Ambient;
                if (gate != null && o is FunctionDel gated)
                {
                    // A gated item is one of OUR chain writes. Its setter
                    // raises PropertyChanged synchronously on the loop's
                    // thread, which the provenance scope must mark as a
                    // local echo — or the echo advances the chain generation
                    // and the NEXT gated setter refuses because "the chain
                    // changed" (Track G2 re-review, step 9: the deferred apply
                    // invalidated itself after its first changed setter).
                    var wrapped = gate.Wrap(() => { using (OwnProfileWrite()) gated(); }, name ?? "unnamed",
                        (n, why) => Tracing.TraceLine("q: refused '" + n + "' at its run — " + why, TraceLevel.Warning));
                    o = (FunctionDel)(() => wrapped());
                }
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
