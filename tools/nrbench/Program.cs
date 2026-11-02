// Offline only: reads WAV files and writes WAVs/text; no audio devices.
// nrbench suite <recordingsRoot> [--out <resultsRoot>] [--deepfilter <nativeDir>]
// nrbench <input.wav> <outDir> [--deepfilter <nativeDir>]
// nrbench cpu [input.wav] [--out <report.txt>] [--deepfilter <nativeDir>]
// nrbench selftest [--mutation <control-name>]
// nrbench synth <speech.wav> <outDir> [--start s] [--seconds s] [--snr dB] [--carrier Hz] [--seed n]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using JJFlexWpf;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NrBench;
partial class BenchApp
{
const int Rate = 48000, Frame = 480;
static int Main(string[] args) => Entry(args);
static int Synth(string[] a)
{
    if (a.Length < 2) { Console.Error.WriteLine("synth wants <speech.wav> <outDir>"); return 2; }
    string speechPath = a[0], outDir = a[1];
    double start = 0, len = 30, snrDb = 5, carrierHz = 0;
    int seed = 731;
    for (int i = 2; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--start": start = double.Parse(a[++i]); break;
            case "--seconds": len = double.Parse(a[++i]); break;
            case "--snr": snrDb = double.Parse(a[++i]); break;
            case "--carrier": carrierHz = double.Parse(a[++i]); break;
            case "--seed": seed = int.Parse(a[++i]); break;
            default: Console.Error.WriteLine($"unknown option {a[i]}"); return 2;
        }
    }
    Directory.CreateDirectory(outDir);

    var all = Mono(LoadWav(speechPath, out int ch, out _), ch);
    int s0 = Math.Clamp((int)(start * Rate), 0, all.Length);
    int n = Math.Clamp((int)(len * Rate), 0, all.Length - s0);
    var speech = all.AsSpan(s0, n).ToArray();

    // An SSB receiver's passband: speech above 300 Hz and below 2.7 kHz.
    BandPass(speech, 300, 2700);

    // Band noise in the same passband, the kind a receiver's AGC rides on.
    var rng = new Random(seed);
    var noise = new float[n];
    for (int i = 0; i < n; i++) noise[i] = (float)Gaussian(rng);
    BandPass(noise, 200, 2900);

    // Scale the noise against the speech's ACTIVE level (silences excluded),
    // so the stated SNR is the one a listener hears during words.
    double speechActive = ActiveRms(speech);
    double noiseRms = Math.Sqrt(noise.Average(v => (double)v * v));
    double gain = speechActive / Math.Max(1e-12, noiseRms) / Math.Pow(10, snrDb / 20);
    var noisy = new float[n];
    for (int i = 0; i < n; i++)
    {
        double v = speech[i] + noise[i] * gain;
        if (carrierHz > 0) v += speechActive * 0.3 * Math.Sin(2 * Math.PI * carrierHz * i / Rate);
        noisy[i] = (float)v;
    }

    // Common scale so the louder of the two peaks at -3 dBFS and the clean
    // file stays sample-aligned and level-matched with the noisy one.
    double peak = Math.Max(noisy.Max(Math.Abs), speech.Max(Math.Abs));
    float k = (float)(Math.Pow(10, -3 / 20.0) / Math.Max(1e-9, peak));
    for (int i = 0; i < n; i++) { noisy[i] *= k; speech[i] *= k; }

    WriteWav(Path.Combine(outDir, "noisy.wav"), noisy, 1);
    WriteWav(Path.Combine(outDir, "clean.wav"), speech, 1);
    Console.WriteLine($"synth: {n / (double)Rate:F1} s of {Path.GetFileName(speechPath)} from {start:F1} s, passband 300 to 2700 Hz, " +
                      $"band noise at {snrDb:F1} dB SNR against active speech{(carrierHz > 0 ? $", carrier at {carrierHz:F0} Hz" : "")}, seed {seed}.");
    Console.WriteLine($"wrote {Path.Combine(outDir, "noisy.wav")} and clean.wav");
    return 0;
}

// ===========================================================================
// Helpers
// ===========================================================================

static float[] LoadWav(string path, out int channels, out string description)
{
    using var reader = new WaveFileReader(path);
    ISampleProvider sp = reader.ToSampleProvider();
    channels = sp.WaveFormat.Channels;
    if (channels > 2) throw new NotSupportedException($"{path}: {channels} channels; the bench takes mono or stereo.");
    description = $"{reader.WaveFormat.SampleRate} Hz {reader.WaveFormat.BitsPerSample}-bit {reader.WaveFormat.Encoding}";
    if (sp.WaveFormat.SampleRate != Rate)
    {
        sp = new WdlResamplingSampleProvider(sp, Rate);
        description += $", resampled to {Rate} Hz";
    }
    var list = new List<float>();
    var chunk = new float[Rate * channels];
    int got;
    while ((got = sp.Read(chunk)) > 0)
        list.AddRange(chunk.AsSpan(0, got).ToArray());
    int whole = list.Count / channels * channels;
    return list.GetRange(0, whole).ToArray();
}

static void WriteWav(string path, float[] samples, int channels)
{
    using var w = new WaveFileWriter(path, new WaveFormat(Rate, 16, channels));
    var clamped = new float[samples.Length];
    for (int i = 0; i < samples.Length; i++) {
        if (!float.IsFinite(samples[i]) || Math.Abs(samples[i]) > 1f) throw new InvalidOperationException("Refusing a WAV that would clip or contain non-finite samples");
        clamped[i] = samples[i];
    }
    w.WriteSamples(clamped, 0, clamped.Length);
}

