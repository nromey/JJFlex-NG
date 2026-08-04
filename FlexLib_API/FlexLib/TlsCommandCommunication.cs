#nullable enable

using System;
using System.Net;

namespace Flex.Smoothlake.FlexLib;

public class TlsCommandCommunication : ICommandCommunication
{
    // JJFlex patch: SslClient -> SslClientTls12 to enforce TLS 1.2/1.3 floor.
    // See MIGRATION.md.
    private SslClientTls12? _tlsToRadio;

    private bool _isConnected;
    public bool IsConnected => _tlsToRadio != null && _isConnected;

    public IPAddress? LocalIp { set; get; } = null;

    public bool Connect(IPAddress radioIp, bool setupReply)
    {
        throw new NotImplementedException();
    }

    public bool Connect(IPAddress radioIp, int radioPort, int srcPort = 0)
    {
        _tlsToRadio = new SslClientTls12(radioIp.ToString(), radioPort.ToString(), srcPort, startPingThread: false, validateCert: false);
        
        // set the event handlers prior to calling connect so we don't miss any events
        _tlsToRadio.Disconnected += _tlsToRadio_Disconnected;
        _tlsToRadio.MessageReceivedReady += _tlsToRadio_MessageReceivedReady;

        try
        {
            _tlsToRadio.Connect().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            _tlsToRadio = null;
            return false;
        }
            
        _isConnected = true;
        OnIsConnectedChanged(_isConnected);
        return true;
    }

    private void _tlsToRadio_Disconnected(object? sender, bool _)
    {
        Disconnect();
    }

    private void _tlsToRadio_MessageReceivedReady(string msg)
    {
        OnDataReceivedReady(msg);
    }

    public void Disconnect()
    {
        if (!_isConnected) 
            return;
            
        _tlsToRadio?.Write("\x04");
        _tlsToRadio?.Disconnect();
            
        _isConnected = false;
        OnIsConnectedChanged(_isConnected);
    }

    public void Write(string msg)
    {
        _tlsToRadio?.Write(msg);
    }

    public delegate void TCPDataReceivedReadyEventHandler(string msg);
    public event TcpCommandCommunication.TcpDataReceivedReadyEventHandler? DataReceivedReady;

    private void OnDataReceivedReady(string msg)
    {
        DataReceivedReady?.Invoke(msg);
    }

    public delegate void IsConnectedChangedEventHandler(bool connected);
    public event TcpCommandCommunication.IsConnectedChangedEventHandler? IsConnectedChanged;

    private void OnIsConnectedChanged(bool connected)
    {
        IsConnectedChanged?.Invoke(connected);
    }
}