#nullable enable

using System;

namespace Radios.SmartLink
{
    /// <summary>
    /// Single source of truth for human-readable messages derived from
    /// <see cref="SessionStatus"/>. Sprint 27 Track D extends this file
    /// with NetworkTest-informed richer messages (e.g., "UPnP failed,
    /// check router port forwarding"). For Sprint 26 the messages are
    /// intentionally simple and user-facing — no stack traces, no
    /// technical jargon, no codes.
    ///
    /// <para>
    /// All consumers (screen-reader announcements, status bar binding,
    /// future tooltip rendering, etc.) call into this class so user-visible
    /// phrasing stays consistent.
    /// </para>
    /// </summary>
    public static class SessionStatusMessages
    {
        /// <summary>
        /// Human-readable status line for display / screen-reader announcement.
        /// Short — fits in a status bar cell and reads well when announced
        /// inline by a screen reader.
        /// </summary>
        public static string ForStatus(SessionStatus status, int reconnectAttemptCount = 0, Exception? lastError = null)
        {
            return status switch
            {
                SessionStatus.Disconnected => "Disconnected",
                SessionStatus.Connecting => "Connecting to SmartLink…",
                SessionStatus.Connected => "Connected",
                SessionStatus.Reconnecting => reconnectAttemptCount > 0
                    ? $"Reconnecting (attempt {reconnectAttemptCount})"
                    : "Reconnecting…",
                SessionStatus.AuthorizationExpired => "Authorization expired — please sign in again",
                SessionStatus.ShutDown => "Session closed",
                _ => status.ToString(),
            };
        }

        /// <summary>
        /// Longer explanation suitable for a tooltip or details panel. Sprint 26
        /// returns the same text as <see cref="ForStatus"/>; Sprint 27 Track D
        /// will extend with NetworkTest results when applicable.
        /// </summary>
        public static string ForStatusDetailed(SessionStatus status, int reconnectAttemptCount = 0, Exception? lastError = null)
        {
            string baseLine = ForStatus(status, reconnectAttemptCount, lastError);
            if (lastError != null && status == SessionStatus.Reconnecting)
            {
                return $"{baseLine}. Last error: {lastError.Message}";
            }
            return baseLine;
        }
    }
}
