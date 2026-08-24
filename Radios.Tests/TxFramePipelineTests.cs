using System;
using System.Collections.Generic;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one definition of what happens to a transmit frame between
    /// "samples exist" and "bytes go to the radio".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this has tests at all: the pipeline exists because there are now
    /// TWO producers of transmit frames — the capture callback and the
    /// self-clocked source — and the promise the whole test-tone design rests
    /// on is that an injected signal rides the identical path a voice does.
    /// While that promise was a comment in one callback it could not be
    /// checked. Now it is a type, and these are the checks.
    /// </para>
    /// <para>
    /// The ORDER is the substance. Injection, then conditioning, then
    /// metering, then encode, then send. Get it wrong and nothing fails
    /// loudly: a tone metered before the conditioner still sounds like a tone,
    /// and the meter simply reports a number that is quietly not what shipped.
    /// </para>
    /// </remarks>
    public class TxFramePipelineTests
    {
        private const int Floats = 8;         // four stereo frames, enough to see order
        private const uint Rate = 24000;

        /// <summary>A source that records when it ran and stamps the buffer.</summary>
        private sealed class FakeSource : ITxInputSource
        {
            public bool Engaged { get; set; }
            public bool Idle { get; set; } = true;
            public bool BypassesConditioning { get; set; }
            public float Writes = 1f;
            public readonly List<string> Log;
            public FakeSource(List<string> log) { Log = log; }
            public void Process(float[] b, int n, uint rate)
            {
                Log.Add("source");
                for (int i = 0; i < n; i++) b[i] = Writes;
            }
        }

        private static TxFramePipeline Wire(List<string> log, out List<byte[]> sent, out FakeSource source)
        {
            var sentLocal = new List<byte[]>();
            var src = new FakeSource(log);
            var p = new TxFramePipeline
            {
                Source = src,
                Conditioner = (b, n, r) => { log.Add("condition"); for (int i = 0; i < n; i++) b[i] += 10f; },
                Meter = null,   // LufsMeter is concrete; order around it is covered by the encode probe
                Encode = b => { log.Add("encode:" + b[0].ToString("0.##")); return new byte[] { 7 }; },
                Handler = d => { log.Add("send"); sentLocal.Add(d); },
            };
            sent = sentLocal;
            source = src;
            return p;
        }

        [Fact]
        public void A_frame_runs_inject_then_condition_then_encode_then_send()
        {
            // The positive control for the whole type. If this ever passes
            // while reporting a different order, every "the tone rides the
            // identical path" claim in the codebase is worthless.
            var log = new List<string>();
            var p = Wire(log, out var sent, out var src);
            src.Engaged = true;
            src.BypassesConditioning = false;

            Assert.True(p.Emit(new float[Floats], Floats, Rate));

            Assert.Equal(new[] { "source", "condition", "encode:11", "send" }, log);
            Assert.Single(sent);
            Assert.Equal(1, p.FramesSent);
        }

        [Fact]
        public void The_conditioner_sees_what_the_source_wrote_not_what_arrived()
        {
            // Injection REPLACES. A conditioner reading the original buffer
            // would mean the tone was being mixed with the microphone rather
            // than standing in for it — and a reference signal mixed with a
            // live mic is not a reference signal.
            var log = new List<string>();
            var p = Wire(log, out _, out var src);
            src.Engaged = true;
            src.Writes = 2f;

            var buf = new float[Floats];
            for (int i = 0; i < Floats; i++) buf[i] = -99f;   // "microphone"
            p.Emit(buf, Floats, Rate);

            Assert.Equal("encode:12", log[2]);   // 2 written, +10 conditioned
        }

        [Fact]
        public void An_engaged_source_that_asks_to_bypass_gets_no_conditioning()
        {
            // The test tone's case. It is a CALIBRATED reference — -10 dBFS in
            // must read -10 dBFS on the radio's meter — and a gate or a
            // speech-trained noise reducer shaping a synthesized sine breaks
            // that property silently.
            var log = new List<string>();
            var p = Wire(log, out _, out var src);
            src.Engaged = true;
            src.BypassesConditioning = true;

            p.Emit(new float[Floats], Floats, Rate);

            Assert.DoesNotContain("condition", log);
            Assert.Equal("encode:1", log[1]);
        }

        [Fact]
        public void A_source_that_bypasses_but_is_NOT_engaged_still_gets_conditioning()
        {
            // The microphone's case while a tone sits armed but not sounding.
            // Bypassing here would silently disable noise reduction and the
            // gate for ordinary voice transmit — a real regression that would
            // never announce itself.
            var log = new List<string>();
            var p = Wire(log, out _, out var src);
            src.Engaged = false;
            src.BypassesConditioning = true;

            p.Emit(new float[Floats], Floats, Rate);

            Assert.Contains("condition", log);
        }

        [Fact]
        public void With_no_source_at_all_the_buffer_is_conditioned_and_sent_unchanged()
        {
            // The plain microphone path, which must keep working exactly as it
            // did before any of this existed.
            var log = new List<string>();
            var p = Wire(log, out var sent, out _);
            p.Source = null;

            var buf = new float[Floats];
            for (int i = 0; i < Floats; i++) buf[i] = 3f;
            Assert.True(p.Emit(buf, Floats, Rate));

            Assert.Equal(new[] { "condition", "encode:13", "send" }, log);
            Assert.Single(sent);
        }

        [Fact]
        public void A_frame_is_encoded_but_NOT_sent_when_the_producer_has_stopped()
        {
            // The teardown guard, which the capture callback has always had
            // between encode and send. It sits there and not earlier on
            // purpose: the encoder is stateful, and skipping an encode leaves
            // the bitstream with a hole the radio would have to recover from.
            var log = new List<string>();
            var p = Wire(log, out var sent, out _);

            Assert.False(p.Emit(new float[Floats], Floats, Rate, () => false));

            Assert.Contains(log, s => s.StartsWith("encode"));
            Assert.DoesNotContain("send", log);
            Assert.Empty(sent);
            Assert.Equal(1, p.FramesAbandonedAtTeardown);
            Assert.Equal(0, p.FramesSent);
        }

        [Fact]
        public void A_running_producer_sends_normally()
        {
            // Negative control for the guard above: a test that only ever
            // passed `false` would also pass with the guard inverted.
            var log = new List<string>();
            var p = Wire(log, out var sent, out _);

            Assert.True(p.Emit(new float[Floats], Floats, Rate, () => true));

            Assert.Single(sent);
            Assert.Equal(0, p.FramesAbandonedAtTeardown);
        }

        [Fact]
        public void An_encode_that_throws_is_counted_and_reported_as_stop_not_swallowed()
        {
            // An encoder that throws throws every frame. The caller has to
            // learn that from the return value — the capture callback turns it
            // into paAbort — because the alternative is a hundred failures a
            // second producing silence and no complaint.
            var p = new TxFramePipeline
            {
                Encode = b => throw new InvalidOperationException("boom"),
                Handler = d => Assert.Fail("must not send a frame that failed to encode"),
            };

            Assert.False(p.Emit(new float[Floats], Floats, Rate));
            Assert.False(p.Emit(new float[Floats], Floats, Rate));
            Assert.Equal(2, p.EncodeFailures);
            Assert.Equal(0, p.FramesSent);
        }

        [Fact]
        public void No_encoder_at_all_fails_rather_than_throwing()
        {
            // Reachable for real: the stream's encoder is nulled at close, and
            // that nulls this in the same breath, so a frame in flight lands
            // here. It must be a counted refusal, not a NullReferenceException
            // on an audio thread.
            var p = new TxFramePipeline { Handler = d => Assert.Fail("nothing to send") };

            Assert.False(p.Emit(new float[Floats], Floats, Rate));
            Assert.Equal(1, p.EncodeFailures);
        }

        [Fact]
        public void The_meter_reads_the_conditioned_signal_not_the_raw_one()
        {
            // The one ordering mistake that would change nothing audible and
            // still be wrong. The meter is the number an operator sets their
            // level by; if it runs BEFORE the conditioning chain it reports
            // what arrived rather than what shipped, and the whole
            // measurement quietly answers a different question. Nothing about
            // the transmitted audio would change, so nobody would hear it.
            //
            // Discriminated by feeding silence and letting the conditioner
            // supply the only signal there is: a meter reading the raw buffer
            // sees digital silence, one reading the conditioned buffer sees a
            // healthy level. No threshold guesswork — the two cases are
            // roughly a hundred decibels apart.
            //
            // A SINE, not a constant. The first version of this wrote 0.25 to
            // every sample and read -150 LUFS, which looked like the ordering
            // being wrong and was not: a constant is DC, and LUFS is K-
            // weighted, so its high-pass shelf correctly throws DC away. The
            // probe was the thing at fault. Worth leaving on the record —
            // an instrument that reports silence is not evidence of silence.
            var meter = new LufsMeter();
            double phase = 0;
            var p = new TxFramePipeline
            {
                Conditioner = (b, n, r) =>
                {
                    for (int i = 0; i < n; i += 2)
                    {
                        float s = (float)(0.25 * Math.Sin(phase));
                        phase += 2.0 * Math.PI * 1000.0 / r;
                        b[i] = s;
                        b[i + 1] = s;
                    }
                },
                Meter = meter,
                Encode = b => new byte[] { 1 },
                Handler = d => { },
            };

            const int frameFloats = 480;           // 10 ms of stereo at 24 kHz
            for (int f = 0; f < 100; f++)          // a second, past the momentary window
                p.Emit(new float[frameFloats], frameFloats, Rate);

            Assert.True(meter.MomentaryLufs > -40f,
                "the meter read " + meter.MomentaryLufs.ToString("0.0")
                + " LUFS — that is the silence handed IN, so the meter is running"
                + " before the conditioning chain and is measuring the wrong signal");
        }

        [Fact]
        public void The_meter_really_would_report_silence_if_it_were_given_silence()
        {
            // The negative control for the test above, without which that one
            // proves nothing: a meter stuck at some fixed non-silent value
            // would pass it.
            var meter = new LufsMeter();
            var p = new TxFramePipeline
            {
                Meter = meter,
                Encode = b => new byte[] { 1 },
                Handler = d => { },
            };

            const int frameFloats = 480;
            for (int f = 0; f < 100; f++)
                p.Emit(new float[frameFloats], frameFloats, Rate);

            Assert.True(meter.MomentaryLufs < -40f,
                "the meter reported " + meter.MomentaryLufs.ToString("0.0")
                + " LUFS on pure silence, so it cannot distinguish signal from"
                + " silence and the ordering test above is meaningless");
        }

        [Fact]
        public void Counters_reset_at_the_start_of_a_transmission()
        {
            var log = new List<string>();
            var p = Wire(log, out _, out _);
            p.Emit(new float[Floats], Floats, Rate);
            p.Emit(new float[Floats], Floats, Rate, () => false);

            Assert.Equal(1, p.FramesSent);
            Assert.Equal(1, p.FramesAbandonedAtTeardown);

            p.ResetCounters();

            Assert.Equal(0, p.FramesSent);
            Assert.Equal(0, p.FramesAbandonedAtTeardown);
            Assert.Equal(0, p.EncodeFailures);
        }
    }
}
