#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    /// <b>Static-event note:</b> FlexLib's <c>WanRadioRadioListRecieved</c>
    /// (sic) event is declared <c>static</c>, so multiple adapter instances
    /// would all fire on the same subscription. For Sprint 26 this is fine
    /// (N=1 session at a time). For Sprint 28+ multi-session this will need
    /// either a per-account filter OR an upstream FlexLib change to make the
    /// event instance-scoped.
    /// </para>
    /// </summary>
    public sealed class WanServerAdapter : IWanServer
    {
        private readonly WanServer _wan;
        private readonly System.Threading.Lock _gate = new();
        private readonly string _tracePrefix;
        private bool _disposed;

        /// <summary>
        /// Create an adapter wrapping a fresh <see cref="WanServer"/> instance.
        /// </summary>
        /// <param name="tracePrefix">
        /// String prepended to every trace line from this adapter. Typically
        /// <c>[session=&lt;id&gt;]</c> from the owning <see cref="WanSessionOwner"/>
        /// (D3 discipline — per-session trace tagging).
        /// </param>
        public WanServerAdapter(string tracePrefix = "")
        {
            _tracePrefix = string.IsNullOrEmpty(tracePrefix) ? "" : tracePrefix + " ";
            _wan = new WanServer();
            _wan.PropertyChanged += OnWanPropertyChanged;
            _wan.WanRadioConnectReady += OnWanRadioConnectReady;
            _wan.WanApplicationRegistrationInvalid += OnWanApplicationRegistrationInvalid;
            WanServer.WanRadioRadioListRecieved += OnWanRadioRadioListReceived;
        }

        // --- IWanServer surface ---

        public bool IsConnected
        {
            get
            {
                lock (_gate) return _wan.IsConnected;
            }
        }

        public void Connect()
        {
            Tracing.TraceLine($"{_tracePrefix}WanServerAdapter.Connect", TraceLevel.Info);
            lock (_gate)
            {
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

        public void SendConnectMessageToRadio(string serial, int flags)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.SendConnectMessageToRadio serial={serial} flags={flags}",
                TraceLevel.Info);
            lock (_gate)
            {
                _wan.SendConnectMessageToRadio(serial, flags);
            }
        }

        // --- Events (re-raised from FlexLib) ---

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<WanRadioConnectReadyEventArgs>? WanRadioConnectReady;
        public event EventHandler? WanApplicationRegistrationInvalid;
        public event EventHandler<WanRadioListReceivedEventArgs>? WanRadioRadioListReceived;

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

        private void OnWanRadioRadioListReceived(List<Radio> radios)
        {
            Tracing.TraceLine(
                $"{_tracePrefix}WanServerAdapter.WanRadioRadioListReceived count={radios.Count}",
                TraceLevel.Info);
            WanRadioRadioListReceived?.Invoke(this, new WanRadioListReceivedEventArgs(radios));
        }

        // --- Disposal ---

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Tracing.TraceLine($"{_tracePrefix}WanServerAdapter.Dispose", TraceLevel.Info);

            _wan.PropertyChanged -= OnWanPropertyChanged;
            _wan.WanRadioConnectReady -= OnWanRadioConnectReady;
            _wan.WanApplicationRegistrationInvalid -= OnWanApplicationRegistrationInvalid;
            WanServer.WanRadioRadioListRecieved -= OnWanRadioRadioListReceived;

            try
            {
                lock (_gate) { _wan.Disconnect(); }
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
