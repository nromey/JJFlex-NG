using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using JJPortaudio;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// The one place a microphone recording can be started or stopped, and the
/// one voice that says so (Sprint 33 Track I).
/// </summary>
/// <remarks>
/// <para>
/// RECORDING IS NEVER SILENT AND NEVER INCIDENTAL. A microphone that writes
/// files is exactly the kind of feature that must not be able to surprise
/// anybody, so this class exists to make the announcement impossible to skip:
/// nothing else in the application opens <see cref="MicRecorder"/>. Every
/// surface that wants to record — the Audio Workshop, Settings, a leader key,
/// and the station-message library that will want the same thing next — goes
/// through here, which means "it always says it started, it always says it
/// stopped" is a property of the code rather than a promise that each new
/// caller has to keep.
/// </para>
/// <para>
/// What it announces, and why each part is there: an earcon and a spoken
/// sentence at the start, so a recording cannot begin unheard; the elapsed
/// time at intervals while it runs, so a recording cannot be left running
/// unnoticed; a warning as the length cap approaches, so an operator is not
/// cut off mid-sentence without warning; and an earcon plus a spoken result at
/// the end, saying how long it is and where it went. A recorder that stopped
/// quietly would be as bad as one that started quietly.
/// </para>
/// <para>
/// It refuses OUT LOUD rather than failing quietly — no microphone configured,
/// no microphone present, Windows microphone privacy switched off, a disk that
/// will not take the file. A blind operator pressing record and hearing
/// nothing has no way to tell "recording" from "broken", and that ambiguity is
/// the failure this whole design is arranged to prevent.
/// </para>
/// <para>
/// Built on the shape <see cref="NoiseCaptureNarrator"/> established for the
/// noise capture: a static single entry point, a Toggle that makes "press it
/// again to stop" true everywhere, a StateChanged event so buttons can rename
/// themselves honestly, and a dispatcher tick that does the talking. Two
/// capture features with two different spoken behaviours would be a worse
/// application than one with a shared spine.
/// </para>
/// </remarks>
public static class RecordingNarrator
{
    /// <summary>
    /// Set by MainWindow: where the audio device selection is stored, so the
    /// recorder uses the microphone the operator already chose rather than
    /// asking again.
    /// </summary>
    public static Func<string?>? AudioDevicesPath { get; set; }

    /// <summary>
    /// Fired when a recording starts or stops, so buttons can rename
    /// themselves honestly ("Record" versus "Stop recording").
    /// </summary>
    public static event Action? StateChanged;

    /// <summary>
    /// Fired when a recording finishes with a usable file, carrying its path.
    /// The surface that started it decides what the file now means — a
    /// reference take, a station message, an attachment.
    /// </summary>
    public static event Action<string>? RecordingSaved;

    private static MicRecorder? _recorder;
    private static DispatcherTimer? _timer;
    private static string _purpose = "";
    private static string _path = "";
    private static DateTime _startedLocal;
    private static int _lastSpokenInterval;
    private static bool _capWarned;

    /// <summary>
    /// How often the elapsed time is spoken while recording, in seconds.
    /// Long enough not to talk over somebody recording a message, short enough
    /// that a forgotten recorder announces itself well before the cap.
    /// </summary>
    private const int ProgressIntervalSeconds = 30;

    /// <summary>Seconds before the cap at which the operator is warned.</summary>
    private const int CapWarningSeconds = 30;

    /// <summary>True while a recording is in flight.</summary>
    public static bool IsRunning => _recorder != null && _recorder.IsRunning;

    /// <summary>Seconds recorded so far; zero when nothing is running.</summary>
    public static double ElapsedSeconds => _recorder?.Seconds ?? 0.0;

    /// <summary>
    /// The file the recording in flight is being written to, or empty.
    /// </summary>
    public static string CurrentPath => IsRunning ? _path : "";

    /// <summary>
    /// Start a recording, or stop the one in flight — the single entry point
    /// every surface calls, so "press it again to stop" is true everywhere.
    /// </summary>
    /// <param name="purpose">
    /// What this recording is for, in the operator's words, spoken in the
    /// announcements: "reference take", "station message". Keep it a short
    /// noun phrase; it is read inside a sentence.
    /// </param>
    /// <param name="path">
    /// Where to write it. Null or empty takes the timestamped default in the
    /// recordings folder.
    /// </param>
    public static void Toggle(string purpose, string? path = null)
    {
        if (IsRunning) Stop();
        else Start(purpose, path);
    }

