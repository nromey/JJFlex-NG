#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// C and G5 from Sol's review: the parser requires every field its own
    /// writer emits, a persisted lower-bound bit round-trips with its counts,
    /// and a merged issue count is a lower bound unless the deduplication
    /// evidence proves the union exact.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactJournalStrictnessTests
    {
        private static (FactKit Kit, LoadReport Report) Load(string dir)
        {
            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir);
            Assert.True(journal.TakeLease());
            LoadReport report = journal.LoadHistory();
            journal.Dispose();
            return (kit, report);
        }

        /// <summary>A store with one of everything the writer emits: a fact with an attempt and a receipt, a continuity entry, an issue.</summary>
        private static string FullImage()
        {
            var kit = new FactKit();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession session = kit.Session("SERIAL-S");
            EpisodeId id = FactKit.OnsetHot(kit.HotSlot(session), 70m, 3).Handle!.Id;
            AttemptHandle a = kit.Allocate(kit.PlanAutomatic(id), tracked.Binding);
            AttemptRunner.Run(a, tracked.Submit);
            a.Report(TransportEvidence.Completed(5));
            new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"), _ => ToneRequestResult.Requested).RequestFor(id);
            kit.Store.NoteIssue(IssueKind.RetentionPressure, "retention", "facts", "one lost", 1, ExtentCertainty.Exact, "x", "k1");
            session.End(FactKit.T0, "closing");
            return FactJournalFormat.Render(kit.Store.CaptureImage(), 1);
        }

        /// <summary>The same JSON with one property removed. Asserts the property WAS there, so a wrong path cannot pass by checking nothing.</summary>
        private static string Without(string json, string path)
        {
            JsonNode root = JsonNode.Parse(json)!;
            string[] parts = path.Split('.');
            JsonNode node = root;
            for (int i = 0; i < parts.Length - 1; i++) node = Step(node, parts[i]);
            Assert.True(node.AsObject().Remove(parts[^1]), path + " was not in the writer's output, so removing it would prove nothing");
            return root.ToJsonString();
        }

        private static JsonNode Step(JsonNode node, string part)
        {
            int b = part.IndexOf('[');
            if (b < 0) return node[part] ?? throw new InvalidOperationException(part + " missing");
            string name = part[..b];
            int index = int.Parse(part[(b + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
            return node[name]![index] ?? throw new InvalidOperationException(part + " missing");
        }

        [Fact]
        public void EveryWriterEmittedFieldAndSectionIsRequired()
        {
            string json = FullImage();

            // POSITIVE CONTROL: the writer's own output parses whole.
            LoadedSource whole = FactJournalFormat.Parse(json, "whole", out List<string> none);
            Assert.Empty(none);
            Assert.Single(whole.Facts);
            Assert.Single(whole.Continuity);
            Assert.Single(whole.Issues);
            Assert.Single(whole.Facts[0].Attempts);
            Assert.NotEmpty(whole.Facts[0].Attempts[0].Evidence);

            // A missing SECTION is a load issue for the whole file.
            foreach (string section in new[] { "compacted", "facts", "overlays", "continuity", "issues", "mutation", "compacted.lowerBound", "compacted.covered" })
            {
                string broken = Without(json, section);
                Assert.Throws<InvalidDataException>(() => FactJournalFormat.Parse(broken, section, out _));
            }

            // A missing FACT field rejects that record, and says so.
            foreach (string field in new[]
                     {
                         "facts[0].lineage", "facts[0].lineage.predecessor", "facts[0].detailTruncated", "facts[0].detail",
                         "facts[0].compactedLowerBound", "facts[0].occurrence", "facts[0].pause",
                         "facts[0].receipt.consumed", "facts[0].receipt.closed", "facts[0].receipt.id",
                         "facts[0].materials[0].origin", "facts[0].materials[0].root", "facts[0].materials[0].supersedes",
                         "facts[0].attempts[0].historical", "facts[0].attempts[0].consumed", "facts[0].attempts[0].authorized",
                         "facts[0].attempts[0].interrupted", "facts[0].attempts[0].cancelRequested", "facts[0].attempts[0].ticket",
                         "facts[0].attempts[0].evidence[0].ticket", "facts[0].attempts[0].evidence[0].cause",
                         "facts[0].validity.ended", "facts[0].validity.note", "facts[0].events[0].source",
                     })
            {
                LoadedSource source = FactJournalFormat.Parse(Without(json, field), field, out List<string> rejected);
                Assert.Empty(source.Facts);
                string reason = Assert.Single(rejected);
                Assert.Contains("missing", reason, StringComparison.Ordinal);
            }

            // A missing CONTINUITY or ISSUE field rejects that entry.
            foreach (string field in new[]
                     {
                         "continuity[0].supported", "continuity[0].conflicted", "continuity[0].assertions", "continuity[0].receipt",
                         "continuity[0].receipt.closed", "continuity[0].root", "continuity[0].revision", "continuity[0].supersedes",
                         "continuity[0].assertions[0].covered", "continuity[0].assertions[0].reviewed",
                     })
            {
                LoadedSource source = FactJournalFormat.Parse(Without(json, field), field, out List<string> rejected);
                Assert.Empty(source.Continuity);
                Assert.Single(source.Facts);
                Assert.Single(rejected);
            }
            foreach (string field in new[] { "issues[0].exemplarsOverflowed", "issues[0].seenOverflowed", "issues[0].source", "issues[0].reason", "issues[0].seen" })
            {
                LoadedSource source = FactJournalFormat.Parse(Without(json, field), field, out List<string> rejected);
                Assert.Empty(source.Issues);
                Assert.Single(rejected);
            }

            // Through the journal: a file missing a section falls back, and
            // the fallback is a recovery row — never a Loaded source with an
            // empty section.
            using var dir = new TempFactDir();
            File.WriteAllText(Path.Combine(dir.Path, "facts-0123456789abcdef0123456789abcdef.json"), Without(json, "issues"));
            var (kit, report) = Load(dir.Path);
            Assert.Contains(report.Sources, s => s.Status == SourceStatus.Corrupt);
            Assert.Empty(kit.Store.All);
            Assert.Contains(kit.Store.Issues, i => i.Kind == IssueKind.RecoveryGap);
            Assert.False(kit.Store.Project(FactView.Pending, null).Predicates.CompleteInventory);
        }

        [Fact]
        public void TheLowerBoundBitRoundTripsWithItsCounts()
        {
            using var dir = new TempFactDir();
            var kit = new FactKit();
            FactSession s = kit.Session("SERIAL-B");
            FactKit.OpenHot(kit.HotSlot(s));
            s.End(FactKit.T0, "closing");
            string path;
            using (var journal = new FactJournal(kit.Store, dir.Path))
            {
                Assert.True(journal.TakeLease());
                Assert.True(journal.Write());
                path = journal.ShardPath;
            }

            // POSITIVE CONTROL: as written, the counts are exact and load as exact.
            Assert.Contains("\"lowerBound\": false", File.ReadAllText(path), StringComparison.Ordinal);
            var (exact, _) = Load(dir.Path);
            Assert.False(exact.Store.CompactedCountsLowerBound);

            // The saved certainty bit says lower bound: the store says so
            // after loading, and says so again in what it writes.
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"lowerBound\": false", "\"lowerBound\": true"));
            var reader = new FactKit();
            var rj = new FactJournal(reader.Store, dir.Path);
            Assert.True(rj.TakeLease());
            rj.LoadHistory();
            Assert.True(reader.Store.CompactedCountsLowerBound);
            FactKit.OpenHot(reader.HotSlot(reader.Session("SERIAL-C")));   // something of its own to write
            Assert.True(rj.Write());
            LoadedSource rewritten = FactJournalFormat.Parse(File.ReadAllText(rj.ShardPath), "rewritten", out List<string> rejected);
            Assert.Empty(rejected);
            Assert.True(rewritten.CompactedLowerBound);
            rj.Dispose();
        }

        [Fact]
        public void MergedIssueCountsAreLowerBoundsUnlessProvenExact()
        {
            // Sol's case: two writers each saw ten disjoint losses, but only
            // four observations on each side carried a deduplication key.
            using var dir = new TempFactDir();
            static void Losses(FactKit kit, string prefix, int keyed, int unkeyed)
            {
                for (int i = 0; i < keyed; i++)
                    kit.Store.NoteIssue(IssueKind.RetentionPressure, "retention", "facts", "lost", 1, ExtentCertainty.Exact, null, prefix + i);
                for (int i = 0; i < unkeyed; i++)
                    kit.Store.NoteIssue(IssueKind.RetentionPressure, "retention", "facts", "lost", 1, ExtentCertainty.Exact, null, null);
            }
            static void Save(FactKit kit, string dir)
            {
                using var journal = new FactJournal(kit.Store, dir);
                Assert.True(journal.TakeLease());
                Assert.True(journal.Write());
            }

            var a = new FactKit();
            Losses(a, "a", 4, 6);
            var b = new FactKit();
            Losses(b, "b", 4, 6);
            StoreIssueSnapshot own = Assert.Single(a.Store.Issues);
            Assert.Equal(10, own.Count);
            Assert.Equal(ExtentCertainty.Exact, own.Extent);   // each writer's own count IS exact
            Save(a, dir.Path);
            Save(b, dir.Path);

            var (merged, _) = Load(dir.Path);
            StoreIssueSnapshot issue = Assert.Single(merged.Store.Issues, i => i.Kind == IssueKind.RetentionPressure);
            Assert.True(issue.Count >= 10);
            Assert.Equal(ExtentCertainty.LowerBound, issue.Extent);   // at least twenty happened; ten is all that can be said

            // POSITIVE CONTROL: complete deduplication evidence proves the
            // union — disjoint keys add, identical keys do not.
            using var disjoint = new TempFactDir();
            var c = new FactKit();
            Losses(c, "c", 10, 0);
            var d = new FactKit();
            Losses(d, "d", 10, 0);
            Save(c, disjoint.Path);
            Save(d, disjoint.Path);
            StoreIssueSnapshot twenty = Assert.Single(Load(disjoint.Path).Kit.Store.Issues, i => i.Kind == IssueKind.RetentionPressure);
            Assert.Equal(20, twenty.Count);
            Assert.Equal(ExtentCertainty.Exact, twenty.Extent);

            using var same = new TempFactDir();
            var e = new FactKit();
            Losses(e, "s", 10, 0);
            var f = new FactKit();
            Losses(f, "s", 10, 0);
            Save(e, same.Path);
            Save(f, same.Path);
            StoreIssueSnapshot ten = Assert.Single(Load(same.Path).Kit.Store.Issues, i => i.Kind == IssueKind.RetentionPressure);
            Assert.Equal(10, ten.Count);
            Assert.Equal(ExtentCertainty.Exact, ten.Extent);
        }
    }
}
