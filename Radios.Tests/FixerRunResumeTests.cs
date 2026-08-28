using System;
using System.Linq;
using Radios.Fixer;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Continuing a stopped run: what it keeps, what it refuses, and the
    /// records it leaves behind so nobody can mistake it for one sitting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule this is all built around:</b> a stored report is a record of
    /// a window that genuinely happened. The QSO signal analyzer satisfies it
    /// by never resuming at all — a capture is one continuous window of
    /// readings and a resumed one would be a lie about when it was taken. A
    /// Fixer run is a different shape: a sequence of discrete measurements,
    /// each already carrying its own timestamp and the settings it ran under.
    /// So it CAN be continued honestly, on one condition — that the document
    /// says the measurements were not all taken at one go.
    /// </para>
    /// <para>
    /// Take that condition away and this becomes the exact thing the ruling
    /// forbids, which is why the sittings and their statement in the exported
    /// document are tested here beside the engine seam rather than somewhere
    /// else.
    /// </para>
    /// </remarks>
    public class FixerRunResumeTests
    {
        private static (FixerRunRecord record, FixerRun first) FirstSitting(TempFolder dir)
        {
            var store = new FixerRunStore(dir.Path);
            var first = new FixerRun(FixerTestKit.Kettle(fill: FixerTestKit.Answering("Yes.")),
                                     FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            var journal = new FixerRunJournal(first, store, null);
            journal.StageRecorded(first.RunStage("fill"));
            journal.RunEnded("abandoned");
            return (store.FindById(first.RunId)!, first);
        }

        // -------- the engine seam --------

        [Fact]
        public void A_resumed_engine_keeps_the_test_id_the_start_time_and_the_history()
        {
            using var dir = new TempFolder();
            (FixerRunRecord record, FixerRun first) = FirstSitting(dir);

            FixerRun resumed = FixerRun.Resume(
                FixerTestKit.Kettle(boil: FixerTestKit.Answering("It boils.")),
                FixerRunRehydrator.Rehydrate(record),
                FixerTestKit.Clock(FixerTestKit.T0.AddDays(1), TimeSpan.FromMinutes(1)));

            // The ID is the whole point: a support thread quoting it must reach
            // the same investigation, not a second one that looks like it.
            Assert.Equal(first.RunId, resumed.RunId);
            Assert.Equal(first.StartedUtc, resumed.StartedUtc);
            Assert.Equal("Yes.", resumed.ResultFor("fill")!.Answer);
            Assert.Null(resumed.ResultFor("boil"));
        }

        [Fact]
        public void New_work_can_never_interleave_falsely_with_the_old()
        {
            // The sequence counter is what the report uses to say "these stages
            // ran AFTER that fix". Restarting it at zero would put the second
            // sitting's work before the first sitting's in that ordering, and
            // every "what changed when" sentence in the report would be wrong.
            using var dir = new TempFolder();
            (FixerRunRecord record, _) = FirstSitting(dir);
            FixerRehydratedState prior = FixerRunRehydrator.Rehydrate(record);

            FixerRun resumed = FixerRun.Resume(
                FixerTestKit.Kettle(boil: FixerTestKit.Answering("It boils.")), prior,
                FixerTestKit.Clock(FixerTestKit.T0.AddDays(1), TimeSpan.FromMinutes(1)));

            FixerStageResult next = resumed.RunStage("boil");
            Assert.True(next.Sequence > prior.MaxSequence,
                "a resumed run's new results must sequence above everything it inherited");
            Assert.Equal(new[] { "fill", "boil" },
                         resumed.ResultsInRunOrder.Select(r => r.StageId).ToArray());
        }

        [Fact]
        public void Resuming_needs_both_halves()
        {
            Assert.Throws<ArgumentNullException>(
                () => FixerRun.Resume(null!, null!));
            Assert.Throws<ArgumentNullException>(
                () => FixerRun.Resume(FixerTestKit.Kettle(), null!));
        }

        // -------- the journal's refusals --------

        [Fact]
        public void A_record_recorded_against_different_checks_is_refused_not_reconciled()
        {
            // New results landing beside old ones under a stage list that
            // describes neither sitting is unreadable evidence. Refused, and
            // the run stays viewable and exportable — which is what it is for.
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);

            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Stages.Add(new RecordedStageInfo { Id = "steep", Number = 2, Title = "Steep" });

            var run = new FixerRun(FixerTestKit.Kettle(), FixerTestKit.Clock(TimeSpan.Zero));
            record.RunId = run.RunId;

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => FixerRunJournal.Resume(run, store, null, record));
            Assert.Contains("different set of checks", ex.Message);
        }

        [Fact]
        public void A_result_for_a_stage_this_build_no_longer_has_is_dropped_not_carried()
        {
            // The backstop behind that refusal. A result nothing can re-run,
            // re-read or report against is worse than absent: it would be
            // counted in "how much of the test was done".
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));
            record.Results.Add(EvidenceRecords.Ran("steep", 2));   // no such stage

            FixerRun resumed = FixerRun.Resume(FixerTestKit.Kettle(),
                                               FixerRunRehydrator.Rehydrate(record),
                                               FixerTestKit.Clock(TimeSpan.Zero));

            Assert.NotNull(resumed.ResultFor("fill"));
            Assert.Null(resumed.ResultFor("steep"));
        }

        // -------- sittings --------

        [Fact]
        public void Every_run_opens_a_sitting_and_ending_it_closes_that_sitting()
        {
            using var dir = new TempFolder();
            (FixerRunRecord record, _) = FirstSitting(dir);

            RecordedSitting only = Assert.Single(record.Sittings);
            Assert.NotNull(only.EndedUtc);
            Assert.Equal("abandoned", only.EndReason);
        }

        [Fact]
        public void Resuming_opens_a_SECOND_sitting_rather_than_extending_the_first()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            (FixerRunRecord record, _) = FirstSitting(dir);

            var resumed = FixerRun.Resume(
                FixerTestKit.Kettle(boil: FixerTestKit.Answering("It boils.")),
                FixerRunRehydrator.Rehydrate(record),
                FixerTestKit.Clock(FixerTestKit.T0.AddDays(1), TimeSpan.FromMinutes(1)));

            var journal = FixerRunJournal.Resume(resumed, store, null, record);
            journal.StageRecorded(resumed.RunStage("boil"));
            journal.RunEnded("closed");

            FixerRunRecord back = store.FindById(resumed.RunId)!;
            Assert.Equal(2, back.Sittings.Count);
            Assert.Equal("abandoned", back.Sittings[0].EndReason);
            Assert.Equal("closed", back.Sittings[1].EndReason);
            Assert.True(back.Sittings[1].StartedUtc > back.Sittings[0].EndedUtc);

            // And the document says it, which is the condition that makes the
            // whole thing honest rather than merely recorded.
            Assert.Contains("NOT all measured in one continuous session",
                            FixerRunExport.PlainText(back));
        }

        [Fact]
        public void A_resumed_run_is_listed_as_worked_on_in_more_than_one_sitting()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));
            record.Sittings.Add(new RecordedSitting { StartedUtc = EvidenceRecords.T0 });
            record.Sittings.Add(new RecordedSitting { StartedUtc = EvidenceRecords.T0.AddDays(1) });

            Assert.Contains("worked on in 2 sittings", record.Summary());
        }

        // -------- identity --------

        [Fact]
        public void The_radio_identity_is_read_at_the_first_recording_not_when_the_window_opened()
        {
            // The Fixer is routinely opened before the operator connects.
            // Reading identity then would stamp "not reported" on a radio that
            // was perfectly readable by the time anything was measured.
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            var run = new FixerRun(FixerTestKit.Kettle(fill: FixerTestKit.Answering("Yes.")),
                                   FixerTestKit.Clock(TimeSpan.FromMinutes(1)));

            int reads = 0;
            var journal = new FixerRunJournal(run, store, null, () =>
            {
                reads++;
                return new FixerRunIdentity
                {
                    Station = new[] { "Model: FLEX-6300" },
                    Software = new[] { "JJ Flexible version: 4.1.16" },
                };
            });

            Assert.Equal(0, reads);                  // nothing recorded yet

            journal.StageRecorded(run.RunStage("fill"));
            Assert.Equal(1, reads);

            journal.StageRecorded(run.RunStage("fill"));
            journal.RunEnded("closed");
            Assert.Equal(1, reads);                  // once, not per recording

            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.Equal("Model: FLEX-6300", Assert.Single(back.Station));
            Assert.Equal("JJ Flexible version: 4.1.16", Assert.Single(back.Software));
        }

        [Fact]
        public void An_identity_that_throws_costs_the_identity_and_never_the_measurement()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            var run = new FixerRun(FixerTestKit.Kettle(fill: FixerTestKit.Answering("Yes.")),
                                   FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            var journal = new FixerRunJournal(run, store, null,
                () => throw new InvalidOperationException("no radio"));

            journal.StageRecorded(run.RunStage("fill"));

            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.Single(back.Results);
            Assert.Empty(back.Station);
        }

        // -------- what a keyed stage's evidence carries --------

        [Fact]
        public void A_keyed_stages_evidence_text_survives_being_saved_and_continued()
        {
            // The transmitting stages compose a provenance line into their
            // evidence — the declared load, when it was declared, and whether
            // it was declared remotely. That belongs to the measurement: a
            // reading with no idea what the RF went into cannot be interpreted
            // by anybody, which is #217's standard and #188's point.
            //
            // Nothing in the evidence layer knows that string exists, and that
            // is fine — but it must reach disk and come back, or a saved run
            // would hold a transmit measurement whose conditions were dropped
            // in the filing. It conflicts with nothing and builds cleanly if it
            // ever stops, so it is pinned here rather than assumed.
            const string Provenance =
                "Load declared as \"A dummy load\" at 2026-01-01 12:00 UTC, declared remotely.";

            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            var run = new FixerRun(
                FixerTestKit.Kettle(boil: _ => new FixerOutcome
                {
                    Answer = "It boils.",
                    Evidence = Provenance,
                }),
                FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            var journal = new FixerRunJournal(run, store, null);

            journal.StageRecorded(run.RunStage("boil"));

            FixerRunRecord saved = store.FindById(run.RunId)!;
            Assert.Equal(Provenance, saved.Results[0].Evidence);

            FixerRun resumed = FixerRun.Resume(FixerTestKit.Kettle(),
                                               FixerRunRehydrator.Rehydrate(saved),
                                               FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            Assert.Equal(Provenance, resumed.ResultFor("boil")!.Evidence);
        }

        // -------- the name --------

        [Fact]
        public void Renaming_a_run_overwrites_the_same_file_and_keeps_the_id()
        {
            // The path derives from the start stamp and the id only. If it
            // derived from the label, renaming would leave the old file behind
            // and the list would show the run twice.
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));

            Assert.True(store.Save(record));
            record.Label = "Tuesday, when it worked";
            Assert.True(store.Save(record));

            FixerRunRecord only = Assert.Single(store.LoadAll(out int unreadable));
            Assert.Equal(0, unreadable);
            Assert.Equal("Tuesday, when it worked", only.Label);
            Assert.Equal("AAA-222", only.RunId);
            Assert.Equal("Tuesday, when it worked", only.DisplayName);
            Assert.Contains("Tuesday, when it worked (AAA-222)", only.Summary());
        }

        [Fact]
        public void A_run_with_no_name_is_displayed_and_summarised_by_its_id()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            Assert.Equal("AAA-222", record.DisplayName);
            Assert.StartsWith("AAA-222 —", record.Summary());
        }
    }
}
