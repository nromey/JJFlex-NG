using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Radios.Listenback;

public sealed record ToneSurvivalResult(bool Identified, string Reason, double OffsetHz = 0, double AboveRestDb = 0);
public sealed record TakeAnalysisResult(AudioAlignmentResult Alignment, AudioDifference Difference, ToneSurvivalResult Tone);

public static class TakeAnalysis
{
    private static double Number(TakeInspection take, string key) => double.Parse(take.Get(key), CultureInfo.InvariantCulture);
    public static TakeAnalysisResult Run(string directory)
    {
        var take = TakeReader.Inspect(directory);
        if (!take.Playable) throw new InvalidDataException(take.Reason);
        var (rate, heard) = TakeAudio.ReadMono(Path.Combine(directory, "heard.wav"));
        var keyedSpans = TakeTimeline.KeyedSpans(take, heard.Length);
        var gaps = TakeTimeline.Gaps(take);
        var spans = TakeTimeline.WithoutGaps(keyedSpans, gaps);
        double low = Number(take, "tx-filter-low"), high = Number(take, "tx-filter-high");
        var tone = CheckTone(directory, take, rate, spans);
        string pending = Path.Combine(directory, "analysis-" + Guid.NewGuid().ToString("N") + ".pending");
        using var analysis = new TakeHeader(pending, "jjflex-audio-analysis-1");
        analysis.Event("tone", tone);
        foreach (var span in spans.Where(s => s.Length >= rate / 20))
            analysis.Event("keyed-span", new { span, Levels = AudioDifferential.Measure(heard.AsSpan((int)span.Start, (int)span.Length), rate, low, high) });
        double? onsetDelta = null;
        if (spans.Count > 0)
        {
            var first = spans[0]; int window = Math.Max(1, rate / 100);
            double rest = 0;
            int restStart = (int)Math.Max(0, first.Start - rate / 2);
            for (int i = restStart; i < first.Start; ++i) rest += heard[i] * (double)heard[i];
            rest /= Math.Max(1, first.Start - restStart);
            for (long at = first.Start; at + window <= first.End; at += window)
            {
                double power = 0;
                for (long j = at; j < at + window; ++j) power += heard[j] * (double)heard[j];
                if (power / window > Math.Max(1e-12, rest * 16))
                { onsetDelta = (at - first.Start) * 1000.0 / rate; analysis.Write("energy-onset-pair", at); break; }
            }
            analysis.Write("energy-onset-from-radio-key-ms", onsetDelta?.ToString("R", CultureInfo.InvariantCulture) ?? "not detected");
            // A release is evidence in its own right, including a delayed tail.
            // Never infer an energy edge from a filled transport gap.
            var lastKey = keyedSpans[^1];
            long releaseStart = Math.Max(lastKey.Start, lastKey.End - rate / 2);
            long releaseEnd = Math.Min(heard.Length, lastKey.End + rate / 2);
            long? release = null;
            if (!gaps.Any(g => g.Start < releaseEnd && g.End > releaseStart))
                for (long at = releaseStart; at + window <= releaseEnd; at += window)
                {
                    double power = 0;
                    for (long j = at; j < at + window; ++j) power += heard[j] * (double)heard[j];
                    if (power / window > Math.Max(1e-12, rest * 16)) release = at + window;
                }
            if (release is { } end && end < releaseEnd)
            {
                analysis.Write("energy-release-pair", end);
                analysis.Write("energy-release-from-radio-unkey-ms", (end - lastKey.End) * 1000.0 / rate);
            }
            else analysis.Write("energy-release-from-radio-unkey-ms", "not detected or gap-interrupted");
        }
        var firstPacket = JsonSerializer.Deserialize<IqPacketHeader>(take.Get("first-packet"));
        var lastPacket = JsonSerializer.Deserialize<IqPacketHeader>(take.Get("last-packet") ?? take.Get("first-packet"));
        if (firstPacket.HasRadioClock && lastPacket.HasRadioClock)
            analysis.Write("radio-versus-arrival-span-ms", 1000 * (lastPacket.SecondsSince(firstPacket) -
                (lastPacket.ArrivalTicks - firstPacket.ArrivalTicks) / (double)Stopwatch.Frequency));
        AudioAlignmentResult alignment = new(false, "sent-unavailable", Array.Empty<SpanAlignment>());
        AudioDifference difference = new(false, "sent-unavailable");
        if (take.Get("sent-status") == "clean" && File.Exists(Path.Combine(directory, "sent.wav")))
        {
            var sentInfo = TakeHeader.Read(Path.Combine(directory, "sent.txt"));
            string sum = sentInfo.LastOrDefault(p => p.Key == "sha256").Value;
            if (sum != TakeHeader.Checksum(Path.Combine(directory, "sent.wav"))) throw new InvalidDataException("sent-checksum");
            var (sentRate, sentRaw) = TakeAudio.ReadMono(Path.Combine(directory, "sent.wav"));
            var sent = TakeAudio.Resample(sentRaw, sentRate, rate);
            long firstSentTicks = long.Parse(take.Get("sent-first-frame-ticks"), CultureInfo.InvariantCulture);
            int offset = (int)Math.Round((firstSentTicks - firstPacket.ArrivalTicks) * (double)rate / Stopwatch.Frequency);
            var reference = new float[heard.Length];
            int from = Math.Max(0, -offset), to = Math.Max(0, offset);
            int n = Math.Min(sent.Length - from, reference.Length - to);
            if (n > 0) Array.Copy(sent, from, reference, to, n);
            var sentGaps = new List<TakeSpan>();
            foreach (var entry in sentInfo.Where(p => p.Key == "gap"))
            {
                using var json = JsonDocument.Parse(entry.Value);
                long at = json.RootElement.GetProperty("AtSample").GetInt64();
                long length = json.RootElement.GetProperty("Samples").GetInt64();
                sentGaps.Add(new(offset + at * rate / sentRate, offset + (at + length) * rate / sentRate));
            }
            spans = TakeTimeline.WithoutGaps(spans, sentGaps);
            reference = TakeAudio.KeepOnlySpans(reference, spans);
            analysis.Write("reference-scope", "radio-keyed gap-free spans only; unkeyed room audio excluded");
            alignment = AudioAlignment.Measure(reference, heard, rate, spans);
            // Boundary evidence is independent of content; a gross conflict is
            // retained and prevents a numerical differential being promoted.
            if (alignment.Aligned && onsetDelta is { } onset && Math.Abs(onset - alignment.LagMilliseconds(rate)) > 250)
                alignment = alignment with { Aligned = false, Reason = "boundary-disagreement" };
            difference = AudioDifferential.Compare(reference, heard, rate, alignment, low, high);
            string alignPath = Path.Combine(directory, "align.txt");
            using var align = new StreamWriter(alignPath);
            align.WriteLine("aligned=" + (alignment.Aligned ? "yes" : "no"));
            align.WriteLine("method=content-correlation; steady tones require edges or a frequency step and are refused here");
            align.WriteLine("reason=" + alignment.Reason);
            align.WriteLine(FormattableString.Invariant($"lag-ms={alignment.LagMilliseconds(rate):R}\nreference-clock-offset-samples={offset}\nconfidence-min-correlation=0.5\nconfidence-min-peak-to-sidelobe=1.2\ndrift-limit-ms=3"));
            foreach (var item in alignment.Spans) align.WriteLine("span=" + JsonSerializer.Serialize(item));
            align.WriteLine("difference=" + JsonSerializer.Serialize(difference));
        }
        else
        {
            // A partial/missing reference must leave an explicit refusal too;
            // never leave an older successful alignment beside a re-analysis.
            File.WriteAllText(Path.Combine(directory, "align.txt"),
                "aligned=no\nreason=sent-unavailable-or-incomplete\n");
        }
        analysis.Event("alignment", alignment); analysis.Event("difference", difference);
        analysis.Write("closed", "complete"); analysis.Dispose();
        File.Move(pending, Path.Combine(directory, "analysis.txt"), true);
        return new(alignment, difference, tone);
    }

