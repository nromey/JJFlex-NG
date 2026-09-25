using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using JJTrace;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The drop window's "what happens next is being kept" is decided by the
    /// coordinator's state at the moment the notice is built, not by the seal
    /// (Sol's review of H9, blocker 1). Driven through the REAL coordinator
    /// and the REAL seal worker, with the archive worker held on a barrier so
    /// the world can be changed in the five-minute gap the production hook
    /// waits through: the operator turns the log off, or the fresh file
    /// fails. No window, no radio.
    /// </summary>
    /// <remarks>
    /// <para>The seal hook here mirrors <c>globals.vb</c>'s
    /// <c>SealCaptureForConnectionDrop</c> in the one respect that matters:
    /// it seals through <c>TrySeal</c>, then WAITS for the archive before
    /// returning — which is where the operator gets their chance. A hook that
    /// returned at once could not reproduce the blocker.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs),
    // which is what makes the process-wide coordinator, seal hook and queue
    // safe to drive here.
    public sealed class DropNoticeStateTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;
        private readonly string _livePath;
        private readonly string _savedArchiveRoot;
        private readonly TraceSession _savedSession;
        private readonly bool _savedOn;
        private readonly TraceLevel _savedLevel;
        private readonly Func<CaptureSealRequest, CaptureSealResult> _savedHook;
        private readonly Action<Action> _savedQueue;

        public DropNoticeStateTests(ITestOutputHelper output)
        {
            _out = output;
            _savedLevel = Tracing.TheSwitch.Level;
            _savedOn = Tracing.On;
            _savedArchiveRoot = TraceCoordinator.ArchiveRootDir;
            _savedSession = TraceSessionContext.Current;
            _savedHook = CaptureSeal.SealHook;
            _savedQueue = CaptureSeal.Queue;

            _dir = Path.Combine(Path.GetTempPath(), "jjflex-h10-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _livePath = Path.Combine(_dir, "JJFlexRadioTrace.txt");

            Tracing.TheSwitch.Level = TraceLevel.Verbose;
            TraceCoordinator.ArchiveRootDir = Path.Combine(_dir, "Traces");
            TraceCoordinator.AppIdentity = new TraceEnvironment
            {
                Instance = 1, AppVersion = "0.0-test", AppPath = @"C:\test\jjflexible.exe",
            };
            RestoreSession(null);
            TraceArchiveWorker.BeforeArchiveForTests = null;
            TraceRecordingHealth.ResetForTests();
            // Both claim spaces, together. The drop's operation id is built
            // from the connection token's ordinal, and forgetting the tokens
            // restarts the ordinals — so a coordinator still holding an
            // earlier test's ticket under that id would answer this test's
            // seal with AlreadyClaimed and somebody else's session.
            CaptureSeal.ForgetClaimForTests();
            TraceCoordinator.ResetClaimsForTests();
            Tracing.On = true;
        }

        public void Dispose()
        {
            TraceArchiveWorker.BeforeArchiveForTests = null;
            CaptureSeal.SealHook = _savedHook;
            CaptureSeal.Queue = _savedQueue;
            CaptureSeal.ForgetClaimForTests();
            if (TraceCoordinator.CurrentHandle != null)
            {
                TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    ShutdownAuthority = true,
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.None,
                    OperationId = Guid.NewGuid(),
                });
            }
            TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
            TraceCoordinator.ArchiveRootDir = _savedArchiveRoot;
            TraceCoordinator.SetStandingIntent(true, TraceLevel.Info);
            TraceRecordingHealth.ResetForTests();
            RestoreSession(_savedSession);
            Tracing.TheSwitch.Level = _savedLevel;
            Tracing.On = _savedOn;
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static void RestoreSession(TraceSession session) =>
            typeof(TraceCoordinator)
                .GetMethod("RestoreSessionForTests", System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { session });

        /// <summary>
        /// Break the live sink's writer so its next write throws — the
        /// deterministic stand-in for a disk that stops accepting bytes
        /// (the same technique as <c>TraceCoordinatorTests</c>).
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
        /// One drop, end to end: a standing session is recording; the radio's
        /// connection falls; the seal hook seals it and waits for its archive,
        /// which is held on a barrier; <paramref name="duringTheWait"/> runs
        /// on the test thread while it is held; the barrier lifts; the notice
        /// the operator would be shown is returned.
        /// </summary>
        private CaptureSealNotice OneDrop(Action duringTheWait)
        {
            TraceCoordinator.SetStandingIntent(true, TraceLevel.Verbose);
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);
            TraceSessionHandle old = began.Successor;

            using var atWorker = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var told = new ManualResetEventSlim(false);
            CaptureSealNotice notice = null;
            TraceTransitionResult sealResult = null;
            Thread worker = null;

            TraceArchiveWorker.BeforeArchiveForTests = t =>
            {
                if (t.SessionId != old.SessionId) return;
                atWorker.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            CaptureSeal.SealHook = req =>
            {
                sealResult = TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    Expected = (TraceSessionHandle)req.ExpectedSession,
                    OperationId = req.DropOperationId,
                    Outcome = TraceSessionOutcome.ConnectionDropped,
                    OutcomeDetail = req.OutcomeDetail,
                    Resume = TraceResumeIntent.Standing,
                });
                var outcome = new CaptureSealResult
                {
                    Refused = !sealResult.Owned,
                    RefusalReason = sealResult.Owned ? null : sealResult.Explanation,
                    ArchivedSessionId = sealResult.Ticket?.SessionId,
                    SuccessorOpened = sealResult.SuccessorOpened,
                    SuccessorRecording = sealResult.TracingOn,
                    TailUncertain = sealResult.TailUncertain,
                    SinkFailedBeforeDrop = sealResult.SinkFailedBeforeSeal,
                };
                // As globals.vb does: the operator is only ever offered a path
                // that exists, so wait for the archive here, on the worker.
                if (sealResult.Owned && sealResult.Ticket?.Completion != null
                    && sealResult.Ticket.Completion.Wait(TimeSpan.FromSeconds(60))
                    && sealResult.Ticket.Completion.Result.ArchiveCommitted)
                {
                    outcome.ArchivePath = sealResult.Ticket.Completion.Result.ArchiveFullPath;
                }
                return outcome;
            };
            // A real background thread, so the test thread is free to be the
            // operator while the worker waits.
            CaptureSeal.Queue = work => { worker = new Thread(() => work()) { IsBackground = true }; worker.Start(); };
            void OnSealed(CaptureSealNotice n) { notice = n; told.Set(); }
            CaptureSeal.SealedAfterDrop += OnSealed;
            try
            {
                CaptureSeal.AfterConnectionDrop(new object(), "6300inshack", null, old);
                Assert.True(atWorker.Wait(TimeSpan.FromSeconds(10)),
                    "the archive worker never reached the old session's ticket; seal: "
                    + (sealResult == null ? "hook not called" : sealResult.Status + " — " + sealResult.Explanation)
                    + "; worker alive: " + (worker?.IsAlive));
                // Positive control on the seal itself: it owned the session
                // and a successor is recording at this moment.
                Assert.NotNull(sealResult);
                Assert.True(sealResult.Owned);
                Assert.True(sealResult.SuccessorOpened, "no successor opened, so nothing can change during the wait");
                Assert.True(TraceCoordinator.Observe().Recording, "the successor is not recording at the seal");

                duringTheWait();

                release.Set();
                Assert.True(told.Wait(TimeSpan.FromSeconds(60)), "the operator was never told");
                Assert.NotNull(notice);
                Assert.False(string.IsNullOrEmpty(notice.ArchivePath));
                Assert.True(File.Exists(notice.ArchivePath), "the window names a path that does not exist");
            }
            finally
            {
                release.Set();
                CaptureSeal.SealedAfterDrop -= OnSealed;
                TraceArchiveWorker.BeforeArchiveForTests = null;
                worker?.Join(TimeSpan.FromSeconds(30));
            }
            _out.WriteLine(notice.Explanation);
            _out.WriteLine(string.Empty);
            return notice;
        }

        /// <summary>
        /// <b>Sol's review of H9, blocker 1.</b> The operator turns the
        /// standing log off in Settings while the drop's archive is being
        /// made. The seal DID open a successor; by the time the window opens
        /// nothing is recording; and the window says so rather than "the
        /// next thing that happens is being kept too".
        /// </summary>
        [Fact]
        public void The_window_says_what_is_being_kept_when_it_opens_not_when_the_seal_happened()
        {
            CaptureSealNotice notice = OneDrop(duringTheWait: () =>
            {
                // Exactly what ApplyDiagnosticLogSettings does for "off".
                TraceSessionHandle successor = TraceCoordinator.CurrentHandle;
                Assert.NotNull(successor);
                TraceTransitionResult off = TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    Expected = successor,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    OutcomeDetail = "User turned diagnostic log off",
                    Resume = TraceResumeIntent.None,
                });
                Assert.Equal(TraceTransition.Accepted, off.Status);
                Assert.False(TraceCoordinator.Observe().Recording);
            });

            Assert.True(notice.SuccessorOpened, "the seal's own fact is kept: a successor did open");
            Assert.False(notice.RecordingNow);
            string text = notice.Explanation;
            Assert.DoesNotContain("has already started recording again", text, StringComparison.Ordinal);
            Assert.DoesNotContain("is being kept too", text, StringComparison.Ordinal);
            Assert.DoesNotContain("has not started recording again", text, StringComparison.Ordinal);
            Assert.Contains("did start recording again after the connection went, but it is not recording now", text, StringComparison.Ordinal);
            Assert.Contains("what happens next is not being kept", text, StringComparison.Ordinal);
            Assert.Contains("Control J then Control R", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// The other way the successor can stop during the wait: its own
        /// file fails a write and the coordinator retires it with nothing in
        /// its place. Same answer — nothing is being kept, and the window
        /// does not claim otherwise.
        /// </summary>
        [Fact]
        public void A_successor_that_fails_during_the_archive_wait_is_not_called_a_running_log()
        {
            CaptureSealNotice notice = OneDrop(duringTheWait: () =>
            {
                BreakTheLiveSink();
                Tracing.TraceLine("the write that fails", TraceLevel.Warning);
                Assert.True(SpinWait.SpinUntil(() => TraceCoordinator.CurrentHandle == null, TimeSpan.FromSeconds(10)),
                            "the faulted successor was never retired");
                Assert.False(TraceCoordinator.Observe().Recording);
            });

            Assert.True(notice.SuccessorOpened);
            Assert.False(notice.RecordingNow);
            Assert.DoesNotContain("is being kept too", notice.Explanation, StringComparison.Ordinal);
            Assert.Contains("but it is not recording now", notice.Explanation, StringComparison.Ordinal);
        }

        /// <summary>
        /// Positive control for the instrument: the same drop with nothing
        /// changed during the wait says recording has started again and the
        /// next thing is being kept — so the two tests above are red for the
        /// reason they claim and not because the sentence is never chosen.
        /// </summary>
        [Fact]
        public void A_successor_still_recording_when_the_window_opens_is_said_to_be_keeping_what_happens_next()
        {
            CaptureSealNotice notice = OneDrop(duringTheWait: () =>
            {
                Assert.True(TraceCoordinator.Observe().Recording);
            });

            Assert.True(notice.SuccessorOpened);
            Assert.True(notice.RecordingNow);
            Assert.Contains("has already started recording again, so the next thing that happens is being kept too",
                            notice.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("not recording now", notice.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("Control J then Control R", notice.Explanation, StringComparison.Ordinal);
        }
    }
}
