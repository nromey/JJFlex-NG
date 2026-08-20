using System;
using System.Collections.Generic;
using System.Linq;
using JJFlex.RigSurface;

namespace JJFlex.TxFactAudit
{
    /// <summary>
    /// How a fact can be proved, which is not the same question as what it
    /// means. The analyzer's credibility does not rest on the rules — those are
    /// tested — it rests on each fact being wired to the thing it claims to
    /// describe, and this says how we would find out.
    /// </summary>
    public enum Provenance
    {
        /// <summary>The radio publishes it as a status key. Read it from a second
        /// connection and compare: no ambiguity, no inference.</summary>
        WireField,

        /// <summary>The radio publishes a descriptor for the meter — name, source,
        /// description, units, range — over the command channel. Identity is
        /// fully provable this way even though the READING is not.</summary>
        MeterDescriptor,

        /// <summary>The reading itself. It never crosses the command channel —
        /// it arrives as VITA-49 UDP to a client that registered a stream — so
        /// the identity is provable here and the VALUE is read out of the
        /// application's own trace instead. Deliberately not out of a stream of
        /// our own: a second client sees a different meter set, and cannot
        /// testify about what the application knew.</summary>
        MeterValue,

        /// <summary>State that lives in JJ Flexible, not on the radio. No wire
        /// key exists and none should; verified against the app, not the rig.</summary>
        AppLocal,

        /// <summary>Windows audio state. Nothing on the radio knows about it.</summary>
        PcLocal,

        /// <summary>The radio answers it, but on a topic this harness does not
        /// parse. Named honestly rather than silently omitted.</summary>
        WireTopicNotParsed,

        /// <summary>Not a status key at all: it arrives in the discovery beacon
        /// that creates the Radio object, and is never re-sent on the command
        /// channel. A second command-channel client cannot see it, and neither
        /// can the first — it is a connect-time value held forever.</summary>
        DiscoveryBeacon,
    }

    /// <summary>
    /// Whose state the fact describes. Straight from Track C's
    /// <see cref="StateOwnership"/> vocabulary — deliberately the same three
    /// words, because there must be exactly one classification of radio state
    /// in this sprint and it is Track C's.
    /// </summary>
    public enum FactOwnership
    {
        /// <summary>One value for the whole radio. Reading it from any connection
        /// is honest.</summary>
        StationGlobal,

        /// <summary>Lives on an object carrying a client handle. Globally
        /// observable, privately owned — so a second connection may READ it, but
        /// only by attributing the object to the application's handle. A tool
        /// that makes its own object and reports on that has proved nothing.</summary>
        ClientOwned,

        /// <summary>The radio reporting on itself. Never written.</summary>
        Telemetry,

        /// <summary>Not radio state at all.</summary>
        NotRadioState,
    }

    /// <summary>
    /// What the fact does when the thing behind it has never reported.
    ///
    /// <para>This is the whole point of the audit. A rules engine that is
    /// provably honest about unreadable facts is defeated completely by a fact
    /// that lies about its own readability, and the lie is invisible: a zero
    /// that means "no reading" and a zero that means "actually zero" are the
    /// same bits.</para>
    /// </summary>
    public enum IdleHonesty
    {
        /// <summary>Always readable once connected. There is no idle state to be
        /// dishonest about — a setting the radio holds is current by
        /// definition.</summary>
        NoIdleState,

        /// <summary>Publishes Absent or Silent when it has nothing. Honest.</summary>
        Gated,

        /// <summary>Publishes a number that reads exactly like a measurement when
        /// nothing has been measured. This is the defect class.</summary>
        Fabricates,
    }

    /// <summary>
    /// One fact the transmit-chain analyzer collects, and everything needed to
    /// find out whether it tells the truth on a live radio.
    /// </summary>
    public sealed record FactSpec
    {
        /// <summary>The name rules use.</summary>
        public required string Name { get; init; }

