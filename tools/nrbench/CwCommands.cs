using System.Globalization;
using System.Text;

namespace NrBench;
// Sprint 49 bench CW: three commands.
//   nrbench cw <capture.wav> <outDir> [--name <take>] [--start s] [--seconds s] [--width Hz]
//       [--pitch Hz] [--station Hz[@from-to]]... [--engines id,id,...] [--target-db dBFS]
//       [--segments] [--helper <helper.exe>] [--deepfilter <nativeDir>]
//     Renders one CW capture through every engine (or the named ones, in that order), each
//     twice: as the engine leaves it (wide), and through a narrow filter at the station's pitch
//     AFTER the engine. Every file is level-matched on the station (see LevelMatch) and
//     measured with the CW measure; <name>-measures.txt holds one line per file. Self-checks
//     run first. Without --pitch, the found pitch must be clearly ahead of the next keyed one
//     or the command refuses. Each --station is a second station followed element by element
//     (between from and to seconds of the span, if given) and kept out of the main station's
//     rest of the passband.
//   nrbench cwmeasure <reference.wav> <file.wav>... [--start s] [--seconds s] [--pitch Hz]
//       [--station Hz[@from-to]]... [--width Hz] [--narrow] [--aligned] [--segments]
//     Measures existing files against an unprocessed reference with the CW measure. --aligned
//     says the files are already aligned to the reference, so no delay is searched for.
//   nrbench cwscan <capture.wav> [--start s] [--seconds s] [--window s] [--step s]
//     Lists the keyed pitches in each stretch of a capture, best first: how to find a station.
// The left channel only: these captures are a centred slice, left equal to right, and the
// commands print how far apart the two channels actually are.
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
        new("nr2", "NR2, WDSP's spectral noise reduction at its receive defaults (OSMS noise estimator)", Nr2()),
        new("nr2-mmse", "NR2 with the MMSE noise estimator, otherwise its receive defaults", Nr2("mmse")),
        new("nr2-nstat", "NR2 with the non-stationary noise estimator, otherwise its receive defaults", Nr2("nstat")),
        new("nr4", "NR4, libspecbleach at the app's defaults (20 dB reduction, smoothing 0.5)", Nr4()),
        new("nr4-smooth", "NR4 at the app's defaults except smoothing 1.0, the most the setting allows", Nr4(1.0)),
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

    /// <summary>A second station to follow: its pitch, and optionally the stretch of the span it is in.</summary>
    sealed record CwStationSpec(double Hz, double From, double To)
    {
        public static CwStationSpec Parse(string v)
        {
            var parts = v.Split('@');
            double hz = double.Parse(parts[0], CultureInfo.InvariantCulture);
            if (parts.Length == 1) return new(hz, 0, double.PositiveInfinity);
            var span = parts[1].Split('-');
            return new(hz, double.Parse(span[0], CultureInfo.InvariantCulture), double.Parse(span[1], CultureInfo.InvariantCulture));
        }
        public string Words => $"{Hz:0} Hz" + (double.IsPositiveInfinity(To) ? "" : $" ({From:0} to {To:0} s)");
    }

    sealed record CwStation(CwStationSpec Spec, CwMask Mask);

    /// <summary>One file's line for a second station, against the reference and against the main station's own change.</summary>
    static string StationLine(CwStation st, CwFigures file, CwFigures reference, double mainChangeDb)
    {
        var s = Survival(file, reference, mainChangeDb);
        if (double.IsNaN(s.StationChangeDb)) return $"  the station at {st.Spec.Words}: too few of its elements seen to follow ({s.Measured}).";
        return $"  the station at {st.Spec.Words}: {s.StationChangeDb - mainChangeDb:+0.0;-0.0;0.0} dB against the main station; elements kept {s.Kept} of {s.Measured}; " +
               $"key-down {file.Separation:0.0} dB above its gaps ({file.Separation - reference.Separation:+0.0;-0.0;0.0} dB against the capture).";
    }

    static string SegmentLine(CwFigures file, CwFigures reference) =>
        "  the station's level against the capture, by 20 s from the start: " +
        string.Join(", ", Survival(file, reference).SegmentChangeDb.Select(v => double.IsNaN(v) ? "unseen" : v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture))) + " dB.";

    static int Cw(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("cw wants <capture.wav> <outDir>"); return 2; }
        string capturePath = Path.GetFullPath(a[0]), outDir = Path.GetFullPath(a[1]);
        string name = Path.GetFileName(Path.GetDirectoryName(capturePath)) ?? "take";
        double start = 0, seconds = 0, width = 250, targetDb = -20; double? pitchHz = null;
        bool segments = false;
        string? native = null;
        var specs = new List<CwStationSpec>();
        var order = CwListeningOrder.ToList();
        for (int i = 2; i < a.Length; i++)
            switch (a[i])
            {
                case "--name": name = a[++i]; break;
                case "--start": start = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--width": width = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--pitch": pitchHz = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--station": specs.Add(CwStationSpec.Parse(a[++i])); break;
                case "--engines": order = a[++i].Split(',').ToList(); break;
                case "--target-db": targetDb = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--segments": segments = true; break;
                case "--helper": HelperPath = Path.GetFullPath(a[++i]); break;
                case "--deepfilter": native = a[++i]; break;
                default: throw new ArgumentException("Unknown cw argument " + a[i]);
            }
        if (!order.Contains("unprocessed")) order.Insert(0, "unprocessed");
        foreach (string id in order)
            if (!CwEngines().Any(e => e.File == id)) throw new ArgumentException($"Unknown engine {id}; known: {string.Join(", ", CwEngines().Select(e => e.File))}");
        string library = LibraryPath(native);
        if (SelfTest(capturePath) != 0) throw new InvalidOperationException("Selftest failed; cw refused before rendering");

        var all = LoadWav(capturePath, out int channels, out string format);
        var capture = CwSpan(LeftChannel(all, channels, out string channelWords), start, seconds);
        var found = FindPitch(capture);
        // The confidence gate: a found pitch must be clearly the most keyed. A filter change, a
        // pileup or a second station can otherwise win, and every level and figure follows it.
        if (pitchHz == null && found.ScoreDb - found.RunnerUpScoreDb < 1)
            throw new InvalidOperationException($"The pitch finder's choice, {found.Hz:0.0} Hz, is not clearly ahead of {found.RunnerUpHz:0} Hz " +
                $"({found.ScoreDb - found.RunnerUpScoreDb:0.0} dB). Listen, then give the station's pitch with --pitch.");
        double pitch = pitchHz ?? found.Hz;
        var pass = FindPassband(capture, pitch);
        var mask = MakeMask(capture, pitch);
        var others = specs.Select(s => s.Hz).ToArray();
        var stations = specs.Select(s => new CwStation(s, MakeMask(capture, s.Hz, s.From, s.To))).ToList();
        int keyBlocks = mask.Key.Count(k => k == true), gapBlocks = mask.Key.Count(k => k == false);
        Console.WriteLine($"{name}: pitch {pitch:0.0} Hz{(pitchHz != null ? $" (given; the finder chose {found.Hz:0.0})" : "")}; passband {pass.Lo:0} to {pass.Hi:0} Hz.");

        Directory.CreateDirectory(outDir);
        var renders = new List<(int Number, CwEngine Engine, string Kind, float[] Audio, CwFigures Figures, double KeyDb, List<CwFigures> Stations)>();
        CwFigures? wideRef = null, narrowRef = null;
        var stationRefs = new List<CwFigures>();
        var refused = new List<string>();
        int number = 0;
        foreach (string id in order)
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
            var narrow = NarrowBand(wide, pitch, width);
            foreach (var (kind, audio, lo, hi) in new[] { ("wide", wide, pass.Lo, pass.Hi), ("narrow", narrow, pitch - width / 2, pitch + width / 2) })
            {
                number++;
                // Every render here has its delay removed, so it is aligned by construction: no search.
                var f = Measure(audio, mask, pitch, lo, hi, others, 0);
                var st = stations.Select(s => Measure(audio, s.Mask, s.Spec.Hz, lo, hi, new[] { pitch }.Concat(others.Where(o => o != s.Spec.Hz)).ToArray(), 0)).ToList();
                if (engine.Stage == null && kind == "wide") { wideRef = f; stationRefs = st; }
                if (engine.Stage == null && kind == "narrow") narrowRef = f;
                renders.Add((number, engine, kind, audio, f, KeyDownRmsDb(narrow, mask, 0), st));
            }
        }
        if (wideRef == null || narrowRef == null) throw new InvalidOperationException("The unprocessed capture did not measure");

        // Level match on the station: every file's key-down, through the narrow filter, at one target,
        // the loudest target at or below the requested one at which no file peaks above -1 dBFS.
        double target = targetDb;
        foreach (var r in renders) target = Math.Min(target, -1 - (PeakDb(r.Audio) - r.KeyDb));
        var lines = new StringBuilder();
        lines.AppendLine($"{name}: {capture.Length / (double)Rate:0.0} s from {start:0.0} s of {Path.GetFileName(Path.GetDirectoryName(capturePath))}{Path.DirectorySeparatorChar}{Path.GetFileName(capturePath)} ({format}, {channelWords}).");
        lines.AppendLine(pitchHz != null
            ? $"Station pitch {pitch:0.0} Hz, given (the finder chose {found.Hz:0.0} Hz). Receiver passband {pass.Lo:0} to {pass.Hi:0} Hz."
            : $"Station pitch {pitch:0.0} Hz, found from keying activity (next best {found.RunnerUpHz:0} Hz, {found.ScoreDb - found.RunnerUpScoreDb:0.0} dB less active). Receiver passband {pass.Lo:0} to {pass.Hi:0} Hz.");
        lines.AppendLine($"Key-down and gaps from the unprocessed capture, against a threshold that follows the station's level: {keyBlocks} key-down and {gapBlocks} gap blocks of 5 ms, " +
                         $"{mask.Elements.Length} elements, {mask.ShortElements} too short to measure; {100.0 * mask.UncertainBlocks / mask.Key.Length:0} percent of the span too faint or too near an edge to classify.");
        foreach (var s in stations) lines.AppendLine($"Also followed: the station at {s.Spec.Words}, {s.Mask.Elements.Length} elements seen.");
        lines.AppendLine($"Level: every file scaled so the station's key-down, through a {width:0} Hz filter at its pitch, is {target:0.0} dBFS RMS.");
        lines.AppendLine($"Narrow filter: {width:0} Hz at the -6 dB points, centred on the pitch, applied after the engine: a linear-phase windowed sinc, 4,095 taps, 4-term Blackman-Harris, as WDSP builds its band-pass, delay removed.");
        foreach (var r in renders)
        {
            string file = $"{name}-{r.Number:00}-{r.Engine.File}-{r.Kind}.wav";
            double gain = target - r.KeyDb;
            WriteWav(Path.Combine(outDir, file), r.Audio, 1, Math.Pow(10, gain / 20));
            var reference = r.Kind == "wide" ? wideRef : narrowRef;
            lines.AppendLine(CwLine(file, r.Figures, reference) + $" Scaled {gain:+0.0;-0.0;0.0} dB.");
            if (r.Kind == "wide")
            {
                double mainChange = Survival(r.Figures, reference).StationChangeDb;
                for (int s = 0; s < stations.Count; s++) lines.AppendLine(StationLine(stations[s], r.Stations[s], stationRefs[s], mainChange));
                if (segments) lines.AppendLine(SegmentLine(r.Figures, reference));
            }
        }
        foreach (string r in refused) lines.AppendLine(r);
        File.WriteAllText(Path.Combine(outDir, name + "-measures.txt"), lines.ToString());
        Console.Write(lines);
        return refused.Count == 0 ? 0 : 1;
    }

    static int CwMeasureCommand(string[] a)
    {
        if (a.Length < 1) { Console.Error.WriteLine("cwmeasure wants <reference.wav> [<file.wav>...]"); return 2; }
        double start = 0, seconds = 0, width = 250; double? pitchHz = null; bool narrow = false, aligned = false, segments = false;
        var files = new List<string>();
        var specs = new List<CwStationSpec>();
        string reference = Path.GetFullPath(a[0]);
        for (int i = 1; i < a.Length; i++)
            switch (a[i])
            {
                case "--start": start = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--width": width = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--pitch": pitchHz = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--station": specs.Add(CwStationSpec.Parse(a[++i])); break;
                case "--narrow": narrow = true; break;
                case "--aligned": aligned = true; break;
                case "--segments": segments = true; break;
                case "--spectrum": PrintSpectrum = true; break;
                default: files.Add(Path.GetFullPath(a[i])); break;
            }
        var raw = LoadWav(reference, out int ch, out _);
        var capture = CwSpan(LeftChannel(raw, ch, out string words), start, seconds);
        var found = FindPitch(capture);
        double pitch = pitchHz ?? found.Hz;
        var pass = FindPassband(capture, pitch);
        var mask = MakeMask(capture, pitch);
        var others = specs.Select(s => s.Hz).ToArray();
        var stations = specs.Select(s => new CwStation(s, MakeMask(capture, s.Hz, s.From, s.To))).ToList();
        int? lag = aligned ? 0 : null;
        Console.WriteLine($"Reference {reference} ({words}), {capture.Length / (double)Rate:0.0} s from {start:0.0} s.");
        Console.WriteLine($"Pitch {pitch:0.0} Hz{(pitchHz != null ? $" (given; the finder chose {found.Hz:0.0})" : $" (next best {found.RunnerUpHz:0} Hz, {found.ScoreDb - found.RunnerUpScoreDb:0.0} dB less active)")}; passband {pass.Lo:0} to {pass.Hi:0} Hz; " +
                          $"{mask.Key.Count(k => k == true)} key-down and {mask.Key.Count(k => k == false)} gap blocks, {mask.Elements.Length} elements, " +
                          $"{100.0 * mask.UncertainBlocks / mask.Key.Length:0} percent too faint or too near an edge to classify.");
        foreach (var s in stations) Console.WriteLine($"Also followed: the station at {s.Spec.Words}, {s.Mask.Elements.Length} elements seen.");
        CwFigures StationFigures(float[] x, CwStation s, double lo, double hi) =>
            Measure(x, s.Mask, s.Spec.Hz, lo, hi, new[] { pitch }.Concat(others.Where(o => o != s.Spec.Hz)).ToArray(), lag ?? 0);
        var wideRef = Measure(capture, mask, pitch, pass.Lo, pass.Hi, others, 0);
        var stationRefs = stations.Select(s => StationFigures(capture, s, pass.Lo, pass.Hi)).ToList();
        var narrowCapture = NarrowBand(capture, pitch, width);
        var narrowRef = Measure(narrowCapture, mask, pitch, pitch - width / 2, pitch + width / 2, others, 0);
        Console.WriteLine(CwLine("reference", wideRef, wideRef));
        if (narrow) Console.WriteLine(CwLine("reference then the narrow filter", narrowRef, narrowRef));
        foreach (string path in files)
        {
            var audio = LoadWav(path, out int fc, out _);
            var x = CwSpan(LeftChannel(audio, fc, out _), start, seconds);
            var f = Measure(x, mask, pitch, pass.Lo, pass.Hi, others, lag);
            Console.WriteLine(CwLine(Path.GetFileName(path), f, wideRef));
            double mainChange = Survival(f, wideRef).StationChangeDb;
            for (int s = 0; s < stations.Count; s++) Console.WriteLine(StationLine(stations[s], StationFigures(x, stations[s], pass.Lo, pass.Hi), stationRefs[s], mainChange));
            if (segments) Console.WriteLine(SegmentLine(f, wideRef));
            if (narrow) Console.WriteLine(CwLine(Path.GetFileName(path) + " then the narrow filter", Measure(NarrowBand(x, pitch, width), mask, pitch, pitch - width / 2, pitch + width / 2, others, lag), narrowRef));
        }
        return 0;
    }

    static int CwScanCommand(string[] a)
    {
        if (a.Length < 1) { Console.Error.WriteLine("cwscan wants <capture.wav>"); return 2; }
        double start = 0, seconds = 0, window = 15, step = 5;
        for (int i = 1; i < a.Length; i++)
            switch (a[i])
            {
                case "--start": start = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--window": window = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--step": step = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("Unknown cwscan argument " + a[i]);
            }
        var raw = LoadWav(Path.GetFullPath(a[0]), out int ch, out _);
        var capture = CwSpan(LeftChannel(raw, ch, out string words), start, seconds);
        Console.WriteLine($"{a[0]} ({words}): the keyed pitches in each {window:0} s stretch, best first, with their keying score (the level's spread within 2 s).");
        for (double t = 0; t + window <= capture.Length / (double)Rate + 1e-9; t += step)
        {
            var p = FindPitch(CwSpan(capture, t, window));
            Console.WriteLine($"{start + t:0} to {start + t + window:0} s: " + string.Join(", ", p.Candidates.Take(6).Select(c => $"{c.Hz:0} Hz {c.ScoreDb:0.0} dB")) + ".");
        }
        return 0;
    }
}
