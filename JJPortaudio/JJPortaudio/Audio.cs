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
                    case workItems.open:
                        {
                            Tracing.TraceLine("server:open", TraceLevel.Info);
                            PortAudio.PaStreamParameters devParms = new PortAudio.PaStreamParameters();
                            devParms.device = item.StreamBlock.Device.DevinfoID;
                            devParms.channelCount = 2;
                            devParms.sampleFormat = PortAudio.PaSampleFormat.paFloat32;
                            devParms.suggestedLatency = (item.StreamBlock.Device.Type==Devices.DeviceTypes.input)?
                                item.StreamBlock.Device.defaultLowInputLatency:
                                item.StreamBlock.Device.defaultLowOutputLatency;
                            devParms.hostApiSpecificStreamInfo = (IntPtr)null;
                            PortAudio.PaStreamParameters* nullParms = null;
                            PortAudio.PaStreamParameters* p1 = (item.StreamBlock.Device.Type == Devices.DeviceTypes.input) ?
                                &devParms : nullParms;
                            PortAudio.PaStreamParameters* p2 = (item.StreamBlock.Device.Type == Devices.DeviceTypes.output) ?
                                &devParms : nullParms;

                            erv = PortAudio.Pa_IsFormatSupported(ref *p1, ref *p2, (double)item.StreamBlock.SampleRate);
                            if (erv != 0)
                            {
                                Tracing.TraceLine("rate not supported:" + item.StreamBlock.SampleRate + " changing to " + item.StreamBlock.Device.defaultSampleRate, TraceLevel.Error);
                                item.StreamBlock.SampleRate = (uint)item.StreamBlock.Device.defaultSampleRate;
                            }

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
                                // wait for callback to complete the stream.
                                while ((int)PortAudio.Pa_IsStreamActive(item.StreamBlock.Stream) == 1) Thread.Sleep(110);
                                Tracing.TraceLine("serverProc:stop or close:wait done", TraceLevel.Info);
                                erv = PortAudio.Pa_StopStream(item.StreamBlock.Stream);
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
            public PortAudio.PaStreamCallbackDelegate CB;
            public int CBUser;
            public WavCallback WavInputHandler;
            public OpusCallback OpusInputHandler;
            public AudioSentCallback AudioSent;
            public bool SilentPeriod = false;
        }
        internal class staticQueues
        {
            private Dictionary<int, StreamCB> Qs = new Dictionary<int, StreamCB>();
            private Random rand = new Random();
            public int Add()
            {
                int key;
                do
                {
                    key = rand.Next();
                } while (Qs.Keys.Contains(key));
                Qs.Add(key, new StreamCB());
                return key;
            }
            public void Remove(int key)
            {
                Qs.Remove(key);
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
            Tracing.TraceLine("Audio.Open:" + CBData.Device.Name + ' ' + rate + ' ' + useOpus.ToString()
                + " api=" + CBData.Device.hostApiName
                + " inCh=" + CBData.Device.maxInputChannels
                + " outCh=" + CBData.Device.maxOutputChannels, TraceLevel.Info);
            CBData.SampleRate = (rate == 0) ? (uint)CBData.Device.defaultSampleRate : rate;
            if (outputCallback != null) CBData.CB = outputCallback;
            else
            {
                // Use the default callback defined here.
                CBData.CB = (CBData.Device.Type == Devices.DeviceTypes.input) ? inCallback : outCallback;
            }
            CBData.CBUser = qKey;
            CBData.UseOpus = useOpus;
            uint bufSZ;
            if (useOpus)
            {
                POpusCodec.Enums.SamplingRate oRate;
                switch (rate)
                {
                    case 800: oRate = POpusCodec.Enums.SamplingRate.Sampling08000; break;
                    case 12000: oRate = POpusCodec.Enums.SamplingRate.Sampling12000; break;
                    case 16000: oRate = POpusCodec.Enums.SamplingRate.Sampling16000; break;
                    case 24000: oRate = POpusCodec.Enums.SamplingRate.Sampling24000; break;
                    default: oRate = POpusCodec.Enums.SamplingRate.Sampling48000; break;
                }
                // always create the encoder to get values for bufSZ.
                CBData.Encoder = new OpusEncoder(oRate, POpusCodec.Enums.Channels.Stereo);
                CBData.Encoder.EncoderDelay = POpusCodec.Enums.Delay.Delay10ms;
                // JJFlex diag 2026-08-10 (708 TX-audio): mirror the shipped
                // SmartSDR 4.2.18 TX encoder profile exactly (decompiled
                // OpusStreamViewModel.OpusEncodingThread): bitrate 70000,
                // Complexity 1, MaxBandwidth Fullband, in-band FEC off, 10 ms
                // frames, stereo. We previously left bitrate at the libopus
                // default (~100+ kbps VBR observed) and capped bandwidth at
                // SuperWideband. The VITA wrapping is proven byte-identical to
                // the working client, so the encode profile is the remaining
                // field-level difference on the TX wire; replicate it rather
                // than improvise.
                // Mirror the shipped SmartSDR 4.2.18 TX encoder profile exactly:
                // 70000 bps VBR, Complexity 1, Fullband cap (yields CELT-only
                // SWB / TOC 0xD4 at 24 kHz), FEC off, 10 ms stereo. 2026-08-10
                // pcap confirmed our on-wire Opus TOC + payload are byte-identical
                // to SmartSDR's working stream — the 714 hard-CBR experiment was
                // unnecessary (SmartSDR is VBR) and is reverted here.
                CBData.Encoder.Bitrate = 70000;
                CBData.Encoder.Complexity = POpusCodec.Enums.Complexity.Complexity1;
                CBData.Encoder.MaxBandwidth = POpusCodec.Enums.Bandwidth.Fullband;
                CBData.Encoder.UseInbandFEC = false;
                CBData.OpusFrameSZ = (uint)CBData.Encoder.FrameSizePerChannel * 2;
                // Get a buffer size to yield 10 callbacks/second.
                float channelsPerDecisec = (float)rate / (float)CBData.Encoder.FrameSizePerChannel / cbPerSec;
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
                bufSZ = (rate * 2) / (uint)cbPerSec;
                if (inOut == Devices.DeviceTypes.input)
                {
                    // input buffer cache
                    CBData.opusPool = new bufPool(bufSZ, 10);
                }
            }
            Tracing.TraceLine("Audio.Open buffer size set to:" + bufSZ, TraceLevel.Info);
            CBData.BufferSize = bufSZ;

            AudioAnchor.work.Add(new AudioAnchor.workItem(AudioAnchor.workItems.open, CBData));

            // Await the open (0.5 seconds).
            while (!Tracing.await(() => { return CBData.Open; }, 500)) { }

            return (CBData.Open) ? true : false;
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
            int smallWait = 200;
            int longWait = smallWait * 25;
            while (longWait-- != 0)
            {
                while (CBData != null) Thread.Sleep(smallWait);
            }
            if (CBData != null)
            {
                Tracing.TraceLine("audio.Finished:didn't stop", TraceLevel.Error);
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
                while (allocater.IsAlive) Thread.Sleep(1);
            }
        }

        private static int bufCount = 0;
        private static int byteCount = 0;
        // Diag 2026-08-10: once-per-second capture peak, so a trace shows whether
        // real audio or pure zeros is entering at the PortAudio boundary.
        private static float inPeak = 0f;
        private static int inFrames = 0;
        private static int inLevelLastLog = 0;
        private static PortAudio.PaStreamCallbackResult inputCallback(IntPtr inbuf,
                IntPtr outbuf,
                uint frameCount,
                ref PortAudio.PaStreamCallbackTimeInfo timeInfo,
                PortAudio.PaStreamCallbackFlags statusFlags,
                IntPtr userData)
        {
            StreamCB data = queues.getQ((int)userData);

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
                            float s = *(inPtr++);
                            buf[i] = s;
                            if (s > inPeak) inPeak = s; else if (-s > inPeak) inPeak = -s;
                        }
                        byte[] encodedBuf = data.Encoder.Encode(buf);
                        if (!data.Active) break;
                        data.OpusInputHandler(encodedBuf);
                        bufCount++;
                        byteCount += buf.Length;
                        inFrames++;
                        int nowMs = System.Environment.TickCount;
                        if (nowMs - inLevelLastLog >= 1000)
                        {
                            Tracing.TraceLine("InputCallback level: peak=" + inPeak.ToString("F4")
                                + " over " + inFrames + " frames", TraceLevel.Info);
                            inPeak = 0f; inFrames = 0; inLevelLastLog = nowMs;
                        }
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
                bufCount++;                
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
                    if (data.Active) data.WavInputHandler(buf);
                    byteCount += (int)data.BufferSize;
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
                Tracing.TraceLine("InputCallback:" + bufCount + ' ' + byteCount, TraceLevel.Verbose);
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