    /// <summary>
    /// Begin a recording, announcing it. Refuses out loud when it cannot work.
    /// </summary>
    public static void Start(string purpose, string? path = null)
    {
        if (IsRunning) return;

        _purpose = string.IsNullOrWhiteSpace(purpose)
            ? Lexicon.Get("audio.recording.default_purpose")
            : purpose.Trim();

        if (!Open(path, quiet: false)) return;

        EarconPlayer.ConfirmTone();
        Speak(Lexicon.Get("audio.recording.started", ("purpose", _purpose)), interrupt: true);
    }

    /// <summary>
    /// Begin a recording for a Check your audio method, which has ALREADY
    /// announced it: its countdown has been said in full before this is
    /// called, and the caller sounds the check's start tone the moment this
    /// returns (Sprint 49 Track Q2, Noel 2026-11-19 12:22: <i>"start recording
    /// after speech is done, start it with a beep or count it down like we've
    /// done in all the other stuff"</i>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is spoken while this records.</b> The take is the operator's
    /// voice, and on speakers the microphone hears the screen reader: a
    /// sentence said as capture opens, or the elapsed time said half a minute
    /// in, would be in the take. So there is no start sentence and no confirm
    /// tone here (the check's countdown and its start tone are the
    /// announcement, as for the computer microphone check), and neither the
    /// elapsed time nor the length-cap warning is spoken while it runs. The
    /// window it is started from shows that it is recording and stops it on
    /// Start, Ctrl+Enter or Escape, so it cannot run on unnoticed.
    /// </para>
    /// <para>
    /// <b>Everything else is <see cref="Start"/>'s, unchanged:</b> the same
    /// refusals, said aloud (no capture has opened, so they cannot be in a
    /// take), and the same stop, with its tone and its sentence, said once the
    /// file is closed. The record key's recording is untouched by this.
    /// </para>
    /// </remarks>
    /// <param name="path">Where to write it.</param>
    /// <param name="holdFileStart">
    /// True when a start tone follows: the FILE then waits for
    /// <see cref="BeginCheckFileAt"/>, which the caller gives once the tone is
    /// audibly over (Sprint 49 Track Q3, Noel 2026-11-19 20:29: <i>"the
    /// microphone may open while the tone plays, but the file begins once the
    /// tone has finished"</i>; repair 1). False begins the file as the
    /// microphone opens.
    /// </param>
    /// <param name="stoppedTooSoon">
    /// What to say, given the seconds kept, when the recording is stopped with
    /// the microphone working but nothing, or too little, kept to make a file:
    /// the check's own words for a take that is too short (repair 1). Null
    /// says the general "nothing was captured, check the microphone", which is
    /// true only when the microphone delivered nothing.
    /// </param>
    /// <returns>True when the microphone is open and recording to <paramref name="path"/>.</returns>
    public static bool StartForCheck(string path, bool holdFileStart, Func<double, string>? stoppedTooSoon = null)
    {
        if (IsRunning || string.IsNullOrWhiteSpace(path)) return false;
        _purpose = "";
        if (!Open(path, quiet: true, holdFileStart)) return false;
        _stoppedTooSoon = stoppedTooSoon;
        return true;
    }

    /// <summary>
    /// For the check recording to <paramref name="path"/>, started with a held
    /// file start: the file begins at the frame the microphone captured at
    /// <paramref name="stopwatchTimestamp"/>, or, when null, at the first
    /// frame captured. Any thread; ignored for any other recording.
    /// </summary>
    public static void BeginCheckFileAt(string path, long? stopwatchTimestamp)
    {
        var recorder = _recorder;
        if (recorder == null || !recorder.IsRunning || !_quiet) return;
        if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
        recorder.BeginFileAt(stopwatchTimestamp);
    }

    /// <summary>True while the recording in flight was started for a check, so nothing is spoken until it stops.</summary>
    private static bool _quiet;

    /// <summary>The check's words for a recording stopped too soon to keep a file; null for the record key.</summary>
    private static Func<double, string>? _stoppedTooSoon;

    /// <summary>What a stop says, chosen by <see cref="StopKind"/>.</summary>
    internal enum StopSaying
    {
        /// <summary>The recorder faulted: say why.</summary>
        Faulted,
        /// <summary>The microphone delivered nothing: check it.</summary>
        NothingCaptured,
        /// <summary>The microphone worked, but the recording was stopped before enough of it was kept: the check's own words.</summary>
        StoppedTooSoon,
        /// <summary>A file was kept: say its length and name.</summary>
        Saved,
    }

