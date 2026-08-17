using System;
using System.Diagnostics;
using System.Threading;
using JJTrace;
using PortAudioSharp;

namespace JJPortaudio
{
    /// <summary>
    /// Opens one input device on its own and reports what it hears, so an
    /// operator can find out whether their microphone works without keying a
    /// transmitter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mic Track, 2026-08-12. Until now the only way to answer "is my
    /// microphone producing audio" was to transmit and read the radio's mic
    /// meter — which means going on the air to settle a question that has
    /// nothing to do with the radio. On a remote connection into a real
    /// antenna that is a genuinely bad trade.
    /// </para>
    /// <para>
    /// This is deliberately NOT built on <see cref="Audio"/> /
    /// <see cref="JJAudioStream"/>. That path binds to the two devices held in
    /// AudioAnchor's process-wide statics and runs them through the shared
    /// AudioServer work queue; borrowing it to audition a third device would
    /// mean rewriting the configured devices out from under a live radio
    /// connection, and queueing probe work behind whatever the radio path is
    /// doing. A check you can run while connected has to own its own stream.
    /// </para>
    /// <para>
    /// Blocking reads rather than a callback, for the same reason: a probe torn
    /// down by a dialog closing must not leave a marshalled callback delegate
    /// alive with PortAudio holding a pointer to it. One thread owns every
    /// PortAudio call for this stream from Pa_Initialize to Pa_Terminate, and
    /// stopping is a flag that thread notices between reads.
    /// </para>
    /// <para>
    /// PortAudio reference-counts Pa_Initialize / Pa_Terminate, so holding an
    /// initialisation open for the length of a check does not disturb the radio
    /// audio engine or the picker's Refresh — but the count itself is shared
    /// mutable state, so both calls take the same lock
    /// <see cref="Devices.Enumerate"/> uses.
    /// </para>
    /// </remarks>
    public sealed unsafe class MicProbe : IDisposable
    {
        /// <summary>How a start attempt ended.</summary>
        public enum StartOutcome
        {
            /// <summary>Capture is running.</summary>
            Started = 0,
            /// <summary>A check was already running on this probe.</summary>
            AlreadyRunning,
            /// <summary>The device is not present any more.</summary>
            DeviceGone,
            /// <summary>PortAudio refused to open it. The message carries its reason.</summary>
            OpenFailed
        }

        /// <summary>
        /// The dBFS value that means "nothing at all". Peaks are clamped here
        /// rather than running to negative infinity, which does not read well
        /// out loud and does not format.
        /// </summary>
        public const float SilenceDb = -140f;

