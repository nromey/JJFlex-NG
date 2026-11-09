using System;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests
{
    /// <summary>
    /// No callsign, no transmit (#335), repair one: the Fix tool's transmit
    /// stages are refused BEFORE their gate and their countdown, with the
    /// sentence the operator heard on the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this the stages were refused only at the radio's chokepoint,
    /// after the countdown, behind a raise that returned true, so a stage
    /// waited for the radio and then wrote that the radio "was asked to
    /// transmit and never reported doing so", which was untrue: nothing was
    /// asked. The raise itself is held in <c>TransmitKillSwitchTests</c>.
    /// </para>
    /// <para>
    /// The rig is the real FlexBase on the vendor fake radio (as in
    /// <c>CallsignGateTests</c>), handed a callsign reader; nothing here gets
    /// as far as a key. The callsign is the conventional placeholder N0CALL.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FixerNoCallsignTests : IDisposable
    {
        private const string Run = "TX-CALLSIGN";
        private readonly RadioConfigStaticsScope _scope = new(nameof(FixerNoCallsignTests));
        private readonly RadioCase _case = new();
        private string _call = "";
        private readonly FlexBase _rig;
        private bool _counted;

        public FixerNoCallsignTests()
        {
            Lexicon.Load(Lexicon.Partitions);
            _rig = _case.F.Rig;
            _rig.Callouts.GetOperatorCallsign = () => _call;
        }

        public void Dispose()
        {
            _case.Dispose();
            _scope.Dispose();
        }

        private int Sent(string fragment)
        {
            int n = 0;
            foreach (string c in _case.F.Vendor.Transport.Commands)
                if (c.Contains(fragment, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        private static FixerTransmitGate ReadyGate()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("50 ohm dummy load on ANT1", FixerLoadKind.DummyLoad);
            return g;
        }

        // The countdown is on and short; a stop asked once it has played ends
        // the stage there, so a positive control never reaches a key.
        private TxTuneProbe.Result TuneStage(FixerTransmitGate gate) =>
            FixerTransmitBoundary.ProbeTransmitter(gate, () => _rig, TransmitStageSet.TransmitterCheck,
                countdown: () => _counted = true, stopRequested: () => _counted,
                countdownKeyUpAtMs: 50, countdownOn: () => true)();

        private FixerTransmitAudioBoundary Audio(FixerTransmitGate gate) =>
            FixerTransmitAudioBoundary.Create(gate, () => _rig,
                stopRequested: () => _counted, countdown: () => _counted = true,
                countdownKeyUpAtMs: 50, countdownDurationMs: 60, countdownOn: () => true);

        [Fact]
        public void The_tune_stage_is_refused_before_its_countdown_with_the_sentence_heard()
        {
            FixerTransmitGate gate = ReadyGate();
            TxTuneProbe.Result r = TuneStage(gate);

            Assert.Equal(TxTuneProbe.Verdict.NotRun, r.Verdict);
            Assert.Equal(TxTuneProbe.SkipReason.RefusedByHost, r.Skipped);
            Assert.Equal(FlexBase.NoCallsignRefusal, r.SkipDetail);
            Assert.False(_counted, "the stage counted down before it was refused");
            Assert.Equal(0, gate.TransmitCount);
            Assert.Equal(0, Sent("tune"));
        }

        [Fact]
        public void With_a_callsign_the_tune_stage_counts_down_as_before()
        {
            _call = "N0CALL";
            TxTuneProbe.Result r = TuneStage(ReadyGate());
            Assert.True(_counted, "the positive control never reached its countdown: " + r.SkipDetail);
            Assert.NotEqual(FlexBase.NoCallsignRefusal, r.SkipDetail);
        }

        [Fact]
        public void The_injected_stage_is_refused_before_its_gate_with_the_sentence_heard()
        {
            FixerTransmitGate gate = ReadyGate();
            InjectedTransmitFacts f = Audio(gate).InjectedTransmit(TransmitStageSet.InjectedTransmit)();

            Assert.Equal(FlexBase.NoCallsignRefusal, f.Detail);
            Assert.Empty(f.Probes);
            Assert.False(_counted);
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void The_spoken_stage_is_refused_before_its_gate_with_the_sentence_heard()
        {
            FixerTransmitGate gate = ReadyGate();
            SpokenTransmitFacts f = Audio(gate).SpokenTransmit(TransmitStageSet.SpokenTransmit)();

            Assert.Equal(FlexBase.NoCallsignRefusal, f.Detail);
            Assert.False(f.Attempted);
            Assert.False(_counted);
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void With_a_callsign_the_audio_stages_go_on_past_the_question()
        {
            // The positive control: the same stages on the same rig, a callsign
            // entered. They go on to their next refusal (this rig's audio path
            // is shut, or the gate's own), which is not the callsign sentence.
            _call = "N0CALL";
            InjectedTransmitFacts inj = Audio(ReadyGate()).InjectedTransmit(TransmitStageSet.InjectedTransmit)();
            SpokenTransmitFacts spk = Audio(ReadyGate()).SpokenTransmit(TransmitStageSet.SpokenTransmit)();
            Assert.False(string.IsNullOrEmpty(inj.Detail));
            Assert.NotEqual(FlexBase.NoCallsignRefusal, inj.Detail);
            Assert.NotEqual(FlexBase.NoCallsignRefusal, spk.Detail);
        }
    }
}
