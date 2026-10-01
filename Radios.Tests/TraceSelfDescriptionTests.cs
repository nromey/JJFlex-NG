using System;
using System.Collections.Generic;
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
    /// The trace file describes itself (#625, ruled by Noel 2026-09-25): every
    /// part opens with a reading guide, every kind of data record introduces
    /// itself where it first appears in a part, in its writer's own words,
    /// and the archive says why it closed the file. Driven through a real
    /// <see cref="RotatingTraceListener"/> and the real coordinator on real
    /// files, and the files are READ BACK — the guarantee is about what a
    /// person opening the file finds, not about what the code intended.
    /// </summary>
    /// <remarks>
    /// <para><b>What this replaced.</b> <c>TraceFileFactsTests</c> pinned a
    /// classifier that recognised meter lines by their text and a tally the
    /// window described the file from. Three review rounds found three ways
    /// the description could be false, all of the same shape: an observer
    /// summarising writers it had not been taught. The rule now is that the
    /// thing that wrote the data is the thing that may describe it, and these
    /// tests check that it does, exactly where it wrote it.</para>
    /// <para>Every introduction and guide line is a DRAFT for Noel; the
    /// assertions here are about PROPERTIES of the lines — that they exist,
    /// where they sit, that they name the lines they describe, that they read
    /// as sentences — so a rewording does not turn this red for the wrong
    /// reason.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs),
    // which is what makes the process-wide coordinator safe to drive here.
    public sealed class TraceSelfDescriptionTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;
        private readonly string _livePath;
        private readonly string _savedArchiveRoot;
        private readonly TraceSession _savedSession;
        private readonly bool _savedOn;
        private readonly TraceLevel _savedLevel;
        private readonly bool _savedMeterStream;

        public TraceSelfDescriptionTests(ITestOutputHelper output)
        {
            _out = output;
            _savedLevel = Tracing.TheSwitch.Level;
            _savedOn = Tracing.On;
            _savedArchiveRoot = TraceCoordinator.ArchiveRootDir;
            _savedSession = TraceSessionContext.Current;
            _savedMeterStream = MeterTraceStream.Enabled;

            _dir = Path.Combine(Path.GetTempPath(), "jjflex-h16-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _livePath = Path.Combine(_dir, "JJFlexRadioTrace.txt");

            Tracing.TheSwitch.Level = TraceLevel.Verbose;
            TraceCoordinator.ArchiveRootDir = Path.Combine(_dir, "Traces");
            TraceCoordinator.AppIdentity = new TraceEnvironment
            {
                Instance = 1, AppVersion = "0.0-test", AppPath = @"C:\test\jjflexible.exe",
            };
            RestoreSession(null);
            TraceCoordinator.ResetClaimsForTests();
            TraceCoordinator.SetStandingIntent(true, TraceLevel.Verbose);
            Tracing.On = true;
        }

        public void Dispose()
        {
            TraceCoordinator.TransitionProbeForTests = null;
            MeterTraceStream.Enabled = _savedMeterStream;
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
                .GetMethod("RestoreSessionForTests", System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { session });

        private static string ReadLive(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        private static string[] Lines(string text) =>
            text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        private string ReadArchived(TraceArchiveTicket ticket)
        {
            Assert.True(ticket.Completion.Wait(TimeSpan.FromSeconds(60)), "the archive worker never finished");
            TraceArchiveCompletion done = ticket.Completion.Result;
            Assert.True(done.ArchiveCommitted, "the archive was not committed: " + done.FailureStage);
            string extracted = SessionArchive.ExtractTraceText(done.ArchiveFullPath,
                Path.Combine(_dir, "extract-" + Guid.NewGuid().ToString("N")));
            Assert.NotNull(extracted);
            return File.ReadAllText(extracted);
        }

        private static readonly TraceRecordKind KindA = new TraceRecordKind("testA",
            "lines that begin 'alpha:' are the test's first kind of record.");
        private static readonly TraceRecordKind KindB = new TraceRecordKind("testB",
            "lines that begin 'beta:' are the test's second kind of record.");

        /// <summary>Index of the one line containing <paramref name="needle"/>, asserting there is exactly one.</summary>
        private static int Only(string[] lines, string needle, string what)
        {
            int[] at = lines.Select((l, i) => (l, i)).Where(p => p.l.Contains(needle, StringComparison.Ordinal)).Select(p => p.i).ToArray();
            Assert.True(at.Length == 1, what + ": found " + at.Length + " times, expected once");
            return at[0];
        }

        // ────────────────────────────────────────────────────────────────
        //  The sink: once per part, immediately before the first record
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A kind introduces itself once per part, on the line immediately
        /// before its first record; a second kind likewise, in order of first
        /// appearance; a kind never written is never mentioned. After a
        /// rotation the next part is a file of its own: its first line is the
        /// continuation header, then the reading guide, and the kind
        /// introduces itself again before its first record THERE — while the
        /// moved part still carries exactly one introduction.
        /// </summary>
        [Fact]
        public void A_kind_introduces_itself_once_per_part_immediately_before_its_first_record()
        {
            string partPath = null;
            using (var sink = new RotatingTraceListener(_livePath, rotationThresholdBytes: 4096,
                                                        resolvePartPath: n => partPath = Path.Combine(_dir, "part-" + n.ToString("D3") + ".txt"),
                                                        onPartClosed: null))
            {
                sink.WriteLine("1 [T1] an ordinary line");
                sink.WriteLine("2 [T1] alpha: 1", KindA);
                sink.WriteLine("3 [T1] alpha: 2", KindA);
                sink.WriteLine("4 [T1] beta: 1", KindB);
                sink.WriteLine("5 [T1] another ordinary line");
                sink.WriteLine("6 [T1] alpha: 3", KindA);
                sink.Flush();

                string[] part1 = Lines(ReadLive(_livePath));
                _out.WriteLine(string.Join(Environment.NewLine, part1));
                int introA = Only(part1, TraceSelfDescription.Introduction(KindA), "kind A introduction");
                int introB = Only(part1, TraceSelfDescription.Introduction(KindB), "kind B introduction");
                Assert.Equal(Array.FindIndex(part1, l => l.EndsWith("alpha: 1", StringComparison.Ordinal)) - 1, introA);
                Assert.Equal(Array.FindIndex(part1, l => l.EndsWith("beta: 1", StringComparison.Ordinal)) - 1, introB);
                Assert.True(introA < introB, "kinds introduce themselves in order of first appearance");
                Assert.Equal(new[] { "testA", "testB" }, sink.IntroducedKindsInCurrentPart.OrderBy(k => k));
                // Every introduction carries the ordinary trace prefix, so it
                // reads like every other line and sorts with them.
                Assert.Matches(@"^\d+ \[T\d+[^\]]*\] " + System.Text.RegularExpressions.Regex.Escape(TraceSelfDescription.Marker), part1[introA]);

                // Fill until the part rotates.
                for (int i = 0; i < 400 && sink.PartNumber < 2; i++) sink.WriteLine(i + " [T1] filler " + new string('x', 80));
                Assert.Equal(2, sink.PartNumber);
                Assert.NotNull(partPath);
                Assert.Empty(sink.IntroducedKindsInCurrentPart);

                sink.WriteLine("7 [T1] plain after the rotation");
                sink.WriteLine("8 [T1] alpha: 4", KindA);
                sink.Flush();

                string[] moved = Lines(File.ReadAllText(partPath));
                Only(moved, TraceSelfDescription.Introduction(KindA), "kind A in the moved part");

                string[] part2 = Lines(ReadLive(_livePath));
                _out.WriteLine("--- part 2");
                _out.WriteLine(string.Join(Environment.NewLine, part2));
                Assert.True(TraceLeftoverAdoption.TryParseContinuationHeader(part2[0], out _, out int from, out int thisPart),
                            "the continuation header is no longer the first line of a rotated part");
                Assert.Equal(1, from);
                Assert.Equal(2, thisPart);
                IReadOnlyList<string> guide = TraceSelfDescription.PartPreamble();
                for (int i = 0; i < guide.Count; i++) Assert.EndsWith(guide[i], part2[1 + i], StringComparison.Ordinal);
                int introA2 = Only(part2, TraceSelfDescription.Introduction(KindA), "kind A in part 2");
                Assert.Equal(Array.FindIndex(part2, l => l.EndsWith("alpha: 4", StringComparison.Ordinal)) - 1, introA2);
                Assert.DoesNotContain(part2, l => l.Contains(TraceSelfDescription.Introduction(KindB), StringComparison.Ordinal));
                Assert.Equal(new[] { "testA" }, sink.IntroducedKindsInCurrentPart);
            }
        }

        /// <summary>
        /// A terminal record that is a data record introduces its kind ahead
        /// of itself too — the drop's partial meter window is written this
        /// way — and a second terminal record of the same kind is not
        /// re-introduced. A terminal record with no kind introduces nothing.
        /// </summary>
        [Fact]
        public void A_terminal_record_with_a_kind_is_introduced_ahead_of_it()
        {
            using (var sink = new RotatingTraceListener(_livePath, 0, null, null))
            {
                Assert.True(sink.WriteTerminalLine("1 [T1] an ordinary terminal line"));
                Assert.True(sink.WriteTerminalLine("2 [T1] alpha: terminal", KindA));
                Assert.True(sink.WriteTerminalLine("3 [T1] alpha: terminal again", KindA));
                Assert.True(sink.FlushAndClose(out _));
            }
            string[] lines = Lines(File.ReadAllText(_livePath));
            _out.WriteLine(string.Join(Environment.NewLine, lines));
            Assert.Equal(4, lines.Length);
            Assert.EndsWith("an ordinary terminal line", lines[0], StringComparison.Ordinal);
            Assert.Contains(TraceSelfDescription.Introduction(KindA), lines[1], StringComparison.Ordinal);
            Assert.EndsWith("alpha: terminal", lines[2], StringComparison.Ordinal);
            Assert.EndsWith("alpha: terminal again", lines[3], StringComparison.Ordinal);
        }

        /// <summary>
        /// The listener's own coalesced frame-gap summary is a record the
        /// listener writes, so the listener introduces it: the first summary
        /// in a part is preceded by the PanFrameGaps introduction, and the raw
        /// vendor text is still quoted inside the summary.
        /// </summary>
        [Fact]
        public void The_frame_gap_summary_introduces_itself()
        {
            using (var sink = new RotatingTraceListener(_livePath, 0, null, null))
            {
                sink.WriteLine("Expected frame 10 but got frame 12");
                Thread.Sleep(1100);
                sink.WriteLine("Expected frame 12 but got frame 15");
                sink.Flush();
                string[] lines = Lines(ReadLive(_livePath));
                _out.WriteLine(string.Join(Environment.NewLine, lines));
                int summary = Only(lines, "PanFrameGaps: n=2", "the summary");
                Assert.Contains("Expected frame 12 but got frame 15", lines[summary], StringComparison.Ordinal);
                Assert.Equal(summary - 1, Only(lines, TraceSelfDescription.Introduction(RotatingTraceListener.PanFrameGapsRecord), "its introduction"));
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Through Tracing and the coordinator, on real sessions
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A record written through <see cref="Tracing.TraceRecord(TraceRecordKind, string)"/>
        /// reaches the live file with its introduction ahead of it; an
        /// ordinary <see cref="Tracing.TraceLine(string)"/> introduces
        /// nothing. And the real opt-in meter stream — the writer Sol's H10
        /// review found the old count could not see — introduces itself
        /// through its own <see cref="MeterTraceStream.Report"/>.
        /// </summary>
        [Fact]
        public void A_record_written_through_Tracing_introduces_itself_in_the_live_file()
        {
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);

            Tracing.TraceLine("plain line one", TraceLevel.Info);
            Tracing.TraceRecord(KindA, "alpha: through Tracing", TraceLevel.Info);
            Tracing.TraceRecord(KindA, "alpha: again", TraceLevel.Info);
            Tracing.TraceLine("plain line two", TraceLevel.Info);

            var stream = new MeterTraceStream();
            MeterTraceStream.Enabled = true;
            stream.Report("micData:", -120f);
            Thread.Sleep(1050);
            stream.Report("micData:", -118f);   // closes the one-second window and writes the line

            string[] lines = Lines(ReadLive(_livePath));
            _out.WriteLine(string.Join(Environment.NewLine, lines));
            int introA = Only(lines, TraceSelfDescription.Introduction(KindA), "kind A introduction");
            Assert.Equal(Array.FindIndex(lines, l => l.EndsWith("alpha: through Tracing", StringComparison.Ordinal)) - 1, introA);
            int micLine = Array.FindIndex(lines, l => l.Contains("] micData: min=-120 max=-118 last=-118 n=2", StringComparison.Ordinal));
            Assert.True(micLine > 0, "the meter stream wrote nothing");
            Assert.Equal(micLine - 1, Only(lines, TraceSelfDescription.Introduction(MeterTraceStream.Record), "the meter stream's introduction"));
            // Ordinary lines introduce nothing: the guide, plus exactly the two
            // kinds written, and no more.
            Assert.Equal(TraceSelfDescription.PartPreamble().Count + 2,
                         lines.Count(l => l.Contains("] " + TraceSelfDescription.Marker, StringComparison.Ordinal)));
        }

        /// <summary>
        /// The router defers a write that finds a transition holding the gate.
        /// An archive drains what queued up while it held the gate into the
        /// file it is closing, ahead of its terminal records (H6: those lines
        /// belong to the session that was current when they were written). A
        /// DATA record deferred that way keeps its kind and introduces itself
        /// where it lands — in the archived file, before the closing lines —
        /// and the successor carries neither the record nor its introduction.
        /// The deferral itself is asserted, so the test cannot pass by the
        /// write going straight through.
        /// </summary>
        [Fact]
        public void A_record_deferred_behind_a_transition_introduces_itself_where_it_lands()
        {
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);
            Tracing.TraceLine("before the archive", TraceLevel.Info);

            using var inside = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            TraceTransitionResult archived = null;
            Thread archive = null;
            try
            {
                TraceCoordinator.TransitionProbeForTests = point =>
                {
                    if (point != "archive:owned") return;
                    inside.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                };
                archive = new Thread(() => archived = TraceCoordinator.TryArchive(new TraceArchiveRequest
                {
                    Expected = began.Successor,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.Standing,
                })) { IsBackground = true };
                archive.Start();
                Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the archive never reached its probe");
                Assert.True(TraceCoordinator.TransitionInProgress);

                int queuedBefore = Tracing.DeferredLinesQueued;
                Tracing.TraceRecord(KindA, "alpha: deferred behind the archive", TraceLevel.Info);
                Assert.True(Tracing.DeferredLinesQueued > queuedBefore, "the record was not deferred, so nothing below tests the deferral");
            }
            finally
            {
                release.Set();
                TraceCoordinator.TransitionProbeForTests = null;
                archive?.Join(TimeSpan.FromSeconds(30));
            }
            Assert.NotNull(archived);
            Assert.Equal(TraceTransition.Accepted, archived.Status);
            Assert.True(archived.SuccessorOpened);

            string[] old = Lines(ReadArchived(archived.Ticket));
            _out.WriteLine(string.Join(Environment.NewLine, old));
            Assert.Contains(old, l => l.EndsWith("before the archive", StringComparison.Ordinal));
            int record = Array.FindIndex(old, l => l.EndsWith("alpha: deferred behind the archive", StringComparison.Ordinal));
            Assert.True(record > 0, "the deferred record never landed in the archived file");
            Assert.Equal(record - 1, Only(old, TraceSelfDescription.Introduction(KindA), "the deferred record's introduction"));
            int closing = Array.FindIndex(old, l => l.Contains(TraceSelfDescription.Marker + " this recording is closed.", StringComparison.Ordinal));
            Assert.True(closing > record, "the drained record must precede the terminal records");

            string successor = ReadLive(_livePath);
            Assert.DoesNotContain("alpha: deferred behind the archive", successor, StringComparison.Ordinal);
            Assert.DoesNotContain(TraceSelfDescription.Introduction(KindA), successor, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every fresh part opens with the machine header the coordinator
        /// verifies, then the reading guide; the guide names the phrase a
        /// reader searches for, so it can be found blind. And a session's
        /// archive writes why it closed the file, in the closer's words,
        /// before the terminal state marker — which is itself introduced.
        /// </summary>
        [Fact]
        public void A_part_opens_with_the_reading_guide_and_the_archive_says_why_it_closed()
        {
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);
            string[] live = Lines(ReadLive(_livePath));
            IReadOnlyList<string> guide = TraceSelfDescription.PartPreamble();
            Assert.Contains("--- trace session " + began.Successor.SessionId + " part 001 opened", live[0], StringComparison.Ordinal);
            for (int i = 0; i < guide.Count; i++) Assert.EndsWith(guide[i], live[1 + i], StringComparison.Ordinal);

            Tracing.TraceLine("an evening", TraceLevel.Info);
            TraceTransitionResult archived = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = began.Successor,
                OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.ConnectionDropped,
                OutcomeDetail = "The connection to 6300inshack dropped while this session was running",
                Resume = TraceResumeIntent.None,
            });
            Assert.Equal(TraceTransition.Accepted, archived.Status);
            string[] lines = Lines(ReadArchived(archived.Ticket));
            _out.WriteLine(string.Join(Environment.NewLine, lines));

            int closing = Only(lines, TraceSelfDescription.Marker + " this recording is closed.", "the closing line");
            Assert.Contains("Connection dropped", lines[closing], StringComparison.Ordinal);
            Assert.Contains("The connection to 6300inshack dropped while this session was running", lines[closing], StringComparison.Ordinal);
            int marker = Array.FindLastIndex(lines, l => l.Contains("CaptureState:", StringComparison.Ordinal));
            Assert.True(marker > closing, "the closing line must come before the terminal marker");
            Assert.Equal(marker - 1, Only(lines, TraceSelfDescription.Introduction(TraceStateMarker.Record), "the state marker's introduction"));
            Assert.Equal(lines.Length - 1, marker);   // the terminal marker is the last line
        }

        /// <summary>
        /// A bound line whose session has already been archived is written
        /// into the successor as a refusal record, and that record — which
        /// quotes another session's line inside it — introduces itself so a
        /// reader does not take the quoted line for this file's own.
        /// </summary>
        [Fact]
        public void The_refusal_record_introduces_itself()
        {
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);
            TraceSessionHandle old = began.Successor;
            TraceTransitionResult archived = TraceCoordinator.TryArchive(new TraceArchiveRequest
            {
                Expected = old, OperationId = Guid.NewGuid(),
                Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
            });
            Assert.Equal(TraceTransition.Accepted, archived.Status);
            Assert.True(archived.SuccessorOpened);

            long refusedBefore = Tracing.DeferredLinesRefused;
            Tracing.TraceLineDeferred("a line about the old session, formatted late", TraceLevel.Warning, old);
            Tracing.FlushDeferred();
            Assert.Equal(refusedBefore + 1, Tracing.DeferredLinesRefused);

            string[] lines = Lines(ReadLive(_livePath));
            _out.WriteLine(string.Join(Environment.NewLine, lines));
            // The record itself, not its introduction, which quotes the head.
            int refusal = Only(lines, "] TraceDeferred: REFUSED", "the refusal record");
            Assert.Contains("a line about the old session, formatted late", lines[refusal], StringComparison.Ordinal);
            Assert.Equal(refusal - 1, Only(lines, TraceSelfDescription.Introduction(TraceCoordinator.DeferredRefusalRecord), "its introduction"));
        }

        // ────────────────────────────────────────────────────────────────
        //  H17: the guide promises only what the sink guarantees
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Words that would make the reading guide claim more than the sink
        /// can keep. The first group claims completeness — that every kind
        /// introduces itself, or that the search lists everything the file
        /// holds. The second claims every line carries the prefix. H16's
        /// guide said both; Sol's review found both false (#625, H17).
        /// </summary>
        internal static readonly string[] OverclaimingPhrases =
        {
            "each kind", "every kind", "all kinds", "everything", "list what this file carries",
            "lists what this file", "what this file holds", "what it holds",
            "each line begins", "every line begins", "all lines", "one event per line",
        };

        /// <summary>
        /// The reading guide claims only what the mechanism guarantees. A real
        /// file is written with the three things that falsified H16's guide
        /// — a measurement line that declares no kind, a line handed straight
        /// to <c>Trace.WriteLine</c> as the radio library does, and a message
        /// that runs over two lines — and each is shown to be IN the file,
        /// unexplained or unprefixed, as the positive control. Then the guide:
        /// it must say "most" lines carry the prefix, and the line that names
        /// the search phrase must say the search finds ONLY what has explained
        /// itself; and it must use none of <see cref="OverclaimingPhrases"/>.
        /// The assertions are about claims, not wording, so a rewrite that
        /// keeps the guide honest stays green and one that restores a
        /// completeness claim goes red.
        /// </summary>
        [Fact]
        public void The_reading_guide_claims_only_what_the_sink_guarantees()
        {
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);

            Tracing.TraceRecord(KindA, "alpha: a declared record", TraceLevel.Info);
            Tracing.TraceLine("undeclaredReading: 13.8", TraceLevel.Verbose);
            Trace.WriteLine("a vendor line with no prefix of its own");
            Tracing.TraceLine("a message that runs\nover two lines", TraceLevel.Info);

            string[] lines = Lines(ReadLive(_livePath));
            _out.WriteLine(string.Join(Environment.NewLine, lines));

            // Controls: the file really holds what the guide must not deny.
            int undeclared = Only(lines, "] undeclaredReading: 13.8", "the undeclared measurement line");
            Assert.DoesNotContain(lines, l => l.Contains(TraceSelfDescription.Marker, StringComparison.Ordinal)
                                              && l.Contains("undeclaredReading", StringComparison.Ordinal));
            Assert.DoesNotContain(TraceSelfDescription.Marker, lines[undeclared - 1], StringComparison.Ordinal);
            Assert.Contains(lines, l => l == "a vendor line with no prefix of its own");
            Assert.Contains(lines, l => l == "over two lines");

            IReadOnlyList<string> guide = TraceSelfDescription.PartPreamble();
            foreach (string line in guide)
            {
                foreach (string phrase in OverclaimingPhrases)
                {
                    Assert.False(line.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                        "the reading guide claims more than the sink guarantees (\"" + phrase + "\"): " + line);
                }
            }
            Assert.Contains(guide, l => l.Contains("Most lines begin", StringComparison.Ordinal));
            string searchPhrase = TraceSelfDescription.Marker.TrimEnd(':');
            string searchLine = Assert.Single(guide, l => l.Contains("search for the words '" + searchPhrase + "'", StringComparison.Ordinal));
            Assert.Contains(" only ", searchLine, StringComparison.Ordinal);
        }

        /// <summary>
        /// The two measurement writers Sol's H16 review and its follow-up
        /// found writing bare lines — <c>VoltsDataHandler</c> (supply
        /// voltage) and <c>paEffData</c> (PA efficiency), both at Verbose, so
        /// both in every detailed capture — now introduce themselves. Driven
        /// through the REAL handlers on a radioless rig into a real live file:
        /// each kind's introduction sits on the line immediately before its
        /// first record, once, and the record's text is what it always was.
        /// </summary>
        [Fact]
        public void The_supply_voltage_and_PA_efficiency_handlers_introduce_themselves()
        {
            TraceTransitionResult began = TraceCoordinator.Begin(_livePath, TraceLevel.Verbose, asDetailedCapture: false);
            Assert.Equal(TraceTransition.Accepted, began.Status);

            FlexBase rig = null;
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                System.Reflection.MethodInfo volts = typeof(FlexBase).GetMethod("VoltsDataHandler",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                System.Reflection.MethodInfo eff = typeof(FlexBase).GetMethod("paEffData",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(volts);
                Assert.NotNull(eff);
                volts.Invoke(rig, new object[] { 13.5f });
                eff.Invoke(rig, new object[] { 42.5f });
                volts.Invoke(rig, new object[] { 13.25f });
                eff.Invoke(rig, new object[] { 40.5f });
            }
            finally
            {
                try { rig?.Dispose(); } catch { /* teardown of a radioless rig */ }
            }

            string[] lines = Lines(ReadLive(_livePath));
            _out.WriteLine(string.Join(Environment.NewLine, lines));
            foreach ((TraceRecordKind kind, string head, string first) in new[]
            {
                (FlexBase.SupplyVoltsRecord, "VoltsDataHandler:", "VoltsDataHandler:" + 13.5f.ToString()),
                (FlexBase.PaEfficiencyRecord, "paEffData:", "paEffData:" + 42.5f.ToString()),
            })
            {
                int record = Array.FindIndex(lines, l => l.EndsWith("] " + first, StringComparison.Ordinal));
                Assert.True(record > 0, head + " wrote nothing");
                Assert.Equal(record - 1, Only(lines, TraceSelfDescription.Introduction(kind), head + " introduction"));
                Assert.Equal(2, lines.Count(l => l.Contains("] " + head, StringComparison.Ordinal)));
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The words, read as a person reads them
        // ────────────────────────────────────────────────────────────────

        /// <summary>Every kind a production writer declares today. A writer
        /// added later declares its own; this list is what the prose checks
        /// below run over, not a registry the sink consults.</summary>
        private static readonly (TraceRecordKind Kind, string LineHead)[] ProductionKinds =
        {
            (CaptureMeterSet.TxMetersRecord, "txMeters:"),
            (CaptureMeterSet.CaptureMetersRecord, "captureMeters:"),
            (MeterTraceStream.Record, "micData:"),
            (TraceStateMarker.Record, "CaptureState:"),
            (RotatingTraceListener.PanFrameGapsRecord, "PanFrameGaps:"),
            (TraceCoordinator.DeferredRefusalRecord, "TraceDeferred: REFUSED"),
            (FlexBase.SupplyVoltsRecord, "VoltsDataHandler:"),
            (FlexBase.PaEfficiencyRecord, "paEffData:"),
        };

        /// <summary>
        /// Each introduction begins with the marker the guide tells the reader
        /// to search for, names the line head it describes so a reader can
        /// connect the two, ends in a full stop, carries no lexicon key, and
        /// has a key no other kind shares. The guide's own lines are
        /// sentences, and the guide names the search phrase. The closing line
        /// carries the outcome's plain label and the detail. Every assembled
        /// line is written to the output so a person can read it end to end.
        /// </summary>
        [Fact]
        public void The_descriptions_read_as_sentences_and_name_the_lines_they_describe()
        {
            foreach ((TraceRecordKind kind, string head) in ProductionKinds)
            {
                string intro = TraceSelfDescription.Introduction(kind);
                _out.WriteLine(intro);
                _out.WriteLine(string.Empty);
                Assert.StartsWith(TraceSelfDescription.Marker + " ", intro, StringComparison.Ordinal);
                Assert.EndsWith(".", intro, StringComparison.Ordinal);
                Assert.Contains("'" + head, intro, StringComparison.Ordinal);
                Assert.DoesNotContain("logging.", intro, StringComparison.Ordinal);
                Assert.False(intro.Contains("  ", StringComparison.Ordinal), "double space in: " + intro);
            }
            Assert.Equal(ProductionKinds.Length, ProductionKinds.Select(k => k.Kind.Key).Distinct(StringComparer.Ordinal).Count());

            IReadOnlyList<string> guide = TraceSelfDescription.PartPreamble();
            Assert.True(guide.Count >= 2);
            string searchPhrase = TraceSelfDescription.Marker.TrimEnd(':');
            foreach (string line in guide)
            {
                _out.WriteLine(line);
                Assert.StartsWith(TraceSelfDescription.Marker + " ", line, StringComparison.Ordinal);
                Assert.EndsWith(".", line, StringComparison.Ordinal);
            }
            Assert.Contains(guide, l => l.Contains("search for the words '" + searchPhrase + "'", StringComparison.Ordinal));

            string closing = TraceSelfDescription.Closing(TraceSessionOutcome.ConnectionDropped, "The connection to 6300inshack dropped while this session was running.");
            _out.WriteLine(closing);
            Assert.StartsWith(TraceSelfDescription.Marker + " ", closing, StringComparison.Ordinal);
            Assert.Contains("Connection dropped", closing, StringComparison.Ordinal);
            Assert.Contains("6300inshack dropped while this session was running", closing, StringComparison.Ordinal);
            Assert.EndsWith(".", closing, StringComparison.Ordinal);
            Assert.DoesNotContain("..", closing, StringComparison.Ordinal);
            Assert.EndsWith("Clean exit.", TraceSelfDescription.Closing(TraceSessionOutcome.CleanExit, null), StringComparison.Ordinal);
        }

        /// <summary>
        /// The production writers really declare their kinds — read from the
        /// source, because a writer that quietly went back to a bare
        /// <c>TraceLine</c> would compile, run, and describe nothing. Each
        /// check has a positive control (a string known to be in the file), so
        /// a reader that finds nothing is shown to find something.
        /// </summary>
        [Fact]
        public void The_writers_declare_their_kinds()
        {
            string root = CaptureMeterSetTests.RepoRoot();
            string flexBase = File.ReadAllText(Path.Combine(root, "Radios", "FlexBase.cs"));
            Assert.Contains("private void traceTxMeters(", flexBase, StringComparison.Ordinal);   // control
            Assert.Contains("Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, \"txMeters: state=\"", flexBase, StringComparison.Ordinal);
            Assert.Contains("Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, \"txMeters: \" + census", flexBase, StringComparison.Ordinal);
            Assert.Contains("Tracing.TraceRecord(CaptureMeterSet.TxMetersRecord, \"txMeters: \" + election.MeterName", flexBase, StringComparison.Ordinal);
            Assert.DoesNotContain("Tracing.TraceLine(\"txMeters:", flexBase, StringComparison.Ordinal);
            // H17: the two meter handlers that wrote measurements bare.
            Assert.Contains("private void VoltsDataHandler(float data)", flexBase, StringComparison.Ordinal);   // control
            Assert.Contains("Tracing.TraceRecord(SupplyVoltsRecord, \"VoltsDataHandler:\"", flexBase, StringComparison.Ordinal);
            Assert.DoesNotContain("Tracing.TraceLine(\"VoltsDataHandler:", flexBase, StringComparison.Ordinal);
            Assert.Contains("private void paEffData(float data)", flexBase, StringComparison.Ordinal);   // control
            Assert.Contains("Tracing.TraceRecord(PaEfficiencyRecord, \"paEffData:\"", flexBase, StringComparison.Ordinal);
            Assert.DoesNotContain("Tracing.TraceLine(\"paEffData:", flexBase, StringComparison.Ordinal);

            string meters = File.ReadAllText(Path.Combine(root, "Radios", "FlexBase.CaptureMeters.cs"));
            Assert.Contains("recordCaptureMeters(", meters, StringComparison.Ordinal);   // control
            Assert.Contains("Tracing.TraceRecord(CaptureMeterSet.CaptureMetersRecord, line, TraceLevel.Info)", meters, StringComparison.Ordinal);
            Assert.Contains("internal TraceRecord collectCaptureMeterFlush(", meters, StringComparison.Ordinal);
            Assert.Contains("CaptureMeterSet.CaptureMetersRecord);", meters, StringComparison.Ordinal);

            string stream = File.ReadAllText(Path.Combine(root, "Radios", "MeterTraceStream.cs"));
            Assert.Contains("public void Report(string channel, float value)", stream, StringComparison.Ordinal);   // control
            Assert.Contains("Tracing.TraceRecord(Record, line)", stream, StringComparison.Ordinal);
            Assert.DoesNotContain("Tracing.TraceLine(line)", stream, StringComparison.Ordinal);

            string globals = File.ReadAllText(Path.Combine(root, "globals.vb"));
            Assert.Contains("Friend Sub TraceCaptureStateMarker(", globals, StringComparison.Ordinal);   // control
            Assert.Contains("Tracing.TraceRecord(TraceStateMarker.Record, TraceStateMarker.Render(", globals, StringComparison.Ordinal);
            Assert.DoesNotContain("Tracing.TraceLine(TraceStateMarker.Render(", globals, StringComparison.Ordinal);

            string coordinator = File.ReadAllText(Path.Combine(root, "JJTrace", "TraceCoordinator.cs"));
            Assert.Contains("TraceStateMarker.RenderTerminal(AppIdentity", coordinator, StringComparison.Ordinal);   // control
            Assert.Contains("TraceStateMarker.Record))", coordinator, StringComparison.Ordinal);

            // And the classifier this replaced is gone, not idle.
            Assert.False(File.Exists(Path.Combine(root, "JJTrace", "TraceFileFacts.cs")), "TraceFileFacts.cs is back");
            Assert.DoesNotContain("TraceFileFacts", coordinator, StringComparison.Ordinal);
            Assert.DoesNotContain("TraceFileFacts", File.ReadAllText(Path.Combine(root, "Radios", "CaptureArchiveNotice.cs")), StringComparison.Ordinal);
        }
    }
}
