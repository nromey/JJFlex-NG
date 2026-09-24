using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJTrace
{
    /// <summary>
    /// Outcome of a traced session. Tagged on each manifest entry for grep-ability —
    /// "show me all as_retry_failed sessions in the last month" is a 1-second answer
    /// against the manifest. Per project_trace_persistence_design.md.
    /// </summary>
    public static class TraceSessionOutcome
    {
        public const string Success = "success";
        public const string CleanExit = "clean_exit";
        public const string AsRetryThenSuccess = "as_retry_then_success";
        public const string AsRetryFailed = "as_retry_failed";
        public const string SliceUnavailable = "slice_unavailable";
        public const string ConnectionDropped = "connection_dropped";
        public const string Killed = "killed";
        public const string Crashed = "crashed";
        public const string NetworkFailed = "network_failed";
        public const string NoRadios = "no_radios";
        public const string Unknown = "unknown";
    }

    /// <summary>
    /// Connection target metadata captured per session. All fields optional —
    /// boot-only sessions may have no target; SmartLink sessions populate
    /// SmartlinkAccount; LAN sessions don't.
    /// </summary>
    public sealed class TraceConnectionTarget
    {
        [JsonPropertyName("serial")]
        public string Serial { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; }

        [JsonPropertyName("smartlink_account")]
        public string SmartlinkAccount { get; set; }

        [JsonPropertyName("ip")]
        public string Ip { get; set; }
    }

    /// <summary>
    /// Single archived-session entry in the trace manifest. Filename points at the
    /// compressed archive on disk (relative to TraceArchiveDir). Outcome tag is the
    /// load-bearing field for diagnostic queries.
    /// </summary>
    public sealed class TraceSessionEntry
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("filename")]
        public string Filename { get; set; }

        [JsonPropertyName("boot_time")]
        public DateTime BootTime { get; set; }

        [JsonPropertyName("end_time")]
        public DateTime? EndTime { get; set; }

        [JsonPropertyName("duration_ms")]
        public long? DurationMs { get; set; }

        [JsonPropertyName("outcome")]
        public string Outcome { get; set; }

        [JsonPropertyName("outcome_detail")]
        public string OutcomeDetail { get; set; }

        [JsonPropertyName("connection_target")]
        public TraceConnectionTarget ConnectionTarget { get; set; }

        [JsonPropertyName("trace_size_uncompressed_bytes")]
        public long? TraceSizeUncompressedBytes { get; set; }

        [JsonPropertyName("trace_size_compressed_bytes")]
        public long? TraceSizeCompressedBytes { get; set; }

        [JsonPropertyName("verbosity_level")]
        public string VerbosityLevel { get; set; }

        [JsonPropertyName("app_version")]
        public string AppVersion { get; set; }

        [JsonPropertyName("key_events")]
        public List<string> KeyEvents { get; set; }

        [JsonPropertyName("kept_forever")]
        public bool KeptForever { get; set; }

        /// <summary>
        /// 1-based part number when size-based rotation split the session into
        /// a chain of parts; null for a session archived whole. All parts of
        /// one session share <see cref="SessionId"/> and <see cref="BootTime"/>,
        /// so "give me this session's chain" is a group-by on session_id and
        /// retention ages every part of a session out together.
        /// </summary>
        [JsonPropertyName("part_number")]
        public int? PartNumber { get; set; }

        /// <summary>
        /// True on the last part of a chain. Absent on non-parted archives and
        /// on a chain whose session was killed before it could close cleanly —
        /// a chain with no part_final is itself a signal.
        /// </summary>
        [JsonPropertyName("part_final")]
        public bool? PartFinal { get; set; }

        /// <summary>
        /// File name of the plain-text trace this archive was made from. Lets
        /// boot maintenance tell an already-archived leftover part from an
        /// orphan that died before its background compression finished.
        /// </summary>
        [JsonPropertyName("source_name")]
        public string SourceName { get; set; }

        /// <summary>
        /// True when the source was too large to archive whole and only its
        /// tail was kept. Only reachable for traces written before rotation
        /// existed, or with rotation disabled.
        /// </summary>
        [JsonPropertyName("truncated")]
        public bool? Truncated { get; set; }

        /// <summary>
        /// True when this entry's <see cref="SessionId"/> is an inventory
        /// identity assigned at boot, because the raw file it was made from
        /// carried no durable record of the session that wrote it. The bytes
        /// are real; the identity and, unless the entry says otherwise, the
        /// outcome are not recovered. Absent on every entry a live session
        /// wrote itself.
        /// </summary>
        [JsonPropertyName("orphaned")]
        public bool? Orphaned { get; set; }
    }

    /// <summary>
    /// Trace manifest — index of every archived session. Lives at
    /// %AppData%\JJFlexRadio\Traces\manifest.json. Versioned so future schema
    /// changes are explicit.
    /// </summary>
    public sealed class TraceManifest
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("created")]
        public DateTime Created { get; set; }

        [JsonPropertyName("entries")]
        public List<TraceSessionEntry> Entries { get; set; } = new List<TraceSessionEntry>();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Load manifest from disk. Returns a fresh manifest if the file doesn't exist
        /// or fails to parse. Never throws — manifest corruption shouldn't crash the app.
        /// </summary>
        public static TraceManifest Load(string path)
        {
            if (!File.Exists(path))
            {
                return new TraceManifest { Created = DateTime.UtcNow };
            }
            try
            {
                string json = File.ReadAllText(path);
                TraceManifest manifest = JsonSerializer.Deserialize<TraceManifest>(json, JsonOptions);
                if (manifest == null)
                {
                    return new TraceManifest { Created = DateTime.UtcNow };
                }
                if (manifest.Entries == null)
                {
                    manifest.Entries = new List<TraceSessionEntry>();
                }
                return manifest;
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
                return new TraceManifest { Created = DateTime.UtcNow };
            }
        }

        /// <summary>
        /// Save manifest to disk atomically (write to temp, then rename) so a kill
        /// mid-write doesn't leave a partial JSON file.
        ///
        /// <para><b>It reports now.</b> This used to swallow a write failure and
        /// return void, so an archive could say "committed" on the strength of a
        /// manifest write that never happened — and a ticket cannot truthfully
        /// report committed without knowing.</para>
        /// </summary>
        /// <returns>True when the manifest really landed.</returns>
        public bool Save(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { Tracing.ErrTraceOnly(ex); return false; }
            }
            string tempPath = path + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(this, JsonOptions);
                File.WriteAllText(tempPath, json);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return false;
            }
        }

        // ── The archive-store transaction ──────────────────────────────────
        //
        // Every mutation of the manifest — an archive appending its entry, a
        // prune removing rows, the browser's delete, boot reconciliation —
        // is a read, a modify and a replace. Run two of those concurrently and
        // the second one's Load predates the first one's Save, so the first
        // one's change is silently gone. Rotation parts already shared one
        // chain; the final file did not, which is exactly the pair most likely
        // to collide, at exit.
        //
        // This is a SEPARATE lock from the trace boundary, deliberately. It is
        // held for a short read/modify/replace and NEVER around compression,
        // and the boundary is never held here — the two must not be able to
        // wait on each other.

        private static readonly object _storeLock = new object();

        /// <summary>
        /// Name of the cross-process mutex guarding one archive store. Two app
        /// instances share <c>%AppData%\JJFlexRadio\Traces</c>, so an
        /// in-process lock alone would be exactly half a transaction. Local
        /// rather than Global: this is per-user data.
        /// </summary>
        private static string MutexNameFor(string manifestPath)
        {
            string key = (manifestPath ?? string.Empty).ToUpperInvariant();
            uint hash = 2166136261;
            foreach (char c in key) { hash = (hash ^ c) * 16777619; }
            return "Local\\JJFlexTraceManifest-" + hash.ToString("X8");
        }

        /// <summary>How long to wait for the other instance to finish its
        /// read/modify/replace. Generous for a JSON file; bounded so a crashed
        /// holder cannot wedge an archive worker forever.</summary>
        private const int StoreLockTimeoutMs = 15000;

        /// <summary>
        /// Read, modify and replace the manifest under the archive-store
        /// transaction. <paramref name="mutate"/> must not compress, scan
        /// directories, or touch the trace boundary.
        /// </summary>
        /// <returns>True when the mutation ran AND the manifest was written.</returns>
        public static bool Mutate(string manifestPath, Func<TraceManifest, bool> mutate)
        {
            if (string.IsNullOrEmpty(manifestPath) || mutate == null) return false;
            System.Threading.Mutex crossProcess = null;
            bool held = false;
            try
            {
                try
                {
                    crossProcess = new System.Threading.Mutex(false, MutexNameFor(manifestPath));
                    held = crossProcess.WaitOne(StoreLockTimeoutMs);
                }
                catch (System.Threading.AbandonedMutexException)
                {
                    // The other instance died holding it. The manifest is
                    // written by replace, so it is whole; carry on.
                    held = true;
                }
                catch (Exception ex)
                {
                    // No cross-process mutex available (a sandbox, a policy).
                    // The in-process lock below is still worth having.
                    Tracing.ErrTraceOnly(ex);
                }

                lock (_storeLock)
                {
                    TraceManifest manifest = Load(manifestPath);
                    bool changed;
                    try { changed = mutate(manifest); }
                    catch (Exception ex) { Tracing.ErrTraceOnly(ex); return false; }
                    if (!changed) return true;
                    return manifest.Save(manifestPath);
                }
            }
            finally
            {
                if (crossProcess != null)
                {
                    try { if (held) crossProcess.ReleaseMutex(); } catch { }
                    try { crossProcess.Dispose(); } catch { }
                }
            }
        }
    }
}
