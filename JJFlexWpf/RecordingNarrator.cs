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

        // The microphone the operator already chose. Asking again here would
        // be a second place to configure the same thing, and the two would
        // drift apart the first time somebody changed one of them.
        Devices.DeviceInfo? device = ResolveMicrophone(out string trouble);
        if (device == null)
        {
            Refuse(trouble);
            return;
        }

        // Windows can be handing desktop applications silence while its own
        // Sound Recorder works perfectly — the single most confusing state a
        // microphone can be in. Say so BEFORE recording rather than handing
        // somebody a file of digital zeroes afterwards.
        var access = MicrophonePrivacy.Check(out string privacyExplanation);
        if (MicrophonePrivacy.IsBlocked(access))
        {
            Refuse(privacyExplanation);
            return;
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
            return;
        }

        var recorder = new MicRecorder();
        MicRecorder.StartOutcome outcome = recorder.Start(device, _path, out string failure);
        if (outcome != MicRecorder.StartOutcome.Started)
        {
            recorder.Dispose();
            Refuse(failure);
            return;
        }

        _recorder = recorder;
        _lastSpokenInterval = 0;
        _capWarned = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        _timer.Start();
        StateChanged?.Invoke();

        EarconPlayer.ConfirmTone();
        Speak(Lexicon.Get("audio.recording.started", ("purpose", _purpose)), interrupt: true);
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
        MicRecorder.StopReason reason = recorder.LastStopReason;
        string fault = recorder.FaultMessage;
        recorder.Dispose();
        _recorder = null;
        StateChanged?.Invoke();

        if (reason == MicRecorder.StopReason.Faulted)
        {
            EarconPlayer.LeaderInvalidTone();
            Speak(Lexicon.Get("audio.recording.stopped_faulted", ("reason", fault)), interrupt: true);
            return;
        }

        if (string.IsNullOrEmpty(saved))
        {
            // Nothing was captured. Say which of the two reasons it was,
            // because they need completely different remedies.
            EarconPlayer.LeaderCancelTone();
            Speak(Lexicon.Get("audio.recording.stopped_nothing_captured"), interrupt: true);
            return;
        }

        EarconPlayer.ConfirmTone();
        string capNote = Lexicon.Get(reason == MicRecorder.StopReason.MaxLength
            ? "audio.recording.reached_length_limit"
            : "audio.recording.stopped");
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
