using System;
using System.Diagnostics;
using Flex.Smoothlake.Vita;

namespace Radios.Listenback;

/// <summary>Values copied before FlexLib reuses both its packet and payload.</summary>
public readonly record struct IqPacketHeader(uint StreamId, byte Count, ushort ClassCode,
    byte IntegerKind, byte FractionKind, uint Seconds, ulong Fraction, long ArrivalTicks)
{
    public static IqPacketHeader Copy(VitaIFDataPacket p) => new(p.StreamId,
        p.Header.packet_count, p.ClassId.PacketClassCode, (byte)p.Header.tsi,
        (byte)p.Header.tsf, p.TimestampInt, p.TimestampFrac, Stopwatch.GetTimestamp());

    public int Rate => ClassCode switch { 0x02e3 => 24000, 0x02e4 => 48000,
        0x02e5 => 96000, 0x02e6 => 192000, _ => 0 };
    public bool HasRadioClock => IntegerKind != 0 && FractionKind == 2 && Fraction < 1_000_000_000_000UL;

    // Subtract the integer seconds BEFORE converting: double Unix time loses
    // sub-sample precision, especially at 192 kHz.
    public double SecondsSince(IqPacketHeader prior) => (long)Seconds - prior.Seconds
        + ((double)Fraction - prior.Fraction) / 1e12;
}

public sealed record IqBlock(IqPacketHeader Header, ReadOnlyMemory<float> Samples,
    long DroppedPackets, long DroppedPairs);

public readonly record struct IqGap(long FillPairs, bool Discard, bool Exact, string Basis);

/// <summary>One ledger per stream instance, never keyed by a reusable stream id.</summary>
public sealed class IqGapLedger
{
    private IqPacketHeader? _last;
    private int _lastPairs;
    private long _lastDropped;
    public IqGap Observe(IqPacketHeader now, int pairs, long droppedPairs = 0)
    {
        if (pairs <= 0 || now.Rate == 0) throw new ArgumentOutOfRangeException(nameof(pairs));
        IqGap result = new(0, false, true, "first");
        if (_last is { } last)
        {
            if (last.Rate != now.Rate) return new(0, true, false, "rate-changed");
            if (now.HasRadioClock && last.HasRadioClock && now.IntegerKind == last.IntegerKind)
            {
                double elapsed = now.SecondsSince(last) * now.Rate;
                if (elapsed < _lastPairs - 0.75) return new(0, true, true, "duplicate-or-reordered");
                long fill = Math.Max(0, (long)Math.Round(elapsed) - _lastPairs);
                // A broken clock must not create hours of apparently valid silence.
                if (fill > now.Rate * 60L) return new(0, true, false, "timestamp-discontinuity");
                result = new(fill, false, Math.Abs(elapsed - Math.Round(elapsed)) < 0.25, "radio-timestamp");
            }
            else
            {
                int skipped = (now.Count - last.Count - 1 + 16) & 15;
                long ours = Math.Max(0, droppedPairs - _lastDropped);
                double wallPairs = (now.ArrivalTicks - last.ArrivalTicks) * (double)now.Rate / Stopwatch.Frequency;
                long estimate = Math.Max(ours, (long)Math.Round(wallPairs) - _lastPairs);
                // Without timestamps the counter aliases loss and reordering. Never
                // call its answer exact, including an apparently contiguous packet.
                long fill = Math.Max(ours, skipped * (long)_lastPairs);
                if (estimate >= 16L * _lastPairs) fill = Math.Max(fill, estimate);
                if (fill > now.Rate * 60L) return new(0, true, false, "wall-clock-discontinuity");
                result = new(fill, false, false, "counter-and-arrival-estimate");
            }
        }
        _last = now;
        _lastPairs = pairs;
        _lastDropped = droppedPairs;
        return result;
    }
}
