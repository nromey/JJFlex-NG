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

        /// <summary>A fresh supply-voltage reading, for the coalescer tests
        /// below, which are about windows rather than about volts.</summary>
        private static SupplyVoltage Volts(float v) =>
            SupplyVoltage.Reading(v, TimeSpan.Zero);

        // ────────────────────────────────────────────────────────────────
        //  The recorded set — ONE selection, owned by the alarm subsystem
        //  (#566, folded 2026-10-01)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The list that used to live here is gone, and the fold has one shape:
        /// the alarm service's recorded set reaches the rig through
        /// SetCaptureSelection, pushed by the host on attach and on change, and
        /// the rig records temperature only while PATEMP is selected. Pinned
        /// against the source, the way SafetyRouteShapeTests pins its adapters,
        /// because a second list quietly re-growing here is exactly the
        /// two-vocabularies collision the integration pass exists to catch.
        /// </summary>
        [Fact]
        public void There_is_no_hardcoded_recorded_list_and_the_selection_reaches_the_rig_from_the_alarm_service()
        {
            string set = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "CaptureMeterSet.cs"));
            Assert.DoesNotContain("IReadOnlyList<RecordedMeter> Recorded", set);
            Assert.DoesNotContain("struct RecordedMeter", set);
            Assert.DoesNotContain("const string SupplyVoltageMeterName", set);
            Assert.Contains("const string PreferredSupplyVoltageMeterName", set);   // a preference between selected meters, not a list

            string rig = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.CaptureMeters.cs"));
            Assert.Contains("public void SetCaptureSelection(", rig);
            Assert.Contains("if (!isSelectedForCapture(CaptureMeterSet.PaTemperatureMeterName)) return;", rig);
            Assert.Contains("if (!supplyVoltageSelected(selection)) return SupplyVoltage.NotSelected();", rig);

            string host = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "OperatorAlarmHost.cs"));
            Assert.Contains("Service.Changed += PushCaptureSelection;", host);
            Assert.Contains("rig.SetCaptureSelection(names);", host);
            Assert.Contains("service.RecordedMetersResolved", host);
        }

        [Fact]
        public void With_no_selection_pushed_the_rig_records_no_temperature_line_and_says_so_once()
        {
            // The negative control for the gate below: the same handler, the
            // same keyed rig, and no SetCaptureSelection — nothing is written,
            // and the trace carries one Warning saying why. Silent absence is
            // the #494 failure, and this is the test that it is not silent.
            var captured = new List<string>();
            var listener = new CapturingListener(captured);
            Trace.Listeners.Add(listener);
            bool wasOn = Tracing.On;
            var savedSwitch = Tracing.TheSwitch;
            bool meterStreamWas = MeterTraceStream.Enabled;
            FlexBase rig = null;
            try
            {
                MeterTraceStream.Enabled = false;
                Tracing.TheSwitch = new TraceSwitch("captureMeters", "captureMeters") { Level = TraceLevel.Info };
                Tracing.On = true;
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                typeof(FlexBase).GetField("_Transmit", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(rig, true);
                MethodInfo handler = typeof(FlexBase).GetMethod("PATempDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
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
                try { rig?.Dispose(); } catch { }
            }
            Assert.DoesNotContain(captured, l => l.Contains(CaptureMeterSet.CaptureMetersLine, StringComparison.Ordinal));
            Assert.Single(captured, l => l.Contains("no recorded-meter selection has been pushed", StringComparison.Ordinal));
        }

        /// <summary>
        /// Astra's Track IJK review, blocker 4 — introduced by the
        /// one-selection fold. <c>recordCaptureMeters</c> returned unless PATEMP
        /// was selected and was the only thing feeding the window, so keeping a
        /// supply meter ticked and unticking temperature lost every periodic
        /// line, voltage history included. Driven through the production volts
        /// handler on a radioless rig: the inventory is empty there, so the
        /// voltage field reads <c>unknown</c>; what this proves is that the
        /// supply meter's cadence writes the line at all, once a window, and
        /// that the line says temperature was NOT SELECTED rather than that the
        /// radio sent none.
        /// </summary>
        [Fact]
        public void The_production_volts_handler_writes_the_window_when_supply_voltage_is_recorded_without_temperature()
        {
            var captured = new List<string>();
            var listener = new CapturingListener(captured);
            Trace.Listeners.Add(listener);
            bool wasOn = Tracing.On;
            var savedSwitch = Tracing.TheSwitch;
            bool meterStreamWas = MeterTraceStream.Enabled;
            FlexBase rig = null;
            try
            {
                MeterTraceStream.Enabled = false;
                Tracing.TheSwitch = new TraceSwitch("captureMeters", "captureMeters") { Level = TraceLevel.Info };
                Tracing.On = true;
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PreferredSupplyVoltageMeterName });   // volts, no PATEMP
                typeof(FlexBase).GetField("_Transmit", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(rig, true);
                MethodInfo volts = typeof(FlexBase).GetMethod("VoltsDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(volts);
                volts.Invoke(rig, new object[] { 13.8f });
                System.Threading.Thread.Sleep(CaptureMeterSet.TransmitWindowMs + 120);
                volts.Invoke(rig, new object[] { 13.7f });
            }
            finally
            {
                Tracing.On = wasOn;
                Tracing.TheSwitch = savedSwitch;
                MeterTraceStream.Enabled = meterStreamWas;
                Trace.Listeners.Remove(listener);
                try { rig?.Dispose(); } catch { }
            }
            string emitted = Assert.Single(captured, l => l.Contains(CaptureMeterSet.CaptureMetersLine, StringComparison.Ordinal));
            _out.WriteLine(emitted);
            Assert.Contains(" state=tx ", emitted, StringComparison.Ordinal);
            Assert.Contains(" paTemp not-selected ", emitted, StringComparison.Ordinal);
            Assert.DoesNotContain("paTemp none", emitted, StringComparison.Ordinal);
            Assert.Contains(" volts=", emitted, StringComparison.Ordinal);
        }

        /// <summary>
        /// The negative control for the test above: with temperature selected
        /// the temperature handler owns the window, and the volts handler
        /// writes nothing — otherwise two drivers would close one window
        /// twice.
        /// </summary>
        [Fact]
        public void The_production_volts_handler_writes_nothing_while_temperature_is_selected()
        {
            var captured = new List<string>();
            var listener = new CapturingListener(captured);
            Trace.Listeners.Add(listener);
            bool wasOn = Tracing.On;
            var savedSwitch = Tracing.TheSwitch;
            bool meterStreamWas = MeterTraceStream.Enabled;
            FlexBase rig = null;
            try
            {
                MeterTraceStream.Enabled = false;
                Tracing.TheSwitch = new TraceSwitch("captureMeters", "captureMeters") { Level = TraceLevel.Info };
                Tracing.On = true;
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PaTemperatureMeterName, CaptureMeterSet.PreferredSupplyVoltageMeterName });
                typeof(FlexBase).GetField("_Transmit", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(rig, true);
                MethodInfo volts = typeof(FlexBase).GetMethod("VoltsDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                volts.Invoke(rig, new object[] { 13.8f });
                System.Threading.Thread.Sleep(CaptureMeterSet.TransmitWindowMs + 120);
                volts.Invoke(rig, new object[] { 13.7f });
            }
            finally
            {
                Tracing.On = wasOn;
                Tracing.TheSwitch = savedSwitch;
                MeterTraceStream.Enabled = meterStreamWas;
                Trace.Listeners.Remove(listener);
                try { rig?.Dispose(); } catch { }
            }
            Assert.DoesNotContain(captured, l => l.Contains(CaptureMeterSet.CaptureMetersLine, StringComparison.Ordinal));
        }

        [Fact]
        public void A_window_driven_by_the_supply_meter_alone_closes_on_its_cadence_and_names_temperature_as_not_selected()
        {
            var set = new CaptureMeterSet();
            var volts = SupplyVoltage.Reading(13.61f, TimeSpan.FromMilliseconds(80));
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 1000));
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 1500));
            string line = set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 2000);
            Assert.Equal("captureMeters: state=tx paTemp not-selected degC volts=13.61", line);

            // And a flush with temperature unticked says the same thing rather
            // than claiming the radio sent none.
            set.ReportWithoutTemperature(volts, transmittingOrTuning: false, nowTick: 2100);
            string flushed = set.Flush(volts, transmittingOrTuning: false, CaptureMeterSet.PartialConnectionDropped, 2600,
                temperatureSelected: false);
            Assert.Equal("captureMeters: state=rest paTemp not-selected degC volts=13.61 partial=connection_dropped", flushed);

            // Positive control: with temperature selected an empty flush keeps
            // its original meaning — the radio sent none.
            Assert.Equal("captureMeters: state=rest paTemp none n=0 degC volts=13.61 partial=connection_dropped",
                set.Flush(volts, transmittingOrTuning: false, CaptureMeterSet.PartialConnectionDropped, 2700));
        }

        // ────────────────────────────────────────────────────────────────
        //  The selection changes hands while a window is open (Astra's Track
        //  IJK2 review, blocker 2 — introduced by the B4 fix above)
        //
        //  The voltage driver closed whatever window was open with zeros in
        //  the temperature fields and the REAL sample count, so unticking PA
        //  temperature after the temperature handler had put a 61 in the
        //  window wrote "paTemp min=0 max=0 last=0 n=1". Both B4 tests started
        //  on an empty coalescer, so neither had a window holding a sample
        //  when its driver changed. Two repairs, each with its own test: the
        //  voltage driver prints the window's own samples whatever they are,
        //  and the rig cuts the open window at a driver change so the next one
        //  starts clean under the new selection.
        // ────────────────────────────────────────────────────────────────

        /// <summary>Astra's exact case, at the coalescer: a 61 in the window, temperature unticked, the voltage driver closes.</summary>
        [Fact]
        public void Unticking_temperature_with_a_real_sample_in_the_window_writes_that_sample_never_zeros()
        {
            var set = new CaptureMeterSet();
            var volts = Volts(13.8f);
            Assert.Null(set.Report(61f, volts, transmittingOrTuning: true, nowTick: 1000));

            // The rig's cut at the untick, under the selection the window ran under.
            string cut = set.CloseIfOpen(volts, nowTick: 1500, temperatureSelected: true);
            Assert.Equal("captureMeters: state=tx paTemp min=61 max=61 last=61 n=1 degC volts=13.8 partial=recorded_set_changed", cut);

            // The voltage driver then owns a NEW window with nothing of the old one in it.
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 2000));
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 2500));
            string next = set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 3000);
            Assert.Equal("captureMeters: state=tx paTemp not-selected degC volts=13.8", next);
            Assert.DoesNotContain("min=0", cut + next, StringComparison.Ordinal);
        }

        /// <summary>
        /// Created state: the voltage driver closing a window that already
        /// holds a temperature — the cut did not happen first, because a
        /// temperature sample can land between the rig's selection swap and
        /// its cut. The samples are printed, not replaced by zeros, and
        /// "not-selected" is said only of a window holding none.
        /// </summary>
        [Fact]
        public void The_voltage_driver_closing_a_window_that_holds_a_temperature_prints_that_temperature()
        {
            var set = new CaptureMeterSet();
            var volts = Volts(13.8f);
            Assert.Null(set.Report(61f, volts, transmittingOrTuning: true, nowTick: 1000));
            Assert.Null(set.Report(63f, volts, transmittingOrTuning: true, nowTick: 1400));
            string line = set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 2000);
            Assert.Equal("captureMeters: state=tx paTemp min=61 max=63 last=63 n=2 degC volts=13.8", line);
            // And the window after it is the voltage driver's own.
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 3000));
            Assert.Equal("captureMeters: state=tx paTemp not-selected degC volts=13.8",
                set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 4000));
        }

        /// <summary>The other direction: temperature TICKED while the voltage driver's window is open.</summary>
        [Fact]
        public void Ticking_temperature_cuts_the_voltage_window_as_not_selected_and_the_next_window_counts_only_new_samples()
        {
            var set = new CaptureMeterSet();
            var volts = Volts(13.8f);
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 1000));

            string cut = set.CloseIfOpen(volts, nowTick: 1400, temperatureSelected: false);
            Assert.Equal("captureMeters: state=tx paTemp not-selected degC volts=13.8 partial=recorded_set_changed", cut);

            Assert.Null(set.Report(61f, volts, transmittingOrTuning: true, nowTick: 2000));
            Assert.Equal("captureMeters: state=tx paTemp min=61 max=62 last=62 n=2 degC volts=13.8",
                set.Report(62f, volts, transmittingOrTuning: true, nowTick: 3000));
        }

        /// <summary>An untick ten milliseconds before the window would have closed on its own.</summary>
        [Fact]
        public void An_untick_just_before_periodic_closure_writes_the_window_as_cut_and_the_voltage_driver_starts_its_own()
        {
            var set = new CaptureMeterSet();
            var volts = Volts(13.8f);
            Assert.Null(set.Report(61f, volts, transmittingOrTuning: true, nowTick: 1000));
            Assert.Null(set.Report(63f, volts, transmittingOrTuning: true, nowTick: 1500));
            Assert.Equal("captureMeters: state=tx paTemp min=61 max=63 last=63 n=2 degC volts=13.8 partial=recorded_set_changed",
                set.CloseIfOpen(volts, nowTick: 1990, temperatureSelected: true));
            // The voltage driver's first arrival opens a new window; it closes a second later, not ten ms later.
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 2000));
            Assert.Null(set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 2990));
            Assert.Equal("captureMeters: state=tx paTemp not-selected degC volts=13.8",
                set.ReportWithoutTemperature(volts, transmittingOrTuning: true, nowTick: 3000));
        }

        [Fact]
        public void A_cut_with_no_window_open_writes_nothing_and_a_stale_window_is_abandoned_not_reported()
        {
            var set = new CaptureMeterSet();
            var volts = Volts(13.8f);
            Assert.Null(set.CloseIfOpen(volts, nowTick: 1000, temperatureSelected: true));

            Assert.Null(set.Report(61f, volts, transmittingOrTuning: false, nowTick: 1000));
            Assert.Null(set.CloseIfOpen(volts, nowTick: 1000 + CaptureMeterSet.StaleWindowMs + 1, temperatureSelected: true));
            // Abandoned: the next samples open a fresh window and the 61 is not in it.
            int t = 1000 + CaptureMeterSet.StaleWindowMs + 1000;
            Assert.Null(set.Report(70f, volts, transmittingOrTuning: true, nowTick: t));
            Assert.Equal("captureMeters: state=tx paTemp min=70 max=70 last=70 n=2 degC volts=13.8",
                set.Report(70f, volts, transmittingOrTuning: true, nowTick: t + 1000));
        }

        /// <summary>
        /// The production path: the real handlers on a radioless rig, the real
        /// SetCaptureSelection in between. The inventory is empty there, so
        /// volts reads unknown; what this proves is the cut line, its marker
        /// and its samples, and that the voltage driver's first window after
        /// the change carries nothing of the old one.
        /// </summary>
        [Fact]
        public void The_rig_cuts_the_open_window_when_temperature_is_unticked_and_the_voltage_driver_starts_clean()
        {
            List<string> lines = OnATracedRig(rig =>
            {
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PaTemperatureMeterName, CaptureMeterSet.PreferredSupplyVoltageMeterName });
                MethodInfo temp = typeof(FlexBase).GetMethod("PATempDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo volts = typeof(FlexBase).GetMethod("VoltsDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                temp.Invoke(rig, new object[] { 61f });                       // a real temperature in the window
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PreferredSupplyVoltageMeterName });   // untick PATEMP
                volts.Invoke(rig, new object[] { 13.8f });
                System.Threading.Thread.Sleep(CaptureMeterSet.TransmitWindowMs + 120);
                volts.Invoke(rig, new object[] { 13.7f });
            });
            foreach (string l in lines) _out.WriteLine(l);
            Assert.Equal(2, lines.Count);
            Assert.Contains(" paTemp min=61 max=61 last=61 n=1 ", lines[0], StringComparison.Ordinal);
            Assert.EndsWith(" partial=recorded_set_changed", lines[0], StringComparison.Ordinal);
            Assert.Contains(" paTemp not-selected ", lines[1], StringComparison.Ordinal);
            Assert.DoesNotContain("partial=", lines[1], StringComparison.Ordinal);
            Assert.DoesNotContain(lines, l => l.Contains("min=0", StringComparison.Ordinal));
        }

        [Fact]
        public void The_rig_cuts_the_open_voltage_window_when_temperature_is_ticked_and_the_temperature_window_counts_only_its_own()
        {
            List<string> lines = OnATracedRig(rig =>
            {
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PreferredSupplyVoltageMeterName });
                MethodInfo temp = typeof(FlexBase).GetMethod("PATempDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo volts = typeof(FlexBase).GetMethod("VoltsDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                volts.Invoke(rig, new object[] { 13.8f });                    // the voltage driver opens a window
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PaTemperatureMeterName, CaptureMeterSet.PreferredSupplyVoltageMeterName });
                temp.Invoke(rig, new object[] { 61f });
                System.Threading.Thread.Sleep(CaptureMeterSet.TransmitWindowMs + 120);
                temp.Invoke(rig, new object[] { 62f });
            });
            foreach (string l in lines) _out.WriteLine(l);
            Assert.Equal(2, lines.Count);
            Assert.Contains(" paTemp not-selected ", lines[0], StringComparison.Ordinal);
            Assert.EndsWith(" partial=recorded_set_changed", lines[0], StringComparison.Ordinal);
            Assert.Contains(" paTemp min=61 max=62 last=62 n=2 ", lines[1], StringComparison.Ordinal);
            Assert.DoesNotContain("partial=", lines[1], StringComparison.Ordinal);
        }

        /// <summary>A change that keeps the same driver cuts nothing; the positive control is the untick that follows.</summary>
        [Fact]
        public void A_selection_change_that_keeps_the_same_driver_cuts_no_window()
        {
            List<string> lines = OnATracedRig(rig =>
            {
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PaTemperatureMeterName });
                MethodInfo temp = typeof(FlexBase).GetMethod("PATempDataHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                temp.Invoke(rig, new object[] { 61f });
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PaTemperatureMeterName, CaptureMeterSet.PreferredSupplyVoltageMeterName });   // same driver
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PaTemperatureMeterName, "FWDPWR" });                                           // same driver
                Assert.Empty(TracedCaptureLines());
                rig.SetCaptureSelection(new[] { "FWDPWR" });                                                                                   // no driver at all: cut
            });
            var only = Assert.Single(lines);
            Assert.Contains(" paTemp min=61 max=61 last=61 n=1 ", only, StringComparison.Ordinal);
            Assert.EndsWith(" partial=recorded_set_changed", only, StringComparison.Ordinal);
        }

        private List<string> _tracedForRig;
        private List<string> TracedCaptureLines()
        {
            lock (_tracedForRig)
                return _tracedForRig.Where(l => l.Contains(CaptureMeterSet.CaptureMetersLine, StringComparison.Ordinal)).ToList();
        }

        /// <summary>A keyed radioless rig with tracing captured, torn down afterwards; returns the captureMeters lines in order.</summary>
        private List<string> OnATracedRig(Action<FlexBase> body)
        {
            _tracedForRig = new List<string>();
            var listener = new CapturingListener(_tracedForRig);
            Trace.Listeners.Add(listener);
            bool wasOn = Tracing.On;
            var savedSwitch = Tracing.TheSwitch;
            bool meterStreamWas = MeterTraceStream.Enabled;
            FlexBase rig = null;
            try
            {
                MeterTraceStream.Enabled = false;
                Tracing.TheSwitch = new TraceSwitch("captureMeters", "captureMeters") { Level = TraceLevel.Info };
                Tracing.On = true;
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                typeof(FlexBase).GetField("_Transmit", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(rig, true);
                body(rig);
            }
            finally
            {
                Tracing.On = wasOn;
                Tracing.TheSwitch = savedSwitch;
                MeterTraceStream.Enabled = meterStreamWas;
                Trace.Listeners.Remove(listener);
                try { rig?.Dispose(); } catch { }
            }
            return TracedCaptureLines();
        }

        [Fact]
        public void A_supply_meter_the_operator_did_not_select_is_written_as_a_choice_not_an_absence()
        {
            Assert.Equal("volts=not-selected", CaptureMeterSet.FormatVolts(SupplyVoltage.NotSelected(), CaptureMeterSet.TransmitWindowMs));
            Assert.Equal(SupplyVoltageState.NotSelected, SupplyVoltage.NotSelected().State);
        }

        // ────────────────────────────────────────────────────────────────
        //  The line itself
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_transmit_window_reads_as_a_transmit_window()
        {
            string line = CaptureMeterSet.Format(
                min: 44.5f, max: 71.25f, last: 70f, count: 17,
                volts: SupplyVoltage.Reading(13.8f, TimeSpan.Zero), transmitting: true);
            _out.WriteLine(line);
            Assert.Equal(
                "captureMeters: state=tx paTemp min=44.5 max=71.25 last=70 n=17 degC volts=13.8",
                line);
        }

        // ────────────────────────────────────────────────────────────────
        //  The voltage field's four states (#597)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The defect this pins: every value at or below zero used to render as
        /// <c>none</c>, so "no meter", "no sample yet", "a stale reading" and "a
        /// genuine zero" were one word. A zero on a transmitting radio is a
        /// finding; "we never got one" is not; and the two must never read the
        /// same. Presence now comes from the meter inventory, not from the
        /// value.
        /// </summary>
        [Fact]
        public void The_four_voltage_states_read_differently()
        {
            int w = CaptureMeterSet.TransmitWindowMs;

            // A radio that has not published its meter list yet. Not the same
            // claim as "no meter" — an absence of evidence, said as one.
            Assert.Equal("volts=unknown", CaptureMeterSet.FormatVolts(SupplyVoltage.Unknown(), w));

            // The radio listed its meters and +13.8A was not among them.
            Assert.Equal("volts=no-meter", CaptureMeterSet.FormatVolts(SupplyVoltage.NoMeter(), w));

            // It is there and has never said anything.
            Assert.Equal("volts=no-sample", CaptureMeterSet.FormatVolts(SupplyVoltage.NoSample(), w));

            // And the one the old format could not express at all.
            Assert.Equal("volts=0",
                CaptureMeterSet.FormatVolts(SupplyVoltage.Reading(0f, TimeSpan.Zero), w));
            Assert.Equal("volts=13.8",
                CaptureMeterSet.FormatVolts(SupplyVoltage.Reading(13.8f, TimeSpan.Zero), w));
        }

        [Fact]
        public void A_reading_older_than_its_own_window_carries_its_age()
        {
            // Freshness from a received-sample timestamp, which is what
            // MeterInventory has stamped on every sample all along. A held
            // value and a live one are indistinguishable without it, and "the
            // voltage was fine" read off a two-minute-old sample is exactly the
            // wrong conclusion to draw about a radio that shut itself off.
            string fresh = CaptureMeterSet.FormatVolts(
                SupplyVoltage.Reading(13.8f, TimeSpan.FromMilliseconds(200)),
                CaptureMeterSet.TransmitWindowMs);
            Assert.Equal("volts=13.8", fresh);

            string stale = CaptureMeterSet.FormatVolts(
                SupplyVoltage.Reading(13.8f, TimeSpan.FromSeconds(4.2)),
                CaptureMeterSet.TransmitWindowMs);
            _out.WriteLine(stale);
            Assert.Equal("volts=13.8 voltsAge=4.2s", stale);
        }

        [Fact]
        public void A_six_thousand_three_hundred_is_expected_to_publish_the_meter()
        {
            // #566's 2026-09-22 correction, from a 6300's own capture: it
            // publishes +13.8A before the fuse and +13.8B after it. The code
            // this replaced asserted the opposite WHILE CITING #566 as its
            // authority. +13.8A is the meter the voltage field PREFERS when the
            // operator has selected more than one supply meter, so the field
            // keeps meaning what every capture before the fold meant; which
            // meters are recorded at all is the alarm subsystem's selection.
            Assert.Equal("+13.8A", CaptureMeterSet.PreferredSupplyVoltageMeterName);
        }

        // ────────────────────────────────────────────────────────────────
        //  The coalescer
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Nothing_is_written_until_the_window_closes()
        {
            var set = new CaptureMeterSet();
            Assert.Null(set.Report(40f, Volts(13.8f), transmittingOrTuning: true, nowTick: 1_000));
            Assert.Null(set.Report(41f, Volts(13.8f), transmittingOrTuning: true, nowTick: 1_500));
            Assert.Null(set.Report(42f, Volts(13.8f), transmittingOrTuning: true, nowTick: 1_999));
        }

        [Fact]
        public void A_transmit_window_closes_after_a_second_and_keeps_the_peak()
        {
            var set = new CaptureMeterSet();
            set.Report(40f, Volts(13.8f), true, 1_000);
            set.Report(71f, Volts(13.8f), true, 1_400);   // the peak, which must survive
            set.Report(60f, Volts(13.8f), true, 1_800);
            string line = set.Report(62f, Volts(13.7f), true, 2_000);

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
            set.Report(40f, Volts(13.8f), transmittingOrTuning: false, nowTick: 1_000);
            Assert.Null(set.Report(40f, Volts(13.8f), false, 1_000 + CaptureMeterSet.TransmitWindowMs));
            Assert.Null(set.Report(40f, Volts(13.8f), false, 1_000 + CaptureMeterSet.RestingWindowMs - 1));
            Assert.NotNull(set.Report(41f, Volts(13.8f), false, 1_000 + CaptureMeterSet.RestingWindowMs));
        }

        [Fact]
        public void Keying_up_mid_window_makes_it_a_transmit_window_immediately()
        {
            // Otherwise the first second of every transmission — the second a
            // thermal question cares most about — waits out the rest of a
            // thirty-second resting window and is then labelled "rest".
            var set = new CaptureMeterSet();
            set.Report(40f, Volts(13.8f), transmittingOrTuning: false, nowTick: 1_000);
            set.Report(44f, Volts(13.8f), transmittingOrTuning: true, nowTick: 1_500);
            string line = set.Report(52f, Volts(13.8f), transmittingOrTuning: true, nowTick: 2_000);

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
            set.Report(71f, Volts(13.8f), transmittingOrTuning: true, nowTick: 1_000);

            int afterTheGap = 1_000 + CaptureMeterSet.StaleWindowMs + 1;
            Assert.Null(set.Report(40f, Volts(13.8f), true, afterTheGap));

            string line = set.Report(41f, Volts(13.8f), true, afterTheGap + 1_000);
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
            set.Report(40f, Volts(13.8f), transmittingOrTuning: false, nowTick: 1_000);
            string line = set.Report(43f, Volts(13.8f), false, 1_000 + CaptureMeterSet.RestingWindowMs);
            Assert.NotNull(line);
            Assert.Contains("n=2", line);
        }

        // ────────────────────────────────────────────────────────────────
        //  The flush the archive path needs (#598)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The defect: a line was written only when a window closed on its own,
        /// and nothing closed the open one before the session was archived. The
        /// moment of death was precisely the part that went missing.
        /// </summary>
        [Fact]
        public void A_drop_part_way_through_a_window_still_records_what_the_radio_said()
        {
            var set = new CaptureMeterSet();
            set.Report(44f, Volts(13.8f), transmittingOrTuning: true, nowTick: 1_000);
            set.Report(71.5f, Volts(13.8f), transmittingOrTuning: true, nowTick: 1_300);
            // 300 ms in. Before this change, nothing at all was ever written.

            string line = set.Flush(Volts(13.8f), transmittingOrTuning: true,
                                    CaptureMeterSet.PartialConnectionDropped, nowTick: 1_300);
            _out.WriteLine(line);
            Assert.Contains("state=tx", line);
            Assert.Contains("max=71.5", line);
            Assert.Contains("n=2", line);
            Assert.Contains("partial=connection_dropped", line);
        }

        [Fact]
        public void A_drop_inside_the_first_second_records_that_the_radio_said_nothing()
        {
            // The worst case in #598: the connection goes before any sample
            // arrives. An empty flush is not silence — "the radio produced no
            // temperature in this window" is #494's question answered, and it
            // must not look the same as the application failing to write one.
            var set = new CaptureMeterSet();
            string line = set.Flush(SupplyVoltage.NoSample(), transmittingOrTuning: false,
                                    CaptureMeterSet.PartialConnectionDropped, nowTick: 1_000);
            _out.WriteLine(line);
            Assert.Contains("paTemp none n=0", line);
            Assert.Contains("volts=no-sample", line);
            Assert.Contains("partial=connection_dropped", line);
        }

        [Fact]
        public void A_flushed_window_is_not_reported_twice()
        {
            var set = new CaptureMeterSet();
            set.Report(44f, Volts(13.8f), true, 1_000);
            Assert.Contains("n=1", set.Flush(Volts(13.8f), true,
                CaptureMeterSet.PartialConnectionDropped, 1_100));

            // The samples went out with the flush; a second one must not
            // re-count them, and a window opened afterwards starts clean.
            Assert.Contains("n=0", set.Flush(Volts(13.8f), true,
                CaptureMeterSet.PartialConnectionDropped, 1_200));
            Assert.Null(set.Report(50f, Volts(13.8f), true, 1_300));
        }

        [Fact]
        public void A_stale_window_is_flushed_as_empty_rather_than_as_a_false_statistic()
        {
            // Same rule the natural close already applies: samples either side
            // of a very long gap describe different situations, and min/max
            // across them is a fiction with a plausible n on it. A machine that
            // slept and then lost the radio is exactly this shape.
            var set = new CaptureMeterSet();
            set.Report(71f, Volts(13.8f), true, 1_000);
            string line = set.Flush(Volts(13.8f), true,
                CaptureMeterSet.PartialConnectionDropped,
                1_000 + CaptureMeterSet.StaleWindowMs + 1);
            _out.WriteLine(line);
            Assert.Contains("n=0", line);
            Assert.DoesNotContain("max=71", line);
        }

        [Fact]
        public void An_ordinary_window_carries_no_partial_marker()
        {
            // The positive control for the marker: if every line said partial,
            // the word would carry no information at all.
            var set = new CaptureMeterSet();
            set.Report(40f, Volts(13.8f), true, 1_000);
            string line = set.Report(41f, Volts(13.8f), true, 1_000 + CaptureMeterSet.TransmitWindowMs);
            Assert.NotNull(line);
            Assert.DoesNotContain("partial=", line);
        }

        /// <summary>
        /// <b>This test used to pin the defect (#618).</b> It asserted that the
        /// flush happened BEFORE the archive was queued — which was H2's ordering,
        /// and the ordering Sol's review found wrong: the line was written
        /// before anything asked whether this removal was already claimed, so a
        /// repeat notice stamped a false <c>partial=connection_dropped</c>
        /// record into the fresh standing log the first archive had just started.
        /// Green, and pinning the wrong behaviour as desired.
        ///
        /// <para>The line still has to be in the file before the archive zips it.
        /// It gets there by a different route: the drop arm hands over a
        /// FUNCTION, the claim is taken first, the winning removal renders the
        /// window, and the trace boundary writes it into the accepted session's
        /// own sink as a terminal record. So the assertion is no longer about
        /// two statements' order — it is about the flush not being a statement
        /// at all.</para>
        /// </summary>
        [Fact]
        public void The_drop_path_renders_its_window_only_after_the_claim()
        {
            // Source-read because the real ordering needs a live session, a
            // radio and a drop — and because a helper with no caller is how the
            // first attempt at this flush was lost.
            //
            // Track H5 moved the archive from the RadioRemoved drop arm, which
            // FlexLib never reaches for a SmartLink-only radio, to our radio's
            // Connected property falling. The ordering this pins moved with it.
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.cs"));
            string body = CaptureArchiveTests.ArchiveMethodBody(source);

            int archive = body.IndexOf("CaptureArchive.AfterConnectionDrop", StringComparison.Ordinal);
            int collector = body.IndexOf("() => collectCaptureMeterFlush(", StringComparison.Ordinal);
            Assert.True(archive > 0, "the connection's fall no longer queues an archive");
            Assert.True(collector > archive,
                "the meter window must be handed to the archive as a function, not rendered ahead of it");

            // Nothing on this arm renders the window as a statement of its own.
            Assert.DoesNotContain("flushCaptureMeters(", body, StringComparison.Ordinal);
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

                // The operator's recorded set, as the alarm host pushes it
                // (#566): temperature is recorded only while PATEMP is in it.
                rig.SetCaptureSelection(new[] { CaptureMeterSet.PaTemperatureMeterName });

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
        /// A <c>txMeters:</c> line written before the radio has reported power
        /// carries NO power number, and one written after carries the number
        /// the radio sent (#625, H17 — Sol's H16 review, blocker 2). Driven
        /// through the real writer, <c>FlexBase.traceTxMeters</c>, and the real
        /// meter handlers on a radioless rig, keyed so the writer runs.
        /// <para>Before H17 the first line read <c>fwd=-150.0 dBm
        /// refl=0.0 dBm ... reflW=0.001</c>: the no-data initialiser and a
        /// default zero — which is one milliwatt, a real-looking reading —
        /// under an introduction that calls them readings. The same holds for
        /// every field of the line with a no-data state (SC_MIC, its peak,
        /// SWALC, the radio's SWR) and for anything worked out from a power
        /// that has not arrived.</para>
        /// <para>Then forward power arrives alone: forward becomes a number and
        /// reflected, and everything that needs both, stays
        /// <see cref="CaptureMeterSet.NoSample"/>. Then the rest arrive, and
        /// every power field is a number. Finally a new connection clears the
        /// has-it-reported state with the elections, so a value left over
        /// from the last radio is not written as this one's reading.</para>
        /// </summary>
        [Fact]
        public void A_txMeters_line_prints_no_power_until_the_radio_reports_it()
        {
            var captured = new List<string>();
            var listener = new CapturingListener(captured);
            bool wasOn = Tracing.On;
            bool meterStreamWas = MeterTraceStream.Enabled;
            TraceSwitch savedSwitch = Tracing.TheSwitch;
            Trace.Listeners.Add(listener);

            const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
            FlexBase rig = null;
            var lines = new List<string>();
            try
            {
                MeterTraceStream.Enabled = false;
                Tracing.TheSwitch = new TraceSwitch("txMeters", "txMeters") { Level = TraceLevel.Info };
                Tracing.On = true;

                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                typeof(FlexBase).GetField("_Transmit", Private)!.SetValue(rig, true);
                Assert.True(rig.Transmit);

                MethodInfo write = typeof(FlexBase).GetMethod("traceTxMeters", Private);
                MethodInfo forward = typeof(FlexBase).GetMethod("forwardPowerData", Private);
                MethodInfo reflected = typeof(FlexBase).GetMethod("reflectedPowerData", Private);
                MethodInfo swr = typeof(FlexBase).GetMethod("sWRData", Private);
                MethodInfo reset = typeof(FlexBase).GetMethod("resetMeterInventory", Private);
                FieldInfo lastWrite = typeof(FlexBase).GetField("_txMeterTraceTime", Private);
                Assert.NotNull(write); Assert.NotNull(forward); Assert.NotNull(reflected);
                Assert.NotNull(swr); Assert.NotNull(reset); Assert.NotNull(lastWrite);

                // One line, now: the writer is rate-limited, so the last-write
                // time is set a minute back rather than slept past.
                string WriteOne()
                {
                    lastWrite.SetValue(rig, Environment.TickCount64 - 60000L);
                    int before;
                    lock (captured) before = captured.Count;
                    write.Invoke(rig, null);
                    lock (captured)
                    {
                        string line = captured.Skip(before).SingleOrDefault(
                            l => l.Contains("txMeters: state=", StringComparison.Ordinal));
                        Assert.NotNull(line);
                        lines.Add(line);
                        return line;
                    }
                }

                string nothingYet = WriteOne();
                forward.Invoke(rig, new object[] { 47.0f });
                string forwardOnly = WriteOne();
                reflected.Invoke(rig, new object[] { 20.0f });
                swr.Invoke(rig, new object[] { 1.09f });
                string both = WriteOne();
                reset.Invoke(rig, null);   // what a new connection does
                string reconnected = WriteOne();

                foreach (string l in lines) _out.WriteLine(l);
                string ns = CaptureMeterSet.NoSample;

                // Before any report: no number for any value that has a
                // no-data state, and the placeholders never appear.
                foreach (string field in new[] { "SC_MIC", "SWALC", "fwd", "refl", "fwdW", "reflW", "back", "SWRraw", "SWRcalc" })
                {
                    Assert.Contains(" " + field + "=" + ns, nothingYet, StringComparison.Ordinal);
                }
                Assert.Contains("(peak " + ns + ")", nothingYet, StringComparison.Ordinal);
                Assert.DoesNotContain("-150", nothingYet, StringComparison.Ordinal);
                Assert.DoesNotContain("dBm", nothingYet, StringComparison.Ordinal);
                Assert.DoesNotContain("0.001", nothingYet, StringComparison.Ordinal);

                // Forward alone: forward is a number; reflected and everything
                // that needs both are still not.
                Assert.Contains(" fwd=" + 47.0f.ToString("F1") + " dBm", forwardOnly, StringComparison.Ordinal);
                Assert.Contains(" fwdW=" + FlexBase.DBmToWatts(47.0f).ToString("F2"), forwardOnly, StringComparison.Ordinal);
                foreach (string field in new[] { "refl", "reflW", "back", "SWRraw", "SWRcalc" })
                {
                    Assert.Contains(" " + field + "=" + ns, forwardOnly, StringComparison.Ordinal);
                }

                // Both, and the radio's SWR: every power field is a number.
                Assert.Contains(" fwd=" + 47.0f.ToString("F1") + " dBm", both, StringComparison.Ordinal);
                Assert.Contains(" refl=" + 20.0f.ToString("F1") + " dBm", both, StringComparison.Ordinal);
                Assert.Contains(" reflW=" + FlexBase.DBmToWatts(20.0f).ToString("F3"), both, StringComparison.Ordinal);
                Assert.Contains(" SWRraw=" + 1.09f.ToString("F2"), both, StringComparison.Ordinal);
                foreach (string field in new[] { "fwd", "refl", "fwdW", "reflW", "back", "SWRraw", "SWRcalc" })
                {
                    Assert.DoesNotContain(" " + field + "=" + ns, both, StringComparison.Ordinal);
                }

                // A new connection: nothing carried over from the last radio.
                foreach (string field in new[] { "fwd", "refl", "fwdW", "reflW", "back", "SWRraw", "SWRcalc" })
                {
                    Assert.Contains(" " + field + "=" + ns, reconnected, StringComparison.Ordinal);
                }
            }
            finally
            {
                Tracing.On = wasOn;
                Tracing.TheSwitch = savedSwitch;
                MeterTraceStream.Enabled = meterStreamWas;
                Trace.Listeners.Remove(listener);
                try { rig?.Dispose(); } catch { /* teardown of a radioless rig */ }
            }
        }

        /// <summary>
        /// The introduction the <c>txMeters:</c> line carries says what the
        /// writer does, not what it used to (H17): it names the no-data token
        /// the writer prints and what it means, it states the rate as the
        /// writer's limits ("at most"), and it no longer says "about once a
        /// second", which was false during a tune (every 250 ms).
        /// </summary>
        [Fact]
        public void The_txMeters_introduction_names_the_no_data_token_and_the_real_rate()
        {
            string intro = CaptureMeterSet.TxMetersRecord.Introduction;
            _out.WriteLine(intro);
            Assert.Contains("'" + CaptureMeterSet.NoSample + "'", intro, StringComparison.Ordinal);
            Assert.DoesNotContain("about once a second", intro, StringComparison.Ordinal);
            Assert.Contains("at most once a second while transmitting", intro, StringComparison.Ordinal);
            Assert.Contains("at most four times a second while tuning", intro, StringComparison.Ordinal);

            // And the writer's own limits are what the sentence says: read
            // from the source, with a control that the reader finds the method.
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.cs"));
            int at = source.IndexOf("private void traceTxMeters()", StringComparison.Ordinal);
            Assert.True(at > 0, "traceTxMeters not found");
            string body = source.Substring(at, Math.Min(12000, source.Length - at));
            Assert.Contains("int interval = tuning ? 250 : 1000;", body, StringComparison.Ordinal);
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

            // And the supply meter's handler drives the window when temperature
            // is not selected (blocker 4) — the same one-caller rule.
            int volts = source.IndexOf("private void VoltsDataHandler(float data)", StringComparison.Ordinal);
            Assert.True(volts > 0, "VoltsDataHandler not found");
            string voltsBody = source.Substring(volts, Math.Min(1200, source.Length - volts));
            Assert.Contains("recordCaptureMetersFromVolts();", voltsBody, StringComparison.Ordinal);
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
