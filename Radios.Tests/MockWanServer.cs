#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.SmartLink;

namespace Radios.Tests
{
    /// <summary>
    /// In-memory IWanServer fake for unit tests. Simulates PropertyChanged
    /// synchronously on the mutator thread (matching the real FlexLib behavior
    /// identified in Sprint 26 Phase 0.1). Exposes hooks for tests to force
    /// connect failures, inject delays, fire events on demand, and observe
    /// call counts.
    /// </summary>
    public sealed class MockWanServer : IWanServer
    {
        private bool _isConnected;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<WanRadioConnectReadyEventArgs>? WanRadioConnectReady;
        public event EventHandler? WanApplicationRegistrationInvalid;
        public event EventHandler<WanRadioListReceivedEventArgs>? WanRadioRadioListReceived;
        public event EventHandler<WanTestConnectionResultsEventArgs>? TestConnectionResultsReceived;

        // --- Test-controllable behavior hooks ---

        /// <summary>
        /// When set, Connect() throws this exception instead of succeeding.
        /// Consumers can set/unset between calls to simulate flaky networks.
        /// </summary>
        public Exception? ConnectThrows { get; set; }

        /// <summary>
        /// When set, Connect() delays by this duration before either succeeding
        /// or throwing. Used for shutdown-while-Connect-in-flight tests.
        /// </summary>
        public TimeSpan? ConnectDelay { get; set; }

        /// <summary>
        /// When true, successful Connect() flips IsConnected to true (firing
        /// PropertyChanged). Default true. Set false to model Connect that
        /// completes but never actually reports connected.
        /// </summary>
        public bool ConnectFlipsToConnected { get; set; } = true;

        /// <summary>
        /// When set, PropertyChanged handlers can call this to re-enter the
        /// adapter-equivalent lock — used by Lock_IsReentrantOnSameThread tests.
        /// </summary>
        public Action? OnPropertyChangedHook { get; set; }

        // --- Observable call counters ---

        private int _connectCallCount;
        /// <summary>Thread-safe read of Connect() invocation count.</summary>
        public int ConnectCallCount => Volatile.Read(ref _connectCallCount);
        public int DisconnectCallCount { get; private set; }
        public int SendRegisterCallCount { get; private set; }
        public int SendConnectMessageCallCount { get; private set; }
        public string? LastSendConnectSerial { get; private set; }

        // --- IWanServer surface ---

        public bool IsConnected
        {
            get => _isConnected;
        }

        public void Connect()
        {
            Interlocked.Increment(ref _connectCallCount);

            if (ConnectDelay is { } delay)
            {
                Thread.Sleep(delay);
            }

            if (ConnectThrows is { } ex)
            {
                throw ex;
            }

            if (ConnectFlipsToConnected)
            {
                SetIsConnected(true);
            }
        }

        public void Disconnect()
        {
            DisconnectCallCount++;
            SetIsConnected(false);
        }

        public void SendRegisterApplicationMessageToServer(string programName, string platform, string jwt)
        {
            SendRegisterCallCount++;
        }

        public void SendConnectMessageToRadio(string serial, int flags)
        {
            SendConnectMessageCallCount++;
            LastSendConnectSerial = serial;
        }

        public int SendTestConnectionCallCount { get; private set; }
        public string? LastSendTestConnectionSerial { get; private set; }

        public void SendTestConnection(string serial)
        {
            SendTestConnectionCallCount++;
            LastSendTestConnectionSerial = serial;
        }

        // --- Test-side triggers ---

        /// <summary>
        /// Force IsConnected to the given value and fire PropertyChanged
        /// synchronously on the calling thread — mirroring FlexLib's behavior.
        /// </summary>
        public void ForceIsConnected(bool value)
        {
            SetIsConnected(value);
        }

        public void RaiseWanRadioConnectReady(string handle, string serial)
        {
            WanRadioConnectReady?.Invoke(this, new WanRadioConnectReadyEventArgs(handle, serial));
        }

        public void RaiseWanRadioRadioListReceived(IReadOnlyList<Radio> radios)
        {
            WanRadioRadioListReceived?.Invoke(this, new WanRadioListReceivedEventArgs(radios));
        }

        public void RaiseWanApplicationRegistrationInvalid()
        {
            WanApplicationRegistrationInvalid?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseTestConnectionResultsReceived(
            string radioSerial,
            bool upnpTcp,
            bool upnpUdp,
            bool forwardTcp,
            bool forwardUdp,
            bool natSupportsHolePunch)
        {
            TestConnectionResultsReceived?.Invoke(this, new WanTestConnectionResultsEventArgs(
                radioSerial, upnpTcp, upnpUdp, forwardTcp, forwardUdp, natSupportsHolePunch));
        }

        private void SetIsConnected(bool value)
        {
            if (_isConnected == value) return;
            _isConnected = value;
            OnPropertyChangedHook?.Invoke();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
        }
    }
}
