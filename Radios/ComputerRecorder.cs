using System;
using System.Threading.Tasks;

namespace Radios;

/// <summary>
/// The computer recorder as the controls see it (#787): "record to file" and
/// "play from computer". Tape Deck track 2 owns the CONTROLS and calls this;
/// Tape Deck track 3 owns the ENGINE and implements it. PROPOSED by track 2
/// on 2026-10-22, to be agreed with track 3 through the orchestrator; until an
/// engine is installed, <see cref="ComputerRecorder.Current"/> is a stub that
/// refuses every request in a plain sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the controls rely on, and nothing more.</b> Two states to label a
/// button by, start and stop for each, and a path to play. Format, folder,
/// encoder, gap handling and disk-full behaviour are the engine's, and none of
/// them appear here, so track 3's open decisions (FLAC or not, 24 or 48 kHz,
/// the folder's name) change nothing on this side.
/// </para>
/// <para>
/// <b>Every method answers with a <see cref="RecorderOutcome"/></b> — the
/// same shape the radio's slice recorder answers with — so a door speaks a
/// computer outcome exactly as it speaks a slice outcome. A refusal is an
/// outcome with <c>Success</c> false and a complete sentence, never an
/// exception: an exception thrown into a key handler is a key that silently
/// did nothing. The subject is <see cref="Speech.SpeechSubject.ComputerRecorder"/>.
/// </para>
/// <para>
/// <b>Playback never transmits.</b> Play from computer plays to the speakers.
/// The controls refuse it while transmitting before the engine is asked
/// (Noel, 2026-10-21 10:57: keyed, Play sending to air is phase 2), so an
/// engine does not need its own transmit check, and must not key the radio.
/// </para>
/// <para>
/// <b>Threading.</b> The controls call from the UI thread and await. An engine
/// should return promptly and do its file work elsewhere; the receive audio
/// thread must never wait on a file (track 3's research report, "Tap point").
/// <see cref="StateChanged"/> may be raised on any thread.
/// </para>
/// </remarks>
public interface IComputerRecorder
{
    /// <summary>True while a recording to file is being made.</summary>
    bool IsRecording { get; }

    /// <summary>True while a computer recording is playing to the speakers.</summary>
    bool IsPlaying { get; }

    /// <summary>The newest finished recording, as a full path, or null when there is none.</summary>
    string LastRecordingPath { get; }

    /// <summary>
    /// Start recording what the radio plays through this computer. The rig is
    /// the connected radio whose receive audio is recorded; refuse with a
    /// sentence when it cannot be (no PC audio, no disk, no engine).
    /// </summary>
    Task<RecorderOutcome> StartRecordingAsync(FlexBase rig);

    /// <summary>Stop the recording and finish the file so it plays elsewhere.</summary>
    Task<RecorderOutcome> StopRecordingAsync();

    /// <summary>Play the recording at this path to the speakers. Never transmits.</summary>
    Task<RecorderOutcome> PlayAsync(string path);

    /// <summary>Stop the playback.</summary>
    Task<RecorderOutcome> StopPlaybackAsync();

    /// <summary>Raised when recording or playback starts or stops, including on its own (end of file, a full disk).</summary>
    event Action StateChanged;
}

/// <summary>
/// Where the controls find the computer recorder, and which recording "play
/// from computer" plays.
/// </summary>
public static class ComputerRecorder
{
    private static IComputerRecorder _current = new UnavailableComputerRecorder();

    /// <summary>The installed engine, or the refusing stub until one is installed.</summary>
    public static IComputerRecorder Current => _current;

    /// <summary>Raised after <see cref="Install"/> swaps the engine, so doors can re-subscribe.</summary>
    public static event Action Installed;

    /// <summary>
    /// Install the engine (track 3). Called once at startup; passing null puts
    /// the stub back, which is what tests do.
    /// </summary>
    public static void Install(IComputerRecorder engine)
    {
        _current = engine ?? new UnavailableComputerRecorder();
        Installed?.Invoke();
    }

    /// <summary>
    /// The recording chosen in the recordings dialog (track 4), or null for
    /// "the last one". Noel, 2026-10-21 09:25: play from computer plays the
    /// last recording, or whatever file you select in the dialog. Kept for
    /// this run of the program only; whether a choice should survive a
    /// restart is the dialog's question.
    /// </summary>
    public static string ChosenRecording { get; set; }

    /// <summary>What play from computer plays now: the chosen recording, else the last one, else null.</summary>
    public static string ChosenForPlayback() =>
        string.IsNullOrEmpty(ChosenRecording) ? _current.LastRecordingPath : ChosenRecording;

    private static RecorderOutcome Refusal(string key) =>
        new(false, Lexicon.Get(key), Speech.SpeechSubject.ComputerRecorder);

    /// <summary>
    /// "Record to file": start recording, or stop the recording that is
    /// running. Stopping needs no radio, because a recording outlives the
    /// connection it began on; starting needs the radio whose audio it records.
    /// </summary>
    public static async Task<RecorderOutcome> ToggleRecordingAsync(FlexBase rig)
    {
        var recorder = _current;
        if (recorder.IsRecording) return await recorder.StopRecordingAsync().ConfigureAwait(false);
        if (rig == null) return Refusal("audio.no_radio_connected");
        return await recorder.StartRecordingAsync(rig).ConfigureAwait(false);
    }

    /// <summary>
    /// "Play from computer": play the chosen or last recording to the
    /// speakers, or stop the one playing. Needs no radio. Refused while
    /// transmitting BEFORE the engine is asked (Noel, 2026-10-21 10:57: keyed,
    /// Play sending to air is phase 2), so no engine can forget the refusal.
    /// </summary>
    public static async Task<RecorderOutcome> TogglePlaybackAsync(bool transmitting)
    {
        var recorder = _current;
        if (recorder.IsPlaying) return await recorder.StopPlaybackAsync().ConfigureAwait(false);
        // DRAFT PROSE (#787): awaiting Noel
        if (transmitting) return Refusal("audio.recorder.computer_play_refused_transmitting");
        return await recorder.PlayAsync(ChosenForPlayback()).ConfigureAwait(false);
    }
}

/// <summary>
/// The computer recorder before Tape Deck track 3's engine is installed. It
/// refuses everything in a complete sentence and changes nothing.
/// </summary>
internal sealed class UnavailableComputerRecorder : IComputerRecorder
{
    public bool IsRecording => false;
    public bool IsPlaying => false;
    public string LastRecordingPath => null;

    // DRAFT PROSE (#787): awaiting Noel
    private static Task<RecorderOutcome> Refuse(string key) =>
        Task.FromResult(new RecorderOutcome(false, Lexicon.Get(key), Speech.SpeechSubject.ComputerRecorder));

    public Task<RecorderOutcome> StartRecordingAsync(FlexBase rig) => Refuse("audio.recorder.computer_record_unavailable");
    public Task<RecorderOutcome> StopRecordingAsync() => Refuse("audio.recorder.computer_record_unavailable");
    public Task<RecorderOutcome> PlayAsync(string path) => Refuse("audio.recorder.computer_play_unavailable");
    public Task<RecorderOutcome> StopPlaybackAsync() => Refuse("audio.recorder.computer_play_unavailable");

    public event Action StateChanged { add { } remove { } }
}
