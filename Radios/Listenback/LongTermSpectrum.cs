using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Radios.Listenback;

/// <summary>
/// One third-octave band of a long-term average spectrum (Sprint 49 Track J,
/// J3). <see cref="PowerDbfs"/> is the band's mean power, dB relative to a
/// sample of one squared (a full-scale sine reads -3.01); <see cref="LevelDb"/>
/// is the band's share of the power in every band INSIDE the passband, dB, so
/// two spectra compare by shape, not loudness. A band outside the passband
/// has <see cref="Inside"/> false and no meaningful <see cref="LevelDb"/>.
/// </summary>
public sealed record SpectrumBand(int Index, double CentreHz, double LowerHz, double UpperHz,
    double PowerDbfs, double LevelDb, bool Inside)
{
    /// <summary>The band's nominal name, Hz (100, 125, 160 ... 8000), as the standards print it.</summary>
    public int NominalHz => LongTermSpectrumMeter.NominalCentresHz[Index];
}

/// <summary>Why a long-term spectrum could not be measured. No words here: the reader chooses the sentence.</summary>
public enum LongTermSpectrumRefusal
{
    None = 0,
    /// <summary>The take cannot be read (missing, cut short, or failing its checksum).</summary>
    NotPlayable,
    /// <summary>The take's mode has no speech render (anything but USB and LSB).</summary>
    NoSpeechRender,
    /// <summary>Less than a quarter of a second of keyed, gap-free audio.</summary>
    TooLittleSpeech,
    /// <summary>The take's transmit filter holds no whole third-octave band.</summary>
    PassbandHoldsNoBand,
    /// <summary>The take's header has no usable transmit filter.</summary>
    NoTransmitFilter,
}

/// <summary>A long-term average speech spectrum: the result of <see cref="LongTermSpectrumMeter"/>.</summary>
public sealed record LongTermSpectrum(bool Available, LongTermSpectrumRefusal Refusal,
    IReadOnlyList<SpectrumBand> Bands, double PassbandLowHz, double PassbandHighHz, int Rate,
    double SecondsMeasured, int FramesUsed, int FramesTotal)
{
    public static LongTermSpectrum Refused(LongTermSpectrumRefusal why, double low = 0, double high = 0, int rate = 0) =>
        new(false, why, Array.Empty<SpectrumBand>(), low, high, rate, 0, 0, 0);

    /// <summary>The bands inside the passband, in frequency order.</summary>
    public IEnumerable<SpectrumBand> InsideBands => Bands.Where(b => b.Inside);
}

/// <summary>
/// The suggest pass's measurement (Sprint 49 Track J, J3, #736): a long-term
/// average spectrum of a voice over ALL of a take's keyed, gap-free speech, in
/// third-octave bands, inside the take's transmit filter, normalised to the
/// take's own speech power so it compares SHAPE.
/// </summary>
/// <remarks>
/// <para><b>Why not <see cref="AudioDifferential.Measure"/>:</b> that reads
/// three broad bands from the first 4,096 samples it is given (about 85 ms at
/// 48 kHz). Its readers rely on it, so it is left as it is; this measures the
/// whole selection.</para>
/// <para><b>How:</b> a Welch power spectrum. Frames of about a sixth of a
/// second (a power of two: 8,192 samples at 48 kHz, 5.9 Hz resolution), Hann
/// window, half overlapping, each lying wholly inside one keyed gap-free
/// span, so no frame straddles a key edge or a gap. Each frame's bins are
/// summed into third-octave bands (IEC 61260 base-ten centres, 100 Hz to
/// 8 kHz), calibrated so a sine of amplitude A reads A squared over two. The
/// FFT is our own radix-2 transform: <c>Radios</c> takes no FFT package.</para>
/// <para><b>Pauses are left out:</b> a frame whose in-passband power is more
/// than <see cref="ActivityGateDb"/> below the 90th-percentile frame is a
/// pause, and is not averaged. An off-air take carries receiver hiss in its
/// pauses; averaging it would tilt the shape toward the hiss.</para>
/// <para><b>Inside the passband:</b> a band counts when it lies inside the
/// transmit filter, allowing <see cref="EdgeOverhang"/> at each edge (the 2.5
/// kHz band, up to 2.82 kHz, counts on a 2.7 kHz filter).</para>
/// <para>The selection of keyed gap-free audio is the same one
/// <see cref="TakeStatistics.Measure"/> makes, through the same
/// <see cref="TakeTimeline"/> calls; it is not shared code because
/// <see cref="TakeStatistics"/> is read-only to this track (reported).</para>
/// </remarks>
public static class LongTermSpectrumMeter
{
    /// <summary>A frame this far below the 90th-percentile frame is a pause.</summary>
    public const double ActivityGateDb = 30.0;

