// Sprint 48 Track G (#731): nrbench, the offline noise-reduction bench.
//
// Renders one input WAV through each noise-reduction variant the app can run,
// using the app's own sources (linked, see nrbench.csproj), and writes one WAV
// per variant plus a plain-text report a screen reader can read top to bottom.
//
//   dotnet run --project tools/nrbench -- <input.wav> <outDir> [options]
//
//   --clean <wav>            the clean original, when the input was made by
//                            mixing noise into it; adds a fidelity figure
//   --external <name>=<wav>  fold in a WAV produced elsewhere (repeatable);
//                            it is copied beside the others and reported the
//                            same way, so a denoiser that cannot live in this
//                            repository can still be compared on the same
//                            recording
//   --deepfilter <dir>       folder holding deepfilter.dll and its model;
//                            default runtimes\win-x64\native in this repository
//
//   dotnet run --project tools/nrbench -- synth <speech.wav> <outDir> [options]
//
//   Makes a synthetic test input: the speech band-limited to an SSB passband,
//   mixed with generated noise. Writes noisy.wav and clean.wav.
//   --start <s>  --seconds <s>  --snr <dB>  --carrier <Hz>  --seed <n>
//
// Input may be mono or stereo at any rate; it is resampled to 48 kHz. Output
// WAVs are 48 kHz 16-bit with the input's channel count.
//
// Nothing here opens an audio device or plays a sound.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using JJFlexWpf;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

const int Rate = 48000;
const int Frame = 480;            // 10 ms: one Opus packet on the live receive path
const float AppStrength = 0.8f;   // NoiseReductionProvider's default, and the Home control's default of 80 percent

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length >= 1 && args[0] == "synth")
    return Synth(args.Skip(1).ToArray());
if (args.Length < 2 || args[0].StartsWith("-"))
{
    Console.Error.WriteLine("usage: nrbench <input.wav> <outDir> [--clean <wav>] [--external <name>=<wav>]... [--deepfilter <dir>]");
    Console.Error.WriteLine("       nrbench synth <speech.wav> <outDir> [--start s] [--seconds s] [--snr dB] [--carrier Hz] [--seed n]");
    return 2;
}
return Bench(args);

// ===========================================================================
// The bench
// ===========================================================================

