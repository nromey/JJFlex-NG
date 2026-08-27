using System;
using System.IO;
using System.Linq;
using Radios.SignalCapture;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The capture store (#271): the shared evidence-store mechanics under the
    /// capture record family — and the one behaviour the Fixer store never
    /// needed, rename-in-place.
    /// </summary>
    public sealed class QsoSignalCaptureStoreTests
    {
        private static readonly DateTime T0 = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        private static QsoSignalCaptureRecord Capture(string id, DateTime started)
            => new QsoSignalCaptureRecord
            {
                CaptureId = id,
                StartedUtc = started,
                EndedUtc = started.AddSeconds(60),
                CaptureSeconds = 60,
                ReportText = "report for " + id,
                ReportHtml = "<p>report for " + id + "</p>",
            };

        [Fact]
        public void SavesLoadsNewestFirstAndFindsById()
        {
            using var dir = new TempFolder();
            var store = new QsoSignalCaptureStore(dir.Path);

            Assert.True(store.Save(Capture("AAA-222", T0)));
            Assert.True(store.Save(Capture("TTT-333", T0.AddMinutes(5))));

            var all = store.LoadAll(out int unreadable);
            Assert.Equal(0, unreadable);
            Assert.Equal(new[] { "TTT-333", "AAA-222" },
                all.Select(c => c.CaptureId).ToArray());

            Assert.Equal("AAA-222", store.FindById("AAA-222").CaptureId);
            Assert.Null(store.FindById("XXX-777"));
        }

        [Fact]
        public void RenamingRewritesTheSameFileInPlace()
        {
            using var dir = new TempFolder();
            var store = new QsoSignalCaptureStore(dir.Path);

            var capture = Capture("AAA-222", T0);
            Assert.True(store.Save(capture));

            capture.Label = "Don on 40 meters";
            Assert.True(store.Save(capture));

            // One file, not two: the path derives from the start stamp and id,
            // never from the label, so a rename cannot fork the record.
            Assert.Single(Directory.GetFiles(dir.Path));
            Assert.Equal("Don on 40 meters", store.FindById("AAA-222").Label);
        }

        [Fact]
        public void RetentionKeepsTheNewestFiftyLabelledOrNot()
        {
            using var dir = new TempFolder();
            var store = new QsoSignalCaptureStore(dir.Path);

            for (int i = 0; i < QsoSignalCaptureStore.MaxCapturesKept + 2; i++)
            {
                var c = Capture(Id(i), T0.AddMinutes(i));
                if (i == 0) c.Label = "the oldest, and labelled";
                Assert.True(store.Save(c));
            }

            var all = store.LoadAll(out _);
            Assert.Equal(QsoSignalCaptureStore.MaxCapturesKept, all.Count);
            // The two oldest are gone — a label must not quietly turn the cap off.
            Assert.Null(store.FindById(Id(0)));
            Assert.Null(store.FindById(Id(1)));
            Assert.NotNull(store.FindById(Id(2)));
        }

        [Fact]
        public void AnUnreadableFileIsCountedNotSwallowed()
        {
            using var dir = new TempFolder();
            var store = new QsoSignalCaptureStore(dir.Path);
            Assert.True(store.Save(Capture("AAA-222", T0)));
            File.WriteAllText(Path.Combine(dir.Path, "capture-20260826-999999-BAD-BAD.json"),
                "{ not json");

            var all = store.LoadAll(out int unreadable);
            Assert.Single(all);
            Assert.Equal(1, unreadable);
        }

        [Fact]
        public void DeleteRemovesTheFile()
        {
            using var dir = new TempFolder();
            var store = new QsoSignalCaptureStore(dir.Path);
            var capture = Capture("AAA-222", T0);
            Assert.True(store.Save(capture));
            Assert.True(store.Delete(capture));
            Assert.Empty(store.LoadAll(out _));
        }

        [Fact]
        public void TheCaptureFamilyLivesInItsOwnFolderName()
        {
            // The Fixer store and this one share one mechanism but must never
            // share a folder — the schema version inside a record file is
            // per-family.
            Assert.NotEqual(Radios.Fixer.Evidence.FixerRunStore.FolderName,
                QsoSignalCaptureStore.FolderName);
        }

        /// <summary>Distinct ids from the run-id alphabet, index-stamped.</summary>
        private static string Id(int i)
            => "A" + (i / 10) + (i % 10) + "-T2X";
    }
}
