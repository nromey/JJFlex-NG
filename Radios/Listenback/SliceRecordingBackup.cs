#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Radios.Listenback;

/// <summary>The header keys a slice recording's backup writes beside the playback take's own (#999). Schema tokens.</summary>
public static class SliceRecordingFields
{
    /// <summary>The recording's length as the radio reported it when the backup began.</summary>
    public const string RadioSeconds = "slice-recording-seconds";
    /// <summary>Where the recording came from: a <see cref="SliceTakeSource"/> token.</summary>
    public const string Source = "slice-recording-source";
    /// <summary>When the recording was made, round-trip UTC, when the stamp knew.</summary>
    public const string MadeUtc = "slice-recording-made-utc";
    /// <summary>The slice's frequency when the recording was made, in MHz, when the stamp knew.</summary>
    public const string FrequencyMHz = "slice-recording-frequency-mhz";
}

/// <summary>How a slice recording's backup ended (#999).</summary>
public enum SliceBackupResult
{
    /// <summary>The whole recording is on this computer as a take, and the slice's stamp says so.</summary>
    BackedUp,
    /// <summary>The radio reports no recording on the slice, or no length for it.</summary>
    NothingToBackUp,
    /// <summary>The radio is transmitting or tuning: a playback now would go to the air.</summary>
    Transmitting,
    /// <summary>Another backup is running.</summary>
    Busy,
    /// <summary>The slice is recording or playing already.</summary>
    SliceBusy,
    /// <summary>The take could not be started (no folder, no capture); nothing was saved.</summary>
    NotSaved,
    /// <summary>The radio did not play the recording; <see cref="SliceBackupOutcome.PlayRefusal"/> says why.</summary>
    PlayRefused,
    /// <summary>The operator stopped it, or something ended the playback early. A partial take may remain; it is not a backup.</summary>
    Stopped,
    /// <summary>The capture closed incomplete, or shorter than the recording. Not a backup.</summary>
    Incomplete,
    /// <summary>The recording on the slice changed while it was being backed up. Not a backup.</summary>
    RecordingChanged,
}

/// <param name="Letter">The slice's letter.</param>
/// <param name="Seconds">The recording's length as the radio reported it, or 0.</param>
/// <param name="Directory">The take folder, when one was written.</param>
/// <param name="PlayRefusal">The recorder's own refusal, when the radio would not play.</param>
public sealed record SliceBackupOutcome(SliceBackupResult Result, string Letter, double Seconds,
    string? Directory = null, RecorderOutcome? PlayRefusal = null)
{
    public bool BackedUp => Result == SliceBackupResult.BackedUp;
}

/// <summary>
/// Back up one of the radio's slice recordings to this computer (#999): the
/// radio plays it, the other audible slices muted (D2's mutes, put back however
/// it ends), and the playback is captured as a take of kind
/// <see cref="TakeKind.SliceRecording"/>. On success the slice's stamp names the
/// take folder, so the overwrite warning knows the recording is safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only an explicit Back up makes one</b> (ruled S2, 2026-11-07 20:51): Play
/// takes' Back up, or the overwrite warning's Back up first. No playback of a
/// slice recording, local or to the air, makes a take by itself.
/// </para>
/// <para>
/// <b>Runs in real time.</b> A two-minute recording takes two minutes. One backup
/// at a time, app-wide; <see cref="Stop"/> ends the running one, and what it had
/// captured is kept as a take but is not a backup.
/// </para>
/// <para>
/// <b>Plays locally, never to the air.</b> It refuses while the radio transmits
/// or tunes, and plays through <see cref="FlexBase.StartSlicePlayAsync"/>, which
/// pauses VOX for a local listen and stops on any transmit edge. A transmit edge
/// therefore ends the backup short, and a short capture is never counted.
/// </para>
/// <para>
/// <b>Speaks nothing.</b> The door that started it says what happened.
/// </para>
/// </remarks>
public sealed class SliceRecordingBackup
{
    /// <summary>How much shorter than the radio's length a capture may be and still count (the boundary is unverified).</summary>
    /// <remarks>A GUESS until the bench measures the playback boundary (design section 5); recorded in the #999 report.</remarks>
    public const double ShortfallAllowedSeconds = 1.5;

