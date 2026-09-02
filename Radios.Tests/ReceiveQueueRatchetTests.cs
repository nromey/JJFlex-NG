using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJPortaudio;
using JJTrace;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The receive playback queue, driven by a simulated packet-arrival pattern
    /// instead of by a radio (#473).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The simulation reproduces the real callback's arithmetic, it does not
    /// approximate it.</b> <c>outputCallback</c> asks
    /// <see cref="RxPlaybackQueue.Begin"/> what to do, discards what it is told
    /// to, plays queued packets until the device buffer is full, zero-fills what
    /// is left, and reports the shortfall to
    /// <see cref="RxPlaybackQueue.NotePlayed"/>. <see cref="Sim.Callback"/> below
    /// does the same five things in the same order against the same object. What
    /// it stands in for is PortAudio and the network, neither of which a test can
    /// have.
    /// </para>
    /// <para>
    /// Numbers throughout are the shipped shape: 48 kHz, 10 ms Opus packets, ten
    /// callbacks a second. So one queued buffer is 10 ms and 480 frames, and one
    /// device buffer is ten of them.
    /// </para>
    /// </remarks>
    public class ReceiveQueueRatchetTests
    {
        private const int BuffersPerCallback = 10;    // 100 ms device buffer
        private const double BufferMs = 10.0;         // one Opus packet
        private const int FramesPerBuffer = 480;      // 10 ms at 48 kHz
        private const uint Rate = 48000;

        private readonly ITestOutputHelper _out;

        public ReceiveQueueRatchetTests(ITestOutputHelper output) { _out = output; }

        private sealed class Sim
        {
            public readonly RxPlaybackQueue Queue;
            public int Depth;
            public int Callbacks;

            public Sim(double reserveMs, string name = "receive")
            {
                Queue = new RxPlaybackQueue(name, BuffersPerCallback, BufferMs, Rate, reserveMs);
            }

            /// <summary>
            /// One output callback, preceded by the packets that arrived since
            /// the previous one.
            /// </summary>
            public void Callback(int arrivals)
            {
                Depth += arrivals;
                Callbacks++;

                RxCallbackPlan plan = Queue.Begin(Depth, midBuffer: false);
                int discarded = Math.Min(plan.Discard, Depth);
                Depth -= discarded;

                if (plan.HoldForPrime)
                {
                    Queue.NotePrimingBuffer((long)BuffersPerCallback * FramesPerBuffer);
                    return;
                }

                int consumed = Math.Min(Depth, BuffersPerCallback);
                Depth -= consumed;
                long silentFrames = (long)(BuffersPerCallback - consumed) * FramesPerBuffer;
                Queue.NotePlayed(consumed, silentFrames);
            }

            public void Steady(int callbacks)
            {
                for (int i = 0; i < callbacks; i++) Callback(BuffersPerCallback);
            }
        }

        // ── The premise, checked before anything is built on it ─────────────

        [Fact]
        public void OneLatePacketCostsOneFrameOfSilenceNotAWholeBuffer()
        {
            // #473 and AudioBuffering's own remarks both said "one late packet
            // costs 100 ms of silence, not 10". The loop says otherwise: it
            // plays every queued packet that fits before it fills anything.
            // Reserve zero, so this measures the SHIPPED behaviour with no
            // policy in the way.
            var sim = new Sim(reserveMs: 0);
            sim.Steady(3);
            Assert.Equal(0, sim.Queue.Starvations);

            sim.Callback(arrivals: BuffersPerCallback - 1);   // exactly one packet late

            Assert.Equal(1, sim.Queue.Starvations);
            Assert.Equal(10.0, sim.Queue.StarvationSilenceMilliseconds, 3);
            Assert.NotEqual(100.0, sim.Queue.StarvationSilenceMilliseconds);
        }

        [Fact]
        public void ShortfallIsProportionalToHowManyPacketsAreMissing()
        {
            var sim = new Sim(reserveMs: 0);
            sim.Steady(3);
            sim.Callback(arrivals: 4);        // six packets short

            Assert.Equal(60.0, sim.Queue.StarvationSilenceMilliseconds, 3);
        }

        // ── The ratchet, in the shipped arithmetic ──────────────────────────

        [Fact]
        public void ShippedArithmeticNeverGivesBackASurplus()
        {
            // No policy object at all — this is the callback as it has always
            // behaved, written out so the defect is stated rather than argued.
            // Consumption is capped at one device buffer per call because
            // PortAudio sizes the buffer, so a queue that gets ahead stays
            // ahead for the life of the stream.
            int depth = 0;
            for (int i = 0; i < 5; i++)
            {
                depth += BuffersPerCallback;
                depth -= Math.Min(depth, BuffersPerCallback);
            }
            Assert.Equal(0, depth);

            depth += 30;                                   // a burst
            depth -= Math.Min(depth, BuffersPerCallback);
            Assert.Equal(20, depth);                       // 200 ms standing

            for (int i = 0; i < 500; i++)                  // fifty seconds of calm
            {
                depth += BuffersPerCallback;
                depth -= Math.Min(depth, BuffersPerCallback);
            }
            Assert.Equal(20, depth);   // still there, and it always will be
        }

        [Fact]
        public void ShippedArithmeticLeavesNoMarginSoTheNextLatePacketStarvesAgain()
        {
            // The equilibrium without a reserve is EXACTLY the depth one
            // callback consumes: every callback takes everything there is, so
            // there is nothing left over to absorb anything.
            var sim = new Sim(reserveMs: 0);
            sim.Steady(5);
            Assert.Equal(0, sim.Depth);          // nothing carried between callbacks

            sim.Callback(arrivals: 4);
            Assert.Equal(1, sim.Queue.Starvations);
            sim.Steady(3);                       // three healthy seconds in between
            Assert.Equal(0, sim.Depth);          // and still no margin
            sim.Callback(arrivals: 4);
            Assert.Equal(2, sim.Queue.Starvations);
        }

        // ── What priming changes ────────────────────────────────────────────

        [Fact]
        public void PrimingHoldsPlaybackUntilTheReserveExistsAndThatIsNotStarvation()
        {
            var sim = new Sim(RxPlaybackQueue.DefaultReserveMilliseconds);
            Assert.Equal(6, sim.Queue.ReserveBuffers);
            Assert.Equal(16, sim.Queue.PrimeTarget);

            sim.Callback(arrivals: BuffersPerCallback);   // depth 10, short of 16
            Assert.True(sim.Queue.Priming);
            Assert.Equal(0, sim.Queue.Starvations);
            Assert.Equal(100.0, sim.Queue.PrimeSilenceMilliseconds, 3);

            sim.Callback(arrivals: BuffersPerCallback);   // depth 20, target crossed
            Assert.False(sim.Queue.Priming);
            Assert.Equal(0, sim.Queue.Starvations);
        }

        [Fact]
        public void TheRealisedReserveIsTheConfiguredOneNotWhateverLuckPutIn()
        {
            // A callback looks at the queue once, so priming releases on the
            // first callback that SEES the target and the overshoot can be a
            // whole buffer. The release trim removes it; without that, asking
            // for 60 ms of reserve would hand out 60 to 160.
            var sim = new Sim(RxPlaybackQueue.DefaultReserveMilliseconds);
            sim.Steady(5);
            Assert.Equal(6, sim.Depth);                    // exactly the reserve
            Assert.Equal(4, sim.Queue.ReleaseTrimmedBuffers);
            Assert.Equal(0, sim.Queue.Trims);              // nothing ratcheted
        }

        [Fact]
        public void AReserveTurnsAFortyMillisecondOutageIntoNoGapAtAll()
        {
            var withReserve = new Sim(RxPlaybackQueue.DefaultReserveMilliseconds);
            withReserve.Steady(5);
            withReserve.Callback(arrivals: 4);             // six packets late
            Assert.Equal(0, withReserve.Queue.Starvations);
            Assert.Equal(0.0, withReserve.Queue.StarvationSilenceMilliseconds, 3);

            var without = new Sim(reserveMs: 0);
            without.Steady(5);
            without.Callback(arrivals: 4);                 // the identical outage
            Assert.Equal(1, without.Queue.Starvations);
            Assert.Equal(60.0, without.Queue.StarvationSilenceMilliseconds, 3);
        }

        [Fact]
        public void AStarvationRebuildsTheReserveSoTheSameOutageCostsTheSameTwice()
        {
            // THE point of the whole change. Priming once is not enough: the
            // first starvation spends the reserve, and with nothing to rebuild
            // it the stream runs the rest of its life on the bare minimum.
            var sim = new Sim(RxPlaybackQueue.DefaultReserveMilliseconds);
            sim.Steady(5);

            sim.Callback(arrivals: 0);                     // a whole period arrives late
            Assert.Equal(1, sim.Queue.Starvations);
            double firstGap = sim.Queue.StarvationSilenceMilliseconds;
            Assert.Equal(40.0, firstGap, 3);               // reserve absorbed 60 of the 100
            Assert.True(sim.Queue.Priming);                // and it is being rebuilt

            sim.Steady(6);
            Assert.False(sim.Queue.Priming);
            Assert.Equal(6, sim.Depth);                    // margin restored in full
            Assert.Equal(1, sim.Queue.PrimeEpisodes);

            sim.Callback(arrivals: 0);                     // the identical outage again
            Assert.Equal(2, sim.Queue.Starvations);
            double secondGap = sim.Queue.StarvationSilenceMilliseconds - firstGap;
            Assert.Equal(firstGap, secondGap, 3);          // no worse than the first
        }

        [Fact]
        public void WithoutRebuildingTheSecondOutageWouldCostMoreThanTheFirst()
        {
            // The counter-case, to show the previous test is measuring
            // something. Reserve zero is the shipped behaviour: no rebuild,
            // and every outage costs the full hundred milliseconds.
            var sim = new Sim(reserveMs: 0);
            sim.Steady(5);

            sim.Callback(arrivals: 0);
            double first = sim.Queue.StarvationSilenceMilliseconds;
            Assert.Equal(100.0, first, 3);

            sim.Steady(6);
            sim.Callback(arrivals: 0);
            double second = sim.Queue.StarvationSilenceMilliseconds - first;
            Assert.Equal(100.0, second, 3);
            Assert.True(second > 40.0,
                "the shipped path has no reserve to absorb any of the outage");
        }

        // ── Giving a ratcheted backlog back ─────────────────────────────────

        [Fact]
        public void ABurstThatRatchetsTheBacklogIsTrimmedRatherThanCarriedForever()
        {
            var sim = new Sim(RxPlaybackQueue.DefaultReserveMilliseconds);
            sim.Steady(5);
            long trimmedBefore = sim.Queue.TrimmedBuffers;

            sim.Callback(arrivals: 30);      // depth 36 at entry, ceiling is 26
            Assert.Equal(6, sim.Depth);      // back to the reserve, not 26

            Assert.Equal(trimmedBefore + 20, sim.Queue.TrimmedBuffers);
            sim.Steady(20);
            Assert.Equal(6, sim.Depth);      // and it stays there
        }

        [Fact]
        public void ASurplusInsideTheCeilingIsLeftAloneBecauseDiscardingIsAudible()
        {
            var sim = new Sim(RxPlaybackQueue.DefaultReserveMilliseconds);
            sim.Steady(5);
            long trimmedBefore = sim.Queue.TrimmedBuffers;

            sim.Callback(arrivals: 18);      // depth 24 at entry, under the 26 ceiling
            Assert.Equal(trimmedBefore, sim.Queue.TrimmedBuffers);
            Assert.Equal(14, sim.Depth);
        }

        // ── Streams that must not be touched ────────────────────────────────

        [Fact]
        public void AStreamWithNoReserveNeverHoldsNeverTrimsAndNeverRePrimes()
        {
            // JJFLEX_RX_PRIME_MS=0, and also the CW monitor: a sidetone held
            // back to build a reserve arrives late, and trimming a queue
            // somebody is keying into discards their own Morse.
            var sim = new Sim(reserveMs: 0);
            Assert.False(sim.Queue.Priming);
            Assert.Equal(0, sim.Queue.ReserveBuffers);

            sim.Steady(5);
            sim.Callback(arrivals: 40);                    // far past any ceiling
            Assert.Equal(0, sim.Queue.Trims);
            Assert.Equal(30, sim.Depth);                   // nothing was discarded

            sim.Callback(arrivals: 0);
            Assert.False(sim.Queue.Priming);               // and no re-prime either
        }

        [Fact]
        public void SilenceBeforeAnyAudioHasPlayedIsNotCalledStarvation()
        {
            // The CW monitor spends most of a session here: an output stream
            // that is fed only while somebody is keying. Its 1,277 silent
            // buffers on 2026-09-01 were correctly not called starvation, and
            // must stay that way.
            var sim = new Sim(reserveMs: 0, name: "CW monitor");
            for (int i = 0; i < 50; i++) sim.Callback(arrivals: 0);

            Assert.Equal(0, sim.Queue.Starvations);
            Assert.Equal(50, sim.Queue.PrimeCallbacks);
            Assert.False(sim.Queue.EverPlayed);
        }

        // ── The knobs ───────────────────────────────────────────────────────

        [Fact]
        public void TheReserveIsSettableWithoutARebuild()
        {
            string saved = Environment.GetEnvironmentVariable(
                RxPlaybackQueue.ReserveEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    RxPlaybackQueue.ReserveEnvironmentVariable, "120");
                RxPlaybackQueue.ResetConfiguredReserve();
                Assert.Equal(120.0, RxPlaybackQueue.ConfiguredReserveMilliseconds());

                Environment.SetEnvironmentVariable(
                    RxPlaybackQueue.ReserveEnvironmentVariable, "not a number");
                RxPlaybackQueue.ResetConfiguredReserve();
                Assert.Equal(RxPlaybackQueue.DefaultReserveMilliseconds,
                    RxPlaybackQueue.ConfiguredReserveMilliseconds());

                Environment.SetEnvironmentVariable(
                    RxPlaybackQueue.ReserveEnvironmentVariable, "0");
                RxPlaybackQueue.ResetConfiguredReserve();
                Assert.Equal(0.0, RxPlaybackQueue.ConfiguredReserveMilliseconds());
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    RxPlaybackQueue.ReserveEnvironmentVariable, saved);
                RxPlaybackQueue.ResetConfiguredReserve();
            }
        }

        [Fact]
        public void TheReceiveCallbackRateIsSettableWithoutARebuildAndTheDefaultIsUnchanged()
        {
            string saved = Environment.GetEnvironmentVariable(
                AudioBuffering.RxCallbackRateEnvironmentVariable);
            try
            {
                AudioBuffering.ResetConfiguredRxCallbacksPerSecond();
                Assert.Equal(AudioBuffering.DefaultCallbacksPerSecond,
                    AudioBuffering.ConfiguredRxCallbacksPerSecond());

                Environment.SetEnvironmentVariable(
                    AudioBuffering.RxCallbackRateEnvironmentVariable, "50");
                AudioBuffering.ResetConfiguredRxCallbacksPerSecond();
                Assert.Equal(50, AudioBuffering.ConfiguredRxCallbacksPerSecond());

                // 200 a second is below one whole Opus frame per callback, which
                // Audio.Open refuses; refuse it here too rather than let it get
                // that far.
                Environment.SetEnvironmentVariable(
                    AudioBuffering.RxCallbackRateEnvironmentVariable, "200");
                AudioBuffering.ResetConfiguredRxCallbacksPerSecond();
                Assert.Equal(AudioBuffering.DefaultCallbacksPerSecond,
                    AudioBuffering.ConfiguredRxCallbacksPerSecond());
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    AudioBuffering.RxCallbackRateEnvironmentVariable, saved);
                AudioBuffering.ResetConfiguredRxCallbacksPerSecond();
            }
        }

        // ── The instrumentation, captured through the real trace subsystem ──

        [Fact]
        public void TheStarvationInstrumentationProducesReadableTraceLines()
        {
            // Not a formatting assertion for its own sake: this is how the
            // track's report gets REAL trace lines out of a machine with no
            // radio attached. The lines below come out of JJTrace itself, from
            // the same code paths a connected session runs.
            var captured = new List<string>();
            var listener = new CapturingListener(captured);

            bool wasOn = Tracing.On;
            TraceSwitch savedSwitch = Tracing.TheSwitch;
            Trace.Listeners.Add(listener);
            try
            {
                Tracing.TheSwitch = new TraceSwitch("rxqueue", "rxqueue")
                {
                    Level = TraceLevel.Verbose
                };
                Tracing.On = true;

                var sim = new Sim(RxPlaybackQueue.DefaultReserveMilliseconds);
                sim.Steady(5);
                sim.Callback(arrivals: 0);      // a starvation
                sim.Steady(8);
                sim.Callback(arrivals: 30);     // a burst that needs trimming
                sim.Steady(5);
                sim.Queue.TraceSummary();
            }
            finally
            {
                Tracing.On = wasOn;
                Tracing.TheSwitch = savedSwitch;
                Trace.Listeners.Remove(listener);
            }

            foreach (string line in captured) _out.WriteLine(line);

            Assert.Contains(captured, l => l.Contains("audio receive stream: the playback queue ran dry"));
            Assert.Contains(captured, l => l.Contains("audio receive queue policy: primed to 16 buffer(s)"));
            Assert.Contains(captured, l => l.Contains("audio receive queue summary:"));
            Assert.Contains(captured, l => l.Contains("audio receive standing latency:"));
            Assert.Contains(captured, l => l.Contains("audio receive backlog trims:"));
            Assert.Contains(captured, l => l.Contains("audio receive queue depth:"));
            // Every line names the stream, which is the whole point: a connected
            // session has two output streams and every line used to say only
            // "audio output stream".
            Assert.DoesNotContain(captured, l => l.Contains("audio output stream:"));
        }

        private sealed class CapturingListener : TraceListener
        {
            private readonly List<string> _lines;
            public CapturingListener(List<string> lines) { _lines = lines; }
            public override void Write(string message) { }
            public override void WriteLine(string message) { _lines.Add(message); }
        }
    }
}
