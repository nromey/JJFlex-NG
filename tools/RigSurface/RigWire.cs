using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace JJFlex.RigSurface
{
    /// <summary>A reply to one command: <c>R&lt;seq&gt;|&lt;code&gt;|&lt;message&gt;</c>.</summary>
    public readonly record struct WireReply(string Command, string Code, string Message)
    {
        /// <summary>Code "0" is the radio's success code. Anything else is a refusal.</summary>
        public bool Ok => string.Equals(Code, "0", StringComparison.Ordinal)
                          || string.Equals(Code, "00000000", StringComparison.Ordinal);

        public override string ToString() =>
            Ok ? $"{Command}: OK" : $"{Command}: ERROR {Code} ({Message})";
    }

    /// <summary>
    /// A raw connection to a FlexRadio's command channel on TCP 4992.
    ///
    /// <para><b>Why raw wire and not FlexLib.</b> This tool has to be able to
    /// say "the radio reports X" and be believed. FlexLib keeps a local cache of
    /// slice state and dedups a set against it, so a command the radio rejects
    /// can leave FlexLib reporting the value we asked for. Reading through
    /// FlexLib would therefore let a broken command path pass a test. Everything
    /// in <see cref="State"/> arrived from the radio and nothing else does.</para>
    ///
    /// <para><b>Why this is not a second operator.</b> Connecting does make us a
    /// MultiFlex client with our own handle. What keeps that honest is that the
    /// observing paths never issue a mutating command, and the exercising paths
    /// refuse to touch any object whose <c>client_handle</c> is not ours. See
    /// <see cref="StateOwnership"/>.</para>
    ///
    /// <para>LAN only, deliberately. There is no TLS here because there is no
    /// SmartLink here: the bench radio is on the local network and a tool that
    /// could reach a remote station over the internet is a tool that could
    /// disturb one by accident.</para>
    /// </summary>
    public sealed class RigWire : IDisposable
    {
        public const int DefaultPort = 4992;

        /// <summary>
        /// The bench 8600's fixed address on the local network. Overridable with
        /// --host on every command, and with the RIGSURFACE_HOST environment
        /// variable. It is hardcoded on purpose: this is bench tooling for one
        /// known radio, and having to type the address every time is how a
        /// safety tool stops being run.
        /// </summary>
#pragma warning disable S1313 // Deliberate: the bench radio's LAN address, not a production endpoint.
        public const string BenchHost = "192.168.50.100";
#pragma warning restore S1313

        public static string DefaultHost =>
            Environment.GetEnvironmentVariable("RIGSURFACE_HOST") is { Length: > 0 } fromEnv
                ? fromEnv
                : BenchHost;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Thread _reader;
        private readonly object _gate = new();
        private readonly Dictionary<string, WireReply> _replies = new(StringComparer.Ordinal);
        private readonly List<string> _messages = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private TextWriter? _trace;
        private int _seq;
        private bool _disposed;

        private RigWire(TcpClient client, string host)
        {
            _client = client;
            Host = host;
            _stream = client.GetStream();
            State = new RadioState();
            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "RigSurface wire reader",
            };
        }

        public string Host { get; }

        /// <summary>Our own client handle, e.g. "0x1A2B3C4D", from the H banner.</summary>
        public string? ClientHandle { get; private set; }

        /// <summary>The radio's protocol version banner.</summary>
        public string? Version { get; private set; }

        /// <summary>
        /// The radio's own state, fed exclusively by the status stream.
        /// </summary>
        public RadioState State { get; }

        /// <summary>Every M-line the radio has sent, newest last.</summary>
        public IReadOnlyList<string> Messages
        {
            get { lock (_gate) { return _messages.ToArray(); } }
        }

        /// <summary>
        /// When set, every line in and out is written here with a millisecond
        /// timestamp. This is what makes the composed mode with the UI driver
        /// work: the driver logs "pressed Alt+M at T", we log what the radio
        /// said at T plus latency, and the two are correlated afterwards.
        /// </summary>
        public TextWriter? Trace
        {
            get => _trace;
            set { lock (_gate) { _trace = value; } }
        }

        public static RigWire Connect(string? host = null, int port = DefaultPort, TimeSpan? timeout = null)
        {
            host ??= DefaultHost;
            var client = new TcpClient();
            try
            {
                client.Connect(host, port);
                client.ReceiveTimeout = 500;
                var wire = new RigWire(client, host);
                wire._reader.Start();
                wire.WaitForBanner(timeout ?? TimeSpan.FromSeconds(5));
                return wire;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private void WaitForBanner(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && ClientHandle is null)
            {
                Thread.Sleep(20);
            }
            if (ClientHandle is null)
            {
                throw new IOException(
                    $"Connected to {Host} but the radio never sent its handle banner. " +
                    "Either this is not a FlexRadio command port or the radio is refusing new clients.");
            }
        }

        // ---------------------------------------------------------------- //
        // Sending
        // ---------------------------------------------------------------- //

        /// <summary>
        /// Sends one command and waits for the radio's reply.
        /// <para>REFUSES anything that would key the transmitter. There is no
        /// flag on this method to override that; keying goes through
        /// <see cref="SendKeying"/>, which needs a consent object that only the
        /// transmit harness can mint.</para>
        /// </summary>
        public WireReply Send(string command, TimeSpan? timeout = null)
        {
            CommandEffect effect = TransmitGuard.Classify(command);
            if (effect != CommandEffect.Silent)
            {
                throw new TransmitRefusedException(
                    $"Refused '{command}': this command would key the radio ({effect}). " +
                    "Nothing on the non-transmitting path may do that.");
            }
            return SendRaw(command, timeout);
        }

        /// <summary>
        /// The only path that can key the radio. The consent object records who
        /// authorised it, at what power ceiling, and how much duty budget is
        /// left; it is checked here rather than trusted.
        /// </summary>
        public WireReply SendKeying(string command, TransmitConsent consent, TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(consent);
            CommandEffect effect = TransmitGuard.Classify(command);
            consent.Authorise(command, effect);
            return SendRaw(command, timeout);
        }

        private WireReply SendRaw(string command, TimeSpan? timeout)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string seq;
            lock (_gate)
            {
                seq = (++_seq).ToString(CultureInfo.InvariantCulture);
            }

            byte[] payload = Encoding.UTF8.GetBytes($"C{seq}|{command}\n");
            WriteTrace(">>", command);
            _stream.Write(payload, 0, payload.Length);
            _stream.Flush();

            TimeSpan wait = timeout ?? TimeSpan.FromSeconds(10);
            DateTime deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_replies.Remove(seq, out WireReply reply))
                    {
                        return reply with { Command = command };
                    }
                }
                Thread.Sleep(5);
            }
            throw new TimeoutException($"No reply from {Host} to: {command}");
        }

        /// <summary>
        /// Subscribes to the status streams we model. The radio sends the full
        /// picture once on subscription and deltas thereafter — it does NOT
        /// re-send everything if you subscribe again, so re-reading is not a way
        /// to refresh. That is why the model is fed continuously.
        /// </summary>
        public void SubscribeAll()
        {
            // Deliberately a SUBSET of what the application subscribes to, and
            // only long-established topics. Four of the newer subscriptions are
            // firmware-gated in this repository precisely because older firmware
            // halts the Opus audio stream a couple of packets after receiving a
            // subscription it does not recognise. An observer that broke the
            // application's audio while measuring it would be worse than
            // useless, so this list stays conservative.
            //
            // Note also that the subscribe token is not the status topic:
            // "sub tx all" produces "transmit ..." and "sub pan all" produces
            // "display pan ...". Panadapters matter here because RF gain,
            // preamp and band all live on them rather than on a slice.
            foreach (string what in new[]
                     {
                         "radio all", "slice all", "pan all", "tx all", "atu all",
                         "xvtr all", "client all", "gps all", "amplifier all", "meter all",
                     })
            {
                try
                {
                    SendRaw("sub " + what, TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    // An older firmware may not know a stream. Missing status is
                    // visible later as an unreadable field, which is the honest
                    // outcome; a hard failure here would be worse.
                }
            }
        }

        /// <summary>
        /// Waits until the radio stops talking, so a snapshot is taken of a
        /// settled picture rather than a half-delivered one.
        /// </summary>
        public bool Settle(TimeSpan quietFor, TimeSpan max)
        {
            DateTime deadline = DateTime.UtcNow + max;
            long lastVersion = -1;
            DateTime lastChange = DateTime.UtcNow;

            while (DateTime.UtcNow < deadline)
            {
                long version = State.Version;
                if (version != lastVersion)
                {
                    lastVersion = version;
                    lastChange = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastChange >= quietFor)
                {
                    return true;
                }
                Thread.Sleep(20);
            }
            return false;
        }

        /// <summary>
        /// Waits for the RADIO to report a field at an expected value.
        /// <para>This is the assertion primitive. It never inspects what we
        /// sent — only what came back. A field that never arrives times out and
        /// reads as unobserved, which is the truthful answer and not the same as
        /// "wrong".</para>
        /// </summary>
        public bool WaitForValue(RigField field, string expected, TimeSpan timeout)
            => WaitFor(field, actual => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase), timeout);

        /// <summary>Waits for the radio to report a field satisfying a predicate.</summary>
        public bool WaitFor(RigField field, Func<string?, bool> predicate, TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (predicate(State.Get(field))) return true;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(15);
            }
        }

        /// <summary>Waits for a field to move away from a known previous value.</summary>
        public bool WaitForChange(RigField field, string? previous, TimeSpan timeout)
            => WaitFor(field, actual => !string.Equals(actual, previous, StringComparison.Ordinal), timeout);

        // ---------------------------------------------------------------- //
        // Reading
        // ---------------------------------------------------------------- //

        private void ReadLoop()
        {
            var buffer = new byte[8192];
            var pending = new StringBuilder();

            while (!_stopping.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = _stream.Read(buffer, 0, buffer.Length);
                }
                catch (IOException ex)
                {
                    // A receive timeout is the normal case and just means the
                    // radio has nothing to say; loop and re-check cancellation.
                    // Anything else is a real socket failure, and continuing
                    // would spin forever while every read of the model quietly
                    // returned stale values.
                    if (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
                    {
                        continue;
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (read <= 0) break;

                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                string all = pending.ToString();
                int start = 0;
                int newline;
                while ((newline = all.IndexOf('\n', start)) >= 0)
                {
                    HandleLine(all[start..newline].TrimEnd('\r'));
                    start = newline + 1;
                }
                pending.Clear();
                pending.Append(all[start..]);
            }
        }

        private void HandleLine(string line)
        {
            if (line.Length == 0) return;
            WriteTrace("<<", line);

            char tag = line[0];
            string body = line[1..];

            switch (tag)
            {
                case 'R':
                {
                    string[] parts = body.Split('|', 3);
                    if (parts.Length >= 2)
                    {
                        var reply = new WireReply(string.Empty, parts[1], parts.Length > 2 ? parts[2] : string.Empty);
                        lock (_gate) { _replies[parts[0]] = reply; }
                    }
                    break;
                }

                case 'S':
                {
                    int bar = body.IndexOf('|', StringComparison.Ordinal);
                    if (bar < 0) return;
                    foreach (ParsedStatus status in StatusParser.Parse(body[(bar + 1)..]))
                    {
                        State.Fold(status);
                    }
                    break;
                }

                case 'M':
                    lock (_gate) { _messages.Add(body); }
                    break;

                case 'V':
                    Version = body;
                    break;

                case 'H':
                    ClientHandle = NormaliseHandle(body);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// The handle banner arrives as bare hex; slice status spells the same
        /// value "0x1A2B3C4D". Comparing the two textually without normalising
        /// is a silent way to conclude that none of your own slices are yours.
        /// </summary>
        public static string NormaliseHandle(string raw)
        {
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[2..];
            }
            return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
                ? "0x" + value.ToString("X8", CultureInfo.InvariantCulture)
                : raw.Trim();
        }

        private void WriteTrace(string direction, string text)
        {
            TextWriter? writer = _trace;
            if (writer is null) return;
            lock (_gate)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{_clock.Elapsed.TotalMilliseconds:F1}\t{direction}\t{text}"));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopping.Cancel();
            try { _stream.Dispose(); } catch (IOException) { }
            _client.Dispose();
            if (!_reader.Join(TimeSpan.FromSeconds(2)))
            {
                // Background thread; the process is allowed to leave it.
            }
            _stopping.Dispose();
        }
    }
}
