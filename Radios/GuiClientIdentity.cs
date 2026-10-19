using System;
using System.IO;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The GUI client id JJ Flexible presents to ONE radio from ONE running
    /// instance, and what the radio said back (#782).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Until 2026-10-19 both connect paths called
    /// <c>Radio.Connect()</c> with no id, so FlexLib sent a bare
    /// <c>client gui</c> and the radio minted a brand-new GUI client id on
    /// every connection: nine connects measured on 2026-10-02 gave nine
    /// distinct ids. The FlexRadio API wiki says a client "should then store
    /// the returned client_id" and present it in later sessions, which is how
    /// SmartSDR comes back as the same client. We never did, so every
    /// session was a stranger to the radio.
    /// </para>
    /// <para>
    /// <b>What is stored, and where.</b> The RADIO'S OWN answer to the first
    /// bare <c>client gui</c>, exactly as the wiki prescribes — never an id we
    /// invent. One per (radio serial, program instance), under the per-radio
    /// serial-keyed store (<c>radios\{serial}\gui-client-id.json</c>, with an
    /// instance suffix for a second running copy of the program), so it
    /// follows <c>JJFLEX_CONFIG_DIR</c> like every other store. SmartSDR keeps
    /// one id per Windows user for every radio; ours is per radio because
    /// Noel ruled per-radio configuration is keyed by serial.
    /// </para>
    /// <para>
    /// <b>Two instances must never present the same id.</b> The radio refuses
    /// a duplicate by disconnecting one of the two connections, and nothing
    /// says which. The instance number the program already computes for its
    /// trace file name is the key here too, so each concurrent copy has its
    /// own file.
    /// </para>
    /// <para>
    /// <b>Refusal keeps the stored id.</b> When the radio reports
    /// <c>duplicate_client_id=1</c> for our handle, the id is in use by a
    /// live connection — ours from an earlier session the radio has not
    /// timed out, or another copy of the program. The rest of this run
    /// presents no id so the radio mints a session-only one, which is NOT
    /// stored; the file keeps the id that was refused, because next time
    /// the other holder will probably be gone.
    /// </para>
    /// </remarks>
    public sealed class GuiClientIdentity
    {
        private readonly object _sync = new object();
        private string _presentedId;
        private string _radioReportedId;
        private bool _refusedAsDuplicate;
        private bool _storedThisAttempt;
        private readonly bool _persist;

        private GuiClientIdentity(string serial, int instance, string storedId, bool duplicateRefusedThisRun, bool persist)
        {
            Serial = serial ?? "";
            Instance = instance < 1 ? 1 : instance;
            _persist = persist;
            StoredId = !persist || string.IsNullOrWhiteSpace(storedId) ? null : storedId.Trim();
            DuplicateRefusedEarlierThisRun = duplicateRefusedThisRun;
            // Present the stored id unless this run has already had it refused.
            _presentedId = duplicateRefusedThisRun ? null : StoredId;
        }

        /// <summary>
        /// False for a SESSION-ONLY identity: nothing is read from the store,
        /// nothing is presented, and the radio's mint is not written back.
        /// The Connection Tester's throwaway rigs use this, because they run
        /// in the same process as — and possibly at the same time as — the
        /// operator's live connection, which holds the stored id.
        /// </summary>
        public bool Persistent => _persist;

        /// <summary>The radio this identity is for.</summary>
        public string Serial { get; }

        /// <summary>The program instance (1 for the first running copy).</summary>
        public int Instance { get; }

        /// <summary>The id on disk when this attempt began, or null.</summary>
        public string StoredId { get; }

        /// <summary>True when an earlier attempt in this run had the stored id
        /// refused as a duplicate, so this attempt presented none.</summary>
        public bool DuplicateRefusedEarlierThisRun { get; }

        /// <summary>The id handed to <c>Radio.Connect</c>, or null when the radio
        /// was asked to mint one.</summary>
        public string PresentedId { get { lock (_sync) return _presentedId; } }

        /// <summary>The id the radio reported for OUR client on this connection,
        /// or null until it has.</summary>
        public string RadioReportedId { get { lock (_sync) return _radioReportedId; } }

        /// <summary>The radio refused the presented id as a duplicate on this
        /// connection and is disconnecting us.</summary>
        public bool RefusedAsDuplicate { get { lock (_sync) return _refusedAsDuplicate; } }

        /// <summary>A stored id was presented, so the radio had the chance to
        /// recognise this client and restore its own panadapters and slices.</summary>
        public bool PresentedStoredId => PresentedId != null;

        /// <summary>The radio reported the very id we presented: this connection
        /// is a RETURNING client as far as the radio is concerned.</summary>
        public bool IsReturning
        {
            get
            {
                lock (_sync)
                    return _presentedId != null && _radioReportedId != null
                        && string.Equals(_presentedId, _radioReportedId, StringComparison.Ordinal);
            }
        }

        /// <summary>The id in force for this connection: the radio's report,
        /// else what we presented, else what is stored. Null before any of
        /// those exist.</summary>
        public string EffectiveId
        {
            get { lock (_sync) return _radioReportedId ?? _presentedId ?? StoredId; }
        }

        /// <summary>
        /// Whether a client id the radio attributes something to is THIS
        /// client — this connection's id, the one we presented, or the one
        /// stored for this radio and instance. This is the question the
        /// listen-back recorder (#456) needs answered after a reconnect: an
        /// object stamped with any of these was created by us, in this
        /// session or an earlier one.
        /// </summary>
        public bool IsOurs(string clientId)
        {
            if (string.IsNullOrEmpty(clientId)) return false;
            lock (_sync)
            {
                return string.Equals(clientId, _radioReportedId, StringComparison.Ordinal)
                    || string.Equals(clientId, _presentedId, StringComparison.Ordinal)
                    || string.Equals(clientId, StoredId, StringComparison.Ordinal);
            }
        }

        /// <summary>Where this identity is (or would be) stored.</summary>
        public string FilePath => GuiClientIdentityStore.FilePathFor(Serial, Instance);

        /// <summary>
        /// Build the identity for one connect attempt: reads the store once.
        /// <paramref name="duplicateRefusedThisRun"/> is the process-wide memory
        /// that an earlier attempt had the stored id refused.
        /// </summary>
        public static GuiClientIdentity ForConnect(string serial, int instance, bool duplicateRefusedThisRun, bool useStore = true)
        {
            string stored = useStore ? GuiClientIdentityStore.Read(serial, instance) : null;
            return new GuiClientIdentity(serial, instance, stored, duplicateRefusedThisRun, useStore);
        }

        /// <summary>One sentence for the trace, written before <c>Radio.Connect</c>.</summary>
        public string DescribePresentation()
        {
            string where = FilePath ?? "(no per-radio store: the serial is malformed or there is no settings root)";
            lock (_sync)
            {
                if (!_persist)
                    return "session-only identity for radio " + Serial + ": presenting no id and storing none; the radio mints one for this connection only"
                        + " (a throwaway rig, such as the connection tester's, must not present the id the operator's live connection may hold)";
                if (_presentedId != null)
                    return "presenting stored id " + _presentedId + " for radio " + Serial + ", instance " + Instance
                        + " (from " + where + "); the radio should recognise this client and restore its own panadapters and slices";
                if (StoredId != null && DuplicateRefusedEarlierThisRun)
                    return "the stored id " + StoredId + " for radio " + Serial + ", instance " + Instance
                        + " was refused as a duplicate earlier in this run; presenting no id so the radio mints a fresh one for this session only; the stored id is kept at " + where;
                return "no stored id for radio " + Serial + ", instance " + Instance
                    + "; asking the radio to mint one; it will be stored at " + where + " when the radio reports it";
            }
        }

        /// <summary>
        /// The radio reported OUR client's id. Stores a freshly minted id when
        /// nothing was presented and nothing is stored; never overwrites a
        /// stored id. Returns the sentence for the trace, or null when this
        /// report adds nothing new (the same id reported again).
        /// </summary>
        public string RadioReported(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return null;
            clientId = clientId.Trim();
            string presented;
            bool store;
            lock (_sync)
            {
                if (string.Equals(_radioReportedId, clientId, StringComparison.Ordinal)) return null;
                _radioReportedId = clientId;
                presented = _presentedId;
                // Store only a fresh mint for a radio and instance with nothing
                // stored, once per attempt. A session-only id after a refusal is
                // not stored: the stored id is kept.
                store = _persist && presented == null && StoredId == null && !_storedThisAttempt;
                if (store) _storedThisAttempt = true;
            }

            if (presented != null)
            {
                if (string.Equals(presented, clientId, StringComparison.Ordinal))
                    return "the radio reports our client_id=" + clientId + " — the same id we presented; this is a RETURNING client";
                return "the radio reports our client_id=" + clientId + " — MISMATCH: we presented " + presented
                    + "; using the radio's id for this session; the stored id is kept and the next connect presents it again";
            }

            if (store)
            {
                bool written = GuiClientIdentityStore.Write(Serial, Instance, clientId);
                return "the radio reports our client_id=" + clientId + " — freshly minted; "
                    + (written ? "stored at " + FilePath + " for the next connect to radio " + Serial + ", instance " + Instance
                               : "it could NOT be stored (see the RadioConfig line above), so the next connect will be minted afresh too");
            }

            return "the radio reports our client_id=" + clientId + " — freshly minted for this session only"
                + (StoredId != null ? " (the stored id " + StoredId + " is kept after the duplicate refusal)"
                   : !_persist ? " (session-only identity; nothing is stored)" : "");
        }

        /// <summary>The radio refused the presented id as a duplicate.</summary>
        public void MarkRefusedAsDuplicate()
        {
            lock (_sync) _refusedAsDuplicate = true;
        }

        public override string ToString()
        {
            lock (_sync)
                return "GuiClientIdentity(serial=" + Serial + ", instance=" + Instance
                    + ", stored=" + (StoredId ?? "none") + ", presented=" + (_presentedId ?? "none")
                    + ", radio=" + (_radioReportedId ?? "not yet reported")
                    + (_refusedAsDuplicate ? ", REFUSED as duplicate" : "") + ")";
        }
    }

    /// <summary>What the store writes: the id, and enough to read the file by hand.</summary>
    public sealed class GuiClientIdentityRecord
    {
        public string ClientId { get; set; } = "";
        public string Serial { get; set; } = "";
        public int Instance { get; set; } = 1;
        public DateTime StoredUtc { get; set; }
        /// <summary>Always "radio-assigned": the id is the radio's own mint,
        /// recorded so a reader of the file knows nothing here was invented.</summary>
        public string Source { get; set; } = "radio-assigned";
    }

    /// <summary>
    /// The per-radio, per-instance file behind <see cref="GuiClientIdentity"/>.
    /// Resolves through <see cref="RadioConfig.RadioDirectory"/>, so it lives
    /// beside the radio's <c>config.xml</c> and follows the settings root
    /// wherever startup put it. Never throws: a store that cannot be read or
    /// written is traced and the connect proceeds with a radio-minted id.
    /// </summary>
    public static class GuiClientIdentityStore
    {
        private static readonly object _sync = new object();

        /// <summary>
        /// <c>radios\{serial}\gui-client-id.json</c> for the first instance,
        /// <c>gui-client-id-N.json</c> for instance N. Null when the serial is
        /// malformed or there is no settings root.
        /// </summary>
        public static string FilePathFor(string serial, int instance)
        {
            var dir = RadioConfig.RadioDirectory(serial ?? "");
            if (string.IsNullOrEmpty(dir)) return null;
            string name = instance <= 1 ? "gui-client-id.json" : "gui-client-id-" + instance + ".json";
            return Path.Combine(dir, name);
        }

        /// <summary>The stored id, or null when none, unreadable, or malformed.</summary>
        public static string Read(string serial, int instance)
        {
            string file = FilePathFor(serial, instance);
            if (file == null) return null;
            try
            {
                lock (_sync)
                {
                    if (!File.Exists(file)) return null;
                    var record = JsonSerializer.Deserialize<GuiClientIdentityRecord>(File.ReadAllText(file));
                    string id = record?.ClientId;
                    return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("GuiClientIdentityStore.Read(" + serial + ", " + instance + "): " + ex.Message
                    + " — treating the store as empty; the radio will mint an id", System.Diagnostics.TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>Write the id. True when it landed.</summary>
        public static bool Write(string serial, int instance, string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return false;
            string file = FilePathFor(serial, instance);
            if (file == null)
            {
                Tracing.TraceLine("GuiClientIdentityStore.Write(" + serial + ", " + instance + "): no per-radio store to write to",
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
            try
            {
                var record = new GuiClientIdentityRecord
                {
                    ClientId = clientId.Trim(),
                    Serial = serial ?? "",
                    Instance = instance < 1 ? 1 : instance,
                    StoredUtc = DateTime.UtcNow,
                };
                lock (_sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
                }
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("GuiClientIdentityStore.Write(" + serial + ", " + instance + "): " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }
    }
}
