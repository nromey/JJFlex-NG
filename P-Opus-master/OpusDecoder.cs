using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using POpusCodec.Enums;

namespace POpusCodec
{
    public class OpusDecoder : IDisposable
    {
        private IntPtr _handle = IntPtr.Zero;
        private string _version = string.Empty;
        private const int MaxFrameSize = 5760;
        private int _channelCount = 2;
        private readonly int _sampleRate;

        public string Version
        {
            get
            {
                return _version;
            }
        }

        private Bandwidth? _previousPacketBandwidth = null;

        public Bandwidth? PreviousPacketBandwidth
        {
            get
            {
                return _previousPacketBandwidth;
            }
        }

        /// <summary>
        /// The rate this decoder was built at, in hertz. Every exact-length
        /// call below counts samples at THIS rate, never at an assumed 48 kHz:
        /// the receive decoder is built at the rate the device negotiated.
        /// </summary>
        public int SampleRate
        {
            get { return _sampleRate; }
        }

        /// <summary>Channels the decoder emits, interleaved.</summary>
        public int ChannelCount
        {
            get { return _channelCount; }
        }

        public OpusDecoder(SamplingRate outputSamplingRateHz, Channels numChannels)
        {
            if ((outputSamplingRateHz != SamplingRate.Sampling08000)
                && (outputSamplingRateHz != SamplingRate.Sampling12000)
                && (outputSamplingRateHz != SamplingRate.Sampling16000)
                && (outputSamplingRateHz != SamplingRate.Sampling24000)
                && (outputSamplingRateHz != SamplingRate.Sampling48000))
            {
                throw new ArgumentOutOfRangeException("outputSamplingRateHz", "Must use one of the pre-defined sampling rates");
            }
            if ((numChannels != Channels.Mono)
                && (numChannels != Channels.Stereo))
            {
                throw new ArgumentOutOfRangeException("numChannels", "Must be Mono or Stereo");
            }

            _channelCount = (int)numChannels;
            _sampleRate = (int)outputSamplingRateHz;
            _handle = Wrapper.opus_decoder_create(outputSamplingRateHz, numChannels);
            _version = Wrapper.opus_get_version();

            if (_handle == IntPtr.Zero)
            {
                throw new OpusException(OpusStatusCode.AllocFail, "Memory was not allocated for the encoder");
            }
        }

        // ── #1155, 2026-11-18: what was here, and why it went ──
        //
        // DecodePacketLost and DecodePacketLostFloat concealed a lost packet,
        // and DecodePacket/DecodePacketFloat decoded the next one with FEC
        // whenever opus_packet_get_bandwidth called the previous packet
        // invalid. None of it could ever run:
        //   - the two Lost methods had no callers anywhere in the tree;
        //   - libopus's opus_packet_get_bandwidth reads only the packet's
        //     first byte, and every one of its three branches returns a
        //     bandwidth, so it never returns OPUS_INVALID_PACKET and the FEC
        //     branch was unreachable (RxGapConcealmentTests pins this for all
        //     256 first bytes).
        // And had it run, it would have been wrong twice over: concealment
        // passed frame_size 5760, which makes libopus invent 120 ms of audio
        // for a 10 ms loss, and decoding the next packet with decode_fec = 1
        // returns the LOST frame's redundancy in place of the packet's own
        // audio. The exact-length calls below replace it; the receive loop
        // decides when a frame is missing, from the radio's packet count.

        /// <summary>Decode one packet. A null or empty packet decodes to nothing.</summary>
        public short[] DecodePacket(byte[] packetData)
        {
            if (packetData == null || packetData.Length == 0)
                return new short[] { };

            short[] tempData = new short[MaxFrameSize * _channelCount];
            NoteBandwidth(packetData);
            int numSamplesDecoded = Wrapper.opus_decode(_handle, packetData, tempData, 0, _channelCount);

            if (numSamplesDecoded == 0)
                return new short[] { };

            short[] pcm = new short[numSamplesDecoded * _channelCount];
            Buffer.BlockCopy(tempData, 0, pcm, 0, pcm.Length * sizeof(short));

            return pcm;
        }

