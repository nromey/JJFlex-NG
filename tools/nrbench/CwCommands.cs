using System.Globalization;
using System.Text;

namespace NrBench;
// Sprint 49 bench CW: two commands.
//   nrbench cw <capture.wav> <outDir> [--name <take>] [--start s] [--seconds s] [--width Hz]
//       [--helper <helper.exe>] [--deepfilter <nativeDir>]
//     Renders one CW capture through every engine, each twice: as the engine leaves it
//     (wide), and through a narrow filter at the station's pitch AFTER the engine. Every
//     file is level-matched on the station (see LevelMatch) and measured with the CW
//     measure; <name>-measures.txt holds one line per file. Self-checks run first.
//   nrbench cwmeasure <reference.wav> <file.wav>... [--start s] [--seconds s] [--pitch Hz]
//       [--width Hz] [--narrow]
//     Measures existing files against an unprocessed reference with the CW measure.
// The left channel only, in both: these captures are a centred slice, left equal to right,
// and the commands print how far apart the two channels actually are.
partial class BenchApp
{
    sealed record CwEngine(string File, string Words, Stage? Stage);

    static readonly string[] CwListeningOrder =
    {
        "unprocessed", "nnr-standard-cw", "nnr-premium-cw", "nr2", "nr4", "rnnoise", "deepfilternet3", "nnr-standard", "nnr-premium",
    };

    static CwEngine[] CwEngines() => new CwEngine[]
    {
        new("unprocessed", "the capture itself, no noise reduction", null),
        new("nnr-standard-cw", "NNR, Standard CW model (WDSP 2.11 slot 2)", Nnr("standard_cw")),
        new("nnr-premium-cw", "NNR, Premium CW model (slot 3)", Nnr("premium_cw")),
        new("nr2", "NR2, WDSP's spectral noise reduction at its receive defaults", Nr2()),
        new("nr4", "NR4, libspecbleach at the app's defaults (20 dB reduction)", Nr4()),
        new("rnnoise", "RNNoise at 80 percent, the app's default", Rn()),
        new("deepfilternet3", "DeepFilterNet3, attenuation limit 9 dB, the app's default", Neural(9)),
        new("nnr-standard", "NNR, Standard voice model (slot 0)", Nnr("standard")),
        new("nnr-premium", "NNR, Premium voice model (slot 1)", Nnr("premium")),
    };