    /// <summary>
    /// Which sentence a stop says (Track Q3 repair 1). "Nothing was captured,
    /// check the microphone" is said only for a genuine capture fault; a check
    /// recording stopped before its file began, or with too little kept, says
    /// the check's own words instead, because the operator stopped it, not the
    /// microphone.
    /// </summary>
    /// <remarks>
    /// <b>A stop during the held lead (repair 2, Codex's re-check of ca39306c,
    /// item 3).</b> The microphone reports itself open before its first block
    /// is read, so a stop pressed at once can come before anything was
    /// delivered. When the file was still held for a start that had not come
    /// (<paramref name="stoppedDuringLead"/>), that stop is the operator's
    /// even with nothing captured. A microphone that delivered nothing past
    /// the moment the file was to begin, or with no lead at all, is still a
    /// capture fault and still says so.
    /// </remarks>
    internal static StopSaying StopKind(bool faulted, bool fileSaved, double capturedSeconds, bool checkHasWords,
        bool stoppedDuringLead)
    {
        if (faulted) return StopSaying.Faulted;
        if (fileSaved) return StopSaying.Saved;
        return checkHasWords && (capturedSeconds > 0 || stoppedDuringLead) ? StopSaying.StoppedTooSoon : StopSaying.NothingCaptured;
    }

