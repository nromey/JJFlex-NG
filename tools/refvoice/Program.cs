// Sprint 33 Track I — render the reference-voice script to the WAV that ships
// with JJ Flexible.
//
//   dotnet run --project tools/refvoice -- [scriptPath] [outWav] [--rate N]
//
// --rate is the speech voice's rate, -10 to 10; 0, the default, is the voice's
// own pace, and reproduces the render that shipped until 2026-11-06. THE WAV
// THAT SHIPS NOW IS RENDERED AT --rate 1 (task 905, Noel 2026-11-06 17:43: "I'd
// just jack the TTS speed up so that you end up at 1:50"), every word of the
// script kept, so a Record yourself check carrying it ends with the reference
// rather than at the radio's two-minute recorder limit. Only the speech speeds
// up: the tone, the silence and the gaps between sections keep their lengths.
// Measured 2026-11-06 with Microsoft David Desktop: rate 0 is 127.7 s (byte for
// byte the old shipped file), rate 1 is 113.0 s, rate 2 is 102.4 s, rate 3 is
// 92.0 s, rate 4 is 83.4 s. Rate 1 is the closest to 110 s while under 115 s,
// seven seconds inside the recorder's two minutes. To reproduce the shipped
// file, pass the same rate, with the same default voice.
//
// The script (jjflex-reference-voice.txt) is the source of truth. Lines in
// square brackets are directives, everything else is spoken:
//
//   [section: name]                 start a section, gain resets to 0 dB
//   [section: name | gain: -12 dB]  start a section at a fixed gain
//   [tone: 1000 Hz, -20 dBFS, 3 seconds]
//   [silence: 5 seconds]
//
// Output is 48 kHz, 16-bit, mono — the rate the transmit stream runs at by
// default, so the application never has to resample the shipped reference.
//
// The whole render is peak-normalised to a KNOWN peak at the end (see
// TargetPeakDbfs). That is the point of the exercise: a reference file whose
// level depends on which Windows voice happened to be installed is not a
// reference. The relative gains inside the dynamics section survive
// normalisation because it scales everything by one factor.

using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;

const int SampleRate = 48000;

// −3 dBFS: loud enough that the file is unambiguously a full-level signal,
// with enough headroom that no player's resampler or decoder clips it on the
// way back out.
const double TargetPeakDbfs = -3.0;

// A short gap between sections, so the ear (and any segmenter) can tell where
// one ends and the next begins.
const double SectionGapSeconds = 0.35;

string repoRoot = FindRepoRoot();
var positional = new List<string>();
int rate = 0;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--rate")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rate)
            || rate < -10 || rate > 10)
        {
            Console.Error.WriteLine("--rate takes a whole number from -10 to 10");
            return 1;
        }
        i++;
        continue;
    }
    positional.Add(args[i]);
}
string scriptPath = positional.Count > 0
    ? positional[0]
    : Path.Combine(repoRoot, "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.txt");
string outPath = positional.Count > 1
    ? positional[1]
    : Path.Combine(repoRoot, "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");

if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"no script at {scriptPath}");
    return 1;
}

Console.WriteLine($"refvoice — reading {scriptPath}");

var samples = new List<float>(SampleRate * 180);
var synth = new SpeechSynthesizer();
Console.WriteLine($"  voice: {synth.Voice.Name}");
synth.Rate = rate;   // 0 is the voice's own default pace; see the header for the shipped rate.
Console.WriteLine($"  rate: {rate}");

double gainDb = 0;
var spoken = new StringBuilder();
int sections = 0;

foreach (string rawLine in File.ReadAllLines(scriptPath))
{
    string line = rawLine.Trim();

    // The header block above the first [section:] is documentation, not
    // script. Nothing is spoken until the first section opens.
    if (line.StartsWith('[') && line.EndsWith(']'))
    {
        FlushSpeech();
        string directive = line[1..^1];

        if (directive.StartsWith("section:", StringComparison.OrdinalIgnoreCase))
        {
            if (sections > 0) AppendSilence(SectionGapSeconds);
            sections++;
            gainDb = ParseGainDb(directive);
            Console.WriteLine($"  section {sections}: {ParseSectionName(directive)}"
                + (Math.Abs(gainDb) > 0.001 ? $" at {gainDb:+0.#;-0.#} dB" : ""));
        }
        else if (directive.StartsWith("tone:", StringComparison.OrdinalIgnoreCase))
        {
            double hz = ParseNumber(directive, @"([\d.]+)\s*Hz", 1000);
            double db = ParseNumber(directive, @"(-?[\d.]+)\s*dBFS", -20);
            double secs = ParseNumber(directive, @"([\d.]+)\s*second", 3);
            Console.WriteLine($"    tone {hz} Hz, {db} dBFS, {secs} s");
            AppendTone(hz, db, secs);
        }
        else if (directive.StartsWith("silence:", StringComparison.OrdinalIgnoreCase))
        {
            double secs = ParseNumber(directive, @"([\d.]+)\s*second", 5);
            Console.WriteLine($"    silence {secs} s");
            AppendSilence(secs);
        }
        continue;
    }

    if (sections == 0) continue;              // still in the header
    if (line.Length == 0) { FlushSpeech(); continue; }
    if (line.StartsWith("===") || line.StartsWith("---")) continue;

    spoken.Append(line).Append(' ');
}
FlushSpeech();

