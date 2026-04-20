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
        /// <param name="flags">
        /// Connection flags passed through to the SmartLink protocol. Historical
        /// call sites have always passed 0; kept on the interface for future use.
        /// </param>
        void SendConnectMessageToRadio(string serial, int flags);

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

    /// <summary>Event payload for <see cref="IWanServer.WanRadioRadioListReceived"/>.</summary>
    public sealed class WanRadioListReceivedEventArgs : EventArgs
    {
        public IReadOnlyList<Radio> Radios { get; }

        public WanRadioListReceivedEventArgs(IReadOnlyList<Radio> radios)
        {
            Radios = radios;
        }
    }
}
