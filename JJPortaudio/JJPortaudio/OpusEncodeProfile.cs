using System;
using POpusCodec;
using POpusCodec.Enums;

namespace JJPortaudio
{
    /// <summary>
    /// Every Opus ENCODER decision in one place, so each can be chosen
    /// deliberately instead of arriving as a constructor default nobody named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Encoder only, and that asymmetry is real rather than an oversight
    /// (#460).</b> Channel count, bitrate, bandwidth and frame duration are
    /// properties of an ENCODE. On receive we do not encode — the radio does —
    /// and an Opus packet is self-describing, so a decoder derives all four
    /// from the bitstream and has no such settings to offer. The receive side's
    /// only levers are the decode sample rate and how much audio the playback
    /// device buffers; see <see cref="AudioBuffering"/>.
    /// </para>
    /// <para>
    /// <b>The shipped profile is the proven one and stays the default.</b> The
    /// honest-tx-audio arc established that our wire bytes are indistinguishable
    /// from a working client's; changing any of these changes those bytes.
    /// <see cref="Shipped"/> reproduces exactly what
    /// <c>Audio.Open</c> built before this type existed, and
    /// <c>OpusProfileTests</c> pins that field by field so a future edit to the
    /// defaults has to be deliberate.
    /// </para>
    /// <para>
    /// <b>Bandwidth and latency pull in opposite directions, and this type
    /// refuses to pretend otherwise.</b> <see cref="FrameDuration"/> is the one
    /// member that moves both: shorter frames mean lower latency AND more
    /// packets per second, and each packet costs 28 bytes of VITA header plus
    /// 28 of IP and UDP whatever it carries. At the shipped 10 ms that header
    /// tax is about 45 kbps — a large fraction of the stream, and the reason a
    /// "low bandwidth" control and a "low latency" control can never be the
    /// same switch.
    /// </para>
    /// </remarks>
    public sealed class OpusEncodeProfile
    {
        /// <summary>
        /// Channels the ENCODER is built with. Not the device's channel count
        /// and not the transmit pipeline's — both of those stay stereo. See
        /// <see cref="BuildEncodeStep"/>, which is the only place the two
        /// shapes meet.
        /// </summary>
        public Channels Channels { get; init; } = Channels.Stereo;

        /// <summary>
        /// The widest audio bandwidth the encoder may spend bits on.
        /// SuperWideband is 12 kHz of audio; a transmit path that ends in an
        /// SSB filter around 2.7 to 3 kHz cannot use most of it, which is why
        /// this is settable per direction rather than fixed.
        /// </summary>
        public Bandwidth MaxBandwidth { get; init; } = Bandwidth.SuperWideband;

        /// <summary>
        /// Frame duration. Sets the codec's algorithmic delay, the packet rate,
        /// and therefore the per-second header tax — see the class remarks.
        /// </summary>
        public Delay FrameDuration { get; init; } = Delay.Delay10ms;

        /// <summary>
        /// Bits per second, or null to leave libopus's own default in place.
        /// </summary>
        /// <remarks>
        /// <b>Null is not "no opinion", it is the shipped behaviour stated
        /// exactly.</b> Nothing in this application has ever set a bitrate, so
        /// the encoder runs at whatever libopus picks for the rate and channel
        /// count it was built with — around 70 kbps at 24 kHz stereo, which is
        /// where that figure in the register comes from. Naming a number here
        /// is the single largest bandwidth lever available, and setting it to
        /// what we merely BELIEVE the default to be would be a silent change of
        /// the proven bytes. So the default has to be "do not call the setter",
        /// which only null can express.
        /// </remarks>
        public int? Bitrate { get; init; }

        /// <summary>
        /// The encoder's application hint. <see cref="OpusApplicationType.Audio"/>,
        /// and deliberately not settable to Voip by any shipped profile.
        /// </summary>
        /// <remarks>
        /// <b>Do not switch this to Voip to save bits (#460).</b> Voip engages
        /// the speech-optimised path, which mangles tones. Data modes need low
        /// distortion rather than width — FT8 occupies about 50 Hz — so the
        /// trade is voice efficiency against data-mode integrity, and only one
        /// of those is recoverable by the operator once it is wrong.
        /// <c>OpusProfileTests</c> fails if a built-in profile carries Voip.
        /// </remarks>
        public OpusApplicationType Application { get; init; } = OpusApplicationType.Audio;

