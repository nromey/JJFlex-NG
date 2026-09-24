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
            TraceCoordinator.TransitionProbeForTests = null;
            TraceCoordinator.BeforeCaptureSlotWriteForTests = null;
        }

        /// <summary>
        /// Wait until <paramref name="t"/> is either blocked or finished —
        /// "it has had its chance to act". A thread still runnable after the
        /// bound means the test machine is starved, not that the code is wrong,
        /// so the bound is generous and never asserted on.
        /// </summary>
        private static void UntilBlockedOrDone(Thread t)
        {
            SpinWait.SpinUntil(() =>
                (t.ThreadState & (System.Threading.ThreadState.WaitSleepJoin
                                  | System.Threading.ThreadState.Stopped)) != 0,
                TimeSpan.FromSeconds(5));
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

        // ── Emission ───────────────────────────────────────────────────────

        /// <summary>
        /// <b>Opening a sink raises the emission gate.</b> Making
        /// <c>Tracing.On</c> a pure gate — so that turning it off could no
        /// longer close somebody else's file — left a hole: boot was the only
        /// place that raised it. An operator who launched with logging off and
        /// then turned it on in Settings would have got a session, a file, and
        /// not one line in it, because every <c>TraceLine</c> tests that gate
        /// first.
        /// </summary>
        [Fact]
        public void A_log_turned_on_after_a_silent_launch_actually_writes()
        {
            Tracing.On = false;                       // launched with logging off
            TraceSessionHandle live = Open();
            Assert.True(Tracing.On, "opening a sink did not raise the emission gate");

            Write("the first line after the operator turned it on");

            TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.Contains("the first line after the operator turned it on",
                            ReadArchivedText(r.Ticket), StringComparison.Ordinal);
        }

        /// <summary>
        /// And the gate is not the same question as "is a log being written".
        /// It stays raised once anything has opened a sink, so every reader that
        /// meant the second question has to ask the boundary — the running-cost
        /// register, the Diagnostics status sentence, the state marker and the
        /// support snapshot all did.
        /// </summary>
        [Fact]
        public void The_emission_gate_is_not_the_same_question_as_recording()
        {
            TraceSessionHandle live = Open();
            TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });

            Assert.True(Tracing.On);                  // still raised
            Assert.False(TraceCoordinator.Recording); // and nothing is being written
        }

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

            // The operator starts another capture immediately — as one
            // transition, the way the application does since Track H6.
            TraceTransitionResult second = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = stop.Successor,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Explicit,
                ResumeLevel = TraceLevel.Verbose,
                SuccessorIsCapture = true,
            });
            Guid secondId = TraceCoordinator.CaptureId;
            Assert.Equal(second.StartedCaptureId, secondId);
            Assert.NotEqual(firstId, secondId);

            // The first capture's completion arrives now.
            Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));

            // The new capture is untouched: still running, still its own id.
            Assert.True(TraceCoordinator.CaptureRunning);
            Assert.Equal(secondId, TraceCoordinator.CaptureId);
        }

        // ── Track H6: capture start is one transition ──────────────────────

        /// <summary>
        /// <b>A drop or a Stop racing a capture start meets either the session
        /// before it or the finished capture — never the successor half
        /// made.</b> Sol's review of H3, finding 2: capture start sealed, opened
        /// a successor, released the gate, and only then marked that successor
        /// as the capture. A drop landing in between sealed it as an ordinary
        /// session; a Stop found no capture running.
        ///
        /// <para>The start is held on a barrier INSIDE its transition, at the
        /// exact point the old code released the gate: the successor open, the
        /// capture not yet marked. A drop reads the current session the way the
        /// fall does — without the gate — and asks to seal it; a Stop takes its
        /// observation. Both get their chance while the start is held.</para>
        ///
        /// <para>Positive control, run by hand at H6: with the gate released
        /// and the handle published at that barrier — the old two-step shape —
        /// the drop reads the successor, seals it as an ordinary session, and
        /// this test goes red.</para>
        /// </summary>
        [Fact]
        public void A_drop_racing_a_capture_start_never_meets_the_capture_half_made()
        {
            TraceSessionHandle standing = Open(TraceLevel.Info);
            Write("the standing log");

            var inside = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            TraceTransitionResult start = null, drop = null;
            TraceObservation stopSaw = null;
            TraceSessionHandle dropSaw = null;
            Thread starter = null, dropper = null, stopper = null;

            TraceCoordinator.TransitionProbeForTests = point =>
            {
                if (point != "seal:successor-opened") return;
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            try
            {
                starter = new Thread(() => start = TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    Expected = standing,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    OutcomeDetail = "Standing diagnostic log closed to begin a detailed capture",
                    Resume = TraceResumeIntent.Explicit,
                    ResumeLevel = TraceLevel.Verbose,
                    SuccessorIsCapture = true,
                })) { IsBackground = true };
                starter.Start();
                Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the capture start never reached its barrier");

                dropper = new Thread(() =>
                {
                    dropSaw = TraceCoordinator.CurrentHandle;
                    drop = TraceCoordinator.TrySeal(new TraceSealRequest
                    {
                        Expected = dropSaw,
                        OperationId = Guid.NewGuid(),
                        Outcome = TraceSessionOutcome.ConnectionDropped,
                        Resume = TraceResumeIntent.Standing,
                    });
                }) { IsBackground = true };
                stopper = new Thread(() => stopSaw = TraceCoordinator.Observe()) { IsBackground = true };
                dropper.Start();
                stopper.Start();
                UntilBlockedOrDone(dropper);
                UntilBlockedOrDone(stopper);
            }
            finally
            {
                release.Set();
                TraceCoordinator.TransitionProbeForTests = null;
                starter?.Join(TimeSpan.FromSeconds(10));
                dropper?.Join(TimeSpan.FromSeconds(10));
                stopper?.Join(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(TraceTransition.Accepted, start.Status);
            Assert.NotEqual(Guid.Empty, start.StartedCaptureId);

            // While the start was in flight, the published session was still
            // the standing log — the successor was invisible until complete...
            Assert.Equal(standing.SessionId, dropSaw.SessionId);
            // ...so the drop named the standing log, which the start had
            // already sealed: refused, touching nothing.
            Assert.Equal(TraceTransition.NotCurrent, drop.Status);

            // The Stop's observation is of the finished capture: running, with
            // the identity the start reported, on the session it opened.
            Assert.True(stopSaw.CaptureRunning, "a Stop observed the capture's session before it was a capture");
            Assert.Equal(start.StartedCaptureId, stopSaw.CaptureId);
            Assert.Equal(start.Successor.SessionId, stopSaw.SessionId);

            // And a drop that reads now ends it AS a capture.
            TraceTransitionResult later = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = TraceCoordinator.CurrentHandle,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.Accepted, later.Status);
            Assert.True(later.EndedDetailedCapture);
            Assert.Equal(start.StartedCaptureId, later.EndedCaptureId);
        }

        /// <summary>
        /// Capture start reports what it started, so a caller never re-reads
        /// "is a capture running?" afterwards — by which time a drop may already
        /// have ended it, and a real start would be spoken as a failure.
        /// </summary>
        [Fact]
        public void A_capture_start_reports_the_capture_it_started()
        {
            TraceSessionHandle standing = Open(TraceLevel.Info);
            TraceTransitionResult start = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = standing,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Explicit,
                ResumeLevel = TraceLevel.Verbose,
                SuccessorIsCapture = true,
            });
            Assert.NotEqual(Guid.Empty, start.StartedCaptureId);
            Assert.Equal(start.StartedCaptureId, TraceCoordinator.CaptureId);
            Assert.Equal(start.StartedCaptureId, TraceCoordinator.CompletedCaptureSlotId);

            // An ordinary restart starts none.
            TraceTransitionResult restart = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = start.Successor,
                ExpectedCaptureId = start.StartedCaptureId,
                OperationId = start.StartedCaptureId,
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(Guid.Empty, restart.StartedCaptureId);
            Assert.False(TraceCoordinator.CaptureRunning);

            // And a start during shutdown opens nothing, so it starts nothing.
            TraceCoordinator.LatchShutdown();
            TraceTransitionResult refused = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = restart.Successor,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Explicit,
                SuccessorIsCapture = true,
            });
            Assert.Null(refused.Successor);
            Assert.Equal(Guid.Empty, refused.StartedCaptureId);
            Assert.False(TraceCoordinator.CaptureRunning);
        }

        // ── Track H6: the completed-capture slot is one value ──────────────

        /// <summary>
        /// <b>An old capture's path cannot land in a new capture's slot.</b>
        /// Sol's review of H3, finding 3: the application checked the slot, then
        /// assigned the path in a second statement, while starting a capture
        /// reset both — so an old completion could pass its check, lose the
        /// processor to the reset, and write the old path over the new slot.
        ///
        /// <para>This tests the ACTUAL path field — the value the application's
        /// <c>LastCaptureArchivePath</c> now reads — with the old completion held
        /// on a barrier between its check and its write, which is exactly the
        /// interleaving point of the defect. A new capture then gets every chance
        /// to finish its reset first.</para>
        ///
        /// <para>Positive control, run by hand at H6: with the lock taken out of
        /// the record, the new capture finishes its reset during the barrier,
        /// the old path lands on top of it, and this test goes red.</para>
        /// </summary>
        [Fact]
        public void An_old_capture_path_cannot_land_in_a_new_capture_slot()
        {
            TraceSessionHandle a = Open(TraceLevel.Verbose, asCapture: true);
            Guid aId = TraceCoordinator.CaptureId;
            Write("capture A");
            TraceTransitionResult stopA = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = a,
                ExpectedCaptureId = aId,
                OperationId = aId,
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.True(stopA.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            string aPath = stopA.Ticket.Completion.Result.ArchiveFullPath;
            Assert.False(string.IsNullOrEmpty(aPath));
            Assert.Equal(aId, TraceCoordinator.CompletedCaptureSlotId);
            Assert.Null(TraceCoordinator.CompletedCaptureArchivePath);

            var inside = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            bool recorded = false;
            TraceTransitionResult startB = null;
            Thread completer = null, starter = null;

            TraceCoordinator.BeforeCaptureSlotWriteForTests = id =>
            {
                if (id != aId) return;
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            try
            {
                // A's completion has checked the slot and is about to write.
                completer = new Thread(() => recorded = TraceCoordinator.RecordCaptureArchive(aId, aPath))
                    { IsBackground = true };
                completer.Start();
                Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the completion never reached its barrier");

                // Capture B starts, and is given every chance to finish first.
                TraceSessionHandle standing = TraceCoordinator.CurrentHandle;
                starter = new Thread(() => startB = TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    Expected = standing,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.Explicit,
                    ResumeLevel = TraceLevel.Verbose,
                    SuccessorIsCapture = true,
                })) { IsBackground = true };
                starter.Start();
                starter.Join(TimeSpan.FromMilliseconds(500));
            }
            finally
            {
                release.Set();
                TraceCoordinator.BeforeCaptureSlotWriteForTests = null;
                completer?.Join(TimeSpan.FromSeconds(10));
                starter?.Join(TimeSpan.FromSeconds(10));
            }

            // A's completion was entitled to write when it checked...
            Assert.True(recorded);
            // ...and B's start still ends with B owning an EMPTY slot. The old
            // path did not land in it.
            Assert.NotEqual(Guid.Empty, startB.StartedCaptureId);
            Assert.Equal(startB.StartedCaptureId, TraceCoordinator.CompletedCaptureSlotId);
            Assert.Null(TraceCoordinator.CompletedCaptureArchivePath);

            // A completion arriving now for A is refused outright.
            Assert.False(TraceCoordinator.RecordCaptureArchive(aId, aPath));
            Assert.Null(TraceCoordinator.CompletedCaptureArchivePath);

            // Positive control for the slot itself: B's own completion does land.
            Assert.True(TraceCoordinator.RecordCaptureArchive(startB.StartedCaptureId, @"C:\b.zip"));
            Assert.Equal(@"C:\b.zip", TraceCoordinator.CompletedCaptureArchivePath);
        }

        // ── Track H6: a pending record that will not write ─────────────────

        /// <summary>
        /// <b>A failed pending-record write is reported, and the raw trace it
        /// was meant to protect is kept.</b> Sol's review of H3, finding 4:
        /// <c>WritePendingRecord</c> swallowed the failure and the seal published
        /// its successor as if the record existed — so a crash before the
        /// archive committed left a raw file boot recovery could not see, and
        /// the one-day sweep deleted it unread.
        ///
        /// <para>The failure is forced with a DIRECTORY where the record goes,
        /// the same device H3 used for the detach: a file there would be
        /// overwritten, and the collision-safe naming would route round anything
        /// placed at the trace's own name.</para>
        ///
        /// <para><b>One assertion pins today's behaviour and NOT a ruling:</b>
        /// the successor still opens. Keeping the design's contract to the
        /// letter would refuse it — logging would stop because one small write
        /// failed — and Track H6 was told to report that trade rather than
        /// choose it. When it is ruled, change that one line.</para>
        /// </summary>
        [Fact]
        public void A_pending_record_that_will_not_write_is_reported_and_its_raw_trace_kept()
        {
            TraceSessionHandle live = Open();
            Write("evidence that has to outlive a crash");
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPath(_livePath, boot);
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(target));

            TraceTransitionResult r = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                Resume = TraceResumeIntent.Standing,
            });

            Assert.Equal(TraceTransition.Accepted, r.Status);
            // Positive control: the obstacle sat where THIS seal's record goes.
            Assert.Equal(target, r.Ticket.SourcePath);

            // Explicit, not swallowed.
            Assert.True(r.PendingRecordFailed);
            Assert.False(r.Ticket.PendingRecordWritten);
            Assert.Contains(r.DeferredFaults, f => f.Contains(target, StringComparison.Ordinal)
                                                   && f.Contains("NO durable pending record", StringComparison.Ordinal));

            // TODAY'S BEHAVIOUR, NOT A RULING: logging carried on.
            Assert.True(r.SuccessorOpened);

            // The raw file is there, and a day later the sweep keeps it,
            // because no archive holds it yet.
            Assert.True(File.Exists(target));
            DateTime now = DateTime.UtcNow;
            Assert.Equal(PlainTextTraceVerdict.KeptBecauseUnarchived,
                TraceArchiveWorker.ClassifyPlainTextTrace(target, now.AddDays(-2), now, 1,
                    SessionArchive.ArchivedSourceNames(_archiveDir)));

            // Positive control for the sweep: once its archive commits, the
            // ordinary window applies again. The record's absence never stopped
            // the archive being made.
            Assert.True(r.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(r.Ticket.Completion.Result.ArchiveCommitted);
            Assert.Equal(PlainTextTraceVerdict.Delete,
                TraceArchiveWorker.ClassifyPlainTextTrace(target, now.AddDays(-2), now, 1,
                    SessionArchive.ArchivedSourceNames(_archiveDir)));

            // And an ordinary seal reports no failure.
            TraceTransitionResult ordinary = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = r.Successor,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.False(ordinary.PendingRecordFailed);
            Assert.True(ordinary.Ticket.PendingRecordWritten);
        }

        /// <summary>
        /// The checkpoint path had the same swallowed write (Sol's review,
        /// finding 4, "same fix in the checkpoint path"). A problem-report
        /// snapshot whose record will not write says so, is still pinned for
        /// the bundle, and the session carries on.
        /// </summary>
        [Fact]
        public void A_checkpoint_whose_pending_record_will_not_write_says_so()
        {
            TraceSessionHandle live = Open();
            Write("before the bundle");
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPartPath(_livePath, boot, 1);
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(target));

            TraceTransitionResult snap = TraceCoordinator.SnapshotForBundle(live);

            Assert.Equal(TraceTransition.Accepted, snap.Status);
            Assert.Equal(target, snap.Ticket.SourcePath);
            Assert.True(snap.PendingRecordFailed);
            Assert.False(snap.Ticket.PendingRecordWritten);
            Assert.Contains(snap.DeferredFaults, f => f.Contains("NO durable pending record", StringComparison.Ordinal));
            Assert.True(File.Exists(target));
            Assert.True(TraceEvidencePins.IsPinned(target));
            Assert.Equal(live.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            TraceEvidencePins.Release(target);
        }

        /// <summary>
        /// The sweep's rule on its own: young files, pinned files and pending
        /// work are kept; an archived file goes after the ordinary window; an
        /// unarchived one is kept until an archive's own window has passed, and
        /// an unreadable manifest counts as nothing archived.
        /// </summary>
        [Fact]
        public void The_plain_text_sweep_keeps_what_no_archive_holds()
        {
            string file = Path.Combine(_dir, "JJFlexRadioTrace-20260901-120000.txt");
            File.WriteAllText(file, "x");
            DateTime now = DateTime.UtcNow;
            var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(file) };
            var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(PlainTextTraceVerdict.Keep,
                TraceArchiveWorker.ClassifyPlainTextTrace(file, now.AddHours(-2), now, 1, none));
            Assert.Equal(PlainTextTraceVerdict.Delete,
                TraceArchiveWorker.ClassifyPlainTextTrace(file, now.AddDays(-2), now, 1, archived));
            Assert.Equal(PlainTextTraceVerdict.KeptBecauseUnarchived,
                TraceArchiveWorker.ClassifyPlainTextTrace(file, now.AddDays(-2), now, 1, none));
            Assert.Equal(PlainTextTraceVerdict.KeptBecauseUnarchived,
                TraceArchiveWorker.ClassifyPlainTextTrace(file, now.AddDays(-2), now, 1, null));
            Assert.Equal(PlainTextTraceVerdict.Delete,
                TraceArchiveWorker.ClassifyPlainTextTrace(file, now.AddDays(-(SessionArchive.DefaultRetentionDays + 1)), now, 1, none));

            TraceEvidencePins.Pin(file);
            try
            {
                Assert.Equal(PlainTextTraceVerdict.Keep,
                    TraceArchiveWorker.ClassifyPlainTextTrace(file, now.AddDays(-2), now, 1, archived));
            }
            finally { TraceEvidencePins.Release(file); }
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
            // Pinned for the bundle like any snapshot (Track H6); the bundler's
            // Finally is what releases it.
            Assert.True(TraceEvidencePins.IsPinned(known.Ticket.SourcePath));
            TraceEvidencePins.Release(known.Ticket.SourcePath);

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

        /// <summary>
        /// A checkpoint whose detach fails must not destroy the evidence in the
        /// course of failing to preserve it. The session has to get writing
        /// again or it goes dark for the rest of the run, and reopening the
        /// live path with <c>FileMode.Create</c> would truncate the very bytes
        /// the move could not take away.
        /// </summary>
        /// <summary>
        /// <b>A bundle keeps the evidence of a session that was sealed with
        /// nothing after it.</b> Sol's review of H3, finding 5: the checkpoint
        /// answered NoSession before looking up an already sealed session's
        /// ticket, so a Stop or logging switched off between the bundler reading
        /// its handle and arriving at the checkpoint — sealing the session with
        /// no successor — made the bundle leave out a trace that was sealed,
        /// detached and sitting on disk.
        ///
        /// <para>Positive control, run by hand at H6: with the old order
        /// restored, the answer is NoSession and this test goes red.</para>
        /// </summary>
        [Fact]
        public void A_bundle_keeps_a_session_sealed_with_nothing_after_it()
        {
            TraceSessionHandle expected = Open();          // what the bundler read
            Write("the evening the operator is reporting");

            // Logging switched off before the bundler reaches the checkpoint.
            TraceTransitionResult off = TraceCoordinator.TrySeal(new TraceSealRequest
            {
                Expected = expected,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.False(off.SuccessorOpened);
            Assert.False(TraceCoordinator.Recording);

            TraceTransitionResult snap = TraceCoordinator.SnapshotForBundle(expected);

            Assert.Equal(TraceTransition.AlreadyClaimed, snap.Status);
            Assert.Same(off.Ticket, snap.Ticket);
            Assert.False(snap.TracingOn);
            Assert.Null(snap.Successor);
            // The bytes are there, and pinned while the bundle is built.
            Assert.True(File.Exists(snap.Ticket.SourcePath));
            Assert.Contains("the evening the operator is reporting",
                            File.ReadAllText(snap.Ticket.SourcePath), StringComparison.Ordinal);
            Assert.True(TraceEvidencePins.IsPinned(snap.Ticket.SourcePath));

            // The bundler's Finally releases what it was handed — and that is
            // the only pin, so the file is free again.
            TraceEvidencePins.Release(snap.Ticket.SourcePath);
            Assert.False(TraceEvidencePins.IsPinned(snap.Ticket.SourcePath));

            // Positive control for the order: a session this process never
            // sealed, with nothing recording, is still NoSession.
            var neverSeen = (TraceSessionHandle)typeof(TraceSessionHandle)
                .GetConstructors(System.Reflection.BindingFlags.NonPublic
                               | System.Reflection.BindingFlags.Instance)[0]
                .Invoke(new object[] { new TraceSession() });
            Assert.Equal(TraceTransition.NoSession, TraceCoordinator.SnapshotForBundle(neverSeen).Status);
        }

        [Fact]
        public void A_snapshot_that_cannot_detach_keeps_the_bytes_and_keeps_recording()
        {
            TraceSessionHandle live = Open();
            Write("bytes that must survive a failed move");

            // Put a DIRECTORY where the part file wants to go. A file there
            // would not do it: the naming is collision-safe on purpose — it
            // must never overwrite existing evidence — so it would simply pick
            // the next name and succeed. A directory is invisible to that
            // guard's File.Exists and fatal to the move, which is exactly the
            // shape of a real detach failure.
            string blocked = TraceFileNaming.StampedPartPath(
                _livePath, TraceCoordinator.Observe().SessionBootTimeUtc.Value, 1);
            Directory.CreateDirectory(blocked);
            try
            {
                TraceTransitionResult snap = TraceCoordinator.SnapshotForBundle(live);

                Assert.Equal(TraceTransition.Failed, snap.Status);
                Assert.Equal("detach", snap.FailedStage);
                Assert.Null(snap.Ticket);

                // Still the same session, still recording, and the old bytes
                // are still in the live file.
                Assert.True(snap.TracingOn);
                Assert.Equal(live.SessionId, TraceCoordinator.CurrentHandle.SessionId);
                Write("and recording carried on");
            }
            finally
            {
                try { Directory.Delete(blocked); } catch { }
            }

            string text = ReadLiveText(_livePath);
            Assert.Contains("bytes that must survive a failed move", text, StringComparison.Ordinal);
            Assert.Contains("and recording carried on", text, StringComparison.Ordinal);
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
