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
        /// The receive tail every decoded buffer passes through, real packet,
        /// concealed frame or FEC-recovered frame alike (#1155). The three
        /// properties below are forwards to it, so their callers are
        /// unchanged.
        /// </summary>
        private readonly RxOpusChain _rx;

        public JJAudioStream()
        {
            _rx = new RxOpusChain(buf => aud.TheQ.Enqueue(buf), () => SampleRate);
        }

        /// <summary>
        /// Output gain scalar applied to decoded audio samples.
        /// 1.0 = unity (no change), 2.0 = +6dB boost, etc.
        /// Default is 1.0 (no gain applied).
        /// </summary>
        public float OutputGain
        {
            get { return _rx.OutputGain; }
            set { _rx.OutputGain = value; }
        }

        /// <summary>
        /// Optional audio processing delegate invoked on decoded PCM samples
        /// after gain scaling, before PortAudio enqueue. Used by the PC-side
        /// noise reduction pipeline (RNNoise, spectral subtraction).
        /// The delegate receives the float[] buffer to process in-place.
        /// Called from the remoteAudioProc thread.
        /// </summary>
        public Action<float[]>? PostDecodeProcessor
        {
            get { return _rx.PostDecodeProcessor; }
            set { _rx.PostDecodeProcessor = value; }
        }

        /// <summary>Observer of the final receive samples; it must return without blocking.</summary>
        public Action<float[]>? PostOutputObserver
        {
            get { return _rx.PostOutputObserver; }
            set { _rx.PostOutputObserver = value; }
        }

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
        /// <summary>The instance pipeline shared by capture and synthesized input.</summary>
        public TxFramePipeline TransmitPipeline => aud?.TransmitPipeline;

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
        public double InputDrainMilliseconds => aud?.InputDrainMilliseconds ?? 0;
        /// <summary>Callbacks so far and the input overflows and underflows PortAudio flagged among them (Sprint 49 Track H, item D's dropout check).</summary>
        public (long Callbacks, long Overflows, long Underflows) InputDropouts => aud?.InputDropouts ?? (0, 0, 0);
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
            // with a line of the form "N starvation(s) in the final partial
            // second". Across the five streams whose totals were logged that
            // evening it was 4 of 6, 4 of 9, 4 of 8, 2 of 8 and 5 of 7 —
            // between a quarter and two thirds of every stream's starvation
            // count, manufactured by its own teardown. Nothing was audible; the
            // meter was reporting the shutdown.
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
        /// Decode one receive packet and play it, through the shared receive
        /// tail (<see cref="RxOpusChain"/>): gain, clamp, processing, the heard
        /// tap, the meters, the queue.
        /// </summary>
        /// <param name="data">one Opus packet from the radio</param>
        public void WriteOpus(byte[] data)
        {
            _rx.WritePacket(aud.Decoder, data);
        }

        /// <summary>
        /// Fill a short gap of lost receive frames that ended at
        /// <paramref name="next"/>, before the caller plays <paramref name="next"/>
        /// itself (#1155). See <see cref="RxOpusChain.FillGap"/>.
        /// </summary>
        public (int Concealed, int Recovered) FillGap(byte[] next, int missingFrames,
            Action<int>? beforeEachFrame = null)
        {
            return _rx.FillGap(aud.Decoder, next, missingFrames, beforeEachFrame);
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
