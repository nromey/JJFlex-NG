using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using JJTrace;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The facts a sealed file carries about its own contents — what kinds of
    /// meter reading were written and flushed, and what a fault took — as the
    /// sink counts them (Sol's review of H9, blocker 2). Driven through a
    /// REAL <see cref="RotatingTraceListener"/> on a real file, with the two
    /// deterministic faults the coordinator tests already use: a disposed
    /// writer (the next write throws) and a disposed stream (writes buffer,
    /// the next flush throws).
    /// </summary>
    public sealed class TraceFileFactsTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;
        private readonly string _path;

        public TraceFileFactsTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-h10-facts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "JJFlexRadioTrace.txt");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private RotatingTraceListener Sink() =>
            new RotatingTraceListener(_path, rotationThresholdBytes: 0, resolvePartPath: null, onPartClosed: null);

        private static void DisposeField(RotatingTraceListener sink, string field)
        {
            var value = (IDisposable)typeof(RotatingTraceListener)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(sink)!;
            value.Dispose();
        }

        // Real renderings, not hand-typed strings: the temperature line is
        // what CaptureMeterSet.Format produces, and the power line's head is
        // read from the one place FlexBase writes it.
        private static string TemperatureLine(int samples) =>
            "12345 [T7] " + CaptureMeterSet.Format(41.5f, 43f, 42.25f, samples, SupplyVoltage.NoMeter(), transmitting: true);

        private static string PowerLine() =>
            "12346 [T7] txMeters: state=tx SC_MIC=-20.0 (peak -18.0) via mic SWALC=0.0 fwd=47.0 dBm refl=20.0 dBm fwdW=50.12 reflW=0.100 back=0.2% SWRraw=1.09 SWRcalc=1.09";

        // ── The classifier, pinned to the vocabulary it recognises ────────

        /// <summary>
        /// The markers live in JJTrace so the boot trace counts from its
        /// first line; this pins them to Radios' constants and to really
        /// rendered lines, so a rename there turns this red rather than
        /// silently making every file read as empty.
        /// </summary>
        [Fact]
        public void The_classifier_recognises_the_lines_Radios_actually_writes()
        {
            Assert.StartsWith(CaptureMeterSet.TxMetersLine, TraceLineKinds.PowerMarker, StringComparison.Ordinal);
            Assert.Equal(CaptureMeterSet.CaptureMetersLine, TraceLineKinds.TemperatureMarker);

            // The transmit line is written in exactly one place, with this head.
            string flexBase = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));
            Assert.Contains("Tracing.TraceLine(\"" + TraceLineKinds.PowerMarker, flexBase, StringComparison.Ordinal);

            Assert.Equal(TraceLineKinds.Power, TraceLineKinds.Classify(PowerLine()));
            Assert.Equal(TraceLineKinds.Temperature, TraceLineKinds.Classify(TemperatureLine(6)));
            // A window with no samples is not a reading — Sol's "paTemp none n=0".
            Assert.Contains("paTemp none n=0", TemperatureLine(0), StringComparison.Ordinal);
            Assert.Null(TraceLineKinds.Classify(TemperatureLine(0)));
            // The census and election lines share the prefix and are not readings.
            Assert.Null(TraceLineKinds.Classify("1 [T1] txMeters: census 3 copies of SC_MIC"));
            // A refusal record carrying another session's reading is not this file's.
            Assert.Null(TraceLineKinds.Classify("1 [T1] TraceDeferred: REFUSED — kept here so the moment is not lost: " + PowerLine()));
            Assert.Null(TraceLineKinds.Classify("1 [T1] propertyChanged:Radio:Connected:False"));
            Assert.Null(TraceLineKinds.Classify(null));
        }

        // ── The tally, through a real sink ────────────────────────────────

        [Fact]
        public void A_file_with_no_reading_in_it_says_so_and_a_file_with_both_says_both()
        {
            using (RotatingTraceListener sink = Sink())
            {
                sink.WriteLine("1 [T1] an ordinary line");
                sink.WriteLine(TemperatureLine(0));
                sink.Flush();
                TraceFileFacts none = sink.Facts;
                _out.WriteLine(none.ToString());
                Assert.False(none.PowerReadingsWritten);
                Assert.False(none.TemperatureReadingsWritten);
                Assert.False(none.AnyReadingsWritten);
                Assert.False(none.Faulted);

                sink.WriteLine(PowerLine());
                // Handed over but not yet flushed: not yet a fact.
                Assert.False(sink.Facts.PowerReadingsWritten);
                sink.Flush();
                Assert.True(sink.Facts.PowerReadingsWritten);
                Assert.False(sink.Facts.TemperatureReadingsWritten);

                // A terminal line flushes itself.
                Assert.True(sink.WriteTerminalLine(TemperatureLine(3)));
                TraceFileFacts both = sink.Facts;
                _out.WriteLine(both.ToString());
                Assert.True(both.PowerReadingsWritten);
                Assert.True(both.TemperatureReadingsWritten);
                Assert.True(sink.FlushAndClose(out _));
                Assert.False(sink.Facts.Faulted);
            }
        }

        /// <summary>
        /// A write that throws with an empty buffer takes only itself. The
        /// failing line is a reading here, so a reading was lost AT the
        /// fault; the line after it, refused by the closed sink, is a
        /// reading refused AFTER it.
        /// </summary>
        [Fact]
        public void A_failed_write_with_an_empty_buffer_loses_only_the_failing_line()
        {
            using (RotatingTraceListener sink = Sink())
            {
                sink.WriteLine(TemperatureLine(4));
                sink.Flush();
                DisposeField(sink, "_writer");
                sink.WriteLine(PowerLine());                 // throws inside; the sink closes
                Assert.True(sink.IsClosed);
                TraceFileFacts f = sink.Facts;
                _out.WriteLine(f.ToString());
                Assert.True(f.Faulted);
                Assert.Equal(0, f.LinesUnflushedAtFault);
                Assert.True(f.ReadingsLostAtFault);
                Assert.False(f.ReadingsRefusedAfterFault);
                Assert.True(f.TemperatureReadingsWritten);
                Assert.False(f.PowerReadingsWritten);

                sink.WriteLine("1 [T1] refused, not a reading");
                Assert.False(sink.Facts.ReadingsRefusedAfterFault);
                Assert.False(sink.WriteTerminalLine(TemperatureLine(2)));
                Assert.True(sink.Facts.ReadingsRefusedAfterFault);
                Assert.True(sink.Facts.ReadingsMissingSinceFault);
            }
        }

        /// <summary>
        /// The counterexample Sol named for "everything before that point is
        /// in the file": writes that buffered and a flush that then failed.
        /// Three lines went in after the last good flush; the fault took all
        /// three, and the facts say three, and that a reading was among them.
        /// </summary>
        [Fact]
        public void A_failed_flush_reports_how_many_buffered_lines_it_took()
        {
            using (RotatingTraceListener sink = Sink())
            {
                sink.WriteLine("1 [T1] before");
                sink.Flush();
                DisposeField(sink, "_stream");               // the writer still buffers; the stream is gone
                sink.WriteLine("2 [T1] buffered one");
                sink.WriteLine(PowerLine());
                sink.WriteLine("3 [T1] buffered three");
                Assert.False(sink.IsClosed);
                sink.Flush();                                // throws inside; the sink closes
                Assert.True(sink.IsClosed);
                TraceFileFacts f = sink.Facts;
                _out.WriteLine(f.ToString());
                Assert.True(f.Faulted);
                Assert.Equal(3, f.LinesUnflushedAtFault);
                Assert.True(f.ReadingsLostAtFault);
                Assert.False(f.PowerReadingsWritten, "a reading that never flushed is not in the file");
            }
        }

        /// <summary>
        /// The other way the buffer can fail: a terminal line whose own
        /// flush throws. It had entered the buffer, so it is the failing
        /// line and is not counted as "before the point" — two lines before
        /// it are.
        /// </summary>
        [Fact]
        public void A_terminal_line_whose_flush_fails_is_the_failing_line_not_one_of_those_before_it()
        {
            using (RotatingTraceListener sink = Sink())
            {
                sink.WriteLine("1 [T1] flushed");
                sink.Flush();
                DisposeField(sink, "_stream");
                sink.WriteLine("2 [T1] buffered one");
                sink.WriteLine("3 [T1] buffered two");
                Assert.False(sink.WriteTerminalLine("4 [T1] the terminal line"));
                TraceFileFacts f = sink.Facts;
                _out.WriteLine(f.ToString());
                Assert.Equal(2, f.LinesUnflushedAtFault);
                Assert.False(f.ReadingsLostAtFault);
                // Positive control for the same path with an empty buffer.
                using (RotatingTraceListener clean = new RotatingTraceListener(Path.Combine(_dir, "b.txt"), 0, null, null))
                {
                    clean.WriteLine("1 [T1] flushed");
                    clean.Flush();
                    DisposeField(clean, "_stream");
                    Assert.False(clean.WriteTerminalLine("2 [T1] terminal"));
                    Assert.Equal(0, clean.Facts.LinesUnflushedAtFault);
                }
            }
        }

        /// <summary>
        /// The seal's result carries the facts: a real session, a real
        /// reading through <c>Tracing.TraceLine</c>, a real seal.
        /// </summary>
        [Fact]
        public void The_seal_result_carries_what_the_file_holds()
        {
            string savedRoot = TraceCoordinator.ArchiveRootDir;
            TraceSession savedSession = TraceSessionContext.Current;
            bool savedOn = Tracing.On;
            TraceLevel savedLevel = Tracing.TheSwitch.Level;
            try
            {
                typeof(TraceCoordinator).GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { null });
                TraceCoordinator.ArchiveRootDir = Path.Combine(_dir, "Traces");
                Tracing.TheSwitch.Level = TraceLevel.Verbose;
                TraceTransitionResult began = TraceCoordinator.Begin(_path, TraceLevel.Verbose, asDetailedCapture: false);
                Assert.Equal(TraceTransition.Accepted, began.Status);
                Tracing.TraceLine(CaptureMeterSet.Format(40f, 41f, 40.5f, 5, SupplyVoltage.NoMeter(), transmitting: false), TraceLevel.Info);
                Tracing.TraceLine("a plain line", TraceLevel.Info);

                TraceTransitionResult sealed_ = TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    Expected = began.Successor,
                    OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit,
                    Resume = TraceResumeIntent.None,
                    // The drop's partial window, as a terminal record: empty here.
                    TerminalLines = new[] { CaptureMeterSet.Format(0f, 0f, 0f, 0, SupplyVoltage.NoMeter(), true, "connection_dropped") },
                });
                Assert.Equal(TraceTransition.Accepted, sealed_.Status);
                Assert.NotNull(sealed_.FileFacts);
                _out.WriteLine(sealed_.Explanation);
                Assert.True(sealed_.FileFacts.TemperatureReadingsWritten);
                Assert.False(sealed_.FileFacts.PowerReadingsWritten);
                Assert.False(sealed_.FileFacts.Faulted);
                Assert.False(sealed_.TailUncertain);
                Assert.Contains("temperature=yes", sealed_.Explanation, StringComparison.Ordinal);
                TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));

                // A refusal carries none: it sealed nothing.
                TraceTransitionResult refused = TraceCoordinator.TrySeal(new TraceSealRequest
                {
                    Expected = began.Successor, OperationId = Guid.NewGuid(),
                    Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.None,
                });
                Assert.False(refused.Owned);
                Assert.Null(refused.FileFacts);
            }
            finally
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
                TraceCoordinator.ArchiveRootDir = savedRoot;
                typeof(TraceCoordinator).GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { savedSession });
                Tracing.TheSwitch.Level = savedLevel;
                Tracing.On = savedOn;
            }
        }
    }
}
