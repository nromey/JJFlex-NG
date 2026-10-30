using System.IO;
using System.Globalization;
using NAudio.Wave;

namespace SoundTimingTool;

/// <summary>
/// A loopback recording placed on the QPC time line: every packet where the
/// engine says it was, gaps as silence, resampled to the analysis rate.
/// </summary>
internal sealed class Capture
{
    /// <summary>Mono, at <see cref="Dsp.AnalysisRate"/>, starting at <see cref="StartQpc100ns"/>.</summary>
    public float[] Samples { get; private init; } = Array.Empty<float>();

    /// <summary>The same, high-passed at 4 kHz before resampling would lose it, for click detection: full rate.</summary>
    public float[] FullRate { get; private init; } = Array.Empty<float>();
    public int FullRateHz { get; private init; }

    public long StartQpc100ns { get; private init; }
    public long EndQpc100ns => StartQpc100ns + (long)(Samples.Length * (10_000_000.0 / Dsp.AnalysisRate));

    /// <summary>Gaps in the packet stream longer than 5 ms: (start, length ms) on the QPC line.</summary>
    public List<(long qpc100ns, double ms)> Gaps { get; } = new();

    /// <summary>Packets the engine flagged as a discontinuity (a glitch on the capture side or the engine's).</summary>
    public List<long> Discontinuities { get; } = new();

    public int SourceRate { get; private init; }
    public int Channels { get; private init; }

    public long IndexOf(long qpc100ns) => (long)Math.Round((qpc100ns - StartQpc100ns) * Dsp.AnalysisRate / 10_000_000.0);
    public long QpcOf(long index) => StartQpc100ns + (long)Math.Round(index * 10_000_000.0 / Dsp.AnalysisRate);

    public static Capture Load(string dir)
    {
        string wav = Path.Combine(dir, "capture.wav");
        string tsv = Path.Combine(dir, "capture.packets.tsv");
        using var reader = new WaveFileReader(wav);
        int rate = reader.WaveFormat.SampleRate, ch = reader.WaveFormat.Channels;
        var sp = reader.ToSampleProvider();
        var all = new List<float>((int)(reader.Length / 4));
        var buf = new float[rate * ch];
        int n;
        while ((n = sp.Read(buf.AsSpan())) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());
        var mono = Dsp.ToMono(all.ToArray(), ch);

        var packets = new List<(long fileFrame, int frames, long qpc, int flags)>();
        foreach (var line in File.ReadLines(tsv))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var f = line.Split('\t');
            packets.Add((long.Parse(f[0], CultureInfo.InvariantCulture), int.Parse(f[1], CultureInfo.InvariantCulture),
                long.Parse(f[3], CultureInfo.InvariantCulture), int.Parse(f[4], CultureInfo.InvariantCulture)));
        }
        if (packets.Count == 0) throw new InvalidOperationException("the recording has no packets");

        long t0 = packets[0].qpc;
        var gaps = new List<(long, double)>();
        var disc = new List<long>();
        long last = packets[^1].qpc - t0;
        int length = (int)(last * rate / 10_000_000) + packets[^1].frames + 1;
        var placed = new float[length];
        long cursor = 0;
        foreach (var p in packets)
        {
            long at = (long)Math.Round((p.qpc - t0) * rate / 10_000_000.0);
            // Within a couple of milliseconds of where the previous packet
            // ended, it is contiguous and the stamp's jitter is ignored; past
            // that, the stream really stopped and the gap is silence.
            long slack = rate / 500;
            if (Math.Abs(at - cursor) <= slack) at = cursor;
            else if (at > cursor) gaps.Add((t0 + (long)(cursor * 10_000_000.0 / rate), (at - cursor) * 1000.0 / rate));
            if ((p.flags & 1) != 0) disc.Add(p.qpc);
            for (int i = 0; i < p.frames; i++)
            {
                long k = at + i, src = p.fileFrame + i;
                if (k >= 0 && k < placed.Length && src < mono.Length) placed[k] = mono[src];
            }
            cursor = at + p.frames;
        }

        var c = new Capture
        {
            Samples = Dsp.ToAnalysisRate(placed, rate),
            FullRate = placed,
            FullRateHz = rate,
            StartQpc100ns = t0,
            SourceRate = rate,
            Channels = ch,
        };
        c.Gaps.AddRange(gaps.Where(g => g.Item2 > 5));
        c.Discontinuities.AddRange(disc);
        return c;
    }
}
