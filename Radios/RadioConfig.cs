using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// How JJ Flex should reach this radio over SmartLink.
    /// </summary>
    public enum RadioConnectionPreference
    {
        /// <summary>Follow what the radio reports about itself (forwarded ports,
        /// hole-punch requirement) on each connect. The right answer for almost
        /// everyone; the radio-list message already carries the truth.</summary>
        Auto = 0,

        /// <summary>Always use the forwarded/public ports, never hole punch.</summary>
        ForwardOnly = 1,

        /// <summary>Always hole punch, never rely on forwarded ports.</summary>
        HolePunch = 2,
    }

    /// <summary>
    /// How the Audio Check session lets the operator hear themselves.
    /// Numeric values are stable so a future Loopback tier (or others) can
    /// join the cycle without renumbering saved configs.
    /// </summary>
    public enum AudioCheckListenMethods
    {
        /// <summary>TX monitor — instant, colored (post-DSP, pre-RF tap).
        /// Works on every model. The conservative default.</summary>
        Monitor = 0,

        /// <summary>Slice quick-record, auto-played after unkey. The
        /// recommended path over remote (no delayed-self-hearing) — the
        /// recording carries the full processing chain.</summary>
        RecordPlayback = 1,
    }

    /// <summary>
    /// What PC audio (radio audio through this computer) should do when this
    /// radio connects (Threads Track, 2026-08-12). Numeric values are stable
    /// for saved configs.
    /// </summary>
    public enum PcAudioOnConnectModes
    {
        /// <summary>Come back the way the operator left it. The adaptive
        /// default — but not sufficient alone, because it faithfully carries
        /// an accident forward: a session where PC audio got switched off by
        /// a hiccup would poison every session after it. The explicit modes
        /// below exist for exactly that.</summary>
        RememberLast = 0,

        /// <summary>Always turn PC audio on for this radio. Survives a bad
        /// night, which is exactly what a remote-only operator needs — over
        /// remote, PC audio is the only way to hear the radio at all.</summary>
        AlwaysOn = 1,

        /// <summary>Always leave PC audio off for this radio.</summary>
        AlwaysOff = 2,
    }

    /// <summary>
    /// How the Audio Check handles transmit power (Workshop Track,
    /// 2026-08-11). Numeric values are stable for saved configs.
    /// </summary>
    public enum AudioCheckPowerModes
    {
        /// <summary>Dummy load: zero watts, no RF at all. The default —
        /// every meter the check reads (SC_MIC, SW ALC) sits upstream of
        /// the power amplifier, so the measurement is identical with no
        /// RF, and a check with a tone armed must not put a carrier on an
        /// occupied frequency. Proven at the radio 2026-08-11: a tone at
        /// -10 dBFS read -11 on SC_MIC at zero watts.</summary>
        DummyLoad = 0,

        /// <summary>Cap transmit power at <see
        /// cref="RadioConfig.AudioCheckLowPowerWatts"/> for the separate,
        /// deliberate act of confirming RF actually leaves the radio. A
        /// cap only — it never raises power and cannot override an active
        /// dummy load mode.</summary>
        LowPower = 1,
    }

    /// <summary>
    /// Per-radio configuration, keyed by radio serial (or, for future non-Flex
    /// rigs, whatever stable identifier the backend provides). Stored at
    /// <c>{BaseConfigDir}\radios\{radioId}\config.xml</c>.
    ///
    /// <para>
    /// This is the first tenant of the serial-keyed store called for by the
    /// 2026-04-28 per-radio-config principle: settings that describe THE RADIO
    /// (how to reach it, its site's network reality) rather than the operator.
    /// Two operators of one radio share this file's meaning; one operator with
    /// two radios has two files. Operator preferences stay in the existing
    /// {opName}_*.xml files.
    /// </para>
    /// </summary>
    public class RadioConfig
    {
        /// <summary>Schema version for forward-compatible migrations.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Echo of the radio id this file belongs to. Informational —
        /// the directory name is authoritative — but it lets a stray file
        /// identify itself.</summary>
        public string RadioId { get; set; } = "";

        /// <summary>Last known nickname, refreshed on connect. Lets offline
        /// pickers show "6300 inshack" instead of a bare serial.</summary>
        public string Nickname { get; set; } = "";

        /// <summary>Connection strategy for this radio. Auto (default) follows
        /// the radio-reported flags each connect.</summary>
        public RadioConnectionPreference ConnectionPreference { get; set; }
            = RadioConnectionPreference.Auto;

        /// <summary>Fixed client hole-punch port for this radio. 0 (default)
        /// means pick a fresh random port per connect, which is the recommended
        /// setting — a fixed port can clash with a stale NAT mapping. Non-zero
        /// exists for testing rigs and routers that need a pinned rule.</summary>
        public int FixedHolePunchPort { get; set; }

        /// <summary>
        /// Owner-declared waiver (Noel, 2026-08-06): allow changing the radio's
        /// SmartLink port settings from a remote connection, where the default
        /// policy demands the primary operator at the radio. The trust model:
        /// a valid SmartLink token for the radio's account is itself the
        /// owner's grant — anyone holding it was given it — so the owner of a
        /// remote-base radio (who is NEVER at it) flips this on rather than
        /// being locked out of their own rig. Default false: conservative,
        /// per-radio, the operator's choice.
        /// </summary>
        public bool AllowRemotePortChanges { get; set; }

        /// <summary>
        /// Owner-declared waiver: allow firmware updates without the
        /// at-the-radio presence challenge. Firmware always travels the local
        /// network, so "remote" here means a VPN path (Tailscale) that makes a
        /// distant operator look local. Stored now; enforced when the firmware
        /// presence challenge (PresenceLevel.ActiveChallenge) ships — that
        /// implementation MUST honor this waiver or remote-base owners can
        /// never update firmware at all. Default false.
        /// </summary>
        public bool AllowRemoteFirmwareUpdates { get; set; }

        // ---------------------------------------------------------------
        // Roster display metadata (queue-burn Track E, 2026-08-07).
        // APPEND-ONLY: these fields exist so the radio selector can present
        // every radio this install has ever seen, whether or not it is
        // discoverable right now. They describe how the radio PRESENTS in a
        // list, never how it is reached — the reachability fields above are a
        // separate concern and must not be folded in here.
        //
        // Absent elements deserialize to their defaults, so a config.xml
        // written before this shipped loads unchanged.
        // ---------------------------------------------------------------

        /// <summary>Last known model string ("FLEX-8600"), refreshed whenever the
        /// radio is seen. Lets an offline row read as a radio rather than a
        /// serial number.</summary>
        public string Model { get; set; } = "";

        /// <summary>User-marked favorite. Favorites sort to the top of the
        /// selector's list. Purely a display preference — it changes no
        /// connection behaviour.</summary>
        public bool IsFavorite { get; set; }

        /// <summary>When this radio was last seen by discovery (UTC).
        /// <see cref="DateTime.MinValue"/> means "never seen since this field
        /// shipped" and is announced as "last seen unknown", never as 1 AD.</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>True when the last sighting arrived over SmartLink rather
        /// than local discovery. Drives the offline row's "remote via
        /// SmartLink" versus "local network" wording.</summary>
        public bool LastSeenRemote { get; set; }

        /// <summary>SmartLink account (email) that last listed this radio, or
        /// empty for a LAN-only sighting. Answers "which account do I sign in
        /// as to see this radio again?" — the question an offline remote row
        /// otherwise leaves dangling.</summary>
        public string LastSeenViaAccount { get; set; } = "";

        /// <summary>
        /// The operator's CHOICE of SmartLink account for this radio, as
        /// opposed to <see cref="LastSeenViaAccount"/> which is an
        /// observation. Set only by deliberate action (the row's context menu,
        /// the account manager's associations view) and NEVER auto-overwritten
        /// by a sighting — conflating the two lets an incidental listing
        /// destroy a deliberate decision with no event anyone could hear.
        /// Empty means automatic: resolution falls through to the observation,
        /// then to the preferred-account-for-new-connections. Exists for the
        /// radio reachable by TWO accounts (a club rig), where no heuristic
        /// can choose and last-seen-wins would flip-flop.
        /// </summary>
        public string PreferredAccount { get; set; } = "";

        /// <summary>
        /// Audio Check listen method for this radio (2026-08-07 Audio
        /// Workshop). Per-radio because the right answer follows the radio's
        /// situation (a remote rig wants record-and-play, a local one is fine
        /// on monitor). Conservative default: Monitor.
        /// </summary>
        public AudioCheckListenMethods AudioCheckListenMethod { get; set; }
            = AudioCheckListenMethods.Monitor;

        /// <summary>
        /// SUPERSEDED by <see cref="AudioCheckPowerMode"/> (Workshop Track,
        /// 2026-08-11). Retained so configs written before the change still
        /// round-trip; no longer read by the Audio Check. The old meaning:
        /// cap RF power at a hardwired 10 watts while keyed.
        /// </summary>
        public bool AudioCheckLowPower { get; set; } = true;

        /// <summary>
        /// How the Audio Check handles transmit power for this radio.
        /// Default is dummy load — an audio check does not need RF at all
        /// (the meters it reads sit upstream of the power amplifier), and
        /// with a test tone armed a transmitting check puts a steady
        /// carrier on whatever frequency the operator is tuned to.
        /// Low power exists for the separate, deliberate act of confirming
        /// RF actually leaves the radio.
        /// </summary>
        public AudioCheckPowerModes AudioCheckPowerMode { get; set; }
            = AudioCheckPowerModes.DummyLoad;

        /// <summary>
        /// The power cap, in watts, used when <see cref="AudioCheckPowerMode"/>
        /// is LowPower (Noel: "a low power output with a combo you can change
        /// so I can change it to 1 if I need to"). A cap: the check drops to
        /// this value when current power exceeds it and never raises power.
        /// Default 10, matching the old hardwired behaviour.
        /// </summary>
        public int AudioCheckLowPowerWatts { get; set; } = 10;

        // ---------------------------------------------------------------
        // PC audio on connect (Threads Track, 2026-08-12). Before this, PC
        // audio state was persisted nowhere: every connect started with it
        // off (remote connects auto-on), so an operator whose radio is only
        // reachable remotely re-enabled it every single session. Per-radio
        // because the right answer follows the radio's situation, and NOT
        // gated on the connection being remote — a local operator who
        // always listens through the PC gets the same memory.
        // ---------------------------------------------------------------

        /// <summary>
        /// What PC audio should do when this radio connects. Default is
        /// RememberLast; connect-time application announces what it did
        /// rather than silently flipping a switch.
        /// </summary>
        public PcAudioOnConnectModes PcAudioOnConnect { get; set; }
            = PcAudioOnConnectModes.RememberLast;

        /// <summary>
        /// True once <see cref="PcAudioLastOn"/> has ever been recorded.
        /// While false, RememberLast expresses no opinion and connect keeps
        /// its historical behaviour (remote connects turn PC audio on,
        /// local connects leave it off) — so a config written before this
        /// shipped changes nothing.
        /// </summary>
        public bool PcAudioLastStateKnown { get; set; }

        /// <summary>
        /// The operator's last deliberate PC audio choice for this radio.
        /// Recorded at the USER toggle surfaces (menu, Settings, hotkey),
        /// never from internal flips — disconnect turns PC audio off
        /// mechanically and a failed start turns it off in error, and
        /// neither is a choice worth remembering.
        /// </summary>
        public bool PcAudioLastOn { get; set; }

        /// <summary>
        /// What connect should set PC audio to, or null for "no opinion,
        /// keep the historical behaviour".
        /// </summary>
        [XmlIgnore]
        public bool? DesiredPcAudioOnConnect => PcAudioOnConnect switch
        {
            PcAudioOnConnectModes.AlwaysOn => true,
            PcAudioOnConnectModes.AlwaysOff => false,
            _ => PcAudioLastStateKnown ? PcAudioLastOn : (bool?)null,
        };

        /// <summary>
        /// Record a deliberate user PC-audio toggle for RememberLast. Call
        /// from the user-facing toggle surfaces with the state the USER asked
        /// for (intent, not outcome): if turning on failed tonight because a
        /// device was missing, the wish to have it on is still the thing
        /// worth carrying to the next session. No-ops on an unknown radio id
        /// and skips the disk write when nothing changed.
        /// </summary>
        public static void RecordPcAudioUserChoice(string radioId, bool on)
        {
            if (string.IsNullOrEmpty(radioId)) return;
            var cfg = LoadForRadio(radioId);
            if (cfg.PcAudioLastStateKnown && cfg.PcAudioLastOn == on) return;
            cfg.PcAudioLastStateKnown = true;
            cfg.PcAudioLastOn = on;
            cfg.SaveForRadio(radioId);
        }

        /// <summary>
        /// App-wide config root, assigned once at startup (ApplicationEvents,
        /// next to the other handler wiring). Static because the Radios layer
        /// has no ambient config-path service and the value never changes for
        /// the life of the process. When unset, LoadForRadio returns defaults
        /// and SaveForRadio declines — callers never need a null check.
        /// </summary>
        public static string? BaseDirectory { get; set; }

        /// <summary>Load via the app-wide <see cref="BaseDirectory"/>.</summary>
        public static RadioConfig LoadForRadio(string radioId)
        {
            var dir = BaseDirectory;
            return string.IsNullOrEmpty(dir)
                ? new RadioConfig { RadioId = radioId }
                : Load(dir, radioId);
        }

        /// <summary>Save via the app-wide <see cref="BaseDirectory"/>.</summary>
        public bool SaveForRadio(string radioId)
        {
            var dir = BaseDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                Tracing.TraceLine(
                    "RadioConfig.SaveForRadio: BaseDirectory not set — nothing saved",
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
            return Save(dir, radioId);
        }

        /// <summary>
        /// Loads the config for a radio, returning defaults when no file exists
        /// or the file is unreadable. Never throws.
        /// </summary>
        /// <param name="configDirectory">Base config directory (BaseConfigDir).</param>
        /// <param name="radioId">Radio serial or stable identifier.</param>
        public static RadioConfig Load(string configDirectory, string radioId)
        {
            var filePath = GetFilePath(configDirectory, radioId);
            if (!File.Exists(filePath))
            {
                return new RadioConfig { RadioId = radioId };
            }

            try
            {
                var serializer = new XmlSerializer(typeof(RadioConfig));
                using var stream = File.OpenRead(filePath);
                var config = (RadioConfig)serializer.Deserialize(stream);
                config.RadioId = radioId; // directory name is authoritative
                return config;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"RadioConfig.Load: unreadable {filePath}: {ex.Message} — using defaults",
                    System.Diagnostics.TraceLevel.Warning);
                return new RadioConfig { RadioId = radioId };
            }
        }

        /// <summary>
        /// Saves this config. Creates the radios\{id} directory as needed.
        /// Returns false (and traces) on failure rather than throwing.
        /// </summary>
        public bool Save(string configDirectory, string radioId)
        {
            var filePath = GetFilePath(configDirectory, radioId);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                RadioId = radioId;
                var serializer = new XmlSerializer(typeof(RadioConfig));
                using var stream = File.Create(filePath);
                serializer.Serialize(stream, this);
                Tracing.TraceLine(
                    $"RadioConfig.Save: {radioId} pref={ConnectionPreference} punchPort={FixedHolePunchPort}",
                    System.Diagnostics.TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"RadioConfig.Save: failed for {filePath}: {ex.Message}",
                    System.Diagnostics.TraceLevel.Error);
                return false;
            }
        }

        /// <summary>True when a config file exists for this radio.</summary>
        public static bool Exists(string configDirectory, string radioId)
        {
            return File.Exists(GetFilePath(configDirectory, radioId));
        }

        /// <summary>
        /// Radio ids that have saved config — the offline picker's data source.
        /// </summary>
        public static List<string> ListKnownRadioIds(string configDirectory)
        {
            var root = Path.Combine(configDirectory, "radios");
            if (!Directory.Exists(root))
            {
                return new List<string>();
            }

            return Directory.EnumerateDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "config.xml")))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Every saved per-radio config, loaded. The roster's data source;
        /// <see cref="ListKnownRadioIds"/> gives ids alone when that is all a
        /// caller needs. Never throws — an unreadable file contributes its
        /// defaults rather than aborting the whole enumeration.
        /// </summary>
        public static List<RadioConfig> LoadAllKnown(string configDirectory)
        {
            var result = new List<RadioConfig>();
            if (string.IsNullOrEmpty(configDirectory)) return result;
            foreach (var id in ListKnownRadioIds(configDirectory))
            {
                result.Add(Load(configDirectory, id));
            }
            return result;
        }

        /// <summary>Load every saved per-radio config via the app-wide
        /// <see cref="BaseDirectory"/>. Empty list when the base directory has
        /// not been assigned yet.</summary>
        public static List<RadioConfig> LoadAllKnown()
        {
            var dir = BaseDirectory;
            return string.IsNullOrEmpty(dir) ? new List<RadioConfig>() : LoadAllKnown(dir);
        }

        private static string GetFilePath(string configDirectory, string radioId)
        {
            return Path.Combine(configDirectory, "radios", SanitizeRadioId(radioId), "config.xml");
        }

        /// <summary>
        /// Flex serials (digits and dashes) pass through unchanged; anything a
        /// future backend supplies gets filesystem-hostile characters replaced
        /// so the id can always be a directory name.
        /// </summary>
        internal static string SanitizeRadioId(string radioId)
        {
            if (string.IsNullOrWhiteSpace(radioId))
            {
                return "_unknown";
            }

            var sb = new StringBuilder(radioId.Length);
            foreach (char c in radioId.Trim())
            {
                sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
            }
            return sb.ToString();
        }
    }
}
