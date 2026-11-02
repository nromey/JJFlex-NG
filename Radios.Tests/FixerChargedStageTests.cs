using System;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #858, repair round one: a refusal is not a result, and a stage the gate
    /// has charged keeps its way to run again however its last attempt ended.
    /// Driven through the real gate, engine and page. The host below keys
    /// nothing: it asks the gate exactly as the boundary does and counts the
    /// grants, which is what "keyed twice" would be.
    /// </summary>
    public sealed class FixerChargedStageTests
    {
        private const string Stage = TransmitStageSet.SpokenTransmit;

        private sealed class Bench
        {
            public readonly FixerTransmitGate Gate = new FixerTransmitGate();
            public readonly FixerRun Run;
            public int Keys;
            public Action AfterKeying;

            public Bench()
            {
                Run = new FixerRun(TransmitStageSet.Build(new TransmitStageSet.Hosts
                {
                    RunSpokenTransmit = Spoken,
                }));
                Gate.BeginRun(Run.RunId);
                Gate.DeclareLoad("A dummy load", FixerLoadKind.DummyLoad);
            }

            private SpokenTransmitFacts Spoken()
            {
                FixerTransmitGate.Decision d = Gate.Request(Run.RunId, Stage, stageTransmits: true,
                    radioReachable: true, rigIsKeyed: false, transmitPowerWatts: 25);
                if (!d.Allowed) return new SpokenTransmitFacts { Detail = d.Explanation };

                Gate.NoteKeyed(Stage);
                Keys++;
                Gate.NoteUnkeyed();
                AfterKeying?.Invoke();
                return new SpokenTransmitFacts { Keyed = true, Attempted = true, ReachedRadio = true };
            }

            public string Page() => FixerPage.Render(Run, new FixerPageState
            {
                SelectedStageId = Stage,
                ChargedStages = Gate.ChargedStages,
            });
        }

        private const string RunAgain = "data-action=\"rerun\" data-arg=\"" + Stage + "\"";

        [Fact]
        public void A_stale_plain_run_leaves_the_measurement_standing_and_keys_nothing()
        {
            var b = new Bench();
            FixerStageResult measured = b.Run.RunStage(Stage);
            Assert.Equal(FixerStageStatus.Ran, measured.Status);
            Assert.Equal(1, b.Keys);

            FixerStageResult again = b.Run.RunStage(Stage);

            Assert.Same(measured, again);
            Assert.Same(measured, b.Run.ResultFor(Stage));
            Assert.Equal(1, b.Keys);
            Assert.Contains("already transmitted", b.Run.TakeRefusal(Stage), StringComparison.Ordinal);
            Assert.Null(b.Run.TakeRefusal(Stage)); // said once
            Assert.Contains(RunAgain, b.Page(), StringComparison.Ordinal);
        }

        [Fact]
        public void Only_the_explicit_gesture_grants_another_key()
        {
            var b = new Bench();
            b.Run.RunStage(Stage);
            b.Run.RunStage(Stage);
            b.Run.RunStage(Stage);
            Assert.Equal(1, b.Keys); // positive control: plain runs are refused

            b.Gate.AllowReRun(Stage);
            FixerStageResult second = b.Run.RunStage(Stage);

            Assert.Equal(2, b.Keys);
            Assert.True(second.WasReRun);
        }

        [Fact]
        public void A_stage_that_keyed_and_then_failed_still_offers_Run_again()
        {
            var b = new Bench { AfterKeying = () => throw new InvalidOperationException("the walk fell over") };

            FixerStageResult failed = b.Run.RunStage(Stage);

            Assert.Equal(FixerStageStatus.CouldNotRun, failed.Status);
            Assert.Contains(RunAgain, b.Page(), StringComparison.Ordinal);

            // And it is still charged: the plain gesture does not key.
            b.AfterKeying = null;
            b.Run.RunStage(Stage);
            Assert.Equal(1, b.Keys);

            b.Gate.AllowReRun(Stage);
            Assert.Equal(FixerStageStatus.Ran, b.Run.RunStage(Stage).Status);
            Assert.Equal(2, b.Keys);
        }

        [Fact]
        public void A_stage_that_never_keyed_keeps_the_plain_control()
        {
            var b = new Bench();
            b.Gate.DeclareLoad("", FixerLoadKind.None); // the gate refuses before any key

            FixerStageResult refused = b.Run.RunStage(Stage);

            Assert.Equal(FixerStageStatus.CouldNotRun, refused.Status);
            Assert.Equal(0, b.Keys);
            string page = b.Page();
            Assert.DoesNotContain(RunAgain, page, StringComparison.Ordinal);
            Assert.Contains("data-action=\"run\" data-arg=\"" + Stage + "\"", page, StringComparison.Ordinal);
        }

        [Fact]
        public void An_injected_stage_that_keyed_and_failed_stays_Ran_with_a_finding()
        {
            // The boundary's catch keeps the facts it had, so a fault after the
            // key arrives as Keyed with no probes, and that is not "never keyed".
            FixerOutcome o = TransmitStages.Injected(new InjectedTransmitFacts
            {
                Keyed = true,
                Detail = "The injected check failed unexpectedly: the walk fell over",
            });

            Assert.False(o.NothingMeasured);
            Assert.Contains(o.Findings, f => f.Id == "tx-too-few-probes");
            Assert.Contains("the walk fell over", o.Answer, StringComparison.Ordinal);
        }

        [Fact]
        public void A_failed_stage_puts_its_reason_in_the_report_and_not_its_stack()
        {
            const string secret = @"C:\Users\someone\private\inner.txt";
            var run = new FixerRun(TransmitStageSet.Build(new TransmitStageSet.Hosts
            {
                RunSpokenTransmit = () => throw new InvalidOperationException(
                    "the reason", new Exception(secret)),
            }));

            run.RunStage(Stage);

            string plain = FixerReport.PlainText(run);
            string html = FixerReport.HtmlFragment(run);
            Assert.Contains("the reason", plain, StringComparison.Ordinal); // positive control
            Assert.DoesNotContain(secret, plain, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(A_failed_stage_puts_its_reason_in_the_report_and_not_its_stack), plain, StringComparison.Ordinal);
        }

        [Fact]
        public void A_refused_stage_keeps_its_evidence_in_the_report()
        {
            var run = new FixerRun(TransmitStageSet.Build(new TransmitStageSet.Hosts
            {
                RunSpokenTransmit = () => new SpokenTransmitFacts { Detail = "Nothing was transmitted. The path is shut." },
            }));

            run.RunStage(Stage);

            Assert.Contains("Reached the radio: not measured", FixerReport.PlainText(run), StringComparison.Ordinal);
        }
    }
}
