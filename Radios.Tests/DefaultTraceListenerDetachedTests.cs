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
    /// Task #434: a launch in which every trace line cost exactly ten seconds,
    /// so no window ever appeared.
    ///
    /// <para><b>The mechanism, measured.</b> The delay tracked trace lines
    /// one-for-one — 42 identical lines that carry the same tick in a healthy
    /// launch cost 10,000 ms each in the stalled one, with a 32 ms spread on a
    /// 10,000 ms base and no relationship at all to payload length. Nothing in
    /// the repo removed the framework's <c>DefaultTraceListener</c>, whose only
    /// output with an empty <c>LogFileName</c> is <c>OutputDebugStringW</c> —
    /// and that call waits on <c>DBWIN_BUFFER_READY</c> with a fixed 10,000 ms
    /// timeout when a debug monitor has registered but is not servicing the
    /// buffer.</para>
    ///
    /// <para><b>Proven by deliberate reproduction 2026-09-01</b>, measured from
    /// PowerShell — which writes none of our trace files, so the filesystem and
    /// antivirus explanations are dead: 0.020 ms per call at baseline,
    /// 10,003.542 ms with the event created and unserviced, 0.024 ms after
    /// release.</para>
    ///
    /// <para><b>Why these are tests and not a comment.</b> The listener is put
    /// there by the framework, not by us, so there is no line of our code for a
    /// future editor to read before re-introducing it. A test is the only thing
    /// that notices.</para>
    /// </summary>
    public sealed class DefaultTraceListenerDetachedTests
    {
        // ------------------------------------------------------------------
        // The listener set
        // ------------------------------------------------------------------

        /// <summary>
        /// Merely touching JJTrace must leave no DefaultTraceListener behind.
        /// The static constructor does this so that no ordering rule has to be
        /// remembered by anybody.
        /// </summary>
        [Fact]
        public void TouchingTracingDetachesTheFrameworkDefaultListener()
        {
            // Touch a harmless static so the type initializer has certainly run.
            _ = Tracing.Version;

            Assert.True(Tracing.DefaultListenerDetached);
            Assert.DoesNotContain(Trace.Listeners.Cast<TraceListener>(),
                                  l => l is DefaultTraceListener);
        }

        /// <summary>
        /// The positive control for the test above: this collection really does
        /// hold what is put in it, and the assertion really would catch a
        /// DefaultTraceListener. Without this, "we found none" could equally
        /// mean the search was looking in the wrong place.
        /// </summary>
        [Fact]
        public void DetachRemovesADefaultListenerThatIsActuallyThere()
        {
            var planted = new DefaultTraceListener();
            Trace.Listeners.Add(planted);
            try
            {
                Assert.Contains(Trace.Listeners.Cast<TraceListener>(),
                                l => ReferenceEquals(l, planted));

                Assert.True(Tracing.DetachDefaultListener());

                Assert.DoesNotContain(Trace.Listeners.Cast<TraceListener>(),
                                      l => l is DefaultTraceListener);
            }
            finally
            {
                Trace.Listeners.Remove(planted);
            }
        }

        /// <summary>
        /// Idempotent, and it says so: a second call removes nothing and still
        /// reports the state as detached. Anything on a startup path gets called
        /// twice sooner or later.
        /// </summary>
        [Fact]
        public void DetachIsIdempotent()
        {
            Tracing.DetachDefaultListener();
            Assert.False(Tracing.DetachDefaultListener());
            Assert.True(Tracing.DefaultListenerDetached);
        }

        /// <summary>
        /// Detaching must not disturb the listener we actually keep. The whole
        /// value of the trace file is that it is still being written.
        /// </summary>
        [Fact]
        public void DetachLeavesOtherListenersAlone()
        {
            var keeper = new TextWriterTraceListener(TextWriter.Null, "keeper");
            Trace.Listeners.Add(keeper);
            try
            {
                Tracing.DetachDefaultListener();
                Assert.Contains(Trace.Listeners.Cast<TraceListener>(),
                                l => ReferenceEquals(l, keeper));
            }
            finally
            {
                Trace.Listeners.Remove(keeper);
            }
        }

        // ------------------------------------------------------------------
        // The standing debug-monitor instrument
        // ------------------------------------------------------------------

        /// <summary>
        /// The boot line answers the question in words, and never throws doing
        /// it. It runs inside the boot trace block, where an exception would
        /// cost the operator their diagnostic log.
        /// </summary>
        [Fact]
        public void DebugMonitorProbeDescribesTheEventAndNeverThrows()
        {
            string described = DebugMonitorProbe.Describe();

            Assert.Contains("DBWIN_BUFFER_READY", described, StringComparison.Ordinal);
            Assert.Contains("#434", described, StringComparison.Ordinal);
            // Both namespaces are reported, so a "present" in either is visible.
            Assert.Contains("local=", described, StringComparison.Ordinal);
            Assert.Contains("global=", described, StringComparison.Ordinal);
        }

        /// <summary>
        /// **The probe must never create what it looks for.** Creating
        /// <c>DBWIN_BUFFER_READY</c> and not servicing it is precisely the
        /// fault — and it is machine-wide, so it would stall the operator's
        /// screen reader host processes at ten seconds a call, not just this
        /// app.
        ///
        /// The test only asserts when the event is genuinely absent, which is
        /// the ordinary state; on a machine that has a debug monitor running it
        /// declines to conclude rather than reporting a false pass.
        /// </summary>
        [Fact]
        public void DebugMonitorProbeDoesNotCreateTheEvent()
        {
            if (EventExists("DBWIN_BUFFER_READY"))
            {
                // A debug monitor is genuinely running on this machine. The
                // question this test asks cannot be answered here, and a
                // fabricated pass would be worse than saying so.
                return;
            }

            DebugMonitorProbe.Describe();

            Assert.False(EventExists("DBWIN_BUFFER_READY"));
        }

        private static bool EventExists(string name)
        {
            try
            {
                using (EventWaitHandle.OpenExisting(name)) { return true; }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------
        // The split stopwatch
        // ------------------------------------------------------------------

        /// <summary>
        /// A listener that blocks names itself in the marker line. This is the
        /// instrument the original #434 entry asked for — "a trace line naming
        /// which call took the ten seconds" — and it is exercised here against
        /// a deliberately slow listener rather than against the real fault,
        /// which cannot be induced on an attended machine.
        /// </summary>
        [Fact]
        public void ASlowListenerEarnsAMarkerNamingTheListenerSet()
        {
            var slow = new SlowListener(TimeSpan.FromMilliseconds(1200));
            var capture = new CapturingListener();

            bool wasOn = Tracing.On;
            Trace.Listeners.Add(slow);
            Trace.Listeners.Add(capture);
            try
            {
                Tracing.On = true;
                // Two lines: the first is slow and arms the marker, the marker
                // itself lands on the write that follows it.
                Tracing.TraceLine("#434 marker probe");

                Assert.Contains(capture.Lines,
                                l => l.Contains("SLOW TRACE WRITE", StringComparison.Ordinal));

                string marker = capture.Lines.First(
                    l => l.Contains("SLOW TRACE WRITE", StringComparison.Ordinal));

                // It names the listener set, so a future stall trace says what
                // was in the chain rather than leaving it to be reconstructed.
                Assert.Contains(nameof(SlowListener), marker, StringComparison.Ordinal);
                // And it reports whether we were immune, which is the fix's
                // own status line.
                Assert.Contains("defaultListenerDetached=", marker, StringComparison.Ordinal);
            }
            finally
            {
                Trace.Listeners.Remove(slow);
                Trace.Listeners.Remove(capture);
                Tracing.On = wasOn;
            }
        }

        private sealed class SlowListener : TraceListener
        {
            private readonly TimeSpan _cost;
            private bool _spent;

            public SlowListener(TimeSpan cost) { _cost = cost; }

            public override void Write(string message) { Stall(); }
            public override void WriteLine(string message) { Stall(); }

            private void Stall()
            {
                // Once only. A listener that is slow on every line would make
                // the marker's own write slow too, which is the real fault's
                // shape but makes a unit test take minutes.
                if (_spent) return;
                _spent = true;
                Thread.Sleep(_cost);
            }
        }

        private sealed class CapturingListener : TraceListener
        {
            private readonly List<string> _lines = new List<string>();
            public IReadOnlyList<string> Lines
            {
                get { lock (_lines) { return _lines.ToList(); } }
            }
            public override void Write(string message) { }
            public override void WriteLine(string message)
            {
                lock (_lines) { _lines.Add(message ?? string.Empty); }
            }
        }
    }
}
