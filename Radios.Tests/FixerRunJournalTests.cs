using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Fixer;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The journal's one promise: the run is on disk the moment anything is
    /// recorded — not on close, because the close path is also the abandon
    /// path — and keeping the evidence never takes the diagnosis down.
    /// </summary>
    public class FixerRunJournalTests
    {
        private static (FixerRun run, FixerRunJournal journal, FixerRunStore store)
            Rig(TempFolder dir, FixerStageSet? set = null, FixerSettingProbeSet? probes = null)
        {
            var run = new FixerRun(set ?? FixerTestKit.Kettle(
                    fill: FixerTestKit.Answering("Yes — water."),
                    boil: FixerTestKit.Answering("Yes — it boils.")),
                FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            var store = new FixerRunStore(dir.Path);
            var journal = new FixerRunJournal(run, store, probes);
            return (run, journal, store);
        }

        [Fact]
        public void The_run_is_on_disk_the_moment_a_stage_is_recorded()
        {
            using var dir = new TempFolder();
            (FixerRun run, FixerRunJournal journal, FixerRunStore store) = Rig(dir);

            FixerStageResult result = run.RunStage("fill");
            journal.StageRecorded(result);

            // No close, no end — the file is already there and already whole.
            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.NotNull(back);
            Assert.Null(back.EndedUtc);
            RecordedStage stage = Assert.Single(back.Results);
            Assert.Equal("fill", stage.StageId);
            Assert.Equal("Yes — water.", stage.Answer);
            Assert.Contains(run.RunId, back.ReportText);       // the reused renderer ran
            Assert.Contains("Test ID: " + run.RunId, back.ReportText);
            Assert.NotEqual("", back.ReportHtml);
        }

        [Fact]
        public void Nothing_recorded_means_no_file_even_after_an_orderly_end()
        {
            using var dir = new TempFolder();
            (FixerRun run, FixerRunJournal journal, _) = Rig(dir);

            journal.RunEnded("closed");

            Assert.Empty(Directory.Exists(dir.Path)
                ? Directory.GetFiles(dir.Path) : Array.Empty<string>());
            _ = run;   // opened and closed with nothing measured: no artifact
        }

        [Fact]
        public void Skips_fixes_and_declarations_all_persist_as_they_happen()
        {
            using var dir = new TempFolder();
            FixerStageSet set = FixerTestKit.KettleWithDryFinding(out _);
            var run = new FixerRun(set, FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            var store = new FixerRunStore(dir.Path);
            var journal = new FixerRunJournal(run, store, null);

            journal.DeclarationRecorded("power-source", "mains", "The mains");
            journal.StageRecorded(run.RunStage("fill"));
            journal.FixRecorded(run.ApplyFix("fill", "dry"));
            journal.StageRecorded(run.SkipStage("boil", "later"));

            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.Equal(2, back.Results.Count);
            Assert.Equal("Skipped", back.Results[1].Status);
            Assert.Equal("later", back.Results[1].SkipChoiceId);
            RecordedFix fix = Assert.Single(back.Fixes);
            Assert.Equal("water flowing", fix.WhatItBecame);
            RecordedDeclaration decl = Assert.Single(back.Declarations);
            Assert.Equal("mains", decl.AnswerId);
        }

        [Fact]
        public void Redeclaring_replaces_the_answer_the_way_the_gate_does()
        {
            using var dir = new TempFolder();
            (FixerRun run, FixerRunJournal journal, FixerRunStore store) = Rig(dir);

            journal.StageRecorded(run.RunStage("fill"));
            journal.DeclarationRecorded("power-source", "mains", "The mains");
            journal.DeclarationRecorded("power-source", "generator", "A generator");

            FixerRunRecord back = store.FindById(run.RunId)!;
            RecordedDeclaration decl = Assert.Single(back.Declarations);
            Assert.Equal("generator", decl.AnswerId);
        }

        [Fact]
        public void A_rerun_appends_history_rather_than_erasing_it()
        {
            using var dir = new TempFolder();
            (FixerRun run, FixerRunJournal journal, FixerRunStore store) = Rig(dir);

            journal.StageRecorded(run.RunStage("fill"));
            journal.StageRecorded(run.RunStage("fill"));   // again

            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.Equal(2, back.Results.Count);           // both kept on disk
            Assert.False(back.Results[0].WasReRun);
            Assert.True(back.Results[1].WasReRun);
            Assert.Equal("fill", back.LatestResultsPerStage()["fill"].StageId);
            Assert.Equal(back.Results[1].Sequence,
                         back.LatestResultsPerStage()["fill"].Sequence);
        }

        [Fact]
        public void The_fingerprint_is_taken_at_the_moment_of_recording()
        {
            using var dir = new TempFolder();
            var tap = new FakeSetting("open");
            var probes = new FixerSettingProbeSet(
                new[] { tap.Probe("tap", "Tap") },
                new Dictionary<string, IReadOnlyList<string>> { ["fill"] = new[] { "tap" } });
            (FixerRun run, FixerRunJournal journal, FixerRunStore store) = Rig(dir, probes: probes);

            journal.StageRecorded(run.RunStage("fill"));
            tap.Value = "closed";   // changed AFTER the recording

            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.Equal("open", Assert.Single(back.Results[0].Settings).Value);

            // And the live staleness check names the change.
            FixerStalenessReport report = journal.StalenessNow()!;
            Assert.Contains("Tap changed from open to closed.", report.Summary());
        }

        [Fact]
        public void The_end_is_stamped_once_and_the_first_reason_wins()
        {
            using var dir = new TempFolder();
            (FixerRun run, FixerRunJournal journal, FixerRunStore store) = Rig(dir);

            journal.StageRecorded(run.RunStage("fill"));
            journal.RunEnded("abandoned");
            journal.RunEnded("closed");   // the close path after an abandon

            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.NotNull(back.EndedUtc);
            Assert.Equal("abandoned", back.EndReason);
        }

        [Fact]
        public void The_capture_note_rides_along_even_when_noted_before_anything_ran()
        {
            using var dir = new TempFolder();
            (FixerRun run, FixerRunJournal journal, FixerRunStore store) = Rig(dir);

            journal.CaptureNoted("A diagnostic recording ran alongside this run.",
                                 @"C:\traces\t.zip");
            Assert.Empty(Directory.GetFiles(dir.Path));   // a note alone is not evidence

            journal.StageRecorded(run.RunStage("fill"));

            FixerRunRecord back = store.FindById(run.RunId)!;
            Assert.Contains("alongside", back.CaptureNote);
            Assert.Equal(@"C:\traces\t.zip", back.CaptureArchivePath);
        }

        [Fact]
        public void A_broken_store_never_breaks_the_run()
        {
            using var dir = new TempFolder();
            string fileAsRoot = Path.Combine(dir.Path, "actually-a-file");
            File.WriteAllText(fileAsRoot, "");

            var run = new FixerRun(FixerTestKit.Kettle(fill: FixerTestKit.Answering("Yes.")),
                                   FixerTestKit.Clock(TimeSpan.Zero));
            var journal = new FixerRunJournal(run, new FixerRunStore(fileAsRoot), null);

            // Every journal call against an unwritable store: no throw, run intact.
            journal.StageRecorded(run.RunStage("fill"));
            journal.DeclarationRecorded("power-source", "mains", "The mains");
            journal.CaptureNoted("note", "");
            journal.RunEnded("closed");

            Assert.Equal(FixerStageStatus.Ran, run.ResultFor("fill")!.Status);
        }

        // -------- rehydration: the record back into engine types --------

        [Fact]
        public void A_saved_run_rehydrates_into_the_engine_types_it_came_from()
        {
            using var dir = new TempFolder();
            FixerStageSet set = FixerTestKit.KettleWithDryFinding(out _);
            var run = new FixerRun(set, FixerTestKit.Clock(TimeSpan.FromMinutes(1)));
            var store = new FixerRunStore(dir.Path);
            var journal = new FixerRunJournal(run, store, null);

            journal.StageRecorded(run.RunStage("fill"));
            journal.FixRecorded(run.ApplyFix("fill", "dry"));
            journal.StageRecorded(run.RunStage("fill"));            // re-run after the fix
            journal.StageRecorded(run.SkipStage("boil", "later"));

            FixerRunRecord record = store.FindById(run.RunId)!;
            FixerRehydratedState state = FixerRunRehydrator.Rehydrate(record);

            Assert.Equal(run.RunId, state.RunId);
            Assert.Equal(run.StartedUtc, state.StartedUtc);
            Assert.Equal(3, state.Results.Count);                    // history intact
            Assert.Equal(4, state.MaxSequence);

            FixerStageResult rerun = state.Results[1];
            Assert.True(rerun.WasReRun);
            Assert.Equal(FixerStageStatus.Ran, rerun.Status);
            FixerFinding finding = Assert.Single(rerun.Findings);
            Assert.Equal("dry", finding.Id);
            Assert.Equal(FixOwner.Us, finding.Owner);

            FixerStageResult skipped = state.Results[2];
            Assert.Equal(FixerStageStatus.Skipped, skipped.Status);
            Assert.Equal("later", skipped.Skip.Id);
            Assert.Equal(FixerSkipEffect.OperatorChoice, skipped.Skip.Effect);

            FixerFixRecord fix = Assert.Single(state.Fixes);
            Assert.Equal("water flowing", fix.WhatItBecame);
            Assert.Equal(run.RunId, fix.RunId);
        }

        [Fact]
        public void A_malformed_entry_costs_that_entry_never_the_run()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));
            var bad = EvidenceRecords.Ran("boil", 2);
            bad.Status = "NoSuchStatus";
            record.Results.Add(bad);

            FixerRehydratedState state = FixerRunRehydrator.Rehydrate(record);
            Assert.Equal("fill", Assert.Single(state.Results).StageId);
        }

        [Fact]
        public void The_microphone_baseline_survives_the_round_trip()
        {
            using var dir = new TempFolder();
            var facts = new MicCheckFacts
            {
                Measured = true,
                AudioArrived = true,
                Device = "EVO8",
                PeakDb = -6.5,
                NoiseFloorDb = double.NaN,
            };
            var set = FixerTestKit.Kettle(
                fill: _ => new FixerOutcome { Answer = "Yes.", Payload = facts });
            var run = new FixerRun(set, FixerTestKit.Clock(TimeSpan.Zero));
            var store = new FixerRunStore(dir.Path);
            var journal = new FixerRunJournal(run, store, null);

            journal.StageRecorded(run.RunStage("fill"));

            FixerRehydratedState state =
                FixerRunRehydrator.Rehydrate(store.FindById(run.RunId)!);
            var back = Assert.IsType<MicCheckFacts>(state.Results[0].Payload);
            Assert.Equal("EVO8", back.Device);
            Assert.Equal(-6.5, back.PeakDb);
            Assert.True(double.IsNaN(back.NoiseFloorDb));
        }
    }
}
