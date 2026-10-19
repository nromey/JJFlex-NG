using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The GUI client id JJ Flexible presents to ONE radio from ONE running
    /// copy of the program, and what the radio said back (#782).
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
    /// invent. One per (radio serial, <see cref="GuiClientSlot"/>), under the
    /// per-radio serial-keyed store (<c>radios\{serial}\gui-client-id.json</c>,
    /// with the slot number suffixed for a second running copy), so it follows
    /// <c>JJFLEX_CONFIG_DIR</c> like every other store. SmartSDR keeps one id
    /// per Windows user for every radio; ours is per radio because Noel ruled
    /// per-radio configuration is keyed by serial.
    /// </para>
    /// <para>
    /// <b>Two copies must never present the same id.</b> The radio refuses a
    /// duplicate by disconnecting one of the two connections, and nothing says
    /// which. The slot is a named mutex held for the life of the process (see
    /// <see cref="GuiClientSlot"/>), NOT the process count: the count let a
    /// later copy inherit a live copy's number after an earlier copy closed.
    /// </para>
    /// <para>
    /// <b>Refusal keeps the stored id, and the memory of it outlives the rig.</b>
    /// When the radio reports <c>duplicate_client_id=1</c> for our handle, the
    /// id is in use by a live connection — ours from an earlier session the
    /// radio has not timed out, or another copy of the program. The rest of
    /// this run presents no id for that radio and slot so the radio mints a
    /// session-only one, which is NOT stored; the file keeps the id that was
    /// refused. The application REPLACES its FlexBase on reconnect and retry,
    /// so that memory lives in <see cref="GuiClientIdentityRefusals"/>, keyed
    /// by (serial, slot), for the process, not in any rig.
    /// </para>
    /// <para>
    /// <b>Two different questions about an id.</b>
    /// <see cref="IsThisConnectionsId"/> is OWNERSHIP: the id the radio
    /// reported for OUR client on THIS connection, not refused. That is the
    /// one a feature deciding what to remove or restore may use.
    /// <see cref="IsAnIdThisCopyHasPresentedOrStored"/> is HISTORY: an id we
    /// stored or presented, which after a refusal may be another live copy's.
    /// It correlates; it never authorises.
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

        private GuiClientIdentity(string serial, int slot, string storedId, bool duplicateRefusedThisRun, bool persist)
        {
            Serial = serial ?? "";
            Slot = slot < 0 ? 0 : slot;
            // No slot means no file: a session-only identity whatever the caller asked.
            _persist = persist && Slot > 0;
            StoredId = !_persist || string.IsNullOrWhiteSpace(storedId) ? null : storedId.Trim();
            DuplicateRefusedEarlierThisRun = duplicateRefusedThisRun;
            // Present the stored id unless this run has already had it refused.
            _presentedId = duplicateRefusedThisRun ? null : StoredId;
        }

        /// <summary>
        /// False for a SESSION-ONLY identity: nothing is read from the store,
        /// nothing is presented, and the radio's mint is not written back.
        /// The Connection Tester's and the radio-in-the-loop harness's rigs
        /// ask for this because they run beside the operator's live
        /// connection, which holds the stored id; a copy with no free slot
        /// gets it whether it asked or not.
        /// </summary>
        public bool Persistent => _persist;

        /// <summary>The radio this identity is for.</summary>
        public string Serial { get; }

        /// <summary>This copy's <see cref="GuiClientSlot"/> number (1-based); 0
        /// when none was held, which makes the identity session-only.</summary>
        public int Slot { get; }

        /// <summary>The id on disk when this attempt began, or null.</summary>
        public string StoredId { get; }

        /// <summary>True when an earlier attempt in this run had the stored id
        /// refused as a duplicate for this radio and slot, so this attempt
        /// presented none.</summary>
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
        /// recognise this client and restore its own panadapters and slices.
        /// The station phase uses this to choose its materialization boundary.</summary>
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
        /// those exist. Correlation only; see <see cref="IsThisConnectionsId"/>.</summary>
        public string EffectiveId
        {
            get { lock (_sync) return _radioReportedId ?? _presentedId ?? StoredId; }
        }

        /// <summary>
        /// OWNERSHIP, for this connection: true only when the radio has
        /// reported <paramref name="clientId"/> as OUR client's id on this
        /// connection and has not since refused it. This is the question a
        /// feature deciding whether to remove or restore a radio object may
        /// ask (the #456 recorder), and it must still check the object's own
        /// facts — handle, fingerprint, current state — because a stable id
        /// says who connected, not what a slice index denotes today.
        /// </summary>
        public bool IsThisConnectionsId(string clientId)
        {
            if (string.IsNullOrEmpty(clientId)) return false;
            lock (_sync)
                return !_refusedAsDuplicate && _radioReportedId != null
                    && string.Equals(clientId, _radioReportedId, StringComparison.Ordinal);
        }

        /// <summary>
        /// HISTORY, not ownership: true when <paramref name="clientId"/> is an
        /// id this copy stored for this radio and slot, presented on this
        /// attempt, or was reported on this connection. After a refusal the
        /// stored id may belong to another live copy, so this correlates an
        /// object with an earlier session of ours and authorises nothing.
        /// </summary>
        public bool IsAnIdThisCopyHasPresentedOrStored(string clientId)
        {
            if (string.IsNullOrEmpty(clientId)) return false;
            lock (_sync)
            {
                return string.Equals(clientId, _radioReportedId, StringComparison.Ordinal)
                    || string.Equals(clientId, _presentedId, StringComparison.Ordinal)
                    || string.Equals(clientId, StoredId, StringComparison.Ordinal);
            }
        }

        /// <summary>Where this identity is (or would be) stored; null for a
        /// session-only identity or a malformed serial.</summary>
        public string FilePath => _persist ? GuiClientIdentityStore.FilePathFor(Serial, Slot) : null;

        /// <summary>
        /// Build the identity for one connect attempt: reads the store once,
        /// and the process-wide refusal memory for this radio and slot.
        /// </summary>
        public static GuiClientIdentity ForConnect(string serial, int slot, bool useStore = true)
        {
            bool refused = GuiClientIdentityRefusals.IsRefused(serial, slot);
            string stored = useStore && slot > 0 ? GuiClientIdentityStore.Read(serial, slot) : null;
            return new GuiClientIdentity(serial, slot, stored, refused, useStore);
        }

        /// <summary>One sentence for the trace, written before <c>Radio.Connect</c>.</summary>
        public string DescribePresentation()
        {
            string where = FilePath ?? "(no per-radio store: the serial is malformed or there is no settings root)";
            lock (_sync)
            {
                if (!_persist)
                    return Slot > 0
                        ? "session-only identity for radio " + Serial + ": presenting no id and storing none; the radio mints one for this connection only"
                          + " (a throwaway rig, such as the connection tester's, must not present the id the operator's live connection may hold)"
                        : "session-only identity for radio " + Serial + ": this copy holds no client-id slot (every slot is held by another copy), so it presents no id and stores none";
                if (_presentedId != null)
                    return "presenting stored id " + _presentedId + " for radio " + Serial + ", slot " + Slot
                        + " (from " + where + "); the radio should recognise this client and restore its own panadapters and slices";
                if (StoredId != null && DuplicateRefusedEarlierThisRun)
                    return "the stored id " + StoredId + " for radio " + Serial + ", slot " + Slot
                        + " was refused as a duplicate earlier in this run; presenting no id so the radio mints a fresh one for this session only; the stored id is kept at " + where;
                return "no stored id for radio " + Serial + ", slot " + Slot
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
                // Store only a fresh mint for a radio and slot with nothing
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
                string refusal = GuiClientIdentityStore.Write(Serial, Slot, clientId);
                return "the radio reports our client_id=" + clientId + " — freshly minted; "
                    + (refusal == null ? "stored at " + FilePath + " for the next connect to radio " + Serial + ", slot " + Slot
                                       : "NOT stored (" + refusal + "), so the next connect will be minted afresh too");
            }

            return "the radio reports our client_id=" + clientId + " — freshly minted for this session only"
                + (StoredId != null ? " (the stored id " + StoredId + " is kept after the duplicate refusal)"
                   : !_persist ? " (session-only identity; nothing is stored)" : "");
        }

        /// <summary>The radio refused the presented id as a duplicate. Records
        /// it for the process (this radio and slot), so a replacement rig's
        /// next attempt presents none.</summary>
        public void MarkRefusedAsDuplicate()
        {
            lock (_sync) _refusedAsDuplicate = true;
            GuiClientIdentityRefusals.Record(Serial, Slot);
        }

        public override string ToString()
        {
            lock (_sync)
                return "GuiClientIdentity(serial=" + Serial + ", slot=" + Slot
                    + ", stored=" + (StoredId ?? "none") + ", presented=" + (_presentedId ?? "none")
                    + ", radio=" + (_radioReportedId ?? "not yet reported")
                    + (_refusedAsDuplicate ? ", REFUSED as duplicate" : "") + (_persist ? "" : ", session-only") + ")";
        }
    }

    /// <summary>
    /// Process-lifetime memory of duplicate refusals, keyed by (radio serial,
    /// slot). Lives outside any rig because the application disposes and
    /// recreates its FlexBase on reconnect and on retry (<c>CloseTheRadio</c>,
    /// <c>wpfSelectorProc</c>, <c>TryAutoConnectOnStartup</c>,
    /// <c>openTheRadio</c>); a flag on the rig was lost with it, and the next
    /// connect presented the refused id again. Keyed rather than a single
    /// Boolean so a refusal on one radio does not switch off identity on
    /// every other radio.
    /// </summary>
    public static class GuiClientIdentityRefusals
    {
        private static readonly ConcurrentDictionary<string, DateTime> _refused =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private static string Key(string serial, int slot) => (serial ?? "").Trim() + "|" + slot;

        /// <summary>The radio refused this radio-and-slot's stored id in this run.</summary>
        public static void Record(string serial, int slot) => _refused[Key(serial, slot)] = DateTime.UtcNow;

        /// <summary>Whether this run has had the stored id for this radio and slot refused.</summary>
        public static bool IsRefused(string serial, int slot) => _refused.ContainsKey(Key(serial, slot));

        /// <summary>Every radio-and-slot refused this run, for the trace.</summary>
        public static int Count => _refused.Count;

        /// <summary>Forget one refusal. Tests, and a future "try my stored id again" action.</summary>
        public static bool Forget(string serial, int slot) => _refused.TryRemove(Key(serial, slot), out _);

        /// <summary>Forget everything. Tests only.</summary>
        internal static void ClearForTests() => _refused.Clear();
    }

    /// <summary>What the store writes: the id, and the key it was written under.</summary>
    public sealed class GuiClientIdentityRecord
    {
        public string ClientId { get; set; } = "";
        public string Serial { get; set; } = "";
        public int Slot { get; set; } = 1;
        public DateTime StoredUtc { get; set; }
        /// <summary>Always "radio-assigned": the id is the radio's own mint,
        /// recorded so a reader of the file knows nothing here was invented.</summary>
        public string Source { get; set; } = "radio-assigned";
    }

    /// <summary>
    /// The per-radio, per-slot file behind <see cref="GuiClientIdentity"/>.
    /// Resolves through <see cref="RadioConfig.RadioDirectory"/>, so it lives
    /// beside the radio's <c>config.xml</c> and follows the settings root
    /// wherever startup put it. Never throws: a store that cannot be read or
    /// written is traced and the connect proceeds with a radio-minted id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A record is only believed for the key it was written under.</b>
    /// <see cref="Read"/> checks the file's serial, slot and source against
    /// the request and the id's shape against what the protocol can carry; a
    /// file that is valid JSON but says the wrong radio or slot, or holds an
    /// id with a space in it, reads as nothing stored. Codex Astra's
    /// 2026-10-19 review: a misfiled but parseable id used to be presented.
    /// </para>
    /// <para>
    /// <b>Writes are atomic</b> (temporary file, then replace), so an
    /// interrupted write leaves the previous record rather than a torn one.
    /// And a write REFUSES to replace a readable record for the same key that
    /// holds a different id: the only writer is a first mint for a key the
    /// reader found empty, so a different id there means the reader could not
    /// read the file at the time (an IO failure, not an empty store), and
    /// replacing it would throw away the identity the radio knows.
    /// </para>
    /// </remarks>
    public static class GuiClientIdentityStore
    {
        private static readonly object _sync = new object();
        public const string Source = "radio-assigned";

        /// <summary>
        /// <c>radios\{serial}\gui-client-id.json</c> for slot 1,
        /// <c>gui-client-id-N.json</c> for slot N. Null when the serial is
        /// malformed, the slot is not held (0), or there is no settings root.
        /// </summary>
        public static string FilePathFor(string serial, int slot)
        {
            if (slot < 1) return null;
            var dir = RadioConfig.RadioDirectory(serial ?? "");
            if (string.IsNullOrEmpty(dir)) return null;
            string name = slot == 1 ? "gui-client-id.json" : "gui-client-id-" + slot + ".json";
            return Path.Combine(dir, name);
        }

        /// <summary>
        /// Whether <paramref name="clientId"/> has the shape of a token the
        /// protocol can carry in <c>client gui &lt;id&gt;</c>: one word of
        /// printable ASCII, no whitespace, 8 to 64 characters. The radio has
        /// minted GUIDs so far; this is the loosest check that still refuses
        /// anything that would break the command line it is sent on.
        /// </summary>
        public static bool IsWellFormedClientId(string clientId)
        {
            if (string.IsNullOrEmpty(clientId)) return false;
            if (clientId.Length < 8 || clientId.Length > 64) return false;
            foreach (char c in clientId)
            {
                if (c <= ' ' || c > '~') return false;
            }
            return true;
        }

        /// <summary>The stored id for this exact key, or null when none,
        /// unreadable, misfiled or malformed.</summary>
        public static string Read(string serial, int slot)
        {
            string file = FilePathFor(serial, slot);
            if (file == null) return null;
            try
            {
                lock (_sync)
                {
                    if (!File.Exists(file)) return null;
                    var record = JsonSerializer.Deserialize<GuiClientIdentityRecord>(File.ReadAllText(file));
                    string why = Validate(record, serial, slot);
                    if (why != null)
                    {
                        Tracing.TraceLine("GuiClientIdentityStore.Read(" + serial + ", slot " + slot + "): " + file + " is not believed — " + why
                            + "; treating the store as empty; the radio will mint an id", System.Diagnostics.TraceLevel.Warning);
                        return null;
                    }
                    return record.ClientId.Trim();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("GuiClientIdentityStore.Read(" + serial + ", slot " + slot + "): " + ex.Message
                    + " — treating the store as empty; the radio will mint an id", System.Diagnostics.TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>Null when the record is believable for this key, else why not.</summary>
        internal static string Validate(GuiClientIdentityRecord record, string serial, int slot)
        {
            if (record == null) return "the file holds no record";
            string id = record.ClientId?.Trim();
            if (string.IsNullOrEmpty(id)) return "the record has no client id";
            if (!IsWellFormedClientId(id)) return "the client id is not a protocol token (whitespace, control or non-ASCII characters, or an odd length)";
            if (!string.Equals(record.Serial?.Trim(), (serial ?? "").Trim(), StringComparison.Ordinal))
                return "it was written for radio '" + record.Serial + "', not '" + serial + "'";
            if (record.Slot != slot) return "it was written for slot " + record.Slot + ", not slot " + slot;
            if (!string.Equals(record.Source, Source, StringComparison.Ordinal)) return "its source is '" + record.Source + "', not '" + Source + "'";
            return null;
        }

        /// <summary>
        /// Write the id. Returns null when it landed, else one sentence for
        /// the trace saying why not.
        /// </summary>
        public static string Write(string serial, int slot, string clientId)
        {
            clientId = clientId?.Trim();
            if (!IsWellFormedClientId(clientId)) return "the id '" + clientId + "' is not a protocol token, so it is not stored";
            string file = FilePathFor(serial, slot);
            if (file == null) return "there is no per-radio store to write to (malformed serial, no slot, or no settings root)";
            try
            {
                lock (_sync)
                {
                    if (File.Exists(file))
                    {
                        // Never replace a readable record for this key that
                        // names a different id; see the class remarks.
                        GuiClientIdentityRecord existing = null;
                        try { existing = JsonSerializer.Deserialize<GuiClientIdentityRecord>(File.ReadAllText(file)); } catch { }
                        if (existing != null && Validate(existing, serial, slot) == null
                            && !string.Equals(existing.ClientId.Trim(), clientId, StringComparison.Ordinal))
                        {
                            return "the store already holds id " + existing.ClientId.Trim() + " for radio " + serial + ", slot " + slot
                                + ", which the connect could not read at the time; the existing id is kept";
                        }
                    }
                    var record = new GuiClientIdentityRecord
                    {
                        ClientId = clientId,
                        Serial = serial ?? "",
                        Slot = slot,
                        StoredUtc = DateTime.UtcNow,
                        Source = Source,
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    string temp = file + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temp, file, overwrite: true);
                }
                return null;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("GuiClientIdentityStore.Write(" + serial + ", slot " + slot + "): " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
                return "writing " + file + " failed: " + ex.Message;
            }
        }
    }
}