if (samples.Count == 0)
{
    Console.Error.WriteLine("nothing was rendered — check the script's [section:] markers");
    return 1;
}

Normalise(samples, TargetPeakDbfs);
WriteWav(outPath, samples, SampleRate);

double seconds = samples.Count / (double)SampleRate;
Console.WriteLine();
Console.WriteLine($"wrote {outPath}");
Console.WriteLine($"  {seconds:F1} s, {SampleRate} Hz, mono, 16-bit, "
    + $"{new FileInfo(outPath).Length / 1024.0 / 1024.0:F1} MB");
Console.WriteLine($"  normalised to {TargetPeakDbfs:F1} dBFS peak");
return 0;

// ---------------------------------------------------------------------------

void FlushSpeech()
{
    if (spoken.Length == 0) return;
    string text = spoken.ToString().Trim();
    spoken.Clear();
    if (text.Length == 0) return;

    using var ms = new MemoryStream();
    synth.SetOutputToAudioStream(ms,
        new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
    synth.Speak(text);
    synth.SetOutputToNull();

    float gain = (float)Math.Pow(10.0, gainDb / 20.0);
    byte[] pcm = ms.ToArray();
    for (int i = 0; i + 1 < pcm.Length; i += 2)
    {
        short v = (short)(pcm[i] | (pcm[i + 1] << 8));
        samples.Add(v / 32768f * gain);
    }
}

void AppendSilence(double secs)
{
    int n = (int)(secs * SampleRate);
    for (int i = 0; i < n; i++) samples.Add(0f);
}

void AppendTone(double hz, double dbfs, double secs)
{
    int n = (int)(secs * SampleRate);
    double amp = Math.Pow(10.0, dbfs / 20.0);
    // 10 ms ramps at both ends: an abrupt tone start is a click, and a click
    // in a reference file is a defect every measurement then has to explain.
    int ramp = SampleRate / 100;
    for (int i = 0; i < n; i++)
    {
        double env = 1.0;
        if (i < ramp) env = i / (double)ramp;
        else if (i > n - ramp) env = (n - i) / (double)ramp;
        samples.Add((float)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * amp * env));
    }
}

static void Normalise(List<float> buf, double targetDbfs)
{
    float peak = 0f;
    for (int i = 0; i < buf.Count; i++)
    {
        float a = Math.Abs(buf[i]);
        if (a > peak) peak = a;
    }
    if (peak <= 0f) return;

    float target = (float)Math.Pow(10.0, targetDbfs / 20.0);
    float scale = target / peak;
    for (int i = 0; i < buf.Count; i++) buf[i] *= scale;

    Console.WriteLine($"  peak was {20 * Math.Log10(peak):F1} dBFS, scaled by {20 * Math.Log10(scale):+0.0;-0.0} dB");
}

static void WriteWav(string path, List<float> mono, int rate)
{
    string? dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(fs, Encoding.ASCII);
    int dataBytes = mono.Count * 2;

    w.Write(Encoding.ASCII.GetBytes("RIFF"));
    w.Write((uint)(36 + dataBytes));
    w.Write(Encoding.ASCII.GetBytes("WAVE"));
    w.Write(Encoding.ASCII.GetBytes("fmt "));
    w.Write(16u);
    w.Write((short)1);              // PCM
    w.Write((short)1);              // mono
    w.Write((uint)rate);
    w.Write((uint)(rate * 2));
    w.Write((short)2);
    w.Write((short)16);
    w.Write(Encoding.ASCII.GetBytes("data"));
    w.Write((uint)dataBytes);

    foreach (float s in mono)
    {
        float c = Math.Clamp(s, -1f, 1f);
        w.Write((short)(c * 32767f));
    }
}

static double ParseGainDb(string directive)
{
    var m = Regex.Match(directive, @"gain:\s*([+-]?[\d.]+)\s*dB", RegexOptions.IgnoreCase);
    return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0.0;
}

static string ParseSectionName(string directive)
{
    var m = Regex.Match(directive, @"section:\s*([^|]+)", RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value.Trim() : "(unnamed)";
}

static double ParseNumber(string directive, string pattern, double fallback)
{
    var m = Regex.Match(directive, pattern, RegexOptions.IgnoreCase);
    return m.Success
        ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
        : fallback;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
