using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Radios.Listenback;

/// <summary>
/// The audio take's header vocabulary (<see cref="TakeKind.AudioFormat"/>,
/// design section 1). Schema tokens, never spoken.
/// </summary>
public static class AudioTakeFields
{
    public const string AudioFile = "audio-file";
    public const string AudioRate = "audio-rate";
    public const string AudioChannels = "audio-channels";
    public const string AudioEncoding = "audio-encoding";
    public const string AudioScale = "audio-scale";
    public const string CaptureTap = "capture-tap";
    public const string WireCodec = "wire-codec";
    public const string Provenance = "audio-provenance";
    public const string CaptureScope = "capture-scope";
    public const string BoundaryBasis = "boundary-basis";
    public const string BoundaryQuality = "boundary-quality";
    public const string SamplesWritten = "samples-written";
    public const string Checksum = "audio-sha256";
    public const string EndReason = "end-reason";
    /// <summary>Repeated: one per observed playback stretch, in sample indices.</summary>
    public const string PlaybackSpan = "playback-span";
    /// <summary>Repeated: one per transport gap filled with silence, in sample indices.</summary>
    public const string AudioGap = "audio-gap";

    public const string WavName = "heard.wav";
    public const string Encoding = "pcm-s16le";
    public const int Rate = 24000;

    /// <summary>Until a bench measurement proves where the radio's playback audio starts and ends.</summary>
    public const string Unverified = "unverified";
    public const string Verified = "verified";
}

/// <summary>A playback stretch or a gap in an audio take, in sample indices.</summary>
public sealed record AudioSpanRecord(long StartSample, long EndSample);
/// <summary>A transport gap the writer filled with silence.</summary>
public sealed record AudioGapRecord(long AtSample, long Samples, string Reason);

/// <summary>
/// The audio road's inspection (design section 2): a complete PCM WAV of the
/// declared shape, its sample count and checksum against the closing block,
/// and spans and gaps in order and in bounds. Nothing here reads, needs or
/// fabricates IQ facts.
/// </summary>
public static class AudioTakeInspector
{
    public static TakeInspection Inspect(string directory, IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        var result = new TakeInspection("incomplete", "no-closing-block", fields);
        try
        {
            if (TakeKind.Of(fields) == TakeKind.Damaged) return result with { State = "corrupt", Reason = "kind" };
            foreach (string key in new[] { AudioTakeFields.AudioFile, AudioTakeFields.AudioRate,
                         AudioTakeFields.AudioChannels, AudioTakeFields.AudioEncoding })
                if (string.IsNullOrEmpty(result.Get(key))) return result with { State = "corrupt", Reason = "missing:" + key };
            // The file is named, never a path: a header cannot point outside its folder.
            if (result.Get(AudioTakeFields.AudioFile) != AudioTakeFields.WavName)
                return result with { State = "corrupt", Reason = "audio-file" };
            if (result.Get(AudioTakeFields.AudioEncoding) != AudioTakeFields.Encoding ||
                result.Get(AudioTakeFields.AudioChannels) != "1" ||
                !int.TryParse(result.Get(AudioTakeFields.AudioRate), NumberStyles.None, CultureInfo.InvariantCulture, out int rate) ||
                rate <= 0)
                return result with { State = "corrupt", Reason = "audio-format" };
            string wav = Path.Combine(directory, AudioTakeFields.WavName);
            if (!File.Exists(wav)) return result with { State = "corrupt", Reason = "audio-missing" };

            string closed = result.Get("closed");
            if (closed == null)
            {
                // A capture that never closed. Its WAV is readable only if the
                // writer got as far as finishing it; either way it is listed,
                // and it is never repaired in place.
                try { TakeAudio.ReadMono(wav); return result; }
                catch (InvalidDataException) { return result with { Reason = "unclosed-audio-unreadable", AudioUnreadable = true }; }
                catch (EndOfStreamException) { return result with { Reason = "unclosed-audio-unreadable", AudioUnreadable = true }; }
            }
            string count = result.Get(AudioTakeFields.SamplesWritten), sum = result.Get(AudioTakeFields.Checksum);
            if (fields[^1].Key != "closed" || count == null || sum == null)
                return result with { State = "corrupt", Reason = "closing-block" };
            if (sum != TakeHeader.Checksum(wav)) return result with { State = "corrupt", Reason = "checksum" };
            var (wavRate, samples) = TakeAudio.ReadMono(wav);
            if (wavRate != rate) return result with { State = "corrupt", Reason = "audio-format" };
            if (!long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out long written) ||
                written != samples.Length)
                return result with { State = "corrupt", Reason = "length" };
            if (!AudioTakeTimeline.InBounds(result, samples.Length)) return result with { State = "corrupt", Reason = "span-range" };
            return closed == "clean"
                ? result with { State = "clean", Reason = "" }
                : result with { Reason = result.Get(AudioTakeFields.EndReason) ?? "incomplete" };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException ||
                                   ex is JsonException || ex is FormatException || ex is OverflowException ||
                                   ex is KeyNotFoundException || ex is InvalidOperationException)
        { return result with { State = "corrupt", Reason = ex is InvalidDataException ? ex.Message : ex.GetType().Name }; }
    }
}

