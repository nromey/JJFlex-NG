#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// Whether opening the contact log may ask the operator to open or create
    /// a log file, when none is set or the one named is missing (#814).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found at the Logging mode bench, 2026-10-23: with no log file, every
    /// Alt+letter field jump tried to open the log, failed, and opened the
    /// Log Characteristics window, once per press. Noel ruled that a missing
    /// log is said plainly, with a choice to open an existing log, create a
    /// new one, or cancel; and a cancelled choice must not come back on every
    /// following field jump.
    /// </para>
    /// <para>
    /// So a cancel is remembered against the file it was about (an empty name
    /// when none was set), and the question is not asked again for that file
    /// until the operator takes an explicit log action (<see cref="AskAgain"/>):
    /// opening the full log form, or setting a log up. A field jump is not one.
    /// A different missing file is a different question and is asked.
    /// </para>
    /// <para>
    /// Kept apart from the VB logger so the rule is testable without a window;
    /// the logger holds one per log and asks it before showing anything.
    /// </para>
    /// </remarks>
    public sealed class LogFilePromptGate
    {
        private string? _declinedFor;

        /// <summary>True when the operator cancelled the choice for this file and
        /// has not since taken an explicit log action.</summary>
        public bool IsDeclined(string? logFile) =>
            _declinedFor != null && string.Equals(_declinedFor, logFile ?? "", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether to ask the operator about this missing log file now.</summary>
        public bool ShouldAsk(string? logFile) => !IsDeclined(logFile);

        /// <summary>The operator cancelled the choice for this file.</summary>
        public void Declined(string? logFile) => _declinedFor = logFile ?? "";

        /// <summary>The operator chose a log file; nothing is declined any more.</summary>
        public void Chosen() => _declinedFor = null;

        /// <summary>An explicit log action: the next attempt may ask again.</summary>
        public void AskAgain() => _declinedFor = null;
    }
}
