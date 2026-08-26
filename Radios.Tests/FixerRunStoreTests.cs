using System;
using System.IO;
using System.Linq;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The store's promises: one file per run updated in place, honest
    /// handling of files it cannot read, the resume list's window, and the
    /// retention cap doing its job without eating the newest runs.
    /// </summary>
    public class FixerRunStoreTests
    {
        [Fact]
        public void Save_then_load_round_trips_and_the_filename_carries_stamp_and_id()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            FixerRunRecord record = EvidenceRecords.TwoStages("ATX-357");

            Assert.True(store.Save(record));

            string file = Assert.Single(Directory.GetFiles(dir.Path));
            Assert.Equal("run-20260101-120000-ATX-357.json", Path.GetFileName(file));

            var loaded = store.LoadAll(out int unreadable);
            Assert.Equal(0, unreadable);
            Assert.Equal("ATX-357", Assert.Single(loaded).RunId);
        }

        [Fact]
        public void Saving_as_the_run_goes_updates_one_file_not_many()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            FixerRunRecord record = EvidenceRecords.TwoStages();

            store.Save(record);
            record.Results.Add(EvidenceRecords.Ran("fill", 1));
            store.Save(record);
            record.Results.Add(EvidenceRecords.Ran("boil", 2));
            store.Save(record);

            Assert.Single(Directory.GetFiles(dir.Path));
            FixerRunRecord back = store.LoadAll(out _).Single();
            Assert.Equal(2, back.Results.Count);
        }

        [Fact]
        public void FindById_returns_the_record_and_null_for_a_stranger()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            store.Save(EvidenceRecords.TwoStages("ATX-357"));
            store.Save(EvidenceRecords.TwoStages("WYX-234", EvidenceRecords.T0.AddHours(1)));

            Assert.Equal("ATX-357", store.FindById("ATX-357")?.RunId);
            Assert.Null(store.FindById("777-777"));
            Assert.Null(store.FindById(""));
        }

        [Fact]
        public void One_unreadable_file_is_counted_and_costs_nothing_else()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            store.Save(EvidenceRecords.TwoStages("ATX-357"));

            // A file matching the pattern that is not a record. The positive
            // control beside it proves the reader would have seen a good one.
            File.WriteAllText(Path.Combine(dir.Path, "run-20260101-000000-XXX-XXX.json"),
                              "this is not json");

            var loaded = store.LoadAll(out int unreadable);
            Assert.Equal(1, unreadable);
            Assert.Equal("ATX-357", Assert.Single(loaded).RunId);
        }

        [Fact]
        public void Newest_runs_come_first()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            store.Save(EvidenceRecords.TwoStages("AAA-222", EvidenceRecords.T0));
            store.Save(EvidenceRecords.TwoStages("WWW-333", EvidenceRecords.T0.AddHours(2)));
            store.Save(EvidenceRecords.TwoStages("XXX-444", EvidenceRecords.T0.AddHours(1)));

            var loaded = store.LoadAll(out _);
            Assert.Equal(new[] { "WWW-333", "XXX-444", "AAA-222" },
                         loaded.Select(r => r.RunId).ToArray());
        }

        [Fact]
        public void The_resume_list_offers_recent_stopped_runs_only()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            DateTime now = EvidenceRecords.T0.AddDays(100);

            // Stopped yesterday: offered.
            var recent = EvidenceRecords.TwoStages("AAA-222", now.AddDays(-1));
            recent.Results.Add(EvidenceRecords.Ran("fill", 1));
            store.Save(recent);

            // Finished yesterday: complete, not offered.
            var finished = EvidenceRecords.TwoStages("WWW-333", now.AddDays(-1).AddHours(1));
            finished.Results.Add(EvidenceRecords.Ran("fill", 1));
            finished.Results.Add(EvidenceRecords.Ran("boil", 2));
            store.Save(finished);

            // Stopped a month ago: still viewable evidence, but not offered.
            var old = EvidenceRecords.TwoStages("XXX-444", now.AddDays(-30));
            old.Results.Add(EvidenceRecords.Ran("fill", 1));
            store.Save(old);

            var stopped = store.StoppedRuns(now);
            Assert.Equal("AAA-222", Assert.Single(stopped).RunId);

            // The old one is not gone — only not offered for resume.
            Assert.NotNull(store.FindById("XXX-444"));
        }

        [Fact]
        public void Retention_deletes_the_oldest_beyond_the_cap_and_never_the_newest()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);

            for (int i = 0; i < FixerRunStore.MaxRunsKept + 5; i++)
            {
                // Distinct ids and stamps; the alphabet doesn't matter here.
                store.Save(EvidenceRecords.TwoStages("AAA-" + i.ToString("D3"),
                    EvidenceRecords.T0.AddMinutes(i)));
            }

            string[] files = Directory.GetFiles(dir.Path);
            Assert.Equal(FixerRunStore.MaxRunsKept, files.Length);

            // The newest survives; the five oldest are gone.
            Assert.NotNull(store.FindById("AAA-" + (FixerRunStore.MaxRunsKept + 4).ToString("D3")));
            Assert.Null(store.FindById("AAA-000"));
            Assert.Null(store.FindById("AAA-004"));
            Assert.NotNull(store.FindById("AAA-005"));
        }

        [Fact]
        public void Delete_removes_the_file_and_missing_files_do_not_alarm()
        {
            using var dir = new TempFolder();
            var store = new FixerRunStore(dir.Path);
            FixerRunRecord record = EvidenceRecords.TwoStages();
            store.Save(record);

            Assert.True(store.Delete(record));
            Assert.Empty(Directory.GetFiles(dir.Path));
            Assert.True(store.Delete(record));   // already gone: still fine
        }

        [Fact]
        public void An_unwritable_store_reports_false_rather_than_throwing()
        {
            using var dir = new TempFolder();
            // The "directory" is a file, so CreateDirectory must fail.
            string fileAsRoot = Path.Combine(dir.Path, "actually-a-file");
            File.WriteAllText(fileAsRoot, "");

            var store = new FixerRunStore(fileAsRoot);
            Assert.False(store.Save(EvidenceRecords.TwoStages()));
            Assert.Empty(store.LoadAll(out _));
        }
    }
}
