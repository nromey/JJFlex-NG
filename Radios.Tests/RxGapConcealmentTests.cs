using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JJPortaudio;
using POpusCodec;
using POpusCodec.Enums;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1155: a lost receive packet used to be SPLICED (the next packet decoded
    /// straight after the last, a click at the join), and the decoder's
    /// concealment and FEC branch could never run. These pin the finding, the
    /// exact-length concealment that replaced it, the gap arithmetic shared
    /// with the recorder, and the recorder's heard mode.
    /// <para>Everything here runs the real libopus through
    /// <see cref="OpusDecoder"/> and <see cref="OpusEncoder"/>, and the real
    /// receive tail <see cref="RxOpusChain"/> with a list for its queue. No
    /// audio device, no radio, no window, and nothing is heard.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RxGapConcealmentTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RxGapConcealmentTests));
        public void Dispose() => _scope.Dispose();

        public RxGapConcealmentTests() => RecordedQsoOggTests.OpusLibraryReady();

        private const int Rate = 48000;
        private const int FrameSamples = 480; // 10 ms per channel at 48 kHz

        // ── The finding ──

        /// <summary>
        /// libopus's opus_packet_get_bandwidth never says "invalid packet", for
        /// any first byte. That is why the old FEC branch was unreachable.
        /// </summary>
        [Fact]
        public void Opus_packet_get_bandwidth_never_reports_an_invalid_packet()
        {
            for (int b = 0; b < 256; b++)
            {
                int bw = OpusDecoder.PacketBandwidth(new byte[] { (byte)b, 0, 0, 0 });
                Assert.NotEqual((int)OpusStatusCode.InvalidPacket, bw);
                Assert.InRange(bw, (int)Bandwidth.Narrowband, (int)Bandwidth.Fullband);
            }
        }

        [Fact]
        public void The_unreachable_loss_methods_are_gone()
        {
            Assert.Null(typeof(OpusDecoder).GetMethod("DecodePacketLost"));
            Assert.Null(typeof(OpusDecoder).GetMethod("DecodePacketLostFloat"));
            Assert.Null(typeof(OpusDecoder).GetField("_previousPacketInvalid",
                BindingFlags.Instance | BindingFlags.NonPublic));
        }

        /// <summary>
        /// An empty packet is "lost" to libopus, which would conceal a whole
        /// 5760-sample buffer (120 ms). The wrapper now returns nothing instead.
        /// </summary>
        [Fact]
        public void An_empty_packet_decodes_to_nothing_not_to_120_ms()
        {
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            Assert.Empty(dec.DecodePacketFloat(new byte[0]));
            Assert.Empty(dec.DecodePacket(new byte[0]));
        }

        // ── Encoding a test signal ──

        private static float[] Sine(int frames, double hz, float amplitude, int channels = 2)
        {
            var pcm = new float[frames * FrameSamples * channels];
            for (int n = 0; n < frames * FrameSamples; n++)
            {
                float s = (float)(amplitude * Math.Sin(2 * Math.PI * hz * n / Rate));
                for (int c = 0; c < channels; c++) pcm[n * channels + c] = s;
            }
            return pcm;
        }

        private static List<byte[]> Encode(float[] pcm, Action<OpusEncoder>? configure = null,
            OpusApplicationType app = OpusApplicationType.Audio)
        {
            using var enc = new OpusEncoder(SamplingRate.Sampling48000, Channels.Stereo, app, Delay.Delay10ms);
            configure?.Invoke(enc);
            var packets = new List<byte[]>();
            int frame = FrameSamples * 2;
            for (int i = 0; i + frame <= pcm.Length; i += frame)
            {
                var chunk = new float[frame];
                Array.Copy(pcm, i, chunk, 0, frame);
                packets.Add(enc.Encode(chunk));
            }
            return packets;
        }

        /// <summary>Play the packets through the real receive tail, losing <paramref name="lost"/>.</summary>
        private static float[] Receive(List<byte[]> packets, int lost, bool conceal, out (int Concealed, int Recovered) filled)
        {
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            var played = new List<float[]>();
            var chain = new RxOpusChain(played.Add, () => Rate);
            filled = (0, 0);
            for (int i = 0; i < packets.Count; i++)
            {
                if (i == lost) continue;
                if (conceal && i == lost + 1) filled = chain.FillGap(dec, packets[i], 1);
                chain.WritePacket(dec, packets[i]);
            }
            return played.SelectMany(b => b).ToArray();
        }

        /// <summary>
        /// The largest second difference (curvature) on the left channel. The
        /// first difference does not see a splice: the codec's overlap-add
        /// crossfades the two frames, so the join never steps harder than the
        /// sine itself does. The second difference does see it.
        /// </summary>
        private static double MaxCurvature(float[] interleaved, int from)
        {
            double max = 0;
            for (int i = from + 4; i < interleaved.Length; i += 2)
                max = Math.Max(max, Math.Abs(interleaved[i] - 2 * interleaved[i - 2] + interleaved[i - 4]));
            return max;
        }

        [Fact]
        public void A_concealed_gap_plays_exactly_as_long_as_the_audio_that_was_sent()
        {
            var packets = Encode(Sine(30, 1234, 0.5f));
            Assert.All(packets, p => Assert.NotEmpty(p));
            var whole = Receive(packets, lost: -1, conceal: false, out _);
            var spliced = Receive(packets, lost: 12, conceal: false, out _);
            var concealed = Receive(packets, lost: 12, conceal: true, out var filled);

            Assert.Equal(30 * FrameSamples * 2, whole.Length);
            // Today's splice was short by exactly the lost frame.
            Assert.Equal(whole.Length - FrameSamples * 2, spliced.Length);
            // Concealment makes up exactly what was lost: not 120 ms, not nothing.
            Assert.Equal(whole.Length, concealed.Length);
            Assert.Equal(1, filled.Concealed + filled.Recovered);
        }

        /// <summary>
        /// Continuity at the loss. Measured on a low tone (137 Hz), where the
        /// splice bends the waveform measurably harder than the tone itself
        /// ever does. Measured 2026-11-18 on the shipped libopus: splice about
        /// 2.8 times the steady curvature, concealment equal to steady. At
        /// higher tones (1234 Hz, 3 kHz) neither the splice nor concealment
        /// stands out from the tone on this measure, so they are not asserted.
        /// </summary>
        [Fact]
        public void Concealment_bends_the_waveform_less_at_the_loss_than_the_splice_did()
        {
            var packets = Encode(Sine(30, 137, 0.5f));
            var whole = Receive(packets, -1, false, out _);
            var spliced = Receive(packets, 12, false, out _);
            var concealed = Receive(packets, 12, true, out _);
            int skip = 4 * FrameSamples * 2; // past the codec's start-up
            double steady = MaxCurvature(whole, skip);
            double splice = MaxCurvature(spliced, skip);
            double conceal = MaxCurvature(concealed, skip);
            // Positive control: the splice really does bend harder than the signal.
            Assert.True(splice > steady * 1.5,
                "the splice did not show (steady " + steady + ", splice " + splice + "); this test proves nothing");
            Assert.True(conceal < splice,
                "concealment bent " + conceal + " at the loss, the splice " + splice);
        }

        [Fact]
        public void FEC_recovers_a_lost_frame_better_than_concealment_when_the_next_packet_carries_it()
        {
            // A voiced signal, low bitrate, FEC on and loss expected: settings
            // that push libopus into SILK, where in-band FEC exists. Whether
            // they really do is NOT assumed: HasLbrr is the positive control.
            var rnd = new Random(1155);
            var pcm = new float[40 * FrameSamples * 2];
            for (int n = 0; n < 40 * FrameSamples; n++)
            {
                double t = (double)n / Rate;
                float s = (float)(0.3 * Math.Sin(2 * Math.PI * 180 * t) + 0.15 * Math.Sin(2 * Math.PI * 360 * t)
                    + 0.08 * Math.Sin(2 * Math.PI * 540 * t) + 0.01 * (rnd.NextDouble() - 0.5));
                pcm[2 * n] = pcm[2 * n + 1] = s;
            }
            var packets = Encode(pcm, enc =>
            {
                enc.SignalHint = SignalHint.Voice;
                enc.Bitrate = 24000;
                enc.MaxBandwidth = Bandwidth.Wideband;
                enc.UseInbandFEC = true;
                enc.ExpectedPacketLossPercentage = 20;
            }, OpusApplicationType.Voip);
            const int lost = 25;
            Assert.True(OpusDecoder.HasLbrr(packets[lost + 1]),
                "the packet after the loss carries no LBRR, so this test cannot compare FEC with concealment");

            float[] reference;
            using (var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo))
            {
                for (int i = 0; i < lost; i++) dec.DecodePacketFloat(packets[i]);
                reference = dec.DecodePacketFloat(packets[lost]);
            }
            float[] Recover(bool fec)
            {
                using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
                for (int i = 0; i < lost; i++) dec.DecodePacketFloat(packets[i]);
                int spc = dec.SamplesPerChannel(packets[lost + 1]);
                Assert.Equal(FrameSamples, spc);
                return fec ? dec.RecoverFloatFromNext(packets[lost + 1], spc) : dec.ConcealFloat(spc);
            }
            double Error(float[] got) => got.Zip(reference, (a, b) => (double)(a - b) * (a - b)).Sum();
            var recovered = Recover(true);
            var concealed = Recover(false);
            Assert.Equal(reference.Length, recovered.Length);
            Assert.Equal(reference.Length, concealed.Length);
            Assert.True(Error(recovered) < Error(concealed),
                "FEC error " + Error(recovered) + " was not below concealment's " + Error(concealed));

            // And the chain uses it: one frame, recovered rather than concealed.
            using var chainDec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            var chain = new RxOpusChain(_ => { }, () => Rate);
            for (int i = 0; i < lost; i++) chain.WritePacket(chainDec, packets[i]);
            var filled = chain.FillGap(chainDec, packets[lost + 1], 1);
            Assert.Equal((0, 1), filled);
        }

        [Fact]
        public void A_gap_of_k_frames_conceals_k_minus_one_and_takes_the_last_from_FEC_only_if_present()
        {
            var packets = Encode(Sine(20, 1234, 0.5f)); // CELT: no LBRR
            Assert.False(OpusDecoder.HasLbrr(packets[10]));
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            var played = new List<float[]>();
            var chain = new RxOpusChain(played.Add, () => Rate);
            for (int i = 0; i < 7; i++) chain.WritePacket(dec, packets[i]);
            var stamps = new List<int>();
            var filled = chain.FillGap(dec, packets[10], 3, stamps.Add);
            Assert.Equal((3, 0), filled);
            Assert.Equal(new[] { 1, 2, 3 }, stamps);
            Assert.All(played.Skip(7), b => Assert.Equal(FrameSamples * 2, b.Length));
        }

        [Fact]
        public void The_tail_runs_for_concealed_frames_exactly_as_for_real_packets()
        {
            var packets = Encode(Sine(10, 1234, 0.5f));
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            var played = new List<float[]>();
            int processed = 0, observed = 0;
            var chain = new RxOpusChain(played.Add, () => Rate)
            {
                OutputGain = 4f,
                PostDecodeProcessor = _ => processed++,
                PostOutputObserver = _ => observed++,
            };
            chain.WritePacket(dec, packets[0]);
            chain.FillGap(dec, packets[3], 2);
            chain.WritePacket(dec, packets[3]);
            Assert.Equal(4, played.Count);
            Assert.Equal(4, processed);
            Assert.Equal(4, observed);
            Assert.Equal(4, chain.BuffersEmitted);
            Assert.All(played, b => Assert.All(b, s => Assert.InRange(s, -1f, 1f)));
        }

        [Fact]
        public void A_receive_decoder_counts_at_its_own_rate_not_an_assumed_48_kHz()
        {
            var packets = Encode(Sine(3, 440, 0.3f));
            using var dec24 = new OpusDecoder(SamplingRate.Sampling24000, Channels.Stereo);
            Assert.Equal(24000, dec24.SampleRate);
            Assert.Equal(240, dec24.SamplesPerChannel(packets[1]));
            Assert.Equal(240 * 2, dec24.ConcealFloat(240).Length);
            Assert.Equal(480, OpusDecoder.PacketSamples(packets[1], 48000));
        }

        [Fact]
        public void A_packet_whose_length_cannot_be_read_conceals_nothing()
        {
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            var chain = new RxOpusChain(_ => throw new Exception("nothing may be played"), () => Rate);
            Assert.Equal((0, 0), chain.FillGap(dec, new byte[0], 2));
            Assert.Equal((0, 0), chain.FillGap(dec, new byte[] { 0xFF }, 2)); // code 3 with no count byte
        }

        // ── The gap arithmetic ──

        [Theory]
        [InlineData(5, 6, 0)]
        [InlineData(15, 0, 0)]   // the counter wraps
        [InlineData(14, 1, 2)]
        [InlineData(3, 7, 3)]
        [InlineData(9, 9, 15)]   // a duplicate reads as 15
        [InlineData(2, 1, 14)]   // reordered: an earlier packet reads as a long gap
        public void The_counter_arithmetic(int previous, int sequence, int expected)
        {
            Assert.Equal(expected, ReceiveGap.MissingFrames(previous, sequence, 1000, 1000));
        }

        [Fact]
        public void A_whole_skipped_second_counts_a_hundred_frames_and_no_previous_means_no_gap()
        {
            Assert.Equal(0, ReceiveGap.MissingFrames(null, 7, 0, 1000));
            Assert.Equal(0, ReceiveGap.MissingFrames(3, 4, 1000, 1001));
            Assert.Equal(100, ReceiveGap.MissingFrames(3, 4, 1000, 1002));
            Assert.Equal(1000, ReceiveGap.MissingFrames(3, 4, 1000, 1500)); // capped
            Assert.Equal(0, ReceiveGap.MissingFrames(3, 4, 0, 1500));       // no previous second
        }

        [Fact]
        public void The_tracker_conceals_up_to_the_cap_and_splices_past_it()
        {
            var t = new ReceiveGapTracker();
            t.Seed(0, 1000.1);
            Assert.Equal(ReceiveGapAction.None, t.Observe(1, 1000.2, false, out int m));
            Assert.Equal(0, m);
            Assert.Equal(ReceiveGapAction.Conceal, t.Observe(1 + ReceiveGap.ConcealCapFrames + 1, 1000.3, false, out m));
            Assert.Equal(ReceiveGap.ConcealCapFrames, m);
            Assert.Equal(ReceiveGapAction.SpliceOverCap, t.Observe((6 + ReceiveGap.ConcealCapFrames + 2) & 15, 1000.4, false, out m));
            Assert.Equal(ReceiveGap.ConcealCapFrames + 1, m);
            Assert.Equal(1, t.GapsOverCap);
            Assert.Equal(ReceiveGap.ConcealCapFrames, 4);
        }

        [Fact]
        public void The_JustStarted_seed_makes_the_next_packet_consecutive()
        {
            var t = new ReceiveGapTracker();
            Assert.Equal(ReceiveGapAction.None, t.Observe(9, 1000.0, false, out _)); // nothing before it
            t.Seed(13, 1005.5);
            Assert.Equal(ReceiveGapAction.None, t.Observe(14, 1005.6, false, out int m));
            Assert.Equal(0, m);
        }

        [Fact]
        public void Packets_consumed_while_muted_keep_the_state_and_conceal_nothing()
        {
            var t = new ReceiveGapTracker();
            t.Seed(0, 1000.0);
            Assert.Equal(ReceiveGapAction.None, t.Observe(1, 1000.0, true, out _));
            Assert.Equal(ReceiveGapAction.Muted, t.Observe(4, 1000.0, true, out int m));
            Assert.Equal(2, m);
            // Unmuted again: the next packet follows the last muted one.
            Assert.Equal(ReceiveGapAction.None, t.Observe(5, 1000.0, false, out _));
            Assert.Equal(1, t.GapsWhileMuted);
        }

        [Fact]
        public void Concealment_switched_off_splices_and_says_so()
        {
            var t = new ReceiveGapTracker(concealEnabled: false);
            t.Seed(0, 1000.0);
            Assert.Equal(ReceiveGapAction.SpliceSwitchedOff, t.Observe(2, 1000.0, false, out _));
            Assert.Contains("SWITCHED OFF", t.Summary());
        }

        [Fact]
        public void The_test_switches_parse_strictly()
        {
            Assert.Equal(50, ReceiveTestSwitches.ParseDropEvery("every:50"));
            Assert.Equal(7, ReceiveTestSwitches.ParseDropEvery(" EVERY: 7 "));
            Assert.Equal(0, ReceiveTestSwitches.ParseDropEvery(null));
            Assert.Equal(0, ReceiveTestSwitches.ParseDropEvery("every:1"));
            Assert.Equal(0, ReceiveTestSwitches.ParseDropEvery("50"));
            Assert.Equal(0, ReceiveTestSwitches.ParseDropEvery("every:x"));
            Assert.True(ReceiveTestSwitches.ParseConcealEnabled(null));
            Assert.True(ReceiveTestSwitches.ParseConcealEnabled("on"));
            Assert.False(ReceiveTestSwitches.ParseConcealEnabled(" OFF "));

            var drop = new ReceiveDropTest(3);
            Assert.Equal(new[] { false, false, true, false, false, true },
                Enumerable.Range(0, 6).Select(_ => drop.ShouldDrop()).ToArray());
            var inert = new ReceiveDropTest(0);
            Assert.False(Enumerable.Range(0, 100).Any(_ => inert.ShouldDrop()));
        }

        [Fact]
        public void The_census_reads_mode_length_and_LBRR_once()
        {
            Assert.Equal("SILK", ReceiveOpusCensus.ModeOf(0x00));
            Assert.Equal("SILK", ReceiveOpusCensus.ModeOf(11 << 3));
            Assert.Equal("hybrid", ReceiveOpusCensus.ModeOf(12 << 3));
            Assert.Equal("hybrid", ReceiveOpusCensus.ModeOf(15 << 3));
            Assert.Equal("CELT", ReceiveOpusCensus.ModeOf(16 << 3));
            Assert.Equal("CELT", ReceiveOpusCensus.ModeOf(0xFF));

            var packets = Encode(Sine(ReceiveOpusCensus.PacketsToSample + 5, 1234, 0.3f));
            var census = new ReceiveOpusCensus();
            string? line = null;
            int lines = 0;
            foreach (var p in packets)
            {
                var l = census.Observe(p);
                if (l != null) { line = l; lines++; }
            }
            Assert.Equal(1, lines);
            Assert.Null(census.Report());
            Assert.Contains("500 packet(s)", line);
            Assert.Contains("10.0 to 10.0 ms", line);
            Assert.Contains("0 carry in-band FEC", line);

            var shortRun = new ReceiveOpusCensus();
            shortRun.Observe(packets[0]);
            Assert.Contains("run ended before", shortRun.Report());
            Assert.Null(new ReceiveOpusCensus().Report());
        }

        // ── The loop's order, read from its source ──

        [Fact]
        public void The_loop_drops_then_meters_then_fills_then_records_then_plays()
        {
            string source = FlexBasePartials.JoinedText();
            int start = source.IndexOf("private void remoteAudioProc(long run)", StringComparison.Ordinal);
            Assert.True(start >= 0, "remoteAudioProc was not found; this scan would be vacuous");
            int poll = source.IndexOf("// opus receive polling.", start, StringComparison.Ordinal);
            Assert.True(poll > start);
            int seed = source.IndexOf("rxGaps.Seed(seedSequence, seedTs)", poll, StringComparison.Ordinal);
            int drop = source.IndexOf("rxDrop.ShouldDrop()", poll, StringComparison.Ordinal);
            int meter = source.IndexOf("_opusRxContinuity.Consume(consumedTs)", poll, StringComparison.Ordinal);
            int observe = source.IndexOf("rxGaps.Observe(consumedSequence", poll, StringComparison.Ordinal);
            int fill = source.IndexOf(".FillGap(opusBuf, missing", poll, StringComparison.Ordinal);
            int record = source.IndexOf("recorder?.Receive(this, opusBuf", poll, StringComparison.Ordinal);
            int play = source.IndexOf(".WriteOpus(opusBuf)", poll, StringComparison.Ordinal);
            Assert.True(seed > poll && drop > seed && meter > drop && observe > meter && fill > observe
                && record > fill && play > record,
                "the receive loop's order changed: seed " + seed + ", drop " + drop + ", meter " + meter
                + ", observe " + observe + ", fill " + fill + ", record " + record + ", play " + play);
            Assert.Contains("ReceiveConcealed(this, prev + i, consumedTs)", source.Substring(fill, record - fill));
        }

        // ── The recorder ──

        /// <summary>Today's writer rule, pinned case by case before it was extracted.</summary>
        [Theory]
        [InlineData(null, 0L, 0L, 5, 1000L, 10L, 0, 0)]     // first frame
        [InlineData(4, 1000L, 0L, 5, 1000L, 10L, 0, 0)]     // consecutive
        [InlineData(4, 1000L, 0L, 7, 1000L, 30L, 0, 2)]     // two lost
        [InlineData(4, 1000L, 0L, 7, 1000L, 30L, 1, 1)]     // one already written while waiting
        [InlineData(4, 1000L, 0L, 5, 1003L, 10L, 0, 200)]   // two whole seconds skipped
        [InlineData(4, 1000L, 0L, 5, 1000L, 500L, 0, 49)]   // the counter wrapped exactly; arrival says 500 ms
        [InlineData(4, 1000L, 0L, 4, 1000L, 10L, 0, 15)]    // a repeated sequence
        public void The_recorder_silence_rule(int? prevSeq, long prevSecond, long prevArrival,
            int seq, long second, long arrival, int silentSince, int expected)
        {
            Assert.Equal(expected, ComputerFlacRecorder.SilentFramesBefore(prevSeq, prevSecond, prevArrival,
                seq, second, arrival, silentSince));
        }

        private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>
        /// Heard mode, a gap of k concealed buffers: exactly (packets + k)
        /// frames reach the disk. Driven through the recorder's real entry
        /// points into its real queue, and read back through the writer's real
        /// silence rule. The second half is the negative control: stamped the
        /// old way, every concealed buffer adds a gap's worth of false silence.
        /// </summary>
        [Fact]
        public void Heard_mode_writes_exactly_the_packets_plus_the_concealed_frames()
        {
            const int k = 3;
            int framesRight = FramesOnDisk(k, stampConcealed: true, out int packets);
            Assert.Equal(packets + k, framesRight);

            int framesWrong = FramesOnDisk(k, stampConcealed: false, out _);
            Assert.True(framesWrong > packets + k + 10,
                "without the concealed stamp the recorder wrote " + framesWrong + " frames; the hazard did not show");
        }

        private static int FramesOnDisk(int k, bool stampConcealed, out int packets)
        {
            var recorder = new ComputerFlacRecorder(_ => true, () => { }, () => false);
            var sessionType = typeof(ComputerFlacRecorder).GetNestedType("Session", BindingFlags.NonPublic)!;
            var session = sessionType.GetConstructors(Any)[0].Invoke(new object?[] { null, "unused.flac", true, 2, Rate });
            typeof(ComputerFlacRecorder).GetField("_session", Any)!.SetValue(recorder, session);
            var heard = new float[FrameSamples * 2];

            // Packets 0..4, then k lost, then 5 more. The loop's order:
            // concealed frames first, each stamped, then the real packet.
            packets = 0;
            int seq = 0;
            for (int i = 0; i < 5; i++, seq++, packets++)
            {
                recorder.Receive(null!, new byte[] { 1 }, seq & 15, 1000.5, false);
                recorder.ReceiveHeard(null!, heard);
            }
            int prev = seq - 1;
            seq += k;
            for (int i = 1; i <= k; i++)
            {
                if (stampConcealed) recorder.ReceiveConcealed(null!, (prev + i) & 15, 1000.5);
                recorder.ReceiveHeard(null!, heard);
            }
            for (int i = 0; i < 5; i++, seq++, packets++)
            {
                recorder.Receive(null!, new byte[] { 1 }, seq & 15, 1000.5, false);
                recorder.ReceiveHeard(null!, heard);
            }

            // Drain the queue and apply the writer's rule frame by frame.
            object queue = sessionType.GetField("Queue", Any)!.GetValue(session)!;
            object reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            var tryRead = reader.GetType().GetMethod("TryRead")!;
            int? previousSequence = null;
            long previousSecond = 0, previousArrival = 0;
            int frames = 0;
            var args = new object?[] { null };
            while ((bool)tryRead.Invoke(reader, args)!)
            {
                object item = args[0]!;
                int s = (int)item.GetType().GetProperty("Sequence")!.GetValue(item)!;
                long sec = (long)item.GetType().GetProperty("Second")!.GetValue(item)!;
                long arrival = (long)item.GetType().GetProperty("ArrivalMilliseconds")!.GetValue(item)!;
                frames += ComputerFlacRecorder.SilentFramesBefore(previousSequence, previousSecond, previousArrival,
                    s, sec, arrival, 0) + 1;
                previousSequence = s; previousSecond = sec; previousArrival = arrival;
                args[0] = null;
            }
            return frames;
        }

        [Fact]
        public void Raw_mode_ignores_concealed_frames()
        {
            var recorder = new ComputerFlacRecorder(_ => true, () => { }, () => false);
            var sessionType = typeof(ComputerFlacRecorder).GetNestedType("Session", BindingFlags.NonPublic)!;
            var session = sessionType.GetConstructors(Any)[0].Invoke(new object?[] { null, "unused.flac", false, 2, 24000 });
            typeof(ComputerFlacRecorder).GetField("_session", Any)!.SetValue(recorder, session);
            recorder.ReceiveConcealed(null!, 3, 1000.5);
            object queue = sessionType.GetField("Queue", Any)!.GetValue(session)!;
            object reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            Assert.Equal(0, (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!);
            Assert.Equal(0, (int)sessionType.GetField("PendingSequence", Any)!.GetValue(session)!);
        }
    }
}
