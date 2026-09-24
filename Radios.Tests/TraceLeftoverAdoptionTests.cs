using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JJTrace;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A restart with genuinely discarded coordinator and ticket state, and
    /// no old worker able to finish: what boot makes of the raw files it finds.
    /// Sprint 45 Track H7, Astra's ruling on the pending-record failure,
    /// implementation note 5 and its restart acceptance case.
    /// </summary>
    /// <remarks>
    /// <para><b>What changed.</b> Boot used to group leftover part files by
    /// the stamp in their names, invent a new <c>TraceSession</c>, mark it
    /// <c>killed</c>, and join the live leftover to whichever chain had the
    /// newest stamp. Since Track H6 a sidecarless part is exactly what a
    /// failed pending-record write leaves — on a session whose real outcome
    /// may have been <c>connection_dropped</c> — so <c>killed</c> was an
    /// invention and the join a guess.</para>
    ///
    /// <para><b>The production ordering</b> is <c>RotateBootTraceIfNeeded</c>
    /// (adopt parts, then decide the live leftover) followed by
    /// <c>TraceArchiveBootMaintenance</c> (recover pending records, reconcile,
    /// prune). Both live in the VB application, which this project cannot
    /// reference, so the JJTrace pieces are driven here in that order:
    /// <c>TraceLeftoverAdoption.AdoptLeftoverParts</c>,
    /// <c>ChainForLiveLeftover</c>, then
    /// <c>TraceArchiveWorker.RecoverPending</c>. A source-read test pins that
    /// the application really delegates to them.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs).
    public sealed class TraceLeftoverAdoptionTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _archiveDir;
        private const string Stem = "JJFlexRadioTrace";

        public TraceLeftoverAdoptionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-h7-adopt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _archiveDir = Path.Combine(_dir, "Traces");
            TraceRecordingHealth.ResetForTests();
        }

        public void Dispose()
        {
            TraceArchiveWorker.Drain(TimeSpan.FromSeconds(20));
            TraceRecordingHealth.ResetForTests();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Part(string stamp, int part, string content = "bytes")
        {
            string path = Path.Combine(_dir, Stem + "-" + stamp + "-part-" + part.ToString("D3") + ".txt");
            File.WriteAllText(path, content + Environment.NewLine);
            return path;
        }

        private TraceManifest Manifest() =>
            TraceManifest.Load(Path.Combine(_archiveDir, SessionArchive.ManifestFileName));

        /// <summary>
        /// Sidecarless parts are filed as ORPHANED evidence: outcome unknown,
        /// an inventory identity the manifest marks as such, the bytes kept on
        /// disk. Nothing says <c>killed</c>.
        /// </summary>
        [Fact]
        public void Sidecarless_parts_are_filed_as_orphaned_evidence_with_unknown_outcome()
        {
            string p1 = Part("20260924-101500", 1, "first part");
            string p2 = Part("20260924-101500", 2, "second part");

            IReadOnlyList<LeftoverChainAdoption> chains =
                TraceLeftoverAdoption.AdoptLeftoverParts(_dir, Stem, _archiveDir);

            LeftoverChainAdoption chain = Assert.Single(chains);
            Assert.Equal(2, chain.HighestPart);
            Assert.Equal(2, chain.Archived);
            Assert.True(chain.Session.IdentityUnknown);

            var entries = Manifest().Entries.Where(e => e.SessionId == chain.Session.SessionId.ToString())
                                            .OrderBy(e => e.PartNumber).ToList();
            Assert.Equal(2, entries.Count);
            foreach (TraceSessionEntry e in entries)
            {
                Assert.Equal(TraceSessionOutcome.Unknown, e.Outcome);
                Assert.NotEqual(TraceSessionOutcome.Killed, e.Outcome);
                Assert.True(e.Orphaned == true, "the entry does not say its identity is an inventory one");
                Assert.Equal(TraceLeftoverAdoption.OrphanDetail, e.OutcomeDetail);
                Assert.Null(e.EndTime);
            }
            // The raw files are retained.
            Assert.True(File.Exists(p1));
            Assert.True(File.Exists(p2));
            // And the health read knows evidence was filed without its details.
            Assert.Equal(2, TraceRecordingHealth.Snapshot().OrphansAdoptedAtBoot);

            // Idempotent across boots: a second pass files nothing twice.
            IReadOnlyList<LeftoverChainAdoption> again =
                TraceLeftoverAdoption.AdoptLeftoverParts(_dir, Stem, _archiveDir);
            Assert.Equal(0, Assert.Single(again).Archived);
            Assert.Equal(2, Manifest().Entries.Count);
        }

        /// <summary>
        /// A live leftover with no continuation header is NOT joined to the
        /// chain with the newest stamp. Its own words are the only evidence
        /// that would join it, and it has none.
        /// </summary>
        [Fact]
        public void A_live_leftover_is_not_joined_to_a_chain_by_timestamp_alone()
        {
            Part("20260924-101500", 1);
            Part("20260924-101500", 2);
            string live = Path.Combine(_dir, Stem + ".txt");
            File.WriteAllText(live, "12 [T1] Boot Tracing on instance:1 ..." + Environment.NewLine
                                    + "13 [T1] a fresh session that never rotated" + Environment.NewLine);

            var chains = TraceLeftoverAdoption.AdoptLeftoverParts(_dir, Stem, _archiveDir);
            Assert.Single(chains);

            Assert.Null(TraceLeftoverAdoption.ChainForLiveLeftover(chains, live));
        }

        /// <summary>
        /// The positive control: a live leftover whose first lines carry the
        /// continuation header naming a part of the chain IS joined to it.
        /// Both header shapes are accepted — bare, as rotation writes it, and
        /// prefixed, as a checkpoint writes it.
        /// </summary>
        [Fact]
        public void A_live_leftover_that_names_its_chain_is_joined_to_it()
        {
            Part("20260924-101500", 1);
            Part("20260924-101500", 2);
            Part("20260923-080000", 1);   // an OLDER chain: the newest-stamp rule would never pick it

            var chains = TraceLeftoverAdoption.AdoptLeftoverParts(_dir, Stem, _archiveDir);
            Assert.Equal(2, chains.Count);

            string live = Path.Combine(_dir, Stem + ".txt");
            // Names the OLDER chain, as a checkpointed live file would (prefixed).
            File.WriteAllText(live,
                "44 [T7] --- trace continues from part 001 (JJFlexRadioTrace-20260923-080000-part-001.txt) — this is part 002 ---"
                + Environment.NewLine + "45 [T7] more" + Environment.NewLine);
            LeftoverChainAdoption joined = TraceLeftoverAdoption.ChainForLiveLeftover(chains, live);
            Assert.NotNull(joined);
            Assert.Equal(new DateTime(2026, 9, 23, 8, 0, 0), joined.StampLocal);

            // Bare, as rotation writes it.
            File.WriteAllText(live,
                "--- trace continues from part 002 (JJFlexRadioTrace-20260924-101500-part-002.txt) — this is part 003 ---"
                + Environment.NewLine);
            joined = TraceLeftoverAdoption.ChainForLiveLeftover(chains, live);
            Assert.NotNull(joined);
            Assert.Equal(new DateTime(2026, 9, 24, 10, 15, 0), joined.StampLocal);

            // A header naming a chain nobody adopted joins nothing.
            File.WriteAllText(live,
                "--- trace continues from part 001 (JJFlexRadioTrace-20200101-000000-part-001.txt) — this is part 002 ---"
                + Environment.NewLine);
            Assert.Null(TraceLeftoverAdoption.ChainForLiveLeftover(chains, live));
        }

        /// <summary>
        /// With a valid sidecar the original identity is recovered — the
        /// positive control for "unknown attribution" above. The part is
        /// skipped by adoption and archived by recovery under the session id
        /// the sidecar names, with the outcome it froze, and no orphan mark.
        /// Run in the production order: adopt, then recover.
        /// </summary>
        [Fact]
        public void A_part_with_a_valid_sidecar_recovers_its_original_identity()
        {
            string path = Part("20260924-101500", 3, "the part whose record survived");
            Guid original = Guid.NewGuid();
            var ticket = new TraceArchiveTicket
            {
                SessionId = original,
                PartNumber = 3,
                IsFinalPart = true,
                SourcePath = path,
                ArchiveRootDir = _archiveDir,
                OutcomeFileTag = TraceSessionOutcome.ConnectionDropped,
                StampLocal = new DateTime(2026, 9, 24, 10, 15, 0),
                Entry = new TraceSessionEntry
                {
                    SessionId = original.ToString(),
                    BootTime = new DateTime(2026, 9, 24, 15, 15, 0, DateTimeKind.Utc),
                    Outcome = TraceSessionOutcome.ConnectionDropped,
                    OutcomeDetail = "the real outcome, frozen at the seal",
                },
            };
            Assert.True(TraceArchiveWorker.WritePendingRecord(ticket, new List<string>()));

            // Adoption in the same boot sees the sidecar and leaves it alone.
            var chains = TraceLeftoverAdoption.AdoptLeftoverParts(_dir, Stem, _archiveDir);
            Assert.Equal(0, Assert.Single(chains).Archived);
            Assert.Empty(Manifest().Entries);

            // Recovery files it under its OWN identity.
            Assert.Equal(1, TraceArchiveWorker.RecoverPending(_dir));
            Assert.True(TraceArchiveWorker.Drain(TimeSpan.FromSeconds(60)));
            TraceSessionEntry entry = Assert.Single(Manifest().Entries);
            Assert.Equal(original.ToString(), entry.SessionId);
            Assert.Equal(TraceSessionOutcome.ConnectionDropped, entry.Outcome);
            Assert.Equal("the real outcome, frozen at the seal", entry.OutcomeDetail);
            Assert.Null(entry.Orphaned);
            Assert.Equal(3, entry.PartNumber);
        }

        /// <summary>
        /// An unparted seal that lost its sidecar stays LOOSE at restart —
        /// adoption matches parts only — and the plain-text sweep keeps it as
        /// unarchived evidence. With a sidecar, recovery finishes it under its
        /// own identity. Neither path invents a session for it.
        /// </summary>
        [Fact]
        public void An_unparted_leftover_stays_loose_without_a_sidecar_and_is_finished_with_one()
        {
            string loose = Path.Combine(_dir, Stem + "-20260924-090000.txt");
            File.WriteAllText(loose, "an unparted seal whose record never wrote" + Environment.NewLine);

            var chains = TraceLeftoverAdoption.AdoptLeftoverParts(_dir, Stem, _archiveDir);
            Assert.Empty(chains);
            Assert.Equal(0, TraceArchiveWorker.RecoverPending(_dir));
            Assert.Empty(Manifest().Entries);
            Assert.True(File.Exists(loose));
            DateTime now = DateTime.UtcNow;
            Assert.Equal(PlainTextTraceVerdict.KeptBecauseUnarchived,
                TraceArchiveWorker.ClassifyPlainTextTrace(loose, now.AddDays(-2), now, 1,
                    SessionArchive.ArchivedSourceNames(_archiveDir)));

            Guid original = Guid.NewGuid();
            var ticket = new TraceArchiveTicket
            {
                SessionId = original, PartNumber = 0, IsFinalPart = false,
                SourcePath = loose, ArchiveRootDir = _archiveDir,
                OutcomeFileTag = TraceSessionOutcome.CleanExit,
                StampLocal = new DateTime(2026, 9, 24, 9, 0, 0),
                Entry = new TraceSessionEntry { SessionId = original.ToString(), Outcome = TraceSessionOutcome.CleanExit },
            };
            Assert.True(TraceArchiveWorker.WritePendingRecord(ticket, new List<string>()));
            Assert.Equal(1, TraceArchiveWorker.RecoverPending(_dir));
            Assert.True(TraceArchiveWorker.Drain(TimeSpan.FromSeconds(60)));
            TraceSessionEntry entry = Assert.Single(Manifest().Entries);
            Assert.Equal(original.ToString(), entry.SessionId);
            Assert.Null(entry.Orphaned);
        }

        [Fact]
        public void The_header_and_name_parsers_read_what_rotation_and_checkpoints_write()
        {
            Assert.True(TraceLeftoverAdoption.TryParseContinuationHeader(
                "--- trace continues from part 001 (JJFlexRadioTrace-20260924-101500-part-001.txt) — this is part 002 ---",
                out string name, out int from, out int thisPart));
            Assert.Equal("JJFlexRadioTrace-20260924-101500-part-001.txt", name);
            Assert.Equal(1, from);
            Assert.Equal(2, thisPart);
            Assert.False(TraceLeftoverAdoption.TryParseContinuationHeader("CaptureState: capture=off", out _, out _, out _));

            Assert.True(TraceLeftoverAdoption.TryParsePartName("JJFlexRadioTrace-20260924-101500-part-007-2.txt",
                out string stem, out DateTime stamp, out int part));
            Assert.Equal("JJFlexRadioTrace", stem);
            Assert.Equal(new DateTime(2026, 9, 24, 10, 15, 0), stamp);
            Assert.Equal(7, part);
            Assert.False(TraceLeftoverAdoption.TryParsePartName("JJFlexRadioTrace-20260924-101500.txt", out _, out _, out _));
        }

        /// <summary>
        /// The application really delegates to this module, in the production
        /// order, and the old inventing body is gone. Source-read, because the
        /// VB application cannot be referenced here.
        /// </summary>
        [Fact]
        public void The_application_adopts_through_this_module_and_no_longer_invents_killed()
        {
            string source = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "globals.vb"));
            int method = source.IndexOf("Private Function ArchiveLeftoverTraceChains(", StringComparison.Ordinal);
            Assert.True(method > 0, "ArchiveLeftoverTraceChains is gone");
            int end = source.IndexOf("End Function", method, StringComparison.Ordinal);
            string body = source.Substring(method, end - method);
            Assert.Contains("TraceLeftoverAdoption.AdoptLeftoverParts(", body, StringComparison.Ordinal);
            Assert.Contains("TraceLeftoverAdoption.ChainForLiveLeftover(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TraceSessionOutcome.Killed", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Leftover trace parts adopted at next launch", body, StringComparison.Ordinal);

            // Positive control for the reader: the one place the live leftover
            // IS marked killed — after the file's own header joined it — is
            // still there, in RotateBootTraceIfNeeded.
            int rotate = source.IndexOf("Private Sub RotateBootTraceIfNeeded()", StringComparison.Ordinal);
            int rotateEnd = source.IndexOf("End Sub", rotate, StringComparison.Ordinal);
            string rotateBody = source.Substring(rotate, rotateEnd - rotate);
            Assert.Contains("adopted.Session.MarkOutcome(TraceSessionOutcome.Killed", rotateBody, StringComparison.Ordinal);
        }
    }
}
