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
        /// Audio Track C: optional TX test-tone source for input streams.
        /// When set and engaged, generated samples REPLACE the microphone
        /// samples in the input callback ahead of the Opus encode — the mic
        /// is discarded (muted), never mixed. Set after OpenOpus.
        /// </summary>
        public TxToneGenerator InputToneSource
        {
            get { return (aud != null) ? aud.ToneSource : null; }
            set { if (aud != null) aud.ToneSource = value; }
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
            PortAudio.PaStreamCallbackDelegate audioCallback = null, int cbPerSec = 10)
        {
            Tracing.TraceLine("audioStream.open:" + inOut.ToString() + ' ' + rate, TraceLevel.Info);
            aud = new Audio();
            aud.WavInputHandler = inCallback;
            bool rv = aud.Open(inOut, rate, false, audioCallback, cbPerSec);
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
        /// <returns>true on success</returns>
        public bool OpenOpus(Devices.DeviceTypes inOut, uint sampleRate, Audio.OpusCallback inCallback = null,
            PortAudio.PaStreamCallbackDelegate audioCallback=null, int cbPerSec = 10)
        {
            Tracing.TraceLine("audioStream.OpenOpus:" + sampleRate, TraceLevel.Info);

            // Open the device.
            aud = new Audio();
            aud.OpusInputHandler = inCallback;
            if (!aud.Open(inOut, sampleRate, true, audioCallback, cbPerSec))
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
        /// Stop an open audio device.
        /// </summary>
        /// <returns>True on success</returns>
        public bool StopAudio()
        {
            aud.TheQ.Clear();
            Tracing.TraceLine("audioStream.Stop", TraceLevel.Info);
            aud.Stop();
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
        public void WriteOpus(byte[] data)
        {
            float[] buf = aud.Decoder.DecodePacketFloat(data);

            // Track peak level before gain for diagnostics
            float prePeak = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float abs = Math.Abs(buf[i]);
                if (abs > prePeak) prePeak = abs;
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
            for (int i = 0; i < buf.Length; i++)
            {
                float abs = Math.Abs(buf[i]);
                if (abs > postPeak) postPeak = abs;
            }
            if (postPeak > _postPeakSinceLastLog) _postPeakSinceLastLog = postPeak;

            // Every ~5 seconds, at ~100 packets/sec.
            if (++_peakLogCounter >= 500)
            {
                // The limiter caps at full scale, so a raw peak that would have
                // exceeded it is the clipping test — the measured peak cannot
                // report clipping on its own, because the clamp is exactly what
                // stops it going above 1.0.
                bool clipped = (_peakSinceLastLog * OutputGain) > 1.0f;
                Tracing.TraceLine($"OpusAudio: raw peak={_peakSinceLastLog:F4} "
                    + $"({20 * Math.Log10(_peakSinceLastLog + 1e-10):F1} dBFS), gain={OutputGain:F1}x, "
                    + $"measured output peak={_postPeakSinceLastLog:F4} "
                    + $"({20 * Math.Log10(_postPeakSinceLastLog + 1e-10):F1} dBFS)"
                    + $"{(PostDecodeProcessor != null ? ", post-decode processing ON" : "")}"
                    + $"{(clipped ? " CLIPPED" : "")}", TraceLevel.Info);
                _peakLogCounter = 0;
                _peakSinceLastLog = 0f;
                _postPeakSinceLastLog = 0f;
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
