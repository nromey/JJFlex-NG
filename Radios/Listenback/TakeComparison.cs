using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Radios.Listenback;

/// <summary>
/// Whether the radio's playback boundary has been MEASURED on a transport:
/// where the playback's audio really starts and ends against the radio's
/// PlayOn reports (Unified takes, design section 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The one place this is decided.</b> Loudness and dynamics between two
/// playback takes (RMS, LUFS and peak differences) are compared only when the
/// boundary is verified for the transport both takes came over. Until the
/// harness has measured it (the bench procedure in the build report), both
/// transports are unverified, so those pairs compare TONE only, from interior,
/// gap-free audio with <see cref="GuardSeconds"/> left out at each end, and
/// the sentence says loudness is held back.
/// </para>
/// <para>
/// <b>Flipping either line is a ruling, not a fix.</b> It comes back to Noel
/// with the measurement. <c>UnifiedTakesComparisonTests</c> pins both as
/// unverified so the flip cannot happen by accident.
/// </para>
/// </remarks>
public static class PlaybackBoundary
{
    /// <summary>The analysis guard at each end of every measured stretch. It is NOT a claimed network delay.</summary>
    public const double GuardSeconds = 0.25;

    public static bool IsVerified(string transport) => transport switch
    {
        "LAN" => false,
        "SmartLink" => false,
        _ => false,
    };
}

/// <summary>Per-take figures on the audio road, attributed to that take; never a difference.</summary>
public static class AudioTakeStatistics
{
    /// <summary>The common tone band every new-road comparison measures, whatever each take's filter.</summary>
    public const double ToneLow = 100, ToneHigh = 3000;

    /// <summary>The guarded, gap-free playback stretches of an audio take, in its own samples.</summary>
    public static IReadOnlyList<TakeSpan> Measurable(TakeInspection take, long count, int rate) =>
        Guarded(AudioTakeTimeline.MeasurableSpans(take, count), (long)(rate * PlaybackBoundary.GuardSeconds));

    /// <summary>Each span shortened by <paramref name="guard"/> at both ends; spans that vanish are dropped.</summary>
    public static IReadOnlyList<TakeSpan> Guarded(IEnumerable<TakeSpan> spans, long guard) =>
        spans.Select(s => new TakeSpan(s.Start + guard, s.End - guard)).Where(s => s.Length > 0).ToList();

    public static TakeStatistics Measure(string directory, TakeInspection take)
    {
        if (!take.CanMeasure) return new(false, "take is not measurable") { SplatterAvailable = false };
        var (rate, heard) = TakeAudio.ReadMono(Path.Combine(directory, AudioTakeFields.WavName));
        var spans = Measurable(take, heard.Length, rate);
        var selected = spans.SelectMany(s => heard.Skip((int)s.Start).Take((int)s.Length)).ToArray();
        if (selected.Length < rate / 4) return new(false, "too little gap-free playback audio") { SplatterAvailable = false };
        var shape = AudioDifferential.Measure(selected, rate, ToneLow, ToneHigh);
        var shapes = new[] { shape }.Concat(Enumerable.Range(1, 3).Select(part =>
            AudioDifferential.Measure(selected.AsSpan(part * Math.Max(0, selected.Length - 4096) / 3), rate, ToneLow, ToneHigh))).ToArray();
        var loudness = new JJPortaudio.LufsMeter();
        const int frames = 4096;
        for (int start = 0; start < selected.Length; start += frames)
        {
            int n = Math.Min(frames, selected.Length - start);
            var stereo = new float[n * 2];
            for (int i = 0; i < n; i++) stereo[2 * i] = stereo[2 * i + 1] = selected[start + i];
            loudness.Process(stereo, stereo.Length, (uint)rate);
        }
        // Splatter is unavailable on this road: the receive filter and the
        // codec destroy the evidence of out-of-filter RF energy.
        return new(true, "", loudness.IntegratedLufs, shape.PeakDb, shape.RmsDb, shapes.Average(s => s.TiltDb), shape.CrestDb, 0)
        { SplatterAvailable = false };
    }
}

