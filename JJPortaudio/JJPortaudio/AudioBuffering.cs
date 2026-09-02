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
    /// starvation longer, because the callback fills whatever it could not find
    /// queued with silence, and a bigger buffer has more room to fill.
    /// </para>
    /// <para>
    /// <b>This paragraph said "one late packet costs 100 ms of silence, not 10"
    /// until 2026-09-02, and that was wrong.</b> It came from #473 and it does
    /// not survive reading the loop: <c>outputCallback</c> plays every queued
    /// packet that fits before it fills anything, so nine packets present out of
    /// ten costs ten milliseconds of silence. The claim survived because the
    /// meter counted EVENTS — one "silent fill" whether it filled 10 ms or 100 —
    /// so a 10 ms shortfall and a 100 ms shortfall were indistinguishable in
    /// every trace anybody read. The shortfall is measured now; see
    /// <see cref="RxPlaybackQueue"/>.
    /// </para>
    /// <para>
    /// <b>What IS true is that the callback cannot catch up.</b> PortAudio sizes
    /// the output buffer, so however far behind the queue falls, no callback can
    /// write more than one buffer's worth — the drain rate is capped at exactly
    /// the nominal rate and can never exceed it. Every millisecond of silence
    /// inserted is therefore permanent: nothing anywhere plays faster afterwards
    /// to reclaim it. That is the ratchet, and the only way to give any of it
    /// back is to discard queued audio.
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
        /// Environment variable that changes the RECEIVE callback rate for one
        /// launch, so the 100 ms buffer can be measured against 50 ms or 20 ms
        /// without a rebuild.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The default is not changing, and that is a finding rather than
        /// caution.</b> #473 argues the tradeoff is backwards — that a big
        /// buffer buys tolerance of PC scheduling jitter and pays in network
        /// jitter, and over SmartLink we have the latter. The argument is sound
        /// and the evidence for it does not exist: the six receive streams
        /// captured at the radio on 2026-09-01 measured PC-side device latency
        /// at 98.7 to 99.9 ms against a claimed 100.0, a spread of about one
        /// millisecond, and measured nothing at all about network jitter,
        /// because nothing on this side can see it. Halving the buffer on that
        /// basis would be a guess dressed as a fix, and the two people it would
        /// affect want opposite things: a wired LAN has no network jitter to
        /// pay, and a SmartLink link over a domestic uplink has little else.
        /// </para>
        /// <para>
        /// So it is a knob, it is one relaunch away, and every run says in its
        /// own trace which value it used. Values above
        /// <c>PacketsPerSecond(frameDuration)</c> are refused; below one whole
        /// Opus frame per callback the buffer arithmetic truncates to nothing.
        /// A testing lever, like <c>JJFLEX_CONFIG_DIR</c> — never a setting,
        /// never a UI toggle.
        /// </para>
        /// </remarks>
        public const string RxCallbackRateEnvironmentVariable = "JJFLEX_RX_CALLBACKS_PER_SEC";

        private static bool _rxRateResolved;
        private static int _rxRate = DefaultCallbacksPerSecond;

        /// <summary>
        /// The receive callback rate this launch is using: the shipped ten
        /// unless <c>JJFLEX_RX_CALLBACKS_PER_SEC</c> names something else. Read
        /// once, traced either way.
        /// </summary>
        public static int ConfiguredRxCallbacksPerSecond()
        {
            if (_rxRateResolved) return _rxRate;
            _rxRateResolved = true;
            string raw = null;
            try { raw = Environment.GetEnvironmentVariable(RxCallbackRateEnvironmentVariable); }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("AudioBuffering: could not read "
                    + RxCallbackRateEnvironmentVariable + ", using the shipped "
                    + DefaultCallbacksPerSecond + ": " + ex.Message,
                    System.Diagnostics.TraceLevel.Error);
                return _rxRate;
            }
            if (string.IsNullOrWhiteSpace(raw)) return _rxRate;

            if (int.TryParse(raw.Trim(), out int rate) && rate >= 1 && rate <= 100)
            {
                _rxRate = rate;
                JJTrace.Tracing.TraceLine("AudioBuffering: " + RxCallbackRateEnvironmentVariable
                    + " set the receive callback rate to " + rate + " a second for this launch — a "
                    + (1000.0 / rate).ToString("F0") + " ms device buffer, against the shipped "
                    + DefaultCallbacksPerSecond + " (100 ms)",
                    System.Diagnostics.TraceLevel.Error);
            }
            else
            {
                JJTrace.Tracing.TraceLine("AudioBuffering: " + RxCallbackRateEnvironmentVariable
                    + "=\"" + raw + "\" is not a whole number of callbacks a second between 1 and "
                    + "100; using the shipped " + DefaultCallbacksPerSecond,
                    System.Diagnostics.TraceLevel.Error);
            }
            return _rxRate;
        }

        /// <summary>Forget the cached environment read. Tests only.</summary>
        public static void ResetConfiguredRxCallbacksPerSecond()
        {
            _rxRateResolved = false;
            _rxRate = DefaultCallbacksPerSecond;
        }

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
