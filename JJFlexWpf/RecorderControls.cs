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
/// <b>Play never requests keying, except under the VOX send box (#835).</b> Slice
/// playback joins an existing owned transmission or listens locally with VOX
/// off, using intent fixed at the press. Computer playback, keyed, now sends
/// to the air through the voice message's transmit-file path
/// (<see cref="VoiceMessageSender.SendComputerRecording"/>). With the box
/// ticked and VOX on, both key the radio themselves through the one box send.
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
    /// pressed. A refusal under tune is the exception: it is Terse, the way
    /// every tune door tells it (ruled by Noel 2026-11-01 13:06, verbosity
    /// gated for tone and text).
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
        if (outcome.Cue == RecorderCue.TuneRefused) EarconPlayer.ATUFailTone();
        else if (!outcome.Success) EarconPlayer.LeaderInvalidTone();
        ScreenReaderOutput.Speak(outcome.Message, SpeechIntent.Interrupt,
            outcome.Success || outcome.Cue == RecorderCue.TuneRefused ? VerbosityLevel.Terse : VerbosityLevel.Critical,
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
        // Nothing records under tune (#860). A recording already running can
        // always be stopped.
        if (rig != null && !rig.IsSliceRecording(target) && TuneDoors.Up(rig))
        {
            TuneDoors.RefuseStart();
            return new(false, "", SpeechSubject.SliceRecorder(""));
        }
        // A recording made while the radio is transmitting at 0 watts on a rig
        // that records nothing there (#859): a hardware push to talk, which we
        // cannot refuse, so this press is. In receive this door records at any
        // power, and it keys nothing. Only the menu passes a speaker of its own.
        if (rig != null && (rig.Transmit || rig.RadioInterlockReportsTransmit) && !rig.IsSliceRecording(target)
            && ZeroPowerDoors.RefuseTransmit(rig, "a slice recording", afterMenu: speak != null))
            return new(false, "", SpeechSubject.SliceRecorder(""));
        // #999: a recording about to start over one already in the slice asks
        // first. Only when starting; a running recording always stops at once.
        if (rig != null && !rig.IsSliceRecording(target))
        {
            var answered = await ConfirmOverwriteAsync(rig, target);
            if (!answered.Proceed) return new(false, "", SpeechSubject.SliceRecorder(""));
            if (answered.Asked)
            {
                // The question was open for a while; say no to what is no longer safe.
                if (TuneDoors.Up(rig))
                {
                    TuneDoors.RefuseStart();
                    return new(false, "", SpeechSubject.SliceRecorder(""));
                }
                if ((rig.Transmit || rig.RadioInterlockReportsTransmit)
                    && ZeroPowerDoors.RefuseTransmit(rig, "a slice recording", afterMenu: speak != null))
                    return new(false, "", SpeechSubject.SliceRecorder(""));
                target = answered.Target;
            }
        }
        var outcome = rig == null
            ? NoRadio(SpeechSubject.SliceRecorder(""))
            : await Guarded(() => rig.ToggleSliceRecordAsync(target, owner), SpeechSubject.SliceRecorder(""));
        (speak ?? Speak)(outcome);
        return outcome;
    }

    // ── The overwrite warning (#999) ──

    /// <summary>The question the overwrite warning asks: its form, the slice and the recording's length.</summary>
    public sealed record SliceOverwriteQuestion(bool BackedUp, string Letter, double Seconds);

    /// <summary>What <see cref="ConfirmOverwriteAsync"/> decided: go ahead or not, whether it asked, and the slice to record on.</summary>
    public readonly record struct OverwriteGo(bool Proceed, bool Asked, SliceRecorderTarget Target);

    /// <summary>
    /// Seam for tests: answers the warning instead of showing it, so no window
    /// is ever built. Null in the app.
    /// </summary>
    internal static Func<SliceOverwriteQuestion, Dialogs.SliceOverwriteReply>? AskOverride { get; set; }

    /// <summary>Seam for tests: runs Back up first instead of <see cref="SliceBackupDoor"/>.</summary>
    internal static Func<FlexBase, SliceRecorderTarget, Task<Radios.Listenback.SliceBackupOutcome?>>? BackUpOverride { get; set; }

    /// <summary>Seam for tests: what the radio would report, instead of asking a radio. Null in the app.</summary>
    internal static Func<FlexBase, SliceRecorderTarget, SliceOverwriteFacts?>? FactsOverride { get; set; }

    private static SliceOverwriteFacts? FactsFor(FlexBase rig, SliceRecorderTarget target) =>
        FactsOverride != null ? FactsOverride(rig, target) : rig.SliceOverwriteFactsFor(target);

    private static bool _asking;

    /// <summary>True when starting a recording on <paramref name="target"/> would ask first.</summary>
    public static bool NeedsOverwriteQuestion(FlexBase? rig, SliceRecorderTarget target) =>
        rig != null && SliceOverwriteGate.Decide(FactsFor(rig, target)) != SliceOverwriteDecision.RecordNow;

    /// <summary>
    /// Ask before a recording starts over the one already in the slice (#999),
    /// through <see cref="SliceOverwriteGate"/>: nothing asked when the slice is
    /// empty or the warning is turned off. Record goes ahead; Back up first backs
    /// the recording up and then goes ahead, on the same slice, only if the
    /// backup succeeded; Cancel and Escape go no further and say nothing. The
    /// warning's "don't show this again" turns the setting off, and only when
    /// the operator went ahead.
    /// </summary>
    /// <remarks>
    /// Shared by every door that starts a slice recording (F11, the JJ key, Home's
    /// slice row and the Slice menu, through <see cref="ToggleSliceRecordAsync"/>)
    /// and by Check your audio before its Radio recording method starts. While the
    /// warning is open, another request to start a recording answers nothing: it
    /// neither opens a second warning nor answers this one.
    /// </remarks>
    /// <param name="owner">The window the warning opens over; the main window when null.</param>
    public static async Task<OverwriteGo> ConfirmOverwriteAsync(FlexBase rig, SliceRecorderTarget target, System.Windows.Window? owner = null)
    {
        var facts = FactsFor(rig, target);
        var decision = SliceOverwriteGate.Decide(facts);
        if (decision == SliceOverwriteDecision.RecordNow) return new(true, false, target);
        if (_asking) return new(false, true, target);
        var question = new SliceOverwriteQuestion(decision == SliceOverwriteDecision.WarnBackedUp, facts!.Letter,
            facts.RadioSeconds ?? 0);
        Dialogs.SliceOverwriteReply reply;
        _asking = true;
        try { reply = Ask(question, owner); }
        finally { _asking = false; }
        JJTrace.Tracing.TraceLine("SliceOverwrite: slice " + question.Letter + " backedUp=" + question.BackedUp
            + " answer=" + reply.Answer + " dontShowAgain=" + reply.DontShowAgain, System.Diagnostics.TraceLevel.Info);
        // Cancel and Escape say nothing (#999, question C3): Escape on a
        // "Replace the recording?" question already means don't, and Home's own
        // announcement of the returning focus says where the operator is.
        if (reply.Answer == Dialogs.SliceOverwriteAnswer.Cancel) return new(false, true, target);
        if (reply.DontShowAgain && !RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(facts.Serial, false))
            JJTrace.Tracing.TraceLine("SliceOverwrite: the warning setting could not be saved", System.Diagnostics.TraceLevel.Warning);
        // The same slice the warning named, whichever becomes active meanwhile.
        var slice = SliceRecorderTarget.Letter(question.Letter[0]);
        if (reply.Answer == Dialogs.SliceOverwriteAnswer.BackUpFirst)
        {
            var backup = BackUpOverride != null ? await BackUpOverride(rig, slice) : await SliceBackupDoor.BackUpAsync(rig, slice);
            // A backup that did not finish leaves the recording unprotected, so
            // nothing records over it; the backup door has said why.
            if (backup?.BackedUp != true) return new(false, true, target);
        }
        return new(true, true, slice);
    }

    private static Dialogs.SliceOverwriteReply Ask(SliceOverwriteQuestion question, System.Windows.Window? owner)
    {
        if (AskOverride != null) return AskOverride(question);
        var dialog = new Dialogs.SliceOverwriteWarningDialog(question.BackedUp, question.Letter, question.Seconds);
        if (owner != null) dialog.Owner = owner;
        dialog.ShowModalDialog();
        return dialog.Reply;
    }

    /// <summary>
    /// A Play press that a send now answers (#835): the send says what it is
    /// doing itself, under the message key's subject, so the door says nothing.
    /// </summary>
    private static RecorderOutcome HandedToSend(string subject) => new(true, "", subject);

    /// <summary>
    /// Play the target slice's recording, or stop it playing.
    /// </summary>
    /// <remarks>
    /// <b>The VOX send box (#835).</b> With the box ticked and VOX on, Play IS the
    /// send (Noel, 2026-10-27 15:22): the radio is keyed, the recording goes
    /// out whole and the operator hears it, then VOX comes back. Not for a door
    /// that tags its work (<paramref name="owner"/>, the Audio Workshop's
    /// listen-back buttons), whose Play is a check you listen to, never a send.
    /// Pressed again while the send is going out, Play stops it.
    /// </remarks>
    public static async Task<RecorderOutcome> ToggleSlicePlayAsync(FlexBase? rig, SliceRecorderTarget target,
        Action<RecorderOutcome>? speak = null, object? owner = null)
    {
        if (owner == null && VoiceMessageSender.InFlightFromPlay)
        {
            VoiceMessageSender.Stop();
            return HandedToSend(SpeechSubject.SliceRecorder(""));
        }
        if (owner == null && rig != null && !rig.IsSlicePlaying(target)
            && VoiceMessageSender.RouteFor(rig) == SendRoute.Box)
        {
            VoiceMessageSender.SendSliceRecording(rig, target);
            return HandedToSend(SpeechSubject.SliceRecorder(""));
        }
        Observe(rig);
        // It still plays under tune, as it always has, and says at the press
        // that it will not go out (#860, Noel 2026-11-01 08:04).
        bool underTune = rig != null && !rig.IsSlicePlaying(target) && TuneDoors.Up(rig);
        if (rig != null && !rig.IsSlicePlaying(target)) PlayStartClick();
        var outcome = rig == null
            ? NoRadio(SpeechSubject.SliceRecorder(""))
            : await Guarded(() => rig.ToggleSlicePlayAsync(target, owner), SpeechSubject.SliceRecorder(""));
        outcome = TuneDoors.ForPlay(outcome, underTune);
        (speak ?? Speak)(outcome);
        return outcome;
    }

    /// <summary>
    /// Listen to the target slice's recording, never as a send (#999, repair
    /// round 1): Play takes' slice rows. Refused while the radio transmits; the
    /// local intent is carried to the radio command, so a key after the press
    /// cannot put it on the air. Stopping a running playback is always allowed.
    /// </summary>
    public static async Task<RecorderOutcome> ToggleSliceListenAsync(FlexBase? rig, SliceRecorderTarget target,
        Action<RecorderOutcome>? speak = null, object? owner = null)
    {
        Observe(rig);
        bool starting = rig != null && !rig.IsSlicePlaying(target);
        if (starting && !(rig!.Transmit || rig.RadioInterlockReportsTransmit || TuneDoors.Up(rig))) PlayStartClick();
        var outcome = rig == null
            ? NoRadio(SpeechSubject.SliceRecorder(""))
            : await Guarded(() => rig.ToggleSliceListenAsync(target, owner), SpeechSubject.SliceRecorder(""));
        (speak ?? Speak)(outcome);
        return outcome;
    }

    /// <summary>
    /// The slice's playback into a transmission the operator is holding, as
    /// Play did before the box (#835): the box send hands back to this when it
    /// finds the radio held by someone other than VOX once VOX is off.
    /// </summary>
    internal static async Task<RecorderOutcome> StartSliceOnAirAsync(FlexBase rig, SliceRecorderTarget target)
    {
        Observe(rig);
        var outcome = await Guarded(() => rig.StartSlicePlayAsync(target), SpeechSubject.SliceRecorder(""));
        Speak(outcome);
        return outcome;
    }

    /// <summary>
    /// Play from computer pressed while a recording plays on the speakers
    /// (#835 fix round; Noel, 2026-10-28 21:20): pause it where it is, or carry
    /// a paused one on from there. The same words the Recorded QSOs window
    /// says for the same act. VOX comes back while it is paused, and carrying
    /// on pauses VOX again before it sounds, as for that window's preview
    /// (<see cref="LocalPlaybackVox"/>; Noel, 2026-10-29 23:58).
    /// </summary>
    private static async Task<RecorderOutcome> PauseOrCarryOnSpeakersAsync()
    {
        if (RecordingPlayer.IsPaused)
        {
            // VOX came back while the recording was paused; carrying on pauses
            // it again, confirmed by the radio, before a sample sounds (#835,
            // fix round 2026-10-30; Noel, 2026-10-29 23:58).
            double at = RecordingPlayer.PositionSeconds ?? 0;
            // DRAFT PROSE (#835): awaiting Noel (the two refusals)
            return await LocalPlaybackVox.CarryOnAsync() switch
            {
                LocalPlaybackVox.CarryOn.Resumed => new(true,
                    TuneDoors.ForPlay(Lexicon.Get("audio.qsos.resumed", ("position", RecordedQsoWords.Position(at, null))),
                        TuneDoors.Up(LocalPlaybackVox.PausedRig)),
                    SpeechSubject.ComputerRecorder),
                LocalPlaybackVox.CarryOn.Transmitting => new(false,
                    Lexicon.Get("audio.qsos.resume_refused_transmitting"), SpeechSubject.ComputerRecorder),
                LocalPlaybackVox.CarryOn.VoxUnconfirmed => new(false,
                    Lexicon.Get("audio.qsos.resume_vox_unconfirmed"), SpeechSubject.ComputerRecorder),
                // A carry-on already under way: this press adds nothing, and says nothing.
                LocalPlaybackVox.CarryOn.Busy => new(true, "", SpeechSubject.ComputerRecorder),
                // The playback, radio or connection it was for changed while the
                // radio confirmed VOX off (Codex round 1030, B4); whatever changed
                // it has spoken for itself, and nothing resumed.
                LocalPlaybackVox.CarryOn.Superseded => new(true, "", SpeechSubject.ComputerRecorder),
                _ => new(false, Lexicon.Get("audio.recorder.failed"), SpeechSubject.ComputerRecorder),
            };
        }
        if (!RecordingPlayer.Pause())
            return new(false, Lexicon.Get("audio.recorder.failed"), SpeechSubject.ComputerRecorder);
        double pausedAt = RecordingPlayer.PositionSeconds ?? 0;
        return new(true, Lexicon.Get("audio.qsos.paused", ("position", RecordedQsoWords.Position(pausedAt, null))),
            SpeechSubject.ComputerRecorder);
    }

    /// <summary>
    /// Escape in the main window while a computer recording plays or is paused
    /// on the speakers: stop it (#835 fix round; Noel, 2026-10-28 21:20). True
    /// when there was one to stop. A send to the air has its own Escape, asked
    /// first (<see cref="VoiceMessageSender.InFlight"/>).
    /// </summary>
    internal static bool StopSpeakersOnEscape()
    {
        if (!ComputerRecorder.Current.IsPlaying) return false;
        _ = StopSpeakersAsync();
        return true;
    }

    private static async Task StopSpeakersAsync()
    {
        var outcome = await Guarded(() => ComputerRecorder.Current.StopPlaybackAsync(), SpeechSubject.ComputerRecorder);
        Speak(outcome);
    }

    /// <summary>
    /// Start recording to a file on this computer, or stop the recording.
    /// Stopping needs no radio: a recording outlives the connection it began on.
    /// </summary>
    public static async Task<RecorderOutcome> ToggleComputerRecordAsync(FlexBase? rig,
        Action<RecorderOutcome>? speak = null)
    {
        // Nothing records under tune (#860); a running recording can always stop.
        if (!ComputerRecorder.Current.IsRecording && TuneDoors.Up(rig))
        {
            TuneDoors.RefuseStart();
            return new(false, "", SpeechSubject.ComputerRecorder);
        }
        var outcome = await Guarded(() => ComputerRecorder.ToggleRecordingAsync(rig), SpeechSubject.ComputerRecorder);
        (speak ?? Speak)(outcome);
        return outcome;
    }

    /// <summary>
    /// Play the last computer recording, or the one chosen in the recordings
    /// dialog, to the speakers; or stop it playing. Needs no radio. While
    /// transmitting it is a send to the air instead, routed before the engine
    /// is asked, so ComputerRecorder.TogglePlaybackAsync's refusal while
    /// transmitting stays as the engine's own guard and is no longer what this
    /// door says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tape Deck phase 2 (#835).</b> Keyed, Play sends the chosen or last
    /// recording to the air through the transmit-file path, and never touches
    /// the key (Noel, 2026-10-21 10:57). With the VOX send box ticked and VOX on, Play
    /// keys the radio itself, sends it whole and hands back to VOX (2026-10-27
    /// 14:54). Either way the operator hears it through the radio's transmit
    /// monitor, switched on for the send if it was off and put back after
    /// (2026-10-28 16:47). Unkeyed with the box unticked, Play listens, as it
    /// always has.
    /// </para>
    /// <para>
    /// Pressed again while the send is going out, Play PAUSES it, unkeying only
    /// what the send keyed and holding its place; pressed once more it carries
    /// on from there (Noel, 2026-10-27 20:51: <i>"That's how you pause
    /// normally"</i>). Escape ends it. At ten minutes the send pauses by itself
    /// so the operator can identify (20:48, 20:50).
    /// </para>
    /// <para>
    /// <b>On the speakers too</b> (2026-10-28 21:20): pressed while a recording
    /// plays here, Play pauses it, and pressed again carries it on; Escape in
    /// the main window stops it (<see cref="StopSpeakersOnEscape"/>).
    /// </para>
    /// </remarks>
    public static async Task<RecorderOutcome> ToggleComputerPlayAsync(FlexBase? rig,
        Action<RecorderOutcome>? speak = null)
    {
        // A send in hand answers first (#835, 20:51): a computer recording
        // going out pauses, a paused one carries on, any other send stops.
        if (VoiceMessageSender.AnswerPlayFromComputer())
            return HandedToSend(SpeechSubject.ComputerRecorder);
        // A recording paused on the speakers carries on the way a press now
        // would go (Noel, 2026-10-30 05:56: "If you've paused and you turn send
        // on, say you didn't realize you did, it should send"): the box ticked
        // with VOX on, or the operator keyed, sends it from where it paused.
        if (rig != null && RecordingPlayer.IsPaused
            && VoiceMessageSender.SendPausedSpeakerPlayback(rig, VoiceMessageSender.RouteFor(rig)))
            return HandedToSend(SpeechSubject.ComputerRecorder);
        // On the speakers too, Play pauses and Play again carries on; Escape
        // stops (Noel, 2026-10-28 21:20: one meaning for the key everywhere).
        if (ComputerRecorder.Current.IsPlaying)
        {
            var paused = await PauseOrCarryOnSpeakersAsync();
            (speak ?? Speak)(paused);
            return paused;
        }
        bool starting = !ComputerRecorder.Current.IsPlaying;
        if (starting && rig != null)
        {
            SendRoute route = VoiceMessageSender.RouteFor(rig);
            if (route != SendRoute.AsBefore)
            {
                string? path = ComputerRecorder.ChosenForPlayback();
                if (string.IsNullOrEmpty(path))
                {
                    // DRAFT PROSE (#835): awaiting Noel
                    var none = new RecorderOutcome(false, Lexicon.Get("audio.send.no_computer_recording"),
                        SpeechSubject.ComputerRecorder);
                    (speak ?? Speak)(none);
                    return none;
                }
                VoiceMessageSender.SendComputerRecording(rig, path, route);
                return HandedToSend(SpeechSubject.ComputerRecorder);
            }
        }
        // Under tune RouteFor said AsBefore, so this is the speakers, with the
        // same VOX pause as any speaker playback: a carrier is not the operator
        // talking, and the speakers must not key VOX when the tune ends (#860).
        bool underTune = starting && TuneDoors.Up(rig);
        bool transmitting = rig?.Transmit == true && !underTune;
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
            if (outcome.Success && ComputerRecorder.Current.IsPlaying) LocalPlaybackVox.HoldUntilPlaybackEnds(rig, pause);
            else pause.Dispose();
        }
        outcome = TuneDoors.ForPlay(outcome, underTune);
        (speak ?? Speak)(outcome);
        return outcome;
    }
}
