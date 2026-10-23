using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using POpusCodec;
using POpusCodec.Enums;
using Radios.Speech;

namespace Radios;

/// <summary>Records the received computer audio to a local FLAC file (#787).</summary>
/// <remarks>The receive thread only copies into a bounded channel. Encoding and disk access own a worker.</remarks>
public sealed class ComputerFlacRecorder : IComputerRecorder
{
    private sealed record Frame(byte[] Packet, float[] Heard, int Sequence, long Second,
        long ArrivalMilliseconds, bool Muted);
    private sealed class Session
    {
        internal readonly FlexBase Rig;
        internal readonly string Path;
        internal readonly bool Heard;
        internal readonly int Channels;
        internal readonly int Rate;
        internal readonly Channel<Frame> Queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(1000)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Writer;
        internal volatile Exception Fault;
        internal long PendingSecond;
        internal int PendingSequence;
        internal bool PendingMuted;
        internal long PendingArrivalMilliseconds;
        internal volatile bool Stopping;
        internal long StoppedAtMilliseconds;

        internal Session(FlexBase rig, string path, bool heard, int channels, int rate)
        { Rig = rig; Path = path; Heard = heard; Channels = channels; Rate = rate; }
    }

    private readonly object _gate = new();
    private readonly Func<string, bool> _play;
    private readonly Action _stopPlay;
    private readonly Func<bool> _isPlaying;
    private Session _session;
    private string _lastPath;

    /// <summary>True records post-gain, post-noise-reduction receive audio at the output rate.</summary>
    public bool RecordWhatYouHeard { get; set; }
    /// <summary>Mono averages both slice channels; stereo preserves pan. Chosen before Start.</summary>
    public bool Stereo { get; set; }

    public ComputerFlacRecorder(Func<string, bool> play, Action stopPlay, Func<bool> isPlaying)
    {
        _play = play; _stopPlay = stopPlay; _isPlaying = isPlaying;
        try
        {
            string root = System.IO.Path.Combine(RadioConfig.AppDataRoot, "Recorded QSOs");
            if (Directory.Exists(root))
            {
                foreach (string candidate in Directory.EnumerateFiles(root, "QSO-*.flac", SearchOption.AllDirectories))
                    if (_lastPath == null || string.CompareOrdinal(System.IO.Path.GetFileName(candidate),
                            System.IO.Path.GetFileName(_lastPath)) > 0)
                        _lastPath = candidate;
            }
        }
        catch (IOException ex) { JJTrace.Tracing.TraceLine("ComputerFlacRecorder scan: " + ex, TraceLevel.Warning); }
        catch (UnauthorizedAccessException ex) { JJTrace.Tracing.TraceLine("ComputerFlacRecorder scan: " + ex, TraceLevel.Warning); }
    }

    public bool IsRecording { get { lock (_gate) return _session != null && !_session.Stopping; } }
    public bool IsPlaying => _isPlaying();
    public string LastRecordingPath { get { lock (_gate) return _lastPath; } }

    // DRAFT PROSE (#787): awaiting Noel. These are assembled sentences spoken by RecorderControls.
    private static RecorderOutcome Result(bool ok, string message) =>
        new(ok, message, SpeechSubject.ComputerRecorder);

    public async Task<RecorderOutcome> StartRecordingAsync(FlexBase rig)
    {
        if (rig == null) return Result(false, "Connect to a radio before recording to a file.");
        lock (_gate)
            if (_session != null) return Result(false, "A computer recording is already running.");
        bool heard = RecordWhatYouHeard;
        int rate = heard ? (int)rig.ReceiveAudioSampleRate : 24000;
        if (rate <= 0 || !rig.ReceiveAudioRunning)
            return Result(false, "Computer receive audio is unavailable for recording.");
        int channels = Stereo ? 2 : 1;
        DateTime now = DateTime.Now;
        string folder = System.IO.Path.Combine(RadioConfig.AppDataRoot, "Recorded QSOs",
            now.ToString("yyyy"), now.ToString("MM"));
        string path;
        try
        {
            Directory.CreateDirectory(folder);
            if (new DriveInfo(System.IO.Path.GetPathRoot(folder)).AvailableFreeSpace < 200L * 1024 * 1024)
                return Result(false, "Recording cannot start because less than 200 MB of disk space is free.");
            string stem = now.ToString("'QSO-'yyyy-MM-dd-HHmmss");
            path = System.IO.Path.Combine(folder, stem + ".flac");
            for (int suffix = 2; File.Exists(path); suffix++)
                path = System.IO.Path.Combine(folder, stem + "-" + suffix + ".flac");
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("ComputerFlacRecorder start: " + ex, TraceLevel.Error);
            return Result(false, "Computer recording could not start. Check the recordings folder and free disk space.");
        }

        var session = new Session(rig, path, heard, channels, rate);
        lock (_gate)
        {
            if (_session != null) return Result(false, "A computer recording is already running.");
            _session = session;
            session.Writer = Task.Run(() => WriteSession(session));
        }
        if (!await session.Ready.Task.ConfigureAwait(false))
            return Result(false, "Computer recording could not start. Check the recordings folder and FLAC library.");
        return Result(true, heard
            ? "Recording what you hear to a file."
            : "Recording the radio's receive audio to a file.");
    }

