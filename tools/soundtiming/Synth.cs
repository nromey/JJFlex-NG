using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using JJFlexWpf;
using NAudio.Wave;

namespace SoundTimingTool;

/// <summary>
/// #855 step 3: how long every sound takes to MAKE, offline, with no device.
/// Each sound is rendered through the app's own code (the offline render), so
/// the time is the time the app spends; "play" is the part done before the
/// sound reaches the mixer (an up-front render), "read" the part done as the
/// mixer pulls it (a provider that synthesises as it is read, which in the app
/// runs on the output device's own thread, inside its buffer deadline).
/// </summary>
internal static class Synth
{
    private sealed record Case(string Name, Action Play, double? AudioMs = null);

    public static int Run(int warmRuns, string? outFile, TextWriter console)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); console.WriteLine(s); }
        var c = CultureInfo.InvariantCulture;

        W($"Synthesis timing, {DateTime.Now:yyyy-MM-dd HH:mm}, {warmRuns} warm runs per sound after one cold run.");
        W($"Machine: {Environment.ProcessorCount} logical processors; Stopwatch resolution {1e9 / Stopwatch.Frequency:0.#} ns; "
          + $"{(Debugger.IsAttached ? "debugger attached" : "no debugger")}; build {BuildFlavour()}.");
        W("Times in milliseconds. Cold is the first render of that sound in this process; the very first sound also pays");
        W("for compiling the synthesis code, and is listed separately. Warm is median, 99th percentile and worst.");
        W("");

        // The process-cold figure: the first sound this process ever renders.
        long t0 = Stopwatch.GetTimestamp();
        EarconPlayer.RenderOffline(EarconPlayer.TxStartTone);
        W($"First sound in a fresh process (TxStartTone, including compiling the code): {Ms(Stopwatch.GetTimestamp() - t0):0.00} ms");
        W("");

        var cases = new List<Case>();
        foreach (var e in EarconCatalog.All.Where(e => !e.IsContinuous))
            cases.Add(new Case(e.Id, e.Play));
        cases.Add(new Case("ConnectPhaseTone count=2", () => EarconPlayer.ConnectPhaseTone(2)));
        var cd = Radios.CountdownSettings.Current;
        cases.Add(new Case($"Countdown pip ({cd.PipHz} Hz, {cd.PipMs} ms, {cd.PipSound})",
            () => EarconPlayer.CountdownBeatTone(cd.PipHz, cd.PipMs, false, cd.PipSound)));
        cases.Add(new Case($"Countdown landing ({cd.PipHz * 2} Hz, {cd.LandingMs} ms)",
            () => EarconPlayer.CountdownBeatTone(cd.PipHz * 2, cd.LandingMs, true, cd.PipSound)));

        W("Earcons (play = rendered before the hand-off; read = made while the mixer reads it):");
        foreach (var k in cases)
        {
            var r = Measure(k.Play, warmRuns);
            W($"- {k.Name}: {r.AudioMs:0} ms of sound. Cold {r.Cold:0.000} (play {r.ColdPlay:0.000}, read {r.ColdRead:0.000}). "
              + $"Warm play median {r.PlayMedian:0.000}, 99th {r.PlayP99:0.000}, worst {r.PlayMax:0.000}; "
              + $"warm read median {r.ReadMedian:0.000}, worst {r.ReadMax:0.000}.");
        }

        W("");
        W("CW elements (the cost is all in the read, which in the app happens on the output device's thread):");
        foreach (var voiceId in new[] { "Sine", "Hollow" })
        {
            var wave = EarconVoices.ResolveCwWaveform(voiceId);
            foreach (int wpm in new[] { 13, 20, 25, 30, 40 })
            {
                int dit = 1200 / wpm, dah = 3 * dit;
                var dRes = MeasureProvider(() => new CwToneSampleProvider(EarconPlayer.MixerSampleRate, 700, dit, 5, 0.5f, wave.Voice), warmRuns);
                var aRes = MeasureProvider(() => new CwToneSampleProvider(EarconPlayer.MixerSampleRate, 700, dah, 5, 0.5f, wave.Voice), warmRuns);
                W($"- {wave.Label} at {wpm} WPM: dit {dit} ms made in median {dRes.median:0.000} (cold {dRes.cold:0.000}, worst {dRes.max:0.000}); "
                  + $"dah {dah} ms in median {aRes.median:0.000} (cold {aRes.cold:0.000}, worst {aRes.max:0.000}).");
            }
        }

        W("");
        W("For comparison, playing a sound already in memory (copying 100 ms of mono samples, the pre-rendered path):");
        var mem = new float[EarconPlayer.MixerSampleRate / 10];
        var copy = MeasureProvider(() => new ArrayProvider(mem), warmRuns);
        W($"- median {copy.median:0.0000} ms, worst {copy.max:0.0000} ms.");

        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
            console.WriteLine($"wrote {outFile}");
        }
        return 0;
    }

    private static string BuildFlavour()
    {
        var a = typeof(EarconPlayer).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().FirstOrDefault();
        return a?.IsJITOptimizerDisabled == true ? "Debug (JIT optimiser off)" : "optimised";
    }

    private sealed class Result
    {
        public double Cold, ColdPlay, ColdRead, AudioMs;
        public double PlayMedian, PlayP99, PlayMax, ReadMedian, ReadMax;
    }

    private static Result Measure(Action play, int warm)
    {
        var r = new Result();
        var s = EarconPlayer.RenderOfflineTimed(play, 20000, out long p0, out long r0);
        r.ColdPlay = Ms(p0); r.ColdRead = Ms(r0); r.Cold = r.ColdPlay + r.ColdRead;
        r.AudioMs = s.Length / 2 * 1000.0 / EarconPlayer.MixerSampleRate;
        var plays = new double[warm];
        var reads = new double[warm];
        for (int i = 0; i < warm; i++)
        {
            EarconPlayer.RenderOfflineTimed(play, 20000, out long p, out long rd);
            plays[i] = Ms(p);
            reads[i] = Ms(rd);
        }
        r.PlayMedian = Dsp.Median(plays); r.PlayP99 = Dsp.Percentile(plays, 0.99); r.PlayMax = plays.Max();
        r.ReadMedian = Dsp.Median(reads); r.ReadMax = reads.Max();
        return r;
    }

    private static (double cold, double median, double max) MeasureProvider(Func<ISampleProvider> make, int warm)
    {
        var buf = new float[4410];
        double Once()
        {
            var p = make();
            long t = Stopwatch.GetTimestamp();
            while (p.Read(buf.AsSpan()) > 0) { }
            return Ms(Stopwatch.GetTimestamp() - t);
        }
        double cold = Once();
        var w = new double[warm];
        for (int i = 0; i < warm; i++) w[i] = Once();
        return (cold, Dsp.Median(w), w.Max());
    }

    private sealed class ArrayProvider : ISampleProvider
    {
        private readonly float[] _d;
        private int _pos;
        public ArrayProvider(float[] d) { _d = d; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 1);
        public int Read(Span<float> buffer)
        {
            int n = Math.Min(buffer.Length, _d.Length - _pos);
            if (n <= 0) return 0;
            _d.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
