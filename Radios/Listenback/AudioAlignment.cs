using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Listenback;

public sealed record SpanAlignment(TakeSpan Span, int LagSamples, double Correlation, double PeakToSidelobe, bool Confident, TakeSpan CorrelationWindow = default);
public sealed record AudioAlignmentResult(bool Aligned, string Reason, IReadOnlyList<SpanAlignment> Spans)
{
    public double LagMilliseconds(int rate) => Spans.Count == 0 ? 0 : Spans[0].LagSamples * 1000.0 / rate;
}

/// <summary>Content alignment. Positive lag means received audio follows the sent reference.</summary>
public static class AudioAlignment
{
    public static AudioAlignmentResult Measure(float[] sent, float[] heard, int rate,
        IReadOnlyList<TakeSpan> gapFreeKeyedSpans, double maximumLagSeconds = 1.5)
    {
        var results = new List<SpanAlignment>();
        int count = Math.Min(sent.Length, heard.Length);
        int stride = Math.Max(1, rate / 4000);
        var coarseSent = Decimate(sent, stride);
        var coarseHeard = Decimate(heard, stride);
        int maxLag = (int)Math.Round(rate * maximumLagSeconds);
        foreach (var candidate in gapFreeKeyedSpans)
        {
            var span = new TakeSpan(Math.Clamp(candidate.Start, 0, count), Math.Clamp(candidate.End, 0, count));
            if (span.Length < rate / 4) { results.Add(new(span, 0, 0, 0, false)); continue; }
            int limit = Math.Min(maxLag, (int)span.Length - rate / 5);
            var peaks = new List<(int Lag, double Value)>();
            int bestLag = 0; double best = 0;
            var coarseSpan = new TakeSpan((span.Start + stride - 1) / stride, span.End / stride);
            for (int coarseLag = -limit / stride; coarseLag <= limit / stride; ++coarseLag)
            {
                int lag = coarseLag * stride;
                double value = Math.Abs(Correlation(coarseSent, coarseHeard, coarseSpan, coarseLag, 1, rate / stride));
                peaks.Add((lag, value));
                if (value > best) { best = value; bestLag = lag; }
            }
            // Refine the sample lag on the full-rate waveforms.
            int coarse = bestLag; best = 0;
            for (int lag = Math.Max(-limit, coarse - stride); lag <= Math.Min(limit, coarse + stride); ++lag)
            {
                double value = Math.Abs(Correlation(sent, heard, span, lag, 1, rate));
                if (value > best) { best = value; bestLag = lag; }
            }
            double sidelobe = peaks.Where(p => Math.Abs(p.Lag - bestLag) > rate * .003)
                .Select(p => p.Value).DefaultIfEmpty(best).Max();
            double ratio = best / Math.Max(1e-9, sidelobe);
            // A steady sine's peaks repeat every cycle: ratio stays near one.
            bool confident = best >= .5 && ratio >= 1.2 && Math.Abs(bestLag) < limit - stride;
            long windowStart = Math.Max(span.Start, span.Start - bestLag);
            long windowEnd = Math.Min(Math.Min(span.End, span.End - bestLag), windowStart + rate * 2L);
            results.Add(new(span, bestLag, best, ratio, confident, new(windowStart, windowEnd)));
        }
        if (results.Count == 0) return new(false, "no-keyed-gap-free-span", results);
        if (results.Any(r => !r.Confident)) return new(false, "weak-or-periodic-correlation", results);
        if (results.Max(r => r.LagSamples) - results.Min(r => r.LagSamples) > rate * .003)
            return new(false, "alignment-drifted", results);
        return new(true, "content-correlation", results);
    }
    private static float[] Decimate(float[] input, int stride)
    {
        if (stride == 1) return input;
        var result = new float[input.Length / stride];
        for (int i = 0; i < result.Length; ++i)
        {
            double sum = 0;
            for (int j = 0; j < stride; ++j) sum += input[i * stride + j];
            result[i] = (float)(sum / stride);
        }
        return result;
    }
    private static double Correlation(float[] a, float[] b, TakeSpan span, int lag, int stride, int rate)
    {
        int start = (int)Math.Max(span.Start, span.Start - lag);
        int end = (int)Math.Min(span.End, span.End - lag);
        // Bound computation without hiding the chosen window: all samples are
        // inside the named span, at its start, for at most two seconds.
        end = Math.Min(end, start + rate * 2);
        if (end - start < rate / 5) return 0;
        double sa = 0, sb = 0, aa = 0, bb = 0, ab = 0; int n = 0;
        for (int i = start; i < end; i += stride)
        {
            double x = a[i], y = b[i + lag];
            sa += x; sb += y; aa += x * x; bb += y * y; ab += x * y; n++;
        }
        aa -= sa * sa / n; bb -= sb * sb / n; ab -= sa * sb / n;
        return aa <= 1e-12 || bb <= 1e-12 ? 0 : ab / Math.Sqrt(aa * bb);
    }
}