public static partial class TakeAnalysis
{
    /// <summary>
    /// The audio road (design sections 2 and 3, with the build brief's one
    /// loudness rule). Gates first, in order: readable takes; kinds that can
    /// be compared at all; a known, matching source; the same mode; the same
    /// antennas wherever both takes have that role; the same radio whenever
    /// Heard on the air takes part. Then TONE, on the common 100 to 3000 Hz
    /// band, from guarded gap-free audio. Level is added only between two
    /// playback takes of the same kind, over the same transport, at the same
    /// slice volume, and only once <see cref="PlaybackBoundary"/> says that
    /// transport is verified. Punch and splatter are never compared here.
    /// </summary>
    private static AudioDifference CompareOnAudioRoad(TakeInspection a, TakeInspection b, string previousDirectory,
        string currentDirectory, bool reuseRenders)
    {
        string ka = a.Kind, kb = b.Kind;
        AudioDifference Refuse(string reason, IReadOnlyList<SetupChange> changes = null) =>
            new(false, reason, SetupChanges: changes, Road: "audio", FirstKind: ka, SecondKind: kb, LevelCompared: false);

        if (!a.CanMeasure || !b.CanMeasure) return Refuse("corrupt-take");
        // A diagnostic take recorded no transmission of this source; an older
        // take of unrecorded kind cannot evidence its source (design section 3).
        if (ka is TakeKind.Diagnostic or TakeKind.UnknownLegacy or TakeKind.Damaged ||
            kb is TakeKind.Diagnostic or TakeKind.UnknownLegacy or TakeKind.Damaged)
            return Refuse("unlike-takes:kind");
        // A playback that also caught the other slices, or was keyed over, is
        // kept and played but never measured against another (D2's fallback).
        if (a.IsAudio && a.Get(CaptureIsolation) != Isolated || b.IsAudio && b.Get(CaptureIsolation) != Isolated)
            return Refuse("unlike-takes:contaminated");
        string sa = SourceOf(a), sb = SourceOf(b);
        if (sa == null || sb == null) return Refuse("unlike-takes:source-unknown");
        if (sa != sb) return Refuse("unlike-takes:source", new[] { new SetupChange("source", sa, sb) });
        if (a.Get("mode") is not ("USB" or "LSB") || a.Get("mode") != b.Get("mode")) return Refuse("unlike-takes:mode");
        if (a.Get("tx-antenna") == null || a.Get("tx-antenna") != b.Get("tx-antenna")) return Refuse("unlike-takes:tx-antenna");
        // Radio recording has no RF receive role; the other two both received.
        if (ka != TakeKind.RadioRecording && kb != TakeKind.RadioRecording &&
            (a.Get("receive-antenna") == null || a.Get("receive-antenna") != b.Get("receive-antenna")))
            return Refuse("unlike-takes:receive-antenna");
        bool sameRadio = a.Get("serial") != null && a.Get("serial") == b.Get("serial");
        if ((ka == TakeKind.OffAir || kb == TakeKind.OffAir) && !sameRadio) return Refuse("unlike-takes:serial");

        var changes = TakeSetup.Changes(a, b).ToList();
        if (!sameRadio)
        {
            // The radio is named after the microphone and the profile (Q2).
            int at = changes.TakeWhile(c => TakeSetup.GroupOf(c.Key) is TakeSetup.GroupMicrophone or TakeSetup.GroupProfile).Count();
            changes.Insert(at, new SetupChange("radio", RadioName(a), RadioName(b)));
        }

        string withheld = LevelWithheld(a, b);
        var (rateA, first, spansA) = MeasuringAudio(a, previousDirectory, reuseRenders);
        var (_, second, spansB) = MeasuringAudio(b, currentDirectory, reuseRenders);
        var measured = AudioDifferential.CompareOverallAverage(first, spansA, second, spansB, rateA,
            AudioTakeStatistics.ToneLow, AudioTakeStatistics.ToneHigh);
        if (!measured.Comparable) return Refuse("too-little-audio", changes);
        bool level = withheld.Length == 0;
        return new(true, "tone" + (level ? "-and-level" : ""), level ? measured.LevelDb : 0, measured.TiltDb,
            SetupChanges: changes, Road: "audio", FirstKind: ka, SecondKind: kb, LevelCompared: level, LevelWithheld: withheld);
    }

