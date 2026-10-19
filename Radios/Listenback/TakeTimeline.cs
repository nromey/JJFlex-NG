using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Radios.Listenback;

public readonly record struct TakeSpan(long Start, long End)
{
    public long Length => End - Start;
}

public static class TakeTimeline
{
    public static IReadOnlyList<TakeSpan> KeyedSpans(TakeInspection take, long count)
    {
        var result = new List<TakeSpan>();
        long? start = null;
        foreach (var line in take.Fields.Where(p => p.Key == "keying"))
        {
            using var json = JsonDocument.Parse(line.Value);
            var root = json.RootElement;
            if (root.GetProperty("Source").GetString() != "radio-TransmitChange") continue;
            long at = Math.Clamp(root.TryGetProperty("ArrivalClockPair", out var clock)
                ? clock.GetInt64() : root.GetProperty("AtPair").GetInt64(), 0, count);
            if (root.GetProperty("Keyed").GetBoolean()) start ??= at;
            else if (start is { } from) { if (at > from) result.Add(new(from, at)); start = null; }
        }
        if (start is { } unfinished && unfinished < count) result.Add(new(unfinished, count));
        return result;
    }
    public static IReadOnlyList<TakeSpan> Gaps(TakeInspection take)
    {
        var result = new List<TakeSpan>();
        foreach (var field in take.Fields.Where(f => f.Key == "gap"))
        {
            using var json = JsonDocument.Parse(field.Value);
            var root = json.RootElement;
            long fill = root.GetProperty("FillPairs").GetInt64();
            if (fill > 0) { long at = root.GetProperty("AtPair").GetInt64(); result.Add(new(at, at + fill)); }
        }
        return result;
    }
    public static IReadOnlyList<TakeSpan> WithoutGaps(IEnumerable<TakeSpan> spans, IEnumerable<TakeSpan> gaps)
    {
        var result = spans.ToList();
        foreach (var gap in gaps)
        {
            var next = new List<TakeSpan>();
            foreach (var span in result)
            {
                if (gap.End <= span.Start || gap.Start >= span.End) next.Add(span);
                else
                {
                    if (gap.Start > span.Start) next.Add(new(span.Start, gap.Start));
                    if (gap.End < span.End) next.Add(new(gap.End, span.End));
                }
            }
            result = next;
        }
        return result;
    }
}
