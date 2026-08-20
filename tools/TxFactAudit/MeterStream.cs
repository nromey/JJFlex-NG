using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using JJFlex.RigSurface;

namespace JJFlex.TxFactAudit
{
    /// <summary>
    /// One meter's live reading, scaled out of the radio's raw integer.
    /// </summary>
    public sealed record MeterSample(int Index, string Name, string Units, double Value,
                                     DateTimeOffset At, long Count);

    /// <summary>
    /// Receives the radio's meter readings.
    ///
    /// <para><b>Why this had to exist.</b> The command channel carries meter
    /// DESCRIPTORS and nothing else — name, source, units, range. The readings
    /// travel separately, as VITA-49 extension-data packets over UDP, to
    /// whichever port a client nominates. So an observer built only on the
    /// command channel can prove what every meter MEANS and not one thing about
    /// what any of them SAYS, and the facts that matter most in a transmit
    /// diagnosis — what the radio hears, what drive it is making, how much power
    /// left, what the antenna reflected — are exactly the ones it cannot
    /// see.</para>
    ///
    /// <para><b>What it does to the radio: one thing.</b> It nominates a UDP
    /// port for its OWN client with <c>client udpport</c>. That is per-client
    /// routing, not station state — no other operator's stream moves, nothing is
    /// written that needs restoring, and there is nothing here to put back.</para>
    ///
    /// <para><b>The scaling is FlexLib's, deliberately copied rather than
    /// shared.</b> A raw integer becomes a reading by a rule that depends on the
    /// meter's declared units, and if this harness inherited that rule from the
    /// same code the application uses, an error in it would read as agreement.
    /// Two independent implementations of the same documented arithmetic is the
    /// only arrangement in which agreement means anything.</para>
    /// </summary>
    public sealed class MeterStream : IDisposable
    {
        /// <summary>The VITA class code the radio stamps on meter packets.</summary>
        private const ushort MeterClassCode = 0x8002;

        private readonly UdpClient _udp;
        private readonly Thread _reader;
        private readonly CancellationTokenSource _stopping = new();
        private readonly object _gate = new();
        private readonly Dictionary<int, MeterSample> _latest = new();
        private readonly Dictionary<int, (string Name, string Units)> _descriptors;
        private readonly double _voltDenominator;

        private long _packets;
        private long _datagrams;
        private bool _disposed;

        /// <summary>Every datagram that arrived, meter-class or not. Separating
        /// this from the meter count is what distinguishes "the radio is not
        /// sending" from "the radio is sending something we do not
        /// understand" — two findings with completely different fixes.</summary>
        public long DatagramsReceived => Interlocked.Read(ref _datagrams);

        /// <summary>The radio's answer to our port request, kept so a run can
        /// print it rather than infer from silence.</summary>
        public string RegistrationReply { get; private set; } = "";

        private MeterStream(UdpClient udp, Dictionary<int, (string, string)> descriptors,
                            double voltDenominator)
        {
            _udp = udp;
            _descriptors = descriptors;
            _voltDenominator = voltDenominator;
            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "TxFactAudit meter reader",
            };
        }

        /// <summary>How many meter packets have arrived. Zero after a few
        /// seconds means the radio is not streaming to us, which is a finding
        /// and not a reason to print zeroes.</summary>
        public long PacketsReceived => Interlocked.Read(ref _packets);

