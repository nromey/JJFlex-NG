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
    /// A radio-side connection loss seals the running capture as
    /// <c>connection_dropped</c>, the operator's own disconnect does not, and
    /// the session that gets sealed is the one that was recording AT THE DROP
    /// (#566's bridge, Sprint 45 Tracks H and H2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap.</b> <c>TraceSessionOutcome.ConnectionDropped</c> has existed
    /// since Sprint 29 and no archive on this machine has ever carried it — 231
    /// zips, counted 2026-09-22, all <c>clean_exit</c>, <c>killed</c>,
    /// <c>no_radios</c> or <c>slice_unavailable</c>. Nothing on the drop path
    /// archived anything, so a radio dying mid-transmit left the session open
    /// and the app sealed the whole evening <c>clean_exit</c> hours later. A
    /// radio death and a quiet evening produced the same file.
    /// </para>
    /// <para>
    /// <b>The positive control is the important half.</b> Sealing on a
    /// deliberate disconnect would be worse than not sealing at all: it would
    /// fill the archive with the word and destroy the one query the outcome
    /// exists to answer.
    /// </para>
    /// <para>
    /// <b>And the lifecycle tests below are the ones the first build could not
    /// have.</b> Its thirty-seven tests were all helper tests, and the defect
    /// was not in a helper: the seal was queued with nothing but a radio name
    /// and resolved the process-wide current session whenever the worker
    /// happened to run. A test that cannot decide when the worker wakes cannot
    /// see that at all. <see cref="CaptureSeal.Queue"/> exists so these can.
    /// </para>
    /// </remarks>
    // No collection attribute needed: the suite runs sequentially by assembly
    // policy (see TestParallelism.cs), which is what makes the process-wide
    // SealHook, queue and claim safe to drive here.
    public sealed class CaptureSealTests : IDisposable
    {
        private readonly Func<Guid, string, string> _savedHook;
        private readonly Action<Action> _savedQueue;
        private readonly TraceSession _savedSession;

        public CaptureSealTests()
        {
            _savedHook = CaptureSeal.SealHook;
            _savedQueue = CaptureSeal.Queue;
            _savedSession = TraceSessionContext.Current;
            CaptureSeal.ForgetClaimForTests();
        }

        public void Dispose()
        {
            CaptureSeal.SealHook = _savedHook;
            CaptureSeal.Queue = _savedQueue;
            CaptureSeal.ForgetClaimForTests();
            RestoreSession(_savedSession);
        }

        /// <summary>
        /// Put the real <see cref="TraceSessionContext"/> back the way it was.
        /// These tests drive the genuine process-wide session pointer, because
        /// the defect they exist for lives in exactly that pointer being
        /// replaceable from any thread.
        /// </summary>
        private static void RestoreSession(TraceSession session)
        {
            // Set the pointer rather than calling EndSession, which would stamp
            // an end time on a session this test did not open.
            typeof(TraceSessionContext)
                .GetField("_current", System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Static)
                .SetValue(null, session);
        }

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
        //  Which removals seal, and which must not
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Only_our_radio_dropping_seals_the_capture()
        {
            Assert.True(FlexBase.RemovalSealsTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio));

            // The operator hung up. Their own disconnect is a normal end to a
            // session and the manifest already has the vocabulary for it.
            Assert.False(FlexBase.RemovalSealsTheCapture(
                FlexBase.RadioRemovalKind.SelfInitiated));

            // Some other radio aged out of discovery. Says nothing whatever
            // about the session we are recording.
            Assert.False(FlexBase.RemovalSealsTheCapture(
                FlexBase.RadioRemovalKind.DiscoveryLoss));
        }

        [Fact]
        public void The_drop_case_seals_and_the_self_case_does_not_in_the_shipped_handler()
        {
            // A truth table nothing consults is a truth table that is wrong for
            // free. This pins the call site: the seal is inside the
            // ConnectionLostOurRadio arm and nowhere else in the handler.
            string source = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));
            Assert.Contains("case RadioRemovalKind.ConnectionLostOurRadio:", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ThisStringIsNotInFlexBaseAnywhere", source, StringComparison.Ordinal);

            int drop = source.IndexOf("case RadioRemovalKind.ConnectionLostOurRadio:", StringComparison.Ordinal);
            int selfCase = source.IndexOf("case RadioRemovalKind.SelfInitiated:", StringComparison.Ordinal);
            Assert.True(drop > 0 && selfCase > 0);

            string dropArm = source.Substring(drop, source.IndexOf("default:", drop, StringComparison.Ordinal) - drop);
            string selfArm = source.Substring(selfCase, drop - selfCase);

            Assert.Contains("CaptureSeal.AfterConnectionDrop", dropArm, StringComparison.Ordinal);
            Assert.DoesNotContain("CaptureSeal.AfterConnectionDrop", selfArm, StringComparison.Ordinal);

            // And the removal's own object is what identifies the drop, not
            // just its nickname — two removals carrying the same object are one
            // drop, and that is what stops a repeat notice sealing a fresh log.
            Assert.Contains("CaptureSeal.AfterConnectionDrop(r,", dropArm, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  The seal itself
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_drop_seals_once_and_hands_back_where_it_landed()
        {
            using var sealed_ = new ManualResetEventSlim(false);
            using var told = new ManualResetEventSlim(false);
            string detailSeen = null;
            Guid sessionSeen = Guid.Empty;
            CaptureSealNotice notice = null;

            TraceSession session = TraceSessionContext.BeginSession();
            CaptureSeal.SealHook = (id, detail) =>
            {
                sessionSeen = id;
                detailSeen = detail;
                sealed_.Set();
                return @"C:\Traces\2026\09\trace-20260922-201500-connection_dropped.zip";
            };

            void OnSealed(CaptureSealNotice n) { notice = n; told.Set(); }
            CaptureSeal.SealedAfterDrop += OnSealed;
            try
            {
                CaptureSeal.AfterConnectionDrop(new RemovedRadio("6300inshack"), "6300inshack");
                Assert.True(sealed_.Wait(TimeSpan.FromSeconds(5)), "the seal hook was never called");
                Assert.True(told.Wait(TimeSpan.FromSeconds(5)), "nobody was told where it landed");
            }
            finally
            {
                CaptureSeal.SealedAfterDrop -= OnSealed;
            }

            // The hook is told WHICH session to seal. Without that it resolved
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
            // sealed file can read unknown-part-3 while the manifest inside it
            // correctly says connection_dropped (Codex's review, bench
            // predictions). Anything that promises the operator a filename is
            // wrong; the notice hands over the path the archive actually
            // returned, whatever it is called.
            string rotated = @"C:\Traces\2026\09\trace-20260922-201500-unknown-part-003.zip";
            using var told = new ManualResetEventSlim(false);
            CaptureSealNotice notice = null;

            TraceSessionContext.BeginSession();
            CaptureSeal.SealHook = (_, __) => rotated;
            void OnSealed(CaptureSealNotice n) { notice = n; told.Set(); }
            CaptureSeal.SealedAfterDrop += OnSealed;
            try
            {
                CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
                Assert.True(told.Wait(TimeSpan.FromSeconds(5)));
            }
            finally { CaptureSeal.SealedAfterDrop -= OnSealed; }

            Assert.Equal(rotated, notice.ArchivePath);
            Assert.Equal(rotated, notice.ClipboardText);
        }

        [Fact]
        public void An_unnamed_radio_still_gets_a_sentence()
        {
            Assert.Equal("The radio's connection dropped while this session was running",
                CaptureSeal.OutcomeDetail(""));
            Assert.Equal("The radio's connection dropped while this session was running",
                CaptureSeal.OutcomeDetail(null));
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
            // own comment said — it checked that the seal HAD been spent — so
            // the suite was pinning the behaviour as desired.
            TraceSessionContext.BeginSession();
            CaptureSeal.SealHook = null;
            var radio = new RemovedRadio("A");
            CaptureSeal.AfterConnectionDrop(radio, "A");

            int calls = 0;
            using var gate = new ManualResetEventSlim(false);
            CaptureSeal.SealHook = (_, __) =>
            {
                Interlocked.Increment(ref calls);
                gate.Set();
                return @"C:\Traces\one.zip";
            };
            CaptureSeal.AfterConnectionDrop(radio, "A");
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
            CaptureSeal.SealHook = (_, __) => null;
            void OnSealed(CaptureSealNotice _) { told = true; }
            CaptureSeal.SealedAfterDrop += OnSealed;
            try
            {
                CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
                Thread.Sleep(300);
            }
            finally
            {
                CaptureSeal.SealedAfterDrop -= OnSealed;
            }
            Assert.False(told);
        }

        [Fact]
        public void A_hook_that_throws_does_not_take_the_drop_path_down_with_it()
        {
            TraceSessionContext.BeginSession();
            CaptureSeal.SealHook = (_, __) => throw new InvalidOperationException("disk busy");
            CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");   // must not throw here
            Thread.Sleep(200);
        }

        [Fact]
        public void Nothing_recording_means_nothing_to_seal()
        {
            // Not a failure and not silence. The hook is never called, because
            // there is no session for it to archive.
            RestoreSession(null);
            int calls = 0;
            CaptureSeal.SealHook = (_, __) => { Interlocked.Increment(ref calls); return null; };
            CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Thread.Sleep(200);
            Assert.Equal(0, Volatile.Read(ref calls));
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
        /// </summary>
        [Fact]
        public void A_worker_that_wakes_to_find_its_session_gone_archives_nothing()
        {
            var held = new HeldQueue();
            CaptureSeal.Queue = held.Take;

            int calls = 0;
            CaptureSeal.SealHook = (_, __) =>
            {
                Interlocked.Increment(ref calls);
                return @"C:\Traces\wrong.zip";
            };

            TraceSessionContext.BeginSession();
            CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Assert.Equal(1, held.Count);

            // Everything that can happen between the drop and the worker:
            // the operator stops the capture, and something starts another.
            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();

            held.RunAll();
            Assert.Equal(0, Volatile.Read(ref calls));
        }

        [Fact]
        public void A_worker_whose_session_is_still_recording_seals_it()
        {
            // The positive control for the test above: a guard that refuses
            // everything proves nothing at all.
            var held = new HeldQueue();
            CaptureSeal.Queue = held.Take;

            Guid sealed_ = Guid.Empty;
            CaptureSeal.SealHook = (id, __) => { sealed_ = id; return @"C:\Traces\right.zip"; };

            TraceSession session = TraceSessionContext.BeginSession();
            CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
            held.RunAll();

            Assert.Equal(session.SessionId, sealed_);
        }

        [Fact]
        public void The_session_ending_altogether_is_refused_too()
        {
            // Exit, or a problem-report bundle that takes the log away. There
            // is no session at all, and the old code would have called the hook
            // regardless.
            var held = new HeldQueue();
            CaptureSeal.Queue = held.Take;

            int calls = 0;
            CaptureSeal.SealHook = (_, __) => { Interlocked.Increment(ref calls); return null; };

            TraceSessionContext.BeginSession();
            CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
            TraceSessionContext.EndSession();

            held.RunAll();
            Assert.Equal(0, Volatile.Read(ref calls));
        }

        // ────────────────────────────────────────────────────────────────
        //  One seal per DROP, not per session bit
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The other half of the merge blocker. The seal restarts the standing
        /// log; restarting a log begins a session; and beginning a session used
        /// to re-arm the one-bit guard. So the guard disarmed itself, on the
        /// seal's own path, in time for the duplicate removal it existed to
        /// refuse — and the second notice sealed the fresh, nearly empty log
        /// and put a second window in front of an operator whose radio had just
        /// died.
        /// </summary>
        [Fact]
        public void A_repeat_notice_from_the_same_drop_does_not_seal_the_fresh_log()
        {
            int calls = 0;
            using var first = new ManualResetEventSlim(false);
            CaptureSeal.SealHook = (_, __) =>
            {
                Interlocked.Increment(ref calls);
                first.Set();
                return @"C:\Traces\one.zip";
            };

            var radio = new RemovedRadio("A");
            TraceSessionContext.BeginSession();
            CaptureSeal.AfterConnectionDrop(radio, "A");
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));

            // What the seal itself does next: archive, then restart the log.
            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();

            // FlexLib raises the removal again, carrying the same object.
            CaptureSeal.AfterConnectionDrop(radio, "A");
            Thread.Sleep(250);
            Assert.Equal(1, Volatile.Read(ref calls));
        }

        [Fact]
        public void A_genuinely_new_drop_seals_again()
        {
            // The positive control. A second radio, or the same radio after a
            // reconnect, arrives as a different object — which is exact,
            // because a removal is only called a drop of OUR radio when the
            // object IS theRadio, so a new drop needs a new connection first.
            int calls = 0;
            using var gate = new ManualResetEventSlim(false);
            CaptureSeal.SealHook = (_, __) =>
            {
                Interlocked.Increment(ref calls);
                gate.Set();
                return @"C:\Traces\one.zip";
            };

            TraceSessionContext.BeginSession();
            CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            gate.Reset();

            TraceSessionContext.EndSession();
            TraceSessionContext.BeginSession();

            CaptureSeal.AfterConnectionDrop(new RemovedRadio("A"), "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, Volatile.Read(ref calls));
        }

        [Fact]
        public void With_no_object_to_compare_it_falls_back_to_one_seal_per_session()
        {
            // A caller that has no radio object still must not seal twice for
            // one drop. The old rule is the safe answer when identity is
            // unavailable — it is only wrong when a session event re-arms it,
            // and nothing does that any more.
            int calls = 0;
            using var gate = new ManualResetEventSlim(false);
            CaptureSeal.SealHook = (_, __) =>
            {
                Interlocked.Increment(ref calls);
                gate.Set();
                return @"C:\Traces\one.zip";
            };

            TraceSessionContext.BeginSession();
            CaptureSeal.AfterConnectionDrop(null, "A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));

            CaptureSeal.AfterConnectionDrop(null, "A");
            Thread.Sleep(250);
            Assert.Equal(1, Volatile.Read(ref calls));
        }

        // ────────────────────────────────────────────────────────────────
        //  The word that ends up on the file
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_application_seals_the_session_the_drop_was_about()
        {
            // The hook receives the session id and the detail; the OUTCOME is
            // chosen in globals.vb, and it is the outcome that puts the word on
            // the zip. Source-read because the sealing needs a live trace
            // session and a radio, and reading it is how this track can assert
            // the one thing 231 archives on disk prove nobody ever did.
            string globals = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "globals.vb"));
            Assert.Contains("Friend Function SealCaptureForConnectionDrop(sessionId As Guid, detail As String)",
                globals, StringComparison.Ordinal);
            Assert.Contains("TraceSessionOutcome.ConnectionDropped", globals, StringComparison.Ordinal);
            Assert.Contains("Radios.CaptureSeal.SealHook =", globals, StringComparison.Ordinal);

            // The guard at the place that actually takes the session: archive
            // the one the drop was about, or nothing.
            Assert.Contains("If Not CurrentTraceSessionIs(sessionId) Then", globals, StringComparison.Ordinal);

            // And nothing re-arms a seal where a session begins any more. That
            // line WAS the duplicate-seal defect: RestartDiagnosticLog reaches
            // BeginNewTraceSession, and the seal calls RestartDiagnosticLog.
            Assert.DoesNotContain("CaptureSeal.Rearm", globals, StringComparison.Ordinal);
        }

        [Fact]
        public void The_outcome_constant_is_the_one_the_manifest_already_defines()
        {
            Assert.Equal("connection_dropped", TraceSessionOutcome.ConnectionDropped);
        }
    }
}
