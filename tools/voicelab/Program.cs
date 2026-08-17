// Track D2 voicelab — renders the REAL meter-voice synthesis offline so the
// empirical question ("are the voices distinguishable with several playing at
// once under speech?") can be answered by ear, from WAV files, without a
// radio. Also runs an objective separation screen over the built-in alphabet.
//
//   dotnet run --project tools/voicelab -- <outputDir> [speechWavPath]
//
// Outputs (all tones panned CENTRE on purpose — the test must hold in mono,
// because pan is never allowed to be load-bearing):
//   solo-<voice>.wav          each built-in voice, value sweeping
//   trio-dry.wav              3 concurrent meters, moving values
//   quartet-dry.wav           the shipped TX Monitor concurrency (4)
//   quintet-dry.wav           5 concurrent meters
//   *-speech.wav              same, under synthesized speech (if provided)
//   analysis.txt              objective separation screen

using JJFlexWpf;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Text;

const int SampleRate = 44100;

string outDir = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "out");
string? speechPath = args.Length > 1 ? args[1] : null;
Directory.CreateDirectory(outDir);

var report = new StringBuilder();
void Log(string line) { Console.WriteLine(line); report.AppendLine(line); }

Log($"voicelab — rendering with the app's own synthesis ({DateTime.Now:yyyy-MM-dd HH:mm})");
Log("");

// ---------------------------------------------------------------------------
// 1. Solo renders: every built-in voice, value sweeping 0 -> 1 -> mid.
//    Pitch range 200-1200 Hz, the default mapping.
// ---------------------------------------------------------------------------
foreach (var voice in MeterVoiceLibrary.BuiltIns)
{
    var render = RenderScenario(6.0, new[]
    {
        new SimMeter(voice.Name, 0.5f, 200, 1200,
            t => t < 3 ? t / 3.0 : t < 4.5 ? 1 - (t - 3) / 1.5 * 0.7 : 0.3),
    });
    string file = Path.Combine(outDir, $"solo-{voice.Name.Replace(' ', '-')}.wav");
    WriteWav(file, render.Samples);
    if (render.Peak > 0.99f || render.HasNaN)
        Log($"  WARNING {voice.Name}: peak={render.Peak:F2} NaN={render.HasNaN}");
}
Log($"Solo renders: {MeterVoiceLibrary.BuiltIns.Count} voices written.");

// ---------------------------------------------------------------------------
// 2. Ensembles. Value motion imitates real operating:
//    - S-meter: slow random drift (band noise breathing)
//    - SWR: steady low with one excursion (antenna moment)
//    - Mic: syllabic bounce correlated with speech rhythm
//    - ALC: bursts when mic pushes hard
//    - Power: keyed high with ripple
// ---------------------------------------------------------------------------
var rng = new Random(52); // deterministic renders
double drift = 0.5;
double SMeterMotion(double t)
{
    drift = Math.Clamp(drift + (rng.NextDouble() - 0.5) * 0.06, 0.35, 0.75);
    return drift;
}
double SwrMotion(double t) =>
    t is > 8 and < 11.5 ? 0.12 + 0.55 * Math.Sin((t - 8) / 3.5 * Math.PI) : 0.12;
double MicMotion(double t)
{
    double phrase = Math.Max(0, Math.Sin(2 * Math.PI * 0.18 * t));      // phrases
    double syllable = Math.Abs(Math.Sin(2 * Math.PI * 3.8 * t));        // syllables
    return 0.12 + 0.6 * phrase * syllable;
}
double AlcMotion(double t) => Math.Clamp((MicMotion(t) - 0.35) * 1.8, 0, 0.85);
double PowerMotion(double t) => 0.72 + 0.05 * Math.Sin(2 * Math.PI * 0.9 * t);

// The shipped TX Monitor voices at their preset volumes and pitch ranges.
var alc = new SimMeter("Raspy", 0.5f, 300, 1500, AlcMotion);
var mic = new SimMeter("Hollow", 0.4f, 350, 800, MicMotion);
var pwr = new SimMeter("Organ", 0.4f, 200, 1000, PowerMotion);
var swr = new SimMeter("Trill", 0.5f, 200, 1200, SwrMotion);
var smtr = new SimMeter("Pure", 0.6f, 200, 1200, SMeterMotion);

const double EnsembleSeconds = 20.0;
RenderEnsemble("trio", new[] { swr, alc, mic });
RenderEnsemble("quartet", new[] { alc, mic, pwr, swr });
RenderEnsemble("quintet", new[] { smtr, alc, mic, pwr, swr });

