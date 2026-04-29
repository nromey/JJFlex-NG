using System.Net;

namespace Flex.Smoothlake.FlexLib;

public interface ICommandCommunication
{
    bool IsConnected { get; }
    IPAddress LocalIp { set;  get; }

    event TcpCommandCommunication.TcpDataReceivedReadyEventHandler DataReceivedReady;
    event TcpCommandCommunication.IsConnectedChangedEventHandler IsConnectedChanged;
        
    bool Connect(IPAddress radioIp, int radioPort = 4992, int srcPort = 0);
    void Disconnect();
    void Write(string msg);
}