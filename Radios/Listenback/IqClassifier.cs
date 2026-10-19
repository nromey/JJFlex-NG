using System;
using System.Collections.Generic;

namespace Radios.Listenback;

public sealed record IqClassification(string Verdict, double Correlation, int Period,
    int AlphabetSize, double ObservedStep);

public static class IqClassifier
{
    // Same candidate periods and window as the bench probe. The observed step
    // is evidence from this block, not a promise about every RF signal level.
    public static IqClassification Classify(ReadOnlySpan<float> radioUnits)
    {
        int n = Math.Min(16384, radioUnits.Length);
        if (n < 4096) return new("insufficient-data", 0, 0, 0, 0);
        var alphabet = new HashSet<float>();
        double mean = 0, step = double.PositiveInfinity;
        for (int i = 0; i < n; ++i)
        {
            float v = radioUnits[i];
            if (!float.IsFinite(v)) return new("non-finite", 0, 0, 0, 0);
            mean += v;
            alphabet.Add(v);
            if (v != 0) step = Math.Min(step, Math.Abs(v));
        }
        mean /= n;
        var x = new double[n];
        double power = 0;
        for (int i = 0; i < n; ++i) { x[i] = radioUnits[i] - mean; power += x[i] * x[i]; }
        if (power == 0) return new("constant", 1, 1, alphabet.Count, double.IsFinite(step) ? step : 0);
        double best = 0;
        int period = 0;
        for (int lag = 2; lag < 512; ++lag)
        {
            double sum = 0;
            for (int i = 0; i < n - lag; ++i) sum += x[i] * x[i + lag];
            double r = sum / power;
            if (r > best) { best = r; period = lag; }
        }
        return new(best > .95 ? "repeating-pattern" : "noise-like", best, period,
            alphabet.Count, double.IsFinite(step) ? step : 0);
    }
}
