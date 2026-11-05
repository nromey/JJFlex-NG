using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Radios.Listenback;

/// <summary>What <see cref="AudioPacketContinuity{T}"/> decided about one offered packet.</summary>
public enum PacketOutcome
{
    /// <summary>Taken: placed now, or held one packet while its gap is decided.</summary>
    Taken,
    /// <summary>Not newer than a packet already taken: a repeat or a late packet. Dropped.</summary>
    Late,
    /// <summary>The radio's clock moved too far to fill, forward or back. The take ends.</summary>
    Discontinuity,
}

/// <summary>A packet ready to write, and how many packets of silence go in front of it.</summary>
public readonly record struct PlacedPacket<T>(T Item, long MissingBefore, string Basis);

/// <summary>What the per-second count found when the take closed.</summary>
public readonly record struct ContinuityWitness(long PeakPerSecond, int CompleteSeconds, int UnaccountedShortSeconds);

/// <summary>
/// Where the gaps are in one stream of received Opus packets, worked out
/// WITHOUT reading the fractional half of the radio's timestamp as time
/// (Unified takes, round three).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the timestamp.</b> FlexLib's key is <c>TimestampInt +
/// TimestampFrac / 2^16</c>. The integer half is UTC seconds; the fractional
/// half advances only about 0.147 over a second of audio and jumps the rest at
/// each second (measured, <see cref="ReceiveContinuityMeter"/>). The capture
/// once read the key as seconds, and on the 2026-11-04 bench kept about one
/// packet in four and padded every second with 0.849 s of silence.
/// </para>
/// <para>
/// <b>What each part of a packet is used for.</b>
/// </para>
/// <list type="bullet">
/// <item><description><b>The key ORDERS packets.</b> It only ever goes up, even
/// though it is no good as a clock, so a packet not newer than the last one
/// taken is a repeat or a late packet and is dropped. The live receive loop
/// already takes only newer packets and FlexLib refuses a duplicate key, so on
/// the live path this branch is reached only by a radio clock that stepped
/// back by less than a second.</description></item>
/// <item><description><b>The 4-bit counter COUNTS them.</b> Exact for a loss of
/// 1 to 15 packets, and blind to a loss of exactly 16 or any multiple, because
/// it wraps every 16 packets.</description></item>
/// <item><description><b>Whole UTC seconds bound the count.</b> A key that
/// skipped whole seconds lost at least that many seconds of packets, and the
/// counter's residue then says how many more.</description></item>
/// <item><description><b>Arrival time chooses between the counter's readings</b>
/// when it is far longer than the counter alone allows: the loss of 16 the
/// counter cannot see.</description></item>
/// </list>
/// <para>
/// <b>The trap in arrival time, and the one-packet hold that answers it.</b>
/// "Arrival" is when the receive loop took the packet, not when it came off the
/// network. A loop that stalls and then takes its backlog in a burst gives the
/// first packet after the stall a long arrival gap and LOSES NOTHING, which is
/// exactly what a loss of 16 looks like at that packet. The next packet tells
/// them apart: a backlog drains in microseconds, while audio resuming after a
/// real loss arrives at the radio's own pace. So a packet whose arrival gap
/// alone suggests more than the counter says is held until the next one
/// arrives. A burst behind it means a stall, and the gap is the counter's; a
/// packet at the radio's pace means a loss, and the gap is the arrival
/// estimate snapped to the counter's residue. A held packet with nothing
/// after it is given the counter's reading.
/// </para>
/// <para>
/// <b>The witness.</b> A stall can also hide a loss, when FlexLib clears its
/// receive queue past 30 packets and the counter happens to come out
/// contiguous. That cannot be placed, but it can be counted afterwards, the
/// way <see cref="ReceiveContinuityMeter"/> counts: every complete UTC second
/// should hold as many packets as the busiest one. A complete second that came
/// up short with no gap placed in or against it is an unaccounted loss, and
/// the take is not clean.
/// </para>
/// <para>
/// <b>Every gap is an estimate, and says what it was based on:</b>
/// <c>counter</c>, <c>counter-and-utc-second</c>,
/// <c>counter-and-arrival-estimate</c>, or <c>counter-arrival-unresolved</c>
/// for a held packet the take closed on.
/// </para>
/// </remarks>
public sealed class AudioPacketContinuity<T>
{
    /// <summary>What a take's header says its gaps were measured with.</summary>
    public const string GapClock = "counter-and-arrival-estimate";
    /// <summary>A step back in radio time longer than this ends the take rather than dropping everything after it.</summary>
    public const double LongestStepBackSeconds = 1;