static float[] Mono(float[] interleaved, int channels)
{
    if (channels == 1) return interleaved;
    var m = new float[interleaved.Length / channels];
    for (int i = 0; i < m.Length; i++)
    {
        float s = 0;
        for (int c = 0; c < channels; c++) s += interleaved[i * channels + c];
        m[i] = s / channels;
    }
    return m;
}

static double RmsDb(float[] s)
{
    double sum = 0;
    foreach (float v in s) sum += (double)v * v;
    return 10 * Math.Log10(sum / Math.Max(1, s.Length) + 1e-20);
}

static double ActiveRms(float[] s)
{
    // RMS over 20 ms blocks within 30 dB of the loudest block.
    int b = Rate / 50;
    var blocks = new List<double>();
    for (int i = 0; i + b <= s.Length; i += b)
    {
        double e = 0;
        for (int j = 0; j < b; j++) e += (double)s[i + j] * s[i + j];
        blocks.Add(e / b);
    }
    if (blocks.Count == 0) return 0;
    double max = blocks.Max();
    var active = blocks.Where(e => e > max * 1e-3).ToList();
    return Math.Sqrt(active.Average());
}

static double Gaussian(Random r)
{
    double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
    return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
}

// Two cascaded RBJ second-order sections each way: a 24 dB per octave band-pass.
static void BandPass(float[] s, double lowHz, double highHz)
{
    for (int pass = 0; pass < 2; pass++)
    {
        Biquad(s, HighPassCoeffs(lowHz));
        Biquad(s, LowPassCoeffs(highHz));
    }
}

static double[] LowPassCoeffs(double f)
{
    double w = 2 * Math.PI * f / Rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * 0.7071);
    double a0 = 1 + al;
    return new[] { (1 - c) / 2 / a0, (1 - c) / a0, (1 - c) / 2 / a0, -2 * c / a0, (1 - al) / a0 };
}

static double[] HighPassCoeffs(double f)
{
    double w = 2 * Math.PI * f / Rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * 0.7071);
    double a0 = 1 + al;
    return new[] { (1 + c) / 2 / a0, -(1 + c) / a0, (1 + c) / 2 / a0, -2 * c / a0, (1 - al) / a0 };
}

static void Biquad(float[] s, double[] k)
{
    double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
    for (int i = 0; i < s.Length; i++)
    {
        double x = s[i];
        double y = k[0] * x + k[1] * x1 + k[2] * x2 - k[3] * y1 - k[4] * y2;
        x2 = x1; x1 = x; y2 = y1; y1 = y;
        s[i] = (float)y;
    }
}

/// How far b lags a, in ms, from the cross-correlation peak over 0 to 250 ms.
static double DelayMs(float[] a, float[] b) => DelaySamples(a, b) * 1000.0 / Rate;

static int DelaySamples(float[] a, float[] b)
{
    if (Mutation == "delay-finder") return 0;
    const int N = 1 << 18;           // about 5.5 s
    int maxLag = Rate / 4;
    int len = Math.Min(a.Length, b.Length);
    if (len < N / 2) return 0;
    int start = Math.Max(0, Math.Min(len - N / 2, len / 2 - N / 4));
    var fa = new System.Numerics.Complex[N];
    var fb = new System.Numerics.Complex[N];
    for (int i = 0; i < N / 2 && start + i < len; i++)
    {
        fa[i] = a[start + i];
        fb[i] = b[start + i];
    }
    Fft(fa, false); Fft(fb, false);
    for (int i = 0; i < N; i++) fa[i] = fb[i] * System.Numerics.Complex.Conjugate(fa[i]);
    Fft(fa, true);
    int best = 0; double bestV = double.MinValue;
    for (int lag = 0; lag <= maxLag; lag++)
        if (fa[lag].Real > bestV) { bestV = fa[lag].Real; best = lag; }
    return best;
}

/// Scale-invariant signal-to-distortion ratio of `estimate` against `reference`, after delay removal.
static double Fidelity(float[] reference, float[] estimate)
{
    int d = DelaySamples(reference, estimate);
    int n = Math.Min(reference.Length, estimate.Length - d);
    if (n <= 0) return double.NaN;
    double dot = 0, rr = 0;
    for (int i = 0; i < n; i++) { dot += (double)reference[i] * estimate[i + d]; rr += (double)reference[i] * reference[i]; }
    double alpha = dot / Math.Max(1e-20, rr);
    double es = 0, en = 0;
    for (int i = 0; i < n; i++)
    {
        double t = alpha * reference[i];
        double e = estimate[i + d] - t;
        es += t * t; en += e * e;
    }
    return 10 * Math.Log10(es / Math.Max(1e-20, en));
}

static void Fft(System.Numerics.Complex[] x, bool inverse)
{
    int n = x.Length;
    for (int i = 1, j = 0; i < n; i++)
    {
        int bit = n >> 1;
        for (; (j & bit) != 0; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) (x[i], x[j]) = (x[j], x[i]);
    }
    for (int size = 2; size <= n; size <<= 1)
    {
        double ang = 2 * Math.PI / size * (inverse ? 1 : -1);
        var wn = new System.Numerics.Complex(Math.Cos(ang), Math.Sin(ang));
        for (int i = 0; i < n; i += size)
        {
            var w = System.Numerics.Complex.One;
            for (int k = 0; k < size / 2; k++)
            {
                var u = x[i + k];
                var v = x[i + k + size / 2] * w;
                x[i + k] = u + v;
                x[i + k + size / 2] = u - v;
                w *= wn;
            }
        }
    }
    if (inverse) for (int i = 0; i < n; i++) x[i] /= n;
}

static string Sanitise(string name)
{
    var sb = new StringBuilder();
    foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
    return sb.ToString();
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
    return d?.FullName ?? "";
}

}