        /// <summary>
        /// Opens a UDP port, tells the radio to stream meters to it, and starts
        /// listening. The meter descriptors already on the wire supply the
        /// index-to-name mapping; a reading whose index we have no descriptor
        /// for is kept under its number rather than dropped, because a meter we
        /// cannot name is still evidence.
        /// </summary>
        public static MeterStream Open(RigWire wire)
        {
            ArgumentNullException.ThrowIfNull(wire);

            var descriptors = new Dictionary<int, (string, string)>();
            foreach (RigObject m in wire.State.GetObjects(RigTarget.Meter))
            {
                m.Fields.TryGetValue("nam", out string? name);
                m.Fields.TryGetValue("unit", out string? unit);
                descriptors[m.Index] = (name ?? ("meter " + m.Index), unit ?? "");
            }

            // Bind before announcing, so there is a listener the instant the
            // radio starts sending.
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.ReceiveTimeout = 500;
            int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

            // volt_denom changes at firmware 1.11. Nothing in this tool reads a
            // voltage meter for a fact, but getting it wrong silently would make
            // the supply readings in a census wrong by a factor of four, and a
            // census exists to be believed.
            double voltDenominator = ParsesBelow_1_11(wire.Version) ? 1024.0 : 256.0;

            var stream = new MeterStream(udp, descriptors, voltDenominator);
            try
            {
                WireReply reply = wire.Send("client udpport " + port.ToString(CultureInfo.InvariantCulture));
                stream.RegistrationReply = $"asked for UDP port {port}; the radio replied {reply}";
                if (!reply.Ok)
                {
                    throw new InvalidOperationException(
                        $"The radio refused to route meter data to us: {reply}. " +
                        "Without it, every meter reading below would be a number nobody measured.");
                }
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            stream._reader.Start();

            // Open a return path through this computer's own firewall.
            //
            // Windows blocks unsolicited inbound UDP to a program that has no
            // rule, and the application HAS one — a per-worktree "JJ Flexible
            // Radio Access" allow entry — while a console tool run out of a bin
            // folder does not. Rather than ask an operator to make a firewall
            // change for a diagnostic, send one datagram outward first: a
            // stateful firewall then permits the reply from that peer, which is
            // the same mechanism every UDP client relies on.
            //
            // The payload is a single zero byte to a port whose listener will
            // discard anything it cannot parse. Nothing about the radio's state
            // changes, and if the radio's stream arrives from a port we did not
            // guess, the result is the honest "nothing arrived" this tool
            // already reports rather than a wrong number.
            foreach (int radioPort in new[] { 4991, 4992 })
            {
                try { udp.Send(new byte[] { 0 }, 1, new IPEndPoint(IPAddress.Parse(wire.Host), radioPort)); }
                catch (SocketException) { }
                catch (FormatException) { }
            }

            return stream;
        }

        /// <summary>
        /// Blocks until every named meter has produced a reading, or the
        /// deadline passes. Returns the names still silent, which is the
        /// answer worth having: a meter that never spoke is a fact that must
        /// come back unreadable rather than as a plausible zero.
        /// </summary>
        public IReadOnlyList<string> WaitForReadings(IEnumerable<string> names, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            var wanted = new List<string>(names);

            while (DateTime.UtcNow < deadline)
            {
                var missing = new List<string>();
                foreach (string name in wanted)
                {
                    if (Latest(name) is null) missing.Add(name);
                }
                if (missing.Count == 0) return Array.Empty<string>();
                Thread.Sleep(50);
            }

            var stillMissing = new List<string>();
            foreach (string name in wanted)
            {
                if (Latest(name) is null) stillMissing.Add(name);
            }
            return stillMissing;
        }

        /// <summary>The most recent reading for a meter by name, or null when it
        /// has never reported. Null is the honest answer and the caller must
        /// carry it through rather than substituting a number.</summary>
        public MeterSample? Latest(string name)
        {
            lock (_gate)
            {
                foreach (MeterSample s in _latest.Values)
                {
                    if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
                }
            }
            return null;
        }

        /// <summary>Every meter that has reported, newest value each.</summary>
        public IReadOnlyList<MeterSample> All()
        {
            lock (_gate) return new List<MeterSample>(_latest.Values);
        }

        private void ReadLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stopping.IsCancellationRequested)
            {
                byte[] data;
                try
                {
                    data = _udp.Receive(ref any);
                }
                catch (SocketException)
                {
                    continue; // receive timeout, or the socket closing under us
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _datagrams);
                try { Consume(data); }
                catch (Exception) { /* a malformed packet is not worth a crash in a diagnostic */ }
            }
        }

        private void Consume(byte[] data)
        {
            if (data.Length < 4) return;

            uint word = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0));
            int packetType = (int)(word >> 28);
            bool hasClassId = (word & 0x08000000) != 0;
            bool hasTrailer = (word & 0x04000000) != 0;
            int tsi = (int)((word >> 22) & 0x03);
            int tsf = (int)((word >> 20) & 0x03);
            int packetWords = (int)(word & 0xFFFF);

            int index = 4;
            int payloadBytes = (packetWords - 1) * 4;

            // 1 and 3 are the with-stream variants of IF data and extension
            // data; both carry a stream id ahead of the class id.
            if (packetType is 1 or 3)
            {
                if (data.Length < index + 4) return;
                index += 4;
                payloadBytes -= 4;
            }

            if (!hasClassId) return; // a meter packet always carries one
            if (data.Length < index + 8) return;

            uint classWord = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index + 4));
            ushort packetClassCode = (ushort)classWord;
            index += 8;
            payloadBytes -= 8;

            if (packetClassCode != MeterClassCode) return;

            if (tsi != 0) { index += 4; payloadBytes -= 4; }
            if (tsf != 0) { index += 8; payloadBytes -= 8; }
            if (hasTrailer) payloadBytes -= 4;

            if (payloadBytes <= 0 || payloadBytes % 4 != 0) return;
            if (data.Length < index + payloadBytes) return;

            Interlocked.Increment(ref _packets);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            int pairs = payloadBytes / 4;
            for (int i = 0; i < pairs; i++)
            {
                int id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
                short raw = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(index + 2));
                index += 4;

                string name = "meter " + id.ToString(CultureInfo.InvariantCulture);
                string units = "";
                if (_descriptors.TryGetValue(id, out (string Name, string Units) d))
                {
                    name = d.Name;
                    units = d.Units;
                }

                double value = Scale(raw, units);

                lock (_gate)
                {
                    long count = _latest.TryGetValue(id, out MeterSample? previous) ? previous.Count + 1 : 1;
                    _latest[id] = new MeterSample(id, name, units, value, now, count);
                }
            }
        }

        /// <summary>
        /// Raw integer to reading, by the meter's own declared units. This is
        /// the arithmetic FlexLib performs in <c>Meter.UpdateValue</c>, written
        /// out again here on purpose — see the class note about why sharing it
        /// would make agreement meaningless.
        /// </summary>
        private double Scale(short raw, string units)
        {
            switch (units.ToLowerInvariant())
            {
                case "db":
                case "dbm":
                case "dbfs":
                case "swr":
                    return raw / 128.0;
                case "volts":
                case "amps":
                    return raw / _voltDenominator;
                case "degf":
                case "degc":
                    return raw / 64.0;
                default:
                    return raw;
            }
        }

        private static bool ParsesBelow_1_11(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            string[] parts = version.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minor)) return false;
            return major < 1 || (major == 1 && minor < 11);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopping.Cancel();
            // Disposing a socket mid-receive throws on the reader thread by
            // design; there is nothing to handle and nothing to report.
            try { _udp.Dispose(); } catch (SocketException) { } catch (ObjectDisposedException) { }
            _stopping.Dispose();
        }
    }
}