    private readonly double _ticksPerPacket;
    private readonly int _packetsPerSecond;
    private readonly double _longestFillSeconds;
    private Mark? _last;
    private Held? _held;
    private readonly Dictionary<long, int> _perSecond = new();
    private readonly HashSet<long> _gapTouched = new();
    private long _firstSecond = long.MinValue, _lastSecond = long.MinValue;

    private readonly record struct Mark(double Key, int Sequence, long Arrival);
    private sealed record Held(T Item, Mark At, long IfStall, string StallBasis, long IfLoss);

    /// <param name="tickFrequency">Ticks per second of the arrival times (<see cref="Stopwatch.Frequency"/>).</param>
    /// <param name="packetSeconds">Audio in one packet: ten milliseconds of Opus.</param>
    /// <param name="longestFillSeconds">Whole seconds missing at or beyond this end the take.</param>
    public AudioPacketContinuity(long tickFrequency, double packetSeconds = 0.01, double longestFillSeconds = 5)
    {
        if (tickFrequency <= 0 || packetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(tickFrequency));
        _ticksPerPacket = tickFrequency * packetSeconds;
        _packetsPerSecond = (int)Math.Round(1 / packetSeconds);
        _longestFillSeconds = longestFillSeconds;
    }

    /// <summary>Packets dropped as repeats or late.</summary>
    public long LatePackets { get; private set; }
    /// <summary>Held packets later read as a stall (no gap beyond the counter's).</summary>
    public long StallsSeen { get; private set; }
    /// <summary>The reason the last <see cref="PacketOutcome.Discontinuity"/> gave.</summary>
    public string DiscontinuityReason { get; private set; } = "";

    /// <summary>
    /// Offer the next packet the receive loop took, in the order it took them.
    /// Anything now ready to write, the held packet included, is appended to
    /// <paramref name="ready"/> in order.
    /// </summary>
    public PacketOutcome Offer(T item, double key, int sequence, long arrival, List<PlacedPacket<T>> ready)
    {
        Mark now = new(key, sequence & 15, arrival);
        double? newest = _held?.At.Key ?? _last?.Key;
        if (newest is { } n && key <= n)
        {
            if (n - key > LongestStepBackSeconds) return Discontinuity("clock-stepped-back");
            LatePackets++;
            return PacketOutcome.Late;
        }
        if (_held is { } held)
        {
            // A backlog drains in microseconds; audio after a real loss comes at the radio's pace.
            bool stall = now.Arrival - held.At.Arrival < _ticksPerPacket / 4;
            if (stall) StallsSeen++;
            Place(held.Item, held.At, stall ? held.IfStall : held.IfLoss,
                stall ? held.StallBasis : "counter-and-arrival-estimate", ready);
            _held = null;
        }
        if (_last is not { } last)
        {
            Place(item, now, 0, "first", ready);
            return PacketOutcome.Taken;
        }

        long seconds = (long)Math.Floor(key) - (long)Math.Floor(last.Key);
        if (seconds - 1 >= _longestFillSeconds) return Discontinuity("clock-discontinuity");
        int residue = (now.Sequence - last.Sequence - 1) & 15;
        long least = seconds >= 2 ? (seconds - 1) * _packetsPerSecond : 0;
        long most = Math.Max(least, (seconds + 1) * _packetsPerSecond - 1);
        long byCounter = AtLeast(residue, least);
        string counterBasis = least > 0 ? "counter-and-utc-second" : "counter";
        double byArrival = (now.Arrival - last.Arrival) / _ticksPerPacket - 1;
        // Within half a wrap of what the counter says, the counter's reading stands.
        if (byArrival < byCounter + 8)
        {
            Place(item, now, byCounter, counterBasis, ready);
            return PacketOutcome.Taken;
        }
        long byLoss = Nearest(residue, byArrival, byCounter, most);
        if (byLoss <= byCounter)
        {
            Place(item, now, byCounter, counterBasis, ready);
            return PacketOutcome.Taken;
        }
        _held = new Held(item, now, byCounter, counterBasis, byLoss);
        return PacketOutcome.Taken;
    }