        /// <summary>
        /// A snapshot of what the microphone is doing. Taken under lock and
        /// handed back by value, so the UI never reads a half-written state off
        /// the capture thread.
        /// </summary>
        public readonly struct Reading
        {
            /// <summary>True while capture is running.</summary>
            public bool Running { get; init; }
            /// <summary>True when capture stopped because something went wrong.</summary>
            public bool Faulted { get; init; }
            /// <summary>Why it stopped, written to be spoken. Empty unless faulted.</summary>
            public string FaultMessage { get; init; }
            /// <summary>Peak since the previous <see cref="MicProbe.Read"/>, dBFS.</summary>
            public float RecentPeakDb { get; init; }
            /// <summary>Loudest peak seen since the check started, dBFS.</summary>
            public float HoldPeakDb { get; init; }
            /// <summary>
            /// Loudness over the last three seconds, LUFS, K-weighted per
            /// BS.1770 — the same measure, from the same meter class, as the
            /// transmit path's loudness figures, so one voice never gets two
            /// vocabularies depending on which stage measured it.
            /// <see cref="LufsMeter.Floor"/> until enough audio has flowed.
            /// </summary>
            /// <remarks>
            /// Mic Level Track, 2026-08-13. Peak was the only figure here, and
            /// peak answers exactly one question — "am I clipping" — while the
            /// check is the one place an operator is actually SETTING a level.
            /// Peak cannot say when they have stopped being too quiet; that is
            /// loudness's job, and it was absent at the only stage where the
            /// Windows input level can still fix it.
            /// </remarks>
            public float ShortTermLufs { get; init; }
            /// <summary>
            /// Gated loudness of the whole check so far, LUFS — the BS.1770
            /// integrated figure, which discards the silent gaps between
            /// words. The number to report when a check ends.
            /// <see cref="LufsMeter.Floor"/> when nothing gated in yet.
            /// </summary>
            public float IntegratedLufs { get; init; }
            /// <summary>
            /// False when every sample captured so far has been exactly zero.
            /// A real microphone always has a noise floor, so an unbroken run of
            /// digital zeroes is Windows feeding us silence, not a quiet room —
            /// the single most useful thing this probe can tell anyone.
            /// </summary>
            public bool AnySound { get; init; }
            /// <summary>Frames captured since the check started.</summary>
            public long Frames { get; init; }
            /// <summary>Seconds of audio captured since the check started.</summary>
            public double Seconds { get; init; }
            /// <summary>Channels actually opened (1 for a mono device).</summary>
            public int Channels { get; init; }
            /// <summary>Sample rate actually opened.</summary>
            public int SampleRate { get; init; }
            /// <summary>
            /// The host API the check actually opened through. Not always the
            /// one the chosen row named: when the device is present under a
            /// different audio system than the one selected,
            /// <see cref="ResolveDeviceIndex"/> checks it anyway rather than
            /// telling someone their microphone is unplugged when it is not.
            /// That is the right call and it is worth saying out loud, because
            /// a check that passes under MME while transmit is configured for
            /// WASAPI is exactly the kind of disagreement this dialog exists to
            /// surface. Empty until the device opens.
            /// </summary>
            public string HostApiName { get; init; }
            /// <summary>How many reads reported dropped input. Not fatal, worth tracing.</summary>
            public long Overflows { get; init; }
        }

        /// <summary>
        /// How the capture thread reports the result of the open back to the
        /// thread that called <see cref="Start"/>. A small object rather than
        /// ref parameters because a lambda cannot capture a ref local, and
        /// never disposed because the capture thread may still be holding it
        /// after a timeout has given up on it.
        /// </summary>
        private sealed class StartGate
        {
            public readonly ManualResetEventSlim Opened = new ManualResetEventSlim(false);
            public StartOutcome Outcome = StartOutcome.OpenFailed;
            public string Message = "";

            public void Report(StartOutcome outcome, string message)
            {
                Outcome = outcome;
                Message = message;
                Opened.Set();
            }
        }

        private readonly object _sync = new object();

        private Thread _thread;
        private volatile bool _stopRequested;
        private volatile bool _running;

        private float _windowPeak;      // peak since the last Read()
        private float _holdPeak;        // peak for the whole check
        // Loudness for the check. A fresh instance per Start rather than a
        // reset: the meter's sliding windows and filter state belong to one
        // continuous capture, and a new object is the reset that cannot be
        // done halfway. Written by the capture thread via Process (internally
        // thread-safe); read from Read() through volatile getters.
        private LufsMeter _lufs = new LufsMeter();
        private float[] _lufsScratch;   // mono→stereo expansion, capture thread only
        private bool _anySound;
        private long _frames;
        private long _overflows;
        private int _channels;
        private int _sampleRate;
        private string _hostApiName = "";
        private bool _faulted;
        private string _faultMessage = "";

        /// <summary>True while a check is running.</summary>
        public bool IsRunning => _running;

        /// <summary>The device this check is running on, for the caller's own bookkeeping.</summary>
        public Devices.DeviceInfo Device { get; private set; }

