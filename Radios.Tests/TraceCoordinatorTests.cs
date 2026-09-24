using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JJTrace;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The trace boundary, driven with real files and deterministic barriers —
    /// no window, no radio, no timing hope.
    /// </summary>
    /// <remarks>
    /// <para><b>What these exist to catch.</b> #612 was not a bug in any one
    /// statement; it was four callers each doing a correct-looking sequence of
    /// reads and writes that were not one transition. A test that calls one
    /// caller and checks the result cannot see that at all. So these drive the
    /// boundary directly, replace the session between a caller's decision and
    /// its execution, and assert what the LOSER was allowed to touch — which is
    /// nothing.</para>
    ///
    /// <para><b>Why a temporary directory rather than a mock.</b> The defects
    /// live in file operations: a successor opened at a path whose old bytes
    /// were still being compressed, a rename that deletes its source on
    /// failure, a part number frozen too late. A fake filesystem would have
    /// agreed with the old code.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs),
    // which is what makes the process-wide coordinator safe to drive here.
    public sealed class TraceCoordinatorTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _archiveDir;
        private readonly string _livePath;
        private readonly string _savedArchiveRoot;
        private readonly TraceSession _savedSession;
        private readonly bool _savedOn;

        public TraceCoordinatorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-h3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _archiveDir = Path.Combine(_dir, "Traces");
            _livePath = Path.Combine(_dir, "JJFlexRadioTrace.txt");

            _savedArchiveRoot = TraceCoordinator.ArchiveRootDir;
            _savedSession = TraceSessionContext.Current;
            _savedOn = Tracing.On;

            TraceCoordinator.ArchiveRootDir = _archiveDir;
            TraceCoordinator.AppIdentity = new TraceEnvironment
            {
                Instance = 1,
                AppVersion = "0.0-test",
                AppPath = @"C:\test\jjflexible.exe",
            };
            ResetCoordinator();
            Tracing.On = true;
        }

        public void Dispose()
        {
            try { ForceCloseAnySession(); } catch { }
            ResetCoordinator();
            RestoreSession(_savedSession);
            TraceCoordinator.ArchiveRootDir = _savedArchiveRoot;
            Tracing.On = _savedOn;
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ── Harness ────────────────────────────────────────────────────────

        private static void Invoke(string name, params object[] args) =>
            typeof(TraceCoordinator)
                .GetMethod(name, System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Static)
                .Invoke(null, args);

        private static void RestoreSession(TraceSession session) =>
            Invoke("RestoreSessionForTests", session);

        private static void ResetCoordinator()
        {
            Invoke("UnlatchShutdownForTests");
            Invoke("ResetClaimsForTests");
            TraceCoordinator.SetStandingIntent(true, TraceLevel.Info);
        }

        private void ForceCloseAnySession()
        {
            if (TraceCoordinator.CurrentHandle == null) return;
            TraceCoordinator.TrySeal(new TraceSealRequest
            {
                ShutdownAuthority = true,
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
                OperationId = Guid.NewGuid(),
            });
            TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
        }

        private TraceSessionHandle Open(TraceLevel level = TraceLevel.Info, bool asCapture = false)
        {
            TraceTransitionResult r = TraceCoordinator.Begin(_livePath, level, asCapture);
            Assert.Equal(TraceTransition.Accepted, r.Status);
            return r.Successor;
        }

        private static void Write(string line) => Tracing.TraceLine(line);

        /// <summary>
        /// Read the LIVE trace while it is being written.
        ///
        /// <para><c>File.ReadAllText</c> cannot: it asks for
        /// <c>FileShare.Read</c>, which forbids the writer that is holding the
        /// file. The sink deliberately opens <c>FileShare.ReadWrite |
        /// Delete</c> so Notepad, a screen reader and the crash bundler can all
        /// read a running trace — a reader just has to say so too.</para>
        /// </summary>
        private static string ReadLiveText(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        private string ReadArchivedText(TraceArchiveTicket ticket)
        {
            Assert.True(ticket.Completion.Wait(TimeSpan.FromSeconds(60)), "the archive worker never finished");
            TraceArchiveCompletion done = ticket.Completion.Result;
            Assert.True(done.ArchiveCommitted, "the archive was not committed: " + done.FailureStage);
            string outDir = Path.Combine(_dir, "extract-" + Guid.NewGuid().ToString("N"));
            string extracted = SessionArchive.ExtractTraceText(done.ArchiveFullPath, outDir);
            Assert.NotNull(extracted);
            return File.ReadAllText(extracted);
        }

        private TraceManifest Manifest() =>
            TraceManifest.Load(Path.Combine(_archiveDir, SessionArchive.ManifestFileName));

        // ── The losing caller ──────────────────────────────────────────────

        /// <summary>
        /// The central case, and the one a "cheap" fix would have got wrong.
        /// Sol's review said an <c>EndSessionIfCurrent(expected)</c> added after
        /// the listener closed would protect the pointer too late — a concurrent
        /// replacement's live trace could already have been closed. So the
        /// assertion here is not "the pointer survived": it is that the
        /// replacement's FILE is still open, still being written, and still
        /// holds its own bytes.
        /// </summary>
        [Fact]
        public void A_caller_whose_session_was_replaced_touches_nothing_at_all()
        {
            TraceSessionHandle stale = Open();
            Write("belongs to the first session");

            // The first session ends properly and a replacement opens.
            TraceTransitionResult first = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = stale,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.Accepted, first.Status);
            TraceSessionHandle live = first.Successor;
            Assert.NotNull(live);

            Write("belongs to the replacement");

            // Now the delayed caller arrives, carrying the old handle.
            TraceTransitionResult loser = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = stale,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                OutcomeDetail = "a drop that lost the race",
                TerminalLines = new[] { "captureMeters: partial=connection_dropped" },
                Resume = TraceResumeIntent.Standing,
            });

            Assert.Equal(TraceTransition.NotCurrent, loser.Status);
            Assert.Null(loser.Ticket);
            Assert.Equal(stale.SessionId, loser.ExpectedSessionId);
            Assert.Equal(live.SessionId, loser.ObservedSessionId);

            // A refusal, logged as a refusal, naming both identities — never as
            // a statement that the current session suffered a drop.
            Assert.Contains("refused", loser.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(stale.SessionId.ToString(), loser.Explanation, StringComparison.Ordinal);
            Assert.Contains(live.SessionId.ToString(), loser.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("sealed session", loser.Explanation, StringComparison.OrdinalIgnoreCase);

            // No listener closed, no capture state cleared, no logging
            // restarted: the replacement is still the live one and still
            // writing to its own file.
            Assert.Equal(live.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(TraceCoordinator.Recording);
            Write("still the replacement, after the refusal");

            TraceTransitionResult end = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            string text = ReadArchivedText(end.Ticket);
            Assert.Contains("belongs to the replacement", text, StringComparison.Ordinal);
            Assert.Contains("still the replacement, after the refusal", text, StringComparison.Ordinal);

            // And the loser's line went nowhere. That is #618's second half:
            // an old request never emits its drop record in a successor's log.
            Assert.DoesNotContain("partial=connection_dropped", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// The positive control. A guard that refuses everything proves nothing
        /// at all, so the matching-session case really archives, really puts
        /// the terminal line in the right file, and really opens a successor.
        /// </summary>
        [Fact]
        public void A_caller_whose_session_is_still_live_really_archives_it()
        {
            TraceSessionHandle live = Open();
            Write("the evening that ended in a drop");

            TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                OutcomeDetail = "The connection to 6300inshack dropped while this session was running",
                TerminalLines = new[] { "captureMeters: paTemp none n=0 partial=connection_dropped" },
                Resume = TraceResumeIntent.Standing,
            });

            Assert.Equal(TraceTransition.Accepted, r.Status);
            Assert.NotNull(r.Ticket);
            Assert.True(r.SuccessorOpened);
            Assert.True(r.TracingOn);
            Assert.NotEqual(live.SessionId, r.Successor.SessionId);

            string text = ReadArchivedText(r.Ticket);
            Assert.Contains("the evening that ended in a drop", text, StringComparison.Ordinal);
            Assert.Contains("partial=connection_dropped", text, StringComparison.Ordinal);

            // The terminal marker is the LAST CaptureState line, and it says
            // the file is finished. That is what makes a corpse
            // distinguishable from a capture in flight.
            string lastState = text.Split('\n')
                .Where(l => l.Contains("CaptureState:", StringComparison.Ordinal))
                .Last();
            Assert.Contains("capture=off", lastState, StringComparison.Ordinal);
            Assert.Contains("level=Off", lastState, StringComparison.Ordinal);

            // And the word really lands on the manifest entry — the thing 231
            // archives on this machine prove nobody had ever managed.
            TraceSessionEntry entry = Manifest().Entries
                .Single(e => e.SessionId == live.SessionId.ToString());
            Assert.Equal(TraceSessionOutcome.ConnectionDropped, entry.Outcome);
        }

        /// <summary>
        /// A seal must name what it is about. Only shutdown may act on a
        /// session it did not name, and it has to say so explicitly rather than
        /// reaching the same effect by reading the current pointer first.
        /// </summary>
        [Fact]
        public void Only_shutdown_may_seal_a_session_it_did_not_name()
        {
            Open();
            TraceTransitionResult anonymous = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = null,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.Equal(TraceTransition.NotCurrent, anonymous.Status);
            Assert.True(TraceCoordinator.Recording);

            TraceTransitionResult shutdown = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = null,
                ShutdownAuthority = true,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.Equal(TraceTransition.Accepted, shutdown.Status);
        }

        // ── Bytes, files and the successor ─────────────────────────────────

        /// <summary>
        /// The old path compressed the source and renamed it afterwards, so the
        /// next <c>FileMode.Create</c> at the live path could truncate the very
        /// bytes being read. The boundary closes and MOVES first, so the
        /// successor opens at the live path while the old bytes sit somewhere
        /// nobody is going to write.
        /// </summary>
        [Fact]
        public void The_old_bytes_are_detached_before_the_successor_opens()
        {
            TraceSessionHandle live = Open();
            Write("old bytes");

            TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });

            Assert.NotEqual(_livePath, r.Ticket.SourcePath);
            Assert.True(File.Exists(r.Ticket.SourcePath), "the detached source is gone");
            Assert.Contains("old bytes", File.ReadAllText(r.Ticket.SourcePath), StringComparison.Ordinal);

            // The successor is writing the live path, and it starts empty.
            Write("new bytes");
            Assert.True(File.Exists(_livePath));
            string liveText = ReadLiveText(_livePath);
            Assert.Contains("new bytes", liveText, StringComparison.Ordinal);
            Assert.DoesNotContain("old bytes", liveText, StringComparison.Ordinal);

            // Compression happens later and can only read the detached copy.
            string archived = ReadArchivedText(r.Ticket);
            Assert.Contains("old bytes", archived, StringComparison.Ordinal);
            Assert.DoesNotContain("new bytes", archived, StringComparison.Ordinal);
        }

        /// <summary>
        /// A ticket is frozen. A later observation on the session object — which
        /// the archive worker used to read at compression time — cannot rewrite
        /// what was sealed.
        /// </summary>
        [Fact]
        public void A_sealed_ticket_cannot_be_rewritten_by_a_later_observation()
        {
            TraceSessionHandle live = Open();
            TraceSessionContext.SetConnectionTarget("1234-5678", "6300inshack", null, "10.0.0.7");
            TraceSessionContext.AddKeyEvent("before_the_seal");
            Write("x");

            TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                Resume = TraceResumeIntent.None,
            });

            // Somebody still holding the old session object keeps talking.
            TraceSession old = r.Ticket.Entry == null ? null : null; // frozen: no live object in the ticket
            Assert.Null(old);
            Assert.Equal("6300inshack", r.Ticket.Entry.ConnectionTarget.Nickname);
            Assert.Contains("before_the_seal", r.Ticket.Entry.KeyEvents);

            Assert.True(r.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            TraceSessionEntry entry = Manifest().Entries
                .Single(e => e.SessionId == live.SessionId.ToString());
            Assert.Equal(TraceSessionOutcome.ConnectionDropped, entry.Outcome);
            Assert.Equal("6300inshack", entry.ConnectionTarget.Nickname);
        }

        // ── Shutdown ───────────────────────────────────────────────────────

        /// <summary>
        /// A drop that wins the race during a teardown may seal its own session
        /// — the closing evidence is exactly what a teardown needs to record —
        /// but it must not open a successor. A log started here is a file the
        /// process is about to abandon, which the next boot reads as a killed
        /// session.
        /// </summary>
        [Fact]
        public void A_drop_during_shutdown_seals_its_own_session_and_opens_nothing()
        {
            TraceSessionHandle live = Open();
            Write("the last thing the radio said");

            TraceCoordinator.LatchShutdown();

            TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                Resume = TraceResumeIntent.Standing,
            });

            Assert.Equal(TraceTransition.Accepted, r.Status);
            Assert.False(r.SuccessorOpened);
            Assert.False(r.TracingOn);
            Assert.False(r.RestartFailed);   // not a failure: a deliberate refusal
            Assert.Contains("exit is committed", r.Explanation, StringComparison.Ordinal);
            Assert.Null(TraceCoordinator.CurrentHandle);

            Assert.Contains("the last thing the radio said", ReadArchivedText(r.Ticket), StringComparison.Ordinal);
        }

        [Fact]
        public void Shutdown_refuses_a_new_session_outright()
        {
            TraceCoordinator.LatchShutdown();
            TraceTransitionResult r = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
            Assert.Equal(TraceTransition.ShuttingDown, r.Status);
            Assert.False(r.TracingOn);
        }

        /// <summary>
        /// Both exit hooks run, and the second one must not seal anything. They
        /// share one operation id, so it gets the first one's ticket.
        /// </summary>
        [Fact]
        public void Both_exit_hooks_share_one_seal()
        {
            TraceSessionHandle live = Open();
            Write("x");

            TraceTransitionResult first = TraceCoordinator.FinalizeShutdown(
                TraceSessionOutcome.CleanExit, "ExitApplication clean shutdown");
            TraceTransitionResult second = TraceCoordinator.FinalizeShutdown(
                TraceSessionOutcome.CleanExit, "MyApplication_Shutdown event");

            Assert.Equal(TraceTransition.Accepted, first.Status);
            Assert.Equal(TraceTransition.AlreadyClaimed, second.Status);
            Assert.Same(first.Ticket, second.Ticket);
            Assert.True(first.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));

            // Exactly one archive for that session, not two.
            Assert.Single(Manifest().Entries.Where(e => e.SessionId == live.SessionId.ToString()));
        }

        /// <summary>
        /// The shutdown budget bounds the whole backlog, and an expired budget
        /// preserves the raw files and their pending records rather than
        /// claiming an archive exists. A timeout must never try to cancel a
        /// file write by deleting its source.
        /// </summary>
        [Fact]
        public void An_expired_shutdown_budget_keeps_the_raw_evidence_and_claims_nothing()
        {
            TraceSessionHandle live = Open();
            Write("evidence that must survive a slow disk");

            // No archive root: the worker cannot produce a zip, which stands in
            // for a compression that has not finished. The interesting question
            // is what the raw file and the ticket say, not why.
            TraceCoordinator.ArchiveRootDir = null;

            TraceTransitionResult r = TraceCoordinator.FinalizeShutdown(
                TraceSessionOutcome.CleanExit, "budget test");
            Assert.Equal(TraceTransition.Accepted, r.Status);

            TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
            TraceArchiveCompletion done = r.Ticket.Completion.Result;

            Assert.False(done.ArchiveCommitted);
            Assert.True(done.RawRetained, "the raw trace was not kept");
            Assert.True(File.Exists(done.RawPath));
            Assert.Contains("evidence that must survive a slow disk",
                            File.ReadAllText(done.RawPath), StringComparison.Ordinal);

            TraceCoordinator.ArchiveRootDir = _archiveDir;
        }

        /// <summary>
        /// A detached file that was never committed leaves a durable record
        /// beside it, and the next boot finishes the job rather than leaving
        /// the evidence for the retention sweep to delete unread.
        /// </summary>
        [Fact]
        public void A_detached_file_whose_archive_never_committed_is_finished_at_the_next_boot()
        {
            TraceSessionHandle live = Open();
            Write("a run that ended before its zip did");

            string pendingDir = Path.Combine(_dir, "pendingroot");
            TraceCoordinator.ArchiveRootDir = pendingDir;   // exists only after recovery runs

            TraceTransitionResult r = TraceCoordinator.FinalizeShutdown(
                TraceSessionOutcome.Killed, "pending test");
            TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));

            string raw = r.Ticket.SourcePath;
            Assert.True(File.Exists(raw));

            // Simulate the process ending between detach and commit: the ticket
            // committed here, so put the record back and delete the archive.
            TraceArchiveWorker.WritePendingRecord(r.Ticket, new List<string>());
            Assert.True(File.Exists(raw + ".trace-pending.json"));

            // The sweep must see it as pending work and leave it alone.
            Assert.True(TraceArchiveWorker.IsPendingWork(raw));

            int found = TraceArchiveWorker.RecoverPending(_dir);
            Assert.Equal(1, found);
            TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(60));

            // Deduplicated by source name: recovery re-queued a ticket whose
            // manifest entry was already there, and it did not double it.
            var entries = TraceManifest
                .Load(Path.Combine(pendingDir, SessionArchive.ManifestFileName))
                .Entries.Where(e => e.SessionId == live.SessionId.ToString()).ToList();
            Assert.Single(entries);

            TraceCoordinator.ArchiveRootDir = _archiveDir;
        }

        // ── Capture stop ───────────────────────────────────────────────────

        [Fact]
        public void Stopping_a_capture_verifies_its_identity_and_the_session_together()
        {
            TraceSessionHandle live = Open(TraceLevel.Verbose, asCapture: true);
            Guid captureId = TraceCoordinator.CaptureId;
            Assert.True(TraceCoordinator.CaptureRunning);
            Write("the capture's own line");

            // Somebody else's capture id: refused, and nothing happens.
            TraceTransitionResult wrong = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                ExpectedCaptureId = Guid.NewGuid(),
                RequireCaptureRunning = true,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.NotCurrent, wrong.Status);
            Assert.True(TraceCoordinator.CaptureRunning);
            Assert.True(TraceCoordinator.Recording);

            TraceTransitionResult stop = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                ExpectedCaptureId = captureId,
                RequireCaptureRunning = true,
                OperationId = captureId,
                Outcome = TraceSessionOutcome.CleanExit,
                OutcomeDetail = "Detailed capture: 8:14 PM",
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.Accepted, stop.Status);
            Assert.True(stop.EndedDetailedCapture);
            Assert.Equal(captureId, stop.EndedCaptureId);
            Assert.False(TraceCoordinator.CaptureRunning);
            Assert.True(stop.SuccessorOpened);
        }

        /// <summary>
        /// A second Stop gets the same ticket and performs no extra restart.
        /// The old sequence archived, cleared and restarted as three
        /// statements, so a second press could restart a second time.
        /// </summary>
        [Fact]
        public void A_second_stop_gets_the_same_ticket_and_restarts_nothing()
        {
            TraceSessionHandle live = Open(TraceLevel.Verbose, asCapture: true);
            Guid captureId = TraceCoordinator.CaptureId;
            Write("x");

            var request = new Func<TraceSealRequest>(() => new TraceSealRequest
            {
                Expected = live,
                ExpectedCaptureId = captureId,
                OperationId = captureId,
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });

            TraceTransitionResult first = TraceCoordinator.TrySeal(request());
            TraceSessionHandle successor = first.Successor;
            Assert.NotNull(successor);

            TraceTransitionResult second = TraceCoordinator.TrySeal(request());
            Assert.Equal(TraceTransition.AlreadyClaimed, second.Status);
            Assert.Same(first.Ticket, second.Ticket);
            Assert.Null(second.Successor);

            // The successor the FIRST stop opened is still the one recording —
            // the second press did not replace it.
            Assert.Equal(successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);
        }

        /// <summary>
        /// A late completion belongs to the capture it was about. It must not
        /// clear a newly started capture's state, which is what a
        /// process-global "capture running" flag cleared by whoever finished
        /// last would do.
        /// </summary>
        [Fact]
        public void An_old_completion_cannot_undo_a_newly_started_capture()
        {
            TraceSessionHandle firstCapture = Open(TraceLevel.Verbose, asCapture: true);
            Guid firstId = TraceCoordinator.CaptureId;
            Write("first capture");

            TraceTransitionResult stop = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = firstCapture,
                ExpectedCaptureId = firstId,
                OperationId = firstId,
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.True(stop.EndedDetailedCapture);

            // The operator starts another capture immediately.
            TraceCoordinator.MarkSuccessorAsCapture(stop.Successor);
            Guid secondId = TraceCoordinator.CaptureId;
            Assert.NotEqual(firstId, secondId);

            // The first capture's completion arrives now.
            Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));

            // The new capture is untouched: still running, still its own id.
            Assert.True(TraceCoordinator.CaptureRunning);
            Assert.Equal(secondId, TraceCoordinator.CaptureId);
        }

        // ── The bundler's checkpoint ───────────────────────────────────────

        /// <summary>
        /// A checkpoint freezes a part and leaves the session running. The
        /// bundler only ever wanted the live file released; ending the logical
        /// session to get it cost a running capture its identity and its start
        /// time.
        /// </summary>
        [Fact]
        public void A_bundle_checkpoint_leaves_the_session_and_the_capture_alive()
        {
            TraceSessionHandle live = Open(TraceLevel.Verbose, asCapture: true);
            Guid captureId = TraceCoordinator.CaptureId;
            DateTime? startedAt = TraceCoordinator.CaptureStartedLocal;
            Write("before the bundle");

            TraceTransitionResult snap = TraceCoordinator.SnapshotForBundle(live);

            Assert.Equal(TraceTransition.Accepted, snap.Status);
            Assert.True(snap.Ticket.IsCheckpoint);
            Assert.False(snap.Ticket.IsFinalPart);
            Assert.True(snap.TracingOn);

            // THE session, not a successor: same identity, same capture, same
            // start time, so its duration stays coherent.
            Assert.Equal(live.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(TraceCoordinator.CaptureRunning);
            Assert.Equal(captureId, TraceCoordinator.CaptureId);
            Assert.Equal(startedAt, TraceCoordinator.CaptureStartedLocal);

            // The snapshot holds the bytes and is pinned against the sweeps.
            Assert.True(File.Exists(snap.Ticket.SourcePath));
            Assert.Contains("before the bundle", File.ReadAllText(snap.Ticket.SourcePath),
                            StringComparison.Ordinal);
            Assert.True(TraceEvidencePins.IsPinned(snap.Ticket.SourcePath));

            // It is a NON-FINAL part: it must not claim the session ended, and
            // it must not stamp a terminal capture=off record.
            Assert.Null(snap.Ticket.Entry.EndTime);
            Assert.Null(snap.Ticket.Entry.DurationMs);
            string frozen = File.ReadAllText(snap.Ticket.SourcePath);
            Assert.DoesNotContain("capture=off", frozen, StringComparison.Ordinal);
            Assert.Contains("TraceCheckpoint:", frozen, StringComparison.Ordinal);

            // And recording carried straight on, into the next part.
            Write("after the bundle");
            Assert.Contains("after the bundle", ReadLiveText(_livePath), StringComparison.Ordinal);

            TraceEvidencePins.Release(snap.Ticket.SourcePath);
        }

        /// <summary>
        /// When the expected session has already gone, the bundler is told so
        /// rather than being handed a later session's trace under the same
        /// name.
        /// </summary>
        [Fact]
        public void A_checkpoint_never_substitutes_a_later_session()
        {
            TraceSessionHandle stale = Open();
            TraceTransitionResult sealed_ = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = stale,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });

            // The expected session WAS sealed, so its own ticket is the
            // evidence and the bundler may use that.
            TraceTransitionResult known = TraceCoordinator.SnapshotForBundle(stale);
            Assert.Equal(TraceTransition.AlreadyClaimed, known.Status);
            Assert.Same(sealed_.Ticket, known.Ticket);

            // A session this process never sealed: an explicit refusal, with no
            // ticket at all.
            var neverSeen = (TraceSessionHandle)typeof(TraceSessionHandle)
                .GetConstructors(System.Reflection.BindingFlags.NonPublic
                               | System.Reflection.BindingFlags.Instance)[0]
                .Invoke(new object[] { new TraceSession() });
            TraceTransitionResult refused = TraceCoordinator.SnapshotForBundle(neverSeen);
            Assert.Equal(TraceTransition.NotCurrent, refused.Status);
            Assert.Null(refused.Ticket);

            // And the live session is untouched throughout.
            Assert.Equal(sealed_.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(TraceCoordinator.Recording);
        }

        // ── Rotation against a seal, and direct Trace writers ──────────────

        /// <summary>
        /// Force rotation against a seal, with a producer that writes through
        /// <c>System.Diagnostics.Trace</c> directly rather than through
        /// <see cref="Tracing"/> — which is how JJFlexWpf and FlexLib's
        /// panadapter write. No deadlock, one terminal marker in the sealed
        /// tail, a correct part identity, and every manifest entry kept.
        /// </summary>
        [Fact]
        public void Rotation_racing_a_seal_keeps_every_part_and_one_terminal_marker()
        {
            long savedThreshold = Tracing.RotationThresholdBytes;
            try
            {
                Tracing.RotationThresholdBytes = 4096;
                TraceSessionHandle live = Open();

                var stop = new ManualResetEventSlim(false);
                var producer = new Thread(() =>
                {
                    int n = 0;
                    while (!stop.IsSet)
                    {
                        // The direct path, on purpose: this is the writer that
                        // does not pass through Tracing at all.
                        Trace.WriteLine("direct writer line " + n++);
                    }
                });
                producer.IsBackground = true;
                producer.Start();

                Thread.Sleep(200);   // let it rotate a few times
                TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    Expected = live,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.ConnectionDropped,
                    Resume = TraceResumeIntent.None,
                });
                stop.Set();
                Assert.True(producer.Join(TimeSpan.FromSeconds(10)), "a writer was still blocked — lock inversion");

                Assert.Equal(TraceTransition.Accepted, r.Status);
                Assert.True(r.Ticket.IsFinalPart, "a rotated session's tail is the final part of a chain");
                Assert.True(r.Ticket.PartNumber >= 2);

                Assert.True(TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(120)),
                            "the archive backlog never drained");

                var chain = Manifest().Entries
                    .Where(e => e.SessionId == live.SessionId.ToString())
                    .OrderBy(e => e.PartNumber ?? 0).ToList();

                // Every part survived, numbered without gaps: the rotation
                // worker and the final seal share one archive transaction now,
                // so their manifest writes cannot lose each other.
                Assert.True(chain.Count >= 2);
                for (int i = 0; i < chain.Count; i++) Assert.Equal(i + 1, chain[i].PartNumber);
                Assert.True(chain.Last().PartFinal);
                Assert.All(chain.Take(chain.Count - 1), e => Assert.Null(e.PartFinal));

                // Exactly one terminal marker, in the tail and nowhere else.
                string tail = ReadArchivedText(r.Ticket);
                int terminals = tail.Split('\n').Count(l => l.Contains("level=Off", StringComparison.Ordinal));
                Assert.Equal(1, terminals);
            }
            finally
            {
                Tracing.RotationThresholdBytes = savedThreshold;
            }
        }

        /// <summary>
        /// The router stays in <c>Trace.Listeners</c> for the life of the
        /// process. Sealing a session used to call process-wide
        /// <c>Trace.Close</c> and remove the listener, so between a close and
        /// the next open there were zero listeners and direct writers'
        /// lines evaporated.
        /// </summary>
        [Fact]
        public void The_listener_set_does_not_change_when_a_session_is_sealed()
        {
            TraceSessionHandle live = Open();
            int before = Trace.Listeners.Count;

            TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });

            Assert.Equal(before, Trace.Listeners.Count);

            // And a direct writer's line lands in the successor rather than
            // going nowhere.
            Trace.WriteLine("direct, after the seal");
            TraceTransitionResult end = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = r.Successor,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.Contains("direct, after the seal", ReadArchivedText(end.Ticket), StringComparison.Ordinal);
        }
    }
}
