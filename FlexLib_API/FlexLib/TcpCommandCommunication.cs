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

using AsyncAwaitBestPractices;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flex.Smoothlake.FlexLib;

// TODO: IDisposable?
public class TcpCommandCommunication : ICommandCommunication
{
    private StreamWriter? _writer;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            _isConnected = value;
            OnIsConnectedChanged(value);
        }
    }

    /// <summary>
    /// The local client IP address
    /// </summary>
    public IPAddress LocalIp { set; get; } = IPAddress.Parse("0.0.0.0");

    private const int COMMAND_PORT = 4992;

    private IPAddress _radioIp = IPAddress.Parse("0.0.0.0");
    private int _radioPort;
    private int _sourcePort;

    private TaskCompletionSource<bool> _tcs = new();
    public bool Connect(IPAddress radioIp, int radioPort = COMMAND_PORT, int srcPort = 0)
    {
        _radioIp = radioIp;
        _radioPort =  radioPort;
        _sourcePort = srcPort;
        
        _tcs = new TaskCompletionSource<bool>();
        
        Task.Run(TcpReadLoop).SafeFireAndForget();
        try
        {
            return _tcs.Task.Result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception connecting to radio TCP: {ex}");
            return false;
        }
    }

    private CancellationTokenSource _cts = new ();
    private int _connectLockFlag;
    private async Task TcpReadLoop()
    {
        if (Interlocked.CompareExchange(ref _connectLockFlag, 1, 0) != 0)
        {
            Debug.WriteLine("Already connecting to radio TCP");
            _tcs.SetException(new IOException("Already connecting to radio TCP"));
            return;
        }
        
        Debug.WriteLine("Attempting to perform TCP connection");

        _cts.Dispose();
        _cts = new CancellationTokenSource();

        using var client = new TcpClient(new IPEndPoint(IPAddress.Any, _sourcePort));
        client.ReceiveBufferSize = 1024;

        for (var retries = 0; retries < 20; ++retries)
        {
            try
            {
#if NET6_0_OR_GREATER
                var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _cts.Token);
                await client.ConnectAsync(new IPEndPoint(_radioIp, _radioPort), cts.Token);
#else
                await client.ConnectAsync(_radioIp, _radioPort);
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception connecting to radio TCP: {ex}");
#if !NET6_0_OR_GREATER
                await Task.Delay(TimeSpan.FromSeconds(1));
#endif
                continue;
            }

            if (client.Connected)
                break;
        }

        if (!client.Connected)
        {
            Debug.WriteLine("Timed out trying to connect to radio TCP");
            Interlocked.Exchange(ref _connectLockFlag, 0);
            _tcs.SetException(new IOException("Timed out trying to connect to radio TCP"));
            return;
        }
        
        IsConnected = client.Connected;
        _tcs.SetResult(true);

#if NET6_0_OR_GREATER
        await using var netStream = client.GetStream();
#else
        using var netStream = client.GetStream();
#endif
        
        using var streamReader = new StreamReader(netStream, Encoding.UTF8, true, client.ReceiveBufferSize);
        _writer = new StreamWriter(netStream)
        {
            AutoFlush = true
        };
        
        Debug.WriteLine("TCP Read Loop Begins");

        await ReadLoopAsync(streamReader, _cts.Token);
        
        IsConnected =  false;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
#if NET6_0_OR_GREATER

                var nextLine = await streamReader.ReadLineAsync(_cts.Token);
#else
                var nextLine = await streamReader.ReadLineAsync();
#endif
                if (nextLine == null)
                    break;

                if (nextLine == string.Empty)
                    continue;

                OnDataReceivedReady(nextLine);
            }
            catch (OperationCanceledException)
            {
            }
            catch(Exception ex) {
                Debug.WriteLine($"Exception reading from radio: {ex}");
                break;
            }
        }

        IsConnected = false;

        try
        {
#if NET6_0_OR_GREATER
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(0.5));
            await netStream.WriteAsync(new ReadOnlyMemory<byte>([0x04]), timeoutCts.Token);
#else
            await netStream.WriteAsync([0x04], 0, 1);
#endif
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ignoring exception writing dying gasp: {ex}");
        }

#if  NET6_0_OR_GREATER
        await _writer.DisposeAsync();
#else
        _writer.Dispose();
#endif
        _writer = null;
        
        Interlocked.Exchange(ref _connectLockFlag, 0);
        
        Debug.WriteLine("TCP Read Loop Ends");
    }

