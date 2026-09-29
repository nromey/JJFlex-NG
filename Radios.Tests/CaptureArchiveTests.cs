using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using JJTrace;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A radio-side connection loss archives the running capture as
    /// <c>connection_dropped</c>, the operator's own disconnect does not, and
    /// the session that gets archived is the one that was recording AT THE DROP
    /// (#566's bridge, Sprint 45 Tracks H, H2 and H3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap.</b> <c>TraceSessionOutcome.ConnectionDropped</c> has existed
    /// since Sprint 29 and no archive on this machine has ever carried it — 231
    /// zips, counted 2026-09-22, all <c>clean_exit</c>, <c>killed</c>,
    /// <c>no_radios</c> or <c>slice_unavailable</c>. Nothing on the drop path
    /// archived anything, so a radio dying mid-transmit left the session open
    /// and the app archived the whole evening <c>clean_exit</c> hours later. A
    /// radio death and a quiet evening produced the same file.
    /// </para>
    /// <para>
    /// <b>The positive control is the important half.</b> Archiving on a
    /// deliberate disconnect would be worse than not archiving at all: it would
    /// fill the archive with the word and destroy the one query the outcome
    /// exists to answer.
    /// </para>
    /// <para>
    /// <b>And the lifecycle tests below are the ones the first build could not
    /// have.</b> Its thirty-seven tests were all helper tests, and the defect
    /// was not in a helper: the archive was queued with nothing but a radio name
    /// and resolved the process-wide current session whenever the worker
    /// happened to run. A test that cannot decide when the worker wakes cannot
    /// see that at all. <see cref="CaptureArchive.Queue"/> exists so these can.
    /// </para>
    /// </remarks>
    // No collection attribute needed: the suite runs sequentially by assembly
    // policy (see TestParallelism.cs), which is what makes the process-wide
    // ArchiveHook, queue and claim safe to drive here.
    public sealed class CaptureArchiveTests : IDisposable
    {
        private readonly Func<CaptureArchiveRequest, CaptureArchiveResult> _savedHook;
        private readonly Action<Action> _savedQueue;
        private readonly TraceSession _savedSession;

        public CaptureArchiveTests()
        {
            _savedHook = CaptureArchive.ArchiveHook;
            _savedQueue = CaptureArchive.Queue;
            _savedSession = TraceSessionContext.Current;
            CaptureArchive.ForgetClaimForTests();
        }

        public void Dispose()
        {
            CaptureArchive.ArchiveHook = _savedHook;
            CaptureArchive.Queue = _savedQueue;
            CaptureArchive.ForgetClaimForTests();
            RestoreSession(_savedSession);
        }

        /// <summary>
        /// Put the real trace session pointer back the way it was. These tests
        /// drive the genuine process-wide pointer, because the defect they exist
        /// for lives in exactly that pointer being replaceable from any thread.
        /// </summary>
        private static void RestoreSession(TraceSession session)
        {
            // Set the pointer rather than calling EndSession, which would stamp
            // an end time on a session this test did not open. The coordinator
            // owns the pointer now and exposes a test-only setter, so this no
            // longer reaches through reflection into a private field.
            typeof(TraceCoordinator)
                .GetMethod("RestoreSessionForTests", System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { session });
        }

        private static CaptureArchiveResult Archived(string path) =>
            new CaptureArchiveResult { ArchivePath = path, SuccessorOpened = true };

        /// <summary>A stand-in for the FlexLib <c>Radio</c> object a removal
        /// carries. Only its identity matters, which is the point.</summary>
        private sealed class RemovedRadio
        {
            public RemovedRadio(string name) { Name = name; }
            public string Name { get; }
        }

        /// <summary>Hold the queued work instead of running it, so the test
        /// decides what happens to the session in between.</summary>
        private sealed class HeldQueue
        {
            private readonly List<Action> _work = new List<Action>();
            public void Take(Action w) { _work.Add(w); }
            public int Count => _work.Count;
            public void RunAll() { foreach (Action w in _work) w(); _work.Clear(); }
        }

        // ────────────────────────────────────────────────────────────────
        //  Which removals archive, and which must not
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Only_our_radio_dropping_archives_the_capture()
        {
            Assert.True(FlexBase.RemovalArchivesTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio));

            // The operator hung up. Their own disconnect is a normal end to a
            // session and the manifest already has the vocabulary for it.
            Assert.False(FlexBase.RemovalArchivesTheCapture(
                FlexBase.RadioRemovalKind.SelfInitiated));

            // Some other radio aged out of discovery. Says nothing whatever
            // about the session we are recording.
            Assert.False(FlexBase.RemovalArchivesTheCapture(
                FlexBase.RadioRemovalKind.DiscoveryLoss));
        }

        /// <summary>
        /// <b>This test used to pin the defect.</b> Until Track H5 it asserted
        /// that the archive sat inside <c>apiRadioRemovedHandler</c>'s
        /// <c>ConnectionLostOurRadio</c> arm — the arm FlexLib never reaches for
        /// a radio known only through SmartLink, because <c>API.RemoveRadio</c>
        /// raises nothing for a serial outside its LAN discovery dictionary. It
        /// was green on every run and on the bench, because the 8600 is on the
        /// LAN as well.
        ///
        /// <para>The truth table still decides. What moved is the signal it is
        /// consulted on: our Radio's <c>Connected</c> property falling, which
        /// FlexLib raises on every path. <c>ConnectionFallArchivesTheCapture</c>
        /// answers through <c>RemovalArchivesTheCapture</c>, so a hang-up is still
        /// ours and still archives nothing.</para>
        /// </summary>
        [Fact]
        public void The_shipped_archive_is_gated_by_the_truth_table_and_carries_the_radio_itself()
        {
            // A truth table nothing consults is a truth table that is wrong for
            // free. This pins the call site.
            string source = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));
            string archiveMethod = ArchiveMethodBody(source);

            Assert.Contains("ClassifyRadioRemoval(", archiveMethod, StringComparison.Ordinal);
            Assert.Contains("ConnectionFallArchivesTheCapture(kind,", archiveMethod, StringComparison.Ordinal);
            // Track H6: a firmware restart is our flag AND FlexLib's confirmation.
            Assert.Contains("=> RemovalArchivesTheCapture(kind) && !(firmwareUpdateSent && radioUpdating);", source, StringComparison.Ordinal);

            // Neither removal arm asks for an archive any more.
            int drop = source.IndexOf("case RadioRemovalKind.ConnectionLostOurRadio:", StringComparison.Ordinal);
            int selfCase = source.IndexOf("case RadioRemovalKind.SelfInitiated:", StringComparison.Ordinal);
            Assert.True(drop > 0 && selfCase > 0);
            string dropArm = source.Substring(drop, source.IndexOf("default:", drop, StringComparison.Ordinal) - drop);
            string selfArm = source.Substring(selfCase, drop - selfCase);
            Assert.DoesNotContain("CaptureArchive.AfterConnectionDrop", dropArm, StringComparison.Ordinal);
            Assert.DoesNotContain("CaptureArchive.AfterConnectionDrop", selfArm, StringComparison.Ordinal);

            // And the fallen radio's own object is what identifies the drop, not
            // just its nickname — its connection lifetime is what makes two
            // notices one drop.
            Assert.Contains("CaptureArchive.AfterConnectionDrop(\r\n                token,", archiveMethod,
                            StringComparison.Ordinal);
        }

        /// <summary>The body of the one method that takes the archive.</summary>
        internal static string ArchiveMethodBody(string flexBaseSource)
        {
            int at = flexBaseSource.IndexOf("private void archiveIfOurConnectionDropped(Radio r, ConnectionLifetime.Token token, JJTrace.TraceSessionHandle fall)", StringComparison.Ordinal);
            Assert.True(at > 0, "archiveIfOurConnectionDropped is gone");
            int end = flexBaseSource.IndexOf("private void wireProducerHandlers(", at, StringComparison.Ordinal);
            Assert.True(end > at, "the member after archiveIfOurConnectionDropped moved");
            return flexBaseSource.Substring(at, end - at);
        }

        /// <summary>
        /// #618, pinned at the call site: the meter window is handed over as a
        /// FUNCTION, so it can only be rendered after the claim is won.
        /// </summary>
        [Fact]
        public void The_drop_arm_hands_the_flush_over_as_a_function_rather_than_writing_it()
        {
            string source = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));
            // The archive moved from the removal arm to the connection's fall in
            // Track H5; the #618 shape moved with it.
            string dropArm = ArchiveMethodBody(source);

            // A lambda passed to AfterConnectionDrop, not a statement before it.
            Assert.Contains("() => collectCaptureMeterFlush(", dropArm, StringComparison.Ordinal);

            // And the old shape is gone: nothing on this arm renders the window
            // before the claim, and nothing writes it globally.
            Assert.DoesNotContain("flushCaptureMeters(", dropArm, StringComparison.Ordinal);

            // The collector itself must not trace — its whole job is to return
            // a record for the boundary to place (a TraceRecord since H16, so
            // the line travels with its writer's kind, #625).
            string meters = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.CaptureMeters.cs"));
            int collector = meters.IndexOf("internal TraceRecord collectCaptureMeterFlush(", StringComparison.Ordinal);
            Assert.True(collector > 0, "collectCaptureMeterFlush is gone");
            string body = meters.Substring(collector);
            int firstBrace = body.IndexOf('{');
            int end = body.IndexOf("private SupplyVoltage readSupplyVoltage", StringComparison.Ordinal);
            string collectorBody = end > firstBrace ? body.Substring(firstBrace, end - firstBrace) : body;
            Assert.DoesNotContain("Tracing.TraceLine(line", collectorBody, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  The archive itself
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_drop_archives_once_and_hands_back_where_it_landed()
        {
            using var archived_ = new ManualResetEventSlim(false);
            using var told = new ManualResetEventSlim(false);
            string detailSeen = null;
            Guid sessionSeen = Guid.Empty;
            CaptureArchiveNotice notice = null;

            TraceSession session = TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = req =>
            {
                sessionSeen = ((TraceSessionHandle)req.ExpectedSession).SessionId;
                detailSeen = req.OutcomeDetail;
                archived_.Set();
                return Archived(@"C:\Traces\2026\09\trace-20260922-201500-connection_dropped.zip");
            };

            void OnArchived(CaptureArchiveNotice n) { notice = n; told.Set(); }
            CaptureArchive.ArchivedAfterDrop += OnArchived;
            try
            {
                CaptureArchive.AfterConnectionDrop(new RemovedRadio("6300inshack"), "6300inshack");
                Assert.True(archived_.Wait(TimeSpan.FromSeconds(5)), "the archive hook was never called");
                Assert.True(told.Wait(TimeSpan.FromSeconds(5)), "nobody was told where it landed");
            }
            finally
            {
                CaptureArchive.ArchivedAfterDrop -= OnArchived;
            }

            // The hook is told WHICH session to archive. Without that it resolved
            // the process-wide current one at whatever moment it ran.
            Assert.Equal(session.SessionId, sessionSeen);

            // The detail is what the trace browser prints in its list, so it is
            // a sentence a person reads, not a code.
            Assert.Equal(
                "The connection to 6300inshack dropped while this session was running",
                detailSeen);
            Assert.NotNull(notice);
            Assert.Equal("6300inshack", notice.RadioName);
        }

        [Fact]
        public void The_notice_carries_whatever_path_the_archive_produced()
        {
            // NOT "a name ending connection_dropped.zip". A session that
            // rotated froze its part filename tag at the first part, so the
            // archived file can read unknown-part-3 while the manifest inside it
            // correctly says connection_dropped (Codex's review, bench
            // predictions). Anything that promises the operator a filename is
            // wrong; the notice hands over the path the archive actually
            // returned, whatever it is called.
            string rotated = @"C:\Traces\2026\09\trace-20260922-201500-unknown-part-003.zip";
            using var told = new ManualResetEventSlim(false);
            CaptureArchiveNotice notice = null;

            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = _ => Archived(rotated);
            void OnArchived(CaptureArchiveNotice n) { notice = n; told.Set(); }
            CaptureArchive.ArchivedAfterDrop += OnArchived;
            try
            {
                CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
                Assert.True(told.Wait(TimeSpan.FromSeconds(5)));
            }
            finally { CaptureArchive.ArchivedAfterDrop -= OnArchived; }

            Assert.Equal(rotated, notice.ArchivePath);
            Assert.Equal(rotated, notice.ClipboardText);
        }

        /// <summary>
        /// The notice carries whether a successor really opened. A drop that
        /// wins during a teardown archives its own session and opens nothing, and
        /// the facts have to say so even while the prose still promises a
        /// restart — that sentence is Noel's to rule.
        /// </summary>
        [Fact]
        public void The_notice_says_whether_recording_actually_resumed()
        {
            using var told = new ManualResetEventSlim(false);
            CaptureArchiveNotice notice = null;

            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = _ => new CaptureArchiveResult
            {
                ArchivePath = @"C:\Traces\one.zip",
                SuccessorOpened = false,
                ArchivedSessionId = Guid.NewGuid(),
            };
            void OnArchived(CaptureArchiveNotice n) { notice = n; told.Set(); }
            CaptureArchive.ArchivedAfterDrop += OnArchived;
            try
            {
                CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
                Assert.True(told.Wait(TimeSpan.FromSeconds(5)));
            }
            finally { CaptureArchive.ArchivedAfterDrop -= OnArchived; }

            Assert.False(notice.SuccessorOpened);
            Assert.NotNull(notice.ArchivedSessionId);
        }

        [Fact]
        public void An_unnamed_radio_still_gets_a_sentence()
        {
            Assert.Equal("The radio's connection dropped while this session was running",
                CaptureArchive.OutcomeDetail(""));
            Assert.Equal("The radio's connection dropped while this session was running",
                CaptureArchive.OutcomeDetail(null));
        }

        [Fact]
        public void With_no_hook_installed_nothing_happens_and_the_drop_is_not_consumed()
        {
            // A missing hook is a wiring defect, and the symptom — a capture
            // that quietly says clean_exit — looks exactly like the bug this
            // class fixes. It must not also silently consume the drop, or a
            // later correctly-wired call for the same drop would be refused.
            //
            // The first build's test for this asserted the opposite of what its
            // own comment said — it checked that the archive HAD been spent — so
            // the suite was pinning the behaviour as desired.
            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = null;
            var radio = new RemovedRadio("A");
            CaptureArchive.AfterConnectionDrop(radio, "A");

            int calls = 0;
            using var gate = new ManualResetEventSlim(false);
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref calls);
                gate.Set();
                return Archived(@"C:\Traces\one.zip");
            };
            CaptureArchive.AfterConnectionDrop(radio, "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, Volatile.Read(ref calls));
        }

        [Fact]
        public void Nobody_is_told_when_no_archive_was_produced()
        {
            // A window offering a path that does not exist is worse than no
            // window.
            bool told = false;
            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = _ => new CaptureArchiveResult();
            void OnArchived(CaptureArchiveNotice _) { told = true; }
            CaptureArchive.ArchivedAfterDrop += OnArchived;
            try
            {
                CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
                Thread.Sleep(300);
            }
            finally
            {
                CaptureArchive.ArchivedAfterDrop -= OnArchived;
            }
            Assert.False(told);
        }

        [Fact]
        public void A_hook_that_throws_does_not_take_the_drop_path_down_with_it()
        {
            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = _ => throw new InvalidOperationException("disk busy");
            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");   // must not throw here
            Thread.Sleep(200);
        }

        [Fact]
        public void Nothing_recording_means_nothing_to_archive()
        {
            // Not a failure and not silence. The hook is never called, because
            // there is no session for it to archive.
            RestoreSession(null);
            int calls = 0;
            CaptureArchive.ArchiveHook = _ => { Interlocked.Increment(ref calls); return new CaptureArchiveResult(); };
            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Thread.Sleep(200);
            Assert.Equal(0, Volatile.Read(ref calls));
        }

        // ────────────────────────────────────────────────────────────────
        //  #618 — claim first, THEN collect
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The winning removal renders its meter window and the line travels to
        /// the boundary as data. It is never written through a global trace
        /// call, so it cannot land in a successor's file.
        /// </summary>
        [Fact]
        public void The_accepted_drop_collects_its_meter_window_and_carries_it_to_the_archive()
        {
            string seen = null;
            using var gate = new ManualResetEventSlim(false);
            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = req =>
            {
                seen = req.PartialMeterLine?.Text;
                gate.Set();
                return Archived(@"C:\Traces\one.zip");
            };

            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A",
                () => "captureMeters: paTemp none n=0 partial=connection_dropped");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal("captureMeters: paTemp none n=0 partial=connection_dropped", seen);
        }

        /// <summary>
        /// #618 exactly. A repeat notice for the same connection is refused —
        /// and the collector is never even CALLED, so no
        /// <c>partial=connection_dropped</c> record exists to be written
        /// anywhere, let alone into the fresh standing log the first archive
        /// started.
        /// </summary>
        [Fact]
        public void A_refused_duplicate_never_renders_a_drop_line_at_all()
        {
            int collected = 0;
            int calls = 0;
            using var first = new ManualResetEventSlim(false);
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref calls);
                first.Set();
                return Archived(@"C:\Traces\one.zip");
            };

            var radio = new RemovedRadio("A");
            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(radio, "A", () => { Interlocked.Increment(ref collected); return "line"; });
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, Volatile.Read(ref collected));

            // What the archive itself does next: archive, then restart the log.
            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();

            // FlexLib raises the removal again, carrying the same object.
            CaptureArchive.AfterConnectionDrop(radio, "A", () => { Interlocked.Increment(ref collected); return "line"; });
            Thread.Sleep(250);

            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(1, Volatile.Read(ref collected));
        }

        [Fact]
        public void A_collector_that_throws_does_not_stop_the_archive()
        {
            using var gate = new ManualResetEventSlim(false);
            string seen = "not null";
            TraceSessionContext.BeginSession();
            CaptureArchive.ArchiveHook = req => { seen = req.PartialMeterLine?.Text; gate.Set(); return Archived(@"C:\one.zip"); };
            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A",
                () => throw new InvalidOperationException("meter lock wedged"));
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(seen);
        }

        // ────────────────────────────────────────────────────────────────
        //  The lifecycle race — the central merge blocker
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The defect, exactly: the worker used to resolve the process-wide
        /// current session when it ran. A Stop, another capture, a log toggle
        /// or an exit in between made it archive a different session under the
        /// word <c>connection_dropped</c>, and nothing said so — the failure
        /// looked like an ordinary evening, which is the symptom this whole
        /// bridge exists to end.
        ///
        /// <para>The comparison now lives INSIDE the trace boundary, so what
        /// this proves is that the expected handle really is carried from the
        /// removal to the hook: the hook is still called, and the session id it
        /// is handed is the OLD one, never the replacement's.</para>
        /// </summary>
        [Fact]
        public void A_worker_that_wakes_after_a_replacement_still_names_the_old_session()
        {
            var held = new HeldQueue();
            CaptureArchive.Queue = held.Take;

            Guid asked = Guid.Empty;
            CaptureArchive.ArchiveHook = req =>
            {
                asked = ((TraceSessionHandle)req.ExpectedSession).SessionId;
                // What the real boundary does with a handle that is no longer
                // current: refuse, with no lifecycle effect at all.
                return new CaptureArchiveResult { Refused = true, RefusalReason = "NotCurrent" };
            };

            TraceSession original = TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Assert.Equal(1, held.Count);

            // Everything that can happen between the drop and the worker:
            // the operator stops the capture, and something starts another.
            TraceSessionContext.EndSession();
            TraceSession replacement = TraceSessionContext.BeginSession();

            held.RunAll();

            Assert.Equal(original.SessionId, asked);
            Assert.NotEqual(replacement.SessionId, asked);
        }

        [Fact]
        public void A_worker_whose_session_is_still_recording_archives_it()
        {
            // The positive control for the test above: a guard that refuses
            // everything proves nothing at all.
            var held = new HeldQueue();
            CaptureArchive.Queue = held.Take;

            Guid archived_ = Guid.Empty;
            CaptureArchive.ArchiveHook = req =>
            {
                archived_ = ((TraceSessionHandle)req.ExpectedSession).SessionId;
                return Archived(@"C:\Traces\right.zip");
            };

            TraceSession session = TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
            held.RunAll();

            Assert.Equal(session.SessionId, archived_);
        }

        // ────────────────────────────────────────────────────────────────
        //  One archive per CONNECTION, not per session bit and not per timer
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The other half of the merge blocker. The archive restarts the standing
        /// log; restarting a log begins a session; and beginning a session used
        /// to re-arm the one-bit guard. So the guard disarmed itself, on the
        /// archive's own path, in time for the duplicate removal it existed to
        /// refuse — and the second notice archived the fresh, nearly empty log
        /// and put a second window in front of an operator whose radio had just
        /// died.
        /// </summary>
        [Fact]
        public void A_repeat_notice_from_the_same_drop_does_not_archive_the_fresh_log()
        {
            int calls = 0;
            using var first = new ManualResetEventSlim(false);
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref calls);
                first.Set();
                return Archived(@"C:\Traces\one.zip");
            };

            var radio = new RemovedRadio("A");
            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(radio, "A");
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));

            // What the archive itself does next: archive, then restart the log.
            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();

            // FlexLib raises the removal again, carrying the same object.
            CaptureArchive.AfterConnectionDrop(radio, "A");
            Thread.Sleep(250);
            Assert.Equal(1, Volatile.Read(ref calls));
        }

        /// <summary>
        /// <b>The sixty-second bound is gone, and this is what replaced it.</b>
        /// H2 let a same-object claim lapse after a minute, because nothing had
        /// verified whether FlexLib hands back a new object after a reconnect —
        /// so a delayed duplicate arriving later archived a fresh log and
        /// announced a second drop. (The subtraction it used could not even
        /// enforce a true minute across a signed <c>TickCount</c> half-wrap.)
        /// The claim now belongs to the CONNECTION and is terminal, so time
        /// plays no part at all.
        /// </summary>
        [Fact]
        public void A_duplicate_arriving_long_after_the_drop_is_still_refused()
        {
            int calls = 0;
            using var first = new ManualResetEventSlim(false);
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref calls);
                first.Set();
                return Archived(@"C:\Traces\one.zip");
            };

            var radio = new RemovedRadio("A");
            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(radio, "A");
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));

            // Two hours of evening, in one line. A wall-clock test would have
            // to sleep; a lifetime test does not have to be told what time it
            // is, which is the whole improvement.
            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();

            CaptureArchive.AfterConnectionDrop(radio, "A");
            Thread.Sleep(250);
            Assert.Equal(1, Volatile.Read(ref calls));
        }

        /// <summary>
        /// A, B, delayed-A. The claim is kept PER TOKEN rather than as one
        /// process-global "most recent object", so B's arrival cannot make A's
        /// late duplicate look new again.
        /// </summary>
        [Fact]
        public void A_then_B_then_a_late_A_does_not_archive_a_third_time()
        {
            var archivedFor = new List<string>();
            using var gate = new ManualResetEventSlim(false);
            CaptureArchive.ArchiveHook = req =>
            {
                lock (archivedFor) { archivedFor.Add(req.OutcomeDetail); }
                gate.Set();
                return Archived(@"C:\Traces\one.zip");
            };

            var a = new RemovedRadio("A");
            var b = new RemovedRadio("B");

            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(a, "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            gate.Reset();

            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(b, "B");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));

            // A's second notice, arriving after B's whole drop.
            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(a, "A");
            Thread.Sleep(300);

            lock (archivedFor)
            {
                Assert.Equal(2, archivedFor.Count);
                Assert.Contains(archivedFor, s => s.Contains("to A dropped", StringComparison.Ordinal));
                Assert.Contains(archivedFor, s => s.Contains("to B dropped", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void A_genuinely_new_drop_archives_again()
        {
            // The positive control. A second radio, or the same radio after a
            // reconnect that acquired a fresh object, arrives as a different
            // object and therefore a different connection lifetime.
            int calls = 0;
            using var gate = new ManualResetEventSlim(false);
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref calls);
                gate.Set();
                return Archived(@"C:\Traces\one.zip");
            };

            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            gate.Reset();

            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();

            CaptureArchive.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, Volatile.Read(ref calls));
        }

        [Fact]
        public void With_no_object_to_compare_it_falls_back_to_one_archive_per_session()
        {
            // A caller that has no radio object still must not archive twice for
            // one drop. The old rule is the safe answer when identity is
            // unavailable — it is only wrong when a session event re-arms it,
            // and nothing does that any more.
            int calls = 0;
            using var gate = new ManualResetEventSlim(false);
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref calls);
                gate.Set();
                return Archived(@"C:\Traces\one.zip");
            };

            TraceSessionContext.BeginSession();
            CaptureArchive.AfterConnectionDrop(null, "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));

            CaptureArchive.AfterConnectionDrop(null, "A");
            Thread.Sleep(250);
            Assert.Equal(1, Volatile.Read(ref calls));
        }

        // ────────────────────────────────────────────────────────────────
        //  The word that ends up on the file
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_application_archives_the_session_the_drop_was_about()
        {
            // The hook receives the expected handle and the detail; the OUTCOME
            // is chosen in globals.vb, and it is the outcome that puts the word
            // on the zip. Source-read because the archiving needs a live trace
            // session and a radio, and reading it is how this track can assert
            // the one thing 231 archives on disk prove nobody ever did.
            string globals = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "globals.vb"));
            Assert.Contains("Friend Function ArchiveCaptureForConnectionDrop(request As Radios.CaptureArchiveRequest)",
                globals, StringComparison.Ordinal);
            Assert.Contains("TraceSessionOutcome.ConnectionDropped", globals, StringComparison.Ordinal);
            Assert.Contains("Radios.CaptureArchive.ArchiveHook =", globals, StringComparison.Ordinal);

            // The ownership comparison is the boundary's, carried by a handle —
            // not a read of shared state followed by an act on it.
            Assert.Contains(".Expected = expected", globals, StringComparison.Ordinal);

            // And nothing re-arms an archive where a session begins any more. That
            // line WAS the duplicate-archive defect: RestartDiagnosticLog reaches
            // the session start, and the archive calls RestartDiagnosticLog.
            Assert.DoesNotContain("CaptureArchive.Rearm", globals, StringComparison.Ordinal);
        }

        [Fact]
        public void The_outcome_constant_is_the_one_the_manifest_already_defines()
        {
            Assert.Equal("connection_dropped", TraceSessionOutcome.ConnectionDropped);
        }
    }
}
