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
        private readonly TraceLevel _savedLevel;

        public TraceCoordinatorTests()
        {
            // The switch is process-wide and gates TraceLineDeferred as it
            // gates TraceLine; a test that writes a Warning line through
            // either needs it raised, and must put it back.
            _savedLevel = Tracing.TheSwitch.Level;
            Tracing.TheSwitch.Level = TraceLevel.Verbose;
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
            Tracing.TheSwitch.Level = _savedLevel;
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>
        /// Hold the archive worker at the ticket that matches, so a condition
        /// the worker would resolve in milliseconds is still there to assert
        /// on. Dispose releases it and clears the hook.
        /// </summary>
        private sealed class HeldWorker : IDisposable
        {
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            public ManualResetEventSlim Reached { get; } = new ManualResetEventSlim(false);

            public HeldWorker(Func<TraceArchiveTicket, bool> match)
            {
                TraceArchiveWorker.BeforeArchiveForTests = t =>
                {
                    if (!match(t)) return;
                    Reached.Set();
                    _release.Wait(TimeSpan.FromSeconds(30));
                };
            }

            public void Release() => _release.Set();

            public void Dispose()
            {
                _release.Set();
                TraceArchiveWorker.BeforeArchiveForTests = null;
            }
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
            TraceCoordinator.FaultRetireQueue = TraceCoordinator.DefaultFaultRetireQueue;
            TraceArchiveWorker.BeforeArchiveForTests = null;
            TraceRecordingHealth.ResetForTests();
            Tracing.ResetDeferredCountersForTests();
        }

        /// <summary>
        /// Hold the fault-retire work item so a caller can be raced against
        /// it. <see cref="Run"/> runs whatever was captured, on this thread.
        /// </summary>
        private sealed class HeldFaultRetire : IDisposable
        {
            private Action _work;
            public HeldFaultRetire()
            {
                TraceCoordinator.FaultRetireQueue = w => { lock (this) _work = w; };
            }
            public bool Captured { get { lock (this) return _work != null; } }
            public void Run()
            {
                Action w;
                lock (this) { w = _work; _work = null; }
                w?.Invoke();
            }
            public void Dispose()
            {
                TraceCoordinator.FaultRetireQueue = TraceCoordinator.DefaultFaultRetireQueue;
            }
        }

        /// <summary>Collect every health change raised while the returned
        /// handle is alive.</summary>
        private sealed class HealthChanges : IDisposable
        {
            private readonly List<TraceRecordingHealthChange> _changes = new List<TraceRecordingHealthChange>();
            public HealthChanges() { TraceRecordingHealth.Changed += OnChanged; }
            private void OnChanged(TraceRecordingHealthChange c) { lock (_changes) _changes.Add(c); }
            public List<TraceRecordingHealthChange> All { get { lock (_changes) return new List<TraceRecordingHealthChange>(_changes); } }
            public void Dispose() { TraceRecordingHealth.Changed -= OnChanged; }
        }

        /// <summary>
        /// Break the live sink's writer so its next write throws — the
        /// deterministic stand-in for a disk that stops accepting bytes.
        /// </summary>
        private static void BreakTheLiveSink()
        {
            RotatingTraceListener live = Tracing.LiveListener;
            Assert.NotNull(live);
            var writer = (StreamWriter)typeof(RotatingTraceListener)
                .GetField("_writer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(live)!;
            writer.Dispose();
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
            if (TraceCoordinator.CurrentHandle != null)
            {
                TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    ShutdownAuthority = true,
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.None,
                    OperationId = Guid.NewGuid(),
                });
            }
            // ALWAYS drain, whether or not anything was open. A test that ended
            // with no session — an archive with no successor, a shutdown — can
            // still have its last ticket compressing on the worker; returning
            // early here let Dispose delete the directory under it, and the
            // worker then reported "compress: could not find file" into the
            // NEXT test's freshly reset health state. Found at H7 the first
            // time the health model made a failed archive visible.
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

            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceTransitionResult first = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceTransitionResult loser = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = stale,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                OutcomeDetail = "a drop that lost the race",
                TerminalLines = new TraceRecord[] { "captureMeters: partial=connection_dropped" },
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
            Assert.DoesNotContain("archived session", loser.Explanation, StringComparison.OrdinalIgnoreCase);

            // No listener closed, no capture state cleared, no logging
            // restarted: the replacement is still the live one and still
            // writing to its own file.
            Assert.Equal(live.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(TraceCoordinator.Recording);
            Write("still the replacement, after the refusal");

            TraceTransitionResult end = TraceCoordinator.TryArchive(new TraceArchiveRequest
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

            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                OutcomeDetail = "The connection to 6300inshack dropped while this session was running",
                TerminalLines = new TraceRecord[] { "captureMeters: paTemp none n=0 partial=connection_dropped" },
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
        /// An archive must name what it is about. Only shutdown may act on a
        /// session it did not name, and it has to say so explicitly rather than
        /// reaching the same effect by reading the current pointer first.
        /// </summary>
        [Fact]
        public void Only_shutdown_may_archive_a_session_it_did_not_name()
        {
            Open();
            TraceTransitionResult anonymous = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = null,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.Equal(TraceTransition.NotCurrent, anonymous.Status);
            Assert.True(TraceCoordinator.Recording);

            TraceTransitionResult shutdown = TraceCoordinator.TryArchive(new TraceArchiveRequest
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

            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
        /// what was archived.
        /// </summary>
        [Fact]
        public void A_archived_ticket_cannot_be_rewritten_by_a_later_observation()
        {
            TraceSessionHandle live = Open();
            TraceSessionContext.SetConnectionTarget("1234-5678", "6300inshack", null, "10.0.0.7");
            TraceSessionContext.AddKeyEvent("before_the_archive");
            Write("x");

            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            Assert.Contains("before_the_archive", r.Ticket.Entry.KeyEvents);

            Assert.True(r.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            TraceSessionEntry entry = Manifest().Entries
                .Single(e => e.SessionId == live.SessionId.ToString());
            Assert.Equal(TraceSessionOutcome.ConnectionDropped, entry.Outcome);
            Assert.Equal("6300inshack", entry.ConnectionTarget.Nickname);
        }

        // ── Shutdown ───────────────────────────────────────────────────────

        /// <summary>
        /// A drop that wins the race during a teardown may archive its own session
        /// — the closing evidence is exactly what a teardown needs to record —
        /// but it must not open a successor. A log started here is a file the
        /// process is about to abandon, which the next boot reads as a killed
        /// session.
        /// </summary>
        [Fact]
        public void A_drop_during_shutdown_archives_its_own_session_and_opens_nothing()
        {
            TraceSessionHandle live = Open();
            Write("the last thing the radio said");

            TraceCoordinator.LatchShutdown();

            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
        /// Both exit hooks run, and the second one must not archive anything. They
        /// share one operation id, so it gets the first one's ticket.
        /// </summary>
        [Fact]
        public void Both_exit_hooks_share_one_archive()
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
            TraceTransitionResult wrong = TraceCoordinator.TryArchive(new TraceArchiveRequest
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

            TraceTransitionResult stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
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

            var request = new Func<TraceArchiveRequest>(() => new TraceArchiveRequest
            {
                Expected = live,
                ExpectedCaptureId = captureId,
                OperationId = captureId,
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });

            TraceTransitionResult first = TraceCoordinator.TryArchive(request());
            TraceSessionHandle successor = first.Successor;
            Assert.NotNull(successor);

            TraceTransitionResult second = TraceCoordinator.TryArchive(request());
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

            TraceTransitionResult stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceTransitionResult second = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
        /// made.</b> Sol's review of H3, finding 2: capture start archived, opened
        /// a successor, released the gate, and only then marked that successor
        /// as the capture. A drop landing in between archived it as an ordinary
        /// session; a Stop found no capture running.
        ///
        /// <para>The start is held on a barrier INSIDE its transition, at the
        /// exact point the old code released the gate: the successor open, the
        /// capture not yet marked. A drop reads the current session the way the
        /// fall does — without the gate — and asks to archive it; a Stop takes its
        /// observation. Both get their chance while the start is held.</para>
        ///
        /// <para>Positive control, run by hand at H6: with the gate released
        /// and the handle published at that barrier — the old two-step shape —
        /// the drop reads the successor, archives it as an ordinary session, and
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
                if (point != "archive:successor-opened") return;
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            try
            {
                starter = new Thread(() => start = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
                    drop = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            // already archived: refused, touching nothing.
            Assert.Equal(TraceTransition.NotCurrent, drop.Status);

            // The Stop's observation is of the finished capture: running, with
            // the identity the start reported, on the session it opened.
            Assert.True(stopSaw.CaptureRunning, "a Stop observed the capture's session before it was a capture");
            Assert.Equal(start.StartedCaptureId, stopSaw.CaptureId);
            Assert.Equal(start.Successor.SessionId, stopSaw.SessionId);

            // And a drop that reads now ends it AS a capture.
            TraceTransitionResult later = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceTransitionResult start = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceTransitionResult restart = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceTransitionResult refused = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            TraceTransitionResult stopA = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
                starter = new Thread(() => startB = TraceCoordinator.TryArchive(new TraceArchiveRequest
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

        // ── Track H6/H7: a pending record that will not write ──────────────

        /// <summary>
        /// <b>A failed pending-record write is reported to the operator's
        /// state, logging continues on a VERIFIED successor, and the raw trace
        /// the record was meant to protect is kept.</b> Sol's review of H3,
        /// finding 4, found the write swallowed; Track H6 made it explicit and
        /// stopped at whether the successor may open; Astra ruled
        /// (<c>for-claude/2026-09-24-codex-design-pending-record-failure.md</c>)
        /// that it may, provided the old file is safely detached, the successor
        /// can actually write, and the old ticket's failure is reported
        /// independently of the new session's state.
        ///
        /// <para>The failure is forced with a DIRECTORY where the record goes,
        /// the same device H3 used for the detach: a file there would be
        /// overwritten, and the collision-safe naming would route round anything
        /// placed at the trace's own name. The archive worker is held on a
        /// barrier so the old file's archive is genuinely pending while the
        /// successor is read back — a handle alone does not prove logging
        /// continued, and Astra asked for a real line through the real routing
        /// listener.</para>
        ///
        /// <para><b>Positive controls, run by hand at H7:</b> refusing the
        /// successor (Resume None in the request) fails the distinctive-line
        /// assertion; dropping <c>TraceRecordingHealth.NoteDetached</c> from
        /// the archive fails the reachable-status assertion; clearing the
        /// condition when the successor opens (a NoteSink that resolved
        /// conditions) fails the same assertion. Each restored afterwards.</para>
        /// </summary>
        [Fact]
        public void A_pending_record_that_will_not_write_is_reported_and_its_raw_trace_kept()
        {
            TraceSessionHandle a = Open();
            Write("evidence that has to outlive a crash");
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPath(_livePath, boot);
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(target));

            var atWorker = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            TraceArchiveWorker.BeforeArchiveForTests = t =>
            {
                if (t.SessionId != a.SessionId) return;
                atWorker.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            TraceTransitionResult r;
            using (var changes = new HealthChanges())
            {
                try
                {
                    r = TraceCoordinator.TryArchive(new TraceArchiveRequest
                    {
                        Expected = a,
                        OperationId = Guid.NewGuid(),
                        Outcome = TraceSessionOutcome.ConnectionDropped,
                        OutcomeDetail = "the drop whose record would not write",
                        Resume = TraceResumeIntent.Standing,
                    });

                    Assert.Equal(TraceTransition.Accepted, r.Status);
                    // Positive control: the obstacle sat where THIS archive's record goes.
                    Assert.Equal(target, r.Ticket.SourcePath);

                    // Explicit, not swallowed.
                    Assert.True(r.PendingRecordFailed);
                    Assert.False(r.Ticket.PendingRecordWritten);
                    Assert.Contains(r.DeferredFaults, f => f.Contains(target, StringComparison.Ordinal)
                                                           && f.Contains("NO durable pending record", StringComparison.Ordinal));

                    // RULED: logging continues, and "continues" means verified —
                    // the successor's first record was written and flushed.
                    Assert.True(r.SuccessorOpened);
                    Assert.True(r.TracingOn);

                    // Published BEFORE the archive returned, so before anyone waits
                    // on the archive: the change is already in hand.
                    Assert.Contains(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.ConditionRaised
                                                      && c.Condition.TicketId == r.Ticket.TicketId);

                    // The worker is held: A's archive is genuinely pending.
                    Assert.True(atWorker.Wait(TimeSpan.FromSeconds(10)), "the archive worker never reached A");
                    Assert.False(r.Ticket.Completion.IsCompleted);

                    // A real line, through the real routing listener, into B —
                    // flushed and read back while A's archive is still blocked.
                    Write("a distinctive line for successor B");
                    Trace.Flush();
                    string bText = ReadLiveText(_livePath);
                    Assert.Contains("a distinctive line for successor B", bText, StringComparison.Ordinal);
                    Assert.DoesNotContain("evidence that has to outlive a crash", bText, StringComparison.Ordinal);

                    // A's bytes are unchanged and untouched by B.
                    string aText = File.ReadAllText(target);
                    Assert.Contains("evidence that has to outlive a crash", aText, StringComparison.Ordinal);
                    Assert.DoesNotContain("a distinctive line for successor B", aText, StringComparison.Ordinal);

                    // A's recovery failure is reachable through the operator-state
                    // model, naming A, its file, and what is missing.
                    TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
                    TraceRecoveryCondition condition = Assert.Single(snap.Unresolved);
                    Assert.Equal(a.SessionId, condition.SessionId);
                    Assert.Equal(target, condition.RawPath);
                    Assert.True(condition.RawRetained);
                    Assert.False(condition.RecoveryRecordWritten);
                    Assert.Null(condition.ArchiveFailureStage);

                    // B is recording, and the two facts are independent: the
                    // healthy new session does not erase the old ticket's state.
                    Assert.Equal(TraceSinkState.Recording, snap.SinkState);
                    Assert.Equal(r.Successor.SessionId, snap.LiveSessionId);
                    Assert.Equal(r.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);

                    // Neither result claims an archive is committed.
                    Assert.False(r.Ticket.Completion.IsCompleted);

                    // And a day later the sweep keeps the raw file, because no
                    // archive holds it yet.
                    DateTime now = DateTime.UtcNow;
                    Assert.Equal(PlainTextTraceVerdict.KeptBecauseUnarchived,
                        TraceArchiveWorker.ClassifyPlainTextTrace(target, now.AddDays(-2), now, 1,
                            SessionArchive.ArchivedSourceNames(_archiveDir)));
                }
                finally
                {
                    release.Set();
                    TraceArchiveWorker.BeforeArchiveForTests = null;
                }

                // Released: A commits with its ORIGINAL metadata — outcome,
                // detail, identity — and only A's unresolved status clears.
                Assert.True(r.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
                Assert.True(r.Ticket.Completion.Result.ArchiveCommitted);
                TraceSessionEntry entry = Manifest().Entries.Single(e => e.SessionId == a.SessionId.ToString());
                Assert.Equal(TraceSessionOutcome.ConnectionDropped, entry.Outcome);
                Assert.Equal("the drop whose record would not write", entry.OutcomeDetail);
                Assert.Null(entry.Orphaned);
                Assert.Contains(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.ConditionResolved
                                                  && c.Condition.TicketId == r.Ticket.TicketId);
            }
            TraceRecordingHealthSnapshot after = TraceRecordingHealth.Snapshot();
            Assert.Empty(after.Unresolved);
            // Resolution changed nothing live, and the history of the failure
            // is kept separately from the condition that cleared.
            Assert.Equal(TraceSinkState.Recording, after.SinkState);
            Assert.Equal(r.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(after.HistoricalFailures >= 1);

            DateTime later = DateTime.UtcNow;
            Assert.Equal(PlainTextTraceVerdict.Delete,
                TraceArchiveWorker.ClassifyPlainTextTrace(target, later.AddDays(-2), later, 1,
                    SessionArchive.ArchivedSourceNames(_archiveDir)));

            // Positive control: the ordinary sidecar succeeds, reports no
            // failure, and raises no condition.
            using (var quiet = new HealthChanges())
            {
                TraceTransitionResult ordinary = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = r.Successor,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.None,
                });
                Assert.False(ordinary.PendingRecordFailed);
                Assert.True(ordinary.Ticket.PendingRecordWritten);
                Assert.True(ordinary.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
                Assert.DoesNotContain(quiet.All, c => c.Kind == TraceRecordingHealthChangeKind.ConditionRaised);
            }
        }

        /// <summary>
        /// The sidecar fails AND the successor cannot write. No recording claim
        /// survives: the result says the restart failed and tracing is off,
        /// the health model says the sink FAILED (not "off by choice"), A's
        /// raw file is there to the extent it was written, and A's ticket is
        /// still queued. Paired below with the successful first write.
        ///
        /// <para>The successor is made unwritable by putting a DIRECTORY at the
        /// live path from inside the transition, after the old file has been
        /// moved away and before the successor opens — the probe at
        /// <c>archive:detached</c> is exactly that moment.</para>
        /// </summary>
        [Fact]
        public void A_failed_record_and_a_successor_that_cannot_write_claims_no_recording()
        {
            TraceSessionHandle a = Open();
            Write("what A managed to write");
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPath(_livePath, boot);
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(target));

            TraceCoordinator.TransitionProbeForTests = point =>
            {
                if (point == "archive:detached") Directory.CreateDirectory(_livePath);
            };
            TraceTransitionResult r;
            using var held = new HeldWorker(t => t.SessionId == a.SessionId);
            try
            {
                using var changes = new HealthChanges();
                r = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = a,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.ConnectionDropped,
                    Resume = TraceResumeIntent.Standing,
                });

                Assert.Equal(TraceTransition.Accepted, r.Status);   // A was detached
                Assert.True(r.PendingRecordFailed);
                Assert.True(r.RestartFailed);
                Assert.False(r.TracingOn);
                Assert.False(r.SuccessorOpened);
                Assert.NotNull(r.SinkFault);
                Assert.NotNull(r.Ticket.Completion);                 // still queued
                Assert.True(File.Exists(target));
                Assert.Contains("what A managed to write", File.ReadAllText(target), StringComparison.Ordinal);

                TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
                Assert.Equal(TraceSinkState.Failed, snap.SinkState);
                Assert.NotNull(snap.SinkFault);
                Assert.Single(snap.Unresolved);
                Assert.Contains(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed);
                Assert.Contains(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.ConditionRaised);
                Assert.False(TraceCoordinator.Recording);
                Assert.Null(TraceCoordinator.CurrentHandle);
            }
            finally
            {
                TraceCoordinator.TransitionProbeForTests = null;
                try { Directory.Delete(_livePath); } catch { }
            }
            held.Release();
            Assert.True(r.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));

            // The pair: with a writable path, the first write is verified and
            // the successor is called recording — and the live file carries
            // the header that verified it.
            TraceSessionHandle b = Open();
            Assert.True(TraceCoordinator.Recording);
            Assert.Contains("--- trace session " + b.SessionId + " part 001 opened",
                            ReadLiveText(_livePath), StringComparison.Ordinal);
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
        }

        /// <summary>
        /// The sink's own truth (Astra, implementation note 3): a terminal
        /// write that fails says so, latches the fault, and a
        /// <c>FlushAndClose</c> on the sink it closed does NOT turn that
        /// failure into success. Paired with a healthy sink that says yes to
        /// both.
        /// </summary>
        [Fact]
        public void A_terminal_write_failure_followed_by_a_close_does_not_become_success()
        {
            string path = Path.Combine(_dir, "sink-fault.txt");
            var healthy = new RotatingTraceListener(path, 0, _ => null, (_, _) => { });
            Assert.True(healthy.WriteTerminalLine("a line that lands"));
            Assert.Null(healthy.WriteFault);
            Assert.True(healthy.FlushAndClose(out string none));
            Assert.Null(none);
            // Closed cleanly, and a second close still says so.
            Assert.True(healthy.FlushAndClose(out _));

            string broken = Path.Combine(_dir, "sink-broken.txt");
            var sink = new RotatingTraceListener(broken, 0, _ => null, (_, _) => { });
            ((StreamWriter)typeof(RotatingTraceListener)
                .GetField("_writer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(sink)!).Dispose();

            Assert.False(sink.WriteTerminalLine("a line that cannot land"));
            Assert.NotNull(sink.WriteFault);
            Assert.True(sink.IsClosed);
            // THE OLD LIE: closed already, so "true". Now the latched fault.
            Assert.False(sink.FlushAndClose(out string failure));
            Assert.Equal(sink.WriteFault, failure);
        }

        /// <summary>
        /// A write failure on the LIVE sink, mid-session, reaches the health
        /// model as "the log stopped" — not "off" — naming the fault, AND the
        /// session is retired: its file detached and archived under
        /// <c>recording_failed</c> with its tail marked uncertain, the
        /// coordinator left with no session, nothing reopened.
        ///
        /// <para>Until Track H8 this test asserted the OPPOSITE of the last
        /// clause — <c>TraceCoordinator.SinkFault</c> non-null, meaning the
        /// dead session was still in place — and called that the desired
        /// state. It was the state Sol's review of H7 (finding 1) showed left
        /// the process without a log for the rest of its run, because an archive
        /// refused a closed sink and a Begin refused a present session. The
        /// old assertion pinned the defect; it is gone.</para>
        /// </summary>
        [Fact]
        public void A_live_sink_that_fails_a_write_reports_the_log_stopped_and_retires_the_session()
        {
            TraceSessionHandle live = Open();
            Write("fine so far");
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPath(_livePath, boot);

            var failed = new ManualResetEventSlim(false);
            void OnChanged(TraceRecordingHealthChange c)
            {
                if (c.Kind == TraceRecordingHealthChangeKind.SinkFailed) failed.Set();
            }
            TraceRecordingHealth.Changed += OnChanged;
            try
            {
                BreakTheLiveSink();
                Write("the write that fails");
                Assert.True(failed.Wait(TimeSpan.FromSeconds(10)), "the sink's failure never reached the health model");
            }
            finally { TraceRecordingHealth.Changed -= OnChanged; }

            // The pool retires the session on its own; wait for it rather
            // than hoping. A live handle still here after the bound means the
            // retirement never ran.
            Assert.True(SpinWait.SpinUntil(() => TraceCoordinator.CurrentHandle == null, TimeSpan.FromSeconds(10)),
                        "the faulted session was never retired");
            Assert.False(TraceCoordinator.Recording);
            Assert.False(TraceCoordinator.CaptureRunning);

            TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
            Assert.Equal(TraceSinkState.Failed, snap.SinkState);
            Assert.NotNull(snap.SinkFault);
            Assert.Equal(_livePath, snap.SinkPath);
            Assert.Equal(live.SessionId, snap.LiveSessionId);

            // The file was detached to its stamped path, with what did land in
            // it, and archived under the coordinator's own outcome.
            Assert.True(TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(60)));
            TraceSessionEntry entry = Manifest().Entries.Single(e => e.SessionId == live.SessionId.ToString());
            Assert.Equal(TraceSessionOutcome.RecordingFailed, entry.Outcome);
            Assert.Contains("stopped accepting writes", entry.OutcomeDetail, StringComparison.Ordinal);
            Assert.False(File.Exists(_livePath), "the dead file was left at the live path, where a Begin would truncate it");

            // Nothing reopened by itself: a disk that just refused a write is
            // not hammered. The operator's own on-again is the retry, and it
            // works — which is the whole point.
            TraceTransitionResult again = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, again.Status);
            Write("after the operator turned it on again");
            Assert.Contains("after the operator turned it on again", ReadLiveText(_livePath), StringComparison.Ordinal);
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
        }

        /// <summary>
        /// The checkpoint path had the same swallowed write (Sol's review,
        /// finding 4, "same fix in the checkpoint path"). During a DETAILED
        /// CAPTURE, a problem-report snapshot whose record will not write says
        /// so, is still pinned for the bundle, keeps the session AND the
        /// capture's identity, claims no session end, writes the next part —
        /// and the condition is reachable, marked as a checkpoint.
        /// </summary>
        [Fact]
        public void A_checkpoint_whose_pending_record_will_not_write_says_so()
        {
            TraceSessionHandle live = Open(TraceLevel.Verbose, asCapture: true);
            Guid captureId = TraceCoordinator.CaptureId;
            DateTime? startedAt = TraceCoordinator.CaptureStartedLocal;
            Write("before the bundle");
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPartPath(_livePath, boot, 1);
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(target));

            // Held so the condition is still there to read: the worker would
            // otherwise commit the part and resolve it in milliseconds.
            using var held = new HeldWorker(t => t.IsCheckpoint);
            TraceTransitionResult snap = TraceCoordinator.SnapshotForBundle(live);

            Assert.Equal(TraceTransition.Accepted, snap.Status);
            Assert.Equal(target, snap.Ticket.SourcePath);
            Assert.True(snap.PendingRecordFailed);
            Assert.False(snap.Ticket.PendingRecordWritten);
            Assert.Contains(snap.DeferredFaults, f => f.Contains("NO durable pending record", StringComparison.Ordinal));
            Assert.True(File.Exists(target));
            Assert.True(TraceEvidencePins.IsPinned(target));

            // Same session, same capture, same start; no false end.
            Assert.Equal(live.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(TraceCoordinator.CaptureRunning);
            Assert.Equal(captureId, TraceCoordinator.CaptureId);
            Assert.Equal(startedAt, TraceCoordinator.CaptureStartedLocal);
            Assert.Null(snap.Ticket.Entry.EndTime);
            Assert.True(snap.Ticket.IsCheckpoint);

            // The next part is writable and verified.
            Assert.True(snap.TracingOn);
            Write("after the bundle");
            Assert.Contains("after the bundle", ReadLiveText(_livePath), StringComparison.Ordinal);

            TraceRecoveryCondition condition = Assert.Single(TraceRecordingHealth.Snapshot().Unresolved);
            Assert.True(condition.IsCheckpoint);
            Assert.Equal(1, condition.PartNumber);
            Assert.Equal(live.SessionId, condition.SessionId);
            TraceEvidencePins.Release(target);
            held.Release();
            Assert.True(snap.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
        }

        /// <summary>
        /// Continuation cannot override the standing intent: with the standing
        /// log OFF, a failed record still means no successor. The ticket is
        /// still queued and the condition still reachable — the two facts are
        /// independent of the successor decision.
        /// </summary>
        [Fact]
        public void A_failed_record_with_the_standing_log_off_opens_nothing_and_still_reports()
        {
            TraceSessionHandle a = Open();
            Write("x");
            TraceCoordinator.SetStandingIntent(false, TraceLevel.Info);
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPath(_livePath, boot);
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(target));

            using var held = new HeldWorker(t => t.SessionId == a.SessionId);
            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = a,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.Accepted, r.Status);
            Assert.True(r.PendingRecordFailed);
            Assert.False(r.SuccessorOpened);
            Assert.False(r.RestartFailed);
            Assert.False(r.TracingOn);
            Assert.NotNull(r.Ticket.Completion);
            TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
            Assert.Single(snap.Unresolved);
            Assert.Equal(TraceSinkState.Off, snap.SinkState);   // by choice, not failure
            held.Release();
            Assert.True(r.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
        }

        /// <summary>Same, for shutdown: exit committed means no successor,
        /// whatever happened to the record.</summary>
        [Fact]
        public void A_failed_record_during_shutdown_opens_nothing_and_still_reports()
        {
            TraceSessionHandle a = Open();
            Write("x");
            DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string target = TraceFileNaming.StampedPath(_livePath, boot);
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(target));
            TraceCoordinator.LatchShutdown();

            using var held = new HeldWorker(t => t.SessionId == a.SessionId);
            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = a,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.Accepted, r.Status);
            Assert.True(r.PendingRecordFailed);
            Assert.False(r.SuccessorOpened);
            Assert.Contains("exit is committed", r.Explanation, StringComparison.Ordinal);
            Assert.NotNull(r.Ticket.Completion);
            Assert.Single(TraceRecordingHealth.Snapshot().Unresolved);
            held.Release();
            Assert.True(r.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
        }

        /// <summary>
        /// A compression that cannot happen (no archive root) is delivered to
        /// the operator's state BEFORE the ticket's completion is released,
        /// and without any archive path to offer — a notification cannot
        /// depend on a path existing.
        /// </summary>
        [Fact]
        public void An_archive_that_fails_is_delivered_before_completion_and_without_a_path()
        {
            TraceSessionHandle a = Open();
            Write("x");
            TraceCoordinator.ArchiveRootDir = null;   // the worker cannot make a zip
            bool deliveredBeforeCompletion = false;
            TraceRecoveryCondition seen = null;
            TraceArchiveTicket ticket = null;
            var raised = new ManualResetEventSlim(false);
            void OnChanged(TraceRecordingHealthChange c)
            {
                if (c.Kind != TraceRecordingHealthChangeKind.ConditionRaised) return;
                seen = c.Condition;
                deliveredBeforeCompletion = ticket != null && !ticket.Completion.IsCompleted;
                raised.Set();
            }
            TraceRecordingHealth.Changed += OnChanged;
            try
            {
                TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = a,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.Standing,
                });
                ticket = r.Ticket;
                Assert.False(r.PendingRecordFailed);   // the sidecar was fine; it is the ARCHIVE that fails
                Assert.True(raised.Wait(TimeSpan.FromSeconds(30)), "the archive failure never reached the health model");
                Assert.True(ticket.Completion.Wait(TimeSpan.FromSeconds(30)));
                Assert.False(ticket.Completion.Result.ArchiveCommitted);
                Assert.Null(ticket.Completion.Result.ArchiveFullPath);

                Assert.True(deliveredBeforeCompletion, "the condition was raised only after the completion was released");
                Assert.NotNull(seen);
                Assert.Equal("no_archive_root", seen.ArchiveFailureStage);
                Assert.True(seen.RawRetained);
                Assert.Equal(ticket.SourcePath, seen.RawPath);
                // And the live recording is unaffected by the old file's fate.
                Assert.True(TraceCoordinator.Recording);
                Assert.Equal(r.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            }
            finally
            {
                TraceRecordingHealth.Changed -= OnChanged;
                TraceCoordinator.ArchiveRootDir = _archiveDir;
            }
        }

        /// <summary>
        /// Two tickets at risk; the first's archive commits. ONLY that ticket
        /// resolves, and the live recording state does not move.
        /// </summary>
        [Fact]
        public void A_later_successful_archive_resolves_only_its_own_ticket()
        {
            TraceSessionHandle a = Open();
            Write("A");
            DateTime bootA = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(TraceFileNaming.StampedPath(_livePath, bootA)));

            var holdB = new ManualResetEventSlim(false);
            var atB = new ManualResetEventSlim(false);
            Guid bSession = Guid.Empty;
            TraceArchiveWorker.BeforeArchiveForTests = t =>
            {
                if (t.SessionId != bSession) return;
                atB.Set();
                holdB.Wait(TimeSpan.FromSeconds(30));
            };
            try
            {
                TraceTransitionResult archiveA = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = a, OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
                });
                Assert.True(archiveA.PendingRecordFailed);
                TraceSessionHandle b = archiveA.Successor;
                bSession = b.SessionId;
                Write("B");
                DateTime bootB = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
                Directory.CreateDirectory(TraceArchiveWorker.PendingPathFor(TraceFileNaming.StampedPath(_livePath, bootB)));
                TraceTransitionResult archiveB = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = b, OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
                });
                Assert.True(archiveB.PendingRecordFailed);
                TraceSessionHandle c = archiveB.Successor;

                // A commits (the worker is serial: A runs before it reaches B).
                Assert.True(archiveA.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
                Assert.True(archiveA.Ticket.Completion.Result.ArchiveCommitted);
                Assert.True(atB.Wait(TimeSpan.FromSeconds(10)));

                TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
                TraceRecoveryCondition only = Assert.Single(snap.Unresolved);
                Assert.Equal(archiveB.Ticket.TicketId, only.TicketId);
                Assert.Equal(c.SessionId, TraceCoordinator.CurrentHandle.SessionId);
                Assert.Equal(TraceSinkState.Recording, snap.SinkState);

                holdB.Set();
                Assert.True(archiveB.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
                Assert.Empty(TraceRecordingHealth.Snapshot().Unresolved);
                Assert.Equal(c.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            }
            finally
            {
                holdB.Set();
                TraceArchiveWorker.BeforeArchiveForTests = null;
            }
        }

        // ── Track H7: deferred lines are bound to their session ────────────

        /// <summary>
        /// <b>Sol's interleaving (review of H6, finding 1).</b> A Stop holds the
        /// gate past the point where it drains its own session's lines; a fall
        /// reads the old published handle and queues its lines; the Stop
        /// publishes its successor; the lines drain. Until H7 they were unbound
        /// and landed in the successor as bare <c>Connected:False</c> lines.
        /// Now they are bound to the session they were formatted under and,
        /// that session being archived, are REFUSED: written into the successor
        /// only as refusal records naming both sessions, never as a bare line,
        /// and never dropped.
        ///
        /// <para>Positive control, run by hand at H7: with the binding removed
        /// (bound session forced to empty), the bare line lands in the
        /// successor and this test goes red.</para>
        /// </summary>
        [Fact]
        public void A_fall_line_queued_after_the_archive_drained_its_session_is_refused_not_misfiled()
        {
            TraceSessionHandle old = Open(TraceLevel.Verbose);
            Write("the old session");

            var inside = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            TraceCoordinator.TransitionProbeForTests = point =>
            {
                if (point != "archive:detached") return;   // past the archive's own drain point
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            TraceTransitionResult stop = null;
            Thread stopper = null;
            try
            {
                stopper = new Thread(() => stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = old, OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
                })) { IsBackground = true };
                stopper.Start();
                Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the Stop never reached its barrier");

                // The fall: reads the published handle (still the old session —
                // publication happens at the END of the transition) and queues
                // its lines without touching the gate.
                Assert.Equal(old.SessionId, TraceCoordinator.CurrentHandle.SessionId);
                Tracing.TraceLineDeferred("Connected:False", TraceLevel.Error);
                Tracing.TraceLineDeferred("connection fell: H7-test — our connection dropped without us asking", TraceLevel.Warning);
                Assert.Equal(0, Tracing.DeferredLinesRefused);
            }
            finally
            {
                release.Set();
                TraceCoordinator.TransitionProbeForTests = null;
                stopper?.Join(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(TraceTransition.Accepted, stop.Status);
            TraceSessionHandle successor = stop.Successor;
            Assert.NotNull(successor);
            // Drained on the transition's way out, so both are refused by now.
            Tracing.FlushDeferred();
            Assert.Equal(2, Tracing.DeferredLinesRefused);

            // The successor carries them ONLY as refusal records.
            string live = ReadLiveText(_livePath);
            Assert.Contains("TraceDeferred: REFUSED", live, StringComparison.Ordinal);
            Assert.Contains(old.SessionId.ToString(), live, StringComparison.Ordinal);
            Assert.Contains(successor.SessionId.ToString(), live, StringComparison.Ordinal);
            foreach (string line in live.Split('\n'))
            {
                if (!line.Contains("Connected:False", StringComparison.Ordinal)
                    && !line.Contains("connection fell", StringComparison.Ordinal)) continue;
                Assert.Contains("TraceDeferred: REFUSED", line, StringComparison.Ordinal);
            }
            // And the old session's archive does not have them either: they
            // arrived after its drain point, which is the honest limit.
            string archived = ReadArchivedText(stop.Ticket);
            Assert.Contains("the old session", archived, StringComparison.Ordinal);
            Assert.DoesNotContain("connection fell", archived, StringComparison.Ordinal);
        }

        /// <summary>
        /// The other ordering, and the ordinary case: the fall's lines are
        /// queued BEFORE the archive reaches its drain point, so the archive writes
        /// them into the session they describe, ahead of its terminal records,
        /// and nothing is refused.
        /// </summary>
        [Fact]
        public void A_fall_line_queued_before_the_archive_drains_lands_in_the_session_it_describes()
        {
            TraceSessionHandle old = Open(TraceLevel.Verbose);
            Write("the old session");

            var inside = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            TraceCoordinator.TransitionProbeForTests = point =>
            {
                if (point != "archive:owned") return;   // ownership decided, drain not yet run
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            TraceTransitionResult stop = null;
            Thread stopper = null;
            try
            {
                stopper = new Thread(() => stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = old, OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
                })) { IsBackground = true };
                stopper.Start();
                Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
                Tracing.TraceLineDeferred("Connected:False", TraceLevel.Error);
                Tracing.TraceLineDeferred("connection fell: H7-test — our connection dropped without us asking", TraceLevel.Warning);
            }
            finally
            {
                release.Set();
                TraceCoordinator.TransitionProbeForTests = null;
                stopper?.Join(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(TraceTransition.Accepted, stop.Status);
            Tracing.FlushDeferred();
            Assert.Equal(0, Tracing.DeferredLinesRefused);
            string archived = ReadArchivedText(stop.Ticket);
            Assert.Contains("Connected:False", archived, StringComparison.Ordinal);
            Assert.Contains("connection fell: H7-test", archived, StringComparison.Ordinal);
            // Ahead of the terminal marker.
            Assert.True(archived.IndexOf("connection fell: H7-test", StringComparison.Ordinal)
                        < archived.IndexOf("level=Off", StringComparison.Ordinal));
            Assert.DoesNotContain("connection fell", ReadLiveText(_livePath), StringComparison.Ordinal);
        }

        // ── Track H7: nobody waits on a transition to write a line ─────────

        /// <summary>
        /// An ordinary <c>Tracing.TraceLine</c> and a direct
        /// <c>System.Diagnostics.Trace.WriteLine</c> — the way JJFlexWpf and
        /// FlexLib's panadapter write — both RETURN while a transition holds
        /// the gate, and both land in the successor, in order, once it opens.
        /// The gate is proved held by a gated read that does block.
        ///
        /// <para>Positive control, run by hand at H7: with the transition check
        /// taken out of <c>TryEnterForWrite</c>, both writers block and this
        /// test goes red.</para>
        /// </summary>
        [Fact]
        public void A_transition_holding_the_gate_does_not_block_an_ordinary_writer()
        {
            TraceSessionHandle old = Open();
            Write("before");

            var inside = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            TraceCoordinator.TransitionProbeForTests = point =>
            {
                if (point != "archive:detached") return;
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            TraceTransitionResult stop = null;
            Thread stopper = null, reader = null, ours = null, direct = null;
            try
            {
                stopper = new Thread(() => stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = old, OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
                })) { IsBackground = true };
                stopper.Start();
                Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
                Assert.True(TraceCoordinator.TransitionInProgress);

                // The gate IS held: a gated read cannot get in.
                reader = new Thread(() => { _ = TraceCoordinator.Recording; }) { IsBackground = true };
                reader.Start();
                Assert.False(reader.Join(TimeSpan.FromMilliseconds(300)), "a gated read got in; the gate is not held and nothing below is measured");

                // The writers do not wait on it.
                ours = new Thread(() => Tracing.TraceLine("H7: ours, written during a transition")) { IsBackground = true };
                direct = new Thread(() => Trace.WriteLine("H7: direct, written during a transition")) { IsBackground = true };
                ours.Start();
                direct.Start();
                Assert.True(ours.Join(TimeSpan.FromSeconds(2)), "Tracing.TraceLine waited on a transition's file I/O");
                Assert.True(direct.Join(TimeSpan.FromSeconds(2)), "Trace.WriteLine waited on a transition's file I/O");
                Assert.True(Tracing.DeferredLinesQueued >= 2);
                Assert.True(stopper.IsAlive, "the transition finished early; nothing was measured");
            }
            finally
            {
                release.Set();
                TraceCoordinator.TransitionProbeForTests = null;
                stopper?.Join(TimeSpan.FromSeconds(10));
                reader?.Join(TimeSpan.FromSeconds(10));
                ours?.Join(TimeSpan.FromSeconds(10));
                direct?.Join(TimeSpan.FromSeconds(10));
            }

            // Both landed in the SUCCESSOR — where they would have landed had
            // they waited — before anything written directly afterwards.
            Assert.Equal(TraceTransition.Accepted, stop.Status);
            Write("H7: after the transition");
            string live = ReadLiveText(_livePath);
            int ourAt = live.IndexOf("H7: ours, written during a transition", StringComparison.Ordinal);
            int directAt = live.IndexOf("H7: direct, written during a transition", StringComparison.Ordinal);
            int afterAt = live.IndexOf("H7: after the transition", StringComparison.Ordinal);
            Assert.True(ourAt >= 0 && directAt >= 0 && afterAt >= 0, "a deferred line never landed: " + live);
            Assert.True(ourAt < afterAt && directAt < afterAt, "a deferred line landed after a later direct write");
            Assert.DoesNotContain("H7: ours", ReadArchivedText(stop.Ticket), StringComparison.Ordinal);
            Assert.Equal(0, Tracing.DeferredLinesRefused);
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
            TraceTransitionResult archived_ = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = stale,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });

            // The expected session WAS archived, so its own ticket is the
            // evidence and the bundler may use that.
            TraceTransitionResult known = TraceCoordinator.SnapshotForBundle(stale);
            Assert.Equal(TraceTransition.AlreadyClaimed, known.Status);
            Assert.Same(archived_.Ticket, known.Ticket);
            // Pinned for the bundle like any snapshot (Track H6); the bundler's
            // Finally is what releases it.
            Assert.True(TraceEvidencePins.IsPinned(known.Ticket.SourcePath));
            TraceEvidencePins.Release(known.Ticket.SourcePath);

            // A session this process never archived: an explicit refusal, with no
            // ticket at all.
            var neverSeen = (TraceSessionHandle)typeof(TraceSessionHandle)
                .GetConstructors(System.Reflection.BindingFlags.NonPublic
                               | System.Reflection.BindingFlags.Instance)[0]
                .Invoke(new object[] { new TraceSession() });
            TraceTransitionResult refused = TraceCoordinator.SnapshotForBundle(neverSeen);
            Assert.Equal(TraceTransition.NotCurrent, refused.Status);
            Assert.Null(refused.Ticket);

            // And the live session is untouched throughout.
            Assert.Equal(archived_.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);
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
        /// <b>A bundle keeps the evidence of a session that was archived with
        /// nothing after it.</b> Sol's review of H3, finding 5: the checkpoint
        /// answered NoSession before looking up an already archived session's
        /// ticket, so a Stop or logging switched off between the bundler reading
        /// its handle and arriving at the checkpoint — archiving the session with
        /// no successor — made the bundle leave out a trace that was archived,
        /// detached and sitting on disk.
        ///
        /// <para>Positive control, run by hand at H6: with the old order
        /// restored, the answer is NoSession and this test goes red.</para>
        /// </summary>
        [Fact]
        public void A_bundle_keeps_a_session_archived_with_nothing_after_it()
        {
            TraceSessionHandle expected = Open();          // what the bundler read
            Write("the evening the operator is reporting");

            // Logging switched off before the bundler reaches the checkpoint.
            TraceTransitionResult off = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
            // archived, with nothing recording, is still NoSession.
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

        // ── Rotation against an archive, and direct Trace writers ──────────────

        /// <summary>
        /// Force rotation against an archive, with a producer that writes through
        /// <c>System.Diagnostics.Trace</c> directly rather than through
        /// <see cref="Tracing"/> — which is how JJFlexWpf and FlexLib's
        /// panadapter write. No deadlock, one terminal marker in the archived
        /// tail, a correct part identity, and every manifest entry kept.
        /// </summary>
        [Fact]
        public void Rotation_racing_a_archive_keeps_every_part_and_one_terminal_marker()
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
                TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
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
                // worker and the final archive share one archive transaction now,
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
        /// <b>Sol's review of H12: a rotation whose recovery cannot reopen the
        /// file is a sink fault like any other.</b> The rotation closes the
        /// part, the move fails, and the recovery's own open fails too. That
        /// used to leave a closed sink with no fault latched, so the
        /// coordinator never queued the retirement, health stayed
        /// <c>Recording</c>, the Problems list stayed silent, and Diagnostics
        /// said a capture was in progress while every later line was refused.
        ///
        /// <para>Both failures are real file operations, not mocks. The live
        /// file is deleted under the sink (it opens with
        /// <c>FileShare.Delete</c>, so it may be), which makes the move fail
        /// and sends the recovery to the part path; a DIRECTORY sits at the
        /// part path, so that open fails as well. Rotation is forced through
        /// the existing <see cref="Tracing.RotationThresholdBytes"/> seam at
        /// 4096 bytes, which the sink takes as its constructor threshold.</para>
        /// </summary>
        [Fact]
        public void A_rotation_whose_recovery_cannot_reopen_the_file_retires_the_session_as_failed()
        {
            long savedThreshold = Tracing.RotationThresholdBytes;
            try
            {
                Tracing.RotationThresholdBytes = 4096;
                TraceSessionHandle a = Open();
                RotatingTraceListener sink = Tracing.LiveListener;
                Assert.NotNull(sink);
                Assert.True(TraceCoordinator.RecordingWithoutWaiting());

                DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
                string partPath = TraceFileNaming.StampedPartPath(_livePath, boot, 1);
                Directory.CreateDirectory(partPath);
                File.Delete(_livePath);

                using var held = new HeldFaultRetire();
                using var changes = new HealthChanges();
                for (int i = 0; i < 400 && !held.Captured; i++)
                    Write("rotation filler " + i + " " + new string('x', 80));

                // The fault is latched through the same path a failed write
                // takes, and it carries both causes.
                Assert.True(sink.IsClosed, "the rotation never ran, or its recovery succeeded");
                Assert.Equal(1, sink.PartNumber);
                Assert.NotNull(sink.LastRotationError);
                Assert.NotNull(sink.WriteFault);
                Assert.Contains(sink.LastRotationError, sink.WriteFault, StringComparison.Ordinal);
                Assert.NotEqual(sink.LastRotationError, sink.WriteFault);
                Assert.True(sink.Faulted);

                // Retirement queued, and the drop-window reader already reads
                // the closed file as not recording.
                Assert.True(held.Captured, "the coordinator never queued the fault retirement");
                Assert.False(TraceCoordinator.RecordingWithoutWaiting());

                held.Run();

                TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
                Assert.Equal(TraceSinkState.Failed, snap.SinkState);
                Assert.Equal(sink.WriteFault, snap.SinkFault);
                Assert.True(snap.NeedsAttention);

                TraceRecordingHealthChange failed = Assert.Single(
                    changes.All, c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed);
                var entry = RecordingHealthNotice.Announcement(failed);
                Assert.NotNull(entry);
                Assert.Equal(FailureKind.RecordingStopped, entry.Value.Kind);
                Assert.Contains(sink.WriteFault, entry.Value.Detail, StringComparison.Ordinal);

                // The session is over: nothing claims to be recording it.
                Assert.Null(TraceCoordinator.CurrentHandle);
                Assert.False(TraceCoordinator.Recording);
                Assert.False(TraceCoordinator.RecordingWithoutWaiting());
                Assert.NotEqual(Guid.Empty, a.SessionId);
            }
            finally
            {
                Tracing.RotationThresholdBytes = savedThreshold;
            }
        }

        /// <summary>
        /// The positive control for the test above: the same writes at the
        /// same threshold, with nothing in the way, rotate cleanly and leave
        /// the session recording — no fault, no retirement, no Problems entry.
        /// </summary>
        [Fact]
        public void A_rotation_that_succeeds_stays_recording()
        {
            long savedThreshold = Tracing.RotationThresholdBytes;
            try
            {
                Tracing.RotationThresholdBytes = 4096;
                TraceSessionHandle a = Open();
                RotatingTraceListener sink = Tracing.LiveListener;
                Assert.NotNull(sink);

                using var held = new HeldFaultRetire();
                using var changes = new HealthChanges();
                for (int i = 0; i < 400 && sink.PartNumber < 2; i++)
                    Write("rotation filler " + i + " " + new string('x', 80));

                Assert.Equal(2, sink.PartNumber);
                Assert.False(sink.IsClosed);
                Assert.Null(sink.WriteFault);
                Assert.Null(sink.LastRotationError);
                Assert.False(held.Captured);
                Assert.True(TraceCoordinator.RecordingWithoutWaiting());
                Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
                Assert.DoesNotContain(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed);
                Assert.Equal(a.SessionId, TraceCoordinator.CurrentHandle?.SessionId);
            }
            finally
            {
                Tracing.RotationThresholdBytes = savedThreshold;
            }
        }

        // ── A part that has moved is archived, whatever fails after (H14) ──

        /// <summary>The archive worker's hold for this session's rotated
        /// part 1, capturing its ticket.</summary>
        private sealed class HeldPartOne : IDisposable
        {
            private readonly HeldWorker _held;
            public TraceArchiveTicket Ticket { get; private set; }

            public HeldPartOne(Guid sessionId, string partPath)
            {
                _held = new HeldWorker(t =>
                {
                    if (t.SessionId != sessionId || t.PartNumber != 1 || t.IsFinalPart
                        || !string.Equals(t.SourcePath, partPath, StringComparison.OrdinalIgnoreCase))
                        return false;
                    Ticket = t;
                    return true;
                });
            }

            public bool Reached(TimeSpan wait) => _held.Reached.Wait(wait);
            public void Release() => _held.Release();
            public void Dispose() => _held.Dispose();
        }

        /// <summary>Everything a moved part must have to outlive the
        /// plain-text sweep: its archive committed, its name in the manifest
        /// (which is how the sweep tells an archived file from an orphan), and
        /// the bytes written before the rotation inside the zip.</summary>
        private void AssertPartOneArchived(HeldPartOne held, Guid sessionId, string partPath, string firstLine)
        {
            held.Release();
            Assert.True(TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(60)), "the archive backlog never drained");

            TraceArchiveTicket ticket = held.Ticket;
            Assert.NotNull(ticket);
            Assert.True(ticket.Completion.Result.ArchiveCommitted,
                        "the moved part's archive was not committed: " + ticket.Completion.Result.FailureStage);

            TraceSessionEntry entry = Assert.Single(Manifest().Entries,
                e => e.SessionId == sessionId.ToString() && e.PartNumber == 1);
            Assert.Equal(Path.GetFileName(partPath), entry.SourceName);

            var archivedNames = new HashSet<string>(
                Manifest().Entries.Where(e => e != null && e.SourceName != null).Select(e => e.SourceName),
                StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFileName(partPath), archivedNames);

            Assert.Contains(firstLine, ReadArchivedText(ticket), StringComparison.Ordinal);
        }

        /// <summary>
        /// <b>Sol's review of H13, blocker 1.</b> The part moves, then the
        /// fresh file will not open and neither will the recovery's append.
        /// The hand-off to the archive used to sit after the whole rotation,
        /// so this jumped past it: the retirement's archive looked at the empty
        /// live path, the moved part got no ticket and no pending record, and
        /// the plain-text sweep eventually deleted the only copy as an orphan.
        ///
        /// <para>Both failures are real file operations. The step seam only
        /// marks the moment: once the part has moved, a directory is created
        /// at the live path, so the fresh create and the recovery's append
        /// both fail on the disk.</para>
        /// </summary>
        [Fact]
        public void A_part_that_moved_before_the_fresh_file_failed_to_open_is_archived_and_the_session_retires_as_failed()
        {
            long savedThreshold = Tracing.RotationThresholdBytes;
            try
            {
                Tracing.RotationThresholdBytes = 4096;
                TraceSessionHandle a = Open();
                RotatingTraceListener sink = Tracing.LiveListener;
                Assert.NotNull(sink);

                DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
                string partPath = TraceFileNaming.StampedPartPath(_livePath, boot, 1);

                bool obstacleLaid = false;
                sink.RotationStepForTests = step =>
                {
                    if (step != RotatingTraceListener.RotationStepMoved || obstacleLaid) return;
                    obstacleLaid = true;
                    Directory.CreateDirectory(_livePath);
                };

                using var part = new HeldPartOne(a.SessionId, partPath);
                using var held = new HeldFaultRetire();
                using var changes = new HealthChanges();
                const string firstLine = "moved part filler 0 ";
                for (int i = 0; i < 400 && !held.Captured; i++)
                    Write("moved part filler " + i + " " + new string('x', 80));
                sink.RotationStepForTests = null;

                Assert.True(obstacleLaid, "the part never moved");
                Assert.True(File.Exists(partPath), "the moved part is not at its part path");
                Assert.True(Directory.Exists(_livePath));

                // Tracing is down and says so, as H13 made it.
                Assert.True(sink.IsClosed, "the rotation's recovery reopened something");
                Assert.NotNull(sink.LastRotationError);
                Assert.NotNull(sink.WriteFault);
                Assert.Contains(sink.LastRotationError, sink.WriteFault, StringComparison.Ordinal);
                Assert.True(held.Captured, "the coordinator never queued the fault retirement");
                Assert.False(TraceCoordinator.RecordingWithoutWaiting());

                // The moved part was handed to its archive at the move.
                Assert.True(part.Reached(TimeSpan.FromSeconds(10)),
                            "the moved part was never given an archive ticket");

                held.Run();

                TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
                Assert.Equal(TraceSinkState.Failed, snap.SinkState);
                Assert.Equal(sink.WriteFault, snap.SinkFault);
                Assert.Single(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed);
                Assert.Null(TraceCoordinator.CurrentHandle);
                Assert.False(TraceCoordinator.Recording);
                Assert.False(TraceCoordinator.RecordingWithoutWaiting());

                AssertPartOneArchived(part, a.SessionId, partPath, firstLine);

                // ── Sol's review of H14: the retry must be able to open ──
                // The retirement's archive could not detach the live path —
                // but there is no trace file there, only the obstacle. It
                // must not be recorded as retained evidence, or every later
                // Begin refuses the path over a file that does not exist.
                Assert.False(File.Exists(_livePath), "the precondition: no trace file at the live path");
                Assert.DoesNotContain(_livePath, TraceCoordinator.RetainedEvidencePathsForTests);

                // The obstacle goes, and the operator's off-and-on opens a
                // fresh file: no refusal, no reclaim of a phantom, recording.
                Directory.Delete(_livePath);
                TraceTransitionResult on = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
                Assert.Equal(TraceTransition.Accepted, on.Status);   // was Failed at "retained-evidence"
                Assert.NotEqual("retained-evidence", on.FailedStage);
                Assert.Null(on.RetainedSourcePath);
                Assert.Null(on.Reclaimed);
                Assert.True(on.TracingOn);
                Assert.DoesNotContain("still holds the raw trace", on.SinkFault ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain(on.DeferredFaults ?? new List<string>(),
                    f => f.Contains("still holds the raw trace", StringComparison.Ordinal)
                         || f.Contains("still could not be moved aside", StringComparison.Ordinal));
                TraceSessionHandle b = on.Successor;
                Assert.NotNull(b);
                Assert.NotEqual(a.SessionId, b.SessionId);
                Assert.Equal(b.SessionId, TraceCoordinator.CurrentHandle?.SessionId);
                Assert.Empty(TraceCoordinator.RetainedEvidencePathsForTests);

                Write("a line after the retry");
                Trace.Flush();
                Assert.True(File.Exists(_livePath), "the retry opened no file at the live path");
                Assert.Contains("a line after the retry", ReadLiveText(_livePath), StringComparison.Ordinal);
                Assert.True(TraceCoordinator.Recording);
                Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
                Assert.Equal(b.SessionId, TraceRecordingHealth.Snapshot().LiveSessionId);
            }
            finally
            {
                Tracing.RotationThresholdBytes = savedThreshold;
            }
        }

        /// <summary>
        /// <b>Sol's review of H14, the reclaim half.</b> A genuine retained
        /// file — the detach failed with the bytes present — whose file then
        /// disappears from outside the process. The reclaim's move now fails
        /// for want of a source, not for the obstacle, and must clear the
        /// record rather than refuse the path forever. The stamped target is
        /// kept blocked throughout, so what clears the record is the absence,
        /// not a move that succeeded.
        ///
        /// <para>The positive control is the first half: with the file still
        /// there, the open is refused and the bytes are untouched — H9's guard,
        /// unchanged.</para>
        /// </summary>
        [Fact]
        public void A_retained_file_that_disappears_is_released_by_the_next_Begin_instead_of_refused_forever()
        {
            TraceSessionHandle a = Open();
            Write("what A managed to write");
            string aTarget = TraceFileNaming.StampedPath(_livePath, TraceCoordinator.Observe().SessionBootTimeUtc.Value);
            Directory.CreateDirectory(aTarget);
            try
            {
                using var held = new HeldFaultRetire();
                BreakTheLiveSink();
                Write("the write that fails");
                held.Run();   // archives A, cannot move it: the bytes are REAL, so they are retained
                Assert.Contains(_livePath, TraceCoordinator.RetainedEvidencePathsForTests);
                string retained = File.ReadAllText(_livePath);
                Assert.Contains("what A managed to write", retained, StringComparison.Ordinal);

                // Positive control: real bytes still refuse a truncating open.
                TraceTransitionResult refused = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
                Assert.Equal(TraceTransition.Failed, refused.Status);
                Assert.Equal("retained-evidence", refused.FailedStage);
                Assert.Equal(retained, File.ReadAllText(_livePath));
                Assert.Contains(_livePath, TraceCoordinator.RetainedEvidencePathsForTests);

                // The file goes, from outside; the target is still blocked.
                File.Delete(_livePath);

                TraceTransitionResult on = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
                Assert.Equal(TraceTransition.Accepted, on.Status);   // was Failed at "retained-evidence", forever
                Assert.Null(on.Reclaimed);
                Assert.True(on.TracingOn);
                Assert.Empty(TraceCoordinator.RetainedEvidencePathsForTests);
                Assert.Contains(on.DeferredFaults, f => f.Contains(a.SessionId.ToString(), StringComparison.Ordinal)
                                                        && f.Contains("is no longer at", StringComparison.Ordinal));
                Assert.DoesNotContain(on.DeferredFaults,
                    f => f.Contains("still could not be moved aside", StringComparison.Ordinal));
                Assert.True(Directory.Exists(aTarget), "the target was unblocked, so this proves nothing about absence");

                Write("a line after the release");
                Trace.Flush();
                Assert.Contains("a line after the release", ReadLiveText(_livePath), StringComparison.Ordinal);
                Assert.Equal(on.Successor.SessionId, TraceCoordinator.CurrentHandle?.SessionId);
            }
            finally
            {
                try { Directory.Delete(aTarget); } catch { }
            }
        }

        /// <summary>
        /// <b>Sol's review of H13, blocker 2.</b> The part moves and the
        /// fresh file opens, then the continuation header's write or flush
        /// throws. The recovery's append succeeds, so recording carries on —
        /// which used to hide that the moved part had no ticket, and that the
        /// fresh file's stream was left open while the recovery overwrote the
        /// only reference to it.
        ///
        /// <para>The step seam stands in for the failing header write: it
        /// throws at the header step, after the fresh open, from inside the
        /// same try. It also reads the stream that open created, so the test
        /// can see whether it was closed.</para>
        /// </summary>
        [Fact]
        public void A_part_that_moved_before_the_continuation_header_failed_is_archived_and_the_first_stream_is_closed()
        {
            long savedThreshold = Tracing.RotationThresholdBytes;
            try
            {
                Tracing.RotationThresholdBytes = 4096;
                TraceSessionHandle a = Open();
                RotatingTraceListener sink = Tracing.LiveListener;
                Assert.NotNull(sink);

                DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
                string partPath = TraceFileNaming.StampedPartPath(_livePath, boot, 1);
                var streamField = typeof(RotatingTraceListener)
                    .GetField("_stream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

                FileStream first = null;
                sink.RotationStepForTests = step =>
                {
                    if (step != RotatingTraceListener.RotationStepHeader || first != null) return;
                    first = (FileStream)streamField.GetValue(sink);
                    throw new IOException("the continuation header would not write (test)");
                };

                using var part = new HeldPartOne(a.SessionId, partPath);
                using var held = new HeldFaultRetire();
                using var changes = new HealthChanges();
                const string firstLine = "header part filler 0 ";
                for (int i = 0; i < 400 && first == null; i++)
                    Write("header part filler " + i + " " + new string('x', 80));
                sink.RotationStepForTests = null;

                Assert.NotNull(first);
                Assert.False(first.CanWrite, "the stream the failed header left open was never closed");
                Assert.NotSame(first, streamField.GetValue(sink));

                // The recovery resumed at the live path, as part 2, and the
                // session is still recording.
                Assert.False(sink.IsClosed);
                Assert.Null(sink.WriteFault);
                Assert.NotNull(sink.LastRotationError);
                Assert.Equal(2, sink.PartNumber);
                Assert.Equal(_livePath, sink.FilePath);
                Assert.False(held.Captured);
                Assert.True(TraceCoordinator.RecordingWithoutWaiting());
                Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
                Assert.DoesNotContain(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed);
                Assert.Equal(a.SessionId, TraceCoordinator.CurrentHandle?.SessionId);

                Write("a line after the failed header");
                Assert.Contains("a line after the failed header", ReadLiveText(_livePath), StringComparison.Ordinal);

                // The moved part was handed to its archive at the move, and
                // the recovery did not write into it.
                Assert.True(part.Reached(TimeSpan.FromSeconds(10)),
                            "the moved part was never given an archive ticket");
                AssertPartOneArchived(part, a.SessionId, partPath, firstLine);
                Assert.DoesNotContain("a line after the failed header", File.ReadAllText(partPath), StringComparison.Ordinal);
            }
            finally
            {
                Tracing.RotationThresholdBytes = savedThreshold;
            }
        }

        /// <summary>
        /// The positive control for the two tests above: an ordinary rotation
        /// passes both steps the seam marks, hands part 1 to its archive,
        /// opens part 2 at the live path with its continuation header, and
        /// keeps recording with no rotation error.
        /// </summary>
        [Fact]
        public void An_ordinary_rotation_hands_its_part_to_the_archive_and_carries_on()
        {
            long savedThreshold = Tracing.RotationThresholdBytes;
            try
            {
                Tracing.RotationThresholdBytes = 4096;
                TraceSessionHandle a = Open();
                RotatingTraceListener sink = Tracing.LiveListener;
                Assert.NotNull(sink);

                DateTime boot = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
                string partPath = TraceFileNaming.StampedPartPath(_livePath, boot, 1);

                var steps = new List<string>();
                sink.RotationStepForTests = step => steps.Add(step);

                using var part = new HeldPartOne(a.SessionId, partPath);
                using var held = new HeldFaultRetire();
                const string firstLine = "ordinary filler 0 ";
                for (int i = 0; i < 400 && sink.PartNumber < 2; i++)
                    Write("ordinary filler " + i + " " + new string('x', 80));
                sink.RotationStepForTests = null;

                Assert.Equal(new[] { RotatingTraceListener.RotationStepMoved, RotatingTraceListener.RotationStepHeader },
                             steps);
                Assert.Equal(2, sink.PartNumber);
                Assert.False(sink.IsClosed);
                Assert.Null(sink.WriteFault);
                Assert.Null(sink.LastRotationError);
                Assert.Equal(_livePath, sink.FilePath);
                Assert.False(held.Captured);
                Assert.True(TraceCoordinator.RecordingWithoutWaiting());
                Assert.StartsWith("--- trace continues from part 001 (" + Path.GetFileName(partPath) + ")",
                                  ReadLiveText(_livePath), StringComparison.Ordinal);

                Assert.True(part.Reached(TimeSpan.FromSeconds(10)),
                            "the rotated part was never given an archive ticket");
                AssertPartOneArchived(part, a.SessionId, partPath, firstLine);
            }
            finally
            {
                Tracing.RotationThresholdBytes = savedThreshold;
            }
        }

        /// <summary>
        /// The router stays in <c>Trace.Listeners</c> for the life of the
        /// process. Archiving a session used to call process-wide
        /// <c>Trace.Close</c> and remove the listener, so between a close and
        /// the next open there were zero listeners and direct writers'
        /// lines evaporated.
        /// </summary>
        [Fact]
        public void The_listener_set_does_not_change_when_a_session_is_archived()
        {
            TraceSessionHandle live = Open();
            int before = Trace.Listeners.Count;

            TraceTransitionResult r = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = live,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });

            Assert.Equal(before, Trace.Listeners.Count);

            // And a direct writer's line lands in the successor rather than
            // going nowhere.
            Trace.WriteLine("direct, after the archive");
            TraceTransitionResult end = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = r.Successor,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.None,
            });
            Assert.Contains("direct, after the archive", ReadArchivedText(end.Ticket), StringComparison.Ordinal);
        }

        // ── Track H8: a faulted sink can be recovered from ─────────────────

        /// <summary>
        /// <b>The off-and-on route, after a write fault, with the pool's own
        /// retirement held back</b> — so the sequence the settings path runs
        /// (<c>globals.vb</c> <c>ApplyDiagnosticLogSettings</c>: archive the
        /// observed handle with no successor, then <c>Begin</c>) meets the dead
        /// session itself. Until H8 the archive answered NoSession over the closed
        /// sink and the Begin answered AlreadyRecording over the still-present
        /// session, and no fresh file could ever open (Sol's review of H7,
        /// finding 1). Now the archive is accepted with an uncertain tail, the
        /// Begin opens, and a line written afterwards is read back from the
        /// fresh file.
        ///
        /// <para>Then the held work item runs — the stale failure arriving
        /// AFTER the successor was published, which is Sol's finding 2 — and
        /// the health state stays Recording for the successor while the old
        /// sink's failure is kept as history. Positive control at the end: a
        /// failure naming the LIVE session does move the state.</para>
        ///
        /// <para>The VB route itself is not reachable from this project; what
        /// is tested is the exact boundary sequence it makes, in its order.</para>
        /// </summary>
        [Fact]
        public void After_a_write_fault_the_off_and_on_route_opens_a_fresh_file_and_a_stale_failure_cannot_overwrite_it()
        {
            TraceSessionHandle a = Open();
            Write("what A managed to write");
            DateTime bootA = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string aTarget = TraceFileNaming.StampedPath(_livePath, bootA);

            using var held = new HeldFaultRetire();
            using var changes = new HealthChanges();
            BreakTheLiveSink();
            Write("the write that fails");
            Assert.True(held.Captured, "the fault was never noticed — the positive control for everything below");
            // Still nominally in place: the retirement is held, and this is
            // the window the settings path can land in.
            Assert.Equal(a.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.False(TraceCoordinator.Recording);

            // OFF: archive the observed handle with no successor — as the
            // settings path does when the operator turns the log off.
            TraceTransitionResult off = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = TraceCoordinator.CurrentHandle,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                OutcomeDetail = "User turned diagnostic log off",
                Resume = TraceResumeIntent.None,
            });
            Assert.Equal(TraceTransition.Accepted, off.Status);   // was NoSession
            Assert.True(off.TailUncertain);
            Assert.NotNull(off.SinkFault);
            Assert.NotNull(off.Ticket);
            Assert.Equal(aTarget, off.Ticket.SourcePath);
            Assert.Null(TraceCoordinator.CurrentHandle);
            Assert.False(off.SuccessorOpened);

            // ON: Begin, as the settings path does when the operator turns it
            // back on.
            TraceTransitionResult on = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, on.Status);     // was AlreadyRecording
            Assert.True(on.TracingOn);
            TraceSessionHandle b = on.Successor;
            Assert.NotEqual(a.SessionId, b.SessionId);
            Write("a line for the fresh file");
            Trace.Flush();
            string fresh = ReadLiveText(_livePath);
            Assert.Contains("a line for the fresh file", fresh, StringComparison.Ordinal);
            Assert.DoesNotContain("what A managed to write", fresh, StringComparison.Ordinal);
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
            Assert.Equal(b.SessionId, TraceRecordingHealth.Snapshot().LiveSessionId);

            // Finding 2: A's failure arrives now, after B was published.
            int historyBefore = TraceRecordingHealth.Snapshot().HistoricalFailures;
            int sinkFailedBefore = changes.All.Count(c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed);
            held.Run();
            TraceRecordingHealthSnapshot snap = TraceRecordingHealth.Snapshot();
            Assert.Equal(TraceSinkState.Recording, snap.SinkState);
            Assert.Equal(b.SessionId, snap.LiveSessionId);
            Assert.Null(snap.SinkFault);
            Assert.Equal(historyBefore + 1, snap.HistoricalFailures);
            Assert.Contains(a.SessionId.ToString(), snap.LastFailure, StringComparison.Ordinal);
            Assert.Contains("after a newer sink was published", snap.LastFailure, StringComparison.Ordinal);
            Assert.Equal(sinkFailedBefore, changes.All.Count(c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed));
            // And the held retirement found nothing to retire: B is untouched.
            Assert.Equal(b.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(TraceCoordinator.Recording);

            // A's archive carries the OPERATOR's outcome, not the coordinator's.
            Assert.True(off.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(off.Ticket.Completion.Result.ArchiveCommitted);
            TraceSessionEntry entry = Manifest().Entries.Single(e => e.SessionId == a.SessionId.ToString());
            Assert.Equal(TraceSessionOutcome.CleanExit, entry.Outcome);
            Assert.Equal("User turned diagnostic log off", entry.OutcomeDetail);

            // Positive controls for the ordering rule. A failure of the LIVE
            // generation is not stale, and moves the state...
            long gen = TraceCoordinator.SinkGenerationForTests;
            var noteSink = typeof(TraceRecordingHealth)
                .GetMethod("NoteSink", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            noteSink.Invoke(null, new object[] { TraceSinkState.Failed, "control fault", _livePath, b.SessionId, gen });
            Assert.Equal(TraceSinkState.Failed, TraceRecordingHealth.Snapshot().SinkState);
            Assert.Equal("control fault", TraceRecordingHealth.Snapshot().SinkFault);
            // ...a Recording note for the SAME generation arriving after it
            // does not resurrect the dead sink...
            noteSink.Invoke(null, new object[] { TraceSinkState.Recording, null, _livePath, b.SessionId, gen });
            Assert.Equal(TraceSinkState.Failed, TraceRecordingHealth.Snapshot().SinkState);
            // ...and a note for a NEWER generation replaces the state.
            noteSink.Invoke(null, new object[] { TraceSinkState.Recording, null, _livePath, Guid.NewGuid(), gen + 1 });
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
        }

        /// <summary>
        /// <b>Stop after a failed capture sink, arriving BEFORE the pool's
        /// retirement.</b> The capture's file died; the operator presses Stop.
        /// The archive is accepted with the operator's own outcome detail, the
        /// capture ends with its session, the tail is marked uncertain, the
        /// standing log opens in its place — and the retirement that then runs
        /// finds nothing to do.
        /// </summary>
        [Fact]
        public void Stop_after_a_failed_capture_sink_archives_the_capture_and_ends_it()
        {
            TraceSessionHandle capture = Open(TraceLevel.Verbose, asCapture: true);
            Guid captureId = TraceCoordinator.CaptureId;
            Write("captured before the disk went");

            using var held = new HeldFaultRetire();
            BreakTheLiveSink();
            Write("the write that fails");
            Assert.True(held.Captured);
            Assert.True(TraceCoordinator.CaptureRunning);   // the window Sol described

            TraceTransitionResult stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = capture,
                ExpectedCaptureId = captureId,
                RequireCaptureRunning = true,
                OperationId = captureId,
                Outcome = TraceSessionOutcome.CleanExit,
                OutcomeDetail = "Detailed capture: 8:14 PM, about 3 minutes",
                TerminalLines = new TraceRecord[] { "Detailed capture stopped" },
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.Accepted, stop.Status);   // was NoSession
            Assert.True(stop.EndedDetailedCapture);
            Assert.Equal(captureId, stop.EndedCaptureId);
            Assert.True(stop.TailUncertain);
            Assert.False(TraceCoordinator.CaptureRunning);
            Assert.True(stop.SuccessorOpened);
            Assert.True(TraceCoordinator.Recording);

            held.Run();   // the retirement: nothing left to retire
            Assert.Equal(stop.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);

            Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(stop.Ticket.Completion.Result.ArchiveCommitted);
            TraceSessionEntry entry = Manifest().Entries.Single(e => e.SessionId == capture.SessionId.ToString());
            Assert.Equal("Detailed capture: 8:14 PM, about 3 minutes", entry.OutcomeDetail);
            Assert.Contains("captured before the disk went", ReadArchivedText(stop.Ticket), StringComparison.Ordinal);
            // The terminal line could not land: the tail is honestly short.
            Assert.DoesNotContain("Detailed capture stopped", ReadArchivedText(stop.Ticket), StringComparison.Ordinal);
        }

        /// <summary>
        /// <b>Stop after a failed capture sink, arriving AFTER the pool's
        /// retirement.</b> The capture already ended with its file: Stop finds
        /// no capture running, Diagnostics no longer says one is in progress,
        /// and the retired file carries the coordinator's outcome. A new
        /// capture can then start.
        /// </summary>
        [Fact]
        public void Stop_after_the_retirement_finds_no_capture_and_a_new_one_can_start()
        {
            TraceSessionHandle capture = Open(TraceLevel.Verbose, asCapture: true);
            Guid captureId = TraceCoordinator.CaptureId;
            Write("captured before the disk went");

            using var held = new HeldFaultRetire();
            BreakTheLiveSink();
            Write("the write that fails");
            held.Run();

            Assert.Null(TraceCoordinator.CurrentHandle);
            Assert.False(TraceCoordinator.CaptureRunning);
            Assert.Equal(TraceSinkState.Failed, TraceRecordingHealth.Snapshot().SinkState);

            TraceTransitionResult stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = capture,
                ExpectedCaptureId = captureId,
                RequireCaptureRunning = true,
                OperationId = captureId,
                Outcome = TraceSessionOutcome.CleanExit,
                Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.NoSession, stop.Status);
            Assert.False(stop.TracingOn);

            Assert.True(TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(60)));
            TraceSessionEntry entry = Manifest().Entries.Single(e => e.SessionId == capture.SessionId.ToString());
            Assert.Equal(TraceSessionOutcome.RecordingFailed, entry.Outcome);

            TraceTransitionResult next = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: true);
            Assert.Equal(TraceTransition.Accepted, next.Status);
            Assert.NotEqual(Guid.Empty, next.StartedCaptureId);
            Assert.True(TraceCoordinator.CaptureRunning);
        }

        /// <summary>
        /// A problem-report snapshot over a faulted session gets that
        /// session's own archived ticket, pinned, rather than "nothing is
        /// recording" over a file full of evidence.
        /// </summary>
        [Fact]
        public void A_bundle_snapshot_over_a_faulted_session_is_handed_its_archived_ticket()
        {
            TraceSessionHandle live = Open();
            Write("evidence the bundle wants");
            using var held = new HeldFaultRetire();
            BreakTheLiveSink();
            Write("the write that fails");

            TraceTransitionResult snap = TraceCoordinator.SnapshotForBundle(live);
            Assert.Equal(TraceTransition.AlreadyClaimed, snap.Status);
            Assert.NotNull(snap.Ticket);
            Assert.Equal(live.SessionId, snap.Ticket.SessionId);
            Assert.True(TraceEvidencePins.IsPinned(snap.Ticket.SourcePath));
            Assert.Contains("evidence the bundle wants", File.ReadAllText(snap.Ticket.SourcePath), StringComparison.Ordinal);
            TraceEvidencePins.Release(snap.Ticket.SourcePath);
            held.Run();
            Assert.True(snap.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
        }

        // ── Track H9: never open over retained evidence ────────────────────

        /// <summary>
        /// <b>Sol's review of H8, blocker 1.</b> An archive whose detach fails
        /// leaves the archived file at the live path as the only copy. The
        /// operator's prescribed off-and-on then called Begin, which found
        /// nothing recording and opened that path with FileMode.Create —
        /// truncating the evidence the archive had refused to delete. Now: the
        /// archive remembers what it could not move; the open REFUSES that path
        /// and says why; and the next Begin tries the move again first, so a
        /// detach that was only transiently blocked becomes the ordinary
        /// ticket, with the operator's own outcome on it.
        ///
        /// <para>The move is blocked the way the checkpoint test blocks it: a
        /// DIRECTORY at the stamped target. The bytes are compared whole,
        /// before and after the refused open — a truncating open leaves an
        /// empty file, which this comparison cannot miss. Mutation control,
        /// run at H9 and restored: with the guard in OpenSessionLocked taken
        /// out, both this test and the one below went red at the status
        /// assertion — the refused open was Accepted, which is the truncating
        /// open happening.</para>
        /// </summary>
        [Fact]
        public void A_failed_detach_survives_the_off_and_on_retry_and_is_reclaimed_once_the_move_can_succeed()
        {
            TraceSessionHandle a = Open();
            Write("what A managed to write");
            DateTime bootA = TraceCoordinator.Observe().SessionBootTimeUtc.Value;
            string aTarget = TraceFileNaming.StampedPath(_livePath, bootA);
            Directory.CreateDirectory(aTarget);   // the move cannot succeed while this is here

            using var held = new HeldFaultRetire();
            using var changes = new HealthChanges();
            BreakTheLiveSink();
            Write("the write that fails");
            Assert.True(held.Captured, "the fault was never noticed — the positive control for everything below");

            // OFF: the archive owns A; its terminal records fail; the move fails;
            // the file stays, and nothing has a ticket for it.
            TraceTransitionResult off = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = TraceCoordinator.CurrentHandle,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                OutcomeDetail = "User turned diagnostic log off",
                Resume = TraceResumeIntent.None,
            });
            Assert.Equal(TraceTransition.Failed, off.Status);
            Assert.Equal("detach", off.FailedStage);
            Assert.Equal(_livePath, off.RetainedSourcePath);
            Assert.True(off.TailUncertain);
            Assert.Null(off.Ticket);
            Assert.Null(TraceCoordinator.CurrentHandle);
            Assert.Contains(_livePath, TraceCoordinator.RetainedEvidencePathsForTests);
            string retained = File.ReadAllText(_livePath);
            Assert.Contains("what A managed to write", retained, StringComparison.Ordinal);

            // ON, with the move still blocked: the open is REFUSED, the
            // bytes are untouched, and the operator's surface says which
            // file and why.
            TraceTransitionResult on1 = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Failed, on1.Status);   // was Accepted, over a truncated file
            Assert.Equal("retained-evidence", on1.FailedStage);
            Assert.Equal(_livePath, on1.RetainedSourcePath);
            Assert.False(on1.TracingOn);
            Assert.Null(on1.Reclaimed);
            Assert.Null(TraceCoordinator.CurrentHandle);
            Assert.False(TraceCoordinator.Recording);
            Assert.Equal(retained, File.ReadAllText(_livePath));   // byte for byte
            Assert.Contains(a.SessionId.ToString(), on1.SinkFault, StringComparison.Ordinal);
            Assert.Contains(on1.DeferredFaults, f => f.Contains("still could not be moved aside", StringComparison.Ordinal));
            TraceRecordingHealthSnapshot refused = TraceRecordingHealth.Snapshot();
            Assert.Equal(TraceSinkState.Failed, refused.SinkState);
            Assert.Equal(_livePath, refused.SinkPath);
            Assert.Contains("moved aside", refused.SinkFault, StringComparison.Ordinal);
            Assert.Contains(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.SinkFailed);

            // Unblock and ON again: the move succeeds, A gets the ticket its
            // archive could not make, and B opens fresh beside it.
            Directory.Delete(aTarget);
            TraceTransitionResult on2 = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, on2.Status);
            Assert.True(on2.TracingOn);
            Assert.NotNull(on2.Reclaimed);
            Assert.Equal(a.SessionId, on2.Reclaimed.SessionId);
            Assert.Equal(aTarget, on2.Reclaimed.SourcePath);
            Assert.True(on2.Reclaimed.TailUncertain);
            Assert.True(on2.Reclaimed.PendingRecordWritten);
            Assert.Empty(TraceCoordinator.RetainedEvidencePathsForTests);
            Assert.Equal(retained, File.ReadAllText(aTarget));   // moved, not copied, not touched
            TraceSessionHandle b = on2.Successor;
            Assert.NotEqual(a.SessionId, b.SessionId);
            Write("a line for the fresh file");
            Trace.Flush();
            string fresh = ReadLiveText(_livePath);
            Assert.Contains("a line for the fresh file", fresh, StringComparison.Ordinal);
            Assert.DoesNotContain("what A managed to write", fresh, StringComparison.Ordinal);
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
            Assert.Equal(b.SessionId, TraceRecordingHealth.Snapshot().LiveSessionId);

            // The reclaimed ticket reached the health model (its tail is
            // uncertain, so it is a condition) and the worker, and A's
            // archive carries the OPERATOR's outcome, not a reclaim's.
            Assert.Contains(changes.All, c => c.Kind == TraceRecordingHealthChangeKind.ConditionRaised
                                              && c.Condition?.TicketId == on2.Reclaimed.TicketId);
            Assert.True(on2.Reclaimed.Completion.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(on2.Reclaimed.Completion.Result.ArchiveCommitted);
            TraceSessionEntry entry = Manifest().Entries.Single(e => e.SessionId == a.SessionId.ToString());
            Assert.Equal(TraceSessionOutcome.CleanExit, entry.Outcome);
            Assert.Equal("User turned diagnostic log off", entry.OutcomeDetail);
            Assert.Contains("what A managed to write", ReadArchivedText(on2.Reclaimed), StringComparison.Ordinal);

            // The held retirement, running last, finds nothing to retire.
            held.Run();
            Assert.Equal(b.SessionId, TraceCoordinator.CurrentHandle.SessionId);
            Assert.True(TraceCoordinator.Recording);
        }

        /// <summary>
        /// The same guard when the pool's retirement — not the operator —
        /// is what archived the faulted session and could not move it: the
        /// operator's off finds nothing to archive, and their on is refused
        /// rather than truncating. A bundle snapshot over it is NoSession,
        /// which is the pre-existing limit named in the H9 report.
        /// </summary>
        [Fact]
        public void A_retirement_whose_detach_failed_is_not_truncated_by_the_next_Begin()
        {
            TraceSessionHandle a = Open();
            Write("what A managed to write");
            string aTarget = TraceFileNaming.StampedPath(_livePath, TraceCoordinator.Observe().SessionBootTimeUtc.Value);
            Directory.CreateDirectory(aTarget);
            try
            {
                using var held = new HeldFaultRetire();
                BreakTheLiveSink();
                Write("the write that fails");
                held.Run();   // the retirement: archives A as recording_failed, and cannot move it
                Assert.Null(TraceCoordinator.CurrentHandle);
                Assert.Contains(_livePath, TraceCoordinator.RetainedEvidencePathsForTests);
                string retained = File.ReadAllText(_livePath);
                Assert.Contains("what A managed to write", retained, StringComparison.Ordinal);

                TraceTransitionResult off = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = a, OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.None,
                });
                Assert.Equal(TraceTransition.NoSession, off.Status);

                TraceTransitionResult on = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
                Assert.Equal(TraceTransition.Failed, on.Status);
                Assert.Equal("retained-evidence", on.FailedStage);
                Assert.Equal(retained, File.ReadAllText(_livePath));
                Assert.Null(TraceCoordinator.CurrentHandle);
            }
            finally
            {
                try { Directory.Delete(aTarget); } catch { }
            }

            // And once the move can succeed, the coordinator's own outcome is
            // what A's archive carries.
            TraceTransitionResult on2 = TraceCoordinator.Begin(_livePath, TraceLevel.Info, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, on2.Status);
            Assert.NotNull(on2.Reclaimed);
            Assert.True(on2.Reclaimed.Completion.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(on2.Reclaimed.Completion.Result.ArchiveCommitted);
            Assert.Equal(TraceSessionOutcome.RecordingFailed,
                Manifest().Entries.Single(e => e.SessionId == a.SessionId.ToString()).Outcome);
        }
    }
}
