#nullable enable

namespace Radios.SmartLink
{
    /// <summary>
    /// Sprint 27 Track D — process-wide toggle for the "Verbose network
    /// diagnostics" preference. When true, status announcements +
    /// diagnostic panel text include raw error messages and probe details;
    /// when false (default), they stay short and user-facing. The Settings
    /// dialog's Network tab exposes a checkbox that flips this flag;
    /// persistence (if we choose to persist it beyond a process lifetime)
    /// is a later phase. Sprint 27 keeps it in-memory only — advanced users
    /// opt in per session.
    /// </summary>
    public static class DiagnosticVerbosityPreference
    {
        /// <summary>
        /// True when verbose output is requested. Thread-safe via volatile
        /// because the flag is read from the session monitor thread (in
        /// ForStatusRich call sites) and written from the UI thread (the
        /// Settings checkbox).
        /// </summary>
        private static volatile bool _verbose;

        public static bool Verbose
        {
            get => _verbose;
            set => _verbose = value;
        }
    }
}
