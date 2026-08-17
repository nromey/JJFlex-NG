using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// One recorded connect attempt: which path was tried, what happened,
    /// how long it took. The substrate two future policies read — "you have
    /// connected remotely three times, switch the default?" and "SmartLink
    /// is slow for you, Connect would be faster" — and a support tool in its
    /// own right: "how long is Don's connect actually taking" becomes a
    /// stored fact instead of a trace-file archaeology session.
    /// </summary>
    public sealed class ConnectionAttemptRecord
    {
        public DateTime TimestampUtc { get; set; }

        /// <summary>The path attempted — a <see cref="ConnectPathKind"/>
        /// name ("Local", "SmartLink"), stored as text so future paths need
        /// no schema change.</summary>
        public string Path { get; set; } = "";

        /// <summary>"connected", "failed", or a failure class name from
        /// <see cref="ConnectFailureClass"/> when one was filed.</summary>
        public string Outcome { get; set; } = "";

        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Per-radio connection history: a short ring of the last attempts,
    /// keyed by serial in the serial-keyed per-radio store
    /// (<c>radios\{serial}\connect-history.json</c>).
    ///
    /// <para>LOCAL JSON ONLY, never phoned home
    /// (project_no_silent_phone_home.md): this is timing telemetry about the
    /// operator's own network and it stays on their machine. Record-only in
    /// this batch — the offer UX and both policies that would read it are
    /// deliberately out of scope. Every method swallows IO failure and
    /// traces; history must never break a connect.</para>
    /// </summary>
    public static class ConnectionHistory
    {
        /// <summary>The ring size — the last ten attempts, not unbounded
        /// history. Baselines need a median, not an archive.</summary>
        public const int MaxEntries = 10;

        private static readonly object _sync = new();

        private static string? FilePathFor(string serial)
        {
            var baseDir = RadioConfig.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrWhiteSpace(serial)) return null;
            return Path.Combine(baseDir, "radios", RadioConfig.SanitizeRadioId(serial), "connect-history.json");
        }

        /// <summary>
        /// Record one attempt. Never throws; a declined write is traced and
        /// dropped.
        /// </summary>
        public static void Record(string serial, string path, string outcome, long durationMs)
        {
            try
            {
                var file = FilePathFor(serial);
                if (file == null) return;

                lock (_sync)
                {
                    var entries = LoadInternal(file);
                    entries.Add(new ConnectionAttemptRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        Path = path ?? "",
                        Outcome = outcome ?? "",
                        DurationMs = durationMs,
                    });
                    while (entries.Count > MaxEntries) entries.RemoveAt(0);

                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, JsonSerializer.Serialize(entries,
                        new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionHistory.Record({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>The recorded attempts for one radio, oldest first.
        /// Empty when nothing has been recorded (or the store is
        /// unreadable) — never null, never throws.</summary>
        public static List<ConnectionAttemptRecord> Load(string serial)
        {
            try
            {
                var file = FilePathFor(serial);
                if (file == null) return new List<ConnectionAttemptRecord>();
                lock (_sync) { return LoadInternal(file); }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionHistory.Load({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return new List<ConnectionAttemptRecord>();
            }
        }

        private static List<ConnectionAttemptRecord> LoadInternal(string file)
        {
            if (!File.Exists(file)) return new List<ConnectionAttemptRecord>();
            try
            {
                return JsonSerializer.Deserialize<List<ConnectionAttemptRecord>>(File.ReadAllText(file))
                       ?? new List<ConnectionAttemptRecord>();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionHistory: unreadable {file}: {ex.Message} — starting fresh",
                    System.Diagnostics.TraceLevel.Warning);
                return new List<ConnectionAttemptRecord>();
            }
        }
    }
}
