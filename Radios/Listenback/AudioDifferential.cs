using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Listenback;

public sealed record AudioShape(double RmsDb, double PeakDb, double CrestDb, double LowDb, double MidDb,
    double HighDb, double StopbandDb)
{
    public double TiltDb => HighDb - LowDb;
}
public sealed record AudioDifference(bool Comparable, string Reason, double LevelDb = 0,
    double TiltDb = 0, double CrestDb = 0, double StopbandDb = 0);

/// <summary>Sample-derived audio comparison; independent of the meter differential.</summary>
public static class AudioDifferential
{
    public static AudioShape Measure(ReadOnlySpan<float> samples, int rate, double filterLow, double filterHigh)
    {
        if (samples.Length == 0) throw new ArgumentException("empty-span");
        double square = 0, peak = 0;
        foreach (float value in samples) { square += value * (double)value; peak = Math.Max(peak, Math.Abs(value)); }
        double rms = Db(square / samples.Length), peakDb = 20 * Math.Log10(Math.Max(1e-10, peak));
        int n = Math.Min(4096, samples.Length);
        var x = new double[n]; double norm = 0;
        for (int i = 0; i < n; ++i)
        {
            double w = .5 - .5 * Math.Cos(2 * Math.PI * i / Math.Max(1, n - 1));
            x[i] = samples[i] * w; norm += w * w;
        }
        double low = 0, mid = 0, high = 0, stop = 0;
        for (int bin = 1; bin < n / 2; ++bin)
        {
            double hz = bin * (double)rate / n;
            double coefficient = 2 * Math.Cos(2 * Math.PI * bin / n), p = 0, pp = 0;
            foreach (double v in x) { double current = v + coefficient * p - pp; pp = p; p = current; }
            double power = 2 * Math.Max(0, p * p + pp * pp - coefficient * p * pp) / (n * norm);
            if (hz >= 100 && hz < 300) low += power;
            if (hz >= 300 && hz < 1000) mid += power;
            if (hz >= 1000 && hz < 3000) high += power;
            if (hz < Math.Max(0, filterLow - 100) || hz > filterHigh + 300) stop += power;
        }
        return new(rms, peakDb, peakDb - rms, Db(low), Db(mid), Db(high), Db(stop));
    }
    private static double Db(double power) => 10 * Math.Log10(Math.Max(1e-20, power));

    public static AudioDifference Compare(float[] reference, float[] received, int rate, AudioAlignmentResult alignment,
        double filterLow, double filterHigh)
    {
        if (!alignment.Aligned) return new(false, alignment.Reason);
        var values = new List<AudioDifference>();
        foreach (var measured in alignment.Spans)
        {
            int start = (int)Math.Max(measured.Span.Start, measured.Span.Start - measured.LagSamples);
            int end = (int)Math.Min(measured.Span.End, measured.Span.End - measured.LagSamples);
            if (end <= start) continue;
            var a = Measure(reference.AsSpan(start, end - start), rate, filterLow, filterHigh);
            var b = Measure(received.AsSpan(start + measured.LagSamples, end - start), rate, filterLow, filterHigh);
            values.Add(new(true, "aligned-span", b.RmsDb - a.RmsDb, b.TiltDb - a.TiltDb, b.CrestDb - a.CrestDb, b.StopbandDb - a.StopbandDb));
        }
        return values.Count == 0 ? new(false, "no-keyed-gap-free-span") : new(true, "aligned-spans",
            values.Average(v => v.LevelDb), values.Average(v => v.TiltDb), values.Average(v => v.CrestDb), values.Average(v => v.StopbandDb));
    }
    public static string Compatibility(TakeInspection a, TakeInspection b)
    {
        foreach (string key in new[] { "serial", "mode", "rate-echoed", "receive-antenna", "rf-gain", "tx-antenna", "rf-power",
            "tx-frequency-mhz", "pan-centre-mhz", "tx-filter-low", "tx-filter-high", "dummy-load", "render-scale" })
            if (a.Get(key) == null || b.Get(key) == null || a.Get(key) != b.Get(key)) return key;
        return "";
    }
}
