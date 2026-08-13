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
            p.channelCount = 2;
            p.sampleFormat = PortAudio.PaSampleFormat.paFloat32;
            p.suggestedLatency = (cb.Device.Type == Devices.DeviceTypes.input) ?
                cb.Device.defaultLowInputLatency : cb.Device.defaultLowOutputLatency;
            p.hostApiSpecificStreamInfo = (IntPtr)null;
            return p;
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
                            if (requested > 0
                                && PortAudio.Pa_IsFormatSupported(ref *p1, ref *p2, (double)requested) == 0)
                            {
                                Tracing.TraceLine("server:negotiate:" + cb.Device.Name
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
                                        + " refused " + requested + " Hz, using " + settled
                                        + " Hz" + (cb.UseOpus ? " (Opus-legal)" : ""), TraceLevel.Error);
                                    cb.SampleRate = settled;
                                }
                                else
                                {
                                    // Nothing was accepted. Keep the requested
                                    // rate and let Pa_OpenStream produce the
                                    // real error text — PortAudio's own answer
                                    // is worth more to whoever reads the trace
                                    // than a rate invented here.
                                    Tracing.TraceLine("server:negotiate:" + cb.Device.Name
                                        + " reported no usable rate; leaving " + requested
                                        + " Hz for the open to fail on", TraceLevel.Error);
                                }
                            }
                            cb.RateSettled = true;
                        }
                        break;
                    case workItems.open:
                        {
                            Tracing.TraceLine("server:open", TraceLevel.Info);
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
                                erv = PortAudio.Pa_OpenStream(
                                    out item.StreamBlock.Stream,
                                    ref *p1,
                                    ref *p2,
                                    item.StreamBlock.SampleRate,
                                    item.StreamBlock.BufferSize / 2,
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
                                if (item.StreamBlock.Encoder != null) item.StreamBlock.Encoder.Dispose();
                                if (item.StreamBlock.Decoder != null) item.StreamBlock.Decoder.Dispose();
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
            public OpusEncoder Encoder;
            public OpusDecoder Decoder;
            public Queue Q = Queue.Synchronized(new Queue());
            public uint Offset = 0; // outputCallback's buffer offset
            public float[] Buffer; // for output data
            public uint BufferSize;
            public uint SampleRate;
            // Set by the AudioServer thread when workItems.negotiate has
            // written the answer into SampleRate. Audio.Open waits on this
            // before it sizes a buffer or builds a codec, because everything
            // downstream of the rate has to agree with the rate the device
            // actually accepted — see the negotiate case in serverProc.
            public bool RateSettled = false;
            public PortAudio.PaStreamCallbackDelegate CB;
            public int CBUser;
            public WavCallback WavInputHandler;
            public OpusCallback OpusInputHandler;
            public AudioSentCallback AudioSent;
            public bool SilentPeriod = false;
            // Audio Track C: optional TX test-tone source. When engaged, its
            // samples REPLACE the mic capture in inputCallback ahead of the
            // Opus encode (the mic is discarded, never mixed).
            public TxToneGenerator ToneSource;
            // Engine Track: optional LUFS meter, fed in inputCallback AFTER
            // the tone injection point so it measures whatever is actually
            // being transmitted, tone or mic.
            public LufsMeter InputMeter;
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
        internal TxToneGenerator ToneSource
        {
            get { return CBData?.ToneSource; }
            set { var cb = CBData; if (cb != null) cb.ToneSource = value; }
        }
        internal LufsMeter InputMeter
        {
            get { return CBData?.InputMeter; }
            set { var cb = CBData; if (cb != null) cb.InputMeter = value; }
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
        /// <returns>new Device, null on failure</returns>
        internal bool Open(Devices.DeviceTypes inOut, uint rate, bool useOpus=false,
            PortAudio.PaStreamCallbackDelegate outputCallback = null, int cbPerSec = 10)
        {
            CBData.Device = (inOut == Devices.DeviceTypes.input) ?
                inDevice : outDevice;
            Tracing.TraceLine("Audio.Open:" + CBData.Device.Name + ' ' + rate + ' ' + useOpus.ToString(), TraceLevel.Info);
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
                // always create the encoder to get values for bufSZ.
                CBData.Encoder = new OpusEncoder(oRate, POpusCodec.Enums.Channels.Stereo);
                CBData.Encoder.MaxBandwidth = POpusCodec.Enums.Bandwidth.SuperWideband;
                CBData.Encoder.EncoderDelay = POpusCodec.Enums.Delay.Delay10ms;
                CBData.OpusFrameSZ = (uint)CBData.Encoder.FrameSizePerChannel * 2;
                // Get a buffer size to yield 10 callbacks/second.
                float channelsPerDecisec = (float)openRate / (float)CBData.Encoder.FrameSizePerChannel / cbPerSec;
                bufSZ = (uint)(channelsPerDecisec * (float)CBData.OpusFrameSZ);
                // We'll use bufSZ for input and output.
                if (inOut == Devices.DeviceTypes.input)
                {
                    CBData.opusPool = new bufPool(CBData.OpusFrameSZ, 200);
                }
                else
                {
                    CBData.Encoder.Dispose();
                    CBData.Encoder = null;
                    CBData.Decoder = new OpusDecoder(oRate, POpusCodec.Enums.Channels.Stereo);
                }
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

        internal void Finished()
        {
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

            PortAudio.PaStreamCallbackResult rv = PortAudio.PaStreamCallbackResult.paContinue;
            if (!data.Active)
            {
                Tracing.TraceLine("audio.inputCallback done", TraceLevel.Info);
                rv = PortAudio.PaStreamCallbackResult.paComplete;
                goto inCallbackDone;
            }

            float* inPtr = (float*)inbuf;
            float* endPtr = inPtr + data.BufferSize;
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
                        for (int i = 0; i < data.OpusFrameSZ; i++)
                        {
                            buf[i] = *(inPtr++);
                        }
                        // Audio Track C: TX test tone. When engaged this
                        // REPLACES the mic samples in buf (mute-by-discard,
                        // never a mix) before the Opus encode, so the tone
                        // rides the identical encode-and-send path the mic
                        // does — an honest test of the whole TX chain.
                        data.ToneSource?.Process(buf, (int)data.OpusFrameSZ, data.SampleRate);
                        // Engine Track: LUFS metering, deliberately AFTER the
                        // tone injection — the meter reads whatever is really
                        // going to the encoder, tone or mic, pre-Opus.
                        data.InputMeter?.Process(buf, (int)data.OpusFrameSZ, data.SampleRate);
                        byte[] encodedBuf = data.Encoder.Encode(buf);
                        if (!data.Active) break;
                        data.OpusInputHandler(encodedBuf);
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
                    while (inPtr != endPtr)
                    {
                        buf[offset++] = *(inPtr++);
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

            PortAudio.PaStreamCallbackResult rv = PortAudio.PaStreamCallbackResult.paContinue;
            if (!data.Active)
            {
                rv = PortAudio.PaStreamCallbackResult.paComplete;
                goto outCallbackDone;
            }
            data.SilentPeriod = false;

            float* outptr = (float*)outbuf;
            float* endptr = outptr + data.BufferSize;
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
                    }
                    else
                    {
                        data.Buffer = (float[])data.Q.Dequeue();
                    }
                }
                // else still data in this buffer.

                if (silence)
                {
                    while (outptr != endptr) *(outptr++) = 0f;
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
                traceStatusFlagSummary(data, "output");
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
