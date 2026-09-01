using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using JJTrace;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// The one place a recording an operator made can be played back to them on
/// this computer, and the one voice that says so (task #455).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this had to exist before "Play last take" could be honest.</b> Don,
/// 2026-09-01: he recorded a take, pressed Stop recording, pressed Play last
/// take, and was told there were no recordings — while the same dialog's Open
/// recordings folder button showed the files and Winamp played them. The play
/// button was asking the RADIO's quick-record buffer; the folder button was
/// asking <see cref="RecordingStore"/>. Both were working. The word "take"
/// meant two things, and the application had no way at all to play a file it
/// had itself just written.
/// </para>
/// <para>
/// It is deliberately shaped like <see cref="RecordingNarrator"/>: a static
/// single entry point, a Toggle that makes "press it again to stop" true
/// everywhere, a StateChanged event so a button can rename itself honestly,
/// and it says what it is doing. A recorder that announces itself and a player
/// that does not would be two behaviours for one pair of buttons.
/// </para>
/// <para>
/// It plays through <see cref="EarconPlayer"/>'s alert channel rather than
/// opening an audio device of its own: that is the device the operator already
/// chose for the application to speak and beep through, it is already open, and
/// a second device would be a second thing to configure and a second thing to
/// go wrong. It is NOT an earcon, so the earcons-off switch does not silence
/// it — see <c>EarconPlayer.AddRecordingPlayback</c>.
/// </para>
/// <para>
/// NOTHING HERE TOUCHES THE RADIO. This is local playback of a local file; it
/// never keys, never transmits, and works with no radio connected — which is
/// the whole point, because the recordings folder works with no radio
/// connected too.
/// </para>
/// </remarks>
public static class RecordingPlayer
{
    /// <summary>
    /// Fired when playback starts or stops, so a button can rename itself
    /// honestly. Raised on whatever thread the change happened on; a WPF
    /// subscriber must marshal.
    /// </summary>
    public static event Action? StateChanged;

    private static readonly object Gate = new();
    private static WaveFileReader? _reader;
    private static EndOfFileSampleProvider? _provider;
    private static DispatcherTimer? _watcher;
    private static string _path = "";
    private static string _name = "";

    /// <summary>
    /// How often the UI thread asks whether the file has played out. Short
    /// enough that "finished" lands while the operator is still listening for
    /// it, long enough to cost nothing.
    /// </summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>True while a recording is playing.</summary>
    public static bool IsPlaying
    {
        get { lock (Gate) return _provider != null; }
    }

    /// <summary>The file being played, or empty.</summary>
    public static string CurrentPath
    {
        get { lock (Gate) return _provider != null ? _path : ""; }
    }

    /// <summary>
    /// Play the recording, or stop the one already running — so one button can
    /// be both, the way the recorder's already is.
    /// </summary>
    public static void Toggle(string path)
    {
        if (IsPlaying) Stop();
        else Play(path);
    }

    /// <summary>
    /// Start playing a recording, announcing what it is. Refuses out loud
    /// rather than failing quietly: a blind operator who presses play and hears
    /// nothing cannot tell "playing silence" from "did not start", and that
    /// ambiguity is exactly what this whole finding was made of.
    /// </summary>
    public static bool Play(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Refuse(Lexicon.Get("audio.take.play_no_file"));
            return false;
        }

        Stop();

        WaveFileReader reader;
        try
        {
            if (!File.Exists(path))
            {
                Refuse(Lexicon.Get("audio.take.play_file_gone",
                    ("file", Path.GetFileNameWithoutExtension(path))));
                return false;
            }
            reader = new WaveFileReader(path);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("RecordingPlayer: could not open " + path + " — " + ex.Message,
                TraceLevel.Warning);
            Refuse(Lexicon.Get("audio.take.play_failed", ("reason", ex.Message)));
            return false;
        }

        EndOfFileSampleProvider provider;
        try
        {
            provider = new EndOfFileSampleProvider(ToMixerFormat(reader));
        }
        catch (Exception ex)
        {
            reader.Dispose();
            Tracing.TraceLine("RecordingPlayer: could not prepare " + path + " — " + ex.Message,
                TraceLevel.Warning);
            Refuse(Lexicon.Get("audio.take.play_failed", ("reason", ex.Message)));
            return false;
        }

        double seconds = 0;
        try
        {
            seconds = reader.TotalTime.TotalSeconds;
        }
        catch (Exception ex)
        {
            // Ignored deliberately: the length is only used to say how long the
            // take is. A header this cannot measure costs the operator that
            // phrase and nothing else — refusing to play a file we can
            // otherwise decode, over a duration, would be the worse trade.
            Trace.WriteLine($"RecordingPlayer: no length for {path}: {ex.Message}");
        }

        lock (Gate)
        {
            _reader = reader;
            _provider = provider;
            _path = path;
            _name = Path.GetFileNameWithoutExtension(path);
        }

        if (!EarconPlayer.AddRecordingPlayback(provider))
        {
            // No mixer means no audio device is open for us to play through.
            // Say so instead of sitting silently in a "playing" state.
            lock (Gate)
            {
                _reader = null;
                _provider = null;
                _path = "";
            }
            reader.Dispose();
            Refuse(Lexicon.Get("audio.take.play_no_output"));
            return false;
        }

