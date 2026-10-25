using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>Raised after a recording ends naturally and its completion notice is queued.</summary>
    public static event Action<string>? PlaybackCompleted;

    private static readonly object Gate = new();
    private static WaveStream? _reader;
    private static EndOfFileSampleProvider? _provider;
    private static FixedGainLimiterSampleProvider? _gain;
    private static TransportSampleProvider? _transport;
    private static FlexBase? _liveRig;
    private static bool _priorLiveMute;
    private static string _path = "";
    private static string _name = "";

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

    /// <summary>True while a recording is playing but paused (#787, the recordings dialog's preview).</summary>
    public static bool IsPaused
    {
        get { lock (Gate) return _transport?.Paused == true; }
    }

    /// <summary>
    /// Where the listener is in the file now, in seconds, or null when nothing
    /// is playing. While playing this is what has reached the speakers, so it
    /// trails what the mixer has read by the output's latency; once paused the
    /// buffered audio has drained and the two agree. The recordings dialog
    /// marks a trim point here when "[" or "]" is pressed mid-playback, and the
    /// point should be what the operator HEARD at the press.
    /// </summary>
    public static double? PositionSeconds
    {
        get
        {
            TransportSampleProvider? transport;
            lock (Gate) transport = _transport;
            if (transport == null) return null;
            double read = transport.PositionSeconds;
            if (transport.Paused) return read;
            double heard = read - EarconPlayer.AlertOutputLatencyMs / 1000.0;
            return Math.Max(transport.StartSeconds, heard);
        }
    }

    /// <summary>Pause the recording that is playing. False when nothing is playing.</summary>
    public static bool Pause()
    {
        TransportSampleProvider? transport;
        lock (Gate) transport = _transport;
        if (transport == null) return false;
        transport.Paused = true;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Carry on from where <see cref="Pause"/> stopped. False when nothing is paused.</summary>
    public static bool Resume()
    {
        TransportSampleProvider? transport;
        lock (Gate) transport = _transport;
        if (transport == null || !transport.Paused) return false;
        transport.Paused = false;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Move the recording that is playing or paused to another point. Paused,
    /// the next Resume carries on from there; sounding, playback jumps once
    /// the audio already queued for the speakers has played out.
    /// </summary>
    /// <remarks>
    /// Until 2026-10-25 this refused while sounding, a ruling of 2026-10-21
    /// ("waits for a later round"). Noel asked for that round at the bench on
    /// 2026-10-25 ("I'd make it so it can jump ahead and back"), and the
    /// dialog was changed to step while playing, but this refusal stayed: every
    /// step announced a new position while the audio carried on where it was.
    /// The transport's lock already keeps a seek from landing mid-read.
    /// </remarks>
    public static bool Seek(double seconds)
    {
        TransportSampleProvider? transport;
        lock (Gate) transport = _transport;
        if (transport == null) return false;
        try { transport.Seek(seconds); }
        catch (Exception ex)
        {
            Tracing.TraceLine("RecordingPlayer: seek failed — " + ex.Message, TraceLevel.Warning);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Play a recording from a point inside it (#787, the recordings dialog).
    /// Everything else is <see cref="Play"/>'s: the one player, the one output,
    /// the one end-of-file notice.
    /// </summary>
    public static bool PlayFrom(string path, double startSeconds, string announcement) =>
        PlayWithMixer(path, announcement, null, false, EarconPlayer.AddRecordingPlayback, null, startSeconds);

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
    public static bool Play(string path, string? announcement = null, FlexBase? liveRig = null,
        bool offAirListeningGain = false, bool? priorLiveMute = null) =>
        PlayWithMixer(path, announcement, liveRig, offAirListeningGain, EarconPlayer.AddRecordingPlayback,
            priorLiveMute);

    internal static bool PlayWithMixer(string path, string? announcement, FlexBase? liveRig,
        bool offAirListeningGain, Func<ISampleProvider, bool> addPlayback, bool? priorLiveMute = null,
        double startSeconds = 0)
    {
        if (string.IsNullOrEmpty(path))
        {
            Refuse(Lexicon.Get("audio.take.play_no_file"));
            return false;
        }

        Stop();

        WaveStream reader;
        try
        {
            if (!File.Exists(path))
            {
                Refuse(Lexicon.Get("audio.take.play_file_gone",
                    ("file", Path.GetFileNameWithoutExtension(path))));
                return false;
            }
            // FLAC through our decoder; WAV, and the MP3, Opus and M4A files Save
            // As writes into Recorded QSOs, through RecordedQsoMedia (#787).
            reader = string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase)
                ? new RecordedQsoFlacWaveStream(path)
                : RecordedQsoMedia.Open(path);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("RecordingPlayer: could not open " + path + " — " + ex.Message,
                TraceLevel.Warning);
            Refuse(Lexicon.Get("audio.take.play_failed", ("reason", ex.Message)));
            return false;
        }

        EndOfFileSampleProvider provider;
        FixedGainLimiterSampleProvider gain;
        TransportSampleProvider transport;
        try
        {
            transport = new TransportSampleProvider(reader);
            if (startSeconds > 0) transport.Seek(startSeconds);
            // The +30 dB listening gain is for the off-air IQ render only (Noel, 2026-10-20 09:01),
            // which is fixed-scale and quiet. A take this computer recorded is already at
            // microphone level; lifting it 30 dB would drive it into the limiter.
            gain = new FixedGainLimiterSampleProvider(ToMixerFormat(transport), offAirListeningGain);
            provider = new EndOfFileSampleProvider(gain);
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

        var announced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Ended += () => _ = Task.Run(async () =>
        {
            await announced.Task.ConfigureAwait(false);
            End(Lexicon.Get("audio.take.playback_finished"), confirmTone: true, expected: provider);
        });
        lock (Gate)
        {
            _reader = reader;
            _provider = provider;
            _gain = gain;
            _transport = transport;
            _path = path;
            _name = Path.GetFileNameWithoutExtension(path);
            _liveRig = liveRig;
            _priorLiveMute = priorLiveMute ?? liveRig?.LocalReceiveMutedForTakePlayback ?? false;
            if (liveRig != null) liveRig.LocalReceiveMutedForTakePlayback = true;
            // Lower or mute the radio on this computer for the playback, as
            // the operator chose (RxPlaybackLevel). Every exit below goes
            // through RestoreLiveAudio, which ends it.
            RxPlaybackLevel.Begin();
        }

        if (!addPlayback(provider))
        {
            announced.TrySetResult(true);
            // No mixer means no audio device is open for us to play through.
            // Say so instead of sitting silently in a "playing" state.
            lock (Gate)
            {
                _reader = null;
                _provider = null;
                _gain = null;
                _transport = null;
                _path = "";
                RestoreLiveAudio();
            }
            reader.Dispose();
            Refuse(Lexicon.Get("audio.take.play_no_output"));
            return false;
        }

        try
        {
            StateChanged?.Invoke();
            AnnounceStarted(announcement ?? Lexicon.Get("audio.take.playing",
                ("name", _name), ("length", RecordingStore.DescribeLength(seconds))), offAirListeningGain);
            announced.TrySetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            announced.TrySetResult(true);
            // This path begins after the local receive gate was applied.
            // End clears it before any refusal can itself fault or be delayed.
            try { End(""); } catch (Exception cleanup) { Trace.WriteLine($"RecordingPlayer cleanup failed: {cleanup}"); }
            Refuse(Lexicon.Get("audio.take.play_failed", ("reason", ex.Message)));
            return false;
        }
    }

    /// <summary>The sole successful-start announcement, chosen by the playback caller.</summary>
    internal static void AnnounceStarted(string announcement, bool gained = true) => ScreenReaderOutput.Speak(
        gained ? announcement.TrimEnd() + " " + Lexicon.Get("audio.take.fixed_gain") : announcement.TrimEnd(),
        VerbosityLevel.Terse, interrupt: true);

    private static void RestoreLiveAudio()
    {
        // The radio's audio comes back up with the same restore that lifts the
        // workshop's gate, so no playback can end without it (#787).
        RxPlaybackLevel.End();
        if (_liveRig != null) _liveRig.LocalReceiveMutedForTakePlayback = _priorLiveMute;
        _liveRig = null;
        _priorLiveMute = false;
    }

    /// <summary>
    /// Stop playback. Safe when nothing is playing; silent in that case, so a
    /// stop-before-start in <see cref="Play"/> costs nothing.
    /// </summary>
    public static void Stop() => End(spokenReason: Lexicon.Get("audio.check.playback_stopped"));

    /// <summary>
    /// Stop playback without the stop sentence, for a caller that says
    /// something more exact itself (the recordings dialog says where it
    /// stopped). One stop, one sentence.
    /// </summary>
    public static void StopWithoutSpeaking() => End(spokenReason: "");

    private static void End(string spokenReason, bool confirmTone = false, EndOfFileSampleProvider? expected = null)
    {
        WaveStream? reader;
        EndOfFileSampleProvider? provider;
        bool limited;
        string completedPath;
        lock (Gate)
        {
            if (expected != null && !ReferenceEquals(_provider, expected)) return;
            reader = _reader;
            provider = _provider;
            limited = _gain?.Limited ?? false;
            completedPath = _path;
            _reader = null;
            _provider = null;
            _gain = null;
            _transport = null;
            _path = "";
            RestoreLiveAudio();
        }
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
        if (!string.IsNullOrEmpty(spokenReason))
            ScreenReaderOutput.Speak(spokenReason, VerbosityLevel.Terse, interrupt: false);
        if (limited) ScreenReaderOutput.Speak(Lexicon.Get("audio.take.limiter_engaged"), VerbosityLevel.Terse);
        if (expected != null) PlaybackCompleted?.Invoke(completedPath);
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
    private static ISampleProvider ToMixerFormat(ISampleProvider source)
    {
        WaveFormat want = EarconPlayer.RecordingPlaybackFormat;
        if (source.WaveFormat.SampleRate != want.SampleRate)
            source = new WdlResamplingSampleProvider(source, want.SampleRate);
        if (source.WaveFormat.Channels == 1 && want.Channels == 2)
            source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > want.Channels)
            source = new StereoToMonoSampleProvider(source);
        return source;
    }

    /// <summary>Fixed 30 dB playback gain with a soft ceiling for the off-air render, or unity for
    /// anything else; file samples remain unmodified either way.</summary>
    internal sealed class FixedGainLimiterSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private int _limited;
        private readonly bool _gained;
        public FixedGainLimiterSampleProvider(ISampleProvider source, bool gained = true)
        {
            _source = source;
            _gained = gained;
        }
        public WaveFormat WaveFormat => _source.WaveFormat;
        public bool Limited => Volatile.Read(ref _limited) != 0;
        public int Read(Span<float> buffer)
        {
            int count = _source.Read(buffer);
            if (!_gained) return count; // unity: the file plays exactly as recorded
            for (int i = 0; i < count; i++)
            {
                double scaled = buffer[i] * 31.622776601683793; // +30 dB, never per-take normalization.
                double level = Math.Abs(scaled);
                if (level > 0.8)
                {
                    Volatile.Write(ref _limited, 1);
                    level = 0.8 + 0.19 * (1.0 - Math.Exp(-(level - 0.8) / 0.19));
                    scaled = Math.CopySign(level, scaled);
                }
                buffer[i] = (float)scaled;
            }
            return count;
        }
    }

    /// <summary>
    /// The file's own samples with a pause and a seek (#787, the recordings
    /// dialog's preview), placed FIRST in the chain, before the resampler, so a
    /// position is a position in the file and not in the mixer's rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paused, it fills every read with silence, the whole length asked for.
    /// A short read would be taken as the end of the file twice over: by
    /// <see cref="EndOfFileSampleProvider"/>, and by NAudio's mixer, which
    /// drops an input the moment it returns less than it was asked for.
    /// </para>
    /// <para>
    /// Read runs on the audio render thread and Seek on the UI thread; one lock
    /// keeps a seek from moving the decoder halfway through a read.
    /// </para>
    /// </remarks>
    internal sealed class TransportSampleProvider : ISampleProvider
    {
        private readonly WaveStream _reader;
        private readonly ISampleProvider _source;
        private readonly object _lock = new();
        private readonly int _channels;
        private readonly int _rate;
        private long _frames;
        private volatile bool _paused;

        internal TransportSampleProvider(WaveStream reader)
        {
            _reader = reader;
            _source = reader.ToSampleProvider();
            _channels = Math.Max(1, _source.WaveFormat.Channels);
            _rate = Math.Max(1, _source.WaveFormat.SampleRate);
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        internal bool Paused { get => _paused; set => _paused = value; }

        /// <summary>Where playback started, in seconds; the earliest a heard position can be.</summary>
        internal double StartSeconds { get; private set; }

        /// <summary>How far into the file the mixer has read, in seconds.</summary>
        internal double PositionSeconds
        {
            get { lock (_lock) return _frames / (double)_rate; }
        }

        /// <summary>Move to a point in the file, in whole sample frames, clamped to the file.</summary>
        internal void Seek(double seconds)
        {
            lock (_lock)
            {
                long frame = (long)Math.Floor(Math.Max(0, seconds) * _rate);
                int block = _reader.WaveFormat.BlockAlign;
                long bytes = frame * block;
                if (_reader.Length > 0 && bytes > _reader.Length) bytes = _reader.Length - _reader.Length % block;
                _reader.Position = bytes;
                _frames = bytes / block;
                StartSeconds = _frames / (double)_rate;
            }
        }

        public int Read(Span<float> buffer)
        {
            if (_paused)
            {
                buffer.Clear();
                return buffer.Length;
            }
            lock (_lock)
            {
                int read = _source.Read(buffer);
                _frames += read / _channels;
                return read;
            }
        }
    }

    /// <summary>
    /// Passes audio through and signals end of file without doing cleanup on the mixer thread.
    /// </summary>
    /// <remarks>
    /// Read runs on the audio render thread, inside the mixer's input walk.
    /// The signal only schedules cleanup on a worker; it never removes an
    /// input, disposes a file, or speaks inside that walk.
    /// <para>
    /// It keeps returning zero afterwards, so a mixer that has not dropped it
    /// yet hears silence rather than the file starting again.
    /// </para>
    /// </remarks>
    private sealed class EndOfFileSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private int _ended;
        public event Action? Ended;

        public EndOfFileSampleProvider(ISampleProvider source) { _source = source; }

        /// <summary>True once the file has played out.</summary>
        public bool HasEnded => Volatile.Read(ref _ended) != 0;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            if (HasEnded) return 0;
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
            if (read == buffer.Length) return read;

            // A short read is the end too, not just a zero one. NAudio's
            // MixingSampleProvider drops an input the moment it returns fewer
            // samples than asked and never reads it again, so a provider that
            // waited for a zero read never heard its own end: the 8.4 s take on
            // 2026-10-20 still said "playing" 13 s later, with live audio muted.
            if (Interlocked.Exchange(ref _ended, 1) == 0) Ended?.Invoke();
            return read;
        }
    }
}