    /// <summary>How long past the recording's own length the capture may run before it is ended as incomplete.</summary>
    public static readonly TimeSpan Overrun = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static SliceRecordingBackup? _running;

    private readonly TaskCompletionSource<bool> _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SliceRecordingBackup(string letter, double seconds) { Letter = letter; Seconds = seconds; }

    /// <summary>The slice being backed up.</summary>
    public string Letter { get; }
    /// <summary>The recording's length as the radio reported it.</summary>
    public double Seconds { get; }

    /// <summary>The backup running now, or null.</summary>
    public static SliceRecordingBackup? Running { get { lock (Gate) return _running; } }

    /// <summary>End this backup now. Nothing it captured counts as a backup.</summary>
    public void Stop() => _stop.TrySetResult(true);

    /// <summary>Stop the running backup, if there is one. True when there was.</summary>
    public static bool StopRunning()
    {
        var running = Running;
        running?.Stop();
        return running != null;
    }

    /// <summary>
    /// Back up the recording on <paramref name="target"/>'s slice into
    /// <paramref name="root"/>, capturing through <paramref name="startCapture"/>
    /// (in the app, the computer recorder's second sink).
    /// </summary>
    /// <param name="started">Told once the radio is playing the recording, with the backup, so a door can say so and offer Stop.</param>
    public static async Task<SliceBackupOutcome> RunAsync(FlexBase rig, SliceRecorderTarget target, string root,
        Func<AudioTakeWriter, int, PlaybackTakeCapture> startCapture, Action<SliceRecordingBackup>? started = null)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        var facts = rig.SliceOverwriteFactsFor(target);
        string letter = facts?.Letter ?? rig.SliceRecorderLetter(target);
        if (facts == null || !facts.HasRecording || facts.RadioSeconds is not double seconds)
            return new(SliceBackupResult.NothingToBackUp, letter, 0);
        // The same slice throughout, whichever becomes active meanwhile.
        var slice = SliceRecorderTarget.Letter(letter[0]);
        if (rig.Transmit || rig.TuningNow) return new(SliceBackupResult.Transmitting, letter, seconds);
        if (rig.IsSliceRecording(slice) || rig.IsSlicePlaying(slice)) return new(SliceBackupResult.SliceBusy, letter, seconds);