int Bench(string[] a)
{
    string inputPath = a[0];
    string outDir = a[1];
    string? cleanPath = null;
    string? dfDir = null;
    var externals = new List<(string Name, string Path)>();
    for (int i = 2; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--clean": cleanPath = a[++i]; break;
            case "--deepfilter": dfDir = a[++i]; break;
            case "--external":
                string spec = a[++i];
                int eq = spec.IndexOf('=');
                if (eq <= 0) { Console.Error.WriteLine($"--external wants name=path, got {spec}"); return 2; }
                externals.Add((spec[..eq], spec[(eq + 1)..]));
                break;
            default: Console.Error.WriteLine($"unknown option {a[i]}"); return 2;
        }
    }

    Directory.CreateDirectory(outDir);
    dfDir ??= Path.Combine(FindRepoRoot(), "runtimes", "win-x64", "native");
    string dfLib = Path.Combine(dfDir, DeepFilterNet3Engine.LibraryFileName);

    var input = LoadWav(inputPath, out int channels, out string inputDescription);
    float[]? clean = cleanPath != null ? Mono(LoadWav(cleanPath, out int cleanCh, out _), cleanCh) : null;
    double seconds = input.Length / (double)channels / Rate;

    var report = new StringBuilder();
    void Line(string s = "") { report.AppendLine(s); Console.WriteLine(s); }

    Line("Noise reduction bench report");
    Line();
    Line($"Made {DateTime.Now:yyyy-MM-dd HH:mm} by tools/nrbench, which runs the app's own noise-reduction code.");
    Line($"Input: {Path.GetFileName(inputPath)}, {inputDescription}, {seconds:F1} seconds, {channels} channel{(channels == 1 ? "" : "s")}.");
    if (clean != null)
        Line($"Clean original for the fidelity figure: {Path.GetFileName(cleanPath!)}.");
    Line($"Every variant is fed the way the live receive path feeds it: one 10 ms packet ({Frame} samples per channel) at a time, through NoiseReductionProvider, one engine per channel.");
    Line($"Times are wall-clock on this machine ({Environment.ProcessorCount} logical processors), single-threaded, for the whole file divided by its 10 ms frames.");
    Line("The real-time factor is processing time over audio time: 0.05 means one second of audio took a twentieth of a second to process.");
    Line("Measured delay is how far the output lags the input, found by cross-correlation, not quoted from any documentation.");
    Line("Output level is RMS in dBFS. Noise reduction lowers it, and a quieter file tends to sound worse in a quick comparison, so match levels before judging.");
    if (clean != null)
        Line("Fidelity is scale-invariant signal-to-distortion ratio against the clean original, after removing the measured delay. Higher is closer to the clean speech; the unprocessed figure is the starting point.");
    Line();

    var variants = new List<Variant>
    {
        new("unprocessed", "Unprocessed: the input as it went in.", null, 0f),
        new("rnnoise", $"RNNoise at the app's default strength of {AppStrength * 100:F0} percent, exactly as the app runs it today.",
            () => new RnnoiseEngine(RnnoiseScaling.Historical), AppStrength),
        new("rnnoise-corrected", $"RNNoise at {AppStrength * 100:F0} percent with its input scaled once instead of twice. Not what the app does; rendered so the scaling question can be settled by ear.",
            () => new RnnoiseEngine(RnnoiseScaling.Corrected), AppStrength),
        new("deepfilternet3-unlimited", "DeepFilterNet3 with no attenuation limit (upstream's default, 100 dB), fully wet.",
            () => new DeepFilterNet3Engine(100f, dfLib), 1f),
        new("deepfilternet3-limit-18db", "DeepFilterNet3 limited to 18 dB of attenuation, fully wet.",
            () => new DeepFilterNet3Engine(18f, dfLib), 1f),
        new("deepfilternet3-limit-9db", "DeepFilterNet3 limited to 9 dB of attenuation, fully wet: the gentlest setting here.",
            () => new DeepFilterNet3Engine(9f, dfLib), 1f),
    };

    int number = 0;
    foreach (var v in variants)
    {
        number++;
        var buf = (float[])input.Clone();
        string file = v.Name + ".wav";
        Line($"Variant {number}: {v.Name}");
        Line(v.Description);

        if (v.Factory == null)
        {
            WriteWav(Path.Combine(outDir, file), buf, channels);
            Line($"File: {file}.");
            Line($"Output level: {RmsDb(buf):F1} dBFS.");
            if (clean != null) Line($"Fidelity: {Fidelity(clean, Mono(buf, channels)):F1} dB.");
            Line();
            continue;
        }

        using var nr = new NoiseReductionProvider(Rate, channels, v.Factory) { Enabled = true, Strength = v.Strength };
        var bank = nr.Bank;
        if (bank == null || !bank.IsAvailable)
        {
            string why = bank?.EngineFor(0) is DeepFilterNet3Engine d ? d.UnavailableReason : "the engine did not start";
            Line($"NOT RENDERED: {why}");
            Line();
            continue;
        }

        int packet = Frame * channels;
        int frames = 0;
        var sw = Stopwatch.StartNew();
        for (int pos = 0; pos + packet <= buf.Length; pos += packet, frames++)
            nr.ProcessInPlace(buf, pos, packet, channels);
        sw.Stop();
        // A final short packet, so the whole file is processed (and the carry exercised once).
        int tail = buf.Length % packet;
        if (tail > 0) nr.ProcessInPlace(buf, buf.Length - tail, tail, channels);

        WriteWav(Path.Combine(outDir, file), buf, channels);
        double msPerFrame = sw.Elapsed.TotalMilliseconds / Math.Max(1, frames);
        double rtf = sw.Elapsed.TotalSeconds / Math.Max(1e-9, frames * 0.010);
        Line($"File: {file}.");
        Line($"Processing time: {msPerFrame:F3} ms per 10 ms frame for {(channels == 1 ? "the one channel" : $"all {channels} channels together")}, {msPerFrame / channels:F3} ms per channel.");
        Line($"Real-time factor: {rtf:F3}, meaning processing took {rtf * 100:F1} percent of the audio's own duration.");
        Line($"Measured delay: {DelayMs(Mono(input, channels), Mono(buf, channels)):F1} ms. " +
             $"The engine reports {bank.EngineLatencySamples * 1000.0 / Rate:F1} ms of its own, " +
             $"and the frame carry added {bank.CarryLatencySamples * 1000.0 / Rate:F1} ms " +
             "(nothing on 10 ms packets; the short last packet is what moves it).");
        if (bank.FallbackFrames > 0)
            Line($"WARNING: {bank.FallbackFrames} frames went out unprocessed because the engine failed on them.");
        Line($"Output level: {RmsDb(buf):F1} dBFS.");
        if (bank.EngineFor(0) is DeepFilterNet3Engine)
        {
            double sum = 0; long n = 0;
            for (int ch = 0; ch < channels; ch++)
                if (bank.EngineFor(ch) is DeepFilterNet3Engine e) { sum += e.SnrSumDb; n += e.FramesProcessed; }
            Line($"Mean per-frame SNR DeepFilterNet3 reported: {sum / Math.Max(1, n):F1} dB, over {n} frames.");
        }
        if (clean != null)
            Line($"Fidelity: {Fidelity(clean, Mono(buf, channels)):F1} dB.");
        Line();
    }

    foreach (var (name, path) in externals)
    {
        number++;
        Line($"Variant {number}: external, {name}");
        Line($"Produced outside this repository and folded in for comparison: {Path.GetFileName(path)}.");
        if (!File.Exists(path)) { Line("NOT FOUND."); Line(); continue; }
        var ext = LoadWav(path, out int extCh, out string extDesc);
        string file = "external-" + Sanitise(name) + ".wav";
        WriteWav(Path.Combine(outDir, file), ext, extCh);
        Line($"File: {file}, copied from {extDesc}.");
        Line("Processing time: not measured here; it was produced by another program.");
        Line($"Measured delay: {DelayMs(Mono(input, channels), Mono(ext, extCh)):F1} ms.");
        Line($"Output level: {RmsDb(ext):F1} dBFS.");
        if (clean != null) Line($"Fidelity: {Fidelity(clean, Mono(ext, extCh)):F1} dB.");
        Line();
    }

    Line("End of report.");
    File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
    return 0;
}

// ===========================================================================
// Synthetic input
// ===========================================================================

int Synth(string[] a)
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
    for (int i = 0; i < samples.Length; i++) clamped[i] = Math.Clamp(samples[i], -1f, 1f);
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
    return d?.FullName ?? Environment.CurrentDirectory;
}

record Variant(string Name, string Description, Func<INeuralDenoiseEngine>? Factory, float Strength);
