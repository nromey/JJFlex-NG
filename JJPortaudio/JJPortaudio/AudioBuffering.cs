using System;
using POpusCodec.Enums;

namespace JJPortaudio
{
    /// <summary>
    /// How much audio a stream holds before the callback runs, and what that
    /// costs in time and in bytes (#462).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the dominant latency term and it has never been chosen.</b>
    /// <c>Audio.Open</c> takes a <c>cbPerSec</c> parameter that defaults to
    /// <see cref="DefaultCallbacksPerSecond"/>, and until Sprint 43 no caller
    /// anywhere passed a value. Ten callbacks a second is a 100 ms device
    /// buffer wrapped around 10 ms Opus frames — an order of magnitude more
    /// delay than the codec contributes, sitting in a default argument.
    /// </para>
    /// <para>
    /// <b>Bigger is not simply safer, and smaller is not simply better.</b> A
    /// bigger device buffer tolerates more PC scheduling jitter before it
    /// underruns, and that is the whole of its advantage. It also makes every
    /// starvation LONGER and MORE likely on the playback side, because the
    /// output callback must find a whole buffer's worth of decoded audio queued
    /// at the instant it runs, and fills the remainder with silence if it does
    /// not. At 100 ms that is ten 10 ms packets that all have to have arrived;
    /// one late packet costs 100 ms of silence, not 10.
    /// </para>
    /// <para>
    /// The arithmetic here is deliberately the same float expression
    /// <c>Audio.Open</c> has always used, extracted rather than rewritten, so
    /// the extraction cannot change a buffer size by a rounding step.
    /// </para>
    /// </remarks>
    public static class AudioBuffering
    {
        /// <summary>
        /// The historical default: ten callbacks a second, a 100 ms buffer.
        /// </summary>
        public const int DefaultCallbacksPerSecond = 10;

        /// <summary>
        /// Bytes of VITA-49 header on every audio packet, header and trailer
        /// together. Read from <c>VitaOpusDataPacket</c>, which sizes its
        /// packet as <c>payload/4 + 7</c> 32-bit words.
        /// </summary>
        public const int VitaHeaderBytes = 28;

        /// <summary>
        /// Bytes of IPv4 and UDP header under it. Ethernet framing adds 18 more
        /// on a local segment and is deliberately not counted here — it does
        /// not survive the first router, and the interesting case is a domestic
        /// uplink.
        /// </summary>
        public const int IpUdpHeaderBytes = 28;

        /// <summary>Frame duration in milliseconds, for arithmetic and for prose.</summary>
        public static double FrameMilliseconds(Delay frameDuration)
        {
            // The enum's value is milliseconds doubled — see POpusCodec.Enums.Delay,
            // where Delay10ms is 20 — which is what makes 2.5 ms expressible.
            return (int)frameDuration / 2.0;
        }

        /// <summary>Opus packets a second at a given frame duration.</summary>
        public static double PacketsPerSecond(Delay frameDuration)
        {
            return 1000.0 / FrameMilliseconds(frameDuration);
        }

        /// <summary>
        /// Bits per second of packet HEADER at a given frame duration, before
        /// a single byte of audio. About 45 kbps at the shipped 10 ms, which is
        /// the number that makes frame duration a bandwidth lever and not only
        /// a latency one.
        /// </summary>
        public static double HeaderBitsPerSecond(Delay frameDuration)
        {
            return PacketsPerSecond(frameDuration) * (VitaHeaderBytes + IpUdpHeaderBytes) * 8;
        }

        /// <summary>
        /// Floats in one device buffer, on the interleaved-stereo path the
        /// callbacks and the queues work in.
        /// </summary>
        /// <remarks>
        /// This is byte-for-byte the expression <c>Audio.Open</c> used inline,
        /// float arithmetic included. Integer arithmetic would give a different
        /// answer whenever the packet rate does not divide the callback rate —
        /// 100 packets a second at 3 callbacks a second is 33.33 frames per
        /// buffer in float and 33 in integer — so the float form is preserved
        /// deliberately rather than tidied.
        /// </remarks>
        public static uint OpusBufferFloats(uint openRate, int frameSizePerChannel, int cbPerSec)
        {
            float framesPerCallback = (float)openRate / (float)frameSizePerChannel / cbPerSec;
            uint opusFrameFloats = (uint)frameSizePerChannel * (uint)Devices.StreamChannels;
            return (uint)(framesPerCallback * (float)opusFrameFloats);
        }

        /// <summary>Milliseconds of audio in a buffer of this many stereo-path floats.</summary>
        public static double BufferMilliseconds(uint bufferFloats, uint rate)
        {
            if (rate == 0) return 0;
            return (bufferFloats / (double)Devices.StreamChannels) * 1000.0 / rate;
        }

        /// <summary>
        /// True when a callback rate leaves at least one whole Opus frame in
        /// each buffer.
        /// </summary>
        /// <remarks>
        /// Below one frame per callback the buffer-size arithmetic truncates
        /// toward zero and the stream opens with a buffer of nothing, which is
        /// not a degraded audio path but an absent one. At the shipped 10 ms
        /// frame the ceiling is therefore 100 callbacks a second — a 10 ms
        /// buffer, which is also what WASAPI shared mode runs natively.
        /// </remarks>
        public static bool IsUsableCallbackRate(uint openRate, int frameSizePerChannel, int cbPerSec)
        {
            if (cbPerSec < 1 || frameSizePerChannel < 1 || openRate < 1) return false;
            return OpusBufferFloats(openRate, frameSizePerChannel, cbPerSec) >= (uint)frameSizePerChannel * 2;
        }
    }
}
