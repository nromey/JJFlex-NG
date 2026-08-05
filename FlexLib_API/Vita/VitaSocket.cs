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

    private readonly IPEndPoint _radioEndpoint;
    private volatile bool _stopping;

    private bool _disposed;
        
    private const int MIN_UDP_PORT = 1025;
    private const int MAX_UDP_PORT = 65535;

    // JJFlex patch: optional trace hook so the host app's tracing system can see
    // UDP socket health in field traces. Debug.WriteLine is invisible outside a
    // debugger, which let WAN UDP failures die silently. See MIGRATION.md.
    public static Action<string> TraceSink { get; set; }

    private int _consecutiveReceiveFailures;

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
    
    public VitaSocket(int port, VitaDataReceivedCallback callback, IPAddress radioIp, int radioPort) : this (port, callback)
    {
        // In addition to creating the VitaSocket, for WAN we must also send the 
        // 'client udp_register' command to the radio over the created UDP socket

        //ensure port is within range before assigning endpoint
        if (radioPort is >= MIN_UDP_PORT and <= MAX_UDP_PORT)
        {
            _radioEndpoint = new IPEndPoint(radioIp, radioPort);
            TraceSink?.Invoke($"VitaSocket: WAN mode, radio endpoint {_radioEndpoint}"); // JJFlex patch
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
            return;
        }

        try
        {
            _client.Send(data, length, _radioEndpoint);
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
            return;
        }

        try
        {
            await _client.SendAsync(data, data.Length, _radioEndpoint);
        }
        catch (Exception ex)
        {
            // JJFlex patch: same as SendUdp — transient failures never dispose.
            Debug.WriteLine($"Exception sending UDP packet: {ex}");
            TraceSink?.Invoke($"VitaSocket: send failed: {ex.Message}");
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