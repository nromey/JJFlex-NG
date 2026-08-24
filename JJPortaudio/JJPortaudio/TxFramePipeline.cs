using System;
using System.Diagnostics;
using JJTrace;
using POpusCodec;

namespace JJPortaudio
{
    /// <summary>
    /// Everything that happens to one transmit frame between "samples exist"
    /// and "bytes go to the radio": injection, conditioning, metering, Opus
    /// encode, send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a type and not four lines in a callback.</b> Two things
    /// now produce transmit frames — the PortAudio capture callback, and the
    /// self-clocked source that paces a synthesized tone on its own clock. The
    /// promise the whole test-tone design rests on is that an injected signal
    /// "rides the identical encode-and-send path the microphone does." While
    /// there was one producer, that promise was a comment. With two, a comment
    /// is worth nothing: the paths drift, and they drift SILENTLY, because a
    /// tone that skips the conditioner or the meter still sounds exactly like a
    /// tone.
    /// </para>
    /// <para>
    /// So the tail is written once, here, and both producers call it. The
    /// promise becomes a property of the code rather than a claim about it.
    /// </para>
    /// <para>
    /// <b>The order is load-bearing and is the order the capture callback has
    /// always used.</b> The source INJECTS (replacing the microphone, never
    /// mixing). The conditioner MODIFIES — unless the engaged source says to
    /// stand it down, which a calibrated tone does and a recorded voice does
    /// not. The meter OBSERVES, deliberately last, so it measures what is
    /// genuinely about to be encoded rather than what arrived.
    /// </para>
    /// <para>
    /// <b>Thread model.</b> One producer at a time, and the caller guarantees
    /// it. <see cref="Emit"/> is not re-entrant and must not be called from two
    /// threads at once: <see cref="POpusCodec.OpusEncoder"/> is stateful and
    /// interleaving two callers would corrupt the bitstream in a way the radio
    /// would render as noise rather than as an error. The transmit gate in
    /// FlexBase enforces the one-producer rule by stopping the capture stream
    /// before starting the self-clocked source and vice versa; PortAudio's stop
    /// waits for the callback to quiesce, so the handover has no overlap.
    /// </para>
    /// </remarks>
    public sealed class TxFramePipeline
    {
        /// <summary>
        /// What may stand in for the microphone. Null means the buffer passes
        /// through untouched.
        /// </summary>
        public ITxInputSource Source { get; set; }

        /// <summary>
        /// The transmit conditioning chain (noise reduction, gate). Skipped
        /// while an engaged source asks to bypass it.
        /// </summary>
        public TxAudioProcessorCallback Conditioner { get; set; }

        /// <summary>The loudness meter, fed last so it measures what ships.</summary>
        public LufsMeter Meter { get; set; }

        /// <summary>
        /// Encode one frame of interleaved stereo floats. In the app this is
        /// <see cref="POpusCodec.OpusEncoder.Encode(float[])"/>.
        /// </summary>
        /// <remarks>
        /// A delegate rather than the encoder itself, for one reason that
        /// matters: this class is the ONE definition of what a transmit frame
        /// goes through, shared by two producers, so it is the piece that most
        /// needs a test — and a test cannot construct a real
        /// <c>OpusEncoder</c> without libopus loaded in the test host. Taking
        /// the encode as a delegate makes the ordering, the bypass rule and the
        /// teardown guard all verifiable with a two-line fake. It also happens
        /// to be the truth about what the pipeline needs: a function from
        /// samples to bytes.
        /// <para>
        /// The delegate is still bound to a stateful encoder, so the
        /// one-producer-at-a-time rule in the class remarks stands.
        /// </para>
        /// </remarks>
        public Func<float[], byte[]> Encode { get; set; }

        /// <summary>Where encoded frames go. In the app, FlexBase.sendOpusInput.</summary>
        public Audio.OpusCallback Handler { get; set; }

        /// <summary>Frames encoded and sent since <see cref="ResetCounters"/>.</summary>
        public long FramesSent { get; private set; }