        /// <summary>
        /// Exactly what <c>Audio.Open</c> built before this type existed:
        /// stereo, SuperWideband, 10 ms frames, application Audio, and no
        /// bitrate set at all. The default for both directions.
        /// </summary>
        public static readonly OpusEncodeProfile Shipped = new OpusEncodeProfile();

        /// <summary>
        /// A one-line description for the trace, so a session's log says which
        /// profile produced its bytes without anyone having to infer it.
        /// </summary>
        public string Describe()
        {
            return Channels + ", " + MaxBandwidth + ", " + FrameDuration
                + ", " + Application
                + ", bitrate " + (Bitrate.HasValue
                    ? Bitrate.Value + " bps"
                    : "libopus default (not set)");
        }

        /// <summary>
        /// Build an encoder for this profile at a settled sample rate.
        /// </summary>
        /// <remarks>
        /// The bitrate setter is called ONLY when a bitrate was named, for the
        /// reason in <see cref="Bitrate"/>: the shipped path must not touch it.
        /// </remarks>
        public OpusEncoder CreateEncoder(SamplingRate rate)
        {
            var enc = new OpusEncoder(rate, Channels, Application, FrameDuration);
            enc.MaxBandwidth = MaxBandwidth;
            if (Bitrate.HasValue) enc.Bitrate = Bitrate.Value;
            return enc;
        }

        /// <summary>
        /// The function that turns one transmit frame into bytes, for an
        /// encoder of whatever channel count it was built with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the whole of mono support, and its placement is the
        /// point.</b> The transmit pipeline is interleaved stereo from end to
        /// end — the tone generator, the reference-file player, the noise gate,
        /// the conditioner and the LUFS meter every one of them step the buffer
        /// two floats at a time and document the dual-mono invariant they rely
        /// on. Making the CODEC mono by making the PIPELINE mono would silently
        /// break all five: the gate would read consecutive samples as an L/R
        /// pair, the meter would be 3 dB out, and a tone would come out at half
        /// frequency. Nothing would throw.
        /// </para>
        /// <para>
        /// So the fold happens here, at the last possible moment, and nothing
        /// upstream changes. Because the transmit signal is exactly dual-mono
        /// by construction, averaging the pair is lossless — it recovers the
        /// original mono capture rather than approximating it.
        /// </para>
        /// <para>
        /// The scratch buffer is allocated once per encoder, never per frame:
        /// this runs a hundred times a second on an audio callback thread.
        /// </para>
        /// </remarks>
        public static Func<float[], byte[]> BuildEncodeStep(OpusEncoder encoder)
        {
            if (encoder == null) return null;
            if (encoder.InputChannels == Channels.Stereo) return encoder.Encode;

            float[] folded = new float[encoder.FrameSizePerChannel];
            return src =>
            {
                // Guarded rather than trusted: a short buffer would otherwise
                // read past the end on an audio thread. The pipeline always
                // hands a full stereo frame, so this never fires in practice.
                if (src == null || src.Length < folded.Length * 2) return encoder.Encode(src);
                FoldStereoToMono(src, folded);
                return encoder.Encode(folded);
            };
        }

        /// <summary>
        /// Average each interleaved stereo pair of <paramref name="src"/> into
        /// <paramref name="destination"/>, filling it completely.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="BuildEncodeStep"/> so it can be tested
        /// without libopus loaded in the test host — the same reason
        /// <c>TxFramePipeline</c> takes its encode step as a delegate.
        /// Allocation-free: this runs a hundred times a second on an audio
        /// callback thread.
        /// </remarks>
        public static void FoldStereoToMono(float[] src, float[] destination)
        {
            int n = destination.Length;
            for (int i = 0, j = 0; i < n; i++, j += 2)
            {
                destination[i] = (src[j] + src[j + 1]) * 0.5f;
            }
        }
    }
}
