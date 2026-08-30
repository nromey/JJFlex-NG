using System;
using System.Linq;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The transmit boundary: the only place that both consults the gate and
    /// calls something that keys a radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A gate nothing consults is not a gate.</b> These exist because the
    /// guard being correct and the guard being ON THE PATH are two different
    /// claims, and only the second protects anybody. That is the same failure
    /// this whole tool was built to expose, turned on the tool.
    /// </para>
    /// <para>
    /// Nothing here asserts what a reading MEANS. The host measures and the
    /// engine interprets, so the words the operator hears belong to
    /// <c>TransmitStages.Transmitter</c> and are tested with it.
    /// </para>
    /// </remarks>
    public class FixerTransmitBoundaryTests
    {
        private const string Run = "TX-4K2M";
        private const string Stage = "transmitter-check";

        private static FixerTransmitGate ReadyGate()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("50 ohm dummy load on ANT1", FixerLoadKind.DummyLoad);
            return g;
        }

        // ---- wiring: a half-wired host keys nothing ----

        [Fact]
        public void With_no_gate_there_is_no_probe()
        {
            // Null is the engine's "the host wired nothing" signal, and it
            // records the stage as unable to run. For a transmitting stage that
            // null is what stands between a half-wired host and a keyed radio.
            Assert.Null(FixerTransmitBoundary.ProbeTransmitter(null, () => null, Stage));
        }

        [Fact]
        public void With_no_way_to_reach_a_radio_there_is_no_probe()
        {
            Assert.Null(FixerTransmitBoundary.ProbeTransmitter(ReadyGate(), null, Stage));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void With_no_stage_to_charge_it_to_there_is_no_probe(string stageId)
        {
            // The gate enforces once-per-stage. A probe with no stage name
            // would be a transmit charged to nothing, which is a transmit the
            // repeat guard cannot see.
            Assert.Null(FixerTransmitBoundary.ProbeTransmitter(ReadyGate(), () => null, stageId));
        }

        [Fact]
        public void A_fully_wired_host_gets_a_probe()
        {
            // The positive control. Without it, the three above would pass on a
            // factory that always returned null.
            Assert.NotNull(FixerTransmitBoundary.ProbeTransmitter(ReadyGate(), () => null, Stage));
        }

        // ---- the gate is actually consulted ----

        [Fact]
        public void The_probe_asks_the_gate_and_nothing_keys_when_it_says_no()
        {
            var gate = ReadyGate();
            TxTuneProbe.Result r =
                FixerTransmitBoundary.ProbeTransmitter(gate, () => null, Stage)();

            Assert.Equal(TxTuneProbe.Verdict.NotRun, r.Verdict);
            Assert.Equal(0, gate.TransmitCount);
            Assert.Equal(0, gate.KeyDownSeconds);
        }

        [Fact]
        public void A_refusal_carries_the_gates_own_words_through_to_the_explanation()
        {
            // The whole reason SkipDetail exists. Without it the refusal would
            // arrive as a generic reason and the specific, actionable sentence
            // the gate wrote would be thrown away at the boundary.
            var gate = new FixerTransmitGate();
            gate.BeginRun(Run);            // deliberately no load declared

            TxTuneProbe.Result r =
                FixerTransmitBoundary.ProbeTransmitter(gate, () => null, Stage)();

            Assert.NotEqual("", r.SkipDetail);
            Assert.Equal(r.SkipDetail, TxTuneProbe.Explain(r));
        }

        [Fact]
        public void A_radio_source_that_throws_is_treated_as_no_radio_not_as_a_crash()
        {
            // The Fixer Tool only opens when something is already wrong, so the
            // thing it asks for the radio is exactly the thing likely to throw.
            var gate = ReadyGate();
            TxTuneProbe.Result r = FixerTransmitBoundary.ProbeTransmitter(
                gate, () => throw new InvalidOperationException("no connection"), Stage)();

            Assert.Equal(TxTuneProbe.SkipReason.RadioNotReachable, r.Skipped);
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void An_abandoned_run_keys_nothing()
        {
            var gate = ReadyGate();
            gate.AbortRun();

            TxTuneProbe.Result r =
                FixerTransmitBoundary.ProbeTransmitter(gate, () => null, Stage)();

            Assert.Equal(TxTuneProbe.SkipReason.Cancelled, r.Skipped);
            Assert.Equal(0, gate.TransmitCount);
        }

        // ---- a refusal keeps its meaning across the boundary ----

        [Fact]
        public void Station_conditions_keep_the_probes_own_name_for_themselves()
        {
            // These three already have a name in the probe's vocabulary, and a
            // reader of the report should see the same word whichever layer
            // refused.
            Assert.Equal(TxTuneProbe.SkipReason.RadioNotReachable,
                FixerTransmitBoundary.SkipFor(FixerTransmitGate.Refusal.NoRadio));
            Assert.Equal(TxTuneProbe.SkipReason.AlreadyTransmitting,
                FixerTransmitBoundary.SkipFor(FixerTransmitGate.Refusal.AlreadyInFlight));
            Assert.Equal(TxTuneProbe.SkipReason.LoadNotDeclared,
                FixerTransmitBoundary.SkipFor(FixerTransmitGate.Refusal.LoadNotDeclared));
        }

        [Fact]
        public void Faults_in_our_software_are_never_reported_as_faults_in_the_station()
        {
            // THE mapping rule. Telling an operator their radio was unreachable
            // when really our repeat guard fired sends them hunting a fault
            // that is not there — which is this project's dominant defect class
            // wearing a different hat.
            foreach (FixerTransmitGate.Refusal r in new[]
            {
                FixerTransmitGate.Refusal.TooFast,
                FixerTransmitGate.Refusal.BudgetSpent,
                FixerTransmitGate.Refusal.StageAlreadyTransmitted,
                FixerTransmitGate.Refusal.StageDoesNotTransmit,
                FixerTransmitGate.Refusal.WrongRun,
                FixerTransmitGate.Refusal.NoRun,
            })
                Assert.Equal(TxTuneProbe.SkipReason.RefusedByHost,
                             FixerTransmitBoundary.SkipFor(r));
        }

        [Fact]
        public void No_refusal_maps_to_no_reason_at_all()
        {
            // SkipReason.None on a NotRun result would read as "nothing was
            // skipped", which is the silent failure restated.
            foreach (FixerTransmitGate.Refusal r in
                     (FixerTransmitGate.Refusal[])Enum.GetValues(typeof(FixerTransmitGate.Refusal)))
            {
                if (r == FixerTransmitGate.Refusal.None) continue;
                Assert.NotEqual(TxTuneProbe.SkipReason.None, FixerTransmitBoundary.SkipFor(r));
            }
        }

        [Fact]
        public void Every_refusal_reaching_the_probe_produces_words_worth_hearing()
        {
            foreach (FixerTransmitGate.Decision d in EveryRefusal())
            {
                TxTuneProbe.Result r =
                    TxTuneProbe.Result.NotRun(FixerTransmitBoundary.SkipFor(d.Why), d.Explanation);

                Assert.Equal(TxTuneProbe.Verdict.NotRun, r.Verdict);
                Assert.False(string.IsNullOrWhiteSpace(TxTuneProbe.Explain(r)),
                             d.Why + " explained nothing");
            }
        }

        [Fact]
        public void A_refusal_never_gives_the_audio_stages_standing_to_run()
        {
            // A refused transmitter check proves nothing about the transmitter,
            // so nothing downstream may be read as though it did.
            foreach (FixerTransmitGate.Decision d in EveryRefusal())
                Assert.False(TxTuneProbe.Result
                    .NotRun(FixerTransmitBoundary.SkipFor(d.Why), d.Explanation)
                    .AudioTestingHasStanding);
        }

        // ---- the cues (#255): stage 2 announces its own transmit ----
        //
        // Everything here runs against a null radio, the file's standing
        // convention: every refusal path must be provable without a
        // transmitter in the room, and the granted path — countdown, key,
        // speak on MOX confirmation — rides the same Witness/callback shape
        // stage 4 uses and cannot honestly be tested without a rig.

        [Fact]
        public void No_cue_ever_sounds_for_a_refused_transmit()
        {
            // The countdown is a promise that RF is imminent, and "transmitting"
            // is a claim about the radio. A refusal must produce neither — a
            // blind operator hearing the count for a transmit that never keys
            // learns to distrust the count, which costs every stage that
            // depends on it.
            bool counted = false, spokeNow = false, spokeDone = false;

            var gate = new FixerTransmitGate();
            gate.BeginRun(Run);            // deliberately no load declared

            TxTuneProbe.Result r = FixerTransmitBoundary.ProbeTransmitter(
                gate, () => null, Stage,
                speakNow: () => spokeNow = true,
                speakDone: () => spokeDone = true,
                countdown: () => counted = true)();

            Assert.Equal(TxTuneProbe.Verdict.NotRun, r.Verdict);
            Assert.False(counted, "the countdown sounded for a refused transmit");
            Assert.False(spokeNow, "\"transmitting\" was spoken for a refused transmit");
            Assert.False(spokeDone, "\"finished\" was spoken for a transmit that never began");
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void A_stop_during_the_countdown_keys_nothing_and_says_so()
        {
            // The countdown only runs after a grant, and a grant needs a
            // reachable radio — so the full path cannot run here. The shared
            // pacing helper is the half that exists without a rig, and its
            // contract is the one that matters: a stop asked during the count
            // must answer "do not key".
            bool counted = false;
            bool ready = FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                () => counted = true, stopRequested: () => true);

            Assert.True(counted, "the countdown itself should still have been started");
            Assert.False(ready, "a stop during the count still said it was fine to key");
        }

        [Fact]
        public void With_no_stop_hook_the_count_paces_the_key_up_and_allows_it()
        {
            // A missing hook counts silently and still paces the key-up, so
            // the timing an operator learns does not change with the wiring.
            //
            // The pacing is the HOST'S number now, passed in rather than read
            // from a constant here, so this names its own short one — which is
            // also the assertion that the parameter is honoured at all.
            const int keyUpAt = 250;
            var w = System.Diagnostics.Stopwatch.StartNew();
            bool ready = FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                countdown: null, stopRequested: null, keyUpAtMs: keyUpAt);
            w.Stop();

            Assert.True(ready);
            Assert.True(w.ElapsedMilliseconds >= keyUpAt - 30,
                "the key-up was not paced to the count: " + w.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void A_pacing_of_nothing_waits_the_conservative_default_rather_than_keying_at_once()
        {
            // THE ONE DIRECTION THIS MUST NOT FAIL IN IS EARLY. The count runs
            // unkeyed and those milliseconds are the operator's only window to
            // stop the transmission before it starts (#236), so a host that
            // publishes zero — or a negative, or nothing at all — must wait,
            // not key immediately.
            //
            // Asserted on the boundary rather than by sleeping the default,
            // which is two seconds: the clamp is what matters and it is
            // observable without paying for it.
            Assert.True(FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs > 0);

            var w = System.Diagnostics.Stopwatch.StartNew();
            // Stopped straight away, so this returns on the first poll and the
            // test costs nothing; what it proves is that a zero pacing did not
            // sail through the loop and answer "fine to key".
            bool ready = FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                countdown: null, stopRequested: () => true, keyUpAtMs: 0);
            w.Stop();

            Assert.False(ready, "a stop during the count still said it was fine to key");
        }

        /// <summary>
        /// The host publishes the countdown pacing to BOTH keying boundaries,
        /// and derives it from the sound rather than restating it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A parameter nobody supplies is the same class of defect as a shared
        /// check nobody calls: it compiles, it runs, every test passes, and the
        /// behaviour silently falls back. Here the fallback is a safety
        /// behaviour — the operator's abort window — so it is worth a guard of
        /// its own.
        /// </para>
        /// <para>
        /// <b>What this replaced.</b> The key-up moment was a constant in the
        /// radio layer with a remark naming the earcon's step length; the step
        /// length changed and the constant did not, so every keying stage raised
        /// RF halfway through its own warning for months. Nothing in either
        /// assembly could see the other's number. This asserts the seam that
        /// makes that impossible rather than the value, which is the point —
        /// a value would have to be restated here and would drift the same way.
        /// </para>
        /// </remarks>
        [Fact]
        public void The_host_publishes_the_countdown_pacing_to_both_keying_boundaries()
        {
            string dialog = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "FixerDialog.cs"));

            // Both boundaries — the tune carrier and the two audio stages.
            int supplied = 0;
            int from = 0;
            while (true)
            {
                int at = dialog.IndexOf("countdownKeyUpAtMs:", from, StringComparison.Ordinal);
                if (at < 0) break;
                supplied++;
                from = at + 1;
            }
            Assert.True(supplied >= 2,
                "the countdown pacing reaches " + supplied + " of the two keying boundaries, so "
                + "at least one of them is falling back to the conservative default");

            // And it is DERIVED, not restated. The rule is "the start of the
            // countdown's final element", asked of the figure itself.
            Assert.Contains("EarconPlayer.CountdownSteps(", dialog, StringComparison.Ordinal);
        }

        [Fact]
        public void A_stop_hook_that_throws_reads_as_stop()
        {
            // Carrying on past a broken stop path is the one direction this
            // must not fail in.
            bool ready = FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                countdown: null,
                stopRequested: () => throw new InvalidOperationException("broken"));
            Assert.False(ready);
        }

        // ---- helpers ----

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }

        private static System.Collections.Generic.IEnumerable<FixerTransmitGate.Decision>
            EveryRefusal()
        {
            yield return ReadyGate().Request(Run, "s1", false, true, false);
            yield return ReadyGate().Request(Run, "s1", true, false, false);
            yield return ReadyGate().Request(Run, "s1", true, true, true);
            yield return ReadyGate().Request("TX-OLD1", "s1", true, true, false);

            var noRun = new FixerTransmitGate();
            yield return noRun.Request(Run, "s1", true, true, false);

            var noLoad = new FixerTransmitGate();
            noLoad.BeginRun(Run);
            yield return noLoad.Request(Run, "s1", true, true, false);

            var aborted = ReadyGate();
            aborted.AbortRun();
            yield return aborted.Request(Run, "s1", true, true, false);

            var once = ReadyGate();
            once.Request(Run, "s1", true, true, false);
            once.NoteKeyed("s1");
            once.NoteUnkeyed();
            yield return once.Request(Run, "s1", true, true, false);

            var burst = ReadyGate();
            for (int i = 0; i < FixerTransmitGate.BurstLimit; i++)
            {
                burst.Request(Run, "b" + i, true, true, false);
                burst.NoteKeyed("b" + i);
                burst.NoteUnkeyed();
            }
            yield return burst.Request(Run, "b-next", true, true, false);
        }
    }
}
