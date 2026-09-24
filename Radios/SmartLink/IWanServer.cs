#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Flex.Smoothlake.FlexLib;

namespace Radios.SmartLink
{
    /// <summary>
    /// Abstraction over FlexLib's <see cref="Flex.Smoothlake.FlexLib.WanServer"/>.
    /// Every call surface FlexBase reaches into for SmartLink session management
    /// goes through this interface.
    ///
    /// <para>
    /// Buys four benefits for one abstraction:
    /// testability (mock implementations exercise the session owner state machine
    /// without a network), version insulation (future FlexLib updates don't break
    /// us at the API level), protocol safety (the adapter's reentrant lock
    /// serializes concurrent calls), and tracing (every call is traced before
    /// forwarding to FlexLib).
    /// </para>
    ///
    /// <para>
    /// <b>Threading:</b> FlexLib raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// synchronously on the mutator thread (confirmed in Sprint 26 Phase 0.1
    /// audit). Subscribers must not block on the adapter's lock while inside
    /// a property-change handler; they may read state freely because the lock
    /// is reentrant on the same thread.
    /// </para>
    /// </summary>
    public interface IWanServer : INotifyPropertyChanged
    {
        /// <summary>
        /// True when the SmartLink session SSL connection is live. Raises
        /// <see cref="INotifyPropertyChanged.PropertyChanged"/> with property name
        /// <c>"IsConnected"</c> when this changes.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Which connection is the newest one dialed: 0 before the first
        /// <see cref="Connect"/>, and one higher after every <see cref="Connect"/>
        /// that dials, whether or not the dial succeeds. A list whose
        /// <see cref="WanRadioListReceivedEventArgs.ConnectionGeneration"/> is
        /// below this came from a transport that has since been replaced.
        /// </summary>
        /// <remarks>
        /// <para><b>Lock-free, on purpose.</b> The reader that needs it is the
        /// radio-list handler, which runs on the receive thread of whichever
        /// transport produced the list — possibly a dying one — while the
        /// monitor thread may be inside a dial holding the adapter's lock for
        /// up to fifteen seconds. A read that took that lock would stall the
        /// receive thread behind the dial; a read that could return a value
        /// older than the list's own generation would be wrong. The adapter
        /// writes this before it creates the transport that carries the new
        /// generation, so a plain volatile read on any thread is at least as
        /// new as any list it is compared against (#619).</para>
        /// </remarks>
        long ConnectionGeneration { get; }

        /// <summary>Initiate a SmartLink session connect.</summary>
        void Connect();

        /// <summary>Tear down the SmartLink session cleanly.</summary>
        void Disconnect();

        /// <summary>
        /// Register this application with the SmartLink backend after auth.
        /// Called once on session establishment and again after token refresh.
        /// </summary>
        void SendRegisterApplicationMessageToServer(string programName, string platform, string jwt);

        /// <summary>
        /// Ask SmartLink to broker a connection to the radio with the given serial.
        /// Response arrives via <see cref="WanRadioConnectReady"/>.
        /// </summary>
        /// <param name="serial">Radio serial number.</param>
        /// <param name="holePunchPort">
        /// Sprint 27 Track F — the port number the client advertises to
        /// SmartLink for UDP hole-punch coordination. Pass 0 for Tier 1
        /// (manual forwarding) and Tier 2 (UPnP) — both paths don't need
        /// hole-punch. Pass the account's configured listen port for Tier 3
        /// (AutomaticHolePunch) so Flex's SmartLink server can coordinate a
        /// UDP hole-punch between radio and client. (Parameter was historically
        /// called <c>flags</c> with a 0-only call convention; Track F
        /// discovered it was always the hole-punch port.)
        /// </param>
        void SendConnectMessageToRadio(string serial, int holePunchPort);

        /// <summary>
        /// Fires when SmartLink has brokered a radio connection and returned
        /// the connection handle.
        /// </summary>
        event EventHandler<WanRadioConnectReadyEventArgs>? WanRadioConnectReady;

        /// <summary>
        /// Fires when app registration is rejected (bad JWT, expired token, etc.).
        /// Consumer should prompt re-auth.
        /// </summary>
        event EventHandler? WanApplicationRegistrationInvalid;

        /// <summary>
        /// Fires when SmartLink sends the list of radios available to this account.
        /// <para>
        /// Note: FlexLib's underlying event is misspelled <c>WanRadioRadioListRecieved</c>
        /// (sic). This interface corrects the spelling; the adapter handles the
        /// typo at the FlexLib boundary. Example of the adapter's version-insulation
        /// benefit documented in Sprint 26 Phase 0.1 findings.
        /// </para>
        /// </summary>
        event EventHandler<WanRadioListReceivedEventArgs>? WanRadioRadioListReceived;

