using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The run rules: one test ID stamped on everything, a timestamp on every
    /// result, skips recorded with their reason, re-runs replacing and saying
    /// so, fixes recorded loudly, and honest refusals instead of improvised
    /// measurements.
    /// </summary>
    public class FixerRunTests
    {
        // A tiny domain of its own, defined here as data — which is also the
        // point: the engine under test never learns what "boil" means.
        private static FixerStageSet KettleSet(
            Func<FixerStageContext, FixerOutcome>? fill = null,
            Func<FixerStageContext, FixerOutcome>? boil = null,
            IReadOnlyDictionary<string, FixerFixAction>? fixes = null)
        {
            return new FixerStageSet("kettle", "Kettle", "Start with water.",
                new[]
                {
                    new FixerStage
                    {
                        Id = "fill", Number = 0, Title = "Fill",
                        Question = "Is there water in the kettle?",
                        SkipChoices = new[]
                        {
                            new FixerSkipChoice("no-tap", "There is no tap here.",
                                FixerSkipEffect.LeavesQuestionOpen,
                                "With no tap, whether the kettle holds water is left open."),
                            new FixerSkipChoice("later", "I'll do it later.",
                                FixerSkipEffect.OperatorChoice,
                                "The answer is weaker for it."),
                        },
                        Execute = fill,
                    },
                    new FixerStage
                    {
                        Id = "boil", Number = 1, Title = "Boil",
                        Question = "Does the kettle boil?",
                        Transmits = true, // stands in for "does something irreversible"
                        SkipChoices = new[]
                        {
                            new FixerSkipChoice("later", "I'll do it later.",
                                FixerSkipEffect.OperatorChoice,
                                "The answer is weaker for it."),
                        },
                        Execute = boil,
                    },
                },
                fixes ?? new Dictionary<string, FixerFixAction>());
        }

        private static Func<FixerStageContext, FixerOutcome> Answering(string answer)
            => _ => new FixerOutcome { Answer = answer };

        private static Func<DateTime> TickingClock(DateTime start, TimeSpan step)
        {
            DateTime t = start;
            return () => { DateTime now = t; t += step; return now; };
        }

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // ---- one test ID per run, stamped on everything ----

        [Fact]
        public void Every_result_carries_the_runs_id()
        {
            var run = new FixerRun(KettleSet(Answering("wet"), Answering("hot")));

            run.RunStage("boil");
            run.RunStage("fill");
            run.SkipStage("boil", "later");

            Assert.NotEmpty(run.RunId);
            foreach (FixerStageResult r in run.ResultsInRunOrder)
                Assert.Equal(run.RunId, r.RunId);
        }

        [Fact]
        public void Fix_records_carry_the_runs_id_too()
        {
            FixerRun run = RunWithAFixableFinding(out _);
            FixerFixRecord fix = run.ApplyFix("fill", "dry");
            Assert.Equal(run.RunId, fix.RunId);
        }

        // ---- every result has its own timestamp ----

        [Fact]
        public void Each_result_is_stamped_with_its_own_time_not_the_runs()
        {
            var run = new FixerRun(KettleSet(Answering("wet"), Answering("hot")),
                                   TickingClock(T0, TimeSpan.FromMinutes(7)));

            FixerStageResult first = run.RunStage("fill");
            FixerStageResult second = run.RunStage("boil");

            Assert.NotEqual(first.AtUtc, second.AtUtc);
            Assert.True(second.AtUtc > first.AtUtc);
        }

        // ---- skips are recorded, with the reason, never blank ----

        [Fact]
        public void A_skip_is_recorded_with_its_reason_and_its_effect()
        {
            var run = new FixerRun(KettleSet());
            FixerStageResult r = run.SkipStage("fill", "no-tap");

            Assert.Equal(FixerStageStatus.Skipped, r.Status);
            Assert.NotNull(r.Skip);
            Assert.Equal("no-tap", r.Skip.Id);
            Assert.Contains(r.Skip.Label, r.Answer);
            Assert.Contains(r.Skip.EffectText, r.Answer);
        }

        [Fact]
        public void A_skip_reason_the_stage_never_offered_is_refused()
        {
            var run = new FixerRun(KettleSet());
            Assert.Throws<ArgumentException>(() => run.SkipStage("fill", "invented-reason"));
        }

        [Fact]
        public void Two_skip_reasons_produce_two_different_records()
        {
            // The two microphone skip reasons are the founding case: they do
            // different things to the conclusion, so they must never collapse
            // into one rendering.
            var a = new FixerRun(KettleSet()).SkipStage("fill", "no-tap");
            var b = new FixerRun(KettleSet()).SkipStage("fill", "later");

            Assert.NotEqual(a.Skip.Effect, b.Skip.Effect);
            Assert.NotEqual(a.Answer, b.Answer);
        }

        // ---- re-running replaces, and says so ----

        [Fact]
        public void Rerunning_a_stage_replaces_its_result_and_marks_the_rerun()
        {
            var run = new FixerRun(KettleSet(Answering("wet"), Answering("hot")));

            FixerStageResult first = run.RunStage("fill");
            FixerStageResult second = run.RunStage("fill");

            Assert.False(first.WasReRun);
            Assert.True(second.WasReRun);
            Assert.Same(second, run.ResultFor("fill"));
            Assert.Single(run.ResultsInRunOrder); // no stale twin survives
        }

        [Fact]
        public void Skipping_after_running_also_replaces()
        {
            var run = new FixerRun(KettleSet(Answering("wet"), Answering("hot")));
            run.RunStage("fill");
            FixerStageResult skip = run.SkipStage("fill", "later");

            Assert.True(skip.WasReRun);
            Assert.Equal(FixerStageStatus.Skipped, run.ResultFor("fill")!.Status);
        }

        // ---- the actual order is kept ----

        [Fact]
        public void Results_keep_the_order_things_were_actually_done_in()
        {
            var run = new FixerRun(KettleSet(Answering("wet"), Answering("hot")));
            run.RunStage("boil");
            run.RunStage("fill");

            string[] order = run.ResultsInRunOrder.Select(r => r.StageId).ToArray();
            Assert.Equal(new[] { "boil", "fill" }, order);
        }

        // ---- honest refusals ----

        [Fact]
        public void A_stage_with_no_executor_is_recorded_not_attempted_some_other_way()
        {
            // For a transmitting stage this IS the transmit boundary: no
            // delegate from the host, no key-down, and a record saying that
            // nothing was transmitted.
            var run = new FixerRun(KettleSet(fill: Answering("wet"), boil: null));
            FixerStageResult r = run.RunStage("boil");

            Assert.Equal(FixerStageStatus.CouldNotRun, r.Status);
            Assert.Contains("nothing was transmitted", r.Answer,
                            StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_executor_that_throws_is_recorded_rather_than_killing_the_run()
        {
            var run = new FixerRun(KettleSet(
                fill: _ => throw new InvalidOperationException("the tap fell off"),
                boil: Answering("hot")));

            FixerStageResult r = run.RunStage("fill");
            Assert.Equal(FixerStageStatus.CouldNotRun, r.Status);
            Assert.Contains("the tap fell off", r.Answer);

            // The run carries on afterwards.
            Assert.Equal(FixerStageStatus.Ran, run.RunStage("boil").Status);
        }

        [Fact]
        public void An_unknown_stage_is_a_programming_error_not_a_recorded_result()
        {
            var run = new FixerRun(KettleSet());
            Assert.Throws<ArgumentException>(() => run.RunStage("no-such-stage"));
        }

        // ---- a later stage can read an earlier one's result ----

        [Fact]
        public void A_stage_can_read_the_baseline_an_earlier_stage_recorded()
        {
            object? seenPayload = null;
            var set = KettleSet(
                fill: _ => new FixerOutcome { Answer = "wet", Payload = "half-full" },
                boil: ctx => { seenPayload = ctx.ResultFor("fill")?.Payload;
                               return new FixerOutcome { Answer = "hot" }; });

            var run = new FixerRun(set);
            run.RunStage("fill");
            run.RunStage("boil");

            Assert.Equal("half-full", seenPayload);
        }

        // ---- fixes: offered, acted on, recorded — never silent ----

        private static FixerRun RunWithAFixableFinding(out List<string> actionLog,
                                                       bool bindAction = true)
        {
            var log = new List<string>();
            actionLog = log;

            var fixes = new Dictionary<string, FixerFixAction>();
            if (bindAction)
                fixes["turn-tap"] = () => { log.Add("tap turned"); return FixerFixOutcome.Done("water flowing"); };

            var set = KettleSet(
                fill: _ => new FixerOutcome
                {
                    Answer = "dry",
                    Findings = new[]
                    {
                        new FixerFinding("dry", FixOwner.Us, "The kettle is dry.",
                                         "Turn the tap", "turn-tap"),
                    },
                },
                boil: Answering("hot"),
                fixes: fixes);

            var run = new FixerRun(set, TickingClock(T0, TimeSpan.FromMinutes(1)));
            run.RunStage("fill");
            return run;
        }

        [Fact]
        public void Applying_a_fix_invokes_the_bound_action_and_records_before_after_when()
        {
            FixerRun run = RunWithAFixableFinding(out List<string> log);
            FixerFixRecord fix = run.ApplyFix("fill", "dry");

            Assert.Equal(new[] { "tap turned" }, log);
            Assert.True(fix.Succeeded);
            Assert.Equal("The kettle is dry.", fix.WhatWasWrong);
            Assert.Equal("water flowing", fix.WhatItBecame);
            Assert.NotEqual(default, fix.AtUtc);
            Assert.Single(run.FixesApplied);
        }

        [Fact]
        public void A_fix_the_host_never_wired_is_recorded_as_failed_not_silently_dropped()
        {
            FixerRun run = RunWithAFixableFinding(out _, bindAction: false);
            FixerFixRecord fix = run.ApplyFix("fill", "dry");

            Assert.False(fix.Succeeded);
            Assert.NotEmpty(fix.WhatItBecame);
            Assert.Single(run.FixesApplied); // the failure is still on the record
        }

        [Fact]
        public void Stages_run_after_a_fix_are_identifiable()
        {
            // The report must be able to say which measurements were taken
            // against the changed configuration.
            FixerRun run = RunWithAFixableFinding(out _);
            FixerFixRecord fix = run.ApplyFix("fill", "dry");
            run.RunStage("boil");

            var after = run.ResultsAfter(fix);
            Assert.Single(after);
            Assert.Equal("boil", after[0].StageId);
        }

        [Fact]
        public void A_fix_can_only_be_applied_to_a_finding_we_own()
        {
            var set = KettleSet(
                fill: _ => new FixerOutcome
                {
                    Answer = "dry",
                    Findings = new[]
                    {
                        new FixerFinding("leak", FixOwner.Operator, "The kettle leaks.",
                                         "Buy a kettle that does not leak."),
                    },
                });
            var run = new FixerRun(set);
            run.RunStage("fill");

            Assert.Throws<InvalidOperationException>(() => run.ApplyFix("fill", "leak"));
        }

        // ---- the finding taxonomy holds by construction ----

        [Fact]
        public void A_finding_we_can_fix_must_name_its_action_and_others_must_not()
        {
            // Us without an action is a button with nothing behind it;
            // an action on anyone else is a fix nobody can press.
            Assert.Throws<ArgumentException>(() =>
                new FixerFinding("x", FixOwner.Us, "wrong", "do"));
            Assert.Throws<ArgumentException>(() =>
                new FixerFinding("x", FixOwner.Operator, "wrong", "do", "some-action"));
            Assert.Throws<ArgumentException>(() =>
                new FixerFinding("x", FixOwner.NobodyHere, "wrong", "do", "some-action"));

            // And every finding must say what to do, even when the answer is
            // that nothing here can.
            Assert.Throws<ArgumentException>(() =>
                new FixerFinding("x", FixOwner.NobodyHere, "wrong", ""));
        }
    }
}
