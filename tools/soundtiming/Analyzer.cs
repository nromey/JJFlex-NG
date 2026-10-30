using System.IO;
using System.Globalization;
using System.Text;
using JJFlexWpf;

namespace SoundTimingTool;

/// <summary>
/// Pairs a loopback recording with the app's SoundTiming lines: finds each
/// sound where it was really heard, splits its lag into the app's stages,
/// finds speech onsets around the sounds, and flags dropouts and clicks.
/// </summary>
internal static class Analyzer
{
    private sealed class Event
    {
        public SoundTimingLine Line = null!;
        public string Family = "";
        public string Method = "missed";
        public double Score;
        public long HeardQpc;            // audible onset, 100 ns
        public long BufferStartIndex = -1;
        public Template? Template;
        public double Gain;
        public double AskedToHeardMs = double.NaN;
        public double PredictedMs = double.NaN;
        public List<(double atMs, double lengthMs)> Dropouts = new();
    }

    public static int Run(string captureDir, IReadOnlyList<string> traces, double windowMs, TextWriter console)
    {
        var cap = Capture.Load(captureDir);
        var lines = new List<SoundTimingLine>();
        foreach (var path in traces)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            string? l;
            while ((l = r.ReadLine()) != null)
                if (SoundTimingLine.TryParse(l, out var p) && p!.QpcFrequency > 0) lines.Add(p);
        }

        var inside = lines
            .Where(l => l.MixHundredNs >= cap.StartQpc100ns && l.MixHundredNs < cap.EndQpc100ns - 2_000_000)
            .OrderBy(l => l.AskedHundredNs)
            .ToList();

        var events = new List<Event>();
        foreach (var line in inside)
        {
            var e = new Event { Line = line, Family = FamilyOf(line) };
            Locate(cap, e, windowMs);
            events.Add(e);
        }