    public async Task<RecorderOutcome> StopRecordingAsync()
    {
        Session session;
        lock (_gate)
        {
            session = _session;
            if (session == null) return Result(false, "No computer recording is running.");
            session.Stopping = true;
            session.StoppedAtMilliseconds = session.Clock.ElapsedMilliseconds;
            _session = null;
        }
        session.Queue.Writer.TryComplete();
        await session.Writer.ConfigureAwait(false);
        if (session.Fault != null)
            return Result(false, "Computer recording stopped because the file could not be completed. Check disk space.");
        lock (_gate) _lastPath = session.Path;
        return Result(true, "Computer recording saved as " + System.IO.Path.GetFileName(session.Path) + ".");
    }

    public Task<RecorderOutcome> PlayAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Task.FromResult(Result(false, "There is no computer recording to play."));
        return Task.FromResult(_play(path)
            ? Result(true, "") // RecordingPlayer owns the audible start and finish.
            : Result(false, "")); // RecordingPlayer already spoke the refusal.
    }

    public Task<RecorderOutcome> StopPlaybackAsync()
    { _stopPlay(); return Task.FromResult(Result(true, "")); }

    /// <summary>Called on the receive thread before output decoding. Never touches disk.</summary>
    internal void Receive(FlexBase rig, byte[] packet, int sequence, double timestamp, bool muted)
    {
        Session session;
        lock (_gate) session = _session;
        if (session == null || session.Rig != rig || session.Stopping) return;
        long second = (long)Math.Floor(timestamp);
        long arrived = session.Clock.ElapsedMilliseconds;
        if (session.Heard)
        {
            session.PendingSecond = second;
            session.PendingSequence = sequence;
            session.PendingMuted = muted;
            session.PendingArrivalMilliseconds = arrived;
            if (muted) Enqueue(session, new Frame(null, null, sequence, second, arrived, true));
        }
        else Enqueue(session, new Frame((byte[])packet.Clone(), null, sequence, second, arrived, muted));
    }

    /// <summary>Called after gain and NR on the same receive thread as Receive.</summary>
    internal void ReceiveHeard(FlexBase rig, float[] audio)
    {
        Session session;
        lock (_gate) session = _session;
        if (session == null || session.Rig != rig || !session.Heard || session.Stopping) return;
        Enqueue(session, new Frame(null, (float[])audio.Clone(), session.PendingSequence,
            session.PendingSecond, session.PendingArrivalMilliseconds, session.PendingMuted));
    }

    internal void ReceiveEnded(FlexBase rig)
    {
        Session session;
        lock (_gate) session = _session;
        if (session == null || session.Rig != rig) return;
        _ = StopAfterReceiveEndedAsync();
    }

    private async Task StopAfterReceiveEndedAsync()
    {
        RecorderOutcome outcome = await StopRecordingAsync().ConfigureAwait(false);
        if (outcome.Success)
            ScreenReaderOutput.Speak("Computer recording stopped because receive audio ended. " +
                outcome.Message, SpeechIntent.Interrupt, VerbosityLevel.Critical,
                subject: SpeechSubject.ComputerRecorder);
    }

    private static void Enqueue(Session session, Frame frame)
    {
        if (session.Queue.Writer.TryWrite(frame)) return;
        if (session.Stopping) return; // a receive callback raced the operator's Stop.
        session.Fault = new IOException("The recording writer could not keep up with receive audio.");
        session.StoppedAtMilliseconds = session.Clock.ElapsedMilliseconds;
        session.Queue.Writer.TryComplete();
    }

    private void WriteSession(Session session)
    {
        try
        {
            using var file = new RecordedQsoFlac(session.Path, session.Rate, session.Channels);
            using var decoder = session.Heard ? null : new OpusDecoder(SamplingRate.Sampling24000, Channels.Stereo);
            session.Ready.TrySetResult(true);
            int? previousSequence = null;
            long previousSecond = 0;
            long previousArrivalMilliseconds = 0;
            long frames = 0;
            int silentSincePacket = 0;
            while (true)
            {
                if (!session.Queue.Reader.TryRead(out Frame first))
                {
                    if (session.Queue.Reader.Completion.IsCompleted) break;
                    // Keep a long dropout on disk during the recording, including a power cut.
                    long due = session.Clock.ElapsedMilliseconds * session.Rate / 1000 - session.Rate / 5;
                    while (frames + session.Rate / 100 <= due)
                    {
                        WriteSilence(file, session.Rate / 100, session.Channels);
                        frames += session.Rate / 100;
                        silentSincePacket++;
                    }
                    Thread.Sleep(10);
                    continue;
                }
                Frame item = first;
                do
                {
                    int missing = previousSequence.HasValue ? (item.Sequence - previousSequence.Value + 15) & 15 : 0;
                    if (previousSecond > 0 && item.Second > previousSecond + 1)
                        missing += (int)Math.Min(1000, (item.Second - previousSecond - 1) * 100);
                    // The wire counter wraps every 16 packets. Elapsed arrival time also
                    // detects a longer dropout when the counter happens to wrap exactly.
                    if (previousSequence.HasValue)
                    {
                        long elapsed = item.ArrivalMilliseconds - previousArrivalMilliseconds;
                        if (elapsed > 120)
                            missing = Math.Max(missing, (int)Math.Min(1000, elapsed / 10 - 1));
                    }
                    missing = Math.Max(0, missing - silentSincePacket);
                    for (int i = 0; i < missing; ++i)
                    {
                        WriteSilence(file, session.Rate / 100, session.Channels);
                        frames += session.Rate / 100;
                    }
                    silentSincePacket = 0;
                    float[] samples = item.Muted ? null : session.Heard ? item.Heard : decoder.DecodePacketFloat(item.Packet);
                    if (samples == null)
                        samples = new float[session.Rate / 100 * 2];
                    if (session.Channels == 1)
                    {
                        float[] mono = new float[samples.Length / 2];
                        for (int i = 0; i < mono.Length; i++) mono[i] = (samples[i * 2] + samples[i * 2 + 1]) * .5f;
                        samples = mono;
                    }
                    file.Write(samples);
                    frames += samples.Length / session.Channels;
                    previousSequence = item.Sequence;
                    previousSecond = item.Second;
                    previousArrivalMilliseconds = item.ArrivalMilliseconds;
                    if (frames % (session.Rate * 10) < session.Rate / 100 &&
                        new DriveInfo(System.IO.Path.GetPathRoot(session.Path)).AvailableFreeSpace < 200L * 1024 * 1024)
                        throw new IOException("Less than 200 MB of disk space remains.");
                } while (session.Queue.Reader.TryRead(out item));
            }
            long total = (session.StoppedAtMilliseconds > 0
                ? session.StoppedAtMilliseconds : session.Clock.ElapsedMilliseconds) * session.Rate / 1000;
            if (session.Fault != null) throw session.Fault;
            while (frames + session.Rate / 100 <= total)
            {
                WriteSilence(file, session.Rate / 100, session.Channels);
                frames += session.Rate / 100;
            }
        }
        catch (Exception ex)
        {
            session.Fault = ex;
            session.Ready.TrySetResult(false);
            JJTrace.Tracing.TraceLine("ComputerFlacRecorder write: " + ex, TraceLevel.Error);
            // DRAFT PROSE (#787): awaiting Noel.
            ScreenReaderOutput.Speak("Computer recording stopped because the file could not be written.",
                SpeechIntent.Interrupt, VerbosityLevel.Critical, subject: SpeechSubject.ComputerRecorder);
        }
        finally
        {
            if (session.Fault != null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_session, session)) _session = null;
                    if (File.Exists(session.Path)) _lastPath = session.Path;
                }
            }
        }
    }

    private static void WriteSilence(RecordedQsoFlac file, int frames, int channels) =>
        file.Write(new float[frames * channels]);
}
