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

        /// <summary>
        /// Forget everything recorded for one radio (task #102's reset).
        ///
        /// <para><b>This IS the reset, and there is nothing smaller.</b> A
        /// learned path is not stored anywhere — <see cref="ConnectPathPolicy"/>
        /// derives it from this ring every time it is asked. So "clear the
        /// learned path but keep the history" is not a thing that can exist:
        /// the history is the learned path, one derivation later. Any UI
        /// offering the choice would be offering a lie.</para>
        ///
        /// <para>What that costs, and callers must SAY so: the ring is also
        /// diagnostic data in its own right — the last ten attempts with their
        /// paths, outcomes and durations, which is how "how long is this connect
        /// actually taking" gets answered without a trace-file archaeology
        /// session. Clearing it throws that away too.</para>
        ///
        /// <para>Returns true when nothing is left on disk for this radio,
        /// including the case where there was nothing to begin with. False
        /// means the file is still there and the caller must not claim
        /// success.</para>
        /// </summary>
        public static bool Clear(string serial)
        {
            try
            {
                var file = FilePathFor(serial);
                if (file == null) return false;
                lock (_sync)
                {
                    if (!File.Exists(file)) return true;
                    File.Delete(file);
                    Tracing.TraceLine($"ConnectionHistory.Clear({serial}): ring deleted",
                        System.Diagnostics.TraceLevel.Info);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionHistory.Clear({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// Forget everything recorded for every radio this install knows.
        /// Returns how many rings were cleared and how many refused, so the
        /// caller can be honest about a partial result rather than reporting a
        /// round number it did not achieve.
        /// </summary>
        public static (int cleared, int failed) ClearAll()
        {
            int cleared = 0, failed = 0;
            try
            {
                var baseDir = RadioConfig.BaseDirectory;
                if (string.IsNullOrEmpty(baseDir)) return (0, 0);
                var root = Path.Combine(baseDir, "radios");
                if (!Directory.Exists(root)) return (0, 0);

                // Walk the store for ring files rather than asking
                // RadioConfig.ListKnownRadioIds, which lists only directories
                // that also hold a config.xml. A "forget everything" that
                // quietly skipped a radio whose profile is missing would leave
                // it still steering connects, which is the one outcome the
                // operator pressed this button to prevent. Only directories
                // that actually have a ring are counted — "cleared 5 radios"
                // when four never had a history sounds like more happened
                // than did.
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var file = Path.Combine(dir, "connect-history.json");
                    if (!File.Exists(file)) continue;
                    lock (_sync)
                    {
                        try
                        {
                            File.Delete(file);
                            cleared++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Tracing.TraceLine(
                                $"ConnectionHistory.ClearAll: {file}: {ex.Message}",
                                System.Diagnostics.TraceLevel.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionHistory.ClearAll: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
            }
            return (cleared, failed);
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
