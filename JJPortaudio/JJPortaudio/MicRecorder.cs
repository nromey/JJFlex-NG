using System;
using System.Diagnostics;
using System.IO;
using JJTrace;

namespace JJPortaudio
{
    /// <summary>
    /// Records the microphone to a WAV file (Sprint 33 Track I).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <see cref="MicProbe"/> with its samples kept. The probe already
    /// owns an independent input stream, resolves the device the same way the
    /// transmit path resolves it, tolerates the device disappearing mid-run,
    /// meters level and loudness, and can tell a quiet room from Windows
    /// handing us digital silence. All of that is exactly what a recorder
    /// needs, so this class adds a file and nothing else.
    /// </para>
    /// <para>
    /// DELIBERATELY IGNORANT OF WHAT IT IS RECORDING. It takes a device and a
    /// path. It does not know about reference files, station messages, contest
    /// calls or anything else that might want a microphone captured to disk —
    /// naming, organising and describing recordings belong to the layer above,
    /// which is why a voice-message library can use this unchanged.
    /// </para>
    /// <para>
    /// A HARD DURATION CAP is built in rather than left to the caller. A
    /// recorder is the kind of thing that gets started and forgotten, and an
    /// unbounded one quietly fills a disk with a microphone's worth of the
    /// operator's room. It stops itself, and it says it stopped.
    /// </para>
    /// <para>
    /// It does NOT announce anything. Speech belongs to the surface that
    /// started the recording, because that surface knows who is listening and
    /// in what verbosity — but nothing here is silent either: every start,
    /// stop, cap and fault is traced, and the state a caller must announce is
    /// on the object at all times.
    /// </para>
    /// </remarks>
    public sealed class MicRecorder : IDisposable
    {
        /// <summary>How a start attempt ended.</summary>
        public enum StartOutcome
        {
            /// <summary>Recording.</summary>
            Started = 0,
            /// <summary>This recorder was already running.</summary>
            AlreadyRunning,
            /// <summary>The device is not present any more.</summary>
            DeviceGone,
            /// <summary>PortAudio refused to open it. The message carries its reason.</summary>
            OpenFailed,
            /// <summary>The file could not be created. The message carries why.</summary>
            FileFailed
        }

        /// <summary>Why a recording ended.</summary>
        public enum StopReason
        {
            /// <summary>Not running, or never run.</summary>
            None = 0,
            /// <summary>Somebody asked it to stop.</summary>
            Requested,
            /// <summary>It hit <see cref="MaxSeconds"/> and stopped itself.</summary>
            MaxLength,
            /// <summary>The device or the file failed.</summary>
            Faulted
        }

        /// <summary>
        /// The longest any single recording may run, in seconds. Fifteen
        /// minutes is far past any sane reference take or station message and
        /// still nothing like "until the disk fills".
        /// </summary>
        public const int MaxSeconds = 900;

        private readonly object _sync = new object();
        private MicProbe _probe;
        private WavWriter _writer;
        private volatile bool _running;
        private StopReason _stopReason = StopReason.None;
        private string _faultMessage = "";
        private long _maxDataFrames;

        /// <summary>True while a recording is in flight.</summary>
        public bool IsRunning => _running;

        /// <summary>The file this recording is being written to.</summary>
        public string Path { get; private set; } = "";

        /// <summary>The device being recorded.</summary>
        public Devices.DeviceInfo Device { get; private set; }

        /// <summary>Why the last recording ended.</summary>
        public StopReason LastStopReason { get { lock (_sync) return _stopReason; } }

        /// <summary>
        /// What went wrong, written to be spoken as-is. Empty unless the last
        /// recording faulted.
        /// </summary>
        public string FaultMessage { get { lock (_sync) return _faultMessage; } }

        /// <summary>Seconds captured so far, or in the finished recording.</summary>
        public double Seconds
        {
            get
            {
                var w = _writer;
                return w?.Seconds ?? _finishedSeconds;
            }
        }

        private double _finishedSeconds;

        /// <summary>Sample rate the device actually opened at; 0 before it opens.</summary>
        public int SampleRate { get; private set; }

        /// <summary>Channels the device actually opened; 0 before it opens.</summary>
        public int Channels { get; private set; }

        /// <summary>
        /// The live level and loudness of what is being recorded, straight
        /// from the probe, so a surface can report it without opening a second
        /// stream. Default when nothing is running.
        /// </summary>
        public MicProbe.Reading Read()
        {
            var p = _probe;
            return p != null ? p.Read() : default;
        }

