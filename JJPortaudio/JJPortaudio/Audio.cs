using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using JJTrace;
using POpusCodec;
using PortAudioSharp;

namespace JJPortaudio
{
    static unsafe class AudioAnchor
    {
        public static Devices.Device inDevice, outDevice;
        private static Thread server;

        internal enum workItems
        {
            negotiate,
            open,
            close,
            terminate,
            start,
            stop
        }
        internal class workItem
        {
            public workItems Type;
            public Audio.StreamCB StreamBlock;
            public workItem(workItems type)
            {
                Type = type;
            }
            public workItem(workItems type, Audio.StreamCB cb)
            {
                Type = type;
                StreamBlock = cb;
            }
        }
        static internal BlockingCollection<workItem> work;

        public static void Init(Devices.Device inDev, Devices.Device outDev)
        {
            Tracing.TraceLine("AudioAnchor.init:" +
                (string)((inDev != null) ? inDev.Name : "") + ' ' +
                (string)((outDev != null) ? outDev.Name : ""), TraceLevel.Info);
            inDevice = inDev;
            outDevice = outDev;
            work = new BlockingCollection<workItem>();
            server = new Thread(serverProc);
            server.Name = "AudioServer";
            server.Priority = ThreadPriority.Normal;
            // Engine Track (2026-08-11): background, so a wedged PortAudio
            // close can no longer pin the whole process alive after the UI
            // exits — the field-confirmed orphan jjflexible.exe hang. The
            // orderly path is unchanged: Term() still drains the work queue
            // through Pa_Terminate before the process goes down; background
            // only matters when that path is already stuck, and the old
            // outcome was a ghost process racing the next instance for the
            // config file.
            server.IsBackground = true;
            server.Start();
            // Note the server does the Pa_Initialize().
            Thread.Yield();
        }