void RenderEnsemble(string name, SimMeter[] meters)
{
    var render = RenderScenario(EnsembleSeconds, meters);
    WriteWav(Path.Combine(outDir, $"{name}-dry.wav"), render.Samples);
    Log($"{name}: {meters.Length} voices ({string.Join(", ", meters.Select(m => m.VoiceName))}), " +
        $"peak {render.Peak:F2}{(render.HasNaN ? " NaN!" : "")}");

    if (speechPath != null && File.Exists(speechPath))
    {
        var withSpeech = MixSpeech(render.Samples, speechPath);
        WriteWav(Path.Combine(outDir, $"{name}-speech.wav"), withSpeech);
    }
}

// ---------------------------------------------------------------------------
// 3. Objective separation screen. Each voice rendered steady at 500 Hz; we
//    measure the identity axes the design claims: spectral centroid (timbre
//    brightness), dominant modulation rate and depth (texture), and onset
//    rate (pattern). Pairs close on ALL axes are flagged as likely
//    confusions. This is a screen, not a substitute for ears.
// ---------------------------------------------------------------------------
Log("");
Log("Objective separation screen (steady 500 Hz, 3 s):");
Log("voice; centroid Hz; mod rate Hz; mod depth; onsets per second; pitch motion Hz");

var metrics = new List<(string Name, double Centroid, double ModRate, double ModDepth, double Onsets, double PitchMotion)>();
foreach (var voice in MeterVoiceLibrary.BuiltIns)
{
    var mono = VoicedToneSampleProvider.RenderMono(voice, 500f, 3000, 0.5f);
    var m = Analyze(voice, mono);
    metrics.Add(m);
    Log($"  {m.Name}; {m.Centroid:F0}; {m.ModRate:F1}; {m.ModDepth:F2}; {m.Onsets:F1}; {m.PitchMotion:F1}");
}

Log("");
Log("Pairs close on every axis (candidate confusions):");
bool anyClose = false;
for (int i = 0; i < metrics.Count; i++)
    for (int j = i + 1; j < metrics.Count; j++)
    {
        var a = metrics[i]; var b = metrics[j];
        bool centroidClose = Ratio(a.Centroid, b.Centroid) < 1.25;
        bool modClose = (a.ModDepth < 0.15 && b.ModDepth < 0.15) ||
                        (Ratio(Math.Max(a.ModRate, 0.1), Math.Max(b.ModRate, 0.1)) < 1.4
                         && Math.Abs(a.ModDepth - b.ModDepth) < 0.25);
        bool onsetClose = Ratio(Math.Max(a.Onsets, 0.1), Math.Max(b.Onsets, 0.1)) < 1.5;
        bool pitchMotionClose = (a.PitchMotion < 0.1 && b.PitchMotion < 0.1) ||
            Ratio(Math.Max(a.PitchMotion, 0.1), Math.Max(b.PitchMotion, 0.1)) < 1.5;
        if (centroidClose && modClose && onsetClose && pitchMotionClose)
        {
            Log($"  {a.Name} vs {b.Name}");
            anyClose = true;
        }
    }
if (!anyClose) Log("  none — every pair differs on at least one axis.");

// ---------------------------------------------------------------------------
// 4. Serialization smoke test: the model types ride in audioConfig.xml, so a
//    voice with an override and a derived-source meter must round-trip
//    through XmlSerializer without loss.
// ---------------------------------------------------------------------------
Log("");
Log(SerializationRoundTrip());

File.WriteAllText(Path.Combine(outDir, "analysis.txt"), report.ToString());
Log("");
Log($"Done. Output: {outDir}");
return;

// ===========================================================================

static double Ratio(double a, double b) => a > b ? a / b : b / a;

