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
                Tracing.ErrMessageTrace(ex);
                return new TraceManifest { Created = DateTime.UtcNow };
            }
        }

        /// <summary>
        /// Save manifest to disk atomically (write to temp, then rename) so a kill
        /// mid-write doesn't leave a partial JSON file.
        /// </summary>
        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
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
            }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }
}
