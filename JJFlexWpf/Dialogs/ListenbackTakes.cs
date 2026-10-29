using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Radios;
using Radios.Listenback;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// What both windows that make IQ takes share: finding takes on disk, playing
/// one after the click, the words for a take's state, and the comparison
/// sentences.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (#835).</b> Until then every IQ take was made from one
/// place, the Audio Workshop's Hear Yourself page, and these lived as private
/// members of that window. Check your audio now makes the off-air take, and the
/// Workshop's Diagnostics page makes the receive-only diagnostic take and plays
/// the detuned renders. Two windows, one set of rules, so the rules live here
/// once rather than being copied into each.
/// </para>
/// <para>
/// <b>Exclusivity is the radio's, not ours.</b> <c>FlexBase.StartListenbackAsync</c>
/// holds a per-radio lease and refuses a second take with
/// <c>take-already-running</c>, so two windows cannot run two takes on one radio
/// however the operator moves between them. The windows still check each other
/// for a running Audio Check, because a check is not a take.
/// </para>
/// </remarks>
internal static class ListenbackTakes
{
    /// <summary>Where takes live. Resolved from <see cref="RadioConfig.AppDataRoot"/>, so a test run's settings root holds its own.</summary>
    internal static string Root => Path.Combine(RadioConfig.AppDataRoot, "Recordings", "takes");

