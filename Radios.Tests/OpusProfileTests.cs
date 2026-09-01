using System;
using JJPortaudio;
using POpusCodec.Enums;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Opus encode profile and the buffering arithmetic behind it
    /// (Sprint 43 Track J — #460, Opus stereo is our hardcode; #462, our own
    /// 100 ms buffer is the latency term).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these are tests and not comments.</b> The shipped Opus profile is
    /// load-bearing in a way that is invisible from the code: a long
    /// investigation established that our wire bytes are indistinguishable from
    /// a working client's, on three independent witnesses. Any change to the
    /// channel count, the bitrate, the encoder bandwidth or the frame duration
    /// changes those bytes, and would be very hard to attribute months later.
    /// A comment asking a future editor to keep them has the same fate every
    /// such comment in this codebase has had. A failing test does not.
    /// </para>
    /// <para>
    /// So these pin the defaults field by field. They are not asserting that
    /// the defaults are optimal — several are known not to be — only that
    /// changing one is a deliberate act with a test to update.
    /// </para>
    /// </remarks>
    public class OpusProfileTests
    {
        // ── The shipped profile, field by field ──

        [Fact]
        public void ShippedProfileIsStereo()
        {
            // The hardcode #460 names: JJPortaudio Audio.Open built
            // `new OpusEncoder(rate, Channels.Stereo)`. Mono is expected to
            // save far less than intuition suggests — the transmit signal is
            // exactly dual-mono, so Opus's mid/side coupling already spends
            // almost nothing on the side channel — and whether the radio
            // accepts a mono packet is a bench question, not a code one.
            Assert.Equal(Channels.Stereo, OpusEncodeProfile.Shipped.Channels);
        }

        [Fact]
        public void ShippedProfileIsSuperWideband()
        {
            // 12 kHz of audio bandwidth, which on transmit meets an SSB filter
            // at 2.7 to 3 kHz. Known to be wasteful on transmit and left alone
            // deliberately until it can be changed one knob at a time.
            Assert.Equal(Bandwidth.SuperWideband, OpusEncodeProfile.Shipped.MaxBandwidth);
        }

        [Fact]
        public void ShippedProfileUsesTenMillisecondFrames()
        {
            Assert.Equal(Delay.Delay10ms, OpusEncodeProfile.Shipped.FrameDuration);
        }

        [Fact]
        public void ShippedProfileSetsNoBitrateAtAll()
        {
            // Null means "never call the setter", which is the only way to say
            // "whatever libopus defaults to" honestly. Writing in a number we
            // merely believe to be the default would silently change the proven
            // bytes — see #460, where the ~70 kbps figure comes from libopus's
            // own choice and not from anything this application asked for.
            Assert.Null(OpusEncodeProfile.Shipped.Bitrate);
        }

        [Fact]
        public void NoShippedProfileUsesVoipApplicationMode()
        {
            // #460 explicitly: do NOT switch to Voip to save bits. It engages
            // the speech-optimised path, which mangles tones. Data modes need
            // low distortion rather than width — FT8 occupies about 50 Hz — so
            // the trade is voice efficiency against data-mode integrity, and
            // only one of those is recoverable by the operator afterwards.
            Assert.Equal(OpusApplicationType.Audio, OpusEncodeProfile.Shipped.Application);
            Assert.NotEqual(OpusApplicationType.Voip, OpusEncodeProfile.Shipped.Application);
        }

        [Fact]
        public void DescribeNamesEveryDecisionIncludingTheUnsetBitrate()
        {
            // The trace line is the record of which profile produced a
            // session's bytes. An omitted field there is a field nobody can
            // rule out afterwards.
            string d = OpusEncodeProfile.Shipped.Describe();
            Assert.Contains("Stereo", d);
            Assert.Contains("SuperWideband", d);
            Assert.Contains("Delay10ms", d);
            Assert.Contains("Audio", d);
            Assert.Contains("not set", d);
        }

        [Fact]
        public void ADeliberatelyLeanProfileKeepsEveryOtherDecisionExplicit()
        {
            // The plumbing's whole purpose: one knob at a time, each
            // independently measurable, with everything else provably
            // untouched.
            var lean = new OpusEncodeProfile { Bitrate = 24000 };
            Assert.Equal(24000, lean.Bitrate);
            Assert.Equal(OpusEncodeProfile.Shipped.Channels, lean.Channels);
            Assert.Equal(OpusEncodeProfile.Shipped.MaxBandwidth, lean.MaxBandwidth);
            Assert.Equal(OpusEncodeProfile.Shipped.FrameDuration, lean.FrameDuration);
            Assert.Equal(OpusEncodeProfile.Shipped.Application, lean.Application);
        }

        // ── The application's own defaults ──

        [Fact]
        public void FlexBaseTransmitsWithTheShippedProfile()
        {
            Assert.Same(OpusEncodeProfile.Shipped, FlexBase.OpusTxEncodeProfile);
        }

        [Fact]
        public void BothDirectionsDefaultToTenCallbacksPerSecond()
        {
            // #462: this was a default argument no caller had ever passed, and
            // it is the dominant latency term. Ten a second is a 100 ms buffer.
            Assert.Equal(10, FlexBase.TxAudioCallbacksPerSecond);
            Assert.Equal(10, FlexBase.RxAudioCallbacksPerSecond);
            Assert.Equal(10, AudioBuffering.DefaultCallbacksPerSecond);
        }

        [Fact]
        public void ANullProfileIsRefusedRatherThanStored()
        {
            var before = FlexBase.OpusTxEncodeProfile;
            try
            {
                FlexBase.OpusTxEncodeProfile = null;
                Assert.Same(before, FlexBase.OpusTxEncodeProfile);
            }
            finally
            {
                FlexBase.OpusTxEncodeProfile = before;
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(101)] // one whole 10 ms frame per callback is the ceiling
        public void ACallbackRateThatCannotHoldAWholeFrameIsRefused(int rate)
        {
            int before = FlexBase.TxAudioCallbacksPerSecond;
            try
            {
                FlexBase.TxAudioCallbacksPerSecond = rate;
                Assert.Equal(before, FlexBase.TxAudioCallbacksPerSecond);
            }
            finally
            {
                FlexBase.TxAudioCallbacksPerSecond = before;
            }
        }

        [Fact]
        public void AReachableCallbackRateIsAccepted()
        {
            // A positive control for the refusals above: the setter has to be
            // capable of accepting something, or the tests prove nothing.
            int before = FlexBase.RxAudioCallbacksPerSecond;
            try
            {
                FlexBase.RxAudioCallbacksPerSecond = 50;
                Assert.Equal(50, FlexBase.RxAudioCallbacksPerSecond);
            }
            finally
            {
                FlexBase.RxAudioCallbacksPerSecond = before;
            }
        }

        // ── The buffer arithmetic, which is the latency ──

        [Fact]
        public void TheShippedBufferIsNineThousandSixHundredFloatsAtFortyEightKilohertz()
        {
            // 48 kHz, 10 ms frames: 480 samples per channel, 960 interleaved
            // floats per frame, ten frames per callback. This is the exact
            // arithmetic Audio.Open has always run inline; pinning the number
            // is what makes any future change to it visible.
            uint floats = AudioBuffering.OpusBufferFloats(48000, 480, 10);
            Assert.Equal(9600u, floats);
            Assert.Equal(4800u, floats / 2); // frames, which is PortAudio's framesPerBuffer
        }

        [Fact]
        public void TheShippedBufferIsOneHundredMilliseconds()
        {
            Assert.Equal(100.0, AudioBuffering.BufferMilliseconds(9600, 48000), 3);
        }

        [Theory]
        [InlineData(48000u, 480)]
        [InlineData(24000u, 240)]
        [InlineData(16000u, 160)]
        [InlineData(12000u, 120)]
        [InlineData(8000u, 80)]
        public void EveryOpusRateGivesTheSameHundredMillisecondBufferAtTenCallbacks(uint rate, int frameSize)
        {
            // The buffer is a duration, not a size, so lowering the transmit
            // sample rate (#57, which shipped) does not change the latency at
            // all. Worth pinning: the two settings look related and are not.
            uint floats = AudioBuffering.OpusBufferFloats(rate, frameSize, 10);
            Assert.Equal(100.0, AudioBuffering.BufferMilliseconds(floats, rate), 3);
        }

        [Theory]
        [InlineData(10, 100.0)]
        [InlineData(20, 50.0)]
        [InlineData(50, 20.0)]
        [InlineData(100, 10.0)]
        public void RaisingTheCallbackRateLowersLatencyProportionally(int cbPerSec, double expectedMs)
        {
            uint floats = AudioBuffering.OpusBufferFloats(48000, 480, cbPerSec);
            Assert.Equal(expectedMs, AudioBuffering.BufferMilliseconds(floats, 48000), 3);
        }

        [Fact]
        public void OneHundredCallbacksIsTheCeilingAtTenMillisecondFrames()
        {
            Assert.True(AudioBuffering.IsUsableCallbackRate(48000, 480, 100));
            Assert.False(AudioBuffering.IsUsableCallbackRate(48000, 480, 101));
        }

        // ── Frame duration moves BOTH latency and bandwidth ──

        [Fact]
        public void TheDelayEnumIsMillisecondsDoubled()
        {
            // A wrong reading here would make every packet-rate and header
            // figure below wrong by a factor of two, silently.
            Assert.Equal(2.5, AudioBuffering.FrameMilliseconds(Delay.Delay2dot5ms), 3);
            Assert.Equal(10.0, AudioBuffering.FrameMilliseconds(Delay.Delay10ms), 3);
            Assert.Equal(60.0, AudioBuffering.FrameMilliseconds(Delay.Delay60ms), 3);
        }

        [Fact]
        public void TenMillisecondFramesCostAboutFortyFiveKilobitsOfHeaderPerSecond()
        {
            // 100 packets a second, each carrying 28 bytes of VITA header and
            // 28 of IP and UDP, before a single byte of audio. Beside a stream
            // whose payload is around 70 kbps that is roughly two fifths of the
            // traffic — which is why frame duration is a bandwidth lever and
            // not only a latency one, and why a low-bandwidth mode and a
            // low-latency mode can never be the same switch (#462).
            Assert.Equal(100.0, AudioBuffering.PacketsPerSecond(Delay.Delay10ms), 3);
            Assert.Equal(44800.0, AudioBuffering.HeaderBitsPerSecond(Delay.Delay10ms), 1);
        }

        [Fact]
        public void LongerFramesTradeLatencyForHeaderBytesAndShorterFramesTradeBack()
        {
            // The opposition stated as arithmetic rather than as prose, so it
            // cannot quietly stop being true.
            double tenMs = AudioBuffering.HeaderBitsPerSecond(Delay.Delay10ms);
            double twentyMs = AudioBuffering.HeaderBitsPerSecond(Delay.Delay20ms);
            double fiveMs = AudioBuffering.HeaderBitsPerSecond(Delay.Delay5ms);

            // Doubling the frame halves the header tax and adds 10 ms of delay.
            Assert.Equal(tenMs / 2, twentyMs, 1);
            Assert.True(AudioBuffering.FrameMilliseconds(Delay.Delay20ms)
                      > AudioBuffering.FrameMilliseconds(Delay.Delay10ms));

            // Halving it does the reverse: less delay, twice the header tax.
            Assert.Equal(tenMs * 2, fiveMs, 1);
            Assert.True(AudioBuffering.FrameMilliseconds(Delay.Delay5ms)
                      < AudioBuffering.FrameMilliseconds(Delay.Delay10ms));
        }

        // ── The mono fold, which is the whole of mono support ──

        [Fact]
        public void FoldingADualMonoFrameRecoversTheOriginalMonoSamples()
        {
            // The transmit signal is exactly dual-mono by construction — the
            // input callback duplicates a mono capture onto both channels — so
            // averaging the pair is lossless rather than approximate. That is
            // what makes folding at the encode step, instead of making the
            // whole transmit pipeline mono, both correct and cheap.
            float[] mono = { 0.25f, -0.5f, 0.75f, 0f };
            float[] stereo = new float[mono.Length * 2];
            for (int i = 0; i < mono.Length; i++)
            {
                stereo[i * 2] = mono[i];
                stereo[i * 2 + 1] = mono[i];
            }

            float[] folded = new float[mono.Length];
            OpusEncodeProfile.FoldStereoToMono(stereo, folded);

            Assert.Equal(mono, folded);
        }

        [Fact]
        public void FoldingAGenuineStereoFrameAveragesTheChannels()
        {
            // A positive control for the test above: if the fold simply took
            // the left channel, the dual-mono assertion would pass anyway and
            // prove nothing about what the function does.
            float[] stereo = { 1.0f, 0.0f, -0.5f, 0.5f };
            float[] folded = new float[2];
            OpusEncodeProfile.FoldStereoToMono(stereo, folded);
            Assert.Equal(0.5, (double)folded[0], 5);
            Assert.Equal(0.0, (double)folded[1], 5);
        }

        [Fact]
        public void ANullEncoderHasNoEncodeStep()
        {
            // The close path nulls the encoder to drop the pipeline's encode
            // delegate with it, so a frame arriving late finds nothing to
            // encode rather than a disposed encoder.
            Assert.Null(OpusEncodeProfile.BuildEncodeStep(null));
        }
    }
}
