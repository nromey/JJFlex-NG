using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using JJTrace;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// PA temperature has to reach an ordinary capture, and until 2026-09-22 it
    /// did not (#566's bridge, #494's bench note).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The measurement these tests exist to hold.</b>
    /// <c>trace-20260907-080956-clean_exit.zip</c> is a real <c>Ctrl+J</c>,
    /// <c>Ctrl+D</c> capture over a real transmission — its header line says
    /// <c>reason=Ctrl+J Ctrl+D level=Verbose</c>. It carries 258
    /// <c>txMeters:</c> lines, every one with <c>reflW=</c>, and <b>zero</b>
    /// occurrences of <c>paTemp:</c> or <c>PATEMP</c>. Reflected power reached
    /// the capture; temperature did not.
    /// </para>
    /// <para>
    /// <b>And the stated reason was wrong</b>, which is why a test rather than
    /// a comment. <c>PATempDataHandler</c> blamed Verbose lines being dropped at
    /// Normal detail; the actual gate is <c>MeterTraceStream.Enabled</c>, an
    /// opt-in setting that is off by default and has nothing to do with the
    /// detail level. The last test below is the one that matters: it drives the
    /// real handler and asserts the line comes out at Info — Normal detail —
    /// with the meter stream switched off, which is every operator's default.
    /// </para>
    /// </remarks>
    public sealed class CaptureMeterSetTests
    {
        private readonly ITestOutputHelper _out;
        public CaptureMeterSetTests(ITestOutputHelper output) { _out = output; }

        // ────────────────────────────────────────────────────────────────
        //  The recorded set — one list, in one place
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_recorded_set_is_the_four_meters_the_shutdown_question_needs()
        {
            var names = CaptureMeterSet.Recorded.Select(m => m.RadioMeterName).ToArray();
            Assert.Equal(new[] { "FWDPWR", "REFPWR", "PATEMP", "+13.8A" }, names);
        }

        [Fact]
        public void Temperature_and_volts_ride_the_capture_line_power_rides_txMeters()
        {
            // The split is the whole design: forward and reflected already
            // reach an ordinary capture on the txMeters line, so nothing
            // re-emits them. If a future edit moves one, this says so.
            var byName = CaptureMeterSet.Recorded.ToDictionary(m => m.RadioMeterName);
            Assert.Equal(CaptureMeterSet.TxMetersLine, byName["FWDPWR"].CarriedBy);
            Assert.Equal(CaptureMeterSet.TxMetersLine, byName["REFPWR"].CarriedBy);
            Assert.Equal(CaptureMeterSet.CaptureMetersLine, byName["PATEMP"].CarriedBy);
            Assert.Equal(CaptureMeterSet.CaptureMetersLine, byName["+13.8A"].CarriedBy);
        }

        // ────────────────────────────────────────────────────────────────
        //  The line itself
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_transmit_window_reads_as_a_transmit_window()
        {
            string line = CaptureMeterSet.Format(
                min: 44.5f, max: 71.25f, last: 70f, count: 17, volts: 13.8f, transmitting: true);
            _out.WriteLine(line);
            Assert.Equal(
                "captureMeters: state=tx paTemp min=44.5 max=71.25 last=70 n=17 degC volts=13.8",
                line);
        }

        [Fact]
        public void A_radio_with_no_volts_meter_says_none_rather_than_zero()
        {
            // Don's 6300 publishes no +13.8A meter at all (#566, verified
            // against his own inventory). "volts=0" on a transmitting radio
            // would be a finding; "we never got one" is not, and the two must
            // never read the same.
            string line = CaptureMeterSet.Format(
                min: 40f, max: 40f, last: 40f, count: 1, volts: 0f, transmitting: false);
            _out.WriteLine(line);
            Assert.Contains("volts=none", line);
            Assert.Contains("state=rest", line);
        }

        [Fact]
        public void Publishing_a_supply_voltage_is_a_reading_above_zero()
        {
            Assert.False(CaptureMeterSet.RadioPublishesSupplyVoltage(0f));
            Assert.False(CaptureMeterSet.RadioPublishesSupplyVoltage(-1f));
            Assert.True(CaptureMeterSet.RadioPublishesSupplyVoltage(13.8f));
        }

        // ────────────────────────────────────────────────────────────────
        //  The coalescer
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Nothing_is_written_until_the_window_closes()
        {
            var set = new CaptureMeterSet();
            Assert.Null(set.Report(40f, 13.8f, transmittingOrTuning: true, nowTick: 1_000));
            Assert.Null(set.Report(41f, 13.8f, transmittingOrTuning: true, nowTick: 1_500));
            Assert.Null(set.Report(42f, 13.8f, transmittingOrTuning: true, nowTick: 1_999));
        }

        [Fact]
        public void A_transmit_window_closes_after_a_second_and_keeps_the_peak()
        {
            var set = new CaptureMeterSet();
            set.Report(40f, 13.8f, true, 1_000);
            set.Report(71f, 13.8f, true, 1_400);   // the peak, which must survive
            set.Report(60f, 13.8f, true, 1_800);
            string line = set.Report(62f, 13.7f, true, 2_000);

            Assert.NotNull(line);
            _out.WriteLine(line);
            Assert.Contains("min=40", line);
            Assert.Contains("max=71", line);
            Assert.Contains("last=62", line);
            Assert.Contains("n=4", line);
        }

        [Fact]
        public void A_resting_window_is_thirty_seconds_not_one()
        {
            // The cost argument: this line is unconditional, for every operator
            // and every session. A second-by-second temperature nobody asked
            // for is how MeterTraceStream's 139-lines-a-second incident began.
            var set = new CaptureMeterSet();
            set.Report(40f, 0f, transmittingOrTuning: false, nowTick: 1_000);
            Assert.Null(set.Report(40f, 0f, false, 1_000 + CaptureMeterSet.TransmitWindowMs));
            Assert.Null(set.Report(40f, 0f, false, 1_000 + CaptureMeterSet.RestingWindowMs - 1));
            Assert.NotNull(set.Report(41f, 0f, false, 1_000 + CaptureMeterSet.RestingWindowMs));
        }

        [Fact]
        public void Keying_up_mid_window_makes_it_a_transmit_window_immediately()
        {
            // Otherwise the first second of every transmission — the second a
            // thermal question cares most about — waits out the rest of a
            // thirty-second resting window and is then labelled "rest".
            var set = new CaptureMeterSet();
            set.Report(40f, 0f, transmittingOrTuning: false, nowTick: 1_000);
            set.Report(44f, 0f, transmittingOrTuning: true, nowTick: 1_500);
            string line = set.Report(52f, 0f, transmittingOrTuning: true, nowTick: 2_000);

            Assert.NotNull(line);
            _out.WriteLine(line);
            Assert.Contains("state=tx", line);
        }

        [Fact]
        public void A_window_left_open_across_a_long_gap_is_abandoned_not_reported()
        {
            // The radio went away at 71 degrees and came back cold. Reporting
            // min=40 max=71 n=2 across that gap would be a false statistic,
            // and the n would be a count of nothing.
            var set = new CaptureMeterSet();
            set.Report(71f, 13.8f, transmittingOrTuning: true, nowTick: 1_000);

            int afterTheGap = 1_000 + CaptureMeterSet.StaleWindowMs + 1;
            Assert.Null(set.Report(40f, 13.8f, true, afterTheGap));

            string line = set.Report(41f, 13.8f, true, afterTheGap + 1_000);
            Assert.NotNull(line);
            _out.WriteLine(line);
            Assert.DoesNotContain("max=71", line);
            Assert.Contains("n=2", line);
        }

        [Fact]
        public void An_ordinary_resting_window_never_trips_the_stale_rule()
        {
            // The positive control for the rule above: the bound has to be
            // comfortably longer than the longest legitimate window, or a
            // resting radio would lose every line it was supposed to write.
            Assert.True(CaptureMeterSet.StaleWindowMs > CaptureMeterSet.RestingWindowMs);

            var set = new CaptureMeterSet();
            set.Report(40f, 0f, transmittingOrTuning: false, nowTick: 1_000);
            string line = set.Report(43f, 0f, false, 1_000 + CaptureMeterSet.RestingWindowMs);
            Assert.NotNull(line);
            Assert.Contains("n=2", line);
        }

        // ────────────────────────────────────────────────────────────────
        //  Through the production handler, at Normal detail
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The test the whole track turns on: a PA temperature sample fed
        /// through <c>FlexBase.PATempDataHandler</c> itself lands in the trace
        /// at <see cref="TraceLevel.Info"/> — which is what
        /// <c>DiagnosticsConfig.TraceLevel</c> maps Normal detail to — with
        /// <c>MeterTraceStream.Enabled</c> false, as it is for every operator
        /// who never found the switch.
        /// </summary>
        [Fact]
        public void The_production_handler_puts_temperature_in_an_ordinary_capture()
        {
            var captured = new List<string>();
            var listener = new CapturingListener(captured);
            bool wasOn = Tracing.On;
            bool meterStreamWas = MeterTraceStream.Enabled;
            TraceSwitch savedSwitch = Tracing.TheSwitch;
            Trace.Listeners.Add(listener);

            FlexBase rig = null;
            try
            {
                // Normal detail, and the meter stream OFF — the default an
                // operator asked to "run a capture while you transmit" has.
                MeterTraceStream.Enabled = false;
                Tracing.TheSwitch = new TraceSwitch("captureMeters", "captureMeters")
                {
                    Level = TraceLevel.Info
                };
                Tracing.On = true;

                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

                // Keyed, so the window is a second rather than thirty. The
                // field is what the Transmit property reads; nothing else in
                // this process can set it without a radio.
                typeof(FlexBase)
                    .GetField("_Transmit", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(rig, true);
                Assert.True(rig.Transmit);

                MethodInfo handler = typeof(FlexBase).GetMethod(
                    "PATempDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(handler);

                handler.Invoke(rig, new object[] { 44.0f });
                handler.Invoke(rig, new object[] { 71.5f });
                System.Threading.Thread.Sleep(CaptureMeterSet.TransmitWindowMs + 120);
                handler.Invoke(rig, new object[] { 70.0f });
            }
            finally
            {
                Tracing.On = wasOn;
                Tracing.TheSwitch = savedSwitch;
                MeterTraceStream.Enabled = meterStreamWas;
                Trace.Listeners.Remove(listener);
                try { rig?.Dispose(); } catch { /* teardown of a radioless rig */ }
            }

            foreach (string line in captured.Where(l => l.Contains("aTemp"))) _out.WriteLine(line);

            string emitted = captured.FirstOrDefault(
                l => l.Contains(CaptureMeterSet.CaptureMetersLine, StringComparison.Ordinal));
            Assert.NotNull(emitted);
            Assert.Contains("paTemp", emitted);
            Assert.Contains("max=71.5", emitted);

            // The other half of the finding, stated as an assertion: the meter
            // stream path stays silent, which is exactly why the capture on
            // disk had zero paTemp lines while carrying 258 reflW ones.
            Assert.DoesNotContain(captured, l => l.StartsWith("paTemp:", StringComparison.Ordinal));
        }

        /// <summary>
        /// The handler really calls it. Reflection above proves the helper
        /// works when driven; this proves the shipped handler is what drives
        /// it, so a future edit cannot leave a working helper with no caller —
        /// the failure mode that produced this track in the first place.
        /// </summary>
        [Fact]
        public void The_shipped_handler_is_what_calls_it()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(),
                "Radios", "FlexBase.cs"));
            // Positive control first: a reader that finds nothing must be
            // shown to find something.
            Assert.Contains("private void PATempDataHandler(float data)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ThisStringIsNotInFlexBaseAnywhere", source, StringComparison.Ordinal);

            int at = source.IndexOf("private void PATempDataHandler(float data)", StringComparison.Ordinal);
            string body = source.Substring(at, Math.Min(2400, source.Length - at));
            Assert.Contains("recordCaptureMeters(data);", body, StringComparison.Ordinal);
        }

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir.FullName;
        }

        internal sealed class CapturingListener : TraceListener
        {
            private readonly List<string> _lines;
            public CapturingListener(List<string> lines) { _lines = lines; }
            public override void Write(string message) { }
            public override void WriteLine(string message)
            {
                lock (_lines) _lines.Add(message);
            }
        }
    }
}
