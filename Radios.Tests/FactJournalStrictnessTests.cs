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

        /// <summary>Write a store's own shard into a directory and say where it went.</summary>
        private static string Save(FactKit kit, string dir)
        {
            using var journal = new FactJournal(kit.Store, dir);
            Assert.True(journal.TakeLease());
            Assert.True(journal.Write());
            return journal.ShardPath;
        }

        private static void CopyInto(string shard, string dir) =>
            File.Copy(shard, Path.Combine(dir, Path.GetFileName(shard)));

        private static MaterialUnit Unit(FactSnapshot f, string name) => f.Materials.Single(m => m.Name == name);

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
                         "continuity[0].seenRoots", "continuity[0].seenRootsOverflowed",
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

        [Fact]
        public void IncompatibleSameRootEvidenceIsNotCombined()
        {
            // Sol, G2 blocker 1: the continuity merge took an equal Root as
            // enough and ORed Covered and Reviewed across two sources holding
            // one assertion identity with different values, so a value that
            // was never read out could be credited with the coverage another
            // value earned. Astra: compatible exact-lineage evidence combines;
            // incompatible candidates remain unknown.
            using var dir = new TempFactDir();
            var a = new FactKit();
            var tracked = new RecordingTransport(a.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession sA = a.Session("SERIAL-X");
            EpisodeId first = FactKit.OnsetHot(a.HotSlot(sA), 70m, 3).Handle!.Id;
            AttemptHandle heard = a.Allocate(a.PlanAutomatic(first), tracked.Binding);
            AttemptRunner.Run(heard, tracked.Submit);
            heard.Report(TransportEvidence.Completed(5));                            // 70 was read out, in full
            sA.End(FactKit.T0, "closing");
            string aShard = Save(a, dir.Path);

            // Writer B continues A's occurrence across a restart at the same
            // value, so B's shard names A's assertion roots.
            var b = new FactKit();
            var bj = new FactJournal(b.Store, dir.Path);
            Assert.True(bj.TakeLease());
            bj.LoadHistory();
            FactSession sB = b.Session("SERIAL-X");
            PublicationResult continued = FactKit.ContinueHot(b.HotSlot(sB), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, continued.Outcome);
            EpisodeId second = continued.Handle!.Id;
            Assert.Equal(new AssertionRef(first, Unit(a.Store.Find(first)!, "temperature").Id), Unit(continued.Fact!, "temperature").Root);
            sB.End(FactKit.T0, "closing");
            Assert.True(bj.Write());
            string bShard = bj.ShardPath;
            bj.Dispose();

            // POSITIVE CONTROL: compatible exact-lineage evidence combines.
            var (compatible, _) = Load(dir.Path);
            FactSnapshot same = compatible.Store.Find(second)!;
            Assert.Contains(Unit(same, "temperature").Id, same.Covered);
            Assert.NotNull(compatible.HotSlot(compatible.Session("SERIAL-X")).Continuity!.Reference);
            Assert.DoesNotContain(compatible.Store.Issues, i => i.Kind == IssueKind.IdentityConflict);

            // Now B's shard says the SAME assertion — same root — holds 72:
            // not covered, but reviewed. One identity, two contents.
            JsonNode root = JsonNode.Parse(File.ReadAllText(bShard))!;
            JsonObject assertion = root["continuity"]!.AsArray().Single()!["assertions"]!.AsArray()
                .Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "temperature");
            Assert.Equal("70", assertion["value"]!["v"]!.GetValue<string>());
            assertion["value"]!["v"] = "72";
            assertion["covered"] = false;
            assertion["reviewed"] = true;
            JsonObject unit = root["facts"]!.AsArray().Single()!["materials"]!.AsArray()
                .Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "temperature");
            unit["value"]!["v"] = "72";
            root["facts"]!.AsArray().Single()!["coverage"]!["reviewed"]!.AsArray().Add(unit["id"]!.GetValue<long>());
            File.WriteAllText(bShard, root.ToJsonString());

            var (merged, report) = Load(dir.Path);
            Assert.Equal(2, report.Counts.Accepted);

            // The continuity for the key supports nothing, and says why.
            ContinuityView view = merged.HotSlot(merged.Session("SERIAL-X")).Continuity!;
            Assert.True(view.Conflicted);
            Assert.Null(view.Reference);
            Assert.False(view.DeliveryEvidenceSupported);
            Assert.Contains(merged.Store.Issues, i => i.Kind == IssueKind.IdentityConflict
                                                       && i.SourceKey.StartsWith("continuity:", StringComparison.Ordinal));

            // 72 was never read out: the coverage 70 earned is not its.
            FactSnapshot claimed = merged.Store.Find(second)!;
            MaterialUnit seventyTwo = Unit(claimed, "temperature");
            Assert.Equal(72m, seventyTwo.Value.AsDecimal);
            Assert.DoesNotContain(seventyTwo.Id, claimed.Covered);
            Assert.Contains(seventyTwo.Id, claimed.Reviewed);                        // its OWN review stands

            // A review of 72 is not a review of 70; 70's own coverage stands.
            FactSnapshot original = merged.Store.Find(first)!;
            MaterialUnit seventy = Unit(original, "temperature");
            Assert.Contains(seventy.Id, original.Covered);
            Assert.DoesNotContain(seventy.Id, original.Reviewed);

            // The assertions the two sources agree on still combine: the
            // duration was the same information, read out once.
            Assert.Contains(Unit(claimed, "duration").Id, claimed.Covered);

            // A continuation claim against the conflicted key is unknown, not permitted.
            PublicationResult next = FactKit.ContinueHot(merged.HotSlot(merged.Session("SERIAL-X")), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, next.Outcome);
            Assert.Equal(PauseCause.ContinuityUnknown, next.Fact!.Pause);
            Assert.Empty(next.Fact.Grants);
            Assert.False(merged.Store.IsEligibleForAutomaticDelivery(next.Fact));

            // The continuity table is bounded and evicts entries while the
            // records stay. With NO continuity entry for the occurrence, the
            // only evidence B's 72 could find is A's record itself — and A's
            // record holds that root with different content, so it credits
            // nothing, while the duration they agree on still combines.
            static void StripContinuity(string shard)
            {
                JsonNode n = JsonNode.Parse(File.ReadAllText(shard))!;
                Assert.NotEmpty(n["continuity"]!.AsArray());                          // there WAS one to strip
                n["continuity"] = new JsonArray();
                File.WriteAllText(shard, n.ToJsonString());
            }
            StripContinuity(aShard);
            StripContinuity(bShard);
            var (recordsOnly, onlyReport) = Load(dir.Path);
            Assert.Equal(2, onlyReport.Counts.Accepted);
            Assert.Null(recordsOnly.HotSlot(recordsOnly.Session("SERIAL-X")).Continuity);
            FactSnapshot bAlone = recordsOnly.Store.Find(second)!;
            Assert.DoesNotContain(Unit(bAlone, "temperature").Id, bAlone.Covered);
            Assert.Contains(Unit(bAlone, "duration").Id, bAlone.Covered);
            FactSnapshot aAlone = recordsOnly.Store.Find(first)!;
            Assert.Contains(Unit(aAlone, "temperature").Id, aAlone.Covered);
            Assert.DoesNotContain(Unit(aAlone, "temperature").Id, aAlone.Reviewed);
        }

        [Fact]
        public void SeenRootsSurviveAReloadSoASuccessorIsCheckedAgainstAllOfThem()
        {
            // Sol, G2: schema 3 persisted SupersededRoots and Conflicted but
            // not SeenRoots, which the supersede decision reads. Two
            // independent first occurrences of one condition on one station
            // conflict; the process that saw it writes the conflict; a later
            // source that supersedes only ONE of the two must not be taken as
            // the occurrence once the other's shard is gone.
            using var origins = new TempFactDir();
            var x = new FactKit();
            FactSession sX = x.Session("SERIAL-K");
            FactKit.OnsetHot(x.HotSlot(sX));                                         // root R1
            sX.End(FactKit.T0, "closing");
            string xShard = Save(x, origins.Path);

            // Process one: X first, then W, so the conflict is rooted at R1
            // and R4 lives only in SeenRoots — deterministically.
            var one = new FactKit();
            var loadX = new FactJournal(one.Store, origins.Path);
            Assert.True(loadX.TakeLease());
            loadX.LoadHistory();
            loadX.Dispose();
            var w = new FactKit();
            FactSession sW = w.Session("SERIAL-K");
            FactKit.OnsetHot(w.HotSlot(sW));                                         // root R4, nothing to do with R1
            sW.End(FactKit.T0, "closing");
            Save(w, origins.Path);
            var oneJournal = new FactJournal(one.Store, origins.Path);
            Assert.True(oneJournal.TakeLease());
            oneJournal.LoadHistory();
            Assert.True(one.HotSlot(one.Session("SERIAL-K")).Continuity!.Conflicted);
            Assert.True(oneJournal.Write());
            string oneShard = oneJournal.ShardPath;
            oneJournal.Dispose();
            Assert.Contains("\"seenRoots\"", File.ReadAllText(oneShard), StringComparison.Ordinal);   // it is written

            // Writer Z knew only X, and opened an evidenced onset over it: its
            // root supersedes R1 and nothing else.
            using var zDir = new TempFactDir();
            CopyInto(xShard, zDir.Path);
            var z = new FactKit();
            var zJournal = new FactJournal(z.Store, zDir.Path);
            Assert.True(zJournal.TakeLease());
            zJournal.LoadHistory();
            Assert.NotNull(z.HotSlot(z.Session("SERIAL-K")).Continuity!.Reference);   // X alone is supported
            FactSession sZ = z.Session("SERIAL-K");
            FactKit.OnsetHot(z.HotSlot(sZ));                                         // root R3 over R1
            sZ.End(FactKit.T0, "closing");
            Assert.True(zJournal.Write());
            string zShard = zJournal.ShardPath;
            zJournal.Dispose();

            // Process two sees only process one's shard and Z's. R4 is
            // unaccounted for, and only the persisted SeenRoots know it.
            using var later = new TempFactDir();
            CopyInto(oneShard, later.Path);
            CopyInto(zShard, later.Path);
            var (two, _) = Load(later.Path);
            ContinuityView unresolved = two.HotSlot(two.Session("SERIAL-K")).Continuity!;
            Assert.True(unresolved.Conflicted);
            Assert.Null(unresolved.Reference);

            // POSITIVE CONTROL: a writer that saw the whole conflict and
            // opened an evidenced onset over it supersedes both roots, and
            // the same later process accepts it.
            var y = new FactKit();
            var yJournal = new FactJournal(y.Store, origins.Path);
            Assert.True(yJournal.TakeLease());
            yJournal.LoadHistory();
            Assert.True(y.HotSlot(y.Session("SERIAL-K")).Continuity!.Conflicted);
            FactSession sY = y.Session("SERIAL-K");
            EpisodeId r5 = FactKit.OnsetHot(y.HotSlot(sY)).Handle!.Id;
            sY.End(FactKit.T0, "closing");
            Assert.True(yJournal.Write());
            string yShard = yJournal.ShardPath;
            yJournal.Dispose();
            using var settled = new TempFactDir();
            CopyInto(oneShard, settled.Path);
            CopyInto(yShard, settled.Path);
            var (three, _) = Load(settled.Path);
            ContinuityView resolved = three.HotSlot(three.Session("SERIAL-K")).Continuity!;
            Assert.False(resolved.Conflicted);
            Assert.Equal(r5, resolved.Reference!.Root);
        }
    }
}