        // Speech: the recording's energy in excess of every located sound's
        // own (in energy, not samples, so a fraction of a sample's
        // misalignment cannot leave a tone behind looking like a voice), so a
        // number spoken over a pip is still found.
        var sounds = new float[cap.Samples.Length];
        foreach (var e in events.Where(e => e.Template != null && e.BufferStartIndex >= 0))
        {
            var w = e.Template!.Whole;
            for (int i = 0; i < w.Length; i++)
            {
                long k = e.BufferStartIndex + i;
                if (k >= 0 && k < sounds.Length) sounds[k] += (float)(e.Gain * w[i]);
            }
        }
        var speech = SpeechOnsets(cap.Samples, sounds);
        // A hole's edges are broadband steps, which read as a voice starting;
        // the hole is already reported as a dropout, so they are not speech.
        var holes = events.SelectMany(e => e.Dropouts.Select(d =>
            (from: cap.IndexOf(e.HeardQpc) + (long)((d.atMs - 40) * Dsp.AnalysisRate / 1000),
             to: cap.IndexOf(e.HeardQpc) + (long)((d.atMs + d.lengthMs + 40) * Dsp.AnalysisRate / 1000)))).ToList();
        speech.RemoveAll(s => holes.Any(h => s >= h.from && s <= h.to));
        var clicks = Clicks(cap);

        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); console.WriteLine(s); }
        double Rel(long qpc) => (qpc - cap.StartQpc100ns) / 10_000.0;
        var c = CultureInfo.InvariantCulture;

        W($"Sound timing analysis of {captureDir}");
        W($"Recording: {cap.Samples.Length / (double)Dsp.AnalysisRate:0.0} s, recorded at {cap.SourceRate} Hz, {cap.Channels} channels.");
        W($"Trace lines read: {lines.Count}; inside the recording: {inside.Count}.");
        W($"Gaps in the engine's stream longer than 5 ms: {cap.Gaps.Count}"
          + (cap.Gaps.Count > 0 ? " (" + string.Join(", ", cap.Gaps.Take(10).Select(g => $"{Rel(g.qpc100ns) / 1000:0.00} s for {g.ms:0} ms")) + ")" : "")
          + $". Packets the engine flagged as a discontinuity: {cap.Discontinuities.Count}.");
        W("");
        W("Each sound, in order. Times are from the start of the recording. Asked to heard is from the moment the app");
        W("asked for the sound to the moment it was heard at the audio engine's output; predicted is the app's own");
        W("estimate of the same thing; synth is time spent making the samples; queued is the audio already waiting in");
        W("the output device when the sound was mixed.");
        foreach (var e in events)
        {
            var l = e.Line;
            string heard = e.Method == "missed" ? "not found" :
                $"heard at {Rel(e.HeardQpc) / 1000:0.000} s, asked to heard {e.AskedToHeardMs:0.0} ms (predicted {e.PredictedMs:0.0}), found by {e.Method} {e.Score:0.00}";
            W($"- {e.Family}: asked at {Rel(l.AskedHundredNs) / 1000:0.000} s; {heard}; synth {l.SynthMs:0.00} ms ({l.Source}); "
              + $"asked to hand-off {l.AskedToHandoffMs:0.00}, hand-off to mix {l.HandoffToMixMs:0.0}, queued {Fmt(l.QueuedMs)} ms; "
              + $"mix reads {l.Reads}, slowest {l.MixReadMaxMs:0.000} ms"
              + (e.Dropouts.Count > 0 ? $"; DROPOUT {string.Join(", ", e.Dropouts.Select(d => $"{d.lengthMs:0} ms at +{d.atMs:0}"))}" : ""));
        }

        W("");
        W("By sound. Median, 90th percentile and worst asked to heard, then jitter as the spread between the fastest and slowest.");
        foreach (var g in events.GroupBy(e => e.Family).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var found = g.Where(e => e.Method != "missed").ToList();
            var a2h = found.Select(e => e.AskedToHeardMs).ToList();
            var resid = found.Select(e => e.AskedToHeardMs - e.PredictedMs).ToList();
            W($"- {g.Key}: {g.Count()} played, {found.Count} found. Asked to heard: median {Fmt(Dsp.Median(a2h))}, "
              + $"90th {Fmt(Dsp.Percentile(a2h, 0.9))}, worst {Fmt(a2h.DefaultIfEmpty(double.NaN).Max())} ms; jitter "
              + $"{Fmt(a2h.Count > 0 ? a2h.Max() - a2h.Min() : double.NaN)} ms range, {Fmt(Dsp.StdDev(a2h))} ms deviation. "
              + $"Synth median {Fmt(Dsp.Median(g.Select(e => e.Line.SynthMs)))} ms, worst {Fmt(g.Max(e => e.Line.SynthMs))}. "
              + $"Queued median {Fmt(Dsp.Median(g.Select(e => e.Line.QueuedMs)))} ms. "
              + $"Hand-off to mix median {Fmt(Dsp.Median(g.Select(e => e.Line.HandoffToMixMs)))} ms. "
              + $"Heard minus predicted median {Fmt(Dsp.Median(resid))} ms (the engine's own share, outside the app).");
        }

        W("");
        W($"Speech onsets found: {speech.Count}. Each with the nearest sound within 700 ms; negative means the speech was heard first.");
        var located = events.Where(e => e.Method != "missed").ToList();
        foreach (var s in speech)
        {
            long sq = cap.QpcOf(s);
            var near = located.OrderBy(e => Math.Abs(e.HeardQpc - sq)).FirstOrDefault();
            if (near == null || Math.Abs(near.HeardQpc - sq) > 7_000_000)
                W($"- speech at {Rel(sq) / 1000:0.000} s: no sound within 700 ms");
            else
                W($"- speech at {Rel(sq) / 1000:0.000} s: {near.Family} at {Rel(near.HeardQpc) / 1000:0.000} s, speech minus sound {(sq - near.HeardQpc) / 10_000.0:+0;-0} ms");
        }

        W("");
        W($"Clicks (sudden steps the waveform does not otherwise make; a heuristic, so listen before believing one): {clicks.Count}.");
        foreach (var k in clicks.Take(50))
        {
            var inSound = located.FirstOrDefault(e => e.Template != null && k >= e.HeardQpc - 50_000
                && k <= e.HeardQpc + (long)(e.Template.Whole.Length * 10_000_000.0 / Dsp.AnalysisRate));
            W($"- click at {Rel(k) / 1000:0.000} s" + (inSound != null ? $", during {inSound.Family}" : ""));
        }

        string report = Path.Combine(captureDir, "analysis.txt");
        File.WriteAllText(report, sb.ToString());
        var tsv = new StringBuilder("family\taskedS\theardS\tasked2heardMs\tpredictedMs\tsynthMs\tqueuedMs\thandoff2mixMs\tmethod\tscore\tdropouts\n");
        foreach (var e in events)
            tsv.AppendLine(string.Join('\t', e.Family, (Rel(e.Line.AskedHundredNs) / 1000).ToString("0.0000", c),
                e.Method == "missed" ? "" : (Rel(e.HeardQpc) / 1000).ToString("0.0000", c),
                e.AskedToHeardMs.ToString("0.00", c), e.PredictedMs.ToString("0.00", c), e.Line.SynthMs.ToString("0.000", c),
                e.Line.QueuedMs.ToString("0.0", c), e.Line.HandoffToMixMs.ToString("0.0", c), e.Method,
                e.Score.ToString("0.00", c), e.Dropouts.Count.ToString(c)));
        File.WriteAllText(Path.Combine(captureDir, "events.tsv"), tsv.ToString());
        console.WriteLine($"wrote {report}");
        return 0;
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The name a sound is grouped under: its id, and for a countdown beat which beat.</summary>
    private static string FamilyOf(SoundTimingLine l)
    {
        string d = l.Detail ?? "";
        if (d.Contains("beat=landing", StringComparison.Ordinal)) return l.Sound + " landing";
        if (d.Contains("beat=count", StringComparison.Ordinal)) return l.Sound + " pip";
        if (l.Sound == "ConnectPhaseTone")
        {
            var m = d.Split(' ').FirstOrDefault(t => t.StartsWith("count=", StringComparison.Ordinal));
            if (m != null) return l.Sound + " " + m;
        }
        return l.Sound;
    }

    private static void Locate(Capture cap, Event e, double windowMs)
    {
        var l = e.Line;
        long from = Math.Max(cap.IndexOf(l.MixHundredNs) - Dsp.AnalysisRate / 200, 0);
        long to = Math.Min(cap.IndexOf(l.MixHundredNs + (long)(windowMs * 10_000)), cap.Samples.Length - 1);
        if (to <= from) return;

        var t = Templates.For(l);
        e.Template = t;
        double tick = 10_000_000.0 / Dsp.AnalysisRate;
        if (t != null)
        {
            var ncc = Dsp.Ncc(cap.Samples, (int)from, (int)to, t.Head);
            if (ncc.Length > 0)
            {
                double max = ncc.Max();
                if (max >= 0.5)
                {
                    // The highest peak, unless a separate occurrence nearly as
                    // strong comes earlier (a pip repeated a second later can
                    // score a hair higher than the one asked for). "Separate"
                    // matters: a tone correlates with itself one period off
                    // almost as well as at zero, so a near-equal peak within a
                    // few milliseconds is the same sound, not an earlier one.
                    int best = Array.IndexOf(ncc, max);
                    int sep = Dsp.AnalysisRate / 20;   // 50 ms
                    for (int i = 0; i < best - sep; i++)
                    {
                        if (ncc[i] < 0.95 * max) continue;
                        int j = i;
                        int end = Math.Min(ncc.Length, i + sep);
                        for (int k = i; k < end; k++) if (ncc[k] > ncc[j]) j = k;
                        best = j;
                        break;
                    }
                    long onset = from + best;
                    e.Method = "template";
                    e.Score = ncc[best];
                    e.HeardQpc = cap.QpcOf(onset);
                    e.BufferStartIndex = onset - t.OnsetIndex;
                    e.Gain = LeastSquaresGain(cap.Samples, e.BufferStartIndex, t.Whole);
                    e.Dropouts = Dropouts(cap.Samples, e.BufferStartIndex, t.Whole, e.Gain);
                }
            }
        }
        if (e.Method == "missed")
        {
            long onset = EnergyOnset(cap.Samples, from, to);
            if (onset >= 0)
            {
                e.Method = "energy";
                e.Score = 0;
                e.HeardQpc = cap.QpcOf(onset);
            }
        }
        if (e.Method != "missed")
        {
            e.AskedToHeardMs = (e.HeardQpc - l.AskedHundredNs) / 10_000.0;
            double onsetMs = t != null ? t.OnsetIndex * 1000.0 / Dsp.AnalysisRate + t.LeadMs : 0;
            if (!double.IsNaN(l.AskedToOutMs)) e.PredictedMs = l.AskedToOutMs + onsetMs;
        }
    }

    private static double LeastSquaresGain(float[] x, long start, float[] t)
    {
        double xt = 0, tt = 0;
        for (int i = 0; i < t.Length; i++)
        {
            long k = start + i;
            if (k < 0 || k >= x.Length) continue;
            xt += (double)x[k] * t[i];
            tt += (double)t[i] * t[i];
        }
        return tt > 0 ? xt / tt : 0;
    }

    /// <summary>
    /// 5 ms windows where the sound should be loud (within 20 dB of its peak)
    /// but the recording holds under a third of it. Speech on top only adds
    /// energy, so it cannot hide a hole.
    /// </summary>
    private static List<(double, double)> Dropouts(float[] x, long start, float[] t, double gain)
    {
        var res = new List<(double, double)>();
        if (gain <= 0) return res;
        int win = Dsp.AnalysisRate / 200;
        var tr = Dsp.Rms(t, 0, t.Length, win);
        var xr = Dsp.Rms(x, (int)start, t.Length, win);
        double peak = tr.DefaultIfEmpty(0).Max();
        int runStart = -1;
        for (int f = 0; f <= tr.Length; f++)
        {
            bool hole = f < tr.Length && tr[f] >= peak * 0.1 && xr[f] < tr[f] * gain / 3;
            if (hole && runStart < 0) runStart = f;
            if (!hole && runStart >= 0)
            {
                res.Add((runStart * 5.0, (f - runStart) * 5.0));
                runStart = -1;
            }
        }
        return res;
    }

    /// <summary>First 5 ms frame 15 dB above the 50 ms before the window, and above -60 dBFS.</summary>
    private static long EnergyOnset(float[] x, long from, long to)
    {
        int win = Dsp.AnalysisRate / 200;
        var before = Dsp.Rms(x, (int)Math.Max(from - Dsp.AnalysisRate / 20, 0), Dsp.AnalysisRate / 20, win);
        double floor = Dsp.Db(Dsp.Median(before.Select(v => v)));
        var r = Dsp.Rms(x, (int)from, (int)(to - from), win);
        for (int f = 0; f < r.Length; f++)
        {
            double db = Dsp.Db(r[f]);
            if (db > floor + 15 && db > -60) return from + (long)f * win;
        }
        return -1;
    }

    /// <summary>
    /// Speech onsets: the 300 Hz high-passed level rising past a threshold set
    /// from the recording itself, after 200 ms below it, and staying up for
    /// most of the next 50 ms.
    /// </summary>
    private static List<long> SpeechOnsets(float[] recording, float[] sounds)
    {
        // Per frequency, not per frame: a sound is a handful of harmonics and
        // a voice is spread across the band, so subtracting the located
        // sounds' own spectrum (with a margin, since the rendered and the
        // recorded sound differ a little) leaves a voice standing even when it
        // is quieter than the pip it overlaps. Above 300 Hz only.
        int hop = Dsp.AnalysisRate / 500, size = 512;   // 2 ms steps, 32 ms window
        int frames = Math.Max((recording.Length - size) / hop, 0);
        var window = new double[size];
        for (int i = 0; i < size; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1));
        int lowBin = 300 * size / Dsp.AnalysisRate;
        var re1 = new double[size]; var im1 = new double[size];
        var re2 = new double[size]; var im2 = new double[size];
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            int o = f * hop;
            bool anySound = false;
            for (int i = 0; i < size; i++)
            {
                re1[i] = recording[o + i] * window[i]; im1[i] = 0;
                re2[i] = sounds[o + i] * window[i]; im2[i] = 0;
                if (sounds[o + i] != 0) anySound = true;
            }
            Dsp.Fft(re1, im1);
            if (anySound) Dsp.Fft(re2, im2);
            double excess = 0;
            for (int b = lowBin; b < size / 2; b++)
            {
                double x = re1[b] * re1[b] + im1[b] * im1[b];
                double sn = anySound ? re2[b] * re2[b] + im2[b] * im2[b] : 0;
                excess += Math.Max(x - 2.0 * sn, 0);
            }
            // Digital silence reads as -120 dB, so a quiet machine's floor is
            // a floor and not minus infinity.
            db[f] = Math.Max(10 * Math.Log10(excess / (size * size) + 1e-24), -120);
        }
        if (db.Length == 0) return new List<long>();
        double floor = Dsp.Percentile(db, 0.10), peak = Dsp.Percentile(db, 0.995);
        double thr = Math.Max(floor + 15, peak - 40);
        int win = hop;
        var res = new List<long>();
        // After 200 ms below the threshold, above it for most of the next 50 ms.
        int quietFrames = 200 / 2, holdFrames = 50 / 2;
        int quiet = quietFrames;
        for (int i = 0; i < db.Length; i++)
        {
            if (db[i] > thr)
            {
                if (quiet >= quietFrames)
                {
                    int held = 0;
                    for (int k = i; k < Math.Min(i + holdFrames, db.Length); k++) if (db[k] > thr) held++;
                    if (held >= holdFrames * 3 / 5)
                    {
                        // The threshold sits 40 dB under the loudest speech,
                        // so a voice trips it within a couple of milliseconds
                        // of entering the analysis window's leading edge.
                        res.Add((long)i * win + size - Dsp.AnalysisRate / 500);
                    }
                }
                quiet = 0;
            }
            else quiet++;
        }
        return res;
    }

    /// <summary>
    /// Clicks: 2 ms frames whose largest second difference is ten times the
    /// median of the frames around them and large in absolute terms. Earcons
    /// and speech are smooth at the full rate; a dropped buffer or a cut
    /// waveform is not.
    /// </summary>
    private static List<long> Clicks(Capture cap)
    {
        var x = cap.FullRate;
        int rate = cap.FullRateHz, win = Math.Max(rate / 500, 1);
        int frames = x.Length / win;
        var peak = new float[frames];
        var where = new int[frames];
        for (int fr = 0; fr < frames; fr++)
        {
            float m = 0;
            int o = fr * win;
            for (int i = Math.Max(o, 1); i < Math.Min(o + win, x.Length - 1); i++)
            {
                float d = Math.Abs(x[i + 1] - 2 * x[i] + x[i - 1]);
                if (d > m) { m = d; where[fr] = i; }
            }
            peak[fr] = m;
        }
        var res = new List<long>();
        int ctx = 50;
        var window = new List<float>(2 * ctx);
        for (int fr = ctx; fr < frames - ctx; fr++)
        {
            if (peak[fr] < 0.05f) continue;
            window.Clear();
            for (int k = fr - ctx; k < fr + ctx; k++) if (k != fr) window.Add(peak[k]);
            window.Sort();
            float med = window[window.Count / 2];
            if (peak[fr] > 10 * Math.Max(med, 1e-4f))
            {
                long qpc = cap.StartQpc100ns + (long)((double)where[fr] * 10_000_000.0 / rate);
                if (res.Count == 0 || qpc - res[^1] > 200_000) res.Add(qpc);
            }
        }
        return res;
    }
}
