using System;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The transmit-audio boundary: the gate is consulted before anything
    /// keys for stages 3 and 4, and a refusal comes back as facts that
    /// honestly say nothing was measured — carrying the gate's own words.
    /// </summary>
    /// <remarks>
    /// Everything here runs against a null radio, which is the point: every
    /// refusal path must be provable without a transmitter in the room, and
    /// the assertion that matters most is the one on
    /// <see cref="FixerTransmitGate.TransmitCount"/> — nothing keyed.
    /// The keyed paths cannot be tested without a radio and are not
    /// pretended at; their decisions live in <see cref="TxAudioProbe"/> and
    /// are tested there.
    /// </remarks>
    public class FixerTransmitAudioBoundaryTests
    {
        private const string Run = "TX-7Q9Z";

        private static FixerTransmitGate ReadyGate()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("50 ohm dummy load on ANT1", FixerLoadKind.DummyLoad);
            return g;
        }

        private static FixerTransmitAudioBoundary Wired(FixerTransmitGate gate,
                                                        FixerTransmitBoundary.RadioSource radio)
            => FixerTransmitAudioBoundary.Create(gate, radio);

        // ---- wiring: a half-wired host keys nothing ----

        [Fact]
        public void With_no_gate_there_is_no_boundary()
        {
            Assert.Null(FixerTransmitAudioBoundary.Create(null, () => null));
        }

        [Fact]
        public void With_no_way_to_reach_a_radio_there_is_no_boundary()
        {
            Assert.Null(FixerTransmitAudioBoundary.Create(ReadyGate(), null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void With_no_stage_to_charge_it_to_there_is_no_measurement(string stageId)
        {
            FixerTransmitAudioBoundary b = Wired(ReadyGate(), () => null);
            Assert.Null(b.InjectedTransmit(stageId));
            Assert.Null(b.SpokenTransmit(stageId));
        }

        [Fact]
        public void A_fully_wired_host_gets_both_measurements()
        {
            // The positive control for the nulls above.
            FixerTransmitAudioBoundary b = Wired(ReadyGate(), () => null);
            Assert.NotNull(b.InjectedTransmit(TransmitStageSet.InjectedTransmit));
            Assert.NotNull(b.SpokenTransmit(TransmitStageSet.SpokenTransmit));
        }

        // ---- the gate is actually consulted ----

        [Fact]
        public void The_injected_stage_asks_the_gate_and_nothing_keys_when_it_says_no()
        {
            FixerTransmitGate gate = ReadyGate();
            InjectedTransmitFacts f = Wired(gate, () => null)
                .InjectedTransmit(TransmitStageSet.InjectedTransmit)();

            Assert.Empty(f.Probes);
            Assert.Equal(0, gate.TransmitCount);
            Assert.Equal(0, gate.KeyDownSeconds);
        }

        [Fact]
        public void The_spoken_stage_asks_the_gate_and_nothing_keys_when_it_says_no()
        {
            FixerTransmitGate gate = ReadyGate();
            SpokenTransmitFacts f = Wired(gate, () => null)
                .SpokenTransmit(TransmitStageSet.SpokenTransmit)();

            Assert.False(f.Attempted);
            Assert.False(f.ReachedRadio);
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void A_refusal_carries_the_gates_own_words_into_the_facts()
        {
            // The gate writes every refusal to be spoken as it stands, so the
            // boundary hands its words over VERBATIM — not paraphrased, not
            // prefixed, not wrapped. With no radio the refusal here is
            // NoRadio; the contract asserted is that whichever refusal fired,
            // its exact sentence is what reaches the facts.
            var gate = new FixerTransmitGate();
            gate.BeginRun(Run);   // deliberately no load declared

            InjectedTransmitFacts inj = Wired(gate, () => null)
                .InjectedTransmit(TransmitStageSet.InjectedTransmit)();
            SpokenTransmitFacts spk = Wired(gate, () => null)
                .SpokenTransmit(TransmitStageSet.SpokenTransmit)();

            FixerTransmitGate.Decision d = gate.Request(
                Run, "probe", stageTransmits: true,
                radioReachable: false, rigIsKeyed: false);

            Assert.Equal(d.Explanation, inj.Detail);
            Assert.Equal(d.Explanation, spk.Detail);
        }

        [Fact]
        public void A_radio_source_that_throws_is_treated_as_no_radio_not_as_a_crash()
        {
            FixerTransmitGate gate = ReadyGate();
            FixerTransmitAudioBoundary b = Wired(gate,
                () => throw new InvalidOperationException("no connection"));

            InjectedTransmitFacts inj = b.InjectedTransmit(TransmitStageSet.InjectedTransmit)();
            SpokenTransmitFacts spk = b.SpokenTransmit(TransmitStageSet.SpokenTransmit)();

            Assert.Equal(0, gate.TransmitCount);
            Assert.Contains("radio is not reachable", inj.Detail);
            Assert.Contains("radio is not reachable", spk.Detail);
        }

        [Fact]
        public void An_abandoned_run_keys_nothing()
        {
            FixerTransmitGate gate = ReadyGate();
            gate.AbortRun();
            FixerTransmitAudioBoundary b = Wired(gate, () => null);

            InjectedTransmitFacts inj = b.InjectedTransmit(TransmitStageSet.InjectedTransmit)();
            SpokenTransmitFacts spk = b.SpokenTransmit(TransmitStageSet.SpokenTransmit)();

            Assert.Equal(0, gate.TransmitCount);
            Assert.Contains("stopped", inj.Detail);
            Assert.Contains("stopped", spk.Detail);
        }

        // ---- refused facts stay honest all the way to the operator ----

        [Fact]
        public void Refused_injected_facts_leave_the_conditioning_state_unknown_with_no_radio()
        {
            InjectedTransmitFacts f = Wired(ReadyGate(), () => null)
                .InjectedTransmit(TransmitStageSet.InjectedTransmit)();

            // "Could not be read" is the honest state, and the engine's
            // explanation must then hedge rather than name a setting.
            Assert.Null(f.ConditioningActive);
        }

        [Fact]
        public void The_engine_reads_a_refusal_as_nothing_to_compare_never_as_a_result()
        {
            // The whole path: refused facts through the engine's own words.
            InjectedTransmitFacts f = Wired(ReadyGate(), () => null)
                .InjectedTransmit(TransmitStageSet.InjectedTransmit)();
            FixerOutcome o = TransmitStages.Injected(f);

            Assert.Contains("nothing to compare", o.Answer);
            Assert.Contains("nothing was transmitted", o.Evidence);
        }

        [Fact]
        public void The_spoken_engine_reading_of_a_refusal_says_it_cannot_be_said_either_way()
        {
            SpokenTransmitFacts f = Wired(ReadyGate(), () => null)
                .SpokenTransmit(TransmitStageSet.SpokenTransmit)();
            FixerOutcome o = TransmitStages.Spoken(f, micBaseline: null);

            Assert.Contains("cannot be said either way", o.Answer);
            Assert.Contains("nothing was transmitted", o.Evidence);
        }
    }
}