        /// <summary>
        /// Sprint 27 Track C — request a SmartLink NetworkTest probe for the
        /// given radio serial. Fire-and-forget: the call returns immediately,
        /// and results arrive asynchronously via
        /// <see cref="TestConnectionResultsReceived"/>. Silently no-ops when
        /// the underlying SmartLink SSL connection is not up (FlexLib's behavior
        /// per the C.0 audit — no exception, no event).
        /// </summary>
        void SendTestConnection(string serial);

        /// <summary>
        /// Sprint 27 Track C — fires when SmartLink returns the results of a
        /// NetworkTest probe. Event fires on FlexLib's SSL listener thread;
        /// consumers must marshal to the UI thread before touching WPF state.
        /// </summary>
        event EventHandler<WanTestConnectionResultsEventArgs>? TestConnectionResultsReceived;
    }

    /// <summary>Event payload for <see cref="IWanServer.WanRadioConnectReady"/>.</summary>
    public sealed class WanRadioConnectReadyEventArgs : EventArgs
    {
        public string Handle { get; }
        public string Serial { get; }

        public WanRadioConnectReadyEventArgs(string handle, string serial)
        {
            Handle = handle;
            Serial = serial;
        }
    }

    /// <summary>
    /// Event payload for <see cref="IWanServer.WanRadioRadioListReceived"/>:
    /// the list, and which connection it was born on.
    /// </summary>
    /// <remarks>
    /// <para><b>The generation is provenance, stamped where the transport is
    /// created, never decided when the handler happens to run.</b> FlexLib
    /// invokes list handlers on the transport's own read loop, and closing
    /// that transport cancels the loop without waiting for a handler already
    /// in flight. So an old connection's callback can pause, the monitor can
    /// dial a new connection, and the old callback can then reach our handler
    /// after the new connection is up. Track L3 stamped the list with a
    /// number the OWNER kept current at that moment, which labelled exactly
    /// such a callback as the new connection's; Sol's review of L3 named it.
    /// The adapter now subscribes each transport's list event with that
    /// transport's generation captured in the subscription, so a late
    /// callback carries the generation it was actually born under, and the
    /// owner compares it with <see cref="IWanServer.ConnectionGeneration"/>
    /// to decide whether it describes the live connection (#619).</para>
    ///
    /// <para>The constructor takes the generation as a required argument so
    /// that no list can be raised without saying where it came from.</para>
    /// </remarks>
    public sealed class WanRadioListReceivedEventArgs : EventArgs
    {
        public IReadOnlyList<Radio> Radios { get; }

        /// <summary>
        /// The <see cref="IWanServer.ConnectionGeneration"/> of the connection
        /// whose transport delivered this list.
        /// </summary>
        public long ConnectionGeneration { get; }

        public WanRadioListReceivedEventArgs(IReadOnlyList<Radio> radios, long connectionGeneration)
        {
            Radios = radios;
            ConnectionGeneration = connectionGeneration;
        }
    }

    /// <summary>
    /// Sprint 27 Track C — event payload for
    /// <see cref="IWanServer.TestConnectionResultsReceived"/>. Mirrors the
    /// five booleans on FlexLib's <c>WanTestConnectionResults</c> plus the
    /// radio serial. Kept as a FlexLib-free DTO so consumers (including
    /// tests via MockWanServer) don't need to build a FlexLib object to
    /// simulate a probe response.
    /// </summary>
    public sealed class WanTestConnectionResultsEventArgs : EventArgs
    {
        public string RadioSerial { get; }
        public bool UpnpTcpWorking { get; }
        public bool UpnpUdpWorking { get; }
        public bool ForwardTcpWorking { get; }
        public bool ForwardUdpWorking { get; }
        public bool NatSupportsHolePunch { get; }

        public WanTestConnectionResultsEventArgs(
            string radioSerial,
            bool upnpTcpWorking,
            bool upnpUdpWorking,
            bool forwardTcpWorking,
            bool forwardUdpWorking,
            bool natSupportsHolePunch)
        {
            RadioSerial = radioSerial ?? string.Empty;
            UpnpTcpWorking = upnpTcpWorking;
            UpnpUdpWorking = upnpUdpWorking;
            ForwardTcpWorking = forwardTcpWorking;
            ForwardUdpWorking = forwardUdpWorking;
            NatSupportsHolePunch = natSupportsHolePunch;
        }
    }
}