    /// <summary>
    /// Why level is not compared between these two takes, or "" when it is.
    /// The first reason that applies wins, because it is the one the sentence
    /// gives.
    /// </summary>
    /// <summary>A playback take's verdict on what else reached its capture: <see cref="Isolated"/>, <c>contaminated</c> or <c>unknown</c>.</summary>
    public const string CaptureIsolation = "capture-isolation";
    public const string Isolated = "isolated";

    private static string LevelWithheld(TakeInspection a, TakeInspection b)
    {
        if (a.Kind != b.Kind || !a.IsAudio || !b.IsAudio) return "kinds";
        string ta = a.Get("transport"), tb = b.Get("transport");
        if (ta == null || ta != tb) return "transport";
        string ga = a.Get("slice-audio-gain"), gb = b.Get("slice-audio-gain");
        if (ga == null || ga != gb) return "slice-volume";
        if (a.Kind == TakeKind.LoopedBack)
            foreach (string key in new[] { "rf-gain", "agc-mode", "agc-threshold", "rx-filter-low", "rx-filter-high" })
                if (a.Get(key) == null || a.Get(key) != b.Get(key)) return "receive-chain";
        if (!PlaybackBoundary.IsVerified(ta)) return "boundary";
        return "";
    }

    /// <summary>The source a take recorded: a new take says so outright; an IQ take is read the old way.</summary>
    private static string SourceOf(TakeInspection take) =>
        take.IsAudio ? take.Get("source") is TakeSetup.Tone or TakeSetup.Voice or TakeSetup.Reference ? take.Get("source") : null
            : TakeSetup.Source(take);

    private static string RadioName(TakeInspection take) =>
        string.Join(" ", new[] { take.Get("model"), take.Get("nickname") }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// A take's audio for measuring, at 24 kHz, with the stretches that may be
    /// measured: an audio take's guarded playback; an IQ take's rendered keyed
    /// gap-free stretches, guarded the same way, resampled without changing
    /// gain.
    /// </summary>
    private static (int Rate, float[] Samples, IReadOnlyList<TakeSpan> Spans) MeasuringAudio(TakeInspection take,
        string directory, bool reuseRender)
    {
        const int rate = AudioTakeFields.Rate;
        if (take.IsAudio)
        {
            var (wavRate, heard) = TakeAudio.ReadMono(Path.Combine(directory, AudioTakeFields.WavName));
            var spans = AudioTakeStatistics.Measurable(take, heard.Length, wavRate);
            return wavRate == rate ? (rate, heard, spans)
                : (rate, TakeAudio.Resample(heard, wavRate, rate), Scale(spans, wavRate, rate));
        }
        if (!reuseRender || !File.Exists(Path.Combine(directory, "heard.wav"))) SsbTakeRenderer.Render(directory);
        var (iqRate, rendered) = TakeAudio.ReadMono(Path.Combine(directory, "heard.wav"));
        var keyed = TakeTimeline.WithoutGaps(TakeTimeline.KeyedSpans(take, rendered.Length), TakeTimeline.Gaps(take));
        var guarded = AudioTakeStatistics.Guarded(keyed, (long)(iqRate * PlaybackBoundary.GuardSeconds));
        return (rate, TakeAudio.Resample(rendered, iqRate, rate), Scale(guarded, iqRate, rate));
    }

    private static IReadOnlyList<TakeSpan> Scale(IEnumerable<TakeSpan> spans, int from, int to) =>
        spans.Select(s => new TakeSpan(s.Start * to / from, s.End * to / from)).Where(s => s.Length > 0).ToList();
}