/// <summary>
/// Where an audio take's playback stretches and gaps lie, in sample indices.
/// The audio road's counterpart of <see cref="TakeTimeline"/>'s keyed spans:
/// observed PLAYBACK, never inferred from a speech gate (design section 2).
/// </summary>
public static class AudioTakeTimeline
{
    public static IReadOnlyList<TakeSpan> PlaybackSpans(TakeInspection take, long count)
    {
        var result = new List<TakeSpan>();
        foreach (var field in take.Fields.Where(f => f.Key == AudioTakeFields.PlaybackSpan))
        {
            var span = JsonSerializer.Deserialize<AudioSpanRecord>(field.Value)
                ?? throw new InvalidDataException("span-record");
            long start = Math.Clamp(span.StartSample, 0, count), end = Math.Clamp(span.EndSample, 0, count);
            if (end > start) result.Add(new(start, end));
        }
        return result;
    }

    public static IReadOnlyList<TakeSpan> Gaps(TakeInspection take)
    {
        var result = new List<TakeSpan>();
        foreach (var field in take.Fields.Where(f => f.Key == AudioTakeFields.AudioGap))
        {
            var gap = JsonSerializer.Deserialize<AudioGapRecord>(field.Value)
                ?? throw new InvalidDataException("gap-record");
            if (gap.Samples > 0) result.Add(new(gap.AtSample, gap.AtSample + gap.Samples));
        }
        return result;
    }

    /// <summary>The playback stretches with the transport gaps taken out: what the numbers may be measured on.</summary>
    public static IReadOnlyList<TakeSpan> MeasurableSpans(TakeInspection take, long count) =>
        TakeTimeline.WithoutGaps(PlaybackSpans(take, count), Gaps(take));

    /// <summary>Spans ordered and inside the file, gaps inside the file.</summary>
    internal static bool InBounds(TakeInspection take, long count)
    {
        long previousEnd = 0;
        foreach (var field in take.Fields.Where(f => f.Key == AudioTakeFields.PlaybackSpan))
        {
            var span = JsonSerializer.Deserialize<AudioSpanRecord>(field.Value);
            if (span == null || span.StartSample < previousEnd || span.EndSample < span.StartSample || span.EndSample > count)
                return false;
            previousEnd = span.EndSample;
        }
        foreach (var field in take.Fields.Where(f => f.Key == AudioTakeFields.AudioGap))
        {
            var gap = JsonSerializer.Deserialize<AudioGapRecord>(field.Value);
            if (gap == null || gap.AtSample < 0 || gap.Samples < 0 || gap.AtSample + gap.Samples > count) return false;
        }
        return true;
    }
}

/// <summary>Facts read the same way whichever road a take is on.</summary>
public static class TakeFacts
{
    /// <summary>
    /// The whole take's length in seconds: samples over rate on the audio
    /// road, IQ pairs over the requested rate on the IQ road (the calculation
    /// the status lines always used).
    /// </summary>
    public static double Seconds(TakeInspection take)
    {
        if (take.IsAudio)
        {
            double.TryParse(take.Get(AudioTakeFields.SamplesWritten) ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out double samples);
            return int.TryParse(take.Get(AudioTakeFields.AudioRate), NumberStyles.None, CultureInfo.InvariantCulture, out int rate) && rate > 0
                ? samples / rate : 0;
        }
        return double.Parse(take.Get("pairs-written") ?? "0", CultureInfo.InvariantCulture) /
               int.Parse(take.Get("rate-requested"), CultureInfo.InvariantCulture);
    }
}
