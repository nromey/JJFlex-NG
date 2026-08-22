using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace JJFlexWpf;

/// <summary>
/// DSP controls track (2026-08-11) — the noise-profile folder convention.
/// SaveNoiseProfile always took a bare path; nobody had ever decided where
/// profiles live. They live here now: %AppData%\JJFlexRadio\NoiseProfiles,
/// one XML file per profile, extension .jjnoise (a serialized
/// NoiseProfileData: name, band, antenna, capture time, magnitudes).
///
/// Deliberately shack-level, not per-operator: a noise profile describes the
/// antenna and the QTH, which don't change when a different operator signs in.
/// </summary>
public static class NoiseProfileStore
{
    /// <summary>File extension for saved noise profiles.</summary>
    public const string Extension = ".jjnoise";

    /// <summary>The profiles folder: %AppData%\JJFlexRadio\NoiseProfiles.</summary>
    public static string FolderPath => Path.Combine(
        Radios.RadioConfig.AppDataRoot, "NoiseProfiles");

    /// <summary>
    /// Where the most recent capture auto-saves. Every completed capture
    /// lands here (and is remembered in AudioOutputConfig.NoiseProfileLastPath)
    /// so a captured profile survives an app restart without the operator
    /// having to name and save anything. Explicit Save As writes a second,
    /// named copy.
    /// </summary>
    public static string LastCapturePath => Path.Combine(FolderPath, "last-capture" + Extension);

    /// <summary>Turn a display name into a file path inside the profiles folder.</summary>
    public static string PathForName(string name) =>
        Path.Combine(FolderPath, SafeFileName(name) + Extension);

