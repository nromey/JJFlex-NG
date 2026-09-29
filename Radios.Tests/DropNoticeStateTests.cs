using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JJTrace;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The drop window's "what happens next is being kept" is decided by the
    /// coordinator's state at the moment the notice is built, not by the archive
    /// (Sol's review of H9, blocker 1). Driven through the REAL coordinator
    /// and the REAL archive worker, with the archive worker held on a barrier so
    /// the world can be changed in the five-minute gap the production hook
    /// waits through: the operator turns the log off, or the fresh file
    /// fails. No window, no radio.
    /// </summary>
    /// <remarks>
    /// <para>The archive hook here mirrors <c>globals.vb</c>'s
    /// <c>ArchiveCaptureForConnectionDrop</c> in the one respect that matters:
    /// it archives through <c>TryArchive</c>, then WAITS for the archive before
    /// returning — which is where the operator gets their chance. A hook that
    /// returned at once could not reproduce the blocker.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs),
    // which is what makes the process-wide coordinator, archive hook and queue
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
        private readonly Func<CaptureArchiveRequest, CaptureArchiveResult> _savedHook;
        private readonly Action<Action> _savedQueue;

        public DropNoticeStateTests(ITestOutputHelper output)
        {
            _out = output;
            _savedLevel = Tracing.TheSwitch.Level;
            _savedOn = Tracing.On;
            _savedArchiveRoot = TraceCoordinator.ArchiveRootDir;
            _savedSession = TraceSessionContext.Current;
            _savedHook = CaptureArchive.ArchiveHook;
            _savedQueue = CaptureArchive.Queue;

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
            // archive with AlreadyClaimed and somebody else's session.
            CaptureArchive.ForgetClaimForTests();
            TraceCoordinator.ResetClaimsForTests();
            Tracing.On = true;
        }

        public void Dispose()
        {
            TraceArchiveWorker.BeforeArchiveForTests = null;
            CaptureArchive.ArchiveHook = _savedHook;
            CaptureArchive.Queue = _savedQueue;
            CaptureArchive.ForgetClaimForTests();
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
            TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
            // Leave no ticket behind under an operation id another class may
            // rebuild from a restarted token ordinal.
            TraceCoordinator.ResetClaimsForTests();
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
        /// Hold the fault-retire work item so a session whose file has died
        /// is still current when the drop's archive arrives — the same helper
        /// <c>TraceCoordinatorTests</c> uses. Disposing runs what was held.
        /// </summary>
        private sealed class HeldFaultRetire : IDisposable
        {
            private Action _work;
            public HeldFaultRetire() { TraceCoordinator.FaultRetireQueue = w => { lock (this) _work = w; }; }
            public void Dispose()
            {
                TraceCoordinator.FaultRetireQueue = TraceCoordinator.DefaultFaultRetireQueue;
                Action w;
                lock (this) { w = _work; _work = null; }
                w?.Invoke();
            }
        }

        private static string TemperatureLine(int samples, string partialReason = null) =>
            CaptureMeterSet.Format(41.5f, 43f, 42.25f, samples, SupplyVoltage.NoMeter(), transmitting: true, partialReason);

        /// <summary>The drop's partial window exactly as
        /// <c>FlexBase.collectCaptureMeterFlush</c> hands it over: the rendered
        /// line WITH its writer's kind (#625).</summary>
        private static TraceRecord TemperatureRecord(int samples, string partialReason) =>
            new TraceRecord(TemperatureLine(samples, partialReason), CaptureMeterSet.CaptureMetersRecord);

        private const string PowerLine =
            "txMeters: state=tx SC_MIC=-20.0 (peak -18.0) via mic SWALC=0.0 fwd=47.0 dBm refl=20.0 dBm fwdW=50.12 reflW=0.100 back=0.2% SWRraw=1.09 SWRcalc=1.09";

        private const string MeterStreamLine = "micData: min=-120 max=-118.4 last=-119 n=34";

        /// <summary>
        /// One drop, end to end: a standing session is recording;
        /// <paramref name="beforeTheDrop"/> writes into it; the radio's
        /// connection falls, collecting <paramref name="partialMeterLine"/>
        /// as the window it closed; the archive hook archives it and waits for its
        /// archive, which is held on a barrier; <paramref name="duringTheWait"/>
        /// runs on the test thread while it is held; the barrier lifts; the
        /// notice the operator would be shown is returned.
        /// <paramref name="underTheGate"/>, if given, is the coordinator's
        /// transition probe for the archive — the one place a fault can be
        /// injected AFTER the archive has read the sink's state and BEFORE it
        /// writes, deterministically.
        /// </summary>
        private CaptureArchiveNotice OneDrop(Action duringTheWait, Action beforeTheDrop = null,
                                          TraceRecord partialMeterLine = null, Action<string> underTheGate = null)
        {
            TraceCoordinator.SetStandingIntent(true, TraceLevel.Verbose);
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);
            TraceSessionHandle old = began.Successor;
            beforeTheDrop?.Invoke();

            using var atWorker = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var told = new ManualResetEventSlim(false);
            CaptureArchiveNotice notice = null;
            TraceTransitionResult archiveResult = null;
            Thread worker = null;

            TraceArchiveWorker.BeforeArchiveForTests = t =>
            {
                if (t.SessionId != old.SessionId) return;
                atWorker.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            TraceCoordinator.TransitionProbeForTests = underTheGate;
            CaptureArchive.ArchiveHook = req =>
            {
                var lines = new System.Collections.Generic.List<TraceRecord>();
                if (!string.IsNullOrEmpty(req.PartialMeterLine?.Text)) lines.Add(req.PartialMeterLine);
                archiveResult = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = (TraceSessionHandle)req.ExpectedSession,
                    OperationId = req.DropOperationId,
                    Outcome = TraceSessionOutcome.ConnectionDropped,
                    OutcomeDetail = req.OutcomeDetail,
                    TerminalLines = lines,
                    Resume = TraceResumeIntent.Standing,
                });
                var outcome = new CaptureArchiveResult
                {
                    Refused = !archiveResult.Owned,
                    RefusalReason = archiveResult.Owned ? null : archiveResult.Explanation,
                    ArchivedSessionId = archiveResult.Ticket?.SessionId,
                    SuccessorOpened = archiveResult.SuccessorOpened,
                    SuccessorRecording = archiveResult.TracingOn,
                    TailUncertain = archiveResult.TailUncertain,
                    SinkFailedBeforeDrop = archiveResult.SinkFailedBeforeArchive,
                };
                // As globals.vb does: the operator is only ever offered a path
                // that exists, so wait for the archive here, on the worker.
                if (archiveResult.Owned && archiveResult.Ticket?.Completion != null
                    && archiveResult.Ticket.Completion.Wait(TimeSpan.FromSeconds(60))
                    && archiveResult.Ticket.Completion.Result.ArchiveCommitted)
                {
                    outcome.ArchivePath = archiveResult.Ticket.Completion.Result.ArchiveFullPath;
                }
                return outcome;
            };
            // A real background thread, so the test thread is free to be the
            // operator while the worker waits.
            CaptureArchive.Queue = work => { worker = new Thread(() => work()) { IsBackground = true }; worker.Start(); };
            void OnArchived(CaptureArchiveNotice n) { notice = n; told.Set(); }
            CaptureArchive.ArchivedAfterDrop += OnArchived;
            try
            {
                CaptureArchive.AfterConnectionDrop(new object(), "6300inshack",
                                                partialMeterLine == null ? null : (Func<TraceRecord>)(() => partialMeterLine), old);
                Assert.True(atWorker.Wait(TimeSpan.FromSeconds(10)),
                    "the archive worker never reached the old session's ticket; archive: "
                    + (archiveResult == null ? "hook not called" : archiveResult.Status + " — " + archiveResult.Explanation)
                    + "; worker alive: " + (worker?.IsAlive));
                // Positive control on the archive itself: it owned the session
                // and a successor is recording at this moment.
                Assert.NotNull(archiveResult);
                Assert.True(archiveResult.Owned);
                Assert.True(archiveResult.SuccessorOpened, "no successor opened, so nothing can change during the wait");
                Assert.True(TraceCoordinator.Observe().Recording, "the successor is not recording at the archive");

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
                CaptureArchive.ArchivedAfterDrop -= OnArchived;
                TraceArchiveWorker.BeforeArchiveForTests = null;
                TraceCoordinator.TransitionProbeForTests = null;
                worker?.Join(TimeSpan.FromSeconds(30));
            }
            _out.WriteLine("archive: " + archiveResult.Explanation);
            _out.WriteLine(notice.Explanation);
            _out.WriteLine(string.Empty);
            return notice;
        }

        // ── #625: the window describes nothing; the file describes itself ──
        //
        // From H10 to H15 this section drove Sol's content counterexamples
        // through a real drop and asserted that the window named exactly the
        // readings the file held. Noel ruled on 2026-09-25 that the window
        // stops describing the file and the file describes itself, each writer
        // introducing its own lines as it writes them. So the same drops are
        // driven, and the assertions are the other way round: the window says
        // one thing whatever was written, and the ARCHIVED FILE carries the
        // introductions for exactly the kinds that were.

        private static void DisposeLiveField(string field)
        {
            RotatingTraceListener live = Tracing.LiveListener;
            Assert.NotNull(live);
            var value = (IDisposable)typeof(RotatingTraceListener)
                .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(live)!;
            value.Dispose();
        }

        /// <summary>
        /// End the successor the last drop opened, so the next
        /// <see cref="OneDrop"/> can begin a session of its own — what Dispose
        /// does, between drops.
        /// </summary>
        private static void EndRecording()
        {
            TraceSessionHandle current = TraceCoordinator.CurrentHandle;
            if (current != null)
            {
                TraceTransitionResult off = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = current,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.None,
                });
                Assert.Equal(TraceTransition.Accepted, off.Status);
            }
            Assert.True(TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(30)));
            Assert.Null(TraceCoordinator.CurrentHandle);
        }

        private string ArchivedText(CaptureArchiveNotice notice)
        {
            string outDir = Path.Combine(_dir, "extract-" + Guid.NewGuid().ToString("N"));
            string extracted = SessionArchive.ExtractTraceText(notice.ArchivePath, outDir);
            Assert.NotNull(extracted);
            return File.ReadAllText(extracted);
        }

        private static void AssertNoContentClaim(CaptureArchiveNotice notice)
        {
            foreach (string word in CaptureArchiveNoticeTests.ContentWords)
            {
                Assert.DoesNotContain(word, notice.Explanation, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Asserts that <paramref name="kind"/> is introduced in
        /// <paramref name="text"/> exactly once, and on the line immediately
        /// before the first line that begins (after the prefix) with
        /// <paramref name="lineHead"/>.
        /// </summary>
        private static void AssertIntroducedOnceBeforeFirst(string text, TraceRecordKind kind, string lineHead)
        {
            string[] lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            string intro = TraceSelfDescription.Introduction(kind);
            int[] introAt = lines.Select((l, i) => (l, i)).Where(p => p.l.Contains(intro, StringComparison.Ordinal)).Select(p => p.i).ToArray();
            Assert.True(introAt.Length == 1, kind.Key + " introduced " + introAt.Length + " times; expected once");
            int firstRecord = Array.FindIndex(lines, l => l.Contains("] " + lineHead, StringComparison.Ordinal));
            Assert.True(firstRecord > 0, "no " + lineHead + " line in the file");
            Assert.Equal(firstRecord - 1, introAt[0]);
        }

        /// <summary>
        /// <b>#625, end to end.</b> Three drops that used to earn three
        /// different saved paragraphs — a receive-only session (temperature
        /// only), a session with nothing measured, a transmitting session with
        /// power, temperature AND the opt-in meter stream (the writer Sol
        /// found the old count could not see) — now earn one, and it names
        /// nothing. The positive control is the file: each archived recording
        /// introduces exactly the kinds written into it, once each, directly
        /// ahead of the first line of that kind, opens with the reading guide,
        /// and closes with why it ended.
        /// </summary>
        [Fact]
        public void The_window_says_the_same_thing_whatever_the_file_holds_and_the_file_says_it_itself()
        {
            CaptureArchiveNotice receiveOnly = OneDrop(
                duringTheWait: () => { },
                beforeTheDrop: () =>
                {
                    Tracing.TraceLine("propertyChanged:Slice:Freq", TraceLevel.Info);
                    Tracing.TraceRecord(CaptureMeterSet.CaptureMetersRecord, TemperatureLine(6), TraceLevel.Info);
                },
                partialMeterLine: TemperatureRecord(0, "connection_dropped"));
            string receiveOnlyText = ArchivedText(receiveOnly);
            EndRecording();

            CaptureArchiveNotice nothing = OneDrop(
                duringTheWait: () => { },
                beforeTheDrop: () => Tracing.TraceLine("propertyChanged:Slice:Freq", TraceLevel.Info),
                partialMeterLine: null);
            string nothingText = ArchivedText(nothing);
            EndRecording();

            CaptureArchiveNotice transmitting = OneDrop(
                duringTheWait: () => { },
                beforeTheDrop: () =>
                {
                    Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, PowerLine, TraceLevel.Info);
                    Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, PowerLine, TraceLevel.Info);
                    Tracing.TraceRecord(MeterTraceStream.Record, MeterStreamLine);
                },
                partialMeterLine: TemperatureRecord(2, "connection_dropped"));
            string transmittingText = ArchivedText(transmitting);

            // The window: one sentence, whatever was written, and nothing in it
            // about what was written.
            foreach (CaptureArchiveNotice n in new[] { receiveOnly, nothing, transmitting })
            {
                Assert.False(n.TailUncertain);
                AssertNoContentClaim(n);
            }
            Assert.Equal(receiveOnly.WhatWasSaved, nothing.WhatWasSaved);
            Assert.Equal(receiveOnly.WhatWasSaved, transmitting.WhatWasSaved);
            // Which sentence it is, by key, is RecordingHealthNoticeTests'
            // business: that class sits in the statics collection the lexicon
            // store requires, and this one does not.
            Assert.Contains("The recording has been closed and saved.", receiveOnly.WhatWasSaved, StringComparison.Ordinal);

            // The file: each kind that was written introduces itself, once,
            // directly ahead of its first line; a kind that was not written is
            // not mentioned.
            _out.WriteLine("--- receive-only archive");
            _out.WriteLine(receiveOnlyText);
            AssertIntroducedOnceBeforeFirst(receiveOnlyText, CaptureMeterSet.CaptureMetersRecord, CaptureMeterSet.CaptureMetersLine);
            Assert.DoesNotContain(TraceSelfDescription.Introduction(CaptureMeterSet.TxMetersRecord), receiveOnlyText, StringComparison.Ordinal);
            Assert.DoesNotContain(TraceSelfDescription.Introduction(MeterTraceStream.Record), receiveOnlyText, StringComparison.Ordinal);

            _out.WriteLine("--- nothing-measured archive");
            _out.WriteLine(nothingText);
            Assert.DoesNotContain(TraceSelfDescription.Introduction(CaptureMeterSet.CaptureMetersRecord), nothingText, StringComparison.Ordinal);
            Assert.DoesNotContain(TraceSelfDescription.Introduction(CaptureMeterSet.TxMetersRecord), nothingText, StringComparison.Ordinal);
            Assert.DoesNotContain(TraceSelfDescription.Introduction(MeterTraceStream.Record), nothingText, StringComparison.Ordinal);

            _out.WriteLine("--- transmitting archive");
            _out.WriteLine(transmittingText);
            AssertIntroducedOnceBeforeFirst(transmittingText, CaptureMeterSet.TxMetersRecord, CaptureMeterSet.TxMetersLine);
            AssertIntroducedOnceBeforeFirst(transmittingText, MeterTraceStream.Record, "micData:");
            // The partial window is a terminal record with its kind: it is the
            // FIRST captureMeters line in this file, so its introduction sits
            // among the terminal records, directly ahead of it.
            AssertIntroducedOnceBeforeFirst(transmittingText, CaptureMeterSet.CaptureMetersRecord, CaptureMeterSet.CaptureMetersLine);

            // Every archived file opens with the reading guide after its header
            // and closes with why it ended, ahead of the terminal marker.
            foreach (string text in new[] { receiveOnlyText, nothingText, transmittingText })
            {
                string[] lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
                Assert.Contains("--- trace session ", lines[0], StringComparison.Ordinal);
                foreach ((string guide, int i) in TraceSelfDescription.PartPreamble().Select((g, i) => (g, i + 1)))
                {
                    Assert.Contains(guide, lines[i], StringComparison.Ordinal);
                }
                int closing = Array.FindIndex(lines, l => l.Contains(TraceSelfDescription.Marker + " this recording is closed.", StringComparison.Ordinal));
                int lastState = Array.FindLastIndex(lines, l => l.Contains("CaptureState:", StringComparison.Ordinal));
                Assert.True(closing > 0, "no closing line");
                Assert.True(lastState > closing, "the closing line must come before the terminal marker");
                Assert.Contains("Connection dropped", lines[closing], StringComparison.Ordinal);
                Assert.Contains("The connection to 6300inshack dropped while this session was running", lines[closing], StringComparison.Ordinal);
                AssertIntroducedOnceBeforeFirst(text, TraceStateMarker.Record, "CaptureState:");
            }
        }

        /// <summary>
        /// <b>#625 — the reverse of H10's earlier-fault pair.</b> The file died
        /// before the drop. Whether the last reading came before the fault or
        /// a reading was refused after it, the window's caveat is ONE sentence
        /// that says the file stopped taking lines earlier and what to tell
        /// Noel, and names no reading either way. The two H10 sentences that
        /// took sides ("came before that failure, so they are in the file",
        /// "including the last readings the radio sent, is missing") are gone.
        /// </summary>
        [Fact]
        public void An_earlier_fault_gets_one_caveat_whatever_the_readings_did()
        {
            CaptureArchiveNotice readingKept, readingRefused;
            using (new HeldFaultRetire())
            {
                readingKept = OneDrop(
                    duringTheWait: () => { },
                    beforeTheDrop: () =>
                    {
                        Tracing.TraceRecord(CaptureMeterSet.CaptureMetersRecord, TemperatureLine(6), TraceLevel.Info);
                        DisposeLiveField("_writer");
                        Tracing.TraceLine("the write that fails, not a reading", TraceLevel.Info);
                        Assert.True(Tracing.LiveListener.IsClosed);
                    },
                    partialMeterLine: TemperatureRecord(0, "connection_dropped"));   // refused by the dead file
            }
            EndRecording();
            using (new HeldFaultRetire())
            {
                readingRefused = OneDrop(
                    duringTheWait: () => { },
                    beforeTheDrop: () =>
                    {
                        Tracing.TraceRecord(CaptureMeterSet.CaptureMetersRecord, TemperatureLine(6), TraceLevel.Info);
                        DisposeLiveField("_writer");
                        Tracing.TraceLine("the write that fails, not a reading", TraceLevel.Info);
                    },
                    partialMeterLine: TemperatureRecord(3, "connection_dropped"));   // refused, and it held samples
            }

            foreach (CaptureArchiveNotice n in new[] { readingKept, readingRefused })
            {
                Assert.True(n.TailUncertain);
                Assert.True(n.SinkFailedBeforeDrop);
                AssertNoContentClaim(n);
                Assert.Contains("had already stopped taking new lines before the connection went", n.TailCaveat, StringComparison.Ordinal);
                Assert.DoesNotContain("came before that failure", n.Explanation, StringComparison.Ordinal);
                Assert.DoesNotContain("including the last readings the radio sent, is missing", n.Explanation, StringComparison.Ordinal);
                Assert.DoesNotContain("It holds everything up to the moment", n.Explanation, StringComparison.Ordinal);
            }
            Assert.Equal(readingKept.Explanation, readingRefused.Explanation);
        }

        /// <summary>
        /// <b>#625 — the reverse of H10's close-time pair.</b> A write failed
        /// as the recording was being closed. Whether or not lines sat
        /// unflushed in the buffer when it did, the window's caveat is ONE
        /// sentence: the last lines may be missing, so may lines just before
        /// them, send it anyway. "Everything before that point is in the file
        /// above" is never said — it was a claim about contents that needed
        /// the sink's count to be exact, and the count is gone.
        /// </summary>
        [Fact]
        public void A_close_time_fault_gets_one_caveat_whether_or_not_lines_were_buffered()
        {
            CaptureArchiveNotice buffered = OneDrop(
                duringTheWait: () => { },
                beforeTheDrop: () => Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, PowerLine, TraceLevel.Info),
                partialMeterLine: TemperatureRecord(2, "connection_dropped"),
                underTheGate: point =>
                {
                    if (point != "archive:owned") return;
                    RotatingTraceListener live = Tracing.LiveListener;
                    live.WriteLine("1 [T1] buffered one");
                    live.WriteLine("2 [T1] buffered two");
                    live.WriteLine("3 [T1] buffered three");
                    DisposeLiveField("_stream");
                });
            EndRecording();
            CaptureArchiveNotice empty = OneDrop(
                duringTheWait: () => { },
                beforeTheDrop: () => Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, PowerLine, TraceLevel.Info),
                partialMeterLine: TemperatureRecord(2, "connection_dropped"),
                underTheGate: point => { if (point == "archive:owned") DisposeLiveField("_writer"); });

            foreach (CaptureArchiveNotice n in new[] { buffered, empty })
            {
                Assert.True(n.TailUncertain);
                Assert.False(n.SinkFailedBeforeDrop, "the sink was alive when the archive read it");
                AssertNoContentClaim(n);
                Assert.Contains("as the recording was being closed", n.TailCaveat, StringComparison.Ordinal);
                Assert.Contains("lines written just before it may be missing as well", n.TailCaveat, StringComparison.Ordinal);
                Assert.DoesNotContain("Everything before that point is in the file above", n.Explanation, StringComparison.Ordinal);
            }
            Assert.Equal(buffered.Explanation, empty.Explanation);
        }

        /// <summary>
        /// <b>Sol's review of H9, blocker 1.</b> The operator turns the
        /// standing log off in Settings while the drop's archive is being
        /// made. The archive DID open a successor; by the time the window opens
        /// nothing is recording; and the window says so rather than "the
        /// next thing that happens is being kept too".
        /// </summary>
        [Fact]
        public void The_window_says_what_is_being_kept_when_it_opens_not_when_the_archive_happened()
        {
            CaptureArchiveNotice notice = OneDrop(duringTheWait: () =>
            {
                // Exactly what ApplyDiagnosticLogSettings does for "off".
                TraceSessionHandle successor = TraceCoordinator.CurrentHandle;
                Assert.NotNull(successor);
                TraceTransitionResult off = TraceCoordinator.TryArchive(new TraceArchiveRequest
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

            Assert.True(notice.SuccessorOpened, "the archive's own fact is kept: a successor did open");
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
            CaptureArchiveNotice notice = OneDrop(duringTheWait: () =>
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
            CaptureArchiveNotice notice = OneDrop(duringTheWait: () =>
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

        // ── Sol's review of H10, blocker 2: the second queue boundary ──────
        //
        // OneDrop returns once the worker has raised ArchivedAfterDrop and
        // exited — the notice exists, exactly as CaptureArchiveWatch receives
        // it, and nothing has composed its text for a window yet. What the
        // test thread does next is what the dispatcher queue does in
        // production between the worker's BeginInvoke and the dialog's
        // constructor. Reading Explanation afterwards IS the constructor's
        // read.

        /// <summary>
        /// The operator's Settings "off" lands after the worker composed the
        /// notice and before the window installs its text. The window says
        /// nothing is being kept — the same notice that, read a moment
        /// earlier, promised it was.
        /// </summary>
        [Fact]
        public void The_sentence_is_decided_when_the_text_is_installed_not_when_the_worker_composed_the_notice()
        {
            CaptureArchiveNotice notice = OneDrop(duringTheWait: () => { });

            // The worker's moment: recording, and the notice says so. This is
            // what H10 froze, and what a window built now would say.
            Assert.True(TraceRecordingHealth.Snapshot().SinkState == TraceSinkState.Recording);
            string atTheWorker = notice.Explanation;
            Assert.Contains("has already started recording again, so the next thing that happens is being kept too",
                            atTheWorker, StringComparison.Ordinal);

            // The queued Settings action runs first: exactly what
            // ApplyDiagnosticLogSettings does for "off".
            TraceSessionHandle successor = TraceCoordinator.CurrentHandle;
            Assert.NotNull(successor);
            TraceTransitionResult off = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = successor,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit,
                OutcomeDetail = "User turned diagnostic log off",
                Resume = TraceResumeIntent.None,
            });
            Assert.Equal(TraceTransition.Accepted, off.Status);
            Assert.False(TraceCoordinator.Observe().Recording);
            Assert.NotEqual(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);

            // The window's moment: the same notice, read as the constructor
            // reads it.
            string atTheWindow = notice.Explanation;
            _out.WriteLine("--- at the window, after the log was turned off in the queue:");
            _out.WriteLine(atTheWindow);
            Assert.NotEqual(atTheWorker, atTheWindow);
            Assert.False(notice.RecordingNow);
            Assert.True(notice.SuccessorOpened, "the archive's own fact is kept: a successor did open");
            Assert.DoesNotContain("is being kept too", atTheWindow, StringComparison.Ordinal);
            Assert.DoesNotContain("has not started recording again", atTheWindow, StringComparison.Ordinal);
            Assert.Contains("did start recording again after the connection went, but it is not recording now", atTheWindow, StringComparison.Ordinal);
            Assert.Contains("what happens next is not being kept", atTheWindow, StringComparison.Ordinal);
            Assert.Contains("Control J then Control R", atTheWindow, StringComparison.Ordinal);
        }

        /// <summary>
        /// The other way: the successor's file fails after the worker
        /// composed the notice, and the fault retire runs before the
        /// window. Same answer at the window.
        /// </summary>
        [Fact]
        public void A_successor_that_fails_after_the_worker_composed_the_notice_is_not_called_a_running_log_at_the_window()
        {
            CaptureArchiveNotice notice = OneDrop(duringTheWait: () => { });
            Assert.Contains("is being kept too", notice.Explanation, StringComparison.Ordinal);   // the worker's moment

            BreakTheLiveSink();
            Tracing.TraceLine("the write that fails", TraceLevel.Warning);
            Assert.True(SpinWait.SpinUntil(() => TraceCoordinator.CurrentHandle == null, TimeSpan.FromSeconds(10)),
                        "the faulted successor was never retired");
            Assert.Equal(TraceSinkState.Failed, TraceRecordingHealth.Snapshot().SinkState);

            string atTheWindow = notice.Explanation;
            _out.WriteLine("--- at the window, after the successor failed in the queue:");
            _out.WriteLine(atTheWindow);
            Assert.False(notice.RecordingNow);
            Assert.True(notice.SuccessorOpened);
            Assert.DoesNotContain("is being kept too", atTheWindow, StringComparison.Ordinal);
            Assert.Contains("but it is not recording now", atTheWindow, StringComparison.Ordinal);
        }

        // ── Sol's review of H11, the blocker: the fault-retire interval ────
        //
        // The test above waits for the retirement, so it reads the LATER
        // state. A write fault closes the sink inside the failing write, and
        // the health model hears of it only when the queued retirement runs.
        // Holding that work item leaves the world exactly in the interval a
        // window can render in: the file closed, the session still current,
        // the model still saying Recording.

        /// <summary>
        /// The successor's file fails and its retirement is still queued when
        /// the window installs its text. The window must not promise that the
        /// next thing is being kept: the sink has closed, whatever the health
        /// model has yet to be told.
        /// </summary>
        [Fact]
        public void A_successor_whose_file_has_closed_is_not_called_recording_while_its_retirement_is_still_queued()
        {
            CaptureArchiveNotice notice = OneDrop(duringTheWait: () => { });
            Assert.Contains("is being kept too", notice.Explanation, StringComparison.Ordinal);   // the worker's moment
            RotatingTraceListener successorSink = Tracing.LiveListener;
            Assert.NotNull(successorSink);

            string atTheWindow;
            using (new HeldFaultRetire())
            {
                BreakTheLiveSink();
                Tracing.TraceLine("the write that fails", TraceLevel.Warning);

                // The interval, pinned. Each of these is a precondition: if
                // any fails, the read below would not be testing the gap.
                Assert.True(successorSink.IsClosed, "the failing write did not close the successor's sink");
                Assert.NotNull(successorSink.WriteFault);
                Assert.NotNull(TraceCoordinator.CurrentHandle);   // not retired yet: the work item is held
                Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);   // not told yet
                Assert.False(TraceCoordinator.Observe().Recording);   // the gated view already knows

                atTheWindow = notice.Explanation;
                _out.WriteLine("--- at the window, sink closed, retirement still queued:");
                _out.WriteLine(atTheWindow);
                Assert.False(notice.RecordingNow);
                Assert.False(CaptureArchiveNotice.LiveRecordingState());
                Assert.True(notice.SuccessorOpened);
                Assert.DoesNotContain("is being kept too", atTheWindow, StringComparison.Ordinal);
                Assert.Contains("but it is not recording now", atTheWindow, StringComparison.Ordinal);
            }

            // The held retirement has now run: the model agrees, and the
            // sentence does not change back.
            Assert.Null(TraceCoordinator.CurrentHandle);
            Assert.Equal(TraceSinkState.Failed, TraceRecordingHealth.Snapshot().SinkState);
            Assert.False(notice.RecordingNow);
            Assert.Equal(atTheWindow, notice.Explanation);
        }

        /// <summary>
        /// The reader is asked on the UI thread, so it must answer while a
        /// transition holds the trace gate (H7's rule: no thread waits on a
        /// transition). An archive is held at its probe, under the gate; the
        /// reader returns at once, and a gated read, as the control, does not.
        /// </summary>
        [Fact]
        public void The_window_reader_answers_while_a_transition_holds_the_gate()
        {
            TraceCoordinator.SetStandingIntent(true, TraceLevel.Verbose);
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);
            Assert.True(CaptureArchiveNotice.LiveRecordingState(), "a healthy session is not read as recording");   // positive control

            using var inside = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            Thread archive = null, gated = null, reader = null;
            try
            {
                TraceCoordinator.TransitionProbeForTests = point =>
                {
                    if (point != "archive:owned") return;
                    inside.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                };
                archive = new Thread(() => TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = began.Successor,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.None,
                })) { IsBackground = true };
                archive.Start();
                Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the archive never reached its probe");

                // Control: the gate really is held.
                gated = new Thread(() => { _ = TraceCoordinator.Recording; }) { IsBackground = true };
                gated.Start();
                Assert.False(gated.Join(TimeSpan.FromMilliseconds(300)),
                    "a gated read got past the held archive — the gate is not held, and the check below would be vacuous");

                bool answer = false;
                reader = new Thread(() => { answer = CaptureArchiveNotice.LiveRecordingState(); }) { IsBackground = true };
                reader.Start();
                Assert.True(reader.Join(TimeSpan.FromSeconds(2)), "the window's reader waited on the trace gate");
                Assert.True(archive.IsAlive, "the archive finished before the reader was measured; nothing was measured");
                _out.WriteLine("reader answered " + answer + " while the archive held the gate");
            }
            finally
            {
                release.Set();
                TraceCoordinator.TransitionProbeForTests = null;
                archive?.Join(TimeSpan.FromSeconds(10));
                gated?.Join(TimeSpan.FromSeconds(10));
                reader?.Join(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>
        /// Positive control for the two above: nothing lands in the queue,
        /// and the text at the window is the text at the worker — so the
        /// change of sentence above is caused by the change of state and
        /// not by reading twice.
        /// </summary>
        [Fact]
        public void With_nothing_in_the_queue_the_window_reads_what_the_worker_would_have_said()
        {
            CaptureArchiveNotice notice = OneDrop(duringTheWait: () => { });
            string atTheWorker = notice.Explanation;
            Assert.Equal(TraceSinkState.Recording, TraceRecordingHealth.Snapshot().SinkState);
            string atTheWindow = notice.Explanation;
            Assert.Equal(atTheWorker, atTheWindow);
            Assert.True(notice.RecordingNow);
            Assert.Contains("so the next thing that happens is being kept too", atTheWindow, StringComparison.Ordinal);
        }
    }
}