        /// <summary>
        /// Open the device and start listening.
        /// </summary>
        /// <param name="device">A live enumeration row, from <see cref="Devices.InputDevices"/>.</param>
        /// <param name="message">
        /// Empty on success; otherwise why it did not start, written to be
        /// spoken as-is. PortAudio's own error text is included verbatim rather
        /// than translated — an operator reporting "Invalid sample rate" back to
        /// us is worth more than a smoothed-over "could not open".
        /// </param>
        public StartOutcome Start(Devices.DeviceInfo device, out string message)
        {
            message = "";
            if (device == null) throw new ArgumentNullException(nameof(device));

            lock (_sync)
            {
                if (_running || _thread != null) return StartOutcome.AlreadyRunning;

                _stopRequested = false;
                _windowPeak = 0f;
                _holdPeak = 0f;
                _lufs = new LufsMeter();
                _anySound = false;
                _frames = 0;
                _overflows = 0;
                _channels = 0;
                _sampleRate = 0;
                _hostApiName = "";
                _faulted = false;
                _faultMessage = "";
                Device = device;
            }

            // The open happens on the capture thread so that every PortAudio
            // call for this stream comes from one thread, but the CALLER needs
            // the outcome, so the thread reports back through this gate.
            var gate = new StartGate();
            var t = new Thread(() => CaptureProc(device, gate))
            {
                // Background: a wedged driver must never be able to hold the
                // process alive after the window is gone. The orderly path
                // still joins this thread on Stop.
                IsBackground = true,
                Name = "MicProbe"
            };
            _thread = t;
            t.Start();

            // A slow USB interface can take a moment to hand over its capture
            // endpoint. Bounded, because an unbounded wait here would freeze
            // the UI thread that called us.
            if (!gate.Opened.Wait(6000))
            {
                Tracing.TraceLine("MicProbe.Start: device did not open within 6 seconds", TraceLevel.Error);
                _stopRequested = true;
                message = "The microphone did not open within six seconds. "
                    + "It may be in use by another program.";
                JoinThread();
                return StartOutcome.OpenFailed;
            }

            if (gate.Outcome != StartOutcome.Started)
            {
                message = gate.Message;
                JoinThread();
                return gate.Outcome;
            }

            _running = true;
            return StartOutcome.Started;
        }

        /// <summary>
        /// Stop the check and close the device. Safe when nothing is running,
        /// safe twice, and it does not return until PortAudio has actually let
        /// go — a probe that outlives its dialog is exactly the failure this
        /// class exists to avoid.
        /// </summary>
        public void Stop()
        {
            _stopRequested = true;
            JoinThread();
            _running = false;
        }

        public void Dispose() => Stop();

        private void JoinThread()
        {
            Thread t = _thread;
            _thread = null;
            if (t == null) return;
            // Generous next to the ~21 ms read chunk, but bounded: an
            // unresponsive driver must not wedge the UI thread that is closing
            // the dialog. The thread is background, so abandoning it cannot pin
            // the process the way the old audio-engine waits could.
            if (!t.Join(3000))
            {
                Tracing.TraceLine("MicProbe: capture thread did not stop within 3 seconds, abandoning it",
                    TraceLevel.Error);
            }
        }

        /// <summary>
        /// Take a snapshot and reset the recent-peak window, so the value
        /// reported is "the loudest thing since you last looked" rather than a
        /// peak hold that only ever climbs.
        /// </summary>
        public Reading Read()
        {
            lock (_sync)
            {
                float window = _windowPeak;
                _windowPeak = 0f;
                return new Reading
                {
                    Running = _running && !_faulted,
                    Faulted = _faulted,
                    FaultMessage = _faultMessage,
                    RecentPeakDb = ToDb(window),
                    HoldPeakDb = ToDb(_holdPeak),
                    ShortTermLufs = _lufs.ShortTermLufs,
                    IntegratedLufs = _lufs.IntegratedLufs,
                    AnySound = _anySound,
                    Frames = _frames,
                    Seconds = (_sampleRate > 0) ? (double)_frames / _sampleRate : 0.0,
                    Channels = _channels,
                    SampleRate = _sampleRate,
                    HostApiName = _hostApiName,
                    Overflows = _overflows
                };
            }
        }

