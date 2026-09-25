using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using JJTrace;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// A line about a session that arrives after that session was sealed,
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
                TraceCoordinator.TrySeal(new TraceSealRequest
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
            TraceCoordinator.TrySeal(new TraceSealRequest
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
        }

        /// <summary>
        /// The common case: the other operation's archive commits, and the
        /// line lands beside the zip, with a header naming the session. A
        /// second line appends under the same header. No sink exists at any
        /// point (the seal opened nothing).
        /// </summary>
        [Fact]
        public void A_line_about_a_sealed_session_lands_beside_its_committed_archive_with_no_sink_at_all()
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

            // Positive controls: the sealed archive does not hold it, and a
            // session this coordinator never sealed gets null, not a file.
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
        /// The edge: the other operation's seal could not move the file
        /// aside (a retained seal, H9 blocker 1), so there is no ticket and
        /// no archive. The line lands beside the retained file at the live
        /// path — and when the operator's retry reclaims that file, the
        /// late-evidence file moves with it.
        /// </summary>
        [Fact]
        public void A_line_about_a_retained_seal_sits_beside_the_retained_file_and_moves_with_it()
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
        }
    }
}
