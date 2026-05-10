using System.Collections.Generic;

namespace JJTrace
{
    /// <summary>
    /// Programmatic outcome enum (snake_case) → human-readable display name.
    /// Static dictionary for now; broader localization is Sprint 30+ scope per
    /// memory/project_localization_strings_file.md. The display name is what
    /// shows up in the Archive Browser ListView and dropdown, and what the
    /// screen reader speaks on selection.
    /// </summary>
    public static class TraceOutcomeLabels
    {
        private static readonly Dictionary<string, string> _labels = new Dictionary<string, string>
        {
            { TraceSessionOutcome.Success,            "Success" },
            { TraceSessionOutcome.CleanExit,          "Clean exit" },
            { TraceSessionOutcome.AsRetryThenSuccess, "AS retry then success" },
            { TraceSessionOutcome.AsRetryFailed,      "AS retry failed" },
            { TraceSessionOutcome.SliceUnavailable,   "Slice unavailable" },
            { TraceSessionOutcome.ConnectionDropped,  "Connection dropped" },
            { TraceSessionOutcome.Killed,             "Killed" },
            { TraceSessionOutcome.Crashed,            "Crashed" },
            { TraceSessionOutcome.NetworkFailed,      "Network failed" },
            { TraceSessionOutcome.Unknown,            "Unknown" },
        };

        /// <summary>
        /// Display name for an outcome enum value. Falls back to a humanized
        /// version of the enum string (underscores → spaces, sentence case)
        /// when the outcome isn't in the static map — defensive against future
        /// outcomes added to TraceSessionOutcome that aren't yet labelled here.
        /// </summary>
        public static string Display(string outcome)
        {
            if (string.IsNullOrEmpty(outcome)) return "Unknown";
            if (_labels.TryGetValue(outcome, out string label)) return label;
            return Humanize(outcome);
        }

        /// <summary>
        /// Enumerate every known outcome paired with its display name. Used by
        /// the Archive Browser's filter dropdown to populate options without
        /// hard-coding the list in two places.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> AllOutcomes()
        {
            return _labels;
        }

        private static string Humanize(string raw)
        {
            char[] chars = raw.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '_') chars[i] = ' ';
            }
            string s = new string(chars).Trim();
            if (s.Length == 0) return "Unknown";
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
