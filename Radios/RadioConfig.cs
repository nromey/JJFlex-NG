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
    /// One rung of a radio's connection-path chain — the transports JJ Flex
    /// can reach a radio by. NOT the same thing as
    /// <see cref="RadioConnectionPreference"/>, which chooses HOW the
    /// SmartLink transport crosses the network (forwarded ports vs hole
    /// punch); this enum chooses WHICH transport to try. Numeric values are
    /// stable for saved configs; JJ Flexible Connect joins as a third member
    /// when it exists, without renumbering.
    /// </summary>
    public enum ConnectPathKind
    {
        /// <summary>The local network — VITA discovery and a direct TCP
        /// connection. The better path whenever it exists.</summary>
        Local = 0,

        /// <summary>FlexRadio's SmartLink service.</summary>
        SmartLink = 1,

        // Connect = 2 is reserved for JJ Flexible Connect.
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
    /// What JJ Flex should do about the radio's REM ON remote-power jack when
    /// connecting to this radio (Track C, settings that stick). REM ON is
    /// radio-persistent state, so this is a queued intent: settable with no
    /// radio present, applied at the next connection. Numeric values are
    /// stable for saved configs.
    /// </summary>
    public enum RemOnOnConnectModes
    {
        /// <summary>Leave the radio's REM ON setting alone. The default.</summary>
        LeaveAlone = 0,

        /// <summary>Make sure REM ON is enabled at each connect. The setting a
        /// no-physical-access radio wants — REM ON is the only remote way back
        /// from a powered-off radio. Does nothing physically unless the RCA
        /// jack is wired to a relay.</summary>
        TurnOn = 1,

        /// <summary>Make sure REM ON is disabled at each connect.</summary>
        TurnOff = 2,
    }

    /// <summary>
    /// Whether this radio is meant to be reachable from away (Sprint 30
    /// Track A). An APP-side setting: nothing is written to the radio and
    /// nothing is asked of SmartLink — it records what the OPERATOR intends
    /// for this radio, which is the only place that answer exists.
    ///
    /// <para>It exists because a local connect used to produce a registration
    /// complaint. Not registering is a perfectly good answer for a radio that
    /// lives in the same room as the operator, and an app that keeps asking is
    /// treating a valid choice as an unfinished task. Numeric values are
    /// stable for saved configs.</para>
    /// </summary>
    public enum SmartLinkIntents
    {
        /// <summary>No answer yet. The default, and the only value that lets
        /// the offer appear.</summary>
        Undecided = 0,

        /// <summary>"I only use this radio on my own network." Registration
        /// advisories stay quiet for this radio, permanently.</summary>
        LocalOnly = 1,

        /// <summary>"I want to reach this radio from away." Registration help
        /// stays on, because now it is help rather than nagging.</summary>
        WantsSmartLink = 2,
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
    /// Whose radio this is, as the OPERATOR declares it (Sprint 31 Track S,
    /// task #94, ratified 2026-08-19). Numeric values are stable for saved
    /// configs.
    ///
    /// <para><b>This cannot be derived, and the temptation to derive it is the
    /// whole reason the enum exists.</b> Registration was the obvious
    /// candidate — your radio if it is registered to your account — and it
    /// fails on the exact case that raised the question: Noel connected to
    /// Margaret's radio USING MARGARET'S ACCOUNT, so to SmartLink he WAS the
    /// owner. Registration answers who has ACCESS, and access and ownership
    /// diverge the moment anyone helps anyone else, which is most of what a
    /// tester pool does. Two more cases no inference can see: a LAN-only radio
    /// has no registration at all, and Don's 6300 lives at Tony's house —
    /// local to Tony, remote to Don, unambiguously Don's — so physical
    /// location does not settle it either. Discovery or registration may SEED
    /// a suggested answer at the moment of asking; neither may decide.</para>
    ///
    /// <para><b>It is a declaration of intent, not a security control.</b> The
    /// question "how do we stop someone marking a radio that is not theirs?"
    /// has one honest answer: we do not, and we should not try. JJ Flex is not
    /// defending against a malicious operator — it is protecting an honest one
    /// from an accident. Anyone who deliberately marks another person's radio
    /// as theirs has taken that on knowingly. Do not build enforcement on top
    /// of this; enforcement would cost every honest operator friction and buy
    /// nothing against the dishonest one.</para>
    /// </summary>
    public enum RadioOwnership
    {
        /// <summary>Not answered. The DEFAULT, and it means guest behaviour:
        /// nothing new is created on the radio without asking first. Also the
        /// only value that lets the question be raised, so a radio that has
        /// been answered — either way — is never asked about again.</summary>
        Unset = 0,

        /// <summary>"This radio is mine." JJ Flex may create radio-side state
        /// on it as ordinary housekeeping.</summary>
        Mine = 1,

        /// <summary>"This is someone else's radio." A real answer, not a
        /// deferral: the question stops being raised, and any action that
        /// would create new radio-side state says plainly why it is not
        /// offering itself. Reversible from the Radios tab in Settings — an
        /// operator who buys a friend's rig should not have to guess where the
        /// answer went.</summary>
        SomeoneElses = 2,
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

        /// <summary>Last known nickname as OBSERVED — the name the radio
        /// broadcasts about itself, refreshed on every sighting and connect.
        /// The operator's chosen label lives in <see cref="UserNickname"/>;
        /// this field is the observation and sightings may overwrite it
        /// freely. Display through <see cref="DisplayName"/>.</summary>
        public string Nickname { get; set; } = "";

        /// <summary>
        /// The operator's CHOSEN name for this radio, as opposed to
        /// <see cref="Nickname"/> which is what discovery observed. Set only
        /// by deliberate action (the Settings Radio Profile tab, a rename
        /// from JJ Flex) and NEVER auto-overwritten by a sighting — the same
        /// choice-versus-observation split <see cref="PreferredAccount"/> and
        /// <see cref="LastSeenViaAccount"/> already document. Empty means no
        /// choice made: display falls through to the observation. Fixes task
        /// #75, where a name set in per-radio settings was clobbered by the
        /// radio's own broadcast name at the first sighting.
        /// </summary>
        public string UserNickname { get; set; } = "";

        /// <summary>The name to SHOW for this radio: the operator's choice
        /// when one exists, otherwise the observed nickname. The operator
        /// typed theirs deliberately and recently — it wins.</summary>
        [XmlIgnore]
        public string DisplayName =>
            !string.IsNullOrWhiteSpace(UserNickname) ? UserNickname : Nickname;

        /// <summary>Connection strategy for this radio. Auto (default) follows
        /// the radio-reported flags each connect.</summary>
        public RadioConnectionPreference ConnectionPreference { get; set; }
            = RadioConnectionPreference.Auto;

        /// <summary>
        /// The operator's ordered chain of connection paths to try for this
        /// radio: first entry first, walk on failure, announcing each move.
        /// Empty (the default) means no preference has been recorded and the
        /// selector derives the order from live availability, local first —
        /// the historical behaviour, now an explicit default rather than an
        /// emergent one. A one-entry chain means "this path only, never fall
        /// back" — which is what makes force-remote a valid hole-punch test
        /// instrument. Persisted per radio; survives the radio moving
        /// networks, which is the point: the chain is the operator's intent,
        /// where <c>LastSeenRemote</c> is merely history.
        /// </summary>
        public List<ConnectPathKind> PathChain { get; set; } = new();

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

        /// <summary>
        /// "This radio is operated remotely; I cannot reach its front panel"
        /// (Track C, 2026-08-16). Geography, not networking: deliberately NOT
        /// derived from the connection path, because the failure modes are
        /// asymmetric — wrongly inferring "local" suppresses a warning that
        /// would have saved you, wrongly inferring "remote" merely shows a
        /// prompt you did not need. A safety gate opens because the operator
        /// said so. Consumers: advice text must never offer "power cycle it"
        /// for a flagged radio, firmware-update warnings sharpen, and REM ON
        /// stops being optional.
        /// </summary>
        public bool NoPhysicalAccess { get; set; }

        /// <summary>
        /// True once the operator has explicitly answered the no-physical-
        /// access question for this radio. While false, the UI may pre-populate
        /// the checkbox from the connection-path guess (and say it did), but
        /// nothing treats the guess as a decision.
        /// </summary>
        public bool NoPhysicalAccessDecided { get; set; }

        /// <summary>
        /// Queued intent for the radio's REM ON remote-power jack, applied at
        /// each connection to this radio (Track C). Default LeaveAlone: a
        /// config written before this shipped changes nothing.
        /// </summary>
        public RemOnOnConnectModes RemOnOnConnect { get; set; }
            = RemOnOnConnectModes.LeaveAlone;

        /// <summary>
        /// Whether the operator wants this radio reachable from away (Sprint
        /// 30 Track A). App-side and per-radio: one operator can have a shack
        /// rig they will never register and a remote-base rig they must.
        ///
        /// <para>Default Undecided, so a config written before this shipped
        /// behaves exactly as it did — the offer appears once and the answer
        /// sticks from then on. LocalOnly is a real answer, not a deferral:
        /// nothing about SmartLink registration is raised for this radio
        /// again, on any connect, on any run.</para>
        /// </summary>
        public SmartLinkIntents SmartLinkIntent { get; set; }
            = SmartLinkIntents.Undecided;

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

        /// <summary>
        /// The operator asked for this radio to be taken off the list, while
        /// keeping everything configured for it (task #98, the safe scope).
        ///
        /// <para>A flag rather than a deletion, because the whole point of the
        /// safe scope is that the settings survive — and deleting the profile
        /// to hide the row would be the destructive scope wearing the safe
        /// scope's label.</para>
        ///
        /// <para><b>A live sighting clears it.</b> A radio that answers is
        /// real, and a hidden reachable radio is an operator locked out of
        /// their own rig with no explanation anywhere. That is not a
        /// concession, it is the stated behaviour: hiding an ONLINE radio is
        /// nearly a no-op, and the removal UI must not promise otherwise.
        /// Where it earns its keep is the junk roster entry that will never
        /// answer again — which is exactly the case that had no escape but
        /// hand-editing AppData, something a blind operator must never be
        /// asked to do.</para>
        /// </summary>
        public bool HiddenFromList { get; set; }

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

        // ---------------------------------------------------------------
        // Where the operator left this radio (Sprint 35 Track I, #226).
        //
        // Every conventional transceiver comes back on the frequency it was
        // switched off on. A Flex driven through a client does not: it
        // replays its global profile on connect, so the operator lands
        // wherever the profile was last SAVED, and everything since is gone
        // silently. This block is the app-side memory of where the operator
        // actually was — frequency, mode, slice letter — recorded per radio,
        // per install.
        //
        // APP-SIDE AND PER-OPERATOR BY CONSTRUCTION. Nothing here is written
        // to the radio, so the MultiFlex ownership question ("whose state
        // wins") does not arise: two people sharing one radio each carry
        // their own last place in their own install. That is also why this
        // deliberately does NOT feed any automatic restore — the honest
        // first version TELLS the operator where they were and stops there.
        // Restoring is a separate decision with a real MultiFlex hazard
        // (retuning a slice somebody else is using), deferred to the
        // station-state ownership audit (project_operator_state_vs_
        // station_state). Keep this block shaped so an identity-aware
        // answer can replace it rather than migrate it: it is a cache of
        // observation, never a data model anything else depends on.
        //
        // APPEND-ONLY like the blocks around it: absent elements deserialize
        // to defaults, so a config.xml written before this shipped loads
        // unchanged and reads as "no last place known".
        // ---------------------------------------------------------------

        /// <summary>True once a last place has ever been recorded for this
        /// radio. While false, the other LastPlace fields mean nothing.</summary>
        public bool LastPlaceKnown { get; set; }

        /// <summary>Frequency the operator's active slice was on, in Hz.</summary>
        public ulong LastPlaceFrequencyHz { get; set; }

        /// <summary>Demodulation mode the active slice was in ("USB", "CW").</summary>
        public string LastPlaceMode { get; set; } = "";

        /// <summary>Letter of the slice that was active ("A").</summary>
        public string LastPlaceSliceLetter { get; set; } = "";

        /// <summary>When the place last CHANGED (UTC) — unchanged sessions skip
        /// the write, so this is not "when last connected".</summary>
        public DateTime LastPlaceRecordedUtc { get; set; }

        /// <summary>
        /// Record where the operator is on a radio. Skips the disk write when
        /// the place has not changed — an evening parked on one frequency
        /// costs one write, not one per debounce tick. No-ops on an unknown
        /// radio id or an empty place, and never throws: losing one place
        /// observation must not be able to hurt anything else.
        /// </summary>
        public static void RecordLastPlace(
            string radioId, ulong frequencyHz, string mode, string sliceLetter)
        {
            if (string.IsNullOrEmpty(radioId)) return;
            if (frequencyHz == 0 || string.IsNullOrEmpty(mode)) return;
            try
            {
                var cfg = LoadForRadio(radioId);
                if (cfg.LastPlaceKnown
                    && cfg.LastPlaceFrequencyHz == frequencyHz
                    && cfg.LastPlaceMode == mode
                    && cfg.LastPlaceSliceLetter == (sliceLetter ?? ""))
                {
                    return;
                }
                cfg.LastPlaceKnown = true;
                cfg.LastPlaceFrequencyHz = frequencyHz;
                cfg.LastPlaceMode = mode;
                cfg.LastPlaceSliceLetter = sliceLetter ?? "";
                cfg.LastPlaceRecordedUtc = DateTime.UtcNow;
                cfg.SaveForRadio(radioId);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    "RadioConfig.RecordLastPlace: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        // ---------------------------------------------------------------
        // Ownership (Sprint 31 Track S, 2026-08-19, task #94).
        //
        // The layer beneath the microphone-profile model: the app had no
        // concept of WHOSE RADIO it was connected to, and two real behaviours
        // needed one. See the RadioOwnership enum for why it cannot be
        // inferred and why it is not a security control.
        //
        // APPEND-ONLY, like the roster block above: an absent element
        // deserialises to Unset, which is the safe guest default, so every
        // config.xml written before this shipped behaves exactly as it did.
        // ---------------------------------------------------------------

        /// <summary>
        /// Whose radio this is, as the operator declared it. Default Unset —
        /// guest behaviour, and the state in which the question may be raised.
        /// </summary>
        public RadioOwnership Ownership { get; set; } = RadioOwnership.Unset;

        /// <summary>
        /// Whether JJ Flex may CREATE new state on this radio without stopping
        /// to ask: profiles, settings that persist for every client, anything
        /// the operator did not individually request.
        ///
        /// <para><b>Applying an existing binding is deliberately NOT gated on
        /// this.</b> A microphone profile that names a radio-side mic profile
        /// for THIS radio was bound by the operator, on this radio, earlier —
        /// the binding IS the consent, and re-checking it here would break a
        /// working setup on the day the flag shipped. What this gates is
        /// creating radio-side state that does not exist yet, and unrequested
        /// housekeeping writes. (Ratified 2026-08-19; see
        /// docs/planning/design/Mic-Profile-Ownership.md.)</para>
        /// </summary>
        [XmlIgnore]
        public bool MayCreateRadioSideState => Ownership == RadioOwnership.Mine;

        /// <summary>
        /// True when the operator has answered the ownership question for this
        /// radio, either way. The condition for NOT asking again.
        /// </summary>
        [XmlIgnore]
        public bool OwnershipAnswered => Ownership != RadioOwnership.Unset;

        /// <summary>Ownership for a radio id, without the caller loading a
        /// whole config. Unknown ids read as Unset, which is the safe
        /// answer.</summary>
        public static RadioOwnership OwnershipOf(string radioId)
        {
            if (string.IsNullOrEmpty(radioId)) return RadioOwnership.Unset;
            return LoadForRadio(radioId).Ownership;
        }

        /// <summary>
        /// Record the operator's ownership answer for a radio. Skips the disk
        /// write when nothing changed. Returns false only when the value could
        /// not reach disk — callers should still honour the answer for this
        /// session, per SaveForRadio's contract.
        /// </summary>
        public static bool RecordOwnership(string radioId, RadioOwnership value)
        {
            if (string.IsNullOrEmpty(radioId)) return false;
            var cfg = LoadForRadio(radioId);
            if (cfg.Ownership == value) return true;
            cfg.Ownership = value;
            Tracing.TraceLine(
                $"RadioConfig: ownership of {radioId} declared {value} by the operator.",
                System.Diagnostics.TraceLevel.Info);
            return cfg.SaveForRadio(radioId);
        }

        /// <summary>
        /// What to PRE-SELECT when the ownership question is asked — never
        /// what to store. A radio last seen through the operator's own
        /// preferred SmartLink account, or one that has only ever been seen on
        /// the local network, are both weak evidence of "mine"; neither is
        /// proof, and the caller must present the suggestion AS a suggestion.
        /// Returns Unset when nothing worth suggesting is known.
        /// </summary>
        /// <param name="operatorAccount">The SmartLink account the operator
        /// signs in as, or empty when they use none.</param>
        public RadioOwnership SuggestOwnership(string? operatorAccount)
        {
            // Seen over SmartLink through an account that is not the
            // operator's own is the one signal that points AWAY from "mine" —
            // and even that is only a hint, because a shared club account
            // exists and a borrowed login exists. Never returns SomeoneElses:
            // suggesting "not yours" about a radio that IS yours would teach
            // the operator to dismiss this question without reading it.
            if (!string.IsNullOrEmpty(operatorAccount)
                && !string.IsNullOrEmpty(LastSeenViaAccount)
                && !string.Equals(LastSeenViaAccount, operatorAccount,
                                  StringComparison.OrdinalIgnoreCase))
            {
                return RadioOwnership.Unset;
            }

            if (!string.IsNullOrEmpty(operatorAccount)
                && string.Equals(LastSeenViaAccount, operatorAccount,
                                 StringComparison.OrdinalIgnoreCase))
            {
                return RadioOwnership.Mine;
            }

            // Local-only sighting: no registration exists to reason from at
            // all, which is exactly the case that killed the derive-it idea.
            // A radio on your own network, never seen remotely, is still the
            // most likely thing to be yours.
            if (!LastSeenRemote && LastSeenUtc != DateTime.MinValue)
            {
                return RadioOwnership.Mine;
            }

            return RadioOwnership.Unset;
        }

        /// <summary>
        /// App-wide config root, assigned once at startup (ApplicationEvents,
        /// next to the other handler wiring). Static because the Radios layer
        /// has no ambient config-path service and the value never changes for
        /// the life of the process. When unset, LoadForRadio returns defaults
        /// and SaveForRadio declines — callers never need a null check.
        /// </summary>
        public static string? BaseDirectory { get; set; }

        /// <summary>
        /// The app's AppData folder name. Must match <c>InternalName</c> in
        /// globals.vb, which is what startup combines with ApplicationData to
        /// build BaseConfigDir. If these two ever disagree, the fallback below
        /// writes settings to a directory nothing reads — which is worse than
        /// not saving at all, because it looks like success.
        /// </summary>
        private const string AppDataFolderName = "JJFlexRadio";

        /// <summary>
        /// The config root, self-healed if startup never set it.
        ///
        /// An unset <see cref="BaseDirectory"/> is OUR defect, never the
        /// operator's, and it is not hypothetical: on 2026-08-06 the assignment
        /// sat in radio-window wiring instead of startup, so every connect-time
        /// load returned defaults and every save silently declined — the whole
        /// per-radio feature was inert exactly where it mattered, and nothing
        /// said so. Deriving the path costs nothing and cannot be wrong as long
        /// as <see cref="AppDataFolderName"/> tracks globals.vb.
        ///
        /// Traced at Error, not Warning: reaching this means startup ordering
        /// broke and someone should fix it. It must never reach speech, though
        /// — the operator did nothing and can do nothing about it.
        /// </summary>
        private static string? ResolveBaseDirectory()
        {
            var dir = BaseDirectory;
            if (!string.IsNullOrEmpty(dir)) return dir;

            try
            {
                var derived = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AppDataFolderName);
                Tracing.TraceLine(
                    "RadioConfig: BaseDirectory was never set by startup — deriving "
                    + derived + ". This is a startup-ordering defect; per-radio settings "
                    + "would otherwise have silently stopped persisting.",
                    System.Diagnostics.TraceLevel.Error);
                BaseDirectory = derived;
                return derived;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"RadioConfig: could not derive a config directory: {ex.Message}",
                    System.Diagnostics.TraceLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// The config root actually in use, self-healed exactly as the per-radio
        /// load and save paths do it.
        ///
        /// <para>Exists so that other app-level stores under this root
        /// (<see cref="ConnectPathLearningConfig"/>) read and write the SAME
        /// directory these methods do. Two stores resolving the root two
        /// different ways is the failure that looks like success: the save
        /// reports fine and lands somewhere nothing reads.</para>
        /// </summary>
        public static string? ResolvedBaseDirectory => ResolveBaseDirectory();

        /// <summary>
        /// The environment variable that moves the app's ENTIRE settings tree
        /// somewhere else for one run.
        /// </summary>
        public const string ConfigDirOverrideVariable = "JJFLEX_CONFIG_DIR";

        private static string? _appDataRoot;

        /// <summary>
        /// The one true settings root — <c>%AppData%\JJFlexRadio</c>, or the
        /// throwaway tree when <see cref="ConfigDirOverrideVariable"/> names
        /// one. EVERY app-owned store under that root must come through here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Read this before writing <c>Environment.GetFolderPath(
        /// SpecialFolder.ApplicationData)</c> anywhere in the app again.</b>
        /// On 2026-08-22 a sweep found twenty places doing exactly that and
        /// appending "JJFlexRadio" themselves. Each worked perfectly and each
        /// was invisible to any attempt to relocate the tree, so the first
        /// isolated run reported "temporary settings in use" — truthfully, for
        /// the one directory it governed — while the rest wrote the operator's
        /// live folder regardless.
        /// </para>
        /// <para>
        /// That is the same defect <see cref="ResolvedBaseDirectory"/> was
        /// written to stop, one layer up: "two stores resolving the root two
        /// different ways is the failure that looks like success."
        /// </para>
        /// <para>
        /// <b>Resolved from the environment, NOT from startup state, and that
        /// is load-bearing.</b> Several callers hold their folder in a
        /// <c>static readonly</c> field evaluated at type-load, which can
        /// happen before startup runs. Anything depending on "GetConfigInfo
        /// already assigned it" would bind the wrong root, sometimes, based on
        /// which type someone touched first — a bug that reproduces on Tuesday
        /// and not on Wednesday. Reading the variable makes order irrelevant.
        /// </para>
        /// <para>
        /// Cached after the first read: a process cannot meaningfully change
        /// its own settings root mid-run, and every store caching a different
        /// snapshot of it would reintroduce the very split this closes.
        /// <see cref="ForgetAppDataRoot"/> exists for tests only.
        /// </para>
        /// </remarks>
        public static string AppDataRoot
        {
            get
            {
                return _appDataRoot ??= ComputeAppDataRoot();
            }
        }

        /// <summary>Drop the cached root. Tests only.</summary>
        internal static void ForgetAppDataRoot() => _appDataRoot = null;

        private static string ComputeAppDataRoot()
        {
            string standard;
            try
            {
                standard = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AppDataFolderName);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    "RadioConfig: could not resolve the settings root: " + ex.Message,
                    System.Diagnostics.TraceLevel.Error);
                return "";
            }

            string resolved = ResolveStartupDirectory(
                standard,
                Environment.GetEnvironmentVariable(ConfigDirOverrideVariable),
                out bool isTemporary,
                out string? refusal);

            if (refusal != null)
            {
                Tracing.TraceLine("RadioConfig: " + refusal, System.Diagnostics.TraceLevel.Warning);
            }
            else if (isTemporary)
            {
                Tracing.TraceLine(
                    "RadioConfig: settings root redirected to " + resolved +
                    " by " + ConfigDirOverrideVariable + ".",
                    System.Diagnostics.TraceLevel.Warning);
            }

            return resolved;
        }

        /// <summary>
        /// Decide where this run's settings live: normally
        /// <paramref name="defaultDirectory"/>, or somewhere temporary when
        /// <see cref="ConfigDirOverrideVariable"/> names a place.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this exists.</b> Until 2026-08-22 there was no way to point a
        /// SPAWNED build anywhere but the operator's one live
        /// <c>%AppData%\JJFlexRadio</c>. Tests could set
        /// <see cref="BaseDirectory"/> because they run in-process; a launched
        /// exe could not. So every instance, from every build in every
        /// worktree, read and wrote the operator's real settings.
        /// </para>
        /// <para>
        /// On 2026-08-21 a background agent's worktree build rewrote the
        /// operator's <c>KeyDefs.xml</c>, and because no copy existed anywhere,
        /// "did that damage anything?" could not be answered even afterwards.
        /// That is what this closes: an automated run gets its own tree, and
        /// the operator's settings are not in the blast radius.
        /// </para>
        /// <para>
        /// <b>It must never engage by accident, and never engage silently.</b>
        /// Hence the guards below and <paramref name="isTemporary"/>, which the
        /// caller uses to say so out loud. An app quietly running on settings
        /// that are not yours is precisely the failure that looks like success.
        /// </para>
        /// </remarks>
        /// <param name="defaultDirectory">Where settings live normally.</param>
        /// <param name="overrideValue">
        /// The variable's value. Passed in rather than read here so this is
        /// testable without touching process environment.
        /// </param>
        /// <param name="isTemporary">True when the override took effect.</param>
        /// <param name="refusal">
        /// When an override was offered and REFUSED, why. Null otherwise. The
        /// caller must surface this: a rejected override that falls back
        /// silently would run against live settings while the person who set it
        /// believes otherwise, which is worse than either outcome alone.
        /// </param>
        public static string ResolveStartupDirectory(
            string defaultDirectory, string? overrideValue, out bool isTemporary, out string? refusal)
        {
            isTemporary = false;
            refusal = null;

            if (string.IsNullOrWhiteSpace(overrideValue)) return defaultDirectory;

            string candidate = overrideValue!.Trim();

            // A relative path depends on the working directory, which for a
            // spawned process is whatever the launcher happened to be in. That
            // is not a location, it is a guess.
            if (!Path.IsPathRooted(candidate))
            {
                refusal = ConfigDirOverrideVariable + " was set to a relative path (" + candidate +
                          "), which depends on the working directory. Use a full path. " +
                          "Continuing with the normal settings folder.";
                return defaultDirectory;
            }

            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                refusal = ConfigDirOverrideVariable + " was set to something that is not a usable path (" +
                          candidate + "): " + ex.Message + ". Continuing with the normal settings folder.";
                return defaultDirectory;
            }

            // Pointing the override AT the real folder is almost certainly a
            // mistake, and letting it through would report "temporary" while
            // writing the operator's live settings — the exact lie this guards.
            if (!string.IsNullOrEmpty(defaultDirectory) &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(full),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultDirectory)),
                    StringComparison.OrdinalIgnoreCase))
            {
                refusal = ConfigDirOverrideVariable + " points at the normal settings folder, so it would " +
                          "not isolate anything. Continuing with the normal settings folder.";
                return defaultDirectory;
            }

            isTemporary = true;
            return full;
        }

        /// <summary>Load via the app-wide <see cref="BaseDirectory"/>.</summary>
        public static RadioConfig LoadForRadio(string radioId)
        {
            var dir = ResolveBaseDirectory();
            return string.IsNullOrEmpty(dir)
                ? new RadioConfig { RadioId = radioId }
                : Load(dir, radioId);
        }

        /// <summary>
        /// Save via the app-wide <see cref="BaseDirectory"/>, self-healing an
        /// unset root and retrying once before admitting failure.
        ///
        /// The retry exists because the realistic transient is a lock, not a
        /// permanent fault: this app supports multiple instances, and an
        /// antivirus scanner opening the file mid-write looks identical. Both
        /// clear in tens of milliseconds. The delay is safe here — every caller
        /// is a UI action or a connect step, never an audio callback.
        ///
        /// **Callers: a false return means the value did not reach disk. It does
        /// NOT mean the operator's choice should be discarded.** Apply it in
        /// memory anyway and say plainly that it may not survive a restart —
        /// refusing an intent because the disk was busy is the disk's problem
        /// being handed to the operator.
        /// </summary>
        public bool SaveForRadio(string radioId)
        {
            var dir = ResolveBaseDirectory();
            if (string.IsNullOrEmpty(dir))
            {
                Tracing.TraceLine(
                    "RadioConfig.SaveForRadio: no config directory available — nothing saved",
                    System.Diagnostics.TraceLevel.Error);
                return false;
            }

            if (Save(dir, radioId)) return true;

            System.Threading.Thread.Sleep(50);
            if (Save(dir, radioId))
            {
                Tracing.TraceLine(
                    $"RadioConfig.SaveForRadio: {radioId} succeeded on retry — "
                    + "first attempt hit a transient (likely a file lock).",
                    System.Diagnostics.TraceLevel.Warning);
                return true;
            }

            Tracing.TraceLine(
                $"RadioConfig.SaveForRadio: {radioId} failed twice — the setting is "
                + "live for this session but will not survive a restart.",
                System.Diagnostics.TraceLevel.Error);

            // Offer the operator the evidence, here rather than at the call
            // sites. This is the same reasoning that put the retry here in the
            // first place (#77): every caller shares the failure, so every
            // caller would otherwise have to remember to report it, and the one
            // that forgets is the one that fails silently.
            //
            // Deliberately kind-level rather than per-setting: DiagnosticOffer
            // announces at most one problem per kind per session, so a settings
            // pass that fails on six fields speaks once, not six times. All six
            // are still recorded in the Problems list — the cap is on speech,
            // never on what is kept.
            OperationFailure.Report(
                FailureKind.SettingNotSaved,
                "A radio setting could not be saved",
                "The change is in effect right now, but it will not be there the "
                + "next time you start JJ Flex.");
            return false;
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

            // Sprint 33 Track J. '.' is an allowed character, so an id of ".."
            // survived sanitising intact and Path.Combine(base, "radios", "..")
            // resolves to the base directory itself. KnownRadioRoster.Remove
            // does a recursive delete on exactly that path under the
            // destructive scope, which would have taken every radio's settings
            // rather than one radio's. That scope only became selectable in
            // Sprint 32 Track G, so this was newly reachable rather than a
            // long-standing live hazard -- but a directory id that is nothing
            // but dots is never a real radio and has no business round-tripping.
            var sanitized = sb.ToString();
            if (sanitized.Trim('.').Length == 0)
            {
                return "_unknown";
            }
            return sanitized;
        }
    }
}