        /// <summary>The label the operator hears in the evidence block.</summary>
        public required string Label { get; init; }

        /// <summary>The property the fact source reads, as written in
        /// TxChainFacts.cs.</summary>
        public required string AppMember { get; init; }

        /// <summary>The FlexLib property behind it, or a note when there is
        /// none.</summary>
        public required string LibMember { get; init; }

        /// <summary>The radio's own status key, when the fact has one.</summary>
        public RigField? Wire { get; init; }

        /// <summary>The radio's name for the meter, when the fact reads one.</summary>
        public string? Meter { get; init; }

        public required Provenance Provenance { get; init; }

        public required FactOwnership Ownership { get; init; }

        public required IdleHonesty Idle { get; init; }

        /// <summary>What the fact publishes before anything has reported, in
        /// the operator's units. Empty when <see cref="Idle"/> is
        /// <see cref="IdleHonesty.NoIdleState"/> or Gated.</summary>
        public string IdleReads { get; init; } = "";

        /// <summary>How to prove it, in one sentence. Read aloud in the map.</summary>
        public required string Proof { get; init; }

        /// <summary>Anything already known to be wrong or suspicious, stated
        /// plainly. Empty when nothing is known against it.</summary>
        public string Concern { get; init; } = "";

        /// <summary>
        /// Why this fact's ownership class deliberately differs from the class
        /// Track C's table gives its underlying wire key.
        /// <para>There is exactly one of these and it is the whole point of
        /// having it: <c>interlock.state</c> really is telemetry, but the fact
        /// built on it is gated on the transmitting client being ours, which
        /// makes the FACT client-owned even though the KEY is not. Declaring
        /// the divergence keeps the cross-check meaningful instead of teaching
        /// everyone to ignore a permanent warning.</para>
        /// </summary>
        public string WhyOwnershipDiffers { get; init; } = "";

        /// <summary>
        /// True when Sprint 33 Track D corrected this fact, and
        /// <see cref="Concern"/> therefore describes what it USED to do.
        /// <para>Kept rather than deleted on purpose. A defect that has been
        /// fixed and forgotten gets reintroduced; a defect recorded beside the
        /// fact it lived on does not.</para>
        /// </summary>
        public bool FixedHere { get; init; }
    }

    /// <summary>
    /// Every fact the transmit-chain analyzer collects, with its wiring traced
    /// from the rule name through JJ Flexible and FlexLib to the radio's own
    /// status key or meter.
    ///
    /// <para><b>Why this exists as data rather than as a document.</b> A
    /// document about wiring goes stale the first time somebody moves a
    /// property, and nothing notices. This is checked against the radio and
    /// against Track C's ownership table on every run, so a fact whose wire key
    /// stops existing is a failing line rather than a paragraph nobody
    /// re-reads.</para>
    ///
    /// <para>The list is kept in the same order as
    /// <c>TxChainFacts.Collect</c> states them, which is signal-path order, so
    /// the audit reads as a walk along the transmit chain.</para>
    /// </summary>
    public static class FactMap
    {
        private static readonly List<FactSpec> _all = Build();

        public static IReadOnlyList<FactSpec> All => _all;

