using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Flex.Smoothlake.FlexLib;
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

            /// <summary>The radio reports its transmit state: the real edge
            /// the property handler's Mox case runs.</summary>
            public void Mox(bool on) => Call("noteRadioMox", on);

            /// <summary>What the push-to-talk controller does at its own
            /// key-down: the elections' peaks and since-key-down counts restart.
            /// Not called for a transmission keyed any other way.</summary>
            public void PttPeakReset() => Flex.ResetScMicMax();

            /// <summary>One sample from a transmit-chain meter copy, through
            /// the real election routing.</summary>
            public void Sample(Meter meter, float value) => Call("routeTxMeterSample", meter, value);

            public void Forward(float dbm) => Call("forwardPowerData", dbm);
            public void Reflected(float dbm) => Call("reflectedPowerData", dbm);
            public void Swr(float swr) => Call("sWRData", swr);

            public void Dispose()
            {
                Tracing.On = _wasOn;
                Tracing.TheSwitch = _savedSwitch;
                MeterTraceStream.Enabled = _meterStreamWas;
                Trace.Listeners.Remove(_listener);
                try { Flex.Dispose(); } catch { /* teardown of a radioless rig */ }
            }
        }

        /// <summary>A FlexLib meter copy, as the radio would publish it. The
        /// constructor needs a live radio, so the object is made bare and
        /// given the three things the election routing reads: name, index and
        /// source.</summary>
        internal static Meter NewMeter(string name, int index)
        {
            var m = (Meter)RuntimeHelpers.GetUninitializedObject(typeof(Meter));
            m.Name = name;
            m.Source = "TX-";
            FieldInfo idx = typeof(Meter).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(idx);
            idx!.SetValue(m, index);
            return m;
        }

        private static string F1(float v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        private const string NoSample = CaptureMeterSet.NoSample;

        // ── Blocker 1: every value from the current transmission or tune ─

        /// <summary>
        /// Sol's case, exactly (#625, H18 — his H17 review, blocker 1). One
        /// transmission reports SC_MIC, SWALC and both powers. The operator
        /// unkeys and keys again through push-to-talk, which resets the peak.
        /// Only SWALC has reported when the next line is driven.
        /// <para>Before H18 that line read <c>SC_MIC=-18.0 (peak no-sample)</c>
        /// and <c>fwd=47.0 dBm</c>: the FIRST transmission's mic level and
        /// power, printed as this one's, beside a peak from a different window.
        /// Now every value that has not arrived in this transmission says so,
        /// and the values that do arrive are this transmission's.</para>
        /// </summary>
        [Fact]
        public void A_second_transmission_does_not_print_the_first_ones_readings()
        {
            using var rig = new Rig();
            Meter scMic = NewMeter("SC_MIC", 24);
            Meter alc = NewMeter("ALC", 25);

            rig.PttPeakReset();
            rig.Mox(true);                       // transmission one
            rig.Sample(scMic, -18f);
            rig.Sample(alc, -20f);
            rig.Forward(47f);
            rig.Reflected(20f);
            rig.Swr(1.09f);
            string first = rig.WriteOne();

            rig.Mox(false);
            rig.PttPeakReset();
            rig.Mox(true);                       // transmission two
            rig.Sample(alc, -30f);               // SWALC drives the line; SC_MIC has not spoken
            string second = rig.WriteOne();

            rig.Sample(scMic, -25f);
            string third = rig.WriteOne();
            rig.Sample(scMic, -28f);
            string fourth = rig.WriteOne();

            foreach (string l in new[] { first, second, third, fourth }) _out.WriteLine(l);

            // Positive control: the first transmission's readings really are
            // numbers, so their absence below is not a writer that prints none.
            Assert.Contains(" SC_MIC=" + F1(-18f) + " (peak " + F1(-18f) + ")", first, StringComparison.Ordinal);
            Assert.Contains(" SWALC=" + F1(-20f), first, StringComparison.Ordinal);
            Assert.Contains(" fwd=" + F1(47f) + " dBm", first, StringComparison.Ordinal);

            // The second transmission: only SWALC has arrived.
            Assert.Contains(" SC_MIC=" + NoSample + " (peak " + NoSample + ")", second, StringComparison.Ordinal);
            Assert.Contains(" SWALC=" + F1(-30f), second, StringComparison.Ordinal);
            foreach (string field in new[] { "fwd", "refl", "fwdW", "reflW", "back", "SWRraw", "SWRcalc" })
            {
                Assert.Contains(" " + field + "=" + NoSample, second, StringComparison.Ordinal);
            }
            Assert.DoesNotContain(F1(-18f), second, StringComparison.Ordinal);
            Assert.DoesNotContain(F1(47f), second, StringComparison.Ordinal);

            // And it follows the second transmission once SC_MIC speaks.
            Assert.Contains(" SC_MIC=" + F1(-25f) + " (peak " + F1(-25f) + ")", third, StringComparison.Ordinal);
            Assert.Contains(" SC_MIC=" + F1(-28f) + " (peak " + F1(-25f) + ")", fourth, StringComparison.Ordinal);
        }

        /// <summary>
        /// A transmission keyed any way but the push-to-talk controller — a
        /// foot switch, the radio's own PTT, CW, another client — never gets
        /// the controller's peak reset. The election's "peak since key-down"
        /// then spans every transmission since the last one the controller
        /// keyed, so the line's peak is the writer's own, for this
        /// transmission.
        /// </summary>
        [Fact]
        public void A_transmission_keyed_without_push_to_talk_prints_its_own_peak()
        {
            using var rig = new Rig();
            Meter scMic = NewMeter("SC_MIC", 24);

            rig.Mox(true);
            rig.Sample(scMic, -10f);
            string loud = rig.WriteOne();
            rig.Mox(false);

            rig.Mox(true);                       // no PttPeakReset: keyed elsewhere
            rig.Sample(scMic, -30f);
            string quiet = rig.WriteOne();
            _out.WriteLine(loud);
            _out.WriteLine(quiet);

            Assert.Contains(" SC_MIC=" + F1(-10f) + " (peak " + F1(-10f) + ")", loud, StringComparison.Ordinal);
            Assert.Contains(" SC_MIC=" + F1(-30f) + " (peak " + F1(-30f) + ")", quiet, StringComparison.Ordinal);
        }

        /// <summary>
        /// The peak and the copy "via" names are the same copy. Push-to-talk
        /// keys the radio and the radio's Mox can arrive before the
        /// controller's peak reset; a copy that spoke before the reset and
        /// then lost the election must not leave its peak under the name of
        /// the copy that won.
        /// </summary>
        [Fact]
        public void The_peak_belongs_to_the_copy_via_names()
        {
            using var rig = new Rig();
            Meter first = NewMeter("SC_MIC", 24);
            Meter second = NewMeter("SC_MIC", 48);

            rig.Mox(true);                       // the radio reports Mox first
            rig.Sample(first, -10f);             // elected: the only copy to have spoken
            rig.PttPeakReset();                  // then the controller's key-down reset
            rig.Sample(first, -40f);
            rig.Sample(second, -30f);            // more signal since the reset: displaces
            string line = rig.WriteOne();
            _out.WriteLine(line);

            Assert.Contains(" SC_MIC=" + F1(-30f) + " (peak " + F1(-30f) + ") via [48]", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// A tune on an unkeyed radio opens its own window, so the forward
        /// power of the transmission before it is not printed as the tune's;
        /// and the radio keying Mox part-way through a tune (an ATU sweep does)
        /// is the same keyed stretch, so the tune's readings stay.
        /// </summary>
        [Fact]
        public void A_tune_opens_its_own_window_and_Mox_inside_it_does_not()
        {
            using var rig = new Rig();

            rig.Mox(true);
            rig.Forward(47f);
            string transmit = rig.WriteOne();
            rig.Mox(false);

            rig.Call("beginTuneCycle", "carrier");
            string tuneBeforePower = rig.WriteOne();
            rig.Forward(30f);
            string tune = rig.WriteOne();
            rig.Mox(true);                       // the sweep keys Mox
            string tuneAndTx = rig.WriteOne();
            foreach (string l in new[] { transmit, tuneBeforePower, tune, tuneAndTx }) _out.WriteLine(l);

            Assert.Contains(" fwd=" + F1(47f) + " dBm", transmit, StringComparison.Ordinal);
            Assert.Contains("state=tune ", tuneBeforePower, StringComparison.Ordinal);
            Assert.Contains(" fwd=" + NoSample, tuneBeforePower, StringComparison.Ordinal);
            Assert.Contains(" fwd=" + F1(30f) + " dBm", tune, StringComparison.Ordinal);
            Assert.Contains("state=tune+tx ", tuneAndTx, StringComparison.Ordinal);
            Assert.Contains(" fwd=" + F1(30f) + " dBm", tuneAndTx, StringComparison.Ordinal);
        }

        /// <summary>
        /// The introduction names the window, and it is the one the writer
        /// uses: the current transmission or tune, for every field, including
        /// the peak. And it no longer claims the connection is the window, or
        /// that the copy-count line says "census", which it never did.
        /// </summary>
        [Fact]
        public void The_introduction_names_the_window_the_writer_uses()
        {
            string intro = CaptureMeterSet.TxMetersRecord.Introduction;
            _out.WriteLine(intro);
            Assert.Contains("Every value on the line comes from the current transmission or tune", intro, StringComparison.Ordinal);
            Assert.Contains("when the radio reports that it is transmitting or when a tune begins", intro, StringComparison.Ordinal);
            Assert.Contains("starts again on a new connection", intro, StringComparison.Ordinal);
            Assert.Contains("highest SC_MIC reading from that copy so far in this transmission or tune", intro, StringComparison.Ordinal);
            Assert.Contains("'" + NoSample + "' is one the radio has not reported during this transmission or tune", intro, StringComparison.Ordinal);
            Assert.DoesNotContain("since this connection", intro, StringComparison.Ordinal);
            Assert.DoesNotContain("'census'", intro, StringComparison.Ordinal);
        }

        // ── Blocker 3: the connection reset comes before the hooks ───────

        /// <summary>
        /// Sol's blocker 3, as a sequence: a reading that arrives and is then
        /// followed by the per-connection reset is disowned by it, because the
        /// reset opens a new window. That is right for a reading from the LAST
        /// connection and wrong for one from THIS connection, so the reset must
        /// never follow a hook that could have delivered one; see the next
        /// test for <c>Connect</c>'s order. Reset first, and the reading that
        /// follows is kept.
        /// </summary>
        [Fact]
        public void The_reset_disowns_what_came_before_it_and_keeps_what_comes_after()
        {
            using var rig = new Rig();
            rig.ForceTransmitField(true);

            rig.Forward(47f);                    // the old order: a callback in the gap...
            rig.Call("resetMeterInventory");     // ...then the reset
            string disowned = rig.WriteOne();

            rig.Call("resetMeterInventory");     // the new order: the reset...
            rig.Forward(47f);                    // ...then the callback
            string kept = rig.WriteOne();
            _out.WriteLine(disowned);
            _out.WriteLine(kept);

            Assert.Contains(" fwd=" + NoSample, disowned, StringComparison.Ordinal);
            Assert.Contains(" fwd=" + F1(47f) + " dBm", kept, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>Connect</c> resets the meter inventory BEFORE it subscribes a
        /// single handler, so no reading this connection delivers can land in
        /// the gap the previous test shows (Sol's blocker 3). Read from the
        /// source, because <c>Connect</c> needs a radio: every handler
        /// subscription in it, meter or not, must come after the one reset, and
        /// there must be exactly one reset in it. Positive controls: the reader
        /// finds the method, and finds each of the three power subscriptions.
        /// </summary>
        [Fact]
        public void Connect_resets_the_meter_inventory_before_hooking_any_handler()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));
            int start = source.IndexOf("public bool Connect(string serial, bool lowBW", StringComparison.Ordinal);
            Assert.True(start > 0, "Connect not found");
            int end = source.IndexOf("theRadio.TxBandSettingsAdded +=", start, StringComparison.Ordinal);
            Assert.True(end > start, "the end of Connect's handler block not found");
            string body = source.Substring(start, end - start);

            foreach (string hook in new[]
            {
                "theRadio.ForwardPowerDataReady +=",
                "theRadio.ReflectedPowerDataReady +=",
                "theRadio.SWRDataReady +=",
            })
            {
                Assert.Contains(hook, body, StringComparison.Ordinal);   // control
            }

            int reset = body.IndexOf("resetMeterInventory();", StringComparison.Ordinal);
            Assert.True(reset > 0, "Connect no longer resets the meter inventory");
            Assert.Equal(reset, body.LastIndexOf("resetMeterInventory();", StringComparison.Ordinal));

            int firstHook = body.IndexOf("wireRadioPropertyHandler(theRadio);", StringComparison.Ordinal);
            int firstEvent = body.IndexOf(" += ", StringComparison.Ordinal);
            Assert.True(firstHook > 0 && firstEvent > 0);
            Assert.True(reset < firstHook, "the reset comes after the property handler is wired");
            Assert.True(reset < firstEvent, "the reset comes after a handler is subscribed");
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
