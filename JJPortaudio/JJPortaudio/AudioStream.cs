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
using JJTrace;
using POpusCodec;
using PortAudioSharp;

namespace JJPortaudio
{
    /// <summary>
    /// Track I: in-place processor for the transmit input callback —
    /// (buffer, floatCount, sampleRate), interleaved stereo, called on the
    /// PortAudio callback thread between the test-tone injection point and
    /// the LUFS meter. TxAudioConditioner.Process matches this contract.
    /// </summary>
    public delegate void TxAudioProcessorCallback(float[] buffer, int count, uint sampleRate);

    public class JJAudioStream
    {
        private const uint defaultSampleRate = 48000;
        private const uint defaultBufsz = 4800;
        private Audio aud = null;

        /// <summary>
        /// True for the five rates Opus can encode. A device that runs at any
        /// other rate — 44.1 kHz being the common one — cannot carry the radio
        /// link's audio, because the codec has no mode to follow it into.
        /// Public so a diagnostic surface can say so before an operator finds
        /// out by transmitting.
        /// </summary>
        public static bool IsOpusRate(uint rate) => AudioAnchor.isOpusRate(rate);

        /// <summary>
        /// Output gain scalar applied to decoded audio samples.
        /// 1.0 = unity (no change), 2.0 = +6dB boost, etc.
        /// Default is 1.0 (no gain applied).
        /// </summary>
        public float OutputGain { get; set; } = 1.0f;

        /// <summary>
        /// Optional audio processing delegate invoked on decoded PCM samples
        /// after gain scaling, before PortAudio enqueue. Used by the PC-side
        /// noise reduction pipeline (RNNoise, spectral subtraction).
        /// The delegate receives the float[] buffer to process in-place.
        /// Called from the remoteAudioProc thread.
        /// </summary>
        public Action<float[]>? PostDecodeProcessor { get; set; }

        /// <summary>
        /// Audio Track C: optional TX injection source for input streams.
        /// When set and engaged, its samples REPLACE the microphone samples in
        /// the input callback ahead of the Opus encode — the mic is discarded
        /// (muted), never mixed. Set after OpenOpus.
        /// </summary>
        /// <remarks>
        /// Sprint 33 Track I: this used to be typed <see cref="TxToneGenerator"/>
        /// and named <c>InputToneSource</c> for it. It is the one place
        /// anything can stand in for the microphone, so it now takes
        /// <see cref="ITxInputSource"/> — the test tone, the reference-voice
        /// player, or a <see cref="TxInputSourceMux"/> carrying several.
        /// </remarks>
        public ITxInputSource InputSource
        {
            get { return (aud != null) ? aud.ToneSource : null; }
            set { if (aud != null) aud.ToneSource = value; }
        }

        /// <summary>
        /// Track I: optional in-place processor for input streams (the TX
        /// conditioning chain — noise reduction and the gate). Runs in the
        /// input callback AFTER the tone injection point and BEFORE the LUFS
        /// meter, so the meter keeps measuring what genuinely goes out.
        /// Skipped while the test tone is engaged: the tone is a calibrated
        /// reference that must arrive at the encoder untouched, and there is
        /// no room noise in a synthesized sine to clean. Set after OpenOpus.
        /// </summary>
        public TxAudioProcessorCallback InputProcessor
        {
            get { return (aud != null) ? aud.InputProcessor : null; }
            set { if (aud != null) aud.InputProcessor = value; }
        }

        /// <summary>
        /// Engine Track: optional LUFS meter for input streams. Fed in the
        /// input callback AFTER the test-tone injection point, pre-Opus, so it
        /// measures exactly what is being encoded and sent — tone or mic.
        /// Set after OpenOpus/OpenAudio.
        /// </summary>
        public LufsMeter InputLufsMeter
        {
            get { return (aud != null) ? aud.InputMeter : null; }
            set { if (aud != null) aud.InputMeter = value; }
        }

        /// <summary>
        /// buffer size used for this stream.
        /// </summary>
        public uint BufferSize { get { return aud.BufferSize; } }
        /// <summary>
        /// The rate this stream actually opened at. May differ from the rate
        /// requested — the device gets the last word — so this is the number to
        /// report, log or speak.
        /// </summary>
        public uint SampleRate { get { return (aud != null) ? aud.SampleRate : 0; } }
        /// <summary>
        /// Channels this stream opened with: 1 on a genuinely mono device, 2
        /// otherwise. The audio itself is stereo either way — a mono capture is
        /// duplicated onto both channels and a mono playback device gets the
        /// pair mixed down — so this is for reporting, not for arithmetic.
        /// </summary>
        public int Channels { get { return (aud != null) ? aud.Channels : Devices.StreamChannels; } }

