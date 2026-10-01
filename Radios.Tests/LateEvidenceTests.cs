using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using JJTrace;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// A line about a session that arrives after that session was archived,
    /// with nothing live to take it, has a destination that needs no sink:
    /// a late-evidence file beside the session's archive (Sol's review of
    /// H9, blocker 3). Real coordinator, real files, no window, no radio.
    /// </summary>
    // The suite runs sequentially by assembly policy (TestParallelism.cs).
    public sealed class LateEvidenceTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;
        private readonly string _archiveDir;
        private readonly string _livePath;
        private readonly string _savedArchiveRoot;
        private readonly TraceSession _savedSession;
        private readonly bool _savedOn;
        private readonly TraceLevel _savedLevel;

        public LateEvidenceTests(ITestOutputHelper output)
        {
            _out = output;
            _savedLevel = Tracing.TheSwitch.Level;
            _savedOn = Tracing.On;
            _savedArchiveRoot = TraceCoordinator.ArchiveRootDir;
            _savedSession = TraceSessionContext.Current;
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-h10-late-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _archiveDir = Path.Combine(_dir, "Traces");
            _livePath = Path.Combine(_dir, "JJFlexRadioTrace.txt");
            Tracing.TheSwitch.Level = TraceLevel.Verbose;
            TraceCoordinator.ArchiveRootDir = _archiveDir;
            RestoreSession(null);
            TraceCoordinator.ResetClaimsForTests();
            Tracing.On = true;
        }

        public void Dispose()
        {
            if (TraceCoordinator.CurrentHandle != null)
            {
                TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    ShutdownAuthority = true, Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.None, OperationId = Guid.NewGuid(),
                });
            }
            TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
            TraceCoordinator.ResetClaimsForTests();
            TraceCoordinator.ArchiveRootDir = _savedArchiveRoot;
            TraceCoordinator.SetStandingIntent(true, TraceLevel.Info);
            RestoreSession(_savedSession);
            Tracing.TheSwitch.Level = _savedLevel;
            Tracing.On = _savedOn;
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static void RestoreSession(TraceSession session) =>
            typeof(TraceCoordinator)
                .GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { session });

        private TraceSessionHandle Open()
        {
            TraceTransitionResult r = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, r.Status);
            return r.Successor;
        }

        private static TraceTransitionResult Stop(TraceSessionHandle h, TraceResumeIntent resume = TraceResumeIntent.None) =>
            TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = h, OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit, Resume = resume,
            });

        [Fact]
        public void The_path_is_named_after_the_archive_or_the_raw_file()
        {
            Assert.Equal(@"C:\t\Traces\2026\09\trace-20260924-201500-clean_exit.late-evidence.txt",
                         SessionArchive.LateEvidencePathFor(@"C:\t\Traces\2026\09\trace-20260924-201500-clean_exit.zip"));
            Assert.Equal(@"C:\t\JJFlexRadioTrace-20260924-201500.txt.late-evidence.txt",
                         SessionArchive.LateEvidencePathFor(@"C:\t\JJFlexRadioTrace-20260924-201500.txt"));
            Assert.Null(SessionArchive.LateEvidencePathFor(null));
            // Neither is a session archive to the walk that excludes trace-*.zip.
            Assert.DoesNotContain(".zip", SessionArchive.LateEvidencePathFor(@"C:\t\trace-x.zip"), StringComparison.OrdinalIgnoreCase);

            // And the name says what it sits beside, both ways round.
            Assert.True(SessionArchive.IsLateEvidencePath(@"C:\t\JJFlexRadioTrace-20260924-201500.txt.late-evidence.txt"));
            Assert.False(SessionArchive.IsLateEvidencePath(@"C:\t\JJFlexRadioTrace-20260924-201500.txt"));
            Assert.False(SessionArchive.IsLateEvidencePath(null));
            Assert.Equal(@"C:\t\JJFlexRadioTrace-20260924-201500.txt",
                         SessionArchive.LateEvidenceOwnerOf(@"C:\t\JJFlexRadioTrace-20260924-201500.txt.late-evidence.txt"));
            Assert.Equal(@"C:\t\Traces\2026\09\trace-20260924-201500-clean_exit.zip",
                         SessionArchive.LateEvidenceOwnerOf(@"C:\t\Traces\2026\09\trace-20260924-201500-clean_exit.late-evidence.txt"));
            Assert.Null(SessionArchive.LateEvidenceOwnerOf(@"C:\t\JJFlexRadioTrace-20260924-201500.txt"));
        }

        /// <summary>
        /// The common case: the other operation's archive commits, and the
        /// line lands beside the zip, with a header naming the session. A
        /// second line appends under the same header. No sink exists at any
        /// point (the archive opened nothing).
        /// </summary>
        [Fact]
        public void A_line_about_a_archived_session_lands_beside_its_committed_archive_with_no_sink_at_all()
        {
            TraceSessionHandle old = Open();
            Tracing.TraceLine("something happened", TraceLevel.Info);
            TraceTransitionResult stop = Stop(old);
            Assert.Equal(TraceTransition.Accepted, stop.Status);
            Assert.Null(TraceCoordinator.CurrentHandle);
            Assert.False(TraceCoordinator.Observe().Recording);

            string first = TraceCoordinator.KeepLateEvidence(old, "captureMeters: state=tx paTemp min=41 max=43 last=42 n=3 degC partial=connection_dropped", TimeSpan.FromSeconds(60));
            Assert.NotNull(first);
            Assert.True(stop.Ticket.Completion.IsCompleted, "the call returned before the archive committed");
            string zip = stop.Ticket.Completion.Result.ArchiveFullPath;
            Assert.True(stop.Ticket.Completion.Result.ArchiveCommitted);
            Assert.Equal(SessionArchive.LateEvidencePathFor(zip), first);
            Assert.Equal(Path.GetDirectoryName(zip), Path.GetDirectoryName(first));

            string second = TraceCoordinator.KeepLateEvidence(old, "a second late line", TimeSpan.FromSeconds(60));
            Assert.Equal(first, second);
            string[] lines = File.ReadAllLines(first);
            foreach (string l in lines) _out.WriteLine(l);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("Late evidence for trace session " + old.SessionId, lines[0], StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(zip), lines[0], StringComparison.Ordinal);
            Assert.EndsWith("n=3 degC partial=connection_dropped", lines[1], StringComparison.Ordinal);
            Assert.Matches(@"^\d+ \[T\d+", lines[1]);   // each line carries its own trace prefix
            Assert.EndsWith("a second late line", lines[2], StringComparison.Ordinal);

            // Positive controls: the archived archive does not hold it, and a
            // session this coordinator never archived gets null, not a file.
            string extracted = SessionArchive.ExtractTraceText(zip, Path.Combine(_dir, "extract"));
            Assert.DoesNotContain("partial=connection_dropped", File.ReadAllText(extracted), StringComparison.Ordinal);
            var stranger = (TraceSessionHandle)typeof(TraceSessionHandle)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                .Invoke(new object[] { new TraceSession() });
            Assert.Null(TraceCoordinator.KeepLateEvidence(stranger, "nowhere to go", TimeSpan.FromSeconds(1)));
            Assert.Null(TraceCoordinator.KeepLateEvidence(null, "nowhere to go", TimeSpan.FromSeconds(1)));
            Assert.Null(TraceCoordinator.KeepLateEvidence(old, "", TimeSpan.FromSeconds(1)));
        }

        /// <summary>
        /// When the archive cannot commit — no archive root here — the line
        /// lands beside the retained raw file instead, so it is still next to
        /// the evidence it describes.
        /// </summary>
        [Fact]
        public void When_the_archive_does_not_commit_the_line_lands_beside_the_raw_file()
        {
            TraceCoordinator.ArchiveRootDir = null;
            TraceSessionHandle old = Open();
            TraceTransitionResult stop = Stop(old);
            Assert.Equal(TraceTransition.Accepted, stop.Status);
            Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            Assert.False(stop.Ticket.Completion.Result.ArchiveCommitted);
            Assert.Equal("no_archive_root", stop.Ticket.Completion.Result.FailureStage);

            string kept = TraceCoordinator.KeepLateEvidence(old, "late line", TimeSpan.FromSeconds(60));
            Assert.Equal(SessionArchive.LateEvidencePathFor(stop.Ticket.SourcePath), kept);
            Assert.True(File.Exists(stop.Ticket.SourcePath), "the raw file it sits beside is gone");
            Assert.Contains("late line", File.ReadAllText(kept), StringComparison.Ordinal);
        }

        /// <summary>
        /// The file ages out with the archive it belongs to: the prune that
        /// deletes the zip deletes the late-evidence file beside it, and so
        /// does an explicit delete from the saved-logs list.
        /// </summary>
        [Fact]
        public void The_file_is_deleted_with_the_archive_it_sits_beside()
        {
            TraceSessionHandle a = Open();
            TraceTransitionResult stopA = Stop(a);
            string keptA = TraceCoordinator.KeepLateEvidence(a, "late for a", TimeSpan.FromSeconds(60));
            Assert.NotNull(keptA);
            string zipA = stopA.Ticket.Completion.Result.ArchiveFullPath;

            TraceSessionHandle b = Open();
            TraceTransitionResult stopB = Stop(b);
            string keptB = TraceCoordinator.KeepLateEvidence(b, "late for b", TimeSpan.FromSeconds(60));
            Assert.NotNull(keptB);
            string zipB = stopB.Ticket.Completion.Result.ArchiveFullPath;
            Assert.NotEqual(keptA, keptB);

            // Explicit delete of A only.
            int deleted = SessionArchive.DeleteEntries(_archiveDir, new[] { stopA.Ticket.Completion.Result.ArchiveRelativeName });
            Assert.Equal(1, deleted);
            Assert.False(File.Exists(zipA));
            Assert.False(File.Exists(keptA), "the late-evidence file outlived its archive");
            Assert.True(File.Exists(zipB));
            Assert.True(File.Exists(keptB), "the wrong late-evidence file was deleted");

            // Prune of everything older than the future: B goes, with its file.
            int pruned = SessionArchive.PruneOlderThan(_archiveDir, 1);
            Assert.Equal(0, pruned);   // B is minutes old; a one-day window keeps it
            Assert.True(File.Exists(keptB));
            // Age it out by manifest boot time rather than by waiting a day.
            TraceManifest manifest = TraceManifest.Load(Path.Combine(_archiveDir, SessionArchive.ManifestFileName));
            Assert.Single(manifest.Entries);
            TraceManifest.Mutate(Path.Combine(_archiveDir, SessionArchive.ManifestFileName), m =>
            {
                m.Entries[0].BootTime = DateTime.UtcNow.AddDays(-40);
                return true;
            });
            pruned = SessionArchive.PruneOlderThan(_archiveDir, 30);
            Assert.Equal(1, pruned);
            Assert.False(File.Exists(zipB));
            Assert.False(File.Exists(keptB), "the late-evidence file outlived the prune");
        }

        /// <summary>
        /// The edge: the other operation's archive could not move the file
        /// aside (a retained archive, H9 blocker 1), so there is no ticket and
        /// no archive. The line lands beside the retained file at the live
        /// path — and when the operator's retry reclaims that file, the
        /// late-evidence file moves with it.
        /// </summary>
        [Fact]
        public void A_line_about_a_retained_archive_sits_beside_the_retained_file_and_moves_with_it()
        {
            TraceSessionHandle old = Open();
            Tracing.TraceLine("before the failed detach", TraceLevel.Info);
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPath(_livePath, boot);
            Directory.CreateDirectory(target);   // the move is blocked by a directory at the target
            TraceTransitionResult failed = Stop(old);
            Assert.Equal(TraceTransition.Failed, failed.Status);
            Assert.Equal("detach", failed.FailedStage);
            Assert.Equal(_livePath, failed.RetainedSourcePath);
            Assert.Null(failed.Ticket);

            string kept = TraceCoordinator.KeepLateEvidence(old, "late for the retained one", TimeSpan.FromSeconds(1));
            Assert.Equal(SessionArchive.LateEvidencePathFor(_livePath), kept);
            Assert.Contains("late for the retained one", File.ReadAllText(kept), StringComparison.Ordinal);
            Assert.Contains("before the failed detach", File.ReadAllText(_livePath), StringComparison.Ordinal);

            // The retry: unblock, turn the log on, and the reclaim moves both.
            Directory.Delete(target);
            TraceTransitionResult on = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, on.Status);
            Assert.NotNull(on.Reclaimed);
            Assert.Equal(old.SessionId, on.Reclaimed.SessionId);
            Assert.False(File.Exists(kept), "the late-evidence file was left behind at the live path");
            string moved = SessionArchive.LateEvidencePathFor(on.Reclaimed.SourcePath);
            Assert.True(File.Exists(moved), "the late-evidence file did not move with the reclaimed file");
            Assert.Contains("late for the retained one", File.ReadAllText(moved), StringComparison.Ordinal);

            // Sol's review of H10, blocker 3, the failed-detach-then-reclaim
            // path, through archive completion: the reclaimed ticket's
            // archive commits, and the file follows it from the raw path to
            // the zip's side. H10 stopped at the reclaim.
            Assert.True(on.Reclaimed.Completion.Wait(TimeSpan.FromSeconds(60)), "the reclaimed ticket's archive never completed");
            Assert.True(on.Reclaimed.Completion.Result.ArchiveCommitted);
            string zipSide = SessionArchive.LateEvidencePathFor(on.Reclaimed.Completion.Result.ArchiveFullPath);
            Assert.False(File.Exists(moved), "the late-evidence file stayed beside the raw file after its archive committed");
            Assert.True(File.Exists(zipSide), "the late-evidence file did not follow the reclaimed session's archive");
            Assert.Contains("late for the retained one", File.ReadAllText(zipSide), StringComparison.Ordinal);
            Assert.Equal(on.Reclaimed.Completion.Result.ArchiveFullPath, on.Reclaimed.CommittedArchivePath);
        }

        // ── Sol's review of H10, blocker 3: the file follows its archive ───

        /// <summary>
        /// The slow archive. The other operation's archive is held on the
        /// worker; the writer's wait expires and the line lands beside the
        /// raw file, as H10 designed. Then the archive commits — and the
        /// file follows it: gone from the raw side, present beside the zip,
        /// one header, and a later line lands there directly. Then
        /// KeptForever covers it, with the prune that would otherwise take
        /// the zip as the control.
        /// </summary>
        [Fact]
        public void A_line_kept_beside_the_raw_file_while_its_archive_is_slow_follows_the_archive_when_it_commits()
        {
            TraceSessionHandle old = Open();
            Tracing.TraceLine("something happened", TraceLevel.Info);
            using var atWorker = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            TraceArchiveWorker.BeforeArchiveForTests = t =>
            {
                if (t.SessionId != old.SessionId) return;
                atWorker.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            try
            {
                TraceTransitionResult stop = Stop(old);
                Assert.Equal(TraceTransition.Accepted, stop.Status);
                Assert.True(atWorker.Wait(TimeSpan.FromSeconds(10)), "the archive worker never reached the ticket");
                Assert.False(stop.Ticket.Completion.IsCompleted);
                Assert.Null(stop.Ticket.CommittedArchivePath);

                // The wait expires: the line is kept beside the raw file.
                string rawSide = SessionArchive.LateEvidencePathFor(stop.Ticket.SourcePath);
                string kept = TraceCoordinator.KeepLateEvidence(old, "late while the archive was slow", TimeSpan.FromMilliseconds(200));
                Assert.Equal(rawSide, kept);
                Assert.True(File.Exists(rawSide));
                Assert.False(stop.Ticket.Completion.IsCompleted,
                             "the archive committed inside the short wait, so the expiry was not exercised");
                // Instrument control: the sweep's own glob reaches this file,
                // which is why it used to be aged out.
                Assert.Contains(rawSide, Directory.GetFiles(_dir, "JJFlexRadioTrace-*.txt"), StringComparer.OrdinalIgnoreCase);

                // The archive commits.
                release.Set();
                Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
                Assert.True(stop.Ticket.Completion.Result.ArchiveCommitted);
                string zip = stop.Ticket.Completion.Result.ArchiveFullPath;
                string zipSide = SessionArchive.LateEvidencePathFor(zip);
                Assert.Equal(zip, stop.Ticket.CommittedArchivePath);
                Assert.False(File.Exists(rawSide), "the late-evidence file stayed beside the raw file after its archive committed");
                Assert.True(File.Exists(zipSide), "the late-evidence file did not follow its archive");
                string[] lines = File.ReadAllLines(zipSide);
                foreach (string l in lines) _out.WriteLine(l);
                Assert.Equal(2, lines.Length);
                Assert.StartsWith("Late evidence for trace session " + old.SessionId, lines[0], StringComparison.Ordinal);
                Assert.EndsWith("late while the archive was slow", lines[1], StringComparison.Ordinal);

                // A later line goes straight beside the zip, under the one header.
                Assert.Equal(zipSide, TraceCoordinator.KeepLateEvidence(old, "late after the commit", TimeSpan.FromSeconds(60)));
                lines = File.ReadAllLines(zipSide);
                Assert.Equal(3, lines.Length);
                Assert.Single(lines, l => l.StartsWith("Late evidence for trace session", StringComparison.Ordinal));
                Assert.EndsWith("late after the commit", lines[2], StringComparison.Ordinal);

                // KeptForever covers it: aged past the window and marked, the
                // prune leaves the zip and the file alone.
                string manifestPath = Path.Combine(_archiveDir, SessionArchive.ManifestFileName);
                TraceManifest.Mutate(manifestPath, m =>
                {
                    TraceSessionEntry e = m.Entries.Single(x => x.SessionId == old.SessionId.ToString());
                    e.KeptForever = true;
                    e.BootTime = DateTime.UtcNow.AddDays(-40);
                    return true;
                });
                Assert.Equal(0, SessionArchive.PruneOlderThan(_archiveDir, 30));
                Assert.True(File.Exists(zip));
                Assert.True(File.Exists(zipSide), "KeptForever did not cover the late-evidence file");

                // Positive control: unmarked, the same prune takes both.
                TraceManifest.Mutate(manifestPath, m =>
                {
                    m.Entries.Single(x => x.SessionId == old.SessionId.ToString()).KeptForever = false;
                    return true;
                });
                Assert.Equal(1, SessionArchive.PruneOlderThan(_archiveDir, 30));
                Assert.False(File.Exists(zip));
                Assert.False(File.Exists(zipSide), "the late-evidence file outlived the prune");
            }
            finally
            {
                release.Set();
                TraceArchiveWorker.BeforeArchiveForTests = null;
            }
        }

        /// <summary>
        /// The sweep. A raw-side file whose owner is archived (a crash
        /// between the manifest commit and the move leaves exactly this) is
        /// not aged out at any age: the verdict is FollowsArchive and the
        /// re-home puts it beside the zip, merging under one header if a
        /// zip-side file already exists. A file whose owner is still on disk
        /// and unarchived is kept for as long as the owner is, however old;
        /// an orphan is judged on its own age by the unarchived rule; a
        /// pending owner keeps its file. The raw trace itself is the
        /// positive control at each step: the rule the file used to inherit.
        /// </summary>
        [Fact]
        public void The_sweep_re_homes_a_file_whose_archive_committed_and_keeps_one_whose_owner_is_still_raw()
        {
            TraceSessionHandle old = Open();
            Tracing.TraceLine("something happened", TraceLevel.Info);
            TraceTransitionResult stop = Stop(old);
            Assert.Equal(TraceTransition.Accepted, stop.Status);
            Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(stop.Ticket.Completion.Result.ArchiveCommitted);
            string raw = stop.Ticket.SourcePath;
            string zip = stop.Ticket.Completion.Result.ArchiveFullPath;
            string rawSide = SessionArchive.LateEvidencePathFor(raw);
            string zipSide = SessionArchive.LateEvidencePathFor(zip);
            DateTime now = DateTime.UtcNow;
            HashSet<string> archived = SessionArchive.ArchivedSourceNames(_archiveDir);
            Assert.Contains(Path.GetFileName(raw), archived);   // instrument control: the manifest names the owner

            // The crash-orphaned raw-side file, written after the commit.
            File.WriteAllText(rawSide, "Late evidence for trace session " + old.SessionId + ". stand-in header" + Environment.NewLine
                                       + "1 [T1] orphaned by a crash" + Environment.NewLine);
            Assert.Equal(PlainTextTraceVerdict.FollowsArchive,
                         TraceArchiveWorker.ClassifyPlainTextTrace(rawSide, now.AddHours(-1), now, 1, archived));
            Assert.Equal(PlainTextTraceVerdict.FollowsArchive,
                         TraceArchiveWorker.ClassifyPlainTextTrace(rawSide, now.AddDays(-40), now, 1, archived));
            // Positive control: the archived raw trace itself ages out on the
            // ordinary window — the verdict the file used to get.
            Assert.Equal(PlainTextTraceVerdict.Delete,
                         TraceArchiveWorker.ClassifyPlainTextTrace(raw, now.AddDays(-2), now, 1, archived));

            Assert.True(SessionArchive.ReHomeLateEvidence(_archiveDir, rawSide));
            Assert.False(File.Exists(rawSide));
            Assert.True(File.Exists(zipSide));
            Assert.Contains("orphaned by a crash", File.ReadAllText(zipSide), StringComparison.Ordinal);
            Assert.False(SessionArchive.ReHomeLateEvidence(_archiveDir, rawSide));   // nothing left to move

            // The merge: a zip-side file exists and a raw-side one turns up.
            // One file, one header, both lines, and a line saying where the
            // moved ones came from.
            File.WriteAllText(rawSide, "Late evidence for trace session " + old.SessionId + ". second header" + Environment.NewLine
                                       + "2 [T1] the raw-side line" + Environment.NewLine);
            Assert.True(SessionArchive.ReHomeLateEvidence(_archiveDir, rawSide));
            string[] merged = File.ReadAllLines(zipSide);
            foreach (string l in merged) _out.WriteLine(l);
            Assert.Single(merged, l => l.StartsWith("Late evidence for trace session", StringComparison.Ordinal));
            Assert.Contains(merged, l => l.EndsWith("orphaned by a crash", StringComparison.Ordinal));
            Assert.Contains(merged, l => l.EndsWith("the raw-side line", StringComparison.Ordinal));
            Assert.Contains(merged, l => l.Contains("first kept beside " + Path.GetFileName(rawSide), StringComparison.Ordinal));
            Assert.False(File.Exists(rawSide));
            // A zip-side file is never "re-homed" anywhere.
            Assert.False(SessionArchive.ReHomeLateEvidence(_archiveDir, zipSide));
            Assert.True(File.Exists(zipSide));

            // An unarchived owner still on disk: kept, however old, and not
            // counted as an unarchived trace of its own.
            string rawB = Path.Combine(_dir, "JJFlexRadioTrace-20260901-120000.txt");
            File.WriteAllText(rawB, "x");
            string sideB = SessionArchive.LateEvidencePathFor(rawB);
            File.WriteAllText(sideB, "y");
            var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(PlainTextTraceVerdict.Keep,
                         TraceArchiveWorker.ClassifyPlainTextTrace(sideB, now.AddDays(-40), now, 1, none));
            Assert.Equal(PlainTextTraceVerdict.Keep,
                         TraceArchiveWorker.ClassifyPlainTextTrace(sideB, now.AddDays(-40), now, 1, null));
            // Positive control: the owner itself, at that age, is past the
            // unarchived window.
            Assert.Equal(PlainTextTraceVerdict.Delete,
                         TraceArchiveWorker.ClassifyPlainTextTrace(rawB, now.AddDays(-40), now, 1, none));
            // A pending owner keeps its file whatever the manifest says.
            File.WriteAllText(TraceArchiveWorker.PendingPathFor(rawB), "{}");
            Assert.Equal(PlainTextTraceVerdict.Keep,
                         TraceArchiveWorker.ClassifyPlainTextTrace(sideB, now.AddDays(-40), now, 1,
                             new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(rawB) }));
            File.Delete(TraceArchiveWorker.PendingPathFor(rawB));

            // An orphan — owner gone, no archive — is the only copy of what
            // it holds, and ages out on the unarchived window, on its own.
            File.Delete(rawB);
            Assert.Equal(PlainTextTraceVerdict.Keep,
                         TraceArchiveWorker.ClassifyPlainTextTrace(sideB, now.AddHours(-1), now, 1, none));
            Assert.Equal(PlainTextTraceVerdict.KeptBecauseUnarchived,
                         TraceArchiveWorker.ClassifyPlainTextTrace(sideB, now.AddDays(-2), now, 1, none));
            Assert.Equal(PlainTextTraceVerdict.Delete,
                         TraceArchiveWorker.ClassifyPlainTextTrace(sideB, now.AddDays(-(SessionArchive.DefaultRetentionDays + 1)), now, 1, none));
            // And an orphan with no archive has nowhere to be re-homed to.
            Assert.False(SessionArchive.ReHomeLateEvidence(_archiveDir, sideB));
            Assert.True(File.Exists(sideB));
        }
    }
}