        public static FactSpec? Find(string name) =>
            _all.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        private static List<FactSpec> Build()
        {
            var list = new List<FactSpec>();

            void F(string name, string label, string appMember, string libMember,
                   Provenance provenance, FactOwnership ownership, IdleHonesty idle,
                   string proof, RigField? wire = null, string? meter = null,
                   string idleReads = "", string concern = "", string whyOwnershipDiffers = "",
                   bool fixedHere = false)
            {
                list.Add(new FactSpec
                {
                    Name = name,
                    Label = label,
                    AppMember = appMember,
                    LibMember = libMember,
                    Wire = wire,
                    Meter = meter,
                    Provenance = provenance,
                    Ownership = ownership,
                    Idle = idle,
                    IdleReads = idleReads,
                    Proof = proof,
                    Concern = concern,
                    WhyOwnershipDiffers = whyOwnershipDiffers,
                    FixedHere = fixedHere,
                });
            }

            // ── Connection and identity ───────────────────────────────────
            F("radio-connected", "A radio is connected",
              "FlexBase.IsConnected", "Radio.Connected via radioPropertyChangedHandler",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "Trivially true whenever anything else can be read. Nothing to prove separately.");

            F("radio-model", "Radio model",
              "FlexBase.RadioModel", "Radio.Model",
              Provenance.DiscoveryBeacon, FactOwnership.Telemetry, IdleHonesty.NoIdleState,
              "NOT a status key. FlexLib's radio-status parser handles no 'model' key at all; the model arrives in the discovery beacon that creates the Radio object. Compare against the beacon or the radio's front panel, not against the command channel.");

            F("radio-serial", "Radio serial number",
              "FlexBase.SelectedRadioSerial", "app-side selection, not a FlexLib read",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "Deliberately survives a disconnect so a dead-connection report still names the radio. Compare against the radio's own serial when connected.");

            F("radio-firmware", "Radio firmware version",
              "FlexBase.RadioFirmwareVersion", "Radio.Version",
              Provenance.DiscoveryBeacon, FactOwnership.Telemetry, IdleHonesty.NoIdleState,
              "NOT a status key either. Version is set when a discovery packet creates the Radio object, so it is a connect-time snapshot rather than a live read.",
              fixedHere: true,
              concern: "WAS: when Version is zero the property returns an empty string, and the fact published that as an OBSERVED empty value. The evidence block then reads 'Radio firmware version: empty' rather than saying it could not be read.");

            F("radio-nickname", "Radio nickname",
              "FlexBase.RadioNickname", "Radio.Nickname",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Compare with radio.nickname, and change it on the wire to watch the fact follow.",
              RigField.Radio("nickname"));

            F("radio-callsign", "Callsign set on the radio",
              "FlexBase.RadioCallsign", "Radio.Callsign",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Compare with radio.callsign.",
              RigField.Radio("callsign"));

            F("connection", "How the radio is connected",
              "FlexBase.RemoteRig", "Radio.IsWan",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "A property of this client's path, not of the radio. Two clients on different paths both read correctly and differently.");

            F("meter-count", "Meters this radio publishes",
              "FlexBase.MeterInventory.Count", "Radio.GetMeters",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.NoIdleState,
              "Count the meter descriptors the radio sends on the wire and compare. The list GROWS during registration, so both counts must be taken after the stream settles.");

            // ── Stage 4: audio leaving this computer ──────────────────────
            F("pc-audio", "Radio audio through this computer",
              "FlexBase.PCAudio", "none — app intent",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "Cannot be checked against the radio and must not be: it is INTENT, true from the moment the setter runs even if the audio thread has died.",
              concern: "Named as if it described liveness. TxChainFacts already says so in a comment; the fact's LABEL does not.");

            F("pc-tx-path-trouble", "What the app says is wrong with the computer transmit path",
              "FlexBase.TxTonePathTrouble", "none — app diagnosis",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "App-side string. Verified by inducing the trouble it names, not from the radio.");

            F("pc-tx-audio-flowing", "Sound from this computer is reaching the transmit stream",
              "FlexBase.TxLufsAvailable", "none — PC-side loudness meter",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "Gated on PCAudio, mic_selection being PC, and samples within the last half second. The mic_selection half IS checkable on the wire.",
              wire: RigField.Transmit("mic_selection"));

            F("pc-tx-loudness", "Loudness of the transmit audio leaving this computer",
              "FlexBase.TxLufsShortTerm", "none — PC-side loudness meter",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "Gated on TxLufsSampleAvailable and returns Silent otherwise. Cross-check against the radio's SC_MIC during a known tone: the bench anchor is a minus ten dBFS tone reading about minus eleven.");

            // ── Stage 8: which input the radio listens to ─────────────────
            F("mic-source", "Microphone input selected on the radio",
              "FlexBase.MicSource", "Radio.MicInput",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.Gated,
              "Change transmit mic_selection on the wire and watch the fact follow. Empty is reported Absent rather than as a selection of nothing.",
              RigField.Transmit("mic_selection"));

            F("mic-source-options", "Microphone inputs this radio offers",
              "FlexBase.MicSourceList", "Radio.MicInputList",
              Provenance.WireTopicNotParsed, FactOwnership.StationGlobal, IdleHonesty.Gated,
              "The list arrives as a reply to 'mic list', not as status, so this harness reads it with a direct command rather than from the status cache.");

            // ── Stage 9: the mic profile ──────────────────────────────────
            F("mic-profile", "Mic profile selected on the radio",
              "FlexBase.CurrentMicProfileName", "Radio.ProfileMICSelection",
              Provenance.WireTopicNotParsed, FactOwnership.StationGlobal, IdleHonesty.Gated,
              "Profile status arrives on the 'profile' topic. Careful already: reports Absent until the radio has positively listed profiles, so a slow subscription cannot be mistaken for the pcap-confirmed empty-selection fault.");

            F("mic-profile-empty", "The radio has no mic profile selected",
              "FlexBase.MicProfileSelectionEmpty", "Radio.ProfileMICSelection plus ProfileMICList",
              Provenance.WireTopicNotParsed, FactOwnership.StationGlobal, IdleHonesty.Gated,
              "The one fact with a pcap behind it. True only once the radio has listed profiles AND its selection is empty.");

            F("mic-profile-count", "Mic profiles this radio offers",
              "FlexBase.MicProfileNames.Count", "Radio.ProfileMICList",
              Provenance.WireTopicNotParsed, FactOwnership.StationGlobal, IdleHonesty.Gated,
              "Count the names on the profile topic and compare.",
              idleReads: "0",
              fixedHere: true,
              concern: "WAS: published zero before the radio has listed anything, and zero profiles is also a real and reportable state. The sibling mic-profile fact gates on exactly this and this one does not.");

            F("mic-profile-suggested", "Mic profile the radio would load",
              "FlexBase.SuggestedMicProfileName", "app-side derivation from the profile list",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "An app suggestion, not a radio state. Correct that it is labelled as what the radio WOULD load rather than what it has.");

            // ── Stage 10: the radio's own transmit chain ──────────────────
            F("mic-gain", "Mic gain on the radio",
              "FlexBase.MicGain", "Radio.MicLevel",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Set transmit mic_level to a distinctive value on the wire and read the fact back.",
              RigField.Transmit("mic_level"),
              concern: "Published with EMPTY units. It is a zero to a hundred scale and the evidence block says so nowhere.");

            F("mic-boost", "Mic boost on the radio",
              "FlexBase.MicBoost", "Radio.MicBoost",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Toggle mic boost on the wire and read the fact back. Note the radio writes it with the top-level 'mic' verb, not 'transmit set'.",
              RigField.Transmit("mic_boost"));

            F("mic-bias", "Mic bias on the radio",
              "FlexBase.MicBias", "Radio.MicBias",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Toggle mic bias on the wire and read the fact back.",
              RigField.Transmit("mic_bias"));

            F("speech-processor", "Speech processor",
              "FlexBase.ProcessorOn", "Radio.SpeechProcessorEnable",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Toggle speech_processor_enable on the wire and read the fact back.",
              RigField.Transmit("speech_processor_enable"));

            F("speech-processor-level", "Speech processor level",
              "FlexBase.ProcessorSetting", "Radio.SpeechProcessorLevel",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Read the raw number off the wire and compare it with the word the fact prints. THE point of this one: FlexBase casts the number to a three-value enum (NOR, DX, DXX) while FlexLib clamps it to a zero-to-hundred range, so if the radio ever means a percentage the fact prints a confident wrong word.",
              RigField.Transmit("speech_processor_level"),
              concern: "A number cast to a three-name enum. Any value above two prints as a bare number with no units and no clue that it is not a level name.");

            F("compander", "Compander",
              "FlexBase.Compander", "Radio.CompanderOn",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Toggle compander on the wire and read the fact back.",
              RigField.Transmit("compander"));

            F("tx-filter-low", "Transmit filter low cut",
              "FlexBase.TXFilterLow", "Radio.TXFilterLow",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "The radio spells this 'lo', not 'filter_low'. Set it on the wire and read the fact back in Hz.",
              RigField.Transmit("lo"));

            F("tx-filter-high", "Transmit filter high cut",
              "FlexBase.TXFilterHigh", "Radio.TXFilterHigh",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "The radio spells this 'hi'. Set it on the wire and read the fact back in Hz.",
              RigField.Transmit("hi"));

            F("tx-filter-width", "Transmit filter width",
              "FlexBase.TXFilterHigh minus TXFilterLow", "derived",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "Derived. Correct exactly when both edges are, and verified by the same change.",
              RigField.Transmit("hi"));

            F("tx-eq", "Transmit equalizer",
              "FlexBase.GetTxEq().Enabled", "Radio transmit equaliser state",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.Gated,
              "The radio publishes it on the 'eq txsc' topic. Returns Absent until the radio has answered, which is the right shape.");

            F("tx-monitor", "Transmit monitor",
              "FlexBase.Monitor", "Radio.TXMonitor",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.NoIdleState,
              "The radio spells this 'sb_monitor' in status and takes 'transmit set mon=' as the write. Toggle it and read the fact back.",
              RigField.Transmit("sb_monitor"));

            F("tx-tone-armed", "Transmit test tone armed",
              "FlexBase.TxToneEngaged", "none — the app's own tone generator",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "The app injects the tone into the PC audio path, so nothing on the radio knows it exists. Verified in the app.");

            // ── Stage 11: what the radio says it hears ────────────────────
            F("sc-mic-peak", "Loudest transmit audio the radio has heard",
              "FlexBase.ScMicMaxDb", "Meter SC_MIC DataReady, peak-held",
              Provenance.MeterValue, FactOwnership.Telemetry, IdleHonesty.Gated,
              "VERIFIED from the application's trace, 2026-08-20: SC_MIC moved from the -150 sentinel to -10.8 dBFS on tone, and the peak-hold sat at -10.7 across the transmission. Identity from the descriptor: SC_MIC is the mic OUTPUT, downstream of mic selection, so it hears PC audio and the analog jack alike.",
              meter: "SC_MIC");

            F("sc-mic-recent", "Transmit audio the radio heard in the last second and a half",
              "FlexBase.ScMicRecentDb", "Meter SC_MIC DataReady, rolling peak-hold",
              Provenance.MeterValue, FactOwnership.Telemetry, IdleHonesty.Gated,
              "VERIFIED from the application's trace, 2026-08-20: -150 on the first second of a transmission, then -10.8 dBFS for the rest of it. Still stays a number at the floor once the meter is known to have reported, because a LIVE meter at its floor while transmitting is THE finding rather than an absence of one.",
              meter: "SC_MIC",
              fixedHere: true,
              concern: "WAS: gated on the meter EXISTING, while the value only ever moves if hookTxMeters found and subscribed it. Those are two different conditions and only one is checked.");

            F("sw-alc", "Transmit drive after the radio's own levelling",
              "FlexBase.SwAlcDb", "Meter ALC DataReady",
              Provenance.MeterValue, FactOwnership.Telemetry, IdleHonesty.Gated,
              "VERIFIED from the application's trace, 2026-08-20: SWALC moved from the -150 sentinel to between -4.9 and -2.8 dBFS across a transmission, at instants where SC_MIC read -10.8 — so it is live, moving, and genuinely a different meter. That is also the independent proof for the Peak Watcher finding: the real transmit-drive meter reports perfectly well, and nothing watches it.",
              meter: "ALC",
              fixedHere: true,
              concern: "WAS: the gate asked the inventory case-INSENSITIVELY while the subscription asks FlexLib case-SENSITIVELY. A radio spelling it any other way passes the gate and publishes the untouched minus one fifty initialiser as a measured reading.");

            F("codec-mic", "Analog microphone level at the radio's codec",
              "FlexBase.MicData", "Radio.MicDataReady, meter MIC",
              Provenance.MeterValue, FactOwnership.Telemetry, IdleHonesty.Gated,
              "The wrong instrument for a PC-audio operator and correctly demoted to context here. Prove from the descriptor that MIC is the analog converter path.",
              meter: "MIC",
              idleReads: "0 dBFS",
              fixedHere: true,
              concern: "WAS: gated on the MIC meter existing but read a field whose initialiser is zero. Zero dBFS is FULL SCALE. Before the first reading this fact publishes the loudest value the scale has.");

            F("meter-sc-mic", "Radio transmit mic meter", "MeterInventory.Find SC_MIC", "Meter",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Straight from the inventory, so it carries the three states and a timestamp intact. This is the honest shape the scalar facts above should have.",
              meter: "SC_MIC");
            F("meter-micpeak", "Radio mic peak meter", "MeterInventory.Find MICPEAK", "Meter",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Descriptor comparison on the wire.", meter: "MICPEAK");
            F("meter-comppeak", "Radio compression peak meter", "MeterInventory.Find COMPPEAK", "Meter",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Descriptor comparison on the wire.", meter: "COMPPEAK");
            F("meter-fwdpwr", "Radio forward power meter", "MeterInventory.Find FWDPWR", "Meter",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Descriptor comparison on the wire. Note its units: FlexLib converts dBm to watts downstream, so a descriptor in any other unit would silently change the scale.",
              meter: "FWDPWR");
            F("meter-revpwr", "Radio reflected power meter", "MeterInventory.Find REFPWR", "Meter",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Descriptor comparison on the wire. The fact's own NAME is still meter-revpwr because the rule file uses it; only the meter it looks for changed.",
              meter: "REFPWR",
              fixedHere: true,
              concern: "WAS: the analyzer asked the radio for a meter named REVPWR. Every other place in this repository spells it REFPWR — the meters panel, MeterModel's note, FlexLib's own AddMeter wiring, and the 2026-08-16 census of the bench 8600. This was the only REVPWR anywhere, so the fact was permanently Absent and told the operator, in the evidence for a high standing wave ratio, that their radio publishes no reflected power meter. It does.");
            F("meter-swr", "Radio SWR meter", "MeterInventory.Find SWR", "Meter",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Descriptor comparison on the wire.", meter: "SWR");
            F("meter-patemp", "Radio power amplifier temperature", "MeterInventory.Find PATEMP", "Meter",
              Provenance.MeterDescriptor, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Descriptor comparison on the wire.", meter: "PATEMP");

            // ── Stage 12: did RF actually leave ───────────────────────────
            F("transmitting", "The radio is transmitting right now",
              "FlexBase.Transmit", "Radio.Mox, synthesised from InterlockState",
              Provenance.WireField, FactOwnership.ClientOwned, IdleHonesty.NoIdleState,
              "There is no mox key on the wire. Read interlock.state and interlock.tx_client_handle, and attribute: Mox is true only for Transmitting, PTTRequested or UnkeyRequested AND only when the transmitting client is ours.",
              RigField.Interlock("state"),
              concern: "Labelled as a station fact and computed as a client one. On a MultiFlex station with another operator keyed, this reads 'no' while the radio is transmitting. The rules fail safe on it; the evidence block does not.",
              whyOwnershipDiffers: "interlock.state genuinely is telemetry, and Track C's table is right about the KEY. The FACT is client-owned because FlexLib gates Mox on the transmitting client handle being ours, so the same key yields different answers to different clients.");

            F("forward-power", "Forward power",
              "FlexBase.ForwardPowerWatts", "Radio.ForwardPowerDataReady, meter FWDPWR, dBm converted to watts",
              Provenance.MeterValue, FactOwnership.Telemetry, IdleHonesty.Gated,
              "VERIFIED from the application's trace, 2026-08-20: 17.4 to 26.0 dBm across a transmission, which the analyzer publishes as 0.055 to 0.398 watts. The dBm-to-watts conversion is correct. Read the power SETTING off the wire before keying and never key above the ceiling.",
              meter: "FWDPWR",
              idleReads: "0 watts",
              fixedHere: true,
              concern: "STILL OPEN, and separate from the gate: the no-power-out rule fires below a TENTH OF A WATT, and this radio measured 0.055 to 0.398 watts across a normal bench transmission — under the threshold for part of it, with a completely correct reading. The rule file admits the figure is a guess and says the floor a Flex reports on a genuine dead-key has not been measured. It has now, and 55 milliwatts is a real transmission rather than a dead key, so QRP and transverter operators are told their radio is barely transmitting when it is doing exactly what they set. Retuning a threshold is a judgement about what counts as a fault, with verdict wording attached, so it is reported. WAS: no gate at all. The dBm field initialises to minus one fifty, which converts to about a millionth of a millionth of a watt and prints as 0 watts. The no-power-out rule fires below a tenth of a watt, so an unreported meter during a real transmission produces the confident wrong verdict 'your radio is transmitting but almost no power is leaving it'.");

            F("swr", "Standing wave ratio",
              "FlexBase.SWRValue", "Radio.SWRDataReady, meter SWR",
              Provenance.MeterValue, FactOwnership.Telemetry, IdleHonesty.Gated,
              "Needs a transmit window.",
              meter: "SWR",
              idleReads: "0 to 1",
              fixedHere: true,
              concern: "WAS: no gate at all, and the field has no initialiser, so it publishes 0 to 1 before the meter has ever spoken. An SWR of zero to one is not a bad reading, it is an impossible one. The high-swr rule tests 'above 3', so an unreported meter reads as a perfect match and the stage is declared healthy.");

            F("rf-power-setting", "Transmit power setting",
              "FlexBase.XmitPower", "Radio.RFPower",
              Provenance.WireField, FactOwnership.StationGlobal, IdleHonesty.Fabricates,
              "Compare with transmit.rfpower on the wire. THIS is the reading to trust before keying — the app's copy is a mirror updated by an event, the wire is the radio.",
              RigField.Transmit("rfpower"),
              idleReads: "0 percent",
              concern: "A cached mirror updated only from a property-changed handler. Never seeded at connect, so if the change never fires it reads 0 percent, which is also a real setting.");

            F("dummy-load", "Dummy load mode",
              "FlexBase.DummyLoadMode", "none — an app mode that zeroes RFPower",
              Provenance.AppLocal, FactOwnership.NotRadioState, IdleHonesty.NoIdleState,
              "An app concept. Correctly sourced to 'the app' rather than to the radio. Its EFFECT is visible on the wire as rfpower going to zero.");

            F("ptt-source", "What is keying the transmitter",
              "FlexBase.PttSourceName", "Radio.PTTSource",
              Provenance.WireField, FactOwnership.Telemetry, IdleHonesty.NoIdleState,
              "Read the interlock source off the wire during a keying window and compare with the word the fact prints.",
              RigField.Interlock("source"));

            F("ptt-hardware", "The transmitter is keyed by a hardware line",
              "FlexBase.PttSourceIsHardware", "Radio.PTTSource",
              Provenance.WireField, FactOwnership.Telemetry, IdleHonesty.NoIdleState,
              "Derived from the same source word. Correct exactly when ptt-source is.",
              RigField.Interlock("source"));

            F("tx-slice", "Transmit slice",
              "FlexBase.TXSliceLetter", "Slice.IsTransmitSlice, then Letter",
              Provenance.WireField, FactOwnership.ClientOwned, IdleHonesty.NoIdleState,
              "Find the slice whose tx flag is set AND whose client_handle is the APPLICATION's. Reading it from a connection of our own describes our client and nothing else — and a harness that makes its own slice proves nothing at all.",
              RigField.Slice(0, "tx"),
              concern: "Reported as a LETTER while the radio indexes slices by number. The letter is an app-side naming; the mapping has to hold for the fact to name the operator's slice correctly.");

            F("tx-mode", "Transmit mode",
              "FlexBase.TXMode", "Slice.DemodMode of the transmit slice",
              Provenance.WireField, FactOwnership.ClientOwned, IdleHonesty.Gated,
              "Read mode off the transmit slice attributed to the application's client handle.",
              RigField.Slice(0, "mode"),
              idleReads: "empty",
              fixedHere: true,
              concern: "WAS: cached in a field only written when a slice property-change is seen for a slice already flagged as the transmit slice. Empty until then, and empty is published as an observed value rather than as an absence.");

            // ── PC-side facts, collected by TxChainPcFacts ────────────────
            F("pc-input-device", "Microphone chosen on this computer",
              "JJPortaudio Devices.InputDevice.Name", "none",
              Provenance.PcLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "Read from audioDevices.xml. Correctly separates unreadable from unconfigured, including the damaged-file case.");
            F("pc-audio-driver", "Audio driver this computer uses for the microphone",
              "JJPortaudio Devices.InputDevice.hostApiName", "none",
              Provenance.PcLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "Only added when non-empty, so it is absent rather than blank.");
            F("pc-input-device-present", "The chosen microphone is present",
              "WindowsMicLevel.TryFindByName", "none",
              Provenance.PcLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "A real fault when false, which is why it is a flag rather than an absence. Verified by unplugging the device.");
            F("pc-input-device-missing-reason", "Why the chosen microphone could not be found",
              "WindowsMicLevel lookup failure text", "none",
              Provenance.PcLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "Only present when the lookup failed.");
            F("pc-mic-muted", "Windows has the microphone muted",
              "WindowsMicLevel.Muted", "none",
              Provenance.PcLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "The reason this fact source exists: a Windows mute is invisible to every radio-side observable. Verified by muting in Windows and watching the radio report a floor while this says muted.");
            F("pc-mic-level", "Windows input level for the microphone",
              "WindowsMicLevel.Percent", "none",
              Provenance.PcLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "Verified by moving the Windows slider.");
            F("pc-mic-boost", "Microphone boost in Windows",
              "WindowsMicLevel.BoostDb", "none",
              Provenance.PcLocal, FactOwnership.NotRadioState, IdleHonesty.Gated,
              "Only added when the endpoint has a boost control, so its absence is honest.");

            return list;
        }

        /// <summary>
        /// The facts that publish a plausible measurement when nothing has been
        /// measured. The audit's headline, because everything else in this
        /// analyzer is built on the premise that this set is empty.
        /// </summary>
        public static IReadOnlyList<FactSpec> Fabricators =>
            _all.Where(f => f.Idle == IdleHonesty.Fabricates).ToList();

        /// <summary>Facts that describe a client's own state rather than the
        /// station's. Reading these from a second connection is only honest with
        /// attribution.</summary>
        public static IReadOnlyList<FactSpec> ClientOwned =>
            _all.Where(f => f.Ownership == FactOwnership.ClientOwned).ToList();

        /// <summary>Facts with a known or suspected wiring problem.</summary>
        public static IReadOnlyList<FactSpec> WithConcerns =>
            _all.Where(f => f.Concern.Length != 0).ToList();
    }
}
