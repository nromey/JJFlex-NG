#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using Flex.Smoothlake.Vita;

namespace Flex.Smoothlake.FlexLib;

// JJFlex patch: qualified-UDP observation for the SmartLink connect proof and
// its hardware gate. Reapply with MIGRATION.md item 30. Purely additive: it
// changes no FlexLib state, and in particular never touches
// _udpSuccessfulRegistration, which still flips on any Flex-OUI packet from
// any sender, exactly as stock code does.
//
// JJFlex patch (MIGRATION.md item 33): a qualifying sighting is raised once per
// socket AND command connection, not once per socket. On a hole punch the
// socket exists before the command connection that will own it, so the first
// qualifying packet can arrive under the previous connection (or none); with
// one sighting per socket, a consumer that rightly refuses that one could never
// be shown a later packet that arrived under the right connection.

/// <summary>
/// One sighting of Flex VITA traffic on one VITA socket. Raised (and traced)
/// for the expected radio address at most once per socket for each command
/// connection it arrives under (item 33), and traced at most once per socket
/// for a Flex VITA packet from any other address.
/// <para>Only <see cref="Matched"/> observations qualify. <see cref="Radio.QualifiedUdpObserved"/>
/// raises matched ones only; unmatched ones reach the trace and nothing else.</para>
/// </summary>
public sealed class QualifiedUdpObservation
{
    internal QualifiedUdpObservation(int socketGeneration, IPEndPoint source, IPAddress? expected,
        bool matched, TimeSpan sinceSocketCreated, TimeSpan? sinceOpenStarted, bool openCompleted,
        CommandConnection? connection)
    {
        SocketGeneration = socketGeneration;
        Source = source;
        ExpectedAddress = expected;
        Matched = matched;
        SinceSocketCreated = sinceSocketCreated;
        SinceOpenStarted = sinceOpenStarted;
        OpenCompleted = openCompleted;
        Connection = connection;
    }

    /// <summary>Which VITA socket of this Radio saw it; compare with <see cref="Radio.VitaSocketGeneration"/>.</summary>
    public int SocketGeneration { get; }
    /// <summary>The datagram's source, port included (the port may differ from the one we send to).</summary>
    public IPEndPoint Source { get; }
    /// <summary>The radio address the socket was created for, or null if none was known.</summary>
    public IPAddress? ExpectedAddress { get; }
    /// <summary>True when the source address is the expected radio address. Only these qualify.</summary>
    public bool Matched { get; }
    /// <summary>Time from the socket's creation to this packet.</summary>
    public TimeSpan SinceSocketCreated { get; }
    /// <summary>Time from the start of <see cref="Radio.Connect"/> (the open) to this packet; null if no open had started.</summary>
    public TimeSpan? SinceOpenStarted { get; }
    /// <summary>Whether FlexLib had finished initializing the command connection (<see cref="Radio.Connected"/>) when it arrived.</summary>
    public bool OpenCompleted { get; }
    /// <summary>The command connection current when it arrived; null before one exists.</summary>
    public CommandConnection? Connection { get; }

    /// <summary>The line the bench reads. Plain words, one line.</summary>
    public string TraceLine
    {
        get
        {
            string ms(TimeSpan t) => ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            return "QualifiedUdp: socket#" + SocketGeneration.ToString(CultureInfo.InvariantCulture)
                + " matched=" + (Matched ? "true" : "false")
                + " source=" + Source
                + " expected=" + (ExpectedAddress?.ToString() ?? "none")
                + " sinceSocketMs=" + ms(SinceSocketCreated)
                + " sinceOpenMs=" + (SinceOpenStarted is TimeSpan o ? ms(o) : "none")
                + " openCompleted=" + (OpenCompleted ? "true" : "false")
                + " connection=" + (Connection?.Id.ToString("N").Substring(0, 8) ?? "none");
        }
    }
}

/// <summary>
/// The per-socket watch. One is created with each VITA socket, before the socket
/// can receive anything, so the watch starts at socket creation rather than
/// after the open. It decides only whether a packet is worth one observation.
/// </summary>
public sealed class QualifiedUdpWatch
{
    private int _matchedSeen;
    private int _unmatchedSeen;
    // JJFlex patch (item 33): the command connection the last qualifying
    // sighting was reported under. NoConnection stands for "none existed".
    private static readonly object NoConnection = new();
    private object? _matchedUnder;

    public QualifiedUdpWatch(int generation, IPAddress? expectedAddress, long createdTimestamp)
    {
        Generation = generation;
        ExpectedAddress = expectedAddress;
        CreatedTimestamp = createdTimestamp;
    }

    public int Generation { get; }
    public IPAddress? ExpectedAddress { get; }
    /// <summary><see cref="Stopwatch.GetTimestamp"/> at socket creation.</summary>
    public long CreatedTimestamp { get; }
    /// <summary>True once a qualifying packet has been seen on this socket.</summary>
    public bool HasMatched => Volatile.Read(ref _matchedSeen) != 0;

    /// <summary>
    /// Returns an observation for the first Flex VITA packet from the expected
    /// address under each command connection (item 33: once per socket and
    /// connection), and for the first from any other address; null for
    /// everything else, so the receive thread pays one reference comparison
    /// after a match while the connection stays the same.
    /// <paramref name="openStartedTimestamp"/> is 0 when no open has started.
    /// </summary>
    public QualifiedUdpObservation? Observe(IPEndPoint? source, byte[]? data, int bytes,
        long nowTimestamp, long openStartedTimestamp, bool openCompleted, CommandConnection? connection)
    {
        object under = (object?)connection ?? NoConnection;
        if (Volatile.Read(ref _matchedSeen) != 0 && ReferenceEquals(Volatile.Read(ref _matchedUnder), under)) return null;
        if (source == null || data == null || bytes < 16 || data.Length < 16) return null;
        if (!IsFlexVita(data)) return null;

        bool matched = ExpectedAddress != null && SameAddress(source.Address, ExpectedAddress);
        if (matched)
        {
            // One sighting per connection: the same connection again is not news.
            object? prior = Interlocked.Exchange(ref _matchedUnder, under);
            bool first = Interlocked.Exchange(ref _matchedSeen, 1) == 0;
            if (!first && ReferenceEquals(prior, under)) return null;
        }
        else
        {
            if (Interlocked.Exchange(ref _unmatchedSeen, 1) != 0) return null;
        }

        TimeSpan sinceSocket = Stopwatch.GetElapsedTime(CreatedTimestamp, nowTimestamp);
        TimeSpan? sinceOpen = openStartedTimestamp != 0
            ? Stopwatch.GetElapsedTime(openStartedTimestamp, nowTimestamp)
            : null;
        return new QualifiedUdpObservation(Generation, source, ExpectedAddress, matched,
            sinceSocket, sinceOpen, openCompleted, connection);
    }

    /// <summary>The same test the receive pipeline applies before it sets its registration flag.</summary>
    public static bool IsFlexVita(byte[] data)
    {
        try
        {
            var preamble = new VitaPacketPreamble(data);
            return preamble.class_id.OUI == VitaFlex.FLEX_OUI;
        }
        catch (Exception)
        {
            return false; // a short or malformed datagram is simply not a sighting
        }
    }

    public static bool SameAddress(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return a.Equals(b);
    }
}
