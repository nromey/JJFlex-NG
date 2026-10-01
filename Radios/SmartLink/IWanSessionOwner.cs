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

        /// <summary>
        /// Sprint 35 Track K (#259) — UTC time the server last delivered a
        /// radio list on this session, or null if it never has. The honest
        /// datum behind any "last heard from SmartLink at …" wording: it says
        /// when WE last heard, which is all we actually know — never a claim
        /// about the radio itself.
        /// </summary>
        DateTime? LastRadioListUtc { get; }

        /// <summary>
        /// The latest radio list together with the two facts that say whether
        /// it describes the present — is the session connected, and did the
        /// list arrive on the connection that is live now — read under one
        /// lock, so the three can never come from different moments.
        /// </summary>
        /// <remarks>
        /// <para><b>Why <see cref="LastRadioListUtc"/> is not enough.</b> A
        /// session keeps its list across a drop, on purpose: the post-drop
        /// diagnostic probe reads it to choose which radio to test, and the
        /// connect flow replays it for discovery. So after a reconnect, and
        /// before the new connection's first list, a connected session is
        /// still carrying the previous connection's list with a non-null
        /// timestamp. A non-null timestamp proves the session received SOME
        /// list, not that its current connection did — and a reader asking
        /// about now could take a listing the radio has since left as a
        /// current answer (#619).</para>
        ///
        /// <para><b>Where "arrived on the live connection" comes from.</b>
        /// Each list carries the generation of the transport it was born on,
        /// stamped by the adapter at the moment that transport was created —
        /// never a number the owner happened to hold when the callback
        /// reached it, which is what Track L3 did and what let a callback
        /// from a replaced transport, arriving late, read as the new
        /// connection's (Sol's review of L3). A list from a replaced transport
        /// is not held at all, so nothing here can be one.</para>
        ///
        /// <para>This does not replace <see cref="AvailableRadios"/> or
        /// <see cref="LastRadioListUtc"/>, whose meanings are unchanged. It is
        /// for a reader whose sentence is in the present tense.</para>
        /// </remarks>
        SessionRadioListSnapshot RadioListSnapshot { get; }

        /// <summary>
        /// Whether a list born on <paramref name="connectionGeneration"/> is
        /// this session's current knowledge AT THIS MOMENT: that connection
        /// is the newest dialed, this owner has not torn it down, its
        /// transport has not reported itself gone, and the operator still
        /// wants the session up. The same predicate the owner's own list
        /// handler accepts under; a consumer that receives a list later than
        /// that decision asks it again, with the list's own generation.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a consumer asks twice (#619, Sol's review of L5).</b>
        /// The owner accepts a list under its lock, releases the lock, and
        /// forwards it. Between those two a dial can begin and the NEXT
        /// connection's list can be accepted and forwarded, so the first
        /// list arrives at the consumer after the second and would be read
        /// as newer. Holding the owner's lock across the forward would
        /// serialize that, at the price of holding it through the
        /// coordinator and the intake — arbitrary callbacks, one of which
        /// takes the intake's own lock. So the provenance travels with the
        /// list instead, and the consumer compares it with the live one at
        /// the moment it consumes, under its own lock.</para>
        /// </remarks>
        bool ListIsCurrent(long connectionGeneration);

        /// <summary>
        /// Fired when something has happened that can make a list this
        /// session delivered stop being current: the live connection's
        /// transport reported itself gone, a new connection began dialing,
        /// the operator asked the session to disconnect, or the session is
        /// being disposed. It carries no verdict. A consumer holding a
        /// sighting from one of this session's lists asks
        /// <see cref="ListIsCurrent"/> again, with that sighting's own
        /// generation, and acts on the answer.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a consumer needs a signal and not only a question
        /// (#619, Noel's ruling of 2026-09-30).</b> The picker asks
        /// <see cref="ListIsCurrent"/> whenever a sighting arrives, and that
        /// is enough for a picker opened after a drop. But a drop raises no
        /// sighting, so a picker already open kept the SmartLink rows it had
        /// taken as live reading online, and eligible for auto-connect, until
        /// another list happened to arrive. The rows keep describing the last
        /// list; this tells the consumer that list may now be history.</para>
        /// <para>Raised outside this owner's lock, after the change is
        /// recorded, so a consumer that asks at once already gets the new
        /// answer. It fires on whichever thread witnessed the change — the
        /// transport's, the monitor's, or the caller of
        /// <see cref="Disconnect"/> or Dispose — so a consumer marshals before
        /// touching a window. Being raised when nothing a consumer holds has
        /// changed is harmless: the consumer re-asks and nothing moves.</para>
        /// </remarks>
        event EventHandler? ListCurrencyMayHaveChanged;

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
        /// Fired whenever the server delivers a radio list on THIS session —
        /// the first list after registration and every push after it. Sender
        /// is the owner, so <see cref="AccountId"/> attributes the list to an
        /// account (#259 presence model). Fires on the SmartLink receive
        /// thread; consumers must marshal before touching UI.
        /// </summary>
        event EventHandler<WanRadioListReceivedEventArgs>? RadioListReceived;

        /// <summary>
        /// Re-register the application with the SmartLink backend (e.g. after
        /// a JWT refresh). Requires the session to be connected. Counts as the
        /// current connection's registration for <see cref="TryClaimRegistration"/>.
        /// </summary>
        void ReRegister(string programName, string platform, string jwt);

        /// <summary>
        /// Sprint 35 Track K (#259) — wire the session to keep itself
        /// registered across reconnects. <paramref name="jwtProvider"/> runs
        /// on the monitor thread (may block on a silent token refresh); its
        /// bool argument is true only on the registration-invalid recovery
        /// path, where a forced refresh is the point. Returning null means
        /// "not without UI" — the session retries on a timer and NEVER raises
        /// a sign-in form.
        /// </summary>
        void EnableAutoRegistration(Func<bool, string?> jwtProvider, string programName, string platform = "Win10");

        /// <summary>
        /// Atomically claim the single registration the current connection
        /// needs. True = caller should send it; false = it is already sent
        /// (by the monitor's auto-register or an earlier explicit
        /// <see cref="ReRegister"/>). Resets when the connection drops.
        /// </summary>
        bool TryClaimRegistration();

        /// <summary>
        /// Broker a connection to a specific radio via SmartLink. Awaits the
        /// <see cref="IWanServer.WanRadioConnectReady"/> response.
        /// </summary>
        /// <param name="serial">Radio serial to connect to.</param>
        /// <param name="holePunchPort">
        /// Sprint 27 Track F — port to advertise for hole-punch coordination.
        /// 0 = don't request hole-punch (Tier 1 / Tier 2 paths). Non-zero =
        /// request Tier 3 UDP hole-punch on this port. See
        /// <see cref="IWanServer.SendConnectMessageToRadio"/> for the mapping
        /// from account <c>ConnectionMode</c> to this value.
        /// </param>
        /// <param name="cancellationToken">Cancellation for the await.</param>
        /// <returns>
        /// The WAN connection handle on success (the <c>handle</c> value from the
        /// broker's response, which the caller assigns to <c>Radio.WANConnectionHandle</c>),
        /// or null on timeout/cancellation/failure.
        /// </returns>
        System.Threading.Tasks.Task<string?> ConnectToRadio(string serial, int holePunchPort = 0, System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// Force a full reset: clear state, tear down the IWanServer, and
        /// re-enter Connecting. Used by UI "Reconnect" button.
        /// </summary>
        void Reset();

        /// <summary>
        /// Sprint 27 Track C — run (or retrieve a cached) SmartLink
        /// NetworkTest probe for <paramref name="radioSerial"/>. Non-blocking;
        /// Task completes with the <see cref="NetworkDiagnosticReport"/> when
        /// SmartLink responds or the timeout fires (whichever is first).
        /// See <see cref="NetworkTestRunner"/> for caching/dedup semantics.
        /// </summary>
        System.Threading.Tasks.Task<NetworkDiagnosticReport> RunNetworkDiagnosticAsync(
            string radioSerial,
            bool forceRefresh = false,
            TimeSpan? timeout = null,
            System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// Sprint 27 Track C — most recently cached NetworkTest report for
        /// the serial, or null if none. Bypasses TTL so UIs can display
        /// "last tested N minutes ago" without forcing a fresh probe.
        /// </summary>
        NetworkDiagnosticReport? GetLastNetworkReport(string radioSerial);

        /// <summary>
        /// Sprint 27 Track D — most recent NetworkTest report across any
        /// radio on this session, by report timestamp. Used by the status-
        /// announcement path that doesn't know a specific serial (e.g., a
        /// disconnect event that didn't tell us which radio dropped). Null
        /// when no probe has ever completed on this session.
        /// </summary>
        NetworkDiagnosticReport? MostRecentNetworkReport { get; }

        /// <summary>
        /// Sprint 27 Track C — fires on the SmartLink listener thread
        /// whenever a NetworkTest probe completes (fresh or late). UI
        /// consumers must marshal to the dispatcher thread before touching
        /// controls.
        /// </summary>
        event EventHandler<NetworkDiagnosticReport>? NetworkReportReady;
    }

    /// <summary>
    /// A session's latest radio list and how current it is, read together —
    /// see <see cref="IWanSessionOwner.RadioListSnapshot"/>.
    /// </summary>
    /// <param name="Radios">The latest list the server sent this session, on
    /// any connection. Empty before the first list.</param>
    /// <param name="ReceivedUtc">When it arrived, or null if no list ever
    /// has. Same value as <see cref="IWanSessionOwner.LastRadioListUtc"/>.</param>
    /// <param name="SessionConnected">The session is connected now.</param>
    /// <param name="ArrivedOnTheLiveConnection">The list arrived on the
    /// connection that is live now, and that connection's transport has not
    /// reported itself gone. False whenever the session is not connected,
    /// false after a reconnect until the new connection's first list lands,
    /// and false from the moment the transport reports its death — before
    /// the monitor has changed the session's status (#619).</param>
    /// <param name="ConnectionGeneration">The
    /// <see cref="IWanServer.ConnectionGeneration"/> of the connection the
    /// list arrived on; -1 before any list. With <see cref="SessionId"/> this
    /// names the list exactly, so a consumer that took rows from it can later
    /// prove those rows came from the list that is current now, rather than
    /// from an earlier connection's that happened to describe the same
    /// account.</param>
    /// <param name="SessionId">The session that holds the list, so the
    /// generation is scoped: generations restart when a session is rebuilt.</param>
    public readonly record struct SessionRadioListSnapshot(
        IReadOnlyList<Radio> Radios,
        DateTime? ReceivedUtc,
        bool SessionConnected,
        bool ArrivedOnTheLiveConnection,
        long ConnectionGeneration,
        string SessionId);

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