        /// <summary>
        /// Frames encoded but NOT sent because <see cref="StillRunning"/> went
        /// false. Counted rather than ignored: a teardown that regularly
        /// abandons frames is a different fault from one that never does.
        /// </summary>
        public long FramesAbandonedAtTeardown { get; private set; }

        /// <summary>Encode failures. Never zero silently — see <see cref="Emit"/>.</summary>
        public long EncodeFailures { get; private set; }

        /// <summary>Zero the counters. Call at the start of a transmission.</summary>
        public void ResetCounters()
        {
            FramesSent = 0;
            FramesAbandonedAtTeardown = 0;
            EncodeFailures = 0;
        }

        /// <summary>
        /// Run one frame all the way through and send it.
        /// </summary>
        /// <param name="buffer">
        /// Interleaved stereo float samples. Carries microphone capture on the
        /// capture path and silence on the self-clocked path; an engaged source
        /// overwrites it either way.
        /// </param>
        /// <param name="floatCount">Floats to process — frames times channels.</param>
        /// <param name="sampleRate">The rate the stream actually opened at.</param>
        /// <param name="stillRunning">
        /// Asked immediately before the send, never before the encode — the
        /// teardown guard the capture callback has always had as
        /// <c>if (!data.Active) break;</c> between encoding and sending. The
        /// gap it closes is small and real: a frame may be mid-encode when the
        /// producer is told to stop, and handing that frame to a channel being
        /// torn down is how a stop becomes an exception on an audio thread.
        /// <para>
        /// It is a PARAMETER rather than a property on purpose. Each producer
        /// has its own answer — the capture path asks whether the PortAudio
        /// stream is active, the self-clocked source asks whether its own
        /// thread is still meant to be running — and a single shared predicate
        /// would be wrong for whichever one did not set it. Callers pass a
        /// cached delegate; this runs a hundred times a second on an audio
        /// thread and must not allocate.
        /// </para>
        /// Null means always send.
        /// </param>
        /// <returns>
        /// True when a frame was handed to the handler. False means the frame
        /// was abandoned at teardown or the encode failed — in both cases the
        /// caller should stop, not retry.
        /// </returns>
        public bool Emit(float[] buffer, int floatCount, uint sampleRate, Func<bool> stillRunning = null)
        {
            // Injection. Every source is called, engaged or not: an idle one
            // still needs the buffer to stamp its stream-gap clock and run its
            // release ramp. See TxInputSourceMux.Process.
            Source?.Process(buffer, floatCount, sampleRate);

            // Conditioning, unless the engaged source is a calibrated
            // reference. The question is the SOURCE's to answer — a tone says
            // bypass because -10 dBFS in must read -10 dBFS on the radio's
            // meter, a recorded voice says don't because the whole point is to
            // measure the chain a voice really travels.
            if (Conditioner != null
                && (Source == null || !Source.Engaged || !Source.BypassesConditioning))
            {
                Conditioner(buffer, floatCount, sampleRate);
            }

            // Metering last, so it reads what the encoder is about to receive.
            Meter?.Process(buffer, floatCount, sampleRate);

            var encode = Encode;
            if (encode == null)
            {
                EncodeFailures++;
                return false;
            }

            byte[] encoded;
            try
            {
                encoded = encode(buffer);
            }
            catch (Exception ex)
            {
                // Counted AND traced on the first one only. An encoder that
                // throws throws every frame, and a hundred lines a second is
                // the trace flood that has cost this project two debugging
                // sessions already.
                EncodeFailures++;
                if (EncodeFailures == 1)
                {
                    Tracing.TraceLine("TxFramePipeline: Opus encode failed, " + ex.Message
                        + " — transmit audio stops here; further failures counted, not logged",
                        TraceLevel.Error);
                }
                return false;
            }

            if (stillRunning != null && !stillRunning())
            {
                FramesAbandonedAtTeardown++;
                return false;
            }

            Handler?.Invoke(encoded);
            FramesSent++;
            return true;
        }
    }
}
