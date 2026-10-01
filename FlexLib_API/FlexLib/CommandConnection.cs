#nullable enable
using System;

namespace Flex.Smoothlake.FlexLib;

// JJFlex patch: producer identity contract. Reapply with MIGRATION.md item 14.
/// <summary>One command transport attempt. Reference identity survives delayed callbacks.</summary>
public sealed class CommandConnection
{
    internal CommandConnection() { }
    public Guid Id { get; } = Guid.NewGuid();
    internal bool WasConnected { get; set; }
    public CommandConnectionState State { get; internal set; } = CommandConnectionState.Connecting;
}

public enum CommandConnectionState { Connecting, Connected, Disconnected }

public sealed class CommandConnectionChanged
{
    internal CommandConnectionChanged(CommandConnection connection, CommandConnectionState state)
    { Connection = connection; State = state; WasConnected = connection.WasConnected; }
    public CommandConnection Connection { get; }
    public CommandConnectionState State { get; }
    public bool WasConnected { get; }
}

public sealed class CommandDataReceived
{
    internal CommandDataReceived(CommandConnection connection, string text)
    { Connection = connection; Text = text; }
    public CommandConnection Connection { get; }
    public string Text { get; }
}

public enum RadioClientReportKind { Handle, Connected, Disconnected }

/// <summary>Immutable command-reader evidence, independent of discovery collection membership.</summary>
public sealed class RadioClientReport
{
    internal RadioClientReport(CommandConnection connection, RadioClientReportKind kind,
        uint handle, string? clientId = null, string? program = null, string? station = null,
        bool localPtt = false)
    {
        Connection = connection; Kind = kind; Handle = handle; ClientId = clientId;
        Program = program; Station = station; LocalPtt = localPtt;
    }
    public CommandConnection Connection { get; }
    public RadioClientReportKind Kind { get; }
    public uint Handle { get; }
    public string? ClientId { get; }
    public string? Program { get; }
    public string? Station { get; }
    public bool LocalPtt { get; }
}

// JJFlex patch: both adapters serialize identity selection and delivery on this gate.
// No socket wait holds it. Radio's teardown uses the same gate, so an old callback
// cannot pass a check and then tear down a replacement installed in the gap.
public abstract class CommandCommunicationBase : ICommandCommunication
{
    public object ConnectionSync { get; } = new object();
    public CommandConnection? CurrentConnection { get; private set; }
    public bool IsConnected => CurrentConnection?.State == CommandConnectionState.Connected;
    public abstract System.Net.IPAddress? LocalIp { get; set; }
    public bool Connect(System.Net.IPAddress radioIp, int radioPort = 4992, int srcPort = 0) =>
        Connect(radioIp, radioPort, srcPort, out _);
    public abstract bool Connect(System.Net.IPAddress radioIp, int radioPort, int srcPort, out CommandConnection? connection);
    public abstract void Disconnect();
    public abstract void Write(string msg);

    public event Action<CommandConnectionChanged>? ConnectionChanged;
    public event Action<CommandDataReceived>? DataReceived;
    public event TcpCommandCommunication.IsConnectedChangedEventHandler? IsConnectedChanged;
    public event TcpCommandCommunication.TcpDataReceivedReadyEventHandler? DataReceivedReady;

    protected CommandConnection BeginConnection()
    {
        lock (ConnectionSync)
        {
            if (CurrentConnection != null && CurrentConnection.State != CommandConnectionState.Disconnected)
                throw new InvalidOperationException("A command connection is already active.");
            var connection = new CommandConnection();
            CurrentConnection = connection;
            ConnectionChanged?.Invoke(new CommandConnectionChanged(connection, connection.State));
            return connection;
        }
    }

    protected bool PublishConnected(CommandConnection connection)
    {
        lock (ConnectionSync)
        {
            if (!ReferenceEquals(CurrentConnection, connection) || connection.State != CommandConnectionState.Connecting)
                return false;
            connection.WasConnected = true;
            connection.State = CommandConnectionState.Connected;
            ConnectionChanged?.Invoke(new CommandConnectionChanged(connection, connection.State));
            if (ReferenceEquals(CurrentConnection, connection) && connection.State == CommandConnectionState.Connected)
                IsConnectedChanged?.Invoke(true);
            return connection.State == CommandConnectionState.Connected;
        }
    }

    protected void PublishDisconnected(CommandConnection connection)
    {
        lock (ConnectionSync)
        {
            if (!ReferenceEquals(CurrentConnection, connection) || connection.State == CommandConnectionState.Disconnected)
                return;
            connection.State = CommandConnectionState.Disconnected;
            ConnectionChanged?.Invoke(new CommandConnectionChanged(connection, connection.State));
            IsConnectedChanged?.Invoke(false);
        }
    }

    protected void PublishData(CommandConnection connection, string text)
    {
        lock (ConnectionSync)
        {
            if (!ReferenceEquals(CurrentConnection, connection) || connection.State == CommandConnectionState.Disconnected)
                return;
            DataReceived?.Invoke(new CommandDataReceived(connection, text));
            DataReceivedReady?.Invoke(text);
        }
    }
}
