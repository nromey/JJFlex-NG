using System;
using System.Threading.Tasks;
using Radios;
using Radios.Speech;

namespace JJFlexWpf;

/// <summary>
/// The one place every recorder DOOR goes through (#787): JJ Flexible Home,
/// the Slice and Audio menus, the JJ key, the registry commands a flat key
/// can be bound to, and the Audio Workshop's Record slice and Play recording
/// buttons. Each door decides only WHICH slice; what happens and what is said
/// are decided here, so two doors cannot grow two vocabularies for one press.
/// </summary>
/// <remarks>
/// <para>
/// Two controls, not one with a source choice (Noel, 2026-10-21 11:00): the
/// radio's per-slice record and play, and a separate "record to file" and
/// "play from computer" for the computer. The radio half is
/// <see cref="FlexBase.ToggleSliceRecordAsync"/> and its siblings; the
/// computer half is <see cref="ComputerRecorder.Current"/>, whose engine is
/// Tape Deck track 3's and until it lands is a stub that refuses in a plain
/// sentence.
/// </para>
/// <para>
/// <b>Play never keys the radio, and both Plays are refused while
/// transmitting</b> (Noel, 2026-10-21 10:57: keyed, Play sending to air is
/// phase 2). The slice recorder enforces its own refusal; the computer
/// refusal is enforced in ComputerRecorder.TogglePlaybackAsync, before the engine is asked, so no engine can
/// forget it.
/// </para>
/// </remarks>
public static class RecorderControls
{
    /// <summary>
    /// Speak a recorder outcome under its subject. A refusal thunks first and
    /// speaks at Critical, so it is heard at every verbosity; a success is
    /// Terse. Both interrupt, because each is the answer to the key just
    /// pressed.
    /// </summary>
    public static void Speak(RecorderOutcome outcome)
    {
        if (outcome == null || string.IsNullOrEmpty(outcome.Message)) return;
        if (!outcome.Success) EarconPlayer.LeaderInvalidTone();
        ScreenReaderOutput.Speak(outcome.Message, SpeechIntent.Interrupt,
            outcome.Success ? VerbosityLevel.Terse : VerbosityLevel.Critical,
            subject: outcome.Subject);
    }

    private static RecorderOutcome NoRadio(string subject) =>
        new(false, Lexicon.Get("audio.no_radio_connected"), subject);

    /// <summary>
    /// Run one recorder request and turn anything it throws into a spoken
    /// refusal. Home's buttons are async void click handlers, and an exception
    /// escaping one of those takes the whole program down; a key that throws
    /// is a key that silently did nothing. Neither is acceptable for a control
    /// pressed every day, so the one door every door uses catches here.
    /// </summary>
    private static async Task<RecorderOutcome> Guarded(Func<Task<RecorderOutcome>> request, string subject)
    {
        try { return await request(); }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("RecorderControls: " + ex, System.Diagnostics.TraceLevel.Error);
            // DRAFT PROSE (#787): awaiting Noel
            return new(false, Lexicon.Get("audio.recorder.failed"), subject);
        }
    }

    /// <summary>Record on the target slice, or stop the recording it is making.</summary>
    public static async Task<RecorderOutcome> ToggleSliceRecordAsync(FlexBase? rig, SliceRecorderTarget target,
        Action<RecorderOutcome>? speak = null, object? owner = null)
    {
        var outcome = rig == null
            ? NoRadio(SpeechSubject.SliceRecorder(""))
            : await Guarded(() => rig.ToggleSliceRecordAsync(target, owner), SpeechSubject.SliceRecorder(""));
        (speak ?? Speak)(outcome);
        return outcome;
    }

    /// <summary>Play the target slice's recording, or stop it playing.</summary>
    public static async Task<RecorderOutcome> ToggleSlicePlayAsync(FlexBase? rig, SliceRecorderTarget target,
        Action<RecorderOutcome>? speak = null, object? owner = null)
    {
        var outcome = rig == null
            ? NoRadio(SpeechSubject.SliceRecorder(""))
            : await Guarded(() => rig.ToggleSlicePlayAsync(target, owner), SpeechSubject.SliceRecorder(""));
        (speak ?? Speak)(outcome);
        return outcome;
    }

    /// <summary>
    /// Start recording to a file on this computer, or stop the recording.
    /// Stopping needs no radio: a recording outlives the connection it began on.
    /// </summary>
    public static async Task<RecorderOutcome> ToggleComputerRecordAsync(FlexBase? rig,
        Action<RecorderOutcome>? speak = null)
    {
        var outcome = await Guarded(() => ComputerRecorder.ToggleRecordingAsync(rig), SpeechSubject.ComputerRecorder);
        (speak ?? Speak)(outcome);
        return outcome;
    }

    /// <summary>
    /// Play the last computer recording, or the one chosen in the recordings
    /// dialog, to the speakers; or stop it playing. Needs no radio. Refused
    /// while transmitting, in ComputerRecorder.TogglePlaybackAsync, before the engine is asked.
    /// </summary>
    public static async Task<RecorderOutcome> ToggleComputerPlayAsync(FlexBase? rig,
        Action<RecorderOutcome>? speak = null)
    {
        var outcome = await Guarded(() => ComputerRecorder.TogglePlaybackAsync(rig?.Transmit == true), SpeechSubject.ComputerRecorder);
        (speak ?? Speak)(outcome);
        return outcome;
    }
}
