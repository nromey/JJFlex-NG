using System.Globalization;
using System.IO;
using JJFlexWpf;
using NAudio.Wave;

namespace SoundTimingTool;

/// <summary>
/// The analyser's positive control, with no device and no app: it builds a
/// recording with sounds placed at known instants (one with a hole cut in it,
/// one with a "spoken" burst after it, one CW string), writes trace lines for
/// them, runs the analyser, and checks each sound is found where it was put.
/// An instrument that has never found a thing it was shown is not yet an
/// instrument.
/// </summary>
internal static class SelfTest
{
    private const int Rate = 48000;
    private const long Freq = 10_000_000;       // trace QPC in 100 ns units, so the two clocks agree trivially
    private const long Base = 5_000_000_000;    // an arbitrary QPC origin

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        double seconds = 6;
        var mono = new float[(int)(Rate * seconds)];

        var placed = new List<(string line, double heardS)>();
        void Place(float[] stereo44k, double atS, string sound, string? detail, string? recipe, double askedBeforeMs, double leadMs = 0)
        {
            var m = Resample(Dsp.ToMono(stereo44k, 2), EarconPlayer.MixerSampleRate, Rate);
            int at = (int)(atS * Rate);
            for (int i = 0; i < m.Length && at + i < mono.Length; i++) mono[at + i] += m[i];
            long bufferQpc = Base + (long)(atS * Freq);
            long asked = bufferQpc - (long)(askedBeforeMs * 10_000);
            long mix = bufferQpc - 200 * 10_000;     // mixed 200 ms before it sounded, as two 100 ms buffers would
            string line = $"{(atS * 1000):0} [T9] {SoundTiming.LinePrefix} v=1 sound={sound}"
                + (detail != null ? " detail=" + detail.Replace(' ', ';') : "")
                + (recipe != null ? " recipe=" + recipe : "")
                + $" qpcAsked={asked} qpcMix={mix} qpcFreq={Freq} source=rendered synth=0.4 synthSteps=1"
                + $" asked2handoff=0.1 handoff2mix={(askedBeforeMs - 200 - 0.1).ToString("0.0", CultureInfo.InvariantCulture)} queued=200"
                + $" asked2out={askedBeforeMs.ToString("0.0", CultureInfo.InvariantCulture)} mixRead=0.01 mixReadMax=0.01 reads=2 inputs=1 samples=1 buffer=100x2";
            int onset = Dsp.FirstAbove(m, 0.05);
            placed.Add((line, atS + onset / (double)Rate + leadMs / 1000));
        }

        Place(EarconPlayer.RenderOffline(EarconPlayer.TxStartTone), 1.000, "TxStartTone", null, null, 230);
        var cd = Radios.CountdownSettings.Current;
        Place(EarconPlayer.RenderOffline(() => EarconPlayer.CountdownBeatTone(cd.PipHz, cd.PipMs, false, cd.PipSound)),
            2.000, "CountdownTransmitTone", "beat=count sound=" + cd.PipSound,
            $"beat:hz={cd.PipHz},ms={cd.PipMs},sound={cd.PipSound}", 260);
        Place(EarconPlayer.RenderOffline(() => EarconPlayer.ConnectPhaseTone(2)), 3.000, "ConnectPhaseTone", "count=2 pitchHz=750", null, 210);
        var dit = new CwToneSampleProvider(EarconPlayer.MixerSampleRate, 700, 60, 5, 0.5f, null);
        var ditBuf = new List<float>();
        var b = new float[1024];
        int n;
        while ((n = dit.Read(b)) > 0) for (int i = 0; i < n; i++) { ditBuf.Add(b[i]); ditBuf.Add(b[i]); }
        Place(ditBuf.ToArray(), 4.000, "Cw", "elements=1 ms=60", "cw:hz=700,mark=60,lead=0,rf=5,voice=Sine", 250);
        Place(EarconPlayer.RenderOffline(EarconPlayer.DialogOpenTone), 5.000, "DialogOpenTone", null, null, 220);

