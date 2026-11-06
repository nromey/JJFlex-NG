#nullable enable
using System;
using System.Net;

namespace Flex.Smoothlake.FlexLib;

// JJFlex patch: each TLS producer owns its identity and its callbacks; MIGRATION.md item 15.
public class TlsCommandCommunication : CommandCommunicationBase
{
    public bool Connect(IPAddress radioIp, bool setupReply) => throw new NotImplementedException();
    public delegate void TCPDataReceivedReadyEventHandler(string msg);
    public delegate void IsConnectedChangedEventHandler(bool connected);

    private sealed class Session
    {
        public Session(SslClientTls12 client, CommandConnection connection)
        { Client = client; Connection = connection; }
        public SslClientTls12 Client { get; }
        public CommandConnection Connection { get; }
    }
    private Session? _session;
    public override IPAddress? LocalIp { get; set; }

    public override bool Connect(IPAddress radioIp, int radioPort, int srcPort, out CommandConnection? connection)
    {
        Session session;
        lock (ConnectionSync)
        {
            connection = CurrentConnection;
            if (CurrentConnection?.State == CommandConnectionState.Connected) return true;
            if (CurrentConnection?.State == CommandConnectionState.Connecting) return false;
            // JJFlex patch: retain the TLS 1.2/1.3 wrapper (MIGRATION.md item 2).
            session = StartClient(new SslClientTls12(radioIp.ToString(), radioPort.ToString(),
                srcPort, startPingThread: false, validateCert: false));
            connection = session.Connection;
        }
        try
        {
            session.Client.Connect().GetAwaiter().GetResult();
            lock (ConnectionSync)
            {
                if (session.Client.IsConnected && PublishConnected(session.Connection)) return true;
            }
        }
        catch (Exception) { }
        FinishClient(session);
        return false;
    }

    // Kept separate from socket creation so the actual callback wiring can be exercised offline.
    private Session StartClient(SslClientTls12 client)
    {
        lock (ConnectionSync)
        {
            var session = new Session(client, BeginConnection());
            _session = session;
            client.Disconnected += (sender, _) =>
            {
                if (ReferenceEquals(sender, session.Client)) FinishClient(session);
            };
            client.MessageReceivedReady += msg => PublishData(session.Connection, msg);
            return session;
        }
    }

    private void FinishClient(Session session)
    {
        lock (ConnectionSync)
        {
            // The sender's session, never the adapter's replacement client.
            session.Client.Disconnect();
            PublishDisconnected(session.Connection);
        }
    }

    public override void Disconnect()
    {
        lock (ConnectionSync)
        {
            var session = _session;
            if (session == null || session.Connection.State == CommandConnectionState.Disconnected) return;
            session.Client.Write("\x04");
            FinishClient(session);
        }
    }

    public override void Write(string msg)
    {
        // JJFlex patch: no dispatch gate while a caller may own a vendor collection lock.
        var session = System.Threading.Volatile.Read(ref _session);
        // JJFlex patch: write-time gate (MIGRATION.md item 21), against the session written.
        var pin = CommandWritePin.Current;
        if (!AdmitWrite(session?.Connection)) return;
        // JJFlex patch: write order (MIGRATION.md item 25). The question at the
        // bytes and the writer as one step against every other write on this
        // adapter; the outcome is reported after the gate is let go.
        string? refused = null, failure = null;
        bool ok = false;
        EnterWriteGate();
        try
        {
            if (!AdmitAtTheBytes(pin, out refused)) { /* refused at the bytes: told after the gate */ }
            // JJFlex patch: the write's outcome, after the writer (MIGRATION.md item 22).
            else if (session?.Connection.State != CommandConnectionState.Connected)
                failure = "the command connection is not connected";
            else ok = session.Client.TryWrite(msg, out failure);
        }
        finally { ExitWriteGate(); }
        if (refused != null) { ReportRefusal(pin, refused); return; }
        ReportWrite(pin, ok, failure);
    }
}
