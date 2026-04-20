#nullable enable

using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;

namespace Radios.SmartLink
{
    /// <summary>
    /// Status of a <see cref="IWanSessionOwner"/>. Drives status-bar messages
    /// and screen-reader announcements (Sprint 26 Phase 3 binds to this enum).
    /// </summary>
    public enum SessionStatus
    {
        /// <summary>Not connected and not trying. Initial state; state after explicit Disconnect.</summary>
        Disconnected,
        /// <summary>First connect attempt in flight.</summary>
        Connecting,
        /// <summary>Session is up and healthy.</summary>
        Connected,
        /// <summary>Previously connected, currently retrying after a drop.</summary>
        Reconnecting,
        /// <summary>Authorization expired or rejected. User must sign in again.</summary>
        AuthorizationExpired,
        /// <summary>Final state after shutdown; monitor thread has exited.</summary>
        ShutDown,
    }

    /// <summary>
    /// Owns a SmartLink session lifecycle. Holds an <see cref="IWanServer"/>
    /// (via the adapter) plus a dedicated monitor thread that implements the
    /// behavioral spec from <c>docs/planning/hole-punch-lifeline-ragchew.md</c>:
    /// retry with exponential backoff on Connect failure, wake on
    /// <see cref="IWanServer.PropertyChanged"/> <c>"IsConnected"</c> transitions,
    /// clean shutdown via an explicit flag.
    ///
    /// <para>
    /// <b>D4 discipline:</b> consumers must access the owner via
    /// <see cref="SmartLinkSessionCoordinator.ActiveSession"/> on every access.
    /// Never capture an <c>IWanSessionOwner</c> reference into a field — a
    /// captured reference becomes stale when future tab-switching rebinds the
    /// active session. Re-accessing each time is correct; caching is a
    /// review-blocker.
    /// </para>
    /// </summary>
    public interface IWanSessionOwner : IDisposable
    {
        /// <summary>Stable identity for this session (GUID). Used in trace prefixes.</summary>
        string SessionId { get; }

        /// <summary>Account identity the session was created for.</summary>
        string AccountId { get; }

        /// <summary>Mirror of <see cref="IWanServer.IsConnected"/>; kept as a convenience.</summary>
        bool IsConnected { get; }

        /// <summary>Human-meaningful status for status-bar binding.</summary>
        SessionStatus Status { get; }

        /// <summary>
        /// Most recent exception from a failed Connect attempt, or null if the
        /// last attempt succeeded (or none has run). Populated for UI consumption.
        /// </summary>
        Exception? LastError { get; }

        /// <summary>
        /// Number of consecutive reconnect attempts since the last success.
        /// Resets to 0 on a successful Connect. UI may render "Reconnecting
        /// (attempt N)" from this.
        /// </summary>
        int ReconnectAttemptCount { get; }

        /// <summary>
        /// List of radios available on this session, populated from
        /// <see cref="IWanServer.WanRadioRadioListReceived"/>. Empty until
        /// SmartLink delivers the first list.
        /// </summary>
        IReadOnlyList<Radio> AvailableRadios { get; }

        /// <summary>Audio output primitive for this session (D2 discipline).</summary>
        ISessionAudioSink AudioSink { get; }

        /// <summary>Fired whenever <see cref="Status"/> transitions.</summary>
        event EventHandler<SessionStatus>? StatusChanged;

        /// <summary>
        /// Fired when a signal-strength observable crosses a configured threshold.
        /// Sprint 26 doesn't consume this; Sprint 28's smart-squelch-off-axis
        /// subscribes for focus-alert behavior.
        /// </summary>
        event EventHandler<SignalThresholdEventArgs>? SignalThresholdCrossed;

        /// <summary>
        /// Start the session. Idempotent — calling while already connecting or
        /// connected is a no-op.
        /// </summary>
        void Connect();

        /// <summary>
        /// Explicit disconnect. Does NOT re-enter the retry loop; the monitor
        /// thread settles into <see cref="SessionStatus.Disconnected"/> and
        /// waits for a new <see cref="Connect"/> or <see cref="Reset"/>.
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Re-register the application with the SmartLink backend (e.g. after
        /// a JWT refresh). Requires the session to be connected.
        /// </summary>
        void ReRegister(string programName, string platform, string jwt);

        /// <summary>
        /// Broker a connection to a specific radio via SmartLink. Awaits the
        /// <see cref="IWanServer.WanRadioConnectReady"/> response.
        /// </summary>
        /// <returns>True when the broker responds positively; false on timeout.</returns>
        System.Threading.Tasks.Task<bool> ConnectToRadio(string serial, System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// Force a full reset: clear state, tear down the IWanServer, and
        /// re-enter Connecting. Used by UI "Reconnect" button.
        /// </summary>
        void Reset();
    }

    /// <summary>Event payload for signal-strength threshold crossings.</summary>
    public sealed class SignalThresholdEventArgs : EventArgs
    {
        /// <summary>Slice letter (A, B, C…) whose signal crossed the threshold.</summary>
        public string SliceLetter { get; }

        /// <summary>Current S-meter value in the slice's native units.</summary>
        public double CurrentValue { get; }

        /// <summary>Threshold that was crossed.</summary>
        public double Threshold { get; }

        /// <summary>True if crossing upward (signal rose through threshold); false if dropping through.</summary>
        public bool IsRisingEdge { get; }

        public SignalThresholdEventArgs(string sliceLetter, double currentValue, double threshold, bool isRisingEdge)
        {
            SliceLetter = sliceLetter;
            CurrentValue = currentValue;
            Threshold = threshold;
            IsRisingEdge = isRisingEdge;
        }
    }
}