        /// <summary>
        /// Forget everything measured so far, without stopping the check.
        /// </summary>
        /// <remarks>
        /// Call this whenever the input gain changes underneath a running
        /// check. The hold peak and the integrated loudness both describe a
        /// gain setting that no longer exists the instant the operator moves
        /// the level, so carrying them forward is not merely stale — it is
        /// wrong, and wrong in the direction that matters most.
        ///
        /// <para>
        /// Found live by Noel on 2026-08-13, the same morning the level slider
        /// was added: he turned the Windows input level all the way down and
        /// the check still reported 0 dBFS and "clipping". It was reporting the
        /// loudest thing it had heard since the check started, which was before
        /// he touched anything. Reading the display means pausing, and pausing
        /// is exactly the branch that reports the hold peak — so every single
        /// adjustment appeared to do nothing. A meter that cannot be zeroed
        /// cannot be used to set a level, which made the whole control useless
        /// on the day it shipped.
        /// </para>
        ///
        /// <para>
        /// Deliberately does NOT clear <c>_anySound</c>, <c>_frames</c> or the
        /// fault state: whether this device has ever produced audio, and
        /// whether it has since broken, are facts about the DEVICE and survive
        /// a gain change.
        /// </para>
        /// </remarks>
        public void ResetLevels()
        {
            lock (_sync)
            {
                _windowPeak = 0f;
                _holdPeak = 0f;
                _lufs = new LufsMeter();
            }
        }

        /// <summary>Amplitude 0..1 to dBFS, floored at <see cref="SilenceDb"/>.</summary>
        public static float ToDb(float amplitude)
        {
            if (amplitude <= 0f) return SilenceDb;
            double db = 20.0 * Math.Log10(amplitude);
            return (db < SilenceDb) ? SilenceDb : (float)db;
        }

        // ------------------------------------------------------------ capture