        /// <summary>
        /// The Opus sample rates JJ Flex will offer for transmit, highest
        /// first. 48 kHz is the default and the only one the radio path has
        /// been proven at; the lower rates are the fallback for a constrained
        /// link. The frame duration is 10 ms at every one of them, so the
        /// packet cadence the radio expects — 100 Opus frames a second — does
        /// not change with the rate.
        /// </summary>
        public static readonly uint[] OpusTxRates = { 48000, 24000, 16000, 12000, 8000 };
        /// <summary>
        /// true if stream is active.
        /// </summary>
        public bool IsActive { get { return aud.IsActive; } }
        public Audio.AudioSentCallback AudioSent
        {
            get { return aud.AudioSent; }
            set { aud.AudioSent = value; }
        }

        /// <summary>
        /// Open this audio device.
        /// </summary>
        /// <param name="inOut">input/output</param>
        /// <param name="rate">sample rate</param>
        /// <param name="inCallback">called with input data, type input only</param>
        /// <param name="audioCallback">(optional) audio callback</param>
        /// <param name="cbPerSec">(optional) callbacks per second, default 10</param>
        /// <returns>true on success</returns>
        public bool OpenAudio(Devices.DeviceTypes inOut, uint rate, Audio.WavCallback inCallback = null,
            PortAudio.PaStreamCallbackDelegate audioCallback = null,
            int cbPerSec = AudioBuffering.DefaultCallbacksPerSecond,
            string streamName = null)
        {
            Tracing.TraceLine("audioStream.open:" + inOut.ToString() + ' ' + rate, TraceLevel.Info);
            aud = new Audio();
            aud.WavInputHandler = inCallback;
            bool rv = aud.Open(inOut, rate, false, audioCallback, cbPerSec, null, streamName);
            if (!rv)
            {
                aud.Finished();
            }
            return rv;
        }

