using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using JJTrace;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The <c>txMeters:</c> line is evidence, so every number on it must come
    /// from the window its introduction names, and the line must be written at
    /// all (#625, H18 — Sol's second merge-gate review of Track H). Every test
    /// here drives the REAL writer, <c>FlexBase.traceTxMeters</c>, and the real
    /// handlers that feed it, on a radioless rig.
    /// </summary>
    public sealed class TxMetersEvidenceTests
    {
        private readonly ITestOutputHelper _out;

        public TxMetersEvidenceTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// A radioless rig with the trace captured and the writer's clock in
        /// the test's hands. Disposing restores every global it touched.
        /// </summary>
        internal sealed class Rig : IDisposable
        {
            private readonly List<string> _captured = new();
            private readonly CaptureMeterSetTests.CapturingListener _listener;
            private readonly bool _wasOn;
            private readonly bool _meterStreamWas;
            private readonly TraceSwitch _savedSwitch;

            public FlexBase Flex { get; }

            /// <summary>What the writer's clock reads, in milliseconds.</summary>
            public long Clock;

            public Rig(long clock = 1_000_000L)
            {
                _listener = new CaptureMeterSetTests.CapturingListener(_captured);
                _wasOn = Tracing.On;
                _meterStreamWas = MeterTraceStream.Enabled;
                _savedSwitch = Tracing.TheSwitch;
                Trace.Listeners.Add(_listener);
                MeterTraceStream.Enabled = false;
                Tracing.TheSwitch = new TraceSwitch("txMeters", "txMeters") { Level = TraceLevel.Info };
                Tracing.On = true;

                Clock = clock;
                Flex = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                FieldInfo clockField = typeof(FlexBase).GetField("_txMeterTraceClock", Private);
                Assert.NotNull(clockField);
                clockField!.SetValue(Flex, (Func<long>)(() => Clock));
            }

            public object Call(string method, params object[] args)
            {
                MethodInfo m = typeof(FlexBase).GetMethod(method, Private);
                Assert.True(m != null, method + " not found on FlexBase");
                return m!.Invoke(Flex, args);
            }

            /// <summary>Key the radio without the radio: sets the transmit
            /// state the writer reads, and nothing else.</summary>
            public void ForceTransmitField(bool on) =>
                typeof(FlexBase).GetField("_Transmit", Private)!.SetValue(Flex, on);

            /// <summary>The txMeters readings written since <paramref name="mark"/>.</summary>
            public List<string> ReadingsSince(int mark)
            {
                lock (_captured)
                {
                    return _captured.Skip(mark)
                        .Where(l => l.Contains("txMeters: state=", StringComparison.Ordinal))
                        .ToList();
                }
            }

            public int Mark { get { lock (_captured) return _captured.Count; } }

            /// <summary>Call the writer once and return the reading it wrote,
            /// or null when it wrote none.</summary>
            public string WriteNow()
            {
                int mark = Mark;
                Call("traceTxMeters");
                List<string> lines = ReadingsSince(mark);
                Assert.True(lines.Count <= 1, "more than one txMeters reading from one call");
                return lines.SingleOrDefault();
            }

            /// <summary>Move the clock past every rate limit and write one
            /// reading, which must appear.</summary>
            public string WriteOne()
            {
                Clock += 60_000L;
                string line = WriteNow();
                Assert.NotNull(line);
                return line!;
            }

            public void Dispose()
            {
                Tracing.On = _wasOn;
                Tracing.TheSwitch = _savedSwitch;
                MeterTraceStream.Enabled = _meterStreamWas;
                Trace.Listeners.Remove(_listener);
                try { Flex.Dispose(); } catch { /* teardown of a radioless rig */ }
            }
        }

        // ── Blocker 4: the limiter's clock ───────────────────────────────

        /// <summary>
        /// On a machine whose 32-bit uptime clock has gone negative, the writer
        /// still writes (#625, H18 — Sol's blocker 4).
        /// <para>The limiter was <c>int now = Environment.TickCount</c> against
        /// a field starting at 0. <c>TickCount</c> is negative for about 24.9
        /// of every 49.7 days of Windows uptime; for all of that, "now - 0"
        /// was negative, always under the interval, and the field never moved,
        /// so the app wrote no <c>txMeters:</c> line at all. The clock here
        /// reads 25 days: its low 32 bits, which is what
        /// <c>Environment.TickCount</c> would have said, are negative — the
        /// premise is asserted, not assumed.</para>
        /// </summary>
        [Fact]
        public void A_machine_up_twenty_five_days_still_gets_txMeters_lines()
        {
            const long TwentyFiveDaysMs = 25L * 24 * 60 * 60 * 1000;   // 2,160,000,000
            Assert.True(unchecked((int)TwentyFiveDaysMs) < 0,
                "premise: a 32-bit TickCount reads negative at 25 days of uptime");

            using var rig = new Rig(TwentyFiveDaysMs);
            rig.ForceTransmitField(true);

            string first = rig.WriteNow();
            Assert.NotNull(first);   // the very first callback writes

            rig.Clock += 400;
            Assert.Null(rig.WriteNow());   // the limit still holds inside the second

            rig.Clock += 700;
            Assert.NotNull(rig.WriteNow());   // and lets the next second through

            // Across the 32-bit boundary itself, both ways round.
            Assert.True(FlexBase.TxMeterLineDue((long)int.MaxValue + 1000L, int.MaxValue - 100L, 1000));
            Assert.False(FlexBase.TxMeterLineDue((long)int.MaxValue + 500L, int.MaxValue - 100L, 1000));
            Assert.True(FlexBase.TxMeterLineDue(TwentyFiveDaysMs, long.MinValue, 1000));
        }
    }
}
