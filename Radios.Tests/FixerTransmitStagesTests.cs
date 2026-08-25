using System;
using System.Linq;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The host's transmitting stage executor: the only place that both
    /// consults the gate and calls something that keys a radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A gate nothing consults is not a gate.</b> These tests exist because
    /// the guard being correct and the guard being ON THE PATH are two
    /// different claims, and only the second one protects anybody. That is the
    /// same failure this whole tool was built to expose, turned on the tool.
    /// </para>
    /// <para>
    /// Decisions are taken from real gates rather than constructed, because the
    /// factories are internal — which turns out to be the better test anyway:
    /// it exercises the actual pairing rather than a hand-made stand-in.
    /// </para>
    /// </remarks>
    public class FixerTransmitStagesTests
    {
        private const string Run = "TX-4K2M";

        private static FixerTransmitGate ReadyGate()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("50 ohm dummy load on ANT1");
            return g;
        }

        private static FixerStage TransmittingStage(string id = "transmitter-check")
            => new FixerStage { Id = id, Number = 2, Title = "Transmitter check", Transmits = true };

        private static FixerStageContext Ctx(FixerStage stage, string runId = Run)
            => new FixerStageContext(runId, stage, default, null);

        // ---- wiring: a half-wired host keys nothing ----

        [Fact]
        public void With_no_gate_there_is_no_executor()
        {
            // Null is the engine's "the host wired nothing" signal, and it
            // records the stage as unable to run. For a transmitting stage that
            // null is the thing standing between a half-wired host and a keyed
            // radio.
            Assert.Null(FixerTransmitStages.TransmitterCheck(null, () => null));
        }

        [Fact]
        public void With_no_way_to_reach_a_radio_there_is_no_executor()
        {
            Assert.Null(FixerTransmitStages.TransmitterCheck(new FixerTransmitGate(), null));
        }

        [Fact]
        public void A_fully_wired_host_gets_an_executor()
        {
            // The positive control: without it, both tests above would pass on
            // a factory that always returned null.
            Assert.NotNull(FixerTransmitStages.TransmitterCheck(ReadyGate(), () => null));
        }

        // ---- the gate is actually consulted ----

        [Fact]
        public void The_executor_asks_the_gate_and_reports_its_refusal()
        {
            // No radio, so the gate refuses before anything can key. What is
            // being tested is that the refusal reached the operator at all: a
            // stage that returns a blank outcome when it was refused is the
            // silent failure, restated.
            var gate = ReadyGate();
            var run = FixerTransmitStages.TransmitterCheck(gate, () => null);

            FixerOutcome o = run(Ctx(TransmittingStage()));

            Assert.False(string.IsNullOrWhiteSpace(o.Answer));
            Assert.Contains("radio is not reachable", o.Answer, StringComparison.OrdinalIgnoreCase);
            Assert.Single(o.Findings);
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void A_radio_source_that_throws_is_treated_as_no_radio_not_as_a_crash()
        {
            // The Fixer Tool only ever opens when something is already wrong, so
            // the thing it asks for the radio is exactly the thing likely to
            // throw. Falling over here would take away the diagnosis at the
            // moment it was wanted.
            var gate = ReadyGate();
            var run = FixerTransmitStages.TransmitterCheck(
                gate, () => throw new InvalidOperationException("no connection"));

            FixerOutcome o = run(Ctx(TransmittingStage()));

            Assert.Contains("radio is not reachable", o.Answer, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void A_stage_that_did_not_declare_itself_transmitting_is_refused_by_the_executor()
        {
            var gate = ReadyGate();
            var stage = TransmittingStage();
            stage.Transmits = false;

            FixerOutcome o = FixerTransmitStages.TransmitterCheck(gate, () => null)(Ctx(stage));

            Assert.Contains("not meant to transmit", o.Answer, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_stale_run_id_is_refused_by_the_executor()
        {
            var gate = ReadyGate();
            FixerOutcome o = FixerTransmitStages
                .TransmitterCheck(gate, () => null)(Ctx(TransmittingStage(), "TX-OLD1"));

            Assert.Contains("earlier test", o.Answer, StringComparison.OrdinalIgnoreCase);
        }

        // ---- a refusal is reported, not swallowed ----

        [Fact]
        public void Every_refusal_produces_an_outcome_with_words_and_exactly_one_finding()
        {
            foreach (FixerTransmitGate.Decision d in EveryRefusal())
            {
                FixerOutcome o = FixerTransmitStages.OutcomeForRefusal(d);

                Assert.False(string.IsNullOrWhiteSpace(o.Answer));
                Assert.Single(o.Findings);
                Assert.False(string.IsNullOrWhiteSpace(o.Evidence));
            }
        }

        [Fact]
        public void A_refusal_uses_the_gates_own_words_unchanged()
        {
            // Two descriptions of one refusal drift apart, and the operator ends
            // up hearing one thing and mailing FlexRadio another.
            foreach (FixerTransmitGate.Decision d in EveryRefusal())
                Assert.Equal(d.Explanation, FixerTransmitStages.OutcomeForRefusal(d).Answer);
        }

        [Fact]
        public void A_refusal_says_in_the_evidence_that_nothing_ran()
        {
            // The report has to be readable by someone who was not there. A
            // stage with no measurement and no note reads as an omission.
            foreach (FixerTransmitGate.Decision d in EveryRefusal())
                Assert.StartsWith("Not run.", FixerTransmitStages.OutcomeForRefusal(d).Evidence);
        }

        [Fact]
        public void Faults_in_our_software_are_not_dressed_up_as_operator_actions()
        {
            // Telling somebody to do something about a bug in our repeat guard
            // would be inventing an action they do not have.
            var gate = ReadyGate();
            for (int i = 0; i < FixerTransmitGate.BurstLimit; i++)
            {
                gate.Request(Run, "s" + i, true, true, false);
                gate.NoteKeyed("s" + i);
                gate.NoteUnkeyed();
            }

            FixerTransmitGate.Decision tooFast = gate.Request(Run, "next", true, true, false);
            Assert.Equal(FixerTransmitGate.Refusal.TooFast, tooFast.Why);

            FixerFinding f = FixerTransmitStages.OutcomeForRefusal(tooFast).Findings.Single();
            Assert.Equal(FixOwner.NobodyHere, f.Owner);
        }

        [Fact]
        public void Things_the_operator_really_can_put_right_are_theirs_to_put_right()
        {
            var noLoad = new FixerTransmitGate();
            noLoad.BeginRun(Run);

            FixerFinding f = FixerTransmitStages
                .OutcomeForRefusal(noLoad.Request(Run, "s1", true, true, false))
                .Findings.Single();

            Assert.Equal(FixOwner.Operator, f.Owner);
            Assert.Contains("antenna socket", f.WhatToDo, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void No_refusal_finding_offers_a_button_there_is_nothing_behind()
        {
            // FixerFinding enforces this in its constructor, so what is really
            // asserted is that none of these is misclassified as ours to fix —
            // a button we cannot honour is worse than no button.
            foreach (FixerTransmitGate.Decision d in EveryRefusal())
            {
                FixerFinding f = FixerTransmitStages.OutcomeForRefusal(d).Findings.Single();
                Assert.NotEqual(FixOwner.Us, f.Owner);
                Assert.Equal("", f.FixActionId);
            }
        }

        [Fact]
        public void Refusal_findings_have_distinct_ids_so_a_report_can_tell_them_apart()
        {
            var ids = EveryRefusal()
                .Select(d => FixerTransmitStages.OutcomeForRefusal(d).Findings.Single().Id)
                .ToList();

            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // ---- a measurement becomes an answer, in one vocabulary ----

        [Fact]
        public void A_working_transmitter_produces_no_findings_at_all()
        {
            // A clean stage that manufactures a finding to look thorough is
            // noise in a report somebody reads while something is broken.
            FixerOutcome o = FixerTransmitStages.OutcomeFor(Ran(TxTuneProbe.Verdict.MakesPower), "dummy load");
            Assert.Empty(o.Findings);
            Assert.False(string.IsNullOrWhiteSpace(o.Answer));
        }

        [Fact]
        public void No_power_is_critical_and_says_it_is_not_the_microphone()
        {
            // The single most important sentence the tool says: stop looking
            // where you were looking.
            FixerFinding f = FixerTransmitStages
                .OutcomeFor(Ran(TxTuneProbe.Verdict.NoPower), "dummy load").Findings.Single();

            Assert.True(f.Critical);
            Assert.Contains("microphone", f.WhatIsWrong, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Only_the_no_power_verdict_is_critical()
        {
            // Critical interrupts. Making everything critical makes nothing
            // critical, and an assertive live region that fires constantly is
            // one an operator learns to ignore.
            foreach (TxTuneProbe.Verdict v in new[]
            {
                TxTuneProbe.Verdict.MakesPower,
                TxTuneProbe.Verdict.MakesPowerLoadSuspect,
                TxTuneProbe.Verdict.NoForwardPowerMeter,
            })
                Assert.DoesNotContain(FixerTransmitStages.OutcomeFor(Ran(v), "load").Findings,
                                      f => f.Critical);
        }

        [Fact]
        public void A_missing_meter_is_a_gap_in_the_measurement_not_a_fault_in_the_station()
        {
            FixerFinding f = FixerTransmitStages
                .OutcomeFor(Ran(TxTuneProbe.Verdict.NoForwardPowerMeter), "load").Findings.Single();

            Assert.Equal(FixOwner.NobodyHere, f.Owner);
        }

        [Fact]
        public void The_answer_is_the_probes_own_explanation_and_not_a_second_one()
        {
            // One measurement, one vocabulary. Two would drift.
            foreach (TxTuneProbe.Verdict v in (TxTuneProbe.Verdict[])
                     Enum.GetValues(typeof(TxTuneProbe.Verdict)))
            {
                TxTuneProbe.Result r = v == TxTuneProbe.Verdict.NotRun
                    ? TxTuneProbe.Result.NotRun(TxTuneProbe.SkipReason.LoadNotDeclared)
                    : Ran(v);

                Assert.Equal(TxTuneProbe.Explain(r),
                             FixerTransmitStages.OutcomeFor(r, "load").Answer);
            }
        }

        [Fact]
        public void The_operators_own_words_about_the_load_reach_the_evidence()
        {
            // FlexRadio will ask what the measurement was taken into, and a
            // power reading with no stated load cannot be read by anyone later,
            // us included.
            FixerOutcome o = FixerTransmitStages
                .OutcomeFor(Ran(TxTuneProbe.Verdict.MakesPower), "50 ohm dummy load on ANT1");

            Assert.Contains("50 ohm dummy load on ANT1", o.Evidence);
        }

        [Fact]
        public void An_unstated_load_adds_no_line_rather_than_an_empty_one()
        {
            FixerOutcome o = FixerTransmitStages.OutcomeFor(Ran(TxTuneProbe.Verdict.MakesPower), "");
            Assert.DoesNotContain("as stated by the operator", o.Evidence);
        }

        [Fact]
        public void The_measurement_itself_is_handed_on_for_a_later_stage_to_read()
        {
            // Stage four reads this to know whether the transmitter had already
            // been proved good, which changes entirely what an audio failure
            // means.
            FixerOutcome o = FixerTransmitStages.OutcomeFor(Ran(TxTuneProbe.Verdict.MakesPower), "load");
            Assert.IsType<TxTuneProbe.Result>(o.Payload);
        }

        [Fact]
        public void Every_verdict_produces_an_answer_worth_hearing()
        {
            foreach (TxTuneProbe.Verdict v in (TxTuneProbe.Verdict[])
                     Enum.GetValues(typeof(TxTuneProbe.Verdict)))
            {
                TxTuneProbe.Result r = v == TxTuneProbe.Verdict.NotRun
                    ? TxTuneProbe.Result.NotRun(TxTuneProbe.SkipReason.RadioNotReachable)
                    : Ran(v);

                FixerOutcome o = FixerTransmitStages.OutcomeFor(r, "load");
                Assert.False(string.IsNullOrWhiteSpace(o.Answer), v + " said nothing");
            }
        }

        // ---- helpers ----

        private static TxTuneProbe.Result Ran(TxTuneProbe.Verdict v)
            => TxTuneProbe.Result.Ran(v, DateTime.UtcNow,
                                      Array.Empty<TxTuneProbe.Reading>(),
                                      10, double.NaN, false,
                                      "14.200.000", "USB", "ANT1");

        private static System.Collections.Generic.IEnumerable<FixerTransmitGate.Decision>
            EveryRefusal()
        {
            yield return ReadyGate().Request(Run, "s1", false, true, false);   // not a transmit stage
            yield return ReadyGate().Request(Run, "s1", true, false, false);   // no radio
            yield return ReadyGate().Request(Run, "s1", true, true, true);     // already keyed
            yield return ReadyGate().Request("TX-OLD1", "s1", true, true, false); // stale run

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