    /// <summary>Strip filesystem-hostile characters; never returns empty.</summary>
    public static string SafeFileName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name ?? "")
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)
                sb.Append(c);
        }
        string result = sb.ToString().Trim();
        return result.Length > 0 ? result : "profile";
    }

    /// <summary>A profile file on disk with its parsed metadata.</summary>
    public sealed class ProfileFile
    {
        public string Path { get; init; } = "";
        public NoiseProfileData Data { get; init; } = new();

        /// <summary>
        /// One spoken/displayed line: name first, then the metadata an
        /// operator actually thinks in — band, antenna, when it was captured.
        /// </summary>
        public string Describe()
        {
            var parts = new List<string> { string.IsNullOrEmpty(Data.Name) ? "unnamed" : Data.Name };
            if (!string.IsNullOrEmpty(Data.Band)) parts.Add(Data.Band);
            if (!string.IsNullOrEmpty(Data.Antenna)) parts.Add(Data.Antenna);
            if (Data.CapturedUtc != default)
                parts.Add("captured " + Data.CapturedUtc.ToLocalTime().ToString(
                    "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Every readable profile in the folder, newest capture first. Unreadable
    /// files are skipped (traced), never thrown — a corrupt profile must not
    /// take the picker down with it.
    /// </summary>
    public static List<ProfileFile> Enumerate()
    {
        var result = new List<ProfileFile>();
        try
        {
            if (!Directory.Exists(FolderPath)) return result;
            var serializer = new XmlSerializer(typeof(NoiseProfileData));
            foreach (string file in Directory.GetFiles(FolderPath, "*" + Extension))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    if (serializer.Deserialize(stream) is NoiseProfileData data && data.Magnitudes != null)
                        result.Add(new ProfileFile { Path = file, Data = data });
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"NoiseProfileStore: skipping {file}: {ex.Message}");
                }
            }
            result.Sort((a, b) => b.Data.CapturedUtc.CompareTo(a.Data.CapturedUtc));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NoiseProfileStore.Enumerate failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Open the profiles folder in File Explorer (creating it first) so
    /// profiles can be shared, renamed, or deleted with ordinary file tools.
    /// </summary>
    public static bool OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            Process.Start(new ProcessStartInfo { FileName = FolderPath, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NoiseProfileStore.OpenFolder failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Friendly band label for the rig's current RX frequency: "20m" style
    /// for the ordinary ham bands, plain megahertz when out of band or
    /// somewhere exotic. Used to stamp profile metadata at capture time.
    /// </summary>
    public static string BandLabelFor(Radios.FlexBase rig)
    {
        ulong freq = 0;
        try
        {
            freq = rig.RXFrequency;
            var item = HamBands.Bands.Query(freq);
            if (item != null)
            {
                string n = item.Band.ToString(); // enum names like "m20"
                if (n.Length > 1 && n[0] == 'm' &&
                    int.TryParse(n.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int meters))
                    return meters + "m";
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NoiseProfileStore.BandLabelFor: {ex.Message}");
        }
        return freq > 0
            ? (freq / 1e6).ToString("F3", CultureInfo.InvariantCulture) + " MHz"
            : "";
    }
}

/// <summary>
/// DSP controls track (2026-08-11) — the spoken noise capture. A blind
/// operator must HEAR a three-second capture happening: start, the seconds
/// passing, and the result. This narrator wraps
/// RxAudioPipeline.StartNoiseSampling with a 100 ms poll of
/// IsNoiseSampling/NoiseSamplingProgress and speaks all three acts. Every
/// surface that captures (leader key Ctrl+J Q, DSP panel button, menu item,
/// Noise Profiles dialog) goes through here so the behavior can never fork.
///
/// Shared-capture note: the transverter-presence detector and the RNN
/// training-corpus capture are the same pattern (grab a baseline, store it,
/// use it later). SpectralSubtractionProvider.StartSampling is the shared
/// engine spine; this class is the shared spoken-UX spine. Reuse both.
///
/// Completed captures auto-save to NoiseProfileStore.LastCapturePath and are
/// remembered in AudioOutputConfig.NoiseProfileLastPath, so a capture
/// survives an app restart without a save dialog in the way (friction-tax
/// rule: profile capture is one keystroke, done means done).
/// </summary>
public static class NoiseCaptureNarrator
{
    /// <summary>Set by MainWindow: the live per-operator audio config.</summary>
    public static Func<AudioOutputConfig?>? AudioConfigSource { get; set; }

    /// <summary>Set by MainWindow: persist the audio config now.</summary>
    public static Action? AudioConfigSave { get; set; }

    /// <summary>Fired when a capture starts or stops, so buttons can rename
    /// themselves honestly ("Capture Noise Profile" vs "Cancel Noise Capture").</summary>
    public static event Action? StateChanged;

    private static DispatcherTimer? _timer;
    private static RxAudioPipeline? _pipeline;
    private static int _duration;
    private static int _lastSpokenSecond;
    private static DateTime _startedUtc;
    private static string _band = "";
    private static string _antenna = "";
    private static Action? _onFinished;

    /// <summary>True while a narrated capture is in flight.</summary>
    public static bool IsRunning => _timer != null;

    /// <summary>Band label stamped at the start of the most recent capture
    /// this session ("" if none). Used by Save As for honest metadata.</summary>
    public static string LastCaptureBand => _band;

    /// <summary>Antenna stamped at the start of the most recent capture
    /// this session ("" if none).</summary>
    public static string LastCaptureAntenna => _antenna;

    /// <summary>
    /// Start a capture, or cancel the one in flight — the single entry point
    /// every surface calls, so "press it again to cancel" is true everywhere.
    /// </summary>
    public static void Toggle(Radios.FlexBase? rig, RxAudioPipeline? pipeline,
        int durationSeconds, Action? onFinished = null)
    {
        if (IsRunning) Cancel();
        else Start(rig, pipeline, durationSeconds, onFinished);
    }

    /// <summary>
    /// Begin a narrated capture. Refuses out loud when it cannot work:
    /// no pipeline, or PC audio off (the capture listens to the radio audio
    /// this computer plays — with PC audio off, no samples ever arrive).
    /// </summary>
    public static void Start(Radios.FlexBase? rig, RxAudioPipeline? pipeline,
        int durationSeconds, Action? onFinished = null)
    {
        if (IsRunning) return;
        if (pipeline == null)
        {
            EarconPlayer.LeaderInvalidTone();
            Speak(Radios.Lexicon.Get("audio.noise.capture.pipeline_not_ready"), interrupt: true);
            return;
        }
        if (rig != null && !rig.PCAudio)
        {
            EarconPlayer.LeaderInvalidTone();
            Speak(Radios.Lexicon.Get("audio.noise.capture.pc_audio_off"), interrupt: true);
            return;
        }

        _pipeline = pipeline;
        _duration = Math.Clamp(durationSeconds, 1, 5);
        _lastSpokenSecond = 0;
        _startedUtc = DateTime.UtcNow;
        _band = rig != null ? NoiseProfileStore.BandLabelFor(rig) : "";
        _antenna = rig?.RXAntennaName ?? "";
        _onFinished = onFinished;

        pipeline.StartNoiseSampling(_duration);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
        StateChanged?.Invoke();

        EarconPlayer.ConfirmTone();
        Speak(Radios.Lexicon.Get("audio.noise.capture.started",
            ("seconds", _duration),
            ("unit", _duration == 1
                ? Radios.Lexicon.Get("audio.unit.second")
                : Radios.Lexicon.Get("audio.unit.seconds"))),
            interrupt: true);
    }

    /// <summary>Cancel the capture in flight, and say so.</summary>
    public static void Cancel()
    {
        var p = _pipeline;
        if (!IsRunning && p == null) return;
        StopTimer();
        p?.CancelNoiseSampling();
        EarconPlayer.LeaderCancelTone();
        Speak(Radios.Lexicon.Get("audio.noise.capture.cancelled"), interrupt: true);
        FireFinished();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var p = _pipeline;
        if (p == null) { StopTimer(); return; }

        if (p.IsNoiseSampling)
        {
            float progress = p.NoiseSamplingProgress;

            // Speak each elapsed whole second ("1", "2", ...) — queued, not
            // interrupting, so the start announcement finishes naturally.
            int second = (int)(progress * _duration + 0.0001f);
            if (second > _lastSpokenSecond && second < _duration)
            {
                _lastSpokenSecond = second;
                Speak(second.ToString(CultureInfo.InvariantCulture), interrupt: false);
            }

            // Stall watchdog: the sampler only advances when decoded RX audio
            // flows through the pipeline. If nothing has arrived after 2.5
            // seconds, waiting longer will not help — stop and say why.
            if (progress <= 0f && (DateTime.UtcNow - _startedUtc).TotalSeconds > 2.5)
            {
                StopTimer();
                p.CancelNoiseSampling();
                EarconPlayer.LeaderInvalidTone();
                Speak(Radios.Lexicon.Get("audio.noise.capture.no_audio_arriving"), interrupt: true);
                FireFinished();
            }
            return;
        }

        // Sampling flag dropped: finished (profile present) or cancelled elsewhere.
        StopTimer();
        if (p.HasNoiseProfile)
        {
            AutoSave(p);
            EarconPlayer.ConfirmTone();
            string next = p.SpectralEnabled
                ? Radios.Lexicon.Get("audio.noise.capture.spectral_is_using_it")
                : Radios.Lexicon.Get("audio.noise.capture.turn_spectral_on");
            Speak(Radios.Lexicon.Get("audio.noise.capture.captured", ("next", next)), interrupt: true);
        }
        else
        {
            EarconPlayer.LeaderCancelTone();
            Speak(Radios.Lexicon.Get("audio.noise.capture.cancelled"), interrupt: true);
        }
        FireFinished();
    }

    /// <summary>
    /// Persist the fresh capture to the last-capture file, remember it in
    /// config, and reload it through the pipeline so the profile NAME the UI
    /// reports comes from the file (a fresh capture would otherwise keep the
    /// previously loaded profile's name — the engine only names profiles on
    /// load). Failures trace and degrade to an in-memory-only profile.
    /// </summary>
    private static void AutoSave(RxAudioPipeline p)
    {
        try
        {
            // Plain name on purpose — band/antenna ride the metadata fields,
            // and ProfileFile.Describe() already reads them back after the
            // name; folding them into the name would speak them twice.
            string name = "Last capture";

            Directory.CreateDirectory(NoiseProfileStore.FolderPath);
            if (p.SaveNoiseProfile(NoiseProfileStore.LastCapturePath, name, _band, _antenna))
            {
                p.LoadNoiseProfile(NoiseProfileStore.LastCapturePath);
                var cfg = AudioConfigSource?.Invoke();
                if (cfg != null)
                {
                    cfg.NoiseProfileLastPath = NoiseProfileStore.LastCapturePath;
                    AudioConfigSave?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NoiseCaptureNarrator.AutoSave failed: {ex.Message}");
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
        _pipeline = null;
        StateChanged?.Invoke();
    }

    private static void FireFinished()
    {
        var cb = _onFinished;
        _onFinished = null;
        cb?.Invoke();
    }

    private static void Speak(string text, bool interrupt) =>
        Radios.ScreenReaderOutput.Speak(text, Radios.VerbosityLevel.Terse, interrupt);
}
