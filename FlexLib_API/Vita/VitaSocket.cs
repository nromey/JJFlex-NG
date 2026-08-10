// ****************************************************************************
///*!	\file VitaSocket.cs
// *	\brief A Socket for use in communicating with the Vita protocol
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2012-03-05
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.Vita;

namespace Vita;

public class VitaSocket : IDisposable
{
    private readonly VitaDataReceivedCallback _callback;
    private readonly UdpClient _client = new ()
    {
        ExclusiveAddressUse = false
    };

    // JJFlex patch: no longer readonly — in hole-punch mode the receive loop
    // may retarget it onto the radio's observed UDP source (source latch).
    private IPEndPoint _radioEndpoint;

    // JJFlex patch: hole-punch mode only. The radio-side NAT may rewrite the
    // UDP source port (2026-08-06 capture: punch port 40420, packets arrived
    // from 7604), and that rewritten port is the only inbound UDP path to the
    // radio. No protocol message carries it — it is only learnable from
    // received datagrams. Never enabled for forwarded or LAN connections,
    // where the configured port is authoritative and latching could misaim.
    private readonly bool _latchToSource;
    private volatile bool _stopping;

    private bool _disposed;
        
    private const int MIN_UDP_PORT = 1025;
    private const int MAX_UDP_PORT = 65535;

    // JJFlex patch: optional trace hook so the host app's tracing system can see
    // UDP socket health in field traces. Debug.WriteLine is invisible outside a
    // debugger, which let WAN UDP failures die silently. See MIGRATION.md.
    public static Action<string> TraceSink { get; set; }

    private int _consecutiveReceiveFailures;

    // JJFlex diag 2026-08-10 (708 TX-audio): send-side telemetry, local patch to
    // vendor code. TX opus reaches AddTXData at 100 pkts/s with a healthy
    // endpoint, yet the radio meters silence. Every hop from AddTXData to the
    // Send() call below has been verified byte-equivalent to the working 4.1.5
    // code (serializer harness-proven identical), so this instruments the one
    // unobserved hop: datagrams actually handed to the OS — how many, from
    // which local port, to which destination, and the exact head bytes of the
    // first TX packet. On LAN there is no other client-to-radio UDP, so these
    // lines appear only while keyed — exactly the window under test.
    private int _sentDatagrams, _sentBytes, _sentVitaTx;
    private int _skippedSends;
    private int _sendTelemetryLastLog, _skipTelemetryLastLog;
    private bool _firstSendLogged, _firstVitaTxLogged;

    public int Port { get; }

    public IPAddress Ip => ((IPEndPoint)_client.Client.LocalEndPoint)?.Address;

