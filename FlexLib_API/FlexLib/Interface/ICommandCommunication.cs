using System.Net;

namespace Flex.Smoothlake.FlexLib;

public interface ICommandCommunication
{
    // JJFlex patch: immutable producer identity; MIGRATION.md item 14.
    object ConnectionSync { get; }
    CommandConnection CurrentConnection { get; }
    event System.Action<CommandConnectionChanged> ConnectionChanged;
    event System.Action<CommandDataReceived> DataReceived;
    bool IsConnected { get; }
    IPAddress LocalIp { set;  get; }

    event TcpCommandCommunication.TcpDataReceivedReadyEventHandler DataReceivedReady;
    event TcpCommandCommunication.IsConnectedChangedEventHandler IsConnectedChanged;
        
    bool Connect(IPAddress radioIp, int radioPort = 4992, int srcPort = 0);
    // JJFlex patch: Connect returns the identity it actually attempted, even if delivery overlaps reuse.
    bool Connect(IPAddress radioIp, int radioPort, int srcPort, out CommandConnection connection);
    void Disconnect();
    void Write(string msg);
}