        public static void Term()
        {
            Tracing.TraceLine("AudioAnchor.Terminate", TraceLevel.Info);
            try
            {
                workItem item = new workItem(workItems.terminate);
                work.Add(item);
                if (!server.Join(1000))
                {
                    Tracing.TraceLine("AudioAnchor.Terminate:server didn't terminate", TraceLevel.Error);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AudioAnchor.Terminate exception:" + ex.Message, TraceLevel.Error);
            }
        }

        /// <summary>
        /// The PaStreamParameters for a stream block. Shared by negotiate and
        /// open so the format the rate was checked against is byte-for-byte the
        /// format the stream is opened with — a rate accepted for one channel
        /// count or latency is not evidence about another.
        /// </summary>
        private static PortAudio.PaStreamParameters deviceParams(Audio.StreamCB cb)
        {
            PortAudio.PaStreamParameters p = new PortAudio.PaStreamParameters();
            p.device = cb.Device.DevinfoID;
            // The device's own channel count, not a hardcoded 2. This was
            // `p.channelCount = 2` unconditionally, which PortAudio must reject
            // on a one-channel device — so a mono microphone could not be used
            // at all, and the only workaround (gang two interface inputs, pan
            // both to centre) needed a multi-channel interface to perform. A
            // single mono USB headset mic had no workaround whatever, and a
            // mono device is frequently somebody's only microphone.
            p.channelCount = channelsFor(cb.Device);
            p.sampleFormat = PortAudio.PaSampleFormat.paFloat32;
            p.suggestedLatency = (cb.Device.Type == Devices.DeviceTypes.input) ?
                cb.Device.defaultLowInputLatency : cb.Device.defaultLowOutputLatency;
            // Zero normally. Non-zero only when negotiation engaged the
            // WASAPI shared-mode converter as a last resort (#12) — and
            // because this function is the one source of stream parameters,
            // the format the rate was checked against and the format the
            // stream opens with carry the identical stream info, preserving
            // the byte-for-byte invariant this function exists for.
            p.hostApiSpecificStreamInfo = cb.WasapiAutoConvertInfo;
            return p;
        }

        /// <summary>
        /// Log what PortAudio reports about a freshly opened stream: the
        /// driver's own latency claim, and the rate it really settled on.
        /// </summary>
        /// <remarks>
        /// Track J, 2026-09-01 (#462). Our side of the latency budget is
        /// arithmetic and can be read off the source; the driver's side cannot,
        /// and PortAudio has been offering it through <c>Pa_GetStreamInfo</c>
        /// all along with nothing calling it. Logged once per open, so a
        /// session's trace carries the figure without anyone having to
        /// reproduce anything.
        /// <para>
        /// Reported latency is a CLAIM, not a measurement — WASAPI and MME
        /// derive it differently and neither is obliged to be right. The
        /// callback-timing figures in the close summary are the measurement;
        /// this is what the driver said it would be.
        /// </para>
        /// </remarks>
        private static void traceStreamLatency(Audio.StreamCB cb)
        {
            try
            {
                PortAudio.PaStreamInfo info = PortAudio.Pa_GetStreamInfo(cb.Stream);
                bool isInput = (cb.Device.Type == Devices.DeviceTypes.input);
                double reported = isInput ? info.inputLatency : info.outputLatency;
                cb.ReportedDeviceLatency = reported;
                Tracing.TraceLine("audio " + (isInput ? "input" : "output")
                    + " stream latency: PortAudio reports "
                    + (reported * 1000).ToString("F1") + " ms for the device,"
                    + " on top of our own "
                    + AudioBuffering.BufferMilliseconds(cb.BufferSize, cb.SampleRate).ToString("F1")
                    + " ms buffer (stream rate " + info.sampleRate.ToString("F0") + " Hz)",
                    TraceLevel.Info);
            }
            catch (Exception ex)
            {
                // Never fail an open over a diagnostic.
                Tracing.TraceLine("audio stream latency: Pa_GetStreamInfo failed, "
                    + ex.Message, TraceLevel.Info);
            }
        }

        /// <summary>
        /// Channels to open on a saved device: stereo when it has two or more,
        /// mono when it genuinely has one. Mirrors
        /// <see cref="Devices.DeviceInfo.OpenChannels"/> for the persisted
        /// shape, which is what the engine holds.
        /// </summary>
        internal static int channelsFor(Devices.Device d)
        {
            if (d == null) return Devices.StreamChannels;
            int native = (d.Type == Devices.DeviceTypes.input)
                ? d.maxInputChannels : d.maxOutputChannels;
            return (native >= Devices.StreamChannels) ? Devices.StreamChannels : 1;
        }

        /// <summary>
        /// The host API a device index actually belongs to, read from
        /// PortAudio inside the server's own initialisation.
        /// </summary>
        /// <remarks>
        /// Track E, 2026-08-16. The engine logged device name and sample rate
        /// and never the host API — and the name is identical across all four
        /// APIs, so the trace could not distinguish an MME open from a WASAPI
        /// one. For a stream whose host API decides whether its reported rate
        /// is genuine or quietly resampled, that was the single most useful
        /// fact missing from the log. Read live rather than taken from the
        /// saved device record, because the record can be an older file and
        /// the point of the line is what really happened.
        /// </remarks>
        private static string hostApiOf(int devInfoId)
        {
            try
            {
                PortAudio.PaDeviceInfo info = PortAudio.Pa_GetDeviceInfo(devInfoId);
                return PortAudio.Pa_GetHostApiInfo(info.hostApi).name ?? "unknown host API";
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("hostApiOf(" + devInfoId + ") failed: " + ex.Message, TraceLevel.Info);
                return "unknown host API";
            }
        }

        /// <summary>
        /// True when a device index belongs to WASAPI — the one host API
        /// whose shared-mode rate refusal has a caller-side remedy (#12).
        /// Read live for the same reason hostApiOf is.
        /// </summary>
        private static bool isWasapiDevice(int devInfoId)
        {
            try
            {
                PortAudio.PaDeviceInfo info = PortAudio.Pa_GetDeviceInfo(devInfoId);
                return PortAudio.Pa_GetHostApiInfo(info.hostApi).type
                    == PortAudio.PaHostApiTypeId.paWASAPI;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True for the five rates Opus can encode. There is no 44.1 kHz mode,
        /// which is exactly why a device that insists on 44100 cannot simply be
        /// accommodated by opening the stream and hoping.
        /// </summary>
        internal static bool isOpusRate(uint rate)
        {
            return rate == 8000 || rate == 12000 || rate == 16000
                || rate == 24000 || rate == 48000;
        }

        private static void serverProc()
        {
            PortAudio.PaError erv;
            erv = PortAudio.Pa_Initialize();
            if (erv < 0)
            {
                Tracing.TraceLine("Init error:" + PortAudio.Pa_GetErrorText(erv), TraceLevel.Error);
                return;
            }

            while (!work.IsAddingCompleted)
            {
                workItem item;
                try { item = work.Take(); }
                catch (InvalidOperationException)
                {
                    break;
                }

                switch (item.Type)
                {
                    case workItems.negotiate:
                        {
                            // Settle the sample rate BEFORE the caller derives
                            // anything from it. This check used to live in the
                            // open case below, which runs long after Audio.Open
                            // has sized its buffers and built the Opus encoder
                            // from the rate it *asked* for — so a device that
                            // refused 48 kHz got a stream at its own rate
                            // feeding a codec still convinced it was 48 kHz.
                            // Nothing failed and nothing was logged: the input
                            // callback simply fired 9.19 times a second instead
                            // of 10, and we emitted ~92 Opus frames per second
                            // where the radio expects 100. That reads as
                            // periodic gaps, not as a broken feature, which is
                            // why it survived.
                            var cb = item.StreamBlock;
                            PortAudio.PaStreamParameters devParms = deviceParams(cb);
                            PortAudio.PaStreamParameters* nullParms = null;
                            PortAudio.PaStreamParameters* p1 = (cb.Device.Type == Devices.DeviceTypes.input) ?
                                &devParms : nullParms;
                            PortAudio.PaStreamParameters* p2 = (cb.Device.Type == Devices.DeviceTypes.output) ?
                                &devParms : nullParms;

                            uint requested = cb.SampleRate;
                            // The host API is named on every one of these lines
                            // because it is what decides whether the answer is
                            // genuine: MME resamples on the way through and will
                            // accept almost anything, so "accepts 48 kHz" from
                            // MME is a statement about MME rather than about the
                            // hardware. WASAPI's answer is about the hardware.
                            string negApi = hostApiOf(cb.Device.DevinfoID);
                            if (requested > 0
                                && PortAudio.Pa_IsFormatSupported(ref *p1, ref *p2, (double)requested) == 0)
                            {
                                Tracing.TraceLine("server:negotiate:" + cb.Device.Name
                                    + " [" + negApi + ", " + devParms.channelCount + " ch]"
                                    + " accepts the requested " + requested + " Hz", TraceLevel.Info);
                            }
                            else
                            {
                                // Candidates: the device's own default first —
                                // it is the rate the hardware is most likely
                                // actually running — then the usual ladder.
                                var candidates = new List<uint>();
                                uint dflt = (uint)cb.Device.defaultSampleRate;
                                if (dflt > 0) candidates.Add(dflt);
                                foreach (uint r in new uint[] { 48000, 44100, 32000, 24000, 16000, 12000, 8000 })
                                {
                                    if (!candidates.Contains(r)) candidates.Add(r);
                                }
                                // An Opus stream may only consider rates Opus
                                // can encode. Accepting 44100 here because the
                                // device likes it would put us straight back
                                // into the mismatch this work item exists to
                                // prevent — the codec has no 44.1 kHz mode to
                                // follow the device into.
                                if (cb.UseOpus) candidates.RemoveAll(r => !isOpusRate(r));

                                uint settled = 0;
                                foreach (uint cand in candidates)
                                {
                                    if (cand == requested) continue; // already refused
                                    if (PortAudio.Pa_IsFormatSupported(ref *p1, ref *p2, (double)cand) == 0)
                                    {
                                        settled = cand;
                                        break;
                                    }
                                }

                                if (settled != 0)
                                {
                                    Tracing.TraceLine("server:negotiate:" + cb.Device.Name
                                        + " [" + negApi + ", " + devParms.channelCount + " ch]"
                                        + " refused " + requested + " Hz, using " + settled
                                        + " Hz" + (cb.UseOpus ? " (Opus-legal)" : ""), TraceLevel.Error);
                                    cb.SampleRate = settled;
                                }
                                else
                                {
                                    // Nothing was accepted natively. For an
                                    // Opus stream on a WASAPI device there is
                                    // one more honest move before giving up
                                    // (#12): the endpoint's shared-mode format
                                    // is a rate Opus cannot encode — 44.1 kHz
                                    // being the usual one — and the ladder
                                    // legally cannot follow the device there.
                                    // Engage WASAPI's own shared-mode
                                    // converter (paWinWasapiAutoConvert) and
                                    // keep the requested rate: the codec gets
                                    // the cadence it needs, Windows bridges
                                    // the rates, and the trace says plainly
                                    // that resampling is happening — the
                                    // native-first ordering above means every
                                    // device that CAN run without conversion
                                    // still does.
                                    bool rescued = false;
                                    if (cb.UseOpus && isWasapiDevice(cb.Device.DevinfoID))
                                    {
                                        cb.WasapiAutoConvertInfo = WasapiAutoConvert.Allocate();
                                        devParms.hostApiSpecificStreamInfo = cb.WasapiAutoConvertInfo;
                                        if (PortAudio.Pa_IsFormatSupported(ref *p1, ref *p2, (double)requested) == 0)
                                        {
                                            rescued = true;
                                            Tracing.TraceLine("server:negotiate:" + cb.Device.Name
                                                + " [" + negApi + ", " + devParms.channelCount + " ch]"
                                                + " offers no Opus-legal rate natively (device default "
                                                + cb.Device.defaultSampleRate + " Hz); opening at "
                                                + requested + " Hz with the WASAPI shared-mode "
                                                + "converter engaged — Windows resamples between the "
                                                + "stream and the device's own rate. Native, "
                                                + "conversion-free audio needs the device set to "
                                                + requested + " Hz in Windows Sound settings.",
                                                TraceLevel.Error);
                                        }
                                        else
                                        {
                                            WasapiAutoConvert.Release(ref cb.WasapiAutoConvertInfo);
                                            devParms.hostApiSpecificStreamInfo = IntPtr.Zero;
                                        }
                                    }
                                    if (!rescued)
                                    {
                                        // Keep the requested rate and let
                                        // Pa_OpenStream produce the real error
                                        // text — PortAudio's own answer is
                                        // worth more to whoever reads the
                                        // trace than a rate invented here.
                                        // With the AutoConvert rescue above,
                                        // reaching this under WASAPI means
                                        // even the converter was refused;
                                        // under other host APIs it means what
                                        // it always did.
                                        Tracing.TraceLine("server:negotiate:" + cb.Device.Name
                                            + " [" + negApi + ", " + devParms.channelCount + " ch]"
                                            + " reported no usable rate (device default "
                                            + cb.Device.defaultSampleRate + " Hz); leaving " + requested
                                            + " Hz for the open to fail on. Set the device to 48000 Hz in "
                                            + "Windows Sound settings, or choose MME as the audio system, "
                                            + "which converts rates for you.", TraceLevel.Error);
                                    }
                                }
                            }
                            cb.RateSettled = true;
                        }
                        break;
                    case workItems.open:
                        {
                            Tracing.TraceLine("server:open:" + item.StreamBlock.Device.Name
                                + " [" + hostApiOf(item.StreamBlock.Device.DevinfoID) + "] "
                                + item.StreamBlock.SampleRate + " Hz, "
                                + item.StreamBlock.Channels + " channel(s)"
                                + (item.StreamBlock.WasapiAutoConvertInfo != IntPtr.Zero
                                    ? ", WASAPI shared-mode converter engaged" : ""), TraceLevel.Info);
                            PortAudio.PaStreamParameters devParms = deviceParams(item.StreamBlock);
                            PortAudio.PaStreamParameters* nullParms = null;
                            PortAudio.PaStreamParameters* p1 = (item.StreamBlock.Device.Type == Devices.DeviceTypes.input) ?
                                &devParms : nullParms;
                            PortAudio.PaStreamParameters* p2 = (item.StreamBlock.Device.Type == Devices.DeviceTypes.output) ?
                                &devParms : nullParms;

                            erv = PortAudio.Pa_IsFormatSupported(ref *p1, ref *p2, (double)item.StreamBlock.SampleRate);
                            if (erv != 0)
                            {
                                // The rate was negotiated against this exact
                                // format moments ago, so a refusal now means
                                // the device changed underneath us. Opening
                                // anyway and rewriting the rate — which is what
                                // this code used to do — would reintroduce the
                                // encoder/stream split, so fail instead. The
                                // caller sees the false return Open always
                                // promised.
                                Tracing.TraceLine("server:open:device no longer accepts "
                                    + item.StreamBlock.SampleRate + " Hz ("
                                    + PortAudio.Pa_GetErrorText(erv) + "); not opening",
                                    TraceLevel.Error);
                            }
                            else
                            {
                                // framesPerBuffer, not sample count. BufferSize
                                // counts samples of the STEREO path the codec
                                // and the queues work in, so frames are always
                                // BufferSize / 2 — the device's own channel
                                // count does not enter into it. A mono device
                                // hands back half as many samples for the same
                                // number of frames, which is what the callbacks
                                // walk (see data.Channels there).
                                erv = PortAudio.Pa_OpenStream(
                                    out item.StreamBlock.Stream,
                                    ref *p1,
                                    ref *p2,
                                    item.StreamBlock.SampleRate,
                                    item.StreamBlock.BufferSize / Devices.StreamChannels,
                                    PortAudio.PaStreamFlags.paNoFlag,
                                    item.StreamBlock.CB,
                                    (IntPtr)item.StreamBlock.CBUser);
                                if (erv < 0)
                                {
                                    Tracing.TraceLine("open error:" + PortAudio.Pa_GetErrorText(erv), TraceLevel.Error);
                                }
                                else
                                {
                                    item.StreamBlock.Open = true;
                                    traceStreamLatency(item.StreamBlock);
                                }
                            }
                        }
                        break;
                    case workItems.start:
                        {
                            Tracing.TraceLine("server:start", TraceLevel.Info);
                            if (item.StreamBlock.Open & !item.StreamBlock.Started)
                            {
                                erv = PortAudio.Pa_StartStream(item.StreamBlock.Stream);
                                item.StreamBlock.Active = (erv >= 0);
                                if (item.StreamBlock.Active)
                                {
                                    // Clear out any prior stuff in the queue (output only).
                                    if (item.StreamBlock.Q != null)
                                    {
                                        item.StreamBlock.Q.Clear();
                                    }
                                    Tracing.TraceLine("server:start channel started", TraceLevel.Info);
                                    item.StreamBlock.Started = true;
                                }
                                else
                                {
                                    Tracing.TraceLine("start error:" + PortAudio.Pa_GetErrorText(erv), TraceLevel.Error);
                                }
                            }
                            else Tracing.TraceLine("start error:stream not open or start/stop error", TraceLevel.Error);
                        }
                        break;
                    case workItems.stop:
                    case workItems.close:
                        {
                            Tracing.TraceLine("serverProc:stop or close", TraceLevel.Info);
                            if (item.StreamBlock.Open && item.StreamBlock.Started)
                            {
                                item.StreamBlock.Active = false;
                                // Wait for the callback to complete the stream —
                                // BOUNDED (Engine Track, 2026-08-11). This wait
                                // was unbounded; a stuck device or driver left
                                // the AudioServer thread here forever, every
                                // later work item (including the input stream's
                                // close) queued behind it, Audio.Finished()
                                // waiting on that close forever, and the whole
                                // process pinned — the orphan-shutdown chain.
                                int activeWait = 20; // ~2.2 s at 110 ms/poll
                                while ((int)PortAudio.Pa_IsStreamActive(item.StreamBlock.Stream) == 1
                                    && activeWait-- > 0) Thread.Sleep(110);
                                Tracing.TraceLine("serverProc:stop or close:wait done", TraceLevel.Info);
                                if ((int)PortAudio.Pa_IsStreamActive(item.StreamBlock.Stream) == 1)
                                {
                                    // The callback never completed: error path.
                                    // Abort discards pending buffers instead of
                                    // waiting for a driver that already isn't
                                    // answering.
                                    Tracing.TraceLine("serverProc:stream still active after 2.2s, aborting stream", TraceLevel.Error);
                                    erv = PortAudio.Pa_AbortStream(item.StreamBlock.Stream);
                                }
                                else
                                {
                                    erv = PortAudio.Pa_StopStream(item.StreamBlock.Stream);
                                }
                                Tracing.TraceLine("serverProc:stop or close:stop done", TraceLevel.Info);
                                if (erv < 0)
                                {
                                    Tracing.TraceLine("stop error:" + PortAudio.Pa_GetErrorText(erv), TraceLevel.Error);
                                }
                                item.StreamBlock.Started = false;
                            }
                            if (item.Type == workItems.close)
                            {
                                Tracing.TraceLine("serverProc:close", TraceLevel.Info);
                                if (item.StreamBlock.Open)
                                {
                                    erv = PortAudio.Pa_CloseStream(item.StreamBlock.Stream);
                                    if (erv != 0) Tracing.TraceLine("close of stream returned: " + PortAudio.Pa_GetErrorText(erv), TraceLevel.Error);
                                    else item.StreamBlock.Open = false;
                                }
                                if (item.StreamBlock.opusPool != null) item.StreamBlock.opusPool.Done();
                                if (item.StreamBlock.Encoder != null)
                                {
                                    OpusEncoder enc = item.StreamBlock.Encoder;
                                    // Null it FIRST — the setter drops the
                                    // pipeline's encode delegate with it, so a
                                    // frame arriving late finds nothing to
                                    // encode rather than a disposed encoder.
                                    item.StreamBlock.Encoder = null;
                                    enc.Dispose();
                                }
                                if (item.StreamBlock.Decoder != null) item.StreamBlock.Decoder.Dispose();
                                WasapiAutoConvert.Release(ref item.StreamBlock.WasapiAutoConvertInfo);
                                Audio.queues.Remove(item.StreamBlock.CBUser);
                            }
                        }
                        break;
                    case workItems.terminate:
                        {
                            Tracing.TraceLine("Audio server terminating portaudio", TraceLevel.Info);
                            erv = PortAudio.Pa_Terminate();
                            if (erv == 0) Tracing.TraceLine("AudioServer:portAudio terminated", TraceLevel.Info);
                            else Tracing.TraceLine("Pa_Terminate returned: " + PortAudio.Pa_GetErrorText(erv), TraceLevel.Error);
                            work.CompleteAdding(); // exit the loop
                        }
                        break;
                }
            }
            Tracing.TraceLine("Audioserver done", TraceLevel.Info);
        }
    }

    public unsafe class Audio
    {
        private const uint defaultBufsize = 115200;
        private IntPtr stream = (IntPtr)0;

        internal Devices.Device inDevice { get { return AudioAnchor.inDevice; } }
        internal Devices.Device outDevice { get { return AudioAnchor.outDevice; } }

        private static PortAudio.PaStreamCallbackDelegate inCallback = new PortAudio.PaStreamCallbackDelegate(inputCallback);
        private static PortAudio.PaStreamCallbackDelegate outCallback = new PortAudio.PaStreamCallbackDelegate(outputCallback);

        public delegate void WavCallback(float[] data);
        public delegate void OpusCallback(byte[] data);
        public delegate void AudioSentCallback();

        internal class StreamCB
        {
            public Devices.Device Device;
            public bool Open = false;
            public bool Active = false;
            public bool Started = false; // true if started and out of start/stop code.
            public IntPtr Stream;
            public bool IsAlive
            {
                get { return ((int)PortAudio.Pa_IsStreamActive(Stream) == 1); }
            }
            public bool UseOpus = false;
            public uint OpusFrameSZ;
            public bufPool opusPool;
            /// <summary>
            /// The shared transmit tail — injection, conditioning, metering,
            /// encode, send — written once so the capture callback and the
            /// self-clocked source cannot drift apart. See TxFramePipeline.
            /// </summary>
            public readonly TxFramePipeline TxPipeline = new TxFramePipeline();
            private OpusEncoder _encoder;
            /// <summary>
            /// Setting this rebinds the pipeline's encode step in the same
            /// breath, so there is no window in which the stream holds one
            /// encoder and the pipeline encodes through another — including at
            /// close, where a null here nulls the pipeline's too and a frame
            /// arriving late cannot reach a disposed encoder.
            /// </summary>
            public OpusEncoder Encoder
            {
                get { return _encoder; }
                set
                {
                    _encoder = value;
                    // Track J, 2026-09-01 (#460): the encode step is now built
                    // from the encoder rather than being its Encode method
                    // directly, because a MONO encoder needs the pipeline's
                    // interleaved-stereo frame folded first. The choice is
                    // derived from the encoder's own InputChannels, so a stereo
                    // encoder still gets exactly value.Encode and the two can
                    // never disagree about which shape is in flight.
                    TxPipeline.Encode = OpusEncodeProfile.BuildEncodeStep(value);
                }
            }
            public OpusDecoder Decoder;

            /// <summary>
            /// This stream's answer to the pipeline's teardown question,
            /// cached once. The input callback passes it a hundred times a
            /// second and must not allocate a closure per frame.
            /// </summary>
            public readonly Func<bool> IsActive;

            public StreamCB()
            {
                IsActive = () => Active;
            }
            public Queue Q = Queue.Synchronized(new Queue());
            public uint Offset = 0; // outputCallback's buffer offset
            public float[] Buffer; // for output data
            public uint BufferSize;
            public uint SampleRate;
            /// <summary>
            /// Channels the PortAudio stream was opened with: 2 normally, 1 on
            /// a genuinely mono device. The rest of the engine — the Opus
            /// codec, the queues, BufferSize — is stereo throughout, so this
            /// is read in exactly two places: the input callback, which
            /// duplicates a mono capture onto both channels, and the output
            /// callback, which mixes the stereo pair down for a mono device.
            /// </summary>
            public int Channels = Devices.StreamChannels;
            /// <summary>
            /// Unmanaged PaWasapiStreamInfo carrying paWinWasapiAutoConvert,
            /// or zero (the normal case). Set by negotiation as a LAST
            /// resort when a WASAPI endpoint's shared-mode format offers no
            /// Opus-legal rate (#12); deviceParams feeds it to both
            /// Pa_IsFormatSupported and Pa_OpenStream; freed at close.
            /// </summary>
            public IntPtr WasapiAutoConvertInfo = IntPtr.Zero;
            // Set by the AudioServer thread when workItems.negotiate has
            // written the answer into SampleRate. Audio.Open waits on this
            // before it sizes a buffer or builds a codec, because everything
            // downstream of the rate has to agree with the rate the device
            // actually accepted — see the negotiate case in serverProc.
            public bool RateSettled = false;
            public PortAudio.PaStreamCallbackDelegate CB;
            public int CBUser;
            public WavCallback WavInputHandler;
            public OpusCallback OpusInputHandler
            {
                get { return TxPipeline.Handler; }
                set { TxPipeline.Handler = value; }
            }
            public AudioSentCallback AudioSent;
            public bool SilentPeriod = false;
            // Audio Track C: optional TX injection source. When engaged, its
            // samples REPLACE the mic capture in inputCallback ahead of the
            // Opus encode (the mic is discarded, never mixed).
            // Sprint 33 Track I: this was typed TxToneGenerator. The slot was
            // never really about tones — it is the one place anything can
            // stand in for the microphone — so it is now ITxInputSource,
            // shared by the test tone and the reference-file player.
            //
            // 2026-08-24: these three, and the encoder and handler above, now
            // live in TxPipeline rather than here. They are still reachable by
            // their old names because two dozen call sites use them and the
            // rename would be churn, but there is only ONE copy of each, and
            // the self-clocked source sees the same one the capture callback
            // does. That is the point: the promise that an injected tone rides
            // the identical path as a voice is now structural.
            public ITxInputSource ToneSource
            {
                get { return TxPipeline.Source; }
                set { TxPipeline.Source = value; }
            }
            // Track I: optional TX conditioning processor (NR + gate), run
            // AFTER the tone injection point and BEFORE the meter, so the
            // meter still measures what genuinely goes to the encoder.
            public TxAudioProcessorCallback InputProcessor
            {
                get { return TxPipeline.Conditioner; }
                set { TxPipeline.Conditioner = value; }
            }
            // Engine Track: optional LUFS meter, fed in inputCallback AFTER
            // the tone injection point so it measures whatever is actually
            // being transmitted, tone or mic.
            public LufsMeter InputMeter
            {
                get { return TxPipeline.Meter; }
                set { TxPipeline.Meter = value; }
            }
            // Engine Track: end-of-stream callback diagnostics. These were
            // static across ALL streams (meaningless with an input and an
            // output stream open at once); now per-stream.
            public int diagBufCount;
            public long diagByteCount;
            // Threads Track (2026-08-12): PortAudio status-flag glitch
            // instrumentation. statusFlags is PortAudio's own per-buffer
            // glitch report — paInputOverflow / paOutputUnderflow are set
            // exactly when audio was dropped — and until now both callbacks
            // received it and threw it away. Logging is per FLAG TRANSITION
            // and per stream close, never per buffer: a per-buffer line
            // floods exactly like the old startOpusInputChannel bug did
            // (tens of lines per millisecond, a 268 MB trace).
            public PortAudio.PaStreamCallbackFlags SeenStatusFlags;
            public long StatusCallbackCount;    // callbacks observed on this stream
            public long FlaggedCallbackCount;   // callbacks carrying any status flag
            public readonly long[] StatusFlagCounts = new long[5]; // per flag, bit order
            // Track B, 2026-08-18 (#29): output-queue silence instrumentation.
            // statusFlags only reports glitches PORTAUDIO caused. When OUR
            // queue runs dry the output callback fills the device buffer with
            // zeros itself — PortAudio was fed on time, no flag is raised, and
            // the operator still hears a gap with a click at each edge. These
            // count that blind spot. Priming silence (before the first queued
            // buffer ever arrives) is expected and counted separately from
            // mid-stream starvation, which is the audible defect.
            public long SilenceFills;           // total silent device buffers while Active
            public long StarvationFills;        // silent buffers AFTER data had been flowing
            public bool OutputDataSeen;         // a queued buffer has been consumed
            public bool StarvationLogged;       // first-occurrence line emitted
            // #196, 2026-08-23: WHEN the starvations happen, not just how many.
            // The 2026-08-22 capture reported "20 mid-stream starvation" at
            // stream close and nothing else — enough to name the galloping
            // monitor tone as queue starvation, and not enough to say whether
            // the twenty were spread evenly or clustered inside the seconds
            // the operator was transmitting. Those point at different causes:
            // clustered means something about transmitting starves the
            // playback path, evenly spread means the jitter buffer is simply
            // too shallow.
            //
            // Rate-limited to at most one line per second, and only in a
            // second that actually had one. That is the same discipline the
            // coalesced meter stream uses, and it keeps the audio callback out
            // of the flooding failure the status-flag comment above describes.
            // The trace's own leading timestamp shares a time base with the
            // output transcript, so these lines can be read directly against
            // TxStart and TxStop without correlating anything by hand.
            public long StarvationWindowTick;   // Environment.TickCount64 of the open window
            public long StarvationInWindow;     // starvations counted in it
            // Track J, 2026-09-01 (#462): the device half of the latency
            // budget, measured rather than assumed.
            //
            // PortAudio hands every callback a PaStreamCallbackTimeInfo, and
            // both callbacks discarded it. It carries the only figure this
            // side of the link cannot compute: how far ahead of the DAC we are
            // writing (output), or how long ago the ADC captured what we are
            // reading (input). Our own buffer arithmetic is exact and knowable
            // from the source; the driver's is not.
            //
            // Min and max only, accumulated with two comparisons per callback
            // and reported in the close summary. A per-callback line here is
            // the trace flood that has cost this project two sessions.
            public double DeviceLatencyMin = double.MaxValue;
            public double DeviceLatencyMax;
            public long DeviceLatencySamples;
            // What PortAudio reported for the stream at open, in seconds. This
            // is the driver's own claim about its buffering, separate from what
            // the callback timing measures — the two disagreeing is itself
            // worth seeing.
            public double ReportedDeviceLatency;
        }
        internal class staticQueues
        {
            // Engine Track (2026-08-11): this table is written by UI /
            // remote-audio threads (Add), the AudioServer thread (Remove), and
            // read from the PortAudio callback threads (getQ). It was a bare
            // Dictionary — a concurrent Add/Remove against a callback's
            // TryGetValue is undefined behaviour. ConcurrentDictionary makes
            // every path safe without adding a lock to the audio callback.
            private readonly ConcurrentDictionary<int, StreamCB> Qs =
                new ConcurrentDictionary<int, StreamCB>();
            private readonly Random rand = new Random();
            public int Add()
            {
                int key;
                do
                {
                    key = rand.Next();
                } while (!Qs.TryAdd(key, new StreamCB()));
                return key;
            }
            public void Remove(int key)
            {
                Qs.TryRemove(key, out _);
            }
            public StreamCB getQ(int key)
            {
                StreamCB rv = null;
                Qs.TryGetValue(key, out rv);
                return rv;
            }
        }
        internal static staticQueues queues = new staticQueues();
        private int qKey;
        /// <summary>
        /// stream control block
        /// </summary>
        private StreamCB CBData { get { return queues.getQ(qKey); } }
        /// <summary>
        /// true if stream is active
        /// </summary>
        internal bool IsActive { get { return CBData.IsAlive; } }
        /// <summary>
        /// buffer size in use
        /// </summary>
        public uint BufferSize
        {
            get { return CBData.BufferSize; }
            internal set { CBData.BufferSize = value; }
        }
        /// <summary>
        /// The rate the stream is actually running at, which is not necessarily
        /// the rate that was asked for — see workItems.negotiate. Read this,
        /// never the requested rate, when reporting to an operator.
        /// </summary>
        public uint SampleRate { get { return CBData?.SampleRate ?? 0; } }
        /// <summary>
        /// Channels the stream actually opened with — 1 on a mono device, 2
        /// otherwise. Everything downstream is stereo either way; this is the
        /// fact about the hardware, for reporting and tracing.
        /// </summary>
        public int Channels { get { return CBData?.Channels ?? Devices.StreamChannels; } }
        internal OpusDecoder Decoder { get { return CBData.Decoder; } }
        internal Queue TheQ { get { return CBData.Q; } }
        internal WavCallback WavInputHandler
        {
            get { return CBData.WavInputHandler; }
            set { CBData.WavInputHandler = value; }
        }
        internal OpusCallback OpusInputHandler
        {
            get { return CBData.OpusInputHandler; }
            set { CBData.OpusInputHandler = value; }
        }
        internal AudioSentCallback AudioSent
        {
            get { return CBData.AudioSent; }
            set { CBData.AudioSent = value; }
        }
        internal ITxInputSource ToneSource
        {
            get { return CBData?.ToneSource; }
            set { var cb = CBData; if (cb != null) cb.ToneSource = value; }
        }
        internal LufsMeter InputMeter
        {
            get { return CBData?.InputMeter; }
            set { var cb = CBData; if (cb != null) cb.InputMeter = value; }
        }
        internal TxAudioProcessorCallback InputProcessor
        {
            get { return CBData?.InputProcessor; }
            set { var cb = CBData; if (cb != null) cb.InputProcessor = value; }
        }

        internal Audio()
        {
            qKey = queues.Add();
            Tracing.TraceLine("audio:qkey:" + qKey, TraceLevel.Info);
        }

        /// <summary>
        /// Initialize
        /// </summary>
        /// <param name="inDev">the input Devices.Device to use</param>
        /// <param name="outDev">the output Devices.Device to use</param>
        public static void Initialize(Devices.Device inDev, Devices.Device outDev)
        {
            AudioAnchor.Init(inDev, outDev);
        }

        public static void Terminate()
        {
            AudioAnchor.Term();
        }

        /// <summary>
        /// Setup specified device for i/o.
        /// </summary>
        /// <param name="inOut">input/output</param>
        /// <param name="rate">sample rate</param>
        /// <param name="useOpus">(optional) true if for opus input</param>
        /// <param name="outputCallback">(optional) output callback</param>
        /// <param name="cbPerSec">(optional) callbacks per sec, default 10</param>
        /// <param name="profile">
        /// (optional) the Opus ENCODER settings for this stream. Null means
        /// <see cref="OpusEncodeProfile.Shipped"/>, which reproduces exactly
        /// what this method built before the profile existed.
        /// </param>
        /// <returns>new Device, null on failure</returns>
        internal bool Open(Devices.DeviceTypes inOut, uint rate, bool useOpus=false,
            PortAudio.PaStreamCallbackDelegate outputCallback = null,
            int cbPerSec = AudioBuffering.DefaultCallbacksPerSecond,
            OpusEncodeProfile profile = null)
        {
            CBData.Device = (inOut == Devices.DeviceTypes.input) ?
                inDevice : outDevice;
            CBData.Channels = AudioAnchor.channelsFor(CBData.Device);
            // The host API belongs on this line. It was absent, and the device
            // NAME is identical under all four APIs, so nothing in the trace
            // could tell an MME open from a WASAPI one — on a stream where the
            // API is what decides whether the rate below is genuine or silently
            // resampled. The name here comes from the saved device record; the
            // server logs what PortAudio actually reports for the index it
            // opens, which is the authoritative one if the two ever disagree.
            Tracing.TraceLine("Audio.Open:" + CBData.Device.Name
                + " [" + (string.IsNullOrEmpty(CBData.Device.hostApiName)
                            ? "host API not recorded in audioDevices.xml"
                            : CBData.Device.hostApiName) + "]"
                + " requested " + rate + " Hz, " + CBData.Channels + " channel(s)"
                + (CBData.Channels == 1 ? " (mono device, duplicated to stereo)" : "")
                + ", opus=" + useOpus.ToString(), TraceLevel.Info);
            CBData.SampleRate = (rate == 0) ? (uint)CBData.Device.defaultSampleRate : rate;
            // UseOpus must be set before negotiating: it decides whether 44.1 kHz
            // is a candidate at all.
            CBData.UseOpus = useOpus;

            // Settle the rate with the device before building anything from it.
            // Every size and every codec below reads openRate, never the `rate`
            // parameter — the whole point of this round-trip is that the two can
            // differ, and every consumer must follow the device rather than the
            // request. See workItems.negotiate.
            CBData.RateSettled = false;
            AudioAnchor.work.Add(new AudioAnchor.workItem(AudioAnchor.workItems.negotiate, CBData));
            if (!Tracing.await(() => { var cb = CBData; return cb != null && cb.RateSettled; }, 5000))
            {
                Tracing.TraceLine("Audio.Open:sample rate negotiation did not complete within 5s",
                    TraceLevel.Error);
                return false;
            }
            uint openRate = CBData.SampleRate;
            if (openRate != rate)
            {
                Tracing.TraceLine("Audio.Open:opening at " + openRate
                    + " Hz rather than the requested " + rate + " Hz", TraceLevel.Info);
            }

            if (outputCallback != null) CBData.CB = outputCallback;
            else
            {
                // Use the default callback defined here.
                CBData.CB = (CBData.Device.Type == Devices.DeviceTypes.input) ? inCallback : outCallback;
            }
            CBData.CBUser = qKey;
            uint bufSZ;
            if (useOpus)
            {
                POpusCodec.Enums.SamplingRate oRate;
                switch (openRate)
                {
                    // 8000 was written `800` here, a rate no device reports and
                    // nothing could ever match.
                    case 8000: oRate = POpusCodec.Enums.SamplingRate.Sampling08000; break;
                    case 12000: oRate = POpusCodec.Enums.SamplingRate.Sampling12000; break;
                    case 16000: oRate = POpusCodec.Enums.SamplingRate.Sampling16000; break;
                    case 24000: oRate = POpusCodec.Enums.SamplingRate.Sampling24000; break;
                    case 48000: oRate = POpusCodec.Enums.SamplingRate.Sampling48000; break;
                    default:
                        // Was a silent fall-through to 48 kHz. Negotiation only
                        // ever hands back an Opus-legal rate, so arriving here
                        // means the device accepted nothing at all — and
                        // encoding at a rate the stream is not running is the
                        // defect, not the fallback.
                        Tracing.TraceLine("Audio.Open:" + openRate
                            + " Hz is not a rate Opus can encode; refusing to open",
                            TraceLevel.Error);
                        return false;
                }
                // Track J, 2026-09-01 (#460): every encoder decision now comes
                // from one profile instead of three literals written here.
                // Passing null gives OpusEncodeProfile.Shipped, which builds
                // the identical encoder these three lines did — stereo,
                // SuperWideband, 10 ms frames, application Audio, no bitrate
                // set. The old form constructed at 20 ms and then reassigned
                // EncoderDelay; the state it left behind is the same state the
                // four-argument constructor reaches directly.
                var opusProfile = profile ?? OpusEncodeProfile.Shipped;
                // always create the encoder to get values for bufSZ.
                CBData.Encoder = opusProfile.CreateEncoder(oRate);
                // The PIPELINE's frame, not the codec's: the transmit chain is
                // interleaved stereo end to end whatever the encoder's channel
                // count is, and a mono encoder is fed by folding at the encode
                // step (see OpusEncodeProfile.BuildEncodeStep). Writing
                // Devices.StreamChannels rather than a bare 2 says which of the
                // two channel counts this one is — the value is unchanged.
                CBData.OpusFrameSZ = (uint)CBData.Encoder.FrameSizePerChannel
                    * (uint)Devices.StreamChannels;
                // The buffer that holds cbPerSec callbacks' worth of that.
                // Extracted to AudioBuffering, float arithmetic and all, so the
                // dominant latency term is nameable and testable (#462).
                bufSZ = AudioBuffering.OpusBufferFloats(
                    openRate, CBData.Encoder.FrameSizePerChannel, cbPerSec);
                if (bufSZ < CBData.OpusFrameSZ)
                {
                    // Below one whole Opus frame per callback the arithmetic
                    // above truncates toward zero, and a stream opened with a
                    // buffer of nothing is an absent audio path rather than a
                    // degraded one. Refuse, and say which number caused it.
                    Tracing.TraceLine("Audio.Open:" + cbPerSec + " callbacks/second leaves "
                        + bufSZ + " floats per buffer, less than the " + CBData.OpusFrameSZ
                        + " one Opus frame needs at " + openRate + " Hz; refusing to open",
                        TraceLevel.Error);
                    CBData.Encoder.Dispose();
                    CBData.Encoder = null;
                    return false;
                }
                // What the encoder ACTUALLY settled on, read back from libopus
                // rather than assumed (Track J, #460 / #462). Two figures that
                // have only ever been quoted from documentation:
                //   - the bitrate nobody sets, which is where the ~70 kbps in
                //     the register comes from and which nothing in this tree
                //     has ever confirmed on a real machine;
                //   - the lookahead, the codec's delay on top of the frame
                //     duration, quotable as 2.5 to 6.5 ms from the specification
                //     and knowable exactly from the encoder itself.
                // Read HERE, above the branch, because the output path disposes
                // this encoder — it exists only to size the buffer — and the
                // figures are the same either way.
                string codecFacts;
                try
                {
                    var enc = CBData.Encoder;
                    codecFacts = "libopus settled on " + enc.Bitrate + " bps, lookahead "
                        + enc.Lookahead + " samples ("
                        + (enc.Lookahead * 1000.0 / openRate).ToString("F1") + " ms)";
                }
                catch (Exception ex)
                {
                    codecFacts = "libopus would not report its bitrate or lookahead: " + ex.Message;
                }
                // We'll use bufSZ for input and output.
                if (inOut == Devices.DeviceTypes.input)
                {
                    CBData.opusPool = new bufPool(CBData.OpusFrameSZ, 200);
                }
                else
                {
                    CBData.Encoder.Dispose();
                    CBData.Encoder = null;
                    // The decoder stays STEREO regardless of the profile, and
                    // that is not an oversight (#460). Channel count is a
                    // property of an encode: an Opus packet is self-describing
                    // and a stereo decoder upmixes a mono packet transparently,
                    // so this number describes the shape of OUR playback path —
                    // the queue and the output callback are interleaved stereo —
                    // and not anything about the wire. Making it mono would
                    // halve every decoded buffer and desynchronise the queue.
                    CBData.Decoder = new OpusDecoder(oRate, POpusCodec.Enums.Channels.Stereo);
                }
                Tracing.TraceLine("Audio.Open:opus "
                    + ((inOut == Devices.DeviceTypes.input) ? "encode" : "decode")
                    + " at " + openRate + " Hz, " + opusProfile.Describe()
                    + "; " + codecFacts
                    + "; " + cbPerSec + " callback(s)/second = "
                    + AudioBuffering.BufferMilliseconds(bufSZ, openRate).ToString("F1")
                    + " ms of buffer, "
                    + AudioBuffering.PacketsPerSecond(opusProfile.FrameDuration).ToString("F0")
                    + " packets/second carrying "
                    + (AudioBuffering.HeaderBitsPerSecond(opusProfile.FrameDuration) / 1000).ToString("F1")
                    + " kbps of header before any audio", TraceLevel.Info);
            }
            else
            {
                // not opus, set bufSZ to call callback every .1 seconds.
                // openRate, not rate: with rate 0 (meaning "device default")
                // this computed a buffer size of zero.
                bufSZ = (openRate * 2) / (uint)cbPerSec;
                if (inOut == Devices.DeviceTypes.input)
                {
                    // input buffer cache
                    CBData.opusPool = new bufPool(bufSZ, 10);
                }
            }
            Tracing.TraceLine("Audio.Open buffer size set to:" + bufSZ, TraceLevel.Info);
            CBData.BufferSize = bufSZ;

            AudioAnchor.work.Add(new AudioAnchor.workItem(AudioAnchor.workItems.open, CBData));

            // Await the open — BOUNDED (Engine Track, 2026-08-11). The old
            // code retried a 500 ms await in a while loop forever, so a failed
            // Pa_OpenStream (device unplugged mid-connect, exclusive-mode
            // grab) hung the calling thread — the remote-audio thread —
            // permanently. 5 s covers a slow driver; after that we report the
            // failure the return value always promised.
            if (!Tracing.await(() => { var cb = CBData; return cb != null && cb.Open; }, 5000))
            {
                Tracing.TraceLine("Audio.Open:open did not complete within 5s", TraceLevel.Error);
            }

            return (CBData?.Open == true) ? true : false;
        }

        /// <summary>
        /// Start a device.
        /// </summary>
        /// <returns>true on success</returns>
        internal bool Start()
        {
            Tracing.TraceLine("Audio.Start:qkey:" + qKey, TraceLevel.Info);
            bool rv = false;
            AudioAnchor.workItem item = new AudioAnchor.workItem(AudioAnchor.workItems.start, CBData);
            AudioAnchor.work.Add(item);
            rv = Tracing.await(() => { return CBData.Started; }, 1000);
            return rv;
        }

        internal void Stop()
        {
            Tracing.TraceLine("Audio.Stop:qkey:" + qKey, TraceLevel.Info);
            AudioAnchor.workItem item = new AudioAnchor.workItem(AudioAnchor.workItems.stop, CBData);
            AudioAnchor.work.Add(item);
            Tracing.await(() => { return !CBData.Started; }, 5000);
        }

        #region Self-clocked transmit (#208)
        // The tone's own clock. Built on first use and kept, so the encoder,
        // the source and the meter are the SAME objects the capture path uses
        // — the pipeline is shared, only the thing supplying frames differs.
        private TxSelfClockedSource _selfClockedTx;

        /// <summary>True while transmit frames are coming from the self-clock.</summary>
        internal bool SelfClockedTxRunning => _selfClockedTx != null && _selfClockedTx.Running;

        /// <summary>
        /// Start producing transmit frames from elapsed time rather than from
        /// the capture device.
        /// </summary>
        /// <remarks>
        /// The caller must have stopped the PortAudio capture stream first.
        /// Two producers sharing one Opus encoder would corrupt the bitstream
        /// into something the radio renders as noise rather than as an error —
        /// see the thread model on TxFramePipeline.
        /// </remarks>
        internal bool StartSelfClockedTx()
        {
            StreamCB cb = CBData;
            if (cb == null)
            {
                Tracing.TraceLine("Audio.StartSelfClockedTx: no stream", TraceLevel.Error);
                return false;
            }
            if (!cb.UseOpus || cb.Encoder == null || cb.OpusFrameSZ == 0)
            {
                Tracing.TraceLine("Audio.StartSelfClockedTx: this stream has no Opus encoder,"
                    + " so there is nothing to pace", TraceLevel.Error);
                return false;
            }

            // Interleaved stereo: OpusFrameSZ counts floats across both
            // channels, the clock counts samples per channel.
            //
            // Read from the encoder rather than divided out of OpusFrameSZ
            // (Track J, 2026-09-01). Identical today and it stays identical
            // under a mono profile, because the fold to mono happens at the
            // encode step and the pipeline this clock feeds is stereo either
            // way — whereas dividing by a literal 2 would have quietly become
            // a claim about the CODEC's channel count rather than the
            // pipeline's.
            int samplesPerFrame = cb.Encoder.FrameSizePerChannel;

            // Noel, 2026-08-24: "make sure that the sample rate doesn't
            // change." A kept clock that no longer matches the stream would
            // keep confidently producing a hundred frames a second of the
            // wrong thing — healthy-looking and wrong, the worst shape a fault
            // can take here. Rebuild rather than adapt; the clock has no rate
            // setter precisely so this is the only available answer.
            if (_selfClockedTx != null && !_selfClockedTx.Matches(cb.SampleRate, samplesPerFrame))
            {
                Tracing.TraceLine("Audio.StartSelfClockedTx: "
                    + _selfClockedTx.Pump.Clock.DescribeMismatch((int)cb.SampleRate, samplesPerFrame),
                    TraceLevel.Warning);
                _selfClockedTx.Stop();
                _selfClockedTx = null;
            }

            _selfClockedTx ??= new TxSelfClockedSource(cb.TxPipeline, cb.SampleRate, samplesPerFrame);
            return _selfClockedTx.Start();
        }

        /// <summary>Stop the self-clock. Hard and immediate; see TxSelfClockedSource.Stop.</summary>
        internal void StopSelfClockedTx()
        {
            _selfClockedTx?.Stop();
        }
        #endregion

        internal void Finished()
        {
            // Nothing may still be feeding the encoder when the stream closes
            // and the encoder is disposed underneath it.
            StopSelfClockedTx();
            Tracing.TraceLine("Audio.Finished", TraceLevel.Info);
            AudioAnchor.workItem item = new AudioAnchor.workItem(AudioAnchor.workItems.close, CBData);
            AudioAnchor.work.Add(item);
            Tracing.TraceLine("Audio.Finished:waiting for close", TraceLevel.Info);
            // Engine Track (2026-08-11): this "timeout loop" could never time
            // out. It read:
            //     int smallWait = 200;
            //     int longWait = smallWait * 25;      // 5000 ITERATIONS, not 5 s
            //     while (longWait-- != 0)
            //         while (CBData != null) Thread.Sleep(smallWait);  // UNBOUNDED
            // The inner while blocked forever whenever the AudioServer never
            // processed the close (wedged driver, stuck earlier work item), so
            // the outer countdown was unreachable — and the caller is the
            // remote-audio thread, whose 6 s Join in FlexBase then failed,
            // abandoning a foreground thread that pinned the process: the
            // field-confirmed orphan jjflexible.exe shutdown hang. Now: one
            // flat, bounded wait. CBData goes null when the server removes
            // this stream from the queue table at the end of close.
            const int pollMs = 200;
            int remainingMs = 5000;
            while (CBData != null && remainingMs > 0)
            {
                Thread.Sleep(pollMs);
                remainingMs -= pollMs;
            }
            if (CBData != null)
            {
                Tracing.TraceLine("audio.Finished:didn't stop within 5s, abandoning wait", TraceLevel.Error);
            }
        }

        internal class bufPool
        {
            public Queue Q = Queue.Synchronized(new Queue());
            private uint bufferSZ;
            private int initialCount;
            private bool needMore
            {
                get { return (Q.Count < (initialCount / 2)); }
            }
            private Thread allocater;
            public bufPool(uint bufSZ, int startCt)
            {
                bufferSZ = bufSZ;
                initialCount = startCt;
                for(int i = 0; i < startCt; i++)
                {
                    float[] buf = new float[bufSZ];
                    Q.Enqueue(buf);
                }

                allocater = new Thread(allocProc);
                allocater.Name = "bufferAllocater";
                // Engine Track: background. This thread lives until Done(),
                // which only runs on the orderly close path — if close never
                // happens, a foreground allocater pins the process (part of
                // the orphan-shutdown chain).
                allocater.IsBackground = true;
                allocater.Start();
            }

            public float[] getBuf()
            {
                return (Q.Count == 0) ? null : (float[])Q.Dequeue();
            }

            private void allocProc()
            {
                try
                {
                    while (true)
                    {
                        if (needMore)
                        {
                            int ct = initialCount - Q.Count;
                            for (int i = 0; i < ct; i++)
                            {
                                float[] buf = new float[bufferSZ];
                                Q.Enqueue(buf);
                            }
                        }
                        else Thread.Sleep(100);
                    }
                }
                catch(ThreadInterruptedException) { }
            }

            public void Done()
            {
                allocater.Interrupt();
                // Engine Track: bounded. The old spin-until-dead loop ran on
                // the AudioServer thread and would wedge it forever if the
                // allocater failed to exit; a wedged server is the head of the
                // orphan-shutdown chain. One second is generous — the thread
                // only sleeps 100 ms at a time.
                if (!allocater.Join(1000))
                {
                    Tracing.TraceLine("bufPool.Done:allocater didn't stop within 1s", TraceLevel.Error);
                }
            }
        }

        // ── Threads Track (2026-08-12): status-flag instrumentation ──
        // The five PaStreamCallbackFlags bits, in bit order, with the
        // plain-language meaning of the two that report dropped audio.
        // paInputOverflow: the device produced audio faster than we consumed
        // it — input samples were DROPPED (heard as a click or gap in what
        // we encode and send). paOutputUnderflow: we failed to supply output
        // samples in time — the device played a gap (heard as a click).
        private static readonly PortAudio.PaStreamCallbackFlags[] statusFlagBits =
        {
            PortAudio.PaStreamCallbackFlags.paInputUnderflow,
            PortAudio.PaStreamCallbackFlags.paInputOverflow,
            PortAudio.PaStreamCallbackFlags.paOutputUnderflow,
            PortAudio.PaStreamCallbackFlags.paOutputOverflow,
            PortAudio.PaStreamCallbackFlags.paPrimingOutput,
        };

        private static string describeStatusFlag(PortAudio.PaStreamCallbackFlags flag)
        {
            switch (flag)
            {
                case PortAudio.PaStreamCallbackFlags.paInputOverflow:
                    return "input samples were dropped before we could read them";
                case PortAudio.PaStreamCallbackFlags.paOutputUnderflow:
                    return "output samples were not supplied in time, the device played a gap";
                case PortAudio.PaStreamCallbackFlags.paInputUnderflow:
                    return "the input stream ran dry";
                case PortAudio.PaStreamCallbackFlags.paOutputOverflow:
                    return "output data was discarded";
                default:
                    return "the output stream is priming";
            }
        }

        /// <summary>
        /// Record this buffer's PortAudio status flags. Logs a flag the FIRST
        /// time it appears on this stream (with what it means), counts every
        /// occurrence silently after that. Runs on the PortAudio callback
        /// thread, so the non-first-appearance path is counters only.
        /// </summary>
        private static void noteStatusFlags(StreamCB data,
            PortAudio.PaStreamCallbackFlags statusFlags, string streamName)
        {
            data.StatusCallbackCount++;
            if (statusFlags == 0) return;
            data.FlaggedCallbackCount++;
            for (int i = 0; i < statusFlagBits.Length; i++)
            {
                var bit = statusFlagBits[i];
                if ((statusFlags & bit) == 0) continue;
                data.StatusFlagCounts[i]++;
                if ((data.SeenStatusFlags & bit) == 0)
                {
                    data.SeenStatusFlags |= bit;
                    Tracing.TraceLine("audio " + streamName + " stream: PortAudio reports "
                        + bit + " at callback " + data.StatusCallbackCount + " — "
                        + describeStatusFlag(bit)
                        + ". Further occurrences are counted silently; totals logged when the stream closes.",
                        TraceLevel.Error);
                }
            }
        }

        /// <summary>
        /// One-line status-flag summary, logged when a stream's callback
        /// completes. Zero-flag streams say so — a clean run is evidence too.
        /// </summary>
        private static void traceStatusFlagSummary(StreamCB data, string streamName)
        {
            if (data.FlaggedCallbackCount == 0)
            {
                Tracing.TraceLine("audio " + streamName + " stream summary: "
                    + data.StatusCallbackCount + " callbacks, no PortAudio status flags (no glitches reported)",
                    TraceLevel.Info);
                return;
            }
            var sb = new StringBuilder();
            sb.Append("audio ").Append(streamName).Append(" stream summary: ")
              .Append(data.StatusCallbackCount).Append(" callbacks, ")
              .Append(data.FlaggedCallbackCount).Append(" carried PortAudio status flags:");
            for (int i = 0; i < statusFlagBits.Length; i++)
            {
                if (data.StatusFlagCounts[i] == 0) continue;
                sb.Append(' ').Append(statusFlagBits[i]).Append('=').Append(data.StatusFlagCounts[i]);
            }
            Tracing.TraceLine(sb.ToString(), TraceLevel.Error);
        }

        /// <summary>
        /// Record the device-path latency PortAudio reports for THIS callback
        /// (#462). Two comparisons and a counter; nothing is logged here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On output, <c>outputBufferDacTime - currentTime</c> is how far ahead
        /// of the converter we are writing. On input,
        /// <c>currentTime - inputBufferAdcTime</c> is how long ago the oldest
        /// sample in this buffer was captured. Together with our own buffer
        /// size — which is arithmetic, and exact — they are the whole of the
        /// PC-side latency budget.
        /// </para>
        /// <para>
        /// <b>Not every host API fills this in.</b> A zero, a negative or a
        /// non-finite value means "not supplied", and is skipped rather than
        /// averaged in: an instrument that quietly reports zero latency because
        /// the driver declined to answer is worse than no instrument. The
        /// summary says how many callbacks actually carried a figure.
        /// </para>
        /// </remarks>
        private static void noteDeviceLatency(StreamCB data,
            ref PortAudio.PaStreamCallbackTimeInfo timeInfo, bool isInput)
        {
            double seconds = isInput
                ? timeInfo.currentTime - timeInfo.inputBufferAdcTime
                : timeInfo.outputBufferDacTime - timeInfo.currentTime;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return;
            if (seconds < data.DeviceLatencyMin) data.DeviceLatencyMin = seconds;
            if (seconds > data.DeviceLatencyMax) data.DeviceLatencyMax = seconds;
            data.DeviceLatencySamples++;
        }

        /// <summary>
        /// The latency companion to <see cref="traceStatusFlagSummary"/>,
        /// logged when a stream's callback completes.
        /// </summary>
        /// <remarks>
        /// Reports the measured device path, our own buffer, and their sum —
        /// which is this computer's entire contribution to the delay an
        /// operator hears. What it deliberately does NOT claim is an end-to-end
        /// figure: the network and the radio are the other half, and neither is
        /// visible from here. See the track report for the bench measurement
        /// that closes the loop.
        /// </remarks>
        private static void traceLatencySummary(StreamCB data, string streamName)
        {
            double ourBufferMs = AudioBuffering.BufferMilliseconds(data.BufferSize, data.SampleRate);
            if (data.DeviceLatencySamples == 0)
            {
                Tracing.TraceLine("audio " + streamName + " latency summary: this host API"
                    + " supplied no callback timing, so only our own buffer is known — "
                    + ourBufferMs.ToString("F1") + " ms, plus a device path PortAudio claimed"
                    + " would be " + (data.ReportedDeviceLatency * 1000).ToString("F1") + " ms",
                    TraceLevel.Info);
                return;
            }
            double minMs = data.DeviceLatencyMin * 1000;
            double maxMs = data.DeviceLatencyMax * 1000;
            Tracing.TraceLine("audio " + streamName + " latency summary: device path measured "
                + minMs.ToString("F1") + " to " + maxMs.ToString("F1") + " ms over "
                + data.DeviceLatencySamples + " callbacks (PortAudio claimed "
                + (data.ReportedDeviceLatency * 1000).ToString("F1") + " ms at open); our buffer "
                + ourBufferMs.ToString("F1") + " ms; this computer contributes "
                + (minMs + ourBufferMs).ToString("F1") + " to "
                + (maxMs + ourBufferMs).ToString("F1")
                + " ms, network and radio excluded", TraceLevel.Info);
        }

        private static PortAudio.PaStreamCallbackResult inputCallback(IntPtr inbuf,
                IntPtr outbuf,
                uint frameCount,
                ref PortAudio.PaStreamCallbackTimeInfo timeInfo,
                PortAudio.PaStreamCallbackFlags statusFlags,
                IntPtr userData)
        {
            StreamCB data = queues.getQ((int)userData);
            // Stream already removed from the table (close raced a late
            // callback): tell PortAudio to stop calling us.
            if (data == null) return PortAudio.PaStreamCallbackResult.paAbort;

            // Threads Track: read the glitch report before anything else —
            // a final callback can carry flags too.
            noteStatusFlags(data, statusFlags, "input");
            noteDeviceLatency(data, ref timeInfo, true);

            PortAudio.PaStreamCallbackResult rv = PortAudio.PaStreamCallbackResult.paContinue;
            if (!data.Active)
            {
                Tracing.TraceLine("audio.inputCallback done", TraceLevel.Info);
                rv = PortAudio.PaStreamCallbackResult.paComplete;
                goto inCallbackDone;
            }

            // A mono device delivers half the samples for the same number of
            // frames, so this walks half the buffer and duplicates each sample
            // onto both channels below. Same expansion MicProbe has always done
            // to feed its loudness meter — copied, not reinvented, so a mono
            // microphone measures and transmits through the identical shape.
            bool mono = (data.Channels == 1);
            float* inPtr = (float*)inbuf;
            float* endPtr = inPtr + (mono ? data.BufferSize / 2 : data.BufferSize);
            if (data.UseOpus)
            {
                try
                {
                    do
                    {
                        float[] buf = data.opusPool.getBuf();
                        if (buf == null)
                        {
                            Tracing.TraceLine("InputCallback:no buffer", TraceLevel.Error);
                            goto inCallbackDone;
                        }
                        if (mono)
                        {
                            // OpusFrameSZ is FrameSizePerChannel * 2, always
                            // even, so this fills the frame exactly.
                            for (int i = 0; i < data.OpusFrameSZ; i += 2)
                            {
                                float s = *(inPtr++);
                                buf[i] = s;
                                buf[i + 1] = s;
                            }
                        }
                        else
                        {
                            for (int i = 0; i < data.OpusFrameSZ; i++)
                            {
                                buf[i] = *(inPtr++);
                            }
                        }
                        // Inject, condition, meter, encode, send — all five in
                        // TxFramePipeline, which is also what the self-clocked
                        // transmit source calls.
                        //
                        // This used to be written out here, and it was the only
                        // definition of a transmit frame's journey. Once a
                        // second producer existed (2026-08-24, the tone that
                        // paces itself instead of borrowing the microphone's
                        // clock), one definition in one place stopped being a
                        // tidiness question: two copies would drift, and they
                        // would drift SILENTLY, because a tone that skipped the
                        // meter or the conditioner still sounds like a tone.
                        //
                        // Emit returns false when the frame was abandoned at
                        // teardown or the encode failed. Both mean stop, which
                        // is exactly what the old `if (!data.Active) break;`
                        // between encode and send did — it moved into the
                        // pipeline as StillRunning so both producers honour it.
                        if (!data.TxPipeline.Emit(buf, (int)data.OpusFrameSZ, data.SampleRate, data.IsActive))
                        {
                            // False has two causes and they need different
                            // answers. Teardown: Active is already false and
                            // the loop condition below turns it into
                            // paComplete, the ordinary path. Encode failure
                            // while still active: the stream cannot recover —
                            // this used to throw out of Encode into the catch
                            // below and abort, and it must still abort, or a
                            // broken encoder becomes silence with no complaint.
                            if (data.Active) rv = PortAudio.PaStreamCallbackResult.paAbort;
                            break;
                        }
                        data.diagBufCount++;
                        data.diagByteCount += buf.Length;
                    } while (data.Active && (inPtr != endPtr));
                    if (!data.Active) rv = PortAudio.PaStreamCallbackResult.paComplete;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("inCallback exception:" + ex.Message, TraceLevel.Error);
                    rv = PortAudio.PaStreamCallbackResult.paAbort;
                }
            }
            else
            {
                data.diagBufCount++;
                try
                {
                    float[] buf = data.opusPool.getBuf();
                    if (buf == null)
                    {
                        Tracing.TraceLine("InputCallback:no buffer", TraceLevel.Error);
                        goto inCallbackDone;
                    }
                    int offset = 0;
                    if (mono)
                    {
                        // Same duplication as the Opus path above: what the
                        // handler receives is always stereo, whatever the
                        // device supplied.
                        while (inPtr != endPtr)
                        {
                            float s = *(inPtr++);
                            buf[offset++] = s;
                            buf[offset++] = s;
                        }
                    }
                    else
                    {
                        while (inPtr != endPtr)
                        {
                            buf[offset++] = *(inPtr++);
                        }
                    }
                    // Engine Track: meter uncompressed input too, same
                    // pre-handler tap as the Opus path.
                    data.InputMeter?.Process(buf, (int)data.BufferSize, data.SampleRate);
                    if (data.Active) data.WavInputHandler(buf);
                    data.diagByteCount += (int)data.BufferSize;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("inCallback exception:" + ex.Message, TraceLevel.Error);
                    rv = PortAudio.PaStreamCallbackResult.paAbort;
                }
            }

            inCallbackDone:
            if (rv != PortAudio.PaStreamCallbackResult.paContinue)
            {
                if (data.UseOpus) data.OpusInputHandler(new byte[0]);
                else data.WavInputHandler(new float[0]);
                Tracing.TraceLine("InputCallback:" + data.diagBufCount + ' ' + data.diagByteCount, TraceLevel.Verbose);
                // Threads Track: the stream is completing — report the
                // glitch totals for its whole life.
                traceStatusFlagSummary(data, "input");
                traceLatencySummary(data, "input");
            }
            return rv;
        }

        private static PortAudio.PaStreamCallbackResult outputCallback(IntPtr inbuf,
            IntPtr outbuf,
            uint frameCount,
            ref PortAudio.PaStreamCallbackTimeInfo timeInfo,
            PortAudio.PaStreamCallbackFlags statusFlags,
            IntPtr userData)
        {
            StreamCB data = queues.getQ((int)userData);
            // Stream already removed from the table (close raced a late
            // callback): tell PortAudio to stop calling us.
            if (data == null) return PortAudio.PaStreamCallbackResult.paAbort;

            // Threads Track: read the glitch report before anything else —
            // a final callback can carry flags too.
            noteStatusFlags(data, statusFlags, "output");
            noteDeviceLatency(data, ref timeInfo, false);

            PortAudio.PaStreamCallbackResult rv = PortAudio.PaStreamCallbackResult.paContinue;
            if (!data.Active)
            {
                rv = PortAudio.PaStreamCallbackResult.paComplete;
                goto outCallbackDone;
            }
            data.SilentPeriod = false;

            // A mono playback device takes half the samples for the same number
            // of frames, so the queued stereo pair is mixed down to one. The
            // same argument as mono capture applies: refusing to play through
            // somebody's only speaker because it has one channel is not a
            // policy, it is a missing few lines.
            bool monoOut = (data.Channels == 1);
            float* outptr = (float*)outbuf;
            float* endptr = outptr + (monoOut ? data.BufferSize / 2 : data.BufferSize);
            while (data.Active)
            {
                bool silence = false;
                if (data.Offset == 0)
                {
                    // Fresh queued buffer.
                    if (data.Q.Count == 0)
                    {
                        Tracing.TraceLine("silence", TraceLevel.Verbose);
                        silence = true;
                        // Track B (#29): count the self-inflicted gap. See the
                        // field comments — this is the glitch statusFlags
                        // cannot see, because we fed the device on time, with
                        // zeros.
                        data.SilenceFills++;
                        if (data.OutputDataSeen)
                        {
                            data.StarvationFills++;

                            // #196: rate-limited "when". Environment.TickCount64
                            // is a cheap read with no allocation, safe from the
                            // realtime callback; the string is built at most
                            // once a second and only while something is wrong.
                            long nowTick = Environment.TickCount64;
                            if (data.StarvationWindowTick == 0) data.StarvationWindowTick = nowTick;
                            data.StarvationInWindow++;
                            if (nowTick - data.StarvationWindowTick >= 1000)
                            {
                                Tracing.TraceLine("audio output stream: "
                                    + data.StarvationInWindow + " starvation(s) in the last "
                                    + (nowTick - data.StarvationWindowTick) + " ms"
                                    + " (running total " + data.StarvationFills
                                    + ", callback " + data.StatusCallbackCount + ")",
                                    TraceLevel.Error);
                                data.StarvationWindowTick = nowTick;
                                data.StarvationInWindow = 0;
                            }

                            if (!data.StarvationLogged)
                            {
                                data.StarvationLogged = true;
                                Tracing.TraceLine("audio output stream: the playback queue ran dry "
                                    + "mid-stream at callback " + data.StatusCallbackCount
                                    + " — a device buffer was filled with silence, audible as a gap "
                                    + "with a click at each edge. PortAudio raises no flag for this "
                                    + "(we supplied the zeros ourselves). Further occurrences are "
                                    + "counted silently; totals logged when the stream closes.",
                                    TraceLevel.Error);
                            }
                        }
                    }
                    else
                    {
                        data.Buffer = (float[])data.Q.Dequeue();
                        data.OutputDataSeen = true;
                    }
                }
                // else still data in this buffer.

                if (silence)
                {
                    while (outptr != endptr) *(outptr++) = 0f;
                }
                else if (monoOut)
                {
                    // while there's data in the input, and room in the output:
                    while ((data.Offset < data.Buffer.Length) & (outptr != endptr))
                    {
                        float l = data.Buffer[data.Offset++];
                        // The queued buffers are stereo, so their length is
                        // even and the pair is always complete. Guarded anyway:
                        // half a frame of silence is a click, and a click is
                        // the kind of thing that gets chased for a week.
                        float r = (data.Offset < data.Buffer.Length)
                            ? data.Buffer[data.Offset++] : l;
                        *(outptr++) = (l + r) * 0.5f;
                    }
                    if (data.Offset >= data.Buffer.Length) data.Offset = 0;
                }
                else
                {
                    // while there's data in the input, and room in the output:
                    while ((data.Offset < data.Buffer.Length) & (outptr != endptr))
                    {
                        *(outptr++) = data.Buffer[data.Offset++];
                    }
                    if (data.Offset == data.Buffer.Length) data.Offset = 0;
                }
                if (outptr == endptr)
                {
                    break;
                }
            }

            outCallbackDone:
            if (rv == PortAudio.PaStreamCallbackResult.paContinue) rv = (data.Active) ? PortAudio.PaStreamCallbackResult.paContinue : PortAudio.PaStreamCallbackResult.paComplete;
            // Threads Track: stream completing — report the glitch totals
            // for its whole life.
            if (rv != PortAudio.PaStreamCallbackResult.paContinue)
            {
                traceStatusFlagSummary(data, "output");
                traceLatencySummary(data, "output");
                // Track B (#29): the queue-side companion summary. Zero
                // starvation is evidence too — with statusFlags also clean it
                // acquits the whole playback side and points the click hunt
                // upstream (see the receive-continuity meter in FlexBase).
                // #196: flush a partial window, so starvations in the final
                // second are reported rather than silently discarded at close.
                if (data.StarvationInWindow > 0)
                {
                    Tracing.TraceLine("audio output stream: "
                        + data.StarvationInWindow + " starvation(s) in the final partial second"
                        + " (callback " + data.StatusCallbackCount + ")",
                        TraceLevel.Error);
                    data.StarvationInWindow = 0;
                }
                Tracing.TraceLine("audio output queue summary: "
                    + data.SilenceFills + " silent fill(s), of which "
                    + data.StarvationFills + " were mid-stream starvation"
                    + (data.StarvationFills == 0
                        ? " (the queue never ran dry while playing)" : ""),
                    data.StarvationFills == 0 ? TraceLevel.Info : TraceLevel.Error);
            }
            if ((rv == PortAudio.PaStreamCallbackResult.paContinue) &
                (data.Q.Count == 0))
            {
                // Only call once per silent period.
                if (!data.SilentPeriod & (data.AudioSent != null))
                {
                    data.SilentPeriod = true;
                    data.AudioSent();
                }
            }
            else data.SilentPeriod = false;
            //if (rv != PortAudio.PaStreamCallbackResult.paContinue)
            //{
            //Tracing.TraceLine("outCallback:" + rv.ToString(), TraceLevel.Error);
            //}
            return rv;
        }
    }
}