    /// <summary>The newest playable take, or null.</summary>
    internal static string? NewestDirectory()
    {
        string root = Root;
        return !Directory.Exists(root) ? null : Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "heard.wav")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "take.txt"))).FirstOrDefault();
    }

    /// <summary>The newest playable take other than <paramref name="current"/>, or null.</summary>
    internal static string? PreviousDirectory(string current)
    {
        string root = Root;
        return !Directory.Exists(root) ? null : Directory.EnumerateDirectories(root)
            .Where(d => d != current && File.Exists(Path.Combine(d, "heard.wav")) &&
                File.Exists(Path.Combine(d, "take.txt")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "take.txt"))).FirstOrDefault();
    }

    /// <summary>When this run of the app began: the start of the session the
    /// automatic comparison is confined to (<see cref="TakeSession"/>).</summary>
    internal static readonly DateTime SessionStartUtc =
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    internal static string StateText(string state)
    {
        string key = "audio.iq.state." + state;
        return Lexicon.Get(key);
    }

    internal static string UnalignedKey(string reason) => reason == "not-keyed" ? "audio.iq.no_transmission" :
        reason == "sent-unavailable" ? "audio.iq.sent_unavailable" : "audio.iq.not_aligned";

    /// <summary>
    /// The take's length as the status lines say it: the PLAYBACK's, from just
    /// before the first keyed mark, when there is a render (#835, fix round
    /// 2026-10-28); the whole capture otherwise.
    /// </summary>
    internal static string Seconds(TakeInspection take, TakeRenderResult? rendered) =>
        rendered != null && rendered.PlaybackPath.Length > 0
            ? rendered.PlaybackSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : Seconds(take);

    /// <summary>The file to play: the trimmed playback when there is one, heard.wav otherwise.</summary>
    internal static string PlaybackPath(string directory, TakeRenderResult? rendered) =>
        rendered != null && rendered.PlaybackPath.Length > 0 && System.IO.File.Exists(rendered.PlaybackPath)
            ? rendered.PlaybackPath : System.IO.Path.Combine(directory, "heard.wav");

    /// <summary>The take's length in seconds, as the status lines say it.</summary>
    internal static string Seconds(TakeInspection take) =>
        (double.Parse(take.Get("pairs-written") ?? "0", System.Globalization.CultureInfo.InvariantCulture) /
         int.Parse(take.Get("rate-requested"), System.Globalization.CultureInfo.InvariantCulture)).ToString("F1");

    /// <summary>The sentence for a take that failed, from the failure's reason key.</summary>
    internal static string FailureText(Exception ex)
    {
        string reason = ex is OperationCanceledException ? "cancelled" : ex.Message;
        string key = "audio.iq.reason." + reason;
        string text = Lexicon.Get(key);
        if (text == key) text = Lexicon.Get("audio.iq.reason.unexpected");
        return Lexicon.Get("audio.iq.failed", ("reason", text));
    }

    /// <summary>
    /// The notice for a take window that closed while a take was ending. Read at
    /// delivery, after the closing focus transition and after cleanup, because
    /// cancellation or playback failure does not imply a record.
    /// </summary>
    internal static void PostClosedNotice(Dispatcher dispatcher, Func<RadioRecoverySnapshot>? readRecovery,
        string text, bool failure)
    {
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            ListenbackCloseNotice notice;
            try { notice = ListenbackCloseNotice.ForCurrentRecord(readRecovery?.Invoke(), text, failure); }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Listenback close record read: " + ex, System.Diagnostics.TraceLevel.Error);
                notice = new(Lexicon.Get("audio.iq.recovery_state_unknown"), true);
            }
            if (notice.Failure) EarconPlayer.LeaderInvalidTone(); else EarconPlayer.ConfirmTone();
            ScreenReaderOutput.Speak(notice.Text, VerbosityLevel.Critical);
        }));
    }

    /// <summary>
    /// Play a take file after the confirmation click, with live receive muted
    /// before the click so the take is not heard over the band.
    /// </summary>
    /// <param name="stillWanted">Asked after the click: false when the window closed or the radio changed meanwhile.</param>
    internal static async Task<bool> PlayAfterClickAsync(string path, string announcement,
        FlexBase? rig, Func<FlexBase?, bool> stillWanted, bool offAirListeningGain = false)
    {
        RecordingPlayer.Stop();
        bool priorMute = rig?.LocalReceiveMutedForTakePlayback ?? false;
        if (rig != null) rig.LocalReceiveMutedForTakePlayback = true;
        bool started = false;
        try
        {
            // confirm.wav lasts 200 ms. Mute live receive before this click,
            // then let the click finish before adding the take to the mixer.
            EarconPlayer.ConfirmTone();
            await Task.Delay(240);
            if (!stillWanted(rig)) return false;
            started = RecordingPlayer.Play(path, announcement, rig, offAirListeningGain,
                priorLiveMute: priorMute);
            return started;
        }
        finally { if (!started && rig != null) rig.LocalReceiveMutedForTakePlayback = priorMute; }
    }

    /// <summary>
    /// Render a take, optionally detuned, and play it. Returns null when it
    /// played or was not wanted, or the status sentence for a take that could
    /// not be played. Throws for the host's failure path.
    /// </summary>
    internal static async Task<string?> RenderAndPlayAsync(string directory, int detune, FlexBase? rig,
        Func<FlexBase?, bool> stillWanted)
    {
        var take = await Task.Run(() => TakeReader.Inspect(directory));
        if (!take.Playable) return Lexicon.Get("audio.iq.corrupt", ("check", take.Reason));
        var rendered = await Task.Run(() => SsbTakeRenderer.Render(directory, detune));
        if (!stillWanted(rig)) return null;
        string playbackKey = take.State == "incomplete" ? "audio.iq.playing_incomplete" : detune == 0 ? "audio.iq.playing" : "audio.iq.playing_detuned";
        await PlayAfterClickAsync(PlaybackPath(directory, rendered), Lexicon.Get(playbackKey), rig, stillWanted,
            offAirListeningGain: true);
        return null;
    }

    /// <summary>One take's measurements, one per line, each a sentence.</summary>
    internal static string FormatStatistics(string label, TakeStatistics? stats)
    {
        if (stats == null) return label + ": no earlier take.";
        if (!stats.Available) return label + ": " + stats.Reason + ".";
        return string.Join(Environment.NewLine, new[] {
            $"{label} integrated loudness: {stats.Lufs:F1} LUFS",
            $"{label} peak: {stats.PeakDbfs:F1} dBFS",
            $"{label} RMS: {stats.RmsDbfs:F1} dBFS",
            $"{label} brightness, highs versus lows: {stats.BrightnessDb:+0.0;-0.0;0.0} dB",
            $"{label} punch, crest: {stats.PunchDb:F1} dB",
            $"{label} splatter, out-of-filter: {stats.SplatterDbfs:F1} dBFS" });
    }

    internal static string FormatAnalysisDetails(TakeAnalysisResult measured, TakeInspection take)
    {
        string details = measured.Alignment.Aligned ?
            Lexicon.Get("audio.iq.aligned", ("ms", measured.Alignment.LagMilliseconds(
                int.Parse(take.Get("rate-echoed"))).ToString("F1"))) :
            Lexicon.Get(UnalignedKey(measured.Alignment.Reason));
        if (measured.Difference.Comparable) details += " " + ReferenceDifferenceSentence(measured.Difference);
        if (measured.Tone.Reason != "no-recorded-keyed-tone") details += " " + Lexicon.Get(
            measured.Tone.Identified ? "audio.iq.tone_identified" : "audio.iq.tone_not_identified",
            ("hz", measured.Tone.OffsetHz.ToString("F1")), ("db", measured.Tone.AboveRestDb.ToString("F1")));
        return details;
    }

    private const double AudibleComparisonDb = 0.5;
    private static bool Changed(double db) => Math.Abs(db) >= AudibleComparisonDb;

    private static string LevelAndToneSentence(AudioDifference difference, bool sentReference = false)
    {
        bool levelChanged = Changed(difference.LevelDb), toneChanged = Changed(difference.TiltDb);
        string key = (sentReference, levelChanged, toneChanged) switch
        {
            (false, false, false) => "audio.iq.same_level_tone",
            (false, true, false) => "audio.iq.level_changed_tone_same",
            (false, false, true) => "audio.iq.level_same_tone_changed",
            (false, true, true) => "audio.iq.level_and_tone_changed",
            (true, false, false) => "audio.iq.reference_same_level_tone",
            (true, true, false) => "audio.iq.reference_level_changed_tone_same",
            (true, false, true) => "audio.iq.reference_level_same_tone_changed",
            _ => "audio.iq.reference_level_and_tone_changed"
        };
        return Lexicon.Get(key,
            ("level_word", Lexicon.Get(difference.LevelDb >= 0 ? "audio.iq.louder" : "audio.iq.quieter")),
            ("level", Math.Abs(difference.LevelDb).ToString("F1")),
            ("brightness_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.brighter" : "audio.iq.darker")),
            ("high_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.stronger" : "audio.iq.weaker")),
            ("tilt", Math.Abs(difference.TiltDb).ToString("F1")));
    }

    private static string AlignedDifferenceSentence(AudioDifference difference, bool sentReference)
    {
        bool crestChanged = Changed(difference.CrestDb), stopChanged = Changed(difference.StopbandDb);
        if (!Changed(difference.LevelDb) && !Changed(difference.TiltDb) &&
            !crestChanged && !stopChanged)
            return Lexicon.Get(sentReference ? "audio.iq.reference_all_measures_same" : "audio.iq.all_measures_same");
        string punch = crestChanged ? Lexicon.Get("audio.iq.punch_changed",
            ("crest_word", Lexicon.Get(difference.CrestDb < 0 ? "audio.iq.more_compressed" : "audio.iq.more_peaky")),
            ("crest", Math.Abs(difference.CrestDb).ToString("F1"))) : Lexicon.Get("audio.iq.punch_same");
        string stopband = stopChanged ? Lexicon.Get("audio.iq.stopband_changed",
            ("direction", Lexicon.Get(difference.StopbandDb >= 0 ? "audio.iq.rose" : "audio.iq.fell")),
            ("stop", Math.Abs(difference.StopbandDb).ToString("F1"))) : Lexicon.Get("audio.iq.stopband_same");
        return LevelAndToneSentence(difference, sentReference) + " " + punch + " " + stopband;
    }

    internal static string DifferenceSentence(AudioDifference difference) =>
        AlignedDifferenceSentence(difference, sentReference: false);
    internal static string ReferenceDifferenceSentence(AudioDifference difference) =>
        AlignedDifferenceSentence(difference, sentReference: true);
    internal static string AverageDifferenceSentence(AudioDifference difference) => LevelAndToneSentence(difference);

    /// <summary>
    /// The spoken result of the automatic comparison after an off-air take has
    /// played back: the intro, then (round 14) any setup difference named in
    /// front of the numbers, then the numbers. Assembled, as NVDA hears it:
    /// "Compared with last take. Your microphone profile was Default on your
    /// last take and is Heil PR 781 on this one. Your new take is quieter by
    /// 2.1 dB than your last take, with the same tone."
    /// </summary>
    internal static string AutoComparisonSentence(AudioDifference difference) =>
        Lexicon.Get("audio.iq.auto_result_intro") + " " + WithSetupChanges(difference, LevelAndToneSentence(difference));

    internal static string WithSetupChanges(AudioDifference difference, string numbers)
    {
        string changes = ListenbackResultText.SetupChanges(difference);
        return changes.Length == 0 ? numbers : changes + " " + numbers;
    }
}