    /// <summary>How far past each edge of the transmit filter a band may reach and still count.</summary>
    public const double EdgeOverhang = 1.05;

    /// <summary>The nominal third-octave centres the bands are named by, 100 Hz to 8 kHz.</summary>
    public static readonly int[] NominalCentresHz =
        { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };

    /// <summary>The exact base-ten centre of band <paramref name="index"/> (index 10 is 1 kHz).</summary>
    public static double CentreHz(int index) => 1000.0 * Math.Pow(10.0, (index - 10) / 10.0);

    public static double LowerHz(int index) => CentreHz(index) * Math.Pow(10.0, -0.05);

    public static double UpperHz(int index) => CentreHz(index) * Math.Pow(10.0, 0.05);

    /// <summary>Whether a band counts as inside a transmit filter.</summary>
    public static bool Inside(int index, double filterLow, double filterHigh) =>
        LowerHz(index) * EdgeOverhang >= filterLow && UpperHz(index) <= filterHigh * EdgeOverhang;

    /// <summary>The frame length for a rate: the power of two nearest above a sixth of a second.</summary>
    public static int FrameLength(int rate)
    {
        int n = 512;
        while (n < rate / 6) n <<= 1;
        return n;
    }

    /// <summary>Measure a whole array as one span.</summary>
    public static LongTermSpectrum Measure(float[] samples, int rate, double filterLow, double filterHigh) =>
        Measure(samples, new[] { new TakeSpan(0, samples?.Length ?? 0) }, rate, filterLow, filterHigh);

