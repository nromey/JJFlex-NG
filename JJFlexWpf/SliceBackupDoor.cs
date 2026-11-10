using System;
using System.Globalization;
using System.Threading.Tasks;
using Radios;
using Radios.Listenback;
using Radios.Speech;

namespace JJFlexWpf;

/// <summary>
/// The one door to backing up a slice recording (#999): Play takes' Back up,
/// and the overwrite warning's Back up first. It checks what only the app can
/// (the radio's audio on this computer, the computer recorder), runs
/// <see cref="SliceRecordingBackup"/>, and says what happened.
/// </summary>
/// <remarks>
/// <b>Only an explicit Back up reaches this</b> (ruled S2, 2026-11-07 20:51). No
/// playback, local or to the air, makes a take of a slice recording by itself.
/// </remarks>
public static class SliceBackupDoor
{
    /// <summary>Seam for tests: speech goes here instead of the screen reader when set.</summary>
    internal static Action<string, bool>? SpeakOverride { get; set; }

    /// <summary>
    /// Back up the recording on <paramref name="target"/>'s slice. Null when it
    /// never began (no radio, no computer audio); otherwise how it ended. Said
    /// aloud either way.
    /// </summary>
    /// <param name="afterNotBackedUp">
    /// A sentence the caller needs said when this does not end in a backup,
    /// carried at the end of the door's own sentence, in the same utterance
    /// (#1040). Spoken separately behind it, it was lost: the door's sentence
    /// interrupts, and the screen reader's cancel for that interrupt is carried
    /// out after the call that asked for it returns, so it is reported against
    /// the interrupting sentence and the speech queue then withdraws what waits
    /// behind it as if the operator had asked for quiet. Not carried by the
    /// tune refusal, which is the tune door's own sentence.
    /// </param>
    public static async Task<SliceBackupOutcome?> BackUpAsync(FlexBase? rig, SliceRecorderTarget target,
        string? afterNotBackedUp = null)
    {
        void Refuse(string sentence) => Say(WithFollower(sentence, afterNotBackedUp), refusal: true);

        // PLACEHOLDER: Codex writes (#999). Every sentence below is an audio.takes.backup.* key.
        if (rig == null)
        {
            Refuse(Lexicon.Get("audio.no_radio_connected"));
            return null;
        }
        if (SliceRecordingBackup.Running != null)
        {
            Refuse(Lexicon.Get("audio.takes.backup.busy"));
            return null;
        }
        // The capture reads the radio's audio on this computer, as a saved
        // playback does (Check your audio's needs_computer_audio).
        if (!rig.ReceiveAudioRunning || ComputerRecorder.Current is not ComputerFlacRecorder recorder)
        {
            Refuse(Lexicon.Get("audio.takes.backup.needs_computer_audio"));
            return null;
        }
        if (TuneDoors.Up(rig))
        {
            TuneDoors.RefuseStart();
            return null;
        }
        SliceBackupOutcome outcome;
        try
        {
            outcome = await SliceRecordingBackup.RunAsync(rig, target, ListenbackTakesRoot,
                (writer, slice) => recorder.StartTakeCapture(rig, writer, slice),
                started => Say(Lexicon.Get("audio.takes.backup.started", ("letter", started.Letter),
                    ("seconds", ((int)Math.Round(started.Seconds)).ToString(CultureInfo.CurrentCulture))), refusal: false));
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("SliceBackupDoor: " + ex, System.Diagnostics.TraceLevel.Error);
            Refuse(Lexicon.Get("audio.takes.backup.failed"));
            return null;
        }
        if (outcome.BackedUp) Say(Sentence(outcome), refusal: false);
        else Refuse(Sentence(outcome));
        return outcome;
    }

    /// <summary>The door's sentence, then the caller's, as one utterance (#1040). No words of its own.</summary>
    internal static string WithFollower(string sentence, string? follower) =>
        string.IsNullOrEmpty(follower) ? sentence : sentence + " " + follower;

    /// <summary>What a backup's ending says.</summary>
    public static string Sentence(SliceBackupOutcome outcome)
    {
        var letter = ("letter", (object)outcome.Letter);
        return outcome.Result switch
        {
            SliceBackupResult.BackedUp => Lexicon.Get("audio.takes.backup.done", letter),
            SliceBackupResult.NothingToBackUp => Lexicon.Get("audio.takes.backup.nothing", letter),
            SliceBackupResult.Transmitting => Lexicon.Get("audio.takes.backup.transmitting"),
            SliceBackupResult.Busy => Lexicon.Get("audio.takes.backup.busy"),
            SliceBackupResult.SliceBusy => Lexicon.Get("audio.takes.backup.slice_busy", letter),
            SliceBackupResult.PlayRefused => outcome.PlayRefusal?.Message is { Length: > 0 } refusal
                ? refusal : Lexicon.Get("audio.takes.backup.failed"),
            SliceBackupResult.Stopped => Lexicon.Get("audio.takes.backup.stopped", letter),
            SliceBackupResult.Incomplete => Lexicon.Get("audio.takes.backup.incomplete", letter),
            SliceBackupResult.RecordingChanged => Lexicon.Get("audio.takes.backup.changed", letter),
            _ => Lexicon.Get("audio.takes.backup.failed"),
        };
    }

    /// <summary>Stop the running backup. True when there was one.</summary>
    public static bool Stop() => SliceRecordingBackup.StopRunning();

    private static string ListenbackTakesRoot => Dialogs.ListenbackTakes.Root;

    private static void Say(string text, bool refusal)
    {
        if (SpeakOverride != null) { SpeakOverride(text, refusal); return; }
        if (refusal) EarconPlayer.LeaderInvalidTone();
        ScreenReaderOutput.Speak(text, SpeechIntent.Interrupt, refusal ? VerbosityLevel.Critical : VerbosityLevel.Terse,
            subject: SpeechSubject.SliceRecordingBackup);
    }
}