    public static ToneSurvivalResult CheckTone(string directory, TakeInspection take, int rate, IReadOnlyList<TakeSpan> spans)
    {
        bool engaged = string.Equals(take.Get("tone-engaged"), "True", StringComparison.OrdinalIgnoreCase);
        double.TryParse(take.Get("tone-hz"), NumberStyles.Float, CultureInfo.InvariantCulture, out double hz);
        var keyedTone = take.Fields.LastOrDefault(p => p.Key == "tone-at-keying").Value;
        if (keyedTone != null)
        {
            using var json = JsonDocument.Parse(keyedTone);
            engaged = json.RootElement.GetProperty("Engaged").GetBoolean();
            hz = json.RootElement.GetProperty("Frequency").GetDouble();
        }
        if (!engaged || spans.Count == 0) return new(false, "no-recorded-keyed-tone");
        var span = spans.FirstOrDefault(s => s.Length >= rate / 4 && s.Start >= rate / 4);
        if (span.Length == 0) return new(false, "no-gap-free-tone-and-rest-span");
        int n = (int)Math.Min(rate, Math.Min(span.Start, span.Length - rate / 10));
        var resting = new TakeSpan(span.Start - n, span.Start);
        if (TakeTimeline.Gaps(take).Any(g => g.Start < resting.End && g.End > resting.Start)) return new(false, "rest-span-has-gap");
        double offset = (Number(take, "tx-frequency-mhz") - Number(take, "pan-centre-mhz")) * 1e6 + (take.Get("mode") == "USB" ? hz : -hz);
        if (Math.Abs(offset) >= rate / 2) return new(false, "tone-outside-iq-band", offset);
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "rf.iq")));
        double Power(long at)
        {
            reader.BaseStream.Position = at * 8;
            double re = 0, im = 0;
            for (int i = 0; i < n; ++i)
            {
                double x = reader.ReadSingle(), y = reader.ReadSingle(), phase = -2 * Math.PI * offset * i / rate;
                double w = .5 - .5 * Math.Cos(2 * Math.PI * i / (n - 1));
                re += (x * Math.Cos(phase) - y * Math.Sin(phase)) * w;
                im += (x * Math.Sin(phase) + y * Math.Cos(phase)) * w;
            }
            return (re * re + im * im) / (n * (double)n);
        }
        double rest = Power(resting.Start), keyed = Power(span.Start + rate / 10);
        double above = 10 * Math.Log10(Math.Max(1e-20, keyed) / Math.Max(1e-20, rest));
        return new(above >= 20, above >= 20 ? "predicted-tone-above-rest" : "tone-not-identified", offset, above);
    }

    public static AudioDifference CompareTakes(string previousDirectory, string currentDirectory)
    {
        var a = TakeReader.Inspect(previousDirectory); var b = TakeReader.Inspect(currentDirectory);
        if (!a.Playable || !b.Playable) return new(false, "corrupt-take");
        string mismatch = AudioDifferential.Compatibility(a, b);
        if (mismatch != "") return new(false, "unlike-takes:" + mismatch);
        SsbTakeRenderer.Render(previousDirectory); SsbTakeRenderer.Render(currentDirectory);
        var (rate, first) = TakeAudio.ReadMono(Path.Combine(previousDirectory, "heard.wav"));
        var (_, second) = TakeAudio.ReadMono(Path.Combine(currentDirectory, "heard.wav"));
        var firstSpan = TakeTimeline.WithoutGaps(TakeTimeline.KeyedSpans(a, first.Length), TakeTimeline.Gaps(a)).FirstOrDefault();
        var secondSpan = TakeTimeline.WithoutGaps(TakeTimeline.KeyedSpans(b, second.Length), TakeTimeline.Gaps(b)).FirstOrDefault();
        int length = (int)Math.Min(firstSpan.Length, secondSpan.Length);
        if (length < rate / 4) return new(false, "no-keyed-gap-free-span");
        first = first.AsSpan((int)firstSpan.Start, length).ToArray(); second = second.AsSpan((int)secondSpan.Start, length).ToArray();
        var alignment = AudioAlignment.Measure(first, second, rate, new[] { new TakeSpan(0, length) });
        return AudioDifferential.Compare(first, second, rate, alignment, Number(a, "tx-filter-low"), Number(a, "tx-filter-high"));
    }
}
