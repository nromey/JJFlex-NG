using System.IO;
using System.Globalization;
using JJFlexWpf;
using NAudio.Wave;

namespace SoundTimingTool;

/// <summary>
/// A sound's matched detector: the sound itself, rendered by the app's own
/// code from the recipe its trace line names, at the analysis rate.
/// </summary>
internal sealed class Template
{
    /// <summary>The whole sound, mono, at <see cref="Dsp.AnalysisRate"/>.</summary>
    public float[] Whole = Array.Empty<float>();

    /// <summary>Where the sound first becomes audible within <see cref="Whole"/> (leading silence skipped).</summary>
    public int OnsetIndex;

    /// <summary>The part the detector slides: from the onset, up to 500 ms.</summary>
    public float[] Head = Array.Empty<float>();

    /// <summary>Extra lead the sound starts with on purpose (CW's leading space), in ms, added back to "heard".</summary>
    public double LeadMs;

    /// <summary>What it was built from, for the report.</summary>
    public string Origin = "";
}

internal static class Templates
{
    private static readonly Dictionary<string, Template?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// The detector for one timed sound, or null when the sound has no fixed
    /// shape to match (typing sounds pick a random key; unnamed sounds have no
    /// recipe). The analyser then falls back to an energy onset.
    /// </summary>
    public static Template? For(SoundTimingLine line)
    {
        string key = line.Sound + "|" + (line.Recipe ?? "") + "|" + (line.Detail ?? "");
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var t)) return t;
            t = Build(line);
            _cache[key] = t;
            return t;
        }
    }

    private static Template? Build(SoundTimingLine line)
    {
        string? recipe = line.Recipe;
        if (recipe != null && recipe.StartsWith("beat:", StringComparison.Ordinal))
        {
            var a = Args(recipe.Substring(5));
            int hz = Int(a, "hz", 300), ms = Int(a, "ms", 100);
            string sound = a.TryGetValue("sound", out var s) ? s : "Hollow";
            bool landing = line.Detail?.Contains("beat=landing", StringComparison.Ordinal) == true;
            return Make(EarconPlayer.RenderOffline(() => EarconPlayer.CountdownBeatTone(hz, ms, landing, sound)),
                $"countdown beat {hz} Hz {ms} ms {sound}");
        }
        if (recipe != null && recipe.StartsWith("cw:", StringComparison.Ordinal))
        {
            var a = Args(recipe.Substring(3));
            int hz = Int(a, "hz", 700), mark = Int(a, "mark", 60), lead = Int(a, "lead", 0), rf = Int(a, "rf", 5);
            string voiceName = a.TryGetValue("voice", out var v) ? v.Replace('_', ' ') : "Sine";
            var voice = EarconVoices.CwWaveforms.Select(w => w.Voice).FirstOrDefault(m => m?.Name == voiceName);
            if (mark <= 0) return null;
            var tone = new CwToneSampleProvider(EarconPlayer.MixerSampleRate, hz, mark, rf, 0.5f, voice);
            var mono = ReadAll(tone);
            var t = Make(Stereo(mono), $"CW first mark {mark} ms at {hz} Hz ({voiceName})");
            if (t != null) t.LeadMs = lead;
            return t;
        }

        switch (line.Sound)
        {
            case "ConnectPhaseTone":
            {
                int count = DetailInt(line, "count", 2);
                return Make(EarconPlayer.RenderOffline(() => EarconPlayer.ConnectPhaseTone(count)), $"connect count {count}");
            }
            case "CutHoldEndedTone":
            {
                bool vox = line.Detail?.Contains("voxRestored=True", StringComparison.OrdinalIgnoreCase) == true;
                int delay = DetailInt(line, "delayMs", 0);
                return Make(EarconPlayer.RenderOffline(() => EarconPlayer.CutHoldEndedTone(vox, delay)), "cut-hold cue");
            }
            case "Beep":
            {
                int hz = DetailInt(line, "hz", 800), ms = DetailInt(line, "ms", 150);
                return Make(EarconPlayer.RenderOffline(() => EarconPlayer.Beep(hz, ms)), $"beep {hz} Hz {ms} ms");
            }
            case "Chirp":
            {
                int from = DetailInt(line, "from", 400), to = DetailInt(line, "to", 800), ms = DetailInt(line, "ms", 100);
                return Make(EarconPlayer.RenderOffline(() => EarconPlayer.Chirp(from, to, ms)), $"chirp {from}-{to} Hz");
            }
            case "RecorderStartBeep":
                return Make(EarconPlayer.RenderOffline(EarconPlayer.RecorderStartBeep), "recorder start");
            case "RecorderStopBeep":
                return Make(EarconPlayer.RenderOffline(EarconPlayer.RecorderStopBeep), "recorder stop");
        }

        var entry = EarconCatalog.Find(line.Sound);
        if (entry != null && !entry.IsContinuous)
            return Make(EarconPlayer.RenderOffline(entry.Play), "catalog " + entry.Id);
        return null;
    }

    private static Template? Make(float[] stereo44k, string origin)
    {
        if (stereo44k.Length == 0) return null;
        var mono = Dsp.ToMono(stereo44k, 2);
        var at = Dsp.ToAnalysisRate(mono, EarconPlayer.MixerSampleRate);
        int onset = Dsp.FirstAbove(at, 0.05);
        if (onset < 0) return null;
        // Up to half a second from the onset: long enough to carry the
        // sound's rhythm, so a figure of repeated identical notes cannot match
        // its own second note, and a hole in one note cannot hand the match
        // to the next.
        int headLen = Math.Min(Dsp.AnalysisRate / 2, at.Length - onset);
        if (headLen < Dsp.AnalysisRate / 200) return null;
        return new Template
        {
            Whole = at,
            OnsetIndex = onset,
            Head = at.AsSpan(onset, headLen).ToArray(),
            Origin = origin,
        };
    }

    private static float[] ReadAll(ISampleProvider p)
    {
        var all = new List<float>();
        var buf = new float[4410];
        int n;
        while ((n = p.Read(buf.AsSpan())) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());
        return all.ToArray();
    }

    private static float[] Stereo(float[] mono)
    {
        var s = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) { s[2 * i] = mono[i]; s[2 * i + 1] = mono[i]; }
        return s;
    }

    private static Dictionary<string, string> Args(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) d[part.Substring(0, eq)] = part.Substring(eq + 1);
        }
        return d;
    }

    private static int Int(Dictionary<string, string> a, string k, int dflt) =>
        a.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : dflt;

    private static int DetailInt(SoundTimingLine line, string key, int dflt)
    {
        if (line.Detail == null) return dflt;
        foreach (var tok in line.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (tok.StartsWith(key + "=", StringComparison.Ordinal)
                && int.TryParse(tok.AsSpan(key.Length + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
        return dflt;
    }
}
