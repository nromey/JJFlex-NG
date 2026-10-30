using System.IO;
namespace SoundTimingTool;

/// <summary>The small amount of signal processing the analyser needs.</summary>
internal static class Dsp
{
    /// <summary>The rate everything is compared at. Earcons and the speech band sit well under its Nyquist.</summary>
    public const int AnalysisRate = 16000;

    public sealed class Biquad
    {
        private double b0, b1, b2, a1, a2, x1, x2, y1, y2;

        public static Biquad LowPass(double fs, double f0, double q = 0.7071)
        {
            double w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
            return new Biquad { b0 = (1 - c) / 2 / a0, b1 = (1 - c) / a0, b2 = (1 - c) / 2 / a0, a1 = -2 * c / a0, a2 = (1 - al) / a0 };
        }

        public static Biquad HighPass(double fs, double f0, double q = 0.7071)
        {
            double w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
            return new Biquad { b0 = (1 + c) / 2 / a0, b1 = -(1 + c) / a0, b2 = (1 + c) / 2 / a0, a1 = -2 * c / a0, a2 = (1 - al) / a0 };
        }

        public double Run(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }

    /// <summary>Mono from interleaved, by averaging the channels.</summary>
    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        var mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++) s += interleaved[i * channels + c];
            mono[i] = s / channels;
        }
        return mono;
    }

    /// <summary>
    /// Resample to <see cref="AnalysisRate"/>: a fourth-order low-pass at 7 kHz,
    /// then linear interpolation. Plenty for locating a sound to a fraction of
    /// a millisecond; not a hi-fi converter.
    /// </summary>
    public static float[] ToAnalysisRate(float[] mono, int fromRate)
    {
        if (fromRate == AnalysisRate) return (float[])mono.Clone();
        var f = new double[mono.Length];
        var lp1 = Biquad.LowPass(fromRate, 7000);
        var lp2 = Biquad.LowPass(fromRate, 7000);
        for (int i = 0; i < mono.Length; i++) f[i] = lp2.Run(lp1.Run(mono[i]));
        int n = (int)((long)mono.Length * AnalysisRate / fromRate);
        var o = new float[n];
        double step = (double)fromRate / AnalysisRate;
        for (int i = 0; i < n; i++)
        {
            double p = i * step;
            int j = (int)p;
            double frac = p - j;
            double a = f[Math.Min(j, f.Length - 1)], b = f[Math.Min(j + 1, f.Length - 1)];
            o[i] = (float)(a + (b - a) * frac);
        }
        return o;
    }

    /// <summary>Index of the first sample at or above <paramref name="fraction"/> of the peak, or -1.</summary>
    public static int FirstAbove(float[] x, double fraction)
    {
        float peak = 0;
        foreach (var v in x) peak = Math.Max(peak, Math.Abs(v));
        if (peak <= 0) return -1;
        float thr = (float)(peak * fraction);
        for (int i = 0; i < x.Length; i++) if (Math.Abs(x[i]) >= thr) return i;
        return -1;
    }

    /// <summary>
    /// Normalised cross-correlation of <paramref name="t"/> against
    /// <paramref name="x"/> at every lag in [from, to). 1 is a perfect match at
    /// any gain; speech on top of the sound lowers it but rarely below 0.5.
    /// </summary>
    public static double[] Ncc(float[] x, int from, int to, float[] t)
    {
        int m = t.Length;
        to = Math.Min(to, x.Length - m);
        if (to <= from || m == 0) return Array.Empty<double>();
        double tt = 0;
        foreach (var v in t) tt += (double)v * v;
        if (tt <= 0) return Array.Empty<double>();
        var r = new double[to - from];
        double xx = 0;
        for (int i = 0; i < m; i++) xx += (double)x[from + i] * x[from + i];
        for (int lag = from; lag < to; lag++)
        {
            if (lag > from)
            {
                double o = x[lag - 1], n = x[lag + m - 1];
                xx += n * n - o * o;
                if (xx < 0) xx = 0;
            }
            double xt = 0;
            for (int i = 0; i < m; i++) xt += (double)x[lag + i] * t[i];
            r[lag - from] = xx > 1e-12 ? xt / Math.Sqrt(xx * tt) : 0;
        }
        return r;
    }

    /// <summary>RMS in consecutive windows of <paramref name="win"/> samples.</summary>
    public static double[] Rms(float[] x, int start, int count, int win)
    {
        int frames = Math.Max(count / win, 0);
        var r = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double s = 0;
            int o = start + f * win;
            for (int i = 0; i < win; i++)
            {
                int k = o + i;
                double v = k >= 0 && k < x.Length ? x[k] : 0;
                s += v * v;
            }
            r[f] = Math.Sqrt(s / win);
        }
        return r;
    }

    public static double Db(double v) => 20 * Math.Log10(v + 1e-12);

    public static double Median(IEnumerable<double> v)
    {
        var a = v.Where(d => !double.IsNaN(d)).OrderBy(d => d).ToArray();
        if (a.Length == 0) return double.NaN;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    public static double Percentile(IEnumerable<double> v, double p)
    {
        var a = v.Where(d => !double.IsNaN(d)).OrderBy(d => d).ToArray();
        if (a.Length == 0) return double.NaN;
        double idx = p * (a.Length - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return a[lo] + (a[hi] - a[lo]) * (idx - lo);
    }

    public static double StdDev(IEnumerable<double> v)
    {
        var a = v.Where(d => !double.IsNaN(d)).ToArray();
        if (a.Length < 2) return double.NaN;
        double m = a.Average();
        return Math.Sqrt(a.Sum(d => (d - m) * (d - m)) / (a.Length - 1));
    }
}