static string SerializationRoundTrip()
{
    var tweaked = MeterVoiceLibrary.Resolve("Bell").Clone();
    tweaked.Brightness = 0.4f;
    var meters = new List<MeterDefinition>
    {
        new()
        {
            Name = "SWR fine",
            Source = new MeterSourceRef { Kind = MeterSourceKind.RadioReported, Key = "SWR" },
            Range = new MeterRange { Low = 1.0, High = 1.5, Units = MeterUnits.Swr, UnitsLabel = "SWR" },
            VoiceName = "Bell",
            VoiceOverride = tweaked,
            Enabled = true,
            Activation = MeterActivation.TransmitOnly,
        },
        new()
        {
            Name = "NB effectiveness",
            Source = new MeterSourceRef
            {
                Kind = MeterSourceKind.Derived, Key = "NB_IN", SecondaryKey = "NB_OUT",
            },
            Range = new MeterRange { Low = 0, High = 30, Units = MeterUnits.Db, UnitsLabel = "dB" },
            VoiceName = "Trill",
        },
    };

    var voices = new List<MeterVoice> { tweaked };
    try
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(ModelBundle));
        using var ms = new MemoryStream();
        serializer.Serialize(ms, new ModelBundle { Meters = meters, Voices = voices });
        ms.Position = 0;
        var back = (ModelBundle)serializer.Deserialize(ms)!;
        bool ok = back.Meters.Count == 2
            && back.Meters[0].VoiceOverride is { Brightness: 0.4f }
            && back.Meters[0].Range.High == 1.5
            && back.Meters[1].Source.Kind == MeterSourceKind.Derived
            && back.Meters[1].Source.SecondaryKey == "NB_OUT"
            && back.Voices[0].Partials.Length == MeterVoiceLibrary.Resolve("Bell").Partials.Length;
        return ok
            ? "Serialization round-trip: OK (override, narrowed range, derived source, partials all survived)"
            : "Serialization round-trip: FAILED — data lost, inspect before shipping the config change";
    }
    catch (Exception ex)
    {
        return $"Serialization round-trip: THREW {ex.Message}";
    }
}

// Emulates the engine: 10 Hz value updates driving live providers, exactly
// the cadence MeterToneEngine uses.
static RenderResult RenderScenario(double seconds, SimMeter[] meters)
{
    const float MasterVolume = 0.5f; // engine default
    var providers = meters.Select(m => new VoicedToneSampleProvider(m.PitchLow, m.Volume * MasterVolume)
    {
        Voice = MeterVoiceLibrary.Resolve(m.VoiceName),
        Pan = 0f,       // centre: the test must hold in mono
        Active = true,
    }).ToArray();

    int totalFrames = (int)(seconds * SampleRate);
    int stepFrames = SampleRate / 10; // 100 ms, the engine's update throttle
    var mix = new float[totalFrames * 2];
    var block = new float[stepFrames * 2];
    float peak = 0f;
    bool hasNaN = false;

    for (int start = 0; start < totalFrames; start += stepFrames)
    {
        double t = (double)start / SampleRate;
        int frames = Math.Min(stepFrames, totalFrames - start);
        for (int m = 0; m < meters.Length; m++)
        {
            double value = Math.Clamp(meters[m].Motion(t), 0, 1);
            providers[m].Frequency =
                meters[m].PitchLow + (meters[m].PitchHigh - meters[m].PitchLow) * (float)value;
            providers[m].Read(block, 0, frames * 2);
            for (int i = 0; i < frames * 2; i++)
            {
                float s = block[i];
                if (float.IsNaN(s)) { hasNaN = true; s = 0; }
                mix[start * 2 + i] += s;
            }
        }
    }

    for (int i = 0; i < mix.Length; i++)
        peak = Math.Max(peak, Math.Abs(mix[i]));
    if (peak > 0.98f)
    {
        float scale = 0.9f / peak;
        for (int i = 0; i < mix.Length; i++) mix[i] *= scale;
    }
    return new RenderResult(mix, peak, hasNaN);
}

static float[] MixSpeech(float[] toneStereo, string speechPath)
{
    using var reader = new AudioFileReader(speechPath);
    ISampleProvider sp = reader;
    if (sp.WaveFormat.SampleRate != SampleRate)
        sp = new WdlResamplingSampleProvider(sp, SampleRate);
    if (sp.WaveFormat.Channels == 2)
        sp = new StereoToMonoSampleProvider(sp);

    var speech = new List<float>();
    var buf = new float[SampleRate];
    int read;
    while ((read = sp.Read(buf, 0, buf.Length)) > 0)
        for (int i = 0; i < read; i++) speech.Add(buf[i]);

    float speechPeak = speech.Count > 0 ? speech.Max(Math.Abs) : 0f;
    float gain = speechPeak > 0 ? 0.8f / speechPeak : 0f;

    var result = new float[toneStereo.Length];
    int frames = toneStereo.Length / 2;
    for (int i = 0; i < frames; i++)
    {
        float s = speech.Count > 0 ? speech[i % speech.Count] * gain : 0f;
        result[i * 2] = toneStereo[i * 2] + s;
        result[i * 2 + 1] = toneStereo[i * 2 + 1] + s;
    }
    float peak = result.Max(Math.Abs);
    if (peak > 0.98f)
    {
        float scale = 0.9f / peak;
        for (int i = 0; i < result.Length; i++) result[i] *= scale;
    }
    return result;
}

