#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;

namespace Flex.Smoothlake.FlexLib;

/// <summary>
/// JJFlex TLS 1.2/1.3 wrapper. Mirrors the v4.2.18 SslClient surface so that
/// TlsCommandCommunication only needs to swap the type name. The reason this
/// wrapper exists at all is that vendor SslClient calls
/// AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ... })
/// without an explicit EnabledSslProtocols, leaving protocol selection to OS
/// defaults. We pin TLS 1.3 + 1.2 so plaintext / SSLv3 / TLS 1.0 / TLS 1.1 can
/// never be silently negotiated. See MIGRATION.md.
/// </summary>
public class SslClientTls12
{
    private const int TCP_KEEPALIVE_PING_MS = 10000;
    private const int CANCEL_TOKEN_TIMEOUT_SECS = 15;
    private const SslProtocols PreferredProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;

    private StreamWriter? _writer;
    private readonly System.Timers.Timer _pingTimer = new(TCP_KEEPALIVE_PING_MS)
    {
        AutoReset = true
    };

    private readonly int _srcPort;
    private readonly int _dstPort;
    private readonly string _hostname;
    private readonly bool _validateCert;

    public SslClientTls12(string hostname, string port, int srcPort = 0, bool startPingThread = false,
        bool validateCert = true)
    {
        _dstPort = int.Parse(port);
        _srcPort = srcPort;
        _hostname = hostname;
        _validateCert = validateCert;

        if (!startPingThread)
            return;

        _pingTimer.Elapsed += PingEvent;
        _pingTimer.Enabled = true;
    }

    private readonly TaskCompletionSource<bool> _connectTcs = new();
    public Task<bool> Connect()
    {
        Task.Run(ReadLoop).SafeFireAndForget();
        return _connectTcs.Task;
    }

    public void Write(string message)
    {
        try
        {
            _writer?.WriteLine(message);
        }
        catch (Exception)
        {
        }
    }

    public async Task WriteAsync(string message)
    {
        try
        {
            await (_writer?.WriteAsync(message) ?? Task.CompletedTask);
        }
        catch (Exception)
        {
        }
    }

    private void PingEvent(object? source, System.Timers.ElapsedEventArgs e)
    {
        if (!IsConnected)
            return;

        try
        {
            Write("ping from client");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SslClientTls12::PingThread Exception: {ex}");
        }
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _readLoopLock = new SemaphoreSlim(1, 1);
    private async Task ReadLoop()
    {
        if (!await _readLoopLock.WaitAsync(0))
        {
            _connectTcs.SetException(new IOException("Already Connected"));
            return;
        }

        // Declared outside the try so the fallback can replace them and the
        // finally can dispose whichever pair ended up live.
        TcpClient? tcpClient = null;
        SslStream? sslStream = null;

        try
        {
            // A failed TLS handshake poisons the TCP connection underneath it:
            // bytes have already been exchanged and the peer has torn its side
            // down, so a second AuthenticateAsClientAsync on the same stream
            // fails with "the supplied message is incomplete / the signature was
            // not verified" no matter which protocol it asks for. The TLS 1.2
            // fallback therefore has to dial a NEW socket. Retrying in place —
            // as this wrapper originally did — could never succeed, which made
            // every radio that cannot negotiate TLS 1.3 unreachable over
            // SmartLink (found on Don's 6300, 2026-08-05). See MIGRATION.md.
            SslProtocols[] attempts = { PreferredProtocols, SslProtocols.Tls12 };

            for (int attempt = 0; attempt < attempts.Length; attempt++)
            {
                if (sslStream != null)
                {
                    sslStream.Dispose();
                    sslStream = null;
                }
                tcpClient?.Dispose();

                tcpClient = new TcpClient(new IPEndPoint(IPAddress.Any, _srcPort));
                try
                {
#if NET6_0_OR_GREATER
                    using var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CANCEL_TOKEN_TIMEOUT_SECS));
                    await tcpClient.ConnectAsync(_hostname, _dstPort, connectTimeoutCts.Token);
#else
                    await tcpClient.ConnectAsync(_hostname, _dstPort);
#endif
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Connection to SSL host failed: {ex}");
                    _connectTcs.SetException(ex);
                    return;
                }

                sslStream = new SslStream(tcpClient.GetStream(), false, _validateCert
                    ? ValidateServerCertificate
                    : new RemoteCertificateValidationCallback((_, _, _, _) => true), null);

                try
                {
#if NET6_0_OR_GREATER
                    using var authenticationTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CANCEL_TOKEN_TIMEOUT_SECS));
                    await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = _hostname,
                        EnabledSslProtocols = attempts[attempt]
                    },
                        authenticationTimeoutCts.Token);
#else
                    await sslStream.AuthenticateAsClientAsync(_hostname, null, attempts[attempt], false);
#endif
                    break; // negotiated
                }
                catch (Exception ex) when (attempt < attempts.Length - 1)
                {
                    // Older radio firmware offers TLS 1.2 only. The rejection
                    // surfaces as AuthenticationException on some stacks and as a
                    // truncated-frame IOException on others, so catch broadly and
                    // retry once on a clean socket.
                    Debug.WriteLine(
                        $"SslClientTls12: handshake with {attempts[attempt]} failed ({ex.Message}); reconnecting for TLS 1.2 only");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SSL Authentication Failed: {ex}");
                    _connectTcs.SetException(ex);
                    return;
                }
            }

            Debug.WriteLine("SslClientTls12 negotiated protocol: " + sslStream!.SslProtocol);

            IsConnected = true;
            _connectTcs.SetResult(true);

            using var reader = new StreamReader(sslStream);
            _writer = new StreamWriter(sslStream!)
            {
                AutoFlush = true
            };

            Debug.WriteLine("Beginning SSL Read Loop");

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
#if NET6_0_OR_GREATER
                    var nextLine = await reader.ReadLineAsync(_cts.Token);
#else
                    var nextLine = await reader.ReadLineAsync();
#endif
                    if (nextLine is null)
                    {
                        break;
                    }

                    OnMessageReceivedReady(nextLine);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading SSL: {ex}");
                    Disconnect();
                    break;
                }
            }

            Debug.WriteLine("Ending SSL Read Loop");

            IsConnected = false;
#if NET6_0_OR_GREATER
            await _writer.DisposeAsync();
#else
            _writer.Dispose();
#endif
            _writer = null;
            _pingTimer.Dispose();
        }
        finally
        {
            // Replaces the `using var` declarations these locals used to carry.
            sslStream?.Dispose();
            tcpClient?.Dispose();
            _readLoopLock.Release();
        }
    }

    public delegate void MessageReceived(string msg);
    public event MessageReceived? MessageReceivedReady;

    private void OnMessageReceivedReady(string msg)
    {
        MessageReceivedReady?.Invoke(msg);
    }

    private static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        Debug.WriteLine("Certificate error: {0}", sslPolicyErrors);

        // Do not allow this client to communicate with unauthenticated servers.
        return false;
    }

    public void Disconnect()
    {
        if (!IsConnected || _cts.IsCancellationRequested)
            return;

        _cts.Cancel();
    }


    public EventHandler<bool>? Disconnected;

    private void OnDisconnected()
    {
        Disconnected?.Invoke(this, false);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            _isConnected = value;
            if (!_isConnected)
                OnDisconnected();
        }
    }
}