        /// <summary>
        /// Start recording <paramref name="device"/> to <paramref name="path"/>.
        /// </summary>
        /// <param name="message">
        /// Empty on success; otherwise why it did not start, written to be
        /// spoken as-is.
        /// </param>
        public StartOutcome Start(Devices.DeviceInfo device, string path, out string message)
        {
            message = "";
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path", nameof(path));

            lock (_sync)
            {
                if (_running || _probe != null) return StartOutcome.AlreadyRunning;
                _stopReason = StopReason.None;
                _faultMessage = "";
                _finishedSeconds = 0;
            }

            var probe = new MicProbe();

            // Open the device FIRST. Its real rate and channel count go into
            // the WAV header, and asking the file to guess would produce a
            // file that plays at the wrong speed on any device that refused
            // the rate we asked for — the one failure mode nobody would
            // suspect the recorder for.
            MicProbe.StartOutcome outcome = probe.Start(device, out string failure);
            if (outcome != MicProbe.StartOutcome.Started)
            {
                probe.Dispose();
                message = failure;
                return outcome == MicProbe.StartOutcome.DeviceGone
                    ? StartOutcome.DeviceGone
                    : StartOutcome.OpenFailed;
            }

            MicProbe.Reading opened = probe.Read();
            int rate = opened.SampleRate > 0 ? opened.SampleRate : 48000;
            int channels = opened.Channels > 0 ? opened.Channels : 1;

            WavWriter writer;
            try
            {
                writer = new WavWriter(path, rate, channels);
            }
            catch (Exception ex)
            {
                probe.Stop();
                probe.Dispose();
                Tracing.TraceLine("MicRecorder: could not create \"" + path + "\": "
                    + ex.Message, TraceLevel.Error);
                message = "The recording file could not be created: " + ex.Message;
                return StartOutcome.FileFailed;
            }

            lock (_sync)
            {
                _probe = probe;
                _writer = writer;
                Path = path;
                Device = device;
                SampleRate = rate;
                Channels = channels;
                _maxDataFrames = (long)MaxSeconds * rate;
                _running = true;
            }

            // Installed only now that everything else is in place, so no block
            // can arrive before there is somewhere to put it.
            probe.FrameSink = OnFrames;

            Tracing.TraceLine("MicRecorder: recording \"" + device.Name + "\" at " + rate
                + " Hz, " + channels + " channel(s) to " + path, TraceLevel.Info);
            return StartOutcome.Started;
        }

        /// <summary>
        /// Stop recording and close the file. Safe when nothing is running and
        /// safe twice. Returns the finished file's path, or empty if there is
        /// no usable file.
        /// </summary>
        public string Stop() => StopInternal(StopReason.Requested, "");

        private string StopInternal(StopReason reason, string fault)
        {
            MicProbe probe;
            WavWriter writer;
            string path;

            lock (_sync)
            {
                probe = _probe;
                writer = _writer;
                path = Path;
                _probe = null;
                _writer = null;
                if (probe == null && writer == null) return "";
                _stopReason = reason;
                if (!string.IsNullOrEmpty(fault)) _faultMessage = fault;
            }

            if (probe != null)
            {
                probe.FrameSink = null;
                probe.Stop();
                probe.Dispose();
            }

            if (writer != null)
            {
                _finishedSeconds = writer.Seconds;
                try { writer.Dispose(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("MicRecorder: closing the file failed: " + ex.Message,
                        TraceLevel.Error);
                }
            }

            _running = false;
            Tracing.TraceLine("MicRecorder: stopped (" + reason + ") after "
                + _finishedSeconds.ToString("F1") + " s — " + path, TraceLevel.Info);

            // A file with no audio in it is a trap, not a recording: it looks
            // like a take until somebody plays it. Remove it and report none.
            if (_finishedSeconds <= 0.05)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("MicRecorder: could not remove the empty file: "
                        + ex.Message, TraceLevel.Warning);
                }
                return "";
            }

            return path;
        }

        /// <summary>Capture thread. Write the block, and enforce the cap.</summary>
        private void OnFrames(float[] buffer, int count, int frames)
        {
            WavWriter w = _writer;
            if (w == null) return;

            try
            {
                w.Write(buffer, count);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("MicRecorder: write failed: " + ex.Message, TraceLevel.Error);
                // Off the capture thread — StopInternal joins that very thread,
                // and joining yourself deadlocks.
                string why = "Writing the recording failed: " + ex.Message;
                System.Threading.ThreadPool.QueueUserWorkItem(
                    _ => StopInternal(StopReason.Faulted, why));
                return;
            }

            if (w.Frames >= _maxDataFrames)
            {
                Tracing.TraceLine("MicRecorder: reached the " + MaxSeconds
                    + " second cap, stopping", TraceLevel.Info);
                System.Threading.ThreadPool.QueueUserWorkItem(
                    _ => StopInternal(StopReason.MaxLength, ""));
            }
        }

        /// <summary>Stops and closes anything still open.</summary>
        public void Dispose() => StopInternal(StopReason.Requested, "");
    }
}
