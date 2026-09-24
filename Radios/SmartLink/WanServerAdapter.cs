#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>
    /// Adapter forwarding <see cref="IWanServer"/> calls to FlexLib's
    /// <see cref="Flex.Smoothlake.FlexLib.WanServer"/>. Holds the reentrant
    /// <see cref="System.Threading.Lock"/> that serializes concurrent calls
    /// to the SmartLink backend (R1 decision: reentrant primitive chosen
    /// because FlexLib raises PropertyChanged synchronously on the mutator
    /// thread; see Phase 0.1 audit). Traces every call before forwarding.
    ///
    /// <para>
    /// <b>Static-event note (resolved Sprint 35 Track K):</b> FlexLib's
    /// <c>WanRadioRadioListRecieved</c> (sic) event is declared <c>static</c>,
    /// so multiple adapter instances all fired on the same subscription and a
    /// list could not be attributed to the session that received it. The
    /// vendored FlexLib now carries an additive, comment-marked instance
    /// event, <c>RadioListReceivedForThisConnection</c>, raised by the same
    /// parse path (see MIGRATION.md). This adapter subscribes THAT, on its own
    /// wrapped <see cref="WanServer"/>, so with one session held open per
    /// SmartLink account each session hears only its own account's lists.
    /// </para>
    ///
    /// <para>
    /// <b>One <see cref="WanServer"/> per dial (Sprint 45 Track L4, #619).</b>
    /// "For this connection" in that event's name means "for this WanServer
    /// instance", and a WanServer reused across reconnects — which this
    /// adapter did, one instance for its whole life — cannot say which of its
    /// transports a list came from: <c>WanServer.Connect</c> creates a fresh
    /// <c>SslClient</c> per dial, holds it in a private field, and the message
    /// delegate carries only the text. So the adapter retires its WanServer on
    /// every dial and creates a new one, which makes a WanServer's identity
    /// the same thing as its one transport's identity, and subscribes the new
    /// instance's list event with the new generation captured in the
    /// subscription. A list from an old transport can only ever reach the old
    /// instance's handler, and that handler can only ever say the old
    /// generation — however late the callback arrives. Nothing in the vendor
    /// tree changed for this.
    /// </para>
    /// </summary>
    public sealed class WanServerAdapter : IWanServer
    {
        // The instance for the connection most recently dialed. Replaced under
        // _gate by every Connect() that dials; read under _gate everywhere else.
        private WanServer _wan;
        private readonly System.Threading.Lock _gate = new();
        private readonly string _tracePrefix;
        private bool _disposed;

        // Written under _gate, BEFORE the WanServer that carries the value is
        // created; read without the lock (see IWanServer.ConnectionGeneration).
        private long _connectionGeneration;

        /// <summary>
        /// Create an adapter wrapping a fresh <see cref="WanServer"/> instance.
        /// </summary>
        /// <param name="tracePrefix">
        /// String prepended to every trace line from this adapter. Typically
        /// <c>[session=&lt;id&gt;]</c> from the owning <see cref="WanSessionOwner"/>
        /// (D3 discipline — per-session trace tagging).
        /// </param>
        public WanServerAdapter(string tracePrefix = "")
            : this(tracePrefix, static () => new WanServer())
        {
        }

        /// <summary>
        /// For the suite: the same adapter, with the <see cref="WanServer"/>
        /// instances supplied by <paramref name="newWanServer"/>, so the
        /// dial-retire-dial path can run against instances that never reach
        /// a network. Production uses the public constructor.
        /// </summary>
        internal WanServerAdapter(string tracePrefix, Func<WanServer> newWanServer)
        {
            _tracePrefix = string.IsNullOrEmpty(tracePrefix) ? "" : tracePrefix + " ";
            _newWanServer = newWanServer ?? throw new ArgumentNullException(nameof(newWanServer));
            // Generation 0: never dialed, so nothing can ever arrive on it. It
            // exists so IsConnected and the no-op sends have something to ask.
            _wan = Hook(_newWanServer(), generation: 0);
        }

        private readonly Func<WanServer> _newWanServer;

        // --- IWanServer surface ---

        public bool IsConnected
        {
            get
            {
                lock (_gate) return _wan.IsConnected;
            }
        }

        public long ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

        public void Connect()
        {
            Tracing.TraceLine($"{_tracePrefix}WanServerAdapter.Connect", TraceLevel.Info);
            lock (_gate)
            {
                // Stock WanServer.Connect returns when it already holds a
                // transport; the same rule here, so a redundant Connect does
                // not retire a live connection.
                if (_wan.IsConnected) return;

                var previous = _wan;
                long generation = _connectionGeneration + 1;
                // The generation is published before the transport that will
                // carry it exists. So a list from the NEW instance always reads
                // a current generation equal to its own, and a list from the
                // OLD instance — even one whose callback was already in flight
                // when this dial began — reads one that is higher. The
                // comparison lives in WanSessionOwner; the ordering that makes
                // it exact lives here (#619).
                Volatile.Write(ref _connectionGeneration, generation);
                Retire(previous);
                _wan = Hook(_newWanServer(), generation);
                Tracing.TraceLine($"{_tracePrefix}WanServerAdapter.Connect dialing connection {generation}", TraceLevel.Info);
                _wan.Connect();
            }
        }

        public void Disconnect()
        {
            Tracing.TraceLine($"{_tracePrefix}WanServerAdapter.Disconnect", TraceLevel.Info);
            lock (_gate)
            {
                _wan.Disconnect();
            }
        }

        public void SendRegisterApplicationMessageToServer(string programName, string platform, string jwt)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.SendRegisterApplicationMessageToServer program={programName} platform={platform}",
                TraceLevel.Info);
            lock (_gate)
            {
                _wan.SendRegisterApplicationMessageToServer(programName, platform, jwt);
            }
        }

        public void SendConnectMessageToRadio(string serial, int holePunchPort)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.SendConnectMessageToRadio serial={serial} holePunchPort={holePunchPort}",
                TraceLevel.Info);
            lock (_gate)
            {
                _wan.SendConnectMessageToRadio(serial, holePunchPort);
            }
        }

        public void SendTestConnection(string serial)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.SendTestConnection serial={serial}",
                TraceLevel.Info);
            lock (_gate)
            {
                _wan.SendTestConnection(serial);
            }
        }

        // --- Events (re-raised from FlexLib) ---

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<WanRadioConnectReadyEventArgs>? WanRadioConnectReady;
        public event EventHandler? WanApplicationRegistrationInvalid;
        public event EventHandler<WanRadioListReceivedEventArgs>? WanRadioRadioListReceived;
        public event EventHandler<WanTestConnectionResultsEventArgs>? TestConnectionResultsReceived;

        // --- Instance lifetime ---

        /// <summary>
        /// Subscribe a fresh <see cref="WanServer"/>, with the generation of the
        /// one connection it will ever dial captured in its list subscription.
        /// </summary>
        /// <remarks>
        /// The list subscription is a closure over <paramref name="generation"/>
        /// and is never unhooked, not even by <see cref="Retire"/>: a callback
        /// from a transport that has been replaced still arrives, still
        /// labelled with the generation it was born under, and the owner
        /// traces it as stale rather than mistaking it for the live
        /// connection's. Keeping it visible is the point. The retired instance
        /// is referenced by nothing else once its read loop ends, so it is
        /// collected in the ordinary way.
        /// </remarks>
        private WanServer Hook(WanServer wan, long generation)
        {
            wan.PropertyChanged += OnWanPropertyChanged;
            wan.WanRadioConnectReady += OnWanRadioConnectReady;
            wan.WanApplicationRegistrationInvalid += OnWanApplicationRegistrationInvalid;
            wan.TestConnectionResultsReceived += OnTestConnectionResultsReceived;
            wan.RadioListReceivedForThisConnection += radios => OnWanRadioRadioListReceived(generation, radios);
            return wan;
        }

        /// <summary>
        /// Unhook a <see cref="WanServer"/> whose one transport is gone. Its
        /// connection-state, connect-ready, registration and probe events can
        /// only describe that dead transport, so they are no longer forwarded;
        /// its list event stays hooked (see <see cref="Hook"/>).
        /// </summary>
        private void Retire(WanServer wan)
        {
            wan.PropertyChanged -= OnWanPropertyChanged;
            wan.WanRadioConnectReady -= OnWanRadioConnectReady;
            wan.WanApplicationRegistrationInvalid -= OnWanApplicationRegistrationInvalid;
            wan.TestConnectionResultsReceived -= OnTestConnectionResultsReceived;
        }

        // --- FlexLib bridging handlers ---

        private void OnWanPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.PropertyChanged {e.PropertyName}",
                TraceLevel.Verbose);
            // Re-raise with our adapter as the sender so consumers don't hold a
            // reference to the wrapped WanServer.
            PropertyChanged?.Invoke(this, e);
        }

        private void OnWanRadioConnectReady(string handle, string serial)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.WanRadioConnectReady handle={handle} serial={serial}",
                TraceLevel.Info);
            WanRadioConnectReady?.Invoke(this, new WanRadioConnectReadyEventArgs(handle, serial));
        }

        private void OnWanApplicationRegistrationInvalid()
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.WanApplicationRegistrationInvalid",
                TraceLevel.Error);
            WanApplicationRegistrationInvalid?.Invoke(this, EventArgs.Empty);
        }

        private void OnWanRadioRadioListReceived(long generation, List<Radio> radios)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.WanRadioRadioListReceived count={radios.Count} connection={generation} newest={ConnectionGeneration}",
                TraceLevel.Info);
            WanRadioRadioListReceived?.Invoke(this, new WanRadioListReceivedEventArgs(radios, generation));
        }

        private void OnTestConnectionResultsReceived(WanTestConnectionResults r)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.TestConnectionResultsReceived serial={r.radio_serial} upnpTcp={r.upnp_tcp_port_working} upnpUdp={r.upnp_udp_port_working} fwdTcp={r.forward_tcp_port_working} fwdUdp={r.forward_udp_port_working} holePunch={r.nat_supports_hole_punch}",
                TraceLevel.Info);
            TestConnectionResultsReceived?.Invoke(this, new WanTestConnectionResultsEventArgs(
                r.radio_serial ?? string.Empty,
                r.upnp_tcp_port_working,
                r.upnp_udp_port_working,
                r.forward_tcp_port_working,
                r.forward_udp_port_working,
                r.nat_supports_hole_punch));
        }

        // --- Disposal ---

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Tracing.TraceLine($"{_tracePrefix}WanServerAdapter.Dispose", TraceLevel.Info);

            lock (_gate)
            {
                Retire(_wan);
                try
                {
                    _wan.Disconnect();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(
                        $"{_tracePrefix}WanServerAdapter.Dispose Disconnect threw: {ex.Message}",
                        TraceLevel.Error);
                }
            }
        }
    }
}