        /// <summary>
        /// Open an opus device.
        /// </summary>
        /// <param name="inOut">input/output</param>
        /// <param name="sampleRate">(optional) sample rate</param>
        /// <param name="inCallback">called with input data, type input only</param>
        /// <param name="audioCallback">(optional) audio callback</param>
        /// <param name="cbPerSec">(optional) callbacks per second, default 10</param>
        /// <param name="profile">
        /// (optional) Opus ENCODER settings. Null means
        /// <see cref="OpusEncodeProfile.Shipped"/> — the proven profile, byte
        /// for byte what this path built before profiles existed. Ignored on an
        /// output stream in every respect except the frame duration, which sets
        /// the buffer granularity; decoding takes all its parameters from the
        /// packets themselves (#460).
        /// </param>
        /// <returns>true on success</returns>
        public bool OpenOpus(Devices.DeviceTypes inOut, uint sampleRate, Audio.OpusCallback inCallback = null,
            PortAudio.PaStreamCallbackDelegate audioCallback=null,
            int cbPerSec = AudioBuffering.DefaultCallbacksPerSecond,
            OpusEncodeProfile profile = null,
            string streamName = null)
        {
            Tracing.TraceLine("audioStream.OpenOpus:" + sampleRate, TraceLevel.Info);

            // Open the device.
            aud = new Audio();
            aud.OpusInputHandler = inCallback;
            if (!aud.Open(inOut, sampleRate, true, audioCallback, cbPerSec, profile, streamName))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Start an open audio device.
        /// </summary>
        /// <returns>True on success</returns>
        public bool StartAudio()
        {
            Tracing.TraceLine("audioStream.Start", TraceLevel.Info);
            bool rv = aud.Start();
            if (!rv)
            {
                aud.Finished();
            }
            return rv;
        }

        /// <summary>
        /// Produce transmit frames from elapsed time instead of from the
        /// capture device (#208).
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a source that has no clock of its own — a synthesized tone, a
        /// file being played out — the capture device contributes nothing to
        /// the signal, and borrowing its clock hands the signal every property
        /// of a device it never touched. A device running a fraction of a
        /// percent off its nominal rate produces a constant rate error, and a
        /// constant rate error against the radio's jitter buffer is heard as a
        /// periodic correction: the galloping.
        /// </para>
        /// <para>
        /// <b>Stop the capture stream first.</b> Both producers share one Opus
        /// encoder, and Opus is stateful — running them together would corrupt
        /// the bitstream into something the radio renders as noise rather than
        /// as an error. <see cref="StopAudio"/> waits for the callback to
        /// quiesce, so a stop-then-start handover has no overlap.
        /// </para>
        /// </remarks>
        public bool StartSelfClockedTx()
        {
            return (aud != null) && aud.StartSelfClockedTx();
        }

        /// <summary>
        /// Stop the self-clock — hard and immediate, no drain, no tail.
        /// </summary>
        /// <remarks>
        /// Ratified 2026-08-24: transmit stop stops everything, whichever
        /// source was feeding it. Letting a source finish its release ramp is a
        /// different decision made one level up, by not calling this until the
        /// source reports <see cref="ITxInputSource.Idle"/>.
        /// </remarks>
        public void StopSelfClockedTx()
        {
            aud?.StopSelfClockedTx();
        }

        /// <summary>True while transmit frames are coming from the self-clock.</summary>
        public bool SelfClockedTxRunning => (aud != null) && aud.SelfClockedTxRunning;

        /// <summary>
        /// Stop an open audio device.
        /// </summary>
        /// <returns>True on success</returns>
        public bool StopAudio()
        {
            Tracing.TraceLine("audioStream.Stop", TraceLevel.Info);
            aud.Stop();
            // Clear AFTER the stop, not before it (#473).
            //
            // This line used to run first, and the stop it precedes is not
            // instantaneous: it posts a work item, the server flips Active, and
            // the callback keeps running until PortAudio notices. Every callback
            // in that window found an empty queue and recorded a starvation —
            // which is why every receive stream captured on 2026-09-01 ended
            // with "4 starvation(s) in the final partial second" and similar,
            // four to five of a stream's six-to-nine total starvations
            // manufactured by its own teardown. Nothing was audible; the meter
            // was reporting the shutdown.
            //
            // Nothing is lost by moving it: workItems.start clears the queue
            // again on the way in, so a restart still cannot play stale audio.
            aud.TheQ.Clear();
            return true;
        }

        /// <summary>
        /// Write audio data
        /// </summary>
        /// <param name="data">float data array</param>
        public void Write(float[] data)
        {
            // don't pass data directly.
            float[] buf = new float[data.Length];
            Array.Copy(data, buf, data.Length);
            aud.TheQ.Enqueue(buf);
        }

        /// <summary>
        /// Write opus data
        /// </summary>
        /// <param name="data">byte array</param>
        private int _peakLogCounter = 0;
        private float _peakSinceLastLog = 0f;
        private float _postPeakSinceLastLog = 0f;
        // Track B, 2026-08-18 (#17): RMS alongside the peaks. A peak says how
        // hot the loudest instant was; RMS says how loud the stream actually
        // IS, which is the number an operator's "it sounds quiet" can be
        // compared against. Accumulated across the same ~5 s window the peak
        // log uses, raw (pre-gain) and measured output (post-gain,
        // post-processing) separately, so the two RMS figures bracket the
        // gain stage exactly as the two peaks do.
        private double _rawSquaresSinceLastLog;
        private double _postSquaresSinceLastLog;
        private long _meterSamplesSinceLastLog;
        // Short-term LUFS of what is actually playing (post-gain,
        // post-processing) — the perceptual companion to the RMS figure,
        // read from the 3-second window so it is meaningful at the 5-second
        // log cadence. ShortTermLufs is a lock-free read; the gated
        // IntegratedLufs (which locks and copies) is deliberately not used
        // here — this runs on the remote-audio thread every 10 ms packet.
        private readonly LufsMeter _outputLufs = new LufsMeter();
        public void WriteOpus(byte[] data)
        {
            float[] buf = aud.Decoder.DecodePacketFloat(data);

            // Track peak and RMS level before gain for diagnostics
            float prePeak = 0f;
            double preSquares = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                float s = buf[i];
                float abs = Math.Abs(s);
                if (abs > prePeak) prePeak = abs;
                preSquares += (double)s * s;
            }

            if (OutputGain != 1.0f)
            {
                // Hard limit at full scale. The gain used to be a bare multiply
                // with no bounds check — safe at the historical fixed 4x on
                // observed material (raw peaks ~0.02-0.10), but the gain is
                // operator-adjustable now (Audio Arc Track A, up to +24 dB),
                // and an unclamped boost on a hot source would wrap into harsh
                // digital garbage. Clamping turns overdrive into ordinary
                // clipping instead.
                for (int i = 0; i < buf.Length; i++)
                {
                    float s = buf[i] * OutputGain;
                    if (s > 1.0f) s = 1.0f;
                    else if (s < -1.0f) s = -1.0f;
                    buf[i] = s;
                }
            }

            if (prePeak > _peakSinceLastLog) _peakSinceLastLog = prePeak;

            // PC-side audio processing (NR, spectral subtraction, etc.)
            PostDecodeProcessor?.Invoke(buf);

            // Measure what is actually going to the speakers, AFTER the gain,
            // the limiter and any noise reduction — rather than computing it
            // from the raw peak, which is what this used to do.
            //
            // Track E, 2026-08-16. This line is the instrument for the standing
            // "decoded PC audio arrives too quiet" report, so it has to be
            // trustworthy before anyone re-measures with it. Computing
            // raw x gain silently omitted PostDecodeProcessor, which is where
            // JJ Neural NR and Spectral NR run and which can move the level by
            // a great deal — so on a machine with either of those turned on,
            // the "output peak" in the trace was a number that never existed.
            // Chasing a quiet-audio report with an instrument that reports a
            // level nothing produced is worse than having no instrument.
            float postPeak = 0f;
            double postSquares = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                float s = buf[i];
                float abs = Math.Abs(s);
                if (abs > postPeak) postPeak = abs;
                postSquares += (double)s * s;
            }
            if (postPeak > _postPeakSinceLastLog) _postPeakSinceLastLog = postPeak;
            _rawSquaresSinceLastLog += preSquares;
            _postSquaresSinceLastLog += postSquares;
            _meterSamplesSinceLastLog += buf.Length;