static void WriteWav(string path, float[] stereoSamples)
{
    using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 2));
    writer.WriteSamples(stereoSamples, 0, stereoSamples.Length);
}

// Identity-axis measurements from rendered audio.
static (string Name, double Centroid, double ModRate, double ModDepth, double Onsets, double PitchMotion)
    Analyze(MeterVoice voice, float[] mono)
{
    // Spectral centroid straight from the parameters (exact for additive):
    // centroid = sum(a_n^2 * f_n) / sum(a_n^2), brightness tilt applied.
    double num = 0, den = 0;
    for (int n = 0; n < voice.Partials.Length; n++)
    {
        double amp = voice.Partials[n];
        if (voice.Brightness != 0)
            amp *= Math.Pow(n + 1, 1.5 * voice.Brightness);
        double fn = 500.0 * (n + 1) * (1 + voice.Inharmonicity * n);
        num += amp * amp * fn;
        den += amp * amp;
    }
    // Noise contributes at its band centre (tracks pitch → 500 Hz).
    if (voice.NoiseLevel > 0)
    {
        double namp = voice.NoiseLevel * 2.5;
        double nf = voice.NoiseTracksPitch ? 500.0 : voice.NoiseCenterHz;
        num += namp * namp * nf;
        den += namp * namp;
    }
    double centroid = den > 0 ? num / den : 500;

    // Envelope at 200 Hz: RMS over 5 ms hops, skipping the first 300 ms
    // (attack transient) and the last 50 ms (RenderMono's fade-out tail —
    // leaving it in drags the minimum to zero and reads every voice as
    // deeply modulated, which is how the first run of this screen lied).
    int hop = SampleRate / 200;
    int skip = (int)(0.3 * 200);
    int trimEnd = (int)(0.05 * 200);
    var env = new List<double>();
    for (int start = 0; start + hop <= mono.Length; start += hop)
    {
        double sum = 0;
        for (int i = 0; i < hop; i++) sum += (double)mono[start + i] * mono[start + i];
        env.Add(Math.Sqrt(sum / hop));
    }
    if (env.Count > skip + trimEnd)
    {
        env.RemoveRange(env.Count - trimEnd, trimEnd);
        env.RemoveRange(0, skip);
    }
    double mean = env.Count > 0 ? env.Average() : 0;
    double emax = env.Count > 0 ? env.Max() : 0;
    double emin = env.Count > 0 ? env.Min() : 0;
    double modDepth = emax > 1e-6 ? (emax - emin) / emax : 0;

    // Dominant modulation rate: zero crossings of (env - mean) / 2 / duration.
    int crossings = 0;
    for (int i = 1; i < env.Count; i++)
        if ((env[i - 1] - mean) * (env[i] - mean) < 0) crossings++;
    double duration = env.Count / 200.0;
    double modRate = duration > 0 ? crossings / 2.0 / duration : 0;
    if (modDepth < 0.1) modRate = 0; // steady voices: rate is noise, report 0

    // Onset rate: env rising through 50% of max after dipping under 30% —
    // counts gate strikes (Bell, Pulsing) rather than tremolo wiggle.
    int onsets = 0; bool low = true;
    foreach (double e in env)
    {
        if (e < emax * 0.3) low = true;
        else if (low && e > emax * 0.5) { onsets++; low = false; }
    }
    double onsetRate = duration > 0 ? onsets / duration : 0;

    // Pitch motion is invisible to an amplitude envelope (alternation and
    // vibrato move frequency, not level), so that axis comes straight from
    // the parameters, where it is exact.
    double pitchMotion = Math.Max(
        voice.AlternateIntervalSemitones != 0 ? voice.AlternateRateHz : 0,
        voice.VibratoDepthSemitones != 0 ? voice.VibratoRateHz : 0);

    return (voice.Name, centroid, modRate, modDepth, onsetRate, pitchMotion);
}

record SimMeter(string VoiceName, float Volume, float PitchLow, float PitchHigh,
    Func<double, double> Motion);

record RenderResult(float[] Samples, float Peak, bool HasNaN);

public class ModelBundle
{
    public List<MeterDefinition> Meters { get; set; } = new();
    public List<MeterVoice> Voices { get; set; } = new();
}
