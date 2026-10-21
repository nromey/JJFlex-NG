using System;
using System.Globalization;
using System.IO;
using System.Linq;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>Measures only keyed audio outside recorded IQ gaps.</summary>
public sealed record TakeStatistics(bool Available, string Reason = "", double Lufs = 0,
    double PeakDbfs = 0, double RmsDbfs = 0, double BrightnessDb = 0,
    double PunchDb = 0, double SplatterDbfs = 0)
{
    public static TakeStatistics Measure(string directory, bool reuseRender = false)
    {
        var take = TakeReader.Inspect(directory);
        if (!take.Playable) return new(false, "take is not playable");
        if (take.Get("mode") is not ("USB" or "LSB")) return new(false, "this mode has no speech render");
        // The Workshop has just rendered the current take for playback and
        // earlier takes already have a render. Rewriting either file here
        // delays playback and can race a reader using heard.wav.
        if (!reuseRender || !File.Exists(Path.Combine(directory, "heard.wav")))
            SsbTakeRenderer.Render(directory);
        var (rate, heard) = TakeAudio.ReadMono(Path.Combine(directory, "heard.wav"));
        var spans = TakeTimeline.WithoutGaps(TakeTimeline.KeyedSpans(take, heard.Length), TakeTimeline.Gaps(take));
        var selected = spans.SelectMany(span => heard.Skip((int)Math.Clamp(span.Start, 0, heard.Length))
            .Take((int)Math.Clamp(span.Length, 0, heard.Length - Math.Clamp(span.Start, 0, heard.Length)))).ToArray();
        if (selected.Length < rate / 4) return new(false, "too little keyed, gap-free audio");
        double low = double.Parse(take.Get("tx-filter-low"), CultureInfo.InvariantCulture);
        double high = double.Parse(take.Get("tx-filter-high"), CultureInfo.InvariantCulture);
        var shape = AudioDifferential.Measure(selected, rate, low, high);
        var shapes = new[] { shape }.Concat(Enumerable.Range(1, 3).Select(part =>
            AudioDifferential.Measure(selected.AsSpan(part * Math.Max(0, selected.Length - 4096) / 3),
                rate, low, high))).ToArray();
        double brightness = shapes.Average(s => s.TiltDb);
        double splatter = 10 * Math.Log10(shapes.Average(s => Math.Pow(10, s.StopbandDb / 10)));
        var loudness = new LufsMeter();
        const int frames = 4096;
        for (int start = 0; start < selected.Length; start += frames)
        {
            int count = Math.Min(frames, selected.Length - start);
            var stereo = new float[count * 2];
            for (int i = 0; i < count; i++) stereo[2 * i] = stereo[2 * i + 1] = selected[start + i];
            loudness.Process(stereo, stereo.Length, (uint)rate);
        }
        return new(true, "", loudness.IntegratedLufs, shape.PeakDb, shape.RmsDb,
            brightness, shape.CrestDb, splatter);
    }
}
