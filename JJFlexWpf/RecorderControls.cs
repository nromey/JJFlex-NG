using System;
using System.Runtime.CompilerServices;
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
/// <b>Play never requests keying.</b> Slice playback joins an existing owned
/// transmission or listens locally with VOX off, using intent fixed at the press.
/// Computer playback still refuses while transmitting; its FLAC engine has
/// no direct route into the voice-message transmit stream.
/// </para>
/// </remarks>
public static class RecorderControls
{
    // DRAFT PROSE: a tone arm must fail closed if its VOX lease is unavailable.
    public const string ToneVoxPauseRefusal = "Test tone could not be armed while VOX is active.";

    public static string ToneArmed(int frequency, bool voxWasOn) =>
        Radios.ScreenReaderOutput.CurrentVerbosity == Radios.VerbosityLevel.Chatty
            ? $"Test tone armed, {frequency} hertz." + (voxWasOn ? " VOX off." : "")
            : "Tone armed.";

    public static string ToneDisarmed(bool voxRestored) =>
        "Test tone disarmed" + (voxRestored ? ", VOX on." : ".");
    public static bool StatusSpeechEnabled { get; set; } = true;
    private static readonly ConditionalWeakTable<FlexBase, object> ObservedRigs = new();

    public static void Observe(FlexBase? rig)
    {
        if (rig == null) return;
        ObservedRigs.GetValue(rig, r =>
        {
            r.SlicePlaybackFinished += outcome =>
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) Speak(outcome);
                else dispatcher.BeginInvoke(new Action(() => Speak(outcome)));
            };
            return new object();
        });
    }
    // DRAFT PROSE (#787): awaiting Noel. Shared by the strip, menu and buttons.
    public static string? RecordingLength(FlexBase? rig, SliceRecorderTarget target) =>
        rig?.SliceRecordingSeconds(target) is double seconds
            ? Lexicon.Get("audio.recorder.seconds", ("seconds", (int)Math.Floor(seconds))) : null;

    public static string RecordingSummary(FlexBase? rig, SliceRecorderTarget target)
    {
        string? length = RecordingLength(rig, target);
        if (rig?.IsSliceRecording(target) == true)
            return length == null ? Lexicon.Get("audio.recorder.has_recording")
                : Lexicon.Get("audio.recorder.recording_length", ("length", length));
        return length != null ? Lexicon.Get("audio.recorder.length_recorded", ("length", length))
            : Lexicon.Get(rig?.SliceHasRecording(target) == true ? "audio.recorder.has_recording" : "audio.recorder.no_recording");
    }

    public static string SlicePlayLabel(FlexBase? rig, SliceRecorderTarget target) =>
        rig?.IsSlicePlaying(target) == true
            ? Lexicon.Get("audio.recorder.stop_play_button", ("letter", rig.SliceRecorderLetter(target)))
            : RecordingLength(rig, target) is string length
                ? Lexicon.Get("audio.recorder.play_length", ("length", length)) : Lexicon.Get("audio.recorder.play_button");

    public static string SliceStripPlayLabel(FlexBase? rig, SliceRecorderTarget target)
    {
        if (rig?.IsSlicePlaying(target) == true) return Lexicon.Get("audio.recorder.stop_strip");
        if (rig?.SliceRecordingSeconds(target) is double seconds)
            return Lexicon.Get("audio.recorder.play_strip", ("seconds", (int)Math.Floor(seconds)));
        return Lexicon.Get("audio.recorder.play_button");
    }
    /// <summary>
    /// Speak a recorder outcome under its subject. A refusal thunks first and
    /// speaks at Critical, so it is heard at every verbosity; a success is
    /// Terse. Both interrupt, because each is the answer to the key just
    /// pressed.
    /// </summary>
    public static void Speak(RecorderOutcome outcome)
    {
        if (outcome == null) return;
        if (outcome.Success && !outcome.AlreadyInState)
        {
            if (outcome.Cue == RecorderCue.Start) EarconPlayer.RecorderStartBeep();
            else if (outcome.Cue == RecorderCue.Stop) EarconPlayer.RecorderStopBeep();
            else if (outcome.Cue == RecorderCue.PlayStop)
            {
                if (EarconPlayer.RecorderBeepsEnabled) EarconPlayer.ConfirmTone();
            }
        }
        if (string.IsNullOrEmpty(outcome.Message) || (outcome.Success && !StatusSpeechEnabled)) return;
        if (!outcome.Success) EarconPlayer.LeaderInvalidTone();
        ScreenReaderOutput.Speak(outcome.Message, SpeechIntent.Interrupt,
            outcome.Success ? VerbosityLevel.Terse : VerbosityLevel.Critical,
            subject: outcome.Subject);
    }

    /// <summary>Observe a teardown that has no control awaiting its result.</summary>
    public static async Task ReportStopFailureAsync(Task<RecorderOutcome> stop)
    {
        var result = await Guarded(() => stop, SpeechSubject.SliceRecorder(""));
        if (!result.Success) Speak(result);
    }

    private static RecorderOutcome NoRadio(string subject) =>
        new(false, Lexicon.Get("audio.no_radio_connected"), subject);

    /// <summary>The start click precedes a play request, including one the player later refuses.</summary>
    public static void PlayStartClick()
    {
        if (EarconPlayer.RecorderBeepsEnabled) EarconPlayer.ConfirmTone();
    }

    /// <summary>Let the 200 ms click finish before local audio enters the mixer.</summary>
    public static async Task PlayStartClickBeforeLocalAudioAsync()
    {
        if (!EarconPlayer.RecorderBeepsEnabled) return;
        EarconPlayer.ConfirmTone();
        await Task.Delay(240);
    }

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
        Observe(rig);
        if (rig != null && !rig.IsSlicePlaying(target)) PlayStartClick();
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
        bool starting = !ComputerRecorder.Current.IsPlaying;
        bool transmitting = rig?.Transmit == true;
        FlexBase.LocalPlaybackVoxPause? pause = null;
        if (starting && !transmitting)
        {
            // Tape Deck track 4 (#787): the speakers must not key the radio
            // through VOX. The same pause slice playback takes, through the
            // radio's one VOX owner; refused, like slice playback, when the
            // radio does not confirm VOX off. While transmitting the play is
            // refused below before any of this matters.
            pause = await LocalPlaybackVox.PauseAsync(rig);
            if (pause == null)
            {
                var refused = new RecorderOutcome(false, Lexicon.Get("audio.recorder.play_vox_unconfirmed"),
                    SpeechSubject.ComputerRecorder);
                (speak ?? Speak)(refused);
                return refused;
            }
        }
        if (starting)
            await PlayStartClickBeforeLocalAudioAsync();
        var outcome = await Guarded(() => ComputerRecorder.TogglePlaybackAsync(transmitting),
            SpeechSubject.ComputerRecorder);
        if (pause != null)
        {
            if (outcome.Success && ComputerRecorder.Current.IsPlaying) LocalPlaybackVox.HoldUntilPlaybackEnds(pause);
            else pause.Dispose();
        }
        (speak ?? Speak)(outcome);
        return outcome;
    }
}
