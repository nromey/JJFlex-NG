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
            _radioEndpoint = new IPEndPoint(radioIp, radioPort);
    }
    
    public void SendUdp(byte [] data)
    {
        SendUdp(data, data.Length);
    }

    public void SendUdp(byte[] data, int length)
    {
        if (_disposed)
        {
            return;
        }
        
        try
        {
            _client.Send(data, length, _radioEndpoint);
        } 
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception sending UDP packet: {ex}");
            Dispose();
        }
    }

    public async Task SendUdpAsync(byte[] data)
    {
        if (_disposed)
        {
            return;
        }
        
        try
        {
            await _client.SendAsync(data, data.Length, _radioEndpoint);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception sending UDP packet: {ex}");
            Dispose();
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception reading from UDP socket: {ex}");
                Dispose();
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