// ****************************************************************************
///*!	\file CommandCommunication.cs
// *	\brief Handles the command pipe to the radio
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2017-01-12
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flex.Smoothlake.FlexLib;

// JJFlex patch: reader, cancellation, completion and writer belong to one attempt.
// There is no adapter-wide reader cleanup that can touch its successor. MIGRATION.md item 16.
public class TcpCommandCommunication : CommandCommunicationBase
{
    private sealed class Attempt
    {
        public Attempt(CommandConnection connection, IPAddress ip, int port, int sourcePort)
        { Connection = connection; Ip = ip; Port = port; SourcePort = sourcePort; }
        public CommandConnection Connection { get; }
        public IPAddress Ip { get; }
        public int Port { get; }
        public int SourcePort { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StreamWriter? Writer;
    }
    private Attempt? _attempt;
    public override IPAddress LocalIp { get; set; } = IPAddress.Any;

    public override bool Connect(IPAddress radioIp, int radioPort, int srcPort, out CommandConnection? connection)
    {
        Attempt attempt;
        lock (ConnectionSync)
        {
            connection = CurrentConnection;
            if (IsConnected) return true;
            if (CurrentConnection?.State == CommandConnectionState.Connecting) return false;
            attempt = BeginAttempt(radioIp, radioPort, srcPort);
            connection = attempt.Connection;
        }
        _ = Task.Run(() => TcpReadLoop(attempt));
        return attempt.Completion.Task.GetAwaiter().GetResult();
    }

    private Attempt BeginAttempt(IPAddress ip, int port, int sourcePort)
    {
        lock (ConnectionSync)
        {
            var attempt = new Attempt(BeginConnection(), ip, port, sourcePort);
            _attempt = attempt;
            return attempt;
        }
    }

    private async Task TcpReadLoop(Attempt attempt)
    {
        try
        {
            using var client = new TcpClient(new IPEndPoint(IPAddress.Any, attempt.SourcePort));
            client.ReceiveBufferSize = 1024;
            for (int retries = 0; retries < 20 && !attempt.Cancellation.IsCancellationRequested; ++retries)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, attempt.Cancellation.Token);
                    await client.ConnectAsync(new IPEndPoint(attempt.Ip, attempt.Port), linked.Token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                { Debug.WriteLine($"Exception connecting to radio TCP: {ex}"); }
            }
            if (!client.Connected || attempt.Cancellation.IsCancellationRequested) return;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, client.ReceiveBufferSize);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            lock (ConnectionSync)
            {
                if (!ReferenceEquals(_attempt, attempt) || attempt.Cancellation.IsCancellationRequested) return;
                attempt.Writer = writer;
                if (!PublishConnected(attempt.Connection)) return;
                attempt.Completion.TrySetResult(true);
            }
            string? line;
            while ((line = await reader.ReadLineAsync(attempt.Cancellation.Token).ConfigureAwait(false)) != null)
                if (line.Length != 0) PublishData(attempt.Connection, line);
        }
        catch (Exception ex)
        { Debug.WriteLine($"TCP reader ended: {ex}"); }
        finally
        {
            FinishAttempt(attempt);
        }
    }

    private void FinishAttempt(Attempt attempt)
    {
        lock (ConnectionSync)
        {
            attempt.Cancellation.Cancel();
            attempt.Writer = null;
            attempt.Completion.TrySetResult(false);
            PublishDisconnected(attempt.Connection);
        }
    }

    public override void Disconnect()
    {
        lock (ConnectionSync)
        {
            if (_attempt == null || _attempt.Connection.State == CommandConnectionState.Disconnected) return;
            try { _attempt.Writer?.Write("\x04"); } catch (Exception) { }
            FinishAttempt(_attempt);
        }
    }

    public override void Write(string msg)
    {
        // JJFlex patch: callers can own vendor collection locks. Do not take the
        // reader's dispatch gate while sending; retain only this attempt's writer.
        var attempt = Volatile.Read(ref _attempt);
        var writer = attempt?.Writer;
        if (attempt?.Connection.State != CommandConnectionState.Connected || writer == null) return;
        try { writer.Write(msg); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error writing to radio TCP: {ex}");
            _ = Task.Run(() => FinishAttempt(attempt));
        }
    }

    public async Task WriteAsync(string msg)
    {
        var attempt = Volatile.Read(ref _attempt);
        var writer = attempt?.Writer;
        if (attempt?.Connection.State != CommandConnectionState.Connected || writer == null) return;
        try { await writer.WriteAsync(msg).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error writing to radio TCP: {ex}");
            _ = Task.Run(() => FinishAttempt(attempt));
        }
    }

    public delegate void IsConnectedChangedEventHandler(bool connected);
    public delegate void TcpDataReceivedReadyEventHandler(string msg);
}