        StartWatching();
        StateChanged?.Invoke();
        ScreenReaderOutput.Speak(
            Lexicon.Get("audio.take.playing",
                ("name", _name),
                ("length", RecordingStore.DescribeLength(seconds))),
            VerbosityLevel.Terse, interrupt: true);
        return true;
    }

    /// <summary>
    /// Stop playback. Safe when nothing is playing; silent in that case, so a
    /// stop-before-start in <see cref="Play"/> costs nothing.
    /// </summary>
    public static void Stop() => End(spokenReason: Lexicon.Get("audio.check.playback_stopped"));

    /// <summary>
    /// Watch for the file playing out, on the thread that started it. See
    /// <see cref="EndOfFileSampleProvider"/> for why end of file is a flag
    /// rather than a callback.
    /// </summary>
    private static void StartWatching()
    {
        StopWatching();
        var watcher = new DispatcherTimer { Interval = WatchInterval };
        watcher.Tick += (s, e) =>
        {
            EndOfFileSampleProvider? provider;
            lock (Gate) provider = _provider;
            if (provider == null) { StopWatching(); return; }
            if (!provider.HasEnded) return;
            // Says so rather than only going quiet: a recording of digital
            // silence and a playback that never started sound identical, and
            // only one of them ends with this sentence.
            End(Lexicon.Get("audio.take.playback_finished"), confirmTone: true);
        };
        _watcher = watcher;
        watcher.Start();
    }

    private static void StopWatching()
    {
        var watcher = _watcher;
        _watcher = null;
        watcher?.Stop();
    }

    private static void End(string spokenReason, bool confirmTone = false)
    {
        WaveFileReader? reader;
        EndOfFileSampleProvider? provider;
        lock (Gate)
        {
            reader = _reader;
            provider = _provider;
            _reader = null;
            _provider = null;
            _path = "";
        }
        StopWatching();
        if (provider == null) return;

        EarconPlayer.RemoveRecordingPlayback(provider);
        try
        {
            reader?.Dispose();
        }
        catch (Exception ex)
        {
            // Ignored deliberately: the provider is already off the mixer and
            // the fields are already cleared, so a throwing Dispose has nothing
            // left to corrupt — while letting it out of a stop would leave the
            // operator unable to stop playback at all.
            Trace.WriteLine($"RecordingPlayer: dispose failed: {ex.Message}");
        }

        StateChanged?.Invoke();
        if (confirmTone) EarconPlayer.ConfirmTone();
        ScreenReaderOutput.Speak(spokenReason, VerbosityLevel.Terse, interrupt: false);
    }

    /// <summary>Say why it will not play, with the tone that means refusal.</summary>
    private static void Refuse(string reason)
    {
        EarconPlayer.LeaderInvalidTone();
        Tracing.TraceLine("RecordingPlayer: " + reason, TraceLevel.Warning);
        ScreenReaderOutput.Speak(reason, VerbosityLevel.Critical, interrupt: true);
    }

    /// <summary>
    /// Bring a file to the mixer's rate and channel count. A take can be any
    /// rate a microphone offered and is usually mono; the mixer is 44100 Hz
    /// stereo and will refuse anything else.
    /// </summary>
    private static ISampleProvider ToMixerFormat(WaveFileReader reader)
    {
        WaveFormat want = EarconPlayer.RecordingPlaybackFormat;
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.SampleRate != want.SampleRate)
            source = new WdlResamplingSampleProvider(source, want.SampleRate);
        if (source.WaveFormat.Channels == 1 && want.Channels == 2)
            source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > want.Channels)
            source = new StereoToMonoSampleProvider(source);
        return source;
    }

    /// <summary>
    /// Passes audio through and RAISES A FLAG when the source runs out.
    /// </summary>
    /// <remarks>
    /// <b>A flag, not a callback, and that is the whole reason this class
    /// exists in this shape.</b> Read runs on the audio render thread, inside
    /// the mixer's own walk of its input list. Calling back from there would
    /// mean removing a mixer input from inside that walk, disposing a file
    /// handle on the render thread, and speaking from a thread that has no
    /// business speaking. So it sets a bool and returns, and the player's own
    /// timer notices on the UI thread, where every other state change in this
    /// class already happens.
    /// <para>
    /// It keeps returning zero afterwards, so a mixer that has not dropped it
    /// yet hears silence rather than the file starting again.
    /// </para>
    /// </remarks>
    private sealed class EndOfFileSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private volatile bool _ended;

        public EndOfFileSampleProvider(ISampleProvider source) { _source = source; }

        /// <summary>True once the file has played out.</summary>
        public bool HasEnded => _ended;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            if (_ended) return 0;
            int read;
            try
            {
                read = _source.Read(buffer);
            }
            catch (Exception ex)
            {
                // A file that vanishes or a device that faults mid-playback
                // must not take the render thread down with it.
                Trace.WriteLine($"RecordingPlayer read failed: {ex.Message}");
                read = 0;
            }
            if (read > 0) return read;

            _ended = true;
            return 0;
        }
    }
}