#if NET6_0_OR_GREATER
    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (line == string.Empty)
                    continue;

                OnDataReceivedReady(line);
            }
        }
        catch (OperationCanceledException)
        {
            // CancellationToken was triggered — expected, not an error
        }
        catch (IOException ex) when (ex.InnerException is SocketException se)
        {
            // Abrupt disconnect (reset, timeout, etc.)
            Debug.WriteLine($"Socket error: {se.SocketErrorCode} – {se.Message}");
        }
        catch (IOException ex)
        {
            // Other IO error (pipe broken, etc.)
            Debug.WriteLine($"IO error: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Stream was disposed (e.g. by a timeout or another thread)
        }
        finally
        {
            // When cancellation races with a socket error inside ReadLineAsync,
            // the underlying NetworkStream.ReadAsync ValueTask can go unobserved.
            // Perform one final read to drain and observe any faulted ValueTask
            // left behind by the StreamReader, preventing UnobservedTaskException.
            // Only drain when cancellation was actually requested — otherwise
            // (e.g. IOException from a dead socket) ReadLineAsync would block
            // indefinitely, preventing cleanup and bricking reconnection.
            if (ct.IsCancellationRequested)
            {
                try
                {
                    await reader.ReadLineAsync();
                }
                catch (Exception)
                {
                    // Expected — the stream is closed/faulted. We just need to observe it
                }
            }
        }
    }
#else
    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            using (ct.Register(() => reader.Close()))
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line == string.Empty)
                        continue;

                    OnDataReceivedReady(line);
                }
                // line == null → clean EOF / graceful disconnect
            }
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // ct.Register closed the stream
        }
        catch (OperationCanceledException)
        {
            // CancellationToken was triggered — expected, not an error
        }
        catch (IOException ex) when (ex.InnerException is SocketException se)
        {
            // Abrupt disconnect (reset, timeout, etc.)
            Debug.WriteLine($"Socket error: {se.SocketErrorCode} – {se.Message}");
        }
        catch (IOException ex)
        {
            // Other IO error (pipe broken, etc.)
            Debug.WriteLine($"IO error: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Stream was disposed from elsewhere (not due to cancellation)
        }
        finally
        {
            // When cancellation races with a socket error inside ReadLineAsync,
            // the underlying NetworkStream.ReadAsync ValueTask can go unobserved.
            // Perform one final read to drain and observe any faulted ValueTask
            // left behind by the StreamReader, preventing UnobservedTaskException.
            // Only drain when cancellation was actually requested — otherwise
            // (e.g. IOException from a dead socket) ReadLineAsync would block
            // indefinitely, preventing cleanup and bricking reconnection.
            if (ct.IsCancellationRequested)
            {
                try
                {
                    await reader.ReadLineAsync();
                }
                catch (Exception)
                {
                    // Expected — the stream is closed/faulted. We just need to observe it
                }
            }
        }
    }
#endif

    public void Disconnect()
    {
        if (!IsConnected || _cts.IsCancellationRequested)
            return;
        
        _cts.Cancel();
        
        Debug.WriteLine("Disconnecting from radio");
    }

    public void Write(string msg)
    {
        if (!IsConnected || _writer == null) 
            return;

        try
        {
            _writer?.Write(msg);
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"Error writing to radio TCP: {ex}");
            Disconnect();
        }
    }

    public async Task WriteAsync(string msg)
    {
        if (!IsConnected || _writer == null) 
            return;

        try
        {
            // TODO: Should probably have a timeout here eventually.
            await (_writer?.WriteAsync(msg) ?? Task.CompletedTask);
        }
        catch(Exception ex)
        {
            Debug.WriteLine($"Error writing to radio TCP: {ex}");
            Disconnect();
        }
    }

    /// <summary>
    /// Delegate event handler for the IsConnectedChanged event
    /// </summary>
    public delegate void IsConnectedChangedEventHandler(bool connected);
    /// <summary>
    /// This event is raised when the radio connects or disconnects from the client
    /// </summary>
    public event IsConnectedChangedEventHandler? IsConnectedChanged;

    private void OnIsConnectedChanged(bool connected)
    {
        IsConnectedChanged?.Invoke(connected);
    }

    /// <summary>
    /// Delegate event handler for the DataReceivedReady event
    /// </summary>
    public delegate void TcpDataReceivedReadyEventHandler(string msg);
    /// <summary>
    /// This event is raised when the client receives data from the radio (each message terminated by '\n')
    /// </summary>
    public event TcpDataReceivedReadyEventHandler? DataReceivedReady;

    private void OnDataReceivedReady(string msg)
    {
        DataReceivedReady?.Invoke(msg);
    }
}