    /// <summary>
    /// Resolve the microphone, refuse aloud if it cannot record, and open the
    /// recorder. Says nothing on success; the caller announces. With
    /// <paramref name="holdFileStart"/> the file waits for
    /// <see cref="BeginCheckFileAt"/> (never for the record key: everything is kept).
    /// </summary>
    private static bool Open(string? path, bool quiet, bool holdFileStart = false)
    {
        // The microphone the operator already chose. Asking again here would
        // be a second place to configure the same thing, and the two would
        // drift apart the first time somebody changed one of them.
        Devices.DeviceInfo? device = ResolveMicrophone(out string trouble);
        if (device == null)
        {
            Refuse(trouble);
            return false;
        }

        // Windows can be handing desktop applications silence while its own
        // Sound Recorder works perfectly — the single most confusing state a
        // microphone can be in. Say so BEFORE recording rather than handing
        // somebody a file of digital zeroes afterwards.
        var access = MicrophonePrivacy.Check(out string privacyExplanation);
        if (MicrophonePrivacy.IsBlocked(access))
        {
            Refuse(privacyExplanation);
            return false;
        }

        _startedLocal = DateTime.Now;
        _path = string.IsNullOrWhiteSpace(path)
            ? RecordingStore.PathForNewTake(_startedLocal)
            : path!;

        try
        {
            Directory.CreateDirectory(RecordingStore.FolderPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RecordingNarrator: cannot create the recordings folder: {ex.Message}");
            Refuse(Lexicon.Get("audio.recording.folder_not_created", ("reason", ex.Message)));
            return false;
        }

        var recorder = new MicRecorder();
        MicRecorder.StartOutcome outcome = recorder.Start(device, _path, holdFileStart, out string failure);
        if (outcome != MicRecorder.StartOutcome.Started)
        {
            recorder.Dispose();
            Refuse(failure);
            return false;
        }

        _recorder = recorder;
        _quiet = quiet;
        _stoppedTooSoon = null;
        _lastSpokenInterval = 0;
        _capWarned = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        _timer.Start();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Stop the recording in flight and say what came of it. Safe when
    /// nothing is running.
    /// </summary>
    public static void Stop()
    {
        var recorder = _recorder;
        if (recorder == null) return;

        StopTimer();
        string saved = recorder.Stop();
        double seconds = recorder.Seconds;
        double captured = recorder.CapturedSeconds;
        bool duringLead = recorder.StoppedDuringLead;
        MicRecorder.StopReason reason = recorder.LastStopReason;
        string fault = recorder.FaultMessage;
        var stoppedTooSoon = _stoppedTooSoon;
        recorder.Dispose();
        _recorder = null;
        _quiet = false;
        _stoppedTooSoon = null;
        StateChanged?.Invoke();

        switch (StopKind(reason == MicRecorder.StopReason.Faulted, !string.IsNullOrEmpty(saved), captured, stoppedTooSoon != null, duringLead))
        {
            case StopSaying.Faulted:
                EarconPlayer.LeaderInvalidTone();
                Speak(Lexicon.Get("audio.recording.stopped_faulted", ("reason", fault)), interrupt: true);
                return;
            case StopSaying.NothingCaptured:
                // Nothing was captured. Say which of the two reasons it was,
                // because they need completely different remedies.
                EarconPlayer.LeaderCancelTone();
                Speak(Lexicon.Get("audio.recording.stopped_nothing_captured"), interrupt: true);
                return;
            case StopSaying.StoppedTooSoon:
                // The microphone worked; the check was stopped before enough of
                // its take was kept (Track Q3 repair 1). Its own words, not
                // "check the microphone".
                EarconPlayer.LeaderCancelTone();
                Trace.WriteLine($"RecordingNarrator: check recording stopped too soon, {captured:F2} s captured and {seconds:F2} s kept"
                    + (duringLead ? ", stopped during the lead before its file's start" : ""));
                Speak(stoppedTooSoon!(seconds), interrupt: true);
                return;
        }

        EarconPlayer.ConfirmTone();
        string capNote = reason == MicRecorder.StopReason.MaxLength
            ? Lexicon.Get("audio.recording.reached_length_limit")
            : Lexicon.Get("audio.recording.stopped");
        Speak(capNote + " " + Lexicon.Get("audio.recording.saved_as",
                ("length", RecordingStore.DescribeLength(seconds)),
                ("file", Path.GetFileNameWithoutExtension(saved))),
            interrupt: true);

        RecordingSaved?.Invoke(saved);
    }

    /// <summary>
    /// The configured microphone as a live device row, or null with a spoken
    /// explanation of why there isn't one.
    /// </summary>
    public static Devices.DeviceInfo? ResolveMicrophone(out string trouble)
    {
        trouble = "";
        string? file = AudioDevicesPath?.Invoke();
        if (string.IsNullOrEmpty(file))
        {
            trouble = Lexicon.Get("audio.recording.device_settings_unavailable");
            return null;
        }

        try
        {
            var devices = new Devices(file);
            if (!devices.Setup(out _, out string enumMessage))
            {
                trouble = string.IsNullOrEmpty(enumMessage)
                    ? Lexicon.Get("audio.recording.audio_system_not_started")
                    : enumMessage;
                return null;
            }

            Devices.Device? saved = devices.GetConfiguredDevice(Devices.DeviceTypes.input);
            if (saved == null)
            {
                // Two different situations, two different remedies.
                if (devices.IsSavedDeviceMissing(Devices.DeviceTypes.input, out string savedName))
                {
                    trouble = Lexicon.Get("audio.recording.saved_device_not_connected",
                        ("device", savedName));
                }
                else
                {
                    trouble = Lexicon.Get("audio.recording.no_microphone_chosen");
                }
                return null;
            }

            Devices.DeviceInfo? live = Devices.FindLive(saved);
            if (live == null)
            {
                trouble = Lexicon.Get("audio.recording.device_unavailable",
                    ("device", saved.Name ?? Lexicon.Get("audio.recording.the_saved_microphone")));
                return null;
            }
            return live;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RecordingNarrator.ResolveMicrophone failed: {ex.Message}");
            trouble = Lexicon.Get("audio.recording.microphone_not_opened", ("reason", ex.Message));
            return null;
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var recorder = _recorder;
        if (recorder == null) { StopTimer(); return; }

        if (!recorder.IsRunning)
        {
            // It stopped itself — the length cap, or a fault. Run the ordinary
            // stop path so the result is announced exactly once, the same way.
            Stop();
            return;
        }

        // A check's recording speaks nothing while it runs: it would be in the take.
        if (_quiet) return;

        double seconds = recorder.Seconds;

        // Periodic elapsed time, queued rather than interrupting, so it never
        // talks over the operator's own announcement or another surface.
        int interval = (int)(seconds / ProgressIntervalSeconds);
        if (interval > _lastSpokenInterval)
        {
            _lastSpokenInterval = interval;
            Speak(Lexicon.Get("audio.recording.still_recording",
                ("length", RecordingStore.DescribeLength(seconds))), interrupt: false);
        }

        // A warning before the cap, so nobody is cut off mid-sentence with no
        // notice. Once only.
        if (!_capWarned && seconds >= MicRecorder.MaxSeconds - CapWarningSeconds)
        {
            _capWarned = true;
            Speak(Lexicon.Get("audio.recording.cap_warning",
                ("seconds", CapWarningSeconds.ToString(CultureInfo.InvariantCulture))),
                interrupt: false);
        }
    }

    private static void StopTimer()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    /// <summary>
    /// Say why it will not record, with the tone that means refusal. Never
    /// leaves a caller to guess whether recording is under way.
    /// </summary>
    private static void Refuse(string reason)
    {
        EarconPlayer.LeaderInvalidTone();
        string text = string.IsNullOrWhiteSpace(reason)
            ? Lexicon.Get("audio.recording.could_not_start")
            : Lexicon.Get("audio.recording.could_not_start_because", ("reason", reason));
        Trace.WriteLine("RecordingNarrator: " + text);
        Speak(text, interrupt: true);
    }

    private static void Speak(string text, bool interrupt) =>
        ScreenReaderOutput.Speak(text, VerbosityLevel.Terse, interrupt);
}