            // Perceptual loudness of the same post-everything audio. The
            // meter wants the stream's real rate; before the stream reports
            // one (0), skip rather than feed a lie.
            uint meterRate = aud.SampleRate;
            if (meterRate > 0) _outputLufs.Process(buf, buf.Length, meterRate);

            // Every ~5 seconds, at ~100 packets/sec.
            if (++_peakLogCounter >= 500)
            {
                // The limiter caps at full scale, so a raw peak that would have
                // exceeded it is the clipping test — the measured peak cannot
                // report clipping on its own, because the clamp is exactly what
                // stops it going above 1.0.
                bool clipped = (_peakSinceLastLog * OutputGain) > 1.0f;
                long n = Math.Max(_meterSamplesSinceLastLog, 1);
                double rawRmsDb = 10 * Math.Log10(_rawSquaresSinceLastLog / n + 1e-20);
                double postRmsDb = 10 * Math.Log10(_postSquaresSinceLastLog / n + 1e-20);
                float lufs = _outputLufs.ShortTermLufs;
                Tracing.TraceLine($"OpusAudio: raw peak={_peakSinceLastLog:F4} "
                    + $"({20 * Math.Log10(_peakSinceLastLog + 1e-10):F1} dBFS), raw RMS={rawRmsDb:F1} dBFS, "
                    + $"gain={OutputGain:F1}x, "
                    + $"measured output peak={_postPeakSinceLastLog:F4} "
                    + $"({20 * Math.Log10(_postPeakSinceLastLog + 1e-10):F1} dBFS), "
                    + $"output RMS={postRmsDb:F1} dBFS, output loudness={lufs:F1} LUFS"
                    + $"{(PostDecodeProcessor != null ? ", post-decode processing ON" : "")}"
                    + $"{(clipped ? " CLIPPED" : "")}", TraceLevel.Info);
                _peakLogCounter = 0;
                _peakSinceLastLog = 0f;
                _postPeakSinceLastLog = 0f;
                _rawSquaresSinceLastLog = 0;
                _postSquaresSinceLastLog = 0;
                _meterSamplesSinceLastLog = 0;
            }

            aud.TheQ.Enqueue(buf);
        }

        public float[] OpusDecode(byte[] buf)
        {
            return aud.Decoder.DecodePacketFloat(buf);
        }

        /// <summary>
        /// Drain the queue.
        /// </summary>
        public void Flush()
        {
                while (aud.IsActive && (aud.TheQ.Count > 0)) Thread.Sleep(1);
        }

        /// <summary>
        /// Cleanup
        /// </summary>
        public void Close()
        {
            if (aud != null) aud.Finished();
        }

#if zero
        // Region - beep
        #region beep
        /// <summary>
        /// Open a beeper
        /// </summary>
        /// <returns>true on success</returns>
        public bool BeepOpen()
        {
            Tracing.TraceLine("AudioStream.BeepOpen", TraceLevel.Info);
            return Beep.Open();
        }

        /// <summary>
        /// Start a beeper
        /// </summary>
        public void BeepStart()
        {
            Beep.Start();
        }

        /// <summary>
        /// stop a beeper, doesn't close it.
        /// </summary>
        public void BeepStop()
        {
            Beep.End();
        }

        /// <summary>
        /// sound a beep
        /// </summary>
        public void BeepOn()
        {
            Beep.On();
        }

        /// <summary>
        ///  silence the beep
        /// </summary>
        public void BeepOff()
        {
            Beep.Off();
        }

        /// <summary>
        /// Set beep frequency
        /// </summary>
        /// <param name="f">freq in HZ</param>
        public void BeepFreq(uint f)
        {
            Beep.Freq(f);
        }

        /// <summary>
        /// set beep amplitude
        /// </summary>
        /// <param name="a">amplitude, usually 1.</param>
        public void BeepAmplitude(float a)
        {
            Beep.Amplitude(a);
        }

        /// <summary>
        /// close the beeper
        /// </summary>
        public void BeepExit()
        {
            Beep.Finished();
        }
        #endregion
#endif
    }
}