    /// <summary>The take is closing: release a held packet with the counter's reading.</summary>
    public void Flush(List<PlacedPacket<T>> ready)
    {
        if (_held is not { } held) return;
        Place(held.Item, held.At, held.IfStall,
            held.IfStall > 0 ? held.StallBasis + "-arrival-unresolved" : "counter-arrival-unresolved", ready);
        _held = null;
    }

    /// <summary>
    /// Count packets per complete UTC second: a short second with no gap placed
    /// in or against it is a loss nothing could place. Judged only once at
    /// least two complete seconds exist, since the busiest one is the measure.
    /// </summary>
    public ContinuityWitness Witness()
    {
        long peak = 0;
        int complete = 0;
        var judged = new List<(long Second, int Count)>();
        if (_firstSecond != long.MinValue)
            for (long s = _firstSecond + 1; s < _lastSecond; s++)
            {
                int count = _perSecond.TryGetValue(s, out int c) ? c : 0;
                judged.Add((s, count));
                complete++;
                if (!_gapTouched.Contains(s)) peak = Math.Max(peak, count);
            }
        if (complete < 2 || peak == 0) return new ContinuityWitness(peak, complete, 0);
        int shortSeconds = 0;
        foreach (var (second, count) in judged)
            if (count < peak && !_gapTouched.Contains(second)) shortSeconds++;
        return new ContinuityWitness(peak, complete, shortSeconds);
    }

    private PacketOutcome Discontinuity(string reason)
    {
        DiscontinuityReason = reason;
        return PacketOutcome.Discontinuity;
    }

    private void Place(T item, Mark at, long missing, string basis, List<PlacedPacket<T>> ready)
    {
        long second = (long)Math.Floor(at.Key);
        if (missing > 0 && _last is { } last)
            for (long s = (long)Math.Floor(last.Key); s <= second; s++) _gapTouched.Add(s);
        _perSecond[second] = (_perSecond.TryGetValue(second, out int count) ? count : 0) + 1;
        if (_firstSecond == long.MinValue) _firstSecond = second;
        _lastSecond = second;
        _last = at;
        ready.Add(new PlacedPacket<T>(item, missing, missing > 0 ? basis : "contiguous"));
    }

    /// <summary>The smallest count of the counter's residue that is at least <paramref name="floor"/>.</summary>
    private static long AtLeast(int residue, long floor) =>
        residue >= floor ? residue : residue + 16 * (long)Math.Ceiling((floor - residue) / 16.0);

    /// <summary>The count of the counter's residue nearest <paramref name="estimate"/>, within the bounds; a tie goes low.</summary>
    private static long Nearest(int residue, double estimate, long floor, long ceiling)
    {
        long best = AtLeast(residue, floor);
        for (long candidate = best + 16; candidate <= ceiling; candidate += 16)
        {
            if (Math.Abs(candidate - estimate) < Math.Abs(best - estimate)) best = candidate;
            else if (candidate > estimate) break;
        }
        return best;
    }
}