        private void CaptureProc(Devices.DeviceInfo device, StartGate gate)
        {
            IntPtr stream = IntPtr.Zero;
            bool initialised = false;
            bool started = false;

            try
            {
                PortAudio.PaError perr;
                lock (Devices.PortAudioLifecycleLock)
                {
                    perr = PortAudio.Pa_Initialize();
                }
                if (perr != 0)
                {
                    gate.Report(StartOutcome.OpenFailed,
                        "The audio system would not start: " + PortAudio.Pa_GetErrorText(perr));
                    Tracing.TraceLine("MicProbe: Pa_Initialize failed, "
                        + PortAudio.Pa_GetErrorText(perr), TraceLevel.Error);
                    return;
                }
                initialised = true;

                // Re-resolve the index inside OUR initialisation. PortAudio
                // device indexes are positional and are rebuilt every time the
                // library initialises from cold, so an index captured during a
                // previous enumeration can point at a different device by the
                // time we get here — the same trap Devices.FindDevice refuses to
                // fall into. Identity is name plus host API, never index.
                int index = ResolveDeviceIndex(device, out PortAudio.PaDeviceInfo info,
                    out string openedApi);
                if (index < 0)
                {
                    gate.Report(StartOutcome.DeviceGone,
                        device.Name + " is not connected any more. "
                        + "Choose Refresh device list, then pick it again.");
                    Tracing.TraceLine("MicProbe: device \"" + device.Name + "\" not found at check time",
                        TraceLevel.Error);
                    return;
                }

                // A mono device gets opened as mono, and has since this class
                // was written — which is why the radio-audio engine copied this
                // shape rather than inventing its own when it learned to do the
                // same on 2026-08-16. Both paths now open at the device's own
                // channel count and duplicate mono onto both channels, so a
                // mono microphone measures here exactly as it transmits.
                int channels = (info.maxInputChannels >= Devices.StreamChannels)
                    ? Devices.StreamChannels : 1;

                var parms = new PortAudio.PaStreamParameters
                {
                    device = index,
                    channelCount = channels,
                    sampleFormat = PortAudio.PaSampleFormat.paFloat32,
                    suggestedLatency = info.defaultLowInputLatency,
                    hostApiSpecificStreamInfo = (IntPtr)null
                };

                double rate = ChooseSampleRate(ref parms, info);

                PortAudio.PaStreamParameters* nullParms = null;
                perr = PortAudio.Pa_OpenStream(
                    out stream,
                    ref parms,
                    ref *nullParms,
                    rate,
                    (uint)PortAudio.paFramesPerBufferUnspecified,
                    PortAudio.PaStreamFlags.paNoFlag,
                    null,                 // a null callback selects blocking reads
                    IntPtr.Zero);
                if (perr != 0)
                {
                    gate.Report(StartOutcome.OpenFailed,
                        "Windows would not open " + device.Name + ": "
                        + PortAudio.Pa_GetErrorText(perr) + ".");
                    Tracing.TraceLine("MicProbe: Pa_OpenStream failed for \"" + device.Name
                        + "\" at " + rate + " Hz, " + channels + " channel(s): "
                        + PortAudio.Pa_GetErrorText(perr), TraceLevel.Error);
                    stream = IntPtr.Zero;
                    return;
                }

                perr = PortAudio.Pa_StartStream(stream);
                if (perr != 0)
                {
                    gate.Report(StartOutcome.OpenFailed,
                        device.Name + " opened but would not start: "
                        + PortAudio.Pa_GetErrorText(perr) + ".");
                    Tracing.TraceLine("MicProbe: Pa_StartStream failed: "
                        + PortAudio.Pa_GetErrorText(perr), TraceLevel.Error);
                    return;
                }
                started = true;

                lock (_sync)
                {
                    _channels = channels;
                    _sampleRate = (int)rate;
                    _hostApiName = openedApi;
                }

                Tracing.TraceLine("MicProbe: listening to \"" + device.Name + "\" ("
                    + openedApi + ") at " + rate + " Hz, " + channels + " channel(s)"
                    + (string.Equals(openedApi, device.HostApiName, StringComparison.Ordinal)
                        ? "" : " — the chosen row named " + device.HostApiName),
                    TraceLevel.Info);

                gate.Report(StartOutcome.Started, "");

                // ~21 ms per read at 48 kHz: short enough that Stop is prompt,
                // long enough that the interop cost is nothing.
                const uint chunkFrames = 1024;
                var buffer = new float[chunkFrames * channels];

                while (!_stopRequested)
                {
                    PortAudio.PaError rerr = PortAudio.Pa_ReadStream(stream, buffer, chunkFrames);
                    if (rerr == PortAudio.PaError.paInputOverflowed)
                    {
                        // Non-fatal and expected on a busy machine: PortAudio
                        // discarded input it could not hand us, and the buffer
                        // it did hand us is still good. Count it — a check that
                        // is dropping audio is worth knowing about in a trace.
                        lock (_sync) { _overflows++; }
                    }
                    else if (rerr != 0)
                    {
                        Fault("The microphone stopped: " + PortAudio.Pa_GetErrorText(rerr) + ".");
                        Tracing.TraceLine("MicProbe: Pa_ReadStream failed: "
                            + PortAudio.Pa_GetErrorText(rerr), TraceLevel.Error);
                        break;
                    }

                    Accumulate(buffer, (int)chunkFrames * channels, (int)chunkFrames);
                    FeedLoudnessMeter(buffer, (int)chunkFrames, channels, (uint)rate);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("MicProbe: capture thread exception — " + ex.Message, TraceLevel.Error);
                Fault("The microphone check stopped unexpectedly: " + ex.Message);
                // If we never got as far as reporting, the caller is still
                // waiting on the gate. Release it with the failure.
                if (!gate.Opened.IsSet)
                    gate.Report(StartOutcome.OpenFailed,
                        "The microphone check could not start: " + ex.Message);
            }
            finally
            {
                // Never leave the caller blocked on the gate, whatever happened.
                if (!gate.Opened.IsSet)
                    gate.Report(StartOutcome.OpenFailed, "The microphone check could not start.");

                // Teardown in the reverse of setup, each step guarded, so one
                // failing step cannot strand the ones after it. This is the
                // discipline the audio engine learned the hard way: a stream
                // that is never closed is a device no other program can open.
                if (stream != IntPtr.Zero)
                {
                    if (started)
                    {
                        PortAudio.PaError serr = PortAudio.Pa_StopStream(stream);
                        if (serr != 0)
                        {
                            Tracing.TraceLine("MicProbe: Pa_StopStream returned "
                                + PortAudio.Pa_GetErrorText(serr) + ", aborting instead",
                                TraceLevel.Error);
                            PortAudio.Pa_AbortStream(stream);
                        }
                    }
                    PortAudio.PaError cerr = PortAudio.Pa_CloseStream(stream);
                    if (cerr != 0)
                    {
                        Tracing.TraceLine("MicProbe: Pa_CloseStream returned "
                            + PortAudio.Pa_GetErrorText(cerr), TraceLevel.Error);
                    }
                }

                if (initialised)
                {
                    lock (Devices.PortAudioLifecycleLock)
                    {
                        PortAudio.Pa_Terminate();
                    }
                }

                lock (_sync)
                {
                    Tracing.TraceLine("MicProbe: check ended after " + _frames + " frames, "
                        + _overflows + " overflow(s), peak "
                        + ToDb(_holdPeak).ToString("F1") + " dBFS, loudness "
                        + _lufs.IntegratedLufs.ToString("F1") + " LUFS, any sound: " + _anySound,
                        TraceLevel.Info);
                }
                _running = false;
            }
        }

        private void Fault(string message)
        {
            lock (_sync)
            {
                _faulted = true;
                _faultMessage = message;
            }
        }

        private void Accumulate(float[] buffer, int samples, int frames)
        {
            float peak = 0f;
            bool sound = false;
            for (int i = 0; i < samples; i++)
            {
                float s = buffer[i];
                if (s != 0f) sound = true;
                float a = (s < 0f) ? -s : s;
                if (a > peak) peak = a;
            }

            lock (_sync)
            {
                if (peak > _windowPeak) _windowPeak = peak;
                if (peak > _holdPeak) _holdPeak = peak;
                if (sound) _anySound = true;
                _frames += frames;
            }
        }

        /// <summary>
        /// Hand one captured chunk to the loudness meter. Capture thread only.
        /// </summary>
        /// <remarks>
        /// Mic Level Track, 2026-08-13. The meter is the same LufsMeter the
        /// transmit path runs — same K-weighting, same gating — fed here with
        /// the same shapes the TX path carries: a stereo capture goes in as it
        /// arrived, and a mono device is duplicated onto both channels, which
        /// is how the TX stream carries a mono mic. For identical L/R content
        /// BS.1770's channel-power sum lands the figure on the dBFS-comparable
        /// scale every other loudness surface in the app reads, so the number
        /// spoken here and the number spoken at transmit are the same
        /// vocabulary about the same voice.
        /// </remarks>
        private void FeedLoudnessMeter(float[] buffer, int frames, int channels, uint sampleRate)
        {
            LufsMeter meter = _lufs;
            if (channels == 2)
            {
                meter.Process(buffer, frames * 2, sampleRate);
                return;
            }

            if (_lufsScratch == null || _lufsScratch.Length < frames * 2)
                _lufsScratch = new float[frames * 2];
            int j = 0;
            for (int i = 0; i < frames; i++)
            {
                float s = buffer[i];
                _lufsScratch[j++] = s;
                _lufsScratch[j++] = s;
            }
            meter.Process(_lufsScratch, frames * 2, sampleRate);
        }

        /// <summary>
        /// Find this device in the CURRENT PortAudio enumeration by name and
        /// host API. Returns -1 when it is gone.
        /// </summary>
        private static int ResolveDeviceIndex(Devices.DeviceInfo want, out PortAudio.PaDeviceInfo info,
            out string hostApiName)
        {
            info = new PortAudio.PaDeviceInfo();
            hostApiName = "";
            int count = PortAudio.Pa_GetDeviceCount();
            if (count < 0) return -1;

            int nameOnlyMatch = -1;
            PortAudio.PaDeviceInfo nameOnlyInfo = new PortAudio.PaDeviceInfo();
            string nameOnlyApi = "";

            for (int i = 0; i < count; i++)
            {
                PortAudio.PaDeviceInfo candidate = PortAudio.Pa_GetDeviceInfo(i);
                if (candidate.maxInputChannels < 1) continue;
                if (!string.Equals(candidate.name, want.Name, StringComparison.Ordinal)) continue;

                int apiTypeId = -1;
                string apiName = "";
                try
                {
                    PortAudio.PaHostApiInfo api = PortAudio.Pa_GetHostApiInfo(candidate.hostApi);
                    apiTypeId = (int)api.type;
                    apiName = api.name ?? "";
                }
                catch { /* a device with no readable host API record is still openable */ }

                if (apiTypeId == want.HostApiTypeId)
                {
                    info = candidate;
                    hostApiName = apiName;
                    return i;
                }
                if (nameOnlyMatch < 0)
                {
                    nameOnlyMatch = i;
                    nameOnlyInfo = candidate;
                    nameOnlyApi = apiName;
                }
            }

            // Same name, different host API: the device is there, the API is
            // not. Checking it is still the right answer, and better than
            // telling someone their microphone is unplugged when it is not.
            // The API actually used is handed back so the caller can say so —
            // a check that passes under one audio system while transmit is
            // configured for another is the exact disagreement this dialog is
            // here to make visible.
            if (nameOnlyMatch >= 0)
            {
                Tracing.TraceLine("MicProbe: \"" + want.Name + "\" found under "
                    + (nameOnlyApi.Length > 0 ? nameOnlyApi : "a different host API")
                    + " rather than the chosen " + want.HostApiName
                    + "; checking it anyway", TraceLevel.Info);
                info = nameOnlyInfo;
                hostApiName = nameOnlyApi;
                return nameOnlyMatch;
            }
            return -1;
        }

        /// <summary>
        /// Pick a rate the device will actually accept: its own default first —
        /// under WASAPI shared mode that is the only rate the Windows mixer
        /// takes without a resampler — then the usual suspects.
        /// </summary>
        private static double ChooseSampleRate(ref PortAudio.PaStreamParameters parms,
            PortAudio.PaDeviceInfo info)
        {
            PortAudio.PaStreamParameters* nullParms = null;

            double preferred = info.defaultSampleRate;
            if (preferred > 0
                && PortAudio.Pa_IsFormatSupported(ref parms, ref *nullParms, preferred) == 0)
            {
                return preferred;
            }

            foreach (double rate in new[] { 48000.0, 44100.0, 32000.0, 24000.0, 16000.0, 8000.0 })
            {
                if (PortAudio.Pa_IsFormatSupported(ref parms, ref *nullParms, rate) == 0)
                {
                    Tracing.TraceLine("MicProbe: device default rate " + preferred
                        + " rejected, using " + rate, TraceLevel.Info);
                    return rate;
                }
            }

            // Nothing was accepted. Open at the device's default anyway and let
            // Pa_OpenStream produce the real error text — a guess invented here
            // would be less informative than PortAudio's own answer.
            Tracing.TraceLine("MicProbe: no sample rate reported supported; trying " + preferred,
                TraceLevel.Error);
            return (preferred > 0) ? preferred : 48000.0;
        }
    }
}
