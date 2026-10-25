using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Listenback;

public sealed record AudioShape(double RmsDb, double PeakDb, double CrestDb, double LowDb, double MidDb,
    double HighDb, double StopbandDb)
{
    public double TiltDb => HighDb - LowDb;
}
/// <summary>
/// A comparison's verdict. <see cref="SetupChanges"/> (round 14) carries the
/// setup facts that differed between the two takes: for a comparable result,
/// the differences to NAME in front of the numbers; for a refusal with
/// <c>Reason</c> <c>unlike-takes:source</c>, one entry keyed <c>source</c>
/// whose values are the two takes' sources, so the refusal can say which was
/// which.
/// </summary>
public sealed record AudioDifference(bool Comparable, string Reason, double LevelDb = 0,
    double TiltDb = 0, double CrestDb = 0, double StopbandDb = 0,
    IReadOnlyList<SetupChange> SetupChanges = null);

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
    /// <summary>Compare independent live readings without claiming sample alignment.</summary>
    public static AudioDifference CompareOverallAverage(float[] first, IReadOnlyList<TakeSpan> firstSpans,
        float[] second, IReadOnlyList<TakeSpan> secondSpans, int rate, double filterLow, double filterHigh)
    {
        static (double Level, double Tilt)? Average(float[] samples, IReadOnlyList<TakeSpan> spans,
            int rate, double filterLow, double filterHigh)
        {
            const int window = 512;
            double squares = 0, lowPower = 0, highPower = 0;
            long sampleCount = 0;
            int windows = 0;
            double lowEnd = filterLow + (filterHigh - filterLow) / 3;
            double highStart = filterLow + 2 * (filterHigh - filterLow) / 3;
            foreach (var span in spans)
            {
                int from = (int)Math.Clamp(span.Start, 0, samples.Length);
                int to = (int)Math.Clamp(span.End, 0, samples.Length);
                for (int i = from; i < to; i++) { squares += samples[i] * (double)samples[i]; sampleCount++; }
                for (int start = from; start + window <= to; start += window)
                {
                    var tapered = new double[window];
                    for (int i = 0; i < window; i++)
                        tapered[i] = samples[start + i] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (window - 1)));
                    for (int bin = 1; bin < window / 2; bin++)
                    {
                        double hz = bin * (double)rate / window;
                        if (hz < filterLow || hz > filterHigh || (hz >= lowEnd && hz < highStart)) continue;
                        double coefficient = 2 * Math.Cos(2 * Math.PI * bin / window), p = 0, pp = 0;
                        foreach (double value in tapered) { double current = value + coefficient * p - pp; pp = p; p = current; }
                        double power = Math.Max(0, p * p + pp * pp - coefficient * p * pp);
                        if (hz < lowEnd) lowPower += power;
                        else highPower += power;
                    }
                    windows++;
                }
            }
            if (sampleCount < rate / 4 || windows == 0 || squares <= 0 || lowPower <= 0 || highPower <= 0)
                return null;
            return (Db(squares / sampleCount), Db(highPower / windows) - Db(lowPower / windows));
        }
        var a = Average(first, firstSpans, rate, filterLow, filterHigh);
        var b = Average(second, secondSpans, rate, filterLow, filterHigh);
        return a == null || b == null ? new(false, "no-keyed-gap-free-speech") :
            new(true, "overall-average", b.Value.Level - a.Value.Level, b.Value.Tilt - a.Value.Tilt);
    }
    /// <summary>
    /// The facts two takes must share before their numbers mean anything, or
    /// "" when they do. The radio, the mode, the IQ rate, both antennas and the
    /// receive gain, the frequencies, the dummy load and the render scale are
    /// refusals: a change in any of them changes what the ears heard for
    /// reasons that have nothing to do with the operator's audio. So is the
    /// SOURCE (round 14, #794): a tone take against a voice take, or either
    /// against the reference, is "source".
    ///
    /// <para>NOT refusals any more, RULED by Noel 2026-10-22 05:39: the
    /// transmit filter and the RF power. How narrowing the filter changed the
    /// sound is what the feature is for, so those are compared and the
    /// difference named (<see cref="TakeSetup.Changes"/>). An antenna change
    /// stays a refusal.</para>
    /// </summary>
    public static string Compatibility(TakeInspection a, TakeInspection b)
    {
        foreach (string key in new[] { "serial", "mode", "rate-echoed", "receive-antenna", "rf-gain", "tx-antenna",
            "tx-frequency-mhz", "pan-centre-mhz", "dummy-load", "render-scale" })
            if (a.Get(key) == null || b.Get(key) == null || a.Get(key) != b.Get(key)) return key;
        if (TakeSetup.Source(a) != TakeSetup.Source(b)) return "source";
        return "";
    }
}
