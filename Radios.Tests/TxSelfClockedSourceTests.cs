using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Producing transmit frames for a source that has no clock of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split the way the code is: <see cref="TxFramePump"/> does the work and
    /// takes time as a parameter, so a fifteen-minute transmission runs in
    /// microseconds and the answers are exact.
    /// <see cref="TxSelfClockedSource"/> is only the thread around it, and
    /// gets the two questions injected time cannot answer — does a thread
    /// really appear, and does it really go away.
    /// </para>
    /// <para>
    /// The property under test is never smoothness. It is that TOTAL frames
    /// track TOTAL elapsed time however ragged the pumping is, because the
    /// radio runs a jitter buffer, which forgives arrival jitter and cannot
    /// forgive sustained rate error.
    /// </para>
    /// </remarks>
    public class TxSelfClockedSourceTests
    {
        private const uint Rate = 24000;
        private const int PerFrame = 240;      // 10 ms
        private const long Tps = 10_000_000;
        private static long Ms(double ms) => (long)(ms * Tps / 1000.0);

        private sealed class Rig
        {
            public readonly TxFramePipeline Pipeline = new TxFramePipeline();
            public readonly List<float[]> Seen = new List<float[]>();
            public int Sent;

            public Rig()
            {
                Pipeline.Encode = b =>
                {
                    // Copy: the pump reuses one buffer, so keeping the
                    // reference would give every entry the last frame's values.
                    var copy = new float[b.Length];
                    Array.Copy(b, copy, b.Length);
                    Seen.Add(copy);
                    return new byte[] { 1 };
                };
                Pipeline.Handler = d => Sent++;
            }

            public TxFramePump Pump() => new TxFramePump(Pipeline, Rate, PerFrame);
        }

        // ── Pacing ────────────────────────────────────────────────────────

        [Fact]
        public void A_second_of_pumping_sends_exactly_one_hundred_frames()
        {
            // End to end rather than at the clock alone: a hundred 10 ms
            // frames in a second, however the pump was scheduled. The 7 ms
            // step is deliberately not a divisor of the 10 ms frame — a
            // per-call-delta implementation would lose a fraction every call
            // and land short.
            var rig = new Rig();
            var pump = rig.Pump();
            pump.Start(0);

            long t = 0;
            while (t < Ms(1000))
            {
                t += Ms(7);
                if (t > Ms(1000)) t = Ms(1000);
                pump.PumpOnce(t, Tps);
            }

            Assert.Equal(100, rig.Sent);
        }

        [Fact]
        public void Fifteen_minutes_of_ragged_pumping_does_not_drift_by_one_frame()
        {
            // The failure mode at the scale it becomes audible. Across 90,000
            // frames a per-call delta loses seconds of audio, and the radio's
            // jitter buffer answers a sustained rate error with a periodic
            // correction — the metronome Noel hears.
            var rig = new Rig();
            var pump = rig.Pump();
            pump.Start(0);

            long t = 0, end = Ms(15 * 60 * 1000);
            int[] pattern = { 3, 27, 1, 14, 9 };
            int i = 0;
            while (t < end)
            {
                t += Ms(pattern[i++ % pattern.Length]);
                if (t > end) t = end;
                pump.PumpOnce(t, Tps);
            }

            Assert.Equal(15 * 60 * 100, rig.Sent);
        }

        [Fact]
        public void Nothing_is_produced_before_Start_or_after_Stop()
        {
            // The outer gate at this level. Ratified 2026-08-24: transmit stop
            // stops everything, no drain and no tail.
            var rig = new Rig();
            var pump = rig.Pump();

            Assert.Equal(0, pump.PumpOnce(Ms(500), Tps));   // never started

            pump.Start(0);
            pump.PumpOnce(Ms(100), Tps);
            int afterFirst = rig.Sent;
            Assert.True(afterFirst > 0, "the positive control failed: pumping produced nothing while running");

            pump.Stop();
            Assert.Equal(0, pump.PumpOnce(Ms(5000), Tps));
            Assert.Equal(afterFirst, rig.Sent);
            Assert.False(pump.Running);
        }

        [Fact]
        public void A_new_transmission_owes_nothing_for_the_silence_between()
        {
            // Unkey, wait a minute, key again. Without the reset the first
            // pump of the new transmission would try to repay 6,000 frames.
            var rig = new Rig();
            var pump = rig.Pump();

            pump.Start(0);
            pump.PumpOnce(Ms(500), Tps);
            pump.Stop();
            int afterFirst = rig.Sent;

            pump.Start(Ms(60_000));
            pump.PumpOnce(Ms(60_010), Tps);
            Assert.Equal(afterFirst + 1, rig.Sent);
        }

        // ── What the source is handed ─────────────────────────────────────

        private sealed class StampingSource : ITxInputSource
        {
            private readonly float _v;
            public bool Stamp = true;
            public StampingSource(float v) { _v = v; }
            public bool Engaged => true;
            public bool Idle => false;
            public bool BypassesConditioning => true;
            public void Process(float[] b, int n, uint rate)
            {
                if (!Stamp) return;
                for (int i = 0; i < n; i++) b[i] = _v;
            }
        }

        [Fact]
        public void Every_frame_arrives_as_silence_never_as_the_previous_frame()
        {
            // There is no microphone here, so the "microphone samples" a
            // source sees must genuinely be silence. An ENGAGED source
            // overwrites the buffer, but one ramping in or out MULTIPLIES it —
            // and multiplying stale samples would ramp the previous frame's
            // audio back up. That would sound like a stutter and would be very
            // hard to attribute to a buffer nobody cleared.
            var rig = new Rig();
            var loud = new StampingSource(9f);
            rig.Pipeline.Source = loud;
            var pump = rig.Pump();
            pump.Start(0);

            pump.PumpOnce(Ms(10), Tps);
            loud.Stamp = false;                 // stops writing; buffer must be clean
            pump.PumpOnce(Ms(20), Tps);

            Assert.Equal(2, rig.Seen.Count);
            Assert.Equal(9f, rig.Seen[0][0]);   // positive control: it really did write
            Assert.Equal(0f, rig.Seen[1][0]);
        }

        [Fact]
        public void The_frames_handed_over_are_stereo_interleaved_at_the_declared_size()
        {
            // Everything downstream assumes interleaved stereo — a mono
            // microphone is duplicated onto both channels long before this
            // point — so a self-clocked frame must be the same shape.
            var rig = new Rig();
            var pump = rig.Pump();
            pump.Start(0);
            pump.PumpOnce(Ms(10), Tps);

            Assert.Equal(PerFrame * 2, rig.Seen[0].Length);
        }

        // ── Rate changes ──────────────────────────────────────────────────

        [Fact]
        public void A_pump_knows_when_it_no_longer_matches_the_stream()
        {
            // Noel, 2026-08-24: "make sure that the sample rate doesn't
            // change, though not often, sometimes it happens." The capture
            // path noticed by accident, because the callback changed with the
            // device. This one has to be asked — and the answer must be to
            // rebuild, because a clock carrying its old frame count into a new
            // timebase drifts from the first call.
            var pump = new Rig().Pump();

            Assert.True(pump.Matches(Rate, PerFrame));
            Assert.False(pump.Matches(48000, PerFrame));
            Assert.False(pump.Matches(Rate, 480));
        }

        // ── Stalls and failures ───────────────────────────────────────────

        [Fact]
        public void A_long_stall_is_clamped_rather_than_burst_at_the_radio()
        {
            // A two-second freeze owes 200 frames. Dumping those at once is a
            // worse fault than the gap — the jitter buffer would discard most
            // of it — so the excess is abandoned, countably.
            var rig = new Rig();
            var pump = rig.Pump();
            pump.Start(0);

            int sent = pump.PumpOnce(Ms(2000), Tps);

            Assert.Equal(TxSampleClock.MaxFramesPerCall, sent);
            Assert.Equal(200 - TxSampleClock.MaxFramesPerCall, pump.Clock.FramesDroppedToClamp);
            // And it recovers immediately rather than repaying the written-off
            // debt on every later call.
            Assert.Equal(1, pump.PumpOnce(Ms(2010), Tps));
        }

        [Fact]
        public void An_encode_failure_stops_the_burst_instead_of_hammering_it()
        {
            // Eight frames owed and the encoder broken. Emitting all eight
            // would be eight identical failures for one stall; the first false
            // answer means stop.
            var rig = new Rig();
            rig.Pipeline.Encode = b => throw new InvalidOperationException("boom");
            var pump = rig.Pump();
            pump.Start(0);

            Assert.Equal(0, pump.PumpOnce(Ms(2000), Tps));
            Assert.Equal(1, rig.Pipeline.EncodeFailures);
        }

        [Fact]
        public void A_nonsense_geometry_is_refused_at_construction()
        {
            var rig = new Rig();
            Assert.Throws<ArgumentNullException>(() => new TxFramePump(null, Rate, PerFrame));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TxFramePump(rig.Pipeline, 0, PerFrame));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TxFramePump(rig.Pipeline, Rate, 0));
        }

        // ── Reporting ─────────────────────────────────────────────────────

        [Fact]
        public void The_run_report_names_what_happened_not_what_was_intended()
        {
            // A trace line that always reads 100 tells us nothing on the day
            // it is wrong. This one carries the realised rate beside the
            // nominal one so the two can visibly disagree.
            var rig = new Rig();
            var pump = rig.Pump();
            pump.Start(0);
            for (long t = Ms(10); t <= Ms(1000); t += Ms(10)) pump.PumpOnce(t, Tps);

            string d = pump.DescribeRun(Ms(1000), Tps);
            Assert.Contains("100 frames owed", d);
            Assert.Contains("100 sent", d);
            Assert.Contains("100 frames/sec realised against 100 nominal", d);
            Assert.Contains("0 frames dropped to the stall clamp", d);
        }

        [Fact]
        public void The_run_report_shows_a_realised_rate_that_disagrees_when_it_should()
        {
            // The negative control for the line above. A report that always
            // said 100 would pass that test and be worthless.
            var rig = new Rig();
            var pump = rig.Pump();
            pump.Start(0);
            pump.PumpOnce(Ms(500), Tps);        // 50 frames owed, 8 allowed

            string d = pump.DescribeRun(Ms(1000), Tps);
            Assert.Contains("50 frames owed", d);
            Assert.Contains("8 sent", d);
            Assert.Contains("42 frames dropped to the stall clamp", d);
        }

        // ── The real thread ───────────────────────────────────────────────

        [Fact]
        public void The_thread_really_runs_and_really_stops()
        {
            // Injected time proves the arithmetic; only the real thread proves
            // there IS one. Deliberately loose on count — this is a liveness
            // check, not a timing measurement, and a tight bound would fail on
            // a loaded machine for no useful reason.
            var rig = new Rig();
            var src = new TxSelfClockedSource(rig.Pipeline, Rate, PerFrame);

            Assert.True(src.Start());
            Assert.False(src.Start());          // idempotent
            Thread.Sleep(150);
            int whileRunning = rig.Sent;

            src.Stop();
            Assert.False(src.Running);
            int atStop = rig.Sent;
            Thread.Sleep(60);

            Assert.True(whileRunning > 0, "the pump thread produced nothing at all in 150 ms");
            Assert.Equal(atStop, rig.Sent);     // nothing after Stop returned
        }

        [Fact]
        public void The_pump_thread_is_a_background_thread()
        {
            // Not a style point, and worth reaching for the private field to
            // assert. A foreground audio thread that outlives its Join is
            // exactly how this app produced orphan jjflexible.exe processes
            // (#14): the process cannot exit while one is alive, and nothing
            // on screen says why. Flipping IsBackground here would break
            // nothing a functional test could see — it would only strand the
            // next operator's process at shutdown.
            var rig = new Rig();
            var src = new TxSelfClockedSource(rig.Pipeline, Rate, PerFrame);
            src.Start();
            try
            {
                var field = typeof(TxSelfClockedSource)
                    .GetField("_thread", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(field);
                var t = (Thread)field.GetValue(src);
                Assert.NotNull(t);
                Assert.True(t.IsBackground, "the TX self-clock thread must not be able to pin the process");
            }
            finally { src.Stop(); }
        }
    }
}
