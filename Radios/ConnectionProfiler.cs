using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Records timestamped events during SmartLink connection lifecycle.
    /// Saves JSON profiles to %AppData%\JJFlexRadio\connection-profiles\ for analysis.
    /// Thread-safe: events can be recorded from any thread.
    /// </summary>
    public class ConnectionProfiler
    {
        internal static readonly string ProfileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JJFlexRadio", "connection-profiles");

        private readonly List<ProfileEvent> _events = new();
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly string _profileId;
        private string _outcome;

        /// <summary>
        /// The currently active profiler for this connection attempt.
        /// Set to a new instance at the beginning of each connection, null when not profiling.
        /// </summary>
        public static ConnectionProfiler Current { get; set; }

        public string ProfileId => _profileId;
        public string Outcome => _outcome;
        public long ElapsedMs => _sw.ElapsedMilliseconds;

        public ConnectionProfiler()
        {
            _profileId = _startTime.ToString("yyyyMMdd-HHmmss-fff");
        }

        /// <summary>
        /// Deletes profile and report files older than 7 days.
        /// Call once at app startup.
        /// </summary>
        public static void PurgeOldProfiles(int keepDays = 7)
        {
            try
            {
                if (!Directory.Exists(ProfileFolder)) return;

                var cutoff = DateTime.UtcNow.AddDays(-keepDays);
                int deleted = 0;

                foreach (var file in Directory.GetFiles(ProfileFolder, "*.json")
                    .Concat(Directory.GetFiles(ProfileFolder, "*.txt")))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }

                if (deleted > 0)
                    Tracing.TraceLine($"ConnectionProfiler: purged {deleted} files older than {keepDays} days", TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectionProfiler.PurgeOldProfiles: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>
        /// Records a connection lifecycle event with optional structured data.
        /// Thread-safe.
        /// </summary>
        public void RecordEvent(string eventName, Dictionary<string, object> data = null)
        {
            var evt = new ProfileEvent
            {
                Timestamp = DateTime.UtcNow,
                ElapsedMs = _sw.ElapsedMilliseconds,
                Event = eventName,
                Data = data
            };

            lock (_lock)
            {
                _events.Add(evt);
            }

            // Also trace for real-time visibility
            var dataStr = data != null ? JsonSerializer.Serialize(data) : "";
            Tracing.TraceLine($"[PROFILE] {evt.ElapsedMs}ms {eventName} {dataStr}", TraceLevel.Info);
        }

        /// <summary>
        /// Saves the profile to disk as JSON.
        /// </summary>
        /// <returns>The file path where the profile was saved, or null on failure.</returns>
        public string Save()
        {
            try
            {
                Directory.CreateDirectory(ProfileFolder);

                List<ProfileEvent> snapshot;
                lock (_lock)
                {
                    snapshot = new List<ProfileEvent>(_events);
                }

                var profile = new ConnectionProfile
                {
                    ProfileId = _profileId,
                    StartTime = _startTime,
                    TotalMs = _sw.ElapsedMilliseconds,
                    Outcome = _outcome,
                    EventCount = snapshot.Count,
                    Events = snapshot
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                string json = JsonSerializer.Serialize(profile, options);
                string filePath = Path.Combine(ProfileFolder, $"profile-{_profileId}.json");
                File.WriteAllText(filePath, json);

                Tracing.TraceLine($"[PROFILE] Saved {snapshot.Count} events to {filePath}", TraceLevel.Info);
                return filePath;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"[PROFILE] Save failed: {ex.Message}", TraceLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Record a terminal event (success/failure), set outcome, and save.
        /// </summary>
        public string RecordAndSave(string eventName, Dictionary<string, object> data = null)
        {
            _outcome = eventName;
            RecordEvent(eventName, data);
            return Save();
        }

        /// <summary>
        /// Returns a snapshot of recorded events for analysis.
        /// </summary>
        public List<ProfileEvent> GetEvents()
        {
            lock (_lock)
            {
                return new List<ProfileEvent>(_events);
            }
        }

        public class ConnectionProfile
        {
            public string ProfileId { get; set; }
            public DateTime StartTime { get; set; }
            public long TotalMs { get; set; }
            public string Outcome { get; set; }
            public int EventCount { get; set; }
            public List<ProfileEvent> Events { get; set; }
        }

        public class ProfileEvent
        {
            public DateTime Timestamp { get; set; }
            public long ElapsedMs { get; set; }
            public string Event { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public Dictionary<string, object> Data { get; set; }
        }
    }
}