        var backup = new SliceRecordingBackup(letter, seconds);
        lock (Gate)
        {
            if (_running != null) return new(SliceBackupResult.Busy, letter, seconds);
            _running = backup;
        }
        try { return await backup.RunCoreAsync(rig, slice, facts, root, startCapture, started).ConfigureAwait(false); }
        finally { lock (Gate) if (ReferenceEquals(_running, backup)) _running = null; }
    }

    private async Task<SliceBackupOutcome> RunCoreAsync(FlexBase rig, SliceRecorderTarget slice, SliceOverwriteFacts facts,
        string root, Func<AudioTakeWriter, int, PlaybackTakeCapture> startCapture, Action<SliceRecordingBackup>? started)
    {
        SliceBackupOutcome Ended(SliceBackupResult result, string? folder = null, RecorderOutcome? refusal = null)
        {
            Trace("slice " + Letter + " " + result + (folder == null ? "" : " " + System.IO.Path.GetFileName(folder)));
            return new(result, Letter, Seconds, folder, refusal);
        }

        var run = PlaybackTakeRun.Admit(rig, TakeKind.SliceRecording, slice);
        if (!await run.ArmAsync(root, startCapture, muteOthers: true).ConfigureAwait(false))
            return Ended(SliceBackupResult.NotSaved);

        // Local only, fixed at this press and read again at the radio command:
        // a key during the arming above refuses it rather than sending it.
        var play = await rig.StartSliceListenAsync(slice, owner: this).ConfigureAwait(false);
        if (!play.Success || play.AlreadyInState)
        {
            run.End("play-refused");
            await Bounded(run.Finished, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return Ended(SliceBackupResult.PlayRefused, run.DirectoryPath, play);
        }
        try { started?.Invoke(this); }
        catch (Exception ex) { Trace("started callback: " + ex.Message); }

        var limit = Task.Delay(TimeSpan.FromSeconds(Seconds) + Overrun);
        var first = await Task.WhenAny(run.Finished, _stop.Task, limit).ConfigureAwait(false);
        if (first != run.Finished)
        {
            bool stopped = first == _stop.Task;
            run.StopRequested(stopped ? "operator-stopped" : "overran");
            try { await rig.StopSlicePlayAsync(slice, owner: this).ConfigureAwait(false); }
            catch (Exception ex) { Trace("stop playback: " + ex.Message); }
            if (!await Bounded(run.Finished, PlaybackTakeRun.StopReportWait + TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                run.End(stopped ? "operator-stopped" : "overran");
            await Bounded(run.Finished, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return Ended(stopped ? SliceBackupResult.Stopped : SliceBackupResult.Incomplete, run.DirectoryPath);
        }

        string? folder = run.DirectoryPath;
        if (!run.Finished.Result || folder == null) return Ended(SliceBackupResult.NotSaved, folder);
        var whole = CapturedWhole(folder, Seconds);
        if (whole != null) return Ended(whole.Value, folder);
        // The recording must still be the one that played: another client could
        // have recorded over the slice while it did.
        var now = rig.SliceOverwriteFactsFor(slice);
        if (now == null || now.RadioSeconds is not double still
            || Math.Abs(still - Seconds) > RadioConfig.SliceTakeLengthTolerance)
            return Ended(SliceBackupResult.RecordingChanged, folder);
        if (!RadioConfig.RecordSliceTakeBackup(facts.Serial, Letter, Seconds, folder))
        {
            Trace("slice " + Letter + " backup taken but the stamp could not be written");
            return Ended(SliceBackupResult.NotSaved, folder);
        }
        return Ended(SliceBackupResult.BackedUp, folder);
    }

    /// <summary>
    /// Null when the take in <paramref name="folder"/> is a clean capture of a
    /// recording <paramref name="seconds"/> long (within
    /// <see cref="ShortfallAllowedSeconds"/>); otherwise why it is not.
    /// </summary>
    public static SliceBackupResult? CapturedWhole(string folder, double seconds)
    {
        var take = TakeReader.Inspect(folder);
        double captured = take.CanPlay ? TakeFacts.Seconds(take) : 0;
        if (take.State == "clean" && captured >= seconds - ShortfallAllowedSeconds) return null;
        Trace("captured " + captured.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + " s of " + seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ", state " + take.State);
        return take.State == "clean" ? SliceBackupResult.Stopped : SliceBackupResult.Incomplete;
    }

    /// <summary>
    /// A Radio recording audio check method playback was saved as a take: it
    /// counts as a backup of that slice's recording (ruled 2026-11-07 17:57),
    /// when it captured the whole recording the radio still reports. True when
    /// the slice's stamp now names it. Makes no take: the check's own "Save each
    /// playback as a take" setting decided that (off by default, S2).
    /// </summary>
    public static bool NoteSavedPlaybackAsBackup(FlexBase rig, SliceRecorderTarget target, string folder)
    {
        if (rig == null || string.IsNullOrEmpty(folder)) return false;
        var facts = rig.SliceOverwriteFactsFor(target);
        if (facts == null || !facts.HasRecording || facts.RadioSeconds is not double seconds) return false;
        if (CapturedWhole(folder, seconds) != null) return false;
        bool noted = RadioConfig.RecordSliceTakeBackup(facts.Serial, facts.Letter, seconds, folder);
        Trace("saved playback of slice " + facts.Letter + (noted ? " counts as its backup" : " could not be noted as its backup"));
        return noted;
    }

    private static async Task<bool> Bounded(Task task, TimeSpan wait) =>
        await Task.WhenAny(task, Task.Delay(wait)).ConfigureAwait(false) == task;

    private static void Trace(string text) => JJTrace.Tracing.TraceLine("SliceRecordingBackup: " + text, TraceLevel.Info);
}