    public VitaSocket(int port, VitaDataReceivedCallback callback)
    {
        _callback = callback;
        Port = port;
        _client.Client.ReceiveBufferSize = 150000 * 5;
        try
        {
            _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8);
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"Failed to set DSCP EF marking (non-admin?): {ex.Message}");
        }

        // JJFlex patch: suppress WSAECONNRESET propagation on this unconnected UDP
        // socket (SIO_UDP_CONNRESET). On Windows, an ICMP "port unreachable" echo
        // for a datagram WE sent makes the NEXT Send/Receive throw
        // SocketException(ConnectionReset). During the WAN hole-punch window such
        // bounces are expected while both sides race to open their NAT mappings —
        // without this ioctl, the first bounce fed the catch blocks below, which
        // used to Dispose() the socket and silently kill the whole UDP data plane.
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
            _client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch (Exception ex) // not supported off-Windows; harmless to skip
        {
            Debug.WriteLine($"SIO_UDP_CONNRESET not applied: {ex.Message}");
            TraceSink?.Invoke($"VitaSocket: SIO_UDP_CONNRESET not applied: {ex.Message}");
        }

        var done = false;
        while (!done)
        {
            try
            {
                _client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));   
                done = true;
            }
            catch (Exception ex)
            {
                ++Port;
                if (Port > 6010)
                    throw new Exception(ex.Message);
            }
        }
        
        Debug.WriteLine($"Vita Socket has bound port {Port}");
        TraceSink?.Invoke($"VitaSocket: bound local UDP port {Port}"); // JJFlex patch

        Task.Factory.StartNew(
            ReceiveLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            HighPriorityTaskScheduler.Instance);
    }
    
    public VitaSocket(int port, VitaDataReceivedCallback callback, IPAddress radioIp, int radioPort, bool latchToSource = false) : this (port, callback)
    {
        // In addition to creating the VitaSocket, for WAN we must also send the
        // 'client udp_register' command to the radio over the created UDP socket

        _latchToSource = latchToSource; // JJFlex patch

        //ensure port is within range before assigning endpoint
        if (radioPort is >= MIN_UDP_PORT and <= MAX_UDP_PORT)
        {
            _radioEndpoint = new IPEndPoint(radioIp, radioPort);
            TraceSink?.Invoke($"VitaSocket: WAN mode, radio endpoint {_radioEndpoint} latch={latchToSource}"); // JJFlex patch
        }
        else
        {
            // JJFlex patch: this used to fail silently — _radioEndpoint stayed
            // null and every WAN send then threw and killed the socket.
            TraceSink?.Invoke(
                $"VitaSocket: radio UDP port {radioPort} out of range [{MIN_UDP_PORT}..{MAX_UDP_PORT}] — WAN sends DISABLED");
        }
    }
    
    public void SendUdp(byte [] data)
    {
        SendUdp(data, data.Length);
    }

    public void SendUdp(byte[] data, int length)
    {
        // JJFlex patch: endpoint is null when the WAN ctor's port-range guard
        // failed (already traced there once) — don't throw per-send over it.
        if (_disposed || _radioEndpoint == null)
        {
            TraceSkippedSend(); // JJFlex diag 2026-08-10: a silent no-op here would exactly mimic dead TX audio
            return;
        }

        try
        {
            _client.Send(data, length, _radioEndpoint);
            TraceSendSuccess(data, length); // JJFlex diag 2026-08-10
        }
        catch (Exception ex)
        {
            // JJFlex patch: do NOT Dispose() here. A transient send failure
            // (e.g. an ICMP-unreachable echo during the hole-punch race) must
            // not kill the data plane; the registration loop retries anyway.
            Debug.WriteLine($"Exception sending UDP packet: {ex}");
            TraceSink?.Invoke($"VitaSocket: send failed: {ex.Message}");
        }
    }

    public async Task SendUdpAsync(byte[] data)
    {
        if (_disposed || _radioEndpoint == null)
        {
            TraceSkippedSend(); // JJFlex diag 2026-08-10
            return;
        }

        try
        {
            await _client.SendAsync(data, data.Length, _radioEndpoint);
            TraceSendSuccess(data, data.Length); // JJFlex diag 2026-08-10
        }
        catch (Exception ex)
        {
            // JJFlex patch: same as SendUdp — transient failures never dispose.
            Debug.WriteLine($"Exception sending UDP packet: {ex}");
            TraceSink?.Invoke($"VitaSocket: send failed: {ex.Message}");
        }
    }

    // JJFlex diag 2026-08-10 (708 TX-audio): successful-send telemetry.
    // Counters are unsynchronized on purpose — sends come from the audio
    // callback and the registration loop, and a torn diag counter is harmless.
    private void TraceSendSuccess(byte[] data, int length)
    {
        _sentDatagrams++;
        _sentBytes += length;
        // VITA ExtDataWithStream (pkt_type 3 in the top nibble) = TX opus / net CW.
        var isVitaTx = length > 0 && (data[0] >> 4) == 3;
        if (isVitaTx) _sentVitaTx++;

        if (!_firstSendLogged || (isVitaTx && !_firstVitaTxLogged))
        {
            _firstSendLogged = true;
            if (isVitaTx) _firstVitaTxLogged = true;
            var n = Math.Min(28, length);
            TraceSink?.Invoke(
                $"VitaSocket: first {(isVitaTx ? "VITA-TX " : "")}send: local={_client.Client.LocalEndPoint} dest={_radioEndpoint} len={length} head={BitConverter.ToString(data, 0, n)}");
        }

        var now = Environment.TickCount;
        if (now - _sendTelemetryLastLog >= 1000)
        {
            _sendTelemetryLastLog = now;
            TraceSink?.Invoke(
                $"VitaSocket: sent {_sentDatagrams} datagrams ({_sentVitaTx} vita-tx, {_sentBytes} bytes) to {_radioEndpoint} from {_client.Client.LocalEndPoint}, skippedTotal={_skippedSends}");
            _sentDatagrams = 0;
            _sentBytes = 0;
            _sentVitaTx = 0;
        }
    }

    // JJFlex diag 2026-08-10 (708 TX-audio): the guarded early-returns above are
    // the only silent drop point in the client-side TX chain; make them loud.
    private void TraceSkippedSend()
    {
        _skippedSends++;
        var now = Environment.TickCount;
        if (now - _skipTelemetryLastLog >= 1000)
        {
            _skipTelemetryLastLog = now;
            TraceSink?.Invoke(
                $"VitaSocket: DROPPING sends: disposed={_disposed} endpointNull={_radioEndpoint == null} droppedTotal={_skippedSends}");
        }
    }
    
    /// <summary>
    /// Synchronous receive loop.  Runs entirely on the MMCSS "Pro Audio" thread
    /// created by HighPriorityTaskScheduler so that the callback (and everything
    /// it triggers) executes at real-time priority.  Using synchronous Receive()
    /// avoids the async-continuation problem where awaits resume on a normal-
    /// priority ThreadPool thread.
    /// </summary>
    private void ReceiveLoop()
    {
        Debug.WriteLine("UDP Read Loop Begins");

        IPEndPoint? remoteEP = null;

        while (!_stopping)
        {
            try
            {
                byte[] data = _client.Receive(ref remoteEP);
                _consecutiveReceiveFailures = 0; // JJFlex patch

                // JJFlex patch (source latch): adopt the radio's observed UDP
                // source as the send target. Guarded to the radio's own address
                // so a stray datagram can't hijack the stream.
                var target = _radioEndpoint;
                if (_latchToSource && target != null && remoteEP != null
                    && remoteEP.Port != target.Port
                    && remoteEP.Address.Equals(target.Address))
                {
                    _radioEndpoint = new IPEndPoint(remoteEP.Address, remoteEP.Port);
                    TraceSink?.Invoke(
                        $"VitaSocket: source latch — radio UDP arrives from {remoteEP}, retargeting sends (was {target})");
                }

                _callback?.Invoke(remoteEP!, data, data.Length);
            }
            catch (ObjectDisposedException)
            {
                break; // socket closed in Dispose
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted)
            {
                break; // socket closed in Dispose
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // JJFlex patch: ICMP port-unreachable echo for a datagram WE sent —
                // expected while the hole punch races, never fatal for UDP. The
                // SIO_UDP_CONNRESET ioctl in the ctor should prevent this arm from
                // firing at all; kept as belt-and-suspenders where the ioctl is
                // unavailable.
                TraceSink?.Invoke("VitaSocket: ignoring ConnectionReset (ICMP unreachable echo)");
            }
            catch (Exception ex)
            {
                // JJFlex patch: previously ANY receive exception disposed the socket
                // on the spot, silently killing the UDP data plane. Tolerate
                // transient errors; only give up after a sustained failure streak,
                // and say so in the trace.
                Debug.WriteLine($"Exception reading from UDP socket: {ex}");
                TraceSink?.Invoke($"VitaSocket: receive failed ({_consecutiveReceiveFailures + 1} consecutive): {ex.Message}");
                if (++_consecutiveReceiveFailures >= 50)
                {
                    TraceSink?.Invoke("VitaSocket: 50 consecutive receive failures — closing socket");
                    Dispose();
                }
            }
        }

        Debug.WriteLine("UDP Read Loop Ends");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _stopping = true;
            _client.Dispose(); // unblocks synchronous Receive()
        }
    }
}