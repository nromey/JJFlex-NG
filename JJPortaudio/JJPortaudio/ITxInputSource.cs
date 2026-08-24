using System;

namespace JJPortaudio
{
    /// <summary>
    /// Something that can stand in for the microphone at the transmit
    /// injection point (Sprint 33 Track I).
    /// </summary>
    /// <remarks>
    /// <para>
    /// There has only ever been one thing here — <see cref="TxToneGenerator"/>,
    /// wired into the PortAudio input callback as a concrete type. The slot was
    /// always more general than its one occupant: it is the place where samples
    /// REPLACE the microphone, ahead of conditioning, metering and the Opus
    /// encoder, so whatever sits here rides the identical encode-and-send path
    /// a voice does. A test tone was the first thing to need that. A recorded
    /// voice file is the second, and a station message will be the third.
    /// </para>
    /// <para>
    /// Naming it as an interface is the whole change: the callback keeps the
    /// same single call site, the same replacement contract, and the same
    /// ordering guarantees, and it stops being a tone-shaped hole.
    /// </para>
    /// <para>
    /// Every implementation runs on the PortAudio callback thread. It must not
    /// allocate per buffer, must not lock, and must return promptly.
    /// </para>
    /// </remarks>
    public interface ITxInputSource
    {
        /// <summary>
        /// True while this source is producing samples, or ramping in or out
        /// of doing so. False means the microphone passes untouched.
        /// </summary>
        /// <remarks>
        /// Read once per buffer from the audio thread; implementations keep it
        /// to a single volatile field read.
        /// </remarks>
        bool Engaged { get; }

        /// <summary>
        /// True when this source has nothing in flight at all and the
        /// microphone may take the slot back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is NOT the negation of <see cref="Engaged"/>, and the gap
        /// between them is the whole reason it exists. <c>Engaged</c> means
        /// "the microphone is muted"; it goes false the instant a release is
        /// requested, while the source is still ramping its own signal down.
        /// Those last ten milliseconds are exactly the ones that must not be
        /// cut off — cutting them is the click the ramps were written to
        /// prevent.
        /// </para>
        /// <para>
        /// <b>Why it became necessary on 2026-08-24.</b> While every transmit
        /// frame came from the capture device, nothing ever had to ask: the
        /// stream ran whether a source was engaged or not, so a release ramp
        /// always got its buffers for free. A self-clocked source is started
        /// and stopped around the source it carries, so something has to know
        /// when the handover back to the microphone is safe. This is that
        /// question, asked of the only thing that knows the answer.
        /// </para>
        /// </remarks>
        bool Idle { get; }

        /// <summary>
        /// True when the transmit conditioning chain (noise reduction and the
        /// gate) should stand aside while this source is engaged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the honest generalisation of a special case that used to be
        /// hardcoded in the callback: conditioning was skipped whenever a tone
        /// was engaged, because the tone is a CALIBRATED REFERENCE — −10 dBFS
        /// in must read −10 dBFS on the radio's mic meter — and a gate or a
        /// noise reducer quietly shaping a synthesized sine breaks that
        /// property. There is also nothing in a sine for a speech-trained
        /// noise reducer to clean.
        /// </para>
        /// <para>
        /// A recorded VOICE is the opposite case and must return false. The
        /// entire point of playing a known voice down the transmit path is to
        /// measure the chain the operator's voice actually travels; bypassing
        /// conditioning would measure a different chain and answer a question
        /// nobody asked.
        /// </para>
        /// </remarks>
        bool BypassesConditioning { get; }

        /// <summary>
        /// Fill (or leave alone) one buffer of interleaved stereo float
        /// samples, in place.
        /// </summary>
        /// <param name="buffer">
        /// The buffer holding the microphone samples for this frame. An
        /// engaged source OVERWRITES them; it never mixes. Replacement is the
        /// design contract — a reference signal mixed with a live microphone
        /// is not a reference signal, and room bleed under a known file makes
        /// the file unknown.
        /// </param>
        /// <param name="count">Floats to process (frames times channels).</param>
        /// <param name="sampleRate">The rate the stream actually opened at.</param>
        void Process(float[] buffer, int count, uint sampleRate);
    }
}