        // A hole in the connect tones: 30 ms of silence starting 40 ms in.
        for (int i = (int)(3.040 * Rate); i < (int)(3.070 * Rate); i++) mono[i] = 0;
        // A "spoken" burst 120 ms after the pip: band noise from 300 Hz up.
        var rng = new Random(855);
        var hp = Dsp.Biquad.HighPass(Rate, 500);
        for (int i = (int)(2.120 * Rate); i < (int)(2.520 * Rate); i++) mono[i] += (float)(hp.Run(rng.NextDouble() * 2 - 1) * 0.3);
        // A "number" spoken 150 ms BEFORE the dialog tone and carrying on
        // through it: the countdown's real geometry.
        for (int i = (int)(4.850 * Rate); i < (int)(5.250 * Rate); i++) mono[i] += (float)(hp.Run(rng.NextDouble() * 2 - 1) * 0.3);
        // A click at 4.5 s.
        mono[(int)(4.5 * Rate)] += 0.6f;

        using (var w = new WaveFileWriter(Path.Combine(dir, "capture.wav"), WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
        {
            var st = new float[mono.Length * 2];
            for (int i = 0; i < mono.Length; i++) { st[2 * i] = mono[i]; st[2 * i + 1] = mono[i]; }
            w.WriteSamples(st, 0, st.Length);
        }
        using (var p = new StreamWriter(Path.Combine(dir, "capture.packets.tsv")))
        {
            p.WriteLine("# fileFrame\tframes\tdevicePosition\tqpc100ns\tflags");
            for (int f = 0; f < mono.Length; f += 480)
            {
                int frames = Math.Min(480, mono.Length - f);
                long jitter = (f / 480 % 3 - 1) * 300; // +-30 us of stamp jitter
                p.WriteLine($"{f}\t{frames}\t{f}\t{Base + (long)(f * (double)Freq / Rate) + jitter}\t0");
            }
        }
        string trace = Path.Combine(dir, "trace.txt");
        File.WriteAllLines(trace, placed.Select(x => x.line));

        var sink = new StringWriter();
        Analyzer.Run(dir, new[] { trace }, 1500, sink);
        var tsv = File.ReadAllLines(Path.Combine(dir, "events.tsv")).Skip(1).Select(l => l.Split('\t')).ToList();

        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
        Check(tsv.Count == placed.Count, $"{placed.Count} sounds placed, {tsv.Count} analysed");
        for (int i = 0; i < Math.Min(tsv.Count, placed.Count); i++)
        {
            var row = tsv[i];
            double heard = row[2].Length > 0 ? double.Parse(row[2], CultureInfo.InvariantCulture) : double.NaN;
            double err = (heard - placed[i].heardS) * 1000;
            Check(row[8] == "template" && Math.Abs(err) < 1.0,
                $"{row[0]} found by {row[8]} at {heard:0.0000} s, placed at {placed[i].heardS:0.0000} s ({err:+0.00;-0.00} ms)");
        }
        Check(tsv.Count > 2 && tsv[2][10] != "0", "the hole cut in the connect tones is reported as a dropout");
        Check(tsv.Where((r, i) => i != 2).All(r => r[10] == "0"), "no other sound reports a dropout");
        string text = sink.ToString();
        var sp = System.Text.RegularExpressions.Regex.Match(text, @"speech at (\d+\.\d+) s: CountdownTransmitTone pip");
        double spAt = sp.Success ? double.Parse(sp.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        Check(Math.Abs(spAt - 2.120) <= 0.005,
            $"the burst 120 ms after the pip, spoken over its tail, is found as speech at {spAt:0.000} s (placed 2.120)");
        var sp2 = System.Text.RegularExpressions.Regex.Match(text, @"speech at 4\.\d+ s: DialogOpenTone at \d+\.\d+ s, speech minus sound ([+-]\d+) ms");
        int lead = sp2.Success ? int.Parse(sp2.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        Check(sp2.Success && Math.Abs(lead - (4850 - 5001)) <= 5,
            $"the voice 150 ms ahead of the dialog tone is found ahead of it ({(sp2.Success ? lead.ToString(CultureInfo.InvariantCulture) : "not found")} ms)");
        Check(text.Contains("click at 4.50", StringComparison.Ordinal), "the click at 4.5 s is found");
        Console.WriteLine(failures == 0 ? "selftest passed" : $"selftest FAILED: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static float[] Resample(float[] x, int from, int to)
    {
        int n = (int)((long)x.Length * to / from);
        var o = new float[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double p = i * step;
            int j = (int)p;
            double f = p - j;
            float a = x[Math.Min(j, x.Length - 1)], c = x[Math.Min(j + 1, x.Length - 1)];
            o[i] = (float)(a + (c - a) * f);
        }
        return o;
    }
}