    static float[] LeftChannel(float[] interleaved, int channels, out string words)
    {
        if (channels == 1) { words = "mono"; return interleaved; }
        var left = new float[interleaved.Length / channels];
        double diff = 0, energy = 0;
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = interleaved[i * channels];
            double d = interleaved[i * channels] - interleaved[i * channels + 1];
            diff += d * d; energy += (double)left[i] * left[i];
        }
        words = diff == 0 ? "left channel; the right channel is identical" :
            $"left channel; the right differs from it by {10 * Math.Log10(diff / Math.Max(1e-30, energy)):0.0} dB";
        return left;
    }

    static float[] CwSpan(float[] x, double start, double seconds)
    {
        int s0 = Math.Clamp((int)(start * Rate), 0, x.Length);
        int n = seconds > 0 ? Math.Clamp((int)(seconds * Rate), 0, x.Length - s0) : x.Length - s0;
        return x.AsSpan(s0, n).ToArray();
    }

    /// <summary>The station's key-down level, as the RMS of a narrow-filtered file over the key-down blocks, in dBFS.</summary>
    static double KeyDownRmsDb(float[] narrow, CwMask mask, int lag)
    {
        double s = 0; long c = 0;
        for (int i = 0; i < mask.Key.Length; i++)
        {
            if (mask.Key[i] != true) continue;
            int centre = (i + lag) * CwHop + CwWindow / 2;
            for (int j = centre - CwHop / 2; j < centre + CwHop / 2; j++)
                if (j >= 0 && j < narrow.Length) { s += (double)narrow[j] * narrow[j]; c++; }
        }
        return 10 * Math.Log10(s / Math.Max(1, c) + 1e-30);
    }

    static double PeakDb(float[] x) => 20 * Math.Log10(Math.Max(1e-12, x.Max(Math.Abs)));

    static int Cw(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("cw wants <capture.wav> <outDir>"); return 2; }
        string capturePath = Path.GetFullPath(a[0]), outDir = Path.GetFullPath(a[1]);
        string name = Path.GetFileName(Path.GetDirectoryName(capturePath)) ?? "take";
        double start = 0, seconds = 0, width = 250;
        string? native = null;
        for (int i = 2; i < a.Length; i++)
            switch (a[i])
            {
                case "--name": name = a[++i]; break;
                case "--start": start = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--width": width = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--helper": HelperPath = Path.GetFullPath(a[++i]); break;
                case "--deepfilter": native = a[++i]; break;
                default: throw new ArgumentException("Unknown cw argument " + a[i]);
            }
        string library = LibraryPath(native);
        if (SelfTest(capturePath) != 0) throw new InvalidOperationException("Selftest failed; cw refused before rendering");

        var all = LoadWav(capturePath, out int channels, out string format);
        var capture = CwSpan(LeftChannel(all, channels, out string channelWords), start, seconds);
        var pitch = FindPitch(capture);
        var pass = FindPassband(capture, pitch.Hz);
        var mask = MakeMask(capture, pitch.Hz);
        int keyBlocks = mask.Key.Count(k => k == true), gapBlocks = mask.Key.Count(k => k == false);
        Console.WriteLine($"{name}: pitch {pitch.Hz:0.0} Hz (activity {pitch.ScoreDb:0.0} dB; next best {pitch.RunnerUpHz:0} Hz at {pitch.RunnerUpScoreDb:0.0} dB); passband {pass.Lo:0} to {pass.Hi:0} Hz.");

        Directory.CreateDirectory(outDir);
        var renders = new List<(int Number, CwEngine Engine, string Kind, float[] Audio, CwFigures Figures, double KeyDb)>();
        CwFigures? wideRef = null, narrowRef = null;
        var refused = new List<string>();
        int number = 0;
        foreach (string id in CwListeningOrder)
        {
            var engine = CwEngines().Single(e => e.File == id);
            float[] wide;
            try
            {
                if (engine.Stage == null) wide = capture;
                else
                {
                    var t = System.Diagnostics.Stopwatch.StartNew();
                    var output = ProcessChain(capture, 1, new() { engine.Stage.Copy() }, library, out int latency);
                    wide = output.AsSpan(latency, capture.Length).ToArray();
                    Console.WriteLine($"{name}: {engine.Words}: rendered in {t.Elapsed.TotalSeconds:0} s, delay {latency} samples removed.");
                }
            }
            catch (Exception ex) { refused.Add($"{engine.Words}: REFUSED, {ex.Message}"); Console.WriteLine(refused[^1]); number += 2; continue; }
            var narrow = NarrowBand(wide, pitch.Hz, width);
            foreach (var (kind, audio, lo, hi) in new[] { ("wide", wide, pass.Lo, pass.Hi), ("narrow", narrow, pitch.Hz - width / 2, pitch.Hz + width / 2) })
            {
                number++;
                var f = Measure(audio, mask, pitch.Hz, lo, hi);
                if (engine.Stage == null && kind == "wide") wideRef = f;
                if (engine.Stage == null && kind == "narrow") narrowRef = f;
                renders.Add((number, engine, kind, audio, f, KeyDownRmsDb(narrow, mask, f.Lag)));
            }
        }
        if (wideRef == null || narrowRef == null) throw new InvalidOperationException("The unprocessed capture did not measure");

        // Level match on the station: every file's key-down, through the narrow filter, at one target,
        // the loudest target at or below -20 dBFS at which no file peaks above -1 dBFS.
        double target = -20;
        foreach (var r in renders) target = Math.Min(target, -1 - (PeakDb(r.Audio) - r.KeyDb));
        var lines = new StringBuilder();
        lines.AppendLine($"{name}: {capture.Length / (double)Rate:0.0} s from {start:0.0} s of {Path.GetFileName(Path.GetDirectoryName(capturePath))}{Path.DirectorySeparatorChar}{Path.GetFileName(capturePath)} ({format}, {channelWords}).");
        lines.AppendLine($"Station pitch {pitch.Hz:0.0} Hz, found from keying activity over the whole span (next best {pitch.RunnerUpHz:0} Hz, {pitch.ScoreDb - pitch.RunnerUpScoreDb:0.0} dB less active). Receiver passband {pass.Lo:0} to {pass.Hi:0} Hz.");
        lines.AppendLine($"Key-down and gaps from the unprocessed capture through the narrow filter: {keyBlocks} key-down and {gapBlocks} gap blocks of 10 ms.");
        lines.AppendLine($"Level: every file scaled so the station's key-down, through a {width:0} Hz filter at its pitch, is {target:0.0} dBFS RMS.");
        lines.AppendLine($"Narrow filter: {width:0} Hz at the -6 dB points, centred on the pitch, applied after the engine: a linear-phase windowed sinc, 4,095 taps, 4-term Blackman-Harris, as WDSP builds its band-pass, delay removed.");
        foreach (var r in renders)
        {
            string file = $"{name}-{r.Number:00}-{r.Engine.File}-{r.Kind}.wav";
            double gain = target - r.KeyDb;
            WriteWav(Path.Combine(outDir, file), r.Audio, 1, Math.Pow(10, gain / 20));
            var reference = r.Kind == "wide" ? wideRef : narrowRef;
            lines.AppendLine(CwLine(file, r.Figures, reference) + $" Scaled {gain:+0.0;-0.0;0.0} dB.");
        }
        foreach (string r in refused) lines.AppendLine(r);
        File.WriteAllText(Path.Combine(outDir, name + "-measures.txt"), lines.ToString());
        Console.Write(lines);
        return refused.Count == 0 ? 0 : 1;
    }

    static int CwMeasureCommand(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("cwmeasure wants <reference.wav> <file.wav>..."); return 2; }
        double start = 0, seconds = 0, width = 250; double? pitchHz = null; bool narrow = false;
        var files = new List<string>();
        string reference = Path.GetFullPath(a[0]);
        for (int i = 1; i < a.Length; i++)
            switch (a[i])
            {
                case "--start": start = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--width": width = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--pitch": pitchHz = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--narrow": narrow = true; break;
                default: files.Add(Path.GetFullPath(a[i])); break;
            }
        var raw = LoadWav(reference, out int ch, out _);
        var capture = CwSpan(LeftChannel(raw, ch, out string words), start, seconds);
        var found = FindPitch(capture);
        double pitch = pitchHz ?? found.Hz;
        var pass = FindPassband(capture, pitch);
        var mask = MakeMask(capture, pitch);
        Console.WriteLine($"Reference {reference} ({words}), {capture.Length / (double)Rate:0.0} s from {start:0.0} s.");
        Console.WriteLine($"Pitch {pitch:0.0} Hz{(pitchHz != null ? $" (given; the finder chose {found.Hz:0.0})" : $" (next best {found.RunnerUpHz:0} Hz, {found.ScoreDb - found.RunnerUpScoreDb:0.0} dB less active)")}; passband {pass.Lo:0} to {pass.Hi:0} Hz; " +
                          $"{mask.Key.Count(k => k == true)} key-down and {mask.Key.Count(k => k == false)} gap blocks.");
        var wideRef = Measure(capture, mask, pitch, pass.Lo, pass.Hi);
        var narrowCapture = NarrowBand(capture, pitch, width);
        var narrowRef = Measure(narrowCapture, mask, pitch, pitch - width / 2, pitch + width / 2);
        Console.WriteLine(CwLine("reference", wideRef, wideRef));
        if (narrow) Console.WriteLine(CwLine("reference then the narrow filter", narrowRef, narrowRef));
        foreach (string path in files)
        {
            var audio = LoadWav(path, out int fc, out _);
            var x = CwSpan(LeftChannel(audio, fc, out _), start, seconds);
            Console.WriteLine(CwLine(Path.GetFileName(path), Measure(x, mask, pitch, pass.Lo, pass.Hi), wideRef));
            if (narrow) Console.WriteLine(CwLine(Path.GetFileName(path) + " then the narrow filter", Measure(NarrowBand(x, pitch, width), mask, pitch, pitch - width / 2, pitch + width / 2), narrowRef));
        }
        return 0;
    }
}
