using System;
using System.IO;
using System.Threading;
using JJTrace;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A radio-side connection loss seals the running capture as
    /// <c>connection_dropped</c>, and the operator's own disconnect does not
    /// (#566's bridge, Sprint 45 Track H).
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
    /// </remarks>
    // No collection attribute needed: the suite runs sequentially by assembly
    // policy (see TestParallelism.cs), which is what makes the process-wide
    // SealHook and arm flag safe to drive here.
    public sealed class CaptureSealTests : IDisposable
    {
        private readonly Func<string, string> _savedHook;

        public CaptureSealTests()
        {
            _savedHook = CaptureSeal.SealHook;
            CaptureSeal.Rearm();
        }

        public void Dispose()
        {
            CaptureSeal.SealHook = _savedHook;
            CaptureSeal.Rearm();
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
            CaptureSealNotice notice = null;

            CaptureSeal.SealHook = detail =>
            {
                detailSeen = detail;
                sealed_.Set();
                return @"C:\Traces\2026\09\trace-20260922-201500-connection_dropped.zip";
            };

            void OnSealed(CaptureSealNotice n) { notice = n; told.Set(); }
            CaptureSeal.SealedAfterDrop += OnSealed;
            try
            {
                CaptureSeal.AfterConnectionDrop("6300inshack");
                Assert.True(sealed_.Wait(TimeSpan.FromSeconds(5)), "the seal hook was never called");
                Assert.True(told.Wait(TimeSpan.FromSeconds(5)), "nobody was told where it landed");
            }
            finally
            {
                CaptureSeal.SealedAfterDrop -= OnSealed;
            }

            // The detail is what the trace browser prints in its list, so it is
            // a sentence a person reads, not a code.
            Assert.Equal(
                "The connection to 6300inshack dropped while this session was running",
                detailSeen);
            Assert.NotNull(notice);
            Assert.Equal("6300inshack", notice.RadioName);
            Assert.EndsWith("connection_dropped.zip", notice.ArchivePath, StringComparison.Ordinal);
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
        public void A_second_drop_in_the_same_session_does_not_seal_again()
        {
            // FlexLib can raise the removal more than once around a single
            // drop. A second seal would archive an empty session and put a
            // second window on an operator whose radio has just died.
            int calls = 0;
            using var first = new ManualResetEventSlim(false);
            CaptureSeal.SealHook = _ =>
            {
                Interlocked.Increment(ref calls);
                first.Set();
                return @"C:\Traces\one.zip";
            };

            CaptureSeal.AfterConnectionDrop("A");
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));

            CaptureSeal.AfterConnectionDrop("A");
            Thread.Sleep(200);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(CaptureSeal.Armed);
        }

        [Fact]
        public void A_new_trace_session_re_arms_the_seal()
        {
            // Otherwise the first dropped connection of the evening spends the
            // seal and every later one is archived as an ordinary session.
            int calls = 0;
            using var gate = new ManualResetEventSlim(false);
            CaptureSeal.SealHook = _ =>
            {
                Interlocked.Increment(ref calls);
                gate.Set();
                return @"C:\Traces\one.zip";
            };

            CaptureSeal.AfterConnectionDrop("A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            gate.Reset();

            CaptureSeal.Rearm();
            Assert.True(CaptureSeal.Armed);
            CaptureSeal.AfterConnectionDrop("A");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, Volatile.Read(ref calls));
        }

        [Fact]
        public void With_no_hook_installed_nothing_happens_and_the_seal_is_not_spent()
        {
            // A missing hook is a wiring defect, and the symptom — a capture
            // that quietly says clean_exit — looks exactly like the bug this
            // class fixes. It must not also silently burn the session's one
            // seal, or a later correctly-wired drop would be refused.
            CaptureSeal.SealHook = null;
            CaptureSeal.AfterConnectionDrop("A");
            Thread.Sleep(100);
            Assert.False(CaptureSeal.Armed);
        }

        [Fact]
        public void Nobody_is_told_when_no_archive_was_produced()
        {
            // A window offering a path that does not exist is worse than no
            // window.
            bool told = false;
            CaptureSeal.SealHook = _ => null;
            void OnSealed(CaptureSealNotice _) { told = true; }
            CaptureSeal.SealedAfterDrop += OnSealed;
            try
            {
                CaptureSeal.AfterConnectionDrop("A");
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
            CaptureSeal.SealHook = _ => throw new InvalidOperationException("disk busy");
            CaptureSeal.AfterConnectionDrop("A");   // must not throw here
            Thread.Sleep(200);
        }

        // ────────────────────────────────────────────────────────────────
        //  The word that ends up on the file
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_application_seals_with_the_connection_dropped_outcome()
        {
            // The hook receives only the detail; the OUTCOME is chosen in
            // globals.vb, and it is the outcome that puts the word on the zip.
            // Source-read because the sealing needs a live trace session and a
            // radio, and reading it is how this track can assert the one thing
            // 231 archives on disk prove nobody ever did.
            string globals = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "globals.vb"));
            Assert.Contains("Friend Function SealCaptureForConnectionDrop", globals, StringComparison.Ordinal);
            Assert.Contains("TraceSessionOutcome.ConnectionDropped", globals, StringComparison.Ordinal);
            Assert.Contains("Radios.CaptureSeal.SealHook =", globals, StringComparison.Ordinal);
            // Re-armed where a session begins, and nowhere else.
            Assert.Contains("Radios.CaptureSeal.Rearm()", globals, StringComparison.Ordinal);
        }

        [Fact]
        public void The_outcome_constant_is_the_one_the_manifest_already_defines()
        {
            Assert.Equal("connection_dropped", TraceSessionOutcome.ConnectionDropped);
        }
    }
}
