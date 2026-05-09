using System;
using System.Collections.Generic;
using System.Reflection;

namespace JJTrace
{
    /// <summary>
    /// Tracks a single trace session's lifecycle — boot to outcome. Mutated during
    /// the session as connection events arrive; serialized to a manifest entry at
    /// archive time. Per project_trace_persistence_design.md.
    /// </summary>
    public sealed class TraceSession
    {
        public Guid SessionId { get; }
        public DateTime BootTimeUtc { get; }
        public DateTime? EndTimeUtc { get; private set; }
        public string AppVersion { get; }
        public string Outcome { get; private set; } = TraceSessionOutcome.Unknown;
        public string OutcomeDetail { get; private set; }
        public TraceConnectionTarget ConnectionTarget { get; private set; }
        public List<string> KeyEvents { get; } = new List<string>();
        public string VerbosityLevel { get; set; }

        private readonly object _lock = new object();

        public TraceSession()
            : this(DateTime.UtcNow)
        {
        }

        /// <summary>
        /// Construct with an explicit boot time. Used for reconstructing killed
        /// sessions at next-boot reconciliation: the leftover trace file's
        /// creation time approximates when the prior app actually booted, so
        /// the manifest entry's boot_time + duration_ms reflect reality
        /// instead of "now" (which would make every killed-session entry
        /// duration_ms ≈ 0 and break grep-by-duration queries).
        /// </summary>
        public TraceSession(DateTime bootTimeUtc)
        {
            SessionId = Guid.NewGuid();
            BootTimeUtc = bootTimeUtc;
            AppVersion = ResolveAppVersion();
        }

        /// <summary>
        /// Mark the outcome of this session. First call wins for the outcome field;
        /// subsequent calls append to OutcomeDetail to preserve later observations
        /// without losing the original signal.
        /// </summary>
        public void MarkOutcome(string outcome, string detail = null)
        {
            lock (_lock)
            {
                if (Outcome == TraceSessionOutcome.Unknown)
                {
                    Outcome = outcome ?? TraceSessionOutcome.Unknown;
                    OutcomeDetail = detail;
                }
                else if (!string.IsNullOrEmpty(detail))
                {
                    OutcomeDetail = string.IsNullOrEmpty(OutcomeDetail)
                        ? detail
                        : OutcomeDetail + "; " + detail;
                }
            }
        }

        /// <summary>
        /// Tag a load-bearing event observed during the session. Aggregated into
        /// the manifest entry's key_events array for grep-style discovery.
        /// </summary>
        public void AddKeyEvent(string evt)
        {
            if (string.IsNullOrEmpty(evt)) return;
            lock (_lock)
            {
                KeyEvents.Add(evt);
            }
        }

        /// <summary>
        /// Capture connection metadata for the session. Called when a radio
        /// connection is established (and refined as nickname / SmartLink account
        /// resolve). Subsequent calls overwrite — most recent wins.
        /// </summary>
        public void SetConnectionTarget(string serial, string nickname, string smartlinkAccount, string ip)
        {
            lock (_lock)
            {
                ConnectionTarget = new TraceConnectionTarget
                {
                    Serial = serial,
                    Nickname = nickname,
                    SmartlinkAccount = smartlinkAccount,
                    Ip = ip
                };
            }
        }

        /// <summary>
        /// Finalize the session. Sets EndTime if not already set. Idempotent —
        /// safe to call from multiple shutdown paths (clean exit, crash handler,
        /// dispose).
        /// </summary>
        public void End()
        {
            lock (_lock)
            {
                if (!EndTimeUtc.HasValue)
                {
                    EndTimeUtc = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// Render to a manifest entry. Outcome defaults to clean_exit if End() was
        /// called without an explicit MarkOutcome — the session reached a normal
        /// shutdown path. Killed sessions never reach End() so they get inferred at
        /// next-boot reconciliation.
        /// </summary>
        public TraceSessionEntry ToManifestEntry(string filename, long? compressedSize, long? uncompressedSize)
        {
            lock (_lock)
            {
                long? durationMs = null;
                if (EndTimeUtc.HasValue)
                {
                    durationMs = (long)(EndTimeUtc.Value - BootTimeUtc).TotalMilliseconds;
                }

                string outcome = Outcome;
                if (outcome == TraceSessionOutcome.Unknown && EndTimeUtc.HasValue)
                {
                    outcome = TraceSessionOutcome.CleanExit;
                }

                return new TraceSessionEntry
                {
                    SessionId = SessionId.ToString(),
                    Filename = filename,
                    BootTime = BootTimeUtc,
                    EndTime = EndTimeUtc,
                    DurationMs = durationMs,
                    Outcome = outcome,
                    OutcomeDetail = OutcomeDetail,
                    ConnectionTarget = ConnectionTarget,
                    TraceSizeUncompressedBytes = uncompressedSize,
                    TraceSizeCompressedBytes = compressedSize,
                    VerbosityLevel = VerbosityLevel,
                    AppVersion = AppVersion,
                    KeyEvents = KeyEvents.Count == 0 ? null : new List<string>(KeyEvents)
                };
            }
        }

        private static string ResolveAppVersion()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                AssemblyInformationalVersionAttribute info = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                {
                    int plusIdx = info.InformationalVersion.IndexOf('+');
                    return plusIdx > 0 ? info.InformationalVersion.Substring(0, plusIdx) : info.InformationalVersion;
                }
                return entry?.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>
    /// Process-wide current session, set when tracing starts and cleared at archive
    /// time. Lifecycle hooks (connection start, AS-retry, slice failure, etc.) call
    /// into TraceSession.Current via the AddKeyEvent / MarkOutcome / SetConnectionTarget
    /// methods. If Current is null (tracing was off at the moment) the call is a no-op.
    /// </summary>
    public static class TraceSessionContext
    {
        private static TraceSession _current;
        private static readonly object _lock = new object();

        public static TraceSession Current
        {
            get { lock (_lock) { return _current; } }
        }

        public static TraceSession BeginSession()
        {
            lock (_lock)
            {
                _current = new TraceSession();
                return _current;
            }
        }

        public static TraceSession EndSession()
        {
            lock (_lock)
            {
                TraceSession ending = _current;
                ending?.End();
                _current = null;
                return ending;
            }
        }

        /// <summary>
        /// Convenience for consumer code: tag a key event on the current session if
        /// one exists. Safe to call when tracing is off.
        /// </summary>
        public static void AddKeyEvent(string evt)
        {
            Current?.AddKeyEvent(evt);
        }

        /// <summary>
        /// Convenience for consumer code: mark outcome on the current session.
        /// Safe to call when tracing is off.
        /// </summary>
        public static void MarkOutcome(string outcome, string detail = null)
        {
            Current?.MarkOutcome(outcome, detail);
        }

        /// <summary>
        /// Convenience for consumer code: set connection target on the current
        /// session. Safe to call when tracing is off.
        /// </summary>
        public static void SetConnectionTarget(string serial, string nickname, string smartlinkAccount, string ip)
        {
            Current?.SetConnectionTarget(serial, nickname, smartlinkAccount, ip);
        }
    }
}