    /// <summary>Measure the given spans of <paramref name="samples"/>; nothing outside them is read.</summary>
    public static LongTermSpectrum Measure(float[] samples, IReadOnlyList<TakeSpan> spans, int rate, double filterLow, double filterHigh)
    {
        if (samples == null || spans == null || rate <= 0) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.TooLittleSpeech, filterLow, filterHigh, rate);
        if (!(filterHigh > filterLow) || !(filterLow >= 0)) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.NoTransmitFilter, filterLow, filterHigh, rate);
        var clean = new List<TakeSpan>();
        long total = 0, longest = 0;
        foreach (var s in spans)
        {
            long from = Math.Clamp(s.Start, 0, samples.Length), to = Math.Clamp(s.End, from, samples.Length);
            if (to <= from) continue;
            clean.Add(new TakeSpan(from, to));
            total += to - from;
            longest = Math.Max(longest, to - from);
        }
        if (total < rate / 4) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.TooLittleSpeech, filterLow, filterHigh, rate);

        int bandCount = 0;
        while (bandCount < NominalCentresHz.Length && UpperHz(bandCount) < rate / 2.0) bandCount++;
        var inside = new bool[bandCount];
        bool any = false;
        for (int b = 0; b < bandCount; b++) { inside[b] = Inside(b, filterLow, filterHigh); any |= inside[b]; }
        if (!any) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.PassbandHoldsNoBand, filterLow, filterHigh, rate);

        int n = FrameLength(rate);
        while (n > 512 && n > longest) n >>= 1;
        if (n > longest) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.TooLittleSpeech, filterLow, filterHigh, rate);

        // Which band each bin belongs to (-1: none).
        var binBand = new int[n / 2];
        for (int k = 1; k < n / 2; k++)
        {
            double hz = k * (double)rate / n;
            binBand[k] = -1;
            for (int b = 0; b < bandCount; b++)
                if (hz >= LowerHz(b) && hz < UpperHz(b)) { binBand[k] = b; break; }
        }
        var window = new double[n];
        double windowPower = 0;
        for (int i = 0; i < n; i++) { window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); windowPower += window[i] * window[i]; }
        double scale = 2.0 / (n * windowPower);

        var frames = new List<double[]>();
        var re = new double[n];
        var im = new double[n];
        int hop = n / 2;
        foreach (var s in clean)
        {
            for (long start = s.Start; start + n <= s.End; start += hop)
            {
                for (int i = 0; i < n; i++) { re[i] = samples[start + i] * window[i]; im[i] = 0; }
                Radix2Fft.Forward(re, im);
                var power = new double[bandCount];
                for (int k = 1; k < n / 2; k++)
                {
                    int b = binBand[k];
                    if (b >= 0) power[b] += (re[k] * re[k] + im[k] * im[k]) * scale;
                }
                frames.Add(power);
            }
        }
        if (frames.Count == 0) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.TooLittleSpeech, filterLow, filterHigh, rate);

        // The activity gate.
        double InsideTotal(double[] p) { double t = 0; for (int b = 0; b < bandCount; b++) if (inside[b]) t += p[b]; return t; }
        var totals = frames.Select(InsideTotal).OrderBy(t => t).ToArray();
        double p90 = totals[Math.Min(totals.Length - 1, (int)Math.Floor(0.9 * (totals.Length - 1)))];
        double gate = p90 * Math.Pow(10.0, -ActivityGateDb / 10.0);
        var mean = new double[bandCount];
        int used = 0;
        foreach (var p in frames)
        {
            if (!(InsideTotal(p) >= gate) || p90 <= 0) continue;
            for (int b = 0; b < bandCount; b++) mean[b] += p[b];
            used++;
        }
        if (used == 0) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.TooLittleSpeech, filterLow, filterHigh, rate);
        for (int b = 0; b < bandCount; b++) mean[b] /= used;
        double insideTotal = 0;
        for (int b = 0; b < bandCount; b++) if (inside[b]) insideTotal += mean[b];

        var bands = new SpectrumBand[bandCount];
        for (int b = 0; b < bandCount; b++)
            bands[b] = new SpectrumBand(b, CentreHz(b), LowerHz(b), UpperHz(b), Db(mean[b]),
                inside[b] && insideTotal > 0 ? Db(mean[b] / insideTotal) : double.NaN, inside[b]);
        double seconds = used * (double)hop / rate + (n - hop) / (double)rate;
        return new LongTermSpectrum(true, LongTermSpectrumRefusal.None, bands, filterLow, filterHigh, rate, seconds, used, frames.Count);
    }

    /// <summary>
    /// Measure a listen-back take: render it as <see cref="TakeStatistics"/>
    /// does (reusing an existing render when asked), keep its keyed gap-free
    /// audio, and measure it inside the take's recorded transmit filter. USB
    /// and LSB only, as every speech render is.
    /// </summary>
    public static LongTermSpectrum MeasureTake(string directory, bool reuseRender = false)
    {
        var take = TakeReader.Inspect(directory);
        if (!take.Playable) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.NotPlayable);
        if (take.Get("mode") is not ("USB" or "LSB")) return LongTermSpectrum.Refused(LongTermSpectrumRefusal.NoSpeechRender);
        if (!double.TryParse(take.Get("tx-filter-low"), NumberStyles.Float, CultureInfo.InvariantCulture, out double low) ||
            !double.TryParse(take.Get("tx-filter-high"), NumberStyles.Float, CultureInfo.InvariantCulture, out double high))
            return LongTermSpectrum.Refused(LongTermSpectrumRefusal.NoTransmitFilter);
        if (!reuseRender || !File.Exists(Path.Combine(directory, "heard.wav")))
            SsbTakeRenderer.Render(directory);
        var (rate, heard) = TakeAudio.ReadMono(Path.Combine(directory, "heard.wav"));
        var spans = TakeTimeline.WithoutGaps(TakeTimeline.KeyedSpans(take, heard.Length), TakeTimeline.Gaps(take));
        return Measure(heard, spans, rate, low, high);
    }

    private static double Db(double power) => 10.0 * Math.Log10(Math.Max(1e-30, power));
}

/// <summary>
/// An in-place radix-2 complex FFT (decimation in time, iterative), written
/// from the textbook Cooley-Tukey algorithm. Offline use only; it allocates
/// nothing but is not tuned for the audio thread.
/// </summary>
internal static class Radix2Fft
{
    public static void Forward(double[] re, double[] im)
    {
        int n = re.Length;
        if (n != im.Length || n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("length must be a power of two");
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = a + half;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