        /// <summary>
        /// Decode one packet. A null or empty packet decodes to nothing: libopus
        /// would read either as "this packet was lost" and conceal a full
        /// 5760-sample buffer, 120 ms at 48 kHz.
        /// </summary>
        public float[] DecodePacketFloat(byte[] packetData)
        {
            if (packetData == null || packetData.Length == 0)
                return new float[] { };

            float[] tempData = new float[MaxFrameSize * _channelCount];
            NoteBandwidth(packetData);
            int numSamplesDecoded = Wrapper.opus_decode(_handle, packetData, tempData, 0, _channelCount);

            if (numSamplesDecoded == 0)
                return new float[] { };

            float[] pcm = new float[numSamplesDecoded * _channelCount];
            Buffer.BlockCopy(tempData, 0, pcm, 0, pcm.Length * sizeof(float));

            return pcm;
        }

        private void NoteBandwidth(byte[] packetData)
        {
            int bandwidth = Wrapper.opus_packet_get_bandwidth(packetData);
            if (bandwidth >= 0) _previousPacketBandwidth = (Bandwidth)bandwidth;
        }

        /// <summary>
        /// The samples per channel this packet decodes to at THIS decoder's
        /// rate, read from the packet itself (its TOC byte and frame count).
        /// Zero or less when the packet is malformed.
        /// </summary>
        public int SamplesPerChannel(byte[] packet)
        {
            return PacketSamples(packet, _sampleRate);
        }

        /// <summary>
        /// Samples per channel a packet decodes to at <paramref name="sampleRate"/>,
        /// or libopus's negative error code for a malformed packet.
        /// </summary>
        public static int PacketSamples(byte[] packet, int sampleRate)
        {
            if (packet == null || packet.Length == 0) return (int)OpusStatusCode.BadArguments;
            return Wrapper.opus_packet_get_nb_samples(packet, packet.Length, sampleRate);
        }

        /// <summary>
        /// True when the packet carries Opus in-band FEC (LBRR) for the frame
        /// before it, so a single lost frame can be recovered from it. Only
        /// SILK and hybrid packets can; a CELT-only packet never does.
        /// </summary>
        public static bool HasLbrr(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return false;
            return Wrapper.opus_packet_has_lbrr(packet, packet.Length) == 1;
        }

        /// <summary>
        /// libopus's answer for the packet's bandwidth: one of the
        /// <see cref="Bandwidth"/> values, or a negative error code. Exposed so
        /// a test can pin what this function really returns.
        /// </summary>
        public static int PacketBandwidth(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return (int)OpusStatusCode.BadArguments;
            return Wrapper.opus_packet_get_bandwidth(packet);
        }

        /// <summary>
        /// Opus packet-loss concealment for EXACTLY
        /// <paramref name="samplesPerChannel"/> missing samples per channel, at
        /// this decoder's rate. Interleaved; length is samplesPerChannel times
        /// the channel count, or empty if libopus produced nothing.
        /// </summary>
        public float[] ConcealFloat(int samplesPerChannel)
        {
            if (samplesPerChannel <= 0) return new float[] { };
            float[] pcm = new float[samplesPerChannel * _channelCount];
            int decoded = Wrapper.opus_decode(_handle, null, pcm, 0, _channelCount);
            return Trim(pcm, decoded);
        }

        /// <summary>
        /// Recover ONE lost frame of <paramref name="samplesPerChannel"/>
        /// samples per channel from the in-band redundancy of
        /// <paramref name="next"/>, the packet after the loss. The caller still
        /// decodes <paramref name="next"/> normally afterwards for its own
        /// audio. Without redundancy in it, libopus conceals instead.
        /// </summary>
        public float[] RecoverFloatFromNext(byte[] next, int samplesPerChannel)
        {
            if (samplesPerChannel <= 0 || next == null || next.Length == 0) return new float[] { };
            float[] pcm = new float[samplesPerChannel * _channelCount];
            int decoded = Wrapper.opus_decode(_handle, next, pcm, 1, _channelCount);
            return Trim(pcm, decoded);
        }

        private float[] Trim(float[] pcm, int samplesPerChannelDecoded)
        {
            if (samplesPerChannelDecoded <= 0) return new float[] { };
            int length = samplesPerChannelDecoded * _channelCount;
            if (length == pcm.Length) return pcm;
            float[] trimmed = new float[length];
            Buffer.BlockCopy(pcm, 0, trimmed, 0, length * sizeof(float));
            return trimmed;
        }

        private bool disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (_handle != IntPtr.Zero)
            {
                Wrapper.opus_decoder_destroy(_handle);
                _handle = IntPtr.Zero;
            }
            disposed = true;
        }

        ~OpusDecoder()
        {
            Dispose(false);
        }
    }
}
